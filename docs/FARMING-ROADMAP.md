# FARMING-ROADMAP.md — cierre mínimo del farmeo y el almacenaje para Alpha 1

> Escrito el 2026-08-22 tras la sesión de diseño con Joel. Pensado para **ejecutarse tal cual**:
> cada ítem dice qué tocar, cómo verificarlo y si necesita ADR antes.
>
> Presupuesto acordado: **máximo una semana, objetivo 2–3 días** para el bloque E (estantería)
> y 1–1,5 días para el bloque A (metal). Scope cerrado a propósito: lo que no está aquí no entra
> en esta pasada (racionamiento, props desmontables, crafteo ADR-064, sync de contenedores).
>
> Dirección de economía vinculante: memo «escasez tipo DayZ» (2026-08-17) — escasez +
> racionamiento futuro + cuatro antídotos al farmeo monopolizador (mundo infinito sin rutas
> memorizables, director de necesidad, peso como tope, almacenaje seguro por tiers).

---

## 0. Dónde estamos (verificado en assets y código, no supuesto)

**Piezas de construcción cerradas** (funcionan; se pulirán después). Costes reales leídos del
`_requirements` de cada prefab en `Assets/Prefabs/Building/`; material `-2823548` = BuildMaterial
`STP_Metal`:

| Pieza | Prefab | Coste hoy | Nota |
|---|---|---|---|
| Marco de pared | `BR_BuildingPiece_GridWall` | 1 Metal | OK |
| Puerta (hoja) | `BR_BuildingPiece_GridDoorLeaf` | 2 Metal | OK |
| Marco de puerta | `BR_BuildingPiece_GridDoorFrame` | **`_requirements: []` — gratis** | Discrepancia: `BackroomsBuildingPieceCreator.cs:145` dice `DoorFrameMetalCost = 5`, pero el prefab pasó de 1 a vacío en `78451db7` y el creator no toca prefabs existentes |
| Claim Marker | `BR_BuildingPiece_ClaimMarker` | 6 Metal | fuera de este roadmap |

**Fuente de Metal**: el carryable vendor `STP_Metal` (`CarriableBuildAction` → BuildMaterial
Metal). El pipeline de carryables está **completo y funcionando** — `ChunkLootManager` canal
carryables (determinista por `(worldSeed,cx,cz,slot)`, respawn 30 min, autoridad backend vía
`set_stp_carryables`, pickup validado por host) — pero **`carryableZoneChance = 0` en los 13
perfiles** de `ZoneLootTable.asset` desde el recorte de escasez del 2026-08-17 (STATE.md, punto
«DEUDA DECLARADA (c)»: «el loop de construcción se queda sin materiales»). Farmear metal =
**encender y rebalancear**, no construir nada nuevo.

**Agujero de seguridad conocido** (auditoría 2026-08-18, punto 1b): `process_stp_carryable_pickup`
(`backend/src/game_loop.rs:5275`) no comprueba distancia; `process_stp_pickup` (`:5360`) sí.

**Estantería**: **no existe como pieza**. Hay un modelo Meshy importado
(`Assets/MeshyImports/metal-shelf-gamemesh_20260817_205927/`, FBX 96 MB + basecolor 87 MB +
metallic), **gitignored** y sin nada que lo referencie. El vendor trae el contenedor listo:
`StorageStation` (`PolymindGames/STP/.../Workstations/StorageStation.cs`, `ISaveableComponent`,
`ItemContainerGenerator` = slots + peso máx + restricciones) y `StorageStationUI` para abrirlo
como cofre. Prefab de referencia: `STP_BuildingPIece_StorageCrate.prefab`. Nada en el vendor
muestra items físicamente; `IItemContainer.SlotChanged` + `ItemDefinition.Pickup` bastan para
hacerlo nosotros.

