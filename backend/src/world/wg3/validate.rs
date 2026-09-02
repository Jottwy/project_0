//! Auditoría WG3 (2026-09-02) — **la validación por niveles del mundo servido.**
//!
//! # Por qué existe
//!
//! Hasta hoy la corrección de WG3 la vigilaban ~120 tests, casi todos sobre UNA semilla
//! (`SERVED_SEED`) y cuatro regiones de referencia, más un barrido de 49 regiones en el plan. Eso
//! caza lo que esa semilla enseña y deja pasar lo que sólo sale con otra: el vano perdido de
//! `región (-1,0)` salió en una partida, no en un test. Este módulo hace que cualquier semilla y
//! cualquier región se puedan validar con la MISMA batería, y que un barrido de cientos sea una
//! llamada.
//!
//! # Los niveles, en el orden en que se generan
//!
//! ```text
//! semilla → puertas de junta → plan (por planta) → edificio → relleno → geometría → ráster → nav
//! ```
//!
//! Cada nivel tiene su lista de problemas y sus cifras. **Un problema es algo que no debería pasar
//! nunca**; una cifra es algo que se mira. La diferencia importa: un barrido no puede asertar sobre
//! cifras de gusto, y sí sobre invariantes.
//!
//! # Lo que NO hace
//!
//! No cambia nada del mundo: es una función pura de (manifiesto, semilla, región). No dibuja. No
//! interpreta el estilo. Para lo visual están los volcadores de `tests.rs`.

use std::collections::VecDeque;
use std::time::Instant;

use super::chunk::{self, Wg3ChunkCoord};
use super::collision::Wg3CollisionCache;
use super::fill;
use super::junction::{self, Wg3Gate};
use super::manifest::Wg3Manifest;
use super::nav;
use super::plan::{self, RegionBuilding, SpaceRole, MAX_WALK_STEP_CM};
use super::raster::{Wg3Raster, WG3_CELL_M};
use super::segment::{Wg3Segment, MIN_GENERATED_WIDTH_CM};
use super::world::{composer_seed, Wg3RegionCoord, Wg3ServedWorld, Wg3WorldCache, REGION_CHUNKS};
use crate::world::Vec3;

/// Hueco de cabeza mínimo para que una cota cuente como pisable, en metros.
///
/// **El del cuerpo del jugador, y no el metro de las sondas de `tests.rs`.** Con un metro, la cavidad
/// que queda bajo un tiro de escalera —entre el techo de la planta de abajo y la cara inferior de los
/// peldaños— contaba como suelo pisable en el que nadie cabe, y salía como una isla por pozo. No es
/// un sitio: es el hueco bajo la escalera, y con 1,8 m deja de contarse solo.
const HEAD_M: f32 = super::collision::PLAYER_BODY_M;
/// Hueco de cabeza por encima del cual una cota deja de contar (tejados y azoteas). Mismo que las
/// sondas desde ADR-102 D5.
const CEILING_CAP_M: f32 = 7.0;
/// Una mancha pisable por debajo de esto es ruido del ráster (una celda sobre una losa, un rellano
/// suelto); por encima es un sitio en el que alguien podría aparecer y no poder salir.
pub const ISLAND_MIN_CELLS: usize = 40;
/// Lo que se le exige a la mancha mayor: el resto son islas.
pub const MAIN_BLOB_MIN_FRACTION: f32 = 0.90;
/// Tramos que se pisan en XZ a menos de esta diferencia de cota son geometría cruzada.
const OVERLAP_SAME_FLOOR_CM: i32 = 250;

/// Lo que se decide validar y con qué coste.
#[derive(Debug, Clone, Copy)]
pub struct ValidateOptions {
    /// Plantas que se piden al edificio. Lo servido usa [`plan::REGION_STOREYS`].
    pub storeys: usize,
    /// Rasterizar y andar la región (lo caro: ~9 chunks a 0,5 m).
    pub walk: bool,
    /// Inundar el grafo de navegación de las criaturas sobre el chunk central.
    pub nav: bool,
    /// Planificar y rellenar dos veces y comparar.
    pub determinism: bool,
}

impl Default for ValidateOptions {
    fn default() -> Self {
        Self {
            storeys: plan::REGION_STOREYS,
            walk: true,
            nav: true,
            determinism: true,
        }
    }
}

/// Cifras del ráster andado.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct WalkStats {
    /// Cotas pisables en total (una celda con suelo en dos plantas cuenta dos).
    pub standable_levels: usize,
    /// Manchas conexas con el escalón del jugador.
    pub blobs: usize,
    /// Tamaño de la mancha mayor, en cotas pisables.
    pub main_blob: usize,
    /// Manchas que NO son la mayor y superan [`ISLAND_MIN_CELLS`]: sitios de los que no se sale.
    pub islands: usize,
    /// Cotas pisables de las islas, sumadas.
    pub island_levels: usize,
    /// Por planta: `(pisables, alcanzadas desde la mancha mayor)`.
    pub storey_reach: Vec<(usize, usize)>,
    /// Puertas de junta cuya celda interior es pisable y está en la mancha mayor.
    pub gates_total: usize,
    pub gates_reached: usize,
    /// Espacios construidos del plan cuyo interior tiene suelo en menos de la mitad de sus celdas.
    pub hollow_spaces: usize,
    /// Cobertura mínima de suelo de un espacio construido, en tanto por uno.
    pub min_space_coverage: f32,
}

/// Cifras del relleno, para leerlas sin abrir `FilledRegion`.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct FillStats {
    pub placements: usize,
    pub segments: usize,
    pub carves: usize,
    pub solids: usize,
    pub spaces_by_piece: u32,
    pub spaces_by_segment: u32,
    pub spaces_unbuilt: u32,
    pub openings_built: u32,
    pub openings_dropped: u32,
    pub links_to_route: usize,
    pub links_failed: usize,
    pub gates_built: u32,
    pub gates_failed: u32,
}

