//! ADR-095 D1 y D2 — la chuleta rasterizada a columnas de tramos.
//!
//! # Por qué se rasteriza, y no se guarda la lista de cajas
//!
//! Probar cada movimiento contra las cajas con giro de las piezas cercanas sería exacto al
//! milímetro, pero reescribe la colisión del jugador entera y deja **dos representaciones del mundo
//! conviviendo**: rejilla para lo que quede de WG2, cajas para WG3. Ésa es la duplicación que este
//! repositorio ya paga cara (`LayerGrid` a 2,5 m contra `ChunkLayoutV1` a 5 m, con dos rutas de
//! tallado que hay que mantener de acuerdo a mano). Con una sola representación rasterizada, el
//! laberinto viejo puede seguir alimentando el mismo ráster mientras dure la migración.
//!
//! # Por qué tramos y no un mapa de alturas
//!
//! Un suelo y un techo por celda da rampas, escalones y salas hundidas, pero **nada encima de
//! nada**: sin balcones, sin entreplantas, sin escalera alrededor de un hueco. Una columna de
//! tramos sí. Se elige el formato capaz desde el principio porque el lector puede crecer hacia él y
//! el formato no puede migrar hacia atrás con el mundo en marcha.
//!
//! Y trae una simplificación que se cobra sola: **con tramos la CAPA desaparece**. No hay un ráster
//! por chunk y capa, hay uno por chunk que cubre toda la altura, porque una columna es continua.
//!
//! # El rasterizado es CONSERVADOR, y es la decisión que sostiene todo
//!
//! Una celda bloquea si la caja la toca lo más mínimo. No se muestrea el centro: una pared mide
//! 0,15 m y una celda 0,5 m, así que una pared entre dos centros **desaparecería** y se atravesaría
//! andando — el peor fallo posible aquí, porque el cliente la sigue dibujando.
//!
//! El precio es que cada pared se infla hasta media celda y eso COME VANO. Cuánto exactamente es
//! un número, no una opinión, y lo mide `narrowest_doorway_clearance` en los tests: si baja del
//! diámetro del jugador, el tamaño de celda de D1 está mal elegido y hay que bajarlo.

use super::placement::PlacedBox;

/// Lado de celda del ráster, en metros. ADR-095 D1.
///
/// Diez veces más fino que la colisión de WG2 (celdas de 5 m). Y el número trae un regalo que lo
/// convierte en la decisión correcta y no solo en la barata: a 0,5 m una columna de medio metro
/// colisiona bien y una moldura de 15 cm no colisiona en absoluto. **La resolución del ráster ES la
/// línea entre estructura y decoración**, escrita como número en vez de como intención.
pub const WG3_CELL_M: f32 = 0.5;

/// Unidad vertical: el centímetro.
///
/// Entero y no flotante para que fundir tramos sea determinista y no dependa de por dónde se
/// acumuló el error. `i16` en centímetros cubre ±327 m, veinte veces la altura del mundo actual, y
/// deja un tramo en 4 bytes. Y da exactos los números que importan: la contrahuella de 0,18 m del
/// catálogo son 18, no 17,999.
pub const CM_PER_M: f32 = 100.0;

/// Un tramo macizo de una columna. `bottom_cm` es la cara de abajo; `top_cm`, la de arriba.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Span {
    pub bottom_cm: i16,
    pub top_cm: i16,
}

impl Span {
    #[inline]
    pub fn contains(&self, y_cm: i32) -> bool {
        y_cm >= self.bottom_cm as i32 && y_cm <= self.top_cm as i32
    }

    #[inline]
    pub fn overlaps(&self, lo_cm: i32, hi_cm: i32) -> bool {
        (self.bottom_cm as i32) < hi_cm && (self.top_cm as i32) > lo_cm
    }
}

/// Construye un ráster metiendo cajas. Se separa del ráster terminado porque construir necesita
/// crecer por columna y consultar necesita una tabla plana: mezclar las dos formas dejaría el caso
/// caliente pagando la flexibilidad del caso frío.
pub struct Wg3RasterBuilder {
    origin_x_cm: i32,
    origin_z_cm: i32,
    cells_x: usize,
    cells_z: usize,
    columns: Vec<Vec<Span>>,
}

impl Wg3RasterBuilder {
    pub fn new(origin_x_cm: i32, origin_z_cm: i32, cells_x: usize, cells_z: usize) -> Self {
        Self {
            origin_x_cm,
            origin_z_cm,
            cells_x,
            cells_z,
            columns: vec![Vec::new(); cells_x * cells_z],
        }
    }

