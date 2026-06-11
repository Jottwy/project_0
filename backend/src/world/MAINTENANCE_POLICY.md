# MAINTENANCE_POLICY — Documentation Update Rules

> This document defines **which doc files to update** and **how** when the codebase changes.
> Follow this policy every time you make a structural change. Keep docs in sync with code.

---

## Rule 0 — Fail Fast

If you cannot identify which doc file to update from the table below, stop and update
this policy first. Undocumented architecture accumulates faster than it gets removed.

---

## Trigger → Update Table

| Change | Files to update |
|---|---|
| New public method on `World` | `README.md` → Public API Surface table |
| New `WorldConfig` field | `WORLD_CONTEXT.md` §2 (Core Data Model) |
| New `Chunk` / `ChunkLayoutV1` field | `WORLD_CONTEXT.md` §2 |
| New cell flag (`CELL_*`) | `WORLD_CONTEXT.md` §2 |
| New edge kind (`EDGE_KIND_*`) | `WORLD_CONTEXT.md` §2 |
| New zone kind (`ZONE_*`) | `WORLD_CONTEXT.md` §2 |
| New floor/ceiling/light profile | `WORLD_CONTEXT.md` §2 |
| New V30A flag (`V30A_*`) | `WORLD_CONTEXT.md` §2 + `architecture/README.md` |
| Change to chunk displacement mechanic | `WORLD_CONTEXT.md` §5 |
| Change to entity AI (new state, new constants) | `WORLD_CONTEXT.md` §6 |
| Change to IPC view format | `WORLD_CONTEXT.md` §7 |
| Change to volumetric grid or its rollout flag | `WORLD_CONTEXT.md` §8 |
| New networking method | `WORLD_CONTEXT.md` §9 |
| New/changed test that validates a contract | `WORLD_CONTEXT.md` §10 |
| New risk identified | `WORLD_CONTEXT.md` §11 |
| New structure type | `WORLD_CONTEXT.md` §12 + `levels/README.md` |
| New template (`TEMPLATE_*`) | `architecture/README.md` + `WORLD_CONTEXT.md §12` |
| New layout grammar type | `architecture/README.md` |
| Change to `chunk_seed` hash | `architecture/README.md` + **WARNING: breaks all seeds** |
| Change to `item_cell_blocked` rules | `architecture/README.md` |
| Change to `finalize_level0_edges` | `architecture/README.md` |
| New graph node kind | `graph/README.md` |
| New graph edge kind | `graph/README.md` |
| Phase 6.5+ verticality changes | `graph/README.md` (verticality.rs section) |
| New level added (`level_N/`) | `levels/README.md` (Adding a New Level section) |
| New Level 0 generation phase | `levels/README.md` (builder.rs section) |
| New external dependency (`crate::X`) | `README.md` → External Dependencies table |
| New `.claude/settings.local.json` permission | No doc update needed |

---

## Format Conventions

### Public API entries
```
world.method_name(params) → ReturnType   // one-line description
```

### External dependency entries
| Import path | What it provides |

### Risk entries (§11)
| Zone | Risk (LOW/MEDIUM/HIGH) | Free-text note |

### Contract entries (§10)
| Plain-English description of invariant | Test function name |

---

## Adding a New Subfolder

When you create a new `world/X/` folder:

1. Create `world/X/README.md` with sections:
   - **Purpose** (1–3 sentences)
   - **Files** (one subsection per file, public API + critical rules)
   - **Dependencies** (what it imports, what imports it)
   - **What Must Not Break** (bullet list of invariants)

2. Add a row to `README.md` Quick-Reference Map table.

3. Add a `See Also` link in `README.md`.

4. Add trigger rows for the new folder's changes to this policy table.

---

## When a Phase / Feature Completes

After shipping a feature phase (e.g., "Phase 3.1 — proven edges"):

1. Remove `(Phase X.Y)` qualifiers from the relevant doc sections — they become baseline.
2. Update `WORLD_CONTEXT.md §11` if the risk level changed.
3. Remove any `(currently false)` / `(planned)` notes that are now live.

---

## Validation Checklist (before PR)

- [ ] Every new public function documented in the appropriate README.
- [ ] Every new constant (cell flag, template, zone) added to `WORLD_CONTEXT.md §2`.
- [ ] Every new tested invariant added to `WORLD_CONTEXT.md §10`.
- [ ] No `(Phase X.Y)` label left in docs for a completed feature.
- [ ] `README.md` Public API Surface matches the actual `impl World` in `mod.rs`.
