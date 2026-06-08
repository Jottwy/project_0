//! World domain: chunks, procedural generation, entities.

pub mod chunk;
pub mod collision;
pub mod entity;
pub mod generator;

use std::collections::{HashMap, HashSet};

use log::info;
use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};
use serde::{Deserialize, Serialize};

use crate::ipc::{ChunkView, EntityView, GameEvent, ItemView};
use crate::network::PeerId;
use crate::player::stats::StatContext;
use crate::utils::{chunks_in_radius, world_to_chunk, ChunkPos, Vec3};
use chunk::{Chunk, ChunkState};
use entity::EntityEvent;

/// Tunable world parameters (ARCHITECTURE_V1.md §11.1).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldConfig {
    pub max_players: u16,
    pub teleport_interval: (f32, f32),
    pub entity_scaling: f32,
    pub chunk_size: f32,
    pub ownership_radius: i32,
}

impl Default for WorldConfig {
    fn default() -> Self {
        Self {
            max_players: 50,
            teleport_interval: (120.0, 600.0),
            entity_scaling: 1.0,
            chunk_size: crate::utils::CHUNK_SIZE,
            ownership_radius: 5,
        }
    }
}

/// Result of a world tick — events to emit + damage to apply to the player.
pub struct WorldTickResult {
    pub events: Vec<GameEvent>,
    pub player_damage: f32,
    pub stat_context: StatContext,
}

