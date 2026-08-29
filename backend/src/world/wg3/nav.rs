//! ADR-108 — la navegación de WG3: lo que necesita un consumidor que no sea el jugador.
//!
//! # Por qué existe
//!
//! WG3 sabía contestar dos preguntas —¿estorba aquí? ¿dónde está el suelo?— y son exactamente las que
//! necesita un jugador, que decide a dónde va con sus ojos. El robapieles y los facelings deciden con
//! un algoritmo, así que necesitan además saber **si se puede llegar** y **por dónde**.
//!
//! # La celda lleva su COTA, y ahí está toda la diferencia con WG2
//!
//! La celda de `grid_gen` es 2D más una capa de 4 m, y le vale porque en aquel mundo **una XZ tiene un
//! solo suelo**. Aquí no: una escalera pisa la misma XZ a diez cotas y un atrio tiene suelo abajo y
//! balcón arriba en la misma vertical (ADR-104). Sin cota, una criatura cruzaría de un piso a otro por
//! el aire y no podría subir una escalera — las dos cosas a la vez.
//!
//! Y el enlace entre vecinas usa [`MAX_WALK_STEP_CM`], que es **el mismo número que decide si el
//! jugador sube un peldaño**. Así una criatura sube una escalera exactamente cuando la subiría él, y
//! no hay dos criterios de andabilidad que puedan separarse con el tiempo.

use std::collections::{BinaryHeap, HashMap};

use super::collision::Wg3CollisionCache;
use super::plan::MAX_WALK_STEP_CM;
use super::raster::WG3_CELL_M;
use crate::world::Vec3;

/// Ventana de búsqueda alrededor del origen, en METROS.
///
/// **En metros y no en celdas, y no es un detalle de estilo.** `grid_gen` usa 24 celdas, que con las
/// suyas de 2,5 m son 60 m; heredar el número con celdas de 0,5 daría 12 m, que no cruza ni una sala
/// grande de este mundo. Lo que hay que conservar es el ALCANCE, no la cuenta.
pub const NAV_WINDOW_M: f32 = 30.0;

/// Tope de expansiones. Mismo orden que el de `grid_gen`: es lo que impide que una sala enorme se
/// coma el tick entero, y el resultado sigue siendo útil porque la búsqueda es best-effort.
pub const NAV_MAX_EXPANSIONS: usize = 3_000;

/// Alto del cuerpo que tiene que caber de pie. Mismo que usa la colisión.
const BODY_M: f32 = 1.8;

/// Cuánto se busca el suelo por encima de los pies, en metros. Espejo de la colisión: es lo que
/// convierte una escalera en algo que se sube en vez de una pared de 25 cm.
const STEP_UP_M: f32 = 0.30;

/// La celda XZ del ráster que contiene un punto.
pub fn cell_of(x: f32, z: f32) -> (i32, i32) {
    (
        (x / WG3_CELL_M).floor() as i32,
        (z / WG3_CELL_M).floor() as i32,
    )
}

/// El centro de una celda, en metros.
pub fn cell_centre(cx: i32, cz: i32) -> (f32, f32) {
    (
        (cx as f32 + 0.5) * WG3_CELL_M,
        (cz as f32 + 0.5) * WG3_CELL_M,
    )
}

/// Qué salió de buscar un camino.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct NavOutcome {
    /// Se llegó al destino pedido.
    pub reached: bool,
    /// Se devolvió el mejor camino parcial en vez de ninguno.
    ///
    /// **No es un modo de fallo**, y por eso tiene nombre propio: el jugador colisiona contra un
    /// modelo y la criatura navega otro, así que puede pedirse un destino que esta búsqueda considera
    /// macizo. Devolver «no hay camino» ahí congelaría a la criatura justo cuando está más cerca.
    pub best_effort: bool,
    pub expansions: usize,
}

/// Cuánta cota separa una celda del destino, en metros.
fn floor_gap(key: CellKey, goal_floor: f32) -> f32 {
    (key.2 as f32 / 100.0 - goal_floor).abs()
}

/// Cuánto puede desviarse la cota de llegada y seguir contando como haber llegado.
///
/// Medio metro: más que cualquier peldaño y muchísimo menos que una planta (3,32), así que distingue
/// «he llegado» de «estoy justo debajo».
const GOAL_FLOOR_TOLERANCE_M: f32 = 0.5;

