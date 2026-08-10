//! Per-chunk deterministic primitives: seeding, layout assembly, and the stable ID
//! stream that names everything a generated chunk contains.
//!
//! WHY the IDs are hashes and not counters: chunk content is never handed out as the
//! single copy of a truth. Every peer — and the backend itself on a restart — rebuilds
//! the same chunk from `world_seed` + `ChunkPos` (+ layer) alone, and the entity / item /
//! volume / structure IDs have to come out identical with zero coordination, because
//! nothing in the system ever reconciles two divergent numberings. A counter would depend
//! on generation ORDER (which chunk was built first, how many were built before); a hash
//! of `(seed, position, index)` does not, so a chunk can be regenerated in isolation.
//!
//! WHAT BREAKS if a constant, a salt or a mixing step below is "improved": the whole world
//! is silently relabelled. Same seed, different IDs. The `level0_golden_slice` fixtures
//! fail on purpose (that is exactly what they are for), the IDs already sent to Unity stop
//! matching the ones regenerated afterwards, and `structure_id` in particular is not just
//! an ID — it becomes `chunk.layout.macro_id` and the `SpatialNodeId` of the Level 0
//! RegionGraph, so renumbering it rewires the graph too. Treat this module like a wire
//! format: additive only, never re-tuned.
//!
//! `next_entity_id` / `next_entity_id_pub` is the one deliberate exception, and it is
//! marked as such below.

use rand::rngs::StdRng;
use rand::Rng;

use crate::utils::ChunkPos;
use crate::world::architecture::layout_grammars::{
    generate_layout_from_template, open_boundary_gaps, TEMPLATE_ARCH_ROOM, TEMPLATE_BLACKOUT_ZONE,
    TEMPLATE_CLEANING_AREA, TEMPLATE_HUMID_ZONE, TEMPLATE_MANILA_ROOM, TEMPLATE_OPEN_HALL,
    TEMPLATE_PILLAR_ROOM, TEMPLATE_PIT_ROOM_PLACEHOLDER, TEMPLATE_POI_ANOMALY,
    TEMPLATE_POI_DANGER_POCKET, TEMPLATE_POI_LANDMARK, TEMPLATE_POI_SAFE_POCKET,
    TEMPLATE_RED_ROOM_WARNING, TEMPLATE_STORAGE_ROOM,
};
use crate::world::architecture::surface_builder::perimeter_openings;
use crate::world::chunk::ChunkLayer;
use crate::world::chunk::{
    ChunkLayoutV1, CEILING_DAMAGED, CEILING_LOW_SERVICE, CEILING_NORMAL, CEILING_TALL_HALL,
    CELL_BLOCKED, CELL_WALL, FLOOR_FLAT, FLOOR_PIT_PLACEHOLDER, FLOOR_RAISED, FLOOR_RAMP_EAST_WEST,
    FLOOR_RAMP_NORTH_SOUTH, FLOOR_STAIRS_EAST_WEST, FLOOR_STAIRS_NORTH_SOUTH, FLOOR_SUNKEN,
    LAYOUT_CELL_SIZE, LIGHT_BLACKOUT, LIGHT_DIM, LIGHT_NORMAL, LIGHT_RED, LIGHT_WARM,
};

use crate::world::chunk::LAYOUT_GRID_SIZE;

/// The ONE non-deterministic ID source in this module: a process-local counter.
///
/// It exists for entities created at RUNTIME (`world/mod.rs`), which by definition are not
/// derivable from the world seed — there is no `(pos, index)` to hash. The trade-off is
/// accepted deliberately: such an ID is only meaningful inside the session that minted it
/// and is NOT reproducible by another peer regenerating the same world.
///
/// ADR-063: the raw counter is process-global on purpose (today only the host ever mints —
/// `tick_respawns` is gated `if net.is_host`, `game_loop.rs`). `partition_runtime_id` stamps
/// the minting peer into the top 16 bits so a FUTURE decentralised minter (a joiner spawning
/// something at runtime) can never collide with the host's ids, without needing a counter
/// per peer.
static NEXT_ENTITY_ID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

