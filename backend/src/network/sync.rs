//! State synchronization: broadcast functions that convert game state to protocol
//! payloads and send them via the NetworkManager.
//! See ARCHITECTURE_V1.md §5.4 and §3.2.
//!
//! SIX FUNCTIONS HERE HAVE NO CALL SITES, and they are unfinished features rather than cruft —
//! the anchor/stabilizer chain was designed and wired up to the wire format but never plugged
//! into the loop. Do not "clean them up" without deciding the feature first:
//!   `build_session_config`, `send_chunk_transfer`, `broadcast_anchor`, `broadcast_stabilizer`,
//!   `build_anchor_list`, `build_stabilizer_list`.
//! They are invisible to the compiler because the crate carries `#![allow(dead_code)]`
//! (`main.rs`); to see them, comment that out and read `cargo test --no-run`.

use crate::player::session::Player;
use crate::utils::{world_to_chunk, Vec3};
use crate::world::chunk::{Chunk, ChunkState};
use crate::world::World;

use log::info;

use super::protocol::{
    encode_packet, AnchorInfo, ChunkSyncData, EntitySyncData, ItemSyncData, PacketHeader,
    PacketPayload, PeerInfo, SessionConfig, StabilizerInfo,
};
use super::NetworkManager;
use super::PeerId;

// ─── Conversion: game types → sync types ───

pub fn chunk_to_sync_data(chunk: &Chunk) -> ChunkSyncData {
    let (stabilized, anchored) = match chunk.state {
        ChunkState::Active {
            stabilized,
            anchored,
        } => (stabilized, anchored),
        _ => (false, false),
    };

    ChunkSyncData {
        pos: [chunk.pos.0, chunk.pos.1],
        layer: chunk.layer,
        seed: chunk.seed,
        template_id: chunk.template_id,
        rotation: chunk.rotation,
        mirrored: chunk.mirrored,
        has_workbench: chunk.has_workbench,
        layout: chunk.layout.clone(),
        stabilized,
        anchored,
        teleport_timer: chunk.teleport_timer,
        entities: chunk
            .entities
            .iter()
            .map(|e| EntitySyncData {
                id: e.id,
                entity_type: e.entity_type.type_name().into(),
                position: e.position.to_array(),
                rotation: e.rotation,
                health: e.health,
                state: e.state.state_name().into(),
            })
            .collect(),
        items: chunk
            .items
            .iter()
            .map(|i| ItemSyncData {
                id: i.id,
                item_type: i.item.type_name().into(),
                quantity: i.quantity,
                position: i.position.to_array(),
            })
            .collect(),
    }
}

pub fn build_session_config(world: &World) -> SessionConfig {
    SessionConfig {
        max_players: world.config.max_players,
        world_name: "Backrooms".into(),
        teleport_interval_min: world.config.teleport_interval.0,
        teleport_interval_max: world.config.teleport_interval.1,
    }
}

pub fn build_peer_list(net: &NetworkManager, local_player: &Player) -> Vec<PeerInfo> {
    let mut peers = vec![PeerInfo {
        id: net.local_id,
        name: local_player.name.clone(),
        addr: net.local_addr().to_string(),
        position: local_player.position.to_array(),
    }];
    for peer in net.peers.values() {
        peers.push(PeerInfo {
            id: peer.id,
            name: peer.name.clone(),
            addr: peer.addr.to_string(),
            position: peer.position,
        });
    }
    peers
}

// ─── Broadcast functions ───

