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
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct PlayerInput {
    pub movement: [f32; 3], // normalized world-space direction
    pub look_delta: [f32; 2],
    pub sprint: bool,
    #[serde(default)]
    pub actions: Vec<String>,
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
    /// Full renderable snapshot, sent at 10hz.
    WorldState(WorldState),
    /// Immediate one-off event (chunk_teleported, entity_killed, …).
    Event(GameEvent),
    /// Result of a requested action.
    ActionResult(ActionResult),
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
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LocalPlayerState {
    pub position: [f32; 3],
    pub rotation: f32,
    pub stats: StatsView,
    pub speed_modifier: f32,
    pub inventory_changed: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StatsView {
    pub health: f32,
    pub hunger: f32,
    pub thirst: f32,
    pub sanity: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RemotePlayerState {
    pub id: u16,
    pub name: String,
    pub position: [f32; 3],
    pub rotation: f32,
    pub animation: String,
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
                },
                speed_modifier: 1.0,
                inventory_changed: false,
            },
            remote_players: vec![],
            visible_chunks: vec![],
            visible_entities: vec![],
            visible_items: vec![],
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
                },
                speed_modifier: 0.7,
                inventory_changed: true,
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
}
