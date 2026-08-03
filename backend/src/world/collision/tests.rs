use super::edge_blocks_movement as ebm;
use super::*;
use crate::utils::chunk_center;
use crate::world::architecture::{build_chunk_layout, TEMPLATE_ROOM_BASIC};
use crate::world::chunk::{
    EDGE_KIND_ARCH, EDGE_KIND_DOOR, EDGE_KIND_WALL, FLOOR_RAMP_NORTH_SOUTH, LAYOUT_GRID_SIZE,
};
use crate::world::generator::generate_chunk;
use crate::world::World;

fn key(pos: (i32, i32)) -> LayeredChunkPos {
    layered_chunk_pos(pos, 0)
}

/// A single all-floor chunk with every edge open (no walls). Tests then add
/// the specific edge walls / cell blockers they care about.
fn clean_world(pos: (i32, i32)) -> World {
    clean_world_layer(pos, 0)
}

fn clean_world_layer(pos: (i32, i32), layer: ChunkLayer) -> World {
    let mut world = World::new(1);
    let mut chunk = crate::world::generator::generate_chunk_layer(1, pos, layer);
    let g = LAYOUT_GRID_SIZE as usize;
    chunk.layout.cells = vec![CELL_WALKABLE; g * g];
    chunk.layout.edges_v = vec![EDGE_KIND_OPEN; (g + 1) * g];
    chunk.layout.edges_h = vec![EDGE_KIND_OPEN; g * (g + 1)];
    chunk.layout.floor_profile = FLOOR_FLAT;
    chunk.layout.vertical_flags = 0;
    world.chunks.insert(layered_chunk_pos(pos, layer), chunk);
    world
}

fn set_cell(world: &mut World, pos: (i32, i32), x: usize, z: usize, flags: u16) {
    let chunk = world.chunks.get_mut(&key(pos)).unwrap();
    if let Some(idx) = chunk.layout.cell_index(x, z) {
        chunk.layout.cells[idx] = flags;
    }
}

fn set_v_edge(world: &mut World, pos: (i32, i32), bx: usize, z: usize, kind: u8) {
    world
        .chunks
        .get_mut(&key(pos))
        .unwrap()
        .layout
        .set_edge_v(bx, z, kind);
}

// ── Edge-wall movement rules (Phase 2.7) ──

#[test]
fn open_floor_is_not_blocked() {
    let world = clean_world((0, 0));
    assert!(!is_blocked_at(
        &world,
        Vec3::new(27.5, 1.8, 27.5),
        PLAYER_RADIUS
    ));
}

fn assert_edge_blocks(kind: u8) {
    // Vertical wall edge at column bx=6 (world x=30), all rows.
    let mut world = clean_world((0, 0));
    for z in 0..LAYOUT_GRID_SIZE as usize {
        set_v_edge(&mut world, (0, 0), 6, z, kind);
    }
    // A point on the wall line is blocked iff the edge kind blocks.
    let on_wall = is_blocked_at(&world, Vec3::new(30.0, 1.8, 27.5), PLAYER_RADIUS);
    assert_eq!(on_wall, ebm(kind), "edge kind {kind} block mismatch");
}

#[test]
fn full_wall_edge_blocks() {
    assert_edge_blocks(EDGE_KIND_WALL);
}

#[test]
fn partition_edge_blocks() {
    assert_edge_blocks(EDGE_KIND_PARTITION);
}

#[test]
fn low_wall_edge_blocks() {
    assert_edge_blocks(EDGE_KIND_LOW_WALL);
}

#[test]
fn half_wall_edge_blocks() {
    assert_edge_blocks(EDGE_KIND_HALF_WALL);
}

#[test]
fn false_door_edge_blocks() {
    assert_edge_blocks(EDGE_KIND_FALSE_DOOR);
}

#[test]
fn door_edge_passes() {
    assert_edge_blocks(EDGE_KIND_DOOR);
}

#[test]
fn arch_edge_passes() {
    assert_edge_blocks(EDGE_KIND_ARCH);
}

