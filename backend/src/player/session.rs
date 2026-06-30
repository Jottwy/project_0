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
        }
    }
}
