//! Level 0 collision + authoritative spawn resolution (Phase 2.6).
//!
//! The backend is the authority for player position. This module answers two
//! questions for the game loop:
//!   1. "Can the player move from A to B?" (`Level0Collision::resolve_move`)
//!   2. "Where is it safe to put the player?" (`resolve_safe_spawn`)
//!
//! Both queries read the backend-authored `ChunkLayoutV1` cell grid — the same
//! grid Unity renders — so collision matches what the player sees.

use log::info;

use crate::utils::{world_to_chunk, ChunkPos, Vec3, CHUNK_SIZE};
use crate::world::chunk::{
    layer_y, layered_chunk_pos, ChunkLayer, ChunkLayoutV1, LayeredChunkPos, CELL_ANOMALY,
    CELL_BLOCKED, CELL_HAZARD, CELL_PILLAR, CELL_PIT, CELL_RAMP, CELL_SAFE, CELL_WALKABLE,
    CELL_WALL, EDGE_KIND_FALSE_DOOR, EDGE_KIND_HALF_WALL, EDGE_KIND_LOW_WALL, EDGE_KIND_OPEN,
    EDGE_KIND_PARTITION, EDGE_KIND_WALL, FLOOR_CONNECTOR_DOWN, FLOOR_CONNECTOR_UP, FLOOR_FLAT,
};
use crate::world::World;

pub const PLAYER_RADIUS: f32 = 0.35;
/// Player-pivot convention: a grounded player's transform sits this far above the floor
/// (a standing player on layer 0, floor world Y ≈ 0, reports transform.y = PLAYER_BASE_Y).
/// MIRRORED (not wire-synced) in Unity as `GridConstants.PlayerBaseY` — the remote-proxy
/// grounding fix subtracts it to place a feet-pivoted avatar on the rendered floor. Keep the
/// two in lockstep; exposing it over the wire would be a protocol change (new ADR).
pub(crate) const PLAYER_BASE_Y: f32 = 1.8;
const FLOOR_LEVEL_HEIGHT: f32 = 1.5;

/// ADR-026 (enmienda 2026-07-06, parte 3): max |claimed.y − from.y| per processed tick for
/// the player's client-reported Y to be ACCEPTED instead of floor-pinned. Plausibility clamp,
/// not real anti-cheat (same trust level as X/Z since ADR-009 Option B). Value rationale:
/// (i) matches the project's already-calibrated vertical-teleport threshold
/// (`ProxyJumpFeeder._verticalTeleportDistance` = 3.0 m single-frame); (ii) < LAYER_HEIGHT
/// (4 m) so an instant layer change never passes as continuous movement; (iii) worst-case
/// legitimate motion between ~30 Hz input samples at terminal fall speed (~25 m/s) is ~0.8 m,
/// so 3.0 gives 3-4x headroom for network jitter.
// TODO(balance): conservative initial value, pending a dedicated calibration pass.
pub const MAX_CLAIMED_Y_STEP: f32 = 3.0;

/// Whether an edge kind blocks player movement across the boundary.
pub fn edge_blocks_movement(kind: u8) -> bool {
    matches!(
        kind,
        EDGE_KIND_WALL
            | EDGE_KIND_LOW_WALL
            | EDGE_KIND_HALF_WALL
            | EDGE_KIND_PARTITION
            | EDGE_KIND_FALSE_DOOR
    )
}

/// Whether an edge kind is rendered as a full-height solid wall face.
pub fn edge_is_full_wall(kind: u8) -> bool {
    matches!(
        kind,
        EDGE_KIND_WALL | EDGE_KIND_PARTITION | EDGE_KIND_FALSE_DOOR
    )
}

/// Extra clearance required around a spawn capsule beyond the player radius.
pub const SPAWN_CLEARANCE_MARGIN: f32 = 0.20;
/// How far (in chunks) the spawn resolver searches around the preferred point.
const SPAWN_SEARCH_CHUNK_RADIUS: i32 = 3;
/// Cap rejection log spam during spawn resolution.
const SPAWN_REJECT_LOG_CAP: u32 = 12;

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum CollisionResultKind {
    Free,
    SlidX,
    SlidZ,
    Blocked,
}

#[derive(Debug, Clone, Copy)]
pub struct CollisionResolve {
    pub position: Vec3,
    pub kind: CollisionResultKind,
    pub chunk_pos: (i32, i32),
    pub cell: (usize, usize),
    pub flags: u16,
    pub reason: &'static str,
}