/// Cifras del plan, por edificio.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct PlanStats {
    pub storeys: usize,
    pub spaces: usize,
    pub built_spaces: usize,
    pub links: usize,
    pub wells: usize,
    /// Enlaces `Route` (los que sólo el enrutador puede tender).
    pub route_links: usize,
    /// Espacios construidos sin un solo enlace ni puerta.
    pub orphans: usize,
    /// Bandas de circulación con grado ≤ 1: corredores que no llevan a ninguna parte.
    pub dead_corridors: usize,
    /// Espacios de circulación (espina, corredores, cruces).
    pub circulation_spaces: usize,
    /// Espacios en zona `Weird` del campo de escala.
    pub weird_spaces: usize,
    /// Puertas (enlaces que no son `Route`) que NO caen en el centro de su pared.
    pub offset_doors: usize,
    /// Proporción construida de la planta baja, en tanto por uno.
    pub ground_built_ratio: f32,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct Timings {
    pub plan_ms: f32,
    pub fill_ms: f32,
    pub raster_ms: f32,
    pub walk_ms: f32,
}

/// El informe de UNA región de UNA semilla.
#[derive(Debug, Clone, Default)]
pub struct RegionReport {
    pub world_seed: u64,
    pub region: Wg3RegionCoord,
    /// Nivel 1-2: lo que el plan y el edificio dicen de sí mismos, más lo que este módulo añade.
    pub plan_problems: Vec<String>,
    /// Nivel 3: lo que el relleno no pudo cumplir.
    pub fill_problems: Vec<String>,
    /// Nivel 4: geometría emitida inválida o cruzada.
    pub geometry_problems: Vec<String>,
    /// Nivel 5: lo que el ráster andado contradice.
    pub walk_problems: Vec<String>,
    /// Nivel 6: lo que la navegación de las criaturas no alcanza.
    pub nav_problems: Vec<String>,
    /// Nivel 7: dos generaciones que no coinciden.
    pub determinism_problems: Vec<String>,
    pub plan: PlanStats,
    pub fill: FillStats,
    pub walk: WalkStats,
    /// Fracción de celdas pisables del chunk central alcanzadas por el grafo de nav.
    pub nav_reach: f32,
    pub timings: Timings,
}

impl RegionReport {
    /// Todos los problemas de todos los niveles, con su nivel delante.
    pub fn problems(&self) -> Vec<String> {
        let mut out = Vec::new();
        for (level, list) in [
            ("plan", &self.plan_problems),
            ("fill", &self.fill_problems),
            ("geometry", &self.geometry_problems),
            ("walk", &self.walk_problems),
            ("nav", &self.nav_problems),
            ("determinism", &self.determinism_problems),
        ] {
            for p in list {
                out.push(format!("[{level}] {p}"));
            }
        }
        out
    }

    pub fn is_valid(&self) -> bool {
        self.plan_problems.is_empty()
            && self.fill_problems.is_empty()
            && self.geometry_problems.is_empty()
            && self.walk_problems.is_empty()
            && self.nav_problems.is_empty()
            && self.determinism_problems.is_empty()
    }

    /// Una línea por región, para el barrido.
    pub fn summary(&self) -> String {
        format!(
            "seed {:#x} región ({},{}): {} plantas, {} espacios, {} tramos, {} piezas | mancha \
             mayor {:.1} % de {} cotas, {} islas ({} cotas) | puertas {}/{} | nav {:.0} % | \
             plan {:.0} ms, fill {:.0} ms, raster {:.0} ms | {}",
            self.world_seed,
            self.region.x,
            self.region.z,
            self.plan.storeys,
            self.plan.spaces,
            self.fill.segments,
            self.fill.placements,
            if self.walk.standable_levels > 0 {
                self.walk.main_blob as f32 * 100.0 / self.walk.standable_levels as f32
            } else {
                0.0
            },
            self.walk.standable_levels,
            self.walk.islands,
            self.walk.island_levels,
            self.walk.gates_reached,
            self.walk.gates_total,
            self.nav_reach * 100.0,
            self.timings.plan_ms,
            self.timings.fill_ms,
            self.timings.raster_ms,
            if self.is_valid() {
                "OK".to_string()
            } else {
                format!("{} problemas", self.problems().len())
            }
        )
    }
}

/// Las puertas de junta de una región, con la semilla del MUNDO (ADR-096).
pub fn gates_of(world_seed: u64, region: Wg3RegionCoord) -> Vec<Wg3Gate> {
    junction::gates_of_region(
        composer_seed(world_seed),
        region.x,
        region.z,
        region.bounds(),
    )
}

/// El edificio de una región, planificado como lo sirve el backend.
pub fn building_of(world_seed: u64, region: Wg3RegionCoord, storeys: usize) -> RegionBuilding {
    let gates = gates_of(world_seed, region);
    plan::plan_building(
        region.composer_seed(world_seed),
        region.bounds(),
        &gates,
        storeys,
    )
}

