# Safe Refactor Plan

> Date: 2026-06-08
> Based on: ARCHITECTURE_RISK_REVIEW.md findings
> Rule: No refactor should be applied without a passing test suite before AND after.

---

## Classification

| Level | Meaning | Action |
|-------|---------|--------|
| **R0** | Do not touch yet | Feature is under active development or change would conflict with in-flight work |
| **R1** | Safe — docs, logs, tests, dead code removal | Can be done on `main` with a single commit |
| **R2** | Low risk — small behavioral change | Review carefully, run full test suite, single commit |
| **R3** | Medium risk — requires separate branch | Create feature branch, full testing, PR review before merge |
| **R4** | High risk — postpone | Major protocol/architecture change, needs design doc first |

---

## R0 — Do Not Touch Yet

These modules are under active development by Codex or have unstable dependencies:

| Item | Reason |
|------|--------|
| `backend/src/network/mod.rs` — handshake logic | Codex may be iterating on peer registry / world interaction authority |
| `backend/src/game_loop.rs` — action handling | `world_interact` flow just added, may still be evolving |
| `backend/src/ipc/server.rs` — IPC write path | Tightly coupled to WorldState shape which may change |
| `Assets/Scripts/Network/IPCClient.cs` | `SendWorldInteractRequest` just added |
| `Assets/Scripts/Network/RemotePlayerManager.cs` | May need changes for interaction feedback |
| `Assets/Scripts/Gameplay/WorldInteractor.cs` | Just added, interaction UX may evolve |
| WorldSync fragmentation | Needs design doc before implementation |
| Delta encoding for IPC WorldState | Needs benchmarking before design |
| MsgPack library replacement (C#) | Too large, too risky right now |

---

## R1 — Safe: Documentation, Logs, Tests, Dead Code

These can be done immediately with near-zero risk:

### R1-1: Remove `Player.owned_chunks` dead field

**File:** `backend/src/player/session.rs`
**Change:** Remove `pub owned_chunks: Vec<ChunkPos>` and its initialization.
**Why:** Never written to. `Chunk.owner` is the source of truth. Leaving it invites someone to use it and create a desync.
**Test:** `cargo test` — no test references this field.

### R1-2: Remove `Entity.respawn_timer` dead field

**File:** `backend/src/world/entity.rs`
**Change:** Remove `pub respawn_timer: Option<f32>` and its initialization.
**Why:** Always `None`. Respawn is tracked by `World.respawn_queue`.
**Test:** `cargo test` — no test references this field.

### R1-3: Remove legacy `interact`/`pickup` match arm

**File:** `backend/src/game_loop.rs`
**Change:** Remove the `"interact" | "pickup"` arm that only logs "legacy local pickup ignored."
**Why:** Dead code. `world_interact` is the active path. The old arm does nothing but confuse.
**Test:** `cargo test` — this arm has no test.

### R1-4: Add WorldSync size test

**File:** New test in `backend/src/network/sync.rs` or `protocol.rs`
**Change:** Test that serializes `WorldSync` for 121 chunks and asserts the byte size.
**Why:** Detects when world growth would break the single-packet assumption.
**Test:** Self-testing.

### R1-5: Add `interact_with_item` concurrent test

**File:** New test in `backend/src/world/mod.rs`
**Change:** Test that two calls to `interact_with_item` on the same target_id: first succeeds, second fails.
**Why:** Already partially tested (`valid_item_interaction_removes_item_and_increments_revision_once`) but the test name and structure should verify the double-pickup invariant more explicitly.
**Test:** Self-testing.

### R1-6: Add `stable_entity_id` collision test

**File:** New test in `backend/src/world/generator.rs`
**Change:** Generate IDs for all entities across 121 chunks, verify no collisions.
**Why:** If the hash function has collisions in the real ID space, `interact_with_item` would pick up the wrong item.
**Test:** Self-testing.

### R1-7: Reduce log verbosity for steady-state messages

**File:** `backend/src/network/mod.rs` (PlayerUpdate handler), `backend/src/network/sync.rs` (broadcast_player_update)
**Change:** Change per-packet `info!` to `debug!`. Keep MPTRACE lines at 1s intervals.
**Why:** 10+ info lines/sec per peer. Noise makes real errors hard to find.
**Caveat:** Marked R0 for mod.rs — defer until Codex's work stabilizes.

---

## R2 — Low Risk: Small Behavioral Changes

### R2-1: Subscribe `RemotePlayerManager` to `player_left` event

**File:** `Assets/Scripts/Network/RemotePlayerManager.cs`
**Change:** In `TrySubscribe()`, also subscribe to `IPCClient.AddEventListener`. On `player_left` event, immediately call `Release()` for the disconnected player ID.
**Why:** Currently ghosts linger 3 seconds. This gives instant despawn on known disconnect.
**Risk:** Low — additive change. Grace period still handles missed events.
**Note:** Marked R0 — defer until RemotePlayerManager stabilizes.

### R2-2: Guard `tick_entities` with `is_host` on joiner

**File:** `backend/src/game_loop.rs`
**Change:** Wrap entity tick with `if net.is_host || net.peer_count() == 0 { tick_entities() }`.
**Why:** Prevents dual simulation jitter. Joiner receives entity positions from host via ChunkState broadcast.
**Risk:** Low — joiner entities freeze between ChunkState updates (5hz). Acceptable for V0.
**Note:** Marked R0 — defer until game_loop stabilizes.

### R2-3: Remove `PeerInfo.addr` from PeerList broadcast

**File:** `backend/src/network/sync.rs` (`build_peer_list`)
**Change:** Replace `peer.addr.to_string()` with empty string or remove `addr` field from `PeerInfo`.
**Why:** IP leak to all peers. Address is transport-level, not needed by game logic.
**Risk:** Low — no code currently uses `PeerInfo.addr` on the receiving side.
**Note:** Marked R0 — defer until protocol stabilizes.

### R2-4: Extract HandshakeAck builder

**File:** `backend/src/network/mod.rs`
**Change:** Extract the three duplicate `PacketPayload::HandshakeAck { ... }` constructions into `fn build_handshake_ack(&self, assigned_id: PeerId) -> PacketPayload`.
**Why:** ~50 lines of copy-paste. DRY.
**Risk:** None — pure refactor with identical behavior.
**Note:** Marked R0 — defer until Codex's handshake work stabilizes.

### R2-5: Add item ID index to World

**File:** `backend/src/world/mod.rs`
**Change:** Add `item_index: HashMap<u32, ChunkPos>` maintained on chunk sync/generate. Use in `interact_with_item`.
**Why:** O(1) lookup instead of O(chunks × items).
**Risk:** Low — must keep index in sync on chunk add/remove/teleport.

### R2-6: Centralize MPTRACE emission

**File:** New `backend/src/utils/trace.rs` or macro in `utils/mod.rs`
**Change:** Create `mptrace!(step, event, key=value, ...)` macro. Replace inline `info!("MPTRACE ...")` calls.
**Why:** Consistent format, easier to grep, less error-prone.
**Risk:** Low — logging-only change.

---

## R3 — Medium Risk: Separate Branch Required

### R3-1: Fragment WorldSync into per-chunk reliable packets

**Files:** `sync.rs`, `mod.rs`, `game_loop.rs`
**Change:** Instead of one `WorldSync` with all chunks, send a `WorldSyncBegin { count }` + N × `ChunkTransfer` + `WorldSyncEnd`. Joiner assembles.
**Why:** P0 risk — current approach will break with larger worlds.
**Risk:** Medium — changes the joiner connection flow. Needs new state machine on receiver.
**Prerequisites:** Add size assertion test (R1-4) first to quantify the problem.

### R3-2: Add IPC version handshake

**Files:** `ipc/mod.rs`, `ipc/server.rs`, `IPCClient.cs`
**Change:** On connection, backend sends `{"type":"hello","version":"0.1.0","schema_hash":"..."}`. Unity validates before proceeding.
**Why:** Silent parse failures on version mismatch (P1-3).
**Risk:** Medium — changes IPC connection flow. Must be backward-compatible during transition.

### R3-3: Add `Disconnect` packet on graceful shutdown

**Files:** `NetworkInitializer.cs`, `mod.rs`
**Change:** Before killing the backend process, send a `Disconnect` IPC command that the backend forwards as a UDP `Disconnect` packet.
**Why:** Ghost players for 5s (P1-1).
**Risk:** Medium — timing sensitive. Must handle case where backend is already dead.

---

## R4 — High Risk: Postpone

| Item | Why postpone |
|------|-------------|
| Delta encoding for IPC WorldState | Major protocol change. Need benchmarks to justify. Current WorldState size is fine for 2 players. |
| Replace C# MsgPack with library | Touches every IPC parse path. Regression risk across all of Unity networking. |
| NAT traversal / relay server | Architecture-level change. Not needed for LAN development phase. |
| Host migration | Requires consensus protocol. Well beyond current scope. |
| Encryption / authentication | Needed for production, not for local dev. Design separately. |
| Entity component system | Architectural change to entity.rs. Current state machine works for 3 entity types. |
| Persistent save across sessions | `persistence/` module is scaffolded. Need to define what "save" means in multiplayer context. |

---

## Execution Order

If starting refactors tomorrow, this is the recommended order:

1. **R1-4** — Add WorldSync size test (proves the problem exists)
2. **R1-5** — Add concurrent interaction test (validates authority model)
3. **R1-6** — Add ID collision test (validates generator)
4. **R1-1** — Remove `Player.owned_chunks` (cleanup)
5. **R1-2** — Remove `Entity.respawn_timer` (cleanup)
6. **R1-3** — Remove legacy `interact`/`pickup` arm (cleanup)
7. **R2-6** — Centralize MPTRACE (improves debugging)
8. **R2-5** — Add item ID index (performance)
9. **R3-1** — Fragment WorldSync (P0 fix, on separate branch)
10. **R3-3** — Graceful disconnect (UX fix, on separate branch)
