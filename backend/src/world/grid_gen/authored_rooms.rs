//! ADR-083 enmienda 1 — LA SALA AUTORADA, reservada y tallada por el servidor.
//!
//! Una sala hecha a mano en Unity no encaja en un hueco que el generador haya dejado por su cuenta:
//! los tamaños no coinciden nunca. Lo que se hace aquí es al revés — **la reserva la dicta la
//! sala**. El backend elige una del manifiesto, reserva sitio de sobra, vacía el interior, lo cierra
//! con un anillo blindado y excava un pasillo hasta engancharlo con el laberinto.
//!
//! Reparto por eje de la reserva:
//!
//! ```text
//! [anillo 1][margen 1][ footprint de la sala ][margen 1][anillo 1]
//! ```
//!
//! **El anillo va FUERA y el margen DENTRO**, y ese orden no es estético. El margen es `Wall`
//! genérico, y `repair_connectivity` carva `Wall` libremente para reconectar bolsillos: con el
//! margen por fuera, la reparación podría abrir un túnel muerto dentro de él. El anillo es
//! `SealedWall`, el único tipo excluido a mano de ese BFS, así que puesto por fuera nadie llega
//! siquiera a tocar el margen.
//!
//! **El margen es MACIZO, no aire.** Es lo que garantiza que no queden huecos por los que caerse:
//! no hay nada que sellar después porque nunca se abre. Y da separación entre el laberinto y las
//! paredes de la sala, que es lo que evita que un pasillo procedural muera pegado a un muro
//! autorado.
//!
//! El emplazamiento es puro y determinista, igual que todo lo demás de `grid_gen`: dos peers con la
//! misma seed reservan el mismo sitio sin hablarse. Lo que SÍ viaja por el wire es qué sala y con
//! qué giro (ADR-083 punto 2), porque el sorteo usa `StdRng` —ChaCha— y eso no se replica en C#.

use rand::rngs::StdRng;
use rand::seq::SliceRandom;
use rand::{Rng, SeedableRng};

use super::build_rooms::{carve_tunnel_fixed, carve_tunnel_outward, RoomPlan};
use super::generator::mix;
use super::room_manifest::{ManifestRoom, RoomManifest, MAX_DOORWAYS};
use super::{Cell, CellType, LayerGrid, CHUNK_CELLS};

/// Grosor del anillo blindado, en celdas de 2,5 m.
const RING_CELLS: usize = 1;

/// Grosor del margen macizo, en celdas de 2,5 m.
const MARGIN_CELLS: usize = 1;

/// Lo que se va por cada lado del footprint: 2 celdas = 5 m = UN TILE EXACTO. Por eje se gasta el
/// doble, así que una sala de 50 m se reserva en 60 m — la separación que se pidió.
///
/// QUE SEA UN NÚMERO ENTERO DE TILES NO ES COSMÉTICO. La otra representación del mundo, el
/// `ChunkLayoutV1` de colisión del jugador, tiene celdas de 5 m: un borde de tile y medio dejaría la
/// frontera de la reserva partiendo un tile por la mitad, y ese medio tile no se puede representar
/// allí. Marcarlo bloqueado inventa 2,5 m de pared invisible; dejarlo libre mete al jugador andando
/// dentro del anillo, que el render dibuja macizo. Es el fallo exacto contra el que avisa
/// `build_room_layout`: paredes que se ven y se atraviesan.
const BORDER_CELLS: usize = RING_CELLS + MARGIN_CELLS;

/// Primera y última celda utilizable de un chunk. Las filas/columnas 0 y `CHUNK_CELLS - 1` son del
/// cosido de bordes (`stitching`) y no se tocan: sellarlas partiría el mundo por ese borde.
const USABLE_LO: usize = 1;
const USABLE_HI: usize = CHUNK_CELLS - 2; // 18

/// Footprint máximo en CELDAS que admite un chunk, de donde sale el cap de **7 × 7 tiles** (35 m).
/// Es aritmética, no gusto: 18 celdas útiles menos las 4 del borde.
///
/// ADR-083 enmienda 1 lo fijó en 6 × 6 partiendo de un borde de 3 celdas; al alinear el borde a
/// tile (2 celdas) el cap sube solo. Ver `BORDER_CELLS`.
pub const MAX_FOOTPRINT_CELLS: usize = (USABLE_HI - USABLE_LO + 1) - 2 * BORDER_CELLS; // 14

/// Probabilidad de que un chunk aloje una sala autorada.
///
/// El espaciado medio es `lado_de_chunk / √p`: con 0,01 y chunks de 50 m sale **una cada 500 m**,
/// que es la cadencia pedida — cinco veces más rara que la habitación construible de ADR-081
/// (`ROOM_CHANCE = 0,05`, una cada ~220 m). Son sitios especiales; verlas cada dos pasos las mata.
const AUTHORED_CHANCE: f64 = 0.01;

/// Constante de dominio propia. Disjunta de `ROOM_SALT` (habitación construible), de la de
/// `aperture_pos` y de la de `subregion_seed`, para que dos sorteos no se correlacionen.
const AUTHORED_SALT: u64 = 0xA57_0AD3_0007_0083;

/// Solo en la capa 0, misma razón que la habitación construible: las capas superiores son
/// verticalidad decorativa (ADR-026 sigue bloqueado) y una sala flotando en una de ellas sería un
/// sitio al que no se llega de forma fiable.
const AUTHORED_LAYER: u8 = 0;

/// Hasta dónde se excava buscando el laberinto antes de rendirse.
const TUNNEL_LIMIT: usize = CHUNK_CELLS / 2;

