---
paths:
  - "Assets/_Migration/STPIntegration/RemoteAvatar/**"
---

# Convención: campo tipado en pose relay + hook de proxy (lado C#)

Este patrón se repite en ADR-020 (crouch) / ADR-021 (pitch) / ADR-022 (equipment) /
ADR-023 (held_item) / ADR-024 (hit_seq). Referencia canónica del piloto:
[docs/systems/damage-sync.md](../../docs/systems/damage-sync.md) y
`docs/DECISIONS.md` (`## ADR-024`).

## Cuándo aplica
Cualquier estado cosmético/host-relay de un peer remoto que deba verse en su
proxy 3P (no autoritativo, no toca combate/hitreg/inventario/stats).

## Reglas del patrón
1. **Nivel sostenido vs. evento transitorio, decide primero.**
   - Estado SOSTENIDO (crouch/pitch/equipment/held_item): se lee como NIVEL en
     cada pose.
   - Evento TRANSITORIO RE-DISPARABLE (hit_seq): se modela como CONTADOR
     MONOTÓNICO (`u8` wrapping) que viaja como nivel en cada pose — NUNCA como
     flanco one-shot ni reusando el canal escalar `animation` (ADR-011 lo
     acota a una sola acción transitoria; colisiona con pickup).
2. **Origen de la señal = el evento nativo correcto de STP, nunca sondeo de
   estado.** Ej.: `IHealthManager.DamageReceived` (daño real), NUNCA sondear
   `Health` (el `StatInterpolator` de ADR-009 lo escribe silenciosamente vía
   `SetHealthSilent`, sin evento → sondear produce falsos positivos en
   reconciliación).
3. **`PlayerPoseTransmitter` solo REPORTA** (rule #3 del proyecto): nunca
   aplica el estado localmente; el efecto local ya lo maneja STP nativo.
4. **Hook de proxy nuevo, espejo de uno existente** (`ProxyCrouchHook` para
   change-detect por-vista, `ProxyPitchHook` para rotación de bones por
   nombre en rig Generic). Cachea bones/refs en `Awake`/`OnEnable`; guard
   no-op si falta el bone. `OnEnable` re-arma el estado para reuso de pool.
   Sentinela (`int.MinValue` o equivalente) para que el primer sample tras
   spawn/join NUNCA dispare el efecto — evita falsos triggers en late-joiner.
5. **Reset en Acquire/Release** del pool (`RemotePlayerView`) — el campo
   vuelve a su default al reciclar el proxy.
6. **Removable por diseño**: el hook debe poder borrarse sin tocar red — si
   se borra el archivo, el proxy pierde el efecto visual y nada más rompe.
7. **Cableado durable vía el builder** (`RemoteAvatarPrefabBuilder.WireXxxHook`,
   mirror del método existente más parecido), idempotente, y requiere
   **re-bake del prefab** (`RemotePlayerAvatar.prefab`) antes de que el hook
   surta efecto en juego — no basta con el compile.
8. **Ortogonalidad explícita**: nunca pises otro campo del mismo pose relay
   (crouch/pitch/equipment/held_item/hit_seq deben poder coexistir en el
   mismo frame sin conflicto).

## Al replicar este patrón a otro sistema
Copia la lista de comprobación anterior punto por punto — decide primero si
es nivel-sostenido o evento-transitorio (paso 1), identifica el evento nativo
correcto (paso 2), y no olvides el bump de `WIRE_SCHEMA_VERSION` correspondiente
del lado Rust (ver `pose-relay-wire-rust.md`).
