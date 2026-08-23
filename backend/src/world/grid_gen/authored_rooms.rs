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

/// El primer origen de footprint admisible que además es PAR.
///
/// La paridad no es un detalle: el footprint tiene que caer en frontera de tile de 5 m o la sala
/// queda a caballo entre dos, y `draw_origin` la garantiza sorteando en tiles. Un origen impar no
/// existe, así que el primero utilizable no es `USABLE_LO + BORDER_CELLS` (3) sino el par siguiente.
const FIRST_EVEN_ORIGIN: usize = (USABLE_LO + BORDER_CELLS).div_ceil(2) * 2; // 4

/// Footprint máximo en CELDAS que cabe DENTRO de un chunk: **6 × 6 tiles** (30 m).
///
/// **NO son 7 × 7, y llevaba desde ADR-083 enmienda 2 anunciándose mal.** La cuenta que daba 14
/// celdas —18 útiles menos las 4 del borde— se olvida de la paridad: una sala de 14 celdas reserva
/// las 18 de la ventana entera, así que su único origen posible es el 3, y el 3 es impar. `fits` la
/// aceptaba como candidata, entraba en el barajado y `draw_origin` la descartaba en silencio. Es el
/// modo de fallo exacto contra el que avisa el punto 6 de ADR-083 enmienda 1: una sala que se
/// hornea, se exporta y no aparece nunca sin que nadie diga por qué. Lo fija
/// `the_advertised_cap_is_actually_placeable`.
///
/// Desde ADR-084 esto ya NO es el techo del sistema, es la frontera entre las salas que caben en su
/// chunk y las que no: por debajo de él una sala se sortea en la ventana de siempre y el mundo sale
/// idéntico; por encima, en la ventana extendida. Ver `draw_origin`.
pub const MAX_FOOTPRINT_CELLS: usize = ((USABLE_HI + 1 - BORDER_CELLS - FIRST_EVEN_ORIGIN) / 2) * 2; // 12

/// Cuántos chunks puede abarcar una sala contando el suyo (ADR-084 punto 2): **2 × 2 = 100 m**.
///
/// Acota el vecindario que hay que barrer, y con él el coste: una sala anclada en `(cx, cz)` no
/// puede asomar más allá de `(cx + 1, cz + 1)`, así que un chunk solo tiene que preguntar a cuatro
/// anclas —él y sus vecinos de menor coordenada— para saber qué le invade.
pub const MAX_SPAN_CHUNKS: usize = 2;

/// Última celda que la reserva de una sala multi-chunk puede ocupar, en coordenadas del ANCLA.
///
/// Es `USABLE_HI` del chunk más lejano que la sala alcanza. Las filas de costura intermedias SÍ
/// entran —la sala las tapa y ahí no se abre apertura, que es lo que hace ADR-084 punto 4— pero las
/// dos exteriores (la 0 del ancla y la 19 del último) siguen siendo intocables.
const USABLE_HI_SPAN: usize = USABLE_HI + (MAX_SPAN_CHUNKS - 1) * CHUNK_CELLS; // 38

/// Footprint máximo absoluto, en celdas: **16 × 16 tiles (80 m)**.
///
/// Sale de la misma cuenta que `MAX_FOOTPRINT_CELLS` y con la misma trampa: la aritmética cruda daría
/// 34 celdas (17 × 17), pero a ese tamaño el único origen posible vuelve a ser impar. El redondeo a
/// par de abajo no es cosmético — es lo que hace que el cap que se anuncia sea el cap que se coloca.
pub const MAX_FOOTPRINT_CELLS_MULTI_CHUNK: usize =
    ((USABLE_HI_SPAN + 1 - BORDER_CELLS - FIRST_EVEN_ORIGIN) / 2) * 2; // 32

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

/// La capa más alta que una sala puede llegar a invadir: **todas**, la última incluida.
///
/// ADR-085 punto 5 la excluía porque "es la única que dibuja techo (`roofSlab = isTopLayer`), así
/// que abrirle un hueco dejaría el mundo sin tapa por ahí". **La premisa era falsa ya entonces**:
/// desde ADR-083 enmienda 1 punto 7 el cliente sale por `continue` en todo tile de sala ANTES de
/// pintar nada, y la losa de techo se pinta después de esa guarda — la supresión llevaba dos ADRs
/// implementada, esperando a que alguien dejara llegar una sala hasta ahí. El cap no protegía de
/// nada; solo cortaba salas. Ver ADR-085 enmienda 3.
///
/// Con 4 capas y 4 m de paso, el techo de altura autorable pasa de 12 m a **16 m**, que ya no es
/// una constante de dominio sino todo el mundo vertical que hay.
const MAX_INVADED_LAYER: u8 = (LAYER_PROFILES.len() - 1) as u8;

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
    /// El chunk que ANCLA la sala: el de menor `(cx, cz)` de los que cubre (ADR-084 punto 1).
    ///
    /// INVARIANTE bajo `shifted_by_chunks`: lo que cambia al mirar la sala desde otro chunk es
    /// `cell_x`/`cell_z`, nunca esto. Por eso sirve de IDENTIDAD de la sala, y es lo que el wire 41
    /// manda para que el cliente instancie el prefab una sola vez aunque le llegue en cuatro chunks
    /// (ADR-084 enmienda 1 punto 2).
    pub anchor_cx: i32,
    pub anchor_cz: i32,
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

    /// El mismo tile de origen pero en coordenadas del CHUNK ANCLA, que es lo que viaja por el wire
    /// 41. `(cx, cz)` es el chunk desde el que se está mirando el plan.
    ///
    /// Sale siempre en `0..=17` —la ventana de sorteo de `draw_origin` en tiles—, sea cual sea el
    /// chunk que pregunte. Es lo que permite que los cuatro chunks que cubre una sala manden por el
    /// wire EXACTAMENTE la misma pareja de números, y que el cliente los deduplique por
    /// `(ancla, tile)` sin tener que reconstruir nada.
    pub fn anchor_tile_origin(&self, cx: i32, cz: i32) -> (i32, i32) {
        let n = CHUNK_CELLS as i32;
        (
            (self.cell_x - (self.anchor_cx - cx) * n).div_euclid(2),
            (self.cell_z - (self.anchor_cz - cz) * n).div_euclid(2),
        )
    }

    /// El MISMO plan visto desde el chunk que tiene el ancla `(dx, dz)` chunks más allá.
    ///
    /// El signo importa y engaña: `(dx, dz)` va DEL QUE MIRA AL ANCLA, así que el observador queda
    /// en `ancla − (dx, dz)`. Es la convención de `collect_from_anchor`, que es quien lo usa: allí
    /// `(ax, az) = (cx + dx, cz + dz)`.
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

/// Cuántas salas autoradas ANCLA un chunk como mucho (ADR-083 enmienda 3 punto 4).
///
/// Es tope de BARRIDO, no de diseño: impide que el planificador siga probando candidatas en un
/// chunk donde ya no cabe nada, y pone techo al coste de una ruta que se re-deriva en cada
/// generación de chunk. Por encima de 3 la aritmética de la reserva ya no da con ningún tamaño de
/// sala que valga la pena autorar — ver `AuthoredRoomSet`.
pub const MAX_AUTHORED_PER_CHUNK: usize = 3;

