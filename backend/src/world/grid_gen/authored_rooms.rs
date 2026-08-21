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

use super::build_rooms::{carve_tunnel_fixed, RoomPlan};
use super::generator::mix;
use super::layer_rules::LAYER_PROFILES;
use super::room_manifest::{ManifestRoom, RoomManifest, MAX_DOORWAYS};
use super::LAYER_HEIGHT_M;
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

/// La capa más alta que una sala puede llegar a invadir.
///
/// La ÚLTIMA capa nunca se invade: es la única que dibuja techo (`roofSlab = isTopLayer`), así que
/// abrirle un hueco dejaría el mundo sin tapa por ahí. ADR-085 punto 5. Con 4 capas y 4 m de paso,
/// el cap de altura autorable es `(4 − 1) · 4 = 12 m`.
const MAX_INVADED_LAYER: u8 = (LAYER_PROFILES.len() - 2) as u8;

/// La capa más alta que ocupa una sala de `height_meters`, contada desde `AUTHORED_LAYER`.
///
/// Se invaden las capas cuya LOSA DE SUELO cae DENTRO de la sala. La de la capa `L` está en
/// `y = L · LAYER_HEIGHT_M` y estorba solo si `0 < L · LAYER_HEIGHT_M < h`; una losa justo en `y = h`
/// no corta la sala, **ES su techo**. De ahí `ceil(h / LH) − 1`.
///
/// NO es `floor(h / LH)`, que era lo que ADR-085 proponía y su enmienda 2 corrigió: con `h = 4` —el
/// default de `RoomDefinition.heightMeters`— habría invadido la capa 1 y le habría quitado a la sala
/// por defecto la losa que era su propio techo.
///
/// Alturas de 0, negativas o NaN caen en la rama de "cabe en su capa", que es el comportamiento
/// anterior a ADR-085: un manifiesto viejo, sin el campo, sigue dando el mundo de siempre.
pub fn top_layer_for_height(height_meters: f32) -> u8 {
    // El NaN va explícito y no colado en un `!(a > b)`: un manifiesto con la altura corrupta tiene
    // que caer en el comportamiento de siempre, no en una capa cualquiera.
    if height_meters.is_nan() || height_meters <= LAYER_HEIGHT_M {
        return AUTHORED_LAYER;
    }
    let top = (height_meters / LAYER_HEIGHT_M).ceil() as i32 - 1;
    top.clamp(AUTHORED_LAYER as i32, MAX_INVADED_LAYER as i32) as u8
}

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
    /// Celda de la esquina de menor x del footprint, **relativa al chunk que consulta el plan**.
    ///
    /// CON SIGNO, y puede caer fuera de `0..CHUNK_CELLS` (ADR-084): una sala anclada en el chunk
    /// vecino asoma por aquí con origen negativo, y una anclada aquí puede desbordar por el otro
    /// lado. Todo el tallado recorta a las celdas que existen en este chunk; ver `clip_to_chunk`.
    /// Hoy ninguna sala del pool pasa de un chunk, así que en la práctica sigue estando en `1..=18`.
    pub cell_x: i32,
    /// Celda de la esquina de menor z del footprint, relativa al chunk que consulta. Ver `cell_x`.
    pub cell_z: i32,
    /// Footprint ya girado, en celdas.
    pub cells_x: usize,
    pub cells_z: usize,
    /// La capa MÁS ALTA que ocupa la sala, contada desde `AUTHORED_LAYER`, que es donde se ancla.
    /// `0` = cabe en su propia capa, que es lo que pasaba con todas antes de ADR-085.
    ///
    /// Se guarda ya discretizada y no en metros por dos razones: el plan es `Copy` y `Eq` —un `f32`
    /// rompería lo segundo—, y esta cuenta la tiene que hacer alguien UNA vez, no cada consumidor.
    pub top_layer: u8,
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
    ///
    /// Sin recortar: es la forma COMPLETA de la reserva, que puede salirse del chunk. Quien vaya a
    /// escribir en la rejilla la recorta con `clip_to_chunk`; quien compare solapes la quiere entera,
    /// porque dos salas se pisan lo mismo aunque el solape caiga en el chunk de al lado.
    pub fn reserve_rect(&self) -> (i32, i32, i32, i32) {
        (
            self.cell_x - BORDER_CELLS as i32,
            self.cell_z - BORDER_CELLS as i32,
            self.cell_x + self.cells_x as i32 + BORDER_CELLS as i32,
            self.cell_z + self.cells_z as i32 + BORDER_CELLS as i32,
        )
    }

    /// Tile de 5 m de la esquina del footprint. El footprint siempre cae en frontera de tile porque
    /// el origen se sortea en celdas pares — ver `plan_authored_rooms`.
    ///
    /// `div_euclid` y no `/`: con origen negativo la división de Rust trunca hacia cero y `-1 / 2`
    /// daría tile 0, el mismo que `1 / 2`. La división euclídea redondea hacia abajo, que es lo que
    /// significa "el tile en el que cae esta celda".
    pub fn tile_origin(&self) -> (i32, i32) {
        (self.cell_x.div_euclid(2), self.cell_z.div_euclid(2))
    }

    /// El MISMO plan visto desde un chunk que está `(dx, dz)` chunks más allá del ancla.
    ///
    /// Solo se mueve el origen: la sala no cambia de sitio en el mundo, cambia el chunk desde el que
    /// se la mira. Es la operación que permite que dos chunks tallen la misma sala sin hablarse
    /// (ADR-084) — el mismo truco que `aperture_pos` usa para la costura.
    fn shifted_by_chunks(self, dx: i32, dz: i32) -> Self {
        Self {
            cell_x: self.cell_x + dx * CHUNK_CELLS as i32,
            cell_z: self.cell_z + dz * CHUNK_CELLS as i32,
            ..self
        }
    }
}

/// Cuántas salas autoradas admite un chunk como mucho (ADR-083 enmienda 3 punto 4).
///
/// Es tope de BARRIDO, no de diseño: impide que el planificador siga probando candidatas en un
/// chunk donde ya no cabe nada, y pone techo al coste de una ruta que se re-deriva en cada
/// generación de chunk. Por encima de 3 la aritmética de la reserva ya no da con ningún tamaño de
/// sala que valga la pena autorar — ver `AuthoredRoomSet`.
pub const MAX_AUTHORED_PER_CHUNK: usize = 3;

/// Separación MÍNIMA entre dos reservas de sala autorada, en celdas de 2,5 m. Un tile exacto.
///
/// **No basta con que las reservas no se solapen, y esto costó tres iteraciones de medición.** Dos
/// reservas PEGADAS dejan sus dos anillos `SealedWall` espalda contra espalda, y ese bloque de cuatro
/// celdas macizas es infranqueable para el BFS de `repair_connectivity`, que carva `Wall` interior
/// pero nunca `SealedWall`. Consecuencia real: una sala cuya puerta da a ese bloque no llega jamás al
/// laberinto y nace siendo una caja sellada en mitad del mundo. Medido con la sonda
/// `probe_unreachable_rooms`: **2 de 14 salas incomunicadas** con reservas pegadas.
///
/// Con un tile de `Wall` genérico entre los dos anillos, `repair_connectivity` tiene por dónde
/// carvar y el fallo desaparece: **0 de 17** con el mismo barrido.
///
/// El precio está medido y es real: la regla de qué cabe pasa de `T₁ + T₂ ≤ 4` tiles a
/// **`T₁ + T₂ ≤ 3`**. Dos salas de 2 × 2 tiles ya NO conviven; 2 + 1 y 1 + 1 sí. Es el coste de que
/// una sala colocada sea siempre una sala alcanzable.
const SEPARATION_CELLS: usize = 2;

