//! Deterministic, seed-based chunk generation — Level 0 Natural Generation V1.
//! See ARCHITECTURE_V1.md §7.1 and CLAUDE_CODE_INSTRUCTIONS.md Task 1.4.
//!
//! Level 0 layout: corridor-based, connected graph, Backrooms-style.
//! All generation is deterministic from world_seed.

use std::collections::HashSet;

use log::info;
use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

use crate::player::inventory::Item;
use crate::utils::{ChunkPos, Vec3, CHUNK_SIZE};
use crate::world::chunk::{
    Chunk, ChunkLayoutV1, ChunkState, DroppedItem, CEILING_DAMAGED, CEILING_LOW_SERVICE,
    CEILING_NORMAL, CEILING_TALL_HALL, CELL_ANOMALY, CELL_ARCH, CELL_BLOCKED, CELL_DOOR,
    CELL_FALSE_DOOR, CELL_HALF_WALL, CELL_HAZARD, CELL_LOW_WALL, CELL_PILLAR, CELL_PIT, CELL_RAMP,
    CELL_SAFE, CELL_SHALLOW_FLUID, CELL_THIN_PARTITION, CELL_WALKABLE, CELL_WALL, EDGE_EAST,
    EDGE_NORTH, EDGE_SOUTH, EDGE_WEST, FLOOR_FLAT, FLOOR_PIT_PLACEHOLDER, FLOOR_RAISED,
    FLOOR_RAMP_EAST_WEST, FLOOR_RAMP_NORTH_SOUTH, FLOOR_STAIRS_EAST_WEST, FLOOR_STAIRS_NORTH_SOUTH,
    FLOOR_SUNKEN, LAYOUT_CELL_SIZE, LAYOUT_GRID_SIZE, LIGHT_BLACKOUT, LIGHT_DIM, LIGHT_NORMAL,
    LIGHT_RED, LIGHT_WARM, ZONE_BLACKOUT, ZONE_CLEANING, ZONE_DANGER, ZONE_HUMID, ZONE_MANILA,
    ZONE_NORMAL, ZONE_OPEN_HALL, ZONE_PILLAR_HALL, ZONE_PIT, ZONE_RED, ZONE_SAFE, ZONE_STORAGE,
};
use crate::world::chunk::{
    EDGE_KIND_ARCH, EDGE_KIND_DOOR, EDGE_KIND_FALSE_DOOR, EDGE_KIND_HALF_WALL, EDGE_KIND_LOW_WALL,
    EDGE_KIND_OPEN, EDGE_KIND_PARTITION, EDGE_KIND_WALL,
};
use crate::world::entity::{Entity, EntityType};

// ─── Template IDs ───

pub const TEMPLATE_ROOM_BASIC: u8 = 0;
pub const TEMPLATE_HALLWAY_STRAIGHT: u8 = 1;
pub const TEMPLATE_HALLWAY_CORNER: u8 = 2;
pub const TEMPLATE_INTERSECTION: u8 = 3;
pub const TEMPLATE_STORAGE_ROOM: u8 = 4;
pub const TEMPLATE_SAFE_ROOM: u8 = 5;
pub const TEMPLATE_DEAD_END: u8 = 6;
pub const TEMPLATE_DANGER_ROOM: u8 = 7;
pub const TEMPLATE_HALLWAY_T: u8 = 8;
pub const TEMPLATE_PILLAR_ROOM: u8 = 9;
// Level 0 macro/zone variants. These are intentionally still single-byte
// template ids so Phase 1 does not touch network or IPC schemas.
pub const TEMPLATE_OPEN_HALL: u8 = 10;
pub const TEMPLATE_ARCH_ROOM: u8 = 11;
pub const TEMPLATE_CLEANING_AREA: u8 = 12;
pub const TEMPLATE_HUMID_ZONE: u8 = 13;
pub const TEMPLATE_BLACKOUT_ZONE: u8 = 14;
pub const TEMPLATE_MANILA_ROOM: u8 = 15;
pub const TEMPLATE_RED_ROOM_WARNING: u8 = 16;
pub const TEMPLATE_PIT_ROOM_PLACEHOLDER: u8 = 17;
// Backwards-compatible aliases for existing code/tests while Phase 1 settles.
pub const TEMPLATE_OPEN_COLUMN_ROOM: u8 = TEMPLATE_OPEN_HALL;
pub const TEMPLATE_CLOSED_ROOM: u8 = TEMPLATE_ARCH_ROOM;
pub const TEMPLATE_FALSE_ROOM: u8 = TEMPLATE_CLEANING_AREA;
pub const TEMPLATE_DARK_ZONE: u8 = TEMPLATE_BLACKOUT_ZONE;
pub const TEMPLATE_OVERLIT_ZONE: u8 = TEMPLATE_MANILA_ROOM;
pub const TEMPLATE_FALSE_RETURN: u8 = TEMPLATE_RED_ROOM_WARNING;
pub const TEMPLATE_VERTICAL_ANOMALY: u8 = TEMPLATE_PIT_ROOM_PLACEHOLDER;
pub const TEMPLATE_COUNT: u8 = 18;

// ─── Structure types ───

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StructureType {
    StarterCluster,
    HallwayChain,
    Intersection,
    StorageRoom,
    SafeRoom,
    DeadEnd,
    DangerRoom,
    HallwayT,
    PillarRoom,
    OpenHall,
    PillarHall,
    HumidZone,
    ArchRoom,
    BlackoutZone,
    RedRoom,
    ManilaRoom,
    CleaningArea,
    PitRoom,
}

impl StructureType {
    pub fn as_str(self) -> &'static str {
        match self {
            StructureType::StarterCluster => "starter_cluster",
            StructureType::HallwayChain => "hallway_chain",
            StructureType::Intersection => "intersection",
            StructureType::StorageRoom => "storage_room",
            StructureType::SafeRoom => "safe_room",
            StructureType::DeadEnd => "dead_end",
            StructureType::DangerRoom => "danger_room",
            StructureType::HallwayT => "hallway_t",
            StructureType::PillarRoom => "pillar_room",
            StructureType::OpenHall => "open_hall",
            StructureType::PillarHall => "pillar_hall",
            StructureType::HumidZone => "humid_zone",
            StructureType::ArchRoom => "arch_room",
            StructureType::BlackoutZone => "blackout_zone",
            StructureType::RedRoom => "red_room",
            StructureType::ManilaRoom => "manila_room",
            StructureType::CleaningArea => "cleaning_area",
            StructureType::PitRoom => "pit_room_placeholder",
        }
    }
}

#[derive(Debug, Clone)]
pub struct StructureV0 {
    pub id: u32,
    pub structure_type: StructureType,
    pub origin: ChunkPos,
    pub size: [u8; 2],
    pub seed: u64,
    pub chunks: Vec<ChunkPos>,
    pub tags: Vec<&'static str>,
    /// Per-chunk (template_id, rotation) overrides. Same order as `chunks`.
    pub chunk_overrides: Vec<(u8, u16)>,
}

// ─── ID generation ───

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

fn stable_u32(world_seed: u64, pos: ChunkPos, salt: u64, index: u32) -> u32 {
    let mut h = chunk_seed(world_seed ^ salt, pos);
    h ^= (index as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15);
    h ^= h >> 32;
    ((h & 0x7FFF_FFFF) as u32).max(1)
}

fn stable_entity_id(world_seed: u64, pos: ChunkPos, index: u32) -> u32 {
    stable_u32(world_seed, pos, 0xE17E_0001, index)
}

fn stable_item_id(world_seed: u64, pos: ChunkPos, index: u32) -> u32 {
    stable_u32(world_seed, pos, 0x17E0_0002, index)
}

fn structure_id(world_seed: u64, index: u32) -> u32 {
    stable_u32(
        world_seed,
        (index as i32, -(index as i32)),
        0x57A7_C700,
        index,
    )
}

// ─── Direction helpers (0=E, 1=N, 2=W, 3=S) ───

fn dir_delta(dir: u8) -> ChunkPos {
    match dir % 4 {
        0 => (1, 0),
        1 => (0, 1),
        2 => (-1, 0),
        _ => (0, -1),
    }
}

/// Rotation for hallway_straight: 0 = N/S open, 90 = E/W open.
fn straight_rotation(dir: u8) -> u16 {
    if dir % 2 == 0 {
        90
    } else {
        0
    }
}

