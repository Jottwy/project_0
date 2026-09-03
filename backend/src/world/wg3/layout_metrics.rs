//! **Métricas de layout de WG3** — space syntax sobre el chunk servido.
//!
//! # Qué mide y por qué existe
//!
//! `validate.rs` dice si una región es CORRECTA (se anda, no hay islas, las puertas conectan). No
//! dice cómo se LEE: si un sitio es un pasillo o una nave, si desde un punto se ve todo o casi
//! nada, si la planta es monótona. Esto es lo segundo, y sólo lo segundo: seis métricas clásicas de
//! isovista/VGA sobre una rejilla de muestreo de 1 m, más la entropía del reparto de tiles.
//!
//! - **área de isovista**: cuántos m² se ven desde el punto.
//! - **occlusivity**: qué parte del perímetro de esa isovista es arista ABIERTA (borde que no es
//!   muro: hay más sitio detrás, sólo que no se ve). 0 = sala convexa, se ve entera.
//! - **jaggedness**: perímetro² / área. Cuanto más alto, más recortada la silueta de lo visible.
//! - **clustering coefficient** del grafo de visibilidad (VGA): de todo lo que veo, qué parte se ve
//!   entre sí. Alto = estoy en un sitio; bajo = estoy en un cruce entre sitios.
//! - **drift**: distancia del punto al centroide de su isovista. Cuánto tira de ti lo que ves.
//! - **entropía de Shannon** del reparto de tiles de la planta, normalizada por `log2(n)` con `n`
//!   el tamaño del vocabulario ([`TILE_CLASSES`]), una cifra POR CHUNK, no por punto.
//!
//! # Lo que NO hace
//!
//! No toca el generador: es una función pura de (manifiesto, semilla). No barre parámetros, no
//! propone valores y no interpreta si un número es bueno. Mide y escribe CSV.
//!
//! # Cómo se corre
//!
//! ```text
//! cargo test --release layout_metrics_batch -- --ignored --nocapture
//! ```
//!
//! Variables: `WG3_METRICS_SEEDS` (por defecto 200, semillas 1..=N), `WG3_METRICS_OUT` (por defecto
//! `backend/viz_out/layout_metrics`).

use std::f32::consts::LN_2;

use super::chunk::{self, Wg3ChunkCoord, WG3_CHUNK_M};
use super::collision::PLAYER_BODY_M;
use super::manifest::Wg3Manifest;
use super::plan::{self, SpaceRole};
use super::raster::Wg3Raster;
use super::world::{Wg3RegionCoord, Wg3ServedWorld};

/// Lado de la celda de muestreo, en metros. **No es la celda del ráster** (0,5 m): el muestreo es
/// más grueso a propósito, porque el grafo de visibilidad es cuadrático en el número de puntos.
pub const SAMPLE_M: f32 = 1.0;
/// Puntos por lado de chunk. 50 m / 1 m = 50, o sea 2500 muestras por chunk.
pub const SAMPLES_PER_SIDE: usize = (WG3_CHUNK_M / SAMPLE_M) as usize;
/// Paso de la traza de visibilidad, en metros. Más fino que la celda para no colarse por esquinas.
const RAY_STEP_M: f32 = 0.25;
/// Hueco de cabeza mínimo para que una cota cuente como pisable (el del cuerpo, igual que
/// `validate`).
const HEAD_M: f32 = PLAYER_BODY_M;
/// Por encima de esto la cota es tejado, no planta.
const CEILING_CAP_M: f32 = 7.0;
/// Margen vertical alrededor de la cota de planta baja para decidir que una celda es de ESTA
/// planta. Las plantas van a varios metros; 1,5 m separa sin partir un rellano.
const GROUND_BAND_M: f32 = 1.5;
/// Vocabulario de tiles: los diez papeles del plan más muro y exterior. Es el `n` de `log2(n)`.
pub const TILE_CLASSES: usize = 12;
/// Bins de los histogramas.
const HIST_BINS: usize = 20;

/// A qué clase pertenece una celda de muestreo.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Tile {
    /// No se pisa a la cota de la planta baja.
    Wall,
    /// Se pisa, pero ningún espacio del plan la cubre (banda de junta, hueco entre huellas).
    Outside,
    Role(SpaceRole),
}

