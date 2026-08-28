//! ADR-098 T3 — el enrutador: tiende una ruta de tramos entre dos bocas que no se alinean.
//!
//! # El problema, medido
//!
//! Dos bocas solo se conectan si coinciden CLAVADAS, y no coinciden nunca. Enmienda 2 de ADR-096,
//! sobre seis semillas: el par compatible más cercano está a 6,25 m, cero pares por debajo de 3 m, y
//! de los 23–258 que llegan a mirarse de frente **ninguno con desvío lateral menor de 2 cm** — el
//! mejor alineado del mundo tiene 0,10 m de lateral tras 52,7 m de avance. Una familia de conectores
//! rectos autorados no cierra ni uno solo: el problema no es la longitud, es la alineación.
//!
//! # Lo que hace este módulo
//!
//! Lo mismo que el sistema de salas hace con el laberinto: en vez de exigir que encaje, **adapta la
//! geometría**. La ruta se genera con la longitud, los quiebros y el ancho que hagan falta, en forma
//! de tramos ([`super::segment::Wg3Segment`]) que el ráster estampa y el cliente dibuja.
//!
//! Dos fases con el MISMO mecanismo y objetivos distintos:
//!
//! 1. **Unir componentes** — parejas cuyas piezas están en islas distintas. Es lo que arregla que
//!    cruzar una junta lleve a un armario de dos piezas (medido: toda isla que no es el árbol de la
//!    semilla mide exactamente 2).
//! 2. **Cerrar anillos** — parejas de la misma componente separadas por al menos unos cuantos saltos
//!    de grafo. Un anillo entre vecinas no es un anillo, es un bulto.
//!
//! # Determinismo
//!
//! Nada aquí depende del orden de recorrido de un `HashMap` ni de comparar flotantes sin cuantizar:
//! **se trabaja en centímetros enteros** y las candidatas se ordenan por `(coste, boca, boca)`. Dos
//! backends que compongan la misma región tienen que producir el mismo mundo hasta el último
//! centímetro, y el enrutador es la parte que más fácil lo rompería.

use super::placement::outward_normal;
use super::raster::CM_PER_M;
use super::segment::{metres, Wg3Opening, Wg3Segment, MAX_SEGMENT_M};

/// Longitudes de arranque que se prueban desde cada boca, en centímetros y EN ESTE ORDEN.
///
/// El arranque es lo que separa el quiebro de la pared de la que sale: con 0 el giro empieza pegado
/// a la boca, con 10 m la ruta se aleja antes de doblar. Probarlas en orden fijo —y quedarse con la
/// primera que cabe— es lo que hace el resultado reproducible sin buscar un óptimo.
const STEMS_CM: [i32; 7] = [0, 120, 250, 500, 1000, 1800, 3000];

/// Tramo recto más corto que se admite. Por debajo, la ruta serían dos esquinas pegadas y el tramo
/// no llegaría a leerse como pasillo.
const MIN_RUN_CM: i32 = 50;

/// Cuánto penaliza un quiebro al elegir entre rutas, en centímetros de longitud equivalente. Sin
/// esto, dos rutas de la misma longitud empatan y gana la que salga antes — que es una forma
/// elegante de decir «al azar».
const BEND_COST_CM: i32 = 75;

/// Tolerancia al comparar cotas de dos bocas. Un centímetro es lo que viaja por el wire.
const COTA_TOLERANCE_CM: i32 = 2;

/// Cuanto puede subir una ruta de un tramo a la siguiente, en centimetros.
///
/// Es la contrahuella de la escalera del catalogo. Por encima deja de ser un escalon y pasa a ser un
/// bordillo contra el que uno se queda parado: el `CharacterController` del cliente sube 30 cm, pero
/// el margen existe para que subir no dependa de un ajuste del cliente.
const MAX_STEP_CM: i32 = 18;

/// Longitud minima de un tramo al partirla para hacer sitio a un escalon. Por debajo, el peldano es
/// mas alto que largo y se anda como una pared.
const MIN_STEP_RUN_CM: i32 = 80;

/// Lo que cuesta, en centimetros de longitud equivalente, gastar una boca libre como destino.
///
/// **Las bocas libres son el recurso escaso del enrutado.** Una region tiene cinco o seis, y cada
/// isla aporta exactamente una: si dos rutas las gastan de dos en dos, la tercera isla se queda sin
/// con que engancharse aunque hubiera sitio de sobra. Empalmar a mitad de un conector no gasta
/// ninguna, asi que se prefiere — pero no a cualquier precio, y este numero es ese precio.
const MOUTH_TARGET_COST_CM: i32 = 2000;

/// Anchuras a las que se intenta estrechar un conector por el medio, en centimetros y EN ESTE ORDEN.
/// El `0` es «a su anchura natural», que es lo que se prueba primero.
const NARROW_STEPS_CM: [i32; 3] = [0, 180, 140];

/// Solape estricto. Mismo epsilon que el compositor: dos rectángulos que se TOCAN son correctos —una
/// tramo arranca pegada a la cara de su pieza—, dos que se penetran no.
const OVERLAP_EPS: f32 = 0.02;

/// Una boca abierta, tal y como la ve el enrutador.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Mouth {
    pub node: usize,
    pub socket: usize,
    /// Punto de mundo, en metros.
    pub x: f32,
    pub z: f32,
    /// Lado de MUNDO hacia el que mira, ya girado.
    pub side: u8,
    pub width: f32,
    /// Cota del suelo de la boca en el MUNDO (origen de la pieza más la cota local de la boca).
    pub floor_y: f32,
    /// Hueco caminable en la boca.
    pub clear_height: f32,
    /// Discriminante de `Wg3SocketType`.
    pub kind: u8,
}

/// Un rectángulo ocupado. El enrutador no sabe de piezas: sabe de dónde no puede poner nada.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Rect {
    pub min_x: f32,
    pub min_z: f32,
    pub max_x: f32,
    pub max_z: f32,
}

