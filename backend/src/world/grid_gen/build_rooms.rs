//! ADR-081 enmienda 5 — LA HABITACIÓN CONSTRUIBLE, tallada en el mundo.
//!
//! Hasta aquí, "zona construible" era una sala que el generador ya hacía por su cuenta
//! (`ZONE_SAFE`): un chunk entero de 50 m, y el cluster de arranque cuatro seguidos. Joel lo probó y
//! el veredicto fue claro — eso es un solar, no un habitáculo, y además no se distingue de nada.
//!
//! Lo que hay ahora es lo contrario: una habitación **construida a propósito** de 3 × 3 tiles
//! (15 × 15 m) que **pisa lo que el generador hubiera puesto ahí**. Hueca por dentro, con pared en
//! todo el perímetro y una sola puerta. Es el ÚNICO sitio del mundo donde se puede construir.
//!
//! POR QUÉ VIVE EN `grid_gen` Y NO EN `world/`: el emplazamiento tiene que ser legible desde las DOS
//! representaciones del mundo — la de colisión del jugador (`chunk.layout`, celdas de 5 m, en
//! `world/`) y la de render/robapieles (`LayerGrid`, celdas de 2,5 m, aquí). `grid_gen` no puede
//! importar `world/` (invariante del módulo) pero `world/` sí importa `grid_gen`, así que el lado
//! común va aquí y `world/` lo consume. El TALLADO está partido en dos por la misma razón: este
//! módulo talla la `LayerGrid`, y `world::build_room_layout` talla el `ChunkLayoutV1` con el MISMO
//! plan.
//!
//! TALLAR LAS DOS NO ES OPCIONAL: tallar solo el render da paredes que ves y atraviesas; tallar solo
//! la colisión, paredes invisibles que te frenan.

use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

use super::generator::mix;
use super::{Cell, CellType, LayerGrid, CHUNK_CELLS};

/// Lado de la habitación en TILES de 5 m. Tres, que es lo que se pidió.
///
/// El tile es la unidad con la que el jugador construye —la pared mide exactamente uno—, así que un
/// lado de 3 tiles admite 3 paredes justas y el recinto interior se puede subdividir sin que sobre
/// ni falte medio metro.
pub const ROOM_TILES: usize = 3;

/// Lado en celdas de `grid_gen` (2,5 m): un tile son dos celdas.
pub const ROOM_CELLS: usize = ROOM_TILES * 2;

/// Lado en metros.
pub const ROOM_SIZE_M: f32 = ROOM_TILES as f32 * 5.0;

/// Probabilidad de que un chunk aloje una habitación.
///
/// TODO(balance): nunca jugado. Con 0,05 sale una cada ~220 m, del orden de lo que ya se midió para
/// las salas seguras (~280 m) — que es la cadencia que Joel ya ha caminado y no puso pega.
const ROOM_CHANCE: f64 = 0.05;

/// Constante de dominio del sorteo de habitación. Separa su espacio de seeds del de las aperturas de
/// costura (`0xED6E_...`) y del de las celdas, para que dos sorteos no se correlacionen.
const ROOM_SALT: u64 = 0xB011_D005_A1E0_0081;

/// Solo en la capa 0. Las capas superiores son verticalidad decorativa (ADR-026 sigue bloqueado) y
/// una habitación construible flotando en una de ellas sería un sitio al que el jugador no puede
/// llegar de forma fiable.
const ROOM_LAYER: u8 = 0;

/// Dónde va la habitación de un chunk, si es que lleva una.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RoomPlan {
    /// Tile de la esquina de menor x/z, dentro del chunk. Rango 2..=5 — ver `room_in_chunk`.
    pub tile_x: usize,
    /// Tile de la esquina de menor z.
    pub tile_z: usize,
    /// Por dónde se entra: 0 = sur (−z), 1 = norte (+z), 2 = oeste (−x), 3 = este (+x).
    pub door_side: u8,
}

impl RoomPlan {
    /// Celda de `grid_gen` de la esquina de menor x/z.
    pub fn cell_origin(&self) -> (usize, usize) {
        (self.tile_x * 2, self.tile_z * 2)
    }
}

