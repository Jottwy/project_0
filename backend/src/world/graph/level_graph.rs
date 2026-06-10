use serde::{Deserialize, Serialize};

use super::{coords::LevelId, region_graph::RegionGraph};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LevelGraph {
    pub level_id: LevelId,
    pub regions: Vec<RegionGraph>,
}

impl LevelGraph {
    pub fn new(level_id: LevelId) -> Self {
        Self {
            level_id,
            regions: Vec::new(),
        }
    }

    pub fn add_region(&mut self, region: RegionGraph) {
        self.regions.push(region);
    }

    pub fn region_count(&self) -> usize {
        self.regions.len()
    }
}
