//! World domain: chunks, procedural generation, entities.

pub mod architecture;
pub mod chunk;
pub mod collision;
pub mod corpse;
pub mod entity;
pub mod generator;
pub mod graph;
pub mod grid_gen;
pub mod levels;
/// ADR-043 — deterministic, lazy draw of where the world's robapieles live.
pub mod phantom_spawn;
pub mod volumetric_grid;
/// ADR-033: `zone_kind` → `LayerRules` para las rutas grid_gen de render y
/// robapieles. Vive aquí (no en `grid_gen`) porque lee `world/`.
pub mod zone_density;

use std::collections::{HashMap, HashSet};
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};

use log::info;
use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};
use serde::{Deserialize, Serialize};

use crate::ipc::{ChunkView, EntityView, GameEvent, ItemView};
use crate::network::PeerId;
use crate::player::stats::StatContext;
use crate::utils::{chunks_in_radius, world_to_chunk, ChunkPos, Vec3};
use chunk::{layer_y, layered_chunk_pos, Chunk, ChunkLayer, ChunkState, LayeredChunkPos};

/// Monotonic id source for player-dropped items. Starts high to keep dropped-item ids
/// out of the range used by procedurally-generated items (stable hashes of seed+pos+index),
/// so a fresh drop never reuses a generated item's id within a session.
static NEXT_DROPPED_ID: AtomicU32 = AtomicU32::new(0xF000_0000);

fn next_dropped_item_id() -> u32 {
    NEXT_DROPPED_ID.fetch_add(1, Ordering::Relaxed)
}
use entity::EntityEvent;
use graph::verticality::{export_vertical_debug_markers, VerticalDebugMarkerV0};
use graph::world_graph::WorldGraph;
use levels::level_0::region_graph_builder::{
    audit_level0_region_graph, build_level0_region_graph_from_generated, reachable_from,
    starter_node_id,
};

static V30A2_VISFIX_IPC_LAST_REVISION: AtomicU64 = AtomicU64::new(0);
static UNIFIED_VOLUMETRIC_IPC_LAST_REVISION: AtomicU64 = AtomicU64::new(0);

/// Rollout flag for the unified volumetric world: `true` attaches a column to EVERY
/// layer-0 chunk. OFF (2026-07-02) — that violated the IPC contract (ipc/mod.rs
/// `ChunkView.volumetric_grid`: "Present only on the near-spawn showcase host chunk;
/// omitted otherwise") and bloated every 10 Hz WorldState to ~1.3 MB, saturating the
/// IPC pipe whenever Unity's main thread stalled (worst at respawn) → the 256-slot
/// broadcast buffer overflowed ("IPC write loop lagged") and dropped one-shot events
/// (player_died / player_respawned). Unity renders non-column chunks via the legacy
/// flat renderer fallback. This is the ONLY constant with this name — a duplicate
/// (dead) associated const inside `impl World` used to shadow-confuse edits; removed.
const ENABLE_LEVEL0_VOLUMETRIC_COLUMNS: bool = false;

/// Tunable world parameters (ARCHITECTURE_V1.md §11.1).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldConfig {
    pub max_players: u16,
    pub teleport_interval: (f32, f32),
    pub entity_scaling: f32,
    pub chunk_size: f32,
    pub ownership_radius: i32,
    /// Chunks are unloaded only beyond this radius (>= ownership_radius).
    /// The gap between load and unload radius is hysteresis: crossing a chunk
    /// boundary back and forth no longer destroys and regenerates a whole row.
    #[serde(default = "default_unload_radius")]
    pub unload_radius: i32,
}

fn default_unload_radius() -> i32 {
    4
}

impl Default for WorldConfig {
    fn default() -> Self {
        Self {
            max_players: 50,
            teleport_interval: (120.0, 600.0),
            entity_scaling: 1.0,
            chunk_size: crate::utils::CHUNK_SIZE,
            ownership_radius: 3,
            unload_radius: default_unload_radius(),
        }
    }
}

/// Result of a world tick — events to emit + damage to apply to the player.
pub struct WorldTickResult {
    pub events: Vec<GameEvent>,
    pub player_damage: f32,
    pub stat_context: StatContext,
}

/// One chunk displacement, with everything the caller needs to both notify the local
/// client and replicate the displacement to peers.
///
/// The seed is returned as a field instead of being read back out of `event.data`
/// on purpose: `event` is the IPC payload and only carries what the client needs
/// (`chunk_pos` + `new_offset`). Reconstructing the P2P broadcast from that JSON is
/// exactly how `new_seed` used to be lost — the broadcast went out with a hardcoded 0,
/// so every peer regenerated the displaced chunk from seed 0 while the owner used the
/// real one. See `remote_teleport_with_owner_seed_converges`.
pub struct TeleportOutcome {
    pub event: GameEvent,
    pub old_pos: [i32; 2],
    pub new_pos: [i32; 2],
    pub new_seed: u64,
}

fn chunk_has_layer_connector(chunk: &Chunk) -> bool {
    chunk.layout.vertical_flags & chunk::V30A_CONNECTOR != 0
        || matches!(
            chunk.layout.floor_profile,
            chunk::FLOOR_CONNECTOR_UP | chunk::FLOOR_CONNECTOR_DOWN
        )
}

fn chunk_is_v30a(chunk: &Chunk) -> bool {
    chunk.layer != 0
        || chunk.layout.vertical_flags
            & (chunk::V30A_CONNECTOR
                | chunk::V30A_STACKED_CORRIDOR
                | chunk::V30A_LOWER_SERVICE_BRANCH
                | chunk::V30A_UPPER_OFFICE_BRANCH
                | chunk::V30A_ATRIUM_VOID_ROOM
                | chunk::V30A_DEEP_PRECIPICE_PLACEHOLDER
                | chunk::V30A_GIANT_PILLAR_HALL
                | chunk::V30A_BLOCKED_VERTICAL_SHAFT)
            != 0
}

fn player_layer_from_y(y: f32) -> ChunkLayer {
    ((y - 1.8) / chunk::LAYER_HEIGHT).round().clamp(-8.0, 8.0) as ChunkLayer
}

fn chunk_key_for_player_pos(pos: Vec3) -> LayeredChunkPos {
    let xz = world_to_chunk(pos);
    layered_chunk_pos(xz, player_layer_from_y(pos.y))
}

fn layered_graph_neighbors(
    pos: LayeredChunkPos,
    chunks: &HashMap<LayeredChunkPos, Chunk>,
) -> Vec<LayeredChunkPos> {
    let mut out = Vec::with_capacity(6);
    for next in [
        (pos.0 + 1, pos.1, pos.2),
        (pos.0 - 1, pos.1, pos.2),
        (pos.0, pos.1, pos.2 + 1),
        (pos.0, pos.1, pos.2 - 1),
    ] {
        if chunks.contains_key(&next) {
            out.push(next);
        }
    }

    if let Some(chunk) = chunks.get(&pos) {
        if chunk_has_layer_connector(chunk) {
            for layer in [pos.1 - 1, pos.1 + 1] {
                let vertical = (pos.0, layer, pos.2);
                if chunks.contains_key(&vertical) {
                    out.push(vertical);
                }
            }
        }
    }

    for layer in [pos.1 - 1, pos.1 + 1] {
        let vertical = (pos.0, layer, pos.2);
        if let Some(chunk) = chunks.get(&vertical) {
            if chunk_has_layer_connector(chunk) {
                out.push(vertical);
            }
        }
    }
    out
}