/// Habitación de `(cx, cz, layer)`, re-derivada del seed. Pura y memoizable: mismo input → mismo
/// output en todo peer y en cualquier momento, incluido antes de que el chunk se haya generado.
pub fn room_in_chunk(world_seed: u64, cx: i32, cz: i32, layer: u8) -> Option<RoomPlan> {
    if layer != ROOM_LAYER {
        return None;
    }

    let mut s = world_seed ^ ROOM_SALT;
    s = mix(s, cx as i64 as u64);
    s = mix(s, cz as i64 as u64);
    let mut rng = StdRng::seed_from_u64(s);

    if !rng.gen_bool(ROOM_CHANCE) {
        return None;
    }

    // Rango 2..=5, y los dos extremos están MEDIDOS, no elegidos por gusto:
    //  · las filas/columnas 0 y 19 de celdas están RESERVADAS al cosido de bordes (`stitching`), y
    //    el muro de la habitación no puede tocarlas — sellaría el borde del chunk y partiría el
    //    mundo por ahí;
    //  · además hace falta UNA celda libre más allá del muro, a los cuatro lados, para que el túnel
    //    de la puerta tenga por dónde salir. Sin ese margen la habitación nace incomunicada: el
    //    túnel topa con la fila de costura, y `repair_connectivity` tampoco puede rescatarla porque
    //    su BFS excluye el borde. Es exactamente lo que rompió cuatro tests de conectividad.
    // Con tile 2..=5: muro en celdas 3..14 como mucho, y las celdas 1-2 y 15-18 quedan libres.
    let tile_x = rng.gen_range(2..=5usize);
    let tile_z = rng.gen_range(2..=5usize);
    let door_side = rng.gen_range(0..4u8);

    Some(RoomPlan {
        tile_x,
        tile_z,
        door_side,
    })
}

/// ¿Cae `(world_x, world_z)` dentro de la habitación de su chunk?
///
/// Es LA PUERTA de construcción de ADR-081 desde la enmienda 5: el host la consulta en
/// `process_stp_place` y el cliente la refleja. Trabaja en coordenadas de mundo para que ni uno ni
/// otro tengan que saber de celdas.
pub fn position_in_build_room(world_seed: u64, world_x: f32, world_z: f32) -> bool {
    let chunk_size = CHUNK_CELLS as f32 * super::CELL_SIZE_M;
    let cx = (world_x / chunk_size).floor() as i32;
    let cz = (world_z / chunk_size).floor() as i32;

    let Some(plan) = room_in_chunk(world_seed, cx, cz, ROOM_LAYER) else {
        return false;
    };

    // Local dentro del chunk, siempre en [0, chunk_size) porque `cx` salió de un `floor`.
    let lx = world_x - cx as f32 * chunk_size;
    let lz = world_z - cz as f32 * chunk_size;

    let (x0, z0) = plan.cell_origin();
    let min_x = x0 as f32 * super::CELL_SIZE_M;
    let min_z = z0 as f32 * super::CELL_SIZE_M;

    lx >= min_x && lx < min_x + ROOM_SIZE_M && lz >= min_z && lz < min_z + ROOM_SIZE_M
}

/// Talla la habitación en la `LayerGrid`: interior hueco, anillo de pared alrededor, y una puerta
/// con su túnel hasta lo primero caminable. Devuelve las celdas TRANSITABLES que ha creado.
///
/// El anillo es `SealedWall` y no `Wall`, y eso es lo que hace que la habitación sobreviva:
/// `repair_connectivity` atraviesa y carva `Wall` libremente para reconectar bolsillos, así que un
/// muro genérico acabaría agujereado por el primer trozo de laberinto que el propio anillo dejara
/// aislado. `SealedWall` está excluido a mano de su BFS y de su carvado — es exactamente el tipo que
/// `grid_gen` inventó para perímetros estampados que no se tocan.
///
/// Corre DESPUÉS del cosido de bordes, y el llamador debe pasar por `repair_connectivity` DESPUÉS de
/// esto con las celdas devueltas como `protected`: el anillo puede haber dejado incomunicado un
/// trozo del laberinto, y esa pasada lo reconecta (o lo sella) sin poder tocar ni el muro ni el
/// interior de la habitación.
pub fn carve_into_grid(grid: &mut LayerGrid, plan: &RoomPlan, ceiling: u8) -> Vec<(usize, usize)> {
    let (x0, z0) = plan.cell_origin();
    let x1 = x0 + ROOM_CELLS; // exclusivo
    let z1 = z0 + ROOM_CELLS;

    // 1. Anillo de muro, ANTES del interior: así una celda que caiga en los dos (imposible hoy, pero
    //    lo sería con un ROOM_CELLS distinto) gana el interior y la habitación nunca nace sellada.
    for x in x0.saturating_sub(1)..=x1 {
        for z in z0.saturating_sub(1)..=z1 {
            let inside_ring = x >= x0 && x < x1 && z >= z0 && z < z1;
            if inside_ring || x >= CHUNK_CELLS || z >= CHUNK_CELLS {
                continue;
            }
            grid.set(x, z, Cell::new(CellType::SealedWall, 0, 0));
        }
    }

    // 2. Interior hueco. `Open` y no `Corridor`: es una sala, y el render de sala es el que no mete
    //    la geometría estrecha de pasillo dentro.
    let mut carved = Vec::with_capacity(ROOM_CELLS * ROOM_CELLS + CHUNK_CELLS / 2);
    for x in x0..x1 {
        for z in z0..z1 {
            grid.set(x, z, Cell::new(CellType::Open, ceiling, 0));
            carved.push((x, z));
        }
    }

    // 3. La puerta. Se prueban los cuatro lados empezando por el sorteado: el túnel de un lado
    //    concreto puede morir contra la fila de costura, y una habitación sellada es un sitio
    //    construible al que no se llega. Determinista igual — el orden es fijo.
    for offset in 0..4u8 {
        let side = (plan.door_side + offset) % 4;
        if carve_door(grid, plan, side, ceiling, &mut carved) {
            break;
        }
    }

    carved
}

