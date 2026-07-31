//! Bloque D — tests de generación de `grid_gen`.
//!
//! Orden de criticidad:
//! 1. Conectividad (flood-fill): el motor genera mundos jugables.
//! 2. Determinismo: misma seed → grid byte-idéntico (blindaje multijugador).
//! 3. Escaleras/pozos con destino transitable garantizado (§5).
//! 4. Perfiles por capa cambian de verdad el output.
//! 5. Borde reservado intacto (precondición del costurado, bloque E).

use super::*;

const TEST_SEED: u64 = 0xBACC_0085;
const TEST_CHUNK: (i32, i32) = (3, -7);

fn gen(layer_index: i32, forced: &[(u8, u8)]) -> LayerOutput {
    let rules = &LAYER_PROFILES[layer_index as usize];
    generate_layer(rules, TEST_SEED, TEST_CHUNK, layer_index, forced)
}

/// Flood-fill ortogonal sobre celdas transitables desde la primera encontrada.
/// Devuelve (alcanzadas, total_transitables).
fn flood_fill_walkable(grid: &LayerGrid) -> (usize, usize) {
    let mut total = 0usize;
    let mut start = None;
    for z in 0..CHUNK_CELLS {
        for x in 0..CHUNK_CELLS {
            if grid.get(x, z).is_walkable() {
                total += 1;
                if start.is_none() {
                    start = Some((x, z));
                }
            }
        }
    }

    let Some(start) = start else { return (0, 0) };
    let mut visited = vec![false; CHUNK_CELLS * CHUNK_CELLS];
    let mut queue = vec![start];
    visited[start.1 * CHUNK_CELLS + start.0] = true;
    let mut reached = 0usize;

    while let Some((x, z)) = queue.pop() {
        reached += 1;
        for (dx, dz) in [(-1i32, 0i32), (1, 0), (0, -1), (0, 1)] {
            let (nx, nz) = (x as i32 + dx, z as i32 + dz);
            if !LayerGrid::in_bounds(nx, nz) {
                continue;
            }
            let (nx, nz) = (nx as usize, nz as usize);
            if !visited[nz * CHUNK_CELLS + nx] && grid.get(nx, nz).is_walkable() {
                visited[nz * CHUNK_CELLS + nx] = true;
                queue.push((nx, nz));
            }
        }
    }
    (reached, total)
}

// ── 1. Conectividad ───────────────────────────────────────────────────────────

#[test]
fn all_walkable_cells_are_connected() {
    // Múltiples seeds y capas: una sola regresión de reconexión debe saltar aquí.
    for seed in [TEST_SEED, 1, 42, 9_999_999] {
        for layer_index in 0..LAYER_PROFILES.len() as i32 {
            let rules = &LAYER_PROFILES[layer_index as usize];
            let out = generate_layer(rules, seed, TEST_CHUNK, layer_index, &[]);
            let (reached, total) = flood_fill_walkable(&out.grid);
            assert!(
                total > 0,
                "seed {seed} capa {layer_index}: grid sin celdas transitables"
            );
            assert_eq!(
                reached, total,
                "seed {seed} capa {layer_index} ({}): {} de {} celdas transitables alcanzables — hay zonas aisladas (§5 falló)",
                rules.name, reached, total
            );
        }
    }
}

/// Blindaje del sellado de bolsillos: el sellado debe eliminar bolsillos
/// (1–2 celdas), nunca la componente principal. El bug catastrófico (raíz del
/// BFS en el bolsillo → se sella el laberinto entero) dejaría un grid casi
/// vacío (~1% transitable), así que un suelo absoluto de fracción transitable
/// + conectividad total lo atrapa, sin depender de los números de perfil.
///
/// (La versión anterior comparaba contra conteos pre-sellado hardcodeados de
/// los perfiles §3 originales; la recalibración de Fase 2 los invalidó.)
#[test]
fn sealing_preserves_main_component() {
    let rules = &LAYER_PROFILES[3];
    // Incluye las 6 seeds que disparaban el caso irreparable con los perfiles
    // originales, más un rango amplio para cubrir los perfiles recalibrados.
    let seeds = (0u64..50).chain([39, 100, 188, 281, 394, 484, TEST_SEED]);
    for seed in seeds {
        let out = generate_layer(rules, seed, TEST_CHUNK, 3, &[]);
        let (reached, total) = flood_fill_walkable(&out.grid);
        assert_eq!(
            reached, total,
            "seed {seed} capa 3: aún hay componentes aisladas"
        );

        // Umbral: un sellado catastrófico deja el bolsillo (1–2 celdas) como
        // único superviviente. Un laberinto legítimamente pequeño (la rama del
        // 18% en Fase 1 puede descartar nodos y dejar regiones sin excavar)
        // baja hasta ~90 celdas. 30 celdas separa limpiamente ambos mundos.
        assert!(
            total >= 30,
            "seed {seed} capa 3: solo {total} celdas transitables — ¿se selló la componente principal?"
        );
    }
}

/// Barrido amplio: 500 seeds × 4 capas. Más lento que el resto de la suite;
/// se ejecuta explícitamente con `cargo test -- --ignored`.
#[test]
#[ignore = "barrido amplio; correr con --ignored"]
fn connectivity_seed_sweep() {
    let mut failures = Vec::new();
    for seed in 0u64..500 {
        for layer_index in 0..LAYER_PROFILES.len() as i32 {
            let rules = &LAYER_PROFILES[layer_index as usize];
            let out = generate_layer(rules, seed, TEST_CHUNK, layer_index, &[]);
            let (reached, total) = flood_fill_walkable(&out.grid);
            if reached != total {
                failures.push((seed, layer_index, reached, total));
            }
        }
    }
    assert!(
        failures.is_empty(),
        "{} combinaciones seed×capa con componentes aisladas: {:?}",
        failures.len(),
        &failures[..failures.len().min(20)]
    );
}

// ── 2. Determinismo ───────────────────────────────────────────────────────────

#[test]
fn same_seed_produces_identical_grid() {
    for layer_index in 0..LAYER_PROFILES.len() as i32 {
        let a = gen(layer_index, &[]);
        let b = gen(layer_index, &[]);
        assert_eq!(
            a.grid.cells(),
            b.grid.cells(),
            "capa {layer_index}: misma seed produjo grids distintos — determinismo roto"
        );
        assert_eq!(a.require_walkable_above, b.require_walkable_above);
        assert_eq!(a.require_walkable_below, b.require_walkable_below);
    }
}

#[test]
fn different_seeds_produce_different_grids() {
    let rules = &LAYER_PROFILES[1];
    let a = generate_layer(rules, TEST_SEED, TEST_CHUNK, 1, &[]);
    let b = generate_layer(rules, TEST_SEED + 1, TEST_CHUNK, 1, &[]);
    assert_ne!(
        a.grid.cells(),
        b.grid.cells(),
        "seeds distintas produjeron el mismo grid"
    );
}

#[test]
fn different_chunk_coords_produce_different_grids() {
    let rules = &LAYER_PROFILES[1];
    let a = generate_layer(rules, TEST_SEED, (0, 0), 1, &[]);
    let b = generate_layer(rules, TEST_SEED, (1, 0), 1, &[]);
    assert_ne!(
        a.grid.cells(),
        b.grid.cells(),
        "chunks distintos produjeron el mismo grid"
    );
}

// ── 3. Escaleras/pozos con destino garantizado ────────────────────────────────

#[test]
fn stairs_have_walkable_floor_in_layer_above() {
    // Capa 0 coloca escaleras → sus coordenadas deben ser transitables en capa 1
    // cuando se pasan como forced_walkable.
    let lower = gen(0, &[]);
    assert!(
        !lower.require_walkable_above.is_empty(),
        "capa 0 (num_stairs=2) no produjo escaleras"
    );
    let upper = gen(1, &lower.require_walkable_above);
    for &(x, z) in &lower.require_walkable_above {
        let cell = upper.grid.get(x as usize, z as usize);
        assert!(
            cell.is_walkable(),
            "Stair en capa 0 ({x},{z}) sube hacia {:?} en capa 1 — escalera contra pared",
            cell.kind()
        );
    }
}

#[test]
fn pits_have_walkable_floor_in_layer_below() {
    // Capa 1 coloca pozos → sus coordenadas deben ser transitables en capa 0.
    let upper = gen(1, &[]);
    assert!(
        !upper.require_walkable_below.is_empty(),
        "capa 1 (num_pits=2) no produjo pozos"
    );
    let lower = gen(0, &upper.require_walkable_below);
    for &(x, z) in &upper.require_walkable_below {
        let cell = lower.grid.get(x as usize, z as usize);
        assert!(
            cell.is_walkable(),
            "Pit en capa 1 ({x},{z}) baja hacia {:?} en capa 0 — pozo contra pared",
            cell.kind()
        );
    }
}