/// ADR-063: partitions a runtime-minted id by `peer_id` so two backends minting independently
/// never produce the same id. **16/16 split, not the 8/24 the ADR's first draft proposed** —
/// `PeerId` is `u16` and `allocate_peer_id` (`network/handlers.rs`) never resets across a
/// host's lifetime, so an 8-bit top half truncates: `peer_id=1` and `peer_id=257` share the
/// same low byte and would collide after `<<24`. With `<<16` no `PeerId` value truncates.
///
/// Counter starts at 0, not 1: `peer_id` is never 0 in practice (`allocate_peer_id` never
/// assigns it; the host is 1 by convention), so `(peer_id << 16) | 0` is already non-zero —
/// no `.max(1)` offset needed, unlike the stable IDs below. The low 16 bits wrap naturally via
/// `as u16` regardless of how far the underlying `AtomicU32` has counted — 65536 ids per peer
/// per process, generous while minting stays host-only; if that ever feels tight, reclaim ids
/// on pickup/despawn or widen the type in a future ADR, don't re-shrink this split.
pub(crate) fn partition_runtime_id(peer_id: u16, counter: u32) -> u32 {
    ((peer_id as u32) << 16) | (counter as u16 as u32)
}

fn next_entity_id(peer_id: u16) -> u32 {
    let counter = NEXT_ENTITY_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    partition_runtime_id(peer_id, counter)
}

/// Public face of the runtime counter (see `NEXT_ENTITY_ID`). `peer_id` is who is minting —
/// see `partition_runtime_id` for why it matters.
pub fn next_entity_id_pub(peer_id: u16) -> u32 {
    next_entity_id(peer_id)
}

/// Root seed of a chunk: a pure hash of `world_seed` and the chunk's `(x, z)`.
///
/// Everything deterministic inside a chunk descends from this value, which is why it must
/// stay a FUNCTION OF POSITION ONLY — never of generation order, of a global counter, or of
/// how many chunks were produced before it. That property is what lets any peer regenerate
/// chunk `(x, z)` on its own and land on the same layout, the same RNG stream and therefore
/// the same contents.
pub fn chunk_seed(world_seed: u64, pos: ChunkPos) -> u64 {
    let mut h = world_seed ^ 0x9E37_79B9_7F4A_7C15;
    h = h.wrapping_add((pos.0 as u64).wrapping_mul(0xFF51_AFD7_ED55_8CCD));
    h ^= h >> 33;
    h = h.wrapping_add((pos.1 as u64).wrapping_mul(0xC4CE_B9FE_1A85_EC53));
    h ^= h >> 29;
    h
}

/// Per-layer variant of `chunk_seed`.
///
/// Layer 0 returns `chunk_seed` UNCHANGED, on purpose: the flat single-layer world predates
/// verticality, so re-mixing layer 0 would move every existing seed's output and invalidate
/// the golden fixtures for nothing. Non-zero layers get a salted, re-mixed value so a chunk
/// stacked above or below `(x, z)` does not inherit the RNG stream of `(x, z)` itself and
/// come out as its clone.
pub fn chunk_seed_layer(world_seed: u64, pos: ChunkPos, layer: ChunkLayer) -> u64 {
    if layer == 0 {
        return chunk_seed(world_seed, pos);
    }

    let mut h = chunk_seed(world_seed, pos);
    h ^= ((layer as i64 as u64).wrapping_mul(0xD6E8_FD9D_AA28_2219)).rotate_left(17);
    h ^= h >> 31;
    h
}

