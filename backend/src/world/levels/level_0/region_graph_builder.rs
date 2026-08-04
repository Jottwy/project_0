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
//
// AUDIT BATTERY — LEE ESTO ANTES DE BORRAR NADA DE AQUÍ ABAJO.
//
// Parte de lo que sigue es producción y parte es la batería de auditoría del grafo:
// consultas cuyos únicos llamadores HOY son los tests de `region_graph_builder/tests.rs`
// (y, para `level0_proven_structure_connections`, también `generator/tests.rs`). NO son
// código muerto: son la forma en que se asertan, semilla a semilla, los invariantes del
// RegionGraph — orden ascendente, determinismo entre ejecuciones, simetría de
// `is_connected`, conectividad desde el nodo starter y recuentos por `SpatialNodeKind`.
//
// El crate lleva un `#![allow(dead_code)]` global (`main.rs`), así que el compilador NUNCA
// te va a avisar de que no tienen llamador de producción. De ahí esta nota.
//
// Alcanzables desde PRODUCCIÓN hoy (`world/mod.rs`, diagnóstico MPTRACE RG1/RG2/RG3 tras
// generar el mundo): `audit_level0_region_graph`, `starter_node_id`, `reachable_from` y,
// transitivamente (la llama `reachable_from`), `traversable_neighbors`.
//
// Sólo de auditoría hoy: las ocho marcadas `AUDIT-ONLY` más abajo. En la misma situación
// está el wrapper `build_level0_region_graph` del principio del fichero: producción usa
// `build_level0_region_graph_from_generated`, y la variante que sólo recibe la semilla
// vuelve a generar el mundo entero, cosa que sólo hacen los tests.

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
/// (undirected, iterative DFS). Always includes the start node itself. Result is sorted.
/// Returns empty Vec if the start node does not exist in the graph.
///
/// Depth-first is not a choice, it is just what a `Vec`-as-stack gives: visit ORDER is
/// irrelevant here because the answer is a set that gets sorted before returning.
pub(crate) fn reachable_from(graph: &RegionGraph, start_id: SpatialNodeId) -> Vec<SpatialNodeId> {
    if graph.find_node(start_id).is_none() {
        return Vec::new();
    }
    let mut visited: HashSet<SpatialNodeId> = HashSet::new();
    let mut stack: Vec<SpatialNodeId> = vec![start_id];
    visited.insert(start_id);

    while let Some(current) = stack.pop() {
        for neighbor in traversable_neighbors(graph, current) {
            if visited.insert(neighbor) {
                stack.push(neighbor);
            }
        }
    }

    let mut result: Vec<SpatialNodeId> = visited.into_iter().collect();
    result.sort_unstable();
    result
}

/// Returns true if `from` and `to` are in the same traversable-edge connected
/// component. Returns false if either node ID does not exist in the graph.
///
/// AUDIT-ONLY (ver la nota AUDIT BATTERY al inicio de esta sección): sus únicos llamadores
/// hoy son los tests. No borrar en una limpieza de código muerto.
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

// ─── Level 0 node selection helpers (AUDIT-ONLY: ver la nota AUDIT BATTERY arriba) ───
//
// Las seis funciones de esta sección no tienen llamador de producción hoy; existen para que
// los tests puedan interrogar el grafo por criterio. Se mantienen a propósito.

/// Returns all node IDs in the graph, sorted ascending.
///
/// AUDIT-ONLY: sus únicos llamadores hoy son los tests. No borrar en una limpieza.
pub(crate) fn level0_node_ids(graph: &RegionGraph) -> Vec<SpatialNodeId> {
    let mut ids: Vec<SpatialNodeId> = graph.nodes.iter().map(|n| n.id).collect();
    ids.sort_unstable();
    ids
}

/// Returns node IDs where `node.accessible == true`, sorted ascending.
///
/// AUDIT-ONLY: sus únicos llamadores hoy son los tests. No borrar en una limpieza.
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
///
/// AUDIT-ONLY: sus únicos llamadores hoy son los tests. Ojo, no confundir con
/// `reachable_from` + `starter_node_id`, que sí corren en producción (traza MPTRACE RG2):
/// este wrapper es la versión que los tests usan para comprobar que ambos concuerdan.
pub(crate) fn level0_reachable_node_ids_from_starter(graph: &RegionGraph) -> Vec<SpatialNodeId> {
    match starter_node_id(graph) {
        Some(starter) => reachable_from(graph, starter),
        None => Vec::new(),
    }
}

/// Returns node IDs whose `kind` matches `kind` exactly, sorted ascending.
///
/// AUDIT-ONLY: sus únicos llamadores hoy son los tests. No borrar en una limpieza.
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
///
/// AUDIT-ONLY: sus únicos llamadores hoy son los tests. No borrar en una limpieza.
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
///
/// AUDIT-ONLY: sus únicos llamadores hoy son los tests. No borrar en una limpieza.
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
///
/// AUDIT-ONLY: sus únicos llamadores hoy son los tests (`region_graph_builder/tests.rs` y
/// `generator/tests.rs`, que la importa vía el re-export `#[cfg(test)]` de `generator.rs`).
/// Producción llama a `level0_proven_structure_connections_from_generated` con los chunks ya
/// generados; este wrapper vuelve a generar el mundo entero, que es justo lo que los tests
/// quieren para comprobar que ambas rutas coinciden. No borrar en una limpieza.
pub(crate) fn level0_proven_structure_connections(world_seed: u64) -> Vec<(u32, u32)> {
    let generated = generate_initial_structure_chunks(world_seed);
    level0_proven_structure_connections_from_generated(&generated)
}

#[cfg(test)]
mod tests;