**Lo que NO sincroniza hoy**: `StpBuildingReplicator` replica pose + estado de construcción,
**no contenido de contenedores**. Un contenedor construido es host-local: el joiner lo abre vacío
y el contenido muere al reconectar. Cerrarlo pide ADR + bump de wire (molde: inventario del
jugador, ADR-045 Fase 3, `InventoryReporter`). **Diferido a propósito** — ver §4.

---

## 1. Decisiones tomadas (2026-08-22, Joel) — no reabrir

| # | Decisión | Por qué |
|---|---|---|
| D1 | **Sin stacks: `_stackSize = 1` en todos los items propios** (`BR_Almond Water` 5→1; `BR_Spray Can` ya es 1). Vendor intacto. | Escasez DayZ (el tope es el peso, no el slot); el racionamiento futuro exige estado por unidad, y un stack no lo puede llevar; visual 1:1 en la balda gratis. La alternativa (restricción solo en la estantería) no existe en STP: sus `ContainerRestriction` son por contenedor, no por slot. |
| D2 | **Estantería por slots fijos, no por capacidad/peso.** | Cada slot es un anchor físico en la balda; el peso no tiene posición. |
| D3 | **Número de slots = huecos reales del modelo, 12–16, mínimo 12.** Se fija en E1 tras medir el FBX (botella ~0,25 m; balda ~1 m ⇒ 3–4 huecos/balda). | 8 con D1 = 8 botellas y balda medio vacía. El anti-acaparamiento no está en el número de slots: está en el coste en metal y en que fuera de chunk estabilizado es robable. |
| D4 | **Coste estantería: 4 Metal.** Pieza libre (`FreeBuildingPiece`), sin snap a retícula. | Mismo orden que el crate vendor; más que una pared, menos que un claim. |
| D5 | **Sync de contenido diferido con ADR**; v1 host-local, avisado. | Cerrar diseño/arte/visual esta semana; el sync es trabajo conocido, no incógnita. |
| D6 | **Abrir como cofre** (UI dual de `StorageStationUI`); coger directamente de la balda queda para v2. | Scope. |

---

## 2. Bloque E — estantería metálica (≈3 días, sin ADR)

Ninguna tarea de este bloque toca protocolo, formato de chunk ni schema de guardado del backend.
El guardado del contenedor va por `ISaveableComponent` del vendor, como cualquier `StorageStation`.

### E0. Stacks a 1 (15 min) — hacer PRIMERO, commit aparte
- `Assets/Resources/Definitions/Item/BR_Almond Water.asset`: `_stackSize: 5` → `1`.
- Verificar: el cofre de mundo (`StpChestSpawner`) y el pickup siguen dando 1 botella por slot;
  test EditMode existente de pools no debe cambiar (no miran stack). Si algún test afirma 5,
  es nuevo rojo: arreglar el test, no el asset.
- Commit: `balance(items): agua de almendras deja de apilar (D1, sin stacks)`.

### E1. Hornear modelo + prefab + definición + coste (½–1 día) — ✅ HECHO 2026-08-22
- `CreateStorageRackIfMissing()` en `Assets/Editor/BackroomsBuildingPieceCreator.cs`, clonado del
  patrón del marco de puerta (bake mesh + textura + material). Malla `.asset` versionada +
  basecolor/metallic a 1024 + material URP en `Assets/Art/Building/StorageRack/`
  (`BR_StorageRack_Mesh.asset`, `_BaseColor.png`, `_Metallic.png`, `_Mat.mat`). Verificado
  `grep -c MeshyImports` = 0 en prefab y `.mat`.
- Prefab `Assets/Prefabs/Building/BR_BuildingPiece_StorageRack.prefab`: `FreeBuildingPiece` +
  `Constructable` (4× Metal) + `MaterialEffect` + collider en la RAÍZ (bounding box completo,
  no un collider por tubo). **Sin `Interactable` todavía a propósito**: lo trae `Workstation`
  por `RequireComponent(IHoverableInteractable)` cuando E2 añada `StorageStation` — añadirlo
  antes habría sido una pieza a medio cablear sin base que lo requiera.
