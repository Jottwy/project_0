> **CONGELADO — 2026-09-05.** Vivía en la raíz del repo como segundo juego de reglas en paralelo a
> `CLAUDE.md`. Dos ficheros de reglas en la raíz es un fichero de reglas de más: cuando se
> contradicen, gana el que el lector abra primero. Se hizo el diff regla a regla y esto salió:
>
> **Lo único que se conservó**, porque no estaba en `CLAUDE.md` y es específico y comprobable —
> ahora es la regla dura **13**: no emitir salida iterando `HashSet`/`HashMap` sin ordenar antes,
> `item_id`/`entity_id` estables, y test de conectividad desde el spawn al cambiar reglas de
> layout.
>
> **Lo que NO se conservó, y por qué:**
> - *Stack, Architecture:* están en [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md), con contratos y
>   al día. Aquí decían «Unity 6 + URP 17» y poco más.
> - *Validated systems* (RemotePlayers V0, Transform Sync V0, …): lista de junio, congelada desde
>   entonces. El estado real lo lleva [`docs/STATE.md`](../STATE.md) y el registro `DECISIONS.md`.
> - *Hard rules* de «do not break X»: nueve prohibiciones que repiten la regla 2 de `CLAUDE.md`
>   («cualquier cambio que contradiga un ADR: PARA y pregunta») sin decir contra qué ADR se mide.
>   La lista de lo intocable, con su porqué y su cifra, está en `STATE.md` → «NO tocar».
> - *La regla de `MaterialHelper`:* el render dejó de ser Built-in en ADR-065 (URP, 2026-08-11).
> - *Rust validation* (`cargo test` + `cargo build --release`): lo cubre el gate de commit
>   (`tools/dev/validate-scope.ps1`) con fmt, clippy `-D warnings` y test. Y `cargo build
>   --release` NO despliega el backend — hay que copiar a `Builds/Backend/`, lo cual costó tres
>   playtests aprenderlo; dejarlo escrito como «validación» era una trampa.
> - *Unity validation* y *Final response required:* los cubren las skills `/auditar` y
>   `/checkpoint`, que además sí pueden rechazar el cierre.

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