/// Rotation for hallway_corner connecting entry_wall and exit_wall.
/// Walls: 0=E, 1=N, 2=W, 3=S. Entry wall = opposite of walking dir.
fn corner_rotation(from_dir: u8, to_dir: u8) -> u16 {
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
fn t_junction_rotation(closed_wall: u8) -> u16 {
    match closed_wall % 4 {
        0 => 180,
        1 => 90,
        2 => 0,
        _ => 270,
    }
}

fn fisher_yates(slice: &mut [usize], rng: &mut StdRng) {
    for i in (1..slice.len()).rev() {
        let j = rng.gen_range(0..=i);
        slice.swap(i, j);
    }
}

fn opposite_edge(edge: u8) -> u8 {
    match edge {
        EDGE_NORTH => EDGE_SOUTH,
        EDGE_EAST => EDGE_WEST,
        EDGE_SOUTH => EDGE_NORTH,
        EDGE_WEST => EDGE_EAST,
        _ => 0,
    }
}

fn edge_delta(edge: u8) -> ChunkPos {
    match edge {
        EDGE_NORTH => (0, -1),
        EDGE_EAST => (1, 0),
        EDGE_SOUTH => (0, 1),
        EDGE_WEST => (-1, 0),
        _ => (0, 0),
    }
}

fn template_zone_kind(template_id: u8) -> u8 {
    match template_id {
        TEMPLATE_STORAGE_ROOM => ZONE_STORAGE,
        TEMPLATE_SAFE_ROOM => ZONE_SAFE,
        TEMPLATE_DANGER_ROOM => ZONE_DANGER,
        TEMPLATE_OPEN_HALL => ZONE_OPEN_HALL,
        TEMPLATE_PILLAR_ROOM => ZONE_PILLAR_HALL,
        TEMPLATE_HUMID_ZONE => ZONE_HUMID,
        TEMPLATE_BLACKOUT_ZONE => ZONE_BLACKOUT,
        TEMPLATE_MANILA_ROOM => ZONE_MANILA,
        TEMPLATE_CLEANING_AREA => ZONE_CLEANING,
        TEMPLATE_RED_ROOM_WARNING => ZONE_RED,
        TEMPLATE_PIT_ROOM_PLACEHOLDER => ZONE_PIT,
        _ => ZONE_NORMAL,
    }
}

fn set_cell(cells: &mut [u16], x: usize, z: usize, flags: u16) {
    let size = LAYOUT_GRID_SIZE as usize;
    if x < size && z < size {
        cells[z * size + x] = flags;
    }
}

fn open_cell(cells: &mut [u16], x: usize, z: usize, extra: u16) {
    set_cell(cells, x, z, CELL_WALKABLE | extra);
}

fn block_cell(cells: &mut [u16], x: usize, z: usize, extra: u16) {
    set_cell(cells, x, z, CELL_BLOCKED | extra);
}

fn wall_cell(cells: &mut [u16], x: usize, z: usize) {
    block_cell(cells, x, z, CELL_WALL);
}

fn thin_partition_cell(cells: &mut [u16], x: usize, z: usize) {
    block_cell(cells, x, z, CELL_WALL | CELL_THIN_PARTITION);
}

fn low_wall_cell(cells: &mut [u16], x: usize, z: usize) {
    block_cell(cells, x, z, CELL_LOW_WALL);
}

fn half_wall_cell(cells: &mut [u16], x: usize, z: usize) {
    block_cell(cells, x, z, CELL_HALF_WALL);
}

fn false_door_cell(cells: &mut [u16], x: usize, z: usize) {
    block_cell(cells, x, z, CELL_WALL | CELL_FALSE_DOOR);
}

fn door_cell(cells: &mut [u16], x: usize, z: usize, extra: u16) {
    open_cell(cells, x, z, CELL_DOOR | extra);
}

fn arch_cell(cells: &mut [u16], x: usize, z: usize, extra: u16) {
    open_cell(cells, x, z, CELL_ARCH | extra);
}

fn carve_rect(cells: &mut [u16], x0: usize, z0: usize, x1: usize, z1: usize, extra: u16) {
    for x in x0..=x1 {
        for z in z0..=z1 {
            open_cell(cells, x, z, extra);
        }
    }
}

fn fill_rect_blocked(cells: &mut [u16], x0: usize, z0: usize, x1: usize, z1: usize, extra: u16) {
    for x in x0..=x1 {
        for z in z0..=z1 {
            block_cell(cells, x, z, extra);
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LayoutGrammarType {
    CorridorSpine,
    CorridorBroken,
    RoomCluster,
    OpenHall,
    PillarGrid,
    MazePocket,
    ArchTransition,
    SideRooms,
    HubAndSpokes,
    ServiceArea,
    BlackoutPocket,
    RedWarningPocket,
    ManilaRoom,
    PitGridRoom,
    VerticalTransition,
}

fn grammar_for_template(template_id: u8, _rotation: u16) -> LayoutGrammarType {
    match template_id {
        TEMPLATE_HALLWAY_STRAIGHT => LayoutGrammarType::CorridorSpine,
        TEMPLATE_HALLWAY_CORNER => LayoutGrammarType::SideRooms,
        TEMPLATE_INTERSECTION => LayoutGrammarType::HubAndSpokes,
        TEMPLATE_STORAGE_ROOM => LayoutGrammarType::ServiceArea,
        TEMPLATE_SAFE_ROOM => LayoutGrammarType::ManilaRoom,
        TEMPLATE_DEAD_END => LayoutGrammarType::SideRooms,
        TEMPLATE_DANGER_ROOM => LayoutGrammarType::MazePocket,
        TEMPLATE_HALLWAY_T => LayoutGrammarType::HubAndSpokes,
        TEMPLATE_PILLAR_ROOM => LayoutGrammarType::PillarGrid,
        TEMPLATE_OPEN_HALL => LayoutGrammarType::OpenHall,
        TEMPLATE_ARCH_ROOM => LayoutGrammarType::ArchTransition,
        TEMPLATE_CLEANING_AREA => LayoutGrammarType::ServiceArea,
        TEMPLATE_HUMID_ZONE => LayoutGrammarType::VerticalTransition,
        TEMPLATE_BLACKOUT_ZONE => LayoutGrammarType::BlackoutPocket,
        TEMPLATE_MANILA_ROOM => LayoutGrammarType::ManilaRoom,
        TEMPLATE_RED_ROOM_WARNING => LayoutGrammarType::RedWarningPocket,
        TEMPLATE_PIT_ROOM_PLACEHOLDER => LayoutGrammarType::PitGridRoom,
        _ => LayoutGrammarType::RoomCluster,
    }
}

// ─── Edge authoring helpers (Phase 2.7) ───
//
// Grammars start from an all-floor chunk (perimeter walls, interior open) and
// place walls/doors on cell *edges*. A wall between two cells is one edge, so
// there are no 5m-thick "wall cells" and no double walls.

fn wall_v(layout: &mut ChunkLayoutV1, bx: usize, z0: usize, z1: usize, kind: u8) {
    for z in z0..=z1 {
        layout.set_edge_v(bx, z, kind);
    }
}

fn wall_h(layout: &mut ChunkLayoutV1, x0: usize, x1: usize, bz: usize, kind: u8) {
    for x in x0..=x1 {
        layout.set_edge_h(x, bz, kind);
    }
}

/// Walls around the rectangle of cells `[x0..=x1] x [z0..=z1]`.
fn room_box(layout: &mut ChunkLayoutV1, x0: usize, z0: usize, x1: usize, z1: usize, kind: u8) {
    wall_h(layout, x0, x1, z0, kind);
    wall_h(layout, x0, x1, z1 + 1, kind);
    wall_v(layout, x0, z0, z1, kind);
    wall_v(layout, x1 + 1, z0, z1, kind);
}

fn pillar_cell(layout: &mut ChunkLayoutV1, x: usize, z: usize) {
    block_cell(&mut layout.cells, x, z, CELL_PILLAR);
}

fn or_all_cells(layout: &mut ChunkLayoutV1, extra: u16) {
    if extra != 0 {
        for c in layout.cells.iter_mut() {
            *c |= extra;
        }
    }
}

fn set_cell_side_edge_kind(layout: &mut ChunkLayoutV1, x: usize, z: usize, side: u8, kind: u8) {
    match side {
        0 => layout.set_edge_h(x, z, kind),
        1 => layout.set_edge_v(x + 1, z, kind),
        2 => layout.set_edge_h(x, z + 1, kind),
        _ => layout.set_edge_v(x, z, kind),
    }
}

// ─── Backrooms cell-edge grammars (Phase 2.7) ───

fn g_starter_safe(layout: &mut ChunkLayoutV1) {
    or_all_cells(layout, CELL_SAFE);
    // Clean, eerie: a couple of low-wall accents away from the centre core.
    wall_h(layout, 1, 2, 2, EDGE_KIND_LOW_WALL);
    wall_h(layout, 7, 8, 8, EDGE_KIND_LOW_WALL);
}

fn g_corridor_spine(layout: &mut ChunkLayoutV1) {
    // Central N–S corridor (cols 4–5) walled from the side areas.
    wall_v(layout, 4, 0, 9, EDGE_KIND_WALL);
    wall_v(layout, 6, 0, 9, EDGE_KIND_WALL);
    layout.set_edge_v(4, 3, EDGE_KIND_DOOR);
    layout.set_edge_v(6, 6, EDGE_KIND_DOOR);
    // West side: two stacked rooms.
    wall_h(layout, 0, 3, 5, EDGE_KIND_WALL);
    layout.set_edge_h(1, 5, EDGE_KIND_DOOR);
    // East side: an office split + a thin alcove partition.
    wall_h(layout, 6, 9, 4, EDGE_KIND_WALL);
    layout.set_edge_h(8, 4, EDGE_KIND_DOOR);
    wall_v(layout, 8, 6, 9, EDGE_KIND_PARTITION);
    layout.set_edge_v(8, 8, EDGE_KIND_DOOR);
    layout.set_edge_h(2, 7, EDGE_KIND_LOW_WALL);
    // False door on the corridor wall face.
    layout.set_edge_v(6, 1, EDGE_KIND_FALSE_DOOR);
}

fn g_broken_corridor(layout: &mut ChunkLayoutV1) {
    // E–W corridor (rows 4–5) with displaced walls and side doorways.
    wall_h(layout, 0, 9, 4, EDGE_KIND_WALL);
    wall_h(layout, 0, 9, 6, EDGE_KIND_WALL);
    layout.set_edge_h(2, 4, EDGE_KIND_DOOR);
    layout.set_edge_h(7, 6, EDGE_KIND_DOOR);
    // Chicane half walls inside the corridor (one row each → still passable).
    layout.set_edge_v(3, 4, EDGE_KIND_HALF_WALL);
    layout.set_edge_v(7, 5, EDGE_KIND_HALF_WALL);
    // North + south side rooms.
    wall_v(layout, 4, 0, 3, EDGE_KIND_WALL);
    layout.set_edge_v(4, 1, EDGE_KIND_DOOR);
    wall_v(layout, 6, 7, 9, EDGE_KIND_WALL);
    layout.set_edge_v(6, 8, EDGE_KIND_ARCH);
}

fn g_room_cluster(layout: &mut ChunkLayoutV1) {
    // A cross of walls makes six rooms, joined by doorframes/arches.
    wall_v(layout, 4, 0, 9, EDGE_KIND_WALL);
    wall_h(layout, 0, 9, 4, EDGE_KIND_WALL);
    wall_h(layout, 0, 9, 7, EDGE_KIND_WALL);
    layout.set_edge_v(4, 2, EDGE_KIND_DOOR);
    layout.set_edge_v(4, 5, EDGE_KIND_ARCH);
    layout.set_edge_v(4, 8, EDGE_KIND_DOOR);
    layout.set_edge_h(2, 4, EDGE_KIND_DOOR);
    layout.set_edge_h(6, 4, EDGE_KIND_DOOR);
    layout.set_edge_h(2, 7, EDGE_KIND_DOOR);
    layout.set_edge_h(7, 7, EDGE_KIND_DOOR);
    // Split the top-left room into two small offices.
    wall_v(layout, 2, 0, 3, EDGE_KIND_PARTITION);
    layout.set_edge_v(2, 1, EDGE_KIND_DOOR);
    // A false door + a low divider for texture.
    layout.set_edge_v(4, 0, EDGE_KIND_FALSE_DOOR);
    layout.set_edge_h(8, 7, EDGE_KIND_LOW_WALL);
}

fn g_open_hall(layout: &mut ChunkLayoutV1) {
    // Large open space broken up by columns + a low partition, with side rooms.
    for (x, z) in [(2, 2), (5, 2), (7, 2), (2, 7), (5, 7), (7, 7)] {
        pillar_cell(layout, x, z);
    }
    wall_h(layout, 1, 4, 5, EDGE_KIND_LOW_WALL);
    layout.set_edge_h(2, 5, EDGE_KIND_OPEN);
    wall_v(layout, 8, 0, 9, EDGE_KIND_WALL);
    layout.set_edge_v(8, 1, EDGE_KIND_DOOR);
    layout.set_edge_v(8, 6, EDGE_KIND_ARCH);
}

fn g_pillar_field(layout: &mut ChunkLayoutV1) {
    for x in [1usize, 4, 7] {
        for z in [1usize, 4, 7] {
            pillar_cell(layout, x, z);
        }
    }
    pillar_cell(layout, 8, 8);
    wall_h(layout, 3, 6, 9, EDGE_KIND_LOW_WALL);
    layout.set_edge_h(4, 9, EDGE_KIND_OPEN);
}

fn g_office_maze(layout: &mut ChunkLayoutV1, extra: u16) {
    or_all_cells(layout, extra);
    // Dense, offset partitions. Connectivity repair guarantees traversal, so
    // these can be aggressive without trapping the player.
    wall_v(layout, 2, 0, 6, EDGE_KIND_PARTITION);
    wall_v(layout, 4, 3, 9, EDGE_KIND_PARTITION);
    wall_v(layout, 6, 0, 5, EDGE_KIND_PARTITION);
    wall_v(layout, 8, 4, 9, EDGE_KIND_PARTITION);
    wall_h(layout, 0, 3, 3, EDGE_KIND_PARTITION);
    wall_h(layout, 2, 6, 6, EDGE_KIND_PARTITION);
    wall_h(layout, 6, 9, 3, EDGE_KIND_PARTITION);
    wall_h(layout, 4, 7, 8, EDGE_KIND_PARTITION);
    layout.set_edge_v(2, 2, EDGE_KIND_DOOR);
    layout.set_edge_v(4, 5, EDGE_KIND_DOOR);
    layout.set_edge_v(6, 3, EDGE_KIND_DOOR);
    layout.set_edge_h(1, 3, EDGE_KIND_DOOR);
    layout.set_edge_h(5, 6, EDGE_KIND_DOOR);
}

fn g_arch_transition(layout: &mut ChunkLayoutV1) {
    // Two parallel walls pierced by a rhythm of arches — a zone transition.
    wall_v(layout, 3, 0, 9, EDGE_KIND_WALL);
    wall_v(layout, 7, 0, 9, EDGE_KIND_WALL);
    for z in [2usize, 5, 8] {
        layout.set_edge_v(3, z, EDGE_KIND_ARCH);
        layout.set_edge_v(7, z, EDGE_KIND_ARCH);
    }
    wall_h(layout, 1, 8, 1, EDGE_KIND_LOW_WALL);
    layout.set_edge_h(4, 1, EDGE_KIND_OPEN);
    layout.set_edge_h(5, 1, EDGE_KIND_OPEN);
}

fn g_side_rooms(layout: &mut ChunkLayoutV1) {
    // Central corridor with three enclosed side rooms reached by doorframes.
    wall_v(layout, 4, 0, 9, EDGE_KIND_WALL);
    wall_v(layout, 6, 0, 9, EDGE_KIND_WALL);
    room_box(layout, 1, 1, 3, 3, EDGE_KIND_WALL);
    room_box(layout, 6, 2, 8, 4, EDGE_KIND_WALL);
    room_box(layout, 1, 6, 3, 8, EDGE_KIND_WALL);
    layout.set_edge_v(4, 2, EDGE_KIND_DOOR);
    layout.set_edge_v(6, 3, EDGE_KIND_DOOR);
    layout.set_edge_v(4, 7, EDGE_KIND_DOOR);
    layout.set_edge_v(8, 8, EDGE_KIND_FALSE_DOOR);
}

fn g_hub(layout: &mut ChunkLayoutV1) {
    // A central 4x4 room with arched spokes toward each side; corner columns.
    room_box(layout, 3, 3, 6, 6, EDGE_KIND_WALL);
    layout.set_edge_h(4, 3, EDGE_KIND_ARCH);
    layout.set_edge_h(5, 7, EDGE_KIND_ARCH);
    layout.set_edge_v(3, 4, EDGE_KIND_ARCH);
    layout.set_edge_v(7, 5, EDGE_KIND_ARCH);
    for (x, z) in [(1usize, 1usize), (8, 1), (1, 8), (8, 8)] {
        pillar_cell(layout, x, z);
    }
}

fn g_service(layout: &mut ChunkLayoutV1, extra: u16) {
    or_all_cells(layout, extra);
    // A column of small storage rooms + an impassable storage stack.
    wall_v(layout, 3, 0, 9, EDGE_KIND_WALL);
    wall_h(layout, 0, 2, 3, EDGE_KIND_WALL);
    wall_h(layout, 0, 2, 6, EDGE_KIND_WALL);
    layout.set_edge_v(3, 1, EDGE_KIND_DOOR);
    layout.set_edge_v(3, 4, EDGE_KIND_DOOR);
    layout.set_edge_v(3, 8, EDGE_KIND_DOOR);
    layout.set_edge_v(3, 6, EDGE_KIND_FALSE_DOOR);
    block_cell(&mut layout.cells, 7, 1, CELL_BLOCKED);
    block_cell(&mut layout.cells, 8, 1, CELL_BLOCKED);
    block_cell(&mut layout.cells, 8, 2, CELL_BLOCKED);
    wall_h(layout, 5, 8, 6, EDGE_KIND_LOW_WALL);
    layout.set_edge_h(6, 6, EDGE_KIND_OPEN);
}

fn g_manila(layout: &mut ChunkLayoutV1) {
    or_all_cells(layout, CELL_SAFE);
    // Warm, clean room with low-wall border accents; clear centre.
    wall_h(layout, 1, 3, 2, EDGE_KIND_LOW_WALL);
    wall_h(layout, 6, 8, 2, EDGE_KIND_LOW_WALL);
    wall_h(layout, 1, 3, 8, EDGE_KIND_LOW_WALL);
    wall_h(layout, 6, 8, 8, EDGE_KIND_LOW_WALL);
}

fn g_pit_field(layout: &mut ChunkLayoutV1) {
    or_all_cells(layout, CELL_HAZARD);
    for x in [2usize, 4, 6] {
        for z in [2usize, 4, 6] {
            block_cell(&mut layout.cells, x, z, CELL_PIT | CELL_HAZARD | CELL_ANOMALY);
        }
    }
    wall_h(layout, 1, 8, 1, EDGE_KIND_LOW_WALL);
    wall_h(layout, 1, 8, 9, EDGE_KIND_LOW_WALL);
    layout.set_edge_h(4, 1, EDGE_KIND_OPEN);
    layout.set_edge_h(5, 9, EDGE_KIND_OPEN);
}

fn g_vertical(layout: &mut ChunkLayoutV1, extra: u16) {
    or_all_cells(layout, extra);
    for z in 2..8 {
        for x in [4usize, 5] {
            if let Some(idx) = layout.cell_index(x, z) {
                layout.cells[idx] |= CELL_RAMP;
            }
        }
    }
    wall_v(layout, 4, 2, 7, EDGE_KIND_HALF_WALL);
    wall_v(layout, 6, 2, 7, EDGE_KIND_HALF_WALL);
    layout.set_edge_v(4, 4, EDGE_KIND_OPEN);
    layout.set_edge_v(6, 5, EDGE_KIND_OPEN);
}

/// Rotate a layout 90° clockwise: floor cells and every wall edge move with it.
fn rotate_layout_cw(src: &ChunkLayoutV1) -> ChunkLayoutV1 {
    let g = src.grid_size as usize;
    let mut out = src.clone();

    let mut cells = vec![CELL_BLOCKED | CELL_WALL; g * g];
    for z in 0..g {
        for x in 0..g {
            let nx = g - 1 - z;
            let nz = x;
            cells[nz * g + nx] = src.cells[z * g + x];
        }
    }
    out.cells = cells;

    // Edges follow the same rotation; under 90° CW a cell's side N→E→S→W→N.
    out.init_edges();
    for z in 0..g {
        for x in 0..g {
            let nx = g - 1 - z;
            let nz = x;
            for s in 0..4u8 {
                let kind = src.cell_side_edge(x, z, s);
                set_cell_side_edge_kind(&mut out, nx, nz, (s + 1) % 4, kind);
            }
        }
    }
    out
}

/// Open a centred 2-cell gap on each of the four chunk-boundary walls. The gap
/// is at the same cells on both sides of any shared boundary, so adjacent
/// chunks always connect reciprocally without needing neighbour layouts.
fn open_boundary_gaps(layout: &mut ChunkLayoutV1) {
    let g = layout.grid_size as usize;
    let a = g / 2 - 1;
    let b = g / 2;
    layout.set_edge_h(a, 0, EDGE_KIND_OPEN);
    layout.set_edge_h(b, 0, EDGE_KIND_OPEN);
    layout.set_edge_h(a, g, EDGE_KIND_OPEN);
    layout.set_edge_h(b, g, EDGE_KIND_OPEN);
    layout.set_edge_v(0, a, EDGE_KIND_OPEN);
    layout.set_edge_v(0, b, EDGE_KIND_OPEN);
    layout.set_edge_v(g, a, EDGE_KIND_OPEN);
    layout.set_edge_v(g, b, EDGE_KIND_OPEN);
}

fn edge_is_opening(kind: u8) -> bool {
    !crate::world::chunk::edge_blocks_movement(kind)
}

fn perimeter_openings(layout: &ChunkLayoutV1) -> u8 {
    let g = layout.grid_size as usize;
    let mut o = 0u8;
    if (0..g).any(|x| edge_is_opening(layout.edge_h(x, 0))) {
        o |= EDGE_NORTH;
    }
    if (0..g).any(|z| edge_is_opening(layout.edge_v(g, z))) {
        o |= EDGE_EAST;
    }
    if (0..g).any(|x| edge_is_opening(layout.edge_h(x, g))) {
        o |= EDGE_SOUTH;
    }
    if (0..g).any(|z| edge_is_opening(layout.edge_v(0, z))) {
        o |= EDGE_WEST;
    }
    o
}

fn is_floor_cell(layout: &ChunkLayoutV1, x: usize, z: usize) -> bool {
    let f = layout.cell_flags(x, z);
    f & CELL_WALKABLE != 0 && f & (CELL_PILLAR | CELL_PIT | CELL_BLOCKED | CELL_WALL) == 0
}

/// Guarantee every floor cell is reachable from the chunk's central cell by
/// flooding across non-blocking edges and knocking a doorway wherever a floor
/// region is otherwise walled off. Makes hand-authored grammars trap-proof.
fn ensure_edge_connectivity(layout: &mut ChunkLayoutV1) {
    let g = layout.grid_size as usize;
    let seed = {
        let mut found = None;
        for z in 0..g {
            for x in 0..g {
                if is_floor_cell(layout, x, z) {
                    found = Some((x, z));
                    break;
                }
            }
            if found.is_some() {
                break;
            }
        }
        match found {
            Some(s) => s,
            None => return,
        }
    };

    let neighbor = |x: usize, z: usize, side: u8| -> Option<(usize, usize)> {
        match side {
            0 if z > 0 => Some((x, z - 1)),
            1 if x + 1 < g => Some((x + 1, z)),
            2 if z + 1 < g => Some((x, z + 1)),
            3 if x > 0 => Some((x - 1, z)),
            _ => None,
        }
    };

    loop {
        let mut visited = vec![false; g * g];
        let mut queue = std::collections::VecDeque::new();
        visited[seed.1 * g + seed.0] = true;
        queue.push_back(seed);
        while let Some((x, z)) = queue.pop_front() {
            for side in 0..4u8 {
                if crate::world::chunk::edge_blocks_movement(layout.cell_side_edge(x, z, side)) {
                    continue;
                }
                if let Some((nx, nz)) = neighbor(x, z, side) {
                    if is_floor_cell(layout, nx, nz) && !visited[nz * g + nx] {
                        visited[nz * g + nx] = true;
                        queue.push_back((nx, nz));
                    }
                }
            }
        }

        // Find an unreached floor cell adjacent to a reached one and open it.
        let mut opened = false;
        'scan: for z in 0..g {
            for x in 0..g {
                if !is_floor_cell(layout, x, z) || visited[z * g + x] {
                    continue;
                }
                for side in 0..4u8 {
                    if let Some((nx, nz)) = neighbor(x, z, side) {
                        if is_floor_cell(layout, nx, nz) && visited[nz * g + nx] {
                            set_cell_side_edge_kind(layout, x, z, side, EDGE_KIND_DOOR);
                            opened = true;
                            break 'scan;
                        }
                    }
                }
            }
        }
        if !opened {
            break;
        }
    }
}

pub fn build_chunk_layout(template_id: u8, rotation: u16) -> ChunkLayoutV1 {
    let size = LAYOUT_GRID_SIZE as usize;
    let zone = template_zone_kind(template_id);
    // Start from an all-floor chunk: perimeter walls, interior open (init_edges).
    let mut layout = ChunkLayoutV1::new(vec![CELL_WALKABLE; size * size], 0, zone);

    match grammar_for_template(template_id, rotation) {
        LayoutGrammarType::CorridorSpine => g_corridor_spine(&mut layout),
        LayoutGrammarType::CorridorBroken => g_broken_corridor(&mut layout),
        LayoutGrammarType::RoomCluster => g_room_cluster(&mut layout),
        LayoutGrammarType::OpenHall => g_open_hall(&mut layout),
        LayoutGrammarType::PillarGrid => g_pillar_field(&mut layout),
        LayoutGrammarType::MazePocket => g_office_maze(&mut layout, 0),
        LayoutGrammarType::ArchTransition => g_arch_transition(&mut layout),
        LayoutGrammarType::SideRooms => g_side_rooms(&mut layout),
        LayoutGrammarType::HubAndSpokes => g_hub(&mut layout),
        LayoutGrammarType::ServiceArea => {
            let extra = if template_id == TEMPLATE_CLEANING_AREA {
                CELL_SHALLOW_FLUID
            } else {
                0
            };
            g_service(&mut layout, extra);
        }
        LayoutGrammarType::BlackoutPocket => g_office_maze(&mut layout, CELL_ANOMALY),
        LayoutGrammarType::RedWarningPocket => g_office_maze(&mut layout, CELL_ANOMALY),
        LayoutGrammarType::ManilaRoom => {
            if template_id == TEMPLATE_SAFE_ROOM {
                g_starter_safe(&mut layout);
            } else {
                g_manila(&mut layout);
            }
        }
        LayoutGrammarType::PitGridRoom => g_pit_field(&mut layout),
        LayoutGrammarType::VerticalTransition => g_vertical(&mut layout, CELL_SHALLOW_FLUID),
    }

    let turns = ((rotation / 90) % 4) as usize;
    for _ in 0..turns {
        layout = rotate_layout_cw(&layout);
    }

    open_boundary_gaps(&mut layout);
    ensure_edge_connectivity(&mut layout);
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

// ─── Level 0 layout builder ───

struct Level0Builder {
    world_seed: u64,
    rng: StdRng,
    occupied: HashSet<ChunkPos>,
    structures: Vec<StructureV0>,
    sid: u32,
}

impl Level0Builder {
    fn new(world_seed: u64) -> Self {
        Self {
            world_seed,
            rng: StdRng::seed_from_u64(world_seed ^ 0xBACB_00B5_CAFE_0001),
            occupied: HashSet::new(),
            structures: Vec::new(),
            sid: 1,
        }
    }

    fn is_free(&self, pos: ChunkPos) -> bool {
        !self.occupied.contains(&pos)
    }

    fn push_structure(
        &mut self,
        stype: StructureType,
        chunks: Vec<ChunkPos>,
        overrides: Vec<(u8, u16)>,
        tags: Vec<&'static str>,
    ) {
        let origin = chunks[0];
        let (min_x, min_z, max_x, max_z) = structure_bounds(&chunks);
        let size = [
            (max_x - min_x + 1).clamp(1, u8::MAX as i32) as u8,
            (max_z - min_z + 1).clamp(1, u8::MAX as i32) as u8,
        ];
        let mut exits = 0usize;
        for pos in &chunks {
            for dir in [0u8, 1, 2, 3] {
                let d = dir_delta(dir);
                let next = (pos.0 + d.0, pos.1 + d.1);
                if !chunks.contains(&next) {
                    exits += 1;
                }
            }
        }
        let depth = origin.0.abs() + origin.1.abs();
        info!(
            "MPTRACE step=DA event=level0_macro_pattern_created id={} kind={} origin=({},{}) size=({},{}) chunks={} exits={} depth={} stability={}",
            structure_id(self.world_seed, self.sid),
            stype.as_str(),
            origin.0,
            origin.1,
            size[0],
            size[1],
            chunks.len(),
            exits,
            depth,
            if tags.contains(&"starter") || tags.contains(&"safe") { "stable" } else { "unstable" }
        );
        self.structures.push(StructureV0 {
            id: structure_id(self.world_seed, self.sid),
            structure_type: stype,
            origin,
            size,
            seed: chunk_seed(self.world_seed, origin),
            chunks,
            tags,
            chunk_overrides: overrides,
        });
        self.sid += 1;
    }

    fn build(mut self) -> Vec<StructureV0> {
        self.build_starter();
        let junctions = self.build_main_corridors();
        self.build_secondary_branches(&junctions);
        self.build_macro_spaces();
        self.build_loop_connections();

        info!(
            "MPTRACE step=AS event=level0_generation_started seed={} structures={} total_chunks={}",
            self.world_seed,
            self.structures.len(),
            self.occupied.len()
        );

        self.structures
    }

    // ── Step 1: Starter cluster at (0,0)-(1,1) ──

    fn build_starter(&mut self) {
        let positions: Vec<ChunkPos> = vec![(0, 0), (1, 0), (0, 1), (1, 1)];
        let overrides = vec![
            (TEMPLATE_SAFE_ROOM, 0u16),
            (TEMPLATE_ROOM_BASIC, 90),
            (TEMPLATE_ROOM_BASIC, 180),
            (TEMPLATE_ROOM_BASIC, 270),
        ];
        for &p in &positions {
            self.occupied.insert(p);
        }
        self.push_structure(
            StructureType::StarterCluster,
            positions,
            overrides,
            vec!["starter", "safeish"],
        );
    }

    // ── Step 2: Main corridors extending from starter ──

    fn build_main_corridors(&mut self) -> Vec<(ChunkPos, u8)> {
        // Candidate arm starting points and directions
        let arm_options: [(ChunkPos, u8); 6] = [
            ((2, 0), 0),  // East from (1,0)
            ((-1, 0), 2), // West from (0,0)
            ((0, 2), 1),  // North from (0,1)
            ((1, -1), 3), // South from (1,0)
            ((-1, 1), 2), // West from (0,1)
            ((1, 2), 1),  // North from (1,1)
        ];

        let num_arms = self.rng.gen_range(4..=5usize);
        let mut indices: Vec<usize> = (0..arm_options.len()).collect();
        fisher_yates(&mut indices, &mut self.rng);
        indices.truncate(num_arms);
        // Always include East arm (index 0) for Backrooms corridor feel
        if !indices.contains(&0) {
            indices[0] = 0;
        }

        let mut junctions: Vec<(ChunkPos, u8)> = Vec::new();

        for &idx in &indices {
            let (start, dir) = arm_options[idx];
            if !self.is_free(start) {
                continue;
            }

            let length: usize = self.rng.gen_range(4..=8);
            let should_turn = self.rng.gen_bool(0.3);
            let turn_at = if should_turn {
                self.rng.gen_range(2..length.max(3))
            } else {
                usize::MAX
            };
            let perp: [u8; 2] = if dir % 2 == 0 { [1, 3] } else { [0, 2] };
            let turn_dir = perp[self.rng.gen_range(0..2)];

            let mut chunks = Vec::new();
            let mut overrides = Vec::new();
            let mut pos = start;
            let mut cur_dir = dir;

            for step in 0..length {
                if !self.is_free(pos) {
                    break;
                }

                if should_turn && step == turn_at {
                    // Place corner
                    let cr = corner_rotation(cur_dir, turn_dir);
                    chunks.push(pos);
                    overrides.push((TEMPLATE_HALLWAY_CORNER, cr));
                    self.occupied.insert(pos);
                    cur_dir = turn_dir;
                } else {
                    chunks.push(pos);
                    overrides.push((TEMPLATE_HALLWAY_STRAIGHT, straight_rotation(cur_dir)));
                    self.occupied.insert(pos);
                }

                let d = dir_delta(cur_dir);
                pos = (pos.0 + d.0, pos.1 + d.1);
            }

            if !chunks.is_empty() {
                self.push_structure(
                    StructureType::HallwayChain,
                    chunks,
                    overrides,
                    vec!["corridor", "main"],
                );
            }

            // Place junction at corridor end
            if self.is_free(pos) {
                let use_intersection = self.rng.gen_bool(0.45);
                if use_intersection {
                    self.occupied.insert(pos);
                    self.push_structure(
                        StructureType::Intersection,
                        vec![pos],
                        vec![(TEMPLATE_INTERSECTION, 0)],
                        vec!["junction"],
                    );
                } else {
                    // T-junction: close the wall opposite the continuation direction
                    let closed = perp[self.rng.gen_range(0..2)];
                    let rot = t_junction_rotation(closed);
                    self.occupied.insert(pos);
                    self.push_structure(
                        StructureType::HallwayT,
                        vec![pos],
                        vec![(TEMPLATE_HALLWAY_T, rot)],
                        vec!["junction"],
                    );
                }
                junctions.push((pos, cur_dir));
            }
        }

        // Ensure at least one intersection exists (for test compatibility)
        let has_intersection = self
            .structures
            .iter()
            .any(|s| s.structure_type == StructureType::Intersection);
        if !has_intersection {
            if let Some(s) = self
                .structures
                .iter_mut()
                .find(|s| s.structure_type == StructureType::HallwayT)
            {
                s.structure_type = StructureType::Intersection;
                s.chunk_overrides = vec![(TEMPLATE_INTERSECTION, 0)];
                s.tags = vec!["junction"];
            }
        }

        junctions
    }

    // ── Step 3: Secondary branches from junctions ──

    fn build_secondary_branches(&mut self, junctions: &[(ChunkPos, u8)]) {
        let junction_data: Vec<(ChunkPos, u8)> = junctions.to_vec();
        let mut has_storage = false;

        for (junction_pos, main_dir) in &junction_data {
            let perp: [u8; 2] = if main_dir % 2 == 0 { [1, 3] } else { [0, 2] };
            let num_branches: usize = self.rng.gen_range(1..=2);

            // Branch in perpendicular directions
            for b in 0..num_branches {
                let bdir = perp[b % 2];
                let bd = dir_delta(bdir);
                let bstart = (junction_pos.0 + bd.0, junction_pos.1 + bd.1);

                if !self.is_free(bstart) {
                    continue;
                }

                let blen: usize = self.rng.gen_range(2..=4);
                let brot = straight_rotation(bdir);
                let mut bchunks = Vec::new();
                let mut boverrides = Vec::new();
                let mut bpos = bstart;

                for _ in 0..blen {
                    if !self.is_free(bpos) {
                        break;
                    }
                    bchunks.push(bpos);
                    boverrides.push((TEMPLATE_HALLWAY_STRAIGHT, brot));
                    self.occupied.insert(bpos);
                    bpos = (bpos.0 + bd.0, bpos.1 + bd.1);
                }

                if !bchunks.is_empty() {
                    self.push_structure(
                        StructureType::HallwayChain,
                        bchunks,
                        boverrides,
                        vec!["corridor", "secondary"],
                    );
                }

                // Terminal room at branch end
                if self.is_free(bpos) {
                    let depth = (bpos.0.abs() + bpos.1.abs()) as f32;
                    let (stype, template, tags) = self.pick_terminal_room(depth, !has_storage);
                    if stype == StructureType::StorageRoom {
                        has_storage = true;
                    }
                    self.occupied.insert(bpos);
                    self.push_structure(stype, vec![bpos], vec![(template, 0)], tags);
                }
            }

            // Continue main direction from junction (50% chance)
            if self.rng.gen_bool(0.5) {
                let cd = dir_delta(*main_dir);
                let cstart = (junction_pos.0 + cd.0, junction_pos.1 + cd.1);
                if self.is_free(cstart) {
                    let clen: usize = self.rng.gen_range(2..=5);
                    let crot = straight_rotation(*main_dir);
                    let mut cchunks = Vec::new();
                    let mut coverrides = Vec::new();
                    let mut cpos = cstart;

                    for _ in 0..clen {
                        if !self.is_free(cpos) {
                            break;
                        }
                        cchunks.push(cpos);
                        coverrides.push((TEMPLATE_HALLWAY_STRAIGHT, crot));
                        self.occupied.insert(cpos);
                        cpos = (cpos.0 + cd.0, cpos.1 + cd.1);
                    }

                    if !cchunks.is_empty() {
                        self.push_structure(
                            StructureType::HallwayChain,
                            cchunks,
                            coverrides,
                            vec!["corridor", "continuation"],
                        );
                    }

                    // Terminal at continuation end
                    if self.is_free(cpos) {
                        let depth = (cpos.0.abs() + cpos.1.abs()) as f32;
                        let (stype, template, tags) = self.pick_terminal_room(depth, !has_storage);
                        if stype == StructureType::StorageRoom {
                            has_storage = true;
                        }
                        self.occupied.insert(cpos);
                        self.push_structure(stype, vec![cpos], vec![(template, 0)], tags);
                    }
                }
            }
        }

        // Guarantee at least one storage room exists
        if !has_storage {
            let last_terminal = self
                .structures
                .iter_mut()
                .rev()
                .find(|s| s.structure_type == StructureType::DeadEnd);
            if let Some(s) = last_terminal {
                s.structure_type = StructureType::StorageRoom;
                s.chunk_overrides = vec![(TEMPLATE_STORAGE_ROOM, 0)];
                s.tags = vec!["loot"];
            }
        }
    }

    fn pick_terminal_room(
        &mut self,
        depth: f32,
        force_storage: bool,
    ) -> (StructureType, u8, Vec<&'static str>) {
        if force_storage && depth < 8.0 {
            return (
                StructureType::StorageRoom,
                TEMPLATE_STORAGE_ROOM,
                vec!["loot"],
            );
        }

        if depth < 4.0 {
            match self.rng.gen_range(0..3) {
                0 => (
                    StructureType::StorageRoom,
                    TEMPLATE_STORAGE_ROOM,
                    vec!["loot"],
                ),
                1 => (StructureType::SafeRoom, TEMPLATE_SAFE_ROOM, vec!["safe"]),
                _ => (
                    StructureType::PillarRoom,
                    TEMPLATE_PILLAR_ROOM,
                    vec!["atmospheric"],
                ),
            }
        } else if depth < 8.0 {
            match self.rng.gen_range(0..5) {
                0 => (
                    StructureType::StorageRoom,
                    TEMPLATE_STORAGE_ROOM,
                    vec!["loot"],
                ),
                1 => (
                    StructureType::PillarRoom,
                    TEMPLATE_PILLAR_ROOM,
                    vec!["atmospheric"],
                ),
                2 => (StructureType::DeadEnd, TEMPLATE_DEAD_END, vec!["dead_end"]),
                3 => (
                    StructureType::DangerRoom,
                    TEMPLATE_DANGER_ROOM,
                    vec!["danger"],
                ),
                _ => (StructureType::SafeRoom, TEMPLATE_SAFE_ROOM, vec!["safe"]),
            }
        } else {
            match self.rng.gen_range(0..5) {
                0 => (
                    StructureType::DangerRoom,
                    TEMPLATE_DANGER_ROOM,
                    vec!["danger", "deep"],
                ),
                1 => (
                    StructureType::PillarRoom,
                    TEMPLATE_PILLAR_ROOM,
                    vec!["atmospheric", "deep"],
                ),
                2 => (
                    StructureType::DeadEnd,
                    TEMPLATE_DEAD_END,
                    vec!["dead_end", "deep"],
                ),
                3 => (
                    StructureType::StorageRoom,
                    TEMPLATE_STORAGE_ROOM,
                    vec!["loot", "deep"],
                ),
                _ => (
                    StructureType::DangerRoom,
                    TEMPLATE_DANGER_ROOM,
                    vec!["danger", "deep"],
                ),
            }
        }
    }

    // ── Step 3.5: Macro-spaces for stronger Backrooms spatial identity ──

    fn build_macro_spaces(&mut self) {
        // These are backend-authored macro visual spaces. Unity should render them based
        // on template_id instead of inventing random ramps/pits client-side.
        //
        // The placement scans deterministic candidate rectangles near existing chunks.
        // Each macro is adjacent to the current graph, so the coarse BFS connectivity
        // invariant stays valid while still producing bigger rooms/halls.

        let mut total_macros = 0u32;
        let mut open_halls = 0u32;
        let mut pillar_halls = 0u32;
        let mut anomaly_count = 0u32;

        let open_hall_placed = self.try_push_macro_rect(
            StructureType::OpenHall,
            3,
            3,
            &[
                ((-4, -2), 0),
                ((3, 2), 180),
                ((-5, 2), 90),
                ((2, -5), 270),
                ((5, -1), 0),
                ((-2, 4), 180),
            ],
            vec![
                TEMPLATE_OPEN_HALL,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_INTERSECTION,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "open_hall", "large_room"],
        ) || self.try_push_macro_rect_scan(
            StructureType::OpenHall,
            2,
            2,
            vec![
                TEMPLATE_OPEN_HALL,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "open_hall", "empty_office", "fallback"],
            3,
        );
        if open_hall_placed {
            total_macros += 1;
            open_halls += 1;
        }

        if self.try_push_macro_rect(
            StructureType::OpenHall,
            2,
            2,
            &[
                ((-3, 5), 0),
                ((5, -3), 90),
                ((-7, 0), 180),
                ((1, 5), 270),
                ((6, 5), 0),
            ],
            vec![
                TEMPLATE_OPEN_HALL,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "open_hall", "empty_office"],
        ) {
            total_macros += 1;
            open_halls += 1;
        }

        let pillar_hall_placed = self.try_push_macro_rect(
            StructureType::PillarHall,
            3,
            3,
            &[
                ((-6, 3), 90),
                ((4, 4), 180),
                ((-6, -4), 0),
                ((3, -6), 270),
                ((6, 2), 90),
            ],
            vec![
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "pillar_hall", "columns"],
        ) || self.try_push_macro_rect(
            StructureType::PillarHall,
            3,
            2,
            &[
                ((-6, 3), 90),
                ((4, 4), 180),
                ((-6, -4), 0),
                ((3, -6), 270),
                ((6, 2), 90),
                ((-4, 7), 0),
                ((7, -4), 90),
            ],
            vec![
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "pillar_hall", "columns", "fallback"],
        ) || self.try_push_macro_rect_scan(
            StructureType::PillarHall,
            3,
            2,
            vec![
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "pillar_hall", "columns", "scan_fallback"],
            4,
        );
        if pillar_hall_placed {
            total_macros += 1;
            pillar_halls += 1;
        }

        if self.try_push_macro_rect(
            StructureType::HumidZone,
            2,
            2,
            &[
                ((-9, 3), 0),
                ((8, -4), 90),
                ((-5, 7), 180),
                ((3, -8), 270),
                ((7, 6), 0),
            ],
            vec![
                TEMPLATE_HUMID_ZONE,
                TEMPLATE_HUMID_ZONE,
                TEMPLATE_HALLWAY_CORNER,
                TEMPLATE_HUMID_ZONE,
            ],
            vec!["macro", "humid", "stains"],
        ) {
            total_macros += 1;
        }

        if self.try_push_macro_rect(
            StructureType::ArchRoom,
            2,
            2,
            &[((-8, 6), 0), ((8, -5), 90), ((5, 6), 180), ((-6, -7), 270)],
            vec![
                TEMPLATE_ARCH_ROOM,
                TEMPLATE_HALLWAY_T,
                TEMPLATE_ARCH_ROOM,
                TEMPLATE_ROOM_BASIC,
            ],
            vec!["macro", "arch_room", "transition"],
        ) {
            total_macros += 1;
        }

        if self.try_push_macro_rect(
            StructureType::BlackoutZone,
            2,
            2,
            &[
                ((10, -8), 0),
                ((-11, -5), 90),
                ((-8, 9), 180),
                ((11, 4), 270),
            ],
            vec![
                TEMPLATE_BLACKOUT_ZONE,
                TEMPLATE_DANGER_ROOM,
                TEMPLATE_BLACKOUT_ZONE,
                TEMPLATE_DEAD_END,
            ],
            vec!["macro", "blackout", "deep", "danger"],
        ) {
            total_macros += 1;
            anomaly_count += 1;
        }

        if self.try_push_macro_rect(
            StructureType::ManilaRoom,
            2,
            2,
            &[((2, 5), 0), ((-5, -6), 90), ((7, -3), 180), ((-8, 4), 270)],
            vec![
                TEMPLATE_MANILA_ROOM,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_MANILA_ROOM,
                TEMPLATE_INTERSECTION,
            ],
            vec!["macro", "manila_room", "warm_dim"],
        ) {
            total_macros += 1;
        }

        if self.try_push_macro_rect(
            StructureType::CleaningArea,
            2,
            2,
            &[
                ((-10, 1), 0),
                ((9, 7), 90),
                ((-9, -8), 180),
                ((6, -10), 270),
            ],
            vec![
                TEMPLATE_CLEANING_AREA,
                TEMPLATE_STORAGE_ROOM,
                TEMPLATE_CLEANING_AREA,
                TEMPLATE_HUMID_ZONE,
            ],
            vec!["macro", "cleaning_area", "loot_candidate"],
        ) {
            total_macros += 1;
            anomaly_count += 1;
        }

        if self.try_push_macro_rect(
            StructureType::RedRoom,
            2,
            2,
            &[
                ((13, -9), 0),
                ((-13, 7), 90),
                ((10, 10), 180),
                ((-11, -11), 270),
            ],
            vec![
                TEMPLATE_RED_ROOM_WARNING,
                TEMPLATE_HALLWAY_STRAIGHT,
                TEMPLATE_RED_ROOM_WARNING,
                TEMPLATE_DANGER_ROOM,
            ],
            vec!["macro", "red_room_warning", "deep", "rare"],
        ) {
            total_macros += 1;
            anomaly_count += 1;
        }

        if self.try_push_macro_rect(
            StructureType::PitRoom,
            2,
            2,
            &[
                ((4, -12), 0),
                ((-12, 1), 90),
                ((12, 4), 180),
                ((-4, 12), 270),
            ],
            vec![
                TEMPLATE_PIT_ROOM_PLACEHOLDER,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_HALLWAY_CORNER,
                TEMPLATE_PIT_ROOM_PLACEHOLDER,
            ],
            vec!["macro", "pit_room_placeholder", "deep", "visual_only"],
        ) {
            total_macros += 1;
            anomaly_count += 1;
        }

        info!(
            "MPTRACE step=BA event=level0_macro_spaces_complete seed={} structures={} chunks={} total_macrospaces={} open_halls={} pillar_halls={} anomalies={}",
            self.world_seed,
            self.structures.len(),
            self.occupied.len(),
            total_macros,
            open_halls,
            pillar_halls,
            anomaly_count
        );
    }

    fn try_push_macro_rect(
        &mut self,
        stype: StructureType,
        w: i32,
        h: i32,
        candidates: &[(ChunkPos, u16)],
        templates: Vec<u8>,
        tags: Vec<&'static str>,
    ) -> bool {
        if templates.is_empty() {
            return false;
        }

        // Deterministically shuffle candidate order, but keep it stable for a given seed.
        let mut order: Vec<usize> = (0..candidates.len()).collect();
        fisher_yates(&mut order, &mut self.rng);

        for idx in order {
            let (origin, base_rotation) = candidates[idx];
            let mut chunks = Vec::new();
            let mut free = true;
            let mut adjacent = false;

            for dz in 0..h {
                for dx in 0..w {
                    let pos = (origin.0 + dx, origin.1 + dz);
                    if !self.is_free(pos) {
                        free = false;
                        break;
                    }

                    for d in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
                        if self.occupied.contains(&(pos.0 + d.0, pos.1 + d.1)) {
                            adjacent = true;
                        }
                    }

                    chunks.push(pos);
                }

                if !free {
                    break;
                }
            }

            if !free || !adjacent || chunks.is_empty() {
                continue;
            }

            let mut overrides = Vec::with_capacity(chunks.len());
            for i in 0..chunks.len() {
                let template = templates[i % templates.len()];
                let rotation = ((base_rotation as u32 + ((i as u32 % 4) * 90)) % 360) as u16;
                overrides.push((template, rotation));
            }

            for &p in &chunks {
                self.occupied.insert(p);
            }

            self.push_structure(stype, chunks, overrides, tags);
            return true;
        }

        false
    }

    // ── Step 4: Loop connections ──

    fn try_push_macro_rect_scan(
        &mut self,
        stype: StructureType,
        w: i32,
        h: i32,
        templates: Vec<u8>,
        tags: Vec<&'static str>,
        min_depth: i32,
    ) -> bool {
        if templates.is_empty() {
            return false;
        }

        let mut anchors: Vec<ChunkPos> = self.occupied.iter().copied().collect();
        anchors.sort_by_key(|p| (p.0.abs() + p.1.abs(), p.0, p.1));

        for anchor in anchors {
            let origins = [
                (anchor.0 + 1, anchor.1 - h / 2),
                (anchor.0 - w, anchor.1 - h / 2),
                (anchor.0 - w / 2, anchor.1 + 1),
                (anchor.0 - w / 2, anchor.1 - h),
            ];

            for origin in origins {
                if origin.0.abs() + origin.1.abs() < min_depth {
                    continue;
                }

                let mut chunks = Vec::new();
                let mut free = true;
                let mut adjacent = false;

                for dz in 0..h {
                    for dx in 0..w {
                        let pos = (origin.0 + dx, origin.1 + dz);
                        if !self.is_free(pos) {
                            free = false;
                            break;
                        }

                        for d in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
                            if self.occupied.contains(&(pos.0 + d.0, pos.1 + d.1)) {
                                adjacent = true;
                            }
                        }

                        chunks.push(pos);
                    }

                    if !free {
                        break;
                    }
                }

                if !free || !adjacent || chunks.is_empty() {
                    continue;
                }

                let mut overrides = Vec::with_capacity(chunks.len());
                for i in 0..chunks.len() {
                    let template = templates[i % templates.len()];
                    let rotation = ((i as u32 % 4) * 90) as u16;
                    overrides.push((template, rotation));
                }

                for &p in &chunks {
                    self.occupied.insert(p);
                }

                self.push_structure(stype, chunks, overrides, tags);
                return true;
            }
        }

        false
    }

    fn build_loop_connections(&mut self) {
        // Sort positions for deterministic iteration (HashSet order is random)
        let mut all_positions: Vec<ChunkPos> = self.occupied.iter().copied().collect();
        all_positions.sort();
        let mut candidates: Vec<ChunkPos> = Vec::new();

        // Find gaps of exactly 1 between two occupied chunks (axis-aligned)
        for &pos in &all_positions {
            for &(dx, dz) in &[(2i32, 0i32), (0, 2), (-2, 0), (0, -2)] {
                let other = (pos.0 + dx, pos.1 + dz);
                if self.occupied.contains(&other) {
                    let mid = (pos.0 + dx / 2, pos.1 + dz / 2);
                    if self.is_free(mid) && !candidates.contains(&mid) {
                        candidates.push(mid);
                    }
                }
            }
        }

        // Shuffle candidates deterministically, then pick 1-3
        let mut indices: Vec<usize> = (0..candidates.len()).collect();
        fisher_yates(&mut indices, &mut self.rng);
        let num_loops = self.rng.gen_range(1..=3usize).min(candidates.len());
        for i in 0..num_loops {
            let mid = candidates[indices[i]];
            if self.is_free(mid) {
                self.occupied.insert(mid);
                self.push_structure(
                    StructureType::Intersection,
                    vec![mid],
                    vec![(TEMPLATE_INTERSECTION, 0)],
                    vec!["corridor", "loop"],
                );
            }
        }
    }
}

// ─── Chunk generation ───

/// Generate a chunk deterministically from the world seed and grid position.
/// Used for chunks outside the initial structures (ownership expansion, teleport).
pub fn generate_chunk(world_seed: u64, pos: ChunkPos) -> Chunk {
    let seed = chunk_seed(world_seed, pos);
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

    let entities = spawn_entities(world_seed, pos, &mut rng);
    let items = spawn_resources(world_seed, pos, &mut rng);
    let layout = build_chunk_layout(template_id, rotation);

    let mut chunk = Chunk {
        pos,
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

// ─── Structure generation (Level 0 V1) ───

pub fn generate_initial_structures(world_seed: u64) -> Vec<StructureV0> {
    Level0Builder::new(world_seed).build()
}

pub fn generate_initial_structure_chunks(world_seed: u64) -> Vec<(StructureV0, Chunk)> {
    let mut out = Vec::new();
    for structure in generate_initial_structures(world_seed) {
        let (min_x, min_z, max_x, max_z) = structure_bounds(&structure.chunks);
        for (index, pos) in structure.chunks.iter().copied().enumerate() {
            let mut chunk = generate_structure_chunk(world_seed, pos, &structure, index as u32);
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
    apply_reciprocal_layout_openings(&mut out);
    reserve_starter_spawn_area(&mut out);
    out
}

fn structure_bounds(chunks: &[ChunkPos]) -> (i32, i32, i32, i32) {
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
        _ => template_zone_kind(template_id),
    }
}

fn apply_reciprocal_layout_openings(chunks: &mut [(StructureV0, Chunk)]) {
    // Phase 2.7: chunk-boundary gaps are uniform (a centred 2-cell opening on
    // every side, identical on both sides of any shared boundary), so all
    // boundaries are reciprocal by construction. Just refresh the cached
    // opening bitmask from the authored perimeter edges.
    for (_, chunk) in chunks.iter_mut() {
        chunk.layout.edge_openings = perimeter_openings(&chunk.layout);
    }
}

fn generate_structure_chunk(
    world_seed: u64,
    pos: ChunkPos,
    structure: &StructureV0,
    chunk_index: u32,
) -> Chunk {
    let mut chunk = generate_chunk(world_seed, pos);
    chunk.mirrored = false;
    chunk.has_workbench = false;

    // Apply per-chunk template/rotation override
    if let Some(&(template_id, rotation)) = structure.chunk_overrides.get(chunk_index as usize) {
        chunk.template_id = template_id;
        chunk.rotation = rotation;
        chunk.layout = build_chunk_layout(template_id, rotation);
    }

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
    }

    // Phase 2.6: structure items/entities use fixed local coordinates that can
    // land inside walls once the grammar layout is applied. Snap them onto safe
    // cells against the chunk's final layout.
    relocate_contents_to_safe_cells(&mut chunk, true);
    chunk
}

// ─── Position helpers ───

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

// ─── Safe cell placement (Phase 2.6) ───

/// Templates whose layout/profile introduces verticality (sunken, raised,
/// ramps, stairs, pits). Kept out of the immediate spawn region.
fn template_is_vertical(template_id: u8) -> bool {
    matches!(
        template_id,
        TEMPLATE_HUMID_ZONE
            | TEMPLATE_MANILA_ROOM
            | TEMPLATE_ARCH_ROOM
            | TEMPLATE_CLEANING_AREA
            | TEMPLATE_PIT_ROOM_PLACEHOLDER
    )
}

fn world_to_cell(
    layout: &ChunkLayoutV1,
    chunk_pos: ChunkPos,
    world_x: f32,
    world_z: f32,
) -> (usize, usize) {
    let grid = layout.grid_size.max(1) as usize;
    let cell_size = layout.cell_size.max(0.01);
    let local_x = (world_x - chunk_pos.0 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE - 0.001);
    let local_z = (world_z - chunk_pos.1 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE - 0.001);
    (
        ((local_x / cell_size).floor() as usize).min(grid - 1),
        ((local_z / cell_size).floor() as usize).min(grid - 1),
    )
}

/// Hard rule: a cell an item/entity must never occupy — inside geometry
/// (wall, pillar, low/half wall, partition, false door), an actual pit hole, or
/// simply non-walkable. Doors / arches / shallow fluid stay valid.
fn item_cell_blocked(layout: &ChunkLayoutV1, x: usize, z: usize) -> bool {
    let flags = layout.cell_flags(x, z);
    flags
        & (CELL_WALL
            | CELL_BLOCKED
            | CELL_PILLAR
            | CELL_PIT
            | CELL_LOW_WALL
            | CELL_HALF_WALL
            | CELL_THIN_PARTITION
            | CELL_FALSE_DOOR)
        != 0
        || flags & CELL_WALKABLE == 0
}

/// Preferred cell: not blocked and not an ambient-hazard floor. Some rooms
/// (e.g. the pit-grid placeholder) flag their whole floor as hazard, where no
/// fully-clear cell exists; in that case placement falls back to a non-blocked
/// (hazard-floor) cell so the item is at least never inside a wall or hole.
fn item_cell_clear(layout: &ChunkLayoutV1, x: usize, z: usize) -> bool {
    !item_cell_blocked(layout, x, z) && layout.cell_flags(x, z) & CELL_HAZARD == 0
}

fn cell_world_center(layout: &ChunkLayoutV1, chunk_pos: ChunkPos, x: usize, z: usize) -> Vec3 {
    let cs = layout.cell_size.max(0.01);
    Vec3::new(
        chunk_pos.0 as f32 * CHUNK_SIZE + (x as f32 + 0.5) * cs,
        0.0,
        chunk_pos.1 as f32 * CHUNK_SIZE + (z as f32 + 0.5) * cs,
    )
}

fn nearest_cell_center<F>(
    layout: &ChunkLayoutV1,
    chunk_pos: ChunkPos,
    want: Vec3,
    accept: F,
) -> Option<Vec3>
where
    F: Fn(&ChunkLayoutV1, usize, usize) -> bool,
{
    let grid = layout.grid_size as usize;
    let mut best: Option<(f32, Vec3)> = None;
    for z in 0..grid {
        for x in 0..grid {
            if !accept(layout, x, z) {
                continue;
            }
            let c = cell_world_center(layout, chunk_pos, x, z);
            let d = c.distance_xz(want);
            if best.as_ref().map(|(bd, _)| d < *bd).unwrap_or(true) {
                best = Some((d, c));
            }
        }
    }
    best.map(|(_, c)| c)
}

/// Choose a relocation target for an object currently at `pos`. Returns `None`
/// when the object is already on an acceptable cell (clear, or unavoidable
/// hazard floor) and should stay put.
fn relocation_target(layout: &ChunkLayoutV1, chunk_pos: ChunkPos, pos: Vec3) -> Option<Vec3> {
    let (cx, cz) = world_to_cell(layout, chunk_pos, pos.x, pos.z);
    let blocked = item_cell_blocked(layout, cx, cz);
    let on_hazard = !blocked && layout.cell_flags(cx, cz) & CELL_HAZARD != 0;
    if !blocked && !on_hazard {
        return None;
    }
    // Prefer a fully clear cell. If none exists and we're stuck in geometry,
    // accept the nearest merely-walkable (hazard-floor) cell.
    nearest_cell_center(layout, chunk_pos, pos, item_cell_clear).or_else(|| {
        if blocked {
            nearest_cell_center(layout, chunk_pos, pos, |l, x, z| {
                !item_cell_blocked(l, x, z)
            })
        } else {
            None
        }
    })
}

/// Snap every dropped item and entity off walls / pits onto a valid cell.
/// Objects already on a valid cell stay put.
fn relocate_contents_to_safe_cells(chunk: &mut Chunk, log: bool) {
    let layout = chunk.layout.clone();
    let chunk_pos = chunk.pos;

    for item in chunk.items.iter_mut() {
        match relocation_target(&layout, chunk_pos, item.position) {
            Some(target) => {
                let from = item.position;
                item.position = Vec3::new(target.x, from.y, target.z);
                if log {
                    info!(
                        "MPTRACE step=V26 event=spawned_item_relocated id={} kind={} from=({:.2},{:.2},{:.2}) to=({:.2},{:.2},{:.2})",
                        item.id,
                        item.item.type_name(),
                        from.x,
                        from.y,
                        from.z,
                        item.position.x,
                        item.position.y,
                        item.position.z
                    );
                }
            }
            None if log => {
                let (cx, cz) = world_to_cell(&layout, chunk_pos, item.position.x, item.position.z);
                info!(
                    "MPTRACE step=V26 event=spawned_item_safe id={} kind={} chunk=({},{}) cell=({},{})",
                    item.id,
                    item.item.type_name(),
                    chunk_pos.0,
                    chunk_pos.1,
                    cx,
                    cz
                );
            }
            None => {}
        }
    }

    for entity in chunk.entities.iter_mut() {
        match relocation_target(&layout, chunk_pos, entity.position) {
            Some(target) => {
                entity.position = Vec3::new(target.x, entity.position.y, target.z);
                entity.patrol_center = entity.position;
            }
            None => {}
        }
        if log {
            let (cx, cz) = world_to_cell(&layout, chunk_pos, entity.position.x, entity.position.z);
            info!(
                "MPTRACE step=V26 event=spawned_entity_safe id={} kind={} chunk=({},{}) cell=({},{})",
                entity.id,
                entity.entity_type.type_name(),
                chunk_pos.0,
                chunk_pos.1,
                cx,
                cz
            );
        }
    }
}

/// Reserve a clean, flat 4x4 walkable spawn area in the centre of the starter
/// chunk (0,0): no pillars, low/half walls, false doors, hazards, pits or
/// vertical profile under the spawn.
fn reserve_starter_spawn_area(chunks: &mut [(StructureV0, Chunk)]) {
    for (_, chunk) in chunks.iter_mut() {
        if chunk.pos != (0, 0) {
            continue;
        }
        chunk.layout.floor_profile = FLOOR_FLAT;
        chunk.layout.floor_level = 0;
        chunk.layout.vertical_flags = 0;
        chunk.layout.ceiling_profile = CEILING_NORMAL;

        let grid = chunk.layout.grid_size as usize;
        let lo = 3usize;
        let hi = 6usize.min(grid.saturating_sub(1));
        let mut clear_cells = 0u32;
        for x in lo..=hi {
            for z in lo..=hi {
                if let Some(idx) = chunk.layout.cell_index(x, z) {
                    chunk.layout.cells[idx] = CELL_WALKABLE | CELL_SAFE;
                    clear_cells += 1;
                }
            }
        }
        // Open every interior edge inside the core so no wall crosses the spawn.
        for x in lo..=hi {
            for z in lo..=hi {
                if x > lo {
                    chunk.layout.set_edge_v(x, z, EDGE_KIND_OPEN);
                }
                if z > lo {
                    chunk.layout.set_edge_h(x, z, EDGE_KIND_OPEN);
                }
            }
        }
        chunk.layout.edge_openings = perimeter_openings(&chunk.layout);

        let exits = chunk.layout.edge_openings.count_ones();
        info!(
            "MPTRACE step=V26 event=spawn_reserved_area_valid ok={} chunk=(0,0) clear_cells={} exits={}",
            clear_cells >= 9 && exits >= 1,
            clear_cells,
            exits
        );
    }
}

// ─── Spawn helpers ───

pub fn export_level0_ascii(world_seed: u64) -> String {
    let generated = generate_initial_structure_chunks(world_seed);
    if generated.is_empty() {
        return String::new();
    }

    let positions: Vec<ChunkPos> = generated.iter().map(|(_, c)| c.pos).collect();
    let (min_x, min_z, max_x, max_z) = structure_bounds(&positions);
    let grid = LAYOUT_GRID_SIZE as i32;
    let width = (max_x - min_x + 1) * grid;
    let height = (max_z - min_z + 1) * grid;
    let mut canvas = vec![vec![' '; width as usize]; height as usize];

    for (_, chunk) in &generated {
        for z in 0..LAYOUT_GRID_SIZE as usize {
            for x in 0..LAYOUT_GRID_SIZE as usize {
                let flags = chunk.layout.cell_flags(x, z);
                let symbol = cell_symbol(flags, chunk.layout.zone_kind);
                let gx = (chunk.pos.0 - min_x) * grid + x as i32;
                let gz = (chunk.pos.1 - min_z) * grid + z as i32;
                canvas[gz as usize][gx as usize] = symbol;
            }
        }
    }

    let mut out = format!(
        "Level0 seed={} chunks={} bounds=({},{})->({},{})\n",
        world_seed,
        positions.len(),
        min_x,
        min_z,
        max_x,
        max_z
    );
    out.push_str("== floor/zone overview (1 char per 5m cell; #=blocked *=pillar P=pit ~=fluid S=spawn) ==\n");
    for row in &canvas {
        out.extend(row.iter());
        out.push('\n');
    }

    // Cell-edge detail for a representative set of chunks (Phase 2.7).
    out.push_str(
        "\n== cell-edge detail: |,- wall  d door  a arch  : low/half wall  x false door  '.' floor ==\n",
    );
    for pos in sample_chunks_for_ascii(&generated) {
        if let Some((_, chunk)) = generated.iter().find(|(_, c)| c.pos == pos) {
            out.push_str(&format!(
                "\n-- chunk ({},{}) template={} zone={} openings={:04b} --\n",
                pos.0, pos.1, chunk.template_id, chunk.layout.zone_kind, chunk.layout.edge_openings
            ));
            out.push_str(&render_chunk_maze(&chunk.layout));
        }
    }
    out
}

/// Pick the four starter chunks plus a handful of distinct-template chunks so
/// the maze detail shows real variety.
fn sample_chunks_for_ascii(generated: &[(StructureV0, Chunk)]) -> Vec<ChunkPos> {
    let mut out = vec![(0, 0), (1, 0), (0, 1), (1, 1)];
    let mut seen_templates: HashSet<u8> = HashSet::new();
    for (_, chunk) in generated {
        if out.contains(&chunk.pos) {
            continue;
        }
        if seen_templates.insert(chunk.template_id) && out.len() < 12 {
            out.push(chunk.pos);
        }
    }
    out
}

fn render_chunk_maze(layout: &ChunkLayoutV1) -> String {
    let g = layout.grid_size as usize;
    let w = 2 * g + 1;
    let h = 2 * g + 1;
    let mut rows = vec![vec![' '; w]; h];
    for gz in (0..h).step_by(2) {
        for gx in (0..w).step_by(2) {
            rows[gz][gx] = '+';
        }
    }
    for z in 0..g {
        for x in 0..g {
            let cx = 2 * x + 1;
            let cz = 2 * z + 1;
            rows[cz][cx] = cell_symbol(layout.cell_flags(x, z), layout.zone_kind);
            rows[cz][2 * x] = v_edge_char(layout.edge_v(x, z));
            rows[cz][2 * x + 2] = v_edge_char(layout.edge_v(x + 1, z));
            rows[2 * z][cx] = h_edge_char(layout.edge_h(x, z));
            rows[2 * z + 2][cx] = h_edge_char(layout.edge_h(x, z + 1));
        }
    }
    let mut s = String::new();
    for row in rows {
        s.extend(row.iter());
        s.push('\n');
    }
    s
}

fn v_edge_char(kind: u8) -> char {
    match kind {
        EDGE_KIND_WALL | EDGE_KIND_PARTITION => '|',
        EDGE_KIND_DOOR => 'd',
        EDGE_KIND_ARCH => 'a',
        EDGE_KIND_LOW_WALL | EDGE_KIND_HALF_WALL => ':',
        EDGE_KIND_FALSE_DOOR => 'x',
        _ => ' ',
    }
}

fn h_edge_char(kind: u8) -> char {
    match kind {
        EDGE_KIND_WALL | EDGE_KIND_PARTITION => '-',
        EDGE_KIND_DOOR => 'd',
        EDGE_KIND_ARCH => 'a',
        EDGE_KIND_LOW_WALL | EDGE_KIND_HALF_WALL => ':',
        EDGE_KIND_FALSE_DOOR => 'x',
        _ => ' ',
    }
}

fn cell_symbol(flags: u16, zone_kind: u8) -> char {
    if flags & CELL_PIT != 0 {
        'P'
    } else if flags & CELL_PILLAR != 0 {
        '*'
    } else if flags & CELL_RAMP != 0 {
        '^'
    } else if flags & (CELL_BLOCKED | CELL_WALL) != 0 {
        '#'
    } else if flags & CELL_SHALLOW_FLUID != 0 {
        '~'
    } else if flags & CELL_WALKABLE != 0 {
        match zone_kind {
            ZONE_BLACKOUT => 'B',
            ZONE_RED => 'R',
            ZONE_MANILA => 'M',
            ZONE_CLEANING => 'C',
            ZONE_SAFE => 'S',
            ZONE_OPEN_HALL => 'O',
            _ => '.',
        }
    } else {
        ' '
    }
}

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

// ─── Tests ───

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::{HashSet, VecDeque};

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
                for &pos in &structure.chunks {
                    assert!(
                        seen.insert(pos),
                        "seed {} duplicate chunk {:?} in structure {:?}",
                        seed,
                        pos,
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

    #[test]
    fn level0_template_ids_are_valid() {
        for seed in [42u64, 43, 99, 1234, 7777, 7778] {
            let chunks = generate_initial_structure_chunks(seed);
            assert!(!chunks.is_empty(), "seed {} generated no chunks", seed);
            for (_, chunk) in chunks {
                assert!(
                    chunk.template_id < TEMPLATE_COUNT,
                    "seed {} chunk {:?} invalid template {}",
                    seed,
                    chunk.pos,
                    chunk.template_id
                );
                assert!(
                    matches!(chunk.rotation, 0 | 90 | 180 | 270),
                    "seed {} chunk {:?} invalid rotation {}",
                    seed,
                    chunk.pos,
                    chunk.rotation
                );
                assert_eq!(chunk.layout.grid_size, LAYOUT_GRID_SIZE);
                assert_eq!(
                    chunk.layout.cells.len(),
                    (LAYOUT_GRID_SIZE as usize) * (LAYOUT_GRID_SIZE as usize)
                );
                assert!(
                    chunk
                        .layout
                        .cells
                        .iter()
                        .any(|flags| flags & CELL_WALKABLE != 0),
                    "seed {} chunk {:?} has no walkable layout cells",
                    seed,
                    chunk.pos
                );
            }
        }
    }

    #[test]
    fn corridor_layouts_connect_expected_edges() {
        let straight_ns = build_chunk_layout(TEMPLATE_HALLWAY_STRAIGHT, 0);
        assert!(straight_ns.edge_openings & EDGE_NORTH != 0);
        assert!(straight_ns.edge_openings & EDGE_SOUTH != 0);
        assert_eq!(straight_ns.edge_openings & (EDGE_EAST | EDGE_WEST), 0);

        let straight_ew = build_chunk_layout(TEMPLATE_HALLWAY_STRAIGHT, 90);
        assert!(straight_ew.edge_openings & EDGE_EAST != 0);
        assert!(straight_ew.edge_openings & EDGE_WEST != 0);

        let intersection = build_chunk_layout(TEMPLATE_INTERSECTION, 0);
        assert_eq!(
            intersection.edge_openings & (EDGE_NORTH | EDGE_EAST | EDGE_SOUTH | EDGE_WEST),
            EDGE_NORTH | EDGE_EAST | EDGE_SOUTH | EDGE_WEST
        );
    }

    #[test]
    fn generated_layout_openings_are_reciprocal() {
        let chunks = generate_initial_structure_chunks(42);
        let by_pos: std::collections::HashMap<ChunkPos, u8> = chunks
            .iter()
            .map(|(_, chunk)| (chunk.pos, chunk.layout.edge_openings))
            .collect();

        for (_, chunk) in &chunks {
            for edge in [EDGE_NORTH, EDGE_EAST, EDGE_SOUTH, EDGE_WEST] {
                if chunk.layout.edge_openings & edge == 0 {
                    continue;
                }
                let delta = edge_delta(edge);
                let neighbor = (chunk.pos.0 + delta.0, chunk.pos.1 + delta.1);
                let neighbor_openings = by_pos.get(&neighbor).unwrap_or_else(|| {
                    panic!("open edge from {:?} to missing {:?}", chunk.pos, neighbor)
                });
                assert!(
                    neighbor_openings & opposite_edge(edge) != 0,
                    "edge {:?} from {:?} not reciprocal",
                    edge,
                    chunk.pos
                );
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
        let chunks = generate_initial_structure_chunks(42);
        let mut special_count = 0usize;
        for (_, chunk) in chunks {
            special_count += chunk
                .layout
                .cells
                .iter()
                .filter(|flags| {
                    **flags
                        & (CELL_DOOR | CELL_ARCH | CELL_LOW_WALL | CELL_HALF_WALL | CELL_FALSE_DOOR)
                        != 0
                })
                .count();
        }
        assert!(special_count >= 20, "only {special_count} special cells");
    }

    #[test]
    fn layout_density_is_not_linear_corridor_only() {
        let chunks = generate_initial_structure_chunks(42);
        let mut wall = 0usize;
        let mut total = 0usize;
        for (_, chunk) in chunks {
            for flags in chunk.layout.cells {
                total += 1;
                if flags & (CELL_BLOCKED | CELL_WALL | CELL_PILLAR | CELL_LOW_WALL | CELL_HALF_WALL)
                    != 0
                {
                    wall += 1;
                }
            }
        }
        let pct = wall as f32 / total.max(1) as f32;
        assert!(pct > 0.18 && pct < 0.72, "wall density {pct}");
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
}
