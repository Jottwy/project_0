//! P2P packet definitions (UDP, MessagePack encoded).
//! 12-byte header + MessagePack payload. See ARCHITECTURE_V1.md §5.

use serde::{Deserialize, Serialize};

use crate::world::chunk::ChunkLayoutV1;

pub const HEADER_SIZE: usize = 12;
pub const MAX_PACKET_SIZE: usize = 65535;

/// 12-byte packet header (ARCHITECTURE_V1.md §5.1).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct PacketHeader {
    pub packet_type: u16,
    pub sender_id: u16,
    pub sequence: u32,
    pub timestamp: u32, // ms since session start
}

impl PacketHeader {
    pub fn new(packet_type: u16, sender_id: u16, sequence: u32, timestamp: u32) -> Self {
        Self {
            packet_type,
            sender_id,
            sequence,
            timestamp,
        }
    }

    pub fn to_bytes(&self) -> [u8; HEADER_SIZE] {
        let mut buf = [0u8; HEADER_SIZE];
        buf[0..2].copy_from_slice(&self.packet_type.to_be_bytes());
        buf[2..4].copy_from_slice(&self.sender_id.to_be_bytes());
        buf[4..8].copy_from_slice(&self.sequence.to_be_bytes());
        buf[8..12].copy_from_slice(&self.timestamp.to_be_bytes());
        buf
    }

    pub fn from_bytes(buf: &[u8]) -> Option<Self> {
        if buf.len() < HEADER_SIZE {
            return None;
        }
        Some(Self {
            packet_type: u16::from_be_bytes([buf[0], buf[1]]),
            sender_id: u16::from_be_bytes([buf[2], buf[3]]),
            sequence: u32::from_be_bytes([buf[4], buf[5], buf[6], buf[7]]),
            timestamp: u32::from_be_bytes([buf[8], buf[9], buf[10], buf[11]]),
        })
    }
}

/// Packet type codes (ARCHITECTURE_V1.md §5.2).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u16)]
pub enum PacketType {
    // Connection (0x00-0x0F)
    Discover = 0x00,
    PeerIntro = 0x01,
    Handshake = 0x02,
    HandshakeAck = 0x03,
    WorldSync = 0x04,
    Heartbeat = 0x05,
    Disconnect = 0x06,
    PeerList = 0x07,
    // State (0x10-0x1F)
    PlayerUpdate = 0x10,
    ChunkState = 0x11,
    ChunkDelta = 0x12,
    EntityUpdate = 0x13,
    StatUpdate = 0x14,
    InventorySync = 0x15,
    StpItemList = 0x16,
    StpPickupRequest = 0x17,
    StpPickupGranted = 0x18,
    StpDropRequest = 0x19,
    StpBuildingList = 0x1A,
    StpPlaceRequest = 0x1B,
    StpBuildAddRequest = 0x1C,
    // Actions (0x20-0x2F)
    Interact = 0x20,
    Attack = 0x21,
    Pickup = 0x22,
    Drop = 0x23,
    Craft = 0x24,
    PlaceStabilizer = 0x25,
    PlaceAnchor = 0x26,
    RepairAnchor = 0x27,
    UseConsumable = 0x28,
    // World (0x30-0x3F)
    ChunkTransfer = 0x30,
    ChunkTransferAck = 0x31,
    ChunkTeleport = 0x32,
    ChunkGenerate = 0x33,
    AnchorBroadcast = 0x34,
    StabilizerBroadcast = 0x35,
    // STP Carryables (0x40-0x4F)
    StpCarryableList = 0x40,
    StpCarryablePickupRequest = 0x41,
    StpCarryablePickupGranted = 0x42,
    StpCarryableDropRequest = 0x43,
    StpHarvestableList = 0x44,
    StpHarvestHitRequest = 0x45,
    // ADR-028 Fase E: host-authoritative corpse relay (same block — loot/world-object family)
    CorpseList = 0x46,
    CorpseSpawnRequest = 0x47,
    CorpseTakeRequest = 0x48,
    CorpseTakeResult = 0x49,
    // Reliability (0xF0-0xFF)
    Ack = 0xF0,
    Nack = 0xF1,
    Ping = 0xF2,
}

