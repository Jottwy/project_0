//! State synchronization: broadcast functions that convert game state to protocol
//! payloads and send them via the NetworkManager.
//! See ARCHITECTURE_V1.md Â§5.4 and Â§3.2.
//!
//! SIX FUNCTIONS HERE HAVE NO CALL SITES, and they are unfinished features rather than cruft â€”
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
use super::roster;
use super::NetworkManager;
use super::PeerId;

// â”€â”€â”€ Conversion: game types â†’ sync types â”€â”€â”€

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

/// ADR-043 gap (auditoría 2026-08-10, playtest H10): antes de este filtro, esta era la ÚNICA
/// función del archivo que ponía un peer en el WIRE sin excluir fantasmas — `broadcast_player_update`,
/// `voice_destinations` y `broadcast_world_sync` ya lo hacían. Un fantasma aquí no es solo un
/// destino de más: su `PeerInfo` (id, nombre, la dirección INERTE `127.0.0.1:1`) viaja dentro del
/// `PeerList` hacia un peer real, que la adopta sin saber que es un fantasma (esa marca vive solo
/// en `phantom_ids`, local a quien lo inyectó, y no cruza el wire) — ver `PacketPayload::PeerList`
/// en `handlers.rs`, que inserta un `PeerConnection` real para cada entrada. Ese peer real acaba
/// con un "peer" fantasma en su propio `net.peers` que SU `is_phantom` no reconoce, y sus propios
/// broadcasts (ya filtrados por `broadcast_destinations`) lo tratarían como real.
pub fn build_peer_list(net: &NetworkManager, local_player: &Player) -> Vec<PeerInfo> {
    let mut peers = vec![PeerInfo {
        id: net.local_id,
        name: local_player.name.clone(),
        addr: net.local_addr().to_string(),
        position: local_player.position.to_array(),
    }];
    for peer in net.peers.values() {
        if net.is_phantom(peer.id) {
            continue;
        }
        peers.push(PeerInfo {
            id: peer.id,
            name: peer.name.clone(),
            addr: peer.addr.to_string(),
            position: peer.position,
        });
    }
    peers
}

#[cfg(test)]
mod peer_list_tests {
    use super::*;
    use crate::network::peer::PeerConnection;

    /// ADR-043 gap cerrado (auditoría 2026-08-10, playtest H10): sin el filtro, un fantasma
    /// aparecía en el `PeerList` con su dirección inerte `127.0.0.1:1` como si fuera un peer
    /// real, y quien lo recibiera lo adoptaba sin saber que era un fantasma.
    #[tokio::test]
    async fn build_peer_list_excludes_phantoms() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let real_addr: std::net::SocketAddr = "127.0.0.1:9800".parse().unwrap();
        host.peers
            .insert(2, PeerConnection::new(2, "Real".into(), real_addr));
        let phantom_id = host.spawn_phantom("Skinwalker", [0.0, 1.8, 0.0]);

        let player = Player::new(host.local_id, "Host");
        let list = build_peer_list(&host, &player);

        assert!(
            list.iter().any(|p| p.id == 2),
            "un peer real SI debe aparecer en el roster"
        );
        assert!(
            !list.iter().any(|p| p.id == phantom_id),
            "un fantasma no puede aparecer en el PeerList — su addr inerte cruzaria el wire"
        );
    }
}

// â”€â”€â”€ Broadcast functions â”€â”€â”€

/// Broadcast local player position/rotation to all peers (unreliable, 10hz).
pub async fn broadcast_player_update(net: &NetworkManager, player: &Player) {
    if net.peers.is_empty() {
        return;
    }
    // ADR-011: a recent local pickup takes priority â€” a trigger flank held ~1s so the client
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
        // ADR-028 post-E3: SERVER-derived (authoritative stats, ADR-025) â€” the one pose field
        // the client does not report.
        dead: player.stats.is_dead(),
        // ADR-038: always false here â€” a real player never shows a "real form". The only
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
    // unthrottled it was the single noisiest line in the backend log â€” it formatted three floats
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

/// ADR-043 â€” peers a relayed pose may legitimately be ADDRESSED to: every real peer, never a
/// phantom. Split out of `broadcast_peer_poses` so the invariant is testable without a socket:
/// the alternative (asserting on datagrams) would need a live UDP endpoint per phantom, which is
/// exactly the thing that does not exist â€” a phantom's `addr` is the inert `127.0.0.1:1` stamped
/// at injection (`NetworkManager::spawn_phantom`).
pub(crate) fn relay_destinations(net: &NetworkManager) -> Vec<PeerId> {
    net.peers
        .keys()
        .copied()
        .filter(|id| !net.is_phantom(*id))
        .collect()
}

/// ADR-046 â€” how far a voice carries, in metres. Sits between the two peer sounds that already
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

/// ADR-046 â€” who gets a copy of `speaker`'s voice, decided by the HOST.
///
/// Three exclusions, each for its own reason:
/// * the speaker (nobody is relayed their own voice, same rule as the pose relay),
/// * phantoms, whose `addr` is the inert `127.0.0.1:1` â€” the destination filter ADR-043 added,
/// * peers out of earshot. THAT one is not an optimisation: it is the only thing stopping a
///   modified client from decoding a conversation happening across the level. A filter that
///   lived only in the listener would be a filter the listener can remove.
///
/// A DEAD peer is excluded too (ADR-046: the dead neither speak nor listen). The client stops
/// capturing on death as well, but that half only saves bandwidth â€” this half is the authority,
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
/// joiner â€” which only connects to the host â€” never learns the rotation or animation of
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
    // receiver â€” its `addr` is the inert `127.0.0.1:1` stamped at injection, so every datagram
    // aimed at one was a real syscall to a dead loopback port (and on Windows, an ICMP
    // port-unreachable that comes back as WSAECONNRESET on the socket). With a populated world
    // that was the dominant cost of the relay: at P total peers of which N are phantoms, the
    // wasted fraction is NÃ—(Pâˆ’1) datagrams per call at 10 Hz. Same packets, same senders, fewer
    // destinations â€” no real peer can observe the difference.
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
        // F0.2: encodear UNA vez por origen en vez de una vez por par (origen, destino). Los
        // bytes no dependen del destino —el header lleva el id del origen y la secuencia de un
        // no-fiable es 0—, así que esto emite exactamente los mismos datagramas: P
        // serializaciones por ronda en vez de P×D.
        let data = net.encode_relay_as(*src_id, payload);
        for &dest_id in &dest_ids {
            if dest_id == *src_id {
                continue; // never echo a peer its own pose
            }
            net.send_prepared_unreliable(dest_id, &data).await;
        }
    }

    // ADR-015 traffic gate instrumentation: throttled (~1/s, no mutable state) report of
    // the relay's datagram rate so the host log can be measured in play-test. Since ADR-043
    // the cost is PÃ—D (minus the self-echoes), where P counts every pose relayed and D only
    // the REAL destinations â€” both are logged because their ratio is the phantom overhead.
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
///
/// ADR-060 (d): paginado. Ver `roster::paginate` para por qué el troceo va por bytes reales y
/// `roster::RosterAssembler` para el reensamblado; la generación es el `timestamp()` de esta
/// ronda, resuelto UNA vez para que todas las páginas la compartan.
/// ADR-071: ask one roster's gate whether this round goes out. Factored so the five broadcasts
/// share the rule instead of each carrying its own copy of it — the five differ only in which
/// roster and which gate, and a rule copied five times is a rule that drifts in four of them.
fn roster_gate_open<T: serde::Serialize>(
    gate: &mut roster::RosterGate,
    items: &[T],
    peers: usize,
) -> bool {
    gate.should_send(
        roster::content_hash(items),
        peers,
        std::time::Instant::now(),
        roster::ROSTER_HEARTBEAT,
    )
}

pub async fn broadcast_stp_items(net: &mut NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    // ADR-071: skip the whole round if this roster is byte-identical to the last one that went
    // out. The gate still gets asked at 10 Hz, so the first round AFTER a change ships it exactly
    // as before — this costs no propagation latency, it only stops re-sending what everyone has.
    if !roster_gate_open(&mut net.roster_gates.items, &net.stp_items, net.peers.len()) {
        return;
    }
    let generation = net.timestamp();
    let pages = roster::paginate(&net.stp_items, roster::ROSTER_PAGE_BUDGET_BYTES);
    let page_count = pages.len() as u16;
    for (index, items) in pages.into_iter().enumerate() {
        let payload = PacketPayload::StpItemList {
            items,
            generation,
            page: index as u16,
            page_count,
        };
        net.broadcast_unreliable(&payload).await;
        // ADR-060 (d): ceder entre páginas. Sin esto la ronda entera sale como una ráfaga
        // ininterrumpida y desborda el buffer de recepción del socket del receptor (~64 KB por
        // defecto): MEDIDO, a partir de ~56 páginas empezaba a perderse al menos una por ronda,
        // y con reensamblado todo-o-nada eso significa que el roster no converge NUNCA.
        tokio::task::yield_now().await;
    }
}

