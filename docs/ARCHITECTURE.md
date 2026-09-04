# ARCHITECTURE.md — Arquitectura validada

> Mapa de CONTRATOS: qué sistema, qué invariante, y **dónde** está escrito. Punteros, no prosa —
> el detalle vive en el documento que se enlaza y la ley en `DECISIONS.md`. El mapa de DOCUMENTOS
> es [INDEX.md](INDEX.md).
>
> Hasta el 2026-09-04 este fichero decía «(rellenar al validar ADR-003 y ADR-004)» con 138 ADR ya
> escritos, y era la primera lectura del agente `auditor-arquitectura`: se auditaba contra una
> plantilla en blanco.

## Capas
1. **Cliente Unity 6 / URP 17 Forward+** — presentación, predicción local, malla de chunk. Desde
   ADR-065 el render es URP de verdad: un magenta significa shader Built-in sin convertir.
2. **Protocolo cliente↔servidor** — UDP propio con framing propio, más un relay opcional (ADR-117).
3. **Servidor Rust** — autoridad de mundo, entidades, loot y persistencia.
4. **Almacenamiento** — guardado por sesión en el host; sin base de datos.

## Sistemas núcleo (tocarlos exige ADR + auditoría, regla dura #7)
| Sistema | Contrato en | Ley |
|---|---|---|
| Wire cliente↔servidor | [systems/ipc-wire-schema.md](systems/ipc-wire-schema.md) — changelog autoritativo | ADR-009 y siguientes |
| Ciclo de vida de sesión | [architecture/SESSION_INVARIANTS.md](architecture/SESSION_INVARIANTS.md) (15 invariantes) y [SESSION_LIFECYCLE.md](architecture/SESSION_LIFECYCLE.md) (8 fases) | ADR-111 y ss. |
| Red y replicación | [architecture/NETWORKING_INVARIANTS.md](architecture/NETWORKING_INVARIANTS.md) · [NETWORK_ARCHITECTURE_CURRENT.md](NETWORK_ARCHITECTURE_CURRENT.md) | ADR-113, ADR-117 |
| Generación del mundo (WorldGen3) | [WORLDGEN3-BRIEF.md](WORLDGEN3-BRIEF.md) · [WG3-ROADMAP.md](WG3-ROADMAP.md) | ADR-095 a ADR-127 |
| Salas autoradas y props | [systems/authored-rooms.md](systems/authored-rooms.md) | ADR-083 a ADR-086 |
| Construcción y territorio (STP) | [systems/vendor-patches.md](systems/vendor-patches.md) | ADR-081 |
| Persistencia y saqueo | — (vive en `DECISIONS.md`) | ADR-032, ADR-115 |
| Escalado | [SCALING-ROADMAP.md](SCALING-ROADMAP.md) — etapas E0–E5 | ADR-073 |

## Fronteras que no se cruzan
- **El wire es un contrato de dos puntas.** `WIRE_SCHEMA_VERSION` (`backend/src/ipc/server.rs`) y
  `WireSchema.Expected` (`Assets/Scripts/Network/WireSchema.cs`) se bumpean **en el mismo commit**:
  desde ADR-061, bumpear sólo uno deja el juego inarrancable, no da un warning.
- **La autoridad es del servidor.** Hoy hay tres huecos abiertos y están listados como riesgo en
  [STATE.md](STATE.md); no son diseño, son deuda.
- **El vendor (`Assets/PolymindGames/`) no se edita.** `PolymindGames.asmdef` no puede referenciar
  `Assembly-CSharp`: la salida es hook externo o corregir después, nunca una guarda dentro del método.
- **WorldGen3 manda sobre WorldGen2.** El mundo servido sale del PLAN (`plan_region`), no de
  `compose_region` (ADR-100). `grid_gen` sigue vivo **sólo** como rejilla de navegación de criaturas.
- **La celda cambia de tamaño al cruzar de sistema**: 2,5 m en `grid_gen`, 0,5 m en WG3. Toda
  constante heredada cambia de significado, y el comentario que la acompaña deja de ser cierto.

## Presupuestos medidos (no objetivos)
- **Red, E0 cerrada (2026-08-15):** 35,8 → 10,4 Mbps en reposo y 12,5 en actividad, 3,4× de mejora.
  El cuello medido es el relay de rosters, no el render — ver [systems/perf-baseline.md](systems/perf-baseline.md).
- **Cliente:** una región de WG3 son del orden de 3 000 GameObjects con collider; el fundido por
  chunk sigue pendiente y es el día 3 del contrato de cierre.
- **Arranque de sesión:** 36 KB entre `CLAUDE.md`, `INDEX.md`, `STATE.md` y `DECISIONS-INDEX.md`.
  Lo comprueba `tools/dev/CheckStateBudget.py`.
