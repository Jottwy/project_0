//! Chunk layout grammars for Level 0.
//!
//! Level 0 layout grammar layer. Converts template_id + rotation into base ChunkLayoutV1.
//! Does not decide placement, connectivity, POIs, structures, networking or world graph.
//!
//! Note: Rotation parameter is accepted but ignored here; rotation is applied after
//! grammar generation in the caller (typically `build_chunk_layout` in generator.rs).
//! This keeps layout grammars purely about layout topology, not geometric transformations.

use crate::utils::ChunkPos;
use crate::world::chunk::{
    ZONE_BLACKOUT, ZONE_CLEANING, ZONE_DANGER, ZONE_HUMID, ZONE_MANILA, ZONE_OPEN_HALL,
    ZONE_PILLAR_HALL, ZONE_PIT, ZONE_RED, ZONE_SAFE, ZONE_STORAGE,
};

use crate::world::chunk::{
    ChunkLayoutV1, CELL_ANOMALY, CELL_BLOCKED, CELL_HAZARD, CELL_PILLAR, CELL_PIT, CELL_RAMP,
    CELL_SAFE, CELL_SHALLOW_FLUID, CELL_WALKABLE, EDGE_KIND_ARCH, EDGE_KIND_DOOR,
    EDGE_KIND_FALSE_DOOR, EDGE_KIND_HALF_WALL, EDGE_KIND_LOW_WALL, EDGE_KIND_OPEN,
    EDGE_KIND_PARTITION, EDGE_KIND_WALL, LAYOUT_GRID_SIZE, ZONE_NORMAL,
};

/// Tipo de gramática usada por cada template.
/// Esto NO es específico de Level 0, por eso vive aquí.
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
    // POI V1
    PoiLandmark,
    PoiAnomaly,
    PoiDangerPocket,
    PoiSafePocket,
}

pub fn template_zone_kind(template_id: u8) -> u8 {
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
        TEMPLATE_POI_LANDMARK => ZONE_OPEN_HALL,
        TEMPLATE_POI_ANOMALY => ZONE_NORMAL,
        TEMPLATE_POI_DANGER_POCKET => ZONE_DANGER,
        TEMPLATE_POI_SAFE_POCKET => ZONE_MANILA,
        _ => ZONE_NORMAL,
    }
}

// Template IDs for Level 0. These are the "semantic" template IDs that the layout grammar layer understands;

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
pub const TEMPLATE_OPEN_HALL: u8 = 10;
pub const TEMPLATE_ARCH_ROOM: u8 = 11;
pub const TEMPLATE_CLEANING_AREA: u8 = 12;
pub const TEMPLATE_HUMID_ZONE: u8 = 13;
pub const TEMPLATE_BLACKOUT_ZONE: u8 = 14;
pub const TEMPLATE_MANILA_ROOM: u8 = 15;
pub const TEMPLATE_RED_ROOM_WARNING: u8 = 16;
pub const TEMPLATE_PIT_ROOM_PLACEHOLDER: u8 = 17;
pub const TEMPLATE_POI_LANDMARK: u8 = 18;
pub const TEMPLATE_POI_ANOMALY: u8 = 19;
pub const TEMPLATE_POI_DANGER_POCKET: u8 = 20;
pub const TEMPLATE_POI_SAFE_POCKET: u8 = 21;

pub const TEMPLATE_OPEN_COLUMN_ROOM: u8 = TEMPLATE_OPEN_HALL;
pub const TEMPLATE_CLOSED_ROOM: u8 = TEMPLATE_ARCH_ROOM;
pub const TEMPLATE_FALSE_ROOM: u8 = TEMPLATE_CLEANING_AREA;
pub const TEMPLATE_DARK_ZONE: u8 = TEMPLATE_BLACKOUT_ZONE;
pub const TEMPLATE_OVERLIT_ZONE: u8 = TEMPLATE_MANILA_ROOM;
pub const TEMPLATE_FALSE_RETURN: u8 = TEMPLATE_RED_ROOM_WARNING;
pub const TEMPLATE_VERTICAL_ANOMALY: u8 = TEMPLATE_PIT_ROOM_PLACEHOLDER;

pub const TEMPLATE_COUNT: u8 = 22;

// ─────────────────────────────────────────────────────────────
// Helpers de celdas
// ─────────────────────────────────────────────────────────────

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

// ─────────────────────────────────────────────────────────────
// Helpers de edges
// ─────────────────────────────────────────────────────────────

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

