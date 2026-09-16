//! El modelo de propagación — Fase 1 de `docs/SMILER-DESIGN.md` §10.
//!
//! # La representación: un grafo de (celda, hueco), no esferas flotantes
//!
//! `Wg3Raster` ya guarda el mundo como columnas de TRAMOS MACIZOS (`Span`) por celda de 0,5 m
//! (ADR-095 D1/D2, `world::wg3::raster`). El complemento de esos tramos —los huecos verticales de
//! esa columna— es exactamente «dónde puede haber Smiler», así que la representación espacial no
//! inventa unidad nueva: es la MISMA rejilla, leída al revés.
//!
//! Un [`Band`] es uno de esos huecos: `[bottom_m, top_m)`. Dos bandas de columnas vecinas están
//! CONECTADAS si sus rangos de altura se solapan; la anchura de ese solape (en metros) es la
//! CONDUCTANCIA del paso entre ellas — un vano de 2 m dado dueño conduce mucho, una rendija de
//! 8 cm conduce poco, y una pared sin solape en absoluto no conduce nada. Todo esto sale de restar
//! dos números: no hay caso especial para «puerta», «pilar» ni «escalera» (§15.1 del diseño).
//!
//! # Por qué esto también sube escaleras y se derrama por pozos SIN lógica vertical aparte
//!
//! Dos bandas de la MISMA columna nunca se tocan directamente —si se tocaran, `Wg3RasterBuilder`
//! las habría fundido en una sola banda maciza—, así que subir de planta tiene que pasar SIEMPRE
//! por una columna VECINA. Un pozo o un hueco de escalera es, visto así, una columna cuya banda es
//! más ALTA que las de sus vecinas de ambos lados: esa única banda se solapa con la banda de abajo
//! Y con la de arriba, y las conecta. Un peldaño de escalera es lo mismo en miniatura: cada tramo
//! sube un poco la cota de su banda respecto al anterior, y mientras el solape vertical con el
//! vecino sea mayor que [`MIN_OVERLAP_M`] la cadena de solapes conecta el pie con el rellano sin
//! que este módulo necesite saber que es una escalera.
//!
//! # Qué NO hace este módulo (Fase 1, ver `smiler/mod.rs`)
//!
//! Nada de IA, nada de luz, nada de VFX. [`SmilerFlow::step`] difunde densidad entre bandas
//! conectadas con un sesgo de intención hacia un punto fijo y una flotabilidad simple; eso es
//! toda la «inteligencia» que tiene. Las constantes de ajuste (`OUTFLOW_RATE`, `INTENT_BIAS`,
//! `BUOYANCY`) son PLACEHOLDER sin calibrar — el objetivo de esta fase es validar el MODELO, no
//! el ritmo del encuentro.

use std::collections::{BTreeMap, BTreeSet};

use crate::utils::Vec3;
use crate::world::wg3::collision::Wg3CollisionCache;
use crate::world::wg3::nav::{cell_centre, cell_of};
use crate::world::wg3::raster::{Span, Wg3Raster, CM_PER_M, WG3_CELL_M};

/// Techo y suelo de la búsqueda vertical. Generoso a propósito: cubre varias plantas de sobra
/// (una planta mide del orden de 3,3 m, `STOREY_HEIGHT_CM`), y acotar de menos recortaría un hueco
/// real (un atrio que sube más de la cuenta) mientras que acotar de más sólo alarga un `Vec` un
/// poco. Nada de esto es el mundo servido: es sólo el rango en el que ESTE prototipo mira.
pub const WORLD_FLOOR_M: f32 = -20.0;
pub const WORLD_CEILING_M: f32 = 40.0;

/// Solape vertical mínimo, en metros, para que dos bandas cuenten como conectadas. Filtra el
/// ruido de redondeo (el ráster trabaja en centímetros enteros) sin descartar una rendija real:
/// una rendija de prueba de 8 cm (§23 C4 del diseño) sigue siendo, con mucho, mayor que esto.
const MIN_OVERLAP_M: f32 = 0.01;