/// Abre la puerta en el muro y sigue excavando hacia fuera hasta encontrar algo caminable.
///
/// Sin el túnel, la habitación puede quedar rodeada de roca maciza y ser inalcanzable: el anillo del
/// paso 1 acaba de convertir en pared todo lo que la tocaba, y `repair_connectivity` ya no vuelve a
/// pasar por aquí.
/// Devuelve `true` si el túnel llegó a enganchar con algo ya transitable.
fn carve_door(
    grid: &mut LayerGrid,
    plan: &RoomPlan,
    side: u8,
    ceiling: u8,
    carved: &mut Vec<(usize, usize)>,
) -> bool {
    let (x0, z0) = plan.cell_origin();
    let mid = ROOM_CELLS / 2;

    // Celda del muro por la que se sale, y la dirección hacia fuera.
    let (x, z, dx, dz) = match side {
        0 => (x0 as i32 + mid as i32, z0 as i32 - 1, 0i32, -1i32), // sur (−z)
        1 => (x0 as i32 + mid as i32, (z0 + ROOM_CELLS) as i32, 0, 1), // norte (+z)
        2 => (x0 as i32 - 1, z0 as i32 + mid as i32, -1, 0),       // oeste (−x)
        _ => ((x0 + ROOM_CELLS) as i32, z0 as i32 + mid as i32, 1, 0), // este (+x)
    };

    // Tope de excavación: lo bastante para cruzar cualquier macizo razonable sin llegar al borde
    // reservado del chunk. Si se agota, la habitación queda sellada — preferible a comerse la fila
    // de costura y cortar el mundo por ese lado.
    carve_tunnel_outward(grid, ceiling, (x, z), (dx, dz), CHUNK_CELLS / 2, carved).is_some()
}

/// Excava en línea recta desde `start` en dirección `dir` hasta engancharse con algo ya transitable,
/// apuntando cada celda abierta en `carved`. Devuelve cuántas celdas excavó, o `None` si no enganchó.
///
/// Compartida por la habitación construible (ADR-081 enmienda 5) y por la sala autorada (ADR-083
/// enmienda 1). Es UNA sola implementación a propósito: las dos tienen exactamente el mismo problema
/// —salir de un recinto cerrado sin comerse la costura del chunk— y tener dos excavadores que
/// divergieran en el tratamiento del borde es la clase de fallo que no se ve hasta que un chunk
/// concreto parte el mundo por un lado.
pub(super) fn carve_tunnel_outward(
    grid: &mut LayerGrid,
    ceiling: u8,
    start: (i32, i32),
    dir: (i32, i32),
    limit: usize,
    carved: &mut Vec<(usize, usize)>,
) -> Option<usize> {
    let (mut x, mut z) = start;
    let (dx, dz) = dir;

    for step in 0..limit {
        if !LayerGrid::in_bounds(x, z) {
            return None;
        }
        let (ux, uz) = (x as usize, z as usize);

        // Nunca la fila/columna de costura: es de `stitching`, y abrirla aquí descoordinaría los dos
        // chunks que comparten ese borde, que derivan su apertura de la misma seed canónica.
        if ux == 0 || uz == 0 || ux == CHUNK_CELLS - 1 || uz == CHUNK_CELLS - 1 {
            return None;
        }

        let already_open = grid.get(ux, uz).is_walkable();
        grid.set(ux, uz, Cell::new(CellType::Corridor, ceiling, 0));
        carved.push((ux, uz));
        if already_open {
            return Some(step + 1); // enganchado con el laberinto: el túnel termina aquí
        }

        x += dx;
        z += dz;
    }

    None
}

