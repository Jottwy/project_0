//! Chunk state, ownership, and teleportation.
//! See ARCHITECTURE_V1.md §3 and §7.2.
//!
//! Phase 1 scaffolding: full data model is present; simulation (teleport timer,
//! ownership transfer) lands in Phase 2.

mod cell_flags;
mod coords;
mod edge_kinds;
mod items;
mod layout;
mod state;
mod surface_profiles;
mod vertical_flags;
mod volume_hints;

pub use cell_flags::*;
pub use coords::*;
pub use edge_kinds::*;
pub use items::*;
pub use layout::*;
pub use state::*;
pub use surface_profiles::*;
pub use vertical_flags::*;
pub use volume_hints::*;

use serde::{Deserialize, Serialize};

use crate::network::PeerId;
use crate::utils::ChunkPos;
use crate::world::entity::Entity;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Chunk {
    pub pos: ChunkPos,
    #[serde(default)]
    pub layer: ChunkLayer,
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
    pub layout: ChunkLayoutV1,
}

impl Chunk {
    pub fn is_active(&self) -> bool {
        matches!(self.state, ChunkState::Active { .. })
    }

    pub fn key(&self) -> LayeredChunkPos {
        layered_chunk_pos(self.pos, self.layer)
    }
}
