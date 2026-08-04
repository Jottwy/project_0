# docs/INDEX.md — Índice de documentación

> Punto de entrada. Enlaza, no duplica — cada dato vive en un solo sitio.

## Fuente de verdad (leer en este orden al iniciar sesión)
1. [STATE.md](STATE.md) — estado vivo: qué está hecho, qué sigue, riesgos abiertos. Léelo SIEMPRE primero.
2. [ARCHITECTURE.md](ARCHITECTURE.md) — arquitectura validada, capas y contratos.
3. [DECISIONS.md](DECISIONS.md) — registro ADR completo (ADR-001..049 + enmiendas). ES LEY, append-only.
4. [CONVENTIONS.md](CONVENTIONS.md) — convenciones C# / Rust / protocolo / git.

## ADRs
- [adr/](adr/) — ADRs nuevos a partir de hoy (stub o copia enlazando a `DECISIONS.md`). Los ADR-001..032 existentes siguen solo en `DECISIONS.md` (no movidos, ver [adr/README.md](adr/README.md)).

## Herramientas
- [EDITOR-MENUS.md](EDITOR-MENUS.md) — qué hace cada una de las 22 entradas de menú del Editor,
  cuáles son idempotentes y cuáles rehacen assets. Léelo antes de ejecutar un bake.

## Sistemas (documentación operativa por subsistema)
- [systems/damage-sync.md](systems/damage-sync.md) — [G] Player Damage Sync (ADR-024): hit-reaction cosmético `hit_seq` en la pose relay.
- [systems/ipc-wire-schema.md](systems/ipc-wire-schema.md) — changelog del wire schema IPC/P2P v2→v19 (ADR-009 y siguientes): qué añadió cada bump y cómo degrada la versión anterior. El número vive en `WIRE_SCHEMA_VERSION` (`backend/src/ipc/server.rs`); este doc es el changelog.

## Limpieza y refactor
- [AUDIT-2026-08-03.md](AUDIT-2026-08-03.md) — barrido de auditoría vigente: qué se limpió, qué
  queda por tier, y qué está bloqueado esperando decisión humana. Empieza aquí antes de "mejorar"
  nada por tu cuenta.

## Otros documentos de referencia
- [STRUCTURES.md](STRUCTURES.md)
- [REMOTEPLAYERS_GATE.md](REMOTEPLAYERS_GATE.md)

> Los tres siguientes llevan `CURRENT` en el nombre pero están **congelados en 2026-06-08**. Se
> mantienen porque su análisis sigue siendo útil; su inventario de ficheros y su estado NO lo son.
- [NETWORK_ARCHITECTURE_CURRENT.md](NETWORK_ARCHITECTURE_CURRENT.md)
- [STABILITY_AUDIT_CURRENT.md](STABILITY_AUDIT_CURRENT.md)
- [ARCHITECTURE_RISK_REVIEW.md](ARCHITECTURE_RISK_REVIEW.md)

## Archivo
- [archive/](archive/) — documentos retirados, con cabecera que dice por qué. No son fuente de verdad.
