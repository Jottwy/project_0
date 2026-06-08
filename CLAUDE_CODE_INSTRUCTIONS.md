# CLAUDE CODE — BACKROOMS SURVIVAL IMPLEMENTATION GUIDE

> Feed this file to Claude Code as project context.
> Usage: `claude --project-context CLAUDE_CODE_INSTRUCTIONS.md`

---

## PROJECT OVERVIEW

Building a distributed P2P co-op survival horror game called "Backrooms Survival".
Architecture: Rust backend (game logic + networking) + Unity C# client (rendering + UI).
Communication: Local TCP IPC on localhost:7777 (MessagePack encoded).

The developer is ADVANCED in C#/Unity, BEGINNER in Rust.
Priority: get a working prototype as fast as possible.

---

## PHASE 1: RUST BACKEND FOUNDATION

### Task 1.1: Project Setup
```
Create Rust project at ./backend/ with these dependencies:
- tokio (full features) — async runtime
- serde + serde_json — serialization
- rmp-serde — MessagePack for network/IPC
- quinn — QUIC/UDP networking
- log + env_logger — logging
- rand — randomization
- uuid (v4, serde) — player IDs
- chrono — timestamps

Structure:
backend/src/
├── main.rs
├── game_loop.rs
├── ipc/
│   ├── mod.rs
│   └── server.rs          # TCP server on localhost:7777
├── world/
│   ├── mod.rs
│   ├── chunk.rs            # Chunk state, teleportation
│   ├── generator.rs        # Seed-based procedural generation
│   └── entity.rs           # Entity AI state machine
├── player/
│   ├── mod.rs
│   ├── session.rs          # Player connection state
│   ├── inventory.rs        # Items, stacking, crafting
│   └── stats.rs            # Hunger/thirst/sanity
├── network/
│   ├── mod.rs
│   ├── protocol.rs         # Packet definitions (MessagePack)
│   ├── peer.rs             # Peer connection management
│   ├── reliability.rs      # ACK/retransmit layer
│   └── sync.rs             # State synchronization
├── crafting/
│   ├── mod.rs
│   └── recipes.rs          # All crafting recipes
├── persistence/
│   ├── mod.rs
│   └── save.rs             # JSON save/load
└── utils/
    └── mod.rs
```

### Task 1.2: IPC Server (Unity ↔ Rust communication)
```
TCP server listening on 127.0.0.1:7777
MessagePack encoded messages
Bidirectional:
  Unity → Rust: player input, action requests, UI events
  Rust → Unity: world state, events, action results

Message types (Rust → Unity):
- WorldState: local player pos/stats, remote players, visible chunks, visible entities
- Event: chunk_teleported, entity_killed, player_died, item_picked_up, etc.
- ActionResult: success/failure of craft, pickup, attack, etc.

Message types (Unity → Rust):
- Input: movement vector, look delta, action queue
- Action: craft, pickup, drop, attack, interact, place_stabilizer, place_anchor
- UIEvent: pause, save, quit, open_inventory
```

### Task 1.3: Game Loop (60hz)
```
Main loop at 60hz (16.67ms per tick):
1. Receive + process IPC messages from Unity
2. Simulate owned chunks (entity AI, teleport timers)
3. Update local player stats (hunger/thirst/sanity decay)
4. Check chunk ownership changes
5. Broadcast state to connected peers (at variable rates)
6. Send WorldState to Unity client (10hz)
7. Auto-save check (every 5 min)

Adaptive tick rates:
- 60hz: local physics, input, combat
- 10hz: position broadcasts, entity AI, stat decay, chunk deltas
- 1hz: heartbeat, ownership recalc, teleport timer checks
```

### Task 1.4: World + Chunks
```
Chunk generation: deterministic from (world_seed, chunk_x, chunk_y)
- Pick template (0-9), apply rotation/mirror/lighting variation
- Place resources: metal(1-5), circuits(1-3), batteries(1-2), food(1-3), water(1-3)
- Place entities: 3-5 per chunk
- 20% chance workbench

Chunk states: Unloaded → Dormant → Active(Random/Stabilized/Anchored)
Teleportation: timer 120-600 sec per chunk, checked at 1hz
Ownership: each peer owns chunks within 5-chunk radius of their position
```

### Task 1.5: Entity AI
```
Simple state machine per entity:
- IDLE: wander within chunk, change direction every 3-8 sec
- ALERT: player within 20 units, move toward last known position for 10 sec
- AGGRO: player within 10 units, chase + attack at 2 unit range, 10 damage/hit
- DEAD: drop 5-10 cable, despawn after 30 sec, respawn 120-300 sec

Types (MVP):
- Lurker: slow, 50 HP, 10 damage
- Crawler: fast, 30 HP, 10 damage
- Shadow: invisible until 8 units, 40 HP, high sanity drain

Pathfinding: simple raycast steering (no navmesh)
```