- Definición `Assets/Resources/Definitions/BuildingPiece/BR_Storage Rack.asset`, `def_id=516330017`.
  Categoría `Building`. **COMMITEADA junto al prefab y el arte horneado — no regenerar.**
- **Modelo medido** (`Backrooms/Diagnostics/Measure Storage Rack`, capturas en `Temp/`): rack
  tubular abierto, sin fondo/laterales, **1,4527 × 1,9026 × 0,6049 m** (ancho×alto×fondo), malla
  única sin submeshes por balda. 4 compartimentos abiertos iguales (no 3 — confirmado por
  captura). D3 resuelto: **`StorageRackSlots = 16` (4 por compartimento × 4 compartimentos)**,
  extremo alto del rango 12–16 — el rack lee como estantería de rejilla abierta, aprovecha para
  el efecto visual de E3. Constantes en el creator, aún sin usar (E3 las consume).
- Probe de diagnóstico `Assets/Editor/BackroomsStorageRackProbe.cs` (tracked, como
  `BackroomsDoorFrameProbe.cs`): `Measure Storage Rack` (FBX crudo) y
  `Measure Storage Rack Result` (prefab horneado).
- Trampa conocida del bake: `meshCompression` manda los datos a `m_CompressedMesh` y sin ForceUpdate la malla cargada
  se dibuja con búferes viejos — seguir el código del marco, que ya lo esquiva.
- Menú: `Backrooms/Create Building Pieces` sigue siendo el punto de entrada (crear-si-falta).
  Documentar la fila nueva en `docs/EDITOR-MENUS.md`.
- Verificar: menú ejecutado, prefab/definición/arte existen y están trackeados por git; en
  Play se coloca, admite 4 Metal y queda construida; `grep -c MeshyImports` sobre el prefab y
  el `.mat` da 0.
- Commit: `feat(building): estantería metálica construible, 4 Metal, arte horneado`.

### E2. Contenedor: abrir y guardar (½ día) — ✅ HECHO 2026-08-22 (con matiz)
- `EnsureStorageRackContainer()` en `BackroomsBuildingPieceCreator.cs`, patch idempotente sobre el
  prefab ya commiteado (mismo contrato que `EnsureDoorFrameOpeningMarker`, nunca toca el `def_id`).
  Añade `Interactable` (Title="Storage Rack") + `StorageStation` (`_defaultContainer`: Name="Storage
  Rack", `AllowStacking=false`, `MaxSlotCount=16`, sin restricciones, sin `PredefinedItems`/
  `LootTable` — empieza vacía, es pieza construida por jugador, no cofre del mundo). Explícito por
  código, no delegado a `Workstation.Reset()`/`Interactable.Reset()` (hooks de editor atados al
  flujo "Add Component" del Inspector, no fiables desde un `MenuItem` en batch — se comprobó que el
  layer del root NO cambiaba tras `AddComponent`, así que esos `Reset()` no dispararon en este
  contexto; por eso el código re-asigna `LayerConstants.Building` a mano en vez de confiar en ello).
- **Verificado ESTRUCTURALMENTE en modo Edit, sin Play** (`Backrooms/Diagnostics/Verify Storage Rack
  Container`, en `BackroomsStorageRackProbe.cs`): reflexión sobre `_defaultContainer` +
  `GenerateContainer(...)` — llamar a `StorageStation.GetContainers()` directamente revienta fuera
  de Play (`Workstation.Name` lee `_interactable`, que solo se asigna en `Start()`). Resultado
  **PASS**: `SlotsCount=16`, contenedor arranca vacío, 3 items iguales añadidos UNO A UNO ocupan 3
  slots distintos (no se apilan) y `SlotChanged` dispara exactamente una vez por item — el gancho
  que E3 necesita.