#[test]
fn doorway_in_a_wall_is_passable_only_at_the_door() {
    let mut world = clean_world((0, 0));
    for z in 0..LAYOUT_GRID_SIZE as usize {
        set_v_edge(&mut world, (0, 0), 6, z, EDGE_KIND_WALL);
    }
    set_v_edge(&mut world, (0, 0), 6, 5, EDGE_KIND_DOOR); // door at row 5
                                                          // At the door row (z≈27.5) the boundary is passable.
    assert!(!is_blocked_at(
        &world,
        Vec3::new(30.0, 1.8, 27.5),
        PLAYER_RADIUS
    ));
    // At a walled row (z≈12.5) it is blocked.
    assert!(is_blocked_at(
        &world,
        Vec3::new(30.0, 1.8, 12.5),
        PLAYER_RADIUS
    ));
}

#[test]
fn pillar_cell_blocks() {
    let mut world = clean_world((0, 0));
    set_cell(&mut world, (0, 0), 5, 5, CELL_WALKABLE | CELL_PILLAR);
    assert!(is_blocked_at(
        &world,
        Vec3::new(27.5, 1.8, 27.5),
        PLAYER_RADIUS
    ));
}

#[test]
fn pit_cell_blocks() {
    let mut world = clean_world((0, 0));
    set_cell(&mut world, (0, 0), 5, 5, CELL_WALKABLE | CELL_PIT);
    assert!(is_blocked_at(
        &world,
        Vec3::new(27.5, 1.8, 27.5),
        PLAYER_RADIUS
    ));
}

#[test]
fn blocked_cell_blocks() {
    let mut world = clean_world((0, 0));
    set_cell(&mut world, (0, 0), 5, 5, CELL_BLOCKED);
    assert!(is_blocked_at(
        &world,
        Vec3::new(27.5, 1.8, 27.5),
        PLAYER_RADIUS
    ));
}

#[test]
fn slides_along_wall_edge() {
    // Vertical wall at column 6 (x=30). Pushing east into it + south should
    // slide along Z. Per-frame steps are small so the move stops at the wall.
    let mut world = clean_world((0, 0));
    for z in 0..LAYOUT_GRID_SIZE as usize {
        set_v_edge(&mut world, (0, 0), 6, z, EDGE_KIND_WALL);
    }
    let from = Vec3::new(29.6, 1.8, 25.0);
    let desired = Vec3::new(29.9, 1.8, 28.0);
    let result = Level0Collision::resolve_move(&world, from, desired);
    assert_eq!(result.kind, CollisionResultKind::SlidZ);
    assert!(
        (result.position.x - from.x).abs() < 0.001,
        "x should be held"
    );
    assert!(result.position.z > from.z, "z should advance");
}

// ─── ADR-026 (enmienda 2026-07-06, parte 3): claimed client Y ───

#[test]
fn claimed_y_within_step_is_accepted_not_flattened() {
    // A real jump/fall: |claimed.y − from.y| ≤ MAX_CLAIMED_Y_STEP → the client's Y IS
    // the result Y (no floor pin). Ascending and descending both accepted.
    let world = clean_world((0, 0));
    let from = Vec3::new(25.0, 1.8, 25.0);

    let up = Level0Collision::resolve_move(&world, from, Vec3::new(25.3, 2.9, 25.0));
    assert_eq!(up.kind, CollisionResultKind::Free);
    assert!(
        (up.position.y - 2.9).abs() < 0.001,
        "ascending claimed Y must be accepted, got {}",
        up.position.y
    );

    let down = Level0Collision::resolve_move(&world, from, Vec3::new(25.3, 0.4, 25.0));
    assert!(
        (down.position.y - 0.4).abs() < 0.001,
        "descending claimed Y must be accepted, got {}",
        down.position.y
    );
}

#[test]
fn claimed_y_absurd_step_falls_back_to_floor() {
    // Vertical teleport / compromised client: |Δy| > MAX_CLAIMED_Y_STEP → the Y falls
    // back to floor_player_y for THAT tick; the XZ move itself is not rejected.
    // Non-finite Y is likewise never accepted.
    let world = clean_world((0, 0));
    let from = Vec3::new(25.0, 1.8, 25.0);
    let floor = Level0Collision::floor_player_y(&world, from);

    let teleport = Level0Collision::resolve_move(&world, from, Vec3::new(25.3, 50.0, 25.0));
    assert_eq!(
        teleport.kind,
        CollisionResultKind::Free,
        "XZ move must not be rejected"
    );
    assert!(
        (teleport.position.y - floor).abs() < 0.001,
        "absurd claimed Y must flatten to floor {floor}, got {}",
        teleport.position.y
    );

    let nan = Level0Collision::resolve_move(&world, from, Vec3::new(25.3, f32::NAN, 25.0));
    assert!(
        (nan.position.y - floor).abs() < 0.001,
        "non-finite claimed Y must flatten to floor, got {}",
        nan.position.y
    );
}

