//! ADR-095 — WorldGen3 en el backend.
//!
//! El mundo deja de estamparse en celdas y pasa a componerse de PIEZAS autoradas. Este módulo es la
//! mitad de servidor: lee el manifiesto que hornea Unity, coloca piezas y rasteriza su chuleta de
//! colisión. No dibuja, no deriva geometría y no sabe qué es una malla (R1).
//!
//! Convive con WG2 tras bandera hasta el borrado (R4 y D3 del ADR): nada de aquí toca un solo
//! fichero de `grid_gen`.

pub mod chunk;
pub mod compose;
pub mod config;
/// ADR-100 — el relleno: convertir un plan en geometría, sin que la geometría decida nada.
pub mod fill;
pub mod hash;
pub mod junction;
pub mod manifest;
pub mod placement;
/// ADR-100 — el plan de región: qué edificio hay aquí, decidido ANTES de colocar una pieza.
pub mod plan;
pub mod raster;
pub mod route;
pub mod scale;
pub mod segment;
pub mod world;

#[cfg(test)]
mod tests;
