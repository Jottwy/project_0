# Stability Audit — Current State

> Date: 2026-06-08
> Scope: Read-only audit of backend (Rust) and client (Unity C#) networking code.
> No code was modified during this audit.

---

## 1. Stable Features Summary

### RemotePlayers = 1 (PASSED)

- Host and Joiner each see exactly 1 remote player.
- Handshake flow: Joiner sends `Handshake` -> Host registers peer, assigns ID, replies `HandshakeAck` -> Joiner registers Host as peer.
- `build_world_state()` in `game_loop.rs` reads `net.peers` and populates `WorldState.remote_players`.
- IPC serializes `remote_players` array via MessagePack.
- Unity `IPCClient.Dispatch()` parses `remote_players` into `WorldStateMsg.remotePlayers`.
- `RemotePlayerManager` spawns/updates `RemotePlayerView` objects per remote ID.
- Grace period of 3s (`missingRemoteGraceSeconds`) before despawning a missing remote.

### Transform Sync V0 (PASSED)

- Backend broadcasts `PlayerUpdate` (position, rotation, animation) at 10hz unreliable UDP.
- Receiving backend stores transform in `PeerConnection.update_player_state()`.
- `build_world_state()` reads peer positions and sends them to Unity via IPC.
- Unity `RemotePlayerManager.Update()` interpolates with exponential smoothing (`positionSmoothing=12`, `rotationSmoothing=10`).

### Shared World Sync V0 (PASSED)

- On peer connect, host sends `WorldSync` (reliable) with all chunks, entities, items.
- Joiner calls `world.apply_world_sync()` to replace local world.
- Host broadcasts `ChunkState` at 5hz unreliable for nearby chunks (within 3-chunk radius).
- Host and Joiner see the same world geometry, entities, and items.

---

## 2. Gates — Status

| Gate | Status | Evidence |
|------|--------|----------|
| Backend compiles | CLOSED | `cargo build --release` succeeds |
| IPC TCP functional | CLOSED | Unity connects, sends input, receives WorldState at 10hz |
| Host/Join UI functional | CLOSED | `NetworkInitializer.StartAsHost()` / `StartAsJoiner()` launch backend with correct env vars |
| Build copies backend | CLOSED | `CopyReleaseBackendToBuild.ps1` copies exe to `Builds\Backend\` |
| RemotePlayers = 1 | CLOSED | Both clients report `remote_players=1` |
| Transform Sync V0 | CLOSED | Remote capsules move in real-time |
| Shared World Sync V0 | CLOSED | Joiner receives and applies host world snapshot |
| World Interaction Authority V0 | OPEN | Under development by Codex |

---

## 3. Technical Risks

### R1. Peer Lifecycle — No Graceful Disconnect on App Close (P0)

**Location:** `NetworkManager` has `Disconnect` packet type defined, but Unity's `OnApplicationQuit` kills the backend process without sending a disconnect packet to peers.

**Impact:** The remote side relies on heartbeat timeout (5s) to detect disconnection. During those 5 seconds, the remote client still shows a ghost player at the last known position.

**Mitigation (future):** Send `Disconnect` packet before process kill. Not blocking for current gate.

### R2. Reliable Queue — No Flow Control Across Peers (P1)

**Location:** `peer.rs` line 72-88, `reliability.rs` `WINDOW_SIZE=32`.

**Details:** Each peer has an independent reliable queue capped at 32 packets. If a peer stops ACKing:
- After `MAX_RETRIES=5` with exponential backoff (200ms -> 1600ms), the queue is **cleared but the peer is NOT removed** (`process_retransmits` in `mod.rs` line 309-331).
- This is intentional (test at line 1123 confirms), but it means a peer that stops ACKing reliable packets will silently lose all reliable data (WorldSync, chunk transfers, anchors) while remaining "connected."

**Impact:** Data loss for reliable payloads without peer eviction. The peer stays alive because heartbeat (unreliable) may still arrive.

### R3. World Snapshot Size — Unbounded (P1)

**Location:** `sync.rs` `send_world_sync()` line 154-194.

**Details:** `send_world_sync` serializes ALL chunks (`world.chunks.values()`) into a single `WorldSync` reliable packet. With many chunks loaded, this payload can exceed UDP MTU (~1400 bytes) significantly. MessagePack + the 12-byte header go into a single `send_to()` call.

**Impact:** Large worlds will produce packets that exceed `MAX_PACKET_SIZE` (65535) or get fragmented at the IP level. IP fragmentation over UDP is unreliable — a single dropped fragment loses the entire snapshot.

**Note:** Currently safe with the small number of chunks loaded in V0 testing.

### R4. IPC Parser — No Schema Versioning (P1)

**Location:** `IPCMessages.cs`, `IPCClient.cs` `Dispatch()`.

**Details:** The C# parser uses hardcoded string keys (`"remote_players"`, `"visible_chunks"`, etc.). If the Rust backend adds, removes, or renames a field, the parser silently returns defaults (0, empty string, `Vector3.zero`) without any error.

**Impact:** A backend/client version mismatch silently produces incorrect game state instead of a clear error. Particularly dangerous for `remote_players` — a parse failure looks identical to "no remote players."

### R5. Entity/Item ID Uniqueness — Not Globally Guaranteed (P1)

**Location:** `protocol.rs` — `EntitySyncData.id` is `u32`, `ItemSyncData.id` is `u32`.

**Details:** Entity and item IDs are chunk-local. When syncing across peers, IDs from different chunks could collide. The current `ChunkSyncData` includes the chunk position as context, but the Unity-side `EntityViewMsg` and `ItemViewMsg` carry only the raw ID without chunk context.

**Impact:** If two chunks have an entity with `id=1`, Unity has no way to distinguish them in the flat `visible_entities` list. Could cause incorrect rendering or interaction targeting.

### R6. Local vs Remote Ownership — Implicit Only (P1)

**Location:** `game_loop.rs` line 112: `if net.is_host || net.peer_count() == 0`.

**Details:** Ownership logic (chunk updates, teleportation, entity ticking) runs only on the host or when solo. The joiner's game loop still runs `apply_movement`, `tick_entities`, and stat updates locally. There is no explicit ownership flag per entity or chunk that prevents the joiner from locally simulating entities that the host also simulates.

**Impact:** Both host and joiner run entity AI on the same entities. Since only the host broadcasts chunk states, the joiner's local simulation is overwritten at 5hz, but between updates, the joiner may see entities "jump" as its local simulation diverges then snaps back.

### R7. Cleanup/Despawn — RemotePlayerManager Grace Period Only (P2)

**Location:** `RemotePlayerManager.cs` line 126-140.

**Details:** Remote players are despawned after `missingRemoteGraceSeconds` (3s) of not appearing in WorldState. This is the only cleanup mechanism. There is no explicit "player left" handler that immediately removes the remote avatar — the `player_left` GameEvent is emitted but not consumed by `RemotePlayerManager`.

**Impact:** When a peer disconnects, the ghost avatar lingers for 3 seconds. Not blocking, but noticeable.

### R8. Logs — Very Verbose at Steady State (P2)

**Location:** Multiple files.

**Details:**
- `sync.rs` `broadcast_player_update()` logs every call at `info!` level (line 109-116), then conditionally logs MPTRACE every ~1s (line 117-127). At 10hz with 1 peer, this produces ~10 log lines/second just for player updates.
- `mod.rs` `PlayerUpdate` handler logs every received update at `info!` (line 501-503).
- `game_loop.rs` logs local transform every 60 ticks (~1/sec) (line 88-97).
- `RemotePlayerManager.cs` logs every 2 seconds for receive and update.
- `IPCClient.cs` logs parsed remote_players every 2 seconds.

**Impact:** Log files grow rapidly. In a longer session, this could cause performance issues on the IPC write path (stdout/stderr piping) and makes it harder to find real errors in the noise.

### R9. Build Backend Packaging — Manual Copy (P2)

**Location:** `tools/dev/CopyReleaseBackendToBuild.ps1`.

**Details:** The backend exe must be manually copied to `Builds\Backend\`. The Unity build process does not automatically include the backend. If a developer builds Unity and forgets to copy the backend, the build silently fails to find the server.

**Impact:** Developer friction. `NetworkInitializer.ResolveBackendPath()` has extensive fallback logic (12+ candidate paths) which mitigates this, but a clean build folder will still fail.

### R10. Heartbeat Timeout — 5s May Be Too Aggressive (P2)

**Location:** `peer.rs` line 12: `HEARTBEAT_TIMEOUT = 5s`. `game_loop.rs` sends heartbeat every 1s (tick 60).

**Details:** If the game loop stalls for >5 seconds (e.g., large world sync processing, GC pause, OS scheduling under load), the remote peer will be evicted.

**Impact:** Spurious disconnections under load. 5s is fine for LAN testing but may be too tight for real-world conditions.

---

## 4. Invariants — Must Not Break

These are design invariants that must hold across all future changes:

| # | Invariant | Rationale |
|---|-----------|-----------|
| I1 | **NET_ID is logical identity, never IP-based.** `PeerId` is a `u16` assigned by the host. Multiple clients on the same machine share an IP but have distinct `peer_id` values. | IP-based identity would break localhost testing and NAT scenarios. |
| I2 | **Host is authoritative for world state.** Only the host runs ownership updates, teleportation, and chunk generation. Joiner receives world via `WorldSync` and `ChunkState`. | Prevents world divergence between clients. |
| I3 | **Joiner does not generate a divergent world.** On connect, joiner calls `world.reset_for_remote_world()` then applies the host's `WorldSync`. | Two different worlds would cause desync in entity positions, items, and chunk layout. |
| I4 | **RemotePlayers count does not drop to 0 unless real disconnect.** A grace period (`missingRemoteGraceSeconds=3s`) and continuous WorldState streaming at 10hz ensure transient gaps don't cause despawn. | Flickering remote players would break the user experience. |
| I5 | **Remote player GameObjects have no local-only components.** `DisableLocalOnlyComponents()` disables `PlayerController`, `Camera`, `AudioListener`, and `PlayerInput` on remote avatars. | Two active cameras or audio listeners would break rendering and audio. |
| I6 | **IPC framing is 4-byte BE length prefix + MessagePack body.** Both sides (Rust `ipc::encode`/`decode` and C# `IPCClient.SendFrame`/`ReadFrames`) must agree on this wire format. | Any framing mismatch corrupts the entire stream (TCP is a byte stream, not a message stream). |
| I7 | **Peer ID allocation avoids 0 and the host's own ID.** `allocate_peer_id()` skips 0 and `self.local_id`, starting at 2. | ID 0 is used as "unassigned" sentinel in joiner startup. ID collision with host would corrupt the peers HashMap. |
| I8 | **Reliable packets get ACKed by the receiver.** `handle_packet()` sends ACK for any packet with `is_reliable(packet_type) && sequence > 0`. | Without ACK, the sender retransmits indefinitely until the peer is considered dead. |

---

## 5. Regression Checklist — Before Each Commit

Run these checks before committing any change to networking or IPC code:

- [ ] `cargo build --release` succeeds with no warnings in `network/`, `ipc/`, `game_loop.rs`
- [ ] `cargo test` passes all tests (especially `two_peers_handshake_and_sync`, `player_update_round_trip`, `reliable_packet_ack`, `peer_timeout_detection`)
- [ ] Unity project compiles without errors in `Assets/Scripts/Network/`
- [ ] `WorldStateMsg.Parse()` handles all fields the backend sends (check for new/renamed fields)
- [ ] `RemotePlayerMsg.Parse()` handles all fields in `RemotePlayerState` struct
- [ ] `DisableLocalOnlyComponents()` still disables Camera, AudioListener, PlayerController, PlayerInput on remote prefabs
- [ ] `HEARTBEAT_TIMEOUT` in `peer.rs` matches the heartbeat send interval in `game_loop.rs` (currently 5s timeout, 1s send)
- [ ] `missingRemoteGraceSeconds` in `RemotePlayerManager` is > 0 (currently 3s)
- [ ] No IP address is used as player identity anywhere in new code
- [ ] `allocate_peer_id()` still skips 0 and `self.local_id`

---

## 6. Two-Build Test Checklist

Use `tools/dev/RunTwoClientNetworkTest.ps1` to launch, then verify:

### Connection Phase
- [ ] Host backend prints `UDP bound on 0.0.0.0:7778`
- [ ] Joiner backend prints `Sending handshake to 127.0.0.1:7778`
- [ ] Host backend prints `Received handshake from ...`
- [ ] Host backend prints `Sending handshake ACK ...`
- [ ] Joiner backend prints `Handshake ACK received ...`
- [ ] Both print `Peer connected id=...`

### RemotePlayers Phase
- [ ] Host: `WorldState remote_players=1 ids=[<joiner_id>]`
- [ ] Joiner: `WorldState remote_players=1 ids=[1]`
- [ ] Unity Host: `[IPCClient] Parsed remote_players count=1`
- [ ] Unity Joiner: `[IPCClient] Parsed remote_players count=1`
- [ ] Unity Host: `[RemotePlayerManager] spawned id=<joiner_id>`
- [ ] Unity Joiner: `[RemotePlayerManager] spawned id=1`

### Transform Sync Phase
- [ ] Move Host player -> Joiner's remote capsule moves
- [ ] Move Joiner player -> Host's remote capsule moves
- [ ] Movement is smooth (no teleporting/snapping)

### World Sync Phase
- [ ] Joiner sees same chunks as Host
- [ ] Joiner sees same entities as Host
- [ ] Joiner sees same items as Host

### Disconnect Phase
- [ ] Close Joiner -> Host's remote capsule disappears within ~8s (5s timeout + 3s grace)
- [ ] Close Host -> Joiner's remote capsule disappears within ~8s
- [ ] No crashes or exceptions in either client

---

## 7. Technical Debt — Prioritized

### P0 — Would Break Multiplayer

| Item | Risk | Location | Notes |
|------|------|----------|-------|
| Reliable queue silent drop | Data loss for WorldSync/chunks if peer stops ACKing | `mod.rs` `process_retransmits` | Peer stays connected but loses all reliable data. Should at minimum log a warning visible to devs. |
| No WorldSync fragmentation | Large world breaks joiner connect | `sync.rs` `send_world_sync` | Single UDP packet for entire world. Fine now, breaks at scale. |
| Dual entity simulation | Host and joiner both tick entity AI | `game_loop.rs` entity tick | Causes visual jitter on joiner as local sim diverges from host broadcasts. |

### P1 — High Risk / Annoying

| Item | Risk | Location | Notes |
|------|------|----------|-------|
| No graceful disconnect packet | Ghost players for 5s on close | `NetworkInitializer.OnApplicationQuit` | Send `Disconnect` before killing backend process. |
| No IPC schema version check | Silent parse failures on version mismatch | `IPCClient.Dispatch`, `IPCMessages.cs` | Add a version handshake or schema hash on IPC connect. |
| Entity ID collision potential | Wrong entity targeted/rendered | `EntitySyncData.id` is chunk-local | Prefix with chunk coords or use globally unique IDs. |
| `RemotePlayerManager` ignores `player_left` event | Despawn delayed by grace period | `RemotePlayerManager.cs` | Subscribe to `player_left` event for immediate removal. |
| `PeerInfo.addr` exposed in protocol | IP leak to all peers | `protocol.rs` `PeerInfo`, `sync.rs` `build_peer_list` | Address should not be broadcast; use peer_id only. |

### P2 — Cleanup / Future

| Item | Risk | Location | Notes |
|------|------|----------|-------|
| Log verbosity at steady state | Log noise, potential perf impact | Multiple files | Reduce `info!` to `debug!` for per-frame updates. Keep MPTRACE at 1s intervals. |
| Manual backend copy to build | Dev friction | Build pipeline | Automate in Unity build post-process or CI. |
| Heartbeat timeout 5s | Spurious disconnects under load | `peer.rs` | Consider 10s for non-LAN, or adaptive timeout based on measured RTT. |
| `Nack` handler is a no-op | No negative acknowledgement recovery | `mod.rs` line 616-619 | Implement retransmit-on-NACK for faster reliable recovery. |
| `Ping` responds with same timestamp | No RTT smoothing | `mod.rs` line 623-627 | Currently just echoes; no latency measurement stored. Peer `latency_ms` is only updated from reliable ACK round-trip. |
| `ChunkState` treated as `ChunkTransfer` | No distinction in handling | `mod.rs` line 557-562 | May cause issues when chunk ownership semantics diverge. |
| `broadcast_channel` capacity = 64 | IPC write can lag | `main.rs` line 71 | At 10hz WorldState + events, 64 is fine now but monitor if event volume increases. |
| `ReadExactlyWithTimeout` busy-waits with `Thread.Sleep(1)` | CPU waste on slow connections | `IPCClient.cs` line 311-335 | Replace with `Socket.Poll` or async read. Fine for localhost IPC. |

---

## 8. Scope Reminder

This audit documents the current state. It does NOT propose:
- Dedicated server architecture
- NAT traversal / relay
- Host migration
- Authentication / encryption
- Chunk streaming protocol redesign
- Entity component system refactor

These are acknowledged future concerns listed in `docs/NETWORK_ARCHITECTURE_CURRENT.md`.
