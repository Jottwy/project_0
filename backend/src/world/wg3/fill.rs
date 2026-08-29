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
};
use super::raster::CM_PER_M;
use super::route::{self, Mouth, PlannedRoute, Rect, RouteSettings};
use super::segment::{
    Wg3Carve, Wg3Opening, Wg3Segment, CARVE_FLOOR_GUARD_CM, MAX_SEGMENT_M, MIN_GENERATED_WIDTH_CM,
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
fn clear_height_cm(space: &PlannedSpace) -> i32 {
    let want = clear_height_by_role(space.role);
    if space.max_clear_cm > 0 {
        want.min(space.max_clear_cm)
    } else {
        want
    }
}

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
    for plan in &building.storeys {
        out.absorb(fill(plan, manifest));
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
        if let Some(p) = fitting_piece(space, manifest).filter(|_| use_catalogue && flat) {
            out.placements.push(p);
            out.spaces_by_piece += 1;
            // Y se le abren las puertas del plan, porque las suyas están donde las puso quien la
            // dibujó y no donde hace falta. Ver `FilledRegion::carves`.
            for w in &wanted[i] {
                out.carves
                    .push(carve_for(w, space.floor_y_cm, clear_height_cm(space)));
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
    let r = s.rect;
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
    const EPS: i32 = 2;
    let r = space.rect;
    // Dentro del tramo del lado, o el hueco se saldría de la pared. Se comprueba con el vano mínimo
    // y no con el pedido: un hueco de 5 m centrado a 30 cm de la esquina no cabe por mucho que la
    // pared mida 20 m.
    let half = MIN_GENERATED_WIDTH_CM / 2;
    if (r.max_z_cm - z_cm).abs() <= EPS && x_cm - half >= r.min_x_cm && x_cm + half <= r.max_x_cm {
        return Some(0);
    }
    if (r.max_x_cm - x_cm).abs() <= EPS && z_cm - half >= r.min_z_cm && z_cm + half <= r.max_z_cm {
        return Some(1);
    }
    if (r.min_z_cm - z_cm).abs() <= EPS && x_cm - half >= r.min_x_cm && x_cm + half <= r.max_x_cm {
        return Some(2);
    }
    if (r.min_x_cm - x_cm).abs() <= EPS && z_cm - half >= r.min_z_cm && z_cm + half <= r.max_z_cm {
        return Some(3);
    }
    None
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
    let want_x = space.rect.width_cm();
    let want_z = space.rect.depth_cm();

    for piece in &manifest.pieces {
        // Un tapón o un callejón no representa un espacio: existe para sellar una boca.
        if piece.dead_end {
            continue;
        }
        for rotation in 0..4u8 {
            let (w, d) = footprint_cm(piece, rotation);
            if (w - want_x).abs() > PIECE_FIT_SLACK_CM || (d - want_z).abs() > PIECE_FIT_SLACK_CM {
                continue;
            }
            return Some(Wg3Placement {
                piece: piece.index,
                rotation,
                origin_x_cm: space.rect.min_x_cm,
                origin_z_cm: space.rect.min_z_cm,
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
    let r = space.rect;
    // División con techo escrita a mano: `i32::div_ceil` sigue siendo inestable en el toolchain del
    // proyecto, y no se va a encender una feature de nightly por una cuenta de dos operaciones.
    //
    // El divisor deja HOLGURA sobre el tope de tramo (21 m contra 25) porque los cortes se van a
    // mover para no partir puertas, y un corte movido alarga el tramo de al lado.
    let ceil_div = |v: i32, by: i32| (v + by - 1) / by;
    let budget = max_cm - 400;
    let nx = ceil_div(r.width_cm(), budget).max(1);
    let nz = ceil_div(r.depth_cm(), budget).max(1);

    let edge = |i: i32, n: i32, from: i32, size: i32| -> i32 {
        if i == n {
            from + size
        } else {
            from + (size * i) / n
        }
    };
    let mut xs: Vec<i32> = (0..=nx)
        .map(|i| edge(i, nx, r.min_x_cm, r.width_cm()))
        .collect();
    let mut zs: Vec<i32> = (0..=nz)
        .map(|i| edge(i, nz, r.min_z_cm, r.depth_cm()))
        .collect();

    // **LOS CORTES SE APARTAN DE LAS PUERTAS, y esto no es un refinamiento: es corrección.**
    //
    // Un hueco a caballo de la frontera entre dos tramos hermanas no lo aloja ninguna de las dos, y
    // el fallo es del tipo que no se nota: los contadores cuadran, ningún enlace sale como fallido, y
    // la sala nace sellada con su puerta dibujada en el plano. Se ve como manchas andables sueltas —
    // 22 en la primera medida de una región—, que es la fragmentación de siempre reaparecida por
    // dentro. Mover el corte cuesta dos restas y lo quita de raíz.
    shift_cuts(&mut xs, wanted, true);
    shift_cuts(&mut zs, wanted, false);

    let height = clear_height_cm(space);
    // Qué huecos ha alojado alguien. Un `false` al terminar es una puerta perdida, y hay que contarla.
    let mut placed = vec![false; wanted.len()];
    // Índices en `out.segments` de lo que emite ESTE espacio, para poder volver sobre ellos si algún
    // hueco necesita rescate.
    let mut emitted: Vec<usize> = Vec::new();

    for iz in 0..nz {
        for ix in 0..nx {
            let (x0, x1) = (xs[ix as usize], xs[ix as usize + 1]);
            let (z0, z1) = (zs[iz as usize], zs[iz as usize + 1]);

            let mut openings = Vec::new();

            // **Las paredes INTERIORES del espacio se abren enteras.** Un espacio partido en cuatro
            // tramos tiene que seguir siendo un sitio; dejar la pared de por medio lo convertiría en
            // cuatro salas que nadie pidió, y ésa es justo la fragmentación de la que se viene.
            if ix + 1 < nx {
                openings.push(full_side(1, z1 - z0));
            }
            if ix > 0 {
                openings.push(full_side(3, z1 - z0));
            }
            if iz + 1 < nz {
                openings.push(full_side(0, x1 - x0));
            }
            if iz > 0 {
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
