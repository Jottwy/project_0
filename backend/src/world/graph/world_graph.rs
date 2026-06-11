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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::levels::level_0::region_graph_builder::build_level0_region_graph;

    /// `from_level0_region_graph` must wrap a non-empty Level 0 RegionGraph and
    /// preserve its node/edge counts (≥1 node and ≥1 edge for the default seed).
    #[test]
    fn from_level0_region_graph_wraps_nonempty_level0() {
        let rg = build_level0_region_graph(0);
        let (nodes, edges) = (rg.node_count(), rg.edge_count());
        assert!(
            nodes > 0,
            "Level 0 region graph should have at least one node"
        );
        assert!(
            edges > 0,
            "Level 0 region graph should have at least one edge"
        );

        let wg = WorldGraph::from_level0_region_graph(0, rg);
        let level0 = wg
            .level0_region_graph()
            .expect("WorldGraph must expose a Level 0 region graph");
        assert_eq!(level0.node_count(), nodes, "wrapped node count must match");
        assert_eq!(level0.edge_count(), edges, "wrapped edge count must match");
    }
}
