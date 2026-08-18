//! ADR-081 enmienda 5 — la MITAD DE COLISIÓN del tallado de la habitación construible.
//!
//! La otra mitad, y el emplazamiento compartido, viven en `grid_gen::build_rooms`. Están partidas
//! porque el mundo existe en dos representaciones y `grid_gen` no puede importar `world/`:
//!
//! | representación | celda | quién la usa |
//! |---|---|---|
//! | `ChunkLayoutV1` (aquí) | 5 m | la colisión del JUGADOR (`Level0Collision::resolve_move`) |
//! | `LayerGrid` (allí) | 2,5 m | lo que se VE y el robapieles |
//!
//! **Tallar una sola no vale**: solo el render da paredes que ves y atraviesas; solo la colisión,
//! paredes invisibles que te frenan. Las dos leen el MISMO `RoomPlan`, así que no pueden discrepar
//! sobre dónde está la habitación — que es la única forma de que esto no se pudra.
//!
//! Aquí sale gratis una coincidencia útil: la celda de layout mide 5 m, o sea EXACTAMENTE un tile.
//! Una habitación de 3 × 3 tiles son 3 × 3 celdas de esta rejilla, sin conversión ninguna.

use super::chunk::{
    ChunkLayoutV1, CELL_BLOCKED, CELL_PILLAR, CELL_PIT, CELL_WALKABLE, CELL_WALL, EDGE_KIND_DOOR,
    EDGE_KIND_OPEN, EDGE_KIND_WALL,
};
use super::grid_gen::{RoomPlan, ROOM_TILES};