/// Tope de conductancia por arista. Sin él, un atrio de 6 m de alto pesaría tres veces más que
/// un pasillo normal de 2 m sólo por ser más alto, y la comparación entre «vano ancho» y
/// «rendija» dejaría de ser sobre la ANCHURA del paso para pasar a ser sobre la altura del cuarto
/// de al lado. Corta en un valor mayor que cualquier vano de la referencia (ADR-095 pasillos ~2 m,
/// atrios hasta 6,4 m) para que un vano normal no llegue a tocar el tope.
const MAX_CONDUCTANCE_M: f32 = 2.5;

/// Fracción de la densidad de una banda que sale por segundo POR CADA METRO de conductancia
/// disponible. PLACEHOLDER sin calibrar (mismo criterio que `PHANTOM_*` antes de jugarlas,
/// ADR-075: «primero se juega, luego se decide qué palanca merece env»). Lo único que importa
/// para esta fase es que el CAUDAL escale con la conductancia, no el número exacto.
const OUTFLOW_RATE: f32 = 1.5;

/// Cuánto se favorece una arista que acerca al objetivo, como multiplicador de su peso.
/// PLACEHOLDER.
const INTENT_BIAS: f32 = 1.6;

/// Cuánto empuja la flotabilidad hacia arriba (con poca densidad) o hacia abajo (con mucha),
/// como fracción extra del peso de la arista vertical correspondiente. PLACEHOLDER.
const BUOYANCY: f32 = 0.5;

/// Por debajo de esto una banda se considera vacía y se poda: sin esto el mapa de bandas
/// crecería para siempre con restos de polvo numérico que ya no significan nada.
const DENSITY_EPS: f32 = 1e-4;

const NEIGHBOR_OFFSETS: [(i32, i32); 4] = [(1, 0), (-1, 0), (0, 1), (0, -1)];

/// Un hueco vertical de una columna: `[bottom_m, top_m)`. Es el complemento de los `Span` macizos
/// de esa columna, acotado a `[WORLD_FLOOR_M, WORLD_CEILING_M]`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Band {
    pub bottom_m: f32,
    pub top_m: f32,
}

impl Band {
    fn contains(&self, y: f32) -> bool {
        y >= self.bottom_m && y < self.top_m
    }

    /// Cuánto se solapan dos bandas, en metros. Cero (nunca negativo) si no llegan a tocarse.
    fn overlap_m(&self, other: &Band) -> f32 {
        (self.top_m.min(other.top_m) - self.bottom_m.max(other.bottom_m)).max(0.0)
    }

    fn mid_m(&self) -> f32 {
        (self.bottom_m + self.top_m) * 0.5
    }
}

/// Los huecos de una columna, de abajo a arriba. `spans` debe venir ya ordenado por `bottom_cm` y
/// sin solapes entre sí — que es exactamente lo que entrega `Wg3Raster::column_at`
/// (`Wg3RasterBuilder::finish` los funde al construir el ráster, raster.rs:393-417).
///
/// Se trabaja en centímetros enteros hasta el final por la misma razón que el propio `Span`: la
/// resta de dos enteros no arrastra el error de redondeo que sí tendría acumular en `f32` a lo
/// largo de una columna con muchos tramos.
fn hollow_bands(spans: &[Span]) -> Vec<Band> {
    let floor_cm = (WORLD_FLOOR_M * CM_PER_M).round() as i32;
    let ceiling_cm = (WORLD_CEILING_M * CM_PER_M).round() as i32;
    let mut out = Vec::with_capacity(spans.len() + 1);
    let mut cursor_cm = floor_cm;
    for span in spans {
        let bottom = (span.bottom_cm as i32).clamp(floor_cm, ceiling_cm);
        if bottom > cursor_cm {
            out.push(Band {
                bottom_m: cursor_cm as f32 / CM_PER_M,
                top_m: bottom as f32 / CM_PER_M,
            });
        }
        cursor_cm = cursor_cm.max((span.top_cm as i32).clamp(floor_cm, ceiling_cm));
    }
    if cursor_cm < ceiling_cm {
        out.push(Band {
            bottom_m: cursor_cm as f32 / CM_PER_M,
            top_m: ceiling_cm as f32 / CM_PER_M,
        });
    }
    out
}