- **HALLAZGO, fuera de esta pieza — NO se ha tocado**: `ItemStack`'s propio constructor
  (`ItemStack.cs:35`) clampa `Count` a `item.StackSize` AL CONSTRUIR, así que
  `AddItemsById(id, N)`/`AddItem(new ItemStack(item, N))` con `N>1` para un item `StackSize=1`
  (todos, desde D1) trunca a 1 SIEMPRE, sin importar cuántos huecos libres tenga el contenedor —
  confirmado en vivo (`GetAllowedCount(dummy,3)` devuelve `(1,"")`). Afecta un sistema YA EN
  PRODUCCIÓN: `InventoryRestorer.cs:236` y `:364` (restauración de inventario al reconectar) y
  posiblemente `StpPickupController.cs:149`. El código ya tiene red de seguridad (`SpillToWorld` del
  sobrante, no hay pérdida de items), pero el jugador vería objetos no-apilables tirados a sus pies
  al reconectar en vez de bien colocados. **Tarea aparte lanzada** (no bloquea esta pieza ni el resto
  del roadmap): arreglar esos call sites con un bucle de altas de 1 en 1 (mismo patrón que
  `VerifyContainer` usa), nunca tocar el vendor.
- **CORRECCIÓN a lo escrito originalmente aquí — la afirmación "guarda... recarga... siguen" era
  FALSA para el estado actual del pipeline**: `StorageStation` implementa `ISaveableComponent`, pero
  `StpBuildingReplicator.cs` (quien de verdad sincroniza/persiste piezas construidas) **solo invoca
  esa interfaz sobre `Constructable` y `BuildingPiece`, nunca sobre `StorageStation`** (verificado
  leyendo el fichero). El contenido del contenedor vive SOLO en memoria mientras el `GameObject`
  exista — sobrevive a abrir/cerrar la UI, NO sobrevive a recargar sesión/reconectar. Esto es
  exactamente D5 (sync de contenedores diferido con ADR), confirmado con más precisión de la que
  tenía el roadmap al escribirlo: no es "falta implementar algo más adelante", es "hoy no hay ningún
  camino de código que lo intente siquiera".
- UI interactiva completa (`StorageStationUI`, tecla de interacción con jugador real) **no
  verificada** — exige Play con input real (sidecar IPC o standalone, ver memoria
  unity-remote-playtest), que es trabajo de E4, no de esta tarea.
- Commit: `feat(building): la estantería abre como cofre (StorageStation, 16 slots, sin stacking)`.

### E3. Visual en vivo por slot (1 día) — la idea de Joel — ✅ HECHO 2026-08-22
- `StorageRackDisplay` en `Assets/Scripts/Gameplay/Building/StorageRackDisplay.cs`
  (`[RequireComponent(typeof(StorageStation))]`). **Cambio de diseño frente a lo planeado**: las 16
  posiciones de balda NO son un `Transform[] _shelfAnchors` autorado a mano — se **calculan** desde
  4 constantes (`TierCount=4`, `SlotsPerTier=4`, `RackWidth`/`RackHeight` medidos en E1) con una
  fórmula (`AnchorLocalPosition`). Motivo: nada que autorar en el prefab, nada que volver a tocar en
  Unity si algún día se re-mide el modelo — solo ajustar constantes. Con el lag de reimport de hoy
  (ver memoria unity-inedit-trigger-automation) cada ida y vuelta al editor cuesta caro; menos
  round-trips por diseño, no por prisa.
- Suscrito a `IItemContainer.SlotChanged` del contenedor de `StorageStation`, resuelto en `Update()`
  (no `Start()`): `GetContainers()` lee `Workstation.Name` → `_interactable` (asignado en
  `Workstation.Start()`), y el orden de `Start()` entre dos componentes del mismo GameObject NO está
  garantizado — resolver en el primer `Update()` es seguro sin más (todo `Start()` de la escena ya
  corrió para cuando corre cualquier `Update()`), mismo patrón que `InventoryReporter.
  ResolveAndSubscribe`.
