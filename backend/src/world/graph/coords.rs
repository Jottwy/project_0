use serde::{Deserialize, Serialize};

use crate::world::chunk::ChunkLayer;

pub type LevelId = u16;

pub const LEVEL_0: LevelId = 0;
pub const LEVEL_1: LevelId = 1;
pub const LEVEL_2: LevelId = 2;
pub const LEVEL_3: LevelId = 3;
pub const LEVEL_9: LevelId = 9;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct RegionCoord {
    pub level_id: LevelId,
    pub region_x: i32,
    pub region_z: i32,
}

impl RegionCoord {
    pub fn level0(region_x: i32, region_z: i32) -> Self {
        Self {
            level_id: LEVEL_0,
            region_x,
            region_z,
        }
    }
}

impl Chunk3DCoord {
    pub fn from_level0_chunk(chunk_x: i32, chunk_y: ChunkLayer, chunk_z: i32) -> Self {
        Self::level0(chunk_x, chunk_y, chunk_z)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct Chunk3DCoord {
    pub level_id: LevelId,
    pub region_x: i32,
    pub region_z: i32,
    pub chunk_x: i32,
    pub chunk_y: ChunkLayer,
    pub chunk_z: i32,
}

impl Chunk3DCoord {
    pub fn level0(chunk_x: i32, chunk_y: ChunkLayer, chunk_z: i32) -> Self {
        Self {
            level_id: LEVEL_0,
            region_x: 0,
            region_z: 0,
            chunk_x,
            chunk_y,
            chunk_z,
        }
    }

    pub fn region_coord(self) -> RegionCoord {
        RegionCoord {
            level_id: self.level_id,
            region_x: self.region_x,
            region_z: self.region_z,
        }
    }

    pub fn legacy_xz(self) -> (i32, i32) {
        (self.chunk_x, self.chunk_z)
    }
}