/// **VALIDA UNA REGIÓN.** Función pura del manifiesto, la semilla y la coordenada.
pub fn validate_region(
    manifest: &Wg3Manifest,
    world_seed: u64,
    region: Wg3RegionCoord,
    opts: &ValidateOptions,
) -> RegionReport {
    let mut report = RegionReport {
        world_seed,
        region,
        ..Default::default()
    };

    // ── nivel 1-2: plan y edificio ───────────────────────────────────────────────────────────
    let t0 = Instant::now();
    let building = building_of(world_seed, region, opts.storeys);
    report.timings.plan_ms = t0.elapsed().as_secs_f32() * 1000.0;
    report.plan_problems = building.problems();
    report.plan_problems.extend(extra_plan_problems(&building));
    report.plan = plan_stats(&building);

    // ── nivel 3: relleno ─────────────────────────────────────────────────────────────────────
    let t0 = Instant::now();
    let filled = fill::fill_building(&building, manifest);
    report.timings.fill_ms = t0.elapsed().as_secs_f32() * 1000.0;
    report.fill = FillStats {
        placements: filled.placements.len(),
        segments: filled.segments.len(),
        carves: filled.carves.len(),
        solids: filled.solids.len(),
        spaces_by_piece: filled.spaces_by_piece,
        spaces_by_segment: filled.spaces_by_segment,
        spaces_unbuilt: filled.spaces_unbuilt,
        openings_built: filled.openings_built,
        openings_dropped: filled.openings_dropped,
        links_to_route: filled.links_to_route.len(),
        links_failed: filled.links_failed.len(),
        gates_built: filled.gates_built,
        gates_failed: filled.gates_failed,
    };
    if filled.spaces_unbuilt > 0 {
        report
            .fill_problems
            .push(format!("{} espacios sin construir", filled.spaces_unbuilt));
    }
    if filled.openings_dropped > 0 {
        report.fill_problems.push(format!(
            "{} huecos perdidos: {:?}",
            filled.openings_dropped,
            &filled.openings_dropped_at[..filled.openings_dropped_at.len().min(4)]
        ));
    }
    if !filled.links_failed.is_empty() {
        report.fill_problems.push(format!(
            "{} enlaces del plan sin construir: {:?}",
            filled.links_failed.len(),
            &filled.links_failed[..filled.links_failed.len().min(4)]
        ));
    }
    if filled.gates_failed > 0 {
        report.fill_problems.push(format!(
            "{} puertas de junta sin abrir",
            filled.gates_failed
        ));
    }

    // ── nivel 4: geometría ───────────────────────────────────────────────────────────────────
    report.geometry_problems = geometry_problems(&filled.segments, &building);

    // ── nivel 5: ráster andado ───────────────────────────────────────────────────────────────
    if opts.walk {
        let served = Wg3ServedWorld::plan_region(manifest, world_seed, region);
        let t0 = Instant::now();
        let rasters = RegionRasters::build(manifest, &served, region);
        report.timings.raster_ms = t0.elapsed().as_secs_f32() * 1000.0;
        let t0 = Instant::now();
        let gates = gates_of(world_seed, region);
        let grid = WalkGrid::build(&rasters);
        let (stats, problems) = walk_region(&grid, &building, &gates);
        report.timings.walk_ms = t0.elapsed().as_secs_f32() * 1000.0;
        report.walk = stats;
        report.walk_problems = problems;
    }

    // ── nivel 6: navegación de criaturas ─────────────────────────────────────────────────────
    if opts.nav {
        let (reach, problems) = nav_reach(manifest, world_seed, region);
        report.nav_reach = reach;
        report.nav_problems = problems;
    }

    // ── nivel 7: determinismo ────────────────────────────────────────────────────────────────
    if opts.determinism {
        let again = building_of(world_seed, region, opts.storeys);
        if again != building {
            report
                .determinism_problems
                .push("el edificio cambia entre dos planificaciones".into());
        }
        let filled_again = fill::fill_building(&again, manifest);
        if filled_again.segments != filled.segments
            || filled_again.placements != filled.placements
            || filled_again.carves != filled.carves
            || filled_again.solids != filled.solids
        {
            report
                .determinism_problems
                .push("el relleno cambia entre dos llamadas".into());
        }
    }

    report
}

/// Lo que el plan no comprueba de sí mismo y este módulo sí.
/// ¿Llega un pozo de escalera a este espacio desde la planta de abajo? Una planta de torre puede
/// ser UN solo espacio sin enlaces ni puertas y estar perfectamente conectada: por la escalera.
fn well_lands_in(building: &RegionBuilding, storey: usize, rect: &plan::PlanRect) -> bool {
    building
        .wells
        .iter()
        .any(|w| w.storey_below + 1 == storey && w.rect.overlaps(rect))
}

fn extra_plan_problems(building: &RegionBuilding) -> Vec<String> {
    let mut out = Vec::new();
    for (n, storey) in building.storeys.iter().enumerate() {
        let degree = storey.degree();
        for (i, s) in storey.built() {
            let has_gate = storey.gates.iter().any(|g| g.space == i);
            if degree[i] == 0 && !has_gate && !well_lands_in(building, n, &s.rect) {
                out.push(format!(
                    "planta {n}: espacio {i} ({}) construido y sin ninguna conexión",
                    s.role.name()
                ));
            }
            // Un espacio construido más estrecho que un vano no puede tener puerta que el ráster
            // deje pasar. La escalera es la excepción medida (sus tiras se abren enteras).
            let narrow = s.rect.width_cm().min(s.rect.depth_cm());
            if narrow < MIN_GENERATED_WIDTH_CM && s.role != SpaceRole::Stair {
                out.push(format!(
                    "planta {n}: espacio {i} ({}) de {narrow} cm de ancho, por debajo del vano \
                     mínimo",
                    s.role.name()
                ));
            }
        }
        // El grafo tiene que ser UNO por planta: el plan ya lo intenta con `ensure_connected`,
        // pero un `Route` que el relleno no pueda tender lo deja en dos, y eso se ve aquí como
        // enlace fallido y allí como isla. Se cuenta como problema del PLAN cuando hay islas sin
        // ni siquiera un Route pedido.
        let comps = storey.components();
        if comps > 1 {
            let routes = storey
                .links
                .iter()
                .filter(|l| l.kind == plan::LinkKind::Route)
                .count();
            if routes == 0 {
                out.push(format!(
                    "planta {n}: {comps} componentes y ningún Route que las una"
                ));
            }
        }
    }
    out
}

