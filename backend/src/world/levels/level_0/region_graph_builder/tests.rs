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
                    && ((e.from == node.id && e.to == nbr) || (e.to == node.id && e.from == nbr))
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
