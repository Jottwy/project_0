//! Inbound packets: the `handle_packet` dispatch, the handshake exchange and the peer-roster
//! accessors those log lines lean on. Split out of `mod.rs` verbatim, except that
//! `handle_packet` and `handle_handshake` are `pub(super)` — they were private and are still
//! called from `mod.rs` (`process_incoming`) and `tests.rs`, no longer the same module.

use std::net::SocketAddr;
use std::time::{Duration, Instant};

use log::{info, warn};

use super::peer::PeerConnection;
use super::protocol::{PacketPayload, PeerInfo, SessionConfig};
use super::reliability::is_reliable;
use super::{IncomingPacket, NetworkEvent, NetworkManager, PeerId};

impl NetworkManager {
    // ─── Packet handling ───

    pub(super) async fn handle_packet(&mut self, pkt: IncomingPacket) -> Option<NetworkEvent> {
        let sender_id = pkt.header.sender_id;

        // Send ACK for reliable packets.
        if is_reliable(pkt.header.packet_type) && pkt.header.sequence > 0 {
            let ack = PacketPayload::Ack {
                acked_sequence: pkt.header.sequence,
            };
            self.send_raw_to(pkt.addr, &ack).await;
        }

        // Update heartbeat for known peers by logical peer id. The socket address is transport only.
        //
        // The address is NOT adopted when it already belongs to a DIFFERENT known peer. Reason:
        // the ADR-015 pose relay (`send_unreliable_as`) re-emits peer B's PlayerUpdate towards C
        // from the HOST's socket while stamping `sender_id = B`. Adopting unconditionally made C
        // overwrite `peers[B].addr` with the host's address 10x/second, so every joiner ended up
        // believing every other joiner lived at the host — there was no direct route left to
        // discover. Refusing only the addresses that another peer already owns keeps genuine NAT
        // rebinding working (a new, unclaimed address is still adopted).
        let relayed_from_other_peer = self
            .peers
            .iter()
            .any(|(id, p)| *id != sender_id && p.addr == pkt.addr);
        let mut log_last_seen_update = false;
        if let Some(peer) = self.peers.get_mut(&sender_id) {
            if !relayed_from_other_peer {
                peer.addr = pkt.addr;
            }
            peer.record_heartbeat();
            let should_log = self
                .last_keepalive_trace_at
                .get(&sender_id)
                .map(|last| last.elapsed() >= Duration::from_secs(1))
                .unwrap_or(true);
            if should_log {
                self.last_keepalive_trace_at
                    .insert(sender_id, Instant::now());
                log_last_seen_update = true;
            }
        }
        if log_last_seen_update {
            info!(
                "MPTRACE step=N event=peer_last_seen_update reason=packet_received self_id={} peer_id={} endpoint={} addr_adopted={} last_seen_ms=0 peer_count={} remote_players_ids={:?}",
                self.local_id,
                sender_id,
                pkt.addr,
                !relayed_from_other_peer,
                self.peers.len(),
                self.peer_ids()
            );
        }

        // Payload → event dispatch. `dispatch_payload!` exists for ONE reason: seventeen of
        // these arms were the same copy — destructure `PacketPayload::X`, rebuild
        // `NetworkEvent::X` from the same fields under the same names. A `macro_rules!` cannot
        // expand into match arms, only into a whole `match`, so the invocation alternates
        // `{ arms written out verbatim }` with `[ the 1:1 variants ]` and every arm keeps the
        // exact position it had when each one was spelled out by hand.
        //
        // A `[ ... ]` entry expands to EXACTLY
        //     PacketPayload::X { a, b } => Some(NetworkEvent::X { a, b }),
        // so an arm belongs there only when it does nothing else. Anything that touches `self`,
        // logs, renames the variant (`CorpseList` → `CorpseListReceived`, the whole `*Received`
        // family) or fills a field from the HEADER instead of the payload (`VoiceFrame`'s
        // `speaker`, `ChunkTransfer`'s `from`) stays written out inside a `{ ... }` group.
        macro_rules! dispatch_payload {
            ($({ $($verbatim:tt)* } [ $($variant:ident { $($field:ident),* $(,)? }),* $(,)? ])*) => {
                match pkt.payload {
                    $(
                        $($verbatim)*
                        $(PacketPayload::$variant { $($field),* } =>
                            Some(NetworkEvent::$variant { $($field),* }),)*
                    )*
                }
            };
        }

        dispatch_payload!(
        {
            PacketPayload::Handshake {
                player_name,
                version,
            } => {
                info!(
                    "Received handshake from addr={} sender_id={} name={}",
                    pkt.addr, sender_id, player_name
                );
                info!(
                    "MPTRACE step=B event=host_receive_handshake self_id={} sender_id={} assigned_id=<pending> peer_id=<pending> endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
                    self.local_id,
                    sender_id,
                    pkt.addr,
                    self.peers.len(),
                    self.peer_ids()
                );
                self.handle_handshake(pkt.addr, sender_id, player_name, version)
                    .await
            }

            PacketPayload::HandshakeAck {
                assigned_id,
                world_seed,
                config: _,
                peers,
                anchors: _,
                stabilizers: _,
            } => self.handle_handshake_ack(pkt.addr, sender_id, assigned_id, world_seed, peers),

            PacketPayload::Heartbeat => {
                // Already updated heartbeat above.
                None
            }

            PacketPayload::Disconnect { reason } => {
                if let Some(peer) = self.peers.remove(&sender_id) {
                    info!(
                        "Peer {} ({}) disconnected: {}",
                        peer.name, peer.addr, reason
                    );
                    info!(
                        "MPTRACE step=L event=peer_removed reason=disconnect_packet self_id={} peer_id={} endpoint={} peer_count_before=<unknown> peer_count_after={} remote_players_ids={:?}",
                        self.local_id,
                        sender_id,
                        peer.addr,
                        self.peers.len(),
                        self.peer_ids()
                    );
                    Some(NetworkEvent::PeerDisconnected {
                        id: sender_id,
                        reason,
                    })
                } else {
                    None
                }
            }

            PacketPayload::PeerList { peers } => {
                // Host-as-server relay: the host periodically sends the full roster so each
                // joiner learns about ALL peers, not just the host. Insert peers we don't know
                // yet (the other joiners) using the address the host reported, and refresh
                // positions for the ones we already track. build_world_state reads net.peers,
                // so this is exactly what makes the other joiners appear in our world_state.
                for info in &peers {
                    if info.id == self.local_id {
                        continue; // never track ourselves as a remote
                    }
                    if let Some(peer) = self.peers.get_mut(&info.id) {
                        let rot = peer.rotation;
                        let anim = peer.animation.clone();
                        peer.update_player_state(info.position, rot, anim);
                    } else if let Ok(addr) = info.addr.parse::<SocketAddr>() {
                        let mut conn = PeerConnection::new(info.id, info.name.clone(), addr);
                        conn.update_player_state(info.position, 0.0, "idle".into());
                        self.peers.insert(info.id, conn);
                    }
                }
                None
            }

            PacketPayload::StpItemList { items } => {
                // Host-authoritative STP item roster: joiners mirror it verbatim so
                // their build_world_state replicates the same items. (Phase 1.)
                self.stp_items = items;
                None
            }

            PacketPayload::StpBuildingList { buildings } => {
                // Host-authoritative STP building roster: joiners mirror it verbatim so
                // their build_world_state replicates the same pieces. (Phase B1.)
                self.stp_buildings = buildings;
                None
            }

        }
        [
            StpPlaceRequest { place_id, def_id, position, rotation, group_id, is_group },
            StpBuildAddRequest { add_id, building_id, material_id },
            StpDemolishRequest { demolish_id, building_id },
        ]
        {

            PacketPayload::StpCarryableList { carryables } => {
                // Host-authoritative carryable roster: joiners mirror it verbatim. (B2.5)
                self.stp_carryables = carryables;
                None
            }

        }
        [
            StpCarryablePickupRequest { carryable_id, requester_id },
            StpCarryablePickupGranted { carryable_id, def_id },
            StpCarryableDropRequest { drop_id, def_id, position, rotation },
        ]
        {

            PacketPayload::StpHarvestableList { harvestables } => {
                // Host-authoritative harvestable health roster: joiners mirror it. (B2.6)
                self.stp_harvestables = harvestables;
                None
            }

        }
        [
            StpHarvestHitRequest { hit_id, harvestable_id, amount },
            StpPickupRequest { item_id, requester_id },
            // ADR-028 Fase E: corpse relay — 1:1 payload→event mapping; all the authority
            // logic (dedupe, spawn/take, verdict relay, mirroring) lives in game_loop, which
            // owns World (corpses live in world.corpses, not in NetworkManager).
            CorpseSpawnRequest { request_id, requester_id, owner_name, position, equipment,
                held_item, items },
            CorpseTakeRequest { request_id, requester_id, corpse_id, item_index, quantity,
                requester_pos },
            CorpseTakeResult { request_id, accepted, corpse_id, item_index, item_id, quantity,
                corpse_empty, reason },
        ]
        {

            PacketPayload::CorpseList { corpses } => {
                Some(NetworkEvent::CorpseListReceived { corpses })
            }

        }
        [
            // ADR-029 V0: PvP relay — 1:1 payload→event mapping; all authority logic
            // (dedupe, validation order, grant/reject dispatch) lives in game_loop, which
            // owns Player/PlayerStats (health lives there, not in NetworkManager).
            PvpHitCandidate { request_id, attacker_id, victim_id, weapon_id, damage, origin,
                direction, client_tick, hit_position },
            PvpDamageGrant { request_id, attacker_id, victim_id, weapon_id, damage, reason },
            PvpHitRejected { request_id, attacker_id, victim_id, reason },
            // ADR-047 — decode only. Every authority check (are we really the victim? is this a
            // retransmit? are we invulnerable?) lives in game_loop.rs, the same split the PvP
            // family above uses.
            PhantomAttackGrant { request_id, victim_id, kind, damage, impulse },
        ]
        {

            PacketPayload::NoiseReport { position, loudness } => {
                Some(NetworkEvent::NoiseReported { position, loudness })
            }

            PacketPayload::VoiceFrame { seq, data } => Some(NetworkEvent::VoiceReceived {
                speaker: sender_id,
                seq,
                data,
            }),

        }
        [
            StpPickupGranted { item_id, def_id, count },
            StpDropRequest { drop_id, def_id, count, position, rotation },
        ]
        {

            PacketPayload::PlayerUpdate {
                position,
                rotation,
                animation,
                crouch,
                pitch,
                equipment,
                held_item,
                hit_seq,
                dead,
                revealed,
                light_on,
                fire_seq,
                buttons,
                melee_seq,
                vocal_seq,
                vocal_kind,
                carry_def,
                carry_count,
            } => {
                if let Some(peer) = self.peers.get_mut(&sender_id) {
                    peer.update_player_state(position, rotation, animation.clone());
                    peer.crouch = crouch; // ADR-020: cosmetic crouch, alongside the pose
                    peer.pitch = pitch; // ADR-021: cosmetic camera pitch, alongside the pose
                    peer.equipment = equipment; // ADR-022: cosmetic clothing, alongside the pose
                    peer.held_item = held_item; // ADR-023: cosmetic held item, alongside the pose
                    peer.hit_seq = hit_seq; // ADR-024: cosmetic hit-reaction counter, alongside the pose
                    peer.dead = dead; // ADR-028 post-E3: cosmetic dead flag, alongside the pose
                    peer.revealed = revealed; // ADR-038: cosmetic real-form flag, alongside the pose
                    peer.light_on = light_on; // ADR-042: cosmetic held-light flag, alongside the pose
                    peer.fire_seq = fire_seq; // ADR-042: cosmetic shot counter, alongside the pose
                    peer.buttons = buttons; // ADR-044: cosmetic aim/reload bits, alongside the pose
                    peer.melee_seq = melee_seq; // ADR-044: cosmetic swing counter, alongside the pose
                    peer.vocal_seq = vocal_seq; // ADR-048: cosmetic vocalisation counter, alongside the pose
                    peer.vocal_kind = vocal_kind; // ADR-048: which voice the last bump was
                    peer.carry_def = carry_def; // ADR-049: cosmetic carry state, alongside the pose
                    peer.carry_count = carry_count; // ADR-049: plain assignments, not a struct literal — a dropped line relays 0 forever
                }
                let should_log = self
                    .last_transform_trace_at
                    .get(&sender_id)
                    .map(|last| last.elapsed() >= Duration::from_secs(1))
                    .unwrap_or(true);
                if should_log {
                    self.last_transform_trace_at
                        .insert(sender_id, Instant::now());
                    // Shares the MPTRACE 1 s window on purpose: this used to fire on EVERY
                    // PlayerUpdate (10 Hz per peer), and the MPTRACE line below is a strict
                    // superset of it. stdout/stderr are PIPED to Unity (see ipc/server.rs), so a
                    // per-packet log is backpressure on the game, not just noise.
                    info!(
                        "Received player update from peer id={} pos=({:.2}, {:.2}, {:.2})",
                        sender_id, position[0], position[1], position[2]
                    );
                    info!(
                        "MPTRACE step=S event=receive_player_update self_id={} peer_id={} sender_id={} endpoint={} peer_count={} pos=({:.2},{:.2},{:.2}) rot={:.2}",
                        self.local_id,
                        sender_id,
                        sender_id,
                        pkt.addr,
                        self.peers.len(),
                        position[0],
                        position[1],
                        position[2],
                        rotation
                    );
                }
                Some(NetworkEvent::RemotePlayerUpdate {
                    id: sender_id,
                    position,
                    rotation,
                    animation,
                    crouch,
                    pitch,
                    equipment,
                    held_item,
                    hit_seq,
                    dead,
                    revealed,
                    light_on,
                    fire_seq,
                    buttons,
                    melee_seq,
                    vocal_seq,
                    vocal_kind,
                    carry_def,
                    carry_count,
                })
            }

            PacketPayload::WorldSync {
                world_seed,
                world_revision,
                chunks,
            } => {
                info!(
                    "MPTRACE step=Y event=receive_world_snapshot self_id={} from_peer={} revision={} chunks={} entities={} items={}",
                    self.local_id,
                    sender_id,
                    world_revision,
                    chunks.len(),
                    chunks.iter().map(|c| c.entities.len()).sum::<usize>(),
                    chunks.iter().map(|c| c.items.len()).sum::<usize>()
                );
                Some(NetworkEvent::WorldSyncReceived {
                    world_seed,
                    world_revision,
                    chunks,
                })
            }

            PacketPayload::ChunkState { data } => {
                // Treat as a chunk transfer for now.
                Some(NetworkEvent::ChunkTransferReceived {
                    from: sender_id,
                    data,
                })
            }

            PacketPayload::ChunkTransfer { data } => Some(NetworkEvent::ChunkTransferReceived {
                from: sender_id,
                data,
            }),

            PacketPayload::ChunkTransferAck { pos } => {
                Some(NetworkEvent::ChunkTransferAckReceived {
                    from: sender_id,
                    pos,
                })
            }

            PacketPayload::ChunkTeleport {
                old_pos,
                new_pos,
                new_seed,
            } => Some(NetworkEvent::ChunkTeleportReceived {
                old_pos,
                new_pos,
                new_seed,
            }),

            PacketPayload::AnchorBroadcast {
                chunk_pos,
                durability,
                installed_by,
            } => Some(NetworkEvent::AnchorBroadcastReceived {
                chunk_pos,
                durability,
                installed_by,
            }),

            PacketPayload::StabilizerBroadcast {
                chunk_pos,
                tier,
                remaining_hours,
            } => Some(NetworkEvent::StabilizerBroadcastReceived {
                chunk_pos,
                tier,
                remaining_hours,
            }),

            PacketPayload::Ack { acked_sequence } => {
                if let Some(peer) = self.peers.get_mut(&sender_id) {
                    peer.process_ack(acked_sequence);
                }
                None
            }

            PacketPayload::Nack {
                requested_sequence: _,
            } => {
                // Future: retransmit the requested packet.
                None
            }

            PacketPayload::Ping { send_time } => {
                // Respond with the same timestamp so the sender can measure RTT.
                let pong = PacketPayload::Ping { send_time };
                self.send_raw_to(pkt.addr, &pong).await;
                None
            }

            // Action packets — forward to game loop as-is.
            PacketPayload::Interact {
                requester_id,
                request_id,
                target_id,
                target_kind,
                interaction_type,
                player_position,
            } => {
                info!(
                    "MPTRACE step=AE event=host_receive_interact_request self_id={} requester_id={} target_id={} request_id={} kind={} type={}",
                    self.local_id,
                    requester_id,
                    target_id,
                    request_id,
                    target_kind,
                    interaction_type
                );
                Some(NetworkEvent::WorldInteractRequest {
                    requester_id,
                    request_id,
                    target_id,
                    target_kind,
                    interaction_type,
                    player_position,
                })
            }

            PacketPayload::Attack { .. }
            | PacketPayload::Pickup { .. }
            | PacketPayload::Drop { .. }
            | PacketPayload::Craft { .. }
            | PacketPayload::PlaceStabilizer { .. }
            | PacketPayload::PlaceAnchor
            | PacketPayload::ChunkDelta { .. }
            | PacketPayload::EntityUpdate { .. } => {
                // These will be processed when full action handling is wired up.
                None
            }
        }
        []
        )
    }