#[test]
fn stair_and_pit_counts_match_rules() {
    for layer_index in 0..LAYER_PROFILES.len() as i32 {
        let rules = &LAYER_PROFILES[layer_index as usize];
        let out = gen(layer_index, &[]);
        let stairs = out
            .grid
            .cells()
            .iter()
            .filter(|c| c.kind() == CellType::Stair)
            .count();
        let pits = out
            .grid
            .cells()
            .iter()
            .filter(|c| c.kind() == CellType::Pit)
            .count();
        assert_eq!(
            stairs, rules.num_stairs as usize,
            "capa {layer_index}: nº de escaleras no respeta el perfil"
        );
        assert_eq!(
            pits, rules.num_pits as usize,
            "capa {layer_index}: nº de pozos no respeta el perfil"
        );
    }
}

// ── 4. Perfiles respetados ────────────────────────────────────────────────────

#[test]
fn layer_profiles_change_the_output() {
    // El Vestíbulo (capa 0) debe ser significativamente más cerrado que El Caos
    // (capa 2). Comparamos fracción de celdas no-Wall sobre varias seeds para
    // que el test no dependa de una seed afortunada.
    let mut open_0 = 0usize;
    let mut open_2 = 0usize;
    for seed in [TEST_SEED, 7, 1234, 555_555] {
        let v = generate_layer(&LAYER_PROFILES[0], seed, TEST_CHUNK, 0, &[]);
        let c = generate_layer(&LAYER_PROFILES[2], seed, TEST_CHUNK, 2, &[]);
        open_0 += v
            .grid
            .cells()
            .iter()
            .filter(|c| c.kind() != CellType::Wall)
            .count();
        open_2 += c
            .grid
            .cells()
            .iter()
            .filter(|c| c.kind() != CellType::Wall)
            .count();
    }
    assert!(
        open_2 > open_0 + open_0 / 4,
        "El Caos ({open_2} celdas no-muro) debería ser claramente más abierto que El Vestíbulo ({open_0})"
    );
}

#[test]
fn ceiling_heights_respect_profile_and_layer_bound() {
    for layer_index in 0..LAYER_PROFILES.len() as i32 {
        let rules = &LAYER_PROFILES[layer_index as usize];
        let out = gen(layer_index, &[]);
        for (i, cell) in out.grid.cells().iter().enumerate() {
            assert!(
                cell.ceiling_height <= MAX_CEILING_UNITS,
                "capa {layer_index} celda {i}: ceiling_height {} > MAX_CEILING_UNITS — desbordamiento de capa",
                cell.ceiling_height
            );
            match cell.kind() {
                CellType::Corridor => assert_eq!(cell.ceiling_height, rules.ceiling_corridor),
                CellType::Open => assert_eq!(cell.ceiling_height, rules.ceiling_open),
                _ => {}
            }
        }
    }
}

// ── Bloque E — costurado de bordes ────────────────────────────────────────────

/// Coherencia de costura: el borde este de (0,0) y el borde oeste de (1,0)
/// deben tener aperturas en las MISMAS posiciones, calculadas sin comunicación.
#[test]
fn seam_apertures_match_between_neighbours() {
    for layer_index in 0..LAYER_PROFILES.len() as i32 {
        let rules = &LAYER_PROFILES[layer_index as usize];
        for seed in [TEST_SEED, 1, 42] {
            // Este↔Oeste
            let a = generate_chunk_layer(rules, seed, (0, 0), layer_index, &[]);
            let b = generate_chunk_layer(rules, seed, (1, 0), layer_index, &[]);
            let east_a: Vec<usize> = (0..CHUNK_CELLS)
                .filter(|&z| a.grid.get(CHUNK_CELLS - 1, z).is_walkable())
                .collect();
            let west_b: Vec<usize> = (0..CHUNK_CELLS)
                .filter(|&z| b.grid.get(0, z).is_walkable())
                .collect();
            assert!(
                !east_a.is_empty(),
                "seed {seed} capa {layer_index}: borde este de (0,0) sin apertura"
            );
            assert_eq!(
                east_a, west_b,
                "seed {seed} capa {layer_index}: aperturas E/O no coinciden"
            );

            // Norte↔Sur
            let c = generate_chunk_layer(rules, seed, (0, 1), layer_index, &[]);
            let north_a: Vec<usize> = (0..CHUNK_CELLS)
                .filter(|&x| a.grid.get(x, CHUNK_CELLS - 1).is_walkable())
                .collect();
            let south_c: Vec<usize> = (0..CHUNK_CELLS)
                .filter(|&x| c.grid.get(x, 0).is_walkable())
                .collect();
            assert!(
                !north_a.is_empty(),
                "seed {seed} capa {layer_index}: borde norte de (0,0) sin apertura"
            );
            assert_eq!(
                north_a, south_c,
                "seed {seed} capa {layer_index}: aperturas N/S no coinciden"
            );
        }
    }
}

/// Conectividad inter-chunk: rejilla 3×3 de chunks fusionada en un grid de
/// 60×60 — flood-fill global debe alcanzar TODO lo transitable cruzando
/// fronteras. Este es el test que prueba el mundo infinito.
#[test]
fn merged_3x3_chunks_are_globally_connected() {
    const N: usize = 3;
    let side = N * CHUNK_CELLS;

    for layer_index in 0..LAYER_PROFILES.len() as i32 {
        let rules = &LAYER_PROFILES[layer_index as usize];
        for seed in [TEST_SEED, 1, 42] {
            // Fusionar 3×3 chunks en un grid grande.
            let mut merged = vec![Cell::SOLID_WALL; side * side];
            for ccz in 0..N {
                for ccx in 0..N {
                    let out = generate_chunk_layer(
                        rules,
                        seed,
                        (ccx as i32, ccz as i32),
                        layer_index,
                        &[],
                    );
                    for z in 0..CHUNK_CELLS {
                        for x in 0..CHUNK_CELLS {
                            merged[(ccz * CHUNK_CELLS + z) * side + ccx * CHUNK_CELLS + x] =
                                out.grid.get(x, z);
                        }
                    }
                }
            }

            // Flood-fill global sobre el grid fusionado.
            let mut total = 0usize;
            let mut start = None;
            for (i, cell) in merged.iter().enumerate() {
                if cell.is_walkable() {
                    total += 1;
                    if start.is_none() {
                        start = Some(i);
                    }
                }
            }
            let start = start.expect("grid fusionado sin celdas transitables");
            let mut visited = vec![false; side * side];
            visited[start] = true;
            let mut queue = vec![start];
            let mut reached = 0usize;
            while let Some(i) = queue.pop() {
                reached += 1;
                let (x, z) = (i % side, i / side);
                for (dx, dz) in [(-1i32, 0i32), (1, 0), (0, -1), (0, 1)] {
                    let (nx, nz) = (x as i32 + dx, z as i32 + dz);
                    if nx < 0 || nz < 0 || nx as usize >= side || nz as usize >= side {
                        continue;
                    }
                    let ni = nz as usize * side + nx as usize;
                    if !visited[ni] && merged[ni].is_walkable() {
                        visited[ni] = true;
                        queue.push(ni);
                    }
                }
            }
            assert_eq!(
                reached, total,
                "seed {seed} capa {layer_index}: {reached} de {total} celdas alcanzables en el mundo 3×3 — frontera de chunk bloqueada"
            );
        }
    }
}

/// Determinismo del chunk costurado completo.
#[test]
fn stitched_chunk_is_deterministic() {
    for layer_index in 0..LAYER_PROFILES.len() as i32 {
        let rules = &LAYER_PROFILES[layer_index as usize];
        let a = generate_chunk_layer(rules, TEST_SEED, TEST_CHUNK, layer_index, &[]);
        let b = generate_chunk_layer(rules, TEST_SEED, TEST_CHUNK, layer_index, &[]);
        assert_eq!(
            a.grid.cells(),
            b.grid.cells(),
            "capa {layer_index}: chunk costurado no determinista"
        );
    }
}

// ── 5. Borde reservado ────────────────────────────────────────────────────────

