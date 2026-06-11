use serde::{Deserialize, Serialize};

use super::{
    coords::{LevelId, LEVEL_0},
    level_graph::LevelGraph,
    region_graph::RegionGraph,
};

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

    pub fn from_level0_region_graph(world_seed: u64, rg: RegionGraph) -> Self {
        let mut level = LevelGraph::new(LEVEL_0);
        level.add_region(rg);

        Self {
            world_seed,
            levels: vec![level],
        }
    }

    pub fn add_level(&mut self, level: LevelGraph) {
        self.levels.push(level);
    }

    pub fn find_level(&self, level_id: LevelId) -> Option<&LevelGraph> {
        self.levels.iter().find(|l| l.level_id == level_id)
    }

    pub fn level0(&self) -> Option<&LevelGraph> {
        self.find_level(LEVEL_0)
    }

    pub fn level0_region_graph(&self) -> Option<&RegionGraph> {
        self.level0()?.primary_region()
    }
}
