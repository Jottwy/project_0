use std::collections::HashSet;

use crate::utils::ChunkPos;
use crate::world::chunk::{
    Chunk, ChunkLayoutV1, CELL_BLOCKED, CELL_PILLAR, CELL_PIT, CELL_WALKABLE, CELL_WALL,
    EDGE_KIND_ARCH, EDGE_KIND_DOOR, LAYOUT_GRID_SIZE, ZONE_BLACKOUT, ZONE_CLEANING, ZONE_MANILA,
    ZONE_OPEN_HALL, ZONE_RED, ZONE_SAFE,
};
use crate::world::generator::{generate_initial_structure_chunks, structure_bounds, StructureV0};

// ─── Spawn helpers ───

pub fn export_level0_ascii(world_seed: u64) -> String {
    let generated = generate_initial_structure_chunks(world_seed);
    if generated.is_empty() {
        return String::new();
    }

    let positions: Vec<ChunkPos> = generated.iter().map(|(_, c)| c.pos).collect();
    let (min_x, min_z, max_x, max_z) = structure_bounds(&positions);
    let grid = LAYOUT_GRID_SIZE as i32;
    let width = (max_x - min_x + 1) * grid;
    let height = (max_z - min_z + 1) * grid;
    let mut canvas = vec![vec![' '; width as usize]; height as usize];

    for (_, chunk) in &generated {
        for z in 0..LAYOUT_GRID_SIZE as usize {
            for x in 0..LAYOUT_GRID_SIZE as usize {
                let flags = chunk.layout.cell_flags(x, z);
                let symbol = cell_symbol(flags, chunk.layout.zone_kind);
                let gx = (chunk.pos.0 - min_x) * grid + x as i32;
                let gz = (chunk.pos.1 - min_z) * grid + z as i32;
                canvas[gz as usize][gx as usize] = symbol;
            }
        }
    }

    let mut out = format!(
        "Level0 seed={} chunks={} bounds=({},{})->({},{})\n",
        world_seed,
        positions.len(),
        min_x,
        min_z,
        max_x,
        max_z
    );
    out.push_str("== floor/zone overview (1 char per 5m cell; #=blocked *=pillar P=pit ~=fluid S=spawn) ==\n");
    for row in &canvas {
        out.extend(row.iter());
        out.push('\n');
    }

    // Cell-edge detail for a representative set of chunks (Phase 2.7).
    out.push_str(
        "\n== cell-edge detail: |,- wall  d door  a arch  : low/half wall  x false door  '.' floor ==\n",
    );
    for pos in sample_chunks_for_ascii(&generated) {
        if let Some((_, chunk)) = generated.iter().find(|(_, c)| c.pos == pos) {
            out.push_str(&format!(
                "\n-- chunk ({},{}) template={} zone={} openings={:04b} --\n",
                pos.0, pos.1, chunk.template_id, chunk.layout.zone_kind, chunk.layout.edge_openings
            ));
            out.push_str(&render_chunk_maze(&chunk.layout));
        }
    }
    out
}

/// Pick the four starter chunks plus a handful of distinct-template chunks so
/// the maze detail shows real variety.
fn sample_chunks_for_ascii(generated: &[(StructureV0, Chunk)]) -> Vec<ChunkPos> {
    let mut out = vec![(0, 0), (1, 0), (0, 1), (1, 1)];
    let mut seen_templates: HashSet<u8> = HashSet::new();
    for (_, chunk) in generated {
        if out.contains(&chunk.pos) {
            continue;
        }
        if seen_templates.insert(chunk.template_id) && out.len() < 12 {
            out.push(chunk.pos);
        }
    }
    out
}

fn render_chunk_maze(layout: &ChunkLayoutV1) -> String {
    let g = layout.grid_size as usize;
    let w = 2 * g + 1;
    let h = 2 * g + 1;
    let mut rows = vec![vec![' '; w]; h];
    for gz in (0..h).step_by(2) {
        for gx in (0..w).step_by(2) {
            rows[gz][gx] = '+';
        }
    }
    for z in 0..g {
        for x in 0..g {
            let cx = 2 * x + 1;
            let cz = 2 * z + 1;
            rows[cz][cx] = cell_symbol(layout.cell_flags(x, z), layout.zone_kind);
            rows[cz][2 * x] = edge_char(layout.edge_v(x, z));
            rows[cz][2 * x + 2] = edge_char(layout.edge_v(x + 1, z));
            rows[2 * z][cx] = edge_char(layout.edge_h(x, z));
            rows[2 * z + 2][cx] = edge_char(layout.edge_h(x, z + 1));
        }
    }
    let mut s = String::new();
    for row in rows {
        s.extend(row.iter());
        s.push('\n');
    }
    s
}

/// Symbol for one cell edge, vertical or horizontal alike: the orientation is
/// already carried by the position in the maze grid, not by the character, so
/// both directions share this alphabet on purpose. If a future revision ever
/// needs distinct symbols per orientation, split it again here — the golden
/// slices (`level0_golden_slice.rs`) pin every char this returns.
fn edge_char(kind: u8) -> char {
    match kind {
        EDGE_KIND_DOOR => 'd',
        EDGE_KIND_ARCH => 'a',
        _ => ' ',
    }
}

fn cell_symbol(flags: u16, zone_kind: u8) -> char {
    if flags & CELL_PIT != 0 {
        'P'
    } else if flags & CELL_PILLAR != 0 {
        '*'
    } else if flags & (CELL_BLOCKED | CELL_WALL) != 0 {
        '#'
    } else if flags & CELL_WALKABLE != 0 {
        match zone_kind {
            ZONE_BLACKOUT => 'B',
            ZONE_RED => 'R',
            ZONE_MANILA => 'M',
            ZONE_CLEANING => 'C',
            ZONE_SAFE => 'S',
            ZONE_OPEN_HALL => 'O',
            _ => '.',
        }
    } else {
        ' '
    }
}
