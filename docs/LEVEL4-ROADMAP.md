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