impl Tile {
    /// Índice estable dentro del vocabulario, para el histograma de la entropía.
    fn index(self) -> usize {
        match self {
            Tile::Wall => 0,
            Tile::Outside => 1,
            Tile::Role(r) => {
                2 + match r {
                    SpaceRole::Spine => 0,
                    SpaceRole::Corridor => 1,
                    SpaceRole::Stair => 2,
                    SpaceRole::Junction => 3,
                    SpaceRole::Hall => 4,
                    SpaceRole::Office => 5,
                    SpaceRole::Service => 6,
                    SpaceRole::Storage => 7,
                    SpaceRole::DeadEnd => 8,
                    SpaceRole::Void => 9,
                }
            }
        }
    }
}

/// La rejilla de muestreo de UN chunk: qué se pisa y qué tile es cada celda.
pub struct SampleGrid {
    pub side: usize,
    pub min_x: f32,
    pub min_z: f32,
    /// Paralelo a la rejilla: la celda se pisa a la cota de la planta baja.
    pub open: Vec<bool>,
    pub tiles: Vec<Tile>,
    /// Cota de la planta baja del chunk, en metros.
    pub ground_y: f32,
}

impl SampleGrid {
    #[inline]
    fn idx(&self, ix: usize, iz: usize) -> usize {
        iz * self.side + ix
    }

    /// Centro de la celda en coordenadas de mundo.
    #[inline]
    pub fn centre(&self, ix: usize, iz: usize) -> (f32, f32) {
        (
            self.min_x + (ix as f32 + 0.5) * SAMPLE_M,
            self.min_z + (iz as f32 + 0.5) * SAMPLE_M,
        )
    }

    #[inline]
    fn open_at(&self, ix: i32, iz: i32) -> bool {
        if ix < 0 || iz < 0 || ix as usize >= self.side || iz as usize >= self.side {
            return false;
        }
        self.open[iz as usize * self.side + ix as usize]
    }

    /// Reparto de tiles como entropía de Shannon normalizada por `log2(TILE_CLASSES)`.
    ///
    /// 0 = la planta entera es una sola clase; 1 = las doce clases al mismo peso. Normalizar por el
    /// vocabulario COMPLETO y no por las clases presentes es deliberado: si un chunk sólo tiene
    /// muro y oficina, eso es menos variedad que otro con seis papeles, y la cifra tiene que
    /// decirlo.
    pub fn tile_entropy(&self) -> f32 {
        let mut counts = [0usize; TILE_CLASSES];
        for t in &self.tiles {
            counts[t.index()] += 1;
        }
        let total = self.tiles.len() as f32;
        if total <= 0.0 {
            return 0.0;
        }
        let mut h = 0.0f32;
        for c in counts {
            if c == 0 {
                continue;
            }
            let p = c as f32 / total;
            h -= p * (p.ln() / LN_2);
        }
        h / (TILE_CLASSES as f32).log2()
    }
}

