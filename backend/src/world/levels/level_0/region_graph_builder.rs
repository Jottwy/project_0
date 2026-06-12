//! Level 0 RegionGraph construction.
//!
//! Contract (MIG-2): the `RegionGraph` is *derived* from already-generated chunk
//! data — it must be built immediately after `Level0Builder::build()` (i.e. from
//! the `Vec<(StructureV0, Chunk)>` that generation produced), never the other way
//! around. It is a read-only spatial/navigation view: it does **not** participate
//! in chunk generation, collision resolution, or safe-spawn. Changing this module
//! must not alter generation output, collision, or spawn semantics.

use std::collections::{HashMap, HashSet};

use crate::world::architecture::surface_builder::edge_delta;
use crate::world::chunk::{Chunk, EDGE_EAST, EDGE_NORTH, EDGE_SOUTH, EDGE_WEST, LAYOUT_GRID_SIZE};
use crate::world::generator::generate_initial_structure_chunks;

use crate::world::graph::{
    coords::{Chunk3DCoord, RegionCoord},
    edges::{ConnectionEdge, ConnectionKind},
    nodes::{SpatialNode, SpatialNodeId, SpatialNodeKind},
    region_graph::RegionGraph,
};

use crate::world::graph::verticality::{
    build_basic_vertical_connections, materialize_virtual_vertical_nodes,
};
use crate::world::levels::level_0::structure::{StructureType, StructureV0};

/// Thin wrapper: generates Level 0 chunk data then delegates to
/// [`build_level0_region_graph_from_generated`].  Exists for compatibility
/// with callers that only have a world seed.
pub fn build_level0_region_graph(world_seed: u64) -> RegionGraph {
    let generated = generate_initial_structure_chunks(world_seed);
    build_level0_region_graph_from_generated(world_seed, &generated)
}

/// Phase 3.1D-B — proven edge promotion.
///
/// Builds a `RegionGraph` from already-generated chunk data without invoking
/// the Level0Builder again.  Edges are inferred from chunk-boundary adjacency
/// and promoted by cross-referencing against proven `edge_openings` data:
///
/// - Pairs present in the proven set → `ConnectionKind::Doorway`, `traversable = true`.
/// - All other adjacency-inferred pairs → `ConnectionKind::VisualOnlyGap`, `traversable = false`.
///
/// Only horizontal same-layer proven connections are promoted. Vertical inferred
/// adjacency remains non-traversable until inter-layer topology is proven separately.
pub(crate) fn build_level0_region_graph_from_generated(
    world_seed: u64,
    generated: &[(StructureV0, Chunk)],
) -> RegionGraph {
    // Proven connections from the already-finalized chunk data (no extra generation).
    let proven = level0_proven_structure_connections_from_generated(generated);

    // Collect unique structures, sorted by id for deterministic node ordering.
    // generated may repeat the same StructureV0 once per chunk it contains.
    let mut seen_ids: HashSet<u32> = HashSet::new();
    let mut structures: Vec<&StructureV0> = Vec::new();
    for (s, _) in generated {
        if seen_ids.insert(s.id) {
            structures.push(s);
        }
    }
    structures.sort_by_key(|s| s.id);

    // chunk key → owning structure id (first-writer-wins; per-structure chunk
    // positions are non-overlapping by construction).
    let mut chunk_owner: HashMap<(i32, i8, i32), u32> = HashMap::with_capacity(generated.len());
    for (s, chunk) in generated {
        chunk_owner.entry(chunk.key()).or_insert(s.id);
    }

    // id_to_type used for debug_asserts on edge endpoints.
    let id_to_type: HashMap<u32, StructureType> = structures
        .iter()
        .map(|s| (s.id, s.structure_type))
        .collect();

    let mut graph = RegionGraph::new(RegionCoord::level0(0, 0));

    for s in &structures {
        let kind = structure_type_to_node_kind(s.structure_type);
        let coord = Chunk3DCoord::from_level0_chunk(s.origin.0, s.origin_layer, s.origin.1);
        let accessible = is_structure_accessible(s.structure_type);
        graph.add_node(SpatialNode::new(
            s.id,
            kind,
            coord,
            [0, 0, 0],
            [LAYOUT_GRID_SIZE, 1, LAYOUT_GRID_SIZE],
            accessible,
            true,
        ));
    }
    let mut next_node_id: SpatialNodeId = graph.nodes.iter().map(|n| n.id).max().unwrap_or(0) + 1;

    // Phase 6.5 — parallel verticality layer. Does not touch graph.nodes,
    // graph.edges or proven connection promotion below.
    for connection in build_basic_vertical_connections(&graph.nodes, &mut next_node_id) {
        graph.add_vertical_connection(connection);
    }
    graph.virtual_vertical_nodes =
        materialize_virtual_vertical_nodes(&graph.nodes, &graph.vertical_connections);

    // Collect adjacent structure pairs via chunk-boundary touch (horizontal + vertical).
    const H_DELTAS: [(i32, i32); 4] = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    const V_DELTAS: [i8; 2] = [-1, 1];
    let mut adjacencies: Vec<(u32, u32)> = Vec::new();

    for (s, chunk) in generated {
        let key = chunk.key();
        // Each chunk key belongs to exactly one structure; skip if not this one.
        if chunk_owner.get(&key) != Some(&s.id) {
            continue;
        }
        let (cx, cl, cz) = key;
        for (dx, dz) in H_DELTAS {
            if let Some(&oid) = chunk_owner.get(&(cx + dx, cl, cz + dz)) {
                if oid != s.id {
                    adjacencies.push((s.id.min(oid), s.id.max(oid)));
                }
            }
        }
        for dl in V_DELTAS {
            if let Some(nl) = cl.checked_add(dl) {
                if let Some(&oid) = chunk_owner.get(&(cx, nl, cz)) {
                    if oid != s.id {
                        adjacencies.push((s.id.min(oid), s.id.max(oid)));
                    }
                }
            }
        }
    }

    // Deterministic order, no hashing overhead.
    adjacencies.sort_unstable();
    adjacencies.dedup();

    for (from_id, to_id) in adjacencies {
        debug_assert!(id_to_type.contains_key(&from_id));
        debug_assert!(id_to_type.contains_key(&to_id));

        // pair is already canonical (from_id < to_id) — stored as min/max above.
        // Promote to Doorway only if backed by a finalized chunk edge_opening.
        let pair = (from_id, to_id);
        let (kind, traversable) = if proven.binary_search(&pair).is_ok() {
            (ConnectionKind::Doorway, true)
        } else {
            (ConnectionKind::VisualOnlyGap, false)
        };

        let eid = stable_edge_id(world_seed, from_id, to_id);
        graph.add_edge(ConnectionEdge::new(
            eid,
            from_id,
            to_id,
            kind,
            traversable,
            true, // perceptible
            1,
        ));
    }

    graph
}

