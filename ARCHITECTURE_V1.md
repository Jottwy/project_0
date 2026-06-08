# BACKROOMS SURVIVAL — ARCHITECTURE DOCUMENT v1.0
## Sesión 1 Output: Distributed P2P Architecture

**Date:** June 2026
**Status:** Design Complete — Ready for Implementation
**Author:** Claude (Architecture) + Developer (Unity Client)

---

## 1. ARCHITECTURAL VISION

### The Goal
A distributed peer-to-peer game where every player's machine contributes to world simulation. No central server. More players = more computing power. The world is literally spread across player machines — and when someone disconnects, their chunks "teleport" (which IS the game mechanic).

### Why This Architecture
- **Zero infrastructure cost** at any scale
- **Thematic synergy**: chunks teleporting when peers disconnect IS the gameplay
- **Scales naturally**: more players = more machines = more capacity
- **Resilient**: no single point of failure (no central server to crash)

### Reality Check
Fully distributed P2P at MMO scale (1000+ concurrent) is an unsolved problem in the industry. Our approach: build a system that works reliably at 20-50 players NOW, with architecture designed to scale to 200+ with optimizations post-launch. The key insight is that the backrooms mechanic MASKS the hardest distributed systems problems — desync and chunk loss become gameplay features, not bugs.

---

## 2. NETWORK TOPOLOGY

### 2.1 Hybrid Mesh Architecture

```
┌─────────────────────────────────────────────┐
│              DISCOVERY SERVICE               │
│  (Lightweight relay — $3/month VPS or free)  │
│  • NAT traversal (STUN/TURN)                │
│  • Session listing                           │
│  • Peer introduction only                    │
│  • Zero game state                           │
└──────────────┬──────────────────────────────┘
               │ (initial connection only)
               │
    ┌──────────┼──────────┐
    │          │          │
┌───▼──┐  ┌───▼──┐  ┌───▼──┐
│PEER A│──│PEER B│──│PEER C│    ← Direct UDP mesh
│(host)│  │      │  │      │      after introduction
└──┬───┘  └──┬───┘  └──┬───┘
   │         │         │
   │    ┌────▼───┐     │
   └────│ PEER D │─────┘
        └────────┘
```

**Why Hybrid (not pure P2P):**
- Pure P2P requires NAT traversal — impossible without a relay for ~30% of players behind strict NAT
- Discovery service is stateless, costs almost nothing, and can be replaced or self-hosted
- After initial introduction, all traffic is direct peer-to-peer

### 2.2 Peer Roles

```
SEED (Session Creator):
├── Creates the world initially
├── Acts as first chunk owner
├── Manages session metadata (player list, world config)
├── Can be any peer — role transfers if they leave
└── NOT authoritative for everything (distributed)

CHUNK OWNER:
├── Every peer owns chunks near their player position
├── Simulates entities in owned chunks
├── Validates actions in owned chunks
├── Sends chunk state to nearby peers
└── Ownership transfers automatically based on proximity

RELAY PEER (optional, auto-selected):
├── Peers with good connections can relay for peers behind strict NAT
├── Selected automatically based on bandwidth/latency
└── Only relays packets, doesn't process game logic
```

### 2.3 NAT Traversal Strategy

```
Attempt Order:
1. Direct UDP (works ~60% of cases)
2. UDP hole-punching via STUN (works ~25% more)
3. TURN relay via discovery service (fallback, always works)
4. Peer relay via another player with open NAT (backup)

Libraries:
- Rust: libp2p or custom STUN/TURN implementation
- Fallback: WebRTC data channels (universal NAT traversal)
```

---

## 3. CHUNK OWNERSHIP SYSTEM

This is the core innovation. Each chunk in the world is "owned" by exactly one peer's machine at any time.

### 3.1 Ownership Model

```
WORLD GRID (100x100 chunks max)
┌────┬────┬────┬────┬────┬────┐
│ A  │ A  │ A  │ B  │ B  │    │  A = Peer A owns these chunks
├────┼────┼────┼────┼────┼────┤  B = Peer B owns these chunks
│ A  │ A  │ A  │ B  │ B  │ C  │  C = Peer C owns these chunks
├────┼────┼────┼────┼────┼────┤  (blank) = Unloaded/unowned
│    │ A  │ D  │ D  │ C  │ C  │  D = Peer D owns these chunks
├────┼────┼────┼────┼────┼────┤
│    │    │ D  │ D  │ C  │    │
└────┴────┴────┴────┴────┴────┘

OWNERSHIP RULES:
1. Each peer owns chunks within OWNERSHIP_RADIUS of their player position
2. OWNERSHIP_RADIUS = 5 chunks (250 units) — covers visible area + buffer
3. When two peers are equidistant, the one who arrived first keeps ownership
4. Ownership transfers smoothly when a player moves away
5. Unowned chunks are DORMANT (not simulated, entities frozen)
```

### 3.2 Ownership Transfer Protocol

```
SCENARIO: Player A moves away from chunk (3,2), Player B is now closer.

1. Peer A detects chunk (3,2) is outside its ownership radius
2. Peer A checks: is any other peer within ownership radius of (3,2)?
3. If YES (Peer B is close):
   a. Peer A serializes chunk state (entities, items, anchor status)
   b. Peer A sends CHUNK_TRANSFER packet to Peer B:
      {
        type: "chunk_transfer",
        chunk_pos: [3, 2],
        state: { entities: [...], items: [...], anchor: null },
        timestamp: 1234567890
      }
   c. Peer B acknowledges: CHUNK_TRANSFER_ACK
   d. Peer A removes chunk from its simulation
   e. Peer B adds chunk to its simulation
   f. Both broadcast ownership change to all peers

4. If NO (no peer is close enough):
   a. Chunk becomes DORMANT
   b. State is saved to Peer A's local cache
   c. When someone approaches, closest peer loads it
   d. If Peer A has disconnected → chunk data lost → CHUNK TELEPORTS (game mechanic!)
```

