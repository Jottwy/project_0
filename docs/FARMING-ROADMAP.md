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

### E1. Hornear modelo + prefab + definición + coste (½–1 día)
- Ampliar `Assets/Editor/BackroomsBuildingPieceCreator.cs` con `CreateStorageRackIfMissing()`
  clonando **exactamente** el patrón del marco de puerta (`CreateDoorFrameIfMissing`,
  `BakeDoorFrameMesh`, `BakeDoorFrameTexture`, `ConfigureDoorFrameModelImport`): FBX Meshy →
  malla `.asset` versionada + basecolor/metallic a **1024** + material URP en
  `Assets/Art/Building/StorageRack/` (`BR_StorageRack_Mesh.asset`, `_BaseColor.png`,
  `_Metallic.png`, `_Mat.mat`). Regla: **nada versionado puede apuntar a `MeshyImports/`**
  (gitignored; en otra máquina el objeto sale invisible).
- Prefab `Assets/Prefabs/Building/BR_BuildingPiece_StorageRack.prefab`: `FreeBuildingPiece` +
  `Constructable` (4× Metal vía `ConfigureConstructable`) + `MaterialEffect` + **collider en la
  RAÍZ** (los detectores STP leen componentes del objeto del collider; hijos de malla solo
  MeshFilter+MeshRenderer) + `Interactable`.
- Definición `Assets/Resources/Definitions/BuildingPiece/BR_Storage Rack.asset` (misma categoría
  que las demás; el `def_id` viaja por wire: **crear una vez y commitear, nunca regenerar**).
- **Medir el modelo** y anotar en el script (const con comentario, como `DoorFrame…`): ancho ×
  alto × fondo, nº de baldas, huecos por balda ⇒ `StorageRackSlots` (D3). Trampa conocida del
  bake: `meshCompression` manda los datos a `m_CompressedMesh` y sin ForceUpdate la malla cargada
  se dibuja con búferes viejos — seguir el código del marco, que ya lo esquiva.
- Menú: `Backrooms/Create Building Pieces` sigue siendo el punto de entrada (crear-si-falta).
  Documentar la fila nueva en `docs/EDITOR-MENUS.md`.
- Verificar: menú ejecutado, prefab/definición/arte existen y están trackeados por git; en
  Play se coloca, admite 4 Metal y queda construida; `grep -c MeshyImports` sobre el prefab y
  el `.mat` da 0.
- Commit: `feat(building): estantería metálica construible, 4 Metal, arte horneado`.

### E2. Contenedor: abrir y guardar (½ día)
- Añadir al prefab `StorageStation` con `ItemContainerGenerator` a `StorageRackSlots` slots, peso
  máx generoso (el tope es el slot), sin restricciones en v1. Mirar cómo el crate vendor cablea
  `StorageStation` ↔ `Interactable` (orden de componentes: `Interactable` se requiere por
  `StorageStation`; el `Destroy` inverso ya mordió una vez — ver STATE 11c).
- Comprobar que la UI dual (`StorageStationUI`) se abre con la tecla de interacción y que
  meter/sacar agua y spray funciona; con D1 cada botella ocupa un slot.
- Verificar: mete 3 items, cierra, guarda (`ISaveableComponent`), recarga sesión en host: siguen.
- Commit: `feat(building): la estantería abre como cofre (StorageStation, N slots)`.

### E3. Visual en vivo por slot (1 día) — la idea de Joel
- Componente propio `StorageRackDisplay` (en `Assets/Scripts/Gameplay/Building/`, junto a
  `GridWallBuildingPiece`): `[SerializeField] Transform[] _shelfAnchors` (uno por slot, en orden
  de slot, posicionados en E1 según las medidas del modelo); suscribirse a
  `IItemContainer.SlotChanged` del contenedor de la `StorageStation`.
- En cambio de slot: si hay item, instanciar `ItemDefinition.Pickup` bajo el anchor y
  **neutralizar a solo-visual** (quitar `ItemPickup`, colliders, `Rigidbody`, `Interactable`;
  patrón `ProxyRigUtil.NeutralizeToVisualOnly`, `Assets/_Migration/STPIntegration/RemoteAvatar/
  ProxyRigUtil.cs:47`, respetando el orden de `RequireComponent` de fuera hacia dentro); escalar
  al hueco si el pickup no cabe. Si el slot se vacía, destruir el visual. Al cargar/reconstruir
  (E2 recarga), reconstruir todos los visuales desde el contenido.
- Sin allocs por frame: todo por evento; cachear un `GameObject[]` paralelo a los anchors.
- Verificar (standalone, a ojo): meter agua de almendras por UI ⇒ aparece la botella en su
  balda en el acto; sacarla ⇒ desaparece; recargar ⇒ siguen. Items sin prefab de pickup:
  warning una vez, slot sin visual, no excepción.
- Commit: `feat(building): los items guardados se ven colocados en la estantería`.

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
