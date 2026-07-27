# docs/INDEX.md — Índice de documentación

> Punto de entrada. Enlaza, no duplica — cada dato vive en un solo sitio.

## Fuente de verdad (leer en este orden al iniciar sesión)
1. [STATE.md](STATE.md) — estado vivo: qué está hecho, qué sigue, riesgos abiertos. Léelo SIEMPRE primero.
2. [ARCHITECTURE.md](ARCHITECTURE.md) — arquitectura validada, capas y contratos.
3. [DECISIONS.md](DECISIONS.md) — registro ADR completo (ADR-001..032 + enmiendas). ES LEY, append-only.
4. [CONVENTIONS.md](CONVENTIONS.md) — convenciones C# / Rust / protocolo / git.

## ADRs
- [adr/](adr/) — ADRs nuevos a partir de hoy (stub o copia enlazando a `DECISIONS.md`). Los ADR-001..032 existentes siguen solo en `DECISIONS.md` (no movidos, ver [adr/README.md](adr/README.md)).

## Sistemas (documentación operativa por subsistema)
- [systems/damage-sync.md](systems/damage-sync.md) — [G] Player Damage Sync (ADR-024): hit-reaction cosmético `hit_seq` en la pose relay.

## Otros documentos de referencia
- [NETWORK_ARCHITECTURE_CURRENT.md](NETWORK_ARCHITECTURE_CURRENT.md)
- [STRUCTURES.md](STRUCTURES.md)
- [REMOTEPLAYERS_GATE.md](REMOTEPLAYERS_GATE.md)
- [SAFE_REFACTOR_PLAN.md](SAFE_REFACTOR_PLAN.md)
- [STABILITY_AUDIT_CURRENT.md](STABILITY_AUDIT_CURRENT.md)
- [ARCHITECTURE_RISK_REVIEW.md](ARCHITECTURE_RISK_REVIEW.md)
