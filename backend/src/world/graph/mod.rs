//! WorldGraph — grafo espacial DERIVADO: `WorldGraph` -> `LevelGraph` ->
//! `RegionGraph` -> {`SpatialNode`, `ConnectionEdge`} + capa paralela de
//! verticalidad (`verticality`).
//!
//! Nunca es autoridad de colision ni de generacion: se reconstruye del seed
//! tras `generate_initial_structures` y NO se persiste (ADR-032).
//! Contrato completo en `graph/README.md`.

pub mod coords;
pub mod edges;
pub mod level_graph;
pub mod nodes;
pub mod region_graph;
pub mod verticality;
pub mod world_graph;