#[test]
fn claimed_y_blocked_keeps_from_y() {
    // Fully blocked XZ: the player stays put and KEEPS its previous Y (ADR-026 parte 3:
    // "se mantiene from.y solo si el movimiento queda totalmente bloqueado") — it is
    // NOT floor-pinned (from.y was itself accepted/clamped on a previous tick).
    let mut world = clean_world((0, 0));
    // Box in the +X, +Z and diagonal target cells around cell (4,4) (centre 22.5, 22.5).
    set_cell(&mut world, (0, 0), 5, 4, CELL_BLOCKED);
    set_cell(&mut world, (0, 0), 4, 5, CELL_BLOCKED);
    set_cell(&mut world, (0, 0), 5, 5, CELL_BLOCKED);
    let from = Vec3::new(22.5, 2.5, 22.5); // mid-jump Y from a previous accepted tick
    let result = Level0Collision::resolve_move(&world, from, Vec3::new(26.0, 2.6, 26.0));
    assert_eq!(result.kind, CollisionResultKind::Blocked);
    assert!(
        (result.position.y - 2.5).abs() < 0.001,
        "blocked must keep from.y (2.5), got {}",
        result.position.y
    );
}

#[test]
fn entity_sim_path_keeps_floor_pin() {
    // The entity path (phantom, ADR-016/017) has no client-reported Y — it must keep
    // the historical floor pin for its grounding even if `desired.y` drifts.
    let world = clean_world((0, 0));
    let mut sim = SimChunkCache::new(world.seed);
    let from = Vec3::new(25.0, 3.0, 25.0);
    let result =
        Level0Collision::resolve_move_simulated(&world, &mut sim, from, Vec3::new(25.3, 3.0, 25.0));
    let floor = Level0Collision::floor_player_y(&world, from);
    assert!(
        (result.position.y - floor).abs() < 0.001,
        "entity path must stay floor-pinned at {floor}, got {}",
        result.position.y
    );
}

#[test]
fn cross_chunk_boundary_gap_is_open_and_walls_block() {
    // Two real generated chunks share a boundary with a centred 2-cell gap.
    let mut world = World::new(1);
    let mut a = generate_chunk(1, (0, 0));
    a.layout = build_chunk_layout(TEMPLATE_ROOM_BASIC, 0);
    let mut b = generate_chunk(1, (1, 0));
    b.layout = build_chunk_layout(TEMPLATE_ROOM_BASIC, 0);
    world.chunks.insert(key((0, 0)), a);
    world.chunks.insert(key((1, 0)), b);

    // The boundary gap is at rows 4–5 (world z 20–30); z≈27.5 is open.
    assert!(!is_blocked_at(
        &world,
        Vec3::new(50.0, 1.8, 27.5),
        PLAYER_RADIUS
    ));
    // A non-gap boundary cell (z≈12.5) is a wall.
    assert!(is_blocked_at(
        &world,
        Vec3::new(50.0, 1.8, 12.5),
        PLAYER_RADIUS
    ));
}

// ── Spawn resolver (Phase 2.6/2.7) ──

#[test]
fn spawn_resolver_finds_safe_cell_in_open_chunk() {
    let mut world = clean_world((0, 0));
    let res = resolve_safe_spawn(&mut world, Vec3::new(25.0, 1.8, 25.0));
    assert!(!is_blocked_at(
        &world,
        res.position,
        PLAYER_RADIUS + SPAWN_CLEARANCE_MARGIN
    ));
}

// ── ADR-031 trust-the-bed (risk C mode ii) ──

#[test]
fn try_bed_spawn_accepts_a_clear_bed_position() {
    // A walkable bed cell is accepted verbatim (XZ preserved, Y from the floor).
    let world = clean_world((2, 2));
    let bed = Vec3::new(2.0 * CHUNK_SIZE + 25.0, 1.8, 2.0 * CHUNK_SIZE + 25.0);
    let res = try_bed_spawn(&world, bed).expect("a clear bed spot must resolve");
    assert!(
        (res.position.x - bed.x).abs() < 0.01 && (res.position.z - bed.z).abs() < 0.01,
        "trusted spawn keeps the bed XZ, got {:?}",
        res.position
    );
    assert!(!is_blocked_at(
        &world,
        res.position,
        PLAYER_RADIUS + SPAWN_CLEARANCE_MARGIN
    ));
}

