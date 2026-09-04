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
    Wg3Carve, Wg3Opening, Wg3Segment, Wg3Solid, CARVE_FLOOR_GUARD_CM, MAX_SEGMENT_M,
    MIN_GENERATED_WIDTH_CM,
};

/// ADR-099 D3 — cuánto entra el vano a cada lado de la cara de contacto, en metros. Mismo número que
/// usa la absorción, y por la misma razón: atravesar la pared y la celda del ráster.
const CARVE_DEPTH_M: f32 = 0.5;

/// Altura libre por papel, en centímetros.
///
/// **La verticalidad más barata que existe, y la primera que WG3 tiene por arquitectura y no por
/// pieza.** Hasta aquí la altura venía horneada en el catálogo, así que dos salas contiguas medían lo
/// que midieran sus piezas; con el plan decidiendo el papel, una nave puede ser alta porque es una
/// nave. No mueve el suelo —eso es otro trabajo— pero sí el techo, que es la mitad de lo que hace que
/// un sitio se sienta distinto al de al lado.
fn clear_height_by_role(role: SpaceRole) -> i32 {
    match role {
        SpaceRole::Hall => 450,
        SpaceRole::Spine => 360,
        SpaceRole::Corridor | SpaceRole::Junction => 320,
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
        return ATRIUM_CLEAR_CM;
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
        ));
    }
    out.carves.extend(atrium_carves(building));
    out.carves.extend(hole_carves(building));
    out.carves.extend(well_mouth_carves(building));
    out.solids.extend(atrium_solids(building));
    // ADR-119 enm. 1 — los pilares, que ya no son sólo del atrio. Va DESPUÉS del bucle de plantas
    // porque necesita saber qué espacios acabaron resueltos con una pieza del catálogo, y eso no se
    // sabe hasta que están todos rellenados.
    let placed = out.placements.clone();
    let pillars = hall_pillars(building, manifest, &placed);
    // ADR-105 enm. 3 — la masa interior. Va DESPUES de los pilares porque los esquiva: dos macizos
    // que se pisan son una caja rara, no dos elementos.
    out.solids
        .extend(interior_partitions(building, manifest, &placed, &pillars));
    out.solids.extend(pillars);
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
            let hx = s.rect.min_x_cm + (s.rect.width_cm() - HOLE_SIDE_CM) / 2;
            let hz = s.rect.min_z_cm + (s.rect.depth_cm() - HOLE_SIDE_CM) / 2;
            let hole = super::plan::PlanRect {
                min_x_cm: hx,
                min_z_cm: hz,
                max_x_cm: hx + HOLE_SIDE_CM,
                max_z_cm: hz + HOLE_SIDE_CM,
            };
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

/// Lado de un megapilar, en centímetros.
///
/// **Dos metros, y por lo mismo que un agujero mide dos:** cuatro celdas del ráster. Un pilar fino
/// sale caro en colisión —el rasterizado conservador maciza toda celda que toque— y pequeño en la
/// vista, que es el peor cambio posible. Por eso son MEGApilares.
const PILLAR_SIDE_CM: i32 = 200;

