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
use crate::world::grid_gen::RoomZone;
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
    /// ADR-046 — one encoded voice frame from the LOCAL player's microphone.
    ///
    /// A top-level variant rather than a `PlayerAction`, for the same reason as
    /// `RequestChunk`: actions carry a `action_type` string plus a nested map, and this
    /// travels 25 times a second while someone is speaking. `data` is opaque here — the
    /// backend never decodes audio, it only forwards bytes.
    Voice {
        seq: u16,
        /// `serde_bytes` is REQUIRED, not decoration: a bare `Vec<u8>` deserializes only from a
        /// msgpack array and rejects the bin the client writes, with
        /// `invalid type: byte array, expected a sequence`.
        #[serde(default, with = "serde_bytes")]
        data: Vec<u8>,
    },
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
    /// ADR-042: client-reported "my active wieldable is emitting light" — any enabled `Light`
    /// under it. Cosmetic, relayed to peers, not authoritative.
    #[serde(default)]
    pub light_on: bool,
    /// ADR-042: client-reported shot counter (monotonic, wrapping); incremented on each native
    /// `IFirearmTrigger.Shoot`. Cosmetic, relayed to peers, not authoritative — the phantom hears
    /// through the separate `report_noise` action (ADR-041), never through this.
    #[serde(default)]
    pub fire_seq: u8,
    /// ADR-044: client-reported melee-swing counter (monotonic, wrapping). Sampled as a RISING EDGE
    /// of `MeleeWeapon.IsUsing` — that class exposes no swing event, unlike `IFirearmTrigger.Shoot`.
    /// Cosmetic, relayed to peers, not authoritative: it does not feed the hit validation of ADR-029.
    #[serde(default)]
    pub melee_seq: u8,
    /// ADR-049: client-reported carry state — `carry_def` is the `CarryableDefinition` id being
    /// hauled (0 = empty hands), `carry_count` how many units. A LEVEL, not a counter. Client-origin
    /// on purpose: `process_stp_carryable_pickup` keeps no per-player carry state to derive it from,
    /// and the field concedes nothing — no material, no placement, no collision.
    #[serde(default)]
    pub carry_def: i32,
    #[serde(default)]
    pub carry_count: u8,
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
    /// ADR-046 — one encoded voice frame from a REMOTE peer, already filtered by
    /// distance at the host. Travels on its own broadcast channel, never on the one
    /// carrying world state (see `ipc::server::run`).
    PeerVoice(PeerVoice),
    /// ADR-061 — first frame of every IPC connection, before any world state. Never goes
    /// through a broadcast channel: `handle_connection` writes it straight to the socket.
    Hello(ServerHello),
}

/// ADR-061 — the schema revision this backend speaks, so Unity can refuse a desynced build
/// instead of decoding it into silent defaults (a failed `remote_players` parse is otherwise
/// indistinguishable from "no remote players", STABILITY_AUDIT_CURRENT.md R4).
///
/// Just the number: a build string would duplicate what the startup log already prints and
/// invite logic over strings. The client skips unknown keys, so adding fields here later stays
/// additive.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ServerHello {
    pub schema_version: u32,
}