/// ADR-028 Fase E: host-as-server relay of the corpse roster â€” the host broadcasts its
/// full authoritative `world.corpses` so every joiner mirrors the same lootable corpses
/// (their own build_world_state then filters by THEIR player's proximity). Full-roster
/// UDP at 10 Hz = self-healing, same pattern as broadcast_stp_items.
pub async fn broadcast_corpses(net: &mut NetworkManager, world: &World) {
    if net.peers.is_empty() {
        return;
    }
    let all: Vec<_> = world.corpses.values().cloned().collect();
    // ADR-071. Unlike the other four this one still pays the clone above before the gate can look
    // at it: the roster is assembled from `world.corpses` rather than stored flat. The clone is
    // orders of magnitude cheaper than the send it prevents, so it is not worth restructuring the
    // storage to save it.
    if !roster_gate_open(&mut net.roster_gates.corpses, &all, net.peers.len()) {
        return;
    }
    let generation = net.timestamp();
    let pages = roster::paginate(&all, roster::ROSTER_PAGE_BUDGET_BYTES);
    let page_count = pages.len() as u16;
    for (index, corpses) in pages.into_iter().enumerate() {
        let payload = PacketPayload::CorpseList {
            corpses,
            generation,
            page: index as u16,
            page_count,
        };
        net.broadcast_unreliable(&payload).await;
        // ADR-060 (d): ceder entre páginas. Sin esto la ronda entera sale como una ráfaga
        // ininterrumpida y desborda el buffer de recepción del socket del receptor (~64 KB por
        // defecto): MEDIDO, a partir de ~56 páginas empezaba a perderse al menos una por ronda,
        // y con reensamblado todo-o-nada eso significa que el roster no converge NUNCA.
        tokio::task::yield_now().await;
    }
}

/// Host-as-server relay of the STP building roster: the host broadcasts its full
/// authoritative building list so every joiner spawns the same pieces (Phase B1).
pub async fn broadcast_stp_buildings(net: &mut NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    // ADR-071. This is the roster the measurement singled out: a built base is static for hours and
    // was being re-sent 10 times a second forever.
    if !roster_gate_open(
        &mut net.roster_gates.buildings,
        &net.stp_buildings,
        net.peers.len(),
    ) {
        return;
    }
    let generation = net.timestamp();
    let pages = roster::paginate(&net.stp_buildings, roster::ROSTER_PAGE_BUDGET_BYTES);
    let page_count = pages.len() as u16;
    for (index, buildings) in pages.into_iter().enumerate() {
        let payload = PacketPayload::StpBuildingList {
            buildings,
            generation,
            page: index as u16,
            page_count,
        };
        net.broadcast_unreliable(&payload).await;
        // ADR-060 (d): ceder entre páginas. Sin esto la ronda entera sale como una ráfaga
        // ininterrumpida y desborda el buffer de recepción del socket del receptor (~64 KB por
        // defecto): MEDIDO, a partir de ~56 páginas empezaba a perderse al menos una por ronda,
        // y con reensamblado todo-o-nada eso significa que el roster no converge NUNCA.
        tokio::task::yield_now().await;
    }
}

/// Host-as-server relay of the STP carryable roster: the host broadcasts its full
/// authoritative carryable list so every joiner spawns the same world carryables (B2.5).
pub async fn broadcast_stp_carryables(net: &mut NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    // ADR-071.
    if !roster_gate_open(
        &mut net.roster_gates.carryables,
        &net.stp_carryables,
        net.peers.len(),
    ) {
        return;
    }
    let generation = net.timestamp();
    let pages = roster::paginate(&net.stp_carryables, roster::ROSTER_PAGE_BUDGET_BYTES);
    let page_count = pages.len() as u16;
    for (index, carryables) in pages.into_iter().enumerate() {
        let payload = PacketPayload::StpCarryableList {
            carryables,
            generation,
            page: index as u16,
            page_count,
        };
        net.broadcast_unreliable(&payload).await;
        // ADR-060 (d): ceder entre páginas. Sin esto la ronda entera sale como una ráfaga
        // ininterrumpida y desborda el buffer de recepción del socket del receptor (~64 KB por
        // defecto): MEDIDO, a partir de ~56 páginas empezaba a perderse al menos una por ronda,
        // y con reensamblado todo-o-nada eso significa que el roster no converge NUNCA.
        tokio::task::yield_now().await;
    }
}

/// Host-as-server relay of the STP harvestable health roster: the host broadcasts its full
/// authoritative harvestable list so every joiner reflects the same tree/rock health (B2.6).
pub async fn broadcast_stp_harvestables(net: &mut NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    // ADR-071.
    if !roster_gate_open(
        &mut net.roster_gates.harvestables,
        &net.stp_harvestables,
        net.peers.len(),
    ) {
        return;
    }
    let generation = net.timestamp();
    let pages = roster::paginate(&net.stp_harvestables, roster::ROSTER_PAGE_BUDGET_BYTES);
    let page_count = pages.len() as u16;
    for (index, harvestables) in pages.into_iter().enumerate() {
        let payload = PacketPayload::StpHarvestableList {
            harvestables,
            generation,
            page: index as u16,
            page_count,
        };
        net.broadcast_unreliable(&payload).await;
        // ADR-060 (d): ceder entre páginas. Sin esto la ronda entera sale como una ráfaga
        // ininterrumpida y desborda el buffer de recepción del socket del receptor (~64 KB por
        // defecto): MEDIDO, a partir de ~56 páginas empezaba a perderse al menos una por ronda,
        // y con reensamblado todo-o-nada eso significa que el roster no converge NUNCA.
        tokio::task::yield_now().await;
    }
}

/// Send nearby chunk states to all peers (for chunks the local player owns).
///
/// Host-only. `chunk.owner` is stamped with the RECEIVER's own local_id on every apply
/// path (`update_ownership`, `apply_chunk_sync`), so before this guard a joiner calling
/// this too made every overlapping backend reclaim the other's chunks every 200ms â€”
/// last-writer-wins with no arbiter, ping-ponging `owner` and re-seeding entities/items
/// on both sides.
/// F0.8 (enmienda ADR-073/074, 2026-08-14): `&mut` porque cada chunk lleva ahora su propio gate,
/// que se actualiza cuando decide que una ronda sale. Sigue corriendo a 5 Hz — lo que cambia es
/// que un chunk que nadie ha tocado desde la última ronda ya no se reenvía.
///
/// Es el mecanismo de ADR-071 aplicado al mayor emisor del host: medido con 8 peers, este relay
/// eran 3351 de los 4373 KB/s de subida (77 %), repitiendo layout, entidades e items de chunks
/// que en su inmensa mayoría llevaban horas idénticos. Igual que allí: cambia la CADENCIA, no el
/// formato — `apply_chunk_sync` sigue siendo un reemplazo verbatim idempotente, así que un peer
/// sin actualizar solo recibe menos rondas y no se entera de nada. Cero cambios de wire.
pub async fn broadcast_chunk_states(net: &mut NetworkManager, world: &World, player_pos: Vec3) {
    if !net.is_host || net.peers.is_empty() {
        return;
    }
    let player_chunk = world_to_chunk(player_pos);
    let peers = net.peers.len();
    // Las claves visitadas en ESTA ronda. Se recogen para poder tirar después los gates de chunks
    // que ya no se emiten (descargados o alejados): sin la poda el mapa crece con cada chunk que
    // el jugador visita y no vuelve a pisar, durante toda la sesión.
    let mut seen: Vec<(i32, i32, i8)> = Vec::with_capacity(net.chunk_gates.len().max(16));
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
        let key = (chunk.pos.0, chunk.pos.1, chunk.layer);
        seen.push(key);
        // El hash es del dato que VIAJA, no del `Chunk` de origen: así el gate no puede cortar por
        // un estado interno que el wire no transporta, ni dejar pasar un cambio que sí transporta.
        // Cuesta una serialización que el envío repite — la CPU está sobradísima (27 ms/s medidos
        // en el peor caso) y lo que este fix compra son bytes, que es el recurso escaso.
        let open = {
            let gate = net.chunk_gates.entry(key).or_default();
            gate.should_send(
                roster::content_hash(std::slice::from_ref(&data)),
                peers,
                std::time::Instant::now(),
                roster::ROSTER_HEARTBEAT,
            )
        };
        if !open {
            continue;
        }
        let payload = PacketPayload::ChunkState { data };
        net.broadcast_unreliable(&payload).await;
        // ADR-060 (d): ceder entre páginas. Sin esto la ronda entera sale como una ráfaga
        // ininterrumpida y desborda el buffer de recepción del socket del receptor (~64 KB por
        // defecto): MEDIDO, a partir de ~56 páginas empezaba a perderse al menos una por ronda,
        // y con reensamblado todo-o-nada eso significa que el roster no converge NUNCA.
        tokio::task::yield_now().await;
    }
    // Poda: solo cuando hay más gates que chunks emitidos, para no pagar el retain en la ronda
    // normal (que es la mayoría). Un chunk que vuelve al radio re-emite en su primera ronda con el
    // gate limpio — correcto: mientras estuvo fuera, el peer pudo perderse cualquier cambio.
    if net.chunk_gates.len() > seen.len() {
        net.chunk_gates.retain(|k, _| seen.contains(k));
    }
}

/// ADR-060: seguimiento receptor de la completitud del goteo de snapshot.
///
/// La capa reliable es at-least-once SIN orden: `End` puede llegar antes que chunks, y un chunk
/// puede llegar DUPLICADO (retransmisiÃ³n tras un ACK perdido). Por eso la completitud se cuenta
/// sobre el conjunto de claves (pos, layer) distintas aplicadas â€” nunca sobre paquetes â€” y por
/// revision: una revision mÃ¡s nueva desecha el estado de las anteriores (el goteo viejo queda
/// superseded, sus rezagados caen en `revision < self.revision` y se ignoran).
#[derive(Debug, Default)]
pub struct WorldSyncProgress {
    revision: u64,
    applied: std::collections::HashSet<(i32, i32, i8)>,
    expected: Option<u32>,
    complete: bool,
}

impl WorldSyncProgress {
    fn adopt(&mut self, revision: u64) {
        if revision > self.revision {
            self.revision = revision;
            self.applied.clear();
            self.expected = None;
            self.complete = false;
        }
    }

    pub fn note_chunk(&mut self, revision: u64, pos: [i32; 2], layer: i8) {
        self.adopt(revision);
        if revision == self.revision {
            self.applied.insert((pos[0], pos[1], layer));
            self.refresh();
        }
    }

