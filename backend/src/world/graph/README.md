# graph/ — Spatial Navigation Graph

## Purpose

Represents the world as a navigable graph of spatial regions.
Built once after world generation. Used for AI pathfinding, connectivity audits,
debug visualizations, and future navigation systems.

**Not used for collision.** Movement authority stays in `collision.rs` + `ChunkLayoutV1`.

---

## Hierarchy

```
WorldGraph
  └─ Vec<LevelGraph>       (one per level, currently only Level 0)
        └─ Vec<RegionGraph> (one per region; Level 0 has exactly 1)
              ├─ Vec<SpatialNode>        ← traversal nodes
              ├─ Vec<ConnectionEdge>     ← traversal edges
              ├─ Vec<VerticalConnection> ← PARALLEL layer (Phase 6.5, no traversal)
              └─ Vec<VirtualVerticalNode> ← audit-only, never merged into nodes
```

---

## Files

### `world_graph.rs`

```rust
pub struct WorldGraph { pub world_seed: u64, pub levels: Vec<LevelGraph> }

impl WorldGraph {
  pub fn from_level0_region_graph(seed, rg) -> Self
  pub fn level0_region_graph(&self) -> Option<&RegionGraph>
  pub fn level0(&self) -> Option<&LevelGraph>
}
```

Entry point. `world.world_graph` is set after `generate_initial_structures`.
Before that call it is `None`. Reset to `None` on `apply_world_sync` / `reset_for_remote_world`.

---

### `region_graph.rs`

```rust
pub struct RegionGraph {
  pub coord: RegionCoord,
  pub nodes: Vec<SpatialNode>,
  pub edges: Vec<ConnectionEdge>,
  pub vertical_connections: Vec<VerticalConnection>,   // Phase 6.5 parallel layer
  pub virtual_vertical_nodes: Vec<VirtualVerticalNode>, // audit only
}
```

Key methods:
```rust
fn accessible_node_count(&self) -> usize
fn node_count / edge_count / vertical_connection_count / virtual_vertical_node_count
fn find_node(id) -> Option<&SpatialNode>
fn validate_references(&self) -> bool      // all edge endpoints exist
fn vertical_layer_is_consistent(&self) -> bool
fn has_vertical_content(&self) -> bool
```

---

### `nodes.rs`

```rust
pub struct SpatialNode {
  pub id: SpatialNodeId,    // u32
  pub kind: SpatialNodeKind,
  pub coord: Chunk3DCoord,
  pub local_min: [u8; 3],   // cell-space bounds (inclusive min, exclusive max)
  pub local_max: [u8; 3],
  pub accessible: bool,     // can the player traverse this node?
  pub perceptible: bool,    // can the player notice it even if inaccessible?
}
```

Node kinds:
`Room Corridor Intersection Stair Ramp Atrium Shaft SealedUpperSpace UnderfloorService
ManilaRoom DangerPocket BlockedPortal`

Helpers: `is_vertical()` `is_safe_zone()` `is_danger_zone()` `cell_volume()`
`world_bounds_2d(layout)` `world_bounds_3d(layout)`

---

### `edges.rs`

```rust
pub struct ConnectionEdge { pub from: SpatialNodeId, pub to: SpatialNodeId,
                            pub kind: ConnectionKind, pub traversable: bool }
```

`ConnectionKind`: `Doorway` (promoted, traversable) | `VisualOnlyGap` (adjacency-inferred)

Edge promotion rule (Phase 3.1D-B):
- Edge in `proven_connections` → `Doorway`, `traversable=true`
- Adjacency-inferred but not proven → `VisualOnlyGap`, `traversable=false`

---

### `verticality.rs`

**Phase 6.5 — parallel layer.** Virtual vertical nodes and connections that are:
- Never merged into `RegionGraph.nodes`
- Never used for traversal, collision, or pathfinding
- Used only for debug visualization and audit logs

```rust
pub struct VirtualVerticalNode { pub id: SpatialNodeId, pub accessible: bool(=false), ... }
pub struct VerticalConnection { pub from: SpatialNodeId, pub to: SpatialNodeId, pub connector: SpatialNodeId }
pub fn build_basic_vertical_connections(region_graph) -> Vec<VerticalConnection>
pub fn materialize_virtual_vertical_nodes(connections) -> Vec<VirtualVerticalNode>
pub fn export_vertical_debug_markers(nodes, cell_size, grid_size, layer_height) -> Vec<VerticalDebugMarkerV0>
```

**Invariant:** `VirtualVerticalNode.accessible` is always `false`.
Virtual node IDs must not collide with legacy node IDs.

---

### `coords.rs`

```rust
pub type LevelId = u8;
pub const LEVEL_0: LevelId = 0;
pub struct Chunk3DCoord { pub chunk_x: i32, pub chunk_y: i8, pub chunk_z: i32 }
pub struct RegionCoord { pub level_id: LevelId, pub region_index: u32 }
```

---

### `level_graph.rs`

```rust
pub struct LevelGraph { pub level_id: LevelId, pub regions: Vec<RegionGraph> }
impl LevelGraph {
  pub fn primary_region(&self) -> Option<&RegionGraph>  // regions[0]
  pub fn region_count(&self) -> usize
  pub fn add_region(&mut self, rg: RegionGraph)
}
```

Level 0 always has exactly one region (`region_count() == 1`). Tested.

---

## Systems That Depend on This Module

| System | Usage |
|---|---|
| `mod.rs` (World) | Stores `WorldGraph`, reads `level0_region_graph()` for audit logs and debug markers |
| `levels/level_0/region_graph_builder.rs` | Builds the graph from generated chunk data |
| `levels/level_0/validation.rs` | `validate_level0_region_graph(graph)` |
| `generator.rs` | `level0_proven_structure_connections_from_generated` feeds edge promotion |

---

## What Must Not Break

- `WorldGraph` must be `None` before `generate_initial_structures` and `Some` after. (Tested)
- Level 0 graph must have `node_count > 0` and `edge_count > 0`. (Tested)
- `reachable_from(graph, starter_id)` output must be sorted and deterministic. (Tested)
- Virtual vertical node IDs must not collide with legacy node IDs.
- `validate_references()` — all edge endpoints must reference existing nodes.
