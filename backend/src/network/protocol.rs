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

    // State
    PlayerUpdate {
        position: [f32; 3],
        rotation: f32,
        animation: String,
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
            } => {
                assert_eq!(position, [10.0, 1.8, 20.0]);
                assert_eq!(rotation, 90.0);
                assert_eq!(animation, "walk");
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
}
