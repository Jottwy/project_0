use serde::{Deserialize, Serialize};

use super::{coords::LevelId, level_graph::LevelGraph};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldGraph {
    pub world_seed: u64,
    pub levels: Vec<LevelGraph>,
}

impl WorldGraph {
    pub fn new(world_seed: u64) -> Self {
        Self {
            world_seed,
            levels: Vec::new(),
        }
    }

    pub fn add_level(&mut self, level: LevelGraph) {
        self.levels.push(level);
    }

    pub fn find_level(&self, level_id: LevelId) -> Option<&LevelGraph> {
        self.levels.iter().find(|l| l.level_id == level_id)
    }
}
