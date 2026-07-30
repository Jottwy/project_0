//! Seven-phase deterministic maze builder — §4 of the grid design document.
//!
//! Invariants upheld here (§9):
//! - Same (world_seed, chunk_coord, layer_index) → byte-identical grid on all peers.
//! - Border cells (row/col 0 and CHUNK_CELLS-1) are never carved; reserved for seam
//!   stitching (Block E).
//! - Open zones overwrite the maze ("stamp wins", §5).
//! - Stairs/pits return required-walkable positions; caller stamps those into the
//!   adjacent layer so no stair ever leads into a wall.

use rand::rngs::StdRng;
use rand::seq::SliceRandom;
use rand::{Rng, SeedableRng};

use super::{Cell, CellType, LayerRules, CHUNK_CELLS};

/// Highest odd-index maze node. Leaves index 0 and CHUNK_CELLS-1 as solid border.
const NODE_MAX: i32 = CHUNK_CELLS as i32 - 3; // 17 for CHUNK_CELLS=20

// ── Public types ──────────────────────────────────────────────────────────────

/// Flat 20×20 grid of cells for one layer of one chunk.
/// Index layout: `cells[z * CHUNK_CELLS + x]`.
pub struct LayerGrid {
    cells: Vec<Cell>,
}

impl LayerGrid {
    pub fn new_solid() -> Self {
        Self {
            cells: vec![Cell::SOLID_WALL; CHUNK_CELLS * CHUNK_CELLS],
        }
    }

    #[inline]
    pub fn get(&self, x: usize, z: usize) -> Cell {
        self.cells[z * CHUNK_CELLS + x]
    }

    #[inline]
    pub fn set(&mut self, x: usize, z: usize, cell: Cell) {
        self.cells[z * CHUNK_CELLS + x] = cell;
    }

    /// True if (x, z) is within the grid.
    #[inline]
    pub fn in_bounds(x: i32, z: i32) -> bool {
        x >= 0 && z >= 0 && (x as usize) < CHUNK_CELLS && (z as usize) < CHUNK_CELLS
    }

    /// True if (x, z) is not on the border row/column (borders reserved for seam stitching).
    #[inline]
    fn is_interior(x: i32, z: i32) -> bool {
        x > 0 && z > 0 && (x as usize) < CHUNK_CELLS - 1 && (z as usize) < CHUNK_CELLS - 1
    }

    /// Raw cell slice for serialization and tests.
    pub fn cells(&self) -> &[Cell] {
        &self.cells
    }
}

/// Output of one `generate_layer` call.
pub struct LayerOutput {
    pub grid: LayerGrid,
    /// Positions in `layer_index + 1` that must be walkable (from stairs placed here).
    /// Caller passes these as `forced_walkable` when generating the layer above.
    pub require_walkable_above: Vec<(u8, u8)>,
    /// Positions in `layer_index - 1` that must be walkable (from pits placed here).
    pub require_walkable_below: Vec<(u8, u8)>,
}

// ── Main entry point ──────────────────────────────────────────────────────────