#[test]
fn try_bed_spawn_rejects_a_blocked_bed_position() {
    // A wall line crossing the bed's capsule leaves no clearance → None (caller keeps the fallback).
    let mut world = clean_world((2, 2));
    for z in 0..LAYOUT_GRID_SIZE as usize {
        set_v_edge(&mut world, (2, 2), 5, z, EDGE_KIND_WALL); // wall the x=25 (local) boundary
    }
    let bed = Vec3::new(2.0 * CHUNK_SIZE + 25.0, 1.8, 2.0 * CHUNK_SIZE + 25.0); // sits on the wall
    assert!(is_blocked_at(
        &world,
        bed,
        PLAYER_RADIUS + SPAWN_CLEARANCE_MARGIN
    ));
    assert!(try_bed_spawn(&world, bed).is_none());
}

#[test]
fn try_bed_spawn_recovers_where_resolve_safe_spawn_would_repair() {
    // Bed on a walkable but NON-FLAT chunk: the resolver rejects every cell (→ Repaired), but
    // trust-the-bed still accepts the walkable spot — the exact risk-C scenario.
    let mut world = clean_world((3, 3));
    world
        .chunks
        .get_mut(&key((3, 3)))
        .unwrap()
        .layout
        .vertical_flags = 1; // non-flat → resolver rejects
    let bed = Vec3::new(3.0 * CHUNK_SIZE + 25.0, 1.8, 3.0 * CHUNK_SIZE + 25.0);
    assert_eq!(
        resolve_safe_spawn(&mut world, bed).method,
        SpawnMethod::Repaired,
        "a non-flat bed chunk must make the resolver fall back"
    );
    let res =
        try_bed_spawn(&world, bed).expect("trust-the-bed recovers the walkable non-flat spot");
    assert!((res.position.x - bed.x).abs() < 0.01 && (res.position.z - bed.z).abs() < 0.01);
}

#[test]
fn spawn_resolver_relocates_off_blocked_preferred_cell() {
    let mut world = clean_world((0, 0));
    for x in 4..=6 {
        for z in 4..=6 {
            set_cell(&mut world, (0, 0), x, z, CELL_BLOCKED);
        }
    }
    let res = resolve_safe_spawn(&mut world, Vec3::new(27.5, 1.8, 27.5));
    assert_ne!(res.method, SpawnMethod::Preferred);
    assert!(!is_blocked_at(
        &world,
        res.position,
        PLAYER_RADIUS + SPAWN_CLEARANCE_MARGIN
    ));
}

#[test]
fn spawn_resolver_no_edge_wall_crosses_spawn_cell() {
    // A wall edge bisecting the preferred cell must push the spawn elsewhere.
    let mut world = clean_world((0, 0));
    for z in 0..LAYOUT_GRID_SIZE as usize {
        set_v_edge(&mut world, (0, 0), 5, z, EDGE_KIND_WALL); // wall through col-5 boundary
        set_v_edge(&mut world, (0, 0), 6, z, EDGE_KIND_WALL);
    }
    let res = resolve_safe_spawn(&mut world, Vec3::new(27.5, 1.8, 27.5));
    assert!(!is_blocked_at(
        &world,
        res.position,
        PLAYER_RADIUS + SPAWN_CLEARANCE_MARGIN
    ));
}

#[test]
fn spawn_resolver_repairs_when_no_safe_cell_exists() {
    let mut world = clean_world((0, 0));
    let g = LAYOUT_GRID_SIZE as usize;
    for i in 0..g * g {
        world.chunks.get_mut(&key((0, 0))).unwrap().layout.cells[i] = CELL_BLOCKED;
    }
    world.chunks.retain(|pos, _| *pos == key((0, 0)));
    let res = resolve_safe_spawn(&mut world, Vec3::new(25.0, 1.8, 25.0));
    assert_eq!(res.method, SpawnMethod::Repaired);
    assert!(!is_blocked_at(
        &world,
        res.position,
        PLAYER_RADIUS + SPAWN_CLEARANCE_MARGIN
    ));
}