/// Broadcast local player position/rotation to all peers (unreliable, 10hz).
pub async fn broadcast_player_update(net: &NetworkManager, player: &Player) {
    if net.peers.is_empty() {
        return;
    }
    // ADR-011: a recent local pickup takes priority — a trigger flank held ~1s so the client
    // reliably catches the transition despite ~5Hz sample spacing. The gesture's duration is
    // owned by the client (Animator exitTime), not by this window.
    let animation = if net
        .last_pickup_at
        .is_some_and(|t| t.elapsed().as_millis() < 1000)
    {
        "pickup"
    } else if player.stats.speed_modifier < 1.0 {
        "walk_slow"
    } else {
        "idle"
    };
    let payload = PacketPayload::PlayerUpdate {
        position: player.position.to_array(),
        rotation: player.rotation,
        animation: animation.into(),
        crouch: player.crouch,
        pitch: player.pitch,
        equipment: player.equipment,
        held_item: player.held_item,
        hit_seq: player.hit_seq,
        // ADR-028 post-E3: SERVER-derived (authoritative stats, ADR-025) — the one pose field
        // the client does not report.
        dead: player.stats.is_dead(),
        // ADR-038: always false here — a real player never shows a "real form". The only
        // `true` in the whole system is sealed by PhantomDriver onto a PeerConnection and
        // travels via broadcast_peer_poses, not this path.
        revealed: player.revealed,
        vocal_seq: player.vocal_seq,
        vocal_kind: player.vocal_kind,
        // ADR-042: both client-reported and sealed in the game loop next to `hit_seq`.
        light_on: player.light_on,
        fire_seq: player.fire_seq,
        // ADR-044: `buttons` stops being the dead literal it was and carries the aim/reload bits.
        buttons: player.buttons,
        melee_seq: player.melee_seq,
        // ADR-049: client-reported carry state, sealed in the game loop next to `melee_seq`.
        carry_def: player.carry_def,
        carry_count: player.carry_count,
    };
    // Both lines are on the same once-a-second window now. This runs at the full tick rate, so
    // unthrottled it was the single noisiest line in the backend log — it formatted three floats
    // every tick and buried the TP_WATCH / MPTRACE traces the open TP-attribution diagnosis needs.
    if net.session_start.elapsed().as_millis() % 1000 < 120 {
        info!(
            "Sending player update to peers={} local_id={} pos=({:.2}, {:.2}, {:.2})",
            net.peers.len(),
            net.local_id,
            player.position.x,
            player.position.y,
            player.position.z
        );
        info!(
            "MPTRACE step=R event=send_player_update self_id={} peer_count={} pos=({:.2},{:.2},{:.2}) rot={:.2}",
            net.local_id,
            net.peers.len(),
            player.position.x,
            player.position.y,
            player.position.z,
            player.rotation
        );
    }
    net.broadcast_unreliable(&payload).await;
}

/// Host-as-server relay: broadcast the FULL peer roster (every peer's id + current
/// position) to all connected peers, so each joiner learns about ALL other peers and
/// not just the host. A joiner only connects to the host, so without this it never
/// sees the other joiners. Sent at the player-update cadence, by the host only.
pub async fn broadcast_peer_roster(net: &NetworkManager, player: &Player) {
    if net.peers.is_empty() {
        return;
    }
    let payload = PacketPayload::PeerList {
        peers: build_peer_list(net, player),
    };
    net.broadcast_unreliable(&payload).await;
}

/// ADR-043 — peers a relayed pose may legitimately be ADDRESSED to: every real peer, never a
/// phantom. Split out of `broadcast_peer_poses` so the invariant is testable without a socket:
/// the alternative (asserting on datagrams) would need a live UDP endpoint per phantom, which is
/// exactly the thing that does not exist — a phantom's `addr` is the inert `127.0.0.1:1` stamped
/// at injection (`NetworkManager::spawn_phantom`).
pub(crate) fn relay_destinations(net: &NetworkManager) -> Vec<PeerId> {
    net.peers
        .keys()
        .copied()
        .filter(|id| !net.is_phantom(*id))
        .collect()
}

/// ADR-046 — how far a voice carries, in metres. Sits between the two peer sounds that already
/// exist: a footstep dies at 22 m and a pain grunt at 28 m (`ProxyFootstepHook`,
/// `ProxyDamageAudioHook`), so a voice reaching 25 m is louder than a step and about as far as a
/// cry. The client fades to true silence at exactly this distance with the hard-cutoff curve.
pub const VOICE_RADIUS_M: f32 = 25.0;

/// Extra distance the host will still relay over. Hysteresis, not slack: peer positions land at
/// 10 Hz, so a listener walking the boundary would otherwise have the voice cut mid-word every
/// time a pose update crossed the line. Frames inside the margin arrive and are attenuated to
/// silence by the client's curve, which costs a few bytes and sounds like nothing.
pub const VOICE_RELAY_MARGIN_M: f32 = 6.0;

