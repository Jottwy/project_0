//! ADR-116 — dónde nace cada jugador cuando el mundo los reparte.
//!
//! Hasta hoy todos nacían en el mismo metro cuadrado: `preferred_spawn()`, el centro del chunk
//! (0,0). No era una decisión de diseño, era el punto fijo que quedó cuando ADR-109 etapa 1 unificó
//! los dos caminos. Aquí el mundo reparte.
//!
//! # Qué es una UNIDAD de spawn
//!
//! El reparto no separa jugadores: separa **unidades** (D10). Hoy una unidad es exactamente un
//! jugador, porque no hay squads. El día que los haya, un squad será UNA unidad —sus miembros
//! comparten candidato y no consumen separación entre ellos— y ni el sorteo, ni la separación, ni
//! el colocador, ni el fallback cambian. Por eso el índice se llama `unit` y no `player`.
//!
//! # La celda es de ADR-103, no es una rejilla nueva
//!
//! La unidad de reparto es la celda de identidad de WG3: 900 m, múltiplo exacto de la región, así
//! que una frontera de identidad es siempre frontera de región. Lo que la celda **no** da es
//! separación: dos puntos en celdas contiguas pueden quedar a diez metros si caen pegados al borde.
//! De ahí el margen de [`POINT_MARGIN_M`] y la comprobación de D4, que se mide sobre el PUNTO.
//!
//! # Puro a propósito
//!
//! Ni una línea de aquí toca WG3. El anfitrión elige el punto cuando llega el handshake, donde no
//! hay ráster a mano; quien lo consume lo baja al suelo con `standable_near_bounded` (D6) allí
//! donde WG3 sí vive. La consecuencia está declarada en el checkpoint del Paso 6: un punto sin
//! sitio de pie a menos de 24 m NO salta al candidato siguiente, cae al fallback de D7.

use crate::utils::Vec3;
use crate::world::wg3::identity::IDENTITY_CELL_M;

/// ADR-116 Q1 — distancia mínima entre dos unidades de spawn, en metros.
///
/// **150 y no un número redondo cualquiera:** el anillo de streaming del cliente es de radio 1, o
/// sea 3×3 chunks de 50 m. Por debajo de 150 m dos jugadores comparten columnas cargadas y
/// «repartidos» deja de significar nada en la práctica.
pub const MIN_PLAYER_SEPARATION_M: f32 = 150.0;

/// ADR-116 Q2 — hasta dónde reparte, en celdas de identidad alrededor de la del origen.
///
/// 1 = 3×3 celdas de 900 m = 2,7 km de lado, nueve celdas para 8–12 jugadores. Radio 2 daría 4,5 km
/// y jugadores que no se cruzarían nunca en una sesión.
pub const DISTRIBUTION_RADIUS_CELLS: i32 = 1;

/// ADR-116 Q3 — cuántos candidatos se prueban antes de rendirse al fallback de D7.
///
/// **Sin medir, y declarado como tal.** Cada candidato que llega a consumirse cuesta precalentar el
/// ráster de WG3 más un barrido de 24 m en pasos de 0,5, y eso ocurre AL ENTRAR, que es cuando el
/// jugador mira una pantalla de carga. El número bueno sale de medir un candidato; 8 es el orden de
/// magnitud, y encaja con las 9 celdas que hay a radio 1.
pub const MAX_SPAWN_CANDIDATES: usize = 8;

/// Cuánto se aparta un punto del borde de su celda.
///
/// **Derivado, no elegido:** con media separación de margen por cada lado, dos puntos de celdas
/// contiguas quedan a `IDENTITY_CELL_M - 2 * POINT_MARGIN_M` = 750 m como mínimo, muy por encima de
/// los 150 exigidos. Así la separación de D4 se cumple casi siempre POR CONSTRUCCIÓN y el rechazo
/// de candidatos queda para el caso de verdad interesante: un jugador restaurado que apareció donde
/// nadie lo sorteó.
pub const POINT_MARGIN_M: f32 = MIN_PLAYER_SEPARATION_M / 2.0;

