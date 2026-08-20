use crate::utils::ChunkPos;

pub type ChunkLayer = i8;
pub type LayeredChunkPos = (i32, ChunkLayer, i32);

pub const LAYOUT_GRID_SIZE: u8 = 10;
pub const LAYOUT_CELL_SIZE: f32 = 5.0;

/// Phase 3.0A — vertical separation between adjacent macro layers, in metres.
/// `chunk_root_y = layer * LAYER_HEIGHT`. Layer 0 is normal Level 0 ground.
///
/// NOT THE SAME CONSTANT AS `grid_gen::LAYER_HEIGHT_M` (4 m). This one belongs to the volumetric
/// subsystem: it is what `volumetric_grid` puts in the `layer_height` wire field and what
/// `ChunkRenderer.LayerHeight` mirrors as 7 f. grid_gen — the world the player actually walks —
/// stacks at 4 m (ADR-007), mirrored by `GridConstants.LayerHeight`. Anything that classifies a
/// player Y into a layer of grid_gen geometry must divide by `LAYER_HEIGHT_M`, never by this.
pub const LAYER_HEIGHT: f32 = 7.0;

pub fn layered_chunk_pos(pos: ChunkPos, layer: ChunkLayer) -> LayeredChunkPos {
    (pos.0, layer, pos.1)
}

pub fn layer_y(layer: ChunkLayer) -> f32 {
    layer as f32 * LAYER_HEIGHT
}
