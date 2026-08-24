//! ADR-094 — where the world's facelings live, adults and child packs alike. Mirrors `world::phantom_spawn` (ADR-043)
//! almost verbatim — same PURE, LAZY draw, same determinism guarantee, same reason nothing here
//! is persisted (see that module's doc for the full argument). ONE structural difference: gated
//! to `zone_kind_for(..) == ZONE_OFFICE` BEFORE touching the RNG, and scoped per CHUNK rather
//! than per 200 m block — an office chunk is already its own "planta" (`layout_grammars`: OFFICE
//! stamps 4 sub-regions per chunk, ADR-087), so there is no coarser unit worth drawing over.
//!
//! The CLIENT never replicates this, same reason as `phantom_spawn`: facelings are
//! host-authoritative synthetic peers (ADR-016's trick, ADR-094 point 1), and a second draw in
//! Unity would be a second world.

use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

use crate::world::architecture::chunk_generator::chunk_seed_layer;
use crate::world::chunk::{ChunkLayer, ZONE_OFFICE};
use crate::world::grid_gen::{grid_floor_y, CELL_SIZE_M, CHUNK_CELLS};
use crate::world::zone_density::zone_kind_for;

/// Expected adults per `ZONE_OFFICE` chunk, per layer. v1 PLACEHOLDER: ADR-094 point 5 flags
/// this "densidad por medir con sonda" exactly like ADR-043's own table did before its own
/// measurement pass — layers 1-3 start at zero for the same reason theirs did (unreachable,
/// `PHANTOM_LAYER_DENSITY`'s doc comment).
pub const FACELING_ADULT_LAYER_DENSITY: [f32; 4] = [2.0, 0.0, 0.0, 0.0];

/// Salt that separates this draw from every other consumer of `chunk_seed_layer` — same trick
/// and same reason as `phantom_spawn::PHANTOM_DRAW_SALT`.
const FACELING_ADULT_DRAW_SALT: u64 = 0xFACE_1105_ADD1_7000;

/// Which adults does chunk `(cx, cz)` hold on `layer`? Appended to `out` (cleared first).
///
/// Empty immediately, with the RNG never touched, when the chunk is not `ZONE_OFFICE` — same
/// cheap-out shape as `phantom_spawn::draw_into`'s density-zero return, for the same reason
/// (this runs over every nearby chunk, every population reconcile).
///
/// Positions are raw cell centres and may land inside a wall: snapping is
/// `NetworkManager::spawn_faceling`'s job via `resolve_spawn_near`, exactly like `spawn_phantom`.
pub fn draw_adults_into(
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
    density_scale: f32,
    out: &mut Vec<[f32; 3]>,
) {
    out.clear();
    if zone_kind_for(world_seed, cx, cz, layer) != ZONE_OFFICE {
        return;
    }
    let expected = FACELING_ADULT_LAYER_DENSITY
        .get(layer as usize)
        .copied()
        .unwrap_or(0.0)
        * density_scale.max(0.0);
    if expected <= 0.0 {
        return;
    }

    let mut rng = StdRng::seed_from_u64(chunk_seed_layer(
        world_seed ^ FACELING_ADULT_DRAW_SALT,
        (cx, cz),
        layer as ChunkLayer,
    ));
    // Count settled before positions, fractional part costs exactly one draw — same reason
    // `phantom_spawn::draw_into` does it this way (a load-test lever must not relocate the
    // creatures that were already there).
    let extra = rng.gen::<f32>() < expected.fract();
    let count = expected.floor() as usize + usize::from(extra);

    for _ in 0..count {
        let cell_x = rng.gen_range(0..CHUNK_CELLS as i32);
        let cell_z = rng.gen_range(0..CHUNK_CELLS as i32);
        let gx = cx * CHUNK_CELLS as i32 + cell_x;
        let gz = cz * CHUNK_CELLS as i32 + cell_z;
        out.push([
            (gx as f32 + 0.5) * CELL_SIZE_M,
            // Player-pivot convention, same as `phantom_spawn::draw_into` and for the same
            // reason: a faceling is a peer, and every peer's relayed Y is floor + PLAYER_BASE_Y.
            grid_floor_y(layer) + crate::world::collision::PLAYER_BASE_Y,
            (gz as f32 + 0.5) * CELL_SIZE_M,
        ]);
    }
}