/// Un nodo de la frontera. El orden está invertido para que `BinaryHeap` —que es un montón de
/// máximos— entregue el de menor coste.
#[derive(PartialEq, Eq)]
struct Node {
    f: i64,
    key: CellKey,
}
impl Ord for Node {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        other.f.cmp(&self.f)
    }
}
impl PartialOrd for Node {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

/// **Celda con cota**, redondeada a centímetros.
///
/// La cota va en la CLAVE y no sólo en el estado: sin ella, la primera visita a una XZ cerraría esa
/// columna para siempre, y en un atrio eso significa que quien llegue por el suelo impide para siempre
/// que se explore el balcón —o al revés—. Con dos plantas en la misma vertical, una clave 2D no
/// distingue dos sitios distintos.
type CellKey = (i32, i32, i32);

/// La cota del suelo pisable en una celda, partiendo de una cota de referencia.
///
/// `None` cuando no hay suelo, no cabe uno de pie, o hay algo estorbando. Las tres condiciones hacen
/// falta y ninguna sobra: el suelo puede existir y tener el techo a un metro, o estar libre y tener un
/// pilar dentro del radio del cuerpo.
/// Las cotas a las que se puede pisar una celda viniendo de `from_floor`: **hasta dos**.
///
/// Una sola no basta, y ése fue el fallo que dejaba al robapieles subiendo escaleras y no bajándolas.
/// El rellano y el primer peldaño comparten celda —huella de 60 cm, celda de 50— y
/// [`Wg3CollisionCache::floor_below_m`] siempre devuelve el más alto de los dos, así que desde el
/// rellano **nunca se ofrecía el peldaño de abajo**: el grafo era de un solo sentido.
pub fn floors_at(cache: &Wg3CollisionCache, x: f32, z: f32, from_floor: f32) -> Vec<f32> {
    let mut out = Vec::with_capacity(2);
    if let Some(f) = floor_at(cache, x, z, from_floor) {
        out.push(f);
        // Y la de abajo, si la hay y está a un escalón. El tope importa: sin él, cualquier vecina
        // ofrecería el suelo de la planta inferior y las criaturas se tirarían por los balcones.
        let step = MAX_WALK_STEP_CM as f32 / 100.0;
        if let Some(lower) = cache.floor_strictly_below_m(x, z, f) {
            if f - lower <= step
                && (from_floor - lower).abs() <= step
                && cache.headroom_m(x, z, lower).is_some_and(|h| h >= BODY_M)
            {
                out.push(lower);
            }
        }
    }
    out
}

pub fn floor_at(cache: &Wg3CollisionCache, x: f32, z: f32, from_floor: f32) -> Option<f32> {
    // **Suelo CRUDO, no la cota del jugador.** `floor_y` conserva la de entrada cuando no encuentra
    // nada —correcto para moverse, porque no teletransporta— y eso hacía que un vacío se leyera como
    // «suelo justo aquí». Con esa confusión, un agujero de ADR-104 se recorría como si fuera pasillo.
    let floor = cache.floor_below_m(x, z, from_floor)?;
    // **Altura libre de la COLUMNA, no una cápsula.** Con `blocked_at` el barrido de 35 cm de radio
    // invade el peldaño de al lado —25 cm más alto, o sea dentro del cuerpo— y **ninguna escalera
    // pasaría nunca el filtro**. Medido: con la cápsula, cero escaleras navegables en las cuatro
    // regiones de referencia.
    if cache
        .headroom_m(x, z, from_floor)
        .is_none_or(|h| h < BODY_M)
    {
        return None;
    }
    Some(floor)
}

/// A* de 4 vecinas sobre las celdas del ráster, con cota.
///
/// Devuelve el camino en **posiciones de mundo** —centros de celda a la cota de su suelo— y no en
/// celdas: quien lo consume quiere andar, y exportar el tipo de celda obligaría a cada consumidor a
/// convertirlo por su cuenta, que es una copia más del mapeo que puede desviarse.
///
/// **Best-effort a propósito** (ver [`NavOutcome::best_effort`]).
pub fn find_path(
    cache: &Wg3CollisionCache,
    from: Vec3,
    to: Vec3,
    out: &mut Vec<Vec3>,
) -> NavOutcome {
    out.clear();
    let mut stats = NavOutcome::default();

    let start_cell = cell_of(from.x, from.z);
    let goal_cell = cell_of(to.x, to.z);
    // **La cota de partida se PEGA al suelo real, no se cree la que llega.**
    //
    // Es el fallo que dejaba al robapieles clavado, y no daba error de ninguna clase: si la Y que trae
    // no cae en el suelo de esa columna —porque se movió sobre una losa que aquí está recortada, o
    // porque su FSM la calculó con otras cotas— la búsqueda arranca FLOTANDO, y entonces sus cuatro
    // vecinas quedan a más de un escalón y **no hay ni una salida**. Medido: `exp=1`, cero pasos, con
    // los vecinos perfectamente andables.
    //
    // Pegarla es lo mismo que hace el pin al suelo del resolutor de entidad, un nivel más arriba.
    let start_floor = cache
        .floor_below_m(from.x, from.z, from.y - BODY_M)
        .unwrap_or(from.y - BODY_M);
    let goal_floor = to.y - BODY_M;

    let window = (NAV_WINDOW_M / WG3_CELL_M) as i32;
    let in_window = |c: (i32, i32)| {
        (c.0 - start_cell.0).abs() <= window && (c.1 - start_cell.1).abs() <= window
    };

    // Distancia Manhattan en celdas, escalada para que el coste y la heurística vivan en la misma
    // unidad entera. Enteros y no `f32` porque el montón compara y un NaN de un `f32` corrompe el
    // orden en silencio.
    let h =
        |c: (i32, i32)| -> i64 { ((c.0 - goal_cell.0).abs() + (c.1 - goal_cell.1).abs()) as i64 };

    let start_key: CellKey = (
        start_cell.0,
        start_cell.1,
        (start_floor * 100.0).round() as i32,
    );
    let mut came: HashMap<CellKey, CellKey> = HashMap::new();
    let mut cost: HashMap<CellKey, i64> = HashMap::new();
    let mut open = BinaryHeap::new();

    cost.insert(start_key, 0);
    open.push(Node {
        f: h(start_cell),
        key: start_key,
    });

    let mut best = (
        start_key,
        h(start_cell) + (floor_gap(start_key, goal_floor) / WG3_CELL_M) as i64,
    );
    let mut found: Option<CellKey> = None;

    while let Some(Node { key, .. }) = open.pop() {
        // **Llegar es coincidir en XZ Y EN COTA.** Con sólo XZ, pedir un punto de otra planta se da
        // por alcanzado en cuanto se pasa por encima o por debajo: la criatura cree que llegó, se
        // para, y se queda clavada al pie de la escalera. Visto jugando antes que en ningún número, y
        // es el mismo error de fondo que la celda 2D — un destino sin cota no dice dónde.
        if (key.0, key.1) == goal_cell && floor_gap(key, goal_floor) <= GOAL_FLOOR_TOLERANCE_M {
            found = Some(key);
            stats.reached = true;
            break;
        }
        if stats.expansions >= NAV_MAX_EXPANSIONS {
            break;
        }
        stats.expansions += 1;

        // El mejor parcial se mide en XZ **y en cota**: si no, quedarse justo debajo del destino
        // puntúa como haber llegado, que es el mismo fallo por la puerta de atrás.
        let hk = h((key.0, key.1)) + (floor_gap(key, goal_floor) / WG3_CELL_M) as i64;
        if hk < best.1 {
            best = (key, hk);
        }

        let floor = key.2 as f32 / 100.0;
        let g = cost[&key];
        for (dx, dz) in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
            let nc = (key.0 + dx, key.1 + dz);
            if !in_window(nc) {
                continue;
            }
            let (nx, nz) = cell_centre(nc.0, nc.1);
            for nfloor in floors_at(cache, nx, nz, floor) {
                // **El enlace usa el mismo escalón que sube el jugador.** Es lo que hace que una
                // escalera se pueda navegar y que el suelo de un atrio no conecte con su balcón.
                if ((nfloor - floor).abs() * 100.0) as i32 > MAX_WALK_STEP_CM {
                    continue;
                }
                let nk: CellKey = (nc.0, nc.1, (nfloor * 100.0).round() as i32);
                // **El camino paga por pegarse a la pared.** Sin esto la ruta va por el borde —el A*
                // no sabe que el cuerpo mide 35 cm de radio, sólo que la celda existe— y entonces el
                // suavizado no tiene margen para cortar una esquina sin meter media espalda en el
                // muro. Con el recargo, un pasillo se recorre por el centro y las esquinas se doblan
                // holgadas. Es tres veces el coste de un paso: bastante para elegir el centro cuando
                // se puede, poco para no rechazar un pasillo estrecho cuando es el único camino.
                let ng = g + 1 + 3 * hugs_wall(cache, nc, nfloor) as i64;
                if cost.get(&nk).is_some_and(|&c| c <= ng) {
                    continue;
                }
                cost.insert(nk, ng);
                came.insert(nk, key);
                open.push(Node {
                    f: ng + h(nc),
                    key: nk,
                });
            }
        }
    }