#[test]
fn border_cells_remain_solid_wall() {
    for seed in [TEST_SEED, 1, 42, 9_999_999] {
        for layer_index in 0..LAYER_PROFILES.len() as i32 {
            let rules = &LAYER_PROFILES[layer_index as usize];
            let out = generate_layer(rules, seed, TEST_CHUNK, layer_index, &[]);
            let last = CHUNK_CELLS - 1;
            for i in 0..CHUNK_CELLS {
                for (x, z) in [(i, 0), (i, last), (0, i), (last, i)] {
                    assert_eq!(
                        out.grid.get(x, z),
                        Cell::SOLID_WALL,
                        "seed {seed} capa {layer_index}: celda de borde ({x},{z}) modificada — rompe la precondición del costurado"
                    );
                }
            }
        }
    }
}

// ── 6. Densidades vecinas divergentes (ADR-033) ───────────────────────────────
//
// Escenario que INTRODUCE ADR-033: chunks VECINOS con perfiles de densidad
// DISTINTOS dentro de la MISMA capa. Todo lo probado hasta aquí — incluido
// `merged_3x3_chunks_are_globally_connected` — usa un perfil UNIFORME para el
// bloque entero, así que la costura nunca se ejercitó con densidades mixtas.
//
// Por qué estos tests nacen en VERDE (deliberado, no es un descuido): la
// posición de apertura (`aperture_pos`, stitching.rs) deriva de
// (world_seed, borde canónico, eje, capa) y NO de `rules` — dos vecinos con
// perfiles distintos ya acuerdan la apertura por construcción. Estos tests no
// demuestran un bug actual: fijan por escrito una invariante hoy ACCIDENTAL,
// para que el cableado de zone_kind (ADR-033, Paso 2: `zone_kind` → LayerRules
// por chunk en las rutas de render y del robapieles) no pueda romperla en
// silencio. Son el guardarraíl del Paso 2, no su diagnóstico.

/// Índice de perfil por chunk en tablero de ajedrez: el contraste MÁXIMO
/// expresable con los perfiles ya calibrados — 0 ("El Vestíbulo", el más
/// cerrado: 1 zona de 5 celdas, sin pilares) contra 2 ("El Caos", el más
/// abierto: 4 zonas de 7 celdas, pilares al 0.6). No se inventan números de
/// perfil: cablear zone_kind producirá contrastes de este orden o menores.
fn checkerboard_profile(ccx: usize, ccz: usize) -> usize {
    if (ccx + ccz).is_multiple_of(2) {
        0
    } else {
        2
    }
}

/// Genera un bloque `n`×`n` de chunks COSTURADOS, eligiendo el perfil de CADA
/// chunk con `profile_at` (índice en `LAYER_PROFILES`). Todos comparten seed y
/// capa: lo único que varía entre vecinos es la densidad. Índice del resultado:
/// `ccz * n + ccx`.
fn block_grids(
    n: usize,
    profile_at: impl Fn(usize, usize) -> usize,
    seed: u64,
    layer_index: i32,
) -> Vec<LayerGrid> {
    let mut grids = Vec::with_capacity(n * n);
    for ccz in 0..n {
        for ccx in 0..n {
            let rules = &LAYER_PROFILES[profile_at(ccx, ccz)];
            let out = generate_chunk_layer(rules, seed, (ccx as i32, ccz as i32), layer_index, &[]);
            grids.push(out.grid);
        }
    }
    grids
}

/// Fusiona el bloque en un grid plano de lado `n * CHUNK_CELLS`.
fn merge_grids(grids: &[LayerGrid], n: usize) -> Vec<Cell> {
    let side = n * CHUNK_CELLS;
    let mut merged = vec![Cell::SOLID_WALL; side * side];
    for ccz in 0..n {
        for ccx in 0..n {
            let grid = &grids[ccz * n + ccx];
            for z in 0..CHUNK_CELLS {
                for x in 0..CHUNK_CELLS {
                    merged[(ccz * CHUNK_CELLS + z) * side + ccx * CHUNK_CELLS + x] = grid.get(x, z);
                }
            }
        }
    }
    merged
}

/// Flood-fill ortogonal sobre un grid fusionado cuadrado. (alcanzadas, total).
fn flood_fill_merged(merged: &[Cell], side: usize) -> (usize, usize) {
    let mut total = 0usize;
    let mut start = None;
    for (i, cell) in merged.iter().enumerate() {
        if cell.is_walkable() {
            total += 1;
            if start.is_none() {
                start = Some(i);
            }
        }
    }
    let Some(start) = start else { return (0, 0) };

    let mut visited = vec![false; merged.len()];
    visited[start] = true;
    let mut queue = vec![start];
    let mut reached = 0usize;
    while let Some(i) = queue.pop() {
        reached += 1;
        let (x, z) = (i % side, i / side);
        for (dx, dz) in [(-1i32, 0i32), (1, 0), (0, -1), (0, 1)] {
            let (nx, nz) = (x as i32 + dx, z as i32 + dz);
            if nx < 0 || nz < 0 || nx as usize >= side || nz as usize >= side {
                continue;
            }
            let ni = nz as usize * side + nx as usize;
            if !visited[ni] && merged[ni].is_walkable() {
                visited[ni] = true;
                queue.push(ni);
            }
        }
    }
    (reached, total)
}

/// Seeds de la suite normal: las 4 de `layer_profiles_change_the_output` (para
/// cruzar el mismo terreno que el test de contraste de perfiles) + las 3 de
/// `all_walkable_cells_are_connected`.
const DENSITY_MIX_SEEDS: [u64; 7] = [TEST_SEED, 7, 1234, 555_555, 1, 42, 9_999_999];

/// Bloque 3×3 con densidades alternas: el flood-fill global debe alcanzar TODO
/// lo transitable cruzando fronteras entre chunks de densidad MUY distinta.
/// Requisito duro de la consecuencia #4 de ADR-033.
#[test]
fn neighbouring_density_profiles_stay_globally_connected() {
    const N: usize = 3;
    let side = N * CHUNK_CELLS;

    for seed in DENSITY_MIX_SEEDS {
        let grids = block_grids(N, checkerboard_profile, seed, 0);
        let merged = merge_grids(&grids, N);
        let (reached, total) = flood_fill_merged(&merged, side);
        assert!(
            total > 0,
            "seed {seed}: bloque 3×3 de densidades mixtas sin celdas transitables"
        );
        assert_eq!(
            reached, total,
            "seed {seed}: {reached} de {total} celdas alcanzables en el bloque 3×3 de densidades MIXTAS — la costura no sobrevive a perfiles distintos entre vecinos"
        );
    }
}

/// Alineación de apertura por borde compartido: con perfiles distintos a cada
/// lado, cada frontera sigue teniendo al menos UNA posición transitable en
/// AMBOS chunks — la travesía real que la conectividad global necesita.
///
/// Se asierta la intersección no vacía, no la igualdad de los conjuntos de
/// borde (lo que sí hace el test de perfil uniforme): con densidades divergentes
/// el pase de reparación de cada chunk puede dejar transitables de más en su
/// propio borde. La intersección no vacía es la propiedad SUFICIENTE y es la que
/// depende de que `aperture_pos` ignore `rules`.
#[test]
fn shared_borders_keep_a_crossable_aperture_across_density_profiles() {
    const N: usize = 3;
    let last = CHUNK_CELLS - 1;

    for seed in DENSITY_MIX_SEEDS {
        let grids = block_grids(N, checkerboard_profile, seed, 0);
        for ccz in 0..N {
            for ccx in 0..N {
                let a = &grids[ccz * N + ccx];
                // Borde este: (ccx, ccz) | (ccx+1, ccz).
                if ccx + 1 < N {
                    let b = &grids[ccz * N + ccx + 1];
                    let crossable = (0..CHUNK_CELLS)
                        .any(|z| a.get(last, z).is_walkable() && b.get(0, z).is_walkable());
                    assert!(
                        crossable,
                        "seed {seed}: borde E entre ({ccx},{ccz}) [perfil {}] y ({},{ccz}) [perfil {}] sin travesía transitable",
                        checkerboard_profile(ccx, ccz),
                        ccx + 1,
                        checkerboard_profile(ccx + 1, ccz)
                    );
                }
                // Borde sur (+Z): (ccx, ccz) | (ccx, ccz+1).
                if ccz + 1 < N {
                    let b = &grids[(ccz + 1) * N + ccx];
                    let crossable = (0..CHUNK_CELLS)
                        .any(|x| a.get(x, last).is_walkable() && b.get(x, 0).is_walkable());
                    assert!(
                        crossable,
                        "seed {seed}: borde S entre ({ccx},{ccz}) [perfil {}] y ({ccx},{}) [perfil {}] sin travesía transitable",
                        checkerboard_profile(ccx, ccz),
                        ccz + 1,
                        checkerboard_profile(ccx, ccz + 1)
                    );
                }
            }
        }
    }
}