impl PacketType {
    pub fn from_u16(v: u16) -> Option<Self> {
        match v {
            0x00 => Some(Self::Discover),
            0x01 => Some(Self::PeerIntro),
            0x02 => Some(Self::Handshake),
            0x03 => Some(Self::HandshakeAck),
            0x04 => Some(Self::WorldSync),
            0x05 => Some(Self::Heartbeat),
            0x06 => Some(Self::Disconnect),
            0x07 => Some(Self::PeerList),
            0x10 => Some(Self::PlayerUpdate),
            0x11 => Some(Self::ChunkState),
            0x12 => Some(Self::ChunkDelta),
            0x13 => Some(Self::EntityUpdate),
            0x14 => Some(Self::StatUpdate),
            0x15 => Some(Self::InventorySync),
            0x16 => Some(Self::StpItemList),
            0x17 => Some(Self::StpPickupRequest),
            0x18 => Some(Self::StpPickupGranted),
            0x19 => Some(Self::StpDropRequest),
            0x1A => Some(Self::StpBuildingList),
            0x1B => Some(Self::StpPlaceRequest),
            0x1C => Some(Self::StpBuildAddRequest),
            0x20 => Some(Self::Interact),
            0x21 => Some(Self::Attack),
            0x22 => Some(Self::Pickup),
            0x23 => Some(Self::Drop),
            0x24 => Some(Self::Craft),
            0x25 => Some(Self::PlaceStabilizer),
            0x26 => Some(Self::PlaceAnchor),
            0x27 => Some(Self::RepairAnchor),
            0x28 => Some(Self::UseConsumable),
            0x30 => Some(Self::ChunkTransfer),
            0x31 => Some(Self::ChunkTransferAck),
            0x32 => Some(Self::ChunkTeleport),
            0x33 => Some(Self::ChunkGenerate),
            0x34 => Some(Self::AnchorBroadcast),
            0x35 => Some(Self::StabilizerBroadcast),
            0x40 => Some(Self::StpCarryableList),
            0x41 => Some(Self::StpCarryablePickupRequest),
            0x42 => Some(Self::StpCarryablePickupGranted),
            0x43 => Some(Self::StpCarryableDropRequest),
            0x44 => Some(Self::StpHarvestableList),
            0x45 => Some(Self::StpHarvestHitRequest),
            0x46 => Some(Self::CorpseList),
            0x47 => Some(Self::CorpseSpawnRequest),
            0x48 => Some(Self::CorpseTakeRequest),
            0x49 => Some(Self::CorpseTakeResult),
            0xF0 => Some(Self::Ack),
            0xF1 => Some(Self::Nack),
            0xF2 => Some(Self::Ping),
            _ => None,
        }
    }
}

// ─── Sync data types (shared between protocol payloads) ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerInfo {
    pub id: u16,
    pub name: String,
    pub addr: String,
    pub position: [f32; 3],
}

/// A host-authoritative STP world item instance, replicated to all peers so each
/// client reconstructs the same STP `ItemPickup` (`def_id` → `ItemDefinition` →
/// prefab). `id` is the network instance id (host-assigned, monotonic); `def_id`
/// is the STP `ItemDefinition` id (stable across instances). Phase 1.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StpItemInfo {
    pub id: u32,
    pub def_id: i32,
    pub count: u16,
    pub position: [f32; 3],
    pub rotation: f32,
}

/// A host-authoritative STP building piece, replicated to all peers so each client
/// reconstructs the same placed piece (`def_id` → `BuildingPieceDefinition` → prefab).
/// `id` is the network instance id (host-assigned, monotonic); `def_id` is the STP
/// `BuildingPieceDefinition` id (stable across instances). Phase B1.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StpBuildingInfo {
    pub id: u32,
    pub def_id: i32,
    pub position: [f32; 3],
    pub rotation: f32,
    /// Phase B3: host-authoritative group identity. All pieces of one structure share a
    /// `group_id` so every client buckets them into one `BuildingPieceGroup` and rebuilds
    /// the socket cohesion. `0` = standalone (free pieces, e.g. campfire). Host-assigned.
    #[serde(default)]
    pub group_id: u32,
    /// Phase B2: host-authoritative construction progress — how many units of each
    /// build material have been accepted for this piece. Clients derive completion by
    /// comparing against the prefab-authored required amounts. Omitted when empty.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub added: Vec<StpBuildProgress>,
}

