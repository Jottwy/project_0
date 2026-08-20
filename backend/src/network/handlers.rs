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
                room_manifest_digest,
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
                self.handle_handshake(pkt.addr, sender_id, player_name, version, room_manifest_digest)
                    .await
            }

            PacketPayload::HandshakeAck {
                assigned_id,
                world_seed,
                config: _,
                peers,
                anchors: _,
                stabilizers: _,
                phantom_density_scale,
            } => self.handle_handshake_ack(
                pkt.addr,
                sender_id,
                assigned_id,
                world_seed,
                peers,
                phantom_density_scale,
            ),

            PacketPayload::Heartbeat => {
                // Already updated heartbeat above.
                None
            }

            PacketPayload::Disconnect { reason } => {
                if let Some(peer) = self.peers.remove(&sender_id) {
                    self.purge_peer_state(sender_id);
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
                } else if !self.is_host
                    && self.peers.is_empty()
                    && self.pending_connect_addr == Some(pkt.addr)
                {
                    // Corrección adosada a ADR-060: nuestro propio handshake pre-registro fue
                    // rechazado (session full o version mismatch). Sin esto, `retry_pending_
                    // connection` reenviaba el mismo handshake cada 1s para siempre y Unity nunca
                    // se enteraba — ver doc-comment de `NetworkEvent::ConnectRejected`.
                    warn!(
                        "MPTRACE step=A2 event=joiner_connect_rejected self_id={} endpoint={} reason={}",
                        self.local_id, pkt.addr, reason
                    );
                    self.pending_connect_addr = None;
                    Some(NetworkEvent::ConnectRejected { reason })
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
                    if info.relay_only {
                        // ADR-079: entrada solo-relay (el fantasma del host). Se registra para
                        // que las poses relayadas (ADR-015) tengan dónde aplicar — sin esto la
                        // rama PlayerUpdate las descartaba y el joiner nunca vio al robapieles.
                        // La addr del wire es un placeholder y NO se usa: se estampa la inerte
                        // local (H10: un datagrama a una addr inerte envenena el socket), y toda
                        // la superficie de envío salta `relay_only`. El heartbeat se refresca en
                        // cada roster; cuando el fantasma despawnea deja de venir y
                        // check_timeouts lo cosecha a los 5 s — retirada silenciosa
                        // (`player_left` no tiene suscriptores en Unity).
                        let entry = self.peers.entry(info.id).or_insert_with(|| {
                            let mut conn = PeerConnection::new(
                                info.id,
                                info.name.clone(),
                                super::INERT_PEER_ADDR,
                            );
                            conn.relay_only = true;
                            conn
                        });
                        entry.relay_only = true;
                        let rot = entry.rotation;
                        let anim = entry.animation.clone();
                        entry.update_player_state(info.position, rot, anim);
                        continue;
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

            PacketPayload::StpItemList {
                items,
                generation,
                page,
                page_count,
            } => {
                // Host-authoritative STP item roster: joiners mirror it verbatim so
                // their build_world_state replicates the same items. (Phase 1.)
                //
                // ADR-060 (d): el reemplazo verbatim se conserva, pero solo cuando la generación
                // está COMPLETA — una página suelta no puede sustituir al roster entero.
                if let Some(complete) =
                    self.roster_assemblers
                        .items
                        .accept(generation, page, page_count, items)
                {
                    self.stp_items = complete;
                }
                None
            }

            PacketPayload::StpBuildingList {
                buildings,
                generation,
                page,
                page_count,
            } => {
                // Host-authoritative STP building roster: joiners mirror it verbatim so
                // their build_world_state replicates the same pieces. (Phase B1.)
                // ADR-060 (d): reemplazo solo con la generación completa.
                if let Some(complete) =
                    self.roster_assemblers
                        .buildings
                        .accept(generation, page, page_count, buildings)
                {
                    self.stp_buildings = complete;
                }
                None
            }

        }
        [
            StpBuildAddRequest { add_id, building_id, material_id },
            StpDemolishRequest { demolish_id, building_id },
        ]
        {
            // ADR-081: igual que los dos de abajo, `requester_id` sale de la CABECERA — es contra
            // esa identidad contra la que el host comprueba la propiedad del claim, y el payload
            // no puede tener voz en quién dice ser el que construye.
            PacketPayload::StpPlaceRequest {
                place_id,
                def_id,
                position,
                rotation,
                group_id,
                is_group,
            } => Some(NetworkEvent::StpPlaceRequest {
                place_id,
                def_id,
                position,
                rotation,
                group_id,
                is_group,
                requester_id: sender_id,
            }),

            // ADR-068: `requester_id` sale de la CABECERA, no del payload — por eso este arm no
            // puede vivir en la lista 1:1 de abajo. Es lo que impide que un cliente reclame estar
            // pintando desde la posición de otro para saltarse el tope de alcance.
            PacketPayload::SprayPlaceRequest {
                place_id,
                layer,
                world_pos,
                yaw,
                size,
                strokes,
            } => Some(NetworkEvent::SprayPlaceRequest {
                place_id,
                layer,
                world_pos,
                yaw,
                size,
                strokes,
                requester_id: sender_id,
            }),

            PacketPayload::SprayPlaced { spray } => {
                Some(NetworkEvent::SprayPlacedReceived { spray })
            }

            // ADR-078: el `painter_id` sale de la CABECERA por el mismo motivo que en
            // `SprayPlaceRequest` — el host reparte copias por distancia a ESE peer, y dejar que
            // el payload dijera quién pinta permitiría dibujar en nombre de otro.
            PacketPayload::SprayDraft {
                place_id,
                layer,
                anchor,
                yaw,
                color,
                width,
                first_index,
                points_mm,
            } => Some(NetworkEvent::SprayDraftReceived {
                place_id,
                layer,
                anchor,
                yaw,
                color,
                width,
                first_index,
                points_mm,
                painter_id: sender_id,
            }),

            // También lleva `requester_id` de la cabecera: la respuesta vuelve a ESE peer y no
            // a todos, así que el remitente es parte del evento.
            PacketPayload::SprayChunkRequest { cx, cz, layer } => {
                Some(NetworkEvent::SprayChunkRequest {
                    cx,
                    cz,
                    layer,
                    requester_id: sender_id,
                })
            }

            PacketPayload::StpCarryableList {
                carryables,
                generation,
                page,
                page_count,
            } => {
                // Host-authoritative carryable roster: joiners mirror it verbatim. (B2.5)
                // ADR-060 (d): reemplazo solo con la generación completa.
                if let Some(complete) = self.roster_assemblers.carryables.accept(
                    generation,
                    page,
                    page_count,
                    carryables,
                ) {
                    self.stp_carryables = complete;
                }
                None
            }

        }
        [
            StpCarryablePickupRequest { carryable_id, requester_id },
            StpCarryablePickupGranted { carryable_id, def_id },
            StpCarryableDropRequest { drop_id, def_id, position, rotation },
        ]
        {

            PacketPayload::StpHarvestableList {
                harvestables,
                generation,
                page,
                page_count,
            } => {
                // Host-authoritative harvestable health roster: joiners mirror it. (B2.6)
                // ADR-060 (d): reemplazo solo con la generación completa.
                if let Some(complete) = self.roster_assemblers.harvestables.accept(
                    generation,
                    page,
                    page_count,
                    harvestables,
                ) {
                    self.stp_harvestables = complete;
                }
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

            // ADR-060 (d): a diferencia de los otros cuatro rosters, éste no muta `self` sino que
            // emite un evento que `game_loop` aplica. El ensamblado vive igualmente aquí (es donde
            // está el buffer) y el evento se emite SOLO con la generación completa: una página
            // suelta convertida en evento borraría los cadáveres de las demás páginas.
            PacketPayload::CorpseList {
                corpses,
                generation,
                page,
                page_count,
            } => self
                .roster_assemblers
                .corpses
                .accept(generation, page, page_count, corpses)
                .map(|complete| NetworkEvent::CorpseListReceived { corpses: complete }),

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

            // ADR-050 point 9. The sender IS the victim — the transport already knows who that is,
            // so the packet carries nothing and there is no field to validate or forge.
            PacketPayload::StruggleReport => {
                Some(NetworkEvent::StruggleReported { victim: sender_id })
            }

            PacketPayload::VoiceFrame { seq, data } => Some(NetworkEvent::VoiceReceived {
                speaker: sender_id,
                seq,
                data,
            }),

        }
        [
            StpPickupGranted { item_id, def_id, count },
            StpDropRequest { drop_id, def_id, count, position, rotation, velocity },
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

            // ADR-060: goteo de snapshot. El chunk es 1:1 payload→evento; el End loguea el
            // punto de medida del join (el gemelo de step=Y del monolito de arriba).
            PacketPayload::WorldSyncChunk {
                world_revision,
                data,
            } => Some(NetworkEvent::WorldSyncChunkReceived {
                world_revision,
                data,
            }),

            PacketPayload::WorldSyncEnd {
                world_revision,
                chunk_count,
            } => {
                info!(
                    "MPTRACE step=Y event=receive_world_drip_end self_id={} from_peer={} revision={} chunk_count={}",
                    self.local_id, sender_id, world_revision, chunk_count
                );
                Some(NetworkEvent::WorldSyncEndReceived {
                    world_revision,
                    chunk_count,
                })
            }

            // El "Treat as a chunk transfer for now" que vivía aquí fundía el broadcast periódico
            // con el handoff de propiedad, y con ello heredaba su ACK FIABLE: a ~820 chunks/s eso
            // llenaba permanentemente la ventana de 32 del receptor. Ahora cada uno tiene su
            // evento; la APLICACIÓN sigue siendo la misma en ambos, solo cambia si se confirma.
            PacketPayload::ChunkState { data } => Some(NetworkEvent::ChunkStateReceived {
                from: sender_id,
                data,
            }),

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
                // Auditoría (H12a, 2026-08-10): confirmado no-op — nadie en este crate construye
                // un `PacketPayload::Nack` (grep sin resultados fuera del propio decode/encode).
                // Se conserva el decode a propósito, sin implementar el reenvío: retirar la
                // variante tocaría el enum de wire de `protocol.rs` y por la regla dura #7
                // (cambio de API pública = ADR) eso es un ADR de limpieza, no una corrección
                // adosada. No compensa para un opcode que ya no se emite.
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
        version: String,
        room_manifest_digest: String,
    ) -> Option<NetworkEvent> {
        if !self.is_host {
            // Only the host accepts handshakes.
            //
            // NETPROBE (diagnóstico temporal): este `return None` era mudo. Un backend arrancado
            // con CONNECT_TO puesto (fuga de entorno heredado de Unity, ver el comentario de
            // `LaunchBackendProcess`) queda con is_host=false, escucha en 7778 y descarta TODOS
            // los handshakes sin dejar rastro — indistinguible desde fuera de "no llegó nada".
            warn!(
                "NETPROBE event=handshake_dropped reason=not_host self_id={} sender_id={} from={} name={} version={}",
                self.local_id, sender_id, from_addr, player_name, version
            );
            return None;
        }

        // Corrección adosada a ADR-060 (docs/DECISIONS.md, 2026-08-10): `version` used to be
        // ignored entirely (`_version`). Compared before the duplicate-peer branches below on
        // purpose — a mismatched joiner never got registered, so it can't fall into either of
        // those reconnect paths. Same rejection mechanism as "session full": raw `Disconnect`,
        // never entering `self.peers`.
        let expected_version = crate::ipc::server::WIRE_SCHEMA_VERSION.to_string();
        if version != expected_version {
            warn!(
                "MPTRACE step=B2 event=host_reject_handshake_version_mismatch self_id={} sender_id={} endpoint={} host_version={} joiner_version={}",
                self.local_id, sender_id, from_addr, expected_version, version
            );
            let mismatch = PacketPayload::Disconnect {
                reason: format!("wire schema mismatch: host={expected_version} joiner={version}"),
            };
            self.send_raw_to(from_addr, &mismatch).await;
            return None;
        }

        // ADR-083 enmienda 1, punto 4 — el pool de salas autoradas tiene que ser el MISMO en los dos
        // builds. No viaja por la red: sale de un fichero que va dentro del build, y cada peer
        // genera el mundo por su cuenta desde el seed. Con pools distintos, uno pinta una sala donde
        // el otro pinta otra y nadie se entera hasta que alguien se choca con nada.
        //
        // Rechazo RUIDOSO, nunca degradación en silencio: lo prohíbe el ADR. Mismo mecanismo y mismo
        // sitio que el de versión de wire, y por delante del registro del peer.
        let expected_digest = crate::world::grid_gen::active_manifest()
            .map(|m| m.digest.clone())
            .unwrap_or_default();
        if room_manifest_digest != expected_digest {
            warn!(
                "MPTRACE step=B2 event=host_reject_handshake_room_manifest_mismatch self_id={} sender_id={} endpoint={} host_digest={} joiner_digest={}",
                self.local_id,
                sender_id,
                from_addr,
                if expected_digest.is_empty() { "<sin manifiesto>" } else { &expected_digest },
                if room_manifest_digest.is_empty() { "<sin manifiesto>" } else { &room_manifest_digest }
            );
            let mismatch = PacketPayload::Disconnect {
                reason: format!(
                    "room manifest mismatch: host={} joiner={}",
                    if expected_digest.is_empty() {
                        "<none>"
                    } else {
                        &expected_digest
                    },
                    if room_manifest_digest.is_empty() {
                        "<none>"
                    } else {
                        &room_manifest_digest
                    }
                ),
            };
            self.send_raw_to(from_addr, &mismatch).await;
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
        phantom_density_scale: f32,
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
        // ADR-045 Fase 2: the joiner now knows its world_seed — the other half (identity_key,
        // via set_identity) may already be waiting on this in game_loop::run.
        self.world_seed_known = true;
        // P0-2: the host's value always wins — same precedent as world_seed above. Our own
        // env-derived value never reaches PhantomDriver (nothing consumes it before this).
        if self.phantom_density_scale != phantom_density_scale {
            warn!(
                "P0-2: local PHANTOM_DENSITY_SCALE {} differs from host's {}; adopting host's value",
                self.phantom_density_scale, phantom_density_scale
            );
        }
        self.phantom_density_scale = phantom_density_scale;
        self.pending_connect_addr = None;

        // Add the host as a peer.
        let host_peer = PeerConnection::new(sender_id, "Host".to_string(), from_addr);
        self.peers.insert(sender_id, host_peer);
        // ADR-056: remember WHICH peer that is, so a later `PeerDisconnected` can tell the host
        // falling over from any other peer leaving. Set here and nowhere else — this is the one
        // place a joiner learns the host's identity first-hand.
        self.host_peer_id = Some(sender_id);
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

    /// Orphan cleanup on disconnect (explicit `Disconnect` packet or heartbeat timeout).
    /// `PeerId` is reused after a peer leaves (`allocate_peer_id` just hands out the next free
    /// number), so any state keyed by `PeerId` that outlives the disconnect would silently
    /// apply to whichever different player inherits the number next. `pending_pickups` is
    /// excluded on purpose — it self-purges on its own ~400ms deadline regardless of connection
    /// (ADR-014 drain in `game_loop.rs`), so there is nothing here for it to leak.
    pub(super) fn purge_peer_state(&mut self, id: PeerId) {
        self.voice_echo.remove(&id);
        self.pending_struggles.remove(&id);
        self.processed_corpse_requests
            .retain(|(peer, _)| *peer != id);
        self.last_keepalive_trace_at.remove(&id);
        self.last_transform_trace_at.remove(&id);
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
                .map(|p| {
                    // ADR-079: misma regla que build_peer_list — la marca viaja y la addr
                    // inerte de un fantasma no. handle_handshake_ack ignora esta lista hoy
                    // (solo registra al host), pero una superficie que emite peers al wire
                    // sin la regla es exactamente cómo nació el agujero H10.
                    let relay_only = self.is_phantom(p.id) || p.relay_only;
                    PeerInfo {
                        id: p.id,
                        name: p.name.clone(),
                        addr: if relay_only {
                            "0.0.0.0:0".to_string()
                        } else {
                            p.addr.to_string()
                        },
                        position: p.position,
                        relay_only,
                    }
                })
                .collect(),
            anchors: vec![],
            stabilizers: vec![],
            phantom_density_scale: self.phantom_density_scale,
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
