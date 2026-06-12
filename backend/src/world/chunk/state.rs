use serde::{Deserialize, Serialize};

use crate::network::PeerId;

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