- **`ProxyRigUtil.NeutralizeToVisualOnly` NO se pudo reusar** (hallazgo antes de escribir nada):
  vive en `Assets/_Migration/STPIntegration/` sin `.asmdef` propio, así que cae en el
  `Assembly-CSharp` implícito; `BackroomsSurvival.asmdef` (donde vive este componente) no lo
  referencia y NO PUEDE (`Assembly-CSharp` compila el último, un asmdef nombrado no puede
  depender de él). Reimplementado igual — mismo orden exacto (dependientes → `Interactable` →
  `Rigidbody` → `Collider`, por el mismo motivo: `[RequireComponent]` rechaza el `Destroy` en
  silencio si el orden es al revés) — como método privado en el propio fichero.
- Al vaciar un slot: `Destroy` del visual + `_visuals[index] = null`. Al resolver: recorre TODOS los
  slots y llama `RefreshSlot`, así que si el contenedor ya tenía contenido (recarga futura tras el
  ADR de sync, D5) reconstruye los visuales sin código aparte.
- **Verificado en Edit mode sin Play**, invocando por reflexión el orden real de lifecycle
  (`Workstation.Start()` primero, luego `StorageRackDisplay.Update()` una vez — simula exactamente
  el orden que Play garantiza) en `Backrooms/Diagnostics/Verify Storage Rack Display`
  (`BackroomsStorageRackProbe.cs`): 3 altas de 1 en 1 → 3 visuales instanciados en las posiciones
  EXACTAS que predice la fórmula (`x=-0.545/-0.182/0.182, y=0.060`, tier 0); las 3 bajas →
  `_visuals[]` queda a `null` en los 3 (el `GameObject` en sí queda destroy-pendiente hasta el
  siguiente frame real, por diseño de `Destroy()` — no hay frame boundary en un script de editor
  síncrono, y no es un bug de este componente). Captura final confirma a ojo: 3 botellas de agua de
  almendras sentadas sobre la balda inferior, en 3 de las 4 columnas.
- Trampa de la propia sonda (dejar anotada): el primer intento de captura salió con bounds gigantes
  (26×14×9 m) porque `Object.FindObjectsByType<Renderer>` busca en TODAS las escenas cargadas, no
  solo la aditiva del probe — con otra sesión con `RoomTesting` abierta a la vez, se colaba su
  geometría entera. Arreglo: `instance.GetComponentsInChildren<Renderer>(true)`, acotado al rack.
- Items sin prefab de `Pickup`: `Debug.LogWarning` una vez, slot se queda visualmente vacío, sin
  excepción (comprobado en el código, no en playtest — no hay ningún item propio sin `Pickup` hoy
  para forzar el caso).
- **No verificado**: standalone real con jugador metiendo/sacando items por la UI (`StorageStationUI`
  con input real) — es trabajo de E4, exige Play con input real (sidecar IPC o build, ver memoria
  unity-remote-playtest).
- Commit: `feat(building): los items guardados se ven colocados en la estantería en tiempo real`.

### E3b. Dos correcciones tras el playtest real de Joel (2026-08-22, misma sesión)
- **Bug real encontrado por Joel jugando** (no por las sondas): al construir la estantería, `Update()`
  intentaba leer el contenedor ANTES de que la pieza estuviera realmente construida (fase
  ghost/`Placed`, no `Constructed` — `BuildingPiece.IsConstructed`), y `Workstation.Name` (que lee
  `_interactable`, asignado en `Workstation.Start()`) reventaba con `NullReferenceException` cada
  frame mientras tanto — **149 282 excepciones acumuladas** en el log antes del fix. No rompía nada
  visible (la pieza igual terminaba funcionando una vez construida) pero inundaba el log. Arreglo:
  `StorageRackDisplay.Update()` ahora espera a `GetComponent<BuildingPiece>().IsConstructed` antes de
  tocar `GetContainers()` — además de arreglar el bug, es la condición correcta: una pieza sin
  construir no debería trackear contenido.
