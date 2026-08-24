use serde::{Deserialize, Serialize};

use crate::utils::ChunkPos;
use crate::world::chunk::ChunkLayer;

pub type LevelId = u16;

pub const LEVEL_0: LevelId = 0;
pub const LEVEL_1: LevelId = 1;
pub const LEVEL_2: LevelId = 2;
pub const LEVEL_3: LevelId = 3;
pub const LEVEL_4: LevelId = 4;
pub const LEVEL_9: LevelId = 9;

pub const GROUND_LAYER: ChunkLayer = 0;
pub const UPPER_LAYER: ChunkLayer = 1;
pub const LOWER_LAYER: ChunkLayer = -1;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct RegionCoord {
    pub level_id: LevelId,
    pub region_x: i32,
    pub region_z: i32,
}

impl RegionCoord {
    pub fn new(level_id: LevelId, region_x: i32, region_z: i32) -> Self {
        Self {
            level_id,
            region_x,
            region_z,
        }
    }

    pub fn level0(region_x: i32, region_z: i32) -> Self {
        Self::new(LEVEL_0, region_x, region_z)
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
    pub fn new(
        level_id: LevelId,
        region_x: i32,
        region_z: i32,
        chunk_x: i32,
        chunk_y: ChunkLayer,
        chunk_z: i32,
    ) -> Self {
        Self {
            level_id,
            region_x,
            region_z,
            chunk_x,
            chunk_y,
            chunk_z,
        }
    }

    pub fn level0(chunk_x: i32, chunk_y: ChunkLayer, chunk_z: i32) -> Self {
        Self::new(LEVEL_0, 0, 0, chunk_x, chunk_y, chunk_z)
    }

    pub fn level0_ground(chunk_x: i32, chunk_z: i32) -> Self {
        Self::level0(chunk_x, GROUND_LAYER, chunk_z)
    }

    pub fn level0_upper(chunk_x: i32, chunk_z: i32) -> Self {
        Self::level0(chunk_x, UPPER_LAYER, chunk_z)
    }

    pub fn level0_lower(chunk_x: i32, chunk_z: i32) -> Self {
        Self::level0(chunk_x, LOWER_LAYER, chunk_z)
    }

    pub fn from_level0_chunk(chunk_x: i32, chunk_y: ChunkLayer, chunk_z: i32) -> Self {
        Self::level0(chunk_x, chunk_y, chunk_z)
    }

    pub fn from_legacy_chunk_pos(pos: ChunkPos) -> Self {
        Self::level0_ground(pos.0, pos.1)
    }

    pub fn to_legacy_chunk_pos(self) -> ChunkPos {
        (self.chunk_x, self.chunk_z)
    }

    pub fn is_legacy_layer(self) -> bool {
        self.chunk_y == GROUND_LAYER
    }

    pub fn is_above_ground(self) -> bool {
        self.chunk_y > GROUND_LAYER
    }

    pub fn is_below_ground(self) -> bool {
        self.chunk_y < GROUND_LAYER
    }

    pub fn with_layer(self, chunk_y: ChunkLayer) -> Self {
        Self { chunk_y, ..self }
    }

    pub fn above(self) -> Self {
        Self {
            chunk_y: self.chunk_y + 1,
            ..self
        }
    }

    pub fn below(self) -> Self {
        Self {
            chunk_y: self.chunk_y - 1,
            ..self
        }
    }

    pub fn region_coord(self) -> RegionCoord {
        RegionCoord::new(self.level_id, self.region_x, self.region_z)
    }

    pub fn same_region(self, other: Self) -> bool {
        self.region_coord() == other.region_coord()
    }

    pub fn same_layer(self, other: Self) -> bool {
        self.same_region(other) && self.chunk_y == other.chunk_y
    }

    pub fn same_column(self, other: Self) -> bool {
        self.same_region(other) && self.chunk_x == other.chunk_x && self.chunk_z == other.chunk_z
    }

    pub fn is_vertical_neighbor(self, other: Self) -> bool {
        self.same_column(other) && (self.chunk_y - other.chunk_y).abs() == 1
    }

    pub fn legacy_xz(self) -> (i32, i32) {
        self.to_legacy_chunk_pos()
    }

    pub fn key(self) -> (LevelId, i32, i32, i32, ChunkLayer, i32) {
        (
            self.level_id,
            self.region_x,
            self.region_z,
            self.chunk_x,
            self.chunk_y,
            self.chunk_z,
        )
    }
}