pub fn build_chunk_layout(template_id: u8, rotation: u16) -> ChunkLayoutV1 {
    let size = LAYOUT_GRID_SIZE as usize;
    // Delegate base layout grammar to architecture module.
    let mut layout = generate_layout_from_template(template_id, rotation);

    open_boundary_gaps(&mut layout);
    layout.edge_openings = perimeter_openings(&layout);

    match template_id {
        TEMPLATE_OPEN_HALL | TEMPLATE_PILLAR_ROOM => {
            layout.ceiling_profile = CEILING_TALL_HALL;
        }
        TEMPLATE_STORAGE_ROOM => {
            layout.ceiling_profile = CEILING_LOW_SERVICE;
            layout.light_profile = LIGHT_DIM;
        }
        TEMPLATE_BLACKOUT_ZONE => {
            layout.light_profile = LIGHT_BLACKOUT;
            layout.anomaly_flags |= 1;
            layout.ceiling_profile = CEILING_DAMAGED;
        }
        TEMPLATE_HUMID_ZONE => {
            layout.light_profile = LIGHT_DIM;
            layout.floor_profile = FLOOR_SUNKEN;
            layout.vertical_flags |= 1;
        }
        TEMPLATE_MANILA_ROOM => {
            layout.light_profile = LIGHT_WARM;
            layout.floor_profile = FLOOR_RAISED;
            layout.vertical_flags |= 1 << 1;
        }
        TEMPLATE_RED_ROOM_WARNING => {
            layout.light_profile = LIGHT_RED;
            layout.anomaly_flags |= 1 << 1;
        }
        TEMPLATE_PIT_ROOM_PLACEHOLDER => {
            layout.floor_profile = FLOOR_PIT_PLACEHOLDER;
            layout.anomaly_flags |= 1 << 2;
            layout.vertical_flags |= 1 << 2;
        }
        TEMPLATE_ARCH_ROOM => {
            layout.floor_profile = if rotation.is_multiple_of(180) {
                FLOOR_RAMP_NORTH_SOUTH
            } else {
                FLOOR_RAMP_EAST_WEST
            };
            layout.vertical_flags |= 1 << 3;
        }
        TEMPLATE_CLEANING_AREA => {
            layout.floor_profile = if rotation.is_multiple_of(180) {
                FLOOR_STAIRS_NORTH_SOUTH
            } else {
                FLOOR_STAIRS_EAST_WEST
            };
            layout.vertical_flags |= 1 << 4;
            layout.light_profile = LIGHT_DIM;
            layout.ceiling_profile = CEILING_LOW_SERVICE;
        }
        TEMPLATE_POI_LANDMARK => {
            layout.ceiling_profile = CEILING_TALL_HALL;
            layout.light_profile = LIGHT_WARM;
            layout.floor_profile = FLOOR_FLAT;
        }
        TEMPLATE_POI_ANOMALY => {
            layout.light_profile = LIGHT_DIM;
            layout.anomaly_flags |= 1 << 3;
            layout.floor_profile = FLOOR_FLAT;
        }
        TEMPLATE_POI_DANGER_POCKET => {
            layout.light_profile = LIGHT_DIM;
            layout.ceiling_profile = CEILING_DAMAGED;
            layout.anomaly_flags |= 1;
            layout.floor_profile = FLOOR_FLAT;
        }
        TEMPLATE_POI_SAFE_POCKET => {
            layout.light_profile = LIGHT_WARM;
            layout.floor_profile = FLOOR_FLAT;
        }
        _ => {
            layout.floor_profile = FLOOR_FLAT;
            layout.ceiling_profile = CEILING_NORMAL;
            layout.light_profile = LIGHT_NORMAL;
        }
    }

    if layout.cells.len() != size * size {
        layout.cells.resize(size * size, CELL_BLOCKED | CELL_WALL);
    }
    layout.cell_size = LAYOUT_CELL_SIZE;
    layout.grid_size = LAYOUT_GRID_SIZE;
    layout
}

// ─── MIG-5a: deterministic ID helpers (moved from generator.rs) ───

/// Shared core of the stable-ID family: mixes `world_seed ^ salt` with the chunk position
/// and a per-object `index` into a non-zero, positive `u32`.
///
/// The `salt` is the only thing keeping the families apart — without it the entity and the
/// item with the same `index` in the same chunk would get the SAME id. Each caller below
/// therefore owns a frozen salt constant; the values are arbitrary, but changing one
/// renumbers that entire family across every seed.
///
/// The 31-bit mask keeps the result inside positive `i32` range (Unity reads these as C#
/// `int`), and `.max(1)` keeps 0 free as the absent/empty sentinel used on the wire.
fn stable_u32(world_seed: u64, pos: ChunkPos, salt: u64, index: u32) -> u32 {
    let mut h = chunk_seed(world_seed ^ salt, pos);
    h ^= (index as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15);
    h ^= h >> 32;
    ((h & 0x7FFF_FFFF) as u32).max(1)
}

/// Deterministic ID of an entity generated inside chunk `pos` (`levels/level_0/content.rs`).
///
/// `index` is the entity's ordinal WITHIN its chunk, so the ID holds as long as the
/// generator emits the same entities in the same order for that chunk — that ordering is
/// part of the contract, not an implementation detail. Two peers generating the chunk
/// independently agree on the ID without ever exchanging it, which is what makes an entity
/// referable across the IPC/P2P boundary.
pub(crate) fn stable_entity_id(world_seed: u64, pos: ChunkPos, index: u32) -> u32 {
    stable_u32(world_seed, pos, 0xE17E_0001, index)
}