impl Rect {
    fn overlaps(&self, o: &Rect) -> bool {
        self.min_x < o.max_x - OVERLAP_EPS
            && self.max_x - OVERLAP_EPS > o.min_x
            && self.min_z < o.max_z - OVERLAP_EPS
            && self.max_z - OVERLAP_EPS > o.min_z
    }
}

/// Perillas del enrutador. Todas son números que se tocan mirando el mundo, así que ninguna vive
/// dentro del algoritmo.
#[derive(Debug, Clone, PartialEq)]
pub struct RouteSettings {
    /// ADR-098 D6 — permitir que la ruta cambie de anchura entre sus dos extremos.
    ///
    /// En el catálogo actual anchura y tipo van atados (`Sock()` da 5,0 m a `Wide` y 2,4 a
    /// `Corridor`), así que esto es lo único que junta los dos submundos en los que hoy se parte el
    /// catálogo. Apagable a propósito: es lo que permite medir el mundo sin él y saber si la
    /// generación está quitando presión al autorado de piezas de transición.
    pub width_change: bool,

    /// ADR-098 D7 — permitir unir bocas a cotas distintas con una cadena de tramos escalonadas.
    ///
    /// El FORMATO ya lo admite sin un campo más (cada tramo lleva su cota y su losa es la
    /// contrahuella de la siguiente). Lo que falta es que el ráster gobierne el movimiento del
    /// jugador: hoy no lo hace, así que una escalera generada se verificaría contra la malla del
    /// cliente y no contra lo que de verdad frena. Se cuenta lo que descarta para decidir con un
    /// número.
    pub climb: bool,

    /// Longitud máxima de una ruta, en metros.
    pub max_route_m: f32,
    /// Tramos máximas de una ruta. Acota el peor caso de una U larga partida por el tope de tramo.
    pub max_segments_per_route: usize,
    /// Conectores máximos por composición, sumando las dos fases.
    pub max_connectors: usize,
    /// De ésos, cuántos como mucho pueden ser anillos (fase 2).
    pub max_rings: usize,
    /// Saltos mínimos por el grafo para que unir dos piezas cuente como anillo.
    pub min_ring_hops: usize,
    /// Parejas candidatas que se guardan por boca. Es lo que impide que el coste crezca con el
    /// cuadrado del mundo entero.
    pub candidates_per_mouth: usize,
    /// Estilo que llevarán los tramos emitidas.
    pub style: u8,
}

impl Default for RouteSettings {
    fn default() -> Self {
        Self {
            width_change: true,
            climb: true,
            max_route_m: 130.0,
            max_segments_per_route: 12,
            max_connectors: 12,
            max_rings: 4,
            min_ring_hops: 6,
            candidates_per_mouth: 8,
            style: 0,
        }
    }
}

/// Lo que salio del enrutado, listo para aplicar.
///
/// Los tramos vienen YA COMPLETAS —con los empalmes abiertos en los sitios donde otra ruta se
/// engancho a mitad de pasillo—, asi que quien las recibe no tiene que reconstruir nada. Y los
/// descartes son la mitad util: dicen si lo que frena a los conectores es el mundo apretado o una
/// regla nuestra, que son problemas opuestos.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct RouteOutcome {
    pub segments: Vec<Wg3Segment>,
    /// Indices de las bocas que quedaron conectadas por un conector.
    pub used_mouths: Vec<usize>,
    /// Parejas de piezas que un conector dejo unidas. Es lo que el compositor necesita para saber
    /// que el mundo ya no son islas.
    pub edges: Vec<(usize, usize)>,

    pub connectors: u32,
    pub connectors_joining_islands: u32,
    /// Conectores que se engancharon a MITAD de otro conector en vez de a una boca.
    pub taps: u32,

    /// Bocas que llegaron a mirarse. Sin esto, un cero de conectores no distingue «el mundo esta
    /// apretado» de «no habia nada que intentar».
    pub mouths: u32,
    pub pairs: u32,
    /// Bocas que quedaron SIN usar y componentes que quedaron SIN unir al terminar. Juntos dicen por
    /// que se paro: sin bocas libres es falta de sitio donde engancharse; con bocas libres y varias
    /// componentes, es que ninguna ruta cabia.
    pub unused_mouths: u32,
    pub components_left: u32,
    /// Las bocas que se quedaron sin enganchar: `(x_cm, z_cm, lado, ancho_cm)`. Con la posicion
    /// delante, «no cabia» deja de ser una excusa y pasa a ser un sitio que se puede mirar.
    pub leftover: Vec<(i32, i32, u8, i32)>,
    /// Parejas descartadas porque las dos bocas estan a cotas distintas y `climb` esta apagado.
    pub rejected_by_cota: u32,
    /// Parejas descartadas por anchura distinta con `width_change` apagado.
    pub rejected_by_width: u32,
    /// Parejas descartadas porque el tipo de boca no se puede mezclar.
    pub rejected_by_kind: u32,
    /// Parejas en las que ninguna forma de ruta cabia sin pisar algo.
    pub rejected_by_geometry: u32,
}

/// `Wg3SocketType::Service`. Solo conecta consigo mismo: es la clase semantica —los pasillos de
/// detras de la escena— y unirla al mundo publico por generacion seria tirar la ficcion.
const KIND_SERVICE: u8 = 2;

/// Margen de una toma al extremo de la pared en la que se abre, en centimetros. Por debajo, el
/// empalme se comeria la esquina de el tramo y dejaria una jamba de nada.
const TAP_MARGIN_CM: i32 = 60;

/// Anchura minima de una toma. Mas estrecho no es un pasillo, es una gatera.
const MIN_TAP_WIDTH_CM: i32 = 120;

/// A donde va una ruta.
#[derive(Debug, Clone, Copy, PartialEq)]
enum Target {
    /// A otra boca abierta.
    Mouth(usize),
    /// A MITAD de un tramo ya tendida: se le abre un hueco en la pared y se empalma ahi.
    ///
    /// **Es lo que permite que el mundo acabe en UNA pieza.** Las bocas libres son el recurso
    /// escaso —una region tiene cinco o seis, y cada tramo de junta aporta exactamente una—, asi que
    /// uniendo boca con boca solo se pueden fundir la mitad de las islas: las dos que se unen se
    /// quedan sin bocas y ya no pueden crecer mas. Un conector, en cambio, es geometria GENERADA, y
    /// a la geometria generada se le puede abrir una puerta donde haga falta. Es exactamente lo que
    /// hace el sistema de salas con el laberinto: excavar el vano en vez de exigir que encaje.
    Tap { segment: usize, side: u8 },
}

