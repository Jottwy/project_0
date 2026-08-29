//! ADR-094 — where the world's facelings live, adults and child packs alike. Mirrors `world::phantom_spawn` (ADR-043)
//! almost verbatim — same PURE, LAZY draw, same determinism guarantee, same reason nothing here
//! is persisted (see that module's doc for the full argument). ONE structural difference: density
//! is WEIGHTED by `zone_kind_for(..) == ZONE_OFFICE` (Enmienda 5 — it used to be a hard gate), and
//! scoped per CHUNK rather than per 200 m block — an office chunk is already its own "planta"
//! (`layout_grammars`: OFFICE stamps 4 sub-regions per chunk, ADR-087), so there is no coarser
//! unit worth drawing over.
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

/// Expected adults per `ZONE_OFFICE` chunk, per layer — scaled by `FACELING_OUTSIDE_DENSITY_FACTOR`
/// anywhere else. v1 PLACEHOLDER: ADR-094 point 5 flags
/// this "densidad por medir con sonda" exactly like ADR-043's own table did before its own
/// measurement pass — layers 1-3 start at zero for the same reason theirs did (unreachable,
/// `PHANTOM_LAYER_DENSITY`'s doc comment).
pub const FACELING_ADULT_LAYER_DENSITY: [f32; 4] = [2.0, 0.0, 0.0, 0.0];

/// Salt that separates this draw from every other consumer of `chunk_seed_layer` — same trick
/// and same reason as `phantom_spawn::PHANTOM_DRAW_SALT`.
const FACELING_ADULT_DRAW_SALT: u64 = 0xFACE_1105_ADD1_7000;

/// ADR-109 D5 — sorteo de la concentración en oficinas, ahora hueco a hueco.
const FACELING_WG3_KEEP_SALT: u64 = 0xFACE_1109_0FF1_0000;

/// ADR-109 D5 — con qué probabilidad se queda un hueco que cae en un espacio de OFICINA.
///
/// **No es un gusto: es lo que hace que la población no cambie al mudarse.** Medido, y por eso está
/// aquí: en WG2 la oficina era el 4 % del mundo (la banda `TEMPLATE_OFFICE` del sorteo de plantillas),
/// y en WG3 el papel de oficina —`style` 0, el brazo `_` de `fill::style_of`— cubre el **39 %** de los
/// huecos sorteados. Quedarse con todos multiplicaba la población por 3,5 (255 frente a 72 en la
/// sonda de 36 chunks), que es un cambio de balance que nadie pidió y que el jugador habría notado
/// antes que la migración.
///
/// El 8:1 entre dentro y fuera de oficina sí se conserva: es lo que ADR-094 enmienda 5 dejó
/// calibrado, y es la mitad de lo que hace que entrar en una oficina signifique algo.
const FACELING_WG3_OFFICE_KEEP: f32 = 0.23;

/// La cota con la que sale un candidato del sorteo.
///
/// Con WG2 es la de su capa. Con WG3 no hay cota que dar aquí —la geometría no se conoce en un
/// sorteo puro por semilla— y se devuelve la del suelo base; quien llama la sustituye por la del
/// espacio en el que cae. Un número que sabe que es provisional es mejor que uno de una capa de 4 m
/// que parece bueno y no lo es.
fn candidate_y(wg3: bool, layer: u8) -> f32 {
    match wg3 {
        true => crate::world::collision::PLAYER_BASE_Y,
        false => grid_floor_y(layer) + crate::world::collision::PLAYER_BASE_Y,
    }
}

