//! ADR-040 D-NAV — navigation for the robapieles: 4-connected A* over grid_gen cells.
//!
//! WHY 4-CONNECTED: an 8-connected search would emit diagonal moves, and a diagonal is exactly the
//! step the collision resolver may refuse (ADR-040 D-DIAGONAL). Restricting the search to the four
//! axial neighbours makes every path it produces traversable BY CONSTRUCTION, instead of by a
//! promise someone has to keep. The corner-cutting that 8-connected buys back is recovered later by
//! string-pulling, which validates each shortcut against the same resolver rule.
//!
//! WHAT THIS MODULE MAY NOT DO (ADR-040, and it is structural, not a convention):
//! * It has NO generator and NO cache of its own. It borrows the `PhantomDriver`'s
//!   `GridGenChunkCache`, which already carries `zone_density::rules_for` (ADR-033). It is therefore
//!   impossible for navigation and render to disagree about the world.
//! * It only READS `generate_chunk_layer` output. It never consumes a draw from the generator's
//!   `StdRng` and never touches phase order — one extra draw would regenerate every chunk of every
//!   seed ever played (ADR-007 §9).
//! * It is host-only, sim-only state: never serialized, never in `world.chunks`, never on the wire.
//!
//! COST: the search is bounded twice over — by a window (it cannot wander off across the world) and
//! by an expansion cap. Both buffers live in a reusable `NavScratch` owned by the driver, so a
//! search performs ZERO heap allocations.

use std::collections::BinaryHeap;

use super::collision::{cell_is_walkable, global_cell, GridGenChunkCache};
use super::CELL_SIZE_M;
use crate::utils::Vec3;

/// Half-width of the search window, in cells. 24 cells = 60 m, comfortably beyond
/// `PHANTOM_LOSE_RADIUS` (25 m), so the phantom can route around obstacles at the edge of its own
/// perception without the window ever being the thing that fails.
pub const NAV_WINDOW_CELLS: i32 = 24;
/// Window side in cells (49) and total cells (2401).
const WINDOW_SIDE: i32 = NAV_WINDOW_CELLS * 2 + 1;
const WINDOW_CELLS: usize = (WINDOW_SIDE * WINDOW_SIDE) as usize;
/// Hard cap on expansions. The window is the real limit in practice; this is the backstop that
/// keeps a pathological layout from eating the tick.
pub const NAV_MAX_EXPANSIONS: usize = 3_000;
/// Uniform step cost. Integer arithmetic on purpose: `f32` has no total order, so a float cost
/// would force a wrapper type just to satisfy `Ord` on the heap.
const STEP_COST: u32 = 10;

/// Absolute grid_gen cell.
pub type CellCoord = (i32, i32);

/// What a search did, for tests and for the ignored benchmark.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct NavStats {
    pub expansions: usize,
    /// True when the goal was not reachable and the path aims at the closest cell reached instead.
    pub best_effort: bool,
    /// True when the search stopped on the expansion cap rather than on the window edge.
    pub hit_expansion_cap: bool,
}

/// Reusable buffers. Allocated ONCE by the driver; a search touches no allocator.
pub struct NavScratch {
    g: Vec<u32>,
    parent: Vec<u8>,
    /// Visit stamps: a search bumps `epoch` instead of clearing 2401 entries.
    stamp: Vec<u32>,
    epoch: u32,
    open: BinaryHeap<Node>,
}

impl Default for NavScratch {
    fn default() -> Self {
        Self::new()
    }
}

impl NavScratch {
    pub fn new() -> Self {
        Self {
            g: vec![u32::MAX; WINDOW_CELLS],
            parent: vec![NO_PARENT; WINDOW_CELLS],
            stamp: vec![0; WINDOW_CELLS],
            epoch: 0,
            open: BinaryHeap::with_capacity(512),
        }
    }
}

const NO_PARENT: u8 = 0xFF;
/// Neighbour order: +X, -X, +Z, -Z. `parent[i]` stores the index of the move that REACHED i, so
/// walking back applies the opposite offset.
const DIRS: [(i32, i32); 4] = [(1, 0), (-1, 0), (0, 1), (0, -1)];

