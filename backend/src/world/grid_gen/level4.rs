//! Level 4 — generador de grafo y rasterización de la región (ADR-093, etapas E0+E1).
//!
//! E0: sortea salas rectangulares dentro del rect de región y las conecta con pasillos
//! ortogonales en L, garantizando conectividad total POR CONSTRUCCIÓN: cada sala nueva
//! se conecta a la componente ya conectada (árbol), y después se añaden aristas extra
//! para crear ciclos (rutas de escape).
//!
//! E1: rasteriza ese layout a la rejilla fina de 2,5 m (`LayerGrid`) para los chunks de
//! la RESERVA de región — un rect de chunks lejano al que solo se llega por teleport
//! (E3). La mitad de colisión de 5 m vive en `world::level4_layout`, mismo reparto que
//! salas autoradas (`authored_rooms` ↔ `authored_room_layout`): este módulo no puede
//! importar `world/`.
//!
//! Unidades: celdas de 2,5 m (las de `grid_gen`). Invariante de PARIDAD: todo origen y
//! todo tamaño son PARES, para que el layout sea representable en la rejilla de colisión
//! de 5 m sin el modo de fallo de ADR-083 enmienda 3 (origen impar = sala que nunca
//! aparece). Consecuencia útil: cada tile de 5 m es uniforme (sus 4 celdas finas
//! coinciden), así que la colisión no puede discrepar del render ni en media celda.
//!
//! Determinismo: `(seed_base, epoch)` ⇒ mismo layout, byte a byte. Sin reloj, sin
//! entropía externa (mismo contrato que `Level0Builder`).

use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Mutex;

use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

use super::{Cell, CellType, LayerGrid, LayerOutput, CELL_SIZE_M, CHUNK_CELLS};

/// Esquina de menor coordenada de la reserva de región, en chunks.
///
/// Chunk (200, 0) = **mundo (10 000, 0)**. Separación HORIZONTAL, en la capa 0, y el porqué de
/// las dos cosas está medido, no elegido a ojo:
///
/// · **Por qué no (2000, 2000), como estuvo primero.** Mundo (100 000, 100 000): ahí un `f32`
///   tiene ULP ≈ 8 mm, o sea tembleque visible de cámara y física en primera persona. A 10 000 m
///   el ULP es ≈ 1 mm — imperceptible.
/// · **Por qué no en altura, que es lo que se intentó después.** El streamer de Unity
///   (`ProceduralWorldGenerator`) solo pide capas `0..layerCount-1` con `layerCount = 4`, y
///   clampa la capa del jugador a ese rango. Una reserva en la capa 100 NUNCA se pide: el
///   jugador aterriza en un vacío sin suelo ni paredes aunque el backend tenga la geometría —
///   que es exactamente lo que pasó en el playtest del 2026-08-24. Subirla exige operar el
///   streaming del cliente (más `layerVisuals[4]` y la niebla por capa) y, para pasar de 508 m,
///   `ChunkLayer` `i8`→`i16`, que viaja en `ChunkSyncData` ⇒ ADR nuevo (regla dura #7).
///   Pendiente como tarea propia; ver `docs/LEVEL4-ROADMAP.md`.
/// · **Por qué 10 000 y no menos.** A pie son ~33 min en línea recta en la dirección exacta:
///   inalcanzable en la práctica sin cerrar la puerta a acercarla si algún día conviene.
pub const REGION_ORIGIN_CHUNK: (i32, i32) = (200, 0);

/// Capa de la reserva: la 0, la única que el cliente sabe pedir hoy (ver `REGION_ORIGIN_CHUNK`).
///
/// Sigue siendo parte de la identidad de la región y toda comprobación de pertenencia la mira
/// (`region_chunk_local`, `world_pos_to_region_cell`, `level4_layout::block_is_in_region`): eso
/// entró cuando la reserva compartía XZ con el spawn y se conserva a propósito, porque es lo que
/// hace que mover la región —aquí o a la altura, el día que el cliente lo soporte— sea cambiar
/// estas dos constantes y nada más.
pub const REGION_LAYER: i32 = 0;

/// Lado de la reserva, en chunks.
pub const REGION_CHUNKS: i32 = 3;

/// Lado de la región en celdas de 2,5 m (3 chunks × 20 celdas = 60 celdas = 150 m).
pub const REGION_CELLS: i32 = REGION_CHUNKS * CHUNK_CELLS as i32;

/// Margen sellado junto al borde de región, en celdas: ninguna celda transitable puede
/// tocar el perímetro, o el mapa cerrado dejaría de serlo.
const REGION_BORDER_MARGIN: i32 = 2;

/// Epoch inicial de una sesión — el layout con el que nace la región antes de que nadie la
/// mute. Ver `current_epoch`/`set_current_epoch` para el valor VIGENTE.
pub const EPOCH_V1: u32 = 0;

/// ADR-093 (E4): el epoch vigente de ESTE proceso, mutable durante la sesión.
///
/// Global deliberado, mismo motivo que `room_manifest::active_manifest`: `generate_region_layer`
/// (colisión del jugador, render, `ensure_chunk_layer`, una docena de tests) lo necesita, y esos
/// caminos son funciones puras `(seed, pos, layer) → Chunk` sin hueco para un parámetro de sesión
/// — añadirlo tocaría cada firma para transportar un solo entero. A diferencia del manifiesto
/// (`OnceLock`, se fija UNA vez al arrancar), el epoch cambia DURANTE la partida, así que es un
/// `AtomicU32` normal, escrito solo por `game_loop` cuando `Level4RegionState::current_epoch`
/// avanza.
///
/// Es la ÚNICA excepción en todo `grid_gen` a "generación = función pura de (seed, pos, layer)"
/// (invariante 4 del doc-comment de este módulo) — deliberada: la región del Level 4 EXISTE para
/// mutar con el tiempo. Riesgo conocido y aceptado, mismo que el manifiesto: dos tests que fijen
/// epochs distintos en el MISMO binario de test se pisan si corren en paralelo sobre la reserva;
/// los tests de este módulo fijan el epoch explícitamente y lo devuelven a 0 al terminar.
static CURRENT_EPOCH: AtomicU32 = AtomicU32::new(EPOCH_V1);

/// Epoch vigente de este proceso.
pub fn current_epoch() -> u32 {
    CURRENT_EPOCH.load(Ordering::Relaxed)
}

/// Fija el epoch vigente. Host-only en producción (`game_loop`, cuando
/// `Level4RegionState::current_epoch` avanza); los tests lo usan para fijar un valor conocido.
pub fn set_current_epoch(epoch: u32) {
    CURRENT_EPOCH.store(epoch, Ordering::Relaxed);
}