/// Acceso a la geometría que el modelo necesita, y nada más: los tramos macizos de una columna, y
/// si esa columna tiene datos cargados en absoluto.
///
/// Existe para que el mismo algoritmo sirva tanto contra un [`Wg3Raster`] aislado (pruebas
/// controladas, sin depender del manifiesto ni de una semilla) como contra el
/// [`Wg3CollisionCache`] real que ya usan el robapieles y los facelings — sin este seam no hay
/// forma de probar el modelo con geometría exacta y conocida sin arrastrar la generación completa
/// del mundo a cada test.
pub trait SmilerGeometry {
    /// Los tramos macizos de la columna que contiene `(x, z)`, ordenados y sin solapes.
    fn column_spans(&self, x: f32, z: f32) -> &[Span];
    /// `false` si `(x, z)` no tiene ráster cargado en absoluto. Sin esta distinción, «columna sin
    /// datos» y «columna genuinamente vacía» serían la misma cosa, y el modelo fallaría hacia
    /// hueco en vez de hacia macizo — al revés que el resto del proyecto
    /// (`Wg3CollisionCache::blocked_at`: «un chunk que no está en el caché BLOQUEA»).
    fn has_data(&self, x: f32, z: f32) -> bool;
}

impl SmilerGeometry for Wg3Raster {
    fn column_spans(&self, x: f32, z: f32) -> &[Span] {
        self.column_at(x, z)
    }
    fn has_data(&self, x: f32, z: f32) -> bool {
        self.cell_of(x, z).is_some()
    }
}

impl SmilerGeometry for Wg3CollisionCache {
    fn column_spans(&self, x: f32, z: f32) -> &[Span] {
        self.raster_for(x, z)
            .map(|r| r.column_at(x, z))
            .unwrap_or(&[])
    }
    fn has_data(&self, x: f32, z: f32) -> bool {
        self.raster_for(x, z).is_some()
    }
}

/// Identidad de una banda: la celda XZ del ráster (0,5 m) y el índice de la banda dentro de esa
/// columna, de abajo a arriba. Estable mientras la geometría no cambie a media simulación —cierto
/// en esta fase, que no construye ni demuele nada.
type BandKey = (i32, i32, u16);

#[derive(Debug, Clone, Copy)]
struct BandState {
    band: Band,
    density: f32,
}

/// El campo de presencia: cuánta densidad hay en cada banda hueca tocada hasta ahora.
///
/// `BTreeMap` y no `HashMap` a propósito (regla dura 13, CLAUDE.md): la clave ordena por
/// construcción, así que iterar `self.bands` para calcular un paso o para emitir `nodes()` nunca
/// depende del orden de inserción ni del hasher del proceso. Con las docenas de bandas activas que
/// se esperan en un prototipo, el coste de `BTreeMap` frente a `HashMap` no se nota.
#[derive(Debug, Default)]
pub struct SmilerFlow {
    bands: BTreeMap<BandKey, BandState>,
}

/// Una banda con presencia, en forma de punto+radio+densidad — la «esfera gris» de depuración
/// que pide la Fase 1. **Puramente derivado y de usar y tirar**: no es la representación interna
/// (esa es [`SmilerFlow`] por dentro) y no viaja a ningún sitio. Nunca en el wire, nunca en el
/// juego real — ver `smiler/mod.rs`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SmilerNode {
    pub pos: Vec3,
    pub radius: f32,
    pub density: f32,
}

impl SmilerFlow {
    pub fn new() -> Self {
        Self::default()
    }

