//! Deterministic, seed-based chunk generation.
//! See ARCHITECTURE_V1.md §7.1 and CLAUDE_CODE_INSTRUCTIONS.md Task 1.4.

use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

use crate::player::inventory::Item;
use crate::utils::{ChunkPos, Vec3, CHUNK_SIZE};
use crate::world::chunk::{Chunk, ChunkState, DroppedItem};
use crate::world::entity::{Entity, EntityType};

pub const TEMPLATE_COUNT: u8 = 10;

/// Global entity id counter — not persisted, just needs to be unique per session.
static NEXT_ENTITY_ID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(1);
static NEXT_ITEM_ID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(1);

fn next_entity_id() -> u32 {
    NEXT_ENTITY_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

pub fn next_entity_id_pub() -> u32 {
    next_entity_id()
}

fn next_item_id() -> u32 {
    NEXT_ITEM_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

pub fn chunk_seed(world_seed: u64, pos: ChunkPos) -> u64 {
    let mut h = world_seed ^ 0x9E37_79B9_7F4A_7C15;
    h = h.wrapping_add((pos.0 as u64).wrapping_mul(0xFF51_AFD7_ED55_8CCD));
    h ^= h >> 33;
    h = h.wrapping_add((pos.1 as u64).wrapping_mul(0xC4CE_B9FE_1A85_EC53));
    h ^= h >> 29;
    h
}

/// Generate a chunk deterministically from the world seed and grid position.
pub fn generate_chunk(world_seed: u64, pos: ChunkPos) -> Chunk {
    let seed = chunk_seed(world_seed, pos);
    let mut rng = StdRng::seed_from_u64(seed);

    let template_id = rng.gen_range(0..TEMPLATE_COUNT);
    let rotation = (rng.gen_range(0..4) * 90) as u16;
    let mirrored = rng.gen_bool(0.5);
    let has_workbench = rng.gen_bool(0.2);
    let teleport_timer = rng.gen_range(120.0..600.0);

    let entities = spawn_entities(pos, &mut rng);
    let items = spawn_resources(pos, &mut rng);

    Chunk {
        pos,
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
    }
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

fn spawn_entities(pos: ChunkPos, rng: &mut StdRng) -> Vec<Entity> {
    let count = rng.gen_range(3..=5);
    let mut entities = Vec::with_capacity(count);
    for _ in 0..count {
        let etype = match rng.gen_range(0..10) {
            0..=4 => EntityType::Lurker,   // 50%
            5..=7 => EntityType::Crawler,  // 30%
            _ => EntityType::Shadow,       // 20%
        };
        let spawn_pos = random_pos_in_chunk(pos, rng);
        entities.push(Entity::new(next_entity_id(), etype, spawn_pos));
    }
    entities
}

fn spawn_resources(pos: ChunkPos, rng: &mut StdRng) -> Vec<DroppedItem> {
    let mut items = Vec::new();

    let place = |items: &mut Vec<DroppedItem>, item: Item, count: u16, pos: Vec3| {
        items.push(DroppedItem {
            id: next_item_id(),
            item,
            quantity: count,
            position: pos,
        });
    };

    // Metal: 1-5
    for _ in 0..rng.gen_range(1..=5) {
        place(&mut items, Item::Metal, 1, random_pos_in_chunk(pos, rng));
    }
    // Circuits: 1-3
    for _ in 0..rng.gen_range(1..=3) {
        place(&mut items, Item::Circuit, 1, random_pos_in_chunk(pos, rng));
    }
    // Batteries: 1-2
    for _ in 0..rng.gen_range(1..=2) {
        place(&mut items, Item::Battery, 1, random_pos_in_chunk(pos, rng));
    }
    // Food: 1-3
    for _ in 0..rng.gen_range(1..=3) {
        place(&mut items, Item::Food, 1, random_pos_in_chunk(pos, rng));
    }
    // Water: 1-3
    for _ in 0..rng.gen_range(1..=3) {
        place(&mut items, Item::Water, 1, random_pos_in_chunk(pos, rng));
    }

    items
}

#[cfg(test)]
mod tests {
    use super::*;

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
        // Should have at minimum: 1 metal + 1 circuit + 1 battery + 1 food + 1 water = 5
        assert!(c.items.len() >= 5);
    }

    #[test]
    fn different_positions_give_different_chunks() {
        let c1 = generate_chunk(42, (0, 0));
        let c2 = generate_chunk(42, (1, 0));
        // Entity ids are globally unique, so they must differ.
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
            assert!(e.position.x >= min_x && e.position.x <= max_x,
                "entity x {} out of [{}, {}]", e.position.x, min_x, max_x);
            assert!(e.position.z >= min_z && e.position.z <= max_z,
                "entity z {} out of [{}, {}]", e.position.z, min_z, max_z);
        }
    }
}