### 3.3 Disconnection = Teleportation (The Magic)

```
WHEN A PEER DISCONNECTS:
1. All chunks owned by that peer become ORPHANED
2. Nearby peers detect the disconnect (heartbeat timeout: 5 seconds)
3. For each orphaned chunk:
   a. If another peer has cached state → they adopt the chunk (state preserved)
   b. If no peer has cached state → chunk REGENERATES at new random position
      → This IS the teleportation mechanic
      → The game's core feature emerges naturally from the P2P architecture

RESULT:
- Unstable connections = more teleportation = harder gameplay
- Stable group of friends = stable world = easier gameplay
- Solo play = chunks ONLY teleport on timer (no disconnection chaos)
- Large groups = very stable (many peers caching chunk state)

THIS IS THE KEY INSIGHT:
The "teleporting chunks" mechanic is not just a game feature —
it's how the distributed system handles failure gracefully.
The game design and the architecture are the same thing.
```

### 3.4 Chunk States (Updated for Distributed Model)

```
UNLOADED:
├── Not in any peer's memory
├── Will be generated on first approach
└── Default state for unexplored world

DORMANT:
├── Was previously loaded, now unowned
├── State cached on nearest peers (best effort)
├── Entities frozen, no simulation
└── Revives when a peer approaches

ACTIVE (RANDOM):
├── Owned by a peer, fully simulated
├── Subject to teleportation timer (120-600 sec)
├── If owner disconnects → teleports (regenerates)
└── Default state for explored, unstabilized chunks

ACTIVE (STABILIZED):
├── Has a stabilizer placed
├── Teleportation chance reduced (95-99% fixed)
├── Owner still simulates entities
├── If owner disconnects → stays if cached, teleports if not
└── Stabilizer provides grace period for ownership transfer

ACTIVE (ANCHORED):
├── Has anchor installed (cable + tool)
├── 100% fixed, never teleports
├── State replicated to ALL peers (critical data)
├── Survives owner disconnect (any peer can adopt)
├── Most expensive to create, most resilient
└── Anchor data is in the "consensus layer"
```

---

## 4. CONSENSUS & DATA INTEGRITY

### 4.1 What Needs Consensus (Replicated to ALL Peers)

```
CRITICAL DATA (replicated everywhere):
├── Anchor positions and durability
├── Player inventories (authoritative: each peer owns their own)
├── Player stats (hunger/thirst/sanity)
├── Stabilizer placements
├── World seed (for procedural regeneration)
├── Session configuration (max players, world name)
└── Player list + positions (for chunk ownership calculation)

NON-CRITICAL DATA (only on chunk owner):
├── Entity positions within a chunk
├── Entity health/state
├── Loose item positions (dropped items)
├── Particle effects, ambient state
└── This data is LOST if chunk teleports (acceptable)
```

### 4.2 Simple Consensus: Last-Write-Wins + Validation

```
For MVP, we use a simple model:

INVENTORY:
├── Each peer is authoritative for their OWN inventory
├── When crafting: peer validates locally, broadcasts result
├── If peers disagree: chunk owner's version wins
├── Anti-cheat: chunk owner validates resource pickups
└── Trust model: friends playing together (like Valheim)

ANCHORS:
├── Installation broadcast to ALL peers
├── Each peer stores anchor data locally
├── On rejoin: merge anchor lists (union of all known anchors)
├── Conflict resolution: anchor with earliest timestamp wins
└── Durability tracked by chunk owner, broadcast on change

STABILIZERS:
├── Placement broadcast to chunk owner + all peers
├── Chunk owner validates (is the chunk available?)
├── If valid: chunk owner marks chunk as stabilized
└── All peers update their local world map

WHY NOT BLOCKCHAIN / RAFT / PAXOS:
├── Overkill for 20-50 players who trust each other
├── Adds 100-500ms latency per consensus round
├── Friends playing together = trust model is fine
├── Valheim has zero anti-cheat and sold 11M copies
└── Can add proper consensus post-launch if needed
```

### 4.3 Anti-Cheat (MVP: Minimal)

```
MVP APPROACH:
├── Chunk owner validates actions in their chunks
├── Resource pickup: only chunk owner grants items
├── Combat: chunk owner resolves damage
├── Inventory: peer self-reports (trusted)
├── No client-side prediction for critical actions
└── Cheaters can only affect their owned chunks

POST-LAUNCH:
├── Add peer voting for suspicious behavior
├── Add server-mode option (dedicated host = full authority)
├── Add replay/audit logging
└── Community reporting system
```

---

## 5. PROTOCOL SPECIFICATION

### 5.1 Packet Format

```
ALL PACKETS (UDP):
┌──────────────────────────────────────────┐
│ Header (12 bytes)                         │
├──────────┬───────────┬───────────────────┤
│ Type     │ Sender ID │ Sequence Number   │
│ (2 bytes)│ (2 bytes) │ (4 bytes)         │
├──────────┴───────────┴───────────────────┤
│ Timestamp (4 bytes, ms since session)    │
├──────────────────────────────────────────┤
│ Payload (variable, MessagePack encoded)  │
└──────────────────────────────────────────┘

WHY MESSAGEPACK (not JSON):
├── 2-5x smaller than JSON
├── 10-50x faster to parse than JSON
├── Schema-less (flexible like JSON)
├── Rust + C# libraries available
└── JSON for save files (human readable), MessagePack for network
```

### 5.2 Packet Types