fn set_cell_side_edge_kind(layout: &mut ChunkLayoutV1, x: usize, z: usize, side: u8, kind: u8) {
    match side {
        0 => layout.set_edge_h(x, z, kind),
        1 => layout.set_edge_v(x + 1, z, kind),
        2 => layout.set_edge_h(x, z + 1, kind),
        _ => layout.set_edge_v(x, z, kind),
    }
}

// ─────────────────────────────────────────────────────────────
// Gramáticas g_* Backrooms
// ─────────────────────────────────────────────────────────────

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
    for z in 4..=5 {
        for x in 4..=5 {
            if let Some(idx) = layout.cell_index(x, z) {
                layout.cells[idx] = CELL_WALKABLE | CELL_HAZARD;
            }
        }
    }
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
            block_cell(
                &mut layout.cells,
                x,
                z,
                CELL_PIT | CELL_HAZARD | CELL_ANOMALY,
            );
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

// ─── POI V1 grammars ───

fn g_poi_landmark(layout: &mut ChunkLayoutV1) {
    // Memorable large room: wide central nave with a 3-pillar row as landmark
    // feature, two side alcoves enclosed by half-walls, and arched openings on
    // every face. Tall ceiling signals "important location" to the player.
    for x in [3usize, 5, 7] {
        pillar_cell(layout, x, 5);
    }
    // N alcove — enclosed by half-wall, arch entry from nave
    wall_h(layout, 1, 8, 3, EDGE_KIND_HALF_WALL);
    layout.set_edge_h(4, 3, EDGE_KIND_ARCH);
    layout.set_edge_h(5, 3, EDGE_KIND_ARCH);
    // S alcove — same pattern mirrored
    wall_h(layout, 1, 8, 7, EDGE_KIND_HALF_WALL);
    layout.set_edge_h(4, 7, EDGE_KIND_ARCH);
    layout.set_edge_h(5, 7, EDGE_KIND_ARCH);
    // Corner low-wall accents for visual rhythm
    wall_v(layout, 1, 0, 2, EDGE_KIND_LOW_WALL);
    wall_v(layout, 9, 0, 2, EDGE_KIND_LOW_WALL);
    wall_v(layout, 1, 8, 9, EDGE_KIND_LOW_WALL);
    wall_v(layout, 9, 8, 9, EDGE_KIND_LOW_WALL);
}

fn g_poi_anomaly(layout: &mut ChunkLayoutV1) {
    // Spatially disorienting: an off-centre spine with a false branch, chicane
    // baffles inside the main run, and an unexpected side opening that leads
    // nowhere (false door). Looks like it should connect but feels wrong.
    // Off-centre N–S spine at column 3 (not the symmetric 4–5).
    wall_v(layout, 3, 0, 9, EDGE_KIND_WALL);
    wall_v(layout, 5, 0, 9, EDGE_KIND_WALL);
    layout.set_edge_v(3, 2, EDGE_KIND_DOOR);
    layout.set_edge_v(5, 7, EDGE_KIND_DOOR);
    // Chicane baffles inside the spine — passable but disorienting
    layout.set_edge_h(1, 3, EDGE_KIND_HALF_WALL);
    layout.set_edge_h(4, 6, EDGE_KIND_HALF_WALL);
    // False branch east — large room with a false door on the far wall
    wall_v(layout, 8, 0, 9, EDGE_KIND_WALL);
    layout.set_edge_v(8, 4, EDGE_KIND_FALSE_DOOR);
    // Thin partition bisecting the east room creating a dead alcove
    wall_h(layout, 6, 7, 5, EDGE_KIND_PARTITION);
    layout.set_edge_h(6, 5, EDGE_KIND_DOOR);
    // West dead-end pocket with anomaly cell
    wall_v(layout, 2, 4, 6, EDGE_KIND_WALL);
    layout.set_edge_v(2, 5, EDGE_KIND_ARCH);
    block_cell(&mut layout.cells, 1, 5, CELL_ANOMALY | CELL_BLOCKED);
}

