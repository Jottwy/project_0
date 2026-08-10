//! Networking domain over UDP. Topology is a host-as-server STAR, not a mesh: a joiner only
//! connects to the host, and the host re-emits each peer's pose to the others (ADR-015 relay,
//! `sync::broadcast_peer_poses`). See docs/NETWORK_ARCHITECTURE_CURRENT.md.
//!
//! `NetworkManager` owns the UDP socket, tracks peer connections, handles the
//! reliability layer, and produces `NetworkEvent`s for the game loop.

mod events;
mod handlers;
pub mod peer;
mod phantom;
pub mod protocol;
pub mod reliability;
pub mod roster;
mod send;
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
use protocol::{decode_packet, encode_packet, PacketHeader, PacketPayload, HEADER_SIZE};

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

/// ADR-060 (d): los cinco ensambladores de roster, agrupados en un solo campo de
/// `NetworkManager` en vez de cinco sueltos.
#[derive(Debug, Default)]
pub struct RosterAssemblers {
    pub items: roster::RosterAssembler<protocol::StpItemInfo>,
    pub buildings: roster::RosterAssembler<protocol::StpBuildingInfo>,
    pub carryables: roster::RosterAssembler<protocol::StpCarryableInfo>,
    pub harvestables: roster::RosterAssembler<protocol::StpHarvestableInfo>,
    pub corpses: roster::RosterAssembler<crate::world::corpse::CorpseData>,
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
    /// ADR-060 (joiner-only en la práctica): completitud del goteo de snapshot de mundo.
    /// El gate de spawn del joiner consulta `is_complete()`; el host nunca la toca (resuelve
    /// su spawn en el bootstrap, antes del loop).
    pub world_sync_progress: sync::WorldSyncProgress,
    /// ADR-060 (d), joiner-only: reensamblado de los cinco rosters paginados. Un roster solo se
    /// aplica cuando su generación está completa — aplicar media lista BORRARÍA la otra mitad de
    /// los objetos del joiner, que es peor que esperar los 100 ms a la ronda siguiente.
    pub roster_assemblers: RosterAssemblers,
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
    /// ADR-050 point 9 — victims who reported struggling out of a grab this tick, drained by
    /// `PhantomDriver::tick_grab`.
    ///
    /// A SET keyed by victim and not a flag or a queue: mashing produces many reports for the same
    /// grab and only the first can matter, and one player breaking free must never release the
    /// creature holding somebody else. Host-only — it is the only backend that simulates phantoms,
    /// so a joiner's struggle arrives here as a `StruggleReport` packet.
    pub pending_struggles: std::collections::HashSet<PeerId>,
    /// ADR-053 — the last thing each real player said, kept so a robapieles can say it back.
    ///
    /// ONE packet per speaker, overwritten: this is a stolen scrap of voice, not a recording, and
    /// a rolling buffer would be a per-player audio log living in server memory for no extra
    /// effect. Opus bytes are passed through untouched — the backend never decodes them (it has no
    /// codec and wants none), so the "distortion" is the client's job.
    ///
    /// Host-only in practice: only the host relays voice and only the host simulates phantoms.
    pub voice_echo: std::collections::HashMap<PeerId, Vec<u8>>,
    /// ADR-053 — sequence number for the echoes, and it has to be its OWN monotonic counter.
    ///
    /// The first version borrowed `next_phantom_attack_request_id`, which only moves when a blow is
    /// routed to a REMOTE victim — so in a solo session it sits at 0 forever, every echo went out
    /// with the same `seq`, and the client's jitter buffer (which orders and de-duplicates BY seq,
    /// exactly as a voice stream should) would treat the second one onwards as a repeat and drop
    /// it. The creature would have said your words back exactly once per session.
    pub voice_echo_seq: u16,
    incoming_rx: mpsc::Receiver<IncomingPacket>,
    pub session_start: Instant,
    /// Throttle for `send_datagram`'s failure log: millis since `session_start` of the last
    /// line emitted. Atomic (not a plain field) because the send helper takes `&self` — several
    /// broadcast paths do. One line per second GLOBALLY: a send that fails usually keeps
    /// failing at the broadcast cadence, and the point is to make it visible, not to become the
    /// new noise floor.
    last_send_error_log_ms: std::sync::atomic::AtomicU64,
    /// Igual que el de arriba, para la traza de `ChunkStateReceived`. Hace falta un throttle REAL
    /// (una línea por segundo) y no el `elapsed % 1000 < 120` que usan las trazas de pose: aquél
    /// deja pasar una VENTANA de 120 ms, y a ~820 chunks/s eso son ~60 líneas por segundo, no una
    /// — medido, 2 847 líneas en 45 s antes de cambiarlo.
    last_chunk_state_log_ms: std::sync::atomic::AtomicU64,
    /// ADR-011: when the LOCAL player last confirmed a pickup. `broadcast_player_update`
    /// emits animation="pickup" while inside the ~1s window — a trigger flank for the proxy,
    /// NOT the gesture duration (the client owns that via the Animator exitTime).
    pub last_pickup_at: Option<Instant>,
    next_peer_id: PeerId,
    pub world_seed: u64,
    /// ADR-045 Fase 2: whether `world_seed` above is actually known yet. The host knows it from
    /// its own launch args at construction (`true` from the start); a joiner does not learn it
    /// until `handle_handshake_ack` writes it, where this flips to `true` in the same place.
    /// Exists so player-file resolution (which needs `world_seed` + `identity_key` together, see
    /// `game_loop::run`) can poll a plain field instead of inferring the moment from an event.
    pub world_seed_known: bool,
    /// ADR-056: which peer is the host, from this backend's point of view. `None` on the host
    /// itself (it IS the host — nobody to point at) and on a joiner until its `HandshakeAck`
    /// arrives, where `handle_handshake_ack` fills it in with the same `sender_id` it registers
    /// as the host peer.
    ///
    /// Same shape and same reason as `world_seed_known` above: `PeerDisconnected` has to answer
    /// "was that the host?" and the host's id is only implicit today (peer `1` by convention,
    /// spelled as a literal in ~15 call sites that an earlier audit asked NOT to grow). A plain
    /// field lets the handler compare instead of hardcoding a sixteenth.
    pub host_peer_id: Option<PeerId>,
    /// P0-2: density multiplier for the phantom population draw. Same shape as `world_seed` —
    /// read once from env at boot (or from a loaded save, which wins), travels in the
    /// HandshakeAck, and the joiner adopts the host's value. Defaults to 1.0 (no scaling) so
    /// every existing test constructing a `NetworkManager` directly keeps today's behavior.
    pub phantom_density_scale: f32,
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
            world_sync_progress: sync::WorldSyncProgress::default(),
            roster_assemblers: RosterAssemblers::default(),
            next_corpse_request_id: 1,
            processed_pvp_hits: BoundedDedupeSet::with_capacity(512),
            processed_pvp_grants: BoundedDedupeSet::with_capacity(512),
            processed_phantom_grants: BoundedDedupeSet::with_capacity(512),
            next_phantom_attack_request_id: 1,
            pending_pickups: std::collections::HashMap::new(),
            phantom_ids: std::collections::HashSet::new(),
            pending_struggles: std::collections::HashSet::new(),
            voice_echo: std::collections::HashMap::new(),
            voice_echo_seq: 0,
            incoming_rx: rx,
            session_start: Instant::now(),
            last_send_error_log_ms: std::sync::atomic::AtomicU64::new(0),
            last_chunk_state_log_ms: std::sync::atomic::AtomicU64::new(0),
            last_pickup_at: None,
            next_peer_id: if is_host { 2 } else { 0 },
            world_seed,
            world_seed_known: is_host,
            host_peer_id: None,
            phantom_density_scale: 1.0,
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
                self.purge_peer_state(id);
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

