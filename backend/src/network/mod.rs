//! Networking domain over UDP. Topology is a host-as-server STAR, not a mesh: a joiner only
//! connects to the host, and the host re-emits each peer's pose to the others (ADR-015 relay,
//! `sync::broadcast_peer_poses`). See docs/NETWORK_ARCHITECTURE_CURRENT.md.
//!
//! `NetworkManager` owns the UDP socket, tracks peer connections, handles the
//! reliability layer, and produces `NetworkEvent`s for the game loop.

mod events;
pub mod peer;
mod phantom;
pub mod protocol;
pub mod reliability;
pub mod sync;

pub use events::NetworkEvent;

use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::{Duration, Instant};

use log::{debug, info, warn};
use tokio::net::UdpSocket;
use tokio::sync::mpsc;

use peer::PeerConnection;
use protocol::{
    decode_packet, encode_packet, PacketHeader, PacketPayload, PeerInfo, SessionConfig, HEADER_SIZE,
};
use reliability::is_reliable;

/// Identifier for a peer within a session.
pub type PeerId = u16;

/// ADR-016: base id for injected phantom peers (the robapieles). Chosen ABOVE the real-peer
/// id space so it never collides: host=1, host-assigned fallbacks from 2 up, and joiner
/// NET_IDs = 1000 + pid%60000 ∈ [1000, 60999] (`NetworkInitializer.GenerateDebugNetId`).
/// 0xF000 (61440) clears that range with room to spare in the u16 id space.
const PHANTOM_ID_BASE: PeerId = 0xF000;

/// ADR-029 V0: a size-bounded, insertion-ordered dedupe set. Unlike the older `processed_*`
/// `HashSet`s elsewhere in this module (which grow unbounded for the session's lifetime),
/// ADR-029 explicitly requires PvP dedupe structures to have pruning by size or age — this
/// evicts the oldest entry once `cap` is exceeded, in O(1) amortized per insert.
#[derive(Debug)]
pub struct BoundedDedupeSet<K: std::hash::Hash + Eq + Copy> {
    order: std::collections::VecDeque<K>,
    set: std::collections::HashSet<K>,
    cap: usize,
}

impl<K: std::hash::Hash + Eq + Copy> BoundedDedupeSet<K> {
    pub fn with_capacity(cap: usize) -> Self {
        Self {
            order: std::collections::VecDeque::with_capacity(cap),
            set: std::collections::HashSet::with_capacity(cap),
            cap,
        }
    }

    /// Returns `true` if `key` was newly inserted (not a duplicate). A duplicate does NOT
    /// refresh its position in the eviction order (first-seen wins the recency slot).
    pub fn insert(&mut self, key: K) -> bool {
        if !self.set.insert(key) {
            return false;
        }
        self.order.push_back(key);
        if self.order.len() > self.cap {
            if let Some(oldest) = self.order.pop_front() {
                self.set.remove(&oldest);
            }
        }
        true
    }
}

/// Incoming packet from the receive loop.
struct IncomingPacket {
    addr: SocketAddr,
    header: PacketHeader,
    payload: PacketPayload,
}

