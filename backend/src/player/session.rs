//! Per-connection player state (a peer's view of its own local player).
//! See ARCHITECTURE_V1.md §11.1.

use serde::{Deserialize, Serialize};
use uuid::Uuid;

use crate::network::PeerId;
use crate::player::inventory::{Inventory, StabilizerTier};
use crate::player::stats::PlayerStats;
use crate::utils::{ChunkPos, Vec3};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Player {
    pub id: PeerId,
    pub uuid: Uuid,
    pub name: String,
    pub position: Vec3,
    /// Yaw in degrees (Unity Y-axis rotation).
    pub rotation: f32,
    pub stats: PlayerStats,
    pub inventory: Inventory,
    pub equipped_stabilizer: Option<StabilizerTier>,
    pub owned_chunks: Vec<ChunkPos>,
    /// ADR-020: cosmetic crouch state reported by the client, relayed to peers
    /// (presentation only — not validated, does not affect collision/hitreg/stamina).
    #[serde(default)]
    pub crouch: bool,
    /// ADR-021: cosmetic camera pitch in degrees (−90..90), quantized to 1°. Reported by
    /// the client (from `PlayerInput.look[0]`), relayed to peers; presentation only — not
    /// validated, does not affect collision/hitreg/aim.
    #[serde(default)]
    pub pitch: i8,
    /// ADR-022: cosmetic equipment — the 4 worn clothing item IDs [Head, Torso, Legs, Feet]
    /// (0 = empty). Reported by the client from its inventory equipment slots, relayed to
    /// peers; presentation only — not validated, does not affect inventory/grants/stats.
    #[serde(default)]
    pub equipment: [i32; 4],
    /// ADR-023: cosmetic held item ID (0 = empty hands). Reported by the client from its
    /// wieldable holster slot, relayed to peers; presentation only — not validated, does not
    /// affect inventory/grants/combat.
    #[serde(default)]
    pub held_item: i32,
    /// ADR-024: cosmetic hit-reaction counter (monotonic, wrapping; 0 = never hit). Incremented
    /// by the client on each local DamageReceived event, relayed to peers; presentation only —
    /// not validated, does not affect inventory/grants/stats/combat/hitreg.
    #[serde(default)]
    pub hit_seq: u8,
    /// ADR-038: cosmetic "showing its real form" flag. NEVER written for a real player — the
    /// client does not report it (it is absent from `ipc::PlayerInput` by design) and the game
    /// loop has no seal for it, so it stays `false` for the whole session. It exists so
    /// `broadcast_player_update` fills the pose payload from one place; the only value that is
    /// ever `true` lives on a `PeerConnection` written by the phantom driver.
    #[serde(default)]
    pub revealed: bool,
    /// ADR-042: cosmetic "the wieldable in my hands is emitting light" flag. Reported by the
    /// client as "some `Light` under the active wieldable is enabled" — deliberately GENERIC, so
    /// a lighter/flare/flashlight works the day it exists without touching the wire. Relayed to
    /// peers; presentation only — not validated, does not affect visibility/stealth/AI.
    #[serde(default)]
    pub light_on: bool,
    /// ADR-042: cosmetic shot counter (monotonic, wrapping; 0 = never fired). Incremented by the
    /// client on each native `IFirearmTrigger.Shoot`, relayed to peers so observers can play the
    /// gunshot on the proxy. Same shape as `hit_seq` and for the same reason: a full-auto burst
    /// outruns the 10 Hz pose relay, so a flag would drop shots and a counter does not. NOT an AI
    /// stimulus — the phantom hears via `report_noise` (ADR-041), a separate channel on purpose.
    #[serde(default)]
    pub fire_seq: u8,
    /// ADR-028: server-side dedupe — true once this death's loot snapshot spawned a
    /// corpse. Guards against a double `report_death_loot` (the client's event
    /// fast-path + derived-edge fallback both firing) duplicating the inventory.
    /// Re-armed (false) when `respawn_request` is honored. Session-transient.
    #[serde(default)]
    pub death_loot_reported: bool,
    /// ADR-031: the player's respawn point (a placed "Sleeping Bag" position), or None → the fixed
    /// starter spawn. Set by the `stp_place` handler when a bed is placed ("last placed wins");
    /// consumed by `respawn_request`. Session-transient (RAM, not persisted), like the fields above.
    #[serde(default)]
    pub respawn_point: Option<Vec3>,
    /// ADR-032 amendment: latest client-reported snapshot of the REAL STP inventory (raw item
    /// ids, same stack shape corpses use — NOT the legacy `inventory` above, which is
    /// disconnected from the real game). Fed by the debounced `report_inventory` action
    /// (trust-the-client, sanitized on receipt); persisted via `PlayerSnapshot.stp_inventory`
    /// and pushed back to the client with the `inventory_restored` event after hydration.
    #[serde(default)]
    pub stp_inventory: Vec<crate::world::corpse::CorpseStack>,
    /// TEMP DIAG (TP attribution audit; REMOVE after diagnosis): game-loop tick of the last
    /// authoritative-reposition event sent to this player (`session_restored`/`player_died`/
    /// `player_respawned` — the same three that arm the client's `AuthoritativePoseApplier`
    /// snap window). Lets `TP_WATCH` approximate whether a rubber-band it logs fell inside that
    /// window or not, without the backend needing to know the client's actual timer state.
    /// Diagnostic-only: never serialized (`skip`), never persisted, never read by gameplay logic.
    #[serde(skip)]
    pub last_reposition_tick: Option<u64>,
}

impl Player {
    pub fn new(id: PeerId, name: impl Into<String>) -> Self {
        Self {
            id,
            uuid: Uuid::new_v4(),
            name: name.into(),
            position: Vec3::new(0.0, 1.8, 0.0), // player height 1.8 units
            rotation: 0.0,
            stats: PlayerStats::default(),
            inventory: Inventory::new(),
            equipped_stabilizer: None,
            owned_chunks: Vec::new(),
            crouch: false,
            pitch: 0,
            equipment: [0; 4],
            held_item: 0,
            hit_seq: 0,
            revealed: false,
            light_on: false,
            fire_seq: 0,
            death_loot_reported: false,
            respawn_point: None,
            stp_inventory: Vec::new(),
            last_reposition_tick: None,
        }
    }
}