    pub fn note_end(&mut self, revision: u64, chunk_count: u32) {
        self.adopt(revision);
        if revision == self.revision {
            self.expected = Some(chunk_count);
            self.refresh();
        }
    }

    /// El monolito 0x04 deprecado aplica el mundo entero de golpe: completo por construcciÃ³n.
    pub fn note_monolith(&mut self, revision: u64) {
        self.adopt(revision);
        if revision == self.revision {
            self.complete = true;
        }
    }

    fn refresh(&mut self) {
        if let Some(expected) = self.expected {
            if self.applied.len() as u32 >= expected {
                self.complete = true;
            }
        }
    }

    /// Una vez completo, completo se queda (dentro de la misma revision): el gate de spawn
    /// consulta esto y no debe reabrirse por un rezagado.
    pub fn is_complete(&self) -> bool {
        self.complete
    }
}

/// Send full world sync to a specific peer (on join) â€” ADR-060: como GOTEO, un datagrama por
/// chunk + un `WorldSyncEnd`, vÃ­a la cola diferida (`send_reliable_queued`). Cada datagrama
/// cabe en un MTU; el monolito anterior morÃ­a en `WSAEMSGSIZE` a ~50-80 chunks.
pub async fn send_world_sync(
    net: &mut NetworkManager,
    peer_id: PeerId,
    world: &World,
    player: &Player,
) {
    let chunk_count = world.chunks.len();
    let entity_count: usize = world.chunks.values().map(|c| c.entities.len()).sum();
    let item_count: usize = world.chunks.values().map(|c| c.items.len()).sum();

    // Un goteo nuevo supersede al anterior hacia este peer: lo aparcado y aÃºn no emitido es
    // trabajo muerto (la revision nueva re-envÃ­a el mundo entero). Lo ya en vuelo no se toca â€”
    // son upserts inofensivos y su End viejo nunca completarÃ¡.
    if let Some(peer) = net.peers.get_mut(&peer_id) {
        let dropped = peer.purge_deferred();
        if dropped > 0 {
            info!(
                "MPTRACE step=W event=world_drip_superseded self_id={} peer_id={} deferred_dropped={}",
                net.local_id, peer_id, dropped
            );
        }
    }

    info!(
        "MPTRACE step=W event=host_world_snapshot_created self_id={} seed={} revision={} chunks={} entities={} items={}",
        net.local_id, world.seed, world.revision, chunk_count, entity_count, item_count
    );
    info!(
        "MPTRACE step=X event=send_world_drip self_id={} peer_id={} revision={} chunks={} entities={} items={}",
        net.local_id, peer_id, world.revision, chunk_count, entity_count, item_count
    );

    for chunk in world.chunks.values() {
        let payload = PacketPayload::WorldSyncChunk {
            world_revision: world.revision,
            data: chunk_to_sync_data(chunk),
        };
        net.send_reliable_queued(peer_id, &payload).await;
    }
    let end = PacketPayload::WorldSyncEnd {
        world_revision: world.revision,
        chunk_count: chunk_count as u32,
    };
    net.send_reliable_queued(peer_id, &end).await;

    let peer_list_payload = PacketPayload::PeerList {
        peers: build_peer_list(net, player),
    };
    net.broadcast_unreliable(&peer_list_payload).await;
}