/// The central networking coordinator. Owns the UDP socket, tracks peers,
/// handles reliability, and bridges between raw packets and game-level events.
pub struct NetworkManager {
    socket: Arc<UdpSocket>,
    pub local_id: PeerId,
    pub is_host: bool,
    pub peers: HashMap<PeerId, PeerConnection>,
    /// Host-authoritative STP world items, replicated to peers (Phase 1). On the
    /// host it is set from the IPC `set_stp_items` action; on joiners from the
    /// relayed `StpItemList` packet. build_world_state mirrors it to the client.
    pub stp_items: Vec<crate::network::protocol::StpItemInfo>,
    /// Phase 3: client-generated drop ids already processed by the host, so a
    /// duplicated `stp_drop` (watcher race OR reliable retransmit) spawns one item.
    pub processed_stp_drops: std::collections::HashSet<u64>,
    /// Phase B1: host-authoritative STP building pieces, replicated to peers. On the
    /// host it grows from the IPC `stp_place` action; on joiners from the relayed
    /// `StpBuildingList` packet. build_world_state mirrors it to the client.
    pub stp_buildings: Vec<crate::network::protocol::StpBuildingInfo>,
    /// Phase B1: client-generated place ids already processed by the host, so a
    /// duplicated `stp_place` (reliable retransmit) spawns exactly one piece.
    pub processed_stp_places: std::collections::HashSet<u64>,
    /// Phase B2: client-generated add ids already processed by the host, so a
    /// duplicated `stp_build_add` (reliable retransmit) advances progress exactly once.
    pub processed_stp_build_adds: std::collections::HashSet<u64>,
    /// ADR-037: client-generated demolish ids already processed by the host, so a duplicated
    /// `stp_demolish` (reliable retransmit) can never retire a SECOND piece — building ids are
    /// handed out by a monotonic allocator, but this set is what stops a late retransmit from
    /// acting twice.
    pub processed_stp_demolishes: std::collections::HashSet<u64>,
    /// Phase B3: quantized world pose-cells already occupied by a group piece, so two
    /// players placing on the SAME socket (distinct place_ids) yield exactly one piece —
    /// the host accepts the first and rejects the rest. Key = (x,y,z,yaw) quantized.
    pub occupied_stp_cells: std::collections::HashSet<(i32, i32, i32, i32)>,
    /// ADR-041: noises reported this tick as `(position, loudness_metres)`, drained by
    /// `PhantomDriver`. Host-only and sim-only — never serialized, never sent to a peer, never
    /// persisted. It lives here for the same reason `processed_stp_*` does: the IPC action handler
    /// has `net` in scope but not the driver, and threading the driver through would touch every
    /// caller of `handle_action` for one field.
    pub pending_noises: Vec<([f32; 3], f32)>,
    /// Phase B2.5: host-authoritative STP world carryables, replicated to peers. On the
    /// host it is set from `set_stp_carryables` and grows from drops; on joiners from the
    /// relayed `StpCarryableList` packet. build_world_state mirrors it to the client.
    pub stp_carryables: Vec<crate::network::protocol::StpCarryableInfo>,
    /// Phase B2.5: client-generated carryable drop ids already processed by the host (dedup).
    pub processed_stp_carryable_drops: std::collections::HashSet<u64>,
    /// Phase B2.6: host-authoritative STP scene harvestables (health), replicated to peers.
    /// On the host it is set from `set_stp_harvestables` and reduced by `stp_harvest_hit`;
    /// on joiners from the relayed `StpHarvestableList` packet.
    pub stp_harvestables: Vec<crate::network::protocol::StpHarvestableInfo>,
    /// Phase B2.6: client-generated harvest-hit ids already processed by the host (dedup).
    pub processed_stp_harvest_hits: std::collections::HashSet<u64>,
    /// ADR-011 follow-up: host-assigned item ids whose StpPickupGranted the joiner already
    /// processed, so a reliable retransmit of the grant never re-stamps last_pickup_at (which
    /// would duplicate the proxy "pickup" window). Same dedup pattern as the processed_stp_* above.
    pub processed_stp_pickup_grants: std::collections::HashSet<u32>,
    /// ADR-028 Fase E (host-only): (requester, request_id) pairs of corpse spawn/take requests
    /// already processed, so a reliable retransmit spawns exactly one corpse / takes exactly one
    /// stack. Keyed by requester too (request ids are per-peer counters, not globally unique).
    pub processed_corpse_requests: std::collections::HashSet<(PeerId, u64)>,
    /// ADR-028 Fase E (joiner-only): request_ids whose CorpseTakeResult we already surfaced to
    /// our Unity, so a reliable retransmit of the verdict never double-fires the IPC event
    /// (a duplicated corpse_item_taken would double-shift CorpseLootSync's index mirror).
    pub processed_corpse_results: std::collections::HashSet<u64>,
    /// ADR-028 Fase E (joiner-only): monotonic source for our corpse request ids.
    pub next_corpse_request_id: u64,
    /// ADR-029 V0 (host-only): (attacker_id, request_id) pairs of PvP hit candidates already
    /// validated, so a reliable retransmit of `PvpHitCandidate` never grants/rejects twice.
    /// Size-bounded (see `BoundedDedupeSet`), unlike the older unbounded `processed_*` sets —
    /// required explicitly by ADR-029 ("las estructuras de dedupe deben tener poda").
    pub processed_pvp_hits: BoundedDedupeSet<(u32, u64)>,
    /// ADR-029 V0 (victim-side, host or joiner): (attacker_id, request_id) pairs of
    /// `PvpDamageGrant` already applied to this backend's own `PlayerStats`, so a reliable
    /// retransmit of the grant never doubles the damage. This is the LOAD-BEARING defensive
    /// dedupe — the victim's own backend is the final authority over its own health.
    pub processed_pvp_grants: BoundedDedupeSet<(u32, u64)>,
    /// ADR-047 (victim-side, host or joiner): `request_id`s of `PhantomAttackGrant` already
    /// applied to this backend's own `PlayerStats`, so a reliable retransmit never doubles a
    /// robapieles' blow. A bare `u64` is enough where PvP needs a pair: the host is the sole
    /// minter of these ids, so they are unique without an attacker to disambiguate them.
    pub processed_phantom_grants: BoundedDedupeSet<u64>,
    /// ADR-047 (host-only): monotonic minter for the `request_id` above. Never reset — a restart
    /// gets a fresh backend and a fresh dedupe set, so the two stay consistent.
    pub next_phantom_attack_request_id: u64,
    /// ADR-014 (host-only): reserved pickups awaiting their deferred removal.
    /// item_id → (requester_id, remove_at). The item stays in `stp_items` (visible) until
    /// remove_at, but a second request for a reserved item is rejected — the reservation is the
    /// dedup now that the removal is deferred. Never points at a vanished item (purged on removal).
    pub pending_pickups: std::collections::HashMap<u32, (PeerId, Instant)>,
    /// ADR-016 (host-only, backend-only): ids of injected "phantom" peers (the robapieles).
    /// A phantom lives in `peers` and renders like a real player, but is excluded from
    /// `real_peer_count` (so it doesn't contaminate internal count gates) and skipped by
    /// reliable broadcasts (its addr is inert). INVARIANT: this mark NEVER crosses the wire —
    /// it is not in `PeerInfo` (P2P) nor `RemotePlayerState` (IPC); pure host-side state. A
    /// joiner therefore cannot tell a phantom from a real peer (and its own set stays empty).
    pub phantom_ids: std::collections::HashSet<PeerId>,
    incoming_rx: mpsc::Receiver<IncomingPacket>,
    pub session_start: Instant,
    /// Throttle for `send_datagram`'s failure log: millis since `session_start` of the last
    /// line emitted. Atomic (not a plain field) because the send helper takes `&self` — several
    /// broadcast paths do. One line per second GLOBALLY: a send that fails usually keeps
    /// failing at the broadcast cadence, and the point is to make it visible, not to become the
    /// new noise floor.
    last_send_error_log_ms: std::sync::atomic::AtomicU64,
    /// ADR-011: when the LOCAL player last confirmed a pickup. `broadcast_player_update`
    /// emits animation="pickup" while inside the ~1s window — a trigger flank for the proxy,
    /// NOT the gesture duration (the client owns that via the Animator exitTime).
    pub last_pickup_at: Option<Instant>,
    next_peer_id: PeerId,
    pub world_seed: u64,
    global_sequence: u32,
    pub local_name: String,
    pending_connect_addr: Option<SocketAddr>,
    last_handshake_sent_at: Option<Instant>,
    handshake_attempts: u32,
    last_keepalive_trace_at: HashMap<PeerId, Instant>,
    last_transform_trace_at: HashMap<PeerId, Instant>,
}

