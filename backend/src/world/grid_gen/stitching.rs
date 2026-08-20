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

use super::generator::{mix, repair_connectivity};
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

    // ADR-081 enmienda 5: la habitacion construible se talla AQUI, en el generador compartido, y no
    // en cada consumidor. Render (`chunk_tile_walls`) y colision del robapieles
    // (`GridGenChunkCache`) salen los dos de esta funcion, asi que tallarla una vez es lo unico que
    // garantiza que vean la MISMA habitacion. Va detras del cosido a proposito — ver `carve_into_grid`.
    if layer_index >= 0 && layer_index <= u8::MAX as i32 {
        let build_plan = super::build_rooms::room_in_chunk(
            world_seed,
            chunk_coord.0,
            chunk_coord.1,
            layer_index as u8,
        );

        let mut carved: Vec<(usize, usize)> = Vec::new();

        if let Some(plan) = build_plan {
            carved.extend(super::build_rooms::carve_into_grid(
                &mut out.grid,
                &plan,
                rules.ceiling_open,
            ));
            // Segunda pasada de reparacion, obligatoria: el anillo de la habitacion puede haber
            // dejado un trozo del laberinto incomunicado, y sin esto el chunk sale con zonas
            // inalcanzables (es lo que rompio cinco tests de conectividad al primer intento). El
            // anillo es SealedWall, asi que esta pasada no puede perforarlo; `carved` protege el
            // interior y el tunel de la puerta de acabar sellados si el bolsillo fuera irreparable.
            repair_connectivity(&mut out.grid, rules.ceiling_corridor, &carved);
        }

        // ADR-083 enmienda 1 — la sala autorada. Va DESPUÉS de la construible y le cede el sitio si
        // se solapan: la construible es una regla de juego validada y la autorada es decorado.
        //
        // Aquí, y no en `generate_layer`, por la misma razón que la construible: el cosido de bordes
        // ya ha terminado, así que el laberinto contra el que la puerta tiene que engancharse está
        // en su forma definitiva.
        //
        // La reparación de abajo protege las celdas de LAS DOS salas acumuladas en `carved`, no solo
        // las suyas: sellar un bolsillo irreparable no puede llevarse por delante el interior o el
        // túnel de la habitación construible, que ya estaba tallada y protegida en su propia pasada.
        if let Some(manifest) = super::room_manifest::active_manifest() {
            let rooms = super::authored_rooms::plan_authored_rooms(
                manifest,
                world_seed,
                chunk_coord.0,
                chunk_coord.1,
                layer_index as u8,
                build_plan.as_ref(),
            );
            if !rooms.is_empty() {
                // ADR-083 enmienda 3: se tallan TODAS de una (en dos fases: carcasas y luego
                // puertas — el porqué, en `carve_authored_set_into_grid`) y se repara UNA vez al
                // final. Reparar entre sala y sala sería tender pasillos hacia un trozo de chunk
                // que la siguiente todavía va a rellenar de macizo, y pagar el BFS por cada sala.
                carved.extend(super::authored_rooms::carve_authored_set_into_grid(
                    &mut out.grid,
                    &rooms,
                    rules.ceiling_open,
                    rules.ceiling_corridor,
                ));
                repair_connectivity(&mut out.grid, rules.ceiling_corridor, &carved);
            }
        }
    }

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

    // Cada apertura devuelve las celdas que carvó (borde + túnel interior).
    // Se acumulan y se pasan a `repair_connectivity` como `protected`: el
    // sellado de bolsillos no puede tocarlas aunque queden en un componente
    // irreparable — ver el porqué en el doc-comment de `repair_connectivity`.
    let mut carved: Vec<(usize, usize)> = Vec::new();

    // East border: shared with (cx+1, cz). Canonical key = this chunk.
    let p = aperture_pos(world_seed, cx, cz, EdgeAxis::Vertical, layer_index);
    carved.extend(carve_aperture(grid, rules, (last, p), (-1i32, 0i32)));

    // West border: shared with (cx-1, cz). Canonical key = the western chunk.
    let p = aperture_pos(world_seed, cx - 1, cz, EdgeAxis::Vertical, layer_index);
    carved.extend(carve_aperture(grid, rules, (0, p), (1, 0)));

    // North border (z+1): shared with (cx, cz+1). Canonical key = this chunk.
    let p = aperture_pos(world_seed, cx, cz, EdgeAxis::Horizontal, layer_index);
    carved.extend(carve_aperture(grid, rules, (p, last), (0, -1)));

    // South border (z-1): shared with (cx, cz-1). Canonical key = the southern chunk.
    let p = aperture_pos(world_seed, cx, cz - 1, EdgeAxis::Horizontal, layer_index);
    carved.extend(carve_aperture(grid, rules, (p, 0), (0, 1)));

    // Reconnection rule (§5) applied to the seams: the freshly carved aperture
    // corridors may still be separate components (e.g. the inward carve stopped
    // against stamped content). One repair pass attaches them to the main maze.
    repair_connectivity(grid, rules.ceiling_corridor, &carved);
}

