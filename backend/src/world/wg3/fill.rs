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
    Wg3Carve, Wg3Opening, Wg3Prop, Wg3Segment, Wg3Solid, CARVE_FLOOR_GUARD_CM, CASING_IN_CM,
    CASING_PROUD_CM, CASING_W_CM, MAX_SEGMENT_M, MIN_GENERATED_WIDTH_CM, PROP_BOX, PROP_CABINET,
    PROP_CEILING_TILE_HUNG, PROP_CHAIR, PROP_CHAIR_FALLEN, PROP_CLOCK, PROP_DESK, PROP_KEYBOARD,
    PROP_LIGHT_HUNG, PROP_MONITOR, PROP_PAPER, PROP_PHONE, PROP_SIGN, PROP_TRASH, PROP_TRAY,
    PROP_WHITEBOARD, SHAPE_ARCH, SHAPE_BOX, SHAPE_CYLINDER, SHAPE_HALF_CYLINDER, SHAPE_OCTAGON,
    SIGN_CALENDAR, SIGN_CALENDAR_N, SIGN_CORKBOARD, SIGN_CORKBOARD_N, SIGN_CUBICLE_TAG,
    SIGN_CUBICLE_TAG_N, SIGN_DOOR_PLATE, SIGN_DOOR_PLATE_N, SIGN_EXIT, SIGN_EXIT_N,
    SIGN_GARBLED_BASE, SIGN_PROUD_CM, STYLE_DECOR_BIT, STYLE_HIDDEN_BIT, WALL_THICKNESS_M,
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
    /// ADR-129 D3 — qué proporción de salas se viste con atrezo de oficina.
    pub props: f32,
    /// **Falso techo de oficina** (2026-09-06): rango de altura libre, en cm, que se impone a los
    /// despachos, servicios y almacenes de este carácter — sorteado por sala en pasos de 10 —, o
    /// `(0, 0)` si el carácter no lo pide. Una oficina real va a 2,70–3,00 bajo placas; las naves
    /// y la circulación no lo llevan, que es lo que deja leer la diferencia.
    pub office_ceiling_cm: (i32, i32),
    /// **Cubículos** (2026-09-06): probabilidad de que un despacho grande se reparta en puestos
    /// con mamparas. Ver [`office_cubicles`].
    pub cubicles: f32,
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
        props: 0.35,
        office_ceiling_cm: (0, 0),
        cubicles: 0.10,
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
        props: 0.8,
        office_ceiling_cm: (270, 300),
        cubicles: 0.60,
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
        props: 0.25,
        office_ceiling_cm: (0, 0),
        cubicles: 0.00,
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
        props: 0.2,
        office_ceiling_cm: (0, 0),
        cubicles: 0.00,
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
        props: 0.45,
        office_ceiling_cm: (0, 0),
        cubicles: 0.05,
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
    let kn = knobs_of(seed, space);
    let (lo, hi) = kn.office_ceiling_cm;
    let is_room = matches!(
        space.role,
        SpaceRole::Office | SpaceRole::Service | SpaceRole::Storage
    );
    if hi > 0 && is_room {
        // Falso techo (2026-09-06): por sala y en pasos de 10 cm, para que dos despachos seguidos
        // no midan lo mismo. Sorteo por posición, como todo: la misma sala da el mismo techo.
        let (cx, cz) = space.rect.centre_m();
        let t = super::hash::stream_at(seed, cx, cz, SALT_OFFICE_CEILING).next01();
        let steps = (hi - lo) / 10;
        let cap = lo + ((t * (steps + 1) as f32) as i32).min(steps) * 10;
        return if kn.ceiling_cap_cm > 0 {
            cap.min(kn.ceiling_cap_cm)
        } else {
            cap
        };
    }
    kn.ceiling_cap_cm
}

/// Sal del falso techo de oficina.
const SALT_OFFICE_CEILING: u32 = 0xA9_04_07;

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
    /// ADR-129 — las anclas de atrezo. Aparte de los macizos porque no son geometría: son «aquí
    /// va una mesa», y la mesa la pone el cliente.
    pub props: Vec<Wg3Prop>,

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
        self.props.extend(other.props);
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
    fill_building_with(building, manifest, OCCLUDER_DENSITY)
}

/// [`fill_building`] con la densidad de oclusores intra-espacio como parámetro.
///
/// Existe para lo mismo que `plan_storey_with`: poder medir la pasada contra sí misma APAGADA
/// (`occluder_density = 0.0`) sin tocar nada más. Lo servido entra siempre por [`fill_building`].
pub fn fill_building_with(
    building: &RegionBuilding,
    manifest: &Wg3Manifest,
    occluder_density: f32,
) -> FilledRegion {
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
    // Oclusores intra-espacio (feat/occluders, fusionada el 2026-09-06): después de pilares y
    // divisiones porque los esquivan, y ANTES de bloques, pilastras, listones y atrezo, que esquivan
    // `out.solids` y por tanto también a éstos.
    out.solids.extend(interior_occluders(
        building,
        manifest,
        &placed,
        &pillars,
        &partitions,
        &seg_doors,
        &out.carves,
        &out.segments,
        occluder_density,
    ));
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
    // ADR-129 — el atrezo, el último: esquiva todo lo que está a ras de suelo, y lo que frena deja
    // su macizo invisible.
    // Los CUBÍCULOS (2026-09-06) van antes: reparten el despacho en puestos con mamparas, y el
    // atrezo de pared de siempre se queda para los despachos que no los llevan.
    let (walls, cub_props, cub_hidden, taken) =
        office_cubicles(building, &out.segments, &out.solids, &out.carves);
    out.solids.extend(walls);
    out.props.extend(cub_props);
    out.solids.extend(cub_hidden);
    let (props, hidden) = office_props(building, &out.segments, &out.solids, &out.carves, &taken);
    out.props.extend(props);
    out.solids.extend(hidden);
    // ADR-129 enm. 1 — los CARTELES, después de todo: van pegados a superficies que ya existen
    // (la cáscara y las mamparas) y esquivan todo lo que ya se ha puesto contra ellas.
    let signs = office_signs(building, &out.segments, &out.solids, &out.carves);
    out.props.extend(signs);
    // ADR-105 enm. 19 — el DETERIORO del falso techo, y va el último de todos: es lo único que
    // tiene que esquivar además el atrezo que acaba de caer (una placa sobre una silla se ve).
    let (decay_solids, decay_props) = office_decay(
        building,
        &out.segments,
        &out.solids,
        &out.carves,
        &out.props,
        &taken,
    );
    out.solids.extend(decay_solids);
    out.props.extend(decay_props);
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
    // ADR-130 — en una TORRE no hay pozos: la cámara a −10…−50 m atravesaría los sótanos. Allí se
    // baja por la escalera y por los agujeros de forjado.
    if building.ground > 0 {
        return Vec::new();
    }
    building
        .storeys
        .get(building.ground)
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
        let pits_here = if n == building.ground {
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
                    let mut len = (BLOCK_LEN_CM.0
                        + (st.next01() * (BLOCK_LEN_CM.1 - BLOCK_LEN_CM.0) as f32) as i32)
                        .min(room);
                    // Un bloque de 150×300, 150×350 o 200×400 tiene EXACTAMENTE la silueta de una
                    // cruz de pilar y `is_pillar` lo clasifica como tal (STATE lo daba por ambiguo).
                    // Sale una vez de cada cincuenta sorteos; se le quitan diez centímetros y deja
                    // de tener la forma de otro emisor.
                    if len % PILLAR_SIDE_STEP_CM == 0 {
                        len -= 10;
                    }
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

/// ADR-129 D3 — huellas del atrezo, en centímetros de plan. Son la tabla del servidor; el prefab
/// real se mide en el cliente y esta tabla se ajusta a él, no al revés.
// Medidos en el editor sobre los prefabs del catálogo (`Wg3PropCatalogBuilder`, 2026-09-06):
// mesa 2,30 × 0,75 × 1,02; silla 0,67 × 1,17 × 0,71; «archivador» (Cupboard) 0,89 × 0,79 × 0,46;
// caja 0,40 × 0,29 × 0,29; pizarra 1,50 × 1,09 con el pivote en su centro.
const DESK_W_CM: i32 = 230;
const DESK_D_CM: i32 = 102;
const DESK_H_CM: i32 = 75;
const CHAIR_CM: i32 = 70;
const CABINET_W_CM: i32 = 90;
const CABINET_D_CM: i32 = 46;
const CABINET_H_CM: i32 = 80;
const BOX_CM: i32 = 40;
const BOX_H_CM: i32 = 30;
const WHITEBOARD_W_CM: i32 = 150;
/// Cota del CENTRO de la pizarra (su pivote): a 1,40 el borde inferior queda a 0,85.
const WHITEBOARD_Y_CM: i32 = 140;
/// Lo que el atrezo deja a toda boca de cualquier tramo.
const PROP_MOUTH_CLEAR_CM: i32 = 100;
/// Lo que deja a todo macizo a ras de suelo.
const PROP_GAP_CM: i32 = 40;
/// Sal del sorteo de atrezo.
const SALT_PROPS: u32 = 0xA9_04_05;

// ─────────────────── cubículos (2026-09-06) ───────────────────
//
// Joel, con la foto de la oficina delante: «más detalles de oficina». Lo que convierte «sala con
// mesas» en «planta de oficinas» son los PUESTOS: mamparas de metro y medio en rejilla, un pasillo
// entre cada dos filas, y en cada celda mesa, silla y monitor. Es geometría (las mamparas frenan y
// se ven) más atrezo (ADR-129), así que el cliente no necesita nada nuevo.

/// Grosor de una mampara de cubículo, en cm. **Doce, y ningún otro emisor lo usa**: los tests
/// clasifican los macizos por su forma (8 barrote de rejilla, 15 dintel, 20 pretil, 30 división,
/// 35 parteluz, 40 viga, 45 oclusor) y una mampara tiene que tener la suya. El primer intento fue
/// ocho, y `is_grille_bar` se la quedaba.
pub(super) const CUBICLE_T_CM: i32 = 12;
/// Altura de la mampara: se ve por encima de pie, no sentado. Distinta de los 110 del medio muro
/// bajo y de los 230 de la mampara de sala (enm. 8), por lo mismo.
pub(super) const CUBICLE_H_CM: i32 = 140;
/// Ancho de una celda: la mesa (230) y quince centímetros a cada lado.
const CUBICLE_CELL_CM: i32 = 260;
/// Fondo de una celda: mesa, silla y sitio para levantarse.
const CUBICLE_DEPTH_CM: i32 = 240;
/// Pasillo entre dos filas de celdas enfrentadas.
const CUBICLE_AISLE_CM: i32 = 150;
/// Superficie mínima del despacho para llevar cubículos.
const CUBICLE_MIN_AREA_M2: f32 = 60.0;
/// Lo que las filas dejan libre hasta la pared en el eje largo, en cm.
const CUBICLE_MARGIN_CM: i32 = 60;
/// Celdas que se quedan sin puesto (mamparas y nada más): una oficina no está llena.
const CUBICLE_EMPTY_CHANCE: f32 = 0.15;
/// Tope de celdas por despacho: por encima es un almacén de mamparas, no una oficina.
const CUBICLE_MAX_PER_ROOM: usize = 24;
const SALT_CUBICLES: u32 = 0xA9_04_08;

/// Lo que sale de [`office_cubicles`]: mamparas, atrezo, macizos invisibles del atrezo y los
/// despachos repartidos como (planta, espacio).
type Cubicles = (
    Vec<Wg3Solid>,
    Vec<Wg3Prop>,
    Vec<Wg3Solid>,
    Vec<(usize, usize)>,
);

/// ¿Es una mampara de cubículo? Por su forma, como todo macizo.
pub(super) fn is_cubicle_wall(s: &Wg3Solid) -> bool {
    s.size_x_cm.min(s.size_z_cm) == CUBICLE_T_CM && s.top_y_cm - s.bottom_y_cm == CUBICLE_H_CM
}

/// **Los cubículos**: en los despachos grandes de un carácter que los pida, filas de celdas de
/// 2,60 × 2,40 con mampara al fondo y a los lados, abiertas a un pasillo de 1,50; cada dos filas
/// van espalda con espalda. En cada celda, mesa contra la mampara del fondo con su macizo
/// invisible, silla (a veces caída), y encima monitor, teclado, teléfono o bandeja con las mismas
/// probabilidades que el atrezo de pared; una de cada siete celdas se queda vacía.
///
/// Lo que se esquiva, celda a celda y con medio metro de holgura: las bocas de la sala (una celda
/// delante de una puerta se salta, y ese hueco es el paso hasta el pasillo), rellanos, huecos de
/// escalera y de forjado, pozos, los macizos que ya había (pilares, divisiones, oclusores,
/// bloques) y los recortes de la pared (ventanas). Una mampara de cubículo sólo existe donde
/// existe su celda, así que no puede tapar una boca que la celda no tapa.
///
/// Devuelve las mamparas, el atrezo, los macizos invisibles del atrezo y los despachos repartidos
/// como (planta, espacio), para que [`office_props`] no les ponga encima el atrezo de pared.
fn office_cubicles(
    building: &RegionBuilding,
    segments: &[Wg3Segment],
    solids: &[Wg3Solid],
    carves: &[Wg3Carve],
) -> Cubicles {
    let mut walls: Vec<Wg3Solid> = Vec::new();
    let mut props: Vec<Wg3Prop> = Vec::new();
    let mut hidden: Vec<Wg3Solid> = Vec::new();
    let mut taken: Vec<(usize, usize)> = Vec::new();
    let seed = building.seed;
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
                    o.width_cm / 2 + PROP_MOUTH_CLEAR_CM,
                    g.floor_y_cm,
                )
            })
        })
        .collect();
    let rect_of = |x: i32, z: i32, w: i32, d: i32| super::plan::PlanRect {
        min_x_cm: x,
        min_z_cm: z,
        max_x_cm: x + w,
        max_z_cm: z + d,
    };
    let box_overlaps = |r: &super::plan::PlanRect, o: &Wg3Solid| -> bool {
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
        let pits_here = if n == building.ground {
            pit_rects_of(building, segments)
        } else {
            Vec::new()
        };
        for (i, s) in plan.built() {
            if s.role != SpaceRole::Office || s.rise_cm != 0 || s.is_composite() {
                continue;
            }
            if s.area_m2() < CUBICLE_MIN_AREA_M2 {
                continue;
            }
            let kn = knobs_of(seed, s);
            let (cx, cz) = s.rect.centre_m();
            let mut st = super::hash::stream_at(seed, cx, cz, SALT_CUBICLES);
            if st.next01() >= kn.cubicles {
                continue;
            }
            let floor = s.floor_y_cm;
            let top = floor + clear_height_cm(s);
            let host = segments
                .iter()
                .filter(|g| {
                    g.floor_y_cm == floor
                        && s.covers_rect(&rect_of(g.x_cm, g.z_cm, g.size_x_cm, g.size_z_cm))
                })
                .max_by_key(|g| g.size_x_cm as i64 * g.size_z_cm as i64);
            let Some(g) = host else {
                continue;
            };
            let inner = rect_of(
                g.x_cm + WALL_T_CM,
                g.z_cm + WALL_T_CM,
                g.size_x_cm - 2 * WALL_T_CM,
                g.size_z_cm - 2 * WALL_T_CM,
            );
            // Eje largo `u` (las filas corren por él), eje corto `v` (las filas se apilan por él).
            let along_x = inner.width_cm() >= inner.depth_cm();
            let (u0, u1, v0, v1) = if along_x {
                (
                    inner.min_x_cm,
                    inner.max_x_cm,
                    inner.min_z_cm,
                    inner.max_z_cm,
                )
            } else {
                (
                    inner.min_z_cm,
                    inner.max_z_cm,
                    inner.min_x_cm,
                    inner.max_x_cm,
                )
            };
            let cells = ((u1 - u0) - 2 * CUBICLE_MARGIN_CM) / CUBICLE_CELL_CM;
            if cells < 2 || (v1 - v0) < 2 * CUBICLE_DEPTH_CM + CUBICLE_AISLE_CM + 20 {
                continue;
            }
            let cells = (cells as usize).clamp(2, CUBICLE_MAX_PER_ROOM / 2) as i32;
            let start = u0 + ((u1 - u0) - cells * CUBICLE_CELL_CM) / 2;

            // Las filas: abierta hacia +v, pasillo, abierta hacia −v, y espalda con espalda otra
            // abierta hacia +v… Una fila sólo entra si le queda su pasillo delante.
            let mut rows: Vec<(i32, bool)> = Vec::new();
            let mut v = v0 + 10;
            loop {
                if v + CUBICLE_DEPTH_CM + CUBICLE_AISLE_CM > v1 {
                    break;
                }
                rows.push((v, true));
                v += CUBICLE_DEPTH_CM + CUBICLE_AISLE_CM;
                if v + CUBICLE_DEPTH_CM > v1 - 10 {
                    break;
                }
                rows.push((v, false));
                v += CUBICLE_DEPTH_CM;
                if rows.len() >= CUBICLE_MAX_PER_ROOM {
                    break;
                }
            }
            if rows.is_empty() {
                continue;
            }

            let own_hole = hole_square(&s.rect).shrunk(-50);
            let style = style_of(s.role);
            let free = |r: &super::plan::PlanRect| -> bool {
                let grown = r.shrunk(-50);
                if !inner.contains_rect(r) || !s.covers_rect(r) {
                    return false;
                }
                if mouths.iter().any(|&(mx, mz, half, fl)| {
                    (fl - floor).abs() < 100
                        && mx + half > r.min_x_cm
                        && mx - half < r.max_x_cm
                        && mz + half > r.min_z_cm
                        && mz - half < r.max_z_cm
                }) {
                    return false;
                }
                if landings.iter().any(|l| l.overlaps(&grown))
                    || wells_here.iter().any(|w| w.overlaps(&grown))
                    || holes_above.iter().any(|h| h.overlaps(&grown))
                    || pits_here.iter().any(|p| p.overlaps(&grown))
                    || (n > 0 && own_hole.overlaps(&grown))
                {
                    return false;
                }
                // Un metro a los macizos que ya había: es lo que un bloque exento (enm. 17) exige
                // a su alrededor, y una mampara a 40 cm de un pilar es un rincón que no se pasa.
                // Más el grosor de la mampara, porque la de la frontera se planta justo FUERA de la
                // celda que la pide (costó un test: 93 cm de un bloque en vez de 99).
                let wide = r.shrunk(-(BLOCK_GAP_CM + CUBICLE_T_CM));
                if solids.iter().any(|o| {
                    !o.is_decoration()
                        && o.bottom_y_cm < floor + 200
                        && o.top_y_cm > floor
                        && box_overlaps(&wide, o)
                }) {
                    return false;
                }
                if carves.iter().any(|k| {
                    k.bottom_y_cm < top
                        && k.top_y_cm > floor
                        && k.x_cm < grown.max_x_cm
                        && k.x_cm + k.size_x_cm > grown.min_x_cm
                        && k.z_cm < grown.max_z_cm
                        && k.z_cm + k.size_z_cm > grown.min_z_cm
                }) {
                    return false;
                }
                true
            };
            // De (u, v) a mundo: `u` corre por el eje largo.
            let world = |ua: i32, va: i32, ub: i32, vb: i32| -> super::plan::PlanRect {
                if along_x {
                    rect_of(ua, va, ub - ua, vb - va)
                } else {
                    rect_of(va, ua, vb - va, ub - ua)
                }
            };
            let wall = |r: super::plan::PlanRect| Wg3Solid {
                x_cm: r.min_x_cm,
                z_cm: r.min_z_cm,
                size_x_cm: r.width_cm(),
                size_z_cm: r.depth_cm(),
                bottom_y_cm: floor,
                top_y_cm: floor + CUBICLE_H_CM,
                style,
                yaw_deg: 0,
                shape: SHAPE_BOX,
            };

            let mut any = false;
            for &(rv, open_plus) in &rows {
                // Fondo y frente de la celda en `v`: la mampara del fondo va en el lado cerrado.
                let (back, front) = if open_plus {
                    (rv, rv + CUBICLE_DEPTH_CM)
                } else {
                    (rv + CUBICLE_DEPTH_CM, rv)
                };
                let (va, vb) = (back.min(front), back.max(front));
                let placed: Vec<bool> = (0..cells)
                    .map(|k| {
                        let ua = start + k * CUBICLE_CELL_CM;
                        free(&world(ua, va, ua + CUBICLE_CELL_CM, vb))
                    })
                    .collect();
                if !placed.iter().any(|&p| p) {
                    continue;
                }
                any = true;
                // Mamparas laterales en cada frontera con celda a un lado o al otro.
                for j in 0..=cells {
                    let left = j > 0 && placed[(j - 1) as usize];
                    let right = j < cells && placed[j as usize];
                    if !left && !right {
                        continue;
                    }
                    let u = start + j * CUBICLE_CELL_CM;
                    let u = if j == cells { u - CUBICLE_T_CM } else { u };
                    walls.push(wall(world(u, va, u + CUBICLE_T_CM, vb)));
                }
                for k in 0..cells {
                    if !placed[k as usize] {
                        continue;
                    }
                    let ua = start + k * CUBICLE_CELL_CM;
                    let ub = ua + CUBICLE_CELL_CM;
                    // Mampara del fondo.
                    let bv = if open_plus { back } else { back - CUBICLE_T_CM };
                    walls.push(wall(world(ua, bv, ub, bv + CUBICLE_T_CM)));

                    if st.next01() < CUBICLE_EMPTY_CHANCE {
                        continue;
                    }
                    // La mesa, contra la mampara del fondo y centrada en la celda.
                    let du0 = ua + (CUBICLE_CELL_CM - DESK_W_CM) / 2;
                    let dv0 = if open_plus {
                        back + CUBICLE_T_CM + 5
                    } else {
                        back - CUBICLE_T_CM - 5 - DESK_D_CM
                    };
                    let desk = world(du0, dv0, du0 + DESK_W_CM, dv0 + DESK_D_CM);
                    let yaw: i16 = match (along_x, open_plus) {
                        (true, true) => 0,
                        (true, false) => 180,
                        (false, true) => 90,
                        (false, false) => 270,
                    };
                    let dc = (
                        (desk.min_x_cm + desk.max_x_cm) / 2,
                        (desk.min_z_cm + desk.max_z_cm) / 2,
                    );
                    props.push(Wg3Prop {
                        x_cm: dc.0,
                        z_cm: dc.1,
                        y_cm: floor,
                        yaw_deg: yaw,
                        kind: PROP_DESK,
                        style,
                    });
                    hidden.push(Wg3Solid {
                        x_cm: desk.min_x_cm,
                        z_cm: desk.min_z_cm,
                        size_x_cm: desk.width_cm(),
                        size_z_cm: desk.depth_cm(),
                        bottom_y_cm: floor,
                        top_y_cm: floor + DESK_H_CM,
                        style: style | STYLE_HIDDEN_BIT,
                        yaw_deg: 0,
                        shape: SHAPE_BOX,
                    });
                    let on_desk = |u: i32, v: i32| -> (i32, i32) {
                        match yaw {
                            0 => (dc.0 + u, dc.1 + v),
                            180 => (dc.0 - u, dc.1 - v),
                            90 => (dc.0 + v, dc.1 + u),
                            _ => (dc.0 - v, dc.1 - u),
                        }
                    };
                    let desk_top = floor + DESK_H_CM;
                    for (u, v, kind, chance) in [
                        (0, -20, PROP_MONITOR, 0.85),
                        (0, 15, PROP_KEYBOARD, 0.70),
                        (-75, -10, PROP_PHONE, 0.45),
                        (75, -10, PROP_TRAY, 0.45),
                    ] {
                        if st.next01() < chance {
                            let (x, z) = on_desk(u, v);
                            props.push(Wg3Prop {
                                x_cm: x,
                                z_cm: z,
                                y_cm: desk_top,
                                yaw_deg: yaw,
                                kind,
                                style,
                            });
                        }
                    }
                    // La silla, delante de la mesa hacia el pasillo; una de cada siete, caída.
                    let out = DESK_D_CM / 2 + 45;
                    let fallen = st.next01() < 0.15;
                    let (sx, sz) = match yaw {
                        0 => (dc.0, dc.1 + out),
                        180 => (dc.0, dc.1 - out),
                        90 => (dc.0 + out, dc.1),
                        _ => (dc.0 - out, dc.1),
                    };
                    let side = (st.next01() * 60.0) as i32 - 30;
                    let (sx, sz) = match yaw {
                        0 | 180 => (sx + side, sz),
                        _ => (sx, sz + side),
                    };
                    let cyaw: i16 = if fallen {
                        ((st.next01() * 360.0) as i32 / 15 * 15) as i16
                    } else {
                        (yaw + 180) % 360
                    };
                    props.push(Wg3Prop {
                        x_cm: sx,
                        z_cm: sz,
                        y_cm: floor,
                        yaw_deg: cyaw,
                        kind: if fallen {
                            PROP_CHAIR_FALLEN
                        } else {
                            PROP_CHAIR
                        },
                        style,
                    });
                    // La papelera, en el rincón del fondo.
                    if st.next01() < 0.40 {
                        let (tu, tv) = (
                            ua + 30,
                            if open_plus {
                                back + CUBICLE_T_CM + 25
                            } else {
                                back - CUBICLE_T_CM - 25
                            },
                        );
                        let t = world(tu - 20, tv - 20, tu + 20, tv + 20);
                        props.push(Wg3Prop {
                            x_cm: (t.min_x_cm + t.max_x_cm) / 2,
                            z_cm: (t.min_z_cm + t.max_z_cm) / 2,
                            y_cm: floor,
                            yaw_deg: 0,
                            kind: PROP_TRASH,
                            style,
                        });
                    }
                }
            }
            if any {
                taken.push((n, i));
            }
        }
    }
    (walls, props, hidden, taken)
}

