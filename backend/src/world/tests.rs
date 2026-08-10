use super::*;
use std::collections::{HashSet, VecDeque};

fn key(pos: ChunkPos) -> LayeredChunkPos {
    layered_chunk_pos(pos, 0)
}

#[test]
fn ownership_loads_chunks_around_player() {
    let mut world = World::new(42);
    let player_pos = Vec3::new(25.0, 0.0, 25.0); // chunk (0,0)
    world.update_ownership(player_pos, 1);
    let radius = world.config.ownership_radius; // 5
    let expected = ((radius * 2 + 1) * (radius * 2 + 1)) as usize; // 11x11 = 121
    let layer0 = world.chunks.values().filter(|c| c.layer == 0).count();
    assert_eq!(layer0, expected);
    assert!(world.chunks.len() >= expected);
    assert!(world.chunks.contains_key(&key((0, 0))));
    assert!(world.chunks.contains_key(&key((3, 3))));
    assert!(world.chunks.contains_key(&key((-3, -3))));
}

#[test]
fn ownership_unloads_distant_chunks() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    assert!(world.chunks.contains_key(&key((0, 0))));
    // Move far away.
    world.update_ownership(Vec3::new(1000.0, 0.0, 1000.0), 1);
    assert!(!world.chunks.contains_key(&key((0, 0))));
}

#[test]
fn ownership_hysteresis_keeps_trailing_edge_chunks() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1); // chunk (0,0)
    let radius = world.config.ownership_radius;
    assert!(world.config.unload_radius >= radius);
    let trailing = key((-radius, 0)); // at the load-radius edge
    assert!(world.chunks.contains_key(&trailing));

    // Cross one chunk forward: the trailing chunk is outside the load
    // radius but inside the unload radius — hysteresis keeps it loaded.
    world.update_ownership(Vec3::new(75.0, 0.0, 25.0), 1); // chunk (1,0)
    assert!(world.chunks.contains_key(&trailing));

    // Move past the unload radius: now it is removed.
    world.update_ownership(Vec3::new(125.0, 0.0, 25.0), 1); // chunk (2,0)
    assert!(!world.chunks.contains_key(&trailing));
}

#[test]
fn chunk_views_are_cached_until_world_changes() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    let first = world.visible_chunk_views();
    let second = world.visible_chunk_views();
    assert_eq!(first.len(), second.len());
    assert!(world.view_cache.is_some(), "views should be cached");

    // An ownership change that loads/unloads chunks must invalidate the
    // cache (revision moves) and produce a different chunk set.
    world.update_ownership(Vec3::new(225.0, 0.0, 25.0), 1);
    let third = world.visible_chunk_views();
    let first_keys: Vec<_> = first.iter().map(|c| c.pos).collect();
    let third_keys: Vec<_> = third.iter().map(|c| c.pos).collect();
    assert_ne!(first_keys, third_keys);
}

#[test]
fn chunks_have_entities_and_items() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    let total_entities: usize = world.chunks.values().map(|c| c.entities.len()).sum();
    let total_items: usize = world.chunks.values().map(|c| c.items.len()).sum();
    assert!(total_entities > 0, "should have entities");
    assert!(total_items > 0, "should have items");
}

#[test]
fn stat_context_reflects_chunk_state() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    let ctx = world.stat_context_for(Vec3::new(25.0, 0.0, 25.0), 0);
    // Chunk (0,0) has 3-5 entities.
    assert!(ctx.entities_visible >= 3);
    assert!(!ctx.chunk_stabilized);
}

#[test]
fn visible_views_include_entities() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    let entities = world.visible_entity_views();
    assert!(!entities.is_empty());
    let chunks = world.visible_chunk_views();
    assert!(!chunks.is_empty());
    let items = world.visible_item_views();
    assert!(!items.is_empty());
}

#[test]
fn visible_chunk_views_are_sorted_for_stable_serialization() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    let views = world.visible_chunk_views();
    let keys: Vec<_> = views
        .iter()
        .map(|c| (c.pos[0], c.layer, c.pos[1]))
        .collect();
    let mut sorted = keys.clone();
    sorted.sort();
    assert_eq!(keys, sorted);
}