### Task 1.6: Player Stats
```
Stats (0-100 float):
- Hunger: -0.5/sec base
- Thirst: -0.7/sec base
- Sanity: -0.1 to -2.0/sec (contextual)
  - +0.3 per visible entity
  - +0.2 in unstabilized chunk
  - +0.3 when alone
  - +0.5 in dark areas

Consequences:
- Hunger < 20: speed_modifier = 0.7
- Thirst < 20: triggers vision_blur event to Unity
- Sanity < 50: hallucination_intensity = 1 - (sanity/50)
- Sanity < 20: accuracy_modifier = 0.5
- Health = 0: death → respawn at last anchor, drop items
```

### Task 1.7: Inventory + Crafting
```
Inventory: 20 slots, stack limit 99
Items: Metal, Circuit, Battery, Cable, Food, Water, Medicine, Tool, Stabilizer(T1/T2/T3)

Crafting recipes:
- Stabilizer T1: 10 metal + 5 circuits + 1 battery → instant
- Stabilizer T2: 25 metal + 15 circuits + 3 batteries + 10 cable → 15 min
- Stabilizer T3: 50 metal + 40 circuits + 10 batteries + 30 cable → 30 min
- Anchor: 50 cable + 1 tool (not consumed) → 25 min installation

Requires workbench (present in ~20% of chunks)
```

### Task 1.8: P2P Networking
```
UDP packets, MessagePack encoded, 12-byte header:
- Type(2) + SenderID(2) + SequenceNum(4) + Timestamp(4)

Connection flow:
1. Discovery (future: for now, direct IP entry)
2. HANDSHAKE → HANDSHAKE_ACK (world seed, config, peer list, anchors)
3. WORLD_SYNC (nearby chunks from owners)
4. Regular updates begin

Reliability layer:
- Unreliable: position updates, entity updates, heartbeat
- Reliable: actions, chunk transfers, anchor broadcasts, inventory sync
- ACK within 100ms, retransmit at 200/400/800ms, timeout after 5 retries

Chunk ownership transfer:
- When peer moves away, serialize chunk state → send to nearest peer
- If no peer nearby → chunk goes dormant (cached)
- If owner disconnects → chunks regenerate = teleport mechanic
```

### Task 1.9: Persistence
```
Save format: JSON (see ARCHITECTURE_V1.md Section 10.1)
Auto-save: every 5 minutes + on exit + on anchor placement + on death
Each peer saves: own player data + all known anchors + stabilizers + explored chunks
Seed peer additionally saves: full world config + player list

Load: merge saves on rejoin (latest timestamp per field, union of anchors)
```

---

## PHASE 2: UNITY C# CLIENT

### Task 2.1: IPC Client (connect to Rust backend)
```
Create: Assets/Scripts/Network/IPCClient.cs
- TCP client connecting to localhost:7777
- MessagePack deserialization (install MessagePack-CSharp via NuGet/UPM)
- Async receive loop for WorldState + Events
- Send methods for Input + Actions + UIEvents
- Singleton pattern, initialize on game start
- Auto-reconnect if connection drops
```

### Task 2.2: Player Controller
```
Create: Assets/Scripts/Gameplay/PlayerController.cs
- First-person controller (WASD + mouse look)
- Receives authoritative position from Rust backend
- Sends input to backend every frame
- Sprint (Shift), interact (E), attack (LMB), drop (G)
- No client-side prediction (backend is authoritative)
- Smooth interpolation between received positions
```

### Task 2.3: Remote Players
```
Create: Assets/Scripts/Gameplay/RemotePlayer.cs
- Spawned/despawned based on WorldState.remote_players
- Interpolate between position updates (lerp at 10hz → 60fps)
- Name tag above head (TextMeshPro world space)
- Basic animation states: idle, walk, run, attack
```

### Task 2.4: Chunk Rendering
```
Create: Assets/Scripts/Gameplay/ChunkRenderer.cs
- Pool of chunk GameObjects (reuse, don't instantiate/destroy)
- Receive visible_chunks from WorldState
- Map template_id (0-9) to prefab variants
- Apply rotation/mirror from chunk data
- Load/unload based on visibility
- Teleportation VFX: white flash + static noise for 0.5 sec

Create: Assets/Scripts/Gameplay/ChunkPrefabManager.cs
- ScriptableObject with references to 5-10 chunk prefabs
- For prototype: use ProBuilder or simple geometry
- Each prefab = 50x50 units, backrooms aesthetic (fluorescent lights, carpet, walls)
```