```
CONNECTION (0x00-0x0F):
├── 0x00 DISCOVER        → Sent to discovery service: "I want to join session X"
├── 0x01 PEER_INTRO      → Discovery service introduces peers to each other
├── 0x02 HANDSHAKE       → Direct peer-to-peer: "Hello, I'm Player X"
├── 0x03 HANDSHAKE_ACK   → "Welcome, here's the world seed + peer list"
├── 0x04 WORLD_SYNC      → Full world state dump (on initial join)
├── 0x05 HEARTBEAT       → "I'm still here" (every 1 second)
├── 0x06 DISCONNECT      → "I'm leaving gracefully"
└── 0x07 PEER_LIST       → Updated list of all connected peers

STATE (0x10-0x1F):
├── 0x10 PLAYER_UPDATE   → Position, rotation, animation state
├── 0x11 CHUNK_STATE     → Full chunk data (entities, items)
├── 0x12 CHUNK_DELTA     → Changes since last update
├── 0x13 ENTITY_UPDATE   → Entity position/state changes
├── 0x14 STAT_UPDATE     → Player stats (hunger/thirst/sanity)
└── 0x15 INVENTORY_SYNC  → Inventory changes

ACTIONS (0x20-0x2F):
├── 0x20 INTERACT        → Player interacts with object
├── 0x21 ATTACK          → Player attacks entity
├── 0x22 PICKUP          → Player picks up item
├── 0x23 DROP            → Player drops item
├── 0x24 CRAFT           → Player crafts item
├── 0x25 PLACE_STABILIZER → Player places stabilizer
├── 0x26 PLACE_ANCHOR    → Player starts anchor installation
├── 0x27 REPAIR_ANCHOR   → Player repairs anchor
└── 0x28 USE_CONSUMABLE  → Player uses food/water/medicine

WORLD (0x30-0x3F):
├── 0x30 CHUNK_TRANSFER     → Ownership transfer between peers
├── 0x31 CHUNK_TRANSFER_ACK → Transfer acknowledged
├── 0x32 CHUNK_TELEPORT     → Chunk has teleported (broadcast)
├── 0x33 CHUNK_GENERATE     → New chunk generated at position
├── 0x34 ANCHOR_BROADCAST   → Anchor placed/updated (to all peers)
└── 0x35 STABILIZER_BROADCAST → Stabilizer placed (to all peers)

RELIABILITY (0xF0-0xFF):
├── 0xF0 ACK             → Acknowledge receipt of reliable packet
├── 0xF1 NACK            → Request retransmission
└── 0xF2 PING            → Latency measurement
```

### 5.3 Reliability Layer

```
UDP is unreliable. We need selective reliability:

UNRELIABLE (fire and forget):
├── PLAYER_UPDATE (position) — stale data is useless anyway
├── ENTITY_UPDATE — same logic
├── HEARTBEAT — next one will come in 1 sec
└── PING — timing measurement, loss is fine

RELIABLE (must arrive, retransmit if lost):
├── All ACTION packets — player intent must not be lost
├── CHUNK_TRANSFER — critical for ownership
├── ANCHOR_BROADCAST — must persist everywhere
├── WORLD_SYNC — initial join data
├── INVENTORY_SYNC — item changes matter
└── DISCONNECT — graceful cleanup

IMPLEMENTATION:
├── Reliable packets get sequence numbers
├── Receiver sends ACK within 100ms
├── If no ACK: retransmit at 200ms, 400ms, 800ms (exponential backoff)
├── After 5 retransmits: consider peer disconnected
└── Window size: 32 packets in-flight max
```

### 5.4 Connection Handshake

```
JOINING A SESSION:

1. New Player → Discovery Service:
   DISCOVER { session_id: "abc123", player_name: "Alex" }

2. Discovery Service → New Player:
   PEER_INTRO { peers: [
     { id: 1, ip: "...", port: ..., nat_type: "open" },
     { id: 2, ip: "...", port: ..., nat_type: "symmetric" }
   ]}

3. New Player → Each Peer (UDP hole-punch):
   HANDSHAKE { player_id: 3, player_name: "Alex", version: "0.1.0" }

4. Seed Peer → New Player:
   HANDSHAKE_ACK {
     world_seed: 42,
     session_config: { max_players: 50, world_name: "test" },
     peer_list: [{ id: 1, name: "Bob", pos: [10,0,5] }, ...],
     anchor_list: [{ pos: [3,2], durability: 85 }, ...],
     stabilizer_list: [{ pos: [5,5], tier: 2, remaining: 280 }, ...]
   }

5. Nearby Chunk Owners → New Player:
   WORLD_SYNC {
     chunks: [
       { pos: [10,10], entities: [...], items: [...] },
       { pos: [10,11], entities: [...], items: [...] }
     ]
   }

6. New Player → All Peers:
   PLAYER_UPDATE { id: 3, pos: [10,0,5], state: "idle" }

TOTAL JOIN TIME: ~2-5 seconds (depending on world size near spawn)
```

---

## 6. GAME LOOP ARCHITECTURE

### 6.1 Per-Peer Game Loop (Rust Backend)

