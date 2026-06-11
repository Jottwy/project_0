//! Safe-cell placement and collision-layout validation helpers (Phase 2.6
//! spawn safety): cell-level blocked/clear rules, relocation of spawned
//! items/entities off geometry, and the reserved starter spawn area.
//! Moved out of `generator.rs` in MIG-1; `generator` re-exports these for
//! existing call sites and tests.

use log::info;

use crate::utils::{ChunkPos, Vec3, CHUNK_SIZE};
use crate::world::architecture::surface_builder::perimeter_openings;
use crate::world::chunk::{
    Chunk, ChunkLayoutV1, CEILING_NORMAL, CELL_BLOCKED, CELL_FALSE_DOOR, CELL_HALF_WALL,
    CELL_HAZARD, CELL_LOW_WALL, CELL_PILLAR, CELL_PIT, CELL_SAFE, CELL_THIN_PARTITION,
    CELL_WALKABLE, CELL_WALL, EDGE_KIND_OPEN, FLOOR_FLAT,
};
// MIG-2: template ids come from the sibling layout_grammars module, not through
// generator (which only re-exported them, inverting the layering).
use super::layout_grammars::{
    TEMPLATE_ARCH_ROOM, TEMPLATE_CLEANING_AREA, TEMPLATE_HUMID_ZONE, TEMPLATE_MANILA_ROOM,
    TEMPLATE_PIT_ROOM_PLACEHOLDER,
};
use crate::world::generator::StructureV0;

/// Templates whose layout/profile introduces verticality (sunken, raised,
/// ramps, stairs, pits). Kept out of the immediate spawn region.
pub fn template_is_vertical(template_id: u8) -> bool {
    matches!(
        template_id,
        TEMPLATE_HUMID_ZONE
            | TEMPLATE_MANILA_ROOM
            | TEMPLATE_ARCH_ROOM
            | TEMPLATE_CLEANING_AREA
            | TEMPLATE_PIT_ROOM_PLACEHOLDER
    )
}

pub fn world_to_cell(
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
pub fn item_cell_blocked(layout: &ChunkLayoutV1, x: usize, z: usize) -> bool {
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
pub fn relocate_contents_to_safe_cells(chunk: &mut Chunk, log: bool) {
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
        if let Some(target) = relocation_target(&layout, chunk_pos, entity.position) {
            entity.position = Vec3::new(target.x, entity.position.y, target.z);
            entity.patrol_center = entity.position;
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
pub fn reserve_starter_spawn_area(chunks: &mut [(StructureV0, Chunk)]) {
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