/// Min-heap entry (`BinaryHeap` is a max-heap, so ordering is reversed on f).
#[derive(Eq, PartialEq)]
struct Node {
    f: u32,
    idx: u32,
}

impl Ord for Node {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        other.f.cmp(&self.f).then_with(|| self.idx.cmp(&other.idx))
    }
}

impl PartialOrd for Node {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

/// Centre of a cell, in world units. Paths are built from cell centres so the first segment is
/// never an ambiguous boundary case (same reason `resolve_spawn_near` snaps to centre).
pub fn cell_center(cell: CellCoord, y: f32) -> Vec3 {
    Vec3::new(
        (cell.0 as f32 + 0.5) * CELL_SIZE_M,
        y,
        (cell.1 as f32 + 0.5) * CELL_SIZE_M,
    )
}

/// The absolute cell a world position sits in.
pub fn cell_of(pos: Vec3) -> CellCoord {
    global_cell(pos)
}

/// 4-connected A* from `start` to `goal`, both absolute cells, restricted to a window centred on
/// `start`. Appends the resulting cell path (EXCLUDING `start`, including the reached goal) to
/// `out`, which is cleared first.
///
/// BEST EFFORT is deliberate, not a fallback for bugs: the player collides against
/// `world::collision`, a different model from grid_gen, so the player can legitimately stand in a
/// cell grid_gen calls solid. Returning "no path" there would freeze the phantom exactly when it is
/// closest to you. Instead the search returns the route to the reachable cell with the lowest
/// heuristic, and `NavStats::best_effort` says so.
pub fn find_path(
    cache: &mut GridGenChunkCache,
    layer: u8,
    start: CellCoord,
    goal: CellCoord,
    scratch: &mut NavScratch,
    out: &mut Vec<CellCoord>,
) -> NavStats {
    out.clear();
    let mut stats = NavStats::default();

    let origin = (start.0 - NAV_WINDOW_CELLS, start.1 - NAV_WINDOW_CELLS);
    let idx_of = |c: CellCoord| -> Option<usize> {
        let lx = c.0 - origin.0;
        let lz = c.1 - origin.1;
        if lx < 0 || lz < 0 || lx >= WINDOW_SIDE || lz >= WINDOW_SIDE {
            None
        } else {
            Some((lz * WINDOW_SIDE + lx) as usize)
        }
    };
    let coord_of = |i: usize| -> CellCoord {
        let i = i as i32;
        (origin.0 + i % WINDOW_SIDE, origin.1 + i / WINDOW_SIDE)
    };
    let heuristic =
        |c: CellCoord| -> u32 { STEP_COST * ((c.0 - goal.0).abs() + (c.1 - goal.1).abs()) as u32 };

    if start == goal {
        return stats;
    }

    scratch.epoch = scratch.epoch.wrapping_add(1);
    if scratch.epoch == 0 {
        // Wrapped: stale stamps could alias. Clearing here costs one memset every 4 billion
        // searches, which is cheaper than carrying a per-entry generation check.
        scratch.stamp.iter_mut().for_each(|s| *s = 0);
        scratch.epoch = 1;
    }
    let epoch = scratch.epoch;
    scratch.open.clear();

    let start_idx = match idx_of(start) {
        Some(i) => i,
        None => return stats, // unreachable by construction (start is the window centre)
    };
    scratch.g[start_idx] = 0;
    scratch.parent[start_idx] = NO_PARENT;
    scratch.stamp[start_idx] = epoch;
    scratch.open.push(Node {
        f: heuristic(start),
        idx: start_idx as u32,
    });

    let mut best_idx = start_idx;
    let mut best_h = heuristic(start);
    let mut found = false;

    while let Some(Node { f: _, idx }) = scratch.open.pop() {
        let idx = idx as usize;
        let here = coord_of(idx);
        if here == goal {
            best_idx = idx;
            found = true;
            break;
        }
        stats.expansions += 1;
        if stats.expansions >= NAV_MAX_EXPANSIONS {
            stats.hit_expansion_cap = true;
            break;
        }

        let g_here = scratch.g[idx];
        for (d, (dx, dz)) in DIRS.iter().enumerate() {
            let next = (here.0 + dx, here.1 + dz);
            let n_idx = match idx_of(next) {
                Some(i) => i,
                None => continue, // outside the window
            };
            // The GOAL cell is probed even when solid: reaching it is impossible, but letting the
            // search look at it costs nothing and keeps the best-effort target honest.
            if next != goal && !cell_is_walkable(cache, next.0, next.1, layer) {
                continue;
            }
            let tentative = g_here + STEP_COST;
            let seen = scratch.stamp[n_idx] == epoch;
            if seen && tentative >= scratch.g[n_idx] {
                continue;
            }
            scratch.stamp[n_idx] = epoch;
            scratch.g[n_idx] = tentative;
            scratch.parent[n_idx] = d as u8;
            let h = heuristic(next);
            if h < best_h {
                best_h = h;
                best_idx = n_idx;
            }
            scratch.open.push(Node {
                f: tentative + h,
                idx: n_idx as u32,
            });
        }
    }

    stats.best_effort = !found;
    if best_idx == start_idx {
        return stats; // nowhere better than where we already are
    }

    // Walk parents back to the start, then reverse.
    let mut cursor = best_idx;
    while cursor != start_idx {
        out.push(coord_of(cursor));
        let d = scratch.parent[cursor];
        if d == NO_PARENT {
            break;
        }
        let (dx, dz) = DIRS[d as usize];
        let prev = (coord_of(cursor).0 - dx, coord_of(cursor).1 - dz);
        cursor = match idx_of(prev) {
            Some(i) => i,
            None => break,
        };
    }
    out.reverse();

    // ADR-040 D-COTA: this module reads cells through `get_or_generate`, which does NOT enforce the
    // cache cap — only `resolve_move_grid_gen` did, which is a demonstrated leak (2,000 direct
    // calls left 2,000 chunk-layers cached). Every consumer bounds the cache itself.
    let n = super::CHUNK_CELLS as i32;
    cache.enforce_cap((start.0.div_euclid(n), start.1.div_euclid(n)));

    stats
}

#[cfg(test)]
mod tests {
    use super::super::collision::default_layer_rules;
    use super::*;
    use crate::world::grid_gen::{Cell, CellType, LayerGrid};