    pub(super) async fn handle_handshake(
        &mut self,
        from_addr: SocketAddr,
        sender_id: PeerId,
        player_name: String,
        _version: String,
    ) -> Option<NetworkEvent> {
        if !self.is_host {
            // Only the host accepts handshakes.
            return None;
        }

        if let Some(existing) = self.peers.get(&sender_id) {
            info!(
                "Duplicate handshake from addr={} sender_id={} peer_id={}",
                from_addr, sender_id, existing.id
            );
            info!(
                "MPTRACE step=C event=host_peer_already_registered self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
                self.local_id,
                sender_id,
                existing.id,
                existing.id,
                from_addr,
                self.peers.len(),
                self.peer_ids()
            );
            self.send_handshake_ack(from_addr, sender_id, existing.id)
                .await;
            return None;
        }

        if let Some(existing) = self.peers.values().find(|p| p.addr == from_addr) {
            info!(
                "Duplicate handshake from addr={} sender_id={} already assigned id={}",
                from_addr, sender_id, existing.id
            );
            info!(
                "MPTRACE step=C event=host_peer_already_registered_by_endpoint self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
                self.local_id,
                sender_id,
                existing.id,
                existing.id,
                from_addr,
                self.peers.len(),
                self.peer_ids()
            );
            self.send_handshake_ack(from_addr, sender_id, existing.id)
                .await;
            return None;
        }

        // Aforo. `max_players` existía en SessionConfig y en WorldConfig, y NO se consultaba en
        // ningún sitio del árbol: el host aceptaba handshakes indefinidamente. Se aplica aquí,
        // el único punto donde entra un peer NUEVO — las dos ramas de arriba son reconexiones de
        // alguien ya admitido y no deben rebotar nunca.
        //
        // Se compara contra `real_peer_count()` para que un fantasma (ADR-016) no consuma plaza,
        // y contra `max_players - 1` porque el host ocupa una y no está en `peers`. El valor sale
        // de `SessionConfig::default()`, que es exactamente el que el propio HandshakeAck
        // anuncia en `build_handshake_ack` — anunciar 50 y admitir infinitos era la incoherencia.
        let capacity = (SessionConfig::default().max_players as usize).saturating_sub(1);
        if self.real_peer_count() >= capacity {
            warn!(
                "MPTRACE step=B2 event=host_reject_handshake_session_full self_id={} sender_id={} endpoint={} real_peer_count={} capacity={}",
                self.local_id,
                sender_id,
                from_addr,
                self.real_peer_count(),
                capacity
            );
            let full = PacketPayload::Disconnect {
                reason: "session full".into(),
            };
            self.send_raw_to(from_addr, &full).await;
            return None;
        }

        let assigned_id = self.allocate_peer_id(sender_id);

        info!(
            "New peer connecting sender_id={} name={} from {} -> assigned id {}",
            sender_id, player_name, from_addr, assigned_id
        );

        // Add the peer.
        let peer = PeerConnection::new(assigned_id, player_name.clone(), from_addr);
        self.peers.insert(assigned_id, peer);
        info!(
            "MPTRACE step=C event=host_register_peer self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
            self.local_id,
            sender_id,
            assigned_id,
            assigned_id,
            from_addr,
            self.peers.len(),
            self.peer_ids()
        );

        // Send HandshakeAck with world info.
        self.send_handshake_ack(from_addr, sender_id, assigned_id)
            .await;

        Some(NetworkEvent::PeerConnected {
            id: assigned_id,
            name: player_name,
        })
    }