fn distance_sq(a: [f32; 3], b: [f32; 3]) -> f32 {
    let (dx, dy, dz) = (a[0] - b[0], a[1] - b[1], a[2] - b[2]);
    dx * dx + dy * dy + dz * dz
}

/// True when `a` and `b` are close enough for voice to travel between them.
///
/// 3D and not XZ on purpose: the layers are stacked vertically, so plain euclidean distance
/// already keeps a speaker on layer 1 from being heard by someone standing above them. ADR-043
/// needed an explicit layer comparison because it was culling by XZ; here the Y term does it.
pub fn within_voice_range(a: [f32; 3], b: [f32; 3]) -> bool {
    let reach = VOICE_RADIUS_M + VOICE_RELAY_MARGIN_M;
    distance_sq(a, b) <= reach * reach
}

/// ADR-046 — who gets a copy of `speaker`'s voice, decided by the HOST.
///
/// Three exclusions, each for its own reason:
/// * the speaker (nobody is relayed their own voice, same rule as the pose relay),
/// * phantoms, whose `addr` is the inert `127.0.0.1:1` — the destination filter ADR-043 added,
/// * peers out of earshot. THAT one is not an optimisation: it is the only thing stopping a
///   modified client from decoding a conversation happening across the level. A filter that
///   lived only in the listener would be a filter the listener can remove.
///
/// A DEAD peer is excluded too (ADR-046: the dead neither speak nor listen). The client stops
/// capturing on death as well, but that half only saves bandwidth — this half is the authority,
/// and it is what a patched client cannot get around.
pub(crate) fn voice_destinations(net: &NetworkManager, speaker: PeerId) -> Vec<PeerId> {
    let Some(origin) = net.peers.get(&speaker).map(|p| p.position) else {
        return Vec::new();
    };
    net.peers
        .values()
        .filter(|p| p.id != speaker && !net.is_phantom(p.id) && !p.dead)
        .filter(|p| within_voice_range(origin, p.position))
        .map(|p| p.id)
        .collect()
}

/// ADR-015: host-as-server relay of per-peer POSE (rotation + animation included).
/// The roster relay (`broadcast_peer_roster` / `PeerList`) carries only POSITION, so a
/// joiner — which only connects to the host — never learns the rotation or animation of
/// the OTHER joiners (their pickup gesture, ADR-011, and facing). Here the host re-emits
/// each peer's `PlayerUpdate` (pos/rot/anim from its `PeerConnection`) to every OTHER
/// peer, stamped with that peer's id via `send_unreliable_as`, reusing the exact
/// PlayerUpdate receive path. Host-only and a no-op below two peers (a joiner's peer set
/// is just {host}, nothing to relay; with one joiner there is no second joiner to inform).
/// Sent at the player-update cadence (`NET_BROADCAST_EVERY`, 10 Hz).
pub async fn broadcast_peer_poses(net: &NetworkManager) {
    if net.peers.len() < 2 {
        return;
    }
    // Snapshot ids + poses up front so we don't hold a borrow of net.peers across the
    // awaits below (and so a peer is never echoed its own pose).
    //
    // ADR-043: DESTINATIONS exclude phantoms; SOURCES do not. A phantom is a sender, never a
    // receiver — its `addr` is the inert `127.0.0.1:1` stamped at injection, so every datagram
    // aimed at one was a real syscall to a dead loopback port (and on Windows, an ICMP
    // port-unreachable that comes back as WSAECONNRESET on the socket). With a populated world
    // that was the dominant cost of the relay: at P total peers of which N are phantoms, the
    // wasted fraction is N×(P−1) datagrams per call at 10 Hz. Same packets, same senders, fewer
    // destinations — no real peer can observe the difference.
    let dest_ids = relay_destinations(net);
    if dest_ids.is_empty() {
        return; // only phantoms present: nobody to inform
    }
    let poses: Vec<(PeerId, PacketPayload)> = net
        .peers
        .values()
        .map(|p| {
            (
                p.id,
                PacketPayload::PlayerUpdate {
                    position: p.position,
                    rotation: p.rotation,
                    animation: p.animation.clone(),
                    crouch: p.crouch,
                    pitch: p.pitch,
                    equipment: p.equipment,
                    held_item: p.held_item,
                    hit_seq: p.hit_seq,
                    dead: p.dead,
                    revealed: p.revealed,
                    vocal_seq: p.vocal_seq,
                    vocal_kind: p.vocal_kind,
                    light_on: p.light_on,
                    fire_seq: p.fire_seq,
                    buttons: p.buttons,
                    melee_seq: p.melee_seq,
                    carry_def: p.carry_def,
                    carry_count: p.carry_count,
                },
            )
        })
        .collect();

    for (src_id, payload) in &poses {
        for &dest_id in &dest_ids {
            if dest_id == *src_id {
                continue; // never echo a peer its own pose
            }
            net.send_unreliable_as(*src_id, dest_id, payload).await;
        }
    }

    // ADR-015 traffic gate instrumentation: throttled (~1/s, no mutable state) report of
    // the relay's datagram rate so the host log can be measured in play-test. Since ADR-043
    // the cost is P×D (minus the self-echoes), where P counts every pose relayed and D only
    // the REAL destinations — both are logged because their ratio is the phantom overhead.
    if net.session_start.elapsed().as_millis() % 1000 < 120 {
        let p = poses.len();
        let d = dest_ids.len();
        let per_call = p * d - d.min(p); // each real destination skips its own pose
        info!(
            "MPTRACE step=R15 event=peer_pose_relay self_id={} peer_count={} real_dest_count={} relay_datagrams_per_call={} approx_per_sec={}",
            net.local_id,
            p,
            d,
            per_call,
            per_call * 10
        );
    }
}