### Task 2.5: Entity Rendering
```
Create: Assets/Scripts/Gameplay/EntityRenderer.cs
- Pool of entity GameObjects
- Receive visible_entities from WorldState
- Map entity_type to prefab (Lurker, Crawler, Shadow)
- Interpolate position + animate state
- Shadow type: invisible shader until close, then fade in
- Death: ragdoll/dissolve effect, show cable drop
```

### Task 2.6: HUD
```
Create: Assets/Scripts/UI/HUD.cs
- Health bar (top-left): red bar, shows damage flash
- Stat bars (top-right): hunger (orange), thirst (blue), sanity (purple)
- Minimap (bottom-right): shows explored chunks, player position, anchor icons
- Crosshair (center): changes color when targeting entity
- Action prompt (center-bottom): "Press E to interact", "Press E to craft"
- Notification area (top-center): "Player X joined", "Chunk teleported!"

Use: Canvas with CanvasScaler (1920x1080 reference, scale with screen)
```

### Task 2.7: Inventory UI
```
Create: Assets/Scripts/UI/InventoryUI.cs
- Toggle with Tab key
- 4x5 grid of slots
- Drag-drop between slots
- Right-click context menu: Use, Drop, Equip (for stabilizers)
- Equipment slot (shows active stabilizer)
- Item tooltips on hover
- Shows current stats + weight

Create: Assets/Scripts/UI/CraftingUI.cs
- Available when near workbench
- Recipe list with required materials
- Craft button (grayed out if missing materials)
- Progress bar for timed crafts (stabilizer T2/T3, anchor)
- Interruptible: if entity attacks during craft, cancel + lose resources
```

### Task 2.8: Main Menu + Pause Menu
```
Create: Assets/Scripts/UI/MainMenuManager.cs
- Create World: enter world name, generates seed, starts Rust backend
- Load World: list of save files, select + load
- Join World: enter IP address, connect to remote peer
- Settings: graphics (quality, resolution), audio (master, sfx, music), controls
- Quit

Create: Assets/Scripts/UI/PauseMenu.cs
- Toggle with Escape
- Resume, Inventory, Settings, Save & Quit
- Save & Quit: sends save command to Rust, waits for confirmation, exits
```

### Task 2.9: Sanity Effects
```
Create: Assets/Scripts/Gameplay/SanityEffects.cs
- Sanity > 50: normal
- Sanity 20-50: post-processing volume (slight vignette, chromatic aberration)
- Sanity < 20: heavy distortion, hallucination entities (client-side only, don't exist on server)
  - Fake entities that appear/disappear
  - Whisper audio
  - Flickering lights
  - Slight camera shake
```

### Task 2.10: Audio
```
Create: Assets/Scripts/Audio/AudioManager.cs
- Ambient loops: fluorescent hum, distant sounds, dripping
- Entity sounds: footsteps, growl (alert), screech (aggro)
- Player sounds: footsteps, pickup, craft, eat/drink
- UI sounds: button click, inventory open/close, notification
- Sanity sounds: whispers, distortion, heartbeat (low sanity)
- Chunk teleport: static burst + woosh

Use: AudioMixer with groups (Master, SFX, Music, Ambient, UI)
```

---

## PHASE 3: INTEGRATION + TESTING

### Task 3.1: Integration Test Script
```
Create a test that:
1. Starts Rust backend (cargo run)
2. Unity connects via TCP
3. Send movement input → verify position update received
4. Verify chunks load around player
5. Verify entities spawn and move
6. Send attack action → verify entity takes damage
7. Send craft action → verify item appears in inventory
8. Trigger chunk teleport → verify teleport event received
9. Test save → quit → reload → verify state preserved
```

### Task 3.2: Multi-Peer Test
```
1. Start Rust backend as peer A (host)
2. Start second Rust instance as peer B
3. Verify peer B receives world state
4. Verify both peers see each other's position updates
5. Peer B moves → verify chunk ownership transfers
6. Disconnect peer B → verify their chunks teleport
7. Peer B reconnects → verify save merge works
```

### Task 3.3: Stress Test
```
1. Simulate 20 fake peers connecting
2. Each sends position updates at 10hz
3. Monitor: CPU usage, memory, bandwidth per peer
4. Verify tick rate stays at 60hz
5. Verify no memory leaks after 30 minutes
```

---

## IMPLEMENTATION NOTES

### MessagePack Schema (shared between Rust and C#)