pub struct Level0Collision;

impl Level0Collision {
    /// Player/host movement: collision against the loaded `world.chunks` only. An
    /// unloaded chunk blocks (the host streams its own radius). The historical player
    /// path, expressed via the `LayoutSource::World` seam.
    ///
    /// ADR-026 (enmienda 2026-07-06, parte 3): the client's claimed Y (`desired.y`) rides
    /// along and, when physically plausible (see `MAX_CLAIMED_Y_STEP`), becomes the result Y
    /// instead of the historical `floor_player_y` pin. XZ gating is untouched: blocking is
    /// still tested at `from.y` (same layer/cell resolution as always).
    pub fn resolve_move(world: &World, from: Vec3, desired: Vec3) -> CollisionResolve {
        resolve_move_src(&LayoutSource::World(world), from, desired, Some(desired.y))
    }

    /// ADR-017: movement for a server-side entity with NO screen (the robapieles,
    /// ADR-016). Collision resolves against `world.chunks` first and, on a miss, a
    /// host-only sim-only `SimChunkCache` that generates the chunk layout on-demand
    /// from the deterministic generator. The generated layout is IDENTICAL to the
    /// real chunk's, so this never diverges from the player path; the sim layouts
    /// are NEVER inserted into `world.chunks` nor sent to render.
    pub fn resolve_move_simulated(
        world: &World,
        sim: &mut SimChunkCache,
        from: Vec3,
        desired: Vec3,
    ) -> CollisionResolve {
        // Pre-warm the chunks this move can read so the resolve below is a pure
        // read over (world.chunks ∪ sim cache) — keeps the cache borrow immutable.
        sim.prewarm_for_move(world, from, desired);
        // ADR-026 parte 3 does NOT apply here: a server-driven entity (the phantom,
        // ADR-016) has no client-reported Y — it depends on the floor pin for its
        // grounding (ADR-016 slice 2), so the entity path keeps the historical pin.
        resolve_move_src(&LayoutSource::WorldThenSim(world, sim), from, desired, None)
    }

    pub fn is_blocked_at(world: &World, pos: Vec3, radius: f32) -> bool {
        is_blocked_at(world, pos, radius)
    }

    pub fn floor_player_y(world: &World, pos: Vec3) -> f32 {
        floor_player_y(world, pos)
    }
}

/// ADR-017 — where collision reads chunk layouts from. `World` is the player/host
/// path (loaded chunks only; an absent chunk blocks). `WorldThenSim` is the
/// entity path: loaded chunks first, else the host-only on-demand sim cache. Both
/// are READ-ONLY at query time (the sim cache is pre-warmed before the query), so
/// the lookup never needs `&mut` and the player path is byte-for-byte unchanged.
enum LayoutSource<'a> {
    World(&'a World),
    WorldThenSim(&'a World, &'a SimChunkCache),
}

impl<'a> LayoutSource<'a> {
    #[inline]
    fn layout(&self, key: LayeredChunkPos) -> Option<&ChunkLayoutV1> {
        match self {
            LayoutSource::World(world) => world.chunks.get(&key).map(|c| &c.layout),
            LayoutSource::WorldThenSim(world, sim) => world
                .chunks
                .get(&key)
                .map(|c| &c.layout)
                .or_else(|| sim.layouts.get(&key)),
        }
    }
}

/// ADR-017 — host-only, sim-only collision cache. Holds `ChunkLayoutV1`s generated
/// on-demand by the deterministic world generator for chunks NOT loaded in
/// `world.chunks` (i.e. far from the host player). Consumed only by
/// `Level0Collision::resolve_move_simulated` for screen-less server entities; it is
/// NEVER inserted into `world.chunks` and NEVER serialized to Unity render
/// (`build_chunk_views` reads `world.chunks` only). Bounded by `SIM_CACHE_MAX_CHUNKS`
/// with farthest-from-query eviction so a roaming entity can't grow it without limit.
///
/// No consumer in production until ADR-016 (the robapieles); exercised by tests.
pub struct SimChunkCache {
    world_seed: u64,
    layouts: std::collections::HashMap<LayeredChunkPos, ChunkLayoutV1>,
}

/// Max live sim-only chunk layouts. Calibrable: a few per-move 3×3 spans of
/// headroom; a roaming entity evicts the farthest beyond this. ~420 B/layout, so
/// 64 ≈ 27 KB — trivial, and entirely separate from world.chunks/render memory.
pub const SIM_CACHE_MAX_CHUNKS: usize = 64;