// ─── Audit ───

/// Snapshot counts describing a built Level 0 RegionGraph.
/// Backend-only; never serialized or sent over IPC.
#[derive(Debug, Clone, PartialEq)]
pub struct Level0RegionGraphAudit {
    pub node_count: usize,
    pub edge_count: usize,
    pub accessible_node_count: usize,
    pub perceptible_edge_count: usize,
    pub traversable_edge_count: usize,
    pub visual_only_edge_count: usize,
    pub dangling_edge_count: usize,
    pub manila_room_count: usize,
    pub danger_pocket_count: usize,
    pub blocked_portal_count: usize,
    pub sealed_upper_count: usize,
    pub underfloor_count: usize,
}

pub fn audit_level0_region_graph(graph: &RegionGraph) -> Level0RegionGraphAudit {
    let node_ids: HashSet<u32> = graph.nodes.iter().map(|n| n.id).collect();

    Level0RegionGraphAudit {
        node_count: graph.node_count(),
        edge_count: graph.edge_count(),
        accessible_node_count: graph.accessible_node_count(),
        perceptible_edge_count: graph.edges.iter().filter(|e| e.perceptible).count(),
        traversable_edge_count: graph.edges.iter().filter(|e| e.traversable).count(),
        visual_only_edge_count: graph
            .edges
            .iter()
            .filter(|e| matches!(e.kind, ConnectionKind::VisualOnlyGap))
            .count(),
        dangling_edge_count: graph
            .edges
            .iter()
            .filter(|e| !node_ids.contains(&e.from) || !node_ids.contains(&e.to))
            .count(),
        manila_room_count: graph
            .nodes
            .iter()
            .filter(|n| matches!(n.kind, SpatialNodeKind::ManilaRoom))
            .count(),
        danger_pocket_count: graph
            .nodes
            .iter()
            .filter(|n| matches!(n.kind, SpatialNodeKind::DangerPocket))
            .count(),
        blocked_portal_count: graph
            .nodes
            .iter()
            .filter(|n| matches!(n.kind, SpatialNodeKind::BlockedPortal))
            .count(),
        sealed_upper_count: graph
            .nodes
            .iter()
            .filter(|n| matches!(n.kind, SpatialNodeKind::SealedUpperSpace))
            .count(),
        underfloor_count: graph
            .nodes
            .iter()
            .filter(|n| matches!(n.kind, SpatialNodeKind::UnderfloorService))
            .count(),
    }
}

// ─── Private helpers ───

fn structure_type_to_node_kind(t: StructureType) -> SpatialNodeKind {
    match t {
        StructureType::StarterCluster => SpatialNodeKind::Room,
        StructureType::HallwayChain => SpatialNodeKind::Corridor,
        StructureType::Intersection => SpatialNodeKind::Intersection,
        StructureType::StorageRoom => SpatialNodeKind::Room,
        // SafeRoom shares ManilaRoom semantics (safe pocket) in graph terms.
        StructureType::SafeRoom => SpatialNodeKind::ManilaRoom,
        StructureType::DeadEnd => SpatialNodeKind::Room,
        StructureType::DangerRoom => SpatialNodeKind::DangerPocket,
        StructureType::HallwayT => SpatialNodeKind::Intersection,
        StructureType::PillarRoom => SpatialNodeKind::Room,
        StructureType::OpenHall => SpatialNodeKind::Room,
        StructureType::PillarHall => SpatialNodeKind::Room,
        StructureType::HumidZone => SpatialNodeKind::Room,
        StructureType::ArchRoom => SpatialNodeKind::Room,
        StructureType::BlackoutZone => SpatialNodeKind::DangerPocket,
        StructureType::RedRoom => SpatialNodeKind::DangerPocket,
        StructureType::ManilaRoom => SpatialNodeKind::ManilaRoom,
        StructureType::CleaningArea => SpatialNodeKind::Room,
        StructureType::PitRoom => SpatialNodeKind::BlockedPortal,
        StructureType::StackedCorridor => SpatialNodeKind::Stair,
        StructureType::LowerServiceBranch => SpatialNodeKind::UnderfloorService,
        StructureType::UpperOfficeBranch => SpatialNodeKind::SealedUpperSpace,
        StructureType::AtriumVoidRoom => SpatialNodeKind::Atrium,
        StructureType::DeepPrecipicePlaceholder => SpatialNodeKind::BlockedPortal,
        StructureType::GiantPillarHall => SpatialNodeKind::Atrium,
        StructureType::PoiLandmark => SpatialNodeKind::Atrium,
        StructureType::PoiAnomalyCluster => SpatialNodeKind::Corridor,
        StructureType::PoiDangerPocket => SpatialNodeKind::DangerPocket,
        StructureType::PoiSafePocket => SpatialNodeKind::ManilaRoom,
    }
}

/// Node accessibility. PitRoom and DeepPrecipicePlaceholder are hard-blocked.
/// UpperOfficeBranch is sealed upper space: perceptible, not accessible.
/// LowerServiceBranch has no proven traversable topology in production data,
/// so it is perceptible but not accessible until proven otherwise.
fn is_structure_accessible(t: StructureType) -> bool {
    !matches!(
        t,
        StructureType::PitRoom
            | StructureType::DeepPrecipicePlaceholder
            | StructureType::UpperOfficeBranch
            | StructureType::LowerServiceBranch
    )
}

// ─── Spatial queries ───

/// Returns all nodes directly reachable from `node_id` via a single traversable
/// edge (undirected). Result is sorted. Returns empty Vec if the node does not
/// exist in the graph.
pub(crate) fn traversable_neighbors(
    graph: &RegionGraph,
    node_id: SpatialNodeId,
) -> Vec<SpatialNodeId> {
    if graph.find_node(node_id).is_none() {
        return Vec::new();
    }
    let mut neighbors: Vec<SpatialNodeId> = graph
        .edges
        .iter()
        .filter(|e| e.traversable)
        .filter_map(|e| {
            if e.from == node_id {
                Some(e.to)
            } else if e.to == node_id {
                Some(e.from)
            } else {
                None
            }
        })
        .collect();
    neighbors.sort_unstable();
    neighbors.dedup();
    neighbors
}

/// Returns all node IDs reachable from `start_id` via traversable edges
/// (undirected BFS). Always includes the start node itself. Result is sorted.
/// Returns empty Vec if the start node does not exist in the graph.
pub(crate) fn reachable_from(graph: &RegionGraph, start_id: SpatialNodeId) -> Vec<SpatialNodeId> {
    if graph.find_node(start_id).is_none() {
        return Vec::new();
    }
    let mut visited: HashSet<SpatialNodeId> = HashSet::new();
    let mut queue: Vec<SpatialNodeId> = vec![start_id];
    visited.insert(start_id);

    while let Some(current) = queue.pop() {
        for neighbor in traversable_neighbors(graph, current) {
            if visited.insert(neighbor) {
                queue.push(neighbor);
            }
        }
    }

    let mut result: Vec<SpatialNodeId> = visited.into_iter().collect();
    result.sort_unstable();
    result
}