/// Deterministic ID of an item generated inside chunk `pos` (`levels/level_0/content.rs`).
///
/// Same reasoning as `stable_entity_id`, with a DIFFERENT salt so entity #3 and item #3 of
/// the same chunk cannot collide. `index` is the item's ordinal within the chunk, so
/// inserting a spawn in the middle of that sequence renumbers every item after it.
pub(crate) fn stable_item_id(world_seed: u64, pos: ChunkPos, index: u32) -> u32 {
    stable_u32(world_seed, pos, 0x17E0_0002, index)
}

/// Deterministic ID of an inter-layer volume (`levels/level_0/v30a_showcase.rs`).
///
/// `layer` enters the salt because volumes of different layers live at the SAME `(x, z)`:
/// position + index alone would hand the volume above and the volume below the same ID.
/// This is also why that showcase module can advertise itself as RNG-free — its IDs come
/// from here (pure hash), so it consumes nothing from the chunk's RNG stream and cannot
/// shift the draws made after it.
pub(crate) fn stable_volume_id(
    world_seed: u64,
    pos: ChunkPos,
    layer: crate::world::chunk::ChunkLayer,
    index: u32,
) -> u32 {
    let layer_salt = (layer as i64 as u64)
        .wrapping_mul(0xA30A_2001_5EED_0001)
        .rotate_left(11);
    stable_u32(world_seed, pos, 0xA30A_2002 ^ layer_salt, index)
}

/// Deterministic ID of a Level 0 structure (`levels/level_0/builder.rs`, `visfix_7778.rs`).
///
/// A structure is world-level, not per-chunk, so there is no real position to hash: the
/// `(index, -index)` pair is a synthetic coordinate whose only job is to spread consecutive
/// ordinals apart in the mix. The value travels much further than generation — it is copied
/// into `chunk.layout.macro_id` and becomes the `SpatialNodeId` of the RegionGraph node, so
/// changing this function renumbers the graph and every macro-id-based lookup with it.
pub(crate) fn structure_id(world_seed: u64, index: u32) -> u32 {
    stable_u32(
        world_seed,
        (index as i32, -(index as i32)),
        0x57A7_C700,
        index,
    )
}

/// In-place Fisher-Yates shuffle driven by the caller's `StdRng`.
///
/// The DRAW PATTERN, not just the result, is part of the world's identity: this consumes
/// exactly one `gen_range(0..=i)` per step, back to front, from the caller's own RNG
/// (`Level0Builder::rng`). Swapping in `SliceRandom::shuffle` or any other implementation
/// would consume that stream differently and shift EVERY subsequent draw made by the same
/// `rng`, changing layouts for seeds this change never meant to touch.
pub(crate) fn fisher_yates(slice: &mut [usize], rng: &mut StdRng) {
    for i in (1..slice.len()).rev() {
        let j = rng.gen_range(0..=i);
        slice.swap(i, j);
    }
}

#[cfg(test)]
mod partition_tests {
    use super::partition_runtime_id;

    #[test]
    fn different_peers_never_collide_regardless_of_counter() {
        for counter_a in [0u32, 1, 65535, 65536, 131071] {
            for counter_b in [0u32, 1, 65535, 65536, 131071] {
                assert_ne!(
                    partition_runtime_id(1, counter_a),
                    partition_runtime_id(2, counter_b),
                    "distintos peer_id nunca deben colisionar, sea cual sea el contador"
                );
            }
        }
    }

    #[test]
    fn a_wide_peer_id_does_not_truncate_like_the_rejected_24_bit_split_did() {
        // La formula del borrador (<<24) hacia colisionar peer_id=1 con peer_id=257 (0x101):
        // el byte alto de 257 se perdia fuera del u32. Con <<16 eso no puede pasar.
        assert_ne!(
            partition_runtime_id(1, 0),
            partition_runtime_id(257, 0),
            "16/16 no trunca ningun PeerId, a diferencia del split 8/24 rechazado"
        );
    }

    #[test]
    fn counter_zero_with_a_real_peer_id_is_never_the_sentinel() {
        assert_ne!(
            partition_runtime_id(1, 0),
            0,
            "peer_id nunca es 0 en la practica, asi que el contador puede arrancar en 0"
        );
    }
}