impl NetworkManager {
    /// Bind a UDP socket and start the receive loop.
    pub async fn bind(
        port: u16,
        local_id: PeerId,
        world_seed: u64,
        is_host: bool,
    ) -> std::io::Result<Self> {
        let addr: SocketAddr = format!("0.0.0.0:{port}").parse().unwrap();
        let socket = Arc::new(UdpSocket::bind(addr).await?);
        let local_addr = socket.local_addr()?;
        info!("UDP bound on {local_addr}");
        info!(
            "MPTRACE step=P event=network_state_init reason=bind self_id_before=<none> self_id_after={} peer_count_before=0 peer_count_after=0 endpoint={} role={}",
            local_id,
            local_addr,
            if is_host { "host" } else { "joiner" }
        );

        let (tx, rx) = mpsc::channel::<IncomingPacket>(512);
        let recv_socket = socket.clone();
        tokio::spawn(receive_loop(recv_socket, tx));

        Ok(Self {
            socket,
            local_id,
            is_host,
            peers: HashMap::new(),
            stp_items: Vec::new(),
            processed_stp_drops: std::collections::HashSet::with_capacity(256),
            stp_buildings: Vec::new(),
            processed_stp_places: std::collections::HashSet::with_capacity(256),
            processed_stp_build_adds: std::collections::HashSet::with_capacity(256),
            processed_stp_demolishes: std::collections::HashSet::with_capacity(256),
            occupied_stp_cells: std::collections::HashSet::with_capacity(256),
            pending_noises: Vec::new(),
            stp_carryables: Vec::new(),
            processed_stp_carryable_drops: std::collections::HashSet::with_capacity(64),
            stp_harvestables: Vec::new(),
            processed_stp_harvest_hits: std::collections::HashSet::with_capacity(128),
            processed_stp_pickup_grants: std::collections::HashSet::with_capacity(128),
            processed_corpse_requests: std::collections::HashSet::with_capacity(64),
            processed_corpse_results: std::collections::HashSet::with_capacity(64),
            next_corpse_request_id: 1,
            processed_pvp_hits: BoundedDedupeSet::with_capacity(512),
            processed_pvp_grants: BoundedDedupeSet::with_capacity(512),
            processed_phantom_grants: BoundedDedupeSet::with_capacity(512),
            next_phantom_attack_request_id: 1,
            pending_pickups: std::collections::HashMap::new(),
            phantom_ids: std::collections::HashSet::new(),
            incoming_rx: rx,
            session_start: Instant::now(),
            last_send_error_log_ms: std::sync::atomic::AtomicU64::new(0),
            last_pickup_at: None,
            next_peer_id: if is_host { 2 } else { 0 },
            world_seed,
            global_sequence: 0,
            local_name: format!("Player{local_id}"),
            pending_connect_addr: None,
            last_handshake_sent_at: None,
            handshake_attempts: 0,
            last_keepalive_trace_at: HashMap::new(),
            last_transform_trace_at: HashMap::new(),
        })
    }

