//! Per-connection player state (a peer's view of its own local player).
//! See ARCHITECTURE_V1.md §11.1.

use serde::{Deserialize, Serialize};
use uuid::Uuid;

use crate::network::PeerId;
use crate::player::inventory::{Inventory, StabilizerTier};
use crate::player::stats::PlayerStats;
use crate::utils::Vec3;

/// ADR-045 Fase 3: one item-instance property (durability, ammo count, ...). Mirrors STP's
/// `ItemProperty { int Id, double Value }` 1:1 — no parallel model invented. An id the client
/// no longer recognizes at restore time (a def changed between versions) is ignored, not an
/// error — same degrade-gracefully contract as an unknown `item_id`.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct ItemPropertyValue {
    pub id: i32,
    pub value: f64,
}

/// ADR-045 Fase 3: one inventory stack with instance fidelity — which container, which slot,
/// and its properties — unlike `world::corpse::CorpseStack` (`{item_id, quantity}` only, used
/// by `stp_inventory`/corpses/chests, where slot placement is meaningless).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct InventoryStackV2 {
    pub item_id: i32,
    pub quantity: i32,
    pub container: u8,
    pub slot: u8,
    #[serde(default)]
    pub props: Vec<ItemPropertyValue>,
}

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
    /// ADR-048: monotonic vocalisation counter. Same shape and same reason as `revealed` above —
    /// never written for a real player, absent from `ipc::PlayerInput` by design, and only ever
    /// non-zero on a `PeerConnection` written by the phantom driver.
    #[serde(default)]
    pub vocal_seq: u8,
    /// ADR-048: WHICH voice the last `vocal_seq` bump was. See the ADR for the table.
    #[serde(default)]
    pub vocal_kind: u8,
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
    /// ADR-044: cosmetic sustained-state bitfield — bit 0 = aiming, bit 1 = reloading. The field
    /// itself is NOT new (it existed in `ipc::PlayerInput`, written as a literal 0 and read by
    /// nobody); this ADR gives it meaning and promotes it to the relay. 14 bits are free on
    /// purpose, so the next sustained state costs no wire change. Never an input to simulation.
    #[serde(default)]
    pub buttons: u16,
    /// ADR-044: cosmetic melee-swing counter (monotonic, wrapping; 0 = never swung). A counter and
    /// not a `buttons` bit because a level bit cannot tell two chained swings from one held swing —
    /// exactly the read that matters in a melee exchange.
    #[serde(default)]
    pub melee_seq: u8,
    /// ADR-049: cosmetic carry state — which `CarryableDefinition` this player is hauling (0 = empty
    /// hands) and how many units. A LEVEL, not a counter: carrying is a sustained state, so a dropped
    /// datagram corrects itself on the next one. Client-reported, like `equipment` and `held_item` —
    /// the backend keeps no per-player carry state to derive it from.
    #[serde(default)]
    pub carry_def: i32,
    #[serde(default)]
    pub carry_count: u8,
    /// ADR-028: server-side dedupe — true once this death's loot snapshot spawned a
    /// corpse. Guards against a double `report_death_loot` (the client's event
    /// fast-path + derived-edge fallback both firing) duplicating the inventory.
    /// Re-armed (false) when `respawn_request` is honored. Session-transient.
    #[serde(default)]
    pub death_loot_reported: bool,
    /// ADR-031: the player's respawn point (a placed "Sleeping Bag" position), or None → the fixed
    /// starter spawn. ADR-069 moved the write: the `stp_place` handler no longer sets this (placing
    /// the BLUEPRINT armed a free respawn), only the `bed_constructed` confirmation does.
    /// Consumed by `respawn_request`.
    #[serde(default)]
    pub respawn_point: Option<Vec3>,
    /// ADR-069 phase 1: a bed blueprint THIS player planted, not yet built. Armed by `stp_place`
    /// ("last placed wins", same rule the real point used to follow) and promoted into
    /// `respawn_point` when the `bed_constructed` action arrives with a matching position.
    /// Matched by POSITION, never by `building_id` — the id is host-minted and a joiner's backend
    /// never learns which one its own placement got (see ADR-069 decision 4).
    #[serde(default)]
    pub pending_respawn_point: Option<Vec3>,
    /// ADR-032 amendment: latest client-reported snapshot of the REAL STP inventory (raw item
    /// ids, same stack shape corpses use — NOT the legacy `inventory` above, which is
    /// disconnected from the real game). Fed by the debounced `report_inventory` action
    /// (trust-the-client, sanitized on receipt); persisted via `PlayerSnapshot.stp_inventory`
    /// and pushed back to the client with the `inventory_restored` event after hydration.
    #[serde(default)]
    pub stp_inventory: Vec<crate::world::corpse::CorpseStack>,
    /// ADR-045 Fase 3: instance-fidelity companion to `stp_inventory` above — same items, but
    /// with container/slot placement and per-instance properties (durability, ammo, ...), fed
    /// by the SAME debounced `report_inventory` action once the client sends the richer shape.
    /// `stp_inventory` is left intact and still authoritative for a pre-Fase-3 client (or a
    /// pre-Fase-3 save loaded by a Fase-3 backend): `#[serde(default)]` empties this on load,
    /// and the emission site falls back to the flat `stp_inventory` restore when this is empty.
    #[serde(default)]
    pub inventory_v2: Vec<InventoryStackV2>,
    /// ADR-045 Fase 1: identity key coined by the client ("uuid:{guid}" normally, "name:{name}"
    /// fallback), fixed once via the `set_identity` IPC action. `None` until the client sends it
    /// (or forever, with a pre-ADR-045 client) — never a placeholder, so callers can tell "not
    /// known yet" apart from "resolved to the name: fallback". Session-transient: it selects
    /// which player file to load/write, it is not itself part of `PlayerSnapshot`.
    #[serde(skip)]
    pub identity_key: Option<String>,
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
            crouch: false,
            pitch: 0,
            equipment: [0; 4],
            held_item: 0,
            hit_seq: 0,
            revealed: false,
            vocal_seq: 0,
            vocal_kind: 0,
            light_on: false,
            fire_seq: 0,
            buttons: 0,
            melee_seq: 0,
            carry_def: 0,
            carry_count: 0,
            death_loot_reported: false,
            respawn_point: None,
            pending_respawn_point: None,
            stp_inventory: Vec::new(),
            inventory_v2: Vec::new(),
            identity_key: None,
            last_reposition_tick: None,
        }
    }
}