#[test]
fn spawn_resolver_avoids_vertical_floor_chunk() {
    let mut world = clean_world((0, 0));
    if let Some(chunk) = world.chunks.get_mut(&key((0, 0))) {
        chunk.layout.floor_profile = FLOOR_RAMP_NORTH_SOUTH;
        chunk.layout.vertical_flags = 1;
    }
    let mut flat = clean_world((1, 0));
    let flat_chunk = flat.chunks.remove(&key((1, 0))).unwrap();
    world.chunks.insert(key((1, 0)), flat_chunk);

    let res = resolve_safe_spawn(&mut world, Vec3::new(25.0, 1.8, 25.0));
    assert_eq!(res.chunk, (1, 0), "spawn must avoid the ramp chunk");
}

#[test]
fn spawn_resolver_uses_generated_starter_cluster() {
    let mut world = World::new(7778);
    world.generate_initial_structures(1);
    let preferred = chunk_center((0, 0));
    let res = resolve_safe_spawn(
        &mut world,
        Vec3::new(preferred.x, PLAYER_BASE_Y, preferred.z),
    );
    assert!(!is_blocked_at(
        &world,
        res.position,
        PLAYER_RADIUS + SPAWN_CLEARANCE_MARGIN
    ));
    assert!(res.chunk.0.abs() <= SPAWN_SEARCH_CHUNK_RADIUS);
    assert!(res.chunk.1.abs() <= SPAWN_SEARCH_CHUNK_RADIUS);
    // Spawn chunk has at least one exit opening.
    let starter = world.chunks.get(&key(res.chunk)).unwrap();
    assert!(
        starter.layout.edge_openings != 0,
        "spawn chunk has no exits"
    );
}

#[test]
fn spawn_position_is_cell_centered_away_from_chunk_edges() {
    let mut world = clean_world((0, 0));
    let res = resolve_safe_spawn(&mut world, Vec3::new(25.0, 1.8, 25.0));
    let local_x = res.position.x - res.chunk.0 as f32 * CHUNK_SIZE;
    let local_z = res.position.z - res.chunk.1 as f32 * CHUNK_SIZE;
    assert!(local_x > PLAYER_RADIUS && local_x < CHUNK_SIZE - PLAYER_RADIUS);
    assert!(local_z > PLAYER_RADIUS && local_z < CHUNK_SIZE - PLAYER_RADIUS);
}

#[test]
fn floor_player_y_vertical_is_deterministic_and_pit_is_safe() {
    use crate::world::chunk::{FLOOR_PIT_PLACEHOLDER, FLOOR_RAISED, FLOOR_SUNKEN};
    let mut raised = clean_world((5, 5));
    raised
        .chunks
        .get_mut(&key((5, 5)))
        .unwrap()
        .layout
        .floor_profile = FLOOR_RAISED;
    let cr = chunk_center((5, 5));
    let yr = floor_player_y(&raised, Vec3::new(cr.x, 1.8, cr.z));
    assert!((yr - (PLAYER_BASE_Y + 0.35)).abs() < 0.001);
    assert_eq!(yr, floor_player_y(&raised, Vec3::new(cr.x, 1.8, cr.z))); // deterministic

    let mut sunken = clean_world((6, 6));
    sunken
        .chunks
        .get_mut(&key((6, 6)))
        .unwrap()
        .layout
        .floor_profile = FLOOR_SUNKEN;
    let cs = chunk_center((6, 6));
    let ys = floor_player_y(&sunken, Vec3::new(cs.x, 1.8, cs.z));
    assert!((ys - (PLAYER_BASE_Y - 0.25)).abs() < 0.001);

    // Pit placeholder must never drop the player below normal floor (no fall).
    let mut pit = clean_world((7, 7));
    pit.chunks
        .get_mut(&key((7, 7)))
        .unwrap()
        .layout
        .floor_profile = FLOOR_PIT_PLACEHOLDER;
    let cp = chunk_center((7, 7));
    let yp = floor_player_y(&pit, Vec3::new(cp.x, 1.8, cp.z));
    assert!(yp >= PLAYER_BASE_Y - 0.001, "pit lowered the player: {yp}");
}