/// The active world state owned by this peer.
#[derive(Debug, Clone)]
pub struct World {
    pub seed: u64,
    pub revision: u64,
    pub config: WorldConfig,
    pub chunks: HashMap<LayeredChunkPos, Chunk>,
    pub rng: StdRng,
    pub respawn_queue: Vec<(u32, ChunkPos, f32)>,
    v30a_chunk_cache: Option<Vec<Chunk>>,
    view_cache: Option<(u64, usize, Vec<ChunkView>)>,
    pub world_graph: Option<WorldGraph>,
    /// ADR-028: lootable corpses. GLOBAL on purpose — never stored in chunks, so
    /// `update_ownership` unloads can't destroy them (persist until fully looted).
    pub corpses: HashMap<u32, corpse::CorpseData>,
    /// ADR-028: session-monotonic corpse id source (u32 keyed map, wraps — a session
    /// never approaches 2^32 deaths).
    next_corpse_id: u32,
}

impl World {
    pub fn new(seed: u64) -> Self {
        Self {
            seed,
            revision: 1,
            config: WorldConfig::default(),
            chunks: HashMap::new(),
            rng: StdRng::seed_from_u64(seed.wrapping_add(0xDEAD)),
            respawn_queue: Vec::new(),
            v30a_chunk_cache: None,
            view_cache: None,
            world_graph: None,
            corpses: HashMap::new(),
            next_corpse_id: 1,
        }
    }

    /// ADR-032: expose the corpse-id allocator so world persistence can save it and a loaded save
    /// can restore it — otherwise a fresh corpse/chest could reuse an id that a persisted one still
    /// holds. Read-only + explicit setter; `next_corpse_id` itself stays private.
    pub fn next_corpse_id(&self) -> u32 {
        self.next_corpse_id
    }

    pub fn set_next_corpse_id(&mut self, value: u32) {
        self.next_corpse_id = value;
    }

    /// Ensure a chunk exists at `pos`, generating it deterministically if needed.
    pub fn ensure_chunk(&mut self, pos: ChunkPos) -> &mut Chunk {
        self.ensure_chunk_layer(pos, 0)
    }

    pub fn ensure_chunk_layer(&mut self, pos: ChunkPos, layer: ChunkLayer) -> &mut Chunk {
        let seed = self.seed;
        self.chunks
            .entry(layered_chunk_pos(pos, layer))
            .or_insert_with(|| generator::generate_chunk_layer(seed, pos, layer))
    }

    pub fn reset_for_remote_world(&mut self, world_seed: u64, world_revision: u64) {
        self.seed = world_seed;
        self.revision = world_revision;
        self.chunks.clear();
        self.respawn_queue.clear();
        self.rng = StdRng::seed_from_u64(world_seed.wrapping_add(0xDEAD));
        self.v30a_chunk_cache = None;
        self.view_cache = None;
        self.world_graph = None; // NUEVO
                                 // ADR-028: adopting a remote world discards local corpses (none should exist
                                 // yet at join time; consistent with chunks.clear()).
        self.corpses.clear();
    }