/// Separación entre megapilares, de centro a centro.
const PILLAR_SPACING_CM: i32 = 1000;

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
/// El suelo son 7 m porque un pilar de 2 m maciza sus cuatro celdas de ráster y otro medio metro por
/// el rasterizado conservador: con 6 m de separación el paso libre baja de 3,50 m y una nave con
/// pilares empieza a leerse como un laberinto de pilares.
const PILLAR_SPACING_MIN_CM: i32 = 700;
const PILLAR_SPACING_MAX_CM: i32 = 1300;

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

        for (i, s) in plan.built() {
            if s.role != SpaceRole::Hall || s.rise_cm != 0 {
                continue;
            }
            let r = s.rect;
            if s.area_m2() < PILLAR_MIN_AREA_M2 {
                continue;
            }

            let (cx, cz) = r.centre_m();
            let mut room = super::hash::stream_at(seed, cx, cz, SALT_PILLAR_ROOM);
            if room.next01() >= PILLAR_ROOM_CHANCE {
                continue;
            }
            let span = (PILLAR_SPACING_MAX_CM - PILLAR_SPACING_MIN_CM) as f32;
            let step_x = PILLAR_SPACING_MIN_CM + (room.next01() * span) as i32;
            let step_z = PILLAR_SPACING_MIN_CM + (room.next01() * span) as i32;
            let stagger = room.next01() < 0.45;
            let omit = room.next01() * PILLAR_OMIT_MAX;

            // **LA SEPARACIÓN SE AJUSTA A LA SALA, no al revés.** Es como se dimensiona un vano
            // de verdad: se elige cuántos caben y se reparten a partes iguales, así que el último
            // queda a la misma distancia del muro que el primero. Dibujar un paso fijo y ver qué
            // cae dentro deja siempre una franja muerta contra una de las paredes.
            //
            // `step_x` y `step_z` sorteados son el paso DESEADO; lo que manda es el número entero
            // de vanos que más se le acerca, con el suelo de `PILLAR_SPACING_MIN_CM` para que el
            // peaje del ráster no cierre el paso entre dos pilares (ADR-105 D6).
            let usable_x = r.width_cm() - 2 * PILLAR_WALL_MARGIN_CM - PILLAR_SIDE_CM;
            let usable_z = r.depth_cm() - 2 * PILLAR_WALL_MARGIN_CM - PILLAR_SIDE_CM;
            if usable_x <= 0 || usable_z <= 0 {
                continue;
            }
            let bays = |usable: i32, want: i32| -> i32 {
                let n = ((usable as f32 / want as f32).round() as i32).max(1);
                // Y si al repartir salen vanos por debajo del mínimo, se quitan vanos.
                let mut n = n;
                while n > 1 && usable / n < PILLAR_SPACING_MIN_CM {
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
            if step_x < PILLAR_SPACING_MIN_CM || step_z < PILLAR_SPACING_MIN_CM {
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
            while pz + PILLAR_SIDE_CM <= r.max_z_cm - margin_z {
                // Filas impares a media separación: `OFFSET_GRID`. Con `stagger` apagado sale la
                // retícula recta, que también tiene que existir o no hay contra qué leer la otra.
                let shift = if stagger && row % 2 == 1 {
                    step_x / 2
                } else {
                    0
                };
                let mut px = r.min_x_cm + margin_x + shift;
                while px + PILLAR_SIDE_CM <= r.max_x_cm - margin_x {
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
                    let x = (px + jx as i32)
                        .clamp(r.min_x_cm + PILLAR_SIDE_CM, r.max_x_cm - 2 * PILLAR_SIDE_CM);
                    let z = (pz + jz as i32)
                        .clamp(r.min_z_cm + PILLAR_SIDE_CM, r.max_z_cm - 2 * PILLAR_SIDE_CM);
                    let pillar = super::plan::PlanRect {
                        min_x_cm: x,
                        min_z_cm: z,
                        max_x_cm: x + PILLAR_SIDE_CM,
                        max_z_cm: z + PILLAR_SIDE_CM,
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
                        || doors.iter().any(|&(dx, dz)| {
                            (dx - (x + PILLAR_SIDE_CM / 2)).abs() < PILLAR_DOOR_CLEAR_CM
                                && (dz - (z + PILLAR_SIDE_CM / 2)).abs() < PILLAR_DOOR_CLEAR_CM
                        })
                        || taken.iter().any(|&(x0, z0, x1, z1)| {
                            let (a0, b0, a1, b1) = (
                                x as f32 / CM_PER_M,
                                z as f32 / CM_PER_M,
                                (x + PILLAR_SIDE_CM) as f32 / CM_PER_M,
                                (z + PILLAR_SIDE_CM) as f32 / CM_PER_M,
                            );
                            a0 < x1 && a1 > x0 && b0 < z1 && b1 > z0
                        });
                    if !blocked {
                        out.push(Wg3Solid {
                            x_cm: x,
                            z_cm: z,
                            size_x_cm: PILLAR_SIDE_CM,
                            size_z_cm: PILLAR_SIDE_CM,
                            bottom_y_cm: s.floor_y_cm,
                            top_y_cm: s.floor_y_cm + clear,
                            style: style_of(s.role),
                        });
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
fn interior_partitions(
    building: &RegionBuilding,
    manifest: &Wg3Manifest,
    placements: &[Wg3Placement],
    pillars: &[Wg3Solid],
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
            let mut room = super::hash::stream_at(seed, cx, cz, SALT_PARTITION_ROOM);
            if room.next01() >= PARTITION_ROOM_CHANCE {
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
            // retícula de pilares.
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
            let style = style_of(s.role);
            // Lo que ya ha puesto ESTE espacio: dos divisiones que se cruzan dejan cuadrantes, y un
            // cuadrante con un solo hueco es la isla que el validador caza.
            let mut mine: Vec<super::plan::PlanRect> = Vec::new();
            let mut split_used = false;

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

                let top = if room.next01() < PARTITION_SCREEN_CHANCE {
                    s.floor_y_cm + PARTITION_SCREEN_H_CM.min(clear)
                } else {
                    s.floor_y_cm + clear
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
                let hole = super::plan::PlanRect {
                    min_x_cm: r.min_x_cm + (r.width_cm() - HOLE_SIDE_CM) / 2,
                    min_z_cm: r.min_z_cm + (r.depth_cm() - HOLE_SIDE_CM) / 2,
                    max_x_cm: r.min_x_cm + (r.width_cm() + HOLE_SIDE_CM) / 2,
                    max_z_cm: r.min_z_cm + (r.depth_cm() + HOLE_SIDE_CM) / 2,
                };
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
                    || (s.is_composite() && is_island && !s.covers_rect(&with_gap))
                    || landings.iter().any(|l| l.overlaps(&foot))
                    || (n > 0 && hole.shrunk(-50).overlaps(&foot))
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
                            bottom_y_cm: s.floor_y_cm,
                            top_y_cm: top,
                            style,
                        });
                        cut = end;
                    }
                }
            }
        }
    }
    out
}

fn atrium_carves(building: &RegionBuilding) -> Vec<Wg3Carve> {
    let mut out = Vec::new();
    let grow = (CARVE_DEPTH_M * CM_PER_M) as i32;

    for plan in &building.storeys {
        for s in plan.spaces.iter().filter(|s| is_atrium(s)) {
            out.push(Wg3Carve {
                x_cm: s.rect.min_x_cm - grow,
                z_cm: s.rect.min_z_cm - grow,
                size_x_cm: s.rect.width_cm() + 2 * grow,
                size_z_cm: s.rect.depth_cm() + 2 * grow,
                bottom_y_cm: s.floor_y_cm + STOREY_HEIGHT_CM,
                top_y_cm: s.floor_y_cm + ATRIUM_CLEAR_CM,
            });
        }
    }
    out
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
    fill_storey(plan, manifest, use_catalogue, route_settings, &[])
}

/// El relleno de UNA planta, con los rectángulos en los que no puede ir una pieza del catálogo
/// (los aterrizajes de los pozos que llegan a ella). Ver [`fill_building`].
fn fill_storey(
    plan: &RegionPlan,
    manifest: &Wg3Manifest,
    use_catalogue: bool,
    route_settings: &RouteSettings,
    keep_generated: &[super::plan::PlanRect],
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

    out
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
        SpaceRole::Corridor | SpaceRole::Junction => 2,
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