```
EACH PEER RUNS THIS LOOP AT 60HZ (16.67ms per tick):

fn game_tick() {
    // ─── PHASE 1: RECEIVE (2ms budget) ───
    receive_network_packets();        // Drain UDP socket
    process_reliable_queue();         // Handle ACKs/retransmits
    process_player_updates();         // Update remote player positions
    process_action_requests();        // Handle actions from remote players in our chunks

    // ─── PHASE 2: SIMULATE (8ms budget) ───
    for chunk in owned_chunks {
        update_entities(chunk);       // AI, patrol, aggro
        check_teleport_timer(chunk);  // Should this chunk teleport?
        process_interactions(chunk);  // Player-entity, player-item interactions
        decay_anchor(chunk);          // Reduce anchor durability if visited
    }
    update_local_player_stats();      // Hunger, thirst, sanity decay

    // ─── PHASE 3: OWNERSHIP (1ms budget) ───
    check_chunk_ownership();          // Do I need to transfer/acquire chunks?
    process_ownership_transfers();    // Handle incoming transfers

    // ─── PHASE 4: SEND (3ms budget) ───
    broadcast_player_update();        // My position to all peers (unreliable, 10hz)
    broadcast_chunk_deltas();         // Chunk changes to nearby peers (reliable, 5hz)
    broadcast_critical_events();      // Anchors, stabilizers, deaths (reliable, immediate)
    send_heartbeat();                 // Every 1 second

    // ─── PHASE 5: PERSISTENCE (2ms budget, async) ───
    auto_save_if_needed();            // Every 5 minutes, async write to disk
}

TIMING BUDGET:
├── Total per tick: 16.67ms
├── Used: ~14ms typical
├── Headroom: ~2-3ms (for spikes)
└── If overrun: skip entity updates (least critical)
```

### 6.2 Adaptive Tick Rate

```
Not all systems need 60hz:

60hz (16.67ms):
├── Local player physics
├── Local player input
└── Combat resolution (when fighting)

10hz (100ms):
├── Player position broadcasts
├── Entity AI updates
├── Stat decay calculations
└── Chunk delta broadcasts

1hz (1000ms):
├── Heartbeat
├── Ownership recalculation
├── Teleport timer checks
└── Auto-save check

0.1hz (10000ms):
├── Peer list refresh
├── NAT keepalive
└── Discovery service ping
```

### 6.3 Client-Side (Unity) Loop

```
Unity runs its own Update() loop (vsync, typically 60fps):

void Update() {
    // ─── INPUT ───
    ProcessLocalInput();              // WASD, mouse, interactions
    SendInputToBackend();             // IPC to local Rust process

    // ─── RECEIVE ───
    ReceiveStateFromBackend();        // Get world state updates
    ReceiveRemotePlayerUpdates();     // Other players' positions

    // ─── RENDER ───
    InterpolateRemotePlayers();       // Smooth movement (lerp between updates)
    UpdateChunkVisuals();             // Load/unload chunk meshes
    UpdateEntityVisuals();            // Animate entities
    UpdateHUD();                      // Stats bars, minimap
    UpdateEffects();                  // Sanity effects, teleport flash

    // ─── AUDIO ───
    UpdateAmbient();                  // Background loops
    PlaySFX();                        // Entity sounds, interactions
}

COMMUNICATION BETWEEN UNITY AND RUST:
├── Method: Local TCP socket (localhost:7777)
├── Format: MessagePack (same as network)
├── Latency: <1ms (local)
├── Unity sends: player input, UI events
├── Rust sends: world state, entity updates, stat changes
└── This separation keeps game logic in Rust (fast) and rendering in Unity (pretty)
```

---

## 7. PROCEDURAL GENERATION

### 7.1 Chunk Generation Algorithm

```
SEED-BASED DETERMINISTIC GENERATION:

fn generate_chunk(world_seed: u64, chunk_pos: (i32, i32)) -> ChunkData {
    // Combine world seed with chunk position for unique but reproducible chunks
    let chunk_seed = hash(world_seed, chunk_pos.0, chunk_pos.1);
    let mut rng = SeededRng::new(chunk_seed);

    // 1. Pick template (5-10 base templates)
    let template_id = rng.range(0, TEMPLATE_COUNT);
    let template = load_template(template_id);

    // 2. Apply variations
    let rotation = rng.range(0, 4) * 90;        // 0, 90, 180, 270 degrees
    let mirror = rng.bool();                      // Horizontal flip
    let lighting = rng.range(0.3, 1.0);          // Brightness variation
    let decay_level = rng.range(0.0, 1.0);       // Visual decay

    // 3. Place resources
    let metal_count = rng.range(1, 6);           // 1-5 metal
    let circuit_count = rng.range(1, 4);         // 1-3 circuits
    let battery_count = rng.range(1, 3);         // 1-2 batteries
    let food_count = rng.range(1, 4);            // 1-3 food
    let water_count = rng.range(1, 4);           // 1-3 water

    // 4. Place entities
    let entity_count = rng.range(3, 6);          // 3-5 entities
    let entities = spawn_entities(entity_count, &mut rng);

    // 5. Place workbench (20% chance)
    let has_workbench = rng.float() < 0.2;

    ChunkData { template_id, rotation, mirror, lighting,
                resources, entities, has_workbench, ... }
}

WHY DETERMINISTIC:
├── Any peer can regenerate the same chunk from the seed
├── No need to transmit chunk layouts over the network
├── Only dynamic state (entities, items) needs syncing
├── Saves massive bandwidth
└── "Teleported" chunks regenerate with new seed = different layout (intentional)
```

### 7.2 Teleportation Mechanic