pub fn route(
    mouths: &[Mouth],
    occupancy: &[Rect],
    bounds: Option<(f32, f32, f32, f32)>,
    node_count: usize,
    adjacency: &[Vec<usize>],
    settings: &RouteSettings,
) -> RouteOutcome {
    let mut out = RouteOutcome {
        mouths: mouths.len() as u32,
        ..RouteOutcome::default()
    };
    if mouths.len() < 2 || settings.max_connectors == 0 {
        return out;
    }

    let mut components = UnionFind::new(node_count);
    for (node, neighbours) in adjacency.iter().enumerate() {
        for &n in neighbours {
            components.union(node, n);
        }
    }
    let mut graph: Vec<Vec<usize>> = adjacency.to_vec();
    graph.resize(node_count, Vec::new());

    let mut state = State {
        occupied: occupancy.to_vec(),
        segments: Vec::new(),
        segment_owner: Vec::new(),
        used: vec![false; mouths.len()],
    };

    // FASE 1 — QUE EL MUNDO SEA UNO. Se repite mientras queden islas y presupuesto: cada vuelta
    // busca el enganche mas barato y lo aplica. Iterar en vez de listar de una es lo que permite
    // encadenar, porque el conector que se acaba de tender es sitio nuevo donde engancharse.
    while out.connectors < settings.max_connectors as u32 {
        let Some(link) = best_link(
            mouths,
            &state,
            &mut components,
            &graph,
            settings,
            bounds,
            &mut out,
            Goal::JoinIslands,
        ) else {
            break;
        };
        apply(
            &mut state,
            &mut components,
            &mut graph,
            &mut out,
            mouths,
            link,
            settings,
        );
        out.connectors_joining_islands += 1;
    }

    // FASE 2 — ANILLOS. Ya con el mundo unido: dos sitios de la MISMA componente que esten lejos por
    // el grafo. Un anillo entre vecinas no es un anillo, es un bulto.
    let mut rings = 0u32;
    while out.connectors < settings.max_connectors as u32 && rings < settings.max_rings as u32 {
        let Some(link) = best_link(
            mouths,
            &state,
            &mut components,
            &graph,
            settings,
            bounds,
            &mut out,
            Goal::CloseRing,
        ) else {
            break;
        };
        apply(
            &mut state,
            &mut components,
            &mut graph,
            &mut out,
            mouths,
            link,
            settings,
        );
        rings += 1;
    }

    out.unused_mouths = state.used.iter().filter(|u| !**u).count() as u32;
    out.leftover = mouths
        .iter()
        .enumerate()
        .filter(|(i, _)| !state.used[*i])
        .map(|(_, m)| (cm(m.x), cm(m.z), m.side, cm(m.width)))
        .collect();
    let mut roots: Vec<usize> = mouths.iter().map(|m| components.find(m.node)).collect();
    roots.sort_unstable();
    roots.dedup();
    out.components_left = roots.len() as u32;

    out.segments = state.segments;
    out
}

/// Que se busca en esta vuelta.
#[derive(Debug, Clone, Copy, PartialEq)]
enum Goal {
    /// Unir dos componentes distintas. Es lo que hace el mundo transitable.
    JoinIslands,
    /// Cerrar un anillo dentro de una componente, entre sitios lejanos por el grafo.
    CloseRing,
}

/// Un enganche elegido: de que boca sale y a donde va.
struct Link {
    from: usize,
    target: Target,
    segments: Vec<Wg3Segment>,
}

/// El enganche mas barato que cumple el objetivo, o nada.
///
/// **Las tomas van SIEMPRE antes que las bocas, y no es una preferencia estetica.** Una boca libre
/// solo se puede gastar una vez, y cada isla aporta exactamente una: gastar la del destino deja a esa
/// isla sin con que engancharse a nada mas. Empalmar a mitad de un conector no gasta ninguna, asi que
/// mientras exista una toma viable se usa, y las bocas se reservan para cuando no hay otra —que es,
/// por fuerza, el primer enganche de todos: hasta que no hay conectores, no hay donde empalmar.
#[allow(clippy::too_many_arguments)]
fn best_link(
    mouths: &[Mouth],
    state: &State,
    components: &mut UnionFind,
    graph: &[Vec<usize>],
    settings: &RouteSettings,
    bounds: Option<(f32, f32, f32, f32)>,
    out: &mut RouteOutcome,
    goal: Goal,
) -> Option<Link> {
    let mut best_tap: Option<(i32, Link)> = None;
    let mut best_mouth: Option<(i32, Link)> = None;

    for (i, mouth) in mouths.iter().enumerate() {
        if state.used[i] {
            continue;
        }

        for k in 0..state.segments.len() {
            let owner = state.segment_owner[k];
            if !goal_allows(goal, components, graph, settings, mouth.node, owner) {
                continue;
            }
            for side in 0..4u8 {
                let Some(tap) = tap_mouth(&state.segments[k], side, mouth, settings) else {
                    continue;
                };
                consider(
                    &mut best_tap,
                    mouth,
                    &tap,
                    Target::Tap { segment: k, side },
                    i,
                    settings,
                    state,
                    bounds,
                    out,
                );
            }
        }

        for (j, other) in mouths.iter().enumerate().skip(i + 1) {
            if state.used[j] || mouth.node == other.node {
                continue;
            }
            if !goal_allows(goal, components, graph, settings, mouth.node, other.node) {
                continue;
            }
            if !pair_allowed(mouth, other, settings, out) {
                continue;
            }
            consider(
                &mut best_mouth,
                mouth,
                other,
                Target::Mouth(j),
                i,
                settings,
                state,
                bounds,
                out,
            );
        }
    }

    best_tap.or(best_mouth).map(|(_, link)| link)
}