    pub fn generate_initial_structures(&mut self, owner_id: PeerId) {
        let generated = generator::generate_initial_structure_chunks(self.seed);
        // Derive structure count from already-generated data; avoids a second Level0Builder run.
        let structure_count = {
            let mut ids: Vec<u32> = generated.iter().map(|(s, _)| s.id).collect();
            ids.sort_unstable();
            ids.dedup();
            ids.len()
        };
        // Build RegionGraph before the loop consumes `generated`; no second generation needed.

        let rg = build_level0_region_graph_from_generated(self.seed, &generated);
        info!(
            "MPTRACE step=GRAPH event=level0_vertical_debug_render_exported count={}",
            rg.virtual_vertical_node_count()
        );
        self.world_graph = Some(WorldGraph::from_level0_region_graph(self.seed, rg));

        let mut logged_structures = HashSet::new();
        let mut item_count = 0usize;
        let mut entity_count = 0usize;
        let mut v30a_cache: Vec<Chunk> = Vec::new();

        for (structure, mut chunk) in generated {
            if logged_structures.insert(structure.id) {
                info!(
                    "MPTRACE step=AM event=structure_created id={} type={} origin=({},{},{}) size=({},{}) chunks={} seed={} tags={:?}",
                    structure.id,
                    structure.structure_type.as_str(),
                    structure.origin.0,
                    structure.origin_layer,
                    structure.origin.1,
                    structure.size[0],
                    structure.size[1],
                    structure.chunks.len(),
                    structure.seed,
                    structure.tags
                );
            }

            chunk.owner = Some(owner_id);
            item_count += chunk.items.len();
            entity_count += chunk.entities.len();
            info!(
                "MPTRACE step=AN event=chunk_from_structure structure_id={} chunk_id={} coord=({},{},{}) template_id={} rotation={}",
                structure.id,
                chunk.seed,
                chunk.pos.0,
                chunk.layer,
                chunk.pos.1,
                chunk.template_id,
                chunk.rotation
            );
            for item in &chunk.items {
                info!(
                    "MPTRACE step=AO event=structure_item_spawned structure_id={} item_id={} type={} pos=({:.2},{:.2},{:.2})",
                    structure.id,
                    item.id,
                    item.item.type_name(),
                    item.position.x,
                    item.position.y,
                    item.position.z
                );
            }
            if chunk_is_v30a(&chunk) {
                v30a_cache.push(chunk.clone());
            }
            self.chunks.entry(chunk.key()).or_insert(chunk);
        }
        self.v30a_chunk_cache = Some(v30a_cache);
        self.view_cache = None;

        // Template distribution stats
        let mut template_counts = [0u32; 18];
        for c in self.chunks.values() {
            let idx = (c.template_id as usize).min(17);
            template_counts[idx] += 1;
        }
        let hallway_count = template_counts[1] + template_counts[2];
        let junction_count = template_counts[3] + template_counts[8];

        info!(
            "MPTRACE step=AL event=world_structure_generated seed={} structures={} chunks={} items={} entities={}",
            self.seed,
            structure_count,
            self.chunks.len(),
            item_count,
            entity_count
        );

        info!(
            "MPTRACE step=AT event=level0_layout_stats seed={} total_chunks={} hallways={} junctions={} storage={} danger={} safe={} pillar={}",
            self.seed,
            self.chunks.len(),
            hallway_count,
            junction_count,
            template_counts[4],
            template_counts[7],
            template_counts[5],
            template_counts[9]
        );

        // Connectivity check (BFS from origin)
        let positions: HashSet<LayeredChunkPos> = self.chunks.keys().copied().collect();
        let mut visited = HashSet::new();
        let mut queue = std::collections::VecDeque::from([(0i32, 0i8, 0i32)]);
        while let Some(pos) = queue.pop_front() {
            if !visited.insert(pos) {
                continue;
            }
            for next in layered_graph_neighbors(pos, &self.chunks) {
                if positions.contains(&next) && !visited.contains(&next) {
                    queue.push_back(next);
                }
            }
        }
        let connected = visited.len() == positions.len();
        info!(
            "MPTRACE step=AU event=level0_connectivity seed={} connected={} reachable={} total={}",
            self.seed,
            connected,
            visited.len(),
            positions.len()
        );

        info!(
            "MPTRACE step=AV event=level0_template_distribution seed={} straight={} corner={} t_junc={} intersection={} basic={} pillar={} storage={} safe={} dead_end={} danger={}",
            self.seed,
            template_counts[1],
            template_counts[2],
            template_counts[8],
            template_counts[3],
            template_counts[0],
            template_counts[9],
            template_counts[4],
            template_counts[5],
            template_counts[6],
            template_counts[7]
        );

        let mut walkable = 0usize;
        let mut wall = 0usize;
        let mut pillar = 0usize;
        let mut special = 0usize;
        let mut total_cells = 0usize;
        let mut total_openings = 0usize;
        let mut doorframes = 0usize;
        let mut arches = 0usize;
        let mut lowwalls = 0usize;
        let mut vertical_chunks = 0usize;
        let mut ramp_chunks = 0usize;
        let mut stair_chunks = 0usize;
        let mut raised = 0usize;
        let mut sunken = 0usize;

        for c in self.chunks.values() {
            total_openings += c.layout.edge_openings.count_ones() as usize;
            if c.layout.vertical_flags != 0 {
                vertical_chunks += 1;
            }
            match c.layout.floor_profile {
                chunk::FLOOR_RAMP_NORTH_SOUTH | chunk::FLOOR_RAMP_EAST_WEST => ramp_chunks += 1,
                chunk::FLOOR_STAIRS_NORTH_SOUTH | chunk::FLOOR_STAIRS_EAST_WEST => {
                    stair_chunks += 1
                }
                chunk::FLOOR_RAISED => raised += 1,
                chunk::FLOOR_SUNKEN => sunken += 1,
                _ => {}
            }
            for flags in &c.layout.cells {
                total_cells += 1;
                if flags & chunk::CELL_WALKABLE != 0 {
                    walkable += 1;
                }
                if flags & (chunk::CELL_WALL | chunk::CELL_BLOCKED) != 0 {
                    wall += 1;
                }
                if flags & chunk::CELL_PILLAR != 0 {
                    pillar += 1;
                }
                if flags
                    & (chunk::CELL_DOOR
                        | chunk::CELL_ARCH
                        | chunk::CELL_LOW_WALL
                        | chunk::CELL_HALF_WALL
                        | chunk::CELL_FALSE_DOOR
                        | chunk::CELL_PIT
                        | chunk::CELL_RAMP)
                    != 0
                {
                    special += 1;
                }
                if flags & chunk::CELL_DOOR != 0 {
                    doorframes += 1;
                }
                if flags & chunk::CELL_ARCH != 0 {
                    arches += 1;
                }
                if flags & (chunk::CELL_LOW_WALL | chunk::CELL_HALF_WALL) != 0 {
                    lowwalls += 1;
                }
            }
        }
        let denom = total_cells.max(1) as f32;
        info!(
            "MPTRACE step=DB event=level0_layout_density seed={} walkable_pct={:.2} wall_pct={:.2} pillar_pct={:.2} special_pct={:.2}",
            self.seed,
            walkable as f32 / denom,
            wall as f32 / denom,
            pillar as f32 / denom,
            special as f32 / denom
        );
        info!(
            "MPTRACE step=DC event=level0_vertical_stats seed={} vertical_chunks={} ramp_chunks={} stair_chunks={} raised={} sunken={}",
            self.seed, vertical_chunks, ramp_chunks, stair_chunks, raised, sunken
        );

        // Phase 2.10 — controlled verticality distribution audit.
        {
            use chunk::{
                FLOOR_FLAT, FLOOR_PIT_PLACEHOLDER, FLOOR_RAISED, FLOOR_RAMP_EAST_WEST,
                FLOOR_RAMP_NORTH_SOUTH, FLOOR_STAIRS_EAST_WEST, FLOOR_STAIRS_NORTH_SOUTH,
                FLOOR_SUNKEN,
            };
            let (
                mut v_total,
                mut v_raised,
                mut v_sunken,
                mut v_ramps,
                mut v_stairs,
                mut v_pits,
                mut near,
            ) = (0u32, 0u32, 0u32, 0u32, 0u32, 0u32, 0u32);
            for c in self.chunks.values() {
                let p = c.layout.floor_profile;
                if p == FLOOR_FLAT {
                    continue;
                }
                v_total += 1;
                match p {
                    FLOOR_RAISED => v_raised += 1,
                    FLOOR_SUNKEN => v_sunken += 1,
                    FLOOR_RAMP_NORTH_SOUTH | FLOOR_RAMP_EAST_WEST => v_ramps += 1,
                    FLOOR_STAIRS_NORTH_SOUTH | FLOOR_STAIRS_EAST_WEST => v_stairs += 1,
                    FLOOR_PIT_PLACEHOLDER => v_pits += 1,
                    _ => {}
                }
                if c.pos.0.abs() <= 2 && c.pos.1.abs() <= 2 {
                    near += 1;
                }
            }
            info!(
                "MPTRACE step=V210 event=vertical_distribution seed={} vertical_chunks={} raised={} sunken={} ramps={} stairs={} pits={} near_spawn_vertical={}",
                self.seed, v_total, v_raised, v_sunken, v_ramps, v_stairs, v_pits, near
            );
        }
        info!(
            "MPTRACE step=DD event=level0_opening_stats seed={} reciprocal_ok={} total_openings={} doorframes={} arches={} lowwalls={}",
            self.seed, connected, total_openings, doorframes, arches, lowwalls
        );

        // Phase 2.6 layout audit — grammar distribution + density for this seed.
        let corridor_spines = template_counts[1];
        let room_clusters = template_counts[0] + template_counts[4] + template_counts[5];
        let open_halls = template_counts[10];
        let pillar_fields = template_counts[9];
        let maze_pockets = template_counts[7] + template_counts[14] + template_counts[16];
        let side_rooms = template_counts[2] + template_counts[6];
        let special_branches = template_counts[14]
            + template_counts[16]
            + template_counts[17]
            + template_counts[15]
            + template_counts[12];
        info!(
            "MPTRACE step=V26 event=layout_distribution seed={} corridor_spines={} room_clusters={} open_halls={} pillar_fields={} maze_pockets={} side_rooms={} special_branches={} vertical_nodes={}",
            self.seed,
            corridor_spines,
            room_clusters,
            open_halls,
            pillar_fields,
            maze_pockets,
            side_rooms,
            special_branches,
            vertical_chunks
        );
        info!(
            "MPTRACE step=V26 event=layout_density seed={} avg_wall_pct={:.3} avg_walkable_pct={:.3} avg_special_pct={:.3}",
            self.seed,
            wall as f32 / denom,
            walkable as f32 / denom,
            special as f32 / denom
        );

        // Phase 2.7 architecture + edge-wall audit.
        {
            use crate::world::chunk::{
                EDGE_KIND_ARCH, EDGE_KIND_DOOR, EDGE_KIND_FALSE_DOOR, EDGE_KIND_HALF_WALL,
                EDGE_KIND_LOW_WALL, EDGE_KIND_PARTITION, EDGE_KIND_WALL,
            };
            use crate::world::collision::edge_is_full_wall;
            let mut full_walls = 0u32;
            let mut e_doors = 0u32;
            let mut e_arches = 0u32;
            let mut e_low = 0u32;
            let mut e_half = 0u32;
            let mut e_false = 0u32;
            let mut merged_segments = 0u32;
            let mut tally = |k: u8, prev_full: &mut bool| {
                match k {
                    EDGE_KIND_DOOR => e_doors += 1,
                    EDGE_KIND_ARCH => e_arches += 1,
                    EDGE_KIND_LOW_WALL => e_low += 1,
                    EDGE_KIND_HALF_WALL => e_half += 1,
                    EDGE_KIND_FALSE_DOOR => e_false += 1,
                    EDGE_KIND_WALL | EDGE_KIND_PARTITION => full_walls += 1,
                    _ => {}
                }
                let full = edge_is_full_wall(k);
                if full && !*prev_full {
                    merged_segments += 1;
                }
                *prev_full = full;
            };
            for c in self.chunks.values() {
                let l = &c.layout;
                if !l.has_edges() {
                    continue;
                }
                let g = l.grid_size as usize;
                for z in 0..g {
                    let mut prev = false;
                    for bx in 0..=g {
                        tally(l.edge_v(bx, z), &mut prev);
                    }
                }
                for bz in 0..=g {
                    let mut prev = false;
                    for x in 0..g {
                        tally(l.edge_h(x, bz), &mut prev);
                    }
                }
            }
            let starter = template_counts[5];
            let broken_corridors = template_counts[2];
            let arch_transitions = template_counts[11];
            let special = template_counts[12]
                + template_counts[13]
                + template_counts[14]
                + template_counts[15]
                + template_counts[16]
                + template_counts[17];
            info!(
                "MPTRACE step=V27 event=layout_architecture_distribution seed={} starter={} corridors={} broken_corridors={} room_clusters={} maze_pockets={} open_halls={} pillar_fields={} arch_transitions={} special={}",
                self.seed,
                starter,
                corridor_spines,
                broken_corridors,
                room_clusters,
                maze_pockets,
                open_halls,
                pillar_fields,
                arch_transitions,
                special
            );
            info!(
                "MPTRACE step=V27 event=edge_wall_stats seed={} full_walls={} doors={} arches={} low_walls={} half_walls={} false_doors={} merged_segments={}",
                self.seed, full_walls, e_doors, e_arches, e_low, e_half, e_false, merged_segments
            );
        }

        info!(
            "MPTRACE step=AP event=structure_generation_complete revision={} chunks={} items={} entities={}",
            self.revision,
            self.chunks.len(),
            self.visible_item_views().len(),
            self.visible_entity_views().len()
        );

        info!(
            "MPTRACE step=AW event=level0_generation_complete seed={} structures={} chunks={} connected={}",
            self.seed,
            structure_count,
            self.chunks.len(),
            connected
        );

        // Volumetric "Rubik grid" showcase (replaces the decorative VISFIX
        // overlay, which is gated off by default in the generator).
        if self.seed == volumetric_grid::SHOWCASE_SEED {
            volumetric_grid::log_showcase_once(true);
        }

        // Phase 3.0C-FIX — audit the Level 0 → volumetric adapter output.
        // Aggregate counts only, so unordered chunk iteration cannot affect
        // the logged values.
        let layer0: Vec<&Chunk> = self.chunks.values().filter(|c| c.layer == 0).collect();
        volumetric_grid::log_level0_adapter_fix_once(self.seed, &layer0);

        let rg = self
            .world_graph
            .as_ref()
            .and_then(|wg| wg.level0_region_graph());

        info!(
            "MPTRACE step=RG0 event=level0_region_graph_built seed={} nodes={} edges={}",
            self.seed,
            rg.map_or(0, |g| g.node_count()),
            rg.map_or(0, |g| g.edge_count()),
        );

        if let Some(graph) = rg {
            let a = audit_level0_region_graph(graph);
            info!(
                "MPTRACE step=RG1 event=region_graph_audit seed={} nodes={} edges={} accessible={} visual_only={} traversable={} dangling={} manila={} danger={} blocked={} sealed={} underfloor={}",
                self.seed,
                a.node_count,
                a.edge_count,
                a.accessible_node_count,
                a.visual_only_edge_count,
                a.traversable_edge_count,
                a.dangling_edge_count,
                a.manila_room_count,
                a.danger_pocket_count,
                a.blocked_portal_count,
                a.sealed_upper_count,
                a.underfloor_count,
            );

            // Phase 3.1G — resolve starter node from graph data rather than hardcoded id 0.
            // Purely diagnostic; does not gate gameplay, spawning, collision, or IPC.
            if let Some(starter_id) = starter_node_id(graph) {
                let reachable = reachable_from(graph, starter_id);
                let total = graph.node_count();
                let graph_connected = reachable.len() == total;
                info!(
                    "MPTRACE step=RG2 event=region_graph_connectivity seed={} starter_id={} reachable_structures={} total_structures={} graph_connected={}",
                    self.seed,
                    starter_id,
                    reachable.len(),
                    total,
                    graph_connected,
                );
                // Phase 3.1H — parity between chunk-level BFS (`connected`) and
                // structure-level RegionGraph connectivity (`graph_connected`).
                // Diagnostic only; never gates generation or gameplay.
                info!(
                    "MPTRACE step=RG3 event=connectivity_parity seed={} chunk_connected={} graph_connected={} parity={} reachable_structures={} total_structures={}",
                    self.seed,
                    connected,
                    graph_connected,
                    connected == graph_connected,
                    reachable.len(),
                    total,
                );
            } else {
                info!(
                    "MPTRACE step=RG2 event=region_graph_connectivity seed={} starter_node_missing=true",
                    self.seed,
                );
                info!(
                    "MPTRACE step=RG3 event=connectivity_parity seed={} chunk_connected={} graph_connected=unknown parity=false starter_node_missing=true",
                    self.seed,
                    connected,
                );
            }
        }
    }

