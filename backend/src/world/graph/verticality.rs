use serde::{Deserialize, Serialize};

use super::coords::Chunk3DCoord;
use super::edges::ConnectionKind;
use super::nodes::{SpatialNode, SpatialNodeId, SpatialNodeKind};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum VerticalConnectionKind {
    Stair,
    Ramp,
    Shaft,
    Atrium,
}

impl VerticalConnectionKind {
    pub fn as_node_kind(self) -> SpatialNodeKind {
        match self {
            Self::Stair => SpatialNodeKind::Stair,
            Self::Ramp => SpatialNodeKind::Ramp,
            Self::Shaft => SpatialNodeKind::Shaft,
            Self::Atrium => SpatialNodeKind::Atrium,
        }
    }

    pub fn as_connection_kind(self) -> ConnectionKind {
        match self {
            Self::Stair => ConnectionKind::Stair,
            Self::Ramp => ConnectionKind::Ramp,
            Self::Shaft => ConnectionKind::Shaft,
            Self::Atrium => ConnectionKind::AtriumOpening,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct VerticalConnection {
    pub from: SpatialNodeId,
    pub to: SpatialNodeId,
    pub connector: SpatialNodeId,
    pub kind: VerticalConnectionKind,
}

/// Audit-only materialization of a virtual ID referenced by a
/// [`VerticalConnection`]. Lives in `RegionGraph.virtual_vertical_nodes`,
/// never in `RegionGraph.nodes` — it is not legacy traversal, has no
/// collision and no render geometry.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct VirtualVerticalNode {
    pub id: SpatialNodeId,
    pub kind: SpatialNodeKind,
    pub coord: Chunk3DCoord,

    /// Local cell-space bounds inside a Chunk3D (inclusive min, exclusive max).
    pub local_min: [u8; 3],
    pub local_max: [u8; 3],

    /// Always false until traversal promotion is proven in a later phase.
    pub accessible: bool,
    pub perceptible: bool,
}

/// Materializes the virtual IDs (`to` upper space, `connector`) of each
/// vertical connection as auditable [`VirtualVerticalNode`]s. Pure function:
/// mutates nothing, deterministic for deterministic inputs. Connections whose
/// base node is missing are skipped (they fail the consistency audit instead).
pub fn materialize_virtual_vertical_nodes(
    nodes: &[SpatialNode],
    connections: &[VerticalConnection],
) -> Vec<VirtualVerticalNode> {
    let mut virtuals = Vec::with_capacity(connections.len() * 2);

    for vc in connections {
        let Some(base) = nodes.iter().find(|n| n.id == vc.from) else {
            continue;
        };

        virtuals.push(VirtualVerticalNode {
            id: vc.to,
            kind: SpatialNodeKind::SealedUpperSpace,
            coord: base.coord.above(),
            local_min: base.local_min,
            local_max: base.local_max,
            accessible: false,
            perceptible: true,
        });

        let (local_min, local_max) = connector_bounds(vc.kind);
        virtuals.push(VirtualVerticalNode {
            id: vc.connector,
            kind: vc.kind.as_node_kind(),
            coord: base.coord,
            local_min,
            local_max,
            accessible: false,
            perceptible: true,
        });
    }

    virtuals
}

/// Wire-facing debug placeholder for one [`VirtualVerticalNode`] (Phase 6.6).
/// World-space AABB so Unity can draw a marker/translucent box without any
/// graph knowledge. Debug-only: no collision, no traversal, no gameplay.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct VerticalDebugMarkerV0 {
    pub id: SpatialNodeId,
    /// "stair" | "ramp" | "shaft" | "atrium" | "sealed_upper" | "other"
    pub kind: String,
    pub world_min: [f32; 3],
    pub world_max: [f32; 3],
}

fn debug_marker_kind(kind: SpatialNodeKind) -> &'static str {
    match kind {
        SpatialNodeKind::Stair => "stair",
        SpatialNodeKind::Ramp => "ramp",
        SpatialNodeKind::Shaft => "shaft",
        SpatialNodeKind::Atrium => "atrium",
        SpatialNodeKind::SealedUpperSpace => "sealed_upper",
        _ => "other",
    }
}