/// Deterministic aperture position along a canonical edge, in 1..CHUNK_CELLS-1
/// (never a corner, so apertures of perpendicular edges cannot collide).
fn aperture_pos(world_seed: u64, kx: i32, kz: i32, axis: EdgeAxis, layer_index: i32) -> usize {
    // Constante de dominio: separa el espacio de seeds de borde del de las celdas
    // (`derive_seed` arranca de `world_seed` sin esta máscara → espacios disjuntos).
    let mut s = world_seed ^ 0xED6E_C0A7_05EA_05ED;
    s = mix(s, kx as i64 as u64);
    s = mix(s, kz as i64 as u64);
    s = mix(s, axis as u64);
    s = mix(s, layer_index as i64 as u64);
    StdRng::seed_from_u64(s).gen_range(1..CHUNK_CELLS - 1)
}

/// Open the border cell at `start` and carve inward (direction `dir`) through
/// Wall cells until reaching an already-walkable cell. Stops without carving
/// if it meets Void/Pillar — "estampar gana". `SealedWall` is the ONE
/// exception (see below): breached at most once per aperture, then treated
/// like everything else. Devuelve TODAS las celdas que tocó (borde + túnel
/// interior), para que `stitch_edges` pueda protegerlas de
/// `repair_connectivity` — ver su doc-comment para el porqué.
///
/// Por qué SealedWall es distinto de Void/Pillar aquí: si el estampado de
/// `SealedRoom`/`CorridorSpine` (Fase 4) deja su perímetro justo en la línea
/// de costura, un `carve_aperture` que se detenga ahí abre el borde pero lo
/// deja pegado a un perímetro protegido justo detrás — sin este breach, el
/// único vecino interior de la apertura sería SealedWall, y la apertura
/// nacería ya aislada. Con el breach, al menos llega a la celda Open del
/// interior de la sala inmediatamente detrás.
fn carve_aperture(
    grid: &mut LayerGrid,
    rules: &LayerRules,
    start: (usize, usize),
    dir: (i32, i32),
) -> Vec<(usize, usize)> {
    let corr = Cell::new(CellType::Corridor, rules.ceiling_corridor, 0);
    grid.set(start.0, start.1, corr);
    let mut touched = vec![start];

    let (mut x, mut z) = (start.0 as i32, start.1 as i32);
    let last = (CHUNK_CELLS - 1) as i32;
    let mut sealed_breach_used = false;
    loop {
        x += dir.0;
        z += dir.1;
        // Nunca escribir celdas de borde distintas de la apertura propia: si el
        // carve cruza el chunk entero sin tocar nada transitable, taladraría el
        // borde opuesto creando una apertura unilateral que el vecino no conoce.
        if x <= 0 || z <= 0 || x >= last || z >= last {
            return touched; // el pase de reparación conecta el túnel al laberinto
        }
        let cell = grid.get(x as usize, z as usize);
        if cell.is_walkable() {
            return touched; // reached the maze
        }
        match cell.kind() {
            CellType::Wall => {}
            CellType::SealedWall if !sealed_breach_used => {
                sealed_breach_used = true;
            }
            _ => return touched, // Void/Pillar, o una 2ª SealedWall: estampado, para
        }
        grid.set(x as usize, z as usize, corr);
        touched.push((x as usize, z as usize));
    }
}