/// Host-as-server relay of the STP item roster: the host broadcasts its full
/// authoritative item list so every joiner spawns the same STP items (Phase 1).
pub async fn broadcast_stp_items(net: &NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    let payload = PacketPayload::StpItemList {
        items: net.stp_items.clone(),
    };
    net.broadcast_unreliable(&payload).await;
}

/// ADR-028 Fase E: host-as-server relay of the corpse roster — the host broadcasts its
/// full authoritative `world.corpses` so every joiner mirrors the same lootable corpses
/// (their own build_world_state then filters by THEIR player's proximity). Full-roster
/// UDP at 10 Hz = self-healing, same pattern as broadcast_stp_items.
pub async fn broadcast_corpses(net: &NetworkManager, world: &World) {
    if net.peers.is_empty() {
        return;
    }
    let payload = PacketPayload::CorpseList {
        corpses: world.corpses.values().cloned().collect(),
    };
    net.broadcast_unreliable(&payload).await;
}

/// Host-as-server relay of the STP building roster: the host broadcasts its full
/// authoritative building list so every joiner spawns the same pieces (Phase B1).
pub async fn broadcast_stp_buildings(net: &NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    let payload = PacketPayload::StpBuildingList {
        buildings: net.stp_buildings.clone(),
    };
    net.broadcast_unreliable(&payload).await;
}

/// Host-as-server relay of the STP carryable roster: the host broadcasts its full
/// authoritative carryable list so every joiner spawns the same world carryables (B2.5).
pub async fn broadcast_stp_carryables(net: &NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    let payload = PacketPayload::StpCarryableList {
        carryables: net.stp_carryables.clone(),
    };
    net.broadcast_unreliable(&payload).await;
}

/// Host-as-server relay of the STP harvestable health roster: the host broadcasts its full
/// authoritative harvestable list so every joiner reflects the same tree/rock health (B2.6).
pub async fn broadcast_stp_harvestables(net: &NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    let payload = PacketPayload::StpHarvestableList {
        harvestables: net.stp_harvestables.clone(),
    };
    net.broadcast_unreliable(&payload).await;
}

