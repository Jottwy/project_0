# Phase 3.0D-BACKEND — True Multi-Layer Level 0 Columns V0

## Project

BackroomsSurvivalMMO backend only.

Working directory:

```text
J:\Unity\BackroomsSurvivalMMO\backend
```

This document defines the backend-only target for Phase 3.0D.

Unity rendering is intentionally out of scope for this phase.

---

## Current validated state

Phase 3.0C-FIX is technically green.

Known state:

* Backend compiles.
* Backend tests pass.
* Unity C# compiles.
* Seed `7778` runs the current backend.
* V30CFIX logs confirm:

  * mixed Level 0 semantics;
  * low main-band height;
  * no stray upper/lower band artifacts;
  * no legacy VISFIX;
  * no legacy interlayer renderer.

However, Phase 3.0C-FIX solved the wrong final problem.

It cleaned and stabilized the Level 0 adapter, but it still behaves mainly like a flat Y0 world with hidden or metadata-heavy upper/lower bands.

---

## Problem

The current backend does not yet generate true 3D multi-layer Backrooms architecture.

Observed issues:

* Normal Level 0 chunks still read as mostly flat.
* Y+1 and Y-1 are not consistently generated as real architecture.
* Upper/lower bands are too close to hidden metadata / solid hint mode.
* There is no persistent feeling of floors above and below the player.
* Player can spawn in or near void-like/unreadable areas.
* Some generated areas still feel like holes/slabs rather than coherent spatial architecture.
* The world does not yet behave like a Rubik-cube-style stacked Backrooms volume.

The target is not decorative holes.

The target is real architectural occupancy above, at, and below the player.

---

## High-level target

Implement backend-only Phase 3.0D-BACKEND:

```text
True Multi-Layer Level 0 Columns V0
```

The Rust backend must generate real 3D architectural data before Unity rendering is touched.

Every normal Level 0 `VolumetricColumn` should contain coherent architecture across at least three bands:

```text
Y+1 = upper architecture
Y0  = main playable Level 0
Y-1 = lower/service architecture
```

The upper/lower bands do not need to be fully gameplay-walkable yet, but they must exist as real deterministic data.

---

## Core design rule

The player should feel inside a stacked Backrooms building volume, not standing on a flat plane.

Therefore:

* there should usually be architecture above the player;
* there should usually be architecture below the player;
* ceilings should usually exist over main-band cells;
* floors should usually exist under main-band cells;
* vertical openings must be intentional and rare;
* voids must be explicit, bounded, and tagged.

---

## Required band model

### Main band Y0

Main band remains the primary playable Level 0.

It must preserve:

* readable starter cluster;
* low-ceiling Backrooms feel;
* long corridors;
* rooms;
* intersections;
* T-junctions;
* dead ends;
* safe spawn area;
* danger zones away from spawn;
* support cores/pillars where appropriate.

Y0 is still the gameplay focus.

Do not sacrifice Level 0 readability to make the whole map chaotic.

---

### Upper band Y+1

Upper band must become real generated architecture.

Valid upper-band features include:

* upper rooms;
* upper corridors;
* false-ceiling spaces;
* sealed overhead spaces;
* blocked offices;
* upper service corridors;
* overhead support volumes;
* dark inaccessible upper chambers;
* occasional visible upper-floor hints through shafts/atriums/broken ceilings.

Upper band must not be:

* pure hidden metadata;
* all Solid;
* global checkerboard;
* full-world lattice;
* random floating boxes;
* open everywhere.

It should be coherent and sparse enough to avoid noise, but real enough that the world is spatially stacked.

---

### Lower band Y-1

Lower band must become real generated architecture.

Valid lower-band features include:

* underfloor service spaces;
* lower corridors;
* maintenance rooms;
* storage/service rooms;
* red/danger pockets;
* lower support cores;
* sealed utility voids;
* occasional visible lower rooms through shafts/atriums/broken floors.

Lower band must not be:

* pure hidden metadata;
* all Solid;
* global checkerboard;
* full-world lattice;
* open everywhere;
* randomly dangerous near spawn.

Lower danger/red pockets are allowed, but must be depth-gated, bounded, and not corrupt the starter area.

---

## Spatial coherence rules