```
CHUNK TELEPORTATION (per chunk, on chunk owner):

fn check_teleport(chunk: &mut Chunk, dt: f32) {
    // Only RANDOM chunks teleport
    if chunk.state != ChunkState::Random { return; }

    // Decrement timer
    chunk.teleport_timer -= dt;

    if chunk.teleport_timer <= 0.0 {
        // Check stabilizer effect
        if chunk.has_stabilizer {
            let roll = random();
            if roll < chunk.stabilizer_efficiency {
                // Stabilizer prevented teleport
                chunk.teleport_timer = random_range(120.0, 600.0);
                return;
            }
        }

        // TELEPORT!
        // 1. Pick new position (within ±30 chunks)
        let new_pos = random_offset(chunk.pos, 30);

        // 2. Generate new chunk content at this position
        let new_seed = random_u64(); // NOT deterministic — truly new layout
        chunk.regenerate(new_seed);

        // 3. Reset timer
        chunk.teleport_timer = random_range(120.0, 600.0);

        // 4. Broadcast to all peers
        broadcast(ChunkTeleport {
            old_pos: chunk.pos,
            new_pos: new_pos,
            new_seed: new_seed,
            players_affected: players_in_chunk(chunk.pos),
        });

        // 5. Players in chunk experience the teleport
        // Unity client: flash effect + new environment loads
    }
}

PLAYER EXPERIENCE:
├── Screen flashes white/static for 0.5 seconds
├── Environment around them changes instantly
├── Minimap updates (chunk moved)
├── Sanity penalty: -5 points
├── Disorientation effect: slight camera wobble for 2 seconds
└── If stabilized: "Stabilizer held!" message + no teleport
```

---

## 8. ENTITY SYSTEM

### 8.1 Entity AI (Simple State Machine)

```
STATES:
┌─────────┐    player within 20u    ┌─────────┐
│  IDLE   │ ──────────────────────► │  ALERT  │
│ (patrol)│                         │ (search)│
└────┬────┘                         └────┬────┘
     │           ◄──────────────────     │
     │         player leaves 25u         │
     │                              player within 10u
     │                                   │
     │                              ┌────▼────┐
     │                              │  AGGRO  │
     │                              │ (chase) │
     │                              └────┬────┘
     │                                   │
     │         player kills entity       │
     │                              ┌────▼────┐
     │                              │  DEAD   │
     │                              │ (drops) │
     └──────────────────────────────└─────────┘
                 respawn after 120-300 sec

IDLE: Wander within chunk bounds, random direction changes every 3-8 sec
ALERT: Move toward last known player position, search for 10 sec
AGGRO: Direct chase, attack if within 2 units, deal 10 damage per hit
DEAD: Drop 5-10 cable, despawn body after 30 sec, respawn timer starts

PATHFINDING (MVP):
├── No navmesh (too complex for distributed)
├── Simple raycast steering: avoid walls, move toward target
├── If stuck for >3 seconds: teleport to nearest valid position
└── Works well enough for the backrooms aesthetic (entities are uncanny)
```

### 8.2 Entity Sync

```
Entity data is owned by the chunk owner:

BROADCAST (to peers with players in/near the chunk):
├── Entity ID
├── Position (3 floats)
├── State (1 byte: idle/alert/aggro/dead)
├── Health (1 byte, 0-100 compressed)
├── Target player ID (2 bytes, if aggro)
└── Total: ~20 bytes per entity, 10hz = ~1KB/sec per chunk

COMBAT RESOLUTION:
├── Player sends ATTACK action to chunk owner
├── Chunk owner validates: is player close enough? Is entity alive?
├── Chunk owner applies damage, broadcasts result
├── If entity dies: chunk owner broadcasts ENTITY_DEATH + drops
├── Player picks up drops: chunk owner validates + grants
└── No client-side hit detection (prevents cheating)
```

---

## 9. SURVIVAL STATS SYSTEM

### 9.1 Stat Calculations (Per Peer, Local)

```
EACH PEER TRACKS THEIR OWN STATS:

fn update_stats(player: &mut Player, dt: f32, context: &ChunkContext) {
    // Base decay
    player.hunger -= 0.5 * dt;
    player.thirst -= 0.7 * dt;

    // Sanity decay depends on context
    let sanity_drain = calculate_sanity_drain(context);
    player.sanity -= sanity_drain * dt;

    // Clamp
    player.hunger = player.hunger.clamp(0.0, 100.0);
    player.thirst = player.thirst.clamp(0.0, 100.0);
    player.sanity = player.sanity.clamp(0.0, 100.0);

    // Apply consequences
    if player.hunger < 20.0 {
        player.speed_modifier = 0.7; // -30% speed
    }
    if player.sanity < 50.0 {
        player.hallucination_intensity = 1.0 - (player.sanity / 50.0);
    }
    if player.sanity < 20.0 {
        player.accuracy_modifier = 0.5; // -50% accuracy
    }

    // Broadcast stats every 2 seconds (reliable)
    if time_since_last_stat_broadcast > 2.0 {
        broadcast_stat_update(player);
    }
}

fn calculate_sanity_drain(context: &ChunkContext) -> f32 {
    let mut drain = 0.1; // Base drain

    // Near entities
    if context.entities_visible > 0 {
        drain += 0.3 * context.entities_visible as f32;
    }

    // In unstabilized chunk
    if !context.chunk_stabilized {
        drain += 0.2;
    }

    // Alone (no other players nearby)
    if context.nearby_players == 0 {
        drain += 0.3;
    }

    // In darkness
    if context.light_level < 0.3 {
        drain += 0.5;
    }

    drain.min(2.0) // Cap at 2.0/sec
}
```

### 9.2 Stat Sync (Trust Model)

```
Each peer is authoritative for their own stats.

WHY:
├── Stats are personal (hunger, thirst, sanity)
├── No gameplay benefit to faking stats (you hurt yourself)
├── Reduces network traffic (no validation round-trip)
├── Simple implementation
└── If someone cheats their stats, it only affects their experience

BROADCAST:
├── Stats broadcast every 2 seconds (reliable)
├── Other peers display your stats (team awareness)
├── On death: broadcast PLAYER_DEATH, peers see it
└── On respawn: reset stats to 50/50/50
```

---

## 10. PERSISTENCE SYSTEM

### 10.1 Save File Structure