/// ADR-093 (E4b): la sala a preservar en el PRÓXIMO sorteo (`current_epoch()` ya avanzado).
/// `Mutex` y no un puñado de `Atomic*` sueltos a propósito: `PlacedRoom` son cinco campos que
/// tienen que leerse/escribirse como UNA unidad, o una lectura a medio escribir devolvería una
/// sala que nunca existió. Mismo tipo de global de sesión que `CURRENT_EPOCH`, mismo motivo
/// (ver su doc): `generate_with_preserved` la necesitan `ensure_chunk_layer` y el intercept de
/// `chunk_tile_walls`, ninguno con hueco para un parámetro de sesión.
static PRESERVED_ROOM: Mutex<Option<PlacedRoom>> = Mutex::new(None);

/// Fija la sala a preservar en el próximo sorteo (o `None` para no preservar ninguna).
/// `game_loop::level4_room_to_preserve` la calcula ANTES de avanzar `set_current_epoch`.
pub fn set_preserved_room(room: Option<PlacedRoom>) {
    *PRESERVED_ROOM.lock().unwrap_or_else(|e| e.into_inner()) = room;
}

/// La sala a preservar vigente. Recuperación de lock envenenado (`unwrap_or_else`) porque un
/// panic en OTRO test que la tocara no debe tumbar la generación de todos los que vienen
/// después en el mismo binario.
pub fn preserved_room() -> Option<PlacedRoom> {
    *PRESERVED_ROOM.lock().unwrap_or_else(|e| e.into_inner())
}

/// ADR-093 (E4b): convierte una posición de MUNDO en la celda de REGIÓN (2,5 m) que ocupa, o
/// `None` si cae fuera de la reserva. Mismas constantes que rasterizan la región, así que "estar
/// en tal celda" significa lo mismo aquí que en `generate_region_chunk`.
///
/// **Comprueba la Y**, y no es opcional: con la reserva en XZ (0,0) un jugador parado en el
/// spawn tiene exactamente el mismo XZ que un jugador dentro de la región. Sin la banda de
/// altura, `position_is_buildable` prohibiría construir en el spawn y el sorteo de fantasmas
/// escalaría su densidad ahí.
pub fn world_pos_to_region_cell(pos: [f32; 3]) -> Option<(i32, i32)> {
    // Banda de una capa completa alrededor del suelo de la reserva: el jugador está de pie
    // sobre él (ojos a ~1,8 m) y el techo interior son 5 m, así que media capa por abajo y una
    // entera por arriba cubre estar dentro sin invadir capas vecinas (que además son macizas).
    let floor = region_floor_y();
    if pos[1] < floor - super::LAYER_HEIGHT_M * 0.5 || pos[1] > floor + super::LAYER_HEIGHT_M * 1.5
    {
        return None;
    }
    let chunk_size_m = CHUNK_CELLS as f32 * CELL_SIZE_M;
    let local_x = pos[0] - REGION_ORIGIN_CHUNK.0 as f32 * chunk_size_m;
    let local_z = pos[2] - REGION_ORIGIN_CHUNK.1 as f32 * chunk_size_m;
    if local_x < 0.0 || local_z < 0.0 {
        return None;
    }
    let cell = (
        (local_x / CELL_SIZE_M) as i32,
        (local_z / CELL_SIZE_M) as i32,
    );
    if cell.0 >= REGION_CELLS || cell.1 >= REGION_CELLS {
        return None;
    }
    Some(cell)
}

/// ADR-093 (E3-fix): el VESTÍBULO — la sala fija por la que se entra y se sale.
///
/// Rect CONSTANTE en coordenadas de región, idéntico en todos los epochs: 8×8 celdas (20×20 m)
/// centrado en la reserva (60 celdas de lado ⇒ 26..34 centra en 30). Paridad par como toda sala
/// (ver el invariante de PARIDAD del módulo).
pub const ENTRY_HALL: CellRect = CellRect {
    min: (26, 26),
    size: (8, 8),
};

/// Centro del vestíbulo en coordenadas de MUNDO — dónde aterriza quien cruza la puerta de
/// entrada. Y = suelo de la reserva + la altura de ojos que usa el resto del proyecto
/// (`PLAYER_BASE_Y`, 1,8 m).
pub fn entry_hall_world_pos() -> [f32; 3] {
    let chunk_size_m = CHUNK_CELLS as f32 * CELL_SIZE_M;
    let (cx, cz) = ENTRY_HALL.center();
    [
        REGION_ORIGIN_CHUNK.0 as f32 * chunk_size_m + cx as f32 * CELL_SIZE_M,
        region_floor_y() + 1.8,
        REGION_ORIGIN_CHUNK.1 as f32 * chunk_size_m + cz as f32 * CELL_SIZE_M,
    ]
}

/// Dónde se planta la PUERTA DE VUELTA dentro del vestíbulo, en coordenadas de mundo.
///
/// Separada del punto de aterrizaje a propósito, y es lo que arregla el rebote que hasta ahora
/// tapaba un enfriamiento de 3 s: si la puerta está EXACTAMENTE donde apareces, el frame de
/// llegada ya te tiene encima de ella. `RETURN_DOOR_OFFSET_M` al norte del centro deja la puerta
/// a la vista nada más aterrizar —el vestíbulo mide 20 m, así que 5 m sigue holgadamente dentro—
/// y a ti fuera de su plano, que es lo único que la detección por cruce necesita para no
/// dispararse sola.
///
/// La Y es la del SUELO, no la de los ojos: aquí se planta un marco, no se teletransporta a
/// nadie. Confundirlas dejaría la puerta flotando 1,8 m en el aire.
pub fn return_door_world_pos() -> [f32; 3] {
    let hall = entry_hall_world_pos();
    [hall[0], region_floor_y(), hall[2] - RETURN_DOOR_OFFSET_M]
}

/// Cuánto se separa una puerta de su punto de aterrizaje, en metros.
pub const RETURN_DOOR_OFFSET_M: f32 = 5.0;

/// Ancla de la puerta de ENTRADA, en Level 0. Espejo de lo que planta `GameBootstrap`.
///
/// Está en el CENTRO del tile (0,2) del chunk (0,0), y el sitio no es arbitrario: ese tile forma
/// con (0,1) y (0,3) un tramo norte-sur continuo, así que un marco exento ahí se puede cruzar por
/// los DOS lados. El ancla anterior, (3,0,0), caía en el tile (0,0) — un fondo de saco con pared
/// al norte, este y oeste, donde media puerta daba contra roca. Medido con la sonda de `walls`,
/// no deducido. A ~8 m del spawn por defecto (5,0,5).
pub const ENTRY_DOOR_WORLD_POS: [f32; 3] = [2.5, 0.0, 12.5];

