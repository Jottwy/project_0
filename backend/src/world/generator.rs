//! Deterministic, seed-based chunk generation — Level 0 Natural Generation V1.
//! See ARCHITECTURE_V1.md §7.1 and CLAUDE_CODE_INSTRUCTIONS.md Task 1.4.
//!
//! Level 0 layout: corridor-based, connected graph, Backrooms-style.
//! All generation is deterministic from world_seed.

use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

use crate::utils::ChunkPos;
use crate::world::chunk::{Chunk, ChunkLayer, ChunkState};
#[cfg(test)]
use crate::world::chunk::{
    ChunkLayoutV1, InterLayerVolumeKindV0, InterLayerVolumeV0, CELL_BLOCKED, CELL_FALSE_DOOR,
    CELL_HALF_WALL, CELL_LOW_WALL, CELL_WALKABLE, EDGE_EAST, EDGE_NORTH, EDGE_SOUTH, EDGE_WEST,
    FLOOR_CONNECTOR_DOWN, FLOOR_CONNECTOR_UP, FLOOR_FLAT, LAYOUT_GRID_SIZE, V30A_ATRIUM_VOID_ROOM,
    V30A_BLOCKED_VERTICAL_SHAFT, V30A_CONNECTOR, V30A_DEEP_PRECIPICE_PLACEHOLDER,
    V30A_GIANT_PILLAR_HALL, V30A_LOWER_SERVICE_BRANCH, V30A_STACKED_CORRIDOR,
    V30A_UPPER_OFFICE_BRANCH, VOLUME_VIS_LOWER_ROOM_VISIBLE, VOLUME_VIS_PILLAR_SPANS,
    VOLUME_VIS_RAILINGS, VOLUME_VIS_RIM_TRIMS, VOLUME_VIS_SHAFT_WALLS,
};

use crate::world::architecture::collision_builder::{
    relocate_contents_to_safe_cells, reserve_starter_spawn_area, template_is_vertical,
};
use crate::world::architecture::surface_builder::finalize_level0_edges;
#[cfg(test)]
use crate::world::chunk::{EDGE_KIND_ARCH, EDGE_KIND_DOOR};

#[cfg(test)]
use crate::world::architecture::collision_builder::{item_cell_blocked, world_to_cell};

#[cfg(test)]
use crate::world::architecture::surface_builder::{
    boundary_opening_cells, edge_is_opening, opposite_edge,
};

use crate::world::levels::level_0::builder::Level0Builder;

// MIG-5: template constants live in architecture::layout_grammars.
// MIG-2: generator imports only the templates it uses internally; external
// callers reach templates via the architecture facade (see architecture/mod.rs),
// not through generator.
use crate::world::architecture::layout_grammars::{
    template_zone_kind, TEMPLATE_ARCH_ROOM, TEMPLATE_BLACKOUT_ZONE, TEMPLATE_DEAD_END,
    TEMPLATE_HALLWAY_CORNER, TEMPLATE_HALLWAY_STRAIGHT, TEMPLATE_HALLWAY_T, TEMPLATE_HUMID_ZONE,
    TEMPLATE_INTERSECTION, TEMPLATE_MANILA_ROOM, TEMPLATE_OFFICE, TEMPLATE_OPEN_HALL,
    TEMPLATE_PILLAR_ROOM, TEMPLATE_PIT_ROOM_PLACEHOLDER, TEMPLATE_RED_ROOM_WARNING,
    TEMPLATE_ROOM_BASIC, TEMPLATE_STORAGE_ROOM,
};

pub use crate::world::levels::level_0::structure::StructureV0;

// ─── ID generation ───
pub use crate::world::architecture::chunk_generator::{chunk_seed_layer, next_entity_id_pub};
// MIG-2: build_chunk_layout is re-exported from the architecture facade
// (architecture/mod.rs); generator consumes it through that canonical path and no
// longer re-exports it itself.
use crate::world::architecture::build_chunk_layout;

// MIG-5a: ID helpers moved to architecture/chunk_generator.rs. (MIG-5f moved
// stable_volume_id's only consumer to levels/level_0/v30a_showcase.rs, which
// now imports it from its canonical home.)
pub(crate) use crate::world::architecture::chunk_generator::{fisher_yates, structure_id};