impl SimChunkCache {
    pub fn new(world_seed: u64) -> Self {
        Self {
            world_seed,
            layouts: std::collections::HashMap::new(),
        }
    }

    /// Live cached layouts (diagnostics / tests).
    pub fn len(&self) -> usize {
        self.layouts.len()
    }

    pub fn is_empty(&self) -> bool {
        self.layouts.is_empty()
    }

    /// Make a layout available for `key` WITHOUT touching `world.chunks`: if the
    /// chunk is already loaded for the host, or already cached, do nothing; else
    /// generate it via the SAME deterministic path the real world uses
    /// (`generate_chunk_layer`, same seed+pos+layer) and cache its layout. Identical
    /// seed → layout identical to the real chunk (zero divergence). Side-effect-free:
    /// content spawn uses stable ids, not the global entity-id counter.
    fn ensure(&mut self, world: &World, key: LayeredChunkPos) {
        if world.chunks.contains_key(&key) || self.layouts.contains_key(&key) {
            return;
        }
        let pos = (key.0, key.2);
        let layout =
            crate::world::generator::generate_chunk_layer(self.world_seed, pos, key.1).layout;
        self.layouts.insert(key, layout);
    }

    /// Pre-generate every chunk the upcoming move can read (the 3×3 chunk span
    /// around both endpoints, at the move's layer), then enforce the cap. After
    /// this the resolve is a pure read over `world.chunks ∪ cache`.
    fn prewarm_for_move(&mut self, world: &World, from: Vec3, desired: Vec3) {
        // resolve_move pins desired.y to from.y, so one layer covers the whole move.
        let layer = layer_from_player_y(from.y);
        for end in [from, desired] {
            let c = world_to_chunk(end);
            for dz in -1..=1 {
                for dx in -1..=1 {
                    self.ensure(world, layered_chunk_pos((c.0 + dx, c.1 + dz), layer));
                }
            }
        }
        self.enforce_cap(world_to_chunk(from));
    }

    /// Keep the cache within `SIM_CACHE_MAX_CHUNKS` by evicting the layouts whose
    /// chunk is farthest (Chebyshev) from `center`. The current move's working set
    /// sits closest to `center`, so it is never evicted (cap ≫ per-move span).
    fn enforce_cap(&mut self, center: ChunkPos) {
        while self.layouts.len() > SIM_CACHE_MAX_CHUNKS {
            let Some(farthest) = self
                .layouts
                .keys()
                .max_by_key(|k| (k.0 - center.0).abs().max((k.2 - center.1).abs()))
                .copied()
            else {
                break;
            };
            self.layouts.remove(&farthest);
        }
    }
}

/// Core movement resolution, generic over where layouts are read (`LayoutSource`).
/// The player wrapper passes `World`; the entity wrapper passes `WorldThenSim`.
/// `claimed_y`: ADR-026 parte 3 — `Some(y)` on the player path (the client-reported Y,
/// accepted by `resolve_with_y` when plausible), `None` on the entity path (historical
/// floor pin). XZ gating below always tests at `from.y` regardless — the claimed Y NEVER
/// affects which layer/cells block (the TP-attribution watchpath stays bit-identical).
fn resolve_move_src(
    src: &LayoutSource,
    from: Vec3,
    desired: Vec3,
    claimed_y: Option<f32>,
) -> CollisionResolve {
    let desired = Vec3::new(desired.x, from.y, desired.z);
    if !is_blocked_at_src(src, desired, PLAYER_RADIUS) {
        return resolve_with_y(src, desired, CollisionResultKind::Free, "free", claimed_y);
    }

    let x_only = Vec3::new(desired.x, from.y, from.z);
    if !is_blocked_at_src(src, x_only, PLAYER_RADIUS) {
        return resolve_with_y(
            src,
            x_only,
            CollisionResultKind::SlidX,
            "z_blocked",
            claimed_y,
        );
    }

    let z_only = Vec3::new(from.x, from.y, desired.z);
    if !is_blocked_at_src(src, z_only, PLAYER_RADIUS) {
        return resolve_with_y(
            src,
            z_only,
            CollisionResultKind::SlidZ,
            "x_blocked",
            claimed_y,
        );
    }

    // Fully blocked — stay put, but report what actually blocked the
    // desired move so the trace logs name the real obstruction.
    let (chunk_pos, cell, flags, reason) = describe_block(src, desired, PLAYER_RADIUS);
    let mut position = from;
    if claimed_y.is_none() {
        // Entity path: historical floor pin.
        position.y = floor_player_y_src(src, position);
    }
    // Player path (claimed_y present): keep from.y — ADR-026 parte 3's literal contract
    // ("se mantiene from.y solo si el movimiento queda totalmente bloqueado").
    CollisionResolve {
        position,
        kind: CollisionResultKind::Blocked,
        chunk_pos,
        cell,
        flags,
        reason,
    }
}