    fn handle_handshake_ack(
        &mut self,
        from_addr: SocketAddr,
        sender_id: PeerId,
        assigned_id: PeerId,
        world_seed: u64,
        peers: Vec<PeerInfo>,
    ) -> Option<NetworkEvent> {
        if self.is_host {
            return None; // Host doesn't receive handshake acks.
        }

        info!(
            "Handshake ACK received from {} sender_id={} assigned_id={}, world_seed={}, {} peers",
            from_addr,
            sender_id,
            assigned_id,
            world_seed,
            peers.len()
        );
        info!(
            "MPTRACE step=E event=joiner_receive_handshake_ack self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
            self.local_id,
            sender_id,
            assigned_id,
            sender_id,
            from_addr,
            self.peers.len(),
            self.peer_ids()
        );

        // Update our local ID to the one assigned by the host.
        self.local_id = assigned_id;
        self.world_seed = world_seed;
        self.pending_connect_addr = None;

        // Add the host as a peer.
        let host_peer = PeerConnection::new(sender_id, "Host".to_string(), from_addr);
        self.peers.insert(sender_id, host_peer);
        info!(
            "MPTRACE step=F event=joiner_register_host self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
            self.local_id,
            sender_id,
            assigned_id,
            sender_id,
            from_addr,
            self.peers.len(),
            self.peer_ids()
        );

        Some(NetworkEvent::PeerConnected {
            id: sender_id,
            name: "Host".into(),
        })
    }