// MIG-5b: direction/rotation helpers moved to architecture/layout_grammars.rs.
pub(crate) use crate::world::architecture::layout_grammars::{
    corner_rotation, dir_delta, straight_rotation, t_junction_rotation,
};

// ─── Structure generation (Level 0 V1) ───

pub fn generate_initial_structures(world_seed: u64) -> Vec<StructureV0> {
    Level0Builder::new(world_seed).build()
}

pub fn generate_chunk(world_seed: u64, pos: ChunkPos) -> Chunk {
    generate_chunk_layer(world_seed, pos, 0)
}

pub fn generate_chunk_layer(world_seed: u64, pos: ChunkPos, layer: ChunkLayer) -> Chunk {
    // ADR-093 E1: la reserva del Level 4 no es Level 0 — su layout de colisión sale de
    // rasterizar el MISMO layout de región que la rejilla fina (el intercept espejo
    // vive en `grid_gen::stitching`). Ninguna otra ruta llega a estos chunks: la
    // reserva está a 2000 chunks del origen y las estructuras iniciales no la tocan.
    if let Some(local) = crate::world::grid_gen::level4::region_chunk_local(pos, layer as i32) {
        return crate::world::level4_layout::generate_region_chunk(world_seed, pos, layer, local);
    }

    let seed = chunk_seed_layer(world_seed, pos, layer);
    let mut rng = StdRng::seed_from_u64(seed);

    // Bias template distribution towards corridors and large liminal spaces.
    // Expansion chunks should still feel like Level 0: lots of boring corridors,
    // punctuated by occasional halls, column rooms and strange lighting zones.
    let depth = (pos.0.abs() + pos.1.abs()) as u32;
    let template_id = match rng.gen_range(0..100u32) {
        // ADR-081 enmienda 5 REVIRTIÓ la banda 32..=34 de TEMPLATE_SAFE_ROOM que la pieza 2 metió
        // aquí. Existía solo para que hubiera `ZONE_SAFE` en el mundo infinito cuando "zona segura"
        // era el criterio de construcción; desde que lo construible son las habitaciones talladas
        // (`grid_gen::build_rooms`) no compra nada, y era un cambio de worldgen que nadie pidió.
        0..=34 => TEMPLATE_HALLWAY_STRAIGHT,
        // OFFICE — banda tallada a HALLWAY_STRAIGHT (era 0..=38), el brazo más ancho del
        // sorteo, para que el 4% salga del pasillo genérico y no de un template que ya
        // aportaba carácter propio. Sin gate de `depth`: una planta de oficinas cerca de
        // la entrada es tan legítima como una lejos, igual que PILLAR_HALL.
        // ESPEJO OBLIGATORIO en `zone_density::expansion_template_id` — los dos se editan
        // juntos o `resolver_matches_real_world_zone_kind` falla.
        35..=38 => TEMPLATE_OFFICE,
        39..=51 => TEMPLATE_HALLWAY_CORNER,
        52..=61 => TEMPLATE_HALLWAY_T,
        62..=70 => TEMPLATE_INTERSECTION,
        71..=77 => TEMPLATE_ROOM_BASIC,
        78..=83 => TEMPLATE_OPEN_HALL,
        84..=88 => TEMPLATE_PILLAR_ROOM,
        89..=91 => TEMPLATE_STORAGE_ROOM,
        92..=94 => TEMPLATE_HUMID_ZONE,
        95 if depth >= 8 => TEMPLATE_BLACKOUT_ZONE,
        96 if depth >= 7 => TEMPLATE_ARCH_ROOM,
        97 if depth >= 9 => TEMPLATE_MANILA_ROOM,
        98 if depth >= 12 => TEMPLATE_RED_ROOM_WARNING,
        99 if depth >= 12 => TEMPLATE_PIT_ROOM_PLACEHOLDER,
        _ => TEMPLATE_DEAD_END,
    };

    // Phase 2.6: keep the immediate spawn region flat & navigable. No sunken /
    // raised / ramp / stair / pit chunks within two chunks of the origin, so
    // verticality never sits under or beside the player's spawn.
    let template_id = if depth <= 2 && template_is_vertical(template_id) {
        TEMPLATE_ROOM_BASIC
    } else {
        template_id
    };

    let rotation = (rng.gen_range(0..4u32) * 90) as u16;
    let mirrored = rng.gen_bool(0.5);
    let has_workbench = rng.gen_bool(0.2);
    let teleport_timer = rng.gen_range(120.0..600.0);

    let entities = if layer == 0 {
        spawn_entities(world_seed, pos, &mut rng)
    } else {
        Vec::new()
    };
    // Items v1 (Metal/Circuit/Battery/...) desactivados a petición: se
    // renderizaban como cubos placeholder sin arte propio. Ver content.rs para
    // el mismo apagado en el loot fijo de estructuras.
    let items = Vec::new();
    let mut layout = build_chunk_layout(template_id, rotation);
    // Expansion chunks (unlike generate_initial_structure_chunks_inner's fixed
    // structures) have no StructureV0 to derive a zone from — mirror the same
    // template→zone mapping used by the structure path's fallback
    // (structure_zone_kind's `_ => template_zone_kind(...)` arm) so zone_kind
    // reflects this chunk's actual template instead of staying ZONE_NORMAL.
    layout.zone_kind = template_zone_kind(template_id);

    // ADR-081 enmienda 5: la habitacion construible tambien se talla en el layout de COLISION, con
    // el mismo `RoomPlan` que ya talló la rejilla fina de `grid_gen`. Tallar solo aquella daría
    // paredes que se ven y se atraviesan; tallar solo esta, paredes invisibles que frenan.
    let build_plan = crate::world::grid_gen::room_in_chunk(world_seed, pos.0, pos.1, layer as u8);
    if let Some(plan) = build_plan {
        crate::world::build_room_layout::carve_into_layout(&mut layout, &plan);
    }

    // ADR-083 enmienda 1: y lo mismo con la sala autorada, con el MISMO plan puro que talló la
    // rejilla fina. Va después de la construible y le cede el sitio si se solapan, igual que allí —
    // `plan_authored_room` recibe el plan de la construible justo para eso.
    if let Some(manifest) = crate::world::grid_gen::active_manifest() {
        let rooms = crate::world::grid_gen::plan_authored_rooms(
            manifest,
            world_seed,
            pos.0,
            pos.1,
            layer as u8,
            build_plan.as_ref(),
        );
        for plan in rooms.iter() {
            crate::world::authored_room_layout::carve_authored_into_layout(&mut layout, plan);
        }
    }

    let mut chunk = Chunk {
        pos,
        layer,
        state: ChunkState::Active {
            stabilized: false,
            anchored: false,
        },
        seed,
        owner: None,
        entities,
        items,
        teleport_timer,
        template_id,
        rotation,
        mirrored,
        has_workbench,
        layout,
    };
    // Phase 2.6: nudge any item/entity that landed in a wall onto a clear cell.
    // Expansion chunks log at debug to avoid runtime spam while exploring.
    relocate_contents_to_safe_cells(&mut chunk, false);
    chunk
}