pub async fn broadcast_world_sync(net: &mut NetworkManager, world: &World, player: &Player) {
    // ADR-016: skip phantoms â€” WorldSync is reliable and their addr is inert, so a copy
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

/// F0.1 (enmienda ADR-073, E0): la ventana de coalescing de `maybe_flush_world_sync`. Medido en
/// `perf-baseline.md`: un goteo completo son 84,9 KB por peer; una ráfaga de 20 pickups sin
/// coalescer eran 20 goteos (13,6 MB a 8 peers). A 300 ms la amplificación cae ~95 % frente al
/// disparo por evento, y sigue por debajo del margen de reacción humana — un objeto recogido no
/// puede leerse como "item fantasma" que otro peer intenta coger a su vez.
///
/// La sonda de F0.0 confirmó además que la línea base la domina `broadcast_chunk_states` (77 %),
/// no este goteo (5,6 Mbps sostenido si se disparara 1/s, contra 35,8 Mbps totales): este fix
/// mata el PICO de una ráfaga de interacciones, no la línea base — dicho explícito en
/// `SCALING-ROADMAP.md`, no una promesa incumplida si el número total apenas se mueve.
pub const WORLD_SYNC_COALESCE_WINDOW: std::time::Duration = std::time::Duration::from_millis(300);

/// F0.1: marca el mundo como "cambiado desde el último goteo despachado". Sustituye a la llamada
/// directa a `broadcast_world_sync` en los dos sitios legacy (`game_loop.rs`, pickup y drop):
/// antes, CADA interacción disparaba el goteo del mundo entero a todos los peers; ahora solo
/// arma el flag, y `maybe_flush_world_sync` decide cuándo sale.
pub fn mark_world_sync_dirty(net: &mut NetworkManager) {
    net.world_sync_dirty = true;
}

/// Decisión pura de `maybe_flush_world_sync`, separada para poder probarla sin reloj real ni
/// red — mismo motivo que `RosterGate::should_send` recibe `now` explícito en vez de leerlo él
/// mismo. `last_sent: None` (nunca se despachó) siempre está listo: la primera marca dirty de la
/// sesión no espera a la ventana, igual que ADR-071 no hace esperar al heartbeat a la primera
/// ronda.
fn world_sync_ready(
    dirty: bool,
    last_sent: Option<std::time::Instant>,
    now: std::time::Instant,
    window: std::time::Duration,
) -> bool {
    dirty && last_sent.is_none_or(|t| now.duration_since(t) >= window)
}

/// F0.1: consume el flag como mucho una vez por `WORLD_SYNC_COALESCE_WINDOW`. Se llama en CADA
/// tick (no solo en los ticks de broadcast periódico) para que la latencia máxima tras vencer la
/// ventana sea de un tick (~16 ms a 60 Hz), no de hasta 100 ms si se enganchara al bloque de
/// `NET_BROADCAST_EVERY`.
pub async fn maybe_flush_world_sync(net: &mut NetworkManager, world: &World, player: &Player) {
    let now = std::time::Instant::now();
    if !world_sync_ready(
        net.world_sync_dirty,
        net.world_sync_last_sent,
        now,
        WORLD_SYNC_COALESCE_WINDOW,
    ) {
        return;
    }
    net.world_sync_dirty = false;
    net.world_sync_last_sent = Some(now);
    broadcast_world_sync(net, world, player).await;
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
/// of waiting out the 5 s heartbeat timeout (`peer::HEARTBEAT_TIMEOUT`). No new packet type â€”
/// `PacketPayload::Disconnect` and its receiver (`handlers.rs`, which purges peer state and
/// raises `PeerDisconnected`) have existed since the baseline; only the "session full" rejection
/// ever sent one. Nothing on the wire changes shape, so there is no schema bump on the P2P side.
///
/// Sent raw rather than with `send_reliable`, matching the rejection path: the caller exits
/// immediately afterwards, so nothing would ever process an ACK or a retransmit â€” queueing it as
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

/// Broadcast anchor placement to all peers (reliable â€” replicated critical data).
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

/// Build AnchorInfo list for handshake (placeholder â€” no anchor persistence yet).
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
        // Justo dentro del margen: SÃ se relaya (el cliente lo atenuarÃ¡ hasta el silencio).
        // Un metro mÃ¡s allÃ¡ del margen: no. Fija los dos lados del borde, no solo el cÃ³modo.
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
        // Las capas estÃ¡n apiladas en Y (4 m por capa). Un filtro por XZ meterÃ­a en el mismo
        // canal de voz a alguien que estÃ¡ literalmente bajo tus pies y no puede ni verte.
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
            "a 40 m EN VERTICAL no oye â€” con filtro XZ estaria a 1 m"
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
        // Sin posiciÃ³n del hablante no hay forma de decidir quiÃ©n estÃ¡ cerca. Contestar "todos"
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
    use crate::world::chunk::ChunkLayoutV1;
    use std::net::SocketAddr;
    use std::time::Duration;

    fn loopback_addr(net: &NetworkManager) -> SocketAddr {
        let mut addr = net.local_addr();
        addr.set_ip(std::net::Ipv4Addr::LOCALHOST.into());
        addr
    }

    // P0-1: a non-host must never broadcast chunk states â€” see the doc-comment on
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

        broadcast_chunk_states(&mut joiner, &joiner_world, pos).await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        let host_events = host.process_incoming().await;
        assert!(
            !host_events
                .iter()
                .any(|e| matches!(e, NetworkEvent::ChunkStateReceived { .. })),
            "a non-host must never emit ChunkState, got: {host_events:?}"
        );

        // Positive control: same call, same range, only `is_host` differs â€” proves the
        // silence above is the guard firing, not an unrelated setup mistake.
        let mut host_world = World::new(42);
        host_world.update_ownership(pos, host.local_id);
        broadcast_chunk_states(&mut host, &host_world, pos).await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        let joiner_events = joiner.process_incoming().await;
        assert!(
            joiner_events
                .iter()
                .any(|e| matches!(e, NetworkEvent::ChunkStateReceived { .. })),
            "positive control failed: the host should still broadcast, got: {joiner_events:?}"
        );
    }

    /// El broadcast periodico de chunks NO se confirma; el handoff de propiedad SI.
    ///
    /// MEDIDO antes de este arreglo, en sesion de 2 backends reales (40 s): el joiner emitia
    /// 8 267 `reliable_window_full` porque respondia un `ChunkTransferAck` FIABLE a cada uno de
    /// los ~820 `ChunkState`/s del host. Su ventana de 32 vivia llena, asi que sus propios envios
    /// fiables de gameplay (pickup, place, corpse, PvP) se descartaban en silencio contra el mismo
    /// `send_reliable`. El ack no lo lee nadie: su receptor solo hace `debug!`.
    ///
    /// El par positivo/negativo importa: sin el control positivo, borrar el ack ENTERO tambien
    /// pasaria este test, y el handoff perderia su confirmacion sin que nada avisara.
    #[tokio::test]
    async fn a_broadcast_chunk_is_not_acked_but_a_handoff_still_is() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let host_addr = loopback_addr(&host);
        let joiner_addr = loopback_addr(&joiner);
        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));
        joiner
            .peers
            .insert(1, PeerConnection::new(1, "Host".into(), host_addr));

        let pos = Vec3::new(0.0, 1.8, 0.0);
        let mut host_world = World::new(42);
        host_world.update_ownership(pos, host.local_id);
        let mut joiner_world = World::new(42);

        // NEGATIVO: broadcast periodico -> se aplica, no se confirma.
        broadcast_chunk_states(&mut host, &host_world, pos).await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        let mut applied = 0usize;
        for e in joiner.process_incoming().await {
            if let NetworkEvent::ChunkStateReceived { data, .. } = e {
                joiner_world.apply_chunk_transfer(&data, joiner.local_id);
                applied += 1;
            }
        }
        assert!(applied > 0, "setup: el broadcast tiene que llegar");
        assert_eq!(
            joiner.peers[&1].reliable_queue.len(),
            0,
            "un ChunkState NO puede encolar acks fiables: {applied} chunks llegaron y la ventana \
             del joiner tiene que seguir vacia"
        );

        // POSITIVO: handoff explicito -> sigue confirmandose.
        let chunk = host_world
            .chunks
            .values()
            .next()
            .expect("setup: el host necesita un chunk")
            .clone();
        send_chunk_transfer(&mut host, 2, &chunk).await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        for e in joiner.process_incoming().await {
            if let NetworkEvent::ChunkTransferReceived { from, data } = e {
                let ack = PacketPayload::ChunkTransferAck { pos: data.pos };
                joiner.send_reliable(from, &ack).await;
            }
        }
        assert_eq!(
            joiner.peers[&1].reliable_queue.len(),
            1,
            "el handoff de propiedad SI se confirma — quien cede la autoridad quiere saber que llego"
        );
    }

    // â”€â”€â”€ F0.3 (E0): los veredictos esperan en cola con cap, y el desborde es fatal â”€â”€â”€

    fn a_verdict() -> PacketPayload {
        PacketPayload::StpPickupGranted {
            item_id: 7,
            def_id: -52379,
            count: 1,
        }
    }

    /// La mitad que MÁS importa: una ráfaga legítima no puede desconectar a nadie. Un cap mal
    /// dimensionado convierte 20 pickups seguidos (o un goteo de mundo aparcado por delante) en
    /// desconexiones aleatorias, que es peor que el descarte que F0.3 vino a arreglar.
    #[tokio::test]
    async fn a_legitimate_burst_of_verdicts_never_disconnects_a_peer() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        host.peers.insert(
            2,
            PeerConnection::new(2, "Joiner".into(), loopback_addr(&joiner)),
        );

        // Peor caso legítimo del dimensionado: el goteo de un mundo entero aparcado por delante
        // (50) más una ráfaga de loot intensa (20). Muy por debajo del cap de 256.
        for _ in 0..70 {
            host.send_verdict(2, &a_verdict()).await;
        }

        assert!(
            host.peers.contains_key(&2),
            "70 veredictos seguidos son tráfico legítimo: desconectar aquí sería el bug nuevo"
        );
        let events = host.process_incoming().await;
        assert!(
            !events
                .iter()
                .any(|e| matches!(e, NetworkEvent::PeerDisconnected { .. })),
            "y no puede colarse ninguna desconexión por la puerta de atrás: {events:?}"
        );
    }

    /// La mitad negativa: superado el cap, el peer CAE — no se le descarta el veredicto y se
    /// sigue como si nada. Su inventario ya divergió del host y solo un re-sync lo arregla.
    #[tokio::test]
    async fn a_verdict_queue_overflow_disconnects_the_peer_instead_of_dropping_the_verdict() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        host.peers.insert(
            2,
            PeerConnection::new(2, "Joiner".into(), loopback_addr(&joiner)),
        );

        // Nadie ACKea (el joiner ni siquiera lee), así que la ventana se llena y todo lo demás
        // se aparca: exactamente el escenario que antes descartaba veredictos en silencio.
        for _ in 0..(NetworkManager::VERDICT_QUEUE_CAP + 40) {
            host.send_verdict(2, &a_verdict()).await;
        }

        assert!(
            !host.peers.contains_key(&2),
            "pasado el cap, el peer tiene que salir: seguir encolando o descartar deja su \
             inventario divergido para siempre"
        );
        let events = host.process_incoming().await;
        assert!(
            events
                .iter()
                .any(|e| matches!(e, NetworkEvent::PeerDisconnected { id: 2, .. })),
            "la caída tiene que salir como PeerDisconnected — es lo que dispara el teardown de \
             ADR-056 en un joiner: {events:?}"
        );
    }

    // â”€â”€â”€ F0.2 (E0): el relay encodea una vez por origen, no una por par â”€â”€â”€

    /// EL invariante de F0.2: cachear el encode por origen no puede cambiar un solo byte de lo
    /// que sale al aire. Si alguna vez el header dependiera del destino (una secuencia por peer,
    /// por ejemplo), este test falla y el cacheo hay que deshacerlo — que es exactamente la razón
    /// por la que `broadcast_reliable` NO lo lleva.
    #[tokio::test]
    async fn a_cached_relay_encode_is_byte_identical_to_the_per_destination_one() {
        let host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let payload = PacketPayload::PlayerUpdate {
            position: [12.5, 1.8, -40.0],
            rotation: 90.0,
            animation: "walk_slow".into(),
            crouch: true,
            pitch: -12,
            equipment: [1001, 1002, 1003, 1004],
            held_item: 2001,
            hit_seq: 7,
            dead: false,
            revealed: false,
            vocal_seq: 3,
            vocal_kind: 1,
            light_on: true,
            fire_seq: 9,
            buttons: 1,
            melee_seq: 4,
            carry_def: 55,
            carry_count: 2,
        };

        // Un solo encode reutilizado para tres destinos distintos...
        let cached = host.encode_relay_as(77, &payload);
        // ...contra el encode que el camino viejo hacía POR destino. `send_unreliable_as` sigue
        // definido sobre `encode_relay_as`, así que se comparan las dos llamadas que antes eran
        // dos serializaciones independientes.
        for _dest in [2u16, 3, 4] {
            let per_destination = host.encode_relay_as(77, &payload);
            assert_eq!(
                cached, per_destination,
                "el payload relayado no puede depender del destino: si depende, el cacheo de F0.2 \
                 cambia lo que viaja"
            );
        }

        // Y el origen SÍ tiene que seguir viajando en el header: sin esto el test pasaría aunque
        // el encode ignorara `sender_id` y todos los peers se vieran como el mismo.
        let other_source = host.encode_relay_as(78, &payload);
        assert_ne!(
            cached, other_source,
            "el id del origen viaja en el header (ADR-015): dos orígenes no pueden producir los \
             mismos bytes"
        );
    }

    // â”€â”€â”€ F0.1 (enmienda ADR-073): coalescing de broadcast_world_sync por pickup/drop â”€â”€â”€

    #[test]
    fn a_fresh_dirty_flag_is_ready_without_waiting_for_the_window() {
        let now = std::time::Instant::now();
        assert!(
            world_sync_ready(true, None, now, WORLD_SYNC_COALESCE_WINDOW),
            "la primera marca de la sesión no puede esperar a una ventana que nunca empezó"
        );
    }

    #[test]
    fn a_clean_flag_is_never_ready_regardless_of_timing() {
        let now = std::time::Instant::now();
        assert!(
            !world_sync_ready(false, None, now, WORLD_SYNC_COALESCE_WINDOW),
            "sin marca dirty no hay nada que despachar, aunque la ventana esté vencida"
        );
    }

    #[test]
    fn a_second_mark_inside_the_window_is_not_ready() {
        let t0 = std::time::Instant::now();
        let just_after = t0 + std::time::Duration::from_millis(50);
        assert!(
            !world_sync_ready(true, Some(t0), just_after, WORLD_SYNC_COALESCE_WINDOW),
            "una segunda interacción a 50 ms de la anterior tiene que esperar: es todo el ahorro \
             de F0.1 frente a disparar por evento"
        );
    }

    #[test]
    fn once_the_window_elapses_the_flag_is_ready_again() {
        let t0 = std::time::Instant::now();
        let after_window = t0 + WORLD_SYNC_COALESCE_WINDOW;
        assert!(
            world_sync_ready(true, Some(t0), after_window, WORLD_SYNC_COALESCE_WINDOW),
            "vencida la ventana, una marca pendiente tiene que despacharse"
        );
    }

    /// End-to-end sobre sockets reales: una ráfaga de "interacciones" (marcas dirty) coalesce en
    /// UN solo goteo, y una marca posterior a la ventana produce un segundo goteo — nunca cero,
    /// nunca uno por marca.
    #[tokio::test]
    async fn a_burst_of_dirty_marks_coalesces_into_one_drip() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let joiner_addr = loopback_addr(&joiner);
        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));

        let pos = Vec3::new(0.0, 1.8, 0.0);
        let mut host_world = World::new(42);
        host_world.update_ownership(pos, host.local_id);
        // El test solo necesita CONTAR goteos, no completarlos: recortar a un único chunk
        // mantiene el envío dentro de la ventana fiable (32) y evita la danza de ACK/pump que
        // `send_world_sync` sí necesita con un mundo grande (ver
        // `the_world_snapshot_travels_as_many_small_datagrams_and_completes`, más abajo).
        let one_chunk_key = *host_world
            .chunks
            .keys()
            .next()
            .expect("setup: el jugador necesita al menos un chunk propio");
        host_world.chunks.retain(|k, _| *k == one_chunk_key);
        let player = Player::new(host.local_id, "Host");

        async fn world_sync_ends_received(joiner: &mut NetworkManager) -> usize {
            tokio::time::sleep(Duration::from_millis(80)).await;
            joiner
                .process_incoming()
                .await
                .iter()
                .filter(|e| matches!(e, NetworkEvent::WorldSyncEndReceived { .. }))
                .count()
        }

        // Ráfaga: 20 "pickups" seguidos solo arman el flag, ninguno despacha por sí mismo.
        for _ in 0..20 {
            mark_world_sync_dirty(&mut host);
        }
        assert!(
            host.world_sync_dirty,
            "el flag tiene que seguir armado: nada lo ha consumido todavía"
        );

        // Primera comprobación del tick: dispara de inmediato (last_sent = None).
        maybe_flush_world_sync(&mut host, &host_world, &player).await;
        assert!(
            !host.world_sync_dirty,
            "el primer flush consume el flag aunque hubiera 20 marcas apiladas detrás"
        );
        let n = world_sync_ends_received(&mut joiner).await;
        assert_eq!(
            n, 1,
            "la ráfaga entera tiene que llegar como UN solo goteo, no veinte"
        );

        // Otra marca inmediatamente después: dentro de la ventana, no despacha.
        mark_world_sync_dirty(&mut host);
        maybe_flush_world_sync(&mut host, &host_world, &player).await;
        assert!(
            host.world_sync_dirty,
            "una marca a milisegundos de la anterior tiene que esperar a la ventana"
        );
        let n = world_sync_ends_received(&mut joiner).await;
        assert_eq!(n, 0, "nada puede salir todavía: sigue dentro de los 300 ms");

        // Vencida la ventana, esa misma marca pendiente sí despacha.
        tokio::time::sleep(WORLD_SYNC_COALESCE_WINDOW).await;
        maybe_flush_world_sync(&mut host, &host_world, &player).await;
        assert!(!host.world_sync_dirty);
        let n = world_sync_ends_received(&mut joiner).await;
        assert_eq!(
            n, 1,
            "pasada la ventana, la marca pendiente tiene que despachar sin más espera"
        );
    }

    // â”€â”€â”€ F0.8 (enmienda ADR-073/074): gate por chunk en broadcast_chunk_states â”€â”€â”€

    /// Positivo/negativo sobre sockets reales: dos rondas seguidas sin tocar el mundo mandan el
    /// chunk la primera vez y lo callan la segunda. Sin el par, un gate que corta SIEMPRE pasaria
    /// la mitad negativa por accidente.
    #[tokio::test]
    async fn an_unchanged_chunk_stops_being_sent_after_its_burst() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let joiner_addr = loopback_addr(&joiner);
        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));

        let pos = Vec3::new(0.0, 1.8, 0.0);
        let mut host_world = World::new(42);
        host_world.update_ownership(pos, host.local_id);

        async fn received_chunk_states(joiner: &mut NetworkManager) -> usize {
            tokio::time::sleep(Duration::from_millis(80)).await;
            joiner
                .process_incoming()
                .await
                .iter()
                .filter(|e| matches!(e, NetworkEvent::ChunkStateReceived { .. }))
                .count()
        }

        // Ráfaga post-"cambio" inicial (ROSTER_CHANGE_BURST rondas, ADR-071): las primeras
        // salidas del gate siempre emiten, para que el joiner recién unido no vea el mundo vacío.
        for _ in 0..roster::ROSTER_CHANGE_BURST {
            broadcast_chunk_states(&mut host, &host_world, pos).await;
            let n = received_chunk_states(&mut joiner).await;
            assert!(
                n > 0,
                "cada ronda de la ráfaga tiene que emitir todos los chunks"
            );
        }

        // Agotada la ráfaga y sin cambios: el gate corta, ronda vacía.
        broadcast_chunk_states(&mut host, &host_world, pos).await;
        let n = received_chunk_states(&mut joiner).await;
        assert_eq!(
            n, 0,
            "un chunk sin cambios no puede seguir viajando a 5 Hz: es todo el ahorro de F0.8"
        );

        // Positivo: tocar el mundo (mover al jugador, lo que cambia la vecindad de owner y
        // dispara `update_ownership`) genera al menos un chunk nuevo/relimitado y ese SÍ sale de
        // inmediato, sin esperar al latido — mismo criterio que ADR-071.
        let pos2 = Vec3::new(80.0, 1.8, 0.0);
        host_world.update_ownership(pos2, host.local_id);
        broadcast_chunk_states(&mut host, &host_world, pos2).await;
        let n = received_chunk_states(&mut joiner).await;
        assert!(
            n > 0,
            "un chunk que cambia (o uno nuevo por la vecindad) tiene que salir sin esperar latido"
        );
    }

    /// El heartbeat de ADR-071 es "está para reparar páginas perdidas, no para detectar
    /// cambios" — el mismo criterio se hereda aquí. Con `heartbeat = 0` se demuestra que esa vía
    /// existe también por chunk y es independiente del contenido.
    #[test]
    fn chunk_gate_heartbeat_repairs_independently_of_content() {
        let mut gate = roster::RosterGate::default();
        let data = ChunkSyncData {
            pos: [0, 0],
            layer: 0,
            seed: 42,
            template_id: 1,
            rotation: 0,
            mirrored: false,
            has_workbench: false,
            layout: ChunkLayoutV1::default(),
            stabilized: false,
            anchored: false,
            teleport_timer: 0.0,
            entities: vec![],
            items: vec![],
        };
        let now = std::time::Instant::now();
        let hash = roster::content_hash(std::slice::from_ref(&data));
        for _ in 0..roster::ROSTER_CHANGE_BURST {
            gate.should_send(hash, 1, now, roster::ROSTER_HEARTBEAT);
        }
        assert!(
            !gate.should_send(hash, 1, now, roster::ROSTER_HEARTBEAT),
            "preparación: ya calla"
        );
        assert!(
            gate.should_send(hash, 1, now, std::time::Duration::ZERO),
            "vencido el latido, la ronda sale aunque el chunk sea idéntico"
        );
    }

    /// El gate es POR CHUNK: un chunk que cambia no puede arrastrar a los que no cambiaron. Es
    /// la razón entera de usar un `HashMap` de gates en vez de uno global (que sería lo mismo que
    /// ADR-071 ya hace para rosters, y aquí rompería la propiedad exacta que se quiere).
    #[test]
    fn each_chunk_gates_independently_of_its_neighbours() {
        let mut a = roster::RosterGate::default();
        let mut b = roster::RosterGate::default();
        let now = std::time::Instant::now();
        for _ in 0..roster::ROSTER_CHANGE_BURST {
            a.should_send(1, 1, now, roster::ROSTER_HEARTBEAT);
            b.should_send(1, 1, now, roster::ROSTER_HEARTBEAT);
        }
        assert!(!a.should_send(1, 1, now, roster::ROSTER_HEARTBEAT));
        assert!(!b.should_send(1, 1, now, roster::ROSTER_HEARTBEAT));

        // Solo `a` cambia (hash distinto). `b` con el mismo hash de siempre sigue callado.
        assert!(
            a.should_send(2, 1, now, roster::ROSTER_HEARTBEAT),
            "el chunk que cambió tiene que salir"
        );
        assert!(
            !b.should_send(1, 1, now, roster::ROSTER_HEARTBEAT),
            "el chunk vecino, sin cambios, tiene que seguir callado"
        );
    }

    /// ADR-060 end-to-end sobre sockets reales: el snapshot sale como N datagramas de MTU en vez
    /// de uno gigante, y ninguno se acerca al techo de 65 507 B que mataba al monolito. Sin este
    /// test, un futuro "vuelve a mandarlo junto que es mÃ¡s simple" no falla en ninguna parte.
    #[tokio::test]
    async fn the_world_snapshot_travels_as_many_small_datagrams_and_completes() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let joiner_addr = loopback_addr(&joiner);
        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));

        let mut host_world = World::new(42);
        host_world.update_ownership(Vec3::new(0.0, 1.8, 0.0), host.local_id);
        let chunk_count = host_world.chunks.len();
        assert!(
            chunk_count > 1,
            "setup: hacen falta varios chunks o el goteo no se distingue del monolito"
        );
        let player = Player::new(1, "Host".to_string());

        send_world_sync(&mut host, 2, &host_world, &player).await;
        // El goteo entero cabe en la ventana solo si chunk_count+1 <= WINDOW_SIZE; por encima,
        // el resto sale por la cola diferida a medida que llegan los ACKs.
        for _ in 0..16 {
            tokio::time::sleep(Duration::from_millis(20)).await;
            let events = joiner.process_incoming().await;
            for e in events {
                match e {
                    NetworkEvent::WorldSyncChunkReceived {
                        world_revision,
                        data,
                    } => {
                        joiner
                            .world_sync_progress
                            .note_chunk(world_revision, data.pos, data.layer);
                    }
                    NetworkEvent::WorldSyncEndReceived {
                        world_revision,
                        chunk_count,
                    } => {
                        joiner
                            .world_sync_progress
                            .note_end(world_revision, chunk_count);
                    }
                    _ => {}
                }
            }
            host.process_incoming().await; // drena los ACKs del joiner
            host.pump_deferred_reliable().await;
            if joiner.world_sync_progress.is_complete() {
                break;
            }
        }

        assert!(
            joiner.world_sync_progress.is_complete(),
            "el goteo tiene que completar: {} chunks esperados",
            chunk_count
        );
    }

    /// La mitad negativa, y la razÃ³n entera de la decisiÃ³n "spawn en End": con el goteo a medias
    /// el gate NO puede estar abierto. El gate viejo (`!world.chunks.is_empty()`) habrÃ­a dicho
    /// que sÃ­ con el primer chunk.
    #[tokio::test]
    async fn a_half_delivered_world_never_opens_the_spawn_gate() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let joiner_addr = loopback_addr(&joiner);
        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));

        let mut host_world = World::new(42);
        host_world.update_ownership(Vec3::new(0.0, 1.8, 0.0), host.local_id);
        let player = Player::new(1, "Host".to_string());

        send_world_sync(&mut host, 2, &host_world, &player).await;
        tokio::time::sleep(Duration::from_millis(60)).await;

        // Se procesan los chunks pero se IGNORA el End: exactamente un mundo a medias.
        let mut chunks_seen = 0usize;
        for e in joiner.process_incoming().await {
            if let NetworkEvent::WorldSyncChunkReceived {
                world_revision,
                data,
            } = e
            {
                chunks_seen += 1;
                joiner
                    .world_sync_progress
                    .note_chunk(world_revision, data.pos, data.layer);
            }
        }
        assert!(chunks_seen > 0, "setup: tienen que haber llegado chunks");
        assert!(
            !joiner.world_sync_progress.is_complete(),
            "sin End no hay spawn, por muchos chunks que hayan entrado ({chunks_seen})"
        );
    }
}