/// MEDICIÓN (no aserción dura, deliberado): paneles de pared UNILATERALES en
/// bordes de chunk con densidades divergentes.
///
/// `tile_walls` trata al vecino fuera de chunk como ABIERTO (tile_walls.rs), así
/// que si la celda de borde de A es transitable y la de B no, A no dibuja muro y
/// B sí: un panel visible desde un lado solo. Eso YA ocurre con perfiles
/// uniformes; ADR-033 solo lo hace MÁS frecuente (salón abierto pegado a maze
/// denso). Convertirlo en aserción dura exigiría cambiar el contrato de render,
/// que ADR-033 prohíbe explícitamente — por eso aquí solo se acota con un techo
/// HOLGADO que detecte una explosión (p. ej. un cableado futuro que rompa la
/// simetría de borde por completo), no la asimetría ordinaria.
#[test]
fn border_panel_asymmetry_stays_within_a_loose_ceiling() {
    const N: usize = 3;
    // MEDIDO al escribir este test (2026-07-29): 0 de 840 aristas — asimetría
    // CERO incluso con el contraste máximo de perfiles. Razón: `generate_layer`
    // reserva TODO el borde como Wall sólido (`border_cells_remain_solid_wall`) y
    // lo único que lo abre es la apertura canónica compartida, que ambos vecinos
    // derivan de la misma `edge_seed` con independencia de `rules` — así que las
    // celdas de borde coinciden por construcción y los paneles salen simétricos.
    // El techo se deja HOLGADO (10 %, no 0 %) a propósito: asertar el 0 exacto
    // convertiría esto en una aserción dura del contrato de render, que ADR-033
    // prohíbe. Este techo solo detecta una EXPLOSIÓN de asimetría (un cableado
    // futuro que abra celdas de borde por densidad), no la asimetría ordinaria.
    const MAX_ASYMMETRIC_FRACTION: f64 = 0.10;

    let mut asymmetric = 0usize;
    let mut compared = 0usize;

    for seed in DENSITY_MIX_SEEDS {
        let grids = block_grids(N, checkerboard_profile, seed, 0);
        let walls: Vec<_> = grids.iter().map(tile_walls_from_grid).collect();
        let last_tile = TILES_PER_SIDE - 1;

        for ccz in 0..N {
            for ccx in 0..N {
                let a = &walls[ccz * N + ccx];
                if ccx + 1 < N {
                    let b = &walls[ccz * N + ccx + 1];
                    for tz in 0..TILES_PER_SIDE {
                        compared += 1;
                        if (a[last_tile][tz] & WALL_E != 0) != (b[0][tz] & WALL_W != 0) {
                            asymmetric += 1;
                        }
                    }
                }
                if ccz + 1 < N {
                    let b = &walls[(ccz + 1) * N + ccx];
                    for tx in 0..TILES_PER_SIDE {
                        compared += 1;
                        if (a[tx][last_tile] & WALL_S != 0) != (b[tx][0] & WALL_N != 0) {
                            asymmetric += 1;
                        }
                    }
                }
            }
        }
    }

    assert!(compared > 0, "no se comparó ningún borde");
    let fraction = asymmetric as f64 / compared as f64;
    assert!(
        fraction <= MAX_ASYMMETRIC_FRACTION,
        "{asymmetric} de {compared} aristas de borde compartido son paneles unilaterales ({:.1} %) — por encima del techo holgado del {:.0} %; la simetría de borde se degradó, revisar antes de seguir",
        fraction * 100.0,
        MAX_ASYMMETRIC_FRACTION * 100.0
    );
}

/// Barrido amplio de densidades mixtas. Más lento que la suite normal; se
/// ejecuta explícitamente con `cargo test -- --ignored` (mismo patrón que
/// `connectivity_seed_sweep`).
#[test]
#[ignore = "barrido amplio; correr con --ignored"]
fn neighbouring_density_connectivity_seed_sweep() {
    const N: usize = 3;
    let side = N * CHUNK_CELLS;

    let mut failures = Vec::new();
    for seed in 0u64..200 {
        let grids = block_grids(N, checkerboard_profile, seed, 0);
        let merged = merge_grids(&grids, N);
        let (reached, total) = flood_fill_merged(&merged, side);
        if reached != total {
            failures.push((seed, reached, total));
        }
    }
    assert!(
        failures.is_empty(),
        "{} seeds con componentes aisladas en bloques de densidad mixta: {:?}",
        failures.len(),
        &failures[..failures.len().min(20)]
    );
}

// ── connect_zone_to_maze — "estampar gana" + borde reservado ─────────────────
//
// Fase 5 solo llama a `connect_zone_to_maze` cuando `is_zone_connected` es
// falso, es decir cuando NINGUNA celda del perímetro exterior de la zona es
// transitable. Es una ruta rara en generación normal, así que estos tests la
// construyen a mano en vez de buscar una seed que la dispare: se estampa una
// zona rodeada íntegramente de contenido (Pillar/Void) y se invoca la función
// directamente, que es como la ejerce el call site de `generator.rs`.

use super::generator::{connect_zone_to_maze, is_zone_connected};

/// Zona de prueba: rectángulo `[5,10) × [5,10)` de celdas `Open`.
const ZX0: i32 = 5;
const ZZ0: i32 = 5;
const ZX1: i32 = 10;
const ZZ1: i32 = 10;

/// Grid sólido con la zona estampada y su perímetro exterior COMPLETO relleno
/// de contenido estampado, alternando Pillar/Void/SealedWall. Eso fuerza
/// `is_zone_connected == false` sin usar ninguna celda transitable.
fn grid_with_sealed_zone() -> LayerGrid {
    let mut grid = LayerGrid::new_solid();

    for z in ZZ0..ZZ1 {
        for x in ZX0..ZX1 {
            grid.set(x as usize, z as usize, Cell::new(CellType::Open, 2, 1));
        }
    }

    // Anillo ortogonal exterior: columnas ZX0-1 / ZX1 y filas ZZ0-1 / ZZ1.
    // Las 3 rondas de contenido estampado/protegido que existen hoy.
    let stamp = |i: i32| match i % 3 {
        0 => Cell::new(CellType::Pillar, 0, 1),
        1 => Cell::new(CellType::Void, 0, 0),
        _ => Cell::new(CellType::SealedWall, 0, 1),
    };
    for z in ZZ0..ZZ1 {
        grid.set((ZX0 - 1) as usize, z as usize, stamp(z));
        grid.set(ZX1 as usize, z as usize, stamp(z + 1));
    }
    for x in ZX0..ZX1 {
        grid.set(x as usize, (ZZ0 - 1) as usize, stamp(x));
        grid.set(x as usize, ZZ1 as usize, stamp(x + 1));
    }

    grid
}

fn count_kind(grid: &LayerGrid, kind: CellType) -> usize {
    grid.cells().iter().filter(|c| c.kind() == kind).count()
}

/// Zonas rectangulares no cuadradas: `open_zone_size_x`/`_z` sustituyen a
/// `open_zone_size` cuando están presentes, produciendo un rect anisotrópico
/// real (no una aproximación). `num_open_zones = 1` para que la zona 1 sea la
/// única del grid — sin ambigüedad de solape al medir su bbox.
#[test]
fn open_zone_size_x_z_produce_a_non_square_zone() {
    let mut rules = LAYER_PROFILES[0].clone();
    rules.num_open_zones = 1;
    rules.open_zone_size_x = Some(3);
    rules.open_zone_size_z = Some(12);
    // Cero escaleras/pozos/voids/anomalías: todas esas fases reasignan
    // zone_id a 0 en la celda que tocan (Fase 6/7), lo que podría "recortar"
    // el bbox medido si cayeran justo en una esquina de la zona. Aisla la
    // medición al estampado puro de Fase 4.
    rules.num_stairs = 0;
    rules.num_pits = 0;
    rules.num_voids = 0;
    rules.num_anomalies = 0;

    for seed in [TEST_SEED, 1, 42, 7778] {
        let out = generate_layer(&rules, seed, TEST_CHUNK, 0, &[]);
        let mut bb: Option<(i32, i32, i32, i32)> = None;
        for z in 0..CHUNK_CELLS {
            for x in 0..CHUNK_CELLS {
                if out.grid.get(x, z).zone_id != 1 {
                    continue;
                }
                let (x, z) = (x as i32, z as i32);
                bb = Some(match bb {
                    None => (x, z, x, z),
                    Some((a, b, c, d)) => (a.min(x), b.min(z), c.max(x), d.max(z)),
                });
            }
        }
        let (x0, z0, x1, z1) = bb.expect("zona 1 sin celdas");
        assert_eq!(x1 - x0 + 1, 3, "seed {seed}: ancho X esperado 3");
        assert_eq!(z1 - z0 + 1, 12, "seed {seed}: alto Z esperado 12");
    }
}

