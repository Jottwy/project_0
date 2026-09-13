//! ADR-122 — la RAMPA: colisión escalonada IDÉNTICA en los dos lados del cable, dibujo liso sólo en
//! el cliente.
//!
//! Un plano inclinado de verdad pediría un volumen con cabeceo, un collider no-caja y un ráster nuevo.
//! Lo que se hace es lo mínimo que se ANDA como rampa: una fila de cajas de una celda de ráster
//! (50 cm) a lo largo del sentido de subida, cada una a la cota de su borde alto. Con la pendiente
//! tope de 1:5 cada caja sube como mucho 10 cm, y el `CharacterController` (`stepOffset` 0,275) y la
//! navegación (27 cm) las pasan sin notarlas como escalón. El cliente construye exactamente estas
//! cajas como `BoxCollider` y dibuja encima una cuña lisa (`Wg3MeshBuilder.AddWedge`).
//!
//! Aquí sólo vive el TIPO y la GEOMETRÍA. Quién pone rampas lo decide el relleno (`fill.rs`), y el
//! ráster las recibe como macizos por `Wg3ServedWorld::solids_touching_chunk`.

use super::segment::{metres, Wg3Solid, SHAPE_BOX};

/// Largo de cada caja de colisión a lo largo de la rampa: la celda del ráster.
///
/// Más fina no significa nada en el servidor (el ráster es conservador y se quedaría con la más alta
/// de las que toque), y más gruesa convierte cada caja en un escalón.
pub const RAMP_CELL_CM: i32 = 50;

/// ADR-122 D4 — lo más que sube una caja respecto a la anterior. 10 cm en 50 es la pendiente 1:5.
pub const RAMP_MAX_RISE_PER_CELL_CM: i32 = 10;

/// ADR-122 D4 — el desnivel máximo que salva una rampa. Por encima, escalera: 1 m a 1:5 ya son 5 m.
pub const RAMP_MAX_RISE_CM: i32 = 100;

/// Una rampa, en centímetros enteros como todo lo que viaja.
///
/// La huella es la de la rampa entera. `dir` es hacia dónde SUBE, con la convención de
/// `Wg3Opening::side`: `0 = N` (+Z), `1 = E` (+X), `2 = S` (−Z), `3 = O` (−X). El suelo va de
/// `bottom_y_cm` en el borde contrario a `dir` hasta `top_y_cm` en el borde de `dir`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Wg3Ramp {
    pub x_cm: i32,
    pub z_cm: i32,
    pub size_x_cm: i32,
    pub size_z_cm: i32,
    pub bottom_y_cm: i32,
    pub top_y_cm: i32,
    pub dir: u8,
    /// Aspecto del espacio al que pertenece, como en `Wg3Segment`: el cliente pinta la cuña con el
    /// suelo de ese papel.
    pub style: u8,
}

impl Wg3Ramp {
    /// Centro de la huella, en metros. Decide de qué chunk es la rampa (como un macizo).
    pub fn centre(&self) -> (f32, f32) {
        (
            metres(self.x_cm) + metres(self.size_x_cm) * 0.5,
            metres(self.z_cm) + metres(self.size_z_cm) * 0.5,
        )
    }

    /// `(min_x, min_z, max_x, max_z)` en metros.
    pub fn bounds(&self) -> (f32, f32, f32, f32) {
        let (x0, z0) = (metres(self.x_cm), metres(self.z_cm));
        (
            x0,
            z0,
            x0 + metres(self.size_x_cm),
            z0 + metres(self.size_z_cm),
        )
    }

    /// Desnivel que salva, en centímetros.
    pub fn rise_cm(&self) -> i32 {
        self.top_y_cm - self.bottom_y_cm
    }

    /// Largo a lo largo de `dir`, en centímetros.
    pub fn along_cm(&self) -> i32 {
        if self.dir.is_multiple_of(2) {
            self.size_z_cm
        } else {
            self.size_x_cm
        }
    }

