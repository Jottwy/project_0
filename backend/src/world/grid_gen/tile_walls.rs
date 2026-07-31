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
//!
//! ADR-033/Pillar (enmienda "Opción (c)", 2026-07-29): el nibble alto —antes
//! sin uso, todo consumidor vivo ya enmascaraba con `0x0F`— codifica cuál de
//! las 4 sub-celdas de 2.5 m del tile es `CellType::Pillar`: `0x10` NW
//! `(x0,z0)`, `0x20` NE `(x1,z0)`, `0x40` SW `(x0,z1)`, `0x80` SE `(x1,z1)`.
//! Criterio ESTRICTO — `kind() == CellType::Pillar`, nunca `is_solid()` (que
//! agrupa Wall|Pillar|Void y dibujaría columnas en agujeros/paredes). Mismo
//! tamaño de wire, mismo tipo `[[u8;10];10]`, sin bump de schema. PROHIBIDO
//! reutilizar estos bits o cambiar el mapeo sin nueva enmienda (`DECISIONS.md`).

use super::{generate_chunk_layer, CellType, LayerGrid, LayerRules, RoomZone, CHUNK_CELLS};

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

/// Per-tile pillar bits (nibble alto, ADR-033/Pillar). Uno por sub-celda de
/// 2.5 m del tile de 5 m; ver el mapeo en el doc-comment del módulo.
pub const PILLAR_NW: u8 = 0x10; // (x0, z0)
pub const PILLAR_NE: u8 = 0x20; // (x1, z0)
pub const PILLAR_SW: u8 = 0x40; // (x0, z1)
pub const PILLAR_SE: u8 = 0x80; // (x1, z1)

/// Generate one chunk (with seam stitching) and derive its 5 m tile-wall bitmask.
///
/// `rules` is the chunk's personality profile. Hasta ADR-033 se derivaba aquí de
/// `LAYER_PROFILES[layer]`; ahora lo INYECTA el llamador (`game_loop`, vía
/// `world::zone_density::rules_for`) para que `zone_kind` pueda variar la
/// densidad por chunk sin que `grid_gen` importe nada de `world/`. El consumidor
/// de colisión del robapieles (`GridGenChunkCache`) debe inyectar EL MISMO
/// resolutor o render y colisión divergen.
///
/// `forced_walkable` is empty in Fase 4.1 (no cross-layer coordination yet), so
/// stairs/pits may lead into a wall in adjacent layers — a known, deferred
/// limitation that does not affect single-layer rendering.
pub fn chunk_tile_walls(
    rules: &LayerRules,
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
) -> [[u8; TILES_PER_SIDE]; TILES_PER_SIDE] {
    chunk_tile_walls_and_rooms(rules, world_seed, cx, cz, layer).0
}