/// (1) El camino de reconexión NUNCA convierte contenido estampado en Corridor.
/// Antes del fix la guarda era `!is_walkable()`, que da true para Pillar y Void
/// y por tanto los borraba: en un PILLAR_HALL eso es un hueco en la retícula de
/// columnas, y en El Vacío un puente sobre el abismo. SealedWall (RoomType)
/// nunca tuvo ese bug histórico — `kind() == Wall` ya lo excluía por
/// construcción — pero se cubre igual para que el test siga barriendo las 3
/// rondas de contenido protegido que `grid_with_sealed_zone` estampa hoy.
#[test]
fn connect_zone_never_carves_stamped_content() {
    let mut grid = grid_with_sealed_zone();
    // Única celda transitable fuera de la zona → destino inequívoco.
    grid.set(2, 2, Cell::new(CellType::Corridor, 2, 0));

    assert!(
        !is_zone_connected(&grid, ZX0, ZZ0, ZX1, ZZ1),
        "el montaje debe dejar la zona aislada, si no el test no ejerce nada"
    );

    let pillars_before = count_kind(&grid, CellType::Pillar);
    let voids_before = count_kind(&grid, CellType::Void);
    let sealed_before = count_kind(&grid, CellType::SealedWall);
    assert!(
        pillars_before > 0 && voids_before > 0 && sealed_before > 0,
        "montaje degenerado"
    );

    connect_zone_to_maze(&mut grid, ZX0, ZZ0, ZX1, ZZ1, 2);

    assert_eq!(
        count_kind(&grid, CellType::Pillar),
        pillars_before,
        "ninguna columna estampada puede desaparecer"
    );
    assert_eq!(
        count_kind(&grid, CellType::Void),
        voids_before,
        "ningún void estampado puede desaparecer"
    );
    assert_eq!(
        count_kind(&grid, CellType::SealedWall),
        sealed_before,
        "ningún perímetro protegido puede desaparecer"
    );

    // La celda del anillo que la L atraviesa primero: sigue estampada.
    let first_on_path = grid.get((ZX0 - 1) as usize, ZZ0 as usize);
    assert!(
        !matches!(first_on_path.kind(), CellType::Corridor),
        "la primera celda del anillo en la ruta se convirtió en Corridor: {:?}",
        first_on_path.kind()
    );

    // Y sí debe haber carvado muro: la reconexión tiene que hacer ALGO.
    assert!(
        grid.get(3, 5).kind() == CellType::Corridor || grid.get(2, 3).kind() == CellType::Corridor,
        "el camino debería haber carvado al menos una celda Wall interior"
    );
}

/// (2) La L nunca escribe en la fila/columna de borde. El montaje deja como
/// única celda transitable una del BORDE (0, 2): antes del fix el barrido de
/// destino la elegía y la L bajaba por la columna x = 0 carvando (0,5), (0,4),
/// (0,3) — apertura unilateral que el chunk vecino no conoce. Ahora el borde no
/// es elegible como destino y la función cae en su rama sin-destino.
#[test]
fn connect_zone_never_writes_on_the_reserved_border() {
    let mut grid = grid_with_sealed_zone();
    grid.set(0, 2, Cell::new(CellType::Corridor, 2, 0)); // transitable EN EL BORDE

    assert!(!is_zone_connected(&grid, ZX0, ZZ0, ZX1, ZZ1));

    connect_zone_to_maze(&mut grid, ZX0, ZZ0, ZX1, ZZ1, 2);

    let last = CHUNK_CELLS - 1;
    for i in 0..CHUNK_CELLS {
        // (0,2) es la celda que el montaje puso a mano; el resto del borde debe
        // seguir sólido, y esa tampoco pudo cambiar de tipo.
        if i != 2 {
            assert_eq!(
                grid.get(0, i).kind(),
                CellType::Wall,
                "columna 0 alterada en z={i}"
            );
        }
        assert_eq!(
            grid.get(last, i).kind(),
            CellType::Wall,
            "columna {last} alterada en z={i}"
        );
        assert_eq!(
            grid.get(i, 0).kind(),
            CellType::Wall,
            "fila 0 alterada en x={i}"
        );
        assert_eq!(
            grid.get(i, last).kind(),
            CellType::Wall,
            "fila {last} alterada en x={i}"
        );
    }
    assert_eq!(
        grid.get(0, 2).kind(),
        CellType::Corridor,
        "la celda de borde del montaje no debe reescribirse"
    );
}

/// (3) El gate sigue funcionando: con la zona YA conectada, el call site de
/// Fase 5 (`if !is_zone_connected { connect_zone_to_maze }`) no entra, y el
/// grid queda intacto. Replica ese patrón literal en vez de instrumentar la
/// llamada.
#[test]
fn connected_zone_skips_reconnection_entirely() {
    let mut grid = grid_with_sealed_zone();
    // Abre UNA celda del anillo → la zona pasa a tener vecino transitable.
    grid.set(
        (ZX0 - 1) as usize,
        ZZ0 as usize,
        Cell::new(CellType::Corridor, 2, 0),
    );

    assert!(
        is_zone_connected(&grid, ZX0, ZZ0, ZX1, ZZ1),
        "con una celda del anillo abierta la zona está conectada"
    );

    let before: Vec<Cell> = grid.cells().to_vec();
    if !is_zone_connected(&grid, ZX0, ZZ0, ZX1, ZZ1) {
        connect_zone_to_maze(&mut grid, ZX0, ZZ0, ZX1, ZZ1, 2);
    }

    assert!(
        before
            .iter()
            .zip(grid.cells().iter())
            .all(|(a, b)| a.kind() == b.kind()),
        "el gate no debe dejar pasar nada cuando la zona ya está conectada"
    );
}

// ── Fase 4 — carvado de entrada explícita ────────────────────────────────────

/// Breach de un perímetro propio: `start` sobre una celda `SealedWall`,
/// avanzando hacia una celda ya transitable a 3 pasos de distancia. Debe
/// convertir `start` Y las celdas Wall intermedias a Corridor, deteniéndose
/// justo al tocar lo transitable (sin tocar esa celda de nuevo).
#[test]
fn carve_explicit_entrance_breaches_own_perimeter_and_reaches_the_maze() {
    let mut grid = LayerGrid::new_solid();
    grid.set(5, 5, Cell::new(CellType::SealedWall, 0, 1)); // perímetro propio
    grid.set(2, 5, Cell::new(CellType::Corridor, 2, 0)); // maze existente

    carve_explicit_entrance(&mut grid, 2, (5, 5), (-1, 0));

    assert_eq!(grid.get(5, 5).kind(), CellType::Corridor, "breach del perímetro");
    assert_eq!(grid.get(4, 5).kind(), CellType::Corridor, "muro intermedio carvado");
    assert_eq!(grid.get(3, 5).kind(), CellType::Corridor, "muro intermedio carvado");
    assert_eq!(
        grid.get(2, 5).kind(),
        CellType::Corridor,
        "la celda ya transitable no debe tocarse de más (sigue siendo Corridor)"
    );
    assert_eq!(
        grid.get(1, 5).kind(),
        CellType::Wall,
        "no debe seguir carvando más allá de donde alcanzó el laberinto"
    );
}

/// Se detiene ante contenido estampado/protegido de OTRA zona (Pillar/Void/
/// SealedWall) sin tocarlo — "estampar gana", mismo criterio que
/// `carve_aperture` y `connect_zone_to_maze`.
#[test]
fn carve_explicit_entrance_stops_before_stamped_content() {
    for (kind, label) in [
        (CellType::Pillar, "Pillar"),
        (CellType::Void, "Void"),
        (CellType::SealedWall, "SealedWall"),
    ] {
        let mut grid = LayerGrid::new_solid();
        grid.set(5, 5, Cell::new(CellType::SealedWall, 0, 1));
        grid.set(3, 5, Cell::new(kind, 0, 9)); // contenido de OTRA zona (zid=9)

        carve_explicit_entrance(&mut grid, 2, (5, 5), (-1, 0));

        assert_eq!(grid.get(5, 5).kind(), CellType::Corridor, "{label}: breach propio sí ocurre");
        assert_eq!(grid.get(4, 5).kind(), CellType::Corridor, "{label}: muro intermedio sí se carva");
        assert_eq!(grid.get(3, 5).kind(), kind, "{label}: contenido ajeno intacto");
    }
}

