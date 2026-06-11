use serde::{Deserialize, Serialize};

use super::coords::Chunk3DCoord;
use crate::world::architecture::chunk3d_layout::Chunk3DLayout;
use crate::world::chunk::ChunkLayoutV1;

pub type SpatialNodeId = u32;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum SpatialNodeKind {
    Room,
    Corridor,
    Intersection,
    Stair,
    Ramp,
    Atrium,
    Shaft,
    SealedUpperSpace,
    UnderfloorService,
    ManilaRoom,
    DangerPocket,
    BlockedPortal,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SpatialNode {
    pub id: SpatialNodeId,
    pub kind: SpatialNodeKind,
    pub coord: Chunk3DCoord,

    /// Local cell-space bounds inside a Chunk3D.
    /// Inclusive min, exclusive max.
    /// Format: [x, y, z].
    pub local_min: [u8; 3],
    pub local_max: [u8; 3],

    /// Can the player currently traverse this node?
    pub accessible: bool,

    /// Can the player see/hear/notice it even if inaccessible?
    pub perceptible: bool,
}

impl SpatialNode {
    pub fn new(
        id: SpatialNodeId,
        kind: SpatialNodeKind,
        coord: Chunk3DCoord,
        local_min: [u8; 3],
        local_max: [u8; 3],
        accessible: bool,
        perceptible: bool,
    ) -> Self {
        Self {
            id,
            kind,
            coord,
            local_min,
            local_max,
            accessible,
            perceptible,
        }
    }

    pub fn is_vertical(&self) -> bool {
        matches!(
            self.kind,
            SpatialNodeKind::Stair
                | SpatialNodeKind::Ramp
                | SpatialNodeKind::Atrium
                | SpatialNodeKind::Shaft
        )
    }

    pub fn is_safe_zone(&self) -> bool {
        matches!(self.kind, SpatialNodeKind::ManilaRoom)
    }

    pub fn is_danger_zone(&self) -> bool {
        matches!(self.kind, SpatialNodeKind::DangerPocket)
    }

    pub fn is_accessible_vertical_connector(&self) -> bool {
        self.accessible && self.is_vertical()
    }

    pub fn has_valid_local_bounds(&self) -> bool {
        self.local_min[0] < self.local_max[0]
            && self.local_min[1] < self.local_max[1]
            && self.local_min[2] < self.local_max[2]
    }

    pub fn cell_volume(&self) -> usize {
        let dx = self.local_max[0].saturating_sub(self.local_min[0]) as usize;
        let dy = self.local_max[1].saturating_sub(self.local_min[1]) as usize;
        let dz = self.local_max[2].saturating_sub(self.local_min[2]) as usize;

        dx * dy * dz
    }

    pub fn to_chunk3d_layout(&self, chunk_layout: &ChunkLayoutV1) -> Chunk3DLayout {
        Chunk3DLayout::from_chunk_layout(self.coord, chunk_layout)
    }

    pub fn world_bounds_2d(&self, layout: &Chunk3DLayout) -> ((f32, f32), (f32, f32)) {
        let chunk_origin_x = self.coord.chunk_x as f32 * layout.cell_world_size();
        let chunk_origin_z = self.coord.chunk_z as f32 * layout.cell_world_size();

        let min_x = chunk_origin_x + self.local_min[0] as f32 * layout.cell_size;
        let min_z = chunk_origin_z + self.local_min[2] as f32 * layout.cell_size;
        let max_x = chunk_origin_x + self.local_max[0] as f32 * layout.cell_size;
        let max_z = chunk_origin_z + self.local_max[2] as f32 * layout.cell_size;

        ((min_x, min_z), (max_x, max_z))
    }

    pub fn world_bounds_3d(&self, layout: &Chunk3DLayout) -> ((f32, f32, f32), (f32, f32, f32)) {
        let chunk_origin_x = self.coord.chunk_x as f32 * layout.cell_world_size();
        let chunk_origin_y = self.coord.chunk_y as f32 * layout.layer_height;
        let chunk_origin_z = self.coord.chunk_z as f32 * layout.cell_world_size();

        let min_x = chunk_origin_x + self.local_min[0] as f32 * layout.cell_size;
        let min_y = chunk_origin_y + self.local_min[1] as f32 * layout.cell_size;
        let min_z = chunk_origin_z + self.local_min[2] as f32 * layout.cell_size;

        let max_x = chunk_origin_x + self.local_max[0] as f32 * layout.cell_size;
        let max_y = chunk_origin_y + self.local_max[1] as f32 * layout.cell_size;
        let max_z = chunk_origin_z + self.local_max[2] as f32 * layout.cell_size;

        ((min_x, min_y, min_z), (max_x, max_y, max_z))
    }
}