/// Qué sala va en este chunk, dónde y con qué giro.
///
/// Las coordenadas son las del FOOTPRINT de la sala, no las de la reserva: es lo que el cliente
/// necesita para poner el prefab, y la reserva se re-deriva sumando `BORDER_CELLS`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AuthoredRoomPlan {
    /// Índice en `RoomPool.rooms`. ES lo que viaja por el wire.
    pub entry: u16,
    /// Giro en cuartos de vuelta: 0 = 0°, 1 = 90°, 2 = 180°, 3 = 270°.
    pub quarter: u8,
    /// Celda de la esquina de menor x del footprint.
    pub cell_x: usize,
    /// Celda de la esquina de menor z del footprint.
    pub cell_z: usize,
    /// Footprint ya girado, en celdas.
    pub cells_x: usize,
    pub cells_z: usize,
    /// Las aberturas de la sala, `(lado, tile)`. Solo las `door_count` primeras son válidas.
    ///
    /// Array fijo y no `Vec` a propósito: esto se re-deriva en cada generación de chunk, y esa ruta
    /// la recorren la colisión del jugador, la caché del robapieles y el render. Un `Vec` metería
    /// una asignación en sitio caliente para transportar como mucho ocho parejas de bytes.
    pub doors: [(u8, u8); MAX_DOORWAYS],
    /// Cuántas entradas de `doors` valen. Nunca 0: una sala sin abertura no se coloca.
    pub door_count: u8,
}

impl AuthoredRoomPlan {
    /// Las aberturas válidas, `(lado, tile)`.
    pub fn doorways(&self) -> impl Iterator<Item = (u8, u8)> + '_ {
        self.doors.iter().copied().take(self.door_count as usize)
    }

    /// Rect de la RESERVA completa (anillo incluido), `(x0, z0, x1, z1)` con x1/z1 EXCLUSIVOS.
    pub fn reserve_rect(&self) -> (usize, usize, usize, usize) {
        (
            self.cell_x - BORDER_CELLS,
            self.cell_z - BORDER_CELLS,
            self.cell_x + self.cells_x + BORDER_CELLS,
            self.cell_z + self.cells_z + BORDER_CELLS,
        )
    }

    /// Tile de 5 m de la esquina del footprint. El footprint siempre cae en frontera de tile porque
    /// el origen se sortea en celdas pares — ver `plan_authored_room`.
    pub fn tile_origin(&self) -> (u8, u8) {
        ((self.cell_x / 2) as u8, (self.cell_z / 2) as u8)
    }
}

/// ¿Cabe esta sala con este giro en un chunk?
fn fits(room: &ManifestRoom, quarter: u8) -> Option<(usize, usize)> {
    let (w, h) = room.footprint_cells(quarter);
    if w == 0 || h == 0 || w > MAX_FOOTPRINT_CELLS || h > MAX_FOOTPRINT_CELLS {
        return None;
    }
    Some((w, h))
}

/// Elige sala, giro y sitio para este chunk, o `None`.
///
/// **PURA Y MEMOIZABLE**, igual que `room_in_chunk`: mismo input → mismo output en todo peer y en
/// cualquier momento, sin mirar el grid. Eso es lo que permite que las DOS representaciones del
/// mundo —la rejilla fina de `grid_gen` y el `ChunkLayoutV1` de colisión, que viven en módulos que
/// no se pueden importar entre sí— tallen la MISMA sala sin ponerse de acuerdo.
///
/// NO comprueba que la puerta llegue al laberinto, y no hace falta: el túnel siempre rompe el
/// anillo, y `repair_connectivity` —que corre después con las celdas talladas como `protected`—
/// reconecta su punta con el resto del chunk. Es exactamente el mecanismo por el que la habitación
/// construible nunca nace incomunicada. Sondearlo aquí obligaría a pasar el grid, y con él a
/// regenerar el chunk entero en el lado de colisión solo para saber dónde va una sala.
///
/// `build_room` es la habitación construible de ADR-081 en este mismo chunk, si la hay. **Manda
/// ella**: es una regla de juego validada, y la sala autorada es decorado. Si las reservas se
/// solapan, aquí no se coloca nada.
pub fn plan_authored_room(
    manifest: &RoomManifest,
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
    build_room: Option<&RoomPlan>,
) -> Option<AuthoredRoomPlan> {
    if layer != AUTHORED_LAYER || manifest.rooms.is_empty() {
        return None;
    }

    let mut s = world_seed ^ AUTHORED_SALT;
    s = mix(s, cx as i64 as u64);
    s = mix(s, cz as i64 as u64);
    let mut rng = StdRng::seed_from_u64(s);

    if !rng.gen_bool(AUTHORED_CHANCE) {
        return None;
    }

    // Candidatas: (sala, giro) que quepan. El orden —sala por fuera, giro por dentro— es CONTRATO,
    // igual que en el cliente: la elección es un sorteo sobre esta lista, así que reordenarla
    // cambiaría qué sala sale en cada sitio del mundo.
    let mut candidates: Vec<(usize, u8, usize, usize)> = Vec::new();
    for (i, room) in manifest.rooms.iter().enumerate() {
        for quarter in 0..4u8 {
            if let Some((w, h)) = fits(room, quarter) {
                candidates.push((i, quarter, w, h));
            }
        }
    }
    if candidates.is_empty() {
        return None;
    }
    candidates.shuffle(&mut rng);

    for (idx, quarter, w, h) in candidates {
        let room = &manifest.rooms[idx];

        // Origen de la RESERVA, y desde él el del footprint. Se sortea PAR para que el footprint
        // caiga en frontera de tile de 5 m: el prefab se coloca en tiles, y medio tile de desfase
        // dejaría la sala a caballo entre dos.
        let Some(cell_x) = draw_origin(&mut rng, w) else {
            continue;
        };
        let Some(cell_z) = draw_origin(&mut rng, h) else {
            continue;
        };

        // Las aberturas, ya resueltas para ESTE giro. Se copian a un array fijo para que el plan
        // siga siendo `Copy` y no cueste una asignación por chunk generado.
        let mut doors = [(0u8, 0u8); MAX_DOORWAYS];
        for (slot, door) in doors.iter_mut().zip(room.doorways.iter()) {
            *slot = (
                door.side_by_quarter[quarter as usize],
                door.tile_by_quarter[quarter as usize],
            );
        }

        let plan = AuthoredRoomPlan {
            entry: room.index,
            quarter,
            cell_x,
            cell_z,
            cells_x: w,
            cells_z: h,
            doors,
            door_count: room.doorways.len().min(MAX_DOORWAYS) as u8,
        };

        if let Some(build) = build_room {
            if overlaps_build_room(&plan, build) {
                continue;
            }
        }
        return Some(plan);
    }

    None
}