    /// Masa total presente ahora mismo. Iterar `BTreeMap::values()` ya es determinista (la clave
    /// ordena la iteración), así que la suma sale siempre en el mismo orden de términos.
    pub fn total_density(&self) -> f32 {
        self.bands.values().map(|s| s.density).sum()
    }

    /// Cuánta densidad hay ahora mismo en la banda que contiene `at`. Cero tanto si esa banda
    /// nunca ha tenido presencia como si `at` cae en materia sólida o en una columna sin datos.
    pub fn density_at(&self, geometry: &impl SmilerGeometry, at: Vec3) -> f32 {
        let Some(key) = self.band_key_at(geometry, at) else {
            return 0.0;
        };
        self.bands.get(&key).map_or(0.0, |s| s.density)
    }

    /// Añade `amount` de densidad a la banda que contiene `at`. Devuelve `false` sin tocar nada
    /// si `at` cae en materia sólida o en una columna sin ráster cargado — soltar el Smiler
    /// «dentro de una pared» por un punto mal elegido no debe fallar en silencio.
    pub fn seed(&mut self, geometry: &impl SmilerGeometry, at: Vec3, amount: f32) -> bool {
        let (cell, band) = match self.locate(geometry, at) {
            Some(found) => found,
            None => return false,
        };
        let key = (cell.0, cell.1, band.1);
        self.bands
            .entry(key)
            .or_insert(BandState {
                band: band.0,
                density: 0.0,
            })
            .density += amount;
        true
    }

    /// Comprobación de la invariante física dura (§23 C1 del diseño): ninguna banda activa puede
    /// solapar materia sólida. Debería ser imposible por construcción —una banda activa sólo
    /// nace de `hollow_bands`—, así que esto es un cinturón, no el mecanismo: si algún día
    /// `hollow_bands` tuviera un error de un límite, esto lo grita en un test en vez de dejar
    /// pasar un Smiler dentro de una pared.
    pub fn no_band_overlaps_solid(&self, geometry: &impl SmilerGeometry) -> bool {
        for (&(cx, cz, bi), state) in &self.bands {
            let (x, z) = cell_centre(cx, cz);
            if !geometry.has_data(x, z) {
                return false;
            }
            let spans = geometry.column_spans(x, z);
            let bands = hollow_bands(spans);
            if bands.get(bi as usize) != Some(&state.band) {
                return false;
            }
            let mid_cm = (state.band.mid_m() * CM_PER_M).round() as i32;
            if spans.iter().any(|s| s.contains(mid_cm)) {
                return false;
            }
        }
        true
    }

    /// Hasta `max_nodes` bandas con presencia, de más a menos densa. Desempate por [`BandKey`]
    /// ascendente para que dos llamadas con el mismo estado den siempre el mismo orden (regla
    /// dura 13) — sin desempate, dos bandas empatadas a densidad podrían intercambiarse de un
    /// build a otro sin que nada en el estado lógico haya cambiado.
    pub fn nodes(&self, max_nodes: usize) -> Vec<SmilerNode> {
        // Claves y estados COPIADOS (las dos son `Copy`), no prestados: comparar `a.0.cmp(&b.0)`
        // sobre un `BandKey` de verdad es inequívoco, mientras que ordenar tuplas de REFERENCIAS
        // (`&BandKey`) dejaría dos `impl Ord` candidatos —el de `BandKey` vía autoderef y el de
        // `&BandKey` por el blanket `impl<T: Ord> Ord for &T`— compitiendo por resolución de
        // método. Copiar cuesta nada con las docenas de bandas que se esperan aquí.
        let mut items: Vec<(BandKey, BandState)> =
            self.bands.iter().map(|(&k, &v)| (k, v)).collect();
        items.sort_by(|a, b| {
            b.1.density
                .partial_cmp(&a.1.density)
                .unwrap_or(std::cmp::Ordering::Equal)
                .then_with(|| a.0.cmp(&b.0))
        });
        items.truncate(max_nodes);
        items
            .into_iter()
            .map(|((cx, cz, _), state)| {
                let (x, z) = cell_centre(cx, cz);
                SmilerNode {
                    pos: Vec3::new(x, state.band.mid_m(), z),
                    radius: (WG3_CELL_M * 0.5) * state.density.clamp(0.05, 1.0),
                    density: state.density,
                }
            })
            .collect()
    }

