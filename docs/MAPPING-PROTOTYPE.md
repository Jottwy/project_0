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
