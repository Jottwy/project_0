# AGENTS.md

## Project
Backrooms Survival MMO.

## Stack
- Unity 6
- URP 17
- Rust backend
- Windows build

## Architecture
- Unity connects to local backend through IPC TCP 127.0.0.1:7777.
- Backends communicate through UDP P2P.
- World generation must be deterministic.
- Multiplayer world sync depends on stable world_seed, world_revision, chunk data, entity IDs and item IDs.

## Validated systems
- RemotePlayers V0
- Transform Sync V0
- Shared World Sync V0
- World Interaction Authority V0
- Procedural World Structures V0
- Level 0 V1
- MaterialHelper shader/material fix
- Windows backend packaging

## Hard rules
- Do not break deterministic world generation.
- Do not use unordered HashSet/HashMap iteration for deterministic output unless sorted before output.
- Do not break Shared World Sync.
- Do not break RemotePlayers.
- Do not break Transform Sync.
- Do not break World Interaction Authority.
- Do not modify networking protocol unless explicitly required.
- Do not modify MaterialHelper unless the task specifically requires material/shader changes.
- Do not do broad refactors unless explicitly requested.
- Keep changes small, isolated and reviewable.

## Rust validation
From backend directory, run:

```bash
cargo test
cargo build --release
```

## Unity validation
If Unity-side files are modified, explain:

- What scripts changed.
- What scene/prefab must be checked.
- How to validate host/joiner.
- How to confirm RemotePlayers=1.
- How to confirm same world_seed/world_revision.
- How to confirm same chunks/entities/items/structures.

## Procedural generation rules
- Preserve deterministic output from world_seed.
- Preserve stable item_id and entity_id generation.
- Preserve connectivity from spawn unless task explicitly changes layout rules.
- Add tests for connectivity and determinism when world generation changes.
- Prefer V0 implementation before complex systems.

## Final response required
At the end of every task, report:

- Changed files
- What changed
- Tests run
- Test result
- Risks
- Rollback notes
- Next recommended step