/// Las salas autoradas de un chunk.
///
/// Array fijo y `Copy`, NO `Vec`, por la misma razón que `AuthoredRoomPlan::doors`: esto se
/// re-deriva en cada generación de chunk y esa ruta la recorren la colisión del jugador, la caché
/// del robapieles y el render. ADR-083 enmienda 3 lo prohíbe explícitamente.
///
/// **Cuántas caben de verdad.** Con el borde de un tile (enmienda 2) y el origen sorteado en celda
/// par, la ventana útil por eje es de 16 celdas y una sala de `T` tiles reserva `2T + 4`. Dos
/// reservas caben disjuntas en un eje solo si `T₁ + T₂ ≤ 4` tiles: 2+2, 3+1, 2+1, 1+1. Cuatro salas
/// solo si son de 2 × 2 tiles en rejilla — y por eso el tope de 3 no recorta nada útil.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct AuthoredRoomSet {
    plans: [Option<AuthoredRoomPlan>; MAX_AUTHORED_PER_CHUNK],
    len: u8,
}

impl AuthoredRoomSet {
    pub fn len(&self) -> usize {
        self.len as usize
    }

    pub fn is_empty(&self) -> bool {
        self.len == 0
    }

    fn is_full(&self) -> bool {
        self.len as usize >= MAX_AUTHORED_PER_CHUNK
    }

    fn push(&mut self, plan: AuthoredRoomPlan) {
        self.plans[self.len as usize] = Some(plan);
        self.len += 1;
    }

    /// Las salas colocadas, en el orden en que se colocaron. Ese orden ES CONTRATO: el cliente
    /// instancia por índice de esta lista y reordenarla movería los prefabs de sitio.
    pub fn iter(&self) -> impl Iterator<Item = &AuthoredRoomPlan> + '_ {
        self.plans[..self.len as usize]
            .iter()
            .filter_map(|p| p.as_ref())
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

/// Elige sala, giro y sitio para este chunk — hasta `MAX_AUTHORED_PER_CHUNK`, o ninguna.
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
/// solapan, la autorada cede el sitio.
///
/// ADR-083 enmienda 3: se coloca EN BUCLE, y cada candidata se rechaza si su reserva pisa la de una
/// sala ya colocada. El barrido de candidatas y el sorteo del origen no cambian, así que **la
/// primera sala cae exactamente donde caía en wire 38**: lo único que hace esta versión es seguir
/// probando en vez de devolver a la primera. Las demás salas se añaden detrás.
///
/// ADR-084: además de las que ANCLA este chunk, devuelve las ancladas en un vecino que asoman por
/// aquí, ya trasladadas a coordenadas de este chunk. Ver `plan_anchored_at` y `NEIGHBOUR_SPAN`.
pub fn plan_authored_rooms(
    manifest: &RoomManifest,
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
    build_room: Option<&RoomPlan>,
) -> AuthoredRoomSet {
    let mut placed = AuthoredRoomSet::default();
    if layer > MAX_INVADED_LAYER || manifest.rooms.is_empty() {
        return placed;
    }

    // El chunk PROPIO primero, y los vecinos después en orden canónico. El orden es función de
    // `(cx, cz)` y de nada más, que es lo que exige el determinismo; ponerlo delante conserva además
    // el significado histórico de "la primera sala del chunk" = la que este chunk ancla.
    collect_from_anchor(
        manifest,
        world_seed,
        cx,
        cz,
        layer,
        0,
        0,
        build_room,
        &mut placed,
    );
    for dz in -NEIGHBOUR_SPAN..=NEIGHBOUR_SPAN {
        for dx in -NEIGHBOUR_SPAN..=NEIGHBOUR_SPAN {
            if dx == 0 && dz == 0 {
                continue;
            }
            collect_from_anchor(
                manifest,
                world_seed,
                cx,
                cz,
                layer,
                dx,
                dz,
                None,
                &mut placed,
            );
        }
    }
    placed
}

/// Hasta dónde se mira buscando un ancla que invada este chunk.
///
/// Para SABER QUÉ ME INVADE bastaría ±1 con el cap de 2 × 2 chunks. Es ±2 porque la resolución de
/// conflictos entre anclas necesita ese alcance para ser consistente (ADR-084 punto 3): si A cede
/// ante B y B ante C, un chunk que solo mirase ±1 desde A no vería a C y decidiría distinto.
const NEIGHBOUR_SPAN: i32 = 2;

/// Evalúa el planificador del ancla `(cx + dx, cz + dz)` y añade a `placed` las salas suyas que
/// asomen por `(cx, cz)`, trasladadas a coordenadas de este chunk.
///
/// `own_build_room` solo se usa cuando el ancla ES este chunk. Para los vecinos, la construible se
/// RE-DERIVA con `room_in_chunk`: pasarles la nuestra haría que el resultado de un ancla dependiera
/// de quién la consulta, y dos chunks tallarían sitios distintos para la misma sala.
///
/// (Y por la misma razón, el `own_build_room` que llegue aquí debe ser
/// `room_in_chunk(world_seed, cx, cz, layer)`. Los dos llamadores de producción lo cumplen; los
/// tests lo fuerzan a propósito para comprobar que la construible gana el solape.)
#[allow(clippy::too_many_arguments)]
fn collect_from_anchor(
    manifest: &RoomManifest,
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
    dx: i32,
    dz: i32,
    own_build_room: Option<&RoomPlan>,
    placed: &mut AuthoredRoomSet,
) {
    let (ax, az) = (cx + dx, cz + dz);
    // El sorteo SIEMPRE ocurre en `AUTHORED_LAYER`, se pregunte desde la capa que se pregunte: una
    // sala alta no es una sala distinta en la capa 1, es la MISMA sala vista desde arriba. Por eso
    // la construible que entra en el sorteo es también la de la capa de anclaje — y por eso el
    // `own_build_room` que trae el llamador solo vale cuando pregunta desde ella. Si a la capa 1 se
    // le pasara su propia construible (que es `None`, porque la construible solo existe en la 0), la
    // capa 1 sortearía la sala en otro sitio que la capa 0 y la sala saldría partida en dos.
    let build = if dx == 0 && dz == 0 && layer == AUTHORED_LAYER {
        BuildRoomSource::Given(own_build_room)
    } else {
        BuildRoomSource::Derive {
            layer: AUTHORED_LAYER,
        }
    };

    for plan in plan_anchored_at(manifest, world_seed, ax, az, build).iter() {
        if placed.is_full() {
            return;
        }
        // Una sala solo existe para las capas que ocupa. La 0 las ve todas.
        if layer > plan.top_layer {
            continue;
        }
        // Del ancla a ESTE chunk: un chunk de distancia son `CHUNK_CELLS` celdas.
        let shifted = plan.shifted_by_chunks(dx, dz);
        if reaches_this_chunk(&shifted) {
            placed.push(shifted);
        }
    }
}

/// ¿La RESERVA de este plan asoma por el chunk `0..CHUNK_CELLS`? Se mide contra la reserva y no
/// contra el footprint: el margen y el anillo también hay que tallarlos aquí, y una sala cuyo
/// footprint quede justo al otro lado de la frontera sigue teniendo que macizar celdas de este lado.
fn reaches_this_chunk(plan: &AuthoredRoomPlan) -> bool {
    let n = CHUNK_CELLS as i32;
    rects_overlap(plan.reserve_rect(), (0, 0, n, n))
}

/// De dónde sale la habitación construible del ancla que se está evaluando.
///
/// Existe para no pagarla cuando no hace falta. `room_in_chunk` monta su propio `StdRng` (ChaCha, y
/// sembrarlo no es gratis), y el 99 % de los vecinos ni siquiera tienen sala autorada — derivarla
/// antes de saberlo multiplicaba por veinticinco un coste que casi siempre se tira.
enum BuildRoomSource<'a> {
    /// Ya la tiene el llamador. Solo para el chunk propio.
    Given(Option<&'a RoomPlan>),
    /// Derivarla con `room_in_chunk`, y solo si la sala autorada llega a existir.
    Derive { layer: u8 },
}

/// Las salas que ANCLA `(cx, cz)`, en coordenadas de ese chunk. El sorteo puro de siempre.
fn plan_anchored_at(
    manifest: &RoomManifest,
    world_seed: u64,
    cx: i32,
    cz: i32,
    build_source: BuildRoomSource<'_>,
) -> AuthoredRoomSet {
    let mut placed = AuthoredRoomSet::default();
    if manifest.rooms.is_empty() {
        return placed;
    }

    let mut s = world_seed ^ AUTHORED_SALT;
    s = mix(s, cx as i64 as u64);
    s = mix(s, cz as i64 as u64);
    let mut rng = StdRng::seed_from_u64(s);

    if !rng.gen_bool(AUTHORED_CHANCE) {
        return placed;
    }

    // Pasado el gate —y solo aquí— se paga la construible del ancla. Ver `BuildRoomSource`.
    let derived;
    let build_room = match build_source {
        BuildRoomSource::Given(plan) => plan,
        BuildRoomSource::Derive { layer } => {
            derived = super::build_rooms::room_in_chunk(world_seed, cx, cz, layer);
            derived.as_ref()
        }
    };

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
        return placed;
    }
    candidates.shuffle(&mut rng);

