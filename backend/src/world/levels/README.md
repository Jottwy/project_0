# levels/ — Level Definitions

## Purpose

Contains everything specific to one level's procedural logic.
Currently only Level 0 exists. Future levels (`level_1/`, etc.) follow the same pattern.

---

## `level_0/` — The Backrooms Level 0 (The Lobby)

### What it does
Builds the complete connected structure network for Level 0 from a world seed.
This is the most expensive part of world generation — called once at startup.

Profile: `name = "Level 0 - The Lobby"`, `default_risk = 1`, `supports_verticality = true`

---

### Files

#### `builder.rs` — `Level0Builder`

The main generation orchestrator. All placement is deterministic from `world_seed`.

```rust
pub(crate) struct Level0Builder { world_seed, rng, occupied: HashSet<ChunkPos>, structures, sid }
```

Called only from `generator::generate_initial_structure_chunks(seed)`.
Returns `Vec<(StructureV0, Chunk)>` — one entry per chunk (structures repeat per chunk).

**Generation order (approximate):**
1. StarterCluster at (0,0) — safe room + surrounding hallways
2. HallwayChains growing outward — backbone corridors
3. Rooms branching off corridors — Storage, Danger, Pillar, Open Hall…
4. Special zones — Manila, Blackout, Red Room, Humid, Cleaning, Pit…
5. POI structures — Landmark, Anomaly, DangerPocket, SafePocket
6. V30A multi-layer structures — Atrium, StackedCorridor, LowerService, UpperOffice, GiantPillar

**Must-not-touch:** The seeding strategy (`world_seed ^ 0xBACB_00B5_CAFE_0001`).
Changing it changes the world layout for every existing seed.

---

#### `structure.rs` — `StructureV0` + `StructureType`

```rust
pub struct StructureV0 {
  pub id: u32,
  pub structure_type: StructureType,
  pub origin: ChunkPos,
  pub origin_layer: ChunkLayer,
  pub size: [u8; 2],
  pub seed: u64,
  pub chunks: Vec<ChunkPos>,
  pub layers: Vec<ChunkLayer>,
  pub tags: Vec<&'static str>,
  pub chunk_overrides: Vec<(u8, u16)>,  // (template_id, rotation) per chunk
}
```

28 structure types. See `WORLD_CONTEXT.md §12` for full list.

---

#### `region_graph_builder.rs`

```rust
pub fn build_level0_region_graph_from_generated(seed, &generated) -> RegionGraph
pub fn build_level0_region_graph(seed) -> RegionGraph           // convenience (re-generates)
pub fn audit_level0_region_graph(graph) -> RegionGraphAudit
pub fn starter_node_id(graph) -> Option<SpatialNodeId>
pub fn reachable_from(graph, start_id) -> Vec<SpatialNodeId>    // sorted, deterministic
```

**Key contract (Phase 3.1D-B):** `build_level0_region_graph_from_generated` accepts
already-generated data (avoids second `Level0Builder` run). Always use this path from
`World::generate_initial_structures` — never call `build_level0_region_graph` from the
hot path (it re-generates).

**`reachable_from` contract (Phase 3.1E):** Output is always sorted ascending.
`reachable_from(g, id) == reachable_from(g, id)` — must be deterministic.

`RegionGraphAudit` fields logged at MPTRACE step=RG1:
`node_count edge_count accessible_node_count visual_only_edge_count traversable_edge_count
dangling_edge_count manila_room_count danger_pocket_count blocked_portal_count
sealed_upper_count underfloor_count`

---

#### `validation.rs`

```rust
pub fn validate_level0_region_graph(graph: &RegionGraph) -> bool
```

Passes if: references valid, at least one accessible node, vertical layer consistent.
Tested on seeds 0, 42, 7778.

---

#### `ascii_export.rs`

```rust
pub fn export_level0_ascii(chunks: &HashMap<LayeredChunkPos, Chunk>) -> String
```

Debug utility — renders a top-down ASCII map of the generated world.
Re-exported as `generator::export_level0_ascii`.

---

#### `level0_golden_slice.rs`

Minimal golden-slice fixture for deterministic regression tests.
Contains a hardcoded expected chunk layout for a known seed/position.

---

#### `level0_profile.rs`

```rust
pub struct Level0Profile { pub name: String, pub default_risk: u8, pub supports_verticality: bool }
```

Metadata only. No behavior. Default: `"Level 0 - The Lobby"`, risk=1, verticality=true.

---

## Level 4 — Abandoned Offices (ADR-093, in progress)

Bounded incursion region (`docs/LEVEL4-ROADMAP.md`). It does NOT live in this
directory: the graph generator + 2.5 m rasterization live in `grid_gen/level4.rs` and
the 5 m collision half in `world/level4_layout.rs` — the same split as authored rooms
(`grid_gen` must not import `world/`). The chunk reserve starts at chunk (2000, 2000),
3×3 chunks, teleport-only access (E3). Salt: `world_seed ^ 0xBACB_0004_0FF1_CE00` —
same must-not-touch rule as `Level0Builder`'s salt. Room 0 always carries
`is_return_room`.

---

## Adding a New Level

When a `level_1/` (or any new level) is added:

1. Create `levels/level_1/` with at minimum: `mod.rs`, `builder.rs`, `structure.rs`
2. Add `pub mod level_1;` to `levels/mod.rs`
3. Add a new `LevelGraph` to `WorldGraph.levels` via `world_graph.add_level()`
4. Define a new `LevelId` constant in `graph/coords.rs`
5. Update `levels/README.md` (this file) with the new level's description
6. Add connectivity tests for the new level's seeds

---

## What Must Not Break

- `Level0Builder` placement is **fully deterministic** from `world_seed`. No `SystemTime`, no
  external entropy. The only RNG source is `StdRng::seed_from_u64(world_seed ^ SALT)`.
- `generate_initial_structure_chunks(seed)` called twice with the same seed must return
  structurally identical output (same chunk positions, templates, and connectivity).
- StarterCluster always anchors to `ChunkPos (0, 0)` at layer 0.
- All generated chunks must be reachable from `(0, 0, 0)` via BFS. (Tested for seeds 0, 42, 7778)
- `build_level0_region_graph_from_generated` must NOT call `Level0Builder` again.
