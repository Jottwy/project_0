//! ADR-098 T1 — la CELDA generada: geometría que el servidor sintetiza cuando el catálogo no encaja.
//!
//! # Qué es
//!
//! Un rectángulo alineado a los ejes con sus bocas declaradas igual que las de una pieza —lado más
//! offset recorriendo el perímetro en horario desde `(0, D)`—, su cota de suelo y su altura libre.
//! De ahí sale la geometría por una regla que cabe en tres líneas: losa de suelo bajo la huella,
//! losa de techo sobre ella, y en cada lado la pared partida por las bocas de ese lado.
//!
//! **No hay geometría nueva: hay una pieza que nadie dibujó.** La regla es exactamente la que
//! `Wg3Geometry.Build` aplica en C# a una pieza sin volúmenes horneados, y el lado de Unity la
//! reutiliza literalmente construyendo un `Wg3Piece` sintético en vez de escribirla otra vez.
//!
//! # Por qué un solo tipo, y no "tramos y esquinas"
//!
//! Con bocas libres, la misma tramo cubre los cuatro casos que necesita un conector:
//!
//! - **tramo recto** — dos bocas en lados opuestos;
//! - **quiebro** — dos bocas en lados perpendiculares;
//! - **transición de ancho** — dos bocas opuestas de anchura distinta: la pared del lado estrecho se
//!   parte sola y quedan sus dos jambas, sin un caso especial;
//! - **escalón** — dos tramos contiguas con `floor_y_cm` distinto: la losa de la de arriba ES la
//!   contrahuella.
//!
//! # La partida doble, y quién la vigila
//!
//! Esta expansión está escrita dos veces —C# dibuja, Rust rasteriza— y dos implementaciones
//! internamente consistentes pueden diferir sin que nada reviente: el síntoma sería una pared que se
//! ve y no frena, o al revés. La ata `backend/tests/fixtures/wg3_connector_oracle.json`, exportado
//! desde Unity (que es el lado que fija el aspecto) y reproducido por un test de aquí. Solo se
//! comparan los volúmenes SÓLIDOS: la decoración no cruza la frontera de autoridad (R25), así que el
//! rodapié de un conector es asunto del cliente y no entra en el fixture.
//!
//! Y el digest del catálogo NO cubre esto —un tramo no está en el manifiesto—, dicho aquí para que
//! nadie lea un verde de más.

use super::placement::PlacedBox;
use super::raster::CM_PER_M;

/// Centimetros a metros, con la MISMA operacion que C# (`cm * 0.01f`) y no dividiendo entre 100.
///
/// **No es equivalente, y costo un test rojo.** `0.01` no es representable en binario, asi que
/// `240 * 0.01f` sale un pelo POR DEBAJO de 2,4 y `240 / 100.0` un pelo por encima. Con una pared de
/// 15 mm eso mueve su centro de 232,4999 a 232,5001 cm, y al redondear salen 232 y 233: un
/// centimetro de diferencia entre lo que dibuja el cliente y lo que bloquea el servidor. La
/// resolucion del raster lo taparia hoy, pero el oraculo compara al centimetro y hace bien.
#[inline]
pub fn metres(centimetres: i32) -> f32 {
    centimetres as f32 * 0.01
}

/// Grosor de losa de suelo y techo. Espejo de `Wg3Geometry.SlabThickness`.
pub const SLAB_THICKNESS_M: f32 = 0.12;

/// Grosor de pared, hacia DENTRO de la huella. Espejo del valor por defecto de
/// `Wg3Piece.wallThickness`, que es el que usa el catálogo de código.
pub const WALL_THICKNESS_M: f32 = 0.15;

/// Discriminantes de `Wg3VolumeKind`. Duplicados y no importados: el ráster no los mira —todo lo que
/// llega bloquea— pero hacen legible un volcado, y el fixture los compara.
pub const KIND_FLOOR: u8 = 0;
pub const KIND_CEILING: u8 = 1;
pub const KIND_WALL: u8 = 2;

/// **ANCHURA MÍNIMA DE UNA BOCA GENERADA, Y ES UN NÚMERO MEDIDO, NO ELEGIDO.**
///
/// El ráster es CONSERVADOR: toda celda que una caja toque queda maciza (`raster.rs`), así que cada
/// pared de 15 cm se infla hasta ocupar su celda de 50 cm entera y **come vano por los dos lados**.
/// `narrowest_doorway_clearance` lo mide barriendo la alineación sub-celda, que es la que manda
/// porque el mundo se coloca en centímetros arbitrarios:
///
/// | boca | hueco libre en el peor caso |
/// |---|---|
/// | 120 cm | **0,00 m** — tapiada |
/// | 200 cm | 0,99 m |
/// | 240 cm | 1,49 m |
/// | 500 cm | 3,99 m |
///
/// El jugador mide 0,70 m de diámetro, así que 200 es el primer escalón de 50 cm que pasa. Por
/// debajo, el cliente dibuja un pasillo abierto y el servidor no deja entrar — el peor fallo
/// posible, porque no se ve en una captura.
///
/// El catálogo autorado se libró por accidente: sus bocas son de 2,4 y 5,0 m. Lo que ADR-098 empezó
/// a GENERAR bajaba de ahí.
pub const MIN_GENERATED_WIDTH_CM: i32 = 200;

