//! Boundary surface/opening finalization for the Phase 2.7 edge-wall model.
//!
//! Owns the edge-opening grammar shared by every level: which edge kinds count
//! as passable openings, perimeter opening bitmasks, sealing boundaries that
//! face missing neighbours, and reciprocal opening repair between neighbours.
//! Moved out of `generator.rs` in MIG-1; `generator` re-exports these for
//! existing call sites and tests.

use std::collections::{HashMap, HashSet};

use log::info;

use crate::utils::ChunkPos;
use crate::world::chunk::{
    Chunk, ChunkLayer, ChunkLayoutV1, EDGE_EAST, EDGE_KIND_ARCH, EDGE_KIND_DOOR, EDGE_KIND_OPEN,
    EDGE_KIND_WALL, EDGE_NORTH, EDGE_SOUTH, EDGE_WEST, LAYOUT_GRID_SIZE,
};
use crate::world::generator::StructureV0;

pub fn opposite_edge(edge: u8) -> u8 {
    match edge {
        EDGE_NORTH => EDGE_SOUTH,
        EDGE_EAST => EDGE_WEST,
        EDGE_SOUTH => EDGE_NORTH,
        EDGE_WEST => EDGE_EAST,
        _ => 0,
    }
}

pub fn edge_delta(edge: u8) -> ChunkPos {
    match edge {
        EDGE_NORTH => (0, -1),
        EDGE_EAST => (1, 0),
        EDGE_SOUTH => (0, 1),
        EDGE_WEST => (-1, 0),
        _ => (0, 0),
    }
}

/// Whether an edge kind is a passable boundary opening (open / door / arch).
/// Low walls, half walls, partitions and false doors are NOT openings.
pub fn edge_is_opening(kind: u8) -> bool {
    matches!(kind, EDGE_KIND_OPEN | EDGE_KIND_DOOR | EDGE_KIND_ARCH)
}

/// Recompute the perimeter opening bitmask (EDGE_NORTH/EAST/SOUTH/WEST) from
/// the layout's boundary edges.
pub fn perimeter_openings(layout: &ChunkLayoutV1) -> u8 {
    let g = layout.grid_size as usize;
    let mut openings = 0u8;
    for x in 0..g {
        if edge_is_opening(layout.edge_h(x, 0)) {
            openings |= EDGE_NORTH;
        }
        if edge_is_opening(layout.edge_h(x, g)) {
            openings |= EDGE_SOUTH;
        }
    }
    for z in 0..g {
        if edge_is_opening(layout.edge_v(0, z)) {
            openings |= EDGE_WEST;
        }
        if edge_is_opening(layout.edge_v(g, z)) {
            openings |= EDGE_EAST;
        }
    }
    openings
}

/// Boundary cells of `edge` whose outer edge is a passable opening.
/// Test-only: used by the `edges_connect` traversal check.
#[cfg(test)]
pub fn boundary_opening_cells(layout: &ChunkLayoutV1, edge: u8) -> Vec<(usize, usize)> {
    let g = layout.grid_size as usize;
    let mut out = Vec::new();
    match edge {
        EDGE_NORTH => {
            for x in 0..g {
                if edge_is_opening(layout.edge_h(x, 0)) && layout.is_cell_walkable(x, 0) {
                    out.push((x, 0));
                }
            }
        }
        EDGE_SOUTH => {
            for x in 0..g {
                if edge_is_opening(layout.edge_h(x, g)) && layout.is_cell_walkable(x, g - 1) {
                    out.push((x, g - 1));
                }
            }
        }
        EDGE_WEST => {
            for z in 0..g {
                if edge_is_opening(layout.edge_v(0, z)) && layout.is_cell_walkable(0, z) {
                    out.push((0, z));
                }
            }
        }
        EDGE_EAST => {
            for z in 0..g {
                if edge_is_opening(layout.edge_v(g, z)) && layout.is_cell_walkable(g - 1, z) {
                    out.push((g - 1, z));
                }
            }
        }
        _ => {}
    }
    out
}

