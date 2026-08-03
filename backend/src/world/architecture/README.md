# architecture/ — Layout Grammars & Chunk Build Helpers

## Purpose

Converts `template_id + rotation` into a populated `ChunkLayoutV1`.
Also owns spawn-safety rules and edge-opening finalization.

Extracted from `generator.rs` in **MIG-1** (migration 1). `architecture/mod.rs` is now the
canonical facade (it re-exports `build_chunk_layout` and the `TEMPLATE_*` constants) and
`generator.rs` consumes from it — since MIG-2 the generator no longer re-exports
`build_chunk_layout` itself. The `pub(crate) use` lines still in `generator.rs` are targeted
bridges for individual call sites, not a facade.

---

## Files

### `layout_grammars.rs`
The **single source of truth** for what each template looks like.

- `generate_layout_from_template(template_id, rotation) → ChunkLayoutV1`
- `open_boundary_gaps(layout)` — ensures boundary cells adjacent to openings are walkable
- `template_zone_kind(template_id) → u8`
- All `TEMPLATE_*` constants (re-exported via `generator.rs`)

One `LayoutGrammarType` per template family:
`CorridorSpine CorridorBroken RoomCluster OpenHall PillarGrid MazePocket ArchTransition
SideRooms HubAndSpokes ServiceArea BlackoutPocket RedWarningPocket ManilaRoom PitGridRoom
VerticalTransition PoiLandmark PoiAnomaly PoiDangerPocket PoiSafePocket`

**Critical rule:** Rotation is accepted as a parameter but **ignored here** — grammars produce
canonical (unrotated) layouts. Rotation is applied by the caller after generation.
This keeps grammars topology-only.

---

### `chunk_generator.rs`
Low-level chunk assembly helpers.

```rust
pub fn chunk_seed(world_seed: u64, pos: ChunkPos) -> u64
pub fn chunk_seed_layer(world_seed: u64, pos: ChunkPos, layer: ChunkLayer) -> u64
pub fn build_chunk_layout(template_id: u8, rotation: u16) -> ChunkLayoutV1
pub fn next_entity_id_pub() -> u32    // global AtomicU32, monotonic per session
```

`chunk_seed` is a deterministic hash — same world_seed + pos always produces the same chunk.
`build_chunk_layout` calls the grammar, applies floor/ceiling/light profiles by template,
and runs `finalize_level0_edges`.

---

### `collision_builder.rs`
Safe-cell validation and spawn-area reservation.

```rust
pub fn item_cell_blocked(layout: &ChunkLayoutV1, x: usize, z: usize) -> bool
pub fn world_to_cell(layout, chunk_pos, world_x, world_z) -> (usize, usize)
pub fn relocate_contents_to_safe_cells(chunk: &mut Chunk, ...)
pub fn reserve_starter_spawn_area(chunks: &mut [(StructureV0, Chunk)])
pub fn template_is_vertical(template_id: u8) -> bool
```

**Hard rule:** A cell is blocked if it has `WALL | BLOCKED | PILLAR | PIT | LOW_WALL |
HALF_WALL | THIN_PARTITION | FALSE_DOOR`. Doors and arches remain valid.

`reserve_starter_spawn_area` marks a clear zone at (0,0) origin — do not touch without
full spawn regression.

---

### `surface_builder.rs`
Edge-opening finalization (Phase 2.7 edge-wall model).

```rust
pub fn perimeter_openings(layout: &ChunkLayoutV1) -> u8   // edge_openings bitmask
pub fn finalize_level0_edges(chunks: &mut [(StructureV0, Chunk)])
pub fn edge_delta(edge: u8) -> ChunkPos
pub fn opposite_edge(edge: u8) -> u8
// (test-only) boundary_opening_cells, edge_is_opening
```

`finalize_level0_edges` does two passes:
1. Seals perimeter edges that face no neighbour chunk.
2. Repairs reciprocal openings: if chunk A opens toward chunk B, B must also open toward A.

**Critical:** Do not bypass reciprocal repair. Asymmetric openings cause one-way walls.

---

### `chunk3d_layout.rs`
Thin 3D coordinate wrapper used by the spatial graph.

```rust
pub struct Chunk3DLayout { coord: Chunk3DCoord, cell_size, grid_size, layer_height }
pub fn from_chunk_layout(coord, layout) -> Chunk3DLayout
pub fn cell_world_size(&self) -> f32   // chunk width in world metres
```

---

## Dependencies

- Reads from: `crate::world::chunk` (all constants and layout types)
- Called by: `generator.rs`, `levels/level_0/builder.rs`
- No external module dependencies beyond `chunk` and `utils`

---

## What Must Not Break

- `generate_layout_from_template` must be pure and deterministic — same inputs, same output.
- `chunk_seed` hash function — changing it changes the entire world for all seeds.
- `item_cell_blocked` ruleset — relaxing it allows entities/items to spawn inside walls.
- `finalize_level0_edges` reciprocal repair — asymmetric openings = one-way wall bug.
- `TEMPLATE_*` constant values — they index into a `[u32; 18]` stats array in `mod.rs`.