/// Excava EXACTAMENTE `count` celdas desde `start`, sin condición de parada.
///
/// Es la segunda mitad de un túnel de un TILE de ancho: la primera línea se excava con
/// `carve_tunnel_outward` —que para cuando engancha— y esta replica su longitud en la línea de al
/// lado. Copiar la longitud en vez de dejar que la segunda línea busque su propio enganche es lo que
/// impide que el vano salga dentado, con un lado más largo que el otro.
pub(super) fn carve_tunnel_fixed(
    grid: &mut LayerGrid,
    ceiling: u8,
    start: (i32, i32),
    dir: (i32, i32),
    count: usize,
    carved: &mut Vec<(usize, usize)>,
) {
    let (mut x, mut z) = start;
    let (dx, dz) = dir;

    for _ in 0..count {
        if !LayerGrid::in_bounds(x, z) {
            return;
        }
        let (ux, uz) = (x as usize, z as usize);
        if ux == 0 || uz == 0 || ux == CHUNK_CELLS - 1 || uz == CHUNK_CELLS - 1 {
            return; // misma regla de costura que arriba, y por el mismo motivo
        }
        grid.set(ux, uz, Cell::new(CellType::Corridor, ceiling, 0));
        carved.push((ux, uz));
        x += dx;
        z += dz;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::grid_gen::{generate_chunk_layer, LAYER_PROFILES};

    const SEEDS: [u64; 4] = [42, 7778, 1, 9_999_999];

    /// El emplazamiento es puro: misma seed y mismo chunk → mismo plan. Es lo que permite que dos
    /// peers tallen la misma habitación sin hablarse.
    #[test]
    fn room_placement_is_deterministic() {
        for seed in SEEDS {
            for (cx, cz) in [(0, 0), (3, -7), (11, 11), (-9, 4)] {
                assert_eq!(
                    room_in_chunk(seed, cx, cz, 0),
                    room_in_chunk(seed, cx, cz, 0)
                );
            }
        }
    }

    /// Solo capa 0: una habitación construible en una capa superior sería un sitio al que el jugador
    /// no puede llegar de forma fiable mientras ADR-026 siga bloqueado.
    #[test]
    fn rooms_only_exist_on_layer_zero() {
        for seed in SEEDS {
            for cx in -20..=20 {
                for layer in 1..4u8 {
                    assert!(room_in_chunk(seed, cx, cx, layer).is_none());
                }
            }
        }
    }

    /// La habitación NUNCA toca la fila/columna reservada al cosido de bordes. Si la tocara, su muro
    /// perimetral sellaría el borde del chunk y el mundo quedaría cortado por ahí — un mundo infinito
    /// partido en dos, y el síntoma sería un jugador que no puede seguir avanzando.
    #[test]
    fn rooms_never_touch_the_seam_rows() {
        for seed in SEEDS {
            for cx in -30..=30 {
                for cz in -30..=30 {
                    let Some(plan) = room_in_chunk(seed, cx, cz, 0) else {
                        continue;
                    };
                    let (x0, z0) = plan.cell_origin();
                    // El anillo de muro ocupa una celda MÁS por lado que el interior.
                    assert!(x0 >= 1 && z0 >= 1, "el muro pisaría la costura oeste/sur");
                    assert!(
                        x0 + ROOM_CELLS < CHUNK_CELLS && z0 + ROOM_CELLS < CHUNK_CELLS,
                        "el muro pisaría la costura este/norte"
                    );
                }
            }
        }
    }

    /// Encuentra el primer chunk con habitación de un seed; sin esto cada test tendría que adivinar
    /// coordenadas y se rompería al tocar `ROOM_CHANCE`.
    fn first_room(seed: u64) -> (i32, i32, RoomPlan) {
        for cx in 0..40 {
            for cz in 0..40 {
                if let Some(plan) = room_in_chunk(seed, cx, cz, 0) {
                    return (cx, cz, plan);
                }
            }
        }
        panic!("seed {seed}: ni una habitación en 40x40 chunks");
    }

    /// LO QUE EL JUGADOR VE: interior entero hueco y anillo de pared alrededor, con la única
    /// excepción de la puerta.
    #[test]
    fn the_carved_room_is_hollow_inside_and_walled_around() {
        let (cx, cz, plan) = first_room(42);
        let rules = &LAYER_PROFILES[0];
        let out = generate_chunk_layer(rules, 42, (cx, cz), 0, &[]);
        let mut grid = out.grid;
        carve_into_grid(&mut grid, &plan, rules.ceiling_open);

        let (x0, z0) = plan.cell_origin();
        for x in x0..x0 + ROOM_CELLS {
            for z in z0..z0 + ROOM_CELLS {
                assert!(
                    grid.get(x, z).is_walkable(),
                    "celda interior ({x},{z}) no es caminable: la habitación no está hueca"
                );
            }
        }

        let mut openings = 0;
        for x in x0 - 1..=x0 + ROOM_CELLS {
            for z in z0 - 1..=z0 + ROOM_CELLS {
                let on_ring = x < x0 || x >= x0 + ROOM_CELLS || z < z0 || z >= z0 + ROOM_CELLS;
                if on_ring && grid.get(x, z).is_walkable() {
                    openings += 1;
                }
            }
        }
        assert_eq!(
            openings, 1,
            "el perímetro debe tener exactamente una puerta"
        );
    }

    /// La puerta conecta con algo: sin túnel, el anillo de muro puede dejar la habitación rodeada de
    /// macizo y ser inalcanzable — construible sobre el papel y a la que no se llega en juego.
    #[test]
    fn the_door_tunnels_out_until_it_meets_the_maze() {
        for seed in SEEDS {
            let (cx, cz, plan) = first_room(seed);
            let rules = &LAYER_PROFILES[0];
            let out = generate_chunk_layer(rules, seed, (cx, cz), 0, &[]);
            let mut grid = out.grid;
            carve_into_grid(&mut grid, &plan, rules.ceiling_open);

            // Inundación desde el interior: tiene que alcanzar alguna celda FUERA del anillo.
            let (x0, z0) = plan.cell_origin();
            let mut seen = vec![false; CHUNK_CELLS * CHUNK_CELLS];
            let mut stack = vec![(x0, z0)];
            seen[z0 * CHUNK_CELLS + x0] = true;
            let mut escaped = false;

            while let Some((x, z)) = stack.pop() {
                if x < x0 - 1 || x > x0 + ROOM_CELLS || z < z0 - 1 || z > z0 + ROOM_CELLS {
                    escaped = true;
                    break;
                }
                for (nx, nz) in [
                    (x as i32 + 1, z as i32),
                    (x as i32 - 1, z as i32),
                    (x as i32, z as i32 + 1),
                    (x as i32, z as i32 - 1),
                ] {
                    if !LayerGrid::in_bounds(nx, nz) {
                        continue;
                    }
                    let (nx, nz) = (nx as usize, nz as usize);
                    if seen[nz * CHUNK_CELLS + nx] || !grid.get(nx, nz).is_walkable() {
                        continue;
                    }
                    seen[nz * CHUNK_CELLS + nx] = true;
                    stack.push((nx, nz));
                }
            }

            assert!(
                escaped,
                "seed {seed}: la habitación de ({cx},{cz}) está sellada — inalcanzable en juego"
            );
        }
    }

    /// La puerta de construcción, en coordenadas de mundo: dentro sí, un metro fuera no.
    #[test]
    fn position_in_build_room_matches_the_carved_rect() {
        let (cx, cz, plan) = first_room(42);
        let chunk_size = CHUNK_CELLS as f32 * crate::world::grid_gen::CELL_SIZE_M;
        let (x0, z0) = plan.cell_origin();
        let min_x = cx as f32 * chunk_size + x0 as f32 * crate::world::grid_gen::CELL_SIZE_M;
        let min_z = cz as f32 * chunk_size + z0 as f32 * crate::world::grid_gen::CELL_SIZE_M;

        assert!(position_in_build_room(42, min_x + 0.5, min_z + 0.5));
        assert!(position_in_build_room(
            42,
            min_x + ROOM_SIZE_M - 0.5,
            min_z + ROOM_SIZE_M - 0.5
        ));
        assert!(!position_in_build_room(42, min_x - 0.5, min_z + 0.5));
        assert!(!position_in_build_room(
            42,
            min_x + ROOM_SIZE_M + 0.5,
            min_z + 0.5
        ));
    }
}