/// ADR-129 D3 — **vestir la sala**: mesas contra la pared larga con su silla, su monitor y una
/// papelera; archivador en una esquina; pizarra en la pared de enfrente; cajas sueltas; papeles por
/// el suelo. Dentro de UN tramo (la lección de los pozos), esquivando bocas de cualquier tramo,
/// macizos a ras de suelo, vanos de pared, pozos, rellanos y huecos de escalera. Lo que frena (mesa,
/// archivador, caja) emite además su macizo invisible.
#[allow(clippy::too_many_arguments)]
fn office_props(
    building: &RegionBuilding,
    segments: &[Wg3Segment],
    solids: &[Wg3Solid],
    carves: &[Wg3Carve],
    // Los despachos que ya repartió `office_cubicles`, como (planta, espacio): en ésos el atrezo
    // de pared sobra.
    skip: &[(usize, usize)],
) -> (Vec<Wg3Prop>, Vec<Wg3Solid>) {
    let mut props: Vec<Wg3Prop> = Vec::new();
    let mut hidden: Vec<Wg3Solid> = Vec::new();
    let seed = building.seed;
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
                    o.width_cm / 2 + PROP_MOUTH_CLEAR_CM,
                    g.floor_y_cm,
                )
            })
        })
        .collect();
    let rect_of = |x: i32, z: i32, w: i32, d: i32| super::plan::PlanRect {
        min_x_cm: x,
        min_z_cm: z,
        max_x_cm: x + w,
        max_z_cm: z + d,
    };
    let box_overlaps = |r: &super::plan::PlanRect, o: &Wg3Solid| -> bool {
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
        let pits_here = if n == building.ground {
            pit_rects_of(building, segments)
        } else {
            Vec::new()
        };
        for (i, s) in plan.built() {
            if s.role.is_circulation() || s.role == SpaceRole::Stair || s.rise_cm != 0 {
                continue;
            }
            if skip.contains(&(n, i)) {
                continue;
            }
            let kn = knobs_of(seed, s);
            let (cx, cz) = s.rect.centre_m();
            let mut st = super::hash::stream_at(seed, cx, cz, SALT_PROPS);
            if st.next01() >= kn.props {
                continue;
            }
            let floor = s.floor_y_cm;
            let top = floor + clear_height_cm(s);
            // El tramo anfitrión: el mayor de los suyos a su cota.
            let host = segments
                .iter()
                .filter(|g| {
                    g.floor_y_cm == floor
                        && s.covers_rect(&rect_of(g.x_cm, g.z_cm, g.size_x_cm, g.size_z_cm))
                })
                .max_by_key(|g| g.size_x_cm as i64 * g.size_z_cm as i64);
            let Some(g) = host else {
                continue;
            };
            let inner = rect_of(
                g.x_cm + WALL_T_CM,
                g.z_cm + WALL_T_CM,
                g.size_x_cm - 2 * WALL_T_CM,
                g.size_z_cm - 2 * WALL_T_CM,
            );
            if inner.width_cm() < 300 || inner.depth_cm() < 300 {
                continue;
            }
            let own_hole = hole_square(&s.rect).shrunk(-50);
            let style = style_of(s.role);
            let mut placed: Vec<super::plan::PlanRect> = Vec::new();
            // ¿Cabe aquí, sin pisar nada? `gap` es la holgura con macizos y con lo ya puesto.
            let free = |r: &super::plan::PlanRect,
                        gap: i32,
                        placed: &Vec<super::plan::PlanRect>,
                        mind_solids: bool|
             -> bool {
                let grown = r.shrunk(-gap);
                if !inner.contains_rect(r) || !s.covers_rect(r) {
                    return false;
                }
                if mouths.iter().any(|&(mx, mz, half, fl)| {
                    (fl - floor).abs() < 100
                        && mx + half > r.min_x_cm
                        && mx - half < r.max_x_cm
                        && mz + half > r.min_z_cm
                        && mz - half < r.max_z_cm
                }) {
                    return false;
                }
                if landings.iter().any(|l| l.overlaps(&grown))
                    || wells_here.iter().any(|w| w.overlaps(&grown))
                    || holes_above.iter().any(|h| h.overlaps(&grown))
                    || pits_here.iter().any(|p| p.overlaps(&grown))
                    || (n > 0 && own_hole.overlaps(&grown))
                    || placed.iter().any(|p| p.overlaps(&grown))
                {
                    return false;
                }
                if mind_solids
                    && solids.iter().any(|o| {
                        !o.is_decoration()
                            && o.bottom_y_cm < floor + 200
                            && o.top_y_cm > floor
                            && box_overlaps(&grown, o)
                    })
                {
                    return false;
                }
                if carves.iter().any(|k| {
                    k.bottom_y_cm < top
                        && k.top_y_cm > floor
                        && k.x_cm < grown.max_x_cm
                        && k.x_cm + k.size_x_cm > grown.min_x_cm
                        && k.z_cm < grown.max_z_cm
                        && k.z_cm + k.size_z_cm > grown.min_z_cm
                }) {
                    return false;
                }
                true
            };
            let put = |props: &mut Vec<Wg3Prop>,
                       hidden: &mut Vec<Wg3Solid>,
                       placed: &mut Vec<super::plan::PlanRect>,
                       r: super::plan::PlanRect,
                       y: i32,
                       yaw: i16,
                       kind: u8,
                       h: i32| {
                props.push(Wg3Prop {
                    x_cm: (r.min_x_cm + r.max_x_cm) / 2,
                    z_cm: (r.min_z_cm + r.max_z_cm) / 2,
                    y_cm: y,
                    yaw_deg: yaw,
                    kind,
                    style,
                });
                if h > 0 {
                    hidden.push(Wg3Solid {
                        x_cm: r.min_x_cm,
                        z_cm: r.min_z_cm,
                        size_x_cm: r.width_cm(),
                        size_z_cm: r.depth_cm(),
                        bottom_y_cm: y,
                        top_y_cm: y + h,
                        style: style | STYLE_HIDDEN_BIT,
                        yaw_deg: 0,
                        shape: SHAPE_BOX,
                    });
                    placed.push(r);
                } else {
                    placed.push(r);
                }
            };

            // Las mesas, contra la pared larga; la pizarra, en la de enfrente.
            let along_x = inner.width_cm() >= inner.depth_cm();
            let len = if along_x {
                inner.width_cm()
            } else {
                inner.depth_cm()
            };
            let desk_wall_min = st.next01() < 0.5;
            let count = (len / 350).clamp(0, 3);
            for k in 0..count {
                let u = if along_x {
                    inner.min_x_cm
                } else {
                    inner.min_z_cm
                } + (len * (2 * k + 1)) / (2 * count)
                    - DESK_W_CM / 2;
                let (r, yaw) = if along_x {
                    let z = if desk_wall_min {
                        inner.min_z_cm + 5
                    } else {
                        inner.max_z_cm - 5 - DESK_D_CM
                    };
                    (
                        rect_of(u, z, DESK_W_CM, DESK_D_CM),
                        if desk_wall_min { 0 } else { 180 },
                    )
                } else {
                    let x = if desk_wall_min {
                        inner.min_x_cm + 5
                    } else {
                        inner.max_x_cm - 5 - DESK_D_CM
                    };
                    (
                        rect_of(x, u, DESK_D_CM, DESK_W_CM),
                        if desk_wall_min { 90 } else { 270 },
                    )
                };
                if !free(&r, PROP_GAP_CM, &placed, true) {
                    continue;
                }
                let desk_centre = ((r.min_x_cm + r.max_x_cm) / 2, (r.min_z_cm + r.max_z_cm) / 2);
                put(
                    &mut props,
                    &mut hidden,
                    &mut placed,
                    r,
                    floor,
                    yaw,
                    PROP_DESK,
                    DESK_H_CM,
                );
                // Lo de ENCIMA de la mesa, en el marco de la mesa: `u` a lo largo de la pared,
                // `v` hacia la sala. Monitor atrás y centrado, teclado delante, teléfono a un lado,
                // bandeja al otro. Cada uno con su dado: una mesa vacía también es de la foto.
                let on_desk = |u: i32, v: i32| -> (i32, i32) {
                    match yaw {
                        0 => (desk_centre.0 + u, desk_centre.1 + v),
                        180 => (desk_centre.0 - u, desk_centre.1 - v),
                        90 => (desk_centre.0 + v, desk_centre.1 + u),
                        _ => (desk_centre.0 - v, desk_centre.1 - u),
                    }
                };
                let desk_top = floor + DESK_H_CM;
                for (u, v, kind, chance) in [
                    (0, -20, PROP_MONITOR, 0.85),
                    (0, 15, PROP_KEYBOARD, 0.70),
                    (-75, -10, PROP_PHONE, 0.55),
                    (75, -10, PROP_TRAY, 0.50),
                ] {
                    if st.next01() < chance {
                        let (x, z) = on_desk(u, v);
                        props.push(Wg3Prop {
                            x_cm: x,
                            z_cm: z,
                            y_cm: desk_top,
                            yaw_deg: yaw,
                            kind,
                            style,
                        });
                    }
                }
                // La silla, delante, mirando a la mesa.
                let out = DESK_D_CM / 2 + 45;
                let (sx, sz) = match yaw {
                    0 => (desk_centre.0, desk_centre.1 + out),
                    180 => (desk_centre.0, desk_centre.1 - out),
                    90 => (desk_centre.0 + out, desk_centre.1),
                    _ => (desk_centre.0 - out, desk_centre.1),
                };
                // Una de cada cuatro sillas está CAÍDA, algo más lejos y girada de cualquier
                // manera: el abandono de la foto.
                let fallen = st.next01() < 0.25;
                let (sx, sz) = if fallen {
                    let extra = 40 + (st.next01() * 80.0) as i32;
                    let side = (st.next01() * 120.0) as i32 - 60;
                    match yaw {
                        0 => (sx + side, sz + extra),
                        180 => (sx + side, sz - extra),
                        90 => (sx + extra, sz + side),
                        _ => (sx - extra, sz + side),
                    }
                } else {
                    (sx, sz)
                };
                let cyaw: i16 = if fallen {
                    ((st.next01() * 360.0) as i32 / 15 * 15) as i16
                } else {
                    (yaw + 180) % 360
                };
                let ckind = if fallen {
                    PROP_CHAIR_FALLEN
                } else {
                    PROP_CHAIR
                };
                let chair = rect_of(sx - CHAIR_CM / 2, sz - CHAIR_CM / 2, CHAIR_CM, CHAIR_CM);
                // Sin holgura con la mesa: la silla va pegada a ella a propósito.
                if free(&chair, 0, &placed, true) {
                    put(
                        &mut props,
                        &mut hidden,
                        &mut placed,
                        chair,
                        floor,
                        cyaw,
                        ckind,
                        0,
                    );
                }
                // La papelera, a un lado, en la primera mesa.
                if k == 0 {
                    let (tx, tz) = if along_x {
                        (r.min_x_cm - 30, (r.min_z_cm + r.max_z_cm) / 2)
                    } else {
                        ((r.min_x_cm + r.max_x_cm) / 2, r.min_z_cm - 40)
                    };
                    let trash = rect_of(tx - 20, tz - 20, 40, 40);
                    if free(&trash, 10, &placed, true) {
                        put(
                            &mut props,
                            &mut hidden,
                            &mut placed,
                            trash,
                            floor,
                            0,
                            PROP_TRASH,
                            0,
                        );
                    }
                }
            }
            // El archivador, en una esquina, con la espalda a la pared corta.
            let corner = (st.next01() * 4.0) as i32 % 4;
            let (cxr, czr, cyaw) = match corner {
                0 => (inner.min_x_cm + 10, inner.min_z_cm + 10, 90),
                1 => (inner.max_x_cm - 10 - CABINET_W_CM, inner.min_z_cm + 10, 270),
                2 => (inner.min_x_cm + 10, inner.max_z_cm - 10 - CABINET_D_CM, 90),
                _ => (
                    inner.max_x_cm - 10 - CABINET_W_CM,
                    inner.max_z_cm - 10 - CABINET_D_CM,
                    270,
                ),
            };
            let cab = rect_of(cxr, czr, CABINET_W_CM, CABINET_D_CM);
            if free(&cab, PROP_GAP_CM, &placed, true) {
                put(
                    &mut props,
                    &mut hidden,
                    &mut placed,
                    cab,
                    floor,
                    cyaw,
                    PROP_CABINET,
                    CABINET_H_CM,
                );
            }
            // La pizarra, en la pared de enfrente de las mesas, colgada a 90.
            let (wb, wyaw) = if along_x {
                let z = if desk_wall_min {
                    inner.max_z_cm - 10
                } else {
                    inner.min_z_cm
                };
                let x = (inner.min_x_cm + inner.max_x_cm) / 2 - WHITEBOARD_W_CM / 2;
                (
                    rect_of(x, z, WHITEBOARD_W_CM, 10),
                    if desk_wall_min { 180 } else { 0 },
                )
            } else {
                let x = if desk_wall_min {
                    inner.max_x_cm - 10
                } else {
                    inner.min_x_cm
                };
                let z = (inner.min_z_cm + inner.max_z_cm) / 2 - WHITEBOARD_W_CM / 2;
                (
                    rect_of(x, z, 10, WHITEBOARD_W_CM),
                    if desk_wall_min { 270 } else { 90 },
                )
            };
            if free(&wb, 30, &placed, true) {
                put(
                    &mut props,
                    &mut hidden,
                    &mut placed,
                    wb,
                    floor + WHITEBOARD_Y_CM,
                    wyaw,
                    PROP_WHITEBOARD,
                    0,
                );
            }
            // El reloj, en la misma pared, a un lado de la pizarra, a 2,10.
            if st.next01() < 0.6 {
                let ck = if along_x {
                    rect_of(wb.min_x_cm - 120, wb.min_z_cm, 30, 10)
                } else {
                    rect_of(wb.min_x_cm, wb.min_z_cm - 120, 10, 30)
                };
                if free(&ck, 10, &placed, true) {
                    put(
                        &mut props,
                        &mut hidden,
                        &mut placed,
                        ck,
                        floor + 210,
                        wyaw,
                        PROP_CLOCK,
                        0,
                    );
                }
            }
            // Cajas sueltas.
            let boxes = (st.next01() * 4.0) as i32;
            for _ in 0..boxes {
                let bx = inner.min_x_cm
                    + 100
                    + (st.next01() * (inner.width_cm() - 200 - BOX_CM).max(1) as f32) as i32;
                let bz = inner.min_z_cm
                    + 100
                    + (st.next01() * (inner.depth_cm() - 200 - BOX_CM).max(1) as f32) as i32;
                let b = rect_of(bx, bz, BOX_CM, BOX_CM);
                if free(&b, 30, &placed, true) {
                    let yaw = ((st.next01() * 360.0) as i32 / 15 * 15) as i16;
                    put(
                        &mut props,
                        &mut hidden,
                        &mut placed,
                        b,
                        floor,
                        yaw,
                        PROP_BOX,
                        BOX_H_CM,
                    );
                    // Y a veces otra encima, un poco girada.
                    if st.next01() < 0.4 {
                        props.push(Wg3Prop {
                            x_cm: (b.min_x_cm + b.max_x_cm) / 2,
                            z_cm: (b.min_z_cm + b.max_z_cm) / 2,
                            y_cm: floor + BOX_H_CM,
                            yaw_deg: (yaw + 20) % 360,
                            kind: PROP_BOX,
                            style,
                        });
                    }
                }
            }
            // Papeles por el suelo: no frenan y no esquivan macizos, sólo pozos y bocas.
            let papers = 8 + (st.next01() * 14.0) as i32;
            for _ in 0..papers {
                let px = inner.min_x_cm
                    + 40
                    + (st.next01() * (inner.width_cm() - 80).max(1) as f32) as i32;
                let pz = inner.min_z_cm
                    + 40
                    + (st.next01() * (inner.depth_cm() - 80).max(1) as f32) as i32;
                let p = rect_of(px - 15, pz - 15, 30, 30);
                if free(&p, 0, &Vec::new(), false) {
                    let yaw = (st.next01() * 360.0) as i16;
                    props.push(Wg3Prop {
                        x_cm: px,
                        z_cm: pz,
                        y_cm: floor,
                        yaw_deg: yaw,
                        kind: PROP_PAPER,
                        style,
                    });
                }
            }
        }
    }
    (props, hidden)
}