/// Lado máximo de un tramo, en metros.
///
/// **Es lo que deja intacto el reparto por chunk.** «Una pieza, un chunk» se sostiene sobre que
/// ninguna pieza llega a los 50 m del chunk, así que centrada nunca asoma más allá de los vecinos
/// inmediatos de su dueño. Una ruta larga se parte en más tramos —que es gratis— en vez de obligar a
/// recortar geometría en la frontera.
pub const MAX_SEGMENT_M: f32 = 25.0;

/// Una boca de el tramo. Misma parametrización que `Wg3Socket`: el lado más el offset recorriendo el
/// perímetro en horario desde `(0, D)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Wg3Opening {
    /// `0 = N (+Z)`, `1 = E (+X)`, `2 = S (−Z)`, `3 = O (−X)`.
    pub side: u8,
    /// Metros a lo largo del lado, hasta el CENTRO de la boca. En centímetros enteros.
    pub offset_cm: i32,
    pub width_cm: i32,
}

/// Un rectángulo generado, con sus bocas.
///
/// EN CENTÍMETROS ENTEROS, por lo mismo que `Wg3Placement`: esto viaja, se compara entre dos
/// procesos y tiene que coincidir bit a bit. Una cadena de sumas en `f32` no lo garantiza.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Wg3Segment {
    /// Esquina mínima de la huella, en centímetros de mundo.
    pub x_cm: i32,
    pub z_cm: i32,
    pub size_x_cm: i32,
    pub size_z_cm: i32,
    /// Cota del suelo, en centímetros de mundo (ADR-097, mismas unidades que la colocación).
    pub floor_y_cm: i32,
    /// Altura LIBRE, de suelo a techo. La losa de techo va por encima.
    pub height_cm: i32,
    /// De una a cuatro. Un tramo sin bocas sería una caja maciza, y hay test que lo prohíbe.
    pub openings: Vec<Wg3Opening>,
    /// Aspecto. El servidor no lo interpreta: es el gancho para que el cliente vista los conectores
    /// y el mundo no se lea generado.
    pub style: u8,
}

impl Wg3Segment {
    pub fn min_x(&self) -> f32 {
        metres(self.x_cm)
    }
    pub fn min_z(&self) -> f32 {
        metres(self.z_cm)
    }
    pub fn size_x(&self) -> f32 {
        metres(self.size_x_cm)
    }
    pub fn size_z(&self) -> f32 {
        metres(self.size_z_cm)
    }
    pub fn floor_y(&self) -> f32 {
        metres(self.floor_y_cm)
    }
    pub fn height(&self) -> f32 {
        metres(self.height_cm)
    }

    /// `(min_x, min_z, max_x, max_z)` en metros.
    pub fn bounds(&self) -> (f32, f32, f32, f32) {
        let (x, z) = (self.min_x(), self.min_z());
        (x, z, x + self.size_x(), z + self.size_z())
    }

    /// Centro de la huella, en metros. Es lo que decide de qué chunk es el tramo.
    pub fn centre(&self) -> (f32, f32) {
        let (x, z) = (self.min_x(), self.min_z());
        (x + self.size_x() * 0.5, z + self.size_z() * 0.5)
    }

    /// Lo que este módulo necesita que sea cierto antes de emitir un tramo. Vacío = utilizable.
    pub fn problems(&self) -> Vec<String> {
        let mut out = Vec::new();
        if self.size_x_cm <= 0 || self.size_z_cm <= 0 {
            out.push(format!(
                "huella no positiva: {}×{} cm",
                self.size_x_cm, self.size_z_cm
            ));
        }
        let max_cm = (MAX_SEGMENT_M * CM_PER_M) as i32;
        if self.size_x_cm > max_cm || self.size_z_cm > max_cm {
            // El tope no es estético: es lo que sostiene el reparto por chunk (ver `MAX_SEGMENT_M`).
            out.push(format!(
                "tramo de {}×{} cm por encima del tope de {} cm — el reparto por chunk depende de \
                 este número",
                self.size_x_cm, self.size_z_cm, max_cm
            ));
        }
        if self.height_cm <= 0 {
            out.push(format!("altura no positiva: {} cm", self.height_cm));
        }
        if self.openings.is_empty() {
            out.push("sin bocas: sería una caja maciza".to_string());
        }
        for o in &self.openings {
            if o.width_cm <= 0 {
                out.push(format!("boca de anchura {} cm", o.width_cm));
            } else if o.width_cm < MIN_GENERATED_WIDTH_CM {
                // No es estética: por debajo de aquí el ráster tapia el vano y el conector nace
                // impasable mientras el cliente lo dibuja abierto. Ver `MIN_GENERATED_WIDTH_CM`.
                out.push(format!(
                    "boca de {} cm por debajo del mínimo de {} cm: el ráster la tapiaría",
                    o.width_cm, MIN_GENERATED_WIDTH_CM
                ));
            }
            let side_cm = if o.side.is_multiple_of(2) {
                self.size_x_cm
            } else {
                self.size_z_cm
            };
            let half = o.width_cm / 2;
            if o.offset_cm - half < 0 || o.offset_cm + half > side_cm {
                out.push(format!(
                    "boca del lado {} a {} cm ± {} cm se sale del lado ({} cm)",
                    o.side, o.offset_cm, half, side_cm
                ));
            }
        }
        out
    }
}