Upper/lower layouts must be generated from:

* main-band cell layout;
* chunk/macrostructure type;
* deterministic seed;
* local adjacency;
* explicit vertical access rules.

They must not be random per-cell noise.

Recommended logic:

* corridors in Y0 may produce overhead service corridors or lower service tunnels;
* rooms in Y0 may produce sealed upper chambers, false-ceiling spaces, or lower maintenance voids;
* pillar/support cells may continue vertically as support cores;
* intersections may occasionally create shafts or atrium candidates;
* danger/deep macrostructures may create lower red pockets;
* spawn/starter structures must avoid vertical hazards.

---

## Forbidden generation patterns

Do not reintroduce:

* full-world upper/lower lattice;
* checkerboard occupancy;
* floating dark boxes;
* every upper/lower cell open;
* every upper/lower cell hidden Solid;
* random holes through floors;
* random ceiling voids;
* spawn near pits, shafts, atriums, or edge leaks.

The world must be multi-layered, not noisy.

---

## Required vertical relationship semantics

Vertical relationships must be explicit.

Use existing `VerticalAccessNode` if sufficient. Add backend-only fields only if the existing model cannot express the required information.

Required relationship categories:

```text
sealed_above
sealed_below
shared_floor_ceiling
shaft_opening
atrium_void
service_ramp_placeholder
broken_floor_placeholder
false_ceiling_access
support_core_continuation
```

Definitions:

### sealed_above

There is architecture above, but no passage upward.

### sealed_below

There is architecture below, but no passage downward.

### shared_floor_ceiling

Main-band ceiling/floor relationship is structurally shared with an adjacent band.

### shaft_opening

A bounded vertical void connecting bands.

Must be rare and explicit.

### atrium_void

A larger vertical void, usually in special macrostructures.

Must be bounded.

### service_ramp_placeholder

A planned future traversal connection. Data exists, but gameplay traversal may not yet.

### broken_floor_placeholder

A visible or future opening downward, but not random.

### false_ceiling_access

A controlled ceiling access marker.

### support_core_continuation

Pillar/support structure continues vertically across bands.

---

## Spawn validity

Spawn must be fixed in backend, not hacked in Unity.

A valid spawn must satisfy:

```text
inside_main_band=true
walkable=true
floor=true
ceiling=true
not_void=true
not_shaft=true
not_atrium=true
not_pit=true
not_danger=true
not_blocked=true
not_edge_leak=true
nearby_architecture=true
```

Spawn should land in a readable Level 0 space with walls/corridors/rooms nearby.

It must not appear in empty space, open void, black horizon, pit placeholder, or unresolved cell.

Seeds that must be validated:

```text
42
7778
```

---

## Backend Definition of Done

The backend phase is done only when all are true:

1. Normal Level 0 columns generate real upper/main/lower architecture.
2. Y+1 is not pure metadata and not all Solid.
3. Y-1 is not pure metadata and not all Solid.
4. Upper/lower bands are coherent and deterministic.
5. Upper/lower bands do not create full-world checkerboard/lattice artifacts.
6. Vertical openings are explicit, bounded, and counted.
7. Spawn validation passes for seeds 42 and 7778.
8. RubikGrid/3.0C behavior does not regress.
9. `cargo fmt --check` passes.
10. `cargo test` passes.
11. `cargo build --release` passes.
12. Final report explains changed files, tests, logs, risks, and remaining Unity work.

---

## Out of scope

Do not implement:

* Unity rendering changes;
* final materials;
* final lighting;
* gravity;
* falling;
* fall damage;
* collision unification;
* player movement rewrite;
* inventory;
* crafting;
* combat;
* AI;
* networking changes;
* host/join changes;
* RemotePlayers changes;
* UI changes;
* save/load;
* Steam;
* persistence.

This phase is backend spatial truth only.

---

## Expected final backend result

After Phase 3.0D-BACKEND, Unity may still not render the world perfectly.

That is acceptable.

The important result is that the backend now contains real data for:

```text
upper architecture
main Level 0 architecture
lower architecture
explicit vertical relationships
valid spawn volume
bounded vertical accesses
artifact-free multilayer occupancy
```

Unity can then be updated in a separate phase to render that data correctly.