// ─────────────────── carteles (ADR-129 enm. 1, 2026-09-06) ───────────────────
//
// Joel: «no hay ningún texto en el mundo». Y no lo había: el mundo entero es geometría y muebles,
// y una oficina sin una sola palabra escrita no se lee como una oficina. Los carteles son el único
// sitio donde el mundo HABLA, así que son también el sitio donde puede mentir: al bajar (ADR-130
// D4) el texto empieza a salir mal, y eso se dice sin código nuevo cambiando de rango de variante.

/// Cota del CENTRO de una placa de despacho: a la altura de los ojos, junto a la jamba.
const SIGN_PLATE_Y_CM: i32 = 160;
/// Cota del centro de una señal de salida: por encima de la cabeza y por debajo del techo más bajo
/// que se sirve (2,50 de altura libre), con su medio alto de margen.
const SIGN_EXIT_Y_CM: i32 = 220;
/// Cota del centro de un tablón de corcho: mide 90 de alto, así que el pie queda a 1,00.
const SIGN_CORK_Y_CM: i32 = 145;
/// Cota del centro de un calendario de pared.
const SIGN_CALENDAR_Y_CM: i32 = 165;
/// Cota del centro de un rótulo de cubículo, sobre el pie de la mampara (que mide `CUBICLE_H_CM`).
const SIGN_TAG_Y_CM: i32 = 118;
/// Lo que una placa deja a la jamba, ADEMÁS de la holgura de boca de todo el atrezo: una placa
/// pegada al marco no se lee, y una encima de la puerta está prohibida de entrada.
const SIGN_DOOR_SIDE_CM: i32 = 25;
/// Superficie mínima de una sala para llevar tablón de corcho. Un tablón de 1,20 en un cuarto de
/// tres por tres es una pared de corcho.
const SIGN_CORK_MIN_AREA_M2: f32 = 40.0;
/// Cuántas de esas salas lo llevan de verdad. Sin dado salían treinta y siete por región: un
/// tablón en cada sala grande es una cadena de oficinas, no una oficina.
const SIGN_CORK_CHANCE: f32 = 0.35;
/// Fondo de la banda que un cartel ocupa contra su pared. No es el grosor del cartel (es plano):
/// es lo que se mira para decidir si algo estorba —un marco, una pilastra, un archivador—.
const SIGN_BAND_CM: i32 = 25;
/// Tope de carteles por espacio. Un pasillo empapelado no da miedo, da risa.
const SIGN_MAX_PER_SPACE: usize = 4;
/// Cuántas mamparas de cubículo llevan rótulo.
const SIGN_TAG_CHANCE: f32 = 0.35;
const SALT_SIGNS: u32 = 0xA9_04_09;
/// ADR-130 D4 — cuánto texto sale mal ya en el primer sótano. El decaimiento (`depth²`) sólo se
/// nota a partir de −50 m, y los carteles son lo PRIMERO que deja de tener sentido al bajar: de
/// aquí arranca la rampa, y llega a 1,0 en el fondo.
const SIGN_GARBLE_FLOOR: f32 = 0.35;

/// ADR-130 D4 — la profundidad de una cota, en [0, 1]: 0 en la calle, 1 a −100 m.
fn sign_depth(floor_y_cm: i32) -> f32 {
    (-(floor_y_cm as f32) / 10_000.0).clamp(0.0, 1.0)
}

/// ¿Este cartel sale con el texto ESTROPEADO? Sólo bajo tierra, y cada vez más abajo más veces.
fn sign_garbled(floor_y_cm: i32, basement: bool, st: &mut super::hash::Stream) -> bool {
    if !basement {
        return false;
    }
    let decay = sign_depth(floor_y_cm).powi(2);
    st.next01() < SIGN_GARBLE_FLOOR + (1.0 - SIGN_GARBLE_FLOOR) * decay
}

/// **Los CARTELES**: placas de despacho junto a las puertas, rótulos en las mamparas de cubículo,
/// señales de salida en los pasillos, tablón de corcho en las salas grandes y un calendario parado.
///
/// Un cartel es un ancla de atrezo más ([`PROP_SIGN`]) con la variante en `style`, así que no
/// cuesta wire. Va PEGADO a una superficie: la cara interior de la cáscara del tramo o la cara de
/// una mampara, con [`SIGN_PROUD_CM`] de despegue para que no haya z-fighting, y con el giro que
/// mira hacia afuera de esa cara.
///
/// Lo que se esquiva: toda boca de cualquier tramo con la holgura del atrezo más la jamba (**nunca
/// sobre una puerta**), todo recorte de pared que solape en vertical con el cartel (**nunca sobre
/// una ventana**), todo macizo que asome en la banda a esa altura (marcos, pilastras, oclusores) y
/// los carteles ya puestos. Determinista por posición, como todo lo demás.
fn office_signs(
    building: &RegionBuilding,
    segments: &[Wg3Segment],
    solids: &[Wg3Solid],
    carves: &[Wg3Carve],
) -> Vec<Wg3Prop> {
    let mut signs: Vec<Wg3Prop> = Vec::new();
    let seed = building.seed;
    let rect_of = |x: i32, z: i32, w: i32, d: i32| super::plan::PlanRect {
        min_x_cm: x,
        min_z_cm: z,
        max_x_cm: x + w,
        max_z_cm: z + d,
    };
    // Las bocas de TODOS los tramos, como en el resto del atrezo: (centro, medio ancho + holgura,
    // cota). Una puerta del vecino que caiga en mi pared sigue siendo una puerta.
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
                    o.width_cm / 2 + PROP_MOUTH_CLEAR_CM,
                    g.floor_y_cm,
                )
            })
        })
        .collect();

    // ¿Cabe un cartel en esta banda de pared, entre estas dos cotas?
    let band_free = |r: &super::plan::PlanRect,
                     y_lo: i32,
                     y_hi: i32,
                     floor: i32,
                     placed: &Vec<(super::plan::PlanRect, i32, i32)>|
     -> bool {
        if mouths.iter().any(|&(mx, mz, half, fl)| {
            (fl - floor).abs() < 100
                && mx + half > r.min_x_cm
                && mx - half < r.max_x_cm
                && mz + half > r.min_z_cm
                && mz - half < r.max_z_cm
        }) {
            return false;
        }
        // Ventanas, rendijas y vanos: el recorte que cruce la banda A ESTA ALTURA.
        if carves.iter().any(|k| {
            k.bottom_y_cm < y_hi
                && k.top_y_cm > y_lo
                && k.x_cm < r.max_x_cm
                && k.x_cm + k.size_x_cm > r.min_x_cm
                && k.z_cm < r.max_z_cm
                && k.z_cm + k.size_z_cm > r.min_z_cm
        }) {
            return false;
        }
        // Cualquier macizo que asome a esa altura, decoración incluida: un marco de puerta se ve.
        if solids.iter().any(|o| {
            !o.is_hidden()
                && o.bottom_y_cm < y_hi
                && o.top_y_cm > y_lo
                && o.x_cm < r.max_x_cm
                && o.x_cm + o.size_x_cm > r.min_x_cm
                && o.z_cm < r.max_z_cm
                && o.z_cm + o.size_z_cm > r.min_z_cm
        }) {
            return false;
        }
        !placed
            .iter()
            .any(|(p, lo, hi)| *lo < y_hi && *hi > y_lo && p.overlaps(r))
    };

    for (n, plan) in building.storeys.iter().enumerate() {
        let basement = n < building.ground;
        for (_, s) in plan.built() {
            // Los espacios hundidos no: su suelo no está a la cota del tramo y una placa a 1,60 de
            // una cota que no es la del suelo cuelga donde no toca.
            if s.role == SpaceRole::Stair || s.rise_cm != 0 {
                continue;
            }
            let floor = s.floor_y_cm;
            let clear = clear_height_cm(s);
            let host = segments
                .iter()
                .filter(|g| {
                    g.floor_y_cm == floor
                        && s.covers_rect(&rect_of(g.x_cm, g.z_cm, g.size_x_cm, g.size_z_cm))
                })
                .max_by_key(|g| g.size_x_cm as i64 * g.size_z_cm as i64);
            let Some(g) = host else {
                continue;
            };
            let inner = rect_of(
                g.x_cm + WALL_T_CM,
                g.z_cm + WALL_T_CM,
                g.size_x_cm - 2 * WALL_T_CM,
                g.size_z_cm - 2 * WALL_T_CM,
            );
            if inner.width_cm() < 200 || inner.depth_cm() < 200 {
                continue;
            }
            let (cx, cz) = s.rect.centre_m();
            let mut st = super::hash::stream_at(seed, cx, cz, SALT_SIGNS);
            let mut placed: Vec<(super::plan::PlanRect, i32, i32)> = Vec::new();

            // El ancla y la banda de un cartel de ancho `w` en el lado `side` de la cáscara:
            // 0 pared −z (mira a +z), 1 pared +z, 2 pared −x, 3 pared +x. `along` es la coordenada
            // sobre la pared (x en las de ±z, z en las de ±x).
            let face = |side: u8, along: i32, w: i32| -> (i32, i32, i16, super::plan::PlanRect) {
                match side {
                    0 => (
                        along,
                        inner.min_z_cm + SIGN_PROUD_CM,
                        0,
                        rect_of(along - w / 2, inner.min_z_cm, w, SIGN_BAND_CM),
                    ),
                    1 => (
                        along,
                        inner.max_z_cm - SIGN_PROUD_CM,
                        180,
                        rect_of(
                            along - w / 2,
                            inner.max_z_cm - SIGN_BAND_CM,
                            w,
                            SIGN_BAND_CM,
                        ),
                    ),
                    2 => (
                        inner.min_x_cm + SIGN_PROUD_CM,
                        along,
                        90,
                        rect_of(inner.min_x_cm, along - w / 2, SIGN_BAND_CM, w),
                    ),
                    _ => (
                        inner.max_x_cm - SIGN_PROUD_CM,
                        along,
                        270,
                        rect_of(
                            inner.max_x_cm - SIGN_BAND_CM,
                            along - w / 2,
                            SIGN_BAND_CM,
                            w,
                        ),
                    ),
                }
            };
            // ¿Cabe la variante `v` centrada en `along` de ese lado, a esa cota? Si cabe, se pone.
            let hang = |signs: &mut Vec<Wg3Prop>,
                        placed: &mut Vec<(super::plan::PlanRect, i32, i32)>,
                        side: u8,
                        along: i32,
                        y_centre: i32,
                        variant: u8|
             -> bool {
                if placed.len() >= SIGN_MAX_PER_SPACE {
                    return false;
                }
                let (w, h) = super::segment::sign_size_cm(variant);
                let (y_lo, y_hi) = (floor + y_centre - h / 2, floor + y_centre + h / 2);
                if y_centre + h / 2 + 10 > clear {
                    return false;
                }
                let (x, z, yaw, band) = face(side, along, w);
                let along_lo = match side {
                    0 | 1 => inner.min_x_cm,
                    _ => inner.min_z_cm,
                };
                let along_hi = match side {
                    0 | 1 => inner.max_x_cm,
                    _ => inner.max_z_cm,
                };
                if along - w / 2 < along_lo + 20 || along + w / 2 > along_hi - 20 {
                    return false;
                }
                if !s.covers_rect(&band) || !band_free(&band, y_lo, y_hi, floor, placed) {
                    return false;
                }
                signs.push(Wg3Prop {
                    x_cm: x,
                    z_cm: z,
                    y_cm: floor + y_centre,
                    yaw_deg: yaw,
                    kind: PROP_SIGN,
                    style: variant,
                });
                placed.push((band, y_lo, y_hi));
                true
            };

            if s.role.is_circulation() {
                // **Señales de salida**: en las paredes del FONDO del pasillo, mirando por donde se
                // viene. Dos por pasillo como mucho, y sólo si el techo da de sí.
                let along_x = inner.width_cm() >= inner.depth_cm();
                let (a, b) = if along_x { (2u8, 3u8) } else { (0u8, 1u8) };
                let mid = if along_x {
                    (inner.min_z_cm + inner.max_z_cm) / 2
                } else {
                    (inner.min_x_cm + inner.max_x_cm) / 2
                };
                let span = if along_x {
                    inner.depth_cm()
                } else {
                    inner.width_cm()
                };
                for side in [a, b] {
                    let garbled = sign_garbled(floor, basement, &mut st);
                    let v = SIGN_EXIT
                        + (st.next01() * SIGN_EXIT_N as f32) as u8 % SIGN_EXIT_N
                        + if garbled { SIGN_GARBLED_BASE } else { 0 };
                    // El centro de la pared del fondo es justo donde suele estar la puerta que
                    // sigue el pasillo, así que si el sitio bueno está ocupado se prueba a los
                    // lados antes de renunciar: una salida sin señal es lo que se quería arreglar.
                    for f in [0.0f32, 0.25, -0.25, 0.4, -0.4] {
                        if hang(
                            &mut signs,
                            &mut placed,
                            side,
                            mid + (span as f32 * f) as i32,
                            SIGN_EXIT_Y_CM,
                            v,
                        ) {
                            break;
                        }
                    }
                }
                continue;
            }

            // **Placas de despacho**: una por boca de ESTE tramo, al lado de la jamba y por dentro.
            for o in &g.openings {
                let (w, d) = (g.size_x_cm as f32 / 100.0, g.size_z_cm as f32 / 100.0);
                let (lx, lz) =
                    super::placement::local_point(o.side, o.offset_cm as f32 / 100.0, w, d);
                let (mx, mz) = (
                    g.x_cm + (lx * 100.0).round() as i32,
                    g.z_cm + (lz * 100.0).round() as i32,
                );
                // Lado de la BOCA (0 = +z, 1 = +x, 2 = −z, 3 = −x) al lado de la CÁSCARA.
                let side = match o.side % 4 {
                    0 => 1u8,
                    1 => 3u8,
                    2 => 0u8,
                    _ => 2u8,
                };
                let (pw, _) = super::segment::sign_size_cm(SIGN_DOOR_PLATE);
                let step = o.width_cm / 2 + SIGN_DOOR_SIDE_CM + pw / 2 + PROP_MOUTH_CLEAR_CM;
                let centre = match side {
                    0 | 1 => mx,
                    _ => mz,
                };
                let garbled = sign_garbled(floor, basement, &mut st);
                let v = SIGN_DOOR_PLATE
                    + (st.next01() * SIGN_DOOR_PLATE_N as f32) as u8 % SIGN_DOOR_PLATE_N
                    + if garbled { SIGN_GARBLED_BASE } else { 0 };
                let first = if st.next01() < 0.5 { -step } else { step };
                for delta in [first, -first] {
                    if hang(
                        &mut signs,
                        &mut placed,
                        side,
                        centre + delta,
                        SIGN_PLATE_Y_CM,
                        v,
                    ) {
                        break;
                    }
                }
            }

            // **Tablón de corcho**: sólo en salas grandes, en el centro de una pared.
            if s.rect.area_m2() >= SIGN_CORK_MIN_AREA_M2 && st.next01() < SIGN_CORK_CHANCE {
                let side = (st.next01() * 4.0) as u8 % 4;
                let along = match side {
                    0 | 1 => (inner.min_x_cm + inner.max_x_cm) / 2,
                    _ => (inner.min_z_cm + inner.max_z_cm) / 2,
                };
                let garbled = sign_garbled(floor, basement, &mut st);
                let v = SIGN_CORKBOARD
                    + (st.next01() * SIGN_CORKBOARD_N as f32) as u8 % SIGN_CORKBOARD_N
                    + if garbled { SIGN_GARBLED_BASE } else { 0 };
                for k in 0..4u8 {
                    if hang(
                        &mut signs,
                        &mut placed,
                        (side + k) % 4,
                        along,
                        SIGN_CORK_Y_CM,
                        v,
                    ) {
                        break;
                    }
                }
            }

            // **El calendario parado**: en una pared, descentrado. Uno de cada dos espacios.
            if st.next01() < 0.5 {
                let side = (st.next01() * 4.0) as u8 % 4;
                let (lo, hi) = match side {
                    0 | 1 => (inner.min_x_cm, inner.max_x_cm),
                    _ => (inner.min_z_cm, inner.max_z_cm),
                };
                let along = lo + ((hi - lo) as f32 * (0.25 + st.next01() * 0.5)) as i32;
                let garbled = sign_garbled(floor, basement, &mut st);
                let v = SIGN_CALENDAR
                    + (st.next01() * SIGN_CALENDAR_N as f32) as u8 % SIGN_CALENDAR_N
                    + if garbled { SIGN_GARBLED_BASE } else { 0 };
                hang(&mut signs, &mut placed, side, along, SIGN_CALENDAR_Y_CM, v);
            }
        }
    }

    // **Rótulos de cubículo**: sobre la cara de una mampara, no sobre la cáscara. Van aparte del
    // bucle de espacios porque la superficie es el macizo, y el macizo ya sabe dónde está.
    for w in solids.iter().filter(|s| is_cubicle_wall(s)) {
        let cx = (w.x_cm + w.size_x_cm / 2) as f32 / 100.0;
        let cz = (w.z_cm + w.size_z_cm / 2) as f32 / 100.0;
        let mut st = super::hash::stream_at(seed, cx, cz, SALT_SIGNS);
        if st.next01() >= SIGN_TAG_CHANCE {
            continue;
        }
        let thin_z = w.size_z_cm <= w.size_x_cm;
        let plus = st.next01() < 0.5;
        let (tw, th) = super::segment::sign_size_cm(SIGN_CUBICLE_TAG);
        if (if thin_z { w.size_x_cm } else { w.size_z_cm }) < tw + 20 {
            continue;
        }
        let (x, z, yaw) = if thin_z {
            (
                w.x_cm + w.size_x_cm / 2,
                if plus {
                    w.z_cm + w.size_z_cm + SIGN_PROUD_CM
                } else {
                    w.z_cm - SIGN_PROUD_CM
                },
                if plus { 0i16 } else { 180 },
            )
        } else {
            (
                if plus {
                    w.x_cm + w.size_x_cm + SIGN_PROUD_CM
                } else {
                    w.x_cm - SIGN_PROUD_CM
                },
                w.z_cm + w.size_z_cm / 2,
                if plus { 90i16 } else { 270 },
            )
        };
        // El ancla tiene que caer DENTRO de un tramo a su cota, como todo el atrezo: una mampara
        // pegada a la cáscara dejaría el rótulo dentro del muro.
        let host = segments.iter().find(|g| {
            g.floor_y_cm == w.bottom_y_cm
                && x > g.x_cm + WALL_T_CM
                && x < g.x_cm + g.size_x_cm - WALL_T_CM
                && z > g.z_cm + WALL_T_CM
                && z < g.z_cm + g.size_z_cm - WALL_T_CM
        });
        if host.is_none() {
            continue;
        }
        let y = w.bottom_y_cm + SIGN_TAG_Y_CM;
        if y + th / 2 > w.top_y_cm {
            continue;
        }
        let basement = building
            .storeys
            .iter()
            .position(|p| p.built().any(|(_, s)| s.floor_y_cm == w.bottom_y_cm))
            .is_some_and(|n| n < building.ground);
        let garbled = sign_garbled(w.bottom_y_cm, basement, &mut st);
        let v = SIGN_CUBICLE_TAG
            + (st.next01() * SIGN_CUBICLE_TAG_N as f32) as u8 % SIGN_CUBICLE_TAG_N
            + if garbled { SIGN_GARBLED_BASE } else { 0 };
        signs.push(Wg3Prop {
            x_cm: x,
            z_cm: z,
            y_cm: y,
            yaw_deg: yaw,
            kind: PROP_SIGN,
            style: v,
        });
    }

    signs
}