    pub fn peer_count(&self) -> usize {
        self.peers.len()
    }

    pub fn peer_ids(&self) -> Vec<PeerId> {
        let mut ids: Vec<PeerId> = self.peers.keys().copied().collect();
        ids.sort_unstable();
        ids
    }

    pub fn peer_endpoints(&self) -> Vec<String> {
        let mut endpoints: Vec<String> = self
            .peers
            .values()
            .map(|p| format!("{}={}", p.id, p.addr))
            .collect();
        endpoints.sort();
        endpoints
    }

    /// Build the `HandshakeAck` payload for `assigned_id` from the current peer table.
    /// Single source for the three handshake paths (new peer / duplicate by id / duplicate by
    /// endpoint), which previously carried byte-identical copies of this block.
    fn build_handshake_ack(&self, assigned_id: PeerId) -> PacketPayload {
        PacketPayload::HandshakeAck {
            assigned_id,
            world_seed: self.world_seed,
            config: SessionConfig::default(),
            peers: self
                .peers
                .values()
                .map(|p| PeerInfo {
                    id: p.id,
                    name: p.name.clone(),
                    addr: p.addr.to_string(),
                    position: p.position,
                })
                .collect(),
            anchors: vec![],
            stabilizers: vec![],
        }
    }

    /// Send the `HandshakeAck` for `assigned_id` to `from_addr`, preceded by the two log lines
    /// that have always accompanied it (`Sending handshake ACK` + `MPTRACE step=D`).
    ///
    /// Serves the same three handshake paths as `build_handshake_ack` (new peer / duplicate by
    /// id / duplicate by endpoint): each kept a byte-identical copy of this tail, differing only
    /// in the NAME of the id passed (`existing.id` vs `assigned_id`). The order is verbatim —
    /// payload first (so its peer snapshot predates the logs), then the two logs, then the
    /// datagram — and both logs still read `self.peers`/`self.peer_ids()` at send time, so the
    /// new-peer path keeps reporting the roster WITH the peer just inserted.
    ///
    /// `&self` because `build_handshake_ack` and `send_raw_to` are both `&self`; that is what
    /// lets the two duplicate branches call it while still holding their `&PeerConnection`.
    async fn send_handshake_ack(
        &self,
        from_addr: SocketAddr,
        sender_id: PeerId,
        assigned_id: PeerId,
    ) {
        let ack_payload = self.build_handshake_ack(assigned_id);
        info!(
            "Sending handshake ACK to {} assigned_id={}",
            from_addr, assigned_id
        );
        info!(
            "MPTRACE step=D event=host_send_handshake_ack self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
            self.local_id,
            sender_id,
            assigned_id,
            assigned_id,
            from_addr,
            self.peers.len(),
            self.peer_ids()
        );
        self.send_raw_to(from_addr, &ack_payload).await;
    }

    fn allocate_peer_id(&mut self, requested_id: PeerId) -> PeerId {
        if requested_id != 0
            && requested_id != self.local_id
            && !self.peers.contains_key(&requested_id)
        {
            return requested_id;
        }

        while self.next_peer_id == 0
            || self.next_peer_id == self.local_id
            || self.peers.contains_key(&self.next_peer_id)
        {
            self.next_peer_id = self.next_peer_id.wrapping_add(1);
            if self.next_peer_id < 2 {
                self.next_peer_id = 2;
            }
        }

        let assigned_id = self.next_peer_id;
        self.next_peer_id = self.next_peer_id.wrapping_add(1);
        if self.next_peer_id < 2 {
            self.next_peer_id = 2;
        }
        assigned_id
    }
}