pub fn generate_initial_structure_chunks(world_seed: u64) -> Vec<(StructureV0, Chunk)> {
    // VISFIX overlay OFF by default: the seed-7778 near-spawn architecture is now
    // the backend-authored VolumetricGridV0 showcase (see
    // `crate::world::volumetric_grid`), shipped render-only on the spawn chunk.
    // The old decorative `inter_layer_volumes` validation overlay (oversized
    // markers, validation props) is suppressed so it never reaches runtime.
    generate_initial_structure_chunks_inner(world_seed, false)
}

/// Legacy path that still applies the decorative seed-7778 VISFIX overlay. Kept
/// only so the gated overlay code stays exercised by tests; not used in runtime.
pub fn generate_initial_structure_chunks_with_visfix_overlay(
    world_seed: u64,
) -> Vec<(StructureV0, Chunk)> {
    generate_initial_structure_chunks_inner(world_seed, true)
}

fn generate_initial_structure_chunks_inner(
    world_seed: u64,
    visfix_overlay: bool,
) -> Vec<(StructureV0, Chunk)> {
    let mut out = Vec::new();
    for structure in generate_initial_structures(world_seed) {
        let (min_x, min_z, max_x, max_z) = structure_bounds(&structure.chunks);
        for (index, pos) in structure.chunks.iter().copied().enumerate() {
            let layer = structure.chunk_layer(index);
            let mut chunk =
                generate_structure_chunk(world_seed, pos, layer, &structure, index as u32);
            chunk.layout.macro_id = structure.id;
            chunk.layout.zone_kind =
                structure_zone_kind(structure.structure_type, chunk.template_id);
            chunk.layout.macro_local = [
                (pos.0 - min_x).clamp(0, u8::MAX as i32) as u8,
                (pos.1 - min_z).clamp(0, u8::MAX as i32) as u8,
            ];
            chunk.layout.macro_size = [
                (max_x - min_x + 1).clamp(1, u8::MAX as i32) as u8,
                (max_z - min_z + 1).clamp(1, u8::MAX as i32) as u8,
            ];
            out.push((structure.clone(), chunk));
        }
    }

    reserve_starter_spawn_area(&mut out);
    finalize_level0_edges(&mut out);

    if visfix_overlay {
        apply_seed_7778_visfix_overlay(world_seed, &mut out);
        finalize_level0_edges(&mut out);
    }
    log_seed_7778_visfix_generation(world_seed, &out);
    out
}

