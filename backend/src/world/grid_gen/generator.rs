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

use super::layer_rules::default_room_type_weights;
use super::{Cell, CellType, LayerRules, CHUNK_CELLS};

/// Ancho CAMINABLE fijo (celdas) de una zona `CorridorSpine` — 2 celdas = 5 m,
/// el mismo ancho de tile que usa el resto del render (`tile_walls.rs`).
/// Documentado en DECISIONS.md (RoomType): un pasillo más ancho empezaría a
/// sentirse como una sala, y 2 es el mínimo que sigue leyéndose como
/// "corredor" a escala humana.
///
/// NO es el ancho total del rect: como `SealedRoom`, el perímetro (aquí, los
/// 2 lados largos) es un anillo INTERIOR al footprint, no una franja aparte
/// fuera de él — así que el rect necesita `CORRIDOR_SPINE_WIDTH + 2` de ancho
/// total para alojar 2 celdas de pared (una a cada lado largo) MÁS las
/// `CORRIDOR_SPINE_WIDTH` celdas de suelo caminable en medio. Con solo 2
/// celdas de footprint total, ambas serían pared y no quedaría suelo.
const CORRIDOR_SPINE_WIDTH: i32 = 2;

/// Footprint total del eje angosto de un `CorridorSpine` — ver
/// `CORRIDOR_SPINE_WIDTH`.
const CORRIDOR_SPINE_TOTAL_WIDTH: i32 = CORRIDOR_SPINE_WIDTH + 2;

/// Tipo de zona estampado en Fase 4. `Open` es el único que existía antes de
/// RoomType; `SealedRoom`/`CorridorSpine` estampan un perímetro `SealedWall`
/// en vez de dejarlo a merced del maze/erosión circundante.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub(super) enum RoomType {
    Open,
    SealedRoom,
    CorridorSpine,
}

impl RoomType {
    /// `true` para los tipos con perímetro `SealedWall` propio. `Open` no
    /// tiene perímetro protegido — nada que alinear, el "muro" que la rodea
    /// es maze normal y ya lo maneja `tile_walls_from_grid` como cualquier
    /// otro tramo de pasillo.
    pub(super) fn has_sealed_perimeter(self) -> bool {
        matches!(self, RoomType::SealedRoom | RoomType::CorridorSpine)
    }
}

/// Redondea `sz` al par superior más cercano.
///
/// El perímetro de `SealedRoom`/`CorridorSpine` es una columna/fila de 1
/// celda (2.5 m) de grosor. `tile_walls_from_grid` (Fase 4.1, IPC) cuantiza a
/// tiles de 5 m: una arista de tile solo se dibuja como pared si AMBAS
/// sub-filas/columnas del tile fallan en ser transitables (`edge_open`,
/// `tile_walls.rs`). Si el ancho/alto del rect es impar, un extremo del
/// perímetro cae a mitad de tile — la cuantización se come esa pared y deja
/// un hueco (la cuña triangular negra observada en playtest). Alinear SOLO el
/// origen no basta: mueve el problema al lado opuesto. Hacen falta origen Y
/// tamaño pares — entonces ambos extremos (x0 Y x1, o z0 Y z1) caen justo en
/// un límite de tile.
pub(super) fn tile_aligned_size(sz: i32) -> i32 {
    if sz % 2 == 0 {
        sz
    } else {
        sz + 1
    }
}

/// Redondea `origin` al par más cercano dentro de `[1, max_origin]`.
///
/// Un origen par pone la celda x0/z0 exactamente en el borde oeste/norte de
/// un tile de 5 m en vez de a mitad de tile — ver `tile_aligned_size` para el
/// porqué. Prefiere subir (se aleja del borde reservado en 0); si eso excede
/// `max_origin`, baja. El clamp final a `max_origin` cubre el caso degenerado
/// de una zona casi del tamaño del chunk entero — no ocurre en producción hoy
/// (layer 0 tiene una sola zona de 7-8 celdas en un chunk de 20), así que ese
/// caso puede quedar sin origen par exacto sin que nada se rompa (el clamp de
/// `x1`/`z1` en el llamador ya protege los límites absolutos del grid).
pub(super) fn align_origin_to_tile(origin: i32, max_origin: i32) -> i32 {
    if origin % 2 == 0 {
        return origin;
    }
    let up = origin + 1;
    if up <= max_origin {
        up
    } else {
        (origin - 1).max(1)
    }
}

