# Architecture Risk Review

> Date: 2026-06-08
> Scope: Read-only audit of the full Rust backend + Unity C# client.
> No code was modified.

---

## 1. Current System State

| Feature | Status |
|---------|--------|
| RemotePlayers = 1 | Stable |
| Transform Sync V0 | Stable (10hz unreliable UDP) |
| Shared World Sync V0 | Stable (reliable WorldSync on join, 5hz chunk broadcast) |
| World Interaction Authority V0 | Implemented — host-authoritative item pickup |
| IPC (TCP MessagePack) | Stable |
| Host/Join UI | Functional |
| Backend build pipeline | Manual copy via PowerShell script |
| Entity AI | Functional (Idle/Alert/Aggro/Dead state machine) |
| Survival stats | Functional (hunger/thirst/sanity drain) |
| Crafting | Scaffolded (recipes defined, not wired to network) |
| Persistence | Scaffolded (save module exists, not active) |

---

## 2. Module Map

### Backend (Rust)

```
backend/src/
  main.rs                    — Entry point, env config, tokio runtime, task orchestration
  game_loop.rs               — 60hz authoritative loop: receive, simulate, network send, IPC broadcast
  ipc/
    mod.rs                   — Wire schema (ClientMessage, ServerMessage, WorldState, codec)
    server.rs                — TCP listener, per-connection read/write tasks
  network/
    mod.rs                   — NetworkManager: UDP socket, peer map, handshake, packet dispatch
    peer.rs                  — PeerConnection: state, reliable queue, heartbeat, player transform
    protocol.rs              — Packet header (12-byte), PacketPayload enum, encode/decode
    reliability.rs           — Constants: backoff schedule, window size, ACK deadline
    sync.rs                  — Broadcast helpers: player update, chunk state, world sync, peer list
  world/
    mod.rs                   — World struct: chunks HashMap, ownership, sync apply, interact_with_item
    chunk.rs                 — Chunk struct, ChunkState enum, DroppedItem
    entity.rs                — Entity AI: type, state machine, damage, wander, aggro
    generator.rs             — Deterministic chunk gen: templates, entities, items, structures
  player/
    mod.rs                   — Re-export
    session.rs               — Player struct: id, position, inventory, stats
    inventory.rs             — Item enum, Inventory (20 slots, stacking)
    stats.rs                 — PlayerStats: hunger/thirst/sanity, decay, modifiers
  crafting/
    mod.rs                   — Recipe definitions (scaffolded)
    recipes.rs               — Recipe list (scaffolded)
  persistence/
    mod.rs                   — Save/load (scaffolded)
    save.rs                  — Serialization (scaffolded)
  utils/
    mod.rs                   — Vec3, ChunkPos, coordinate math
```

### Client (Unity C#)

```
Assets/Scripts/
  Network/
    IPCClient.cs             — TCP client: background thread, frame read/write, MessagePack dispatch
    IPCMessages.cs           — Wire types: WorldStateMsg, RemotePlayerMsg, parsers, IPCParse helpers
    MsgPack.cs               — Manual MessagePack reader/writer
    NetworkInitializer.cs    — Backend process launcher, env config, port allocation
    PortUtility.cs           — TCP/UDP port availability checks
    RemotePlayerManager.cs   — Remote avatar lifecycle: spawn, interpolate, despawn, pool
  Gameplay/
    GameBootstrap.cs         — EnsureComponent for all managers
    PlayerController.cs      — Local player movement/camera
    ChunkRenderer.cs         — Chunk visualization from WorldState
    EntityRenderer.cs        — Entity visualization from WorldState
    ItemRenderer.cs          — Item visualization from WorldState
    WorldInteractor.cs       — E-key raycast -> SendWorldInteractRequest
    NetworkWorldObject.cs    — Tag component: id + kind + active
    SanityEffects.cs         — Visual effects based on sanity stat
    TeleportationVFX.cs      — Chunk teleport visual effects
    MaterialHelper.cs        — Safe material creation (avoids magenta in builds)
  UI/
    JoinSessionUI.cs         — Host/Join menu, auto-solo, cursor management
    HUD.cs                   — HUD root
    HUDUpdater.cs            — Stats display from WorldState
    MinimapRenderer.cs       — Minimap visualization
```

---

## 3. Responsibilities by Module