#[test]
fn seed_7778_multichunk_views_carry_volumetric_showcase() {
    let mut world = World::new(7778);
    world.generate_initial_structures(1);

    let views = world.visible_chunk_views();
    assert!(views.iter().all(|c| c.chunk_schema == 2));
    assert!(views
        .iter()
        .all(|c| (c.layer_y - chunk::layer_y(c.layer)).abs() < f32::EPSILON));

    // Runtime-safe mode: not every layer-0 chunk carries a volumetric column.
    // Only the RubikGrid showcase chunks and explicit V30A chunks may carry one;
    // ordinary Level 0 chunks fall back to the normal renderer unless the global
    // Level0 volumetric rollout flag is enabled.
    let with_grid: Vec<_> = views
        .iter()
        .filter(|c| c.volumetric_grid.is_some())
        .collect();

    assert!(
        !with_grid.is_empty(),
        "seed 7778 should expose at least the RubikGrid showcase columns"
    );

    assert!(views
        .iter()
        .filter(|c| c.layer != 0)
        .all(|c| c.volumetric_grid.is_none()));

    let rubik: Vec<_> = with_grid
        .iter()
        .filter(|c| {
            c.volumetric_grid
                .as_ref()
                .map(|g| g.source == volumetric_grid::UNIFIED_COLUMN_SOURCE_RUBIKGRID)
                .unwrap_or(false)
        })
        .copied()
        .collect();
    assert_eq!(rubik.len(), 4, "four RubikGrid adapter columns");
    let mut positions: Vec<[i32; 2]> = rubik.iter().map(|c| c.pos).collect();
    positions.sort();
    assert_eq!(positions, vec![[0, 0], [0, 1], [1, 0], [1, 1]]);
    assert!(rubik.iter().all(|c| c.layer == 0));

    for host in &with_grid {
        let grid = host.volumetric_grid.as_ref().unwrap();
        assert!(grid.active);
        assert_eq!(grid.dims, [10, 3, 10]);
        assert_eq!(grid.layer_bands.len(), 3);
        assert!(!grid.faces.is_empty());
    }
    assert!(rubik
        .iter()
        .all(|host| host.volumetric_grid.as_ref().unwrap().atrium_span));

    // The VISFIX validation overlay (its (1,3)/(1,4) volume chunks) is gone.
    assert!(
        !views
            .iter()
            .any(|c| (c.pos == [1, 3] || c.pos == [1, 4]) && !c.inter_layer_volumes.is_empty()),
        "VISFIX overlay volume chunks must be gone by default"
    );

    let total_faces: usize = with_grid
        .iter()
        .map(|c| c.volumetric_grid.as_ref().unwrap().faces.len())
        .sum();
    let total_layers: usize = with_grid
        .iter()
        .map(|c| c.volumetric_grid.as_ref().unwrap().layer_bands.len())
        .sum();
    let total_cells: usize = with_grid
        .iter()
        .map(|c| c.volumetric_grid.as_ref().unwrap().cells.len())
        .sum();
    let total_access: usize = with_grid
        .iter()
        .map(|c| c.volumetric_grid.as_ref().unwrap().vertical_access.len())
        .sum();
    println!(
        "unified_volumetric_v0_ipc columns={} rubik_columns={} total_layers={total_layers} total_cells={total_cells} total_faces={total_faces} total_access={total_access}",
        with_grid.len(),
        rubik.len()
    );
}

#[test]
fn generated_world_has_connected_initial_structure() {
    let mut world = World::new(42);
    world.generate_initial_structures(1);

    assert!(!world.chunks.is_empty());
    assert!(world.chunks.contains_key(&key((0, 0))));

    let start = key((0, 0));
    let mut visited = HashSet::new();
    let mut queue = VecDeque::from([start]);
    while let Some(pos) = queue.pop_front() {
        if !visited.insert(pos) {
            continue;
        }
        for next in layered_graph_neighbors(pos, &world.chunks) {
            if !visited.contains(&next) {
                queue.push_back(next);
            }
        }
    }

    assert_eq!(visited.len(), world.chunks.len());
}