    /// Ráster que cubre exactamente una caja envolvente de mundo, en metros, con margen de una
    /// celda por lado para que un borde que caiga justo en la frontera no se pierda.
    pub fn covering(min_x: f32, min_z: f32, max_x: f32, max_z: f32) -> Self {
        let cell_cm = (WG3_CELL_M * CM_PER_M) as i32;
        let ox = ((min_x * CM_PER_M).floor() as i32).div_euclid(cell_cm) - 1;
        let oz = ((min_z * CM_PER_M).floor() as i32).div_euclid(cell_cm) - 1;
        let ex = ((max_x * CM_PER_M).ceil() as i32).div_euclid(cell_cm) + 2;
        let ez = ((max_z * CM_PER_M).ceil() as i32).div_euclid(cell_cm) + 2;
        Self::new(
            ox * cell_cm,
            oz * cell_cm,
            (ex - ox) as usize,
            (ez - oz) as usize,
        )
    }

    /// Mete una caja. CONSERVADOR: toda celda que la caja toque queda maciza.
    pub fn add_box(&mut self, b: &PlacedBox) {
        let (hx, hy, hz) = (b.size[0] * 0.5, b.size[1] * 0.5, b.size[2] * 0.5);
        if hx <= 0.0 || hy <= 0.0 || hz <= 0.0 {
            return;
        }

        // Conservador también en vertical: se baja el suelo y se sube el techo al centímetro
        // entero, nunca al revés. Redondear al más cercano dejaría medio centímetro de aire bajo
        // una losa, y medio centímetro es suficiente para que un test de "estoy en el suelo" falle
        // de forma intermitente.
        let bottom = ((b.center[1] - hy) * CM_PER_M).floor();
        let top = ((b.center[1] + hy) * CM_PER_M).ceil();
        let span = Span {
            bottom_cm: bottom.clamp(i16::MIN as f32, i16::MAX as f32) as i16,
            top_cm: top.clamp(i16::MIN as f32, i16::MAX as f32) as i16,
        };
        if span.top_cm <= span.bottom_cm {
            return;
        }

        let rad = b.yaw_degrees.to_radians();
        let (sin, cos) = rad.sin_cos();
        // Convención de Unity para el giro en Y: el eje local X va a (cos, −sin) en XZ, y el local
        // Z a (sin, cos). Escribirlo al revés gira las piezas al espejo y solo se nota con una
        // pieza asimétrica, que es tarde.
        let ux = cos;
        let uz = -sin;
        let vx = sin;
        let vz = cos;

        let ext_x = hx * ux.abs() + hz * vx.abs();
        let ext_z = hx * uz.abs() + hz * vz.abs();

        let (ix0, ix1) = self.cell_range_x(b.center[0] - ext_x, b.center[0] + ext_x);
        let (iz0, iz1) = self.cell_range_z(b.center[2] - ext_z, b.center[2] + ext_z);

        let half = WG3_CELL_M * 0.5;
        for iz in iz0..iz1 {
            for ix in ix0..ix1 {
                let (ccx, ccz) = self.cell_centre(ix, iz);
                if !obb_overlaps_cell(
                    b.center[0] - ccx,
                    b.center[2] - ccz,
                    hx,
                    hz,
                    ux,
                    uz,
                    vx,
                    vz,
                    half,
                ) {
                    continue;
                }
                self.columns[iz * self.cells_x + ix].push(span);
            }
        }
    }

    /// ADR-099 D3 — EXCAVA UN VANO: quita materia en vez de ponerla.
    ///
    /// Es la única operación del ráster que RESTA, y va necesariamente después de estampar: un vano
    /// se abre en una pared que ya existe. Sin esto una pieza colocada es inmutable, que es la
    /// diferencia de fondo entre WG3 y el sistema de salas que Joel echaba de menos — allí una sala
    /// autorada no encajaba con el laberinto y se le EXCAVABA el vano (`carve_authored_into_layout`)
    /// en vez de exigirle que encajara.
    ///
    /// **ANTI-CONSERVADOR A PROPÓSITO, y es lo contrario que `add_box`.** Aquélla maciza toda celda
    /// que la caja TOQUE, porque equivocarse hacia el macizo es seguro. Ésta abre solo la celda cuyo
    /// CENTRO cae dentro, porque equivocarse hacia el hueco abre pared que sostenía algo. Con el
    /// mínimo de 200 cm de vano quedan tres celdas limpias de las cuatro que toca, de sobra para los
    /// 70 cm que mide el jugador.
    ///
    /// La banda vertical NO llega al suelo: se deja `CARVE_FLOOR_GUARD_CM` por debajo intactos, o el
    /// vano se llevaría por delante la losa sobre la que se anda y abriría un agujero en vez de una
    /// puerta.
    pub fn carve_box(
        &mut self,
        min_x: f32,
        min_z: f32,
        max_x: f32,
        max_z: f32,
        bottom_cm: i32,
        top_cm: i32,
    ) {
        let lo = bottom_cm.clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        let hi = top_cm.clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        if hi <= lo {
            return;
        }

        let (ix0, ix1) = self.cell_range_x(min_x, max_x);
        let (iz0, iz1) = self.cell_range_z(min_z, max_z);
        for iz in iz0..iz1 {
            for ix in ix0..ix1 {
                let (ccx, ccz) = self.cell_centre(ix, iz);
                if ccx < min_x || ccx > max_x || ccz < min_z || ccz > max_z {
                    continue;
                }
                let column = &mut self.columns[iz * self.cells_x + ix];
                let mut out = Vec::with_capacity(column.len());
                for span in column.iter() {
                    // Cuatro casos, y el tercero es el que obliga a reconstruir la columna entera:
                    // un vano en mitad de un muro alto lo parte en dos tramos, el zócalo y el dintel.
                    if span.top_cm <= lo || span.bottom_cm >= hi {
                        out.push(*span);
                        continue;
                    }
                    if span.bottom_cm < lo {
                        out.push(Span {
                            bottom_cm: span.bottom_cm,
                            top_cm: lo,
                        });
                    }
                    if span.top_cm > hi {
                        out.push(Span {
                            bottom_cm: hi,
                            top_cm: span.top_cm,
                        });
                    }
                }
                *column = out;
            }
        }
    }