// ─────────────────── deterioro del falso techo (ADR-105 enm. 19) ───────────────────
//
// El falso techo de la enm. 18 es una superficie continua y limpia; lo que hace Backrooms una
// planta de oficinas no es el techo, es el techo ROTO. Cuatro piezas, todas donde hay falso techo
// (la sala que no lo lleva no tiene placas que caer):
//
//   1. placas caídas en el suelo (macizo fino con giro, decoración: ni frena ni entra al ráster),
//   2. placas colgando de un lado del techo (PROP: `Wg3Solid` sólo gira en Y, y una placa colgando
//      pide inclinación, así que la construye el cliente),
//   3. una luminaria descolgada en diagonal POR PLANTA (prop, por lo mismo),
//   4. alguna baldosa de suelo técnico levantada (macizo fino, decoración).
//
// Nada de esto frena ni se estampa: `add_solid` se salta la decoración, así que islas y nav no se
// mueven. Y el atrezo de suelo se salta los despachos con cubículos: la mampara mide 1,40 y las
// placas del techo cuelgan por encima, pero una placa caída SÍ pisaría un puesto.

/// Lado de una placa de falso techo, en cm. Es la del cliente (`Wg3SceneAssembler.CeilingTileM`):
/// las placas caídas son las que faltan arriba.
const CEILING_PLATE_CM: i32 = 60;
/// Canto de una placa caída en el suelo. La forma que ningún otro emisor tiene es la HUELLA
/// cuadrada de 60 × 60 más este canto: los cantos finos ya existen sueltos (un rodapié mide 4 de
/// canto y 6,83 m de largo), y los grosores en planta están cogidos de 8 a 45 (8 barrote,
/// 12 mampara, 15 dintel, 20 pretil, 25 pilastra, 30 división, 35 parteluz, 40 viga, 45 oclusor).
pub(super) const FALLEN_PLATE_H_CM: i32 = 4;
/// Cuánto se levanta una baldosa de suelo técnico. Nueve por lo mismo que los cuatro de arriba, y
/// por debajo del escalón que sube un `CharacterController` sin frenarse.
pub(super) const RAISED_TILE_H_CM: i32 = 9;
/// Lo que el deterioro deja a la pared del tramo.
const DECAY_MARGIN_CM: i32 = 40;
/// Lo que deja a un macizo o a un mueble ya puestos.
const DECAY_GAP_CM: i32 = 25;
/// Tope de placas caídas por sala.
const FALLEN_MAX_PER_ROOM: i32 = 8;
/// Tope de placas colgando por sala.
const HUNG_MAX_PER_ROOM: i32 = 4;
/// Cuántas plantas de sótano hay entre la calle y el fondo del descenso (ADR-130 D1: B30 a −99,6).
const BASEMENT_SPAN_STOREYS: i32 = 30;
/// Sal del sorteo de deterioro.
const SALT_DECAY: u32 = 0xA9_04_09;
/// Sal de la luminaria descolgada de cada planta.
const SALT_DECAY_LAMP: u32 = 0xA9_04_0A;

/// ADR-130 D4 — **el decaimiento por profundidad**, `depth²`, entre 0 en la calle y 1 en el fondo.
///
/// La profundidad se mide contra el fondo SERVIDO, no contra los −100 m del ADR: con los tres
/// sótanos de la rebanada 1, `depth² ` sobre −100 m daría 0,01 en B3 —invisible— y la regla
/// «sube con la profundidad» no se podría ni medir ni ver. Los dos extremos coinciden con D4 (la
/// calle da 0 y el sótano más hondo da 1) y cuando lleguen los treinta, `BASEMENT_SPAN_STOREYS` es
/// el denominador. La rebanada 2 de ADR-130 traerá la función canónica y ésta se irá con ella.
fn decay_at(building: &RegionBuilding, n: usize) -> f32 {
    let below = building.ground as i32 - n as i32;
    if below <= 0 {
        return 0.0;
    }
    let deepest = building.ground.min(BASEMENT_SPAN_STOREYS as usize) as i32;
    let d = below as f32 / deepest.max(1) as f32;
    (d * d).clamp(0.0, 1.0)
}

/// ¿Es una placa de falso techo caída en el suelo? Por su forma, como todo macizo.
pub(super) fn is_fallen_plate(s: &Wg3Solid) -> bool {
    s.is_decoration()
        && s.top_y_cm - s.bottom_y_cm == FALLEN_PLATE_H_CM
        && s.size_x_cm == CEILING_PLATE_CM
        && s.size_z_cm == CEILING_PLATE_CM
}

/// ¿Es una baldosa de suelo técnico levantada? Por su forma.
pub(super) fn is_raised_floor_tile(s: &Wg3Solid) -> bool {
    s.is_decoration()
        && s.top_y_cm - s.bottom_y_cm == RAISED_TILE_H_CM
        && s.size_x_cm == CEILING_PLATE_CM
        && s.size_z_cm == CEILING_PLATE_CM
}

