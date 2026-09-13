> **PLAN — 2026-09-13.** Prototipado de la cartografía en papel (`docs/MAPPING-ROADMAP.md`). **Cero código.**
> P0.1 detallado abajo, **pendiente de validación de Joel** antes de implementar (flujo /plan).

# MAPPING-PROTOTYPE.md — de la maqueta al juego, en pasos que se pueden jugar

## 0. Reglas del prototipado

1. **Primero, si es divertido.** P0 es local: sin red, sin guardado, sin items reales. No toca protocolo,
   formato de chunk ni schema de guardado, así que **no pide ADR** (regla dura 7). El ADR de M0 se escribe
   al empezar P1, con lo aprendido en P0.
2. **Cada paso responde preguntas de playtest.** Si no hay pregunta que responder, el paso no entra.
3. **Se mide el coste desde el primer commit** (ms por muestra, GC). Nada de optimizar después.
4. **Diffs pequeños:** un paso de más de ~300 líneas se parte en commits (regla dura 5).
5. **Dónde:** Unity se prueba en el **clon principal** (el editor de Joel está abierto ahí; desde un
   worktree Unity no ve los cambios). La lógica pura y sus tests se pueden escribir en el worktree.
6. **Verificación autónoma:** tests EditMode + Play con inyección IPC y `Editor.log`. Nada de pedir test manual.

## 1. Fases

### P0 — ¿Es divertido? (local)

| Paso | Qué | Hecho cuando | Preguntas de playtest |
|---|---|---|---|
| **P0.1** | **Recuerdo:** muestreo alrededor del jugador, búfer de 60 s, vista de depuración | §2.6 | ¿Qué se siente como «lo que he visto»? ¿Radio 5 m? ¿Coste asumible? |
| P0.2 | **Hoja y dibujar:** tecla `N`, una hoja, un boli, trazos en RenderTexture, gasto de tinta | Se dibuja la zona de la hoja desde el recuerdo; lo repetido no se cobra | ¿Dibujar quieto da tensión o pesa? ¿60 s es mucho o poco? |
| P0.3 | **Flechas de borde + «Ubicarme»** | Cruzar un borde crea el enlace; umbrales 70/40 con recuerdo de 8 s | ¿Los umbrales se sienten justos? ¿Te orientas? |
| P0.4 | **`M` en la base:** plano con ancla fija, pasar a limpio, tres capas | Colocación solo por enlaces; limpia tapa borrador | ¿Volver a ordenar el mapa apetece o molesta? |

### P1 — Que exista de verdad (necesita ADR de M0)

Hoja como item (`sheet_id` en `ItemProperty`, que solo guarda números), almacén de hojas en el host,
guardado. Aquí se escribe el ADR con lo aprendido en P0.

### P2 — Red

Sincronizar hojas entre peers (payload bajo demanda), hoja al cadáver. Bump de wire en las dos puntas
en el mismo commit (`WireSchema.Expected`).

### Después del playtest

Solo lo que lo gane: letra generada, tintas y papeles, taco y libreta, tablilla de post-its,
archivadores, copia y volcado, plano de bolsillo, radio y facción (bloqueado por ADR de facciones),
dispositivos (bloqueado por crafteo ADR-064).

---

## 2. P0.1 — Recuerdo (plan detallado)

### 2.1 Objetivo

Un búfer local que guarda, cada 0,5 s y durante los últimos 60 s, **qué celdas de 0,5 m ha visto el
jugador y cuáles eran pared**, repartidas por **zona = chunk × planta**, con una vista de depuración para
verlo en Play. Es la base que consumen P0.2 (dibujar) y P0.3 (ubicarse).

**Fuera de alcance:** dibujar, hojas, tecla `N`, red, guardado, items.

### 2.2 Lo que ya existe (verificado 2026-09-13)