    /// Load all chunks within the ownership radius of the player and unload
    /// chunks beyond the (larger) unload radius.
    pub fn update_ownership(&mut self, player_pos: Vec3, player_id: PeerId) {
        let player_chunk = world_to_chunk(player_pos);
        let radius = self.config.ownership_radius;
        let unload_radius = self.config.unload_radius.max(radius);
        let needed_xz: HashSet<ChunkPos> =
            chunks_in_radius(player_chunk, radius).into_iter().collect();
        let keep_xz: HashSet<ChunkPos> = chunks_in_radius(player_chunk, unload_radius)
            .into_iter()
            .collect();
        let mut changed = false;

        // Unload chunks outside the unload radius; preserve every loaded layer
        // inside it. The load/unload gap is hysteresis against boundary thrash.
        let to_remove: Vec<LayeredChunkPos> = self
            .chunks
            .keys()
            .filter(|pos| !keep_xz.contains(&(pos.0, pos.2)))
            .copied()
            .collect();
        for pos in to_remove {
            self.chunks.remove(&pos);
            changed = true;
        }

        let seed = self.seed;
        // Restore deliberate V30A multi-layer showcase chunks that come back
        // into XZ range, from the pristine per-seed cache. The cache is built
        // at most once per seed — never re-run the full Level 0 generation
        // pipeline from here (that regen was the main streaming stall).
        if self.v30a_chunk_cache.is_none() {
            self.v30a_chunk_cache = Some(
                generator::generate_initial_structure_chunks(seed)
                    .into_iter()
                    .map(|(_, chunk)| chunk)
                    .filter(chunk_is_v30a)
                    .collect(),
            );
        }
        let v30a_cache = self.v30a_chunk_cache.take().unwrap_or_default();
        for cached in &v30a_cache {
            if !needed_xz.contains(&cached.pos) {
                continue;
            }
            let key = cached.key();
            if self
                .chunks
                .get(&key)
                .map(|existing| !chunk_is_v30a(existing))
                .unwrap_or(true)
            {
                let mut chunk = cached.clone();
                chunk.owner = Some(player_id);
                self.chunks.insert(key, chunk);
                changed = true;
            }
        }
        self.v30a_chunk_cache = Some(v30a_cache);

        // Generate missing layer-0 chunks only. Upper/lower ambient layers are
        // intentionally not generated in 3.0A.
        for pos in &needed_xz {
            let key = layered_chunk_pos(*pos, 0);
            if let Some(existing) = self.chunks.get_mut(&key) {
                existing.owner = Some(player_id);
            } else {
                let mut c = generator::generate_chunk(seed, *pos);
                c.owner = Some(player_id);
                self.chunks.insert(key, c);
                changed = true;
            }
        }

        // Loading/unloading changes what Unity must render: move the revision
        // and drop the cached views so the next WorldState rebuilds them.
        if changed {
            self.revision = self.revision.wrapping_add(1);
            self.view_cache = None;
        }

        // The three per-layer tallies feed the V30AFIX log only, so they are counted inside the
        // guard — three full scans of `chunks` were being done on every call and thrown away
        // whenever no V30A chunk was loaded, which is the common case.
        let v30a = self.chunks.values().filter(|c| chunk_is_v30a(c)).count();
        if v30a > 0 {
            let layer0 = self.chunks.values().filter(|c| c.layer == 0).count();
            let lower = self.chunks.values().filter(|c| c.layer < 0).count();
            let upper = self.chunks.values().filter(|c| c.layer > 0).count();
            info!(
                "MPTRACE step=V30AFIX event=visible_layer_chunks layer0={} lower={} upper={} v30a={} total={}",
                layer0,
                lower,
                upper,
                v30a,
                self.chunks.len()
            );
        }
        if self.seed == 7778 {
            let volume_chunks = self
                .chunks
                .values()
                .filter(|c| !c.layout.inter_layer_volumes.is_empty())
                .count();
            let volume_count: usize = self
                .chunks
                .values()
                .map(|c| c.layout.inter_layer_volumes.len())
                .sum();
            let validation_loaded = self.chunks.contains_key(&(1, 0, 3));
            let original_loaded = self.chunks.contains_key(&(-5, 0, -1));
            info!(
                "MPTRACE step=V30A2 event=v30a2_visfix_backend_visible_radius_check loaded_chunks={} volume_chunks={} volumes={} validation_loaded={} original_loaded={} ownership_radius={}",
                self.chunks.len(),
                volume_chunks,
                volume_count,
                validation_loaded,
                original_loaded,
                radius
            );
        }
    }