/// Generate one layer of one chunk. Fully deterministic.
///
/// - `rules`: personality profile (from `LAYER_PROFILES`).
/// - `world_seed`: shared seed, identical on all peers.
/// - `chunk_coord`: `(cx, cz)` chunk position in the world grid.
/// - `layer_index`: macro layer index (0 = El Vestíbulo …).
/// - `forced_walkable`: positions that must be walkable, passed down from adjacent
///   layers' `require_walkable_above` / `require_walkable_below` (§5 guarantee).
pub fn generate_layer(
    rules: &LayerRules,
    world_seed: u64,
    chunk_coord: (i32, i32),
    layer_index: i32,
    forced_walkable: &[(u8, u8)],
) -> LayerOutput {
    let seed = derive_seed(world_seed, chunk_coord, layer_index);
    let mut rng = StdRng::seed_from_u64(seed);
    let mut grid = LayerGrid::new_solid();
    let corr = |h: u8| Cell::new(CellType::Corridor, h, 0);

    // ── Phase 1 — Recursive-backtracker maze ─────────────────────────────────
    //
    // Works on odd-index node positions (1, 3, … NODE_MAX); each step carves
    // the intermediate wall cell and the destination node.
    //
    // Stack management:
    //   82% → push destination (DFS bias → long corridors).
    //   18% → drop a random stack entry (ramification / shorter branches).
    let mut wide_marks: Vec<(i32, i32)> = Vec::new();
    let mut stack: Vec<(i32, i32)> = vec![(1, 1)];
    grid.set(1, 1, corr(rules.ceiling_corridor));

    while let Some(&(cx, cz)) = stack.last() {
        let unvisited: Vec<(i32, i32)> = [(-2i32, 0i32), (2, 0), (0, -2), (0, 2)]
            .iter()
            .filter_map(|&(dx, dz)| {
                let (nx, nz) = (cx + dx, cz + dz);
                if nx >= 1
                    && nz >= 1
                    && nx <= NODE_MAX
                    && nz <= NODE_MAX
                    && grid.get(nx as usize, nz as usize).is_solid()
                {
                    Some((nx, nz))
                } else {
                    None
                }
            })
            .collect();

        if unvisited.is_empty() {
            stack.pop();
            continue;
        }

        let (nx, nz) = unvisited[rng.gen_range(0..unvisited.len())];
        let (mx, mz) = ((cx + nx) / 2, (cz + nz) / 2);

        grid.set(mx as usize, mz as usize, corr(rules.ceiling_corridor));
        grid.set(nx as usize, nz as usize, corr(rules.ceiling_corridor));

        if rng.gen::<f32>() < rules.wide_chance {
            wide_marks.push((mx, mz));
            wide_marks.push((nx, nz));
        }

        if rng.gen::<f32>() < 0.82 {
            stack.push((nx, nz));
        } else if !stack.is_empty() {
            let drop = rng.gen_range(0..stack.len());
            stack.remove(drop);
        }
    }

    // ── Phase 2 — Widen marked passages ──────────────────────────────────────
    //
    // For each wide-marked cell, open [+1,0], [0,+1], [+1,+1] if solid.
    // Interior-only: borders stay solid for seam stitching.
    for &(wx, wz) in &wide_marks {
        for (dx, dz) in [(1i32, 0i32), (0, 1), (1, 1)] {
            let (nx, nz) = (wx + dx, wz + dz);
            if LayerGrid::is_interior(nx, nz) && grid.get(nx as usize, nz as usize).is_solid() {
                grid.set(nx as usize, nz as usize, corr(rules.ceiling_corridor));
            }
        }
    }

    // ── Phase 3 — Erosion ─────────────────────────────────────────────────────
    //
    // Snapshot the grid; open each solid interior cell that has ≥2 orthogonal
    // floor neighbours in the snapshot (controlled by erode_chance).
    let snapshot = grid.cells.clone();
    for z in 1..(CHUNK_CELLS - 1) {
        for x in 1..(CHUNK_CELLS - 1) {
            if !snapshot[z * CHUNK_CELLS + x].is_solid() {
                continue;
            }
            let floor_n = [(-1i32, 0i32), (1, 0), (0, -1), (0, 1)]
                .iter()
                .filter(|&&(dx, dz)| {
                    let (nx, nz) = (x as i32 + dx, z as i32 + dz);
                    LayerGrid::in_bounds(nx, nz)
                        && !snapshot[nz as usize * CHUNK_CELLS + nx as usize].is_solid()
                })
                .count();
            if floor_n >= 2 && rng.gen::<f32>() < rules.erode_chance {
                grid.set(x, z, corr(rules.ceiling_corridor));
            }
        }
    }

    // ── Phase 4 — Open zones ("stamp wins" — overwrite maze) ─────────────────
    //
    // Stamps `num_open_zones` rectangles of size ~open_zone_size as OPEN cells.
    // Zones track zone_id (1-based) so reconnection and later phases can
    // identify which cells belong to which zone.
    // Pillar grids are seeded inside zones whose side length is ≥ 6 cells.
    let mut zones: Vec<(i32, i32, i32, i32)> = Vec::new(); // (x0, z0, x1, z1) exclusive
    for zone_idx in 0..rules.num_open_zones {
        let sz = rules.open_zone_size as i32;
        let max_origin = (CHUNK_CELLS as i32 - 1 - sz).max(1);
        let x0 = rng.gen_range(1..=max_origin);
        let z0 = rng.gen_range(1..=max_origin);
        let x1 = (x0 + sz).min(CHUNK_CELLS as i32 - 1);
        let z1 = (z0 + sz).min(CHUNK_CELLS as i32 - 1);
        let zid = zone_idx as u16 + 1;

        for cz in z0..z1 {
            for cx in x0..x1 {
                grid.set(
                    cx as usize,
                    cz as usize,
                    Cell::new(CellType::Open, rules.ceiling_open, zid),
                );
            }
        }

        if sz >= 6 {
            let mut pz = z0 + 2;
            while pz < z1 - 1 {
                let mut px = x0 + 2;
                while px < x1 - 1 {
                    if rng.gen::<f32>() < rules.pillar_chance {
                        grid.set(
                            px as usize,
                            pz as usize,
                            Cell::new(CellType::Pillar, 0, zid),
                        );
                    }
                    px += 3;
                }
                pz += 3;
            }
        }

        zones.push((x0, z0, x1, z1));
    }

    // ── Phase 5 — Reconnect isolated zones (§5 "reconectar después") ─────────
    //
    // Each zone must have ≥1 walkable neighbour outside itself.
    // If isolated, punch a corridor from its border to the nearest existing corridor.
    //
    // NOTE (corrección al plano §4/§5): la validación de conectividad por
    // flood-fill que el documento sitúa como "opcional en fase 5" va en
    // realidad POST-Fase 6: los Void estampados en Fase 6 no son transitables
    // y pueden cortar caminos que esta fase acaba de garantizar. Ver el pase
    // de reparación `repair_connectivity` más abajo.
    for &(x0, z0, x1, z1) in &zones {
        if !is_zone_connected(&grid, x0, z0, x1, z1) {
            connect_zone_to_maze(&mut grid, x0, z0, x1, z1, rules.ceiling_corridor);
        }
    }

    // ── Phase 6 — Voids and anomalies (inside zones) ──────────────────────────
    //
    // Voids are placed freely inside zones. Anomalies avoid Void cells (up to
    // 10× retry budget per anomaly).
    if !zones.is_empty() {
        for _ in 0..rules.num_voids {
            let i = rng.gen_range(0..zones.len());
            let (x0, z0, x1, z1) = zones[i];
            let x = rng.gen_range(x0..x1) as usize;
            let z = rng.gen_range(z0..z1) as usize;
            grid.set(x, z, Cell::new(CellType::Void, 0, 0));
        }

        let mut budget = rules.num_anomalies * 10;
        let mut placed = 0u32;
        while placed < rules.num_anomalies && budget > 0 {
            budget -= 1;
            let i = rng.gen_range(0..zones.len());
            let (x0, z0, x1, z1) = zones[i];
            let x = rng.gen_range(x0..x1) as usize;
            let z = rng.gen_range(z0..z1) as usize;
            if grid.get(x, z).kind() != CellType::Void {
                grid.set(x, z, Cell::new(CellType::Anomaly, rules.ceiling_open, 0));
                placed += 1;
            }
        }
    }

    // Stamp forced positions from adjacent layers (§5 "escaleras que suben a
    // una pared = nunca"). Va ANTES del pase de reparación para que una celda
    // forzada sobre muro aislado quede conectada; ninguna fase posterior
    // elimina transitabilidad, así que sobreviven igual.
    for &(fx, fz) in forced_walkable {
        let (fx, fz) = (fx as usize, fz as usize);
        if fx < CHUNK_CELLS && fz < CHUNK_CELLS && !grid.get(fx, fz).is_walkable() {
            grid.set(fx, fz, corr(rules.ceiling_corridor));
        }
    }

    // ── Connectivity repair (post-Fase 6, pre-Fase 7) ────────────────────────
    //
    // Corrección al orden del pseudocódigo §4: los Void de Fase 6 pueden aislar
    // componentes que Fase 5 había reconectado. Se repara aquí, ANTES de Fase 7,
    // para que escaleras y pozos caigan siempre sobre un grid ya conexo.
    // Determinista: orden de escaneo del grid, sin RNG.
    repair_connectivity(&mut grid, rules.ceiling_corridor);

    // ── Phase 7 — Vertical connections ───────────────────────────────────────
    //
    // Shuffle all walkable cells; take the first (num_stairs + num_pits) as a
    // conflict-free pool (each cell gets at most one transition type).
    // Stairs → record in require_walkable_above.
    // Pits   → record in require_walkable_below.
    // forced_walkable positions from the caller are stamped as Corridor to
    // guarantee no stair/pit in an adjacent layer leads into a wall (§5).
    let mut require_walkable_above: Vec<(u8, u8)> = Vec::new();
    let mut require_walkable_below: Vec<(u8, u8)> = Vec::new();

    let mut walkable: Vec<(usize, usize)> = (0..CHUNK_CELLS)
        .flat_map(|z| (0..CHUNK_CELLS).map(move |x| (x, z)))
        .filter(|&(x, z)| grid.get(x, z).is_walkable())
        .collect();
    walkable.shuffle(&mut rng);

    let stair_n = rules.num_stairs as usize;
    let pit_n = rules.num_pits as usize;
    let take = walkable.len().min(stair_n + pit_n);

    for (i, &(x, z)) in walkable[..take].iter().enumerate() {
        if i < stair_n {
            grid.set(x, z, Cell::new(CellType::Stair, rules.ceiling_corridor, 0));
            require_walkable_above.push((x as u8, z as u8));
        } else {
            grid.set(x, z, Cell::new(CellType::Pit, rules.ceiling_corridor, 0));
            require_walkable_below.push((x as u8, z as u8));
        }
    }

    LayerOutput {
        grid,
        require_walkable_above,
        require_walkable_below,
    }
}