/// Igual que `chunk_tile_walls`, pero devuelve TAMBIÉN los `RoomZone` de Fase 4.
///
/// Existe como función aparte, y no como cambio de firma de `chunk_tile_walls`,
/// porque los consumidores de COLISIÓN (`GridGenChunkCache`) y los tests de
/// paridad de `zone_density` solo quieren el bitmask: el rect de sala no les
/// dice nada y no deben cargar con él. El único llamador de esta variante es la
/// ruta de IPC (`game_loop`, `ServerMessage::ChunkData`), que sí lo manda al
/// cliente. Ambas comparten la MISMA generación — no hay riesgo de divergir.
pub fn chunk_tile_walls_and_rooms(
    rules: &LayerRules,
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
) -> ([[u8; TILES_PER_SIDE]; TILES_PER_SIDE], Vec<RoomZone>) {
    let out = generate_chunk_layer(rules, world_seed, (cx, cz), layer as i32, &[]);
    (tile_walls_from_grid(&out.grid), out.room_zones)
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

            // ADR-033/Pillar: nibble alto, una sub-celda a la vez. Criterio
            // ESTRICTO `kind() == CellType::Pillar` — nunca `is_solid()`, que
            // también marcaría Wall/Void y rompería tanto los tests de bit
            // exacto de este módulo como la garantía "solo columnas reales"
            // de la enmienda.
            if is_pillar(grid, x0, z0) {
                bits |= PILLAR_NW;
            }
            if is_pillar(grid, x1, z0) {
                bits |= PILLAR_NE;
            }
            if is_pillar(grid, x0, z1) {
                bits |= PILLAR_SW;
            }
            if is_pillar(grid, x1, z1) {
                bits |= PILLAR_SE;
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

/// ADR-033/Pillar: true iff the in-bounds cell at `(x, z)` is EXACTLY
/// `CellType::Pillar`. Cells inside a tile are always in-bounds (`x0/x1/z0/z1`
/// never leave `[0, CHUNK_CELLS)`), but this stays defensive like its `_open`
/// siblings above.
fn is_pillar(grid: &LayerGrid, x: i32, z: i32) -> bool {
    LayerGrid::in_bounds(x, z) && grid.get(x as usize, z as usize).kind() == CellType::Pillar
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::grid_gen::{Cell, CellType};

    fn open(grid: &mut LayerGrid, x: usize, z: usize) {
        grid.set(x, z, Cell::new(CellType::Corridor, 2, 0));
    }

    fn pillar(grid: &mut LayerGrid, x: usize, z: usize) {
        grid.set(x, z, Cell::new(CellType::Pillar, 2, 0));
    }

    /// ADR-033/Pillar (a): grid a mano, sin seed — las 4 sub-celdas de un tile
    /// son `Pillar`. Los 4 bits del nibble alto deben salir exactos. Como
    /// ninguna sub-celda es caminable, las 4 aristas del tile también salen
    /// cerradas (mismo mecanismo que `isolated_open_tile_is_walled_on_all_four_edges`
    /// en su caso contrario): el bitmask completo es `0xFF`.
    #[test]
    fn tile_with_all_four_subcells_pillar_sets_exact_nibble() {
        // Tile (2,2): cells x∈{4,5}, z∈{4,5} — mismo tile que usan los tests
        // vecinos, esta vez con Pillar en vez de Corridor.
        let mut grid = LayerGrid::new_solid();
        pillar(&mut grid, 4, 4); // NW (x0,z0)
        pillar(&mut grid, 5, 4); // NE (x1,z0)
        pillar(&mut grid, 4, 5); // SW (x0,z1)
        pillar(&mut grid, 5, 5); // SE (x1,z1)

        let walls = tile_walls_from_grid(&grid);
        assert_eq!(
            walls[2][2],
            WALL_N | WALL_S | WALL_E | WALL_W | PILLAR_NW | PILLAR_NE | PILLAR_SW | PILLAR_SE,
            "nibble alto debe marcar las 4 sub-celdas Pillar; nibble bajo cerrado porque ninguna es caminable"
        );
    }

    /// ADR-033/Pillar (a, complemento): un único pilar en UNA sub-celda no
    /// enciende las otras 3 posiciones del nibble — descarta un `bits |=` mal
    /// direccionado que marcase el tile entero en vez de la sub-celda.
    #[test]
    fn single_pillar_subcell_sets_only_its_own_bit() {
        let mut grid = LayerGrid::new_solid();
        // Resto del tile (2,2) caminable; solo (5,4) = NE es Pillar.
        open(&mut grid, 4, 4);
        pillar(&mut grid, 5, 4);
        open(&mut grid, 4, 5);
        open(&mut grid, 5, 5);

        let walls = tile_walls_from_grid(&grid);
        assert_eq!(
            walls[2][2] & 0xF0,
            PILLAR_NE,
            "solo NE debe llevar el bit de pilar"
        );
    }

    /// ADR-033/Pillar (d): las celdas de borde (índice 0 y `CHUNK_CELLS-1`) son
    /// SIEMPRE `Cell::SOLID_WALL` (`border_cells_remain_solid_wall`, tests.rs) —
    /// nunca `Pillar` — así que los tiles perimetrales (0 y `TILES_PER_SIDE-1`)
    /// no pueden llevar nibble alto. Verificado sobre generación REAL (no solo
    /// por construcción) con el perfil de mayor `pillar_chance` disponible, para
    /// que una futura recalibración de perfiles no lo rompa en silencio.
    #[test]
    // `i` indexa ambos ejes de `walls` (fila y columna) en el mismo cuerpo del
    // bucle; no hay un único `enumerate()` que cubra las 4 comprobaciones.
    #[allow(clippy::needless_range_loop)]
    fn border_tiles_never_carry_pillar_bits() {
        use super::super::LAYER_PROFILES;

        let rules = &LAYER_PROFILES[2]; // "El Caos": pillar_chance 0.6, el más alto calibrado
        let last = TILES_PER_SIDE - 1;
        for seed in [TEST_SEED_LOCAL, 1, 42, 9_999_999] {
            for layer_index in 0..LAYER_PROFILES.len() as i32 {
                let out = generate_chunk_layer(rules, seed, (3, -7), layer_index, &[]);
                let walls = tile_walls_from_grid(&out.grid);
                for i in 0..TILES_PER_SIDE {
                    assert_eq!(
                        walls[i][0] & 0xF0,
                        0,
                        "seed {seed} capa {layer_index}: tile borde norte ({i},0) con nibble alto"
                    );
                    assert_eq!(
                        walls[i][last] & 0xF0,
                        0,
                        "seed {seed} capa {layer_index}: tile borde sur ({i},{last}) con nibble alto"
                    );
                    assert_eq!(
                        walls[0][i] & 0xF0,
                        0,
                        "seed {seed} capa {layer_index}: tile borde oeste (0,{i}) con nibble alto"
                    );
                    assert_eq!(
                        walls[last][i] & 0xF0,
                        0,
                        "seed {seed} capa {layer_index}: tile borde este ({last},{i}) con nibble alto"
                    );
                }
            }
        }
    }

    const TEST_SEED_LOCAL: u64 = 0xBACC_0085;

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