| Module | Responsibility | Concern if violated |
|--------|---------------|---------------------|
| `game_loop.rs` | Sole owner of game simulation tick order | Dual simulation = desync |
| `NetworkManager` | Sole owner of UDP socket, peer map, handshake state machine | Peer identity corruption |
| `World` | Sole owner of chunk/entity/item state | State divergence between host/joiner |
| `IPCClient` | Sole bridge Unity<->Backend | Data loss if framing breaks |
| `RemotePlayerManager` | Sole owner of remote avatar GameObjects | Ghost/duplicate avatars |
| `NetworkInitializer` | Sole launcher of backend process | Port collisions, orphan processes |
| `JoinSessionUI` | Sole owner of session UI state machine | UI stuck in wrong state |
| `WorldInteractor` | Sole sender of `world_interact` actions | Duplicate/unvalidated interactions |

---

## 4. Invariants That Must Not Break

| # | Invariant | Where enforced | Breakage consequence |
|---|-----------|----------------|---------------------|
| I1 | NET_ID is logical, never IP | `allocate_peer_id()`, peer map keyed by `PeerId` | Multiple localhost clients indistinguishable |
| I2 | Host is world authority | `game_loop.rs` line 112: ownership/teleport only if `is_host \|\| peer_count==0` | World divergence |
| I3 | Joiner resets world on connect | `reset_for_remote_world()` + `apply_world_sync()` | Joiner sees stale local world |
| I4 | RemotePlayers count reflects real peer state | `build_world_state()` reads `net.peers` directly | False presence/absence |
| I5 | Remote avatars have no local components | `DisableLocalOnlyComponents()` | Dual cameras, input conflicts |
| I6 | IPC framing: 4-byte BE length + msgpack | Both sides must agree | Entire TCP stream corrupted |
| I7 | Peer ID 0 never assigned | `allocate_peer_id()` skips 0, host=1, joiners>=2 | Sentinel collision |
| I8 | Reliable packets always ACKed | `handle_packet()` sends ACK for `is_reliable()` types | Infinite retransmit, peer eviction |
| I9 | World revision increments on mutation | `interact_with_item()`, teleport, apply_sync | Stale-state detection fails |
| I10 | Item pickup is host-authoritative | `world_interact` -> host processes, broadcasts result | Duplicate pickups, inventory desync |
| I11 | Entity AI ticks on host only (effective) | Host broadcasts ChunkState at 5hz overwriting joiner local | If broadcast stops, joiner diverges |

---

## 5. Risks — Prioritized

### P0 — Would Break Multiplayer