// ── Private helpers ───────────────────────────────────────────────────────────

/// Connectivity repair pass (post-Fase 6).
///
/// Flood-fills walkable cells into components; while more than one component
/// exists, carves a wall corridor (BFS shortest path) from the LARGEST
/// component (tie-break: lowest scan-order id) to the nearest cell of any
/// other component. La raíz debe ser la componente más grande: si fuera la
/// primera en orden de escaneo y esa fuera un bolsillo, el sellado de abajo
/// destruiría el laberinto entero.
///
/// "Estampar gana": el camino NUNCA atraviesa Void ni Pillar — son contenido
/// estampado. Solo se carvan celdas Wall interiores (los bordes quedan
/// reservados para el costurado).
///
/// Bolsillos irreparables (celdas transitables encapsuladas por Voids, sin
/// ningún Wall carvable hacia ellas): se SELLAN a Wall. Un bolsillo de 1–2
/// celdas dentro de un campo de voids es ruido de estampado, no espacio
/// jugable; sellarlo preserva el invariante de conectividad total sin tocar
/// ningún Void.
pub(super) fn repair_connectivity(grid: &mut LayerGrid, ceiling: u8) {
    use std::collections::VecDeque;

    loop {
        // 1. Etiquetar componentes transitables en orden de escaneo.
        let mut comp = vec![u32::MAX; CHUNK_CELLS * CHUNK_CELLS];
        let mut n_comps = 0u32;
        for z in 0..CHUNK_CELLS {
            for x in 0..CHUNK_CELLS {
                if !grid.get(x, z).is_walkable() || comp[z * CHUNK_CELLS + x] != u32::MAX {
                    continue;
                }
                let id = n_comps;
                n_comps += 1;
                let mut stack = vec![(x, z)];
                comp[z * CHUNK_CELLS + x] = id;
                while let Some((cx, cz)) = stack.pop() {
                    for (dx, dz) in [(-1i32, 0i32), (1, 0), (0, -1), (0, 1)] {
                        let (nx, nz) = (cx as i32 + dx, cz as i32 + dz);
                        if LayerGrid::in_bounds(nx, nz) {
                            let (nx, nz) = (nx as usize, nz as usize);
                            if grid.get(nx, nz).is_walkable()
                                && comp[nz * CHUNK_CELLS + nx] == u32::MAX
                            {
                                comp[nz * CHUNK_CELLS + nx] = id;
                                stack.push((nx, nz));
                            }
                        }
                    }
                }
            }
        }
        if n_comps <= 1 {
            return;
        }

        // Raíz = componente más grande (desempate: menor id, determinista).
        let mut sizes = vec![0usize; n_comps as usize];
        for &c in &comp {
            if c != u32::MAX {
                sizes[c as usize] += 1;
            }
        }
        let root = sizes
            .iter()
            .enumerate()
            .max_by(|a, b| a.1.cmp(b.1).then(b.0.cmp(&a.0)))
            .map(|(id, _)| id as u32)
            .unwrap();

        // 2. BFS desde toda la componente raíz a través de celdas transitables o
        //    Wall interior (nunca Void/Pillar/borde) hasta tocar otra componente.
        let mut parent = vec![usize::MAX; CHUNK_CELLS * CHUNK_CELLS];
        let mut visited = vec![false; CHUNK_CELLS * CHUNK_CELLS];
        let mut queue = VecDeque::new();
        for z in 0..CHUNK_CELLS {
            for x in 0..CHUNK_CELLS {
                if comp[z * CHUNK_CELLS + x] == root {
                    visited[z * CHUNK_CELLS + x] = true;
                    queue.push_back((x, z));
                }
            }
        }

        let mut hit: Option<usize> = None;
        'bfs: while let Some((x, z)) = queue.pop_front() {
            for (dx, dz) in [(-1i32, 0i32), (1, 0), (0, -1), (0, 1)] {
                let (nx, nz) = (x as i32 + dx, z as i32 + dz);
                if !LayerGrid::in_bounds(nx, nz) {
                    continue;
                }
                let (nxu, nzu) = (nx as usize, nz as usize);
                let ni = nzu * CHUNK_CELLS + nxu;
                if visited[ni] {
                    continue;
                }
                let cell = grid.get(nxu, nzu);
                let passable = cell.is_walkable()
                    || (cell.kind() == CellType::Wall
                        && nx > 0
                        && nz > 0
                        && nxu < CHUNK_CELLS - 1
                        && nzu < CHUNK_CELLS - 1);
                if !passable {
                    continue;
                }
                visited[ni] = true;
                parent[ni] = z * CHUNK_CELLS + x;
                if comp[ni] != u32::MAX && comp[ni] != root {
                    hit = Some(ni);
                    break 'bfs;
                }
                queue.push_back((nxu, nzu));
            }
        }

        let Some(hit) = hit else {
            // Bolsillos encapsulados por Voids sin Wall carvable hacia ellos:
            // irreparables respetando "estampar gana" → se sellan a Wall.
            // `visited` marca todo lo alcanzable desde la componente raíz
            // (incluyendo a través de muros), así que lo transitable no
            // visitado es exactamente el conjunto de bolsillos.
            for z in 0..CHUNK_CELLS {
                for x in 0..CHUNK_CELLS {
                    if grid.get(x, z).is_walkable() && !visited[z * CHUNK_CELLS + x] {
                        grid.set(x, z, Cell::SOLID_WALL);
                    }
                }
            }
            return;
        };

        // 3. Carvar las celdas Wall del camino reconstruido.
        let mut i = hit;
        while parent[i] != usize::MAX {
            let (x, z) = (i % CHUNK_CELLS, i / CHUNK_CELLS);
            if grid.get(x, z).kind() == CellType::Wall {
                grid.set(x, z, Cell::new(CellType::Corridor, ceiling, 0));
            }
            i = parent[i];
        }
    }
}