    /// Tick all chunk teleport timers (called at 1hz from the game loop).
    /// `tick` is the caller's game-loop tick counter, threaded through ONLY for the
    /// TEMP DIAG log below (TP attribution audit) — no other use, no behavior change.
    pub fn tick_teleportation(&mut self, tick: u64) -> Vec<TeleportOutcome> {
        let mut events = Vec::new();
        let (t_min, t_max) = self.config.teleport_interval;

        let chunk_positions: Vec<LayeredChunkPos> = self.chunks.keys().copied().collect();
        for key in chunk_positions {
            let should_teleport = {
                let chunk = match self.chunks.get_mut(&key) {
                    Some(c) => c,
                    None => continue,
                };
                if chunk.layer != 0 || chunk.layout.vertical_flags & chunk::V30A_CONNECTOR != 0 {
                    continue;
                }
                match chunk.state {
                    ChunkState::Active {
                        stabilized: false,
                        anchored: false,
                    } => {
                        chunk.teleport_timer -= 1.0;
                        chunk.teleport_timer <= 0.0
                    }
                    ChunkState::Active {
                        stabilized: true,
                        anchored: false,
                    } => {
                        chunk.teleport_timer -= 1.0;
                        if chunk.teleport_timer <= 0.0 {
                            // Stabilizer has 95% chance to prevent teleport.
                            let roll: f32 = self.rng.gen();
                            if roll < 0.95 {
                                chunk.teleport_timer = self.rng.gen_range(t_min..t_max);
                                false
                            } else {
                                true
                            }
                        } else {
                            false
                        }
                    }
                    _ => false, // Anchored or inactive chunks don't teleport.
                }
            };

            if should_teleport {
                let old_key = key;
                let old_pos = (old_key.0, old_key.2);
                let new_offset_x = self.rng.gen_range(-30..=30i32);
                let new_offset_z = self.rng.gen_range(-30..=30i32);
                let new_seed = self.rng.gen::<u64>();

                // Regenerate in-place with a new random seed.
                if let Some(chunk) = self.chunks.get_mut(&key) {
                    let old_seed = chunk.seed;
                    chunk.seed = new_seed;
                    chunk.teleport_timer = self.rng.gen_range(t_min..t_max);
                    // Regenerate entities and items (old ones are lost).
                    let gen = generator::generate_chunk(new_seed, old_pos);
                    chunk.entities = gen.entities;
                    chunk.items = gen.items;
                    chunk.template_id = gen.template_id;
                    chunk.rotation = gen.rotation;
                    chunk.mirrored = gen.mirrored;
                    chunk.has_workbench = gen.has_workbench;
                    self.revision = self.revision.wrapping_add(1);

                    info!("Chunk {:?} teleported (new seed {})", old_pos, new_seed);
                    // TEMP DIAG (TP-source audit; REMOVE after diagnosis): legitimate world
                    // mechanic (chunk displacement, CLAUDE.md core mechanic) vs a client-visible
                    // TP bug look identical to the player — this makes the legitimate ones
                    // greppable so they can be cross-referenced against reported TPs and ruled out.
                    info!(
                        "MPTRACE step=CHUNK_DISPLACEMENT tick={} chunk_id=({},{}) old_seed={} new_seed={}",
                        tick, old_pos.0, old_pos.1, old_seed, new_seed
                    );
                    events.push(TeleportOutcome {
                        event: GameEvent {
                            event_type: "chunk_teleported".into(),
                            data: serde_json::json!({
                                "chunk_pos": [old_pos.0, old_pos.1],
                                "new_offset": [new_offset_x, new_offset_z],
                            }),
                        },
                        old_pos: [old_pos.0, old_pos.1],
                        new_pos: [old_pos.0 + new_offset_x, old_pos.1 + new_offset_z],
                        new_seed,
                    });
                }
            }
        }
        events
    }