// MIG-5c: structure helpers moved to levels/level_0/structure.rs.
pub(crate) use crate::world::levels::level_0::structure::{structure_bounds, structure_zone_kind};

// MIG-5g: seed-7778 VISFIX overlay (constants, structure builder, upsert and
// MPTRACE logging) moved to levels/level_0/visfix_7778.rs. The overlay entry
// points keep their exact call sites in generate_initial_structure_chunks_inner.
use crate::world::levels::level_0::visfix_7778::{
    apply_seed_7778_visfix_overlay, log_seed_7778_visfix_generation,
};

// MIG-5g: pub(crate) so the VISFIX upsert (its only external caller, in
// levels/level_0/visfix_7778.rs) can keep building structure chunks.
pub(crate) fn generate_structure_chunk(
    world_seed: u64,
    pos: ChunkPos,
    layer: ChunkLayer,
    structure: &StructureV0,
    chunk_index: u32,
) -> Chunk {
    let mut chunk = generate_chunk_layer(world_seed, pos, layer);
    chunk.mirrored = false;
    chunk.has_workbench = false;

    // Apply per-chunk template/rotation override
    if let Some(&(template_id, rotation)) = structure.chunk_overrides.get(chunk_index as usize) {
        chunk.template_id = template_id;
        chunk.rotation = rotation;
        chunk.layout = build_chunk_layout(template_id, rotation);
    }

    apply_v30a_layout(world_seed, &mut chunk, structure, chunk_index);

    // MIG-5e: per-structure-type content match moved to levels/level_0/content.rs.
    apply_structure_content(world_seed, pos, &mut chunk, structure);

    // Phase 2.6: structure items/entities use fixed local coordinates that can
    // land inside walls once the grammar layout is applied. Snap them onto safe
    // cells against the chunk's final layout.
    relocate_contents_to_safe_cells(&mut chunk, true);
    chunk
}

// MIG-5e: content-spawn helpers and match block moved to
// levels/level_0/content.rs.
use crate::world::levels::level_0::content::{apply_structure_content, spawn_entities};

// MIG-5f: V30A showcase layout + inter-layer volume builders moved to
// levels/level_0/v30a_showcase.rs.
use crate::world::levels::level_0::v30a_showcase::apply_v30a_layout;

#[cfg(test)]
pub use crate::world::levels::level_0::ascii_export::export_level0_ascii;

// ─── Graph topology query ───

// MIG-5d: graph topology queries moved to levels/level_0/region_graph_builder.rs.
// Re-exported for tests in region_graph_builder.rs that import from generator.
#[cfg(test)]
pub(crate) use crate::world::levels::level_0::region_graph_builder::{
    level0_proven_structure_connections, level0_proven_structure_connections_from_generated,
};

// ─── Test-only re-exports (needed by `use super::*` in mod tests) ───

#[cfg(test)]
use crate::utils::CHUNK_SIZE;
#[cfg(test)]
pub(crate) use crate::world::architecture::chunk_generator::chunk_seed;
#[cfg(test)]
use crate::world::architecture::surface_builder::edge_delta;
#[cfg(test)]
use crate::world::chunk::ZONE_NORMAL;
#[cfg(test)]
pub use crate::world::levels::level_0::structure::StructureType;

// ─── Tests ───

#[cfg(test)]
mod tests;