/// ADR-109 D5 — ¿se queda este hueco, con el papel del espacio donde cae?
///
/// Es la enmienda 5 de ADR-094 traducida a WG3: en oficina se queda siempre, fuera una de cada ocho.
/// Lo que cambia es la RESOLUCIÓN — WG2 respondía por chunk de 50 m porque no sabía qué había dentro;
/// WG3 lo sabe espacio a espacio, así que un chunk mixto ya no es todo oficina o nada.
///
/// `style` 0 es el papel de oficina (`fill::style_of` la deja en el brazo `_`, junto con lo que no
/// tiene número propio). Sin espacio —el hueco cae en el vacío del plan— no se queda nadie: ahí no
/// hay suelo que pisar.
///
/// Determinista por (semilla, chunk, capa, índice del hueco): dos llamadas dan lo mismo, y subir
/// `density_scale` no reubica a quien ya estaba.
pub fn wg3_keeps_position(
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
    index: usize,
    style: Option<u8>,
) -> bool {
    let Some(style) = style else {
        return false;
    };
    let keep = match style {
        0 => FACELING_WG3_OFFICE_KEEP,
        _ => FACELING_WG3_OFFICE_KEEP * FACELING_OUTSIDE_DENSITY_FACTOR,
    };
    let mut rng = StdRng::seed_from_u64(
        chunk_seed_layer(
            world_seed ^ FACELING_WG3_KEEP_SALT,
            (cx, cz),
            layer as ChunkLayer,
        )
        .wrapping_add(index as u64),
    );
    rng.gen::<f32>() < keep
}

/// Enmienda 5 — how much of the office density survives OUTSIDE an office chunk.
///
/// One in eight. Low enough that an office is still unmistakably where they live (ADR-094 point 5's
/// "entrar en oficinas ES la decision de riesgo" survives as a matter of degree), high enough that
/// the rest of the level is no longer guaranteed safe. It also bounds the cost: the population
/// reconcile now considers every nearby chunk instead of only the office ones, and this factor is
/// what keeps that from multiplying the active roster (the `FACELING_ACTIVE_CAP` /
/// `FACELING_CHILD_PACK_ACTIVE_CAP` ceilings still backstop it).
pub const FACELING_OUTSIDE_DENSITY_FACTOR: f32 = 0.125;

/// Which adults does chunk `(cx, cz)` hold on `layer`? Appended to `out` (cleared first).
///
/// Sparse but not empty outside an office (Enmienda 5). Still returns before touching the RNG when
/// the resulting density is zero — same cheap-out shape as `phantom_spawn::draw_into`'s, and it
/// still matters: this runs over every nearby chunk, every population reconcile.
///
/// Positions are raw cell centres and may land inside a wall: snapping is
/// `NetworkManager::spawn_faceling`'s job via `resolve_spawn_near`, exactly like `spawn_phantom`.
pub fn draw_adults_into(
    world_seed: u64,
    cx: i32,
    cz: i32,
    layer: u8,
    density_scale: f32,
    // ADR-109 D5 — el reparto se hace con WG3. Cambia DÓNDE se decide la concentración de oficina:
    // aquí ya no, porque un chunk de WG3 tiene varios espacios con papeles distintos y una respuesta
    // por chunk sería más basta que el mundo que describe. La decide `wg3_keeps_position`, hueco a
    // hueco, con el papel del espacio en el que cae. El resultado esperado por chunk es el mismo.
    wg3: bool,
    out: &mut Vec<[f32; 3]>,
) {
    out.clear();
    // Enmienda 5 (Joel, play-test 2026-08-24): they are no longer CONFINED to offices, they are
    // CONCENTRATED there. ADR-094 point 5 kept them strictly inside `ZONE_OFFICE` so that walking
    // in was the risk decision and the maze stayed the robapieles' territory; running into one in
    // a corridor now costs a fraction of that, and the office is still where they live by a factor
    // of `FACELING_OUTSIDE_DENSITY_FACTOR`. Both identities survive; the world stops feeling
    // partitioned.
    let zone_factor = match wg3 {
        true => 1.0,
        false => match zone_kind_for(world_seed, cx, cz, layer) == ZONE_OFFICE {
            true => 1.0,
            false => FACELING_OUTSIDE_DENSITY_FACTOR,
        },
    };
    let expected = FACELING_ADULT_LAYER_DENSITY
        .get(layer as usize)
        .copied()
        .unwrap_or(0.0)
        * density_scale.max(0.0)
        * zone_factor;
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
            //
            // ADR-109 D5 — con WG3 la cota NO sale de aquí: la capa mide 4 m y las plantas 3,32, así
            // que este número sería falso en cuanto el suelo no esté a cero. Se deja el suelo del
            // espacio, que lo pone quien llama con la geometría delante.
            candidate_y(wg3, layer),
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
    draw_adults_into(world_seed, cx, cz, layer, density_scale, false, &mut out);
    out
}