    pub fn local_addr(&self) -> SocketAddr {
        self.socket
            .local_addr()
            .unwrap_or_else(|_| "0.0.0.0:0".parse().unwrap())
    }

    fn timestamp(&self) -> u32 {
        self.session_start.elapsed().as_millis() as u32
    }

    fn next_sequence(&mut self) -> u32 {
        self.global_sequence = self.global_sequence.wrapping_add(1);
        self.global_sequence
    }

    /// Initiate a connection to a remote peer (joiner → host).
    pub async fn initiate_connection(&mut self, addr: SocketAddr) {
        self.pending_connect_addr = Some(addr);
        self.handshake_attempts = 0;
        self.send_handshake(addr).await;
    }

    async fn send_handshake(&mut self, addr: SocketAddr) {
        self.handshake_attempts = self.handshake_attempts.saturating_add(1);
        self.last_handshake_sent_at = Some(Instant::now());
        info!(
            "Sending handshake to {addr} sender_id={} attempt={}",
            self.local_id, self.handshake_attempts
        );
        info!(
            "MPTRACE step=A event=joiner_send_handshake self_id={} sender_id={} assigned_id=<none> peer_id=<none> endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids=[] attempt={}",
            self.local_id,
            self.local_id,
            addr,
            self.peers.len(),
            self.handshake_attempts
        );
        let payload = PacketPayload::Handshake {
            player_name: self.local_name.clone(),
            version: "0.1.0".into(),
        };
        let header = PacketHeader::new(payload.type_code(), self.local_id, 0, self.timestamp());
        let data = encode_packet(&header, &payload);
        self.send_datagram(&data, addr, "handshake").await;
    }

    pub async fn retry_pending_connection(&mut self) {
        if self.is_host || !self.peers.is_empty() {
            return;
        }

        let Some(addr) = self.pending_connect_addr else {
            return;
        };

        let should_retry = self
            .last_handshake_sent_at
            .map(|sent| sent.elapsed() >= Duration::from_secs(1))
            .unwrap_or(true);

        if should_retry {
            self.send_handshake(addr).await;
        }
    }

    /// Process all incoming packets and return game-level events.
    pub async fn process_incoming(&mut self) -> Vec<NetworkEvent> {
        let mut incoming = Vec::new();
        while let Ok(pkt) = self.incoming_rx.try_recv() {
            incoming.push(pkt);
        }

        let mut events = Vec::new();
        for pkt in incoming {
            events.extend(self.handle_packet(pkt).await);
        }
        events
    }

    /// Send heartbeats to all peers.
    pub async fn send_heartbeats(&self) {
        let payload = PacketPayload::Heartbeat;
        self.broadcast_unreliable(&payload).await;
    }

    /// Check for timed-out peers. Returns disconnect events.
    pub fn check_timeouts(&mut self) -> Vec<NetworkEvent> {
        let mut events = Vec::new();
        let peer_count_before = self.peers.len();
        let ids_before = self.peer_ids();
        let timed_out: Vec<PeerId> = self
            .peers
            .values()
            .filter(|p| p.is_timed_out())
            .map(|p| p.id)
            .collect();

        if !timed_out.is_empty() || peer_count_before > 0 {
            info!(
                "MPTRACE step=O event=peer_cleanup_scan self_id={} peer_count_before={} peer_count_after=<pending> removed_ids={:?} threshold_ms=5000 peer_ids_before={:?}",
                self.local_id,
                peer_count_before,
                timed_out,
                ids_before
            );
        }

        for id in timed_out {
            if let Some(peer) = self.peers.remove(&id) {
                info!("Peer {} ({}) timed out", peer.name, peer.addr);
                info!(
                    "MPTRACE step=L event=peer_removed reason=heartbeat_timeout self_id={} peer_id={} endpoint={} peer_count_before={} peer_count_after={} remote_players_ids={:?}",
                    self.local_id,
                    id,
                    peer.addr,
                    peer_count_before,
                    self.peers.len(),
                    self.peer_ids()
                );
                events.push(NetworkEvent::PeerDisconnected {
                    id,
                    reason: "heartbeat timeout".into(),
                });
            }
        }

        if peer_count_before > 0 {
            info!(
                "MPTRACE step=M event=peer_registry_snapshot source=check_timeouts self_id={} peer_count={} peer_ids={:?} endpoints={:?}",
                self.local_id,
                self.peers.len(),
                self.peer_ids(),
                self.peer_endpoints()
            );
        }
        events
    }

