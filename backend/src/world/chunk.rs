//! Chunk state, ownership, and teleportation.
//! See ARCHITECTURE_V1.md §3 and §7.2.
//!
//! Phase 1 scaffolding: full data model is present; simulation (teleport timer,
//! ownership transfer) lands in Phase 2.

use serde::{Deserialize, Serialize};

use crate::network::PeerId;
use crate::utils::ChunkPos;
use crate::world::entity::Entity;

/// Lifecycle of a chunk in the distributed world (ARCHITECTURE_V1.md §3.4).
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ChunkState {
    Unloaded,
    Dormant { cached_by: Vec<PeerId> },
    Active { stabilized: bool, anchored: bool },
}

impl ChunkState {
    /// String id reported to Unity (`random` / `stabilized` / `anchored`).
    pub fn render_name(&self) -> &'static str {
        match self {
            ChunkState::Active { anchored: true, .. } => "anchored",
            ChunkState::Active { stabilized: true, .. } => "stabilized",
            ChunkState::Active { .. } => "random",
            _ => "random",
        }
    }
}

/// A dropped item lying in the world (lost if the chunk teleports).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DroppedItem {
    pub id: u32,
    pub item: crate::player::inventory::Item,
    pub quantity: u16,
    pub position: crate::utils::Vec3,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Chunk {
    pub pos: ChunkPos,
    pub state: ChunkState,
    pub seed: u64,
    pub owner: Option<PeerId>,
    pub entities: Vec<Entity>,
    pub items: Vec<DroppedItem>,
    pub teleport_timer: f32,
    pub template_id: u8,
    pub rotation: u16, // 0, 90, 180, 270
    pub mirrored: bool,
    pub has_workbench: bool,
}

impl Chunk {
    pub fn is_active(&self) -> bool {
        matches!(self.state, ChunkState::Active { .. })
    }
}