/// Phase B2: one (material → accepted count) entry of a building piece's construction
/// progress. `material_id` is the STP `BuildMaterialDefinition` id (stable).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StpBuildProgress {
    pub material_id: i32,
    pub count: u16,
}

/// A host-authoritative STP world carryable (log/stone/metal pile), replicated to all
/// peers so each client reconstructs the same `CarryablePickup` (`def_id` →
/// `CarryableDefinition` → pickup prefab). `id` is the network instance id (host-assigned,
/// monotonic); `def_id` is the STP `CarryableDefinition` id (stable). Phase B2.5.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StpCarryableInfo {
    pub id: u32,
    pub def_id: i32,
    pub position: [f32; 3],
    pub rotation: f32,
}

/// A host-authoritative STP scene-placed harvestable (tree/rock prefab), replicated to all
/// peers so each client reflects the same construction-resource health. `id` is the network
/// instance id (host-assigned); clients map it to their local harvestable by position
/// proximity. `remaining` is the host-authoritative harvest amount (1.0 full → 0.0 depleted).
/// Phase B2.6.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StpHarvestableInfo {
    pub id: u32,
    pub position: [f32; 3],
    pub remaining: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SessionConfig {
    pub max_players: u16,
    pub world_name: String,
    pub teleport_interval_min: f32,
    pub teleport_interval_max: f32,
}