    for (idx, quarter, w, h) in candidates {
        if placed.is_full() {
            break;
        }
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
            top_layer: top_layer_for_height(room.height_meters),
            doors,
            door_count: room.doorways.len().min(MAX_DOORWAYS) as u8,
        };

        if let Some(build) = build_room {
            if overlaps_build_room(&plan, build) {
                continue;
            }
        }
        // Y contra las salas autoradas que ya ganaron sitio en este chunk. Dos reservas que se
        // pisen romperían la garantía que sostiene el sistema entero: el margen macizo de una
        // acabaría siendo el interior hueco de la otra.
        if placed.iter().any(|other| {
            let (x0, z0, x1, z1) = other.reserve_rect();
            let sep = SEPARATION_CELLS as i32;
            let grown = (x0 - sep, z0 - sep, x1 + sep, z1 + sep);
            rects_overlap(grown, plan.reserve_rect())
        }) {
            continue;
        }
        placed.push(plan);
    }

    placed
}

/// Sortea el origen del FOOTPRINT en un eje, en celda PAR, o `None` si no cabe.
///
/// La reserva ocupa `size + 2 · BORDER_CELLS` y tiene que caber entera en `[USABLE_LO, USABLE_HI]`.
/// El origen del footprint queda `BORDER_CELLS` más adentro. Se sortea en unidades de tile y se
/// multiplica por 2, que es la forma barata de garantizar paridad sin descartar tiradas.
fn draw_origin(rng: &mut StdRng, size: usize) -> Option<i32> {
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
    Some((first_even + 2 * rng.gen_range(0..slots)) as i32)
}

/// ¿Se pisan dos rects `(x0, z0, x1, z1)` con x1/z1 EXCLUSIVOS?
///
/// Separado de `overlaps_build_room` por ADR-083 enmienda 3: la misma prueba la necesitan ahora
/// reserva↔construible y reserva↔reserva, y tener dos copias de la aritmética de solape es
/// exactamente como se cuelan los fallos de borde de un off-by-one.
fn rects_overlap(a: (i32, i32, i32, i32), b: (i32, i32, i32, i32)) -> bool {
    a.0 < b.2 && b.0 < a.2 && a.1 < b.3 && b.1 < a.3
}

/// El trozo de un rect `(x0, z0, x1, z1)` que cae DENTRO de este chunk, como índices de la rejilla,
/// o `None` si no asoma nada.
///
/// Es el único sitio por el que una coordenada con signo se convierte en índice. Recorta los
/// ÍNDICES, no la forma: quien decida "esto es anillo" o "esto es interior" debe seguir comparando
/// contra el rect completo, o un trozo de sala que asome desde el chunk vecino se leería como si su
/// borde estuviera en la frontera del chunk.
fn clip_to_chunk(rect: (i32, i32, i32, i32)) -> Option<(usize, usize, usize, usize)> {
    let n = CHUNK_CELLS as i32;
    let (x0, z0) = (rect.0.max(0), rect.1.max(0));
    let (x1, z1) = (rect.2.min(n), rect.3.min(n));
    if x0 >= x1 || z0 >= z1 {
        return None;
    }
    Some((x0 as usize, z0 as usize, x1 as usize, z1 as usize))
}