/// Nunca escribe en el borde reservado (fila/columna 0 o `CHUNK_CELLS - 1`),
/// incluso si el camino cruza todo el chunk sin encontrar nada transitable.
#[test]
fn carve_explicit_entrance_never_writes_the_reserved_border() {
    let mut grid = LayerGrid::new_solid();
    grid.set(2, 5, Cell::new(CellType::SealedWall, 0, 1));

    carve_explicit_entrance(&mut grid, 2, (2, 5), (-1, 0)); // hacia x=0, sin nada transitable

    assert_eq!(grid.get(2, 5).kind(), CellType::Corridor, "breach propio");
    assert_eq!(grid.get(1, 5).kind(), CellType::Corridor, "muro intermedio carvado");
    let last = CHUNK_CELLS - 1;
    assert_eq!(grid.get(0, 5).kind(), CellType::Wall, "borde x=0 intacto");
    for i in 0..CHUNK_CELLS {
        assert_eq!(grid.get(0, i).kind(), CellType::Wall, "columna 0 alterada en z={i}");
        assert_eq!(grid.get(last, i).kind(), CellType::Wall, "columna {last} alterada en z={i}");
        assert_eq!(grid.get(i, 0).kind(), CellType::Wall, "fila 0 alterada en x={i}");
        assert_eq!(grid.get(i, last).kind(), CellType::Wall, "fila {last} alterada en x={i}");
    }
}

/// Grid con TODO el interior (no-borde) transitable — simula "maze en todas
/// partes", así cualquier arista que NO quede bloqueada por el perímetro
/// `SealedWall` se delata como abierta (si el fondo fuera sólido por
/// defecto, una arista mal alineada seguiría leyendo "pared" por accidente,
/// porque el exterior también sería Wall — el test no probaría nada).
fn grid_with_walkable_field() -> LayerGrid {
    let mut grid = LayerGrid::new_solid();
    for z in 1..(CHUNK_CELLS - 1) {
        for x in 1..(CHUNK_CELLS - 1) {
            grid.set(x, z, Cell::new(CellType::Corridor, 2, 0));
        }
    }
    grid
}

/// La cuña triangular negra observada en playtest: sin alinear origen+tamaño
/// a la retícula de tiles de 5 m, un extremo del perímetro `SealedWall` cae a
/// mitad de tile y `tile_walls_from_grid` no lo ve — `edge_open` solo exige
/// que UNA de las dos sub-filas/columnas del tile sea transitable para leer
/// "sin pared", y con el fondo maze-en-todas-partes de arriba eso pasa en
/// cuanto el perímetro no cubre las 2 sub-celdas completas del tile.
///
/// Construye el rect EXACTAMENTE como lo alinea Fase 4 hoy
/// (`tile_aligned_size` + `align_origin_to_tile`, partiendo de un origen
/// impar a propósito, para ejercitar de verdad el ajuste) y verifica que
/// las 4 aristas del perímetro — TODOS los tiles que tocan, no solo uno —
/// producen panel de pared real, no solo celda lógica `SealedWall`.
#[test]
fn tile_aligned_sealed_room_perimeter_renders_real_wall_panels() {
    let max_origin = (CHUNK_CELLS as i32 - 1 - 7).max(1);
    let sz = tile_aligned_size(7);
    let x0 = align_origin_to_tile(3, max_origin);
    let z0 = align_origin_to_tile(3, max_origin);
    assert_eq!(x0 % 2, 0, "origen X debe quedar par tras el ajuste");
    assert_eq!(z0 % 2, 0, "origen Z debe quedar par tras el ajuste");
    assert_eq!(sz % 2, 0, "tamaño debe quedar par tras el ajuste");
    let (x1, z1) = (x0 + sz, z0 + sz);

    let mut grid = grid_with_walkable_field();
    stamp_sealed_room(&mut grid, x0, z0, x1, z1, 1, 2);
    let walls = tile_walls_from_grid(&grid);

    let (tx_west, tx_east) = (x0 / 2 - 1, x1 / 2);
    let (tz_north, tz_south) = (z0 / 2 - 1, z1 / 2);
    let mut gaps = Vec::new();

    for tz in (z0 / 2)..(z1 / 2) {
        if walls[tx_west as usize][tz as usize] & WALL_E == 0 {
            gaps.push(format!("oeste: tile ({tx_west},{tz}) sin WALL_E"));
        }
        if walls[tx_east as usize][tz as usize] & WALL_W == 0 {
            gaps.push(format!("este: tile ({tx_east},{tz}) sin WALL_W"));
        }
    }
    for tx in (x0 / 2)..(x1 / 2) {
        if walls[tx as usize][tz_north as usize] & WALL_S == 0 {
            gaps.push(format!("norte: tile ({tx},{tz_north}) sin WALL_S"));
        }
        if walls[tx as usize][tz_south as usize] & WALL_N == 0 {
            gaps.push(format!("sur: tile ({tx},{tz_south}) sin WALL_N"));
        }
    }

    assert!(
        gaps.is_empty(),
        "perímetro alineado con huecos de render — la cuña triangular no está arreglada:\n{}",
        gaps.join("\n")
    );
}

// ── RoomType — geometría de SealedRoom y CorridorSpine ───────────────────────

/// Estampado + entradas de una `SealedRoom`, aisladas de `generate_layer`
/// (sin Fase 5/6/repair/7 de por medio — geometría pura, determinista, sin
/// depender de que el maze circundante llegue a conectarla). Perímetro
/// completo salvo 1-3 entradas (rango fijo), interior estrictamente Open.
#[test]
fn sealed_room_perimeter_and_entrances_geometry() {
    let perimeter_len = (2 * (ZX1 - ZX0) + 2 * (ZZ1 - ZZ0) - 4) as usize;
    for seed in [TEST_SEED, 1, 42, 7778, 9_999_999, 100, 200, 300] {
        let mut grid = LayerGrid::new_solid();
        stamp_sealed_room(&mut grid, ZX0, ZZ0, ZX1, ZZ1, 1, 2);
        assert_eq!(
            count_kind(&grid, CellType::SealedWall),
            perimeter_len,
            "seed {seed}: perímetro incompleto antes de carvar entradas"
        );

        carve_sealed_room_entrances(&mut grid, 2, seed, ZX0, ZZ0, ZX1, ZZ1);

        let (mut sealed, mut entrances) = (0usize, 0usize);
        for z in ZZ0..ZZ1 {
            for x in ZX0..ZX1 {
                let is_perimeter = x == ZX0 || x == ZX1 - 1 || z == ZZ0 || z == ZZ1 - 1;
                if !is_perimeter {
                    assert_eq!(
                        grid.get(x as usize, z as usize).kind(),
                        CellType::Open,
                        "seed {seed}: interior alterado en ({x},{z})"
                    );
                    continue;
                }
                match grid.get(x as usize, z as usize).kind() {
                    CellType::SealedWall => sealed += 1,
                    CellType::Corridor => entrances += 1,
                    other => panic!(
                        "seed {seed}: perímetro con tipo inesperado {other:?} en ({x},{z})"
                    ),
                }
            }
        }
        assert!(
            (1..=3).contains(&entrances),
            "seed {seed}: {entrances} entradas, esperado 1-3"
        );
        assert_eq!(
            sealed + entrances,
            perimeter_len,
            "seed {seed}: perímetro no cuadra tras carvar entradas"
        );
    }
}

