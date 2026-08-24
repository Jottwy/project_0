# LEVEL4-ROADMAP — Plan de implementación troceado de ADR-093

> Estado: plan aprobado pendiente de ejecución. Cada etapa es un diff ≤~300 líneas, con test y
> verificación propios, y entra **inerte** siempre que se pueda (regla aprendida de ADR-084: los
> cinco tramos entraron sin mover el mundo un byte). Orden estricto E0→E6; ninguna etapa depende de
> una posterior. Wire se toca UNA vez (E2).
>
> Prerrequisito declarado en STATE: playtest de ADR-088..092 (la IA que poblará esta región) ANTES
> de E5.

## Anclajes verificados en código (2026-08-24)

| Qué | Dónde |
|---|---|
| Entrada de generación dual | `backend/src/world/generator.rs:81` (`generate_chunk_layer`), `grid_gen/stitching.rs:37` |
| Tallado colisión 5 m | `backend/src/world/build_room_layout.rs:31` (`carve_into_layout`) |
| Tallado render/fantasma 2,5 m | `backend/src/world/grid_gen/build_rooms.rs:148` (`carve_into_grid`) |
| Manifiesto de salas (OnceLock) | `backend/src/world/grid_gen/room_manifest.rs:144` (`active_manifest`) |
| Zona por chunk | `backend/src/world/zone_density.rs:66` (`zone_kind_for`), caché `:47` |
| Wire version | `backend/src/ipc/server.rs:38` (=41) ↔ `Assets/Scripts/Network/WireSchema.cs:25` |
| Mensajes host↔peer | `backend/src/network/protocol.rs:422` (`PacketPayload`), dispatch `handlers.rs:90`; C# `IPCClient.cs:549` |
| Teleport cliente | `Assets/Scripts/Network/AuthoritativePoseApplier.cs:301` (`_motor.Teleport`); respawn backend `game_loop.rs:2961` |
| Validación de colocación | backend `game_loop.rs:5021` (`process_stp_place`); cliente `BuildPermission.cs:42` (`CanPlaceAt`) |
| Interacción autoritativa | backend `game_loop.rs:5441`; C# `WorldInteractor.cs` + `IPCClient.cs:1166` |
| Broadcast periódico host | `backend/src/network/sync.rs:236` (roster), `:970` (world_sync) |
| Densidad de fantasmas | `game_loop.rs:163` (`resolve_phantom_density_scale`), driver `phantom.rs:1314` |
| Carryables (soltar/replicar) | `game_loop.rs:5242` (`process_stp_carryable_drop`), wire `protocol.rs:512` |

## E0 — Generador de grafo puro (backend, inerte)

Módulo nuevo `backend/src/world/level4/graph.rs`: sortea N rects de sala (dimensiones del pool
autorado), los posiciona sin solape dentro del rect de región, conecta con pasillos ortogonales
(estilo imagen de referencia), garantiza conectividad total (union-find o BFS + pasillo de
reparación). Entrada: `(seed_base, epoch)`. Salida: layout abstracto (lista de salas colocadas +
segmentos de pasillo), en tiles de 2,5 m — la misma unidad que consume el tallado existente.

- Nada lo llama todavía. Cero wire, cero cliente.
- Tests: determinismo byte a byte misma `(seed, epoch)`; conectividad en 100 sorteos (cero
  incomunicadas, verificación (b) del ADR); ningún rect fuera de región; separación mínima 1 tile
  entre reservas (regla conocida de multi-sala).
- Riesgo bajo. ~250 líneas con tests.

## E1 — Región reservada + rasterización dual (backend, inerte en juego)

Constantes de región: rect de chunks reservado en offset fijo lejano (propuesta: chunks
`(2000,2000)..(2006,2006)`, fuera de toda deriva jugable). Hook en `generate_chunk_layer`
(`generator.rs:81`): si el chunk cae en la región, el layout sale de rasterizar el grafo de E0 en
las DOS representaciones, reutilizando `carve_into_layout` (5 m) y `carve_into_grid` (2,5 m) tal
cual — mismo código que salas autoradas, cero rutas nuevas. Fuera de la región, nada cambia.

- El epoch se pasa como parámetro; en E1 es constante 0. **La clave de caché de chunks de la región
  incluye el epoch desde YA** — es lo que hace barata la E4.