// ─── Spawn safety (Phase 2.6) ───

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SpawnMethod {
    /// The preferred cell itself was safe.
    Preferred,
    /// A nearby safe cell was found by ring search.
    RingSearch,
    /// No safe cell existed; the starter cluster spawn area was repaired.
    Repaired,
}

impl SpawnMethod {
    pub fn as_str(self) -> &'static str {
        match self {
            SpawnMethod::Preferred => "preferred",
            SpawnMethod::RingSearch => "ring_search",
            SpawnMethod::Repaired => "repaired",
        }
    }
}

#[derive(Debug, Clone, Copy)]
pub struct SpawnResolution {
    pub position: Vec3,
    pub chunk: (i32, i32),
    pub cell: (usize, usize),
    pub method: SpawnMethod,
}

/// Resolve a guaranteed-safe spawn position near `preferred`.
///
/// The player must never spawn inside a wall, pillar, low/half wall, partition,
/// false door, pit, hazard, ramp, anomaly, blocked cell, or on a vertical floor
/// profile. This inspects the backend-authored layout, verifies capsule
/// clearance, and falls back to repairing the starter cluster if nothing is
/// safe. The returned position is the centre of a cell (well away from chunk
/// boundaries) at the correct standing height.
pub fn resolve_safe_spawn(world: &mut World, preferred: Vec3) -> SpawnResolution {
    info!(
        "MPTRACE step=V26 event=spawn_resolve_start preferred=({:.2},{:.2},{:.2})",
        preferred.x, preferred.y, preferred.z
    );

    if let Some(res) = find_safe_spawn(&*world, preferred) {
        log_spawn_resolved(&res);
        return res;
    }

    // No safe cell anywhere near the preferred point — repair the starter
    // cluster so the player always has somewhere clear to stand.
    let res = repair_starter_spawn(world);
    log_spawn_resolved(&res);
    res
}

/// ADR-031 "trust the bed": accept a player's bed position as their spawn even if it sits on a
/// non-flat / non-"safe" chunk that `resolve_safe_spawn` would reject and dump at the starter cluster.
/// The player stood on this cell to place the bed, so its XZ is walkable — we only verify a player
/// capsule still fits there (the SAME basic clearance check `find_safe_spawn` applies), recompute the
/// standing Y from the loaded floor, and use it. Returns None if the exact spot is blocked (caller
/// keeps the starter fallback so a respawn always resolves). The bed's chunk MUST already be loaded
/// (caller streams it via `update_ownership` first).
pub fn try_bed_spawn(world: &World, bed: Vec3) -> Option<SpawnResolution> {
    let y = floor_player_y(world, bed);
    let standing = Vec3::new(bed.x, y, bed.z);
    if is_blocked_at(world, standing, PLAYER_RADIUS + SPAWN_CLEARANCE_MARGIN) {
        return None;
    }
    let chunk = world_to_chunk(bed);
    Some(SpawnResolution {
        position: standing,
        chunk,
        cell: preferred_cell(bed, chunk),
        method: SpawnMethod::Preferred, // trusted: exact bed position accepted
    })
}

fn log_spawn_resolved(res: &SpawnResolution) {
    info!(
        "MPTRACE step=V26 event=spawn_resolved pos=({:.2},{:.2},{:.2}) chunk=({},{}) cell=({},{}) method={}",
        res.position.x,
        res.position.y,
        res.position.z,
        res.chunk.0,
        res.chunk.1,
        res.cell.0,
        res.cell.1,
        res.method.as_str()
    );
}

fn layer_from_player_y(y: f32) -> ChunkLayer {
    ((y - PLAYER_BASE_Y) / crate::world::chunk::LAYER_HEIGHT)
        .round()
        .clamp(-8.0, 8.0) as ChunkLayer
}

fn chunk_key_at(pos: Vec3) -> LayeredChunkPos {
    let xz = world_to_chunk(pos);
    layered_chunk_pos(xz, layer_from_player_y(pos.y))
}