/// Send nearby chunk states to all peers (for chunks the local player owns).
///
/// Host-only. `chunk.owner` is stamped with the RECEIVER's own local_id on every apply
/// path (`update_ownership`, `apply_chunk_sync`), so before this guard a joiner calling
/// this too made every overlapping backend reclaim the other's chunks every 200ms —
/// last-writer-wins with no arbiter, ping-ponging `owner` and re-seeding entities/items
/// on both sides.
pub async fn broadcast_chunk_states(net: &NetworkManager, world: &World, player_pos: Vec3) {
    if !net.is_host || net.peers.is_empty() {
        return;
    }
    let player_chunk = world_to_chunk(player_pos);
    for chunk in world.chunks.values() {
        if chunk.owner != Some(net.local_id) {
            continue;
        }
        // Only broadcast chunks near the player (within 3 chunks).
        let dx = (chunk.pos.0 - player_chunk.0).abs();
        let dz = (chunk.pos.1 - player_chunk.1).abs();
        if dx > 3 || dz > 3 {
            continue;
        }
        let data = chunk_to_sync_data(chunk);
        let payload = PacketPayload::ChunkState { data };
        net.broadcast_unreliable(&payload).await;
    }
}

/// Send full world sync to a specific peer (on join).
pub async fn send_world_sync(
    net: &mut NetworkManager,
    peer_id: PeerId,
    world: &World,
    player: &Player,
) {
    let chunks: Vec<ChunkSyncData> = world.chunks.values().map(chunk_to_sync_data).collect();
    let entity_count: usize = chunks.iter().map(|c| c.entities.len()).sum();
    let item_count: usize = chunks.iter().map(|c| c.items.len()).sum();

    info!(
        "MPTRACE step=W event=host_world_snapshot_created self_id={} seed={} revision={} chunks={} entities={} items={}",
        net.local_id,
        world.seed,
        world.revision,
        chunks.len(),
        entity_count,
        item_count
    );
    info!(
        "MPTRACE step=X event=send_world_snapshot self_id={} peer_id={} revision={} chunks={} entities={} items={}",
        net.local_id,
        peer_id,
        world.revision,
        chunks.len(),
        entity_count,
        item_count
    );

    let payload = PacketPayload::WorldSync {
        world_seed: world.seed,
        world_revision: world.revision,
        chunks,
    };
    net.send_reliable(peer_id, &payload).await;

    let peer_list_payload = PacketPayload::PeerList {
        peers: build_peer_list(net, player),
    };
    net.broadcast_unreliable(&peer_list_payload).await;
}

pub async fn broadcast_world_sync(net: &mut NetworkManager, world: &World, player: &Player) {
    // ADR-016: skip phantoms — WorldSync is reliable and their addr is inert, so a copy
    // would never be ACKed and just accumulate retransmits.
    let peer_ids: Vec<PeerId> = net
        .peers
        .keys()
        .copied()
        .filter(|id| !net.is_phantom(*id))
        .collect();
    for peer_id in peer_ids {
        send_world_sync(net, peer_id, world, player).await;
    }
}

/// Send a chunk transfer to a specific peer (ownership handoff).
pub async fn send_chunk_transfer(net: &mut NetworkManager, peer_id: PeerId, chunk: &Chunk) {
    let data = chunk_to_sync_data(chunk);
    let payload = PacketPayload::ChunkTransfer { data };
    net.send_reliable(peer_id, &payload).await;
}

/// Broadcast chunk teleport to all peers.
pub async fn broadcast_chunk_teleport(
    net: &NetworkManager,
    old_pos: [i32; 2],
    new_pos: [i32; 2],
    new_seed: u64,
) {
    if net.peers.is_empty() {
        return;
    }
    let payload = PacketPayload::ChunkTeleport {
        old_pos,
        new_pos,
        new_seed,
    };
    net.broadcast_unreliable(&payload).await;
}

/// ADR-056: say goodbye before this process exits, so peers act on the departure NOW instead
/// of waiting out the 5 s heartbeat timeout (`peer::HEARTBEAT_TIMEOUT`). No new packet type —
/// `PacketPayload::Disconnect` and its receiver (`handlers.rs`, which purges peer state and
/// raises `PeerDisconnected`) have existed since the baseline; only the "session full" rejection
/// ever sent one. Nothing on the wire changes shape, so there is no schema bump on the P2P side.
///
/// Sent raw rather than with `send_reliable`, matching the rejection path: the caller exits
/// immediately afterwards, so nothing would ever process an ACK or a retransmit — queueing it as
/// reliable would just drop it in a queue that dies with the process. A lost goodbye therefore
/// degrades to exactly today's behavior (the peer notices on heartbeat timeout), which is what
/// keeps this safe to send unreliably.
///
/// Not gated on `is_host`: a joiner leaving cleanly is worth announcing too, and the host has
/// handled inbound `Disconnect` since the baseline.
pub async fn broadcast_goodbye(net: &NetworkManager, reason: &str) {
    let payload = PacketPayload::Disconnect {
        reason: reason.into(),
    };
    let header = PacketHeader::new(payload.type_code(), net.local_id, 0, net.timestamp());
    let data = encode_packet(&header, &payload);
    for (_, addr) in net.broadcast_destinations() {
        net.send_datagram(&data, addr, "goodbye").await;
    }
}

