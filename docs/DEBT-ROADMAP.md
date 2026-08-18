# DEBT-ROADMAP.md — Hoja de ruta de deuda técnica

> Generado el 2026-08-18 a partir de una auditoría de solo-lectura (20 pasadas de descubrimiento +
> 4 de verificación adversarial + 16 de puntuación/plan, todas agentes `revisor-diffs` de solo
> lectura — **cero código tocado** en toda la auditoría). Fuente completa de los hallazgos:
> [STATE.md](STATE.md), última sesión.
>
> Orden: de MENOS grave a MÁS grave, tal como se pidió — empezar por lo barato y de bajo riesgo
> (código muerto, luego bugs muy leves) antes de tocar el bloque de seguridad de red, que es el
> cambio de más alcance y el que más justifica ir con cuidado (varios ítems ahí piden ADR antes de
> tocar código, regla dura #7 de CLAUDE.md).
>
> **68 ítems: 27 de código muerto + 41 bugs reales** (6 Muy grave, 13 Grave, 9 Medio, 8 Leve, 5 Muy
> leve). Cada ítem lleva nota 1-10, el porqué de esa nota, y un plan de arreglo.
>
> **Actualizado 2026-08-18 (mismo día):** cuatro pasadas de implementación — Código muerto, Muy
> leve + Leve, y Medio, con la misma regla en las tres primeras: **nunca tocar nada cuyo
> comportamiento cambie para un caller/escenario que se ejerza HOY** — solo bordes inalcanzables,
> guardas sobre estados que no ocurren, o cosas puramente aditivas (tests, comentarios). En Medio
> esa regla ya no aplicaba tal cual (varios ítems SÍ cambian comportamiento real) — se implementó
> lo que tenía un plan bien acotado y se pausaron los dos que son rearquitectura real, a la espera
> de confirmar antes de tocarlos.
>
> **Código muerto: 17 ✅ + 1 🟡** en tres commits (`0da821ca` Rust, `91c9f62a` + `1c7840e0` C#). 2
> hallazgos corregidos al reverificar antes de tocar (`Chunk3DLayout` ❌ tenía uso real; de
> `BoundedDedupeSet` solo `retain` 🟡 estaba muerto de verdad). 3 🔒 conservados a propósito
> (`RoomSpawner`, `RemoteVoicePlayer.SetMuted`, `HostEndpointConfig` — diseño documentado con
> motivo propio, ADR-005/ADR-045/comentario de clase, no cruft). 1 pendiente de ADR (`PacketType`).
>
> **Muy leve: 5/5 ✅** en un commit Rust (`40493fcd`) + parte del commit C# de esa pasada — los
> cinco confirmados cero-impacto para todo input/caller conocido hoy antes de tocar.
>
> **Leve: 5/8 ✅** (commits `40493fcd`, `dbab66e9`, y `e0f7e42d` — este último de rebote, ver Medio
> abajo). **3 diferidos** (⏸️, no por indecisión): cambian comportamiento observable en un camino
> que SÍ se ejerce en juego normal hoy — throttle de build de chunk, orden de paquetes de spray,
> conteo de fantasmas en el roster gate.
>
> **Medio: 7/9 ✅** en dos commits (`b8fc7b63` Rust, `e0f7e42d` C#) — el fix de "`BuildRoomRegistry`
> no se limpia al reconectar" también resolvió de rebote el hallazgo Grave gemelo de `ZoneRegistry`
> (mismo commit `e0f7e42d`, mismo punto de enganche en `NetworkInitializer`) y el Leve de
> crecimiento sin poda. **2 pausados para confirmar** (⏸️): `select!` sin shutdown ordenado
> (rearquitectura de control de flujo) y la guardia anti-overflow del anillo de voz (rework de
> concurrencia con `Interlocked` entre el hilo de audio y el principal) — ya investigados y con
> plan escrito, solo falta luz verde antes de tocar algo de ese calibre.
>
> `cargo build`+`cargo test` verdes (801 passed, mismos 9 fallos preexistentes de un WIP ajeno sin
> commitear); `CompileCheckClient.sh` 0 errores en las 4 asambleas, verificado tras cada tanda.
> El resto de categorías (Medio → Muy grave) sigue sin tocar.

## Índice por categoría

1. [Código muerto](#1-código-muerto) (27 ítems) — limpieza de bajo riesgo, buen calentamiento
2. [Muy leve](#2-muy-leve) (5 ítems, nota 1-2)
3. [Leve](#3-leve) (8 ítems, nota 3-4)
4. [Medio](#4-medio) (9 ítems, nota 5-6)
5. [Grave](#5-grave) (13 ítems, nota 6-8)
6. [Muy grave](#6-muy-grave) (6 ítems, nota 9-10)

---

## 1. Código muerto

27 ítems, **17 ya resueltos + 1 parcial** (✅/🟡, commits `0da821ca` Rust / `91c9f62a` + `1c7840e0`
C#). Cuatro de ellos (marcados **[sin acción]**) no son hallazgos nuevos: son comprobaciones
explícitas pedidas que salieron limpias o ya documentadas. Dos quedaron corregidos al reverificar
justo antes de borrar (marcados 🟡/❌). Tres quedan **conservados a propósito** (🔒 — diseño
documentado con motivo propio, no cruft) y uno pendiente de ADR por tocar protocolo.

### [1] Migración de claims/zona-construible no dejó restos **[sin acción]**
**Ubicación:** `backend/src/game_loop.rs:4901-4967` (comprobado, no es hallazgo)
**Por qué:** `grep -rn "ClaimBlock"` en todo el backend da cero resultados — el commit `c7a4775c`
ya borró `CLAIM_BLOCK_M`/`claim_block()` limpiamente. `ZONE_SAFE`/`BuildableZoneKind` del backend
siguen vivos pero con callers reales (son del sistema general `zone_kind`, ADR-033, no restos).
**Plan:** Nada que hacer.

### [4] `WorldTickResult`: struct nunca construido, la función real devuelve una tupla
**Estado:** ✅ RESUELTO — commit `0da821ca`
**Ubicación:** `backend/src/world/mod.rs:106`
**Por qué:** Único match en todo el repo es su propia definición. La función de tick real
(`world/mod.rs:964`) devuelve `(f32, Vec<GameEvent>)`, no este struct. El doc-comment ("Result of
a world tick") engaña a quien busque el tipo de retorno real.
**Plan:** Borrar el struct entero (`world/mod.rs:106-110`). Sin dependencias, eliminación de una
sola pieza.

### [5] `Chunk3DLayout`: wrapper 3D — ❌ FALSO POSITIVO, no se tocó
**Estado:** ❌ DESCARTADO — verificado con grep fresco justo antes de borrar y resultó tener uso
real: `world/graph/nodes.rs` lo importa y lo usa en 3 métodos (`SpatialNode::to_chunk3d_layout`,
`world_bounds_2d`, `world_bounds_3d`), uno de ellos construyéndolo directamente
(`Chunk3DLayout::from_chunk_layout`). Contradice la afirmación original de "cero referencias fuera
de su propio fichero" — el código cambió entre la auditoría y la implementación (sesión
concurrente activa en el repo), o el grep original no fue repo-wide. No se tocó nada.
**Ubicación:** `backend/src/world/architecture/chunk3d_layout.rs:7`
**Por qué (hallazgo original, ya no aplica):** Cero referencias fuera de su propio fichero y de su
propio módulo de tests. `world/architecture/README.md:91-97` afirma que lo usan
`generator.rs`/`levels/level_0/builder.rs` — falso hoy: `SpatialNode` usa `Chunk3DCoord`
directamente.
**Plan:** Ninguno — nada que arreglar, es código vivo. Si se quiere, actualizar el README para que
cite `graph/nodes.rs` como consumidor real en vez de los ficheros que ya no lo usan.

### [4] `layout_grammars.rs`: variante `CorridorBroken` inalcanzable + 4 helpers sin caller
**Estado:** ✅ RESUELTO — commit `0da821ca` (confirmado además por el propio warning `dead_code`
del compilador para `CorridorBroken`, cacheado en `target/`)
**Ubicación:** `backend/src/world/architecture/layout_grammars.rs:28`
**Por qué:** Ninguna de las 23 ramas del mapeo `template_id → LayoutGrammarType` produce
`CorridorBroken`. Además `open_cell`, `carve_rect`, `fill_rect_blocked` y
`set_cell_side_edge_kind` no tienen ningún caller vivo (ni siquiera en tests), a diferencia de sus
hermanos `set_cell`/`block_cell`/`wall_v`/`wall_h`/`room_box`, que sí se usan.
**Plan:** Quitar la variante `CorridorBroken` + su match arm + `g_broken_corridor` si se queda sin
caller tras quitar el arm (comprobar con el compilador). Borrar los 4 helpers.

### [5] `BoundedDedupeSet`: solo `retain` era muerto de verdad
**Estado:** 🟡 PARCIAL — commit `0da821ca`. Al verificar antes de borrar, `contains`/`is_empty` SÍ
tienen caller real (`network/tests.rs:989,1000` y `game_loop/tests.rs:1379`) — el hallazgo
original solo había grepeado dentro de `network/mod.rs`, no el repo entero incluyendo tests. Se
restauraron los dos y solo se borró `retain`, que en efecto no tiene ningún caller (confirmado
repo-wide) y cuyo doc-comment sí mentía sobre su uso.
**Ubicación:** `backend/src/network/mod.rs:104`
**Por qué (`retain`, la única parte que se confirmó muerta):** El doc-comment afirma que lo usa
`purge_peer_state` — falso: esa función llama `.retain()` sobre un `HashSet` normal, no sobre este
tipo.
**Plan:** Ejecutado — se borró solo `retain`. `contains`/`is_empty` quedan intactos, con caller
real en tests.

### [3] `NetworkEvent::HandshakeReceived` nunca se construye, solo se noopea
**Estado:** ✅ RESUELTO — commit `0da821ca`
**Ubicación:** `backend/src/network/events.rs:320`
**Por qué:** Solo dos líneas en todo el repo: la definición y su match arm vacío en
`game_loop.rs:2330`, cuyo propio comentario ya admite que no hace nada ahí.
**Plan:** Borrar la variante del enum y su match arm. No cruza el wire, sin dependencias.

### [2] `edge_architecture_density`: el único método de su familia sin ni siquiera un test
**Estado:** ✅ RESUELTO — commit `0da821ca`
**Ubicación:** `backend/src/world/chunk/layout.rs:280`
**Por qué:** Sus 6 hermanos (`total_edge_count`, `count_edge_kinds`, etc.) todos tienen caller real
en tests; este es la única excepción, cero uso en producción o tests.
**Plan:** Borrar el método (`layout.rs:280-286`).

### [3] 7 variantes de `PacketType` nunca se construyen desde Rust ⚠️ requiere ADR si se toca
**Estado:** ⏳ Pendiente — bloqueado en el chequeo del lado C# que el plan pide antes de decidir.
No tocado en esta pasada.
**Ubicación:** `backend/src/network/protocol.rs:57` (Discover, PeerIntro, StatUpdate,
InventorySync, RepairAnchor, UseConsumable, ChunkGenerate)
**Por qué:** Cero construcción ni match en producción. `PacketType::from_u16` (el decodificador
que en teoría las produciría) solo tiene callers en tests.
**Plan:** **No borrar sin más** — es superficie de protocolo cliente↔servidor (regla dura #7 de
CLAUDE.md). Primero confirmar en `WireSchema.cs` (C#) si el cliente emite alguno de estos opcodes
(en ese caso es un gap funcional, no código muerto). Si se confirma huérfano en los dos lados,
retirar las 7 variantes requiere ADR nuevo por ser cambio de protocolo.

### [3] `BuildPermission.CanBuildAt` sin ningún caller
**Estado:** ✅ RESUELTO — commit `91c9f62a`
**Ubicación:** `Assets/Scripts/Gameplay/Building/BuildPermission.cs:49`
**Por qué:** Todo el código real usa `CanPlaceAt`/`Explain` (con `defId`), llegados en la enmienda
5. `CanBuildAt` es la versión antigua sin `defId`, sin caller.
**Plan:** Borrar el método y su doc XML. Si hiciera falta un "¿puedo construir aquí sin importar
la pieza?", exponerlo como parte de `Explain`.

### [4] Bloque público de reservas de `RoomSpawner` nunca cableado
**Estado:** 🔒 CONSERVADO A PROPÓSITO — documentado como pendiente de sustitución (ADR-005), no
se borra código que el propio proyecto ya declaró que iba a necesitar.
**Ubicación:** `Assets/Scripts/Gameplay/World/RoomSpawner.cs:255-288`
**Por qué:** `ReserveGrid`/`ReleaseGrid`/`IsGridFree`/`WorldToGridCoords`/`GridCoordsToWorld` sin
ningún caller. `RoomSpawner` está documentado como herramienta de prototipo pendiente de
sustitución por el path IPC de Fase 4 (ADR-005); este bloque nunca se integró.
**Plan:** Si el plan de mega-chunks sigue sin fecha, borrar el bloque completo. Si sigue vigente,
anotarlo en STATE.md como API pendiente de cablear.

### [2] `IsolationDirector.Isolation` sin lectores
**Estado:** ✅ RESUELTO — commit `91c9f62a`
**Ubicación:** `Assets/Scripts/Gameplay/Audio/IsolationDirector.cs:63`
**Por qué:** Los consumidores reales (`ReverbMixerDriver`, `FluorescentHumDirector`) leen el valor
vía `Colour()`/`ColourHumVolume()`, nunca de esta propiedad pública.
**Plan:** Eliminar la propiedad (el campo privado ya basta), o cablearla a un HUD de depuración
futuro si se quiere conservar.

### [5] `SetMuted`/`IsMuted` de `RemoteVoicePlayer` implementados pero inalcanzables
**Estado:** 🔒 CONSERVADO A PROPÓSITO — el doc-comment del propio método explica por qué NO se
persiste (referencia a ADR-045) — diseño deliberado con motivo propio, no cruft sin explicación.
Borrarlo tira una feature completa y probada, no "código muerto" en el sentido de resto confuso.
**Ubicación:** `Assets/_Migration/STPIntegration/RemoteAvatar/RemoteVoicePlayer.cs:87,93`
**Por qué:** `Update()` sí consulta el `HashSet` de silenciados para filtrar voz, pero nada en el
repo llama a `SetMuted` para poblarlo — feature de silenciar a un peer completamente implementada
pero inalcanzable hoy.
**Plan:** Cablear un control de UI (lista de jugadores / menú de voz) que llame a `SetMuted`; si no
está planeado a corto plazo, quitar el método y el `HashSet` hasta que exista un consumidor real.

### [3] `SteamLobbyManager.HasLobby` / `LocalSteamId` sin consumidores
**Estado:** ✅ RESUELTO — commit `91c9f62a`
**Ubicación:** `Assets/Scripts/Network/SteamLobbyManager.cs:53,56`
**Por qué:** `JoinSessionUI`, el único consumidor, usa otras 5 propiedades pero nunca estas dos.
**Plan:** Quitarlas, o cablear el indicador "ya tienes lobby abierto" / el SteamId a la UI si eso
era la intención del spike.

### [3] `JoinSessionUI.SetInteractable` es un alias duplicado sin caller
**Estado:** ✅ RESUELTO — commit `91c9f62a`
**Ubicación:** `Assets/Scripts/UI/JoinSessionUI.cs:608`
**Por qué:** Alias de `SetUiInteractable` (que sí se usa ~20 veces). Resto de un renombrado sin
limpiar.
**Plan:** Eliminar la línea 608.

### [3] `HostEndpointConfig.GetHostAddress`/`SetHostAddress` sin consumidor real
**Estado:** 🔒 CONSERVADO A PROPÓSITO — el propio fichero se documenta como "Opt-in and
removable... Delete the file to remove entirely" y describe un orden de resolución de 4 pasos que
incluye explícitamente "call SetHostAddress" como vía #3. Es una feature acabada para el bug
cross-machine (root cause B) con su propio diseño razonado, no un resto sin explicación.
**Ubicación:** `Assets/_Migration/STPIntegration/HostEndpointConfig.cs:99`
**Por qué:** No aparecen en ningún otro fichero; el componente ni siquiera está colocado en
ninguna escena/prefab. Su único efecto activo llega por el `RuntimeInitializeOnLoadMethod`
estático, no por estos métodos de instancia.
**Plan:** Si hay una UI de configuración futura prevista, marcarlo pendiente en el comentario de
clase; si no, borrar ambos métodos.

### [2] `ProxyVocalHook.KindName` sin caller pese a estar "exposed for the prefab builder"
**Estado:** ✅ RESUELTO — commit `1c7840e0`. A diferencia de los 3 anteriores, no tenía diseño
razonado propio — solo una aspiración de comentario ("so the prefab builder can label...") sin
ADR ni motivo documentado, así que sí calificaba como limpieza mecánica.
**Ubicación:** `Assets/_Migration/STPIntegration/RemoteAvatar/ProxyVocalHook.cs:256`
**Por qué:** Ningún Editor builder lo llama pese a que el propio comentario dice que deberían.
**Plan:** Cablearlo en el builder que etiqueta los bancos de audio, o borrarlo si ya se resolvió de
otra forma.

### [2] `EdgeKinds.EdgeIsFullWall` es el único predicado `EdgeIs*` sin uso
**Estado:** ✅ RESUELTO — commit `91c9f62a`
**Ubicación:** `Assets/Scripts/Network/IPCMessages.cs:641`
**Por qué:** Sus 8 hermanos sí se usan en `ChunkRenderer.cs`. `EdgeBlocksMovement` repite su misma
expresión booleana en línea en vez de llamarlo.
**Plan:** Borrar, o hacer que `EdgeBlocksMovement` lo reutilice en vez de duplicar la expresión.

### [3] `GridChunkDataMsg.TileIsBuildRoom` duplicado y sin caller
**Estado:** ✅ RESUELTO — commit `91c9f62a`
**Ubicación:** `Assets/Scripts/Network/IPCMessages.Chunk.cs:454`
**Por qué:** `BuildRoomRegistry` (el consumidor real) lee los campos crudos directamente. Este
método hace la misma comprobación en coordenadas de tile local, resto de antes de la enmienda 5.
**Plan:** Borrar; si hiciera falta una comprobación tile-local futura, añadirla junto a
`BuildRoomRegistry` para no tener dos formas de responder la misma pregunta.

### [2] `GridVisualConstants.WallInset` sin ningún uso
**Estado:** ✅ RESUELTO — commit `91c9f62a`
**Ubicación:** `Assets/Scripts/Gameplay/GridWorld/GridCell.cs:120`
**Por qué:** Su hermana `WallThickness` sí se usa. El cálculo real del inset se hace inline en
`GridChunkBuilder`.
**Plan:** Borrar, o sustituir el cálculo inline por esta constante si coinciden (comprobar primero).

### [2] `ChunkViewMsg.LayoutCellsRaw` sin lectores
**Estado:** ✅ RESUELTO — commit `91c9f62a`
**Ubicación:** `Assets/Scripts/Network/IPCMessages.ChunkView.cs:52`
**Por qué:** Sus vecinas `HasEdgeLayout`/`HasBackendLayout` sí se consumen; esta no.
**Plan:** Borrar la propiedad; el campo subyacente sigue accesible directamente.

### [2] `SprayDraftReceiver.LiveDraftCount` sin el consumidor que su comentario promete
**Estado:** ✅ RESUELTO — commit `91c9f62a`
**Ubicación:** `Assets/Scripts/Gameplay/SprayDraftReceiver.cs:69`
**Por qué:** El comentario dice "para tests y para el log", pero no hay ni tests ni logs que lo
lean.
**Plan:** Borrar, o añadir el test que el comentario da por hecho que existe.

### [2] `SprayGesture.OpenStrokePointCount` sin caller
**Estado:** ✅ RESUELTO — commit `91c9f62a`
**Ubicación:** `Assets/Scripts/Gameplay/SprayGesture.cs:196`
**Por qué:** Ni `SprayPainter` ni `SprayDraftReceiver` (los dos consumidores de `SprayGesture`)
la leen.
**Plan:** Borrar si no hay UI/debug previsto para mostrar el conteo de puntos del trazo abierto.

### [2] `ZoneRegistry.KnownChunkCount` sin el HUD de depuración que anticipa
**Estado:** ✅ RESUELTO — commit `91c9f62a`
**Ubicación:** `Assets/Scripts/Gameplay/ZoneRegistry.cs:59`
**Por qué:** Marcado "Diagnostic only" pero ni siquiera `PoiDebugHud` (el HUD de debug que ya
existe) lo lee.
**Plan:** Borrar, o cablearlo al `PoiDebugHud` existente.

### [1] `VolumetricGridMsg.OccSolid`/`OccBlocked` nunca se leen por nombre **[no borrar]**
**Ubicación:** `Assets/Scripts/Network/IPCMessages.Volumetric.cs:180,187`
**Por qué:** De 16 códigos `Occ*`, `ChunkRenderer` maneja 14 por nombre; estos dos caen al default
implícito. No es candidato a borrado individual: son parte del espejo completo del enum del
backend, y un hueco numérico rompería la paridad de valores.
**Plan:** No borrar. Confirmar si Solid/Blocked necesitan tratamiento propio o si el fallback
genérico es intencional, y dejarlo anotado en el comentario de clase.

### [1] `PoiVisualDecorator` (clase entera) ya documentada como código muerto **[sin acción nueva]**
**Ubicación:** `Assets/Scripts/Gameplay/PoiVisualDecorator.cs:27`
**Por qué:** El propio fichero se autodocumenta "NEVER CALLED... pendiente de la decisión conjunta
con `ChunkRenderer`" (`docs/AUDIT-2026-08-03.md`). Sigue igual.
**Plan:** Ninguna acción nueva — sigue esperando esa decisión conjunta.

### [1] `StructureDefinition` (y `layersTall`) es un DTO nunca instanciado **[documentación-como-código, intencional]**
**Ubicación:** `Assets/Scripts/Gameplay/GridWorld/ProceduralWorldGenerator.cs:37,41`
**Por qué:** El propio comentario de clase dice que documenta el formato; `StructureValidator`
parsea el JSON a mano en vez de deserializar a este tipo (porque `JsonUtility` no soporta arrays
irregulares). Ya declarado así por el autor.
**Plan:** Ninguna acción necesaria si se acepta como documentación intencional; si se prefiere
eliminar la duplicidad con `docs/STRUCTURES.md`, borrar la clase y dejar solo el Markdown.

### [5] `FilterMovementForCollision` de `PlayerController` calcula un `CapsuleCast` que nadie lee
**Estado:** ✅ RESUELTO — commit `91c9f62a` (más de lo previsto: al quitar la llamada,
`ReadMovement` se quedó sin ningún consumidor y se retiró en cadena junto con la variable
`movement`/`sprint`, para no dejar un warning de variable sin usar recién creado)
**Ubicación:** `Assets/Scripts/Gameplay/PlayerController.cs:103,150-183`
**Por qué:** El resultado (`movement`) nunca se lee después — ADR-009 ya retiró a este controlador
como emisor de red. Además de la query de física descartada cada frame, hay 5 campos de Inspector
("Collision Proxy V0") que aparentan estar activos y no lo están.
**Plan:** Eliminar la llamada, el método `FilterMovementForCollision`, su helper `CapsuleBlocked`,
y los 5 campos del header "Collision Proxy V0". Si se quiere colisión predictiva real en el
futuro, reabrir como tarea nueva con un consumidor explícito.

---

## 2. Muy leve

5 ítems, nota 1-2 — **5/5 ✅ RESUELTOS** (commit `40493fcd` Rust + parte de `dbab66e9` C#). Bugs
reales pero de impacto casi nulo hoy — bordes teóricos, probabilidad ínfima, o ya mitigados por
otro mecanismo — confirmados cero-impacto en todo caller conocido antes de tocar.

### [1] Nombres reservados de Windows (CON/NUL/COM1) — **REFUTADO, sin bug real**
**Estado:** ✅ RESUELTO — commit `40493fcd` (comentario documentando la refutación, sin cambio de código)
**Ubicación:** `backend/src/persistence/mod.rs:14-38`, `player_save.rs:128-133`
**Por qué:** Investigado y refutado empíricamente en la máquina real de desarrollo: escribir/
renombrar/releer `CON.json`/`NUL.json`/`COM1.json` no produjo ningún error. Windows solo
intercepta el nombre BASE sin extensión, y el código siempre añade `.json`.
**Plan:** Ninguna acción de código. Añadir un comentario de una línea junto a
`resolve_player_save_path` indicando que esta hipótesis fue probada y refutada el 2026-08-18, para
que un audit futuro no la reabra sin motivo.

### [2] Nombre de `.tmp` sin sufijo de PID (riesgo latente, hoy mitigado)
**Estado:** ✅ RESUELTO — commit `40493fcd`
**Ubicación:** `backend/src/persistence/save.rs:196-198`, `player_save.rs:45-47`
**Por qué:** Dos `save_to` concurrentes sobre el mismo path se pisarían el `.tmp` — pero esa
concurrencia ya está serializada por el lock exclusivo de `persistence/lock.rs`, así que no es
alcanzable hoy. Solo importa si un futuro cambio (mover I/O a `spawn_blocking`, ya documentado en
una sonda existente) reintroduce el riesgo.
**Plan:** Cambiar `tmp.push(".tmp")` a `tmp.push(format!(".{}.tmp", std::process::id()))` en los
dos sitios, mismo idiom que `lock.rs` ya usa para su propio PID. Cambio de una línea por sitio.

### [2] `layoutGridSize` sin techo permite overflow teórico en `SplitPackedLayout`
**Estado:** ✅ RESUELTO — commit `dbab66e9`
**Ubicación:** `Assets/Scripts/Network/IPCMessages.ChunkView.cs:105` (usado en 169, 222, 234, 246)
**Por qué:** Solo `Mathf.Max(1, ...)` por abajo, sin techo. Con `g` grande, `cellCount=g*g`
desborda `int32`. No explotable hoy: el backend Rust es el único emisor y es de confianza en el
despliegue actual.
**Plan:** Añadir `private const int MaxLayoutGridSize = 64;` y cambiar la línea 105 a
`Mathf.Clamp((int)r.ReadInt(), 1, MaxLayoutGridSize)`. Un único fix en el punto de entrada protege
los 4 usos posteriores.

### [2] `take_damage` sin `.clamp` superior (solo `.max(0.0)`)
**Estado:** ✅ RESUELTO — commit `40493fcd`
**Ubicación:** `backend/src/player/stats.rs:113-115`
**Por qué:** Invariante 0..100 no protegido dentro de la función, rompiendo la simetría con
`restore_*`/`use_stamina` (todos `.clamp(0.0,100.0)`). Los 3 llamadores conocidos ya fuerzan
`amount > 0`, así que no hay camino real que exceda 100 hoy — puramente riesgo de mantenimiento
futuro.
**Plan:** Cambiar `(self.health - amount).max(0.0)` a `(self.health - amount).clamp(0.0, 100.0)`.
Una palabra. Cero riesgo de romper los 3 llamadores actuales. De paso corregir el comentario de
`restore_health` ("mirrors take_damage"), que dejará de ser falso.

### [2] `next_sequence()` puede emitir `sequence==0`, ese paquete nunca se ACKea
**Estado:** ✅ RESUELTO — commit `40493fcd`
**Ubicación:** `backend/src/network/mod.rs:506-509`; guarda relacionada en `handlers.rs:23`
**Por qué:** `wrapping_add(1)` de `u32::MAX` da 0, y `handlers.rs:23` trata `sequence==0` como "no
ACKear". Requiere ~4.29×10⁹ envíos fiables acumulados — ninguna sesión del proyecto se acerca. En
el peor caso, desconexión normal por agotamiento de `MAX_RETRIES`, un camino ya manejado.
**Plan:** En `next_sequence()`, tras el `wrapping_add(1)`, saltar el valor 0 (incrementar una vez
más si el wrap aterriza en 0). `sequence` sigue siendo `u32`, sin cambio de wire.

---

## 3. Leve

8 ítems, nota 3-4 — **5/8 ✅ RESUELTOS** (commits `40493fcd`/`dbab66e9`/`e0f7e42d`), **3 ⏸️
DIFERIDOS** a propósito: cambian comportamiento observable en un camino que sí se ejerce en juego
normal hoy (no un edge case inalcanzable), así que se tratan con el mismo rigor que Medio/Grave en
vez de como limpieza automática — ver el porqué en cada entrada.

### [3] Falta test que cruce `carve_into_layout` contra `carve_into_grid`/`carve_door`
**Estado:** ✅ RESUELTO — commit `40493fcd`. Verde hoy sobre 4 seeds × 1600 chunks (~1280 salas): el
fallback de `carve_into_grid` no tuvo que activarse en esa muestra, así que la divergencia sigue sin
observarse en juego — pero el hueco de diseño del hallazgo Grave sigue ahí, este test solo lo
atraparía si algún día una seed/chunk sí fuerza el fallback.
**Ubicación:** `backend/src/world/build_room_layout.rs:88-217`, `grid_gen/build_rooms.rs:241+`
**Por qué:** Brecha de cobertura, no un bug ejecutable por sí sola — pero es la causa directa de
que el desync de la puerta de sala construible (ver sección Grave) lleve sin detectarse desde
ADR-081 enmienda 5.
**Plan:** Añadir un test de integración que, para un barrido de seeds/coords con
`room_in_chunk(..).is_some()`: genere el `LayerGrid` y obtenga el `door_side` resuelto real vía
`carve_into_grid`/`carve_door`; llame a `carve_into_layout` con el mismo `RoomPlan`; y afirme que
la arista `EDGE_KIND_DOOR` del `ChunkLayoutV1` corresponde al MISMO lado. Incluir un caso dirigido
que fuerce el fallback a propósito.

### [3] Reentrada rompe el `foreach` en `NotifyXListeners` de `IPCClient`
**Estado:** ✅ RESUELTO — commit `dbab66e9`
**Ubicación:** `Assets/Scripts/Network/IPCClient.cs:180-227` (los 6 métodos `NotifyXListeners`)
**Por qué:** El lock de C# es reentrante en el mismo hilo; un handler que se auto-desuscribe
dentro de su propio callback muta la lista mientras el `foreach` la recorre, y el
`InvalidOperationException` escapa sin capturar. Hoy no es alcanzable: todos los `Add`/`Remove`
existentes viven en `Awake`/`OnDestroy`, nunca dentro de un callback de notificación.
**Plan:** En cada uno de los 6 métodos, sustituir el `foreach` sobre la lista viva bajo lock por un
patrón snapshot-e-invoca: copiar a un array local dentro del lock, soltar el lock, iterar el
array. Cambio mecánico repetido 6 veces (no introducir un helper genérico — 6 firmas de delegado
distintas, y CLAUDE.md prohíbe abstracciones no pedidas para deuda de este tamaño).

### [3] `BuildRoomRegistry` crece sin poda durante toda la sesión
**Estado:** ✅ RESUELTO — commit `e0f7e42d`, efecto colateral del fix Medio "no se limpia al
reconectar": ahora acota a "chunks vistos desde la última reconexión" en vez de toda la sesión.
**Ubicación:** `Assets/Scripts/Gameplay/BuildRoomRegistry.cs:45-46`
**Por qué:** Cada entrada es un struct minúsculo, la clave está acotada por columnas de chunk
realmente visitadas (cientos, no miles, por sesión típica). Puro overhead de memoria de cliente,
sin vector de ataque.
**Plan:** No requiere trabajo propio — comparte el dict con el fix de "no se limpia al reconectar"
(sección Grave); ese fix acota automáticamente el crecimiento a "chunks vistos desde la última
reconexión".

### [4] Build de chunk sin throttle interno por tile
**Estado:** ⏸️ Diferido — se dispara en TODO chunk nuevo durante la exploración normal (no es un
edge case), y arreglarlo bien implica repartir el build en corutinas/varios frames: cambia timing
y orden real de aparición de chunks, no es un parche de una línea. Tratarlo con el mismo rigor que
Medio/Grave, no como limpieza automática.
**Ubicación:** `Assets/Scripts/Gameplay/GridWorld/ProceduralWorldGenerator.cs:446-501`,
`GridChunkBuilder.cs:319-451`
**Por qué:** El doble bucle de tiles hace `Instantiate`/`AddComponent` de suelo/techo/paredes/
dinteles/pilares sin ceder el frame — cientos de llamadas en un único `Update()`. Se dispara en
cada chunk nuevo durante exploración normal; el impacto es un stutter puntual, no rotura funcional.
**Plan:** Extender el presupuesto por-frame que ya existe (`maxChunkBuildsPerFrame`) a nivel de
TILE dentro de un chunk, vía corutina que haga `yield return null` cada N tiles. No parentar ni
marcar `_loaded` hasta terminar (regla ya exigida por `GridTestWorld`).

### [3] `prefabs.wall` sin null-check antes de `Instantiate`
**Estado:** ✅ RESUELTO — commit `dbab66e9`
**Ubicación:** `Assets/Scripts/Gameplay/GridWorld/GridChunkBuilder.Placement.cs:166,241,298`
**Por qué:** Solo `floor` tiene null-check explícito; `wall` no. Si `Resources.Load` falla solo
para Wall (import parcial de Resources), `Instantiate(null)` revienta en el primer panel de pared.
Exige una precondición de laboratorio (import corrupto selectivo), no alcanzable jugando normal.
**Plan:** Extender el check que ya existe para `floor` a `wall` en `GridPrefabSet.LoadFromResources`
y en `ChunkStreamer.Start()`, mismo patrón que ya usa `prefabs.pillar` como defensa en
profundidad.

### [3] `CaptureRebind()` falta null-check de `VoiceCapture`
**Estado:** ✅ RESUELTO — commit `dbab66e9`
**Ubicación:** `Assets/Scripts/UI/VoiceSettingsUI.cs:110`
**Por qué:** El NRE es real (única línea sin el `if (vc != null)` que sí tienen todos los demás
manejadores del fichero), pero re-trazando el único punto de instanciación (`GameBootstrap.Awake`)
`VoiceCapture` siempre existe antes que `VoiceSettingsUI` en el mismo `Awake` — el caso `null` solo
es alcanzable en una escena de prueba que no existe hoy en el repo.
**Plan:** Replicar el idiom que ya usan el resto de manejadores del mismo fichero: `var vc =
Voice(); if (vc != null) vc.PushToTalkKey = control.keyCode;`.

### [4] `OnDraft` no valida el orden de `firstIndex` antes de anexar puntos
**Estado:** ⏸️ Diferido — cambia qué pasa con un paquete de red real (fuera de orden/duplicado) hoy
lo dibuja igual (zigzag temporal), con el fix se descarta en silencio. Es un cambio de
comportamiento observable ante un caso vivo, no una guarda sobre algo inalcanzable — mismo rigor
que Medio/Grave.
**Ubicación:** `Assets/Scripts/Gameplay/SprayDraftReceiver.cs:145-171`
**Por qué:** Solo se manifiesta cuando UDP reordena/duplica paquetes de continuación durante el
dibujo en vivo de OTRO jugador; el resultado es un zigzag cosmético menor en una preview efímera
que se autocorrige sola al llegar la pintada autoritativa.
**Plan:** Antes del bucle que añade puntos, añadir `if (msg.firstIndex != 0 && msg.firstIndex !=
draft.NextIndex) return;` para descartar silenciosamente paquetes fuera de orden o duplicados.

### [3] `roster_gate_open` cuenta fantasmas vía `peers.len()`, fuerza reenvío por churn no-jugador
**Estado:** ⏸️ Diferido — se dispara en CADA spawn/despawn de fantasma, un evento normal y regular
de esta partida (no un edge case). Cambia tráfico real emitido en cada sesión jugada hoy, así que
se trata con el mismo rigor que Medio/Grave aunque el arreglo en sí sea mecánico.
**Ubicación:** `backend/src/network/sync.rs:604-615` y sus 5 call sites
**Por qué:** Cualquier alta/baja de fantasma dispara reenvío completo de rosters — solo tráfico
desperdiciado, contradice ADR-071 pero no rompe ninguna garantía de corrección.
**Plan:** `real_peer_count()` (`phantom.rs:20-25`) ya existe con el propósito exacto documentado
("must not count a phantom"). Sustituir `net.peers.len()` por `net.real_peer_count()` en las 5
llamadas. Cambio mecánico de una línea por call site.

---

## 4. Medio

9 ítems, nota 5-6 — **7/9 ✅ RESUELTOS** (commits `b8fc7b63` Rust, `e0f7e42d` C#), **2 ⏸️
PAUSADOS para confirmar** (rearquitectura real, no limpieza automática). Reales, alcanzables en
juego normal, pero de impacto acotado a un sistema secundario o requieren una secuencia de eventos
poco común aunque posible.

### [5] `processed_interactions` es un `HashSet` sin poda ni tope
**Estado:** ✅ RESUELTO — commit `b8fc7b63`
**Ubicación:** `backend/src/game_loop.rs:534`
**Por qué:** A diferencia de sus 6 hermanos ya migrados a `BoundedDedupeSet`, crece sin límite
durante toda la vida del proceso. Crecimiento de memoria, no corrupción — pero un cliente puede
acelerarlo deliberadamente spameando pares distintos.
**Plan:** Migrar de `HashSet<(u16,u64)>` a `BoundedDedupeSet<(u16,u64)>`, mismo tipo que sus 6
hermanos. Cambio mecánico: actualizar 3 firmas que reciben `&mut HashSet<...>`; los call
sites/inserts siguen funcionando igual porque `BoundedDedupeSet::insert` devuelve `bool` con la
misma semántica.

### [6] Colisión de namespace `"name:"` en `sanitize_player_key`
**Estado:** ✅ RESUELTO — commit `b8fc7b63`
**Ubicación:** `backend/src/persistence/mod.rs:25-38`
**Por qué:** `raw="name:Joel"` produce la MISMA key que el fallback anónimo de un "Joel" sin
identidad. Vector real pero acotado: el IPC es loopback-only, cada jugador tiene su propio
proceso/disco — solo explotable entre dos instancias de la MISMA máquina/cuenta (un escenario de
testing ya documentado del proyecto), nunca remoto.
**Plan:** En `sanitize_player_key`, reservar el prefijo `"name:"` en exclusiva para el fallback
generado por el servidor: si el `raw` sanitizado empieza por ese prefijo literal, tratarlo como si
hubiera llegado vacío y caer al camino de fallback que deriva la key desde `player_name` (dato que
controla el servidor, no el cliente). Mismo principio que ADR-068/081 ya aplicó a
Spray/StpPlaceRequest. Añadir un test que fije el comportamiento.

### [5] `select!` sin shutdown ordenado: un fallo transitorio del `accept()` del IPC aborta
`game_loop` a mitad de tick
**Estado:** ⏸️ Pausado para confirmar — es rearquitectura real de control de flujo (señal de
apagado ordenado entre `ipc_handle`/`game_handle`, tolerancia a errores transitorios en el accept
loop), no un parche de una línea. Se implementa siguiendo el plan de abajo en cuanto se confirme.
**Ubicación:** `backend/src/main.rs:198-203`
**Por qué:** Si `ipc_handle` termina primero, `select!` sale de inmediato y tokio aborta
`game_handle` sin flush ni aviso a peers. El disparador (un `accept()` fallando en un listener
local de loopback) requiere una secuencia poco común. El autosave periódico (~3 min) acota la
ventana de pérdida real.
**Plan:** (1) En `ipc/server.rs`, hacer que el loop de `accept()` tolere errores transitorios
(loguear y `continue` en vez de propagar con `?`). (2) Reemplazar el `select!` desnudo por una
señal explícita de apagado ordenado (canal `watch`/`oneshot` o `CancellationToken`): cuando
cualquiera de las dos tareas termine, dar a la otra una última oportunidad de guardar y avisar a
peers, en vez de un abort implícito.

### [6] Registro de salas construibles no se limpia al reconectar a otro mundo
**Estado:** ✅ RESUELTO — commit `e0f7e42d`
**Ubicación:** `Assets/Scripts/Gameplay/BuildRoomRegistry.cs:44-49`; disparador en
`NetworkInitializer.StartAsHost`/`StartAsJoiner`
**Por qué:** Alcanzable en juego normal (reconectar sin reiniciar el proceso). Pero los 4
consumidores reales son puramente cliente/feedback — la autoridad vive en `process_stp_place` del
backend, que re-valida contra el seed actual. El daño real es cosmético-persistente: borde de
sala fantasma, feedback de construcción equivocado, material/lámparas erróneos.
**Plan:** Añadir `BuildRoomRegistry.ResetForNewConnection()` (separado del ya existente
`Clear_EditorTestsOnly()`) y llamarlo desde `NetworkInitializer` en ambos puntos de arranque de
sesión. `ZoneRegistry.cs` tiene EXACTAMENTE el mismo patrón (ver más abajo) — limpiar uno sin el
otro reintroduce el desacople al instante, así que un solo cambio debe limpiar ambos registros.

### [5] `ClaimMarkerDefId` sin test que lo ate al asset ni al backend
**Estado:** ✅ RESUELTO — commit `e0f7e42d`
**Ubicación:** `Assets/Scripts/Gameplay/Building/BuildPermission.cs:30-35`; hueco de cobertura en
`BuildPermissionTests.cs`
**Por qué:** Los tres valores coinciden HOY, pero ya hay precedente real (no hipotético) de que la
red de seguridad se pierda en un refactor — el test que lo vigilaba desapareció y no se repuso. Si
diverge, el fallo es SILENCIOSO. Mitigado parcialmente: el creador de assets se niega a
regenerarlo si ya existe.
**Plan:** Reponer un test EditMode equivalente al desaparecido, junto a `RoomSizeMirrorsTheBackend`
(mismo fichero, mismo patrón "espejo del backend"): cargar el asset autorado real y afirmar que su
`.Id` coincide con `ClaimMarkerDefId`, con un literal hardcodeado del valor Rust esperado y
comentario apuntando a `game_loop.rs:4919`.

### [6] Cascabel de spray (`_oneShot`) nunca enruta al mixer SFX
**Estado:** ✅ RESUELTO — commit `e0f7e42d`
**Ubicación:** `Assets/Scripts/Gameplay/Audio/SpraySfx.cs:85-94,113-118,155-164`
**Por qué:** `RouteToSfxMixer()` solo enruta `_source` (el siseo), nunca `_oneShot` (el cascabel).
Alcanzable en cada uso del spray, pero acotado a un único cue de audio cosmético — no rompe una
feature ni corrompe estado.
**Plan:** En `RouteToSfxMixer()`, extender el bloque para asignar también
`_oneShot.outputAudioMixerGroup = group` en la misma pasada. Corregir el comentario de cabecera
que hoy afirma el enrutado resuelto cubriendo solo la mitad. Ampliar el test existente
(`SpraySfxTests.cs`) para afirmar que ambas fuentes comparten mixer group tras `Shake()`.

### [5] Material/Texture del shaft nunca se liberan en cada regeneración
**Estado:** ✅ RESUELTO — commit `e0f7e42d`
**Ubicación:** `Assets/Scripts/Gameplay/Shaft/VerticalShaftChunk.cs:245-353` (mismo patrón en
`VerticalShaftGrid.cs`)
**Por qué:** Fuga real y acumulativa, pero el propio fichero se documenta como "standalone
prototype... NOT part of the validated pipeline" y el único invocador fuera de `Start()` es un
menú de Editor — no lo dispara ningún jugador conectado en juego normal.
**Plan:** Convertir `BuildMaterials()`/`BuildGrateTexture()` en caché estática perezosa, mismo
patrón que `FluorescentHumDirector.ResolveClip()` y los `_sharedHiss`/`_sharedRattle` de
`SpraySfx`. No destruir esos recursos desde `Clear()` una vez cacheados (son `sharedMaterial`
entre instancias). Añadir un test EditMode (hoy no existe ninguno para `Shaft/`).

### [6] Trazo ajeno nace negro y fino si el primer paquete se pierde
**Estado:** ✅ RESUELTO — commit `e0f7e42d`
**Ubicación:** `Assets/Scripts/Gameplay/SprayDraftReceiver.cs:136-163`
**Por qué:** Paquete inicial perdido, o entrar en alcance a mitad del trazo de otro jugador, son
eventos normales. El efecto (Color=0, Width=0) es visualmente persistente mientras dura el trazo,
no un parpadeo — pero acotado a una preview efímera que no se guarda y se autocorrige sola.
**Plan:** Sacar la asignación de `draft.Color`/`draft.Width`/`draft.Layer` del bloque
`firstIndex==0` para que se ejecute también en la rama de recuperación a mitad de trazo. Seguro:
el emisor ya manda color/width/layer en TODOS los paquetes, no solo el primero.

### [5] Guardia anti-overflow del anillo de voz (`Available()<0`) inalcanzable
**Estado:** ⏸️ Pausado para confirmar — el fix rediseña la detección de overflow con contadores
`Interlocked` compartidos entre el hilo de audio y el principal (single-writer/single-reader sin
locks); es rework de concurrencia real, no un guard mecánico. Se implementa siguiendo el plan de
abajo en cuanto se confirme.
**Ubicación:** `Assets/_Migration/STPIntegration/RemoteAvatar/RemoteVoicePlayer.cs:257`
**Por qué:** `Available()` normaliza matemáticamente su resultado a `[0, ring.Length)` por
construcción — la condición es una tautología falsa, no una condición de carrera real. Tras un
corte de red breve (evento común), la voz de ese peer suena entrecortada/adelantada sin que la
protección documentada dispare nunca.
**Plan:** Sustituir la detección basada en resta modular por un contador explícito de muestras
pendientes (`Interlocked.Increment`/`Decrement`, lock-free, compatible con el diseño
single-writer/single-reader ya documentado). Comparar ese contador sin módulo contra la capacidad
real del anillo. Añadir un test que fuerce una ráfaga de más de `RingFrames` tramas en un solo
`Push`/`DecodeInto`.

---

## 5. Grave

13 ítems, nota 6-8 — **1/13 ✅ RESUELTO** de rebote (`ZoneRegistry`, commit `e0f7e42d`, junto con
el fix Medio gemelo de `BuildRoomRegistry`). Bugs reales y alcanzables en juego normal (no
requieren laboratorio), rompen una garantía importante — pero no llegan al nivel de "cualquiera
puede afectar el estado de otro jugador sin límite" de la sección Muy grave.

### [8] `handle_packet` migra `addr` de un peer activo sin prueba
**Ubicación:** `backend/src/network/handlers.rs:19-70`; `send.rs:120-153`
**Por qué:** Basta alcanzar el puerto UDP del host desde un socket nuevo con el `sender_id` de un
peer ya conectado (mientras esa IP no esté reclamada) para secuestrar temporalmente su enrutamiento
de reliables/veredictos. Se autocura cuando la víctima real vuelve a hablar, lo que lo mantiene
por debajo de "control total permanente".
**Plan:** Endurecer la condición de adopción de `addr` para que solo migre a una dirección nueva
cuando el heartbeat del peer en su dirección ACTUAL ya esté vencido (mismo umbral que la detección
de desconexión), no con el primer paquete que reclame ese `sender_id`. Preserva el caso legítimo
de NAT-rebind mientras cierra el secuestro de un peer todavía activo. **Toca el modelo de
confianza de red ya comentado con ADR-015/079 — tratar como cambio de arquitectura validada y
pedir visto bueno explícito antes de implementar (regla 2 de CLAUDE.md).**

### [7] `allocate_peer_id` acuña el id que pide el propio cliente
**Ubicación:** `backend/src/network/handlers.rs:1031-1037`
**Por qué:** El id que el host asigna sale literalmente de lo que el cliente puso en su Handshake,
aceptado tal cual si no está en uso — raíz que priva de valor probatorio a `sender_id` en varios
de los hallazgos Muy grave, aunque explotarlo por sí solo requiere una ventana en la que ese id
concreto esté libre.
**Plan:** Eliminar el atajo que devuelve `requested_id` tal cual; minar SIEMPRE el id desde el
contador propio `next_peer_id`, sin aceptar nunca el valor del cliente. El comentario de limpieza
al desconectar ya documenta que los ids se reciclan y son asignados por el host, así que no rompe
ningún contrato de reconexión existente — confirmar con un grep rápido en el lado Unity/IPC antes
de quitar la rama.

### [7] Escritura de saves sin `fsync` antes del rename atómico
**Ubicación:** `backend/src/persistence/save.rs:196-200` (mismo patrón en `player_save.rs:45-49`)
**Por qué:** Patrón "no durable sin fsync" reconocido en la industria. Atómico en VISIBILIDAD
(nunca se ve un JSON a medio escribir) pero no durable ante un corte de luz cronometrado en la
ventana de flush perezoso del SO. Alcanzable en juego normal (crashes/apagones ocurren, y el
autosave repite la ventana cada ~3 min) en un survival donde perder progreso duele especialmente.
No sube a Muy grave: el peor caso es un rollback silencioso al guardado anterior (intacto), nunca
un archivo corrupto visible.
**Plan:** En `SaveFile::save_to` y `PlayerFile::save_to`, sustituir `std::fs::write` por una
apertura explícita (`File::create` + `write_all`) seguida de `file.sync_all()?` antes del
`rename`. Extraer un helper único `atomic_write_json(path, json)` en `persistence/mod.rs` que
ambos `save_to` llamen, en vez de duplicar el `sync_all`. Alcance: solo `save.rs`/`player_save.rs`.

### [7] `CONNECT_TO` inválido deja `is_host` mal fijado y cuelga al joiner en silencio
**Ubicación:** `backend/src/main.rs:99-100,177-186`
**Por qué:** Alcanzable en juego normal — un typo de IP o un hostname (`SocketAddr::parse` no
resuelve DNS) en el campo de Join real, sin validación previa en la UI, rompe por completo la
feature de unirse a partida. `is_host` queda mal fijado, `world_seed_known` nunca llega a `true`,
`game_loop.rs:1064` bloquea la carga del personaje indefinidamente — sin crash, sin mensaje visible
para Unity. Acotado al propio jugador que intenta unirse.
**Plan:** Mover el parseo de `CONNECT_TO` ANTES de fijar `is_host`; si falla, no seguir arrancando
en un estado a medias — loguear con detalle y `std::process::exit(1)` (fail-fast) en vez de dejar
pasar la ejecución. Complemento cliente: `JoinSessionUI.OnJoinClicked` debería validar formato de
IP antes de llamar a `StartAsJoiner`, dando feedback inmediato en vez de lanzar un backend
condenado a colgarse.

### [6] `.expect()` en el bind UDP aborta todo el proceso, se lleva la conexión IPC ya aceptada
**Ubicación:** `backend/src/main.rs:154-156`
**Por qué:** Sin `catch_unwind` en el crate, cualquier panic mata el proceso entero. La causa
típica (puerto ocupado por un backend huérfano previo) es un problema RECURRENTE ya documentado
del proyecto, no de laboratorio. Ocurre DESPUÉS de que el servidor IPC ya pudo haber aceptado la
conexión de Unity — se la lleva consigo. Acotado al jugador que arranca su propio backend, antes
de conectar con nadie más.
**Plan:** Sustituir el `.expect(...)` por un `match` explícito: en `Err`, loguear un mensaje
accionable (puerto en uso, sugerir matar backend huérfano o cambiar `NET_PORT`) y terminar con
`std::process::exit(1)`. Reordenar el arranque para bindear el socket P2P ANTES de spawnear la
tarea `ipc_handle`, eliminando la carrera con la conexión ya aceptada.

### [7] Lado de puerta: colisión fija vs. render con fallback de 4 lados
**Ubicación:** `backend/src/world/build_room_layout.rs:79-85` (cruzar con
`grid_gen/build_rooms.rs:178-183,194-239`)
**Por qué:** `carve_door` (render) prueba los 4 lados en orden y abre la puerta VISUAL en el
primero que conecta con algo transitable; `carve_into_layout` (colisión) abre siempre en
`plan.door_side`, sin fallback ni forma de saber qué lado ganó — `carve_into_grid` ni siquiera
devuelve esa información. Alcanzable en juego normal: el emplazamiento deja margen ajustado a los
4 lados, exactamente el motivo por el que el fallback de render existe. Rompe la única sala
construible del mundo.
**Plan:** Hacer que `carve_into_grid`/`carve_door` devuelvan el `door_side` RESUELTO, no solo las
celdas (nuevo tipo `CarvedRoom { carved, door_side }`). Propagar ese valor a los 3 llamadores, y
crucialmente hacerlo llegar al wire `GridChunkData.build_room` (hoy manda el `door_side` SIN
resolver) para que el cliente tampoco herede el lado equivocado. En `world/generator.rs`, exponer
una función pura en `grid_gen` que resuelva el lado antes de invocar `carve_into_layout`.

### [8] `WinMmCapture.Read()` siempre escribe desde `dest[0]`
**Ubicación:** `Assets/Scripts/Network/WinMmCapture.cs:140-169`; llamador
`VoiceCapture.cs:542-557`
**Por qué:** Alcanzable en juego normal para cualquier jugador cuya interfaz de audio dispare el
fallback WinMM (el escenario exacto para el que existe ADR-046). `PumpWinMm` espera que `Read()`
anexe tras el remanente, pero siempre empieza en índice 0 — la siguiente lectura pisa el
remanente con muestras nuevas. Audio de voz corrompido/reordenado de forma determinista y
repetible para justo la audiencia que más necesita el fallback.
**Plan:** Añadir parámetro de offset de destino: `Read(short[] dest, int offset, int maxSamples)`.
Cambiar `Marshal.Copy(hdr.lpData, dest, written, samples)` a
`Marshal.Copy(hdr.lpData, dest, offset + written, samples)`. Actualizar el único call-site
(`PumpWinMm`) para pasar `_winmmFill` como offset. Diff acotado a 2 archivos.

### [7] `BuildZoneSign.Build()` crea un `Material` nuevo sin cachear
**Ubicación:** `Assets/Scripts/Gameplay/GridWorld/BuildZoneSign.cs:164`
**Por qué:** Cualquier chunk con sala construible que se descargue y vuelva a streamear (caminar
dentro/fuera de rango, constante en esta MMO) deja huérfano un `Material` nativo por cartel.
`ChunkStreamer` solo destruye el GameObject raíz, no libera el `sharedMaterial`. Fuga que degrada
la sesión con más horas jugadas.
**Plan:** Replicar el patrón que ya usa `GridChunkBuilder.BuildRoomMaterial()` en el mismo
proyecto: campo estático `_plateMat` con getter lazy. Sustituir la creación inline por la llamada
al getter cacheado. Confirmar primero (grep) que ningún punto muta la instancia por-cartel
individualmente.

### [7] `SprayRenderer.Show()` borra siempre la preview local (key 0)
**Ubicación:** `Assets/Scripts/Gameplay/SprayRenderer.cs:283`
**Por qué:** Basta que otro jugador materialice una pintada en cualquier pared, o que el streamer
hidrate un chunk lejano, para que se borre el trazo propio en curso. Sin auto-recuperación
inmediata: `RefreshPreview()` solo se re-dispara al añadir un punto nuevo, así que si el jugador
mantiene el gatillo sin mover lo suficiente la mira, la pared queda en blanco de forma indefinida,
no un parpadeo de un frame.
**Plan:** Aplicar el mismo principio "identificar al autor antes de actuar" que ADR-068/081 ya
fijaron: comparar el `place_id` entrante contra el que el jugador local tiene pendiente de
confirmar, o más simple, mover la llamada `ClearPreview()` al punto donde `SprayPainter` ya sabe
que acaba de hacer `Commit()` de su propio trazo, en vez de reaccionar genéricamente a cualquier
`Show()`.

### [8] Marcadores de depuración vertical visibles por defecto en producción
**Ubicación:** `Assets/Scripts/Debug/VerticalDebugMarkerRenderer.cs:17`; instanciado
incondicionalmente en `GameBootstrap.cs:27`
**Por qué:** Confirmado en cascada: el componente existe en TODO build (sin `#if`, a diferencia de
otros toggles debug del mismo `Awake`); `showMarkers=true` sin la guarda que sí tiene su hermano
`PoiDebugHud`; y el backend envía la lista de marcadores en CADA `WorldStateMsg` sin gate de
entorno. En un build shipped, TODO jugador vería cubos de colores marcando escaleras/pozos/atrios
del backend — rotura visible y persistente para el 100% de la base de jugadores.
**Plan:** Aplicar el patrón exacto de `PoiDebugHud.cs:15-19`: envolver `showMarkers = true` en
`#if UNITY_EDITOR || DEVELOPMENT_BUILD ... #else false #endif`. Mantiene el campo editable desde
Inspector para QA, cambia el default shipped a apagado. Hardening secundario fuera de alcance:
considerar si el backend debería dejar de poblar esta lista en sesiones release (decisión de
protocolo/ADR aparte).

### [7] `drop_peer_over_verdict_backlog` no protege fantasmas, desincroniza `peers`/`phantom_ids`
**Ubicación:** `backend/src/network/send.rs:184-225`; comparar con `mod.rs:735`,
`phantom.rs:105-118`
**Por qué:** Alcanzable en juego normal por cualquier jugador (fabricar 256+ paquetes con
`victim_id`/`requester_id` = id determinista del fantasma). Deja el fantasma en estado zombie
(fuera de `net.peers`, aún en `phantom_ids`), emite un `player_left` FALSO, y el robapieles pierde
pose/relay sin poder respawnear hasta reasignación casual. Acotado al subsistema fantasma (una
entidad NPC), exige volumen (256 paquetes) en vez de un único paquete.
**Plan:** Añadir la misma guarda que ya usa `process_retransmits` (`if self.is_phantom(pid) {...}`)
a `drop_peer_over_verdict_backlog`: comprobar `is_phantom` ANTES de `peers.remove`/
`push_pending_event(PeerDisconnected)`. Si es fantasma, limpiar solo la cola y retornar sin tocar
`net.peers`/`phantom_ids`. Añadir un test que fuerce el desborde con un fantasma y compruebe que
sigue vivo en ambas estructuras.

### [7] `STRUCTURE_ZONES` fuga memoria vía `Box::leak` al recambiar seed
**Ubicación:** `backend/src/world/zone_density.rs:47-104`
**Por qué:** Desconectarse y unirse a otra partida con seed distinta sin cerrar el proceso es un
flujo estándar de MMO. `reset_for_remote_world` limpia otras cachés pero no esta, porque es un
`static` de módulo, no un campo de `World`. Degrada el proceso backend LOCAL del jugador que
reconecta, no el de otros — sin robo/pérdida de datos ajenos.
**Plan:** Sustituir `HashMap<u64, &'static _>` (vía `Box::leak`) por `HashMap<u64, Arc<_>>`. Añadir
`reset_cache()` en `zone_density.rs` y llamarlo desde `World::reset_for_remote_world` junto a las
líneas que ya limpian `v30a_chunk_cache`/`world_graph`. Añadir un test que compruebe que el caché
nunca retiene más de 1 entrada viva tras un reset.

### [8] `ZoneRegistry` no se limpia al reconectar a otro servidor
**Estado:** ✅ RESUELTO — commit `e0f7e42d`. Efecto colateral correcto del fix Medio de
`BuildRoomRegistry`: los dos registros comparten causa raíz (mismo patrón reset-solo-al-arrancar-
proceso) y se limpian juntos desde el mismo punto en `NetworkInitializer`.
**Ubicación:** `Assets/Scripts/Gameplay/ZoneRegistry.cs:36-42`
**Por qué:** Mismo patrón que `BuildRoomRegistry` mas arriba, pero PEOR: `TryGetZone` devuelve
`true` de inmediato con el dato del mundo anterior, contaminando SIMULTÁNEAMENTE
`BuildPermission.ChunkKnown` y la tirada de loot por zona de `ChunkLootManager`, durante TODA la
sesión nueva y sin auto-corrección posible.
**Plan:** Añadir `ZoneRegistry.ResetForNewSession()` que limpie solo `_zoneByChunk` (sin tocar
suscriptores del evento `ZoneArrived`). Añadir el equivalente en `BuildRoomRegistry`, compartiendo
lógica con `Clear_EditorTestsOnly()`. Invocar ambos desde un único método nuevo en
`NetworkInitializer`, en el mismo punto donde ya se llama a `ArmSessionEndHandler()`.

---

## 6. Muy grave

6 ítems, nota 9-10. Explotables HOY por cualquier jugador conectado, sin autenticación extra, con
impacto serio: control total sobre el estado de otro jugador o del mundo compartido. **Este es el
bloque que más justifica una pausa y una enmienda de ADR antes de tocar código** (varios cambian
el modelo de confianza del protocolo P2P — regla dura #7 de CLAUDE.md).

Los 6 comparten la misma raíz y el mismo arreglo de referencia: `SprayPlaceRequest`/
`StpPlaceRequest` YA se corrigieron con el patrón "`requester_id`/`attacker_id` se deriva de
`sender_id` de la cabecera del paquete, nunca del campo que el cliente declara en el payload"
(ADR-068/081, `handlers.rs:276-311`). Los 6 de aquí son ese mismo patrón sin aplicar.

### [9] `PvpHitCandidate` confía `attacker_id`/`victim_id` del payload
**Ubicación:** `backend/src/network/handlers.rs:434-435`; `game_loop.rs:4371-4445`
(`process_pvp_hit_candidate_host`), `4268-4312` (`validate_pvp_hit`)
**Por qué:** Cualquier tercer peer conectado, sin autenticación extra, fuerza daño letal atribuido
a un jugador A contra un jugador B sin que A haga nada, mientras A y B estén en rango entre sí
(situación normal en juego poblado) — control total sobre el estado (vida) de otros dos jugadores.
**Plan:** Sacar `PvpHitCandidate` de la lista de dispatch 1:1 automática de `handlers.rs` (igual
que `StpPlaceRequest`) y darle un arm explícito que fije `attacker_id` desde `sender_id` de la
cabecera, descartando el del payload. No hace falta tocar `validate_pvp_hit` ni
`process_pvp_hit_candidate_host` una vez que el evento solo lleva un `attacker_id` fiable.
**Confirmar con una enmienda breve de ADR-029 si se considera cambio de modelo de autoridad.**

### [9] `StpCarryablePickupRequest` sin distancia ni identidad
**Ubicación:** `backend/src/game_loop.rs:2195-2202` (dispatch), `5233-5272`
(`process_stp_carryable_pickup`); `handlers.rs:373`
**Por qué:** Cualquier cliente que conozca los ids del roster (`StpCarryableList` a 10Hz) puede
vaciar TODO el loot de construcción del mundo desde cualquier distancia, un paquete por item, sin
límite — control total sobre un recurso compartido central de la economía del juego.
**Plan:** Resolver `requester_pos` desde `net.peers.get(&requester_id)` igual que el arm hermano
`StpPickupRequest` (patrón F0.7), y añadir una comprobación tipo `pickup_within_reach` antes de
retirar/conceder el carryable. Sacar el mensaje de la lista macro de `handlers.rs` a un arm
explícito que fije `requester_id: sender_id`.

### [9] `CorpseSpawnRequest`/`CorpseTakeRequest` sin ninguna protección
**Ubicación:** `backend/src/game_loop.rs:2335-2424` (dispatch), `2810-2872`; `protocol.rs:796-815`
**Por qué:** `requester_id` Y `requester_pos` vienen enteramente del payload sin ninguna
mitigación (peor que el de pickup); además `apply_corpse_spawn_request` acepta los `items` del
payload sin validarlos contra nada que el requester realmente poseyera — vía de duplicación/
inyección de items que rompe directamente el pilar de escasez de loot del juego.
**Plan:** Sacar ambos mensajes a arms explícitos que fijen `requester_id: sender_id`. Para
`CorpseTakeRequest`, resolver `requester_pos` desde `net.peers` (patrón F0.7) en vez del payload.
Para `CorpseSpawnRequest`, añadir validación server-side de `items` antes de `spawn_corpse` — si
el backend no trackea inventario STP server-side hoy, marcarlo como decisión de diseño aparte
antes de comprometerse a esa parte.

### [10] `StpBuildAddRequest`/`StpDemolishRequest` sin owner ni identidad ⚠️ requiere ADR (bump de wire)
**Ubicación:** `backend/src/game_loop.rs:5086-5129` (`process_stp_build_add`), `5138-5186`
(`process_stp_demolish`); `protocol.rs:532-536,593-596`
**Por qué:** El wire de estos dos mensajes NI SIQUIERA transporta `requester_id` o posición —
cualquier peer conectado progresa o DEMUELE con un solo paquete la construcción de cualquier otro
dueño, en cualquier parte del mundo, anulando por completo el territorio de ADR-081. Es el único
par STP que no hereda el patrón de fix ya aplicado a `StpPlaceRequest`. **La nota más alta de toda
la auditoría.**
**Plan:** Cambio de wire: añadir `requester_id: u16` a ambos mensajes en `protocol.rs` y bumpear
`WIRE_SCHEMA_VERSION` — **requiere ADR nuevo (regla 7 de CLAUDE.md)**, exactamente el hueco que ya
anticipaba el comentario ADR-081 de `handlers.rs:273` sin completarlo. Sacar ambos variants a arms
explícitos con `requester_id: sender_id`. Añadir comprobación de propiedad contra
`building.owner_id` (ya existe en `StpBuildingInfo`) antes de aplicar, rechazando con
`reason=not_owner` en el mismo estilo que otros rechazos ya usados en el fichero.

### [10] `process_stp_harvest_hit` acepta `amount` sin tope ni distancia
**Ubicación:** `backend/src/game_loop.rs:5278-5312`; `protocol.rs:773-777`
**Por qué:** `amount` está enteramente controlado por el cliente (solo `.abs()`), sin tope máximo
ni distancia y sin `requester_id`/posición en el wire — un único paquete deja `remaining=0` en
cualquier `harvestable_id` del mapa desde cualquier distancia, arrasando un recurso compartido
completo.
**Plan:** **Fix inmediato sin tocar el wire:** clampear `amount` server-side a una constante
`MAX_HARVEST_HIT_AMOUNT` dimensionada a la herramienta legítima más fuerte, en vez de solo
`.abs()` — cierra hoy mismo el vector de "un paquete vacía cualquier recurso". **Fix de
seguimiento (necesita ADR + bump de wire):** añadir `requester_id` al mensaje, sacarlo a un arm
explícito, y añadir comprobación de distancia estilo F0.7 antes de aplicar el golpe.

### [9] `WorldInteractRequest` sin pin de posición ni de identidad
**Ubicación:** `backend/src/network/handlers.rs:681-706`; `game_loop.rs:2241-2265,5399-5443`;
`world/mod.rs:1236-1278`
**Por qué:** El chequeo de alcance de 5m queda neutralizado porque usa el `player_position` que
declara el propio payload sin corroborarlo — permitiendo recoger/soltar items desde cualquier
punto del mapa. Además `requester_id` de `Interact` también viene del payload sin pin a cabecera —
el spoof de identidad viaja gratis en el mismo mensaje. Doble vector, y alcance amplio: todo el
sistema genérico de interacción, no solo STP.
**Plan:** Identidad: en el arm explícito `PacketPayload::Interact`, fijar `requester_id` a
`sender_id` de la cabecera (misma sustitución ADR-068/081), lo que además vuelve a dar sentido al
guard `unknown_requester` ya presente. Posición: dejar de pasar el `player_position` del payload
directamente al chequeo de 5m; resolver la posición real del requester desde `net.peers` con el
mismo patrón F0.7 que `process_stp_pickup`.

---

## Orden de trabajo sugerido

1. **Código muerto** (sección 1) — ✅ **17/27 hecho + 1 parcial** (commits
   `0da821ca`/`91c9f62a`/`1c7840e0`). Quedan 3 conservados a propósito (diseño documentado con
   motivo propio — `RoomSpawner`, `RemoteVoicePlayer.SetMuted`, `HostEndpointConfig`) y el de
   `PacketType`, que necesita confirmación del lado C# antes de tocar nada (protocolo).
2. **Muy leve + Leve** (secciones 2-3) — ✅ **10/13 hecho** (commits `40493fcd`/`dbab66e9`/`e0f7e42d`).
   Los 3 diferidos (throttle de chunk, orden de spray, conteo de fantasmas en roster gate) cambian
   comportamiento en un camino real de hoy — pasan a tratarse con el mismo rigor que Medio/Grave.
3. **Medio** (sección 4) — ✅ **7/9 hecho** (commits `b8fc7b63`/`e0f7e42d`), incluidos los dos
   registros estáticos que no se reseteaban al reconectar (`BuildRoomRegistry`/`ZoneRegistry`,
   arreglados juntos como señalaban sus propios planes — el de `ZoneRegistry` era Grave y cayó de
   rebote). Quedan 2 pausados para confirmar: `select!` sin shutdown ordenado y la guardia
   anti-overflow del anillo de voz — ambos son rearquitectura real (control de flujo /
   concurrencia), no limpieza mecánica.
4. **Grave** (sección 5) — 12 ítems restantes (1/13 ya resuelto de rebote). El de
   `handle_packet`/secuestro de `addr` pide visto bueno explícito antes de tocar el modelo de
   confianza de red (ADR-015/079). El resto son fixes contenidos por fichero.
5. **Muy grave** (sección 6) — 6 ítems, el bloque de seguridad de red. Dos de ellos
   (`StpBuildAddRequest`/`StpDemolishRequest` y, en su fix de seguimiento, `harvest_hit`) requieren
   ADR nuevo por tocar el formato de wire — **parar y decidir con Joel antes de implementar**,
   como exige la regla dura #7 de CLAUDE.md. Los demás (PvP, corpse, carryable pickup,
   WorldInteract) siguen el patrón ya existente en el código (`requester_id: sender_id`) y no
   deberían necesitar ADR nuevo, solo aplicar un patrón ya validado — pero al ser el bloque de
   mayor alcance, vale la pena una revisión conjunta antes de empezar.
