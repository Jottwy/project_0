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
        }
    }
}