/// Projects virtual vertical nodes into deterministic world-space debug
/// markers. Pure function over the already-deterministic virtual layer; same
/// coordinate math as [`SpatialNode::world_bounds_3d`].
pub fn export_vertical_debug_markers(
    virtuals: &[VirtualVerticalNode],
    cell_size: f32,
    grid_size: u8,
    layer_height: f32,
) -> Vec<VerticalDebugMarkerV0> {
    let chunk_world = cell_size * grid_size as f32;

    virtuals
        .iter()
        .map(|v| {
            let ox = v.coord.chunk_x as f32 * chunk_world;
            let oy = v.coord.chunk_y as f32 * layer_height;
            let oz = v.coord.chunk_z as f32 * chunk_world;
            VerticalDebugMarkerV0 {
                id: v.id,
                kind: debug_marker_kind(v.kind).to_string(),
                world_min: [
                    ox + v.local_min[0] as f32 * cell_size,
                    oy + v.local_min[1] as f32 * cell_size,
                    oz + v.local_min[2] as f32 * cell_size,
                ],
                world_max: [
                    ox + v.local_max[0] as f32 * cell_size,
                    oy + v.local_max[1] as f32 * cell_size,
                    oz + v.local_max[2] as f32 * cell_size,
                ],
            }
        })
        .collect()
}

/// Builds the parallel verticality layer without mutating `nodes`.
///
/// Virtual IDs for the (not yet materialized) upper space and connector are
/// allocated from `next_virtual_id`, which the caller seeds from
/// `max(node.id) + 1` so they never collide with legacy node IDs.
/// Deterministic: input slice order is deterministic (nodes sorted by id at
/// build time) and the spawn/kind decisions are pure hashes of coord + id.
pub fn build_basic_vertical_connections(
    nodes: &[SpatialNode],
    next_virtual_id: &mut SpatialNodeId,
) -> Vec<VerticalConnection> {
    let mut connections = Vec::new();

    let base_nodes: Vec<&SpatialNode> = nodes
        .iter()
        .filter(|n| {
            n.accessible
                && n.coord.chunk_y == 0
                && matches!(
                    n.kind,
                    SpatialNodeKind::Room
                        | SpatialNodeKind::Corridor
                        | SpatialNodeKind::Intersection
                )
        })
        .collect();

    for base in base_nodes {
        if !should_spawn_vertical_connection(base) {
            continue;
        }

        let upper_id = *next_virtual_id;
        *next_virtual_id += 1;

        let connector_id = *next_virtual_id;
        *next_virtual_id += 1;

        connections.push(VerticalConnection {
            from: base.id,
            to: upper_id,
            connector: connector_id,
            kind: pick_vertical_kind(base),
        });
    }

    connections
}

fn should_spawn_vertical_connection(base: &SpatialNode) -> bool {
    let hash = vertical_hash(base.coord.chunk_x, base.coord.chunk_z, base.id);
    hash.is_multiple_of(7)
}

fn pick_vertical_kind(base: &SpatialNode) -> VerticalConnectionKind {
    let hash = vertical_hash(base.coord.chunk_x, base.coord.chunk_z, base.id);

    match hash % 11 {
        0 => VerticalConnectionKind::Shaft,
        1 | 2 => VerticalConnectionKind::Ramp,
        3 => VerticalConnectionKind::Atrium,
        _ => VerticalConnectionKind::Stair,
    }
}

fn connector_bounds(kind: VerticalConnectionKind) -> ([u8; 3], [u8; 3]) {
    match kind {
        VerticalConnectionKind::Stair => ([6, 0, 6], [10, 4, 10]),
        VerticalConnectionKind::Ramp => ([4, 0, 5], [12, 3, 11]),
        VerticalConnectionKind::Shaft => ([7, 0, 7], [9, 4, 9]),
        VerticalConnectionKind::Atrium => ([3, 0, 3], [13, 4, 13]),
    }
}

fn vertical_hash(chunk_x: i32, chunk_z: i32, node_id: SpatialNodeId) -> u32 {
    let mut h = node_id;
    h ^= (chunk_x as u32).wrapping_mul(0x9E37_79B9);
    h ^= (chunk_z as u32).wrapping_mul(0x85EB_CA6B);
    h
}
impl VerticalConnection {
    pub fn edge_pairs(&self) -> [(SpatialNodeId, SpatialNodeId); 2] {
        [(self.from, self.connector), (self.connector, self.to)]
    }
}