fn goal_allows(
    goal: Goal,
    components: &mut UnionFind,
    graph: &[Vec<usize>],
    settings: &RouteSettings,
    a: usize,
    b: usize,
) -> bool {
    let same = components.find(a) == components.find(b);
    match goal {
        Goal::JoinIslands => !same,
        // Un anillo exige que las dos puntas esten lejos ANDANDO, no en el plano: unir dos piezas
        // que ya son vecinas no cambia como se recorre el mundo.
        Goal::CloseRing => {
            same && hops(graph, a, b, settings.min_ring_hops) >= settings.min_ring_hops
        }
    }
}

/// Aplica un enganche: marca lo que gasta, abre la toma si la hay y apunta la arista.
fn apply(
    state: &mut State,
    components: &mut UnionFind,
    graph: &mut [Vec<usize>],
    out: &mut RouteOutcome,
    mouths: &[Mouth],
    link: Link,
    settings: &RouteSettings,
) {
    let node = mouths[link.from].node;
    let other_node = match link.target {
        Target::Mouth(j) => {
            state.used[j] = true;
            out.used_mouths.push(j);
            mouths[j].node
        }
        Target::Tap { segment, side } => {
            let tap = tap_mouth(&state.segments[segment], side, &mouths[link.from], settings)
                .expect("la toma se acaba de validar");
            let owner = state.segment_owner[segment];
            open_tap(
                &mut state.segments[segment],
                side,
                tap.x,
                tap.z,
                cm(tap.width),
            );
            out.taps += 1;
            owner
        }
    };

    state.used[link.from] = true;
    out.used_mouths.push(link.from);
    state.push_segments(link.segments, node);
    graph[node].push(other_node);
    graph[other_node].push(node);
    out.edges.push((node, other_node));
    components.union(node, other_node);
    out.connectors += 1;
}

/// Lo que el enrutador va acumulando mientras decide.
struct State {
    occupied: Vec<Rect>,
    segments: Vec<Wg3Segment>,
    /// Pieza a la que pertenece cada tramo, para saber de que componente es un conector cuando otro
    /// se quiera enganchar a el.
    segment_owner: Vec<usize>,
    used: Vec<bool>,
}

impl State {
    fn push_segments(&mut self, segments: Vec<Wg3Segment>, owner: usize) {
        for c in segments {
            let (min_x, min_z, max_x, max_z) = c.bounds();
            self.occupied.push(Rect {
                min_x,
                min_z,
                max_x,
                max_z,
            });
            self.segments.push(c);
            self.segment_owner.push(owner);
        }
    }
}

/// Se pueden unir estas dos bocas? Las tres reglas que NO son geometria, contadas aparte porque cada
/// una apunta a una decision distinta: el tipo es ley, la anchura y la cota son perillas.
fn pair_allowed(a: &Mouth, b: &Mouth, settings: &RouteSettings, out: &mut RouteOutcome) -> bool {
    if (a.kind == KIND_SERVICE) != (b.kind == KIND_SERVICE) {
        out.rejected_by_kind += 1;
        return false;
    }
    if (cm(a.width) - cm(b.width)).abs() > 1 && !settings.width_change {
        out.rejected_by_width += 1;
        return false;
    }
    if (cm(a.floor_y) - cm(b.floor_y)).abs() > COTA_TOLERANCE_CM && !settings.climb {
        out.rejected_by_cota += 1;
        return false;
    }
    true
}

/// Construye la ruta y se queda con ella si es la mas barata hasta ahora.
#[allow(clippy::too_many_arguments)]
fn consider(
    best: &mut Option<(i32, Link)>,
    from: &Mouth,
    to: &Mouth,
    target: Target,
    from_index: usize,
    settings: &RouteSettings,
    state: &State,
    bounds: Option<(f32, f32, f32, f32)>,
    out: &mut RouteOutcome,
) {
    let manhattan = (cm(from.x) - cm(to.x)).abs() + (cm(from.z) - cm(to.z)).abs();
    if manhattan > (settings.max_route_m * CM_PER_M) as i32 {
        return;
    }
    if let Some((cost, _)) = best {
        // Ni se construye si ya no puede ganar: la cota inferior de una ruta ortogonal es su
        // manhattan.
        if manhattan >= *cost {
            return;
        }
    }
    let Some(segments) = try_build(from, to, settings, state, bounds) else {
        out.rejected_by_geometry += 1;
        return;
    };
    let cost = route_length_cm(&segments);
    if best.as_ref().is_none_or(|(c, _)| cost < *c) {
        *best = Some((
            cost,
            Link {
                from: from_index,
                target,
                segments,
            },
        ));
    }
}

/// Longitud de una ruta, medida sobre sus tramos. Sirve para comparar dos rutas hechas: la suma de
/// los lados mayores es proporcional a lo que se anda.
fn route_length_cm(segments: &[Wg3Segment]) -> i32 {
    segments.iter().map(|c| c.size_x_cm.max(c.size_z_cm)).sum()
}