fn plan_stats(building: &RegionBuilding) -> PlanStats {
    let mut st = PlanStats {
        storeys: building.storeys.len(),
        wells: building.wells.len(),
        ..Default::default()
    };
    for (n, storey) in building.storeys.iter().enumerate() {
        let degree = storey.degree();
        st.spaces += storey.spaces.len();
        st.built_spaces += storey.built().count();
        st.links += storey.links.len();
        st.route_links += storey
            .links
            .iter()
            .filter(|l| l.kind == plan::LinkKind::Route)
            .count();
        for (i, s) in storey.built() {
            let has_gate = storey.gates.iter().any(|g| g.space == i);
            if degree[i] == 0 && !has_gate && !well_lands_in(building, n, &s.rect) {
                st.orphans += 1;
            }
            if s.role.is_circulation() {
                st.circulation_spaces += 1;
                if degree[i] <= 1 && !has_gate {
                    st.dead_corridors += 1;
                }
            }
            if s.scale == super::scale::SCALE_WEIRD {
                st.weird_spaces += 1;
            }
        }
        for l in &storey.links {
            if l.kind == plan::LinkKind::Route {
                continue;
            }
            let (a, b) = (storey.spaces[l.a].rect, storey.spaces[l.b].rect);
            if let Some((_, x, z)) = plan::rects_share_wall(a, b) {
                if (x - l.at_x_cm).abs() > 2 || (z - l.at_z_cm).abs() > 2 {
                    st.offset_doors += 1;
                }
            }
        }
        if n == 0 {
            if let Some(b) = storey.bounds_cm {
                st.ground_built_ratio = storey.built_area_m2() / b.area_m2().max(1.0);
            }
        }
    }
    st
}

/// Nivel 4: cada tramo válido por sí mismo, y ninguno cruzado con otro a la misma cota.
fn geometry_problems(segments: &[Wg3Segment], building: &RegionBuilding) -> Vec<String> {
    let mut out = Vec::new();
    for (i, s) in segments.iter().enumerate() {
        for p in s.problems() {
            out.push(format!("tramo {i}: {p}"));
        }
    }

    // Solape XZ a la misma cota. Barrido por X para no pagar el cuadrado entero: con ~1.500
    // tramos por región el cuadrado son dos millones de comparaciones, y esto se llama en barridos.
    let mut order: Vec<usize> = (0..segments.len()).collect();
    order.sort_unstable_by_key(|&i| segments[i].x_cm);
    let mut crossed = 0usize;
    let mut first: Option<(usize, usize)> = None;
    for a in 0..order.len() {
        let sa = &segments[order[a]];
        let a_max_x = sa.x_cm + sa.size_x_cm;
        for b in (a + 1)..order.len() {
            let sb = &segments[order[b]];
            if sb.x_cm >= a_max_x {
                break;
            }
            if (sa.floor_y_cm - sb.floor_y_cm).abs() >= OVERLAP_SAME_FLOOR_CM {
                continue;
            }
            // Un pozo de escalera ATRAVIESA el forjado a propósito: sus tiras suben bajo el
            // espacio de arriba y las cotas se cruzan. Es la única geometría cruzada legítima.
            if (sa.style == 6 || sb.style == 6) && sa.floor_y_cm != sb.floor_y_cm {
                continue;
            }
            // Tocarse es correcto; pisarse un centímetro no.
            let crosses_x = sb.x_cm < a_max_x - 1;
            let crosses_z =
                sa.z_cm < sb.z_cm + sb.size_z_cm - 1 && sb.z_cm < sa.z_cm + sa.size_z_cm - 1;
            if crosses_x && crosses_z {
                crossed += 1;
                if first.is_none() {
                    first = Some((order[a], order[b]));
                }
            }
        }
    }
    if crossed > 0 {
        let (a, b) = first.unwrap_or((0, 0));
        out.push(format!(
            "{crossed} parejas de tramos se pisan a la misma cota; primera: {:?} y {:?}",
            (
                segments[a].x_cm,
                segments[a].z_cm,
                segments[a].size_x_cm,
                segments[a].size_z_cm
            ),
            (
                segments[b].x_cm,
                segments[b].z_cm,
                segments[b].size_x_cm,
                segments[b].size_z_cm
            )
        ));
    }

    // Todo tramo dentro de la caja de su región (con la holgura de una celda del ráster).
    if let Some(b) = building.storeys.first().and_then(|s| s.bounds_cm) {
        let slack = (WG3_CELL_M * 100.0) as i32;
        let outside = segments
            .iter()
            .filter(|s| {
                s.x_cm < b.min_x_cm - slack
                    || s.z_cm < b.min_z_cm - slack
                    || s.x_cm + s.size_x_cm > b.max_x_cm + slack
                    || s.z_cm + s.size_z_cm > b.max_z_cm + slack
            })
            .count();
        if outside > 0 {
            out.push(format!("{outside} tramos se salen de la región"));
        }
    }
    out
}

/// Los nueve rásteres de una región, resueltos como los resuelve el servidor.
pub struct RegionRasters {
    pub min_x: f32,
    pub min_z: f32,
    base: Wg3ChunkCoord,
    side: usize,
    rasters: Vec<Wg3Raster>,
}

