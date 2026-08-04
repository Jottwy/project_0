//! Level 0 — el nivel generado de arranque.
//!
//! Estado de cada submódulo, para que una limpieza futura distinga código vivo de andamiaje
//! SIN tener que repetir el grep: el crate lleva un `#![allow(dead_code)]` global
//! (`main.rs`), así que el compilador no lo va a decir por ti.
//!
//! - `ascii_export` — DEPURACIÓN / SÓLO TESTS. `export_level0_ascii` se re-exporta desde
//!   `generator.rs` bajo `#[cfg(test)]`; sus llamadores son `generator/tests.rs` y el golden
//!   slice. No entra en ninguna ruta de runtime.
//! - `builder` — VIVO. `Level0Builder::new(seed).build()` ES la generación de estructuras de
//!   Level 0 (`generator::generate_initial_structures`).
//! - `content` — VIVO. `apply_structure_content` / `spawn_entities` / `spawn_resources`
//!   rellenan cada chunk de estructura generado (`generator::generate_structure_chunk`).
//! - `level0_golden_slice` — FIXTURE DE TEST. El fichero no contiene otra cosa que un
//!   `#[cfg(test)] mod tests` que fija valores absolutos (IDs, tipos y posiciones para
//!   seed 42 y el hash del export ASCII de 42/7778). Es el cable trampa de cualquier cambio
//!   en el stream de RNG o en los IDs deterministas: si falla, es que algo se renumeró.
//! - `region_graph_builder` — VIVO. `world/mod.rs` construye el RegionGraph a partir de los
//!   chunks ya generados y loguea su auditoría (MPTRACE RG0..RG3). Sus consultas espaciales
//!   son harina de otro costal: hay ocho que sólo usan los tests, marcadas `AUDIT-ONLY`
//!   dentro de ese fichero.
//! - `structure` — VIVO. `StructureV0` / `StructureType` más `structure_bounds` y
//!   `structure_zone_kind`; lo consumen generación, contenido, el grafo y ambos showcases.
//! - `v30a_showcase` — VIVO PERO CONDICIONAL. `apply_v30a_layout` se llama en TODOS los
//!   chunks de estructura y retorna de inmediato salvo que la estructura lleve el tag
//!   `v30a_multilayer_showcase`, que `builder.rs` pone en el StackedCorridor que planta en
//!   cada mundo. O sea: se ejecuta siempre, actúa en una estructura por mundo.
//! - `validation` — SÓLO AUDITORÍA / TESTS. `validate_level0_region_graph` no tiene llamador
//!   de producción; sus tres call sites están todos en tests.
//! - `visfix_7778` — ANDAMIAJE, APAGADO POR DEFECTO. El overlay decorativo de seed 7778 sólo
//!   corre por `generate_initial_structure_chunks_with_visfix_overlay`, cuyos únicos
//!   llamadores son tests. En la ruta por defecto lo ÚNICO que se ejecuta de este módulo es
//!   `log_seed_7778_visfix_generation` (traza MPTRACE, sin efecto sobre los chunks).

pub mod ascii_export;
pub mod builder;
pub mod content;
pub mod level0_golden_slice;
pub mod region_graph_builder;
pub mod structure;
pub mod v30a_showcase;
pub mod validation;
pub mod visfix_7778;