/// La boca virtual de una TOMA: donde se le abriria el hueco a un tramo por uno de sus lados.
///
/// El punto es la proyeccion de la boca de origen sobre esa pared, recortada para que el hueco no se
/// coma las esquinas. Proyectar —y no elegir el centro— es lo que hace que el empalme salga enfrente
/// de quien llega, que es la ruta mas corta y la que menos quiebros necesita.
fn tap_mouth(cell: &Wg3Segment, side: u8, from: &Mouth, settings: &RouteSettings) -> Option<Mouth> {
    // Un lado que ya tiene boca no se toca: seria abrir un hueco encima de otro.
    if cell.openings.iter().any(|o| o.side % 4 == side) {
        return None;
    }

    let (min_x, min_z, max_x, max_z) = (
        cell.x_cm,
        cell.z_cm,
        cell.x_cm + cell.size_x_cm,
        cell.z_cm + cell.size_z_cm,
    );
    let along_x = side.is_multiple_of(2);
    let face_len = if along_x {
        cell.size_x_cm
    } else {
        cell.size_z_cm
    };

    let width = cm(from.width).min(face_len - 2 * TAP_MARGIN_CM);
    if width < MIN_TAP_WIDTH_CM {
        return None;
    }
    if !settings.climb && (cell.floor_y_cm - cm(from.floor_y)).abs() > COTA_TOLERANCE_CM {
        return None;
    }

    let half = width / 2;
    let (lo, hi) = if along_x {
        (min_x + TAP_MARGIN_CM + half, max_x - TAP_MARGIN_CM - half)
    } else {
        (min_z + TAP_MARGIN_CM + half, max_z - TAP_MARGIN_CM - half)
    };
    if lo > hi {
        return None;
    }
    let projected = if along_x { cm(from.x) } else { cm(from.z) };
    let at = projected.clamp(lo, hi);

    let (x, z) = match side % 4 {
        0 => (at, max_z),
        1 => (max_x, at),
        2 => (at, min_z),
        _ => (min_x, at),
    };

    Some(Mouth {
        // No pertenece a ninguna pieza: el nodo lo resuelve el dueno de el tramo, y este campo no se
        // usa para nada mas que para no chocar con la comprobacion de «misma pieza».
        node: usize::MAX,
        socket: usize::MAX,
        x: metres(x),
        z: metres(z),
        side,
        width: metres(width),
        floor_y: metres(cell.floor_y_cm),
        clear_height: metres(cell.height_cm),
        kind: from.kind,
    })
}

/// Abre el hueco de la toma en el tramo. Es la unica mutacion de geometria ya tendida, y es lo que
/// convierte un conector en sitio donde engancharse.
fn open_tap(cell: &mut Wg3Segment, side: u8, x: f32, z: f32, width_cm: i32) {
    let (lx, lz) = (cm(x) - cell.x_cm, cm(z) - cell.z_cm);
    let (w, d) = (cell.size_x_cm, cell.size_z_cm);
    // El offset recorre el perimetro en horario desde (0, D), igual que en una pieza.
    let offset = match side % 4 {
        0 => lx,
        1 => d - lz,
        2 => w - lx,
        _ => lz,
    };
    cell.openings.push(Wg3Opening {
        side,
        offset_cm: offset,
        width_cm,
    });
}

/// La primera forma de ruta que cabe, o nada.
fn try_build(
    a: &Mouth,
    b: &Mouth,
    settings: &RouteSettings,
    state: &State,
    bounds: Option<(f32, f32, f32, f32)>,
) -> Option<Vec<Wg3Segment>> {
    let occupied = &state.occupied;
    let mut best: Option<(i32, Vec<Wg3Segment>)> = None;
    let narrowest = cm(a.width).min(cm(b.width));

    for shape in shapes(a, b, settings) {
        // A su anchura natural primero. Estrechar es la respuesta a que no quepa, no una mejora: un
        // pasillo de metro y medio entre dos de dos y medio se lee como un conducto, y eso solo
        // compensa cuando la alternativa es que no haya paso.
        for narrow in NARROW_STEPS_CM {
            if narrow > 0 && narrow >= narrowest {
                continue;
            }
            let narrow = if narrow > 0 { Some(narrow) } else { None };
            let Some(segments) = segments_of(a, b, &shape, settings, narrow) else {
                continue;
            };
            if segments.len() > settings.max_segments_per_route {
                continue;
            }
            if segments.iter().any(|c| !c.problems().is_empty()) {
                continue;
            }
            if !fits(&segments, occupied, bounds) {
                continue;
            }
            // Entre las que caben gana la mas barata; a igualdad manda el orden de generacion, que
            // es fijo. Quedarse con la primera que cabe daria rutas mas largas de lo necesario solo
            // porque su arranque se probo antes.
            if best.as_ref().is_none_or(|(cost, _)| shape.cost_cm < *cost) {
                best = Some((shape.cost_cm, segments));
            }
            break;
        }
    }

    best.map(|(_, segments)| segments)
}

/// Una forma de ruta: la polilínea de su eje, en centímetros.
struct Shape {
    points: Vec<(i32, i32)>,
    cost_cm: i32,
}

/// Todas las formas que se prueban, en orden fijo: por cada par de arranques y por cada orden de la
/// L que los une.
///
/// Cubre recto, L, Z y U, que es todo lo que puede hacer falta en un mundo ortogonal. Más quiebros
/// no serían una ruta: serían un laberinto, y eso lo pone el catálogo.
fn shapes(a: &Mouth, b: &Mouth, settings: &RouteSettings) -> Vec<Shape> {
    let pa = (cm(a.x), cm(a.z));
    let pb = (cm(b.x), cm(b.z));
    let na = normal_cm(a.side);
    let nb = normal_cm(b.side);
    let max_cm = (settings.max_route_m * CM_PER_M) as i32;

    let mut out = Vec::new();
    for &sa in &STEMS_CM {
        for &sb in &STEMS_CM {
            let p1 = (pa.0 + na.0 * sa, pa.1 + na.1 * sa);
            let p2 = (pb.0 + nb.0 * sb, pb.1 + nb.1 * sb);
            for order in 0..2 {
                let mid = if order == 0 {
                    (p2.0, p1.1)
                } else {
                    (p1.0, p2.1)
                };
                let raw = [pa, p1, mid, p2, pb];
                let Some(points) = clean(&raw) else {
                    continue;
                };
                if !directions_ok(&points, na, nb) {
                    continue;
                }
                let (len, bends) = measure(&points);
                let cost = len + bends as i32 * BEND_COST_CM;
                if len > max_cm {
                    continue;
                }
                out.push(Shape {
                    points,
                    cost_cm: cost,
                });
            }
        }
    }
    out
}