    /// Tick all entity AI in owned chunks. Returns accumulated damage to the player.
    pub fn tick_entities(
        &mut self,
        dt: f32,
        player_pos: Vec3,
        player_id: PeerId,
    ) -> (f32, Vec<GameEvent>) {
        let mut total_damage = 0.0f32;
        let mut events = Vec::new();

        // The key snapshot existed only to avoid holding a borrow of `self.chunks` across the uses
        // of `self.rng` / `self.respawn_queue`. Destructuring gives those three disjoint borrows
        // directly, so the per-tick Vec of every loaded chunk key disappears. Iteration order is
        // unchanged: `keys()` + `get_mut` walked the map in exactly the order `values_mut()` does.
        let Self {
            chunks,
            rng,
            respawn_queue,
            ..
        } = self;
        for chunk in chunks.values_mut() {
            if !chunk.is_active() {
                continue;
            }
            let pos = chunk.pos;

            let mut despawned_ids = Vec::new();
            for entity in chunk.entities.iter_mut() {
                let event = entity.update(dt, player_pos, player_id, pos, rng);
                match event {
                    EntityEvent::AttackPlayer { damage, .. } => {
                        total_damage += damage;
                        events.push(GameEvent {
                            event_type: "damage_taken".into(),
                            data: serde_json::json!({
                                "amount": damage,
                                "source": entity.entity_type.type_name(),
                            }),
                        });
                    }
                    EntityEvent::Despawned => {
                        despawned_ids.push(entity.id);
                    }
                    EntityEvent::None => {}
                }
            }

            // Remove despawned entities; queue them for respawn. Nothing despawned is the normal
            // case, and then the retain is a full scan of the chunk's entities for no reason.
            if despawned_ids.is_empty() {
                continue;
            }
            for id in &despawned_ids {
                let timer = rng.gen_range(120.0..300.0);
                respawn_queue.push((*id, pos, timer));
            }
            chunk.entities.retain(|e| !despawned_ids.contains(&e.id));
        }

        (total_damage, events)
    }

    /// Tick respawn timers and spawn fresh entities back into their chunks.
    pub fn tick_respawns(&mut self, dt: f32) {
        let mut still_waiting = Vec::new();
        for (id, chunk_pos, mut timer) in self.respawn_queue.drain(..) {
            timer -= dt;
            if timer <= 0.0 {
                if let Some(chunk) = self.chunks.get_mut(&layered_chunk_pos(chunk_pos, 0)) {
                    let spawn_pos = crate::utils::chunk_center(chunk_pos);
                    let etype = match self.rng.gen_range(0..3) {
                        0 => entity::EntityType::Lurker,
                        1 => entity::EntityType::Crawler,
                        _ => entity::EntityType::Shadow,
                    };
                    chunk.entities.push(entity::Entity::new(
                        generator::next_entity_id_pub(),
                        etype,
                        spawn_pos,
                    ));
                }
            } else {
                still_waiting.push((id, chunk_pos, timer));
            }
        }
        self.respawn_queue = still_waiting;
    }

    /// Build the stat context from actual world state around the player.
    pub fn stat_context_for(&self, player_pos: Vec3, nearby_players: u32) -> StatContext {
        let player_chunk = chunk_key_for_player_pos(player_pos);
        let mut entities_visible = 0u32;
        let mut chunk_stabilized = false;

        if let Some(chunk) = self.chunks.get(&player_chunk) {
            entities_visible = chunk.entities.iter().filter(|e| e.is_alive()).count() as u32;
            chunk_stabilized = matches!(
                chunk.state,
                ChunkState::Active {
                    stabilized: true,
                    ..
                } | ChunkState::Active { anchored: true, .. }
            );
        }

        StatContext {
            entities_visible,
            chunk_stabilized,
            nearby_players,
            light_level: 1.0, // Phase 5: lighting data from Unity
        }
    }

    // ─── Networking integration (Phase 3) ───

    /// Apply a full world sync received from the host peer.
    pub fn apply_world_sync(
        &mut self,
        world_seed: u64,
        world_revision: u64,
        chunks: &[crate::network::protocol::ChunkSyncData],
        local_id: crate::network::PeerId,
    ) {
        self.seed = world_seed;
        self.revision = world_revision;
        self.chunks.clear();
        self.v30a_chunk_cache = None;
        self.view_cache = None;
        self.world_graph = None;
        for data in chunks {
            self.apply_chunk_sync(data, local_id);
        }
        info!(
            "MPTRACE step=Z event=apply_world_snapshot self_id={} revision={} chunks={} entities={} items={}",
            local_id,
            self.revision,
            self.chunks.len(),
            self.visible_entity_views().len(),
            self.visible_item_views().len()
        );
    }

    /// Apply a chunk transfer from a remote peer (take ownership).
    pub fn apply_chunk_transfer(
        &mut self,
        data: &crate::network::protocol::ChunkSyncData,
        local_id: crate::network::PeerId,
    ) {
        self.apply_chunk_sync(data, local_id);
    }

    /// Apply a single chunk sync data to the world.
    fn apply_chunk_sync(
        &mut self,
        data: &crate::network::protocol::ChunkSyncData,
        owner_id: crate::network::PeerId,
    ) {
        self.view_cache = None;
        let pos: ChunkPos = (data.pos[0], data.pos[1]);
        let layer = data.layer;
        let chunk = self
            .chunks
            .entry(layered_chunk_pos(pos, layer))
            .or_insert_with(|| generator::generate_chunk_layer(self.seed, pos, layer));

        chunk.layer = layer;
        chunk.seed = data.seed;
        chunk.template_id = data.template_id;
        chunk.rotation = data.rotation;
        chunk.mirrored = data.mirrored;
        chunk.has_workbench = data.has_workbench;
        chunk.layout = data.layout.clone();
        chunk.teleport_timer = data.teleport_timer;
        chunk.owner = Some(owner_id);
        chunk.state = ChunkState::Active {
            stabilized: data.stabilized,
            anchored: data.anchored,
        };

        // Sync entities from remote data.
        chunk.entities.clear();
        for e_data in &data.entities {
            let etype = match e_data.entity_type.as_str() {
                "crawler" => entity::EntityType::Crawler,
                "shadow" => entity::EntityType::Shadow,
                _ => entity::EntityType::Lurker,
            };
            let mut e = entity::Entity::new(
                e_data.id,
                etype,
                crate::utils::Vec3::from_array(e_data.position),
            );
            e.rotation = e_data.rotation;
            e.health = e_data.health;
            chunk.entities.push(e);
        }

        // Sync items from remote data.
        chunk.items.clear();
        for i_data in &data.items {
            let item = match i_data.item_type.as_str() {
                "metal" => crate::player::inventory::Item::Metal,
                "circuit" => crate::player::inventory::Item::Circuit,
                "battery" => crate::player::inventory::Item::Battery,
                "cable" => crate::player::inventory::Item::Cable,
                "food" => crate::player::inventory::Item::Food,
                "water" => crate::player::inventory::Item::Water,
                "medicine" => crate::player::inventory::Item::Medicine,
                "tool" => crate::player::inventory::Item::Tool,
                _ => crate::player::inventory::Item::Metal,
            };
            chunk.items.push(chunk::DroppedItem {
                id: i_data.id,
                item,
                quantity: i_data.quantity,
                position: crate::utils::Vec3::from_array(i_data.position),
            });
        }
    }