/// SplitMix64 finalizer — full-avalanche bit diffusion of a single word.
#[inline]
fn splitmix64(mut z: u64) -> u64 {
    z = (z ^ (z >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    z ^ (z >> 31)
}

/// Fold one value into a running hash state. Order-dependent (so `(cx, cz)` and
/// `(cz, cx)` diverge) and fully diffused, unlike the previous commutative
/// XOR-of-products mix where symmetric coordinates could correlate.
#[inline]
pub(super) fn mix(state: u64, value: u64) -> u64 {
    splitmix64(
        state
            .wrapping_add(0x9e37_79b9_7f4a_7c15)
            .wrapping_add(value),
    )
}

/// Deterministic seed for one (chunk, layer) pair, unique across the world grid.
fn derive_seed(world_seed: u64, (cx, cz): (i32, i32), layer_index: i32) -> u64 {
    let mut s = world_seed;
    s = mix(s, cx as i64 as u64);
    s = mix(s, cz as i64 as u64);
    s = mix(s, layer_index as i64 as u64);
    s
}

/// True if the zone has at least one walkable neighbour cell outside its bounds.
pub(super) fn is_zone_connected(grid: &LayerGrid, x0: i32, z0: i32, x1: i32, z1: i32) -> bool {
    for cz in z0..z1 {
        for cx in x0..x1 {
            for (dx, dz) in [(-1i32, 0i32), (1, 0), (0, -1), (0, 1)] {
                let (nx, nz) = (cx + dx, cz + dz);
                if !LayerGrid::in_bounds(nx, nz) {
                    continue;
                }
                if nx >= x0 && nx < x1 && nz >= z0 && nz < z1 {
                    continue; // still inside the zone
                }
                if grid.get(nx as usize, nz as usize).is_walkable() {
                    return true;
                }
            }
        }
    }
    false
}

/// Punch a single-cell-wide corridor from the zone border to the nearest
/// existing maze corridor outside the zone.
///
/// "Estampar gana": el camino solo carva celdas `Wall` INTERIORES — nunca Void
/// ni Pillar, que son contenido estampado por las Fases 4 y 6. Mismo criterio,
/// literalmente la misma guarda, que `repair_connectivity` (`kind() == Wall`) y
/// que `carve_aperture` del costurado, que se detiene ante contenido estampado
/// ("stamped content (Void/Pillar): stop", `stitching.rs`). Antes esta función
/// usaba `!is_walkable()`, que trata Pillar y Void como material carvable y por
/// tanto los borraba — era la única de las tres rutas de carvado que rompía el
/// invariante.
///
/// Bordes: ni la búsqueda de destino ni la carvada tocan ya la fila/columna 0 y
/// `CHUNK_CELLS - 1`, reservadas para el costurado. La restricción es HOY
/// inocua —en Fase 5 los bordes siguen sólidos, así que jamás serían destino—,
/// pero se hace explícita a propósito: es defensa ante un reordenado de fases.
/// Si el costurado llegara a correr antes que esta función, un destino en el
/// borde haría que la L taladrase una apertura unilateral que el chunk vecino
/// no conoce. Misma defensa, y por la misma razón, que la guarda de borde de
/// `carve_aperture`.
pub(super) fn connect_zone_to_maze(
    grid: &mut LayerGrid,
    x0: i32,
    z0: i32,
    x1: i32,
    z1: i32,
    ceiling: u8,
) {
    let cx_center = (x0 + x1) / 2;
    let cz_center = (z0 + z1) / 2;
    let last = CHUNK_CELLS as i32 - 1;

    // Find nearest walkable cell outside the zone (Manhattan distance). Interior
    // only: a border cell must never become the target (see the note above).
    let mut best_dist = i32::MAX;
    let mut target: Option<(i32, i32)> = None;
    for z in 1..last {
        for x in 1..last {
            if x >= x0 && x < x1 && z >= z0 && z < z1 {
                continue;
            }
            if grid.get(x as usize, z as usize).is_walkable() {
                let d = (x - cx_center).abs() + (z - cz_center).abs();
                if d < best_dist {
                    best_dist = d;
                    target = Some((x, z));
                }
            }
        }
    }

    let (tx, tz) = match target {
        Some(p) => p,
        None => {
            // No maze exists yet — open the zone's first interior corner.
            grid.set(
                x0 as usize,
                z0 as usize,
                Cell::new(CellType::Corridor, ceiling, 0),
            );
            return;
        }
    };

    // Walk from the zone-border cell closest to the target toward the target,
    // carving every solid cell along the way.
    let bx = tx.clamp(x0, x1 - 1);
    let bz = tz.clamp(z0, z1 - 1);
    let (mut cx, mut cz) = (bx, bz);

    while cx != tx || cz != tz {
        let dx = (tx - cx).signum();
        let dz = (tz - cz).signum();
        if dx != 0 {
            cx += dx;
        } else {
            cz += dz;
        }
        // Solo muro interior. El contenido estampado (Void/Pillar) se respeta y
        // el camino sigue avanzando sin escribir; lo que quede suelto lo
        // reconecta `repair_connectivity` (post-Fase 6), que es quien tiene
        // permiso para buscar una ruta alternativa alrededor del estampado.
        if LayerGrid::is_interior(cx, cz)
            && grid.get(cx as usize, cz as usize).kind() == CellType::Wall
        {
            grid.set(
                cx as usize,
                cz as usize,
                Cell::new(CellType::Corridor, ceiling, 0),
            );
        }
    }
}

#[cfg(test)]
mod hash_tests {
    use super::*;

    /// El fix: el plegado SplitMix64 NO conmuta (a diferencia del XOR-of-products
    /// anterior), así que transponer entradas cambia la seed → coords de chunk
    /// simétricas respecto a la diagonal dejan de correlacionar. Además, un cambio
    /// de un solo bit en la entrada debe difundirse ampliamente (avalancha).
    #[test]
    fn mix_is_order_dependent_and_diffuses() {
        let s = 0xBACC_0085;
        assert_ne!(mix(mix(s, 3), 7), mix(mix(s, 7), 3), "mix no debe conmutar");
        let flips = (mix(s, 0) ^ mix(s, 1)).count_ones();
        assert!(
            flips >= 8,
            "un bit de entrada debe avalanchar, solo {flips} bits cambiaron"
        );
    }
}