/// Quita puntos repetidos y funde tramos colineales. Devuelve `None` si algún tramo queda en
/// diagonal, que es lo único que esta geometría no sabe expresar.
fn clean(raw: &[(i32, i32)]) -> Option<Vec<(i32, i32)>> {
    let mut pts: Vec<(i32, i32)> = Vec::with_capacity(raw.len());
    for &p in raw {
        if pts.last() != Some(&p) {
            pts.push(p);
        }
    }
    if pts.len() < 2 {
        return None;
    }
    for w in pts.windows(2) {
        if w[0].0 != w[1].0 && w[0].1 != w[1].1 {
            return None;
        }
    }

    // Colineales seguidos: dos tramos en el mismo eje son UN tramo. Si no se funden, el trozo del
    // medio pediría una esquina que no existe.
    let mut fused: Vec<(i32, i32)> = vec![pts[0]];
    for &p in pts.iter().skip(1) {
        let last = *fused.last().expect("nunca vacío");
        if fused.len() >= 2 {
            let prev = fused[fused.len() - 2];
            let same_axis =
                (prev.0 == last.0 && last.0 == p.0) || (prev.1 == last.1 && last.1 == p.1);
            if same_axis {
                fused.pop();
            }
        }
        if *fused.last().expect("nunca vacío") != p {
            fused.push(p);
        }
    }
    if fused.len() < 2 {
        return None;
    }
    Some(fused)
}

/// La ruta tiene que SALIR por donde mira la boca de origen y ENTRAR por donde mira la de destino.
/// Sin esto, la primera tramo se metería dentro de la pieza y la última llegaría a la boca de
/// costado, atravesando su pared.
fn directions_ok(points: &[(i32, i32)], na: (i32, i32), nb: (i32, i32)) -> bool {
    let first = dir(points[0], points[1]);
    let last = dir(points[points.len() - 2], points[points.len() - 1]);
    if first != na {
        return false;
    }
    // Llegar a la boca B es viajar CONTRA su normal, que apunta hacia afuera de su pieza.
    if last != (-nb.0, -nb.1) {
        return false;
    }
    // Y ningún tramo puede deshacer el anterior: eso sería una ruta que se pisa a sí misma.
    for w in points.windows(3) {
        let d0 = dir(w[0], w[1]);
        let d1 = dir(w[1], w[2]);
        if d0 == (-d1.0, -d1.1) {
            return false;
        }
    }
    true
}

fn measure(points: &[(i32, i32)]) -> (i32, usize) {
    let mut len = 0;
    for w in points.windows(2) {
        len += (w[1].0 - w[0].0).abs() + (w[1].1 - w[0].1).abs();
    }
    (len, points.len().saturating_sub(2))
}

/// De polilinea a tramos.
///
/// Cada recta da uno o mas tramos —partidos por el tope de tamano, que es lo que mantiene intacto el
/// reparto por chunk— y cada quiebro, un tramo cuadrado. Las rectas se recortan medio cuadrado a
/// cada lado donde hay quiebro, asi que los tramos se TOCAN y no se pisan.
///
/// **Todas las bocas van centradas en su cara**, y no es una simplificacion: la ruta entera esta
/// centrada en su eje, y el eje pasa por el centro de cada tramo.
///
/// `narrow` estrecha los tramos de EN MEDIO. Los dos extremos no se tocan nunca —tienen que casar
/// con la boca a la que se pegan—, pero por el medio un conector puede adelgazar para colarse por un
/// hueco por el que no cabria a su anchura natural. Es la misma idea que lo demas: la geometria se
/// adapta.
fn segments_of(
    a: &Mouth,
    b: &Mouth,
    shape: &Shape,
    settings: &RouteSettings,
    narrow: Option<i32>,
) -> Option<Vec<Wg3Segment>> {
    let wa = cm(a.width);
    let wb = cm(b.width);
    let floor_y = cm(a.floor_y);
    let height = cm(a.clear_height.min(b.clear_height));
    let legs = shape.points.len() - 1;

    // El cambio de ancho va en el quiebro del medio: el cuadrado de esa esquina ya mide lo que el mas
    // ancho de los dos, asi que hace de transicion sin un tramo especial. Con una sola recta no hay
    // quiebro donde ponerlo, y entonces se parte la recta por la mitad.
    let change_at = if legs >= 2 { legs / 2 } else { 0 };
    let widths: Vec<i32> = (0..legs)
        .map(|i| {
            if i == 0 {
                wa
            } else if i == legs - 1 {
                wb
            } else if let Some(n) = narrow {
                n
            } else if i < change_at {
                wa
            } else {
                wb
            }
        })
        .collect();
    // El cuadrado de un quiebro mide lo que la mas ancha de las dos rectas que une. Ni mas —una
    // esquina inflada come sitio donde no hay— ni menos, o una de las dos entraria por una boca mas
    // estrecha que su propio pasillo.
    let corners: Vec<i32> = (0..legs.saturating_sub(1))
        .map(|i| widths[i].max(widths[i + 1]))
        .collect();

    let mut segments: Vec<Wg3Segment> = Vec::new();

    for i in 0..legs {
        let (from, to) = (shape.points[i], shape.points[i + 1]);
        let d = dir(from, to);
        let width = widths[i];

        let start_trim = if i == 0 { 0 } else { corners[i - 1] / 2 };
        let end_trim = if i == legs - 1 { 0 } else { corners[i] / 2 };

        let start = (from.0 + d.0 * start_trim, from.1 + d.1 * start_trim);
        let end = (to.0 - d.0 * end_trim, to.1 - d.1 * end_trim);
        let run = (end.0 - start.0).abs() + (end.1 - start.1).abs();
        if run < MIN_RUN_CM {
            return None;
        }

        // Una sola recta con cambio de ancho y sin quiebro donde ponerlo: se parte por la mitad y la
        // junta entre las dos mitades es la transicion.
        let split_for_width = legs == 1 && wa != wb;
        let chunks = chunk_lengths(run, split_for_width);
        let mut cursor = start;
        for (k, len) in chunks.iter().enumerate() {
            let next = (cursor.0 + d.0 * len, cursor.1 + d.1 * len);
            let w = if split_for_width {
                if k == 0 {
                    wa
                } else {
                    wb
                }
            } else {
                width
            };
            // La boca de una junta mide lo que el mas estrecho de los dos que une: si midiera lo que
            // el ancho, la pared del estrecho quedaria con un hueco que da a su propia pared.
            let joint = if split_for_width { wa.min(wb) } else { w };
            let before = if k == 0 { w } else { joint };
            let after = if k + 1 < chunks.len() { joint } else { w };

            segments.push(run_cell(
                cursor,
                next,
                d,
                w,
                before.min(w),
                after.min(w),
                floor_y,
                height,
                settings.style,
            ));
            cursor = next;
        }

        // El cuadrado del quiebro, entre esta recta y la siguiente.
        if i + 1 < legs {
            let d_next = dir(shape.points[i + 1], shape.points[i + 2]);
            segments.push(corner_cell(
                to,
                corners[i],
                d,
                d_next,
                widths[i],
                widths[i + 1],
                floor_y,
                height,
                settings.style,
            ));
        }
    }

    if segments.is_empty() {
        return None;
    }

    // ADR-098 D7 — LA RUTA SUBE. El desnivel se reparte entre los tramos y la losa de cada uno hace
    // de contrahuella del anterior: una escalera no necesita ni un campo mas en el formato, solo
    // tramos suficientes.
    let target_y = cm(b.floor_y);
    if target_y != floor_y {
        if !settings.climb {
            return None;
        }
        stagger(
            &mut segments,
            floor_y,
            target_y,
            settings.max_segments_per_route,
        )?;
    }

    Some(segments)
}

