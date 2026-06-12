# Phase 3.0D-BACKEND — Validation

## Required commands

Run from backend root:

```bash
cargo fmt --check
cargo test
cargo build --release
```

All must pass.

---

## Required runtime/log validation

After build, the backend should emit V30D logs when run with seed `7778`.

Required log patterns:

```text
MPTRACE step=V30D event=true_multilayer_columns_enabled enabled=true
MPTRACE step=V30D event=multilayer_band_counts
MPTRACE step=V30D event=spawn_volume_valid
MPTRACE step=V30D event=vertical_access_counts
MPTRACE step=V30D event=multilayer_artifact_check
```

Expected semantic meaning:

```text
true_multilayer_columns_enabled enabled=true
```

Confirms Phase 3.0D backend path is active.

```text
multilayer_band_counts columns=... upper_cells=... main_cells=... lower_cells=...
```

Confirms upper/main/lower architecture exists as real generated data.

```text
spawn_volume_valid floor=true ceiling=true not_void=true nearby_architecture=true
```

Confirms spawn is not void.

```text
vertical_access_counts shafts=... atriums=... ramps=... sealed=...
```

Confirms vertical relationships are explicit and bounded.

```text
multilayer_artifact_check checkerboard=false floating_lattice=false
```

Confirms the backend did not reintroduce full-world lattice/checkerboard artifacts.

---

## Required test coverage

Backend tests must prove:

1. Seed `42` generates real upper/main/lower architecture.
2. Seed `7778` generates real upper/main/lower architecture.
3. Upper bands are not pure metadata.
4. Lower bands are not pure metadata.
5. Upper/lower bands are not all Solid.
6. Upper/lower bands are not all open.
7. Upper/lower bands do not form global checkerboard/lattice.
8. Vertical accesses are explicit and bounded.
9. Spawn seed `42` has floor, ceiling, not void, nearby architecture.
10. Spawn seed `7778` has floor, ceiling, not void, nearby architecture.
11. RubikGrid showcase still has volumetric behavior.
12. Existing 3.0C tests still pass.

---

## Acceptance checklist

Backend-only Phase 3.0D is accepted if:

```text
[ ] cargo fmt --check passes
[ ] cargo test passes
[ ] cargo build --release passes
[ ] no Unity files were modified
[ ] normal Level 0 columns generate Y+1 / Y0 / Y-1 real architecture
[ ] upper/lower bands are not pure metadata
[ ] upper/lower bands are not global lattice/checkerboard
[ ] vertical access nodes are explicit and bounded
[ ] spawn is valid for seed 42
[ ] spawn is valid for seed 7778
[ ] RubikGrid behavior does not regress
[ ] final report identifies remaining Unity renderer work
```

---

## Non-acceptance examples

Reject the implementation if:

```text
upper/lower bands are hidden metadata only
upper/lower bands are all Solid
upper/lower bands are fully open everywhere
checkerboard/lattice returns
spawn remains in void
spawn is fixed only in Unity
Unity files are modified
tests are skipped
cargo build fails
RubikGrid breaks
networking behavior changes
```

---

## Post-backend next phase

After backend acceptance, the next separate phase should be:

```text
Phase 3.0D-UNITY — Render True Multi-Layer Level 0 Columns
```

That phase will update Unity to render:

```text
upper architecture
main architecture
lower architecture
explicit shafts/atriums/vertical access
```

Do not start Unity work during backend-only Phase 3.0D.
