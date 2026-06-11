pub mod chunk3d_layout;
pub mod chunk_generator;
pub mod collision_builder;
pub mod layout_grammars;
pub mod surface_builder;

// MIG-2: canonical re-export facade. `build_chunk_layout` lives in
// `chunk_generator` and the `TEMPLATE_*` ids in `layout_grammars`; expose both at
// the `architecture` root so callers reach them here instead of through
// `generator.rs` (which previously re-exported them, inverting the layering —
// `architecture::collision_builder` was importing its own sibling's constants
// via `generator`).
pub use chunk_generator::build_chunk_layout;
pub use layout_grammars::{
    TEMPLATE_ARCH_ROOM, TEMPLATE_BLACKOUT_ZONE, TEMPLATE_CLEANING_AREA, TEMPLATE_DANGER_ROOM,
    TEMPLATE_DEAD_END, TEMPLATE_HALLWAY_CORNER, TEMPLATE_HALLWAY_STRAIGHT, TEMPLATE_HALLWAY_T,
    TEMPLATE_HUMID_ZONE, TEMPLATE_INTERSECTION, TEMPLATE_MANILA_ROOM, TEMPLATE_OPEN_HALL,
    TEMPLATE_PILLAR_ROOM, TEMPLATE_PIT_ROOM_PLACEHOLDER, TEMPLATE_POI_ANOMALY,
    TEMPLATE_POI_DANGER_POCKET, TEMPLATE_POI_LANDMARK, TEMPLATE_POI_SAFE_POCKET,
    TEMPLATE_RED_ROOM_WARNING, TEMPLATE_ROOM_BASIC, TEMPLATE_SAFE_ROOM, TEMPLATE_STORAGE_ROOM,
};