impl RegionRasters {
    pub fn build(manifest: &Wg3Manifest, served: &Wg3ServedWorld, region: Wg3RegionCoord) -> Self {
        let (min_x, min_z, _, _) = region.bounds();
        let side = REGION_CHUNKS as usize;
        let base = Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster_full(
                    manifest,
                    &served.placements_touching_chunk(manifest, coord),
                    &served.segments_touching_chunk(coord),
                    &served.carves_touching_chunk(coord),
                    &served.solids_touching_chunk(coord),
                    coord,
                ));
            }
        }
        Self {
            min_x,
            min_z,
            base,
            side,
            rasters,
        }
    }

    pub fn cells(&self) -> usize {
        self.side * chunk::WG3_CHUNK_CELLS
    }

    /// Los tramos macizos de la columna del ráster bajo un punto, `(abajo, arriba)` en cm. Para
    /// las sondas: es lo que hay que mirar cuando una puerta no conecta lo que el plan dice.
    pub fn column(&self, x: f32, z: f32) -> Vec<(i16, i16)> {
        self.raster_at(x, z)
            .map(|r| {
                r.column_at(x, z)
                    .iter()
                    .map(|s| (s.bottom_cm, s.top_cm))
                    .collect()
            })
            .unwrap_or_default()
    }

    fn raster_at(&self, x: f32, z: f32) -> Option<&Wg3Raster> {
        let coord = Wg3ChunkCoord::containing(x, z);
        let (dx, dz) = (coord.x - self.base.x, coord.z - self.base.z);
        if dx < 0 || dz < 0 || dx as usize >= self.side || dz as usize >= self.side {
            return None;
        }
        self.rasters.get(dz as usize * self.side + dx as usize)
    }

    /// Las cotas pisables de una celda de la región, de abajo arriba.
    pub fn levels_at(&self, ix: usize, iz: usize) -> Vec<f32> {
        let x = self.min_x + ix as f32 * WG3_CELL_M + WG3_CELL_M * 0.5;
        let z = self.min_z + iz as f32 * WG3_CELL_M + WG3_CELL_M * 0.5;
        let Some(r) = self.raster_at(x, z) else {
            return Vec::new();
        };
        let column = r.column_at(x, z);
        let mut out = Vec::new();
        for (i, span) in column.iter().enumerate() {
            let head = match column.get(i + 1) {
                Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                None => f32::MAX,
            };
            if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                out.push(span.top_cm as f32 / 100.0);
            }
        }
        out
    }
}

/// El ráster de una región ya INUNDADO: cotas pisables por celda y a qué mancha pertenece cada
/// una. Es lo que comparten el informe y las sondas que van a mirar por qué algo no se alcanza.
pub struct WalkGrid {
    pub cells: usize,
    pub min_x: f32,
    pub min_z: f32,
    /// Cotas pisables de cada celda, de abajo arriba.
    pub floors: Vec<Vec<f32>>,
    /// Mancha de cada cota, paralelo a `floors`.
    pub blob_of: Vec<Vec<i32>>,
    pub sizes: Vec<usize>,
    /// La mancha mayor, o −1 si no hay ni una cota pisable.
    pub main: i32,
}

impl WalkGrid {
    pub fn build(rasters: &RegionRasters) -> Self {
        let cells = rasters.cells();
        let mut floors: Vec<Vec<f32>> = vec![Vec::new(); cells * cells];
        for iz in 0..cells {
            for ix in 0..cells {
                floors[iz * cells + ix] = rasters.levels_at(ix, iz);
            }
        }

        // Etiquetar manchas con el escalón del jugador.
        let mut blob_of: Vec<Vec<i32>> = floors.iter().map(|l| vec![-1; l.len()]).collect();
        let mut sizes: Vec<usize> = Vec::new();
        for c0 in 0..cells * cells {
            for l0 in 0..floors[c0].len() {
                if blob_of[c0][l0] >= 0 {
                    continue;
                }
                let id = sizes.len() as i32;
                sizes.push(0);
                blob_of[c0][l0] = id;
                let mut q = VecDeque::new();
                q.push_back((c0 % cells, c0 / cells, l0));
                while let Some((ix, iz, li)) = q.pop_front() {
                    sizes[id as usize] += 1;
                    let here = floors[iz * cells + ix][li];
                    for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                        let (nx, nz) = (ix as i32 + dx, iz as i32 + dz);
                        if nx < 0 || nz < 0 || nx as usize >= cells || nz as usize >= cells {
                            continue;
                        }
                        let (nx, nz) = (nx as usize, nz as usize);
                        for (nl, there) in floors[nz * cells + nx].iter().enumerate() {
                            // En centímetros ENTEROS, como `nav::find_path`: 9,97 − 9,70 da
                            // 0,2700005 en `f32` y eso ya es «más de 27».
                            let step_cm = ((there - here).abs() * 100.0).round() as i32;
                            if blob_of[nz * cells + nx][nl] >= 0 || step_cm > MAX_WALK_STEP_CM {
                                continue;
                            }
                            blob_of[nz * cells + nx][nl] = id;
                            q.push_back((nx, nz, nl));
                        }
                    }
                }
            }
        }
        let main = sizes
            .iter()
            .enumerate()
            .max_by_key(|(i, n)| (**n, std::cmp::Reverse(*i)))
            .map(|(i, _)| i as i32)
            .unwrap_or(-1);
        Self {
            cells,
            min_x: rasters.min_x,
            min_z: rasters.min_z,
            floors,
            blob_of,
            sizes,
            main,
        }
    }

    /// Índice de celda de un punto en metros, si cae en la región.
    pub fn cell(&self, x: f32, z: f32) -> Option<usize> {
        let ix = ((x - self.min_x) / WG3_CELL_M).floor() as i32;
        let iz = ((z - self.min_z) / WG3_CELL_M).floor() as i32;
        if ix < 0 || iz < 0 || ix as usize >= self.cells || iz as usize >= self.cells {
            return None;
        }
        Some(iz as usize * self.cells + ix as usize)
    }

    /// La mancha de la cota pisable más cercana a `y` en ese punto, si hay alguna a menos de
    /// medio metro.
    pub fn blob_at(&self, x: f32, z: f32, y: f32) -> Option<i32> {
        let c = self.cell(x, z)?;
        self.floors[c]
            .iter()
            .enumerate()
            .filter(|(_, f)| (*f - y).abs() <= 0.5)
            .min_by(|a, b| (a.1 - y).abs().total_cmp(&(b.1 - y).abs()))
            .map(|(li, _)| self.blob_of[c][li])
    }

    pub fn standable_levels(&self) -> usize {
        self.floors.iter().map(|l| l.len()).sum()
    }
}

