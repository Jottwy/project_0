# RemotePlayers=1 Gate

## Objective

Validate that two clients running on localhost can see each other as remote players. The gate is passed when **both** clients report:

```
RemotePlayers = 1
```

This means:
- Host sees 1 remote player (the joiner).
- Joiner sees 1 remote player (the host).

No gameplay, persistence, or visual features are required — only network identity and presence.

---

## Architecture

```
 Client A (Unity)                              Client B (Unity)
      |                                              |
      | TCP (IPC)                                    | TCP (IPC)
      v                                              v
 Backend A (Rust, local)  <--- UDP/P2P --->  Backend B (Rust, local)
```

Each Unity client spawns its own local Rust backend process. The backends communicate directly via UDP. Unity never talks UDP — it only communicates with its local backend over TCP (IPC).

---

## Port and Identity Reference

| Concept | Variable | Description |
|---------|----------|-------------|
| **IPC_PORT** | `IPC_PORT` env / config | TCP port for Unity <-> local backend communication. Each client uses its own IPC port (e.g., 9001 for Host, 9002 for Joiner). |
| **NET_PORT** | `NET_PORT` / `--port` | Local UDP port the backend binds for peer-to-peer traffic. Default: 7778. |
| **NET_ID** | `NET_ID` / `peer_id` | Logical identity of this backend instance in the network. NOT an IP address. Used to identify the player across the session. |
| **CONNECT_TO** | `CONNECT_TO` / `--connect` | Address (`ip:port`) of the remote backend to connect to. The Joiner sets this to the Host's `ip:NET_PORT`. |

**Important:** Player identity is determined by `NET_ID` / `peer_id`, never by IP address. Multiple clients on the same machine will share an IP but have distinct identities.

---

## Expected Log Sequence

When the gate passes correctly, logs appear in this order:

### Host backend (after clicking Host Session)
```
UDP bound on 0.0.0.0:7778
```

### Joiner backend (after clicking Join Session)
```
UDP bound on 0.0.0.0:<joiner_port>
Sending handshake to 127.0.0.1:7778
```

### Host backend (receives joiner)
```
Received handshake from 127.0.0.1:<joiner_port>
Sending handshake ACK to 127.0.0.1:<joiner_port>
Peer connected id=<joiner_net_id>
```

### Joiner backend (receives ACK)
```
Handshake ACK received from 127.0.0.1:7778
Peer connected id=<host_net_id>
```

### Both backends (world state broadcast)
```
WorldState remote_players=1 ids=[<remote_id>]
```

### Both Unity clients (IPC parse)
```
Parsed remote_players count=1 ids=[<remote_id>]
RemotePlayerManager spawned id=<remote_id>
RemotePlayerManager updated id=<remote_id>
```

---

## Diagnostic Checklist

If `RemotePlayers` stays at 0, walk through each step to find where the chain breaks:

| Step | Check | What to look for |
|------|-------|-------------------|
| **A** | Joiner sends handshake | Log: `Sending handshake to ...` in joiner backend. If missing: backend didn't receive join command via IPC, or `CONNECT_TO` is not set. |
| **B** | Host receives handshake | Log: `Received handshake from ...` in host backend. If missing: UDP packet not arriving — check firewall, port mismatch, or host not bound. |
| **C** | Host registers peer | Log: `Peer connected id=...` in host backend. If missing: handshake validation failed, or peer registry logic has a bug. |
| **D** | Host responds ACK | Log: `Sending handshake ACK ...` in host backend. If missing: peer was registered but ACK send was skipped. |
| **E** | Joiner receives ACK | Log: `Handshake ACK received ...` in joiner backend. If missing: ACK UDP packet lost, or joiner port not reachable from host. |
| **F** | Joiner registers host | Log: `Peer connected id=...` in joiner backend. If missing: ACK processing or peer registry bug on joiner side. |
| **G** | WorldState includes remote_players | Log: `WorldState remote_players=1`. If missing: game loop not reading peer registry, or world state builder skips peers. |
| **H** | IPC serializes remote_players | Check IPC TCP traffic or logs for `remote_players` field in the JSON payload. If missing: IPC serialization doesn't include the field. |
| **I** | Unity parses remote_players | Log: `Parsed remote_players count=1`. If missing: C# IPC client not parsing the `remote_players` field from the JSON. |
| **J** | RemotePlayerManager spawns remote | Log: `RemotePlayerManager spawned id=...`. If missing: manager not receiving parsed data, or spawn logic has a condition bug. |

### Quick checks

- **Firewall**: Windows Firewall may block UDP. Run `netsh advfirewall firewall add rule name="Backrooms UDP" dir=in action=allow protocol=UDP localport=7778` if needed.
- **Port conflict**: Ensure nothing else is using port 7778: `netstat -ano | findstr 7778`.
- **IPC connection**: Verify Unity connects to backend IPC: look for `IPC connected` in Unity console.
- **Backend running**: Verify `backrooms_server.exe` is running: `tasklist | findstr backrooms_server`.

---

## Test Script

Use the automated test launcher:

```powershell
.\tools\dev\RunTwoClientNetworkTest.ps1
```

This kills old processes, copies the latest backend build, and launches two game clients with instructions.