    /// Lo que se exige antes de emitir una rampa. Vacío = utilizable.
    pub fn problems(&self) -> Vec<String> {
        let mut out = Vec::new();
        if self.size_x_cm <= 0 || self.size_z_cm <= 0 {
            out.push(format!(
                "huella no positiva: {}×{} cm",
                self.size_x_cm, self.size_z_cm
            ));
        }
        if self.dir > 3 {
            out.push(format!("sentido {} fuera de 0..3", self.dir));
        }
        let rise = self.rise_cm();
        if rise <= 0 {
            out.push(format!("no sube: {rise} cm"));
        }
        if rise > RAMP_MAX_RISE_CM {
            out.push(format!(
                "sube {rise} cm, más que el tope de {RAMP_MAX_RISE_CM}"
            ));
        }
        // Pendiente ≤ 1:5, en enteros: rise / along ≤ 10 / 50.
        let along = self.along_cm();
        if along > 0 && rise * RAMP_CELL_CM > RAMP_MAX_RISE_PER_CELL_CM * along {
            out.push(format!(
                "pendiente {rise}/{along} por encima de 1:5 ({RAMP_MAX_RISE_PER_CELL_CM} cm por celda)"
            ));
        }
        out
    }
}

/// ADR-122 D2 — las cajas de colisión de la rampa, desde el extremo BAJO hacia `dir`.
///
/// Una por celda de [`RAMP_CELL_CM`] (la última se lleva el resto), con toda la anchura de la huella,
/// de `bottom_y_cm` a la cota de la rampa en el borde ALTO de la celda redondeada hacia arriba. Así
/// la superficie andable nunca queda por debajo del dibujo liso, y la última caja termina
/// exactamente en `top_y_cm`.
///
/// **El cliente tiene que producir EXACTAMENTE esta lista** (`Wg3RampGeometry.StepBoxes`): es lo que
/// frena a los dos lados, y un centímetro de diferencia es un jugador que flota o que tropieza.
pub fn ramp_step_boxes(r: &Wg3Ramp) -> Vec<Wg3Solid> {
    let along = r.along_cm();
    let rise = r.rise_cm();
    if along <= 0 || rise <= 0 || r.size_x_cm <= 0 || r.size_z_cm <= 0 {
        return Vec::new();
    }
    let cells = (along + RAMP_CELL_CM - 1) / RAMP_CELL_CM;
    (0..cells)
        .map(|k| {
            let a0 = k * RAMP_CELL_CM;
            let a1 = ((k + 1) * RAMP_CELL_CM).min(along);
            let top = r.bottom_y_cm + (rise * a1 + along - 1) / along;
            let (x_cm, z_cm, size_x_cm, size_z_cm) = match r.dir % 4 {
                0 => (r.x_cm, r.z_cm + a0, r.size_x_cm, a1 - a0),
                1 => (r.x_cm + a0, r.z_cm, a1 - a0, r.size_z_cm),
                2 => (r.x_cm, r.z_cm + r.size_z_cm - a1, r.size_x_cm, a1 - a0),
                _ => (r.x_cm + r.size_x_cm - a1, r.z_cm, a1 - a0, r.size_z_cm),
            };
            Wg3Solid {
                x_cm,
                z_cm,
                size_x_cm,
                size_z_cm,
                bottom_y_cm: r.bottom_y_cm,
                top_y_cm: top,
                style: r.style,
                yaw_deg: 0,
                shape: SHAPE_BOX,
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::wg3::plan::MAX_WALK_STEP_CM;
    use crate::world::wg3::raster::Wg3RasterBuilder;

    fn ramp(dir: u8) -> Wg3Ramp {
        // Un hundido de tipo terraza: 60 cm en 320 cm de largo (1:5,3), 300 de ancho.
        let (sx, sz) = if dir.is_multiple_of(2) {
            (300, 320)
        } else {
            (320, 300)
        };
        Wg3Ramp {
            x_cm: 1000,
            z_cm: -450,
            size_x_cm: sx,
            size_z_cm: sz,
            bottom_y_cm: -60,
            top_y_cm: 0,
            dir,
            style: 2,
        }
    }

    #[test]
    fn a_legal_ramp_has_no_problems() {
        for dir in 0..4 {
            assert!(
                ramp(dir).problems().is_empty(),
                "dir {dir}: {:?}",
                ramp(dir).problems()
            );
        }
    }

    #[test]
    fn a_steep_or_tall_ramp_is_a_problem() {
        let mut steep = ramp(0);
        steep.size_z_cm = 250; // 60 en 250: 12 cm por celda
        assert!(!steep.problems().is_empty(), "1:4,2 tiene que rechazarse");

        let mut tall = ramp(0);
        tall.bottom_y_cm = -120;
        tall.size_z_cm = 1000;
        assert!(!tall.problems().is_empty(), "120 cm pasa del tope de 100");
    }

    /// Cada caja sube como mucho 10 cm sobre la anterior, la primera como mucho 10 sobre el
    /// fondo y la última acaba exactamente en lo alto.
    #[test]
    fn boxes_climb_at_most_one_tenth_per_cell_and_end_on_top() {
        for dir in 0..4 {
            let r = ramp(dir);
            let boxes = ramp_step_boxes(&r);
            assert_eq!(
                boxes.len(),
                7,
                "320 cm son 7 celdas de 50 (la última de 20)"
            );
            let mut prev = r.bottom_y_cm;
            for b in &boxes {
                assert!(b.top_y_cm >= prev, "dir {dir}: la rampa no baja nunca");
                assert!(
                    b.top_y_cm - prev <= RAMP_MAX_RISE_PER_CELL_CM,
                    "dir {dir}: escalón de {} cm",
                    b.top_y_cm - prev
                );
                assert_eq!(b.bottom_y_cm, r.bottom_y_cm);
                assert!(b.problems().is_empty(), "dir {dir}: {:?}", b.problems());
                prev = b.top_y_cm;
            }
            assert_eq!(prev, r.top_y_cm, "dir {dir}: la última caja acaba arriba");
        }
    }

    /// Las cajas cubren la huella entera, sin solaparse, y el extremo bajo está donde dice `dir`.
    #[test]
    fn boxes_tile_the_footprint_and_start_low_opposite_dir() {
        for dir in 0..4 {
            let r = ramp(dir);
            let boxes = ramp_step_boxes(&r);
            let area: i64 = boxes
                .iter()
                .map(|b| i64::from(b.size_x_cm) * i64::from(b.size_z_cm))
                .sum();
            assert_eq!(
                area,
                i64::from(r.size_x_cm) * i64::from(r.size_z_cm),
                "dir {dir}"
            );
            for b in &boxes {
                assert!(b.x_cm >= r.x_cm && b.x_cm + b.size_x_cm <= r.x_cm + r.size_x_cm);
                assert!(b.z_cm >= r.z_cm && b.z_cm + b.size_z_cm <= r.z_cm + r.size_z_cm);
            }
            let low = boxes.first().unwrap();
            let high = boxes.last().unwrap();
            let (lx, lz) = low.centre();
            let (hx, hz) = high.centre();
            match dir {
                0 => assert!(hz > lz, "N sube hacia +Z"),
                1 => assert!(hx > lx, "E sube hacia +X"),
                2 => assert!(hz < lz, "S sube hacia −Z"),
                _ => assert!(hx < lx, "O sube hacia −X"),
            }
        }
    }

    /// Lo que importa de verdad: rasterizada con el ráster CONSERVADOR del servidor, se anda. Sin
    /// salto entre celdas vecinas por encima del escalón de la navegación, del fondo a lo alto.
    #[test]
    fn rasterized_ramp_is_walkable_end_to_end() {
        for dir in 0..4 {
            let r = ramp(dir);
            let (min_x, min_z, max_x, max_z) = r.bounds();
            let mut builder = Wg3RasterBuilder::covering(min_x, min_z, max_x, max_z);
            for b in ramp_step_boxes(&r) {
                builder.add_solid(&b);
            }
            let raster = builder.finish();

            let (cx, cz) = r.centre();
            let (dx, dz) = match dir {
                0 => (0.0, 1.0),
                1 => (1.0, 0.0),
                2 => (0.0, -1.0),
                _ => (-1.0, 0.0),
            };
            let half = metres(r.along_cm()) * 0.5;
            let mut prev: Option<f32> = None;
            let mut t = -half + 0.25;
            while t < half {
                let (x, z) = (cx + dx * t, cz + dz * t);
                let floor = raster
                    .floor_below(x, 2.0, z)
                    .unwrap_or_else(|| panic!("dir {dir}: sin suelo en t={t}"));
                if let Some(p) = prev {
                    let step_cm = ((floor - p) * 100.0).abs();
                    assert!(
                        step_cm <= MAX_WALK_STEP_CM as f32,
                        "dir {dir}: salto de {step_cm} cm en t={t}"
                    );
                }
                prev = Some(floor);
                t += 0.5;
            }
            // La última muestra cae a media celda del borde alto: está como mucho una celda por
            // debajo de lo alto, nunca por encima.
            let top = prev.unwrap();
            let below_cm = (metres(r.top_y_cm) - top) * 100.0;
            assert!(
                (-0.5..=RAMP_MAX_RISE_PER_CELL_CM as f32 + 0.5).contains(&below_cm),
                "dir {dir}: la última muestra queda {below_cm} cm por debajo de lo alto"
            );
        }
    }
}
