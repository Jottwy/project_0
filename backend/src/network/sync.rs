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
        .map_or(false, |t| t.elapsed().as_millis() < 1000)
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
    };
    info!(
        "Sending player update to peers={} local_id={} pos=({:.2}, {:.2}, {:.2})",
        net.peers.len(),
        net.local_id,
        player.position.x,
        player.position.y,
        player.position.z
    );
    if net.session_start.elapsed().as_millis() % 1000 < 120 {
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
    let peer_ids: Vec<PeerId> = net.peers.keys().copied().collect();
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
                },
            )
        })
        .collect();

    for (src_id, payload) in &poses {
        for &dest_id in &peer_ids {
            if dest_id == *src_id {
                continue; // never echo a peer its own pose
            }
            net.send_unreliable_as(*src_id, dest_id, payload).await;
        }
    }

    // ADR-015 traffic gate instrumentation: throttled (~1/s, no mutable state) report of
    // the relay's datagram rate so the host log can be measured in play-test. Datagrams
    // per call = P×(P−1); per second ≈ ×10 (NET_BROADCAST_EVERY = 10 Hz).
    if net.session_start.elapsed().as_millis() % 1000 < 120 {
        let p = peer_ids.len();
        let per_call = p * p.saturating_sub(1);
        info!(
            "MPTRACE step=R15 event=peer_pose_relay self_id={} peer_count={} relay_datagrams_per_call={} approx_per_sec={}",
            net.local_id,
            p,
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
    for (_key, chunk) in &world.chunks {
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