/// Estampado + entradas de un `CorridorSpine`, ambas orientaciones. Ancho
/// caminable fijo (2 celdas) entre los 2 lados largos sellados; extremos
/// cortos abiertos, con carvado extendiéndose más allá del propio footprint.
#[test]
fn corridor_spine_perimeter_and_entrances_geometry() {
    for seed in [TEST_SEED, 1, 42, 7778] {
        // Horizontal: ancho caminable en Z (2 celdas, footprint total 4), largo en X.
        let (x0, z0, x1, z1) = (3, 5, 15, 9); // z1-z0 = 4 = CORRIDOR_SPINE_TOTAL_WIDTH
        let mut grid = LayerGrid::new_solid();
        stamp_corridor_spine(&mut grid, x0, z0, x1, z1, 1, 2, true);
        carve_corridor_spine_entrances(&mut grid, 2, seed, x0, z0, x1, z1, true);

        for x in x0..x1 {
            assert_eq!(
                grid.get(x as usize, z0 as usize).kind(),
                CellType::SealedWall,
                "seed {seed}: lado largo norte no sellado en x={x}"
            );
            assert_eq!(
                grid.get(x as usize, (z1 - 1) as usize).kind(),
                CellType::SealedWall,
                "seed {seed}: lado largo sur no sellado en x={x}"
            );
        }
        for z in (z0 + 1)..(z1 - 1) {
            assert!(
                grid.get(x0 as usize, z as usize).is_walkable(),
                "seed {seed}: extremo oeste no caminable en z={z}"
            );
            assert!(
                grid.get((x1 - 1) as usize, z as usize).is_walkable(),
                "seed {seed}: extremo este no caminable en z={z}"
            );
        }
        let west_extends = (0..CHUNK_CELLS as i32).any(|z| {
            LayerGrid::in_bounds(x0 - 1, z)
                && grid.get((x0 - 1) as usize, z as usize).kind() == CellType::Corridor
        });
        let east_extends = (0..CHUNK_CELLS as i32).any(|z| {
            LayerGrid::in_bounds(x1, z)
                && grid.get(x1 as usize, z as usize).kind() == CellType::Corridor
        });
        assert!(west_extends, "seed {seed}: sin carvado más allá del extremo oeste");
        assert!(east_extends, "seed {seed}: sin carvado más allá del extremo este");
    }

    for seed in [TEST_SEED, 1, 42, 7778] {
        // Vertical: ancho caminable en X (2 celdas, footprint total 4), largo en Z.
        let (x0, z0, x1, z1) = (5, 3, 9, 15);
        let mut grid = LayerGrid::new_solid();
        stamp_corridor_spine(&mut grid, x0, z0, x1, z1, 1, 2, false);
        carve_corridor_spine_entrances(&mut grid, 2, seed, x0, z0, x1, z1, false);

        for z in z0..z1 {
            assert_eq!(
                grid.get(x0 as usize, z as usize).kind(),
                CellType::SealedWall,
                "seed {seed}: lado largo oeste no sellado en z={z}"
            );
            assert_eq!(
                grid.get((x1 - 1) as usize, z as usize).kind(),
                CellType::SealedWall,
                "seed {seed}: lado largo este no sellado en z={z}"
            );
        }
        let north_extends = (0..CHUNK_CELLS as i32).any(|x| {
            LayerGrid::in_bounds(x, z0 - 1)
                && grid.get(x as usize, (z0 - 1) as usize).kind() == CellType::Corridor
        });
        let south_extends = (0..CHUNK_CELLS as i32).any(|x| {
            LayerGrid::in_bounds(x, z1)
                && grid.get(x as usize, z1 as usize).kind() == CellType::Corridor
        });
        assert!(north_extends, "seed {seed}: sin carvado más allá del extremo norte");
        assert!(south_extends, "seed {seed}: sin carvado más allá del extremo sur");
    }
}

// ── SealedWall — sellado de bolsillos sobre una sala incomunicada ───────────

/// Grid con una SealedRoom completamente incomunicada: perímetro SealedWall
/// SIN NINGÚN hueco, interior Open, y una componente "exterior" (corredor)
/// más grande en otra zona del grid para que sea la raíz de
/// `repair_connectivity`. SealedWall no es `passable` para el BFS de
/// reparación (guarda de la Pieza 1), así que la sala nunca puede conectarse
/// por esa vía — el "caso borde" que documenta DECISIONS.md/RoomType.
fn grid_with_incommunicado_sealed_room() -> LayerGrid {
    let mut grid = LayerGrid::new_solid();

    for z in ZZ0..ZZ1 {
        for x in ZX0..ZX1 {
            let perimeter = x == ZX0 || x == ZX1 - 1 || z == ZZ0 || z == ZZ1 - 1;
            let cell = if perimeter {
                Cell::new(CellType::SealedWall, 0, 1)
            } else {
                Cell::new(CellType::Open, 2, 1)
            };
            grid.set(x as usize, z as usize, cell);
        }
    }

    // Componente "exterior" más grande, lejos de la sala, para que sea la raíz.
    for x in 1..15 {
        grid.set(x as usize, 1, Cell::new(CellType::Corridor, 2, 0));
    }

    grid
}

/// El sellado de bolsillos (post-Fase 6, dentro de `repair_connectivity`) NO
/// distingue "interior de una SealedRoom legítima" de "bolsillo de ruido": si
/// la sala queda TOTALMENTE incomunicada (sin ninguna entrada viva), su
/// interior Open se convierte en Wall igual que cualquier otro bolsillo. El
/// perímetro SealedWall en sí es inmune (nunca `is_walkable()`, la guarda de
/// la Pieza 1 ni siquiera necesita activarse para protegerlo), pero el
/// CONTENIDO que encerraba desaparece igual.
///
/// Limitación conocida, documentada en DECISIONS.md (RoomType, "caso borde
/// 5(a)") — este test la deja EXPLÍCITA en vez de silenciosa: si algún día
/// `repair_connectivity` aprende a preservar salas selladas en vez de
/// borrarlas, este test debe actualizarse A PROPÓSITO, no romperse por
/// sorpresa sin que nadie lo note.
#[test]
fn repair_connectivity_erases_a_fully_incommunicado_sealed_room() {
    let mut grid = grid_with_incommunicado_sealed_room();

    let sealed_before = count_kind(&grid, CellType::SealedWall);
    let open_before = count_kind(&grid, CellType::Open);
    assert!(sealed_before > 0 && open_before > 0, "montaje degenerado");

    repair_connectivity(&mut grid, 2);

    assert_eq!(
        count_kind(&grid, CellType::SealedWall),
        sealed_before,
        "el perímetro SealedWall no debería poder desaparecer NUNCA"
    );
    assert_eq!(
        count_kind(&grid, CellType::Open),
        0,
        "una SealedRoom sin entradas debería perder TODO su interior Open al \
         sellado de bolsillos (limitación conocida, ver DECISIONS.md/RoomType \
         — si esto falla, alguien cambió el comportamiento; actualiza este \
         test a propósito)"
    );
}

// ── Fase 1 — sesgo de rectitud configurable ──────────────────────────────────

/// FNV-1a sobre los 4 bytes de cada celda (tipo, techo, zone_id). Cubre el grid
/// entero, no solo la topología: si cambiara un `ceiling_height` el hash lo ve.
fn grid_fingerprint(grid: &LayerGrid) -> u64 {
    let mut h: u64 = 0xcbf2_9ce4_8422_2325;
    for c in grid.cells() {
        for b in [
            c.cell_type,
            c.ceiling_height,
            c.zone_id as u8,
            (c.zone_id >> 8) as u8,
        ] {
            h ^= b as u64;
            h = h.wrapping_mul(0x100_0000_01b3);
        }
    }
    h
}

/// Huellas capturadas del generador ANTES de introducir `straight_bias` /
/// `branch_persistence` (y DESPUÉS del fix de `connect_zone_to_maze`, que sí
/// cambia output a propósito). Son el contrato de determinismo de seeds ya
/// jugadas: si una de estas cambia, todo mundo existente se regenera distinto.
///
/// NO se regeneran "porque el test falla". Un fallo aquí significa que un
/// cambio alteró la secuencia de sorteos del caso por defecto, que es
/// exactamente lo que estas constantes existen para impedir.
const PHASE1_GOLDENS: [((i32, u64), u64); 16] = [
    ((0, 3133931653), 0x0FF40FF7E328D804),
    ((0, 1), 0x6AA9C9BF3A0D1E3F),
    ((0, 42), 0xF6C220EB54522D7E),
    ((0, 7778), 0xD1FC7FC55857B890),
    ((1, 3133931653), 0x729F00EC161AE35A),
    ((1, 1), 0x94A9570BED47AA30),
    ((1, 42), 0xA64F9DF2D6414E94),
    ((1, 7778), 0xBB477C0673433452),
    ((2, 3133931653), 0x21F5AA54AC3E781D),
    ((2, 1), 0x20E48EE57701349E),
    ((2, 42), 0x037F8205D291E17A),
    ((2, 7778), 0x866CE51249E7FF3F),
    ((3, 3133931653), 0x424D9C1396AC3BB7),
    ((3, 1), 0x1290567179328DCF),
    ((3, 42), 0x428CC16E52B3433E),
    ((3, 7778), 0x0C875E3B81D0BDE6),
];