fn g_poi_danger_pocket(layout: &mut ChunkLayoutV1) {
    // Hostile, claustrophobic: constricted entry, fragmented internal walls,
    // hazard cells dotted in dead pockets, and storage-like obstruction making
    // the space feel occupied and dangerous.
    or_all_cells(layout, CELL_HAZARD);
    // Constricted entry: thick wall across the whole E side with a single-cell
    // doorway (not the usual 2-cell gap; note open_boundary_gaps runs after this
    // and will add the standard gap — so this wall creates internal constriction).
    wall_v(layout, 7, 0, 9, EDGE_KIND_WALL);
    layout.set_edge_v(7, 4, EDGE_KIND_DOOR);
    // Dense partition maze in the centre
    wall_h(layout, 0, 4, 3, EDGE_KIND_PARTITION);
    wall_h(layout, 3, 7, 6, EDGE_KIND_PARTITION);
    layout.set_edge_h(2, 3, EDGE_KIND_DOOR);
    layout.set_edge_h(5, 6, EDGE_KIND_DOOR);
    // Impassable obstruction cluster (storage debris / blocked cells)
    block_cell(&mut layout.cells, 1, 7, CELL_BLOCKED);
    block_cell(&mut layout.cells, 2, 7, CELL_BLOCKED);
    block_cell(&mut layout.cells, 1, 8, CELL_BLOCKED);
    // Hazard pit cells in dead corners
    block_cell(
        &mut layout.cells,
        8,
        1,
        CELL_PIT | CELL_HAZARD | CELL_ANOMALY,
    );
    block_cell(
        &mut layout.cells,
        9,
        0,
        CELL_PIT | CELL_HAZARD | CELL_ANOMALY,
    );
    // False door on the south dead-end wall
    layout.set_edge_h(4, 9, EDGE_KIND_FALSE_DOOR);
    for z in 4..=5 {
        for x in 4..=5 {
            if let Some(idx) = layout.cell_index(x, z) {
                layout.cells[idx] = CELL_WALKABLE | CELL_HAZARD;
            }
        }
    }
}

fn g_poi_safe_pocket(layout: &mut ChunkLayoutV1) {
    // Calming, recognizable: open centre, CELL_SAFE throughout, corner low-wall
    // accents in a different pattern from g_manila, with a single central pillar
    // pair as a landmark. Warm carpet-like feel.
    or_all_cells(layout, CELL_SAFE);
    // Central pillar pair as landmark (not a grid — just two, offset slightly)
    pillar_cell(layout, 4, 4);
    pillar_cell(layout, 6, 6);
    // Corner accent walls — small L-shapes in each corner
    wall_h(layout, 0, 2, 2, EDGE_KIND_LOW_WALL);
    wall_v(layout, 2, 0, 1, EDGE_KIND_LOW_WALL);
    wall_h(layout, 7, 9, 2, EDGE_KIND_LOW_WALL);
    wall_v(layout, 8, 0, 1, EDGE_KIND_LOW_WALL);
    wall_h(layout, 0, 2, 8, EDGE_KIND_LOW_WALL);
    wall_v(layout, 2, 8, 9, EDGE_KIND_LOW_WALL);
    wall_h(layout, 7, 9, 8, EDGE_KIND_LOW_WALL);
    wall_v(layout, 8, 8, 9, EDGE_KIND_LOW_WALL);
}

// ─────────────────────────────────────────────────────────────
// Selección de gramática y generación
// ─────────────────────────────────────────────────────────────

pub fn grammar_for_template(template_id: u8, _rotation: u16) -> LayoutGrammarType {
    match template_id {
        1 => LayoutGrammarType::CorridorSpine, // TEMPLATE_HALLWAY_STRAIGHT
        2 => LayoutGrammarType::SideRooms,     // TEMPLATE_HALLWAY_CORNER
        3 => LayoutGrammarType::HubAndSpokes,  // TEMPLATE_INTERSECTION
        4 => LayoutGrammarType::ServiceArea,   // TEMPLATE_STORAGE_ROOM
        5 => LayoutGrammarType::ManilaRoom,    // TEMPLATE_SAFE_ROOM
        6 => LayoutGrammarType::SideRooms,     // TEMPLATE_DEAD_END
        7 => LayoutGrammarType::MazePocket,    // TEMPLATE_DANGER_ROOM
        8 => LayoutGrammarType::HubAndSpokes,  // TEMPLATE_HALLWAY_T
        9 => LayoutGrammarType::PillarGrid,    // TEMPLATE_PILLAR_ROOM
        10 => LayoutGrammarType::OpenHall,     // TEMPLATE_OPEN_HALL
        11 => LayoutGrammarType::ArchTransition, // TEMPLATE_ARCH_ROOM
        12 => LayoutGrammarType::ServiceArea,  // TEMPLATE_CLEANING_AREA
        13 => LayoutGrammarType::VerticalTransition, // TEMPLATE_HUMID_ZONE
        14 => LayoutGrammarType::BlackoutPocket, // TEMPLATE_BLACKOUT_ZONE
        15 => LayoutGrammarType::ManilaRoom,   // TEMPLATE_MANILA_ROOM
        16 => LayoutGrammarType::RedWarningPocket, // TEMPLATE_RED_ROOM_WARNING
        17 => LayoutGrammarType::PitGridRoom,  // TEMPLATE_PIT_ROOM_PLACEHOLDER
        18 => LayoutGrammarType::PoiLandmark,
        19 => LayoutGrammarType::PoiAnomaly,
        20 => LayoutGrammarType::PoiDangerPocket,
        21 => LayoutGrammarType::PoiSafePocket,
        _ => LayoutGrammarType::RoomCluster,
    }
}