/// Broadcast anchor placement to all peers (reliable — replicated critical data).
pub async fn broadcast_anchor(
    net: &mut NetworkManager,
    chunk_pos: [i32; 2],
    durability: f32,
    installed_by: &str,
) {
    let payload = PacketPayload::AnchorBroadcast {
        chunk_pos,
        durability,
        installed_by: installed_by.into(),
    };
    net.broadcast_reliable(&payload).await;
}

/// Broadcast stabilizer placement to all peers (reliable).
pub async fn broadcast_stabilizer(
    net: &mut NetworkManager,
    chunk_pos: [i32; 2],
    tier: u8,
    remaining_hours: f32,
) {
    let payload = PacketPayload::StabilizerBroadcast {
        chunk_pos,
        tier,
        remaining_hours,
    };
    net.broadcast_reliable(&payload).await;
}

/// Build AnchorInfo list for handshake (placeholder — no anchor persistence yet).
pub fn build_anchor_list(_world: &World) -> Vec<AnchorInfo> {
    Vec::new()
}

/// Build StabilizerInfo list for handshake (placeholder).
pub fn build_stabilizer_list(_world: &World) -> Vec<StabilizerInfo> {
    Vec::new()
}

#[cfg(test)]
mod voice_tests {
    use super::*;
    use crate::network::peer::PeerConnection;

    async fn host_with_peers(peers: &[(PeerId, [f32; 3], bool)]) -> NetworkManager {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        for (id, pos, dead) in peers {
            let addr = (std::net::Ipv4Addr::LOCALHOST, 40000 + *id).into();
            let mut conn = PeerConnection::new(*id, format!("P{id}"), addr);
            conn.update_player_state(*pos, 0.0, "idle".into());
            conn.dead = *dead;
            net.peers.insert(*id, conn);
        }
        net
    }

    #[tokio::test]
    async fn voice_reaches_the_near_peer_and_not_the_far_one() {
        // 10 m away hears; 200 m away does not. Without this the relay is a broadcast wearing a
        // proximity label, and someone across the level decodes your conversation.
        let net = host_with_peers(&[
            (2, [0.0, 1.8, 0.0], false),
            (3, [10.0, 1.8, 0.0], false),
            (4, [200.0, 1.8, 0.0], false),
        ])
        .await;

        let dests = voice_destinations(&net, 2);
        assert!(dests.contains(&3), "un peer a 10 m tiene que oir");
        assert!(!dests.contains(&4), "un peer a 200 m NO puede oir");
        assert!(!dests.contains(&2), "a nadie se le reenvia su propia voz");
    }

    #[tokio::test]
    async fn the_margin_is_hysteresis_and_the_cut_is_where_it_says() {
        // Justo dentro del margen: SÍ se relaya (el cliente lo atenuará hasta el silencio).
        // Un metro más allá del margen: no. Fija los dos lados del borde, no solo el cómodo.
        let inside = VOICE_RADIUS_M + VOICE_RELAY_MARGIN_M - 0.5;
        let outside = VOICE_RADIUS_M + VOICE_RELAY_MARGIN_M + 1.0;
        let net = host_with_peers(&[
            (2, [0.0, 1.8, 0.0], false),
            (3, [inside, 1.8, 0.0], false),
            (4, [outside, 1.8, 0.0], false),
        ])
        .await;

        let dests = voice_destinations(&net, 2);
        assert!(dests.contains(&3), "dentro del margen se sigue relayando");
        assert!(!dests.contains(&4), "pasado el margen se corta");
    }