/// ADR-060. El invariante que estos tests fijan no es "los chunks llegan" sino QUE NO SE ABRE EL
/// GATE DE SPAWN antes de tiempo: el emisor pasÃ³ de un datagrama a N, y el gate viejo
/// (`!world.chunks.is_empty()`) se habrÃ­a disparado con el primero.
#[cfg(test)]
mod world_drip_tests {
    use super::*;

    #[test]
    fn the_drip_is_incomplete_until_the_end_arrives_and_every_chunk_is_in() {
        let mut p = WorldSyncProgress::default();
        p.note_chunk(7, [0, 0], 0);
        p.note_chunk(7, [1, 0], 0);
        assert!(
            !p.is_complete(),
            "sin End no hay completitud: el receptor no sabe cuantos faltan"
        );

        p.note_end(7, 3);
        assert!(
            !p.is_complete(),
            "con End pero 2 de 3 chunks, sigue faltando"
        );

        p.note_chunk(7, [2, 0], 0);
        assert!(p.is_complete(), "tercer chunk distinto: completo");
    }

    #[test]
    fn the_end_may_arrive_before_the_chunks() {
        // La capa reliable es at-least-once SIN orden. Un End primero es legal y no puede
        // dejar el join colgado para siempre.
        let mut p = WorldSyncProgress::default();
        p.note_end(7, 2);
        assert!(!p.is_complete());
        p.note_chunk(7, [0, 0], 0);
        p.note_chunk(7, [1, 0], 0);
        assert!(
            p.is_complete(),
            "el orden de llegada no decide la completitud"
        );
    }