#[test]
fn floor_player_y_accounts_for_true_layers() {
    let upper = clean_world_layer((2, 2), 1);
    let cu = chunk_center((2, 2));
    let yu = floor_player_y(
        &upper,
        Vec3::new(
            cu.x,
            PLAYER_BASE_Y + crate::world::chunk::LAYER_HEIGHT,
            cu.z,
        ),
    );
    assert!((yu - (PLAYER_BASE_Y + crate::world::chunk::LAYER_HEIGHT)).abs() < 0.001);

    let lower = clean_world_layer((3, 3), -1);
    let cl = chunk_center((3, 3));
    let yl = floor_player_y(
        &lower,
        Vec3::new(
            cl.x,
            PLAYER_BASE_Y - crate::world::chunk::LAYER_HEIGHT,
            cl.z,
        ),
    );
    assert!((yl - (PLAYER_BASE_Y - crate::world::chunk::LAYER_HEIGHT)).abs() < 0.001);
}

#[test]
fn connector_floor_y_is_deterministic_and_safe() {
    let mut world = clean_world((4, 4));
    let chunk = world.chunks.get_mut(&key((4, 4))).unwrap();
    chunk.layout.floor_profile = FLOOR_CONNECTOR_DOWN;
    chunk.layout.vertical_flags = crate::world::chunk::V30A_CONNECTOR;

    let base_x = 4f32 * CHUNK_SIZE + 25.0;
    let south = Vec3::new(base_x, PLAYER_BASE_Y, 4f32 * CHUNK_SIZE + 2.0);
    let north = Vec3::new(base_x, PLAYER_BASE_Y, 4f32 * CHUNK_SIZE + CHUNK_SIZE - 2.0);
    let ys = floor_player_y(&world, south);
    let yn = floor_player_y(&world, north);
    assert!(ys <= PLAYER_BASE_Y + 0.01);
    assert!(yn < PLAYER_BASE_Y - crate::world::chunk::LAYER_HEIGHT * 0.8);
    assert_eq!(yn, floor_player_y(&world, north));
}

#[test]
fn walls_and_doors_work_on_upper_layer() {
    let mut world = clean_world_layer((5, 5), 1);
    for z in 0..LAYOUT_GRID_SIZE as usize {
        let chunk = world.chunks.get_mut(&layered_chunk_pos((5, 5), 1)).unwrap();
        chunk.layout.set_edge_v(6, z, EDGE_KIND_WALL);
    }
    world
        .chunks
        .get_mut(&layered_chunk_pos((5, 5), 1))
        .unwrap()
        .layout
        .set_edge_v(6, 5, EDGE_KIND_DOOR);

    let base = 5f32 * CHUNK_SIZE;
    let y = PLAYER_BASE_Y + crate::world::chunk::LAYER_HEIGHT;
    assert!(is_blocked_at(
        &world,
        Vec3::new(base + 30.0, y, base + 12.5),
        PLAYER_RADIUS
    ));
    assert!(!is_blocked_at(
        &world,
        Vec3::new(base + 30.0, y, base + 27.5),
        PLAYER_RADIUS
    ));
}

#[test]
fn vertical_chunk_wall_blocks_and_door_passes() {
    let mut world = clean_world((4, 4));
    world
        .chunks
        .get_mut(&key((4, 4)))
        .unwrap()
        .layout
        .floor_profile = crate::world::chunk::FLOOR_RAISED;
    for z in 0..LAYOUT_GRID_SIZE as usize {
        set_v_edge(&mut world, (4, 4), 6, z, EDGE_KIND_WALL);
    }
    set_v_edge(&mut world, (4, 4), 6, 5, EDGE_KIND_DOOR);
    let base = 4f32 * CHUNK_SIZE;
    // Walled row blocks; height profile does not let the player cross.
    assert!(is_blocked_at(
        &world,
        Vec3::new(base + 30.0, 1.8, base + 12.5),
        PLAYER_RADIUS
    ));
    // Door row still passes on a vertical chunk.
    assert!(!is_blocked_at(
        &world,
        Vec3::new(base + 30.0, 1.8, base + 27.5),
        PLAYER_RADIUS
    ));
}

#[test]
fn spawn_safe_across_multiple_seeds() {
    for seed in [42u64, 7777, 7778, 1234] {
        let mut world = World::new(seed);
        world.generate_initial_structures(1);
        let res = resolve_safe_spawn(&mut world, Vec3::new(25.0, 1.8, 25.0));
        assert!(
            !is_blocked_at(&world, res.position, PLAYER_RADIUS + SPAWN_CLEARANCE_MARGIN),
            "seed {seed} spawn not clear"
        );
    }
}