    #[tokio::test]
    async fn distance_is_measured_in_3d_so_the_layer_below_cannot_hear() {
        // Las capas están apiladas en Y (4 m por capa). Un filtro por XZ metería en el mismo
        // canal de voz a alguien que está literalmente bajo tus pies y no puede ni verte.
        let net = host_with_peers(&[
            (2, [0.0, 1.8, 0.0], false),
            (3, [1.0, 1.8, 0.0], false),
            (4, [1.0, 1.8 + 40.0, 0.0], false),
        ])
        .await;

        let dests = voice_destinations(&net, 2);
        assert!(dests.contains(&3), "mismo plano, a 1 m: oye");
        assert!(
            !dests.contains(&4),
            "a 40 m EN VERTICAL no oye — con filtro XZ estaria a 1 m"
        );
    }

    #[tokio::test]
    async fn the_dead_do_not_listen_and_a_phantom_is_never_a_destination() {
        let mut net = host_with_peers(&[
            (2, [0.0, 1.8, 0.0], false),
            (3, [5.0, 1.8, 0.0], true), // muerto, y a tiro de piedra
        ])
        .await;
        // Un fantasma pegado al hablante: su addr es el 127.0.0.1:1 inerte de ADR-043.
        let phantom_id = net.spawn_phantom("Victima", [2.0, 1.8, 0.0]);

        let dests = voice_destinations(&net, 2);
        assert!(!dests.contains(&3), "un muerto no oye a los vivos");
        assert!(
            !dests.contains(&phantom_id),
            "un fantasma nunca es destino: su addr es un puerto muerto de loopback"
        );
    }

    #[tokio::test]
    async fn an_unknown_speaker_relays_to_nobody() {
        // Sin posición del hablante no hay forma de decidir quién está cerca. Contestar "todos"
        // seria justo el fallo abierto que este filtro existe para impedir.
        let net = host_with_peers(&[(2, [0.0, 1.8, 0.0], false)]).await;
        assert!(voice_destinations(&net, 9999).is_empty());
    }
}

#[cfg(test)]
mod chunk_broadcast_tests {
    use super::*;
    use crate::network::peer::PeerConnection;
    use crate::network::NetworkEvent;
    use std::net::SocketAddr;
    use std::time::Duration;

    fn loopback_addr(net: &NetworkManager) -> SocketAddr {
        let mut addr = net.local_addr();
        addr.set_ip(std::net::Ipv4Addr::LOCALHOST.into());
        addr
    }

    // P0-1: a non-host must never broadcast chunk states — see the doc-comment on
    // `broadcast_chunk_states`. Wired as a positive+negative pair over real sockets so a
    // regression that silences the function entirely (e.g. an early return that always
    // fires) cannot pass by accident.
    #[tokio::test]
    async fn only_the_host_broadcasts_chunk_states() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let host_addr = loopback_addr(&host);
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let joiner_addr = loopback_addr(&joiner);

        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));
        joiner
            .peers
            .insert(1, PeerConnection::new(1, "Host".into(), host_addr));

        let pos = Vec3::new(0.0, 1.8, 0.0);

        let mut joiner_world = World::new(42);
        joiner_world.update_ownership(pos, joiner.local_id);
        assert!(
            joiner_world
                .chunks
                .values()
                .any(|c| c.owner == Some(joiner.local_id)),
            "setup bug: the joiner needs an owned chunk in range or this test is vacuous"
        );

        broadcast_chunk_states(&joiner, &joiner_world, pos).await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        let host_events = host.process_incoming().await;
        assert!(
            !host_events
                .iter()
                .any(|e| matches!(e, NetworkEvent::ChunkTransferReceived { .. })),
            "a non-host must never emit ChunkState, got: {host_events:?}"
        );

        // Positive control: same call, same range, only `is_host` differs — proves the
        // silence above is the guard firing, not an unrelated setup mistake.
        let mut host_world = World::new(42);
        host_world.update_ownership(pos, host.local_id);
        broadcast_chunk_states(&host, &host_world, pos).await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        let joiner_events = joiner.process_incoming().await;
        assert!(
            joiner_events
                .iter()
                .any(|e| matches!(e, NetworkEvent::ChunkTransferReceived { .. })),
            "positive control failed: the host should still broadcast, got: {joiner_events:?}"
        );
    }
}
