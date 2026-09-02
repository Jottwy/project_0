//! ADR-095 — el campo de escala: qué TAMAÑO de espacio quiere el mundo en cada punto.
//!
//! Espejo de `Wg3ScaleField`. Lo que importa de este campo, y por lo que sustituye al "historial
//! reciente de piezas" que pedía el documento de diseño, es que es FUNCIÓN PURA DE LA POSICIÓN: dos
//! chunks vecinos coinciden sin hablarse, el contraste está en el mapa y no en el camino, y el mismo
//! sitio se siente igual al volver. Un historial exigiría haber generado la cadena que lleva hasta
//! el chunk (500,300) para poder generarlo, que es justo la propiedad que hace imposible el mundo
//! infinito.
//!
//! Las clases se devuelven como `u8` y no como enum propio a propósito: `Wg3Piece::scale` llega del
//! manifiesto como discriminante, y meter un enum en medio obligaría a una conversión que solo
//! puede fallar de una forma —silenciosa— justo en el dato que decide qué se coloca.

use super::hash;

/// Celda gruesa, en metros: el grano al que el mundo cambia de tamaño.
pub const COARSE_CELL: f32 = 46.0;

/// Celda fina. Desplazada y con otro grano para que la trama de la gruesa no se lea como una
/// cuadrícula — que sería reintroducir por la puerta de atrás justo lo que WG3 viene a quitar.
pub const FINE_CELL: f32 = 29.0;

const SALT_COARSE: u32 = 0x5CA1_E000;
const SALT_FINE: u32 = 0x5CA1_E001;

pub const SCALE_NARROW: u8 = 0;
pub const SCALE_MEDIUM: u8 = 1;
pub const SCALE_LARGE: u8 = 2;
pub const SCALE_WEIRD: u8 = 3;

/// Valor crudo del campo en `[0,1)`.
pub fn value_at(world_seed: i32, x: f32, z: f32) -> f32 {
    let coarse = cell(world_seed, x, z, COARSE_CELL, 0.0, 0.0, SALT_COARSE);
    let fine = cell(world_seed, x, z, FINE_CELL, 23.0, 17.0, SALT_FINE);
    coarse * 0.66 + fine * 0.34
}

/// Umbrales de clase, en el valor crudo del campo. **Espejo exacto de `Wg3ScaleField.ScaleAt`.**
///
/// # Por que estos numeros y no los de antes (ADR-119 D1)
///
/// El campo no es uniforme: `value_at` suma dos uniformes con pesos 0,66 y 0,34, asi que su
/// densidad es un trapecio con la meseta en `[0,34 , 0,66]` y las colas flacas. Con los umbrales de
/// ADR-095 —0,34 / 0,70 / 0,92— eso repartia el mundo en **estrecho 25,8 %, medio 54,2 %, grande
/// 18,6 % y raro 1,4 %**, medido sobre tres millones de muestras.
///
/// **El 1,4 % es el numero que importaba.** Todas las perillas liminales de ADR-118 D3 —los
/// corredores ciegos, las bandas ensanchadas, el vacio extra, `WEIRD_SPREAD`— se disparan en zona
/// `Weird`, o sea que la rareza del mundo entero vivia en una centesima parte de el. Y el reparto de
/// TAMANOS no lo decide el area de cada clase sino `area / objetivo`: una zona estrecha produce seis
/// veces mas hojas por metro cuadrado que una grande, asi que un 18,6 % de superficie `Large` daba
/// un 5 % de los espacios y el 93,3 % del mundo medido caia por debajo de 300 m2.
///
/// Los de aqui son los cuantiles 0,16 / 0,58 / 0,91 de ese mismo trapecio, redondeados: **estrecho
/// 16,2 %, medio 41,4 %, grande 33,5 %, raro 8,9 %**. No se tocan ni los pesos ni el tamano de
/// celda —eso moveria la TRAMA, que es lo que impide que el campo se lea como cuadricula—, solo
/// donde se corta.
const CLASS_NARROW_BELOW: f32 = 0.27;
const CLASS_MEDIUM_BELOW: f32 = 0.55;
const CLASS_LARGE_BELOW: f32 = 0.80;

/// Clase de escala que el mundo pide en ese punto.
pub fn scale_at(world_seed: i32, x: f32, z: f32) -> u8 {
    let v = value_at(world_seed, x, z);
    if v < CLASS_NARROW_BELOW {
        SCALE_NARROW
    } else if v < CLASS_MEDIUM_BELOW {
        SCALE_MEDIUM
    } else if v < CLASS_LARGE_BELOW {
        SCALE_LARGE
    } else {
        SCALE_WEIRD
    }
}

/// Ruido de celda, vecino más próximo. Escalonado a propósito: el mundo cambia de escala al cruzar
/// una frontera, no derivando poco a poco. Un gradiente suave se lee como terreno, y el terreno es
/// lo contrario de lo liminal.
fn cell(world_seed: i32, x: f32, z: f32, size: f32, off_x: f32, off_z: f32, salt: u32) -> f32 {
    let cx = floor_div(x + off_x, size);
    let cz = floor_div(z + off_z, size);
    hash::to_unit(hash::mix(world_seed, cx, cz, salt as i32))
}

