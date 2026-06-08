//! World domain: chunks, procedural generation, entities.

pub mod chunk;
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

    /// Load all chunks within the ownership radius of the player and unload distant ones.
    pub fn update_ownership(&mut self, player_pos: Vec3, player_id: PeerId) {
        let player_chunk = world_to_chunk(player_pos);
        let radius = self.config.ownership_radius;
        let needed: HashSet<ChunkPos> = chunks_in_radius(player_chunk, radius)
            .into_iter()
            .collect();

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
                    chunk
                        .entities
                        .push(entity::Entity::new(generator::next_entity_id_pub(), etype, spawn_pos));
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
            entities_visible = chunk
                .entities
                .iter()
                .filter(|e| e.is_alive())
                .count() as u32;
            chunk_stabilized = matches!(
                chunk.state,
                ChunkState::Active {
                    stabilized: true,
                    ..
                } | ChunkState::Active {
                    anchored: true,
                    ..
                }
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
        chunks: &[crate::network::protocol::ChunkSyncData],
        local_id: crate::network::PeerId,
    ) {
        for data in chunks {
            self.apply_chunk_sync(data, local_id);
        }
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
        let chunk = self.chunks.entry(pos).or_insert_with(|| {
            generator::generate_chunk(self.seed, pos)
        });

        chunk.seed = data.seed;
        chunk.template_id = data.template_id;
        chunk.rotation = data.rotation;
        chunk.mirrored = data.mirrored;
        chunk.has_workbench = data.has_workbench;
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
            chunk.teleport_timer = self.rng.gen_range(
                self.config.teleport_interval.0..self.config.teleport_interval.1,
            );
        }
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
        assert_ne!(old_seed, new_seed, "chunk seed should change after teleport");
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
        let mut e = entity::Entity::new(9999, entity::EntityType::Lurker, Vec3::new(25.0, 0.0, 25.0));
        e.state = entity::EntityState::Aggro { target: 1, attack_cooldown: 0.0 };
        chunk.entities.push(e);
        let (damage, events) = world.tick_entities(0.1, Vec3::new(25.5, 0.0, 25.0), 1);
        assert!(damage > 0.0, "entity should deal damage");
        assert!(!events.is_empty());
    }
}