/// Dónde aparece quien VUELVE al Level 0. Ahora lo resuelve `portal_exit`, que además blinda el
/// caso de una posición desincronizada; esto se conserva como el punto CANÓNICO de llegada para
/// quien necesite uno sin cruzar (tests, y el mirror con `GameBootstrap`).
pub fn entry_door_arrival_pos() -> [f32; 3] {
    portal_exit(ENTRY_DOOR_WORLD_POS, false)
}

/// Cuánto delante de la cara transitable de la puerta de destino se aparece, en metros.
///
/// Tiene que ser MAYOR que la banda de muestreo del cruce del cliente (`CrossBandM`, 1 m) o
/// aterrizarías dentro de la banda de la puerta por la que acabas de salir, y el primer paso en
/// cualquier dirección volvería a contar como cruce.
pub const PORTAL_EXIT_M: f32 = 1.5;

/// Holgura sobre el suelo al aparecer. Cero exacto arranca dentro del propio suelo; 1,8 (la altura
/// de ojos) es una CAÍDA de 1,8 m en cada cruce, que es lo que se sentía como "te lanza un poco".
const PORTAL_EXIT_CLEARANCE_M: f32 = 0.1;

/// Dónde sale quien cruza una puerta.
///
/// TRES DECISIONES, y las tres salen de bugs vistos en el log del playtest del 2026-08-25:
///
/// 1. **El eje de cruce es FIJO, no relativo.** Antes esto era una traslación pura
///    (`pos + (destino − origen)`), que conserva de qué lado del plano estás. Suena bien y es lo
///    que hace Portal, pero Portal tiene los portales EN PAREDES y solo se cruzan por delante:
///    estos marcos son exentos y se cruzan por los dos lados, así que cruzar "al revés" te
///    escupía por la cara NO transitable de la gemela — contra la pared, y del lado de "todavía
///    no he cruzado", con lo que el primer paso te devolvía. En el log: tres cruces en 340 ms.
///    Ahora sales SIEMPRE por la cara transitable, cruces por donde cruces.
/// 2. **La Y es la del SUELO, no la del jugador.** El backend ignora la Y que reporta el cliente
///    (`resolve_move` fuerza la suya), así que arrastraba un 1,9 heredado y cada cruce acababa en
///    una caída de 1,8 m.
/// 3. **El desplazamiento LATERAL sí se conserva**, recortado al ancho del vano. Es lo que hace
///    que el salto no se note: sales por donde entraste, no recentrado de golpe. Y es el único
///    eje donde una posición ligeramente desincronizada no puede hacer daño — a diferencia del
///    eje de cruce, donde heredar un error mandaba el destino a 57 m de distancia (el
///    `dest z=127.49` del log, con el backend creyendo al jugador dentro del vestíbulo).
pub fn portal_exit(requester_pos: [f32; 3], door_is_entry: bool) -> [f32; 3] {
    let (from_door, to_door) = if door_is_entry {
        (ENTRY_DOOR_WORLD_POS, return_door_world_pos())
    } else {
        (return_door_world_pos(), ENTRY_DOOR_WORLD_POS)
    };
    // Las dos puertas son perpendiculares a Z, así que "lateral" es X y la cara transitable es un
    // signo conocido: la de vuelta da al vestíbulo (+Z), la de entrada al lado del spawn (−Z).
    // Rotarlas obliga a generalizar esto con su yaw — misma precondición que documenta
    // `GameBootstrap.SpawnLevel4Doors`.
    let lateral =
        (requester_pos[0] - from_door[0]).clamp(-PORTAL_HALF_WIDTH_M, PORTAL_HALF_WIDTH_M);
    let exit_sign = if door_is_entry { 1.0 } else { -1.0 };
    [
        to_door[0] + lateral,
        to_door[1] + PORTAL_EXIT_CLEARANCE_M,
        to_door[2] + exit_sign * PORTAL_EXIT_M,
    ]
}

/// Medio ancho del vano. Espejo de `Level4DoorTrigger.DoorWidth / 2`: recortar aquí evita que un
/// jugador que cruce rozando la jamba salga incrustado en el marco de la otra.
pub const PORTAL_HALF_WIDTH_M: f32 = 0.8;

/// Altura de techo del interior, en unidades de 2,5 m (2 = 5 m de oficina).
const REGION_CEILING_UNITS: u8 = 2;

/// Cuántas salas intenta colocar el sorteo (el espacio puede admitir menos).
pub const ROOM_TARGET: usize = 12;

/// Salas mínimas para dar el layout por válido; por debajo, el sorteo es un bug.
pub const ROOM_MIN_COUNT: usize = 6;

/// Grosor de pasillo en celdas (2 celdas = 5 m, un tile de colisión).
pub const CORRIDOR_THICKNESS: i32 = 2;

/// Separación mínima entre rects de sala, en celdas.
const ROOM_SEPARATION: i32 = 2;

/// Lados de sala permitidos (pares, dentro de los topes reales de salas autoradas).
const ROOM_SIDES: [i32; 4] = [6, 8, 10, 12];

/// Intentos de colocación antes de rendirse con las salas que hayan cabido.
const PLACEMENT_ATTEMPTS: usize = 200;

/// Sal del nivel. Mismo esquema que `Level0Builder` (`world_seed ^ SALT`); no se cambia
/// sin cambiar el mundo de todos los seeds existentes.
const LEVEL4_SALT: u64 = 0xBACB_0004_0FF1_CE00;

/// Rect alineado a ejes en celdas de 2,5 m. `min` inclusivo, `size` en celdas.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CellRect {
    pub min: (i32, i32),
    pub size: (i32, i32),
}

impl CellRect {
    pub fn max_exclusive(&self) -> (i32, i32) {
        (self.min.0 + self.size.0, self.min.1 + self.size.1)
    }

    pub fn center(&self) -> (i32, i32) {
        (self.min.0 + self.size.0 / 2, self.min.1 + self.size.1 / 2)
    }

    /// Centro forzado a coordenadas pares (ancla de pasillos, hereda la paridad).
    pub fn center_even(&self) -> (i32, i32) {
        let (cx, cz) = self.center();
        (cx & !1, cz & !1)
    }

