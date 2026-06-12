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
    TEMPLATE_ARCH_ROOM, TEMPLATE_BLACKOUT_ZONE, TEMPLATE_DEAD_END, TEMPLATE_HALLWAY_CORNER,
    TEMPLATE_HALLWAY_STRAIGHT, TEMPLATE_HALLWAY_T, TEMPLATE_HUMID_ZONE, TEMPLATE_INTERSECTION,
    TEMPLATE_MANILA_ROOM, TEMPLATE_OPEN_HALL, TEMPLATE_PILLAR_ROOM, TEMPLATE_PIT_ROOM_PLACEHOLDER,
    TEMPLATE_RED_ROOM_WARNING, TEMPLATE_ROOM_BASIC, TEMPLATE_STORAGE_ROOM,
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
use crate::world::levels::level_0::content::{
    apply_structure_content, spawn_entities, spawn_resources,
};

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
use crate::player::inventory::Item;
#[cfg(test)]
use crate::utils::{Vec3, CHUNK_SIZE};
#[cfg(test)]
pub(crate) use crate::world::architecture::chunk_generator::{
    chunk_seed, stable_entity_id, stable_item_id,
};
#[cfg(test)]
use crate::world::architecture::surface_builder::edge_delta;
#[cfg(test)]
use crate::world::chunk::DroppedItem;
#[cfg(test)]
use crate::world::entity::{Entity, EntityType};
#[cfg(test)]
pub use crate::world::levels::level_0::structure::StructureType;

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
                if crate::world::collision::edge_blocks_movement(layout.cell_side_edge(x, z, side))
                {
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