    fn cell_centre(&self, ix: usize, iz: usize) -> (f32, f32) {
        let ox = self.origin_x_cm as f32 / CM_PER_M;
        let oz = self.origin_z_cm as f32 / CM_PER_M;
        (
            ox + (ix as f32 + 0.5) * WG3_CELL_M,
            oz + (iz as f32 + 0.5) * WG3_CELL_M,
        )
    }

    fn cell_range_x(&self, lo: f32, hi: f32) -> (usize, usize) {
        let ox = self.origin_x_cm as f32 / CM_PER_M;
        let a = ((lo - ox) / WG3_CELL_M).floor().max(0.0) as usize;
        let b = (((hi - ox) / WG3_CELL_M).ceil().max(0.0) as usize).min(self.cells_x);
        (a.min(self.cells_x), b)
    }

    fn cell_range_z(&self, lo: f32, hi: f32) -> (usize, usize) {
        let oz = self.origin_z_cm as f32 / CM_PER_M;
        let a = ((lo - oz) / WG3_CELL_M).floor().max(0.0) as usize;
        let b = (((hi - oz) / WG3_CELL_M).ceil().max(0.0) as usize).min(self.cells_z);
        (a.min(self.cells_z), b)
    }

    /// Ordena, funde y aplana. Fundir tramos que se TOCAN (no solo los que se solapan) es lo que
    /// hace que una pared apoyada en la losa de suelo sea un solo tramo macizo y no dos con una
    /// junta de espesor cero por la que una consulta podría colarse.
    pub fn finish(mut self) -> Wg3Raster {
        let mut offsets = Vec::with_capacity(self.columns.len() + 1);
        let mut spans = Vec::new();

        for column in self.columns.iter_mut() {
            offsets.push(spans.len() as u32);
            if column.is_empty() {
                continue;
            }
            column.sort_unstable_by_key(|s| (s.bottom_cm, s.top_cm));

            let mut current = column[0];
            for next in column.iter().skip(1) {
                if next.bottom_cm <= current.top_cm {
                    current.top_cm = current.top_cm.max(next.top_cm);
                } else {
                    spans.push(current);
                    current = *next;
                }
            }
            spans.push(current);
        }
        offsets.push(spans.len() as u32);

        Wg3Raster {
            origin_x_cm: self.origin_x_cm,
            origin_z_cm: self.origin_z_cm,
            cells_x: self.cells_x,
            cells_z: self.cells_z,
            offsets,
            spans,
        }
    }
}

/// Un ráster terminado: tabla plana de columnas, cada una con sus tramos ordenados y sin solapes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Wg3Raster {
    origin_x_cm: i32,
    origin_z_cm: i32,
    cells_x: usize,
    cells_z: usize,
    /// `cells_x * cells_z + 1` entradas. La cuenta de una columna sale de la diferencia con la
    /// siguiente, que ahorra guardar la longitud por celda.
    offsets: Vec<u32>,
    spans: Vec<Span>,
}

impl Wg3Raster {
    pub fn cells_x(&self) -> usize {
        self.cells_x
    }
    pub fn cells_z(&self) -> usize {
        self.cells_z
    }
    pub fn span_count(&self) -> usize {
        self.spans.len()
    }

    /// Memoria que ocupa de verdad. R10: el presupuesto se mide, no se estima.
    pub fn bytes(&self) -> usize {
        self.offsets.len() * std::mem::size_of::<u32>()
            + self.spans.len() * std::mem::size_of::<Span>()
    }