    /// Retransmit reliable packets that haven't been ACKed.
    pub async fn process_retransmits(&mut self) {
        let peer_ids: Vec<PeerId> = self.peers.keys().copied().collect();
        let peer_count_before = self.peers.len();
        let mut failed_reliable_peers = Vec::new();

        for pid in peer_ids {
            // El préstamo mutable de `peers` se cierra ANTES de enviar: `send_datagram` toma
            // `&self`. Se saca la dirección y la lista de reenvíos, y se sale del scope.
            let pending = match self.peers.get_mut(&pid) {
                Some(peer) => {
                    let (retransmits, peer_dead) = peer.collect_retransmits();
                    if peer_dead {
                        failed_reliable_peers.push(pid);
                        continue;
                    }
                    Some((peer.addr, retransmits))
                }
                None => None,
            };
            if let Some((addr, retransmits)) = pending {
                for data in retransmits {
                    debug!("Retransmitting {} bytes to {}", data.len(), addr);
                    self.send_datagram(&data, addr, "retransmit").await;
                }
            }
        }

        for pid in failed_reliable_peers {
            let self_id = self.local_id;
            let ids_after = self.peer_ids();
            if let Some(peer) = self.peers.get_mut(&pid) {
                let endpoint = peer.addr;
                let queued = peer.reliable_queue.len();
                peer.reliable_queue.clear();
                warn!(
                    "Peer {} ({}) reliable queue dropped after too many retransmit failures; peer retained",
                    peer.name, endpoint
                );
                info!(
                    "MPTRACE step=L event=peer_reliable_queue_dropped reason=reliable_retransmit_exhausted_peer_retained self_id={} peer_id={} endpoint={} peer_count_before={} peer_count_after={} queued_reliable_before={} remote_players_ids={:?}",
                    self_id,
                    pid,
                    endpoint,
                    peer_count_before,
                    peer_count_before,
                    queued,
                    ids_after
                );
            }
        }
    }

    // ─── Send methods ───

    /// Send an unreliable packet to a specific peer.
    pub async fn send_unreliable_to(&self, peer_id: PeerId, payload: &PacketPayload) {
        if let Some(peer) = self.peers.get(&peer_id) {
            let seq = 0; // unreliable packets don't need meaningful sequence
            let header =
                PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
            let data = encode_packet(&header, payload);
            self.send_datagram(&data, peer.addr, "unreliable_to").await;
        }
    }

    /// ADR-015: send an unreliable packet to `dest_peer` stamped with an ARBITRARY
    /// `sender_id` in the header (instead of `self.local_id`). The host uses this to
    /// RELAY another peer's `PlayerUpdate` "on behalf of" that peer, so a joiner —
    /// which only connects to the host — still learns the rotation+animation of the
    /// OTHER joiners (PeerInfo/PeerList carry only position). The receive path is
    /// unchanged: the destination updates `peers[sender_id]` exactly as for a genuine
    /// PlayerUpdate. Also the mechanism ADR-016 will reuse to propagate a phantom
    /// peer's pose (sender_id = phantom_id) — kept generic over `sender_id`/`dest`.
    pub async fn send_unreliable_as(
        &self,
        sender_id: PeerId,
        dest_peer: PeerId,
        payload: &PacketPayload,
    ) {
        if let Some(peer) = self.peers.get(&dest_peer) {
            let header = PacketHeader::new(payload.type_code(), sender_id, 0, self.timestamp());
            let data = encode_packet(&header, payload);
            self.send_datagram(&data, peer.addr, "relay_as").await;
        }
    }

    /// Broadcast an unreliable packet to all connected peers.
    pub async fn broadcast_unreliable(&self, payload: &PacketPayload) {
        let header = PacketHeader::new(payload.type_code(), self.local_id, 0, self.timestamp());
        let data = encode_packet(&header, payload);
        for (_, addr) in self.broadcast_destinations() {
            self.send_datagram(&data, addr, "broadcast_unreliable")
                .await;
        }
    }