#[test]
fn world_sync_contains_generated_structure_chunks() {
    let mut world = World::new(42);
    world.generate_initial_structures(1);

    let sync_chunks: Vec<crate::network::protocol::ChunkSyncData> = world
        .chunks
        .values()
        .map(crate::network::sync::chunk_to_sync_data)
        .collect();

    assert_eq!(sync_chunks.len(), world.chunks.len());
    assert!(sync_chunks
        .iter()
        .any(|c| c.template_id == architecture::TEMPLATE_STORAGE_ROOM));
    assert!(sync_chunks
        .iter()
        .any(|c| c.template_id == architecture::TEMPLATE_INTERSECTION));
    assert!(sync_chunks
        .iter()
        .filter(|c| c.layer == 0)
        .all(|c| !c.items.is_empty()
            || c.template_id == architecture::TEMPLATE_SAFE_ROOM
            || c.layout.vertical_flags
                & (chunk::V30A_CONNECTOR
                    | chunk::V30A_ATRIUM_VOID_ROOM
                    | chunk::V30A_DEEP_PRECIPICE_PLACEHOLDER)
                != 0));
}

#[test]
fn interaction_pickup_still_removes_generated_item() {
    let mut world = World::new(42);
    world.generate_initial_structures(1);
    let item = world
        .chunks
        .values()
        .flat_map(|chunk| chunk.items.iter())
        .next()
        .cloned()
        .expect("structured world should have an item");
    let revision_before = world.revision;

    let result = world.interact_with_item(item.id, item.position, 5.0);

    assert!(result.is_ok());
    assert_eq!(world.revision, revision_before + 1);
    assert!(!world
        .chunks
        .values()
        .any(|chunk| chunk.items.iter().any(|candidate| candidate.id == item.id)));
}

#[test]
fn world_sync_replaces_local_chunks_and_preserves_local_id() {
    let mut host_world = World::new(1234);
    host_world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    let sync_chunks: Vec<crate::network::protocol::ChunkSyncData> = host_world
        .chunks
        .values()
        .take(3)
        .map(crate::network::sync::chunk_to_sync_data)
        .collect();

    let mut joiner_world = World::new(9999);
    joiner_world.update_ownership(Vec3::new(1000.0, 0.0, 1000.0), 77);
    let local_id = 77;

    joiner_world.apply_world_sync(host_world.seed, host_world.revision, &sync_chunks, local_id);

    assert_eq!(joiner_world.seed, host_world.seed);
    assert_eq!(joiner_world.revision, host_world.revision);
    assert_eq!(joiner_world.chunks.len(), sync_chunks.len());
    assert!(joiner_world
        .chunks
        .values()
        .all(|chunk| chunk.owner == Some(local_id)));
}

#[test]
fn dropped_item_ids_are_partitioned_by_peer() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    let pos = Vec3::new(25.0, 1.8, 25.0);

    let id_from_peer_1 = world
        .spawn_dropped_item(pos, crate::player::inventory::Item::Metal, 1, 1)
        .expect("chunk should be loaded at pos after update_ownership");
    let id_from_peer_2 = world
        .spawn_dropped_item(pos, crate::player::inventory::Item::Metal, 1, 2)
        .expect("chunk should be loaded at pos after update_ownership");

    assert_ne!(
        id_from_peer_1 >> 16,
        id_from_peer_2 >> 16,
        "ids acunados por peers distintos deben tener distinto peer_id en los bits altos"
    );
    assert_eq!(
        id_from_peer_1 >> 16,
        1,
        "el peer_id 1 debe quedar en los bits altos del id"
    );
    assert_eq!(
        id_from_peer_2 >> 16,
        2,
        "el peer_id 2 debe quedar en los bits altos del id"
    );
}

#[test]
fn valid_item_interaction_removes_item_and_increments_revision_once() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    let item = world
        .chunks
        .values()
        .flat_map(|chunk| chunk.items.iter())
        .next()
        .cloned()
        .expect("world should have at least one item");
    let revision_before = world.revision;

    let result = world.interact_with_item(item.id, item.position, 5.0);

    assert!(result.is_ok());
    assert_eq!(world.revision, revision_before + 1);
    assert!(!world
        .chunks
        .values()
        .any(|chunk| chunk.items.iter().any(|candidate| candidate.id == item.id)));

    let revision_after_first = world.revision;
    let duplicate = world.interact_with_item(item.id, item.position, 5.0);

    assert!(duplicate.is_err());
    assert_eq!(world.revision, revision_after_first);
}

