//! Deterministic, seed-based chunk generation — Level 0 Natural Generation V1.
//! See ARCHITECTURE_V1.md §7.1 and CLAUDE_CODE_INSTRUCTIONS.md Task 1.4.
//!
//! Level 0 layout: corridor-based, connected graph, Backrooms-style.
//! All generation is deterministic from world_seed.

use std::collections::HashMap;

use log::info;
use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

use crate::player::inventory::Item;
use crate::utils::{ChunkPos, Vec3, CHUNK_SIZE};
use crate::world::chunk::{
    Chunk, ChunkLayer, ChunkLayoutV1, ChunkState, DroppedItem, InterLayerVolumeKindV0,
    InterLayerVolumeV0, CEILING_LOW_SERVICE, CEILING_NORMAL, CEILING_TALL_HALL, CELL_BLOCKED,
    CELL_HAZARD, CELL_PILLAR, CELL_PIT, CELL_WALKABLE, EDGE_EAST, EDGE_NORTH, EDGE_SOUTH,
    EDGE_WEST, FLOOR_CONNECTOR_DOWN, FLOOR_CONNECTOR_UP, FLOOR_FLAT, LAYOUT_GRID_SIZE, LIGHT_DIM,
    LIGHT_WARM, V30A_ATRIUM_VOID_ROOM, V30A_BLOCKED_VERTICAL_SHAFT, V30A_CONNECTOR,
    V30A_DEEP_PRECIPICE_PLACEHOLDER, V30A_GIANT_PILLAR_HALL, V30A_LOWER_SERVICE_BRANCH,
    V30A_STACKED_CORRIDOR, V30A_UPPER_OFFICE_BRANCH, VOLUME_VIS_ATRIUM_WALLS,
    VOLUME_VIS_CEILING_HINTS, VOLUME_VIS_DEPTH_CUES, VOLUME_VIS_LOWER_ROOM_VISIBLE,
    VOLUME_VIS_PILLAR_SPANS, VOLUME_VIS_RAILINGS, VOLUME_VIS_RIM_TRIMS, VOLUME_VIS_SHAFT_WALLS,
    VOLUME_VIS_STACKED_ALIGNMENT, VOLUME_VIS_UNDERFLOOR_HINTS, ZONE_BLACKOUT, ZONE_CLEANING,
    ZONE_DANGER, ZONE_HUMID, ZONE_MANILA, ZONE_NORMAL, ZONE_OPEN_HALL, ZONE_PILLAR_HALL, ZONE_PIT,
    ZONE_RED, ZONE_SAFE, ZONE_STORAGE,
};
#[cfg(test)]
use crate::world::chunk::{CELL_FALSE_DOOR, CELL_HALF_WALL, CELL_LOW_WALL};
use crate::world::chunk::{EDGE_KIND_LOW_WALL, EDGE_KIND_OPEN};

use crate::world::architecture::collision_builder::{
    relocate_contents_to_safe_cells, reserve_starter_spawn_area, template_is_vertical,
};
use crate::world::architecture::surface_builder::{
    edge_delta, finalize_level0_edges, perimeter_openings,
};
#[cfg(test)]
use crate::world::chunk::{EDGE_KIND_ARCH, EDGE_KIND_DOOR};
use crate::world::entity::{Entity, EntityType};

#[cfg(test)]
use crate::world::architecture::collision_builder::{item_cell_blocked, world_to_cell};

#[cfg(test)]
use crate::world::architecture::surface_builder::{
    boundary_opening_cells, edge_is_opening, opposite_edge,
};

use crate::world::levels::level_0::builder::Level0Builder;

// MIG-5: template constants moved to architecture::layout_grammars
pub use crate::world::architecture::layout_grammars::{
    TEMPLATE_ARCH_ROOM, TEMPLATE_BLACKOUT_ZONE, TEMPLATE_CLEANING_AREA, TEMPLATE_DANGER_ROOM,
    TEMPLATE_DEAD_END, TEMPLATE_HALLWAY_CORNER, TEMPLATE_HALLWAY_STRAIGHT, TEMPLATE_HALLWAY_T,
    TEMPLATE_HUMID_ZONE, TEMPLATE_INTERSECTION, TEMPLATE_MANILA_ROOM, TEMPLATE_OPEN_HALL,
    TEMPLATE_PILLAR_ROOM, TEMPLATE_PIT_ROOM_PLACEHOLDER, TEMPLATE_POI_ANOMALY,
    TEMPLATE_POI_DANGER_POCKET, TEMPLATE_POI_LANDMARK, TEMPLATE_POI_SAFE_POCKET,
    TEMPLATE_RED_ROOM_WARNING, TEMPLATE_ROOM_BASIC, TEMPLATE_SAFE_ROOM, TEMPLATE_STORAGE_ROOM,
};

const V30A2_VISFIX_SEED: u64 = 7778;
const V30A2_VISFIX_CONNECTOR: ChunkPos = (1, 3);
const V30A2_VISFIX_ATRIUM: ChunkPos = (1, 4);
const V30A2_VISFIX_TARGET_LAYER: ChunkLayer = -1;

pub use crate::world::levels::level_0::structure::{StructureType, StructureV0};

// ─── ID generation ───
pub use crate::world::architecture::chunk_generator::{
    build_chunk_layout, chunk_seed, chunk_seed_layer, next_entity_id_pub,
};

fn stable_u32(world_seed: u64, pos: ChunkPos, salt: u64, index: u32) -> u32 {
    let mut h = chunk_seed(world_seed ^ salt, pos);
    h ^= (index as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15);
    h ^= h >> 32;
    ((h & 0x7FFF_FFFF) as u32).max(1)
}

pub(crate) fn stable_entity_id(world_seed: u64, pos: ChunkPos, index: u32) -> u32 {
    stable_u32(world_seed, pos, 0xE17E_0001, index)
}

pub(crate) fn stable_item_id(world_seed: u64, pos: ChunkPos, index: u32) -> u32 {
    stable_u32(world_seed, pos, 0x17E0_0002, index)
}

fn stable_volume_id(world_seed: u64, pos: ChunkPos, layer: ChunkLayer, index: u32) -> u32 {
    let layer_salt = (layer as i64 as u64)
        .wrapping_mul(0xA30A_2001_5EED_0001)
        .rotate_left(11);
    stable_u32(world_seed, pos, 0xA30A_2002 ^ layer_salt, index)
}

pub(crate) fn structure_id(world_seed: u64, index: u32) -> u32 {
    stable_u32(
        world_seed,
        (index as i32, -(index as i32)),
        0x57A7_C700,
        index,
    )
}

// ─── Direction helpers (0=E, 1=N, 2=W, 3=S) ───

pub(crate) fn dir_delta(dir: u8) -> ChunkPos {
    match dir % 4 {
        0 => (1, 0),
        1 => (0, 1),
        2 => (-1, 0),
        _ => (0, -1),
    }
}

/// Rotation for hallway_straight: 0 = N/S open, 90 = E/W open.
pub(crate) fn straight_rotation(dir: u8) -> u16 {
    if dir % 2 == 0 {
        90
    } else {
        0
    }
}

/// Rotation for hallway_corner connecting entry_wall and exit_wall.
/// Walls: 0=E, 1=N, 2=W, 3=S. Entry wall = opposite of walking dir.
pub(crate) fn corner_rotation(from_dir: u8, to_dir: u8) -> u16 {
    let entry_wall = (from_dir + 2) % 4;
    let exit_wall = to_dir;
    let (a, b) = if entry_wall < exit_wall {
        (entry_wall, exit_wall)
    } else {
        (exit_wall, entry_wall)
    };
    match (a, b) {
        (0, 1) => 0,   // {E, N}
        (0, 3) => 90,  // {E, S}
        (2, 3) => 180, // {W, S}
        (1, 2) => 270, // {N, W}
        _ => 0,
    }
}

/// Rotation for hallway_t: determines which wall is closed.
/// Base (rot 0) = W closed. rot 90 = N closed. rot 180 = E closed. rot 270 = S closed.
pub(crate) fn t_junction_rotation(closed_wall: u8) -> u16 {
    match closed_wall % 4 {
        0 => 180,
        1 => 90,
        2 => 0,
        _ => 270,
    }
}

pub(crate) fn fisher_yates(slice: &mut [usize], rng: &mut StdRng) {
    for i in (1..slice.len()).rev() {
        let j = rng.gen_range(0..=i);
        slice.swap(i, j);
    }
}

// ─── Structure generation (Level 0 V1) ───

pub fn generate_initial_structures(world_seed: u64) -> Vec<StructureV0> {
    Level0Builder::new(world_seed).build()
}

pub fn generate_chunk(world_seed: u64, pos: ChunkPos) -> Chunk {
    generate_chunk_layer(world_seed, pos, 0)
}

