//! Auditoría 2026-09-02, Fase 6 — **EL CAMPO DE DENSIDAD AMBIENTAL**: cuánto contenido pide cada
//! sitio, decidido por la posición y no por quien lo vista.
//!
//! # Qué contesta
//!
//! `vacío → disperso → estructurado → denso → anómalo`. Es la escala de contraste que se pidió: hay
//! zonas casi vacías y zonas con cables, cajas, humedad y señales, y **el vacío también es
//! contenido** — por eso es la clase más probable y no la que sobra.
//!
//! # Por qué un campo, y no una tirada por objeto
//!
//! Es el mismo argumento que el campo de escala (`scale.rs`): función pura de la posición, así que
//! dos chunks vecinos coinciden sin hablarse, el contraste está en el mapa y no en el camino, y el
//! mismo sitio se siente igual al volver. Y con el mismo grano escalonado: **el mundo cambia de
//! densidad al cruzar una frontera, no derivando**. La celda gruesa mide del orden de una sala
//! grande para que una sala se lea entera como «vacía» o «llena»; una tirada por objeto daría un
//! ruido uniforme que se lee como generador, no como abandono.
//!
//! # Qué NO hace, a propósito
//!
//! No coloca nada. Ningún consumidor lo lee todavía (regla 7 de la auditoría: sin entidades). Es
//! espejo EXACTO de `Wg3DensityField` en C#, para que el día que el cliente vista un tramo con
//! props lea lo mismo que el servidor cuando decida loot o cordura por densidad — cero wire.

use super::hash;
use super::scale;

/// Celda gruesa, en metros: del orden de una sala grande, para que una sala sea de una clase.
pub const DENSITY_COARSE_CELL: f32 = 38.0;
/// Celda fina, desplazada y con otro grano, para que la gruesa no se lea como cuadrícula.
pub const DENSITY_FINE_CELL: f32 = 13.0;

const SALT_COARSE: u32 = 0xDE45_0000;
const SALT_FINE: u32 = 0xDE45_0001;

pub const DENSITY_EMPTY: u8 = 0;
pub const DENSITY_SPARSE: u8 = 1;
pub const DENSITY_STRUCTURED: u8 = 2;
pub const DENSITY_DENSE: u8 = 3;
pub const DENSITY_ANOMALOUS: u8 = 4;

/// Valor crudo del campo en `[0,1)`. Espejo de `Wg3DensityField.ValueAt`.
pub fn value_at(world_seed: i32, x: f32, z: f32) -> f32 {
    let coarse = cell(world_seed, x, z, DENSITY_COARSE_CELL, 0.0, 0.0, SALT_COARSE);
    let fine = cell(world_seed, x, z, DENSITY_FINE_CELL, 11.0, 7.0, SALT_FINE);
    coarse * 0.70 + fine * 0.30
}

/// Clase de densidad por posición. **Los umbrales son el reparto**, y el reparto es la decisión:
/// un tercio vacío, un tercio disperso, un cuarto estructurado, y lo denso y lo anómalo son la
/// excepción que hace que lo demás se lea como abandono y no como decorado.
pub fn class_at(world_seed: i32, x: f32, z: f32) -> u8 {
    let v = value_at(world_seed, x, z);
    if v < 0.32 {
        DENSITY_EMPTY
    } else if v < 0.62 {
        DENSITY_SPARSE
    } else if v < 0.86 {
        DENSITY_STRUCTURED
    } else if v < 0.95 {
        DENSITY_DENSE
    } else {
        DENSITY_ANOMALOUS
    }
}

/// La clase que le toca a un ESPACIO, cruzada con su clase de escala: en zona `Weird` todo lo que
/// no es vacío sube un escalón. Un vacío en zona rara es liminal y se queda; una sala normal en
/// zona rara es donde tocan las cosas fuera de sitio.
pub fn class_for_space(world_seed: i32, x: f32, z: f32, scale_class: u8) -> u8 {
    let c = class_at(world_seed, x, z);
    if scale_class == scale::SCALE_WEIRD && c > DENSITY_EMPTY && c < DENSITY_ANOMALOUS {
        c + 1
    } else {
        c
    }
}

