//! ADR-095 — el ráster de un chunk.
//!
//! # Una sola pasada, y el borde no decide nada
//!
//! La propiedad que sostiene todo lo que viene después: **rasterizar una pieza dentro de un chunk
//! da exactamente las mismas celdas que rasterizarla en una región que la contenga entera.** El
//! borde del chunk recorta, nunca modifica. Sin eso, una pieza a caballo de dos chunks tendría una
//! colisión distinta a cada lado de una línea invisible, y el síntoma —quedarse enganchado en mitad
//! de un pasillo— no señalaría en ningún momento a la frontera.
//!
//! Es también el cimiento de la ruta A1 (contrato de frontera), que ADR-095 deja para su propio
//! ADR: si el recorte cambiara el resultado, dos chunks vecinos no podrían coincidir sin hablarse
//! ni aunque el sorteo fuese idéntico.
//!
//! # Una capa, no cuatro
//!
//! WG2 tiene un layout por chunk Y POR CAPA porque su celda solo sabe de un suelo. Aquí una columna
//! de tramos es continua y cubre toda la altura, así que **hay un ráster por chunk y ya está**. Es
//! la simplificación que se cobra D2 sola, y hay que tenerla delante al comparar memoria: 159 KB
//! aquí se comparan con cuatro `ChunkLayoutV1`, no con uno.

use super::manifest::Wg3Manifest;
use super::placement::{self, Wg3Placement};
use super::raster::{Wg3Raster, Wg3RasterBuilder, CM_PER_M, WG3_CELL_M};
use super::segment::{self, Wg3Carve, Wg3Segment};

/// Lado del chunk en metros. Mismo tamaño que el chunk de WG2 a propósito: mientras los dos mundos
/// convivan (D3), el streaming, la caché de simulación y los logs hablan de la misma cuadrícula, y
/// una unidad distinta obligaría a traducir en cada frontera entre sistemas.
pub const WG3_CHUNK_M: f32 = 50.0;

/// Celdas por lado de chunk. `50 / 0,5 = 100`.
pub const WG3_CHUNK_CELLS: usize = (WG3_CHUNK_M / WG3_CELL_M) as usize;

/// Coordenada de chunk. El chunk `(0,0)` va de `(0,0)` a `(50,50)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Wg3ChunkCoord {
    pub x: i32,
    pub z: i32,
}

impl Wg3ChunkCoord {
    /// El chunk que contiene un punto de mundo.
    ///
    /// `div_euclid` y no `/`: la división trunca hacia cero, así que −1 y +1 caerían en el mismo
    /// chunk y todo el hemisferio negativo quedaría espejado. Es el mismo fallo que obligó a
    /// `div_euclid` al tallar salas a caballo de dos chunks, y es invisible salvo que se mire.
    pub fn containing(x: f32, z: f32) -> Self {
        Self {
            x: (x / WG3_CHUNK_M).floor() as i32,
            z: (z / WG3_CHUNK_M).floor() as i32,
        }
    }

    /// Esquina mínima en centímetros enteros. En centímetros y no en metros porque es el origen de
    /// un ráster, y el origen de un ráster tiene que ser exacto: un `f32` a 5 km del centro ya no
    /// distingue el milímetro y las celdas empezarían a desalinearse con la geometría.
    pub fn origin_cm(&self) -> (i32, i32) {
        let side = (WG3_CHUNK_M * CM_PER_M) as i32;
        (self.x * side, self.z * side)
    }

    /// `(min_x, min_z, max_x, max_z)` en metros.
    pub fn bounds(&self) -> (f32, f32, f32, f32) {
        let x = self.x as f32 * WG3_CHUNK_M;
        let z = self.z as f32 * WG3_CHUNK_M;
        (x, z, x + WG3_CHUNK_M, z + WG3_CHUNK_M)
    }
}

/// Rasteriza en un chunk todas las colocaciones que lo tocan.
///
/// Las que no lo tocan se descartan por caja envolvente antes de mirar una sola caja: una pieza
/// grande son más de treinta cajas, y `add_box` acaba visitando celdas incluso cuando el recorte las
/// tira todas. Es la única optimización de esta función y va aquí porque el filtro por huella es
/// exacto — no descarta nada que pudiera haber entrado.
///
/// ADR-098 — los TRAMOS generados entran por el mismo sitio y con las mismas reglas: se descartan
/// por caja envolvente y se estampan caja a caja. No hay código de rasterizado nuevo, y eso no es
/// suerte: un tramo se expande a la misma lista de cajas que una chuleta.
pub fn build_chunk_raster(
    manifest: &Wg3Manifest,
    placements: &[Wg3Placement],
    segments: &[Wg3Segment],
    coord: Wg3ChunkCoord,
) -> Wg3Raster {
    build_chunk_raster_with_carves(manifest, placements, segments, &[], coord)
}

