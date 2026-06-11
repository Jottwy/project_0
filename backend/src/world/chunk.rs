//! Chunk state, ownership, and teleportation.
//! See ARCHITECTURE_V1.md §3 and §7.2.
//!
//! Phase 1 scaffolding: full data model is present; simulation (teleport timer,
//! ownership transfer) lands in Phase 2.

use serde::{Deserialize, Serialize};

use crate::network::PeerId;
use crate::utils::ChunkPos;
use crate::world::entity::Entity;

pub type ChunkLayer = i8;
pub type LayeredChunkPos = (i32, ChunkLayer, i32);

pub const LAYOUT_GRID_SIZE: u8 = 10;
pub const LAYOUT_CELL_SIZE: f32 = 5.0;

pub const EDGE_NORTH: u8 = 1 << 0;
pub const EDGE_EAST: u8 = 1 << 1;
pub const EDGE_SOUTH: u8 = 1 << 2;
pub const EDGE_WEST: u8 = 1 << 3;

pub const CELL_WALKABLE: u16 = 1 << 0;
pub const CELL_WALL: u16 = 1 << 1;
pub const CELL_PILLAR: u16 = 1 << 2;
pub const CELL_BLOCKED: u16 = 1 << 3;
pub const CELL_HAZARD: u16 = 1 << 4;
pub const CELL_RAMP: u16 = 1 << 5;
pub const CELL_PIT: u16 = 1 << 6;
pub const CELL_SHALLOW_FLUID: u16 = 1 << 7;
pub const CELL_SAFE: u16 = 1 << 8;
pub const CELL_ANOMALY: u16 = 1 << 9;
pub const CELL_DOOR: u16 = 1 << 10;
pub const CELL_ARCH: u16 = 1 << 11;
pub const CELL_LOW_WALL: u16 = 1 << 12;
pub const CELL_HALF_WALL: u16 = 1 << 13;
pub const CELL_THIN_PARTITION: u16 = 1 << 14;
pub const CELL_FALSE_DOOR: u16 = 1 << 15;

pub const ZONE_NORMAL: u8 = 0;
pub const ZONE_STORAGE: u8 = 1;
pub const ZONE_SAFE: u8 = 2;
pub const ZONE_DANGER: u8 = 3;
pub const ZONE_OPEN_HALL: u8 = 4;
pub const ZONE_PILLAR_HALL: u8 = 5;
pub const ZONE_HUMID: u8 = 6;
pub const ZONE_BLACKOUT: u8 = 7;
pub const ZONE_MANILA: u8 = 8;
pub const ZONE_CLEANING: u8 = 9;
pub const ZONE_RED: u8 = 10;
pub const ZONE_PIT: u8 = 11;

pub const FLOOR_FLAT: u8 = 0;
pub const FLOOR_SUNKEN: u8 = 1;
pub const FLOOR_RAISED: u8 = 2;
pub const FLOOR_RAMP_NORTH_SOUTH: u8 = 3;
pub const FLOOR_RAMP_EAST_WEST: u8 = 4;
pub const FLOOR_PIT_PLACEHOLDER: u8 = 5;
pub const FLOOR_STAIRS_NORTH_SOUTH: u8 = 6;
pub const FLOOR_STAIRS_EAST_WEST: u8 = 7;
// Phase 3.0A — layer connectors. These span a full `LAYER_HEIGHT` along +Z so a
// player walks the whole vertical distance between two stacked layers without
// any free fall. UP rises toward the north (z+) edge, DOWN descends toward it.
pub const FLOOR_CONNECTOR_UP: u8 = 8;
pub const FLOOR_CONNECTOR_DOWN: u8 = 9;

/// Phase 3.0A — vertical separation between adjacent macro layers, in metres.
/// `chunk_root_y = layer * LAYER_HEIGHT`. Layer 0 is normal Level 0 ground.
pub const LAYER_HEIGHT: f32 = 7.0;

pub const V30A_STACKED_CORRIDOR: u16 = 1 << 8;
pub const V30A_LOWER_SERVICE_BRANCH: u16 = 1 << 9;
pub const V30A_UPPER_OFFICE_BRANCH: u16 = 1 << 10;
pub const V30A_ATRIUM_VOID_ROOM: u16 = 1 << 11;
pub const V30A_DEEP_PRECIPICE_PLACEHOLDER: u16 = 1 << 12;
pub const V30A_GIANT_PILLAR_HALL: u16 = 1 << 13;
pub const V30A_CONNECTOR: u16 = 1 << 14;
pub const V30A_BLOCKED_VERTICAL_SHAFT: u16 = 1 << 15;

pub const VOLUME_VIS_ATRIUM_WALLS: u32 = 1 << 0;
pub const VOLUME_VIS_LOWER_ROOM_VISIBLE: u32 = 1 << 1;
pub const VOLUME_VIS_SHAFT_WALLS: u32 = 1 << 2;
pub const VOLUME_VIS_RAILINGS: u32 = 1 << 3;
pub const VOLUME_VIS_RIM_TRIMS: u32 = 1 << 4;
pub const VOLUME_VIS_PILLAR_SPANS: u32 = 1 << 5;
pub const VOLUME_VIS_CEILING_HINTS: u32 = 1 << 6;
pub const VOLUME_VIS_UNDERFLOOR_HINTS: u32 = 1 << 7;
pub const VOLUME_VIS_STACKED_ALIGNMENT: u32 = 1 << 8;
pub const VOLUME_VIS_DEPTH_CUES: u32 = 1 << 9;

