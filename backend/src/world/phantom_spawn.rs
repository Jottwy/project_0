//! ADR-043 — where the world's robapieles live.
//!
//! A PURE, LAZY draw: `(world_seed, block, layer) → Option<position>`. There is no global roster of
//! phantoms and no persisted state, because the world is procedurally infinite (`generator.rs`
//! "infinite on-demand expansion path") — "every phantom in the world" is not a finite set, so it
//! cannot be a list. Asking about one block costs a hash, which is what lets the host ask about
//! only the blocks near a player, every second, forever.
//!
//! Determinism is the point: two players on the same seed must find the same creatures in the same
//! places, and a phantom that despawns because everyone walked away must come back to its anchor
//! rather than somewhere new. That is also why nothing here is persisted — it is re-derived.
//!
//! The CLIENT never replicates this. Phantoms are host-authoritative and reach every player as
//! relayed poses (ADR-015/016); a second draw in Unity would be a second world.

use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

use crate::world::architecture::chunk_generator::chunk_seed_layer;
use crate::world::chunk::ChunkLayer;
use crate::world::grid_gen::{grid_floor_y, CELL_SIZE_M, CHUNK_CELLS};

/// Chunks per side of a spawn block. 4 × 50 m = **200 m**, so at density 1.0 the world holds one
/// robapieles per 200 × 200 m — with `PHANTOM_DETECT_RADIUS` at 15 m, that is running into one
/// every several minutes of exploration rather than every corner.
pub const BLOCK_CHUNKS: i32 = 4;

/// World-units per chunk side. Mirrors `grid_gen::collision`'s private `CHUNK_SIZE_M`; kept as its
/// own const rather than made public there because this module reasons in BLOCKS and would
/// otherwise import a constant it does not conceptually own.
const CHUNK_SIZE_M: f32 = CELL_SIZE_M * CHUNK_CELLS as f32;

/// World-units per block side (200 m).
pub const BLOCK_SIZE_M: f32 = CHUNK_SIZE_M * BLOCK_CHUNKS as f32;

/// Probability a given block holds a robapieles, per layer. Index = layer.
///
/// ADR-043 D-TABLA: layers 1-3 start at ZERO on purpose, and this is the multiplier doing its job,
/// not a decision deferred. They are not reachable by playing — `inter_layer_up`/`inter_layer_down`
/// are declared config-only and unwired (`grid_gen::layer_rules`), `num_stairs` only stamps
/// `CellType::Stair` onto cells that were already walkable in the same plane, and the client lays a
/// floor slab with a collider on every tile of every layer. Seeding creatures there would be paying
/// CPU for something nobody can reach or see. Turn them on the day the vertical traversal lands;
/// that is a constant change, not an architecture change.
pub const PHANTOM_LAYER_DENSITY: [f32; 4] = [1.0, 0.0, 0.0, 0.0];

/// Salt that separates this draw from every other consumer of `chunk_seed_layer`.
///
/// Without it, block (0,0) and chunk (0,0) would hash to the SAME value, so whether a phantom lives
/// somewhere would correlate with which layout template that coordinate rolled — a hidden coupling
/// that would only ever show up as "the creatures are always in the pillar halls". Same trick
/// `stable_u32` uses for its own id families.
const PHANTOM_DRAW_SALT: u64 = 0xB0DA_5EED_1701_C0DE;

/// The spawn block containing a world XZ position.
pub fn block_of(x: f32, z: f32) -> (i32, i32) {
    (
        (x / BLOCK_SIZE_M).floor() as i32,
        (z / BLOCK_SIZE_M).floor() as i32,
    )
}