/// Las cotas pisables de una columna del ráster, de abajo arriba. Mismo criterio que
/// `validate::RegionRasters::levels_at`, replicado aquí porque aquello trabaja sobre los nueve
/// rásteres de una región y esto sobre uno solo.
fn standable_levels(raster: &Wg3Raster, x: f32, z: f32) -> Vec<f32> {
    let column = raster.column_at(x, z);
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

/// Muestrea el chunk `coord` del mundo servido con la semilla dada.
///
/// El ráster es el del servidor (`build_chunk_raster_full` sobre lo que el plan coloca), y los
/// papeles salen de la planta baja del edificio de la región. No se genera nada nuevo aquí.
pub fn sample_chunk(manifest: &Wg3Manifest, world_seed: u64, coord: Wg3ChunkCoord) -> SampleGrid {
    let region = Wg3RegionCoord::of_chunk(coord);
    let served = Wg3ServedWorld::plan_region(manifest, world_seed, region);
    let raster = chunk::build_chunk_raster_full(
        manifest,
        &served.placements_touching_chunk(manifest, coord),
        &served.segments_touching_chunk(coord),
        &served.carves_touching_chunk(coord),
        &served.solids_touching_chunk(coord),
        coord,
    );
    let building = super::validate::building_of(world_seed, region, plan::REGION_STOREYS);
    let ground_plan = building.storeys.first();

    let side = SAMPLES_PER_SIDE;
    let (min_x, min_z, _, _) = coord.bounds();
    let mut grid = SampleGrid {
        side,
        min_x,
        min_z,
        open: vec![false; side * side],
        tiles: vec![Tile::Wall; side * side],
        ground_y: 0.0,
    };

    // Primera pasada: todas las cotas pisables. La planta baja es la MENOR de las que aparecen en
    // el chunk, no el cero absoluto: un chunk puede caer entero sobre una planta alta.
    let mut levels: Vec<Vec<f32>> = Vec::with_capacity(side * side);
    let mut ground = f32::MAX;
    for iz in 0..side {
        for ix in 0..side {
            let (x, z) = grid.centre(ix, iz);
            let l = standable_levels(&raster, x, z);
            if let Some(&lo) = l.first() {
                if lo < ground {
                    ground = lo;
                }
            }
            levels.push(l);
        }
    }
    grid.ground_y = if ground == f32::MAX { 0.0 } else { ground };

    for iz in 0..side {
        for ix in 0..side {
            let i = grid.idx(ix, iz);
            let open = levels[i]
                .iter()
                .any(|&y| (y - grid.ground_y).abs() <= GROUND_BAND_M);
            grid.open[i] = open;
            if !open {
                grid.tiles[i] = Tile::Wall;
                continue;
            }
            let (x, z) = grid.centre(ix, iz);
            let (xc, zc) = ((x * 100.0) as i32, (z * 100.0) as i32);
            grid.tiles[i] = ground_role(ground_plan, xc, zc)
                .map(Tile::Role)
                .unwrap_or(Tile::Outside);
        }
    }
    grid
}

fn ground_role(plan: Option<&plan::RegionPlan>, x_cm: i32, z_cm: i32) -> Option<SpaceRole> {
    plan?
        .spaces
        .iter()
        .find(|s| s.contains_point(x_cm, z_cm))
        .map(|s| s.role)
}

/// Grafo de visibilidad de la rejilla: por cada punto abierto, el conjunto de puntos abiertos que
/// ve, como bitset.
pub struct VisibilityGraph {
    /// Índice de rejilla de cada nodo.
    pub nodes: Vec<usize>,
    /// Nodo de cada celda de rejilla, o `usize::MAX` si la celda no es nodo.
    node_of: Vec<usize>,
    words: usize,
    bits: Vec<u64>,
}

impl VisibilityGraph {
    #[inline]
    fn set(&mut self, a: usize, b: usize) {
        self.bits[a * self.words + b / 64] |= 1u64 << (b % 64);
    }

    #[inline]
    pub fn row(&self, a: usize) -> &[u64] {
        &self.bits[a * self.words..(a + 1) * self.words]
    }

    /// Cuántos nodos ve `a`, sin contarse a sí mismo.
    pub fn degree(&self, a: usize) -> usize {
        self.row(a).iter().map(|w| w.count_ones() as usize).sum()
    }

    pub fn sees(&self, a: usize, b: usize) -> bool {
        self.bits[a * self.words + b / 64] & (1u64 << (b % 64)) != 0
    }

    /// Construye el grafo. Las trazas se hacen una vez por par y el bit se pone a los dos lados:
    /// la visibilidad es simétrica y trazarla dos veces sería trabajo tirado.
    pub fn build(grid: &SampleGrid) -> Self {
        let mut nodes = Vec::new();
        let mut node_of = vec![usize::MAX; grid.open.len()];
        for (i, &open) in grid.open.iter().enumerate() {
            if open {
                node_of[i] = nodes.len();
                nodes.push(i);
            }
        }
        let n = nodes.len();
        let words = n.div_ceil(64).max(1);
        let mut g = Self {
            nodes,
            node_of,
            words,
            bits: vec![0u64; n * words],
        };
        for a in 0..n {
            for b in (a + 1)..n {
                let (ax, az) = cell_centre(grid, g.nodes[a]);
                let (bx, bz) = cell_centre(grid, g.nodes[b]);
                if line_of_sight(grid, ax, az, bx, bz) {
                    g.set(a, b);
                    g.set(b, a);
                }
            }
        }
        g
    }

    fn node_at(&self, cell: usize) -> Option<usize> {
        match self.node_of.get(cell) {
            Some(&n) if n != usize::MAX => Some(n),
            _ => None,
        }
    }
}

#[inline]
fn cell_centre(grid: &SampleGrid, cell: usize) -> (f32, f32) {
    let ix = cell % grid.side;
    let iz = cell / grid.side;
    grid.centre(ix, iz)
}

/// Traza recta entre dos centros de celda. Simétrica por construcción: el número de pasos depende
/// sólo de la distancia y las muestras son `k/steps`, que al invertir el segmento dan el mismo
/// conjunto de puntos.
fn line_of_sight(grid: &SampleGrid, ax: f32, az: f32, bx: f32, bz: f32) -> bool {
    let (dx, dz) = (bx - ax, bz - az);
    let dist = (dx * dx + dz * dz).sqrt();
    let steps = (dist / RAY_STEP_M).ceil() as usize;
    for k in 1..steps {
        let t = k as f32 / steps as f32;
        let x = ax + dx * t;
        let z = az + dz * t;
        let ix = ((x - grid.min_x) / SAMPLE_M).floor() as i32;
        let iz = ((z - grid.min_z) / SAMPLE_M).floor() as i32;
        if !grid.open_at(ix, iz) {
            return false;
        }
    }
    true
}

/// Las cifras de UN punto de muestreo.
#[derive(Debug, Clone, Copy, Default)]
pub struct PointMetrics {
    pub ix: usize,
    pub iz: usize,
    pub x: f32,
    pub z: f32,
    /// m² vistos, el propio punto incluido.
    pub isovist_area_m2: f32,
    /// Perímetro de la isovista, en metros.
    pub perimeter_m: f32,
    /// Parte de ese perímetro que NO es muro: hay sitio detrás y no se ve.
    pub occlusivity: f32,
    pub jaggedness: f32,
    pub clustering: f32,
    pub drift_m: f32,
}

/// Todas las cifras de un chunk.
pub struct ChunkMetrics {
    pub seed: u64,
    pub chunk: Wg3ChunkCoord,
    pub open_cells: usize,
    pub total_cells: usize,
    pub tile_entropy: f32,
    pub points: Vec<PointMetrics>,
}

impl ChunkMetrics {
    /// Media y varianza (poblacional) de una métrica sobre los puntos del chunk.
    pub fn moments(&self, f: impl Fn(&PointMetrics) -> f32) -> (f32, f32) {
        let values: Vec<f32> = self.points.iter().map(f).collect();
        moments(&values)
    }
}

fn moments(values: &[f32]) -> (f32, f32) {
    if values.is_empty() {
        return (0.0, 0.0);
    }
    let n = values.len() as f32;
    let mean = values.iter().sum::<f32>() / n;
    let var = values.iter().map(|v| (v - mean) * (v - mean)).sum::<f32>() / n;
    (mean, var)
}

/// Mide un chunk entero: muestreo, grafo de visibilidad y las seis métricas por punto.
pub fn measure_chunk(
    manifest: &Wg3Manifest,
    world_seed: u64,
    coord: Wg3ChunkCoord,
) -> ChunkMetrics {
    let grid = sample_chunk(manifest, world_seed, coord);
    let graph = VisibilityGraph::build(&grid);
    let n = graph.nodes.len();
    let mut points = Vec::with_capacity(n);

    for a in 0..n {
        let cell = graph.nodes[a];
        let ix = cell % grid.side;
        let iz = cell / grid.side;
        let (x, z) = grid.centre(ix, iz);

        // El punto se ve a sí mismo: la isovista lo incluye.
        let area_cells = graph.degree(a) + 1;
        let area = area_cells as f32 * SAMPLE_M * SAMPLE_M;

        // Perímetro: aristas entre una celda visible y una que no lo es. Si la de fuera es muro, la
        // arista es maciza; si es suelo que no se ve, es arista OCLUSIVA (hay más sitio detrás).
        let mut solid_edges = 0usize;
        let mut open_edges = 0usize;
        let mut cx = 0.0f32;
        let mut cz = 0.0f32;
        let visit =
            |vcell: usize, cx: &mut f32, cz: &mut f32, solid: &mut usize, open: &mut usize| {
                let vix = (vcell % grid.side) as i32;
                let viz = (vcell / grid.side) as i32;
                let (vx, vz) = grid.centre(vix as usize, viz as usize);
                *cx += vx;
                *cz += vz;
                for (nx, nz) in [
                    (vix + 1, viz),
                    (vix - 1, viz),
                    (vix, viz + 1),
                    (vix, viz - 1),
                ] {
                    let neighbour_visible =
                        if nx < 0 || nz < 0 || nx as usize >= grid.side || nz as usize >= grid.side
                        {
                            false
                        } else {
                            let ncell = nz as usize * grid.side + nx as usize;
                            match graph.node_at(ncell) {
                                Some(nb) => nb == a || graph.sees(a, nb),
                                None => false,
                            }
                        };
                    if neighbour_visible {
                        continue;
                    }
                    if grid.open_at(nx, nz) {
                        *open += 1;
                    } else {
                        *solid += 1;
                    }
                }
            };
        visit(cell, &mut cx, &mut cz, &mut solid_edges, &mut open_edges);
        for b in 0..n {
            if graph.sees(a, b) {
                visit(
                    graph.nodes[b],
                    &mut cx,
                    &mut cz,
                    &mut solid_edges,
                    &mut open_edges,
                );
            }
        }
        let perimeter = (solid_edges + open_edges) as f32 * SAMPLE_M;
        let occlusivity = if solid_edges + open_edges == 0 {
            0.0
        } else {
            open_edges as f32 / (solid_edges + open_edges) as f32
        };
        let jaggedness = if area > 0.0 {
            perimeter * perimeter / area
        } else {
            0.0
        };
        cx /= area_cells as f32;
        cz /= area_cells as f32;
        let drift = ((cx - x) * (cx - x) + (cz - z) * (cz - z)).sqrt();

        points.push(PointMetrics {
            ix,
            iz,
            x,
            z,
            isovist_area_m2: area,
            perimeter_m: perimeter,
            occlusivity,
            jaggedness,
            clustering: clustering_of(&graph, a),
            drift_m: drift,
        });
    }

    ChunkMetrics {
        seed: world_seed,
        chunk: coord,
        open_cells: n,
        total_cells: grid.open.len(),
        tile_entropy: grid.tile_entropy(),
        points,
    }
}

/// Coeficiente de clustering de VGA: de los pares de vecinos visibles de `a`, cuántos se ven entre
/// sí. Se cuenta con AND de bitsets, que es lo que hace viable el grafo completo.
fn clustering_of(graph: &VisibilityGraph, a: usize) -> f32 {
    let row = graph.row(a);
    let k = graph.degree(a);
    if k < 2 {
        return 0.0;
    }
    let mut links = 0usize;
    for b in 0..graph.nodes.len() {
        if !graph.sees(a, b) {
            continue;
        }
        let other = graph.row(b);
        links += row
            .iter()
            .zip(other)
            .map(|(x, y)| (x & y).count_ones() as usize)
            .sum::<usize>();
    }
    // `links` cuenta cada arista dos veces (una por extremo) y el bucle ya excluye a `a` porque
    // `sees(a, a)` es falso.
    links as f32 / (k * (k - 1)) as f32
}

// ─────────────────────────── salida ───────────────────────────

/// Cabecera del CSV por punto.
pub const POINT_CSV_HEADER: &str =
    "seed,chunk_x,chunk_z,ix,iz,x,z,isovist_area_m2,perimeter_m,occlusivity,jaggedness,clustering,drift_m";

pub fn point_csv(m: &ChunkMetrics) -> String {
    let mut s = String::with_capacity(m.points.len() * 96 + POINT_CSV_HEADER.len() + 1);
    s.push_str(POINT_CSV_HEADER);
    s.push('\n');
    for p in &m.points {
        s.push_str(&format!(
            "{},{},{},{},{},{:.2},{:.2},{:.3},{:.3},{:.5},{:.5},{:.5},{:.4}\n",
            m.seed,
            m.chunk.x,
            m.chunk.z,
            p.ix,
            p.iz,
            p.x,
            p.z,
            p.isovist_area_m2,
            p.perimeter_m,
            p.occlusivity,
            p.jaggedness,
            p.clustering,
            p.drift_m
        ));
    }
    s
}

/// Cabecera del CSV agregado (una fila por chunk).
pub const BATCH_CSV_HEADER: &str =
    "seed,chunk_x,chunk_z,total_cells,open_cells,open_fraction,tile_entropy,\
isovist_area_mean,isovist_area_var,perimeter_mean,perimeter_var,occlusivity_mean,occlusivity_var,\
jaggedness_mean,jaggedness_var,clustering_mean,clustering_var,drift_mean,drift_var";

pub fn batch_csv_row(m: &ChunkMetrics) -> String {
    let (a_m, a_v) = m.moments(|p| p.isovist_area_m2);
    let (p_m, p_v) = m.moments(|p| p.perimeter_m);
    let (o_m, o_v) = m.moments(|p| p.occlusivity);
    let (j_m, j_v) = m.moments(|p| p.jaggedness);
    let (c_m, c_v) = m.moments(|p| p.clustering);
    let (d_m, d_v) = m.moments(|p| p.drift_m);
    format!(
        "{},{},{},{},{},{:.4},{:.4},{:.3},{:.3},{:.3},{:.3},{:.4},{:.5},{:.4},{:.4},{:.5},{:.6},{:.4},{:.4}",
        m.seed,
        m.chunk.x,
        m.chunk.z,
        m.total_cells,
        m.open_cells,
        m.open_cells as f32 / m.total_cells.max(1) as f32,
        m.tile_entropy,
        a_m, a_v, p_m, p_v, o_m, o_v, j_m, j_v, c_m, c_v, d_m, d_v
    )
}

/// Un histograma de una métrica sobre todos los puntos del lote.
pub struct Histogram {
    pub metric: &'static str,
    pub lo: f32,
    pub hi: f32,
    pub counts: [usize; HIST_BINS],
}

impl Histogram {
    pub fn build(metric: &'static str, values: &[f32]) -> Self {
        let lo = values.iter().copied().fold(f32::MAX, f32::min);
        let hi = values.iter().copied().fold(f32::MIN, f32::max);
        let mut counts = [0usize; HIST_BINS];
        let span = (hi - lo).max(f32::EPSILON);
        for &v in values {
            let mut b = (((v - lo) / span) * HIST_BINS as f32) as usize;
            if b >= HIST_BINS {
                b = HIST_BINS - 1;
            }
            counts[b] += 1;
        }
        Self {
            metric,
            lo,
            hi,
            counts,
        }
    }

    pub fn csv_rows(&self) -> String {
        let span = (self.hi - self.lo) / HIST_BINS as f32;
        let mut s = String::new();
        for (i, c) in self.counts.iter().enumerate() {
            s.push_str(&format!(
                "{},{:.4},{:.4},{}\n",
                self.metric,
                self.lo + span * i as f32,
                self.lo + span * (i + 1) as f32,
                c
            ));
        }
        s
    }

    pub fn ascii(&self) -> String {
        let max = self.counts.iter().copied().max().unwrap_or(1).max(1);
        let span = (self.hi - self.lo) / HIST_BINS as f32;
        let mut s = format!("{} [{:.3} .. {:.3}]\n", self.metric, self.lo, self.hi);
        for (i, &c) in self.counts.iter().enumerate() {
            let bar = c * 50 / max;
            s.push_str(&format!(
                "  {:>9.3} |{:<50}| {}\n",
                self.lo + span * i as f32,
                "#".repeat(bar),
                c
            ));
        }
        s
    }
}

pub const HIST_CSV_HEADER: &str = "metric,bin_lo,bin_hi,count";

#[cfg(test)]
mod runner {
    use super::*;
    use std::path::PathBuf;

    fn manifest() -> Wg3Manifest {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("Assets")
            .join("StreamingAssets")
            .join("wg3_manifest.json");
        let text = std::fs::read_to_string(&path)
            .unwrap_or_else(|e| panic!("no se pudo leer {}: {e}", path.display()));
        super::super::manifest::parse_manifest(&text).expect("el manifiesto no valida")
    }

    /// El chunk medido de una región: el CENTRAL, para que las juntas queden fuera del muestreo.
    fn centre_chunk(region: Wg3RegionCoord) -> Wg3ChunkCoord {
        let (min_x, min_z, max_x, max_z) = region.bounds();
        Wg3ChunkCoord::containing((min_x + max_x) * 0.5, (min_z + max_z) * 0.5)
    }

    #[test]
    fn a_sampled_chunk_has_open_and_closed_cells() {
        let m = manifest();
        let grid = sample_chunk(&m, 1, centre_chunk(Wg3RegionCoord { x: 0, z: 0 }));
        let open = grid.open.iter().filter(|&&o| o).count();
        assert_eq!(grid.open.len(), SAMPLES_PER_SIDE * SAMPLES_PER_SIDE);
        assert!(
            open > 0,
            "el chunk muestreado no tiene ni una celda pisable"
        );
        assert!(open < grid.open.len(), "el chunk muestreado no tiene muros");
        let h = grid.tile_entropy();
        assert!((0.0..=1.0).contains(&h), "entropía fuera de rango: {h}");
    }

    #[test]
    fn visibility_is_symmetric_and_isovists_are_bounded() {
        let m = manifest();
        let grid = sample_chunk(&m, 1, centre_chunk(Wg3RegionCoord { x: 0, z: 0 }));
        let g = VisibilityGraph::build(&grid);
        for a in (0..g.nodes.len()).step_by(37) {
            for b in (0..g.nodes.len()).step_by(53) {
                assert_eq!(g.sees(a, b), g.sees(b, a), "visibilidad asimétrica {a}/{b}");
            }
            assert!(!g.sees(a, a), "un nodo no se ve a sí mismo en el grafo");
        }
        let cm = measure_chunk(&m, 1, centre_chunk(Wg3RegionCoord { x: 0, z: 0 }));
        for p in &cm.points {
            assert!(p.isovist_area_m2 > 0.0);
            assert!((0.0..=1.0).contains(&p.occlusivity));
            assert!((0.0..=1.0).contains(&p.clustering));
        }
    }

    /// **EL LOTE.** No corre en la suite normal: se pide a mano.
    ///
    /// ```text
    /// cargo test --release layout_metrics_batch -- --ignored --nocapture
    /// ```
    #[test]
    #[ignore]
    fn layout_metrics_batch() {
        let seeds: u64 = std::env::var("WG3_METRICS_SEEDS")
            .ok()
            .and_then(|s| s.parse().ok())
            .unwrap_or(200);
        let out = std::env::var("WG3_METRICS_OUT").unwrap_or_else(|_| {
            PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("viz_out")
                .join("layout_metrics")
                .to_string_lossy()
                .into_owned()
        });
        let out = PathBuf::from(out);
        std::fs::create_dir_all(&out).expect("no se pudo crear el directorio de salida");
        let m = manifest();
        let coord = centre_chunk(Wg3RegionCoord { x: 0, z: 0 });

        let mut batch = String::from(BATCH_CSV_HEADER);
        batch.push('\n');
        let mut all: Vec<Vec<f32>> = vec![Vec::new(); 6];
        let start = std::time::Instant::now();

        for seed in 1..=seeds {
            let cm = measure_chunk(&m, seed, coord);
            std::fs::write(out.join(format!("chunk_seed{seed:04}.csv")), point_csv(&cm))
                .expect("no se pudo escribir el CSV del chunk");
            batch.push_str(&batch_csv_row(&cm));
            batch.push('\n');
            for p in &cm.points {
                all[0].push(p.isovist_area_m2);
                all[1].push(p.perimeter_m);
                all[2].push(p.occlusivity);
                all[3].push(p.jaggedness);
                all[4].push(p.clustering);
                all[5].push(p.drift_m);
            }
            if seed % 20 == 0 {
                println!(
                    "[wg3-metrics] {seed}/{seeds} chunks, {:.1} s",
                    start.elapsed().as_secs_f32()
                );
            }
        }
        std::fs::write(out.join("batch.csv"), &batch).expect("no se pudo escribir el agregado");

        let names = [
            "isovist_area_m2",
            "perimeter_m",
            "occlusivity",
            "jaggedness",
            "clustering",
            "drift_m",
        ];
        let mut hist = String::from(HIST_CSV_HEADER);
        hist.push('\n');
        for (name, values) in names.iter().zip(&all) {
            let h = Histogram::build(name, values);
            hist.push_str(&h.csv_rows());
            println!("{}", h.ascii());
        }
        std::fs::write(out.join("histograms.csv"), &hist)
            .expect("no se pudieron escribir los histogramas");
        println!(
            "[wg3-metrics] {} chunks, {} puntos, {:.1} s → {}",
            seeds,
            all[0].len(),
            start.elapsed().as_secs_f32(),
            out.display()
        );
    }
}