/// Reparte un desnivel entre los tramos de una ruta, partiendo las que hagan falta.
///
/// La primera se queda en la cota de la boca de origen y la ultima en la de destino —tienen que
/// casar con lo que hay al otro lado o el jugador se encuentra un escalon donde deberia haber una
/// junta—, y el desnivel se reparte por igual entre medias. Devuelve `None` si no hay forma de
/// partirlo en escalones que se puedan subir, que es una respuesta valida: esa pareja de bocas no se
/// une, y la sonda lo cuenta.
fn stagger(segments: &mut Vec<Wg3Segment>, from_y: i32, to_y: i32, max_cells: usize) -> Option<()> {
    let delta = to_y - from_y;
    let needed = (delta.abs() + MAX_STEP_CM - 1) / MAX_STEP_CM;

    // Cada frontera entre dos tramos es un escalon, asi que hacen falta `needed + 1` tramos.
    while (segments.len() as i32) < needed + 1 {
        if segments.len() >= max_cells {
            return None;
        }
        let index = longest_splittable(segments)?;
        let (a, b) = split_segment(&segments[index])?;
        segments[index] = a;
        segments.insert(index + 1, b);
    }

    let n = segments.len() as i32;
    for (i, cell) in segments.iter_mut().enumerate() {
        // Redondeo entero repartido: el ultimo cae EXACTO en la cota de destino, no cerca.
        cell.floor_y_cm = from_y + (delta * i as i32) / (n - 1);
    }
    if let Some(last) = segments.last_mut() {
        last.floor_y_cm = to_y;
    }
    Some(())
}

/// El tramo mas larga que se puede partir: un tramo recto, con sus dos bocas en lados opuestos. Una
/// esquina no se parte — sus bocas estan en lados perpendiculares y partirla dejaria media esquina
/// sin salida.
fn longest_splittable(segments: &[Wg3Segment]) -> Option<usize> {
    let mut best: Option<(i32, usize)> = None;
    for (i, c) in segments.iter().enumerate() {
        if c.openings.len() != 2 {
            continue;
        }
        let (s0, s1) = (c.openings[0].side % 4, c.openings[1].side % 4);
        if (s0 + 2) % 4 != s1 {
            continue;
        }
        let along = if s0.is_multiple_of(2) {
            c.size_z_cm
        } else {
            c.size_x_cm
        };
        if along < 2 * MIN_STEP_RUN_CM {
            continue;
        }
        if best.is_none_or(|(len, _)| along > len) {
            best = Some((along, i));
        }
    }
    best.map(|(_, i)| i)
}

/// Parte un tramo recto en dos por la mitad. La cara nueva queda abierta de par en par a los dos
/// lados: donde no habia junta no puede aparecer un tabique.
fn split_segment(cell: &Wg3Segment) -> Option<(Wg3Segment, Wg3Segment)> {
    let horizontal = cell.openings[0].side % 4 == 1 || cell.openings[0].side % 4 == 3;
    let (mut a, mut b) = (cell.clone(), cell.clone());

    if horizontal {
        let half = cell.size_x_cm / 2;
        a.size_x_cm = half;
        b.x_cm = cell.x_cm + half;
        b.size_x_cm = cell.size_x_cm - half;
    } else {
        let half = cell.size_z_cm / 2;
        a.size_z_cm = half;
        b.z_cm = cell.z_cm + half;
        b.size_z_cm = cell.size_z_cm - half;
    }

    // Cada mitad conserva la boca de su extremo y estrena la de la junta, a todo el ancho.
    let cross = if horizontal {
        cell.size_z_cm
    } else {
        cell.size_x_cm
    };
    let (low_side, high_side) = if horizontal { (3u8, 1u8) } else { (2u8, 0u8) };
    let keep = |c: &Wg3Segment, side: u8| c.openings.iter().find(|o| o.side % 4 == side).copied();

    let a_end = keep(cell, low_side)?;
    let b_end = keep(cell, high_side)?;
    a.openings = vec![
        a_end,
        centred_opening(high_side, a.size_x_cm, a.size_z_cm, cross),
    ];
    b.openings = vec![
        centred_opening(low_side, b.size_x_cm, b.size_z_cm, cross),
        b_end,
    ];

    Some((a, b))
}

/// Cómo se parte un tramo en tramos. Trozos iguales, y nunca por encima del tope.
fn chunk_lengths(run: i32, force_two: bool) -> Vec<i32> {
    let max = (MAX_SEGMENT_M * CM_PER_M) as i32;
    // División hacia arriba a mano: `div_ceil` de enteros con signo sigue siendo inestable.
    let mut n = ((run + max - 1) / max).max(1);
    if force_two && n < 2 {
        n = 2;
    }
    let base = run / n;
    let mut out = vec![base; n as usize];
    let rest = run - base * n;
    if let Some(last) = out.last_mut() {
        *last += rest;
    }
    out
}

