use crate::world::graph::region_graph::RegionGraph;

pub fn validate_level0_region_graph(graph: &RegionGraph) -> bool {
    graph.node_count() > 0 && graph.validate_references()
}