pub fn class_name(class: u8) -> &'static str {
    match class {
        DENSITY_EMPTY => "empty",
        DENSITY_SPARSE => "sparse",
        DENSITY_STRUCTURED => "structured",
        DENSITY_DENSE => "dense",
        _ => "anomalous",
    }
}

/// ADR-155 D1 (enm. 1) — **el campo del BIOMA LABERINTO**. Celda gruesa de dos regiones por lado: lo
/// que hace que la zona sea una MEGAzona y no manchas sueltas.
pub const MAZE_ZONE_COARSE_CELL: f32 = 300.0;
/// Celda fina de un cuarto de región (150 m), alineada con la rejilla de regiones: recorta el borde
/// de la megazona por cuartos, que es la unidad que el plan puede decidir en sus primeros cortes.
pub const MAZE_ZONE_FINE_CELL: f32 = 75.0;
/// Umbral del valor mezclado `0,75·gruesa + 0,25·fina`. La suma de dos uniformes no es uniforme: su
/// distribución es un trapecio, y en el tramo central `P(v < t) = (t − 0,125) / 0,75`. Para el 40 %
/// que pidió Joel, `t = 0,425`. Cuenta hecha, no perilla: el test mide el reparto.
pub const MAZE_ZONE_THRESHOLD: f32 = 0.425;

const SALT_MAZE_COARSE: u32 = 0xDE45_0155;
const SALT_MAZE_FINE: u32 = 0xDE45_0156;

/// ¿Cae este punto (metros de PLAN) en zona laberinto? Función pura de la posición, como los demás
/// campos: dos regiones vecinas coinciden sin hablarse.
pub fn in_maze_zone(world_seed: i32, x: f32, z: f32) -> bool {
    let coarse = cell(
        world_seed,
        x,
        z,
        MAZE_ZONE_COARSE_CELL,
        0.0,
        0.0,
        SALT_MAZE_COARSE,
    );
    let fine = cell(
        world_seed,
        x,
        z,
        MAZE_ZONE_FINE_CELL,
        0.0,
        0.0,
        SALT_MAZE_FINE,
    );
    coarse * 0.75 + fine * 0.25 < MAZE_ZONE_THRESHOLD
}

/// Ruido de celda, vecino más próximo. Copia de `scale::cell` a propósito (R4): dos campos que
/// comparten función se mueven juntos el día que uno cambie, y el de escala tiene un oráculo.
fn cell(world_seed: i32, x: f32, z: f32, size: f32, off_x: f32, off_z: f32, salt: u32) -> f32 {
    let cx = floor_div(x + off_x, size);
    let cz = floor_div(z + off_z, size);
    hash::to_unit(hash::mix(world_seed, cx, cz, salt as i32))
}

