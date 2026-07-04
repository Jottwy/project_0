# WORLD_CONTEXT — Full Subsystem Reference

> **Audience:** Claude Code sessions, future contributors.
> Read this file + the README of the relevant subfolder before touching any code.

---

## 1. Purpose & Boundaries

The `world` subsystem is the backend's **simulation authority**. It answers:
- What does the world look like? (chunk layouts, entity positions, dropped items)
- Where can the player stand? (collision resolution)
- What happens next? (entity AI, chunk teleportation, respawns)
- What does Unity need to render? (IPC view structs)

It does **not** own:
- TCP/UDP transport (→ `networking`)
- Player session state, health, inventory (→ `player`)
- Persistence / save files (→ separate module, not in this zip)
- Unity-side rendering decisions (only provides data)

---

## 2. Core Data Model

### `World` (`mod.rs`)
```
World {
  seed: u64                               // deterministic generation key
  revision: u64                           // incremented on any state change
  config: WorldConfig                     // ownership_radius(3), unload_radius(4), etc.
  chunks: HashMap<LayeredChunkPos, Chunk> // all active chunks
  rng: StdRng                             // seeded from world seed + 0xDEAD
  respawn_queue: Vec<(id, ChunkPos, timer)>
  v30a_chunk_cache: Option<Vec<Chunk>>    // multi-layer chunks, regenerated once
  view_cache: Option<(revision, count, Vec<ChunkView>)>  // IPC cache
  world_graph: Option<WorldGraph>         // built after generate_initial_structures
}
```

### `Chunk` (`chunk.rs`)
```
Chunk {
  pos: ChunkPos               // (i32, i32) — X/Z grid position
  layer: ChunkLayer           // i8 — vertical macro layer (usually 0)
  state: ChunkState           // Unloaded | Dormant | Active{stabilized, anchored}
  seed: u64                   // per-chunk seed (changes on teleport)
  owner: Option<PeerId>       // which peer simulates this chunk
  entities: Vec<Entity>
  items: Vec<DroppedItem>
  teleport_timer: f32         // seconds until next displacement
  template_id: u8             // layout grammar (0–17+)
  rotation: u16               // 0 / 90 / 180 / 270
  mirrored: bool
  has_workbench: bool
  layout: ChunkLayoutV1
}
```

### `ChunkLayoutV1` (`chunk.rs`)
The authoritative grid layout. **10×10 cells, 5m each → 50m chunk.**
```
ChunkLayoutV1 {
  cells: Vec<u16>       // 100 cells; each is a bitfield of CELL_* flags
  edge_openings: u8     // which sides (N/E/S/W) have passages to neighbours
  edges_v: Vec<u8>      // (grid+1)*grid vertical wall edges
  edges_h: Vec<u8>      // grid*(grid+1) horizontal wall edges
  zone_kind: u8         // ZONE_* constant
  floor_profile: u8     // FLOOR_FLAT / SUNKEN / RAISED / RAMP_* / STAIRS_* / CONNECTOR_*
  ceiling_profile: u8   // CEILING_NORMAL / LOW_SERVICE / TALL_HALL / DAMAGED
  light_profile: u8     // LIGHT_NORMAL / DIM / BLACKOUT / RED / WARM
  vertical_flags: u16   // V30A_* bitmask for multi-layer architecture
  inter_layer_volumes: Vec<InterLayerVolumeV0>  // cross-layer render metadata
  anomaly_flags: u16
  macro_id, macro_local, macro_size  // structure membership
}
```

**Cell flags** (bitfield u16):
`WALKABLE WALL PILLAR BLOCKED HAZARD RAMP PIT SHALLOW_FLUID SAFE ANOMALY DOOR ARCH LOW_WALL HALF_WALL THIN_PARTITION FALSE_DOOR`

**Edge kinds** (Phase 2.7, walls live on cell *edges* not cells):
`OPEN WALL DOOR ARCH LOW_WALL HALF_WALL PARTITION FALSE_DOOR BROKEN`

---

## 3. Coordinate System

```
LayeredChunkPos = (chunk_x: i32, layer: i8, chunk_z: i32)
ChunkPos        = (i32, i32)   // XZ only, layer=0 implied
Vec3            = world space (metres), Y-up
```