    /// Addresses an unreliable broadcast may legitimately be sent to: every real peer, never a
    /// phantom. Split out — like `sync::relay_destinations` and for the same reason — so the
    /// invariant is testable without a socket.
    ///
    /// This closes the LAST hole in ADR-043's rule ("DESTINATIONS exclude phantoms; SOURCES do
    /// not"). ADR-043 applied the filter to the pose relay, ADR-046 to the voice relay and
    /// ADR-016 to WorldSync, but `broadcast_unreliable` — the path the HOST'S OWN pose and the
    /// peer roster take — was still addressing phantoms.
    ///
    /// It is not a micro-optimisation. A phantom's `addr` is the inert `127.0.0.1:1` stamped at
    /// injection, so every datagram aimed at one is a real syscall to a dead loopback port; on
    /// Windows the ICMP port-unreachable comes back as WSAECONNRESET **on the socket**. With the
    /// world populated by default since ADR-043 (five phantoms, ~10 call sites, 10-20 Hz) that
    /// poisoned the host's own socket — measured at 1,073,132 `os error 10054` lines in a single
    /// play-test log. The symptom is precisely asymmetric and is what it cost to find: joiners saw
    /// each other (relayed through the already-filtered path) while the HOST was invisible to
    /// everyone, because its own pose and the roster are the two things that ride this one.
    ///
    /// Before ADR-043 a single phantom lived behind an env flag that defaults OFF, so the defect
    /// was unreachable rather than absent.
    fn broadcast_destinations(&self) -> Vec<(PeerId, SocketAddr)> {
        self.peers
            .values()
            .filter(|p| !self.is_phantom(p.id))
            .map(|p| (p.id, p.addr))
            .collect()
    }

    /// Send a reliable packet to a specific peer (queued for ACK tracking).
    pub async fn send_reliable(&mut self, peer_id: PeerId, payload: &PacketPayload) {
        let seq = self.next_sequence();
        let header = PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
        let data = encode_packet(&header, payload);

        // Igual que en process_retransmits: se resuelve la dirección con un préstamo INMUTABLE,
        // se envía, y solo después se vuelve a pedir el mutable para encolar el reenvío.
        let Some(peer) = self.peers.get(&peer_id) else {
            return;
        };
        let addr = peer.addr;

        // Control de ventana. `can_queue_reliable` existía desde la Fase 3 y NO lo llamaba
        // nadie: la cola crecía sin tope por peer, y al llegar a MAX_RETRIES el barrido la
        // vacía ENTERA (`peer.reliable_queue.clear()`), tirando también lo que aún era
        // recuperable. Con la ventana llena se descarta el paquete NUEVO y se dice en el log,
        // en vez de acumular presión hasta el borrado masivo.
        if !peer.can_queue_reliable() {
            warn!(
                "MPTRACE step=SEND_FAIL event=reliable_window_full self_id={} peer_id={} type=0x{:02x} in_flight={} window={} dropped_bytes={}",
                self.local_id,
                peer_id,
                payload.type_code(),
                peer.reliable_queue.len(),
                reliability::WINDOW_SIZE,
                data.len()
            );
            return;
        }

        self.send_datagram(&data, addr, "reliable").await;
        if let Some(peer) = self.peers.get_mut(&peer_id) {
            peer.queue_reliable(seq, data);
        }
    }

    /// Broadcast a reliable packet to all peers.
    ///
    /// ADR-016: phantom peers are skipped — their addr is inert (nobody listens), so a
    /// reliable packet to one would never be ACKed and just pile up retransmits. Real
    /// peers still receive it.
    pub async fn broadcast_reliable(&mut self, payload: &PacketPayload) {
        let peer_addrs: Vec<(PeerId, SocketAddr)> = self
            .peers
            .iter()
            .filter(|(id, _)| !self.phantom_ids.contains(id))
            .map(|(id, p)| (*id, p.addr))
            .collect();

        for (pid, addr) in peer_addrs {
            let seq = self.next_sequence();
            let header =
                PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
            let data = encode_packet(&header, payload);
            self.send_datagram(&data, addr, "broadcast_reliable").await;
            if let Some(peer) = self.peers.get_mut(&pid) {
                peer.queue_reliable(seq, data);
            }
        }
    }

