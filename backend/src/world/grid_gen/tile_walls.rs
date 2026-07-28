//! Cell-grid → 5 m tile-wall bitmask conversion (Fase 4.1, IPC world path).
//!
//! `grid_gen` is CELL-based: a wall is a solid `Cell` (Wall/Pillar/Void), there
//! are NO edge structures. The Unity render contract, by contrast, is "every
//! tile is floored; walls are thin 0.2 m panels on tile EDGES" at a 5 m tile
//! granularity (2×2 cells = 1 tile). This module bridges the two by quantizing
//! the 2.5 m cell maze to a 10×10 grid of 5 m tiles, emitting per-tile edge
//! walls. The 2.5 m → 5 m quantization is intentionally lossy (≤2.5 m of detail
//! collapses); that is the agreed contract.
//!
//! Conversion rule — "passable crossing" (chosen 2026-06-19):
//!   A tile edge is OPEN iff at least one of its two constituent 2.5 m cell
//!   crossings is passable. A crossing from the tile's own (interior) edge cell
//!   to the neighbour cell across the boundary is passable iff BOTH are walkable.
//!   Out-of-chunk neighbours are treated as OPEN, because the adjacent chunk
//!   carves a matching seam aperture from the same canonical edge seed
//!   (`stitching.rs`) — so a walkable border cell here means the seam is open.
//!   The edge gets a wall iff it is NOT open.
//!
//! This makes interior edges symmetric (tile A's E edge agrees with tile B's W
//! edge, since both read the same cell pair) and preserves maze connectivity
//! (any 2.5 m gap keeps the 5 m edge open).
//!
//! Axis convention (IPC contract, mirror in Unity): N = −Z, S = +Z, E = +X,
//! W = −X. Bits: N=1, S=2, E=4, W=8.

use super::{generate_chunk_layer, LayerGrid, CHUNK_CELLS, LAYER_PROFILES};

/// Tiles per chunk side: 2 cells per 5 m tile → 10 for CHUNK_CELLS = 20.
pub const TILES_PER_SIDE: usize = CHUNK_CELLS / 2;

// The IPC `GridChunkData.walls` field is a literal `[[u8; 10]; 10]`. Tie that
// contract to CHUNK_CELLS here: if CHUNK_CELLS ever changes, this fails to
// compile loudly instead of silently mismatching the wire schema.
const _: [(); 10] = [(); TILES_PER_SIDE];

/// Per-tile edge-wall bits.
pub const WALL_N: u8 = 1; // −Z
pub const WALL_S: u8 = 2; // +Z
pub const WALL_E: u8 = 4; // +X
pub const WALL_W: u8 = 8; // −X

/// Generate one chunk (with seam stitching) and derive its 5 m tile-wall bitmask.
///
/// `layer` selects the personality profile, clamped to the 4 `LAYER_PROFILES`.
/// `forced_walkable` is empty in Fase 4.1 (no cross-layer coordination yet), so
/// stairs/pits may lead into a wall in adjacent layers — a known, deferred
/// limitation that does not affect single-layer rendering.
pub fn chunk_tile_walls(
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
) -> [[u8; TILES_PER_SIDE]; TILES_PER_SIDE] {
    let rules = &LAYER_PROFILES[(layer as usize).min(LAYER_PROFILES.len() - 1)];
    let out = generate_chunk_layer(rules, world_seed, (cx, cz), layer as i32, &[]);
    tile_walls_from_grid(&out.grid)
}

/// Pure conversion: derive the tile-wall bitmask from an already-generated
/// 20×20 cell grid. RNG-free and deterministic, so it can be unit-tested with a
/// hand-built grid.
pub fn tile_walls_from_grid(grid: &LayerGrid) -> [[u8; TILES_PER_SIDE]; TILES_PER_SIDE] {
    let mut walls = [[0u8; TILES_PER_SIDE]; TILES_PER_SIDE];
    for (tx, column) in walls.iter_mut().enumerate() {
        for (tz, cell) in column.iter_mut().enumerate() {
            let x0 = (tx * 2) as i32; // west cell column
            let x1 = x0 + 1; // east cell column
            let z0 = (tz * 2) as i32; // north (−Z) cell row
            let z1 = z0 + 1; // south (+Z) cell row

            let mut bits = 0u8;
            // N (−Z): interior row z0, neighbour z0−1; sub-crossings at x0 and x1.
            if !edge_open(grid, &[((x0, z0), (x0, z0 - 1)), ((x1, z0), (x1, z0 - 1))]) {
                bits |= WALL_N;
            }
            // S (+Z): interior row z1, neighbour z1+1.
            if !edge_open(grid, &[((x0, z1), (x0, z1 + 1)), ((x1, z1), (x1, z1 + 1))]) {
                bits |= WALL_S;
            }
            // E (+X): interior column x1, neighbour x1+1; sub-crossings at z0 and z1.
            if !edge_open(grid, &[((x1, z0), (x1 + 1, z0)), ((x1, z1), (x1 + 1, z1))]) {
                bits |= WALL_E;
            }
            // W (−X): interior column x0, neighbour x0−1.
            if !edge_open(grid, &[((x0, z0), (x0 - 1, z0)), ((x0, z1), (x0 - 1, z1))]) {
                bits |= WALL_W;
            }
            *cell = bits;
        }
    }
    walls
}