    #[test]
    fn duplicate_chunks_do_not_fake_completion() {
        // Un ACK perdido hace que el emisor retransmita: el MISMO chunk llega dos veces. Contar
        // paquetes en vez de claves abriria el gate con medio mundo aplicado.
        let mut p = WorldSyncProgress::default();
        p.note_end(7, 2);
        p.note_chunk(7, [0, 0], 0);
        p.note_chunk(7, [0, 0], 0);
        assert!(
            !p.is_complete(),
            "dos copias del mismo chunk no son dos chunks"
        );
        p.note_chunk(7, [1, 0], 0);
        assert!(p.is_complete());
    }

    #[test]
    fn the_same_coord_on_another_layer_is_another_chunk() {
        // Las capas se apilan en la misma (x,z): clave por (pos, layer) o el mundo multicapa
        // nunca completaria.
        let mut p = WorldSyncProgress::default();
        p.note_end(7, 2);
        p.note_chunk(7, [0, 0], 0);
        p.note_chunk(7, [0, 0], 1);
        assert!(p.is_complete());
    }

    #[test]
    fn a_newer_revision_discards_the_previous_count() {
        let mut p = WorldSyncProgress::default();
        p.note_end(7, 2);
        p.note_chunk(7, [0, 0], 0);

        // Llega un goteo nuevo (revision 8): lo acumulado de la 7 no cuenta para el.
        p.note_chunk(8, [5, 5], 0);
        p.note_end(8, 2);
        assert!(
            !p.is_complete(),
            "el chunk de la revision vieja no completa la nueva"
        );
        p.note_chunk(8, [6, 5], 0);
        assert!(p.is_complete());
    }

    #[test]
    fn a_straggler_from_an_old_revision_is_ignored() {
        let mut p = WorldSyncProgress::default();
        p.note_end(8, 1);
        p.note_chunk(8, [0, 0], 0);
        assert!(p.is_complete());

        // Rezagado de la revision 7 (retransmision tardia): ni completa ni descompleta.
        p.note_chunk(7, [9, 9], 0);
        p.note_end(7, 99);
        assert!(
            p.is_complete(),
            "un rezagado viejo no puede reabrir un gate ya abierto"
        );
    }

    #[test]
    fn the_deprecated_monolith_still_opens_the_gate() {
        // 0x04 aplica el mundo entero de golpe: completo por construccion, o un host viejo
        // dejaria al joiner sin spawn para siempre.
        let mut p = WorldSyncProgress::default();
        p.note_monolith(3);
        assert!(p.is_complete());
    }
}

/// F0.0 (ADR-073 / SCALING-ROADMAP, E0): sonda de la SUBIDA TOTAL del host con 8 peers.
///
/// Es la línea base del gate de E0 y el número que E1 (ADR-074) tiene que mover. Se captura
/// ANTES del primer fix de E0 — capturarla después contaminaría el "antes".
///
/// A diferencia de `roster_relay_cost` (que mide UN componente), esto suma TODO lo que el host
/// emite en régimen permanente, **contando headers UDP/IP: cada datagrama cuesta
/// `payload + 28 B` en el aire** (8 de UDP + 20 de IPv4). Con payloads de pose de ~250 B el
/// header es un ~10 %; con ACKs o heartbeats sería la mitad del paquete. Medir solo lo entregado
/// a `send_datagram` daría una unidad que no existe en el router de nadie.
///
/// También responde la pregunta de F0.1: cuánto pesa un `broadcast_world_sync` completo (el que
/// HOY dispara cada pickup/drop legacy hacia todos los peers) y si esa línea base domina sobre
/// el régimen permanente — si domina, F0.1 se detiene y la decisión vuelve a Joel (ver roadmap).
#[cfg(test)]
mod uplink_probe {
    use crate::network::protocol::{
        encode_packet, PacketHeader, PacketPayload, PeerInfo, StpBuildProgress, StpBuildingInfo,
        StpCarryableInfo, StpHarvestableInfo, StpItemInfo,
    };
    use crate::network::roster::{
        content_hash, paginate, RosterGate, ROSTER_HEARTBEAT, ROSTER_PAGE_BUDGET_BYTES,
    };
    use crate::utils::Vec3;
    use crate::world::World;

    /// UDP (8 B) + IPv4 (20 B). Sin contar Ethernet (18 B más): el gate se mide contra el
    /// ancho de banda IP del uplink, que es como lo reportan los routers domésticos.
    const UDP_IP_HEADER: usize = 28;
    const PEERS: usize = 8;
    const POSE_HZ: f64 = 10.0;
    const CHUNK_HZ: f64 = 5.0;

    /// Bytes EN EL AIRE de un payload: header propio de 12 B + MessagePack + UDP/IP.
    fn wire_len(payload: &PacketPayload) -> usize {
        let header = PacketHeader::new(0, 1, 0, 0);
        encode_packet(&header, payload).len() + UDP_IP_HEADER
    }

    fn pose_payload(seq: u8) -> PacketPayload {
        PacketPayload::PlayerUpdate {
            position: [123.5, 1.8, -412.0],
            rotation: 187.5,
            animation: "walk_slow".into(),
            crouch: false,
            pitch: -12,
            equipment: [1001, 1002, 1003, 1004],
            held_item: 2001,
            hit_seq: seq,
            dead: false,
            revealed: false,
            vocal_seq: 0,
            vocal_kind: 0,
            light_on: true,
            fire_seq: seq,
            buttons: 1,
            melee_seq: 0,
            carry_def: 0,
            carry_count: 0,
        }
    }

    fn building(id: u32) -> StpBuildingInfo {
        StpBuildingInfo {
            id,
            def_id: -4996552,
            position: [12.5, 1.8, -40.0],
            rotation: 90.0,
            group_id: id / 8,
            added: vec![StpBuildProgress {
                material_id: -1234,
                count: 4,
            }],
        }
    }

    fn item(id: u32) -> StpItemInfo {
        StpItemInfo {
            id,
            def_id: -52379,
            count: 3,
            position: [12.5, 1.8, -40.0],
            rotation: 90.0,
            settling: false,
        }
    }

    fn carryable(id: u32) -> StpCarryableInfo {
        StpCarryableInfo {
            id,
            def_id: 7,
            position: [1.0, 2.0, 3.0],
            rotation: 90.0,
        }
    }

    fn harvestable(id: u32) -> StpHarvestableInfo {
        StpHarvestableInfo {
            id,
            position: [1.0, 2.0, 3.0],
            remaining: 0.62,
        }
    }