- **Las botellas salían tumbadas** (captura de Joel lo mostró directamente): medido con
  `Backrooms/Diagnostics/Measure Item Pickup` — el pickup de Almond Water mide 0,072×0,072×0,230 m,
  eje largo en Z (reposa de lado, correcto para tirado en el suelo, no para una balda). Corrección
  `Quaternion.Euler(-90,0,0)` puesta como rotación por defecto tanto en el fallback de
  `StorageRackDisplay` como en el sembrado de anchors — reconciliados los 16 anchors ya existentes
  del prefab (solo los que seguían en `identity`, nunca tocados a mano; el mismo patrón de
  reconciliación que `EnsureDoorFrameOpeningMarker`).
- **Pregunta de Joel — sí, los anchors son editables a mano**: cada slot tiene su propio
  `ShelfAnchor_NN` (Transform hijo real del prefab, no un valor calculado invisible). Para ajustar
  uno: abrir `BR_BuildingPiece_StorageRack.prefab` en modo edición de prefab (doble clic, o
  seleccionar y "Open Prefab"), desplegar la jerarquía, seleccionar `ShelfAnchor_00`..`ShelfAnchor_15`
  (00-03 = balda inferior, 12-15 = balda superior) y mover/rotar con las herramientas normales de
  Scene view — el `StorageRackDisplay` del componente ya apunta a ellos, no hace falta re-cablear
  nada. Un anchor movido a mano nunca se toca de nuevo por `Backrooms/Create Building Pieces` (el
  reconcile solo corrige los que sigan en `identity`).
- Commit: `fix(building): la estantería no revienta al construirse y las botellas quedan de pie`.

### E4. Cierre del bloque (½ día)
- Arreglar el coste del marco de puerta: poner `_requirements` del prefab a
  `DoorFrameMetalCost` (5 en el script, o el valor que se decida en A3 — ver abajo) con un
  `Ensure…` idempotente en el creator (como `EnsureDoorFrameOpeningMarker`), no a mano en YAML.
- Playtest standalone completo: colocar estantería, construir con metal, guardar 12+ items, ver
  la balda llena, reconectar como host. Anotar en STATE lo que se vio.
- Test EditMode nuevo: `StorageRackDisplayTests` (crear contenedor en memoria, simular
  `SlotChanged`, comprobar que hay exactamente N visuales para N items y 0 tras vaciar).
- Commit(s) por preocupación; `tools/dev/PolishSweep.sh` en verde.

---

## 3. Bloque A — farmeo de metal «pilas de chatarra» (1–1,5 días, sin ADR)

**Pendiente de dos decisiones de Joel** (A1 zonas, A3 costes) — marcadas. El resto está cerrado.

### A1. Encender carryables solo-metal en zonas destino (½ día)
- `Assets/Resources/Loot/ZoneLootTable.asset` (12 perfiles 1:1 con `zone_kind`):
  `carryableZoneChance > 0` **solo** en las zonas que leen como almacén/mantenimiento
  (propuesta: `ZONE_STORAGE` y `ZONE_MAINTENANCE`; **decisión de Joel**), `metalWeight = 100`,
  `logWeight = stoneWeight = 0`. El resto sigue a 0: las pilas son destino de farmeo, no alfombra
  — y el mundo infinito + displacement impiden memorizar un sitio (antídoto 1).
- Restricción dura de Pieza 3: un perfil puede variar chance/pesos, **nunca el número de slots**.
- Verificar: `ChunkLootRollTests` en verde; en juego, zona STORAGE con pila visible.

### A2. Que lea como pila Backrooms, no como scatter (½ día)
- `ChunkLootRoll.cs:129` `CarryablesPerZone` 16 → **6**; `:138` `ZoneSpreadRadius` 12 m → **2–3 m**
  (normalizado sobre 50 m). Cambiar el count es legal pre-alpha (reindexa `(cx,cz,slot)` de
  `_collectedCarry`; no hay save de loot que romper) — dejar comentario de fecha.
