//! Volumetric "Rubik grid" world model V0 (backend-authored 3D architecture).
//!
//! This module replaces the old decorative "flat layers + holes + props"
//! verticality pipeline (the Phase 3.0A2 `inter_layer_volumes` VISFIX overlay)
//! with a real volumetric cell model. Architecture is authored as a 3D grid of
//! occupancy cells (`cell_x`, `cell_y`/layer, `cell_z`) and the renderable
//! surfaces are *derived deterministically* from 6-direction neighbour state —
//! never from disconnected validation props.
//!
//! Authority model is unchanged: this is render metadata only. Movement,
//! collision, items and sync stay authoritative in the rest of the backend.
//! Unity consumes [`VolumetricGridViewV0`] over IPC and renders the faces.
//!
//! Coordinate convention (matches the rest of the world):
//!   * world is Y-up, 1 unit = 1 metre;
//!   * a cell is `cell_size_xz` wide in X/Z and `layer_height` tall in Y;
//!   * cell `(x, y, z)` min corner = `origin_world + (x*cs, y*lh, z*lh)`;
//!   * grid layer `y` maps to world macro layer `base_layer + y`.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;

use log::info;
use serde::{Deserialize, Serialize};

use crate::utils::{ChunkPos, CHUNK_SIZE};
use crate::world::chunk::{
    edge_is_full_wall, Chunk, ChunkLayer, ChunkLayoutV1, CELL_ANOMALY, CELL_BLOCKED, CELL_HAZARD,
    CELL_PILLAR, CELL_PIT, CELL_SAFE, CELL_WALKABLE, EDGE_KIND_HALF_WALL, EDGE_KIND_LOW_WALL,
    FLOOR_CONNECTOR_DOWN, FLOOR_CONNECTOR_UP, FLOOR_PIT_PLACEHOLDER, LAYER_HEIGHT,
    LAYOUT_CELL_SIZE, LAYOUT_GRID_SIZE, V30A_CONNECTOR, ZONE_BLACKOUT, ZONE_CLEANING, ZONE_DANGER,
    ZONE_HUMID, ZONE_MANILA, ZONE_OPEN_HALL, ZONE_PILLAR_HALL, ZONE_PIT, ZONE_RED, ZONE_SAFE,
    ZONE_STORAGE,
};

/// World seed that gets the guaranteed near-spawn volumetric showcase.
pub const SHOWCASE_SEED: u64 = 7778;

pub const UNIFIED_COLUMN_SOURCE_LEVEL0: &str = "LEVEL0_ADAPTER";
pub const UNIFIED_COLUMN_SOURCE_RUBIKGRID: &str = "RUBIKGRID_ADAPTER";
pub const UNIFIED_COLUMN_SOURCE_V30A: &str = "INTER_LAYER_ADAPTER";

// Phase 3.0B2 — multi-chunk showcase. The architecture is authored as ONE
// global occupancy grid spanning a small 2×2 chunk block near spawn; each chunk
// renders its own 10×10 window of that global grid, so adjacent showcase chunks
// connect seamlessly (no double walls at the seam) and the outer boundary walls
// it off cleanly against normal Level 0.
const CELLS_PER_CHUNK: i32 = 10; // 10 cells * 5 m = 50 m = CHUNK_SIZE
const SHOWCASE_NY: i32 = 3; // stacked macro layers (-1, 0, +1)
const SHOWCASE_BASE_LAYER: i8 = -1;
/// Showcase chunk block: 2×2 chunks anchored at the spawn chunk (0,0).
const SHOWCASE_CHUNKS: [ChunkPos; 4] = [(0, 0), (1, 0), (0, 1), (1, 1)];
const GNX: i32 = 20; // 2 chunks * 10 cells
const GNZ: i32 = 20;

// ─── Global plan regions (global cell coords gx in 0..GNX, gz in 0..GNZ) ───
//
// Central 4×4 atrium void, wrapped by a 1-cell corridor ring, with support-core
// pillars at the ring corners. An enclosed service shaft sits to the NE, a
// closed (divided) room cluster to the SE, and a small service nook to the SW.
const ATRIUM_X: (i32, i32) = (8, 11);
const ATRIUM_Z: (i32, i32) = (10, 13);
const RING_X: (i32, i32) = (7, 12);
const RING_Z: (i32, i32) = (9, 14);
const CLUSTER_X: (i32, i32) = (13, 19);
const CLUSTER_Z: (i32, i32) = (0, 8);
const SHAFT_BLOCK_X: (i32, i32) = (16, 18);
const SHAFT_BLOCK_Z: (i32, i32) = (15, 17);
const SERVICE_NOOK_X: (i32, i32) = (0, 1);
const SERVICE_NOOK_Z: (i32, i32) = (0, 3);

/// Clean doorways punched through the otherwise-solid showcase perimeter so the
/// player can read a transition into normal Level 0. `(gx, gy, gz, dir)`.
const TRANSITION_DOORWAYS: [(i32, i32, i32, FaceDir); 2] = [
    (5, 1, 0, FaceDir::South),   // south entry from chunk (0,-1)
    (14, 1, 19, FaceDir::North), // north exit toward chunk (1,2)
];