/// Sorteo ponderado de `RoomType` contra `(open, sealed, spine)`. Solo se
/// llama cuando los pesos NO son el default — ver el caller en Fase 4, que
/// evita esta función (y el draw que consume) por completo en ese caso.
pub(super) fn pick_room_type(weights: (f32, f32, f32), rng: &mut StdRng) -> RoomType {
    let (open, sealed, spine) = weights;
    let total = open + sealed + spine;
    if total <= 0.0 {
        return RoomType::Open;
    }
    let roll = rng.gen::<f32>() * total;
    if roll < open {
        RoomType::Open
    } else if roll < open + sealed {
        RoomType::SealedRoom
    } else {
        RoomType::CorridorSpine
    }
}

/// Seed determinista por zona para la selección de entradas
/// (SealedRoom/CorridorSpine). Stream INDEPENDIENTE del `rng` compartido de
/// Fase 4 — mismo patrón que `aperture_pos` en `stitching.rs` — así que
/// activar RoomType nunca perturba el resto del sorteo de la capa (el sorteo
/// de TIPO sí usa el `rng` compartido; la posición/cuenta de entradas, no).
pub(super) fn zone_entrance_seed(
    world_seed: u64,
    (cx, cz): (i32, i32),
    layer_index: i32,
    zone_idx: u32,
) -> u64 {
    // Constante de dominio propia, distinta de la de `aperture_pos`
    // (`0xED6E_C0A7_05EA_05ED`) y de `derive_seed` (sin máscara) — tres
    // espacios de seed disjuntos por construcción.
    let mut s = world_seed ^ 0x2E17_5A17_E17E_A17E;
    s = mix(s, cx as i64 as u64);
    s = mix(s, cz as i64 as u64);
    s = mix(s, layer_index as i64 as u64);
    s = mix(s, zone_idx as u64);
    s
}

/// Estampa una zona `RoomType::Open` — comportamiento histórico, sin cambios.
pub(super) fn stamp_open_zone(
    grid: &mut LayerGrid,
    x0: i32,
    z0: i32,
    x1: i32,
    z1: i32,
    zid: u16,
    ceiling_open: u8,
) {
    for cz in z0..z1 {
        for cx in x0..x1 {
            grid.set(
                cx as usize,
                cz as usize,
                Cell::new(CellType::Open, ceiling_open, zid),
            );
        }
    }
}

/// Estampa una `SealedRoom`: perímetro completo (el anillo exterior del rect,
/// no un anillo aparte) a `SealedWall`, interior a `Open`. Las entradas se
/// carvan por separado (`carve_sealed_room_entrances`), después de estampar,
/// para poder breachear el perímetro que se acaba de escribir.
pub(super) fn stamp_sealed_room(
    grid: &mut LayerGrid,
    x0: i32,
    z0: i32,
    x1: i32,
    z1: i32,
    zid: u16,
    ceiling_open: u8,
) {
    for cz in z0..z1 {
        for cx in x0..x1 {
            let perimeter = cx == x0 || cx == x1 - 1 || cz == z0 || cz == z1 - 1;
            let cell = if perimeter {
                Cell::new(CellType::SealedWall, 0, zid)
            } else {
                Cell::new(CellType::Open, ceiling_open, zid)
            };
            grid.set(cx as usize, cz as usize, cell);
        }
    }
}

/// Estampa un `CorridorSpine`: los 2 lados LARGOS (perpendiculares al eje
/// mayor) a `SealedWall`; los 2 extremos CORTOS quedan `Open`, sin sellar —
/// las entradas se carvan hacia fuera desde ahí
/// (`carve_corridor_spine_entrances`), no perforando un perímetro propio (no
/// hay perímetro en los extremos cortos, solo en los lados largos).
// TODO(refactor): 8 argumentos porque toma el rect (x0,z0,x1,z1) desuelto en
// vez de un tipo `Rect`; mismo criterio que los `too_many_arguments` ya
// aceptados en el repo (game_loop.rs, volumetric_grid.rs, v30a_showcase.rs) —
// riesgo cero, el refactor de firma queda para sesión propia.
#[allow(clippy::too_many_arguments)]
pub(super) fn stamp_corridor_spine(
    grid: &mut LayerGrid,
    x0: i32,
    z0: i32,
    x1: i32,
    z1: i32,
    zid: u16,
    ceiling_open: u8,
    horizontal: bool,
) {
    for cz in z0..z1 {
        for cx in x0..x1 {
            let long_side = if horizontal {
                cz == z0 || cz == z1 - 1
            } else {
                cx == x0 || cx == x1 - 1
            };
            let cell = if long_side {
                Cell::new(CellType::SealedWall, 0, zid)
            } else {
                Cell::new(CellType::Open, ceiling_open, zid)
            };
            grid.set(cx as usize, cz as usize, cell);
        }
    }
}