    pub fn contains(&self, cell: (i32, i32)) -> bool {
        let (mx, mz) = self.max_exclusive();
        cell.0 >= self.min.0 && cell.0 < mx && cell.1 >= self.min.1 && cell.1 < mz
    }

    fn inflated(&self, by: i32) -> CellRect {
        CellRect {
            min: (self.min.0 - by, self.min.1 - by),
            size: (self.size.0 + 2 * by, self.size.1 + 2 * by),
        }
    }

    fn overlaps(&self, other: &CellRect) -> bool {
        let (amx, amz) = self.max_exclusive();
        let (bmx, bmz) = other.max_exclusive();
        self.min.0 < bmx && other.min.0 < amx && self.min.1 < bmz && other.min.1 < amz
    }
}

/// Una sala colocada. `is_return_room` marca la sala que contendrá la puerta de vuelta
/// (etapa E3); siempre existe exactamente una.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PlacedRoom {
    pub rect: CellRect,
    pub is_return_room: bool,
}

/// Layout abstracto de la región para un `(seed_base, epoch)`. Los pasillos son rects
/// de grosor `CORRIDOR_THICKNESS`; pueden solapar salas y entre sí (el tallado lo
/// resuelve: celda vaciada es celda vaciada).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Level4Layout {
    pub epoch: u32,
    pub rooms: Vec<PlacedRoom>,
    pub corridors: Vec<CellRect>,
}

impl Level4Layout {
    /// ¿La celda (coordenadas de REGIÓN, 2,5 m) es transitable? Sala gana a pasillo
    /// solo nominalmente: ambos son "abierto"; lo que importa es abierto vs macizo.
    pub fn cell_open(&self, cell: (i32, i32)) -> bool {
        self.rooms.iter().any(|r| r.rect.contains(cell))
            || self.corridors.iter().any(|c| c.contains(cell))
    }

    /// ¿La celda cae dentro de alguna SALA (no pasillo)?
    pub fn cell_in_room(&self, cell: (i32, i32)) -> bool {
        self.rooms.iter().any(|r| r.rect.contains(cell))
    }

    /// ADR-093 (E4b): la sala (si alguna) que contiene esta celda. `Copy` de vuelta a
    /// propósito — `PlacedRoom` es diminuto y el llamador (elección de sala a preservar) lo
    /// quiere desacoplado del `Level4Layout` saliente, que se descarta acto seguido.
    pub fn room_containing(&self, cell: (i32, i32)) -> Option<PlacedRoom> {
        self.rooms.iter().find(|r| r.rect.contains(cell)).copied()
    }
}

