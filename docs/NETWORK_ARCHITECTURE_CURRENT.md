# Network Architecture — Current State

## Overview

Each game client is a Unity application paired with a local Rust backend process. Clients do not communicate directly with each other — all network traffic flows through the local backends, which handle peer-to-peer UDP communication.

```
 +-----------------+         +-----------------+
 | Unity Client A  |         | Unity Client B  |
 +--------+--------+         +--------+--------+
          |                           |
          | TCP (IPC)                 | TCP (IPC)
          |                           |
 +--------+--------+         +--------+--------+
 | Rust Backend A  |<--UDP-->| Rust Backend B  |
 +-----------------+         +-----------------+
```

## Components

### Unity Client

- Game UI, rendering, input handling.
- Sends commands to its local backend via IPC (e.g., "host session", "join session").
- Receives world state from its local backend via IPC.
- **Does not** send or receive UDP traffic.
- **Does not** communicate directly with other Unity clients.

### Rust Backend (Local)

- One instance per client, runs as a separate process on the same machine.
- Binds a TCP port for IPC with its paired Unity client.
- Binds a UDP port for peer-to-peer communication with other backends.
- Maintains the game loop: processes ticks, builds world state, broadcasts to Unity via IPC.
- Manages the peer registry: tracks connected remote backends.

### IPC (TCP)

- Local TCP connection between Unity and its Rust backend.
- Carries JSON-encoded messages in both directions.
- Unity -> Backend: commands (host, join, player input).
- Backend -> Unity: world state snapshots (positions, remote players, game state).
- Each client uses a distinct IPC port to avoid conflicts when running multiple instances on one machine.

### UDP Peer-to-Peer

- Direct UDP communication between Rust backends.
- Carries handshake, heartbeat, and game state synchronization.
- Host backend binds on a known port (default: 7778).
- Joiner backend connects to host's `ip:port`.
- No relay server — backends talk directly.

## Identity Model

| Concept | Description |
|---------|-------------|
| `NET_ID` / `peer_id` | Logical identity assigned to each backend instance. Unique per session. |
| `player_id` | Game-level identity, currently maps 1:1 with `peer_id`. |
| IP address | Transport-level only. **Never used as player identity.** Multiple clients on localhost share the same IP but have distinct `peer_id` values. |

## Current Status

| Component | Status |
|-----------|--------|
| Rust backend | Compiles and runs |
| IPC (TCP) | Functional — Unity connects, sends commands, receives state |
| Host/Join UI | Functional — buttons trigger IPC commands |
| Build pipeline | `CopyReleaseBackendToBuild.ps1` copies server exe to Builds/ |
| UDP peer binding | Implemented — host binds, joiner connects |
| Handshake exchange | Implemented — under active development by Codex |
| Peer registry | Implemented — under active development by Codex |
| RemotePlayers in WorldState | Pending validation (RemotePlayers=1 gate) |
| Remote player spawning in Unity | Pending validation |

## What This Architecture Does NOT Include (Yet)

These are acknowledged future concerns, not current implementations:

- Dedicated/relay server
- Host migration
- Chunk streaming
- Persistence / save state
- NAT traversal / hole punching
- Encryption or authentication
