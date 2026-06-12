# CONVENTIONS.md

## C# (Unity)
- Identificadores en inglés. Namespaces por sistema: `BSMMO.WorldGen`, `BSMMO.Net`, `BSMMO.Core`.
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
