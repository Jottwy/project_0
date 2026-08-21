//! ADR-083 enmienda 1 — la MITAD DE COLISIÓN del tallado de la sala autorada.
//!
//! La otra mitad, y el emplazamiento compartido, viven en `grid_gen::authored_rooms`. Están
//! partidas por la misma razón que las de la habitación construible (ver `build_room_layout`): el
//! mundo existe en dos representaciones y `grid_gen` no puede importar `world/`.
//!
//! | representación | celda | quién la usa |
//! |---|---|---|
//! | `ChunkLayoutV1` (aquí) | 5 m | la colisión del JUGADOR (`Level0Collision::resolve_move`) |
//! | `LayerGrid` (allí) | 2,5 m | lo que se VE y el robapieles |
//!
//! **Tallar una sola no vale**: solo el render da paredes que ves y atraviesas; solo la colisión,
//! paredes invisibles que te frenan.
//!
//! Aquí TODO cae en tile redondo, y no por suerte: el footprint se sortea en celdas pares y el borde
//! de la reserva mide 2 celdas = 5 m = una celda de esta rejilla. Por eso el margen y el anillo, que
//! allí son dos cosas distintas, aquí son **un solo anillo de celdas macizas** de una celda de
//! grosor. Es la misma geometría vista con la mitad de resolución, no una aproximación.

use super::chunk::{
    ChunkLayoutV1, CELL_BLOCKED, CELL_PILLAR, CELL_PIT, CELL_WALKABLE, CELL_WALL, EDGE_KIND_DOOR,
    EDGE_KIND_OPEN, EDGE_KIND_WALL,
};
use super::grid_gen::AuthoredRoomPlan;

