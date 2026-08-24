//! ADR-094 faceling peers (adultos y niños de oficina): host-side synthetic peers injected
//! outside the handshake. Mirrors `network::phantom` (ADR-016) almost verbatim — same trick,
//! same guarantees — with one deliberate difference: `relay_only` is stamped here directly at
//! spawn, so every exclusion site that already reads `PeerConnection::relay_only` (`build_peer_list`,
//! `real_peer_count`, `real_peer_names`, the reliable-broadcast filter in `send.rs`) treats a
//! faceling exactly like a phantom with ZERO changes to those sites. `faceling_ids` exists only
//! for the two things `relay_only` cannot express on its own: "is this specifically a faceling"
//! (PvP damage routing) and heartbeat refresh (nobody ever sends it a real packet to refresh from).

use std::net::SocketAddr;

use log::info;

use super::peer::PeerConnection;
use super::{NetworkManager, PeerId, FACELING_ID_BASE};

impl NetworkManager {
    // ─── ADR-094: faceling peers (adultos y niños) ───

    /// Whether `id` is an injected faceling peer (host-side mark only).
    pub fn is_faceling(&self, id: PeerId) -> bool {
        self.faceling_ids.contains(&id)
    }

    /// Pick a non-colliding id in the dedicated faceling range (≥ FACELING_ID_BASE), skipping
    /// the local id and any id already in use — same probe-and-skip as `allocate_phantom_id`.
    fn allocate_faceling_id(&self) -> PeerId {
        let mut id = FACELING_ID_BASE;
        while id == self.local_id || self.peers.contains_key(&id) {
            id = id.checked_add(1).unwrap_or(FACELING_ID_BASE);
        }
        id
    }

    /// Inject a synthetic "faceling" peer DIRECTLY into `peers`, OUTSIDE the handshake — no
    /// `PeerConnected` event, no real-peer id allocator. Unlike `spawn_phantom`, `relay_only` is
    /// stamped HERE rather than left for `build_peer_list`/`is_phantom` to derive, because a
    /// faceling has no `phantom_ids`-equivalent union to fall back on at those sites — see the
    /// module doc. Returns the assigned faceling id.
    pub fn spawn_faceling(&mut self, name: &str, position: [f32; 3], species: u8) -> PeerId {
        let id = self.allocate_faceling_id();
        let addr: SocketAddr = super::INERT_PEER_ADDR;
        let mut conn = PeerConnection::new(id, name.to_string(), addr);
        conn.relay_only = true;
        conn.species = species;
        conn.update_player_state(position, 0.0, "idle".into());
        self.peers.insert(id, conn);
        self.faceling_ids.insert(id);
        info!(
            "MPTRACE step=FL event=faceling_spawned self_id={} faceling_id={} species={} name={} pos=({:.2},{:.2},{:.2}) peer_count={}",
            self.local_id, id, species, name, position[0], position[1], position[2], self.peers.len()
        );
        id
    }

    /// The counterpart of `spawn_faceling`. Only a faceling can be removed through here, same
    /// non-generic-eviction guard `despawn_phantom` uses.
    pub fn despawn_faceling(&mut self, id: PeerId) -> bool {
        if !self.faceling_ids.remove(&id) {
            return false;
        }
        self.peers.remove(&id);
        info!(
            "MPTRACE step=FL event=faceling_despawned self_id={} faceling_id={} peer_count={}",
            self.local_id,
            id,
            self.peers.len()
        );
        true
    }

    /// Refresh injected facelings' heartbeat so `check_timeouts` never reaps them (they receive
    /// no real packets) — same reason and same shape as `refresh_phantom_heartbeats`.
    pub fn refresh_faceling_heartbeats(&mut self) {
        for id in &self.faceling_ids {
            if let Some(peer) = self.peers.get_mut(id) {
                peer.record_heartbeat();
            }
        }
    }
}
