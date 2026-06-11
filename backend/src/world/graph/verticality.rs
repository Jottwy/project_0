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

#[allow(dead_code)]
pub fn inject_basic_verticality(
    nodes: &mut Vec<SpatialNode>,
    next_id: &mut SpatialNodeId,
) -> Vec<VerticalConnection> {
    let mut connections = Vec::new();

    let base_nodes: Vec<SpatialNode> = nodes
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
        .cloned()
        .collect();

    for base in base_nodes {
        if !should_spawn_vertical_connection(&base) {
            continue;
        }

        let kind = pick_vertical_kind(&base);
        let upper_coord: Chunk3DCoord = base.coord.above();

        let upper_id = *next_id;
        *next_id += 1;

        let connector_id = *next_id;
        *next_id += 1;

        let upper_node = SpatialNode::new(
            upper_id,
            SpatialNodeKind::SealedUpperSpace,
            upper_coord,
            base.local_min,
            base.local_max,
            false,
            true,
        );

        let (local_min, local_max) = connector_bounds(kind);

        let connector_node: SpatialNode = SpatialNode::new(
            connector_id,
            kind.as_node_kind(),
            base.coord,
            local_min,
            local_max,
            false,
            true,
        );

        nodes.push(upper_node);
        nodes.push(connector_node);

        connections.push(VerticalConnection {
            from: base.id,
            to: upper_id,
            connector: connector_id,
            kind,
        });
    }

    connections
}

fn should_spawn_vertical_connection(base: &SpatialNode) -> bool {
    let hash = vertical_hash(base.coord.chunk_x, base.coord.chunk_z, base.id);
    hash % 7 == 0
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
