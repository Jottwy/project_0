//! ADR-093 E1 — la MITAD DE COLISIÓN de la región del Level 4.
//!
//! Mismo reparto que la habitación construible (`build_room_layout`) y las salas
//! autoradas (`authored_room_layout`): la lógica compartida y la rejilla fina de 2,5 m
//! viven en `grid_gen::level4`; aquí se construye el `ChunkLayoutV1` de 5 m que lee la
//! colisión del jugador (`Level0Collision::resolve_move`). Las dos representaciones
//! rasterizan EL MISMO `Level4Layout`, y la paridad par del generador hace cada tile de
//! 5 m uniforme (sus 4 celdas finas coinciden): no pueden discrepar ni en media celda.

use super::architecture::chunk_generator::chunk_seed_layer;
use super::architecture::layout_grammars::TEMPLATE_OFFICE;
use super::chunk::{
    Chunk, ChunkLayer, ChunkLayoutV1, ChunkState, CELL_BLOCKED, CELL_WALKABLE, CELL_WALL,
    EDGE_KIND_OPEN, EDGE_KIND_WALL, LAYOUT_GRID_SIZE, ZONE_OFFICE,
};
use super::grid_gen::level4;
use crate::utils::ChunkPos;

/// Genera el chunk COMPLETO (colisión de 5 m) de una posición de la reserva de región.
///
/// `local` es el índice devuelto por `level4::region_chunk_local(pos)` — el llamador ya
/// lo comprobó. Estado `stabilized + anchored`: la región queda FUERA del chunk
/// displacement de ADR-067; que el intercambio simétrico eligiera un chunk de la
/// reserva mandaría medio Level 4 al Level 0 y viceversa.
pub fn generate_region_chunk(
    world_seed: u64,
    pos: ChunkPos,
    layer: ChunkLayer,
    local: (i32, i32),
) -> Chunk {
    let grid = LAYOUT_GRID_SIZE as usize;
    let layout_l4 = level4::generate(world_seed, level4::EPOCH_V1);

    // Tile de 5 m (tx,tz) del chunk local → celda fina (2tx, 2tz) en coordenadas de
    // REGIÓN. Con paridad par, esa celda representa el tile entero.
    let open_at = |tx: i32, tz: i32| -> bool {
        if layer != 0 {
            return false;
        }
        let cell = (
            local.0 * grid as i32 * 2 + tx * 2,
            local.1 * grid as i32 * 2 + tz * 2,
        );
        layout_l4.cell_open(cell)
    };

    let mut cells = vec![CELL_WALL | CELL_BLOCKED; grid * grid];
    for tz in 0..grid {
        for tx in 0..grid {
            if open_at(tx as i32, tz as i32) {
                cells[tz * grid + tx] = CELL_WALKABLE;
            }
        }
    }

    let mut layout = ChunkLayoutV1::new(cells, 0, ZONE_OFFICE);

    // Aristas desde la misma verdad: OPEN solo entre dos tiles abiertos; todo lo demás,
    // WALL. `open_at` acepta índices fuera del chunk (−1, grid): consulta el layout
    // GLOBAL de región, así que un pasillo que cruza de chunk abre su arista de borde
    // en los dos chunks por construcción. Fuera de la reserva no hay tiles abiertos y
    // el perímetro exterior queda sellado.
    for tz in 0..grid as i32 {
        for bx in 0..=grid as i32 {
            let kind = if open_at(bx - 1, tz) && open_at(bx, tz) {
                EDGE_KIND_OPEN
            } else {
                EDGE_KIND_WALL
            };
            layout.set_edge_v(bx as usize, tz as usize, kind);
        }
    }
    for bz in 0..=grid as i32 {
        for tx in 0..grid as i32 {
            let kind = if open_at(tx, bz - 1) && open_at(tx, bz) {
                EDGE_KIND_OPEN
            } else {
                EDGE_KIND_WALL
            };
            layout.set_edge_h(tx as usize, bz as usize, kind);
        }
    }

    Chunk {
        pos,
        layer,
        state: ChunkState::Active {
            stabilized: true,
            anchored: true,
        },
        seed: chunk_seed_layer(world_seed, pos, layer),
        owner: None,
        entities: Vec::new(),
        items: Vec::new(),
        // Nunca elegible para displacement; el valor solo existe porque el campo existe.
        teleport_timer: f32::MAX,
        template_id: TEMPLATE_OFFICE,
        rotation: 0,
        mirrored: false,
        has_workbench: false,
        layout,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::grid_gen::level4::{region_chunk_local, REGION_CHUNKS, REGION_ORIGIN_CHUNK};

    fn region_pos(lx: i32, lz: i32) -> ChunkPos {
        (REGION_ORIGIN_CHUNK.0 + lx, REGION_ORIGIN_CHUNK.1 + lz)
    }

    /// Verificación (a) de ADR-093 en la mitad de 5 m, cruzada contra la rejilla fina:
    /// cada tile caminable del layout de colisión coincide con sus 4 celdas finas, en
    /// TODOS los chunks de la reserva. Es el test cruzado que a `build_rooms` le faltó
    /// (auditoría 2026-08-18, punto 2) — aquí entra desde el primer día.
    #[test]
    fn every_5m_tile_matches_its_four_fine_cells() {
        for seed in [42u64, 7778] {
            for lx in 0..REGION_CHUNKS {
                for lz in 0..REGION_CHUNKS {
                    let pos = region_pos(lx, lz);
                    let local = region_chunk_local(pos).unwrap();
                    let chunk = generate_region_chunk(seed, pos, 0, local);
                    let fine = crate::world::grid_gen::level4::generate_region_layer(
                        seed,
                        crate::world::grid_gen::level4::EPOCH_V1,
                        local,
                        0,
                    );
                    let grid = LAYOUT_GRID_SIZE as usize;
                    for tx in 0..grid {
                        for tz in 0..grid {
                            let walk5 = chunk.layout.is_cell_walkable(tx, tz);
                            for (dx, dz) in [(0, 0), (1, 0), (0, 1), (1, 1)] {
                                let walk25 = fine.grid.get(tx * 2 + dx, tz * 2 + dz).is_walkable();
                                assert_eq!(
                                    walk5, walk25,
                                    "seed {seed} chunk ({lx},{lz}) tile ({tx},{tz}) subcelda ({dx},{dz})"
                                );
                            }
                        }
                    }
                }
            }
        }
    }

    #[test]
    fn region_chunks_are_deterministic_and_displacement_proof() {
        let pos = region_pos(1, 1);
        let local = region_chunk_local(pos).unwrap();
        let a = generate_region_chunk(42, pos, 0, local);
        let b = generate_region_chunk(42, pos, 0, local);
        assert_eq!(a.layout, b.layout);
        assert!(
            matches!(
                a.state,
                ChunkState::Active {
                    stabilized: true,
                    anchored: true
                }
            ),
            "un chunk de la reserva elegible para displacement mezclaría los niveles"
        );
        assert_eq!(a.layout.zone_kind, ZONE_OFFICE);
        assert!(a.entities.is_empty() && a.items.is_empty());
    }

    /// Un pasillo que cruza de chunk abre la arista de borde EN LOS DOS chunks; donde no
    /// hay pasillo, el borde queda tabicado. Y el perímetro exterior de la reserva es
    /// muro siempre.
    #[test]
    fn seams_open_exactly_where_the_global_layout_crosses() {
        let grid = LAYOUT_GRID_SIZE as usize;
        for seed in [42u64, 7778] {
            let layout_l4 = level4::generate(seed, level4::EPOCH_V1);
            for lz in 0..REGION_CHUNKS {
                let left_pos = region_pos(0, lz);
                let right_pos = region_pos(1, lz);
                let left =
                    generate_region_chunk(seed, left_pos, 0, region_chunk_local(left_pos).unwrap());
                let right = generate_region_chunk(
                    seed,
                    right_pos,
                    0,
                    region_chunk_local(right_pos).unwrap(),
                );
                for tz in 0..grid {
                    let a = (grid as i32 * 2 - 2, lz * grid as i32 * 2 + tz as i32 * 2);
                    let b = (grid as i32 * 2, a.1);
                    let crosses = layout_l4.cell_open(a) && layout_l4.cell_open(b);
                    let expected = if crosses {
                        EDGE_KIND_OPEN
                    } else {
                        EDGE_KIND_WALL
                    };
                    assert_eq!(
                        left.layout.edge_v(grid, tz),
                        expected,
                        "seed {seed} fila {lz}/{tz}: borde este del chunk (0,{lz})"
                    );
                    assert_eq!(
                        right.layout.edge_v(0, tz),
                        expected,
                        "seed {seed} fila {lz}/{tz}: borde oeste del chunk (1,{lz})"
                    );
                }
            }

            // Perímetro exterior oeste de la reserva: tabicado entero.
            for lz in 0..REGION_CHUNKS {
                let pos = region_pos(0, lz);
                let chunk = generate_region_chunk(seed, pos, 0, region_chunk_local(pos).unwrap());
                for tz in 0..grid {
                    assert_eq!(
                        chunk.layout.edge_v(0, tz),
                        EDGE_KIND_WALL,
                        "seed {seed}: el perímetro exterior de la reserva tiene un hueco"
                    );
                }
            }
        }
    }

    #[test]
    fn non_zero_layers_have_no_walkable_tiles() {
        let pos = region_pos(2, 0);
        let local = region_chunk_local(pos).unwrap();
        for layer in [-1, 1] {
            let chunk = generate_region_chunk(42, pos, layer, local);
            let grid = LAYOUT_GRID_SIZE as usize;
            for tx in 0..grid {
                for tz in 0..grid {
                    assert!(!chunk.layout.is_cell_walkable(tx, tz), "capa {layer}");
                }
            }
        }
    }
}