/// Talla la sala autorada en el layout de colisión: borde macizo, interior caminable y el vano de la
/// puerta abierto por el mismo sitio que en la rejilla fina.
pub fn carve_authored_into_layout(layout: &mut ChunkLayoutV1, plan: &AuthoredRoomPlan) {
    let grid = layout.grid_size as usize;

    // Todo en TILES de 5 m, que aquí son celdas. El footprint sale de celdas de 2,5 m pares, así que
    // la división entre 2 es exacta. `div_euclid` porque con una sala anclada en el chunk vecino
    // (ADR-084) el origen es negativo, y `/` truncaría hacia cero: −1 y 1 caerían en el mismo tile.
    let (tx0, tz0) = (plan.cell_x.div_euclid(2), plan.cell_z.div_euclid(2));
    let (tw, th) = ((plan.cells_x / 2) as i32, (plan.cells_z / 2) as i32);
    let (tx1, tz1) = (tx0 + tw, tz0 + th);

    // El borde de la reserva: una celda de esta rejilla por lado.
    let (rx0, rz0) = (tx0 - 1, tz0 - 1);
    let (rx1, rz1) = (tx1 + 1, tz1 + 1);

    // Lo que asoma por ESTE layout. Antes se descartaba la sala entera si no cabía; ahora se talla
    // el trozo, porque una sala multi-chunk no cabe entera en ninguno de los suyos por definición.
    // Los rects sin recortar se conservan: son los que deciden qué es borde y qué es interior, y
    // decidirlo contra el recorte le pondría a la sala una pared falsa en la frontera del chunk.
    let clip = |a: i32, b: i32| {
        let (lo, hi) = (a.max(0), b.clamp(0, grid as i32));
        (lo.min(hi) as usize)..(hi as usize)
    };

    // 1. El borde macizo. Va ANTES del interior para que, si alguna vez se tocaran, gane el interior
    //    y la sala no nazca sellada por su propio tallado — mismo orden que en la rejilla fina.
    for x in clip(rx0, rx1) {
        for z in clip(rz0, rz1) {
            let (ix, iz) = (x as i32, z as i32);
            let inside = ix >= tx0 && ix < tx1 && iz >= tz0 && iz < tz1;
            if inside {
                continue;
            }
            if let Some(idx) = layout.cell_index(x, z) {
                let cell = &mut layout.cells[idx];
                *cell &= !CELL_WALKABLE;
                *cell |= CELL_WALL;
            }
        }
    }

    // 2. El interior caminable. Se limpia de `CELL_WALL | CELL_PILLAR | CELL_BLOCKED | CELL_PIT`
    //    además de marcarse caminable: `is_cell_walkable` exige las dos cosas, y una columna
    //    heredada de la plantilla dejaría un obstáculo INVISIBLE en mitad de la sala — invisible
    //    porque el render sale de la otra representación, donde el tallado ya la borró.
    for x in clip(tx0, tx1) {
        for z in clip(tz0, tz1) {
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

    // 3. Aristas INTERIORES abiertas: la sala es una pieza, no una retícula de casillas tabicadas.
    //    El interior real lo pone el prefab (columnas, bloques, entreplantas), y ESO no está en esta
    //    representación — punto 9 del ADR, aceptado y anotado.
    for z in tz0..tz1 {
        for bx in (tx0 + 1)..tx1 {
            set_edge_v_at(layout, bx, z, EDGE_KIND_OPEN);
        }
    }
    for x in tx0..tx1 {
        for bz in (tz0 + 1)..tz1 {
            set_edge_h_at(layout, x, bz, EDGE_KIND_OPEN);
        }
    }

    // 4. Perímetro del FOOTPRINT a pared: es la pared que trae el prefab.
    for x in tx0..tx1 {
        set_edge_h_at(layout, x, tz0, EDGE_KIND_WALL);
        set_edge_h_at(layout, x, tz1, EDGE_KIND_WALL);
    }
    for z in tz0..tz1 {
        set_edge_v_at(layout, tx0, z, EDGE_KIND_WALL);
        set_edge_v_at(layout, tx1, z, EDGE_KIND_WALL);
    }

    // 5. El vano. Tres cosas tienen que pasar a la vez o la sala queda incomunicada para el jugador
    //    aunque el render enseñe un pasillo abierto: la arista del perímetro se abre, la celda de
    //    borde que hay detrás vuelve a ser caminable —es el túnel— y la arista de salida de esa
    //    celda se abre también.
    //
    //    El tile de la puerta sale de `door_tile_offset`, la MISMA cuenta que eligió la pareja de
    //    celdas allí. Recalcularlo con otro criterio aquí es exactamente cómo se acaba con una
    //    puerta que se ve en un sitio y se cruza en otro.
    for (side, tile) in plan.doorways() {
        let off = tile as i32;
        match side {
            0 => open_door(layout, (tx0 + off, tz0 - 1), (tx0 + off, tz0), true), // sur (−z)
            1 => open_door(layout, (tx0 + off, tz1), (tx0 + off, tz1 + 1), true), // norte (+z)
            2 => open_door(layout, (tx0 - 1, tz0 + off), (tx0, tz0 + off), false), // oeste (−x)
            _ => open_door(layout, (tx1, tz0 + off), (tx1 + 1, tz0 + off), false), // este (+x)
        }
    }
}

/// Las dos únicas puertas por las que una coordenada con signo llega a `set_edge_*`. Fuera del
/// layout no escriben: el trozo de sala que caiga en el chunk vecino es problema de ESE chunk, que
/// evalúa el mismo plan y talla su parte.
///
/// El índice de una arista va de 0 a `grid_size` INCLUSIVE —hay una arista más que celdas por eje—,
/// mientras que el de la celda que la acompaña va de 0 a `grid_size` exclusive.
fn set_edge_v_at(layout: &mut ChunkLayoutV1, bx: i32, z: i32, kind: u8) {
    let g = layout.grid_size as i32;
    if (0..=g).contains(&bx) && (0..g).contains(&z) {
        layout.set_edge_v(bx as usize, z as usize, kind);
    }
}

fn set_edge_h_at(layout: &mut ChunkLayoutV1, x: i32, bz: i32, kind: u8) {
    let g = layout.grid_size as i32;
    if (0..g).contains(&x) && (0..=g).contains(&bz) {
        layout.set_edge_h(x as usize, bz as usize, kind);
    }
}

/// Abre el vano: la celda del túnel a caminable, y las dos aristas que la flanquean.
///
/// `tunnel` es la celda de borde que se atraviesa; `outer` la de más allá, cuya arista compartida
/// hay que abrir para salir al laberinto. `horizontal` distingue si el vano cruza una arista
/// horizontal (lados sur/norte) o vertical (oeste/este).
fn open_door(layout: &mut ChunkLayoutV1, tunnel: (i32, i32), outer: (i32, i32), horizontal: bool) {
    let g = layout.grid_size as i32;
    if (0..g).contains(&tunnel.0) && (0..g).contains(&tunnel.1) {
        if let Some(idx) = layout.cell_index(tunnel.0 as usize, tunnel.1 as usize) {
            let cell = &mut layout.cells[idx];
            *cell &= !(CELL_WALL | CELL_PILLAR | CELL_BLOCKED | CELL_PIT);
            *cell |= CELL_WALKABLE;
        }
    }

    // Las dos aristas del túnel, en orden de recorrido: la del perímetro de la sala y la de salida.
    // `set_edge_*` toma la arista por su lado de MENOR coordenada, así que ambas se nombran con el
    // mínimo de cada pareja.
    let (a, b) = if horizontal {
        (tunnel.1.min(outer.1), tunnel.1.max(outer.1))
    } else {
        (tunnel.0.min(outer.0), tunnel.0.max(outer.0))
    };
    if horizontal {
        set_edge_h_at(layout, tunnel.0, a, EDGE_KIND_DOOR);
        set_edge_h_at(layout, tunnel.0, b, EDGE_KIND_DOOR);
    } else {
        set_edge_v_at(layout, a, tunnel.1, EDGE_KIND_DOOR);
        set_edge_v_at(layout, b, tunnel.1, EDGE_KIND_DOOR);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Una sala de 4 x 4 tiles con origen en la celda 8, que es un emplazamiento admisible real
    /// (footprint par, reserva dentro de [1, 18]).
    fn plan(door_side: u8) -> AuthoredRoomPlan {
        AuthoredRoomPlan {
            entry: 0,
            quarter: 0,
            cell_x: 8,
            cell_z: 8,
            cells_x: 8,
            cells_z: 8,
            top_layer: 0,
            doors: {
                let mut d = [(0u8, 0u8); crate::world::grid_gen::MAX_DOORWAYS];
                d[0] = (door_side, 2);
                d
            },
            door_count: 1,
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
        carve_authored_into_layout(&mut layout, &plan(1));

        for x in 4..8 {
            for z in 4..8 {
                assert!(
                    layout.is_cell_walkable(x, z),
                    "celda ({x},{z}) no caminable: la colision no conoce la sala"
                );
            }
        }
    }

    /// Una columna heredada de la plantilla dentro de la sala seria un obstaculo INVISIBLE: el
    /// render sale de la otra representacion, donde el tallado ya la borro.
    #[test]
    fn the_interior_is_cleared_of_pillars_and_blockers() {
        let mut layout = solid_layout();
        let idx = layout.cell_index(5, 6).unwrap();
        layout.cells[idx] = CELL_PILLAR | CELL_BLOCKED | CELL_PIT;

        carve_authored_into_layout(&mut layout, &plan(1));

        assert!(layout.is_cell_walkable(5, 6));
    }

    /// El margen macizo, visto desde la colision: el anillo de celdas alrededor del footprint no es
    /// caminable por ningun lado salvo por el tile del vano.
    #[test]
    fn the_border_ring_is_not_walkable_except_the_doorway() {
        for door_side in 0..4u8 {
            let p = plan(door_side);
            let mut layout = solid_layout();
            // Se parte de TODO caminable para que el test mida lo que el tallado CIERRA, no lo que
            // se encontro cerrado de antes.
            for c in layout.cells.iter_mut() {
                *c = CELL_WALKABLE;
            }
            carve_authored_into_layout(&mut layout, &p);

            let off = p.doors[0].1 as usize;
            // Los planes de estos tests caen enteros dentro del chunk, así que el paso a índice es
            // directo. Un plan multi-chunk (ADR-084) no lo sería — ver `clip`.
            let (tx0, tz0) = ((p.cell_x / 2) as usize, (p.cell_z / 2) as usize);
            let (tx1, tz1) = (tx0 + p.cells_x / 2, tz0 + p.cells_z / 2);
            let doorway = match door_side {
                0 => (tx0 + off, tz0 - 1),
                1 => (tx0 + off, tz1),
                2 => (tx0 - 1, tz0 + off),
                _ => (tx1, tz0 + off),
            };

            for x in (tx0 - 1)..(tx1 + 1) {
                for z in (tz0 - 1)..(tz1 + 1) {
                    let inside = x >= tx0 && x < tx1 && z >= tz0 && z < tz1;
                    if inside || (x, z) == doorway {
                        continue;
                    }
                    assert!(
                        !layout.is_cell_walkable(x, z),
                        "lado {door_side}: hueco en el borde en ({x},{z})"
                    );
                }
            }
        }
    }

    /// LA INVARIANTE FUERTE DE ESTA MITAD: por el vano se pasa. Si la arista sigue siendo pared, el
    /// render ensena un pasillo abierto y el jugador rebota contra nada.
    #[test]
    fn the_doorway_is_crossable_from_outside_on_every_side() {
        for door_side in 0..4u8 {
            let p = plan(door_side);
            let mut layout = solid_layout();
            carve_authored_into_layout(&mut layout, &p);

            let off = p.doors[0].1 as usize;
            // Los planes de estos tests caen enteros dentro del chunk, así que el paso a índice es
            // directo. Un plan multi-chunk (ADR-084) no lo sería — ver `clip`.
            let (tx0, tz0) = ((p.cell_x / 2) as usize, (p.cell_z / 2) as usize);
            let (tx1, tz1) = (tx0 + p.cells_x / 2, tz0 + p.cells_z / 2);

            // (celda del tunel, arista del perimetro, arista de salida) por lado.
            let (tunnel, inner_edge, outer_edge, horizontal) = match door_side {
                0 => ((tx0 + off, tz0 - 1), tz0, tz0 - 1, true),
                1 => ((tx0 + off, tz1), tz1, tz1 + 1, true),
                2 => ((tx0 - 1, tz0 + off), tx0, tx0 - 1, false),
                _ => ((tx1, tz0 + off), tx1, tx1 + 1, false),
            };

            assert!(
                layout.is_cell_walkable(tunnel.0, tunnel.1),
                "lado {door_side}: la celda del tunel no es caminable"
            );
            if horizontal {
                assert_eq!(
                    layout.edge_h(tunnel.0, inner_edge),
                    EDGE_KIND_DOOR,
                    "lado {door_side}: la arista del perimetro sigue cerrada"
                );
                assert_eq!(
                    layout.edge_h(tunnel.0, outer_edge),
                    EDGE_KIND_DOOR,
                    "lado {door_side}: la arista de salida sigue cerrada"
                );
            } else {
                assert_eq!(
                    layout.edge_v(inner_edge, tunnel.1),
                    EDGE_KIND_DOOR,
                    "lado {door_side}: la arista del perimetro sigue cerrada"
                );
                assert_eq!(
                    layout.edge_v(outer_edge, tunnel.1),
                    EDGE_KIND_DOOR,
                    "lado {door_side}: la arista de salida sigue cerrada"
                );
            }
        }
    }

    /// La sala es UNA pieza, no una reticula de casillas tabicadas.
    #[test]
    fn the_interior_edges_are_open() {
        let mut layout = solid_layout();
        let p = plan(1);
        carve_authored_into_layout(&mut layout, &p);

        let (tx0, tz0) = ((p.cell_x / 2) as usize, (p.cell_z / 2) as usize);
        let (tx1, tz1) = (tx0 + p.cells_x / 2, tz0 + p.cells_z / 2);
        for z in tz0..tz1 {
            for bx in (tx0 + 1)..tx1 {
                assert_eq!(layout.edge_v(bx, z), EDGE_KIND_OPEN, "tabique en x={bx}");
            }
        }
        for x in tx0..tx1 {
            for bz in (tz0 + 1)..tz1 {
                assert_eq!(layout.edge_h(x, bz), EDGE_KIND_OPEN, "tabique en z={bz}");
            }
        }
    }

    /// El perimetro de la sala es pared por todas partes menos por el vano: es la pared que trae el
    /// prefab, y sin ella se entraria por cualquier lado.
    #[test]
    fn the_footprint_perimeter_is_wall_except_at_the_door() {
        let p = plan(1);
        let mut layout = solid_layout();
        carve_authored_into_layout(&mut layout, &p);

        let off = p.doors[0].1 as usize;
        let (tx0, tz0) = ((p.cell_x / 2) as usize, (p.cell_z / 2) as usize);
        let (tx1, tz1) = (tx0 + p.cells_x / 2, tz0 + p.cells_z / 2);

        for x in tx0..tx1 {
            assert_eq!(layout.edge_h(x, tz0), EDGE_KIND_WALL);
            let expected = if x == tx0 + off {
                EDGE_KIND_DOOR
            } else {
                EDGE_KIND_WALL
            };
            assert_eq!(layout.edge_h(x, tz1), expected, "lado norte en x={x}");
        }
        for z in tz0..tz1 {
            assert_eq!(layout.edge_v(tx0, z), EDGE_KIND_WALL);
            assert_eq!(layout.edge_v(tx1, z), EDGE_KIND_WALL);
        }
    }
}