pub fn generate_layout_from_template(template_id: u8, _rotation: u16) -> ChunkLayoutV1 {
    let size = LAYOUT_GRID_SIZE as usize;
    let zone = template_zone_kind(template_id);

    let mut layout = ChunkLayoutV1::new(vec![CELL_WALKABLE; size * size], 0, zone);

    let grammar = grammar_for_template(template_id, _rotation);

    match grammar {
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
            let extra = if template_id == 12 {
                CELL_SHALLOW_FLUID
            } else {
                0
            };
            g_service(&mut layout, extra);
        }
        LayoutGrammarType::BlackoutPocket => g_office_maze(&mut layout, CELL_ANOMALY),
        LayoutGrammarType::RedWarningPocket => g_office_maze(&mut layout, CELL_ANOMALY),
        LayoutGrammarType::ManilaRoom => {
            if template_id == 5 {
                g_starter_safe(&mut layout);
            } else {
                g_manila(&mut layout);
            }
        }
        LayoutGrammarType::PitGridRoom => g_pit_field(&mut layout),
        LayoutGrammarType::VerticalTransition => g_vertical(&mut layout, CELL_SHALLOW_FLUID),
        LayoutGrammarType::PoiLandmark => g_poi_landmark(&mut layout),
        LayoutGrammarType::PoiAnomaly => g_poi_anomaly(&mut layout),
        LayoutGrammarType::PoiDangerPocket => g_poi_danger_pocket(&mut layout),
        LayoutGrammarType::PoiSafePocket => g_poi_safe_pocket(&mut layout),
    }
    if matches!(template_id, 7 | 14 | 16 | 17 | 20) {
        for z in 4..=5 {
            for x in 4..=5 {
                if let Some(idx) = layout.cell_index(x, z) {
                    layout.cells[idx] = CELL_WALKABLE | CELL_HAZARD;
                }
            }
        }
    }
    layout
}

pub(crate) fn open_boundary_gaps(layout: &mut ChunkLayoutV1) {
    let g = LAYOUT_GRID_SIZE as usize;
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

// ─── MIG-5b: direction/rotation helpers (moved from generator.rs) ───

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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_templates_generate_nonpanic() {
        for template_id in 0..22 {
            let layout = generate_layout_from_template(template_id, 0);
            assert_eq!(
                layout.cells.len(),
                (LAYOUT_GRID_SIZE as usize) * (LAYOUT_GRID_SIZE as usize),
                "template {}: layout cells wrong size",
                template_id
            );
        }
    }

    #[test]
    fn templates_have_nonzero_cells() {
        for template_id in 0..22 {
            let layout = generate_layout_from_template(template_id, 0);
            let walkable_count = layout
                .cells
                .iter()
                .filter(|c| **c & CELL_WALKABLE != 0)
                .count();
            let blocked_count = layout
                .cells
                .iter()
                .filter(|c| **c & CELL_BLOCKED != 0)
                .count();
            assert!(
                walkable_count > 0 || blocked_count > 0,
                "template {}: must have at least some cells",
                template_id
            );
        }
    }

    #[test]
    fn safe_templates_contain_safe_cells() {
        for template_id in [5, 15, 21] {
            // TEMPLATE_SAFE_ROOM, TEMPLATE_MANILA_ROOM, TEMPLATE_POI_SAFE_POCKET
            let layout = generate_layout_from_template(template_id, 0);
            let safe_count = layout.cells.iter().filter(|c| **c & CELL_SAFE != 0).count();
            assert!(
                safe_count > 0,
                "template {}: expected CELL_SAFE cells",
                template_id
            );
        }
    }

    #[test]
    fn hazard_templates_contain_hazard_cells() {
        for template_id in [7, 14, 16] {
            // TEMPLATE_DANGER_ROOM, TEMPLATE_BLACKOUT_ZONE, TEMPLATE_RED_ROOM_WARNING
            let layout = generate_layout_from_template(template_id, 0);
            let hazard_count = layout
                .cells
                .iter()
                .filter(|c| **c & CELL_HAZARD != 0)
                .count();
            assert!(
                hazard_count > 0,
                "template {}: expected CELL_HAZARD cells",
                template_id
            );
        }
    }
}