/// Nivel 5: contar sobre el ráster inundado.
fn walk_region(
    grid: &WalkGrid,
    building: &RegionBuilding,
    gates: &[Wg3Gate],
) -> (WalkStats, Vec<String>) {
    let cells = grid.cells;
    let floors = &grid.floors;
    let blob_of = &grid.blob_of;
    let sizes = &grid.sizes;
    let standable_levels = grid.standable_levels();
    let rasters = grid;
    let mut stats = WalkStats {
        standable_levels,
        blobs: sizes.len(),
        ..Default::default()
    };
    let mut problems = Vec::new();
    if standable_levels == 0 {
        problems.push("ni una celda pisable en la región".into());
        return (stats, problems);
    }
    let main = grid.main;
    stats.main_blob = sizes[main as usize];
    for (i, n) in sizes.iter().enumerate() {
        if i as i32 != main && *n >= ISLAND_MIN_CELLS {
            stats.islands += 1;
            stats.island_levels += n;
        }
    }

    // Alcance por planta.
    let storeys = building.storeys.len().max(1);
    stats.storey_reach = vec![(0, 0); storeys];
    for c in 0..cells * cells {
        for (li, y) in floors[c].iter().enumerate() {
            let s = plan::storey_of_floor_cm((y * 100.0).round() as i32);
            if s < 0 || s as usize >= storeys {
                continue;
            }
            stats.storey_reach[s as usize].0 += 1;
            if blob_of[c][li] == main {
                stats.storey_reach[s as usize].1 += 1;
            }
        }
    }

    // Puertas de junta: la celda justo por dentro tiene que ser pisable y de la mancha mayor.
    stats.gates_total = gates.len();
    for g in gates {
        let (nx, nz) = super::placement::outward_normal(g.outward_side);
        let x = g.x - nx * 0.75;
        let z = g.z - nz * 0.75;
        let ix = ((x - rasters.min_x) / WG3_CELL_M).floor() as i32;
        let iz = ((z - rasters.min_z) / WG3_CELL_M).floor() as i32;
        if ix < 0 || iz < 0 || ix as usize >= cells || iz as usize >= cells {
            continue;
        }
        let c = iz as usize * cells + ix as usize;
        let ok = floors[c]
            .iter()
            .enumerate()
            .any(|(li, y)| y.abs() < 0.30 && blob_of[c][li] == main);
        if ok {
            stats.gates_reached += 1;
        } else {
            problems.push(format!(
                "puerta de junta en ({:.1},{:.1}) sin suelo alcanzable por dentro",
                g.x, g.z
            ));
        }
    }

    // Cobertura de suelo de cada espacio construido: un espacio del plan que no tiene suelo es
    // un agujero en el edificio que ningún contador del relleno ve.
    stats.min_space_coverage = 1.0;
    for (n, storey) in building.storeys.iter().enumerate() {
        // Un pozo de escalera perfora el suelo del espacio de arriba A PROPÓSITO: sus celdas no
        // cuentan como suelo que falte.
        let wells: Vec<plan::PlanRect> = building
            .wells
            .iter()
            .filter(|w| w.storey_below + 1 == n)
            .map(|w| w.rect)
            .collect();
        for (i, s) in storey.built() {
            let r = s.rect.shrunk(60);
            if r.width_cm() <= 0 || r.depth_cm() <= 0 {
                continue;
            }
            let lo_y = (s.floor_y_cm.min(s.floor_y_cm + s.rise_cm) - 30) as f32 / 100.0;
            let hi_y = (s.floor_y_cm.max(s.floor_y_cm + s.rise_cm) + 30) as f32 / 100.0;
            let ix0 = ((r.min_x_cm as f32 / 100.0 - rasters.min_x) / WG3_CELL_M).floor() as i32;
            let ix1 = ((r.max_x_cm as f32 / 100.0 - rasters.min_x) / WG3_CELL_M).ceil() as i32;
            let iz0 = ((r.min_z_cm as f32 / 100.0 - rasters.min_z) / WG3_CELL_M).floor() as i32;
            let iz1 = ((r.max_z_cm as f32 / 100.0 - rasters.min_z) / WG3_CELL_M).ceil() as i32;
            let (mut total, mut with_floor) = (0usize, 0usize);
            for iz in ix_range(iz0, iz1, cells) {
                for ix in ix_range(ix0, ix1, cells) {
                    let cx_cm = ((rasters.min_x + (ix as f32 + 0.5) * WG3_CELL_M) * 100.0) as i32;
                    let cz_cm = ((rasters.min_z + (iz as f32 + 0.5) * WG3_CELL_M) * 100.0) as i32;
                    if wells.iter().any(|w| w.contains_point(cx_cm, cz_cm)) {
                        continue;
                    }
                    total += 1;
                    if floors[iz * cells + ix]
                        .iter()
                        .any(|y| *y >= lo_y && *y <= hi_y)
                    {
                        with_floor += 1;
                    }
                }
            }
            if total == 0 {
                continue;
            }
            let cov = with_floor as f32 / total as f32;
            stats.min_space_coverage = stats.min_space_coverage.min(cov);
            if cov < 0.5 {
                stats.hollow_spaces += 1;
                if stats.hollow_spaces <= 3 {
                    problems.push(format!(
                        "espacio {i} ({}) a cota {} con suelo en el {:.0} % de sus celdas",
                        s.role.name(),
                        s.floor_y_cm,
                        cov * 100.0
                    ));
                }
            }
        }
    }

    let main_fraction = stats.main_blob as f32 / standable_levels as f32;
    if main_fraction < MAIN_BLOB_MIN_FRACTION {
        problems.push(format!(
            "la mancha mayor sólo cubre el {:.1} % de lo pisable ({} manchas, {} islas de ≥ {} \
             celdas)",
            main_fraction * 100.0,
            sizes.len(),
            stats.islands,
            ISLAND_MIN_CELLS
        ));
    }
    for (n, (total, reached)) in stats.storey_reach.iter().enumerate() {
        if n == 0 || *total == 0 {
            continue;
        }
        // Una planta servida a la que no se llega es decorado (ADR-102). Se exige la mitad porque
        // los peldaños intermedios cuentan en la planta de abajo y las torres se estrechan.
        if (*reached as f32) < 0.5 * *total as f32 {
            problems.push(format!(
                "planta {n}: sólo {reached} de {total} cotas pisables se alcanzan desde la mancha \
                 mayor"
            ));
        }
    }
    (stats, problems)
}