    /// Build a cache whose chunk (0,0) is a hand-authored grid.
    fn cache_with(grid: LayerGrid) -> GridGenChunkCache {
        let mut c = GridGenChunkCache::with_rules(1, default_layer_rules);
        c.insert_for_test((0, 0, 0), grid);
        c
    }

    fn open_grid() -> LayerGrid {
        let mut g = LayerGrid::new_solid();
        for x in 0..20 {
            for z in 0..20 {
                g.set(x, z, Cell::new(CellType::Corridor, 2, 0));
            }
        }
        g
    }

    /// The quantization guard. If `cell_of` and the collision sampler ever disagree, the phantom
    /// would plan from a cell it is not standing in — the split-brain ADR-040 forbids by making
    /// both derive from `global_cell`.
    #[test]
    fn cell_of_matches_collision_quantization() {
        for &(x, z) in &[
            (0.0_f32, 0.0_f32),
            (2.49, 2.51),
            (-0.01, -2.6),
            (49.9, 50.1),
            (-51.0, 12.4),
        ] {
            let pos = Vec3::new(x, 0.0, z);
            assert_eq!(
                cell_of(pos),
                global_cell(pos),
                "cell_of must BE the collision quantization, not a copy of it"
            );
        }
    }

    #[test]
    fn path_routes_around_a_wall_instead_of_through_it() {
        let mut g = open_grid();
        // Vertical wall across x = 5, with a gap at z = 0 only.
        for z in 1..20 {
            g.set(5, z, Cell::new(CellType::Wall, 2, 0));
        }
        let mut cache = cache_with(g);
        let mut scratch = NavScratch::new();
        let mut path = Vec::new();

        let stats = find_path(&mut cache, 0, (2, 10), (8, 10), &mut scratch, &mut path);

        assert!(!stats.best_effort, "a route exists through the gap");
        assert!(!path.is_empty());
        assert_eq!(*path.last().unwrap(), (8, 10), "must reach the goal");
        for c in &path {
            assert!(
                !(c.0 == 5 && c.1 >= 1),
                "path must never cross the wall, but it used {c:?}"
            );
        }
    }