    let end = match found {
        Some(k) => k,
        None => {
            stats.best_effort = true;
            best.0
        }
    };
    if end == start_key {
        return stats;
    }

    // Reconstrucción hacia atrás y del revés al final: seguir `came` da el camino invertido.
    let mut chain = Vec::new();
    let mut cur = end;
    while cur != start_key {
        let (cx, cz) = cell_centre(cur.0, cur.1);
        chain.push(Vec3::new(cx, cur.2 as f32 / 100.0 + BODY_M, cz));
        match came.get(&cur) {
            Some(&prev) => cur = prev,
            None => break,
        }
    }
    chain.reverse();
    out.extend(chain);
    stats
}

/// Cuántas de las cuatro vecinas de una celda NO son andables — o sea, cuánta pared la rodea.
fn hugs_wall(cache: &Wg3CollisionCache, c: (i32, i32), floor: f32) -> usize {
    [(1, 0), (-1, 0), (0, 1), (0, -1)]
        .iter()
        .filter(|(dx, dz)| {
            let (nx, nz) = cell_centre(c.0 + dx, c.1 + dz);
            floor_at(cache, nx, nz, floor).is_none()
        })
        .count()
}

/// ¿Se puede ir de `a` a `b` en línea recta, andando?
///
/// Muestrea la recta cada media celda y exige lo mismo en cada punto que exige la búsqueda: que haya
/// suelo, que quepa uno de pie, y que el salto respecto al punto anterior sea el que sube un jugador.
/// **La última condición es la que hace que esto sirva en un mundo con plantas**: sin ella, una recta
/// que cruza por encima de un atrio pasaría por «libre» porque abajo hay suelo — a tres metros.
pub fn segment_is_clear(cache: &Wg3CollisionCache, a: Vec3, b: Vec3, radius: f32) -> bool {
    let dx = b.x - a.x;
    let dz = b.z - a.z;
    let dist = (dx * dx + dz * dz).sqrt();
    if dist < 1e-3 {
        return true;
    }
    let steps = (dist / (WG3_CELL_M * 0.5)).ceil() as i32;

    // **TRES RAÍLES Y NO UNO, y con uno solo el robapieles se clava en las esquinas.**
    //
    // La línea central puede pasar a diez centímetros de una jamba y salir «libre»; el cuerpo mide 35
    // de radio y se come la pared. El suavizado entonces recorta la esquina, la criatura se lanza a la
    // diagonal, el resolutor la frena y ahí se queda — visto jugando, media espalda dentro del muro.
    // Es lo mismo que `segment_is_clear_for_body` de `grid_gen` hace desde ADR-082, y por lo mismo.
    let (nx, nz) = (-dz / dist * radius, dx / dist * radius);

    for (ox, oz) in [(0.0, 0.0), (nx, nz), (-nx, -nz)] {
        let mut floor = a.y - BODY_M;
        for i in 1..=steps {
            let t = i as f32 / steps as f32;
            let (x, z) = (a.x + dx * t + ox, a.z + dz * t + oz);
            match floor_at(cache, x, z, floor) {
                Some(f) => floor = f,
                None => return false,
            }
        }
    }
    true
}