/// Immutable search for the nearest safe spawn cell within the search radius.
fn find_safe_spawn(world: &World, preferred: Vec3) -> Option<SpawnResolution> {
    let pref_chunk = world_to_chunk(preferred);
    let pref_cell = preferred_cell(preferred, pref_chunk);
    let mut best: Option<(f32, SpawnResolution)> = None;
    let mut rejected = 0u32;

    for dz in -SPAWN_SEARCH_CHUNK_RADIUS..=SPAWN_SEARCH_CHUNK_RADIUS {
        for dx in -SPAWN_SEARCH_CHUNK_RADIUS..=SPAWN_SEARCH_CHUNK_RADIUS {
            let chunk_pos = (pref_chunk.0 + dx, pref_chunk.1 + dz);
            let Some(chunk) = world.chunks.get(&layered_chunk_pos(chunk_pos, 0)) else {
                continue;
            };
            // Spawn only on flat ground — verticality must never sit under spawn.
            if !is_flat_spawn_chunk(&chunk.layout) {
                continue;
            }

            let grid = chunk.layout.grid_size as usize;
            for z in 0..grid {
                for x in 0..grid {
                    if !is_safe_spawn_cell(&chunk.layout, x, z) {
                        continue;
                    }

                    let center = cell_center_world(chunk_pos, x, z, chunk.layout.cell_size);
                    if is_blocked_at(world, center, PLAYER_RADIUS + SPAWN_CLEARANCE_MARGIN) {
                        if rejected < SPAWN_REJECT_LOG_CAP {
                            info!(
                                "MPTRACE step=V26 event=spawn_candidate_rejected chunk=({},{}) cell=({},{}) flags={} reason=capsule_clearance",
                                chunk_pos.0,
                                chunk_pos.1,
                                x,
                                z,
                                chunk.layout.cell_flags(x, z)
                            );
                        }
                        rejected = rejected.saturating_add(1);
                        continue;
                    }

                    let d = center.distance_xz(preferred);
                    let method = if chunk_pos == pref_chunk && (x, z) == pref_cell {
                        SpawnMethod::Preferred
                    } else {
                        SpawnMethod::RingSearch
                    };
                    let y = floor_player_y(world, center);
                    let res = SpawnResolution {
                        position: Vec3::new(center.x, y, center.z),
                        chunk: chunk_pos,
                        cell: (x, z),
                        method,
                    };
                    if best.as_ref().map(|(bd, _)| d < *bd).unwrap_or(true) {
                        best = Some((d, res));
                    }
                }
            }
        }
    }

    best.map(|(_, res)| res)
}

/// Carve a clean 3x3 walkable area in the centre of the starter chunk (0,0)
/// and return its centre. Last-resort guarantee that a spawn always exists.
fn repair_starter_spawn(world: &mut World) -> SpawnResolution {
    let pos = (0, 0);
    let chunk = world.ensure_chunk(pos);
    chunk.layout.floor_profile = FLOOR_FLAT;
    chunk.layout.floor_level = 0;
    chunk.layout.vertical_flags = 0;

    let grid = chunk.layout.grid_size as usize;
    let cx = grid / 2;
    let cz = grid / 2;
    let lo_x = cx.saturating_sub(1);
    let lo_z = cz.saturating_sub(1);
    for x in lo_x..=cx + 1 {
        for z in lo_z..=cz + 1 {
            if let Some(idx) = chunk.layout.cell_index(x, z) {
                chunk.layout.cells[idx] = CELL_WALKABLE | CELL_SAFE;
            }
        }
    }
    // Open every interior edge inside the core so no wall crosses the spawn area.
    for x in lo_x..=cx + 1 {
        for z in lo_z..=cz + 1 {
            if x > lo_x {
                chunk.layout.set_edge_v(x, z, EDGE_KIND_OPEN);
            }
            if z > lo_z {
                chunk.layout.set_edge_h(x, z, EDGE_KIND_OPEN);
            }
        }
    }

    let cell_size = chunk.layout.cell_size;
    let floor_level = chunk.layout.floor_level;
    let center = cell_center_world(pos, cx, cz, cell_size);
    let y = PLAYER_BASE_Y + floor_level as f32 * FLOOR_LEVEL_HEIGHT;
    SpawnResolution {
        position: Vec3::new(center.x, y, center.z),
        chunk: pos,
        cell: (cx, cz),
        method: SpawnMethod::Repaired,
    }
}