    /// `true` como mucho una vez por segundo — throttle REAL, con el mismo compare_exchange que
    /// usa `send_datagram` para su log de fallos. Existe porque el broadcast de chunks llega a
    /// ~820/s y su traza tiene que ser legible, no el nuevo suelo de ruido.
    pub fn should_log_chunk_state(&self) -> bool {
        use std::sync::atomic::Ordering;
        let now_ms = self.session_start.elapsed().as_millis() as u64;
        let last = self.last_chunk_state_log_ms.load(Ordering::Relaxed);
        now_ms.saturating_sub(last) >= 1000
            && self
                .last_chunk_state_log_ms
                .compare_exchange(last, now_ms, Ordering::Relaxed, Ordering::Relaxed)
                .is_ok()
    }

    /// ADR-060: drena las colas diferidas hacia la ventana reliable. Por cada peer, mientras
    /// haya hueco en la ventana Y paquetes aparcados, el más antiguo pasa al aire y a la cola
    /// de retransmisión. Llamado desde `process_retransmits` — mismo tick que procesa los ACKs
    /// que abren el hueco, así el drenaje avanza a velocidad de ventana sin timer propio.
    pub async fn pump_deferred_reliable(&mut self) {
        let peer_ids: Vec<PeerId> = self.peers.keys().copied().collect();
        for pid in peer_ids {
            loop {
                // Préstamo corto: decidir y extraer con el mutable, enviar con `&self`.
                let next = match self.peers.get_mut(&pid) {
                    Some(peer) if peer.can_queue_reliable() => {
                        match peer.deferred_reliable.pop_front() {
                            Some(pkt) => Some((peer.addr, pkt)),
                            None => None,
                        }
                    }
                    _ => None,
                };
                let Some((addr, pkt)) = next else {
                    break;
                };
                self.send_datagram(&pkt.data, addr, "deferred_reliable")
                    .await;
                if let Some(peer) = self.peers.get_mut(&pid) {
                    peer.queue_reliable(pkt.sequence, pkt.data);
                }
            }
        }
    }

