//! ADR-100 paso 2 — EL RELLENO: convertir un plan en geometría, sin que la geometría decida nada.
//!
//! # El cambio de papel, en una línea
//!
//! El compositor preguntaba «¿dónde debería aparecer la siguiente pieza?» y la respuesta salía de la
//! boca de la anterior. Aquí la pregunta es **«¿qué representa este espacio?»**, y el espacio ya
//! existe: tiene sitio, tamaño, papel y puertas antes de que este módulo abra los ojos. Lo único que
//! se decide aquí es CON QUÉ se construye, nunca DÓNDE.
//!
//! # Dos materiales, y el segundo es el que hoy hace casi todo
//!
//! 1. **Una pieza del catálogo**, cuando alguna encaja en la huella del espacio. Es contenido
//!    autorado y es lo que se quiere ver.
//! 2. **Tramos generados** ([`super::segment`]) para todo lo demás. Un `Wg3Segment` es exactamente lo
//!    que un espacio del plan necesita: un rectángulo con suelo, techo, cuatro paredes y las bocas
//!    donde se le digan.
//!
//! **Y hoy gana el segundo casi siempre, por una razón que conviene tener delante antes de leer la
//! cifra:** el catálogo tiene 19 piezas con huellas fijas (9 × 9, 13 × 10, 42 × 30…) y el plan produce
//! rectángulos de la medida que pide la arquitectura. Que una huella autorada caiga dentro de la
//! tolerancia de un espacio planificado es casualidad, y la sonda la cuenta. Subir ese número tiene
//! dos caminos y ninguno es éste: que el plan se ajuste a las medidas que existen, o que existan
//! piezas de las medidas que el plan pide. Forzarlo aquí —encajar una pieza de 9 × 9 en un espacio de
//! 13 × 11 y dejar el resto a oscuras— es volver a tener hueco que nadie decidió.
//!
//! # Un espacio puede necesitar VARIOS tramos, y no es un detalle
//!
//! `MAX_SEGMENT_M` son 25 m, y la espina de una región mide 150. Un espacio grande se tesela en una
//! rejilla de tramos y **entre tramos hermanas se abre la pared entera**, así que siguen leyéndose
//! como un solo sitio. Ese tope no es estético: es lo que sostiene «una pieza, un chunk».
//!
//! # Lo que este módulo NO hace
//!
//! No enruta. Un [`LinkKind::Route`] —dos espacios que el plan quiere unidos pero que no se tocan— se
//! anota como pendiente y sale en el resultado con nombre y apellidos. **Un enlace que no se puede
//! construir es un fallo con nombre, no una arquitectura inventada en silencio para taparlo.**

use super::manifest::{Wg3Manifest, Wg3Piece};
use super::placement::Wg3Placement;
use super::plan::{
    LinkKind, PlannedSpace, RegionBuilding, RegionPlan, SpaceRole, SLAB_THICKNESS_CM,
    STOREY_HEIGHT_CM,
};
use super::raster::CM_PER_M;
use super::route::{self, Mouth, PlannedRoute, Rect, RouteSettings};
use super::segment::{
    Wg3Carve, Wg3Opening, Wg3Segment, Wg3Solid, CARVE_FLOOR_GUARD_CM, CASING_IN_CM,
    CASING_PROUD_CM, CASING_W_CM, MAX_SEGMENT_M, MIN_GENERATED_WIDTH_CM, SHAPE_ARCH, SHAPE_BOX,
    SHAPE_CYLINDER, SHAPE_HALF_CYLINDER, SHAPE_OCTAGON, STYLE_DECOR_BIT, WALL_THICKNESS_M,
};

/// ADR-099 D3 — cuánto entra el vano a cada lado de la cara de contacto, en metros. Mismo número que
/// usa la absorción, y por la misma razón: atravesar la pared y la celda del ráster.
pub(super) const CARVE_DEPTH_M: f32 = 0.5;

/// Altura libre por papel, en centímetros.
///
/// **La verticalidad más barata que existe, y la primera que WG3 tiene por arquitectura y no por
/// pieza.** Hasta aquí la altura venía horneada en el catálogo, así que dos salas contiguas medían lo
/// que midieran sus piezas; con el plan decidiendo el papel, una nave puede ser alta porque es una
/// nave. No mueve el suelo —eso es otro trabajo— pero sí el techo, que es la mitad de lo que hace que
/// un sitio se sienta distinto al de al lado.
pub(super) fn clear_height_by_role(role: SpaceRole) -> i32 {
    match role {
        SpaceRole::Hall => 450,
        SpaceRole::Spine => 360,
        SpaceRole::Corridor => 320,
        SpaceRole::Service | SpaceRole::Storage => 280,
        // La escalera va HOLGADA de techo: sus peldaños suben, y con la altura de un corredor el
        // último quedaría a 2,60 del techo mientras el primero está a 3,20. Se ve como que el techo
        // baja encima de ti justo donde estás subiendo.
        SpaceRole::Stair => 380,
        _ => 320,
    }
}

/// La altura libre que de verdad le toca a este espacio.
///
/// **ADR-102 D2 — la altura libre deja de ser libre en cuanto hay planta encima.** Sin el tope, una
/// nave de la planta baja pide 450 y su losa de techo se planta en `[450, 462]`, o sea 130 cm POR
/// ENCIMA del suelo de la planta de arriba, que está en 332: geometría de abajo atravesando el
/// forjado y saliendo dentro de las salas de arriba. No da error, no rompe ningún contador, y desde
/// dentro se lee como un bloque de hormigón en mitad de una oficina.
///
/// El tope lo pone el PLAN y no esta función, porque saber si hay algo encima es cosa del edificio y
/// no del papel del espacio. Cero quiere decir que no hay nada encima, y entonces la nave es una nave.
pub(super) fn clear_height_cm(space: &PlannedSpace) -> i32 {
    if is_atrium(space) {
        return atrium_clear_cm(space);
    }
    // **El espacio manda sobre el papel.** `ceiling_clear_cm` a cero no es un techo de cero: es que
    // este espacio no ha pedido nada —o que la perilla de `plan::CEILING_VARIETY` está apagada— y
    // entonces vuelve a decidir el papel, al centímetro, igual que antes de que el campo existiera.
    let want = if space.ceiling_clear_cm > 0 {
        space.ceiling_clear_cm
    } else {
        clear_height_by_role(space.role)
    };
    if space.max_clear_cm > 0 {
        want.min(space.max_clear_cm)
    } else {
        want
    }
}

/// ADR-104 D1 — **una nave con vacío encima es un ATRIO, y pide dos plantas de altura libre.**
///
/// Hasta ADR-104 una nave bajo un vacío no estaba limitada… y aun así pedía los 450 de su papel, así
/// que salía una sala ALTA y no un atrio. La diferencia entre 4,50 y 6,40 m es la diferencia entre
/// «techo generoso» y «esto ocupa dos pisos», que es la sensación que se pidió.
///
/// **Y son dos losas restadas, no una**, por el mismo motivo que en `plan::cap_headroom_under`: el
/// techo del atrio ocupa el sitio del techo de la planta de arriba, y contar una sola losa deja dos
/// caras coplanares — el z-fighting que costó 456 pares y hasta 94,8 m² en ADR-102.
const ATRIUM_CLEAR_CM: i32 = 2 * STOREY_HEIGHT_CM - 2 * SLAB_THICKNESS_CM;

/// La altura libre de ESTE atrio: tantas alturas de planta como diga el plan, menos dos losas.
///
/// [`ATRIUM_CLEAR_CM`] es este mismo número con las dos plantas de ADR-104, que sigue siendo el caso
/// normal. La **MEGASALA** (ADR-104 enm. 4) es el mismo atrio pidiendo hasta cinco: `5 * 332 - 24 =
/// 1636 cm`. Quién puede pedirlas y por qué el número no sale de contar vacíos está en
/// `plan::atrium_storeys_for`.
///
/// El `max(2)` es una red y no una rama: `is_atrium` ya exige `void_above`, así que el plan siempre
/// ha puesto aquí un dos o más. Sin él, un espacio marcado a mano en un test pediría menos altura que
/// una planta y el fallo se leería como un techo bajo cualquiera en vez de como un dato mal puesto.
pub(super) fn atrium_clear_cm(space: &PlannedSpace) -> i32 {
    let storeys = (space.atrium_storeys as i32).max(2);
    storeys * STOREY_HEIGHT_CM - 2 * SLAB_THICKNESS_CM
}

/// Si este espacio es un atrio: una NAVE con la planta de arriba vacía justo encima.
///
/// **Sólo `Hall`, y a propósito.** Un pasillo con 6,40 m de techo no es expansivo, es un error de
/// datos que nadie va a leer como intención. El papel es lo que separa «el plan quiso un atrio» de
/// «aquí arriba resultó no haber nada».
fn is_atrium(space: &PlannedSpace) -> bool {
    // **Y rectangular** (ADR-120 D5). El pretil se traza metiendo la envolvente hacia dentro y el
    // vano del forjado se recorta sobre ella: en una L los dos caerían sobre el suelo del vecino —
    // un pretil flotando en la sala de al lado y un agujero en SU techo. Un atrio de huella compuesta
    // es trabajo aparte, y hasta entonces una nave deformada es una nave de altura normal.
    space.void_above && space.role == SpaceRole::Hall && !space.is_composite()
}

/// ADR-105 enm. 14 — **EL CARÁCTER de una zona: el campo de densidad decide QUÉ gramática manda.**
///
/// Hasta aquí cada variación tiraba su dado por sala con la misma probabilidad en todo el mundo, y
/// eso es exactamente lo que Joel llamó «repetitivo»: la misma mezcla en todas partes. El campo de
/// densidad (ADR-118 D4) es función pura de la posición con grano de sala grande y tramos de ~22 m
/// andando; hasta hoy sólo lo leían los pilares. Ahora cada clase es un carácter, y TODAS las
/// probabilidades del relleno salen de su tabla: una zona abierta es abierta de verdad, y a veinte
/// metros empieza un laberinto.
///
/// Los umbrales son propios y no los de `density::class_at`, porque el reparto que se pide no es el
/// de la ambientación (un tercio vacío) sino el de la arquitectura: un quinto del mundo laberinto.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub(super) enum Character {
    /// Naves diáfanas, techos altos, casi nada dentro. El vacío también es contenido.
    Open,
    /// Cuartos exentos, ventanas en serie, pilastras, mamparas: la oficina.
    Office,
    /// Pilares, arcadas, bóvedas, vigas: la nave.
    Hall,
    /// Tabiques densos, laberinto real, techo a 2,40: Level 0.
    Maze,
    /// Colgados, rendijas, cornisas, techo a 2,00: lo raro.
    Weird,
}

/// Las perillas de un carácter. Una tabla y no un `match` por consumidor: así se lee entera de una
/// vez, y cambiar cómo se siente una zona es cambiar una fila.
pub(super) struct Knobs {
    pub character: Character,
    pub pillar_room: f32,
    pub pillar_forest: bool,
    pub partition_room: f32,
    pub maze: f32,
    /// ADR-105 enm. 15 — el laberinto de REJILLA (árbol de expansión), el de verdad.
    pub grid_maze: f32,
    pub cell: f32,
    pub hang_below: f32,
    /// ADR-105 enm. 16 — bajo qué tirada del perfil sale el MEDIO MURO BAJO (entre la mampara,
    /// que va hasta 0,34, y esto). Era una constante (0,49) para todos: la referencia del Nivel 0
    /// es una sala abierta con medios muros rematados en madera, y las salas «abierto» tenían dos
    /// en toda una región.
    pub low_below: f32,
    /// ADR-105 enm. 17 — qué proporción de salas lleva BLOQUES gruesos exentos (1–2 m de grosor,
    /// hasta el techo): las masas del Nivel 0.
    pub block: f32,
    /// ADR-105 enm. 17 — qué proporción de puertas lleva dintel. Las demás son HUECOS hasta el
    /// techo, como en las referencias. Era la constante `LINTEL_CHANCE` = 0,60 para todos.
    pub lintel: f32,
    pub beam_room: f32,
    pub beam_tight: bool,
    pub joist: f32,
    pub pilaster_room: f32,
    pub window: f32,
    pub series: f32,
    pub grille: f32,
    pub slit: f32,
    pub niche: f32,
    pub arcade: f32,
    pub vault: f32,
    pub soffit: f32,
    pub cornice: f32,
    pub platform: f32,
    /// Tope de altura libre de la zona, 0 = ninguno. Lo aplica el PLAN al asignar techos.
    pub ceiling_cap_cm: i32,
    /// ADR-125 — qué proporción de las naves con pilares los lleva CILÍNDRICOS.
    pub round_pillar: f32,
    /// ADR-125 — y OCTOGONALES (se sortea después del cilindro, sobre el resto).
    pub octagon_pillar: f32,
    /// ADR-125 — qué proporción de los espacios con pilastras las lleva en MEDIA LUNA.
    pub round_pilaster: f32,
    /// ADR-125 enm. 1 — qué proporción de las puertas con dintel llevan ARCO LISO bajo él.
    pub arch_door: f32,
}

const KNOBS: [Knobs; 5] = [
    Knobs {
        character: Character::Open,
        pillar_room: 0.15,
        pillar_forest: false,
        partition_room: 0.20,
        maze: 0.00,
        grid_maze: 0.00,
        cell: 0.05,
        hang_below: 0.55,
        low_below: 0.68,
        block: 0.42,
        lintel: 0.45,
        beam_room: 0.30,
        beam_tight: false,
        joist: 0.20,
        pilaster_room: 0.25,
        window: 0.20,
        series: 0.30,
        grille: 0.20,
        slit: 0.10,
        niche: 0.20,
        arcade: 0.20,
        vault: 0.45,
        soffit: 0.15,
        cornice: 0.20,
        platform: 0.35,
        ceiling_cap_cm: 0,
        round_pillar: 0.30,
        octagon_pillar: 0.20,
        round_pilaster: 0.25,
        arch_door: 0.20,
    },
    Knobs {
        character: Character::Office,
        pillar_room: 0.20,
        pillar_forest: false,
        partition_room: 0.90,
        maze: 0.10,
        grid_maze: 0.05,
        cell: 0.35,
        hang_below: 0.62,
        low_below: 0.49,
        block: 0.28,
        lintel: 0.75,
        beam_room: 0.45,
        beam_tight: false,
        joist: 0.30,
        pilaster_room: 0.75,
        window: 0.65,
        series: 0.65,
        grille: 0.35,
        slit: 0.20,
        niche: 0.40,
        arcade: 0.20,
        vault: 0.10,
        soffit: 0.40,
        cornice: 0.35,
        platform: 0.10,
        ceiling_cap_cm: 0,
        round_pillar: 0.10,
        octagon_pillar: 0.15,
        round_pilaster: 0.15,
        arch_door: 0.25,
    },
    Knobs {
        character: Character::Hall,
        pillar_room: 0.90,
        pillar_forest: true,
        partition_room: 0.45,
        maze: 0.05,
        grid_maze: 0.00,
        cell: 0.05,
        hang_below: 0.62,
        low_below: 0.45,
        block: 0.35,
        lintel: 0.55,
        beam_room: 0.85,
        beam_tight: true,
        joist: 0.15,
        pilaster_room: 0.60,
        window: 0.30,
        series: 0.30,
        grille: 0.30,
        slit: 0.15,
        niche: 0.25,
        arcade: 0.55,
        vault: 0.35,
        soffit: 0.25,
        cornice: 0.30,
        platform: 0.25,
        ceiling_cap_cm: 0,
        round_pillar: 0.40,
        octagon_pillar: 0.25,
        round_pilaster: 0.30,
        arch_door: 0.35,
    },
    Knobs {
        character: Character::Maze,
        pillar_room: 0.00,
        pillar_forest: false,
        partition_room: 1.00,
        maze: 0.60,
        grid_maze: 0.75,
        cell: 0.10,
        hang_below: 0.55,
        low_below: 0.42,
        block: 0.1,
        lintel: 0.7,
        beam_room: 0.20,
        beam_tight: true,
        joist: 0.10,
        pilaster_room: 0.15,
        window: 0.15,
        series: 0.10,
        grille: 0.50,
        slit: 0.35,
        niche: 0.20,
        arcade: 0.00,
        vault: 0.00,
        soffit: 0.10,
        cornice: 0.10,
        platform: 0.05,
        ceiling_cap_cm: 300,
        round_pillar: 0.20,
        octagon_pillar: 0.20,
        round_pilaster: 0.30,
        arch_door: 0.30,
    },
    Knobs {
        character: Character::Weird,
        pillar_room: 0.35,
        pillar_forest: true,
        partition_room: 0.85,
        maze: 0.30,
        grid_maze: 0.35,
        cell: 0.15,
        hang_below: 0.85,
        low_below: 0.49,
        block: 0.25,
        lintel: 0.6,
        beam_room: 0.60,
        beam_tight: true,
        joist: 0.60,
        pilaster_room: 0.50,
        window: 0.40,
        series: 0.20,
        grille: 0.60,
        slit: 0.60,
        niche: 0.45,
        arcade: 0.30,
        vault: 0.30,
        soffit: 0.20,
        cornice: 0.45,
        platform: 0.15,
        ceiling_cap_cm: 270,
        round_pillar: 0.55,
        octagon_pillar: 0.25,
        round_pilaster: 0.50,
        arch_door: 0.50,
    },
];

/// El carácter de un punto. Umbrales sobre el valor crudo del campo: abierto 30 %, oficina 25 %,
/// nave 17 %, laberinto 20 %, raro 8 %.
pub(super) fn character_at(seed: i32, x_m: f32, z_m: f32) -> Character {
    let v = super::density::value_at(seed, x_m, z_m);
    if v < 0.30 {
        Character::Open
    } else if v < 0.55 {
        Character::Office
    } else if v < 0.72 {
        Character::Hall
    } else if v < 0.92 {
        Character::Maze
    } else {
        Character::Weird
    }
}

/// Las perillas del espacio, por el centro de su envolvente: una sala es de UN carácter entero.
pub(super) fn knobs_of(seed: i32, space: &PlannedSpace) -> &'static Knobs {
    let (cx, cz) = space.rect.centre_m();
    let c = character_at(seed, cx, cz);
    KNOBS
        .iter()
        .find(|k| k.character == c)
        .expect("la tabla cubre los cinco caracteres")
}

/// El tope de altura libre que el carácter impone a un espacio, 0 si ninguno. Lo aplica
/// `plan::assign_ceilings`, que es quien sabe la semilla y decide techos.
pub(super) fn ceiling_cap_cm(seed: i32, space: &PlannedSpace) -> i32 {
    knobs_of(seed, space).ceiling_cap_cm
}

/// Discriminante de `Wg3VolumeKind::Step`: una caja de peldaño en la chuleta de una pieza.
const KIND_STEP: u8 = 5;

/// Cuánto puede sobrar entre la huella de una pieza y la del espacio para que se considere que
/// encaja, en centímetros y por eje.
///
/// **Medio metro, y es estricto a propósito.** Con tolerancia ancha una pieza de 9 × 9 entra en un
/// espacio de 13 × 11 y deja cuatro metros de nada alrededor: hueco que nadie decidió, que es
/// exactamente el problema que ADR-100 viene a quitar. Antes que rellenar mal, se genera.
const PIECE_FIT_SLACK_CM: i32 = 50;

/// Qué salió de rellenar un plan.
#[derive(Debug, Clone, Default)]
pub struct FilledRegion {
    pub placements: Vec<Wg3Placement>,
    pub segments: Vec<Wg3Segment>,
    /// ADR-099 D3 — los vanos EXCAVADOS en las piezas del catálogo.
    ///
    /// **Es lo que hace usable una pieza autorada dentro de un plan.** Una pieza trae sus bocas
    /// horneadas y el plan pone las puertas donde manda la arquitectura; los dos sitios no coinciden
    /// casi nunca, así que sin excavar la pieza nace sellada. Medido antes de cablearlo: cada pieza
    /// colocada añadía unas dos manchas andables sueltas, y la región (0,0) pasaba de 3 a 20.
    ///
    /// La operación ya existía —la trajo ADR-099 para la absorción— y no tenía consumidor.
    pub carves: Vec<Wg3Carve>,

    /// ADR-105 — los MACIZOS: materia que se ANADE y que no es la cascara de ninguna sala.
    ///
    /// Aparte de los vanos porque hacen lo contrario, y aparte de los tramos porque un tramo trae
    /// suelo, techo y cuatro paredes: un pilar hecho de tramo dejaria dos losas coplanares con las
    /// del atrio, que es el z-fighting que ADR-102 pago con 456 pares.
    pub solids: Vec<Wg3Solid>,

    /// Espacios resueltos con una pieza del catálogo, y con tramos generados. **Los dos números
    /// juntos son la salud del catálogo frente al plan**, y hoy el primero es pequeño: ver la
    /// cabecera del módulo.
    pub spaces_by_piece: u32,
    pub spaces_by_segment: u32,
    /// Espacios que no se pudieron construir de ninguna forma. Debería ser cero.
    pub spaces_unbuilt: u32,

    /// Bocas abiertas por enlaces del plan, y enlaces que no se pudieron cumplir con un vano.
    pub openings_built: u32,
    /// **Huecos que el plan pidió, que caían en la pared correcta, y que ningún tramo pudo alojar.**
    ///
    /// Ocurre cuando el hueco queda a caballo de la frontera entre dos tramos hermanas de un espacio
    /// teselado. Es el fallo más peligroso de este módulo porque no se nota: los contadores de arriba
    /// siguen cuadrando, `links_failed` sigue vacío, y la sala nace sellada con su puerta dibujada en
    /// el plano. Se cuenta aparte para que un cero sea una afirmación y no una suposición.
    pub openings_dropped: u32,
    /// Dónde se perdió cada uno, en centímetros de mundo, y de qué espacio era.
    ///
    /// **Un contador sin sitio no se puede depurar.** El primero que apareció en una partida real
    /// —`región (-1,0): 1 huecos perdidos`— costó una vuelta entera de adivinar qué espacio era,
    /// porque el número no decía nada más que su propio valor.
    pub openings_dropped_at: Vec<(usize, i32, i32)>,
    /// Enlaces `Route`: los que el plan quiere y sólo el enrutador puede tender. No son un fallo, son
    /// el encargo del paso 3.
    pub links_to_route: Vec<(usize, usize)>,
    /// Enlaces que el plan pidió y que NO se han podido construir ni encargar. **Éstos sí son un
    /// fallo**, y salen con los dos espacios delante para poder ir a mirarlos.
    pub links_failed: Vec<(usize, usize)>,
    /// Puertas de junta cumplidas y no cumplidas. Una puerta sin cumplir es una caída al vacío en la
    /// región de al lado, así que se cuenta aparte de todo lo demás.
    pub gates_built: u32,
    pub gates_failed: u32,
}

impl FilledRegion {
    /// Se traga otra planta ya rellenada. Los contadores se suman; la geometría se concatena.
    ///
    /// **Los índices de espacio dejan de ser únicos al juntar plantas**, y está dicho aquí para que
    /// nadie los lea como si lo fueran: `openings_dropped_at`, `links_to_route` y `links_failed`
    /// llevan el índice DENTRO de su planta. Para depurar sirve la coordenada, que es de mundo y no se
    /// repite; el índice, no.
    fn absorb(&mut self, other: FilledRegion) {
        self.placements.extend(other.placements);
        self.segments.extend(other.segments);
        self.carves.extend(other.carves);
        self.solids.extend(other.solids);
        self.spaces_by_piece += other.spaces_by_piece;
        self.spaces_by_segment += other.spaces_by_segment;
        self.spaces_unbuilt += other.spaces_unbuilt;
        self.openings_built += other.openings_built;
        self.openings_dropped += other.openings_dropped;
        self.openings_dropped_at.extend(other.openings_dropped_at);
        self.links_to_route.extend(other.links_to_route);
        self.links_failed.extend(other.links_failed);
        self.gates_built += other.gates_built;
        self.gates_failed += other.gates_failed;
    }
}

/// Un hueco pedido en la pared exterior de un espacio, antes de repartirlo entre sus tramos.
#[derive(Debug, Clone, Copy)]
struct Wanted {
    /// Lado del ESPACIO, en coordenadas de mundo: `0 = N (+Z)`, `1 = E (+X)`, `2 = S (−Z)`, `3 = O`.
    side: u8,
    /// Punto del centro del hueco, en centímetros de mundo.
    at_x_cm: i32,
    at_z_cm: i32,
    width_cm: i32,
}

/// **RELLENA UN PLAN.** Función pura: mismo plan y mismo catálogo ⇒ misma geometría.
pub fn fill(plan: &RegionPlan, manifest: &Wg3Manifest) -> FilledRegion {
    fill_with(plan, manifest, true)
}

/// **EL EDIFICIO ENTERO** (ADR-102 D5): todas las plantas, y el suelo perforado por donde suben las
/// escaleras.
///
/// Cada planta se rellena por su cuenta —incluido el enrutador, que ve sólo la ocupación de la suya—
/// y lo único que las relaciona es el vano del forjado. Ésa es la misma decisión de D1 vista desde
/// aquí: nada de este módulo aprende una tercera coordenada, porque la planta ya viene con su cota
/// puesta en cada espacio.
pub fn fill_building(building: &RegionBuilding, manifest: &Wg3Manifest) -> FilledRegion {
    let mut out = FilledRegion::default();
    for (n, plan) in building.storeys.iter().enumerate() {
        // **Donde aterriza un pozo no va una pieza del catálogo** (auditoría 2026-09-02). El
        // rellano es el suelo de la planta de llegada y sale al espacio que lo rodea; una pieza
        // trae su interior horneado —pilares, bloques, sus propias escaleras— que el plan no ve, y
        // el rellano nacía dentro de un `room_core` con doce celdas de suelo y ninguna salida: la
        // planta entera al 0 %. Esos espacios se construyen generados, que es geometría que el plan
        // controla.
        let landings: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below + 1 == n)
            .map(|w| w.rect)
            .collect();
        out.absorb(fill_storey(
            plan,
            manifest,
            true,
            &RouteSettings::default(),
            &landings,
            building.seed,
        ));
    }
    out.carves.extend(atrium_carves(building));
    out.carves.extend(hole_carves(building));
    out.carves.extend(well_mouth_carves(building));
    // ADR-126 — las rejillas de pozos de la planta baja: vanos en la losa, tierra y cámara.
    let (pit_carves, pit_solids) = pit_geometry(building, &out.segments);
    out.carves.extend(pit_carves);
    out.solids.extend(pit_solids);
    out.solids.extend(atrium_solids(building));
    out.solids.extend(atrium_aprons(building));
    // ADR-119 enm. 1 — los pilares, que ya no son sólo del atrio. Va DESPUÉS del bucle de plantas
    // porque necesita saber qué espacios acabaron resueltos con una pieza del catálogo, y eso no se
    // sabe hasta que están todos rellenados.
    let placed = out.placements.clone();
    let pillars = hall_pillars(building, manifest, &placed, &out.segments);
    // ADR-105 enm. 9 — las bocas de los TRAMOS emitidos. `plan.links` guarda el punto medio de una
    // ruta, no la boca en la pared, así que una división podía tapar la entrada de un conector sin
    // que ningún contador lo viera: subió las islas de 1,3 a 1,5 por región al meter el peine.
    let seg_doors = segment_door_points(&out.segments);
    // ADR-105 enm. 3 — la masa interior. Va DESPUES de los pilares porque los esquiva: dos macizos
    // que se pisan son una caja rara, no dos elementos.
    let partitions = interior_partitions(
        building,
        manifest,
        &placed,
        &pillars,
        &seg_doors,
        &out.carves,
        &out.segments,
    );
    out.solids.extend(partitions);
    out.solids.extend(pillars);
    // ADR-105 enm. 17 — los bloques gruesos, después de pilares y divisiones porque los esquivan.
    let blocks = wall_blocks(
        building,
        manifest,
        &placed,
        &out.segments,
        &out.solids,
        &out.carves,
    );
    // Las tarimas (más abajo) los esquivan: una tarima bajo un bloque es un bloque que flota.
    let blocks_for_platforms = blocks.clone();
    out.solids.extend(blocks);
    // ADR-105 enm. 10 — las pilastras, pegadas a las paredes de pasillos y naves. Después de las
    // divisiones: éstas guardan 300 cm de margen con la pared y no se tocan.
    let pilasters = wall_pilasters(building, &seg_doors, &out.carves, &out.segments);
    out.solids.extend(pilasters);
    // ADR-105 enm. 16 — los listones de pared, después de todo lo que se pega a una pared, porque
    // los esquivan.
    let rails = wall_rails(building.seed, &out.segments, &out.solids, &out.carves);
    out.solids.extend(rails);
    // ADR-105 enm. 11 — arcadas entre pilares y bóvedas escalonadas. Cuelgan por encima de 2,50, así
    // que no esquivan nada del suelo; sólo pozos y agujeros, que atraviesan el techo.
    let arcades = pillar_arcades(building, &out.solids);
    out.solids.extend(arcades);
    out.solids.extend(wall_vaults(building, manifest, &placed));
    // ADR-105 enm. 5 — el relieve del techo. Cuelga de la losa, así que no esquiva nada de lo de
    // abajo: sólo pozos y agujeros, que son lo único que atraviesa el techo.
    out.solids
        .extend(ceiling_beams(building, manifest, &placed));
    // ADR-105 enm. 13 — descuelgue perimetral o cornisa, y tarimas.
    out.solids.extend(wall_soffits(building, manifest, &placed));
    out.solids.extend(floor_platforms(
        building,
        manifest,
        &placed,
        &out.segments,
        &blocks_for_platforms,
    ));
    out
}

/// Altura libre de la boca de un pozo, desde el suelo de la planta de llegada.
const WELL_MOUTH_CLEAR_CM: i32 = 240;

/// VERTICALITY-ROADMAP D1 — **la pared que cruza la boca de un pozo se recorta.**
///
/// Con el aterrizaje repartido entre varios espacios (`landing_over`), la frontera entre dos de
/// ellos puede cruzar por encima del tiro — y esa frontera es una pared que `fill` emite sin saber
/// que debajo hay una escalera. El recorte va DENTRO de la huella, quince centímetros por dentro,
/// para que las paredes que BORDEAN el pozo sigan enteras: lo que se abre es el paso sobre la boca,
/// no una barandilla. El suelo no se toca: `carve_box` deja intacto todo tramo cuya cara de arriba
/// quede EN la cota de arranque o por debajo, así que la losa `[984, 996]` sobrevive a un recorte que
/// arranca en 996.
///
/// **Y arranca EN la cota, no un centímetro por encima** (auditoría 2026-09-02). Con `floor + 1`, la
/// pared que cruzaba la boca dejaba una lengüeta de `[996, 997]` sobre el rellano: el último peldaño
/// pasaba de 26 a 27 cm, justo el tope que sube el jugador, y con el redondeo del ráster la planta
/// entera quedaba inalcanzable —medido en una de 27 regiones del barrido— sin que el plan viera nada.
fn well_mouth_carves(building: &RegionBuilding) -> Vec<Wg3Carve> {
    const INSET_CM: i32 = 15;
    building
        .wells
        .iter()
        .filter(|w| w.rect.width_cm() > 2 * INSET_CM && w.rect.depth_cm() > 2 * INSET_CM)
        .map(|w| {
            let floor = (w.storey_below as i32 + 1) * STOREY_HEIGHT_CM;
            Wg3Carve {
                x_cm: w.rect.min_x_cm + INSET_CM,
                z_cm: w.rect.min_z_cm + INSET_CM,
                size_x_cm: w.rect.width_cm() - 2 * INSET_CM,
                size_z_cm: w.rect.depth_cm() - 2 * INSET_CM,
                bottom_y_cm: floor,
                top_y_cm: floor + WELL_MOUTH_CLEAR_CM,
            }
        })
        .collect()
}

/// Lado de un agujero de forjado, en centímetros.
///
/// **Cuatro celdas del ráster, y el número sale de ahí y no del gusto.** Con dos celdas el
/// rasterizado conservador puede cerrarlo —toda celda que una caja TOQUE queda maciza—, y entonces el
/// agujero se dibuja y no se cae por él, que es la clase de fallo que este sistema ya ha pagado tres
/// veces. Con cuatro sobran dos celdas limpias en el centro pase lo que pase en los bordes.
const HOLE_SIDE_CM: i32 = 200;

/// Cada cuánto un espacio de una planta alta se lleva un agujero.
///
/// Bajo a propósito: un agujero es una trampa sin aviso mientras no haya pretil (ver ADR-104
/// enmienda 1), así que la primera versión pone pocos y se mira. Subirlo es cambiar este número.
///
/// **Recalibrado en ADR-119 D5, y el motivo es que este numero no dice agujeros: dice agujeros POR
/// ESPACIO.** Al subir el tamano medio de sala (D1 y D2) una region paso de 263 espacios a 163, asi
/// que con 0,10 los agujeros por region cayeron un 38 % sin que nadie tocara la regla — de 8+ a 5 en
/// las cuatro regiones de referencia, por debajo del suelo que fija
/// `a_hole_drops_you_a_whole_storey`. Y no bastaba con reescalar por el numero de espacios: los
/// candidatos cayeron mas que ellos (de ~80 a ~31 en las cuatro regiones de referencia) porque con
/// salas mas grandes hay mas `Void` y mas atrios debajo, y un agujero sobre vacio no se emite.
/// 0,26 devuelve la densidad POR REGION que ADR-104 D4 eligio, que es la magnitud que de verdad se
/// miro jugando.
const HOLE_CHANCE: f32 = 0.26;

/// Sal del sorteo de agujeros.
const SALT_HOLE: u32 = 0xA9_04_01;

/// El cuadrado central de un espacio donde [`hole_carves`] pondría su agujero. Es la ÚNICA
/// definición: la usan el emisor y quienes tienen que esquivarlo (divisiones y pilares), para que
/// nadie esquive un sitio distinto del que se perfora.
fn hole_square(r: &super::plan::PlanRect) -> super::plan::PlanRect {
    let hx = r.min_x_cm + (r.width_cm() - HOLE_SIDE_CM) / 2;
    let hz = r.min_z_cm + (r.depth_cm() - HOLE_SIDE_CM) / 2;
    super::plan::PlanRect {
        min_x_cm: hx,
        min_z_cm: hz,
        max_x_cm: hx + HOLE_SIDE_CM,
        max_z_cm: hz + HOLE_SIDE_CM,
    }
}

/// Los cuadrados donde la planta `n + 1` PUEDE abrir un agujero, con medio metro de margen, para que
/// ningún macizo de la planta `n` nazca debajo. Se esquiva todo candidato sin repetir el dado de
/// [`hole_carves`]: duplicar el sorteo es duplicar lógica que luego se separa, y el centro de una
/// sala no es una pérdida. Un pilar o una división justo bajo el forjado perforado convierten la
/// caída de dos plantas en una de veinte centímetros, y el síntoma es un agujero por el que se ve y
/// no se pasa.
fn hole_squares_above(building: &RegionBuilding, n: usize) -> Vec<super::plan::PlanRect> {
    building
        .storeys
        .get(n + 1)
        .map(|up| {
            up.spaces
                .iter()
                .filter(|t| {
                    t.role.is_built()
                        && !t.role.is_circulation()
                        && t.role != SpaceRole::Stair
                        && t.rise_cm == 0
                })
                .map(|t| hole_square(&t.rect).shrunk(-50))
                .collect()
        })
        .unwrap_or_default()
}

/// ADR-104 D4 — **un hueco sin escalera dentro es un AGUJERO**, y es la conexión vertical más barata
/// que existe.
///
/// Una escalera recta pide 12,6 m de sala y por eso sólo hay de 2 a 5 sitios por región donde cabe.
/// Un agujero pide dos metros. Es de un solo sentido, y eso no es un defecto: es lo que se pidió.
///
/// # Por qué la banda vertical empieza DOS losas por debajo
///
/// Entre dos plantas hay **dos** losas y no una —el techo de abajo en `[308, 320]` y el suelo de
/// arriba en `[320, 332]`, espalda contra espalda—, que es lo mismo que ya obligó a restar dos en
/// `cap_headroom_under` y en [`ATRIUM_CLEAR_CM`]. Llevarse sólo el suelo de arriba dejaría el techo de
/// abajo entero: un agujero por el que se ve y no se pasa, dibujado perfecto y con todos los
/// contadores en verde.
///
/// # Y por qué esto no contradice [`CARVE_FLOOR_GUARD_CM`]
///
/// Esa guarda existe para que **un vano de PUERTA** no se lleve la losa sobre la que se anda. Aquí
/// llevársela es el objetivo, y la guarda no vive en `raster::carve_box` sino en quien lo llama, así
/// que no hay que romper nada: hay que no aplicarla.
fn hole_carves(building: &RegionBuilding) -> Vec<Wg3Carve> {
    let mut out = Vec::new();
    if building.storeys.len() < 2 {
        return out;
    }

    for n in 1..building.storeys.len() {
        for s in &building.storeys[n].spaces {
            // Nunca en circulación: un agujero en la espina es la trampa que no se puede esquivar
            // porque es el único sitio por donde se pasa. Y nunca en una escalera ni en un espacio
            // hundido, que ya tienen su propia geometría vertical.
            if !s.role.is_built()
                || s.role.is_circulation()
                || s.role == SpaceRole::Stair
                || s.rise_cm != 0
            {
                continue;
            }
            // Tiene que caber con margen: un agujero pegado a la pared no se ve hasta pisarlo.
            if s.rect.width_cm() < HOLE_SIDE_CM * 3 || s.rect.depth_cm() < HOLE_SIDE_CM * 3 {
                continue;
            }

            let (cx, cz) = s.rect.centre_m();
            let mut st = super::hash::stream_at(building.seed, cx, cz, SALT_HOLE);
            if st.next01() >= HOLE_CHANCE {
                continue;
            }

            // **Y debajo tiene que haber SUELO CONSTRUIDO.** Un agujero sobre un vacío intencionado no
            // es un agujero de dos plantas: es una caída al forjado de más abajo o a nada, y desde
            // arriba se ve igual. Se comprueba contra la planta de debajo, que es lo único que este
            // módulo puede consultar sin que una planta aprenda de otra.
            let hole = hole_square(&s.rect);
            let (hx, hz) = (hole.min_x_cm, hole.min_z_cm);
            let lands_on_floor = building.storeys[n - 1]
                .spaces
                .iter()
                .any(|t| t.role.is_built() && t.role != SpaceRole::Stair && t.hits_rect(&hole));
            if !lands_on_floor {
                continue;
            }
            // **Y el agujero tiene que estar ENTERO sobre suelo de este espacio** (ADR-120 D5). El
            // centro de la envolvente de una L cae en la muesca —o sea, en la sala del vecino—, y
            // ahí un vano de forjado no abre un hueco de dos plantas: le quita el techo al de al
            // lado, que no ha decidido nada.
            if !s.covers_rect(&hole) {
                continue;
            }

            out.push(Wg3Carve {
                x_cm: hx,
                z_cm: hz,
                size_x_cm: HOLE_SIDE_CM,
                size_z_cm: HOLE_SIDE_CM,
                bottom_y_cm: s.floor_y_cm - 2 * SLAB_THICKNESS_CM,
                top_y_cm: s.floor_y_cm + CARVE_FLOOR_GUARD_CM,
            });
        }
    }
    out
}

/// ADR-126 enm. 1 — lado de cada pozo. Cuatro celdas del ráster exactas. Era 100 (D2): Joel pidió
/// «el doble de pozo y la mitad de pasillo», más vertiginoso.
pub(super) const PIT_SIDE_CM: i32 = 200;
/// ADR-126 enm. 1 — paso de la rejilla: pozo más pasillo de [`PIT_BRIDGE_CM`].
pub(super) const PIT_PITCH_CM: i32 = PIT_SIDE_CM + PIT_BRIDGE_CM;
/// ADR-126 enm. 1 — el pasillo entre pozos: UNA celda. Más estrecho que el jugador (70 cm): se
/// puede cruzar y se puede caer, que es lo que se pidió. Rodear siempre se puede por el margen.
pub(super) const PIT_BRIDGE_CM: i32 = 50;
/// ADR-126 D2 — de la pared más cercana al primer pozo. Un pozo pegado a la pared no se ve hasta
/// pisarlo, y las pilastras y los rodapiés viven en ese medio metro.
pub(super) const PIT_MARGIN_CM: i32 = 150;
/// ADR-126 D1 — lado mínimo de la sala: margen, dos pozos con su pasillo, margen.
const PIT_MIN_SIDE_CM: i32 = 2 * PIT_MARGIN_CM + PIT_PITCH_CM + PIT_SIDE_CM + 100;
/// ADR-126 D2 — pozos por eje, como mucho. Seis por seis son 36 vanos y 49 macizos en un chunk.
const PIT_MAX_PER_AXIS: i32 = 6;
/// ADR-126 D1 — la misma densidad por sala que el agujero de ADR-104.
const PIT_CHANCE: f32 = 0.26;
/// ADR-126 D3 — alto libre de la cámara del fondo.
pub(super) const PIT_CHAMBER_H_CM: i32 = 300;
/// ADR-126 D3 — profundidades posibles, en metros. Con la curva del vendor (gravedad 20, mortal a
/// 30 m/s) 10 m cuesta el 67 % de la vida y 20 m el 94 %; de 30 en adelante se muere. Cuatro de
/// siete son sobrevivibles con la vida entera, que es lo que pidió Joel.
const PIT_DEPTHS_M: [i32; 7] = [10, 10, 20, 20, 30, 40, 50];
/// ADR-126 D4 — el estilo negro de la CÁMARA. El cliente lo tiñe a casi nada y no le cuelga
/// luminaria.
pub const PIT_STYLE: u8 = 7;
/// ADR-126 enm. 1 — el estilo de la pared del POZO (la tierra): gris de hormigón, para que las
/// lámparas de la sala le den a los primeros metros y la perspectiva se lea. Con todo a 0,05 cada
/// pozo era un cuadrado negro plano y cuarenta metros no se distinguían de pintura.
pub const PIT_SHAFT_STYLE: u8 = 8;
/// Sal del sorteo de rejillas.
const SALT_PIT: u32 = 0xA9_04_02;

/// ADR-126 — una rejilla de pozos ya sorteada: dónde empieza, cuántos, y a qué profundidad cae.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) struct PitCluster {
    /// Esquina mínima del primer pozo, en centímetros de mundo, múltiplo de 50.
    pub x0_cm: i32,
    pub z0_cm: i32,
    pub nx: i32,
    pub nz: i32,
    pub floor_y_cm: i32,
    pub depth_cm: i32,
}

impl PitCluster {
    /// El pozo (i, j).
    pub fn pit(&self, i: i32, j: i32) -> super::plan::PlanRect {
        let x = self.x0_cm + i * PIT_PITCH_CM;
        let z = self.z0_cm + j * PIT_PITCH_CM;
        super::plan::PlanRect {
            min_x_cm: x,
            min_z_cm: z,
            max_x_cm: x + PIT_SIDE_CM,
            max_z_cm: z + PIT_SIDE_CM,
        }
    }
    /// La huella de la rejilla con su margen: lo que ocupan la tierra y la cámara.
    pub fn footprint(&self) -> super::plan::PlanRect {
        super::plan::PlanRect {
            min_x_cm: self.x0_cm - PIT_MARGIN_CM,
            min_z_cm: self.z0_cm - PIT_MARGIN_CM,
            max_x_cm: self.x0_cm + (self.nx - 1) * PIT_PITCH_CM + PIT_SIDE_CM + PIT_MARGIN_CM,
            max_z_cm: self.z0_cm + (self.nz - 1) * PIT_PITCH_CM + PIT_SIDE_CM + PIT_MARGIN_CM,
        }
    }
    /// Cota del suelo de la cámara.
    pub fn chamber_floor_y_cm(&self) -> i32 {
        self.floor_y_cm - self.depth_cm
    }
}

/// ADR-126 D1 — la rejilla de UN espacio, si le toca. Definición única: la usan el emisor y quienes
/// tienen que esquivarla (pilares, divisiones, pilastras), como `hole_square`.
///
/// **Y dentro de UN solo tramo.** Una sala de WG3 no es un espacio abierto: la rellenan varios
/// `Wg3Segment` con paredes entre sí, y una rejilla que cruce una de esas paredes tiene un pasillo
/// que es muro (medido: en (−142,5, 345) la tierra se fundía con una pared de 2,52 m). La huella con
/// su margen tiene que caber en el interior de un tramo de la planta baja, que además deja fuera a
/// las piezas del catálogo (traen su interior horneado y no son tramos).
fn pit_cluster_of(
    building: &RegionBuilding,
    s: &PlannedSpace,
    segments: &[Wg3Segment],
) -> Option<PitCluster> {
    if !s.role.is_built() || s.role.is_circulation() || s.role == SpaceRole::Stair || s.rise_cm != 0
    {
        return None;
    }
    let r = s.rect;
    if r.width_cm() < PIT_MIN_SIDE_CM || r.depth_cm() < PIT_MIN_SIDE_CM {
        return None;
    }
    let (cx, cz) = r.centre_m();
    let mut st = super::hash::stream_at(building.seed, cx, cz, SALT_PIT);
    if st.next01() >= PIT_CHANCE {
        return None;
    }
    // Cuántos caben con margen a los dos lados, y centrados en la sala.
    let fits = |side: i32| -> i32 {
        ((side - 2 * PIT_MARGIN_CM - PIT_SIDE_CM) / PIT_PITCH_CM + 1).clamp(0, PIT_MAX_PER_AXIS)
    };
    let nx = fits(r.width_cm());
    let nz = fits(r.depth_cm());
    if nx < 2 || nz < 2 {
        return None;
    }
    let span_x = (nx - 1) * PIT_PITCH_CM + PIT_SIDE_CM;
    let span_z = (nz - 1) * PIT_PITCH_CM + PIT_SIDE_CM;
    // Alineado a 50 cm de mundo: dos celdas exactas por pozo y por pasillo (D2).
    let x0 = (r.min_x_cm + (r.width_cm() - span_x) / 2).div_euclid(50) * 50;
    let z0 = (r.min_z_cm + (r.depth_cm() - span_z) / 2).div_euclid(50) * 50;
    let depth_cm = PIT_DEPTHS_M[(st.next_raw() % PIT_DEPTHS_M.len() as u64) as usize] * 100;
    let cluster = PitCluster {
        x0_cm: x0,
        z0_cm: z0,
        nx,
        nz,
        floor_y_cm: s.floor_y_cm,
        depth_cm,
    };
    // Entera sobre suelo de ESTE espacio (una L con la muesca en medio no la lleva), y con el
    // margen dentro también: la tierra y la cámara no pueden asomar bajo el vecino.
    if !s.covers_rect(&cluster.footprint()) {
        return None;
    }
    let fp = cluster.footprint();
    let inside_one_segment = segments.iter().any(|g| {
        g.floor_y_cm == s.floor_y_cm && {
            let inner = super::plan::PlanRect {
                min_x_cm: g.x_cm + WALL_T_CM,
                min_z_cm: g.z_cm + WALL_T_CM,
                max_x_cm: g.x_cm + g.size_x_cm - WALL_T_CM,
                max_z_cm: g.z_cm + g.size_z_cm - WALL_T_CM,
            };
            inner.contains_rect(&fp)
        }
    });
    if !inside_one_segment {
        return None;
    }
    Some(cluster)
}

/// ADR-126 — todas las rejillas de la planta baja del edificio, dados sus tramos ya emitidos.
pub(super) fn pit_clusters_of(
    building: &RegionBuilding,
    segments: &[Wg3Segment],
) -> Vec<PitCluster> {
    building
        .storeys
        .first()
        .map(|plan| {
            plan.spaces
                .iter()
                .filter_map(|s| pit_cluster_of(building, s, segments))
                .collect()
        })
        .unwrap_or_default()
}

/// ADR-126 D5 — las huellas que nadie de la planta baja debe pisar, con medio metro de margen.
fn pit_rects_of(building: &RegionBuilding, segments: &[Wg3Segment]) -> Vec<super::plan::PlanRect> {
    pit_clusters_of(building, segments)
        .iter()
        .map(|c| c.footprint().shrunk(-50))
        .collect()
}

/// ADR-126 D3 — lo que una rejilla emite: un vano por pozo, la tierra entre pozos y la cámara.
fn pit_geometry(
    building: &RegionBuilding,
    segments: &[Wg3Segment],
) -> (Vec<Wg3Carve>, Vec<Wg3Solid>) {
    let mut carves = Vec::new();
    let mut solids = Vec::new();
    let boxed = |r: &super::plan::PlanRect, y0: i32, y1: i32, style: u8| -> Wg3Solid {
        Wg3Solid {
            x_cm: r.min_x_cm,
            z_cm: r.min_z_cm,
            size_x_cm: r.width_cm(),
            size_z_cm: r.depth_cm(),
            bottom_y_cm: y0,
            top_y_cm: y1,
            style,
            yaw_deg: 0,
            shape: SHAPE_BOX,
        }
    };
    for c in pit_clusters_of(building, segments) {
        let fp = c.footprint();
        let floor = c.floor_y_cm;
        let chamber_floor = c.chamber_floor_y_cm();
        let earth_top = floor - SLAB_THICKNESS_CM;
        let earth_bottom = chamber_floor + PIT_CHAMBER_H_CM;

        // Los vanos: la losa de la planta baja, como el agujero de ADR-104 (sin la guarda del
        // suelo, que aquí es el objetivo).
        for i in 0..c.nx {
            for j in 0..c.nz {
                let p = c.pit(i, j);
                carves.push(Wg3Carve {
                    x_cm: p.min_x_cm,
                    z_cm: p.min_z_cm,
                    size_x_cm: PIT_SIDE_CM,
                    size_z_cm: PIT_SIDE_CM,
                    bottom_y_cm: floor - SLAB_THICKNESS_CM - 1,
                    top_y_cm: floor + CARVE_FLOOR_GUARD_CM,
                });
            }
        }
        // La tierra: tiras de ancho completo entre filas de pozos (y en los dos márgenes)…
        let mut z_edges = vec![fp.min_z_cm];
        for j in 0..c.nz {
            let p = c.pit(0, j);
            z_edges.push(p.min_z_cm);
            z_edges.push(p.max_z_cm);
        }
        z_edges.push(fp.max_z_cm);
        for k in (0..z_edges.len()).step_by(2) {
            let (z0, z1) = (z_edges[k], z_edges[k + 1]);
            if z1 > z0 {
                let strip = super::plan::PlanRect {
                    min_x_cm: fp.min_x_cm,
                    min_z_cm: z0,
                    max_x_cm: fp.max_x_cm,
                    max_z_cm: z1,
                };
                solids.push(boxed(&strip, earth_bottom, earth_top, PIT_SHAFT_STYLE));
            }
        }
        // …y, en cada fila, los bloques entre pozos (y los dos de los márgenes).
        for j in 0..c.nz {
            let row = c.pit(0, j);
            let mut x_edges = vec![fp.min_x_cm];
            for i in 0..c.nx {
                let p = c.pit(i, j);
                x_edges.push(p.min_x_cm);
                x_edges.push(p.max_x_cm);
            }
            x_edges.push(fp.max_x_cm);
            for k in (0..x_edges.len()).step_by(2) {
                let (x0, x1) = (x_edges[k], x_edges[k + 1]);
                if x1 > x0 {
                    let block = super::plan::PlanRect {
                        min_x_cm: x0,
                        min_z_cm: row.min_z_cm,
                        max_x_cm: x1,
                        max_z_cm: row.max_z_cm,
                    };
                    solids.push(boxed(&block, earth_bottom, earth_top, PIT_SHAFT_STYLE));
                }
            }
        }
        // La cámara: losa de suelo y cuatro paredes por FUERA de la huella, hasta la tierra.
        let wall = WALL_T_CM;
        let outer = fp.shrunk(-wall);
        solids.push(boxed(
            &outer,
            chamber_floor - SLAB_THICKNESS_CM,
            chamber_floor,
            PIT_STYLE,
        ));
        let walls = [
            super::plan::PlanRect {
                min_x_cm: outer.min_x_cm,
                min_z_cm: outer.min_z_cm,
                max_x_cm: outer.max_x_cm,
                max_z_cm: fp.min_z_cm,
            },
            super::plan::PlanRect {
                min_x_cm: outer.min_x_cm,
                min_z_cm: fp.max_z_cm,
                max_x_cm: outer.max_x_cm,
                max_z_cm: outer.max_z_cm,
            },
            super::plan::PlanRect {
                min_x_cm: outer.min_x_cm,
                min_z_cm: fp.min_z_cm,
                max_x_cm: fp.min_x_cm,
                max_z_cm: fp.max_z_cm,
            },
            super::plan::PlanRect {
                min_x_cm: fp.max_x_cm,
                min_z_cm: fp.min_z_cm,
                max_x_cm: outer.max_x_cm,
                max_z_cm: fp.max_z_cm,
            },
        ];
        for w in &walls {
            solids.push(boxed(w, chamber_floor, earth_bottom, PIT_STYLE));
        }
    }
    (carves, solids)
}

/// ADR-105 enm. 16 — el LISTÓN de pared: la tabla de madera a media altura que llevan las paredes
/// del Nivel 0 en cuatro de las diez referencias de Joel. Cota de su cara inferior sobre el suelo.
pub(super) const WALL_RAIL_Y_CM: i32 = 90;
/// Canto de la tabla. Seis y no más: el cliente talla perfil a toda decoración de más de 4,5 cm de
/// canto… salvo que su fondo sea menor de 4,5 (`AddCasingBox` cae a caja lisa), y aquí el fondo es
/// el vuelo de 3. Una tabla lisa, pues.
pub(super) const WALL_RAIL_H_CM: i32 = 6;
/// Cuánto vuela la tabla de la cara de la pared.
pub(super) const WALL_RAIL_PROUD_CM: i32 = 3;
/// Cada cuánto una pared de tramo lleva listón.
const WALL_RAIL_CHANCE: f32 = 0.40;
/// Largo del listón, mínimo y máximo. En la referencia son TRAMOS, no una línea continua.
const WALL_RAIL_LEN_CM: (i32, i32) = (150, 450);
/// Lo que el listón deja libre hasta una esquina, una boca o una pieza pegada a la pared.
const WALL_RAIL_CLEAR_CM: i32 = 40;
/// Sal del sorteo de listones.
const SALT_WALL_RAIL: u32 = 0xA9_04_03;

/// ADR-105 enm. 16 — un listón por pared de tramo, si le toca: un tramo de tabla a
/// [`WALL_RAIL_Y_CM`] sobre el suelo, volando [`WALL_RAIL_PROUD_CM`] de la cara interior, que
/// nunca cruza una boca ni una pieza pegada a esa pared (pilastra, marco, división que muere en
/// ella) ni un vano de pared (ventana, hornacina). Decoración: no estampa, no frena.
fn wall_rails(
    seed: i32,
    segments: &[Wg3Segment],
    others: &[Wg3Solid],
    carves: &[Wg3Carve],
) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    let t = WALL_T_CM;
    // Las bocas de TODOS los tramos, en mundo: los tramos se solapan por el grosor de pared y una
    // boca del vecino cae sobre la misma línea de pared que la de este tramo.
    let mouths: Vec<(i32, i32, i32, i32)> = segments
        .iter()
        .flat_map(|g| {
            let (w, d) = (g.size_x_cm as f32 / 100.0, g.size_z_cm as f32 / 100.0);
            g.openings.iter().map(move |o| {
                let (lx, lz) =
                    super::placement::local_point(o.side, o.offset_cm as f32 / 100.0, w, d);
                (
                    g.x_cm + (lx * 100.0).round() as i32,
                    g.z_cm + (lz * 100.0).round() as i32,
                    o.width_cm / 2 + WALL_RAIL_CLEAR_CM,
                    g.floor_y_cm,
                )
            })
        })
        .collect();
    for g in segments {
        let (x0, z0) = (g.x_cm, g.z_cm);
        let (x1, z1) = (g.x_cm + g.size_x_cm, g.z_cm + g.size_z_cm);
        let y0 = g.floor_y_cm + WALL_RAIL_Y_CM;
        let y1 = y0 + WALL_RAIL_H_CM;
        if y1 > g.floor_y_cm + g.height_cm - 20 {
            continue;
        }
        let (cx, cz) = ((x0 + x1) as f32 / 200.0, (z0 + z1) as f32 / 200.0);
        for side in 0..4u8 {
            let length = if side.is_multiple_of(2) {
                g.size_x_cm
            } else {
                g.size_z_cm
            };
            if length < 2 * WALL_RAIL_CLEAR_CM + WALL_RAIL_LEN_CM.0 {
                continue;
            }
            let mut st = super::hash::stream_at(seed, cx, cz, SALT_WALL_RAIL + side as u32);
            if st.next01() >= WALL_RAIL_CHANCE {
                continue;
            }
            let len = WALL_RAIL_LEN_CM.0
                + (st.next01() * (WALL_RAIL_LEN_CM.1 - WALL_RAIL_LEN_CM.0) as f32) as i32;
            let len = len.min(length - 2 * WALL_RAIL_CLEAR_CM);
            let free = length - 2 * WALL_RAIL_CLEAR_CM - len;
            let off0 = WALL_RAIL_CLEAR_CM + (st.next01() * free as f32) as i32;
            let off1 = off0 + len;
            // Ninguna boca de este lado bajo el listón, con holgura.
            let crosses_mouth = g.openings.iter().any(|o| {
                o.side % 4 == side
                    && o.offset_cm - o.width_cm / 2 - WALL_RAIL_CLEAR_CM < off1
                    && o.offset_cm + o.width_cm / 2 + WALL_RAIL_CLEAR_CM > off0
            });
            if crosses_mouth {
                continue;
            }
            // Al mundo. Convención de `segment::emit_wall`: 0 = N (z máx, offset +x), 1 = E
            // (x máx, offset −z desde z máx), 2 = S (z mín, offset −x desde x máx), 3 = O (x mín,
            // offset +z).
            let p = WALL_RAIL_PROUD_CM;
            let r = match side {
                0 => (x0 + off0, z1 - t - p, x0 + off1, z1 - t),
                1 => (x1 - t - p, z1 - off1, x1 - t, z1 - off0),
                2 => (x1 - off1, z0 + t, x1 - off0, z0 + t + p),
                _ => (x0 + t, z0 + off0, x0 + t + p, z0 + off1),
            };
            let c = WALL_RAIL_CLEAR_CM;
            let grown = (r.0 - c, r.1 - c, r.2 + c, r.3 + c);
            let hits_solid = others.iter().any(|o| {
                o.bottom_y_cm < y1 + c
                    && o.top_y_cm > y0 - c
                    && o.x_cm < grown.2
                    && o.x_cm + o.size_x_cm > grown.0
                    && o.z_cm < grown.3
                    && o.z_cm + o.size_z_cm > grown.1
            });
            let hits_mouth = mouths.iter().any(|&(mx, mz, half, fl)| {
                (fl - g.floor_y_cm).abs() < 100
                    && mx + half > r.0
                    && mx - half < r.2
                    && mz + half > r.1
                    && mz - half < r.3
            });
            let hits_carve = carves.iter().any(|k| {
                k.bottom_y_cm < y1 + c
                    && k.top_y_cm > y0 - c
                    && k.x_cm < grown.2
                    && k.x_cm + k.size_x_cm > grown.0
                    && k.z_cm < grown.3
                    && k.z_cm + k.size_z_cm > grown.1
            });
            if hits_solid || hits_carve || hits_mouth {
                continue;
            }
            out.push(Wg3Solid {
                x_cm: r.0,
                z_cm: r.1,
                size_x_cm: r.2 - r.0,
                size_z_cm: r.3 - r.1,
                bottom_y_cm: y0,
                top_y_cm: y1,
                style: g.style | STYLE_DECOR_BIT,
                yaw_deg: 0,
                shape: SHAPE_BOX,
            });
        }
    }
    out
}

/// ADR-105 enm. 17 — grosores posibles del BLOQUE exento: las masas del Nivel 0 miden de uno a dos
/// metros, no treinta centímetros.
const BLOCK_T_CM: [i32; 3] = [100, 150, 200];
/// Largo del bloque, mínimo y máximo.
const BLOCK_LEN_CM: (i32, i32) = (250, 700);
/// Lo que el bloque deja libre a las paredes y a las bocas: el jugador pasa por los dos lados.
const BLOCK_CLEAR_CM: i32 = 150;
/// Lo que deja a otro macizo (pilar, división, otro bloque).
const BLOCK_GAP_CM: i32 = 100;
/// Área mínima de la sala.
const BLOCK_MIN_AREA_M2: f32 = 40.0;
/// Por encima de esto, dos bloques.
const BLOCK_TWO_AREA_M2: f32 = 150.0;
/// Sal del sorteo de bloques.
const SALT_BLOCK: u32 = 0xA9_04_04;

/// ADR-105 enm. 17 — ¿es un bloque? Grueso como un bloque, largo como un bloque, alto como una
/// sala. Definición única para el volcado y los tests.
pub(super) fn is_block(s: &Wg3Solid) -> bool {
    let (a, b) = (s.size_x_cm.min(s.size_z_cm), s.size_x_cm.max(s.size_z_cm));
    !s.is_decoration()
        && s.style != PIT_STYLE
        && s.style != PIT_SHAFT_STYLE
        && BLOCK_T_CM.contains(&a)
        && b >= BLOCK_LEN_CM.0
        && s.top_y_cm - s.bottom_y_cm >= 250
}

/// ADR-105 enm. 17 — los BLOQUES exentos: la masa gruesa del Nivel 0, de suelo a techo, en mitad
/// de una sala. Uno o dos por sala, dentro de UN tramo (como los pozos: una sala son varios tramos
/// con paredes), a [`BLOCK_CLEAR_CM`] de paredes y bocas de cualquier tramo, y a [`BLOCK_GAP_CM`]
/// de todo macizo ya emitido de su planta: pilares, divisiones, otros bloques. Esquiva pozos,
/// rellanos, huecos de escalera, agujeros de arriba y piezas del catálogo. Es macizo de verdad:
/// estampa y frena.
#[allow(clippy::too_many_arguments)]
fn wall_blocks(
    building: &RegionBuilding,
    manifest: &Wg3Manifest,
    placements: &[Wg3Placement],
    segments: &[Wg3Segment],
    others: &[Wg3Solid],
    carves: &[Wg3Carve],
) -> Vec<Wg3Solid> {
    let mut out: Vec<Wg3Solid> = Vec::new();
    let seed = building.seed;
    let taken: Vec<(f32, f32, f32, f32)> = placements
        .iter()
        .filter_map(|p| {
            manifest
                .pieces
                .get(p.piece as usize)
                .map(|piece| p.bounds(piece))
        })
        .collect();
    let mouths: Vec<(i32, i32, i32, i32)> = segments
        .iter()
        .flat_map(|g| {
            let (w, d) = (g.size_x_cm as f32 / 100.0, g.size_z_cm as f32 / 100.0);
            g.openings.iter().map(move |o| {
                let (lx, lz) =
                    super::placement::local_point(o.side, o.offset_cm as f32 / 100.0, w, d);
                (
                    g.x_cm + (lx * 100.0).round() as i32,
                    g.z_cm + (lz * 100.0).round() as i32,
                    o.width_cm / 2 + BLOCK_CLEAR_CM,
                    g.floor_y_cm,
                )
            })
        })
        .collect();
    let overlaps_box = |r: &super::plan::PlanRect, o: &Wg3Solid| -> bool {
        o.x_cm < r.max_x_cm
            && o.x_cm + o.size_x_cm > r.min_x_cm
            && o.z_cm < r.max_z_cm
            && o.z_cm + o.size_z_cm > r.min_z_cm
    };

    for (n, plan) in building.storeys.iter().enumerate() {
        let landings: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below + 1 == n)
            .map(|w| w.rect.shrunk(-50))
            .collect();
        let wells_here: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below == n)
            .map(|w| w.rect.shrunk(-50))
            .collect();
        let holes_above = hole_squares_above(building, n);
        let pits_here = if n == 0 {
            pit_rects_of(building, segments)
        } else {
            Vec::new()
        };
        for (_, s) in plan.built() {
            if s.role.is_circulation()
                || s.role == SpaceRole::Stair
                || s.rise_cm != 0
                || s.area_m2() < BLOCK_MIN_AREA_M2
            {
                continue;
            }
            let kn = knobs_of(seed, s);
            let (cx, cz) = s.rect.centre_m();
            let mut st = super::hash::stream_at(seed, cx, cz, SALT_BLOCK);
            if st.next01() >= kn.block {
                continue;
            }
            let own_hole = hole_square(&s.rect).shrunk(-50);
            let floor = s.floor_y_cm;
            let top = floor + clear_height_cm(s);
            // Los tramos de esta sala: los suyos, a su cota, enteros sobre su suelo.
            let hosts: Vec<&Wg3Segment> = segments
                .iter()
                .filter(|g| {
                    g.floor_y_cm == floor
                        && s.covers_rect(&super::plan::PlanRect {
                            min_x_cm: g.x_cm,
                            min_z_cm: g.z_cm,
                            max_x_cm: g.x_cm + g.size_x_cm,
                            max_z_cm: g.z_cm + g.size_z_cm,
                        })
                })
                .collect();
            if hosts.is_empty() {
                continue;
            }
            let count = if s.area_m2() >= BLOCK_TWO_AREA_M2 {
                2
            } else {
                1
            };
            let mut placed: Vec<Wg3Solid> = Vec::new();
            for _ in 0..count {
                for _attempt in 0..6 {
                    let g = hosts[(st.next01() * hosts.len() as f32) as usize % hosts.len()];
                    let inset = WALL_T_CM + BLOCK_CLEAR_CM;
                    let inner = super::plan::PlanRect {
                        min_x_cm: g.x_cm + inset,
                        min_z_cm: g.z_cm + inset,
                        max_x_cm: g.x_cm + g.size_x_cm - inset,
                        max_z_cm: g.z_cm + g.size_z_cm - inset,
                    };
                    let t = BLOCK_T_CM[(st.next01() * 3.0) as usize % 3];
                    let along_x = st.next01() < 0.5;
                    let room = if along_x {
                        inner.width_cm()
                    } else {
                        inner.depth_cm()
                    };
                    let across = if along_x {
                        inner.depth_cm()
                    } else {
                        inner.width_cm()
                    };
                    if room < BLOCK_LEN_CM.0 || across < t {
                        continue;
                    }
                    let len = (BLOCK_LEN_CM.0
                        + (st.next01() * (BLOCK_LEN_CM.1 - BLOCK_LEN_CM.0) as f32) as i32)
                        .min(room);
                    let (w, d) = if along_x { (len, t) } else { (t, len) };
                    let x = inner.min_x_cm
                        + ((st.next01() * (inner.width_cm() - w) as f32) as i32 / 10) * 10;
                    let z = inner.min_z_cm
                        + ((st.next01() * (inner.depth_cm() - d) as f32) as i32 / 10) * 10;
                    let r = super::plan::PlanRect {
                        min_x_cm: x,
                        min_z_cm: z,
                        max_x_cm: x + w,
                        max_z_cm: z + d,
                    };
                    let grown = r.shrunk(-BLOCK_GAP_CM);
                    let near_mouth = mouths.iter().any(|&(mx, mz, half, fl)| {
                        (fl - floor).abs() < 100
                            && mx + half > r.min_x_cm
                            && mx - half < r.max_x_cm
                            && mz + half > r.min_z_cm
                            && mz - half < r.max_z_cm
                    });
                    let on_piece = taken.iter().any(|&(x0, z0, x1, z1)| {
                        x1 * 100.0 > grown.min_x_cm as f32
                            && x0 * 100.0 < grown.max_x_cm as f32
                            && z1 * 100.0 > grown.min_z_cm as f32
                            && z0 * 100.0 < grown.max_z_cm as f32
                    });
                    let y_hits = |o: &Wg3Solid| o.bottom_y_cm < top && o.top_y_cm > floor;
                    let blocked = !s.covers_rect(&grown)
                        || near_mouth
                        || on_piece
                        || landings.iter().any(|l| l.overlaps(&grown))
                        || wells_here.iter().any(|w| w.overlaps(&grown))
                        || holes_above.iter().any(|h| h.overlaps(&grown))
                        || pits_here.iter().any(|p| p.overlaps(&grown))
                        || (n > 0 && own_hole.overlaps(&grown))
                        || others
                            .iter()
                            .any(|o| !o.is_decoration() && y_hits(o) && overlaps_box(&grown, o))
                        || placed.iter().any(|o| overlaps_box(&grown, o))
                        || carves.iter().any(|k| {
                            k.bottom_y_cm < top
                                && k.top_y_cm > floor
                                && k.x_cm < grown.max_x_cm
                                && k.x_cm + k.size_x_cm > grown.min_x_cm
                                && k.z_cm < grown.max_z_cm
                                && k.z_cm + k.size_z_cm > grown.min_z_cm
                        });
                    if blocked {
                        continue;
                    }
                    placed.push(Wg3Solid {
                        x_cm: r.min_x_cm,
                        z_cm: r.min_z_cm,
                        size_x_cm: w,
                        size_z_cm: d,
                        bottom_y_cm: floor,
                        top_y_cm: top,
                        style: style_of(s.role),
                        yaw_deg: 0,
                        shape: SHAPE_BOX,
                    });
                    break;
                }
            }
            out.extend(placed);
        }
    }
    out
}

/// ADR-104 D3 — **abrir el atrio por arriba, porque hasta aquí era un pozo SELLADO.**
///
/// El atrio ya medía dos plantas —eso lo hizo D1 y está verificado en el ráster— y aun así no se veía
/// desde la planta alta: `segment::emit_side` emite las cuatro paredes de cada tramo a altura
/// COMPLETA, cortadas sólo por sus bocas, así que los espacios de arriba que dan al vacío le plantan
/// un muro de 3,08 m. Un atrio que sólo existe para quien está dentro no es un atrio: es una sala con
/// el techo alto.
///
/// **Se resuelve restando, y por eso no cuesta wire.** `Wg3Carve` ya viaja desde ADR-101 y el cliente
/// ya lo aplica antes de malla y colisión; un vano es exactamente esto. La caja va:
///
/// - **En horizontal**, la huella del atrio ensanchada [`CARVE_DEPTH_M`] — el mismo medio metro que usa
///   la absorción, y por lo mismo: las paredes de arriba viven en el rectángulo del VECINO, pegadas a
///   la frontera, así que con la huella exacta no se toca ninguna.
/// - **En vertical**, desde la cota del suelo de la planta de arriba hasta el techo del atrio. Ni un
///   centímetro por debajo: la losa de la planta alta cuelga en `[320, 332]`, y empezar más abajo se
///   la llevaría por delante — sería abrir un agujero en el suelo del vecino en vez de tirar su pared.
///
/// # Todo borde es `Open`, y hoy no puede ser otra cosa
///
/// ADR-104 D3 declaraba dos bordes: `Balcony` con pretil y `Open` sin él. **Sólo `Open` se puede
/// construir sin tocar el cable.** Un pretil es una caja NUEVA de altura reducida, y `Wg3Segment` no
/// tiene dónde declararla: emitirla pediría campo nuevo, o sea bump de wire y ADR. Restar sabe hacerlo
/// el sistema; añadir un muro bajo, no. Queda como enmienda, y mientras tanto el borde de un atrio es
/// un sitio del que se cae — que es la mitad de lo que se pidió.
/// Altura de un pretil, en centímetros.
///
/// **A la altura del pecho: tiene que parar sin tapar.** Un pretil bajo no se lee como protección y
/// uno alto convierte el balcón en una pared, que es exactamente lo que ADR-104 D3 vino a quitar.
const PARAPET_H_CM: i32 = 110;

/// Grosor de un pretil, en centímetros.
///
/// **Y aquí el ráster cobra su peaje, que es el aviso de ADR-105 D6.** Veinte centímetros macizan la
/// celda de cincuenta que tocan, así que un pretil se come medio metro de suelo andable a cada lado
/// del borde. Es el precio de que exista, y hay que medirlo y no suponerlo.
const PARAPET_T_CM: i32 = 20;

/// Tramo máximo de un macizo, en centímetros.
///
/// Muy por debajo del chunk (50 m) a propósito: un macizo se dibuja en el chunk de su CENTRO, así que
/// cuanto más largo, más lejos de su geometría puede caer el objeto que lo monta. Partirlo no cuesta
/// nada y mantiene cada trozo cerca de donde se ve.
const MAX_SOLID_CM: i32 = 2000;

/// Lado mínimo de un pilar, en centímetros.
///
/// **Dos metros, y por lo mismo que un agujero mide dos:** cuatro celdas del ráster. Un pilar fino
/// sale caro en colisión —el rasterizado conservador maciza toda celda que toque— y pequeño en la
/// vista, que es el peor cambio posible. Por eso son MEGApilares.
const PILLAR_SIDE_MIN_CM: i32 = 200;

/// Lado máximo de un pilar, en centímetros (ADR-105 enm. 4).
///
/// Cuatro metros es un pilar que ya no se rodea con la vista: tapa una sala entera detrás. Por
/// encima deja de ser pilar y es un núcleo, que es otro caso y otra gramática.
const PILLAR_SIDE_MAX_CM: i32 = 400;

/// El lado se sortea en pasos de UNA celda del ráster, para que el pilar dibujado y el estampado
/// midan lo mismo y no haya medio metro de aire o de macizo de más en un lado.
const PILLAR_SIDE_STEP_CM: i32 = 50;

/// Lado a partir del cual un pilar puede ser una CRUZ de dos cajas concéntricas.
///
/// Con 2 m los brazos miden una celda y el ráster conservador los engorda hasta parecer un cuadrado
/// con muescas; a partir de 3 m el brazo mide 1,50 y la cruz se lee como cruz.
const PILLAR_CROSS_MIN_SIDE_CM: i32 = 300;

/// Qué proporción de las naves con pilares los lleva en cruz.
const PILLAR_CROSS_CHANCE: f32 = 0.30;

/// ADR-105 enm. 10 — qué proporción de las naves con pilares cuadrados les pone zapata y capitel.
const PILLAR_TRIM_CHANCE: f32 = 0.40;
/// Cuánto sobresale la zapata (y el capitel) por cada lado del pilar.
const PILLAR_TRIM_OUT_CM: i32 = 30;
/// Alto de la zapata y del capitel. Treinta y no veinticinco: por encima del escalón del jugador
/// (27), para que la zapata sea parte del pilar y no un bordillo que se sube.
const PILLAR_TRIM_H_CM: i32 = 30;

/// ADR-105 enm. 10 — **la PILASTRA**: un pilar adosado a la pared, en pasillos y naves.
///
/// Es el ritmo de pared que un pasillo de Backrooms tiene y el nuestro no tenía: un resalte cada
/// pocos metros, en las dos paredes largas, a tresbolillo. Cuesta poco paso —25 cm de fondo, que el
/// ráster convierte en su celda— y por eso sólo va donde la circulación mide `PILASTER_MIN_WIDTH_CM`
/// o más, y nunca a menos de `PILASTER_DOOR_CLEAR_CM` de una boca.
const PILASTER_W_CM: i32 = 60;
const PILASTER_D_CM: i32 = 25;
const PILASTER_PITCH_CM: (i32, i32) = (400, 600);
const PILASTER_MARGIN_CM: i32 = 200;
const PILASTER_DOOR_CLEAR_CM: i32 = 150;
const PILASTER_MIN_WIDTH_CM: i32 = 280;
/// ADR-125 — la pilastra en MEDIA LUNA: medio cilindro adosado a la pared, 1 m de cuerda y 50 cm
/// de panza. Es la «media luna» que Joel pidió el 2026-09-04, y la primera forma curva que se
/// toca al pasar. Cuesta el doble de paso que la recta (50 frente a 25), así que va con las
/// mismas exclusiones y sólo en los espacios que sortean `round_pilaster`.
const PILASTER_ROUND_W_CM: i32 = 100;
const PILASTER_ROUND_D_CM: i32 = 50;

/// ¿Es una pilastra en media luna de [`wall_pilasters`]? Por la forma del cable, no por medidas:
/// ningún otro macizo lleva `SHAPE_HALF_CYLINDER` hoy.
pub(super) fn is_round_pilaster(s: &Wg3Solid) -> bool {
    s.shape == SHAPE_HALF_CYLINDER
        && s.size_x_cm == PILASTER_ROUND_W_CM
        && s.size_z_cm == PILASTER_ROUND_D_CM
}
const PILASTER_ROOM_CHANCE: f32 = 0.50;
const SALT_PILASTER: u32 = 0xB1_11_A0_08;

/// ¿Es este macizo una pilastra? Por la forma: 25 de fondo, 60 de ancho, y más alto que un cuerpo.
pub(super) fn is_pilaster(s: &Wg3Solid) -> bool {
    s.size_x_cm.min(s.size_z_cm) == PILASTER_D_CM
        && s.size_x_cm.max(s.size_z_cm) == PILASTER_W_CM
        && s.top_y_cm - s.bottom_y_cm > 200
}

fn wall_pilasters(
    building: &RegionBuilding,
    seg_doors: &[(i32, i32, i32)],
    carves: &[Wg3Carve],
    segments: &[Wg3Segment],
) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    let seed = building.seed;
    for (n, plan) in building.storeys.iter().enumerate() {
        let mut keep_out: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below + 1 == n || w.storey_below == n)
            .map(|w| w.rect.shrunk(-50))
            .collect();
        keep_out.extend(hole_squares_above(building, n));
        if n == 0 {
            keep_out.extend(pit_rects_of(building, segments));
        }
        for (i, s) in plan.built() {
            if !(s.role.is_circulation() || s.role == SpaceRole::Hall)
                || s.is_composite()
                || s.rise_cm != 0
                || is_atrium(s)
            {
                continue;
            }
            let r = s.rect;
            let wide = r.width_cm() >= r.depth_cm();
            let short = r.width_cm().min(r.depth_cm());
            if short < PILASTER_MIN_WIDTH_CM {
                continue;
            }
            let (cx, cz) = r.centre_m();
            let mut st = super::hash::stream_at(seed, cx, cz, SALT_PILASTER);
            if st.next01() >= knobs_of(seed, s).pilaster_room {
                continue;
            }
            let pitch = PILASTER_PITCH_CM.0
                + (st.next01() * (PILASTER_PITCH_CM.1 - PILASTER_PITCH_CM.0) as f32) as i32;
            // ADR-125 — recta o en media luna, por espacio. Sorteo después del paso: los espacios
            // que ya tenían pilastras conservan su ritmo.
            let round = st.next01() < knobs_of(seed, s).round_pilaster;
            let (w, d) = if round {
                (PILASTER_ROUND_W_CM, PILASTER_ROUND_D_CM)
            } else {
                (PILASTER_W_CM, PILASTER_D_CM)
            };
            let long = r.width_cm().max(r.depth_cm());
            let usable = long - 2 * PILASTER_MARGIN_CM - w;
            let count = usable / pitch;
            if count < 1 {
                continue;
            }
            let step = usable / count;
            let clear = clear_height_cm(s);
            let style = style_of(s.role);
            // Todas las bocas de este espacio: las del plan, las de junta y las de los tramos.
            let doors: Vec<(i32, i32)> = plan
                .links
                .iter()
                .filter(|l| l.a == i || l.b == i)
                .map(|l| (l.at_x_cm, l.at_z_cm))
                .chain(
                    plan.gates
                        .iter()
                        .filter(|g| g.space == i)
                        .map(|g| (g.x_cm, g.z_cm)),
                )
                .chain(
                    seg_doors
                        .iter()
                        .filter(|&&(x, z, floor)| floor == s.floor_y_cm && on_space_wall(s, x, z))
                        .map(|&(x, z, _)| (x, z)),
                )
                .collect();

            // Las dos paredes largas: `0` la de coordenada mínima, `1` la máxima. La segunda va a
            // tresbolillo, medio paso corrida.
            for wall in 0..2 {
                let shift = if wall == 1 { step / 2 } else { 0 };
                for k in 0..=count {
                    let along = (if wide { r.min_x_cm } else { r.min_z_cm })
                        + PILASTER_MARGIN_CM
                        + k * step
                        + shift;
                    if along + w > (if wide { r.max_x_cm } else { r.max_z_cm }) - PILASTER_MARGIN_CM
                    {
                        break;
                    }
                    // Desde la cara interior del muro hacia dentro. `foot` es la HUELLA en el
                    // mundo (lo que se excluye y se comprueba); la caja del cable de la media luna
                    // es la caja sin girar centrada en ella, ver abajo.
                    let foot = if wide {
                        let z = if wall == 0 {
                            r.min_z_cm + WALL_T_CM
                        } else {
                            r.max_z_cm - WALL_T_CM - d
                        };
                        super::plan::PlanRect {
                            min_x_cm: along,
                            min_z_cm: z,
                            max_x_cm: along + w,
                            max_z_cm: z + d,
                        }
                    } else {
                        let x = if wall == 0 {
                            r.min_x_cm + WALL_T_CM
                        } else {
                            r.max_x_cm - WALL_T_CM - d
                        };
                        super::plan::PlanRect {
                            min_x_cm: x,
                            min_z_cm: along,
                            max_x_cm: x + d,
                            max_z_cm: along + w,
                        }
                    };
                    let near = foot.shrunk(-PILASTER_DOOR_CLEAR_CM);
                    // Y ningún VANO de pared (ventana, rendija, hornacina: enm. 6 y 12) bajo la
                    // pilastra: un macizo es inmune a los vanos, así que la pilastra tapaba la
                    // ventana y el test la cazó (−22,85, 391,58) al subir las dos por zona.
                    let on_carve = on_wall_carve(&foot, s, carves);
                    if doors.iter().any(|&(dx, dz)| near.contains_point(dx, dz))
                        || keep_out.iter().any(|k| k.overlaps(&foot))
                        || on_carve
                        || !s.covers_rect(&foot)
                    {
                        continue;
                    }
                    if round {
                        // La media luna viaja como su caja SIN girar (cuerda × panza) centrada en
                        // la huella, con la cara plana en la z mínima local y la panza hacia +z
                        // local; el giro la pone contra cada pared: 0 = pared de z mínima, 180 =
                        // de z máxima, 90 = de x mínima (local +z va a mundo +x), 270 = de x
                        // máxima.
                        let (cx, cz) = (
                            (foot.min_x_cm + foot.max_x_cm) / 2,
                            (foot.min_z_cm + foot.max_z_cm) / 2,
                        );
                        let yaw_deg = match (wide, wall) {
                            (true, 0) => 0,
                            (true, _) => 180,
                            (false, 0) => 90,
                            (false, _) => 270,
                        };
                        out.push(Wg3Solid {
                            x_cm: cx - w / 2,
                            z_cm: cz - d / 2,
                            size_x_cm: w,
                            size_z_cm: d,
                            bottom_y_cm: s.floor_y_cm,
                            top_y_cm: s.floor_y_cm + clear,
                            style,
                            yaw_deg,
                            shape: SHAPE_HALF_CYLINDER,
                        });
                        continue;
                    }
                    out.push(Wg3Solid {
                        x_cm: foot.min_x_cm,
                        z_cm: foot.min_z_cm,
                        size_x_cm: foot.width_cm(),
                        size_z_cm: foot.depth_cm(),
                        bottom_y_cm: s.floor_y_cm,
                        top_y_cm: s.floor_y_cm + clear,
                        style,
                        yaw_deg: 0,
                        shape: SHAPE_BOX,
                    });
                }
            }
        }
    }
    out
}

/// ¿Este macizo es un pilar de [`hall_pillars`]? Lo usan los tests para separarlos de tabiques y
/// pretiles sin un campo de cable: un pilar es cuadrado con lado en el rango, o un brazo de cruz
/// (una dimensión el lado y la otra la mitad o menos, nunca por debajo de dos celdas). Un tabique
/// mide 30 de grosor y un pretil 20, así que ninguno cae dentro.
pub(super) fn is_pillar(s: &Wg3Solid) -> bool {
    let (a, b) = (s.size_x_cm.min(s.size_z_cm), s.size_x_cm.max(s.size_z_cm));
    let long_ok =
        (PILLAR_SIDE_MIN_CM..=PILLAR_SIDE_MAX_CM).contains(&b) && b % PILLAR_SIDE_STEP_CM == 0;
    long_ok
        && (a == b || (a >= 2 * PILLAR_SIDE_STEP_CM && a <= b / 2 && a % PILLAR_SIDE_STEP_CM == 0))
}

/// Superficie mínima de un atrio para que lleve pilares, en m².
///
/// Por debajo de esto los pilares no articulan el espacio: lo llenan.
const PILLAR_MIN_AREA_M2: f32 = 300.0;

/// ADR-105 D5 — **los dos casos con nombre: el PRETIL de un balcón y el MEGAPILAR de un atrio.**
///
/// La tabla de casos de ADR-105 es cerrada a propósito: un canal que acepta cajas arbitrarias es, sin
/// acotar, un segundo sistema de geometría sin disciplina, y quitar eso fue el motivo entero de
/// ADR-100. Añadir un caso aquí es una enmienda al ADR, no una tarde.
///
/// # Por qué el pretil va por FUERA del rectángulo del atrio
///
/// El suelo de la planta alta empieza donde acaba el hueco, así que el pretil se apoya en el primer
/// palmo de ese suelo y no en el aire. Y cae dentro de la banda que el vano de atrio dejó limpia —lo
/// que es correcto y no accidental: **los macizos son inmunes a los vanos** (D2), así que el mismo
/// medio metro que quitó la pared es donde ahora se pone la barandilla.
///
/// # Y sólo donde hay suelo al lado
///
/// Un pretil en un lado del atrio que da al vacío es una valla flotando. Se comprueba lado a lado
/// contra los espacios construidos de la planta de arriba.
fn atrium_solids(building: &RegionBuilding) -> Vec<Wg3Solid> {
    let mut out = Vec::new();

    for (n, plan) in building.storeys.iter().enumerate() {
        let above = match building.storeys.get(n + 1) {
            Some(a) => a,
            None => continue,
        };
        for s in plan.spaces.iter().filter(|s| is_atrium(s)) {
            let style = style_of(s.role);
            let r = s.rect;
            let deck_y = s.floor_y_cm + STOREY_HEIGHT_CM;

            // ---- PRETILES, lado a lado ----
            for side in 0..4u8 {
                // La franja de suelo que habría al otro lado del borde. Media celda basta: lo que se
                // pregunta es si hay planta ahí, no cuánta.
                let probe = match side {
                    0 => super::plan::PlanRect {
                        min_x_cm: r.min_x_cm,
                        min_z_cm: r.max_z_cm,
                        max_x_cm: r.max_x_cm,
                        max_z_cm: r.max_z_cm + 50,
                    },
                    1 => super::plan::PlanRect {
                        min_x_cm: r.max_x_cm,
                        min_z_cm: r.min_z_cm,
                        max_x_cm: r.max_x_cm + 50,
                        max_z_cm: r.max_z_cm,
                    },
                    2 => super::plan::PlanRect {
                        min_x_cm: r.min_x_cm,
                        min_z_cm: r.min_z_cm - 50,
                        max_x_cm: r.max_x_cm,
                        max_z_cm: r.min_z_cm,
                    },
                    _ => super::plan::PlanRect {
                        min_x_cm: r.min_x_cm - 50,
                        min_z_cm: r.min_z_cm,
                        max_x_cm: r.min_x_cm,
                        max_z_cm: r.max_z_cm,
                    },
                };
                if !above
                    .spaces
                    .iter()
                    .any(|t| t.role.is_built() && t.rect.overlaps(&probe))
                {
                    continue;
                }

                // El pretil ocupa el borde entero del lado, partido en tramos manejables.
                let along_x = side.is_multiple_of(2);
                let (from, to) = if along_x {
                    (r.min_x_cm, r.max_x_cm)
                } else {
                    (r.min_z_cm, r.max_z_cm)
                };
                let mut at = from;
                while at < to {
                    let end = (at + MAX_SOLID_CM).min(to);
                    let (x, z, sx, sz) = match side {
                        0 => (at, r.max_z_cm, end - at, PARAPET_T_CM),
                        1 => (r.max_x_cm, at, PARAPET_T_CM, end - at),
                        2 => (at, r.min_z_cm - PARAPET_T_CM, end - at, PARAPET_T_CM),
                        _ => (r.min_x_cm - PARAPET_T_CM, at, PARAPET_T_CM, end - at),
                    };
                    out.push(Wg3Solid {
                        x_cm: x,
                        z_cm: z,
                        size_x_cm: sx,
                        size_z_cm: sz,
                        bottom_y_cm: deck_y,
                        top_y_cm: deck_y + PARAPET_H_CM,
                        style,
                        yaw_deg: 0,
                        shape: SHAPE_BOX,
                    });
                    at = end;
                }
            }
        }
    }
    out
}

/// Separación entre pilares, mínimo y máximo de centro a centro, en centímetros.
///
/// **El rango es la mitad de la variedad**, y por eso son dos números y no uno: `PILLAR_SPACING_CM`
/// era una constante y producía la retícula perfecta que ADR-119 enm. 1 viene a quitar. Se sortea
/// una separación por EJE y por SALA, así que una nave puede tener las filas juntas y las columnas
/// separadas — que es el arquetipo `UNEVEN_ROWS` sin una sola línea dedicada a él.
///
/// **Desde ADR-105 enm. 4 se sortea el PASO LIBRE, cara a cara, y el lado se suma después.** Con el
/// lado variable, sortear de centro a centro dejaba que un pilar de 4 m se comiera el paso que un
/// pilar de 2 m dejaba: lo que se anda es el hueco, así que es el hueco lo que tiene mínimo.
///
/// El suelo son 5 m (los 7 de centro a centro de antes menos los 2 del pilar) porque el rasterizado
/// conservador se come otro medio metro por cara: con menos, el paso libre baja de 3,50 m y una nave
/// con pilares empieza a leerse como un laberinto de pilares.
const PILLAR_GAP_MIN_CM: i32 = 500;
const PILLAR_GAP_MAX_CM: i32 = 1100;

/// Paso libre máximo en zona DENSA o ANÓMALA del campo de densidad: el bosque de pilares.
///
/// Recortar el máximo y no el mínimo es lo que hace que el bosque siga siendo andable: el hueco
/// nunca baja de los 5 m, sólo deja de haber naves con los pilares a 15 m.
const PILLAR_GAP_MAX_DENSE_CM: i32 = 800;

/// Qué proporción de las naves que CABEN llevan pilares en zona densa o anómala.
const PILLAR_ROOM_CHANCE_DENSE: f32 = 0.90;

/// Franja contra la pared donde no va pilar, en centímetros. Es el primer vano del edificio.
///
/// FIJA, y no medio paso como en la primera versión. Con medio paso, una separación sorteada de
/// 13 m dejaba 6,5 m muertos contra cada muro y en una nave de 300 m² ya no cabía más que UN pilar:
/// medido, el 57,9 % de las naves con pilares tenían uno o dos. **Un pilar suelto no es una sala de
/// pilares; es una sala con una columna**, y eso se lee como un descuido, no como arquitectura.
const PILLAR_WALL_MARGIN_CM: i32 = 350;

/// Pilares mínimos por eje para que una nave lleve retícula.
///
/// Dos, o ninguno. Una fila sola no articula el espacio ni produce las líneas de visión que se
/// buscaban: se lee como un obstáculo. Una nave que no llega a dos por los dos ejes se queda
/// diáfana, que además es lo que hace que las que sí la tienen signifiquen algo.
const PILLAR_MIN_PER_AXIS: i32 = 2;

/// Cuánto puede moverse un pilar de su sitio en la retícula, en centímetros.
///
/// `SLIGHTLY_IRREGULAR` y `SHIFTED_PILLAR` del encargo, y son el mismo número: una retícula perfecta
/// se lee como generada, y medio metro de desorden basta para que no lo parezca sin que el sitio
/// deje de tener orden. Se sortea por PILAR con su propia posición, así que dos regiones vecinas no
/// comparten el desorden y el mismo pilar está siempre donde estaba.
const PILLAR_JITTER_MAX_CM: i32 = 55;

/// Probabilidad máxima de que a un pilar de la retícula le toque NO existir.
///
/// `MISSING_PILLAR`. Se sortea un tope por sala y luego pilar a pilar, así que hay naves completas y
/// naves a las que les faltan tres columnas. **Nunca llega a 1**: una nave que sortea 20 % pierde
/// uno de cada cinco, no la mitad, y sigue leyéndose como una retícula con huecos y no como ruido.
const PILLAR_OMIT_MAX: f32 = 0.20;

/// Qué proporción de las naves que CABEN llevan pilares.
///
/// No todas, y es una decisión del encargo: «no poner pilares en todas las salas». Una nave diáfana
/// al lado de una nave con pilares es lo que hace que la segunda signifique algo.
const PILLAR_ROOM_CHANCE: f32 = 0.62;

/// Distancia mínima entre un pilar y el punto de un vano, en centímetros.
///
/// Medio vano ancho (250) más el medio pilar (100) más holgura. Un pilar plantado en la boca de una
/// puerta no es arquitectura: es el paso cortado, y el ráster lo estampa macizo sin quejarse — que es
/// exactamente el modo de fallo que ADR-118 pasó una auditoría entera persiguiendo.
const PILLAR_DOOR_CLEAR_CM: i32 = 400;

/// Sal de la gramática de pilares de una SALA.
const SALT_PILLAR_ROOM: u32 = 0xB1_11_A0_00;
/// Sal del sorteo de CADA pilar.
const SALT_PILLAR_ONE: u32 = 0xB1_11_A0_01;

/// ADR-105 enmienda 1 (ADR-119 enm. 1 en el registro) — **EL PILAR SALE DEL ATRIO.**
///
/// # Qué cambia respecto a ADR-105 D5
///
/// La tabla de casos de ADR-105 era cerrada —pretil y megapilar de atrio— y decía con todas las
/// letras que ampliarla es una enmienda. Ésta es la enmienda, y la razón es medible: tras ADR-119 el
/// papel `Hall` cubre el 19,5 % de los espacios y el 21,5 % del mundo pasa de 300 m², pero los
/// pilares seguían encerrados en `is_atrium`, o sea en las naves que además tienen vacío
/// intencionado justo encima. Una nave de 800 m² completamente vacía no se lee como un espacio: se
/// lee como que falta contenido, que es la queja que abrió la Fase 2.
///
/// **Lo que NO cambia**: sigue siendo un [`Wg3Solid`], sigue siendo inmune a los vanos (D2), sigue
/// viajando por el chunk de su centro (D3) y sigue pagando el peaje del ráster (D6). No hay canal
/// nuevo ni tipo nuevo: lo que se amplía es DÓNDE se emite, que era lo único que estaba cerrado.
///
/// # La gramática, y por qué no hay un caso por variante
///
/// El encargo pide ocho variantes (retícula, retícula desplazada, ligeramente irregular, pilar
/// ausente, pilar desplazado, filas desiguales, zona densa, zona abierta). **Ninguna es una rama de
/// código.** Salen de cuatro números sorteados por SALA —separación en X, separación en Z, si las
/// filas impares van a media separación, y cuánto se omite— más dos sorteados por PILAR —si existe y
/// cuánto se desplaza—. Ocho nombres, seis números, cero `match`.
///
/// Y las dos últimas —`DENSE_SECTION` y `OPEN_SECTION`— salen del campo de densidad, que hasta hoy
/// no tenía consumidor (ADR-118 D4 lo dejó así a propósito): dentro de la MISMA nave, un pilar que
/// cae en zona vacía o dispersa tiene el doble de probabilidades de no existir. Eso da claros y
/// espesuras dentro de una sola sala sin inventar un segundo campo espacial, que es justo lo que ese
/// campo se escribió para evitar.
///
/// # Todo sorteo parte de la POSICIÓN (R3)
///
/// El de la sala, del centro de la sala; el del pilar, del sitio del pilar. Ni un índice ni un
/// contador: dos regiones vecinas tienen que producir el mismo pilar en el mismo sitio sin hablarse,
/// y un `for` con contador rompe eso en cuanto cambia el número de salas.
fn hall_pillars(
    building: &RegionBuilding,
    manifest: &Wg3Manifest,
    placements: &[Wg3Placement],
    segments: &[Wg3Segment],
) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    let seed = building.seed;

    // Huellas ya ocupadas por una pieza del catálogo: una pieza trae su interior horneado y un pilar
    // dentro es un bloque en mitad de un salón que nadie dibujó.
    let taken: Vec<(f32, f32, f32, f32)> = placements
        .iter()
        .filter_map(|p| {
            manifest
                .pieces
                .get(p.piece as usize)
                .map(|piece| p.bounds(piece))
        })
        .collect();

    for (n, plan) in building.storeys.iter().enumerate() {
        // Rellanos que ATERRIZAN en esta planta, con medio metro de margen: el cuerpo del jugador
        // (radio 0,35) más holgura. Un pilar encima de la salida de una escalera deja el cuerpo
        // clavado a media subida — ya pasó, y está anotado en `atrium_solids`.
        let landings: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below + 1 == n)
            .map(|w| w.rect.shrunk(-50))
            .collect();
        // Y los huecos de escalera que ARRANCAN en ésta: su boca es un pozo abierto.
        let wells_here: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below == n)
            .map(|w| w.rect.shrunk(-50))
            .collect();
        // **Y donde la planta de ARRIBA puede abrir un agujero, tampoco.** Un pilar llega al techo,
        // o sea justo debajo del forjado que el agujero perfora: quien se tira cae sobre el pilar y no
        // baja la planta entera. Con pilares de 2 m casi nunca coincidían; con 4 m y retícula densa
        // lo hicieron, y `a_hole_drops_you_a_whole_storey` bajó de 8 a 7.
        let holes_above = hole_squares_above(building, n);
        // ADR-126 D5 — y la rejilla de pozos de la planta baja, por lo mismo.
        let pits_here = if n == 0 {
            pit_rects_of(building, segments)
        } else {
            Vec::new()
        };

        for (i, s) in plan.built() {
            if s.role != SpaceRole::Hall || s.rise_cm != 0 {
                continue;
            }
            let r = s.rect;
            if s.area_m2() < PILLAR_MIN_AREA_M2 {
                continue;
            }
            // El agujero de ESTA sala, si es de planta alta: un macizo es inmune a los vanos, así que
            // un pilar ahí no desaparece al restar — sale encima y tapa la caída.
            let own_hole = hole_square(&r).shrunk(-50);

            let (cx, cz) = r.centre_m();
            // ADR-105 enm. 4 — **el CAMPO decide cuánta masa.** En zona densa o anómala casi toda
            // nave lleva pilares, más anchos y más juntos: el «bosque de pilares». Sale del campo de
            // densidad que ya consumía el sorteo de cada pilar, no de un segundo campo espacial.
            // Enm. 14 — el carácter de la zona decide si hay pilares y si es bosque.
            let k = knobs_of(seed, s);
            let dense = k.pillar_forest;
            let mut room = super::hash::stream_at(seed, cx, cz, SALT_PILLAR_ROOM);
            if room.next01() >= k.pillar_room {
                continue;
            }
            // Los sorteos de siempre van PRIMERO y en el mismo orden: una nave que ya tenía
            // retícula conserva su paso, su desfase y sus ausencias, y sólo estrena lado y cruz.
            let (u_x, u_z) = (room.next01(), room.next01());
            let stagger = room.next01() < 0.45;
            let omit = room.next01() * PILLAR_OMIT_MAX;
            // El lado, en pasos de una celda. En zona densa el sorteo se sesga hacia ancho.
            let u_side = room.next01();
            // Fuera de zona densa se sesga a ESTRECHO: un pilar ancho come vanos, y una nave de 20 m
            // con dos de 4 m ya no tiene retícula. Medido sin el sesgo: 1225 → 950 pilares en 27
            // regiones, que es lo contrario de lo que se pidió.
            let u_side = if dense { u_side.sqrt() } else { u_side.powi(2) };
            let side_steps = (PILLAR_SIDE_MAX_CM - PILLAR_SIDE_MIN_CM) / PILLAR_SIDE_STEP_CM;
            let side = PILLAR_SIDE_MIN_CM
                + ((u_side * (side_steps + 1) as f32) as i32).min(side_steps) * PILLAR_SIDE_STEP_CM;
            let cross = side >= PILLAR_CROSS_MIN_SIDE_CM && room.next01() < PILLAR_CROSS_CHANCE;
            // ADR-105 enm. 10 — base y capitel, sólo en el pilar cuadrado. Sorteo al final de la
            // secuencia de la sala: todo lo anterior sale donde salía.
            let trim = !cross && room.next01() < PILLAR_TRIM_CHANCE;
            // ADR-125 — la FORMA, último sorteo de la sala para que todo lo anterior salga donde
            // salía. El cilindro y el octógono son un solo macizo con la huella del cuadrado, así
            // que las exclusiones y el paso libre medidos sobre `pillar` siguen valiendo; la cruz
            // es de dos cajas y sólo tiene sentido con caja.
            let u_shape = room.next01();
            let shape = if u_shape < k.round_pillar {
                SHAPE_CYLINDER
            } else if u_shape < k.round_pillar + k.octagon_pillar {
                SHAPE_OCTAGON
            } else {
                SHAPE_BOX
            };
            let cross = cross && shape == SHAPE_BOX;
            // De centro a centro: el lado más el paso libre, que es lo que de verdad se anda.
            let gap_max = if dense {
                PILLAR_GAP_MAX_DENSE_CM
            } else {
                PILLAR_GAP_MAX_CM
            };
            let spacing_min = side + PILLAR_GAP_MIN_CM;
            let span = (gap_max - PILLAR_GAP_MIN_CM) as f32;
            let step_x = spacing_min + (u_x * span) as i32;
            let step_z = spacing_min + (u_z * span) as i32;

            // **LA SEPARACIÓN SE AJUSTA A LA SALA, no al revés.** Es como se dimensiona un vano
            // de verdad: se elige cuántos caben y se reparten a partes iguales, así que el último
            // queda a la misma distancia del muro que el primero. Dibujar un paso fijo y ver qué
            // cae dentro deja siempre una franja muerta contra una de las paredes.
            //
            // `step_x` y `step_z` sorteados son el paso DESEADO; lo que manda es el número entero
            // de vanos que más se le acerca, con el suelo de `spacing_min` para que el peaje del
            // ráster no cierre el paso entre dos pilares (ADR-105 D6).
            let usable_x = r.width_cm() - 2 * PILLAR_WALL_MARGIN_CM - side;
            let usable_z = r.depth_cm() - 2 * PILLAR_WALL_MARGIN_CM - side;
            if usable_x <= 0 || usable_z <= 0 {
                continue;
            }
            let bays = |usable: i32, want: i32| -> i32 {
                let n = ((usable as f32 / want as f32).round() as i32).max(1);
                // Y si al repartir salen vanos por debajo del mínimo, se quitan vanos.
                let mut n = n;
                while n > 1 && usable / n < spacing_min {
                    n -= 1;
                }
                n
            };
            let bays_x = bays(usable_x, step_x);
            let bays_z = bays(usable_z, step_z);
            let (count_x, count_z) = (bays_x + 1, bays_z + 1);
            if count_x < PILLAR_MIN_PER_AXIS || count_z < PILLAR_MIN_PER_AXIS {
                continue;
            }
            let step_x = usable_x / bays_x;
            let step_z = usable_z / bays_z;
            // **Y con UN vano el mínimo tampoco se negocia.** `bays` no puede bajar de uno, así que
            // una sala estrecha salía con dos pilares a la distancia que fuera: medido, 15 cm entre
            // vecinos. Si ni con un solo vano se llega al mínimo, la sala no admite retícula y se
            // queda diáfana — que es la respuesta correcta para una nave de 12 m de ancho.
            if step_x < spacing_min || step_z < spacing_min {
                continue;
            }
            let (margin_x, margin_z) = (PILLAR_WALL_MARGIN_CM, PILLAR_WALL_MARGIN_CM);

            // Los puntos por los que se entra a esta sala: enlaces del plan y puertas de junta.
            let doors: Vec<(i32, i32)> = plan
                .links
                .iter()
                .filter(|l| l.a == i || l.b == i)
                .map(|l| (l.at_x_cm, l.at_z_cm))
                .chain(
                    plan.gates
                        .iter()
                        .filter(|g| g.space == i)
                        .map(|g| (g.x_cm, g.z_cm)),
                )
                .collect();

            let clear = clear_height_cm(s);
            let mut row = 0i32;
            let mut pz = r.min_z_cm + margin_z;
            while pz + side <= r.max_z_cm - margin_z {
                // Filas impares a media separación: `OFFSET_GRID`. Con `stagger` apagado sale la
                // retícula recta, que también tiene que existir o no hay contra qué leer la otra.
                let shift = if stagger && row % 2 == 1 {
                    step_x / 2
                } else {
                    0
                };
                let mut px = r.min_x_cm + margin_x + shift;
                while px + side <= r.max_x_cm - margin_x {
                    let (mx, mz) = (px as f32 / CM_PER_M, pz as f32 / CM_PER_M);
                    let mut one = super::hash::stream_at(seed, mx, mz, SALT_PILLAR_ONE);
                    // `DENSE_SECTION` / `OPEN_SECTION`: en zona vacía o dispersa se cae el doble.
                    let thin = matches!(
                        super::density::class_at(seed, mx, mz),
                        super::density::DENSITY_EMPTY | super::density::DENSITY_SPARSE
                    );
                    let chance = if thin { omit * 2.0 } else { omit };
                    if one.next01() < chance {
                        px += step_x;
                        continue;
                    }
                    let jx = (one.next01() * 2.0 - 1.0) * PILLAR_JITTER_MAX_CM as f32;
                    let jz = (one.next01() * 2.0 - 1.0) * PILLAR_JITTER_MAX_CM as f32;
                    let x = (px + jx as i32).clamp(r.min_x_cm + side, r.max_x_cm - 2 * side);
                    let z = (pz + jz as i32).clamp(r.min_z_cm + side, r.max_z_cm - 2 * side);
                    let pillar = super::plan::PlanRect {
                        min_x_cm: x,
                        min_z_cm: z,
                        max_x_cm: x + side,
                        max_z_cm: z + side,
                    };

                    // **Y el pilar tiene que apoyar en suelo de ESTA sala, CON SU MARGEN** (ADR-120
                    // D5). La retícula se traza sobre la envolvente, así que en una L caen puntos
                    // dentro de la muesca: un macizo es inmune a los vanos, o sea que un pilar ahí es
                    // una columna permanente plantada en mitad de la sala del vecino.
                    //
                    // Y con el margen, no sólo el pilar: la muesca es pared, y `PILLAR_WALL_MARGIN_CM`
                    // existe para que un pilar no nazca pegado a una. Sin el margen se midieron dos
                    // pilares de salas distintas a 498 cm — por debajo del mínimo de separación, que
                    // es el número que impide que el peaje del ráster cierre el paso entre los dos.
                    let with_margin =
                        pillar.shrunk(-(PILLAR_WALL_MARGIN_CM - PILLAR_JITTER_MAX_CM));
                    let blocked = !s.covers_rect(&with_margin)
                        || landings.iter().any(|l| l.overlaps(&pillar))
                        || wells_here.iter().any(|w| w.overlaps(&pillar))
                        || (n > 0 && own_hole.overlaps(&pillar))
                        || holes_above.iter().any(|h| h.overlaps(&pillar))
                        || pits_here.iter().any(|p| p.overlaps(&pillar))
                        || doors.iter().any(|&(dx, dz)| {
                            (dx - (x + side / 2)).abs() < PILLAR_DOOR_CLEAR_CM
                                && (dz - (z + side / 2)).abs() < PILLAR_DOOR_CLEAR_CM
                        })
                        || taken.iter().any(|&(x0, z0, x1, z1)| {
                            let (a0, b0, a1, b1) = (
                                x as f32 / CM_PER_M,
                                z as f32 / CM_PER_M,
                                (x + side) as f32 / CM_PER_M,
                                (z + side) as f32 / CM_PER_M,
                            );
                            a0 < x1 && a1 > x0 && b0 < z1 && b1 > z0
                        });
                    if !blocked {
                        let solid =
                            |x_cm: i32, z_cm: i32, size_x_cm: i32, size_z_cm: i32| Wg3Solid {
                                x_cm,
                                z_cm,
                                size_x_cm,
                                size_z_cm,
                                bottom_y_cm: s.floor_y_cm,
                                top_y_cm: s.floor_y_cm + clear,
                                style: style_of(s.role),
                                yaw_deg: 0,
                                shape,
                            };
                        if cross {
                            // Dos cajas concéntricas. El brazo es la mitad del lado redondeada a
                            // celda, y las dos comparten centro con el cuadrado que sustituyen, así
                            // que toda exclusión medida sobre `pillar` sigue valiendo.
                            let arm = (side / 2) / PILLAR_SIDE_STEP_CM * PILLAR_SIDE_STEP_CM;
                            let inset = (side - arm) / 2;
                            out.push(solid(x, z + inset, side, arm));
                            out.push(solid(x + inset, z, arm, side));
                        } else {
                            out.push(solid(x, z, side, side));
                            if trim {
                                // Zapata y capitel: la misma caja del pilar crecida
                                // `PILLAR_TRIM_OUT_CM` por cada lado, de `PILLAR_TRIM_H_CM` de alto,
                                // una en el suelo y otra bajo el techo. Rompen el prisma sin cambiar
                                // lo que se anda: el paso entre pilares baja 60 cm y sigue por
                                // encima del mínimo (500 − 60).
                                let o = PILLAR_TRIM_OUT_CM;
                                let big = side + 2 * o;
                                out.push(Wg3Solid {
                                    x_cm: x - o,
                                    z_cm: z - o,
                                    size_x_cm: big,
                                    size_z_cm: big,
                                    bottom_y_cm: s.floor_y_cm,
                                    top_y_cm: s.floor_y_cm + PILLAR_TRIM_H_CM,
                                    style: style_of(s.role),
                                    yaw_deg: 0,
                                    // Zapata y capitel con la forma del fuste: un disco mayor.
                                    shape,
                                });
                                out.push(Wg3Solid {
                                    x_cm: x - o,
                                    z_cm: z - o,
                                    size_x_cm: big,
                                    size_z_cm: big,
                                    bottom_y_cm: s.floor_y_cm + clear - PILLAR_TRIM_H_CM,
                                    top_y_cm: s.floor_y_cm + clear,
                                    style: style_of(s.role),
                                    yaw_deg: 0,
                                    shape,
                                });
                            }
                        }
                    }
                    px += step_x;
                }
                pz += step_z;
                row += 1;
            }
        }
    }
    out
}

/// Superficie mínima de un espacio para que admita divisiones, en m².
///
/// Noventa metros es una sala de 9 × 10: la más pequeña en la que un tabique DIVIDE en vez de
/// estrechar. Por debajo, lo que sale a los dos lados es un pasillo, y un pasillo dentro de una sala
/// no es arquitectura, es un estorbo.
const PARTITION_MIN_AREA_M2: f32 = 90.0;

/// Longitud mínima del vano que cruza una división, en centímetros.
///
/// **Y bajarlo NO sube la dosis, que es lo contrario de lo que parecía.** Se probó a 650 junto con un
/// margen de pared de 250 para dejar entrar los espacios alargados: la medida dio 52,2 divisiones por
/// región contra las 61,2 de 800/300, porque lo que entra por abajo son vanos que luego no llegan a
/// `PARTITION_MIN_LEN_CM` y lo que se pierde son colocaciones que sí valían. Queda escrito para que
/// nadie vuelva a gastar la vuelta.
const PARTITION_MIN_SPAN_CM: i32 = 800;

/// Grosor de un tabique, en centímetros.
///
/// **Treinta, no quince.** La pared de un tramo mide 15 cm y es la cáscara de una sala; un tabique es
/// obra suelta y tiene que leerse como tal desde los dos lados. Y el ráster cobra lo mismo por quince
/// que por treinta —maciza toda celda que TOQUE, ADR-105 D6—, así que los quince de más salen gratis
/// en colisión y no salen gratis en la vista.
const PARTITION_T_CM: i32 = 30;

/// Hueco de paso de una división que cruza el espacio entero, en centímetros.
///
/// **Cuatro metros, y el número está medido, no elegido.** Simulando divisiones sobre el ráster
/// servido de cuatro regiones: con hueco de 1,5 m salían hasta 2 islas y la mancha mayor bajaba al
/// 98,0 %; con 4 m, mancha mayor 100 % e islas cero en las cuatro. El paso ancho es además lo que
/// separa «una sala partida» de «dos salas», que es la lectura que se busca.
const PARTITION_GAP_CM: i32 = 400;

/// Distancia mínima entre una división y una puerta, en centímetros.
///
/// Es el mismo criterio que `PILLAR_DOOR_CLEAR_CM` y por el mismo motivo: un tabique plantado delante
/// de un vano lo tapia sin que ningún contador se entere, y el síntoma aparece cien metros más allá
/// como una sala a la que no se llega.
const PARTITION_DOOR_CLEAR_CM: i32 = 350;

/// Distancia mínima entre una división y la pared con la que va paralela, en centímetros.
///
/// Es lo que queda de sala al otro lado: por debajo de tres metros no es una división, es un pasillo
/// pegado al muro que nadie pidió. Ver la nota de [`PARTITION_MIN_SPAN_CM`] sobre por qué bajarlo
/// tampoco sube la dosis.
const PARTITION_WALL_MARGIN_CM: i32 = 300;

/// Probabilidad de que un espacio que cumple los requisitos lleve divisiones.
const PARTITION_ROOM_CHANCE: f32 = 0.80;

/// Reparto de los tres tipos de división: ISLA, ESPOLÓN y PARTICIÓN, acumulado.
///
/// **Y mandan los dos que no parten la sala, a propósito.** Un tabique que cruza de lado a lado con
/// su hueco convierte una sala en dos salas, y de eso el mundo ya va lleno: es literalmente lo que
/// hace el BSP, y repetirlo dentro de la hoja no añade una lectura nueva. La isla —una pared exenta,
/// despegada de las cuatro paredes— y el espolón —pegado a una sola— rompen la línea de visión SIN
/// partir el sitio: la sala grande sigue siendo una sala grande, que es lo que se pidió no perder.
///
/// # Y la isla existe porque el primer intento se quedó al 40 % de la dosis
///
/// Con sólo espolón y partición, toda división llegaba a una pared perpendicular, o sea a 0 cm de
/// cualquier puerta de esa pared: la esquiva de puertas rechazaba dos de cada tres intentos y la
/// medida lo cazó —480 metros lineales por región contra los 1200 que la simulación pedía. Una isla
/// no toca ninguna pared, así que la esquiva casi nunca la ve.
const PARTITION_ISLAND_BELOW: f32 = 0.45;
const PARTITION_SPUR_BELOW: f32 = 0.80;

/// Fracción del vano que recorre un espolón, mínimo y máximo.
const PARTITION_SPUR_SPAN: (f32, f32) = (0.45, 0.78);

/// Fracción del vano que recorre una isla, mínimo y máximo.
const PARTITION_ISLAND_SPAN: (f32, f32) = (0.35, 0.62);

/// Cuánto deja libre una isla en cada extremo, en centímetros.
///
/// **Dos metros y medio, no cuatro.** El hueco de una partición es un PASO —lo que separa dos mitades
/// de sala— y por eso se midió a cuatro metros contra las islas del ráster. Los extremos de una isla
/// no son un paso: son el rodeo alrededor de una pared exenta que ya se puede bordear por los dos
/// lados, y pedirles cuatro metros dejaba la isla fuera de toda sala por debajo de 21 m de vano.
const PARTITION_ISLAND_CLEAR_CM: i32 = 250;

/// Longitud mínima de una división, en centímetros.
const PARTITION_MIN_LEN_CM: i32 = 350;

/// Longitud mínima de un TROZO de división, en centímetros.
///
/// Lo que separa un tabique corto de un poste. El hueco de una partición se sortea a lo largo del
/// vano y puede caer a un palmo de un extremo; el trozo que queda se tira y el paso se ensancha.
const PARTITION_STUB_MIN_CM: i32 = 120;

/// Probabilidad de que una división se quede por debajo del techo.
const PARTITION_SCREEN_CHANCE: f32 = 0.34;

/// Altura de una división que no llega al techo, en centímetros.
///
/// **Tapa la vista y no el volumen.** Los ojos van a 1,60 m, así que 2,30 corta la línea de visión
/// entera igual que un muro; y dejar el techo corrido por encima es lo que la lee como mampara de
/// oficina en vez de como muro de carga. Es la única división que se puede mirar por encima, y por
/// eso no puede ser la única que hay.
const PARTITION_SCREEN_H_CM: i32 = 230;

/// ADR-105 enm. 8 — **el MEDIO MURO desde el suelo**: 110 cm, a la altura de la cadera. Se ve por
/// encima y no se pasa, como el pretil de un atrio pero dentro de una sala. Es la mitad baja de lo
/// que Joel pidió («medios muros de alturas tanto de suelo como techo»).
pub(super) const PARTITION_LOW_H_CM: i32 = 110;
/// ADR-105 enm. 16 — el REMATE del medio muro bajo: una tabla de madera encima, como en la foto
/// del Nivel 0. Alto de la tabla. Cuatro y no más: el cliente talla perfil a toda decoración de
/// más de 4,5 cm de canto (`Wg3MeshBuilder.AddCasingBox`), y una tabla es lisa.
pub(super) const LOW_WALL_RAIL_H_CM: i32 = 4;
/// ADR-105 enm. 16 — cuánto vuela la tabla sobre cada cara del medio muro (y sobre los extremos).
pub(super) const LOW_WALL_RAIL_OVERHANG_CM: i32 = 3;
/// Y el que CUELGA del techo hasta dos metros: se pasa por debajo y corta la vista al fondo. Dos
/// metros y no 1,80 porque el hueco libre tiene que quedar por encima del cuerpo con holgura, o el
/// ráster conservador cierra el paso por un centímetro.
pub(super) const PARTITION_HANG_CLEAR_CM: i32 = 200;
/// ADR-105 enm. 16 — el umbral del medio muro bajo vive en `Knobs::low_below`.
const PARTITION_HANG_BELOW: f32 = 0.62;

/// ADR-105 enm. 9 — **el LABERINTO**: peine de espolones alternos desde paredes opuestas, con un
/// paso al final de cada uno. El camino es una S garantizada: un espolón arranca de una pared y
/// muere en el aire, así que ningún número de ellos desconecta nada (enm. 3 D3), y alternarlos es
/// lo que obliga a recorrer la sala entera para cruzarla.
const MAZE_CHANCE: f32 = 0.12;
/// Y el CUARTO EXENTO: cuatro tabiques con una boca, flotando en mitad de la sala. El «cuarto sin
/// motivo» de Level 0.
const CELL_CHANCE: f32 = 0.15;
/// Superficie mínima para un laberinto y para un cuarto exento, en m².
const MAZE_MIN_AREA_M2: f32 = 200.0;
const CELL_MIN_AREA_M2: f32 = 150.0;
/// Paso libre al final de cada espolón del peine. Más que el hueco de una partición (400) no hace
/// falta: se pasa de uno en uno, y menos de 300 lo cierra el peaje del ráster.
const MAZE_GAP_CM: i32 = 400;
/// Paso entre espolones del peine, mínimo y máximo, sorteado por sala.
const MAZE_PITCH_CM: (i32, i32) = (400, 550);
/// Lado interior del cuarto exento, mínimo y máximo, en pasos de celda.
const CELL_SIDE_CM: (i32, i32) = (300, 450);
/// Ancho de la boca del cuarto exento: el de una puerta del plan (`DOORWAY_CM`). Con 200 el barrido
/// subió las islas de 1,3 a 1,5 por región: los dos muñones macizan su celda y el peaje del ráster
/// se come otra a cada lado, y una boca de 200 mal alineada con la rejilla se queda en 50.
const CELL_DOOR_CM: i32 = 250;
/// Paso libre alrededor del cuarto exento, como el de una isla.
const CELL_CLEAR_CM: i32 = 250;
/// Sal del sorteo de laberinto o cuarto exento de una SALA. Propia y no la de las divisiones: así
/// la secuencia de la enm. 3 no se mueve.
const SALT_MAZE_ROOM: u32 = 0xB1_11_A0_07;

/// ADR-105 enm. 15 — **EL LABERINTO DE REJILLA: el de verdad.**
///
/// El peine (enm. 9) es una S. Esto es un laberinto: rejilla de celdas de `GRID_MAZE_CELL_CM`
/// dentro de la sala, un árbol de expansión sorteado por posición (búsqueda en profundidad con
/// retroceso) y un tabique en cada arista que el árbol no abre. **La conectividad va por
/// construcción**: un árbol de expansión une todas las celdas y no tiene ciclos, así que dentro
/// no hay islas posibles y sí callejones por todas partes, que es Level 0. Alrededor queda un anillo
/// de `GRID_MAZE_RING_CM` donde caen las puertas de la sala, y el bloque se abre al anillo por
/// `GRID_MAZE_ENTRANCES` celdas de borde.
const GRID_MAZE_CELL_CM: i32 = 250;
/// Un metro de anillo: las puertas de la sala caen ahí, y los tabiques a menos de 350 de una puerta
/// se descartan uno a uno (`blocked_foot`), así que el anillo no necesita ser el paso de puerta.
/// Con 150 y 100 m² de mínimo, el 47 % de las salas de zona laberinto se quedaban vacías: son
/// pequeñas. Con 100 y 55 m² cabe una rejilla de 2 × 2 en una sala de 7,3 × 7,3.
const GRID_MAZE_RING_CM: i32 = 100;
const GRID_MAZE_MIN_AREA_M2: f32 = 55.0;
const GRID_MAZE_ENTRANCES: usize = 2;
const SALT_GRID_MAZE: u32 = 0xB1_11_A0_0E;

/// Un tabique de laberinto de rejilla sobre una arista, con medio grosor de solape a cada extremo
/// para cerrar la esquina con el siguiente. `(x, z)` es el punto medio de la arista.
#[allow(clippy::too_many_arguments)]
fn grid_maze_wall(
    out: &mut Vec<Wg3Solid>,
    along_x: bool,
    x: i32,
    z: i32,
    bottom: i32,
    top: i32,
    style: u8,
    blocked: &dyn Fn(&super::plan::PlanRect) -> bool,
) {
    let half = GRID_MAZE_CELL_CM / 2 + PARTITION_T_CM / 2;
    let t = PARTITION_T_CM / 2;
    let foot = if along_x {
        super::plan::PlanRect {
            min_x_cm: x - half,
            min_z_cm: z - t,
            max_x_cm: x + half,
            max_z_cm: z + t,
        }
    } else {
        super::plan::PlanRect {
            min_x_cm: x - t,
            min_z_cm: z - half,
            max_x_cm: x + t,
            max_z_cm: z + half,
        }
    };
    if blocked(&foot) {
        return;
    }
    out.push(Wg3Solid {
        x_cm: foot.min_x_cm,
        z_cm: foot.min_z_cm,
        size_x_cm: foot.width_cm(),
        size_z_cm: foot.depth_cm(),
        bottom_y_cm: bottom,
        top_y_cm: top,
        style,
        yaw_deg: 0,
        shape: SHAPE_BOX,
    });
}

/// Un tramo de tabique troceado al tope de macizo, a partes iguales, con muñones fuera (ver el
/// emisor de divisiones: un trozo de 30 × 30 es un poste, no un muro).
#[allow(clippy::too_many_arguments)]
fn push_partition_run(
    out: &mut Vec<Wg3Solid>,
    across_x: bool,
    at: i32,
    a: i32,
    b: i32,
    bottom: i32,
    top: i32,
    style: u8,
) {
    let len = b - a;
    if len < PARTITION_STUB_MIN_CM {
        return;
    }
    let n = (len + MAX_SOLID_CM - 1) / MAX_SOLID_CM;
    let mut cut = a;
    for k in 1..=n {
        let end = a + (len * k) / n;
        let (x, z, sx, sz) = if across_x {
            (at, cut, PARTITION_T_CM, end - cut)
        } else {
            (cut, at, end - cut, PARTITION_T_CM)
        };
        out.push(Wg3Solid {
            x_cm: x,
            z_cm: z,
            size_x_cm: sx,
            size_z_cm: sz,
            bottom_y_cm: bottom,
            top_y_cm: top,
            style,
            yaw_deg: 0,
            shape: SHAPE_BOX,
        });
        cut = end;
    }
}

/// Cuántas divisiones como mucho por espacio.
const PARTITION_MAX_PER_SPACE: i32 = 3;

/// Metros cuadrados de espacio por división.
///
/// Sale de la dosis medida sobre el ráster servido: unos 12-14 metros lineales de división por
/// espacio construido. Con el espacio medio en 170 m², eso es una división en la sala normal y tres
/// en la nave.
const PARTITION_AREA_PER_ONE_M2: f32 = 170.0;

const SALT_PARTITION_ROOM: u32 = 0xB1_11_A0_02;

/// Una división a lo largo de su vano: `(desde, hasta, hueco)`, en centímetros de mundo. El hueco es
/// `None` en una isla y en un espolón, que no lo necesitan.
type PartitionRun = (i32, i32, Option<(i32, i32)>);

/// ADR-105 enmienda 3 — **la masa interior: el tabique, el espolón y la mampara.**
///
/// # El agujero que tapa, dicho con el número que lo mide
///
/// Hasta aquí había exactamente DOS emisores de [`Wg3Solid`] en todo el relleno: el pretil de un
/// balcón y la retícula de pilares de una nave. Los dos son de atrio o de nave, así que **el 92,5 %
/// de los espacios del mundo no contenía ni un solo volumen**: seis caras y aire. La medida que lo
/// dice sin discutirlo es la isovista —cuánto suelo se ve de golpe—: **1883 m² de mediana contra un
/// espacio medio de 170 m²**. De pie en cualquier sitio se veían diez salas a la vez, y ninguna
/// invariante del validador miraba eso, que es por qué 270/270 regiones válidas convivían con la
/// queja.
///
/// # Por qué esto no es «más densidad»
///
/// Un tabique cambia la TOPOLOGÍA de lo que se ve, no la cantidad de cosas que hay. Se midió contra
/// la alternativa: sembrar paredes al azar y sembrarlas en peine desfasado convergen en la misma
/// isovista (~260 m²) al mismo presupuesto de metros. Lo que mueve el número es que haya masa, no de
/// qué forma esté puesta — así que aquí no hay ocho arquetipos, hay dos, y el resto es la posición.
///
/// # La conectividad no se confía, se construye
///
/// Un espolón no puede desconectar nada: arranca de una pared y muere en el aire. Una partición sí
/// podría, y por eso **hay como mucho UNA por espacio** y su hueco mide cuatro metros. Con eso la
/// simulación sobre el ráster servido de cuatro regiones da mancha mayor 100 % e islas cero; con
/// hueco de metro y medio daba 98,0 % y dos islas.
/// Los puntos de boca de los tramos emitidos, con la cota de su suelo: `(x, z, suelo)`. Sólo las
/// bocas de VERDAD —las que no ocupan el lado entero, que son las juntas interiores entre tramos
/// hermanos (`full_side`)—. Es la lista completa de por dónde se entra a un espacio: puertas del
/// plan, de junta, bocas de ruta y puertas rescatadas, que `plan.links` no sabe dar.
fn segment_door_points(segments: &[Wg3Segment]) -> Vec<(i32, i32, i32)> {
    let mut out = Vec::new();
    for seg in segments {
        let (x0, z0) = (seg.x_cm, seg.z_cm);
        let (x1, z1) = (x0 + seg.size_x_cm, z0 + seg.size_z_cm);
        for o in &seg.openings {
            let side_len = if o.side.is_multiple_of(2) {
                seg.size_x_cm
            } else {
                seg.size_z_cm
            };
            if o.width_cm >= side_len - 1 {
                continue;
            }
            let (x, z) = match o.side % 4 {
                0 => (x0 + o.offset_cm, z1),
                1 => (x1, z1 - o.offset_cm),
                2 => (x1 - o.offset_cm, z0),
                _ => (x0, z0 + o.offset_cm),
            };
            out.push((x, z, seg.floor_y_cm));
        }
    }
    out
}

/// ¿Pisa esta huella (con 30 cm de holgura) algún vano de la planta del espacio? Un macizo es inmune
/// a los vanos, así que el que nace encima de una ventana, una rendija o una hornacina la tapa sin
/// que ningún contador lo vea. Lo preguntan divisiones, laberintos, cuartos y pilastras.
fn on_wall_carve(foot: &super::plan::PlanRect, s: &PlannedSpace, carves: &[Wg3Carve]) -> bool {
    let grown = foot.shrunk(-30);
    carves.iter().any(|c| {
        c.bottom_y_cm >= s.floor_y_cm
            && c.bottom_y_cm < s.floor_y_cm + STOREY_HEIGHT_CM
            && grown.min_x_cm < c.x_cm + c.size_x_cm
            && grown.max_x_cm > c.x_cm
            && grown.min_z_cm < c.z_cm + c.size_z_cm
            && grown.max_z_cm > c.z_cm
    })
}

/// ¿Cae este punto sobre la pared exterior de este espacio? Dos centímetros de tolerancia, como
/// `wall_side_of`. Una boca de tramo en la pared de un espacio de OTRA planta se descarta por la cota
/// antes de llegar aquí.
fn on_space_wall(s: &PlannedSpace, x: i32, z: i32) -> bool {
    const EPS: i32 = 2;
    s.parts().iter().any(|p| {
        let in_x = x >= p.min_x_cm - EPS && x <= p.max_x_cm + EPS;
        let in_z = z >= p.min_z_cm - EPS && z <= p.max_z_cm + EPS;
        (in_x && ((p.min_z_cm - z).abs() <= EPS || (p.max_z_cm - z).abs() <= EPS))
            || (in_z && ((p.min_x_cm - x).abs() <= EPS || (p.max_x_cm - x).abs() <= EPS))
    })
}

#[allow(clippy::too_many_arguments)]
fn interior_partitions(
    building: &RegionBuilding,
    manifest: &Wg3Manifest,
    placements: &[Wg3Placement],
    pillars: &[Wg3Solid],
    seg_doors: &[(i32, i32, i32)],
    carves: &[Wg3Carve],
    segments: &[Wg3Segment],
) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    let seed = building.seed;

    // Huellas ya resueltas por una pieza del catálogo: la pieza trae su interior horneado y un
    // tabique dentro es obra en mitad de un salón que nadie dibujó.
    let taken: Vec<(f32, f32, f32, f32)> = placements
        .iter()
        .filter_map(|p| {
            manifest
                .pieces
                .get(p.piece as usize)
                .map(|piece| p.bounds(piece))
        })
        .collect();

    for (n, plan) in building.storeys.iter().enumerate() {
        let landings: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below + 1 == n)
            .map(|w| w.rect.shrunk(-50))
            .collect();
        let wells_here: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below == n)
            .map(|w| w.rect.shrunk(-50))
            .collect();
        // Y bajo los agujeros que la planta de arriba pueda abrir (ADR-105 enm. 4 D5): una división
        // de 230 cm bajo un forjado perforado deja la caída en 20 cm. Se encontró al cambiar los
        // pilares: la exclusión de éstos movió una división de la semilla servida justo debajo del
        // agujero de (-1,2), y `a_hole_drops_you_a_whole_storey` lo cazó.
        let holes_above = hole_squares_above(building, n);
        // ADR-126 D5 — y la rejilla de pozos de la planta baja, por lo mismo.
        let pits_here = if n == 0 {
            pit_rects_of(building, segments)
        } else {
            Vec::new()
        };

        // **El espacio de LLEGADA de una escalera se deja entero en paz.**
        //
        // Costó dos tests rojos y merece estar escrito: el hueco de escalera es un AGUJERO en el
        // forjado de la planta de llegada, así que se comporta como una pared. Una división puesta
        // justo pasado el rellano —a 51 cm del hueco, que la exclusión de `landings` ya daba por
        // buena— encierra el rellano entre el agujero y ella, y como ésa es la única forma de subir,
        // la planta entera deja de alcanzarse: `planta 2: sólo 0 de 18 849 cotas pisables se alcanzan
        // desde la mancha mayor`. Un margen mayor no lo arregla —depende de dónde caiga la puerta del
        // espacio—, así que se renuncia al espacio completo. Son unos seis por región.
        let arrivals: Vec<usize> = building
            .wells
            .iter()
            .filter(|w| w.storey_below + 1 == n)
            .map(|w| w.space_above)
            .collect();
        // **Y el espacio que abre una PUERTA DE JUNTA tampoco lleva divisiones**, por el mismo motivo
        // y con la misma medida detrás. El borde de región es una pared dura y la puerta es su único
        // hueco; el validador comprueba la celda a 75 cm por dentro y la exige en la mancha mayor
        // (`validate.rs:889`). Esquivar la puerta 350 cm no bastó: quedaron dos puertas selladas de 27
        // regiones —(0,0,−131,2) y (179,1,150,0)—, porque lo que encierra la celda no es la división
        // sola sino la división MÁS el borde. Son cuatro a seis espacios por región.
        let gated: Vec<usize> = plan.gates.iter().map(|g| g.space).collect();

        for (i, s) in plan.built() {
            // **Nunca en circulación.** Un tabique en la espina parte el edificio en dos, y eso no es
            // una división: es el fallo de conectividad de siempre con otro nombre. Es la misma regla
            // que `plan::assign_void` escribe para el vacío.
            if s.role.is_circulation() || s.rise_cm != 0 || s.role == SpaceRole::Stair {
                continue;
            }
            if arrivals.contains(&i) || gated.contains(&i) {
                continue;
            }
            let r = s.rect;
            if s.area_m2() < PARTITION_MIN_AREA_M2 {
                continue;
            }

            let (cx, cz) = r.centre_m();
            let kn = knobs_of(seed, s);
            let mut room = super::hash::stream_at(seed, cx, cz, SALT_PARTITION_ROOM);
            if room.next01() >= kn.partition_room {
                continue;
            }
            // **Y una huella compuesta admite una más, porque tiene una sala más.**
            //
            // El cupo se reparte una por parte (ver `host`), así que con el tope en tres una L de
            // cuatro partes dejaba siempre una sin estructura. Y el tope existe por lo de siempre:
            // dos divisiones que se cruzan dejan cuadrantes.
            let cap = if s.is_composite() {
                super::plan::MAX_PARTS as i32
            } else {
                PARTITION_MAX_PER_SPACE
            };
            let want = ((s.area_m2() / PARTITION_AREA_PER_ONE_M2).round() as i32).clamp(1, cap);

            // Los puntos por los que se entra: enlaces del plan y puertas de junta, igual que en la
            // retícula de pilares — **y las bocas de los tramos emitidos** (enm. 9), que son las
            // únicas que saben dónde cae de verdad la boca de una ruta o una puerta rescatada.
            let doors: Vec<(i32, i32)> = plan
                .links
                .iter()
                .filter(|l| l.a == i || l.b == i)
                .map(|l| (l.at_x_cm, l.at_z_cm))
                .chain(
                    plan.gates
                        .iter()
                        .filter(|g| g.space == i)
                        .map(|g| (g.x_cm, g.z_cm)),
                )
                .chain(
                    seg_doors
                        .iter()
                        .filter(|&&(x, z, floor)| floor == s.floor_y_cm && on_space_wall(s, x, z))
                        .map(|&(x, z, _)| (x, z)),
                )
                .collect();

            let clear = clear_height_cm(s);
            let style = style_of(s.role);
            // Lo que ya ha puesto ESTE espacio: dos divisiones que se cruzan dejan cuadrantes, y un
            // cuadrante con un solo hueco es la isla que el validador caza.
            let mut mine: Vec<super::plan::PlanRect> = Vec::new();
            let mut split_used = false;

            // **ADR-105 enm. 9 — laberinto o cuarto exento, y entonces nada más en esta sala.** Con
            // sal propia: la secuencia de sorteos de las divisiones de siempre no se mueve.
            //
            // Sólo sobre huella de UNA parte: el peine se traza de pared a pared y en una L la
            // envolvente ofrece paredes que en el rincón no existen (ADR-120 D5).
            if !s.is_composite() {
                let overlaps_m =
                    |foot: &super::plan::PlanRect, (x0, z0, x1, z1): (f32, f32, f32, f32)| {
                        let (a0, b0, a1, b1) = (
                            foot.min_x_cm as f32 / CM_PER_M,
                            foot.min_z_cm as f32 / CM_PER_M,
                            foot.max_x_cm as f32 / CM_PER_M,
                            foot.max_z_cm as f32 / CM_PER_M,
                        );
                        a0 < x1 && a1 > x0 && b0 < z1 && b1 > z0
                    };
                // Las mismas exclusiones que una división: fuera de suelo propio, rellanos, agujeros
                // (propio y de arriba), pozos, pilares, puertas, piezas y vanos de pared.
                let hole_here = hole_square(&r).shrunk(-50);
                let blocked_foot = |foot: &super::plan::PlanRect| -> bool {
                    !s.covers_rect(foot)
                        || on_wall_carve(foot, s, carves)
                        || landings.iter().any(|l| l.overlaps(foot))
                        || (n > 0 && hole_here.overlaps(foot))
                        || holes_above.iter().any(|h| h.overlaps(foot))
                        || pits_here.iter().any(|p| p.overlaps(foot))
                        || wells_here.iter().any(|w| w.overlaps(foot))
                        || pillars.iter().any(|p| overlaps_m(foot, p.bounds()))
                        || doors.iter().any(|&(dx, dz)| {
                            foot.shrunk(-PARTITION_DOOR_CLEAR_CM).contains_point(dx, dz)
                        })
                        || taken.iter().any(|&t| overlaps_m(foot, t))
                };
                let mut maze = super::hash::stream_at(seed, cx, cz, SALT_MAZE_ROOM);
                let u = maze.next01();
                let wide = r.width_cm() >= r.depth_cm();
                // **Nunca un laberinto en una sala con pilares.** El pasillo entre dos espolones
                // mide 3,7–5,2 m y un pilar de 2–4 m plantado en medio lo sella por los dos lados:
                // el espolón esquivaba el pilar, el pasillo no. Medido: +5 islas en 27 regiones, y
                // cada una del tamaño exacto de un pasillo del peine (279–305 cotas).
                let has_pillars = pillars.iter().any(|p| overlaps_m(&r, p.bounds()));

                // ADR-105 enm. 15 — el laberinto de REJILLA, con dado propio (`SALT_GRID_MAZE`) para
                // no mover la secuencia del peine y el cuarto.
                let mut gm = super::hash::stream_at(seed, cx, cz, SALT_GRID_MAZE);
                if gm.next01() < kn.grid_maze
                    && s.area_m2() >= GRID_MAZE_MIN_AREA_M2
                    && !has_pillars
                {
                    let inner = r.shrunk(WALL_T_CM + GRID_MAZE_RING_CM);
                    let cell = GRID_MAZE_CELL_CM;
                    let (nx, nz) = (
                        (inner.width_cm() / cell).max(0) as usize,
                        (inner.depth_cm() / cell).max(0) as usize,
                    );
                    if nx >= 2 && nz >= 2 {
                        let gx0 = inner.min_x_cm + (inner.width_cm() - nx as i32 * cell) / 2;
                        let gz0 = inner.min_z_cm + (inner.depth_cm() - nz as i32 * cell) / 2;
                        // Árbol de expansión: `open_e[c]` abre la arista este de la celda `c`,
                        // `open_n[c]` la norte.
                        let count = nx * nz;
                        let mut visited = vec![false; count];
                        let mut open_e = vec![false; count];
                        let mut open_n = vec![false; count];
                        let start = (gm.next01() * count as f32) as usize % count;
                        let mut stack = vec![start];
                        visited[start] = true;
                        while let Some(&c) = stack.last() {
                            let (ix, iz) = (c % nx, c / nx);
                            let mut cands: Vec<(usize, u8)> = Vec::with_capacity(4);
                            if ix + 1 < nx && !visited[c + 1] {
                                cands.push((c + 1, 0));
                            }
                            if ix > 0 && !visited[c - 1] {
                                cands.push((c - 1, 1));
                            }
                            if iz + 1 < nz && !visited[c + nx] {
                                cands.push((c + nx, 2));
                            }
                            if iz > 0 && !visited[c - nx] {
                                cands.push((c - nx, 3));
                            }
                            if cands.is_empty() {
                                stack.pop();
                                continue;
                            }
                            let (next, dir) =
                                cands[(gm.next01() * cands.len() as f32) as usize % cands.len()];
                            match dir {
                                0 => open_e[c] = true,
                                1 => open_e[next] = true,
                                2 => open_n[c] = true,
                                _ => open_n[next] = true,
                            }
                            visited[next] = true;
                            stack.push(next);
                        }
                        // Entradas: celdas de borde cuya arista exterior queda abierta.
                        let perimeter = 2 * (nx + nz);
                        let mut entrances: Vec<usize> = Vec::new();
                        while entrances.len() < GRID_MAZE_ENTRANCES.min(perimeter) {
                            let e = (gm.next01() * perimeter as f32) as usize % perimeter;
                            if !entrances.contains(&e) {
                                entrances.push(e);
                            }
                        }
                        let top = if gm.next01() < PARTITION_SCREEN_CHANCE {
                            s.floor_y_cm + PARTITION_SCREEN_H_CM.min(clear)
                        } else {
                            s.floor_y_cm + clear
                        };
                        let blocked = |foot: &super::plan::PlanRect| -> bool { blocked_foot(foot) };
                        // Aristas interiores cerradas.
                        for iz in 0..nz {
                            for ix in 0..nx {
                                let c = iz * nx + ix;
                                let (x0, z0) = (gx0 + ix as i32 * cell, gz0 + iz as i32 * cell);
                                if ix + 1 < nx && !open_e[c] {
                                    grid_maze_wall(
                                        &mut out,
                                        false,
                                        x0 + cell,
                                        z0 + cell / 2,
                                        s.floor_y_cm,
                                        top,
                                        style,
                                        &blocked,
                                    );
                                }
                                if iz + 1 < nz && !open_n[c] {
                                    grid_maze_wall(
                                        &mut out,
                                        true,
                                        x0 + cell / 2,
                                        z0 + cell,
                                        s.floor_y_cm,
                                        top,
                                        style,
                                        &blocked,
                                    );
                                }
                            }
                        }
                        // Borde exterior, menos las entradas. Índice de perímetro: sur (0..nx),
                        // norte (nx..2nx), oeste (2nx..2nx+nz), este (resto).
                        for ix in 0..nx {
                            let x = gx0 + ix as i32 * cell + cell / 2;
                            if !entrances.contains(&ix) {
                                grid_maze_wall(
                                    &mut out,
                                    true,
                                    x,
                                    gz0,
                                    s.floor_y_cm,
                                    top,
                                    style,
                                    &blocked,
                                );
                            }
                            if !entrances.contains(&(nx + ix)) {
                                grid_maze_wall(
                                    &mut out,
                                    true,
                                    x,
                                    gz0 + nz as i32 * cell,
                                    s.floor_y_cm,
                                    top,
                                    style,
                                    &blocked,
                                );
                            }
                        }
                        for iz in 0..nz {
                            let z = gz0 + iz as i32 * cell + cell / 2;
                            if !entrances.contains(&(2 * nx + iz)) {
                                grid_maze_wall(
                                    &mut out,
                                    false,
                                    gx0,
                                    z,
                                    s.floor_y_cm,
                                    top,
                                    style,
                                    &blocked,
                                );
                            }
                            if !entrances.contains(&(2 * nx + nz + iz)) {
                                grid_maze_wall(
                                    &mut out,
                                    false,
                                    gx0 + nx as i32 * cell,
                                    z,
                                    s.floor_y_cm,
                                    top,
                                    style,
                                    &blocked,
                                );
                            }
                        }
                        continue;
                    }
                }

                if u < kn.maze && s.area_m2() >= MAZE_MIN_AREA_M2 && !has_pillars {
                    // Espolones perpendiculares al eje LARGO, alternando la pared de arranque.
                    let (long, short) = if wide {
                        (r.width_cm(), r.depth_cm())
                    } else {
                        (r.depth_cm(), r.width_cm())
                    };
                    let pitch = MAZE_PITCH_CM.0
                        + (maze.next01() * (MAZE_PITCH_CM.1 - MAZE_PITCH_CM.0) as f32) as i32;
                    let len = short - MAZE_GAP_CM;
                    let usable = long - 2 * PARTITION_WALL_MARGIN_CM - PARTITION_T_CM;
                    let count = usable / pitch;
                    if len >= PARTITION_MIN_LEN_CM && count >= 2 {
                        let step = usable / count;
                        let top = if maze.next01() < PARTITION_SCREEN_CHANCE {
                            s.floor_y_cm + PARTITION_SCREEN_H_CM.min(clear)
                        } else {
                            s.floor_y_cm + clear
                        };
                        let mut placed = 0;
                        for k in 0..=count {
                            // `across_x`: el tabique corre a lo largo de Z con `at` en X.
                            let across_x = wide;
                            let at_base = if wide { r.min_x_cm } else { r.min_z_cm };
                            let at = at_base + PARTITION_WALL_MARGIN_CM + k * step;
                            if at + PARTITION_T_CM
                                > (if wide { r.max_x_cm } else { r.max_z_cm })
                                    - PARTITION_WALL_MARGIN_CM
                            {
                                break;
                            }
                            let from_base = if wide { r.min_z_cm } else { r.min_x_cm };
                            let (a, b) = if k % 2 == 0 {
                                (from_base, from_base + len)
                            } else {
                                (from_base + short - len, from_base + short)
                            };
                            let foot = if across_x {
                                super::plan::PlanRect {
                                    min_x_cm: at,
                                    min_z_cm: a,
                                    max_x_cm: at + PARTITION_T_CM,
                                    max_z_cm: b,
                                }
                            } else {
                                super::plan::PlanRect {
                                    min_x_cm: a,
                                    min_z_cm: at,
                                    max_x_cm: b,
                                    max_z_cm: at + PARTITION_T_CM,
                                }
                            };
                            // **Y el HUECO al final del espolón tiene que estar libre**, no sólo el
                            // espolón. Un pilar plantado en esos 300 cm cierra el paso y deja una
                            // bolsa: medido, +4 islas en 27 regiones (305 cotas en una sola).
                            let (g0, g1) = if k % 2 == 0 {
                                (from_base + len, from_base + short)
                            } else {
                                (from_base, from_base + short - len)
                            };
                            let gap = if across_x {
                                super::plan::PlanRect {
                                    min_x_cm: at - 100,
                                    min_z_cm: g0,
                                    max_x_cm: at + PARTITION_T_CM + 100,
                                    max_z_cm: g1,
                                }
                            } else {
                                super::plan::PlanRect {
                                    min_x_cm: g0,
                                    min_z_cm: at - 100,
                                    max_x_cm: g1,
                                    max_z_cm: at + PARTITION_T_CM + 100,
                                }
                            };
                            let gap_blocked = pillars.iter().any(|p| overlaps_m(&gap, p.bounds()))
                                || taken.iter().any(|&t| overlaps_m(&gap, t))
                                || landings.iter().any(|l| l.overlaps(&gap))
                                || wells_here.iter().any(|w| w.overlaps(&gap));
                            if blocked_foot(&foot) || gap_blocked {
                                continue;
                            }
                            push_partition_run(
                                &mut out,
                                across_x,
                                at,
                                a,
                                b,
                                s.floor_y_cm,
                                top,
                                style,
                            );
                            placed += 1;
                        }
                        if placed >= 2 {
                            continue;
                        }
                    }
                } else if u < kn.maze + kn.cell && s.area_m2() >= CELL_MIN_AREA_M2 {
                    let side = (CELL_SIDE_CM.0
                        + (maze.next01() * (CELL_SIDE_CM.1 - CELL_SIDE_CM.0) as f32) as i32)
                        / 50
                        * 50;
                    let outer = side + 2 * PARTITION_T_CM;
                    let free_x = r.width_cm() - 2 * CELL_CLEAR_CM - outer;
                    let free_z = r.depth_cm() - 2 * CELL_CLEAR_CM - outer;
                    if free_x > 0 && free_z > 0 {
                        let x0 =
                            r.min_x_cm + CELL_CLEAR_CM + (maze.next01() * free_x as f32) as i32;
                        let z0 =
                            r.min_z_cm + CELL_CLEAR_CM + (maze.next01() * free_z as f32) as i32;
                        let foot = super::plan::PlanRect {
                            min_x_cm: x0,
                            min_z_cm: z0,
                            max_x_cm: x0 + outer,
                            max_z_cm: z0 + outer,
                        };
                        // Con su paso alrededor, como una isla.
                        if !blocked_foot(&foot.shrunk(-CELL_CLEAR_CM)) {
                            let door_side = (maze.next01() * 4.0) as i32 % 4;
                            let top = s.floor_y_cm + clear;
                            let (x1, z1) = (x0 + outer, z0 + outer);
                            let jamb = (outer - CELL_DOOR_CM) / 2;
                            // Los cuatro lados: 0 = N (z1), 1 = E (x1), 2 = S (z0), 3 = O (x0). El
                            // lado con boca se emite como dos muñones.
                            for side_k in 0..4 {
                                let (across_x, at, a, b) = match side_k {
                                    0 => (false, z1 - PARTITION_T_CM, x0, x1),
                                    1 => (true, x1 - PARTITION_T_CM, z0, z1),
                                    2 => (false, z0, x0, x1),
                                    _ => (true, x0, z0, z1),
                                };
                                if side_k == door_side {
                                    push_partition_run(
                                        &mut out,
                                        across_x,
                                        at,
                                        a,
                                        a + jamb,
                                        s.floor_y_cm,
                                        top,
                                        style,
                                    );
                                    push_partition_run(
                                        &mut out,
                                        across_x,
                                        at,
                                        b - jamb,
                                        b,
                                        s.floor_y_cm,
                                        top,
                                        style,
                                    );
                                } else {
                                    push_partition_run(
                                        &mut out,
                                        across_x,
                                        at,
                                        a,
                                        b,
                                        s.floor_y_cm,
                                        top,
                                        style,
                                    );
                                }
                            }
                            continue;
                        }
                    }
                }
            }

            // Las partes de mayor a menor: una división por parte, empezando por la grande. Ver el
            // comentario de `host`.
            let mut hosts: Vec<super::plan::PlanRect> = s
                .parts()
                .iter()
                .copied()
                .filter(|p| p.width_cm().min(p.depth_cm()) >= PARTITION_MIN_SPAN_CM)
                .collect();
            hosts.sort_unstable_by_key(|p| -(p.area_m2() as i64));
            if hosts.is_empty() {
                hosts.push(r);
            }
            for k in 0..want as usize {
                // **La división se sortea dentro de una PARTE, no de la envolvente** (ADR-120 D5).
                //
                // Sobre una L, la envolvente cubre terreno del vecino: la tirada caía ahí y la
                // división se descartaba después por `covers_rect`. Medido al subir el rendimiento de
                // la composición de huellas: el recuento de divisiones del barrido bajó de más de mil
                // a 858 — o sea que cada mordisco que PASS 1 ponía le quitaba a PASS 2 su sala. La
                // parte se elige con el mismo flujo, y con una sola parte no se sortea nada: ahí la
                // envolvente y la huella son lo mismo y la secuencia no se mueve.
                // **Y la parte se recorre de mayor a menor, una por división.**
                //
                // Sorteando la parte, media tirada caía en el brazo de tres metros de una L, no cabía
                // un tramo y se perdía la división entera: 919 de las mil que la gramática tiene que
                // emitir. Usando siempre la mayor, las tres divisiones de una nave compuesta se
                // apilaban en la misma parte y se estorbaban entre ellas: 975. Una por parte, de
                // mayor a menor, es lo que además reparte la estructura por toda la huella —que es
                // para lo que está—. Con una sola parte no cambia nada: `hosts` es un elemento y se
                // recorre en círculo, igual que antes.
                let host = hosts[k % hosts.len()];
                // El eje se sortea; el vano que cruza es el lado perpendicular al tabique.
                let across_x = room.next01() < 0.5;
                let (span, room_side) = if across_x {
                    (host.depth_cm(), host.width_cm())
                } else {
                    (host.width_cm(), host.depth_cm())
                };
                if span < PARTITION_MIN_SPAN_CM {
                    continue;
                }
                let free = room_side - 2 * PARTITION_WALL_MARGIN_CM - PARTITION_T_CM;
                if free <= 0 {
                    continue;
                }
                let at_base = if across_x {
                    host.min_x_cm
                } else {
                    host.min_z_cm
                };
                let at = at_base + PARTITION_WALL_MARGIN_CM + (room.next01() * free as f32) as i32;

                // Isla, espolón o partición. La partición sólo puede salir una vez por espacio: dos
                // que se cruzan dejan cuadrantes, y un cuadrante con un solo hueco es la isla que el
                // validador caza.
                let kind = room.next01();
                let from_base = if across_x {
                    host.min_z_cm
                } else {
                    host.min_x_cm
                };
                let (run_from, run_to, gap): PartitionRun = if kind < PARTITION_ISLAND_BELOW {
                    let f = PARTITION_ISLAND_SPAN.0
                        + room.next01() * (PARTITION_ISLAND_SPAN.1 - PARTITION_ISLAND_SPAN.0);
                    let len = ((span as f32 * f) as i32).min(span - 2 * PARTITION_ISLAND_CLEAR_CM);
                    if len < PARTITION_MIN_LEN_CM {
                        continue;
                    }
                    let slack = span - len - 2 * PARTITION_ISLAND_CLEAR_CM;
                    let a = from_base
                        + PARTITION_ISLAND_CLEAR_CM
                        + (room.next01() * slack as f32) as i32;
                    (a, a + len, None)
                } else if split_used || kind < PARTITION_SPUR_BELOW {
                    let f = PARTITION_SPUR_SPAN.0
                        + room.next01() * (PARTITION_SPUR_SPAN.1 - PARTITION_SPUR_SPAN.0);
                    // **El extremo libre nunca se acerca a la pared de enfrente más de lo que mide un
                    // paso.** Sin este tope un espolón del 78 % de un vano corto acaba a metro y medio
                    // del muro: no es un espolón, es una partición con un hueco que no cabe.
                    let len = ((span as f32 * f) as i32).min(span - PARTITION_GAP_CM);
                    if len < PARTITION_MIN_LEN_CM {
                        continue;
                    }
                    if room.next01() < 0.5 {
                        (from_base, from_base + len, None)
                    } else {
                        (from_base + span - len, from_base + span, None)
                    }
                } else {
                    split_used = true;
                    let slack = span - PARTITION_GAP_CM;
                    let g0 = from_base + (room.next01() * slack as f32) as i32;
                    (
                        from_base,
                        from_base + span,
                        Some((g0, g0 + PARTITION_GAP_CM)),
                    )
                };
                let is_split = gap.is_some();

                // El PERFIL: mampara, medio muro bajo, colgado del techo, o de suelo a techo
                // (enm. 8). Un solo dado y en este orden, para que la mampara salga donde salía.
                let profile = room.next01();
                let (bottom, top) = if profile < PARTITION_SCREEN_CHANCE {
                    (
                        s.floor_y_cm,
                        s.floor_y_cm + PARTITION_SCREEN_H_CM.min(clear),
                    )
                } else if profile < kn.low_below {
                    (s.floor_y_cm, s.floor_y_cm + PARTITION_LOW_H_CM)
                } else if profile < kn.hang_below
                    && clear >= PARTITION_HANG_CLEAR_CM + 2 * PARTITION_T_CM
                {
                    (s.floor_y_cm + PARTITION_HANG_CLEAR_CM, s.floor_y_cm + clear)
                } else {
                    (s.floor_y_cm, s.floor_y_cm + clear)
                };

                // La huella entera de la división, para comprobarla de una vez: una división con un
                // trozo suprimido en medio no es la misma cosa, así que o entra completa o no entra.
                let foot = if across_x {
                    super::plan::PlanRect {
                        min_x_cm: at,
                        min_z_cm: run_from,
                        max_x_cm: at + PARTITION_T_CM,
                        max_z_cm: run_to,
                    }
                } else {
                    super::plan::PlanRect {
                        min_x_cm: run_from,
                        min_z_cm: at,
                        max_x_cm: run_to,
                        max_z_cm: at + PARTITION_T_CM,
                    }
                };
                let near_door = foot.shrunk(-PARTITION_DOOR_CLEAR_CM);
                // **Y NUNCA sobre el centro del espacio, que es donde va el agujero de suelo.**
                // `hole_carves` centra siempre su cuadrado, y un macizo es inmune a los vanos
                // (ADR-105 D2): un tabique ahí no sale al restar, sale ENCIMA — un muro cruzando el
                // hueco, que además tapa la caída de dos plantas. Se esquiva el cuadrado entero sin
                // repetir el dado de `hole_carves`: duplicar el sorteo es duplicar lógica que luego
                // se separa, y esquivar el centro de una sala tampoco es una pérdida.
                let hole = hole_square(&r);
                // Y ENTERA sobre suelo propio, **CON SU PASO ALREDEDOR** (ADR-120 D5).
                //
                // La tirada se calcula sobre la envolvente, así que en una L una isla puede nacer
                // flotando en la sala del vecino —y un macizo es inmune a los vanos, o sea que ahí se
                // queda—; ése es el primer motivo. El segundo es peor y sólo aparece con huellas
                // compuestas: la isla se separa de las paredes de la ENVOLVENTE, no de las reales, así
                // que dentro del brazo estrecho de una L puede tapar el brazo entero. El síntoma no
                // es un macizo mal puesto: son mil metros cuadrados de región que dejan de alcanzarse
                // —medido, 2 regiones de 270 con la puerta de junta dentro de la isla—.
                //
                // Se exige que la división MÁS su holgura de paso quepa en la huella. Con una sola
                // parte no cambia nada: ahí la envolvente y la huella son lo mismo.
                // **Y el paso alrededor se le exige a la ISLA, no a todo.** Una isla flota y por eso
                // necesita suelo propio a los cuatro lados; un espolón y una partición nacen PEGADOS
                // a una pared, así que inflarlos los saca del espacio por definición y se descartaban
                // todos: sobre huella compuesta se perdían las dos terceras partes de la gramática.
                let with_gap = foot.shrunk(-PARTITION_ISLAND_CLEAR_CM);
                let is_island = kind < PARTITION_ISLAND_BELOW;
                let blocked = !s.covers_rect(&foot)
                    // Enm. 14 — ni sobre un vano de pared: un espolón que muere en la pared justo
                    // donde hay una ventana la tapa, y el test lo cazó en (−22,85, 391,58).
                    || on_wall_carve(&foot, s, carves)
                    || (s.is_composite() && is_island && !s.covers_rect(&with_gap))
                    || landings.iter().any(|l| l.overlaps(&foot))
                    || (n > 0 && hole.shrunk(-50).overlaps(&foot))
                    || holes_above.iter().any(|h| h.overlaps(&foot))
                    || pits_here.iter().any(|p| p.overlaps(&foot))
                    || wells_here.iter().any(|w| w.overlaps(&foot))
                    || mine.iter().any(|m| m.overlaps(&foot))
                    || pillars.iter().any(|p| {
                        let (x0, z0, x1, z1) = p.bounds();
                        let (a0, b0, a1, b1) = (
                            foot.min_x_cm as f32 / CM_PER_M,
                            foot.min_z_cm as f32 / CM_PER_M,
                            foot.max_x_cm as f32 / CM_PER_M,
                            foot.max_z_cm as f32 / CM_PER_M,
                        );
                        a0 < x1 && a1 > x0 && b0 < z1 && b1 > z0
                    })
                    || doors
                        .iter()
                        .any(|&(dx, dz)| near_door.contains_point(dx, dz))
                    || taken.iter().any(|&(x0, z0, x1, z1)| {
                        let (a0, b0, a1, b1) = (
                            foot.min_x_cm as f32 / CM_PER_M,
                            foot.min_z_cm as f32 / CM_PER_M,
                            foot.max_x_cm as f32 / CM_PER_M,
                            foot.max_z_cm as f32 / CM_PER_M,
                        );
                        a0 < x1 && a1 > x0 && b0 < z1 && b1 > z0
                    });
                if blocked {
                    // Una partición que no llegó a ponerse no gasta el cupo del espacio.
                    if is_split {
                        split_used = false;
                    }
                    continue;
                }
                mine.push(foot);

                // Y a cajas: el hueco parte la tirada en dos, y cada trozo se corta a `MAX_SOLID_CM`
                // por lo mismo que un pretil — un macizo se dibuja en el chunk de su CENTRO.
                // **Y un trozo demasiado corto se lo come el hueco.** El hueco de una partición se
                // sortea a lo largo del vano, así que puede caer a treinta centímetros de un extremo
                // y dejar un muñón de treinta por treinta: eso no es un tabique, es un poste, y el
                // test lo cazó en la primera pasada. Tirar el trozo ensancha el paso un palmo, que es
                // la respuesta correcta.
                let runs: Vec<(i32, i32)> = match gap {
                    Some((g0, g1)) => vec![(run_from, g0), (g1, run_to)],
                    None => vec![(run_from, run_to)],
                }
                .into_iter()
                .filter(|(a, b)| b - a >= PARTITION_STUB_MIN_CM)
                .collect();
                for (a, b) in runs {
                    // **Y el troceado es a PARTES IGUALES, no a mordiscos de 20 m.** Cortando
                    // avaricioso, una tirada de 20,30 m sale como un trozo de 20 m y un MUÑÓN de
                    // 30 × 30 — un poste, no un tabique. Sale 1 vez cada 30 semillas y el test lo
                    // caza; repartir el resto entre todos los trozos lo hace imposible.
                    let len = b - a;
                    let n = (len + MAX_SOLID_CM - 1) / MAX_SOLID_CM;
                    let mut cut = a;
                    for k in 1..=n {
                        let end = a + (len * k) / n;
                        let (x, z, sx, sz) = if across_x {
                            (at, cut, PARTITION_T_CM, end - cut)
                        } else {
                            (cut, at, end - cut, PARTITION_T_CM)
                        };
                        out.push(Wg3Solid {
                            x_cm: x,
                            z_cm: z,
                            size_x_cm: sx,
                            size_z_cm: sz,
                            bottom_y_cm: bottom,
                            top_y_cm: top,
                            style,
                            yaw_deg: 0,
                            shape: SHAPE_BOX,
                        });
                        // ADR-105 enm. 16 — el medio muro bajo lleva su tabla encima: decoración
                        // (no estampa, no frena), que vuela por las dos caras y los extremos. En
                        // tono de marco de la sala, el mismo que su rodapié.
                        if top - bottom == PARTITION_LOW_H_CM {
                            let o = LOW_WALL_RAIL_OVERHANG_CM;
                            out.push(Wg3Solid {
                                x_cm: x - o,
                                z_cm: z - o,
                                size_x_cm: sx + 2 * o,
                                size_z_cm: sz + 2 * o,
                                bottom_y_cm: top,
                                top_y_cm: top + LOW_WALL_RAIL_H_CM,
                                style: style | STYLE_DECOR_BIT,
                                yaw_deg: 0,
                                shape: SHAPE_BOX,
                            });
                        }
                        cut = end;
                    }
                }
            }
        }
    }
    out
}

/// ADR-105 enm. 11 — **la ARCADA**: hiladas de arco entre dos pilares consecutivos de la misma fila.
/// Fondo de la hilada (a lo largo del eje perpendicular a la fila); 40 y no 15 para que la arcada
/// se lea exenta y no como una pared con arcos.
const ARCADE_T_CM: i32 = 40;
/// Altura de cada hilada, media celda como en el arco de una boca.
const ARCADE_BAND_CM: i32 = 25;
/// Vano máximo entre dos pilares para tender un arco.
const ARCADE_MAX_SPAN_CM: i32 = 900;
/// Hueco libre mínimo bajo la línea de arranque del arco.
const ARCADE_CLEAR_CM: i32 = 250;
/// Qué proporción de las naves con pilares llevan arcadas.
const ARCADE_CHANCE: f32 = 0.35;
const SALT_ARCADE: u32 = 0xB1_11_A0_09;

/// ADR-105 enm. 11 — **la BÓVEDA ESCALONADA**: hiladas colgadas de las dos paredes largas que se
/// meten hacia el centro cuanto más arriba, como una bóveda de ladrillo por aproximación de hiladas.
/// Sólo en salas altas: por debajo de `VAULT_MIN_CLEAR_CM` no hay dónde escalonar.
const VAULT_MIN_CLEAR_CM: i32 = 400;
const VAULT_BAND_CM: i32 = 25;
/// Cuánto baja la bóveda desde el techo, como máximo.
const VAULT_MAX_RISE_CM: i32 = 200;
/// Hueco libre mínimo bajo la hilada más baja.
const VAULT_CLEAR_CM: i32 = 260;
/// Cuánto se mete la hilada más alta desde la pared, como máximo (y nunca más de un cuarto del
/// lado corto: la clave tiene que quedar abierta).
const VAULT_MAX_REACH_CM: i32 = 300;
const VAULT_CHANCE: f32 = 0.25;
const SALT_VAULT: u32 = 0xB1_11_A0_0A;

/// ¿Es este macizo una hilada de arcada o de bóveda? Por la forma: media celda de alto y ni grosor
/// de pared ni de pretil ni de división — las hiladas de una BOCA miden 15 y las de aquí 40 o más.
pub(super) fn is_hung_band(s: &Wg3Solid) -> bool {
    let thin = s.size_x_cm.min(s.size_z_cm);
    s.top_y_cm - s.bottom_y_cm == ARCADE_BAND_CM && thin >= ARCADE_T_CM
}

fn pillar_arcades(building: &RegionBuilding, pillars: &[Wg3Solid]) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    let seed = building.seed;
    for plan in &building.storeys {
        for (_, s) in plan.built() {
            if s.role != SpaceRole::Hall || s.rise_cm != 0 || is_atrium(s) {
                continue;
            }
            let r = s.rect;
            // Los pilares CUADRADOS de esta sala, a su cota. Los brazos de cruz se quedan fuera: la
            // arcada arranca de una cara plana.
            let mut mine: Vec<&Wg3Solid> = pillars
                .iter()
                .filter(|p| {
                    is_pillar(p)
                        && p.size_x_cm == p.size_z_cm
                        && p.bottom_y_cm == s.floor_y_cm
                        && p.x_cm >= r.min_x_cm
                        && p.x_cm + p.size_x_cm <= r.max_x_cm
                        && p.z_cm >= r.min_z_cm
                        && p.z_cm + p.size_z_cm <= r.max_z_cm
                })
                .collect();
            if mine.len() < 2 {
                continue;
            }
            let (cx, cz) = r.centre_m();
            let mut st = super::hash::stream_at(seed, cx, cz, SALT_ARCADE);
            if st.next01() >= knobs_of(seed, s).arcade {
                continue;
            }
            let clear = clear_height_cm(s);
            let top = s.floor_y_cm + clear;
            let style = style_of(s.role);
            // Filas a lo largo de X (mismo z) o de Z (mismo x): la que más pares consecutivos dé.
            let along_x = st.next01() < 0.5;
            mine.sort_by_key(|p| {
                if along_x {
                    (p.z_cm, p.x_cm)
                } else {
                    (p.x_cm, p.z_cm)
                }
            });
            for w in mine.windows(2) {
                let (a, b) = (w[0], w[1]);
                let same_row = if along_x {
                    a.z_cm == b.z_cm && a.size_z_cm == b.size_z_cm
                } else {
                    a.x_cm == b.x_cm && a.size_x_cm == b.size_x_cm
                };
                if !same_row {
                    continue;
                }
                let (from, to) = if along_x {
                    (a.x_cm + a.size_x_cm, b.x_cm)
                } else {
                    (a.z_cm + a.size_z_cm, b.z_cm)
                };
                let span = to - from;
                if span <= 0 || span > ARCADE_MAX_SPAN_CM {
                    continue;
                }
                let radius = span / 2;
                let rise = radius.min(clear - ARCADE_CLEAR_CM);
                if rise < 2 * ARCADE_BAND_CM {
                    continue;
                }
                let spring = top - rise;
                // El fondo de la hilada, centrado en el eje del pilar.
                let (t0, t1) = if along_x {
                    let c = a.z_cm + a.size_z_cm / 2;
                    (c - ARCADE_T_CM / 2, c + ARCADE_T_CM / 2)
                } else {
                    let c = a.x_cm + a.size_x_cm / 2;
                    (c - ARCADE_T_CM / 2, c + ARCADE_T_CM / 2)
                };
                let mut y1 = top;
                while y1 - ARCADE_BAND_CM >= spring {
                    let y0 = y1 - ARCADE_BAND_CM;
                    let t = (y1 - spring) as f32 / rise as f32;
                    let open = (radius as f32 * (1.0 - t * t).max(0.0).sqrt()) as i32;
                    let fill = radius - open;
                    if fill >= ARCADE_T_CM {
                        for (p0, p1) in [(from, from + fill), (to - fill, to)] {
                            let (x, z, sx, sz) = if along_x {
                                (p0, t0, p1 - p0, t1 - t0)
                            } else {
                                (t0, p0, t1 - t0, p1 - p0)
                            };
                            out.push(Wg3Solid {
                                x_cm: x,
                                z_cm: z,
                                size_x_cm: sx,
                                size_z_cm: sz,
                                bottom_y_cm: y0,
                                top_y_cm: y1,
                                style,
                                yaw_deg: 0,
                                shape: SHAPE_BOX,
                            });
                        }
                    }
                    y1 = y0;
                }
            }
        }
    }
    out
}

fn wall_vaults(
    building: &RegionBuilding,
    manifest: &Wg3Manifest,
    placements: &[Wg3Placement],
) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    let seed = building.seed;
    let taken: Vec<(f32, f32, f32, f32)> = placements
        .iter()
        .filter_map(|p| {
            manifest
                .pieces
                .get(p.piece as usize)
                .map(|piece| p.bounds(piece))
        })
        .collect();
    for (n, plan) in building.storeys.iter().enumerate() {
        let mut cuts: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below == n)
            .map(|w| w.rect.shrunk(-50))
            .collect();
        cuts.extend(hole_squares_above(building, n));
        for (_, s) in plan.built() {
            if s.is_composite() || s.rise_cm != 0 || s.role == SpaceRole::Stair || is_atrium(s) {
                continue;
            }
            let clear = clear_height_cm(s);
            if clear < VAULT_MIN_CLEAR_CM {
                continue;
            }
            let r = s.rect;
            let (cx, cz) = r.centre_m();
            if taken
                .iter()
                .any(|&(x0, z0, x1, z1)| cx > x0 && cx < x1 && cz > z0 && cz < z1)
            {
                continue;
            }
            let mut st = super::hash::stream_at(seed, cx, cz, SALT_VAULT);
            if st.next01() >= knobs_of(seed, s).vault {
                continue;
            }
            let wide = r.width_cm() >= r.depth_cm();
            let short = r.width_cm().min(r.depth_cm());
            let reach = VAULT_MAX_REACH_CM.min(short / 4);
            let rise = VAULT_MAX_RISE_CM.min(clear - VAULT_CLEAR_CM);
            let bands = rise / VAULT_BAND_CM;
            if bands < 2 || reach < 50 {
                continue;
            }
            let top = s.floor_y_cm + clear;
            let style = style_of(s.role);
            let inner = r.shrunk(WALL_T_CM);
            for j in 0..bands {
                // La hilada más alta (j = 0) se mete `reach`; las de abajo, cada vez menos, con el
                // perfil de un cuarto de elipse: es lo que se lee como bóveda y no como escalera.
                let f = 1.0 - (j as f32 / bands as f32).powi(2);
                // En pasos de 10 y nunca por debajo de 50: con 30 la hilada tenía la forma exacta
                // de una división y el test de divisiones la adoptó (cota 365, región (1,−1)).
                let p = ((reach as f32 * f) as i32) / 10 * 10;
                if p < 50 {
                    continue;
                }
                let (y1, y0) = (top - j * VAULT_BAND_CM, top - (j + 1) * VAULT_BAND_CM);
                for wall in 0..2 {
                    let foot = if wide {
                        let (z0, z1) = if wall == 0 {
                            (inner.min_z_cm, inner.min_z_cm + p)
                        } else {
                            (inner.max_z_cm - p, inner.max_z_cm)
                        };
                        super::plan::PlanRect {
                            min_x_cm: inner.min_x_cm,
                            min_z_cm: z0,
                            max_x_cm: inner.max_x_cm,
                            max_z_cm: z1,
                        }
                    } else {
                        let (x0, x1) = if wall == 0 {
                            (inner.min_x_cm, inner.min_x_cm + p)
                        } else {
                            (inner.max_x_cm - p, inner.max_x_cm)
                        };
                        super::plan::PlanRect {
                            min_x_cm: x0,
                            min_z_cm: inner.min_z_cm,
                            max_x_cm: x1,
                            max_z_cm: inner.max_z_cm,
                        }
                    };
                    if cuts.iter().any(|c| c.overlaps(&foot)) {
                        continue;
                    }
                    // Troceado al tope de macizo a lo largo de la pared.
                    let (from, to) = if wide {
                        (foot.min_x_cm, foot.max_x_cm)
                    } else {
                        (foot.min_z_cm, foot.max_z_cm)
                    };
                    let len = to - from;
                    let pieces = (len + MAX_SOLID_CM - 1) / MAX_SOLID_CM;
                    let mut cut = from;
                    for k in 1..=pieces {
                        let end = from + (len * k) / pieces;
                        let (x, z, sx, sz) = if wide {
                            (cut, foot.min_z_cm, end - cut, foot.depth_cm())
                        } else {
                            (foot.min_x_cm, cut, foot.width_cm(), end - cut)
                        };
                        out.push(Wg3Solid {
                            x_cm: x,
                            z_cm: z,
                            size_x_cm: sx,
                            size_z_cm: sz,
                            bottom_y_cm: y0,
                            top_y_cm: y1,
                            style,
                            yaw_deg: 0,
                            shape: SHAPE_BOX,
                        });
                        cut = end;
                    }
                }
            }
        }
    }
    out
}

/// Ancho de una viga, en centímetros. Ni 15 (faldón), ni 20 (pretil), ni 30 (división): los tests
/// distinguen los macizos por su forma y una viga tiene que tener la suya.
const BEAM_T_CM: i32 = 40;
/// Cuánto cuelga una viga por debajo de la losa de techo.
const BEAM_DROP_CM: i32 = 40;
/// Altura libre mínima del espacio para que lleve vigas. Con 3,00 quedan 2,60 bajo la viga; un
/// servicio de 2,80 con vigas se leería como un túnel.
const BEAM_MIN_CLEAR_CM: i32 = 300;
/// Superficie mínima para que un espacio lleve vigas: por debajo, una viga lo parte en dos techos.
const BEAM_MIN_AREA_M2: f32 = 40.0;
/// Qué proporción de los espacios que CABEN llevan vigas. No todos, por lo mismo que los pilares:
/// un techo liso al lado de uno con vigas es lo que hace que el segundo signifique algo.
const BEAM_ROOM_CHANCE: f32 = 0.55;
/// De los que llevan vigas, cuántos las llevan en los DOS ejes: casetones.
const BEAM_GRID_CHANCE: f32 = 0.30;
/// Paso entre vigas, mínimo y máximo, sorteado por sala.
const BEAM_PITCH_CM: (i32, i32) = (350, 500);
/// Y en zona DENSA o ANÓMALA del campo, más apretado.
const BEAM_PITCH_DENSE_CM: (i32, i32) = (250, 350);
/// Un trozo de viga por debajo de esto se tira: es un tocón, no una viga.
const BEAM_MIN_LEN_CM: i32 = 100;
/// Sal del sorteo de vigas de una SALA.
const SALT_BEAM_ROOM: u32 = 0xB1_11_A0_03;

/// ADR-105 enm. 13 — **VIGUETAS**: el tercer ritmo de techo, fino y apretado, en un solo eje. Treinta
/// y cinco de ancho (ninguna otra forma colgada mide eso: la viga 40, el faldón 15) y 30 de caída.
const JOIST_CHANCE: f32 = 0.25;
const JOIST_T_CM: i32 = 35;
const JOIST_DROP_CM: i32 = 30;
const JOIST_PITCH_CM: i32 = 100;

/// ADR-105 enm. 13 — **DESCUELGUE PERIMETRAL y CORNISA**: una banda colgada del techo a lo largo de
/// las cuatro paredes (60 de fondo, 50 de caída), o una moldura de 10 × 10 en la arista. Excluyentes
/// por sala, con un solo dado.
const SOFFIT_CHANCE: f32 = 0.30;
const CORNICE_BELOW: f32 = 0.60;
const SOFFIT_DEPTH_CM: i32 = 60;
const SOFFIT_DROP_CM: i32 = 50;
const CORNICE_CM: i32 = 10;
const SOFFIT_MIN_CLEAR_CM: i32 = 300;
const SALT_SOFFIT: u32 = 0xB1_11_A0_0C;

/// ADR-105 enm. 13 — **la TARIMA**: un escalón de suelo de `PLATFORM_H_CM` pegado a una pared, que
/// ocupa una franja de la sala. Veinte y no treinta: por debajo del escalón del jugador (27) y del de
/// la navegación (30), así que se sube sin pensarlo y el ráster la ofrece como suelo. La zapata del
/// pilar mide 30 justamente para lo contrario.
const PLATFORM_CHANCE: f32 = 0.20;
const PLATFORM_H_CM: i32 = 20;
const PLATFORM_MIN_AREA_M2: f32 = 100.0;
const PLATFORM_MARGIN_CM: i32 = 300;
const PLATFORM_MIN_SPAN_CM: i32 = 450;
const SALT_PLATFORM: u32 = 0xB1_11_A0_0D;

/// ¿Es este macizo una tarima? Por la forma: 20 de alto, más de 4,5 m de largo Y más de 1,5 m de
/// fondo — un dintel bajo un techo de 2,60 también mide 20 de alto y 5 m de largo, pero 15 de fondo.
pub(super) fn is_platform(s: &Wg3Solid) -> bool {
    s.top_y_cm - s.bottom_y_cm == PLATFORM_H_CM
        && s.size_x_cm.max(s.size_z_cm) >= PLATFORM_MIN_SPAN_CM
        && s.size_x_cm.min(s.size_z_cm) >= 150
}

/// Las cuatro bandas de un anillo pegado a las paredes de `inner`, de `depth` de fondo, entre `y0` e
/// `y1`, troceadas al tope de macizo y recortadas por `cuts`.
#[allow(clippy::too_many_arguments)]
fn ring_bands(
    out: &mut Vec<Wg3Solid>,
    inner: &super::plan::PlanRect,
    depth: i32,
    y0: i32,
    y1: i32,
    style: u8,
    cuts: &[super::plan::PlanRect],
) {
    let strips = [
        // Las dos largas en Z (paredes O y E), enteras; las dos en X entre ellas.
        super::plan::PlanRect {
            min_x_cm: inner.min_x_cm,
            min_z_cm: inner.min_z_cm,
            max_x_cm: inner.min_x_cm + depth,
            max_z_cm: inner.max_z_cm,
        },
        super::plan::PlanRect {
            min_x_cm: inner.max_x_cm - depth,
            min_z_cm: inner.min_z_cm,
            max_x_cm: inner.max_x_cm,
            max_z_cm: inner.max_z_cm,
        },
        super::plan::PlanRect {
            min_x_cm: inner.min_x_cm + depth,
            min_z_cm: inner.min_z_cm,
            max_x_cm: inner.max_x_cm - depth,
            max_z_cm: inner.min_z_cm + depth,
        },
        super::plan::PlanRect {
            min_x_cm: inner.min_x_cm + depth,
            min_z_cm: inner.max_z_cm - depth,
            max_x_cm: inner.max_x_cm - depth,
            max_z_cm: inner.max_z_cm,
        },
    ];
    for (i, strip) in strips.iter().enumerate() {
        if strip.width_cm() <= 0 || strip.depth_cm() <= 0 {
            continue;
        }
        if cuts.iter().any(|c| c.overlaps(strip)) {
            continue;
        }
        let run_x = i >= 2;
        let (from, to) = if run_x {
            (strip.min_x_cm, strip.max_x_cm)
        } else {
            (strip.min_z_cm, strip.max_z_cm)
        };
        let len = to - from;
        let pieces = (len + MAX_SOLID_CM - 1) / MAX_SOLID_CM;
        let mut cut = from;
        for k in 1..=pieces {
            let end = from + (len * k) / pieces;
            let (x, z, sx, sz) = if run_x {
                (cut, strip.min_z_cm, end - cut, strip.depth_cm())
            } else {
                (strip.min_x_cm, cut, strip.width_cm(), end - cut)
            };
            out.push(Wg3Solid {
                x_cm: x,
                z_cm: z,
                size_x_cm: sx,
                size_z_cm: sz,
                bottom_y_cm: y0,
                top_y_cm: y1,
                style,
                yaw_deg: 0,
                shape: SHAPE_BOX,
            });
            cut = end;
        }
    }
}

fn wall_soffits(
    building: &RegionBuilding,
    manifest: &Wg3Manifest,
    placements: &[Wg3Placement],
) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    let seed = building.seed;
    let taken: Vec<(f32, f32, f32, f32)> = placements
        .iter()
        .filter_map(|p| {
            manifest
                .pieces
                .get(p.piece as usize)
                .map(|piece| p.bounds(piece))
        })
        .collect();
    for (n, plan) in building.storeys.iter().enumerate() {
        let mut cuts: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below == n)
            .map(|w| w.rect.shrunk(-50))
            .collect();
        cuts.extend(hole_squares_above(building, n));
        for (_, s) in plan.built() {
            if s.is_composite() || s.rise_cm != 0 || s.role == SpaceRole::Stair || is_atrium(s) {
                continue;
            }
            let clear = clear_height_cm(s);
            if clear < SOFFIT_MIN_CLEAR_CM {
                continue;
            }
            let r = s.rect;
            let (cx, cz) = r.centre_m();
            if taken
                .iter()
                .any(|&(x0, z0, x1, z1)| cx > x0 && cx < x1 && cz > z0 && cz < z1)
            {
                continue;
            }
            let u = super::hash::stream_at(seed, cx, cz, SALT_SOFFIT).next01();
            let k = knobs_of(seed, s);
            let top = s.floor_y_cm + clear;
            let style = style_of(s.role);
            let inner = r.shrunk(WALL_T_CM);
            if u < k.soffit {
                ring_bands(
                    &mut out,
                    &inner,
                    SOFFIT_DEPTH_CM,
                    top - SOFFIT_DROP_CM,
                    top,
                    style,
                    &cuts,
                );
            } else if u < k.soffit + k.cornice {
                ring_bands(
                    &mut out,
                    &inner,
                    CORNICE_CM,
                    top - CORNICE_CM,
                    top,
                    style,
                    &cuts,
                );
            }
        }
    }
    out
}

fn floor_platforms(
    building: &RegionBuilding,
    manifest: &Wg3Manifest,
    placements: &[Wg3Placement],
    segments: &[Wg3Segment],
    blocks: &[Wg3Solid],
) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    let seed = building.seed;
    let taken: Vec<(f32, f32, f32, f32)> = placements
        .iter()
        .filter_map(|p| {
            manifest
                .pieces
                .get(p.piece as usize)
                .map(|piece| p.bounds(piece))
        })
        .collect();
    for (n, plan) in building.storeys.iter().enumerate() {
        let mut keep_out: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below + 1 == n || w.storey_below == n)
            .map(|w| w.rect.shrunk(-50))
            .collect();
        keep_out.extend(hole_squares_above(building, n));
        if n == 0 {
            keep_out.extend(pit_rects_of(building, segments));
        }
        // ADR-105 enm. 17 — y los bloques gruesos de esta planta, con su hueco de paso.
        keep_out.extend(
            blocks
                .iter()
                .filter(|b| {
                    (b.bottom_y_cm - plan.spaces.first().map_or(0, |sp| sp.floor_y_cm)).abs()
                        < STOREY_HEIGHT_CM / 2
                })
                .map(|b| {
                    super::plan::PlanRect {
                        min_x_cm: b.x_cm,
                        min_z_cm: b.z_cm,
                        max_x_cm: b.x_cm + b.size_x_cm,
                        max_z_cm: b.z_cm + b.size_z_cm,
                    }
                    .shrunk(-BLOCK_GAP_CM)
                }),
        );
        for (_, s) in plan.built() {
            if s.is_composite()
                || s.rise_cm != 0
                || s.role.is_circulation()
                || s.role == SpaceRole::Stair
                || is_atrium(s)
                || s.area_m2() < PLATFORM_MIN_AREA_M2
            {
                continue;
            }
            let r = s.rect;
            let (cx, cz) = r.centre_m();
            if taken
                .iter()
                .any(|&(x0, z0, x1, z1)| cx > x0 && cx < x1 && cz > z0 && cz < z1)
            {
                continue;
            }
            let mut st = super::hash::stream_at(seed, cx, cz, SALT_PLATFORM);
            if st.next01() >= knobs_of(seed, s).platform {
                continue;
            }
            // Pegada a un lado sorteado, con un fondo de un tercio del lado perpendicular.
            let side = (st.next01() * 4.0) as i32 % 4;
            let inner = r.shrunk(WALL_T_CM);
            let along_x = side % 2 == 0;
            let depth = ((if along_x {
                inner.depth_cm()
            } else {
                inner.width_cm()
            }) / 3)
                / 10
                * 10;
            let span = (if along_x {
                inner.width_cm()
            } else {
                inner.depth_cm()
            }) - 2 * PLATFORM_MARGIN_CM;
            if depth < 150 || span < PLATFORM_MIN_SPAN_CM {
                continue;
            }
            let foot = match side {
                0 => super::plan::PlanRect {
                    min_x_cm: inner.min_x_cm + PLATFORM_MARGIN_CM,
                    min_z_cm: inner.max_z_cm - depth,
                    max_x_cm: inner.max_x_cm - PLATFORM_MARGIN_CM,
                    max_z_cm: inner.max_z_cm,
                },
                1 => super::plan::PlanRect {
                    min_x_cm: inner.max_x_cm - depth,
                    min_z_cm: inner.min_z_cm + PLATFORM_MARGIN_CM,
                    max_x_cm: inner.max_x_cm,
                    max_z_cm: inner.max_z_cm - PLATFORM_MARGIN_CM,
                },
                2 => super::plan::PlanRect {
                    min_x_cm: inner.min_x_cm + PLATFORM_MARGIN_CM,
                    min_z_cm: inner.min_z_cm,
                    max_x_cm: inner.max_x_cm - PLATFORM_MARGIN_CM,
                    max_z_cm: inner.min_z_cm + depth,
                },
                _ => super::plan::PlanRect {
                    min_x_cm: inner.min_x_cm,
                    min_z_cm: inner.min_z_cm + PLATFORM_MARGIN_CM,
                    max_x_cm: inner.min_x_cm + depth,
                    max_z_cm: inner.max_z_cm - PLATFORM_MARGIN_CM,
                },
            };
            // Ni bajo el agujero propio (lo taparía: un macizo es inmune a los vanos), ni sobre
            // pozos ni rellanos, que tienen su propia cota.
            if (n > 0 && hole_square(&r).shrunk(-50).overlaps(&foot))
                || keep_out.iter().any(|k| k.overlaps(&foot))
            {
                continue;
            }
            let style = style_of(s.role);
            let (from, to, run_x) = if along_x {
                (foot.min_x_cm, foot.max_x_cm, true)
            } else {
                (foot.min_z_cm, foot.max_z_cm, false)
            };
            let len = to - from;
            let pieces = (len + MAX_SOLID_CM - 1) / MAX_SOLID_CM;
            let mut cut = from;
            for k in 1..=pieces {
                let end = from + (len * k) / pieces;
                let (x, z, sx, sz) = if run_x {
                    (cut, foot.min_z_cm, end - cut, foot.depth_cm())
                } else {
                    (foot.min_x_cm, cut, foot.width_cm(), end - cut)
                };
                out.push(Wg3Solid {
                    x_cm: x,
                    z_cm: z,
                    size_x_cm: sx,
                    size_z_cm: sz,
                    bottom_y_cm: s.floor_y_cm,
                    top_y_cm: s.floor_y_cm + PLATFORM_H_CM,
                    style,
                    yaw_deg: 0,
                    shape: SHAPE_BOX,
                });
                cut = end;
            }
        }
    }
    out
}

/// ¿Este macizo es una viga de [`ceiling_beams`]? Para los tests, por la forma: cuelga
/// `BEAM_DROP_CM` y mide `BEAM_T_CM` de ancho.
pub(super) fn is_beam(s: &Wg3Solid) -> bool {
    s.top_y_cm - s.bottom_y_cm == BEAM_DROP_CM && s.size_x_cm.min(s.size_z_cm) == BEAM_T_CM
}

/// ADR-105 enmienda 5 — **el RELIEVE DEL TECHO: vigas colgadas del forjado.**
///
/// Un techo plano a altura constante es, con el eje único, el delator más fuerte de que el mundo es
/// una planta extruida. `height_cm` ya varía por espacio (2026-09-04), pero DENTRO del espacio el
/// techo sigue siendo una losa lisa. Una viga es un macizo que cuelga de la losa: no toca el suelo,
/// el ráster mide hueco libre por columna (`headroom_above_floor`) y un cuerpo de 1,80 pasa bajo
/// 2,60 sin enterarse. Lo que cambia es la vista, y la cambia con el ritmo que los plafones
/// deberían dar y no dan (R32).
///
/// Dos ritmos y un solo caso: vigas cruzando el eje CORTO de cada parte, o en los dos ejes
/// (casetones). El paso se sortea por sala y el campo de densidad lo aprieta en zona densa.
///
/// # Dónde NO va una viga
///
/// - Bajo un techo de menos de 3 m, ni en un atrio (su techo es el de dos plantas), ni en una
///   escalera o un hundido.
/// - En un espacio resuelto con una pieza del catálogo: su techo es el que horneó quien la dibujó.
/// - Cruzando la boca de un pozo que ARRANCA en esta planta —cerraría la subida— ni bajo un
///   candidato a agujero de forjado, propio o de la planta de arriba (enm. 4 D5): ahí se RECORTA
///   el tramo, no se pierde la viga.
fn ceiling_beams(
    building: &RegionBuilding,
    manifest: &Wg3Manifest,
    placements: &[Wg3Placement],
) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    let seed = building.seed;
    let taken: Vec<(f32, f32, f32, f32)> = placements
        .iter()
        .filter_map(|p| {
            manifest
                .pieces
                .get(p.piece as usize)
                .map(|piece| p.bounds(piece))
        })
        .collect();

    for (n, plan) in building.storeys.iter().enumerate() {
        let mut cuts: Vec<super::plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below == n)
            .map(|w| w.rect.shrunk(-50))
            .collect();
        cuts.extend(hole_squares_above(building, n));

        for (_, s) in plan.built() {
            if s.rise_cm != 0 || s.role == SpaceRole::Stair || is_atrium(s) {
                continue;
            }
            let clear = clear_height_cm(s);
            if clear < BEAM_MIN_CLEAR_CM || s.area_m2() < BEAM_MIN_AREA_M2 {
                continue;
            }
            let (cx, cz) = s.rect.centre_m();
            if taken
                .iter()
                .any(|&(x0, z0, x1, z1)| cx > x0 && cx < x1 && cz > z0 && cz < z1)
            {
                continue;
            }
            let k = knobs_of(seed, s);
            let mut room = super::hash::stream_at(seed, cx, cz, SALT_BEAM_ROOM);
            if room.next01() >= k.beam_room {
                continue;
            }
            let dense = k.beam_tight;
            let (lo, hi) = if dense {
                BEAM_PITCH_DENSE_CM
            } else {
                BEAM_PITCH_CM
            };
            let pitch = lo + (room.next01() * (hi - lo) as f32) as i32;
            let grid = room.next01() < BEAM_GRID_CHANCE;
            // ADR-105 enm. 13 — el tercer ritmo: VIGUETAS, finas y a un metro, en un solo eje. Sorteo
            // al final de la secuencia: las salas con vigas o casetones salen donde salían.
            let joists = !grid && room.next01() < k.joist;
            let (pitch, t, drop) = if joists {
                (JOIST_PITCH_CM, JOIST_T_CM, JOIST_DROP_CM)
            } else {
                (pitch, BEAM_T_CM, BEAM_DROP_CM)
            };

            let top = s.floor_y_cm + clear;
            let bottom = top - drop;
            let style = style_of(s.role);
            let mut mine = cuts.clone();
            if n > 0 {
                mine.push(hole_square(&s.rect).shrunk(-50));
            }
            for part in s.parts() {
                // De pared a pared: la viga arranca en la cara interior del muro, no en la línea
                // del plan, o asomaría medio grosor dentro de la sala de al lado.
                let inner = part.shrunk(WALL_T_CM);
                if inner.width_cm() <= 0 || inner.depth_cm() <= 0 {
                    continue;
                }
                let run_x = inner.width_cm() <= inner.depth_cm();
                beam_rows(&mut out, &inner, run_x, t, pitch, bottom, top, style, &mine);
                if grid {
                    beam_rows(
                        &mut out, &inner, !run_x, t, pitch, bottom, top, style, &mine,
                    );
                }
            }
        }
    }
    out
}

/// Las vigas de una parte en un eje: `run_x` es que corren a lo largo de X y se reparten a lo
/// largo de Z. Repartidas a partes iguales como los pilares (enm. 2 D3): se elige cuántos vanos
/// caben y el paso se ajusta, así que la última queda a la misma distancia del muro que la primera.
#[allow(clippy::too_many_arguments)]
fn beam_rows(
    out: &mut Vec<Wg3Solid>,
    inner: &super::plan::PlanRect,
    run_x: bool,
    t: i32,
    pitch: i32,
    bottom: i32,
    top: i32,
    style: u8,
    cuts: &[super::plan::PlanRect],
) {
    let long = if run_x {
        inner.depth_cm()
    } else {
        inner.width_cm()
    };
    let bays = ((long as f32 / pitch as f32).round() as i32).max(1);
    // Una viga sola en mitad del techo no es un ritmo: son dos vanos o nada.
    if bays < 2 {
        return;
    }
    let step = long / bays;
    for k in 1..bays {
        let at = if run_x {
            inner.min_z_cm + k * step
        } else {
            inner.min_x_cm + k * step
        };
        let strip = if run_x {
            super::plan::PlanRect {
                min_x_cm: inner.min_x_cm,
                min_z_cm: at - t / 2,
                max_x_cm: inner.max_x_cm,
                max_z_cm: at + t / 2,
            }
        } else {
            super::plan::PlanRect {
                min_x_cm: at - t / 2,
                min_z_cm: inner.min_z_cm,
                max_x_cm: at + t / 2,
                max_z_cm: inner.max_z_cm,
            }
        };
        beam_strip(out, &strip, run_x, t, bottom, top, style, cuts);
    }
}

/// Una viga recortada por lo que no puede cruzar, y partida al tope de macizo.
#[allow(clippy::too_many_arguments)]
fn beam_strip(
    out: &mut Vec<Wg3Solid>,
    strip: &super::plan::PlanRect,
    run_x: bool,
    t: i32,
    bottom: i32,
    top: i32,
    style: u8,
    cuts: &[super::plan::PlanRect],
) {
    let (from, to) = if run_x {
        (strip.min_x_cm, strip.max_x_cm)
    } else {
        (strip.min_z_cm, strip.max_z_cm)
    };
    let mut blocked: Vec<(i32, i32)> = cuts
        .iter()
        .filter(|c| c.overlaps(strip))
        .map(|c| {
            if run_x {
                (c.min_x_cm, c.max_x_cm)
            } else {
                (c.min_z_cm, c.max_z_cm)
            }
        })
        .collect();
    blocked.sort_unstable();

    let mut push = |a: i32, b: i32| {
        let mut at = a;
        while at < b {
            let end = (at + MAX_SOLID_CM).min(b);
            if end - at >= BEAM_MIN_LEN_CM {
                out.push(if run_x {
                    Wg3Solid {
                        x_cm: at,
                        z_cm: strip.min_z_cm,
                        size_x_cm: end - at,
                        size_z_cm: t,
                        bottom_y_cm: bottom,
                        top_y_cm: top,
                        style,
                        yaw_deg: 0,
                        shape: SHAPE_BOX,
                    }
                } else {
                    Wg3Solid {
                        x_cm: strip.min_x_cm,
                        z_cm: at,
                        size_x_cm: t,
                        size_z_cm: end - at,
                        bottom_y_cm: bottom,
                        top_y_cm: top,
                        style,
                        yaw_deg: 0,
                        shape: SHAPE_BOX,
                    }
                });
            }
            at = end;
        }
    };

    let mut cursor = from;
    for (lo, hi) in blocked {
        if lo > cursor {
            push(cursor, lo.min(to));
        }
        cursor = cursor.max(hi);
    }
    if cursor < to {
        push(cursor, to);
    }
}

fn atrium_carves(building: &RegionBuilding) -> Vec<Wg3Carve> {
    let mut out = Vec::new();
    let grow = (CARVE_DEPTH_M * CM_PER_M) as i32;

    for (n, plan) in building.storeys.iter().enumerate() {
        for s in plan.spaces.iter().filter(|s| is_atrium(s)) {
            // ADR-104 enm. 3 — **el vano se abre LADO A LADO, y sólo hacia una sala construida de
            // arriba.** Un solo vano en anillo tiraba también el muro alto del atrio por los lados
            // en los que arriba no hay nada (vacío del plan, borde de la planta), y desde abajo se
            // veía la nada: los «techos negros» de la galería del 2026-09-04. Donde no hay nadie
            // que mire, el muro de 6,40 se queda: una nave de doble altura tapiada es
            // arquitectura; un agujero a la nada no.
            // **UN VANO POR PLANTA, y no un cajón desde la primera hasta la última** (ADR-104
            // enm. 4). Con una sola planta de vacío las dos formas dan lo mismo y por eso el cajón
            // duró dos ADRs. Con una MEGASALA de cinco no: el cajón va de `floor + 332` a
            // `floor + 1636` y ahí dentro caen los forjados de las plantas intermedias, que cuelgan
            // en `[k*332 - 12, k*332]`. Medido tal cual al intentarlo:
            // `[walk] espacio 0 (spine) a cota 664 con suelo en el 0 % de sus celdas`.
            //
            // Cada vano arranca en el TECHO del forjado de su planta y muere dos losas por debajo
            // del siguiente: se lleva el muro y deja el suelo, que es justo lo que hace balcón.
            // Y sólo hasta donde HAY planta: la megasala sube por encima del edificio (ver
            // `plan::atrium_storeys_for`), y ahí arriba no hay forjado que abrir ni nadie que mire.
            for k in 1..=(s.void_storeys_above as usize).max(1) {
                let plan_up = building.storeys.get(n + k);
                let bottom = s.floor_y_cm + k as i32 * STOREY_HEIGHT_CM;
                let top = bottom + STOREY_HEIGHT_CM - 2 * SLAB_THICKNESS_CM;
                for side in bands_of(&s.rect, grow) {
                    let someone_up = plan_up.is_some_and(|plan_up| {
                        plan_up
                            .spaces
                            .iter()
                            .any(|t| t.role.is_built() && t.hits_rect(&side))
                    });
                    if !someone_up {
                        continue;
                    }
                    out.push(Wg3Carve {
                        x_cm: side.min_x_cm,
                        z_cm: side.min_z_cm,
                        size_x_cm: side.width_cm(),
                        size_z_cm: side.depth_cm(),
                        bottom_y_cm: bottom,
                        top_y_cm: top,
                    });
                }
            }
        }
    }
    out
}

/// ADR-104 enm. 3 — **el faldón del ATRIO, sólo en los lados que quedan con muro.**
///
/// `ceiling_aprons` salta los atrios a propósito (un panel de tres metros y medio sobre una puerta
/// en mitad de una doble altura, y encima el pretil). Pero en los lados donde arriba NO hay sala el
/// muro de 6,40 se queda entero (`atrium_carves`), y una puerta en ese muro dejaba una franja
/// abierta desde el techo del vecino hasta el del atrio: los «huecos al cambiar de altura» que
/// Joel vio en la galería. Ahí el faldón es exactamente la pared que faltaba: tapiada natural. En
/// los lados abiertos a una sala de arriba no hace falta: la franja ES la vista al vacío.
fn atrium_aprons(building: &RegionBuilding) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    let grow = (CARVE_DEPTH_M * CM_PER_M) as i32;
    for (n, plan) in building.storeys.iter().enumerate() {
        let up = building.storeys.get(n + 1);
        for link in &plan.links {
            if link.kind == LinkKind::Route {
                continue;
            }
            let (a, b) = (&plan.spaces[link.a], &plan.spaces[link.b]);
            if !a.role.is_built() || !b.role.is_built() {
                continue;
            }
            let (atrium, other) = if is_atrium(a) {
                (a, b)
            } else if is_atrium(b) {
                (b, a)
            } else {
                continue;
            };
            // ¿En qué banda del atrio cae la puerta, y está esa banda tapiada?
            let door = super::plan::PlanRect {
                min_x_cm: link.at_x_cm - 1,
                min_z_cm: link.at_z_cm - 1,
                max_x_cm: link.at_x_cm + 1,
                max_z_cm: link.at_z_cm + 1,
            };
            let walled = bands_of(&atrium.rect, grow)
                .iter()
                .filter(|band| band.overlaps(&door))
                .any(|band| {
                    !up.is_some_and(|plan_up| {
                        plan_up
                            .spaces
                            .iter()
                            .any(|t| t.role.is_built() && t.hits_rect(band))
                    })
                });
            if !walled {
                continue;
            }
            let low_top = (atrium.floor_y_cm + clear_height_cm(atrium))
                .min(other.floor_y_cm + clear_height_cm(other));
            let high_top = atrium.floor_y_cm + atrium_clear_cm(atrium);
            if high_top <= low_top + SLAB_THICKNESS_CM {
                continue;
            }
            let Some(side) = wall_side(atrium, link.at_x_cm, link.at_z_cm) else {
                continue;
            };
            let half = link.width_cm / 2 + APRON_JAMB_CM;
            let (x_cm, z_cm, size_x_cm, size_z_cm) =
                door_band(side, link.at_x_cm, link.at_z_cm, half);
            out.push(Wg3Solid {
                x_cm,
                z_cm,
                size_x_cm,
                size_z_cm,
                bottom_y_cm: low_top,
                top_y_cm: high_top,
                style: style_of(atrium.role),
                yaw_deg: 0,
                shape: SHAPE_BOX,
            });
        }
    }
    out
}

/// ADR-104 enm. 3 — las cuatro bandas de un vano de atrio: cada una cubre un lado de la huella
/// ensanchada `grow` hacia fuera Y hacia dentro (la pared del atrio vive dentro de su rectángulo;
/// la del vecino, en el suyo), y las esquinas por las dos bandas que las tocan.
pub(super) fn bands_of(r: &super::plan::PlanRect, grow: i32) -> [super::plan::PlanRect; 4] {
    let (x0, z0, x1, z1) = (
        r.min_x_cm - grow,
        r.min_z_cm - grow,
        r.max_x_cm + grow,
        r.max_z_cm + grow,
    );
    let w = 2 * grow;
    [
        // Norte (z máx) y sur (z mín), de esquina a esquina.
        super::plan::PlanRect {
            min_x_cm: x0,
            min_z_cm: z1 - w,
            max_x_cm: x1,
            max_z_cm: z1,
        },
        super::plan::PlanRect {
            min_x_cm: x0,
            min_z_cm: z0,
            max_x_cm: x1,
            max_z_cm: z0 + w,
        },
        // Este (x máx) y oeste (x mín).
        super::plan::PlanRect {
            min_x_cm: x1 - w,
            min_z_cm: z0,
            max_x_cm: x1,
            max_z_cm: z1,
        },
        super::plan::PlanRect {
            min_x_cm: x0,
            min_z_cm: z0,
            max_x_cm: x0 + w,
            max_z_cm: z1,
        },
    ]
}

/// Igual, pudiendo APAGAR el catálogo. Lo usan las sondas que quieren medir sólo lo generado.
pub fn fill_with(plan: &RegionPlan, manifest: &Wg3Manifest, use_catalogue: bool) -> FilledRegion {
    fill_full(plan, manifest, use_catalogue, &RouteSettings::default())
}

/// Igual, con las perillas del enrutador puestas desde fuera. Para las sondas que lo barren.
pub fn fill_full(
    plan: &RegionPlan,
    manifest: &Wg3Manifest,
    use_catalogue: bool,
    route_settings: &RouteSettings,
) -> FilledRegion {
    // Una planta suelta no tiene semilla de edificio: el plan ya lleva la suya en cada posición, y
    // los sorteos por posición del relleno (dinteles) salen de cero. Las sondas comparan plan contra
    // plan, no edificio contra planta.
    fill_storey(plan, manifest, use_catalogue, route_settings, &[], 0)
}

/// El relleno de UNA planta, con los rectángulos en los que no puede ir una pieza del catálogo
/// (los aterrizajes de los pozos que llegan a ella). Ver [`fill_building`].
fn fill_storey(
    plan: &RegionPlan,
    manifest: &Wg3Manifest,
    use_catalogue: bool,
    route_settings: &RouteSettings,
    keep_generated: &[super::plan::PlanRect],
    seed: i32,
) -> FilledRegion {
    let mut out = FilledRegion::default();

    // Lo primero, repartir las puertas: cada espacio tiene que saber TODOS sus huecos antes de
    // teselarse, porque el reparto en tramos depende de dónde caen. Hacerlo al revés obligaría a
    // volver sobre un tramo ya emitida.
    let mut wanted: Vec<Vec<Wanted>> = vec![Vec::new(); plan.spaces.len()];

    let mut route_requests: Vec<PlannedRoute> = Vec::new();

    for (i, link) in plan.links.iter().enumerate() {
        if link.kind == LinkKind::Route {
            out.links_to_route.push((link.a, link.b));
            if let Some(r) = route_request(plan, link.a, link.b, link.width_cm) {
                route_requests.push(r);
            } else {
                out.links_failed.push((link.a, link.b));
            }
            continue;
        }
        let a_side = wall_side(&plan.spaces[link.a], link.at_x_cm, link.at_z_cm);
        let b_side = wall_side(&plan.spaces[link.b], link.at_x_cm, link.at_z_cm);
        match (a_side, b_side) {
            (Some(sa), Some(sb)) => {
                wanted[link.a].push(Wanted {
                    side: sa,
                    at_x_cm: link.at_x_cm,
                    at_z_cm: link.at_z_cm,
                    width_cm: link.width_cm,
                });
                wanted[link.b].push(Wanted {
                    side: sb,
                    at_x_cm: link.at_x_cm,
                    at_z_cm: link.at_z_cm,
                    width_cm: link.width_cm,
                });
            }
            // Un enlace que el plan declaró como vano y cuyo punto no cae en la pared de los dos es
            // un plan incoherente, no un problema del relleno. Se anota con los dos extremos para
            // poder ir a mirarlo, y NO se abre medio vano: media puerta es un muro con una marca.
            _ => {
                let _ = i;
                out.links_failed.push((link.a, link.b));
            }
        }
    }

    // Las puertas de junta son huecos como los demás, pero en la pared EXTERIOR de la región. Van
    // aparte porque su incumplimiento no es cosmético: la vecina ya da por hecho que existen y abre
    // el suyo, así que fallar aquí es un agujero por el que se cae.
    for gate in &plan.gates {
        let space = &plan.spaces[gate.space];
        match wall_side(space, gate.x_cm, gate.z_cm) {
            Some(side) => {
                wanted[gate.space].push(Wanted {
                    side,
                    at_x_cm: gate.x_cm,
                    at_z_cm: gate.z_cm,
                    width_cm: gate.width_cm,
                });
                out.gates_built += 1;
            }
            None => out.gates_failed += 1,
        }
    }

    // **ADR-100 D3 — el enrutador construye lo que el plan pidió, y va ANTES de emitir geometría.**
    //
    // Antes porque una ruta tendida abre pared en los dos espacios que une, y esa pared la construye
    // la tesela de más abajo: enrutar después obligaría a volver sobre un tramo ya emitida. La
    // ocupación son los rectángulos del PLAN, que es todo lo que va a existir.
    if !route_requests.is_empty() {
        let occupancy: Vec<Rect> = plan
            .built()
            .map(|(_, s)| {
                let (min_x, min_z, max_x, max_z) = s.rect.bounds_m();
                Rect {
                    min_x,
                    min_z,
                    max_x,
                    max_z,
                }
            })
            .collect();
        let bounds = plan.bounds_cm.map(|b| b.bounds_m());
        let routed = route::route_planned(&route_requests, &occupancy, bounds, route_settings);

        for r in &route_requests {
            if !routed.built.contains(&(r.a, r.b)) {
                continue;
            }
            // La ruta llega a la pared de los dos, así que los dos necesitan su hueco.
            wanted[r.a].push(mouth_opening(&r.from));
            wanted[r.b].push(mouth_opening(&r.to));
        }
        out.segments.extend(routed.segments);
        out.links_failed.extend(routed.failed);
    }

    // Quién acabó resuelto con una pieza del catálogo. Lo necesita el faldón: el techo de una pieza
    // es el que horneó quien la dibujó, no el que pide el plan.
    let mut by_piece = vec![false; plan.spaces.len()];

    for (i, space) in plan.spaces.iter().enumerate() {
        if !space.role.is_built() {
            continue;
        }
        // **Un espacio con desnivel NUNCA se resuelve con una pieza del catálogo.** Una pieza es
        // plana por construcción: colocarla dejaría el plan diciendo que ahí se baja y la geometría
        // diciendo que no. Costó tres de 42 hundidos en la primera medida, y el síntoma era un
        // agujero con puerta — se dibuja abierto y no se entra.
        let flat = space.rise_cm == 0;
        let free_of_landings = !keep_generated.iter().any(|r| r.overlaps(&space.rect));
        if let Some(p) =
            fitting_piece(space, manifest).filter(|_| use_catalogue && flat && free_of_landings)
        {
            let piece = manifest
                .piece(p.piece)
                .expect("la pieza acaba de salir del catálogo");
            let footprint = footprint_cm(piece, p.rotation);
            out.placements.push(p);
            out.spaces_by_piece += 1;
            // Y se le abren las puertas del plan, porque las suyas están donde las puso quien la
            // dibujó y no donde hace falta. Ver `FilledRegion::carves`.
            //
            // **Contra la pared de la PIEZA y la del vecino a la vez** (auditoría 2026-09-02). El
            // vano se centraba en la línea del plan, y la pared de una pieza más pequeña que su
            // espacio quedaba fuera de la caja: la puerta se abría en el vecino y la pieza nacía
            // sellada — medido como un bolsillo de cinco salas y una puerta de junta sin salida.
            for w in &wanted[i] {
                out.carves.push(carve_for_piece(
                    w,
                    space,
                    (p.origin_x_cm, p.origin_z_cm),
                    footprint,
                    clear_height_cm(space),
                ));
                out.openings_built += 1;
            }
            by_piece[i] = true;
            continue;
        }
        let before = out.segments.len();
        emit_space(i, space, &wanted[i], &mut out);
        if out.segments.len() > before {
            out.spaces_by_segment += 1;
        } else {
            out.spaces_unbuilt += 1;
        }
    }

    // **Y al final, los faldones.** Después de emitir porque necesita saber quién se resolvió con
    // una pieza, que es lo único que este pase no puede medir por su cuenta.
    out.solids.extend(ceiling_aprons(plan, &by_piece));
    // ADR-105 enm. 6 — y los dinteles, por lo mismo.
    out.solids.extend(door_lintels(plan, &by_piece, seed));
    // ADR-105 enm. 7 — y los arcos sobre las bocas anchas.
    out.solids.extend(door_arches(plan, &by_piece, seed));
    // Y las paredes ciegas ganan ventanas y rendijas: vanos, que se restan después de estampar.
    // ADR-105 enm. 12 — y rejillas, hornacinas y ventanas en serie; la rejilla es macizo.
    let (carves, bars) = blind_wall_openings(plan, &by_piece, seed);
    out.carves.extend(carves);
    out.solids.extend(bars);
    // ADR-105 enm. 12 — el parteluz de las bocas anchas.
    out.solids.extend(door_mullions(plan, &by_piece, seed));

    out
}

/// Grosor de pared en centímetros enteros. Derivado, no escrito a mano: el faldón ocupa EXACTAMENTE
/// el sitio de la pared que le falta al vano, y un número suelto que se separe del de
/// [`super::segment::WALL_THICKNESS_M`] deja una rendija que nadie va a buscar aquí.
pub(super) const WALL_T_CM: i32 = (WALL_THICKNESS_M * CM_PER_M) as i32;

/// Cuánto se mete el faldón en cada jamba. Cinco centímetros de solape contra la pared que sigue: la
/// alternativa es una junta a hueso entre dos cajas que se calculan por caminos distintos, y ahí es
/// donde salen las líneas de luz.
const APRON_JAMB_CM: i32 = 5;

/// **EL FALDÓN DEL VANO: lo que cierra un techo contra el techo más alto de al lado.**
///
/// # Por qué hace falta
///
/// Una boca de tramo NO TIENE DINTEL: `segment::emit_side` parte la pared por el ancho del hueco y
/// la parte de suelo a techo, así que el vano llega a la losa. Con las dos salas a la misma altura
/// eso no se nota; en cuanto una mide 2,40 y la vecina 3,40, la pared de la alta está cortada desde
/// el suelo hasta 3,40 y **de 2,52 para arriba no hay nada al otro lado**: la losa de la baja se
/// acaba ahí. Desde dentro de la sala alta es una franja abierta sobre la puerta que da al plenum.
///
/// No es un artefacto nuevo de la altura por espacio: ya existía entre un servicio (2,80) y un
/// corredor (3,20), y es exactamente el mismo que `carve_for_piece` ya paga capando su vano al
/// dintel. Lo que cambia es la frecuencia.
///
/// # Por qué un MACIZO y no un dintel de verdad
///
/// Un dintel pediría que [`Wg3Opening`] llevara altura, y eso es campo nuevo en la trama, espejo en
/// C# y el oráculo de conectores rehecho. El macizo ya viaja, ya se estampa DESPUÉS de excavar
/// (ADR-105 D2) y ya lo dibujan los dos lados con la misma regla. La misma caja, sin canal nuevo.
///
/// # Dónde va exactamente
///
/// En la banda de pared del lado ALTO —el único cuyo muro falta ahí— desde la cara inferior de la
/// losa baja hasta la cara inferior de la alta. El resultado es que **el dintel efectivo del vano es
/// el menor de los dos techos**, que es lo que se pedía, sin tocar una sola boca.
///
/// # Lo que NO cubre, dicho aquí para que nadie lea un verde de más
///
/// - Los espacios resueltos con una PIEZA del catálogo: su techo es el que horneó quien la dibujó y
///   no el que pide el plan, así que la diferencia que se calcularía aquí no sería la real. Su vano
///   ya va capado a [`PIECE_DOOR_CLEAR_CM`], que es el dintel de toda la vida.
/// - Las puertas de JUNTA: al otro lado hay otra región, y su altura no se negocia en el contrato de
///   ADR-096. Cruzar de región sigue siendo por espacios de la misma altura de siempre.
/// - Las bocas de RUTA: el conector trae su propia altura del enrutador y no la del espacio.
fn ceiling_aprons(plan: &RegionPlan, by_piece: &[bool]) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    for link in &plan.links {
        // Una ruta no comparte pared: los dos extremos dan a un conector, no el uno al otro.
        if link.kind == LinkKind::Route {
            continue;
        }
        let (a, b) = (&plan.spaces[link.a], &plan.spaces[link.b]);
        if !a.role.is_built() || !b.role.is_built() || by_piece[link.a] || by_piece[link.b] {
            continue;
        }
        // **Y un ATRIO no lleva faldón**, aunque su salto de techo sea el mayor del mundo.
        //
        // Su altura libre no es la de una sala: son dos plantas (`ATRIUM_CLEAR_CM`), y taparlas
        // plantaría tres metros y medio de panel sobre la puerta — que ya no es un dintel, es una
        // pared en mitad de una doble altura. Y peor: sube hasta la cota del PRETIL de ADR-104, que
        // está justo encima en el borde del vacío, y el pretil deja de tener por dónde ver. Medido:
        // `el pretil de (-61.41, 347.05) sigue siendo macizo 4.92 m arriba`.
        //
        // La franja que queda abierta sobre la puerta de un atrio no la trae esta fase: un atrio ya
        // medía 6,40 contra los 2,80 de un servicio antes de que la altura fuera por espacio. Es de
        // ADR-104, y se arregla donde se decidió la doble altura.
        if is_atrium(a) || is_atrium(b) {
            continue;
        }
        let top_a = a.floor_y_cm + clear_height_cm(a);
        let top_b = b.floor_y_cm + clear_height_cm(b);
        let (low_top, high_top) = (top_a.min(top_b), top_a.max(top_b));
        // Y el ALTO es el que tiene la pared cortada de más; el bajo ya cierra con su propia losa.
        let high = if top_a >= top_b { a } else { b };
        // Por debajo de una losa no hay franja que tapar: la del techo bajo ya la cubre.
        if high_top <= low_top + SLAB_THICKNESS_CM {
            continue;
        }
        let Some(side) = wall_side(high, link.at_x_cm, link.at_z_cm) else {
            continue;
        };
        let half = link.width_cm / 2 + APRON_JAMB_CM;
        let (x_cm, z_cm, size_x_cm, size_z_cm) = door_band(side, link.at_x_cm, link.at_z_cm, half);
        out.push(Wg3Solid {
            x_cm,
            z_cm,
            size_x_cm,
            size_z_cm,
            // Desde la cara INFERIOR de la losa baja: entre `low_top` y `low_top + SLAB` la losa sólo
            // cubre su mitad de la línea, y la otra mitad es la banda de pared que falta.
            bottom_y_cm: low_top,
            top_y_cm: high_top,
            // Con el aspecto de la sala ALTA, que es de quien es la pared que se está completando.
            style: style_of(high.role),
            yaw_deg: 0,
            shape: SHAPE_BOX,
        });
    }
    out
}

/// La banda de pared de un vano, vista desde el espacio que tiene esa pared en su lado `side`:
/// `(x, z, ancho, fondo)` en centímetros. Cae a un lado u otro de la línea del plan según por qué
/// cara la toque el espacio, porque las paredes de un tramo van hacia DENTRO de su huella. Es la
/// misma caja para el faldón y para el dintel: dos cálculos separados serían dos que pueden
/// desviarse un grosor de pared, y ahí es donde salen las líneas de luz.
fn door_band(side: u8, at_x_cm: i32, at_z_cm: i32, half: i32) -> (i32, i32, i32, i32) {
    match side % 4 {
        0 => (at_x_cm - half, at_z_cm - WALL_T_CM, 2 * half, WALL_T_CM),
        1 => (at_x_cm - WALL_T_CM, at_z_cm - half, WALL_T_CM, 2 * half),
        2 => (at_x_cm - half, at_z_cm, 2 * half, WALL_T_CM),
        _ => (at_x_cm, at_z_cm - half, WALL_T_CM, 2 * half),
    }
}

/// Altura del paso de una puerta con dintel, en centímetros. El mismo número que
/// [`PIECE_DOOR_CLEAR_CM`]: por encima queda pared, que es el dintel de toda la vida.
const DOOR_LINTEL_CLEAR_CM: i32 = 240;
/// ADR-105 enm. 17 — la proporción de puertas con dintel vive en `Knobs::lintel`.
/// Sal del sorteo del dintel, por la posición de la puerta.
const SALT_LINTEL: u32 = 0xB1_11_A0_04;
/// ADR-125 enm. 1 — línea de arranque del ARCO LISO de una puerta, sobre el suelo. Diez por
/// encima del cuerpo (1,80): el intradós baja hasta aquí en las jambas y ninguna puerta se cierra
/// al paso. Con el paso a 2,40 y la clave de 10, la flecha es de 40: rebajado, no de medio punto
/// —el de medio punto sobre 1,20 pondría la clave a 2,60 y obligaría a subir todos los vanos.
const ARCH_DOOR_SPRING_CM: i32 = 190;

/// ADR-105 enmienda 6 — **EL DINTEL: la pared que hay sobre una puerta.**
///
/// Una boca de tramo se corta de suelo a techo (`segment::emit_side`), así que toda puerta generada
/// es una rendija de 3,20 m: se lee como un corte en la pared, no como una puerta. STATE 2026-09-03
/// §0c lo midió: «metros de pared que no van de suelo a techo: 0,0». El faldón de arriba
/// ([`ceiling_aprons`]) cierra sólo la franja entre dos techos distintos; esto cierra desde el paso
/// (2,40) hasta el techo más bajo de los dos, y en los DOS lados, porque las dos paredes están
/// cortadas.
///
/// Mismo macizo, misma banda ([`door_band`]) y mismas exclusiones que el faldón: ni piezas del
/// catálogo (su vano ya va capado en `carve_for_piece`), ni puertas de junta, ni bocas de ruta, ni
/// atrios.
fn door_lintels(plan: &RegionPlan, by_piece: &[bool], seed: i32) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    for link in &plan.links {
        if link.kind == LinkKind::Route {
            continue;
        }
        let (a, b) = (&plan.spaces[link.a], &plan.spaces[link.b]);
        if !a.role.is_built() || !b.role.is_built() || by_piece[link.a] || by_piece[link.b] {
            continue;
        }
        if is_atrium(a) || is_atrium(b) {
            continue;
        }
        let (mx, mz) = (
            link.at_x_cm as f32 / CM_PER_M,
            link.at_z_cm as f32 / CM_PER_M,
        );
        let mut st = super::hash::stream_at(seed, mx, mz, SALT_LINTEL);
        // ADR-105 enm. 17 — por carácter (del lado `a`, como el arco): sin dintel, la boca es un
        // HUECO hasta el techo, que es la puerta del Nivel 0.
        if st.next01() >= knobs_of(seed, a).lintel {
            continue;
        }
        // Con arco no hay dintel plano: las dos cajas compartirían la cara de la pared y pelearían
        // por el mismo plano.
        if has_arch(seed, link) {
            continue;
        }
        let low_top = (a.floor_y_cm + clear_height_cm(a)).min(b.floor_y_cm + clear_height_cm(b));
        let half = link.width_cm / 2 + APRON_JAMB_CM;
        // ADR-125 enm. 1 — bajo el dintel, un ARCO LISO. Sorteo después del dintel, para que las
        // puertas que ya lo tenían lo conserven; por el carácter del lado `a` para que los dos lados
        // de la misma puerta digan lo mismo. Sólo en puertas normales: las bocas anchas llevan el
        // escalonado de ladrillo (enm. 7), que es otro sabor y se queda.
        let arch = link.width_cm < ARCH_MIN_WIDTH_CM && st.next01() < knobs_of(seed, a).arch_door;
        for s in [a, b] {
            let bottom = s.floor_y_cm + DOOR_LINTEL_CLEAR_CM;
            // Un dintel de menos de dos celdas no es un dintel: es un alféizar al revés.
            if low_top - bottom < 20 {
                continue;
            }
            let Some(side) = wall_side(s, link.at_x_cm, link.at_z_cm) else {
                continue;
            };
            let (x_cm, z_cm, size_x_cm, size_z_cm) =
                door_band(side, link.at_x_cm, link.at_z_cm, half);
            out.push(Wg3Solid {
                x_cm,
                z_cm,
                size_x_cm,
                size_z_cm,
                bottom_y_cm: bottom,
                top_y_cm: low_top,
                style: style_of(s.role),
                yaw_deg: 0,
                shape: SHAPE_BOX,
            });
            if arch {
                // La cuerda es la boca EXACTA, sin jambas: el arco arranca en la jamba y la pared
                // de al lado ya es maciza. Va del arranque al paso, justo bajo el dintel.
                let (x_cm, z_cm, size_x_cm, size_z_cm) =
                    door_band(side, link.at_x_cm, link.at_z_cm, link.width_cm / 2);
                out.push(Wg3Solid {
                    x_cm,
                    z_cm,
                    size_x_cm,
                    size_z_cm,
                    bottom_y_cm: s.floor_y_cm + ARCH_DOOR_SPRING_CM,
                    top_y_cm: bottom,
                    style: style_of(s.role),
                    yaw_deg: 0,
                    shape: SHAPE_ARCH,
                });
            }
        }
        // ADR-125 enm. 2 — EL MARCO, una vez por puerta y a caballo de las DOS paredes. Es lo que
        // tapa la junta: cada lado es una pared distinta con su tono, y en la mocheta el cambio se
        // ve a mitad de grosor. Jambas y dintel (o arquivolta) en el tono de UN lado, decorativos
        // (sin ráster ni collider), 2 cm proud por sala y 1 cm dentro de la luz. Sólo si los dos
        // suelos coinciden: con desnivel, una jamba de suelo a dintel no tiene un suelo.
        if a.floor_y_cm == b.floor_y_cm {
            if let Some(side) = wall_side(a, link.at_x_cm, link.at_z_cm) {
                door_casing(
                    &mut out,
                    side,
                    link.at_x_cm,
                    link.at_z_cm,
                    link.width_cm,
                    a.floor_y_cm,
                    low_top,
                    arch,
                    style_of(a.role) | STYLE_DECOR_BIT,
                );
            }
        }
    }
    out
}

/// ADR-125 enm. 2 — el marco de una puerta: dos jambas y una cabeza, decorativos.
///
/// Todo centrado en la línea de la puerta (`at`), que es el plano entre las dos paredes: la caja
/// cubre `WALL_T_CM` a cada lado más `CASING_PROUD_CM` proud por sala. Las jambas entran
/// `CASING_IN_CM` en la luz y cubren `CASING_W_CM` sobre la pared; suben hasta la cabeza. La cabeza
/// es un dintel plano de `CASING_W_CM` de alto bajo el paso, o —con arco— la ARQUIVOLTA: un
/// `SHAPE_ARCH` decorativo cuya curva exterior es la del arco crecida `CASING_W_CM` en cuerda y
/// flecha; el cliente le resta `CASING_W_CM + CASING_IN_CM` para la interior.
#[allow(clippy::too_many_arguments)]
fn door_casing(
    out: &mut Vec<Wg3Solid>,
    side: u8,
    at_x_cm: i32,
    at_z_cm: i32,
    width_cm: i32,
    floor_y_cm: i32,
    low_top: i32,
    arch: bool,
    style: u8,
) {
    let along_x = side.is_multiple_of(2);
    let depth = 2 * (WALL_T_CM + CASING_PROUD_CM);
    let half = width_cm / 2;
    // Cabeza: el paso (dintel) o el arranque del arco; la jamba llega hasta ahí más el solape.
    let head_y = floor_y_cm
        + if arch {
            ARCH_DOOR_SPRING_CM
        } else {
            DOOR_LINTEL_CLEAR_CM
        };
    if head_y + CASING_W_CM > low_top {
        return;
    }
    // Caja centrada en la línea de la puerta, `u` a lo largo de la pared, `v` a través.
    let boxed = |u0: i32, u1: i32, y0: i32, y1: i32, shape: u8| -> Wg3Solid {
        let (x_cm, z_cm, size_x_cm, size_z_cm) = if along_x {
            (at_x_cm + u0, at_z_cm - depth / 2, u1 - u0, depth)
        } else {
            (at_x_cm - depth / 2, at_z_cm + u0, depth, u1 - u0)
        };
        Wg3Solid {
            x_cm,
            z_cm,
            size_x_cm,
            size_z_cm,
            bottom_y_cm: y0,
            top_y_cm: y1,
            style,
            yaw_deg: 0,
            shape,
        }
    };
    let inner = half - CASING_IN_CM;
    let outer = half + CASING_W_CM;
    // Jambas, de suelo a cabeza (solapando `CASING_IN_CM` con ella).
    out.push(boxed(
        -outer,
        -inner,
        floor_y_cm,
        head_y + CASING_IN_CM,
        SHAPE_BOX,
    ));
    out.push(boxed(
        inner,
        outer,
        floor_y_cm,
        head_y + CASING_IN_CM,
        SHAPE_BOX,
    ));
    if arch {
        // Arquivolta: cuerda y flecha exteriores = las del arco más el ancho del marco. La flecha
        // del arco es del arranque a la clave (paso − clave).
        let rise = DOOR_LINTEL_CLEAR_CM - ARCH_DOOR_SPRING_CM - super::segment::ARCH_KEY_CM;
        out.push(boxed(
            -outer,
            outer,
            head_y,
            head_y + rise + CASING_W_CM,
            SHAPE_ARCH,
        ));
    } else {
        out.push(boxed(
            -outer,
            outer,
            head_y - CASING_IN_CM,
            head_y + CASING_W_CM,
            SHAPE_BOX,
        ));
    }
}

/// Línea de arranque de un arco, sobre el suelo. Por encima de la cabeza (1,80 + escalón), así que
/// el paso a la altura de los hombros conserva el ancho entero de la boca.
const ARCH_SPRING_CM: i32 = 200;
/// Altura de cada hilada del arco. Media celda: el arco se lee escalonado, como de ladrillo, y la
/// media celda es lo más fino que el ráster estampa sin cambiar de significado en vertical (el
/// ráster es conservador al centímetro en Y, no a la celda).
pub(super) const ARCH_BAND_CM: i32 = 25;
/// Ancho mínimo de boca para un arco. Sólo las bocas anchas (`WIDE_DOORWAY_CM`): un arco sobre una
/// puerta de 2,40 son dos hiladas y se lee como un dintel torcido.
const ARCH_MIN_WIDTH_CM: i32 = 300;
/// Qué proporción de las bocas anchas llevan arco.
const ARCH_CHANCE: f32 = 0.50;
/// Sal del sorteo del arco, por la posición de la boca.
const SALT_ARCH: u32 = 0xB1_11_A0_06;

/// ¿Lleva arco esta boca? Lo preguntan el arco y el dintel: los dos sobre la misma banda de pared,
/// y por eso el que decide es uno.
fn has_arch(seed: i32, link: &super::plan::PlannedLink) -> bool {
    if link.kind == LinkKind::Route || link.width_cm < ARCH_MIN_WIDTH_CM {
        return false;
    }
    let mut st = super::hash::stream_at(
        seed,
        link.at_x_cm as f32 / CM_PER_M,
        link.at_z_cm as f32 / CM_PER_M,
        SALT_ARCH,
    );
    st.next01() < ARCH_CHANCE
}

/// ADR-105 enmienda 7 — **EL ARCO ESCALONADO: una boca ancha que se cierra por arriba en hiladas.**
///
/// No hay volumen curvo en WG3 y no lo va a haber (la chuleta son cajas, y el ráster estampa cajas).
/// Un arco es lo que se puede decir con cajas: hiladas de `ARCH_BAND_CM` desde la línea de arranque
/// hasta el techo, cada una metiéndose hacia el centro lo que dicta la elipse que va del ancho de la
/// boca en el arranque a cero en la clave. Se lee como arco de ladrillo, no como curva lisa, y eso
/// es lo que hay: la «media luna» que pidió Joel el 2026-09-04, a la resolución del sistema.
///
/// Son macizos en la banda del vano ([`door_band`]), en los DOS lados como el dintel, y por eso una
/// boca con arco no lleva dintel: compartirían plano. Inmunes a los vanos (ADR-105 D2), así que la
/// propia boca no se los lleva.
fn door_arches(plan: &RegionPlan, by_piece: &[bool], seed: i32) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    for link in &plan.links {
        if !has_arch(seed, link) {
            continue;
        }
        let (a, b) = (&plan.spaces[link.a], &plan.spaces[link.b]);
        if !a.role.is_built() || !b.role.is_built() || by_piece[link.a] || by_piece[link.b] {
            continue;
        }
        if is_atrium(a) || is_atrium(b) {
            continue;
        }
        let low_top = (a.floor_y_cm + clear_height_cm(a)).min(b.floor_y_cm + clear_height_cm(b));
        let radius = link.width_cm / 2;
        let half = radius + APRON_JAMB_CM;
        for s in [a, b] {
            let spring = s.floor_y_cm + ARCH_SPRING_CM;
            let rise = low_top - spring;
            if rise < 2 * ARCH_BAND_CM {
                continue;
            }
            let Some(side) = wall_side(s, link.at_x_cm, link.at_z_cm) else {
                continue;
            };
            let (x_cm, z_cm, size_x_cm, size_z_cm) =
                door_band(side, link.at_x_cm, link.at_z_cm, half);
            // Hiladas desde la CLAVE hacia abajo, todas de la misma altura: la que no cabe entera
            // contra el arranque se tira. Así el test las reconoce por la forma.
            let mut y1 = low_top;
            while y1 - ARCH_BAND_CM >= spring {
                let y0 = y1 - ARCH_BAND_CM;
                // Media anchura de la luz a la altura de la cara SUPERIOR de la hilada: la elipse
                // se evalúa por arriba, así que cada hilada tapa un poco más de lo justo y el arco
                // dibujado nunca deja ver por encima del estampado.
                let t = (y1 - spring) as f32 / rise as f32;
                let open = (radius as f32 * (1.0 - t * t).max(0.0).sqrt()) as i32;
                let fill = half - open;
                // Nunca menos de un grosor de pared: por debajo la hilada es una astilla, y además
                // deja de tener la forma por la que los tests reconocen lo que cuelga sobre una boca.
                if fill >= WALL_T_CM {
                    let along_x = side.is_multiple_of(2);
                    let (len_x, len_z) = if along_x {
                        (fill, size_z_cm)
                    } else {
                        (size_x_cm, fill)
                    };
                    let (bx, bz) = if along_x {
                        (x_cm + size_x_cm - fill, z_cm)
                    } else {
                        (x_cm, z_cm + size_z_cm - fill)
                    };
                    for (px, pz) in [(x_cm, z_cm), (bx, bz)] {
                        out.push(Wg3Solid {
                            x_cm: px,
                            z_cm: pz,
                            size_x_cm: len_x,
                            size_z_cm: len_z,
                            bottom_y_cm: y0,
                            top_y_cm: y1,
                            style: style_of(s.role),
                            yaw_deg: 0,
                            shape: SHAPE_BOX,
                        });
                    }
                }
                y1 = y0;
            }
        }
    }
    out
}

/// Alféizar de una ventana interior, sobre el suelo. A la altura de la cadera: se ve por encima, no
/// se pasa por debajo.
pub(super) const WINDOW_SILL_CM: i32 = 110;
/// Dintel de una ventana interior. **Noventa centímetros de banda, y es lo que la hace ventana en
/// el servidor**: `headroom_above_floor` mide 90 en esa columna, muy por debajo de los 180 del
/// cuerpo, así que ninguna criatura la planifica como paso, y `blocked_standing_at` choca con el
/// antepecho. Ver por ella sí: `line_of_sight` va a 1,40 (ojos menos `EYE_DROP_M`).
const WINDOW_HEAD_CM: i32 = 200;
/// Ancho de una ventana, mínimo y máximo, redondeado a celda.
const WINDOW_WIDTH_CM: (i32, i32) = (100, 250);
/// Qué proporción de las paredes ciegas entre dos espacios llevan ventana.
const WINDOW_CHANCE: f32 = 0.35;
/// Y cuántas llevan rendijas: cortes de tres centímetros por los que se ve la sala de al lado.
const SLIT_CHANCE: f32 = 0.20;
const SLIT_MAX: i32 = 3;
/// Ancho de una rendija. **Por debajo de la celda del ráster a propósito, y con la banda vertical
/// pensada para eso.** `carve_box` abre la celda cuyo CENTRO cae dentro, así que una rendija puede
/// abrir en el servidor una celda entera o ninguna; con la banda de 40 a 200 el hueco libre de esa
/// columna son 160 cm, por debajo del cuerpo, y no es paso en ningún caso. En el cliente el corte
/// es exacto: `Wg3Carving` parte la pared en cajas de 3 cm de luz.
const SLIT_W_CM: i32 = 3;
pub(super) const SLIT_BOTTOM_CM: i32 = 40;
const SLIT_TOP_CM: i32 = 200;
/// Cuánto se aleja un hueco de los extremos del solape de pared. **Más que media puerta más la
/// profundidad de su vano (120 + 50)**: una puerta en la pared PERPENDICULAR, junto al rincón,
/// excava una caja que se lleva también el arranque de esta pared, y una ventana ahí nace sin
/// antepecho. Con 60 salió en (−128,51, 339,17) al subir la frecuencia de ventanas por zona.
const OPENING_JAMB_CM: i32 = 180;
/// Sal del sorteo de huecos en pared ciega, por el centro del solape.
const SALT_WINDOW: u32 = 0xB1_11_A0_05;

/// ¿Es este vano una ventana interior? Por la forma: 90 cm de banda vertical.
pub(super) fn is_window(c: &Wg3Carve) -> bool {
    c.top_y_cm - c.bottom_y_cm == WINDOW_HEAD_CM - WINDOW_SILL_CM
}

/// ¿Y una rendija? Tres centímetros de luz.
pub(super) fn is_slit(c: &Wg3Carve) -> bool {
    c.size_x_cm.min(c.size_z_cm) == SLIT_W_CM
}

/// ADR-105 enm. 12 — **ventanas en serie**: con esta probabilidad, la ventana de una pared ciega se
/// repite a lo largo del solape, a paso fijo. La fila de ventanas de oficina a un pasillo.
const WINDOW_SERIES_CHANCE: f32 = 0.40;
const WINDOW_SERIES_MAX: i32 = 4;
const WINDOW_SERIES_GAP_CM: i32 = 100;
/// **La REJILLA**: barrotes en una ventana. Macizos de `GRILLE_BAR_CM` de ancho cada
/// `GRILLE_PITCH_CM`, del alféizar al dintel, cubriendo las dos paredes. En el servidor un barrote
/// maciza su celda, así que una ventana con reja se ve a través en el cliente y es pared para el
/// ráster: «se ve y no se pasa», por el otro camino.
const GRILLE_CHANCE: f32 = 0.35;
const GRILLE_BAR_CM: i32 = 8;
const GRILLE_PITCH_CM: i32 = 25;
/// **La HORNACINA**: un nicho de `NICHE_DEPTH_CM` en una sola pared, que NO la atraviesa. Deja
/// cinco centímetros de fondo, más que el `MinSliver` del cliente; en el servidor una caja de diez
/// centímetros casi nunca contiene el centro de una celda, y cuando lo contiene abre una banda de
/// 60 a 180 que no es paso.
const NICHE_CHANCE: f32 = 0.30;
const NICHE_W_CM: i32 = 60;
const NICHE_DEPTH_CM: i32 = 10;
const NICHE_BOTTOM_CM: i32 = 60;
const NICHE_TOP_CM: i32 = 180;
/// **El PARTELUZ**: un pilar de `MULLION_CM` en medio de una boca ancha, que la parte en dos
/// puertas. Treinta y cinco y no 30: ninguna otra forma del sistema mide eso.
const MULLION_CM: i32 = 35;
const MULLION_CHANCE: f32 = 0.40;
const MULLION_MIN_WIDTH_CM: i32 = 400;
const SALT_MULLION: u32 = 0xB1_11_A0_0B;

/// ¿Es este macizo un barrote de rejilla? Por la forma: 8 de ancho.
pub(super) fn is_grille_bar(s: &Wg3Solid) -> bool {
    s.size_x_cm.min(s.size_z_cm) == GRILLE_BAR_CM
}

/// ADR-105 enm. 12 — el parteluz: en las bocas de ≥ `MULLION_MIN_WIDTH_CM` sin arco, un pilar de
/// `MULLION_CM` en el centro, de suelo al techo más bajo, cubriendo las dos paredes.
fn door_mullions(plan: &RegionPlan, by_piece: &[bool], seed: i32) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    for link in &plan.links {
        if link.kind == LinkKind::Route || link.width_cm < MULLION_MIN_WIDTH_CM {
            continue;
        }
        let (a, b) = (&plan.spaces[link.a], &plan.spaces[link.b]);
        if !a.role.is_built() || !b.role.is_built() || by_piece[link.a] || by_piece[link.b] {
            continue;
        }
        if is_atrium(a) || is_atrium(b) || has_arch(seed, link) {
            continue;
        }
        let mut st = super::hash::stream_at(
            seed,
            link.at_x_cm as f32 / CM_PER_M,
            link.at_z_cm as f32 / CM_PER_M,
            SALT_MULLION,
        );
        if st.next01() >= MULLION_CHANCE {
            continue;
        }
        let low_top = (a.floor_y_cm + clear_height_cm(a)).min(b.floor_y_cm + clear_height_cm(b));
        let floor = a.floor_y_cm.max(b.floor_y_cm);
        let h = MULLION_CM / 2;
        out.push(Wg3Solid {
            x_cm: link.at_x_cm - h,
            z_cm: link.at_z_cm - h,
            size_x_cm: MULLION_CM,
            size_z_cm: MULLION_CM,
            bottom_y_cm: floor,
            top_y_cm: low_top,
            style: style_of(a.role),
            yaw_deg: 0,
            shape: SHAPE_BOX,
        });
    }
    out
}

/// ADR-105 enmienda 6 (segunda mitad) — **VENTANAS INTERIORES Y RENDIJAS en las paredes ciegas**, y
/// desde la enmienda 12 también en serie, con rejilla, y hornacinas. Devuelve los vanos y los
/// barrotes (macizos).
///
/// Dos espacios vecinos sin puerta entre ellos comparten una pared que hoy es ciega de suelo a techo.
/// Backrooms está lleno de paredes por las que se ve y no se pasa: ventanas de oficina a un pasillo,
/// tabiques con un corte por el que se adivina otra sala. Las dos salen del mismo canal que abre las
/// puertas —[`Wg3Carve`] con su banda vertical, libre desde ADR-101 y nunca usada—, así que no hay
/// wire nuevo y el cliente ya sabe cortar.
///
/// # Lo que NO se toca
/// - Paredes con puerta (el enlace ya la abre), huellas compuestas (la envolvente ofrece paredes
///   que en el rincón de la L no existen), escaleras, hundidos, atrios, piezas del catálogo y dos
///   espacios a distinta cota.
/// - Los macizos: un vano no resta de un macizo (ADR-105 D2), así que una ventana nunca abre una
///   isla ni un dintel.
fn blind_wall_openings(
    plan: &RegionPlan,
    by_piece: &[bool],
    seed: i32,
) -> (Vec<Wg3Carve>, Vec<Wg3Solid>) {
    let mut out = Vec::new();
    let mut bars = Vec::new();
    let depth = (CARVE_DEPTH_M * CM_PER_M) as i32;
    let n = plan.spaces.len();
    for i in 0..n {
        for j in (i + 1)..n {
            let (a, b) = (&plan.spaces[i], &plan.spaces[j]);
            if !a.role.is_built() || !b.role.is_built() || by_piece[i] || by_piece[j] {
                continue;
            }
            if a.is_composite()
                || b.is_composite()
                || a.role == SpaceRole::Stair
                || b.role == SpaceRole::Stair
                || a.rise_cm != 0
                || b.rise_cm != 0
                || is_atrium(a)
                || is_atrium(b)
                || a.floor_y_cm != b.floor_y_cm
            {
                continue;
            }
            if plan
                .links
                .iter()
                .any(|l| (l.a == i && l.b == j) || (l.a == j && l.b == i))
            {
                continue;
            }
            let Some((_, x, z)) = super::plan::rects_share_wall(a.rect, b.rect) else {
                continue;
            };
            let vertical = (a.rect.max_x_cm - b.rect.min_x_cm).abs() <= 1
                || (b.rect.max_x_cm - a.rect.min_x_cm).abs() <= 1;
            let (lo, hi) = if vertical {
                (
                    a.rect.min_z_cm.max(b.rect.min_z_cm),
                    a.rect.max_z_cm.min(b.rect.max_z_cm),
                )
            } else {
                (
                    a.rect.min_x_cm.max(b.rect.min_x_cm),
                    a.rect.max_x_cm.min(b.rect.max_x_cm),
                )
            };
            let mut st =
                super::hash::stream_at(seed, x as f32 / CM_PER_M, z as f32 / CM_PER_M, SALT_WINDOW);
            let floor = a.floor_y_cm;
            // Enm. 14 — el carácter de `a` manda sobre la pared que comparten.
            let k = knobs_of(seed, a);

            if st.next01() < k.window {
                let span = (WINDOW_WIDTH_CM.1 - WINDOW_WIDTH_CM.0) as f32;
                let w = (WINDOW_WIDTH_CM.0 + (st.next01() * span) as i32) / 50 * 50;
                let room = hi - lo - 2 * OPENING_JAMB_CM - w;
                if room >= 0 {
                    let at = lo + OPENING_JAMB_CM + w / 2 + (st.next01() * room as f32) as i32;
                    // Enm. 12 — en serie: la misma ventana repetida a paso fijo desde `at` hacia el
                    // final del solape, las que quepan hasta `WINDOW_SERIES_MAX`.
                    let series = st.next01() < k.series;
                    let grille = st.next01() < k.grille;
                    let count = if series { WINDOW_SERIES_MAX } else { 1 };
                    let pitch = w + WINDOW_SERIES_GAP_CM;
                    for k in 0..count {
                        let c = at + k * pitch;
                        if c + w / 2 > hi - OPENING_JAMB_CM {
                            break;
                        }
                        out.push(carve_across(
                            vertical,
                            x,
                            z,
                            c,
                            w,
                            depth,
                            floor + WINDOW_SILL_CM,
                            floor + WINDOW_HEAD_CM,
                        ));
                        if grille {
                            // Barrotes cada `GRILLE_PITCH_CM`, cubriendo las dos paredes.
                            let mut p = c - w / 2 + GRILLE_PITCH_CM;
                            while p + GRILLE_BAR_CM < c + w / 2 {
                                let (bx, bz, sx, sz) = if vertical {
                                    (x - WALL_T_CM, p, 2 * WALL_T_CM, GRILLE_BAR_CM)
                                } else {
                                    (p, z - WALL_T_CM, GRILLE_BAR_CM, 2 * WALL_T_CM)
                                };
                                bars.push(Wg3Solid {
                                    x_cm: bx,
                                    z_cm: bz,
                                    size_x_cm: sx,
                                    size_z_cm: sz,
                                    bottom_y_cm: floor + WINDOW_SILL_CM,
                                    top_y_cm: floor + WINDOW_HEAD_CM,
                                    style: style_of(a.role),
                                    yaw_deg: 0,
                                    shape: SHAPE_BOX,
                                });
                                p += GRILLE_PITCH_CM;
                            }
                        }
                    }
                }
            }
            // Enm. 12 — hornacinas en la pared de `a`, que NO atraviesan: diez centímetros desde su
            // cara interior. ¿A qué lado de la línea está `a`? Su muro va hacia dentro de su huella.
            if st.next01() < k.niche {
                let a_neg = if vertical {
                    (a.rect.max_x_cm - x).abs() <= 1
                } else {
                    (a.rect.max_z_cm - z).abs() <= 1
                };
                let k = 1 + (st.next01() * 2.0) as i32;
                let room = hi - lo - 2 * OPENING_JAMB_CM - NICHE_W_CM;
                for _ in 0..k {
                    if room <= 0 {
                        break;
                    }
                    let at =
                        lo + OPENING_JAMB_CM + NICHE_W_CM / 2 + (st.next01() * room as f32) as i32;
                    // Desde la cara interior del muro de `a` (a 15 de la línea) diez centímetros
                    // hacia la línea: quedan cinco de fondo.
                    let (near, far) = if a_neg {
                        (
                            x_or_z(vertical, x, z) - WALL_T_CM,
                            x_or_z(vertical, x, z) - WALL_T_CM + NICHE_DEPTH_CM,
                        )
                    } else {
                        (
                            x_or_z(vertical, x, z) + WALL_T_CM - NICHE_DEPTH_CM,
                            x_or_z(vertical, x, z) + WALL_T_CM,
                        )
                    };
                    out.push(if vertical {
                        Wg3Carve {
                            x_cm: near,
                            z_cm: at - NICHE_W_CM / 2,
                            size_x_cm: far - near,
                            size_z_cm: NICHE_W_CM,
                            bottom_y_cm: floor + NICHE_BOTTOM_CM,
                            top_y_cm: floor + NICHE_TOP_CM,
                        }
                    } else {
                        Wg3Carve {
                            x_cm: at - NICHE_W_CM / 2,
                            z_cm: near,
                            size_x_cm: NICHE_W_CM,
                            size_z_cm: far - near,
                            bottom_y_cm: floor + NICHE_BOTTOM_CM,
                            top_y_cm: floor + NICHE_TOP_CM,
                        }
                    });
                }
            }
            if st.next01() < k.slit {
                let count = 1 + (st.next01() * SLIT_MAX as f32) as i32;
                let room = hi - lo - 2 * OPENING_JAMB_CM;
                for _ in 0..count.min(SLIT_MAX) {
                    if room <= 0 {
                        break;
                    }
                    let at = lo + OPENING_JAMB_CM + (st.next01() * room as f32) as i32;
                    out.push(carve_across(
                        vertical,
                        x,
                        z,
                        at,
                        SLIT_W_CM,
                        depth,
                        floor + SLIT_BOTTOM_CM,
                        floor + SLIT_TOP_CM,
                    ));
                }
            }
        }
    }
    (out, bars)
}

/// La coordenada de la línea de una pared compartida: `x` si corre a lo largo de Z, `z` si no.
fn x_or_z(vertical: bool, x: i32, z: i32) -> i32 {
    if vertical {
        x
    } else {
        z
    }
}

/// Un vano que atraviesa una pared compartida: `vertical` es que la pared corre a lo largo de Z en
/// `x`; si no, a lo largo de X en `z`. `at` es el centro del hueco a lo largo de la pared. Como
/// [`carve_for`], la caja cubre medio metro a cada lado de la línea para llevarse las DOS paredes
/// y la celda del ráster que las contiene.
#[allow(clippy::too_many_arguments)]
fn carve_across(
    vertical: bool,
    x: i32,
    z: i32,
    at: i32,
    width: i32,
    depth: i32,
    bottom_y_cm: i32,
    top_y_cm: i32,
) -> Wg3Carve {
    if vertical {
        Wg3Carve {
            x_cm: x - depth,
            z_cm: at - width / 2,
            size_x_cm: 2 * depth,
            size_z_cm: width,
            bottom_y_cm,
            top_y_cm,
        }
    } else {
        Wg3Carve {
            x_cm: at - width / 2,
            z_cm: z - depth,
            size_x_cm: width,
            size_z_cm: 2 * depth,
            bottom_y_cm,
            top_y_cm,
        }
    }
}

/// El hueco que hay que abrir en la pared para que una ruta enganche ahí.
fn mouth_opening(m: &Mouth) -> Wanted {
    Wanted {
        side: m.side,
        at_x_cm: (m.x * CM_PER_M).round() as i32,
        at_z_cm: (m.z * CM_PER_M).round() as i32,
        width_cm: (m.width * CM_PER_M).round() as i32,
    }
}

/// El encargo al enrutador para unir dos espacios que NO se tocan.
///
/// Las dos bocas se ponen en las caras enfrentadas, alineadas con el otro espacio todo lo que su
/// propia pared permita. **Alinear importa**: una ruta que sale por la esquina de una sala y entra por
/// la esquina opuesta de la otra necesita dos quiebros que no hacen falta, y cada quiebro es una
/// forma más que puede no caber.
///
/// `None` cuando la pared enfrentada es más corta que el vano: ahí no hay puerta que poner, y decirlo
/// aquí es mejor que dejar que el enrutador construya una ruta a una pared que no la admite.
fn route_request(plan: &RegionPlan, a: usize, b: usize, width_cm: i32) -> Option<PlannedRoute> {
    let (ra, rb) = (plan.spaces[a].rect, plan.spaces[b].rect);
    let (acx, acz) = ra.centre_m();
    let (bcx, bcz) = rb.centre_m();
    let horizontal = (bcx - acx).abs() >= (bcz - acz).abs();

    let side_a = if horizontal {
        if bcx > acx {
            1
        } else {
            3
        }
    } else if bcz > acz {
        0
    } else {
        2
    };
    let side_b = (side_a + 2) % 4;

    let from = mouth_on(plan, a, side_a, (bcx, bcz), width_cm)?;
    let to = mouth_on(plan, b, side_b, (acx, acz), width_cm)?;
    Some(PlannedRoute { from, to, a, b })
}

/// Una boca en el lado `side` del espacio, lo más cerca posible de `towards`.
fn mouth_on(
    plan: &RegionPlan,
    space: usize,
    side: u8,
    towards: (f32, f32),
    width_cm: i32,
) -> Option<Mouth> {
    let s = &plan.spaces[space];
    // **La boca va en la parte que de verdad tiene esa pared** (ADR-120 D5). Sobre una huella
    // compuesta, la envolvente ofrece una pared exterior que en el rincón de la L no existe: el
    // conector moriría contra el suelo del vecino. Se elige la parte que más se asoma por ese lado, y
    // a igualdad la más cercana a donde va la ruta. Con una sola parte es la de siempre.
    let r = *s
        .parts()
        .iter()
        .max_by_key(|p| {
            let out = match side % 4 {
                0 => p.max_z_cm,
                1 => p.max_x_cm,
                2 => -p.min_z_cm,
                _ => -p.min_x_cm,
            };
            let (c_x, c_z) = p.centre_m();
            let near = if side.is_multiple_of(2) {
                -((c_x - towards.0).abs() * CM_PER_M) as i32
            } else {
                -((c_z - towards.1).abs() * CM_PER_M) as i32
            };
            (out, near)
        })
        .expect("todo espacio tiene al menos una parte");
    let half = width_cm / 2;
    let (x_cm, z_cm) = match side % 4 {
        0 => (
            clamp_side(towards.0, r.min_x_cm, r.max_x_cm, half)?,
            r.max_z_cm,
        ),
        1 => (
            r.max_x_cm,
            clamp_side(towards.1, r.min_z_cm, r.max_z_cm, half)?,
        ),
        2 => (
            clamp_side(towards.0, r.min_x_cm, r.max_x_cm, half)?,
            r.min_z_cm,
        ),
        _ => (
            r.min_x_cm,
            clamp_side(towards.1, r.min_z_cm, r.max_z_cm, half)?,
        ),
    };
    Some(Mouth {
        // El enrutador no mira el nodo ni la boca cuando se le dice qué unir: sólo los usaba para
        // decidirlo él, y eso es justamente lo que ha dejado de hacer.
        node: space,
        socket: 0,
        x: x_cm as f32 / CM_PER_M,
        z: z_cm as f32 / CM_PER_M,
        side,
        width: width_cm as f32 / CM_PER_M,
        floor_y: s.floor_y_cm as f32 / CM_PER_M,
        clear_height: clear_height_cm(s) as f32 / CM_PER_M,
        kind: 0,
    })
}

fn clamp_side(target_m: f32, min_cm: i32, max_cm: i32, half_cm: i32) -> Option<i32> {
    if max_cm - min_cm < half_cm * 2 {
        return None;
    }
    Some(((target_m * CM_PER_M) as i32).clamp(min_cm + half_cm, max_cm - half_cm))
}

/// ¿En qué lado del espacio cae este punto? `None` si no está sobre ninguna de sus cuatro paredes.
///
/// La tolerancia es de un centímetro: los rectángulos del plan teselan la región, así que el punto de
/// una puerta cae exactamente sobre la línea que comparten dos: no hay nada que buscar, sólo que
/// reconocer.
fn wall_side(space: &PlannedSpace, x_cm: i32, z_cm: i32) -> Option<u8> {
    // Dentro del tramo del lado, o el hueco se saldría de la pared. Se comprueba con el vano mínimo
    // y no con el pedido: un hueco de 5 m centrado a 30 cm de la esquina no cabe por mucho que la
    // pared mida 20 m.
    //
    // **Y la responde el plan, no este módulo** (ADR-120 D5). El plan declara ilegal el enlace que
    // no cae en pared y el relleno lo abre: si las dos comprobaciones se escriben aparte, un día
    // dicen cosas distintas y el edificio nace con salas selladas que ningún contador ve. Ahora es
    // literalmente la misma función.
    space.wall_side_of(x_cm, z_cm, MIN_GENERATED_WIDTH_CM / 2)
}

/// La pieza del catálogo que representa este espacio, si alguna encaja.
///
/// **Encajar quiere decir LLENARLO, no caber dentro.** Se exige que la huella girada cubra el
/// rectángulo del plan salvo la tolerancia, en los dos ejes. Una pieza más pequeña «cabe» y deja
/// hueco alrededor, y ese hueco es exactamente el problema que este ADR viene a quitar.
///
/// A igualdad, gana la de menor índice: el mundo no puede depender de en qué orden se recorrió el
/// catálogo.
fn fitting_piece(space: &PlannedSpace, manifest: &Wg3Manifest) -> Option<Wg3Placement> {
    // **Una pieza es un rectángulo, así que sobre una huella compuesta se DESCARTA** (ADR-120 D6).
    // No es una limitación que haya que quitar: es la respuesta correcta. Encajar aquí quiere decir
    // LLENAR el espacio, y ninguna caja llena una L — forzarla sería devolverle al catálogo el poder
    // de imponer cuadrícula, que es justo lo que este trabajo viene a quitarle.
    if space.is_composite() {
        return None;
    }
    let want_x = space.rect.width_cm();
    let want_z = space.rect.depth_cm();
    // **La pieza tiene que caber también en ALTURA** (auditoría 2026-09-02). Una pieza de 4,50 m
    // bajo una planta de 3,32 plantaba su techo y sus paredes 1,18 m por encima del suelo de la
    // sala de arriba: la sala de arriba se dibujaba, tenía su suelo, y no cabía nadie de pie en
    // el 60-90 % de sus celdas. Medido en tres semillas del barrido, siempre debajo de una
    // `room_pillars` o una `hall_large`. El tope es el mismo que respeta un tramo generado.
    let max_h_cm = if space.max_clear_cm > 0 {
        space.max_clear_cm
    } else {
        i32::MAX
    };
    // **Un ATRIO no se resuelve con una pieza** (auditoría 2026-09-02). `atrium_carves` abre el
    // atrio por arriba quitando todo lo que haya entre la planta de encima y los 6,40 m — y una
    // pieza trae su techo a su altura (3,60 en `room_core`), justo dentro de esa banda. El techo
    // desaparecía, encima había vacío intencionado, y el atrio quedaba ABIERTO AL CIELO: una nave
    // de 430 m² sin techo, con el suelo contando como azotea y cero celdas pisables. Un atrio es
    // geometría planificada y la construye el relleno a la altura que el plan pide.
    if is_atrium(space) {
        return None;
    }

    for piece in &manifest.pieces {
        // Un tapón o un callejón no representa un espacio: existe para sellar una boca.
        if piece.dead_end {
            continue;
        }
        if (piece.height_meters * CM_PER_M).round() as i32 > max_h_cm {
            continue;
        }
        // **Una pieza con PELDAÑOS no es plana**, y el plan sólo pone piezas en espacios planos
        // (auditoría 2026-09-02). `cor_ramp` mide 8 × 2,4 y cabe clavada en un corredor generado; su
        // rampa subía el suelo un metro dentro de un espacio que el plan declaraba a cota cero, y la
        // mitad de sus celdas dejaba de ser pisable a la cota de sus puertas. El plan no sabe lo que
        // hay dentro de una pieza, así que el criterio es el de la chuleta: cualquier caja `Step`.
        if piece.collision.iter().any(|b| b.kind == KIND_STEP) {
            continue;
        }
        for rotation in 0..4u8 {
            let (w, d) = footprint_cm(piece, rotation);
            // **Nunca MÁS grande que el espacio** (auditoría 2026-09-02). La tolerancia era
            // simétrica y una pieza 40 cm mayor metía su pared 40 cm dentro de la sala de al lado:
            // geometría cruzada que el ráster estampa maciza y que ningún contador ve.
            if w > want_x
                || d > want_z
                || want_x - w > PIECE_FIT_SLACK_CM
                || want_z - d > PIECE_FIT_SLACK_CM
            {
                continue;
            }
            // **Y CENTRADA**, no pegada a la esquina mínima. Pegada, el hueco entre su pared y la
            // del vecino llegaba a los 50 cm por el lado opuesto: una celda entera del ráster sin
            // suelo justo en la puerta. Centrada, sobran como mucho 25 cm por lado, que caen en una
            // celda que también toca suelo y que la cápsula del jugador no puede atravesar.
            return Some(Wg3Placement {
                piece: piece.index,
                rotation,
                origin_x_cm: space.rect.min_x_cm + (want_x - w) / 2,
                origin_z_cm: space.rect.min_z_cm + (want_z - d) / 2,
                origin_y_cm: space.floor_y_cm,
            });
        }
    }
    None
}

fn footprint_cm(piece: &Wg3Piece, rotation: u8) -> (i32, i32) {
    let (x, z) = if rotation.is_multiple_of(2) {
        (piece.size_x, piece.size_z)
    } else {
        (piece.size_z, piece.size_x)
    };
    ((x * CM_PER_M).round() as i32, (z * CM_PER_M).round() as i32)
}

/// Un espacio, teselado en tramos y con sus huecos repartidos.
///
/// **La rejilla se calcula en centímetros enteros y el último tramo se lleva el resto**, no se
/// reparte a partes iguales en coma flotante: dos tramos hermanas tienen que tocarse exactamente o
/// queda una junta de un milímetro que el ráster conservador convierte en pared.
fn emit_space(index: usize, space: &PlannedSpace, wanted: &[Wanted], out: &mut FilledRegion) {
    if space.role == SpaceRole::Stair && space.rise_cm != 0 {
        emit_stair(index, space, wanted, out);
        return;
    }
    let max_cm = (MAX_SEGMENT_M * CM_PER_M) as i32;
    // División con techo escrita a mano: `i32::div_ceil` sigue siendo inestable en el toolchain del
    // proyecto, y no se va a encender una feature de nightly por una cuenta de dos operaciones.
    //
    // El divisor deja HOLGURA sobre el tope de tramo porque los cortes se van a mover para no partir
    // puertas, y un corte movido alarga el tramo de al lado.
    //
    // **Y la holgura tiene que ser el PEOR caso, no una estimacion** (ADR-119 D3). Eran 400 cm y el
    // peor caso son 560: `shift_cuts` mueve un corte hasta `width/2 + JAMB` = 280 cm con una boca
    // ancha (`WIDE_DOORWAY_CM`), y **dos cortes contiguos pueden moverse en sentidos opuestos**, asi
    // que el tramo de en medio crece el doble. Con salas de una hoja el caso no existia —`nx` valia
    // 1 y no habia cortes que mover—; en cuanto ADR-119 D1 subio el tamano medio salieron tramos de
    // 2558 × 634 cm contra un tope de 2500, y un tramo por encima del tope rompe el reparto por
    // chunk: se dibuja en el chunk de su centro y asoma mas alla de los vecinos inmediatos, o sea
    // que un cliente con radio 1 puede no verlo entero.
    let budget = max_cm - 600;

    // **ADR-120 D4 — LA REJILLA COMÚN Y SU MÁSCARA.**
    //
    // Las líneas de rejilla son las CARAS DE LAS PARTES más los cortes que exige el tope de tramo, y
    // se emite la celda cuyo centro cae dentro de alguna parte. Tres propiedades, y las tres son la
    // razón de hacerlo así y no con vanos parciales:
    //
    // 1. Con UNA sola parte, las líneas son exactamente las de antes, en el mismo orden y con las
    //    mismas bocas: la salida es idéntica. El mundo sin deformar no se entera de este cambio.
    // 2. Las caras de celda coinciden siempre con las caras de parte, así que una celda viva y su
    //    vecina viva comparten el lado ENTERO — sigue siendo `full_side` o nada, y no hace falta
    //    ninguna matemática de vanos nueva.
    // 3. La máscara es lo único que distingue una L de un rectángulo. La geometría no cambia.
    let (mut xs, hard_x) = grid_lines(space.parts(), true, budget);
    let (mut zs, hard_z) = grid_lines(space.parts(), false, budget);

    // **LOS CORTES SE APARTAN DE LAS PUERTAS, y esto no es un refinamiento: es corrección.**
    //
    // Un hueco a caballo de la frontera entre dos tramos hermanas no lo aloja ninguna de las dos, y
    // el fallo es del tipo que no se nota: los contadores cuadran, ningún enlace sale como fallido, y
    // la sala nace sellada con su puerta dibujada en el plano. Se ve como manchas andables sueltas —
    // 22 en la primera medida de una región—, que es la fragmentación de siempre reaparecida por
    // dentro. Mover el corte cuesta dos restas y lo quita de raíz.
    //
    // **Pero una cara de parte NO se mueve** (ADR-120 D4): moverla cambiaría la forma emitida sin
    // cambiar la huella del plan, y entonces las métricas, el validador y el mundo dejarían de hablar
    // de lo mismo. Se corre sólo lo de dentro de cada tramo entre caras, que con una sola parte es
    // todo el vector y da la llamada de siempre.
    shift_between(&mut xs, &hard_x, wanted, true);
    shift_between(&mut zs, &hard_z, wanted, false);

    // La máscara: qué celdas de la rejilla son suelo de este espacio.
    let (nx, nz) = (xs.len() - 1, zs.len() - 1);
    let mut alive = vec![false; nx * nz];
    for iz in 0..nz {
        for ix in 0..nx {
            let cx = (xs[ix] + xs[ix + 1]) / 2;
            let cz = (zs[iz] + zs[iz + 1]) / 2;
            alive[iz * nx + ix] = space.parts().iter().any(|p| p.contains_point(cx, cz));
        }
    }
    let live_cells = alive.iter().filter(|&&a| a).count();

    let height = clear_height_cm(space);
    // Qué huecos ha alojado alguien. Un `false` al terminar es una puerta perdida, y hay que contarla.
    let mut placed = vec![false; wanted.len()];
    // Índices en `out.segments` de lo que emite ESTE espacio, para poder volver sobre ellos si algún
    // hueco necesita rescate.
    let mut emitted: Vec<usize> = Vec::new();

    for iz in 0..nz {
        for ix in 0..nx {
            if !alive[iz * nx + ix] {
                continue;
            }
            let (x0, x1) = (xs[ix], xs[ix + 1]);
            let (z0, z1) = (zs[iz], zs[iz + 1]);

            let mut openings = Vec::new();

            // **Las paredes INTERIORES del espacio se abren enteras.** Un espacio partido en cuatro
            // tramos tiene que seguir siendo un sitio; dejar la pared de por medio lo convertiría en
            // cuatro salas que nadie pidió, y ésa es justo la fragmentación de la que se viene.
            //
            // Con la máscara, «interior» pasa a querer decir «la celda de al lado también es suelo
            // mío»: es el rincón de la L el que deja pared, y no hay ningún caso especial que
            // escribir para que lo haga.
            if ix + 1 < nx && alive[iz * nx + ix + 1] {
                openings.push(full_side(1, z1 - z0));
            }
            if ix > 0 && alive[iz * nx + ix - 1] {
                openings.push(full_side(3, z1 - z0));
            }
            if iz + 1 < nz && alive[(iz + 1) * nx + ix] {
                openings.push(full_side(0, x1 - x0));
            }
            if iz > 0 && alive[(iz - 1) * nx + ix] {
                openings.push(full_side(2, x1 - x0));
            }

            // Y los huecos que pidió el plan, cada uno en el tramo que contiene su punto.
            for (k, w) in wanted.iter().enumerate() {
                if let Some(o) = opening_in(w, x0, z0, x1, z1) {
                    openings.push(o);
                    out.openings_built += 1;
                    placed[k] = true;
                }
            }

            // **Un espacio de UN solo tramo se rescata aquí mismo** (auditoría 2026-09-02). Si su
            // única puerta no cuadra —un cruce tan ancho como la pared y tres centímetros
            // descentrado— el tramo se saltaba, no quedaba ninguno sobre el que rescatarla, y el
            // espacio entero desaparecía: sin suelo, sin paredes, con su puerta en el plano.
            if openings.is_empty() && live_cells == 1 {
                for (k, w) in wanted.iter().enumerate() {
                    if let Some(o) = clamped_opening_in(w, x0, z0, x1, z1) {
                        openings.push(o);
                        out.openings_built += 1;
                        placed[k] = true;
                    }
                }
            }

            if openings.is_empty() {
                // Un tramo sin bocas es una caja maciza, y `Wg3Segment::problems` lo prohíbe con
                // razón. Sólo puede pasar en un espacio de un solo tramo al que el plan no le dio
                // ninguna puerta — que el plan garantiza que no ocurre, pero no se emite geometría
                // impasable ni aunque el garante sea otro módulo.
                continue;
            }

            emitted.push(out.segments.len());
            out.segments.push(Wg3Segment {
                x_cm: x0,
                z_cm: z0,
                size_x_cm: x1 - x0,
                size_z_cm: z1 - z0,
                floor_y_cm: space.floor_y_cm,
                height_cm: height,
                openings,
                style: style_of(space.role),
            });
        }
    }

    // **RESCATE: al que no cupo, se le hace sitio corriéndolo.**
    //
    // Apartar los cortes resuelve casi todo, pero no puede resolverlo siempre: dos puertas cerca la
    // una de la otra dejan al corte sin sitio adonde ir, y entonces alguna se queda a caballo. Antes
    // que perderla —una sala sellada con la puerta dibujada— se aloja en el tramo que contiene su
    // CENTRO y se corre lo justo para caber.
    //
    // El vano se mueve, y eso hay que decirlo: la puerta ya no cae donde el plan la puso. Se corre
    // como mucho media puerta, y lo que importa —que los dos lados se solapen— se conserva porque el
    // centro sigue dentro del vano del otro lado.
    for (k, w) in wanted.iter().enumerate() {
        if placed[k] {
            continue;
        }
        let mut rescued = false;
        for &si in &emitted {
            let s = &out.segments[si];
            let (x0, z0) = (s.x_cm, s.z_cm);
            let (x1, z1) = (x0 + s.size_x_cm, z0 + s.size_z_cm);
            if let Some(o) = clamped_opening_in(w, x0, z0, x1, z1) {
                out.segments[si].openings.push(o);
                out.openings_built += 1;
                rescued = true;
                break;
            }
        }
        if !rescued {
            out.openings_dropped += 1;
            out.openings_dropped_at.push((index, w.at_x_cm, w.at_z_cm));
            // Un hueco perdido es una sala sellada con la puerta dibujada en el plano, y el
            // contador no dice por que. `WG3_DROP_DEBUG=1` escupe la rejilla entera del espacio, que
            // es lo unico con lo que se puede decidir si sobra una linea o falta un rescate.
            if std::env::var("WG3_DROP_DEBUG").is_ok() {
                eprintln!(
                    "[drop] espacio {index} lado {} en ({},{}) de {} cm | partes {:?} | xs {:?} | \
                     zs {:?} | vivas {live_cells}",
                    w.side,
                    w.at_x_cm,
                    w.at_z_cm,
                    w.width_cm,
                    space.parts(),
                    xs,
                    zs,
                );
            }
        }
    }
}

/// El vano excavado que cumple un hueco del plan sobre una pieza del catálogo.
///
/// **Se excava en los DOS lados o en ninguno**: medio vano es un muro con una marca. La caja cubre el
/// grosor entero del contacto —medio metro a cada lado de la cara— porque tiene que atravesar la pared
/// (0,15 m) Y la celda del ráster (0,50), que queda maciza entera en cuanto la pared la toca.
///
/// La banda vertical NO llega al suelo: sin esa guarda el vano se lleva la losa sobre la que se anda y
/// abre un agujero por el que se cae en vez de una puerta. Es el mismo número que usa la absorción.
fn carve_for(w: &Wanted, floor_y_cm: i32, height_cm: i32) -> Wg3Carve {
    let depth = (CARVE_DEPTH_M * CM_PER_M) as i32;
    let half = w.width_cm / 2;
    // Los lados pares (N/S) tienen la pared corriendo en X, así que el vano es ancho en X y profundo
    // en Z. Los impares, al revés.
    let (sx, sz) = if w.side.is_multiple_of(2) {
        (half, depth)
    } else {
        (depth, half)
    };
    Wg3Carve {
        x_cm: w.at_x_cm - sx,
        z_cm: w.at_z_cm - sz,
        size_x_cm: sx * 2,
        size_z_cm: sz * 2,
        bottom_y_cm: floor_y_cm + CARVE_FLOOR_GUARD_CM,
        top_y_cm: floor_y_cm + height_cm,
    }
}

/// El vano de una PIEZA del catálogo: la caja de [`carve_for`] alargada para cubrir la pared de la
/// pieza —que puede estar hasta [`PIECE_FIT_SLACK_CM`] / 2 por dentro de la línea del plan— y la del
/// vecino, que está en la línea. Medio metro más allá de cada una, como siempre.
fn carve_for_piece(
    w: &Wanted,
    space: &PlannedSpace,
    origin_cm: (i32, i32),
    footprint_cm: (i32, i32),
    height_cm: i32,
) -> Wg3Carve {
    let depth = (CARVE_DEPTH_M * CM_PER_M) as i32;
    let half = w.width_cm / 2;
    let r = space.rect;
    // La línea del plan y la de la pieza, en el eje normal a la pared.
    let (plan_line, piece_line) = match w.side % 4 {
        0 => (r.max_z_cm, origin_cm.1 + footprint_cm.1),
        1 => (r.max_x_cm, origin_cm.0 + footprint_cm.0),
        2 => (r.min_z_cm, origin_cm.1),
        _ => (r.min_x_cm, origin_cm.0),
    };
    let lo = plan_line.min(piece_line) - depth;
    let hi = plan_line.max(piece_line) + depth;
    let (x_cm, z_cm, size_x_cm, size_z_cm) = if w.side.is_multiple_of(2) {
        (w.at_x_cm - half, lo, w.width_cm, hi - lo)
    } else {
        (lo, w.at_z_cm - half, hi - lo, w.width_cm)
    };
    Wg3Carve {
        x_cm,
        z_cm,
        size_x_cm,
        size_z_cm,
        bottom_y_cm: space.floor_y_cm + CARVE_FLOOR_GUARD_CM,
        // **Hasta el dintel, no hasta el techo** (auditoría 2026-09-02). La caja cubre medio metro
        // del lado del VECINO, y hasta la altura libre de la pieza se llevaba también el techo del
        // vecino cuando éste era más bajo (un servicio de 2,80 o una sala con planta encima, 3,08):
        // un agujero de 50 cm en el techo justo sobre la puerta, abierto al forjado o al cielo. Una
        // puerta mide 2,40 de paso; por encima queda la pared, que es el dintel de toda la vida.
        top_y_cm: space.floor_y_cm + height_cm.min(PIECE_DOOR_CLEAR_CM),
    }
}

/// Altura del paso de un vano excavado en una pieza, en centímetros. Por encima queda dintel.
const PIECE_DOOR_CLEAR_CM: i32 = 240;

/// **EL HUECO DEL FORJADO** (ADR-102 D5): el trozo de suelo de la planta de arriba que se lleva la
/// escalera por delante.
///
/// Productor APARTE de [`carve_for`], y esa separación es la decisión entera. Los dos vanos de vivir
/// hoy —la puerta del plan y la de la absorción— suman [`CARVE_FLOOR_GUARD_CM`] al construirse, y con
/// razón: sin esa guarda una puerta se lleva la losa sobre la que se anda y abre un agujero por el que
/// se cae. Bajar la constante para que quepa la escalera **convertiría toda puerta del mundo en un
/// agujero**. Así que la escalera trae su propio productor y la guarda no se toca.
///
/// La maquinaria de restar ya servía tal cual: `Wg3RasterBuilder::carve_box` parte el tramo en zócalo
/// y dintel sin mirar de qué es, y `Wg3Carving.Apply` hace lo mismo en el cliente. El bloqueo estaba
/// al cien por cien en los dos productores, no en la operación.
///
/// Y hay que RESTAR el grosor de losa, no igualar la cota: el suelo cuelga por debajo de su cota, así
/// que un vano que empiece exactamente en `floor_y_cm` deja la losa entera intacta y el último peldaño
/// da contra el techo.
fn carve_for_well(rect: (i32, i32, i32, i32), upper_floor_y_cm: i32) -> Wg3Carve {
    let (min_x, min_z, max_x, max_z) = rect;
    Wg3Carve {
        x_cm: min_x,
        z_cm: min_z,
        size_x_cm: max_x - min_x,
        size_z_cm: max_z - min_z,
        // Un centímetro de más por cada lado. `carve_box` deja intacto el tramo que sólo TOCA la banda
        // (`span.top_cm <= lo`), así que una banda que empiece justo en la cara de la losa no la corta.
        bottom_y_cm: upper_floor_y_cm - SLAB_THICKNESS_CM - 1,
        top_y_cm: upper_floor_y_cm + 1,
    }
}

/// Como [`opening_in`], pero corriendo el hueco lo justo para que quepa en este tramo.
///
/// Sólo lo aloja si el CENTRO cae dentro de su pared: correr un vano hasta un tramo que no lo tocaba
/// lo pondría en otro sitio del edificio, y eso ya no es rescatar una puerta, es inventarse otra.
fn clamped_opening_in(w: &Wanted, x0: i32, z0: i32, x1: i32, z1: i32) -> Option<Wg3Opening> {
    const EPS: i32 = 2;
    let (sx, sz) = (x1 - x0, z1 - z0);
    let half = w.width_cm / 2;

    let (on_wall, along, length) = match w.side % 4 {
        0 => ((z1 - w.at_z_cm).abs() <= EPS, w.at_x_cm - x0, sx),
        1 => ((x1 - w.at_x_cm).abs() <= EPS, z1 - w.at_z_cm, sz),
        2 => ((z0 - w.at_z_cm).abs() <= EPS, x1 - w.at_x_cm, sx),
        _ => ((x0 - w.at_x_cm).abs() <= EPS, w.at_z_cm - z0, sz),
    };
    if !on_wall || along < 0 || along > length || w.width_cm > length {
        return None;
    }
    Some(Wg3Opening {
        side: w.side,
        offset_cm: along.clamp(half, length - half),
        width_cm: w.width_cm,
    })
}

/// Aparta los cortes interiores de las puertas para que ninguna quede a caballo de dos tramos.
///
/// `along_x` dice si estos cortes corren en X (y por tanto pueden partir una puerta de los lados N y
/// S, que son los que corren en X). El corte se mueve al borde del hueco más la jamba, por el lado
/// más cercano, y nunca más allá de sus vecinos: un corte que adelantara a otro daría un tramo de
/// tamaño negativo.
/// ADR-120 D4 — las líneas de rejilla de un eje, y cuáles de ellas son CARAS DE PARTE.
///
/// Devuelve `(líneas, índices duros)`. Las duras son las caras: no se mueven nunca, porque son la
/// forma. Las de en medio las mete el tope de tramo y sí se pueden correr para no partir una puerta.
///
/// Con una sola parte hay exactamente dos líneas duras —los dos extremos— y el reparto de en medio es
/// el mismo `from + size * i / n` de siempre, así que sale el vector de antes.
fn grid_lines(
    parts: &[super::plan::PlanRect],
    along_x: bool,
    budget: i32,
) -> (Vec<i32>, Vec<usize>) {
    let ceil_div = |v: i32, by: i32| (v + by - 1) / by;
    let mut faces: Vec<i32> = Vec::with_capacity(parts.len() * 2);
    for p in parts {
        if along_x {
            faces.push(p.min_x_cm);
            faces.push(p.max_x_cm);
        } else {
            faces.push(p.min_z_cm);
            faces.push(p.max_z_cm);
        }
    }
    faces.sort_unstable();
    faces.dedup();

    let mut lines = vec![faces[0]];
    let mut hard = vec![0usize];
    for w in faces.windows(2) {
        let (from, size) = (w[0], w[1] - w[0]);
        let n = ceil_div(size, budget).max(1);
        for i in 1..n {
            lines.push(from + (size * i) / n);
        }
        lines.push(w[1]);
        hard.push(lines.len() - 1);
    }
    (lines, hard)
}

/// Corre los cortes de dentro de cada tramo entre caras, dejando las caras quietas.
///
/// Con una sola parte el tramo es el vector entero y esto ES [`shift_cuts`] tal cual.
fn shift_between(lines: &mut [i32], hard: &[usize], wanted: &[Wanted], along_x: bool) {
    for w in hard.windows(2) {
        shift_cuts(&mut lines[w[0]..=w[1]], wanted, along_x);
    }
}

fn shift_cuts(cuts: &mut [i32], wanted: &[Wanted], along_x: bool) {
    /// Jamba mínima entre el borde de una puerta y el corte. Por debajo, el tramo hermana empieza
    /// dentro del vano y la pared que lo forma se parte en dos.
    const JAMB_CM: i32 = 30;

    if cuts.len() < 3 {
        return;
    }
    for i in 1..cuts.len() - 1 {
        let (lo, hi) = (
            cuts[i - 1] + MIN_GENERATED_WIDTH_CM,
            cuts[i + 1] - MIN_GENERATED_WIDTH_CM,
        );
        if lo >= hi {
            continue;
        }
        for w in wanted {
            let runs_along_x = w.side % 4 == 0 || w.side % 4 == 2;
            if runs_along_x != along_x {
                continue;
            }
            let centre = if along_x { w.at_x_cm } else { w.at_z_cm };
            let reach = w.width_cm / 2 + JAMB_CM;
            if (cuts[i] - centre).abs() >= reach {
                continue;
            }
            let before = centre - reach;
            let after = centre + reach;
            let pick = if (cuts[i] - before).abs() <= (cuts[i] - after).abs() {
                before
            } else {
                after
            };
            cuts[i] = pick.clamp(lo, hi);
        }
    }
}

/// **ADR-100 enmienda 2 — LA ESCALERA: la banda emitida como peldaños.**
///
/// La banda se parte en tiras ATRAVESADA —perpendicular a su longitud—, y cada tira sube una
/// contrahuella sobre la anterior. Cruzarla sube; recorrerla a lo largo es plano.
///
/// # Tres cosas que hacen que el peldaño exista de verdad
///
/// 1. **La contrahuella es el grosor de la losa** (`STEP_RISE_CM`). El suelo de una tira cuelga por
///    debajo de su cota, así que la losa de la tira de arriba llega justo hasta la cara de la de
///    abajo y tapa el peldaño. Con cualquier otro número queda una rendija por la que se ve el vacío.
/// 2. **Entre tiras se abre la pared entera**, igual que entre dos tramos hermanas: si no, la
///    escalera son cinco armarios apilados.
/// 3. **La huella pasa de la celda del ráster.** 320 cm entre 5 peldaños son 64 cm, y la celda mide
///    50: por debajo de eso el escalón queda bajo la resolución con la que se colisiona y se anda
///    como una rampa rota.
///
/// La tira PRIMERA está a la cota de entrada y la ÚLTIMA a `floor + rise`, así que las salas de cada
/// lado enganchan cada una con la suya. Eso no es casualidad: el plan pone la cota del bloque A en la
/// banda y la del bloque B en `+ rise`, y aquí se respeta el orden.
fn emit_stair(index: usize, space: &PlannedSpace, wanted: &[Wanted], out: &mut FilledRegion) {
    let r = space.rect;
    let rise = space.rise_cm;
    // ADR-102 D4 — la contrahuella la pone el ESPACIO. Una terraza usa los 12 cm que cierran contra la
    // losa; un hueco de escalera usa 24, porque con 12 subir una planta pide 28 peldaños y ocho metros
    // de tiro. El número de aquí decidía los dos casos y arruinaba uno.
    // **CONTRAHUELLAS Y TIRAS NO SON EL MISMO NÚMERO, y confundirlas dejaba la escalera corta.**
    //
    // N contrahuellas piden N+1 tiras: la primera a la cota de entrada y la última a `floor + rise`,
    // que es lo que esta función tiene documentado desde que existe y lo que `RegionPlan::problems`
    // da por hecho al eximir a las escaleras del tope de escalón. Repartiendo `rise` entre N tiras, la
    // última se quedaba en `rise * (N-1) / N`: una terraza de 60 cm bajaba 48, y una escalera de
    // planta se quedaba a 26 cm del suelo de arriba — por debajo de los 27 que sube el jugador, o sea
    // que se subía igual y nadie se enteraba. Con una planta más alta, o una contrahuella distinta,
    // el mismo código deja un escalón imposible en el último peldaño.
    let risers = (rise.abs() / space.rise_step_cm.max(1)).max(1);
    // Y subiendo una planta, una tira MÁS: el rellano de arriba son dos, porque el vano que abre el
    // forjado es conservador y se lleva la losa de toda celda que toque. Con un rellano de una sola
    // tira, la celda que comparte con el peldaño de abajo se queda sin suelo justo donde hay que
    // pisar. Una terraza no lo necesita: no perfora nada.
    let steps = risers + 1 + i32::from(rise > 0);
    // **Y el techo del hueco es UNO, a la cota de arriba del todo.**
    //
    // Con altura constante el techo sube con cada peldaño, que es lo correcto en una terraza —se baja
    // dentro de la misma sala— y es un desastre subiendo una planta: el techo de la primera tira
    // quedaría a 3,80 m, o sea medio metro DENTRO del suelo de la planta de encima. Un hueco de
    // escalera es un pozo abierto, y su techo está donde el de la planta a la que llega.
    let clear = clear_height_cm(space);
    // Un centímetro por debajo del techo de la planta a la que llega, y no a la misma cota: la sala de
    // arriba pone su propio techo sobre el hueco, y dos losas en el mismo sitio son la misma cara
    // dibujada dos veces — z-fighting, que sí se ve en una captura.
    let ceiling_cm = space.floor_y_cm + rise.max(0) + clear - i32::from(rise > 0);
    let max_cm = (MAX_SEGMENT_M * CM_PER_M) as i32;
    let ceil_div = |v: i32, by: i32| (v + by - 1) / by;

    // Los peldaños se alejan de la puerta, así que el eje del desnivel es el PERPENDICULAR a su
    // pared: si se entra por el norte o por el sur (lados pares), se baja en Z.
    let entry = space.rise_from_side % 4;
    let across_x = !entry.is_multiple_of(2);
    // Y el sentido: entrando por el norte (0) o por el este (1) se avanza hacia el mínimo, así que la
    // tira de la puerta es la ÚLTIMA. Equivocarse aquí no da error: pone la puerta en el fondo del
    // pozo, y desde dentro parece que la sala esté al revés.
    let from_max = entry == 0 || entry == 1;
    let across_cm = if across_x { r.width_cm() } else { r.depth_cm() };
    let along_cm = if across_x { r.depth_cm() } else { r.width_cm() };
    let runs = ceil_div(along_cm, max_cm - 400).max(1);

    let edge = |i: i32, n: i32, from: i32, size: i32| -> i32 {
        if i == n {
            from + size
        } else {
            from + (size * i) / n
        }
    };

    let mut emitted: Vec<usize> = Vec::new();
    let mut placed = vec![false; wanted.len()];

    for step in 0..steps {
        // `step` cuenta desde la PUERTA. La tira 0 se queda a la cota de la puerta —que es lo que
        // hace que ningún vecino se entere del desnivel— y cada siguiente baja una contrahuella.
        // `min(risers)` para que las dos tiras del rellano compartan cota: la subida se reparte entre
        // las contrahuellas y ahí ya no queda ninguna.
        let floor = space.floor_y_cm + (rise * step.min(risers)) / risers;
        // De ahí a la tira geométrica: entrando por el máximo, la tira 0 es la última del eje.
        let slot = if from_max { steps - 1 - step } else { step };
        let (a0, a1) = (
            edge(slot, steps, 0, across_cm),
            edge(slot + 1, steps, 0, across_cm),
        );
        for run in 0..runs {
            let (l0, l1) = (
                edge(run, runs, 0, along_cm),
                edge(run + 1, runs, 0, along_cm),
            );

            let (x0, x1, z0, z1) = if across_x {
                (
                    r.min_x_cm + a0,
                    r.min_x_cm + a1,
                    r.min_z_cm + l0,
                    r.min_z_cm + l1,
                )
            } else {
                (
                    r.min_x_cm + l0,
                    r.min_x_cm + l1,
                    r.min_z_cm + a0,
                    r.min_z_cm + a1,
                )
            };

            let mut openings = Vec::new();
            // Hacia el peldaño de al lado y hacia el trozo siguiente a lo largo: pared entera. Sin
            // esto la escalera son cinco armarios apilados.
            let (lo_side, hi_side) = if across_x { (3u8, 1u8) } else { (2u8, 0u8) };
            let (run_lo, run_hi) = if across_x { (2u8, 0u8) } else { (3u8, 1u8) };
            let across_len = if across_x { z1 - z0 } else { x1 - x0 };
            let along_len = if across_x { x1 - x0 } else { z1 - z0 };
            if slot > 0 {
                openings.push(full_side(lo_side, across_len));
            }
            if slot + 1 < steps {
                openings.push(full_side(hi_side, across_len));
            }
            if run > 0 {
                openings.push(full_side(run_lo, along_len));
            }
            if run + 1 < runs {
                openings.push(full_side(run_hi, along_len));
            }

            // **EL RELLANO NO SE CONSTRUYE: es el suelo de la planta de arriba** (ADR-102 D5).
            //
            // Las dos últimas tiras reservan su sitio en el reparto del tiro y no emiten nada. El
            // suelo que hay ahí ya está —es el de la planta a la que se llega, y el vano del forjado
            // no lo toca a propósito—, así que construirlo otra vez ponía una losa encima de otra:
            // dos caras a la misma cota mirando a la misma parte, o sea z-fighting justo donde el
            // jugador sale de la escalera. Y de paso desaparecen las paredes del rellano, que era lo
            // que hacía que se subiera hasta arriba del todo para darse con ellas.
            //
            // El último peldaño que sí se construye queda a una contrahuella del suelo de arriba, que
            // es exactamente lo que es: el último peldaño.
            if rise > 0 && step >= risers {
                continue;
            }
            if rise > 0 {
                // Y por debajo del rellano hay que QUITAR el suelo de la planta de encima, o la
                // escalera sube hasta darse con él en la cabeza. El rellano no: ése es el suelo por
                // el que se sale, y perforarlo deja un agujero donde debería haber salida.
                out.carves
                    .push(carve_for_well((x0, z0, x1, z1), space.floor_y_cm + rise));
            }

            for (k, w) in wanted.iter().enumerate() {
                if let Some(o) = opening_in(w, x0, z0, x1, z1) {
                    openings.push(o);
                    out.openings_built += 1;
                    placed[k] = true;
                }
            }

            if openings.is_empty() {
                continue;
            }
            emitted.push(out.segments.len());
            out.segments.push(Wg3Segment {
                x_cm: x0,
                z_cm: z0,
                size_x_cm: x1 - x0,
                size_z_cm: z1 - z0,
                floor_y_cm: floor,
                // Todas las tiras rematan en el mismo techo, así que la de más abajo es la más alta.
                height_cm: (ceiling_cm - floor).max(clear),
                openings,
                style: style_of(space.role),
            });
        }
    }

    for (k, w) in wanted.iter().enumerate() {
        if placed[k] {
            continue;
        }
        let mut rescued = false;
        for &si in &emitted {
            let s = &out.segments[si];
            let (x0, z0) = (s.x_cm, s.z_cm);
            let (x1, z1) = (x0 + s.size_x_cm, z0 + s.size_z_cm);
            if let Some(o) = clamped_opening_in(w, x0, z0, x1, z1) {
                out.segments[si].openings.push(o);
                out.openings_built += 1;
                rescued = true;
                break;
            }
        }
        if !rescued {
            out.openings_dropped += 1;
            out.openings_dropped_at.push((index, w.at_x_cm, w.at_z_cm));
            if std::env::var("WG3_DROP_DEBUG").is_ok() {
                eprintln!(
                    "[drop-stair] espacio {index} lado {} en ({},{}) de {} cm | rect {:?}",
                    w.side, w.at_x_cm, w.at_z_cm, w.width_cm, space.rect,
                );
            }
        }
    }
}

/// Una boca que se come el lado entero: es la que hace que dos tramos hermanas sean un mismo sitio.
fn full_side(side: u8, length_cm: i32) -> Wg3Opening {
    Wg3Opening {
        side,
        offset_cm: length_cm / 2,
        width_cm: length_cm,
    }
}

/// El hueco pedido, traducido al tramo `(x0,z0)-(x1,z1)` si le toca a ésta.
///
/// El offset se mide recorriendo el perímetro EN HORARIO desde `(0, D)`, que es la parametrización de
/// `Wg3Socket` y de `Wg3Opening`. Equivocarla no da error: pone la puerta en el otro extremo de la
/// pared, y eso sólo se ve andando.
fn opening_in(w: &Wanted, x0: i32, z0: i32, x1: i32, z1: i32) -> Option<Wg3Opening> {
    const EPS: i32 = 2;
    let (sx, sz) = (x1 - x0, z1 - z0);
    let half = w.width_cm / 2;

    let (on_wall, along, length) = match w.side % 4 {
        0 => ((z1 - w.at_z_cm).abs() <= EPS, w.at_x_cm - x0, sx),
        1 => ((x1 - w.at_x_cm).abs() <= EPS, z1 - w.at_z_cm, sz),
        2 => ((z0 - w.at_z_cm).abs() <= EPS, x1 - w.at_x_cm, sx),
        _ => ((x0 - w.at_x_cm).abs() <= EPS, w.at_z_cm - z0, sz),
    };
    if !on_wall {
        return None;
    }
    // El hueco entero tiene que caber en ESTE tramo. Si cae a caballo de dos, no se parte: se deja
    // para la que lo contenga. Partirlo daría dos medias puertas, y media puerta es una pared.
    if along - half < 0 || along + half > length {
        return None;
    }
    Some(Wg3Opening {
        side: w.side,
        offset_cm: along,
        width_cm: w.width_cm,
    })
}

/// Aspecto por papel. El servidor no lo interpreta: es el gancho para que el cliente vista un
/// corredor distinto de una nave y el mundo no se lea generado.
fn style_of(role: SpaceRole) -> u8 {
    match role {
        SpaceRole::Spine => 1,
        SpaceRole::Corridor => 2,
        SpaceRole::Hall => 3,
        SpaceRole::Service | SpaceRole::Storage => 4,
        SpaceRole::DeadEnd => 5,
        // Una escalera es el único sitio del mundo del que se sale por ARRIBA, y caía en el `_ => 0`
        // de una oficina: el cliente no tenía forma de vestirla distinta y el jugador no tenía forma
        // de encontrarla. Es el número que más falta hacía de los seis.
        SpaceRole::Stair => 6,
        _ => 0,
    }
}

/// El faldón del vano: que no quede franja abierta entre dos techos de distinta cota.
#[cfg(test)]
mod apron_tests {
    use super::*;
    use crate::world::wg3::plan;

    /// **Un catálogo VACÍO, y a propósito.** Sin piezas todo espacio se resuelve con tramos
    /// generados, que es donde vive el problema: una boca de tramo no tiene dintel. Un espacio
    /// resuelto con pieza trae su techo horneado y su vano ya va capado a `PIECE_DOOR_CLEAR_CM`.
    fn no_catalogue() -> Wg3Manifest {
        Wg3Manifest {
            version: 1,
            digest: String::new(),
            pieces: Vec::new(),
        }
    }

    fn building(seed: i32) -> RegionBuilding {
        plan::plan_building(seed, (0.0, 0.0, 150.0, 150.0), &[], 4)
    }

    /// El punto que hay que tapar: el centro del vano, metido media pared hacia el lado ALTO.
    fn probe(high: &PlannedSpace, link: &plan::PlannedLink) -> Option<(i32, i32)> {
        let side = wall_side(high, link.at_x_cm, link.at_z_cm)?;
        let h = WALL_T_CM / 2;
        Some(match side % 4 {
            0 => (link.at_x_cm, link.at_z_cm - h),
            1 => (link.at_x_cm - h, link.at_z_cm),
            2 => (link.at_x_cm, link.at_z_cm + h),
            _ => (link.at_x_cm + h, link.at_z_cm),
        })
    }

    #[test]
    fn no_doorway_opens_onto_the_plenum_of_the_lower_ceiling() {
        let m = no_catalogue();
        let mut checked = 0usize;
        for seed in 1..13 {
            let b = building(seed);
            let filled = fill_building(&b, &m);
            for storey in &b.storeys {
                for link in &storey.links {
                    if link.kind == LinkKind::Route {
                        continue;
                    }
                    let (a, c) = (&storey.spaces[link.a], &storey.spaces[link.b]);
                    if !a.role.is_built() || !c.role.is_built() {
                        continue;
                    }
                    // Un ATRIO no lleva faldón, y por qué está escrito en `ceiling_aprons`: taparlo
                    // ciega el pretil de ADR-104 que va justo encima, en el borde del vacío.
                    if is_atrium(a) || is_atrium(c) {
                        continue;
                    }
                    // Un enlace que el propio relleno declaró fallido no tiene vano que tapar.
                    if filled
                        .links_failed
                        .iter()
                        .any(|&(x, y)| (x, y) == (link.a, link.b))
                    {
                        continue;
                    }
                    let top_a = a.floor_y_cm + clear_height_cm(a);
                    let top_c = c.floor_y_cm + clear_height_cm(c);
                    let (low_top, high_top) = (top_a.min(top_c), top_a.max(top_c));
                    if high_top <= low_top + SLAB_THICKNESS_CM {
                        continue;
                    }
                    let high = if top_a >= top_c { a } else { c };
                    let Some((px, pz)) = probe(high, link) else {
                        continue;
                    };
                    let closed = filled.solids.iter().any(|s| {
                        s.x_cm <= px
                            && px <= s.x_cm + s.size_x_cm
                            && s.z_cm <= pz
                            && pz <= s.z_cm + s.size_z_cm
                            && s.bottom_y_cm <= low_top
                            && s.top_y_cm >= high_top
                    });
                    assert!(
                        closed,
                        "semilla {seed}: el vano de ({}, {}) entre {} y {} deja abierto                          [{low_top}, {high_top}] y nada lo tapa",
                        link.at_x_cm,
                        link.at_z_cm,
                        a.role.name(),
                        c.role.name()
                    );
                    checked += 1;
                }
            }
        }
        // Sin esto el test pasa por no mirar nada, que es como se descubre roto el día que hace
        // falta.
        assert!(
            checked > 100,
            "sólo {checked} vanos con salto de techo: la muestra no cubre el caso"
        );
    }

    /// **Y el faldón no puede bajar del techo bajo**, o deja de ser un dintel y pasa a ser una puerta
    /// tapiada: se dibujaría el vano y no se pasaría, que es el peor fallo posible porque no sale en
    /// una captura.
    #[test]
    fn the_apron_never_dips_into_the_doorway() {
        let m = no_catalogue();
        for seed in 1..13 {
            let b = building(seed);
            let mut lowest = i32::MAX;
            for storey in &b.storeys {
                for s in &storey.spaces {
                    if s.role.is_built() {
                        lowest = lowest.min(s.floor_y_cm + clear_height_cm(s));
                    }
                }
            }
            // Los suelos de las plantas: un DINTEL (enm. 6) también mide grosor de pared y arranca
            // a `DOOR_LINTEL_CLEAR_CM` de su suelo, que está por debajo del techo más bajo. Es un
            // dintel, no un faldón que baja.
            let floors: Vec<i32> = b
                .storeys
                .iter()
                .flat_map(|st| st.spaces.iter().map(|s| s.floor_y_cm))
                .collect();
            for s in &fill_building(&b, &m).solids {
                // Sólo los faldones: son los únicos macizos de grosor exactamente de pared.
                if s.size_x_cm != WALL_T_CM && s.size_z_cm != WALL_T_CM {
                    continue;
                }
                // ADR-125 enm. 1 — el arco liso de una puerta vive DENTRO de la boca a propósito
                // (arranque a 1,90 sobre su suelo): no es un faldón que baja.
                if s.shape == SHAPE_ARCH {
                    continue;
                }
                // ADR-126 — las paredes de la cámara de un pozo miden grosor de pared y viven
                // bajo el suelo a propósito.
                if s.style == PIT_STYLE || s.style == PIT_SHAFT_STYLE {
                    continue;
                }
                if floors
                    .iter()
                    .any(|f| s.bottom_y_cm == f + DOOR_LINTEL_CLEAR_CM)
                {
                    continue;
                }
                // Y una hilada de ARCO (enm. 7) mide exactamente media celda de alto y arranca por
                // encima de la cabeza: tampoco es un faldón que baja.
                if s.top_y_cm - s.bottom_y_cm == ARCH_BAND_CM
                    && floors.iter().any(|f| s.bottom_y_cm >= f + ARCH_SPRING_CM)
                {
                    continue;
                }
                assert!(
                    s.bottom_y_cm >= lowest,
                    "semilla {seed}: un faldón arranca en {} cm, por debajo del techo más bajo                      del edificio ({lowest} cm)",
                    s.bottom_y_cm
                );
            }
        }
    }

    /// ADR-105 enm. 16 — todo medio muro bajo lleva su tabla: una decoración de
    /// `LOW_WALL_RAIL_H_CM` justo encima, que lo cubre entero con su vuelo. Y las salas «abierto»
    /// tienen medios muros de verdad, no dos por región.
    #[test]
    fn every_low_wall_wears_its_rail() {
        let m = no_catalogue();
        let mut low = 0usize;
        for seed in 1..40 {
            let b = building(seed);
            let solids = fill_building(&b, &m).solids;
            for s in &solids {
                if s.is_decoration()
                    || s.top_y_cm - s.bottom_y_cm != PARTITION_LOW_H_CM
                    || s.size_x_cm.min(s.size_z_cm) != PARTITION_T_CM
                {
                    continue;
                }
                low += 1;
                let has_rail = solids.iter().any(|r| {
                    r.is_decoration()
                        && r.bottom_y_cm == s.top_y_cm
                        && r.top_y_cm == s.top_y_cm + LOW_WALL_RAIL_H_CM
                        && r.x_cm <= s.x_cm
                        && r.z_cm <= s.z_cm
                        && r.x_cm + r.size_x_cm >= s.x_cm + s.size_x_cm
                        && r.z_cm + r.size_z_cm >= s.z_cm + s.size_z_cm
                });
                assert!(
                    has_rail,
                    "semilla {seed}: medio muro bajo en ({}, {}) sin tabla encima",
                    s.x_cm, s.z_cm
                );
            }
        }
        assert!(
            low >= 30,
            "sólo {low} medios muros bajos en 39 semillas: la muestra no cubre el caso"
        );
        println!("[medio muro] {low} medios muros bajos con tabla en 39 semillas");
    }

    /// ADR-105 enm. 16 — los listones de pared existen, son decoración de 6 cm a 90 sobre el
    /// suelo, y ninguno cruza una boca de su tramo ni una pilastra.
    #[test]
    fn wall_rails_never_cross_a_mouth_or_a_pilaster() {
        let m = no_catalogue();
        let mut rails = 0usize;
        for seed in 1..40 {
            let b = building(seed);
            let filled = fill_building(&b, &m);
            let pilasters: Vec<&Wg3Solid> =
                filled.solids.iter().filter(|s| is_pilaster(s)).collect();
            for r in &filled.solids {
                if !r.is_decoration() || r.top_y_cm - r.bottom_y_cm != WALL_RAIL_H_CM {
                    continue;
                }
                rails += 1;
                assert!(
                    r.size_x_cm.min(r.size_z_cm) == WALL_RAIL_PROUD_CM,
                    "semilla {seed}: un listón con fondo {}",
                    r.size_x_cm.min(r.size_z_cm)
                );
                // Ninguna pilastra bajo el listón.
                let hit = pilasters.iter().find(|p| {
                    p.bottom_y_cm < r.top_y_cm
                        && p.top_y_cm > r.bottom_y_cm
                        && p.x_cm < r.x_cm + r.size_x_cm
                        && p.x_cm + p.size_x_cm > r.x_cm
                        && p.z_cm < r.z_cm + r.size_z_cm
                        && p.z_cm + p.size_z_cm > r.z_cm
                });
                assert!(
                    hit.is_none(),
                    "semilla {seed}: un listón {r:?} atraviesa la pilastra {hit:?}"
                );
                // Ninguna boca de su tramo bajo el listón: se busca el tramo cuya pared lo lleva.
                let host = filled.segments.iter().find(|g| {
                    r.x_cm >= g.x_cm
                        && r.x_cm + r.size_x_cm <= g.x_cm + g.size_x_cm
                        && r.z_cm >= g.z_cm
                        && r.z_cm + r.size_z_cm <= g.z_cm + g.size_z_cm
                        && r.bottom_y_cm == g.floor_y_cm + WALL_RAIL_Y_CM
                });
                let g = host.unwrap_or_else(|| panic!("semilla {seed}: listón sin tramo"));
                for o in &g.openings {
                    let (w, d) = (g.size_x_cm as f32 / 100.0, g.size_z_cm as f32 / 100.0);
                    let (lx, lz) = super::super::placement::local_point(
                        o.side,
                        o.offset_cm as f32 / 100.0,
                        w,
                        d,
                    );
                    let (mx, mz) = (g.x_cm + (lx * 100.0) as i32, g.z_cm + (lz * 100.0) as i32);
                    let half = o.width_cm / 2 + 20;
                    let under = mx + half > r.x_cm
                        && mx - half < r.x_cm + r.size_x_cm
                        && mz + half > r.z_cm
                        && mz - half < r.z_cm + r.size_z_cm;
                    assert!(
                        !under,
                        "semilla {seed}: un listón cruza la boca del lado {} en ({mx}, {mz})",
                        o.side
                    );
                }
            }
        }
        assert!(
            rails >= 100,
            "sólo {rails} listones en 39 semillas: la muestra no cubre el caso"
        );
        println!("[listón] {rails} listones de pared en 39 semillas");
    }

    /// ADR-105 enm. 17 — los bloques gruesos existen, son de suelo a techo, y dejan paso: a
    /// `BLOCK_CLEAR_CM` de toda boca de cualquier tramo y a `BLOCK_GAP_CM` de todo otro macizo de su
    /// planta. Y hay puertas SIN dintel (huecos hasta el techo) además de las que lo llevan.
    #[test]
    fn thick_blocks_stand_free_and_some_doors_reach_the_ceiling() {
        let m = no_catalogue();
        let mut blocks = 0usize;
        for seed in 1..40 {
            let b = building(seed);
            let filled = fill_building(&b, &m);
            let solids = &filled.solids;
            for blk in solids.iter().filter(|s| is_block(s)) {
                // Un brazo de pilar en CRUZ mide como un bloque; se reconoce porque otro macizo
                // comparte su centro.
                let centre = (2 * blk.x_cm + blk.size_x_cm, 2 * blk.z_cm + blk.size_z_cm);
                if solids.iter().any(|o| {
                    !std::ptr::eq(o, blk)
                        && (2 * o.x_cm + o.size_x_cm, 2 * o.z_cm + o.size_z_cm) == centre
                        && o.bottom_y_cm == blk.bottom_y_cm
                }) {
                    continue;
                }
                blocks += 1;
                let r = rect_of(blk);
                let gap = r.shrunk(-(BLOCK_GAP_CM - 1));
                let crowd = solids.iter().find(|o| {
                    !std::ptr::eq(*o, blk)
                        && !o.is_decoration()
                        // Lo que arranca del suelo de su planta: vigas, cornisas y colgados van
                        // en el techo, se emiten después, y no estorban el paso.
                        //
                        // **Por PLANTA y no por una ventana de 50 cm.** La ventana daba por vecinos a
                        // dos macizos de plantas distintas en cuanto el techo de la de abajo se
                        // acercaba al suelo de la de arriba: con los techos subidos a 3,00-3,80, una
                        // cornisa de la planta 0 a 298 queda a 34 cm del forjado de la 1, y el test
                        // acusaba de amontonarse a dos cosas separadas por una losa.
                        && crate::world::wg3::plan::storey_of_floor_cm(o.bottom_y_cm)
                            == crate::world::wg3::plan::storey_of_floor_cm(blk.bottom_y_cm)
                        && (o.bottom_y_cm - blk.bottom_y_cm).abs() <= 50
                        && rect_of(o).overlaps(&gap)
                });
                assert!(
                    crowd.is_none(),
                    "semilla {seed}: el bloque {r:?} tiene un macizo pegado: {crowd:?}"
                );
                for g in &filled.segments {
                    if (g.floor_y_cm - blk.bottom_y_cm).abs() >= 100 {
                        continue;
                    }
                    let (w, d) = (g.size_x_cm as f32 / 100.0, g.size_z_cm as f32 / 100.0);
                    for o in &g.openings {
                        let (lx, lz) = super::super::placement::local_point(
                            o.side,
                            o.offset_cm as f32 / 100.0,
                            w,
                            d,
                        );
                        let (mx, mz) = (g.x_cm + (lx * 100.0) as i32, g.z_cm + (lz * 100.0) as i32);
                        let half = o.width_cm / 2 + BLOCK_CLEAR_CM - 5;
                        let under = mx + half > r.min_x_cm
                            && mx - half < r.max_x_cm
                            && mz + half > r.min_z_cm
                            && mz - half < r.max_z_cm;
                        assert!(
                            !under,
                            "semilla {seed}: el bloque {r:?} tapa la boca en ({mx}, {mz})"
                        );
                    }
                }
            }
        }
        assert!(
            blocks >= 60,
            "sólo {blocks} bloques en 39 semillas: la muestra no cubre el caso"
        );
        println!("[bloque] {blocks} bloques gruesos en 39 semillas");
    }

    /// La huella en el MUNDO de un macizo, con su giro aplicado.
    ///
    /// **Un macizo girado no ocupa su caja.** Una media luna viaja como su caja SIN girar —cuerda ×
    /// panza, centrada en la huella— y es el `yaw` el que la pone contra su pared; `segment.rs` y el
    /// ráster ya lo saben. Leyendo la caja cruda, una pilastra de media luna contra una pared de x
    /// mínima se declara 100 cm de ancho donde ocupa 50, y se sale 25 cm al otro lado del muro:
    /// entonces este test la ve pegada a un bloque que tiene a 85 cm y acusa de amontonarse a
    /// geometría que ni se toca. Salió al subir los techos, que es lo que hizo aparecer pilastras
    /// donde antes no cabían.
    fn rect_of(s: &Wg3Solid) -> super::super::plan::PlanRect {
        let (cx, cz) = (s.x_cm + s.size_x_cm / 2, s.z_cm + s.size_z_cm / 2);
        // Sólo los cuartos de vuelta, que es lo único que emite el relleno.
        let (sx, sz) = if s.yaw_deg % 180 == 90 {
            (s.size_z_cm, s.size_x_cm)
        } else {
            (s.size_x_cm, s.size_z_cm)
        };
        super::super::plan::PlanRect {
            min_x_cm: cx - sx / 2,
            min_z_cm: cz - sz / 2,
            max_x_cm: cx - sx / 2 + sx,
            max_z_cm: cz - sz / 2 + sz,
        }
    }

    /// ADR-126 D2/D3 — la rejilla se alinea a la celda del ráster (múltiplos de 50 cm, dos celdas
    /// por pozo y por pasillo) y la tierra cubre la huella entera menos los pozos: ni un hueco
    /// por el que se caiga donde no hay pozo, ni un macizo dentro de uno.
    #[test]
    fn pit_grids_align_to_the_raster_and_the_earth_covers_everything_but_the_pits() {
        let mut seen = 0usize;
        for seed in 1..60 {
            let b = building(seed);
            let segments = fill_building(&b, &no_catalogue()).segments;
            let clusters = pit_clusters_of(&b, &segments);
            if clusters.is_empty() {
                continue;
            }
            let (carves, solids) = pit_geometry(&b, &segments);
            for c in clusters {
                seen += 1;
                assert_eq!(
                    c.x0_cm.rem_euclid(50),
                    0,
                    "semilla {seed}: x0 fuera de celda"
                );
                assert_eq!(
                    c.z0_cm.rem_euclid(50),
                    0,
                    "semilla {seed}: z0 fuera de celda"
                );
                assert!(
                    (2..=PIT_MAX_PER_AXIS).contains(&c.nx)
                        && (2..=PIT_MAX_PER_AXIS).contains(&c.nz)
                );
                assert!(PIT_DEPTHS_M.contains(&(c.depth_cm / 100)));
                let fp = c.footprint();
                let earth_top = c.floor_y_cm - SLAB_THICKNESS_CM;
                let earth: Vec<&Wg3Solid> = solids
                    .iter()
                    .filter(|s| s.top_y_cm == earth_top && s.style == PIT_SHAFT_STYLE)
                    .filter(|s| fp.contains_rect(&rect_of(s)))
                    .collect();
                let area: i64 = earth
                    .iter()
                    .map(|s| s.size_x_cm as i64 * s.size_z_cm as i64)
                    .sum();
                let expected = fp.width_cm() as i64 * fp.depth_cm() as i64
                    - (c.nx * c.nz) as i64 * (PIT_SIDE_CM * PIT_SIDE_CM) as i64;
                assert_eq!(
                    area, expected,
                    "semilla {seed}: la tierra cubre {area} cm² y la huella menos los pozos son {expected}"
                );
                // Ningún macizo de tierra pisa un pozo, y cada pozo tiene su vano.
                for i in 0..c.nx {
                    for j in 0..c.nz {
                        let p = c.pit(i, j);
                        assert!(
                            !earth.iter().any(|s| rect_of(s).overlaps(&p)),
                            "semilla {seed}: hay tierra dentro del pozo ({i},{j})"
                        );
                        assert!(
                            carves.iter().any(|k| k.x_cm == p.min_x_cm
                                && k.z_cm == p.min_z_cm
                                && k.bottom_y_cm < c.floor_y_cm - SLAB_THICKNESS_CM),
                            "semilla {seed}: el pozo ({i},{j}) no tiene vano en la losa"
                        );
                    }
                }
            }
        }
        assert!(
            seen >= 5,
            "sólo {seen} rejillas en 59 semillas: la muestra no cubre el caso"
        );
        println!("[pozos] {seen} rejillas en 59 semillas");
    }
}

/// FASE 3 — lo que hay que poder afirmar después de mover los techos: que el plan sigue siendo
/// coherente, que la conectividad no se ha tocado y que la altura llega al cliente sin perderse.
#[cfg(test)]
mod ceiling_verification {
    use super::*;
    use crate::world::wg3::plan;

    /// Doce semillas, cuatro plantas. Doce y no cuatro porque lo que puede romperse aquí depende del
    /// sorteo, y cuatro regiones ya dejaron pasar un vano perdido en este proyecto.
    const SEEDS: std::ops::Range<i32> = 1..13;

    fn building(seed: i32, variety: f32) -> RegionBuilding {
        plan::plan_building_with(seed, (0.0, 0.0, 150.0, 150.0), &[], 4, variety)
    }

    #[test]
    fn the_plan_still_has_nothing_to_complain_about() {
        for seed in SEEDS {
            let b = building(seed, plan::CEILING_VARIETY);
            let problems = b.problems();
            assert!(
                problems.is_empty(),
                "el edificio de la semilla {seed} dejo de ser coherente: {}",
                problems.join("; ")
            );
            for (n, storey) in b.storeys.iter().enumerate() {
                let problems = storey.problems();
                assert!(
                    problems.is_empty(),
                    "la planta {n} de la semilla {seed} dejo de ser coherente: {}",
                    problems.join("; ")
                );
            }
        }
    }

    /// **La conectividad no la toca el techo, y esto lo DEMUESTRA en vez de suponerlo.**
    ///
    /// Con la perilla encendida y apagada tienen que salir el mismo número de espacios, los mismos
    /// enlaces uno a uno y las mismas componentes conexas. Comparar contra sí mismo es más fuerte
    /// que exigir «una sola componente»: un mundo que se parta en dos por cualquier otra razón
    /// daría el mismo dos a los dos lados, y eso es justo lo que aquí hay que saber.
    #[test]
    fn turning_the_ceilings_on_changes_no_link_and_no_component() {
        for seed in SEEDS {
            let on = building(seed, plan::CEILING_VARIETY);
            let off = building(seed, 0.0);
            assert_eq!(
                on.storeys.len(),
                off.storeys.len(),
                "la semilla {seed} levanta distinto número de plantas con la perilla encendida"
            );
            for (n, (a, b)) in on.storeys.iter().zip(off.storeys.iter()).enumerate() {
                assert_eq!(
                    a.spaces.len(),
                    b.spaces.len(),
                    "planta {n} de la semilla {seed}: distinto número de espacios"
                );
                assert_eq!(
                    a.links, b.links,
                    "planta {n} de la semilla {seed}: los enlaces no son los mismos"
                );
                assert_eq!(
                    a.components(),
                    b.components(),
                    "planta {n} de la semilla {seed}: distintas componentes conexas"
                );
                assert_eq!(
                    a.gates, b.gates,
                    "planta {n} de la semilla {seed}: las puertas de junta no son las mismas"
                );
            }
        }
    }

    /// **La altura llega al cliente y vuelve igual.**
    ///
    /// Sobre los BYTES y no sobre el struct, por la misma razón que ya escribió
    /// `the_wg3_chunk_encodes_the_keys_the_client_parser_looks_for`: lo que viaja son los bytes, y un
    /// `#[serde(rename)]` mal puesto no cambia el struct. Y con alturas REALES sacadas de un mundo
    /// generado, no con un 320 escrito a mano: un valor que coincide con el de siempre no distingue
    /// «viajó» de «el parser lo saltó y cayó al de por defecto».
    #[test]
    fn the_wire_carries_every_ceiling_height_without_loss() {
        use crate::ipc::{decode, encode, Wg3OpeningWire, Wg3SegmentWire};

        let m = Wg3Manifest {
            version: 1,
            digest: String::new(),
            pieces: Vec::new(),
        };
        let filled = fill_building(&building(7, plan::CEILING_VARIETY), &m);

        let wire: Vec<Wg3SegmentWire> = filled
            .segments
            .iter()
            .map(|s| Wg3SegmentWire {
                x_cm: s.x_cm,
                z_cm: s.z_cm,
                size_x_cm: s.size_x_cm,
                size_z_cm: s.size_z_cm,
                floor_y_cm: s.floor_y_cm,
                height_cm: s.height_cm,
                style: s.style,
                openings: s
                    .openings
                    .iter()
                    .map(|o| Wg3OpeningWire {
                        side: o.side,
                        offset_cm: o.offset_cm,
                        width_cm: o.width_cm,
                    })
                    .collect(),
            })
            .collect();

        let mut distinct: Vec<i32> = wire.iter().map(|w| w.height_cm).collect();
        distinct.sort_unstable();
        distinct.dedup();
        assert!(
            distinct.len() >= 4,
            "sólo {} alturas distintas en la trama: la muestra no distingue una altura que viaja \
             de una que se pierde ({distinct:?})",
            distinct.len()
        );

        let bytes = encode(&wire).expect("la trama de tramos tiene que codificar");
        // `encode` antepone el prefijo de longitud de cuatro bytes y `decode` recibe el CUERPO,
        // que es exactamente lo que hace el lector de verdad.
        let back: Vec<Wg3SegmentWire> = decode(&bytes[4..]).expect("y tiene que volver a leerse");
        assert_eq!(
            wire.len(),
            back.len(),
            "la trama perdió tramos por el camino"
        );
        for (a, b) in wire.iter().zip(back.iter()) {
            assert_eq!(
                a.height_cm, b.height_cm,
                "un tramo en ({}, {}) salió con {} cm de altura y volvió con {}",
                a.x_cm, a.z_cm, a.height_cm, b.height_cm
            );
            assert_eq!(a, b, "y el resto del tramo tampoco sobrevivió igual");
        }
    }

    /// El histograma de alturas sobre 200 semillas. Sin afirmar nada: es la cifra que dice si el
    /// sesgo hacia lo bajo es el que se pidió o sólo lo parece.
    #[test]
    #[ignore = "sonda de medida; se pide a mano"]
    fn probe_ceiling_histogram() {
        use std::collections::BTreeMap;

        let mut hist: BTreeMap<i32, usize> = BTreeMap::new();
        let mut by_role: BTreeMap<&str, (usize, i64)> = BTreeMap::new();
        let mut total = 0usize;

        for seed in 1..=200 {
            let b = building(seed, plan::CEILING_VARIETY);
            for storey in &b.storeys {
                for sp in &storey.spaces {
                    if !sp.role.is_built() {
                        continue;
                    }
                    // La altura que de verdad se construye, no la que se pidió: el tope de
                    // `max_clear_cm` y el atrio mandan por encima del sorteo.
                    let h = clear_height_cm(sp);
                    *hist.entry(h).or_insert(0) += 1;
                    let e = by_role.entry(sp.role.name()).or_insert((0, 0));
                    e.0 += 1;
                    e.1 += h as i64;
                    total += 1;
                }
            }
        }

        println!("[techo] {total} espacios construidos en 200 semillas");
        for (h, n) in &hist {
            let pct = *n as f32 * 100.0 / total as f32;
            let bar = "#".repeat(((pct * 1.5).round() as usize).min(90));
            println!("[techo] {h:>4} cm  {n:>6}  {pct:>5.2}%  {bar}");
        }
        let mut heights: Vec<i32> = Vec::new();
        for (h, n) in &hist {
            for _ in 0..*n {
                heights.push(*h);
            }
        }
        let mean = heights.iter().map(|&h| h as f64).sum::<f64>() / heights.len() as f64;
        println!(
            "[techo] media {:.1} cm, mediana {} cm, p10 {} cm, p90 {} cm",
            mean,
            heights[heights.len() / 2],
            heights[heights.len() / 10],
            heights[heights.len() * 9 / 10]
        );
        for (role, (n, sum)) in &by_role {
            println!(
                "[techo] {role:<9} {n:>6} espacios, media {:.1} cm",
                *sum as f64 / *n as f64
            );
        }
    }
}