#[test]
fn item_interaction_rejects_missing_and_too_far_targets() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    let item = world
        .chunks
        .values()
        .flat_map(|chunk| chunk.items.iter())
        .next()
        .cloned()
        .expect("world should have at least one item");
    let revision_before = world.revision;

    let missing = world.interact_with_item(u32::MAX, item.position, 5.0);
    assert!(missing.is_err());
    assert_eq!(world.revision, revision_before);

    let too_far = world.interact_with_item(item.id, Vec3::new(9999.0, 0.0, 9999.0), 5.0);
    assert!(too_far.is_err());
    assert_eq!(world.revision, revision_before);
}

#[test]
fn remote_world_reset_uses_host_seed_and_clears_local_chunks() {
    let mut world = World::new(9999);
    world.update_ownership(Vec3::new(1000.0, 0.0, 1000.0), 77);
    assert!(!world.chunks.is_empty());

    world.reset_for_remote_world(1234, 5);

    assert_eq!(world.seed, 1234);
    assert_eq!(world.revision, 5);
    assert!(world.chunks.is_empty());
    assert!(world.respawn_queue.is_empty());
}

#[test]
fn teleportation_fires_when_timer_expires() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    // Force a chunk to teleport.
    if let Some(chunk) = world.chunks.get_mut(&key((0, 0))) {
        chunk.teleport_timer = 0.5; // Will expire on next 1hz tick.
    }
    let old_seed = world.chunks[&key((0, 0))].seed;
    let events = world.tick_teleportation(1);
    // The chunk at (0,0) should have teleported.
    let new_seed = world.chunks[&key((0, 0))].seed;
    assert_ne!(
        old_seed, new_seed,
        "chunk seed should change after teleport"
    );
    assert!(!events.is_empty(), "should emit teleport event");
    assert_eq!(events[0].event.event_type, "chunk_teleported");
}

/// The broadcast side of a displacement must carry the seed that was actually applied
/// to the chunk. It used to be reconstructed from the event JSON, which never carried
/// the seed, so the P2P broadcast went out with a hardcoded 0.
#[test]
fn teleport_outcome_carries_applied_seed() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    if let Some(chunk) = world.chunks.get_mut(&key((0, 0))) {
        chunk.teleport_timer = 0.5;
    }
    let old_seed = world.chunks[&key((0, 0))].seed;

    let outcomes = world.tick_teleportation(1);
    let outcome = outcomes
        .iter()
        .find(|o| o.old_pos == [0, 0])
        .expect("chunk (0,0) should have teleported");

    assert_eq!(
        outcome.new_seed,
        world.chunks[&key((0, 0))].seed,
        "the broadcast seed must be the seed the owner applied to the chunk"
    );
    assert_ne!(outcome.new_seed, old_seed, "the seed must have changed");
    assert_ne!(outcome.new_seed, 0, "0 is the bug this test exists for");

    // Event and broadcast must never describe different displacements.
    let data = outcome.event.data.as_object().unwrap();
    let chunk_pos = data["chunk_pos"].as_array().unwrap();
    let offset = data["new_offset"].as_array().unwrap();
    assert_eq!(
        outcome.old_pos,
        [
            chunk_pos[0].as_i64().unwrap() as i32,
            chunk_pos[1].as_i64().unwrap() as i32
        ]
    );
    assert_eq!(
        outcome.new_pos,
        [
            outcome.old_pos[0] + offset[0].as_i64().unwrap() as i32,
            outcome.old_pos[1] + offset[1].as_i64().unwrap() as i32
        ]
    );
}