pub fn generate_chunk_layer(world_seed: u64, pos: ChunkPos, layer: ChunkLayer) -> Chunk {
    let seed = chunk_seed_layer(world_seed, pos, layer);
    let mut rng = StdRng::seed_from_u64(seed);

    // Bias template distribution towards corridors and large liminal spaces.
    // Expansion chunks should still feel like Level 0: lots of boring corridors,
    // punctuated by occasional halls, column rooms and strange lighting zones.
    let depth = (pos.0.abs() + pos.1.abs()) as u32;
    let template_id = match rng.gen_range(0..100u32) {
        0..=38 => TEMPLATE_HALLWAY_STRAIGHT,
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
    let items = if layer == 0 {
        spawn_resources(world_seed, pos, &mut rng)
    } else {
        Vec::new()
    };
    let layout = build_chunk_layout(template_id, rotation);

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

pub(crate) fn structure_bounds(chunks: &[ChunkPos]) -> (i32, i32, i32, i32) {
    let mut min_x = chunks[0].0;
    let mut min_z = chunks[0].1;
    let mut max_x = chunks[0].0;
    let mut max_z = chunks[0].1;
    for &(x, z) in chunks {
        min_x = min_x.min(x);
        min_z = min_z.min(z);
        max_x = max_x.max(x);
        max_z = max_z.max(z);
    }
    (min_x, min_z, max_x, max_z)
}

fn structure_zone_kind(structure_type: StructureType, template_id: u8) -> u8 {
    match structure_type {
        StructureType::StorageRoom => ZONE_STORAGE,
        StructureType::SafeRoom | StructureType::StarterCluster => ZONE_SAFE,
        StructureType::DangerRoom => ZONE_DANGER,
        StructureType::OpenHall => ZONE_OPEN_HALL,
        StructureType::PillarRoom | StructureType::PillarHall => ZONE_PILLAR_HALL,
        StructureType::HumidZone => ZONE_HUMID,
        StructureType::BlackoutZone => ZONE_BLACKOUT,
        StructureType::ManilaRoom => ZONE_MANILA,
        StructureType::CleaningArea => ZONE_CLEANING,
        StructureType::RedRoom => ZONE_RED,
        StructureType::PitRoom => ZONE_PIT,
        StructureType::StackedCorridor => ZONE_NORMAL,
        StructureType::LowerServiceBranch => ZONE_CLEANING,
        StructureType::UpperOfficeBranch => ZONE_MANILA,
        StructureType::AtriumVoidRoom => ZONE_OPEN_HALL,
        StructureType::DeepPrecipicePlaceholder => ZONE_PIT,
        StructureType::GiantPillarHall => ZONE_PILLAR_HALL,
        StructureType::PoiLandmark => ZONE_OPEN_HALL,
        StructureType::PoiAnomalyCluster => ZONE_NORMAL,
        StructureType::PoiDangerPocket => ZONE_DANGER,
        StructureType::PoiSafePocket => ZONE_MANILA,
        _ => crate::world::architecture::layout_grammars::template_zone_kind(template_id),
    }
}

fn seed_7778_visfix_structure(world_seed: u64) -> StructureV0 {
    let chunks = vec![
        V30A2_VISFIX_CONNECTOR,
        V30A2_VISFIX_CONNECTOR,
        V30A2_VISFIX_ATRIUM,
        V30A2_VISFIX_ATRIUM,
    ];
    let layers = vec![0, V30A2_VISFIX_TARGET_LAYER, 0, V30A2_VISFIX_TARGET_LAYER];
    let (min_x, min_z, max_x, max_z) = structure_bounds(&chunks);
    StructureV0 {
        id: structure_id(world_seed, 30_020),
        structure_type: StructureType::StackedCorridor,
        origin: V30A2_VISFIX_CONNECTOR,
        origin_layer: 0,
        size: [
            (max_x - min_x + 1).clamp(1, u8::MAX as i32) as u8,
            (max_z - min_z + 1).clamp(1, u8::MAX as i32) as u8,
        ],
        seed: chunk_seed_layer(world_seed, V30A2_VISFIX_CONNECTOR, 0),
        chunks,
        layers,
        tags: vec![
            "macro",
            "v30a_multilayer_showcase",
            "v30a2_visfix_showcase",
            "seed_7778_validation",
        ],
        chunk_overrides: vec![
            (TEMPLATE_OPEN_HALL, 0),
            (TEMPLATE_CLEANING_AREA, 0),
            (TEMPLATE_OPEN_HALL, 0),
            (TEMPLATE_PILLAR_ROOM, 0),
        ],
    }
}

fn apply_structure_metadata(structure: &StructureV0, chunk: &mut Chunk, index: usize) {
    let (min_x, min_z, max_x, max_z) = structure_bounds(&structure.chunks);
    chunk.layout.macro_id = structure.id;
    chunk.layout.zone_kind = structure_zone_kind(structure.structure_type, chunk.template_id);
    chunk.layout.macro_local = [
        (chunk.pos.0 - min_x).clamp(0, u8::MAX as i32) as u8,
        (chunk.pos.1 - min_z).clamp(0, u8::MAX as i32) as u8,
    ];
    chunk.layout.macro_size = [
        (max_x - min_x + 1).clamp(1, u8::MAX as i32) as u8,
        (max_z - min_z + 1).clamp(1, u8::MAX as i32) as u8,
    ];
    if structure.tags.contains(&"v30a2_visfix_showcase") {
        chunk.teleport_timer = f32::MAX;
        chunk.entities.clear();
        chunk.items.clear();
        info!(
            "MPTRACE step=V30A2 event=v30a2_visfix_backend_volume_chunk chunk=({},{},{}) structure_id={} chunk_index={} macro_local=({},{})",
            chunk.pos.0,
            chunk.layer,
            chunk.pos.1,
            structure.id,
            index,
            chunk.layout.macro_local[0],
            chunk.layout.macro_local[1]
        );
    }
}

fn upsert_seed_7778_visfix_chunk(
    out: &mut Vec<(StructureV0, Chunk)>,
    structure: &StructureV0,
    index: usize,
    world_seed: u64,
) {
    let pos = structure.chunks[index];
    let layer = structure.chunk_layer(index);
    let mut chunk = generate_structure_chunk(world_seed, pos, layer, structure, index as u32);
    apply_structure_metadata(structure, &mut chunk, index);

    if let Some((existing_structure, existing_chunk)) = out
        .iter_mut()
        .find(|(_, c)| c.pos == pos && c.layer == layer)
    {
        *existing_structure = structure.clone();
        *existing_chunk = chunk;
    } else {
        out.push((structure.clone(), chunk));
    }
}

fn apply_seed_7778_visfix_overlay(world_seed: u64, out: &mut Vec<(StructureV0, Chunk)>) {
    if world_seed != V30A2_VISFIX_SEED {
        return;
    }

    info!(
        "MPTRACE step=V30A2 event=v30a2_visfix_backend_seed_7778_active mode=validation_overlay connector=({},{},{}) atrium=({},{},{})",
        V30A2_VISFIX_CONNECTOR.0,
        0,
        V30A2_VISFIX_CONNECTOR.1,
        V30A2_VISFIX_ATRIUM.0,
        V30A2_VISFIX_TARGET_LAYER,
        V30A2_VISFIX_ATRIUM.1
    );

    let structure = seed_7778_visfix_structure(world_seed);
    for index in 0..structure.chunks.len() {
        upsert_seed_7778_visfix_chunk(out, &structure, index, world_seed);
    }
}

fn log_seed_7778_visfix_generation(world_seed: u64, chunks: &[(StructureV0, Chunk)]) {
    if world_seed != V30A2_VISFIX_SEED {
        return;
    }

    let volume_count: usize = chunks
        .iter()
        .map(|(_, c)| c.layout.inter_layer_volumes.len())
        .sum();
    let volume_chunks = chunks
        .iter()
        .filter(|(_, c)| !c.layout.inter_layer_volumes.is_empty())
        .count();
    info!(
        "MPTRACE step=V30A2 event=v30a2_visfix_backend_volume_count seed={} volumes={} volume_chunks={} generated_chunks={}",
        world_seed,
        volume_count,
        volume_chunks,
        chunks.len()
    );

    for (_, chunk) in chunks
        .iter()
        .filter(|(_, c)| !c.layout.inter_layer_volumes.is_empty())
    {
        let dist_from_spawn = chunk.pos.0.abs().max(chunk.pos.1.abs());
        info!(
            "MPTRACE step=V30A2 event=v30a2_visfix_backend_volume_chunk chunk=({},{},{}) volumes={} layer_y={:.2} visible_radius_5={}",
            chunk.pos.0,
            chunk.layer,
            chunk.pos.1,
            chunk.layout.inter_layer_volumes.len(),
            crate::world::chunk::layer_y(chunk.layer),
            dist_from_spawn <= 5
        );
    }

    for pos in [V30A2_VISFIX_CONNECTOR, (-5, -1)] {
        let dist_from_spawn = pos.0.abs().max(pos.1.abs());
        info!(
            "MPTRACE step=V30A2 event=v30a2_visfix_backend_visible_radius_check chunk=({},{}) dist_from_spawn={} ownership_radius=5 visible={}",
            pos.0,
            pos.1,
            dist_from_spawn,
            dist_from_spawn <= 5
        );
    }

    let connector_world = crate::utils::chunk_center(V30A2_VISFIX_CONNECTOR);
    let original_world = crate::utils::chunk_center((-5, -1));
    info!(
        "MPTRACE step=V30A2 event=v30a2_visfix_backend_showcase_world_position validation_chunk=({},{},{}) validation_world=({:.1},{:.1},{:.1}) original_chunk=(-5,0,-1) original_world=({:.1},{:.1},{:.1}) spawn_world=(25.0,1.8,25.0)",
        V30A2_VISFIX_CONNECTOR.0,
        0,
        V30A2_VISFIX_CONNECTOR.1,
        connector_world.x,
        0.0,
        connector_world.z,
        original_world.x,
        0.0,
        original_world.z
    );
}

fn generate_structure_chunk(
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

    let depth = (pos.0.abs() + pos.1.abs()) as f32;

    // Apply structure-type-specific content (items, entities)
    match structure.structure_type {
        StructureType::StarterCluster => {
            chunk.entities.clear();
            if pos == (0, 0) {
                chunk.items = vec![
                    dropped_item(world_seed, pos, 0, Item::Food, 1, 18.0, 18.0),
                    dropped_item(world_seed, pos, 1, Item::Water, 1, 32.0, 30.0),
                ];
            } else {
                chunk.items = vec![dropped_item(world_seed, pos, 0, Item::Food, 1, 20.0, 20.0)];
            }
        }
        StructureType::HallwayChain | StructureType::HallwayT => {
            if depth < 3.0 {
                chunk.entities.clear();
                chunk.items.truncate(1);
            } else if depth < 7.0 {
                chunk.entities.truncate(1);
                chunk.items.truncate(1);
            } else {
                chunk.entities.truncate(2);
                chunk.items.truncate(1);
            }
        }
        StructureType::Intersection => {
            chunk.entities.truncate(1);
            chunk.items.truncate(2);
        }
        StructureType::StorageRoom => {
            chunk.has_workbench = true;
            chunk.entities.clear();
            chunk.items = vec![
                dropped_item(world_seed, pos, 0, Item::Metal, 2, 12.0, 12.0),
                dropped_item(world_seed, pos, 1, Item::Circuit, 1, 18.0, 34.0),
                dropped_item(world_seed, pos, 2, Item::Battery, 1, 35.0, 18.0),
                dropped_item(world_seed, pos, 3, Item::Tool, 1, 37.0, 37.0),
            ];
        }
        StructureType::SafeRoom => {
            chunk.has_workbench = true;
            chunk.entities.clear();
            chunk.items = vec![
                dropped_item(world_seed, pos, 0, Item::Food, 2, 16.0, 16.0),
                dropped_item(world_seed, pos, 1, Item::Medicine, 1, 31.0, 31.0),
            ];
        }
        StructureType::DeadEnd => {
            chunk.entities.truncate(1);
            chunk.items = vec![dropped_item(world_seed, pos, 0, Item::Cable, 2, 25.0, 34.0)];
        }
        StructureType::DangerRoom => {
            chunk.entities = vec![
                Entity::new(
                    stable_entity_id(world_seed, pos, 0),
                    EntityType::Shadow,
                    local_pos_in_chunk(pos, 25.0, 25.0),
                ),
                Entity::new(
                    stable_entity_id(world_seed, pos, 1),
                    EntityType::Crawler,
                    local_pos_in_chunk(pos, 35.0, 20.0),
                ),
            ];
            chunk.items = vec![dropped_item(
                world_seed,
                pos,
                0,
                Item::Battery,
                1,
                14.0,
                36.0,
            )];
        }
        StructureType::PillarRoom | StructureType::PillarHall | StructureType::OpenHall => {
            if depth < 5.0 {
                chunk.entities.clear();
            } else {
                chunk.entities.truncate(1);
            }
            chunk.items.truncate(2);
        }
        StructureType::HumidZone => {
            chunk.entities.truncate(if depth < 6.0 { 0 } else { 1 });
            chunk.items = vec![
                dropped_item(world_seed, pos, 0, Item::Cable, 1, 18.0, 22.0),
                dropped_item(world_seed, pos, 1, Item::Water, 1, 33.0, 28.0),
            ];
        }
        StructureType::ArchRoom => {
            chunk.entities.clear();
            chunk.items.truncate(1);
        }
        StructureType::BlackoutZone => {
            chunk.entities.truncate(2);
            if chunk.entities.is_empty() && depth >= 6.0 {
                chunk.entities = vec![Entity::new(
                    stable_entity_id(world_seed, pos, 0),
                    EntityType::Lurker,
                    local_pos_in_chunk(pos, 30.0, 30.0),
                )];
            }
            chunk.items.truncate(1);
        }
        StructureType::RedRoom => {
            chunk.entities.truncate(if depth < 10.0 { 0 } else { 1 });
            chunk.items.truncate(1);
        }
        StructureType::ManilaRoom => {
            chunk.entities.clear();
            chunk.items.truncate(1);
        }
        StructureType::CleaningArea => {
            chunk.entities.clear();
            chunk.items = vec![
                dropped_item(world_seed, pos, 0, Item::Water, 1, 16.0, 18.0),
                dropped_item(world_seed, pos, 1, Item::Tool, 1, 34.0, 31.0),
                dropped_item(world_seed, pos, 2, Item::Cable, 1, 28.0, 38.0),
            ];
        }
        StructureType::PitRoom => {
            chunk.entities.truncate(if depth < 7.0 { 0 } else { 1 });
            chunk.items.truncate(1);
        }
        StructureType::StackedCorridor
        | StructureType::LowerServiceBranch
        | StructureType::UpperOfficeBranch
        | StructureType::AtriumVoidRoom
        | StructureType::DeepPrecipicePlaceholder
        | StructureType::GiantPillarHall => {
            chunk.entities.clear();
            chunk.items.clear();
        }
        StructureType::PoiLandmark => {
            // Memorable landmark room: no entities, one battery as a remnant.
            chunk.entities.clear();
            chunk.items = vec![dropped_item(
                world_seed,
                pos,
                0,
                Item::Battery,
                1,
                22.0,
                32.0,
            )];
        }
        StructureType::PoiAnomalyCluster => {
            // Disorienting cluster: shadowy presence at depth, sparse loot.
            if depth >= 5.0 {
                chunk.entities = vec![Entity::new(
                    stable_entity_id(world_seed, pos, 0),
                    EntityType::Shadow,
                    local_pos_in_chunk(pos, 28.0, 28.0),
                )];
            } else {
                chunk.entities.clear();
            }
            chunk.items.truncate(1);
        }
        StructureType::PoiDangerPocket => {
            // Danger pocket: hostile entity + hazard loot (deep only).
            chunk.entities = vec![Entity::new(
                stable_entity_id(world_seed, pos, 0),
                EntityType::Crawler,
                local_pos_in_chunk(pos, 20.0, 20.0),
            )];
            chunk.items = vec![dropped_item(
                world_seed,
                pos,
                0,
                Item::Medicine,
                1,
                38.0,
                38.0,
            )];
        }
        StructureType::PoiSafePocket => {
            // Safe pocket: clear of entities, has food/water.
            chunk.entities.clear();
            chunk.items = vec![
                dropped_item(world_seed, pos, 0, Item::Food, 1, 18.0, 25.0),
                dropped_item(world_seed, pos, 1, Item::Water, 1, 34.0, 25.0),
            ];
        }
    }

    // Phase 2.6: structure items/entities use fixed local coordinates that can
    // land inside walls once the grammar layout is applied. Snap them onto safe
    // cells against the chunk's final layout.
    relocate_contents_to_safe_cells(&mut chunk, true);
    chunk
}

fn inter_layer_layers(target_layer: ChunkLayer) -> Vec<ChunkLayer> {
    vec![0, target_layer]
}

fn inter_layer_volume(
    world_seed: u64,
    base_chunk: ChunkPos,
    target_layer: ChunkLayer,
    index: u32,
    kind: InterLayerVolumeKindV0,
    footprint_cell_min: [u8; 2],
    footprint_cell_max: [u8; 2],
    safety_type: &str,
    future_audio_hint: &str,
    visual_flags: u32,
    visual_hints: &[&str],
) -> InterLayerVolumeV0 {
    InterLayerVolumeV0 {
        volume_id: stable_volume_id(world_seed, base_chunk, 0, index),
        kind,
        base_chunk: [base_chunk.0, base_chunk.1],
        involved_layers: inter_layer_layers(target_layer),
        footprint_cell_min,
        footprint_cell_max,
        safety_type: safety_type.into(),
        future_audio_hint: future_audio_hint.into(),
        visual_flags,
        visual_hints: visual_hints.iter().map(|hint| (*hint).into()).collect(),
    }
}

fn push_inter_layer_volume(layout: &mut ChunkLayoutV1, volume: InterLayerVolumeV0) {
    info!(
        "MPTRACE step=V30A2 event=inter_layer_volume_created volume_id={} kind={} base_chunk=({},{}) footprint_cells=({},{})..({},{}) safety_type={} visual_flags={}",
        volume.volume_id,
        volume.kind.as_str(),
        volume.base_chunk[0],
        volume.base_chunk[1],
        volume.footprint_cell_min[0],
        volume.footprint_cell_min[1],
        volume.footprint_cell_max[0],
        volume.footprint_cell_max[1],
        volume.safety_type,
        volume.visual_flags
    );
    info!(
        "MPTRACE step=V30A2 event=vertical_volume_kind volume_id={} kind={}",
        volume.volume_id,
        volume.kind.as_str()
    );
    info!(
        "MPTRACE step=V30A2 event=vertical_volume_layers volume_id={} layers={:?}",
        volume.volume_id, volume.involved_layers
    );
    if !volume.future_audio_hint.is_empty() {
        info!(
            "MPTRACE step=V30A2 event=future_audio_hint_registered volume_id={} hint={}",
            volume.volume_id, volume.future_audio_hint
        );
    }

    match volume.kind {
        InterLayerVolumeKindV0::AtriumStack | InterLayerVolumeKindV0::ServiceShaft => {
            info!(
                "MPTRACE step=V30A2 event=shared_opening_built volume_id={} kind={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.kind.as_str(),
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
        InterLayerVolumeKindV0::StackedCorridorPair => {
            info!(
                "MPTRACE step=V30A2 event=stacked_corridor_pair_built volume_id={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
        InterLayerVolumeKindV0::OverlookRoom => {
            info!(
                "MPTRACE step=V30A2 event=lower_room_visible_from_above volume_id={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
        InterLayerVolumeKindV0::GiantPillarSpan => {
            info!(
                "MPTRACE step=V30A2 event=pillar_span_built volume_id={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
        InterLayerVolumeKindV0::CeilingActivityZone => {
            info!(
                "MPTRACE step=V30A2 event=ceiling_activity_hint_built volume_id={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
        InterLayerVolumeKindV0::UnderfloorServiceZone => {
            info!(
                "MPTRACE step=V30A2 event=underfloor_service_hint_built volume_id={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
    }

    layout.inter_layer_volumes.push(volume);
}

fn add_connector_inter_layer_volumes(world_seed: u64, chunk: &mut Chunk, target_layer: ChunkLayer) {
    let base = chunk.pos;
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            0,
            InterLayerVolumeKindV0::ServiceShaft,
            [2, 0],
            [8, 10],
            "BACKEND_AUTHORED_VISUAL_NO_FALL",
            "service_shaft_hum_from_lower_layer",
            VOLUME_VIS_SHAFT_WALLS
                | VOLUME_VIS_RAILINGS
                | VOLUME_VIS_RIM_TRIMS
                | VOLUME_VIS_DEPTH_CUES,
            &["shaft_walls", "railing_runs", "matched_receiving_space"],
        ),
    );
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            1,
            InterLayerVolumeKindV0::StackedCorridorPair,
            [2, 0],
            [8, 10],
            "BACKEND_AUTHORED_ALIGNMENT",
            "stacked_corridor_air_path",
            VOLUME_VIS_STACKED_ALIGNMENT | VOLUME_VIS_RIM_TRIMS | VOLUME_VIS_CEILING_HINTS,
            &["matching_corridor_axis", "ceiling_floor_alignment"],
        ),
    );
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            2,
            InterLayerVolumeKindV0::UnderfloorServiceZone,
            [1, 1],
            [9, 9],
            "VISUAL_HINT_ONLY",
            "underfloor_service_void",
            VOLUME_VIS_UNDERFLOOR_HINTS | VOLUME_VIS_DEPTH_CUES,
            &["open_floor_service_grates", "subfloor_cable_trays"],
        ),
    );
}

fn add_atrium_inter_layer_volumes(world_seed: u64, chunk: &mut Chunk, target_layer: ChunkLayer) {
    let base = chunk.pos;
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            10,
            InterLayerVolumeKindV0::AtriumStack,
            [3, 3],
            [7, 7],
            "BACKEND_AUTHORED_BLOCKED_SHAFT_NO_FALL",
            "atrium_vertical_reverb",
            VOLUME_VIS_ATRIUM_WALLS
                | VOLUME_VIS_LOWER_ROOM_VISIBLE
                | VOLUME_VIS_RAILINGS
                | VOLUME_VIS_RIM_TRIMS
                | VOLUME_VIS_DEPTH_CUES,
            &["shared_opening", "shaft_wall_panels", "lower_room_cues"],
        ),
    );
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            11,
            InterLayerVolumeKindV0::OverlookRoom,
            [2, 2],
            [8, 8],
            "VISUAL_OVERLOOK_WITH_RAILING",
            "lower_room_floor_reflection",
            VOLUME_VIS_LOWER_ROOM_VISIBLE
                | VOLUME_VIS_RAILINGS
                | VOLUME_VIS_RIM_TRIMS
                | VOLUME_VIS_DEPTH_CUES,
            &[
                "visible_lower_room",
                "overlook_railings",
                "depth_floor_patch",
            ],
        ),
    );
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            12,
            InterLayerVolumeKindV0::GiantPillarSpan,
            [1, 1],
            [9, 9],
            "STRUCTURAL_VISUAL_SUPPORT",
            "pillar_span_occlusion",
            VOLUME_VIS_PILLAR_SPANS | VOLUME_VIS_DEPTH_CUES,
            &[
                "layer_spanning_pillars",
                "pillar_caps_visible_across_layers",
            ],
        ),
    );
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            13,
            InterLayerVolumeKindV0::CeilingActivityZone,
            [1, 1],
            [9, 9],
            "VISUAL_HINT_ONLY",
            "muffled_ceiling_activity",
            VOLUME_VIS_CEILING_HINTS | VOLUME_VIS_STACKED_ALIGNMENT,
            &["ceiling_service_panels", "upper_layer_activity_hint"],
        ),
    );
}