| ID | Risk | Location | Detail |
|----|------|----------|--------|
| P0-1 | **WorldSync is a single unbounded UDP packet** | `sync.rs:send_world_sync()` | All chunks serialized into one `send_reliable()`. With 121 chunks loaded (ownership radius=5), the MessagePack payload easily exceeds UDP MTU. IP fragmentation is unreliable over UDP — a single lost fragment drops the entire joiner connection flow. |
| P0-2 | **Dual entity simulation on host + joiner** | `game_loop.rs:tick_entities()` runs unconditionally | Both host and joiner run entity AI at 10hz. Host broadcasts chunk state at 5hz, overwriting joiner's local sim. Between broadcasts, joiner entities visually "jump" back. With more entities or slower networks, this becomes gameplay-breaking jitter. |
| P0-3 | **Reliable queue exhaustion silently drops data, keeps peer** | `mod.rs:process_retransmits()` | After 5 retries, the reliable queue is cleared but the peer stays connected. The peer will never receive the WorldSync or chunk transfer that was dropped. No recovery path exists — the peer's world state is permanently stale. |
| P0-4 | **World interaction forwarding — joiner actions during host unreachable** | `game_loop.rs:handle_action_from_client/peer` | If the joiner's `world_interact` IPC action arrives at the joiner's local backend but the joiner is temporarily disconnected from host, the action is processed locally (joiner is not host, so `interact_with_item` won't execute). However, there's no queuing or retry — the action is silently lost. |

### P1 — High Risk / Annoying

| ID | Risk | Location | Detail |
|----|------|----------|--------|
| P1-1 | **No graceful disconnect** | `NetworkInitializer.OnApplicationQuit` kills process | Remote side waits 5s heartbeat timeout. Ghost player visible during that window. |
| P1-2 | **Entity/item IDs: stable but collision-prone across chunks** | `stable_entity_id()` / `stable_item_id()` | Hash function `stable_u32()` uses world_seed + chunk_pos + index. Two chunks with same (seed XOR salt) and same index will collide. `interact_with_item()` searches all chunks by flat ID — first match wins. A collision means the wrong item is picked up. |
| P1-3 | **No IPC schema versioning** | `IPCClient.Dispatch()`, `IPCMessages.cs` | Backend adds a field → C# silently returns default (0, empty). Backend removes a field → same. No error, no warning. Particularly dangerous for `remote_players` where parse failure looks like "0 remote players." |
| P1-4 | **`RemotePlayerManager` ignores `player_left` event** | `RemotePlayerManager.cs` | Relies solely on 3s grace period. The `player_left` GameEvent is emitted by `game_loop.rs` but never consumed for immediate despawn. |
| P1-5 | **`PeerInfo.addr` broadcast to all peers** | `build_peer_list()` in `sync.rs` | Every peer receives the IP:port of every other peer. Privacy leak; also leaks internal port allocation. |
| P1-6 | **`broadcast_channel` capacity 64 may lag under event bursts** | `main.rs` line 71 | At 10hz WorldState + game events + action results, heavy combat could exceed 64 queued messages. IPC write loop logs "lagged, skipped N messages" and drops state — joiner loses world updates. |
| P1-7 | **Heartbeat timeout 5s vs. WorldSync processing time** | `peer.rs` HEARTBEAT_TIMEOUT=5s | If `apply_world_sync()` takes >5s (large world), the remote peer sends no heartbeat during processing, and the host evicts it mid-sync. |

### P2 — Cleanup / Future

| ID | Risk | Location | Detail |
|----|------|----------|--------|
| P2-1 | Log verbosity at steady state | Multiple files | ~10+ info lines/sec per peer for player updates alone. |
| P2-2 | Manual backend copy to build | Build pipeline | No automation; easy to forget. |
| P2-3 | `Nack` handler is a no-op | `mod.rs` line 616 | Defined in protocol but never triggers retransmit. |
| P2-4 | `Ping` response doesn't store RTT | `mod.rs` line 623 | `latency_ms` only updated from reliable ACK, not from Ping. |
| P2-5 | `ChunkState` treated as `ChunkTransfer` | `mod.rs` line 557 | Different semantics conflated — ownership vs. observation. |
| P2-6 | `respawn_timer` field on Entity unused | `entity.rs` line 110 | `Option<f32>` always `None`; respawn is handled by `World.respawn_queue`. Dead field. |
| P2-7 | `Player.owned_chunks` never populated | `session.rs` line 23 | `Vec<ChunkPos>` declared but never written to. Chunk ownership tracked in `Chunk.owner` instead. |
| P2-8 | `IPCClient.ReadExactlyWithTimeout` busy-polls | `IPCClient.cs` line 311 | `Thread.Sleep(1)` loop. Fine for localhost, wasteful for higher latency. |
| P2-9 | `GameBootstrap.EnsureComponent` uses `FindFirstObjectByType` | `GameBootstrap.cs` | O(n) scene scan per component type on every Awake. Fine for one-time init. |

---

## 6. Redundancies Detected

| # | What | Where | Detail |
|---|------|-------|--------|
| RD-1 | **Dual entity ID systems** | `next_entity_id()` (atomic counter) AND `stable_entity_id()` (deterministic hash) | `next_entity_id()` is used by `tick_respawns()`. `stable_entity_id()` is used by `generate_chunk()` and structure generation. Both produce `u32` IDs that occupy the same ID space. A respawned entity could collide with a generated entity's stable ID. |
| RD-2 | **`Player.owned_chunks` vs. `Chunk.owner`** | `session.rs` + `chunk.rs` | Two representations of chunk ownership. `Player.owned_chunks` is never written. If someone starts using it, it will diverge from `Chunk.owner`. |
| RD-3 | **`Entity.respawn_timer` vs. `World.respawn_queue`** | `entity.rs` + `world/mod.rs` | Entity has a `respawn_timer: Option<f32>` field that is never set. Respawning is tracked externally in `World.respawn_queue`. Two mechanisms for the same concept. |
| RD-4 | **Legacy `interact`/`pickup` action still handled** | `game_loop.rs` line 477 | Old local pickup code exists alongside new `world_interact` host-authority path. The old path logs "legacy local pickup ignored" but still pattern-matches. Dead code that could confuse future developers. |
| RD-5 | **`StructureV0` and `generate_initial_structures()` unused at runtime** | `generator.rs` | Structures are defined and can be generated, but `World::new()` and `update_ownership()` call `generate_chunk()` directly, not `generate_structure_chunk()`. The structure system is scaffolded but not integrated. |
| RD-6 | **Duplicate handshake ACK code** | `mod.rs` `handle_handshake()` | Three nearly identical blocks send HandshakeAck: (1) duplicate by sender_id, (2) duplicate by endpoint, (3) new peer. Each constructs the same `PacketPayload::HandshakeAck` with the same fields. ~50 lines of copy-paste. |

---

## 7. Deuda Técnica (Technical Debt)

| # | Debt | Impact | Effort to fix |
|---|------|--------|---------------|
| TD-1 | No test for WorldSync packet size exceeding MTU | Silent failure at scale | Medium — add test that serializes 121 chunks and asserts size |
| TD-2 | No integration test for world_interact flow | Regression risk on interaction changes | Medium — test: send action → verify item removed, revision incremented, response sent |
| TD-3 | No test for peer timeout during WorldSync processing | Spurious disconnect bug | Low — test with artificial delay |
| TD-4 | No test for broadcast_channel lag behavior | Silent data loss | Low — fill channel, verify behavior |
| TD-5 | `spawn_entities` / `spawn_resources` signatures take `world_seed` param | Passed through to `stable_*_id()` — functional but adds coupling | Low — could derive from chunk seed |
| TD-6 | MPTRACE logging is ad-hoc with inline format strings | Hard to parse programmatically, easy to break | Medium — centralize trace emission |
| TD-7 | C# MessagePack parser is hand-rolled (`MsgPackReader`/`MsgPackWriter`) | No schema validation, hard to extend | High — replacing with library is large change |
| TD-8 | `JoinSessionUI` builds entire UI programmatically (~300 lines) | Fragile, hard to iterate on visually | Low priority — works, just verbose |

---

## 8. Scalability Bottlenecks

| # | Bottleneck | Current behavior | Breaking point |
|---|-----------|-----------------|----------------|
| SB-1 | **WorldSync packet size** | All 121 chunks in one reliable UDP packet | >50 chunks with entities → exceeds 65KB `MAX_PACKET_SIZE` |
| SB-2 | **`interact_with_item` linear scan** | Iterates all chunks × all items to find target ID | 1000+ items → noticeable latency per interaction |
| SB-3 | **`visible_entity_views()` / `visible_item_views()` collect all** | Flat `Vec` of all entities/items in all loaded chunks | 121 chunks × 5 entities = 605 entity views per IPC frame at 10hz |
| SB-4 | **IPC WorldState at 10hz with full entity/item lists** | Every frame sends all visible chunks, entities, items | Bandwidth grows linearly with world size; no delta encoding |
| SB-5 | **`broadcast_unreliable` sends to each peer sequentially** | `for peer in peers { send_to }` | 50 peers × 10hz = 500 UDP sends/sec just for player updates |
| SB-6 | **Entity AI ticks all entities in all loaded chunks** | 121 chunks × 3-5 entities = ~500 AI ticks at 10hz | With more entity types or complex behavior, this dominates the game loop |
| SB-7 | **Reliable retransmit checks all peers every 100ms** | `process_retransmits()` iterates all peers | 50 peers × 32 reliable queue each = 1600 packet checks per tick |

---

## 9. Security / Local Network Risks

| # | Risk | Detail |
|---|------|--------|
| SEC-1 | **No packet authentication** | Any process on the LAN can forge UDP packets with any `sender_id`. A malicious packet with `sender_id=1` (host) could inject world state. |
| SEC-2 | **No rate limiting on handshake** | A flood of Handshake packets from different source addresses would allocate peer IDs until `u16` wraps. Each allocation creates a `PeerConnection` in memory. |
| SEC-3 | **`PeerInfo.addr` leaks internal topology** | All peers see each other's IP:port. In a LAN/office setting, this reveals machine identity. |
| SEC-4 | **IPC on 127.0.0.1 without auth** | Any local process can connect to the IPC port and inject `ClientMessage`s. Fine for single-player dev; risky if the port is accidentally exposed. |
| SEC-5 | **`MAX_FRAME_BYTES = 16MB` for IPC** | A malicious local process could send a 16MB frame, causing the backend to allocate 16MB for a single message. |
| SEC-6 | **No validation of `world_interact` player position** | The `player_position` sent by the client is trusted for distance checks. A modified client can claim any position to interact with distant items. |

---

## 10. Missing Tests

| # | Test needed | Module | Priority |
|---|------------|--------|----------|
| T1 | WorldSync payload size with 121 chunks | `sync.rs` / `protocol.rs` | P0 |
| T2 | `interact_with_item` concurrent double-pickup (two peers, same item) | `world/mod.rs` | P0 |
| T3 | Peer timeout during long `apply_world_sync` | `mod.rs` + `peer.rs` | P1 |
| T4 | `broadcast_channel` overflow behavior | `main.rs` / `game_loop.rs` | P1 |
| T5 | `stable_entity_id` collision probability across 121 chunks | `generator.rs` | P1 |
| T6 | Handshake flood: 1000 rapid handshakes from different addresses | `mod.rs` | P1 |
| T7 | `RemotePlayerManager` despawn on `player_left` event (currently untested because unimplemented) | `RemotePlayerManager.cs` | P1 |
| T8 | IPC frame with length=0, length=MAX, length=MAX+1 | `ipc/server.rs` | P2 |
| T9 | `allocate_peer_id` wrapping at u16::MAX | `mod.rs` | P2 |
| T10 | `apply_world_sync` with 0 chunks (empty world) | `world/mod.rs` | P2 |
| T11 | `world_interact` with `target_kind="entity"` (not yet implemented but protocol allows) | `game_loop.rs` | P2 |
| T12 | Reliable packet delivery under simulated packet loss | `mod.rs` + `peer.rs` | P2 |

---

## 11. Recommended Refactors (Ordered by Priority)

| # | Refactor | Risk | Priority | Detail |
|---|----------|------|----------|--------|
| RF-1 | **Extract HandshakeAck construction into helper** | None | R1 | Three copy-pasted blocks in `handle_handshake()`. Extract `fn build_handshake_ack(&self) -> PacketPayload`. |
| RF-2 | **Remove `Player.owned_chunks` field** | None | R1 | Dead field, never written. Removing prevents future confusion. |
| RF-3 | **Remove `Entity.respawn_timer` field** | None | R1 | Dead field, never set. Respawn tracked by `World.respawn_queue`. |
| RF-4 | **Remove legacy `interact`/`pickup` match arm** | None | R1 | Already logs "legacy local pickup ignored". Dead code. |
| RF-5 | **Add WorldSync size assertion test** | None | R1 | Test that `send_world_sync` payload stays within bounds. Detects regression early. |
| RF-6 | **Subscribe `RemotePlayerManager` to `player_left` event** | Low | R2 | Immediate despawn instead of 3s grace. Small change, big UX improvement. |
| RF-7 | **Guard `tick_entities` with `is_host` check** | Low | R2 | Prevents dual simulation. Joiner receives entity state from host via ChunkState. |
| RF-8 | **Fragment WorldSync into per-chunk reliable packets** | Medium | R3 | Prevents MTU overflow. Requires new assembly logic on receiver. Separate branch. |
| RF-9 | **Add `HashMap<u32, ChunkPos>` index for item ID lookups** | Low | R2 | O(1) item lookup in `interact_with_item` instead of O(chunks × items). |
| RF-10 | **Centralize MPTRACE emission into a helper** | Low | R2 | Single `fn mptrace(step, event, fields...)` reduces inline format strings. |
| RF-11 | ~~**Add IPC version handshake**~~ **HECHO (ADR-061, v26)** | Medium | R3 | `ServerMessage::Hello { schema_version }` como primer frame de cada conexión IPC; Unity compara contra `WireSchema.Expected` por igualdad exacta y un mismatch cae por la ruta `session_ended` de ADR-056 con `reason=wire_schema_mismatch`. **No cubre el P2P**: el `version` del `Handshake` sigue ignorado (corrección pendiente de ADR-060). |
| RF-12 | **Remove `PeerInfo.addr` from broadcast** | Low | R2 | Replace with just peer_id in PeerList. Addresses are transport-internal. |
| RF-13 | **Add delta encoding to WorldState IPC** | High | R4 | Only send changed entities/items. Major protocol change, postpone. |
| RF-14 | **Replace hand-rolled MsgPack with library** | High | R4 | Large change, high regression risk, currently works. |