/// The determinism invariant the whole sync model rests on: geometry is not replicated,
/// it is regenerated from a seed. Give a peer the owner's seed and its chunk content
/// must match the owner's exactly.
///
/// This is the test that proves the bug, not just the fix: with the hardcoded
/// `new_seed = 0` the peer regenerated from seed 0 and every assertion below fails
/// (`template_id`, `rotation`, entity and item sets all differ), while the owner
/// stayed on its real seed — a live divergence with the host still alive.
#[test]
fn remote_teleport_with_owner_seed_converges() {
    let mut owner = World::new(42);
    owner.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    if let Some(chunk) = owner.chunks.get_mut(&key((0, 0))) {
        chunk.teleport_timer = 0.5;
    }
    let outcomes = owner.tick_teleportation(1);
    let outcome = outcomes
        .iter()
        .find(|o| o.old_pos == [0, 0])
        .expect("chunk (0,0) should have teleported");

    let mut peer = World::new(42);
    peer.update_ownership(Vec3::new(25.0, 0.0, 25.0), 2);
    peer.apply_remote_teleport(outcome.old_pos, outcome.new_seed);

    let a = &owner.chunks[&key((0, 0))];
    let b = &peer.chunks[&key((0, 0))];
    assert_eq!(a.seed, b.seed);
    assert_eq!(a.template_id, b.template_id);
    assert_eq!(a.rotation, b.rotation);
    assert_eq!(a.mirrored, b.mirrored);
    assert_eq!(a.has_workbench, b.has_workbench);
    assert_eq!(
        a.entities.iter().map(|e| e.id).collect::<Vec<_>>(),
        b.entities.iter().map(|e| e.id).collect::<Vec<_>>(),
        "entity spawns must match — they come from the same seed"
    );
    assert_eq!(
        a.items.iter().map(|i| i.id).collect::<Vec<_>>(),
        b.items.iter().map(|i| i.id).collect::<Vec<_>>(),
        "loot must match — it comes from the same seed"
    );
}

/// Regression guard for the owner/peer split in chunk displacement.
///
/// `apply_remote_teleport` used to assign `chunk.layout = gen.layout`, which its
/// owner-side twin `tick_teleportation` never did. That one line moved the server
/// collision source AND the respawn resolver's map out from under geometry that does
/// not move (grid_gen is keyed by world_seed+pos, not by chunk.seed), and could revert
/// a V30A chunk wholesale on the next ownership pass. The two paths must mutate the
/// SAME field set — that is the invariant these two tests pin down.
#[test]
fn apply_remote_teleport_leaves_layout_untouched() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);

    let before = world.chunks[&key((0, 0))].layout.clone();
    let old_seed = world.chunks[&key((0, 0))].seed;

    world.apply_remote_teleport([0, 0], old_seed.wrapping_add(0xABCD));

    let chunk = &world.chunks[&key((0, 0))];
    assert_eq!(
        chunk.layout.cells, before.cells,
        "layout cells are the server collision source — they must not move"
    );
    assert_eq!(
        chunk.layout.vertical_flags, before.vertical_flags,
        "clearing vertical_flags makes chunk_is_v30a false and reverts the chunk"
    );
    assert_eq!(
        chunk.layout.macro_id, before.macro_id,
        "structure metadata must survive a displacement"
    );
    assert_eq!(
        chunk.layout.zone_kind, before.zone_kind,
        "zone_kind drives tint, wall model and loot profile on the client"
    );
    assert_eq!(
        chunk.layout.edge_openings, before.edge_openings,
        "level-0 edge continuity must survive a displacement"
    );
    // ...but the displacement itself must still have happened.
    assert_ne!(
        chunk.seed, old_seed,
        "seed must still change on a remote teleport"
    );
}

/// The owner-side half of the same invariant: `tick_teleportation` must not start
/// touching `layout` either. Both halves are asserted so a future "let's make the twins
/// symmetric" edit fails here instead of shipping.
#[test]
fn tick_teleportation_leaves_layout_untouched() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    if let Some(chunk) = world.chunks.get_mut(&key((0, 0))) {
        chunk.teleport_timer = 0.5;
    }

    let before = world.chunks[&key((0, 0))].layout.clone();
    let old_seed = world.chunks[&key((0, 0))].seed;

    world.tick_teleportation(1);

    let chunk = &world.chunks[&key((0, 0))];
    assert_eq!(chunk.layout.cells, before.cells);
    assert_eq!(chunk.layout.vertical_flags, before.vertical_flags);
    assert_eq!(chunk.layout.macro_id, before.macro_id);
    assert_eq!(chunk.layout.zone_kind, before.zone_kind);
    assert_ne!(
        chunk.seed, old_seed,
        "the displacement must still have happened"
    );
}

#[test]
fn entity_tick_produces_damage() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
    // Place an entity right on top of the player in aggro state.
    let chunk = world.chunks.get_mut(&key((0, 0))).unwrap();
    chunk.entities.clear();
    let mut e = entity::Entity::new(9999, entity::EntityType::Lurker, Vec3::new(25.0, 0.0, 25.0));
    e.state = entity::EntityState::Aggro {
        target: 1,
        attack_cooldown: 0.0,
    };
    chunk.entities.push(e);
    let (damage, events) = world.tick_entities(0.1, Vec3::new(25.5, 0.0, 25.0), 1);
    assert!(damage > 0.0, "entity should deal damage");
    assert!(!events.is_empty());
}