pub fn layered_chunk_pos(pos: ChunkPos, layer: ChunkLayer) -> LayeredChunkPos {
    (pos.0, layer, pos.1)
}

pub fn layer_y(layer: ChunkLayer) -> f32 {
    layer as f32 * LAYER_HEIGHT
}

pub const CEILING_NORMAL: u8 = 0;
pub const CEILING_LOW_SERVICE: u8 = 1;
pub const CEILING_TALL_HALL: u8 = 2;
pub const CEILING_DAMAGED: u8 = 3;

pub const LIGHT_NORMAL: u8 = 0;
pub const LIGHT_DIM: u8 = 1;
pub const LIGHT_BLACKOUT: u8 = 2;
pub const LIGHT_RED: u8 = 3;
pub const LIGHT_WARM: u8 = 4;

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

// ─── Edge-wall model (Phase 2.7) ───
//
// Walls, doors, arches and partitions live on the *boundary* between two cells,
// not as whole blocked cells. This removes 5m-thick "wall cells", double walls,
// and doorframes floating in cell centres. Cells themselves stay floor modules
// (walkable, or carrying a centre prop like a pillar/pit).
pub const EDGE_KIND_OPEN: u8 = 0;
pub const EDGE_KIND_WALL: u8 = 1;
pub const EDGE_KIND_DOOR: u8 = 2;
pub const EDGE_KIND_ARCH: u8 = 3;
pub const EDGE_KIND_LOW_WALL: u8 = 4;
pub const EDGE_KIND_HALF_WALL: u8 = 5;
pub const EDGE_KIND_PARTITION: u8 = 6;
pub const EDGE_KIND_FALSE_DOOR: u8 = 7;
pub const EDGE_KIND_BROKEN: u8 = 8;

/// Cell side indices used by `cell_side_edge`.
pub const SIDE_NORTH: u8 = 0;
pub const SIDE_EAST: u8 = 1;
pub const SIDE_SOUTH: u8 = 2;
pub const SIDE_WEST: u8 = 3;

/// Whether an edge kind blocks player movement across the boundary.
pub fn edge_blocks_movement(kind: u8) -> bool {
    matches!(
        kind,
        EDGE_KIND_WALL
            | EDGE_KIND_LOW_WALL
            | EDGE_KIND_HALF_WALL
            | EDGE_KIND_PARTITION
            | EDGE_KIND_FALSE_DOOR
    )
}

/// Whether an edge kind is rendered as a full-height solid wall face.
pub fn edge_is_full_wall(kind: u8) -> bool {
    matches!(
        kind,
        EDGE_KIND_WALL | EDGE_KIND_PARTITION | EDGE_KIND_FALSE_DOOR
    )
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

    /// Combined architectural density: solid + transition edges plus blocked
    /// cells, over the total number of edges and cells. Meaningful for the
    /// edge-wall model where most architecture lives on edges, not cells.
    pub fn edge_architecture_density(&self) -> f32 {
        let arch =
            self.solid_edge_count() + self.transition_edge_count() + self.blocked_cell_count();
        let denom = (self.total_edge_count() + self.cells.len()).max(1);
        arch as f32 / denom as f32
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

/// Lifecycle of a chunk in the distributed world (ARCHITECTURE_V1.md §3.4).
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ChunkState {
    Unloaded,
    Dormant { cached_by: Vec<PeerId> },
    Active { stabilized: bool, anchored: bool },
}

impl ChunkState {
    /// String id reported to Unity (`random` / `stabilized` / `anchored`).
    pub fn render_name(&self) -> &'static str {
        match self {
            ChunkState::Active { anchored: true, .. } => "anchored",
            ChunkState::Active {
                stabilized: true, ..
            } => "stabilized",
            ChunkState::Active { .. } => "random",
            _ => "random",
        }
    }
}

/// A dropped item lying in the world (lost if the chunk teleports).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DroppedItem {
    pub id: u32,
    pub item: crate::player::inventory::Item,
    pub quantity: u16,
    pub position: crate::utils::Vec3,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Chunk {
    pub pos: ChunkPos,
    #[serde(default)]
    pub layer: ChunkLayer,
    pub state: ChunkState,
    pub seed: u64,
    pub owner: Option<PeerId>,
    pub entities: Vec<Entity>,
    pub items: Vec<DroppedItem>,
    pub teleport_timer: f32,
    pub template_id: u8,
    pub rotation: u16, // 0, 90, 180, 270
    pub mirrored: bool,
    pub has_workbench: bool,
    pub layout: ChunkLayoutV1,
}

impl Chunk {
    pub fn is_active(&self) -> bool {
        matches!(self.state, ChunkState::Active { .. })
    }

    pub fn key(&self) -> LayeredChunkPos {
        layered_chunk_pos(self.pos, self.layer)
    }
}