/// Which robapieles does `block` hold on `layer`, and where? Appended to `out` (which is cleared).
///
/// `density_scale` multiplies the per-layer figure, which is an **expected count per block**, not a
/// probability: at 1.0 the shipped world holds one per 200 m block, and at 8.0 it holds eight. It
/// has to be a count and not a chance for the load test to exist at all — a probability saturates
/// at one creature per block, so past 1.0 the lever would do nothing and the whole world would top
/// out at the handful of blocks that fit inside the activation radius. Measured before this was
/// fixed: scale 8 with a cap of 24 still produced ONE.
///
/// It is a PARAMETER and not an env read so this stays a pure function: the same arguments always
/// give the same answer, which is what the tests and the two-player reproducibility rest on.
///
/// Positions are raw cell centres and may well be inside a wall: snapping is
/// `NetworkManager::spawn_phantom`'s job (it already runs `resolve_spawn_near` with the ADR-033
/// resolver). Doing it here would make the draw depend on generated geometry, and a pure hash is
/// the only version cheap enough to run over every nearby block every second.
pub fn draw_into(
    world_seed: u64,
    block: (i32, i32),
    layer: u8,
    density_scale: f32,
    out: &mut Vec<[f32; 3]>,
) {
    out.clear();
    let expected = PHANTOM_LAYER_DENSITY
        .get(layer as usize)
        .copied()
        .unwrap_or(0.0)
        * density_scale.max(0.0);
    // Cheap out before touching the RNG at all: at density 0 (every layer but 0 today) this is the
    // entire cost of asking, which is what makes scanning the neighbourhood affordable.
    if expected <= 0.0 {
        return;
    }

    let mut rng = StdRng::seed_from_u64(chunk_seed_layer(
        world_seed ^ PHANTOM_DRAW_SALT,
        block,
        layer as ChunkLayer,
    ));
    // The COUNT is settled first and the positions after, so raising `density_scale` adds creatures
    // without MOVING the ones that were already there — a load test that relocated the whole world
    // would not be testing the same world. The fractional part costs exactly one draw whatever its
    // value, which is what keeps the stream aligned across scales: at the shipped 1.0 this consumes
    // the same single f32 the old presence roll did, so the world is byte-for-byte the one that
    // existed before multiples were possible.
    let extra = rng.gen::<f32>() < expected.fract();
    let count = expected.floor() as usize + usize::from(extra);

    for _ in 0..count {
        let cx = block.0 * BLOCK_CHUNKS + rng.gen_range(0..BLOCK_CHUNKS);
        let cz = block.1 * BLOCK_CHUNKS + rng.gen_range(0..BLOCK_CHUNKS);
        let cell_x = rng.gen_range(0..CHUNK_CELLS as i32);
        let cell_z = rng.gen_range(0..CHUNK_CELLS as i32);

        let gx = cx * CHUNK_CELLS as i32 + cell_x;
        let gz = cz * CHUNK_CELLS as i32 + cell_z;
        out.push([
            (gx as f32 + 0.5) * CELL_SIZE_M,
            // Player-pivot convention, same as `spawn_phantom`: a peer's relayed Y is
            // `floor + PLAYER_BASE_Y` and the client subtracts it because the avatar pivot is at
            // the feet. Reporting the bare floor would put the creature's waist in the ground, and
            // `world_pos_to_layer` would still round to the right layer, so nothing would flag it.
            grid_floor_y(layer) + crate::world::collision::PLAYER_BASE_Y,
            (gz as f32 + 0.5) * CELL_SIZE_M,
        ]);
    }
}