/// Sortea el origen del FOOTPRINT en un eje, en celda PAR, o `None` si no cabe.
///
/// La reserva ocupa `size + 2 · BORDER_CELLS` y tiene que caber entera en `[USABLE_LO, USABLE_HI]`.
/// El origen del footprint queda `BORDER_CELLS` más adentro. Se sortea en unidades de tile y se
/// multiplica por 2, que es la forma barata de garantizar paridad sin descartar tiradas.
fn draw_origin(rng: &mut StdRng, size: usize) -> Option<usize> {
    let reserve = size + 2 * BORDER_CELLS;
    if reserve > USABLE_HI - USABLE_LO + 1 {
        return None;
    }
    // Primer y último origen de FOOTPRINT admisibles.
    let lo = USABLE_LO + BORDER_CELLS;
    let hi = USABLE_HI + 1 - BORDER_CELLS - size;
    if hi < lo {
        return None;
    }
    // Solo orígenes pares dentro de [lo, hi].
    let first_even = lo.div_ceil(2) * 2;
    if first_even > hi {
        return None;
    }
    let slots = (hi - first_even) / 2 + 1;
    Some(first_even + 2 * rng.gen_range(0..slots))
}

/// ¿Se pisan la reserva de la sala autorada y la de la habitación construible (su anillo incluido)?
fn overlaps_build_room(plan: &AuthoredRoomPlan, build: &RoomPlan) -> bool {
    let (ax0, az0, ax1, az1) = plan.reserve_rect();
    let (bx, bz) = build.cell_origin();
    // El anillo de la construible es la corona de 1 celda alrededor de su footprint.
    let bx0 = bx.saturating_sub(1);
    let bz0 = bz.saturating_sub(1);
    let bx1 = bx + super::build_rooms::ROOM_CELLS + 1;
    let bz1 = bz + super::build_rooms::ROOM_CELLS + 1;

    ax0 < bx1 && bx0 < ax1 && az0 < bz1 && bz0 < az1
}

/// Las DOS celdas de partida del túnel (la pareja que forma un tile completo, la primera fuera del
/// footprint y a mitad del lado de la puerta) y la dirección hacia fuera.
///
/// El vano mide UN TILE, no una celda, y eso es requisito de coherencia, no estética: el margen que
/// hay que cruzar es de un tile, y la colisión del jugador razona en tiles de 5 m. Un túnel de media
/// anchura de tile dejaría al jugador cruzando 2,5 m de lo que el render dibuja como muro macizo.
///
/// El desplazamiento hasta el centro se redondea a PAR (`& !1`) para que la pareja de celdas caiga
/// dentro de un mismo tile. Sin eso, un footprint cuya mitad sea impar partiría el vano entre dos
/// tiles y volvería el problema por la puerta de atrás.
fn door_starts(plan: &AuthoredRoomPlan, door: (u8, u8)) -> ([(i32, i32); 2], (i32, i32)) {
    let (side, tile) = door;
    let (x0, z0) = (plan.cell_x as i32, plan.cell_z as i32);
    let (x1, z1) = (x0 + plan.cells_x as i32, z0 + plan.cells_z as i32);
    // El tile del vano, en celdas. Sale del manifiesto —la posición real del boquete del prefab, ya
    // rotada— y no de una cuenta local: derivarlo aquí solo acertaría con giro 0.
    let off = tile as i32 * 2;
    let (mx, mz) = (x0 + off, z0 + off);

    match side {
        0 => ([(mx, z0 - 1), (mx + 1, z0 - 1)], (0, -1)), // sur (−z)
        1 => ([(mx, z1), (mx + 1, z1)], (0, 1)),          // norte (+z)
        2 => ([(x0 - 1, mz), (x0 - 1, mz + 1)], (-1, 0)), // oeste (−x)
        _ => ([(x1, mz), (x1, mz + 1)], (1, 0)),          // este (+x)
    }
}