| Pieza | Dónde | Uso en P0.1 |
|---|---|---|
| Jugador local | `LocalPlayerLocator.Find<T>()` (`Assets/Scripts/Network/LocalPlayerLocator.cs:28`); el jugador real lleva `CharacterControllerMotor` (patrón en `PlayerPoseTransmitter.cs`) | Posición del jugador, cacheada por el llamador |
| Colisión de WG3 | `Wg3SceneAssembler.AddColliders` (`Wg3SceneAssembler.cs:1810`): `BoxCollider` por volumen sólido; los prismas llevan collider de malla | Sondeo de pared por física |
| Máscara de geometría | `GridChunkBuilder.GeoMask` (`GridChunkBuilder.cs:182`), ya usada contra WG3 por `OfficeAmbienceDirector` y `ChunkLootManager` | Máscara de las consultas |
| Tamaño de chunk | `Wg3ChunkStreamer.ChunkSize = 50f` (`Wg3ChunkStreamer.cs:23`) | Zona |
| Altura de planta | `Wg3StoreyLayers.StoreyM = 3.32f` (`Wg3SceneAssembler.cs:90`) | Planta |
| Tests EditMode | `Assets/Tests/EditMode/`, asmdef `EditModeTests` → referencia `BackroomsSurvival` | Tests de la lógica pura |
| Input | Input System; `M` y `N` libres | No se usa en P0.1 |

**El cliente NO tiene el ráster de celdas de WG3**: lo calcula el servidor. Por eso P0.1 sondea colliders.

### 2.3 Diseño

**Tres piezas, separando lógica pura de Unity** para poder testear sin escena:

1. **`MapMemory`** (C# puro, sin `MonoBehaviour`) — `Assets/Scripts/Gameplay/Mapping/MapMemory.cs`
   - Coordenadas: celda `(int x, int z)` = `FloorToInt(pos / 0.5)`; zona `(chunkX, chunkZ, storey)` con
     `FloorToInt` (coordenadas negativas correctas) usando `ChunkSize` y `StoreyM`, **nunca números nuevos**.
   - Búfer circular de muestras (capacidad = X / 0,5 = 120), cada una con tiempo, planta, celda del jugador
     y lista de celdas vistas con su tipo (pared/suelo). **Arrays preasignados**: cero GC en régimen.
   - Consultas: `CellsInZone(zone, now, maxAge)` → celdas con su **edad mínima**, **ordenadas** (regla dura 13);
     `Prune(now)`; `Consume(zone)` (lo dibujado sale del búfer, para P0.2).
   - La línea de visión se resuelve **aquí, sobre la rejilla ya sondeada** (recorrido de celdas), no con
     raycasts: la pared se ve, lo que hay detrás no.

2. **`MapMemorySampler`** (`MonoBehaviour`) — `Assets/Scripts/Gameplay/Mapping/MapMemorySampler.cs`
   - Cada 0,5 s: posición del jugador → celdas del disco de radio `R` → **un `Physics.CheckBox` por celda**
     (medio lado 0,24 m, franja de 0,5 a 1,6 m sobre el suelo del jugador) contra `GeoMask`, ignorando triggers.
   - Con `R` = 5 m son ~314 consultas cada 0,5 s. Se mide con `Stopwatch` y se registra en el log cada 10 s:
     `MAPMEM samples=… cells=… ms_avg=… ms_max=…`.
   - Escribe la muestra en `MapMemory`. Parámetros serializados: `radiusM` (5), `memorySeconds` (60),
     `sampleSeconds` (0,5), `probeBottomM`/`probeTopM`.

3. **`MapMemoryDebugView`** (`MonoBehaviour`, solo depuración) — `Assets/Scripts/Gameplay/Mapping/MapMemoryDebugView.cs`
   - Minimapa en esquina (textura pequeña por `OnGUI`) centrado en el jugador: pared y suelo recordados,
     alfa según edad, límites de chunk. Interruptor serializado, **sin tecla** (no se ocupa input en P0.1).
   - Gizmos en la vista de escena con las mismas celdas.

**Montaje:** un GameObject en la escena de juego añadido por un menú de editor
(`Backrooms/Mapeado/Añadir muestreador de recuerdo`), para no editar YAML a mano. La escena exacta se
confirma al implementar.

### 2.4 Tests EditMode — `Assets/Tests/EditMode/Mapping/MapMemoryTests.cs`

| Test | Qué fija |
|---|---|
| `a_sample_older_than_the_memory_is_forgotten` | Caducidad a los X s |
| `cells_split_by_chunk_and_storey` | Reparto por zona, incluidos bordes de chunk y cambio de planta |
| `negative_coordinates_land_in_the_right_zone` | `FloorToInt` con negativos (−0,1 m → chunk −1) |
| `a_cell_keeps_its_freshest_age` | Edad mínima entre muestras |
| `a_wall_hides_what_is_behind_it` | Línea de visión sobre la rejilla |
| `cells_in_zone_come_out_in_a_stable_order` | Orden determinista (regla 13) |
| `consumed_cells_leave_the_memory` | `Consume(zone)` para P0.2 |
| `the_ring_buffer_does_not_grow_past_capacity` | Capacidad 120 y sin realocar |

### 2.5 Commits (diff estimado ~600 líneas → dos)

1. `feat(mapping): MapMemory, búfer de recuerdo por zona` — `MapMemory.cs` + tests. Sin escena.
2. `feat(mapping): muestreador y vista de depuración del recuerdo` — sampler, debug view, menú de editor.

Tras cada `.cs` nuevo: comprobar el asmdef y el `.csproj` a mano (el compile-check da falso verde con ficheros recién creados).

### 2.6 Hecho cuando

1. Tests EditMode de §2.4 en verde; suite sin rojos nuevos (hoy 12 conocidos).
2. En Play: tras andar, el minimapa muestra el rastro que se apaga y desaparece a los 60 s, y **no atraviesa paredes** (captura).
3. Coste: media ≤ **0,5 ms por muestra** y 0 GC por muestra en régimen, leído del log `MAPMEM`.
4. La planta del log cambia al cambiar de planta.

### 2.7 Riesgos y dudas

- **¿`GeoMask` incluye la capa de los chunks de WG3?** Hay indicios (`OfficeAmbienceDirector` la usa contra
  WG3), no prueba. **Primer paso de la implementación:** un `CheckBox` contra una pared conocida en Play.
- **ADR-128 (mundo ×2) sin commitear ni verificar.** La celda de 0,5 m del recuerdo es de MUNDO y las zonas
  salen de `ChunkSize`/`StoreyM`: si el ×2 cambia esas constantes, P0.1 las sigue sin tocar nada.
- **Pozos (ADR-126):** un `CheckBox` a media altura los ve como suelo. Aceptado en P0.1; anotado para P0.2.
- **Atrezo:** según ADR-129 las anclas llevan macizo invisible; si tiene collider, una mesa cuenta como pared.
  Se decide viéndolo.
- **Planta por `FloorToInt(y / StoreyM)`:** la costura de 664 cm clasifica una planta abajo (STATE, deuda).
  Se usa la Y del **suelo bajo el jugador** y se anota si falla.

### 2.8 Decisiones (cerradas por Joel, 2026-09-13)

1. **Radio de visión: 5 m.**
2. **Franja de sondeo: 0,5–1,6 m** sobre el suelo, como se propuso; lo que pase con las mesas se ve en Play.
3. **Dónde:** lógica y tests en el worktree; sampler y Play en el clon principal.

### 2.9 Resultado (2026-09-13)

**Hecho, con playtest de Joel en `Assets/Scenes/MappingPlaytest.unity`:**
- Salen paredes y **el rastro no atraviesa paredes** (Joel, en Play).
- Coste medido en `Editor.log` (`MAPMEM`, 30 ventanas de 20 muestras): **media 0,145–0,172 ms, máximo 0,254 ms** por
  muestra. Objetivo ≤ 0,5 ms: cumplido.
- Disco de 5 m confirmado: en sala abierta 317 celdas visibles con 1 pared; en pasillo 96–166 con 17–21 paredes.
- Búfer en régimen: 120 muestras (60 s).
- GC: `gc0_collections` por ventana casi siempre 0–1, con picos de 8 y 11. Es un contador de TODO el proceso del
  editor y no aísla al muestreador (que no asigna por muestra); el minimapa de depuración sí asigna por frame
  (texto de `OnGUI`). **No verificado** con Profiler.

**Pendiente de verificar:** caducidad a los 60 s vista en Play, cambio de planta, mesas como pared, suite EditMode
dentro de Unity (sólo corrida en `tools/dev/headless-tests`: 8/8).

**Desvíos del plan:**
- La escena la genera un menú (`Backrooms/Mapeado/Crear escena de playtest`, `MappingPlaytestSceneCreator`) y el
  código llegó al tronco por cherry-pick: una sesión en worktree no puede escribir en el clon principal.
- **`Wg3Materials` no es `[Serializable]`**: `Wg3TestWorld.materials` no se guarda en ninguna escena y
  `WorldGen3Test.unity` sale magenta. La escena de playtest lleva sus materiales en un componente propio
  (`MappingPlaytestMaterials`: `Wg3_Floor`, `_Structure`, `_Ceiling`, `_Trim`) sin tocar WG3.
- `probeMask` no puede inicializarse con `GridChunkBuilder.GeoMask` en la declaración del campo: su constructor
  estático crea objetos de Unity y desde un inicializador de `MonoBehaviour` deja el tipo roto en todo el dominio.
  Se asigna en `Awake`.
- Menú extra de diagnóstico (`Backrooms/Mapeado/Diagnosticar pintura de playtest`) para ver qué material pinta cada renderer.

**Preguntas abiertas antes de P0.2:** ¿5 m y 60 s se sienten bien jugando?

---

## 3. P0.2 — Hoja y dibujar (plan VALIDADO por Joel, 2026-09-13)

### 3.1 Objetivo

Con `N` se abre la libreta (panel en pantalla, sin brazos todavía). Una hoja se asigna a la zona donde se coge.
«Dibujar» pasa al papel **lo que se recuerda de esa zona**: paredes como trazos temblorosos, lo viejo más
tembloroso y con huecos, gastando tinta y **dejando al jugador quieto** mientras dura. Lo ya dibujado no se
vuelve a cobrar. Se juega en `MappingPlaytest.unity`.

**Fuera de alcance:** varias herramientas y papeles, marcas, notas, letra generada, flechas de borde,
«Ubicarme», plano `M`, red, guardado, brazos 1P. Todo eso es P0.3 o posterior.

### 3.2 Lo que ya existe y se reutiliza

| Pieza | Dónde | Uso |
|---|---|---|
| Recuerdo por zona | `MapMemory.CellsInZone`, `Consume` (P0.1) | Fuente de los trazos |
| Pintar trazos una vez en `Texture2D` | `SprayRenderer.Rasterize` (`SetPixels32` + `Apply`), lógica pura en `SprayCanvas` | Mismo patrón para la hoja |
| Panel uGUI por código | `PoiDebugHud` (`ScreenSpaceOverlay` + `CanvasScaler` 1920×1080) | Visor de la libreta |
| Teclas | `Keyboard.current` directo (`WristWatchHandler`, `FreeBuildMode`, `Wg3TestPlayer`); `N` libre | Tecla `N` |
| Algoritmo de trazos | Maqueta «Libreta del cartógrafo» (`buildStrokes`: arista pared↔suelo, fusión de colineales, temblor con semilla, hueco en lo viejo) | Se porta a C# |

### 3.3 Diseño — tres piezas puras y una de escena

1. **`MapSheet`** (C# puro): zona, aristas ya dibujadas (`HashSet<long>`, sólo para consultar, nunca para emitir),
   capas de trazos, tinta. **`MapSheetStrokes`** (C# puro): de `CellsInZone` a trazos.
   - Arista = pared recordada junto a suelo recordado de la zona. Hace falta ver las paredes UNA celda fuera del
     chunk (los muros de borde son del vecino): `CellsInZone` gana un parámetro `marginCells` (sólo paredes).
   - Fusión de aristas colineales contiguas, **orden estable** (fila/columna ordenadas, regla 13).
   - Frescura: edad > `memorySeconds / 3` → trazo tembloroso, discontinuo y con un 28 % de huecos.
   - Temblor con semilla por hoja y capa: el mismo recuerdo da el mismo dibujo.
   - Tinta: coste por metro de arista nueva; sin tinta, el trazo se corta ahí.
2. **`MapSheetRaster`** (C# puro): búfer `Color32[]` de 512×512 con papel, trazos gruesos por sellos y
   discontinuos para lo viejo. Testeable sin Unity (sólo `Color32`/`Mathf`, como el arnés ya admite).
3. **`MapNotebookView`** (`MonoBehaviour`): `N` abre/cierra, libera el cursor, `RawImage` con la hoja,
   pestañas de hoja, «Coger hoja» y «Dibujar», barra de tinta. El dibujo se anima pintando N trazos por frame
   (≈ 3 s como mucho). **Quieto mientras dibuja:** desactiva un `Behaviour` configurable (en la escena de
   playtest, el `Wg3TestPlayer`) sin tocar código de WG3.
4. **Creador de escena**: añade `MapNotebookView` al objeto `MapMemory` enlazado al muestreador y al jugador.

### 3.4 Tests EditMode (también en `tools/dev/headless-tests` si no tocan UnityEngine)

| Test | Qué fija |
|---|---|
| `AWallNextToRememberedFloorBecomesAnEdge` | Arista pared↔suelo, y ninguna sin suelo al lado |
| `BorderWallsOfTheNeighbourChunkCloseTheSheet` | `marginCells` cierra los bordes |
| `CollinearEdgesMergeIntoOneStroke` | Fusión |
| `DrawingTheSameMemoryTwiceCostsNothing` | Dedupe de aristas y tinta |
| `OldMemoryDrawsShakyWithGaps` | Frescura |
| `RunningOutOfInkCutsTheStroke` | Corte por tinta |
| `SameMemorySameSeedSameDrawing` | Determinismo |
| `StrokesComeOutInAStableOrder` | Orden estable |
| `RasterPaintsTheStrokeAndNotBeyondItsWidth` | Raster |

### 3.5 Commits

1. `feat(mapping): trazos de hoja desde el recuerdo` — `MapMemory` (`marginCells`), `MapSheet`, `MapSheetStrokes` + tests.
2. `feat(mapping): raster de la hoja` — `MapSheetRaster` + tests.
3. `feat(mapping): libreta con N en la escena de playtest` — `MapNotebookView`, creador, escena regenerada.

### 3.6 Hecho cuando

1. Tests en verde (headless y dentro de Unity).
2. En Play: tras andar, `N` → «Coger hoja» → «Dibujar» pinta la zona recorrida, no atraviesa paredes, lo viejo
   sale tembloroso; dibujar dos veces seguidas no gasta más tinta; el jugador no se mueve mientras dibuja.
3. Coste del dibujo medido y registrado en el log (`MAPSHEET strokes=… ms_build=… ms_raster=…`).

### 3.7 Preguntas de playtest

¿Dibujar quieto da tensión o pesa? ¿60 s de recuerdo es mucho o poco para llegar a dibujar? ¿Se entiende el
dibujo a ese tamaño? ¿El temblor de lo viejo se lee como «esto no lo tengo claro»?

### 3.8 Decisiones (cerradas por Joel, 2026-09-13)

1. **Dibujar con clic** en el botón «Dibujar».
2. **Hoja de 512×512 píxeles** para 50 m de zona (≈ 10 px por metro).
3. **Dibujo de hasta ~3 s, quieto.**
4. **Pulsar `N` o moverse a mitad cancela**, y lo ya trazado se queda.

### 3.10 Resultado (2026-09-13)

**Hecho, con playtest de Joel en `MappingPlaytest.unity`: «dibuja bien».**
- En el tronco: `baf4cd12` trazos, `9646afc6` raster, `97edf5c9` libreta, `8f9bc6aa` escena.
- Tests dentro de Unity: **21/21** (MapMemory 9, MapSheetStrokes 8, MapSheetRaster 3, Wg3MaterialsSerialization 1);
  los mismos, salvo el último, también en `tools/dev/headless-tests`.
- Log `MAPSHEET` de la prueba:
  - Hoja 1: 10/10 trazos, `ms_build` 3,82 (primera llamada, incluye compilación en caliente), `ms_raster_max` 0,91,
    tinta 1,00 → 0,70.
  - Hoja 2: 10/14 trazos, `ms_build` 0,53, `ms_raster_max` 0,80, **terminó sin completar con tinta 0,17**: casi
    seguro se agotó (el tramo siguiente costaba más de lo que quedaba). Un boli dio para ≈ 42 m de pared, cerca de
    los 50 de diseño (`penCostPerMetre` 0,02).

**Ajuste de Joel tras el playtest:** la tinta se acababa demasiado pronto → `penCostPerMetre` **0,02 → 0,002**
(un boli ≈ 500 m de pared).

**Respuestas de Joel a las preguntas de playtest (§3.7):**
- Dibujar quieto: **se queda**, «mola, te hace pensar mucho».
- 60 s de recuerdo: **perfecto**.
- 5 m de visión: **bien por ahora**; 10 m queda como candidato a revisar con feedback de más jugadores.

**Desvíos del plan:** panel IMGUI en vez de uGUI (sin `EventSystem` en la escena); la libreta abierta deja al
jugador quieto también sin dibujar (el 45 % de velocidad llega con el jugador STP en P0.5); cerrar la libreta
devuelve la mirada con `SendMessage("SetLooking")` al `Wg3TestPlayer`, sin tocar WG3.

---

## 4. P0.3 — Flechas de borde y «Ubicarme» (plan; Joel pidió aplicarlo, 2026-09-13)

Diseño ya decidido en MAPPING-ROADMAP §3 (flechas de borde) y §3b (Ubicarme, D12 y D14: 70 % / 40 %, recuerdo
reciente de 8 s). Sigue en `MappingPlaytest.unity`, local, sin red ni guardado.

### 4.1 Qué se juega

- **Flechas de borde.** Si en el recuerdo pasas andando de la zona de la hoja a otra (o cambias de planta), al
  dibujar la hoja apunta una flecha a lápiz en el borde por donde saliste (o un peldaño si fue de planta), y guarda
  el enlace a la zona vecina. Una por vecina.
- **«Ubicarme».** Botón de la libreta. Compara lo que viste en los últimos 8 s con las paredes DIBUJADAS de cada
  hoja de tu planta: ≥ 70 % → círculo pequeño «estás aquí» en esa hoja, que se abre sola; 40–70 % → círculo grande
  discontinuo; < 40 % → «no reconoces este sitio: en tu mapa está en blanco» y botón «Coger hoja y mapear aquí».
  Con menos de 10 paredes vistas no hay veredicto.

### 4.2 Diseño

1. **`MapMemory`**: cada muestra guarda la celda del JUGADOR (además de su planta); `Crossings` devuelve los pasos
   entre zonas de muestras consecutivas. `Consume` deja de BORRAR: marca, y las consultas normales lo saltan, pero
   el reconocimiento lo sigue viendo (si no, justo después de dibujar no te reconocerías). `CellsInZone` gana
   `maxAgeSeconds`.
2. **`MapSheet`**: `Links` (zona vecina, lado, posición local) y `Marks` («estás aquí» seguro o dudoso), en orden
   de alta.
3. **`MapSheetStrokeBuilder`**: `AddLinks(sheet, memory)` a partir de `Crossings`; `RecentEdges` extrae las aristas
   de los últimos N segundos con la misma regla que el dibujo, sin descontar lo ya dibujado, y **`Recognize`**
   devuelve la fracción que ya está en la hoja.
4. **`MapSheetRaster`**: flecha (asta + punta) o peldaño a lápiz gris; círculo «estás aquí» con la tinta del boli.
5. **`MapNotebookView`**: botón «Ubicarme», resultado y «Coger hoja y mapear aquí»; log `MAPFIX`.

### 4.3 Tests

| Test | Qué fija |
|---|---|
| `EachSampleRemembersWhereThePlayerStood` / `CrossingIntoTheNextChunkIsReported` | Celda del jugador y cruces |
| `ConsumedCellsStillCountForRecognition` | Consumir marca, no borra |
| `CrossingTheBorderAddsOneArrowOnThatSide` | Flecha, lado y posición; una por vecina |
| `ChangingStoreyAddsAStairLink` | Enlace de planta |
| `AWellDrawnSheetIsRecognised` / `ABlankZoneIsNotRecognised` | ≥ 70 % y 0 % |
| `AMovedChunkNoLongerMatches` | Displacement sin código: otra geometría, puntuación baja |
| `TooLittleSeenGivesNoVerdict` | < 10 paredes |
| `ArrowsAndHereMarksArePainted` | Raster |

### 4.4 Commits

1. `feat(mapping): el recuerdo sabe dónde estabas y qué cruzaste` — `MapMemory` + tests.
2. `feat(mapping): flechas de borde y reconocimiento` — `MapSheet`, builder + tests.
3. `feat(mapping): Ubicarme en la libreta` — raster, vista, escena.

### 4.5 Feedback de playtest (Joel, 2026-09-13)

- Trazo a mano: «mejoró».
- **Lo que cuesta es orientarse.** Pedido: al ubicarte, una **cruceta** encima de la hoja que marque dónde estás y
  luego desaparezca. Hecho en la vista: no es tinta ni se guarda en la hoja; roja, parpadea el primer segundo y se
  desvanece a los `crosshairSeconds` = 4 s. El círculo de tinta sigue quedando.
- Candidato abierto (sin pedir): marcar también hacia dónde miras, porque saber el punto no dice el rumbo.
- **La hoja no gira** (ROADMAP D23): ni sola ni a mano. Descartado.
- Propuesta: ver más lejos, 20 m o el cono de la mirada (§4.6).

### 4.6 P0.3b — Ver más lejos (plan, pendiente de Joel)

Hoy: disco de 5 m, una `OverlapBox` por celda de 0,5 m y línea de visión sobre esa rejilla (0,15 ms/muestra).

| Opción | Cómo | Coste estimado | Pega |
|---|---|---|---|
| A. Disco de 20 m | Lo de hoy con `radiusM = 20` | ~5 000 cajas + visión O(r³): **~3–5 ms cada 0,5 s**; búfer ×16 (~6 MB) | Caro, y ves lo de detrás de ti |
| **B. Cono de rayos (recomendada)** | Disco de 5 m de hoy (lo que tienes al lado, también detrás) **+ abanico de ~90 rayos a 1,1 m de altura, 20 m, en el ángulo de la cámara** (~90°). Cada rayo: celdas hasta el impacto = suelo, la del impacto = pared | ~90 `Raycast` ≈ **0,1–0,2 ms**; ~1 500 celdas/muestra | Una mesa baja no corta el rayo; el borde de una pared lejana sale a trozos (1° a 20 m ≈ 0,35 m, cabe en una celda) |

La línea de visión la da el propio rayo. `MapMemory` no cambia (recibe celdas); cambia el muestreador y sube
`MaxCellsPerSample`. Radio y ángulo quedan en el Inspector. Test EditMode: el abanico puro (dado el impacto de cada
rayo, qué celdas salen) sin Unity; el `Raycast` se mide en Play (`MAPMEM`). Un commit (~150 líneas).

### 4.7 P0.4 — Plano `M` de la base (plan, pendiente de Joel)

Diseño en ROADMAP §7b (D13, D15). Local, en `MappingPlaytest.unity`, sin guardado. Fuera: conflictos de displacement
y plano de bolsillo (M8, después del playtest).

**Qué se juega.**
- **La base** es la zona (chunk y planta) donde apareces. `M` dentro de ella abre el plano; fuera: «sin plano
  encima, fuera de la base `M` no abre nada» (D15).
- **Colocación solo por lo demostrado (D13).** La zona de la base se coloca siempre; otra zona se coloca si una hoja
  de una zona YA colocada tiene flecha hacia ella (o al revés). Lo que no conecta sale en el margen, «por colocar».
  La posición en el tablero es la de verdad: el enlace decide SI sale, no dónde.
- **Pasar a limpio** (botón del plano, solo en la base): la hoja abierta en la libreta se convierte en su versión
  limpia: paredes rectas fusionadas, tinta negra fina, sin temblor, con sus flechas. Gasta un folio (contador), tinta
  y 5 s quieto (se cancela como dibujar). Una limpia nueva de la misma zona tapa a la anterior, que se archiva.
- **Tres capas** por zona: **nada** (blanco con trama «sin mapear»), **borrador** (la hoja de la libreta, translúcida
  con su tinta) y **limpia** (opaca, encima). Un tablero por planta, con pestañas. La cruceta de «Ubicarme» y el
  «estás aquí» (30 s) salen también en el plano.

**Diseño.**
1. `MapAtlas` (puro): base, limpias por zona con archivo, `Place(sheets)` en anchura desde la base por `Links` de
   borradores y limpias, orden estable (regla 13). `CleanCopy(sheet)` genera la limpia a partir de las aristas de la
   hoja.
2. `MapSheetRaster`: estilo limpio (sin presión ni borrón) y `DrawAtlas` que compone losetas por capa (128 px por zona,
   desplazable).
3. `MapAtlasView` (IMGUI): `M`, pestañas de planta, arrastrar para mover, «Pasar a limpio»; log `MAPATLAS` con ms de
   composición.
4. Escena: la base se fija en el primer muestreo; campos nuevos con valor por defecto, sin regenerar si no hace falta.

**Tests.** `TheBaseIsAlwaysPlaced`, `ASheetWithoutLinkIsNeverPlaced`, `APlacedLinkPlacesTheNeighbour` (y en cadena),
`ANewCleanCoversAndArchivesTheOld`, `CleanCopyKeepsEdgesAndDropsJitter`, `PlacementOrderIsStable`,
`CleanLayerPaintsOverDraft` (raster).

**Commits (~550 líneas → tres).** `MapAtlas` + tests; raster limpio y tablero + tests; vista `M` + escena.

**Preguntas de playtest.** ¿Volver a la base a ordenar el mapa apetece o molesta? ¿Ver el tablero ayuda a orientarse
más que la hoja suelta?

### 3.9 El libro de supervivencia de STP: modelo base para la libreta (Joel, 2026-09-13)

Joel propone aprovechar el libro de crafteo/construcción que ya existe como modelo para la libreta, y más adelante
para carpeta y archivador. Verificado en el vendor:

| Pieza | Dónde | Qué da |
|---|---|---|
| Wieldable en las manos | `STP/Prefabs/Wieldables/STP_Wieldable_SurvivalBook.prefab` (`WieldableTool`) | Se sostiene en 1P; equipar ≈ 1,35 s, enfundar con la misma tecla |
| Interfaz sobre el libro | `SurvivalBookUI : CharacterUIBehaviour` (`STP/Code/Runtime/UI/Building/SurvivalBook/`), prefab `STP_UI_SurvivalBook` con **dos Canvas en World Space** (menú + contenido: `Building`, `Fire`, `Shelter`, `Storage`, `Workstations`) | Páginas/secciones con selección, Escape cierra (`PushEscapeCallback`) |
| Input propio | `FPSSurvivalBookInput` + contexto `STP/Data/Input/STP_SurvivalBook.asset`; acción `Book` | Toggle equipar/enfundar |
| Ambiente de lectura | Perfil `STP/Data/PostProcessing/STP_SurvivalBook.asset` (profundidad de campo), audio `STP_Book_FlipPage` | Leer se siente como leer |
| Objeto del mundo | `STP/Prefabs/Items/STP_Pickup_SurvivalBook.prefab` | Se encuentra y se recoge |

**Teclas del input del vendor (`FPS_InputActions.inputactions`): `B` = libro, `N` = modo de disparo (`FireMode`),
`M` libre.** Nuestro código no usa ni reasigna el libro.

**Consecuencias para el plan:**
- **P0.2 no cambia:** la escena de playtest usa `Wg3TestPlayer`, no el personaje STP, así que el libro no está
  disponible allí. Se prototipa la LÓGICA (recuerdo → trazos → raster) con el panel en pantalla, que es lo que se
  va a jugar para contestar las preguntas de §3.7. La lógica pura sirve tal cual para la versión diegética.
- **Paso nuevo P0.5 — Libreta diegética** (tras P0.4): calcar el patrón del libro sin editar el vendor (wieldable
  propio `BR_Wieldable_Libreta`, UI World Space sobre las páginas con la textura de `MapSheetRaster`, contexto de
  input propio, profundidad de campo y sonido de página), en una escena con el jugador STP. **Regla 14 / ADR-077
  enm. 2: un Canvas colgado de los brazos 1P warpea (`BR_UIWarp`)**, y el puntero sobre UI warpeada no casa: hay que
  medirlo antes de diseñar clics sobre la hoja.
- **Teclas (a decidir en P0.5):** `M` = mapa como «interfaz» (libre). `N` = libreta choca con `FireMode` del
  vendor. Opciones: (a) reasignar `FireMode` en nuestra copia del mapa de input; (b) la libreta en `B` como pestaña
  del libro («Qué sabes | Notas»), que además encaja con la fila «Libro» de INVENTORY-ROADMAP (armonía del HUD).