/// `draw_into` for callers that want one block's worth as a value (tests, one-off queries).
pub fn draw_all(
    world_seed: u64,
    block: (i32, i32),
    layer: u8,
    density_scale: f32,
) -> Vec<[f32; 3]> {
    let mut out = Vec::new();
    draw_into(world_seed, block, layer, density_scale, &mut out);
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::grid_gen::world_pos_to_layer;

    const SEEDS: [u64; 3] = [42, 7778, 9_999_999];

    fn grid(range: i32) -> Vec<(i32, i32)> {
        (-range..range)
            .flat_map(|x| (-range..range).map(move |z| (x, z)))
            .collect()
    }

    #[test]
    fn draw_is_deterministic_and_seed_dependent() {
        // The whole promise of the design: same seed → same creatures in the same places, forever
        // and on every machine, so nothing has to be persisted or transmitted.
        for seed in SEEDS {
            for block in [(0, 0), (3, -7), (-40, 91)] {
                assert_eq!(
                    draw_all(seed, block, 0, 1.0),
                    draw_all(seed, block, 0, 1.0),
                    "seed {seed} block {block:?} is not reproducible"
                );
            }
        }
        // …and a different seed is a different world. Compared over many blocks because any single
        // block can legitimately agree by chance.
        let blocks = grid(6);
        let a: Vec<_> = blocks.iter().map(|b| draw_all(42, *b, 0, 1.0)).collect();
        let b: Vec<_> = blocks.iter().map(|b| draw_all(7778, *b, 0, 1.0)).collect();
        assert_ne!(a, b, "two seeds produced an identical population");
    }

    #[test]
    fn layers_one_to_three_are_empty_and_layer_zero_is_not() {
        // ADR-043 D-TABLA in force. If someone turns a layer on, this test is the reminder that it
        // needs vertical traversal first — it is not a bug to fix by editing the assert.
        let blocks = grid(8);
        for seed in SEEDS {
            for layer in 1..4u8 {
                assert!(
                    blocks
                        .iter()
                        .all(|b| draw_all(seed, *b, layer, 1.0).is_empty()),
                    "seed {seed}: layer {layer} is populated but unreachable"
                );
            }
            assert!(
                blocks
                    .iter()
                    .any(|b| !draw_all(seed, *b, 0, 1.0).is_empty()),
                "seed {seed}: layer 0 came out empty"
            );
        }
    }

    #[test]
    fn density_scale_is_a_count_so_the_load_test_can_actually_load() {
        // The bug this test exists for, found by running the deployed binary and not by reading:
        // as a PROBABILITY the lever saturated at one creature per block, so scale 8 with a cap of
        // 24 produced exactly ONE phantom and "how many AIs does the world hold" stayed
        // unanswerable. As an expected COUNT it scales.
        let blocks = grid(6);
        let at = |scale: f32| -> usize {
            blocks
                .iter()
                .map(|b| draw_all(42, *b, 0, scale).len())
                .sum()
        };
        let (one, four, sixteen) = (at(1.0), at(4.0), at(16.0));
        assert!(
            four >= one * 3 && sixteen >= four * 3,
            "density does not scale: 1.0→{one}, 4.0→{four}, 16.0→{sixteen}"
        );
        // Whole multiples are exact, not merely "more": 4.0 over N blocks is 4N creatures.
        assert_eq!(four, one * 4);
        // Scale 0 empties the world — the off switch the activation code relies on.
        assert_eq!(at(0.0), 0);
    }

    #[test]
    fn scaling_density_adds_creatures_without_moving_the_existing_ones() {
        // The load-test lever must not relocate the world, or it would be measuring a different
        // one. Guaranteed by settling the COUNT before drawing any position, with the fractional
        // roll costing exactly one f32 whatever its value — so the stream stays aligned.
        for b in grid(10) {
            let base = draw_all(42, b, 0, 1.0);
            if base.is_empty() {
                continue;
            }
            let denser = draw_all(42, b, 0, 5.0);
            assert_eq!(
                denser.first(),
                base.first(),
                "block {b:?} moved its original creature when density was scaled up"
            );
        }
    }

    #[test]
    fn drawn_positions_land_inside_their_own_block_and_layer() {
        // A draw that leaked outside its block would double-count creatures at the seams: the
        // activation scan asks per block and would spawn the same one from two neighbours.
        for seed in SEEDS {
            for b in grid(5) {
                for p in draw_all(seed, b, 0, 3.0) {
                    assert_eq!(
                        block_of(p[0], p[2]),
                        b,
                        "seed {seed}: block {b:?} drew a position in another block"
                    );
                    assert_eq!(
                        world_pos_to_layer(p[1]),
                        0,
                        "drawn Y does not resolve back to its own layer"
                    );
                }
            }
        }
    }

    #[test]
    fn draw_does_not_correlate_with_the_layout_template_of_the_same_coordinate() {
        // The salt earns its keep here. Without it the block seed IS the chunk seed, and the two
        // draws would share a stream — "creatures are always in the pillar halls" is the kind of
        // bug nobody would ever trace back to a missing xor.
        let unsalted = |block: (i32, i32)| {
            let mut rng = StdRng::seed_from_u64(chunk_seed_layer(42, block, 0));
            rng.gen::<f32>()
        };
        let salted = |block: (i32, i32)| {
            let mut rng = StdRng::seed_from_u64(chunk_seed_layer(42 ^ PHANTOM_DRAW_SALT, block, 0));
            rng.gen::<f32>()
        };
        let agree = grid(6)
            .iter()
            .filter(|b| (unsalted(**b) - salted(**b)).abs() < 1e-6)
            .count();
        assert_eq!(agree, 0, "{agree} blocks share the layout draw's stream");
    }
}
