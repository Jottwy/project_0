//! ADR-095 — el hash determinista de WG3, espejo EXACTO de `Wg3Hash` en C#.
//!
//! Es duplicación consciente por partida doble (R4): WG3 no referencia a `grid_gen`, lo copia, para
//! que borrar WG2 sea borrar ficheros y no desenredar dependencias. Aquí se duplica una vez más,
//! ahora entre idiomas, porque Unity autora el mundo y Rust lo compone: si los dos hashes se
//! separan, el cliente dibuja un mundo y el servidor colisiona otro, y nada da error.
//!
//! REGLA R3 — NO HAY RNG COMPARTIDO. Cada decisión abre su propio flujo a partir de la POSICIÓN en
//! el mundo, nunca del índice ni del orden de proceso. Es lo que permitirá que dos chunks vecinos
//! lleguen a la misma respuesta sin hablarse cuando se cierre el troceado.
//!
//! CADA OPERACIÓN ESTÁ ESCRITA PARA COINCIDIR CON C#, no para ser bonita:
//!  · `Quantize` usa `Math.Round(double)`, que redondea a la PAR en los empates, no al alza — de ahí
//!    `round_ties_even` y no `round`;
//!  · `ToUnit` convierte el entero a `float` ANTES de multiplicar, así que pierde precisión a
//!    propósito; hacerlo en `f64` daría otros mundos.

/// Cuantización de coordenadas de mundo a enteros para sembrar por posición. 4 pasos por metro: por
/// debajo de 25 cm dos sockets distintos no pueden coexistir, así que no hay colisión posible.
pub const POSITION_QUANTUM: f32 = 4.0;

/// `(int)Math.Round(v * PositionQuantum)`.
///
/// El producto se hace en `f32` —como en C#— y el redondeo en `f64`, porque `Math.Round(double)`
/// recibe el `float` ya ensanchado. Empates a la par: `Math.Round` usa `MidpointRounding.ToEven` por
/// defecto y `f32::round` en Rust redondea alejándose del cero, que no es lo mismo.
pub fn quantize(v: f32) -> i32 {
    ((v * POSITION_QUANTUM) as f64).round_ties_even() as i32
}

/// Mezcla de cuatro enteros a 64 bits. Mismo esqueleto que `CeilingHash` en el cliente.
pub fn mix(a: i32, b: i32, c: i32, d: i32) -> u64 {
    let mut h = 0x9E37_79B9_7F4A_7C15u64;
    h ^= (a as u32 as u64).wrapping_mul(0xFF51_AFD7_ED55_8CCD);
    h ^= h >> 33;
    h ^= (b as u32 as u64).wrapping_mul(0xC4CE_B9FE_1A85_EC53);
    h ^= h >> 29;
    h ^= (c as u32 as u64).wrapping_mul(0x1656_67B1_9E37_79F9);
    h ^= h >> 32;
    h ^= (d as u32 as u64).wrapping_mul(0x27D4_EB2F_1656_67C5);
    h ^= h >> 30;
    h = h.wrapping_mul(0x9E37_79B1_85EB_CA87);
    h ^= h >> 32;
    h
}

/// Hash de una posición de mundo más una sal de propósito. La sal separa decisiones que ocurren en
/// el MISMO punto (qué pieza, si taponar) para que no queden correlacionadas.
pub fn at_position(world_seed: i32, x: f32, z: f32, salt: u32) -> u64 {
    mix(world_seed, quantize(x), quantize(z), salt as i32)
}

/// Flotante en `[0,1)` a partir de un hash ya mezclado.
///
/// La conversión a `f32` va ANTES del producto, igual que en C#. Es una pérdida de precisión
/// deliberada: cambiarla por `f64` mueve el sorteo y produce otro mundo con la misma semilla.
pub fn to_unit(h: u64) -> f32 {
    (h >> 11) as f32 * (1.0 / 9_007_199_254_740_992.0f32)
}

/// Flujo determinista de flotantes. NO es un RNG global: se abre uno por decisión, sembrado por
/// posición, y muere ahí mismo.
#[derive(Debug, Clone, Copy)]
pub struct Stream {
    state: u64,
}

impl Stream {
    pub fn new(seed: u64) -> Self {
        // Un estado a cero deja splitmix64 produciendo la misma secuencia degenerada.
        Self {
            state: if seed == 0 {
                0x9E37_79B9_7F4A_7C15
            } else {
                seed
            },
        }
    }

    /// splitmix64, el mismo avance que usa el mixer de arriba al cerrar.
    pub fn next_raw(&mut self) -> u64 {
        self.state = self.state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    pub fn next01(&mut self) -> f32 {
        to_unit(self.next_raw())
    }
}

pub fn stream_at(world_seed: i32, x: f32, z: f32, salt: u32) -> Stream {
    Stream::new(at_position(world_seed, x, z, salt))
}