// ── ADR-017: sim-only on-demand collision (SimChunkCache) ──

#[test]
fn sim_cache_layout_is_identical_to_the_real_generated_chunk() {
    let world = World::new(42);
    let mut sim = SimChunkCache::new(42);
    let pos = (137, -94); // far from any loaded chunk
    let key = layered_chunk_pos(pos, 0);
    sim.ensure(&world, key);
    let cached = sim
        .layouts
        .get(&key)
        .expect("sim cache must hold the far chunk layout");
    // The SAME deterministic path update_ownership uses for expansion chunks.
    let real = generate_chunk(42, pos).layout;
    assert_eq!(
        *cached, real,
        "sim layout must equal the real generated layout (zero divergence)"
    );
}

#[test]
fn sim_collision_resolves_far_world_that_player_collision_sees_as_unloaded() {
    let world = World::new(42); // no chunks loaded
    let from = Vec3::new(5000.0, 1.8, 5000.0);
    let desired = Vec3::new(5000.5, 1.8, 5000.5);

    // Player/host path: the far chunk is absent → fully blocked as "unloaded".
    let player = Level0Collision::resolve_move(&world, from, desired);
    assert_eq!(player.kind, CollisionResultKind::Blocked);
    assert_eq!(player.reason, "unloaded_chunk");

    // Entity path: the layout is generated on-demand, so the move resolves
    // against real geometry — never "unloaded".
    let mut sim = SimChunkCache::new(world.seed);
    let entity = Level0Collision::resolve_move_simulated(&world, &mut sim, from, desired);
    assert_ne!(
        entity.reason, "unloaded_chunk",
        "sim path must resolve against a generated layout"
    );
    assert!(
        !sim.is_empty(),
        "sim path must have generated + cached the far chunk(s)"
    );
}

#[test]
fn sim_cache_does_not_regenerate_a_present_key() {
    let world = World::new(42);
    let mut sim = SimChunkCache::new(42);
    let key = layered_chunk_pos((137, -94), 0);
    sim.ensure(&world, key);
    let n = sim.len();
    sim.ensure(&world, key); // same key again
    assert_eq!(sim.len(), n, "re-ensuring the same key must not duplicate");
}

#[test]
fn sim_cache_skips_chunks_already_loaded_in_world() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 1.8, 25.0), 1);
    let mut sim = SimChunkCache::new(world.seed);
    // (0,0) is loaded for the host → the sim cache must NOT shadow it.
    sim.ensure(&world, layered_chunk_pos((0, 0), 0));
    assert!(
        sim.is_empty(),
        "sim cache must not cache chunks already present in world.chunks"
    );
}

#[test]
fn sim_cache_evicts_beyond_cap() {
    let world = World::new(42);
    let mut sim = SimChunkCache::new(42);
    // Generate well beyond the cap, all far from a fixed center.
    for i in 0..(SIM_CACHE_MAX_CHUNKS as i32 + 25) {
        sim.ensure(&world, layered_chunk_pos((2000 + i, 0), 0));
    }
    sim.enforce_cap((2000, 0));
    assert!(
        sim.len() <= SIM_CACHE_MAX_CHUNKS,
        "cache must not exceed the cap after eviction (got {})",
        sim.len()
    );
}

#[test]
fn sim_collision_never_touches_world_chunks_or_render() {
    let mut world = World::new(42);
    world.update_ownership(Vec3::new(25.0, 1.8, 25.0), 1);
    let loaded_before = world.chunks.len();
    let views_before = world.visible_chunk_views().len();

    let mut sim = SimChunkCache::new(world.seed);
    let from = Vec3::new(6000.0, 1.8, 6000.0); // far outside the host radius
    let _ = Level0Collision::resolve_move_simulated(
        &world,
        &mut sim,
        from,
        Vec3::new(from.x + 0.5, from.y, from.z + 0.5),
    );

    assert!(
        !sim.is_empty(),
        "the far move must have populated the sim cache"
    );
    assert_eq!(
        world.chunks.len(),
        loaded_before,
        "sim collision must NOT insert into world.chunks"
    );
    assert_eq!(
        world.visible_chunk_views().len(),
        views_before,
        "sim-only chunks must NEVER reach render (build_chunk_views)"
    );
}