```
// Rust → Unity (WorldState, sent at 10hz)
WorldState {
    tick: u64,
    local_player: {
        position: [f32; 3],
        rotation: f32,
        stats: { health: f32, hunger: f32, thirst: f32, sanity: f32 },
        speed_modifier: f32,
        inventory_changed: bool
    },
    remote_players: [{
        id: u16,
        name: String,
        position: [f32; 3],
        rotation: f32,
        animation: String,  // "idle", "walk", "run", "attack"
    }],
    visible_chunks: [{
        pos: [i32; 2],
        template_id: u8,
        rotation: u16,         // 0, 90, 180, 270
        mirrored: bool,
        state: String,         // "random", "stabilized", "anchored"
        has_workbench: bool,
    }],
    visible_entities: [{
        id: u32,
        entity_type: String,   // "lurker", "crawler", "shadow"
        position: [f32; 3],
        rotation: f32,
        state: String,         // "idle", "alert", "aggro", "dead"
        health_pct: f32,       // 0.0 - 1.0
    }],
    visible_items: [{
        id: u32,
        item_type: String,
        position: [f32; 3],
        quantity: u16,
    }]
}

// Rust → Unity (Event, sent immediately)
GameEvent {
    event_type: String,
    data: Value,  // flexible MessagePack value
}
// Event types:
// "chunk_teleported" → { old_pos, new_pos, players_affected }
// "entity_killed" → { entity_id, drops: [{item, quantity}] }
// "player_died" → { player_id, death_pos }
// "item_picked_up" → { item_type, quantity, slot }
// "craft_complete" → { item_type, slot }
// "craft_failed" → { reason }
// "anchor_placed" → { chunk_pos, durability }
// "stabilizer_placed" → { chunk_pos, tier }
// "player_joined" → { player_id, name }
// "player_left" → { player_id, name }
// "damage_taken" → { amount, source }
// "stat_warning" → { stat, value }  // when hunger/thirst/sanity < 20

// Unity → Rust (Input, sent every frame)
PlayerInput {
    movement: [f32; 3],    // normalized direction
    look_delta: [f32; 2],  // mouse delta
    sprint: bool,
    actions: [String],     // queued actions this frame
}

// Unity → Rust (Action, sent on user interaction)
PlayerAction {
    action_type: String,
    data: Value,
}
// Action types:
// "attack" → { target_entity_id: Option<u32> }
// "interact" → { target_pos: [f32;3] }
// "pickup" → { item_id: u32 }
// "drop" → { slot: u8, quantity: u16 }
// "craft" → { recipe: String }  // "stabilizer_t1", "stabilizer_t2", etc.
// "use_consumable" → { slot: u8 }
// "place_stabilizer" → { slot: u8 }
// "place_anchor" → {}
// "repair_anchor" → { chunk_pos: [i32;2] }
// "cancel_craft" → {}

// Unity → Rust (UI Events)
UIEvent {
    event_type: String,  // "pause", "resume", "save", "quit", "open_inventory"
}
```

### Coordinate System Agreement
```
Unity standard: Y-up, left-handed
Chunk origin: bottom-left corner at (chunk_x * 50, 0, chunk_y * 50)
1 unit = 1 meter
Player height: 1.8 units
Entity heights: 1.0 - 2.0 units depending on type
Chunk size: 50x50 units (XZ plane)
```

### Build + Run Commands
```bash
# Start Rust backend
cd backend
cargo run --release

# Build Rust backend
cargo build --release

# Run tests
cargo test

# Unity (from command line)
# Build: use Unity's Build Settings (File → Build)
# For CI: unity -batchmode -buildTarget Win64 -executeMethod BuildScript.Build
```

---

## WORKFLOW FOR THE DEVELOPER

```
Step 1: Run Claude Code in the project root
  $ cd backrooms-survival
  $ claude

Step 2: Give Claude Code this file as context
  "Read CLAUDE_CODE_INSTRUCTIONS.md and ARCHITECTURE_V1.md"

Step 3: Ask Claude Code to implement Phase 1 (Rust backend)
  "Implement Phase 1: create the Rust project structure and IPC server"

Step 4: Test each phase before moving to the next
  "Run cargo build and cargo test to verify Phase 1"

Step 5: After Rust backend works, implement Unity C# scripts
  "Now implement Phase 2: create all Unity C# scripts"

Step 6: Integration
  "Create the integration test script from Phase 3"
```

---

## IMPORTANT CONSTRAINTS

1. **No client-side prediction** — backend is authoritative for all game state
2. **MessagePack for IPC and network** — JSON only for save files
3. **Deterministic chunk generation** — same seed + position = same chunk layout
4. **Trust model** — peers trust each other (co-op game, friends playing together)
5. **Chunk ownership by proximity** — 5 chunk radius from player position
6. **Disconnection = teleportation** — this is a feature, not a bug
7. **Prototype visuals** — use ProBuilder/simple geometry for chunks, placeholder materials
8. **Target: 20-50 concurrent players** for MVP, architecture supports 200+ post-launch