    /// Apply a remote chunk teleport event.
    ///
    /// Deliberately does NOT assign `chunk.layout`, mirroring `tick_teleportation` (the
    /// owner-side twin above). That assignment was the ONLY difference between the two
    /// paths, and it was wrong three ways over:
    ///   1. `gen.layout` is the BARE expansion layout — `generator::generate_chunk` builds
    ///      `build_chunk_layout(template_id, rotation)` and nothing else — so writing it
    ///      erases the structure metadata (`macro_id`/`macro_local`/`macro_size`), the
    ///      starter-spawn reservation and the level-0 edge continuity that the real
    ///      generation path applies on top.
    ///   2. `chunk.layout` is the SERVER collision source (`collision.rs` `LayoutSource`)
    ///      and the spawn/respawn resolver's map (`find_safe_spawn`), whose result the
    ///      client hard-snaps to. The RENDERED geometry comes from grid_gen keyed by
    ///      (world_seed, cx, cz, layer) and does not move on displacement — so moving the
    ///      layout moved collision and respawn out from under geometry that stayed put.
    ///   3. Clearing `vertical_flags` made `chunk_is_v30a` false, so the next
    ///      `update_ownership` re-inserted the pristine V30A chunk from the cache and
    ///      reverted the whole displacement.
    ///
    /// The other fields stay deliberately in lockstep with the owner-side twin. If you are
    /// here because the asymmetry looks like an oversight: it is not — see
    /// `apply_remote_teleport_leaves_layout_untouched`.
    pub fn apply_remote_teleport(&mut self, old_pos: [i32; 2], new_seed: u64) {
        let pos: ChunkPos = (old_pos[0], old_pos[1]);
        if let Some(chunk) = self.chunks.get_mut(&layered_chunk_pos(pos, 0)) {
            chunk.seed = new_seed;
            let gen = generator::generate_chunk(new_seed, pos);
            chunk.entities = gen.entities;
            chunk.items = gen.items;
            chunk.template_id = gen.template_id;
            chunk.rotation = gen.rotation;
            chunk.mirrored = gen.mirrored;
            chunk.has_workbench = gen.has_workbench;
            chunk.teleport_timer = self
                .rng
                .gen_range(self.config.teleport_interval.0..self.config.teleport_interval.1);
            self.revision = self.revision.wrapping_add(1);
        }
    }

    pub fn interact_with_item(
        &mut self,
        target_id: u32,
        requester_pos: Vec3,
        max_distance: f32,
    ) -> Result<(String, u16), String> {
        for chunk in self.chunks.values_mut() {
            if let Some(idx) = chunk.items.iter().position(|item| item.id == target_id) {
                let item_pos = chunk.items[idx].position;
                let distance = requester_pos.distance(item_pos);
                if distance > max_distance {
                    return Err(format!("too_far distance={distance:.2}"));
                }

                let item = chunk.items.remove(idx);
                self.revision = self.revision.wrapping_add(1);
                return Ok((item.item.type_name().into(), item.quantity));
            }
        }

        Err("missing_or_inactive".into())
    }

    /// Spawn a player-dropped item at `position`. Inserts it into the chunk that
    /// contains `position` and bumps the revision so the next `broadcast_world_sync`
    /// propagates it to every peer (the same path the pickup uses to remove items).
    /// Returns the new item id, or `None` if no chunk is loaded at that position.
    pub fn spawn_dropped_item(
        &mut self,
        position: Vec3,
        item: crate::player::inventory::Item,
        quantity: u16,
    ) -> Option<u32> {
        let key = layered_chunk_pos(world_to_chunk(position), 0);
        let chunk = self.chunks.get_mut(&key)?;
        let id = next_dropped_item_id();
        chunk.items.push(chunk::DroppedItem {
            id,
            item,
            quantity,
            position,
        });
        self.revision = self.revision.wrapping_add(1);
        Some(id)
    }

    /// Set a chunk as anchored (from an AnchorBroadcast).
    pub fn set_chunk_anchored(&mut self, chunk_pos: [i32; 2]) {
        let pos: ChunkPos = (chunk_pos[0], chunk_pos[1]);
        if let Some(chunk) = self.chunks.get_mut(&layered_chunk_pos(pos, 0)) {
            chunk.state = ChunkState::Active {
                stabilized: true,
                anchored: true,
            };
            self.view_cache = None;
        }
    }

    /// Set a chunk as stabilized (from a StabilizerBroadcast).
    pub fn set_chunk_stabilized(&mut self, chunk_pos: [i32; 2]) {
        let pos: ChunkPos = (chunk_pos[0], chunk_pos[1]);
        if let Some(chunk) = self.chunks.get_mut(&layered_chunk_pos(pos, 0)) {
            if let ChunkState::Active { anchored, .. } = chunk.state {
                chunk.state = ChunkState::Active {
                    stabilized: true,
                    anchored,
                };
                self.view_cache = None;
            }
        }
    }

    /// Pack the cell grid + edge arrays into one `Vec<u16>` for the IPC
    /// `layout_cells` field (Phase 2.7): `[cells (g*g)] [edges_v ((g+1)*g)]
    /// [edges_h (g*(g+1))]`. The renderer reads the tail to place edge walls;
    /// the P2P path carries the real `ChunkLayoutV1` edge fields natively, so
    /// no IPC/protocol struct changes are needed.
    fn pack_layout_cells(layout: &chunk::ChunkLayoutV1) -> Vec<u16> {
        let mut out = layout.cells.clone();
        out.extend(layout.edges_v.iter().map(|&k| k as u16));
        out.extend(layout.edges_h.iter().map(|&k| k as u16));
        out
    }

    /// Attach the backend-authored unified volumetric column to layer-0 chunks.
    /// Render-only: it does not touch chunk layout / collision authority. Gated by the
    /// module-level `ENABLE_LEVEL0_VOLUMETRIC_COLUMNS` rollout flag (see its doc).
    fn volumetric_grid_view_for(
        &self,
        chunk: &Chunk,
    ) -> Option<volumetric_grid::VolumetricGridViewV0> {
        if chunk.layer != 0 {
            return None;
        }

        let is_showcase = self.seed == volumetric_grid::SHOWCASE_SEED
            && volumetric_grid::is_showcase_chunk_pos(chunk.pos);

        // The `chunk_is_v30a(chunk)` disjunct was removed from this gate (2026-07-02):
        // under worldgraph-v1 nearly every layer-0 chunk carries a V30A vertical flag
        // (measured 60/62), so it re-attached the column payload world-wide (~1.3 MB per
        // WorldState at 10 Hz) exactly like the rollout flag above — same IPC-contract
        // violation, same event-dropping lag. The predicate still gates the V30A collision
        // cache and vertical navigation elsewhere; V30A features render via the flat
        // fallback path (vertical_flags / inter_layer_volumes still ship in ChunkView).
        if is_showcase || ENABLE_LEVEL0_VOLUMETRIC_COLUMNS {
            return volumetric_grid::unified_column_view(self.seed, chunk);
        }

        None
    }
    /// Build visible chunk views for the WorldState IPC message.
    ///
    /// The result is cached per (revision, chunk_count): the 10hz WorldState
    /// send reuses the cached views until the world actually changes, instead
    /// of repacking every layout and rebuilding volumetric grids each send.
    pub fn visible_chunk_views(&mut self) -> Vec<ChunkView> {
        if let Some((rev, count, views)) = &self.view_cache {
            if *rev == self.revision && *count == self.chunks.len() {
                return views.clone();
            }
        }
        let views = self.build_chunk_views();
        self.view_cache = Some((self.revision, self.chunks.len(), views.clone()));
        views
    }

    /// Debug-only world-space markers for the parallel verticality layer
    /// (Phase 6.6). Derived deterministically from
    /// `RegionGraph.virtual_vertical_nodes`; no collision, no traversal.
    pub fn vertical_debug_marker_views(&self) -> Vec<VerticalDebugMarkerV0> {
        let Some(rg) = self
            .world_graph
            .as_ref()
            .and_then(|wg| wg.level0_region_graph())
        else {
            return Vec::new();
        };
        export_vertical_debug_markers(
            &rg.virtual_vertical_nodes,
            chunk::LAYOUT_CELL_SIZE,
            chunk::LAYOUT_GRID_SIZE,
            chunk::LAYER_HEIGHT,
        )
    }