/// Sal del orden de celdas. Propia, como la de cada sorteo desde ADR-043: sin ella, qué celda le
/// toca a una unidad correlacionaría con cualquier otra decisión que hashee las mismas
/// coordenadas, y eso sale como «siempre nacemos en el mismo tipo de sitio».
const SPAWN_CELL_SALT: u64 = 0x5A17_0116_CE11_0000;

/// Sal del punto DENTRO de la celda. Separada de la anterior por el mismo motivo: si el orden de
/// celdas y la posición dentro de una salieran del mismo flujo, mover una movería la otra.
const SPAWN_POINT_SALT: u64 = 0x9E37_79B9_7F4A_7C15;

/// ADR-136 Q3 — a qué distancia del invitador nace un invitado, en metros.
///
/// Un radio de cuerpo y un paso: lo bastante lejos para no nacer DENTRO del invitador, lo bastante
/// cerca para que `standable_near_bounded` (anillos de 0,5 m, `same_storey`) lo deje en la misma
/// sala. **Sin medir** en salas pequeñas; si el desplazamiento cae en pared, el colocador lo trae
/// de vuelta.
pub const INVITE_SPAWN_OFFSET_M: f32 = 2.0;

/// Sal de la dirección del desplazamiento de un invitado. Propia, como las dos de arriba.
const INVITE_DIRECTION_SALT: u64 = 0x1A5E_0136_D4D4_0001;

/// La altura provisional con la que sale un candidato. No es «el suelo»: el suelo lo resuelve
/// `standable_near_bounded` (D6) en quien consume el punto, que es el único que tiene el ráster.
/// Misma altura que usa `preferred_spawn`, para que el colocador arranque desde donde siempre.
const PROVISIONAL_Y: f32 = 1.8;

/// Hash determinista de 64 bits. Local y no el de `wg3::hash` a propósito: aquel es un ESPEJO EXACTO
/// de una clase de C# y cambiarlo cambia el mundo dibujado, así que no es sitio para colgarle un
/// consumidor nuevo. Esto no cruza el cable ni lo recalcula nadie más.
fn mix(mut h: u64) -> u64 {
    h ^= h >> 33;
    h = h.wrapping_mul(0xFF51_AFD7_ED55_8CCD);
    h ^= h >> 33;
    h = h.wrapping_mul(0xC4CE_B9FE_1A85_EC53);
    h ^= h >> 33;
    h
}

fn hash4(world_seed: u64, salt: u64, a: i64, b: i64) -> u64 {
    mix(world_seed
        .wrapping_mul(0x9E37_79B9_7F4A_7C15)
        .wrapping_add(salt)
        .rotate_left(17)
        ^ mix((a as u64).wrapping_mul(0xD6E8_FD9D_AA28_2219))
        ^ mix((b as u64).wrapping_mul(0xA24B_AED4_963E_E407)))
}

/// `h` en `[0, 1)`.
fn to_unit(h: u64) -> f32 {
    ((h >> 11) as f64 / (1u64 << 53) as f64) as f32
}

/// Las celdas del área de reparto, en coordenadas de celda de identidad. `(2R+1)²` celdas alrededor
/// de la del origen.
fn cells() -> Vec<(i32, i32)> {
    let r = DISTRIBUTION_RADIUS_CELLS;
    let mut out = Vec::with_capacity(((2 * r + 1) * (2 * r + 1)) as usize);
    for cz in -r..=r {
        for cx in -r..=r {
            out.push((cx, cz));
        }
    }
    out
}

/// El orden en que una unidad prueba las celdas: las mismas nueve, barajadas por
/// `(world_seed, unit)`.
///
/// Barajadas y no recorridas en orden fijo porque si no todas las unidades empezarían por la misma
/// celda y el primer candidato de cada una chocaría siempre con el de la anterior — el reparto
/// funcionaría igual, pero a base de rechazar candidatos en vez de repartir.
fn cell_order(world_seed: u64, unit: u32) -> Vec<(i32, i32)> {
    let mut cells = cells();
    cells.sort_by_key(|(cx, cz)| {
        hash4(
            world_seed,
            SPAWN_CELL_SALT ^ (unit as u64).wrapping_mul(0x1000_0000_0000_0001),
            *cx as i64,
            *cz as i64,
        )
    });
    cells
}