/// Talla la sala en la `LayerGrid` y devuelve las celdas TRANSITABLES creadas, para pasárselas a
/// `repair_connectivity` como `protected`.
///
/// Orden deliberado —margen, luego anillo, luego interior, luego puerta— para que cada paso pise al
/// anterior donde se toquen y la sala no pueda nacer sellada por su propio tallado.
pub fn carve_authored_into_grid(
    grid: &mut LayerGrid,
    plan: &AuthoredRoomPlan,
    ceiling: u8,
) -> Vec<(usize, usize)> {
    let (rx0, rz0, rx1, rz1) = plan.reserve_rect();
    let (fx0, fz0) = (plan.cell_x, plan.cell_z);
    let (fx1, fz1) = (fx0 + plan.cells_x, fz0 + plan.cells_z);

    // 1. La reserva entera a macizo genérico: eso deja el MARGEN hecho de una vez.
    for x in rx0..rx1 {
        for z in rz0..rz1 {
            grid.set(x, z, Cell::new(CellType::Wall, 0, 0));
        }
    }

    // 2. El anillo, el borde exterior de la reserva, a `SealedWall`. Es lo que impide que
    //    `repair_connectivity` entre a reconectar bolsillos por aquí y agujeree la sala.
    for x in rx0..rx1 {
        for z in rz0..rz1 {
            let on_ring = x < rx0 + RING_CELLS
                || x >= rx1 - RING_CELLS
                || z < rz0 + RING_CELLS
                || z >= rz1 - RING_CELLS;
            if on_ring {
                grid.set(x, z, Cell::new(CellType::SealedWall, 0, 0));
            }
        }
    }

    // 3. Interior hueco. `Open` y no `Corridor`: es una sala, y el render de sala es el que no mete
    //    la geometría estrecha de pasillo dentro.
    let mut carved = Vec::with_capacity(plan.cells_x * plan.cells_z + TUNNEL_LIMIT);
    for x in fx0..fx1 {
        for z in fz0..fz1 {
            grid.set(x, z, Cell::new(CellType::Open, ceiling, 0));
            carved.push((x, z));
        }
    }

    // 4. Las puertas — TODAS, una por abertura del prefab. Con una sola, las demás dan contra el
    //    margen macizo: se ve el vano y detrás un bloque cerrado.
    //
    //    Cada vano mide un tile de ancho: se excava la primera línea hasta que engancha y la segunda
    //    copia su longitud, para que no salga dentado.
    //
    //    Si una línea NO engancha (topó con la costura), su túnel se queda a medias y esa parte
    //    entra en el chunk como componente aparte. No es un fallo: `repair_connectivity` corre justo
    //    después con estas celdas protegidas y le tiende un pasillo desde el componente grande. Es
    //    el mismo mecanismo por el que la habitación construible nunca queda aislada.
    //
    //    Dos boquetes del prefab que caigan en el MISMO `(lado, tile)` describen el mismo hueco:
    //    `door_starts` les da idénticas celdas de partida y excavarlos dos veces repetiría el
    //    tallado y duplicaría esas celdas en `carved`. Con `MAX_DOORWAYS = 8`, un barrido cuadrado
    //    sobre un array en pila sale más barato que montar un set en una ruta que se recorre en
    //    cada generación de chunk.
    let mut dug_doors = [(0u8, 0u8); MAX_DOORWAYS];
    let mut dug_count = 0usize;
    for door in plan.doorways() {
        if dug_doors[..dug_count].contains(&door) {
            continue;
        }
        dug_doors[dug_count] = door;
        dug_count += 1;

        let (starts, dir) = door_starts(plan, door);
        let dug = carve_tunnel_outward(grid, ceiling, starts[0], dir, TUNNEL_LIMIT, &mut carved)
            .unwrap_or(BORDER_CELLS);
        carve_tunnel_fixed(grid, ceiling, starts[1], dir, dug, &mut carved);
    }

    carved
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::grid_gen::ManifestDoorway;
    use crate::world::grid_gen::{generate_chunk_layer, repair_connectivity, LAYER_PROFILES};
    use std::collections::{HashSet, VecDeque};

    const SEEDS: [u64; 4] = [42, 7778, 1, 9_999_999];

    /// Manifiesto de prueba: una sala de 4 x 4 tiles (la que de verdad hay horneada hoy) y una de
    /// 10 x 10 (las otras dos del pool), que NO cabe y no debe salir jamas.
    fn manifest() -> RoomManifest {
        RoomManifest {
            digest: "test".into(),
            rooms: vec![
                ManifestRoom {
                    index: 0,
                    id: "room_0".into(),
                    tiles_x: 4,
                    tiles_z: 4,
                    doorways: vec![ManifestDoorway {
                        side_by_quarter: [1, 3, 0, 2],
                        tile_by_quarter: [2, 1, 1, 2],
                    }],
                },
                ManifestRoom {
                    index: 1,
                    id: "room_1".into(),
                    tiles_x: 10,
                    tiles_z: 10,
                    doorways: vec![ManifestDoorway {
                        side_by_quarter: [0, 2, 1, 3],
                        tile_by_quarter: [5, 4, 4, 5],
                    }],
                },
            ],
        }
    }

    /// Barre chunks hasta encontrar uno con sala, para no depender de que un chunk concreto gane un
    /// sorteo del 1 %.
    fn find_plan(m: &RoomManifest, seed: u64) -> Option<(i32, i32, AuthoredRoomPlan)> {
        for cx in -12..12 {
            for cz in -12..12 {
                if let Some(p) = plan_authored_room(m, seed, cx, cz, 0, None) {
                    return Some((cx, cz, p));
                }
            }
        }
        None
    }

    /// Lo que permite que las dos representaciones del mundo tallen la MISMA sala sin hablarse.
    #[test]
    fn placement_is_deterministic() {
        let m = manifest();
        for seed in SEEDS {
            for (cx, cz) in [(0, 0), (3, -7), (11, 11), (-9, 4)] {
                assert_eq!(
                    plan_authored_room(&m, seed, cx, cz, 0, None),
                    plan_authored_room(&m, seed, cx, cz, 0, None)
                );
            }
        }
    }

    /// Capas 1-3 son verticalidad decorativa: una sala alli seria un sitio al que no se llega.
    #[test]
    fn only_layer_zero() {
        let m = manifest();
        for seed in SEEDS {
            for cx in -12..12 {
                for cz in -12..12 {
                    for layer in 1..4u8 {
                        assert!(plan_authored_room(&m, seed, cx, cz, layer, None).is_none());
                    }
                }
            }
        }
    }

    /// La reserva ENTERA tiene que caber entre las filas de costura, y el footprint caer en
    /// frontera de tile. Si esto falla, o se sella un borde del chunk o la sala sale a medio tile.
    #[test]
    fn the_reserve_never_touches_the_seam_and_the_footprint_is_tile_aligned() {
        let m = manifest();
        let mut found = 0;
        for seed in SEEDS {
            for cx in -12..12 {
                for cz in -12..12 {
                    let Some(p) = plan_authored_room(&m, seed, cx, cz, 0, None) else {
                        continue;
                    };
                    found += 1;
                    let (x0, z0, x1, z1) = p.reserve_rect();
                    assert!(x0 >= USABLE_LO, "reserva pisa la costura oeste: {x0}");
                    assert!(z0 >= USABLE_LO, "reserva pisa la costura norte: {z0}");
                    assert!(x1 <= USABLE_HI + 1, "reserva pisa la costura este: {x1}");
                    assert!(z1 <= USABLE_HI + 1, "reserva pisa la costura sur: {z1}");
                    assert_eq!(p.cell_x % 2, 0, "footprint a medio tile en x");
                    assert_eq!(p.cell_z % 2, 0, "footprint a medio tile en z");
                }
            }
        }
        assert!(found > 0, "el barrido no encontro ni una sala: sorteo roto");
    }

    /// La de 10 x 10 no cabe en un chunk de 20 celdas ni sin margen. Que no salga NUNCA es el cap
    /// de ADR-083 enmienda 1 funcionando.
    #[test]
    fn oversized_rooms_are_never_placed() {
        let m = manifest();
        for seed in SEEDS {
            for cx in -12..12 {
                for cz in -12..12 {
                    if let Some(p) = plan_authored_room(&m, seed, cx, cz, 0, None) {
                        assert_eq!(p.entry, 0, "colocada una sala por encima del cap");
                    }
                }
            }
        }
    }

    /// Sin salas que quepan, no se coloca nada, y no se revienta.
    #[test]
    fn a_manifest_of_only_oversized_rooms_places_nothing() {
        let mut m = manifest();
        m.rooms.retain(|r| r.tiles_x == 10);
        for seed in SEEDS {
            for cx in -12..12 {
                for cz in -12..12 {
                    assert!(plan_authored_room(&m, seed, cx, cz, 0, None).is_none());
                }
            }
        }
    }

    /// La habitacion construible manda: si las reservas se pisan, la autorada se aparta.
    #[test]
    fn the_build_room_wins_an_overlap() {
        let m = manifest();
        let (cx, cz, plan) = find_plan(&m, 42).expect("alguna sala");
        let (rx0, rz0, _, _) = plan.reserve_rect();

        // Una construible plantada justo encima de la esquina de la reserva.
        let build = RoomPlan {
            tile_x: rx0 / 2,
            tile_z: rz0 / 2,
            door_side: 0,
        };
        assert!(
            plan_authored_room(&m, 42, cx, cz, 0, Some(&build)).is_none(),
            "la autorada se colo encima de la construible"
        );
    }

    /// EL TEST DE LO QUE SE PIDIO: margen macizo, sin un solo hueco por el que caerse.
    #[test]
    fn the_border_is_solid_all_around_except_the_doorway() {
        let m = manifest();
        let rules = &LAYER_PROFILES[0];
        let (cx, cz, plan) = find_plan(&m, 42).expect("alguna sala");

        let mut out = generate_chunk_layer(rules, 42, (cx, cz), 0, &[]);
        let carved = carve_authored_into_grid(&mut out.grid, &plan, rules.ceiling_open);
        let door: HashSet<_> = carved.iter().copied().collect();

        let (rx0, rz0, rx1, rz1) = plan.reserve_rect();
        let (fx0, fz0) = (plan.cell_x, plan.cell_z);
        let (fx1, fz1) = (fx0 + plan.cells_x, fz0 + plan.cells_z);

        for x in rx0..rx1 {
            for z in rz0..rz1 {
                let interior = x >= fx0 && x < fx1 && z >= fz0 && z < fz1;
                if interior || door.contains(&(x, z)) {
                    continue; // interior de la sala y vano: transitables a proposito
                }
                assert!(
                    !out.grid.get(x, z).is_walkable(),
                    "hueco en el borde de la reserva en ({x},{z}): por ahi se cae al vacio"
                );
            }
        }
    }

    /// El anillo tiene que ser `SealedWall` y no `Wall`: es lo unico que `repair_connectivity` no
    /// puede perforar. Con `Wall` generico, el primer bolsillo que el propio anillo aisle lo
    /// agujerea.
    #[test]
    fn the_outer_ring_is_sealed_wall() {
        let m = manifest();
        let rules = &LAYER_PROFILES[0];
        let (cx, cz, plan) = find_plan(&m, 42).expect("alguna sala");

        let mut out = generate_chunk_layer(rules, 42, (cx, cz), 0, &[]);
        let carved = carve_authored_into_grid(&mut out.grid, &plan, rules.ceiling_open);
        let door: HashSet<_> = carved.iter().copied().collect();

        let (rx0, rz0, rx1, rz1) = plan.reserve_rect();
        for x in rx0..rx1 {
            for z in rz0..rz1 {
                let on_ring = x < rx0 + RING_CELLS
                    || x >= rx1 - RING_CELLS
                    || z < rz0 + RING_CELLS
                    || z >= rz1 - RING_CELLS;
                if !on_ring || door.contains(&(x, z)) {
                    continue;
                }
                assert_eq!(
                    out.grid.get(x, z).kind(),
                    CellType::SealedWall,
                    "el anillo en ({x},{z}) no esta blindado"
                );
            }
        }
    }

    /// El vano mide UN TILE, no media celda: es lo que hace que el hueco que se ve y el que la
    /// colision del jugador cruza sean el mismo.
    #[test]
    fn the_doorway_is_one_tile_wide() {
        let m = manifest();
        let rules = &LAYER_PROFILES[0];
        let (cx, cz, plan) = find_plan(&m, 42).expect("alguna sala");

        let mut out = generate_chunk_layer(rules, 42, (cx, cz), 0, &[]);
        carve_authored_into_grid(&mut out.grid, &plan, rules.ceiling_open);

        let (starts, dir) = door_starts(&plan, plan.doors[0]);
        for start in starts {
            for step in 0..BORDER_CELLS as i32 {
                let (x, z) = (start.0 + dir.0 * step, start.1 + dir.1 * step);
                assert!(
                    out.grid.get(x as usize, z as usize).is_walkable(),
                    "el vano no esta abierto en ({x},{z}): la puerta se ve pero no se cruza"
                );
            }
        }
    }

    /// LA INVARIANTE FUERTE: despues de tallar y reparar, se puede llegar andando desde el
    /// laberinto hasta dentro de la sala. Una sala preciosa a la que no se entra no vale nada.
    #[test]
    fn the_room_is_reachable_from_the_maze() {
        let m = manifest();
        let rules = &LAYER_PROFILES[0];
        let mut checked = 0;

        for seed in SEEDS {
            let Some((cx, cz, plan)) = find_plan(&m, seed) else {
                continue;
            };
            let mut out = generate_chunk_layer(rules, seed, (cx, cz), 0, &[]);
            let carved = carve_authored_into_grid(&mut out.grid, &plan, rules.ceiling_open);
            repair_connectivity(&mut out.grid, rules.ceiling_corridor, &carved);

            // Arranca desde una celda transitable de FUERA de la reserva.
            let (rx0, rz0, rx1, rz1) = plan.reserve_rect();
            let outside = (0..CHUNK_CELLS)
                .flat_map(|x| (0..CHUNK_CELLS).map(move |z| (x, z)))
                .find(|&(x, z)| {
                    out.grid.get(x, z).is_walkable()
                        && !(x >= rx0 && x < rx1 && z >= rz0 && z < rz1)
                })
                .expect("el chunk no tiene nada transitable fuera de la sala");

            let mut seen = vec![false; CHUNK_CELLS * CHUNK_CELLS];
            let mut queue = VecDeque::from([outside]);
            seen[outside.1 * CHUNK_CELLS + outside.0] = true;
            while let Some((x, z)) = queue.pop_front() {
                for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                    let (nx, nz) = (x as i32 + dx, z as i32 + dz);
                    if !LayerGrid::in_bounds(nx, nz) {
                        continue;
                    }
                    let (nx, nz) = (nx as usize, nz as usize);
                    if seen[nz * CHUNK_CELLS + nx] || !out.grid.get(nx, nz).is_walkable() {
                        continue;
                    }
                    seen[nz * CHUNK_CELLS + nx] = true;
                    queue.push_back((nx, nz));
                }
            }

            for x in plan.cell_x..plan.cell_x + plan.cells_x {
                for z in plan.cell_z..plan.cell_z + plan.cells_z {
                    assert!(
                        seen[z * CHUNK_CELLS + x],
                        "seed {seed}: la sala de ({cx},{cz}) es inalcanzable en ({x},{z})"
                    );
                }
            }
            checked += 1;
        }

        assert!(checked > 0, "ninguna seed produjo una sala que comprobar");
    }

    /// EL FALLO QUE ESTE TEST EXISTE PARA IMPEDIR: con un solo tunel, la segunda abertura de la sala
    /// da contra el margen macizo — se ve el vano y detras un bloque cerrado. Se vio en playtest.
    #[test]
    fn every_doorway_gets_its_own_tunnel() {
        let rules = &LAYER_PROFILES[0];

        // Sala de 4 x 4 tiles con DOS aberturas enfrentadas: sur y norte.
        let m = RoomManifest {
            digest: "test".into(),
            rooms: vec![ManifestRoom {
                index: 0,
                id: "dos_puertas".into(),
                tiles_x: 4,
                tiles_z: 4,
                doorways: vec![
                    ManifestDoorway {
                        side_by_quarter: [0, 0, 0, 0],
                        tile_by_quarter: [1, 1, 1, 1],
                    },
                    ManifestDoorway {
                        side_by_quarter: [1, 1, 1, 1],
                        tile_by_quarter: [2, 2, 2, 2],
                    },
                ],
            }],
        };

        let (cx, cz, plan) = find_plan(&m, 42).expect("alguna sala");
        assert_eq!(
            plan.door_count, 2,
            "las dos aberturas tienen que llegar al plan"
        );

        let mut out = generate_chunk_layer(rules, 42, (cx, cz), 0, &[]);
        carve_authored_into_grid(&mut out.grid, &plan, rules.ceiling_open);

        // Cada abertura tiene que haber abierto su tunel a traves del borde entero (margen +
        // anillo), por sus DOS lineas de celda.
        for door in plan.doorways() {
            let (starts, dir) = door_starts(&plan, door);
            for start in starts {
                for step in 0..BORDER_CELLS as i32 {
                    let (x, z) = (start.0 + dir.0 * step, start.1 + dir.1 * step);
                    assert!(
                        out.grid.get(x as usize, z as usize).is_walkable(),
                        "lado {} tile {}: vano cerrado en ({x},{z}) — se ve la puerta y detras un bloque",
                        door.0,
                        door.1
                    );
                }
            }
        }
    }

    /// Dos boquetes que caigan en el MISMO (lado, tile) son el mismo hueco, y se excava UNA vez.
    ///
    /// Sin el dedup el segundo repetia el tallado y volvia a empujar las mismas celdas a `carved`,
    /// que es lo que se le pasa a `repair_connectivity` como protegidas. Inofensivo con un par de
    /// vanos; feo en una pared llena de ellos.
    #[test]
    fn two_doorways_on_the_same_tile_are_dug_once() {
        let rules = &LAYER_PROFILES[0];

        // La MISMA abertura declarada dos veces: es lo que produce un prefab con dos boquetes
        // pegados que caen en el mismo tile de 5 m.
        let door = ManifestDoorway {
            side_by_quarter: [0, 0, 0, 0],
            tile_by_quarter: [1, 1, 1, 1],
        };
        let m = RoomManifest {
            digest: "test".into(),
            rooms: vec![ManifestRoom {
                index: 0,
                id: "vano_duplicado".into(),
                tiles_x: 4,
                tiles_z: 4,
                doorways: vec![door.clone(), door],
            }],
        };

        let (cx, cz, plan) = find_plan(&m, 42).expect("alguna sala");
        assert_eq!(plan.door_count, 2, "las dos entradas llegan al plan");
        assert_eq!(
            plan.doors[0], plan.doors[1],
            "y describen el mismo (lado, tile)"
        );

        let mut out = generate_chunk_layer(rules, 42, (cx, cz), 0, &[]);
        let carved = carve_authored_into_grid(&mut out.grid, &plan, rules.ceiling_open);

        let unique: HashSet<(usize, usize)> = carved.iter().copied().collect();
        assert_eq!(
            unique.len(),
            carved.len(),
            "el vano duplicado se excavo dos veces: {} celdas talladas, {} distintas",
            carved.len(),
            unique.len()
        );

        // Y el vano sigue abierto: deduplicar no puede saltarse el unico tunel que habia.
        let (starts, dir) = door_starts(&plan, plan.doors[0]);
        for start in starts {
            for step in 0..BORDER_CELLS as i32 {
                let (x, z) = (start.0 + dir.0 * step, start.1 + dir.1 * step);
                assert!(
                    out.grid.get(x as usize, z as usize).is_walkable(),
                    "vano cerrado en ({x},{z}) tras deduplicar"
                );
            }
        }
    }

    /// Una sala sin ninguna abertura no se coloca: seria un sitio inaccesible en mitad del mundo.
    #[test]
    fn a_room_without_doorways_is_rejected_by_the_manifest() {
        let bad = r#"{ "digest": "d", "rooms": [
            { "index": 0, "id": "sellada", "tiles_x": 4, "tiles_z": 4, "doorways": [] }
        ] }"#;
        assert!(crate::world::grid_gen::parse_manifest(bad).is_none());
    }

    /// Sonda de cadencia contra el manifiesto REAL del repo. Ignorada por defecto (depende de un
    /// fichero fuera de `backend/`), pero es la unica que dice cuantas salas salen DE VERDAD y cada
    /// cuantos metros:
    ///
    ///     cargo test --manifest-path backend/Cargo.toml real_manifest_cadence -- --ignored --nocapture
    #[test]
    #[ignore]
    fn real_manifest_cadence() {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../Assets/StreamingAssets/room_manifest.json"
        );
        let m = crate::world::grid_gen::load_manifest(std::path::Path::new(path))
            .expect("manifiesto del repo");

        // Seed por entorno (`PROBE_SEED`), 42 por defecto: la cadencia no depende de ella, pero las
        // coordenadas de "la mas cercana" si, y en un playtest hace falta la seed de la partida.
        let seed: u64 = std::env::var("PROBE_SEED")
            .ok()
            .and_then(|v| v.parse().ok())
            .unwrap_or(42);
        const SPAN: i32 = 60; // 60 x 60 chunks = 3 x 3 km
        let mut placed = 0;
        let mut by_entry: std::collections::BTreeMap<u16, usize> = Default::default();
        for cx in -SPAN / 2..SPAN / 2 {
            for cz in -SPAN / 2..SPAN / 2 {
                if let Some(p) = plan_authored_room(&m, seed, cx, cz, 0, None) {
                    placed += 1;
                    *by_entry.entry(p.entry).or_default() += 1;
                }
            }
        }

        let chunks = (SPAN * SPAN) as f64;
        let spacing = 50.0 / (placed as f64 / chunks).sqrt();
        println!(
            "{placed} salas en {chunks} chunks ({:.1} km2) -> una cada {spacing:.0} m",
            chunks * 0.0025
        );
        for (entry, n) in &by_entry {
            println!("  entrada {entry}: {n}");
        }

        // Las mas cercanas al origen, en coordenadas de MUNDO. Es lo que hace falta para ir a ver
        // una en un playtest sin recorrer medio kilometro a ciegas.
        let mut near: Vec<(f32, i32, i32, AuthoredRoomPlan)> = Vec::new();
        for cx in -SPAN / 2..SPAN / 2 {
            for cz in -SPAN / 2..SPAN / 2 {
                if let Some(p) = plan_authored_room(&m, seed, cx, cz, 0, None) {
                    let wx = cx as f32 * 50.0 + (p.cell_x as f32 + p.cells_x as f32 / 2.0) * 2.5;
                    let wz = cz as f32 * 50.0 + (p.cell_z as f32 + p.cells_z as f32 / 2.0) * 2.5;
                    near.push(((wx * wx + wz * wz).sqrt(), cx, cz, p));
                }
            }
        }
        near.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap());
        println!("\nmas cercanas al origen (centro de la sala, coordenadas de mundo):");
        for (dist, cx, cz, p) in near.iter().take(5) {
            let wx = *cx as f32 * 50.0 + (p.cell_x as f32 + p.cells_x as f32 / 2.0) * 2.5;
            let wz = *cz as f32 * 50.0 + (p.cell_z as f32 + p.cells_z as f32 / 2.0) * 2.5;
            let sides: Vec<&str> = p
                .doorways()
                .map(|(s, _)| ["sur(-z)", "norte(+z)", "oeste(-x)", "este(+x)"][s as usize])
                .collect();
            let side = sides.join("+");
            println!(
                "  x={wx:.1} z={wz:.1}  ({dist:.0} m)  chunk({cx},{cz})  entrada {} giro {}  puerta {side}",
                p.entry, p.quarter
            );
        }

        assert!(placed > 0, "el manifiesto real no coloca ni una sala");
    }

    /// Busca una seed que ponga una sala autorada PEGADA AL SPAWN, para poder ir a verla en un
    /// playtest sin cruzar medio kilometro de laberinto. Ignorada por defecto:
    ///
    ///     cargo test --manifest-path backend/Cargo.toml hunt_seed_with_room_near_spawn -- --ignored --nocapture
    #[test]
    #[ignore]
    fn hunt_seed_with_room_near_spawn() {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../Assets/StreamingAssets/room_manifest.json"
        );
        let m = crate::world::grid_gen::load_manifest(std::path::Path::new(path))
            .expect("manifiesto del repo");

        let mut hits = 0;
        for seed in 1..40_000u64 {
            // Solo los 9 chunks alrededor del origen: el spawn cae ahi.
            for cx in -1..=1 {
                for cz in -1..=1 {
                    let Some(p) = plan_authored_room(&m, seed, cx, cz, 0, None) else {
                        continue;
                    };
                    let wx = cx as f32 * 50.0 + (p.cell_x as f32 + p.cells_x as f32 / 2.0) * 2.5;
                    let wz = cz as f32 * 50.0 + (p.cell_z as f32 + p.cells_z as f32 / 2.0) * 2.5;
                    let dist = (wx * wx + wz * wz).sqrt();
                    let sides: Vec<&str> = p
                        .doorways()
                        .map(|(s, _)| ["sur(-z)", "norte(+z)", "oeste(-x)", "este(+x)"][s as usize])
                        .collect();
                    let side = sides.join("+");
                    println!(
                        "seed {seed}: sala a {dist:.0} m en x={wx:.1} z={wz:.1} \
                         (chunk {cx},{cz}, entrada {}, giro {}, puerta {side})",
                        p.entry, p.quarter
                    );
                    hits += 1;
                }
            }
            if hits >= 12 {
                return;
            }
        }
        assert!(hits > 0, "ninguna seed pone una sala junto al spawn");
    }

    /// Dibuja en ASCII el chunk que pidas, por el camino REAL (`generate_chunk_layer` leyendo el
    /// manifiesto del entorno). Es la forma barata de ver si la sala se talló y con qué forma, sin
    /// arrancar el juego:
    ///
    ///     PROBE_SEED=157 PROBE_CX=-1 PROBE_CZ=-1 cargo test --manifest-path backend/Cargo.toml dump_chunk -- --ignored --nocapture
    #[test]
    #[ignore]
    fn dump_chunk() {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../Assets/StreamingAssets/room_manifest.json"
        );
        // Antes de la primera llamada que inicializa el OnceLock del manifiesto.
        std::env::set_var(crate::world::grid_gen::ROOM_MANIFEST_ENV, path);

        let num = |k: &str, d: i32| -> i32 {
            std::env::var(k)
                .ok()
                .and_then(|v| v.parse().ok())
                .unwrap_or(d)
        };
        let seed = std::env::var("PROBE_SEED")
            .ok()
            .and_then(|v| v.parse::<u64>().ok())
            .unwrap_or(157);
        let (cx, cz) = (num("PROBE_CX", -1), num("PROBE_CZ", -1));

        let rules = crate::world::zone_density::rules_for(seed, cx, cz, 0);
        let out = generate_chunk_layer(&rules, seed, (cx, cz), 0, &[]);

        println!("chunk ({cx},{cz}) seed {seed} — z crece hacia ABAJO, x hacia la derecha");
        println!("  '#'=Wall  'S'=SealedWall  '.'=Open  ','=Corridor  'o'=pilar  '?'=otro\n");
        for z in 0..CHUNK_CELLS {
            let mut line = String::new();
            for x in 0..CHUNK_CELLS {
                line.push(match out.grid.get(x, z).kind() {
                    CellType::Wall => '#',
                    CellType::SealedWall => 'S',
                    CellType::Open => '.',
                    CellType::Corridor => ',',
                    CellType::Pillar => 'o',
                    _ => '?',
                });
            }
            let wz = cz as f32 * 50.0 + z as f32 * 2.5;
            println!("  {line}   z={wz:.0}");
        }
        let wx0 = cx as f32 * 50.0;
        println!("\n  x va de {wx0:.0} a {:.0}", wx0 + 47.5);

        // Y lo que de verdad viaja por el wire para este chunk, con la MISMA cuenta que hace
        // `game_loop`. Es lo que el cliente usa para instanciar el prefab, asi que tiene que casar
        // con el hueco dibujado arriba.
        let m = crate::world::grid_gen::active_manifest().expect("manifiesto");
        let build_plan = crate::world::grid_gen::room_in_chunk(seed, cx, cz, 0);
        match plan_authored_room(m, seed, cx, cz, 0, build_plan.as_ref()) {
            Some(p) => {
                let (tx, tz) = p.tile_origin();
                println!(
                    "  authored_room = [{tx}, {tz}, {}, {}]  (tile de origen, entrada, giro)",
                    p.entry, p.quarter
                );
                println!(
                    "  footprint {}x{} celdas desde ({},{})",
                    p.cells_x, p.cells_z, p.cell_x, p.cell_z
                );
            }
            None => println!("  authored_room = <ninguna>"),
        }
    }
}
