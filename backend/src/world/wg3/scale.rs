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

/// Clase de escala que el mundo pide en ese punto.
pub fn scale_at(world_seed: i32, x: f32, z: f32) -> u8 {
    let v = value_at(world_seed, x, z);
    if v < 0.34 {
        SCALE_NARROW
    } else if v < 0.70 {
        SCALE_MEDIUM
    } else if v < 0.92 {
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