/// Cuántas salas puede VER un chunk: las que ancla él más las que le invaden desde un vecino.
///
/// Son dos topes distintos desde ADR-084 y confundirlos es un fallo de determinismo, no de memoria.
/// Un chunk lo alcanzan como mucho cuatro anclas —él y sus vecinos de menor coordenada, por el cap
/// de 2 × 2 chunks—, cada una con `MAX_AUTHORED_PER_CHUNK` salas. Con capacidad para menos, un chunk
/// tendría que DESCARTAR una sala que otro sí ve, y los dos tallarían mundos distintos: el descarte
/// depende del orden de barrido, y el orden de barrido depende de quién pregunta. Con capacidad para
/// el peor caso completo, ese descarte no puede ocurrir.
pub const MAX_AUTHORED_VISIBLE: usize = MAX_AUTHORED_PER_CHUNK * MAX_SPAN_CHUNKS * MAX_SPAN_CHUNKS;

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
    plans: [Option<AuthoredRoomPlan>; MAX_AUTHORED_VISIBLE],
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
        self.len as usize >= MAX_AUTHORED_VISIBLE
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

/// ¿Cabe esta sala con este giro en el mundo?
///
/// Desde ADR-084 el tope es el de 2 × 2 chunks, no el del chunk: una sala mayor que
/// `MAX_FOOTPRINT_CELLS` sigue siendo colocable, solo que su reserva asoma por el vecino.
fn fits(room: &ManifestRoom, quarter: u8) -> Option<(usize, usize)> {
    let (w, h) = room.footprint_cells(quarter);
    if w == 0
        || h == 0
        || w > MAX_FOOTPRINT_CELLS_MULTI_CHUNK
        || h > MAX_FOOTPRINT_CELLS_MULTI_CHUNK
    {
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
    for dz in -ANCHOR_REACH..=0 {
        for dx in -ANCHOR_REACH..=0 {
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

/// Cuántos chunks hacia +x/+z puede ASOMAR una sala desde su ancla, que es lo mismo que decir hasta
/// dónde hacia −x/−z hay que mirar buscando quién nos invade.
///
/// Sale del cap de 2 × 2 chunks y de que el ancla es siempre la de menor `(cx, cz)` (ADR-084 punto
/// 1): una sala nunca asoma hacia atrás, así que el barrido es de cuatro anclas y no de nueve.
///
/// **Aquí ya no hay ±2, y el ADR lo pedía.** El punto 3 razonaba que la retirada entre anclas
/// necesitaba ese alcance porque la daba por TRANSITIVA. No lo es: `retired_by_lower_anchor` decide
/// con profundidad 1 —una sala retirada sigue ocupando su sitio—, y eso convierte la supervivencia
/// en función solo del ancla y de sus ocho vecinas. El ±2 sigue existiendo, pero DENTRO de esa
/// función y solo para las anclas que de verdad tienen sala, no como barrido de 25 chunks por chunk
/// generado.
const ANCHOR_REACH: i32 = MAX_SPAN_CHUNKS as i32 - 1;

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
        if !reaches_this_chunk(&shifted) {
            continue;
        }
        // La retirada se decide en coordenadas del ANCLA, no en las de quien pregunta: es lo que
        // hace que los cuatro chunks que ven esta sala lleguen todos al mismo veredicto.
        if retired_by_lower_anchor(manifest, world_seed, ax, az, plan) {
            continue;
        }
        placed.push(shifted);
    }
}

/// ¿Se retira esta sala del ancla `(ax, az)` ante otra de un ancla de menor `(cx, cz)`?
///
/// ADR-084 punto 3: dos anclas vecinas pueden querer el mismo sitio y gana la de menor coordenada.
/// El orden es lexicográfico por `(cx, cz)`, la misma convención que la clave canónica de
/// `aperture_pos`, y no el sorteo: un desempate aleatorio dependería de a quién se le pregunte.
///
/// **PROFUNDIDAD 1, y esa es la decisión que hace correcto todo lo demás.** Una sala retirada SIGUE
/// bloqueando el sitio que reclamó. La alternativa —que al retirarse libere su hueco y la siguiente
/// pueda ocuparlo— hace la supervivencia recursiva y de profundidad ilimitada: para saber si C vive
/// habría que saber si B vive, y para eso si vive A, sin un punto donde parar que no sea arbitrario.
/// Con profundidad 1 la supervivencia depende solo del ancla y de sus ocho vecinas, que es una
/// función pura, acotada y —lo único que importa de verdad— la MISMA se pregunte desde el chunk que
/// se pregunte. El precio es un hueco desaprovechado cuando una retirada encadena con otra: cuesta
/// una sala que no sale, no una sala partida por la mitad.
///
/// Solo hace falta mirar a ±1: una reserva vive dentro de 2 × 2 chunks desde su ancla, así que dos
/// reservas que se toquen tienen las anclas a un chunk o menos.
fn retired_by_lower_anchor(
    manifest: &RoomManifest,
    world_seed: u64,
    ax: i32,
    az: i32,
    plan: &AuthoredRoomPlan,
) -> bool {
    for bz in az - 1..=az + 1 {
        for bx in ax - 1..=ax + 1 {
            if (bx, bz) >= (ax, az) {
                continue;
            }
            let rival = plan_anchored_at(
                manifest,
                world_seed,
                bx,
                bz,
                BuildRoomSource::Derive {
                    layer: AUTHORED_LAYER,
                },
            );
            for other in rival.iter() {
                if reserves_conflict(&other.shifted_by_chunks(bx - ax, bz - az), plan) {
                    return true;
                }
            }
        }
    }
    false
}

/// ¿La RESERVA de este plan asoma por el chunk `0..CHUNK_CELLS`? Se mide contra la reserva y no
/// contra el footprint: el margen y el anillo también hay que tallarlos aquí, y una sala cuyo
/// footprint quede justo al otro lado de la frontera sigue teniendo que macizar celdas de este lado.
///
/// **Y de aquí sale, gratis, la simetría que necesita la supresión de costura de ADR-084 punto 4.**
/// Preocupa el caso de una reserva que acabe EXACTAMENTE en la frontera: taparía la celda 19 de este
/// chunk y ninguna del vecino, el vecino no la vería, no suprimiría su apertura, y la costura
/// saldría abierta por un lado y tapiada por el otro. No puede pasar, y lo garantiza `draw_origin`:
/// el origen va de `USABLE_LO + BORDER_CELLS = 3` para arriba y la reserva acaba en `hi_limit + 1`
/// como mucho, así que `x1 ≤ 19` para una sala que cabe en su chunk y `x1 ≥ 22` para una que no
/// —tamaño 16 celdas y origen 4 es el mínimo—. `x1 == 20` no lo produce ningún tamaño. Una reserva
/// que tape una costura la desborda siempre por los dos lados.
fn reaches_this_chunk(plan: &AuthoredRoomPlan) -> bool {
    let n = CHUNK_CELLS as i32;
    rects_overlap(plan.reserve_rect(), (0, 0, n, n))
}

/// ¿Tapa alguna sala la costura que pasa por estas dos celdas, la de acá y la de allá del borde?
///
/// ADR-084 punto 4 (y enmienda 1 punto 1): en un borde que una sala cubre NO se abre apertura — la
/// sala ES la conexión, y su interior cruza la costura por construcción. Sin esto, `stitch_edges`
/// corre ANTES del tallado autorado y `carve_aperture` perfora `SealedWall` una vez a propósito: la
/// apertura quedaría como un boquete determinista en la pared de la sala.
///
/// Se miran las DOS celdas del borde y no solo la propia, porque los dos chunks que comparten la
/// costura tienen que llegar al mismo veredicto y cada uno la nombra con sus coordenadas. Que los
/// dos vean la sala lo garantiza la holgura de `reaches_this_chunk`.
///
/// No aísla el chunk: una reserva empieza en `USABLE_LO` en coordenadas de su ancla, así que jamás
/// puede tapar a la vez las cuatro costuras de un mismo chunk.
pub(super) fn seam_is_covered(rooms: &AuthoredRoomSet, near: (i32, i32), far: (i32, i32)) -> bool {
    let inside =
        |r: (i32, i32, i32, i32), c: (i32, i32)| c.0 >= r.0 && c.0 < r.2 && c.1 >= r.1 && c.1 < r.3;
    rooms
        .iter()
        .any(|p| inside(p.reserve_rect(), near) || inside(p.reserve_rect(), far))
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
        // El tope de COLOCACIÓN, que es el del ancla y no el del chunk que mira. Ver
        // `MAX_AUTHORED_VISIBLE`.
        if placed.len() >= MAX_AUTHORED_PER_CHUNK {
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
            anchor_cx: cx,
            anchor_cz: cz,
            cells_x: w,
            cells_z: h,
            top_layer: top_layer_for_height(room.height_meters),
            doors,
            door_count: room.doorways.len().min(MAX_DOORWAYS) as u8,
        };

        // La construible de CADA chunk que la sala cubre, no solo la del ancla (ADR-084 punto 2 +
        // ADR-081 enmienda 5). Una sala multi-chunk que solo mirase la de su ancla se comería la del
        // vecino, que es una regla de juego validada y gana siempre.
        if overlaps_any_build_room(world_seed, cx, cz, &plan, build_room) {
            continue;
        }
        // Y contra las salas autoradas que ya ganaron sitio en este chunk. Dos reservas que se
        // pisen romperían la garantía que sostiene el sistema entero: el margen macizo de una
        // acabaría siendo el interior hueco de la otra.
        if placed.iter().any(|other| reserves_conflict(other, &plan)) {
            continue;
        }
        placed.push(plan);
    }

    placed
}

/// Sortea el origen del FOOTPRINT en un eje, en celda PAR, o `None` si no cabe.
///
/// La reserva ocupa `size + 2 · BORDER_CELLS` y tiene que caber entera en `[USABLE_LO, hi]`. El
/// origen del footprint queda `BORDER_CELLS` más adentro. Se sortea en unidades de tile y se
/// multiplica por 2, que es la forma barata de garantizar paridad sin descartar tiradas.
///
/// **La ventana se extiende SOLO para las salas que no caben en su chunk**, y eso no es una
/// optimización: es lo que deja el mundo de hoy byte a byte donde estaba. Sortear todas las salas en
/// la ventana de 2 × 2 chunks cambiaría el rango del `gen_range` de cada tirada y movería de sitio
/// hasta la última sala pequeña ya colocada. Con el corte por tamaño, una sala de `≤
/// MAX_FOOTPRINT_CELLS` hace exactamente la misma tirada que en wire 39.
fn draw_origin(rng: &mut StdRng, size: usize) -> Option<i32> {
    let hi_limit = if size <= MAX_FOOTPRINT_CELLS {
        USABLE_HI
    } else {
        USABLE_HI_SPAN
    };
    let reserve = size + 2 * BORDER_CELLS;
    if reserve > hi_limit - USABLE_LO + 1 {
        return None;
    }
    // Primer y último origen de FOOTPRINT admisibles.
    let lo = USABLE_LO + BORDER_CELLS;
    let hi = hi_limit + 1 - BORDER_CELLS - size;
    if hi < lo {
        return None;
    }
    // Solo orígenes pares dentro de [lo, hi]. `FIRST_EVEN_ORIGIN` es esta misma cuenta hecha una
    // vez, y de ahí salen los dos caps: si el cap no la respetara, se anunciaría un tamaño que este
    // `return None` descarta en silencio.
    let first_even = FIRST_EVEN_ORIGIN;
    debug_assert_eq!(first_even, lo.div_ceil(2) * 2);
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
///
/// `(ddx, ddz)` es a cuántos chunks del ancla vive la construible, para poder comparar la de un
/// chunk invadido en las coordenadas del ancla.
fn overlaps_build_room(plan: &AuthoredRoomPlan, build: &RoomPlan, ddx: i32, ddz: i32) -> bool {
    let (bx, bz) = build.cell_origin();
    let (bx, bz) = (
        bx as i32 + ddx * CHUNK_CELLS as i32,
        bz as i32 + ddz * CHUNK_CELLS as i32,
    );
    // El anillo de la construible es la corona de 1 celda alrededor de su footprint.
    let build_reserve = (
        bx - 1,
        bz - 1,
        bx + super::build_rooms::ROOM_CELLS as i32 + 1,
        bz + super::build_rooms::ROOM_CELLS as i32 + 1,
    );

    rects_overlap(plan.reserve_rect(), build_reserve)
}

/// Los chunks que la reserva de esta sala toca, como desplazamiento `(ddx, ddz)` desde el ancla.
///
/// Siempre incluye `(0, 0)`, y como mucho llega a `(1, 1)` por el cap de 2 × 2 chunks. Para una sala
/// que cabe en su chunk devuelve un solo elemento, que es lo que deja el mundo de hoy sin pagar ni
/// una derivación de más.
fn covered_chunks(plan: &AuthoredRoomPlan) -> impl Iterator<Item = (i32, i32)> {
    let n = CHUNK_CELLS as i32;
    let (x0, z0, x1, z1) = plan.reserve_rect();
    let (dx0, dx1) = (x0.div_euclid(n), (x1 - 1).div_euclid(n));
    let (dz0, dz1) = (z0.div_euclid(n), (z1 - 1).div_euclid(n));
    (dz0..=dz1).flat_map(move |dz| (dx0..=dx1).map(move |dx| (dx, dz)))
}

/// ¿Choca la sala con la habitación construible de ALGUNO de los chunks que cubre?
///
/// La del ancla llega ya derivada por el llamador (`anchor_build`), que es quien sabe si tocaba
/// pagarla o le venía dada. Las de los chunks invadidos se derivan aquí, y solo se derivan cuando de
/// verdad hay un chunk invadido: `covered_chunks` devuelve un único elemento para una sala normal.
fn overlaps_any_build_room(
    world_seed: u64,
    cx: i32,
    cz: i32,
    plan: &AuthoredRoomPlan,
    anchor_build: Option<&RoomPlan>,
) -> bool {
    for (ddx, ddz) in covered_chunks(plan) {
        if ddx == 0 && ddz == 0 {
            if anchor_build.is_some_and(|b| overlaps_build_room(plan, b, 0, 0)) {
                return true;
            }
            continue;
        }
        // La construible se sortea SIEMPRE en la capa de anclaje, igual que la sala: es la única
        // capa en la que existe, y preguntarla en otra devolvería `None` y dejaría pasar el solape.
        let other =
            super::build_rooms::room_in_chunk(world_seed, cx + ddx, cz + ddz, AUTHORED_LAYER);
        if other.is_some_and(|b| overlaps_build_room(plan, &b, ddx, ddz)) {
            return true;
        }
    }
    false
}

/// ¿Se pisan las reservas de dos salas autoradas, contando la separación obligatoria?
///
/// Una sola copia de la aritmética para los dos usos que tiene: dos salas de la MISMA ancla y dos
/// de anclas distintas (ADR-084 punto 3). La regla es la misma y por el mismo motivo — dos anillos
/// `SealedWall` espalda contra espalda no los atraviesa `repair_connectivity`, y da igual quién
/// sorteó cada uno. Ver `SEPARATION_CELLS`.
///
/// Se crece UNA de las dos reservas y no las dos: crecer ambas exigiría `SEPARATION_CELLS` celdas de
/// cada lado, o sea el doble de separación de la que el fallo pide.
fn reserves_conflict(a: &AuthoredRoomPlan, b: &AuthoredRoomPlan) -> bool {
    let (x0, z0, x1, z1) = a.reserve_rect();
    let sep = SEPARATION_CELLS as i32;
    rects_overlap((x0 - sep, z0 - sep, x1 + sep, z1 + sep), b.reserve_rect())
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
    /// 20 x 20, que pasa del cap absoluto de 2 x 2 chunks y no debe salir jamas.
    ///
    /// Era de 10 x 10 hasta ADR-084 T3, cuando esa medida paso a ser colocable (multi-chunk) y dejo
    /// de servir como "la que no cabe". Subirla a 20 x 20 no movio ni una sala del mundo de estos
    /// tests: `fits` la rechaza igual que rechazaba la de 10, asi que no entra en `candidates` y el
    /// barajado sale identico. La de 10 x 10 vive ahora en `multi_chunk_manifest`.
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
                    tiles_x: 20,
                    tiles_z: 20,
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

    /// Las salas de un chunk **como las ve produccion**: con la habitacion construible de ese chunk
    /// ya derivada.
    ///
    /// Pasar `None` no significa "no me importa", significa "en este chunk NO hay construible", y es
    /// mentira en el 5 % de los chunks (`ROOM_CHANCE` de ADR-081). Una sonda que mienta ahi planta
    /// salas autoradas encima de la construible, el anillo `SealedWall` de esta las sella, y el
    /// resultado se lee como un fallo del emplazamiento que no existe: asi salia **1 de 11
    /// incomunicadas** que en el mundo real no lo estaba. Los tres llamadores de produccion
    /// (`stitching`, `generator` y el productor del wire) pasan siempre la construible.
    fn rooms_of(m: &RoomManifest, seed: u64, cx: i32, cz: i32) -> AuthoredRoomSet {
        let build = super::super::build_rooms::room_in_chunk(seed, cx, cz, AUTHORED_LAYER);
        plan_authored_rooms(m, seed, cx, cz, AUTHORED_LAYER, build.as_ref())
    }

    /// Las sondas que llaman a `generate_chunk_layer` con un manifiesto DE PRUEBA no pueden convivir
    /// con las que cargan el real.
    ///
    /// `active_manifest()` es un `OnceLock` del PROCESO, y los tests corren en hilos del mismo
    /// proceso: en cuanto `real_manifest_cadence` o `dump_chunk` ponen `ROOM_MANIFEST_ENV`,
    /// `generate_chunk_layer` empieza a tallar `room_0` dentro del bloque que la sonda estaba
    /// midiendo, encima de sus propias salas. No revienta nada — devuelve otro numero. Medido: las
    /// incomunicadas pasaban de 1 de 11 a 5 de 11 segun que sonda hubiera corrido antes.
    ///
    /// Se cae a proposito en vez de avisar: un numero que cambia segun el orden de los tests es peor
    /// que un test rojo, porque se lee como un hallazgo.
    fn require_no_global_manifest(probe: &str) {
        assert!(
            super::super::room_manifest::active_manifest().is_none(),
            "{probe}: otra sonda ya cargo el manifiesto real en el OnceLock del proceso y los \
             numeros saldrian falseados. Lanzala sola:\n    cargo test --manifest-path \
             backend/Cargo.toml --release {probe} -- --ignored --nocapture"
        );
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

    /// La de 20 x 20 tiles (40 celdas) pasa del cap absoluto de 2 x 2 chunks (34). Que no salga
    /// NUNCA es el cap de ADR-084 funcionando, igual que antes era el de ADR-083 enmienda 1.
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
        m.rooms.retain(|r| r.tiles_x == 20);
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
    #[ignore = "sonda contra el manifiesto real del repo: de una en una, con -- --ignored --nocapture"]
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
                // Solo las ANCLADAS aqui. Una sala multi-chunk la ven hasta cuatro chunks y
                // contarla en cada uno multiplica por cuatro la densidad: la cadencia salia de 522 m
                // a 394 m el dia que entro `room_2` sin que hubiera mas salas en el mundo.
                for p in anchored_here(&m, seed, cx, cz) {
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
                for p in anchored_here(&m, seed, cx, cz) {
                    let (wx, wz) = world_center(cx, cz, &p);
                    near.push(((wx * wx + wz * wz).sqrt(), cx, cz, p));
                }
            }
        }
        near.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap());
        println!("\nmas cercanas al origen (centro de la sala, coordenadas de mundo):");
        for (dist, cx, cz, p) in near.iter().take(5) {
            let (wx, wz) = world_center(*cx, *cz, p);
            println!(
                "  x={wx:.1} z={wz:.1}  ({dist:.0} m)  chunk({cx},{cz})  entrada {} giro {}                   {} chunk(s)  puerta {}",
                p.entry,
                p.quarter,
                covered_chunks(p).count(),
                doors_of(p)
            );
        }

        assert!(placed > 0, "el manifiesto real no coloca ni una sala");
    }

    /// Busca una seed que ponga una sala autorada PEGADA AL SPAWN, para poder ir a verla en un
    /// playtest sin cruzar medio kilometro de laberinto. Ignorada por defecto:
    ///
    ///     cargo test --manifest-path backend/Cargo.toml hunt_seed_with_room_near_spawn -- --ignored --nocapture
    #[test]
    #[ignore = "sonda contra el manifiesto real del repo: de una en una, con -- --ignored --nocapture"]
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
        assert_eq!(
            top_layer_for_height(12.0),
            2,
            "el viejo cap; room_0 mide esto"
        );

        // La capa mas alta SI se invade desde ADR-085 enmienda 3: el cliente ya suprimia su losa
        // de techo en todo tile de sala (ADR-083 enmienda 1 punto 7), asi que el cap de 12 m no
        // protegia de nada y solo cortaba salas.
        assert_eq!(
            top_layer_for_height(14.0),
            3,
            "14 m tiene que llegar a la ultima capa"
        );
        assert_eq!(
            MAX_INVADED_LAYER as usize,
            LAYER_PROFILES.len() - 1,
            "todas las capas son invadibles"
        );

        // 16 m clavados NO desbordan: la losa que estaria a y=16 es el techo de la sala, no algo
        // que la corte. Es la formula de la enmienda 2 sosteniendo el nuevo techo.
        assert_eq!(top_layer_for_height(16.0), MAX_INVADED_LAYER);
        assert_eq!(top_layer_for_height(1000.0), MAX_INVADED_LAYER);

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

    /// Manifiesto de ADR-084: una sala de 10 x 10 tiles (20 celdas) que NO cabe en un chunk -- la
    /// reserva pide 24 y solo hay 18 utiles -- y si cabe en 2 x 2. Es la medida de `room_1` y
    /// `room_2`, las dos que llevan fuera del mundo desde que se hornearon.
    fn multi_chunk_manifest() -> RoomManifest {
        RoomManifest {
            digest: "multi".into(),
            rooms: vec![ManifestRoom {
                index: 0,
                id: "big".into(),
                tiles_x: 10,
                tiles_z: 10,
                height_meters: 0.0,
                doorways: vec![ManifestDoorway {
                    side_by_quarter: [0, 2, 1, 3],
                    tile_by_quarter: [5, 4, 4, 5],
                }],
            }],
        }
    }

    /// La celda de origen de un plan en coordenadas de MUNDO, para poder comparar lo que ven dos
    /// chunks distintos de la misma sala.
    fn world_origin(cx: i32, cz: i32, p: &AuthoredRoomPlan) -> (i32, i32) {
        (
            p.cell_x + cx * CHUNK_CELLS as i32,
            p.cell_z + cz * CHUNK_CELLS as i32,
        )
    }

    /// Los chunks que la reserva de un plan toca, en coordenadas de mundo.
    fn covered_world_chunks(cx: i32, cz: i32, p: &AuthoredRoomPlan) -> Vec<(i32, i32)> {
        covered_chunks(p)
            .map(|(ddx, ddz)| (cx + ddx, cz + ddz))
            .collect()
    }

    /// El primer chunk con una sala multi-chunk que de verdad cruce una frontera.
    fn find_multi_chunk_room(m: &RoomManifest) -> Option<(u64, i32, i32, AuthoredRoomPlan)> {
        for seed in SEEDS {
            for cx in -12..12 {
                for cz in -12..12 {
                    for p in plan_authored_rooms(m, seed, cx, cz, 0, None).iter() {
                        if covered_chunks(p).count() > 1 {
                            return Some((seed, cx, cz, *p));
                        }
                    }
                }
            }
        }
        None
    }

    /// ADR-084 punto 2: el cap sube a 2 x 2 chunks, y una sala de 10 x 10 tiles -- que llevaba fuera
    /// del mundo desde que se horneo -- se coloca y cruza una frontera de chunk.
    #[test]
    fn a_room_bigger_than_a_chunk_is_placed_and_crosses_a_border() {
        let m = multi_chunk_manifest();
        let (_seed, _cx, _cz, plan) =
            find_multi_chunk_room(&m).expect("ninguna sala de 10x10 cruzo un chunk");
        let (x0, z0, x1, z1) = plan.reserve_rect();
        assert!(
            x1 > CHUNK_CELLS as i32 || z1 > CHUNK_CELLS as i32,
            "la reserva ({x0},{z0})..({x1},{z1}) no sale del chunk ancla"
        );
    }

    /// **La propiedad que sostiene ADR-084 entero.** Los cuatro chunks que una sala puede cubrir
    /// tienen que verla en el MISMO sitio del mundo, cada uno en sus coordenadas. Si uno solo
    /// discrepa, ese chunk talla su trozo en otro lado y la sala sale partida.
    #[test]
    fn every_chunk_a_room_covers_sees_it_in_the_same_place() {
        let m = multi_chunk_manifest();
        let mut checked = 0;
        for seed in SEEDS {
            for cx in -8..8 {
                for cz in -8..8 {
                    for p in plan_authored_rooms(&m, seed, cx, cz, 0, None).iter() {
                        let want = world_origin(cx, cz, p);
                        for (ocx, ocz) in covered_world_chunks(cx, cz, p) {
                            let seen = plan_authored_rooms(&m, seed, ocx, ocz, 0, None);
                            let found = seen
                                .iter()
                                .any(|q| world_origin(ocx, ocz, q) == want && q.entry == p.entry);
                            assert!(
                                found,
                                "el chunk ({ocx},{ocz}) no ve la sala que ({cx},{cz}) situa en \
                                 {want:?} (seed {seed})"
                            );
                            checked += 1;
                        }
                    }
                }
            }
        }
        assert!(checked > 0, "el barrido no encontro ni una sala");
    }

    /// Las salas SUPERVIVIENTES de dos anclas vecinas nunca se tocan.
    ///
    /// Es la consecuencia observable de ADR-084 punto 3, y se sostiene precisamente por la
    /// profundidad 1: si dos supervivientes chocaran, la de mayor `(cx, cz)` se habria retirado ante
    /// la otra -- viva o no la otra, que es lo que hace la regla local. Dos reservas de sala
    /// autorada que se pisen ponen el margen macizo de una dentro del interior hueco de la otra.
    #[test]
    fn surviving_rooms_of_neighbouring_anchors_never_touch() {
        let m = multi_chunk_manifest();
        let survivors = |seed: u64, ax: i32, az: i32| -> Vec<AuthoredRoomPlan> {
            plan_anchored_at(
                &m,
                seed,
                ax,
                az,
                BuildRoomSource::Derive {
                    layer: AUTHORED_LAYER,
                },
            )
            .iter()
            .filter(|p| !retired_by_lower_anchor(&m, seed, ax, az, p))
            .copied()
            .collect()
        };

        let mut pairs = 0;
        for seed in SEEDS {
            for ax in -30..30 {
                for az in -30..30 {
                    let mine = survivors(seed, ax, az);
                    if mine.is_empty() {
                        continue;
                    }
                    for bz in az - 1..=az + 1 {
                        for bx in ax - 1..=ax + 1 {
                            if (bx, bz) == (ax, az) {
                                continue;
                            }
                            for other in survivors(seed, bx, bz) {
                                let shifted = other.shifted_by_chunks(bx - ax, bz - az);
                                for a in &mine {
                                    assert!(
                                        !reserves_conflict(&shifted, a),
                                        "las salas de ({ax},{az}) y ({bx},{bz}) se tocan con la \
                                         seed {seed}: {:?} y {:?}",
                                        a.reserve_rect(),
                                        shifted.reserve_rect()
                                    );
                                    pairs += 1;
                                }
                            }
                        }
                    }
                }
            }
        }
        assert!(pairs > 0, "ninguna pareja de anclas vecinas tuvo salas");
    }

    /// Que la retirada de ADR-084 punto 3 SE DISPARE de verdad en el mundo, y no sea una rama que
    /// nadie recorre. Un test que solo comprobara la invariante pasaria igual si la regla no
    /// existiera y las salas nunca chocaran.
    #[test]
    fn rooms_that_want_the_same_spot_do_retire() {
        let m = multi_chunk_manifest();
        let (mut retired, mut kept) = (0, 0);
        for seed in SEEDS {
            for ax in -30..30 {
                for az in -30..30 {
                    let set = plan_anchored_at(
                        &m,
                        seed,
                        ax,
                        az,
                        BuildRoomSource::Derive {
                            layer: AUTHORED_LAYER,
                        },
                    );
                    for p in set.iter() {
                        if retired_by_lower_anchor(&m, seed, ax, az, p) {
                            retired += 1;
                        } else {
                            kept += 1;
                        }
                    }
                }
            }
        }
        assert!(kept > 0, "no sobrevivio ni una sala");
        assert!(
            retired > 0,
            "ninguna de las {} salas del barrido se retiro: la regla no se probo",
            kept + retired
        );
    }

    /// ADR-081 enmienda 5 con el cap subido: la construible gana el solape TAMBIEN cuando es la del
    /// chunk invadido, no solo la del ancla. Sin esto una sala multi-chunk se comeria una
    /// habitacion construible del vecino, que es una regla de juego validada.
    #[test]
    fn the_build_room_of_an_invaded_chunk_also_wins() {
        let m = multi_chunk_manifest();
        let mut checked = 0;
        for seed in SEEDS {
            for cx in -8..8 {
                for cz in -8..8 {
                    for p in plan_authored_rooms(&m, seed, cx, cz, 0, None).iter() {
                        for (ocx, ocz) in covered_world_chunks(cx, cz, p) {
                            let Some(build) = super::super::build_rooms::room_in_chunk(
                                seed,
                                ocx,
                                ocz,
                                AUTHORED_LAYER,
                            ) else {
                                continue;
                            };
                            assert!(
                                !overlaps_build_room(p, &build, ocx - cx, ocz - cz),
                                "la sala de ({cx},{cz}) piso la construible de ({ocx},{ocz})"
                            );
                            checked += 1;
                        }
                    }
                }
            }
        }
        assert!(checked > 0, "ninguna sala cubrio un chunk con construible");
    }

    /// Las dos filas de costura EXTERIORES siguen siendo intocables aunque la sala cruce chunks: las
    /// intermedias las tapa la sala (ADR-084 punto 4), pero por las de fuera se entra al mundo.
    #[test]
    fn a_multi_chunk_reserve_never_touches_the_outer_seam() {
        let m = multi_chunk_manifest();
        for seed in SEEDS {
            for cx in -8..8 {
                for cz in -8..8 {
                    for p in plan_authored_rooms(&m, seed, cx, cz, 0, None).iter() {
                        // Contra el ancla, que es donde el sorteo decidio la reserva.
                        let anchor = covered_world_chunks(cx, cz, p)[0];
                        let a = p.shifted_by_chunks(cx - anchor.0, cz - anchor.1);
                        let (x0, z0, x1, z1) = a.reserve_rect();
                        assert!(x0 >= USABLE_LO as i32 && z0 >= USABLE_LO as i32);
                        assert!(x1 <= USABLE_HI_SPAN as i32 + 1);
                        assert!(z1 <= USABLE_HI_SPAN as i32 + 1);
                        // Y NUNCA acaba justo en la frontera. De esto depende que los dos chunks de
                        // una costura vean la misma sala y la supriman los dos — ver
                        // `reaches_this_chunk`.
                        assert_ne!(x1, CHUNK_CELLS as i32, "la reserva muere en la frontera x");
                        assert_ne!(z1, CHUNK_CELLS as i32, "la reserva muere en la frontera z");
                    }
                }
            }
        }
    }

    /// Wire 41: los chunks que cubre una sala mandan TODOS la misma tupla `(tile, ancla)`.
    ///
    /// Es lo que permite que el cliente deduplique por `(ancla, tile)` e instancie el prefab una
    /// sola vez. Si dos chunks mandaran tiles distintos, el cliente veria dos salas y las pondria
    /// las dos, superpuestas y descuadradas.
    #[test]
    fn every_chunk_sends_the_same_anchor_relative_tile() {
        let m = multi_chunk_manifest();
        let mut checked = 0;
        for seed in SEEDS {
            for cx in -8..8 {
                for cz in -8..8 {
                    for p in rooms_of(&m, seed, cx, cz).iter() {
                        let want = (p.anchor_tile_origin(cx, cz), p.anchor_cx, p.anchor_cz);
                        // Y en la ventana de sorteo: el cliente lo suma a un desplazamiento de
                        // chunk, asi que un tile fuera de rango saldria como una sala desplazada.
                        assert!((0..=17).contains(&want.0 .0) && (0..=17).contains(&want.0 .1));

                        for (ocx, ocz) in covered_world_chunks(cx, cz, p) {
                            let seen = rooms_of(&m, seed, ocx, ocz);
                            let same = seen.iter().any(|q| {
                                (q.anchor_tile_origin(ocx, ocz), q.anchor_cx, q.anchor_cz) == want
                                    && q.entry == p.entry
                                    && q.quarter == p.quarter
                            });
                            assert!(
                                same,
                                "el chunk ({ocx},{ocz}) manda otra tupla que ({cx},{cz}) para la \
                                 sala anclada en ({},{})",
                                p.anchor_cx, p.anchor_cz
                            );
                            checked += 1;
                        }
                    }
                }
            }
        }
        assert!(checked > 0, "el barrido no encontro ni una sala");
    }

    /// El ancla es INVARIANTE al mirar la sala desde otro chunk: es lo que la hace servir de
    /// identidad. Lo que se mueve es `cell_x`/`cell_z`.
    #[test]
    fn the_anchor_survives_being_looked_at_from_another_chunk() {
        let m = manifest();
        let (cx, cz, plan) = find_plan(&m, 42).expect("alguna sala");
        assert_eq!((plan.anchor_cx, plan.anchor_cz), (cx, cz));
        // `shifted_by_chunks(dx, dz)` lo que mueve es el OBSERVADOR, y en sentido contrario: el
        // chunk que mira queda en `ancla - (dx, dz)`. Es la misma convencion de
        // `collect_from_anchor`, donde `(dx, dz)` va del que consulta AL ancla.
        for (dx, dz) in [(-1, 0), (1, 0), (0, -1), (2, 2)] {
            let moved = plan.shifted_by_chunks(dx, dz);
            assert_eq!((moved.anchor_cx, moved.anchor_cz), (cx, cz));
            assert_eq!(
                moved.anchor_tile_origin(cx - dx, cz - dz),
                plan.anchor_tile_origin(cx, cz),
                "el tile de ancla cambio al mirar la sala desde ({dx},{dz})"
            );
        }
    }

    /// Cuantas costuras de un chunk tapa alguna sala, barriendo TODAS las filas de borde y no solo
    /// la que el sorteo elige. Devuelve `(tapadas, desacuerdos)`.
    ///
    /// El desacuerdo es lo que de verdad se persigue: los dos chunks que comparten una costura la
    /// nombran con coordenadas distintas y tienen que llegar al mismo veredicto. Si uno suprime y el
    /// otro no, sale un pasillo abierto por un lado y tapiado por el otro.
    fn seam_verdicts(m: &RoomManifest) -> (usize, usize) {
        let n = CHUNK_CELLS as i32;
        let (mut covered, mut disagreements) = (0, 0);
        for seed in SEEDS {
            for cx in -8..8 {
                for cz in -8..8 {
                    let here = rooms_of(m, seed, cx, cz);
                    let east = rooms_of(m, seed, cx + 1, cz);
                    let north = rooms_of(m, seed, cx, cz + 1);
                    for p in 1..n - 1 {
                        for (a, b) in [
                            (
                                seam_is_covered(&here, (n - 1, p), (n, p)),
                                seam_is_covered(&east, (0, p), (-1, p)),
                            ),
                            (
                                seam_is_covered(&here, (p, n - 1), (p, n)),
                                seam_is_covered(&north, (p, 0), (p, -1)),
                            ),
                        ] {
                            if a != b {
                                disagreements += 1;
                            }
                            if a {
                                covered += 1;
                            }
                        }
                    }
                }
            }
        }
        (covered, disagreements)
    }

    /// ADR-084 punto 4: los dos chunks que comparten una costura deciden lo MISMO sobre si una sala
    /// la tapa. Es lo que sostiene la holgura de una celda de `reaches_this_chunk`: una reserva puede
    /// acabar justo en la frontera y entonces solo uno de los dos la veria.
    #[test]
    fn both_sides_of_a_seam_agree_on_whether_a_room_covers_it() {
        let (covered, disagreements) = seam_verdicts(&multi_chunk_manifest());
        assert_eq!(disagreements, 0, "costuras con veredicto distinto por lado");
        assert!(
            covered > 0,
            "ninguna sala tapo una costura: la supresion no se probo"
        );
    }

    /// Y con el contenido de hoy NINGUNA costura se suprime, que es por lo que este trozo no mueve
    /// el mundo: una sala que cabe en su chunk reserva dentro de `[1, 18]` y las filas de costura
    /// son la 0 y la 19.
    #[test]
    fn a_room_that_fits_in_its_chunk_never_covers_a_seam() {
        let (covered, disagreements) = seam_verdicts(&manifest());
        assert_eq!(disagreements, 0);
        assert_eq!(covered, 0, "una sala de un solo chunk tapo una costura");
    }

    /// EL CAP QUE SE ANUNCIA TIENE QUE SER COLOCABLE.
    ///
    /// `fits` acepta hasta `MAX_FOOTPRINT_CELLS_MULTI_CHUNK`, pero quien decide de verdad es
    /// `draw_origin`, y ahi hay dos condiciones mas: el origen empieza en `USABLE_LO + BORDER_CELLS`
    /// y tiene que ser PAR. Una sala del tamano del cap que no tenga ni un origen legal se acepta
    /// como candidata, entra en el barajado, y despues se descarta en silencio — que es exactamente
    /// el modo de fallo contra el que avisa el punto 6 de ADR-083 enmienda 1: una sala que se hornea,
    /// se exporta, y no aparece nunca sin que nadie diga por que.
    #[test]
    fn the_advertised_cap_is_actually_placeable() {
        let mut rng = StdRng::seed_from_u64(1);
        assert!(
            draw_origin(&mut rng, MAX_FOOTPRINT_CELLS_MULTI_CHUNK).is_some(),
            "el cap multi-chunk de {} celdas no tiene ni un origen legal",
            MAX_FOOTPRINT_CELLS_MULTI_CHUNK
        );
        assert!(
            draw_origin(&mut rng, MAX_FOOTPRINT_CELLS).is_some(),
            "el cap de un chunk de {MAX_FOOTPRINT_CELLS} celdas no tiene ni un origen legal"
        );
        // Y todo tamano PAR por debajo del cap tambien: los footprints salen de tiles, asi que
        // siempre son pares, y un hueco en medio del rango seria igual de silencioso.
        for size in (2..=MAX_FOOTPRINT_CELLS_MULTI_CHUNK).step_by(2) {
            assert!(
                draw_origin(&mut rng, size).is_some(),
                "una sala de {size} celdas ({} tiles) no tiene origen legal",
                size / 2
            );
        }
    }

    /// Las salas que este chunk ANCLA, sin las que le invaden desde un vecino.
    ///
    /// **Contar salas sin este filtro multiplica por cuatro.** Desde ADR-084 una sala aparece en el
    /// conjunto de todos los chunks que cubre, así que un barrido que sume `plan_authored_rooms`
    /// chunk a chunk cuenta cada sala multi-chunk hasta cuatro veces. La cadencia real del mundo
    /// pasó de 522 m a 394 m el día que entró `room_2`, sin que hubiera ni una sala más.
    ///
    /// El ancla es el chunk de menor `(cx, cz)` de los que cubre, y como la reserva empieza siempre
    /// en coordenadas positivas dentro de él, es el primero que devuelve `covered_chunks`.
    fn anchored_here(
        m: &RoomManifest,
        seed: u64,
        cx: i32,
        cz: i32,
    ) -> impl Iterator<Item = AuthoredRoomPlan> {
        rooms_of(m, seed, cx, cz)
            .iter()
            .filter(|p| covered_chunks(p).next() == Some((0, 0)))
            .copied()
            .collect::<Vec<_>>()
            .into_iter()
    }

    /// El CENTRO de una sala en coordenadas de mundo, que es a donde hay que ir para verla.
    fn world_center(cx: i32, cz: i32, p: &AuthoredRoomPlan) -> (f32, f32) {
        let side = CHUNK_CELLS as f32 * super::super::CELL_SIZE_M;
        (
            cx as f32 * side
                + (p.cell_x as f32 + p.cells_x as f32 / 2.0) * super::super::CELL_SIZE_M,
            cz as f32 * side
                + (p.cell_z as f32 + p.cells_z as f32 / 2.0) * super::super::CELL_SIZE_M,
        )
    }

    /// Por donde se entra a una sala, en texto.
    fn doors_of(p: &AuthoredRoomPlan) -> String {
        let sides: Vec<&str> = p
            .doorways()
            .map(|(s, _)| ["sur(-z)", "norte(+z)", "oeste(-x)", "este(+x)"][s as usize])
            .collect();
        sides.join("+")
    }

    /// SONDA: **el mapa de salas** — donde esta cada una, en coordenadas de mundo, hasta el radio que
    /// se le pida. Es la sonda para ir a ver salas a un playtest sin buscarlas a ciegas.
    ///
    /// Reparte por bandas de distancia en vez de listar las N mas cercanas, que es lo que hace
    /// `real_manifest_cadence`: para comprobar que el emplazamiento se comporta igual lejos que
    /// cerca hacen falta sitios repartidos, no un racimo alrededor del spawn.
    ///
    /// La seed por defecto es 42 porque es la del backend (`WORLD_SEED` en `main.rs`); si la partida
    /// arrancó con otra, hay que pasarla o las coordenadas no valen para nada.
    ///
    ///     PROBE_SEED=42 PROBE_RADIUS_M=10000 PROBE_PER_BAND=4 cargo test --manifest-path backend/Cargo.toml --release probe_room_map -- --ignored --nocapture
    #[test]
    #[ignore = "sonda contra el manifiesto real del repo: de una en una, con -- --ignored --nocapture"]
    fn probe_room_map() {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../Assets/StreamingAssets/room_manifest.json"
        );
        let m = crate::world::grid_gen::load_manifest(std::path::Path::new(path))
            .expect("manifiesto del repo");

        let num = |k: &str, d: f32| -> f32 {
            std::env::var(k)
                .ok()
                .and_then(|v| v.parse().ok())
                .unwrap_or(d)
        };
        let seed: u64 = std::env::var("PROBE_SEED")
            .ok()
            .and_then(|v| v.parse().ok())
            .unwrap_or(42);
        let radius = num("PROBE_RADIUS_M", 10_000.0);
        let per_band = num("PROBE_PER_BAND", 4.0) as usize;

        let side = CHUNK_CELLS as f32 * super::super::CELL_SIZE_M;
        let span = (radius / side).ceil() as i32;

        let mut found: Vec<(f32, i32, i32, AuthoredRoomPlan)> = Vec::new();
        for cx in -span..=span {
            for cz in -span..=span {
                for p in anchored_here(&m, seed, cx, cz) {
                    let (wx, wz) = world_center(cx, cz, &p);
                    let d = (wx * wx + wz * wz).sqrt();
                    if d <= radius {
                        found.push((d, cx, cz, p));
                    }
                }
            }
        }
        found.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap());

        let chunks = ((2 * span + 1) as f64).powi(2);
        println!(
            "seed {seed} — {} salas en {radius:.0} m de radio ({chunks:.0} chunks barridos, \
             {:.1} km2)",
            found.len(),
            chunks * (side * side) as f64 / 1_000_000.0
        );
        println!(
            "  y = {:.1} en la capa 0 (PLAYER_BASE_Y); la sala se ancla ahi aunque sea mas alta\n",
            crate::world::collision::PLAYER_BASE_Y
        );

        const BANDS: [f32; 9] = [
            0.0, 250.0, 500.0, 1000.0, 2000.0, 3000.0, 5000.0, 7500.0, 10_000.0,
        ];
        for w in BANDS.windows(2) {
            let (lo, hi) = (w[0], w[1]);
            if lo >= radius {
                break;
            }
            let band: Vec<_> = found.iter().filter(|(d, ..)| *d >= lo && *d < hi).collect();
            println!("── {lo:.0}–{hi:.0} m ── {} salas", band.len());
            // Repartidas DENTRO de la banda y no las primeras: las primeras de cada banda salen
            // todas pegadas a su borde interior y no enseñan nada que no enseñara la banda anterior.
            let step = (band.len() / per_band.max(1)).max(1);
            for (d, cx, cz, p) in band.iter().step_by(step).take(per_band) {
                let (wx, wz) = world_center(*cx, *cz, p);
                let room = &m.rooms[p.entry as usize];
                println!(
                    "   x={wx:>9.1}  z={wz:>9.1}   ({d:>6.0} m)  chunk({cx},{cz})  \
                     {} {}x{}t  {} chunk(s)  giro {}  puerta {}",
                    room.id,
                    room.tiles_x,
                    room.tiles_z,
                    covered_chunks(p).count(),
                    p.quarter,
                    doors_of(p)
                );
            }
            println!();
        }

        // Cuantas de cada entrada, y cuantas CRUZAN de chunk. Es lo que dice de un vistazo si el
        // multi-chunk esta encendido de verdad o el pool solo tiene salas que caben en su chunk; y
        // una entrada con CERO salas es una que se horneo, se exporto y no aparece en el mundo, que
        // es el fallo silencioso contra el que avisa el punto 6 de ADR-083 enmienda 1.
        let mut by_entry: std::collections::BTreeMap<u16, (usize, usize)> = Default::default();
        for (_, _, _, p) in &found {
            let e = by_entry.entry(p.entry).or_default();
            e.0 += 1;
            if covered_chunks(p).count() > 1 {
                e.1 += 1;
            }
        }
        println!("── por entrada ──");
        for (i, room) in m.rooms.iter().enumerate() {
            match by_entry.get(&(i as u16)) {
                Some((n, multi)) => println!(
                    "   {i} {} ({}x{}t): {n} salas, {multi} cruzan de chunk",
                    room.id, room.tiles_x, room.tiles_z
                ),
                None => println!(
                    "   {i} {} ({}x{}t): NINGUNA — no cabe en 2x2 chunks",
                    room.id, room.tiles_x, room.tiles_z
                ),
            }
        }

        assert!(
            !found.is_empty(),
            "el manifiesto real no coloca ni una sala"
        );
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
    #[ignore = "sonda contra el manifiesto real del repo: de una en una, con -- --ignored --nocapture"]
    fn probe_neighbour_sweep_cost() {
        require_no_global_manifest("probe_neighbour_sweep_cost");
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

    /// SONDA: cuantas salas quedan incomunicadas. Es la verificacion (e) de ADR-084 y la que ya cazo
    /// las salas selladas de ADR-083 enmienda 3.
    ///
    /// **Mide sobre un bloque de 3 x 3 chunks, no sobre uno solo, y desde ADR-084 no vale de otra
    /// forma.** Una sala multi-chunk tiene el interior repartido y las puertas pueden caer todas en
    /// el chunk de al lado: en la rejilla de un chunk suelto su trozo es una componente aislada
    /// aunque en el mundo se cruce andando. Medirlo por chunk daria incomunicadas que no lo son.
    /// Los chunks pegan sin costura porque la celda 19 de uno y la 0 del siguiente son vecinas en el
    /// mundo y `stitch_edges` las abre en la misma fila por clave canonica.
    ///
    ///     cargo test --manifest-path backend/Cargo.toml probe_unreachable_rooms -- --ignored --nocapture
    #[test]
    #[ignore = "sonda contra el manifiesto real del repo: de una en una, con -- --ignored --nocapture"]
    fn probe_unreachable_rooms() {
        require_no_global_manifest("probe_unreachable_rooms");
        // 5 y no 3: una sala de 2 x 2 chunks con la puerta en su lado lejano excava el tunel hacia
        // fuera, y con un bloque justo se saldria del mapa y se leeria como incomunicada. Dos chunks
        // de margen por lado cubren el tunel mas largo (`TUNNEL_LIMIT` = medio chunk).
        const B: usize = 5; // chunks por lado del bloque
        const W: usize = B * CHUNK_CELLS;
        const CENTRE: i32 = (B / 2) as i32;
        let rules = &LAYER_PROFILES[0];

        // El manifiesto REAL del repo va el primero: es el unico contenido que llega al juego, y es
        // el que puede tener una sala grande con las puertas repartidas entre chunks.
        let real = crate::world::grid_gen::load_manifest(std::path::Path::new(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../Assets/StreamingAssets/room_manifest.json"
        )))
        .expect("manifiesto del repo");

        for (label, m) in [
            ("REAL (el del repo)", real),
            ("real (4x4)", manifest()),
            ("2x2 + 1x1", small_manifest()),
            ("multi-chunk (10x10)", multi_chunk_manifest()),
        ] {
            let (mut chunks, mut rooms, mut bad) = (0, 0, 0);
            for seed in SEEDS {
                for cx in -20..20 {
                    for cz in -20..20 {
                        // Solo las salas ANCLADAS aqui: las que invaden desde un vecino ya se
                        // cuentan cuando le toque el turno a su propio ancla.
                        let own: Vec<_> = anchored_here(&m, seed, cx, cz).collect();
                        if own.is_empty() {
                            continue;
                        }
                        chunks += 1;
                        rooms += own.len();

                        // El bloque, con el chunk ancla en el centro.
                        let mut walk = vec![false; W * W];
                        for bz in 0..B {
                            for bx in 0..B {
                                let (gx, gz) = (cx + bx as i32 - CENTRE, cz + bz as i32 - CENTRE);
                                let set = rooms_of(&m, seed, gx, gz);
                                let mut out = generate_chunk_layer(rules, seed, (gx, gz), 0, &[]);
                                if !set.is_empty() {
                                    let carved = carve_authored_set_into_grid(
                                        &mut out.grid,
                                        &set,
                                        rules.ceiling_open,
                                        rules.ceiling_corridor,
                                    );
                                    repair_connectivity(
                                        &mut out.grid,
                                        rules.ceiling_corridor,
                                        &carved,
                                    );
                                }
                                for z in 0..CHUNK_CELLS {
                                    for x in 0..CHUNK_CELLS {
                                        walk[(bz * CHUNK_CELLS + z) * W + bx * CHUNK_CELLS + x] =
                                            out.grid.get(x, z).is_walkable();
                                    }
                                }
                            }
                        }

                        // Componente MAS GRANDE del bloque = el laberinto.
                        let mut comp = vec![usize::MAX; W * W];
                        let mut sizes: Vec<usize> = Vec::new();
                        for z in 0..W {
                            for x in 0..W {
                                if !walk[z * W + x] || comp[z * W + x] != usize::MAX {
                                    continue;
                                }
                                let id = sizes.len();
                                sizes.push(0);
                                let mut stack = vec![(x, z)];
                                comp[z * W + x] = id;
                                while let Some((px, pz)) = stack.pop() {
                                    sizes[id] += 1;
                                    for (nx, nz) in [
                                        (px.wrapping_sub(1), pz),
                                        (px + 1, pz),
                                        (px, pz.wrapping_sub(1)),
                                        (px, pz + 1),
                                    ] {
                                        if nx < W
                                            && nz < W
                                            && comp[nz * W + nx] == usize::MAX
                                            && walk[nz * W + nx]
                                        {
                                            comp[nz * W + nx] = id;
                                            stack.push((nx, nz));
                                        }
                                    }
                                }
                            }
                        }
                        let main = (0..sizes.len()).max_by_key(|&i| sizes[i]).unwrap();
                        for p in &own {
                            let (fx, fz) = (
                                (CENTRE * CHUNK_CELLS as i32 + p.cell_x) as usize,
                                (CENTRE * CHUNK_CELLS as i32 + p.cell_z) as usize,
                            );
                            if comp[fz * W + fx] != main {
                                bad += 1;
                                println!(
                                    "  INCOMUNICADA: seed {seed} chunk ({cx},{cz}) entrada \
                                     {} giro {} en celda ({},{})",
                                    p.entry, p.quarter, p.cell_x, p.cell_z
                                );
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
    #[ignore = "sonda contra el manifiesto real del repo: de una en una, con -- --ignored --nocapture"]
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