/// `draw_adults_into` for callers that want one chunk's worth as a value (tests, one-off
/// queries).
pub fn draw_adults(
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
    density_scale: f32,
) -> Vec<[f32; 3]> {
    let mut out = Vec::new();
    draw_adults_into(world_seed, cx, cz, layer, density_scale, &mut out);
    out
}

/// Probability a given `ZONE_OFFICE` chunk anchors a child pack. Unlike the adults' expected-COUNT
/// table, a pack is atomic — a chunk holds exactly one pack or none, never a fractional or scaled
/// count of packs — so this is a genuine [0,1] probability, not an "expected count" multiplier.
/// v1 PLACEHOLDER, unmeasured, same as `FACELING_ADULT_LAYER_DENSITY`.
pub const FACELING_CHILD_PACK_LAYER_PROBABILITY: [f32; 4] = [0.5, 0.0, 0.0, 0.0];

/// ADR-094 point 3 said "packs de 3-4"; Enmienda 2 (play-test 2026-08-24) widens it to 3-5, with
/// `assign_roles` giving the fifth a second `Press`. Salt of its own so the pack roll and the adult
/// roll (and the robapieles') never share a stream — same reasoning as `PHANTOM_DRAW_SALT`.
const FACELING_CHILD_DRAW_SALT: u64 = 0xFACE_C41D_0000_7EEE;

/// Does chunk `(cx, cz)` anchor a child pack on `layer`, and where does each member start? Appended
/// to `out` (cleared first) — 3 to 5 positions, or none. Same `ZONE_OFFICE` cheap-out as
/// `draw_adults_into`, same reason.
///
/// Members are scattered a few cells apart (not stacked on one point) so `AdultDriver`-style
/// per-member snapping never needs to shove more than one of them off the same spot.
pub fn draw_child_pack_into(
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
    density_scale: f32,
    out: &mut Vec<[f32; 3]>,
) {
    out.clear();
    if zone_kind_for(world_seed, cx, cz, layer) != ZONE_OFFICE {
        return;
    }
    let chance = FACELING_CHILD_PACK_LAYER_PROBABILITY
        .get(layer as usize)
        .copied()
        .unwrap_or(0.0)
        * density_scale.max(0.0);
    if chance <= 0.0 {
        return;
    }

    let mut rng = StdRng::seed_from_u64(chunk_seed_layer(
        world_seed ^ FACELING_CHILD_DRAW_SALT,
        (cx, cz),
        layer as ChunkLayer,
    ));
    // The roll is settled BEFORE the size, and the size before any position — same "count first"
    // discipline `phantom_spawn::draw_into` uses, so raising `density_scale` never relocates a
    // pack that already existed at a lower scale.
    if rng.gen::<f32>() >= chance.min(1.0) {
        return;
    }
    let size = 3 + rng.gen_range(0..3u32); // 3, 4 or 5 (Enmienda 2)

    for _ in 0..size {
        let cell_x = rng.gen_range(0..CHUNK_CELLS as i32);
        let cell_z = rng.gen_range(0..CHUNK_CELLS as i32);
        let gx = cx * CHUNK_CELLS as i32 + cell_x;
        let gz = cz * CHUNK_CELLS as i32 + cell_z;
        out.push([
            (gx as f32 + 0.5) * CELL_SIZE_M,
            grid_floor_y(layer) + crate::world::collision::PLAYER_BASE_Y,
            (gz as f32 + 0.5) * CELL_SIZE_M,
        ]);
    }
}

