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
    let start_floor = from.y - BODY_M;

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

    let mut best = (start_key, h(start_cell));
    let mut found: Option<CellKey> = None;

    while let Some(Node { key, .. }) = open.pop() {
        if (key.0, key.1) == goal_cell {
            found = Some(key);
            stats.reached = true;
            break;
        }
        if stats.expansions >= NAV_MAX_EXPANSIONS {
            break;
        }
        stats.expansions += 1;

        let hk = h((key.0, key.1));
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
            let Some(nfloor) = floor_at(cache, nx, nz, floor) else {
                continue;
            };
            // **El enlace usa el mismo escalón que sube el jugador.** Es lo que hace que una escalera
            // se pueda navegar y que el suelo de un atrio no conecte con su balcón.
            if ((nfloor - floor).abs() * 100.0) as i32 > MAX_WALK_STEP_CM {
                continue;
            }
            let nk: CellKey = (nc.0, nc.1, (nfloor * 100.0).round() as i32);
            let ng = g + 1;
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