/// One edge crossing: a 2.5 m cell pair straddling a tile boundary.
type EdgeCrossing = ((i32, i32), (i32, i32));

/// True if any of the given (interior_cell, neighbour_cell) crossings is passable.
fn edge_open(grid: &LayerGrid, crossings: &[EdgeCrossing]) -> bool {
    crossings.iter().any(|&(inner, neighbour)| {
        cell_walkable(grid, inner.0, inner.1)
            && neighbour_walkable_or_open(grid, neighbour.0, neighbour.1)
    })
}

/// In-bounds & walkable. Out-of-bounds → false (the tile's own edge cell is
/// always in-bounds, but this stays defensive).
fn cell_walkable(grid: &LayerGrid, x: i32, z: i32) -> bool {
    LayerGrid::in_bounds(x, z) && grid.get(x as usize, z as usize).is_walkable()
}

/// Walkable, but treat out-of-chunk neighbours as OPEN (the adjacent chunk's
/// matching seam aperture). A solid (Wall/Pillar/Void) border cell still walls.
fn neighbour_walkable_or_open(grid: &LayerGrid, x: i32, z: i32) -> bool {
    !LayerGrid::in_bounds(x, z) || grid.get(x as usize, z as usize).is_walkable()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::grid_gen::{Cell, CellType};

    fn open(grid: &mut LayerGrid, x: usize, z: usize) {
        grid.set(x, z, Cell::new(CellType::Corridor, 2, 0));
    }

    #[test]
    fn isolated_open_tile_is_walled_on_all_four_edges() {
        // Tile (2,2) covers cells x∈{4,5}, z∈{4,5}; open it, leave all else solid.
        let mut grid = LayerGrid::new_solid();
        open(&mut grid, 4, 4);
        open(&mut grid, 5, 4);
        open(&mut grid, 4, 5);
        open(&mut grid, 5, 5);

        let walls = tile_walls_from_grid(&grid);
        assert_eq!(walls[2][2], WALL_N | WALL_S | WALL_E | WALL_W);
    }

    #[test]
    fn interior_passage_opens_shared_edge_symmetrically() {
        // Tile (2,2) open, plus the west cells of tile (3,2) (x=6) → a passage
        // across the (2,2)|(3,2) boundary. The shared edge must read OPEN from
        // both sides (E of (2,2) absent, W of (3,2) absent).
        let mut grid = LayerGrid::new_solid();
        for &(x, z) in &[(4, 4), (5, 4), (4, 5), (5, 5), (6, 4), (6, 5)] {
            open(&mut grid, x, z);
        }

        let walls = tile_walls_from_grid(&grid);
        // (2,2): E now open (passage east), other three walled.
        assert_eq!(walls[2][2], WALL_N | WALL_S | WALL_W);
        // (3,2): W open (shared), E walled (x=7 solid), N/S walled.
        assert_eq!(walls[3][2], WALL_N | WALL_S | WALL_E);
    }

    #[test]
    fn chunk_border_aperture_opens_seam_edge() {
        // Tile (0,1) covers x∈{0,1}, z∈{2,3}. Open only the west border cell
        // (0,2): the W seam edge must be OPEN (out-of-chunk neighbour treated as
        // the adjacent chunk's matching aperture); N/S/E stay walled.
        let mut grid = LayerGrid::new_solid();
        open(&mut grid, 0, 2);

        let walls = tile_walls_from_grid(&grid);
        assert_eq!(walls[0][1], WALL_N | WALL_S | WALL_E);
    }
}