- Sin acceso posible (nadie llega andando a 2000 chunks): inerte en juego, verificable por sonda.
- Tests: chunk de región byte-igual host/joiner con mismo `(seed, epoch)` (verificación (a) del
  ADR); paridad tallado dual (el test cruzado que la auditoría del 08-18 señaló como ausente en
  build rooms — aquí obligatorio); chunk fuera de región idéntico a antes (golden test).
- Riesgo: las sondas del manifiesto son OnceLock de proceso (trampa conocida) — correr de una en
  una. ~300 líneas.

### Nota de ejecución E0+E1 (2026-08-24) — desviaciones reales

- El módulo NO quedó en `levels/level_4/`: el intercept de la rejilla fina tiene que
  vivir en el generador compartido (`grid_gen::stitching`) y `grid_gen` no puede
  importar `world/`, así que se siguió el reparto de salas autoradas: generador +
  raster 2,5 m en `grid_gen/level4.rs`, colisión 5 m en `world/level4_layout.rs`.
- Región real: **3×3 chunks (150×150 m)** — el chunk mide 50 m (20 celdas), no los
  20 m que asumía el borrador del plan. Origen: chunk (2000, 2000).
- El epoch en E1 es la constante `level4::EPOCH_V1 = 0` pasada por parámetro; la clave
  de caché NO lo incluye aún. E4 debe decidir: clave con epoch o purga de la reserva al
  avanzar (el patrón `reset_for_remote_world` ya existe como precedente de purga).
- Chunks de la reserva nacen `stabilized + anchored` y con `teleport_timer = f32::MAX`:
  fuera del sorteo de chunk displacement (ADR-067) por estado, no por comprobación.
- Acceso solo-teleport confirmado por Joel; se descartó "región en altura" (Y=2000):
  no hay representación vertical a esa escala y el ADR prohíbe inventarla.

## E2 — Estado de región + wire 41→42 (backend + C#, inerte)

Un solo bump de wire para todo el ADR. Mensajes nuevos en `PacketPayload` (`protocol.rs:422`):

- `Level4State { epoch: u32, window_open: bool, return_dest: [f32;3] }` — host→peers, al cambiar y
  en join (junto al roster, patrón de `sync.rs:236`).
- `Level4DoorRequest { door: enum Entry|Return }` — peer→host; identidad por CABECERA, no por
  payload (patrón ADR-068/081; no repetir el fallo PvP/pickup de la auditoría).
- Respuesta: `Level4DoorVerdict { dest: [f32;3] }` por el carril fiable (ADR-039).

Bump `WIRE_SCHEMA_VERSION` (`ipc/server.rs:38`) **y `WireSchema.Expected`
(`WireSchema.cs:25`) en el MISMO commit** — desparejarlos deja el juego inarrancable, no con
warning. C#: casos en `IPCClient.cs` que guardan el estado en un `Level4Client` nuevo (datos, sin
efectos). Host: estado en `game_loop`, ventana cerrada por defecto.

- Sin puertas todavía: nadie puede emitir `Level4DoorRequest` legítimo; el host responde pero nada
  lo dispara. Inerte.
- Tests: roundtrip serialización de los 3 mensajes; verdict con destino dentro de ventana =
  entrada; deriva por overstay (función pura `drift_dest(overstay) -> radio` con test propio,
  ~100 m/min); estado llega al joiner en join.
- ~250 líneas.

### Nota de ejecución E2 (2026-08-24) — desviaciones reales

- **Opcodes reales: `0x57` (`Level4State`), `0x58` (`Level4DoorRequest`), `0x59`
  (`Level4DoorVerdict`)** — `0x55`/`0x56` se dejan libres a propósito porque el borrador de
  ADR-094 (Facelings, sesión concurrente, sin código aún) los cita textualmente. Wire
  `41 → 42`.
- **Punto de entrada = posición del jugador al cruzar, NO una posición de puerta sorteada.**
  Evita inventar en E2 la placement logic que el roadmap ya asignaba a E3 (sorteo de la
  puerta por seed): el host simplemente recuerda dónde estaba quien cruzó Entry. E3 no
  necesita tocar esta función — solo hacer que un prefab de puerta dispare la petición.
  `Level4DoorRequest` lleva `request_id: u64` (generado por el cliente) para correlar con el
  veredicto; no hace falta un set de dedupe porque `process_entry`/`process_return` son
  ambas puras/idempotentes.
