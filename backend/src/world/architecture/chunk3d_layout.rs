use serde::{Deserialize, Serialize};

use crate::world::chunk::{ChunkLayoutV1, LAYER_HEIGHT, LAYOUT_CELL_SIZE, LAYOUT_GRID_SIZE};
use crate::world::graph::coords::Chunk3DCoord;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Chunk3DLayout {
    pub coord: Chunk3DCoord,
    pub cell_size: f32,
    pub grid_size: u8,
    pub layer_height: f32,
}

impl Chunk3DLayout {
    pub fn new(coord: Chunk3DCoord, cell_size: f32, grid_size: u8, layer_height: f32) -> Self {
        Self {
            coord,
            cell_size,
            grid_size,
            layer_height,
        }
    }

    pub fn from_chunk_layout(coord: Chunk3DCoord, layout: &ChunkLayoutV1) -> Self {
        Self {
            coord,
            cell_size: layout.cell_size.max(LAYOUT_CELL_SIZE),
            grid_size: layout.grid_size.max(LAYOUT_GRID_SIZE),
            layer_height: LAYER_HEIGHT,
        }
    }

    pub fn total_cells(&self) -> usize {
        let g = self.grid_size as usize;
        g * g
    }

    pub fn cell_world_size(&self) -> f32 {
        self.cell_size * self.grid_size as f32
    }

    pub fn cell_to_world_offset(&self, cell_x: usize, cell_z: usize) -> (f32, f32) {
        (
            cell_x as f32 * self.cell_size,
            cell_z as f32 * self.cell_size,
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::generator::generate_chunk;
    use crate::world::graph::coords::Chunk3DCoord;

    #[test]
    fn chunk3d_layout_from_generated_chunk() {
        let chunk = generate_chunk(42, (0, 0));
        let coord = Chunk3DCoord::level0(0, 0, 0);
        let layout = Chunk3DLayout::from_chunk_layout(coord, &chunk.layout);
        assert!(layout.grid_size > 0);
        assert!(layout.cell_size > 0.0);
        assert!(layout.layer_height > 0.0);
        assert_eq!(layout.total_cells(), (layout.grid_size as usize).pow(2));
    }

    #[test]
    fn cell_world_size_matches_expected() {
        let chunk = generate_chunk(42, (3, 7));
        let coord = Chunk3DCoord::level0(3, 0, 7);
        let layout = Chunk3DLayout::from_chunk_layout(coord, &chunk.layout);
        let expected = layout.cell_size * layout.grid_size as f32;
        assert!((layout.cell_world_size() - expected).abs() < f32::EPSILON);
    }

    #[test]
    fn cell_to_world_offset_origin_is_zero() {
        let chunk = generate_chunk(42, (0, 0));
        let coord = Chunk3DCoord::level0(0, 0, 0);
        let layout = Chunk3DLayout::from_chunk_layout(coord, &chunk.layout);
        let (ox, oz) = layout.cell_to_world_offset(0, 0);
        assert_eq!(ox, 0.0);
        assert_eq!(oz, 0.0);
    }
}
