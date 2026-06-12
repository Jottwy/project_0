//! Level 0 — entity and item content spawning (MIG-5e).
//!
//! Moved verbatim from `generator.rs`: the five content-spawn helpers and
//! the per-structure-type item/entity match block, now wrapped in
//! `apply_structure_content`. No behaviour or RNG-stream changes.

use rand::rngs::StdRng;
use rand::Rng;

use crate::player::inventory::Item;
use crate::utils::{ChunkPos, Vec3, CHUNK_SIZE};
use crate::world::architecture::chunk_generator::{stable_entity_id, stable_item_id};
use crate::world::chunk::{Chunk, DroppedItem};
use crate::world::entity::{Entity, EntityType};
use crate::world::levels::level_0::structure::{StructureType, StructureV0};

// --- Position helpers ---

fn local_pos_in_chunk(pos: ChunkPos, local_x: f32, local_z: f32) -> Vec3 {
    Vec3::new(
        pos.0 as f32 * CHUNK_SIZE + local_x,
        0.0,
        pos.1 as f32 * CHUNK_SIZE + local_z,
    )
}

fn random_pos_in_chunk(pos: ChunkPos, rng: &mut StdRng) -> Vec3 {
    let base_x = pos.0 as f32 * CHUNK_SIZE;
    let base_z = pos.1 as f32 * CHUNK_SIZE;

    Vec3::new(
        rng.gen_range(base_x + 2.0..base_x + CHUNK_SIZE - 2.0),
        0.0,
        rng.gen_range(base_z + 2.0..base_z + CHUNK_SIZE - 2.0),
    )
}

// --- Dropped-item construction ---

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

// --- Procedural content spawning ---

pub(crate) fn spawn_resources(
    world_seed: u64,
    pos: ChunkPos,
    rng: &mut StdRng,
) -> Vec<DroppedItem> {
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

pub(crate) fn spawn_entities(world_seed: u64, pos: ChunkPos, rng: &mut StdRng) -> Vec<Entity> {
    let count = rng.gen_range(3..=5);
    let mut entities = Vec::with_capacity(count);
    for index in 0..count {
        let etype = match rng.gen_range(0..10) {
            0..=4 => EntityType::Lurker,
            5..=7 => EntityType::Crawler,
            _ => EntityType::Shadow,
        };
        entities.push(Entity::new(
            stable_entity_id(world_seed, pos, index as u32),
            etype,
            random_pos_in_chunk(pos, rng),
        ));
    }
    entities
}

// --- Per-structure content application ---

/// Applies per-structure-type item and entity content to a structure chunk.
/// Moved verbatim from `generate_structure_chunk` in `generator.rs`.
pub(crate) fn apply_structure_content(
    world_seed: u64,
    pos: ChunkPos,
    chunk: &mut Chunk,
    structure: &StructureV0,
) {
    let depth = (pos.0.abs() + pos.1.abs()) as f32;

    match structure.structure_type {
        // Spawn / starter rooms
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

        // Corridors & junctions
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

        // Crafting & safe rooms (set has_workbench)
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

        // Themed rooms & hazard zones
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

        // Multilayer / vertical structures (cleared)
        StructureType::StackedCorridor
        | StructureType::LowerServiceBranch
        | StructureType::UpperOfficeBranch
        | StructureType::AtriumVoidRoom
        | StructureType::DeepPrecipicePlaceholder
        | StructureType::GiantPillarHall => {
            chunk.entities.clear();
            chunk.items.clear();
        }

        // Points of interest
        StructureType::PoiLandmark => {
            // Memorable landmark room: no entities, one battery as a remnant.
            chunk.entities.clear();
            chunk.items = vec![dropped_item(
                world_seed,
                pos,
                0,
                Item::Battery,
                1,
                22.0,
                32.0,
            )];
        }
        StructureType::PoiAnomalyCluster => {
            // Disorienting cluster: shadowy presence at depth, sparse loot.
            if depth >= 5.0 {
                chunk.entities = vec![Entity::new(
                    stable_entity_id(world_seed, pos, 0),
                    EntityType::Shadow,
                    local_pos_in_chunk(pos, 28.0, 28.0),
                )];
            } else {
                chunk.entities.clear();
            }
            chunk.items.truncate(1);
        }
        StructureType::PoiDangerPocket => {
            // Danger pocket: hostile entity + hazard loot (deep only).
            chunk.entities = vec![Entity::new(
                stable_entity_id(world_seed, pos, 0),
                EntityType::Crawler,
                local_pos_in_chunk(pos, 20.0, 20.0),
            )];
            chunk.items = vec![dropped_item(
                world_seed,
                pos,
                0,
                Item::Medicine,
                1,
                38.0,
                38.0,
            )];
        }
        StructureType::PoiSafePocket => {
            // Safe pocket: clear of entities, has food/water.
            chunk.entities.clear();
            chunk.items = vec![
                dropped_item(world_seed, pos, 0, Item::Food, 1, 18.0, 25.0),
                dropped_item(world_seed, pos, 1, Item::Water, 1, 34.0, 25.0),
            ];
        }
    }
}