- **`Level4RegionState` vive en `NetworkManager` (`net.level4`)**, host-autoritativo, mismo
  reparto que `stp_buildings`. Los campos host-only (`entry_point`, `direction`, `opened_at`,
  `window_count`) nunca cruzan el wire — solo `epoch`/`window_open`/`return_dest`, mismo
  reparto que `SettlingItem` frente a `StpItemInfo`.
- **Ventana y cierre de ventana: semántica mínima, decisión pendiente para E4/E5.** E2 abre la
  ventana en el primer Entry y no la cierra nunca sola (persiste hasta que algo externo la
  reabra a mano); qué la cierra —¿la región vacía?, ¿el avance de epoch?— es responsabilidad
  de E4 (que además decide si el epoch invalida la reserva por clave de caché o por purga
  explícita, nota pendiente de E1).
- **C# de este stage: SOLO el bump de `WireSchema.Expected`.** Nada cruza el límite IPC
  Unity↔backend todavía (el cambio es P2P backend↔backend puro) — el patrón "un cambio
  solo-P2P también bumpea, sin código C# de feature" tiene precedente extenso en
  `ipc-wire-schema.md`. La idea original de un `Level4Client` en C# se descarta: no hay nada
  que consuma el estado hasta que exista un prefab de puerta (E3).
  `NetworkEvent::Level4DoorVerdict` SÍ reenvía un evento IPC genérico
  (`level4_door_resolved`) a la propia Unity del solicitante, completando el round-trip sin
  necesitar una clase C# nueva — E3 solo añade el listener.

## E3 — Puertas + teleport (C# + backend)

Puerta de ida en Level 0: posición determinista por seed (sorteo estilo salas autoradas, a distancia
media del spawn), prefab con interactable que envía `Level4DoorRequest(Entry)`; puerta de vuelta en
sala fija del grafo (la sala 0 del sorteo la contiene siempre — regla en E0, campo reservado ya en
el layout). Al recibir `Level4DoorVerdict`, el cliente teleporta vía el motor
(`AuthoritativePoseApplier.cs:301` como referencia de API; el streaming de chunks sigue solo — el
sistema ya soporta teleport de jugador, cf. bootstrap de sesión). Host: primer cruce de Entry abre
la ventana y fija `return_dest = punto de entrada`; Return consulta destino vigente con deriva si
overstay.

- Primera etapa VISIBLE. Verificación en juego: cruzar, aparecer en la región, volver dentro de
  ventana al punto de entrada; con overstay forzado (ventana corta por env de debug), aparecer a
  radio esperado y COMPARTIDO entre dos peers (verificación (c) del ADR).
- Riesgo: pop-in de chunks al teleportar (~carga inicial); aceptable v1, anotar medida.
- ~300 líneas (prefab aparte, sin contar assets).

### Nota de ejecución E3 (2026-08-24) — desviaciones reales

- **Sin wire nuevo.** El envío Unity→su propio backend usa el canal `PlayerAction` genérico
  (acción IPC `level4_door`, patrón `bed_constructed`/`report_noise`) en vez de un mensaje IPC
  dedicado — cero bump de `WIRE_SCHEMA_VERSION`, cero cambio de esquema. El salto P2P
  joiner→host sigue siendo el `Level4DoorRequest`/`Verdict` que E2 ya dejó listo y probado;
  E3 solo le añade el disparador.
- **`Level4RegionState::process_door` centraliza la rama Entry/Return**, usada por las DOS
  rutas que pueden recibir un cruce (acción IPC host-directa y `NetworkEvent::Level4DoorRequest`
  P2P) — evita que la decisión "qué hacer con cada valor de `door`" viva por duplicado.
- **`request_id` es CLIENT-generado** (un contador simple por trigger), no un contador del
  backend — mismo patrón que `place_id`/`add_id` de STP, no el de `next_corpse_request_id`.
  Sin dedupe: `process_door` es idempotente en las dos ramas.
- **Puertas: dos `GameObject` instanciados por `GameBootstrap`, sin prefab ni edición de
  escena** (`Level4DoorTrigger`, poll de proximidad contra el motor LOCAL — no colisión física,
  para no depender de si un avatar remoto lleva collider). Anclas FIJAS a mano, NO sorteadas
  por seed (eso queda fuera de alcance de E3, es trabajo de autorado/worldgen mayor): Entry a
  `(3, 1, 0)` dentro del starter cluster (garantizado plano por Fase 2.6); Return al CENTRO
  del rect de la reserva del Level 4, `(100075, 1, 100075)` — derivado a mano de
  `REGION_ORIGIN_CHUNK=(2000,2000)` × `REGION_CHUNKS=3` × 50 m/chunk. **Placeholder explícito**:
  sin contenido autorado (E6) la puerta de vuelta no tiene una sala real donde vivir; se
  reposicionará cuando la haya.