/// A spawn-safe *cell*: walkable floor, free of centre blockers, hazards,
/// verticality and anomalies. Edge clearance (no wall/partition/low-wall
/// crossing the capsule) is verified separately via `is_blocked_at`.
fn is_safe_spawn_cell(layout: &ChunkLayoutV1, x: usize, z: usize) -> bool {
    let flags = layout.cell_flags(x, z);
    flags & CELL_WALKABLE != 0
        && flags
            & (CELL_WALL
                | CELL_BLOCKED
                | CELL_PILLAR
                | CELL_PIT
                | CELL_HAZARD
                | CELL_RAMP
                | CELL_ANOMALY)
            == 0
}

fn is_flat_spawn_chunk(layout: &ChunkLayoutV1) -> bool {
    layout.floor_profile == FLOOR_FLAT && layout.vertical_flags == 0
}

fn cell_center_world(chunk_pos: (i32, i32), x: usize, z: usize, cell_size: f32) -> Vec3 {
    Vec3::new(
        chunk_pos.0 as f32 * CHUNK_SIZE + (x as f32 + 0.5) * cell_size,
        0.0,
        chunk_pos.1 as f32 * CHUNK_SIZE + (z as f32 + 0.5) * cell_size,
    )
}

fn preferred_cell(pos: Vec3, chunk_pos: (i32, i32)) -> (usize, usize) {
    let grid = crate::world::chunk::LAYOUT_GRID_SIZE as usize;
    let cell_size = crate::world::chunk::LAYOUT_CELL_SIZE.max(0.01);
    let local_x = (pos.x - chunk_pos.0 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE - 0.001);
    let local_z = (pos.z - chunk_pos.1 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE - 0.001);
    (
        ((local_x / cell_size).floor() as usize).min(grid - 1),
        ((local_z / cell_size).floor() as usize).min(grid - 1),
    )
}

// ─── Movement collision ───

pub fn is_blocked_at(world: &World, pos: Vec3, radius: f32) -> bool {
    is_blocked_at_src(&LayoutSource::World(world), pos, radius)
}

fn is_blocked_at_src(src: &LayoutSource, pos: Vec3, radius: f32) -> bool {
    let samples = capsule_samples(pos, radius);
    // 1. Cell-centre blockers (pillars, pits, storage) + unloaded chunks.
    for &(x, z) in &samples {
        if sample_cell_blocks(src, pos.y, x, z) {
            return true;
        }
    }
    // 2. Edge walls within the capsule radius. Check the chunk of `pos` and any
    //    chunk a sample lands in, so boundary walls are covered.
    let mut seen: [LayeredChunkPos; 9] = [(i32::MIN, i8::MIN, i32::MIN); 9];
    let mut n = 0usize;
    for &(x, z) in &samples {
        let xz = world_to_chunk(Vec3::new(x, 0.0, z));
        let cp = layered_chunk_pos(xz, layer_from_player_y(pos.y));
        if seen[..n].contains(&cp) {
            continue;
        }
        seen[n] = cp;
        n += 1;
        if let Some(layout) = src.layout(cp) {
            if layout.has_edges()
                && first_blocking_edge(layout, (cp.0, cp.2), pos, radius).is_some()
            {
                return true;
            }
        }
    }
    false
}

fn capsule_samples(pos: Vec3, radius: f32) -> [(f32, f32); 9] {
    [
        (pos.x, pos.z),
        (pos.x - radius, pos.z),
        (pos.x + radius, pos.z),
        (pos.x, pos.z - radius),
        (pos.x, pos.z + radius),
        (pos.x - radius, pos.z - radius),
        (pos.x + radius, pos.z - radius),
        (pos.x - radius, pos.z + radius),
        (pos.x + radius, pos.z + radius),
    ]
}

/// A cell that blocks regardless of edges: a centre prop (pillar/pit), explicit
/// storage block, an unloaded chunk, or a legacy non-walkable wall cell.
fn sample_cell_blocks(src: &LayoutSource, y: f32, x: f32, z: f32) -> bool {
    let chunk_pos = world_to_chunk(Vec3::new(x, 0.0, z));
    let key = layered_chunk_pos(chunk_pos, layer_from_player_y(y));
    let Some(layout) = src.layout(key) else {
        return true;
    };
    let (cell_x, cell_z) = cell_for_pos(layout, chunk_pos, x, z);
    let flags = layout.cell_flags(cell_x, cell_z);
    flags & (CELL_BLOCKED | CELL_WALL | CELL_PILLAR | CELL_PIT) != 0 || flags & CELL_WALKABLE == 0
}