impl Default for SessionConfig {
    fn default() -> Self {
        Self {
            max_players: 50,
            world_name: "Backrooms".into(),
            teleport_interval_min: 120.0,
            teleport_interval_max: 600.0,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AnchorInfo {
    pub chunk_pos: [i32; 2],
    pub durability: f32,
    pub installed_by: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StabilizerInfo {
    pub chunk_pos: [i32; 2],
    pub tier: u8,
    pub remaining_hours: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChunkSyncData {
    pub pos: [i32; 2],
    #[serde(default)]
    pub layer: i8,
    pub seed: u64,
    pub template_id: u8,
    pub rotation: u16,
    pub mirrored: bool,
    pub has_workbench: bool,
    #[serde(default)]
    pub layout: ChunkLayoutV1,
    pub stabilized: bool,
    pub anchored: bool,
    pub teleport_timer: f32,
    pub entities: Vec<EntitySyncData>,
    pub items: Vec<ItemSyncData>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EntitySyncData {
    pub id: u32,
    pub entity_type: String,
    pub position: [f32; 3],
    pub rotation: f32,
    pub health: u8,
    pub state: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ItemSyncData {
    pub id: u32,
    pub item_type: String,
    pub quantity: u16,
    pub position: [f32; 3],
}

// ─── Packet payload (MessagePack body after the 12-byte header) ───

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PacketPayload {
    // Connection
    Handshake {
        player_name: String,
        version: String,
    },
    HandshakeAck {
        assigned_id: u16,
        world_seed: u64,
        config: SessionConfig,
        peers: Vec<PeerInfo>,
        anchors: Vec<AnchorInfo>,
        stabilizers: Vec<StabilizerInfo>,
    },
    WorldSync {
        world_seed: u64,
        world_revision: u64,
        chunks: Vec<ChunkSyncData>,
    },
    Heartbeat,
    Disconnect {
        reason: String,
    },
    PeerList {
        peers: Vec<PeerInfo>,
    },
    StpItemList {
        items: Vec<StpItemInfo>,
    },
    StpPickupRequest {
        item_id: u32,
        requester_id: u16,
    },
    StpPickupGranted {
        item_id: u32,
        def_id: i32,
        count: u16,
    },
    StpDropRequest {
        drop_id: u64,
        def_id: i32,
        count: u16,
        position: [f32; 3],
        rotation: f32,
    },
    StpBuildingList {
        buildings: Vec<StpBuildingInfo>,
    },
    StpPlaceRequest {
        place_id: u64,
        def_id: i32,
        position: [f32; 3],
        rotation: f32,
        /// Phase B3: the group the client attached to (0 = new group, host mints one).
        group_id: u32,
        /// Phase B3: true if the piece is a GroupBuildingPiece (sockets/cohesion); false
        /// for free pieces (no group, no pose-cell dedup — free pieces may stack).
        is_group: bool,
    },
    StpBuildAddRequest {
        add_id: u64,
        building_id: u32,
        material_id: i32,
    },

    // State
    PlayerUpdate {
        position: [f32; 3],
        rotation: f32,
        animation: String,
        /// ADR-020: cosmetic crouch (appended last + serde(default) → a v2 peer that
        /// omits it decodes to false; wire-compat across the v2→v3 schema bump).
        #[serde(default)]
        crouch: bool,
        /// ADR-021: cosmetic camera pitch in degrees (−90..90, quantized to 1°). Appended
        /// last + serde(default) → a v3 peer that omits it decodes to 0 (looking forward);
        /// wire-compat across the v3→v4 schema bump.
        #[serde(default)]
        pitch: i8,
        /// ADR-022: cosmetic worn clothing item IDs [Head, Torso, Legs, Feet] (0 = empty).
        /// Appended last + serde(default) → a v4 peer that omits it decodes to [0,0,0,0]
        /// (no clothing); wire-compat across the v4→v5 schema bump.
        #[serde(default)]
        equipment: [i32; 4],
        /// ADR-023: cosmetic held item ID (0 = empty hands). Appended last + serde(default) →
        /// a v5 peer that omits it decodes to 0 (empty hands); wire-compat across the v5→v6
        /// schema bump.
        #[serde(default)]
        held_item: i32,
        /// ADR-024: cosmetic hit-reaction counter (monotonic, wrapping; 0 = never hit). Appended
        /// last + serde(default) → a v6 peer that omits it decodes to 0 (no flinch); wire-compat
        /// across the v6→v7 schema bump.
        #[serde(default)]
        hit_seq: u8,
        /// ADR-028 post-E3: cosmetic dead flag, SERVER-derived on the owning backend
        /// (`player.stats.is_dead()`) — observers hide the standing proxy while dead. Appended
        /// last + serde(default) → a v9 peer that omits it decodes to false (never hides);
        /// wire-compat across the v9→v10 schema bump.
        #[serde(default)]
        dead: bool,
    },
    ChunkState {
        data: ChunkSyncData,
    },
    ChunkDelta {
        pos: [i32; 2],
        entities: Vec<EntitySyncData>,
        items: Vec<ItemSyncData>,
    },
    EntityUpdate {
        chunk_pos: [i32; 2],
        entities: Vec<EntitySyncData>,
    },

    // Actions
    Interact {
        requester_id: u16,
        request_id: u64,
        target_id: u32,
        target_kind: String,
        interaction_type: String,
        player_position: [f32; 3],
    },
    Attack {
        target_entity_id: Option<u32>,
    },
    Pickup {
        item_id: u32,
    },
    Drop {
        slot: u8,
        quantity: u16,
    },
    Craft {
        recipe: String,
    },
    PlaceStabilizer {
        slot: u8,
    },
    PlaceAnchor,

    // World
    ChunkTransfer {
        data: ChunkSyncData,
    },
    ChunkTransferAck {
        pos: [i32; 2],
    },
    ChunkTeleport {
        old_pos: [i32; 2],
        new_pos: [i32; 2],
        new_seed: u64,
    },
    AnchorBroadcast {
        chunk_pos: [i32; 2],
        durability: f32,
        installed_by: String,
    },
    StabilizerBroadcast {
        chunk_pos: [i32; 2],
        tier: u8,
        remaining_hours: f32,
    },
    StpCarryableList {
        carryables: Vec<StpCarryableInfo>,
    },
    StpCarryablePickupRequest {
        carryable_id: u32,
        requester_id: u16,
    },
    StpCarryablePickupGranted {
        carryable_id: u32,
        def_id: i32,
    },
    StpCarryableDropRequest {
        drop_id: u64,
        def_id: i32,
        position: [f32; 3],
        rotation: f32,
    },
    StpHarvestableList {
        harvestables: Vec<StpHarvestableInfo>,
    },
    StpHarvestHitRequest {
        hit_id: u64,
        harvestable_id: u32,
        amount: f32,
    },

    // ADR-028 Fase E: host-authoritative corpse relay. Corpses reuse the storage type
    // (`world::corpse::CorpseData`) directly on the wire — same precedent as ChunkLayoutV1.
    /// Host → all: the full authoritative corpse roster, broadcast at 10 Hz (self-healing,
    /// same pattern as StpItemList). Joiners mirror it verbatim into their `world.corpses`.
    CorpseList {
        corpses: Vec<crate::world::corpse::CorpseData>,
    },
    /// Joiner → host (reliable): "my player died with this loot snapshot — spawn the corpse".
    /// The host dedupes by (sender, request_id): reliable retransmits spawn exactly one corpse.
    CorpseSpawnRequest {
        request_id: u64,
        requester_id: u16,
        owner_name: String,
        position: [f32; 3],
        equipment: [i32; 4],
        held_item: i32,
        items: Vec<crate::world::corpse::CorpseStack>,
    },
    /// Joiner → host (reliable): "take `quantity` from stack `item_index` of corpse `corpse_id`".
    /// `requester_pos` is the claimed position (same trust level as Interact.player_position);
    /// the host validates it against the corpse's frozen death position. Deduped like spawn.
    CorpseTakeRequest {
        request_id: u64,
        requester_id: u16,
        corpse_id: u32,
        item_index: u32,
        quantity: u16,
        requester_pos: [f32; 3],
    },
    /// Host → requester (reliable): the verdict for one CorpseTakeRequest. The joiner backend
    /// dedupes by request_id and emits the SAME IPC event Fase D already consumes
    /// (corpse_item_taken / corpse_take_rejected) to its own Unity.
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

    // Reliability
    Ack {
        acked_sequence: u32,
    },
    Nack {
        requested_sequence: u32,
    },
    Ping {
        send_time: u32,
    },
}

impl PacketPayload {
    /// Returns the wire type code for this payload variant.
    pub fn type_code(&self) -> u16 {
        match self {
            Self::Handshake { .. } => PacketType::Handshake as u16,
            Self::HandshakeAck { .. } => PacketType::HandshakeAck as u16,
            Self::WorldSync { .. } => PacketType::WorldSync as u16,
            Self::Heartbeat => PacketType::Heartbeat as u16,
            Self::Disconnect { .. } => PacketType::Disconnect as u16,
            Self::PeerList { .. } => PacketType::PeerList as u16,
            Self::StpItemList { .. } => PacketType::StpItemList as u16,
            Self::StpPickupRequest { .. } => PacketType::StpPickupRequest as u16,
            Self::StpPickupGranted { .. } => PacketType::StpPickupGranted as u16,
            Self::StpDropRequest { .. } => PacketType::StpDropRequest as u16,
            Self::StpBuildingList { .. } => PacketType::StpBuildingList as u16,
            Self::StpPlaceRequest { .. } => PacketType::StpPlaceRequest as u16,
            Self::StpBuildAddRequest { .. } => PacketType::StpBuildAddRequest as u16,
            Self::PlayerUpdate { .. } => PacketType::PlayerUpdate as u16,
            Self::ChunkState { .. } => PacketType::ChunkState as u16,
            Self::ChunkDelta { .. } => PacketType::ChunkDelta as u16,
            Self::EntityUpdate { .. } => PacketType::EntityUpdate as u16,
            Self::Interact { .. } => PacketType::Interact as u16,
            Self::Attack { .. } => PacketType::Attack as u16,
            Self::Pickup { .. } => PacketType::Pickup as u16,
            Self::Drop { .. } => PacketType::Drop as u16,
            Self::Craft { .. } => PacketType::Craft as u16,
            Self::PlaceStabilizer { .. } => PacketType::PlaceStabilizer as u16,
            Self::PlaceAnchor => PacketType::PlaceAnchor as u16,
            Self::ChunkTransfer { .. } => PacketType::ChunkTransfer as u16,
            Self::ChunkTransferAck { .. } => PacketType::ChunkTransferAck as u16,
            Self::ChunkTeleport { .. } => PacketType::ChunkTeleport as u16,
            Self::AnchorBroadcast { .. } => PacketType::AnchorBroadcast as u16,
            Self::StabilizerBroadcast { .. } => PacketType::StabilizerBroadcast as u16,
            Self::StpCarryableList { .. } => PacketType::StpCarryableList as u16,
            Self::StpCarryablePickupRequest { .. } => PacketType::StpCarryablePickupRequest as u16,
            Self::StpCarryablePickupGranted { .. } => PacketType::StpCarryablePickupGranted as u16,
            Self::StpCarryableDropRequest { .. } => PacketType::StpCarryableDropRequest as u16,
            Self::StpHarvestableList { .. } => PacketType::StpHarvestableList as u16,
            Self::StpHarvestHitRequest { .. } => PacketType::StpHarvestHitRequest as u16,
            Self::CorpseList { .. } => PacketType::CorpseList as u16,
            Self::CorpseSpawnRequest { .. } => PacketType::CorpseSpawnRequest as u16,
            Self::CorpseTakeRequest { .. } => PacketType::CorpseTakeRequest as u16,
            Self::CorpseTakeResult { .. } => PacketType::CorpseTakeResult as u16,
            Self::Ack { .. } => PacketType::Ack as u16,
            Self::Nack { .. } => PacketType::Nack as u16,
            Self::Ping { .. } => PacketType::Ping as u16,
        }
    }
}

// ─── Wire encoding / decoding ───

/// Encode a packet: 12-byte header + MessagePack payload.
pub fn encode_packet(header: &PacketHeader, payload: &PacketPayload) -> Vec<u8> {
    let header_bytes = header.to_bytes();
    let payload_bytes = rmp_serde::to_vec_named(payload).expect("payload serialization");
    let mut buf = Vec::with_capacity(HEADER_SIZE + payload_bytes.len());
    buf.extend_from_slice(&header_bytes);
    buf.extend_from_slice(&payload_bytes);
    buf
}

/// Decode a packet from raw bytes: header + MessagePack payload.
pub fn decode_packet(data: &[u8]) -> Result<(PacketHeader, PacketPayload), String> {
    let header =
        PacketHeader::from_bytes(data).ok_or_else(|| "packet too short for header".to_string())?;
    let payload: PacketPayload =
        rmp_serde::from_slice(&data[HEADER_SIZE..]).map_err(|e| format!("payload decode: {e}"))?;
    Ok((header, payload))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn header_round_trip() {
        let h = PacketHeader::new(0x02, 1, 42, 1000);
        let bytes = h.to_bytes();
        let h2 = PacketHeader::from_bytes(&bytes).unwrap();
        assert_eq!(h, h2);
    }

    #[test]
    fn packet_type_from_u16_round_trip() {
        assert_eq!(
            PacketType::from_u16(PacketType::Handshake as u16),
            Some(PacketType::Handshake)
        );
        assert_eq!(
            PacketType::from_u16(PacketType::Ack as u16),
            Some(PacketType::Ack)
        );
        assert_eq!(PacketType::from_u16(0xFF), None);
    }

    #[test]
    fn handshake_packet_round_trip() {
        let payload = PacketPayload::Handshake {
            player_name: "TestPlayer".into(),
            version: "0.1.0".into(),
        };
        let header = PacketHeader::new(payload.type_code(), 1, 1, 100);
        let data = encode_packet(&header, &payload);
        let (h2, p2) = decode_packet(&data).unwrap();
        assert_eq!(h2.packet_type, PacketType::Handshake as u16);
        assert_eq!(h2.sender_id, 1);
        match p2 {
            PacketPayload::Handshake {
                player_name,
                version,
            } => {
                assert_eq!(player_name, "TestPlayer");
                assert_eq!(version, "0.1.0");
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn handshake_ack_round_trip() {
        let payload = PacketPayload::HandshakeAck {
            assigned_id: 2,
            world_seed: 42,
            config: SessionConfig::default(),
            peers: vec![PeerInfo {
                id: 1,
                name: "Host".into(),
                addr: "127.0.0.1:7778".into(),
                position: [0.0, 1.8, 0.0],
            }],
            anchors: vec![],
            stabilizers: vec![],
        };
        let header = PacketHeader::new(payload.type_code(), 1, 1, 200);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::HandshakeAck {
                assigned_id,
                world_seed,
                ..
            } => {
                assert_eq!(assigned_id, 2);
                assert_eq!(world_seed, 42);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn player_update_round_trip() {
        let payload = PacketPayload::PlayerUpdate {
            position: [10.0, 1.8, 20.0],
            rotation: 90.0,
            animation: "walk".into(),
            crouch: true,
            pitch: -45,
            equipment: [101, 202, 303, 404],
            held_item: 12345,
            hit_seq: 7,
            dead: true,
        };
        let header = PacketHeader::new(payload.type_code(), 3, 100, 5000);
        let data = encode_packet(&header, &payload);
        let (h2, p2) = decode_packet(&data).unwrap();
        assert_eq!(h2.sender_id, 3);
        match p2 {
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
            } => {
                assert_eq!(position, [10.0, 1.8, 20.0]);
                assert_eq!(rotation, 90.0);
                assert_eq!(animation, "walk");
                assert!(crouch);
                assert_eq!(pitch, -45);
                assert_eq!(equipment, [101, 202, 303, 404]);
                assert_eq!(held_item, 12345);
                assert_eq!(hit_seq, 7);
                assert!(dead);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn heartbeat_round_trip() {
        let payload = PacketPayload::Heartbeat;
        let header = PacketHeader::new(payload.type_code(), 1, 50, 3000);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        assert!(matches!(p2, PacketPayload::Heartbeat));
    }

    #[test]
    fn chunk_transfer_round_trip() {
        let payload = PacketPayload::ChunkTransfer {
            data: ChunkSyncData {
                pos: [3, 2],
                layer: 0,
                seed: 12345,
                template_id: 4,
                rotation: 90,
                mirrored: true,
                has_workbench: false,
                layout: ChunkLayoutV1::default(),
                stabilized: false,
                anchored: false,
                teleport_timer: 300.0,
                entities: vec![EntitySyncData {
                    id: 1,
                    entity_type: "lurker".into(),
                    position: [160.0, 0.0, 110.0],
                    rotation: 45.0,
                    health: 50,
                    state: "idle".into(),
                }],
                items: vec![ItemSyncData {
                    id: 10,
                    item_type: "metal".into(),
                    quantity: 1,
                    position: [155.0, 0.0, 115.0],
                }],
            },
        };
        let header = PacketHeader::new(payload.type_code(), 1, 200, 10000);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::ChunkTransfer { data } => {
                assert_eq!(data.pos, [3, 2]);
                assert_eq!(data.entities.len(), 1);
                assert_eq!(data.items.len(), 1);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn world_sync_round_trip_includes_seed_revision_and_counts() {
        let payload = PacketPayload::WorldSync {
            world_seed: 1234,
            world_revision: 7,
            chunks: vec![ChunkSyncData {
                pos: [0, 0],
                layer: 0,
                seed: 1234,
                template_id: 1,
                rotation: 0,
                mirrored: false,
                has_workbench: true,
                layout: ChunkLayoutV1::default(),
                stabilized: false,
                anchored: false,
                teleport_timer: 300.0,
                entities: vec![],
                items: vec![],
            }],
        };
        let header = PacketHeader::new(payload.type_code(), 1, 300, 10000);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::WorldSync {
                world_seed,
                world_revision,
                chunks,
            } => {
                assert_eq!(world_seed, 1234);
                assert_eq!(world_revision, 7);
                assert_eq!(chunks.len(), 1);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn interact_request_round_trip_includes_stable_ids() {
        let payload = PacketPayload::Interact {
            requester_id: 7,
            request_id: 7001,
            target_id: 42,
            target_kind: "item".into(),
            interaction_type: "pickup".into(),
            player_position: [1.0, 2.0, 3.0],
        };
        let header = PacketHeader::new(payload.type_code(), 7, 301, 10000);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::Interact {
                requester_id,
                request_id,
                target_id,
                target_kind,
                interaction_type,
                player_position,
            } => {
                assert_eq!(requester_id, 7);
                assert_eq!(request_id, 7001);
                assert_eq!(target_id, 42);
                assert_eq!(target_kind, "item");
                assert_eq!(interaction_type, "pickup");
                assert_eq!(player_position, [1.0, 2.0, 3.0]);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn ack_round_trip() {
        let payload = PacketPayload::Ack { acked_sequence: 42 };
        let header = PacketHeader::new(payload.type_code(), 2, 0, 100);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::Ack { acked_sequence } => assert_eq!(acked_sequence, 42),
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn type_code_matches_packet_type() {
        let p = PacketPayload::Handshake {
            player_name: "x".into(),
            version: "0.1.0".into(),
        };
        assert_eq!(p.type_code(), PacketType::Handshake as u16);

        let p = PacketPayload::Ack { acked_sequence: 0 };
        assert_eq!(p.type_code(), PacketType::Ack as u16);
    }

    // ADR-028 Fase E: all four corpse-relay payloads must round-trip, including negative
    // raw STP item ids (DataIdReference hashes) in every id-bearing field.
    #[test]
    fn corpse_payloads_round_trip() {
        use crate::utils::Vec3;
        use crate::world::corpse::{CorpseData, CorpseStack};

        let corpse = CorpseData {
            id: 7,
            owner_id: 1004,
            owner_name: "Joel".into(),
            position: Vec3::new(-22.3, 1.8, 9.7),
            equipment: [0, -2328174, -2864101, -3870361],
            held_item: -1159981804,
            items: vec![CorpseStack { item_id: -12345, quantity: 3 }],
        };

        let list = PacketPayload::CorpseList { corpses: vec![corpse.clone()] };
        let header = PacketHeader::new(list.type_code(), 1, 1, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &list)).unwrap();
        match decoded {
            PacketPayload::CorpseList { corpses } => {
                assert_eq!(corpses.len(), 1);
                assert_eq!(corpses[0].id, 7);
                assert_eq!(corpses[0].owner_name, "Joel");
                assert_eq!(corpses[0].held_item, -1159981804);
                assert_eq!(corpses[0].items[0].item_id, -12345);
            }
            _ => panic!("wrong variant"),
        }

        let spawn = PacketPayload::CorpseSpawnRequest {
            request_id: 42,
            requester_id: 1004,
            owner_name: "Joel".into(),
            position: [-22.3, 1.8, 9.7],
            equipment: [0, -2328174, -2864101, -3870361],
            held_item: -1159981804,
            items: vec![CorpseStack { item_id: 99, quantity: 1 }],
        };
        let header = PacketHeader::new(spawn.type_code(), 1004, 2, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &spawn)).unwrap();
        match decoded {
            PacketPayload::CorpseSpawnRequest { request_id, requester_id, items, .. } => {
                assert_eq!(request_id, 42);
                assert_eq!(requester_id, 1004);
                assert_eq!(items[0].item_id, 99);
            }
            _ => panic!("wrong variant"),
        }

        let take = PacketPayload::CorpseTakeRequest {
            request_id: 43,
            requester_id: 1004,
            corpse_id: 7,
            item_index: 2,
            quantity: 1,
            requester_pos: [-21.0, 1.8, 9.0],
        };
        let header = PacketHeader::new(take.type_code(), 1004, 3, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &take)).unwrap();
        match decoded {
            PacketPayload::CorpseTakeRequest { request_id, corpse_id, item_index, .. } => {
                assert_eq!(request_id, 43);
                assert_eq!(corpse_id, 7);
                assert_eq!(item_index, 2);
            }
            _ => panic!("wrong variant"),
        }

        let result = PacketPayload::CorpseTakeResult {
            request_id: 43,
            accepted: false,
            corpse_id: 7,
            item_index: 2,
            item_id: -12345,
            quantity: 0,
            corpse_empty: false,
            reason: "too_far distance=9.31".into(),
        };
        let header = PacketHeader::new(result.type_code(), 1, 4, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &result)).unwrap();
        match decoded {
            PacketPayload::CorpseTakeResult { accepted, item_id, reason, .. } => {
                assert!(!accepted);
                assert_eq!(item_id, -12345);
                assert!(reason.starts_with("too_far"));
            }
            _ => panic!("wrong variant"),
        }
    }
}