/// El candidato número `k` de una unidad: un punto dentro de la celda `k`-ésima de su orden.
///
/// `None` cuando `k` pasa del número de celdas: no se dan vueltas al área: repetir celdas sería
/// devolver el mismo punto con otro nombre.
pub fn candidate(world_seed: u64, unit: u32, k: usize) -> Option<Vec3> {
    let order = cell_order(world_seed, unit);
    let (cx, cz) = *order.get(k)?;

    let half = IDENTITY_CELL_M * 0.5 - POINT_MARGIN_M;
    let key = (unit as i64) << 8 | k as i64;
    let u = to_unit(hash4(world_seed, SPAWN_POINT_SALT, key, cx as i64));
    let v = to_unit(hash4(
        world_seed,
        SPAWN_POINT_SALT,
        key,
        (cz as i64) ^ 0x5EED,
    ));

    let centre_x = (cx as f32 + 0.5) * IDENTITY_CELL_M;
    let centre_z = (cz as f32 + 0.5) * IDENTITY_CELL_M;
    Some(Vec3::new(
        centre_x + (u * 2.0 - 1.0) * half,
        PROVISIONAL_Y,
        centre_z + (v * 2.0 - 1.0) * half,
    ))
}

/// ADR-116 D4/D5/D7 — el punto de una unidad, esquivando lo ya ocupado.
///
/// `occupied` son los puntos ya asignados en esta sesión MÁS las posiciones de los jugadores vivos.
/// Las dos cosas, porque un jugador restaurado de su fichero puede estar donde nadie lo sorteó y no
/// ocupa ninguna celda (D8).
///
/// Devuelve `None` cuando los `MAX_SPAWN_CANDIDATES` se agotan: quien llama aplica el fallback de
/// D7 (el spawn del origen, con aviso). No se inventa un punto aquí — la decisión de qué hacer sin
/// candidatos es del llamante y está escrita en el ADR, no escondida en una función pura.
pub fn choose_spawn(world_seed: u64, unit: u32, occupied: &[Vec3]) -> Option<Vec3> {
    for k in 0..MAX_SPAWN_CANDIDATES {
        let Some(p) = candidate(world_seed, unit, k) else {
            break; // se acabaron las celdas antes que los intentos
        };
        if occupied
            .iter()
            .all(|o| o.distance_xz(p) >= MIN_PLAYER_SEPARATION_M)
        {
            return Some(p);
        }
    }
    None
}