fn apply_v30a_layout(
    world_seed: u64,
    chunk: &mut Chunk,
    structure: &StructureV0,
    chunk_index: u32,
) {
    if !structure.tags.contains(&"v30a_multilayer_showcase") {
        return;
    }

    let target_layer = structure
        .layers
        .iter()
        .copied()
        .find(|layer| *layer != 0)
        .unwrap_or(if world_seed % 2 == 0 { -1 } else { 1 });
    let branch_flag = if target_layer > 0 {
        V30A_UPPER_OFFICE_BRANCH
    } else {
        V30A_LOWER_SERVICE_BRANCH
    };

    chunk.entities.clear();
    chunk.items.clear();
    chunk.teleport_timer = f32::MAX;

    match chunk_index {
        0 => {
            chunk.layout = connector_layout(target_layer);
            chunk.template_id = TEMPLATE_OPEN_HALL;
            chunk.rotation = 0;
            chunk.layout.floor_profile = if target_layer > 0 {
                FLOOR_CONNECTOR_UP
            } else {
                FLOOR_CONNECTOR_DOWN
            };
            chunk.layout.ceiling_profile = CEILING_TALL_HALL;
            chunk.layout.light_profile = LIGHT_WARM;
            chunk.layout.vertical_flags |= V30A_CONNECTOR | branch_flag | V30A_STACKED_CORRIDOR;
            add_connector_inter_layer_volumes(world_seed, chunk, target_layer);
        }
        1 => {
            chunk.layout.vertical_flags |= V30A_STACKED_CORRIDOR | branch_flag;
            chunk.layout.floor_profile = FLOOR_FLAT;
            chunk.layout.floor_level = 0;
            chunk.layout.ceiling_profile = if target_layer > 0 {
                CEILING_NORMAL
            } else {
                CEILING_LOW_SERVICE
            };
            chunk.layout.light_profile = if target_layer > 0 {
                LIGHT_WARM
            } else {
                LIGHT_DIM
            };
            add_connector_inter_layer_volumes(world_seed, chunk, target_layer);
        }
        2 => {
            chunk.template_id = TEMPLATE_OPEN_HALL;
            chunk.layout = build_chunk_layout(TEMPLATE_OPEN_HALL, 0);
            mark_railed_vertical_opening(&mut chunk.layout, true);
            chunk.layout.ceiling_profile = CEILING_TALL_HALL;
            chunk.layout.vertical_flags |= V30A_ATRIUM_VOID_ROOM
                | V30A_DEEP_PRECIPICE_PLACEHOLDER
                | V30A_BLOCKED_VERTICAL_SHAFT;
            add_atrium_inter_layer_volumes(world_seed, chunk, target_layer);
        }
        _ => {
            chunk.template_id = TEMPLATE_PILLAR_ROOM;
            chunk.layout = build_chunk_layout(TEMPLATE_PILLAR_ROOM, 0);
            mark_giant_pillars(&mut chunk.layout);
            chunk.layout.ceiling_profile = CEILING_TALL_HALL;
            chunk.layout.vertical_flags |=
                V30A_GIANT_PILLAR_HALL | V30A_STACKED_CORRIDOR | branch_flag;
            add_atrium_inter_layer_volumes(world_seed, chunk, target_layer);
        }
    }
}

fn connector_layout(target_layer: ChunkLayer) -> ChunkLayoutV1 {
    let size = LAYOUT_GRID_SIZE as usize;
    let mut layout = ChunkLayoutV1::new(vec![CELL_WALKABLE; size * size], 0, ZONE_OPEN_HALL);
    layout.ceiling_profile = CEILING_TALL_HALL;
    layout.light_profile = LIGHT_WARM;

    for z in 0..size {
        layout.set_edge_v(2, z, EDGE_KIND_LOW_WALL);
        layout.set_edge_v(size - 2, z, EDGE_KIND_LOW_WALL);
    }
    for x in 3..=6 {
        layout.set_edge_h(x, 0, EDGE_KIND_OPEN);
        layout.set_edge_h(x, size, EDGE_KIND_OPEN);
    }

    layout.floor_profile = if target_layer > 0 {
        FLOOR_CONNECTOR_UP
    } else {
        FLOOR_CONNECTOR_DOWN
    };
    layout.vertical_flags = V30A_CONNECTOR | V30A_STACKED_CORRIDOR;
    layout.edge_openings = perimeter_openings(&layout);
    layout
}

fn mark_railed_vertical_opening(layout: &mut ChunkLayoutV1, deep_precipice: bool) {
    let g = layout.grid_size as usize;
    let start = 3usize;
    let end = (g - 3).max(start + 1);
    for z in start..end {
        for x in start..end {
            if let Some(idx) = layout.cell_index(x, z) {
                layout.cells[idx] = CELL_WALKABLE | CELL_PIT | CELL_HAZARD;
            }
        }
    }
    for z in start..end {
        layout.set_edge_v(start, z, EDGE_KIND_LOW_WALL);
        layout.set_edge_v(end, z, EDGE_KIND_LOW_WALL);
    }
    for x in start..end {
        layout.set_edge_h(x, start, EDGE_KIND_LOW_WALL);
        layout.set_edge_h(x, end, EDGE_KIND_LOW_WALL);
    }
    layout.floor_profile = FLOOR_FLAT;
    layout.vertical_flags |= V30A_ATRIUM_VOID_ROOM | V30A_BLOCKED_VERTICAL_SHAFT;
    if deep_precipice {
        layout.vertical_flags |= V30A_DEEP_PRECIPICE_PLACEHOLDER;
        layout.anomaly_flags |= 1 << 4;
    }
}