    /// Índice de celda de un punto de mundo, o `None` si cae fuera.
    pub fn cell_of(&self, x: f32, z: f32) -> Option<(usize, usize)> {
        let ox = self.origin_x_cm as f32 / CM_PER_M;
        let oz = self.origin_z_cm as f32 / CM_PER_M;
        let fx = (x - ox) / WG3_CELL_M;
        let fz = (z - oz) / WG3_CELL_M;
        if fx < 0.0 || fz < 0.0 {
            return None;
        }
        let (ix, iz) = (fx as usize, fz as usize);
        if ix >= self.cells_x || iz >= self.cells_z {
            return None;
        }
        Some((ix, iz))
    }

    pub fn column(&self, ix: usize, iz: usize) -> &[Span] {
        let i = iz * self.cells_x + ix;
        let from = self.offsets[i] as usize;
        let to = self.offsets[i + 1] as usize;
        &self.spans[from..to]
    }

    pub fn column_at(&self, x: f32, z: f32) -> &[Span] {
        match self.cell_of(x, z) {
            Some((ix, iz)) => self.column(ix, iz),
            None => &[],
        }
    }

    /// ¿Hay materia en ese punto exacto?
    pub fn is_solid_at(&self, x: f32, y: f32, z: f32) -> bool {
        let y_cm = (y * CM_PER_M).round() as i32;
        self.column_at(x, z).iter().any(|s| s.contains(y_cm))
    }

    /// ¿Choca un cuerpo de pie en `y` con `height` metros de alto?
    ///
    /// El intervalo se abre un centímetro por arriba y por abajo para que estar POSADO en el suelo
    /// no cuente como estar dentro de él: sin ese margen, todo cuerpo apoyado estaría siempre en
    /// colisión y la primera consulta de la primera partida devolvería `true`.
    pub fn blocked_standing_at(&self, x: f32, y: f32, z: f32, height: f32) -> bool {
        let lo = (y * CM_PER_M).round() as i32 + 1;
        let hi = ((y + height) * CM_PER_M).round() as i32 - 1;
        if hi <= lo {
            return false;
        }
        self.column_at(x, z).iter().any(|s| s.overlaps(lo, hi))
    }

    /// Cota del suelo pisable bajo `y`: la cara superior del tramo macizo más alto que quede por
    /// debajo. `None` si no hay nada debajo — que es caerse, no un error.
    pub fn floor_below(&self, x: f32, y: f32, z: f32) -> Option<f32> {
        let y_cm = (y * CM_PER_M).round() as i32;
        self.column_at(x, z)
            .iter()
            .filter(|s| (s.top_cm as i32) <= y_cm)
            .map(|s| s.top_cm)
            .max()
            .map(|top| top as f32 / CM_PER_M)
    }

    /// Hueco libre por encima del suelo que hay bajo `y`. Es lo que decide si un sitio es
    /// caminable, y lo que en su día distinguirá un altillo de un hueco de servicio.
    pub fn headroom_above_floor(&self, x: f32, y: f32, z: f32) -> Option<f32> {
        let floor = self.floor_below(x, y, z)?;
        let floor_cm = (floor * CM_PER_M).round() as i32;
        let ceiling = self
            .column_at(x, z)
            .iter()
            .filter(|s| (s.bottom_cm as i32) > floor_cm)
            .map(|s| s.bottom_cm)
            .min();
        Some(match ceiling {
            Some(c) => (c as i32 - floor_cm) as f32 / CM_PER_M,
            None => f32::INFINITY,
        })
    }
}

/// SAT entre una caja con giro y una celda alineada a los ejes. `dx`/`dz` es el vector del centro
/// de la celda al centro de la caja; `half` es el medio lado de la celda.
///
/// Cuatro ejes y no dos: los de la celda no bastan cuando la caja está girada, y saltárselos daría
/// por tocada una celda que la caja solo roza por la diagonal. Con giros múltiplos de 90°, que es
/// todo lo que hay hoy en el catálogo, degenera en la comparación de dos AABB.
#[allow(clippy::too_many_arguments)]
fn obb_overlaps_cell(
    dx: f32,
    dz: f32,
    hx: f32,
    hz: f32,
    ux: f32,
    uz: f32,
    vx: f32,
    vz: f32,
    half: f32,
) -> bool {
    if dx.abs() > hx * ux.abs() + hz * vx.abs() + half {
        return false;
    }
    if dz.abs() > hx * uz.abs() + hz * vz.abs() + half {
        return false;
    }
    if (dx * ux + dz * uz).abs() > hx + half * (ux.abs() + uz.abs()) {
        return false;
    }
    if (dx * vx + dz * vz).abs() > hz + half * (vx.abs() + vz.abs()) {
        return false;
    }
    true
}
