//! ADR-095 — WorldGen3 en el backend.
//!
//! El mundo deja de estamparse en celdas y pasa a componerse de PIEZAS autoradas. Este módulo es la
//! mitad de servidor: lee el manifiesto que hornea Unity, coloca piezas y rasteriza su chuleta de
//! colisión. No dibuja, no deriva geometría y no sabe qué es una malla (R1).
//!
//! Convive con WG2 tras bandera hasta el borrado (R4 y D3 del ADR): nada de aquí toca un solo
//! fichero de `grid_gen`.

pub mod chunk;
/// ADR-106 — la fuente de colisión de WG3.
pub mod collision;
pub mod compose;
pub mod config;
/// Auditoria 2026-09-02, Fase 6 - el campo de densidad ambiental (vacio, disperso, estructurado,
/// denso, anomalo), funcion pura de la posicion y espejo de Wg3DensityField en C#.
pub mod density;
/// ADR-100 — el relleno: convertir un plan en geometría, sin que la geometría decida nada.
pub mod fill;
pub mod hash;
/// ADR-103 — la identidad de nivel: mezcla de perfiles por celda, función pura de la posición.
pub mod identity;
pub mod junction;
/// Metricas de layout (space syntax): isovista, occlusivity, jaggedness, clustering de VGA, drift y
/// entropia del reparto de tiles. Solo lectura sobre el mundo servido.
pub mod layout_metrics;
pub mod manifest;
/// ADR-108 — la navegacion de WG3.
pub mod nav;
pub mod placement;
/// ADR-100 — el plan de región: qué edificio hay aquí, decidido ANTES de colocar una pieza.
pub mod plan;
pub mod raster;
pub mod route;
pub mod scale;
pub mod segment;
/// Resumen del lote de metricas de layout (media, mediana, desviacion, p10, p90 por chunk). Solo
/// lee el CSV que escribe `layout_metrics`.
pub mod summarize_metrics;
/// Auditoría 2026-09-02 — la validación por niveles: plan, edificio, relleno, geometría, ráster,
/// navegación y determinismo, para cualquier semilla y cualquier región.
pub mod validate;
/// ADR-140 — grafo de visibilidad por salas (PVS), construido y probado pero **sin conectar**: ver
/// el encabezado del módulo para qué falta antes de encenderlo.
pub mod visibility;
pub mod world;

// `pub(crate)` para que las pruebas del bucle de juego (ADR-045 enm. 2) monten el mismo mundo servido
// —manifiesto real y semilla servida— sin duplicar el cargador.
/// Sonda escéptica de interpenetración de cajas: fuerza bruta O(n²) con clasificación propia.
#[cfg(test)]
mod skeptic_boxes;
#[cfg(test)]
pub(crate) mod tests;
#[cfg(test)]
mod validate_tests;