    /// The single outgoing-datagram choke point. Every `send_to` in this file goes through
    /// here so that a failed send can never be silent again.
    ///
    /// The eight call sites this replaced were all `let _ = self.socket.send_to(...)`, which
    /// swallowed `EMSGSIZE` — the exact error produced once a full-roster payload outgrows the
    /// 65507 B IPv4 datagram limit (`StpBuildingList` reaches it somewhere around ~800 placed
    /// pieces). That failure is indistinguishable from packet loss: no log, no error, and no
    /// visible relation to anything the player did. Building replication would simply stop one
    /// day, for everyone, permanently. The payload size travels in the log because the size IS
    /// the diagnosis.
    ///
    /// Takes `&self` (several broadcast paths are `&self`), hence the atomic throttle.
    async fn send_datagram(&self, data: &[u8], addr: SocketAddr, kind: &str) {
        let Err(e) = self.socket.send_to(data, addr).await else {
            return;
        };
        use std::sync::atomic::Ordering;
        let now_ms = self.session_start.elapsed().as_millis() as u64;
        let last = self.last_send_error_log_ms.load(Ordering::Relaxed);
        if now_ms.saturating_sub(last) >= 1000
            && self
                .last_send_error_log_ms
                .compare_exchange(last, now_ms, Ordering::Relaxed, Ordering::Relaxed)
                .is_ok()
        {
            warn!(
                "MPTRACE step=SEND_FAIL event=datagram_send_failed self_id={} kind={} dest={} payload_bytes={} err={}",
                self.local_id,
                kind,
                addr,
                data.len(),
                e
            );
        }
    }

    /// Send a raw encoded packet to an address (used for handshake responses
    /// before the peer is in the peers map).
    async fn send_raw_to(&self, addr: SocketAddr, payload: &PacketPayload) {
        let seq = 0;
        let header = PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
        let data = encode_packet(&header, payload);
        self.send_datagram(&data, addr, "raw").await;
    }

    // ─── Packet handling ───

    async fn handle_packet(&mut self, pkt: IncomingPacket) -> Option<NetworkEvent> {
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

        match pkt.payload {
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
            }),

            PacketPayload::StpBuildAddRequest {
                add_id,
                building_id,
                material_id,
            } => Some(NetworkEvent::StpBuildAddRequest {
                add_id,
                building_id,
                material_id,
            }),

            PacketPayload::StpDemolishRequest {
                demolish_id,
                building_id,
            } => Some(NetworkEvent::StpDemolishRequest {
                demolish_id,
                building_id,
            }),

            PacketPayload::StpCarryableList { carryables } => {
                // Host-authoritative carryable roster: joiners mirror it verbatim. (B2.5)
                self.stp_carryables = carryables;
                None
            }

            PacketPayload::StpCarryablePickupRequest {
                carryable_id,
                requester_id,
            } => Some(NetworkEvent::StpCarryablePickupRequest {
                carryable_id,
                requester_id,
            }),

            PacketPayload::StpCarryablePickupGranted {
                carryable_id,
                def_id,
            } => Some(NetworkEvent::StpCarryablePickupGranted {
                carryable_id,
                def_id,
            }),

            PacketPayload::StpCarryableDropRequest {
                drop_id,
                def_id,
                position,
                rotation,
            } => Some(NetworkEvent::StpCarryableDropRequest {
                drop_id,
                def_id,
                position,
                rotation,
            }),

            PacketPayload::StpHarvestableList { harvestables } => {
                // Host-authoritative harvestable health roster: joiners mirror it. (B2.6)
                self.stp_harvestables = harvestables;
                None
            }

            PacketPayload::StpHarvestHitRequest {
                hit_id,
                harvestable_id,
                amount,
            } => Some(NetworkEvent::StpHarvestHitRequest {
                hit_id,
                harvestable_id,
                amount,
            }),

            PacketPayload::StpPickupRequest {
                item_id,
                requester_id,
            } => Some(NetworkEvent::StpPickupRequest {
                item_id,
                requester_id,
            }),

            // ADR-028 Fase E: corpse relay — 1:1 payload→event mapping; all the authority
            // logic (dedupe, spawn/take, verdict relay, mirroring) lives in game_loop, which
            // owns World (corpses live in world.corpses, not in NetworkManager).
            PacketPayload::CorpseSpawnRequest {
                request_id,
                requester_id,
                owner_name,
                position,
                equipment,
                held_item,
                items,
            } => Some(NetworkEvent::CorpseSpawnRequest {
                request_id,
                requester_id,
                owner_name,
                position,
                equipment,
                held_item,
                items,
            }),

            PacketPayload::CorpseTakeRequest {
                request_id,
                requester_id,
                corpse_id,
                item_index,
                quantity,
                requester_pos,
            } => Some(NetworkEvent::CorpseTakeRequest {
                request_id,
                requester_id,
                corpse_id,
                item_index,
                quantity,
                requester_pos,
            }),

            PacketPayload::CorpseTakeResult {
                request_id,
                accepted,
                corpse_id,
                item_index,
                item_id,
                quantity,
                corpse_empty,
                reason,
            } => Some(NetworkEvent::CorpseTakeResult {
                request_id,
                accepted,
                corpse_id,
                item_index,
                item_id,
                quantity,
                corpse_empty,
                reason,
            }),

