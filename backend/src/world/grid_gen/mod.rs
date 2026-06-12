//! Grid-based world generation (Fase 1 — módulo aislado).
//!
//! Reemplaza el sistema volumétrico (BandHeightSpec / ChunkRenderer) con una
//! rejilla regular de celdas de 2.5 m. Este módulo no importa nada del resto
//! de `world`; la integración con IPC y el loop de juego llega en Fase 4.
//!
//! Invariantes del sistema (§9 del documento de diseño):
//! 1. Rejilla uniforme de 2.5 m — nunca celdas de tamaño distinto en el mismo grid.
//! 2. El contrato de `cell_type` es sagrado — cambiar un valor rompe Unity.
//! 3. Rust es autoridad de colisión; Unity solo renderiza.
//! 4. Generación 100 % determinista — misma seed → mismo mundo.
//! 5. `ceiling_height` nunca supera `MAX_CEILING_UNITS`; capas limpias sin desbordamiento.

mod cell;
mod generator;
mod layer_rules;
mod stitching;

#[cfg(test)]
mod tests;

pub use cell::*;
pub use generator::*;
pub use layer_rules::*;
pub use stitching::*;