Layer `n` root Y = `n * 7.0` metres (`LAYER_HEIGHT = 7.0`).
Chunk `(x, z)` world origin = `(x * 50.0, z * 50.0)` metres.
Cell `(cx, cz)` inside chunk → local offset `(cx * 5.0, cz * 5.0)`.

---

## 4. Generation Pipeline

```
World::generate_initial_structures(owner_id)
  │
  ├─ generator::generate_initial_structure_chunks(seed)
  │     └─ Level0Builder::build()
  │           ├─ place StarterCluster at (0,0)
  │           ├─ grow HallwayChains, Intersections, StorageRooms…
  │           ├─ place POI structures (Landmark, Anomaly, DangerPocket, SafePocket)
  │           ├─ place V30A vertical structures (Atrium, StackedCorridor…)
  │           └─ returns Vec<(StructureV0, Chunk)>  ← DETERMINISTIC from seed
  │
  ├─ build_level0_region_graph_from_generated(seed, &generated)
  │     ├─ creates SpatialNodes per structure
  │     ├─ infers edges from chunk-boundary adjacency
  │     ├─ promotes proven connections → ConnectionKind::Doorway
  │     ├─ unpromoted adjacency → ConnectionKind::VisualOnlyGap
  │     └─ builds parallel verticality layer (virtual nodes, no traversal)
  │
  ├─ WorldGraph::from_level0_region_graph(seed, rg)  → world.world_graph
  │
  ├─ BFS connectivity check: all chunks must be reachable from (0,0,0)
  │
  └─ volumetric_grid::log_level0_adapter_fix_once(…)  // audit only
```

**Runtime chunk generation** (`update_ownership`):
- Chunks within `ownership_radius` (3) are loaded.
- Chunks outside `unload_radius` (4) are removed. The gap is hysteresis.
- Individual chunks generated via `generator::generate_chunk(seed, pos)`.
- V30A chunks restored from `v30a_chunk_cache` (never re-run Level0Builder).

---

## 5. Chunk Displacement (the core Backrooms mechanic)

`tick_teleportation()` runs at **1 hz**:
- Counts down `chunk.teleport_timer` for every `Active` layer-0 chunk.
- `stabilized=false` → teleport when timer hits 0.
- `stabilized=true` → 95% chance the stabilizer blocks the teleport.
- `anchored=true` → never teleports.
- On teleport: chunk gets a new random `seed`, entities/items regenerated.
- Emits `GameEvent { event_type: "chunk_teleported" }`.

Timer range: `config.teleport_interval` (default 120–600 seconds).

---

## 6. Entity AI

Three entity types with fixed HP and speed:
| Type | HP | Speed | Aggro speed |
|---|---|---|---|
| Lurker | 50 | 2.0 m/s | 2.6 m/s |
| Crawler | 30 | 5.0 m/s | 6.5 m/s |
| Shadow | 40 | 3.0 m/s | 3.9 m/s |

State machine per entity:
```
Idle ─(dist < 20m)→ Alert ─(dist < 10m)→ Aggro ─(dist < 2m, cooldown=0)→ AttackPlayer
Aggro ─(dist > 25m)→ Idle
Alert ─(search_timer=0)→ Idle
Dead ─(despawn_timer=0)→ EntityEvent::Despawned → respawn_queue (120–300s)
```

All AI runs in `tick_entities(dt, player_pos, player_id)` — called from game loop.

---

## 7. IPC to Unity

Called at **10 hz** from the WorldState sender:

```rust
world.visible_chunk_views()   // cached per (revision, chunk_count)
world.visible_entity_views()  // always rebuilt
world.visible_item_views()    // always rebuilt
```

`ChunkView` carries the full layout: packed cell grid + edge arrays in `layout_cells`
(format: `[cells(100)] [edges_v(110)] [edges_h(110)]` as `Vec<u16>`), plus all profiles
and the optional `volumetric_grid: Option<VolumetricGridViewV0>`.

Views are sorted by `(pos[0], layer, pos[1])` for stable serialization.

---

## 8. Volumetric Grid (render metadata only)

`VolumetricGridViewV0` is sent to Unity for:
- Showcase chunks near spawn (seed 7778, positions `[0,0],[1,0],[0,1],[1,1]`)
- All layer-0 chunks if `ENABLE_LEVEL0_VOLUMETRIC_COLUMNS = true` (currently **false**)