/// ADR-099 D3 — igual que [`build_chunk_raster`], pero excavando los vanos al final.
///
/// **El orden no es un detalle: los vanos van DESPUÉS de estampar todo.** Un vano se abre en una
/// pared que ya existe, así que excavar antes no quitaría nada y excavar a medias —entre las piezas
/// y los tramos— dejaría que el tramo volviera a tapiar lo que la pieza acababa de abrir.
///
/// Va en una función aparte y no como parámetro más de la de siempre para no tocar los catorce
/// sitios que la llaman con mundos sin absorción.
pub fn build_chunk_raster_with_carves(
    manifest: &Wg3Manifest,
    placements: &[Wg3Placement],
    segments: &[Wg3Segment],
    carves: &[Wg3Carve],
    coord: Wg3ChunkCoord,
) -> Wg3Raster {
    let (ox, oz) = coord.origin_cm();
    let mut builder = Wg3RasterBuilder::new(ox, oz, WG3_CHUNK_CELLS, WG3_CHUNK_CELLS);
    let (cmin_x, cmin_z, cmax_x, cmax_z) = coord.bounds();

    for placement in placements {
        let Some(piece) = manifest.piece(placement.piece) else {
            // Una colocación que apunta a una pieza que no existe es un manifiesto desparejado del
            // mundo que lo generó. Se salta con ruido en vez de en silencio: el síntoma sería un
            // agujero por el que se cae, y eso hay que poder buscarlo por el log.
            log::warn!(
                "[wg3] colocación con pieza {} fuera del catálogo",
                placement.piece
            );
            continue;
        };

        let (pmin_x, pmin_z, pmax_x, pmax_z) = placement.bounds(piece);
        if pmax_x <= cmin_x || pmin_x >= cmax_x || pmax_z <= cmin_z || pmin_z >= cmax_z {
            continue;
        }

        for b in placement::placed_collision(piece, placement) {
            builder.add_box(&b);
        }
    }

    for c in segments {
        let (pmin_x, pmin_z, pmax_x, pmax_z) = c.bounds();
        if pmax_x <= cmin_x || pmin_x >= cmax_x || pmax_z <= cmin_z || pmin_z >= cmax_z {
            continue;
        }
        for b in segment::segment_boxes(c) {
            builder.add_box(&b);
        }
    }

    // ADR-099 D3 — y al final, quitar. No se filtran por caja envolvente como lo demás: un vano
    // mide medio metro y el recorte costaría más que excavarlo.
    for k in carves {
        builder.carve_box(
            k.x_cm as f32 / 100.0,
            k.z_cm as f32 / 100.0,
            (k.x_cm + k.size_x_cm) as f32 / 100.0,
            (k.z_cm + k.size_z_cm) as f32 / 100.0,
            k.bottom_y_cm,
            k.top_y_cm,
        );
    }

    builder.finish()
}

/// Los chunks que toca una colocación. Sirve para saber a cuántos hay que re-rasterizar cuando
/// entra una pieza, y para las pruebas de costura.
pub fn chunks_touched(manifest: &Wg3Manifest, placement: &Wg3Placement) -> Vec<Wg3ChunkCoord> {
    let Some(piece) = manifest.piece(placement.piece) else {
        return Vec::new();
    };
    let (min_x, min_z, max_x, max_z) = placement.bounds(piece);

    let c0 = Wg3ChunkCoord::containing(min_x, min_z);
    // El máximo es EXCLUSIVO: una pieza que acaba justo en x = 50 no entra en el chunk 1. Restar un
    // épsilon en vez de usar el borde evita que una huella clavada en la frontera reclame un chunk
    // entero en el que no pone ni una celda.
    let c1 = Wg3ChunkCoord::containing(max_x - 1e-3, max_z - 1e-3);

    let mut out = Vec::new();
    for z in c0.z..=c1.z {
        for x in c0.x..=c1.x {
            out.push(Wg3ChunkCoord { x, z });
        }
    }
    out
}
