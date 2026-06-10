use serde::{Deserialize, Serialize};

use super::coords::Chunk3DCoord;

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
}