/// La misma división con suelo que `scale::floor_div`, y por lo mismo: la forma de C#.
fn floor_div(v: f32, size: f32) -> i32 {
    let q = v / size;
    let i = q as i32;
    if q < 0.0 && q != i as f32 {
        i - 1
    } else {
        i
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SEED: i32 = 42;

    /// Un punto por cada 5 m en ±1 km: 160 000 muestras, de sobra para leer el reparto.
    fn histogram(seed: i32) -> [usize; 5] {
        let mut h = [0usize; 5];
        let mut x = -1000.0f32;
        while x < 1000.0 {
            let mut z = -1000.0f32;
            while z < 1000.0 {
                h[class_at(seed, x, z) as usize] += 1;
                z += 5.0;
            }
            x += 5.0;
        }
        h
    }

    #[test]
    fn the_density_field_is_pure_position() {
        for &(x, z) in &[(0.0f32, 0.0f32), (17.3, -211.9), (-999.5, 4321.0)] {
            assert_eq!(class_at(SEED, x, z), class_at(SEED, x, z));
            assert_eq!(value_at(SEED, x, z), value_at(SEED, x, z));
        }
        // Y la semilla lo mueve: dos mundos no se parecen.
        let mut differ = 0;
        for i in 0..200 {
            let (x, z) = (i as f32 * 37.0, i as f32 * -23.0);
            if class_at(SEED, x, z) != class_at(SEED + 1, x, z) {
                differ += 1;
            }
        }
        assert!(
            differ > 100,
            "sólo {differ} de 200 puntos cambian de clase con la semilla"
        );
    }

    /// El reparto es la decisión: se afirma con una banda de cuatro puntos por clase.
    #[test]
    fn the_density_classes_come_out_in_the_designed_proportions() {
        let h = histogram(SEED);
        let n = h.iter().sum::<usize>() as f32;
        let pct: Vec<f32> = h.iter().map(|c| *c as f32 * 100.0 / n).collect();
        println!(
            "[density] vacío {:.1} % · disperso {:.1} % · estructurado {:.1} % · denso {:.1} % · \
             anómalo {:.1} %",
            pct[0], pct[1], pct[2], pct[3], pct[4]
        );
        // Con dos ruidos sumados (0,7 + 0,3) el valor no es uniforme: se concentra en el medio, así
        // que los extremos pesan menos que sus umbrales y el centro más. Lo que se afirma es el
        // ORDEN y la banda, que es lo que se ve andando.
        assert!(pct[0] > 20.0 && pct[0] < 40.0, "vacío {:.1} %", pct[0]);
        assert!(pct[1] > 25.0 && pct[1] < 45.0, "disperso {:.1} %", pct[1]);
        assert!(
            pct[2] > 18.0 && pct[2] < 35.0,
            "estructurado {:.1} %",
            pct[2]
        );
        assert!(pct[3] > 3.0 && pct[3] < 14.0, "denso {:.1} %", pct[3]);
        assert!(pct[4] > 0.5 && pct[4] < 6.0, "anómalo {:.1} %", pct[4]);
    }

    /// Escalonado por celda, no por metro: andando en línea recta la clase aguanta del orden de
    /// una celda fina antes de cambiar.
    #[test]
    fn the_field_changes_by_cell_not_by_metre() {
        let mut runs = Vec::new();
        for line in 0..20 {
            let z = line as f32 * 71.0 - 700.0;
            let mut last = class_at(SEED, -1000.0, z);
            let mut run = 1usize;
            let mut x = -999.0f32;
            while x < 1000.0 {
                let c = class_at(SEED, x, z);
                if c == last {
                    run += 1;
                } else {
                    runs.push(run);
                    run = 1;
                    last = c;
                }
                x += 1.0;
            }
            runs.push(run);
        }
        let mean = runs.iter().sum::<usize>() as f32 / runs.len() as f32;
        println!(
            "[density] tramo medio de una clase: {mean:.1} m ({} tramos)",
            runs.len()
        );
        assert!(
            mean >= 8.0,
            "la densidad cambia cada {mean:.1} m: eso es ruido, no zonas"
        );
    }

    /// ADR-155 D6 — la zona laberinto cubre ≈ 40 % del plano (diez semillas, ±3 km a paso de 10 m) y
    /// va por megazonas: andando en línea recta, dentro o fuera aguanta del orden de una región.
    #[test]
    fn the_maze_zone_is_forty_percent_in_megazones() {
        let (mut inside, mut total) = (0usize, 0usize);
        let mut runs = Vec::new();
        for seed in 0..10 {
            let mut z = -3000.0f32;
            while z < 3000.0 {
                let mut last = in_maze_zone(seed, -3000.0, z);
                let mut run = 0usize;
                let mut x = -3000.0f32;
                while x < 3000.0 {
                    let here = in_maze_zone(seed, x, z);
                    inside += here as usize;
                    total += 1;
                    if here == last {
                        run += 10;
                    } else {
                        runs.push(run);
                        run = 10;
                        last = here;
                    }
                    x += 10.0;
                }
                runs.push(run);
                z += 150.0;
            }
        }
        let pct = inside as f32 * 100.0 / total as f32;
        let mean = runs.iter().sum::<usize>() as f32 / runs.len() as f32;
        println!("[maze-zone] {pct:.1} % del plano en zona; tramo medio {mean:.0} m");
        assert!((36.0..44.0).contains(&pct), "zona laberinto {pct:.1} %");
        assert!(
            mean >= 150.0,
            "la zona cambia cada {mean:.0} m: eso no es una megazona"
        );
    }

    /// En zona `Weird` todo sube un escalón salvo el vacío y lo ya anómalo.
    #[test]
    fn weird_scale_promotes_everything_but_the_void() {
        for i in 0..500 {
            let (x, z) = (i as f32 * 13.7, i as f32 * 5.3 - 900.0);
            let base = class_at(SEED, x, z);
            let weird = class_for_space(SEED, x, z, scale::SCALE_WEIRD);
            let normal = class_for_space(SEED, x, z, scale::SCALE_MEDIUM);
            assert_eq!(normal, base);
            if base == DENSITY_EMPTY || base == DENSITY_ANOMALOUS {
                assert_eq!(weird, base);
            } else {
                assert_eq!(weird, base + 1);
            }
        }
    }

    /// Los mismos números que afirma `Wg3DensityFieldTests` en C#. Si uno de los dos lados se
    /// mueve, su test se pone rojo: es la técnica de `the_identity_mirror_golden_values`.
    #[test]
    fn the_density_mirror_golden_values() {
        let expect = |x: f32, z: f32, bits: u32, class: u8| {
            let v = value_at(SEED, x, z);
            assert_eq!(
                v.to_bits(),
                bits,
                "({x},{z}) vale {v} = {:#010x}, no {bits:#010x}",
                v.to_bits()
            );
            assert_eq!(
                class_at(SEED, x, z),
                class,
                "({x},{z}) clase {}",
                class_at(SEED, x, z)
            );
        };
        for (x, z) in GOLDEN_POINTS {
            let v = value_at(SEED, x, z);
            println!(
                "[density-golden] ({x}, {z}) -> {:#010x} = {v} clase {}",
                v.to_bits(),
                class_at(SEED, x, z)
            );
        }
        for &(x, z, bits, class) in GOLDENS {
            expect(x, z, bits, class);
        }
    }

    const GOLDEN_POINTS: [(f32, f32); 8] = [
        (0.0, 0.0),
        (75.0, 75.0),
        (-37.9, 12.5),
        (-38.0, 12.5),
        (1234.5, -678.9),
        (-4425.0, -4425.0),
        (13575.0, -8925.0),
        (19.0, -19.0),
    ];

    /// Rellenados con la primera ejecución de la sonda de arriba, y a partir de ahí, ley.
    const GOLDENS: &[(f32, f32, u32, u8)] = &[
        (0.0, 0.0, 0x3e80_0054, DENSITY_EMPTY),
        (75.0, 75.0, 0x3eea_776a, DENSITY_SPARSE),
        // Frontera EXACTA de celda gruesa: −37,9 y −38,0 caen en la misma celda −1 (38 m) porque
        // −38/38 = −1 justo; una división truncada las separaría.
        (-37.9, 12.5, 0x3f0d_6fdc, DENSITY_SPARSE),
        (-38.0, 12.5, 0x3f0d_6fdc, DENSITY_SPARSE),
        (1234.5, -678.9, 0x3e8f_9552, DENSITY_EMPTY),
        (-4425.0, -4425.0, 0x3f2b_8c8a, DENSITY_STRUCTURED),
        (13575.0, -8925.0, 0x3e9c_dc26, DENSITY_EMPTY),
        (19.0, -19.0, 0x3ed9_5e1e, DENSITY_SPARSE),
    ];
}
