use serde::{Deserialize, Serialize};

use super::cell_flags::{CELL_BLOCKED, CELL_PILLAR, CELL_PIT, CELL_WALKABLE, CELL_WALL};
use super::coords::{ChunkLayer, LAYOUT_CELL_SIZE, LAYOUT_GRID_SIZE};
use super::edge_kinds::{
    EDGE_EAST, EDGE_KIND_ARCH, EDGE_KIND_BROKEN, EDGE_KIND_DOOR, EDGE_KIND_FALSE_DOOR,
    EDGE_KIND_HALF_WALL, EDGE_KIND_LOW_WALL, EDGE_KIND_OPEN, EDGE_KIND_PARTITION, EDGE_KIND_WALL,
    EDGE_NORTH, EDGE_SOUTH, EDGE_WEST, SIDE_EAST, SIDE_NORTH, SIDE_SOUTH,
};
use super::surface_profiles::{CEILING_NORMAL, FLOOR_FLAT, LIGHT_NORMAL, ZONE_NORMAL};

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum InterLayerVolumeKindV0 {
    AtriumStack,
    ServiceShaft,
    StackedCorridorPair,
    OverlookRoom,
    GiantPillarSpan,
    CeilingActivityZone,
    UnderfloorServiceZone,
}

impl InterLayerVolumeKindV0 {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::AtriumStack => "ATRIUM_STACK",
            Self::ServiceShaft => "SERVICE_SHAFT",
            Self::StackedCorridorPair => "STACKED_CORRIDOR_PAIR",
            Self::OverlookRoom => "OVERLOOK_ROOM",
            Self::GiantPillarSpan => "GIANT_PILLAR_SPAN",
            Self::CeilingActivityZone => "CEILING_ACTIVITY_ZONE",
            Self::UnderfloorServiceZone => "UNDERFLOOR_SERVICE_ZONE",
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct InterLayerVolumeV0 {
    pub volume_id: u32,
    pub kind: InterLayerVolumeKindV0,
    pub base_chunk: [i32; 2],
    pub involved_layers: Vec<ChunkLayer>,
    pub footprint_cell_min: [u8; 2],
    pub footprint_cell_max: [u8; 2],
    pub safety_type: String,
    pub future_audio_hint: String,
    pub visual_flags: u32,
    #[serde(default)]
    pub visual_hints: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ChunkLayoutV1 {
    pub grid_size: u8,
    pub cell_size: f32,
    pub cells: Vec<u16>,
    /// Bits: 0=north(z-), 1=east(x+), 2=south(z+), 3=west(x-).
    pub edge_openings: u8,
    pub macro_id: u32,
    pub zone_kind: u8,
    pub macro_local: [u8; 2],
    pub macro_size: [u8; 2],
    pub floor_level: i8,
    pub floor_profile: u8,
    pub ceiling_profile: u8,
    pub light_profile: u8,
    pub anomaly_flags: u16,
    pub vertical_flags: u16,
    /// Backend-authored cross-layer architectural volumes. These are render
    /// metadata only; movement/collision authority remains in the backend.
    #[serde(default)]
    pub inter_layer_volumes: Vec<InterLayerVolumeV0>,
    /// Vertical wall edges (run N–S, separate E–W neighbours).
    /// Indexed `z * (grid_size + 1) + bx`, `bx` in `0..=grid`, `z` in `0..grid`.
    /// `bx == 0` and `bx == grid` are the west/east chunk-boundary walls.
    #[serde(default)]
    pub edges_v: Vec<u8>,
    /// Horizontal wall edges (run E–W, separate N–S neighbours).
    /// Indexed `bz * grid_size + x`, `x` in `0..grid`, `bz` in `0..=grid`.
    /// `bz == 0` and `bz == grid` are the north/south chunk-boundary walls.
    #[serde(default)]
    pub edges_h: Vec<u8>,
}

impl ChunkLayoutV1 {
    pub fn new(cells: Vec<u16>, edge_openings: u8, zone_kind: u8) -> Self {
        let mut layout = Self {
            grid_size: LAYOUT_GRID_SIZE,
            cell_size: LAYOUT_CELL_SIZE,
            cells,
            edge_openings,
            macro_id: 0,
            zone_kind,
            macro_local: [0, 0],
            macro_size: [1, 1],
            floor_level: 0,
            floor_profile: FLOOR_FLAT,
            ceiling_profile: CEILING_NORMAL,
            light_profile: LIGHT_NORMAL,
            anomaly_flags: 0,
            vertical_flags: 0,
            inter_layer_volumes: Vec::new(),
            edges_v: Vec::new(),
            edges_h: Vec::new(),
        };
        layout.init_edges();
        layout
    }

    pub fn cell_index(&self, x: usize, z: usize) -> Option<usize> {
        let size = self.grid_size as usize;
        if x < size && z < size {
            Some(z * size + x)
        } else {
            None
        }
    }

    pub fn cell_flags(&self, x: usize, z: usize) -> u16 {
        self.cell_index(x, z)
            .and_then(|idx| self.cells.get(idx).copied())
            .unwrap_or(CELL_BLOCKED)
    }

    pub fn is_cell_walkable(&self, x: usize, z: usize) -> bool {
        let flags = self.cell_flags(x, z);
        flags & CELL_WALKABLE != 0
            && flags & (CELL_WALL | CELL_PILLAR | CELL_BLOCKED | CELL_PIT) == 0
    }

    // ─── Edge accessors (Phase 2.7) ───

    /// `true` once edge arrays are populated. Layouts deserialized from an
    /// older peer (no edge data) report `false`, and callers fall back to the
    /// legacy cell-based interpretation.
    pub fn has_edges(&self) -> bool {
        let g = self.grid_size as usize;
        self.edges_v.len() == (g + 1) * g && self.edges_h.len() == g * (g + 1)
    }

    /// Allocate edge arrays: all interior edges open, every perimeter edge a wall.
    pub fn init_edges(&mut self) {
        let g = self.grid_size as usize;
        self.edges_v = vec![EDGE_KIND_OPEN; (g + 1) * g];
        self.edges_h = vec![EDGE_KIND_OPEN; g * (g + 1)];
        for z in 0..g {
            self.set_edge_v(0, z, EDGE_KIND_WALL);
            self.set_edge_v(g, z, EDGE_KIND_WALL);
        }
        for x in 0..g {
            self.set_edge_h(x, 0, EDGE_KIND_WALL);
            self.set_edge_h(x, g, EDGE_KIND_WALL);
        }
    }

    pub fn edge_v(&self, bx: usize, z: usize) -> u8 {
        let g = self.grid_size as usize;
        if bx > g || z >= g {
            return EDGE_KIND_WALL;
        }
        self.edges_v
            .get(z * (g + 1) + bx)
            .copied()
            .unwrap_or(EDGE_KIND_OPEN)
    }

    pub fn edge_h(&self, x: usize, bz: usize) -> u8 {
        let g = self.grid_size as usize;
        if x >= g || bz > g {
            return EDGE_KIND_WALL;
        }
        self.edges_h
            .get(bz * g + x)
            .copied()
            .unwrap_or(EDGE_KIND_OPEN)
    }

    pub fn set_edge_v(&mut self, bx: usize, z: usize, kind: u8) {
        let g = self.grid_size as usize;
        if bx > g || z >= g {
            return;
        }
        if self.edges_v.len() != (g + 1) * g {
            self.edges_v = vec![EDGE_KIND_OPEN; (g + 1) * g];
        }
        self.edges_v[z * (g + 1) + bx] = kind;
    }

    pub fn set_edge_h(&mut self, x: usize, bz: usize, kind: u8) {
        let g = self.grid_size as usize;
        if x >= g || bz > g {
            return;
        }
        if self.edges_h.len() != g * (g + 1) {
            self.edges_h = vec![EDGE_KIND_OPEN; g * (g + 1)];
        }
        self.edges_h[bz * g + x] = kind;
    }

    /// The edge kind on `side` (N/E/S/W) of cell `(x, z)`.
    pub fn cell_side_edge(&self, x: usize, z: usize, side: u8) -> u8 {
        match side {
            SIDE_NORTH => self.edge_h(x, z),
            SIDE_EAST => self.edge_v(x + 1, z),
            SIDE_SOUTH => self.edge_h(x, z + 1),
            _ => self.edge_v(x, z),
        }
    }

    // ─── Edge-architecture metrics (Phase 2.7) ───
    //
    // In the edge-wall model walls/doors/arches live on cell *edges*, not in
    // cell centres, so layout "density" and "special architecture" counts must
    // read the edge arrays, not the cell flags.

    /// Total number of edge slots (vertical + horizontal), including the
    /// perimeter boundary edges.
    pub fn total_edge_count(&self) -> usize {
        self.edges_v.len() + self.edges_h.len()
    }

    /// Count every edge (vertical + horizontal) whose kind satisfies `pred`.
    pub fn count_edge_kinds<F: Fn(u8) -> bool>(&self, pred: F) -> usize {
        self.edges_v
            .iter()
            .chain(self.edges_h.iter())
            .filter(|&&k| pred(k))
            .count()
    }

    /// Solid, movement-blocking wall faces: full walls, partitions, low/half
    /// walls and false doors. (Doors and arches are passable transitions.)
    pub fn solid_edge_count(&self) -> usize {
        self.count_edge_kinds(|k| {
            matches!(
                k,
                EDGE_KIND_WALL
                    | EDGE_KIND_PARTITION
                    | EDGE_KIND_LOW_WALL
                    | EDGE_KIND_HALF_WALL
                    | EDGE_KIND_FALSE_DOOR
            )
        })
    }

    /// Passable architectural transitions: doors and arches.
    pub fn transition_edge_count(&self) -> usize {
        self.count_edge_kinds(|k| matches!(k, EDGE_KIND_DOOR | EDGE_KIND_ARCH))
    }

    /// Special (non-plain-wall, non-open) architectural edges: doors, arches,
    /// low/half walls, partitions, false doors and broken walls.
    pub fn special_edge_count(&self) -> usize {
        self.count_edge_kinds(|k| {
            matches!(
                k,
                EDGE_KIND_DOOR
                    | EDGE_KIND_ARCH
                    | EDGE_KIND_LOW_WALL
                    | EDGE_KIND_HALF_WALL
                    | EDGE_KIND_PARTITION
                    | EDGE_KIND_FALSE_DOOR
                    | EDGE_KIND_BROKEN
            )
        })
    }

    /// Cells carrying a centre blocker (pillar, pit or explicit block).
    pub fn blocked_cell_count(&self) -> usize {
        self.cells
            .iter()
            .filter(|&&f| f & (CELL_PILLAR | CELL_PIT | CELL_BLOCKED) != 0)
            .count()
    }
}

impl Default for ChunkLayoutV1 {
    fn default() -> Self {
        let size = LAYOUT_GRID_SIZE as usize;
        Self::new(
            vec![CELL_WALKABLE; size * size],
            EDGE_NORTH | EDGE_EAST | EDGE_SOUTH | EDGE_WEST,
            ZONE_NORMAL,
        )
    }
}
