use serde::{Deserialize, Serialize};

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
}