/// Candidatos de entrada para una `SealedRoom`: cada celda de perímetro que
/// NO es esquina (dirección de breach ambigua en una esquina), con su
/// dirección de carvado hacia fuera. Mismo criterio que `aperture_pos`
/// (`stitching.rs`) de evitar esquinas para que direcciones perpendiculares
/// nunca colisionen.
fn sealed_room_entrance_candidates(
    x0: i32,
    z0: i32,
    x1: i32,
    z1: i32,
) -> Vec<((i32, i32), (i32, i32))> {
    let mut v = Vec::new();
    for x in (x0 + 1)..(x1 - 1) {
        v.push(((x, z0), (0, -1))); // lado norte
        v.push(((x, z1 - 1), (0, 1))); // lado sur
    }
    for z in (z0 + 1)..(z1 - 1) {
        v.push(((x0, z), (-1, 0))); // lado oeste
        v.push(((x1 - 1, z), (1, 0))); // lado este
    }
    v
}

/// Carva de 1 a 3 entradas de una `SealedRoom` (rango fijo; el CONTEO además
/// del punto/dirección viene del hash determinista de la zona). Si la zona es
/// demasiado pequeña para tener un solo candidato válido (ningún lado con
/// celda no-esquina), no carva nada — límite conocido de zonas diminutas, no
/// tratado aquí.
pub(super) fn carve_sealed_room_entrances(
    grid: &mut LayerGrid,
    ceiling: u8,
    seed: u64,
    x0: i32,
    z0: i32,
    x1: i32,
    z1: i32,
) {
    let mut candidates = sealed_room_entrance_candidates(x0, z0, x1, z1);
    if candidates.is_empty() {
        return;
    }
    let mut hash_rng = StdRng::seed_from_u64(seed);
    let n = hash_rng.gen_range(1..=3usize.min(candidates.len()));
    candidates.shuffle(&mut hash_rng);
    for &(start, dir) in candidates.iter().take(n) {
        carve_explicit_entrance(grid, ceiling, start, dir);
    }
}

/// Carva las EXACTAS 2 entradas de un `CorridorSpine`, una en cada extremo
/// corto. A diferencia de `SealedRoom`, el punto de partida no es una celda de
/// perímetro propio (los extremos cortos ya son `Open`, sin sellar): es la
/// celda del propio extremo, y la carvada se extiende hacia fuera desde ahí.
#[allow(clippy::too_many_arguments)] // mismo motivo que stamp_corridor_spine
pub(super) fn carve_corridor_spine_entrances(
    grid: &mut LayerGrid,
    ceiling: u8,
    seed: u64,
    x0: i32,
    z0: i32,
    x1: i32,
    z1: i32,
    horizontal: bool,
) {
    let mut hash_rng = StdRng::seed_from_u64(seed);
    if horizontal {
        let span = (z1 - z0 - 2).max(1); // evita las esquinas (lado largo N/S)
        let z_a = z0 + 1 + hash_rng.gen_range(0..span);
        let z_b = z0 + 1 + hash_rng.gen_range(0..span);
        carve_explicit_entrance(grid, ceiling, (x0, z_a), (-1, 0)); // extremo oeste
        carve_explicit_entrance(grid, ceiling, (x1 - 1, z_b), (1, 0)); // extremo este
    } else {
        let span = (x1 - x0 - 2).max(1);
        let x_a = x0 + 1 + hash_rng.gen_range(0..span);
        let x_b = x0 + 1 + hash_rng.gen_range(0..span);
        carve_explicit_entrance(grid, ceiling, (x_a, z0), (0, -1)); // extremo norte
        carve_explicit_entrance(grid, ceiling, (x_b, z1 - 1), (0, 1)); // extremo sur
    }
}

/// Highest odd-index maze node. Leaves index 0 and CHUNK_CELLS-1 as solid border.
const NODE_MAX: i32 = CHUNK_CELLS as i32 - 3; // 17 for CHUNK_CELLS=20

