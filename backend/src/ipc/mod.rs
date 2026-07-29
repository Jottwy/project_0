//! Local IPC between the Unity client and this Rust backend.
//!
//! Transport: TCP on 127.0.0.1:7777.
//! Framing:   4-byte big-endian length prefix, then a MessagePack body.
//! Encoding:  MessagePack via `rmp_serde::to_vec_named` (maps keyed by field
//!            name), so the C# side (MessagePack-CSharp, keyAsPropertyName) and
//!            the internally-tagged enums below line up.
//!
//! This module defines the wire schema; the server lives in `ipc::server`.
//! Schema mirrors CLAUDE_CODE_INSTRUCTIONS.md "MessagePack Schema".

pub mod server;

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::world::chunk::InterLayerVolumeV0;
use crate::world::graph::verticality::VerticalDebugMarkerV0;
use crate::world::volumetric_grid::VolumetricGridViewV0;

pub const DEFAULT_IPC_ADDR: &str = "127.0.0.1:7777";

pub fn resolve_ipc_addr() -> String {
    if let Ok(addr) = std::env::var("IPC_ADDR") {
        if !addr.trim().is_empty() {
            return addr;
        }
    }

    if let Ok(port) = std::env::var("IPC_PORT") {
        if !port.trim().is_empty() {
            return format!("127.0.0.1:{}", port.trim());
        }
    }

    DEFAULT_IPC_ADDR.to_string()
}

// ───────────────────────── Unity → Rust ─────────────────────────

/// Anything the Unity client can send to the backend.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ClientMessage {
    /// Per-frame movement / look / queued actions.
    Input(PlayerInput),
    /// A discrete gameplay action (craft, pickup, attack, …).
    Action(PlayerAction),
    /// UI lifecycle events (pause, save, quit, …).
    UiEvent(UiEvent),
    /// Fase 4.1 — ask the backend to generate one chunk via `grid_gen` and return
    /// it as a 5 m tile-wall bitmask (see `ServerMessage::ChunkData`). Independent
    /// of the legacy `world/` ChunkView path.
    RequestChunk { cx: i32, cz: i32, layer: u8 },
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct PlayerInput {
    pub movement: [f32; 3], // normalized world-space direction (legacy path)
    pub look_delta: [f32; 2],
    pub sprint: bool,
    #[serde(default)]
    pub actions: Vec<String>,

    // ADR-009 client-prediction fields. Optional (serde default) so a legacy
    // movement-direction client still decodes; the STP client sends an
    // authoritative pose for server validation (Option B). When `input_seq != 0`
    // the game loop takes the prediction path instead of integrating `movement`.
    #[serde(default)]
    pub input_seq: u32,
    #[serde(default)]
    pub client_tick: u32,
    #[serde(default)]
    pub position: [f32; 3],
    #[serde(default)]
    pub velocity: [f32; 3],
    #[serde(default)]
    pub move_state: u8, // 0 idle, 1 walk, 2 run, 3 crouch, 4 jump
    #[serde(default)]
    pub look: [f32; 2], // pitch, yaw — INPUT, not server-corrected (ADR-009 §8)
    #[serde(default)]
    pub buttons: u16,
    /// ADR-020: client-reported crouch (cosmetic; relayed to peers, not authoritative).
    #[serde(default)]
    pub crouch: bool,
    /// ADR-022: client-reported worn clothing item IDs [Head, Torso, Legs, Feet] (0 = empty);
    /// cosmetic, relayed to peers, not authoritative.
    #[serde(default)]
    pub equipment: [i32; 4],
    /// ADR-023: client-reported held item ID (0 = empty hands); cosmetic, relayed to peers,
    /// not authoritative.
    #[serde(default)]
    pub held_item: i32,
    /// ADR-024: client-reported hit-reaction counter (monotonic, wrapping); incremented on each
    /// local DamageReceived. Cosmetic, relayed to peers, not authoritative.
    #[serde(default)]
    pub hit_seq: u8,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PlayerAction {
    pub action_type: String,
    #[serde(default)]
    pub data: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UiEvent {
    pub event_type: String,
}

// ───────────────────────── Rust → Unity ─────────────────────────

/// Anything the backend can push to the Unity client.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ServerMessage {
    /// Full renderable snapshot (stats/chunks/entities), sent at the slow cadence.
    WorldState(WorldState),
    /// ADR-009 §2: 20 Hz movement-domain delta — authoritative pose + input ack,
    /// consumed by the client reconciler. Separate from the full WorldState.
    DeltaUpdate(MovementDelta),
    /// Immediate one-off event (chunk_teleported, entity_killed, …).
    Event(GameEvent),
    /// Result of a requested action.
    ActionResult(ActionResult),
    /// Fase 4.1 — minimal grid_gen chunk payload (reply to `RequestChunk`).
    ChunkData(GridChunkData),
}

/// Fase 4.1 — minimal backend-authoritative chunk: a 10×10 grid of 5 m tiles,
/// each a wall bitmask. Derived from grid_gen's 20×20 grid of 2.5 m cells by
/// `crate::world::grid_gen::chunk_tile_walls`. This is the NEW clean world path;
/// it shares nothing with the legacy `world/` `ChunkView`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GridChunkData {
    pub cx: i32,
    pub cz: i32,
    pub layer: u8,
    /// `walls[x][z]`: per-tile bitmask. Low nibble (`0x0F`) = edge walls: N=1
    /// (−Z), S=2 (+Z), E=4 (+X), W=8 (−X). High nibble (`0x10..0x80`, ADR-033/
    /// Pillar enmienda "Opción (c)") = which of the tile's four 2.5 m sub-cells
    /// is `CellType::Pillar`: `0x10` NW, `0x20` NE, `0x40` SW, `0x80` SE. The
    /// Unity consumer MUST use this same axis convention and bit mapping or Z
    /// will mirror / pillars will render in the wrong sub-cell.
    pub walls: [[u8; 10]; 10],
}