// ─── Unified volumetric world columns V0 ───

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct VolumetricColumnCoord {
    pub x: i32,
    pub z: i32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum LayerBandProfileV0 {
    Level0Classic,
    Level0UpperFalseCeiling,
    Level0UpperOffice,
    Level0RemodeledOffice,
    CeilingServiceVoid,
    UnderfloorService,
    ConcreteSublevel,
    RedDangerZone,
    DarkLobby,
    ManilaSafeNode,
    RemodeledMess,
    SealedArchitecture,
    MegastructureHint,
    VoidOrExtremeAnomaly,
}

impl LayerBandProfileV0 {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Level0Classic => "LEVEL0_CLASSIC",
            Self::Level0UpperFalseCeiling => "LEVEL0_UPPER_FALSE_CEILING",
            Self::Level0UpperOffice => "LEVEL0_UPPER_OFFICE",
            Self::Level0RemodeledOffice => "LEVEL0_REMODELED_OFFICE",
            Self::CeilingServiceVoid => "CEILING_SERVICE_VOID",
            Self::UnderfloorService => "UNDERFLOOR_SERVICE",
            Self::ConcreteSublevel => "CONCRETE_SUBLEVEL",
            Self::RedDangerZone => "RED_DANGER_ZONE",
            Self::DarkLobby => "DARK_LOBBY",
            Self::ManilaSafeNode => "MANILA_SAFE_NODE",
            Self::RemodeledMess => "REMODELED_MESS",
            Self::SealedArchitecture => "SEALED_ARCHITECTURE",
            Self::MegastructureHint => "MEGASTRUCTURE_HINT",
            Self::VoidOrExtremeAnomaly => "VOID_OR_EXTREME_ANOMALY",
        }
    }

    pub fn code(self) -> u8 {
        match self {
            Self::Level0Classic => 0,
            Self::Level0UpperFalseCeiling => 1,
            Self::Level0UpperOffice => 2,
            Self::Level0RemodeledOffice => 3,
            Self::CeilingServiceVoid => 4,
            Self::UnderfloorService => 5,
            Self::ConcreteSublevel => 6,
            Self::RedDangerZone => 7,
            Self::DarkLobby => 8,
            Self::ManilaSafeNode => 9,
            Self::RemodeledMess => 10,
            Self::SealedArchitecture => 11,
            Self::MegastructureHint => 12,
            Self::VoidOrExtremeAnomaly => 13,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum VerticalAccessTypeV0 {
    None,
    Atrium,
    Shaft,
    CollapsedFloor,
    BrokenCeiling,
    Vent,
    Stairwell,
    ElevatorPlaceholder,
    NoclipAnomaly,
    ManilaTransition,
    RedRoomThreshold,
    RemodeledDoor,
    // Phase 3.0D — explicit vertical *relationship* semantics. These nodes
    // describe how stacked bands relate structurally; unlike the opening types
    // above they never carve voids into the grid.
    SealedAbove,
    SealedBelow,
    SharedFloorCeiling,
    ServiceRampPlaceholder,
    BrokenFloorPlaceholder,
    FalseCeilingAccess,
    SupportCoreContinuation,
}

impl VerticalAccessTypeV0 {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::None => "NONE",
            Self::Atrium => "ATRIUM",
            Self::Shaft => "SHAFT",
            Self::CollapsedFloor => "COLLAPSED_FLOOR",
            Self::BrokenCeiling => "BROKEN_CEILING",
            Self::Vent => "VENT",
            Self::Stairwell => "STAIRWELL",
            Self::ElevatorPlaceholder => "ELEVATOR_PLACEHOLDER",
            Self::NoclipAnomaly => "NOCLIP_ANOMALY",
            Self::ManilaTransition => "MANILA_TRANSITION",
            Self::RedRoomThreshold => "RED_ROOM_THRESHOLD",
            Self::RemodeledDoor => "REMODELED_DOOR",
            Self::SealedAbove => "SEALED_ABOVE",
            Self::SealedBelow => "SEALED_BELOW",
            Self::SharedFloorCeiling => "SHARED_FLOOR_CEILING",
            Self::ServiceRampPlaceholder => "SERVICE_RAMP_PLACEHOLDER",
            Self::BrokenFloorPlaceholder => "BROKEN_FLOOR_PLACEHOLDER",
            Self::FalseCeilingAccess => "FALSE_CEILING_ACCESS",
            Self::SupportCoreContinuation => "SUPPORT_CORE_CONTINUATION",
        }
    }

    /// Opening-type accesses carve a real bounded vertical void into the grid.
    /// Relationship-type accesses (sealed/shared/placeholder markers) are
    /// explicit metadata only and never punch holes.
    pub fn is_opening(self) -> bool {
        matches!(
            self,
            Self::Atrium
                | Self::Shaft
                | Self::CollapsedFloor
                | Self::BrokenCeiling
                | Self::Vent
                | Self::Stairwell
                | Self::ElevatorPlaceholder
                | Self::NoclipAnomaly
        )
    }

    pub fn code(self) -> u8 {
        match self {
            Self::None => 0,
            Self::Atrium => 1,
            Self::Shaft => 2,
            Self::CollapsedFloor => 3,
            Self::BrokenCeiling => 4,
            Self::Vent => 5,
            Self::Stairwell => 6,
            Self::ElevatorPlaceholder => 7,
            Self::NoclipAnomaly => 8,
            Self::ManilaTransition => 9,
            Self::RedRoomThreshold => 10,
            Self::RemodeledDoor => 11,
            Self::SealedAbove => 12,
            Self::SealedBelow => 13,
            Self::SharedFloorCeiling => 14,
            Self::ServiceRampPlaceholder => 15,
            Self::BrokenFloorPlaceholder => 16,
            Self::FalseCeilingAccess => 17,
            Self::SupportCoreContinuation => 18,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum DangerProfileV0 {
    None,
    Low,
    RedPocket,
    Blackout,
    ExtremeAnomaly,
}

impl DangerProfileV0 {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::None => "NONE",
            Self::Low => "LOW",
            Self::RedPocket => "RED_POCKET",
            Self::Blackout => "BLACKOUT",
            Self::ExtremeAnomaly => "EXTREME_ANOMALY",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum ResourceProfileV0 {
    None,
    Sparse,
    StorageHint,
    SafeNode,
}

impl ResourceProfileV0 {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::None => "NONE",
            Self::Sparse => "SPARSE",
            Self::StorageHint => "STORAGE_HINT",
            Self::SafeNode => "SAFE_NODE",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum AnomalyProfileV0 {
    None,
    DesaturatedTransition,
    ManilaHint,
    RedRoomHint,
    MegastructureHint,
}

impl AnomalyProfileV0 {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::None => "NONE",
            Self::DesaturatedTransition => "DESATURATED_TRANSITION",
            Self::ManilaHint => "MANILA_HINT",
            Self::RedRoomHint => "RED_ROOM_HINT",
            Self::MegastructureHint => "MEGASTRUCTURE_HINT",
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct LayerBand {
    pub band_id: u32,
    pub layer: ChunkLayer,
    pub profile: LayerBandProfileV0,
    pub accessible: bool,
    pub danger_profile: DangerProfileV0,
    pub resource_profile: ResourceProfileV0,
    pub anomaly_profile: AnomalyProfileV0,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct VerticalAccessNode {
    pub access_id: u32,
    pub access_type: VerticalAccessTypeV0,
    pub from_layer: ChunkLayer,
    pub to_layer: ChunkLayer,
    pub footprint_cell_min: [u8; 2],
    pub footprint_cell_max: [u8; 2],
    pub explicit: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct VolumetricColumn {
    pub column_id: u64,
    pub coord: VolumetricColumnCoord,
    pub source: String,
    pub bands: Vec<LayerBand>,
    pub grid: VolumetricGridV0,
    pub vertical_access: Vec<VerticalAccessNode>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct VolumetricWorld {
    pub world_seed: u64,
    pub columns: Vec<VolumetricColumn>,
}

// ─── Cell occupancy ───

/// What a single volumetric cell contains. Drives both face generation and the
/// renderer's material/element choices.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum CellOccupancy {
    /// Filled structural mass. Blocks movement and sight; emits no faces of its
    /// own (adjacent open cells emit the walls/floors/ceilings against it).
    Solid,
    /// A walkable enclosed room.
    Room,
    /// A walkable corridor.
    Corridor,
    /// Open vertical void forming an atrium (open-sided, railed).
    AtriumVoid,
    /// Open vertical void forming an enclosed service/utility shaft.
    Shaft,
    /// A walkable low service space (sub-floor / plenum walkway).
    ServiceSpace,
    /// A structural support core: a column that rises through stacked layers.
    SupportCore,
    /// Explicitly blocked cell (reserved, impassable). Treated like `Solid`.
    Blocked,
    /// Closed but architecturally real room. It can render as a sealed mass
    /// behind walls without becoming reachable in V0.
    SealedRoom,
    /// False architectural volume: inaccessible cavity or misleading space.
    FalseSpace,
    /// Plenum / false-ceiling void above the main Level 0 band.
    CeilingVoid,
    /// Underfloor technical mass below the main Level 0 band.
    UnderfloorService,
    /// Transition threshold between profiles.
    Transition,
    /// Explicit anomaly cell.
    Anomaly,
    /// Dangerous room/pocket representation.
    DangerZone,
    /// Safe-node architectural placeholder.
    SafeNode,
}

impl CellOccupancy {
    pub fn code(self) -> u8 {
        match self {
            CellOccupancy::Solid => 0,
            CellOccupancy::Room => 1,
            CellOccupancy::Corridor => 2,
            CellOccupancy::AtriumVoid => 3,
            CellOccupancy::Shaft => 4,
            CellOccupancy::ServiceSpace => 5,
            CellOccupancy::SupportCore => 6,
            CellOccupancy::Blocked => 7,
            CellOccupancy::SealedRoom => 8,
            CellOccupancy::FalseSpace => 9,
            CellOccupancy::CeilingVoid => 10,
            CellOccupancy::UnderfloorService => 11,
            CellOccupancy::Transition => 12,
            CellOccupancy::Anomaly => 13,
            CellOccupancy::DangerZone => 14,
            CellOccupancy::SafeNode => 15,
        }
    }

    /// Filled mass: a wall/floor/ceiling forms on the open side of a boundary
    /// with one of these. Out-of-grid neighbours are treated as `Solid` so the
    /// volume is fully enclosed (perimeter walls, ground floor, roof).
    pub fn is_filled(self) -> bool {
        matches!(self, CellOccupancy::Solid | CellOccupancy::Blocked)
    }

    /// Blocks horizontal sight/movement → a wall face forms against it.
    /// Support cores do *not* block horizontally (the room stays open and the
    /// column is drawn as a centred structural element instead of a 5 m wall).
    pub fn blocks_horizontally(self) -> bool {
        self.is_filled() || matches!(self, CellOccupancy::SealedRoom | CellOccupancy::FalseSpace)
    }

    /// A genuine vertical void: the floor/ceiling slab between two such cells is
    /// omitted so the shaft/atrium reads as continuous empty volume.
    pub fn is_vertical_void(self) -> bool {
        matches!(self, CellOccupancy::AtriumVoid | CellOccupancy::Shaft)
    }

    /// A cell a player can stand in (gets railings around adjacent voids).
    pub fn is_walkable(self) -> bool {
        matches!(
            self,
            CellOccupancy::Room
                | CellOccupancy::Corridor
                | CellOccupancy::ServiceSpace
                | CellOccupancy::Transition
                | CellOccupancy::DangerZone
                | CellOccupancy::SafeNode
        )
    }

    /// "Open" for the open/solid census (walkable space + vertical voids).
    pub fn is_open(self) -> bool {
        self.is_walkable()
            || self.is_vertical_void()
            || matches!(
                self,
                CellOccupancy::SealedRoom
                    | CellOccupancy::FalseSpace
                    | CellOccupancy::CeilingVoid
                    | CellOccupancy::UnderfloorService
                    | CellOccupancy::Anomaly
            )
    }

    /// Cells that own surface faces (everything except pure filled mass).
    pub fn emits_surfaces(self) -> bool {
        !self.is_filled()
    }
}

// ─── Face model ───

/// One of the six axis-aligned neighbour directions.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FaceDir {
    North, // +Z
    South, // -Z
    East,  // +X
    West,  // -X
    Up,    // +Y
    Down,  // -Y
}

impl FaceDir {
    pub fn code(self) -> u8 {
        match self {
            FaceDir::North => 0,
            FaceDir::South => 1,
            FaceDir::East => 2,
            FaceDir::West => 3,
            FaceDir::Up => 4,
            FaceDir::Down => 5,
        }
    }

    /// `(dx, dy, dz)` offset to the neighbour in this direction.
    pub fn delta(self) -> (i32, i32, i32) {
        match self {
            FaceDir::North => (0, 0, 1),
            FaceDir::South => (0, 0, -1),
            FaceDir::East => (1, 0, 0),
            FaceDir::West => (-1, 0, 0),
            FaceDir::Up => (0, 1, 0),
            FaceDir::Down => (0, -1, 0),
        }
    }

    const HORIZONTAL: [FaceDir; 4] = [FaceDir::North, FaceDir::South, FaceDir::East, FaceDir::West];
}

/// The kind of renderable surface a face represents.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FaceKind {
    /// Solid vertical wall against filled mass.
    Wall,
    /// Continuous vertical wall lining an atrium/shaft void.
    ShaftWall,
    /// Horizontal floor slab at a cell's bottom boundary.
    Floor,
    /// Horizontal ceiling/roof slab at a cell's top boundary.
    Ceiling,
    /// Low guard railing where a walkable cell meets a vertical void laterally.
    Railing,
    /// Centred structural column for a support-core cell.
    SupportColumn,
    /// Floor-level kerb/lip lining the edge of a true vertical opening, so the
    /// atrium reads as an intentional balcony edge rather than a raw cut.
    Rim,
}

impl FaceKind {
    pub fn code(self) -> u8 {
        match self {
            FaceKind::Wall => 0,
            FaceKind::ShaftWall => 1,
            FaceKind::Floor => 2,
            FaceKind::Ceiling => 3,
            FaceKind::Railing => 4,
            FaceKind::SupportColumn => 5,
            FaceKind::Rim => 6,
        }
    }
}

/// A generated face: which cell owns it, which direction it faces, and its kind.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Face {
    pub x: u8,
    pub y: u8,
    pub z: u8,
    pub dir: FaceDir,
    pub kind: FaceKind,
}

// ─── Grid ───

/// A backend-authored 3D occupancy grid. Index order is `(y*nz + z)*nx + x`.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct VolumetricGridV0 {
    pub nx: u8,
    pub ny: u8,
    pub nz: u8,
    /// World macro layer of grid row `y == 0`.
    pub base_layer: i8,
    pub cells: Vec<CellOccupancy>,
}

impl VolumetricGridV0 {
    fn filled(nx: u8, ny: u8, nz: u8, base_layer: i8, fill: CellOccupancy) -> Self {
        Self {
            nx,
            ny,
            nz,
            base_layer,
            cells: vec![fill; nx as usize * ny as usize * nz as usize],
        }
    }

    #[inline]
    fn index(&self, x: u8, y: u8, z: u8) -> usize {
        ((y as usize * self.nz as usize) + z as usize) * self.nx as usize + x as usize
    }

    #[inline]
    pub fn in_bounds(&self, x: i32, y: i32, z: i32) -> bool {
        x >= 0 && y >= 0 && z >= 0 && x < self.nx as i32 && y < self.ny as i32 && z < self.nz as i32
    }

    pub fn cell(&self, x: u8, y: u8, z: u8) -> CellOccupancy {
        self.cells[self.index(x, y, z)]
    }

    fn set(&mut self, x: u8, y: u8, z: u8, occ: CellOccupancy) {
        let i = self.index(x, y, z);
        self.cells[i] = occ;
    }

    /// Neighbour occupancy in a direction. Out-of-grid is `Solid` (the volume is
    /// a fully enclosed box: perimeter walls, ground floor, roof).
    pub fn neighbour(&self, x: u8, y: u8, z: u8, dir: FaceDir) -> CellOccupancy {
        let (dx, dy, dz) = dir.delta();
        let (nx, ny, nz) = (x as i32 + dx, y as i32 + dy, z as i32 + dz);
        if self.in_bounds(nx, ny, nz) {
            self.cell(nx as u8, ny as u8, nz as u8)
        } else {
            CellOccupancy::Solid
        }
    }

    pub fn open_cell_count(&self) -> u32 {
        self.cells.iter().filter(|c| c.is_open()).count() as u32
    }

    pub fn solid_cell_count(&self) -> u32 {
        self.cells.iter().filter(|c| !c.is_open()).count() as u32
    }

    /// Number of vertical adjacencies where both stacked cells are voids — i.e.
    /// the through-floor openings that make the atrium/shaft a continuous shaft.
    pub fn vertical_connection_count(&self) -> u32 {
        let mut count = 0u32;
        for y in 0..self.ny.saturating_sub(1) {
            for z in 0..self.nz {
                for x in 0..self.nx {
                    if self.cell(x, y, z).is_vertical_void()
                        && self.cell(x, y + 1, z).is_vertical_void()
                    {
                        count += 1;
                    }
                }
            }
        }
        count
    }

    /// Number of *valid vertical openings*: cell boundaries where a floor (and
    /// the matching ceiling) is legitimately omitted because a vertical void
    /// continues downward through that boundary. This is exactly the set of
    /// holes the renderer is allowed to leave — anywhere else must stay closed.
    pub fn valid_vertical_opening_count(&self) -> u32 {
        let mut count = 0u32;
        for y in 1..self.ny {
            for z in 0..self.nz {
                for x in 0..self.nx {
                    if self.cell(x, y, z).is_vertical_void()
                        && self.cell(x, y - 1, z).is_vertical_void()
                    {
                        count += 1;
                    }
                }
            }
        }
        count
    }

    /// True when some `(x, z)` column has a vertical void spanning ≥ 2 layers.
    pub fn has_atrium_span(&self) -> bool {
        for z in 0..self.nz {
            for x in 0..self.nx {
                let mut run = 0u8;
                for y in 0..self.ny {
                    if self.cell(x, y, z).is_vertical_void() {
                        run += 1;
                        if run >= 2 {
                            return true;
                        }
                    } else {
                        run = 0;
                    }
                }
            }
        }
        false
    }

    pub fn support_core_count(&self) -> u32 {
        self.cells
            .iter()
            .filter(|c| **c == CellOccupancy::SupportCore)
            .count() as u32
    }

    /// Open-architecture cells in one grid row (band) `y`.
    pub fn band_open_count(&self, y: u8) -> u32 {
        let mut count = 0u32;
        for z in 0..self.nz {
            for x in 0..self.nx {
                if self.cell(x, y, z).is_open() {
                    count += 1;
                }
            }
        }
        count
    }

    /// Solid/filled cells in one grid row (band) `y`.
    pub fn band_solid_count(&self, y: u8) -> u32 {
        (self.nx as u32 * self.nz as u32).saturating_sub(self.band_open_count(y))
    }

    /// Phase 3.0D artifact probe: does band `y` read as a parity checkerboard?
    /// True only when the band is a substantial open/solid mix AND ≥90% of the
    /// cells match one parity phase — coherent architecture never does.
    pub fn band_is_checkerboard(&self, y: u8) -> bool {
        let mut parity_open = 0u32;
        let mut open = 0u32;
        let total = self.nx as u32 * self.nz as u32;
        if total == 0 {
            return false;
        }
        for z in 0..self.nz {
            for x in 0..self.nx {
                let is_open = self.cell(x, y, z).is_open();
                if is_open {
                    open += 1;
                }
                if is_open == ((x as u32 + z as u32) % 2 == 0) {
                    parity_open += 1;
                }
            }
        }
        // A checkerboard needs both phases populated and near-perfect parity.
        if open * 4 < total || (total - open) * 4 < total {
            return false;
        }
        parity_open * 10 >= total * 9 || parity_open * 10 <= total
    }

    /// Phase 3.0D artifact probe: open upper/lower cells that float over (or
    /// hang under) filled main-band mass instead of tracking the main layout.
    /// Coherent stacked architecture keeps this at ~0; a floating lattice does
    /// not. Only meaningful for the standard 3-band column shape.
    pub fn unsupported_open_cell_count(&self) -> u32 {
        if self.ny != 3 {
            return 0;
        }
        let mut count = 0u32;
        for z in 0..self.nz {
            for x in 0..self.nx {
                let main_filled = self.cell(x, 1, z).is_filled();
                if main_filled && self.cell(x, 2, z).is_open() {
                    count += 1;
                }
                if main_filled && self.cell(x, 0, z).is_open() {
                    count += 1;
                }
            }
        }
        count
    }

    /// Deterministically derive every renderable face from neighbour state.
    /// Iteration order is fixed (`y`, then `z`, then `x`, then a fixed face
    /// order per cell) so the output is stable for a given grid.
    pub fn generate_faces(&self) -> Vec<Face> {
        let mut faces = Vec::new();
        for y in 0..self.ny {
            for z in 0..self.nz {
                for x in 0..self.nx {
                    let cell = self.cell(x, y, z);
                    if !cell.emits_surfaces() {
                        continue;
                    }

                    // Walls / shaft walls against filled mass.
                    for dir in FaceDir::HORIZONTAL {
                        if self.neighbour(x, y, z, dir).blocks_horizontally() {
                            let kind = if cell.is_vertical_void() {
                                FaceKind::ShaftWall
                            } else {
                                FaceKind::Wall
                            };
                            faces.push(Face { x, y, z, dir, kind });
                        }
                    }

                    // Floor at the cell's bottom boundary. A closed (non-void)
                    // cell ALWAYS gets a floor — every room/corridor is fully
                    // enclosed below. A void omits its floor only where the void
                    // continues downward (a true vertical opening), so the very
                    // bottom of a shaft keeps a floor and stays visible from
                    // above. Removal is confined to this cell's own footprint.
                    let below = self.neighbour(x, y, z, FaceDir::Down);
                    if !(cell.is_vertical_void() && below.is_vertical_void()) {
                        faces.push(Face {
                            x,
                            y,
                            z,
                            dir: FaceDir::Down,
                            kind: FaceKind::Floor,
                        });
                    }

                    // Ceiling at the cell's top boundary. A closed cell ALWAYS
                    // gets a ceiling (full enclosure). A void omits its ceiling
                    // only where the void continues upward, so the top of a shaft
                    // keeps a cap. Floor/ceiling slabs at a shared boundary are
                    // offset by the renderer so they never z-fight.
                    let above = self.neighbour(x, y, z, FaceDir::Up);
                    if !(cell.is_vertical_void() && above.is_vertical_void()) {
                        faces.push(Face {
                            x,
                            y,
                            z,
                            dir: FaceDir::Up,
                            kind: FaceKind::Ceiling,
                        });
                    }

                    // Rim kerb + guard railing where a walkable cell borders a
                    // vertical void laterally — edge treatment ONLY around true
                    // vertical openings, so the atrium reads as a balcony.
                    if cell.is_walkable() {
                        for dir in FaceDir::HORIZONTAL {
                            if self.neighbour(x, y, z, dir).is_vertical_void() {
                                faces.push(Face {
                                    x,
                                    y,
                                    z,
                                    dir,
                                    kind: FaceKind::Rim,
                                });
                                faces.push(Face {
                                    x,
                                    y,
                                    z,
                                    dir,
                                    kind: FaceKind::Railing,
                                });
                            }
                        }
                    }

                    // Structural support column for support-core cells.
                    if cell == CellOccupancy::SupportCore {
                        faces.push(Face {
                            x,
                            y,
                            z,
                            dir: FaceDir::Up,
                            kind: FaceKind::SupportColumn,
                        });
                    }
                }
            }
        }
        faces
    }

    /// Build the IPC view (cell codes + faces + counts) for a host chunk whose
    /// root is at world `chunk_root_world`. `origin_world` is the world min
    /// corner of cell `(0, 0, 0)`.
    pub fn to_view(&self, origin_world: [f32; 3]) -> VolumetricGridViewV0 {
        let faces = self.generate_faces();
        VolumetricGridViewV0 {
            active: true,
            column_id: 0,
            column_coord: [0, 0],
            source: String::new(),
            dims: [self.nx, self.ny, self.nz],
            cell_size_xz: LAYOUT_CELL_SIZE,
            layer_height: LAYER_HEIGHT,
            origin_world,
            base_layer: self.base_layer,
            cells: self.cells.iter().map(|c| c.code()).collect(),
            faces: faces
                .iter()
                .map(|f| VolumetricFaceViewV0 {
                    cell: [f.x, f.y, f.z],
                    dir: f.dir.code(),
                    kind: f.kind.code(),
                })
                .collect(),
            open_cell_count: self.open_cell_count(),
            solid_cell_count: self.solid_cell_count(),
            vertical_connection_count: self.vertical_connection_count(),
            valid_vertical_opening_count: self.valid_vertical_opening_count(),
            atrium_span: self.has_atrium_span(),
            layer_bands: Vec::new(),
            vertical_access: Vec::new(),
        }
    }
}

// ─── Unified column adapters ───

fn mix64(mut x: u64) -> u64 {
    x ^= x >> 30;
    x = x.wrapping_mul(0xbf58_476d_1ce4_e5b9);
    x ^= x >> 27;
    x = x.wrapping_mul(0x94d0_49bb_1331_11eb);
    x ^ (x >> 31)
}

pub fn column_id(world_seed: u64, pos: ChunkPos) -> u64 {
    let x = (pos.0 as i64 as u64).wrapping_mul(0x9e37_79b9_7f4a_7c15);
    let z = (pos.1 as i64 as u64).wrapping_mul(0xc2b2_ae3d_27d4_eb4f);
    mix64(world_seed ^ x.rotate_left(17) ^ z.rotate_left(41) ^ 0x30c0_0000_0000_0001)
        & 0x7fff_ffff_ffff_ffff
}

fn column_hash(world_seed: u64, pos: ChunkPos, salt: u64) -> u64 {
    mix64(column_id(world_seed, pos) ^ salt)
}

fn band_id(world_seed: u64, pos: ChunkPos, layer: ChunkLayer) -> u32 {
    (column_hash(
        world_seed,
        pos,
        (layer as i64 as u64).wrapping_mul(0x30c0_0003),
    ) & 0xffff_ffff) as u32
}

fn access_id(world_seed: u64, pos: ChunkPos, index: u32) -> u32 {
    (column_hash(world_seed, pos, 0x30c0_acce_5500_0000 ^ index as u64) & 0xffff_ffff) as u32
}

fn column_origin_world(pos: ChunkPos) -> [f32; 3] {
    [
        pos.0 as f32 * CHUNK_SIZE,
        -LAYER_HEIGHT,
        pos.1 as f32 * CHUNK_SIZE,
    ]
}

fn band_view(band: &LayerBand) -> LayerBandViewV0 {
    LayerBandViewV0 {
        band_id: band.band_id,
        layer: band.layer,
        profile: band.profile.as_str().into(),
        profile_code: band.profile.code(),
        accessible: band.accessible,
        danger_profile: band.danger_profile.as_str().into(),
        resource_profile: band.resource_profile.as_str().into(),
        anomaly_profile: band.anomaly_profile.as_str().into(),
    }
}

fn access_view(node: &VerticalAccessNode) -> VerticalAccessNodeViewV0 {
    VerticalAccessNodeViewV0 {
        access_id: node.access_id,
        access_type: node.access_type.as_str().into(),
        access_type_code: node.access_type.code(),
        from_layer: node.from_layer,
        to_layer: node.to_layer,
        footprint_cell_min: node.footprint_cell_min,
        footprint_cell_max: node.footprint_cell_max,
        explicit: node.explicit,
    }
}

fn main_profile_for(chunk: &Chunk) -> LayerBandProfileV0 {
    match chunk.layout.zone_kind {
        ZONE_SAFE | ZONE_MANILA => LayerBandProfileV0::ManilaSafeNode,
        ZONE_RED => LayerBandProfileV0::RedDangerZone,
        ZONE_DANGER | ZONE_PIT => LayerBandProfileV0::RedDangerZone,
        ZONE_BLACKOUT => LayerBandProfileV0::DarkLobby,
        ZONE_CLEANING => LayerBandProfileV0::Level0RemodeledOffice,
        ZONE_HUMID => LayerBandProfileV0::RemodeledMess,
        ZONE_PILLAR_HALL => LayerBandProfileV0::MegastructureHint,
        ZONE_OPEN_HALL | ZONE_STORAGE => LayerBandProfileV0::Level0Classic,
        _ => LayerBandProfileV0::Level0Classic,
    }
}

fn upper_profile_for(world_seed: u64, chunk: &Chunk) -> LayerBandProfileV0 {
    if chunk.layout.vertical_flags & V30A_CONNECTOR != 0 {
        return LayerBandProfileV0::Level0UpperOffice;
    }
    match column_hash(world_seed, chunk.pos, 0x30c0_0000_0000_1001) % 11 {
        0 => LayerBandProfileV0::Level0UpperOffice,
        1 => LayerBandProfileV0::Level0RemodeledOffice,
        2 => LayerBandProfileV0::CeilingServiceVoid,
        3 => LayerBandProfileV0::MegastructureHint,
        4 => LayerBandProfileV0::SealedArchitecture,
        _ => LayerBandProfileV0::Level0UpperFalseCeiling,
    }
}

fn lower_profile_for(world_seed: u64, chunk: &Chunk) -> LayerBandProfileV0 {
    match column_hash(world_seed, chunk.pos, 0x30c0_0000_0000_1002) % 13 {
        0 => LayerBandProfileV0::ConcreteSublevel,
        1 => LayerBandProfileV0::DarkLobby,
        2 => LayerBandProfileV0::RedDangerZone,
        3 => LayerBandProfileV0::SealedArchitecture,
        4 => LayerBandProfileV0::VoidOrExtremeAnomaly,
        _ => LayerBandProfileV0::UnderfloorService,
    }
}

fn band_profiles(world_seed: u64, chunk: &Chunk) -> [LayerBand; 3] {
    let main = main_profile_for(chunk);
    let lower = lower_profile_for(world_seed, chunk);
    let upper = upper_profile_for(world_seed, chunk);
    [
        LayerBand {
            band_id: band_id(world_seed, chunk.pos, -1),
            layer: -1,
            profile: lower,
            accessible: false,
            danger_profile: match lower {
                LayerBandProfileV0::RedDangerZone => DangerProfileV0::RedPocket,
                LayerBandProfileV0::DarkLobby => DangerProfileV0::Blackout,
                LayerBandProfileV0::VoidOrExtremeAnomaly => DangerProfileV0::ExtremeAnomaly,
                _ => DangerProfileV0::None,
            },
            resource_profile: ResourceProfileV0::None,
            anomaly_profile: match lower {
                LayerBandProfileV0::ConcreteSublevel => AnomalyProfileV0::DesaturatedTransition,
                LayerBandProfileV0::VoidOrExtremeAnomaly => AnomalyProfileV0::MegastructureHint,
                _ => AnomalyProfileV0::None,
            },
        },
        LayerBand {
            band_id: band_id(world_seed, chunk.pos, 0),
            layer: 0,
            profile: main,
            accessible: true,
            danger_profile: match main {
                LayerBandProfileV0::RedDangerZone => DangerProfileV0::RedPocket,
                LayerBandProfileV0::DarkLobby => DangerProfileV0::Blackout,
                _ => DangerProfileV0::Low,
            },
            resource_profile: match main {
                LayerBandProfileV0::ManilaSafeNode => ResourceProfileV0::SafeNode,
                _ if chunk.has_workbench => ResourceProfileV0::StorageHint,
                _ => ResourceProfileV0::Sparse,
            },
            anomaly_profile: match main {
                LayerBandProfileV0::ManilaSafeNode => AnomalyProfileV0::ManilaHint,
                LayerBandProfileV0::RedDangerZone => AnomalyProfileV0::RedRoomHint,
                LayerBandProfileV0::MegastructureHint => AnomalyProfileV0::MegastructureHint,
                _ => AnomalyProfileV0::None,
            },
        },
        LayerBand {
            band_id: band_id(world_seed, chunk.pos, 1),
            layer: 1,
            profile: upper,
            accessible: false,
            danger_profile: DangerProfileV0::None,
            resource_profile: ResourceProfileV0::None,
            anomaly_profile: match upper {
                LayerBandProfileV0::MegastructureHint => AnomalyProfileV0::MegastructureHint,
                _ => AnomalyProfileV0::None,
            },
        },
    ]
}

fn is_corridor_template(chunk: &Chunk) -> bool {
    matches!(chunk.template_id, 1 | 2 | 3 | 6 | 8)
}

/// Contiguous open span (in cells, including `(x, z)` itself) through the cell
/// along one axis, stopping at full-wall edges, non-walkable cells or the chunk
/// boundary. This reads the REAL Phase 2.7 edge-wall layout, so corridor/room
/// classification follows the authored architecture instead of the template id.
fn open_span(layout: &ChunkLayoutV1, x: usize, z: usize, along_x: bool) -> usize {
    let g = layout.grid_size as usize;
    let mut span = 1usize;
    // Negative direction.
    let (mut cx, mut cz) = (x, z);
    loop {
        let blocked = if along_x {
            cx == 0 || edge_is_full_wall(layout.edge_v(cx, cz)) || {
                let n = (cx - 1, cz);
                !layout.is_cell_walkable(n.0, n.1)
            }
        } else {
            cz == 0 || edge_is_full_wall(layout.edge_h(cx, cz)) || {
                let n = (cx, cz - 1);
                !layout.is_cell_walkable(n.0, n.1)
            }
        };
        if blocked {
            break;
        }
        if along_x {
            cx -= 1;
        } else {
            cz -= 1;
        }
        span += 1;
    }
    // Positive direction.
    let (mut cx, mut cz) = (x, z);
    loop {
        let blocked = if along_x {
            cx + 1 >= g || edge_is_full_wall(layout.edge_v(cx + 1, cz)) || {
                let n = (cx + 1, cz);
                !layout.is_cell_walkable(n.0, n.1)
            }
        } else {
            cz + 1 >= g || edge_is_full_wall(layout.edge_h(cx, cz + 1)) || {
                let n = (cx, cz + 1);
                !layout.is_cell_walkable(n.0, n.1)
            }
        };
        if blocked {
            break;
        }
        if along_x {
            cx += 1;
        } else {
            cz += 1;
        }
        span += 1;
    }
    span
}

/// Walkable-cell passage classification from the authored layout: a cell reads
/// as Corridor when it sits in a narrow directional passage (≤2 cells across,
/// running ≥3 cells), otherwise it is open Room space.
fn passage_kind(layout: &ChunkLayoutV1, x: usize, z: usize) -> CellOccupancy {
    let sx = open_span(layout, x, z, true);
    let sz = open_span(layout, x, z, false);
    if sx.min(sz) <= 2 && sx.max(sz) >= 3 {
        CellOccupancy::Corridor
    } else {
        CellOccupancy::Room
    }
}

fn main_cell_kind(chunk: &Chunk, x: usize, z: usize) -> CellOccupancy {
    let flags = chunk.layout.cell_flags(x, z);
    if flags & CELL_PILLAR != 0 {
        return CellOccupancy::SupportCore;
    }
    if flags & CELL_PIT != 0 || chunk.layout.floor_profile == FLOOR_PIT_PLACEHOLDER {
        return CellOccupancy::DangerZone;
    }
    if flags & (CELL_BLOCKED) != 0 || flags & CELL_WALKABLE == 0 {
        return CellOccupancy::Blocked;
    }
    if flags & CELL_ANOMALY != 0 {
        return CellOccupancy::Anomaly;
    }
    // Contained danger pockets: only explicitly hazardous cells, or the rare
    // depth-gated red/danger/pit zones keep their whole-room danger identity.
    if flags & CELL_HAZARD != 0
        || matches!(chunk.layout.zone_kind, ZONE_DANGER | ZONE_RED | ZONE_PIT)
    {
        return CellOccupancy::DangerZone;
    }
    // Safe-node identity only where the layout marks the cell safe — zone-wide
    // safe/manila/cleaning identity lives in the band profile metadata, NOT as
    // a whole-chunk cell override (that flattening caused rooms=0 chunks).
    if flags & CELL_SAFE != 0 {
        return CellOccupancy::SafeNode;
    }
    passage_kind(&chunk.layout, x, z)
}

// ─── Phase 3.0D — true multi-layer Level 0 columns ───
//
// Y+1 / Y-1 are real deterministic architecture derived from the main-band
// layout (so shapes are coherent rooms/corridors, never per-cell noise), the
// column's band profiles (macrostructure identity) and the world seed. Support
// cores continue vertically; everything over/under filled main mass stays
// filled, so nothing floats.

/// Chunks closer than this (Chebyshev, in chunks) to the origin never receive
/// vertical openings or lower danger pockets — the starter area stays safe.
const V30D_SAFE_RADIUS_CHUNKS: i32 = 2;
const V30D_SHAFT_SALT: u64 = 0x30d0_0000_0000_0011;
const V30D_RAMP_SALT: u64 = 0x30d0_0000_0000_0012;
const V30D_BROKEN_SALT: u64 = 0x30d0_0000_0000_0013;
/// Roughly 1-in-N columns get a bounded 1×1 service shaft / placeholder marker.
const V30D_SHAFT_RARITY: u64 = 23;
const V30D_RAMP_RARITY: u64 = 17;
const V30D_BROKEN_RARITY: u64 = 19;

fn chunk_chebyshev_from_origin(pos: ChunkPos) -> i32 {
    pos.0.abs().max(pos.1.abs())
}

/// Upper-band (Y+1) occupancy for the cell above a main-band cell, derived
/// from the upper band profile and the main cell's architectural role.
fn upper_cell_kind(profile: LayerBandProfileV0, main: CellOccupancy) -> CellOccupancy {
    if main == CellOccupancy::SupportCore {
        return CellOccupancy::SupportCore; // support cores continue vertically
    }
    if !main.is_open() {
        return CellOccupancy::Solid; // structure continues over filled mass
    }
    match profile {
        // Office floors above: service corridors over corridors, blocked
        // (sealed) offices over rooms.
        LayerBandProfileV0::Level0UpperOffice => match main {
            CellOccupancy::Corridor => CellOccupancy::Corridor,
            _ => CellOccupancy::SealedRoom,
        },
        // Remodeled upper floor: real upper rooms over rooms, low service
        // walkways over corridors.
        LayerBandProfileV0::Level0RemodeledOffice => match main {
            CellOccupancy::Corridor => CellOccupancy::ServiceSpace,
            CellOccupancy::Room => CellOccupancy::Room,
            _ => CellOccupancy::SealedRoom,
        },
        // Ceiling service plenum runs above the corridor network; room
        // ceilings stay structural mass.
        LayerBandProfileV0::CeilingServiceVoid => match main {
            CellOccupancy::Corridor => CellOccupancy::CeilingVoid,
            _ => CellOccupancy::Solid,
        },
        // Sealed overhead architecture: real but inaccessible chambers.
        LayerBandProfileV0::SealedArchitecture => CellOccupancy::SealedRoom,
        // Megastructure mass with sealed pockets over the rooms only.
        LayerBandProfileV0::MegastructureHint => match main {
            CellOccupancy::Room => CellOccupancy::SealedRoom,
            _ => CellOccupancy::Solid,
        },
        // Default Level 0 upper identity: false-ceiling cavities over the
        // rooms, solid bulkheads over the corridors.
        _ => match main {
            CellOccupancy::Corridor => CellOccupancy::Solid,
            _ => CellOccupancy::CeilingVoid,
        },
    }
}

/// Lower-band (Y-1) occupancy for the cell below a main-band cell. Danger
/// pockets are depth-gated: `danger_allowed` is false near the starter area.
fn lower_cell_kind(
    profile: LayerBandProfileV0,
    main: CellOccupancy,
    danger_allowed: bool,
) -> CellOccupancy {
    if main == CellOccupancy::SupportCore {
        return CellOccupancy::SupportCore; // lower support core
    }
    if !main.is_open() {
        return CellOccupancy::Solid; // foundation under filled mass
    }
    match profile {
        LayerBandProfileV0::ConcreteSublevel => match main {
            CellOccupancy::Corridor => CellOccupancy::ServiceSpace,
            _ => CellOccupancy::SealedRoom,
        },
        LayerBandProfileV0::DarkLobby => match main {
            CellOccupancy::Corridor => CellOccupancy::ServiceSpace,
            _ => CellOccupancy::Room, // dark lower rooms
        },
        LayerBandProfileV0::RedDangerZone if danger_allowed => match main {
            // Bounded red pocket: only the room footprints turn dangerous.
            CellOccupancy::Room => CellOccupancy::DangerZone,
            _ => CellOccupancy::ServiceSpace,
        },
        LayerBandProfileV0::SealedArchitecture => CellOccupancy::SealedRoom,
        LayerBandProfileV0::VoidOrExtremeAnomaly => match main {
            CellOccupancy::Room => CellOccupancy::FalseSpace, // sealed utility void
            _ => CellOccupancy::UnderfloorService,
        },
        // Default: underfloor service corridors under the corridor network,
        // maintenance/storage spaces under the rooms.
        _ => match main {
            CellOccupancy::Corridor => CellOccupancy::UnderfloorService,
            _ => CellOccupancy::ServiceSpace,
        },
    }
}

/// Rare, explicit, bounded 1×1 service shaft connecting all three bands. Never
/// near the starter area, never in a column that already has explicit access,
/// and always paired with an explicit opening-type access node.
fn carve_rare_shaft(
    world_seed: u64,
    chunk: &Chunk,
    grid: &mut VolumetricGridV0,
    access: &mut Vec<VerticalAccessNode>,
) {
    if chunk_chebyshev_from_origin(chunk.pos) < V30D_SAFE_RADIUS_CHUNKS || !access.is_empty() {
        return;
    }
    let h = column_hash(world_seed, chunk.pos, V30D_SHAFT_SALT);
    if h % V30D_SHAFT_RARITY != 0 {
        return;
    }
    // Deterministic interior candidate cell; the shaft only forms where the
    // main band is genuinely walkable (an intentional opening, never a random
    // hole through solid architecture).
    let x = (2 + ((h >> 8) % 6)) as u8;
    let z = (2 + ((h >> 16) % 6)) as u8;
    if !grid.cell(x, 1, z).is_walkable() {
        return;
    }
    for y in 0..grid.ny {
        grid.set(x, y, z, CellOccupancy::Shaft);
    }
    access.push(VerticalAccessNode {
        access_id: access_id(world_seed, chunk.pos, 8),
        access_type: VerticalAccessTypeV0::Shaft,
        from_layer: -1,
        to_layer: 1,
        footprint_cell_min: [x, z],
        footprint_cell_max: [x + 1, z + 1],
        explicit: true,
    });
}

/// Bounding box (`[min_x, min_z]`, exclusive `[max_x, max_z]`) of the open
/// cells in band `y`, or the full grid footprint when the band is pure mass.
fn band_open_bbox(grid: &VolumetricGridV0, y: u8) -> ([u8; 2], [u8; 2]) {
    let mut min = [u8::MAX, u8::MAX];
    let mut max = [0u8, 0u8];
    let mut any = false;
    for z in 0..grid.nz {
        for x in 0..grid.nx {
            if grid.cell(x, y, z).is_open() {
                any = true;
                min[0] = min[0].min(x);
                min[1] = min[1].min(z);
                max[0] = max[0].max(x + 1);
                max[1] = max[1].max(z + 1);
            }
        }
    }
    if any {
        (min, max)
    } else {
        ([0, 0], [grid.nx, grid.nz])
    }
}

/// Append the explicit Phase 3.0D vertical relationship nodes for a finished
/// column grid. Deterministic order; aggregate (no per-cell node spam).
fn push_relationship_nodes(
    world_seed: u64,
    chunk: &Chunk,
    grid: &VolumetricGridV0,
    upper_profile: LayerBandProfileV0,
    access: &mut Vec<VerticalAccessNode>,
) {
    let mut next_index = 16u32; // separate id-space from the opening nodes
    let mut push = |access: &mut Vec<VerticalAccessNode>,
                    access_type: VerticalAccessTypeV0,
                    from_layer: ChunkLayer,
                    to_layer: ChunkLayer,
                    footprint_cell_min: [u8; 2],
                    footprint_cell_max: [u8; 2]| {
        access.push(VerticalAccessNode {
            access_id: access_id(world_seed, chunk.pos, next_index),
            access_type,
            from_layer,
            to_layer,
            footprint_cell_min,
            footprint_cell_max,
            explicit: true,
        });
        next_index += 1;
    };

    // Through-floor opening census on the two band boundaries.
    let mut open_up = false;
    let mut open_down = false;
    for z in 0..grid.nz {
        for x in 0..grid.nx {
            if grid.cell(x, 1, z).is_vertical_void() {
                open_up |= grid.cell(x, 2, z).is_vertical_void();
                open_down |= grid.cell(x, 0, z).is_vertical_void();
            }
        }
    }

    // The main band's ceiling is structurally the upper band's floor (and the
    // floor the lower band's ceiling) everywhere in a stacked column.
    push(
        access,
        VerticalAccessTypeV0::SharedFloorCeiling,
        0,
        1,
        [0, 0],
        [grid.nx, grid.nz],
    );
    if !open_up {
        let (min, max) = band_open_bbox(grid, 2);
        push(access, VerticalAccessTypeV0::SealedAbove, 0, 1, min, max);
    }
    if !open_down {
        let (min, max) = band_open_bbox(grid, 0);
        push(access, VerticalAccessTypeV0::SealedBelow, 0, -1, min, max);
    }

    // Support cores that continue across all three bands.
    let mut core_min = [u8::MAX, u8::MAX];
    let mut core_max = [0u8, 0u8];
    let mut any_core = false;
    for z in 0..grid.nz {
        for x in 0..grid.nx {
            if grid.cell(x, 1, z) == CellOccupancy::SupportCore {
                any_core = true;
                core_min[0] = core_min[0].min(x);
                core_min[1] = core_min[1].min(z);
                core_max[0] = core_max[0].max(x + 1);
                core_max[1] = core_max[1].max(z + 1);
            }
        }
    }
    if any_core {
        push(
            access,
            VerticalAccessTypeV0::SupportCoreContinuation,
            -1,
            1,
            core_min,
            core_max,
        );
    }

    // Controlled ceiling access marker where a false-ceiling/service plenum
    // exists: the first (deterministic scan order) plenum cell.
    if matches!(
        upper_profile,
        LayerBandProfileV0::Level0UpperFalseCeiling | LayerBandProfileV0::CeilingServiceVoid
    ) {
        'plenum: for z in 0..grid.nz {
            for x in 0..grid.nx {
                if grid.cell(x, 2, z) == CellOccupancy::CeilingVoid {
                    push(
                        access,
                        VerticalAccessTypeV0::FalseCeilingAccess,
                        0,
                        1,
                        [x, z],
                        [x + 1, z + 1],
                    );
                    break 'plenum;
                }
            }
        }
    }

    // Rare planned-traversal placeholders, depth-gated away from the starter
    // area. Markers only — no voids are carved.
    if chunk_chebyshev_from_origin(chunk.pos) >= V30D_SAFE_RADIUS_CHUNKS {
        let ramp_hash = column_hash(world_seed, chunk.pos, V30D_RAMP_SALT);
        if ramp_hash % V30D_RAMP_RARITY == 0 {
            let x = (2 + ((ramp_hash >> 8) % 6)) as u8;
            let z = (2 + ((ramp_hash >> 16) % 6)) as u8;
            if grid.cell(x, 0, z).is_walkable() {
                push(
                    access,
                    VerticalAccessTypeV0::ServiceRampPlaceholder,
                    0,
                    -1,
                    [x, z],
                    [x + 1, z + 1],
                );
            }
        }
        let broken_hash = column_hash(world_seed, chunk.pos, V30D_BROKEN_SALT);
        if broken_hash % V30D_BROKEN_RARITY == 0 {
            let x = (2 + ((broken_hash >> 8) % 6)) as u8;
            let z = (2 + ((broken_hash >> 16) % 6)) as u8;
            if grid.cell(x, 1, z).is_walkable() {
                push(
                    access,
                    VerticalAccessTypeV0::BrokenFloorPlaceholder,
                    0,
                    -1,
                    [x, z],
                    [x + 1, z + 1],
                );
            }
        }
    }
}

fn explicit_access_for(world_seed: u64, chunk: &Chunk) -> Vec<VerticalAccessNode> {
    if chunk.layout.vertical_flags & V30A_CONNECTOR == 0
        && chunk.layout.floor_profile != FLOOR_CONNECTOR_UP
        && chunk.layout.floor_profile != FLOOR_CONNECTOR_DOWN
        && chunk.layout.inter_layer_volumes.is_empty()
    {
        return Vec::new();
    }

    let access_type = if chunk
        .layout
        .inter_layer_volumes
        .iter()
        .any(|v| v.kind.as_str() == "ATRIUM_STACK")
    {
        VerticalAccessTypeV0::Atrium
    } else if !chunk.layout.inter_layer_volumes.is_empty() {
        VerticalAccessTypeV0::Shaft
    } else {
        VerticalAccessTypeV0::Stairwell
    };

    vec![VerticalAccessNode {
        access_id: access_id(world_seed, chunk.pos, 0),
        access_type,
        from_layer: 0,
        to_layer: if chunk.layout.floor_profile == FLOOR_CONNECTOR_UP {
            1
        } else {
            -1
        },
        footprint_cell_min: [4, 4],
        footprint_cell_max: [6, 6],
        explicit: true,
    }]
}

fn apply_explicit_access(grid: &mut VolumetricGridV0, access: &[VerticalAccessNode]) {
    for node in access {
        let min_x = node.footprint_cell_min[0].min(LAYOUT_GRID_SIZE - 1);
        let min_z = node.footprint_cell_min[1].min(LAYOUT_GRID_SIZE - 1);
        let max_x = node.footprint_cell_max[0].min(LAYOUT_GRID_SIZE);
        let max_z = node.footprint_cell_max[1].min(LAYOUT_GRID_SIZE);
        let occ = match node.access_type {
            VerticalAccessTypeV0::Atrium | VerticalAccessTypeV0::CollapsedFloor => {
                CellOccupancy::AtriumVoid
            }
            VerticalAccessTypeV0::Stairwell
            | VerticalAccessTypeV0::Shaft
            | VerticalAccessTypeV0::Vent
            | VerticalAccessTypeV0::ElevatorPlaceholder => CellOccupancy::Shaft,
            VerticalAccessTypeV0::ManilaTransition => CellOccupancy::SafeNode,
            VerticalAccessTypeV0::RedRoomThreshold => CellOccupancy::DangerZone,
            VerticalAccessTypeV0::NoclipAnomaly | VerticalAccessTypeV0::RemodeledDoor => {
                CellOccupancy::Anomaly
            }
            VerticalAccessTypeV0::None | VerticalAccessTypeV0::BrokenCeiling => {
                CellOccupancy::CeilingVoid
            }
            // Relationship-type nodes are explicit metadata only — they
            // describe the structural relationship between stacked bands and
            // must never carve voids into the grid.
            VerticalAccessTypeV0::SealedAbove
            | VerticalAccessTypeV0::SealedBelow
            | VerticalAccessTypeV0::SharedFloorCeiling
            | VerticalAccessTypeV0::ServiceRampPlaceholder
            | VerticalAccessTypeV0::BrokenFloorPlaceholder
            | VerticalAccessTypeV0::FalseCeilingAccess
            | VerticalAccessTypeV0::SupportCoreContinuation => continue,
        };
        for y in 0..grid.ny {
            for z in min_z..max_z {
                for x in min_x..max_x {
                    grid.set(x, y, z, occ);
                }
            }
        }
    }
}

fn layout_edge_faces(layout: &ChunkLayoutV1) -> Vec<VolumetricFaceViewV0> {
    let g = layout.grid_size as usize;
    let mut faces = Vec::new();
    for z in 0..g {
        for bx in 0..=g {
            let kind = layout.edge_v(bx, z);
            if !edge_is_full_wall(kind) && kind != EDGE_KIND_LOW_WALL && kind != EDGE_KIND_HALF_WALL
            {
                continue;
            }
            let (x, dir) = if bx == 0 {
                (0usize, FaceDir::West)
            } else {
                (bx - 1, FaceDir::East)
            };
            faces.push(VolumetricFaceViewV0 {
                cell: [x as u8, 1, z as u8],
                dir: dir.code(),
                kind: if edge_is_full_wall(kind) {
                    FaceKind::Wall.code()
                } else {
                    FaceKind::Railing.code()
                },
            });
        }
    }
    for bz in 0..=g {
        for x in 0..g {
            let kind = layout.edge_h(x, bz);
            if !edge_is_full_wall(kind) && kind != EDGE_KIND_LOW_WALL && kind != EDGE_KIND_HALF_WALL
            {
                continue;
            }
            let (z, dir) = if bz == 0 {
                (0usize, FaceDir::South)
            } else {
                (bz - 1, FaceDir::North)
            };
            faces.push(VolumetricFaceViewV0 {
                cell: [x as u8, 1, z as u8],
                dir: dir.code(),
                kind: if edge_is_full_wall(kind) {
                    FaceKind::Wall.code()
                } else {
                    FaceKind::Railing.code()
                },
            });
        }
    }
    faces
}

pub fn build_level0_column(world_seed: u64, chunk: &Chunk) -> VolumetricColumn {
    let bands = band_profiles(world_seed, chunk).to_vec();
    let mut grid = VolumetricGridV0::filled(
        LAYOUT_GRID_SIZE,
        3,
        LAYOUT_GRID_SIZE,
        -1,
        CellOccupancy::Solid,
    );
    for z in 0..LAYOUT_GRID_SIZE as usize {
        for x in 0..LAYOUT_GRID_SIZE as usize {
            grid.set(x as u8, 1, z as u8, main_cell_kind(chunk, x, z));
        }
    }

    // Phase 3.0D — true multi-layer columns. Y+1 / Y-1 become real generated
    // architecture derived from the main band's layout and the column's band
    // profiles (replacing the 3.0C-FIX hidden hint-mode solid mass). Shapes
    // track the main rooms/corridors, so the stacked bands stay coherent and
    // nothing floats over filled mass.
    let upper_profile = bands[2].profile;
    let lower_profile = bands[0].profile;
    let danger_allowed = chunk_chebyshev_from_origin(chunk.pos) >= V30D_SAFE_RADIUS_CHUNKS;
    for z in 0..LAYOUT_GRID_SIZE {
        for x in 0..LAYOUT_GRID_SIZE {
            let main = grid.cell(x, 1, z);
            grid.set(x, 2, z, upper_cell_kind(upper_profile, main));
            grid.set(
                x,
                0,
                z,
                lower_cell_kind(lower_profile, main, danger_allowed),
            );
        }
    }

    let mut vertical_access = explicit_access_for(world_seed, chunk);
    apply_explicit_access(&mut grid, &vertical_access);
    carve_rare_shaft(world_seed, chunk, &mut grid, &mut vertical_access);
    push_relationship_nodes(
        world_seed,
        chunk,
        &grid,
        upper_profile,
        &mut vertical_access,
    );

    let source = if chunk.layout.vertical_flags & V30A_CONNECTOR != 0
        || !chunk.layout.inter_layer_volumes.is_empty()
    {
        UNIFIED_COLUMN_SOURCE_V30A
    } else {
        UNIFIED_COLUMN_SOURCE_LEVEL0
    };

    VolumetricColumn {
        column_id: column_id(world_seed, chunk.pos),
        coord: VolumetricColumnCoord {
            x: chunk.pos.0,
            z: chunk.pos.1,
        },
        source: source.into(),
        bands,
        grid,
        vertical_access,
    }
}

pub fn level0_column_view(world_seed: u64, chunk: &Chunk) -> VolumetricGridViewV0 {
    let column = build_level0_column(world_seed, chunk);
    let mut view = column.grid.to_view(column_origin_world(chunk.pos));
    view.column_id = column.column_id;
    view.column_coord = [column.coord.x, column.coord.z];
    view.source = column.source;
    view.layer_bands = column.bands.iter().map(band_view).collect();
    view.vertical_access = column.vertical_access.iter().map(access_view).collect();
    view.faces.extend(layout_edge_faces(&chunk.layout));
    view
}

pub fn unified_column_view(world_seed: u64, chunk: &Chunk) -> Option<VolumetricGridViewV0> {
    if chunk.layer != 0 {
        return None;
    }

    if world_seed == SHOWCASE_SEED {
        if let Some(view) = showcase_chunk_view(chunk.pos) {
            return Some(view);
        }
    }

    Some(level0_column_view(world_seed, chunk))
}

// ─── Showcase authoring (multi-chunk global plan) ───

#[inline]
fn in_incl(v: i32, range: (i32, i32)) -> bool {
    v >= range.0 && v <= range.1
}

#[inline]
fn in_atrium(gx: i32, gz: i32) -> bool {
    in_incl(gx, ATRIUM_X) && in_incl(gz, ATRIUM_Z)
}

#[inline]
fn in_ring(gx: i32, gz: i32) -> bool {
    in_incl(gx, RING_X) && in_incl(gz, RING_Z) && !in_atrium(gx, gz)
}

#[inline]
fn is_ring_core(gx: i32, gz: i32) -> bool {
    (gx == RING_X.0 || gx == RING_X.1) && (gz == RING_Z.0 || gz == RING_Z.1)
}

/// Occupancy of the enclosed NE service shaft block (3×3 of mostly solid with a
/// 1-cell shaft core and a single south access on the ground layer).
fn shaft_block_occ(gx: i32, gz: i32, gy: i32) -> Option<CellOccupancy> {
    if !in_incl(gx, SHAFT_BLOCK_X) || !in_incl(gz, SHAFT_BLOCK_Z) {
        return None;
    }
    let core_x = (SHAFT_BLOCK_X.0 + SHAFT_BLOCK_X.1) / 2; // 17
    let core_z = (SHAFT_BLOCK_Z.0 + SHAFT_BLOCK_Z.1) / 2; // 16
    if gx == core_x && gz == core_z {
        return Some(CellOccupancy::Shaft);
    }
    // Single south-facing access corridor on the ground layer only.
    if gy == 1 && gx == core_x && gz == SHAFT_BLOCK_Z.0 {
        return Some(CellOccupancy::Corridor);
    }
    Some(CellOccupancy::Solid)
}

/// Closed room cluster (SE): a block split into four enclosed rooms by a solid
/// cross, with single-cell doorways through the dividers.
fn cluster_occ(gx: i32, gz: i32) -> CellOccupancy {
    let div_x = 16;
    let div_z = 4;
    let on_div = gx == div_x || gz == div_z;
    let doorway = matches!((gx, gz), (16, 2) | (16, 6) | (14, 4) | (18, 4));
    if on_div && !doorway {
        CellOccupancy::Solid
    } else {
        CellOccupancy::Room
    }
}

/// The single global occupancy function. Out-of-region resolves to `Solid`, so
/// the whole showcase is enclosed (its perimeter walls off normal Level 0).
/// Layers: gy 0 = macro layer -1 (lower service), 1 = ground, 2 = upper gallery.
pub fn showcase_global(gx: i32, gy: i32, gz: i32) -> CellOccupancy {
    if gx < 0 || gz < 0 || gy < 0 || gx >= GNX || gz >= GNZ || gy >= SHOWCASE_NY {
        return CellOccupancy::Solid;
    }

    // Vertical-spanning features (present on every layer, perfectly aligned).
    if is_ring_core(gx, gz) {
        return CellOccupancy::SupportCore;
    }
    if let Some(occ) = shaft_block_occ(gx, gz, gy) {
        // The shaft core spans all layers; its solid surround/access varies.
        if occ == CellOccupancy::Shaft {
            return CellOccupancy::Shaft;
        }
        // Surround/access only meaningful on the ground layer; lower/upper keep
        // the surround solid for a fully enclosed service shaft.
        if gy == 1 {
            return occ;
        }
        return CellOccupancy::Solid;
    }
    if in_atrium(gx, gz) {
        return CellOccupancy::AtriumVoid;
    }

    match gy {
        // Ground floor: the full plan.
        1 => {
            if in_ring(gx, gz) {
                CellOccupancy::Corridor // wraparound corridor loop
            } else if in_incl(gx, SERVICE_NOOK_X) && in_incl(gz, SERVICE_NOOK_Z) {
                CellOccupancy::ServiceSpace
            } else if in_incl(gx, CLUSTER_X) && in_incl(gz, CLUSTER_Z) {
                cluster_occ(gx, gz)
            } else {
                CellOccupancy::Room
            }
        }
        // Lower layer: a darker service/underfloor ring under the atrium, the
        // rest is foundation mass (visible from above through the shaft sides).
        0 => {
            if in_ring(gx, gz) {
                CellOccupancy::ServiceSpace
            } else {
                CellOccupancy::Solid
            }
        }
        // Upper layer: a walkable gallery ring around the atrium; the rest is
        // roof mass (the implied upper structure).
        _ => {
            if in_ring(gx, gz) {
                CellOccupancy::Room
            } else {
                CellOccupancy::Solid
            }
        }
    }
}

/// Build the full 20×3×20 global showcase grid.
pub fn build_showcase_global_grid() -> VolumetricGridV0 {
    let mut grid = VolumetricGridV0::filled(
        GNX as u8,
        SHOWCASE_NY as u8,
        GNZ as u8,
        SHOWCASE_BASE_LAYER,
        CellOccupancy::Solid,
    );
    for y in 0..SHOWCASE_NY {
        for z in 0..GNZ {
            for x in 0..GNX {
                grid.set(x as u8, y as u8, z as u8, showcase_global(x, y, z));
            }
        }
    }
    grid
}

/// Global faces (whole region), with the clean transition doorways removed so
/// the perimeter reads as having real openings into normal Level 0.
fn showcase_global_faces() -> &'static Vec<Face> {
    static FACES: OnceLock<Vec<Face>> = OnceLock::new();
    FACES.get_or_init(|| {
        let grid = build_showcase_global_grid();
        grid.generate_faces()
            .into_iter()
            .filter(|f| !is_transition_doorway(f))
            .collect()
    })
}

fn is_transition_doorway(f: &Face) -> bool {
    if !matches!(f.kind, FaceKind::Wall | FaceKind::ShaftWall) {
        return false;
    }
    TRANSITION_DOORWAYS.iter().any(|&(dx, dy, dz, dir)| {
        f.x as i32 == dx && f.y as i32 == dy && f.z as i32 == dz && f.dir == dir
    })
}

/// The 2×2 showcase chunk coordinates.
pub fn showcase_chunks() -> [ChunkPos; 4] {
    SHOWCASE_CHUNKS
}

pub fn is_showcase_chunk(pos: ChunkPos) -> bool {
    SHOWCASE_CHUNKS.contains(&pos)
}

/// Global cell origin (gx0, gz0) of a showcase chunk's 10×10 window.
fn chunk_window_origin(pos: ChunkPos) -> (i32, i32) {
    (pos.0 * CELLS_PER_CHUNK, pos.1 * CELLS_PER_CHUNK)
}

/// World min-corner (cell 0,0,0) of a showcase chunk's window. Aligns with the
/// chunk's own world position; the lowest layer sits LAYER_HEIGHT below ground.
fn chunk_origin_world(pos: ChunkPos) -> [f32; 3] {
    [
        pos.0 as f32 * CHUNK_SIZE,
        SHOWCASE_BASE_LAYER as f32 * LAYER_HEIGHT,
        pos.1 as f32 * CHUNK_SIZE,
    ]
}

fn rubikgrid_layer_band_views(pos: ChunkPos) -> Vec<LayerBandViewV0> {
    let bands = [
        LayerBand {
            band_id: band_id(SHOWCASE_SEED, pos, -1),
            layer: -1,
            profile: LayerBandProfileV0::UnderfloorService,
            accessible: false,
            danger_profile: DangerProfileV0::None,
            resource_profile: ResourceProfileV0::None,
            anomaly_profile: AnomalyProfileV0::None,
        },
        LayerBand {
            band_id: band_id(SHOWCASE_SEED, pos, 0),
            layer: 0,
            profile: LayerBandProfileV0::Level0Classic,
            accessible: true,
            danger_profile: DangerProfileV0::Low,
            resource_profile: ResourceProfileV0::Sparse,
            anomaly_profile: AnomalyProfileV0::None,
        },
        LayerBand {
            band_id: band_id(SHOWCASE_SEED, pos, 1),
            layer: 1,
            profile: LayerBandProfileV0::Level0UpperOffice,
            accessible: false,
            danger_profile: DangerProfileV0::None,
            resource_profile: ResourceProfileV0::None,
            anomaly_profile: AnomalyProfileV0::MegastructureHint,
        },
    ];
    bands.iter().map(band_view).collect()
}

fn rubikgrid_access_views(pos: ChunkPos) -> Vec<VerticalAccessNodeViewV0> {
    let node = VerticalAccessNode {
        access_id: access_id(SHOWCASE_SEED, pos, 0),
        access_type: VerticalAccessTypeV0::Atrium,
        from_layer: 0,
        to_layer: -1,
        footprint_cell_min: [3, 3],
        footprint_cell_max: [7, 7],
        explicit: true,
    };
    vec![access_view(&node)]
}

/// Build the per-chunk render view (its 10×3×10 window of the global grid).
/// Faces use LOCAL window coordinates so the existing renderer places them with
/// the chunk's own origin; seams between adjacent showcase chunks carry no wall
/// because the global face generator never emitted one between two open cells.
pub fn showcase_chunk_view(pos: ChunkPos) -> Option<VolumetricGridViewV0> {
    if !is_showcase_chunk(pos) {
        return None;
    }
    let (gx0, gz0) = chunk_window_origin(pos);
    let (wnx, wny, wnz) = (CELLS_PER_CHUNK, SHOWCASE_NY, CELLS_PER_CHUNK);

    // Window cell codes (local index (y*wnz + z)*wnx + x).
    let mut cells = Vec::with_capacity((wnx * wny * wnz) as usize);
    let mut open = 0u32;
    let mut solid = 0u32;
    let mut openings = 0u32;
    for y in 0..wny {
        for z in 0..wnz {
            for x in 0..wnx {
                let occ = showcase_global(gx0 + x, y, gz0 + z);
                cells.push(occ.code());
                if occ.is_open() {
                    open += 1;
                } else {
                    solid += 1;
                }
                if occ.is_vertical_void()
                    && showcase_global(gx0 + x, y - 1, gz0 + z).is_vertical_void()
                {
                    openings += 1;
                }
            }
        }
    }

    // Faces owned by this chunk's cells, converted to local coordinates.
    let faces: Vec<VolumetricFaceViewV0> = showcase_global_faces()
        .iter()
        .filter(|f| {
            let (fx, fz) = (f.x as i32, f.z as i32);
            fx >= gx0 && fx < gx0 + wnx && fz >= gz0 && fz < gz0 + wnz
        })
        .map(|f| VolumetricFaceViewV0 {
            cell: [(f.x as i32 - gx0) as u8, f.y, (f.z as i32 - gz0) as u8],
            dir: f.dir.code(),
            kind: f.kind.code(),
        })
        .collect();

    Some(VolumetricGridViewV0 {
        active: true,
        column_id: column_id(SHOWCASE_SEED, pos),
        column_coord: [pos.0, pos.1],
        source: UNIFIED_COLUMN_SOURCE_RUBIKGRID.into(),
        dims: [wnx as u8, wny as u8, wnz as u8],
        cell_size_xz: LAYOUT_CELL_SIZE,
        layer_height: LAYER_HEIGHT,
        origin_world: chunk_origin_world(pos),
        base_layer: SHOWCASE_BASE_LAYER,
        cells,
        faces,
        open_cell_count: open,
        solid_cell_count: solid,
        vertical_connection_count: openings,
        valid_vertical_opening_count: openings,
        atrium_span: true,
        layer_bands: rubikgrid_layer_band_views(pos),
        vertical_access: rubikgrid_access_views(pos),
    })
}

/// Number of showcase chunk edges that face a normal (non-showcase) chunk —
/// i.e. clean transition boundaries between RubikGrid and normal Level 0.
pub fn showcase_transition_edge_count() -> u32 {
    let mut count = 0u32;
    for &pos in &SHOWCASE_CHUNKS {
        for (dx, dz) in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
            if !is_showcase_chunk((pos.0 + dx, pos.1 + dz)) {
                count += 1;
            }
        }
    }
    count
}

/// World-space bounds of the whole showcase: `[min_x, min_y, min_z, max_x, max_y, max_z]`.
pub fn showcase_bounds_world() -> [f32; 6] {
    let base_y = SHOWCASE_BASE_LAYER as f32 * LAYER_HEIGHT;
    [
        0.0,
        base_y,
        0.0,
        GNX as f32 * LAYOUT_CELL_SIZE,
        base_y + SHOWCASE_NY as f32 * LAYER_HEIGHT,
        GNZ as f32 * LAYOUT_CELL_SIZE,
    ]
}

/// Total global face count (after doorway removal) — for the B2 telemetry.
pub fn showcase_global_face_count() -> u32 {
    showcase_global_faces().len() as u32
}

/// Total global cell count.
pub fn showcase_global_cell_count() -> u32 {
    (GNX * SHOWCASE_NY * GNZ) as u32
}

// ─── Phase 3.0D — spawn volume validation ───

/// Backend spawn validity for one main-band cell of a volumetric column.
/// All flags must be true for a spawn-safe volume.
#[derive(Debug, Clone, Copy)]
pub struct SpawnVolumeReportV0 {
    pub inside_main_band: bool,
    pub walkable: bool,
    pub floor: bool,
    pub ceiling: bool,
    pub not_void: bool,
    pub not_shaft: bool,
    pub not_atrium: bool,
    pub not_pit: bool,
    pub not_danger: bool,
    pub not_blocked: bool,
    pub not_edge_leak: bool,
    pub nearby_architecture: bool,
}

impl SpawnVolumeReportV0 {
    pub fn is_valid(&self) -> bool {
        self.inside_main_band
            && self.walkable
            && self.floor
            && self.ceiling
            && self.not_void
            && self.not_shaft
            && self.not_atrium
            && self.not_pit
            && self.not_danger
            && self.not_blocked
            && self.not_edge_leak
            && self.nearby_architecture
    }
}

/// The volumetric grid that actually represents a chunk's served architecture:
/// the showcase window for RubikGrid chunks, the Level 0 column otherwise.
pub fn spawn_check_grid(world_seed: u64, chunk: &Chunk) -> VolumetricGridV0 {
    if world_seed == SHOWCASE_SEED && is_showcase_chunk(chunk.pos) {
        showcase_window_grid(chunk.pos)
    } else {
        build_level0_column(world_seed, chunk).grid
    }
}

fn showcase_window_grid(pos: ChunkPos) -> VolumetricGridV0 {
    let (gx0, gz0) = chunk_window_origin(pos);
    let mut grid = VolumetricGridV0::filled(
        CELLS_PER_CHUNK as u8,
        SHOWCASE_NY as u8,
        CELLS_PER_CHUNK as u8,
        SHOWCASE_BASE_LAYER,
        CellOccupancy::Solid,
    );
    for y in 0..SHOWCASE_NY {
        for z in 0..CELLS_PER_CHUNK {
            for x in 0..CELLS_PER_CHUNK {
                grid.set(
                    x as u8,
                    y as u8,
                    z as u8,
                    showcase_global(gx0 + x, y, gz0 + z),
                );
            }
        }
    }
    grid
}

/// Grid row holding world macro layer 0 (the main playable band).
fn main_band_row(grid: &VolumetricGridV0) -> Option<u8> {
    let y = -(grid.base_layer as i32);
    (y >= 0 && y < grid.ny as i32).then_some(y as u8)
}

/// Fast spawn filter on the served volumetric grid: the cell must be a
/// walkable, non-danger main-band cell that is not a vertical void and does
/// not sit on the lip of a shaft/atrium opening.
pub fn spawn_cell_volume_ok(grid: &VolumetricGridV0, x: usize, z: usize) -> bool {
    let Some(y) = main_band_row(grid) else {
        return false;
    };
    if x >= grid.nx as usize || z >= grid.nz as usize {
        return false;
    }
    let (x, z) = (x as u8, z as u8);
    let cell = grid.cell(x, y, z);
    if !cell.is_walkable() || cell == CellOccupancy::DangerZone {
        return false;
    }
    FaceDir::HORIZONTAL
        .iter()
        .all(|&dir| !grid.neighbour(x, y, z, dir).is_vertical_void())
}

/// Full Phase 3.0D spawn-volume report for one cell of one chunk.
pub fn spawn_volume_report(
    world_seed: u64,
    chunk: &Chunk,
    x: usize,
    z: usize,
) -> SpawnVolumeReportV0 {
    let grid = spawn_check_grid(world_seed, chunk);
    let row = main_band_row(&grid);
    let in_bounds = x < grid.nx as usize && z < grid.nz as usize;
    let inside_main_band = row.is_some() && in_bounds;
    if !inside_main_band {
        return SpawnVolumeReportV0 {
            inside_main_band: false,
            walkable: false,
            floor: false,
            ceiling: false,
            not_void: false,
            not_shaft: false,
            not_atrium: false,
            not_pit: false,
            not_danger: false,
            not_blocked: false,
            not_edge_leak: false,
            nearby_architecture: false,
        };
    }
    let y = row.unwrap_or(0);
    let (cx, cz) = (x as u8, z as u8);
    let cell = grid.cell(cx, y, cz);
    let below = grid.neighbour(cx, y, cz, FaceDir::Down);
    let above = grid.neighbour(cx, y, cz, FaceDir::Up);
    let beside_kind = |kind: CellOccupancy| {
        FaceDir::HORIZONTAL
            .iter()
            .any(|&dir| grid.neighbour(cx, y, cz, dir) == kind)
    };
    let flags = chunk.layout.cell_flags(x, z);

    // Nearby readable architecture: any structural mass / support core in the
    // main band, or any full wall edge in the layout, within 4 cells.
    let mut nearby_architecture = false;
    for dz in -4i32..=4 {
        for dx in -4i32..=4 {
            let (nx, nz) = (x as i32 + dx, z as i32 + dz);
            if nx < 0 || nz < 0 || nx >= grid.nx as i32 || nz >= grid.nz as i32 {
                continue;
            }
            let neighbour = grid.cell(nx as u8, y, nz as u8);
            if !neighbour.is_open() || neighbour.blocks_horizontally() {
                nearby_architecture = true;
            }
            let (ux, uz) = (nx as usize, nz as usize);
            if edge_is_full_wall(chunk.layout.edge_v(ux, uz))
                || edge_is_full_wall(chunk.layout.edge_v(ux + 1, uz))
                || edge_is_full_wall(chunk.layout.edge_h(ux, uz))
                || edge_is_full_wall(chunk.layout.edge_h(ux, uz + 1))
            {
                nearby_architecture = true;
            }
        }
    }

    SpawnVolumeReportV0 {
        inside_main_band,
        walkable: cell.is_walkable(),
        floor: !(cell.is_vertical_void() && below.is_vertical_void()),
        ceiling: !(cell.is_vertical_void() && above.is_vertical_void()),
        not_void: !cell.is_vertical_void() && cell != CellOccupancy::FalseSpace,
        not_shaft: cell != CellOccupancy::Shaft && !beside_kind(CellOccupancy::Shaft),
        not_atrium: cell != CellOccupancy::AtriumVoid && !beside_kind(CellOccupancy::AtriumVoid),
        not_pit: flags & CELL_PIT == 0 && chunk.layout.floor_profile != FLOOR_PIT_PLACEHOLDER,
        not_danger: cell != CellOccupancy::DangerZone && flags & CELL_HAZARD == 0,
        not_blocked: !cell.is_filled() && flags & CELL_BLOCKED == 0,
        not_edge_leak: x >= 1 && z >= 1 && x + 1 < grid.nx as usize && z + 1 < grid.nz as usize,
        nearby_architecture,
    }
}

// ─── Phase 3.0D — aggregate artifact checks + telemetry ───

/// World-level multilayer artifact flags `(checkerboard, floating_lattice)`.
pub fn multilayer_artifact_flags(columns: &[VolumetricColumn]) -> (bool, bool) {
    let mut checker_columns = 0usize;
    let mut upper_lower_open = 0u64;
    let mut unsupported = 0u64;
    for column in columns {
        if column.grid.band_is_checkerboard(0) || column.grid.band_is_checkerboard(2) {
            checker_columns += 1;
        }
        unsupported += u64::from(column.grid.unsupported_open_cell_count());
        upper_lower_open +=
            u64::from(column.grid.band_open_count(0) + column.grid.band_open_count(2));
    }
    let checkerboard = checker_columns * 2 > columns.len().max(1);
    let floating_lattice = upper_lower_open > 0 && unsupported * 4 > upper_lower_open;
    (checkerboard, floating_lattice)
}

/// Phase 3.0D — once-per-process audit proving the true multi-layer columns:
/// real upper/main/lower cell counts, explicit vertical access census, and the
/// checkerboard / floating-lattice artifact check. Aggregate sums only, so
/// unordered chunk iteration cannot affect the logged values.
pub fn log_v30d_multilayer_once(world_seed: u64, chunks: &[&Chunk]) {
    static LOGGED: AtomicBool = AtomicBool::new(false);
    if LOGGED.swap(true, Ordering::Relaxed) {
        return;
    }

    let columns: Vec<VolumetricColumn> = chunks
        .iter()
        .filter(|c| c.layer == 0 && !(world_seed == SHOWCASE_SEED && is_showcase_chunk(c.pos)))
        .map(|c| build_level0_column(world_seed, c))
        .collect();

    let mut upper_cells = 0u64;
    let mut main_cells = 0u64;
    let mut lower_cells = 0u64;
    let mut shafts = 0u32;
    let mut atriums = 0u32;
    let mut ramps = 0u32;
    let mut sealed = 0u32;
    for column in &columns {
        upper_cells += u64::from(column.grid.band_open_count(2));
        main_cells += u64::from(column.grid.band_open_count(1));
        lower_cells += u64::from(column.grid.band_open_count(0));
        for node in &column.vertical_access {
            match node.access_type {
                VerticalAccessTypeV0::Shaft
                | VerticalAccessTypeV0::Stairwell
                | VerticalAccessTypeV0::Vent
                | VerticalAccessTypeV0::ElevatorPlaceholder => shafts += 1,
                VerticalAccessTypeV0::Atrium => atriums += 1,
                VerticalAccessTypeV0::ServiceRampPlaceholder => ramps += 1,
                VerticalAccessTypeV0::SealedAbove | VerticalAccessTypeV0::SealedBelow => {
                    sealed += 1
                }
                _ => {}
            }
        }
    }
    let (checkerboard, floating_lattice) = multilayer_artifact_flags(&columns);

    info!("MPTRACE step=V30D event=true_multilayer_columns_enabled enabled=true seed={world_seed}");
    info!(
        "MPTRACE step=V30D event=multilayer_band_counts columns={} upper_cells={upper_cells} main_cells={main_cells} lower_cells={lower_cells}",
        columns.len()
    );
    info!(
        "MPTRACE step=V30D event=vertical_access_counts shafts={shafts} atriums={atriums} ramps={ramps} sealed={sealed}"
    );
    info!(
        "MPTRACE step=V30D event=multilayer_artifact_check checkerboard={checkerboard} floating_lattice={floating_lattice}"
    );
}

// ─── IPC view types ───

/// One renderable face, flattened for the wire (mirrors [`Face`]).
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct VolumetricFaceViewV0 {
    /// Owning cell `[x, y, z]`.
    pub cell: [u8; 3],
    /// [`FaceDir`] code.
    pub dir: u8,
    /// [`FaceKind`] code.
    pub kind: u8,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct LayerBandViewV0 {
    pub band_id: u32,
    pub layer: ChunkLayer,
    pub profile: String,
    pub profile_code: u8,
    pub accessible: bool,
    pub danger_profile: String,
    pub resource_profile: String,
    pub anomaly_profile: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct VerticalAccessNodeViewV0 {
    pub access_id: u32,
    pub access_type: String,
    pub access_type_code: u8,
    pub from_layer: ChunkLayer,
    pub to_layer: ChunkLayer,
    pub footprint_cell_min: [u8; 2],
    pub footprint_cell_max: [u8; 2],
    pub explicit: bool,
}

/// Backend-authored volumetric grid shipped to Unity for rendering. Attached to
/// a single near-spawn host chunk; render-only (no movement/collision meaning).
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct VolumetricGridViewV0 {
    pub active: bool,
    #[serde(default)]
    pub column_id: u64,
    #[serde(default)]
    pub column_coord: [i32; 2],
    #[serde(default)]
    pub source: String,
    /// `[nx, ny, nz]`.
    pub dims: [u8; 3],
    pub cell_size_xz: f32,
    pub layer_height: f32,
    /// World min corner of cell `(0, 0, 0)`.
    pub origin_world: [f32; 3],
    /// World macro layer of grid row `y == 0`.
    pub base_layer: i8,
    /// Occupancy codes, index `(y*nz + z)*nx + x`.
    pub cells: Vec<u8>,
    pub faces: Vec<VolumetricFaceViewV0>,
    pub open_cell_count: u32,
    pub solid_cell_count: u32,
    pub vertical_connection_count: u32,
    /// Count of boundaries where a floor/ceiling is legitimately omitted because
    /// a real vertical opening continues through it. The renderer must not leave
    /// a hole anywhere else.
    #[serde(default)]
    pub valid_vertical_opening_count: u32,
    pub atrium_span: bool,
    #[serde(default)]
    pub layer_bands: Vec<LayerBandViewV0>,
    #[serde(default)]
    pub vertical_access: Vec<VerticalAccessNodeViewV0>,
}

/// Phase 3.0C-FIX — once-per-process audit of the Level 0 → volumetric adapter.
/// Proves in logs that normal chunks convert with mixed layout-based semantics
/// (not flattened corridor grids), spawn stays readable, long corridors
/// survive, upper/lower bands are hint-only mass, and no stray faces leak
/// outside the main band without explicit vertical access.
pub fn log_level0_adapter_fix_once(world_seed: u64, chunks: &[&Chunk]) {
    static LOGGED: AtomicBool = AtomicBool::new(false);
    if LOGGED.swap(true, Ordering::Relaxed) {
        return;
    }

    let mut rooms = 0u32;
    let mut corridors = 0u32;
    let mut sealed = 0u32;
    let mut blocked = 0u32;
    let mut service = 0u32;
    let mut support = 0u32;
    let mut danger = 0u32;
    let mut safe = 0u32;
    let mut access = 0u32;
    let mut stray_faces = 0u32;
    let mut long_corridor = false;
    // For the showcase seed the spawn chunk is RubikGrid-authored; its spawn
    // cell (5,5) readability comes from the global showcase plan instead.
    let mut spawn_readable = world_seed == SHOWCASE_SEED && showcase_global(5, 1, 5).is_walkable();

    for chunk in chunks {
        if chunk.layer != 0 || (world_seed == SHOWCASE_SEED && is_showcase_chunk(chunk.pos)) {
            continue;
        }
        let column = build_level0_column(world_seed, chunk);
        let g = column.grid.nx;
        for z in 0..column.grid.nz {
            let mut run = 0u32;
            for x in 0..g {
                match column.grid.cell(x, 1, z) {
                    CellOccupancy::Room => rooms += 1,
                    CellOccupancy::Corridor => corridors += 1,
                    CellOccupancy::SealedRoom => sealed += 1,
                    CellOccupancy::Blocked => blocked += 1,
                    CellOccupancy::ServiceSpace | CellOccupancy::UnderfloorService => service += 1,
                    CellOccupancy::SupportCore => support += 1,
                    CellOccupancy::DangerZone => danger += 1,
                    CellOccupancy::SafeNode => safe += 1,
                    _ => {}
                }
                // Long-corridor probe: ≥4 contiguous corridor cells in a row.
                if column.grid.cell(x, 1, z) == CellOccupancy::Corridor {
                    run += 1;
                    if run >= 4 {
                        long_corridor = true;
                    }
                } else {
                    run = 0;
                }
            }
        }
        access += column.vertical_access.len() as u32;
        if column.vertical_access.is_empty() {
            stray_faces += column
                .grid
                .generate_faces()
                .iter()
                .filter(|f| f.y != 1)
                .count() as u32;
        }
        if chunk.pos == (0, 0) {
            let core_walkable = (4..=5)
                .all(|x| (4..=5).all(|z| column.grid.cell(x as u8, 1, z as u8).is_walkable()));
            let no_danger = !column
                .grid
                .cells
                .iter()
                .any(|c| *c == CellOccupancy::DangerZone);
            spawn_readable = core_walkable && no_danger;
        }
    }

    info!("MPTRACE step=V30CFIX event=level0_volumetric_adapter_fix_enabled enabled=true seed={world_seed}");
    info!(
        "MPTRACE step=V30CFIX event=level0_volumetric_semantic_counts rooms={rooms} corridors={corridors} sealed={sealed} blocked={blocked} service={service} support={support} danger={danger} safe={safe} access={access}"
    );
    info!("MPTRACE step=V30CFIX event=level0_volumetric_spawn_readable readable={spawn_readable}");
    info!(
        "MPTRACE step=V30CFIX event=level0_volumetric_long_corridor_preserved preserved={long_corridor}"
    );
    info!("MPTRACE step=V30CFIX event=level0_volumetric_upper_lower_hint_mode enabled=true");
    info!(
        "MPTRACE step=V30CFIX event=level0_volumetric_invalid_grid_artifacts artifacts={stray_faces}"
    );
    info!("MPTRACE step=V30CFIX event=level0_volumetric_visual_grammar_ready ready=true");
}

/// Emit the required `rubik_grid_v0_*` + Phase 3.0B2 telemetry once per process.
/// `visfix_clutter_disabled` records that the old decorative VISFIX overlay is
/// suppressed by default in favour of this volumetric model.
pub fn log_showcase_once(visfix_clutter_disabled: bool) {
    static LOGGED: AtomicBool = AtomicBool::new(false);
    if LOGGED.swap(true, Ordering::Relaxed) {
        return;
    }
    let global = build_showcase_global_grid();
    let bounds = showcase_bounds_world();
    let opening_count = global.valid_vertical_opening_count();
    let transition_count = showcase_transition_edge_count();

    info!("MPTRACE step=RUBIK event=rubik_grid_v0_seed_7778_active seed={SHOWCASE_SEED} showcase_chunks={SHOWCASE_CHUNKS:?}");
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_cell_count cell_count={}",
        showcase_global_cell_count()
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_layer_count layer_count={SHOWCASE_NY} base_layer={SHOWCASE_BASE_LAYER}"
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_open_cell_count open_cell_count={}",
        global.open_cell_count()
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_solid_cell_count solid_cell_count={}",
        global.solid_cell_count()
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_atrium_span_created atrium_span_created={}",
        global.has_atrium_span()
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_face_count face_count={}",
        showcase_global_face_count()
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_valid_vertical_opening_count valid_vertical_opening_count={opening_count}"
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_visfix_clutter_disabled disabled={visfix_clutter_disabled}"
    );

    // ── Phase 3.0B2 multi-chunk showcase telemetry ──
    info!("MPTRACE step=RUBIK event=rubik_grid_v0_b2_enabled enabled=true seed={SHOWCASE_SEED}");
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_b2_chunk_count chunk_count={} chunks={SHOWCASE_CHUNKS:?}",
        SHOWCASE_CHUNKS.len()
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_b2_showcase_bounds min=({:.1},{:.1},{:.1}) max=({:.1},{:.1},{:.1})",
        bounds[0], bounds[1], bounds[2], bounds[3], bounds[4], bounds[5]
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_b2_cell_count cell_count={}",
        showcase_global_cell_count()
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_b2_face_count face_count={}",
        showcase_global_face_count()
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_b2_vertical_opening_count vertical_opening_count={opening_count}"
    );
    info!(
        "MPTRACE step=RUBIK event=rubik_grid_v0_b2_transition_count transition_count={transition_count} doorways={}",
        TRANSITION_DOORWAYS.len()
    );
    info!("MPTRACE step=RUBIK event=rubik_grid_v0_b2_visual_grammar_ready ready=true");
}

#[cfg(test)]
mod tests {
    use super::*;

    fn physical_opening_access_codes() -> [u8; 8] {
        [
            VerticalAccessTypeV0::Atrium.code(),
            VerticalAccessTypeV0::Shaft.code(),
            VerticalAccessTypeV0::CollapsedFloor.code(),
            VerticalAccessTypeV0::BrokenCeiling.code(),
            VerticalAccessTypeV0::Vent.code(),
            VerticalAccessTypeV0::Stairwell.code(),
            VerticalAccessTypeV0::ElevatorPlaceholder.code(),
            VerticalAccessTypeV0::NoclipAnomaly.code(),
        ]
    }

    fn is_physical_opening_access(node: &VerticalAccessNodeViewV0) -> bool {
        physical_opening_access_codes().contains(&node.access_type_code)
    }

    fn assert_bounded_access_node(node: &VerticalAccessNodeViewV0, nx: u8, nz: u8) {
        assert!(node.explicit, "vertical access metadata must be explicit");
        assert!(
            node.footprint_cell_min[0] < node.footprint_cell_max[0]
                && node.footprint_cell_min[1] < node.footprint_cell_max[1],
            "vertical access footprint must be non-empty"
        );
        assert!(
            node.footprint_cell_max[0] <= nx && node.footprint_cell_max[1] <= nz,
            "vertical access footprint must stay inside the column"
        );
    }

    #[test]
    fn global_showcase_has_three_layers_and_2x2_footprint() {
        let grid = build_showcase_global_grid();
        assert_eq!(grid.ny as i32, SHOWCASE_NY, "showcase has 3 stacked layers");
        assert_eq!(grid.nx as i32, GNX);
        assert_eq!(grid.nz as i32, GNZ);
        assert_eq!(grid.base_layer, -1, "lowest layer is -1");
        assert_eq!(grid.cells.len(), (GNX * SHOWCASE_NY * GNZ) as usize);
        assert_eq!(showcase_chunks().len(), 4);
    }

    #[test]
    fn global_showcase_has_multilayer_atrium_and_vertical_connections() {
        let grid = build_showcase_global_grid();
        assert!(grid.has_atrium_span(), "atrium must span multiple layers");
        assert!(
            grid.vertical_connection_count() > 8,
            "more openings than B1"
        );
        // Atrium present on every layer at the same footprint (aligned).
        for y in 0..grid.ny {
            assert_eq!(grid.cell(9, y, 11), CellOccupancy::AtriumVoid);
            assert_eq!(grid.cell(10, y, 12), CellOccupancy::AtriumVoid);
        }
    }

    #[test]
    fn global_showcase_has_room_corridor_service_shaft_support_semantics() {
        // Ground floor (gy=1) must contain each required region kind.
        let kinds: Vec<CellOccupancy> = (0..GNX)
            .flat_map(|x| (0..GNZ).map(move |z| showcase_global(x, 1, z)))
            .collect();
        assert!(kinds.contains(&CellOccupancy::Room));
        assert!(kinds.contains(&CellOccupancy::Corridor));
        assert!(kinds.contains(&CellOccupancy::ServiceSpace));
        assert!(kinds.contains(&CellOccupancy::Shaft));
        assert!(kinds.contains(&CellOccupancy::AtriumVoid));
        assert!(kinds.contains(&CellOccupancy::SupportCore));
        assert!(kinds.contains(&CellOccupancy::Solid));
        // Lower layer has a service/underfloor ring; upper layer a gallery ring.
        assert_eq!(showcase_global(8, 0, 9), CellOccupancy::ServiceSpace);
        assert_eq!(showcase_global(8, 2, 9), CellOccupancy::Room);
        // Support cores span all three layers (real structural columns).
        for y in 0..SHOWCASE_NY {
            assert_eq!(showcase_global(7, y, 9), CellOccupancy::SupportCore);
        }
    }

    #[test]
    fn vertical_openings_only_where_up_down_voids_align() {
        let grid = build_showcase_global_grid();
        let faces = grid.generate_faces();
        let mut omitted = 0u32;
        for y in 0..grid.ny {
            for z in 0..grid.nz {
                for x in 0..grid.nx {
                    let cell = grid.cell(x, y, z);
                    if !cell.emits_surfaces() {
                        continue;
                    }
                    let has_floor = faces
                        .iter()
                        .any(|f| f.x == x && f.y == y && f.z == z && f.kind == FaceKind::Floor);
                    if !has_floor {
                        let below = grid.neighbour(x, y, z, FaceDir::Down);
                        assert!(
                            cell.is_vertical_void() && below.is_vertical_void(),
                            "illegal floor hole at ({x},{y},{z})"
                        );
                        omitted += 1;
                    }
                }
            }
        }
        assert_eq!(omitted, grid.valid_vertical_opening_count());
    }

    #[test]
    fn closed_cells_generate_floor_and_ceiling() {
        let grid = build_showcase_global_grid();
        let faces = grid.generate_faces();
        for y in 0..grid.ny {
            for z in 0..grid.nz {
                for x in 0..grid.nx {
                    let cell = grid.cell(x, y, z);
                    if !cell.emits_surfaces() || cell.is_vertical_void() {
                        continue;
                    }
                    let has_floor = faces
                        .iter()
                        .any(|f| f.x == x && f.y == y && f.z == z && f.kind == FaceKind::Floor);
                    let has_ceiling = faces
                        .iter()
                        .any(|f| f.x == x && f.y == y && f.z == z && f.kind == FaceKind::Ceiling);
                    assert!(has_floor, "closed cell ({x},{y},{z}) is missing a floor");
                    assert!(
                        has_ceiling,
                        "closed cell ({x},{y},{z}) is missing a ceiling"
                    );
                }
            }
        }
    }

    #[test]
    fn rim_treatment_only_around_vertical_openings() {
        let grid = build_showcase_global_grid();
        let faces = grid.generate_faces();
        for f in faces
            .iter()
            .filter(|f| f.kind == FaceKind::Rim || f.kind == FaceKind::Railing)
        {
            let cell = grid.cell(f.x, f.y, f.z);
            assert!(cell.is_walkable(), "edge treatment on a non-walkable cell");
            assert!(
                grid.neighbour(f.x, f.y, f.z, f.dir).is_vertical_void(),
                "edge treatment not facing a vertical opening"
            );
        }
        assert!(faces.iter().any(|f| f.kind == FaceKind::Rim));
    }

    #[test]
    fn showcase_covers_multiple_chunks_deterministically() {
        for &pos in &SHOWCASE_CHUNKS {
            let a = showcase_chunk_view(pos).expect("showcase chunk view");
            let b = showcase_chunk_view(pos).expect("showcase chunk view");
            assert_eq!(a, b, "per-chunk view must be deterministic for {pos:?}");
            assert!(a.active);
            assert_eq!(a.dims, [10, 3, 10]);
            assert_eq!(a.cells.len(), 300);
            assert!(!a.faces.is_empty());
        }
        // A non-showcase chunk has no volumetric view.
        assert!(showcase_chunk_view((5, 5)).is_none());
        // Every face's owning cell stays inside its chunk's local window.
        let view = showcase_chunk_view((1, 1)).unwrap();
        assert!(view
            .faces
            .iter()
            .all(|f| f.cell[0] < 10 && f.cell[1] < 3 && f.cell[2] < 10));
    }

    #[test]
    fn adjacent_showcase_chunks_connect_without_double_seam_walls() {
        // Global cells (9,1,5) and (10,1,5) are both Room → the chunk seam at
        // x=10 must carry NO wall on either side (they read as one space).
        assert!(showcase_global(9, 1, 5).is_walkable());
        assert!(showcase_global(10, 1, 5).is_walkable());

        let west = showcase_chunk_view((0, 0)).unwrap(); // owns global x 0..9
        let east = showcase_chunk_view((1, 0)).unwrap(); // owns global x 10..19
        let west_wall = west.faces.iter().any(|f| {
            f.cell == [9, 1, 5] && f.dir == FaceDir::East.code() && f.kind == FaceKind::Wall.code()
        });
        let east_wall = east.faces.iter().any(|f| {
            f.cell == [0, 1, 5] && f.dir == FaceDir::West.code() && f.kind == FaceKind::Wall.code()
        });
        assert!(
            !west_wall && !east_wall,
            "seam must be open, not a double wall"
        );
    }

    #[test]
    fn transitions_to_normal_chunks_exist_and_are_stable() {
        // The 2×2 block has 8 outer edges facing normal Level 0 chunks.
        assert_eq!(showcase_transition_edge_count(), 8);
        // The clean perimeter doorways are punched out (no wall there).
        let faces = showcase_global_faces();
        for &(gx, gy, gz, dir) in &TRANSITION_DOORWAYS {
            assert!(
                !faces.iter().any(|f| f.x as i32 == gx
                    && f.y as i32 == gy
                    && f.z as i32 == gz
                    && f.dir == dir
                    && matches!(f.kind, FaceKind::Wall | FaceKind::ShaftWall)),
                "transition doorway at ({gx},{gy},{gz}) must be open"
            );
        }
    }

    #[test]
    fn face_generation_is_deterministic() {
        let a = build_showcase_global_grid().generate_faces();
        let b = build_showcase_global_grid().generate_faces();
        assert_eq!(a, b, "face generation must be deterministic");
    }

    #[test]
    fn per_chunk_view_counts_are_consistent() {
        let view = showcase_chunk_view((0, 0)).unwrap();
        assert_eq!(view.open_cell_count + view.solid_cell_count, 300);
        assert_eq!(
            view.valid_vertical_opening_count,
            view.vertical_connection_count
        );
        assert!(view.atrium_span);
    }

    #[test]
    fn normal_level0_chunk_converts_to_unified_column() {
        let chunk = crate::world::generator::generate_chunk(42, (0, 0));
        let view = level0_column_view(42, &chunk);
        assert!(view.active);
        assert_eq!(view.source, UNIFIED_COLUMN_SOURCE_LEVEL0);
        assert_eq!(view.column_coord, [0, 0]);
        assert_eq!(view.dims, [10, 3, 10]);
        assert_eq!(view.cells.len(), 300);
        assert_eq!(view.layer_bands.len(), 3);
        assert_eq!(view.layer_bands[0].layer, -1);
        assert_eq!(view.layer_bands[1].layer, 0);
        assert_eq!(view.layer_bands[2].layer, 1);
        assert!(!view.layer_bands[0].accessible);
        assert!(view.layer_bands[1].accessible);
        assert!(!view.layer_bands[2].accessible);
        assert!(!view.faces.is_empty());
    }

    #[test]
    fn normal_column_has_upper_lower_bands_without_physical_openings() {
        let chunk = crate::world::generator::generate_chunk(42, (0, 0));
        let column = build_level0_column(42, &chunk);
        let view = level0_column_view(42, &chunk);
        assert!(
            !view.vertical_access.is_empty(),
            "Phase 3.0D normal columns should expose structural relationship nodes"
        );
        assert!(
            view.vertical_access
                .iter()
                .all(|node| !is_physical_opening_access(node)),
            "normal spawn column should not invent physical vertical openings"
        );
        for node in &view.vertical_access {
            assert_bounded_access_node(node, view.dims[0], view.dims[2]);
        }
        assert!(
            view.vertical_access
                .iter()
                .any(|node| node.access_type == VerticalAccessTypeV0::SharedFloorCeiling.as_str()),
            "normal columns should describe shared floor/ceiling structure"
        );
        assert_eq!(
            view.valid_vertical_opening_count, 0,
            "normal column must not create random holes"
        );
        // Phase 3.0D: upper/lower bands are real generated architecture, not
        // the old hint-mode hidden mass and not a lattice/checkerboard artifact.
        assert_eq!(view.layer_bands.len(), 3);
        assert!(
            column.grid.band_open_count(0) > 0,
            "lower band should contain real architecture"
        );
        assert!(
            column.grid.band_open_count(2) > 0,
            "upper band should contain real architecture"
        );
        assert!(
            !column.grid.band_is_checkerboard(0) && !column.grid.band_is_checkerboard(2),
            "upper/lower bands must not regress to checkerboard artifacts"
        );
        assert_eq!(
            column.grid.unsupported_open_cell_count(),
            0,
            "upper/lower architecture must track the main band instead of floating"
        );
    }

    #[test]
    fn level0_adapter_produces_mixed_semantics_not_all_corridors() {
        // Across the initial world, the adapter must produce BOTH room and
        // corridor cells (layout-based), never a flat all-corridor conversion.
        for seed in [42u64, 7778] {
            let chunks = crate::world::generator::generate_initial_structure_chunks(seed);
            let mut rooms = 0u32;
            let mut corridors = 0u32;
            for (_, chunk) in chunks.iter().filter(|(_, c)| c.layer == 0) {
                if seed == SHOWCASE_SEED && is_showcase_chunk(chunk.pos) {
                    continue;
                }
                let column = build_level0_column(seed, chunk);
                for z in 0..column.grid.nz {
                    for x in 0..column.grid.nx {
                        match column.grid.cell(x, 1, z) {
                            CellOccupancy::Room => rooms += 1,
                            CellOccupancy::Corridor => corridors += 1,
                            _ => {}
                        }
                    }
                }
            }
            assert!(rooms > 0, "seed {seed}: adapter produced no Room cells");
            assert!(
                corridors > 0,
                "seed {seed}: adapter produced no Corridor cells"
            );
            assert!(
                rooms * 100 >= (rooms + corridors) * 10,
                "seed {seed}: rooms almost vanished ({rooms} rooms vs {corridors} corridors)"
            );
        }
    }

    #[test]
    fn level0_long_corridor_readability_preserved() {
        // Some chunk in the initial world must carry a ≥4-cell straight
        // corridor run — the long-hallway Level 0 identity.
        let chunks = crate::world::generator::generate_initial_structure_chunks(42);
        let mut found = false;
        'outer: for (_, chunk) in chunks.iter().filter(|(_, c)| c.layer == 0) {
            let column = build_level0_column(42, chunk);
            for z in 0..column.grid.nz {
                let mut run = 0;
                for x in 0..column.grid.nx {
                    if column.grid.cell(x, 1, z) == CellOccupancy::Corridor {
                        run += 1;
                        if run >= 4 {
                            found = true;
                            break 'outer;
                        }
                    } else {
                        run = 0;
                    }
                }
            }
            for x in 0..column.grid.nx {
                let mut run = 0;
                for z in 0..column.grid.nz {
                    if column.grid.cell(x, 1, z) == CellOccupancy::Corridor {
                        run += 1;
                        if run >= 4 {
                            found = true;
                            break 'outer;
                        }
                    } else {
                        run = 0;
                    }
                }
            }
        }
        assert!(found, "no long corridor run survived the adapter");
    }

    #[test]
    fn level0_spawn_chunk_stays_readable_and_danger_free() {
        let mut columns = Vec::new();
        for seed in [42u64, 7778] {
            let chunks = crate::world::generator::generate_initial_structure_chunks(seed);
            columns.extend(
                chunks
                    .iter()
                    .map(|(_, c)| c)
                    .filter(|c| {
                        c.layer == 0 && !(seed == SHOWCASE_SEED && is_showcase_chunk(c.pos))
                    })
                    .map(|c| build_level0_column(seed, c)),
            );
            let starter = chunks
                .iter()
                .map(|(_, c)| c)
                .find(|c| c.pos == (0, 0) && c.layer == 0)
                .expect("starter chunk");
            let column = build_level0_column(seed, starter);
            // Spawn core walkable in the main band, and no danger pocket
            // invades the spawn chunk.
            for x in 4..=5u8 {
                for z in 4..=5u8 {
                    assert!(
                        column.grid.cell(x, 1, z).is_walkable(),
                        "seed {seed}: spawn core cell ({x},{z}) not walkable"
                    );
                }
            }
            assert!(
                !column
                    .grid
                    .cells
                    .iter()
                    .any(|c| *c == CellOccupancy::DangerZone),
                "seed {seed}: danger pocket invaded the spawn chunk"
            );
        }
        let (checkerboard, floating_lattice) = multilayer_artifact_flags(&columns);
        assert!(!checkerboard, "full-world checkerboard artifact returned");
        assert!(
            !floating_lattice,
            "full-world floating lattice artifact returned"
        );
    }

    #[test]
    fn physical_vertical_openings_only_exist_when_explicitly_authored() {
        let mut chunk = crate::world::generator::generate_chunk(42, (3, 3));
        let normal_view = level0_column_view(42, &chunk);
        assert!(
            normal_view
                .vertical_access
                .iter()
                .all(|node| !is_physical_opening_access(node)),
            "normal columns may have relationship nodes but not physical openings"
        );
        for node in &normal_view.vertical_access {
            assert_bounded_access_node(node, normal_view.dims[0], normal_view.dims[2]);
        }
        assert_eq!(
            normal_view.valid_vertical_opening_count, 0,
            "relationship nodes must not carve vertical holes"
        );

        chunk.layout.vertical_flags |= V30A_CONNECTOR;
        chunk.layout.floor_profile = FLOOR_CONNECTOR_DOWN;
        let view = level0_column_view(42, &chunk);
        let openings: Vec<&VerticalAccessNodeViewV0> = view
            .vertical_access
            .iter()
            .filter(|node| is_physical_opening_access(node))
            .collect();
        assert_eq!(openings.len(), 1);
        assert_eq!(
            openings[0].access_type,
            VerticalAccessTypeV0::Stairwell.as_str()
        );
        assert_bounded_access_node(openings[0], view.dims[0], view.dims[2]);
        assert!(
            view.valid_vertical_opening_count > 0,
            "explicit access should create valid vertical openings"
        );
    }

    #[test]
    fn unified_column_generation_is_seed_stable() {
        let chunk_a = crate::world::generator::generate_chunk(42, (4, -2));
        let chunk_b = crate::world::generator::generate_chunk(42, (4, -2));
        let a = level0_column_view(42, &chunk_a);
        let b = level0_column_view(42, &chunk_b);
        assert_eq!(a.column_id, b.column_id);
        assert_eq!(a.cells, b.cells);
        assert_eq!(a.faces, b.faces);
        assert_eq!(a.layer_bands, b.layer_bands);

        let other = level0_column_view(43, &crate::world::generator::generate_chunk(43, (4, -2)));
        assert_ne!(
            a.layer_bands, other.layer_bands,
            "different seeds should be able to choose different upper/lower profiles"
        );
    }

    #[test]
    fn rubikgrid_showcase_uses_unified_column_metadata() {
        let view = showcase_chunk_view((0, 0)).unwrap();
        assert_eq!(view.source, UNIFIED_COLUMN_SOURCE_RUBIKGRID);
        assert_eq!(view.layer_bands.len(), 3);
        assert!(!view.vertical_access.is_empty());
        assert_eq!(view.vertical_access[0].access_type, "ATRIUM");
        assert!(view.atrium_span);
    }

    #[test]
    fn unified_profile_cell_and_access_coverage_exists() {
        let profiles = [
            LayerBandProfileV0::Level0Classic,
            LayerBandProfileV0::Level0UpperFalseCeiling,
            LayerBandProfileV0::Level0UpperOffice,
            LayerBandProfileV0::Level0RemodeledOffice,
            LayerBandProfileV0::CeilingServiceVoid,
            LayerBandProfileV0::UnderfloorService,
            LayerBandProfileV0::ConcreteSublevel,
            LayerBandProfileV0::RedDangerZone,
            LayerBandProfileV0::DarkLobby,
            LayerBandProfileV0::ManilaSafeNode,
            LayerBandProfileV0::RemodeledMess,
            LayerBandProfileV0::SealedArchitecture,
            LayerBandProfileV0::MegastructureHint,
            LayerBandProfileV0::VoidOrExtremeAnomaly,
        ];
        assert_eq!(profiles.len(), 14);
        assert!(profiles.iter().all(|p| !p.as_str().is_empty()));

        let cells = [
            CellOccupancy::Solid,
            CellOccupancy::Room,
            CellOccupancy::Corridor,
            CellOccupancy::SealedRoom,
            CellOccupancy::FalseSpace,
            CellOccupancy::CeilingVoid,
            CellOccupancy::UnderfloorService,
            CellOccupancy::ServiceSpace,
            CellOccupancy::AtriumVoid,
            CellOccupancy::Shaft,
            CellOccupancy::SupportCore,
            CellOccupancy::Blocked,
            CellOccupancy::Transition,
            CellOccupancy::Anomaly,
            CellOccupancy::DangerZone,
            CellOccupancy::SafeNode,
        ];
        assert_eq!(cells.len(), 16);

        let access = [
            VerticalAccessTypeV0::None,
            VerticalAccessTypeV0::Atrium,
            VerticalAccessTypeV0::Shaft,
            VerticalAccessTypeV0::CollapsedFloor,
            VerticalAccessTypeV0::BrokenCeiling,
            VerticalAccessTypeV0::Vent,
            VerticalAccessTypeV0::Stairwell,
            VerticalAccessTypeV0::ElevatorPlaceholder,
            VerticalAccessTypeV0::NoclipAnomaly,
            VerticalAccessTypeV0::ManilaTransition,
            VerticalAccessTypeV0::RedRoomThreshold,
            VerticalAccessTypeV0::RemodeledDoor,
        ];
        assert_eq!(access.len(), 12);
        assert!(access.iter().all(|a| !a.as_str().is_empty()));
    }
}