/// Returns true if `from` and `to` are in the same traversable-edge connected
/// component. Returns false if either node ID does not exist in the graph.
pub(crate) fn is_connected(graph: &RegionGraph, from: SpatialNodeId, to: SpatialNodeId) -> bool {
    if graph.find_node(from).is_none() || graph.find_node(to).is_none() {
        return false;
    }
    reachable_from(graph, from).binary_search(&to).is_ok()
}

/// Returns the `SpatialNodeId` of the Level 0 spawn / starter node.
///
/// Resolved from graph data: looks for an accessible `Room` or `ManilaRoom`
/// node whose `coord` maps to the world-origin chunk
/// (`chunk_x=0, chunk_y=0, chunk_z=0`).
///
/// If multiple candidates match (should not occur in practice), the lowest id
/// wins for determinism — generator.rs sorts structures by id and assigns
/// StarterCluster id=0, so the tie-break never fires today.
pub(crate) fn starter_node_id(graph: &RegionGraph) -> Option<SpatialNodeId> {
    let mut candidates: Vec<SpatialNodeId> = graph
        .nodes
        .iter()
        .filter(|n| {
            n.accessible
                && n.coord.chunk_x == 0
                && n.coord.chunk_y == 0
                && n.coord.chunk_z == 0
                && matches!(n.kind, SpatialNodeKind::Room | SpatialNodeKind::ManilaRoom)
        })
        .map(|n| n.id)
        .collect();
    // Deterministic tie-break: lowest id first.
    candidates.sort_unstable();
    candidates.into_iter().next()
}

// ─── Level 0 node selection helpers ───

/// Returns all node IDs in the graph, sorted ascending.
pub(crate) fn level0_node_ids(graph: &RegionGraph) -> Vec<SpatialNodeId> {
    let mut ids: Vec<SpatialNodeId> = graph.nodes.iter().map(|n| n.id).collect();
    ids.sort_unstable();
    ids
}

/// Returns node IDs where `node.accessible == true`, sorted ascending.
pub(crate) fn level0_accessible_node_ids(graph: &RegionGraph) -> Vec<SpatialNodeId> {
    let mut ids: Vec<SpatialNodeId> = graph
        .nodes
        .iter()
        .filter(|n| n.accessible)
        .map(|n| n.id)
        .collect();
    ids.sort_unstable();
    ids
}

/// Returns all node IDs reachable from the Level 0 starter node via traversable
/// edges, sorted ascending. Returns empty Vec if the starter node is missing.
pub(crate) fn level0_reachable_node_ids_from_starter(graph: &RegionGraph) -> Vec<SpatialNodeId> {
    match starter_node_id(graph) {
        Some(starter) => reachable_from(graph, starter),
        None => Vec::new(),
    }
}

/// Returns node IDs whose `kind` matches `kind` exactly, sorted ascending.
pub(crate) fn level0_node_ids_by_kind(
    graph: &RegionGraph,
    kind: SpatialNodeKind,
) -> Vec<SpatialNodeId> {
    let mut ids: Vec<SpatialNodeId> = graph
        .nodes
        .iter()
        .filter(|n| n.kind == kind)
        .map(|n| n.id)
        .collect();
    ids.sort_unstable();
    ids
}

/// Returns node IDs for recognized safe structure types (`ManilaRoom`),
/// sorted ascending.
pub(crate) fn level0_safe_node_ids(graph: &RegionGraph) -> Vec<SpatialNodeId> {
    let mut ids: Vec<SpatialNodeId> = graph
        .nodes
        .iter()
        .filter(|n| matches!(n.kind, SpatialNodeKind::ManilaRoom))
        .map(|n| n.id)
        .collect();
    ids.sort_unstable();
    ids
}

/// Returns node IDs for recognized danger structure types (`DangerPocket`),
/// sorted ascending.
pub(crate) fn level0_danger_node_ids(graph: &RegionGraph) -> Vec<SpatialNodeId> {
    let mut ids: Vec<SpatialNodeId> = graph
        .nodes
        .iter()
        .filter(|n| matches!(n.kind, SpatialNodeKind::DangerPocket))
        .map(|n| n.id)
        .collect();
    ids.sort_unstable();
    ids
}

fn stable_edge_id(world_seed: u64, from: u32, to: u32) -> u32 {
    let a = from.min(to) as u64;
    let b = from.max(to) as u64;
    let mut h = world_seed ^ 0xE4C3_B2A1_FEED_0001_u64;
    h = h.wrapping_add(a.wrapping_mul(0xFF51_AFD7_ED55_8CCD));
    h ^= b.wrapping_mul(0xC4CE_B9FE_1A85_EC53).rotate_left(32);
    h ^= h >> 33;
    h ^= h >> 17;
    ((h & 0x7FFF_FFFF) as u32).max(1)
}

// ─── MIG-5d: graph topology queries (moved from generator.rs) ───

/// Core logic: derives proven structure connections from already-generated
/// chunk data without calling any generator function.
///
/// For each chunk, any `edge_openings` bit pointing to a same-layer neighbor
/// with a different `macro_id` becomes a proven inter-structure connection.
/// Returns `(min_id, max_id)` pairs, sorted and deduplicated.
pub(crate) fn level0_proven_structure_connections_from_generated(
    generated: &[(StructureV0, Chunk)],
) -> Vec<(u32, u32)> {
    let mut pos_to_macro: HashMap<(i32, i8, i32), u32> = HashMap::with_capacity(generated.len());
    for (_, chunk) in generated {
        pos_to_macro.insert(chunk.key(), chunk.layout.macro_id);
    }

    const EDGES: [u8; 4] = [EDGE_NORTH, EDGE_EAST, EDGE_SOUTH, EDGE_WEST];

    let mut connections: Vec<(u32, u32)> = Vec::new();
    for (_, chunk) in generated {
        let sa = chunk.layout.macro_id;
        let (cx, cl, cz) = chunk.key();
        for &edge in &EDGES {
            if chunk.layout.edge_openings & edge == 0 {
                continue;
            }
            let (dx, dz) = edge_delta(edge);
            let nbr = (cx + dx, cl, cz + dz);
            if let Some(&sb) = pos_to_macro.get(&nbr) {
                if sb != sa {
                    connections.push((sa.min(sb), sa.max(sb)));
                }
            }
        }
    }

    connections.sort_unstable();
    connections.dedup();
    connections
}

