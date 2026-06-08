//! Shared utility types used across the backend.

use serde::{Deserialize, Serialize};

/// A 3D vector. Unity coordinate system: Y-up, 1 unit = 1 meter.
#[derive(Debug, Clone, Copy, Default, PartialEq, Serialize, Deserialize)]
pub struct Vec3 {
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

impl Vec3 {
    pub const ZERO: Vec3 = Vec3 { x: 0.0, y: 0.0, z: 0.0 };

    pub fn new(x: f32, y: f32, z: f32) -> Self {
        Self { x, y, z }
    }

    pub fn from_array(a: [f32; 3]) -> Self {
        Self { x: a[0], y: a[1], z: a[2] }
    }

    pub fn to_array(self) -> [f32; 3] {
        [self.x, self.y, self.z]
    }

    pub fn length(self) -> f32 {
        (self.x * self.x + self.y * self.y + self.z * self.z).sqrt()
    }

    pub fn length_sq(self) -> f32 {
        self.x * self.x + self.y * self.y + self.z * self.z
    }

    /// Returns the vector scaled to unit length, or `ZERO` if it has no length.
    pub fn normalized(self) -> Vec3 {
        let len = self.length();
        if len > f32::EPSILON {
            Vec3::new(self.x / len, self.y / len, self.z / len)
        } else {
            Vec3::ZERO
        }
    }

    pub fn scale(self, s: f32) -> Vec3 {
        Vec3::new(self.x * s, self.y * s, self.z * s)
    }

    pub fn add(self, other: Vec3) -> Vec3 {
        Vec3::new(self.x + other.x, self.y + other.y, self.z + other.z)
    }

    pub fn sub(self, other: Vec3) -> Vec3 {
        Vec3::new(self.x - other.x, self.y - other.y, self.z - other.z)
    }

    pub fn distance(self, other: Vec3) -> f32 {
        self.sub(other).length()
    }

    pub fn distance_xz(self, other: Vec3) -> f32 {
        let dx = self.x - other.x;
        let dz = self.z - other.z;
        (dx * dx + dz * dz).sqrt()
    }
}

/// World chunk coordinate (XZ grid index). Chunk size is 50x50 units.
pub type ChunkPos = (i32, i32);

/// Size of one chunk in world units (see ARCHITECTURE_V1.md §12.2).
pub const CHUNK_SIZE: f32 = 50.0;

/// Convert a world position to the chunk index that contains it.
pub fn world_to_chunk(pos: Vec3) -> ChunkPos {
    (
        (pos.x / CHUNK_SIZE).floor() as i32,
        (pos.z / CHUNK_SIZE).floor() as i32,
    )
}

/// World-space center of a chunk (XZ midpoint, Y=0).
pub fn chunk_center(pos: ChunkPos) -> Vec3 {
    Vec3::new(
        pos.0 as f32 * CHUNK_SIZE + CHUNK_SIZE * 0.5,
        0.0,
        pos.1 as f32 * CHUNK_SIZE + CHUNK_SIZE * 0.5,
    )
}

/// All chunk positions within `radius` of a given chunk center (Chebyshev distance).
pub fn chunks_in_radius(center: ChunkPos, radius: i32) -> Vec<ChunkPos> {
    let mut out = Vec::with_capacity(((radius * 2 + 1) * (radius * 2 + 1)) as usize);
    for x in (center.0 - radius)..=(center.0 + radius) {
        for z in (center.1 - radius)..=(center.1 + radius) {
            out.push((x, z));
        }
    }
    out
}
