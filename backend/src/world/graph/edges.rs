use serde::{Deserialize, Serialize};

use super::nodes::SpatialNodeId;

pub type ConnectionEdgeId = u32;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum ConnectionKind {
    Doorway,
    Corridor,
    Stair,
    Ramp,
    Shaft,
    AtriumOpening,
    VisualOnlyGap,
    BlockedPortal,
    AnomalyTransition,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ConnectionEdge {
    pub id: ConnectionEdgeId,
    pub from: SpatialNodeId,
    pub to: SpatialNodeId,
    pub kind: ConnectionKind,

    /// Player can physically traverse this connection now.
    pub traversable: bool,

    /// Connection exists visually/audio-wise even if not traversable.
    pub perceptible: bool,

    /// Higher cost = less preferred by path validation.
    pub traversal_cost: u16,
}

impl ConnectionEdge {
    pub fn new(
        id: ConnectionEdgeId,
        from: SpatialNodeId,
        to: SpatialNodeId,
        kind: ConnectionKind,
        traversable: bool,
        perceptible: bool,
        traversal_cost: u16,
    ) -> Self {
        Self {
            id,
            from,
            to,
            kind,
            traversable,
            perceptible,
            traversal_cost,
        }
    }

    pub fn is_vertical(&self) -> bool {
        matches!(
            self.kind,
            ConnectionKind::Stair
                | ConnectionKind::Ramp
                | ConnectionKind::Shaft
                | ConnectionKind::AtriumOpening
        )
    }

    pub fn is_portal_like(&self) -> bool {
        matches!(
            self.kind,
            ConnectionKind::BlockedPortal | ConnectionKind::AnomalyTransition
        )
    }

    pub fn is_safe_for_spawn_path(&self) -> bool {
        self.traversable
            && !matches!(
                self.kind,
                ConnectionKind::Shaft
                    | ConnectionKind::AtriumOpening
                    | ConnectionKind::AnomalyTransition
            )
    }
}