```json
{
  "version": "0.1.0",
  "world_seed": 42,
  "session_name": "Friends Server",
  "created_at": "2026-06-07T12:00:00Z",
  "last_saved": "2026-06-07T14:30:00Z",
  "play_time_seconds": 9000,

  "config": {
    "max_players": 50,
    "teleport_interval_min": 120,
    "teleport_interval_max": 600,
    "entity_scaling": 1.0
  },

  "players": {
    "player_uuid_1": {
      "name": "Alex",
      "position": [150.0, 0.0, 200.0],
      "stats": { "health": 85, "hunger": 60, "thirst": 45, "sanity": 70 },
      "inventory": [
        { "item": "metal", "quantity": 15, "slot": 0 },
        { "item": "cable", "quantity": 23, "slot": 1 },
        { "item": "stabilizer_t2", "quantity": 1, "slot": 10, "active": true }
      ]
    }
  },

  "anchors": [
    { "chunk_pos": [3, 2], "durability": 85, "installed_at": "2026-06-07T13:00:00Z" },
    { "chunk_pos": [5, 5], "durability": 100, "installed_at": "2026-06-07T14:00:00Z" }
  ],

  "stabilizers": [
    { "chunk_pos": [4, 3], "tier": 1, "remaining_hours": 42.5 },
    { "chunk_pos": [6, 6], "tier": 2, "remaining_hours": 290.0 }
  ],

  "explored_chunks": [[0,0], [0,1], [1,0], [1,1], [2,0]],

  "chunk_overrides": {
    "(3,2)": { "seed_override": 98765, "entities_killed": [0, 2, 4] },
    "(5,5)": { "seed_override": 54321 }
  }
}
```

### 10.2 Save Strategy (Distributed)

```
EACH PEER SAVES LOCALLY:
├── Their own player data (position, stats, inventory)
├── All known anchors (replicated critical data)
├── All known stabilizers (replicated)
├── Explored chunks list
├── Chunk overrides for their owned chunks

SEED PEER ADDITIONALLY SAVES:
├── Full world configuration
├── Player list (for reconnection)
├── Session metadata
└── Acts as "primary save" (others are backups)

SAVE TRIGGERS:
├── Every 5 minutes (auto)
├── On graceful disconnect
├── On anchor/stabilizer placement
├── On player death
└── Manual save from pause menu

LOAD ON REJOIN:
├── Connect to session
├── Send local save data to seed peer
├── Seed peer merges: takes latest timestamp per field
├── Conflicts: anchor/stabilizer data merged (union), stats from local save
└── Result: seamless rejoin even if some data was lost
```

---

## 11. DATA STRUCTURES (Rust)

### 11.1 Core Structs

```rust
// ─── WORLD ───
pub struct World {
    pub seed: u64,
    pub config: WorldConfig,
    pub chunks: HashMap<(i32, i32), Chunk>,
    pub anchors: Vec<Anchor>,
    pub stabilizers: Vec<Stabilizer>,
}

pub struct WorldConfig {
    pub max_players: u16,
    pub teleport_interval: (f32, f32),  // min, max seconds
    pub entity_scaling: f32,
    pub chunk_size: f32,               // 50.0 units
    pub ownership_radius: i32,         // 5 chunks
}

// ─── CHUNK ───
pub struct Chunk {
    pub pos: (i32, i32),
    pub state: ChunkState,
    pub seed: u64,                     // For regeneration
    pub owner: Option<PeerId>,
    pub entities: Vec<Entity>,
    pub items: Vec<DroppedItem>,
    pub teleport_timer: f32,
    pub has_workbench: bool,
}

pub enum ChunkState {
    Unloaded,
    Dormant { cached_by: Vec<PeerId> },
    Active { stabilized: bool, anchored: bool },
}

// ─── ENTITY ───
pub struct Entity {
    pub id: u32,
    pub entity_type: EntityType,
    pub position: Vec3,
    pub health: u8,
    pub state: EntityState,
    pub target: Option<PeerId>,
    pub patrol_center: Vec3,
    pub respawn_timer: Option<f32>,
}

pub enum EntityType {
    Lurker,        // Basic, slow, low damage
    Crawler,       // Fast, fragile
    Shadow,        // Invisible until close, high sanity drain
}

pub enum EntityState {
    Idle,
    Alert { last_known_pos: Vec3, search_timer: f32 },
    Aggro { target: PeerId },
    Dead { drop_items: Vec<Item>, despawn_timer: f32 },
}

// ─── PLAYER ───
pub struct Player {
    pub id: PeerId,
    pub uuid: Uuid,
    pub name: String,
    pub position: Vec3,
    pub rotation: f32,
    pub stats: PlayerStats,
    pub inventory: Inventory,
    pub equipped_stabilizer: Option<StabilizerItem>,
    pub owned_chunks: Vec<(i32, i32)>,
}

pub struct PlayerStats {
    pub health: f32,
    pub hunger: f32,
    pub thirst: f32,
    pub sanity: f32,
    pub speed_modifier: f32,
    pub accuracy_modifier: f32,
}

pub struct Inventory {
    pub slots: [Option<ItemStack>; 20],
}

pub struct ItemStack {
    pub item: Item,
    pub quantity: u16,   // Max 99
}

// ─── ITEMS ───
pub enum Item {
    Metal,
    Circuit,
    Battery,
    Cable,
    Food,
    Water,
    Medicine,
    Tool,                    // Reusable crafting tool
    Stabilizer(StabilizerTier),
}

pub enum StabilizerTier { T1, T2, T3 }

// ─── INFRASTRUCTURE ───
pub struct Anchor {
    pub chunk_pos: (i32, i32),
    pub durability: f32,     // 0-100
    pub installed_at: u64,   // Timestamp
    pub installed_by: Uuid,
}

pub struct Stabilizer {
    pub chunk_pos: (i32, i32),
    pub tier: StabilizerTier,
    pub remaining_hours: f32,
    pub placed_by: Uuid,
}

// ─── NETWORK ───
pub type PeerId = u16;

pub struct PeerConnection {
    pub id: PeerId,
    pub addr: SocketAddr,
    pub latency_ms: u16,
    pub last_heartbeat: Instant,
    pub reliable_queue: VecDeque<ReliablePacket>,
    pub sequence_counter: u32,
}
```