    #[test]
    fn path_is_four_connected_so_the_resolver_can_always_take_it() {
        let mut cache = cache_with(open_grid());
        let mut scratch = NavScratch::new();
        let mut path = Vec::new();
        find_path(&mut cache, 0, (2, 2), (9, 7), &mut scratch, &mut path);

        let mut prev = (2, 2);
        for &c in &path {
            let step = (c.0 - prev.0).abs() + (c.1 - prev.1).abs();
            assert_eq!(
                step, 1,
                "every step must be axial and unit-length: {prev:?} -> {c:?}"
            );
            prev = c;
        }
    }

    /// NOTE for whoever writes the next nav test: a hand-authored grid only covers chunk (0,0), and
    /// the window spans ±60 m, so the NEIGHBOURING chunks are really generated and are mostly
    /// walkable. A wall drawn across one column is therefore NOT sealed — the search legitimately
    /// walks around it through the next chunk. To test unreachability the start has to be enclosed
    /// in a pocket that never touches the chunk border.
    #[test]
    fn unreachable_goal_returns_best_effort_toward_it() {
        let mut g = LayerGrid::new_solid();
        for x in 2..=6 {
            g.set(x, 2, Cell::new(CellType::Corridor, 2, 0)); // sealed 5-cell corridor
        }
        let mut cache = cache_with(g);
        let mut scratch = NavScratch::new();
        let mut path = Vec::new();

        let stats = find_path(&mut cache, 0, (2, 2), (18, 2), &mut scratch, &mut path);

        assert!(
            stats.best_effort,
            "goal is sealed off — must report best effort"
        );
        assert!(
            !path.is_empty(),
            "must still move toward the goal, not freeze"
        );
        let last = *path.last().unwrap();
        assert_eq!(
            last,
            (6, 2),
            "best effort must reach the far end of the pocket"
        );
        assert!(
            (last.0 - 18).abs() < (2 - 18_i32).abs(),
            "best effort must get us CLOSER to the goal than the start was"
        );
    }

    #[test]
    fn search_stays_inside_the_window() {
        let mut cache = cache_with(open_grid());
        let mut scratch = NavScratch::new();
        let mut path = Vec::new();
        // Goal far outside the window: the search must bound itself, not chase it.
        let stats = find_path(
            &mut cache,
            0,
            (0, 0),
            (NAV_WINDOW_CELLS * 10, 0),
            &mut scratch,
            &mut path,
        );
        assert!(stats.best_effort);
        for c in &path {
            assert!(
                c.0.abs() <= NAV_WINDOW_CELLS && c.1.abs() <= NAV_WINDOW_CELLS,
                "cell {c:?} escaped the window"
            );
        }
    }

    #[test]
    fn same_cell_goal_returns_empty_path() {
        let mut cache = cache_with(open_grid());
        let mut scratch = NavScratch::new();
        let mut path = Vec::new();
        let stats = find_path(&mut cache, 0, (4, 4), (4, 4), &mut scratch, &mut path);
        assert!(path.is_empty());
        assert_eq!(stats.expansions, 0);
    }

    /// ADR-040 D-COTA: a search that reads cells directly must leave the cache bounded. Before
    /// this, only `resolve_move_grid_gen` called `enforce_cap`, so a nav consumer would have grown
    /// the cache without limit.
    #[test]
    fn search_restores_the_chunk_cap() {
        use crate::world::grid_gen::collision::GRID_CACHE_MAX_CHUNKS;
        let mut cache = GridGenChunkCache::with_rules(42, default_layer_rules);
        let mut scratch = NavScratch::new();
        let mut path = Vec::new();
        // A long unreachable goal forces the search to sweep the whole window, touching many chunks.
        find_path(&mut cache, 0, (0, 0), (200, 200), &mut scratch, &mut path);
        assert!(
            cache.len() <= GRID_CACHE_MAX_CHUNKS,
            "nav left {} chunk-layers cached, cap is {}",
            cache.len(),
            GRID_CACHE_MAX_CHUNKS
        );
    }
}