/// `draw_child_pack_into` for callers that want one chunk's worth as a value (tests, one-off
/// queries).
pub fn draw_child_pack(
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
    density_scale: f32,
) -> Vec<[f32; 3]> {
    let mut out = Vec::new();
    draw_child_pack_into(world_seed, cx, cz, layer, density_scale, &mut out);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    const SEEDS: [u64; 3] = [42, 7778, 9_999_999];

    #[test]
    fn draw_is_deterministic_and_seed_dependent() {
        for seed in SEEDS {
            for chunk in [(0, 0), (3, -7), (-40, 91)] {
                assert_eq!(
                    draw_adults(seed, chunk.0, chunk.1, 0, 1.0),
                    draw_adults(seed, chunk.0, chunk.1, 0, 1.0),
                    "seed {seed} chunk {chunk:?} is not reproducible"
                );
            }
        }
        let chunks: Vec<(i32, i32)> = (-30..30)
            .flat_map(|x| (-30..30).map(move |z| (x, z)))
            .collect();
        let a: Vec<_> = chunks
            .iter()
            .map(|c| draw_adults(42, c.0, c.1, 0, 1.0))
            .collect();
        let b: Vec<_> = chunks
            .iter()
            .map(|c| draw_adults(7778, c.0, c.1, 0, 1.0))
            .collect();
        assert_ne!(a, b, "two seeds produced an identical adult population");
    }

    /// The invariant this whole module exists for: a non-empty draw NEVER happens outside
    /// `ZONE_OFFICE`. Checked as an implication over a wide grid rather than by hand-picking one
    /// known office chunk, because which chunks roll OFFICE is itself seed-derived.
    #[test]
    fn adults_never_populate_outside_zone_office() {
        let mut any_populated = false;
        for seed in SEEDS {
            for cx in -25..25 {
                for cz in -25..25 {
                    let drawn = draw_adults(seed, cx, cz, 0, 1.0);
                    if drawn.is_empty() {
                        continue;
                    }
                    any_populated = true;
                    assert_eq!(
                        zone_kind_for(seed, cx, cz, 0),
                        ZONE_OFFICE,
                        "seed {seed} chunk ({cx},{cz}) populated adults outside ZONE_OFFICE"
                    );
                }
            }
        }
        assert!(
            any_populated,
            "no office chunk populated in a 50x50 grid across {} seeds — gate is rejecting everything",
            SEEDS.len()
        );
    }

    #[test]
    fn layers_one_to_three_are_empty_and_layer_zero_is_not() {
        for seed in SEEDS {
            for layer in 1..4u8 {
                for cx in -20..20 {
                    for cz in -20..20 {
                        assert!(
                            draw_adults(seed, cx, cz, layer, 1.0).is_empty(),
                            "seed {seed}: layer {layer} chunk ({cx},{cz}) is populated but unreachable"
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn density_scale_is_a_count() {
        let chunks: Vec<(i32, i32)> = (-25..25)
            .flat_map(|x| (-25..25).map(move |z| (x, z)))
            .collect();
        let at = |scale: f32| -> usize {
            chunks
                .iter()
                .map(|c| draw_adults(42, c.0, c.1, 0, scale).len())
                .sum()
        };
        let (one, four) = (at(1.0), at(4.0));
        assert!(one > 0, "no adults drawn at scale 1.0 in a 50x50 grid");
        assert_eq!(four, one * 4);
        assert_eq!(at(0.0), 0);
    }

    #[test]
    fn drawn_positions_land_inside_their_own_chunk() {
        for seed in SEEDS {
            for cx in -15..15 {
                for cz in -15..15 {
                    for p in draw_adults(seed, cx, cz, 0, 3.0) {
                        let gx = (p[0] / CELL_SIZE_M).floor() as i32;
                        let gz = (p[2] / CELL_SIZE_M).floor() as i32;
                        assert_eq!(
                            (
                                gx.div_euclid(CHUNK_CELLS as i32),
                                gz.div_euclid(CHUNK_CELLS as i32)
                            ),
                            (cx, cz),
                            "seed {seed}: chunk ({cx},{cz}) drew a position in another chunk"
                        );
                    }
                }
            }
        }
    }

    // ─── draw_child_pack_into ───

    #[test]
    fn child_pack_draw_is_deterministic_and_seed_dependent() {
        for seed in SEEDS {
            for chunk in [(0, 0), (3, -7), (-40, 91)] {
                assert_eq!(
                    draw_child_pack(seed, chunk.0, chunk.1, 0, 1.0),
                    draw_child_pack(seed, chunk.0, chunk.1, 0, 1.0),
                    "seed {seed} chunk {chunk:?} is not reproducible"
                );
            }
        }
        let chunks: Vec<(i32, i32)> = (-30..30)
            .flat_map(|x| (-30..30).map(move |z| (x, z)))
            .collect();
        let a: Vec<_> = chunks
            .iter()
            .map(|c| draw_child_pack(42, c.0, c.1, 0, 1.0))
            .collect();
        let b: Vec<_> = chunks
            .iter()
            .map(|c| draw_child_pack(7778, c.0, c.1, 0, 1.0))
            .collect();
        assert_ne!(a, b, "two seeds produced an identical pack population");
    }

    #[test]
    fn child_packs_never_populate_outside_zone_office() {
        let mut any_populated = false;
        for seed in SEEDS {
            for cx in -25..25 {
                for cz in -25..25 {
                    let drawn = draw_child_pack(seed, cx, cz, 0, 1.0);
                    if drawn.is_empty() {
                        continue;
                    }
                    any_populated = true;
                    assert_eq!(
                        zone_kind_for(seed, cx, cz, 0),
                        ZONE_OFFICE,
                        "seed {seed} chunk ({cx},{cz}) anchored a pack outside ZONE_OFFICE"
                    );
                }
            }
        }
        assert!(
            any_populated,
            "no office chunk anchored a pack in a 50x50 grid across {} seeds",
            SEEDS.len()
        );
    }

    /// ADR-094 point 3 + Enmienda 2: "packs de 3-5" — never fewer, never more, and never a
    /// fractional roster (unlike the adults' count, this is atomic per chunk).
    #[test]
    fn a_pack_is_always_three_to_five() {
        let mut sizes_seen: std::collections::HashSet<usize> = std::collections::HashSet::new();
        for seed in SEEDS {
            for cx in -25..25 {
                for cz in -25..25 {
                    let n = draw_child_pack(seed, cx, cz, 0, 1.0).len();
                    if n == 0 {
                        continue;
                    }
                    assert!(
                        (3..=5).contains(&n),
                        "seed {seed} chunk ({cx},{cz}) drew a pack of size {n}"
                    );
                    sizes_seen.insert(n);
                }
            }
        }
        assert_eq!(
            sizes_seen,
            std::collections::HashSet::from([3, 4, 5]),
            "all three pack sizes should show up across this many chunks"
        );
    }

    #[test]
    fn child_pack_positions_land_inside_their_own_chunk() {
        for seed in SEEDS {
            for cx in -15..15 {
                for cz in -15..15 {
                    for p in draw_child_pack(seed, cx, cz, 0, 3.0) {
                        let gx = (p[0] / CELL_SIZE_M).floor() as i32;
                        let gz = (p[2] / CELL_SIZE_M).floor() as i32;
                        assert_eq!(
                            (
                                gx.div_euclid(CHUNK_CELLS as i32),
                                gz.div_euclid(CHUNK_CELLS as i32)
                            ),
                            (cx, cz),
                            "seed {seed}: chunk ({cx},{cz}) drew a pack member in another chunk"
                        );
                    }
                }
            }
        }
    }
}