fn ix_range(lo: i32, hi: i32, cells: usize) -> std::ops::Range<usize> {
    let lo = lo.max(0) as usize;
    let hi = (hi.max(0) as usize).min(cells);
    lo..hi.max(lo)
}

/// Nivel 6: el grafo de navegación de las criaturas sobre el chunk central de la región.
///
/// Misma regla de vecindad que `nav::find_path`, sobre el mismo caché que usa el bucle de juego.
fn nav_reach(
    manifest: &Wg3Manifest,
    world_seed: u64,
    region: Wg3RegionCoord,
) -> (f32, Vec<String>) {
    use std::collections::HashSet;
    const PLAYER_BASE_Y: f32 = 1.8;

    let (min_x, min_z, _, _) = region.bounds();
    let mut worlds = Wg3WorldCache::default();
    let mut cache = Wg3CollisionCache::new();
    let centre = Vec3::new(min_x + 75.0, PLAYER_BASE_Y, min_z + 75.0);
    cache.prewarm_for_move(&mut worlds, manifest, world_seed, centre, centre);

    let mut walkable: HashSet<(i32, i32)> = HashSet::new();
    let mut seeds: Vec<(i32, i32, i32)> = Vec::new();
    let cells = (50.0 / WG3_CELL_M) as i32;
    for iz in 0..cells {
        for ix in 0..cells {
            let x = min_x + 50.0 + ix as f32 * WG3_CELL_M + WG3_CELL_M * 0.5;
            let z = min_z + 50.0 + iz as f32 * WG3_CELL_M + WG3_CELL_M * 0.5;
            let Some(floor) = cache.floor_below_m(x, z, 0.0) else {
                continue;
            };
            if nav::floor_at(&cache, x, z, floor).is_none() {
                continue;
            }
            let c = nav::cell_of(x, z);
            walkable.insert(c);
            seeds.push((c.0, c.1, (floor * 100.0).round() as i32));
        }
    }
    if walkable.is_empty() {
        return (
            0.0,
            vec!["el chunk central no tiene ni una celda navegable".into()],
        );
    }

    // Inundar desde la celda pisable más cercana al centro del chunk, que es donde caería el
    // reparto. Se coge la más cercana y no la primera: la primera es una esquina.
    let (cx0, cz0) = nav::cell_of(min_x + 75.0, min_z + 75.0);
    let start = seeds
        .iter()
        .min_by_key(|(x, z, _)| (x - cx0).abs() + (z - cz0).abs())
        .copied()
        .expect("hay al menos una");
    let mut seen: HashSet<(i32, i32, i32)> = HashSet::new();
    let mut stack = vec![start];
    seen.insert(start);
    let mut reached: HashSet<(i32, i32)> = HashSet::new();
    while let Some((cx, cz, cf)) = stack.pop() {
        reached.insert((cx, cz));
        let floor = cf as f32 / 100.0;
        for (dx, dz) in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
            let nc = (cx + dx, cz + dz);
            let (nx, nz) = nav::cell_centre(nc.0, nc.1);
            for nf in nav::floors_at(&cache, nx, nz, floor) {
                if ((nf - floor).abs() * 100.0) as i32 > MAX_WALK_STEP_CM {
                    continue;
                }
                let k = (nc.0, nc.1, (nf * 100.0).round() as i32);
                if seen.insert(k) {
                    stack.push(k);
                }
            }
        }
    }
    let inside = reached.iter().filter(|c| walkable.contains(c)).count();
    let frac = inside as f32 / walkable.len() as f32;
    let mut problems = Vec::new();
    // El grafo de nav puede salir del chunk central (la ventana es la región precalentada), así
    // que se mide sobre las celdas del chunk. Por debajo de la mitad, las criaturas que nazcan en
    // la otra mitad no llegan al jugador.
    if frac < 0.5 {
        problems.push(format!(
            "la nav sólo alcanza el {:.0} % de las celdas navegables del chunk central",
            frac * 100.0
        ));
    }
    (frac, problems)
}