#[test]
fn world_stores_world_graph_after_generate() {
    let mut world = World::new(42);

    assert!(
        world.world_graph.is_none(),
        "world_graph must be None before generate_initial_structures"
    );

    world.generate_initial_structures(1);

    let graph = world
        .world_graph
        .as_ref()
        .and_then(|wg| wg.level0_region_graph())
        .expect("world_graph must contain level0 region graph");

    assert!(graph.node_count() > 0);
}

#[test]
fn world_graph_level0_region_graph_validates() {
    use levels::level_0::validation::validate_level0_region_graph;

    let mut world = World::new(0);
    world.generate_initial_structures(1);

    let graph = world
        .world_graph
        .as_ref()
        .and_then(|wg| wg.level0_region_graph())
        .expect("world_graph must contain level0 region graph");

    assert!(
        validate_level0_region_graph(graph),
        "stored graph must pass validation"
    );

    assert!(
        graph.accessible_node_count() > 0,
        "graph must have accessible nodes"
    );
}

fn assert_region_graph_connectivity_parity(seed: u64) {
    let mut world = World::new(seed);
    world.generate_initial_structures(1);

    let graph = world
        .world_graph
        .as_ref()
        .and_then(|wg| wg.level0_region_graph())
        .expect("world_graph must contain level0 region graph");

    let starter_id = starter_node_id(graph)
        .unwrap_or_else(|| panic!("seed {seed}: starter_node_id must resolve"));
    let reachable = reachable_from(graph, starter_id);
    assert_eq!(
        reachable.len(),
        graph.node_count(),
        "seed {seed}: all structures must be reachable from starter"
    );

    // Chunk-level BFS parity: the world must also be fully connected at
    // chunk granularity using the existing layered_graph_neighbors helper.
    let start = (0i32, 0i8, 0i32);
    let mut visited = HashSet::new();
    let mut queue = VecDeque::from([start]);
    while let Some(pos) = queue.pop_front() {
        if !visited.insert(pos) {
            continue;
        }
        for next in layered_graph_neighbors(pos, &world.chunks) {
            if !visited.contains(&next) {
                queue.push_back(next);
            }
        }
    }
    assert_eq!(
        visited.len(),
        world.chunks.len(),
        "seed {seed}: chunk-level BFS must be fully connected"
    );
}

#[test]
fn world_region_graph_connectivity_parity_seed_0() {
    assert_region_graph_connectivity_parity(0);
}

#[test]
fn world_region_graph_connectivity_parity_seed_42() {
    assert_region_graph_connectivity_parity(42);
}

#[test]
fn world_region_graph_connectivity_parity_seed_7778() {
    assert_region_graph_connectivity_parity(7778);
}

#[test]
fn world_region_graph_reachable_output_is_deterministic() {
    let mut world = World::new(42);
    world.generate_initial_structures(1);

    let graph = world
        .world_graph
        .as_ref()
        .and_then(|wg| wg.level0_region_graph())
        .expect("world_graph must contain level0 region graph");
    let starter_id = starter_node_id(graph).expect("seed 42 must have a starter node");

    let reachable_a = reachable_from(graph, starter_id);
    let reachable_b = reachable_from(graph, starter_id);
    assert_eq!(
        reachable_a, reachable_b,
        "reachable_from must produce identical output on repeated calls"
    );
    assert!(!reachable_a.is_empty());

    // Phase 3.1E contract: output must be sorted.
    let mut sorted = reachable_a.clone();
    sorted.sort_unstable();
    assert_eq!(reachable_a, sorted, "reachable_from output must be sorted");
}
#[test]
fn world_graph_has_valid_level0_region_graph() {
    let mut world = World::new(42);
    world.generate_initial_structures(1);

    let wg = world
        .world_graph
        .as_ref()
        .expect("world_graph debe existir");
    let rg = wg
        .level0_region_graph()
        .expect("level0 region graph debe existir");

    assert!(rg.node_count() > 0);
    assert!(rg.edge_count() > 0);
    assert_eq!(wg.world_seed, 42);
    assert!(wg.level0().is_some());
    assert_eq!(wg.level0().unwrap().region_count(), 1);
}