/// **El deterioro del falso techo.** Ver el bloque de arriba. Devuelve los macizos de decoración
/// (placas caídas y baldosas levantadas) y los props (placas colgando y la luminaria de la planta).
fn office_decay(
    building: &RegionBuilding,
    segments: &[Wg3Segment],
    solids: &[Wg3Solid],
    carves: &[Wg3Carve],
    props: &[Wg3Prop],
    cubicled: &[(usize, usize)],
) -> (Vec<Wg3Solid>, Vec<Wg3Prop>) {
    let mut out_solids: Vec<Wg3Solid> = Vec::new();
    let mut out_props: Vec<Wg3Prop> = Vec::new();
    let seed = building.seed;
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
                    o.width_cm / 2 + PROP_MOUTH_CLEAR_CM,
                    g.floor_y_cm,
                )
            })
        })
        .collect();
    let rect_of = |x: i32, z: i32, w: i32, d: i32| super::plan::PlanRect {
        min_x_cm: x,
        min_z_cm: z,
        max_x_cm: x + w,
        max_z_cm: z + d,
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
        let pits_here = if n == building.ground {
            pit_rects_of(building, segments)
        } else {
            Vec::new()
        };
        let decay = decay_at(building, n);
        // Las anclas de luminaria de ESTA planta: una sola cuelga, y se elige al final.
        let mut lamp_spots: Vec<(i32, i32, i32, i16)> = Vec::new();

        for (i, s) in plan.built() {
            // Sólo donde hay falso techo, que es lo que se rompe: los despachos, servicios y
            // almacenes del carácter que lo pide (enm. 18).
            let is_room = matches!(
                s.role,
                SpaceRole::Office | SpaceRole::Service | SpaceRole::Storage
            );
            if !is_room || s.rise_cm != 0 || s.is_composite() {
                continue;
            }
            let kn = knobs_of(seed, s);
            if kn.office_ceiling_cm.1 == 0 {
                continue;
            }
            let (cx, cz) = s.rect.centre_m();
            let mut st = super::hash::stream_at(seed, cx, cz, SALT_DECAY);
            // Una sala de cada dos arriba, casi todas abajo: el deterioro es lo que crece.
            if st.next01() >= 0.45 + 0.5 * decay {
                continue;
            }
            let floor = s.floor_y_cm;
            let clear = clear_height_cm(s);
            let ceiling = floor + clear;
            let host = segments
                .iter()
                .filter(|g| {
                    g.floor_y_cm == floor
                        && s.covers_rect(&rect_of(g.x_cm, g.z_cm, g.size_x_cm, g.size_z_cm))
                })
                .max_by_key(|g| g.size_x_cm as i64 * g.size_z_cm as i64);
            let Some(g) = host else {
                continue;
            };
            let inner = rect_of(
                g.x_cm + WALL_T_CM + DECAY_MARGIN_CM,
                g.z_cm + WALL_T_CM + DECAY_MARGIN_CM,
                g.size_x_cm - 2 * (WALL_T_CM + DECAY_MARGIN_CM),
                g.size_z_cm - 2 * (WALL_T_CM + DECAY_MARGIN_CM),
            );
            if inner.width_cm() < CEILING_PLATE_CM || inner.depth_cm() < CEILING_PLATE_CM {
                continue;
            }
            let own_hole = hole_square(&s.rect).shrunk(-50);
            let style = style_of(s.role);

            // Lo que atraviesa el techo y lo que hay a ras de suelo. `at_floor` distingue las dos
            // alturas: una placa colgando no la estorba una mesa, y una caída sí.
            let clear_of_openings = |r: &super::plan::PlanRect| -> bool {
                if !inner.contains_rect(r) || !s.covers_rect(r) {
                    return false;
                }
                let grown = r.shrunk(-DECAY_GAP_CM);
                if mouths.iter().any(|&(mx, mz, half, fl)| {
                    (fl - floor).abs() < 100
                        && mx + half > r.min_x_cm
                        && mx - half < r.max_x_cm
                        && mz + half > r.min_z_cm
                        && mz - half < r.max_z_cm
                }) {
                    return false;
                }
                !(landings.iter().any(|l| l.overlaps(&grown))
                    || wells_here.iter().any(|w| w.overlaps(&grown))
                    || holes_above.iter().any(|h| h.overlaps(&grown))
                    || pits_here.iter().any(|p| p.overlaps(&grown))
                    || (n > 0 && own_hole.overlaps(&grown)))
            };
            let free = |r: &super::plan::PlanRect, at_floor: bool| -> bool {
                if !clear_of_openings(r) {
                    return false;
                }
                let grown = r.shrunk(-DECAY_GAP_CM);
                // Los macizos: a ras de suelo los que están a la altura de una pierna; en el techo,
                // sólo los que llegan hasta él (un pilar, una viga colgada).
                let (lo, hi) = if at_floor {
                    (floor, floor + 200)
                } else {
                    (ceiling - 40, ceiling + 40)
                };
                if solids.iter().any(|o| {
                    !o.is_decoration()
                        && o.bottom_y_cm < hi
                        && o.top_y_cm > lo
                        && o.x_cm < grown.max_x_cm
                        && o.x_cm + o.size_x_cm > grown.min_x_cm
                        && o.z_cm < grown.max_z_cm
                        && o.z_cm + o.size_z_cm > grown.min_z_cm
                }) {
                    return false;
                }
                if carves.iter().any(|k| {
                    k.bottom_y_cm < ceiling
                        && k.top_y_cm > floor
                        && k.x_cm < grown.max_x_cm
                        && k.x_cm + k.size_x_cm > grown.min_x_cm
                        && k.z_cm < grown.max_z_cm
                        && k.z_cm + k.size_z_cm > grown.min_z_cm
                }) {
                    return false;
                }
                // El atrezo ya puesto: la mesa y el archivador llevan macizo invisible, pero la
                // silla, la papelera y los papeles no, y una placa encima de una silla se ve.
                if at_floor
                    && props.iter().any(|p| {
                        (p.y_cm - floor).abs() < 120
                            && p.x_cm > grown.min_x_cm
                            && p.x_cm < grown.max_x_cm
                            && p.z_cm > grown.min_z_cm
                            && p.z_cm < grown.max_z_cm
                    })
                {
                    return false;
                }
                true
            };
            // Un punto al azar de la rejilla de placas de la sala, alineado a la esquina interior.
            let cols = (inner.width_cm() / CEILING_PLATE_CM).max(1);
            let rows = (inner.depth_cm() / CEILING_PLATE_CM).max(1);
            let spot = |st: &mut super::hash::Stream| -> super::plan::PlanRect {
                let c = (st.next01() * cols as f32) as i32;
                let r = (st.next01() * rows as f32) as i32;
                rect_of(
                    inner.min_x_cm + c.min(cols - 1) * CEILING_PLATE_CM,
                    inner.min_z_cm + r.min(rows - 1) * CEILING_PLATE_CM,
                    CEILING_PLATE_CM,
                    CEILING_PLATE_CM,
                )
            };
            let area = s.area_m2();
            let on_cubicles = cubicled.contains(&(n, i));

            // 1 — las placas caídas. Nada en un despacho con cubículos: pisarían un puesto.
            if !on_cubicles {
                let want = (((1.0 + 3.0 * decay) * area / 35.0).round() as i32)
                    .clamp(0, FALLEN_MAX_PER_ROOM);
                for _ in 0..want {
                    let r = spot(&mut st);
                    // Múltiplo de 15, que es lo que exige `every_served_solid_is_well_formed`.
                    let yaw = ((st.next01() * 360.0) as i32 / 15 * 15) as i16;
                    if free(&r, true) {
                        out_solids.push(Wg3Solid {
                            x_cm: r.min_x_cm,
                            z_cm: r.min_z_cm,
                            size_x_cm: CEILING_PLATE_CM,
                            size_z_cm: CEILING_PLATE_CM,
                            bottom_y_cm: floor,
                            top_y_cm: floor + FALLEN_PLATE_H_CM,
                            style: style | STYLE_DECOR_BIT,
                            yaw_deg: yaw,
                            shape: SHAPE_BOX,
                        });
                    }
                }
                // 4 — la baldosa de suelo técnico levantada, una y a veces dos.
                let tiles = if st.next01() < 0.20 + 0.45 * decay {
                    1 + i32::from(st.next01() < 0.25 + 0.35 * decay)
                } else {
                    0
                };
                for _ in 0..tiles {
                    let r = spot(&mut st);
                    let yaw = ((st.next01() * 90.0) as i32 / 15 * 15) as i16;
                    if free(&r, true) {
                        out_solids.push(Wg3Solid {
                            x_cm: r.min_x_cm,
                            z_cm: r.min_z_cm,
                            size_x_cm: CEILING_PLATE_CM,
                            size_z_cm: CEILING_PLATE_CM,
                            bottom_y_cm: floor,
                            top_y_cm: floor + RAISED_TILE_H_CM,
                            style: style | STYLE_DECOR_BIT,
                            yaw_deg: yaw,
                            shape: SHAPE_BOX,
                        });
                    }
                }
            }

            // 2 — las placas colgando. Éstas sí en los despachos con cubículos: cuelgan del techo,
            // a metro y medio por encima de la mampara.
            let want =
                (((0.5 + 1.5 * decay) * area / 45.0).round() as i32).clamp(0, HUNG_MAX_PER_ROOM);
            for _ in 0..want {
                let r = spot(&mut st);
                let yaw = ((st.next01() * 4.0) as i32).min(3) as i16 * 90;
                if free(&r, false) {
                    out_props.push(Wg3Prop {
                        x_cm: (r.min_x_cm + r.max_x_cm) / 2,
                        z_cm: (r.min_z_cm + r.max_z_cm) / 2,
                        y_cm: ceiling,
                        yaw_deg: yaw,
                        kind: PROP_CEILING_TILE_HUNG,
                        style,
                    });
                }
            }

            // 3 — el ancla de luminaria de la sala, candidata para la de la planta.
            let r = spot(&mut st);
            if free(&r, false) {
                let yaw = ((st.next01() * 4.0) as i32).min(3) as i16 * 90;
                lamp_spots.push((
                    (r.min_x_cm + r.max_x_cm) / 2,
                    (r.min_z_cm + r.max_z_cm) / 2,
                    ceiling,
                    yaw,
                ));
            }
        }

        // 3 — UNA luminaria descolgada por planta, elegida entre las anclas de la planta. El orden
        // de `lamp_spots` es el de `built()`, que es estable, así que el sorteo también lo es.
        if !lamp_spots.is_empty() {
            // Por la PRIMERA ancla de la planta, que es de la planta y de nadie más: no hay
            // coordenada de región a mano aquí, y la cota sola repetiría el sorteo entre regiones.
            let mut st = super::hash::stream_at(
                seed,
                lamp_spots[0].0 as f32 / CM_PER_M,
                lamp_spots[0].1 as f32 / CM_PER_M,
                SALT_DECAY_LAMP,
            );
            let k = ((st.next01() * lamp_spots.len() as f32) as usize).min(lamp_spots.len() - 1);
            let (x, z, y, yaw) = lamp_spots[k];
            out_props.push(Wg3Prop {
                x_cm: x,
                z_cm: z,
                y_cm: y,
                yaw_deg: yaw,
                kind: PROP_LIGHT_HUNG,
                style: style_of(SpaceRole::Office),
            });
        }
    }
    (out_solids, out_props)
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
        if n == building.ground {
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
    // El brazo es EXACTAMENTE el que emite la cruz: la mitad del lado redondeada a la celda, y sólo
    // desde el lado mínimo de cruz. Antes valía «entre dos celdas y la mitad», y un bloque exento
    // de 100×250 (enm. 17) pasaba por brazo — salió en el primer sótano de ADR-130, a 395 cm de una
    // puerta, que es una regla de pilares y no de bloques.
    let arm = (b / 2) / PILLAR_SIDE_STEP_CM * PILLAR_SIDE_STEP_CM;
    long_ok && (a == b || (b >= PILLAR_CROSS_MIN_SIDE_CM && a == arm))
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

            // ---- PRETILES, sólo sobre los tramos abiertos ----
            // La misma cuenta que `atrium_carves`, por [`atrium_open_runs`] (fusión 2026-09-06).
            // Antes se probaba una franja de 50 cm a lo largo de todo el lado y, con una sala de
            // arriba en cualquier punto, el pretil corría el lado entero — con el vano ya parcial,
            // eso dejaba pretil con muro encima («sigue siendo macizo medio metro arriba»).
            let grow = (CARVE_DEPTH_M * CM_PER_M) as i32;
            let bands = bands_of(&r, grow);
            for side in 0..4u8 {
                let band = match side {
                    0 => bands[0],
                    1 => bands[2],
                    2 => bands[1],
                    _ => bands[3],
                };
                let along_x = side.is_multiple_of(2);
                let (from, to) = if along_x {
                    (r.min_x_cm, r.max_x_cm)
                } else {
                    (r.min_z_cm, r.max_z_cm)
                };
                for (ra, rb) in atrium_open_runs(&band, above, grow) {
                    let (ra, rb) = (ra.max(from), rb.min(to));
                    if rb <= ra {
                        continue;
                    }
                    let mut at = ra;
                    while at < rb {
                        let end = (at + MAX_SOLID_CM).min(rb);
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
        let pits_here = if n == building.ground {
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
        let pits_here = if n == building.ground {
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
        if n == building.ground {
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

// ─────────────────── oclusores intra-espacio (2026-09-04) ───────────────────
//
// La medida que abre esta pasada: con la masa interior de ADR-105 enm. 3 ya puesta, la isovista
// mediana del mundo servido sigue en 249 m² y el clustering de VGA en 0,797 — o sea, salas-caja
// convexas: desde cualquier punto se ve casi todo lo que hay, y lo que se ve se ve entre sí. La
// masa existente entra en espacios de 90 m² y sólo con `PARTITION_ROOM_CHANCE` 0,80 y una división
// por cada 170 m², así que la sala normal se queda con UNA pieza de obra y el resto es aire.
//
// Esto no sustituye a aquello: se añade por debajo, en el tramo de 60 a 90 m² que aquélla no toca y
// dentro de las grandes que ya llevan división, con piezas más pequeñas y más numerosas.

/// Superficie mínima para que un espacio admita oclusores, en m². El encargo pide «> 60 m²».
const OCCLUDER_MIN_AREA_M2: f32 = 60.0;

/// Grosor de un divisor u oclusor de esta pasada, en centímetros.
///
/// **Cuarenta, y NO los treinta de una división de ADR-105 enm. 3.** El grosor es lo que identifica
/// a cada emisor de macizos en los tests ya validados —el pretil mide 20, el pilar 200 y la división
/// 30, y `partitions_land_where_the_grammar_says` filtra por el eje fino igual a 30—. Un oclusor de
/// 30 entraría en ese filtro y se mediría con la gramática de otra pasada. Cuarenta lo deja fuera
/// sin tocar un solo test.
/// **Fusión 2026-09-06:** en main el 40 ya lo tienen la viga (`BEAM_T_CM`) y la arcada
/// (`ARCADE_T_CM`), y `occluder_tests::ours` reconoce esta pasada por el eje fino. Pasa a 45, que
/// no lo produce ningún otro emisor.
const OCCLUDER_T_CM: i32 = 45;

/// Lado de un pilar de esta pasada, en centímetros. Ochenta por el mismo motivo que el grosor: el
/// pilar de nave mide 200 y los tests lo filtran por ese número exacto.
const OCCLUDER_PILLAR_CM: i32 = 80;

/// Paso libre que se le exige a un divisor en su extremo suelto, en centímetros.
///
/// El encargo pide 120 cm a cada lado. **Se usan 400**, que es lo que `PARTITION_GAP_CM` tiene
/// medido sobre el ráster servido: con 150 cm salían hasta 2 islas y la mancha mayor bajaba al
/// 98,0 %, con 400 la mancha es del 100 %. 120 es el suelo del encargo, no un objetivo, y quedarse
/// en el suelo es exactamente cómo se fabrica una región partida en dos.
const OCCLUDER_CLEAR_CM: i32 = 400;

/// Lo que un oclusor exento deja libre alrededor, en centímetros. Mismo criterio que
/// `PARTITION_ISLAND_CLEAR_CM`: no es un paso entre dos mitades, es el rodeo alrededor de una pieza
/// que ya se bordea por los cuatro lados.
const OCCLUDER_ISLAND_CLEAR_CM: i32 = 250;

/// Longitud mínima y máxima de un divisor, en centímetros. Por debajo del mínimo no oculta nada;
/// por encima del máximo deja de ser parcial y parte la sala, que es lo que esta pasada NO hace.
const OCCLUDER_MIN_LEN_CM: i32 = 250;
const OCCLUDER_MAX_LEN_CM: i32 = 900;

/// Fracción del vano que recorre un divisor anclado a pared, mínimo y máximo.
///
/// **Es el corazón del encargo**: «un pilar en el centro apenas oculta nada; un divisor parcial que
/// nace de una pared y avanza hacia el interior oculta mucho». Un espolón que recorre más de la
/// mitad del vano corta toda línea de visión que cruce ese eje sin cerrar el paso, porque el otro
/// extremo sigue abierto.
const OCCLUDER_SPUR_SPAN: (f32, f32) = (0.40, 0.70);

/// Reparto acumulado de los tres tipos: DIVISOR ANCLADO, MEDIA PARED y PILAR.
///
/// Manda el divisor anclado, por lo que dice el encargo. La media pared —exenta, a media altura—
/// es la que rompe la convexidad en mitad de la sala sin cerrar la lectura del volumen, y el pilar
/// va el último porque es el que menos oculta.
const OCCLUDER_SPUR_BELOW: f32 = 0.55;
const OCCLUDER_HALFWALL_BELOW: f32 = 0.85;

/// Altura de una media pared, en centímetros. Por encima de los ojos (1,60 m), así que corta la
/// línea de visión entera; por debajo del techo, así que el volumen de la sala se sigue leyendo.
const OCCLUDER_HALFWALL_H_CM: i32 = 210;

/// Distancia mínima entre un oclusor y el punto de un vano, en centímetros. Mismo criterio y mismo
/// motivo que `PARTITION_DOOR_CLEAR_CM`: obra delante de una puerta la tapia sin que nada se entere.
const OCCLUDER_DOOR_CLEAR_CM: i32 = 350;

/// Margen contra la pared paralela, en centímetros: lo que queda de sala al otro lado.
const OCCLUDER_WALL_MARGIN_CM: i32 = 250;

/// **Separación entre un vano y la pantalla que lo sombrea**, mínimo y máximo, en centímetros.
///
/// Es el ancho del canal que queda entre la pared del vano y la pantalla: por ahí se entra y por ahí
/// se sale hacia el extremo suelto. El encargo pide 120 cm de paso; el suelo aquí es 250 por lo
/// mismo que `OCCLUDER_CLEAR_CM` — 120 es el mínimo del cuerpo, no el de una entrada por la que
/// además se cruza a oscuras.
const OCC_SHADOW_OFFSET_MIN_CM: i32 = 250;
const OCC_SHADOW_OFFSET_MAX_CM: i32 = 500;

/// Cuánto tiene que pasarse la pantalla del centro del vano para taparlo de verdad, en centímetros.
///
/// Medio vano son 120; con 150 la pantalla sobresale por el lado por el que se rodea, así que la
/// recta que entra por el vano no encuentra sala al otro lado sino canto de pantalla.
const OCC_SHADOW_OVERHANG_CM: i32 = 150;

/// Lo que tiene que quedar de sala MÁS ALLÁ de la pantalla para que ponerla tenga sentido, en
/// centímetros. Por debajo de esto la pantalla no sombrea un espacio: lo tapia.
const OCC_SHADOW_ROOM_BEYOND_CM: i32 = 250;

/// Tope de oclusores por espacio. Un espacio con más obra que esto no es una sala con oclusores:
/// es un laberinto, y el encargo pide romper la convexidad, no cerrar el sitio.
const OCCLUDER_MAX_PER_SPACE: i32 = 6;

/// **Metros cuadrados de espacio por oclusor, POR PAPEL** — la densidad que el encargo pide exponer
/// «por RoomType». Cuanto más bajo, más obra.
///
/// El reparto no es de gusto: sale de para qué sirve cada sitio. Un almacén está lleno de estantes
/// y es donde más se justifica la obra suelta; una oficina lleva mamparas; un callejón sin salida es
/// el sitio raro y se le deja densidad alta para que lo sea de verdad. Una nave (`Hall`) va más
/// suelta porque ya lleva retícula de pilares, y la circulación no lleva NINGUNO —un oclusor en la
/// espina es el fallo de conectividad de siempre con otro nombre—.
fn occluder_area_per_one_m2(role: SpaceRole) -> Option<f32> {
    match role {
        SpaceRole::Office => Some(38.0),
        SpaceRole::Storage => Some(30.0),
        SpaceRole::Service => Some(34.0),
        SpaceRole::DeadEnd => Some(32.0),
        SpaceRole::Hall => Some(55.0),
        // Circulación, escaleras y vacío: nunca.
        SpaceRole::Spine | SpaceRole::Corridor | SpaceRole::Stair | SpaceRole::Void => None,
    }
}

/// Multiplicador global de la densidad de oclusores. 1.0 es lo servido; 0.0 apaga la pasada entera.
pub const OCCLUDER_DENSITY: f32 = 1.0;

/// Sal de la gramática de oclusores de un ESPACIO.
const SALT_OCCLUDER_SPACE: u32 = 0xB1_11_A0_03;
/// Sal del sorteo de CADA oclusor.
const SALT_OCCLUDER_ONE: u32 = 0xB1_11_A0_04;

/// **Oclusores intra-espacio**: divisores anclados a una pared, medias paredes exentas y pilares
/// pequeños, dentro de los espacios de más de [`OCCLUDER_MIN_AREA_M2`].
///
/// # Qué rompe, y por qué no es «más masa»
///
/// Lo que se busca no es llenar: es que desde un punto de la sala no se vea la sala entera. Un pilar
/// en el centro deja cuatro cuadrantes que se ven todos entre sí y el clustering de VGA no se mueve;
/// un divisor que NACE DE UNA PARED y avanza hacia dentro parte el conjunto visible en dos mitades
/// que no se ven entre ellas, y ésa es la métrica que el encargo pide bajar. Por eso el reparto de
/// tipos manda el espolón ([`OCCLUDER_SPUR_BELOW`]) y el pilar es el residuo.
///
/// # La conectividad no se confía
///
/// Ningún oclusor de esta pasada cruza su vano: el anclado deja [`OCCLUDER_CLEAR_CM`] en su extremo
/// suelto, y el exento deja [`OCCLUDER_ISLAND_CLEAR_CM`] a los cuatro lados. No hay un solo tipo que
/// pueda partir un espacio, que es la diferencia con la partición de ADR-105 enm. 3 —aquélla sí
/// cruza, y por eso hay como mucho una por espacio—.
///
/// # Todo sorteo parte de (chunk, espacio, índice)
///
/// El encargo lo pide así y la regla R3 del módulo pide que parta de la POSICIÓN: se cumplen las
/// dos, porque el «espacio» entra como el centro de su huella —que es su posición— y el chunk sale
/// de ese mismo centro. El índice sólo separa un oclusor del siguiente DENTRO del mismo espacio. No
/// hay ningún contador global: dos regiones vecinas producen el mismo oclusor en el mismo sitio sin
/// hablarse.
/// Por qué borde de `r` cae un punto de su perímetro. Mismos números que `plan::side_of_point_in`:
/// 0 = z máxima, 1 = x máxima, 2 = z mínima, 3 = x mínima. `None` si no cae en ninguno.
fn side_of_door_on(r: &super::plan::PlanRect, door: (i32, i32)) -> Option<u8> {
    const EPS: i32 = 2;
    let (x, z) = door;
    if (r.max_z_cm - z).abs() <= EPS {
        return Some(0);
    }
    if (r.max_x_cm - x).abs() <= EPS {
        return Some(1);
    }
    if (r.min_z_cm - z).abs() <= EPS {
        return Some(2);
    }
    if (r.min_x_cm - x).abs() <= EPS {
        return Some(3);
    }
    None
}

/// **La pantalla que sombrea un vano**: un divisor anclado a una pared lateral, plantado a unos
/// metros por dentro del vano y paralelo a la pared que lo aloja.
///
/// # Por qué ésta y no una repartida por la sala
///
/// Lo que llena la isovista de un punto cualquiera no es la sala en la que está: es lo que se ve
/// POR LOS VANOS de las salas de al lado. La medida de `layout_metrics_occ` lo dice sin discutirlo
/// —las celdas pisables sólo bajaron un 0,8 %, o sea que la obra repartida apenas tapa suelo, y la
/// isovista mediana se quedó en 227 m²—. Una pantalla delante del vano corta esa recta en su único
/// cuello: el hueco por el que la sala de al lado entra en la cuenta.
///
/// # Qué devuelve, y cómo no corta el paso
///
/// `(across_x, at, desde, hasta)` en las mismas coordenadas que usa el resto de la pasada. La
/// pantalla nace en la pared lateral MÁS CERCANA al vano y muere en el aire tras pasarse
/// [`OCC_SHADOW_OVERHANG_CM`] del centro del hueco: se entra, se topa uno con ella y se rodea por su
/// extremo suelto, que deja [`OCCLUDER_CLEAR_CM`] hasta la pared de enfrente. El canal entre el vano
/// y la pantalla mide [`OCC_SHADOW_OFFSET_MIN_CM`] como poco.
///
/// `None` cuando la sala no da para ello: sin sitio más allá de la pantalla, sin largo mínimo, o
/// con la pantalla tan larga que ya no dejaría por dónde rodearla.
fn shadow_baffle(
    host: &super::plan::PlanRect,
    side: u8,
    door: (i32, i32),
    offset_cm: i32,
) -> Option<(bool, i32, i32, i32)> {
    // El eje sobre el que CORRE la pantalla es el de la pared del vano; `across_x` es la convención
    // del resto de la pasada: cierto = la pantalla corre en Z y su grosor va en X.
    let across_x = side == 1 || side == 3;
    let (depth, at) = match side {
        0 => (host.depth_cm(), host.max_z_cm - offset_cm - OCCLUDER_T_CM),
        2 => (host.depth_cm(), host.min_z_cm + offset_cm),
        1 => (host.width_cm(), host.max_x_cm - offset_cm - OCCLUDER_T_CM),
        _ => (host.width_cm(), host.min_x_cm + offset_cm),
    };
    if depth - offset_cm - OCCLUDER_T_CM < OCC_SHADOW_ROOM_BEYOND_CM {
        return None;
    }
    let (lo, hi, dc) = if across_x {
        (host.min_z_cm, host.max_z_cm, door.1)
    } else {
        (host.min_x_cm, host.max_x_cm, door.0)
    };
    if dc <= lo || dc >= hi {
        return None;
    }
    let room = hi - lo;
    // Ancla en la pared lateral más cercana al vano: es la que deja la pantalla más corta, o sea la
    // que menos paso se come para el mismo sombreado.
    let from_lo = dc + OCC_SHADOW_OVERHANG_CM - lo;
    let from_hi = hi - (dc - OCC_SHADOW_OVERHANG_CM);
    let anchor_lo = from_lo <= from_hi;
    let want = if anchor_lo { from_lo } else { from_hi };
    let len = want.min(room - OCCLUDER_CLEAR_CM).min(OCCLUDER_MAX_LEN_CM);
    if len < OCCLUDER_MIN_LEN_CM {
        return None;
    }
    let (from, to) = if anchor_lo {
        (lo, lo + len)
    } else {
        (hi - len, hi)
    };
    // Y tras el recorte tiene que SEGUIR tapando el vano: media hoja de puerta pasada del centro. Si
    // el recorte se la comió, esta pantalla no sombrea nada y no se pone.
    let half_leaf = super::plan::DOORWAY_CM / 2;
    let covers = if anchor_lo {
        to >= dc + half_leaf
    } else {
        from <= dc - half_leaf
    };
    if !covers {
        return None;
    }
    Some((across_x, at, from, to))
}

#[allow(clippy::too_many_arguments)]
fn interior_occluders(
    building: &RegionBuilding,
    manifest: &Wg3Manifest,
    placements: &[Wg3Placement],
    pillars: &[Wg3Solid],
    partitions: &[Wg3Solid],
    seg_doors: &[(i32, i32, i32)],
    carves: &[Wg3Carve],
    segments: &[Wg3Segment],
    density: f32,
) -> Vec<Wg3Solid> {
    let mut out = Vec::new();
    if density <= 0.0 {
        return out;
    }
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
    // Los macizos que ya existen, como rectángulos de centímetros: un oclusor encima de un pilar o
    // de una división es una caja rara, no dos elementos.
    let solid_rect = |s: &Wg3Solid| super::plan::PlanRect {
        min_x_cm: s.x_cm,
        min_z_cm: s.z_cm,
        max_x_cm: s.x_cm + s.size_x_cm,
        max_z_cm: s.z_cm + s.size_z_cm,
    };
    let existing: Vec<super::plan::PlanRect> =
        pillars.iter().chain(partitions).map(&solid_rect).collect();

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
        // Las mismas dos renuncias que `interior_partitions`, por las mismas medidas: el espacio al
        // que LLEGA una escalera y el que abre una puerta de junta se dejan enteros en paz. Aquí
        // ningún oclusor cruza, así que en teoría no haría falta; se mantiene porque lo que encierra
        // la celda de una puerta de junta no es la obra sola, es la obra MÁS el borde de la región.
        let arrivals: Vec<usize> = building
            .wells
            .iter()
            .filter(|w| w.storey_below + 1 == n)
            .map(|w| w.space_above)
            .collect();
        let gated: Vec<usize> = plan.gates.iter().map(|g| g.space).collect();
        // ADR-126 D5 — la rejilla de pozos de la planta baja (fusión 2026-09-06): un oclusor
        // encima de un pozo es un macizo sobre un agujero.
        let pits_here = if n == building.ground {
            pit_rects_of(building, segments)
        } else {
            Vec::new()
        };

        for (i, s) in plan.built() {
            let Some(area_per_one) = occluder_area_per_one_m2(s.role) else {
                continue;
            };
            if s.rise_cm != 0 || arrivals.contains(&i) || gated.contains(&i) {
                continue;
            }
            if s.area_m2() < OCCLUDER_MIN_AREA_M2 {
                continue;
            }

            // La cuenta de oclusores del espacio sale de su ÁREA y su papel, no de un dado: la
            // densidad es el parámetro que el encargo pide exponer, y sortear además cuántos pone
            // metería una segunda perilla que nadie podría ajustar por separado. Lo que se sortea es
            // dónde y de qué tipo va cada uno, oclusor a oclusor.
            let want = ((s.area_m2() * density / area_per_one).round() as i32)
                .clamp(1, OCCLUDER_MAX_PER_SPACE);

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
                // ADR-105 enm. 9 — y las bocas de los tramos emitidos, que son las únicas que
                // saben dónde cae de verdad la boca de una ruta o una puerta rescatada.
                .chain(
                    seg_doors
                        .iter()
                        .filter(|&&(x, z, floor)| floor == s.floor_y_cm && on_space_wall(s, x, z))
                        .map(|&(x, z, _)| (x, z)),
                )
                .collect();

            // **El orden de los vanos es el del MUNDO, no el del plan.** Se ordena por coordenada
            // para que dos regiones vecinas sombreen los mismos huecos aunque el plan las haya
            // enumerado distinto: el índice `k` sólo puede significar algo si la lista es estable.
            let mut shaded = doors.clone();
            shaded.sort_unstable();

            let clear = clear_height_cm(s);
            let style = style_of(s.role);
            // El agujero de forjado del centro, que `hole_carves` estampa siempre centrado: un
            // macizo es inmune a los vanos, así que obra ahí sale ENCIMA del hueco.
            let r = s.rect;
            let hole = super::plan::PlanRect {
                min_x_cm: r.min_x_cm + (r.width_cm() - HOLE_SIDE_CM) / 2,
                min_z_cm: r.min_z_cm + (r.depth_cm() - HOLE_SIDE_CM) / 2,
                max_x_cm: r.min_x_cm + (r.width_cm() + HOLE_SIDE_CM) / 2,
                max_z_cm: r.min_z_cm + (r.depth_cm() + HOLE_SIDE_CM) / 2,
            };
            let mut mine: Vec<super::plan::PlanRect> = Vec::new();

            // Las partes de mayor a menor, una por oclusor y en círculo: mismo reparto que la masa
            // interior, y por la misma razón medida —sorteando la parte, media tirada cae en el
            // brazo estrecho de una L y se pierde el oclusor entero—.
            let mut hosts: Vec<super::plan::PlanRect> = s.parts().to_vec();
            hosts.sort_unstable_by_key(|p| -(p.area_m2() as i64));

            for k in 0..want as usize {
                let host = hosts[k % hosts.len()];
                // El índice entra en el sorteo de CADA oclusor, y sólo ahí: separa el oclusor k del
                // k+1 dentro del mismo espacio sin que ningún contador global cruce de sala a sala.
                let mut one = super::hash::stream_at(
                    seed.wrapping_add(k as i32),
                    host.centre_m().0,
                    host.centre_m().1,
                    SALT_OCCLUDER_ONE,
                );

                // **LOS VANOS PRIMERO.** Mientras queden vanos de este espacio sin sombrear, el
                // oclusor va delante de uno; sólo cuando se acaban se reparte por la sala con la
                // gramática de siempre. Es el cambio de criterio entero: lo que llena una isovista
                // no es la sala en la que se está, sino lo que se ve por los huecos.
                let target = shaded.get(k).copied();
                let offset = OCC_SHADOW_OFFSET_MIN_CM
                    + (one.next01() * (OCC_SHADOW_OFFSET_MAX_CM - OCC_SHADOW_OFFSET_MIN_CM) as f32)
                        as i32;
                // **Y la pantalla se planta en la parte que TIENE el vano, no en la que le tocaba
                // por turno.** Sobre una huella compuesta, `hosts[k % n]` rota entre los brazos de
                // la L y el vano casi nunca cae en el que toca: con el turno salían 525 vanos
                // sombreados de 2598, o sea que cuatro de cada cinco pantallas se caían por buscar
                // la puerta en el brazo equivocado y acababan de oclusor repartido.
                let shadow = target.and_then(|d| {
                    let part = hosts.iter().find(|p| side_of_door_on(p, d).is_some())?;
                    let side = side_of_door_on(part, d)?;
                    shadow_baffle(part, side, d, offset)
                });

                let kind = one.next01();
                let across_x = one.next01() < 0.5;
                // `span` es el vano que el oclusor cruzaría de lado a lado; `room_side` el
                // perpendicular, sobre el que se elige a qué altura de la sala se planta.
                let (span, room_side) = if across_x {
                    (host.depth_cm(), host.width_cm())
                } else {
                    (host.width_cm(), host.depth_cm())
                };
                let free = room_side - 2 * OCCLUDER_WALL_MARGIN_CM - OCCLUDER_T_CM;
                if free <= 0 || span < OCCLUDER_MIN_LEN_CM + OCCLUDER_CLEAR_CM {
                    continue;
                }
                let at_base = if across_x {
                    host.min_x_cm
                } else {
                    host.min_z_cm
                };
                let at = at_base + OCCLUDER_WALL_MARGIN_CM + (one.next01() * free as f32) as i32;
                let from_base = if across_x {
                    host.min_z_cm
                } else {
                    host.min_x_cm
                };

                let (run_from, run_to, thickness_along) = if kind < OCCLUDER_SPUR_BELOW {
                    // DIVISOR ANCLADO: nace en una de las dos paredes del vano y avanza hacia
                    // dentro. Nunca llega a la de enfrente: `OCCLUDER_CLEAR_CM` es el paso que
                    // queda, y es lo que hace que no pueda desconectar nada.
                    let f = OCCLUDER_SPUR_SPAN.0
                        + one.next01() * (OCCLUDER_SPUR_SPAN.1 - OCCLUDER_SPUR_SPAN.0);
                    let len = ((span as f32 * f) as i32)
                        .min(span - OCCLUDER_CLEAR_CM)
                        .min(OCCLUDER_MAX_LEN_CM);
                    if len < OCCLUDER_MIN_LEN_CM {
                        continue;
                    }
                    if one.next01() < 0.5 {
                        (from_base, from_base + len, OCCLUDER_T_CM)
                    } else {
                        (from_base + span - len, from_base + span, OCCLUDER_T_CM)
                    }
                } else if kind < OCCLUDER_HALFWALL_BELOW {
                    // MEDIA PARED: exenta, despegada de las dos paredes del vano. Rompe la línea de
                    // visión en mitad de la sala y se bordea por los dos extremos.
                    let f = OCCLUDER_SPUR_SPAN.0
                        + one.next01() * (OCCLUDER_SPUR_SPAN.1 - OCCLUDER_SPUR_SPAN.0);
                    let len = ((span as f32 * f) as i32)
                        .min(span - 2 * OCCLUDER_ISLAND_CLEAR_CM)
                        .min(OCCLUDER_MAX_LEN_CM);
                    if len < OCCLUDER_MIN_LEN_CM {
                        continue;
                    }
                    let slack = span - len - 2 * OCCLUDER_ISLAND_CLEAR_CM;
                    let a =
                        from_base + OCCLUDER_ISLAND_CLEAR_CM + (one.next01() * slack as f32) as i32;
                    (a, a + len, OCCLUDER_T_CM)
                } else {
                    // PILAR: el residuo. Cuadrado, exento, y por eso mismo el que menos oculta.
                    let slack = span - 2 * OCCLUDER_ISLAND_CLEAR_CM - OCCLUDER_PILLAR_CM;
                    if slack < 0 {
                        continue;
                    }
                    let a =
                        from_base + OCCLUDER_ISLAND_CLEAR_CM + (one.next01() * slack as f32) as i32;
                    (a, a + OCCLUDER_PILLAR_CM, OCCLUDER_PILLAR_CM)
                };

                // Y si había pantalla, manda ella: la tirada de arriba sólo sirvió para el reparto
                // de tipos del camino alternativo.
                let (across_x, at, run_from, run_to, thickness_along, is_spur, screen) =
                    match shadow {
                        Some((sx, sat, from, to)) => {
                            (sx, sat, from, to, OCCLUDER_T_CM, true, false)
                        }
                        None => (
                            across_x,
                            at,
                            run_from,
                            run_to,
                            thickness_along,
                            kind < OCCLUDER_SPUR_BELOW,
                            (OCCLUDER_SPUR_BELOW..OCCLUDER_HALFWALL_BELOW).contains(&kind),
                        ),
                    };
                let foot = if across_x {
                    super::plan::PlanRect {
                        min_x_cm: at,
                        min_z_cm: run_from,
                        max_x_cm: at + thickness_along,
                        max_z_cm: run_to,
                    }
                } else {
                    super::plan::PlanRect {
                        min_x_cm: run_from,
                        min_z_cm: at,
                        max_x_cm: run_to,
                        max_z_cm: at + thickness_along,
                    }
                };

                // La media pared y el pilar flotan, así que se les exige suelo propio alrededor. Al
                // divisor anclado NO: nace pegado a una pared, e inflarlo lo saca del espacio por
                // definición — es la misma nota que `interior_partitions` dejó escrita tras perder
                // dos tercios de la gramática sobre huella compuesta.
                let with_gap = foot.shrunk(-OCCLUDER_ISLAND_CLEAR_CM);
                let near_door = foot.shrunk(-OCCLUDER_DOOR_CLEAR_CM);
                let overlaps_taken = |x0: f32, z0: f32, x1: f32, z1: f32| {
                    let (a0, b0, a1, b1) = (
                        foot.min_x_cm as f32 / CM_PER_M,
                        foot.min_z_cm as f32 / CM_PER_M,
                        foot.max_x_cm as f32 / CM_PER_M,
                        foot.max_z_cm as f32 / CM_PER_M,
                    );
                    a0 < x1 && a1 > x0 && b0 < z1 && b1 > z0
                };
                let blocked = !s.covers_rect(&foot)
                    || (!is_spur && !s.covers_rect(&with_gap))
                    || landings.iter().any(|l| l.overlaps(&foot))
                    || wells_here.iter().any(|w| w.overlaps(&foot))
                    || pits_here.iter().any(|p| p.overlaps(&foot))
                    // ADR-105 enm. 12 — detrás de una ventana o una rendija no va obra: se vería
                    // el macizo a la altura de los ojos en vez de la sala de al lado.
                    || on_wall_carve(&foot, s, carves)
                    || (n > 0 && hole.shrunk(-50).overlaps(&foot))
                    || mine
                        .iter()
                        .any(|m| m.shrunk(-OCCLUDER_CLEAR_CM).overlaps(&foot))
                    || existing
                        .iter()
                        .any(|e| e.shrunk(-OCCLUDER_CLEAR_CM).overlaps(&foot))
                    // **La puerta que se sombrea es la excepción, y es el sentido de la pasada.**
                    // La pantalla se planta justo delante de ella; lo que la mantiene practicable no
                    // es la distancia, es el canal de `OCC_SHADOW_OFFSET_MIN_CM` que le queda por
                    // delante y el extremo suelto por el que se rodea. Las DEMÁS puertas se siguen
                    // esquivando enteras.
                    || doors
                        .iter()
                        .filter(|&&d| shadow.is_none() || Some(d) != target)
                        .any(|&(dx, dz)| near_door.contains_point(dx, dz))
                    || taken
                        .iter()
                        .any(|&(x0, z0, x1, z1)| overlaps_taken(x0, z0, x1, z1));
                if blocked {
                    continue;
                }
                mine.push(foot);

                // Una pantalla de vano nunca es media pared: lo que corta es la recta que entra
                // por el hueco, y por encima de una media pared se sigue viendo la sala entera.
                let top = if screen {
                    s.floor_y_cm + OCCLUDER_HALFWALL_H_CM.min(clear)
                } else {
                    s.floor_y_cm + clear
                };

                // A cajas de `MAX_SOLID_CM`, a partes iguales: un macizo se dibuja en el chunk de su
                // centro, y cortar avaricioso deja muñones (ver la nota de `interior_partitions`).
                let along = run_to - run_from;
                let boxes = (along + MAX_SOLID_CM - 1) / MAX_SOLID_CM;
                let mut cut = run_from;
                for b in 1..=boxes {
                    let end = run_from + (along * b) / boxes;
                    let (x, z, sx, sz) = if across_x {
                        (at, cut, thickness_along, end - cut)
                    } else {
                        (cut, at, end - cut, thickness_along)
                    };
                    out.push(Wg3Solid {
                        x_cm: x,
                        z_cm: z,
                        size_x_cm: sx,
                        size_z_cm: sz,
                        bottom_y_cm: s.floor_y_cm,
                        top_y_cm: top,
                        style,
                        yaw_deg: 0,
                        shape: SHAPE_BOX,
                    });
                    cut = end;
                }
            }
        }
    }
    out
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
                    let Some(plan_up) = plan_up else {
                        continue;
                    };
                    // **Y sólo el TRAMO de la banda que tiene sala encima** (2026-09-06). La banda
                    // se prolonga `grow` más allá de la huella por los dos extremos y por eso pasa
                    // por el muro que el atrio comparte con su vecino; si ese vecino es OTRO atrio,
                    // la banda entera cortada le abre a él el muro alto por un lado en el que
                    // arriba no hay nadie — el mismo agujero a la nada que esta enmienda vino a
                    // cerrar, visto desde la sala de al lado. Lo destapó la desalineación de vanos
                    // en la región (−1,2): dos naves de doble altura pared con pared, y encima de
                    // una sola de ellas sala. Ver [`atrium_open_runs`].
                    let along_x = side.width_cm() >= side.depth_cm();
                    let merged = atrium_open_runs(&side, plan_up, grow);
                    for (a, b) in merged {
                        let (x, z, sx, sz) = if along_x {
                            (a, side.min_z_cm, b - a, side.depth_cm())
                        } else {
                            (side.min_x_cm, a, side.width_cm(), b - a)
                        };
                        out.push(Wg3Carve {
                            x_cm: x,
                            z_cm: z,
                            size_x_cm: sx,
                            size_z_cm: sz,
                            bottom_y_cm: bottom,
                            top_y_cm: top,
                        });
                    }
                }
            }
        }
    }
    out
}

/// ADR-104 enm. 3 (fusión 2026-09-06) — **los tramos de una banda de atrio que tienen sala
/// construida encima**, en centímetros a lo largo de la banda: bajo cada parte de cada sala de
/// arriba, con `grow` de holgura a cada lado y fundidos si se tocan. Es lo que abre
/// [`atrium_carves`] y lo único sobre lo que [`atrium_solids`] pone pretil: si los dos no miden
/// lo mismo, el pretil queda con muro encima o el vano sin pretil.
///
/// **Sin las esquinas.** La banda sobresale `grow` de la huella por los dos extremos, y una sala de
/// arriba que sólo pisa ese sobrante está AL LADO del atrio, no encima: con la banda entera, un
/// pasillo que pasaba de largo a 50 cm del atrio abría su lado completo.
fn atrium_open_runs(
    side: &super::plan::PlanRect,
    plan_up: &super::plan::RegionPlan,
    grow: i32,
) -> Vec<(i32, i32)> {
    let along_x = side.width_cm() >= side.depth_cm();
    let (lo, hi) = if along_x {
        (side.min_x_cm, side.max_x_cm)
    } else {
        (side.min_z_cm, side.max_z_cm)
    };
    let mut inner = *side;
    if along_x {
        inner.min_x_cm += grow;
        inner.max_x_cm -= grow;
    } else {
        inner.min_z_cm += grow;
        inner.max_z_cm -= grow;
    }
    let mut runs: Vec<(i32, i32)> = plan_up
        .spaces
        .iter()
        .filter(|t| t.role.is_built() && t.hits_rect(&inner))
        .flat_map(|t| t.parts().to_vec())
        .filter(|p| p.overlaps(&inner))
        .map(|p| {
            let (a, b) = if along_x {
                (p.min_x_cm, p.max_x_cm)
            } else {
                (p.min_z_cm, p.max_z_cm)
            };
            ((a - grow).max(lo), (b + grow).min(hi))
        })
        .filter(|(a, b)| b > a)
        .collect();
    runs.sort_unstable();
    let mut merged: Vec<(i32, i32)> = Vec::new();
    for (a, b) in runs {
        match merged.last_mut() {
            Some(last) if a <= last.1 => last.1 = last.1.max(b),
            _ => merged.push((a, b)),
        }
    }
    merged
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
                if s.style == PIT_STYLE || s.style == PIT_SHAFT_STYLE || s.is_hidden() {
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
                        && !o.is_hidden()
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
    /// ADR-129 — el atrezo existe, cada ancla está dentro de su tramo, a un metro de toda boca, y
    /// cada mesa y cada archivador llevan su macizo invisible debajo (frenan igual en los dos
    /// lados). Los papeles no frenan.
    #[test]
    fn props_sit_in_their_room_and_off_everything() {
        let m = no_catalogue();
        let mut desks = 0usize;
        let mut total = 0usize;
        for seed in 1..40 {
            let b = building(seed);
            let f = fill_building(&b, &m);
            let mouths: Vec<(i32, i32, i32, i32)> = f
                .segments
                .iter()
                .flat_map(|g| {
                    let (w, d) = (g.size_x_cm as f32 / 100.0, g.size_z_cm as f32 / 100.0);
                    g.openings.iter().map(move |o| {
                        let (lx, lz) = super::super::placement::local_point(
                            o.side,
                            o.offset_cm as f32 / 100.0,
                            w,
                            d,
                        );
                        (
                            g.x_cm + (lx * 100.0).round() as i32,
                            g.z_cm + (lz * 100.0).round() as i32,
                            o.width_cm / 2 + 60,
                            g.floor_y_cm,
                        )
                    })
                })
                .collect();
            for p in &f.props {
                total += 1;
                let host_floor = f
                    .segments
                    .iter()
                    .find(|g| {
                        p.x_cm > g.x_cm + WALL_T_CM
                            && p.x_cm < g.x_cm + g.size_x_cm - WALL_T_CM
                            && p.z_cm > g.z_cm + WALL_T_CM
                            && p.z_cm < g.z_cm + g.size_z_cm - WALL_T_CM
                            && (p.y_cm - g.floor_y_cm) >= 0
                            // El tope ata el ancla a SU planta. Los 250 valían mientras todo el
                            // atrezo se apoyaba en algo; el deterioro de la enm. 19 cuelga del
                            // techo, así que el tope es la altura del tramo cuando es mayor.
                            && (p.y_cm - g.floor_y_cm) <= g.height_cm.max(250)
                    })
                    .map(|g| g.floor_y_cm);
                let inside = host_floor.is_some();
                assert!(inside, "semilla {seed}: ancla {p:?} fuera de todo tramo");
                if p.kind != PROP_PAPER {
                    assert!(
                        !mouths
                            .iter()
                            .any(|&(mx, mz, half, fl)| Some(fl) == host_floor
                                && (mx - p.x_cm).abs() < half
                                && (mz - p.z_cm).abs() < half),
                        "semilla {seed}: ancla {p:?} en una boca"
                    );
                }
                if p.kind == PROP_DESK || p.kind == PROP_CABINET {
                    desks += 1;
                    let under = f.solids.iter().any(|h| {
                        h.is_hidden()
                            && h.x_cm <= p.x_cm
                            && h.x_cm + h.size_x_cm >= p.x_cm
                            && h.z_cm <= p.z_cm
                            && h.z_cm + h.size_z_cm >= p.z_cm
                            && h.bottom_y_cm == p.y_cm
                    });
                    assert!(under, "semilla {seed}: {p:?} sin macizo invisible debajo");
                }
            }
            for h in f.solids.iter().filter(|h| h.is_hidden()) {
                assert!(
                    !h.is_decoration(),
                    "semilla {seed}: un macizo no puede ser invisible y decoración a la vez"
                );
            }
        }
        assert!(
            desks >= 30,
            "sólo {desks} mesas y archivadores en 39 semillas"
        );
        println!("[atrezo] {total} anclas, {desks} mesas y archivadores en 39 semillas");
    }

    /// La familia de una variante de cartel, para contar que salen las cinco.
    fn sign_family(v: u8) -> Option<usize> {
        match v % SIGN_GARBLED_BASE {
            x if x < SIGN_CUBICLE_TAG => Some(0),
            x if x < SIGN_EXIT => Some(1),
            x if x < SIGN_CORKBOARD => Some(2),
            x if x < SIGN_CALENDAR => Some(3),
            x if x < SIGN_CALENDAR + SIGN_CALENDAR_N => Some(4),
            _ => None,
        }
    }

    /// La normal hacia afuera de un cartel por su giro, en XZ. `yaw` 0 mira a +z.
    fn sign_normal(yaw: i16) -> (i32, i32) {
        match yaw {
            0 => (0, 1),
            90 => (1, 0),
            180 => (0, -1),
            _ => (-1, 0),
        }
    }

    #[test]
    fn signs_hang_flat_on_a_surface_and_never_on_a_door_or_a_window() {
        let m = no_catalogue();
        let mut total = 0usize;
        let mut families = [0usize; 5];
        for seed in 1..40 {
            let b = building(seed);
            let f = fill_building(&b, &m);
            for p in f.props.iter().filter(|p| p.kind == PROP_SIGN) {
                total += 1;
                let fam = sign_family(p.style);
                assert!(
                    fam.is_some(),
                    "semilla {seed}: variante de cartel sin familia: {p:?}"
                );
                families[fam.unwrap()] += 1;
                // El `variant` vive en los seis bits bajos: los dos altos son de los macizos.
                assert!(
                    p.style < 64,
                    "semilla {seed}: un cartel pisa los bits de macizo: {p:?}"
                );
                // Sin sótanos no hay profundidad, y sin profundidad el texto no se estropea.
                assert!(
                    p.style < SIGN_GARBLED_BASE,
                    "semilla {seed}: texto roto en la calle: {p:?}"
                );
                assert!(
                    matches!(p.yaw_deg, 0 | 90 | 180 | 270),
                    "semilla {seed}: un cartel no pegado a una cara: {p:?}"
                );
                let (w, h) = super::super::segment::sign_size_cm(p.style);
                let (y_lo, y_hi) = (p.y_cm - h / 2, p.y_cm + h / 2);
                let (nx, nz) = sign_normal(p.yaw_deg);
                // La banda que ocupa contra su superficie: desde la cara hacia dentro.
                let face = (p.x_cm - nx * SIGN_PROUD_CM, p.z_cm - nz * SIGN_PROUD_CM);
                let (ax, az) = (face.0 + nx * SIGN_BAND_CM, face.1 + nz * SIGN_BAND_CM);
                let band = plan::PlanRect {
                    min_x_cm: face.0.min(ax) - if nx == 0 { w / 2 } else { 0 },
                    max_x_cm: face.0.max(ax) + if nx == 0 { w / 2 } else { 0 },
                    min_z_cm: face.1.min(az) - if nz == 0 { w / 2 } else { 0 },
                    max_z_cm: face.1.max(az) + if nz == 0 { w / 2 } else { 0 },
                };
                let over_window = f.carves.iter().any(|k| {
                    k.bottom_y_cm < y_hi
                        && k.top_y_cm > y_lo
                        && k.x_cm < band.max_x_cm
                        && k.x_cm + k.size_x_cm > band.min_x_cm
                        && k.z_cm < band.max_z_cm
                        && k.z_cm + k.size_z_cm > band.min_z_cm
                });
                assert!(
                    !over_window,
                    "semilla {seed}: cartel sobre una ventana o un vano: {p:?}"
                );
            }
        }
        for (i, n) in families.iter().enumerate() {
            assert!(*n > 0, "la familia de carteles {i} no sale en 39 semillas");
        }
        assert!(total >= 200, "sólo {total} carteles en 39 semillas");
        println!("[carteles] {total} anclas, por familia {families:?} en 39 semillas");
    }

    #[test]
    fn only_the_basements_get_the_text_wrong() {
        let m = no_catalogue();
        let mut garbled = 0usize;
        let mut clean_above = 0usize;
        for seed in 1..20 {
            let b = plan::plan_building_at(
                seed,
                (0.0, 0.0, 150.0, 150.0),
                &[],
                4,
                plan::REGION_BASEMENTS,
            );
            let f = fill_building(&b, &m);
            for p in f.props.iter().filter(|p| p.kind == PROP_SIGN) {
                if p.style >= SIGN_GARBLED_BASE {
                    garbled += 1;
                    assert!(
                        p.y_cm < 0,
                        "semilla {seed}: texto roto sobre la calle: {p:?}"
                    );
                } else if p.y_cm >= 0 {
                    clean_above += 1;
                }
            }
        }
        assert!(garbled >= 20, "sólo {garbled} carteles rotos bajo tierra");
        assert!(
            clean_above >= 20,
            "sólo {clean_above} carteles buenos sobre la calle"
        );
        println!("[carteles] {garbled} rotos abajo, {clean_above} buenos arriba");
    }

    #[test]
    fn cubicles_fill_big_offices_and_stay_off_everything() {
        let m = no_catalogue();
        let mut walls = 0usize;
        let mut desks = 0usize;
        let mut rooms = 0usize;
        for seed in 1..40 {
            let b = building(seed);
            let f = fill_building(&b, &m);
            let mouths: Vec<(i32, i32, i32, i32)> = f
                .segments
                .iter()
                .flat_map(|g| {
                    let (w, d) = (g.size_x_cm as f32 / 100.0, g.size_z_cm as f32 / 100.0);
                    g.openings.iter().map(move |o| {
                        let (lx, lz) = super::super::placement::local_point(
                            o.side,
                            o.offset_cm as f32 / 100.0,
                            w,
                            d,
                        );
                        (
                            g.x_cm + (lx * 100.0).round() as i32,
                            g.z_cm + (lz * 100.0).round() as i32,
                            o.width_cm / 2 + 30,
                            g.floor_y_cm,
                        )
                    })
                })
                .collect();
            let mut seen: Vec<(i32, i32)> = Vec::new();
            for w in f.solids.iter().filter(|s| is_cubicle_wall(s)) {
                walls += 1;
                let r = rect_of(w);
                // Dentro de un tramo de su planta, sin pisar la cáscara.
                let host = f.segments.iter().find(|g| {
                    g.floor_y_cm == w.bottom_y_cm
                        && r.min_x_cm >= g.x_cm + WALL_T_CM
                        && r.max_x_cm <= g.x_cm + g.size_x_cm - WALL_T_CM
                        && r.min_z_cm >= g.z_cm + WALL_T_CM
                        && r.max_z_cm <= g.z_cm + g.size_z_cm - WALL_T_CM
                });
                assert!(
                    host.is_some(),
                    "semilla {seed}: mampara {r:?} fuera de todo tramo"
                );
                assert!(
                    !mouths.iter().any(|&(mx, mz, half, fl)| fl == w.bottom_y_cm
                        && mx + half > r.min_x_cm
                        && mx - half < r.max_x_cm
                        && mz + half > r.min_z_cm
                        && mz - half < r.max_z_cm),
                    "semilla {seed}: mampara {r:?} tapa una boca"
                );
                // Y sin pisar ningún macizo de otro emisor a ras de suelo.
                let hit = f.solids.iter().find(|o| {
                    !is_cubicle_wall(o)
                        && !o.is_hidden()
                        && !o.is_decoration()
                        && o.bottom_y_cm < w.bottom_y_cm + 200
                        && o.top_y_cm > w.bottom_y_cm
                        && rect_of(o).overlaps(&r)
                });
                assert!(hit.is_none(), "semilla {seed}: mampara {r:?} sobre {hit:?}");
                let key = (r.min_x_cm / 5000, r.min_z_cm / 5000);
                if !seen.contains(&key) {
                    seen.push(key);
                }
            }
            rooms += seen.len();
            desks += f
                .props
                .iter()
                .filter(|p| {
                    p.kind == PROP_DESK
                        && f.solids.iter().any(|w| {
                            is_cubicle_wall(w)
                                && (w.x_cm - p.x_cm).abs() < 300
                                && (w.z_cm - p.z_cm).abs() < 300
                        })
                })
                .count();
        }
        assert!(
            walls >= 200,
            "sólo {walls} mamparas en 39 semillas: la muestra no cubre el caso"
        );
        assert!(desks >= 40, "sólo {desks} mesas de cubículo en 39 semillas");
        println!("[cubículos] {walls} mamparas, {desks} puestos, ~{rooms} zonas en 39 semillas");
    }

    #[test]
    fn nothing_but_a_cubicle_wall_has_the_shape_of_one() {
        // La forma es la identidad del emisor: si otro macizo midiera 12 × 140, el clasificador de
        // los tests lo llamaría mampara y este test dejaría de medir lo que dice.
        let m = no_catalogue();
        for seed in [3, 11, 27] {
            let b = building(seed);
            let f = fill_building(&b, &m);
            for s in &f.solids {
                if s.size_x_cm.min(s.size_z_cm) == CUBICLE_T_CM {
                    assert_eq!(
                        s.top_y_cm - s.bottom_y_cm,
                        CUBICLE_H_CM,
                        "semilla {seed}: un macizo de 12 cm que no es una mampara: {s:?}"
                    );
                }
            }
        }
    }

    #[test]
    fn office_rooms_get_a_lower_ceiling_than_the_rest() {
        // Falso techo: los despachos del carácter de oficina piden 2,70–3,00 y ninguno menos.
        let mut low = 0usize;
        let mut office_rooms = 0usize;
        for seed in 1..40 {
            let b = building(seed);
            for st in &b.storeys {
                for (_, s) in st.built() {
                    let is_room = matches!(
                        s.role,
                        SpaceRole::Office | SpaceRole::Service | SpaceRole::Storage
                    );
                    if !is_room || s.ceiling_clear_cm == 0 {
                        continue;
                    }
                    if knobs_of(seed, s).office_ceiling_cm.1 > 0 {
                        office_rooms += 1;
                        assert!(
                            (270..=300).contains(&s.ceiling_clear_cm)
                                || s.ceiling_clear_cm <= ceiling_cap_cm(seed, s),
                            "semilla {seed}: despacho de oficina con techo {} cm",
                            s.ceiling_clear_cm
                        );
                        if s.ceiling_clear_cm < 300 {
                            low += 1;
                        }
                    }
                }
            }
        }
        assert!(
            office_rooms >= 100,
            "sólo {office_rooms} despachos de oficina en 39 semillas"
        );
        assert!(
            low * 3 >= office_rooms,
            "el falso techo no baja: {low} de {office_rooms} bajo 3,00"
        );
        println!("[falso techo] {low} de {office_rooms} despachos por debajo de 3,00");
    }

    /// El deterioro de la enm. 19, por su forma: `is_fallen_plate` y `is_raised_floor_tile` son las
    /// únicas puertas, así que nadie más puede emitir una caja de 60 × 60 de canto 4 o 9.
    #[test]
    fn the_decay_has_shapes_of_its_own() {
        let m = no_catalogue();
        let mut plates = 0usize;
        let mut tiles = 0usize;
        for seed in 1..20 {
            let b = plan::plan_building_at(
                seed,
                (0.0, 0.0, 150.0, 150.0),
                &[],
                4,
                plan::REGION_BASEMENTS,
            );
            let f = fill_building(&b, &m);
            for s in &f.solids {
                let h = s.top_y_cm - s.bottom_y_cm;
                // La forma es la HUELLA cuadrada de una placa más su canto: un rodapié de 4 cm de
                // canto también existe, pero mide 6,83 m de largo y 36 de fondo.
                if s.size_x_cm == CEILING_PLATE_CM
                    && s.size_z_cm == CEILING_PLATE_CM
                    && (h == FALLEN_PLATE_H_CM || h == RAISED_TILE_H_CM)
                {
                    assert!(
                        is_fallen_plate(s) || is_raised_floor_tile(s),
                        "semilla {seed}: un macizo de 60 × 60 y canto {h} que no es deterioro: {s:?}"
                    );
                }
                if is_fallen_plate(s) {
                    plates += 1;
                }
                if is_raised_floor_tile(s) {
                    tiles += 1;
                }
            }
        }
        assert!(
            plates >= 50 && tiles >= 5,
            "el deterioro no sale: {plates} placas caídas y {tiles} baldosas en 19 semillas"
        );
        println!("[deterioro] {plates} placas caídas, {tiles} baldosas levantadas en 19 semillas");
    }

    /// ADR-130 D4 — el deterioro SUBE con la profundidad: por sala, el sótano más hondo tiene que
    /// dar más piezas que la calle.
    #[test]
    fn the_decay_grows_with_depth() {
        let m = no_catalogue();
        let (mut street, mut street_rooms) = (0usize, 0usize);
        let (mut deep, mut deep_rooms) = (0usize, 0usize);
        for seed in 1..20 {
            let b = plan::plan_building_at(
                seed,
                (0.0, 0.0, 150.0, 150.0),
                &[],
                4,
                plan::REGION_BASEMENTS,
            );
            let f = fill_building(&b, &m);
            let rooms_at = |n: usize| {
                b.storeys[n]
                    .built()
                    .filter(|(_, s)| {
                        matches!(
                            s.role,
                            SpaceRole::Office | SpaceRole::Service | SpaceRole::Storage
                        ) && knobs_of(seed, s).office_ceiling_cm.1 > 0
                    })
                    .count()
            };
            let pieces_at = |floor: i32| {
                f.solids
                    .iter()
                    .filter(|s| {
                        (is_fallen_plate(s) || is_raised_floor_tile(s)) && s.bottom_y_cm == floor
                    })
                    .count()
            };
            let street_y = b.storeys[b.ground].spaces[0].floor_y_cm;
            street += pieces_at(street_y);
            street_rooms += rooms_at(b.ground);
            deep += pieces_at(b.storeys[0].spaces[0].floor_y_cm);
            deep_rooms += rooms_at(0);
        }
        assert!(
            street_rooms > 0 && deep_rooms > 0,
            "el barrido no tiene salas con falso techo arriba ({street_rooms}) o abajo ({deep_rooms})"
        );
        let (a, c) = (
            street as f32 / street_rooms as f32,
            deep as f32 / deep_rooms as f32,
        );
        println!("[deterioro] calle {a:.2} piezas por sala, B3 {c:.2}");
        assert!(
            c > a * 1.5,
            "el deterioro no crece hacia abajo: calle {a:.2}, fondo {c:.2}"
        );
    }

    /// Nada del deterioro pisa una boca ni un puesto de cubículos.
    #[test]
    fn the_decay_keeps_off_mouths_and_cubicles() {
        let m = no_catalogue();
        let mut checked = 0usize;
        for seed in 1..20 {
            let b = plan::plan_building_at(
                seed,
                (0.0, 0.0, 150.0, 150.0),
                &[],
                4,
                plan::REGION_BASEMENTS,
            );
            let f = fill_building(&b, &m);
            let floor_decay: Vec<&Wg3Solid> = f
                .solids
                .iter()
                .filter(|s| is_fallen_plate(s) || is_raised_floor_tile(s))
                .collect();
            // Las bocas de todos los tramos, con su holgura.
            for g in &f.segments {
                let (w, d) = (g.size_x_cm as f32 / 100.0, g.size_z_cm as f32 / 100.0);
                for o in &g.openings {
                    let (lx, lz) = crate::world::wg3::placement::local_point(
                        o.side,
                        o.offset_cm as f32 / 100.0,
                        w,
                        d,
                    );
                    let (mx, mz) = (
                        g.x_cm + (lx * 100.0).round() as i32,
                        g.z_cm + (lz * 100.0).round() as i32,
                    );
                    let half = o.width_cm / 2 + PROP_MOUTH_CLEAR_CM;
                    for s in &floor_decay {
                        if (s.bottom_y_cm - g.floor_y_cm).abs() >= 100 {
                            continue;
                        }
                        let hit = mx + half > s.x_cm
                            && mx - half < s.x_cm + s.size_x_cm
                            && mz + half > s.z_cm
                            && mz - half < s.z_cm + s.size_z_cm;
                        assert!(!hit, "semilla {seed}: deterioro sobre una boca: {s:?}");
                    }
                }
            }
            // Los cubículos: la huella de las mamparas de cada sala, sin nada del deterioro dentro.
            for storey in &b.storeys {
                for (_, sp) in storey.built() {
                    let walls: Vec<&Wg3Solid> = f
                        .solids
                        .iter()
                        .filter(|s| {
                            is_cubicle_wall(s)
                                && s.bottom_y_cm == sp.floor_y_cm
                                && sp.covers_rect(&plan::PlanRect {
                                    min_x_cm: s.x_cm,
                                    min_z_cm: s.z_cm,
                                    max_x_cm: s.x_cm + s.size_x_cm,
                                    max_z_cm: s.z_cm + s.size_z_cm,
                                })
                        })
                        .collect();
                    if walls.is_empty() {
                        continue;
                    }
                    checked += 1;
                    let (mut x0, mut z0, mut x1, mut z1) = (i32::MAX, i32::MAX, i32::MIN, i32::MIN);
                    for w in &walls {
                        x0 = x0.min(w.x_cm);
                        z0 = z0.min(w.z_cm);
                        x1 = x1.max(w.x_cm + w.size_x_cm);
                        z1 = z1.max(w.z_cm + w.size_z_cm);
                    }
                    for s in &floor_decay {
                        if s.bottom_y_cm != sp.floor_y_cm {
                            continue;
                        }
                        let hit = s.x_cm < x1
                            && s.x_cm + s.size_x_cm > x0
                            && s.z_cm < z1
                            && s.z_cm + s.size_z_cm > z0;
                        assert!(
                            !hit,
                            "semilla {seed}: deterioro dentro de los cubículos: {s:?}"
                        );
                    }
                }
            }
        }
        assert!(checked >= 10, "sólo {checked} salas con cubículos miradas");
    }

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

#[cfg(test)]
mod occluder_tests {
    use super::*;
    use crate::world::wg3::plan::{plan_building, PlanRect};

    const BOUNDS: (f32, f32, f32, f32) = (0.0, 0.0, 150.0, 150.0);
    const STOREYS: usize = 2;

    fn manifest() -> Wg3Manifest {
        crate::world::wg3::tests::real_manifest()
    }

    fn rect_of(s: &Wg3Solid) -> PlanRect {
        PlanRect {
            min_x_cm: s.x_cm,
            min_z_cm: s.z_cm,
            max_x_cm: s.x_cm + s.size_x_cm,
            max_z_cm: s.z_cm + s.size_z_cm,
        }
    }

    /// Los macizos que emite ESTA pasada: se reconocen por el eje fino, que es 40 (divisor y media
    /// pared) u 80 (pilar) y no lo produce ningun otro emisor -- pretil 20, division 30, pilar de
    /// nave 200.
    fn ours(f: &FilledRegion) -> Vec<&Wg3Solid> {
        f.solids
            .iter()
            .filter(|s| {
                let thin = s.size_x_cm.min(s.size_z_cm);
                thin == OCCLUDER_T_CM || (thin == OCCLUDER_PILLAR_CM && s.size_x_cm == s.size_z_cm)
            })
            .collect()
    }

    #[test]
    fn the_pass_inserts_occluders_and_the_knob_turns_it_off() {
        let m = manifest();
        let mut with = 0usize;
        for seed in 1..=12 {
            let b = plan_building(seed, BOUNDS, &[], STOREYS);
            let on = fill_building_with(&b, &m, OCCLUDER_DENSITY);
            let off = fill_building_with(&b, &m, 0.0);
            with += ours(&on).len();
            assert!(
                ours(&off).is_empty(),
                "semilla {seed}: con densidad 0 salieron {} oclusores",
                ours(&off).len()
            );
            assert!(
                on.solids.len() > off.solids.len(),
                "semilla {seed}: la pasada no anadio ni un macizo"
            );
        }
        assert!(
            with > 0,
            "la pasada no emitio un solo oclusor en 12 semillas"
        );
    }

    #[test]
    fn occluders_change_nothing_but_the_solids() {
        let m = manifest();
        for seed in 1..=12 {
            let b = plan_building(seed, BOUNDS, &[], STOREYS);
            let on = fill_building_with(&b, &m, OCCLUDER_DENSITY);
            let off = fill_building_with(&b, &m, 0.0);

            for st in &b.storeys {
                assert!(
                    st.problems().is_empty(),
                    "semilla {seed}: {:?}",
                    st.problems()
                );
            }
            assert_eq!(
                on.segments, off.segments,
                "semilla {seed}: cambiaron los tramos"
            );
            assert_eq!(
                on.carves, off.carves,
                "semilla {seed}: cambiaron los recortes"
            );
            assert_eq!(
                on.placements, off.placements,
                "semilla {seed}: cambiaron las piezas"
            );
            assert_eq!(
                on.links_failed, off.links_failed,
                "semilla {seed}: cambiaron los enlaces fallidos"
            );
            assert_eq!(
                on.gates_built, off.gates_built,
                "semilla {seed}: cambiaron las puertas de junta"
            );
            // Sólo los macizos que la pasada ESQUIVA (pilares y divisiones) tienen que ser los
            // mismos: los emisores que vienen después (bloques, pilastras, listones, atrezo)
            // esquivan a su vez `out.solids`, así que con oclusores puestos se reparten distinto.
            for s in off
                .solids
                .iter()
                .filter(|s| is_pillar(s) || s.size_x_cm.min(s.size_z_cm) == PARTITION_T_CM)
            {
                assert!(
                    on.solids.contains(s),
                    "semilla {seed}: la pasada se comio un pilar o una division"
                );
            }
        }
    }

    #[test]
    fn occluders_keep_their_clearances() {
        let m = manifest();
        for seed in 1..=12 {
            let b = plan_building(seed, BOUNDS, &[], STOREYS);
            let f = fill_building_with(&b, &m, OCCLUDER_DENSITY);
            let mine = ours(&f);
            for s in &mine {
                let r = rect_of(s);
                let mut host = None;
                for st in &b.storeys {
                    for (_, sp) in st.built() {
                        if sp.floor_y_cm == s.bottom_y_cm && sp.covers_rect(&r) {
                            host = Some(sp);
                        }
                    }
                }
                let Some(sp) = host else {
                    panic!(
                        "semilla {seed}: oclusor en ({},{}) cota {} fuera de todo espacio",
                        s.x_cm, s.z_cm, s.bottom_y_cm
                    )
                };
                assert!(
                    occluder_area_per_one_m2(sp.role).is_some(),
                    "semilla {seed}: oclusor en un espacio {}, que no lleva",
                    sp.role.name()
                );
                assert!(
                    sp.area_m2() >= OCCLUDER_MIN_AREA_M2,
                    "semilla {seed}: oclusor en un espacio de {:.1} m2",
                    sp.area_m2()
                );
                let (w, d) = (sp.rect.width_cm(), sp.rect.depth_cm());
                let (rw, rd) = (r.width_cm(), r.depth_cm());
                assert!(
                    rw <= w - OCCLUDER_CLEAR_CM || rd <= d - OCCLUDER_CLEAR_CM,
                    "semilla {seed}: oclusor {rw}x{rd} en una sala {w}x{d}: no deja paso"
                );
                let h = s.top_y_cm - s.bottom_y_cm;
                assert!(h > 0, "semilla {seed}: oclusor de altura {h}");
            }
            for (i, a) in mine.iter().enumerate() {
                for b2 in mine.iter().skip(i + 1) {
                    if a.bottom_y_cm != b2.bottom_y_cm {
                        continue;
                    }
                    assert!(
                        !rect_of(a).overlaps(&rect_of(b2)),
                        "semilla {seed}: dos oclusores se pisan"
                    );
                }
            }
        }
    }

    /// **La medida del cambio de criterio**: cuántos vanos de los que pueden llevar pantalla la
    /// llevan. Sin esto, la pasada podría no sombrear un solo hueco y los otros tres tests seguirían
    /// verdes — miden que lo que se pone está bien puesto, no que se ponga delante de un vano.
    #[test]
    fn doors_of_eligible_spaces_get_shaded() {
        let m = manifest();
        let mut doors = 0usize;
        let mut shaded = 0usize;
        for seed in 1..=12 {
            let b = plan_building(seed, BOUNDS, &[], STOREYS);
            let f = fill_building_with(&b, &m, OCCLUDER_DENSITY);
            let mine = ours(&f);
            for st in &b.storeys {
                for (i, sp) in st.built() {
                    if occluder_area_per_one_m2(sp.role).is_none()
                        || sp.area_m2() < OCCLUDER_MIN_AREA_M2
                    {
                        continue;
                    }
                    for l in st.links.iter().filter(|l| l.a == i || l.b == i) {
                        let (dx, dz) = (l.at_x_cm, l.at_z_cm);
                        doors += 1;
                        // Una pantalla del vano: a la distancia del canal, paralela a su pared y
                        // cubriendo su coordenada lateral.
                        let hit = mine.iter().any(|o| {
                            if o.bottom_y_cm != sp.floor_y_cm {
                                return false;
                            }
                            let (x0, z0) = (o.x_cm, o.z_cm);
                            let (x1, z1) = (o.x_cm + o.size_x_cm, o.z_cm + o.size_z_cm);
                            let band = OCC_SHADOW_OFFSET_MIN_CM
                                ..=(OCC_SHADOW_OFFSET_MAX_CM + OCCLUDER_T_CM);
                            let along_x = o.size_x_cm > o.size_z_cm;
                            if along_x {
                                let d = (z0 - dz).abs().min((z1 - dz).abs());
                                band.contains(&d) && x0 - 120 <= dx && dx <= x1 + 120
                            } else {
                                let d = (x0 - dx).abs().min((x1 - dx).abs());
                                band.contains(&d) && z0 - 120 <= dz && dz <= z1 + 120
                            }
                        });
                        if hit {
                            shaded += 1;
                        }
                    }
                }
            }
        }
        println!("[occ] vanos {doors}, sombreados {shaded}");
        assert!(doors > 0, "no hubo vanos que medir");
        // **El listón es la sexta parte, y el número medido es uno de cada cinco (534 de 2598).**
        // No llega a todos por dos topes que esta fase no toca: la densidad por papel decide cuántos
        // oclusores caben en el espacio —una oficina de 100 m² se lleva tres, y puede tener cinco
        // vanos— y la mitad de los intentos se caen contra la esquiva de las OTRAS puertas y de la
        // masa que ya hay. Lo que este test guarda es que el criterio siga siendo «los vanos
        // primero»: antes de esta fase, los vanos sombreados eran cero.
        assert!(
            shaded * 6 >= doors,
            "sólo {shaded} de {doors} vanos llevan pantalla"
        );
    }

    #[test]
    fn occluders_are_deterministic() {
        let m = manifest();
        for seed in [3, 77, 1234] {
            let b = plan_building(seed, BOUNDS, &[], STOREYS);
            let a = fill_building_with(&b, &m, OCCLUDER_DENSITY);
            let c = fill_building_with(&b, &m, OCCLUDER_DENSITY);
            assert_eq!(a.solids, c.solids, "semilla {seed}");
        }
    }
}