/// Distance from `pos` to the nearest blocking edge of `layout`; returns the
/// edge kind if one is within `radius`.
fn first_blocking_edge(
    layout: &ChunkLayoutV1,
    chunk_pos: (i32, i32),
    pos: Vec3,
    radius: f32,
) -> Option<u8> {
    let g = layout.grid_size as usize;
    let cs = layout.cell_size.max(0.01);
    let ox = chunk_pos.0 as f32 * CHUNK_SIZE;
    let oz = chunk_pos.1 as f32 * CHUNK_SIZE;

    for z in 0..g {
        for bx in 0..=g {
            let kind = layout.edge_v(bx, z);
            if !edge_blocks_movement(kind) {
                continue;
            }
            let ex = ox + bx as f32 * cs;
            let z0 = oz + z as f32 * cs;
            let z1 = z0 + cs;
            let nearest_z = pos.z.clamp(z0, z1);
            let dx = pos.x - ex;
            let dz = pos.z - nearest_z;
            if dx * dx + dz * dz < radius * radius {
                return Some(kind);
            }
        }
    }
    for bz in 0..=g {
        for x in 0..g {
            let kind = layout.edge_h(x, bz);
            if !edge_blocks_movement(kind) {
                continue;
            }
            let ez = oz + bz as f32 * cs;
            let x0 = ox + x as f32 * cs;
            let x1 = x0 + cs;
            let nearest_x = pos.x.clamp(x0, x1);
            let dx = pos.x - nearest_x;
            let dz = pos.z - ez;
            if dx * dx + dz * dz < radius * radius {
                return Some(kind);
            }
        }
    }
    None
}

/// Describe what blocks a desired move (for trace logs): a cell blocker if any,
/// otherwise the nearest blocking edge.
fn describe_block(
    src: &LayoutSource,
    pos: Vec3,
    radius: f32,
) -> ((i32, i32), (usize, usize), u16, &'static str) {
    for (x, z) in capsule_samples(pos, radius) {
        let chunk_pos = world_to_chunk(Vec3::new(x, 0.0, z));
        let key = layered_chunk_pos(chunk_pos, layer_from_player_y(pos.y));
        let Some(layout) = src.layout(key) else {
            return (chunk_pos, (0, 0), 0, "unloaded_chunk");
        };
        let cell = cell_for_pos(layout, chunk_pos, x, z);
        let flags = layout.cell_flags(cell.0, cell.1);
        if flags & (CELL_BLOCKED | CELL_WALL | CELL_PILLAR | CELL_PIT) != 0
            || flags & CELL_WALKABLE == 0
        {
            return (chunk_pos, cell, flags, cell_block_reason(flags));
        }
    }
    let chunk_pos = world_to_chunk(pos);
    let cell = src
        .layout(chunk_key_at(pos))
        .map(|layout| cell_for_pos(layout, chunk_pos, pos.x, pos.z))
        .unwrap_or((0, 0));
    if let Some(layout) = src.layout(chunk_key_at(pos)) {
        if let Some(kind) = first_blocking_edge(layout, chunk_pos, pos, radius) {
            return (chunk_pos, cell, kind as u16, edge_block_reason(kind));
        }
    }
    (chunk_pos, cell, 0, "blocked")
}

fn cell_block_reason(flags: u16) -> &'static str {
    if flags & CELL_WALL != 0 {
        "wall"
    } else if flags & CELL_PILLAR != 0 {
        "pillar"
    } else if flags & CELL_PIT != 0 {
        "pit"
    } else if flags & CELL_BLOCKED != 0 {
        "blocked"
    } else if flags & CELL_WALKABLE == 0 {
        "not_walkable"
    } else {
        "clear"
    }
}

fn edge_block_reason(kind: u8) -> &'static str {
    match kind {
        EDGE_KIND_PARTITION => "partition",
        EDGE_KIND_FALSE_DOOR => "false_door",
        EDGE_KIND_LOW_WALL => "low_wall",
        EDGE_KIND_HALF_WALL => "half_wall",
        _ => "wall",
    }
}