    /// Avanza la simulación `dt` segundos. `target`, si se da, sesga el flujo hacia ese punto —
    /// es TODA la «intención» que existe en esta fase (sin FSM, ver `smiler/mod.rs`).
    pub fn step(&mut self, geometry: &impl SmilerGeometry, dt: f32, target: Option<Vec3>) {
        if self.bands.is_empty() || dt <= 0.0 {
            return;
        }

        // 1. Qué celdas hacen falta: toda banda activa, más sus cuatro vecinas horizontales —
        //    para poder fluir HACIA una celda todavía sin presencia. `BTreeSet` por lo mismo que
        //    `BTreeMap` arriba: orden estable, no accidente del hasher.
        let mut candidate_cells: BTreeSet<(i32, i32)> = BTreeSet::new();
        for &(cx, cz, _) in self.bands.keys() {
            candidate_cells.insert((cx, cz));
            for (dx, dz) in NEIGHBOR_OFFSETS {
                candidate_cells.insert((cx + dx, cz + dz));
            }
        }

        // 2. Bandas REALES de cada celda candidata, leídas una sola vez por celda y reutilizadas
        //    para todas las aristas que la tocan. Ausente = sin datos cargados (falla hacia
        //    bloqueado, nunca hacia hueco).
        let mut cell_bands: BTreeMap<(i32, i32), Vec<Band>> = BTreeMap::new();
        for &cell in &candidate_cells {
            let (x, z) = cell_centre(cell.0, cell.1);
            if geometry.has_data(x, z) {
                cell_bands.insert(cell, hollow_bands(geometry.column_spans(x, z)));
            }
        }

        // 3. Para cada banda activa, cuánto sale y hacia dónde. Se calcula TODO sobre una
        //    instantánea de `self.bands` (el propio `BTreeMap`, prestado sólo de lectura) antes
        //    de tocar nada: mutar mientras se itera invalidaría las cuentas de las demás bandas.
        let mut outflow: BTreeMap<BandKey, f32> = BTreeMap::new();
        let mut inflow: BTreeMap<BandKey, f32> = BTreeMap::new();

        for (&(cx, cz, bi), state) in &self.bands {
            if state.density <= DENSITY_EPS {
                continue;
            }
            let Some(band) = cell_bands.get(&(cx, cz)).and_then(|b| b.get(bi as usize)) else {
                // La celda perdió sus datos o la banda ya no coincide con la geometría: no debería
                // pasar mientras el mundo no cambie a media simulación, pero fallar hacia «se
                // queda quieta» es más seguro que fluir sobre una banda que ya no existe.
                continue;
            };
            let (cx_m, cz_m) = cell_centre(cx, cz);

            let mut edges: Vec<(BandKey, f32)> = Vec::new();
            for (dx, dz) in NEIGHBOR_OFFSETS {
                let ncell = (cx + dx, cz + dz);
                let Some(neighbor_bands) = cell_bands.get(&ncell) else {
                    continue;
                };
                let (nx_m, nz_m) = cell_centre(ncell.0, ncell.1);
                for (nbi, nband) in neighbor_bands.iter().enumerate() {
                    let overlap = band.overlap_m(nband);
                    if overlap < MIN_OVERLAP_M {
                        continue;
                    }
                    let mut weight = overlap.min(MAX_CONDUCTANCE_M);

                    if let Some(t) = target {
                        let here = Vec3::new(cx_m, band.mid_m(), cz_m).distance_xz(t);
                        let there = Vec3::new(nx_m, nband.mid_m(), nz_m).distance_xz(t);
                        if there < here {
                            weight *= INTENT_BIAS;
                        }
                    }
                    if nband.mid_m() > band.mid_m() {
                        weight *= 1.0 + BUOYANCY * (1.0 - state.density).max(0.0);
                    } else if nband.mid_m() < band.mid_m() {
                        weight *= 1.0 + BUOYANCY * state.density.max(0.0);
                    }

                    edges.push(((ncell.0, ncell.1, nbi as u16), weight));
                }
            }
            if edges.is_empty() {
                continue;
            }
            let total_weight: f32 = edges.iter().map(|(_, w)| w).sum();
            if total_weight <= 0.0 {
                continue;
            }

            // El caudal escala con la conductancia TOTAL disponible, no sólo con cuánta densidad
            // hay: es lo que hace que una rendija (conductancia baja) deje salir mucha menos masa
            // por segundo que un vano ancho, en vez de repartir siempre la misma salida entre
            // aristas distintas (§23 C4 del diseño).
            let leaving_frac = (OUTFLOW_RATE * total_weight * dt).min(1.0);
            let leaving = state.density * leaving_frac;
            if leaving <= 0.0 {
                continue;
            }
            *outflow.entry((cx, cz, bi)).or_insert(0.0) += leaving;
            for (key, weight) in edges {
                *inflow.entry(key).or_insert(0.0) += leaving * (weight / total_weight);
            }
        }

        // 4. Aplicar: primero lo que sale, luego lo que entra. `BTreeMap` itera en orden de
        //    clave en las dos pasadas, así que el resultado no depende de en qué orden `step`
        //    encontró las aristas más arriba (regla dura 13).
        for (&key, &amount) in &outflow {
            if let Some(state) = self.bands.get_mut(&key) {
                state.density = (state.density - amount).max(0.0);
            }
        }
        for (&key, &amount) in &inflow {
            let (cx, cz, bi) = key;
            let Some(band) = cell_bands.get(&(cx, cz)).and_then(|b| b.get(bi as usize)) else {
                continue;
            };
            self.bands
                .entry(key)
                .or_insert(BandState {
                    band: *band,
                    density: 0.0,
                })
                .density += amount;
        }

        // 5. Poda: una banda que se ha quedado sin nada no debe seguir viva para siempre.
        self.bands.retain(|_, s| s.density > DENSITY_EPS);
    }