/// Quita los puntos intermedios que no hacen falta.
///
/// **Sin esto el camino sale robótico y no es un problema de estética.** Con celdas de medio metro,
/// subir una escalera son sesenta y un puntos; una criatura que los siga uno a uno anda a tirones y se
/// lee como un fallo de animación. Es el mismo trabajo que hace `string_pull` en `grid_gen`, con la
/// comprobación de recta de aquí — que además de paredes mira COTAS, porque en este mundo dos puntos
/// pueden verse y estar en plantas distintas.
pub fn simplify(
    cache: &Wg3CollisionCache,
    from: Vec3,
    path: &[Vec3],
    radius: f32,
    out: &mut Vec<Vec3>,
) {
    out.clear();
    if path.is_empty() {
        return;
    }
    // **El ancla es DÓNDE ESTÁ la criatura, no el primer punto del camino.** Anclando en `path[0]`,
    // el salto que de verdad va a dar primero —de su posición a ese punto— no lo validaba nadie: con
    // el cuerpo de por medio ese trozo puede atravesar una jamba, y es exactamente donde se la ve
    // clavarse. Y de paso, si desde donde está alcanza ya un punto más adelante, se ahorra el rodeo.
    let mut anchor = from;
    let mut i = 0;
    while i < path.len() {
        // Se avanza mientras la recta desde el ancla siga siendo andable, y se fija el último que lo
        // era. El punto final entra siempre.
        let mut last_ok: Option<usize> = None;
        let mut j = i;
        while j < path.len() && segment_is_clear(cache, anchor, path[j], radius) {
            last_ok = Some(j);
            j += 1;
        }
        // **Si ni el primero es alcanzable en recta, se emite igual y se sigue.** No es rendirse: ese
        // punto viene del A*, que ya lo dio por andable celda a celda; lo que no cabe es la RECTA con
        // el cuerpo, y el que la anda paso a paso sí llega. Emitir uno más lejos ahí sería inventarse
        // un atajo, que es el fallo que este suavizado existe para no cometer.
        let take = last_ok.unwrap_or(i);
        anchor = path[take];
        out.push(anchor);
        i = take + 1;
    }
}
