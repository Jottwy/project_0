//! ADR-016 phantom peers (the "robapieles"): host-side synthetic peers injected outside the
//! handshake. Split out of `mod.rs` verbatim.

use std::net::SocketAddr;

use log::info;

use super::peer::PeerConnection;
use super::{NetworkManager, PeerId, PHANTOM_ID_BASE};

impl NetworkManager {
    // ─── ADR-016: phantom peers (robapieles) ───

    /// Number of REAL connected peers, excluding injected phantoms. Used by the internal
    /// count gates that must not count a phantom (joiner spawn gate, sanity context). The
    /// rendered roster (`build_world_state`) still includes phantoms so they appear as
    /// players. ADR-079: on a joiner `phantom_ids` is empty but the host's phantoms now
    /// arrive as `relay_only` entries — excluded here too, so both backends agree that a
    /// phantom is not a player.
    pub fn real_peer_count(&self) -> usize {
        self.peers
            .values()
            .filter(|p| !self.phantom_ids.contains(&p.id) && !p.relay_only)
            .count()
    }

    /// Whether `id` is an injected phantom peer (host-side mark only).
    pub fn is_phantom(&self, id: PeerId) -> bool {
        self.phantom_ids.contains(&id)
    }

    /// Pick a non-colliding id in the dedicated phantom range (≥ PHANTOM_ID_BASE), skipping
    /// the local id and any id already in use. Slice 1 spawns exactly one phantom, but the
    /// linear scan keeps it correct if more are added later.
    fn allocate_phantom_id(&self) -> PeerId {
        let mut id = PHANTOM_ID_BASE;
        while id == self.local_id || self.peers.contains_key(&id) {
            id = id.checked_add(1).unwrap_or(PHANTOM_ID_BASE);
        }
        id
    }

    /// ADR-016 slice 1: inject a synthetic "phantom" peer (the robapieles) DIRECTLY into
    /// `peers`, OUTSIDE the handshake — no `PeerConnected` event (so no `send_world_sync`),
    /// no real-peer id allocator. It renders as an ordinary remote player (`build_world_state`
    /// and the ADR-015 pose relay treat it like any peer), but is marked in `phantom_ids` so
    /// it never inflates `real_peer_count` and is skipped by reliable broadcasts. The mark is
    /// backend-only (never serialized → never crosses the wire). Host-only by construction.
    /// Returns the assigned phantom id.
    pub fn spawn_phantom(&mut self, name: &str, position: [f32; 3]) -> PeerId {
        // ADR-018: the phantom collides + renders against grid_gen, but `position` comes from the
        // world::generator player spawn and may be a grid_gen WALL → it would spawn stuck. Snap it
        // to a nearby grid_gen-walkable cell.
        // ADR-033: mismo resolutor de densidad por zona que usa la caché de
        // movimiento del fantasma (y que el render) — si el snap usara el perfil
        // plano, aterrizaría contra un mundo distinto del que luego camina.
        let mut position = crate::world::grid_gen::resolve_spawn_near(
            self.world_seed,
            position,
            crate::world::zone_density::rules_for,
        );
        // Ground at the grid_gen floor, IN THE PLAYER-PIVOT CONVENTION. A peer's relayed Y is
        // `floor + PLAYER_BASE_Y` (collision.rs: "a standing player on layer 0, floor world Y ≈ 0,
        // reports transform.y = PLAYER_BASE_Y"), and the client SUBTRACTS PlayerBaseY from every
        // remote pose because the avatar pivot is at the FEET (RemotePlayerManager + GridCell.cs).
        // The phantom is a peer, so it MUST speak that same convention: pinning it to `floor + 0.1`
        // (as this did) made the client place its feet 1.7 m BELOW the floor — visible from the
        // waist up. Verified in play-test 2026-08-01 (`phantom_sprint_move pos=(37.67,0.10,-21.05)`).
        // The layer is unaffected: world_pos_to_layer(1.8) = (1.8 / 4.0) as u8 = 0.
        position[1] = crate::world::grid_gen::grid_floor_y(
            crate::world::grid_gen::world_pos_to_layer(position[1]),
        ) + crate::world::collision::PLAYER_BASE_Y;
        let id = self.allocate_phantom_id();
        // Inert, non-routable addr (shared sentinel, see INERT_PEER_ADDR): nobody sends to it
        // on the normal path, and reliable broadcasts skip it explicitly.
        let addr: SocketAddr = super::INERT_PEER_ADDR;
        let mut conn = PeerConnection::new(id, name.to_string(), addr);
        conn.update_player_state(position, 0.0, "idle".into());
        self.peers.insert(id, conn);
        self.phantom_ids.insert(id);
        info!(
            "MPTRACE step=PH event=phantom_spawned self_id={} phantom_id={} name={} pos=({:.2},{:.2},{:.2}) peer_count={} real_peer_count={}",
            self.local_id,
            id,
            name,
            position[0],
            position[1],
            position[2],
            self.peers.len(),
            self.real_peer_count()
        );
        id
    }

    /// ADR-043: remove an injected phantom — the counterpart of `spawn_phantom`, needed once the
    /// world populates itself and creatures come and go with the player.
    ///
    /// Only a phantom can be removed through here; passing a real peer id is a no-op rather than a
    /// silent disconnect, because a bug in the population logic must not be able to evict players.
    ///
    /// No `Disconnect` is sent: the peer never handshook, so there is nobody to tell. Clients drop
    /// the avatar on their own — `RemotePlayerManager` despawns a remote it stops hearing from
    /// after `missingRemoteGraceSeconds` (3 s). Deactivation only ever happens well outside view
    /// distance, so that grace period is never seen.
    pub fn despawn_phantom(&mut self, id: PeerId) -> bool {
        if !self.phantom_ids.remove(&id) {
            return false;
        }
        self.peers.remove(&id);
        info!(
            "MPTRACE step=PH event=phantom_despawned self_id={} phantom_id={} peer_count={} real_peer_count={}",
            self.local_id,
            id,
            self.peers.len(),
            self.real_peer_count()
        );
        true
    }

    /// ADR-043: names of the REAL peers, ordered by id so the ordering is stable across ticks.
    ///
    /// `peers` is a `HashMap`, so iterating it gives an arbitrary order that can change between
    /// runs; anything that assigns identities from this list would otherwise shuffle them for free.
    pub fn real_peer_names(&self) -> Vec<String> {
        let mut ids: Vec<PeerId> = self
            .peers
            .values()
            .filter(|p| !self.phantom_ids.contains(&p.id) && !p.relay_only)
            .map(|p| p.id)
            .collect();
        ids.sort_unstable();
        ids.into_iter()
            .filter_map(|id| self.peers.get(&id).map(|p| p.name.clone()))
            .collect()
    }

    /// ADR-016: refresh injected phantoms' heartbeat so `check_timeouts` never reaps them
    /// (they receive no real packets). Called each heartbeat-tick by the host before the
    /// timeout scan. A no-op where `phantom_ids` is empty (e.g. on joiners).
    pub fn refresh_phantom_heartbeats(&mut self) {
        for id in &self.phantom_ids {
            if let Some(peer) = self.peers.get_mut(id) {
                peer.record_heartbeat();
            }
        }
    }
}