/// SplitMix64 — misma difusión que `grid_gen::generator` (ADR-019), local para no
/// depender de la visibilidad `pub(super)` de aquel módulo.
#[inline]
fn splitmix64(mut z: u64) -> u64 {
    z = (z ^ (z >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    z ^ (z >> 31)
}

fn derive_seed(seed_base: u64, epoch: u32) -> u64 {
    splitmix64(
        (seed_base ^ LEVEL4_SALT)
            .wrapping_add(0x9e37_79b9_7f4a_7c15)
            .wrapping_add(u64::from(epoch)),
    )
}

/// Genera el layout de la región. Determinista: misma entrada ⇒ misma salida.
pub fn generate(seed_base: u64, epoch: u32) -> Level4Layout {
    generate_with_preserved(seed_base, epoch, None)
}

/// ADR-093 (E4b): igual que `generate`, pero con una sala YA FIJADA — la que un jugador
/// ocupaba en el layout saliente al avanzar epoch (ver `game_loop::level4_room_to_preserve`).
/// `preserved` entra en el sorteo como si fuera la primera sala colocada: el resto se sortea
/// respetando su hueco (mismo chequeo de separación que cualquier otra) y `connect_rooms` la
/// conecta con el mismo árbol de vecino-más-cercano — no hace falta reconexión especial,
/// termina enganchada a la red de pasillos como cualquier sala nueva.
pub fn generate_with_preserved(
    seed_base: u64,
    epoch: u32,
    preserved: Option<PlacedRoom>,
) -> Level4Layout {
    let mut rng = StdRng::seed_from_u64(derive_seed(seed_base, epoch));

    let rooms = place_rooms(&mut rng, preserved);
    let corridors = connect_rooms(&mut rng, &rooms);

    Level4Layout {
        epoch,
        rooms,
        corridors,
    }
}

fn place_rooms(rng: &mut StdRng, preserved: Option<PlacedRoom>) -> Vec<PlacedRoom> {
    // ADR-093 E3-fix: el VESTÍBULO va primero y siempre, en el mismo rect en TODOS los epochs.
    // Es lo que hace cierto el "las puertas están ancladas, lo que cambia es el espacio detrás"
    // del ADR: aterrizas aquí al entrar, y aquí está la puerta de vuelta, mientras el resto de
    // la planta se re-sortea a tu alrededor. Antes la puerta de vuelta se anclaba al centro
    // GEOMÉTRICO de la reserva, que el sorteo podía dejar macizo — salida dentro de la roca.
    let mut rooms: Vec<PlacedRoom> = vec![PlacedRoom {
        rect: ENTRY_HALL,
        is_return_room: true,
    }];
    // La sala preservada (E4b) entra después y solo si no pisa el vestíbulo: el vestíbulo no es
    // negociable, y una preservada que lo solapara dejaría dos salas encajadas.
    if let Some(room) = preserved {
        if !ENTRY_HALL.inflated(ROOM_SEPARATION).overlaps(&room.rect) {
            rooms.push(PlacedRoom {
                rect: room.rect,
                is_return_room: false,
            });
        }
    }
    for _ in 0..PLACEMENT_ATTEMPTS {
        if rooms.len() >= ROOM_TARGET {
            break;
        }
        let w = ROOM_SIDES[rng.gen_range(0..ROOM_SIDES.len())];
        let h = ROOM_SIDES[rng.gen_range(0..ROOM_SIDES.len())];
        // Origen par dentro del margen sellado: sorteo en la rejilla de paso 2.
        let min_x = REGION_BORDER_MARGIN;
        let max_x = (REGION_CELLS - REGION_BORDER_MARGIN - w) / 2;
        let max_z = (REGION_CELLS - REGION_BORDER_MARGIN - h) / 2;
        if max_x * 2 < min_x || max_z * 2 < min_x {
            continue;
        }
        let rect = CellRect {
            min: (
                rng.gen_range((min_x / 2)..=max_x) * 2,
                rng.gen_range((min_x / 2)..=max_z) * 2,
            ),
            size: (w, h),
        };
        let padded = rect.inflated(ROOM_SEPARATION);
        if rooms.iter().any(|r| r.rect.overlaps(&padded)) {
            continue;
        }
        // La marca de sala de retorno la lleva SIEMPRE el vestíbulo (índice 0), nunca una
        // sorteada: es el ancla fija de la puerta de vuelta.
        rooms.push(PlacedRoom {
            rect,
            is_return_room: false,
        });
    }
    // Invariante de `exactly_one_return_room`: el vestíbulo se empuja con la marca puesta y
    // nadie más la lleva, así que esto es una red de seguridad para un futuro en el que el
    // vestíbulo dejara de ser el índice 0 — no una rama que hoy se tome.
    if !rooms.iter().any(|r| r.is_return_room) {
        if let Some(first) = rooms.first_mut() {
            first.is_return_room = true;
        }
    }
    rooms
}

fn connect_rooms(rng: &mut StdRng, rooms: &[PlacedRoom]) -> Vec<CellRect> {
    let mut corridors = Vec::new();
    // Árbol: cada sala i se conecta a la sala YA conectada con centro más cercano
    // (manhattan). Conectividad total por construcción.
    for i in 1..rooms.len() {
        let from = rooms[i].rect.center_even();
        let nearest = rooms[..i]
            .iter()
            .min_by_key(|r| {
                let c = r.rect.center_even();
                (c.0 - from.0).abs() + (c.1 - from.1).abs()
            })
            .expect("rooms[..i] no vacío para i >= 1");
        push_l_corridor(&mut corridors, rng, from, nearest.rect.center_even());
    }
    // Ciclos: una arista extra por cada 4 salas, entre pares al azar distintos.
    let extra = rooms.len() / 4;
    for _ in 0..extra {
        let a = rng.gen_range(0..rooms.len());
        let b = rng.gen_range(0..rooms.len());
        if a == b {
            continue;
        }
        push_l_corridor(
            &mut corridors,
            rng,
            rooms[a].rect.center_even(),
            rooms[b].rect.center_even(),
        );
    }
    corridors
}

/// Pasillo en L entre dos anclas pares: tramo horizontal + tramo vertical, grosor
/// `CORRIDOR_THICKNESS`, con el codo elegido por sorteo. Los rects cubren ambas anclas
/// y el codo (rangos inclusivos + grosor).
fn push_l_corridor(out: &mut Vec<CellRect>, rng: &mut StdRng, a: (i32, i32), b: (i32, i32)) {
    let pivot = if rng.gen::<bool>() {
        (b.0, a.1)
    } else {
        (a.0, b.1)
    };
    push_axis_segment(out, a, pivot);
    push_axis_segment(out, pivot, b);
}

fn push_axis_segment(out: &mut Vec<CellRect>, a: (i32, i32), b: (i32, i32)) {
    debug_assert!(a.0 == b.0 || a.1 == b.1, "segmento no alineado a eje");
    let min = (a.0.min(b.0), a.1.min(b.1));
    let max = (a.0.max(b.0), a.1.max(b.1));
    let rect = CellRect {
        min,
        size: (
            (max.0 - min.0) + CORRIDOR_THICKNESS,
            (max.1 - min.1) + CORRIDOR_THICKNESS,
        ),
    };
    if rect.size.0 > 0 && rect.size.1 > 0 {
        out.push(rect);
    }
}

// ─── E1: reserva de región y rasterización de la rejilla fina ────────────────

/// Si el chunk cae en la reserva de región, devuelve su índice LOCAL (0..REGION_CHUNKS).
///
/// **La capa es parte de la identidad, no un adorno.** Desde que la reserva vive en XZ (0,0)
/// —el mismo del spawn— comprobar solo la coordenada horizontal diría que sí para el chunk de
/// arranque del Level 0. Ver el doc-comment de `REGION_ORIGIN_CHUNK`.
pub fn region_chunk_local(chunk: (i32, i32), layer: i32) -> Option<(i32, i32)> {
    if layer != REGION_LAYER {
        return None;
    }
    let lx = chunk.0 - REGION_ORIGIN_CHUNK.0;
    let lz = chunk.1 - REGION_ORIGIN_CHUNK.1;
    ((0..REGION_CHUNKS).contains(&lx) && (0..REGION_CHUNKS).contains(&lz)).then_some((lx, lz))
}

/// Y del suelo de la reserva, en metros de mundo. Es lo que `layer_y` daría para
/// `REGION_LAYER` con el paso de `grid_gen` (4 m), y lo que el teleport de entrada usa como
/// altura de aterrizaje.
pub fn region_floor_y() -> f32 {
    REGION_LAYER as f32 * super::LAYER_HEIGHT_M
}

/// Rasteriza un chunk de la reserva a la rejilla fina de 2,5 m.
///
/// Solo `REGION_LAYER` tiene interior; cualquier otra capa sale maciza (la región es un
/// mapa cerrado de una planta). No se cose (`stitch_edges`) a propósito: los pasillos
/// que cruzan de chunk salen coherentes POR CONSTRUCCIÓN, porque los dos chunks
/// rasterizan el MISMO layout global de región — el mismo argumento de determinismo
/// que sostiene las salas multi-chunk de ADR-084. Un chunk vecino FUERA de la reserva
/// puede abrir su apertura de costura contra nuestro perímetro macizo y quedarse con un
/// fondo de saco decorativo; a 10 km del spawn y sin ruta a pie que nadie vaya a andar,
/// nadie lo verá jamás.
pub fn generate_region_layer(
    world_seed: u64,
    epoch: u32,
    local_chunk: (i32, i32),
    layer_index: i32,
) -> LayerOutput {
    let mut grid = LayerGrid::new_solid();
    if layer_index == REGION_LAYER {
        // ADR-093 E4b: honra la sala preservada vigente (`None` en el caso normal — los tests
        // de este módulo no la tocan, así que su comportamiento no cambia).
        let layout = generate_with_preserved(world_seed, epoch, preserved_room());
        let base = (
            local_chunk.0 * CHUNK_CELLS as i32,
            local_chunk.1 * CHUNK_CELLS as i32,
        );
        for x in 0..CHUNK_CELLS {
            for z in 0..CHUNK_CELLS {
                let cell = (base.0 + x as i32, base.1 + z as i32);
                if layout.cell_open(cell) {
                    let ct = if layout.cell_in_room(cell) {
                        CellType::Open
                    } else {
                        CellType::Corridor
                    };
                    grid.set(x, z, Cell::new(ct, REGION_CEILING_UNITS, 0));
                }
            }
        }
    }
    LayerOutput {
        grid,
        require_walkable_above: Vec::new(),
        require_walkable_below: Vec::new(),
        room_zones: Vec::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::{HashSet, VecDeque};

    /// Rasteriza el layout a celdas transitables (interior de salas + pasillos).
    fn walkable(layout: &Level4Layout) -> HashSet<(i32, i32)> {
        let mut cells = HashSet::new();
        let rects = layout
            .rooms
            .iter()
            .map(|r| r.rect)
            .chain(layout.corridors.iter().copied());
        for rect in rects {
            let (mx, mz) = rect.max_exclusive();
            for x in rect.min.0..mx {
                for z in rect.min.1..mz {
                    cells.insert((x, z));
                }
            }
        }
        cells
    }

    fn reachable_from(cells: &HashSet<(i32, i32)>, start: (i32, i32)) -> HashSet<(i32, i32)> {
        let mut seen = HashSet::new();
        let mut queue = VecDeque::new();
        if cells.contains(&start) {
            seen.insert(start);
            queue.push_back(start);
        }
        while let Some((x, z)) = queue.pop_front() {
            for next in [(x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1)] {
                if cells.contains(&next) && seen.insert(next) {
                    queue.push_back(next);
                }
            }
        }
        seen
    }

    #[test]
    fn same_input_same_layout() {
        for epoch in [0u32, 1, 7] {
            assert_eq!(generate(42, epoch), generate(42, epoch));
        }
        assert_eq!(generate(0, 0), generate(0, 0));
    }

    #[test]
    fn different_epoch_different_layout() {
        // No es un requisito duro celda a celda, pero dos epochs consecutivos idénticos
        // en TODO el layout delatarían que el epoch no entra en la semilla.
        assert_ne!(generate(42, 0), generate(42, 1));
    }

    #[test]
    fn full_connectivity_100_draws() {
        // Verificación (b) de ADR-093: cero salas incomunicadas en 100 sorteos.
        for draw in 0..100u32 {
            let layout = generate(u64::from(draw), draw % 5);
            let cells = walkable(&layout);
            let start = layout.rooms[0].rect.center_even();
            let seen = reachable_from(&cells, start);
            for (i, room) in layout.rooms.iter().enumerate() {
                assert!(
                    seen.contains(&room.rect.center_even()),
                    "sorteo {draw}: sala {i} incomunicada"
                );
            }
        }
    }

    #[test]
    fn rooms_inside_region_and_even_parity() {
        for draw in 0..100u32 {
            let layout = generate(u64::from(draw).wrapping_mul(7919), 0);
            for room in &layout.rooms {
                let (mx, mz) = room.rect.max_exclusive();
                assert!(room.rect.min.0 >= 0 && room.rect.min.1 >= 0);
                assert!(mx <= REGION_CELLS && mz <= REGION_CELLS);
                assert_eq!(room.rect.min.0 % 2, 0, "origen x impar");
                assert_eq!(room.rect.min.1 % 2, 0, "origen z impar");
                assert_eq!(room.rect.size.0 % 2, 0, "ancho impar");
                assert_eq!(room.rect.size.1 % 2, 0, "alto impar");
            }
        }
    }

    #[test]
    fn nothing_walkable_touches_the_region_border() {
        // Mapa cerrado: si una celda abierta toca el perímetro, la región deja de ser
        // una reserva sellada y el margen macizo era mentira.
        for draw in 0..100u32 {
            let layout = generate(u64::from(draw).wrapping_mul(6151), draw % 3);
            for cell in walkable(&layout) {
                assert!(
                    cell.0 >= REGION_BORDER_MARGIN
                        && cell.1 >= REGION_BORDER_MARGIN
                        && cell.0 < REGION_CELLS - REGION_BORDER_MARGIN
                        && cell.1 < REGION_CELLS - REGION_BORDER_MARGIN,
                    "sorteo {draw}: celda {cell:?} pegada al borde de región"
                );
            }
        }
    }

    #[test]
    fn rooms_keep_separation() {
        for draw in 0..100u32 {
            let layout = generate(u64::from(draw).wrapping_mul(104_729), 3);
            for (i, a) in layout.rooms.iter().enumerate() {
                for b in layout.rooms.iter().skip(i + 1) {
                    let padded = a.rect.inflated(ROOM_SEPARATION);
                    assert!(
                        !padded.overlaps(&b.rect),
                        "sorteo {draw}: salas a menos de {ROOM_SEPARATION} celdas"
                    );
                }
            }
        }
    }

    #[test]
    fn exactly_one_return_room_and_enough_rooms() {
        for draw in 0..100u32 {
            let layout = generate(u64::from(draw) ^ 0xDEAD_BEEF, 0);
            let returns = layout.rooms.iter().filter(|r| r.is_return_room).count();
            assert_eq!(returns, 1, "sorteo {draw}: {returns} salas de retorno");
            assert!(layout.rooms[0].is_return_room, "la sala 0 es la de retorno");
            assert!(
                layout.rooms.len() >= ROOM_MIN_COUNT,
                "sorteo {draw}: solo {} salas",
                layout.rooms.len()
            );
        }
    }

    #[test]
    fn corridors_stay_inside_region() {
        // Los pasillos van entre centros de sala: no pueden salirse de la región.
        for draw in 0..100u32 {
            let layout = generate(u64::from(draw).wrapping_add(31_337), 1);
            for c in &layout.corridors {
                let (mx, mz) = c.max_exclusive();
                assert!(c.min.0 >= 0 && c.min.1 >= 0, "sorteo {draw}");
                assert!(mx <= REGION_CELLS && mz <= REGION_CELLS, "sorteo {draw}");
            }
        }
    }

    // ─── E1 ───

    #[test]
    fn region_chunk_local_maps_the_reserve_and_nothing_else() {
        let l = REGION_LAYER;
        assert_eq!(region_chunk_local(REGION_ORIGIN_CHUNK, l), Some((0, 0)));
        assert_eq!(
            region_chunk_local(
                (
                    REGION_ORIGIN_CHUNK.0 + REGION_CHUNKS - 1,
                    REGION_ORIGIN_CHUNK.1 + REGION_CHUNKS - 1
                ),
                l
            ),
            Some((REGION_CHUNKS - 1, REGION_CHUNKS - 1))
        );
        assert_eq!(
            region_chunk_local((REGION_ORIGIN_CHUNK.0 - 1, REGION_ORIGIN_CHUNK.1), l),
            None
        );
        assert_eq!(
            region_chunk_local(
                (REGION_ORIGIN_CHUNK.0 + REGION_CHUNKS, REGION_ORIGIN_CHUNK.1),
                l
            ),
            None
        );
        assert_eq!(region_chunk_local((5, 5), l), None);
    }

    /// La capa es parte de la identidad de la reserva, no solo el XZ. Hoy los separa también la
    /// coordenada horizontal, pero la comprobación de capa se conserva a propósito: es lo que
    /// permitirá mover la región a la altura (cuando el streamer del cliente sepa pedir esa
    /// capa) cambiando solo las dos constantes.
    #[test]
    fn the_reserve_is_only_itself_on_its_own_layer() {
        assert!(region_chunk_local(REGION_ORIGIN_CHUNK, REGION_LAYER).is_some());
        for other in [REGION_LAYER - 1, REGION_LAYER + 1, REGION_LAYER + 8] {
            assert_eq!(
                region_chunk_local(REGION_ORIGIN_CHUNK, other),
                None,
                "capa {other}: no es la capa de la reserva"
            );
        }
        // Y el chunk de spawn no es la reserva en NINGUNA capa.
        for layer in [REGION_LAYER, REGION_LAYER + 1, 3] {
            assert_eq!(region_chunk_local((0, 0), layer), None);
        }
    }

    #[test]
    fn raster_matches_the_abstract_layout_cell_by_cell() {
        for seed in [42u64, 7778] {
            let layout = generate(seed, EPOCH_V1);
            for lx in 0..REGION_CHUNKS {
                for lz in 0..REGION_CHUNKS {
                    let out = generate_region_layer(seed, EPOCH_V1, (lx, lz), REGION_LAYER);
                    for x in 0..CHUNK_CELLS {
                        for z in 0..CHUNK_CELLS {
                            let cell = (
                                lx * CHUNK_CELLS as i32 + x as i32,
                                lz * CHUNK_CELLS as i32 + z as i32,
                            );
                            assert_eq!(
                                out.grid.get(x, z).is_walkable(),
                                layout.cell_open(cell),
                                "seed {seed} chunk ({lx},{lz}) celda ({x},{z})"
                            );
                        }
                    }
                }
            }
        }
    }

    #[test]
    fn raster_is_deterministic_and_seams_are_coherent() {
        // Dos rasterizaciones del mismo chunk: byte a byte iguales (verificación (a)
        // de ADR-093 a nivel de rejilla fina). Y la costura entre chunks vecinos es
        // coherente por construcción: la columna 19 de (0,0) y la columna 0 de (1,0)
        // describen celdas ADYACENTES del mismo layout global, así que un pasillo que
        // cruza no puede morir en el borde.
        let a1 = generate_region_layer(42, EPOCH_V1, (0, 0), REGION_LAYER);
        let a2 = generate_region_layer(42, EPOCH_V1, (0, 0), REGION_LAYER);
        assert_eq!(a1.grid.cells(), a2.grid.cells());

        let layout = generate(42, EPOCH_V1);
        let right = generate_region_layer(42, EPOCH_V1, (1, 0), REGION_LAYER);
        for z in 0..CHUNK_CELLS {
            let global = (CHUNK_CELLS as i32, z as i32);
            assert_eq!(
                right.grid.get(0, z).is_walkable(),
                layout.cell_open(global),
                "celda 0 del chunk (1,0), fila {z}"
            );
        }
    }

    #[test]
    fn layers_other_than_the_regions_own_are_solid() {
        for layer in [REGION_LAYER - 1, REGION_LAYER + 1, REGION_LAYER + 2] {
            let out = generate_region_layer(42, EPOCH_V1, (1, 1), layer);
            assert!(
                out.grid.cells().iter().all(|c| !c.is_walkable()),
                "capa {layer} con celdas transitables en la región"
            );
        }
    }

    // ─── E4b: sala preservada ───
    //
    // Todos estos tests pasan `preserved` como parámetro EXPLÍCITO de `generate_with_preserved`
    // — no tocan el global `PRESERVED_ROOM`, así que no necesitan guard de limpieza (a
    // diferencia de un test que sí lo tocara).

    fn some_room_from(seed: u64, epoch: u32) -> PlacedRoom {
        generate(seed, epoch).rooms[0]
    }

    #[test]
    fn preserved_room_appears_verbatim_in_the_new_layout() {
        let preserved = some_room_from(42, 0);
        let layout = generate_with_preserved(42, 1, Some(preserved));
        assert!(
            layout.rooms.iter().any(|r| r.rect == preserved.rect),
            "la sala preservada debe aparecer con el MISMO rect en el layout entrante"
        );
    }

    #[test]
    fn preserved_room_keeps_separation_from_every_new_room() {
        for draw in 0..50u32 {
            let preserved = some_room_from(u64::from(draw), 0);
            let layout = generate_with_preserved(u64::from(draw), 1, Some(preserved));
            let preserved_padded = preserved.rect.inflated(ROOM_SEPARATION);
            for room in &layout.rooms {
                if room.rect == preserved.rect {
                    continue;
                }
                assert!(
                    !preserved_padded.overlaps(&room.rect),
                    "sorteo {draw}: una sala nueva invade el hueco de la preservada"
                );
            }
        }
    }

    #[test]
    fn preserved_room_stays_fully_connected() {
        // Mismo chequeo que `full_connectivity_100_draws`, pero con una sala inyectada — la
        // garantía de conectividad de `connect_rooms` no distingue "preservada" de "nueva".
        for draw in 0..50u32 {
            let preserved = some_room_from(u64::from(draw).wrapping_mul(7919), 0);
            let layout = generate_with_preserved(u64::from(draw), draw % 5, Some(preserved));
            let cells = walkable(&layout);
            let start = layout.rooms[0].rect.center_even();
            let seen = reachable_from(&cells, start);
            for (i, room) in layout.rooms.iter().enumerate() {
                assert!(
                    seen.contains(&room.rect.center_even()),
                    "sorteo {draw}: sala {i} incomunicada con una sala preservada en juego"
                );
            }
        }
    }

    #[test]
    fn exactly_one_return_room_regardless_of_whether_the_preserved_one_was_it() {
        for draw in 0..30u32 {
            let seed = u64::from(draw) ^ 0xC0FF_EE00;
            let old = generate(seed, 0);
            for preserved in [old.rooms[0], *old.rooms.last().unwrap()] {
                let layout = generate_with_preserved(seed, 1, Some(preserved));
                let returns = layout.rooms.iter().filter(|r| r.is_return_room).count();
                assert_eq!(
                    returns, 1,
                    "sorteo {draw}: {returns} salas de retorno con preservada.is_return_room={}",
                    preserved.is_return_room
                );
            }
        }
    }

    #[test]
    fn world_pos_to_region_cell_maps_inside_and_rejects_outside() {
        let chunk_size_m = CHUNK_CELLS as f32 * CELL_SIZE_M;
        let origin = (
            REGION_ORIGIN_CHUNK.0 as f32 * chunk_size_m,
            REGION_ORIGIN_CHUNK.1 as f32 * chunk_size_m,
        );
        let y = region_floor_y() + 1.8;

        // Esquina exacta de la reserva -> celda (0,0).
        assert_eq!(
            world_pos_to_region_cell([origin.0, y, origin.1]),
            Some((0, 0))
        );

        // Un punto a media celda del origen -> celda (1,1) (2,5 m por celda).
        assert_eq!(
            world_pos_to_region_cell([origin.0 + 3.0, y, origin.1 + 3.0]),
            Some((1, 1))
        );

        // Justo en el borde exterior (== REGION_CELLS * CELL_SIZE_M): fuera, exclusivo.
        let region_extent = REGION_CELLS as f32 * CELL_SIZE_M;
        assert_eq!(
            world_pos_to_region_cell([origin.0 + region_extent, y, origin.1]),
            None
        );
    }

    /// El área de SPAWN nunca es la reserva. Con la región en (200,0) los separa el XZ; la
    /// comprobación de altura sigue puesta y activa (se conserva de cuando compartían XZ), así
    /// que se afirman las dos cosas: ni el spawn a ras de suelo, ni el spawn a la altura de la
    /// región, ni la región a una altura que no es la suya.
    #[test]
    fn the_spawn_area_is_never_the_reserve() {
        let inside = entry_hall_world_pos();
        assert!(
            world_pos_to_region_cell(inside).is_some(),
            "el vestíbulo tiene que caer dentro de la reserva"
        );

        for y in [0.0, 1.8, 4.0, 20.0, 32.0, 400.0] {
            assert_eq!(
                world_pos_to_region_cell([0.0, y, 0.0]),
                None,
                "el spawn (0,{y},0) no es la reserva"
            );
        }

        // Mismo XZ que el vestíbulo pero muy por encima: fuera de la banda de altura.
        assert_eq!(
            world_pos_to_region_cell([inside[0], inside[1] + 100.0, inside[2]]),
            None
        );
    }

    /// ESPEJO CON C#: `GameBootstrap.SpawnLevel4Doors` ancla la puerta de vuelta a estas
    /// coordenadas escritas a mano (Unity no puede llamar a esta función). Si alguien mueve la
    /// región y solo toca el lado Rust, la puerta de vuelta se queda en mitad de la nada y el
    /// jugador se queda encerrado — así que el número vive también aquí, y este test es quien
    /// avisa. Al cambiarlo, cambiar `GameBootstrap.cs` en el MISMO commit.
    #[test]
    fn the_entry_hall_matches_the_hardcoded_csharp_door_anchor() {
        assert_eq!(
            entry_hall_world_pos(),
            [10075.0, 1.8, 75.0],
            "mueve también el ancla de GameBootstrap.SpawnLevel4Doors (Assets/Scripts/Gameplay)"
        );
        // La puerta de vuelta NO va donde aterrizas: `RETURN_DOOR_OFFSET_M` al norte y a ras de
        // SUELO, no a altura de ojos. Las dos cosas son errores plausibles y silenciosos —
        // plantarla en el punto de llegada devuelve el rebote que la detección por cruce quitó,
        // y heredar la Y de los ojos la deja flotando 1,8 m en el aire.
        assert_eq!(
            return_door_world_pos(),
            [10075.0, 0.0, 70.0],
            "mueve también el ancla de vuelta de GameBootstrap.SpawnLevel4Doors"
        );
        assert!(
            return_door_world_pos()[1] < entry_hall_world_pos()[1],
            "la puerta se planta en el suelo; el punto de aterrizaje va a altura de ojos"
        );
        // La de ENTRADA, en Level 0. Su sitio no es cosmético: el tile tiene que dejar hueco a
        // los DOS lados o el marco exento sólo se cruza por uno.
        assert_eq!(
            ENTRY_DOOR_WORLD_POS,
            [2.5, 0.0, 12.5],
            "mueve también el ancla de entrada de GameBootstrap.SpawnLevel4Doors"
        );
        // Cada aterrizaje cae en la CARA TRANSITABLE de su puerta, a `PORTAL_EXIT_M`. Los signos
        // difieren porque esas caras miran a lados opuestos: la de entrada hacia el spawn (−Z),
        // la de vuelta hacia el centro del vestíbulo (+Z). Romper esto no da un rebote: da un
        // portal que enseña un sitio y te suelta en otro, contra la pared.
        assert_eq!(
            ENTRY_DOOR_WORLD_POS[2] - entry_door_arrival_pos()[2],
            PORTAL_EXIT_M,
            "se vuelve a la cara −Z de la puerta de entrada"
        );
        // `RETURN_DOOR_OFFSET_M` es otra cosa y sigue siéndolo: cuánto se separa la puerta de
        // vuelta del CENTRO del vestíbulo, que es lo que la deja a la vista al llegar.
        assert_eq!(
            entry_hall_world_pos()[2] - return_door_world_pos()[2],
            RETURN_DOOR_OFFSET_M,
            "la puerta de vuelta se planta a la vista del centro del vestíbulo"
        );
    }

    #[test]
    fn room_containing_finds_the_room_and_nothing_else() {
        let layout = generate(42, 0);
        let room = layout.rooms[0];
        assert_eq!(
            layout.room_containing(room.rect.min),
            Some(room),
            "la esquina de la sala debe resolver a ella misma"
        );

        // Un punto bien lejos de toda sala/pasillo (fuera de la región entera): ninguna sala.
        assert_eq!(layout.room_containing((-100, -100)), None);
    }

    #[test]
    fn preserved_room_global_round_trips_and_defaults_to_none() {
        struct ResetOnDrop;
        impl Drop for ResetOnDrop {
            fn drop(&mut self) {
                set_preserved_room(None);
            }
        }
        let _guard = ResetOnDrop;

        assert_eq!(preserved_room(), None, "sin fijar, debe ser None");

        let room = some_room_from(42, 0);
        set_preserved_room(Some(room));
        assert_eq!(preserved_room(), Some(room));

        set_preserved_room(None);
        assert_eq!(preserved_room(), None);
    }
}
