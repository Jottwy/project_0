# docs/INDEX.md — Índice de documentación

> Mapa de DOCUMENTOS: qué fichero existe y para qué. Una línea por entrada, sin resúmenes.
> El mapa de CONTRATOS es [ARCHITECTURE.md](ARCHITECTURE.md).

## Fuente de verdad (en este orden al iniciar sesión)
1. [STATE.md](STATE.md) — estado vivo. Tope 200 líneas / 20 KB.
2. [ARCHITECTURE.md](ARCHITECTURE.md) — contratos y fronteras, con punteros.
3. [DECISIONS-INDEX.md](DECISIONS-INDEX.md) — índice de los ADR. Nunca leas `DECISIONS.md` entero.
4. [DECISIONS.md](DECISIONS.md) — ES LEY, append-only. Se lee por `grep '## ADR-NNN'` + offset.
5. [CONVENTIONS.md](CONVENTIONS.md) — convenciones C# / Rust / protocolo / git.

## Histórico
- [SESSION-LOG.md](SESSION-LOG.md) — diario anterior, verbatim.

## Arquitectura y red
- [architecture/NETWORKING_AND_SESSION_ARCHITECTURE.md](architecture/NETWORKING_AND_SESSION_ARCHITECTURE.md) — entrada.
- [architecture/SESSION_LIFECYCLE.md](architecture/SESSION_LIFECYCLE.md) — ocho fases y teardown.
- [architecture/SESSION_INVARIANTS.md](architecture/SESSION_INVARIANTS.md) — 15 invariantes de sesión.
- [architecture/NETWORKING_INVARIANTS.md](architecture/NETWORKING_INVARIANTS.md) — invariantes de red.
- [NETWORK_ARCHITECTURE_CURRENT.md](NETWORK_ARCHITECTURE_CURRENT.md) — transporte y roles.
- [SERVER_BROWSER.md](SERVER_BROWSER.md) — navegador por Steam; §14 primero.

## Sistemas
- [systems/ipc-wire-schema.md](systems/ipc-wire-schema.md) — changelog del wire. LEY.
- [systems/authored-rooms.md](systems/authored-rooms.md) — salas autoradas, punta a punta.
- [systems/perf-baseline.md](systems/perf-baseline.md) — rendimiento medido (backend).
- [perf/PERF_AUDIT_v1.md](perf/PERF_AUDIT_v1.md) — cliente 09-10: frametime/GC/VRAM.
- [systems/vendor-patches.md](systems/vendor-patches.md) — qué se pierde al reimportar el vendor.
- [systems/reverb-mixer.md](systems/reverb-mixer.md) — reverb por zona.
- [AUDIO-PROPAGATION-ROADMAP.md](AUDIO-PROPAGATION-ROADMAP.md) — PROPUESTO: eco y difracción.
- [systems/damage-sync.md](systems/damage-sync.md) — hit-reaction (ADR-024).

## Mundo (WorldGen3)
- [WG3-ALPHA1-ROADMAP.md](WG3-ALPHA1-ROADMAP.md) — **contrato vigente**: WG3 v1 = Alpha 1.
- [WG3-ROADMAP.md](WG3-ROADMAP.md) — plan de WG3 y frentes abiertos.
- [WORLDGEN3-BRIEF.md](WORLDGEN3-BRIEF.md) — resumen del sistema.
- [VERTICALITY-ROADMAP.md](VERTICALITY-ROADMAP.md) — geometrías verticales.
- [PLAN-PLANTAS-ALTAS.md](PLAN-PLANTAS-ALTAS.md) — poblar las plantas altas.
- [ROOMS-ROADMAP.md](ROOMS-ROADMAP.md) — salas autoradas.
- [LEVEL4-ROADMAP.md](LEVEL4-ROADMAP.md) — incursiones (ADR-093).
- [STRUCTURES.md](STRUCTURES.md) — estructuras del mundo.
- [LIGHTING-STUDY.md](LIGHTING-STUDY.md) — luz: estado, causas, Unity 6.7.

## Juego y contenido
- [GAME-LOOP-GDD.md](GAME-LOOP-GDD.md) — el loop y sus bloqueantes.
- [FARMING-ROADMAP.md](FARMING-ROADMAP.md) — farmeo de metal y estantería.
- [INVENTORY-ROADMAP.md](INVENTORY-ROADMAP.md)
- [MAPPING-ROADMAP.md](MAPPING-ROADMAP.md) — PROPUESTO: mapas en papel dibujados de memoria.
- [MAPPING-PROTOTYPE.md](MAPPING-PROTOTYPE.md) — PLAN: prototipado P0–P2 del mapeado; P0.1 (recuerdo) detallado.
- [FACELING-ROADMAP.md](FACELING-ROADMAP.md) — facelings.
- [ASSET-SHOPPING-LIST.md](ASSET-SHOPPING-LIST.md) — arte por comprar.
- [reference/asset-packs.md](reference/asset-packs.md) — packs importados y sus trampas.

## Escalado, deuda y auditorías
- [SCALING-ROADMAP.md](SCALING-ROADMAP.md) — E0–E5 y el calendario de hitos.
- [DEBT-ROADMAP.md](DEBT-ROADMAP.md) — 68 ítems con plan por ítem.
- [AUDIT-2026-08-28.md](AUDIT-2026-08-28.md) — bugs, registro vivo por append.

## Herramientas
- [DEV-ENVIRONMENT.md](DEV-ENVIRONMENT.md) — rutas, comandos y trampas mudas.
- [EDITOR-MENUS.md](EDITOR-MENUS.md) — menús del editor; léelo antes de un bake.
- [web/README.md](web/README.md) — artifacts publicados y su sincronización.

## Congelado (análisis útil, estado NO vigente)
- [archive/](archive/) — desde 2026-09-05 TODO lo congelado vive aquí, cada fichero con cabecera
  «CONGELADO + fecha + qué lo sucede». · [measurements/](measurements/)

> Cada roadmap lleva cabecera **VIGENTE / COMPLETADO / CONGELADO + fecha** en su primera línea.
