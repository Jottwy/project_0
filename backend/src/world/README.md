# world — Backend Subsystem

**Single-sentence purpose:** Owns and simulates the game world — procedural generation,
chunk lifecycle, entity AI, collision authority, and IPC views to Unity.

This module is `backend/src/world/`. It is **one subsystem** of a larger backend.
It does not own networking transport, player sessions, inventory logic, or persistence.

---

## Quick-Reference Map

| File / Folder | Role |
|---|---|
| `mod.rs` | `World` struct — public API, tick loop, IPC views, networking integration |
| `chunk.rs` | `Chunk`, `ChunkLayoutV1`, all cell/edge/zone/layer constants |
| `entity.rs` | `Entity` AI state machine (Lurker / Crawler / Shadow) |
| `generator.rs` | Entry points for chunk + structure generation; re-exports template IDs |
| `collision.rs` | `Level0Collision` — authoritative move resolution + safe spawn |
| `volumetric_grid.rs` | `VolumetricGridViewV0` — 3D render metadata (no collision authority) |
| `architecture/` | Layout grammars, chunk seeding, surface/collision build helpers |
| `graph/` | `WorldGraph` → `RegionGraph` → `SpatialNode`/`ConnectionEdge` |
| `levels/` | `Level0Builder` — places and connects all structures for Level 0 |

---

## Critical Constants (do not change without full audit)

```rust
LAYOUT_GRID_SIZE = 10      // cells per chunk side
LAYOUT_CELL_SIZE = 5.0     // metres per cell → chunk = 50 m
LAYER_HEIGHT     = 7.0     // metres between vertical macro layers
PLAYER_RADIUS    = 0.35    // collision capsule radius
```

---

## Public API Surface (what the game loop calls)

```rust
// Initialization
World::new(seed)
world.generate_initial_structures(owner_id)   // builds Level 0 + WorldGraph

// Runtime
world.update_ownership(player_pos, player_id) // load/unload radius management
world.tick_teleportation()    // 1 hz — random chunk displacement
world.tick_entities(dt, ..)   // entity AI ticks
world.tick_respawns(dt, peer_id)  // respawn timers; peer_id partitions minted ids (ADR-063)

// IPC → Unity (10 hz WorldState)
world.visible_chunk_views()   // cached; invalidated on revision change
world.visible_entity_views()
world.visible_item_views()

// Networking
world.apply_world_sync(..)
world.apply_chunk_transfer(..)
world.apply_remote_teleport(..)
world.set_chunk_stabilized(..)
world.set_chunk_anchored(..)

// Gameplay
world.interact_with_item(id, requester_pos, max_distance)
world.stat_context_for(player_pos, nearby_players)
```

---

## External Dependencies (modules outside `world/`)

| Import | Used for |
|---|---|
| `crate::ipc::{ChunkView, EntityView, GameEvent, ItemView}` | IPC view structs sent to Unity |
| `crate::network::{PeerId, protocol::ChunkSyncData}` | P2P peer identity + sync protocol |
| `crate::network::sync::chunk_to_sync_data` | Serialize a chunk for network sync |
| `crate::player::stats::StatContext` | Gameplay stat context (entities visible, etc.) |
| `crate::player::inventory::Item` | Item types for `DroppedItem` in chunks |
| `crate::utils::{Vec3, ChunkPos, CHUNK_SIZE, world_to_chunk, chunks_in_radius}` | Math + coordinate helpers |

---

## See Also

- [`WORLD_CONTEXT.md`](./WORLD_CONTEXT.md) — full flow, contracts, risk zones
- [`architecture/README.md`](./architecture/README.md) — layout grammars, chunk builders
- [`graph/README.md`](./graph/README.md) — spatial navigation graph
- [`levels/README.md`](./levels/README.md) — Level 0 structure placement
- [`MAINTENANCE_POLICY.md`](./MAINTENANCE_POLICY.md) — how to keep docs in sync