    /// Retransmit reliable packets that haven't been ACKed. Returns disconnect events for the
    /// peers whose reliable path died.
    ///
    /// ADR-062: agotar `MAX_RETRIES` DESCONECTA al peer. Antes se vaciaba su cola y se le
    /// retenía, con lo que quedaba conectado pero mudo para siempre por la vía reliable
    /// (WorldSync, chunks, acciones), sin evento y sin ruta de recuperación — `check_timeouts`
    /// no lo reapaba porque cualquier paquete unreliable le refresca `last_heartbeat`.
    pub async fn process_retransmits(&mut self) -> Vec<NetworkEvent> {
        let mut events = Vec::new();
        self.pump_deferred_reliable().await;
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

            // ADR-016: un fantasma NO se evicta desde aquí — su ciclo de vida lo gestiona el
            // sistema phantom (`refresh_phantom_heartbeats` lo mantiene fuera del alcance de
            // `check_timeouts`). Conserva el comportamiento heredado: purgar y seguir. Ver
            // ADR-062 §guarda de fantasmas: eso lo deja en el mismo estado silencioso que este
            // ADR elimina para peers reales, acotado a fantasmas y declarado como tal.
            if self.is_phantom(pid) {
                let ids_after = self.peer_ids();
                if let Some(peer) = self.peers.get_mut(&pid) {
                    let endpoint = peer.addr;
                    let queued = peer.reliable_queue.len();
                    peer.reliable_queue.clear();
                    let deferred_dropped = peer.purge_deferred();
                    warn!(
                        "Phantom {} ({}) reliable queue dropped after too many retransmit failures; phantom retained (deferred dropped: {deferred_dropped})",
                        peer.name, endpoint
                    );
                    info!(
                        "MPTRACE step=L event=peer_reliable_queue_dropped reason=reliable_retransmit_exhausted_phantom_retained self_id={} peer_id={} endpoint={} peer_count_before={} peer_count_after={} queued_reliable_before={} remote_players_ids={:?}",
                        self_id,
                        pid,
                        endpoint,
                        peer_count_before,
                        peer_count_before,
                        queued,
                        ids_after
                    );
                }
                continue;
            }

            // Peer real: mismo camino de desconexión que `check_timeouts` — remove + purge del
            // estado indexado por PeerId + evento. Su cola diferida muere con él al salir del
            // mapa; no hace falta purgarla aparte.
            if let Some(peer) = self.peers.remove(&pid) {
                self.purge_peer_state(pid);
                warn!(
                    "Peer {} ({}) disconnected: reliable retransmit exhausted ({} reliable + {} deferred packets lost)",
                    peer.name,
                    peer.addr,
                    peer.reliable_queue.len(),
                    peer.deferred_reliable.len()
                );
                info!(
                    "MPTRACE step=L event=peer_removed reason=reliable_retransmit_exhausted self_id={} peer_id={} endpoint={} peer_count_before={} peer_count_after={} queued_reliable_before={} remote_players_ids={:?}",
                    self_id,
                    pid,
                    peer.addr,
                    peer_count_before,
                    self.peers.len(),
                    peer.reliable_queue.len(),
                    self.peer_ids()
                );
                events.push(NetworkEvent::PeerDisconnected {
                    id: pid,
                    reason: "reliable retransmit exhausted".into(),
                });
            }
        }

        events
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