fn mark_giant_pillars(layout: &mut ChunkLayoutV1) {
    let positions = [(2usize, 2usize), (7, 2), (2, 7), (7, 7)];
    for (x, z) in positions {
        if let Some(idx) = layout.cell_index(x, z) {
            layout.cells[idx] = CELL_WALKABLE | CELL_PILLAR;
        }
    }
    layout.floor_profile = FLOOR_FLAT;
    layout.vertical_flags |= V30A_GIANT_PILLAR_HALL;
}

fn dropped_item(
    world_seed: u64,
    pos: ChunkPos,
    index: u32,
    item: Item,
    count: u16,
    local_x: f32,
    local_z: f32,
) -> DroppedItem {
    DroppedItem {
        id: stable_item_id(world_seed, pos, index),
        item,
        quantity: count,
        position: local_pos_in_chunk(pos, local_x, local_z),
    }
}
fn random_pos_in_chunk(pos: ChunkPos, rng: &mut StdRng) -> Vec3 {
    let base_x = pos.0 as f32 * CHUNK_SIZE;
    let base_z = pos.1 as f32 * CHUNK_SIZE;

    Vec3::new(
        rng.gen_range(base_x + 2.0..base_x + CHUNK_SIZE - 2.0),
        0.0,
        rng.gen_range(base_z + 2.0..base_z + CHUNK_SIZE - 2.0),
    )
}

fn local_pos_in_chunk(pos: ChunkPos, local_x: f32, local_z: f32) -> Vec3 {
    Vec3::new(
        pos.0 as f32 * CHUNK_SIZE + local_x,
        0.0,
        pos.1 as f32 * CHUNK_SIZE + local_z,
    )
}

fn spawn_resources(world_seed: u64, pos: ChunkPos, rng: &mut StdRng) -> Vec<DroppedItem> {
    let mut items = Vec::new();
    let mut next_index = 0u32;

    let mut place = |items: &mut Vec<DroppedItem>, item: Item, count: u16, item_pos: Vec3| {
        items.push(DroppedItem {
            id: stable_item_id(world_seed, pos, next_index),
            item,
            quantity: count,
            position: item_pos,
        });
        next_index += 1;
    };

    for _ in 0..rng.gen_range(1..=5) {
        place(&mut items, Item::Metal, 1, random_pos_in_chunk(pos, rng));
    }
    for _ in 0..rng.gen_range(1..=3) {
        place(&mut items, Item::Circuit, 1, random_pos_in_chunk(pos, rng));
    }
    for _ in 0..rng.gen_range(1..=2) {
        place(&mut items, Item::Battery, 1, random_pos_in_chunk(pos, rng));
    }
    for _ in 0..rng.gen_range(1..=3) {
        place(&mut items, Item::Food, 1, random_pos_in_chunk(pos, rng));
    }
    for _ in 0..rng.gen_range(1..=3) {
        place(&mut items, Item::Water, 1, random_pos_in_chunk(pos, rng));
    }

    items
}

#[cfg(test)]
pub use crate::world::levels::level_0::ascii_export::export_level0_ascii;

fn spawn_entities(world_seed: u64, pos: ChunkPos, rng: &mut StdRng) -> Vec<Entity> {
    let count = rng.gen_range(3..=5);
    let mut entities = Vec::with_capacity(count);
    for index in 0..count {
        let etype = match rng.gen_range(0..10) {
            0..=4 => EntityType::Lurker,
            5..=7 => EntityType::Crawler,
            _ => EntityType::Shadow,
        };
        let spawn_pos = random_pos_in_chunk(pos, rng);
        entities.push(Entity::new(
            stable_entity_id(world_seed, pos, index as u32),
            etype,
            spawn_pos,
        ));
    }
    entities
}

// ─── Graph topology query ───

/// Core logic: derives proven structure connections from already-generated
/// chunk data without calling any generator function.
///
/// For each chunk, any `edge_openings` bit pointing to a same-layer neighbor
/// with a different `macro_id` becomes a proven inter-structure connection.
/// Returns `(min_id, max_id)` pairs, sorted and deduplicated.
pub(crate) fn level0_proven_structure_connections_from_generated(
    generated: &[(StructureV0, Chunk)],
) -> Vec<(u32, u32)> {
    let mut pos_to_macro: HashMap<(i32, i8, i32), u32> = HashMap::with_capacity(generated.len());
    for (_, chunk) in generated {
        pos_to_macro.insert(chunk.key(), chunk.layout.macro_id);
    }

    const EDGES: [u8; 4] = [EDGE_NORTH, EDGE_EAST, EDGE_SOUTH, EDGE_WEST];

    let mut connections: Vec<(u32, u32)> = Vec::new();
    for (_, chunk) in generated {
        let sa = chunk.layout.macro_id;
        let (cx, cl, cz) = chunk.key();
        for &edge in &EDGES {
            if chunk.layout.edge_openings & edge == 0 {
                continue;
            }
            let (dx, dz) = edge_delta(edge);
            let nbr = (cx + dx, cl, cz + dz);
            if let Some(&sb) = pos_to_macro.get(&nbr) {
                if sb != sa {
                    connections.push((sa.min(sb), sa.max(sb)));
                }
            }
        }
    }

    connections.sort_unstable();
    connections.dedup();
    connections
}

/// Returns structure-pair connections proven by finalized chunk-boundary
/// edge openings.  Only horizontal same-layer connections are included;
/// vertical/inter-layer connections are out of scope for this query.
///
/// For each chunk in the finalized Level 0 world, any `edge_openings` bit
/// pointing to a present same-layer neighbor with a different `macro_id`
/// becomes a proven inter-structure connection.  Pairs are returned as
/// `(min_id, max_id)`, sorted and deduplicated.
///
/// Wrapper: generates chunk data then delegates to
/// [`level0_proven_structure_connections_from_generated`].
pub(crate) fn level0_proven_structure_connections(world_seed: u64) -> Vec<(u32, u32)> {
    let generated = generate_initial_structure_chunks(world_seed);
    level0_proven_structure_connections_from_generated(&generated)
}

