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
            assert!(total > 0, "seed {seed} capa {layer_index}: grid sin celdas transitables");
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
        assert_eq!(reached, total, "seed {seed} capa 3: aún hay componentes aisladas");

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
    assert_ne!(a.grid.cells(), b.grid.cells(), "seeds distintas produjeron el mismo grid");
}

#[test]
fn different_chunk_coords_produce_different_grids() {
    let rules = &LAYER_PROFILES[1];
    let a = generate_layer(rules, TEST_SEED, (0, 0), 1, &[]);
    let b = generate_layer(rules, TEST_SEED, (1, 0), 1, &[]);
    assert_ne!(a.grid.cells(), b.grid.cells(), "chunks distintos produjeron el mismo grid");
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
        let stairs = out.grid.cells().iter().filter(|c| c.kind() == CellType::Stair).count();
        let pits = out.grid.cells().iter().filter(|c| c.kind() == CellType::Pit).count();
        assert_eq!(stairs, rules.num_stairs as usize, "capa {layer_index}: nº de escaleras no respeta el perfil");
        assert_eq!(pits, rules.num_pits as usize, "capa {layer_index}: nº de pozos no respeta el perfil");
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
        open_0 += v.grid.cells().iter().filter(|c| c.kind() != CellType::Wall).count();
        open_2 += c.grid.cells().iter().filter(|c| c.kind() != CellType::Wall).count();
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
            assert!(!east_a.is_empty(), "seed {seed} capa {layer_index}: borde este de (0,0) sin apertura");
            assert_eq!(east_a, west_b, "seed {seed} capa {layer_index}: aperturas E/O no coinciden");

            // Norte↔Sur
            let c = generate_chunk_layer(rules, seed, (0, 1), layer_index, &[]);
            let north_a: Vec<usize> = (0..CHUNK_CELLS)
                .filter(|&x| a.grid.get(x, CHUNK_CELLS - 1).is_walkable())
                .collect();
            let south_c: Vec<usize> = (0..CHUNK_CELLS)
                .filter(|&x| c.grid.get(x, 0).is_walkable())
                .collect();
            assert!(!north_a.is_empty(), "seed {seed} capa {layer_index}: borde norte de (0,0) sin apertura");
            assert_eq!(north_a, south_c, "seed {seed} capa {layer_index}: aperturas N/S no coinciden");
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
                    let out = generate_chunk_layer(rules, seed, (ccx as i32, ccz as i32), layer_index, &[]);
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
            for i in 0..side * side {
                if merged[i].is_walkable() {
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
