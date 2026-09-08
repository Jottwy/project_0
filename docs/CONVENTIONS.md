# CONVENTIONS.md

## C# (Unity)
- Identificadores en inglés. Namespaces por sistema bajo la raíz `BackroomsSurvival.*`. Los que
  existen hoy, por volumen: `BackroomsSurvival.Net` (48 ficheros),
  `BackroomsSurvival.Migration.STPIntegration` (34), `BackroomsSurvival.Gameplay` (25),
  `BackroomsSurvival.Tests` (22), `BackroomsSurvival.EditorTools` (18), más los sub-namespaces
  de gameplay (`.GridWorld`, `.Building`, `.World`, `.Shaft`, `.Audio`, `.Chunks`) y
  `BackroomsSurvival.UI`.
  - Deuda anotada: conviven `BackroomsSurvival.EditorTools` (18) y `BackroomsSurvival.Editor` (2)
    para lo mismo. Unificar en `EditorTools` cuando toque; no es urgente.
  - Un namespace vacío no es gratis: mientras existió `BackroomsSurvival.Gameplay.Player` con un
    solo fichero muerto dentro, robaba el nombre simple `Player` a `PolymindGames.Player` y
    obligaba a un alias defensivo en `GridTestWorld.cs`.
- Nada de singletons nuevos sin justificación en el plan. Lógica de generación: pura y testeable, separada de MonoBehaviours.
- Trabajo pesado de worldgen: Jobs + Burst; cero allocs en hot path.
- **Viewmodel (ADR-077 enm. 2).** Todo renderer activo dentro de un wieldable (`Assets/Prefabs/Wieldables`,
  `Assets/Resources/Wieldables`) usa un shader de warp: `LitFieldOfView` (objetos), `LitFieldOfView_SSS`
  (piel) o `BR_UIWarp` (Canvas). Cada objeto de mano tiene DOS materiales, como el vendor
  (`X.mat` / `FP_X.mat`): `BR_X_Mat` en URP/Lit para pickup, icono y proxy; `BR_X_FP_Mat` para la mano,
  derivado del primero por `BackroomsViewmodelMaterials.BuildFirstPerson` (mismas texturas + `_MaskMap`
  reempaquetado). `_FOV`/`_FOVEnabled` nunca van en `Properties` de un material. Puerta: `ViewmodelWarpTests`.

## Rust
- tokio para async. `unsafe` prohibido salvo ADR que lo justifique.
- Errores con `thiserror`/`anyhow` según capa. Clippy en verde.

## Protocolo
- Schema versionado desde el día 1. Cambios de wire format = ADR.
- Determinismo: misma seed + misma versión ⇒ mismo chunk, en cliente y servidor.

## Formato de un ADR (`docs/DECISIONS.md`)
- Encabezado: `## ADR-NNN — Título (AAAA-MM-DD) — ESTADO`. Una enmienda es
  `## ADR-NNN — Enmienda N: título (fecha) — ESTADO`. Un encabezado puede cubrir varios números
  (`## ADR-121 y ADR-122 — Aprobación …`) y entonces el estado se aplica a todos ellos.
- **Supersesión entre ADRs distintos: una línea suelta en el cuerpo**, con este formato exacto:
  `Sustituye a: ADR-NNN` (o `Sustituida por:` / `Deroga:` / `Derogada por:` / `Restringe:` /
  `Supera a:`). Un número por línea; varias líneas si son varios.
  - Va en el CUERPO y no en el encabezado porque el encabezado ya carga título, fecha y estado, y
    porque una supersesión casi siempre se descubre DESPUÉS de escribir el ADR: entonces se declara
    en una enmienda nueva, que es lo único que la regla 11 (append-only) permite.
  - `tools/dev/GenDecisionsIndex.py` la lee y la publica en la fila del índice como `· sustituye a
    ADR-NNN`. Sin esa línea, el índice sólo sabe contar enmiendas DENTRO de un número y una
    supersesión entre ADRs distintos queda invisible para quien lee el índice al arrancar sesión.
- El índice se regenera y se COMPRUEBA (`--check`); el gate de commit lo vuelve a comprobar contra
  el índice de git (`--check --staged`). Un encabezado que el parser no sepa leer es rojo, no una
  omisión silenciosa.

## Git
- Commits atómicos: `feat(worldgen): …`, `fix(net): …`. Tag por hito validado: `v0.x-hito`.
- Staging siempre por rutas revisadas; prohibidos `git add -A/--all/.` y `git commit -a/--all` en sesiones concurrentes.

## Harness de desarrollo
- Validación escalonada y sensible al alcance: por edición sólo formato Rust; en cada `git commit`, las suites completas de las superficies que ese commit toca.
- **El gate de commit es `tools/dev/validate-scope.ps1`** y lo dispara el hook PreToolUse `.claude/hooks/pretool-guard.py` antes de que Bash ejecute el `git commit`. Rojo = el commit no se ejecuta. Cubre también el presupuesto de arranque y el índice de ADR, siempre, contra el índice de git.
- **El alcance del gate sale de `git diff --cached --name-only`, no del ledger de sesión.** El ledger (`.claude/.session-touched-<id>`) dice qué tocó esta sesión; el commit puede llevar más (rebase, resolución de conflicto, trabajo anterior) o menos, y no existe sin `session_id`. Lo que entra al repositorio es el índice.
- Los commits que no pasan por Claude (terminal, IDE) se cubren activando el hook versionado: `git config core.hooksPath .githooks`, una vez por clon. Llama al mismo script.
- El hook Stop es informativo y nunca bloquea; `/checkpoint` sigue siendo el cierre de sesión, no el guardián.
- CI Rust activo en GitHub para `main` y `migration/worldgraph-v1`; Unity queda fuera por licencia y C# se valida localmente mediante compile-check.
- Permisos locales: `cargo add` requiere aprobación humana siempre; `cargo run` solo se permite desde `backend/`.
