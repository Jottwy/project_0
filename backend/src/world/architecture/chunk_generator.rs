use rand::rngs::StdRng;
use rand::Rng;

use crate::utils::ChunkPos;
use crate::world::architecture::layout_grammars::{
    generate_layout_from_template, open_boundary_gaps, TEMPLATE_ARCH_ROOM, TEMPLATE_BLACKOUT_ZONE,
    TEMPLATE_CLEANING_AREA, TEMPLATE_HUMID_ZONE, TEMPLATE_MANILA_ROOM, TEMPLATE_OPEN_HALL,
    TEMPLATE_PILLAR_ROOM, TEMPLATE_PIT_ROOM_PLACEHOLDER, TEMPLATE_POI_ANOMALY,
    TEMPLATE_POI_DANGER_POCKET, TEMPLATE_POI_LANDMARK, TEMPLATE_POI_SAFE_POCKET,
    TEMPLATE_RED_ROOM_WARNING, TEMPLATE_STORAGE_ROOM,
};
use crate::world::architecture::surface_builder::perimeter_openings;
use crate::world::chunk::ChunkLayer;
use crate::world::chunk::{
    ChunkLayoutV1, CEILING_DAMAGED, CEILING_LOW_SERVICE, CEILING_NORMAL, CEILING_TALL_HALL,
    CELL_BLOCKED, CELL_WALL, FLOOR_FLAT, FLOOR_PIT_PLACEHOLDER, FLOOR_RAISED, FLOOR_RAMP_EAST_WEST,
    FLOOR_RAMP_NORTH_SOUTH, FLOOR_STAIRS_EAST_WEST, FLOOR_STAIRS_NORTH_SOUTH, FLOOR_SUNKEN,
    LAYOUT_CELL_SIZE, LIGHT_BLACKOUT, LIGHT_DIM, LIGHT_NORMAL, LIGHT_RED, LIGHT_WARM,
};

use crate::world::chunk::LAYOUT_GRID_SIZE;

static NEXT_ENTITY_ID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(1);

fn next_entity_id() -> u32 {
    NEXT_ENTITY_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

pub fn next_entity_id_pub() -> u32 {
    next_entity_id()
}

pub fn chunk_seed(world_seed: u64, pos: ChunkPos) -> u64 {
    let mut h = world_seed ^ 0x9E37_79B9_7F4A_7C15;
    h = h.wrapping_add((pos.0 as u64).wrapping_mul(0xFF51_AFD7_ED55_8CCD));
    h ^= h >> 33;
    h = h.wrapping_add((pos.1 as u64).wrapping_mul(0xC4CE_B9FE_1A85_EC53));
    h ^= h >> 29;
    h
}

pub fn chunk_seed_layer(world_seed: u64, pos: ChunkPos, layer: ChunkLayer) -> u64 {
    if layer == 0 {
        return chunk_seed(world_seed, pos);
    }

    let mut h = chunk_seed(world_seed, pos);
    h ^= ((layer as i64 as u64).wrapping_mul(0xD6E8_FD9D_AA28_2219)).rotate_left(17);
    h ^= h >> 31;
    h
}

pub fn build_chunk_layout(template_id: u8, rotation: u16) -> ChunkLayoutV1 {
    let size = LAYOUT_GRID_SIZE as usize;
    // Delegate base layout grammar to architecture module.
    let mut layout = generate_layout_from_template(template_id, rotation);

    open_boundary_gaps(&mut layout);
    layout.edge_openings = perimeter_openings(&layout);

    match template_id {
        TEMPLATE_OPEN_HALL | TEMPLATE_PILLAR_ROOM => {
            layout.ceiling_profile = CEILING_TALL_HALL;
        }
        TEMPLATE_STORAGE_ROOM => {
            layout.ceiling_profile = CEILING_LOW_SERVICE;
            layout.light_profile = LIGHT_DIM;
        }
        TEMPLATE_BLACKOUT_ZONE => {
            layout.light_profile = LIGHT_BLACKOUT;
            layout.anomaly_flags |= 1;
            layout.ceiling_profile = CEILING_DAMAGED;
        }
        TEMPLATE_HUMID_ZONE => {
            layout.light_profile = LIGHT_DIM;
            layout.floor_profile = FLOOR_SUNKEN;
            layout.vertical_flags |= 1;
        }
        TEMPLATE_MANILA_ROOM => {
            layout.light_profile = LIGHT_WARM;
            layout.floor_profile = FLOOR_RAISED;
            layout.vertical_flags |= 1 << 1;
        }
        TEMPLATE_RED_ROOM_WARNING => {
            layout.light_profile = LIGHT_RED;
            layout.anomaly_flags |= 1 << 1;
        }
        TEMPLATE_PIT_ROOM_PLACEHOLDER => {
            layout.floor_profile = FLOOR_PIT_PLACEHOLDER;
            layout.anomaly_flags |= 1 << 2;
            layout.vertical_flags |= 1 << 2;
        }
        TEMPLATE_ARCH_ROOM => {
            layout.floor_profile = if rotation % 180 == 0 {
                FLOOR_RAMP_NORTH_SOUTH
            } else {
                FLOOR_RAMP_EAST_WEST
            };
            layout.vertical_flags |= 1 << 3;
        }
        TEMPLATE_CLEANING_AREA => {
            layout.floor_profile = if rotation % 180 == 0 {
                FLOOR_STAIRS_NORTH_SOUTH
            } else {
                FLOOR_STAIRS_EAST_WEST
            };
            layout.vertical_flags |= 1 << 4;
            layout.light_profile = LIGHT_DIM;
            layout.ceiling_profile = CEILING_LOW_SERVICE;
        }
        TEMPLATE_POI_LANDMARK => {
            layout.ceiling_profile = CEILING_TALL_HALL;
            layout.light_profile = LIGHT_WARM;
            layout.floor_profile = FLOOR_FLAT;
        }
        TEMPLATE_POI_ANOMALY => {
            layout.light_profile = LIGHT_DIM;
            layout.anomaly_flags |= 1 << 3;
            layout.floor_profile = FLOOR_FLAT;
        }
        TEMPLATE_POI_DANGER_POCKET => {
            layout.light_profile = LIGHT_DIM;
            layout.ceiling_profile = CEILING_DAMAGED;
            layout.anomaly_flags |= 1;
            layout.floor_profile = FLOOR_FLAT;
        }
        TEMPLATE_POI_SAFE_POCKET => {
            layout.light_profile = LIGHT_WARM;
            layout.floor_profile = FLOOR_FLAT;
        }
        _ => {
            layout.floor_profile = FLOOR_FLAT;
            layout.ceiling_profile = CEILING_NORMAL;
            layout.light_profile = LIGHT_NORMAL;
        }
    }

    if layout.cells.len() != size * size {
        layout.cells.resize(size * size, CELL_BLOCKED | CELL_WALL);
    }
    layout.cell_size = LAYOUT_CELL_SIZE;
    layout.grid_size = LAYOUT_GRID_SIZE;
    layout
}

// ─── MIG-5a: deterministic ID helpers (moved from generator.rs) ───

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

pub(crate) fn stable_volume_id(
    world_seed: u64,
    pos: ChunkPos,
    layer: crate::world::chunk::ChunkLayer,
    index: u32,
) -> u32 {
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

pub(crate) fn fisher_yates(slice: &mut [usize], rng: &mut StdRng) {
    for i in (1..slice.len()).rev() {
        let j = rng.gen_range(0..=i);
        slice.swap(i, j);
    }
}
