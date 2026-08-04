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

## Rust
- tokio para async. `unsafe` prohibido salvo ADR que lo justifique.
- Errores con `thiserror`/`anyhow` según capa. Clippy en verde.

## Protocolo
- Schema versionado desde el día 1. Cambios de wire format = ADR.
- Determinismo: misma seed + misma versión ⇒ mismo chunk, en cliente y servidor.

## Git
- Commits atómicos: `feat(worldgen): …`, `fix(net): …`. Tag por hito validado: `v0.x-hito`.
- Staging siempre por rutas revisadas; prohibidos `git add -A/--all/.` y `git commit -a/--all` en sesiones concurrentes.

## Harness de desarrollo
- Validación escalonada y sensible a las rutas tocadas por sesión: por edición solo formato Rust; al cierre, suites completas de las superficies Rust/C# afectadas.
- El hook Stop es informativo y nunca bloquea; `/checkpoint` es el gate que rechaza el commit cuando falla una validación aplicable.
- Si falta el ledger de sesión, se usa `git diff --name-only` declarando que el alcance puede ser impreciso.