/// ADR-136 D4 — un punto a [`INVITE_SPAWN_OFFSET_M`] del invitador, en una dirección determinista
/// por `(world_seed, peer)`.
///
/// Misma altura que el invitador: la planta la conserva `standable_near_bounded` en quien consume
/// el punto (ADR-116 D6), igual que con cualquier otro. Puro, como todo lo de este módulo: el
/// anfitrión lo llama al llegar el handshake, donde no hay ráster a mano.
pub fn beside(world_seed: u64, peer: u16, anchor: Vec3) -> Vec3 {
    let u = to_unit(hash4(world_seed, INVITE_DIRECTION_SALT, peer as i64, 0));
    let angle = u * std::f32::consts::TAU;
    Vec3::new(
        anchor.x + angle.cos() * INVITE_SPAWN_OFFSET_M,
        anchor.y,
        anchor.z + angle.sin() * INVITE_SPAWN_OFFSET_M,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    /// ADR-136 D4 — el invitado nace a la distancia del ADR, a la altura del invitador, y siempre
    /// en el mismo sitio para el mismo par (semilla, peer).
    #[test]
    fn un_invitado_nace_a_dos_metros_del_invitador_y_siempre_en_el_mismo_sitio() {
        let anchor = Vec3::new(100.0, 4.7, -250.0);
        let p = beside(42, 7, anchor);
        assert!(
            (p.distance_xz(anchor) - INVITE_SPAWN_OFFSET_M).abs() < 1e-4,
            "a {:.3} m del invitador",
            p.distance_xz(anchor)
        );
        assert_eq!(
            p.y, anchor.y,
            "la planta es la del invitador; el suelo lo pone D6"
        );
        assert_eq!(p, beside(42, 7, anchor), "determinista");

        let otro = beside(42, 8, anchor);
        assert_ne!(
            (p.x, p.z),
            (otro.x, otro.z),
            "dos invitados no nacen en el mismo punto"
        );
    }

    #[test]
    fn el_reparto_es_determinista_por_semilla() {
        let a = choose_spawn(42, 0, &[]).expect("unidad 0 tiene punto");
        let b = choose_spawn(42, 0, &[]).expect("y el mismo punto");
        assert_eq!(a, b);

        let otra = choose_spawn(43, 0, &[]).expect("otra semilla, otro punto");
        assert_ne!(
            (a.x, a.z),
            (otra.x, otra.z),
            "dos mundos distintos no pueden repartir igual"
        );
    }

    #[test]
    fn dos_unidades_no_reciben_el_mismo_punto() {
        let mut asignados: Vec<Vec3> = Vec::new();
        for unit in 0..9u32 {
            let p = choose_spawn(42, unit, &asignados)
                .unwrap_or_else(|| panic!("la unidad {unit} debería caber en 9 celdas"));
            for previo in &asignados {
                assert!(
                    previo.distance_xz(p) >= MIN_PLAYER_SEPARATION_M,
                    "unidad {unit} nace a {:.1} m de otra",
                    previo.distance_xz(p)
                );
            }
            asignados.push(p);
        }
        assert_eq!(asignados.len(), 9);
    }

    /// D4: un jugador restaurado de su fichero no ocupa ninguna celda, pero sí ocupa un SITIO.
    #[test]
    fn un_jugador_restaurado_tambien_aparta_candidatos() {
        let solo = choose_spawn(42, 0, &[]).expect("sin nadie, el primer candidato");
        // Alguien está justo ahí: el primer candidato deja de valer.
        let con_vecino = choose_spawn(42, 0, &[solo]).expect("quedan celdas");
        assert_ne!((solo.x, solo.z), (con_vecino.x, con_vecino.z));
        assert!(solo.distance_xz(con_vecino) >= MIN_PLAYER_SEPARATION_M);
    }

    /// D7: con todo ocupado no se inventa un punto — se devuelve `None` y decide el llamante.
    #[test]
    fn sin_candidatos_validos_no_se_inventa_un_punto() {
        // Ocupa TODOS los candidatos posibles de la unidad 0.
        let ocupado: Vec<Vec3> = (0..MAX_SPAWN_CANDIDATES)
            .filter_map(|k| candidate(42, 0, k))
            .collect();
        assert!(!ocupado.is_empty());
        assert!(choose_spawn(42, 0, &ocupado).is_none());
    }

    /// El margen de la celda no es decorativo: es lo que hace que la separación se cumpla casi
    /// siempre por construcción en vez de a base de rechazar candidatos.
    #[test]
    fn el_margen_mantiene_los_puntos_dentro_de_su_celda() {
        for unit in 0..9u32 {
            for k in 0..MAX_SPAWN_CANDIDATES {
                let Some(p) = candidate(7, unit, k) else {
                    continue;
                };
                let cx = (p.x / IDENTITY_CELL_M).floor();
                let cz = (p.z / IDENTITY_CELL_M).floor();
                let dx = (p.x - (cx + 0.5) * IDENTITY_CELL_M).abs();
                let dz = (p.z - (cz + 0.5) * IDENTITY_CELL_M).abs();
                let half = IDENTITY_CELL_M * 0.5 - POINT_MARGIN_M;
                assert!(
                    dx <= half + 0.001 && dz <= half + 0.001,
                    "unit={unit} k={k}"
                );
            }
        }
    }

    #[test]
    fn el_reparto_no_sale_del_area_declarada() {
        let limite = (DISTRIBUTION_RADIUS_CELLS + 1) as f32 * IDENTITY_CELL_M;
        for unit in 0..9u32 {
            let p = choose_spawn(1234, unit, &[]).expect("hay sitio");
            assert!(p.x.abs() < limite && p.z.abs() < limite);
        }
    }

    #[test]
    fn cada_unidad_prueba_las_celdas_en_su_propio_orden() {
        let a = cell_order(42, 0);
        let b = cell_order(42, 1);
        assert_eq!(a.len(), 9);
        assert_ne!(
            a, b,
            "si todas empezaran por la misma celda, todas chocarían"
        );
    }
}