/// Talla la habitación en el layout de colisión: interior caminable, perímetro con pared, y la
/// puerta abierta por el mismo lado que en la rejilla fina.
///
/// El interior se limpia de `CELL_WALL | CELL_PILLAR | CELL_BLOCKED | CELL_PIT` además de marcarse
/// caminable: `is_cell_walkable` exige las dos cosas, y una columna heredada de la plantilla que el
/// generador hubiera puesto ahí dejaría un pilar invisible en mitad de la sala — invisible porque el
/// render sale de la OTRA representación, donde ese pilar ya no existe.
pub fn carve_into_layout(layout: &mut ChunkLayoutV1, plan: &RoomPlan) {
    let grid = layout.grid_size as usize;
    let (x0, z0) = (plan.tile_x, plan.tile_z);
    let x1 = x0 + ROOM_TILES; // exclusivo
    let z1 = z0 + ROOM_TILES;

    if x1 > grid || z1 > grid {
        return; // no debería ocurrir con el rango de `room_in_chunk`; no se talla a medias
    }

    for x in x0..x1 {
        for z in z0..z1 {
            if let Some(idx) = layout.cell_index(x, z) {
                let cell = &mut layout.cells[idx];
                *cell &= !(CELL_WALL | CELL_PILLAR | CELL_BLOCKED | CELL_PIT);
                *cell |= CELL_WALKABLE;
            }
        }
    }

    if !layout.has_edges() {
        return; // layout de un peer antiguo, sin aristas: el interior caminable ya es lo esencial
    }

    // Aristas INTERIORES abiertas: la sala es una pieza, no nueve casillas tabicadas.
    for x in x0..x1 {
        for bz in (z0 + 1)..z1 {
            layout.set_edge_h(x, bz, EDGE_KIND_OPEN);
        }
    }
    for bx in (x0 + 1)..x1 {
        for z in z0..z1 {
            layout.set_edge_v(bx, z, EDGE_KIND_OPEN);
        }
    }

    // Perímetro a pared, los cuatro lados.
    for x in x0..x1 {
        layout.set_edge_h(x, z0, EDGE_KIND_WALL);
        layout.set_edge_h(x, z1, EDGE_KIND_WALL);
    }
    for z in z0..z1 {
        layout.set_edge_v(x0, z, EDGE_KIND_WALL);
        layout.set_edge_v(x1, z, EDGE_KIND_WALL);
    }

    // Y la puerta, en el tile CENTRAL del lado sorteado — el mismo criterio (`ROOM_CELLS / 2`) que
    // usa el tallado de la rejilla fina, para que el hueco que se ve y el que se cruza coincidan.
    let mid = ROOM_TILES / 2;
    match plan.door_side {
        0 => layout.set_edge_h(x0 + mid, z0, EDGE_KIND_DOOR), // sur (−z)
        1 => layout.set_edge_h(x0 + mid, z1, EDGE_KIND_DOOR), // norte (+z)
        2 => layout.set_edge_v(x0, z0 + mid, EDGE_KIND_DOOR), // oeste (−x)
        _ => layout.set_edge_v(x1, z0 + mid, EDGE_KIND_DOOR), // este (+x)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::grid_gen::room_in_chunk;

    fn plan_at(tile_x: usize, tile_z: usize, door_side: u8) -> RoomPlan {
        RoomPlan {
            tile_x,
            tile_z,
            door_side,
        }
    }

    fn solid_layout() -> ChunkLayoutV1 {
        let size = crate::world::chunk::LAYOUT_GRID_SIZE as usize;
        let mut layout = ChunkLayoutV1::new(vec![CELL_WALL; size * size], 0, 0);
        // Todo tabicado, que es el peor caso: si el tallado no abre nada, se nota.
        for i in 0..layout.edges_v.len() {
            layout.edges_v[i] = EDGE_KIND_WALL;
        }
        for i in 0..layout.edges_h.len() {
            layout.edges_h[i] = EDGE_KIND_WALL;
        }
        layout
    }

    /// LO QUE HACE QUE SE PUEDA ANDAR DENTRO. Sin esto el jugador ve la sala y rebota contra el aire.
    #[test]
    fn the_interior_becomes_walkable_even_over_solid_rock() {
        let mut layout = solid_layout();
        carve_into_layout(&mut layout, &plan_at(3, 4, 0));

        for x in 3..6 {
            for z in 4..7 {
                assert!(
                    layout.is_cell_walkable(x, z),
                    "celda ({x},{z}) no caminable: la colisión no conoce la habitación"
                );
            }
        }
    }

    /// Una columna heredada de la plantilla dentro de la sala sería un obstáculo INVISIBLE: el render
    /// sale de la otra representación, donde el tallado ya la borró.
    #[test]
    fn the_interior_is_cleared_of_pillars_and_blockers() {
        let mut layout = solid_layout();
        let idx = layout.cell_index(4, 5).unwrap();
        layout.cells[idx] = CELL_PILLAR | CELL_BLOCKED | CELL_PIT;

        carve_into_layout(&mut layout, &plan_at(3, 4, 0));

        assert!(layout.is_cell_walkable(4, 5));
    }

    /// El interior es UNA sala, no nueve casillas: las aristas de dentro se abren.
    #[test]
    fn interior_edges_are_open_and_the_perimeter_is_walled() {
        let mut layout = solid_layout();
        carve_into_layout(&mut layout, &plan_at(3, 4, 2));

        assert_eq!(
            layout.edge_v(4, 5),
            EDGE_KIND_OPEN,
            "arista interior tabicada"
        );
        assert_eq!(
            layout.edge_h(4, 5),
            EDGE_KIND_OPEN,
            "arista interior tabicada"
        );

        // Perímetro: el lado opuesto a la puerta (este) tiene que seguir siendo muro entero.
        for z in 4..7 {
            assert_eq!(
                layout.edge_v(6, z),
                EDGE_KIND_WALL,
                "el muro este tiene un hueco"
            );
        }
    }

    /// La puerta se abre por el lado que dice el plan, y por ese solo. Si el layout y la rejilla fina
    /// eligieran lados distintos, habría una puerta que se ve y no se cruza, y un muro que se cruza
    /// sin verse.
    #[test]
    fn the_door_opens_on_the_side_the_plan_says() {
        for (side, check) in [(0u8, "south"), (1, "north"), (2, "west"), (3, "east")] {
            let mut layout = solid_layout();
            carve_into_layout(&mut layout, &plan_at(3, 4, side));

            let door = match side {
                0 => layout.edge_h(4, 4),
                1 => layout.edge_h(4, 7),
                2 => layout.edge_v(3, 5),
                _ => layout.edge_v(6, 5),
            };
            assert_eq!(
                door, EDGE_KIND_DOOR,
                "lado {check}: la puerta no está abierta"
            );
        }
    }

    /// El plan que se talla aquí es EL MISMO que talla la rejilla fina: mismo origen en tiles, mismo
    /// lado de puerta. Es la garantía de que las dos representaciones no divergen.
    #[test]
    fn the_layout_carve_uses_the_same_plan_as_the_grid_carve() {
        let mut found = 0;
        for cx in 0..40 {
            for cz in 0..40 {
                let Some(plan) = room_in_chunk(42, cx, cz, 0) else {
                    continue;
                };
                found += 1;
                let mut layout = solid_layout();
                carve_into_layout(&mut layout, &plan);

                // El origen en tiles del plan es directamente el índice de celda del layout: la
                // celda de layout mide 5 m, igual que el tile.
                assert!(layout.is_cell_walkable(plan.tile_x, plan.tile_z));
                assert!(layout
                    .is_cell_walkable(plan.tile_x + ROOM_TILES - 1, plan.tile_z + ROOM_TILES - 1));
            }
        }
        assert!(found > 0, "ni una habitación en 40x40 chunks del seed 42");
    }

    /// El lado de puerta que TALLA la colisión (`plan.door_side`, crudo) tiene que ser el MISMO
    /// que el lado que de verdad abrió la rejilla fina (`carve_into_grid`, que prueba los 4 lados
    /// empezando por el sorteado y puede acabar usando uno distinto si el túnel del primero muere
    /// contra la costura). Si divergen: pared invisible en la puerta que se VE abierta, o un muro
    /// que se ve solo y se cruza igual.
    ///
    /// Cubre el hallazgo Grave "colisión fija vs. render con fallback de 4 lados"
    /// (docs/DEBT-ROADMAP.md): `carve_into_layout` talla siempre `plan.door_side` crudo, nunca el
    /// lado resuelto por `carve_into_grid` (que prueba los 4 empezando por el sorteado). Verde hoy
    /// sobre 4 seeds × 1600 chunks — el fallback no tuvo que activarse en esa muestra, así que la
    /// divergencia sigue sin observarse en juego — pero el hueco de diseño sigue ahí: si algún día
    /// una seed/chunk sí fuerza el fallback, este test lo atrapa aquí y no en un jugador atrapado.
    #[test]
    fn the_layout_door_side_matches_the_grid_carves_resolved_side() {
        use crate::world::grid_gen::{
            carve_into_grid, generate_chunk_layer, LayerGrid, LAYER_PROFILES, ROOM_CELLS,
        };

        fn resolved_door_side(grid: &LayerGrid, plan: &RoomPlan) -> Option<u8> {
            let (x0, z0) = plan.cell_origin();
            let mid = ROOM_CELLS / 2;
            let probes: [(i32, i32); 4] = [
                (x0 as i32 + mid as i32, z0 as i32 - 1),
                (x0 as i32 + mid as i32, (z0 + ROOM_CELLS) as i32),
                (x0 as i32 - 1, z0 as i32 + mid as i32),
                ((x0 + ROOM_CELLS) as i32, z0 as i32 + mid as i32),
            ];
            for (side, (x, z)) in probes.iter().enumerate() {
                if LayerGrid::in_bounds(*x, *z) {
                    let (ux, uz) = (*x as usize, *z as usize);
                    if grid.get(ux, uz).is_walkable() {
                        return Some(side as u8);
                    }
                }
            }
            None
        }

        let mut checked = 0;
        for seed in [42u64, 7778, 1, 9_999_999] {
            for cx in 0..40 {
                for cz in 0..40 {
                    let Some(plan) = room_in_chunk(seed, cx, cz, 0) else {
                        continue;
                    };
                    let rules = &LAYER_PROFILES[0];
                    let out = generate_chunk_layer(rules, seed, (cx, cz), 0, &[]);
                    let mut grid = out.grid;
                    carve_into_grid(&mut grid, &plan, rules.ceiling_open);

                    let resolved = resolved_door_side(&grid, &plan).unwrap_or_else(|| {
                        panic!("seed {seed} ({cx},{cz}): ninguna de las 4 aberturas encontrada")
                    });

                    let mut layout = solid_layout();
                    carve_into_layout(&mut layout, &plan);
                    let (lx0, lz0) = (plan.tile_x, plan.tile_z);
                    let lmid = ROOM_TILES / 2;
                    let edges = [
                        layout.edge_h(lx0 + lmid, lz0),
                        layout.edge_h(lx0 + lmid, lz0 + ROOM_TILES),
                        layout.edge_v(lx0, lz0 + lmid),
                        layout.edge_v(lx0 + ROOM_TILES, lz0 + lmid),
                    ];
                    let layout_door_side =
                        edges.iter().position(|&e| e == EDGE_KIND_DOOR).unwrap() as u8;

                    checked += 1;
                    assert_eq!(
                        layout_door_side, resolved,
                        "seed {seed} ({cx},{cz}): colisión abre el lado {layout_door_side} pero \
                         el render resolvió el lado {resolved} (plan sorteó {})",
                        plan.door_side
                    );
                }
            }
        }
        assert!(checked > 0, "ninguna habitación encontrada en el barrido");
    }
}