    /// Bytes en el aire de UNA ronda completa de un roster (todas sus páginas), reproduciendo
    /// la paginación real de los `broadcast_stp_*`.
    fn roster_round_wire<T: serde::Serialize + Clone>(
        items: &[T],
        make: impl Fn(Vec<T>) -> PacketPayload,
    ) -> (usize, usize) {
        let pages = paginate(items, ROSTER_PAGE_BUDGET_BYTES);
        let count = pages.len();
        let bytes = pages.into_iter().map(|p| wire_len(&make(p))).sum();
        (count, bytes)
    }

    /// SONDA DE MEDICIÓN, no un test — imprime, no afirma.
    ///
    /// ```text
    /// cargo test --release host_uplink_baseline -- --ignored --nocapture
    /// ```
    #[test]
    #[ignore = "sonda de medición: imprime, no afirma"]
    fn host_uplink_baseline() {
        println!("\n=== F0.0 / subida TOTAL del host con {PEERS} peers (headers UDP/IP incluidos: +{UDP_IP_HEADER} B/datagrama) ===\n");

        // ── Poses ─────────────────────────────────────────────────────────────────────────
        // El host emite SU pose a cada peer (broadcast_player_update) y además relaya la pose
        // de cada peer a todos los demás (broadcast_peer_poses): P×P − P datagramas por ronda.
        let pose_wire = wire_len(&pose_payload(3));
        let own_pose_dgps = PEERS as f64 * POSE_HZ;
        let relay_dgps = (PEERS * PEERS - PEERS) as f64 * POSE_HZ;
        let own_pose_bps = own_pose_dgps * pose_wire as f64;
        let relay_bps = relay_dgps * pose_wire as f64;
        println!("PlayerUpdate en el aire: {pose_wire} B");
        println!(
            "  pose propia:    {own_pose_dgps:.0} dgr/s = {:.1} KB/s",
            own_pose_bps / 1024.0
        );
        println!(
            "  relay O(N²):    {relay_dgps:.0} dgr/s = {:.1} KB/s",
            relay_bps / 1024.0
        );

        // ── PeerList (broadcast_peer_roster, 10 Hz) ───────────────────────────────────────
        let peers_info: Vec<PeerInfo> = (0..=PEERS as u16)
            .map(|id| PeerInfo {
                id,
                name: format!("Player_{id:02}"),
                addr: "203.0.113.77:7778".into(),
                position: [123.5, 1.8, -412.0],
            })
            .collect();
        let peer_list_wire = wire_len(&PacketPayload::PeerList { peers: peers_info });
        let peer_list_bps = PEERS as f64 * POSE_HZ * peer_list_wire as f64;
        println!(
            "PeerList({} entradas) en el aire: {peer_list_wire} B → {:.1} KB/s",
            PEERS + 1,
            peer_list_bps / 1024.0
        );

        // ── ChunkState (broadcast_chunk_states, 5 Hz, chunks propios a ≤3 de distancia) ───
        // Mundo real: los chunks que update_ownership carga alrededor del jugador, con sus
        // entidades e items seedeados — el tamaño del ChunkSyncData es el de verdad.
        let mut world = World::new(42);
        world.update_ownership(Vec3::new(0.0, 1.0, 0.0), 1);
        let chunk_wires: Vec<usize> = world
            .chunks
            .values()
            .map(|c| {
                wire_len(&PacketPayload::ChunkState {
                    data: super::chunk_to_sync_data(c),
                })
            })
            .collect();
        let chunk_count = chunk_wires.len();
        let chunk_round_bytes: usize = chunk_wires.iter().sum();
        let chunk_bps = chunk_round_bytes as f64 * PEERS as f64 * CHUNK_HZ;
        println!(
            "ChunkState: {chunk_count} chunks cargados, {:.0} B de media → {:.1} KB/s ({} dgr/s)",
            chunk_round_bytes as f64 / chunk_count.max(1) as f64,
            chunk_bps / 1024.0,
            chunk_count * PEERS * CHUNK_HZ as usize
        );

        // ── Rosters (base seria de five_rosters_converge: 1000/300/200/100) ───────────────
        let buildings: Vec<_> = (0..1000).map(building).collect();
        let items: Vec<_> = (0..300).map(item).collect();
        let carryables: Vec<_> = (0..200).map(carryable).collect();
        let harvestables: Vec<_> = (0..100).map(harvestable).collect();

        let g = 1u32;
        let (b_pages, b_bytes) =
            roster_round_wire(&buildings, |p| PacketPayload::StpBuildingList {
                buildings: p,
                generation: g,
                page: 0,
                page_count: 1,
            });
        let (i_pages, i_bytes) = roster_round_wire(&items, |p| PacketPayload::StpItemList {
            items: p,
            generation: g,
            page: 0,
            page_count: 1,
        });
        let (c_pages, c_bytes) =
            roster_round_wire(&carryables, |p| PacketPayload::StpCarryableList {
                carryables: p,
                generation: g,
                page: 0,
                page_count: 1,
            });
        let (h_pages, h_bytes) =
            roster_round_wire(&harvestables, |p| PacketPayload::StpHarvestableList {
                harvestables: p,
                generation: g,
                page: 0,
                page_count: 1,
            });
        let round_pages = b_pages + i_pages + c_pages + h_pages;
        let round_bytes = b_bytes + i_bytes + c_bytes + h_bytes;
        let rosters_ungated_bps = round_bytes as f64 * PEERS as f64 * POSE_HZ;
        println!(
            "Rosters (1000+300+200+100, corpses=0): {round_pages} páginas, {round_bytes} B/ronda \
             → sin gate {:.1} KB/s",
            rosters_ungated_bps / 1024.0
        );

        // ADR-071: 60 s simulados a 10 Hz con un jugador construyendo (una pieza cada 5 s).
        // El reloj es sintético: el latido mide tiempo real y un bucle cerrado nunca lo vencería.
        let mut gates = [
            RosterGate::default(),
            RosterGate::default(),
            RosterGate::default(),
            RosterGate::default(),
        ];
        let mut live = buildings.clone();
        let t0 = std::time::Instant::now();
        let mut busy_bytes = 0usize;
        const ROUNDS: usize = 600;
        for round in 0..ROUNDS {
            if round % 50 == 0 && round > 0 {
                live.push(building(90_000 + round as u32));
            }
            let now = t0 + std::time::Duration::from_millis(round as u64 * 100);
            let hashes = [
                content_hash(&live),
                content_hash(&items),
                content_hash(&carryables),
                content_hash(&harvestables),
            ];
            let sizes = [
                roster_round_wire(&live, |p| PacketPayload::StpBuildingList {
                    buildings: p,
                    generation: g,
                    page: 0,
                    page_count: 1,
                })
                .1,
                i_bytes,
                c_bytes,
                h_bytes,
            ];
            for (k, gate) in gates.iter_mut().enumerate() {
                if gate.should_send(hashes[k], PEERS, now, ROSTER_HEARTBEAT) {
                    busy_bytes += sizes[k];
                }
            }
        }
        let rosters_busy_bps = busy_bytes as f64 * PEERS as f64 / 60.0;
        // Idle: solo latidos — una ronda completa cada 3 s por roster.
        let rosters_idle_bps = round_bytes as f64 * PEERS as f64 / ROSTER_HEARTBEAT.as_secs_f64();
        println!(
            "  con gate ADR-071: construyendo {:.1} KB/s · idle (latidos) {:.1} KB/s",
            rosters_busy_bps / 1024.0,
            rosters_idle_bps / 1024.0
        );

        // ── Totales ────────────────────────────────────────────────────────────────────────
        let fixed = own_pose_bps + relay_bps + peer_list_bps + chunk_bps;
        let total_busy = fixed + rosters_busy_bps;
        let total_idle = fixed + rosters_idle_bps;
        println!("\n--- LÍNEA BASE con {PEERS} peers (gate de E0; E1 tiene que mover esto) ---");
        println!(
            "  construyendo: {:.0} KB/s = {:.1} Mbps de subida",
            total_busy / 1024.0,
            total_busy * 8.0 / 1_000_000.0
        );
        println!(
            "  idle:         {:.0} KB/s = {:.1} Mbps de subida",
            total_idle / 1024.0,
            total_idle * 8.0 / 1_000_000.0
        );

        // ── F0.1: el world_sync completo que HOY dispara cada pickup/drop legacy ──────────
        let sync_wires: Vec<usize> = world
            .chunks
            .values()
            .map(|c| {
                wire_len(&PacketPayload::WorldSyncChunk {
                    world_revision: world.revision,
                    data: super::chunk_to_sync_data(c),
                })
            })
            .collect();
        let end_wire = wire_len(&PacketPayload::WorldSyncEnd {
            world_revision: world.revision,
            chunk_count: chunk_count as u32,
        });
        let drip_bytes: usize = sync_wires.iter().sum::<usize>() + end_wire;
        let per_interaction = drip_bytes * PEERS;
        let sustained_1hz = per_interaction as f64; // 1 interacción/s = 1 goteo/s
        let coalesced_bps = per_interaction as f64 / 0.3; // F0.1: máx 1 goteo por ventana de 300 ms
        println!("\n--- F0.1: broadcast_world_sync por interacción legacy (HOY) ---");
        println!(
            "  un goteo completo: {} chunks + End = {} datagramas, {:.1} KB",
            chunk_count,
            chunk_count + 1,
            drip_bytes as f64 / 1024.0
        );
        println!(
            "  por CADA pickup/drop, a {PEERS} peers: {:.1} KB",
            per_interaction as f64 / 1024.0
        );
        println!(
            "  1 interacción/s sostenida: {:.0} KB/s ({:.1} Mbps) — contra un permanente de {:.0} KB/s",
            sustained_1hz / 1024.0,
            sustained_1hz * 8.0 / 1_000_000.0,
            total_busy / 1024.0
        );
        println!(
            "  coalescido a 300 ms (F0.1): máx {:.2} goteos/s = {:.0} KB/s",
            1.0 / 0.3,
            coalesced_bps / 1024.0
        );
        println!(
            "\nexcluido de la suma: voz (3,9 KB/s por hablante, medido en ADR-046), ACKs y \
             retransmisiones de la capa fiable, heartbeats a 1 Hz (~decenas de B/s)."
        );
    }