/// `claimed_y`: ADR-026 parte 3 — when present (player path) and physically plausible
/// (finite, |Δ| from the incoming `position.y` = the player's previous Y ≤
/// `MAX_CLAIMED_Y_STEP`), the result Y is the CLIENT's claimed Y (real jumps/falls reach the
/// pose relay). Implausible or absent → historical `floor_player_y` pin (safe fallback; the
/// XZ move itself is never rejected over Y). `chunk_pos`/`cell`/`flags` are always computed
/// at the floor-pinned height, exactly as before — bit-identical diagnostics either way.
fn resolve_with_y(
    src: &LayoutSource,
    mut position: Vec3,
    kind: CollisionResultKind,
    reason: &'static str,
    claimed_y: Option<f32>,
) -> CollisionResolve {
    let from_y = position.y;
    position.y = floor_player_y_src(src, position);
    let chunk_pos = world_to_chunk(position);
    let (cell, flags) = src
        .layout(chunk_key_at(position))
        .map(|layout| {
            let cell = cell_for_pos(layout, chunk_pos, position.x, position.z);
            (cell, layout.cell_flags(cell.0, cell.1))
        })
        .unwrap_or(((0, 0), 0));
    if let Some(claimed) = claimed_y {
        if claimed.is_finite() && (claimed - from_y).abs() <= MAX_CLAIMED_Y_STEP {
            position.y = claimed;
        }
    }
    CollisionResolve {
        position,
        kind,
        chunk_pos,
        cell,
        flags,
        reason,
    }
}

fn floor_player_y(world: &World, pos: Vec3) -> f32 {
    floor_player_y_src(&LayoutSource::World(world), pos)
}

fn floor_player_y_src(src: &LayoutSource, pos: Vec3) -> f32 {
    let chunk_pos = world_to_chunk(pos);
    let primary = chunk_key_at(pos);
    // The layout carries no layer field; the key does. A real chunk's `layer`
    // always equals its key layer, so deriving the layer from the resolved key
    // reproduces the previous `chunk.layer` exactly (primary key, else layer 0).
    let (layout, layer) = if let Some(layout) = src.layout(primary) {
        (layout, primary.1)
    } else {
        let zero = layered_chunk_pos(chunk_pos, 0);
        match src.layout(zero) {
            Some(layout) => (layout, 0),
            None => return pos.y.max(PLAYER_BASE_Y - crate::world::chunk::LAYER_HEIGHT),
        }
    };

    let base = PLAYER_BASE_Y + layer_y(layer) + layout.floor_level as f32 * FLOOR_LEVEL_HEIGHT;
    match layout.floor_profile {
        crate::world::chunk::FLOOR_SUNKEN => base - 0.25,
        crate::world::chunk::FLOOR_RAISED => base + 0.35,
        FLOOR_CONNECTOR_UP => {
            let local_z = (pos.z - chunk_pos.1 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE);
            base + (local_z / CHUNK_SIZE) * crate::world::chunk::LAYER_HEIGHT
        }
        FLOOR_CONNECTOR_DOWN => {
            let local_z = (pos.z - chunk_pos.1 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE);
            base - (local_z / CHUNK_SIZE) * crate::world::chunk::LAYER_HEIGHT
        }
        crate::world::chunk::FLOOR_RAMP_NORTH_SOUTH => {
            let local_z = (pos.z - chunk_pos.1 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE);
            base + (local_z / CHUNK_SIZE) * 0.5
        }
        crate::world::chunk::FLOOR_RAMP_EAST_WEST => {
            let local_x = (pos.x - chunk_pos.0 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE);
            base + (local_x / CHUNK_SIZE) * 0.5
        }
        crate::world::chunk::FLOOR_STAIRS_NORTH_SOUTH => {
            let local_z = (pos.z - chunk_pos.1 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE);
            base + ((local_z / 10.0).floor() / 5.0).clamp(0.0, 1.0) * 0.6
        }
        crate::world::chunk::FLOOR_STAIRS_EAST_WEST => {
            let local_x = (pos.x - chunk_pos.0 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE);
            base + ((local_x / 10.0).floor() / 5.0).clamp(0.0, 1.0) * 0.6
        }
        _ => base,
    }
}

fn cell_for_pos(
    layout: &ChunkLayoutV1,
    chunk_pos: (i32, i32),
    world_x: f32,
    world_z: f32,
) -> (usize, usize) {
    let grid = layout.grid_size.max(1) as usize;
    let cell_size = layout.cell_size.max(0.01);
    let local_x = (world_x - chunk_pos.0 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE - 0.001);
    let local_z = (world_z - chunk_pos.1 as f32 * CHUNK_SIZE).clamp(0.0, CHUNK_SIZE - 0.001);
    let cell_x = ((local_x / cell_size).floor() as usize).min(grid - 1);
    let cell_z = ((local_z / cell_size).floor() as usize).min(grid - 1);
    (cell_x, cell_z)
}

#[cfg(test)]
mod tests;