/// (1) REGRESIÓN CRÍTICA. Con los defaults (`straight_bias` 0.0,
/// `branch_persistence` 0.82 — el literal que sustituye) los 4 perfiles reales
/// producen grid byte-idéntico al de antes del cambio. Es el test más
/// importante de esta pieza: si falla, el sesgo de rectitud no se commitea.
#[test]
fn default_profiles_generate_byte_identical_grids() {
    let mut drift = Vec::new();
    for ((layer, seed), expected) in PHASE1_GOLDENS {
        let out = generate_chunk_layer(
            &LAYER_PROFILES[layer as usize],
            seed,
            TEST_CHUNK,
            layer,
            &[],
        );
        let got = grid_fingerprint(&out.grid);
        if got != expected {
            drift.push(format!(
                "layer {layer} seed {seed}: esperado 0x{expected:016X}, obtenido 0x{got:016X}"
            ));
        }
    }
    assert!(
        drift.is_empty(),
        "la generación por defecto cambió — se regeneraría CADA chunk de toda seed ya jugada:\n{}",
        drift.join("\n")
    );
}

/// (1b) Mismo blindaje que el test anterior, pero con `room_type_weights`
/// puesto EXPLÍCITAMENTE a `(1.0, 0.0, 0.0)` en vez de heredarlo del literal
/// de `LAYER_PROFILES` — cubre el camino `weights == default` de la propia
/// comparación en Fase 4, no solo "nadie tocó el campo".
#[test]
fn explicit_default_room_type_weights_generate_byte_identical_grids() {
    let mut drift = Vec::new();
    for ((layer, seed), expected) in PHASE1_GOLDENS {
        let mut rules = LAYER_PROFILES[layer as usize].clone();
        rules.room_type_weights = (1.0, 0.0, 0.0);
        let out = generate_chunk_layer(&rules, seed, TEST_CHUNK, layer, &[]);
        let got = grid_fingerprint(&out.grid);
        if got != expected {
            drift.push(format!(
                "layer {layer} seed {seed}: esperado 0x{expected:016X}, obtenido 0x{got:016X}"
            ));
        }
    }
    assert!(
        drift.is_empty(),
        "room_type_weights = (1.0, 0.0, 0.0) explícito debería seguir dando el grid histórico:\n{}",
        drift.join("\n")
    );
}

/// Cableado end-to-end: `room_type_weights` no default de verdad llega al
/// grid final de `generate_layer` (no solo a las funciones de estampado
/// aisladas que prueban las 2 geometrías de arriba).
#[test]
fn sealed_room_weight_actually_reaches_generate_layer() {
    let mut rules = LAYER_PROFILES[0].clone();
    rules.room_type_weights = (0.0, 1.0, 0.0); // siempre SealedRoom
    for seed in [TEST_SEED, 1, 42, 7778] {
        let out = generate_layer(&rules, seed, TEST_CHUNK, 0, &[]);
        assert!(
            count_kind(&out.grid, CellType::SealedWall) > 0,
            "seed {seed}: weights=(0,1,0) debería producir SealedWall en el grid final"
        );
    }
}

/// Idem para CorridorSpine — y confirma "sin pilares" de verdad en el grid
/// final (no solo por construcción del umbral `min(eff_sz) >= 6`).
#[test]
fn corridor_spine_weight_actually_reaches_generate_layer() {
    let mut rules = LAYER_PROFILES[0].clone();
    rules.room_type_weights = (0.0, 0.0, 1.0); // siempre CorridorSpine
    for seed in [TEST_SEED, 1, 42, 7778] {
        let out = generate_layer(&rules, seed, TEST_CHUNK, 0, &[]);
        assert!(
            count_kind(&out.grid, CellType::SealedWall) > 0,
            "seed {seed}: weights=(0,0,1) debería producir SealedWall en el grid final"
        );
        assert_eq!(
            count_kind(&out.grid, CellType::Pillar),
            0,
            "seed {seed}: CorridorSpine nunca debería sembrar pilares"
        );
    }
}

/// Perfil de laboratorio: sólo Fase 1. Todo lo que ensucia la medida de tramos
/// rectos (ensanchado, erosión, zonas, voids, escaleras) queda apagado, así el
/// grid resultante ES el laberinto del backtracker y nada más.
fn phase1_only_profile(straight_bias: f32, branch_persistence: f32) -> LayerRules {
    let mut r = LAYER_PROFILES[0].clone();
    r.wide_chance = 0.0;
    r.erode_chance = 0.0;
    r.num_open_zones = 0;
    r.open_zone_size = 0;
    r.pillar_chance = 0.0;
    r.num_anomalies = 0;
    r.num_stairs = 0;
    r.num_pits = 0;
    r.num_voids = 0;
    r.straight_bias = straight_bias;
    r.branch_persistence = branch_persistence;
    r
}

/// Longitud media de los tramos rectos maximales de celdas transitables,
/// contando por filas y por columnas. Un laberinto más recto sube esta media.
fn mean_straight_run(grid: &LayerGrid) -> f64 {
    let mut runs: Vec<usize> = Vec::new();
    for fixed in 0..CHUNK_CELLS {
        for horizontal in [true, false] {
            let mut run = 0usize;
            for moving in 0..CHUNK_CELLS {
                let (x, z) = if horizontal {
                    (moving, fixed)
                } else {
                    (fixed, moving)
                };
                if grid.get(x, z).is_walkable() {
                    run += 1;
                } else {
                    if run > 1 {
                        runs.push(run);
                    }
                    run = 0;
                }
            }
            if run > 1 {
                runs.push(run);
            }
        }
    }
    if runs.is_empty() {
        return 0.0;
    }
    runs.iter().sum::<usize>() as f64 / runs.len() as f64
}

/// (2) Con el sesgo alto los tramos rectos son MÁS LARGOS en media. Afirmación
/// estadística sobre un barrido de seeds, no exacta por seed: el sesgo es una
/// probabilidad, no una garantía, y una seed suelta puede ir en contra.
#[test]
fn straight_bias_lengthens_runs_on_average() {
    const SEEDS: u64 = 40;
    let base = phase1_only_profile(0.0, 0.82);
    let biased = phase1_only_profile(0.9, 0.82);

    let mut base_sum = 0.0;
    let mut biased_sum = 0.0;
    for seed in 0..SEEDS {
        base_sum += mean_straight_run(&generate_layer(&base, seed, TEST_CHUNK, 0, &[]).grid);
        biased_sum += mean_straight_run(&generate_layer(&biased, seed, TEST_CHUNK, 0, &[]).grid);
    }
    let (base_mean, biased_mean) = (base_sum / SEEDS as f64, biased_sum / SEEDS as f64);

    assert!(
        biased_mean > base_mean,
        "straight_bias 0.9 debería alargar los tramos rectos: base {base_mean:.3} vs sesgado {biased_mean:.3}"
    );
}

/// El sesgo no puede romper el invariante número uno del módulo: el grid sigue
/// siendo conexo. Sería un modo de fallo plausible — sesgar la exploración
/// altera el orden de visita del backtracker.
#[test]
fn straight_bias_keeps_the_maze_connected() {
    let biased = phase1_only_profile(0.95, 0.82);
    for seed in 0..25u64 {
        let out = generate_chunk_layer(&biased, seed, TEST_CHUNK, 0, &[]);
        let (reached, total) = flood_fill_walkable(&out.grid);
        assert_eq!(reached, total, "seed {seed}: grid no conexo con sesgo alto");
    }
}

/// (3) `branch_persistence` se lee de verdad de la config y no del 0.82 que
/// había incrustado. Tres valores distintos, todo lo demás igual, tres grids
/// distintos — y el 0.82 explícito reproduce exactamente el perfil por defecto,
/// que es la prueba de que sustituir el literal no cambió nada.
#[test]
fn branch_persistence_comes_from_the_profile_not_the_old_literal() {
    let grid_for = |bp: f32| {
        grid_fingerprint(
            &generate_layer(&phase1_only_profile(0.0, bp), 4242, TEST_CHUNK, 0, &[]).grid,
        )
    };

    let (low, mid, high) = (grid_for(0.0), grid_for(0.82), grid_for(1.0));
    assert_ne!(low, mid, "branch_persistence 0.0 y 0.82 deben divergir");
    assert_ne!(mid, high, "branch_persistence 0.82 y 1.0 deben divergir");
    assert_ne!(low, high, "branch_persistence 0.0 y 1.0 deben divergir");

    // El valor por defecto del perfil ES 0.82: pedirlo explícitamente no puede
    // cambiar nada respecto a dejarlo en su default.
    let mut defaulted = phase1_only_profile(0.0, 0.82);
    defaulted.branch_persistence = LAYER_PROFILES[0].branch_persistence;
    assert_eq!(
        mid,
        grid_fingerprint(&generate_layer(&defaulted, 4242, TEST_CHUNK, 0, &[]).grid),
        "el default del perfil debe seguir siendo el 0.82 histórico"
    );
}
