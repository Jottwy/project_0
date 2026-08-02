//! P2P networking domain (UDP mesh).
//!
//! `NetworkManager` owns the UDP socket, tracks peer connections, handles the
//! reliability layer, and produces `NetworkEvent`s for the game loop.

pub mod peer;
pub mod protocol;
pub mod reliability;
pub mod sync;

use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::{Duration, Instant};

use log::{debug, info, warn};
use tokio::net::UdpSocket;
use tokio::sync::mpsc;

use peer::PeerConnection;
use protocol::{
    decode_packet, encode_packet, ChunkSyncData, PacketHeader, PacketPayload, PeerInfo,
    SessionConfig, HEADER_SIZE,
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

/// High-level events produced by the network layer for the game loop.
#[derive(Debug, Clone)]
pub enum NetworkEvent {
    PeerConnected {
        id: PeerId,
        name: String,
    },
    PeerDisconnected {
        id: PeerId,
        reason: String,
    },
    RemotePlayerUpdate {
        id: PeerId,
        position: [f32; 3],
        rotation: f32,
        animation: String,
        crouch: bool,
        pitch: i8,
        equipment: [i32; 4],
        held_item: i32,
        hit_seq: u8,
        dead: bool,
        revealed: bool,
        light_on: bool,
        fire_seq: u8,
        buttons: u16,
        melee_seq: u8,
    },
    WorldInteractRequest {
        requester_id: PeerId,
        request_id: u64,
        target_id: u32,
        target_kind: String,
        interaction_type: String,
        player_position: [f32; 3],
    },
    /// Phase 2: a joiner asks the host to pick up an STP item (host-authoritative).
    StpPickupRequest {
        item_id: u32,
        requester_id: PeerId,
    },
    /// Phase 2: the host grants an STP pickup to this (recoger) peer.
    StpPickupGranted {
        item_id: u32,
        def_id: i32,
        count: u16,
    },
    /// Phase 3: a joiner asks the host to spawn a dropped STP item in the world.
    StpDropRequest {
        drop_id: u64,
        def_id: i32,
        count: u16,
        position: [f32; 3],
        rotation: f32,
    },
    /// Phase B1: a joiner asks the host to place an STP building piece in the world.
    StpPlaceRequest {
        place_id: u64,
        def_id: i32,
        position: [f32; 3],
        rotation: f32,
        group_id: u32,
        is_group: bool,
    },
    /// Phase B2: a joiner asks the host to add one unit of a build material to a piece.
    StpBuildAddRequest {
        add_id: u64,
        building_id: u32,
        material_id: i32,
    },
    /// ADR-037: a joiner asks the host to retire a placed-but-unbuilt piece it just cancelled.
    StpDemolishRequest {
        demolish_id: u64,
        building_id: u32,
    },
    /// Phase B2.5: a joiner asks the host to pick up a world carryable (host-authoritative).
    StpCarryablePickupRequest {
        carryable_id: u32,
        requester_id: PeerId,
    },
    /// Phase B2.5: the host grants a carryable pickup to this peer (it carries it in hand).
    StpCarryablePickupGranted {
        carryable_id: u32,
        def_id: i32,
    },
    /// Phase B2.5: a joiner asks the host to spawn a dropped carryable in the world.
    StpCarryableDropRequest {
        drop_id: u64,
        def_id: i32,
        position: [f32; 3],
        rotation: f32,
    },
    /// Phase B2.6: a joiner reports a harvest hit on a scene harvestable (host-authoritative).
    StpHarvestHitRequest {
        hit_id: u64,
        harvestable_id: u32,
        amount: f32,
    },
    /// ADR-028 Fase E: a joiner's player died — it asks the host to spawn the corpse.
    CorpseSpawnRequest {
        request_id: u64,
        requester_id: PeerId,
        owner_name: String,
        position: [f32; 3],
        equipment: [i32; 4],
        held_item: i32,
        items: Vec<crate::world::corpse::CorpseStack>,
    },
    /// ADR-028 Fase E: a joiner asks the host to take a stack from a corpse.
    CorpseTakeRequest {
        request_id: u64,
        requester_id: PeerId,
        corpse_id: u32,
        item_index: u32,
        quantity: u16,
        requester_pos: [f32; 3],
    },
    /// ADR-028 Fase E: the host's verdict for OUR CorpseTakeRequest (we are the requester).
    CorpseTakeResult {
        request_id: u64,
        accepted: bool,
        corpse_id: u32,
        item_index: u32,
        item_id: i32,
        quantity: u16,
        corpse_empty: bool,
        reason: String,
    },
    /// ADR-028 Fase E: the host's full corpse roster (10 Hz) — mirror it into world.corpses.
    CorpseListReceived {
        corpses: Vec<crate::world::corpse::CorpseData>,
    },
    /// ADR-029 V0: a remote peer's backend forwarded a PvP hit candidate to us (the host) for
    /// validation. All authority logic (dedupe, the 11-step validation order, grant/reject
    /// dispatch) lives in game_loop.rs, same split as the corpse relay above.
    PvpHitCandidate {
        request_id: u64,
        attacker_id: u32,
        victim_id: u32,
        weapon_id: i32,
        damage: f32,
        origin: [f32; 3],
        direction: [f32; 3],
        client_tick: Option<u32>,
        hit_position: Option<[f32; 3]>,
    },
    /// ADR-029 V0: the host validated a PvP hit against OUR local player and granted the
    /// damage. We are the victim's backend — apply it via `PlayerStats::take_damage`.
    PvpDamageGrant {
        request_id: u64,
        attacker_id: u32,
        victim_id: u32,
        weapon_id: i32,
        damage: f32,
        reason: String,
    },
    /// ADR-029 V0: the host rejected OUR PvP hit candidate. We are the shooter's backend —
    /// surface the reason to our own Unity, never apply damage.
    PvpHitRejected {
        request_id: u64,
        attacker_id: u32,
        victim_id: u32,
        reason: String,
    },
    /// ADR-047: a robapieles simulated by the host struck OUR local player. We are the victim's
    /// own backend — apply it here via `PlayerStats::take_damage`, never anywhere else.
    PhantomAttackGrant {
        request_id: u64,
        victim_id: u32,
        kind: u8,
        damage: f32,
        impulse: [f32; 2],
    },
    /// ADR-047: a joiner reported a noise to us (the host). Only the host simulates phantoms, so
    /// this is the sole way a joiner's gunshot can ever reach one.
    NoiseReported {
        position: [f32; 3],
        loudness: f32,
    },
    /// ADR-046: a voice frame arrived from `speaker`. On a joiner the host has already decided
    /// we are close enough to hear it; on the host this is a peer talking, and the host is the
    /// one that decides who else gets a copy.
    ///
    /// `speaker` comes from the packet HEADER, never from the payload — on a relayed frame it is
    /// the id the host stamped via `send_unreliable_as`, which is exactly the peer whose proxy
    /// the audio belongs to.
    VoiceReceived {
        speaker: PeerId,
        seq: u16,
        data: Vec<u8>,
    },
    WorldSyncReceived {
        world_seed: u64,
        world_revision: u64,
        chunks: Vec<ChunkSyncData>,
    },
    ChunkTransferReceived {
        from: PeerId,
        data: ChunkSyncData,
    },
    ChunkTransferAckReceived {
        from: PeerId,
        pos: [i32; 2],
    },
    ChunkTeleportReceived {
        old_pos: [i32; 2],
        new_pos: [i32; 2],
        new_seed: u64,
    },
    AnchorBroadcastReceived {
        chunk_pos: [i32; 2],
        durability: f32,
        installed_by: String,
    },
    StabilizerBroadcastReceived {
        chunk_pos: [i32; 2],
        tier: u8,
        remaining_hours: f32,
    },
    HandshakeReceived {
        from_addr: SocketAddr,
        player_name: String,
    },
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
        for peer in self.peers.values() {
            self.send_datagram(&data, peer.addr, "broadcast_unreliable")
                .await;
        }
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

    async fn handle_packet(&mut self, pkt: IncomingPacket) -> Vec<NetworkEvent> {
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
                vec![]
            }

            PacketPayload::Disconnect { reason } => {
                let mut events = Vec::new();
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
                    events.push(NetworkEvent::PeerDisconnected {
                        id: sender_id,
                        reason,
                    });
                }
                events
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
                vec![]
            }

            PacketPayload::StpItemList { items } => {
                // Host-authoritative STP item roster: joiners mirror it verbatim so
                // their build_world_state replicates the same items. (Phase 1.)
                self.stp_items = items;
                vec![]
            }

            PacketPayload::StpBuildingList { buildings } => {
                // Host-authoritative STP building roster: joiners mirror it verbatim so
                // their build_world_state replicates the same pieces. (Phase B1.)
                self.stp_buildings = buildings;
                vec![]
            }

            PacketPayload::StpPlaceRequest {
                place_id,
                def_id,
                position,
                rotation,
                group_id,
                is_group,
            } => vec![NetworkEvent::StpPlaceRequest {
                place_id,
                def_id,
                position,
                rotation,
                group_id,
                is_group,
            }],

            PacketPayload::StpBuildAddRequest {
                add_id,
                building_id,
                material_id,
            } => vec![NetworkEvent::StpBuildAddRequest {
                add_id,
                building_id,
                material_id,
            }],

            PacketPayload::StpDemolishRequest {
                demolish_id,
                building_id,
            } => vec![NetworkEvent::StpDemolishRequest {
                demolish_id,
                building_id,
            }],

            PacketPayload::StpCarryableList { carryables } => {
                // Host-authoritative carryable roster: joiners mirror it verbatim. (B2.5)
                self.stp_carryables = carryables;
                vec![]
            }

            PacketPayload::StpCarryablePickupRequest {
                carryable_id,
                requester_id,
            } => vec![NetworkEvent::StpCarryablePickupRequest {
                carryable_id,
                requester_id,
            }],

            PacketPayload::StpCarryablePickupGranted {
                carryable_id,
                def_id,
            } => vec![NetworkEvent::StpCarryablePickupGranted {
                carryable_id,
                def_id,
            }],

            PacketPayload::StpCarryableDropRequest {
                drop_id,
                def_id,
                position,
                rotation,
            } => vec![NetworkEvent::StpCarryableDropRequest {
                drop_id,
                def_id,
                position,
                rotation,
            }],

            PacketPayload::StpHarvestableList { harvestables } => {
                // Host-authoritative harvestable health roster: joiners mirror it. (B2.6)
                self.stp_harvestables = harvestables;
                vec![]
            }

            PacketPayload::StpHarvestHitRequest {
                hit_id,
                harvestable_id,
                amount,
            } => vec![NetworkEvent::StpHarvestHitRequest {
                hit_id,
                harvestable_id,
                amount,
            }],

            PacketPayload::StpPickupRequest {
                item_id,
                requester_id,
            } => vec![NetworkEvent::StpPickupRequest {
                item_id,
                requester_id,
            }],

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
            } => vec![NetworkEvent::CorpseSpawnRequest {
                request_id,
                requester_id,
                owner_name,
                position,
                equipment,
                held_item,
                items,
            }],

            PacketPayload::CorpseTakeRequest {
                request_id,
                requester_id,
                corpse_id,
                item_index,
                quantity,
                requester_pos,
            } => vec![NetworkEvent::CorpseTakeRequest {
                request_id,
                requester_id,
                corpse_id,
                item_index,
                quantity,
                requester_pos,
            }],

            PacketPayload::CorpseTakeResult {
                request_id,
                accepted,
                corpse_id,
                item_index,
                item_id,
                quantity,
                corpse_empty,
                reason,
            } => vec![NetworkEvent::CorpseTakeResult {
                request_id,
                accepted,
                corpse_id,
                item_index,
                item_id,
                quantity,
                corpse_empty,
                reason,
            }],

            PacketPayload::CorpseList { corpses } => {
                vec![NetworkEvent::CorpseListReceived { corpses }]
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
            } => vec![NetworkEvent::PvpHitCandidate {
                request_id,
                attacker_id,
                victim_id,
                weapon_id,
                damage,
                origin,
                direction,
                client_tick,
                hit_position,
            }],

            PacketPayload::PvpDamageGrant {
                request_id,
                attacker_id,
                victim_id,
                weapon_id,
                damage,
                reason,
            } => vec![NetworkEvent::PvpDamageGrant {
                request_id,
                attacker_id,
                victim_id,
                weapon_id,
                damage,
                reason,
            }],

            PacketPayload::PvpHitRejected {
                request_id,
                attacker_id,
                victim_id,
                reason,
            } => vec![NetworkEvent::PvpHitRejected {
                request_id,
                attacker_id,
                victim_id,
                reason,
            }],

            // ADR-047 — decode only. Every authority check (are we really the victim? is this a
            // retransmit? are we invulnerable?) lives in game_loop.rs, the same split the PvP
            // family above uses.
            PacketPayload::PhantomAttackGrant {
                request_id,
                victim_id,
                kind,
                damage,
                impulse,
            } => vec![NetworkEvent::PhantomAttackGrant {
                request_id,
                victim_id,
                kind,
                damage,
                impulse,
            }],

            PacketPayload::NoiseReport { position, loudness } => {
                vec![NetworkEvent::NoiseReported { position, loudness }]
            }

            PacketPayload::VoiceFrame { seq, data } => {
                vec![NetworkEvent::VoiceReceived {
                    speaker: sender_id,
                    seq,
                    data,
                }]
            }

            PacketPayload::StpPickupGranted {
                item_id,
                def_id,
                count,
            } => vec![NetworkEvent::StpPickupGranted {
                item_id,
                def_id,
                count,
            }],

            PacketPayload::StpDropRequest {
                drop_id,
                def_id,
                count,
                position,
                rotation,
            } => vec![NetworkEvent::StpDropRequest {
                drop_id,
                def_id,
                count,
                position,
                rotation,
            }],

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
            } => {
                info!(
                    "Received player update from peer id={} pos=({:.2}, {:.2}, {:.2})",
                    sender_id, position[0], position[1], position[2]
                );
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
                }
                let should_log = self
                    .last_transform_trace_at
                    .get(&sender_id)
                    .map(|last| last.elapsed() >= Duration::from_secs(1))
                    .unwrap_or(true);
                if should_log {
                    self.last_transform_trace_at
                        .insert(sender_id, Instant::now());
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
                vec![NetworkEvent::RemotePlayerUpdate {
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
                }]
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
                vec![NetworkEvent::WorldSyncReceived {
                    world_seed,
                    world_revision,
                    chunks,
                }]
            }

            PacketPayload::ChunkState { data } => {
                // Treat as a chunk transfer for now.
                vec![NetworkEvent::ChunkTransferReceived {
                    from: sender_id,
                    data,
                }]
            }

            PacketPayload::ChunkTransfer { data } => {
                vec![NetworkEvent::ChunkTransferReceived {
                    from: sender_id,
                    data,
                }]
            }

            PacketPayload::ChunkTransferAck { pos } => {
                vec![NetworkEvent::ChunkTransferAckReceived {
                    from: sender_id,
                    pos,
                }]
            }

            PacketPayload::ChunkTeleport {
                old_pos,
                new_pos,
                new_seed,
            } => vec![NetworkEvent::ChunkTeleportReceived {
                old_pos,
                new_pos,
                new_seed,
            }],

            PacketPayload::AnchorBroadcast {
                chunk_pos,
                durability,
                installed_by,
            } => vec![NetworkEvent::AnchorBroadcastReceived {
                chunk_pos,
                durability,
                installed_by,
            }],

            PacketPayload::StabilizerBroadcast {
                chunk_pos,
                tier,
                remaining_hours,
            } => vec![NetworkEvent::StabilizerBroadcastReceived {
                chunk_pos,
                tier,
                remaining_hours,
            }],

            PacketPayload::Ack { acked_sequence } => {
                if let Some(peer) = self.peers.get_mut(&sender_id) {
                    peer.process_ack(acked_sequence);
                }
                vec![]
            }

            PacketPayload::Nack {
                requested_sequence: _,
            } => {
                // Future: retransmit the requested packet.
                vec![]
            }

            PacketPayload::Ping { send_time } => {
                // Respond with the same timestamp so the sender can measure RTT.
                let pong = PacketPayload::Ping { send_time };
                self.send_raw_to(pkt.addr, &pong).await;
                vec![]
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
                vec![NetworkEvent::WorldInteractRequest {
                    requester_id,
                    request_id,
                    target_id,
                    target_kind,
                    interaction_type,
                    player_position,
                }]
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
                vec![]
            }
        }
    }

    async fn handle_handshake(
        &mut self,
        from_addr: SocketAddr,
        sender_id: PeerId,
        player_name: String,
        _version: String,
    ) -> Vec<NetworkEvent> {
        if !self.is_host {
            // Only the host accepts handshakes.
            return vec![];
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
            return vec![];
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
            return vec![];
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
            return vec![];
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

        vec![NetworkEvent::PeerConnected {
            id: assigned_id,
            name: player_name,
        }]
    }

    fn handle_handshake_ack(
        &mut self,
        from_addr: SocketAddr,
        sender_id: PeerId,
        assigned_id: PeerId,
        world_seed: u64,
        peers: Vec<PeerInfo>,
    ) -> Vec<NetworkEvent> {
        if self.is_host {
            return vec![]; // Host doesn't receive handshake acks.
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

        vec![NetworkEvent::PeerConnected {
            id: sender_id,
            name: "Host".into(),
        }]
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

    // ─── ADR-016: phantom peers (robapieles) ───

    /// Number of REAL connected peers, excluding injected phantoms. Used by the internal
    /// count gates that must not count a phantom (joiner spawn gate, sanity context). The
    /// rendered roster (`build_world_state`) still includes phantoms so they appear as
    /// players. On a joiner `phantom_ids` is empty, so this equals `peer_count`.
    pub fn real_peer_count(&self) -> usize {
        self.peers
            .keys()
            .filter(|id| !self.phantom_ids.contains(id))
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
        // Inert, non-routable addr: nobody sends to it on the normal path, and reliable
        // broadcasts skip it explicitly. 127.0.0.1:1 is never a real peer endpoint.
        let addr: SocketAddr = (std::net::Ipv4Addr::LOCALHOST, 1).into();
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
            .keys()
            .copied()
            .filter(|id| !self.phantom_ids.contains(id))
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
mod tests {
    use super::*;
    use std::time::Duration;

    /// Get the loopback address for a NetworkManager (replaces 0.0.0.0 with 127.0.0.1).
    fn loopback_addr(net: &NetworkManager) -> SocketAddr {
        let mut addr = net.local_addr();
        addr.set_ip(std::net::Ipv4Addr::LOCALHOST.into());
        addr
    }

    #[tokio::test]
    async fn bind_and_local_addr() {
        let net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let addr = net.local_addr();
        assert_ne!(addr.port(), 0);
    }

    #[tokio::test]
    async fn two_peers_handshake_and_sync() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        host.local_name = "HostPlayer".into();
        let host_addr = loopback_addr(&host);

        let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
        joiner.local_name = "JoinerPlayer".into();

        joiner.initiate_connection(host_addr).await;
        tokio::time::sleep(Duration::from_millis(200)).await;

        let host_events = host.process_incoming().await;
        assert!(
            host_events
                .iter()
                .any(|e| matches!(e, NetworkEvent::PeerConnected { .. })),
            "host should see PeerConnected, got: {host_events:?}"
        );
        assert_eq!(host.peer_count(), 1);

        tokio::time::sleep(Duration::from_millis(100)).await;
        let joiner_events = joiner.process_incoming().await;
        assert!(
            joiner_events
                .iter()
                .any(|e| matches!(e, NetworkEvent::PeerConnected { .. })),
            "joiner should see PeerConnected, got: {joiner_events:?}"
        );
        assert_eq!(joiner.peer_count(), 1);
        assert_ne!(joiner.local_id, 0, "joiner should have an assigned ID");
    }

    // ADR-028 Fase E: the corpse relay's three network hops over real sockets —
    // (1) joiner → host CorpseSpawnRequest (reliable), (2) host → all CorpseList
    // broadcast (mirror), (3) host → requester CorpseTakeResult (reliable) surfacing
    // as the requester-side event. Authority/dedupe logic is unit-tested in game_loop
    // (apply_corpse_* helpers); this covers the wire + handle_packet event mapping.
    #[tokio::test]
    async fn corpse_relay_hops_round_trip_between_peers() {
        use crate::world::corpse::CorpseStack;

        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let host_addr = loopback_addr(&host);
        let mut joiner = NetworkManager::bind(0, 1004, 0, false).await.unwrap();
        joiner.initiate_connection(host_addr).await;
        tokio::time::sleep(Duration::from_millis(200)).await;
        host.process_incoming().await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        joiner.process_incoming().await;
        assert_eq!(joiner.local_id, 1004);

        // Hop 1: joiner forwards its death-loot snapshot to the host.
        let spawn = PacketPayload::CorpseSpawnRequest {
            request_id: 1,
            requester_id: joiner.local_id,
            owner_name: "Joel".into(),
            position: [-22.0, 1.8, 9.0],
            equipment: [0, -1, -2, -3],
            held_item: -99,
            items: vec![CorpseStack {
                item_id: -12345,
                quantity: 3,
            }],
        };
        joiner.send_reliable(1, &spawn).await;
        tokio::time::sleep(Duration::from_millis(150)).await;
        let host_events = host.process_incoming().await;
        assert!(
            host_events.iter().any(|e| matches!(
                e,
                NetworkEvent::CorpseSpawnRequest {
                    request_id: 1,
                    requester_id: 1004,
                    ..
                }
            )),
            "host should receive the spawn request, got: {host_events:?}"
        );

        // Hop 2: host broadcasts the roster; the joiner mirrors it.
        let mut world = crate::world::World::new(42);
        let corpse_id = world.spawn_corpse(
            1004,
            "Joel".into(),
            crate::utils::Vec3::new(-22.0, 1.8, 9.0),
            [0, -1, -2, -3],
            -99,
            vec![CorpseStack {
                item_id: -12345,
                quantity: 3,
            }],
        );
        super::sync::broadcast_corpses(&host, &world).await;
        tokio::time::sleep(Duration::from_millis(150)).await;
        let joiner_events = joiner.process_incoming().await;
        let mirrored = joiner_events.iter().find_map(|e| match e {
            NetworkEvent::CorpseListReceived { corpses } => Some(corpses),
            _ => None,
        });
        let mirrored = mirrored.expect("joiner should receive the corpse roster");
        assert_eq!(mirrored.len(), 1);
        assert_eq!(mirrored[0].id, corpse_id);
        assert_eq!(mirrored[0].owner_name, "Joel");
        assert_eq!(mirrored[0].items[0].item_id, -12345);

        // Hop 3: host sends the take verdict back to the requester only.
        let verdict = PacketPayload::CorpseTakeResult {
            request_id: 2,
            accepted: true,
            corpse_id,
            item_index: 0,
            item_id: -12345,
            quantity: 1,
            corpse_empty: false,
            reason: String::new(),
        };
        host.send_reliable(1004, &verdict).await;
        tokio::time::sleep(Duration::from_millis(150)).await;
        let joiner_events = joiner.process_incoming().await;
        assert!(
            joiner_events.iter().any(|e| matches!(
                e,
                NetworkEvent::CorpseTakeResult {
                    request_id: 2,
                    accepted: true,
                    item_id: -12345,
                    ..
                }
            )),
            "joiner should receive the take verdict, got: {joiner_events:?}"
        );
    }

    #[tokio::test]
    async fn host_honors_requested_peer_id_when_available() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let host_addr = loopback_addr(&host);

        let mut joiner = NetworkManager::bind(0, 4876, 0, false).await.unwrap();
        joiner.initiate_connection(host_addr).await;
        tokio::time::sleep(Duration::from_millis(200)).await;

        host.process_incoming().await;
        assert!(
            host.peers.contains_key(&4876),
            "host should register the joiner by requested NET_ID"
        );

        tokio::time::sleep(Duration::from_millis(100)).await;
        joiner.process_incoming().await;
        assert_eq!(joiner.local_id, 4876);
    }

    #[tokio::test]
    async fn host_assigns_fallback_id_on_requested_id_conflict() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let host_addr = loopback_addr(&host);

        let mut joiner = NetworkManager::bind(0, 1, 0, false).await.unwrap();
        joiner.initiate_connection(host_addr).await;
        tokio::time::sleep(Duration::from_millis(200)).await;

        host.process_incoming().await;
        let assigned_id = host
            .peers
            .keys()
            .next()
            .copied()
            .expect("host should register a peer");
        assert_ne!(assigned_id, 1);

        tokio::time::sleep(Duration::from_millis(100)).await;
        joiner.process_incoming().await;
        assert_eq!(joiner.local_id, assigned_id);
    }

    #[tokio::test]
    async fn player_update_round_trip() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let host_addr = loopback_addr(&host);
        let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
        joiner.initiate_connection(host_addr).await;
        tokio::time::sleep(Duration::from_millis(200)).await;
        host.process_incoming().await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        joiner.process_incoming().await;

        let payload = PacketPayload::PlayerUpdate {
            position: [10.0, 1.8, 20.0],
            rotation: 45.0,
            animation: "walk".into(),
            crouch: false,
            pitch: 0,
            equipment: [0; 4],
            held_item: 0,
            hit_seq: 0,
            dead: false,
            revealed: false,
            light_on: false,
            fire_seq: 0,
            buttons: 0,
            melee_seq: 0,
        };
        host.broadcast_unreliable(&payload).await;

        tokio::time::sleep(Duration::from_millis(100)).await;

        let events = joiner.process_incoming().await;
        let update = events
            .iter()
            .find(|e| matches!(e, NetworkEvent::RemotePlayerUpdate { .. }));
        assert!(
            update.is_some(),
            "joiner should see player update, got: {events:?}"
        );
        if let Some(NetworkEvent::RemotePlayerUpdate {
            position, rotation, ..
        }) = update
        {
            assert_eq!(*position, [10.0, 1.8, 20.0]);
            assert_eq!(*rotation, 45.0);
        }
    }

    #[tokio::test]
    async fn reliable_packet_ack() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let host_addr = loopback_addr(&host);
        let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
        joiner.initiate_connection(host_addr).await;
        tokio::time::sleep(Duration::from_millis(200)).await;
        host.process_incoming().await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        joiner.process_incoming().await;

        let joiner_id = host
            .peers
            .keys()
            .next()
            .copied()
            .expect("host should have a peer");

        let payload = PacketPayload::Disconnect {
            reason: "test".into(),
        };
        host.send_reliable(joiner_id, &payload).await;
        assert_eq!(host.peers[&joiner_id].reliable_queue.len(), 1);

        tokio::time::sleep(Duration::from_millis(100)).await;
        joiner.process_incoming().await;

        tokio::time::sleep(Duration::from_millis(100)).await;
        host.process_incoming().await;
        assert_eq!(
            host.peers.get(&joiner_id).map(|p| p.reliable_queue.len()),
            Some(0),
            "reliable queue should be empty after ACK"
        );
    }

    #[tokio::test]
    async fn reliable_retransmit_failure_does_not_remove_peer() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let peer_id = 2;
        let addr: SocketAddr = "127.0.0.1:9999".parse().unwrap();
        host.peers
            .insert(peer_id, PeerConnection::new(peer_id, "Joiner".into(), addr));

        let payload = PacketPayload::Disconnect {
            reason: "test reliable packet".into(),
        };
        host.send_reliable(peer_id, &payload).await;

        {
            let peer = host.peers.get_mut(&peer_id).unwrap();
            for packet in peer.reliable_queue.iter_mut() {
                packet.retries = reliability::MAX_RETRIES;
                packet.next_retry_at = Instant::now() - Duration::from_millis(1);
            }
        }

        host.process_retransmits().await;

        assert!(
            host.peers.contains_key(&peer_id),
            "reliable retransmit exhaustion must not remove a connected peer"
        );
        assert_eq!(host.peers[&peer_id].reliable_queue.len(), 0);
    }

    #[tokio::test]
    async fn peer_timeout_detection() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        // Manually add a peer with an old heartbeat.
        let addr: SocketAddr = "127.0.0.1:9999".parse().unwrap();
        let mut peer = PeerConnection::new(2, "OldPeer".into(), addr);
        peer.last_heartbeat = Instant::now() - Duration::from_secs(10);
        net.peers.insert(2, peer);

        let events = net.check_timeouts();
        assert_eq!(events.len(), 1);
        assert!(matches!(
            &events[0],
            NetworkEvent::PeerDisconnected { id: 2, .. }
        ));
        assert_eq!(net.peer_count(), 0);
    }

    /// El relay de poses de ADR-015 reemite el `PlayerUpdate` de B hacia C desde el socket
    /// DEL HOST, sellado con `sender_id = B`. Antes, `handle_packet` adoptaba esa `addr` sin
    /// condición, así que C acababa creyendo que B vive en la dirección del host — diez veces
    /// por segundo, y para todos los joiners a la vez. No quedaba ninguna ruta directa que
    /// descubrir. Invisible con 1 host + 1 joiner, porque el relay sale por la puerta
    /// `peers.len() < 2`.
    #[tokio::test]
    async fn relayed_packet_does_not_steal_the_relays_address() {
        let mut net = NetworkManager::bind(0, 3, 42, false).await.unwrap();
        let host_addr: SocketAddr = "127.0.0.1:7000".parse().unwrap();
        let peer_b_addr: SocketAddr = "127.0.0.1:7001".parse().unwrap();
        net.peers
            .insert(1, PeerConnection::new(1, "Host".into(), host_addr));
        net.peers
            .insert(2, PeerConnection::new(2, "PeerB".into(), peer_b_addr));

        // Llega la pose de B, pero por el socket del host (eso es exactamente lo que hace
        // send_unreliable_as).
        let relayed = IncomingPacket {
            addr: host_addr,
            header: PacketHeader::new(protocol::PacketType::PlayerUpdate as u16, 2, 0, 0),
            payload: PacketPayload::PlayerUpdate {
                position: [1.0, 1.8, 2.0],
                rotation: 0.0,
                animation: "idle".into(),
                crouch: false,
                pitch: 0,
                equipment: [0; 4],
                held_item: 0,
                hit_seq: 0,
                dead: false,
                revealed: false,
                light_on: false,
                fire_seq: 0,
                buttons: 0,
                melee_seq: 0,
            },
        };
        net.handle_packet(relayed).await;

        assert_eq!(
            net.peers[&2].addr, peer_b_addr,
            "la direccion de B no puede ser suplantada por la del relay"
        );
        assert_eq!(net.peers[&1].addr, host_addr, "la del host no se toca");
        // Y la pose SI se aplica: rechazar la direccion no puede costar el dato.
        assert_eq!(net.peers[&2].position, [1.0, 1.8, 2.0]);
    }

    /// `can_queue_reliable` existia desde la Fase 3 y no lo llamaba nadie, asi que la cola
    /// fiable crecia sin tope por peer. Importa porque al llegar a MAX_RETRIES el barrido la
    /// vacia ENTERA (`peer.reliable_queue.clear()`), o sea que dejar que se llene no retrasa la
    /// perdida: la agranda.
    #[tokio::test]
    async fn reliable_send_respects_the_window_instead_of_growing_unbounded() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let addr: SocketAddr = "127.0.0.1:7005".parse().unwrap();
        let mut peer = PeerConnection::new(2, "PeerB".into(), addr);
        for seq in 0..reliability::WINDOW_SIZE as u32 {
            peer.queue_reliable(seq, vec![0u8; 8]);
        }
        net.peers.insert(2, peer);
        assert!(
            !net.peers[&2].can_queue_reliable(),
            "ventana llena de partida"
        );

        net.send_reliable(2, &PacketPayload::Heartbeat).await;

        assert_eq!(
            net.peers[&2].reliable_queue.len(),
            reliability::WINDOW_SIZE,
            "con la ventana llena, el paquete nuevo se descarta — no se encola"
        );
    }

    /// `max_players` vivia en SessionConfig y en WorldConfig y no lo consultaba NADIE: el host
    /// anunciaba 50 en su HandshakeAck y admitia peers indefinidamente.
    #[tokio::test]
    async fn handshake_is_rejected_when_the_session_is_full() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let capacity = (SessionConfig::default().max_players as usize).saturating_sub(1);
        for i in 0..capacity {
            let id = (i + 2) as PeerId;
            let addr: SocketAddr = format!("127.0.0.1:{}", 8000 + i).parse().unwrap();
            host.peers
                .insert(id, PeerConnection::new(id, format!("P{id}"), addr));
        }
        assert_eq!(host.real_peer_count(), capacity);

        let newcomer: SocketAddr = "127.0.0.1:9500".parse().unwrap();
        host.handle_handshake(newcomer, 0, "TooMany".into(), "0.1.0".into())
            .await;

        assert_eq!(
            host.real_peer_count(),
            capacity,
            "con el aforo lleno no se admite un peer mas"
        );
        assert!(
            !host.peers.values().any(|p| p.addr == newcomer),
            "el rechazado no puede quedar registrado"
        );
    }

    /// La contrapartida: con sitio libre, el handshake SI admite al peer nuevo.
    #[tokio::test]
    async fn handshake_is_accepted_when_there_is_room() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let newcomer: SocketAddr = "127.0.0.1:9501".parse().unwrap();

        host.handle_handshake(newcomer, 0, "Joiner".into(), "0.1.0".into())
            .await;

        assert_eq!(host.real_peer_count(), 1);
        assert!(host.peers.values().any(|p| p.addr == newcomer));
    }

    /// La contrapartida: por debajo de la ventana, un envio fiable SI se encola.
    #[tokio::test]
    async fn reliable_send_queues_normally_below_the_window() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let addr: SocketAddr = "127.0.0.1:7006".parse().unwrap();
        net.peers
            .insert(2, PeerConnection::new(2, "PeerB".into(), addr));

        net.send_reliable(2, &PacketPayload::Heartbeat).await;

        assert_eq!(net.peers[&2].reliable_queue.len(), 1);
    }

    /// La contrapartida: una direccion que NO pertenece a ningun otro peer si se adopta, para
    /// que un re-binding de NAT genuino siga funcionando.
    #[tokio::test]
    async fn unclaimed_address_is_still_adopted_after_nat_rebind() {
        let mut net = NetworkManager::bind(0, 3, 42, false).await.unwrap();
        let old_addr: SocketAddr = "127.0.0.1:7001".parse().unwrap();
        let rebound_addr: SocketAddr = "127.0.0.1:7099".parse().unwrap();
        net.peers
            .insert(2, PeerConnection::new(2, "PeerB".into(), old_addr));

        let rebound = IncomingPacket {
            addr: rebound_addr,
            header: PacketHeader::new(protocol::PacketType::Heartbeat as u16, 2, 0, 0),
            payload: PacketPayload::Heartbeat,
        };
        net.handle_packet(rebound).await;

        assert_eq!(
            net.peers[&2].addr, rebound_addr,
            "una direccion sin duenio debe adoptarse (re-binding de NAT)"
        );
    }

    // ─── ADR-016: phantom peers ───

    #[tokio::test]
    async fn phantom_counts_in_roster_but_not_in_real_count() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let pid = host.spawn_phantom("Robapieles_Test", [10.0, 1.8, 5.0]);

        // Renders as a peer (the roster / build_world_state includes it)…
        assert_eq!(host.peer_count(), 1);
        // …but the internal count gates (sanity, joiner spawn) must not count it.
        assert_eq!(host.real_peer_count(), 0);
        assert!(host.is_phantom(pid));
        // Id is in the dedicated phantom range, clear of real ids and the local id.
        assert_ne!(pid, host.local_id);
        assert!(pid >= PHANTOM_ID_BASE);
        // The phantom is reachable as a normal PeerConnection (so it renders).
        let p = &host.peers[&pid];
        assert_eq!(p.name, "Robapieles_Test");
        // XZ may be snapped to a grid_gen-walkable cell, and Y is grounded to the grid_gen floor
        // + the player's stand height (ADR-018). NOTE: this assertion used to freeze `+ 0.1`,
        // which contradicted the line above and put the phantom 1.7 m into the floor once the
        // client subtracted PlayerBaseY from its pose (2026-08-01 play-test). The comment was
        // right and the number was wrong.
        let expected_y =
            crate::world::grid_gen::grid_floor_y(0) + crate::world::collision::PLAYER_BASE_Y;
        assert_eq!(
            p.position[1], expected_y,
            "spawn Y must be the grid_gen floor + the player stand height, like any real peer"
        );
    }

    #[tokio::test]
    async fn phantom_survives_timeout_when_refreshed() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let pid = host.spawn_phantom("Robapieles_Test", [0.0, 1.8, 0.0]);
        // Force its heartbeat stale (it never receives real packets), then refresh as the
        // game loop does each heartbeat-tick.
        host.peers.get_mut(&pid).unwrap().last_heartbeat = Instant::now() - Duration::from_secs(10);
        host.refresh_phantom_heartbeats();

        let events = host.check_timeouts();
        assert!(events.is_empty(), "refreshed phantom must not time out");
        assert!(host.peers.contains_key(&pid));
    }

    #[tokio::test]
    async fn phantom_is_reaped_without_refresh() {
        // Sanity check that the refresh is load-bearing: without it the timeout reaps it.
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let pid = host.spawn_phantom("Robapieles_Test", [0.0, 1.8, 0.0]);
        host.peers.get_mut(&pid).unwrap().last_heartbeat = Instant::now() - Duration::from_secs(10);

        let events = host.check_timeouts();
        assert_eq!(events.len(), 1);
        assert!(!host.peers.contains_key(&pid));
    }

    #[tokio::test]
    async fn broadcast_reliable_skips_phantom() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        // A real peer with a routable test addr…
        let real_id = 2;
        let addr: SocketAddr = "127.0.0.1:9999".parse().unwrap();
        host.peers
            .insert(real_id, PeerConnection::new(real_id, "Real".into(), addr));
        // …and a phantom alongside it.
        let phantom_id = host.spawn_phantom("Robapieles_Test", [0.0, 1.8, 0.0]);

        let payload = PacketPayload::AnchorBroadcast {
            chunk_pos: [0, 0],
            durability: 1.0,
            installed_by: "test".into(),
        };
        host.broadcast_reliable(&payload).await;

        assert_eq!(
            host.peers[&real_id].reliable_queue.len(),
            1,
            "real peer must receive the reliable broadcast"
        );
        assert_eq!(
            host.peers[&phantom_id].reliable_queue.len(),
            0,
            "phantom must be skipped by reliable broadcast"
        );
    }

    #[tokio::test]
    async fn pose_relay_addresses_real_peers_only_but_still_relays_phantom_poses() {
        // ADR-043 — the UNRELIABLE relay used to send to phantoms too (unlike the reliable one
        // above), and a phantom's addr is the inert 127.0.0.1:1, so every such datagram was a
        // syscall into a dead loopback port. Both halves are asserted, because dropping phantoms
        // as SOURCES too would be the easy over-correction and would make them invisible.
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let real_id = 2;
        let addr: SocketAddr = "127.0.0.1:9999".parse().unwrap();
        host.peers
            .insert(real_id, PeerConnection::new(real_id, "Real".into(), addr));
        let ghost_a = host.spawn_phantom("Robapieles_A", [0.0, 1.8, 0.0]);
        let ghost_b = host.spawn_phantom("Robapieles_B", [40.0, 1.8, 40.0]);

        let dests = super::sync::relay_destinations(&host);

        assert_eq!(
            dests,
            vec![real_id],
            "only real peers may be addressed, got {dests:?}"
        );
        // …and the counterpart: the phantoms are still in `peers`, so the relay's SOURCE loop
        // still emits their poses to that real peer. Without this the creature stops existing
        // for every joiner.
        assert!(
            host.peers.contains_key(&ghost_a) && host.peers.contains_key(&ghost_b),
            "phantoms must remain relay SOURCES"
        );
    }

    /// ADR-046 — la voz de un joiner llega al host y se atribuye al hablante SEGÚN LA CABECERA,
    /// no según nada que venga dentro del payload. Esa distinción es de seguridad: si el id del
    /// hablante viajara en el cuerpo, un cliente modificado podría firmar su audio como si fuera
    /// otro jugador. Sockets reales, no simulacro.
    #[tokio::test]
    async fn voice_from_a_joiner_arrives_attributed_to_the_header_sender() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let host_addr = loopback_addr(&host);
        let mut joiner = NetworkManager::bind(0, 1004, 0, false).await.unwrap();
        joiner.initiate_connection(host_addr).await;
        tokio::time::sleep(Duration::from_millis(200)).await;
        host.process_incoming().await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        joiner.process_incoming().await;
        assert_eq!(joiner.local_id, 1004);

        let audio: Vec<u8> = (0..120u16).map(|i| (i * 31 + 7) as u8).collect();
        joiner
            .send_unreliable_to(
                1,
                &PacketPayload::VoiceFrame {
                    seq: 4242,
                    data: audio.clone(),
                },
            )
            .await;
        tokio::time::sleep(Duration::from_millis(150)).await;

        let events = host.process_incoming().await;
        let got = events
            .iter()
            .find_map(|e| match e {
                NetworkEvent::VoiceReceived { speaker, seq, data } => Some((*speaker, *seq, data)),
                _ => None,
            })
            .unwrap_or_else(|| panic!("host no recibio la voz del joiner, eventos: {events:?}"));

        assert_eq!(got.0, 1004, "el hablante sale del sender_id de la cabecera");
        assert_eq!(got.1, 4242);
        assert_eq!(got.2, &audio, "el audio cruza el socket byte a byte");
    }

    /// La otra mitad del viaje: el host RELAYA la voz de un tercero sellada con el id de ESE
    /// tercero (`send_unreliable_as`, el mecanismo de ADR-015). Sin el sellado, un joiner
    /// atribuiría al host todo lo que dice cualquier otro jugador y el audio se pegaría al proxy
    /// equivocado. Aquí el host habla EN NOMBRE del peer 1007, que ni siquiera tiene socket.
    #[tokio::test]
    async fn a_relayed_voice_frame_is_attributed_to_the_speaker_not_to_the_host() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let host_addr = loopback_addr(&host);
        let mut joiner = NetworkManager::bind(0, 1004, 0, false).await.unwrap();
        joiner.initiate_connection(host_addr).await;
        tokio::time::sleep(Duration::from_millis(200)).await;
        host.process_incoming().await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        joiner.process_incoming().await;

        const OTHER_SPEAKER: PeerId = 1007;
        host.send_unreliable_as(
            OTHER_SPEAKER,
            joiner.local_id,
            &PacketPayload::VoiceFrame {
                seq: 5,
                data: vec![9; 60],
            },
        )
        .await;
        tokio::time::sleep(Duration::from_millis(150)).await;

        let events = joiner.process_incoming().await;
        let speaker = events
            .iter()
            .find_map(|e| match e {
                NetworkEvent::VoiceReceived { speaker, .. } => Some(*speaker),
                _ => None,
            })
            .unwrap_or_else(|| panic!("el joiner no recibio la voz relayada: {events:?}"));

        assert_eq!(
            speaker, OTHER_SPEAKER,
            "la voz relayada debe atribuirse al hablante, no al host que la reenvia"
        );
    }
}
