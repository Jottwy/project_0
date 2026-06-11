use serde::{Deserialize, Serialize};

use super::{
    coords::RegionCoord,
    edges::ConnectionEdge,
    nodes::{SpatialNode, SpatialNodeId},
    verticality::VerticalConnection,
};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RegionGraph {
    pub coord: RegionCoord,

    pub nodes: Vec<SpatialNode>,
    pub edges: Vec<ConnectionEdge>,

    /// Parallel verticality layer (Phase 6.5). Never feeds legacy traversal:
    /// `nodes`/`edges` and proven_connections are unaffected by this field.
    pub vertical_connections: Vec<VerticalConnection>,
}

impl RegionGraph {
    pub fn new(coord: RegionCoord) -> Self {
        Self {
            coord,
            nodes: Vec::new(),
            edges: Vec::new(),
            vertical_connections: Vec::new(),
        }
    }

    pub fn validate_references(&self) -> bool {
        self.edges
            .iter()
            .all(|edge| self.find_node(edge.from).is_some() && self.find_node(edge.to).is_some())
    }

    pub fn accessible_node_count(&self) -> usize {
        self.nodes.iter().filter(|n| n.accessible).count()
    }

    pub fn add_node(&mut self, node: SpatialNode) {
        self.nodes.push(node);
    }

    pub fn add_edge(&mut self, edge: ConnectionEdge) {
        self.edges.push(edge);
    }

    pub fn node_count(&self) -> usize {
        self.nodes.len()
    }

    pub fn edge_count(&self) -> usize {
        self.edges.len()
    }

    pub fn add_vertical_connection(&mut self, connection: VerticalConnection) {
        self.vertical_connections.push(connection);
    }

    pub fn vertical_connection_count(&self) -> usize {
        self.vertical_connections.len()
    }

    pub fn find_node(&self, id: SpatialNodeId) -> Option<&SpatialNode> {
        self.nodes.iter().find(|n| n.id == id)
    }

    pub fn has_vertical_content(&self) -> bool {
        self.nodes.iter().any(|n| n.is_vertical()) || self.edges.iter().any(|e| e.is_vertical())
    }
}