/// La geometría de un tramo, ya en coordenadas de mundo.
///
/// ORDEN DE EMISIÓN: suelo, techo, y luego los lados 0, 1, 2 y 3, cada uno con sus tramos de pared
/// de menor a mayor offset. **El orden es parte del contrato**: el oráculo compara caja a caja y en
/// orden, y reordenar aquí lo pondría rojo sin que nada esté mal — que es peor que un test que no
/// existe.
///
/// La decoración (rodapié) NO se emite: es del cliente (R25). Lo que sale de aquí es exactamente lo
/// que bloquea.
pub fn segment_boxes(cell: &Wg3Segment) -> Vec<PlacedBox> {
    let w = cell.size_x();
    let d = cell.size_z();
    let h = cell.height();
    let (ox, oz, oy) = (cell.min_x(), cell.min_z(), cell.floor_y());

    let mut out = Vec::with_capacity(8);
    let mut push = |kind: u8, cx: f32, cy: f32, cz: f32, sx: f32, sy: f32, sz: f32| {
        out.push(PlacedBox {
            center: [ox + cx, oy + cy, oz + cz],
            size: [sx, sy, sz],
            // Un tramo está alineada a los ejes por construcción: nunca hay giro que aplicar. Es lo
            // que permite partirla en la frontera de un chunk o alargarla sin tocar nada más.
            yaw_degrees: 0.0,
            kind,
        })
    };

    // El suelo cuelga por DEBAJO de la cota de el tramo para que la cara pisable quede exactamente
    // en ella: dos tramos contiguas a la misma cota no dejan escalón de losa, y dos a cotas
    // distintas dejan exactamente su diferencia.
    push(
        KIND_FLOOR,
        w * 0.5,
        -SLAB_THICKNESS_M * 0.5,
        d * 0.5,
        w,
        SLAB_THICKNESS_M,
        d,
    );
    push(
        KIND_CEILING,
        w * 0.5,
        h + SLAB_THICKNESS_M * 0.5,
        d * 0.5,
        w,
        SLAB_THICKNESS_M,
        d,
    );

    for side in 0..4u8 {
        emit_side(cell, side, w, d, h, &mut push);
    }

    out
}

/// La pared de un lado, partida por sus bocas. Espejo de `Wg3Geometry.BuildSide`.
///
/// Es el punto donde «el vano existe» deja de ser una afirmación: si este recorrido se equivoca, la
/// colisión tapa una puerta que se ve abierta.
fn emit_side<F>(cell: &Wg3Segment, side: u8, w: f32, d: f32, h: f32, push: &mut F)
where
    F: FnMut(u8, f32, f32, f32, f32, f32, f32),
{
    let length = if side.is_multiple_of(2) { w } else { d };

    // Se ordenan aquí y no se presume el orden de quien construyó el tramo: dos bocas declaradas al
    // revés dejarían un tramo de longitud negativa.
    let mut cuts: Vec<(f32, f32)> = cell
        .openings
        .iter()
        .filter(|o| o.side % 4 == side)
        .map(|o| {
            let centre = o.offset_cm as f32 / CM_PER_M;
            let half = o.width_cm as f32 / CM_PER_M * 0.5;
            (centre - half, centre + half)
        })
        .collect();
    cuts.sort_by(|a, b| a.0.total_cmp(&b.0));

    let mut cursor = 0.0f32;
    for (lo, hi) in cuts {
        if lo > cursor {
            emit_wall(side, cursor, lo, w, d, h, push);
        }
        cursor = cursor.max(hi);
    }
    if cursor < length {
        emit_wall(side, cursor, length, w, d, h, push);
    }
}

/// Un tramo de pared entre dos offsets del lado. Espejo de `Wg3Geometry.EmitWall`, sin el rodapié.
fn emit_wall<F>(side: u8, from: f32, to: f32, w: f32, d: f32, h: f32, push: &mut F)
where
    F: FnMut(u8, f32, f32, f32, f32, f32, f32),
{
    let mid = (from + to) * 0.5;
    let len = to - from;
    if len <= 1e-3 {
        return;
    }
    let t = WALL_THICKNESS_M;

    match side % 4 {
        // N, z = d. El offset corre en +X.
        0 => push(KIND_WALL, mid, h * 0.5, d - t * 0.5, len, h, t),
        // E, x = w. El offset corre en −Z desde z = d.
        1 => push(KIND_WALL, w - t * 0.5, h * 0.5, d - mid, t, h, len),
        // S, z = 0. El offset corre en −X desde x = w.
        2 => push(KIND_WALL, w - mid, h * 0.5, t * 0.5, len, h, t),
        // O, x = 0. El offset corre en +Z.
        _ => push(KIND_WALL, t * 0.5, h * 0.5, mid, t, h, len),
    }
}