    /// La celda y la banda (con su índice) que contienen `at`, o `None` si `at` cae en materia
    /// sólida o en una columna sin datos.
    fn locate(
        &self,
        geometry: &impl SmilerGeometry,
        at: Vec3,
    ) -> Option<((i32, i32), (Band, u16))> {
        let cell = cell_of(at.x, at.z);
        let (x, z) = cell_centre(cell.0, cell.1);
        if !geometry.has_data(x, z) {
            return None;
        }
        let bands = hollow_bands(geometry.column_spans(x, z));
        let index = bands.iter().position(|b| b.contains(at.y))?;
        Some((cell, (bands[index], index as u16)))
    }

    fn band_key_at(&self, geometry: &impl SmilerGeometry, at: Vec3) -> Option<BandKey> {
        let (cell, (_, index)) = self.locate(geometry, at)?;
        Some((cell.0, cell.1, index))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::wg3::placement::PlacedBox;
    use crate::world::wg3::raster::Wg3RasterBuilder;

    fn wall_box(center: [f32; 3], size: [f32; 3]) -> PlacedBox {
        PlacedBox {
            center,
            size,
            yaw_degrees: 0.0,
            kind: 0,
        }
    }

    /// Sala abierta (0..10, 0..8 en XZ) partida por una pared en x≈5 que sólo llega hasta z=5 —
    /// deja una boca abierta de 3 m en z∈(5,8) por la que rodear. La pared cubre TODA la vertical
    /// de interés (más allá de [`WORLD_FLOOR_M`], [`WORLD_CEILING_M`]) para que no haya un atajo
    /// por encima o por debajo: la única ruta es la boca.
    fn raster_with_partial_wall() -> Wg3Raster {
        let mut b = Wg3RasterBuilder::covering(0.0, 0.0, 10.0, 8.0);
        b.add_box(&wall_box([5.0, 10.0, 2.5], [0.4, 60.0, 5.0]));
        b.finish()
    }

    #[test]
    fn no_penetra_solido_y_rodea_un_obstaculo() {
        let raster = raster_with_partial_wall();
        let mut flow = SmilerFlow::new();
        let source = Vec3::new(1.0, 1.0, 2.0);
        let target = Vec3::new(9.0, 1.0, 2.0); // detrás de la pared, en línea recta bloqueada
        assert!(
            flow.seed(&raster, source, 1.0),
            "el punto de origen no debería caer en sólido"
        );

        for _ in 0..400 {
            flow.step(&raster, 0.1, Some(target));
            assert!(
                flow.no_band_overlaps_solid(&raster),
                "una banda activa solapa materia sólida a mitad de simulación"
            );
        }

        assert!(
            flow.density_at(&raster, target) > 0.0,
            "el humo no llegó al otro lado de la pared — o atravesó materia, o el modelo no rodea"
        );
    }

    /// Dos escenarios IDÉNTICOS salvo por la altura del hueco que separa dos salas: 8 cm (rendija)
    /// contra 2 m (vano ancho). El resto de la geometría —dos salas totalmente abiertas a ambos
    /// lados— es igual para que la única variable sea la anchura del paso.
    fn two_rooms_with_gap(gap_bottom_m: f32, gap_top_m: f32) -> Wg3Raster {
        let mut b = Wg3RasterBuilder::covering(0.0, 0.0, 4.0, 4.0);
        // Bloque inferior: desde el suelo del mundo de prueba hasta el borde de abajo del hueco.
        let lower_h = gap_bottom_m - WORLD_FLOOR_M;
        b.add_box(&wall_box(
            [2.0, WORLD_FLOOR_M + lower_h * 0.5, 2.0],
            [0.5, lower_h, 10.0],
        ));
        // Bloque superior: desde el borde de arriba del hueco hasta el techo del mundo de prueba.
        let upper_h = WORLD_CEILING_M - gap_top_m;
        b.add_box(&wall_box(
            [2.0, gap_top_m + upper_h * 0.5, 2.0],
            [0.5, upper_h, 10.0],
        ));
        b.finish()
    }

    #[test]
    fn una_rendija_deja_pasar_mucho_menos_que_un_vano_ancho() {
        const STEPS: usize = 200;
        const DT: f32 = 0.1;
        let source = Vec3::new(1.0, 1.0, 2.0);
        let target = Vec3::new(3.0, 1.0, 2.0);

        let narrow = two_rooms_with_gap(0.0, 0.08); // rendija de 8 cm
        let mut narrow_flow = SmilerFlow::new();
        assert!(narrow_flow.seed(&narrow, source, 1.0));
        for _ in 0..STEPS {
            narrow_flow.step(&narrow, DT, Some(target));
        }
        let narrow_arrived = narrow_flow.density_at(&narrow, target);

        let wide = two_rooms_with_gap(0.0, 2.0); // vano de 2 m
        let mut wide_flow = SmilerFlow::new();
        assert!(wide_flow.seed(&wide, source, 1.0));
        for _ in 0..STEPS {
            wide_flow.step(&wide, DT, Some(target));
        }
        let wide_arrived = wide_flow.density_at(&wide, target);

        assert!(
            wide_arrived > 4.0 * narrow_arrived,
            "el vano ancho ({wide_arrived}) debería dejar pasar mucho más que la rendija \
             ({narrow_arrived}) — §23 C4 del diseño pide más de 4x"
        );
    }

    /// Dos plantas separadas por una losa, agujereada en una sola columna (el «pozo»). Sembrar
    /// abajo y comprobar que la densidad llega arriba valida la conexión vertical del §9.3 del
    /// diseño SIN modelar peldaños reales — el mecanismo (solape entre columnas vecinas) es el
    /// mismo que haría falta para una escalera de verdad; una escalera con peldaños explícitos
    /// queda para cuando este prototipo deje de ser sandbox.
    fn raster_with_floor_hole() -> Wg3Raster {
        let mut b = Wg3RasterBuilder::covering(0.0, 0.0, 3.0, 3.0);
        b.add_box(&wall_box([1.5, 3.15, 1.5], [10.0, 0.3, 10.0])); // losa a y=[3.0, 3.3]
        b.carve_box(1.25, 1.25, 1.75, 1.75, 280, 340); // agujero de una celda
        b.finish()
    }

    #[test]
    fn sube_por_el_hueco_de_una_planta_a_otra() {
        let raster = raster_with_floor_hole();
        let mut flow = SmilerFlow::new();
        let lower = Vec3::new(0.5, 1.0, 0.5);
        let upper = Vec3::new(2.5, 5.0, 2.5);
        assert!(flow.seed(&raster, lower, 1.0));

        for _ in 0..300 {
            flow.step(&raster, 0.1, Some(upper));
        }

        assert!(
            flow.density_at(&raster, upper) > 0.0,
            "el humo no subió a la planta de arriba a través del hueco"
        );
        assert!(flow.no_band_overlaps_solid(&raster));
    }

    #[test]
    fn misma_entrada_mismo_resultado_bit_a_bit() {
        let raster = raster_with_partial_wall();
        let source = Vec3::new(1.0, 1.0, 2.0);
        let target = Vec3::new(9.0, 1.0, 2.0);

        let run = || {
            let mut flow = SmilerFlow::new();
            flow.seed(&raster, source, 1.0);
            for _ in 0..50 {
                flow.step(&raster, 0.1, Some(target));
            }
            flow.nodes(32)
        };

        assert_eq!(
            run(),
            run(),
            "regla dura 13: la misma simulación debe repetirse idéntica"
        );
    }

    #[test]
    fn una_banda_sin_datos_no_deja_sembrar_ni_fluir() {
        let raster = Wg3RasterBuilder::covering(0.0, 0.0, 2.0, 2.0).finish();
        let mut flow = SmilerFlow::new();
        // Bien fuera del área cubierta por el ráster (incluido su margen de una celda).
        assert!(!flow.seed(&raster, Vec3::new(500.0, 1.0, 500.0), 1.0));
        assert_eq!(flow.total_density(), 0.0);
    }

    /// Humo de verdad sobre una región REAL de WG3, generada con el manifiesto y la semilla que
    /// usa el resto de la suite de WG3 — no coordenadas inventadas a mano (ver
    /// `docs/FACELING-ROADMAP.md` §5: «ningún test de percepción debe autorar coordenadas a
    /// mano»). Sólo humo: que no reviente, que respete la geometría servida, y que se mueva.
    #[test]
    fn humo_sobre_una_region_real_generada() {
        use crate::world::wg3::tests::{real_manifest, SERVED_SEED};
        use crate::world::wg3::world::Wg3WorldCache;

        let manifest = real_manifest();
        let mut regions = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();

        let guess = Vec3::new(0.0, 2.0, 0.0);
        cache.prewarm_for_move(&mut regions, &manifest, SERVED_SEED, guess, guess);
        let origin = cache
            .standable_near(guess)
            .expect("debería haber un sitio de pie cerca del spawn en el mundo servido");

        let mut flow = SmilerFlow::new();
        assert!(
            flow.seed(&cache, origin, 1.0),
            "el sitio de pie que da standable_near debería tener banda hueca"
        );
        for _ in 0..30 {
            flow.step(&cache, 0.1, None);
        }

        assert!(
            flow.total_density() > 0.0,
            "toda la masa se perdió sobre geometría real"
        );
        assert!(flow.no_band_overlaps_solid(&cache));
    }
}