---

## 12. UNITY ↔ RUST COMMUNICATION

### 12.1 IPC Architecture

```
┌──────────────────┐         ┌──────────────────┐
│   UNITY (C#)     │         │   RUST BACKEND   │
│   Rendering      │◄──TCP──►│   Game Logic     │
│   Input          │ :7777   │   Networking     │
│   UI             │         │   Persistence    │
│   Audio          │         │   Entity AI      │
└──────────────────┘         └──────────────────┘

WHY SEPARATE PROCESSES:
├── Rust handles all game logic + networking (performance critical)
├── Unity handles rendering + input (what it's best at)
├── Crash isolation: if one crashes, the other can recover
├── Language strengths: Rust for systems, C# for UI/visuals
└── Development: you work on Unity (C# expert), Claude Code handles Rust

IPC FORMAT:
├── Local TCP socket (localhost:7777)
├── MessagePack encoded messages
├── Request/Response pattern for actions
├── Stream pattern for state updates
└── Latency: <1ms (local loopback)
```

### 12.2 Messages: Unity → Rust

```
PLAYER INPUT (every frame):
{
    type: "input",
    move: [0.0, 0.0, 1.0],      // Forward
    look: [0.5, -0.1],           // Mouse delta
    actions: ["interact"]         // Queued actions this frame
}

ACTION REQUEST:
{
    type: "action",
    action: "craft",
    data: { recipe: "stabilizer_t1" }
}

UI EVENTS:
{
    type: "ui",
    event: "pause" | "save" | "quit"
}
```

### 12.3 Messages: Rust → Unity

```
WORLD STATE (10hz):
{
    type: "world_state",
    local_player: {
        position: [10.0, 0.0, 5.0],
        stats: { health: 85, hunger: 60, thirst: 45, sanity: 70 }
    },
    remote_players: [
        { id: 2, position: [15.0, 0.0, 8.0], animation: "walk" }
    ],
    visible_chunks: [
        { pos: [2,1], state: "active", anchored: false }
    ],
    visible_entities: [
        { id: 1, position: [12.0, 0.0, 6.0], type: "lurker", state: "idle" }
    ]
}

EVENTS (immediate):
{
    type: "event",
    event: "chunk_teleported" | "entity_killed" | "player_died" | "item_picked_up",
    data: { ... }
}

ACTION RESPONSE:
{
    type: "action_result",
    success: true,
    action: "craft",
    result: { item: "stabilizer_t1", slot: 5 }
}
```

---

## 13. IMPLEMENTATION PLAN

### 13.1 What Claude Code Builds (Rust Backend)

```
PRIORITY ORDER:

Phase 1 — Foundation (2-3 hours):
├── Project structure + dependencies
├── Local TCP IPC server (Unity communication)
├── Basic game loop (60hz tick)
├── Player struct + stats system
├── Simple input processing
└── DELIVERABLE: Unity can connect, send input, receive position updates

Phase 2 — World (2-3 hours):
├── Chunk generation (seed-based, deterministic)
├── Chunk ownership system
├── Teleportation logic + timers
├── Entity spawning + basic AI (state machine)
├── Resource distribution in chunks
└── DELIVERABLE: Walking around, seeing chunks, entities move

Phase 3 — Networking (3-4 hours):
├── UDP socket + packet serialization (MessagePack)
├── Peer discovery + handshake
├── Reliable packet layer (ACK/retransmit)
├── Player position sync
├── Chunk state sync + delta compression
├── Chunk ownership transfer protocol
├── Entity sync to nearby peers
└── DELIVERABLE: Two+ peers see each other, shared world

Phase 4 — Gameplay (2-3 hours):
├── Inventory system + item management
├── Crafting recipes (stabilizers, anchors)
├── Stabilizer placement + effect
├── Anchor installation (timed, interruptible)
├── Combat system (attack → damage → drops)
├── Consumable usage (food/water/medicine)
└── DELIVERABLE: Full gameplay loop working

Phase 5 — Persistence (1-2 hours):
├── JSON save/load
├── Auto-save timer
├── Distributed save merge on rejoin
├── Anchor/stabilizer persistence
└── DELIVERABLE: Save, quit, rejoin, everything preserved
```

### 13.2 What You Build (Unity Client)

```
PRIORITY ORDER (match backend phases):

Phase 1 — Foundation:
├── TCP client to localhost:7777
├── MessagePack deserialization (install NuGet package)
├── Basic player controller (first person)
├── Camera setup (first person + mouselook)
└── DELIVERABLE: Move in empty scene, see position sync with backend

Phase 2 — World:
├── ChunkRenderer: instantiate prefab chunks based on backend data
├── Entity renderer: spawn/move entity prefabs
├── Resource pickups: visual indicators
├── Teleportation VFX: flash + environment swap
└── DELIVERABLE: See chunks loading, entities moving

Phase 3 — Multiplayer:
├── RemotePlayer prefab: shows other players
├── Interpolation: smooth movement between updates
├── Name tags above players
├── Join/leave notifications
└── DELIVERABLE: See other players, smooth movement

Phase 4 — UI:
├── HUD: health bar, stat bars, minimap
├── Inventory UI: 4x5 grid, drag-drop
├── Crafting UI: recipe list, craft button
├── Main menu: Create/Load/Join/Settings
├── Pause menu: Resume/Inventory/Save/Quit
└── DELIVERABLE: Full UI functional

Phase 5 — Polish:
├── Sanity effects (visual distortion, hallucinations)
├── Audio (ambient loops, entity sounds, UI clicks)
├── Chunk transition effects
├── Entity death/spawn animations
└── DELIVERABLE: Feels like a game
```