(2026-07-02) The `chunk_is_v30a()` disjunct was REMOVED from this gate: under
worldgraph-v1 nearly every layer-0 chunk carries a V30A vertical flag (measured
60/62), so it attached the column payload world-wide (~1.3 MB per 10 Hz WorldState),
saturating the IPC pipe and dropping one-shot events under lag. V30A features render
via the flat fallback path (`vertical_flags` / `inter_layer_volumes` still ship).

**Critical:** Volumetric grid has zero collision/movement authority.
It is pure render metadata. Backend collision stays in `collision.rs` + `ChunkLayoutV1`.

Sources: `LEVEL0_ADAPTER` | `RUBIKGRID_ADAPTER` (4 RubikGrid showcase columns) | `INTER_LAYER_ADAPTER`

---

## 9. Networking Integration

```rust
// Host → joining peer
world.apply_world_sync(seed, revision, &chunks, local_id)

// Chunk ownership transfer (P2P)
world.apply_chunk_transfer(&data, local_id)

// Remote displacement event
world.apply_remote_teleport(old_pos, new_seed)

// Items / stabilization
world.interact_with_item(id, pos, max_dist) → Result<(type_name, qty), err>
world.set_chunk_stabilized(chunk_pos)
world.set_chunk_anchored(chunk_pos)
```

`reset_for_remote_world()` clears all local chunks and resets to host seed.

---

## 10. Validated Contracts

These invariants are tested and must not be broken:

| Contract | Test |
|---|---|
| Generated world fully connected from `(0,0,0)` via BFS | `generated_world_has_connected_initial_structure` |
| `visible_chunk_views()` cached until revision changes | `chunk_views_are_cached_until_world_changes` |
| Views sorted `(x, layer, z)` | `visible_chunk_views_are_sorted_for_stable_serialization` |
| `ownership_radius` loads correct square count | `ownership_loads_chunks_around_player` |
| Hysteresis: trailing chunk kept until `unload_radius` crossed | `ownership_hysteresis_keeps_trailing_edge_chunks` |
| Item removal increments revision exactly once | `valid_item_interaction_removes_item_and_increments_revision_once` |
| Seed 7778 → exactly 4 RubikGrid columns at `[0,0],[1,0],[0,1],[1,1]` | `seed_7778_multichunk_views_carry_volumetric_showcase` |
| RegionGraph and chunk BFS connectivity parity | `world_region_graph_connectivity_parity_seed_*` |
| `reachable_from()` output is sorted + deterministic | `world_region_graph_reachable_output_is_deterministic` |

---

## 11. Risk Zones

| Zone | Risk | Notes |
|---|---|---|
| `generate_initial_structure_chunks` + `Level0Builder` | HIGH | Touched = world generation changes for all seeds. Run all 3 seed tests. |
| `ChunkLayoutV1` field additions | MEDIUM | IPC struct changes require Unity client update. |
| `pack_layout_cells()` format | HIGH | Unity reads `[cells][edges_v][edges_h]` by positional offset. |
| `chunk_is_v30a()` predicate | MEDIUM | Gates V30A cache and vertical navigation (no longer the volumetric grid — see §8). |
| `view_cache` invalidation | LOW | Must invalidate on any structural change. Already tested. |
| `update_ownership` V30A restore path | MEDIUM | Avoid re-running Level0Builder in hot path (was a stall bug). |
| `level0_proven_structure_connections` | HIGH | Used for RegionGraph edge promotion. Non-determinism breaks connectivity. |
| Entity ID generation (`AtomicU32`) | MEDIUM | Global counter — not reset between generations. Stable across a session. |

---

## 12. Structure Types (28 total)

`StarterCluster HallwayChain Intersection StorageRoom SafeRoom DeadEnd DangerRoom HallwayT
PillarRoom OpenHall PillarHall HumidZone ArchRoom BlackoutZone RedRoom ManilaRoom CleaningArea
PitRoom StackedCorridor LowerServiceBranch UpperOfficeBranch AtriumVoidRoom DeepPrecipicePlaceholder
GiantPillarHall PoiLandmark PoiAnomalyCluster PoiDangerPocket PoiSafePocket`

V30A types (multi-layer): `StackedCorridor LowerServiceBranch UpperOfficeBranch AtriumVoidRoom
DeepPrecipicePlaceholder GiantPillarHall`