/// ADR-009 §2 DeltaUpdate payload: the 20 Hz authoritative movement state the
/// client reconciler needs — position to detect desync, velocity to correct to
/// immediately (amended §5), and `ack_input_seq` to align with its input buffer.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MovementDelta {
    pub tick: u64,
    pub ack_input_seq: u32,
    pub position: [f32; 3],
    pub velocity: [f32; 3],
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldState {
    pub tick: u64,
    pub world_seed: u64,
    pub world_revision: u64,
    pub local_player: LocalPlayerState,
    pub remote_players: Vec<RemotePlayerState>,
    pub visible_chunks: Vec<ChunkView>,
    pub visible_entities: Vec<EntityView>,
    pub visible_items: Vec<ItemView>,
    /// Debug placeholders for the parallel verticality layer (Phase 6.6).
    /// Optional and omitted when empty so the wire stays backward compatible.
    /// Render-as-debug only: no collision, no traversal, no gameplay authority.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub vertical_debug_markers: Vec<VerticalDebugMarkerV0>,

    /// Phase 1 — host-authoritative STP world items, replicated to all peers.
    /// Omitted from the wire when empty (backward compatible).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub stp_items: Vec<crate::network::protocol::StpItemInfo>,

    /// Phase B1 — host-authoritative STP building pieces, replicated to all peers.
    /// Omitted from the wire when empty (backward compatible).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub stp_buildings: Vec<crate::network::protocol::StpBuildingInfo>,

    /// Phase B2.5 — host-authoritative STP world carryables, replicated to all peers.
    /// Omitted from the wire when empty (backward compatible).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub stp_carryables: Vec<crate::network::protocol::StpCarryableInfo>,

    /// Phase B2.6 — host-authoritative STP scene harvestables (health), replicated to peers.
    /// Omitted from the wire when empty (backward compatible).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub stp_harvestables: Vec<crate::network::protocol::StpHarvestableInfo>,

    /// ADR-028 — lootable corpses near the player (global storage in `World::corpses`,
    /// filtered by proximity for bandwidth only — the map itself is never pruned).
    /// Omitted from the wire when empty (backward compatible: a v7 client never sees it).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub visible_corpses: Vec<CorpseView>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LocalPlayerState {
    pub position: [f32; 3],
    pub rotation: f32,
    pub stats: StatsView,
    pub speed_modifier: f32,
    pub inventory_changed: bool,
    /// ADR-009: echo of the last client `input_seq` the server has applied, so
    /// the client reconciler can compare authoritative pose vs. its prediction.
    #[serde(default)]
    pub ack_input_seq: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StatsView {
    pub health: f32,
    pub hunger: f32,
    pub thirst: f32,
    pub sanity: f32,
    /// ADR-009: server-authoritative stamina, interpolated client-side at 5 Hz.
    pub stamina: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RemotePlayerState {
    pub id: u16,
    pub name: String,
    pub position: [f32; 3],
    pub rotation: f32,
    pub animation: String,
    /// ADR-020: cosmetic crouch state of this remote player (host-relayed).
    #[serde(default)]
    pub crouch: bool,
    /// ADR-021: cosmetic camera pitch in degrees (−90..90, quantized to 1°), host-relayed.
    #[serde(default)]
    pub pitch: i8,
    /// ADR-022: cosmetic worn clothing item IDs [Head, Torso, Legs, Feet] (0 = empty), host-relayed.
    #[serde(default)]
    pub equipment: [i32; 4],
    /// ADR-023: cosmetic held item ID (0 = empty hands), host-relayed.
    #[serde(default)]
    pub held_item: i32,
    /// ADR-024: cosmetic hit-reaction counter (monotonic, wrapping; 0 = never hit), host-relayed.
    #[serde(default)]
    pub hit_seq: u8,
    /// ADR-028 post-E3: cosmetic dead flag (server-derived on the owning backend) — the client
    /// hides this peer's standing proxy while true (its corpse is the visible body).
    #[serde(default)]
    pub dead: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChunkView {
    pub chunk_schema: u8,
    pub pos: [i32; 2],
    #[serde(default)]
    pub layer: i8,
    pub layer_y: f32,
    pub template_id: u8,
    pub rotation: u16,
    pub mirrored: bool,
    pub state: String,
    pub has_workbench: bool,
    pub layout_grid_size: u8,
    pub layout_cell_size: f32,
    pub layout_cells: Vec<u16>,
    pub edge_openings: u8,
    pub macro_id: u32,
    pub zone_kind: u8,
    pub macro_local: [u8; 2],
    pub macro_size: [u8; 2],
    pub floor_level: i8,
    pub floor_profile: u8,
    pub ceiling_profile: u8,
    pub light_profile: u8,
    pub anomaly_flags: u16,
    pub vertical_flags: u16,
    #[serde(default)]
    pub inter_layer_volumes: Vec<InterLayerVolumeV0>,
    /// Backend-authored volumetric "Rubik grid" architecture (Volumetric V0).
    /// Present only on the near-spawn showcase host chunk; omitted otherwise so
    /// the wire stays backward compatible and unchanged for normal chunks.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub volumetric_grid: Option<VolumetricGridViewV0>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EntityView {
    pub id: u32,
    pub entity_type: String,
    pub position: [f32; 3],
    pub rotation: f32,
    pub state: String,
    pub health_pct: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ItemView {
    pub id: u32,
    pub item_type: String,
    pub position: [f32; 3],
    pub quantity: u16,
}

/// ADR-028 — one loot stack of a corpse. `item_id` is the raw STP item id
/// (`DataIdReference` hash, may be NEGATIVE — same scheme as `equipment`/`held_item`,
/// ADR-022/023), NOT the legacy backend `Item` enum.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ItemStackView {
    pub item_id: i32,
    pub quantity: u16,
}

/// ADR-028 — a lootable corpse. `position` is the server-frozen death position (the
/// loot interaction point); the client-side ragdoll is cosmetic and never moves it.
/// `equipment`/`held_item` are the cosmetic snapshot that dresses the ragdoll.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CorpseView {
    pub id: u32,
    pub owner_id: u32,
    pub owner_name: String,
    pub position: [f32; 3],
    pub equipment: [i32; 4],
    pub held_item: i32,
    pub items: Vec<ItemStackView>,
    /// ADR-028 amendment (world chests): crate visual + no dead-player owner client-side.
    #[serde(default)]
    pub is_chest: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GameEvent {
    pub event_type: String,
    #[serde(default)]
    pub data: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ActionResult {
    pub success: bool,
    pub action: String,
    #[serde(default)]
    pub result: Value,
}

// ───────────────────────── Codec helpers ─────────────────────────

/// Encode a server message to a length-prefixed MessagePack frame.
pub fn encode<T: Serialize>(msg: &T) -> Result<Vec<u8>, rmp_serde::encode::Error> {
    let body = rmp_serde::to_vec_named(msg)?;
    let mut frame = Vec::with_capacity(4 + body.len());
    frame.extend_from_slice(&(body.len() as u32).to_be_bytes());
    frame.extend_from_slice(&body);
    Ok(frame)
}

/// Decode a MessagePack frame body (without the length prefix).
pub fn decode<T: for<'de> Deserialize<'de>>(body: &[u8]) -> Result<T, rmp_serde::decode::Error> {
    rmp_serde::from_slice(body)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn player_died_and_respawned_events_encode_with_type_tag() {
        // ADR-025 Slice B diagnosis: prove the death/respawn GameEvents SERIALIZE (encode must
        // not fail on the internally-tagged enum + serde_json::Value payload) and carry the
        // "type":"event" tag + fields the Unity Dispatch switch matches on.
        // ADR-032: session_restored is the third position-arming event (hydration snap) and
        // must keep the exact same wire shape the applier parses.
        for (event_type, key) in [
            ("player_died", "death_pos"),
            ("player_respawned", "position"),
            ("session_restored", "position"),
        ] {
            let msg = ServerMessage::Event(GameEvent {
                event_type: event_type.into(),
                data: serde_json::json!({ key: [22.5f32, 1.8, 22.5] }),
            });
            let frame =
                encode(&msg).unwrap_or_else(|e| panic!("{event_type} failed to encode: {e}"));
            // Decode the body as a generic msgpack map (as Unity's reader does) and check the tag.
            let val: serde_json::Value = rmp_serde::from_slice(&frame[4..])
                .unwrap_or_else(|e| panic!("{event_type} body not a decodable map: {e}"));
            assert_eq!(val.get("type").and_then(|v| v.as_str()), Some("event"));
            assert_eq!(
                val.get("event_type").and_then(|v| v.as_str()),
                Some(event_type)
            );
            assert!(
                val.get("data").and_then(|d| d.get(key)).is_some(),
                "{event_type} data missing {key}"
            );
        }
    }

    #[test]
    fn server_message_round_trips() {
        let msg = ServerMessage::WorldState(WorldState {
            tick: 42,
            world_seed: 42,
            world_revision: 1,
            local_player: LocalPlayerState {
                position: [1.0, 1.8, 2.0],
                rotation: 90.0,
                stats: StatsView {
                    health: 100.0,
                    hunger: 60.0,
                    thirst: 45.0,
                    sanity: 70.0,
                    stamina: 100.0,
                },
                speed_modifier: 1.0,
                inventory_changed: false,
                ack_input_seq: 0,
            },
            remote_players: vec![],
            visible_chunks: vec![],
            visible_entities: vec![],
            visible_items: vec![],
            vertical_debug_markers: vec![],
            stp_items: vec![],
            stp_buildings: vec![],
            stp_carryables: vec![],
            stp_harvestables: vec![],
            visible_corpses: vec![],
        });
        let frame = encode(&msg).unwrap();
        // Strip the 4-byte length prefix before decoding the body.
        let decoded: ServerMessage = decode(&frame[4..]).unwrap();
        match decoded {
            ServerMessage::WorldState(ws) => assert_eq!(ws.tick, 42),
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn world_state_with_chunks_and_entities_round_trips() {
        let msg = ServerMessage::WorldState(WorldState {
            tick: 100,
            world_seed: 42,
            world_revision: 7,
            local_player: LocalPlayerState {
                position: [10.0, 1.8, 20.0],
                rotation: 45.0,
                stats: StatsView {
                    health: 80.0,
                    hunger: 50.0,
                    thirst: 40.0,
                    sanity: 30.0,
                    stamina: 65.0,
                },
                speed_modifier: 0.7,
                inventory_changed: true,
                ack_input_seq: 0,
            },
            remote_players: vec![],
            visible_chunks: vec![ChunkView {
                chunk_schema: 2,
                pos: [0, 0],
                layer: 0,
                layer_y: 0.0,
                template_id: 3,
                rotation: 90,
                mirrored: true,
                state: "random".into(),
                has_workbench: true,
                layout_grid_size: crate::world::chunk::LAYOUT_GRID_SIZE,
                layout_cell_size: crate::world::chunk::LAYOUT_CELL_SIZE,
                layout_cells: vec![crate::world::chunk::CELL_WALKABLE; 100],
                edge_openings: crate::world::chunk::EDGE_NORTH
                    | crate::world::chunk::EDGE_EAST
                    | crate::world::chunk::EDGE_SOUTH
                    | crate::world::chunk::EDGE_WEST,
                macro_id: 0,
                zone_kind: crate::world::chunk::ZONE_NORMAL,
                macro_local: [0, 0],
                macro_size: [1, 1],
                floor_level: 0,
                floor_profile: crate::world::chunk::FLOOR_FLAT,
                ceiling_profile: crate::world::chunk::CEILING_NORMAL,
                light_profile: crate::world::chunk::LIGHT_NORMAL,
                anomaly_flags: 0,
                vertical_flags: 0,
                inter_layer_volumes: vec![],
                volumetric_grid: None,
            }],
            visible_entities: vec![EntityView {
                id: 1,
                entity_type: "lurker".into(),
                position: [12.0, 0.0, 22.0],
                rotation: 180.0,
                state: "idle".into(),
                health_pct: 1.0,
            }],
            visible_items: vec![ItemView {
                id: 10,
                item_type: "metal".into(),
                position: [15.0, 0.0, 18.0],
                quantity: 1,
            }],
            vertical_debug_markers: vec![VerticalDebugMarkerV0 {
                id: 9001,
                kind: "stair".into(),
                world_min: [30.0, 0.0, 30.0],
                world_max: [50.0, 20.0, 50.0],
            }],
            stp_items: vec![],
            stp_buildings: vec![],
            stp_carryables: vec![],
            stp_harvestables: vec![],
            // ADR-028: negative item_id (raw STP DataIdReference hash) must round-trip.
            visible_corpses: vec![CorpseView {
                id: 3,
                owner_id: 7,
                owner_name: "Joel".into(),
                position: [22.5, 1.8, 22.5],
                equipment: [101, 0, -303, 404],
                held_item: -12345,
                items: vec![
                    ItemStackView {
                        item_id: -12345,
                        quantity: 3,
                    },
                    ItemStackView {
                        item_id: 99,
                        quantity: 1,
                    },
                ],
                is_chest: false,
            }],
        });
        let frame = encode(&msg).unwrap();
        let decoded: ServerMessage = decode(&frame[4..]).unwrap();
        match decoded {
            ServerMessage::WorldState(ws) => {
                assert_eq!(ws.tick, 100);
                assert_eq!(ws.world_seed, 42);
                assert_eq!(ws.world_revision, 7);
                assert_eq!(ws.visible_chunks.len(), 1);
                assert_eq!(ws.visible_chunks[0].template_id, 3);
                assert_eq!(ws.visible_entities.len(), 1);
                assert_eq!(ws.visible_entities[0].entity_type, "lurker");
                assert_eq!(ws.visible_items.len(), 1);
                assert_eq!(ws.visible_items[0].item_type, "metal");
                assert_eq!(ws.vertical_debug_markers.len(), 1);
                assert_eq!(ws.vertical_debug_markers[0].id, 9001);
                assert_eq!(ws.vertical_debug_markers[0].kind, "stair");
                assert_eq!(ws.visible_corpses.len(), 1);
                let corpse = &ws.visible_corpses[0];
                assert_eq!(corpse.id, 3);
                assert_eq!(corpse.owner_id, 7);
                assert_eq!(corpse.owner_name, "Joel");
                assert_eq!(corpse.equipment, [101, 0, -303, 404]);
                assert_eq!(corpse.held_item, -12345);
                assert_eq!(corpse.items.len(), 2);
                assert_eq!(corpse.items[0].item_id, -12345);
                assert_eq!(corpse.items[0].quantity, 3);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn client_message_round_trips() {
        let msg = ClientMessage::Input(PlayerInput {
            movement: [0.0, 0.0, 1.0],
            look_delta: [0.5, -0.1],
            sprint: true,
            actions: vec!["interact".into()],
            ..Default::default()
        });
        let body = rmp_serde::to_vec_named(&msg).unwrap();
        let decoded: ClientMessage = decode(&body).unwrap();
        match decoded {
            ClientMessage::Input(input) => {
                assert!(input.sprint);
                assert_eq!(input.movement[2], 1.0);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn movement_delta_round_trips() {
        let msg = ServerMessage::DeltaUpdate(MovementDelta {
            tick: 240,
            ack_input_seq: 57,
            position: [12.0, 1.8, -4.0],
            velocity: [0.0, 0.0, 5.0],
        });
        let frame = encode(&msg).unwrap();
        let decoded: ServerMessage = decode(&frame[4..]).unwrap();
        match decoded {
            ServerMessage::DeltaUpdate(d) => {
                assert_eq!(d.tick, 240);
                assert_eq!(d.ack_input_seq, 57);
                assert_eq!(d.velocity[2], 5.0);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn inter_layer_volume_kind_encodes_as_string_for_unity() {
        let body =
            rmp_serde::to_vec_named(&crate::world::chunk::InterLayerVolumeKindV0::ServiceShaft)
                .unwrap();
        let decoded: serde_json::Value = rmp_serde::from_slice(&body).unwrap();
        assert_eq!(decoded, serde_json::json!("SERVICE_SHAFT"));
    }
}