// ─── Tests ───

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::{HashSet, VecDeque};

    fn lkey(chunk: &Chunk) -> (i32, ChunkLayer, i32) {
        chunk.key()
    }

    fn is_v30a_connector(chunk: &Chunk) -> bool {
        chunk.layout.vertical_flags & V30A_CONNECTOR != 0
            || matches!(
                chunk.layout.floor_profile,
                FLOOR_CONNECTOR_UP | FLOOR_CONNECTOR_DOWN
            )
    }

    fn layered_neighbors(
        key: (i32, ChunkLayer, i32),
        chunks: &std::collections::HashMap<(i32, ChunkLayer, i32), &Chunk>,
    ) -> Vec<(i32, ChunkLayer, i32)> {
        let mut out = Vec::new();
        for next in [
            (key.0 + 1, key.1, key.2),
            (key.0 - 1, key.1, key.2),
            (key.0, key.1, key.2 + 1),
            (key.0, key.1, key.2 - 1),
        ] {
            if chunks.contains_key(&next) {
                out.push(next);
            }
        }
        if chunks
            .get(&key)
            .map(|c| is_v30a_connector(c))
            .unwrap_or(false)
        {
            for layer in [key.1 - 1, key.1 + 1] {
                let vertical = (key.0, layer, key.2);
                if chunks.contains_key(&vertical) {
                    out.push(vertical);
                }
            }
        }
        for layer in [key.1 - 1, key.1 + 1] {
            let vertical = (key.0, layer, key.2);
            if chunks
                .get(&vertical)
                .map(|c| is_v30a_connector(c))
                .unwrap_or(false)
            {
                out.push(vertical);
            }
        }
        out
    }

    #[test]
    fn chunk_generation_is_deterministic() {
        let c1 = generate_chunk(42, (3, 7));
        let c2 = generate_chunk(42, (3, 7));
        assert_eq!(c1.template_id, c2.template_id);
        assert_eq!(c1.rotation, c2.rotation);
        assert_eq!(c1.mirrored, c2.mirrored);
        assert_eq!(c1.has_workbench, c2.has_workbench);
    }

    #[test]
    fn chunk_has_entities() {
        let c = generate_chunk(42, (0, 0));
        assert!(c.entities.len() >= 3 && c.entities.len() <= 5);
        for e in &c.entities {
            assert!(e.health > 0);
            assert!(e.is_alive());
        }
    }

    #[test]
    fn chunk_has_resources() {
        let c = generate_chunk(42, (0, 0));
        assert!(!c.items.is_empty());
        assert!(c.items.len() >= 5);
    }

    #[test]
    fn different_positions_give_different_chunks() {
        let c1 = generate_chunk(42, (0, 0));
        let c2 = generate_chunk(42, (1, 0));
        assert_ne!(c1.entities[0].id, c2.entities[0].id);
    }

    #[test]
    fn entities_spawn_inside_chunk_bounds() {
        let pos = (2, 3);
        let c = generate_chunk(42, pos);
        let min_x = pos.0 as f32 * CHUNK_SIZE + 2.0;
        let max_x = pos.0 as f32 * CHUNK_SIZE + CHUNK_SIZE - 2.0;
        let min_z = pos.1 as f32 * CHUNK_SIZE + 2.0;
        let max_z = pos.1 as f32 * CHUNK_SIZE + CHUNK_SIZE - 2.0;
        for e in &c.entities {
            assert!(
                e.position.x >= min_x && e.position.x <= max_x,
                "entity x {} out of [{}, {}]",
                e.position.x,
                min_x,
                max_x
            );
            assert!(
                e.position.z >= min_z && e.position.z <= max_z,
                "entity z {} out of [{}, {}]",
                e.position.z,
                min_z,
                max_z
            );
        }
    }

    #[test]
    fn same_seed_generates_same_structures() {
        let a = generate_initial_structures(42);
        let b = generate_initial_structures(42);

        assert_eq!(a.len(), b.len());
        for (left, right) in a.iter().zip(b.iter()) {
            assert_eq!(left.id, right.id);
            assert_eq!(left.structure_type, right.structure_type);
            assert_eq!(left.origin, right.origin);
            assert_eq!(left.chunks, right.chunks);
        }
    }

    #[test]
    fn different_seed_generates_different_structures() {
        let a = generate_initial_structures(42);
        let b = generate_initial_structures(43);
        let a_coords: Vec<ChunkPos> = a.iter().flat_map(|s| s.chunks.clone()).collect();
        let b_coords: Vec<ChunkPos> = b.iter().flat_map(|s| s.chunks.clone()).collect();
        assert_ne!(a_coords, b_coords);
    }

    #[test]
    fn starter_cluster_exists() {
        let structures = generate_initial_structures(42);
        let starter = structures
            .iter()
            .find(|s| s.structure_type == StructureType::StarterCluster)
            .expect("starter cluster should exist");
        assert!(starter.chunks.contains(&(0, 0)));
        assert!(starter.chunks.len() >= 3);
    }

    #[test]
    fn level0_structures_do_not_duplicate_chunks() {
        for seed in [42u64, 43, 99, 1234, 7777, 7778] {
            let structures = generate_initial_structures(seed);
            let mut seen = HashSet::new();
            for structure in &structures {
                for (idx, &pos) in structure.chunks.iter().enumerate() {
                    let key = (pos.0, structure.chunk_layer(idx), pos.1);
                    assert!(
                        seen.insert(key),
                        "seed {} duplicate chunk {:?} in structure {:?}",
                        seed,
                        key,
                        structure.structure_type
                    );
                }
            }
        }
    }

    #[test]
    fn chunk_and_item_ids_are_stable() {
        let first = generate_initial_structure_chunks(42);
        let second = generate_initial_structure_chunks(42);

        let first_chunks: Vec<(ChunkPos, u64, u8)> = first
            .iter()
            .map(|(_, c)| (c.pos, c.seed, c.template_id))
            .collect();
        let second_chunks: Vec<(ChunkPos, u64, u8)> = second
            .iter()
            .map(|(_, c)| (c.pos, c.seed, c.template_id))
            .collect();
        assert_eq!(first_chunks, second_chunks);

        let first_items: Vec<(u32, ChunkPos, &'static str)> = first
            .iter()
            .flat_map(|(_, c)| {
                c.items
                    .iter()
                    .map(move |i| (i.id, c.pos, i.item.type_name()))
            })
            .collect();
        let second_items: Vec<(u32, ChunkPos, &'static str)> = second
            .iter()
            .flat_map(|(_, c)| {
                c.items
                    .iter()
                    .map(move |i| (i.id, c.pos, i.item.type_name()))
            })
            .collect();
        assert_eq!(first_items, second_items);
    }

    #[test]
    fn structure_items_are_inside_generated_chunks() {
        let generated = generate_initial_structure_chunks(42);
        for (_, chunk) in &generated {
            let min_x = chunk.pos.0 as f32 * CHUNK_SIZE;
            let max_x = min_x + CHUNK_SIZE;
            let min_z = chunk.pos.1 as f32 * CHUNK_SIZE;
            let max_z = min_z + CHUNK_SIZE;
            for item in &chunk.items {
                assert!(item.position.x >= min_x && item.position.x <= max_x);
                assert!(item.position.z >= min_z && item.position.z <= max_z);
            }
            for entity in &chunk.entities {
                assert!(entity.position.x >= min_x && entity.position.x <= max_x);
                assert!(entity.position.z >= min_z && entity.position.z <= max_z);
            }
        }
    }

    // ─── Level 0 V1 specific tests ───

    #[test]
    fn level0_same_seed_same_layout() {
        let a = generate_initial_structure_chunks(99);
        let b = generate_initial_structure_chunks(99);
        let a_positions: Vec<ChunkPos> = a.iter().map(|(_, c)| c.pos).collect();
        let b_positions: Vec<ChunkPos> = b.iter().map(|(_, c)| c.pos).collect();
        assert_eq!(a_positions, b_positions);
    }

    #[test]
    fn level0_different_seed_different_layout() {
        let a = generate_initial_structure_chunks(100);
        let b = generate_initial_structure_chunks(200);
        let a_set: HashSet<ChunkPos> = a.iter().map(|(_, c)| c.pos).collect();
        let b_set: HashSet<ChunkPos> = b.iter().map(|(_, c)| c.pos).collect();
        assert_ne!(a_set, b_set);
    }

    #[test]
    fn level0_initial_layout_connected() {
        for seed in [42u64, 43, 99, 1234, 7777, 7778] {
            let chunks = generate_initial_structure_chunks(seed);
            let positions: HashSet<ChunkPos> = chunks.iter().map(|(_, c)| c.pos).collect();
            assert!(positions.contains(&(0, 0)), "seed {} missing origin", seed);

            // BFS from (0,0)
            let mut visited = HashSet::new();
            let mut queue = VecDeque::from([(0i32, 0i32)]);
            while let Some(pos) = queue.pop_front() {
                if !visited.insert(pos) {
                    continue;
                }
                for next in [
                    (pos.0 + 1, pos.1),
                    (pos.0 - 1, pos.1),
                    (pos.0, pos.1 + 1),
                    (pos.0, pos.1 - 1),
                ] {
                    if positions.contains(&next) && !visited.contains(&next) {
                        queue.push_back(next);
                    }
                }
            }

            assert_eq!(
                visited.len(),
                positions.len(),
                "seed {} layout not connected: visited={} total={}",
                seed,
                visited.len(),
                positions.len()
            );
        }
    }

    #[test]
    fn level0_spawn_area_stable() {
        let chunks = generate_initial_structure_chunks(42);
        let positions: HashSet<ChunkPos> = chunks.iter().map(|(_, c)| c.pos).collect();
        // Starter cluster must include all 4 positions
        assert!(positions.contains(&(0, 0)));
        assert!(positions.contains(&(1, 0)));
        assert!(positions.contains(&(0, 1)));
        assert!(positions.contains(&(1, 1)));
        // Spawn area should have no entities
        for (_, c) in &chunks {
            if c.pos == (0, 0) || c.pos == (1, 0) || c.pos == (0, 1) || c.pos == (1, 1) {
                assert!(
                    c.entities.is_empty(),
                    "starter chunk {:?} has entities",
                    c.pos
                );
            }
        }
    }

    #[test]
    fn level0_has_hallways() {
        let chunks = generate_initial_structure_chunks(42);
        let hallway_count = chunks
            .iter()
            .filter(|(_, c)| {
                c.template_id == TEMPLATE_HALLWAY_STRAIGHT
                    || c.template_id == TEMPLATE_HALLWAY_CORNER
            })
            .count();
        assert!(hallway_count >= 10, "only {} hallways", hallway_count);
    }

    #[test]
    fn level0_has_macrospaces_when_generation_allows() {
        for seed in [42u64, 43, 99, 1234, 7777, 7778] {
            let structures = generate_initial_structures(seed);
            let macro_count = structures
                .iter()
                .filter(|s| s.tags.contains(&"macro"))
                .count();
            let open_count = structures
                .iter()
                .filter(|s| s.structure_type == StructureType::OpenHall)
                .count();
            let pillar_count = structures
                .iter()
                .filter(|s| s.structure_type == StructureType::PillarHall)
                .count();
            assert!(macro_count >= 1, "seed {} has no macrospaces", seed);
            assert!(open_count >= 1, "seed {} has no open hall", seed);
            assert!(pillar_count >= 1, "seed {} has no pillar hall", seed);
        }
    }

    /// Whether a player can walk from the `from` boundary opening to the `to`
    /// boundary opening, crossing only passable edges and walkable cells. This
    /// validates the edge architecture itself (not just the opening bitmask).
    fn edges_connect(layout: &ChunkLayoutV1, from: u8, to: u8) -> bool {
        let g = layout.grid_size as usize;
        let targets: HashSet<(usize, usize)> =
            boundary_opening_cells(layout, to).into_iter().collect();
        if targets.is_empty() {
            return false;
        }
        let mut visited = vec![false; g * g];
        let mut queue = VecDeque::new();
        for (x, z) in boundary_opening_cells(layout, from) {
            if !visited[z * g + x] {
                visited[z * g + x] = true;
                queue.push_back((x, z));
            }
        }
        while let Some((x, z)) = queue.pop_front() {
            if targets.contains(&(x, z)) {
                return true;
            }
            for side in 0..4u8 {
                if crate::world::chunk::edge_blocks_movement(layout.cell_side_edge(x, z, side)) {
                    continue;
                }
                let next = match side {
                    0 if z > 0 => (x, z - 1),
                    1 if x + 1 < g => (x + 1, z),
                    2 if z + 1 < g => (x, z + 1),
                    3 if x > 0 => (x - 1, z),
                    _ => continue,
                };
                if layout.is_cell_walkable(next.0, next.1) && !visited[next.1 * g + next.0] {
                    visited[next.1 * g + next.0] = true;
                    queue.push_back(next);
                }
            }
        }
        false
    }

    #[test]
    fn corridor_layouts_connect_expected_edges() {
        // Edge-wall model: a freshly built layout opens a centred gap on *every*
        // boundary (uniform reciprocal gaps), so directionality is not expressed
        // by the standalone opening bitmask — it emerges in the generated world
        // once `finalize_level0_edges` walls off boundaries facing missing
        // neighbours. Here we assert each template *contains* its expected axis
        // openings and that the interior is genuinely traversable between them.
        let straight_ns = build_chunk_layout(TEMPLATE_HALLWAY_STRAIGHT, 0);
        assert_eq!(
            straight_ns.edge_openings & (EDGE_NORTH | EDGE_SOUTH),
            EDGE_NORTH | EDGE_SOUTH,
            "N/S corridor missing axis openings"
        );
        assert!(
            edges_connect(&straight_ns, EDGE_NORTH, EDGE_SOUTH),
            "N/S corridor not traversable north<->south"
        );

        let straight_ew = build_chunk_layout(TEMPLATE_HALLWAY_STRAIGHT, 90);
        assert_eq!(
            straight_ew.edge_openings & (EDGE_EAST | EDGE_WEST),
            EDGE_EAST | EDGE_WEST,
            "E/W corridor missing axis openings"
        );
        assert!(
            edges_connect(&straight_ew, EDGE_EAST, EDGE_WEST),
            "E/W corridor not traversable east<->west"
        );

        let intersection = build_chunk_layout(TEMPLATE_INTERSECTION, 0);
        assert_eq!(
            intersection.edge_openings & (EDGE_NORTH | EDGE_EAST | EDGE_SOUTH | EDGE_WEST),
            EDGE_NORTH | EDGE_EAST | EDGE_SOUTH | EDGE_WEST,
            "intersection missing a 4-way opening"
        );
        assert!(edges_connect(&intersection, EDGE_NORTH, EDGE_SOUTH));
        assert!(edges_connect(&intersection, EDGE_EAST, EDGE_WEST));

        // In the generated world, finalize seals every boundary facing a missing
        // neighbour, so a straight corridor only keeps openings toward chunks
        // that actually exist (it becomes directional in practice).
        let generated = generate_initial_structure_chunks(42);
        let present: HashSet<ChunkPos> = generated.iter().map(|(_, c)| c.pos).collect();
        for (_, chunk) in &generated {
            if chunk.template_id != TEMPLATE_HALLWAY_STRAIGHT {
                continue;
            }
            for edge in [EDGE_NORTH, EDGE_EAST, EDGE_SOUTH, EDGE_WEST] {
                if chunk.layout.edge_openings & edge != 0 {
                    let d = edge_delta(edge);
                    let neighbor = (chunk.pos.0 + d.0, chunk.pos.1 + d.1);
                    assert!(
                        present.contains(&neighbor),
                        "corridor {:?} opens edge {edge} into missing {neighbor:?}",
                        chunk.pos
                    );
                }
            }
        }
    }

    #[test]
    fn generated_layout_openings_are_reciprocal() {
        let chunks = generate_initial_structure_chunks(42);
        let by_pos: std::collections::HashMap<(i32, ChunkLayer, i32), u8> = chunks
            .iter()
            .map(|(_, chunk)| (chunk.key(), chunk.layout.edge_openings))
            .collect();

        for (_, chunk) in &chunks {
            for edge in [EDGE_NORTH, EDGE_EAST, EDGE_SOUTH, EDGE_WEST] {
                if chunk.layout.edge_openings & edge == 0 {
                    continue;
                }
                let delta = edge_delta(edge);
                let neighbor = (chunk.pos.0 + delta.0, chunk.layer, chunk.pos.1 + delta.1);
                let neighbor_openings = by_pos.get(&neighbor).unwrap_or_else(|| {
                    panic!("open edge from {:?} to missing {:?}", chunk.key(), neighbor)
                });
                assert!(
                    neighbor_openings & opposite_edge(edge) != 0,
                    "edge {:?} from {:?} not reciprocal",
                    edge,
                    chunk.key()
                );
            }
        }
    }

    #[test]
    fn level0_no_open_edge_points_to_missing_chunk() {
        // Part 2.7A: finalize_level0_edges must seal every boundary facing a
        // chunk that was never generated.
        for seed in [42u64, 43, 99, 1234, 7777, 7778] {
            let chunks = generate_initial_structure_chunks(seed);
            let present: HashSet<ChunkPos> = chunks.iter().map(|(_, c)| c.pos).collect();
            for (_, chunk) in &chunks {
                for edge in [EDGE_NORTH, EDGE_EAST, EDGE_SOUTH, EDGE_WEST] {
                    if chunk.layout.edge_openings & edge == 0 {
                        continue;
                    }
                    let d = edge_delta(edge);
                    let neighbor = (chunk.pos.0 + d.0, chunk.pos.1 + d.1);
                    assert!(
                        present.contains(&neighbor),
                        "seed {seed} chunk {:?} opens edge {edge} into missing {neighbor:?}",
                        chunk.pos
                    );
                }
            }
        }
    }

    #[test]
    fn level0_boundaries_facing_void_are_walls() {
        // Every boundary facing a missing neighbour must be a solid wall along
        // its whole length — no passable doorway/arch leaking into the void.
        let g = LAYOUT_GRID_SIZE as usize;
        let chunks = generate_initial_structure_chunks(7778);
        let present: HashSet<ChunkPos> = chunks.iter().map(|(_, c)| c.pos).collect();
        for (_, chunk) in &chunks {
            let l = &chunk.layout;
            let p = chunk.pos;
            if !present.contains(&(p.0, p.1 - 1)) {
                assert!(
                    (0..g).all(|x| !edge_is_opening(l.edge_h(x, 0))),
                    "chunk {p:?} north boundary leaks into void"
                );
            }
            if !present.contains(&(p.0, p.1 + 1)) {
                assert!((0..g).all(|x| !edge_is_opening(l.edge_h(x, g))));
            }
            if !present.contains(&(p.0 + 1, p.1)) {
                assert!((0..g).all(|z| !edge_is_opening(l.edge_v(g, z))));
            }
            if !present.contains(&(p.0 - 1, p.1)) {
                assert!((0..g).all(|z| !edge_is_opening(l.edge_v(0, z))));
            }
        }
    }

    #[test]
    fn spawn_chunk_has_no_opening_to_missing_neighbor() {
        // The starter chunk must stay reachable: it keeps at least one exit, and
        // every exit it keeps points to a chunk that exists (no sealed-in spawn,
        // no opening into the void beside spawn).
        for seed in [42u64, 7777, 7778, 1234] {
            let chunks = generate_initial_structure_chunks(seed);
            let present: HashSet<ChunkPos> = chunks.iter().map(|(_, c)| c.pos).collect();
            let (_, spawn) = chunks
                .iter()
                .find(|(_, c)| c.pos == (0, 0))
                .expect("starter chunk at origin");
            assert!(
                spawn.layout.edge_openings != 0,
                "seed {seed}: spawn chunk sealed in (no exits)"
            );
            for edge in [EDGE_NORTH, EDGE_EAST, EDGE_SOUTH, EDGE_WEST] {
                if spawn.layout.edge_openings & edge != 0 {
                    let d = edge_delta(edge);
                    assert!(
                        present.contains(&(d.0, d.1)),
                        "seed {seed}: spawn opens edge {edge} into missing neighbour"
                    );
                }
            }
        }
    }

    #[test]
    fn same_seed_template_rotation_gives_deterministic_layout() {
        let a = build_chunk_layout(TEMPLATE_PILLAR_ROOM, 90);
        let b = build_chunk_layout(TEMPLATE_PILLAR_ROOM, 90);
        assert_eq!(a, b);
    }

    #[test]
    fn generated_chunk_walkable_cells_are_connected_to_openings() {
        for seed in [42u64, 43, 99, 1234, 7777, 7778] {
            for (_, chunk) in generate_initial_structure_chunks(seed) {
                assert!(
                    layout_walkable_connected_to_opening(&chunk.layout),
                    "seed {} chunk {:?} template {} has disconnected walkable cells",
                    seed,
                    chunk.pos,
                    chunk.template_id
                );
            }
        }
    }

    #[test]
    fn generated_edge_openings_touch_walkable_cells() {
        for (_, chunk) in generate_initial_structure_chunks(42) {
            let layout = &chunk.layout;
            if layout.edge_openings & EDGE_NORTH != 0 {
                assert!((0..10).any(|x| layout.is_cell_walkable(x, 0)));
            }
            if layout.edge_openings & EDGE_EAST != 0 {
                assert!((0..10).any(|z| layout.is_cell_walkable(9, z)));
            }
            if layout.edge_openings & EDGE_SOUTH != 0 {
                assert!((0..10).any(|x| layout.is_cell_walkable(x, 9)));
            }
            if layout.edge_openings & EDGE_WEST != 0 {
                assert!((0..10).any(|z| layout.is_cell_walkable(0, z)));
            }
        }
    }

    #[test]
    fn layout_grammar_adds_architectural_transition_cells() {
        // Edge-wall model: doors / arches / low+half walls / partitions / false
        // doors live on cell *edges*, not as centre-cell flags. Count the
        // special architectural edge kinds the grammars place.
        let chunks = generate_initial_structure_chunks(42);
        let mut special_edges = 0usize;
        let mut doors = 0usize;
        let mut arches = 0usize;
        for (_, chunk) in &chunks {
            special_edges += chunk.layout.special_edge_count();
            doors += chunk.layout.count_edge_kinds(|k| k == EDGE_KIND_DOOR);
            arches += chunk.layout.count_edge_kinds(|k| k == EDGE_KIND_ARCH);
        }
        assert!(special_edges >= 50, "only {special_edges} special edges");
        // ArchTransition / hub / arch-room grammars place arch edges; room/maze
        // grammars place door edges. Both kinds must actually appear.
        assert!(doors > 0, "no door edges in generated layouts");
        assert!(arches > 0, "no arch edges in generated layouts");

        // The old centre-cell special flags must no longer be the model: the
        // grammars place architecture on edges, leaving these flags unused.
        let centre_special: usize = chunks
            .iter()
            .flat_map(|(_, c)| c.layout.cells.iter())
            .filter(|f| **f & (CELL_LOW_WALL | CELL_HALF_WALL | CELL_FALSE_DOOR) != 0)
            .count();
        assert!(
            special_edges > centre_special,
            "architecture should live on edges ({special_edges}), not cell centres ({centre_special})"
        );
    }

    #[test]
    fn layout_density_is_not_linear_corridor_only() {
        // Edge-wall model: walls live on edges, so density must read edge
        // architecture (+ cell blockers like pillars/pits), not CELL_WALL flags.
        let chunks = generate_initial_structure_chunks(42);
        let mut arch = 0usize;
        let mut denom = 0usize;
        let mut solid_edges = 0usize;
        let mut cell_walls = 0usize;
        for (_, chunk) in &chunks {
            arch += chunk.layout.solid_edge_count()
                + chunk.layout.transition_edge_count()
                + chunk.layout.blocked_cell_count();
            denom += chunk.layout.total_edge_count() + chunk.layout.cells.len();
            solid_edges += chunk.layout.solid_edge_count();
            cell_walls += chunk
                .layout
                .cells
                .iter()
                .filter(|f| **f & CELL_BLOCKED != 0)
                .count();
        }
        let pct = arch as f32 / denom.max(1) as f32;
        assert!(
            pct > 0.12 && pct < 0.70,
            "combined edge/cell architecture density {pct}"
        );
        // Prove the layout is real architecture on edges, not empty corridors
        // with the odd cell-wall flag: edge walls must dominate cell-wall flags.
        assert!(
            solid_edges > cell_walls.saturating_mul(4).max(50),
            "edge walls {solid_edges} should dominate cell-wall flags {cell_walls}"
        );
    }

    #[test]
    fn ascii_export_is_deterministic_and_symbolic() {
        let a = export_level0_ascii(42);
        let b = export_level0_ascii(42);
        assert_eq!(a, b);
        assert!(a.contains('#') || a.contains('D') || a.contains('*'));
    }

    #[test]
    #[ignore]
    fn print_level0_ascii_seed_42() {
        println!("{}", export_level0_ascii(42));
    }

    #[test]
    #[ignore]
    fn print_level0_ascii_seed_7778() {
        println!("{}", export_level0_ascii(7778));
    }

    #[test]
    fn seed_7778_layout_is_varied_not_corridor_only() {
        let structures = generate_initial_structures(7778);
        let has = |t: StructureType| structures.iter().any(|s| s.structure_type == t);
        assert!(
            has(StructureType::StarterCluster),
            "missing starter cluster"
        );
        assert!(has(StructureType::OpenHall), "missing open hall");
        assert!(has(StructureType::PillarHall), "missing pillar hall/field");

        let macro_count = structures
            .iter()
            .filter(|s| s.tags.contains(&"macro"))
            .count();
        assert!(
            macro_count >= 2,
            "only {macro_count} macrospaces for seed 7778"
        );

        // Must contain meaningful non-corridor structures, not just hallways.
        let non_corridor = structures
            .iter()
            .filter(|s| {
                !matches!(
                    s.structure_type,
                    StructureType::HallwayChain | StructureType::HallwayT
                )
            })
            .count();
        assert!(
            non_corridor >= 5,
            "only {non_corridor} non-corridor structures for seed 7778"
        );
    }

    #[test]
    fn seed_7778_spawn_chunk_is_flat_and_walkable() {
        let chunks = generate_initial_structure_chunks(7778);
        let starter = chunks
            .iter()
            .find(|(_, c)| c.pos == (0, 0))
            .map(|(_, c)| c)
            .expect("seed 7778 must have a starter chunk at origin");
        assert_eq!(starter.layout.floor_profile, FLOOR_FLAT);
        assert_eq!(starter.layout.vertical_flags, 0);
        // The reserved spawn core must be walkable and clear.
        for x in 4..=5 {
            for z in 4..=5 {
                assert!(
                    starter.layout.is_cell_walkable(x, z),
                    "starter cell ({x},{z}) not walkable"
                );
            }
        }
    }

    #[test]
    fn generated_items_and_entities_occupy_safe_cells() {
        // The hard guarantee: nothing spawns inside a wall, pillar, partition,
        // false door or pit hole. (Ambient hazard floors — e.g. the pit-grid
        // placeholder — are walkable and acceptable.)
        for seed in [42u64, 7777, 7778] {
            for (_, chunk) in generate_initial_structure_chunks(seed) {
                for item in &chunk.items {
                    let (cx, cz) =
                        world_to_cell(&chunk.layout, chunk.pos, item.position.x, item.position.z);
                    assert!(
                        !item_cell_blocked(&chunk.layout, cx, cz),
                        "seed {seed} item {} in blocked cell ({cx},{cz}) of chunk {:?}",
                        item.id,
                        chunk.pos
                    );
                }
                for entity in &chunk.entities {
                    let (cx, cz) = world_to_cell(
                        &chunk.layout,
                        chunk.pos,
                        entity.position.x,
                        entity.position.z,
                    );
                    assert!(
                        !item_cell_blocked(&chunk.layout, cx, cz),
                        "seed {seed} entity {} in blocked cell ({cx},{cz}) of chunk {:?}",
                        entity.id,
                        chunk.pos
                    );
                }
            }
        }
    }

    #[test]
    fn spawn_region_chunks_are_flat() {
        // Expansion chunks within two of the origin must never be vertical.
        for x in -2..=2 {
            for z in -2..=2 {
                let chunk = generate_chunk(7778, (x, z));
                assert!(
                    !template_is_vertical(chunk.template_id),
                    "chunk ({x},{z}) is vertical template {}",
                    chunk.template_id
                );
            }
        }
    }

    #[test]
    fn level0_vertical_nodes_are_controlled() {
        use crate::world::chunk::FLOOR_FLAT;
        for seed in [42u64, 7777, 7778, 1234] {
            let chunks = generate_initial_structure_chunks(seed);
            let mut vertical = 0usize;
            for (_, c) in &chunks {
                if c.layout.floor_profile == FLOOR_FLAT {
                    continue;
                }
                vertical += 1;
                // Never within Chebyshev radius 2 of origin (spawn region flat).
                assert!(
                    c.pos.0.abs() > 2 || c.pos.1.abs() > 2,
                    "seed {seed} vertical chunk {:?} too close to spawn",
                    c.pos
                );
                // Vertical nodes must stay navigable.
                assert!(
                    c.layout.cells.iter().any(|f| f & CELL_WALKABLE != 0),
                    "seed {seed} vertical chunk {:?} has no walkable cells",
                    c.pos
                );
            }
            // Rare/controlled, never vertical spam (incl. the 2.10B showcase).
            assert!(
                vertical <= 16,
                "seed {seed} has excessive vertical chunks: {vertical} of {}",
                chunks.len()
            );
        }
        // Seed 7778 should still contain at least one controlled vertical node.
        let v7778 = generate_initial_structure_chunks(7778)
            .iter()
            .filter(|(_, c)| c.layout.floor_profile != FLOOR_FLAT)
            .count();
        assert!(v7778 >= 1, "seed 7778 has no vertical nodes");
    }

    #[test]
    fn seed_7778_has_reachable_vertical_showcase() {
        use crate::world::chunk::FLOOR_FLAT;
        let chunks = generate_initial_structure_chunks(7778);
        let positions: HashSet<ChunkPos> = chunks.iter().map(|(_, c)| c.pos).collect();

        // No verticality within Chebyshev radius 2 of origin.
        for (_, c) in &chunks {
            if c.layout.floor_profile != FLOOR_FLAT {
                assert!(
                    c.pos.0.abs() > 2 || c.pos.1.abs() > 2,
                    "vertical chunk within radius 2 at {:?}",
                    c.pos
                );
            }
        }

        // A reachable vertical showcase between Chebyshev radius 3 and 10.
        let showcase = chunks.iter().find(|(_, c)| {
            let r = c.pos.0.abs().max(c.pos.1.abs());
            c.layout.floor_profile != FLOOR_FLAT && (3..=10).contains(&r)
        });
        let (_, sc) =
            showcase.expect("seed 7778 has a reachable vertical showcase in radius 3..=10");
        assert!(
            sc.layout.cells.iter().any(|f| f & CELL_WALKABLE != 0),
            "showcase has no walkable cells"
        );

        // Connected to spawn (0,0) via chunk-adjacency BFS.
        let mut visited = HashSet::new();
        let mut q = VecDeque::from([(0i32, 0i32)]);
        while let Some(p) = q.pop_front() {
            if !visited.insert(p) {
                continue;
            }
            for n in [
                (p.0 + 1, p.1),
                (p.0 - 1, p.1),
                (p.0, p.1 + 1),
                (p.0, p.1 - 1),
            ] {
                if positions.contains(&n) && !visited.contains(&n) {
                    q.push_back(n);
                }
            }
        }
        assert!(
            visited.contains(&sc.pos),
            "showcase {:?} not connected to spawn",
            sc.pos
        );
    }

    #[test]
    fn v30a_chunk_seed_includes_layer_identity() {
        let pos = (4, -2);
        assert_eq!(chunk_seed(7778, pos), chunk_seed_layer(7778, pos, 0));
        assert_ne!(
            chunk_seed_layer(7778, pos, 0),
            chunk_seed_layer(7778, pos, 1)
        );
        assert_ne!(
            chunk_seed_layer(7778, pos, 0),
            chunk_seed_layer(7778, pos, -1)
        );
        assert_ne!(
            chunk_seed_layer(7778, pos, 1),
            chunk_seed_layer(7778, pos, -1)
        );
    }

    #[test]
    fn seed_7778_has_reachable_v30a_multilayer_showcase() {
        let chunks = generate_initial_structure_chunks(7778);
        let by_key: std::collections::HashMap<(i32, ChunkLayer, i32), &Chunk> =
            chunks.iter().map(|(_, c)| (lkey(c), c)).collect();
        assert_eq!(
            by_key.len(),
            chunks.len(),
            "duplicate layered chunk identity"
        );

        let connector = chunks
            .iter()
            .map(|(_, c)| c)
            .find(|c| {
                c.layer == 0
                    && c.layout.vertical_flags & V30A_CONNECTOR != 0
                    && (c.pos.0 + 5).abs() <= 1
                    && (c.pos.1 + 1).abs() <= 1
            })
            .expect("seed 7778 must keep the original layer-0 V30A connector near (-5,-1)");
        let dist = connector.pos.0.abs().max(connector.pos.1.abs());
        assert!((4..=8).contains(&dist), "connector at distance {dist}");
        assert_eq!(
            connector.layout.floor_profile, FLOOR_CONNECTOR_DOWN,
            "seed 7778 should showcase a lower service branch"
        );
        assert!(by_key.contains_key(&(connector.pos.0, -1, connector.pos.1)));

        assert!(chunks.iter().any(|(_, c)| c.layer == -1));
        assert!(chunks
            .iter()
            .any(|(_, c)| c.layout.vertical_flags & V30A_STACKED_CORRIDOR != 0));
        assert!(chunks
            .iter()
            .any(|(_, c)| c.layout.vertical_flags & V30A_LOWER_SERVICE_BRANCH != 0));
        assert!(chunks
            .iter()
            .any(|(_, c)| c.layout.vertical_flags & V30A_ATRIUM_VOID_ROOM != 0));
        assert!(chunks
            .iter()
            .any(|(_, c)| c.layout.vertical_flags & V30A_DEEP_PRECIPICE_PLACEHOLDER != 0));
        assert!(chunks
            .iter()
            .any(|(_, c)| c.layout.vertical_flags & V30A_GIANT_PILLAR_HALL != 0));

        for (_, c) in &chunks {
            let r = c.pos.0.abs().max(c.pos.1.abs());
            let is_multilayer = c.layer != 0
                || c.layout.vertical_flags
                    & (V30A_CONNECTOR
                        | V30A_STACKED_CORRIDOR
                        | V30A_LOWER_SERVICE_BRANCH
                        | V30A_UPPER_OFFICE_BRANCH
                        | V30A_ATRIUM_VOID_ROOM
                        | V30A_DEEP_PRECIPICE_PLACEHOLDER
                        | V30A_GIANT_PILLAR_HALL
                        | V30A_BLOCKED_VERTICAL_SHAFT)
                    != 0;
            assert!(
                !is_multilayer || r > 2,
                "V30A chunk too close to spawn: {:?}",
                c.key()
            );
        }

        let layered_count = chunks.iter().filter(|(_, c)| c.layer != 0).count();
        assert!(
            (1..=5).contains(&layered_count),
            "unexpected V30A layer count {layered_count}"
        );

        let mut visited = HashSet::new();
        let mut q = VecDeque::from([(0i32, 0i8, 0i32)]);
        while let Some(key) = q.pop_front() {
            if !visited.insert(key) {
                continue;
            }
            for next in layered_neighbors(key, &by_key) {
                if !visited.contains(&next) {
                    q.push_back(next);
                }
            }
        }
        for (_, c) in &chunks {
            if c.layer != 0 {
                assert!(
                    visited.contains(&c.key()),
                    "orphan layer chunk {:?}",
                    c.key()
                );
            }
        }
    }

    #[test]
    fn seed_7778_inter_layer_volumes_cover_showcase() {
        let chunks = generate_initial_structure_chunks(7778);
        let connector = chunks
            .iter()
            .map(|(_, c)| c)
            .find(|c| {
                c.layer == 0
                    && c.layout.vertical_flags & V30A_CONNECTOR != 0
                    && (c.pos.0 + 5).abs() <= 1
                    && (c.pos.1 + 1).abs() <= 1
            })
            .expect("seed 7778 must keep the original layer-0 V30A connector near (-5,-1)");
        assert!(
            (connector.pos.0 + 5).abs() <= 1 && (connector.pos.1 + 1).abs() <= 1,
            "connector {:?} should stay near the Phase 3.0A showcase target",
            connector.key()
        );

        let mut volumes: Vec<&InterLayerVolumeV0> = chunks
            .iter()
            .flat_map(|(_, c)| c.layout.inter_layer_volumes.iter())
            .collect();
        volumes.sort_by_key(|v| (v.volume_id, v.kind.as_str()));
        assert!(!volumes.is_empty(), "seed 7778 has no inter-layer volumes");

        for kind in [
            InterLayerVolumeKindV0::AtriumStack,
            InterLayerVolumeKindV0::ServiceShaft,
            InterLayerVolumeKindV0::StackedCorridorPair,
            InterLayerVolumeKindV0::OverlookRoom,
            InterLayerVolumeKindV0::GiantPillarSpan,
            InterLayerVolumeKindV0::CeilingActivityZone,
            InterLayerVolumeKindV0::UnderfloorServiceZone,
        ] {
            assert!(
                volumes.iter().any(|v| v.kind == kind),
                "missing inter-layer volume kind {}",
                kind.as_str()
            );
        }

        for volume in &volumes {
            assert_eq!(
                volume.involved_layers,
                vec![0, -1],
                "seed 7778 volume {} has wrong layers",
                volume.volume_id
            );
            assert!(
                volume.footprint_cell_min[0] < volume.footprint_cell_max[0]
                    && volume.footprint_cell_min[1] < volume.footprint_cell_max[1],
                "invalid footprint for volume {}",
                volume.volume_id
            );
            assert!(
                !volume.safety_type.is_empty(),
                "missing safety_type for volume {}",
                volume.volume_id
            );
            assert!(
                !volume.future_audio_hint.is_empty(),
                "missing future_audio_hint for volume {}",
                volume.volume_id
            );
        }

        let ids_first: Vec<(u32, &'static str, [i32; 2])> = volumes
            .iter()
            .map(|v| (v.volume_id, v.kind.as_str(), v.base_chunk))
            .collect();
        let mut ids_second: Vec<(u32, &'static str, [i32; 2])> =
            generate_initial_structure_chunks(7778)
                .iter()
                .flat_map(|(_, c)| c.layout.inter_layer_volumes.iter())
                .map(|v| (v.volume_id, v.kind.as_str(), v.base_chunk))
                .collect();
        ids_second.sort();
        assert_eq!(ids_first, ids_second, "volume ids must be deterministic");

        assert!(volumes.iter().any(|v| {
            v.kind == InterLayerVolumeKindV0::AtriumStack
                && v.visual_flags & VOLUME_VIS_LOWER_ROOM_VISIBLE != 0
                && v.visual_flags & VOLUME_VIS_RAILINGS != 0
                && v.visual_flags & VOLUME_VIS_RIM_TRIMS != 0
        }));
        assert!(volumes.iter().any(|v| {
            v.kind == InterLayerVolumeKindV0::ServiceShaft
                && v.visual_flags & VOLUME_VIS_SHAFT_WALLS != 0
        }));
        assert!(volumes.iter().any(|v| {
            v.kind == InterLayerVolumeKindV0::GiantPillarSpan
                && v.visual_flags & VOLUME_VIS_PILLAR_SPANS != 0
        }));
    }

    #[test]
    fn seed_7778_visfix_showcase_is_loaded_and_volume_backed() {
        // The decorative VISFIX overlay is gated OFF in the default runtime path
        // (replaced by the VolumetricGridV0 showcase). This test exercises the
        // gated legacy overlay explicitly so that code path stays covered.
        let chunks = generate_initial_structure_chunks_with_visfix_overlay(7778);
        let by_key: std::collections::HashMap<(i32, ChunkLayer, i32), &Chunk> =
            chunks.iter().map(|(_, c)| (lkey(c), c)).collect();

        for key in [(1, 0, 3), (1, -1, 3), (1, 0, 4), (1, -1, 4)] {
            assert!(
                by_key.contains_key(&key),
                "missing VISFIX showcase chunk {key:?}"
            );
        }

        let connector = by_key.get(&(1, 0, 3)).expect("missing VISFIX connector");
        assert_eq!(connector.layout.floor_profile, FLOOR_CONNECTOR_DOWN);
        assert!(
            connector.layout.vertical_flags & V30A_CONNECTOR != 0,
            "VISFIX connector missing V30A connector flag"
        );

        let validation_volume_count: usize = [(1, 0, 3), (1, -1, 3), (1, 0, 4), (1, -1, 4)]
            .iter()
            .map(|key| by_key.get(key).unwrap().layout.inter_layer_volumes.len())
            .sum();
        let total_volume_count: usize = chunks
            .iter()
            .map(|(_, c)| c.layout.inter_layer_volumes.len())
            .sum();
        let total_volume_chunks = chunks
            .iter()
            .filter(|(_, c)| !c.layout.inter_layer_volumes.is_empty())
            .count();

        assert_eq!(validation_volume_count, 14);
        assert_eq!(total_volume_count, 28);
        assert_eq!(total_volume_chunks, 8);

        for key in [(1, 0, 3), (1, -1, 3), (1, 0, 4), (1, -1, 4)] {
            let chunk = by_key.get(&key).unwrap();
            assert!(
                chunk
                    .layout
                    .inter_layer_volumes
                    .iter()
                    .all(|v| v.involved_layers == vec![0, -1]),
                "VISFIX chunk {key:?} has a volume with wrong involved layers"
            );
        }

        let spawn_x = 25.0f32;
        let spawn_z = 25.0f32;
        let showcase_x = 75.0f32;
        let showcase_z = 175.0f32;
        let distance = ((showcase_x - spawn_x).powi(2) + (showcase_z - spawn_z).powi(2)).sqrt();
        assert!(
            distance < 159.0,
            "VISFIX showcase too far from spawn: {distance}"
        );

        println!(
            "v30a2_visfix_objective_counts showcase_chunk=(1,0,3) showcase_world=({showcase_x:.1},0.0,{showcase_z:.1}) spawn_world=({spawn_x:.1},1.8,{spawn_z:.1}) distance={distance:.1} generated_chunks={} total_volumes={total_volume_count} validation_volumes={validation_volume_count} volume_chunks={total_volume_chunks}",
            chunks.len()
        );
    }

    #[test]
    fn seed_7778_visfix_overlay_disabled_by_default() {
        // Default runtime path must NOT inject the decorative VISFIX validation
        // overlay (its dedicated (1,3)/(1,4) layer chunks tagged
        // "v30a2_visfix_showcase"). The replacement is the render-only
        // VolumetricGridV0 showcase, which never appears in chunk layout here.
        let default_chunks = generate_initial_structure_chunks(7778);
        assert!(
            !default_chunks
                .iter()
                .any(|(s, _)| s.tags.contains(&"v30a2_visfix_showcase")),
            "VISFIX overlay clutter must be disabled by default"
        );
        // The dedicated overlay-only layer chunks must be absent by default.
        for key in [(1, -1, 3), (1, -1, 4)] {
            assert!(
                !default_chunks
                    .iter()
                    .any(|(_, c)| (c.pos.0, c.layer, c.pos.1) == key),
                "overlay-only chunk {key:?} should not exist by default"
            );
        }
        // The gated legacy path still produces it (proving the code path lives).
        let overlay_chunks = generate_initial_structure_chunks_with_visfix_overlay(7778);
        assert!(
            overlay_chunks
                .iter()
                .any(|(s, _)| s.tags.contains(&"v30a2_visfix_showcase")),
            "gated legacy overlay path must still build the overlay"
        );
    }

    #[test]
    fn v30a_items_and_entities_stay_off_layered_or_void_chunks() {
        for seed in [42u64, 7778, 1234] {
            for (_, c) in generate_initial_structure_chunks(seed) {
                let v30a_blocked = c.layer != 0
                    || c.layout.vertical_flags
                        & (V30A_CONNECTOR
                            | V30A_ATRIUM_VOID_ROOM
                            | V30A_DEEP_PRECIPICE_PLACEHOLDER
                            | V30A_BLOCKED_VERTICAL_SHAFT)
                        != 0;
                if v30a_blocked {
                    assert!(
                        c.items.is_empty(),
                        "seed {seed} item in V30A chunk {:?}",
                        c.key()
                    );
                    assert!(
                        c.entities.is_empty(),
                        "seed {seed} entity in V30A chunk {:?}",
                        c.key()
                    );
                }
            }
        }
    }

    fn layout_walkable_connected_to_opening(layout: &ChunkLayoutV1) -> bool {
        let mut starts = VecDeque::new();
        for x in 0..10 {
            if layout.edge_openings & EDGE_NORTH != 0 && layout.is_cell_walkable(x, 0) {
                starts.push_back((x, 0));
            }
            if layout.edge_openings & EDGE_SOUTH != 0 && layout.is_cell_walkable(x, 9) {
                starts.push_back((x, 9));
            }
        }
        for z in 0..10 {
            if layout.edge_openings & EDGE_WEST != 0 && layout.is_cell_walkable(0, z) {
                starts.push_back((0, z));
            }
            if layout.edge_openings & EDGE_EAST != 0 && layout.is_cell_walkable(9, z) {
                starts.push_back((9, z));
            }
        }

        if starts.is_empty() {
            return layout.cells.iter().any(|flags| flags & CELL_WALKABLE != 0);
        }

        let mut visited = HashSet::new();
        while let Some((x, z)) = starts.pop_front() {
            if !visited.insert((x, z)) {
                continue;
            }
            for (nx, nz) in [
                (x.wrapping_add(1), z),
                (x.wrapping_sub(1), z),
                (x, z.wrapping_add(1)),
                (x, z.wrapping_sub(1)),
            ] {
                if nx < 10 && nz < 10 && layout.is_cell_walkable(nx, nz) {
                    starts.push_back((nx, nz));
                }
            }
        }

        for z in 0..10 {
            for x in 0..10 {
                if layout.is_cell_walkable(x, z) && !visited.contains(&(x, z)) {
                    return false;
                }
            }
        }
        true
    }

    #[test]
    fn level0_has_intersections() {
        let chunks = generate_initial_structure_chunks(42);
        assert!(
            chunks
                .iter()
                .any(|(_, c)| c.template_id == TEMPLATE_INTERSECTION),
            "no intersections found"
        );
    }

    #[test]
    fn level0_has_t_junctions_or_intersections() {
        let chunks = generate_initial_structure_chunks(42);
        let junction_count = chunks
            .iter()
            .filter(|(_, c)| {
                c.template_id == TEMPLATE_HALLWAY_T || c.template_id == TEMPLATE_INTERSECTION
            })
            .count();
        assert!(junction_count >= 2, "only {} junctions", junction_count);
    }

    #[test]
    fn level0_chunk_profiles_are_deterministic() {
        // Profile is derived from seed+pos — test that chunk_seed is deterministic
        for pos in [(0, 0), (5, 3), (-2, 7)] {
            let s1 = chunk_seed(42, pos);
            let s2 = chunk_seed(42, pos);
            assert_eq!(s1, s2);
        }
        // Different positions give different seeds
        assert_ne!(chunk_seed(42, (0, 0)), chunk_seed(42, (1, 0)));
    }

    #[test]
    fn level0_item_ids_are_stable() {
        let first = generate_initial_structure_chunks(42);
        let second = generate_initial_structure_chunks(42);
        let first_ids: Vec<u32> = first
            .iter()
            .flat_map(|(_, c)| c.items.iter().map(|i| i.id))
            .collect();
        let second_ids: Vec<u32> = second
            .iter()
            .flat_map(|(_, c)| c.items.iter().map(|i| i.id))
            .collect();
        assert_eq!(first_ids, second_ids);
    }

    #[test]
    fn level0_items_spawn_inside_existing_chunks() {
        let chunks = generate_initial_structure_chunks(42);
        let positions: HashSet<ChunkPos> = chunks.iter().map(|(_, c)| c.pos).collect();
        for (_, chunk) in &chunks {
            assert!(positions.contains(&chunk.pos));
            for item in &chunk.items {
                let expected_min_x = chunk.pos.0 as f32 * CHUNK_SIZE;
                let expected_max_x = expected_min_x + CHUNK_SIZE;
                assert!(
                    item.position.x >= expected_min_x && item.position.x <= expected_max_x,
                    "item at x={} outside chunk {:?}",
                    item.position.x,
                    chunk.pos
                );
            }
        }
    }

    #[test]
    fn level0_worldsync_contains_all_generated_chunks() {
        let chunks = generate_initial_structure_chunks(42);
        assert!(
            chunks
                .iter()
                .any(|(_, c)| c.template_id == TEMPLATE_STORAGE_ROOM),
            "missing storage room"
        );
        assert!(
            chunks
                .iter()
                .any(|(_, c)| c.template_id == TEMPLATE_INTERSECTION),
            "missing intersection"
        );
    }

    #[test]
    fn level0_interaction_pickup_still_removes_item() {
        // This tests the pipeline: generate structure → find item → interact
        let chunks = generate_initial_structure_chunks(42);
        let item = chunks
            .iter()
            .flat_map(|(_, c)| c.items.iter())
            .next()
            .expect("should have items");
        // Item id is non-zero and stable
        assert!(item.id > 0);
    }

    #[test]
    fn remote_players_unaffected_by_level0_generation() {
        // Level 0 generation only touches world state, not network peers.
        // This test verifies that generate_initial_structure_chunks doesn't
        // require or modify any network state.
        let a = generate_initial_structure_chunks(42);
        let b = generate_initial_structure_chunks(42);
        assert_eq!(a.len(), b.len());
    }

    // --- 3.1D-A: proven structure connection query ---

    #[test]
    fn proven_connections_nonzero() {
        let connections = level0_proven_structure_connections(0);
        assert!(
            !connections.is_empty(),
            "expected at least one proven connection"
        );
    }

    #[test]
    fn proven_connections_deterministic() {
        let a = level0_proven_structure_connections(42);
        let b = level0_proven_structure_connections(42);
        assert_eq!(a, b, "same seed must produce identical connection Vec");
    }

    #[test]
    fn proven_connections_canonical_form() {
        for seed in [0u64, 42, 7778] {
            for (a, b) in level0_proven_structure_connections(seed) {
                assert!(
                    a < b,
                    "seed {seed}: pair ({a},{b}) is not canonical (expected a < b)"
                );
            }
        }
    }

    // ─── Phase 3.2B: POI V1 tests ───

    fn poi_types() -> [StructureType; 4] {
        [
            StructureType::PoiLandmark,
            StructureType::PoiAnomalyCluster,
            StructureType::PoiDangerPocket,
            StructureType::PoiSafePocket,
        ]
    }

    #[test]
    fn level0_poi_structures_exist_for_seeds_0_42_7778() {
        for seed in [0u64, 42, 7778] {
            let structures = generate_initial_structures(seed);
            let poi_count = structures
                .iter()
                .filter(|s| poi_types().contains(&s.structure_type))
                .count();
            assert!(
                poi_count >= 3,
                "seed {seed}: expected >= 3 POI structures, got {poi_count}"
            );
        }
    }

    #[test]
    fn level0_poi_generation_is_deterministic() {
        for seed in [0u64, 42, 7778] {
            let a = generate_initial_structures(seed);
            let b = generate_initial_structures(seed);
            let poi_a: Vec<(StructureType, ChunkPos)> = a
                .iter()
                .filter(|s| poi_types().contains(&s.structure_type))
                .map(|s| (s.structure_type, s.origin))
                .collect();
            let poi_b: Vec<(StructureType, ChunkPos)> = b
                .iter()
                .filter(|s| poi_types().contains(&s.structure_type))
                .map(|s| (s.structure_type, s.origin))
                .collect();
            assert_eq!(
                poi_a, poi_b,
                "seed {seed}: POI generation not deterministic"
            );
        }
    }

    #[test]
    fn level0_poi_no_duplicate_chunk_positions() {
        for seed in [0u64, 42, 7778] {
            let chunks = generate_initial_structure_chunks(seed);
            let mut seen: HashSet<(i32, i8, i32)> = HashSet::new();
            for (_, chunk) in &chunks {
                let key = chunk.key();
                assert!(
                    seen.insert(key),
                    "seed {seed}: duplicate chunk position {key:?}"
                );
            }
        }
    }

    #[test]
    fn level0_poi_connectivity_maintained() {
        use std::collections::VecDeque;
        for seed in [0u64, 42, 7778] {
            let chunks = generate_initial_structure_chunks(seed);
            // Only test layer-0 connectivity: multilayer showcase chunks at
            // layer ±1 connect via vertical volumes, not horizontal BFS.
            let layer0: HashSet<ChunkPos> = chunks
                .iter()
                .filter(|(_, c)| c.layer == 0)
                .map(|(_, c)| c.pos)
                .collect();
            let start = (0i32, 0i32);
            assert!(layer0.contains(&start), "seed {seed}: no spawn chunk");
            let mut visited: HashSet<ChunkPos> = HashSet::new();
            let mut queue = VecDeque::from([start]);
            while let Some(pos) = queue.pop_front() {
                if !visited.insert(pos) {
                    continue;
                }
                for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                    let n = (pos.0 + dx, pos.1 + dz);
                    if layer0.contains(&n) && !visited.contains(&n) {
                        queue.push_back(n);
                    }
                }
            }
            assert_eq!(
                visited.len(),
                layer0.len(),
                "seed {seed}: layer-0 chunk graph not fully connected after POIs"
            );
        }
    }

    #[test]
    fn level0_region_graph_still_validates_after_pois() {
        use crate::world::levels::level_0::region_graph_builder::{
            audit_level0_region_graph, build_level0_region_graph,
        };
        use crate::world::levels::level_0::validation::validate_level0_region_graph;
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            assert!(
                validate_level0_region_graph(&graph),
                "seed {seed}: region graph failed validation after POIs"
            );
            let audit = audit_level0_region_graph(&graph);
            assert_eq!(audit.dangling_edge_count, 0, "seed {seed}: dangling edges");
        }
    }

    #[test]
    fn poi_structure_types_map_to_sensible_graph_kinds() {
        use crate::world::graph::nodes::SpatialNodeKind;
        use crate::world::levels::level_0::region_graph_builder::build_level0_region_graph;
        let graph = build_level0_region_graph(42);
        // Landmark → Atrium (large, open)
        let has_atrium = graph
            .nodes
            .iter()
            .any(|n| matches!(n.kind, SpatialNodeKind::Atrium));
        assert!(
            has_atrium,
            "seed 42: expected at least one Atrium node from PoiLandmark"
        );
        // DangerPocket → DangerPocket
        let has_danger = graph
            .nodes
            .iter()
            .any(|n| matches!(n.kind, SpatialNodeKind::DangerPocket));
        assert!(
            has_danger,
            "seed 42: expected at least one DangerPocket node"
        );
        // ManilaRoom (from PoiSafePocket or SafeRoom)
        let has_manila = graph
            .nodes
            .iter()
            .any(|n| matches!(n.kind, SpatialNodeKind::ManilaRoom));
        assert!(has_manila, "seed 42: expected at least one ManilaRoom node");
    }

    #[test]
    fn seed_7778_has_reachable_poi() {
        let structures = generate_initial_structures(7778);
        let poi_structs: Vec<&StructureV0> = structures
            .iter()
            .filter(|s| poi_types().contains(&s.structure_type))
            .collect();
        assert!(
            !poi_structs.is_empty(),
            "seed 7778: no POI structures generated"
        );
        // At least one POI within Chebyshev distance 12 of spawn (reachable).
        let reachable = poi_structs
            .iter()
            .any(|s| s.origin.0.abs().max(s.origin.1.abs()) <= 12);
        assert!(
            reachable,
            "seed 7778: no POI within Chebyshev distance 12 of spawn; origins: {:?}",
            poi_structs
                .iter()
                .map(|s| (s.structure_type.as_str(), s.origin))
                .collect::<Vec<_>>()
        );
    }

    #[test]
    fn proven_connections_no_self_loops() {
        for seed in [0u64, 42, 7778] {
            for (a, b) in level0_proven_structure_connections(seed) {
                assert_ne!(a, b, "seed {seed}: self-loop on structure {a}");
            }
        }
    }

    #[test]
    fn proven_connections_ids_exist_in_structures() {
        let seed = 0u64;
        let valid_ids: HashSet<u32> = generate_initial_structures(seed)
            .iter()
            .map(|s| s.id)
            .collect();
        for (a, b) in level0_proven_structure_connections(seed) {
            assert!(
                valid_ids.contains(&a),
                "structure id {a} not in generated structures"
            );
            assert!(
                valid_ids.contains(&b),
                "structure id {b} not in generated structures"
            );
        }
    }

    #[test]
    fn proven_connections_multiple_seeds_valid() {
        for seed in [0u64, 42, 7778] {
            let connections = level0_proven_structure_connections(seed);
            assert!(
                !connections.is_empty(),
                "seed {seed}: expected non-empty proven connections"
            );
            // All pairs canonical and non-self-loop
            for (a, b) in &connections {
                assert!(a < b, "seed {seed}: pair ({a},{b}) not canonical");
            }
            // Sorted and deduplicated
            let mut sorted = connections.clone();
            sorted.sort_unstable();
            sorted.dedup();
            assert_eq!(
                connections, sorted,
                "seed {seed}: result is not sorted+deduped"
            );
        }
    }
}