/// División con suelo. `(int)(v / size)` trunca hacia cero, así que −1 y +1 caerían en la misma
/// celda y el campo saldría espejado en el origen — el mismo fallo que obligó a usar `div_euclid` al
/// tallar salas ancladas en el chunk vecino, y la razón de que el oráculo incluya una semilla
/// negativa.
///
/// Se copia la forma de C# en vez de usar `f32::floor` porque son la misma función solo mientras el
/// cociente quepa en un `i32`; fuera de ahí las dos están igual de rotas, y prefiero que estén rotas
/// igual.
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

    /// Los puntos del espejo. Cubren el origen, la frontera EXACTA de una celda gruesa (46 m), un
    /// punto lejano y dos negativos: es donde una división truncada en vez de con suelo produce otro
    /// mundo, y es un fallo que este proyecto ya ha pagado dos veces.
    const GOLDEN_POINTS: [(f32, f32); 10] = [
        (0.0, 0.0),
        (75.0, 75.0),
        (-45.9, 12.5),
        (-46.0, 12.5),
        (1234.5, -678.9),
        (-4425.0, -4425.0),
        (13575.0, -8925.0),
        (19.0, -19.0),
        // Uno de cada clase que los ocho de arriba no tocaban: las cuatro ramas de `scale_at` tienen
        // que estar cubiertas o los umbrales de ADR-119 D1 se pueden mover sin que nada se ponga rojo.
        (-3700.0, -530.0),
        (-3404.0, -530.0),
    ];

    /// Rellenados con la primera ejecución de la sonda de abajo, y a partir de ahí, ley.
    ///
    /// **`(bits, clase)` y no sólo la clase**, y ésa es la mitad del valor: los bits atan el CAMPO
    /// —pesos, tamaño de celda, `floor_div`, el hash— y la clase ata los UMBRALES. Con sólo la clase,
    /// mover un peso y compensar con un umbral pasaría el test y produciría otro mundo.
    const GOLDENS: &[(f32, f32, u32, u8)] = &[
        (0.0, 0.0, 0x3f14_ea4f, SCALE_LARGE),
        (75.0, 75.0, 0x3ecd_568c, SCALE_MEDIUM),
        // Frontera EXACTA de celda gruesa: −45,9 y −46,0 caen en la misma celda −1 (46 m) porque
        // −46/46 = −1 justo; una división truncada las separaría.
        (-45.9, 12.5, 0x3f3f_b848, SCALE_LARGE),
        (-46.0, 12.5, 0x3f3f_b848, SCALE_LARGE),
        (1234.5, -678.9, 0x3f14_a37e, SCALE_LARGE),
        (-4425.0, -4425.0, 0x3eb3_b176, SCALE_MEDIUM),
        (13575.0, -8925.0, 0x3f04_d6b7, SCALE_MEDIUM),
        (19.0, -19.0, 0x3f3e_a6f8, SCALE_LARGE),
        (-3700.0, -530.0, 0x3e2f_2a5c, SCALE_NARROW),
        (-3404.0, -530.0, 0x3f4e_f014, SCALE_WEIRD),
    ];

    /// Los mismos números que afirma `Wg3ScaleFieldTests` en C#. Si uno de los dos lados se mueve,
    /// su test se pone rojo: es la técnica de `the_density_mirror_golden_values`, y aquí hacía más
    /// falta que allí — **el campo de escala lleva desde ADR-095 escrito dos veces sin un solo valor
    /// atado entre los dos idiomas**, y ADR-119 D1 acaba de moverlo.
    #[test]
    fn the_scale_mirror_golden_values() {
        for (x, z) in GOLDEN_POINTS {
            let v = value_at(SEED, x, z);
            println!(
                "[scale-golden] ({x}, {z}) -> {:#010x} = {v} clase {}",
                v.to_bits(),
                scale_at(SEED, x, z)
            );
        }
        for &(x, z, bits, class) in GOLDENS {
            let v = value_at(SEED, x, z);
            assert_eq!(
                v.to_bits(),
                bits,
                "({x},{z}) vale {v} = {:#010x}, no {bits:#010x}",
                v.to_bits()
            );
            assert_eq!(scale_at(SEED, x, z), class, "({x},{z}) clase");
        }
    }

    /// El REPARTO del campo, que es lo que ADR-119 D1 vino a mover y lo que ningún test miraba.
    ///
    /// Sobre una malla de puntos separados por primos —para no caer siempre en la misma celda de
    /// 46 ni de 29 m—: `Narrow` ~16 %, `Medium` ~41 %, `Large` ~34 %, `Weird` ~9 %. Las bandas son
    /// anchas a propósito: lo que se vigila es que nadie devuelva el mundo al 1,4 % de rareza de
    /// antes, no el segundo decimal.
    #[test]
    fn the_field_spreads_the_four_classes() {
        let mut n = [0usize; 4];
        for i in 0..160 {
            for j in 0..160 {
                let x = i as f32 * 37.0 - 2960.0;
                let z = j as f32 * 53.0 - 4240.0;
                n[scale_at(SEED, x, z) as usize] += 1;
            }
        }
        let total = 160.0 * 160.0;
        let pc = |k: usize| n[k] as f32 * 100.0 / total;
        println!(
            "[scale-spread] narrow {:.1} % medium {:.1} % large {:.1} % weird {:.1} %",
            pc(0),
            pc(1),
            pc(2),
            pc(3)
        );
        assert!((11.0..22.0).contains(&pc(0)), "narrow {:.1} %", pc(0));
        assert!((35.0..48.0).contains(&pc(1)), "medium {:.1} %", pc(1));
        assert!((28.0..40.0).contains(&pc(2)), "large {:.1} %", pc(2));
        assert!((5.0..14.0).contains(&pc(3)), "weird {:.1} %", pc(3));
    }
}
