//! `NetworkEvent`: the game-level events the network layer hands to the game loop.
//!
//! Split out of `mod.rs` verbatim; `network` re-exports it, so `network::NetworkEvent`
//! is still the only path anyone outside this module uses.

use std::net::SocketAddr;

use super::protocol::ChunkSyncData;
use super::PeerId;

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
        /// ADR-048: cosmetic vocalisation counter + which voice it was.
        vocal_seq: u8,
        vocal_kind: u8,
        /// ADR-049: cosmetic carry state — definition id being hauled and how many units.
        carry_def: i32,
        carry_count: u8,
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