/// Resumen de un barrido: cuántas regiones válidas y qué falló, agrupado por nivel.
#[derive(Debug, Clone, Default)]
pub struct SweepReport {
    pub reports: Vec<RegionReport>,
}

impl SweepReport {
    pub fn valid(&self) -> usize {
        self.reports.iter().filter(|r| r.is_valid()).count()
    }
    pub fn total(&self) -> usize {
        self.reports.len()
    }
    /// Problemas agrupados por nivel y por texto (sin los números concretos), con su cuenta.
    pub fn histogram(&self) -> Vec<(String, usize)> {
        use std::collections::BTreeMap;
        let mut h: BTreeMap<String, usize> = BTreeMap::new();
        for r in &self.reports {
            for p in r.problems() {
                let key: String = p
                    .chars()
                    .map(|c| if c.is_ascii_digit() { '#' } else { c })
                    .collect();
                // Sin los números y sin lo que va tras «:» para agrupar de verdad.
                let key = key.split(':').next().unwrap_or(&key).trim().to_string();
                *h.entry(key).or_default() += 1;
            }
        }
        let mut out: Vec<(String, usize)> = h.into_iter().collect();
        out.sort_by(|a, b| b.1.cmp(&a.1).then(a.0.cmp(&b.0)));
        out
    }
    /// Medias del barrido: lo que hay que mirar antes y después de tocar una perilla.
    pub fn averages(&self) -> String {
        let n = self.reports.len().max(1) as f32;
        let sum = |f: &dyn Fn(&RegionReport) -> f32| self.reports.iter().map(f).sum::<f32>() / n;
        format!(
            "medias sobre {} regiones: {:.1} plantas, {:.0} espacios ({:.0} circulación, {:.0} \
             ciegos, {:.0} weird), {:.0} enlaces ({:.0} puertas descentradas), {:.1} pozos, {:.1} \
             piezas, {:.0} tramos | suelo baja {:.0} % | pisable {:.0} cotas, mancha mayor {:.1} %, \
             {:.1} islas | nav {:.0} % | plan {:.1} ms, fill {:.1} ms, raster {:.0} ms",
            self.reports.len(),
            sum(&|r| r.plan.storeys as f32),
            sum(&|r| r.plan.spaces as f32),
            sum(&|r| r.plan.circulation_spaces as f32),
            sum(&|r| r.plan.dead_corridors as f32),
            sum(&|r| r.plan.weird_spaces as f32),
            sum(&|r| r.plan.links as f32),
            sum(&|r| r.plan.offset_doors as f32),
            sum(&|r| r.plan.wells as f32),
            sum(&|r| r.fill.placements as f32),
            sum(&|r| r.fill.segments as f32),
            sum(&|r| r.plan.ground_built_ratio * 100.0),
            sum(&|r| r.walk.standable_levels as f32),
            sum(&|r| {
                if r.walk.standable_levels > 0 {
                    r.walk.main_blob as f32 * 100.0 / r.walk.standable_levels as f32
                } else {
                    0.0
                }
            }),
            sum(&|r| r.walk.islands as f32),
            sum(&|r| r.nav_reach * 100.0),
            sum(&|r| r.timings.plan_ms),
            sum(&|r| r.timings.fill_ms),
            sum(&|r| r.timings.raster_ms),
        )
    }

    pub fn print(&self) {
        for r in &self.reports {
            println!("[wg3-validate] {}", r.summary());
            for p in r.problems() {
                println!("[wg3-validate]     {p}");
            }
        }
        println!("[wg3-validate] {}", self.averages());
        println!(
            "[wg3-validate] {} de {} regiones válidas ({:.1} %)",
            self.valid(),
            self.total(),
            self.valid() as f32 * 100.0 / self.total().max(1) as f32
        );
        for (k, n) in self.histogram() {
            println!("[wg3-validate]   {n:4} × {k}");
        }
    }
}

/// **EL BARRIDO.** Muchas semillas por muchas regiones, cada una validada entera.
pub fn validate_sweep(
    manifest: &Wg3Manifest,
    seeds: &[u64],
    regions: &[(i32, i32)],
    opts: &ValidateOptions,
) -> SweepReport {
    let mut out = SweepReport::default();
    for &seed in seeds {
        for &(x, z) in regions {
            out.reports.push(validate_region(
                manifest,
                seed,
                Wg3RegionCoord { x, z },
                opts,
            ));
        }
    }
    out
}

/// Semillas de barrido reproducibles: `n` semillas distintas a partir de una base, sin RNG.
pub fn sweep_seeds(n: usize) -> Vec<u64> {
    (0..n as u64)
        .map(|i| super::hash::mix(0x5EED, i as i32, (i * 7) as i32, 0x5A11))
        .collect()
}

/// Todo lo que una sonda necesita para mirar UNA región por dentro: el edificio, lo rellenado, el
/// ráster inundado y las puertas. Lo que `validate_region` resume, aquí sin resumir.
pub struct RegionInside {
    pub building: RegionBuilding,
    pub filled: fill::FilledRegion,
    pub grid: WalkGrid,
    pub gates: Vec<Wg3Gate>,
    pub rasters: RegionRasters,
}

pub fn region_inside(
    manifest: &Wg3Manifest,
    world_seed: u64,
    region: Wg3RegionCoord,
) -> RegionInside {
    let building = building_of(world_seed, region, plan::REGION_STOREYS);
    let filled = fill::fill_building(&building, manifest);
    let served = Wg3ServedWorld::plan_region(manifest, world_seed, region);
    let rasters = RegionRasters::build(manifest, &served, region);
    RegionInside {
        building,
        filled,
        grid: WalkGrid::build(&rasters),
        gates: gates_of(world_seed, region),
        rasters,
    }
}
