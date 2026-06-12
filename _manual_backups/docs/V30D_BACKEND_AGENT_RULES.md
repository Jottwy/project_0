# Phase 3.0D-BACKEND — Agent Rules

## Scope

Backend-only task.

Working directory:

```text
J:\Unity\BackroomsSurvivalMMO\backend
```

Do not inspect or modify Unity files.

Allowed to edit only files under:

```text
src/
```

Allowed to read:

```text
docs/
src/
Cargo.toml
Cargo.lock
```

Allowed to run cargo commands from backend root.

---

## Primary files

Prefer working in these files:

```text
src/world/volumetric_grid.rs
src/world/generator.rs
src/world/mod.rs
```

---

## Secondary files

Only modify these if required:

```text
src/world/chunk.rs
src/game_loop.rs
src/ipc/mod.rs
```

Before modifying spawn logic, locate the authoritative spawn source with targeted grep only:

```bash
rg -n "spawn|Spawn|player.*position|initial.*position|start.*position" src
```

Do not perform a broad repository audit.

---

## Forbidden files / systems

Do not touch:

```text
Unity project files
Assets/
ProjectSettings/
Packages/
Builds/
```

Do not modify:

```text
networking behavior
host/join
RemotePlayers
Transform Sync
inventory
crafting
combat
AI
UI
materials
Unity PlayerController
Unity ChunkRenderer
Unity IPCMessages
```

Unity work is a later phase.

---

## Implementation strategy

Do not restart from scratch.

Continue from the current Phase 3.0C-FIX backend.

Preserve existing tests unless they are explicitly wrong because of the new true-multilayer target.

Do not delete 3.0C/RubikGrid behavior.

Do not revert to old 3.0A/VISFIX/interlayer debug systems.

Do not reintroduce:

```text
upper/lower full-world lattice
checkerboard occupancy
floating dark boxes
random holes
pure hidden hint-mode as the final state
```

The goal is controlled real upper/lower architecture.

---

## Required implementation shape

Implement backend logic that produces:

```text
VolumetricColumn
LayerBand Y+1
LayerBand Y0
LayerBand Y-1
VerticalAccessNode / explicit vertical relationship metadata
```

The output must represent real architecture in data, even if Unity does not yet render it perfectly.

---

## Determinism requirements

Generation must be deterministic by seed.

Avoid unordered iteration affecting generation output.

Avoid `HashMap` / `HashSet` nondeterministic iteration where output order matters.

Sort keys before producing final ordered output where needed.

Existing deterministic tests must continue to pass.

---

## Safety rules

Do not move gameplay authority to Unity.

Do not implement gravity.

Do not implement falling.

Do not implement fall damage.

Do not rewrite player movement.

Do not rewrite multiplayer.

Do not introduce new runtime dependencies unless absolutely necessary.

Keep changes reviewable.

---

## Logging requirements

Add V30D logs with stable, grep-friendly names:

```text
MPTRACE step=V30D event=true_multilayer_columns_enabled enabled=true
MPTRACE step=V30D event=multilayer_band_counts columns=... upper_cells=... main_cells=... lower_cells=...
MPTRACE step=V30D event=spawn_volume_valid floor=true ceiling=true not_void=true nearby_architecture=true
MPTRACE step=V30D event=vertical_access_counts shafts=... atriums=... ramps=... sealed=...
MPTRACE step=V30D event=multilayer_artifact_check checkerboard=false floating_lattice=false
```

Logs should be aggregate and order-independent.

Do not spam per-cell logs.

---

## Test requirements

Add or update backend tests for:

```text
seed_42_generates_real_multilayer_level0_columns
seed_7778_generates_real_multilayer_level0_columns
upper_lower_bands_are_real_architecture_not_pure_metadata
upper_lower_bands_do_not_form_lattice_or_checkerboard
vertical_access_nodes_are_explicit_and_bounded
spawn_seed_42_is_valid_main_band_volume
spawn_seed_7778_is_valid_main_band_volume
rubik_grid_showcase_does_not_regress
```

Test names may differ, but coverage must be equivalent.

---

## Final report format

Final report must include:

```text
Result
Changed files
What changed
Tests added/updated
Validation output
Seed 7778 band counts
Spawn validation status
Remaining Unity renderer work
Risks
Rollback notes
```

Do not commit unless explicitly requested.