/// Returns structure-pair connections proven by finalized chunk-boundary
/// edge openings.  Only horizontal same-layer connections are included;
/// vertical/inter-layer connections are out of scope for this query.
///
/// For each chunk in the finalized Level 0 world, any `edge_openings` bit
/// pointing to a present same-layer neighbor with a different `macro_id`
/// becomes a proven inter-structure connection.  Pairs are returned as
/// `(min_id, max_id)`, sorted and deduplicated.
///
/// Wrapper: generates chunk data then delegates to
/// [`level0_proven_structure_connections_from_generated`].
pub(crate) fn level0_proven_structure_connections(world_seed: u64) -> Vec<(u32, u32)> {
    let generated = generate_initial_structure_chunks(world_seed);
    level0_proven_structure_connections_from_generated(&generated)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::generator::{
        generate_initial_structure_chunks, level0_proven_structure_connections,
        level0_proven_structure_connections_from_generated,
    };
    use crate::world::levels::level_0::validation::validate_level0_region_graph;

    // ─── Core invariants ───

    #[test]
    fn node_count_nonzero() {
        let graph = build_level0_region_graph(0);
        assert!(graph.node_count() > 0, "expected nodes, got 0");
    }

    #[test]
    fn validate_passes() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            assert!(
                validate_level0_region_graph(&graph),
                "seed {seed} failed validation"
            );
        }
    }

    #[test]
    fn accessible_nodes_exist() {
        let graph = build_level0_region_graph(0);
        assert!(
            graph.accessible_node_count() > 0,
            "expected accessible nodes"
        );
    }

    #[test]
    fn deterministic_node_and_edge_counts() {
        let g1 = build_level0_region_graph(12345);
        let g2 = build_level0_region_graph(12345);
        assert_eq!(g1.node_count(), g2.node_count());
        assert_eq!(g1.edge_count(), g2.edge_count());
    }

    #[test]
    fn all_edges_reference_existing_nodes() {
        let graph = build_level0_region_graph(0);
        assert!(graph.validate_references());
    }

    #[test]
    fn edge_count_deterministic() {
        let g1 = build_level0_region_graph(42);
        let g2 = build_level0_region_graph(42);
        assert_eq!(g1.edge_count(), g2.edge_count());
    }

    #[test]
    fn multiple_seeds_produce_valid_graphs() {
        for seed in [0u64, 1, 42, 12345, 99999] {
            let graph = build_level0_region_graph(seed);
            assert!(
                validate_level0_region_graph(&graph),
                "seed {seed} failed validation"
            );
        }
    }

    /// Node and edge ordering must be deterministic across builds with the same seed.
    #[test]
    fn deterministic_node_and_edge_ordering() {
        let g1 = build_level0_region_graph(777);
        let g2 = build_level0_region_graph(777);

        let n1: Vec<u32> = g1.nodes.iter().map(|n| n.id).collect();
        let n2: Vec<u32> = g2.nodes.iter().map(|n| n.id).collect();
        assert_eq!(n1, n2, "node ordering not deterministic");

        let e1: Vec<(u32, u32, u32)> = g1.edges.iter().map(|e| (e.id, e.from, e.to)).collect();
        let e2: Vec<(u32, u32, u32)> = g2.edges.iter().map(|e| (e.id, e.from, e.to)).collect();
        assert_eq!(e1, e2, "edge ordering not deterministic");
    }

    /// Every edge endpoint must reference an existing node.
    #[test]
    fn edges_reference_existing_nodes_explicitly() {
        let graph = build_level0_region_graph(99999);
        let node_ids: HashSet<u32> = graph.nodes.iter().map(|n| n.id).collect();
        for edge in graph.edges.iter() {
            assert!(
                node_ids.contains(&edge.from),
                "edge {} dangling from",
                edge.id
            );
            assert!(node_ids.contains(&edge.to), "edge {} dangling to", edge.id);
        }
    }

    /// Structural/perceptible-only node kinds must not be accessible.
    #[test]
    fn structural_nodes_not_accessible_unless_intended() {
        let graph = build_level0_region_graph(0);
        for node in graph.nodes.iter() {
            match node.kind {
                SpatialNodeKind::BlockedPortal
                | SpatialNodeKind::SealedUpperSpace
                | SpatialNodeKind::UnderfloorService => {
                    assert!(
                        !node.accessible,
                        "node {} ({:?}) must not be accessible",
                        node.id, node.kind
                    );
                }
                _ => {}
            }
        }
    }

    // ─── 3.1D-B: proven edge promotion ───

    /// Proven connections must be promoted to traversable Doorway edges.
    #[test]
    fn audit_traversable_count_nonzero() {
        for seed in [0u64, 42, 7778] {
            let proven = level0_proven_structure_connections(seed);
            if proven.is_empty() {
                continue; // no proven connections for this seed — skip
            }
            let graph = build_level0_region_graph(seed);
            let audit = audit_level0_region_graph(&graph);
            assert!(
                audit.traversable_edge_count > 0,
                "seed {seed}: proven connections exist ({}) but traversable_edge_count is 0",
                proven.len()
            );
        }
    }

    /// Every traversable edge must use ConnectionKind::Doorway.
    #[test]
    fn traversable_edges_are_doorway_kind() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            for edge in graph.edges.iter().filter(|e| e.traversable) {
                assert!(
                    matches!(edge.kind, ConnectionKind::Doorway),
                    "seed {seed}: traversable edge {} has kind {:?}, expected Doorway",
                    edge.id,
                    edge.kind
                );
            }
        }
    }

    /// Every Doorway edge must be traversable.
    #[test]
    fn doorway_edges_are_traversable() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            for edge in graph
                .edges
                .iter()
                .filter(|e| matches!(e.kind, ConnectionKind::Doorway))
            {
                assert!(
                    edge.traversable,
                    "seed {seed}: Doorway edge {} is not traversable",
                    edge.id
                );
            }
        }
    }

    /// Every traversable edge's structure pair must appear in the proven connections.
    #[test]
    fn traversable_edge_pairs_exist_in_proven_connections() {
        for seed in [0u64, 42, 7778] {
            let proven = level0_proven_structure_connections(seed);
            let graph = build_level0_region_graph(seed);
            for edge in graph.edges.iter().filter(|e| e.traversable) {
                let pair = (edge.from.min(edge.to), edge.from.max(edge.to));
                assert!(
                    proven.binary_search(&pair).is_ok(),
                    "seed {seed}: traversable edge {} ({},{}) not in proven connections",
                    edge.id,
                    edge.from,
                    edge.to
                );
            }
        }
    }

    /// Unproven adjacency edges must remain VisualOnlyGap and non-traversable.
    #[test]
    fn unproven_edges_remain_visual_only_and_non_traversable() {
        for seed in [0u64, 42, 7778] {
            let proven = level0_proven_structure_connections(seed);
            let graph = build_level0_region_graph(seed);
            for edge in graph.edges.iter() {
                let pair = (edge.from.min(edge.to), edge.from.max(edge.to));
                if proven.binary_search(&pair).is_err() {
                    assert!(
                        matches!(edge.kind, ConnectionKind::VisualOnlyGap),
                        "seed {seed}: unproven edge {} has kind {:?}, expected VisualOnlyGap",
                        edge.id,
                        edge.kind
                    );
                    assert!(
                        !edge.traversable,
                        "seed {seed}: unproven edge {} must not be traversable",
                        edge.id
                    );
                }
            }
        }
    }

    /// Traversable edge count must equal the number of proven connections whose
    /// both node IDs are present in the graph.
    #[test]
    fn traversable_edge_count_matches_proven_in_graph() {
        for seed in [0u64, 42, 7778] {
            let proven = level0_proven_structure_connections(seed);
            let graph = build_level0_region_graph(seed);
            let node_ids: HashSet<u32> = graph.nodes.iter().map(|n| n.id).collect();
            let proven_in_graph = proven
                .iter()
                .filter(|(a, b)| node_ids.contains(a) && node_ids.contains(b))
                .count();
            let traversable = graph.edges.iter().filter(|e| e.traversable).count();
            assert_eq!(
                traversable, proven_in_graph,
                "seed {seed}: traversable_edge_count={traversable} != proven_in_graph={proven_in_graph}"
            );
        }
    }

    /// Proven and unproven edges must separate correctly: proven → Doorway,
    /// unproven → VisualOnlyGap. Replaces the old all-visual-only assertion.
    #[test]
    fn proven_vs_unproven_edge_separation() {
        for seed in [0u64, 42, 7778] {
            let proven = level0_proven_structure_connections(seed);
            let graph = build_level0_region_graph(seed);
            let (mut doorway_count, mut visual_count) = (0usize, 0usize);
            for edge in graph.edges.iter() {
                let pair = (edge.from.min(edge.to), edge.from.max(edge.to));
                if proven.binary_search(&pair).is_ok() {
                    doorway_count += 1;
                    assert!(
                        matches!(edge.kind, ConnectionKind::Doorway) && edge.traversable,
                        "seed {seed}: proven edge {} should be Doorway+traversable",
                        edge.id
                    );
                } else {
                    visual_count += 1;
                    assert!(
                        matches!(edge.kind, ConnectionKind::VisualOnlyGap) && !edge.traversable,
                        "seed {seed}: unproven edge {} should be VisualOnlyGap+non-traversable",
                        edge.id
                    );
                }
            }
            assert_eq!(
                doorway_count + visual_count,
                graph.edge_count(),
                "seed {seed}: edge kind accounting mismatch"
            );
        }
    }

    /// All edges must remain perceptible regardless of promotion status.
    #[test]
    fn all_edges_are_perceptible() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            for edge in graph.edges.iter() {
                assert!(
                    edge.perceptible,
                    "seed {seed}: edge {} must be perceptible",
                    edge.id
                );
            }
        }
    }

    // ─── Audit tests ───

    #[test]
    fn audit_node_count_matches_graph() {
        let graph = build_level0_region_graph(0);
        let audit = audit_level0_region_graph(&graph);
        assert_eq!(audit.node_count, graph.node_count());
    }

    #[test]
    fn audit_edge_count_matches_graph() {
        let graph = build_level0_region_graph(0);
        let audit = audit_level0_region_graph(&graph);
        assert_eq!(audit.edge_count, graph.edge_count());
    }

    #[test]
    fn audit_no_dangling_edges() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let audit = audit_level0_region_graph(&graph);
            assert_eq!(
                audit.dangling_edge_count, 0,
                "seed {seed}: found {} dangling edges",
                audit.dangling_edge_count
            );
        }
    }

    #[test]
    fn audit_traversable_and_visual_sum_to_total() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let audit = audit_level0_region_graph(&graph);
            assert_eq!(
                audit.traversable_edge_count + audit.visual_only_edge_count,
                audit.edge_count,
                "seed {seed}: traversable + visual_only should equal total edges"
            );
        }
    }

    #[test]
    fn audit_is_deterministic() {
        let a1 = audit_level0_region_graph(&build_level0_region_graph(12345));
        let a2 = audit_level0_region_graph(&build_level0_region_graph(12345));
        assert_eq!(a1, a2);
    }

    #[test]
    fn audit_accessible_count_nonzero() {
        let graph = build_level0_region_graph(0);
        let audit = audit_level0_region_graph(&graph);
        assert!(
            audit.accessible_node_count > 0,
            "expected accessible nodes in audit"
        );
    }

    // ─── 3.1E: spatial query helpers ───

    /// Every node returned by traversable_neighbors must have a real traversable
    /// edge connecting it to the query node.
    #[test]
    fn traversable_neighbors_only_traversable_edges() {
        let graph = build_level0_region_graph(0);
        for node in &graph.nodes {
            for nbr in traversable_neighbors(&graph, node.id) {
                let has_edge = graph.edges.iter().any(|e| {
                    e.traversable
                        && ((e.from == node.id && e.to == nbr)
                            || (e.to == node.id && e.from == nbr))
                });
                assert!(
                    has_edge,
                    "node {} lists {} as traversable neighbor but no traversable edge exists",
                    node.id, nbr
                );
            }
        }
    }

    /// A node that has only VisualOnlyGap edges to some neighbor must not have
    /// that neighbor appear in traversable_neighbors.
    #[test]
    fn traversable_neighbors_excludes_visual_only_neighbors() {
        let graph = build_level0_region_graph(0);
        for edge in graph.edges.iter().filter(|e| !e.traversable) {
            // Only enforce when there is no separate traversable edge between the pair.
            let has_separate_traversable = graph.edges.iter().any(|e2| {
                e2.traversable
                    && ((e2.from == edge.from && e2.to == edge.to)
                        || (e2.to == edge.from && e2.from == edge.to))
            });
            if !has_separate_traversable {
                let a_nbrs = traversable_neighbors(&graph, edge.from);
                let b_nbrs = traversable_neighbors(&graph, edge.to);
                assert!(
                    !a_nbrs.contains(&edge.to),
                    "non-traversable neighbor {} appears in traversable_neighbors of {}",
                    edge.to,
                    edge.from
                );
                assert!(
                    !b_nbrs.contains(&edge.from),
                    "non-traversable neighbor {} appears in traversable_neighbors of {}",
                    edge.from,
                    edge.to
                );
            }
        }
    }

    /// traversable_neighbors returns empty Vec for a node ID that does not exist.
    #[test]
    fn traversable_neighbors_missing_node_returns_empty() {
        let graph = build_level0_region_graph(0);
        assert!(traversable_neighbors(&graph, u32::MAX).is_empty());
    }

    /// reachable_from always includes the start node itself.
    #[test]
    fn reachable_from_includes_start_node() {
        let graph = build_level0_region_graph(0);
        for node in &graph.nodes {
            let reachable = reachable_from(&graph, node.id);
            assert!(
                reachable.contains(&node.id),
                "node {} not found in its own reachable set",
                node.id
            );
        }
    }

    /// A node with no traversable edges can only reach itself.
    #[test]
    fn reachable_from_isolated_node_returns_only_self() {
        let graph = build_level0_region_graph(0);
        for node in &graph.nodes {
            let has_traversable = graph
                .edges
                .iter()
                .any(|e| e.traversable && (e.from == node.id || e.to == node.id));
            if !has_traversable {
                let reachable = reachable_from(&graph, node.id);
                assert_eq!(
                    reachable,
                    vec![node.id],
                    "isolated node {} should only reach itself, got {:?}",
                    node.id,
                    reachable
                );
            }
        }
    }

    /// reachable_from returns empty Vec for a node ID that does not exist.
    #[test]
    fn reachable_from_missing_node_returns_empty() {
        let graph = build_level0_region_graph(0);
        assert!(reachable_from(&graph, u32::MAX).is_empty());
    }

    /// is_connected returns true for at least one proven traversable pair.
    #[test]
    fn is_connected_true_for_proven_traversable_pair() {
        let seed = 0u64;
        let proven = level0_proven_structure_connections(seed);
        assert!(
            !proven.is_empty(),
            "seed {seed}: need at least one proven connection for this test"
        );
        let graph = build_level0_region_graph(seed);
        let (a, b) = proven[0];
        assert!(
            is_connected(&graph, a, b),
            "seed {seed}: proven pair ({a},{b}) should be connected"
        );
    }

    /// is_connected returns false when either node does not exist.
    #[test]
    fn is_connected_false_for_missing_node() {
        let graph = build_level0_region_graph(0);
        let existing = graph.nodes[0].id;
        assert!(!is_connected(&graph, u32::MAX, existing));
        assert!(!is_connected(&graph, existing, u32::MAX));
        assert!(!is_connected(&graph, u32::MAX, u32::MAX));
    }

    /// is_connected is symmetric: if A can reach B, B can reach A.
    #[test]
    fn is_connected_is_symmetric() {
        let seed = 42u64;
        let graph = build_level0_region_graph(seed);
        let proven = level0_proven_structure_connections(seed);
        for (a, b) in proven.iter().take(5) {
            assert_eq!(
                is_connected(&graph, *a, *b),
                is_connected(&graph, *b, *a),
                "seed {seed}: is_connected({a},{b}) != is_connected({b},{a})"
            );
        }
    }

    /// All query outputs are deterministic for the same graph.
    #[test]
    fn query_output_is_deterministic() {
        let graph = build_level0_region_graph(42);
        let proven = level0_proven_structure_connections(42);

        // traversable_neighbors
        for node in graph.nodes.iter().take(5) {
            let r1 = traversable_neighbors(&graph, node.id);
            let r2 = traversable_neighbors(&graph, node.id);
            assert_eq!(
                r1, r2,
                "traversable_neighbors not deterministic for {}",
                node.id
            );
        }

        // reachable_from
        for node in graph.nodes.iter().take(3) {
            let r1 = reachable_from(&graph, node.id);
            let r2 = reachable_from(&graph, node.id);
            assert_eq!(r1, r2, "reachable_from not deterministic for {}", node.id);
        }

        // is_connected
        for (a, b) in proven.iter().take(3) {
            assert_eq!(
                is_connected(&graph, *a, *b),
                is_connected(&graph, *a, *b),
                "is_connected not deterministic for ({a},{b})"
            );
        }
    }

    // ─── 3.1G: starter node identity ───

    #[test]
    fn starter_node_id_returns_some_for_seed_0() {
        let graph = build_level0_region_graph(0);
        assert!(
            starter_node_id(&graph).is_some(),
            "expected a starter node for seed 0"
        );
    }

    #[test]
    fn starter_node_id_returns_some_for_multiple_seeds() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            assert!(
                starter_node_id(&graph).is_some(),
                "seed {seed}: expected a starter node"
            );
        }
    }

    #[test]
    fn starter_node_id_exists_in_graph_nodes() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let sid = starter_node_id(&graph).expect("starter node present");
            assert!(
                graph.find_node(sid).is_some(),
                "seed {seed}: starter id {sid} not found in graph nodes"
            );
        }
    }

    #[test]
    fn starter_node_id_node_is_accessible() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let sid = starter_node_id(&graph).expect("starter node present");
            let node = graph.find_node(sid).unwrap();
            assert!(
                node.accessible,
                "seed {seed}: starter node {sid} must be accessible"
            );
        }
    }

    #[test]
    fn starter_node_id_node_has_origin_coord() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let sid = starter_node_id(&graph).expect("starter node present");
            let node = graph.find_node(sid).unwrap();
            assert_eq!(
                node.coord.chunk_x, 0,
                "seed {seed}: starter node {sid} chunk_x must be 0"
            );
            assert_eq!(
                node.coord.chunk_y, 0,
                "seed {seed}: starter node {sid} chunk_y must be 0"
            );
            assert_eq!(
                node.coord.chunk_z, 0,
                "seed {seed}: starter node {sid} chunk_z must be 0"
            );
        }
    }

    #[test]
    fn starter_node_id_used_with_reachable_from() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let sid = starter_node_id(&graph).expect("starter node present");
            let reachable = reachable_from(&graph, sid);
            assert!(
                reachable.contains(&sid),
                "seed {seed}: reachable set must include starter node {sid}"
            );
        }
    }

    #[test]
    fn starter_node_id_is_deterministic() {
        for seed in [0u64, 42, 7778] {
            let g1 = build_level0_region_graph(seed);
            let g2 = build_level0_region_graph(seed);
            assert_eq!(
                starter_node_id(&g1),
                starter_node_id(&g2),
                "seed {seed}: starter_node_id not deterministic"
            );
        }
    }

    // ─── 3.2-prep-A: from_generated parity ───

    fn assert_proven_connections_from_generated_matches(seed: u64) {
        let generated = generate_initial_structure_chunks(seed);
        let from_gen = level0_proven_structure_connections_from_generated(&generated);
        let from_seed = level0_proven_structure_connections(seed);
        assert_eq!(
            from_gen, from_seed,
            "seed {seed}: from_generated and seed-based proven connections differ"
        );
    }

    #[test]
    fn proven_connections_from_generated_matches_seed_0() {
        assert_proven_connections_from_generated_matches(0);
    }

    #[test]
    fn proven_connections_from_generated_matches_seed_42() {
        assert_proven_connections_from_generated_matches(42);
    }

    #[test]
    fn proven_connections_from_generated_matches_seed_7778() {
        assert_proven_connections_from_generated_matches(7778);
    }

    fn assert_graph_from_generated_matches(seed: u64) {
        let generated = generate_initial_structure_chunks(seed);
        let g_seed = build_level0_region_graph(seed);
        let g_gen = build_level0_region_graph_from_generated(seed, &generated);

        assert_eq!(
            g_gen.node_count(),
            g_seed.node_count(),
            "seed {seed}: node count mismatch"
        );
        assert_eq!(
            g_gen.edge_count(),
            g_seed.edge_count(),
            "seed {seed}: edge count mismatch"
        );

        // Full node equality (id, kind, coord, local bounds, accessible, perceptible).
        assert_eq!(
            g_gen.nodes, g_seed.nodes,
            "seed {seed}: node records differ"
        );

        // Full edge equality (id, from, to, kind, traversable, perceptible, cost).
        assert_eq!(
            g_gen.edges, g_seed.edges,
            "seed {seed}: edge records differ"
        );

        // Audit metrics must also match.
        let a_gen = audit_level0_region_graph(&g_gen);
        let a_seed = audit_level0_region_graph(&g_seed);
        assert_eq!(a_gen, a_seed, "seed {seed}: audit mismatch");
    }

    #[test]
    fn graph_from_generated_matches_seed_0() {
        assert_graph_from_generated_matches(0);
    }

    #[test]
    fn graph_from_generated_matches_seed_42() {
        assert_graph_from_generated_matches(42);
    }

    #[test]
    fn graph_from_generated_matches_seed_7778() {
        assert_graph_from_generated_matches(7778);
    }

    // ─── 3.2A: node selection helpers ───

    #[test]
    fn level0_node_ids_returns_all_sorted() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let ids = level0_node_ids(&graph);
            assert_eq!(ids.len(), graph.node_count(), "seed {seed}: count mismatch");
            let mut sorted = ids.clone();
            sorted.sort_unstable();
            assert_eq!(ids, sorted, "seed {seed}: level0_node_ids not sorted");
        }
    }

    #[test]
    fn level0_accessible_node_ids_are_accessible_and_sorted() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let ids = level0_accessible_node_ids(&graph);
            let mut sorted = ids.clone();
            sorted.sort_unstable();
            assert_eq!(ids, sorted, "seed {seed}: not sorted");
            for id in &ids {
                let node = graph.find_node(*id).unwrap();
                assert!(node.accessible, "seed {seed}: node {id} is not accessible");
            }
            // Count must match direct filter.
            assert_eq!(
                ids.len(),
                graph.nodes.iter().filter(|n| n.accessible).count(),
                "seed {seed}: accessible count mismatch"
            );
        }
    }

    #[test]
    fn level0_reachable_from_starter_matches_reachable_from() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let via_helper = level0_reachable_node_ids_from_starter(&graph);
            let starter = starter_node_id(&graph).expect("seed {seed}: starter must exist");
            let direct = reachable_from(&graph, starter);
            assert_eq!(
                via_helper, direct,
                "seed {seed}: mismatch with reachable_from"
            );
        }
    }

    #[test]
    fn level0_reachable_from_starter_empty_on_empty_graph() {
        use crate::world::graph::coords::RegionCoord;
        use crate::world::graph::region_graph::RegionGraph;
        let empty = RegionGraph::new(RegionCoord::level0(0, 0));
        assert!(
            level0_reachable_node_ids_from_starter(&empty).is_empty(),
            "empty graph must return empty vec"
        );
    }

    #[test]
    fn level0_node_ids_by_kind_returns_only_that_kind() {
        let graph = build_level0_region_graph(0);
        for kind in [
            SpatialNodeKind::Room,
            SpatialNodeKind::Corridor,
            SpatialNodeKind::Intersection,
            SpatialNodeKind::ManilaRoom,
            SpatialNodeKind::DangerPocket,
        ] {
            let ids = level0_node_ids_by_kind(&graph, kind);
            let mut sorted = ids.clone();
            sorted.sort_unstable();
            assert_eq!(ids, sorted, "{kind:?}: not sorted");
            for id in &ids {
                let node = graph.find_node(*id).unwrap();
                assert_eq!(node.kind, kind, "{kind:?}: node {id} has wrong kind");
            }
        }
    }

    #[test]
    fn level0_safe_node_ids_are_manila_room_and_sorted() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let ids = level0_safe_node_ids(&graph);
            let mut sorted = ids.clone();
            sorted.sort_unstable();
            assert_eq!(ids, sorted, "seed {seed}: not sorted");
            for id in &ids {
                let node = graph.find_node(*id).unwrap();
                assert!(
                    matches!(node.kind, SpatialNodeKind::ManilaRoom),
                    "seed {seed}: safe node {id} has unexpected kind {:?}",
                    node.kind
                );
            }
        }
    }

    #[test]
    fn level0_danger_node_ids_are_danger_pocket_and_sorted() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let ids = level0_danger_node_ids(&graph);
            let mut sorted = ids.clone();
            sorted.sort_unstable();
            assert_eq!(ids, sorted, "seed {seed}: not sorted");
            for id in &ids {
                let node = graph.find_node(*id).unwrap();
                assert!(
                    matches!(node.kind, SpatialNodeKind::DangerPocket),
                    "seed {seed}: danger node {id} has unexpected kind {:?}",
                    node.kind
                );
            }
        }
    }

    #[test]
    fn level0_query_helpers_are_deterministic() {
        let g1 = build_level0_region_graph(42);
        let g2 = build_level0_region_graph(42);
        assert_eq!(level0_node_ids(&g1), level0_node_ids(&g2));
        assert_eq!(
            level0_accessible_node_ids(&g1),
            level0_accessible_node_ids(&g2)
        );
        assert_eq!(
            level0_reachable_node_ids_from_starter(&g1),
            level0_reachable_node_ids_from_starter(&g2)
        );
        assert_eq!(
            level0_node_ids_by_kind(&g1, SpatialNodeKind::Room),
            level0_node_ids_by_kind(&g2, SpatialNodeKind::Room)
        );
        assert_eq!(level0_safe_node_ids(&g1), level0_safe_node_ids(&g2));
        assert_eq!(level0_danger_node_ids(&g1), level0_danger_node_ids(&g2));
    }

    // ─── 6.5: parallel verticality layer ───

    #[test]
    fn level0_vertical_connections_are_deterministic() {
        let a = build_level0_region_graph(42);
        let b = build_level0_region_graph(42);
        assert_eq!(a.vertical_connections, b.vertical_connections);
    }

    #[test]
    fn level0_vertical_connections_reference_existing_base_nodes() {
        let graph = build_level0_region_graph(42);
        let ids: HashSet<u32> = graph.nodes.iter().map(|n| n.id).collect();
        for vc in &graph.vertical_connections {
            assert!(
                ids.contains(&vc.from),
                "vc.from {} not a graph node",
                vc.from
            );
        }
    }

    #[test]
    fn level0_vertical_connections_do_not_modify_legacy_edges() {
        let graph = build_level0_region_graph(42);
        assert!(graph.edges.iter().all(|e| !e.is_vertical()));
    }

    /// `to`/`connector` are virtual IDs that must never collide with legacy
    /// node IDs, and the verticality layer must not change legacy counts.
    #[test]
    fn level0_vertical_connections_use_virtual_ids_and_preserve_legacy_counts() {
        for seed in [0u64, 42, 7778] {
            let g1 = build_level0_region_graph(seed);
            let g2 = build_level0_region_graph(seed);
            assert_eq!(g1.node_count(), g2.node_count(), "seed {seed}");
            assert_eq!(g1.edge_count(), g2.edge_count(), "seed {seed}");

            let ids: HashSet<u32> = g1.nodes.iter().map(|n| n.id).collect();
            for vc in &g1.vertical_connections {
                assert!(
                    !ids.contains(&vc.to),
                    "seed {seed}: virtual upper id {} collides with a legacy node",
                    vc.to
                );
                assert!(
                    !ids.contains(&vc.connector),
                    "seed {seed}: virtual connector id {} collides with a legacy node",
                    vc.connector
                );
            }
        }
    }

    // ─── 6.5b: virtual vertical node materialization ───

    #[test]
    fn level0_virtual_vertical_nodes_are_deterministic() {
        let a = build_level0_region_graph(42);
        let b = build_level0_region_graph(42);
        assert_eq!(a.virtual_vertical_nodes, b.virtual_vertical_nodes);
    }

    #[test]
    fn level0_every_virtual_id_has_a_materialized_node() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            for vc in &graph.vertical_connections {
                assert!(
                    graph.find_virtual_vertical_node(vc.to).is_some(),
                    "seed {seed}: vc.to {} has no VirtualVerticalNode",
                    vc.to
                );
                assert!(
                    graph.find_virtual_vertical_node(vc.connector).is_some(),
                    "seed {seed}: vc.connector {} has no VirtualVerticalNode",
                    vc.connector
                );
            }
            assert_eq!(
                graph.virtual_vertical_node_count(),
                graph.vertical_connection_count() * 2,
                "seed {seed}: expected exactly 2 virtual nodes per connection"
            );
        }
    }

    #[test]
    fn level0_virtual_vertical_nodes_never_appear_in_legacy_nodes() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let legacy_ids: HashSet<u32> = graph.nodes.iter().map(|n| n.id).collect();
            for v in &graph.virtual_vertical_nodes {
                assert!(
                    !legacy_ids.contains(&v.id),
                    "seed {seed}: virtual node {} collides with legacy node",
                    v.id
                );
            }
        }
    }

    #[test]
    fn level0_virtual_vertical_nodes_preserve_legacy_counts_and_audit() {
        for seed in [0u64, 42, 7778] {
            let g1 = build_level0_region_graph(seed);
            let g2 = build_level0_region_graph(seed);
            assert_eq!(g1.node_count(), g2.node_count(), "seed {seed}");
            assert_eq!(g1.edge_count(), g2.edge_count(), "seed {seed}");
            assert_eq!(
                audit_level0_region_graph(&g1),
                audit_level0_region_graph(&g2),
                "seed {seed}: legacy audit must be unaffected"
            );
        }
    }

    #[test]
    fn level0_virtual_vertical_nodes_are_inaccessible_with_valid_bounds() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            for v in &graph.virtual_vertical_nodes {
                assert!(
                    !v.accessible,
                    "seed {seed}: virtual node {} must not be accessible",
                    v.id
                );
                assert!(
                    v.perceptible,
                    "seed {seed}: virtual node {} must be perceptible",
                    v.id
                );
                assert!(
                    v.local_min[0] < v.local_max[0]
                        && v.local_min[1] < v.local_max[1]
                        && v.local_min[2] < v.local_max[2],
                    "seed {seed}: virtual node {} has invalid bounds",
                    v.id
                );
            }
        }
    }

    #[test]
    fn level0_vertical_layer_is_consistent() {
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            assert!(
                graph.vertical_layer_is_consistent(),
                "seed {seed}: vertical layer audit failed"
            );
        }
    }

    // ─── 6.6: vertical debug marker export ───

    #[test]
    fn level0_vertical_debug_export_count_matches_virtual_nodes() {
        use crate::world::chunk::{LAYER_HEIGHT, LAYOUT_CELL_SIZE};
        use crate::world::graph::verticality::export_vertical_debug_markers;
        for seed in [0u64, 42, 7778] {
            let graph = build_level0_region_graph(seed);
            let markers = export_vertical_debug_markers(
                &graph.virtual_vertical_nodes,
                LAYOUT_CELL_SIZE,
                LAYOUT_GRID_SIZE,
                LAYER_HEIGHT,
            );
            assert_eq!(
                markers.len(),
                graph.virtual_vertical_nodes.len(),
                "seed {seed}: marker count must equal virtual node count"
            );
        }
    }

    #[test]
    fn level0_vertical_debug_export_is_deterministic() {
        use crate::world::chunk::{LAYER_HEIGHT, LAYOUT_CELL_SIZE};
        use crate::world::graph::verticality::export_vertical_debug_markers;
        let g1 = build_level0_region_graph(42);
        let g2 = build_level0_region_graph(42);
        let m1 = export_vertical_debug_markers(
            &g1.virtual_vertical_nodes,
            LAYOUT_CELL_SIZE,
            LAYOUT_GRID_SIZE,
            LAYER_HEIGHT,
        );
        let m2 = export_vertical_debug_markers(
            &g2.virtual_vertical_nodes,
            LAYOUT_CELL_SIZE,
            LAYOUT_GRID_SIZE,
            LAYER_HEIGHT,
        );
        assert_eq!(m1, m2);
    }

    #[test]
    fn level0_vertical_debug_export_does_not_change_legacy_counts() {
        use crate::world::chunk::{LAYER_HEIGHT, LAYOUT_CELL_SIZE};
        use crate::world::graph::verticality::export_vertical_debug_markers;
        let graph = build_level0_region_graph(42);
        let nodes_before = graph.node_count();
        let edges_before = graph.edge_count();
        let _ = export_vertical_debug_markers(
            &graph.virtual_vertical_nodes,
            LAYOUT_CELL_SIZE,
            LAYOUT_GRID_SIZE,
            LAYER_HEIGHT,
        );
        assert_eq!(graph.node_count(), nodes_before);
        assert_eq!(graph.edge_count(), edges_before);
    }

    #[test]
    fn level0_query_helpers_do_not_affect_audit() {
        let graph = build_level0_region_graph(0);
        let audit_before = audit_level0_region_graph(&graph);
        let _ = level0_node_ids(&graph);
        let _ = level0_accessible_node_ids(&graph);
        let _ = level0_reachable_node_ids_from_starter(&graph);
        let _ = level0_node_ids_by_kind(&graph, SpatialNodeKind::Room);
        let _ = level0_safe_node_ids(&graph);
        let _ = level0_danger_node_ids(&graph);
        let audit_after = audit_level0_region_graph(&graph);
        assert_eq!(audit_before, audit_after);
    }
}