            PacketPayload::CorpseList { corpses } => {
                Some(NetworkEvent::CorpseListReceived { corpses })
            }

            // ADR-029 V0: PvP relay — 1:1 payload→event mapping; all authority logic
            // (dedupe, validation order, grant/reject dispatch) lives in game_loop, which
            // owns Player/PlayerStats (health lives there, not in NetworkManager).
            PacketPayload::PvpHitCandidate {
                request_id,
                attacker_id,
                victim_id,
                weapon_id,
                damage,
                origin,
                direction,
                client_tick,
                hit_position,
            } => Some(NetworkEvent::PvpHitCandidate {
                request_id,
                attacker_id,
                victim_id,
                weapon_id,
                damage,
                origin,
                direction,
                client_tick,
                hit_position,
            }),

            PacketPayload::PvpDamageGrant {
                request_id,
                attacker_id,
                victim_id,
                weapon_id,
                damage,
                reason,
            } => Some(NetworkEvent::PvpDamageGrant {
                request_id,
                attacker_id,
                victim_id,
                weapon_id,
                damage,
                reason,
            }),

            PacketPayload::PvpHitRejected {
                request_id,
                attacker_id,
                victim_id,
                reason,
            } => Some(NetworkEvent::PvpHitRejected {
                request_id,
                attacker_id,
                victim_id,
                reason,
            }),

            // ADR-047 — decode only. Every authority check (are we really the victim? is this a
            // retransmit? are we invulnerable?) lives in game_loop.rs, the same split the PvP
            // family above uses.
            PacketPayload::PhantomAttackGrant {
                request_id,
                victim_id,
                kind,
                damage,
                impulse,
            } => Some(NetworkEvent::PhantomAttackGrant {
                request_id,
                victim_id,
                kind,
                damage,
                impulse,
            }),

            PacketPayload::NoiseReport { position, loudness } => {
                Some(NetworkEvent::NoiseReported { position, loudness })
            }

            PacketPayload::VoiceFrame { seq, data } => Some(NetworkEvent::VoiceReceived {
                speaker: sender_id,
                seq,
                data,
            }),

            PacketPayload::StpPickupGranted {
                item_id,
                def_id,
                count,
            } => Some(NetworkEvent::StpPickupGranted {
                item_id,
                def_id,
                count,
            }),

            PacketPayload::StpDropRequest {
                drop_id,
                def_id,
                count,
                position,
                rotation,
            } => Some(NetworkEvent::StpDropRequest {
                drop_id,
                def_id,
                count,
                position,
                rotation,
            }),

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
    }

    async fn handle_handshake(
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
            let ack_payload = self.build_handshake_ack(existing.id);
            info!(
                "Sending handshake ACK to {} assigned_id={}",
                from_addr, existing.id
            );
            info!(
                "MPTRACE step=D event=host_send_handshake_ack self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
                self.local_id,
                sender_id,
                existing.id,
                existing.id,
                from_addr,
                self.peers.len(),
                self.peer_ids()
            );
            self.send_raw_to(from_addr, &ack_payload).await;
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
            let ack_payload = self.build_handshake_ack(existing.id);
            info!(
                "Sending handshake ACK to {} assigned_id={}",
                from_addr, existing.id
            );
            info!(
                "MPTRACE step=D event=host_send_handshake_ack self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
                self.local_id,
                sender_id,
                existing.id,
                existing.id,
                from_addr,
                self.peers.len(),
                self.peer_ids()
            );
            self.send_raw_to(from_addr, &ack_payload).await;
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

/// Background task: read UDP datagrams, parse, and forward to the NetworkManager.
async fn receive_loop(socket: Arc<UdpSocket>, tx: mpsc::Sender<IncomingPacket>) {
    let mut buf = vec![0u8; protocol::MAX_PACKET_SIZE];
    loop {
        match socket.recv_from(&mut buf).await {
            Ok((len, addr)) => {
                if len < HEADER_SIZE {
                    continue;
                }
                match decode_packet(&buf[..len]) {
                    Ok((header, payload)) => {
                        let pkt = IncomingPacket {
                            addr,
                            header,
                            payload,
                        };
                        if tx.send(pkt).await.is_err() {
                            break; // Channel closed, manager dropped.
                        }
                    }
                    Err(e) => {
                        debug!("Failed to decode packet from {addr}: {e}");
                    }
                }
            }
            Err(e) => {
                warn!("UDP recv error: {e}");
            }
        }
    }
}

#[cfg(test)]
mod tests;