/// ADR-046 — a voice frame on its way to the local Unity client. `peer_id` is the
/// speaker, so the client can attach the audio to that peer's proxy; `seq` is what
/// lets the receiver detect loss and reorder (the transport is deliberately
/// unreliable). `data` is opaque: the backend never decodes audio.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerVoice {
    pub peer_id: u16,
    pub seq: u16,
    /// See `ClientMessage::Voice::data` — same adapter, same reason, and here it also decides
    /// what goes OUT: without it this would serialize as an array of integers, ~1.5× the bytes
    /// and undecodable by the client's `ReadBin`.
    #[serde(default, with = "serde_bytes")]
    pub data: Vec<u8>,
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
    /// ADR-034 — rects de Fase 4 con su `RoomType`, en coordenadas de CELDA
    /// (2.5 m), NO de tile. Campo ADITIVO: `walls` queda intacto (su byte está
    /// lleno y blindado por ADR-033/Pillar), así que esto viaja aparte en vez
    /// de robar bits. Omitido del wire cuando está vacío (`num_open_zones == 0`)
    /// — un cliente sin soporte simplemente no ve la clave, mismo patrón que
    /// `volumetric_grid`/`vertical_debug_markers`.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub room_zones: Vec<RoomZone>,
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
    /// ADR-038: cosmetic "showing its real form" flag — true only while the robapieles (ADR-016)
    /// is in `Sprint` or `Statue`. Always false for a real player: it is BACKEND-derived and has
    /// no counterpart in `PlayerInput`, so no client can set it.
    #[serde(default)]
    pub revealed: bool,
    /// ADR-048: monotonic vocalisation counter (backend→Unity). `ProxyVocalHook` fires on a change.
    #[serde(default)]
    pub vocal_seq: u8,
    /// ADR-048: which voice. 0 reveal-scream, 1 search-shriek, 2 noise-grunt, 3 stalking-breath.
    #[serde(default)]
    pub vocal_kind: u8,
    /// ADR-042: cosmetic "this peer's held wieldable is lit" flag (host-relayed) — the observer
    /// enables a light on the proxy's held model.
    #[serde(default)]
    pub light_on: bool,
    /// ADR-042: cosmetic shot counter (monotonic, wrapping; 0 = never fired), host-relayed. The
    /// observer plays the gunshot on a DELTA, so a full-auto burst that outruns the 10 Hz relay
    /// still lands the right number of shots.
    #[serde(default)]
    pub fire_seq: u8,
    /// ADR-044: cosmetic sustained-state bitfield, host-relayed — bit 0 = aiming, bit 1 = reloading.
    #[serde(default)]
    pub buttons: u16,
    /// ADR-044: cosmetic melee-swing counter (monotonic, wrapping; 0 = never swung), host-relayed.
    #[serde(default)]
    pub melee_seq: u8,
    /// ADR-049: cosmetic carry state, host-relayed. `ProxyCarryHook` renders `carry_count` copies of
    /// `carry_def`'s pickup on the peer's left hand.
    #[serde(default)]
    pub carry_def: i32,
    #[serde(default)]
    pub carry_count: u8,
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

    /// ADR-046 Fase 1 — the byte contract with `MsgPackWriter.WriteBin`, pinned against a
    /// HAND-BUILT frame rather than a round-trip through this same serializer. A round-trip
    /// would pass even if both halves agreed on a shape Unity does not emit; these are the
    /// exact bytes the C# writer produces for `{type:"voice", seq, data:<bin>}`.
    fn unity_voice_frame(seq: u16, payload: &[u8]) -> Vec<u8> {
        let mut f = vec![0x83]; // fixmap, 3 entries
        f.push(0xa4);
        f.extend_from_slice(b"type");
        f.push(0xa5);
        f.extend_from_slice(b"voice");
        f.push(0xa3);
        f.extend_from_slice(b"seq");
        f.push(0xcd); // uint16
        f.extend_from_slice(&seq.to_be_bytes());
        f.push(0xa4);
        f.extend_from_slice(b"data");
        // bin8/bin16, exactly as WriteBin chooses the width.
        if payload.len() <= 0xff {
            f.push(0xc4);
            f.push(payload.len() as u8);
        } else {
            f.push(0xc5);
            f.extend_from_slice(&(payload.len() as u16).to_be_bytes());
        }
        f.extend_from_slice(payload);
        f
    }

    #[test]
    fn voice_frame_from_unity_decodes_with_its_bytes_intact() {
        let payload: Vec<u8> = (0..120u16).map(|i| (i * 31 + 7) as u8).collect();
        let frame = unity_voice_frame(0x1234, &payload);

        match decode::<ClientMessage>(&frame).expect("Unity's bin encoding must decode") {
            ClientMessage::Voice { seq, data } => {
                assert_eq!(seq, 0x1234);
                assert_eq!(data, payload, "audio bytes must survive byte for byte");
            }
            other => panic!("decoded as the wrong variant: {other:?}"),
        }
    }

    #[test]
    fn voice_frame_survives_the_bin8_to_bin16_boundary() {
        // 255/256 is where WriteBin switches header width; a decoder that only handled bin8
        // would work in every hand test (a voice frame is ~120 B) and break on the first
        // burst that packs more.
        for len in [0usize, 1, 255, 256, 1024] {
            let payload: Vec<u8> = (0..len).map(|i| (i % 251) as u8).collect();
            match decode::<ClientMessage>(&unity_voice_frame(7, &payload)).unwrap() {
                ClientMessage::Voice { data, .. } => assert_eq!(data.len(), len, "len {len}"),
                other => panic!("wrong variant at len {len}: {other:?}"),
            }
        }
    }

    #[test]
    fn peer_voice_encodes_as_binary_not_as_an_array_of_numbers() {
        // The difference is not cosmetic: an array of 120 integers costs ~1.5× the bytes of a
        // 120 B bin (every value ≥ 128 needs a 2-byte uint8 token), and the client's ReadBin
        // would reject it.
        let msg = ServerMessage::PeerVoice(PeerVoice {
            peer_id: 3,
            seq: 9,
            data: vec![0xff; 40],
        });
        let frame = encode(&msg).expect("PeerVoice must encode");
        let body = &frame[4..];
        assert!(
            body.windows(2).any(|w| w == [0xc4, 40]),
            "expected a bin8 header of length 40 in the encoded body"
        );

        match decode::<ServerMessage>(body).expect("PeerVoice must round-trip") {
            ServerMessage::PeerVoice(v) => {
                assert_eq!(v.peer_id, 3);
                assert_eq!(v.seq, 9);
                assert_eq!(v.data, vec![0xff; 40]);
            }
            other => panic!("decoded as the wrong variant: {other:?}"),
        }
    }

    #[test]
    fn a_flooded_voice_channel_cannot_drop_world_state_messages() {
        // The whole reason ADR-046 gives voice its own broadcast channel: `state_tx` drops its
        // OLDEST messages when it overflows, Events (`player_died`) included. This asserts the
        // isolation directly — overrun the voice channel by 4× and check the state subscriber
        // still receives every message, in order.
        let (state_tx, mut state_rx) = tokio::sync::broadcast::channel::<ServerMessage>(8);
        let (voice_tx, _voice_rx) = tokio::sync::broadcast::channel::<ServerMessage>(4);
        let mut voice_rx = voice_tx.subscribe();

        for i in 0..4u16 {
            state_tx
                .send(ServerMessage::Event(GameEvent {
                    event_type: "player_died".into(),
                    data: serde_json::json!({ "n": i }),
                }))
                .expect("a live subscriber exists");
        }
        for i in 0..16u16 {
            let _ = voice_tx.send(ServerMessage::PeerVoice(PeerVoice {
                peer_id: 1,
                seq: i,
                data: vec![0; 4],
            }));
        }

        for i in 0..4u16 {
            match state_rx.try_recv() {
                Ok(ServerMessage::Event(e)) => {
                    assert_eq!(e.event_type, "player_died");
                    assert_eq!(e.data.get("n").and_then(|v| v.as_u64()), Some(i as u64));
                }
                other => panic!("world state message {i} was lost or reordered: {other:?}"),
            }
        }
        // And the voice channel DID lag — otherwise this test would be proving nothing.
        assert!(
            matches!(
                voice_rx.try_recv(),
                Err(tokio::sync::broadcast::error::TryRecvError::Lagged(_))
            ),
            "the voice channel was expected to overflow; without that the isolation is untested"
        );
    }

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

    /// ADR-061: `IPCClient.Dispatch` reads "type" as the FIRST key and drops the frame if it
    /// isn't — that assumption is serde-derive's internally-tagged codegen, not an observed
    /// convention, so the hello is pinned byte-for-byte here the way the voice frame is. A
    /// future field added to `ServerHello` must not push the tag out of first position.
    #[test]
    fn hello_frame_puts_the_type_tag_first_on_the_wire() {
        let frame = encode(&ServerMessage::Hello(ServerHello { schema_version: 26 })).unwrap();

        let body = &frame[4..];
        assert_eq!(
            u32::from_be_bytes(frame[..4].try_into().unwrap()) as usize,
            body.len(),
            "length prefix must match the body"
        );

        let mut expected = vec![0x82]; // fixmap, 2 entries
        expected.push(0xa4);
        expected.extend_from_slice(b"type");
        expected.push(0xa5);
        expected.extend_from_slice(b"hello");
        expected.push(0xae);
        expected.extend_from_slice(b"schema_version");
        expected.push(26); // positive fixint

        assert_eq!(body, expected.as_slice());
    }
}
