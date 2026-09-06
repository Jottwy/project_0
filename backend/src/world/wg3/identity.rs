//! ADR-103 — la identidad de nivel. De este módulo sobrevive SÓLO el tamaño de su celda.
//!
//! Lo que había aquí —`LevelAnchor` con sus cuatro anclas, `LevelProfile`, `ANCHOR_PROFILES`,
//! `LevelMix`, `at`, `for_region`, `nearest_anchor`, `max_band_width_mul`— era el campo de mezcla
//! de perfiles de ADR-103 D1–D7, escrito entero y **sin un solo consumidor de producción**. Su
//! propia cabecera lo decía: «hoy nadie consume la respuesta». Se borró el 2026-09-05 en el bloque
//! B4 del saneamiento, con la decisión de ADR-103 intacta en el registro y el código recuperable
//! del historial: lo que se retira es el andamio, no la decisión.
//!
//! Tres cosas que hay que saber si alguien lo reescribe:
//!
//! 1. **`GATE_CLEARANCE_CM` tiene que calcularse con el MÁXIMO `band_width_mul` de todos los
//!    perfiles, nunca con el local.** Dos regiones vecinas de identidad distinta comparten las
//!    puertas de su junta (ADR-096); si cada lado despeja según su propia banda, un corte cae
//!    encima de una puerta y la región nace sellada por ese lado. Ya pasó: **64 de 256 puertas
//!    perdidas** (ADR-100 enmienda 1). El módulo tenía una función entera, `max_band_width_mul`,
//!    para esto — y ningún llamador.
//! 2. **El perfil de Level 0 tiene que ser exactamente neutro** (todos los factores a 1,0): es la
//!    guardia de regresión de D5, no pereza. Mientras nadie lea la identidad, el mundo servido es
//!    el de ayer byte a byte.
//! 3. **La semilla es la GLOBAL del compositor, no la de la región.** Una celda cubre 6×6 regiones
//!    y todas tienen que leer lo mismo, por el mismo motivo por el que las puertas de junta
//!    sortean con la del mundo (ADR-096, `region_settings`).
//!
//! El espejo C# (`Wg3Identity`) se conserva y ahora no tiene contraparte en Rust: anotado como
//! deuda, no como sistema.

use super::world;

/// Lado de la celda de identidad, en regiones. MÚLTIPLO EXACTO de la región (D4): una región nunca
/// cae a caballo de dos celdas, así que la frontera de identidad es siempre frontera de región —
/// un sitio donde el mundo ya cambia de mano y donde ya hay contrato.
pub const IDENTITY_CELL_REGIONS: i32 = 6;

/// Lado de la celda de identidad en metros: 6 × 150 = 900. Varios minutos andando dentro de una
/// misma identidad. El número no está defendido por nada más que el orden de magnitud (D4): si al
/// medir se lee como colcha de retales, sube.
///
/// Consumidor vivo: `world::spawn_distribution` (`spawn_distribution.rs:29`), que reparte los
/// spawns una celda de identidad por jugador.
pub const IDENTITY_CELL_M: f32 = IDENTITY_CELL_REGIONS as f32 * world::REGION_M;