    /// SONDA DE MEDICIÓN: el ANTES vs DESPUÉS de la Etapa 0 (F0.1, F0.2, F0.8).
    ///
    /// ```text
    /// cargo test --release etapa0_before_after -- --ignored --nocapture
    /// ```
    ///
    /// `host_uplink_baseline` mide el coste BRUTO de cada emisor: es la foto del "antes" y sigue
    /// siendo la línea base contra la que E1 tendrá que competir. Esta sonda mide lo que los tres
    /// fixes de E0 realmente ahorran, simulando el gate por chunk de F0.8 y el coalescing de F0.1
    /// sobre 60 s de juego con el mismo reloj sintético que usa la sonda de ADR-071 (un bucle
    /// cerrado con `Instant::now()` real recorrería los 60 s simulados en microsegundos y el
    /// latido no vencería nunca, dando un resultado mejor que el real).
    ///
    /// **Lo que NO cambia y por qué está fuera:** F0.2 no toca un solo byte del aire —ahorra
    /// serializaciones, no tráfico— así que se mide aparte, en CPU. Los rosters ya los curó
    /// ADR-071 y su ahorro no es de esta etapa.
    #[test]
    #[ignore = "sonda de medición: imprime, no afirma"]
    fn etapa0_before_after() {
        use std::time::{Duration, Instant};

        println!("\n=== Etapa 0: ANTES vs DESPUÉS ({PEERS} peers, headers UDP/IP incluidos) ===\n");

        // ── Mundo real, el mismo de la línea base ────────────────────────────────────────────
        let mut world = World::new(42);
        world.update_ownership(Vec3::new(0.0, 1.0, 0.0), 1);
        let chunk_wires: Vec<(usize, usize)> = world
            .chunks
            .values()
            .enumerate()
            .map(|(i, c)| {
                (
                    i,
                    wire_len(&PacketPayload::ChunkState {
                        data: super::chunk_to_sync_data(c),
                    }),
                )
            })
            .collect();
        let chunk_count = chunk_wires.len();
        let round_bytes: usize = chunk_wires.iter().map(|(_, b)| b).sum();

        // ── F0.8: ChunkState, antes (todo cada ronda) vs después (gate por chunk) ────────────
        // 300 rondas a 5 Hz = 60 s. `churn` = cuántos de los 49 chunks cambian en cada ronda:
        // un chunk cambia si sus entidades se mueven o sus items cambian, así que el número real
        // depende de cuánta IA y cuánto loot activo haya cerca. Se dan los tres extremos en vez
        // de inventar uno: reposo (nadie cerca), actividad normal y el peor caso absoluto.
        const ROUNDS: usize = 300;
        const CHUNK_HZ_F: f64 = 5.0;
        let before_bps = round_bytes as f64 * PEERS as f64 * CHUNK_HZ_F;
        println!("--- F0.8 · ChunkState ({chunk_count} chunks, {round_bytes} B por ronda) ---");
        println!(
            "  ANTES (sin gate, todas las rondas): {:.0} KB/s = {:.1} Mbps",
            before_bps / 1024.0,
            before_bps * 8.0 / 1_000_000.0
        );

        for (label, churn) in [
            ("reposo (nadie cerca mutando nada)", 0usize),
            ("actividad normal (~4 de 49 chunks)", 4),
            ("peor caso (los 49 cambian siempre)", chunk_count),
        ] {
            let mut gates: Vec<RosterGate> =
                (0..chunk_count).map(|_| RosterGate::default()).collect();
            let t0 = Instant::now();
            let mut sent_bytes = 0usize;
            for round in 0..ROUNDS {
                let now = t0 + Duration::from_millis(round as u64 * 200);
                for (i, wire) in &chunk_wires {
                    // Un chunk "activo" cambia de contenido en cada ronda; el resto es idéntico.
                    let hash = if *i < churn { round as u64 + 1 } else { 0 };
                    if gates[*i].should_send(hash, PEERS, now, ROSTER_HEARTBEAT) {
                        sent_bytes += wire;
                    }
                }
            }
            let after_bps = sent_bytes as f64 * PEERS as f64 / 60.0;
            println!(
                "  DESPUÉS · {label}: {:.0} KB/s = {:.1} Mbps  ({:.1}× menos)",
                after_bps / 1024.0,
                after_bps * 8.0 / 1_000_000.0,
                before_bps / after_bps.max(1.0)
            );
        }

        // ── F0.1: world_sync por interacción, antes (1 por evento) vs después (coalescido) ───
        let drip_bytes: usize = world
            .chunks
            .values()
            .map(|c| {
                wire_len(&PacketPayload::WorldSyncChunk {
                    world_revision: world.revision,
                    data: super::chunk_to_sync_data(c),
                })
            })
            .sum::<usize>()
            + wire_len(&PacketPayload::WorldSyncEnd {
                world_revision: world.revision,
                chunk_count: chunk_count as u32,
            });
        let per_drip = drip_bytes * PEERS;
        println!(
            "\n--- F0.1 · world_sync por interacción ({:.1} KB por goteo a {PEERS} peers) ---",
            per_drip as f64 / 1024.0
        );
        for (label, interactions_per_s) in [
            ("un pickup cada 2 s (juego tranquilo)", 0.5f64),
            ("2 interacciones/s (loot activo)", 2.0),
            ("ráfaga de 20 en 1 s (vaciar un cofre)", 20.0),
        ] {
            let before = per_drip as f64 * interactions_per_s;
            // F0.1: como mucho un goteo por ventana de 300 ms.
            let after = per_drip as f64 * interactions_per_s.min(1.0 / 0.3);
            println!(
                "  {label}: ANTES {:.0} KB/s → DESPUÉS {:.0} KB/s  ({:.1}× menos)",
                before / 1024.0,
                after / 1024.0,
                (before / after.max(1.0)).max(1.0)
            );
        }

        // ── F0.2: mismos bytes, menos CPU. Se mide en serializaciones, no en tráfico ─────────
        let pose = pose_payload(3);
        let reps = 2_000;
        let t = Instant::now();
        for _ in 0..reps {
            // ANTES: una serialización por PAR (origen, destino).
            for _dest in 0..(PEERS - 1) {
                std::hint::black_box(rmp_serde::to_vec_named(&pose).unwrap());
            }
        }
        let before_us = t.elapsed().as_secs_f64() / reps as f64 * 1e6;
        let t = Instant::now();
        for _ in 0..reps {
            // DESPUÉS: una por ORIGEN, reutilizada para todos los destinos.
            std::hint::black_box(rmp_serde::to_vec_named(&pose).unwrap());
        }
        let after_us = t.elapsed().as_secs_f64() / reps as f64 * 1e6;
        let per_round_before = before_us * PEERS as f64;
        let per_round_after = after_us * PEERS as f64;
        println!("\n--- F0.2 · relay de poses: CPU, no tráfico (los bytes son idénticos) ---");
        println!(
            "  serializaciones por ronda: ANTES {} (P×D) → DESPUÉS {} (P)",
            PEERS * (PEERS - 1),
            PEERS
        );
        println!(
            "  CPU por ronda: ANTES {per_round_before:.1} µs → DESPUÉS {per_round_after:.1} µs \
             ({:.1}× menos, {:.2} ms/s a 10 Hz frente a {:.2})",
            per_round_before / per_round_after.max(0.001),
            per_round_after * 10.0 / 1000.0,
            per_round_before * 10.0 / 1000.0
        );

        // ── El total, que es la única cifra que Joel puede comparar contra su router ─────────
        // Los otros emisores no los toca esta etapa: rosters ya gateados por ADR-071 (795 KB/s
        // construyendo), relay de poses (153), PeerList (52) y pose propia (22). Se toman de
        // `host_uplink_baseline`, misma sonda y mismo escenario.
        const OTHER_EMITTERS_KBPS: f64 = 795.0 + 153.0 + 52.0 + 22.0;
        let before_total = before_bps / 1024.0 + OTHER_EMITTERS_KBPS;
        println!("\n--- TOTAL de subida del host con {PEERS} peers ---");
        println!(
            "  ANTES:  {:.0} KB/s = {:.1} Mbps",
            before_total,
            before_total * 1024.0 * 8.0 / 1_000_000.0
        );
        for (label, churn) in [("reposo", 0usize), ("actividad normal", 4)] {
            let mut gates: Vec<RosterGate> =
                (0..chunk_count).map(|_| RosterGate::default()).collect();
            let t0 = Instant::now();
            let mut sent_bytes = 0usize;
            for round in 0..ROUNDS {
                let now = t0 + Duration::from_millis(round as u64 * 200);
                for (i, wire) in &chunk_wires {
                    let hash = if *i < churn { round as u64 + 1 } else { 0 };
                    if gates[*i].should_send(hash, PEERS, now, ROSTER_HEARTBEAT) {
                        sent_bytes += wire;
                    }
                }
            }
            let after_total =
                sent_bytes as f64 * PEERS as f64 / 60.0 / 1024.0 + OTHER_EMITTERS_KBPS;
            println!(
                "  DESPUÉS · {label}: {:.0} KB/s = {:.1} Mbps  ({:.1}× menos de subida)",
                after_total,
                after_total * 1024.0 * 8.0 / 1_000_000.0,
                before_total / after_total
            );
        }

        println!(
            "\nNota: el ahorro de F0.8 depende de cuántos chunks cambian de verdad por ronda, que \
             es lo que no se puede saber sin una sesión real — por eso van los tres extremos y no \
             un número inventado. El de F0.1 depende del ritmo de interacción, igual."
        );
    }
}
