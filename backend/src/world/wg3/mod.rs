//! ADR-095 — WorldGen3 en el backend.
//!
//! El mundo deja de estamparse en celdas y pasa a componerse de PIEZAS autoradas. Este módulo es la
//! mitad de servidor: lee el manifiesto que hornea Unity, coloca piezas y rasteriza su chuleta de
//! colisión. No dibuja, no deriva geometría y no sabe qué es una malla (R1).
//!
//! Convive con WG2 tras bandera hasta el borrado (R4 y D3 del ADR): nada de aquí toca un solo
//! fichero de `grid_gen`.

pub mod chunk;
pub mod config;
pub mod demo;
pub mod manifest;
pub mod placement;
pub mod raster;

#[cfg(test)]
mod tests;
