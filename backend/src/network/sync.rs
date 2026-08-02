//! State synchronization: broadcast functions that convert game state to protocol
//! payloads and send them via the NetworkManager.
//! See ARCHITECTURE_V1.md §5.4 and §3.2.

use crate::player::session::Player;
use crate::utils::{world_to_chunk, Vec3};
use crate::world::chunk::{Chunk, ChunkState};
use crate::world::World;

use log::info;

use super::protocol::{
    AnchorInfo, ChunkSyncData, EntitySyncData, ItemSyncData, PacketPayload, PeerInfo,
    SessionConfig, StabilizerInfo,
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
        // ADR-042: both client-reported and sealed in the game loop next to `hit_seq`.
        light_on: player.light_on,
        fire_seq: player.fire_seq,
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
                    light_on: p.light_on,
                    fire_seq: p.fire_seq,
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
pub async fn broadcast_chunk_states(net: &NetworkManager, world: &World, player_pos: Vec3) {
    if net.peers.is_empty() {
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