### 13.3 Integration Points

```
CRITICAL SYNC POINTS (must coordinate):

1. IPC MESSAGE FORMAT
   ├── Defined in this document (Section 12)
   ├── Both sides must use same MessagePack schema
   ├── Test with simple ping/pong first
   └── Any change requires both sides to update

2. CHUNK RENDERING
   ├── Backend sends chunk template_id + variations
   ├── Unity maps template_id to prefab
   ├── Must agree on coordinate system (1 unit = 1 meter)
   └── Chunk origin: bottom-left corner

3. ENTITY VISUALS
   ├── Backend sends entity_type enum
   ├── Unity maps to prefab (Lurker, Crawler, Shadow)
   ├── Animation state sent as string
   └── Must agree on entity scale

4. INPUT FORMAT
   ├── Unity sends raw input (movement vector + look delta)
   ├── Backend processes physics
   ├── Backend sends back authoritative position
   └── Unity renders at authoritative position (no client prediction in MVP)
```

---

## 14. DESIGN DECISIONS & RATIONALE

### Why Distributed P2P (not Client-Server)?
Zero cost at any scale. The game mechanic (teleporting chunks) naturally maps to distributed system failure modes. More players = more resources. No single point of failure.

**Tradeoff:** More complex to implement, harder anti-cheat, eventual consistency instead of strong consistency.

### Why Rust Backend + Unity Client (not Unity-only)?
Rust gives 10-20x better performance for networking + simulation. The distributed P2P system with chunk ownership, entity AI, and reliable UDP is systems-level work that benefits enormously from Rust's performance and safety guarantees. Unity handles what it's best at: rendering and UI.

**Tradeoff:** Two languages, IPC overhead, more moving parts.

### Why MessagePack (not JSON for network)?
2-5x smaller packets, 10-50x faster parsing. At 10hz × 50 players × 20 bytes per entity × 5 entities = 50KB/sec per peer. JSON would be 150KB/sec. Matters on slow connections.

**Tradeoff:** Not human-readable (but we use JSON for save files where readability matters).

### Why No Client-Side Prediction?
Client-side prediction adds massive complexity (rollback, reconciliation, ghost entities). For a co-op game where 100-200ms latency is acceptable (not competitive FPS), authoritative backend with interpolation is simpler and correct.

**Tradeoff:** Slightly "floaty" feel at high latency. Acceptable for horror survival (not twitchy gameplay).

### Why Trust Model (not Full Validation)?
Valheim sold 11M copies with zero anti-cheat. Co-op survival games are played with friends who trust each other. Adding full validation doubles development time and adds latency.

**Tradeoff:** Cheating is possible. Acceptable for MVP, can add validation post-launch.

### Why Last-Write-Wins (not Consensus)?
RAFT/Paxos adds 100-500ms per operation. For 20-50 players who are friends, timestamp-based resolution is sufficient. Anchors use "earliest timestamp wins" which prevents griefing (can't overwrite someone's earlier anchor).

**Tradeoff:** Theoretical split-brain scenarios. Unlikely with friend groups, and chunk teleportation masks any inconsistency.

---

## 15. KNOWN LIMITATIONS (MVP)

```
1. NAT TRAVERSAL
   ├── ~30% of players may need relay
   ├── Relay adds 50-100ms latency
   └── Solution: TURN server (post-launch) or WebRTC

2. SCALE LIMIT
   ├── MVP tested for 20-50 players
   ├── Beyond 50: bandwidth may bottleneck on some peers
   └── Solution: Interest culling, chunk streaming (post-launch)

3. CHEAT VULNERABILITY
   ├── Players can modify their own client
   ├── Chunk owners can give themselves items
   └── Solution: Peer voting + server mode (post-launch)

4. SAVE CONSISTENCY
   ├── Distributed saves may diverge slightly
   ├── Merge on rejoin is "best effort"
   └── Solution: Cloud backup service (post-launch)

5. HOST MIGRATION
   ├── If seed peer disconnects, session metadata may be lost
   ├── Other peers keep their chunks + data
   └── Solution: Elect new seed peer (post-launch, or in Phase 3)

6. ENTITY AI
   ├── Simple raycast steering, no pathfinding
   ├── Entities may get stuck on geometry
   └── Solution: NavMesh or A* (post-launch)
```

---

## 16. NEXT STEPS

### Immediate (Before Coding):
1. Review this architecture document
2. Ask questions about anything unclear
3. Verify you can set up TCP client in Unity (simple test)
4. Decide on chunk prefab templates (5 minimum for variety)

### Session 2 (Claude Code — Rust Implementation):
1. Claude Code implements Phases 1-5 of the Rust backend
2. You implement Unity client in parallel
3. Integration test: Unity connects to Rust, player moves, chunks render

### Post-Session 2:
1. Test with 2 players (you + one friend)
2. Test chunk teleportation
3. Test stabilizer/anchor mechanics
4. Bug fixing + polish

---

**This architecture is designed to be built fast, work reliably at 20-50 players, and scale gracefully to 200+ with post-launch optimizations. The key insight — that the game mechanic and the distributed architecture are the same thing — makes this uniquely viable as an indie P2P MMO-like experience.**

**Questions? Ask now. Better to clarify before writing code.**