/// ¿Se pisan la reserva de la sala autorada y la de la habitación construible (su anillo incluido)?
fn overlaps_build_room(plan: &AuthoredRoomPlan, build: &RoomPlan) -> bool {
    let (bx, bz) = build.cell_origin();
    let (bx, bz) = (bx as i32, bz as i32);
    // El anillo de la construible es la corona de 1 celda alrededor de su footprint.
    let build_reserve = (
        bx - 1,
        bz - 1,
        bx + super::build_rooms::ROOM_CELLS as i32 + 1,
        bz + super::build_rooms::ROOM_CELLS as i32 + 1,
    );

    rects_overlap(plan.reserve_rect(), build_reserve)
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
    let (x0, z0) = (plan.cell_x, plan.cell_z);
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

/// Talla TODAS las salas autoradas de un chunk y devuelve las celdas TRANSITABLES creadas, para
/// pasárselas a `repair_connectivity` como `protected`.
///
/// **DOS FASES, y el orden es lo único que hace correcto el caso de varias salas.** Primero la
/// carcasa de todas —reserva, anillo, interior—, y solo después las puertas de todas.
///
/// Tallar sala por sala entera es un fallo REAL, no teórico, y así se encontró: en el chunk (9,6)
/// de la seed 42 la sala 0 excavó su túnel y enganchó con una celda de laberinto que estaba abierta
/// en ese momento; después la reserva de la sala 1 tapió el trozo de laberinto que sostenía esa
/// conexión, y el túnel de la sala 0 quedó de muñón ciego. `repair_connectivity` tampoco pudo
/// rehacerlo —su BFS solo carva `Wall` interior, nunca `Void`, `Pillar` ni `SealedWall`— y la sala 0
/// nació incomunicada.
///
/// Con las carcasas puestas antes que ningún túnel, cada puerta excava contra el macizo DEFINITIVO
/// del chunk: la celda abierta con la que engancha ya no se la puede llevar por delante nadie.
pub fn carve_authored_set_into_grid(
    grid: &mut LayerGrid,
    rooms: &AuthoredRoomSet,
    ceiling: u8,
    ceiling_corridor: u8,
) -> Vec<(usize, usize)> {
    let mut carved = Vec::new();
    for plan in rooms.iter() {
        carve_authored_shell(grid, plan, ceiling, &mut carved);
    }

    // REUNIR EL LABERINTO ANTES DE EXCAVAR NINGUNA PUERTA, y solo con varias salas.
    //
    // Dos reservas de 8 × 8 celdas macizan 128 de las 400 de un chunk, y eso PARTE el laberinto en
    // trozos que ya no se hablan. Sin este paso la componente mayor puede quedar al norte de las
    // salas mientras una puerta mira al sur: `tunnel_len_to_maze` no la alcanza nunca, cae al
    // fallback de borde y la sala nace incomunicada. Medido en el chunk (9,6) de la seed 42.
    //
    // No puede perforar las salas: para entrar al margen —que es `Wall` y sí se carva— habría que
    // cruzar el anillo, y el anillo es `SealedWall`, el único tipo que el BFS de `repair_connectivity`
    // no atraviesa ni carva. Es la misma propiedad en la que ya se apoya la enmienda 1 punto 4.
    //
    // Con UNA sala se omite a propósito: el chunk no se fragmenta —0 de 11 incomunicadas en la sonda
    // `probe_unreachable_rooms`— y saltárselo deja los chunks de una sala tallados EXACTAMENTE igual
    // que en wire 38, que es lo que exige el punto 5 de la enmienda 3.
    //
    // `carved` va como protegido y NO es opcional: en este punto solo contiene los interiores de las
    // salas, que todavía no tienen puerta y por tanto son componentes aisladas. Con la lista vacía,
    // `repair_connectivity` las trata como bolsillos irreparables y las SELLA — pasó de verdad, y
    // las salas incomunicadas subieron de 3 a 6 en la sonda.
    if rooms.len() > 1 {
        super::generator::repair_connectivity(grid, ceiling_corridor, &carved);
    }

    let maze = main_component(grid);
    for plan in rooms.iter() {
        carve_authored_doors(grid, plan, ceiling, &maze, &mut carved);
    }
    carved
}

/// Máscara de la componente transitable MÁS GRANDE del chunk: el laberinto.
///
/// Se calcula con las carcasas ya puestas y antes de ningún túnel, que es el único momento en que
/// dice la verdad: los interiores de las salas ya están huecos —y son componentes propias, mucho más
/// pequeñas— y el macizo de todas las reservas ya está en su sitio.
///
/// Existe porque **una celda abierta no es lo mismo que una celda comunicada**. La reserva de una
/// sala tapia trozos de laberinto, y lo que queda al otro lado puede ser un muñón ciego de una o dos
/// celdas. Un túnel que pare ahí deja la sala incomunicada. Ver `carve_authored_doors`.
fn main_component(grid: &LayerGrid) -> Vec<bool> {
    let n = CHUNK_CELLS * CHUNK_CELLS;
    let mut comp = vec![usize::MAX; n];
    let mut sizes: Vec<usize> = Vec::new();

    for z in 0..CHUNK_CELLS {
        for x in 0..CHUNK_CELLS {
            if !grid.get(x, z).is_walkable() || comp[z * CHUNK_CELLS + x] != usize::MAX {
                continue;
            }
            let id = sizes.len();
            sizes.push(0);
            comp[z * CHUNK_CELLS + x] = id;
            let mut stack = vec![(x, z)];
            while let Some((px, pz)) = stack.pop() {
                sizes[id] += 1;
                for (nx, nz) in [
                    (px.wrapping_sub(1), pz),
                    (px + 1, pz),
                    (px, pz.wrapping_sub(1)),
                    (px, pz + 1),
                ] {
                    if nx < CHUNK_CELLS
                        && nz < CHUNK_CELLS
                        && comp[nz * CHUNK_CELLS + nx] == usize::MAX
                        && grid.get(nx, nz).is_walkable()
                    {
                        comp[nz * CHUNK_CELLS + nx] = id;
                        stack.push((nx, nz));
                    }
                }
            }
        }
    }

    match (0..sizes.len()).max_by_key(|&i| sizes[i]) {
        Some(main) => comp.iter().map(|&c| c == main).collect(),
        None => vec![false; n],
    }
}

/// Cuántas celdas hay que excavar desde `start` en la dirección `dir` para llegar al laberinto, o
/// `None` si no se llega dentro de `limit` o se topa con la costura del chunk.
///
/// Mide contra `maze` —la componente grande— y no contra "la primera celda transitable", que es
/// exactamente el fallo que esto arregla.
fn tunnel_len_to_maze(
    start: (i32, i32),
    dir: (i32, i32),
    limit: usize,
    maze: &[bool],
) -> Option<usize> {
    let (mut x, mut z) = start;
    for step in 0..limit {
        if !LayerGrid::in_bounds(x, z) {
            return None;
        }
        let (ux, uz) = (x as usize, z as usize);
        // Nunca la fila/columna de costura, misma regla que `carve_tunnel_outward`: es de
        // `stitching`, y abrirla aquí descoordinaría los dos chunks que comparten ese borde.
        if ux == 0 || uz == 0 || ux == CHUNK_CELLS - 1 || uz == CHUNK_CELLS - 1 {
            return None;
        }
        if maze[uz * CHUNK_CELLS + ux] {
            return Some(step + 1);
        }
        x += dir.0;
        z += dir.1;
    }
    None
}

/// La misma talla para UNA sala. Atajo para los tests y para quien solo tenga un plan.
pub fn carve_authored_into_grid(
    grid: &mut LayerGrid,
    plan: &AuthoredRoomPlan,
    ceiling: u8,
) -> Vec<(usize, usize)> {
    let mut carved = Vec::new();
    carve_authored_shell(grid, plan, ceiling, &mut carved);
    let maze = main_component(grid);
    carve_authored_doors(grid, plan, ceiling, &maze, &mut carved);
    carved
}

/// Carcasa de una sala: margen macizo, anillo blindado e interior hueco. Sin puertas.
///
/// Orden deliberado —margen, luego anillo, luego interior— para que cada paso pise al anterior
/// donde se toquen.
fn carve_authored_shell(
    grid: &mut LayerGrid,
    plan: &AuthoredRoomPlan,
    ceiling: u8,
    carved: &mut Vec<(usize, usize)>,
) {
    let reserve = plan.reserve_rect();
    let (rx0, rz0, rx1, rz1) = reserve;
    let (fx0, fz0) = (plan.cell_x, plan.cell_z);
    let (fx1, fz1) = (fx0 + plan.cells_x as i32, fz0 + plan.cells_z as i32);

    // Los tres pasos escriben solo donde la forma ASOMA por este chunk (ADR-084: una sala anclada
    // en el vecino se talla aquí a trozos). El reparto anillo/margen/interior sigue decidiéndose
    // contra la forma COMPLETA, así que el trozo que cae aquí es idéntico al que sería si el chunk
    // fuera más grande — no una sala achicada con su propio borde en la frontera.
    if let Some((cx0, cz0, cx1, cz1)) = clip_to_chunk(reserve) {
        // 1. La reserva entera a macizo genérico: eso deja el MARGEN hecho de una vez.
        for x in cx0..cx1 {
            for z in cz0..cz1 {
                grid.set(x, z, Cell::new(CellType::Wall, 0, 0));
            }
        }

        // 2. El anillo, el borde exterior de la reserva, a `SealedWall`. Es lo que impide que
        //    `repair_connectivity` entre a reconectar bolsillos por aquí y agujeree la sala.
        for x in cx0..cx1 {
            for z in cz0..cz1 {
                let (ix, iz) = (x as i32, z as i32);
                let on_ring = ix < rx0 + RING_CELLS as i32
                    || ix >= rx1 - RING_CELLS as i32
                    || iz < rz0 + RING_CELLS as i32
                    || iz >= rz1 - RING_CELLS as i32;
                if on_ring {
                    grid.set(x, z, Cell::new(CellType::SealedWall, 0, 0));
                }
            }
        }
    }

    // 3. Interior hueco. `Open` y no `Corridor`: es una sala, y el render de sala es el que no mete
    //    la geometría estrecha de pasillo dentro.
    carved.reserve(plan.cells_x * plan.cells_z + TUNNEL_LIMIT);
    if let Some((cx0, cz0, cx1, cz1)) = clip_to_chunk((fx0, fz0, fx1, fz1)) {
        for x in cx0..cx1 {
            for z in cz0..cz1 {
                grid.set(x, z, Cell::new(CellType::Open, ceiling, 0));
                carved.push((x, z));
            }
        }
    }
}

/// Las puertas de una sala ya con carcasa: TODAS, una por abertura del prefab. Con una sola, las
/// demás dan contra el margen macizo — se ve el vano y detrás un bloque cerrado.
///
/// Cada vano mide un tile de ancho: se excava la primera línea hasta que engancha y la segunda copia
/// su longitud, para que no salga dentado.
///
/// **El túnel se excava hasta el LABERINTO, no hasta la primera celda transitable.** La diferencia
/// no es sutil y costó un test en rojo: la reserva de una sala tapia trozos de laberinto, y lo que
/// queda al otro lado puede ser un muñón ciego de una o dos celdas que sigue estando "abierto". Un
/// túnel que pare ahí deja la sala incomunicada. Medido con la sonda `probe_unreachable_rooms`: con
/// una sala por chunk salían 0 de 11 incomunicadas —de ahí que el fallo no se viera hasta ahora— y
/// con varias, 3 de 14.
///
/// `repair_connectivity` tampoco lo salva, aunque las celdas vayan protegidas: su BFS solo carva
/// `Wall` interior, nunca `Void`, `Pillar` ni `SealedWall`, y con dos o tres anillos blindados en el
/// mismo chunk se queda sin ruta y sella en vez de reconectar.
///
/// Si NO se llega al laberinto (se topó con la costura, o queda fuera de `TUNNEL_LIMIT`), se excava
/// el borde propio y ya: el muñón entra en el chunk como componente aparte y `repair_connectivity`
/// corre después con estas celdas protegidas para tenderle un pasillo. Es el mismo mecanismo por el
/// que la habitación construible nunca queda aislada.
///
/// Va SEPARADA de la carcasa porque con varias salas hay que poner todas las carcasas antes que
/// ningún túnel — ver `carve_authored_set_into_grid`.
fn carve_authored_doors(
    grid: &mut LayerGrid,
    plan: &AuthoredRoomPlan,
    ceiling: u8,
    maze: &[bool],
    carved: &mut Vec<(usize, usize)>,
) {
    // Dos boquetes del prefab que caigan en el MISMO `(lado, tile)` describen el mismo hueco:
    // `door_starts` les da idénticas celdas de partida y excavarlos dos veces repetiría el tallado y
    // duplicaría esas celdas en `carved`. Con `MAX_DOORWAYS = 8`, un barrido cuadrado sobre un array
    // en pila sale más barato que montar un set en una ruta que se recorre en cada generación de
    // chunk.
    let mut dug_doors = [(0u8, 0u8); MAX_DOORWAYS];
    let mut dug_count = 0usize;
    for door in plan.doorways() {
        if dug_doors[..dug_count].contains(&door) {
            continue;
        }
        dug_doors[dug_count] = door;
        dug_count += 1;

        let (starts, dir) = door_starts(plan, door);
        // Las dos líneas del vano se excavan a la MISMA longitud, medida una vez: así el vano no
        // sale dentado, que es lo que pasaba al medir cada línea por su cuenta.
        let dug = tunnel_len_to_maze(starts[0], dir, TUNNEL_LIMIT, maze).unwrap_or(BORDER_CELLS);
        for start in starts {
            carve_tunnel_fixed(grid, ceiling, start, dir, dug, carved);
        }
    }
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
                    height_meters: 0.0,
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
                    height_meters: 0.0,
                    doorways: vec![ManifestDoorway {
                        side_by_quarter: [0, 2, 1, 3],
                        tile_by_quarter: [5, 4, 4, 5],
                    }],
                },
            ],
        }
    }

    /// La PRIMERA sala del chunk, que es la unica que existia antes de ADR-083 enmienda 3.
    ///
    /// Casi todos los tests de este modulo comprueban propiedades de UNA sala (margen macizo,
    /// anillo sellado, vano de un tile...) y valen igual con varias. Mantenerlos escritos contra la
    /// primera tiene ademas un valor propio: si el paso a plural hubiera movido la primera sala de
    /// sitio, estos tests lo habrian cazado.
    fn first_plan(
        manifest: &RoomManifest,
        world_seed: u64,
        cx: i32,
        cz: i32,
        layer: u8,
        build_room: Option<&RoomPlan>,
    ) -> Option<AuthoredRoomPlan> {
        plan_authored_rooms(manifest, world_seed, cx, cz, layer, build_room)
            .iter()
            .next()
            .copied()
    }

    /// Un rect del plan como ÍNDICES de la rejilla, sin recortar.
    ///
    /// Vale porque toda sala de estos tests cae entera dentro de su chunk. Una sala multi-chunk
    /// (ADR-084) tendría coordenadas negativas y esto entraría en pánico al convertir — que es
    /// justamente el aviso que se quiere si alguien reusa el helper donde no toca.
    fn as_indices(rect: (i32, i32, i32, i32)) -> (usize, usize, usize, usize) {
        (
            usize::try_from(rect.0).expect("rect fuera del chunk"),
            usize::try_from(rect.1).expect("rect fuera del chunk"),
            usize::try_from(rect.2).expect("rect fuera del chunk"),
            usize::try_from(rect.3).expect("rect fuera del chunk"),
        )
    }

    /// Barre chunks hasta encontrar uno con sala, para no depender de que un chunk concreto gane un
    /// sorteo del 1 %.
    fn find_plan(m: &RoomManifest, seed: u64) -> Option<(i32, i32, AuthoredRoomPlan)> {
        for cx in -12..12 {
            for cz in -12..12 {
                if let Some(p) = first_plan(m, seed, cx, cz, 0, None) {
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
                    first_plan(&m, seed, cx, cz, 0, None),
                    first_plan(&m, seed, cx, cz, 0, None)
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
                        assert!(first_plan(&m, seed, cx, cz, layer, None).is_none());
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
                    let Some(p) = first_plan(&m, seed, cx, cz, 0, None) else {
                        continue;
                    };
                    found += 1;
                    let (x0, z0, x1, z1) = p.reserve_rect();
                    assert!(
                        x0 >= USABLE_LO as i32,
                        "reserva pisa la costura oeste: {x0}"
                    );
                    assert!(
                        z0 >= USABLE_LO as i32,
                        "reserva pisa la costura norte: {z0}"
                    );
                    assert!(
                        x1 <= USABLE_HI as i32 + 1,
                        "reserva pisa la costura este: {x1}"
                    );
                    assert!(
                        z1 <= USABLE_HI as i32 + 1,
                        "reserva pisa la costura sur: {z1}"
                    );
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
                    if let Some(p) = first_plan(&m, seed, cx, cz, 0, None) {
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
                    assert!(first_plan(&m, seed, cx, cz, 0, None).is_none());
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
            tile_x: (rx0 / 2) as usize,
            tile_z: (rz0 / 2) as usize,
            door_side: 0,
        };
        assert!(
            first_plan(&m, 42, cx, cz, 0, Some(&build)).is_none(),
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

        // Los planes de estos tests caen enteros dentro del chunk, así que el rect pasa a índice sin
        // recortar. Uno multi-chunk (ADR-084) no lo haría — ver `clip_to_chunk`.
        let (rx0, rz0, rx1, rz1) = as_indices(plan.reserve_rect());
        let (fx0, fz0) = (plan.cell_x as usize, plan.cell_z as usize);
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

        let (rx0, rz0, rx1, rz1) = as_indices(plan.reserve_rect());
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
            let (rx0, rz0, rx1, rz1) = as_indices(plan.reserve_rect());
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

            let (fx0, fz0) = (plan.cell_x as usize, plan.cell_z as usize);
            for x in fx0..fx0 + plan.cells_x {
                for z in fz0..fz0 + plan.cells_z {
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
                height_meters: 0.0,
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

    /// Manifiesto de salas PEQUEÑAS: una de 2 x 2 tiles y una de 1 x 1.
    ///
    /// La aritmetica de ADR-083 enmienda 3: una sala de `T` tiles reserva `2T + 4` celdas, entre dos
    /// reservas van `SEPARATION_CELLS` mas, y la ventana util por eje es de 16. De ahi
    /// `T1 + T2 <= 3`: 2 + 1 entra, 2 + 2 NO. Por eso este manifiesto necesita las dos medidas —
    /// con solo salas de 2 x 2 no habria nunca mas de una por chunk y los tests de multi-sala no
    /// tendrian caso que probar.
    fn small_manifest() -> RoomManifest {
        RoomManifest {
            digest: "test".into(),
            rooms: vec![
                ManifestRoom {
                    index: 0,
                    id: "pequena".into(),
                    tiles_x: 2,
                    tiles_z: 2,
                    height_meters: 0.0,
                    doorways: vec![ManifestDoorway {
                        side_by_quarter: [0, 2, 1, 3],
                        tile_by_quarter: [1, 1, 0, 0],
                    }],
                },
                ManifestRoom {
                    index: 1,
                    id: "diminuta".into(),
                    tiles_x: 1,
                    tiles_z: 1,
                    height_meters: 0.0,
                    doorways: vec![ManifestDoorway {
                        side_by_quarter: [1, 3, 0, 2],
                        tile_by_quarter: [0, 0, 0, 0],
                    }],
                },
            ],
        }
    }

    /// Barre chunks hasta encontrar uno con MAS DE UNA sala.
    fn find_multi(m: &RoomManifest, seed: u64) -> Option<(i32, i32, AuthoredRoomSet)> {
        for cx in -20..20 {
            for cz in -20..20 {
                let set = plan_authored_rooms(m, seed, cx, cz, 0, None);
                if set.len() > 1 {
                    return Some((cx, cz, set));
                }
            }
        }
        None
    }

    /// ADR-083 enmienda 3, verificacion (a) primera mitad: con salas pequenas un chunk aloja VARIAS,
    /// y entre dos reservas queda SIEMPRE al menos un tile.
    ///
    /// No basta con que no se solapen. Dos reservas pegadas dejan sus anillos `SealedWall` espalda
    /// contra espalda, y ese bloque es infranqueable para `repair_connectivity` — una sala cuya
    /// puerta de contra el nace sellada. Ver `SEPARATION_CELLS`.
    #[test]
    fn several_small_rooms_keep_a_tile_between_their_reserves() {
        let m = small_manifest();
        let mut checked = 0;

        for seed in SEEDS {
            for cx in -20..20 {
                for cz in -20..20 {
                    let set = plan_authored_rooms(&m, seed, cx, cz, 0, None);
                    if set.len() < 2 {
                        continue;
                    }
                    checked += 1;

                    let rects: Vec<_> = set.iter().map(|p| p.reserve_rect()).collect();
                    for i in 0..rects.len() {
                        for j in (i + 1)..rects.len() {
                            let (x0, z0, x1, z1) = rects[i];
                            let sep = SEPARATION_CELLS as i32;
                            let grown = (x0 - sep, z0 - sep, x1 + sep, z1 + sep);
                            assert!(
                                !rects_overlap(grown, rects[j]),
                                "seed {seed} ({cx},{cz}): reservas {:?} y {:?} sin el tile de separacion",
                                rects[i],
                                rects[j]
                            );
                        }
                    }
                }
            }
        }

        assert!(
            checked > 0,
            "el barrido no encontro ningun chunk multi-sala"
        );
    }

    /// ADR-083 enmienda 3, verificacion (c): nunca mas de `MAX_AUTHORED_PER_CHUNK`.
    #[test]
    fn the_room_count_per_chunk_is_capped() {
        let m = small_manifest();
        for seed in SEEDS {
            for cx in -20..20 {
                for cz in -20..20 {
                    let set = plan_authored_rooms(&m, seed, cx, cz, 0, None);
                    assert!(
                        set.len() <= MAX_AUTHORED_PER_CHUNK,
                        "seed {seed} chunk ({cx},{cz}): {} salas, el tope es {MAX_AUTHORED_PER_CHUNK}",
                        set.len()
                    );
                    assert_eq!(
                        set.len(),
                        set.iter().count(),
                        "len() y iter() discrepan — el array fijo se ha desincronizado"
                    );
                }
            }
        }
    }

    /// ADR-083 enmienda 3, verificacion (d): mismo seed, mismo conjunto. Es lo que permite que las
    /// dos representaciones del mundo tallen LAS MISMAS salas sin hablarse.
    #[test]
    fn the_whole_set_is_deterministic() {
        let m = small_manifest();
        for seed in SEEDS {
            for cx in -6..6 {
                for cz in -6..6 {
                    assert_eq!(
                        plan_authored_rooms(&m, seed, cx, cz, 0, None),
                        plan_authored_rooms(&m, seed, cx, cz, 0, None)
                    );
                }
            }
        }
    }

    /// ADR-083 enmienda 3, verificacion (a) segunda mitad: con VARIAS salas talladas en el mismo
    /// chunk, TODAS siguen alcanzandose a pie tras `repair_connectivity`.
    ///
    /// Es la propiedad que mas facil se rompe al pasar a plural: la segunda sala se talla sobre un
    /// grid que la primera ya ha llenado de macizo, y su tunel puede morir contra el anillo de la
    /// primera en vez de contra el laberinto.
    #[test]
    fn every_room_of_a_multi_room_chunk_is_reachable() {
        let rules = &LAYER_PROFILES[0];
        let m = small_manifest();
        let (cx, cz, set) = find_multi(&m, 42).expect("algun chunk con mas de una sala");

        let mut out = generate_chunk_layer(rules, 42, (cx, cz), 0, &[]);
        let carved = carve_authored_set_into_grid(
            &mut out.grid,
            &set,
            rules.ceiling_open,
            rules.ceiling_corridor,
        );
        repair_connectivity(&mut out.grid, rules.ceiling_corridor, &carved);

        // Inundacion desde el interior de la PRIMERA sala. Si el chunk esta bien cosido, alcanza el
        // interior de todas las demas.
        let first = set.iter().next().expect("al menos una");
        let mut seen: HashSet<(usize, usize)> = HashSet::new();
        let mut queue = VecDeque::new();
        let start = (first.cell_x as usize, first.cell_z as usize);
        seen.insert(start);
        queue.push_back(start);
        while let Some((x, z)) = queue.pop_front() {
            for (nx, nz) in [
                (x.wrapping_sub(1), z),
                (x + 1, z),
                (x, z.wrapping_sub(1)),
                (x, z + 1),
            ] {
                if nx >= CHUNK_CELLS || nz >= CHUNK_CELLS || seen.contains(&(nx, nz)) {
                    continue;
                }
                if out.grid.get(nx, nz).is_walkable() {
                    seen.insert((nx, nz));
                    queue.push_back((nx, nz));
                }
            }
        }

        for (i, plan) in set.iter().enumerate().skip(1) {
            assert!(
                seen.contains(&(plan.cell_x as usize, plan.cell_z as usize)),
                "la sala {i} de ({cx},{cz}) no se alcanza a pie desde la primera"
            );
        }
    }

    /// REGRESION de la consecuencia medida de ADR-083 enmienda 3, verificacion (e): con el
    /// manifiesto REAL del repo se coloca UNA sala por chunk y nunca dos, porque `room_0` mide
    /// 5 x 5 tiles y `T1 + T2 <= 4` no lo admite con ninguna companera.
    ///
    /// Si algun dia falla es una BUENA noticia: significa que hay salas pequenas en el pool y que
    /// esta enmienda por fin sirve de algo. Actualizar el test entonces, no antes.
    #[test]
    fn the_real_pool_still_places_one_room_per_chunk() {
        let m = manifest(); // 4 x 4 y 10 x 10: la de 4 x 4 reserva 12 celdas, tampoco admite pareja
        for seed in SEEDS {
            for cx in -12..12 {
                for cz in -12..12 {
                    let set = plan_authored_rooms(&m, seed, cx, cz, 0, None);
                    assert!(
                        set.len() <= 1,
                        "seed {seed} chunk ({cx},{cz}): {} salas con un pool que no admite parejas",
                        set.len()
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
                height_meters: 0.0,
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
                if let Some(p) = first_plan(&m, seed, cx, cz, 0, None) {
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
            let room = &m.rooms[*entry as usize];
            let top = top_layer_for_height(room.height_meters);
            let capas = if top == AUTHORED_LAYER {
                "solo la suya".to_string()
            } else {
                format!("invade hasta la {top}")
            };
            println!(
                "  entrada {entry} ({}): {n} salas, {} m de alto, {capas}",
                room.id, room.height_meters
            );
        }

        // Las mas cercanas al origen, en coordenadas de MUNDO. Es lo que hace falta para ir a ver
        // una en un playtest sin recorrer medio kilometro a ciegas.
        let mut near: Vec<(f32, i32, i32, AuthoredRoomPlan)> = Vec::new();
        for cx in -SPAN / 2..SPAN / 2 {
            for cz in -SPAN / 2..SPAN / 2 {
                if let Some(p) = first_plan(&m, seed, cx, cz, 0, None) {
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
                    let Some(p) = first_plan(&m, seed, cx, cz, 0, None) else {
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

    /// LA CUENTA QUE ADR-085 SE EQUIVOCO AL ESCRIBIR, y que su enmienda 2 corrigio. Se invaden las
    /// capas cuya LOSA cae DENTRO de la sala; una losa justo a la altura del techo NO se invade,
    /// porque ES el techo.
    #[test]
    fn a_room_only_invades_the_layers_whose_slab_falls_inside_it() {
        // El caso que la formula original rompia: 4 m es el default de RoomDefinition.heightMeters,
        // y floor(4/4) = 1 le habria quitado a la sala por defecto su propio techo.
        assert_eq!(
            top_layer_for_height(4.0),
            0,
            "la sala de una capa no invade"
        );
        assert_eq!(top_layer_for_height(3.0), 0);
        assert_eq!(top_layer_for_height(6.0), 1);
        // Multiplo exacto: la losa de la capa 2 esta en y=8, que es el techo de esta sala.
        assert_eq!(
            top_layer_for_height(8.0),
            1,
            "8 m no debe invadir la capa 2"
        );
        assert_eq!(top_layer_for_height(10.0), 2);
        assert_eq!(top_layer_for_height(12.0), 2, "el cap, y room_0 mide esto");

        // La capa mas alta NUNCA se invade: es la unica que dibuja techo (ADR-085 punto 5).
        assert_eq!(top_layer_for_height(16.0), MAX_INVADED_LAYER);
        assert_eq!(top_layer_for_height(1000.0), MAX_INVADED_LAYER);
        assert!(
            (MAX_INVADED_LAYER as usize) < LAYER_PROFILES.len() - 1,
            "el cap deja al mundo sin techo"
        );

        // Un manifiesto anterior a ADR-085 no trae el campo: 0 cae en "cabe en su capa", que es
        // exactamente lo que ese manifiesto significaba.
        assert_eq!(top_layer_for_height(0.0), 0);
        assert_eq!(top_layer_for_height(-5.0), 0);
        assert_eq!(top_layer_for_height(f32::NAN), 0);
    }

    /// Una sala alta es la MISMA sala vista desde arriba, no otra. Si la capa 1 la sorteara por su
    /// cuenta caeria en otro sitio y la sala saldria partida en dos.
    #[test]
    fn a_tall_room_lands_on_the_same_spot_in_every_layer_it_occupies() {
        let mut m = manifest();
        m.rooms[0].height_meters = 12.0; // invade las capas 1 y 2

        let (cx, cz, ground) = find_plan(&m, 42).expect("alguna sala");
        assert_eq!(ground.top_layer, 2);

        for layer in 1..=2u8 {
            let up = first_plan(&m, 42, cx, cz, layer, None).expect("la sala sigue ahi arriba");
            assert_eq!(
                (up.cell_x, up.cell_z, up.entry, up.quarter),
                (ground.cell_x, ground.cell_z, ground.entry, ground.quarter),
                "capa {layer}: la sala se movio respecto a la capa 0"
            );
        }
        // Y en la capa 3 no esta: es la del techo del mundo.
        assert!(first_plan(&m, 42, cx, cz, 3, None).is_none());
    }

    /// Una sala que cabe en su capa no debe aparecer en ninguna otra. Es la regresion que mantiene
    /// intacto el mundo de antes de ADR-085.
    #[test]
    fn a_one_layer_room_stays_out_of_the_layers_above() {
        let m = manifest(); // sin altura: todas de una capa
        let (cx, cz, plan) = find_plan(&m, 42).expect("alguna sala");
        assert_eq!(plan.top_layer, 0);
        for layer in 1..4u8 {
            assert!(
                first_plan(&m, 42, cx, cz, layer, None).is_none(),
                "la sala de una capa asoma en la capa {layer}"
            );
        }
    }

    /// La aritmetica que sostiene ADR-084: mirar la misma sala desde otro chunk. Se prueba sola
    /// porque hoy NINGUNA sala del pool pasa de un chunk, asi que el barrido de vecindario no puede
    /// ejercitarla de punta a punta todavia -- y cuando el cap suba, esto ya estara anclado.
    #[test]
    fn a_room_seen_from_the_next_chunk_keeps_its_place_in_the_world() {
        let m = manifest();
        let (cx, cz, plan) = find_plan(&m, 42).expect("alguna sala");

        // La misma sala vista desde el chunk de la derecha: se corre un chunk entero hacia las x
        // negativas, porque lo que se mueve es el observador, no la sala.
        let from_east = plan.shifted_by_chunks(-1, 0);
        assert_eq!(from_east.cell_x, plan.cell_x - CHUNK_CELLS as i32);
        assert_eq!(from_east.cell_z, plan.cell_z);
        assert_eq!(from_east.entry, plan.entry, "el traslado cambio la sala");
        assert_eq!(
            from_east.quarter, plan.quarter,
            "el traslado cambio el giro"
        );

        // Ida y vuelta: volver al chunk de origen devuelve el plan intacto.
        assert_eq!(from_east.shifted_by_chunks(1, 0), plan);

        // Y con el cap de hoy no asoma por ningun vecino, que es justo por lo que este commit no
        // mueve el mundo: la reserva entera cabe en `[1, 18]`.
        assert!(reaches_this_chunk(&plan), "no asoma por su propio chunk");
        for (dx, dz) in [(-1, 0), (1, 0), (0, -1), (0, 1), (1, 1)] {
            assert!(
                !reaches_this_chunk(&plan.shifted_by_chunks(dx, dz)),
                "la sala de ({cx},{cz}) invade el vecino ({dx},{dz}) con el cap de un chunk"
            );
        }
    }

    /// SONDA: cuanto cuesta el barrido de vecindario de ADR-084 punto 3, que es la verificacion (f)
    /// que el ADR exige. Mide las DOS cosas que importan y en este orden:
    ///
    ///   1. `plan_authored_rooms` sola, que es lo que el barrido multiplica por 25;
    ///   2. `generate_chunk_layer` entera, que es lo que de verdad corre en la ruta caliente (la
    ///      colision del jugador via `SimLayoutCache::ensure`, la cache del robapieles y el render).
    ///
    /// La segunda es la que decide: si el barrido es ruido al lado de las 7 fases del maze, no hay
    /// nada que optimizar por mucho que la primera se multiplique.
    ///
    ///     cargo test --manifest-path backend/Cargo.toml probe_neighbour_sweep_cost -- --ignored --nocapture
    #[test]
    #[ignore]
    fn probe_neighbour_sweep_cost() {
        let m = manifest();
        let rules = &LAYER_PROFILES[0];
        const N: i32 = 40; // 1600 chunks

        let t0 = std::time::Instant::now();
        let mut found = 0usize;
        for cx in 0..N {
            for cz in 0..N {
                found += plan_authored_rooms(&m, 42, cx, cz, 0, None).len();
            }
        }
        let plan_ns = t0.elapsed().as_nanos() / (N * N) as u128;

        let t1 = std::time::Instant::now();
        for cx in 0..N {
            for cz in 0..N {
                let _ = generate_chunk_layer(rules, 42, (cx, cz), 0, &[]);
            }
        }
        let gen_ns = t1.elapsed().as_nanos() / (N * N) as u128;

        println!(
            "plan_authored_rooms: {plan_ns} ns/chunk ({found} salas en {} chunks)",
            N * N
        );
        println!("generate_chunk_layer: {gen_ns} ns/chunk");
        println!(
            "el emplazamiento es el {:.1} % de generar un chunk",
            plan_ns as f64 * 100.0 / gen_ns as f64
        );
    }

    /// SONDA TEMPORAL: cuantas salas quedan incomunicadas, con UNA sala por chunk (camino de wire
    /// 38) y con varias. Sirve para saber si el fallo de alcanzabilidad es de esta enmienda o venia
    /// de antes.
    #[test]
    #[ignore]
    fn probe_unreachable_rooms() {
        let rules = &LAYER_PROFILES[0];
        for (label, m) in [
            ("real (4x4 y 10x10)", manifest()),
            ("2x2 + 1x1", small_manifest()),
        ] {
            let (mut chunks, mut rooms, mut bad) = (0, 0, 0);
            for seed in SEEDS {
                for cx in -10..10 {
                    for cz in -10..10 {
                        let set = plan_authored_rooms(&m, seed, cx, cz, 0, None);
                        if set.is_empty() {
                            continue;
                        }
                        chunks += 1;
                        rooms += set.len();

                        let mut out = generate_chunk_layer(rules, seed, (cx, cz), 0, &[]);
                        let carved = carve_authored_set_into_grid(
                            &mut out.grid,
                            &set,
                            rules.ceiling_open,
                            rules.ceiling_corridor,
                        );
                        repair_connectivity(&mut out.grid, rules.ceiling_corridor, &carved);

                        // Componente MAS GRANDE del chunk = el laberinto.
                        let mut comp = vec![usize::MAX; CHUNK_CELLS * CHUNK_CELLS];
                        let mut sizes: Vec<usize> = Vec::new();
                        for z in 0..CHUNK_CELLS {
                            for x in 0..CHUNK_CELLS {
                                if !out.grid.get(x, z).is_walkable()
                                    || comp[z * CHUNK_CELLS + x] != usize::MAX
                                {
                                    continue;
                                }
                                let id = sizes.len();
                                sizes.push(0);
                                let mut stack = vec![(x, z)];
                                comp[z * CHUNK_CELLS + x] = id;
                                while let Some((px, pz)) = stack.pop() {
                                    sizes[id] += 1;
                                    for (nx, nz) in [
                                        (px.wrapping_sub(1), pz),
                                        (px + 1, pz),
                                        (px, pz.wrapping_sub(1)),
                                        (px, pz + 1),
                                    ] {
                                        if nx < CHUNK_CELLS
                                            && nz < CHUNK_CELLS
                                            && comp[nz * CHUNK_CELLS + nx] == usize::MAX
                                            && out.grid.get(nx, nz).is_walkable()
                                        {
                                            comp[nz * CHUNK_CELLS + nx] = id;
                                            stack.push((nx, nz));
                                        }
                                    }
                                }
                            }
                        }
                        let main = (0..sizes.len()).max_by_key(|&i| sizes[i]).unwrap();
                        for p in set.iter() {
                            let (fx, fz) = (p.cell_x as usize, p.cell_z as usize);
                            if comp[fz * CHUNK_CELLS + fx] != main {
                                bad += 1;
                            }
                        }
                    }
                }
            }
            println!("{label}: {chunks} chunks, {rooms} salas, {bad} INCOMUNICADAS");
        }
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
        match first_plan(m, seed, cx, cz, 0, build_plan.as_ref()) {
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