- Opcional cosmético, solo si sobra tiempo: `CarryableDefinition` propia `BR_Scrap Metal` con el
  **mismo** BuildMaterial (`-2823548`) y malla de chatarra; el vendor `STP_Metal` vale para
  arrancar.
- Verificar: 6 paquetes agrupados en ≤3 m sobre suelo real (raycast `RaycastUpOffset=1`, no
  volver a 5: el loot flotante del 2026-07-07 fue eso).

### A3. Costes de acceso (15 min) — **decisión de Joel**
- Propuesta: marco de pared 1 · marco de puerta **2** · puerta 2 ⇒ una entrada completa = 5 Metal
  ≈ una pila de 6 (A2). Si se prefiere el 5 del script, una pila no da para una entrada —
  decidir junto con A2. Se aplica en E4 (`DoorFrameMetalCost`).

### A4. Distancia server-side en el pickup de carryables (½ día, backend)
- `backend/src/game_loop.rs:5275` `process_stp_carryable_pickup`: copiar el check de distancia
  de `process_stp_pickup` (`:5360`, posición del peer desde `net.peers`). Sin wire.
- Test Rust: pickup a distancia > umbral rechazado; a distancia válida aceptado. `cargo test`
  verde (desde 2026-08-22 todo rojo es nuevo).
- Commit: `fix(net): el pickup de carryables valida distancia como el de items`.

### A5. Playtest de loop (½ día)
- Standalone: encontrar pila, cargar metal, construir marco de pared + marco de puerta + puerta +
  estantería. Anotar cuántas pilas hizo falta visitar. Ajustar A2/A3 **una variable por vez**
  (regla «no tocar valores ya validados»).

---

## 4. Lo que queda FUERA y por qué (para que nadie lo cuele)

| Tema | Estado | Qué haría falta |
|---|---|---|
| **Sync de contenido de contenedores construidos** | Diferido (D5). v1 host-local, joiner ve vacío, contenido no sobrevive reconexión | **ADR nuevo** + bump de wire: `BuildingContainerReporter` (molde `InventoryReporter`, ADR-045 F3) keyed por `building_id` + índice de contenedor, consumo en peers, persistencia en el save del backend. Estimación 2–3 días. **Siguiente bloque antes de Alpha 1.** |
| Racionamiento (fracción por botella) | No empezado | Schema de item + sync de stats (territorio ADR-009 a medio construir). D1 lo deja preparado. |
| Props desmontables y regenerables | Pendiente de ADR (STATE 2026-08-17) | Autoridad backend + persistencia por chunk + replicación + guarda «una petición en vuelo». |
| Crafteo con Metal/Circuit/Battery/Cable (ADR-064) | Diferido | Entra en `MaterialPool`, no en pool nueva. |
| Coger un item directamente de la balda | v2 | Interacción por slot sobre el visual de E3. |
| Director de necesidad, peso real como tope | Antídotos 2 y 3 del memo | Sin fecha; no bloquean este roadmap. |

---

## 5. Orden de ejecución y puntos de control

1. **E0** (stacks) → commit.
2. **E1 → E2 → E3** en días 1–2. Punto de control tras E3: vídeo/captura de la balda llena.
3. **E4** + **A3** (coste del marco) día 3. Si se llega aquí en 3 días, el bloque E está cerrado.
4. **A1 → A2 → A4 → A5** días 4–5.
5. `/checkpoint`: STATE.md con lo visto en playtest, INDEX.md ya enlaza este doc.

Señal de parada: si E3 supera 1,5 días, se cierra E con la estantería como cofre sin visual
(E1+E2) y el visual pasa a la siguiente iteración — el farmeo de metal (bloque A) tiene más
retorno para el loop que la balda bonita.