/// Una entrada del stack del backtracker de Fase 1:
/// `(posición del nodo, dirección de entrada como paso de 2 celdas, run_len)`.
/// La dirección del nodo raíz es `(0, 0)` — no se entró a él desde ningún sitio.
/// `run_len` cuenta los pasos rectos consecutivos acumulados hasta ese nodo.
type StackNode = ((i32, i32), (i32, i32), u32);

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
    //   `branch_persistence` (0.82 por defecto) → push destination
    //       (DFS bias → long corridors).
    //   resto → drop a random stack entry (ramification / shorter branches).
    //
    // Cada entrada del stack lleva, además de la posición, la DIRECCIÓN de
    // entrada al nodo (paso de 2 celdas; `(0, 0)` en el nodo raíz, que no tiene)
    // y `run_len`, la cuenta de pasos rectos consecutivos hasta él. Ambos
    // alimentan el sesgo de rectitud de `rules.straight_bias`.
    let mut wide_marks: Vec<(i32, i32)> = Vec::new();
    let mut stack: Vec<StackNode> = vec![((1, 1), (0, 0), 0)];
    grid.set(1, 1, corr(rules.ceiling_corridor));

    while let Some(&((cx, cz), dir, run_len)) = stack.last() {
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

        // Bifurcación deliberada. Con `straight_bias == 0.0` se ejecuta EXACTA-
        // MENTE la línea histórica, mismo draw y mismo orden: es lo que
        // garantiza que todo perfil que no active el sesgo siga generando un
        // grid byte-idéntico. Ningún draw extra se consume en esa rama.
        let (nx, nz) = if rules.straight_bias > 0.0 {
            pick_biased_toward_straight(&unvisited, (cx, cz), dir, rules.straight_bias, &mut rng)
        } else {
            unvisited[rng.gen_range(0..unvisited.len())]
        };
        let (mx, mz) = ((cx + nx) / 2, (cz + nz) / 2);

        grid.set(mx as usize, mz as usize, corr(rules.ceiling_corridor));
        grid.set(nx as usize, nz as usize, corr(rules.ceiling_corridor));

        if rng.gen::<f32>() < rules.wide_chance {
            wide_marks.push((mx, mz));
            wide_marks.push((nx, nz));
        }

        if rng.gen::<f32>() < rules.branch_persistence {
            let step = (nx - cx, nz - cz);
            let next_run = if step == dir { run_len + 1 } else { 1 };
            stack.push(((nx, nz), step, next_run));
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
    // Stamps `num_open_zones` rectangles, cada una de un `RoomType` sorteado
    // por hash contra `rules.room_type_weights` (default `(1.0, 0.0, 0.0)` =
    // SIEMPRE Open, sin draw extra del RNG compartido — mismo principio que
    // `straight_bias`: todo perfil que no active los otros dos pesos genera
    // grid byte-idéntico al de antes de RoomType). Zones track zone_id
    // (1-based) so reconnection and later phases can identify which cells
    // belong to which zone.
    //
    // Anti-solape: NO implementado a propósito. Con `num_open_zones > 1` y
    // tipos SealedRoom/CorridorSpine activos, una zona posterior puede
    // solaparse con el perímetro o interior de una anterior sin protección —
    // el origen de cada zona es un `gen_range` ciego (líneas siguientes),
    // ajeno a las demás. Layer 0 (`num_open_zones == 1` en los 4 perfiles de
    // producción) está a salvo de esto por construcción: con una sola zona
    // no hay con qué solaparse. Ver DECISIONS.md (RoomType) para el resto.
    let mut zones: Vec<(i32, i32, i32, i32)> = Vec::new(); // (x0, z0, x1, z1) exclusive
    for zone_idx in 0..rules.num_open_zones {
        let room_type = if rules.room_type_weights == default_room_type_weights() {
            RoomType::Open // caso por defecto: sin draw extra, byte-idéntico
        } else {
            pick_room_type(rules.room_type_weights, &mut rng)
        };

        // `open_zone_size_x`/`_z` en `None` (todo perfil que no las active) ⇒
        // sz_x == sz_z == open_zone_size, exactamente el escalar de antes.
        let sz_x = rules.open_zone_size_x.unwrap_or(rules.open_zone_size) as i32;
        let sz_z = rules.open_zone_size_z.unwrap_or(rules.open_zone_size) as i32;
        // CorridorSpine: ancho fijo caminable (2 celdas = 5 m; footprint total
        // del eje angosto = `CORRIDOR_SPINE_TOTAL_WIDTH`, que YA incluye las 2
        // paredes largas — ver su doc-comment). Con footprint total 4 en el
        // eje angosto, `min(eff_sz_x, eff_sz_z) == 4 < 6` sigue excluyendo la
        // retícula de pilares por construcción, mismo umbral que ya existía:
        // "Sin pilares" sale gratis, sin caso especial. Largo = el eje mayor
        // configurado. Orientación: se deriva de cuál configuración (sz_x vs
        // sz_z) es mayor — no se sortea aparte, así que un perfil con
        // sz_x == sz_z (el caso típico, ambos heredados del mismo
        // `open_zone_size` escalar) siempre da spine horizontal, un desempate
        // determinista documentado, no un bug.
        let (mut eff_sz_x, mut eff_sz_z) = match room_type {
            RoomType::Open | RoomType::SealedRoom => (sz_x, sz_z),
            RoomType::CorridorSpine => {
                if sz_x >= sz_z {
                    (sz_x, CORRIDOR_SPINE_TOTAL_WIDTH)
                } else {
                    (CORRIDOR_SPINE_TOTAL_WIDTH, sz_z)
                }
            }
        };
        // Alineación a la retícula de tiles de 5 m (SOLO tipos con perímetro
        // protegido). Ver `tile_aligned_size`/`align_origin_to_tile`.
        if room_type.has_sealed_perimeter() {
            eff_sz_x = tile_aligned_size(eff_sz_x);
            eff_sz_z = tile_aligned_size(eff_sz_z);
        }

        // Mismos dos `gen_range` (x0, z0) que el caso por defecto, mismo
        // orden — con RoomType::Open (eff_sz == sz, sin alineación) esto es
        // byte-idéntico. La alineación del ORIGEN se aplica DESPUÉS del sorteo,
        // sin consumir draws extra, por la misma razón.
        let max_origin_x = (CHUNK_CELLS as i32 - 1 - eff_sz_x).max(1);
        let max_origin_z = (CHUNK_CELLS as i32 - 1 - eff_sz_z).max(1);
        let mut x0 = rng.gen_range(1..=max_origin_x);
        let mut z0 = rng.gen_range(1..=max_origin_z);
        if room_type.has_sealed_perimeter() {
            x0 = align_origin_to_tile(x0, max_origin_x);
            z0 = align_origin_to_tile(z0, max_origin_z);
        }
        let x1 = (x0 + eff_sz_x).min(CHUNK_CELLS as i32 - 1);
        let z1 = (z0 + eff_sz_z).min(CHUNK_CELLS as i32 - 1);
        let zid = zone_idx as u16 + 1;

        match room_type {
            RoomType::Open => stamp_open_zone(&mut grid, x0, z0, x1, z1, zid, rules.ceiling_open),
            RoomType::SealedRoom => {
                stamp_sealed_room(&mut grid, x0, z0, x1, z1, zid, rules.ceiling_open);
                let seed = zone_entrance_seed(world_seed, chunk_coord, layer_index, zone_idx);
                carve_sealed_room_entrances(
                    &mut grid,
                    rules.ceiling_corridor,
                    seed,
                    x0,
                    z0,
                    x1,
                    z1,
                );
            }
            RoomType::CorridorSpine => {
                let horizontal = eff_sz_x >= eff_sz_z;
                stamp_corridor_spine(
                    &mut grid,
                    x0,
                    z0,
                    x1,
                    z1,
                    zid,
                    rules.ceiling_open,
                    horizontal,
                );
                let seed = zone_entrance_seed(world_seed, chunk_coord, layer_index, zone_idx);
                carve_corridor_spine_entrances(
                    &mut grid,
                    rules.ceiling_corridor,
                    seed,
                    x0,
                    z0,
                    x1,
                    z1,
                    horizontal,
                );
            }
        }

        // Umbral de pilares: `min(eff_sz_x, eff_sz_z) >= 6`, no área — un
        // pasillo largo y angosto (p.ej. 3×18, o cualquier CorridorSpine,
        // ancho fijo 2) nunca debe sembrar pilares aunque su área sea grande;
        // lo que importa es que quepa al menos un slot de retícula (paso 3,
        // offset 2) en AMBOS ejes. Mismo mecanismo para Open y SealedRoom —
        // la retícula solo toca `[x0+2, x1-2] × [z0+2, z1-2]`, siempre
        // interior al perímetro (protegido o no).
        if eff_sz_x.min(eff_sz_z) >= 6 {
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
        if fx < CHUNK_CELLS && fz < CHUNK_CELLS {
            let cell = grid.get(fx, fz);
            // Explícito contra SealedWall, no solo `!is_walkable()`: mismo
            // criterio que el fix de c42dd48 en `connect_zone_to_maze` — una
            // posición forzada nunca debe poder perforar un perímetro
            // protegido. Ruta hoy inalcanzable en producción (los dos
            // llamadores reales pasan `forced_walkable: &[]`, ver
            // collision.rs/tile_walls.rs), pero se corrige igual: era la
            // única de las 4 rutas de carvado que usaba `!is_walkable()` en
            // vez de una guarda explícita por tipo — SealedWall ya es
            // no-walkable por el cambio de cell.rs, así que sin este fix
            // habría caído aquí como cualquier otro muro.
            if !cell.is_walkable() && cell.kind() != CellType::SealedWall {
                grid.set(fx, fz, corr(rules.ceiling_corridor));
            }
        }
    }

    // ── Connectivity repair (post-Fase 6, pre-Fase 7) ────────────────────────
    //
    // Corrección al orden del pseudocódigo §4: los Void de Fase 6 pueden aislar
    // componentes que Fase 5 había reconectado. Se repara aquí, ANTES de Fase 7,
    // para que escaleras y pozos caigan siempre sobre un grid ya conexo.
    // Determinista: orden de escaneo del grid, sin RNG.
    repair_connectivity(&mut grid, rules.ceiling_corridor, &[]);

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
///
/// `protected`: celdas que el sellado de bolsillos NUNCA puede tocar, aunque
/// queden en un componente irreparable. Hoy solo lo usa `stitch_edges`
/// (`stitching.rs`), con el túnel completo que cada `carve_aperture` acaba de
/// carvar — bug real encontrado con perfiles mixtos de RoomType en
/// producción, pero NO específico de SealedWall: reproduce igual con un
/// perfil sin RoomType (`LAYER_PROFILES[2]`, seed 51, chunk (2,1)). Causa:
/// una apertura de borde puede acabar en un componente más pequeño que el
/// grueso del maze del propio chunk (topología de Fase 1, nada que ver con
/// contenido estampado) — el sellado de bolsillos, que no distingue "ruido"
/// de "la única conexión con el chunk vecino", la sella de vuelta a Wall y
/// ese borde queda intransitable para SIEMPRE en ambos chunks. `protected`
/// hace explícito que la apertura de costura pesa más que la limpieza de
/// bolsillos — Fase 4 (llamada sin apertures que proteger) sigue pasando
/// `&[]`.
pub(super) fn repair_connectivity(grid: &mut LayerGrid, ceiling: u8, protected: &[(usize, usize)]) {
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
                // `kind() == Wall` ya excluye SealedWall por construcción (son
                // variantes distintas del mismo enum), pero el
                // `&& kind() != SealedWall` se deja explícito a propósito: si
                // este check migrara algún día a algo más amplio (`is_solid()`,
                // `!is_walkable()` — el mismo error de criterio que corrigió
                // c42dd48 en `connect_zone_to_maze`), SealedWall seguiría
                // bloqueado sin depender de que quien lo cambie recuerde por qué.
                let passable = cell.is_walkable()
                    || (cell.kind() == CellType::Wall
                        && cell.kind() != CellType::SealedWall
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
                    if grid.get(x, z).is_walkable()
                        && !visited[z * CHUNK_CELLS + x]
                        && !protected.contains(&(x, z))
                    {
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
            let k = grid.get(x, z).kind();
            // Misma guarda explícita que en `passable` arriba — ver comentario.
            if k == CellType::Wall && k != CellType::SealedWall {
                grid.set(x, z, Cell::new(CellType::Corridor, ceiling, 0));
            }
            i = parent[i];
        }
    }
}

/// Elige el siguiente nodo sesgando hacia CONTINUAR RECTO.
///
/// Si el nodo que prolonga `dir` sigue sin visitar, se toma con probabilidad
/// `straight_bias`; en cualquier otro caso (raíz sin dirección, recto ya
/// visitado, o sorteo fallido) cae al sorteo uniforme histórico entre los
/// vecinos disponibles. El backtracker sortea dirección uniformemente, así que
/// sin este sesgo "corredor largo" acaba significando "serpenteante largo": el
/// stack no recordaba por dónde se había entrado.
///
/// SOLO se llama con `straight_bias > 0.0`. Consume draws adicionales del RNG,
/// y por eso vive detrás de esa condición — un perfil con el sesgo apagado no
/// debe ver alterada su secuencia de sorteos.
fn pick_biased_toward_straight(
    unvisited: &[(i32, i32)],
    (cx, cz): (i32, i32),
    dir: (i32, i32),
    straight_bias: f32,
    rng: &mut StdRng,
) -> (i32, i32) {
    if dir != (0, 0) {
        let straight = (cx + dir.0, cz + dir.1);
        if unvisited.contains(&straight) && rng.gen::<f32>() < straight_bias {
            return straight;
        }
    }
    unvisited[rng.gen_range(0..unvisited.len())]
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
        // Solo muro interior. El contenido estampado/protegido (Void/Pillar/
        // SealedWall) se respeta y el camino sigue avanzando sin escribir; lo
        // que quede suelto lo reconecta `repair_connectivity` (post-Fase 6),
        // que es quien tiene permiso para buscar una ruta alternativa
        // alrededor del estampado. `&& kind() != SealedWall` explícito, misma
        // razón que en `repair_connectivity`: aunque `kind() == Wall` ya lo
        // excluye, la redundancia es defensa ante un futuro cambio de criterio.
        let k = grid.get(cx as usize, cz as usize).kind();
        if LayerGrid::is_interior(cx, cz) && k == CellType::Wall && k != CellType::SealedWall {
            grid.set(
                cx as usize,
                cz as usize,
                Cell::new(CellType::Corridor, ceiling, 0),
            );
        }
    }
}

/// Carva una entrada explícita: fija `start` a Corridor (sea ya transitable,
/// o el punto exacto que el propio estampador de la zona decide perforar —
/// p. ej. una celda de su perímetro `SealedWall`) y avanza en línea recta
/// (`dir`, paso de 1 celda) hacia fuera hasta tocar una celda ya transitable
/// o contenido estampado/protegido de OTRA zona (SealedWall/Pillar/Void) —
/// se detiene ante ambos. Mismo criterio de parada que
/// `stitching::carve_aperture`, pero a diferencia de aquella, `start` no es
/// una celda de borde de chunk: es un punto de perímetro de zona que YA
/// decidió quien llama (hash determinista, Fase 4) — esta función no elige
/// nada, solo carva.
///
/// Duplicada a propósito en vez de subir `carve_aperture` a `pub(super)`:
/// son ~20 líneas, y los dos módulos existen por razones de vida distintas
/// (costura entre chunks vs. entradas dentro de un chunk).
///
/// Precondición del llamador: `start` debe ser una celda interior — el
/// perímetro de zona de Fase 4 siempre lo es (`x0/z0 >= 1`,
/// `x1/z1 <= CHUNK_CELLS - 1`); esta función no lo valida.
pub(super) fn carve_explicit_entrance(
    grid: &mut LayerGrid,
    ceiling: u8,
    start: (i32, i32),
    dir: (i32, i32),
) {
    let corr = Cell::new(CellType::Corridor, ceiling, 0);
    grid.set(start.0 as usize, start.1 as usize, corr);

    let (mut x, mut z) = start;
    let last = CHUNK_CELLS as i32 - 1;
    loop {
        x += dir.0;
        z += dir.1;
        // Nunca escribir el borde reservado (fila/columna 0 y CHUNK_CELLS-1):
        // mismo criterio que `carve_aperture` y `connect_zone_to_maze` — el
        // costurado de Bloque E depende de que esas celdas queden intactas
        // hasta que él decida abrirlas.
        if x <= 0 || z <= 0 || x >= last || z >= last {
            return; // el pase de reparación conecta el túnel al laberinto
        }
        let cell = grid.get(x as usize, z as usize);
        if cell.is_walkable() {
            return; // alcanzó el laberinto (o el interior de otra zona)
        }
        if cell.kind() != CellType::Wall {
            return; // contenido estampado/protegido de otra zona: para, no lo toca
        }
        grid.set(x as usize, z as usize, corr);
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