- **Verificación**: unitaria completa (backend 914/0, C# 0 errores en las 4 asambleas) y
  símbolos confirmados en el binario release recién compilado
  (`grep -aoc level4_door backrooms_server.exe` → 7 apariciones). **NO se ha hecho playtest
  real en juego** (cruzar de verdad, ver el teletransporte, medir el pop-in de chunks) —
  pendiente, próximo paso antes de dar la etapa por visualmente cerrada.

## E4 — Epochs (backend + C#)

Host avanza `epoch` cada N min (constante, propuesta 10) desde `window_open`. Al avanzar: broadcast
`Level4State`, y cada backend re-genera los chunks de región on-demand — la clave de caché con epoch
(E1) hace que la invalidación sea "pedir de nuevo", sin borrado selectivo frágil. Cliente: al cambiar
el epoch, descarta y re-pide los chunks de región cargados (misma ruta que un chunk nuevo).
Excepción de sala ocupada: el host conoce posiciones; las salas del grafo saliente con jugador
dentro se COPIAN al layout del grafo entrante (mismo rect, mismas puertas) antes de rasterizar —
determinista porque viaja en `Level4State` como lista de rects conservados (cabe en el mensaje;
enmienda al formato de E2 si excede, decidir al medir).

- Tests: avance de epoch re-sortea región y NO toca Level 0; sala ocupada persiste
  (verificación (d) del ADR); dos backends con la misma lista de conservación producen chunks
  byte-iguales.
- Riesgo ALTO comparado con el resto (invalidación de caché + copia de salas): esta etapa se parte
  en dos commits — (a) avance + re-sorteo total, (b) conservación de sala ocupada.
- ~300 líneas en dos commits.

## E5 — Reglas de zona (backend + C#)

- Construcción denegada: guarda por región en `process_stp_place` (`game_loop.rs:5021`) — servidor
  manda — y espejo en `BuildPermission.CanPlaceAt` (`BuildPermission.cs:42`) para feedback
  inmediato (verificación (e) del ADR).
- Fantasmas: densidad de región escalada por epoch — factor en el sorteo por bloque
  (`resolve_phantom_density_scale`, `game_loop.rs:163`) cuando el bloque cae en región.
- Bloqueo de puertas: NADA que implementar — carryables ya se sueltan y replican
  (`process_stp_carryable_drop`), martillo ya demuele/repara. Solo verificación en juego entre dos
  peers (verificación (f) del ADR).
- Loot: spawn de items en salas del grafo con la escasez vigente (dirección DayZ); reutilizar la
  ruta de scatter/loot existente parametrizada por región. Si excede el diff, commit propio.
- Facelings: NO existen como entidad; fuera de este roadmap (nota en ADR ya lo cubre).
- ~250 líneas.

## E6 — Contenido + señales diegéticas + tuning (sin lógica nueva)

Pool de salas oficina adicional (hornear 4-6 piezas, cero código — el 80 % del retorno, lección de
ROOMS-ROADMAP); luz/ambience por epoch (listas dispersas estilo ADR-059/066); señales de avance de
epoch (parpadeo, zumbido — audio por mixer SFX/ambiente, niveles ~0.05, lección conocida); tuning de
N minutos, radio de deriva, tamaño de región. Playtest de incursión completa: entrar, saquear, salir
tarde, aparecer lejos, volver a base, sin desync (verificación en juego del ADR).

## Reglas transversales

- Cada etapa: `cargo test` + clippy `-D warnings` + fmt + `CompileCheckClient.sh` en verde antes de
  commit; comprobar a mano el `.asmdef` si un `using` nuevo cruza asambleas (CompileCheck da falso
  verde ahí).
- Staging explícito de rutas de la sesión, commit por etapa (E4 en dos).
- Ningún cambio fuera de región en golden tests de E1 se mantiene como invariante de TODAS las
  etapas siguientes.
- Si algo contradice lo escrito en ADR-093, PARA: enmienda antes de código.
