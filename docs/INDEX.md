# docs/INDEX.md — Índice de documentación

> Punto de entrada. Enlaza, no duplica — cada dato vive en un solo sitio.

## Fuente de verdad (leer en este orden al iniciar sesión)
1. [STATE.md](STATE.md) — estado vivo: qué está hecho, qué sigue, riesgos abiertos. Léelo SIEMPRE primero.
2. [ARCHITECTURE.md](ARCHITECTURE.md) — arquitectura validada, capas y contratos.
3. [DECISIONS.md](DECISIONS.md) — registro ADR completo. ES LEY, append-only.
4. [CONVENTIONS.md](CONVENTIONS.md) — convenciones C# / Rust / protocolo / git.

## Histórico (NO es fuente de verdad del estado actual)
- [SESSION-LOG.md](SESSION-LOG.md) — las sesiones anteriores de `STATE.md`, movidas VERBATIM el
  2026-08-04 (20 entradas, 2026-08-03 → 2026-07-07). Se sacaron porque `STATE.md` es lectura
  obligatoria en cada arranque y el diario era el 55 % de su peso. Consúltalo para saber **por qué**
  algo quedó como quedó; para saber **cómo está hoy**, `STATE.md`.

## ADRs
- [adr/INDEX.md](adr/INDEX.md) — índice de los ficheros ADR individuales; el registro completo sigue en `DECISIONS.md`.

## Herramientas
- [EDITOR-MENUS.md](EDITOR-MENUS.md) — qué hace cada una de las 22 entradas de menú del Editor,
  cuáles son idempotentes y cuáles rehacen assets. Léelo antes de ejecutar un bake.
- [DEV-ENVIRONMENT.md](DEV-ENVIRONMENT.md) — rutas de esta máquina, comandos exactos de tests
  EditMode / build de desarrollo / playtest, y las trampas de herramienta que NO dan error
  (`Collider.bounds` sin sincronizar en EditMode, banker's rounding de `Mathf.RoundToInt`,
  arrays por `zone_kind` que sirven la última entrada, freshness del arnés). Léelo antes de
  pelearte con un resultado absurdo.

## Sistemas (documentación operativa por subsistema)
- [systems/damage-sync.md](systems/damage-sync.md) — [G] Player Damage Sync (ADR-024): hit-reaction cosmético `hit_seq` en la pose relay.
- [systems/ipc-wire-schema.md](systems/ipc-wire-schema.md) — changelog del wire schema IPC/P2P v2→v19 (ADR-009 y siguientes): qué añadió cada bump y cómo degrada la versión anterior. El número vive en `WIRE_SCHEMA_VERSION` (`backend/src/ipc/server.rs`); este doc es el changelog.
- [systems/perf-baseline.md](systems/perf-baseline.md) — base de rendimiento MEDIDA (2026-08-14): el relay de los cinco rosters completos a 10 Hz sin filtro ni delta es el cuello, no el render. 1170 KB/s POR PEER con una base de 1000 piezas. Sonda reproducible en `backend/src/network/roster.rs` (`roster_relay_cost`, `#[ignore]`). Empieza aquí antes de optimizar nada.
- [systems/vendor-patches.md](systems/vendor-patches.md) — inventario de TODO lo que este proyecto escribió dentro de `Assets/PolymindGames/` (6 familias, 13 ficheros): qué se pierde con cada reimport del `.unitypackage`, con qué marca se detecta y cuál es la cura. `CheckRegressionChecklist.ps1` lo comprueba solo. Empieza aquí después de reimportar el vendor.
- [systems/reverb-mixer.md](systems/reverb-mixer.md) — reverb por zona: los 7 parámetros que `ReverbMixerDriver` escribe en `FPS_AudioMixer` y cómo rehacerlos. El mixer es del VENDOR: un reimport se lleva el efecto y el reverb se apaga en silencio — empieza aquí si dejó de sonar.

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
- [legacy/CLAUDE_CODE_INSTRUCTIONS.md](legacy/CLAUDE_CODE_INSTRUCTIONS.md) — guía inicial de implementación, deprecada y conservada solo como histórico.
