//! Edge stitching between adjacent chunks (Fase 1, bloque E).
//!
//! Backrooms es infinito pero los chunks son finitos: sin costura, cada borde
//! de chunk es un muro que cierra el paso. Este módulo abre ≥1 apertura por
//! borde usando las filas/columnas 0 y 19 que la generación reserva intactas.
//!
//! Coherencia sin comunicación: los dos chunks que comparten un borde derivan
//! la posición de la apertura de la MISMA seed de borde canónica
//! `edge_seed(world_seed, chunk_menor, eje, layer)` — coinciden por
//! construcción, no por sincronía. Requisito multijugador determinista.

use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

use super::generator::repair_connectivity;
use super::{generate_layer, Cell, CellType, LayerGrid, LayerOutput, LayerRules, CHUNK_CELLS};

/// Edge axis for canonical edge identification.
#[derive(Clone, Copy)]
enum EdgeAxis {
    /// Border between (cx, cz) and (cx+1, cz) — the vertical seam line.
    Vertical = 0,
    /// Border between (cx, cz) and (cx, cz+1) — the horizontal seam line.
    Horizontal = 1,
}

/// Generate one fully-stitched layer of one chunk.
///
/// Wraps `generate_layer` and then opens the four seam apertures (N, S, E, W),
/// reconnecting them to the interior maze. Same determinism contract:
/// (world_seed, chunk_coord, layer_index) → byte-identical output.
///
/// Vertical seams (between layers) need nothing here: Fase 7 stairs/pits +
/// `forced_walkable` already guarantee both ends of every vertical transition,
/// and layers stack within the same chunk footprint, so no chunk border is
/// crossed vertically.
pub fn generate_chunk_layer(
    rules: &LayerRules,
    world_seed: u64,
    chunk_coord: (i32, i32),
    layer_index: i32,
    forced_walkable: &[(u8, u8)],
) -> LayerOutput {
    let mut out = generate_layer(rules, world_seed, chunk_coord, layer_index, forced_walkable);
    stitch_edges(&mut out.grid, rules, world_seed, chunk_coord, layer_index);
    out
}

/// Open one aperture on each of the four chunk borders and reconnect.
fn stitch_edges(
    grid: &mut LayerGrid,
    rules: &LayerRules,
    world_seed: u64,
    (cx, cz): (i32, i32),
    layer_index: i32,
) {
    let last = CHUNK_CELLS - 1;

    // East border: shared with (cx+1, cz). Canonical key = this chunk.
    let p = aperture_pos(world_seed, cx, cz, EdgeAxis::Vertical, layer_index);
    carve_aperture(grid, rules, (last, p), (-1i32, 0i32));

    // West border: shared with (cx-1, cz). Canonical key = the western chunk.
    let p = aperture_pos(world_seed, cx - 1, cz, EdgeAxis::Vertical, layer_index);
    carve_aperture(grid, rules, (0, p), (1, 0));

    // North border (z+1): shared with (cx, cz+1). Canonical key = this chunk.
    let p = aperture_pos(world_seed, cx, cz, EdgeAxis::Horizontal, layer_index);
    carve_aperture(grid, rules, (p, last), (0, -1));

    // South border (z-1): shared with (cx, cz-1). Canonical key = the southern chunk.
    let p = aperture_pos(world_seed, cx, cz - 1, EdgeAxis::Horizontal, layer_index);
    carve_aperture(grid, rules, (p, 0), (0, 1));

    // Reconnection rule (§5) applied to the seams: the freshly carved aperture
    // corridors may still be separate components (e.g. the inward carve stopped
    // against stamped content). One repair pass attaches them to the main maze.
    repair_connectivity(grid, rules.ceiling_corridor);
}

/// Deterministic aperture position along a canonical edge, in 1..CHUNK_CELLS-1
/// (never a corner, so apertures of perpendicular edges cannot collide).
fn aperture_pos(world_seed: u64, kx: i32, kz: i32, axis: EdgeAxis, layer_index: i32) -> usize {
    // Constante de dominio: separa el espacio de seeds de borde del de capas.
    let mut s = world_seed ^ 0xED6E_C0A7_05EA_05ED;
    s ^= (kx as i64 as u64).wrapping_mul(0x9e37_79b9_7f4a_7c15);
    s ^= (kz as i64 as u64).wrapping_mul(0x6c62_272e_07bb_0142);
    s ^= (axis as u64).wrapping_mul(0xa5a5_a5a5_a5a5_a5a5);
    s ^= (layer_index as u64).wrapping_mul(1337);
    StdRng::seed_from_u64(s).gen_range(1..CHUNK_CELLS - 1)
}

/// Open the border cell at `start` and carve inward (direction `dir`) through
/// Wall cells until reaching an already-walkable cell. Stops without carving
/// if it meets stamped content (Void/Pillar) — "estampar gana"; the repair
/// pass after stitching reconnects around it.
fn carve_aperture(
    grid: &mut LayerGrid,
    rules: &LayerRules,
    start: (usize, usize),
    dir: (i32, i32),
) {
    let corr = Cell::new(CellType::Corridor, rules.ceiling_corridor, 0);
    grid.set(start.0, start.1, corr);

    let (mut x, mut z) = (start.0 as i32, start.1 as i32);
    let last = (CHUNK_CELLS - 1) as i32;
    loop {
        x += dir.0;
        z += dir.1;
        // Nunca escribir celdas de borde distintas de la apertura propia: si el
        // carve cruza el chunk entero sin tocar nada transitable, taladraría el
        // borde opuesto creando una apertura unilateral que el vecino no conoce.
        if x <= 0 || z <= 0 || x >= last || z >= last {
            return; // el pase de reparación conecta el túnel al laberinto
        }
        let cell = grid.get(x as usize, z as usize);
        if cell.is_walkable() {
            return; // reached the maze
        }
        if cell.kind() != CellType::Wall {
            return; // stamped content (Void/Pillar): stop, repair pass handles it
        }
        grid.set(x as usize, z as usize, corr);
    }
}