/// Probability a `ZONE_OFFICE` chunk anchors a child pack — scaled by
/// `FACELING_OUTSIDE_DENSITY_FACTOR` elsewhere. Unlike the adults' expected-COUNT
/// table, a pack is atomic — a chunk holds exactly one pack or none, never a fractional or scaled
/// count of packs — so this is a genuine [0,1] probability, not an "expected count" multiplier.
/// v1 PLACEHOLDER, unmeasured, same as `FACELING_ADULT_LAYER_DENSITY`.
pub const FACELING_CHILD_PACK_LAYER_PROBABILITY: [f32; 4] = [0.5, 0.0, 0.0, 0.0];

/// ADR-094 point 3 said "packs de 3-4"; Enmienda 2 widened it to 3-5 and Enmienda 5 to 3-8, with
/// `assign_roles` sending everyone past the fifth to the `Ring`. Salt of its own so the pack roll
/// and the adult roll (and the robapieles') never share a stream — as `PHANTOM_DRAW_SALT`.
const FACELING_CHILD_DRAW_SALT: u64 = 0xFACE_C41D_0000_7EEE;

/// Does chunk `(cx, cz)` anchor a child pack on `layer`, and where does each member start? Appended
/// to `out` (cleared first) — 3 to 8 positions, or none. Same office-weighting as
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
    // Igual que en los adultos, ver su doc.
    wg3: bool,
    out: &mut Vec<[f32; 3]>,
) {
    out.clear();
    // Same office-dense / elsewhere-sparse split as the adults — see `draw_adults_into`.
    let zone_factor = match wg3 {
        true => 1.0,
        false => match zone_kind_for(world_seed, cx, cz, layer) == ZONE_OFFICE {
            true => 1.0,
            false => FACELING_OUTSIDE_DENSITY_FACTOR,
        },
    };
    let chance = FACELING_CHILD_PACK_LAYER_PROBABILITY
        .get(layer as usize)
        .copied()
        .unwrap_or(0.0)
        * density_scale.max(0.0)
        * zone_factor;
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
    let size = 3 + rng.gen_range(0..6u32); // 3..=8 (Enmienda 5)

    for _ in 0..size {
        let cell_x = rng.gen_range(0..CHUNK_CELLS as i32);
        let cell_z = rng.gen_range(0..CHUNK_CELLS as i32);
        let gx = cx * CHUNK_CELLS as i32 + cell_x;
        let gz = cz * CHUNK_CELLS as i32 + cell_z;
        out.push([
            (gx as f32 + 0.5) * CELL_SIZE_M,
            candidate_y(wg3, layer), // ver `draw_adults_into`
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
    draw_child_pack_into(world_seed, cx, cz, layer, density_scale, false, &mut out);
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
    fn adults_are_dense_in_offices_and_sparse_outside() {
        // Enmienda 5 replaced the hard `ZONE_OFFICE` gate with a density RATIO, so the invariant
        // worth pinning is no longer "never outside" — it is that an office is still where they
        // live by a wide margin. A regression that flattened the two would pass any "some appear
        // outside" check, so this measures both sides and compares them.
        let mut office_chunks = 0usize;
        let mut office_adults = 0usize;
        let mut other_chunks = 0usize;
        let mut other_adults = 0usize;

        for seed in SEEDS {
            for cx in -25..25 {
                for cz in -25..25 {
                    let n = draw_adults(seed, cx, cz, 0, 1.0).len();
                    if zone_kind_for(seed, cx, cz, 0) == ZONE_OFFICE {
                        office_chunks += 1;
                        office_adults += n;
                    } else {
                        other_chunks += 1;
                        other_adults += n;
                    }
                }
            }
        }

        assert!(office_chunks > 0 && other_chunks > 0, "degenerate sample");
        assert!(office_adults > 0, "offices populated nobody at all");
        assert!(
            other_adults > 0,
            "nothing spawned outside an office — the corridors are still empty"
        );

        let office_rate = office_adults as f32 / office_chunks as f32;
        let other_rate = other_adults as f32 / other_chunks as f32;
        assert!(
            office_rate > other_rate * 3.0,
            "offices ({office_rate:.3}/chunk) are not meaningfully denser than everywhere else              ({other_rate:.3}/chunk) — the office has stopped meaning anything"
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
        // Enmienda 5: exact 4x proportionality no longer holds, and the reason is worth stating.
        // Outside an office the expected count is scaled to a FRACTION, and a fractional expected
        // spends one RNG draw deciding whether to round up — so those chunks contribute a
        // probabilistic count rather than a multiplied one. Inside offices it is still exactly
        // linear; across the mix it lands close to 4x, not on it.
        let ratio = four as f32 / one as f32;
        assert!(
            (3.5..=4.5).contains(&ratio),
            "scale 4.0 drew {four} against {one} at 1.0 (x{ratio:.2}) — density_scale should still              be a count multiplier"
        );
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
    fn child_packs_are_dense_in_offices_and_sparse_outside() {
        // Same shape as the adults' — Enmienda 5 turned the hard gate into a ratio, so what is
        // worth pinning is the RATIO, not the absence.
        let mut office_chunks = 0usize;
        let mut office_packs = 0usize;
        let mut other_chunks = 0usize;
        let mut other_packs = 0usize;

        for seed in SEEDS {
            for cx in -25..25 {
                for cz in -25..25 {
                    let anchored = usize::from(!draw_child_pack(seed, cx, cz, 0, 1.0).is_empty());
                    if zone_kind_for(seed, cx, cz, 0) == ZONE_OFFICE {
                        office_chunks += 1;
                        office_packs += anchored;
                    } else {
                        other_chunks += 1;
                        other_packs += anchored;
                    }
                }
            }
        }

        assert!(office_chunks > 0 && other_chunks > 0, "degenerate sample");
        assert!(office_packs > 0, "offices anchored no packs at all");
        assert!(
            other_packs > 0,
            "no pack anchored outside an office — the corridors are still empty"
        );

        let office_rate = office_packs as f32 / office_chunks as f32;
        let other_rate = other_packs as f32 / other_chunks as f32;
        assert!(
            office_rate > other_rate * 3.0,
            "offices ({office_rate:.3}/chunk) are not meaningfully denser than everywhere else              ({other_rate:.3}/chunk)"
        );
    }

    /// ADR-094 point 3, as widened by Enmiendas 2 and 5: "packs de 3-8" — never fewer, never more,
    /// and never a fractional roster (unlike the adults' count, this is atomic per chunk).
    #[test]
    fn a_pack_is_always_three_to_eight() {
        let mut sizes_seen: std::collections::HashSet<usize> = std::collections::HashSet::new();
        for seed in SEEDS {
            for cx in -25..25 {
                for cz in -25..25 {
                    let n = draw_child_pack(seed, cx, cz, 0, 1.0).len();
                    if n == 0 {
                        continue;
                    }
                    assert!(
                        (3..=8).contains(&n),
                        "seed {seed} chunk ({cx},{cz}) drew a pack of size {n}"
                    );
                    sizes_seen.insert(n);
                }
            }
        }
        assert_eq!(
            sizes_seen,
            std::collections::HashSet::from([3, 4, 5, 6, 7, 8]),
            "every pack size should show up across this many chunks"
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