pub fn open_center_boundary_gap(layout: &mut ChunkLayoutV1, edge: u8) {
    let g = LAYOUT_GRID_SIZE as usize;
    let a = g / 2 - 1;
    let b = g / 2;

    match edge {
        EDGE_NORTH => {
            layout.set_edge_h(a, 0, EDGE_KIND_OPEN);
            layout.set_edge_h(b, 0, EDGE_KIND_OPEN);
        }
        EDGE_EAST => {
            layout.set_edge_v(g, a, EDGE_KIND_OPEN);
            layout.set_edge_v(g, b, EDGE_KIND_OPEN);
        }
        EDGE_SOUTH => {
            layout.set_edge_h(a, g, EDGE_KIND_OPEN);
            layout.set_edge_h(b, g, EDGE_KIND_OPEN);
        }
        EDGE_WEST => {
            layout.set_edge_v(0, a, EDGE_KIND_OPEN);
            layout.set_edge_v(0, b, EDGE_KIND_OPEN);
        }
        _ => {}
    }
}

pub fn finalize_level0_edges(chunks: &mut [(StructureV0, Chunk)]) {
    let present: HashSet<(i32, ChunkLayer, i32)> = chunks.iter().map(|(_, c)| c.key()).collect();
    let g = LAYOUT_GRID_SIZE as usize;
    let mut sealed_boundaries = 0u32;

    for (_, chunk) in chunks.iter_mut() {
        let pos = chunk.pos;
        let layer = chunk.layer;
        // For each boundary, wall it off completely when the neighbour on the
        // far side does not exist. Setting every boundary edge to a full wall
        // also removes any doorway/arch gap that faced the missing neighbour.
        if !present.contains(&(pos.0, layer, pos.1 - 1)) {
            for x in 0..g {
                chunk.layout.set_edge_h(x, 0, EDGE_KIND_WALL);
            }
            sealed_boundaries += 1;
        }
        if !present.contains(&(pos.0, layer, pos.1 + 1)) {
            for x in 0..g {
                chunk.layout.set_edge_h(x, g, EDGE_KIND_WALL);
            }
            sealed_boundaries += 1;
        }
        if !present.contains(&(pos.0 + 1, layer, pos.1)) {
            for z in 0..g {
                chunk.layout.set_edge_v(g, z, EDGE_KIND_WALL);
            }
            sealed_boundaries += 1;
        }
        if !present.contains(&(pos.0 - 1, layer, pos.1)) {
            for z in 0..g {
                chunk.layout.set_edge_v(0, z, EDGE_KIND_WALL);
            }
            sealed_boundaries += 1;
        }

        chunk.layout.edge_openings = perimeter_openings(&chunk.layout);
    }

    // Ensure reciprocal openings between present same-layer neighbours.
    // Some authored special layouts, especially V30A connector/atrium chunks,
    // can have custom boundary openings. If one side opens toward an existing
    // neighbour, the opposite side must expose a matching centred opening too.
    let index: HashMap<(i32, ChunkLayer, i32), usize> = chunks
        .iter()
        .enumerate()
        .map(|(i, (_, c))| (c.key(), i))
        .collect();

    for i in 0..chunks.len() {
        let key = chunks[i].1.key();

        for edge in [EDGE_NORTH, EDGE_EAST, EDGE_SOUTH, EDGE_WEST] {
            let (dx, dz) = edge_delta(edge);
            let neighbor_key = (key.0 + dx, key.1, key.2 + dz);

            let Some(&j) = index.get(&neighbor_key) else {
                continue;
            };

            // Handle each pair once.
            if i >= j {
                continue;
            }

            let opposite = opposite_edge(edge);
            let a_open = chunks[i].1.layout.edge_openings & edge != 0;
            let b_open = chunks[j].1.layout.edge_openings & opposite != 0;

            if a_open || b_open {
                let (left, right) = chunks.split_at_mut(j);
                let (_, chunk_a) = &mut left[i];
                let (_, chunk_b) = &mut right[0];

                open_center_boundary_gap(&mut chunk_a.layout, edge);
                open_center_boundary_gap(&mut chunk_b.layout, opposite);

                chunk_a.layout.edge_openings = perimeter_openings(&chunk_a.layout);
                chunk_b.layout.edge_openings = perimeter_openings(&chunk_b.layout);
            }
        }
    }

    info!(
        "MPTRACE step=V27 event=level0_edges_finalized chunks={} sealed_boundaries={}",
        chunks.len(),
        sealed_boundaries
    );
}