#[allow(clippy::too_many_arguments)]
fn run_cell(
    from: (i32, i32),
    to: (i32, i32),
    d: (i32, i32),
    width: i32,
    open_before: i32,
    open_after: i32,
    floor_y: i32,
    height: i32,
    style: u8,
) -> Wg3Segment {
    let along = (to.0 - from.0).abs() + (to.1 - from.1).abs();
    let horizontal = d.1 == 0;
    let (size_x, size_z) = if horizontal {
        (along, width)
    } else {
        (width, along)
    };
    let min_x = from.0.min(to.0) - if horizontal { 0 } else { width / 2 };
    let min_z = from.1.min(to.1) - if horizontal { width / 2 } else { 0 };

    let entry_side = side_facing((-d.0, -d.1));
    let exit_side = side_facing(d);
    Wg3Segment {
        x_cm: min_x,
        z_cm: min_z,
        size_x_cm: size_x,
        size_z_cm: size_z,
        floor_y_cm: floor_y,
        height_cm: height,
        openings: vec![
            centred_opening(entry_side, size_x, size_z, open_before),
            centred_opening(exit_side, size_x, size_z, open_after),
        ],
        style,
    }
}

#[allow(clippy::too_many_arguments)]
fn corner_cell(
    at: (i32, i32),
    side_cm: i32,
    d_in: (i32, i32),
    d_out: (i32, i32),
    open_in: i32,
    open_out: i32,
    floor_y: i32,
    height: i32,
    style: u8,
) -> Wg3Segment {
    Wg3Segment {
        x_cm: at.0 - side_cm / 2,
        z_cm: at.1 - side_cm / 2,
        size_x_cm: side_cm,
        size_z_cm: side_cm,
        floor_y_cm: floor_y,
        height_cm: height,
        openings: vec![
            centred_opening(side_facing((-d_in.0, -d_in.1)), side_cm, side_cm, open_in),
            centred_opening(side_facing(d_out), side_cm, side_cm, open_out),
        ],
        style,
    }
}

/// Una boca centrada en su cara. El offset se mide sobre el lado, y el lado corre en X para N y S y
/// en Z para E y O.
fn centred_opening(side: u8, size_x: i32, size_z: i32, width: i32) -> Wg3Opening {
    let length = if side.is_multiple_of(2) {
        size_x
    } else {
        size_z
    };
    Wg3Opening {
        side,
        offset_cm: length / 2,
        width_cm: width.min(length),
    }
}

/// El lado de un tramo que mira hacia `d`.
fn side_facing(d: (i32, i32)) -> u8 {
    match d {
        (0, z) if z > 0 => 0,
        (x, 0) if x > 0 => 1,
        (0, _) => 2,
        _ => 3,
    }
}

fn fits(segments: &[Wg3Segment], occupied: &[Rect], bounds: Option<(f32, f32, f32, f32)>) -> bool {
    let rects: Vec<Rect> = segments
        .iter()
        .map(|c| {
            let (min_x, min_z, max_x, max_z) = c.bounds();
            Rect {
                min_x,
                min_z,
                max_x,
                max_z,
            }
        })
        .collect();

    if let Some((bmin_x, bmin_z, bmax_x, bmax_z)) = bounds {
        // Fuera de la región no se pone nada: la vecina compone sin saber de esto y se pisarían.
        if rects
            .iter()
            .any(|r| r.min_x < bmin_x || r.min_z < bmin_z || r.max_x > bmax_x || r.max_z > bmax_z)
        {
            return false;
        }
    }

    for r in &rects {
        if occupied.iter().any(|o| r.overlaps(o)) {
            return false;
        }
    }

    // Y la ruta no se pisa a sí misma: una U puede volver sobre su propio tramo, y los tramos
    // contiguas se tocan a propósito, así que solo se comparan las que no lo son.
    for i in 0..rects.len() {
        for j in (i + 2)..rects.len() {
            if rects[i].overlaps(&rects[j]) {
                return false;
            }
        }
    }
    true
}

/// Saltos entre dos nodos, cortando en cuanto se alcanza el mínimo que interesa. No hace falta la
/// distancia exacta: hace falta saber si están LEJOS.
fn hops(graph: &[Vec<usize>], from: usize, to: usize, cutoff: usize) -> usize {
    if from == to {
        return 0;
    }
    let mut seen = vec![false; graph.len()];
    let mut frontier = vec![from];
    seen[from] = true;
    for depth in 1..=cutoff {
        let mut next = Vec::new();
        for node in frontier {
            for &n in &graph[node] {
                if n == to {
                    return depth;
                }
                if !seen[n] {
                    seen[n] = true;
                    next.push(n);
                }
            }
        }
        if next.is_empty() {
            break;
        }
        frontier = next;
    }
    cutoff
}

fn dir(from: (i32, i32), to: (i32, i32)) -> (i32, i32) {
    ((to.0 - from.0).signum(), (to.1 - from.1).signum())
}

fn normal_cm(side: u8) -> (i32, i32) {
    let (x, z) = outward_normal(side);
    (x as i32, z as i32)
}

fn cm(v: f32) -> i32 {
    (v * CM_PER_M).round() as i32
}

/// Union-find de toda la vida. Se escribe aquí y no se importa porque la sonda de islas tiene la
/// suya: son quince líneas, y compartirlas ataría un test a un módulo de producción.
struct UnionFind {
    parent: Vec<usize>,
}

impl UnionFind {
    fn new(n: usize) -> Self {
        Self {
            parent: (0..n).collect(),
        }
    }

    fn find(&mut self, mut a: usize) -> usize {
        while self.parent[a] != a {
            self.parent[a] = self.parent[self.parent[a]];
            a = self.parent[a];
        }
        a
    }

    fn union(&mut self, a: usize, b: usize) {
        let (ra, rb) = (self.find(a), self.find(b));
        if ra != rb {
            self.parent[ra] = rb;
        }
    }
}