    fn build_chunk_views(&self) -> Vec<ChunkView> {
        let mut views: Vec<ChunkView> = self
            .chunks
            .values()
            .map(|c| ChunkView {
                chunk_schema: 2,
                pos: [c.pos.0, c.pos.1],
                layer: c.layer,
                layer_y: layer_y(c.layer),
                template_id: c.template_id,
                rotation: c.rotation,
                mirrored: c.mirrored,
                state: c.state.render_name().into(),
                has_workbench: c.has_workbench,
                layout_grid_size: c.layout.grid_size,
                layout_cell_size: c.layout.cell_size,
                layout_cells: Self::pack_layout_cells(&c.layout),
                edge_openings: c.layout.edge_openings,
                macro_id: c.layout.macro_id,
                zone_kind: c.layout.zone_kind,
                macro_local: c.layout.macro_local,
                macro_size: c.layout.macro_size,
                floor_level: c.layout.floor_level,
                floor_profile: c.layout.floor_profile,
                ceiling_profile: c.layout.ceiling_profile,
                light_profile: c.layout.light_profile,
                anomaly_flags: c.layout.anomaly_flags,
                vertical_flags: c.layout.vertical_flags,
                inter_layer_volumes: c.layout.inter_layer_volumes.clone(),
                volumetric_grid: self.volumetric_grid_view_for(c),
            })
            .collect();
        views.sort_by_key(|c| (c.pos[0], c.layer, c.pos[1]));
        self.log_unified_volumetric_views(&views);
        self.log_v30a2_visfix_ipc_views(&views);
        views
    }

    fn log_unified_volumetric_views(&self, views: &[ChunkView]) {
        let column_count = views.iter().filter(|c| c.volumetric_grid.is_some()).count();
        if column_count == 0 {
            return;
        }
        if UNIFIED_VOLUMETRIC_IPC_LAST_REVISION
            .compare_exchange(0, self.revision, Ordering::Relaxed, Ordering::Relaxed)
            .is_err()
        {
            return;
        }

        let layer_band_count: usize = views
            .iter()
            .filter_map(|c| c.volumetric_grid.as_ref())
            .map(|g| g.layer_bands.len())
            .sum();
        let cell_count: usize = views
            .iter()
            .filter_map(|c| c.volumetric_grid.as_ref())
            .map(|g| g.cells.len())
            .sum();
        let face_count: usize = views
            .iter()
            .filter_map(|c| c.volumetric_grid.as_ref())
            .map(|g| g.faces.len())
            .sum();
        let vertical_access_count: usize = views
            .iter()
            .filter_map(|c| c.volumetric_grid.as_ref())
            .map(|g| g.vertical_access.len())
            .sum();
        let spawn_column = views.iter().find(|c| c.pos == [0, 0] && c.layer == 0);
        let rubik_columns = views
            .iter()
            .filter_map(|c| c.volumetric_grid.as_ref())
            .filter(|g| g.source == volumetric_grid::UNIFIED_COLUMN_SOURCE_RUBIKGRID)
            .count();

        info!("MPTRACE step=V30C event=unified_volumetric_world_enabled=true");
        info!(
            "MPTRACE step=V30C event=unified_volumetric_seed seed={}",
            self.seed
        );
        if let Some(chunk) = spawn_column {
            info!(
                "MPTRACE step=V30C event=unified_volumetric_spawn_column chunk=({},{},{}) has_column={}",
                chunk.pos[0],
                chunk.layer,
                chunk.pos[1],
                chunk.volumetric_grid.is_some()
            );
        }
        info!("MPTRACE step=V30C event=unified_volumetric_column_count columns={column_count}");
        info!(
            "MPTRACE step=V30C event=unified_volumetric_layer_band_count layer_bands={layer_band_count}"
        );
        info!("MPTRACE step=V30C event=unified_volumetric_cell_count cells={cell_count}");
        info!("MPTRACE step=V30C event=unified_volumetric_face_count faces={face_count}");
        info!(
            "MPTRACE step=V30C event=unified_volumetric_vertical_access_count vertical_access={vertical_access_count}"
        );
        info!("MPTRACE step=V30C event=unified_volumetric_legacy_flat_adapter_enabled fallback_only=true");
        info!(
            "MPTRACE step=V30C event=unified_volumetric_rubikgrid_integrated=true rubik_columns={rubik_columns}"
        );
        info!("MPTRACE step=V30C event=unified_volumetric_world_ready");
    }

    fn log_v30a2_visfix_ipc_views(&self, views: &[ChunkView]) {
        if self.seed != 7778 {
            return;
        }
        let total_volumes: usize = views.iter().map(|c| c.inter_layer_volumes.len()).sum();
        if total_volumes == 0 {
            return;
        }
        if V30A2_VISFIX_IPC_LAST_REVISION
            .compare_exchange(0, self.revision, Ordering::Relaxed, Ordering::Relaxed)
            .is_err()
        {
            return;
        }

        info!(
            "MPTRACE step=V30A2 event=v30a2_visfix_ipc_chunk_volume_count total_visible_chunks={} total_volumes={}",
            views.len(),
            total_volumes
        );
        for chunk in views.iter().filter(|c| !c.inter_layer_volumes.is_empty()) {
            info!(
                "MPTRACE step=V30A2 event=v30a2_visfix_ipc_chunk_volume_count chunk=({},{},{}) volume_count={} kinds={:?}",
                chunk.pos[0],
                chunk.layer,
                chunk.pos[1],
                chunk.inter_layer_volumes.len(),
                chunk
                    .inter_layer_volumes
                    .iter()
                    .map(|v| v.kind.as_str())
                    .collect::<Vec<_>>()
            );
            info!(
                "MPTRACE step=V30A2 event=v30a2_visfix_ipc_layer_y_sent chunk=({},{},{}) layer_y={:.2}",
                chunk.pos[0],
                chunk.layer,
                chunk.pos[1],
                chunk.layer_y
            );
            info!(
                "MPTRACE step=V30A2 event=v30a2_visfix_ipc_schema_2_confirmed chunk=({},{},{}) chunk_schema={}",
                chunk.pos[0],
                chunk.layer,
                chunk.pos[1],
                chunk.chunk_schema
            );
        }
    }

    /// Build visible entity views for the WorldState IPC message.
    pub fn visible_entity_views(&self) -> Vec<EntityView> {
        self.chunks
            .values()
            .flat_map(|c| {
                c.entities.iter().map(|e| EntityView {
                    id: e.id,
                    entity_type: e.entity_type.type_name().into(),
                    position: e.position.to_array(),
                    rotation: e.rotation,
                    state: e.state.state_name().into(),
                    health_pct: e.health_pct(),
                })
            })
            .collect()
    }

    /// Build visible item views for the WorldState IPC message.
    pub fn visible_item_views(&self) -> Vec<ItemView> {
        self.chunks
            .values()
            .flat_map(|c| {
                c.items.iter().map(|i| ItemView {
                    id: i.id,
                    item_type: i.item.type_name().into(),
                    position: i.position.to_array(),
                    quantity: i.quantity,
                })
            })
            .collect()
    }
}

#[cfg(test)]
mod tests;