/// The active world state owned by this peer.
#[derive(Debug, Clone)]
pub struct World {
    pub seed: u64,
    pub revision: u64,
    pub config: WorldConfig,
    pub chunks: HashMap<ChunkPos, Chunk>,
    pub rng: StdRng,
    /// Entities that died and need respawn timers (entity_id, chunk_pos, timer).
    pub respawn_queue: Vec<(u32, ChunkPos, f32)>,
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
        }
    }

    /// Ensure a chunk exists at `pos`, generating it deterministically if needed.
    pub fn ensure_chunk(&mut self, pos: ChunkPos) -> &mut Chunk {
        let seed = self.seed;
        self.chunks
            .entry(pos)
            .or_insert_with(|| generator::generate_chunk(seed, pos))
    }

    pub fn reset_for_remote_world(&mut self, world_seed: u64, world_revision: u64) {
        self.seed = world_seed;
        self.revision = world_revision;
        self.chunks.clear();
        self.respawn_queue.clear();
        self.rng = StdRng::seed_from_u64(world_seed.wrapping_add(0xDEAD));
    }

    pub fn generate_initial_structures(&mut self, owner_id: PeerId) {
        let generated = generator::generate_initial_structure_chunks(self.seed);
        let structure_count = generator::generate_initial_structures(self.seed).len();
        let mut logged_structures = HashSet::new();
        let mut item_count = 0usize;
        let mut entity_count = 0usize;

        for (structure, mut chunk) in generated {
            if logged_structures.insert(structure.id) {
                info!(
                    "MPTRACE step=AM event=structure_created id={} type={} origin=({},{}) size=({},{}) chunks={} seed={} tags={:?}",
                    structure.id,
                    structure.structure_type.as_str(),
                    structure.origin.0,
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
                "MPTRACE step=AN event=chunk_from_structure structure_id={} chunk_id={} coord=({},{}) template_id={} rotation={}",
                structure.id,
                chunk.seed,
                chunk.pos.0,
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
            self.chunks.entry(chunk.pos).or_insert(chunk);
        }

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
        let positions: HashSet<ChunkPos> = self.chunks.keys().copied().collect();
        let mut visited = HashSet::new();
        let mut queue = std::collections::VecDeque::from([(0i32, 0i32)]);
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
                edge_is_full_wall, EDGE_KIND_ARCH, EDGE_KIND_DOOR, EDGE_KIND_FALSE_DOOR,
                EDGE_KIND_HALF_WALL, EDGE_KIND_LOW_WALL, EDGE_KIND_PARTITION, EDGE_KIND_WALL,
            };
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
    }

    /// Load all chunks within the ownership radius of the player and unload distant ones.
    pub fn update_ownership(&mut self, player_pos: Vec3, player_id: PeerId) {
        let player_chunk = world_to_chunk(player_pos);
        let radius = self.config.ownership_radius;
        let needed: HashSet<ChunkPos> =
            chunks_in_radius(player_chunk, radius).into_iter().collect();

        // Unload chunks outside radius.
        let to_remove: Vec<ChunkPos> = self
            .chunks
            .keys()
            .filter(|pos| !needed.contains(pos))
            .copied()
            .collect();
        for pos in to_remove {
            self.chunks.remove(&pos);
        }

        // Generate any missing chunks in radius.
        let seed = self.seed;
        for pos in &needed {
            self.chunks
                .entry(*pos)
                .or_insert_with(|| {
                    let mut c = generator::generate_chunk(seed, *pos);
                    c.owner = Some(player_id);
                    c
                })
                .owner = Some(player_id);
        }
    }

    /// Tick all chunk teleport timers (called at 1hz from the game loop).
    pub fn tick_teleportation(&mut self) -> Vec<GameEvent> {
        let mut events = Vec::new();
        let (t_min, t_max) = self.config.teleport_interval;

        let chunk_positions: Vec<ChunkPos> = self.chunks.keys().copied().collect();
        for pos in chunk_positions {
            let should_teleport = {
                let chunk = match self.chunks.get_mut(&pos) {
                    Some(c) => c,
                    None => continue,
                };
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
                let old_pos = pos;
                let new_offset_x = self.rng.gen_range(-30..=30i32);
                let new_offset_z = self.rng.gen_range(-30..=30i32);
                let new_seed = self.rng.gen::<u64>();

                // Regenerate in-place with a new random seed.
                if let Some(chunk) = self.chunks.get_mut(&pos) {
                    chunk.seed = new_seed;
                    chunk.teleport_timer = self.rng.gen_range(t_min..t_max);
                    // Regenerate entities and items (old ones are lost).
                    let gen = generator::generate_chunk(new_seed, pos);
                    chunk.entities = gen.entities;
                    chunk.items = gen.items;
                    chunk.template_id = gen.template_id;
                    chunk.rotation = gen.rotation;
                    chunk.mirrored = gen.mirrored;
                    chunk.has_workbench = gen.has_workbench;
                    self.revision = self.revision.wrapping_add(1);

                    info!("Chunk {:?} teleported (new seed {})", old_pos, new_seed);
                    events.push(GameEvent {
                        event_type: "chunk_teleported".into(),
                        data: serde_json::json!({
                            "chunk_pos": [old_pos.0, old_pos.1],
                            "new_offset": [new_offset_x, new_offset_z],
                        }),
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

        let chunk_positions: Vec<ChunkPos> = self.chunks.keys().copied().collect();
        for pos in chunk_positions {
            let chunk = match self.chunks.get_mut(&pos) {
                Some(c) if c.is_active() => c,
                _ => continue,
            };

            let mut despawned_ids = Vec::new();
            for entity in chunk.entities.iter_mut() {
                let event = entity.update(dt, player_pos, player_id, pos, &mut self.rng);
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

            // Remove despawned entities; queue them for respawn.
            for id in &despawned_ids {
                let timer = self.rng.gen_range(120.0..300.0);
                self.respawn_queue.push((*id, pos, timer));
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
                if let Some(chunk) = self.chunks.get_mut(&chunk_pos) {
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
        let player_chunk = world_to_chunk(player_pos);
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
        let pos: ChunkPos = (data.pos[0], data.pos[1]);
        let chunk = self
            .chunks
            .entry(pos)
            .or_insert_with(|| generator::generate_chunk(self.seed, pos));

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
    pub fn apply_remote_teleport(&mut self, old_pos: [i32; 2], new_seed: u64) {
        let pos: ChunkPos = (old_pos[0], old_pos[1]);
        if let Some(chunk) = self.chunks.get_mut(&pos) {
            chunk.seed = new_seed;
            let gen = generator::generate_chunk(new_seed, pos);
            chunk.entities = gen.entities;
            chunk.items = gen.items;
            chunk.template_id = gen.template_id;
            chunk.rotation = gen.rotation;
            chunk.mirrored = gen.mirrored;
            chunk.has_workbench = gen.has_workbench;
            chunk.layout = gen.layout;
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

    /// Set a chunk as anchored (from an AnchorBroadcast).
    pub fn set_chunk_anchored(&mut self, chunk_pos: [i32; 2]) {
        let pos: ChunkPos = (chunk_pos[0], chunk_pos[1]);
        if let Some(chunk) = self.chunks.get_mut(&pos) {
            chunk.state = ChunkState::Active {
                stabilized: true,
                anchored: true,
            };
        }
    }

    /// Set a chunk as stabilized (from a StabilizerBroadcast).
    pub fn set_chunk_stabilized(&mut self, chunk_pos: [i32; 2]) {
        let pos: ChunkPos = (chunk_pos[0], chunk_pos[1]);
        if let Some(chunk) = self.chunks.get_mut(&pos) {
            if let ChunkState::Active { anchored, .. } = chunk.state {
                chunk.state = ChunkState::Active {
                    stabilized: true,
                    anchored,
                };
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

    /// Build visible chunk views for the WorldState IPC message.
    pub fn visible_chunk_views(&self) -> Vec<ChunkView> {
        self.chunks
            .values()
            .map(|c| ChunkView {
                pos: [c.pos.0, c.pos.1],
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
            })
            .collect()
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
mod tests {
    use super::*;
    use std::collections::{HashSet, VecDeque};

    #[test]
    fn ownership_loads_chunks_around_player() {
        let mut world = World::new(42);
        let player_pos = Vec3::new(25.0, 0.0, 25.0); // chunk (0,0)
        world.update_ownership(player_pos, 1);
        let radius = world.config.ownership_radius; // 5
        let expected = ((radius * 2 + 1) * (radius * 2 + 1)) as usize; // 11x11 = 121
        assert_eq!(world.chunks.len(), expected);
        assert!(world.chunks.contains_key(&(0, 0)));
        assert!(world.chunks.contains_key(&(5, 5)));
        assert!(world.chunks.contains_key(&(-5, -5)));
    }

    #[test]
    fn ownership_unloads_distant_chunks() {
        let mut world = World::new(42);
        world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
        assert!(world.chunks.contains_key(&(0, 0)));
        // Move far away.
        world.update_ownership(Vec3::new(1000.0, 0.0, 1000.0), 1);
        assert!(!world.chunks.contains_key(&(0, 0)));
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
    fn generated_world_has_connected_initial_structure() {
        let mut world = World::new(42);
        world.generate_initial_structures(1);

        assert!(!world.chunks.is_empty());
        assert!(world.chunks.contains_key(&(0, 0)));

        let start = (0, 0);
        let mut visited = HashSet::new();
        let mut queue = VecDeque::from([start]);
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
                if world.chunks.contains_key(&next) && !visited.contains(&next) {
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
            .any(|c| c.template_id == generator::TEMPLATE_STORAGE_ROOM));
        assert!(sync_chunks
            .iter()
            .any(|c| c.template_id == generator::TEMPLATE_INTERSECTION));
        assert!(sync_chunks
            .iter()
            .all(|c| !c.items.is_empty() || c.template_id == generator::TEMPLATE_SAFE_ROOM));
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
        if let Some(chunk) = world.chunks.get_mut(&(0, 0)) {
            chunk.teleport_timer = 0.5; // Will expire on next 1hz tick.
        }
        let old_seed = world.chunks[&(0, 0)].seed;
        let events = world.tick_teleportation();
        // The chunk at (0,0) should have teleported.
        let new_seed = world.chunks[&(0, 0)].seed;
        assert_ne!(
            old_seed, new_seed,
            "chunk seed should change after teleport"
        );
        assert!(!events.is_empty(), "should emit teleport event");
        assert_eq!(events[0].event_type, "chunk_teleported");
    }

    #[test]
    fn entity_tick_produces_damage() {
        let mut world = World::new(42);
        world.update_ownership(Vec3::new(25.0, 0.0, 25.0), 1);
        // Place an entity right on top of the player in aggro state.
        let chunk = world.chunks.get_mut(&(0, 0)).unwrap();
        chunk.entities.clear();
        let mut e =
            entity::Entity::new(9999, entity::EntityType::Lurker, Vec3::new(25.0, 0.0, 25.0));
        e.state = entity::EntityState::Aggro {
            target: 1,
            attack_cooldown: 0.0,
        };
        chunk.entities.push(e);
        let (damage, events) = world.tick_entities(0.1, Vec3::new(25.5, 0.0, 25.0), 1);
        assert!(damage > 0.0, "entity should deal damage");
        assert!(!events.is_empty());
    }
}
