# Packs de arte: Business Office + Grocery Store Props Collection

> Importados el 2026-08-26 y **arreglados el mismo día**. Este documento describe el estado
> DESPUÉS del arreglo; la sección 2 cuenta lo que venía roto de fábrica y cómo se resolvió, porque
> vuelve a pasar con cualquier pack que se compre.
>
> Relacionado: [ASSET-SHOPPING-LIST.md](../ASSET-SHOPPING-LIST.md),
> [systems/authored-rooms.md](../systems/authored-rooms.md), [ROOMS-ROADMAP.md](../ROOMS-ROADMAP.md).

## 0. Estado actual, en una tabla

| | Business Office | Grocery Store Props |
|---|---|---|
| Ruta | `Assets/AK Studio Art/Business Office/` | `Assets/GroceryStorePropsCollection/` |
| Prefabs | **147** | **406** (solo URP) |
| Materiales en uso | 138 | 45 |
| Shader | URP/Lit (132) + URP SpeedTree8 (5) + skybox (1) | URP/Lit (45) |
| Texturas | 306 · 474 MB | 120 · 1.276 MB |
| LODs | no | sí, 4 niveles |
| Colisión en raíz | 147 de 147 | 405 de 406 |
| Peso en disco | 623 MB | 1.370 MB |

552 de los 553 prefabs tienen collider en la raíz. El único sin él es `SM_Bleach1`, que no tiene
malla que medir.

## 1. Lo que se hizo, en orden

Todo con copia de seguridad previa en `J:\Unity\_AssetPackBackups\2026-08-26` (2.290 ficheros,
1.778 MB): texturas, materiales, prefabs HDRP y shaders originales.

| | Qué | Resultado |
|---|---|---|
| 1 | Materiales de Business Office de Built-in a URP/Lit | 132 convertidos, 1 cutout, 8 transparentes |
| 2 | Materiales de Grocery a URP/Lit | 45 de 45 |
| 3 | Ajustes de importación de texturas | 569 de 569 |
| 4 | Rescate de prefabs que solo existían en HDRP | `SM_CandyBar`, `SM_Cardboard3` |
| 5 | Borrado de duplicados HDRP | 402 prefabs + 53 materiales + 95 shaders |
| 6 | Plantas SpeedTree8 a URP | 5 materiales |
| 7 | Colisión | 244 mallas a caja, 21 cajas nuevas, 288 intactos |
| 8 | Purga de assets huérfanos | 670 ficheros, **698 MB** |

Los packs pasan de 2.494 MB a **1.993 MB**, y de 5 caminos de material (Built-in, SpeedTree,
33 shaders de Unreal, URP y HDRP) a **uno solo**.
## 2. Lo que venía roto, y por qué vuelve a pasar

### 2.1 Business Office venía en shader Built-in

Los 138 materiales apuntaban a `m_Shader: {fileID: 46, guid: 0000000000000000f000000000000000}`,
que es el `Standard` de Built-in. Desde ADR-065 este proyecto es URP Forward+ de verdad: un
material así **sale magenta**. Convertidos por script, no por el menú, para controlar el mapeo:
`_MainTex` a `_BaseMap`, `_Color` a `_BaseColor`, y `_Mode` traducido a opaco / recorte /
transparente con sus keywords y su cola de render.

**El MaskMap NO era un problema**, en contra de lo que parecía al principio: URP lee metálico en R
y smoothness en el alfa de `_MetallicGlossMap`, y AO en G de `_OcclusionMap`. El empaquetado estilo
HDRP que trae el pack encaja exactamente. No hubo que reempaquetar nada.

Cinco materiales de plantas usaban `Nature/SpeedTree8`, que tampoco es URP: van a
`Universal Render Pipeline/Nature/SpeedTree8_PBRLit`. Queda uno en Built-in a propósito,
`Hdri City.mat`, que es un `Skybox/Cubemap` y funciona igual en URP.

### 2.2 Grocery venía portado de Unreal

33 shaders generados por la conversión automática de materiales de Unreal (`UnrealCommon.cginc`,
nombres `M_*` y `MI_*`, propiedades `Material_Texture2D_0..4`). **No exponían `_BaseColor`**, y eso
rompía en silencio `TintAuthoredRoom` (ADR-083, A4): la sala se teñía y el prop se quedaba con su
color de fábrica.

Se reasignaron a URP/Lit leyendo el papel de cada textura por su nombre y **empaquetando un
MaskMap** por material: R metálico, G AO, alfa `1 - roughness`. 37 MaskMaps a 2048 sustituyen 584 MB
de mapas sueltos. Las constantes que el port dejó horneadas en el HLSL se recuperaron leyendo
`PixelMaterialInputs.*`: cristal (opacidad 0,10 · roughness 0,05), espejo (metal 1 · roughness
0,10), alambre (base 0,1 · roughness 0,70), y los tres emisivos.

Ocho materiales usan recorte por alfa. URP/Lit no tiene ranura de opacidad aparte, así que el mapa
`Opacity` se compuso dentro de la alfa de su BaseMap, y el material va con `_ALPHATEST_ON` y corte
0,5.

**Trampa que costó tiempo:** al cambiar de shader, Unity **conserva serializadas** las propiedades
que tenía el shader viejo. Los 45 materiales seguían citando las texturas antiguas, así que ninguna
aparecía como huérfana y la purga daba 6 MB en vez de 698 MB. Hay que limpiar los `m_TexEnvs`
obsoletos en el `.mat` antes de medir qué sobra.

### 2.3 Texturas

Los `.meta` traían `maxTextureSize: 8192`, pero las fuentes reales en Grocery son **4096** (160 de
199) y las de Business Office ya venían sanas (1024/2048/512). Ajuste aplicado: en Grocery, 2048
para BaseColor y Normal, **1024 para mapas de datos**; normales marcados como `NormalMap`; mapas de
datos en lineal (sRGB desactivado); mipmaps en streaming. **BaseColor y Normal no se han
redimensionado en disco**: si algún día se quiere más resolución, es cambiar el tope de importación,
no reimportar el pack.

### 2.4 Duplicados HDRP

Verificado que eran duplicados de verdad: `SM_Shelving1` en URP y en HDRP referencian la misma
malla (`guid abc0000...340643`, mismos cuatro fileID de LOD) y solo cambia el material. Se borraron
tras rescatar los dos únicos prefabs que no tenían gemelo en URP.
## 3. Escala: encaja con la rejilla

Tile del mundo = **2,5 m**. Los dos packs vienen en metros reales, así que no hay que reescalar
nada:

- Escritorio: 2,30 x 0,75 x 1,02 m, cabe en un tile con holgura.
- Mesa de reuniones grande y mostrador de recepción: 3,2-3,9 m, **dos tiles**.
- Estantería de oficina `Shelf 1` y `Shelf 2`: 4,98 m de largo, **dos tiles**, y es pieza de pared.
- Estanterías de supermercado: 2,3-2,9 m, un tile, o en fila continua.
- Puertas: 2,3-2,7 m de alto. El marco doble de almacén mide 8,0 m y no cabe en un vano de un tile.

Las medidas de una caja de colisión son exactas. Las que llevan `aprox` salen de `m_Size` en el
`LODGroup`, que es la dimensión mayor de sus bounds, no las tres.

## 4. Colisión

Estado tras el paso 7: **552 de 553 prefabs con collider en la raíz** (`SM_Bleach1` es la
excepción, no tiene malla). Reparto: 385 con caja, 168 con malla, y 21 prefabs recibieron caja
nueva porque solo tenían colliders en los hijos, que para STP es lo mismo que no tener ninguno.

Criterio aplicado: si la pieza mide menos de 0,60 m, `MeshCollider` pasa a `BoxCollider` ajustada a
sus bounds. Un `MeshCollider` por cada tarro de mermelada no lo paga nadie; en una estantería de
2,9 m sí compensa.

Dos cosas que NO cambian:

- `RoomPool.collisionBoxes` sigue sin consumidor (ROOMS-ROADMAP B4). El robapieles atraviesa
  cualquier prop que esté dentro de una sala autorada, tenga collider o no. La colisión de props es
  cosa del cliente.
- Los 21 prefabs con caja nueva **conservan** los colliders que ya tenían en los hijos. Si alguno
  estorba, se quita a mano.

## 5. Para qué sirve cada cosa

### 5.1 `ZONE_OFFICE` de Level 0: la caja de 32 m

ADR-087 paso 1 dejó una sala sellada de 13 tiles por chunk, sin tabiques; el paso 2 se midió y se
revirtió, y su enmienda 1 dice que "la pregunta correcta no se ha respondido". Business Office la
responde por la vía barata: la compartimentación entra como **props dentro de una sala autorada**,
no como tabiques del worldgen. `Reception Wall` (6,41 x 3,55 m) y las estanterías de 4,98 m hacen de
mampara sin tocar una línea del generador.

### 5.2 Salas autoradas: qué hornear con qué

| Sala | Tamaño | Pack | Piezas ancla |
|---|---|---|---|
| Open-plan de cubículos | 4x4 a 6x6 tiles | Business Office | `Desk 1..4`, `Chair 1..3`, estanterías de mampara |
| Sala de reuniones | 2x2 a 3x3 | Business Office | `Meeting Table Large`, `Whiteboard`, `Monitor` |
| Recepción | 2x2 | Business Office | `Reception Counter`, `Reception Wall`, `Sofa 1..4` |
| Break room | 2x2 | ambos | `Kitchen Counter`, `Fridge`, `Coffee Machine`, `SM_OfficeKitchen` |
| Aseos | 1x1 o 2x2 | Business Office | `Toilet Blocks`, `Sinks`, `Wall Urinal`, `Mirror` |
| Archivo o almacén | 2x2 | Grocery | `SM_Locker`, `SM_WarehouseShelfDouble`, `SM_OfficeBox` |
| Tienda o súper | 6x6 o multi-chunk | Grocery | `SM_Shelving1..8`, `SM_Freezer3`, `SM_CashRegister` |
| Sala técnica | 1x1 | Grocery | `EngineeringCommunications`, 46 piezas de tubería y cuadros |

Reglas que ya costaron tiempo: una sala que no quepa en un chunk necesita **dos vanos o más** (con
uno nace incomunicada 6 de 55 veces); dos salas conviven en un chunk solo si `T1+T2 <= 3` tiles; el
tope de footprint es **6x6** dentro de chunk y **16x16** multi-chunk.

### 5.3 Loot y escasez

`GroceryPack` son 82 piezas de comida y bebida, y `Refrigerators` y `StoreFreezers` son el sitio
donde ponerlas. **Pero** `RoomMarker` se hornea y no lo lee nadie: en cuanto un prop tenga contenido
pasa a ser estado del mundo (quién lo saqueó, persistencia por chunk, dos jugadores abriendo a la
vez). Eso es **ADR nuevo**, ROOMS-ROADMAP B1. Hasta entonces, decorado.

### 5.4 Level 4 y señalética

`Doors2` trae 14 señales (EXIT, WC, flechas, numeración) y `EngineeringCommunications1..3` trae 46
piezas de conducto y rejilla: el techo plano en la región del Level 4 y la sensación de que el sitio
tuvo un propósito antes.

## 6. Lo que queda pendiente

1. **Verlo en juego.** La comprobación estática dice que ningún material sigue en Built-in; que se
   vea bien con la luz de este proyecto solo lo dice un playtest.
2. Los tres materiales emisivos van con emisión blanca x2 fija. El original era un Fresnel con
   intensidad 5; si los carteles y lámparas quedan flojos o pasados, es ese único número.
3. Los scripts `Assets/Editor/ClaudePackFix*.cs` y `ClaudePackShots.cs` son **temporales**: borrar
   antes de commitear.
4. Sigue habiendo 1.993 MB de pack en el repositorio. Bajar de ahí ya sí exige redimensionar
   BaseColor y Normal en disco, que es lo que se decidió NO hacer para no tocar el resultado visual.

## 6.bis Props reales en ZONE_OFFICE (2026-08-26)

El catálogo de props que amuebla cada chunk (`LayerVisualConfig.PropEntry`) tenía `prefab` a null
en todas sus entradas, así que el mundo se amueblaba con primitivas de `PlaceholderFactory`. El
comentario en ese campo ya lo decía: "swappable for a real prefab later". Es la puerta que dejó
abierta ADR-036, y rellenarla es dato, no arquitectura. Sin wire, sin backend, sin ADR.

**Alcance de esta tanda:** SOLO el `ZonePropSet` de `zoneKind 12` (ZONE_OFFICE), que vive nada más
que en `Assets/Resources/LayerVisuals/Layer0_Vestibulo.asset`. Los otros tres layers no tienen
`zonePropSets`, y el catálogo general de la capa se queda como estaba.

| placeholder | prefab asignado |
|---|---|
| `desk` | `Office/Desk 1` |
| `chair` | `Office/Chair 1` |
| `filecab` | `Office/Cupboard` |
| `plant` | `Plants/Plant Spathiphyllum` |
| `paper` | `Office/Note` |
| `monitor` | `BR_Prop_OfficeDeskSet` |
| `boxes` | `BR_Prop_BoxStack` |
| `whiteboard` | `BR_Prop_Whiteboard` |
| `partition` | **sin cambio** |

Tres prefabs compuestos nuevos en `Assets/Prefabs/WorldProps/`, y cada uno existe por un motivo
concreto:

- **`BR_Prop_OfficeDeskSet`** — el placeholder `monitor` no es una pantalla suelta: es una mesa CON
  pantalla encima. Asignar `Monitor Pc` a secas habría dejado monitores tirados por el suelo. Lleva
  `Desk 1` + `Monitor Pc` + `Keyboard` + `Office Tray`.
- **`BR_Prop_BoxStack`** — el placeholder `boxes` apila tres cajas, no una.
- **`BR_Prop_Whiteboard`** — la pizarra que trae el pack tiene el pivote en el CENTRO de la tabla
  (`baseY -0,54`): es una pieza de pared. A ras de suelo quedaría medio enterrada, así que el
  envoltorio la sube a 1,55 m.

`partition` se queda en primitiva a propósito. El placeholder es una mampara de 2,2 x 1,35 m y
ninguno de los dos packs trae esa pieza; lo más parecido son estanterías de 2,4-2,9 m, que **tapan
la línea de visión** donde antes se veía por encima. Eso no es un cambio estético, es un cambio
de percepción para el robapieles y para el jugador, y no entra de tapadillo.

**Sin medir todavía:** el coste. La zona va con `propsPerTile: 3`, `maxPropsPerChunk: 96` y
`densityScale: 2.2`, valores afinados cuando un prop era un cubo. Ahora un prop es un prefab con
LODs y colliders, y `BR_Prop_OfficeDeskSet` son cuatro anidados. Se juzga con el juego corriendo,
no aquí.

## 7. Catálogo

Medidas exactas cuando la raíz lleva caja de colisión; `aprox` cuando salen de `m_Size` en el
`LODGroup`. Columna `col`: `B` una caja, `Bn` n cajas, `M` malla, `-` ninguna.

### 7.1 Business Office (147 prefabs)

Raiz: `Assets/AK Studio Art/Business Office/Prefabs/`

**Books** (22)

| prefab | tamano (m) | col |
|---|---|---|
| `Book 1` | 0.17 x 0.03 x 0.25 | B |
| `Book 10` | 0.17 x 0.03 x 0.25 | B |
| `Book 2` | 0.17 x 0.03 x 0.25 | B |
| `Book 3` | 0.17 x 0.03 x 0.25 | B |
| `Book 4` | 0.17 x 0.03 x 0.25 | B |
| `Book 5` | 0.17 x 0.03 x 0.25 | B |
| `Book 6` | 0.17 x 0.03 x 0.25 | B |
| `Book 7` | 0.17 x 0.03 x 0.25 | B |
| `Book 8` | 0.17 x 0.03 x 0.25 | B |
| `Book 9` | 0.17 x 0.03 x 0.25 | B |
| `Book Old 1` | 0.17 x 0.03 x 0.25 | B |
| `Book Old 10` | 0.17 x 0.03 x 0.25 | B |
| `Book Old 2` | 0.17 x 0.03 x 0.25 | B |
| `Book Old 3` | 0.17 x 0.03 x 0.25 | B |
| `Book Old 4` | 0.17 x 0.03 x 0.25 | B |
| `Book Old 5` | 0.17 x 0.03 x 0.25 | B |
| `Book Old 6` | 0.17 x 0.03 x 0.25 | B |
| `Book Old 7` | 0.17 x 0.03 x 0.25 | B |
| `Book Old 8` | 0.17 x 0.03 x 0.25 | B |
| `Book Old 9` | 0.17 x 0.03 x 0.25 | B |
| `Pack Books 1` | 0.40 x 0.25 x 0.17 | B |
| `Pack Books 2` | 0.36 x 0.28 x 0.17 | B |

**Building** (7)

| prefab | tamano (m) | col |
|---|---|---|
| `Building` | 42.32 x 3.52 x 18.00 | B |
| `Elevator` | 2.97 x 3.33 x 2.99 | B |
| `Glass Windows 1` | 5.00 x 3.50 x 0.10 | B |
| `Glass Windows 2` | 9.07 x 3.50 x 0.10 | B |
| `Glass Windows 3` | 9.07 x 3.50 x 0.10 | B |
| `Wall Glass 1` | 9.08 x 1.19 x 0.10 | B3 |
| `Wall Glass 2` | 4.49 x 1.18 x 0.10 | B3 |

**Cardboard Boxes** (4)

| prefab | tamano (m) | col |
|---|---|---|
| `Carton Box 1` | 0.40 x 0.29 x 0.29 | B |
| `Carton Box 2` | 0.43 x 0.21 x 0.27 | B |
| `Carton Box 3` | 0.29 x 0.19 x 0.17 | B |
| `Carton Box 4` | 0.29 x 0.17 x 0.19 | B |

**Decoration Set** (13)

| prefab | tamano (m) | col |
|---|---|---|
| `Couple Kiss` | 0.09 x 0.30 x 0.19 | B |
| `Crane Bird` | 0.11 x 0.35 x 0.24 | B |
| `Delphin` | 0.10 x 0.30 x 0.27 | B |
| `Fowler Vase` | 0.25 x 0.30 x 0.25 | B |
| `Glass` | 0.09 x 0.22 x 0.09 | B |
| `Globe` | 0.31 x 0.35 x 0.26 | B |
| `Hand Sculptures` | 0.16 x 0.28 x 0.16 | B |
| `Horse` | 0.26 x 0.30 x 0.38 | B |
| `Lion` | 0.14 x 0.30 x 0.23 | B |
| `Pegasus` | 0.13 x 0.30 x 0.21 | B |
| `Picture` | 1.18 x 0.83 x 0.01 | B |
| `Ryan Vase` | 0.13 x 0.59 x 0.13 | B |
| `Shelby Box` | 0.27 x 0.07 x 0.13 | B |

**Diffusers** (2)

| prefab | tamano (m) | col |
|---|---|---|
| `Diffuser 1` | 0.81 x 0.03 x 0.81 | B |
| `Diffuser 2` | 0.81 x 0.27 x 0.02 | B |

**Kitchen** (22)

| prefab | tamano (m) | col |
|---|---|---|
| `Bar Stool 1` | 0.47 x 1.02 x 0.54 | B |
| `Bar Stool 2` | 0.52 x 0.88 x 0.40 | B |
| `Ceiling Lamp` | 0.51 x 1.39 x 0.51 | B |
| `Coffee Machine` | 0.71 x 0.09 x 0.47 | B2 |
| `Cup 1` | 0.08 x 0.12 x 0.08 | B |
| `Cup 2` | 0.08 x 0.09 x 0.12 | B |
| `Cup 3` | 0.08 x 0.07 x 0.11 | B |
| `Cup 4` | 0.08 x 0.06 x 0.10 | B |
| `Cup Saucer Spoon` | 0.14 x 0.07 x 0.15 | B |
| `Dinner Counter` | 3.23 x 0.90 x 0.96 | B |
| `Dish` | 0.14 x 0.01 x 0.14 | B |
| `Fridge` | 0.80 x 1.81 x 0.71 | B |
| `Kitchen Counter` | ? | M |
| `Kitchen Cupboard` | ? | M |
| `Kitchen Extractor` | 0.90 x 0.62 x 0.60 | B |
| `Kitchen Wardrobe` | ? | M |
| `Microwave Oven` | 0.55 x 0.34 x 0.45 | B |
| `Small Table` | 0.70 x 0.04 x 0.70 | B2 |
| `Spoon` | 0.02 x 0.01 x 0.11 | B |
| `Table` | 2.20 x 0.77 x 0.91 | B |
| `Tray` | 0.45 x 0.01 x 0.30 | B |
| `Wood Chair` | 0.52 x 0.47 x 0.49 | B2 |

**Office** (46)

| prefab | tamano (m) | col |
|---|---|---|
| `Bench` | 1.70 x 0.45 x 0.35 | B |
| `Case Pc` | 0.19 x 0.46 x 0.54 | B |
| `Chair 1` | 0.67 x 0.36 x 0.10 | B7 |
| `Chair 2` | 0.50 x 0.50 x 0.48 | B2 |
| `Chair 3` | 0.60 x 0.48 x 0.56 | B2 |
| `Cupboard` | 0.89 x 0.79 x 0.46 | B |
| `Desk 1` | 2.30 x 0.75 x 1.02 | B |
| `Desk 2` | 2.30 x 0.75 x 1.02 | B2 |
| `Desk 3` | 2.51 x 0.75 x 0.83 | B2 |
| `Desk 4` | 2.65 x 0.75 x 1.10 | B2 |
| `Frame` | 1.08 x 0.81 x 0.03 | B |
| `Frames` | 1.08 x 0.82 x 0.03 | B5 |
| `Keyboard` | 0.52 x 0.03 x 0.15 | B |
| `Laptop` | 0.30 x 0.01 x 0.19 | B |
| `Laserjet Print` | 0.42 x 0.27 x 0.43 | B |
| `Marker Pen 1` | 0.23 x 0.02 x 0.02 | B |
| `Marker Pen 2` | 0.23 x 0.02 x 0.02 | B |
| `Marker Pen 3` | 0.23 x 0.02 x 0.02 | B |
| `Meeting Table Large` | 5.30 x 0.75 x 1.60 | B |
| `Monitor` | 2.52 x 1.47 x 0.07 | B |
| `Monitor Pc` | 0.53 x 0.44 x 0.16 | B |
| `Mouse` | 0.06 x 0.03 x 0.12 | B |
| `Note` | 0.07 x 0.00 x 0.07 | B |
| `Notebook Closed` | 0.18 x 0.02 x 0.21 | B |
| `Notebook Open` | 0.34 x 0.02 x 0.21 | B |
| `Office Folder` | 0.07 x 0.32 x 0.26 | B |
| `Office Folder Horizontal` | 0.32 x 0.07 x 0.26 | B |
| `Office Phone` | 0.18 x 0.06 x 0.23 | B |
| `Office Tray` | 0.45 x 0.01 x 0.32 | B4 |
| `Office Tray 2` | 0.45 x 0.01 x 0.32 | B4 |
| `Paper Tray` | 0.01 x 0.23 x 0.29 | B2 |
| `Pen` | 0.01 x 0.01 x 0.16 | B |
| `Polygon Chair` | ? | M |
| `Polygon Table` | ? | M |
| `Reception Counter` | 3.93 x 0.27 x 0.70 | B5 |
| `Reception Wall` | 6.41 x 3.55 x 0.07 | B |
| `Shelf 1` | 4.98 x 0.85 x 0.55 | B10 |
| `Shelf 2` | 4.98 x 1.12 x 0.43 | B9 |
| `Shelves` | 1.53 x 0.02 x 0.50 | B9 |
| `Side Table` | 3.22 x 0.93 x 0.67 | B2 |
| `Table` | 1.80 x 0.75 x 0.90 | B |
| `Trash Can` | 0.21 x 0.38 x 0.21 | B |
| `Wall Clock` | 0.50 x 0.50 x 0.03 | B |
| `Water Cooler` | 0.43 x 1.59 x 0.44 | B |
| `Whiteboard` | 1.50 x 1.09 x 0.01 | B2 |
| `Whiteboard Eraser` | 0.11 x 0.02 x 0.07 | B |

**Plants** (7)

| prefab | tamano (m) | col |
|---|---|---|
| `Plant Cactus` | 0.20 x 0.20 x 0.19 | B |
| `Plant Monstera Deliciosa` | 1.01 x 1.58 x 0.92 | B |
| `Plant Pilea Peperomioides` | 0.37 x 0.48 x 0.35 | B |
| `Plant Pothos` | 0.83 x 0.79 x 0.97 | B |
| `Plant Sansevieria` | 0.41 x 0.86 x 0.41 | B |
| `Plant Spathiphyllum` | 0.66 x 0.98 x 0.65 | B |
| `Plant Tropical` | 0.91 x 1.12 x 1.09 | B |

**Restroom** (18)

| prefab | tamano (m) | col |
|---|---|---|
| `Abstract` | 1.30 x 1.63 x 0.08 | B |
| `Bathroom Door` | 0.10 x 1.83 x 0.69 | B |
| `Dispenser For Wipes` | 0.26 x 0.34 x 0.13 | B2 |
| `Door Man` | 0.97 x 2.08 x 0.13 | B |
| `Door Private` | 0.97 x 2.08 x 0.13 | B |
| `Door Women` | 0.97 x 2.08 x 0.13 | B |
| `Hand Dryer` | 0.25 x 0.53 x 0.08 | B2 |
| `Man Icon` | 0.40 x 1.61 x 0.00 | B |
| `Mirror` | 0.01 x 1.00 x 2.63 | B |
| `Restroom Icon` | 0.67 x 0.76 x 0.01 | B |
| `Sinks` | 2.63 x 0.36 x 0.61 | B |
| `Table Separator` | 0.46 x 0.90 x 0.04 | B |
| `Toilet` | 0.39 x 0.42 x 0.68 | B2 |
| `Toilet Blocks` | 1.38 x 2.13 x 0.02 | B8 |
| `Toilet Roll Holder` | 0.17 x 0.05 x 0.06 | B |
| `Trash Can` | 0.27 x 0.41 x 0.27 | B |
| `Wall Urinal` | 0.46 x 0.97 x 0.33 | B |
| `Women Icon` | 0.40 x 1.61 x 0.00 | B |

**Sofa Set** (6)

| prefab | tamano (m) | col |
|---|---|---|
| `Sofa 1` | 1.61 x 0.74 x 0.70 | B |
| `Sofa 2` | 0.75 x 0.74 x 0.70 | B |
| `Sofa 3` | 0.70 x 0.74 x 0.70 | B |
| `Sofa 4` | 0.70 x 0.74 x 0.70 | B |
| `Table 1` | 1.26 x 0.41 x 0.65 | B |
| `Table 2` | 0.65 x 0.41 x 0.65 | B |

### 7.2 Grocery Store Props Collection (406 prefabs)

Raiz: `Assets/GroceryStorePropsCollection/Prefabs/URP/`. La carpeta es plana; la categoria
de aqui sale de la subcarpeta de `StaticMeshes` a la que apunta la malla.

**URP** (406)

| prefab | tamano (m) | col |
|---|---|---|
| `SM_Apple1` | 0.08 x 0.08 x 0.08 | B |
| `SM_Apple2` | 0.06 x 0.07 x 0.06 | B |
| `SM_Apple3` | 0.05 x 0.05 x 0.05 | B |
| `SM_Ashtray` | 0.12 x 0.02 x 0.12 | B |
| `SM_Avocado` | 0.07 x 0.11 x 0.07 | B |
| `SM_Backpack` | 0.28 x 0.50 x 0.39 | B |
| `SM_Bag1` | 0.44 x 0.42 x 0.60 | B |
| `SM_Bag2` | aprox 0.73 | M |
| `SM_Baguette` | aprox 0.63 | M |
| `SM_BaguettePackage` | aprox 0.67 | M |
| `SM_Banana1` | 0.24 x 0.12 x 0.04 | B |
| `SM_Banana2` | 0.21 x 0.10 x 0.04 | B |
| `SM_Barrel` | aprox 1.01 | M |
| `SM_Basket` | 0.38 x 0.48 x 0.56 | B |
| `SM_Beam1` | aprox 4.03 | M |
| `SM_Beam2` | aprox 2.01 | M |
| `SM_Beer` | 0.06 x 0.25 x 0.06 | B |
| `SM_BenchChair` | aprox 2.09 | M |
| `SM_BigTrashCan` | aprox 1.43 | M |
| `SM_Bleach1` | ? | - |
| `SM_Bleach2` | 0.11 x 0.39 x 0.29 | B |
| `SM_Bleach3` | 0.08 x 0.36 x 0.13 | B |
| `SM_BlueTape` | 0.11 x 0.06 x 0.11 | B |
| `SM_Book1` | 0.19 x 0.02 x 0.14 | B |
| `SM_Book2` | 0.24 x 0.05 x 0.16 | B |
| `SM_Book3` | 0.21 x 0.03 x 0.15 | B |
| `SM_Book4` | 0.17 x 0.04 x 0.12 | B |
| `SM_Book5` | 0.19 x 0.03 x 0.13 | B |
| `SM_Bottle1` | 0.07 x 0.27 x 0.08 | B |
| `SM_Bottle2` | 0.06 x 0.20 x 0.06 | B |
| `SM_Bottle3` | 0.06 x 0.25 x 0.08 | B |
| `SM_Bottle4` | 0.04 x 0.19 x 0.05 | B |
| `SM_Bottle5` | 0.07 x 0.23 x 0.07 | B |
| `SM_Bottle6` | 0.07 x 0.21 x 0.07 | B |
| `SM_Box1` | 0.55 x 0.28 x 0.38 | B |
| `SM_Bread1` | 0.11 x 0.31 x 0.12 | B |
| `SM_Bread2` | 0.11 x 0.23 x 0.11 | B |
| `SM_BubbleGum` | 0.07 x 0.01 x 0.02 | B |
| `SM_Bucket` | 0.38 x 0.38 x 0.39 | B |
| `SM_Bushings` | 0.78 x 0.11 x 1.56 | B |
| `SM_Butter` | 0.09 x 0.04 x 0.06 | B |
| `SM_Button1` | 0.10 x 0.18 x 0.09 | B |
| `SM_Button2` | 0.09 x 0.09 x 0.10 | B |
| `SM_CCTV` | 0.21 x 0.29 x 0.39 | B |
| `SM_CCTV_Camera` | 0.16 x 0.29 x 0.34 | B |
| `SM_CCTV_Part1` | 0.07 x 0.07 x 0.16 | B |
| `SM_Cabel1` | aprox 2.0 | M |
| `SM_Cabel2` | aprox 2.0 | M |
| `SM_Cabel3` | aprox 2.0 | M |
| `SM_Cabel4` | aprox 2.0 | M |
| `SM_Cabel5` | aprox 2.0 | M |
| `SM_CabelSupport1` | 0.06 x 0.05 x 0.03 | B |
| `SM_CabelSupport2` | 0.03 x 0.03 x 0.02 | B |
| `SM_Can1` | 0.07 x 0.07 x 0.07 | B |
| `SM_Can1_1` | 0.11 x 0.13 x 0.12 | B |
| `SM_Can2` | 0.07 x 0.10 x 0.07 | B |
| `SM_Can2_1` | 0.10 x 0.16 x 0.10 | B |
| `SM_Can3` | 0.07 x 0.04 x 0.07 | B |
| `SM_Can3_1` | 0.06 x 0.05 x 0.08 | B |
| `SM_Can4` | 0.10 x 0.03 x 0.10 | B |
| `SM_Can4_1` | 0.07 x 0.13 x 0.07 | B |
| `SM_Can5` | 0.06 x 0.13 x 0.06 | B |
| `SM_CanadyBar` | 0.04 x 0.01 x 0.15 | B |
| `SM_CandyBar` | 0.04 x 0.01 x 0.15 | B |
| `SM_Card1` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card10` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card11` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card12` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card13` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card14` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card15` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card16` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card17` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card18` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card19` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card2` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card3` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card4` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card5` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card6` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card7` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card8` | 0.06 x 0.09 x 0.00 | B |
| `SM_Card9` | 0.06 x 0.09 x 0.00 | B |
| `SM_Cardboard1` | 0.49 x 0.35 x 0.37 | B |
| `SM_Cardboard2` | aprox 0.69 | M |
| `SM_Cardboard3` | aprox 0.97 | M |
| `SM_Cardboard4` | aprox 0.97 | M |
| `SM_Cardboard5` | 0.42 x 0.29 x 0.42 | B |
| `SM_Cards1` | 0.16 x 0.12 x 0.00 | B |
| `SM_Cards2` | 0.18 x 0.12 x 0.00 | B |
| `SM_Carrot1` | 0.03 x 0.03 x 0.16 | B |
| `SM_Carrot2` | 0.03 x 0.03 x 0.18 | B |
| `SM_Cart1` | aprox 2.23 | M |
| `SM_Cart2` | aprox 1.76 | M |
| `SM_Cart3` | aprox 2.05 | M |
| `SM_CaseBag` | 0.14 x 0.32 x 0.45 | B |
| `SM_CashRegister` | aprox 0.92 | M |
| `SM_Certificate` | 0.42 x 0.00 x 0.30 | B |
| `SM_Chair1` | aprox 1.13 | M |
| `SM_Chair1_1` | aprox 1.24 | M |
| `SM_Chair2` | aprox 1.06 | M |
| `SM_Chair3` | aprox 1.18 | M |
| `SM_Cheese1` | 0.14 x 0.08 x 0.08 | B |
| `SM_Cheese2` | 0.16 x 0.13 x 0.09 | B |
| `SM_Cheese3` | 0.15 x 0.02 x 0.13 | B |
| `SM_Cheese4` | 0.13 x 0.02 x 0.13 | B |
| `SM_Chips` | 0.25 x 0.17 x 0.04 | B |
| `SM_Chips1` | 0.24 x 0.05 x 0.17 | B |
| `SM_Chips2` | 0.25 x 0.02 x 0.17 | B |
| `SM_ChocolateBar` | 0.08 x 0.18 x 0.01 | B |
| `SM_Cigarette` | 0.08 x 0.01 x 0.01 | B |
| `SM_CigarettePackage1` | 0.06 x 0.11 x 0.03 | B |
| `SM_CigarettePackage2` | 0.06 x 0.02 x 0.09 | B |
| `SM_Clip` | 0.01 x 0.00 x 0.03 | B |
| `SM_Clock` | 0.46 x 0.46 x 0.03 | B |
| `SM_Clock2` | 0.35 x 0.19 x 0.13 | B |
| `SM_CoffeCup` | 0.09 x 0.14 x 0.09 | B |
| `SM_CoffeKettle` | 0.29 x 0.15 x 0.21 | B |
| `SM_Coffee` | 0.12 x 0.27 x 0.09 | B |
| `SM_CoffeeMachine` | 0.31 x 0.57 x 0.47 | B |
| `SM_Container` | aprox 0.98 | M |
| `SM_Cooler` | aprox 1.14 | M |
| `SM_Cooler_1` | aprox 1.82 | M |
| `SM_Corn1` | 0.06 x 0.06 x 0.22 | B |
| `SM_Corn2` | 0.06 x 0.06 x 0.23 | B |
| `SM_CornFlakes` | 0.14 x 0.23 x 0.04 | B |
| `SM_Corndogs` | 0.14 x 0.23 x 0.05 | B |
| `SM_CounterFreezer` | aprox 2.75 | M |
| `SM_Counter_2` | aprox 1.54 | M |
| `SM_Counter_3` | aprox 1.76 | M |
| `SM_Counter_4` | aprox 1.62 | M |
| `SM_Counter_5` | aprox 1.17 | M |
| `SM_Cracker` | 0.12 x 0.17 x 0.06 | B |
| `SM_Cucumber1` | 0.04 x 0.03 x 0.24 | B |
| `SM_Cucumber2` | 0.03 x 0.03 x 0.20 | B |
| `SM_Cup1` | 0.13 x 0.08 x 0.12 | B |
| `SM_Cup2` | 0.13 x 0.11 x 0.09 | B |
| `SM_Cupboard_1` | aprox 2.03 | M |
| `SM_Cupboard_Door1` | 0.03 x 0.36 x 0.34 | B |
| `SM_Cupboard_Door2` | 0.03 x 0.36 x 0.34 | B |
| `SM_Cupboard_Door3` | 0.03 x 0.36 x 0.34 | B |
| `SM_Curd` | 0.15 x 0.03 x 0.05 | B |
| `SM_Deck` | 0.06 x 0.01 x 0.09 | B |
| `SM_DishesRack` | 0.23 x 0.25 x 0.47 | B |
| `SM_Display1` | aprox 1.03 | M |
| `SM_DoorMain` | aprox 2.42 | M |
| `SM_DoorSingleLeft_1` | aprox 2.4 | M |
| `SM_DoorSingleLeft_2` | aprox 2.4 | M |
| `SM_DoorSingleRight_1` | aprox 2.4 | M |
| `SM_DoorSingleRight_2` | aprox 2.4 | M |
| `SM_DoubleDoorFrame` | aprox 3.01 | M |
| `SM_DoubleDoorFrame3` | aprox 4.64 | M |
| `SM_Drawer1` | aprox 0.91 | M |
| `SM_Duct1` | aprox 1.28 | M |
| `SM_Duct2` | aprox 1.29 | M |
| `SM_Duct3` | 0.40 x 0.21 x 0.02 | B |
| `SM_Duct4` | aprox 0.84 | M |
| `SM_Eggs` | 0.24 x 0.06 x 0.10 | B |
| `SM_EnergyDrink` | 0.06 x 0.14 x 0.06 | B |
| `SM_ExitSign1` | 0.40 x 0.22 x 0.07 | B |
| `SM_ExitSign2` | aprox 0.67 | M |
| `SM_Extinguisher1` | aprox 0.85 | M |
| `SM_Extinguisher2` | aprox 0.79 | M |
| `SM_FeltPen` | 0.02 x 0.02 x 0.17 | B |
| `SM_FireAlarm1` | 0.07 x 0.16 x 0.15 | B |
| `SM_FireAlarm2` | 0.29 x 0.10 x 0.29 | B |
| `SM_FireLocker` | aprox 1.14 | M |
| `SM_FireLocker_Door` | aprox 1.02 | M |
| `SM_Fish1` | 0.13 x 0.04 x 0.45 | B |
| `SM_Fish2` | 0.10 x 0.02 x 0.19 | B |
| `SM_Flour` | 0.18 x 0.27 x 0.13 | B |
| `SM_Fork` | 0.19 x 0.02 x 0.02 | B |
| `SM_Forklift` | aprox 5.06 | M |
| `SM_ForkliftWheel1` | aprox 0.75 | M |
| `SM_ForkliftWheel2` | aprox 0.89 | M |
| `SM_Forklift_Wheelless` | aprox 5.09 | M |
| `SM_Freezer3` | aprox 3.03 | M |
| `SM_Freezer4` | aprox 2.84 | M |
| `SM_Freezer_3` | aprox 4.05 | M |
| `SM_Freezer_3_LeftDoor` | aprox 2.09 | M |
| `SM_Freezer_3_RightDoor` | aprox 2.09 | M |
| `SM_Freezer_4_Door` | aprox 1.96 | M |
| `SM_Garlic` | 0.05 x 0.05 x 0.05 | B |
| `SM_GasCart` | aprox 1.52 | M |
| `SM_GasCylinder1` | aprox 1.76 | M |
| `SM_GasCylinder2` | 0.21 x 0.38 x 0.21 | B |
| `SM_GasCylinder3` | aprox 1.2 | M |
| `SM_Hammer` | 0.28 x 0.03 x 0.13 | B |
| `SM_HandDryer` | 0.47 x 0.50 x 0.23 | B |
| `SM_HeaterTubes1` | aprox 3.1 | M |
| `SM_HeaterTubes2` | aprox 3.6 | M |
| `SM_HeaterTubes3` | 0.20 x 0.25 x 0.13 | B |
| `SM_HeaterTubes4` | 0.05 x 0.17 x 0.17 | B |
| `SM_HeaterTubes5` | aprox 1.01 | M |
| `SM_HeaterTubes6` | aprox 0.86 | M |
| `SM_Icecream` | 0.10 x 0.13 x 0.10 | B |
| `SM_Jam1` | 0.13 x 0.09 x 0.13 | B |
| `SM_Jam2` | 0.11 x 0.11 x 0.11 | B |
| `SM_Juice` | 0.06 x 0.17 x 0.06 | B |
| `SM_Ketchup` | 0.04 x 0.18 x 0.10 | B |
| `SM_Kettle` | 0.21 x 0.29 x 0.30 | B |
| `SM_Keyboard` | 0.48 x 0.02 x 0.24 | B |
| `SM_KitchenCabinetDoorLeft` | aprox 0.99 | M |
| `SM_KitchenCabinetDoorRight` | aprox 0.99 | M |
| `SM_KitchenLamp` | 0.45 x 0.59 x 0.45 | B |
| `SM_KitchenWall` | aprox 3.17 | M |
| `SM_KitchenWallShelf` | aprox 1.23 | M |
| `SM_Knife` | 0.23 x 0.01 x 0.03 | B |
| `SM_Knife2` | 0.19 x 0.01 x 0.02 | B |
| `SM_Lamp` | 0.18 x 0.49 x 0.32 | B |
| `SM_Lamp1` | 0.32 x 0.36 x 0.28 | B |
| `SM_Lamp2` | 0.15 x 0.04 x 0.15 | B |
| `SM_Lamp3` | aprox 1.6 | M |
| `SM_Lamp4` | aprox 0.77 | M |
| `SM_Lamp5` | aprox 1.41 | M |
| `SM_Lamp6` | aprox 2.48 | M |
| `SM_Lamp7` | 0.28 x 0.32 x 0.28 | B |
| `SM_Lemon` | 0.06 x 0.07 x 0.09 | B |
| `SM_Lime` | 0.06 x 0.07 x 0.09 | B |
| `SM_LittleSpoon` | 0.13 x 0.01 x 0.03 | B |
| `SM_Locker` | aprox 2.46 | M |
| `SM_Locker2` | aprox 2.45 | M |
| `SM_LockerDoor` | aprox 1.76 | M |
| `SM_ManualForklift` | aprox 1.9 | M |
| `SM_Meat1` | 0.10 x 0.02 x 0.21 | B |
| `SM_Meat2` | 0.10 x 0.02 x 0.10 | B |
| `SM_Meat3` | 0.12 x 0.02 x 0.19 | B |
| `SM_MetalTable` | aprox 2.01 | M |
| `SM_Microwave` | 0.50 x 0.28 x 0.37 | B |
| `SM_Milk` | 0.09 x 0.28 x 0.09 | B |
| `SM_Milk1` | 0.08 x 0.30 x 0.08 | B |
| `SM_Milk2` | 0.07 x 0.25 x 0.07 | B |
| `SM_Mirror` | aprox 1.22 | M |
| `SM_Mop` | aprox 1.55 | M |
| `SM_Mouse` | 0.07 x 0.03 x 0.13 | B |
| `SM_Noodle` | 0.09 x 0.11 x 0.09 | B |
| `SM_OfficeBox` | aprox 1.15 | M |
| `SM_OfficeChair` | aprox 1.35 | M |
| `SM_OfficeTable` | aprox 1.94 | M |
| `SM_Oil` | 0.05 x 0.26 x 0.08 | B |
| `SM_Onion` | 0.05 x 0.06 x 0.05 | B |
| `SM_Orange` | 0.10 x 0.10 x 0.10 | B |
| `SM_PC` | 0.51 x 0.10 x 0.33 | B |
| `SM_Panel1` | 0.34 x 0.36 x 0.16 | B |
| `SM_Panel2` | 0.41 x 0.50 x 0.15 | B |
| `SM_Paper1` | 0.30 x 0.00 x 0.21 | B |
| `SM_Paper2` | 0.14 x 0.00 x 0.10 | B |
| `SM_Paper3` | 0.21 x 0.00 x 0.15 | B |
| `SM_PaperBag` | 0.35 x 0.41 x 0.14 | B |
| `SM_Papers1` | 0.32 x 0.08 x 0.24 | B |
| `SM_Pasta` | 0.14 x 0.23 x 0.10 | B |
| `SM_Patty1` | 0.07 x 0.01 x 0.07 | B |
| `SM_Patty2` | 0.07 x 0.01 x 0.07 | B |
| `SM_PeanutButter` | 0.10 x 0.14 x 0.10 | B |
| `SM_Pear` | 0.07 x 0.11 x 0.07 | B |
| `SM_Pen` | 0.01 x 0.02 x 0.17 | B |
| `SM_PenCup` | 0.10 x 0.15 x 0.10 | B |
| `SM_Pencil1` | 0.01 x 0.01 x 0.19 | B |
| `SM_Pencil2` | 0.01 x 0.01 x 0.19 | B |
| `SM_Pencil3` | 0.01 x 0.01 x 0.19 | B |
| `SM_Pencil4` | 0.01 x 0.01 x 0.19 | B |
| `SM_PendantBench` | aprox 3.57 | M |
| `SM_PendantDisplay2` | aprox 0.99 | M |
| `SM_PendantLamp` | aprox 2.27 | M |
| `SM_Pepper1` | 0.08 x 0.11 x 0.08 | B |
| `SM_Pepper2` | 0.06 x 0.08 x 0.07 | B |
| `SM_Phone` | 0.15 x 0.07 x 0.19 | B |
| `SM_PictureFrame1` | 0.17 x 0.12 x 0.06 | B |
| `SM_PictureFrame2` | 0.48 x 0.34 x 0.01 | B |
| `SM_Pin` | 0.02 x 0.03 x 0.02 | B |
| `SM_Planter1` | aprox 0.79 | M |
| `SM_Planter10` | aprox 1.33 | M |
| `SM_Planter2` | 0.12 x 0.12 x 0.13 | B |
| `SM_Planter3` | 0.20 x 0.19 x 0.20 | B |
| `SM_Planter5` | 0.18 x 0.31 x 0.18 | B |
| `SM_Planter6` | 0.38 x 0.40 x 0.38 | B |
| `SM_Planter7` | 0.28 x 0.31 x 0.28 | B |
| `SM_Planter9` | 0.23 x 0.22 x 0.23 | B |
| `SM_PlasticBasket` | 0.33 x 0.31 x 0.47 | B |
| `SM_PlasticDoor1` | aprox 2.35 | M |
| `SM_Plate1` | 0.19 x 0.02 x 0.19 | B |
| `SM_Plate2` | 0.16 x 0.08 x 0.16 | B |
| `SM_Pot1` | aprox 1.36 | M |
| `SM_Pot10` | aprox 1.39 | M |
| `SM_Pot11` | aprox 0.81 | M |
| `SM_Pot12` | aprox 1.13 | M |
| `SM_Pot13` | aprox 2.62 | M |
| `SM_Pot14` | 0.17 x 0.29 x 0.17 | B |
| `SM_Pot15` | 0.20 x 0.32 x 0.19 | B |
| `SM_Pot2` | aprox 2.1 | M |
| `SM_Pot3` | aprox 1.71 | M |
| `SM_Pot4` | aprox 1.0 | M |
| `SM_Pot5` | aprox 1.79 | M |
| `SM_Pot6` | aprox 1.14 | M |
| `SM_Pot7` | 0.38 x 0.49 x 0.38 | B |
| `SM_Pot8` | 0.28 x 0.40 x 0.28 | B |
| `SM_Pot9` | aprox 0.69 | M |
| `SM_PotatoPackage` | 0.26 x 0.48 x 0.15 | B |
| `SM_Pudding` | 0.08 x 0.09 x 0.08 | B |
| `SM_Pudding2` | 0.08 x 0.09 x 0.08 | B |
| `SM_Pumkin1` | 0.22 x 0.16 x 0.22 | B |
| `SM_Radiator` | aprox 1.06 | M |
| `SM_Refrigerator1` | aprox 2.54 | M |
| `SM_Refrigerator2` | aprox 2.02 | M |
| `SM_Refrigerator3` | aprox 2.12 | M |
| `SM_RestroomCabinDoor` | aprox 2.19 | M |
| `SM_RestroomCabinPart_1` | aprox 3.51 | M |
| `SM_RestroomCabinPart_2` | aprox 3.3 | M |
| `SM_RestroomDresser` | aprox 2.22 | M |
| `SM_RestroomDresserDoorLeft` | aprox 1.21 | M |
| `SM_RestroomDresserDoorRight` | aprox 1.21 | M |
| `SM_RestroomLocker` | aprox 1.22 | M |
| `SM_RestroomShelf` | aprox 1.05 | M |
| `SM_RestroomWallLamp` | aprox 0.85 | M |
| `SM_Rice` | 0.10 x 0.18 x 0.06 | B |
| `SM_Ruler` | 0.03 x 0.00 x 0.20 | B |
| `SM_Salt` | 0.10 x 0.16 x 0.08 | B |
| `SM_Scissors` | 0.20 x 0.00 x 0.06 | B |
| `SM_Shelf` | aprox 1.01 | M |
| `SM_Shelving1` | aprox 2.4 | M |
| `SM_Shelving2` | aprox 2.86 | M |
| `SM_Shelving3` | aprox 1.97 | M |
| `SM_Shelving4` | aprox 2.4 | M |
| `SM_Shelving5` | aprox 2.27 | M |
| `SM_Shelving6` | aprox 2.27 | M |
| `SM_Shelving7` | aprox 1.57 | M |
| `SM_Shelving8` | aprox 2.4 | M |
| `SM_Sign1` | aprox 0.98 | M |
| `SM_Sign2` | aprox 0.76 | M |
| `SM_Sign3` | 0.42 x 0.24 x 0.00 | B |
| `SM_Sign4` | 0.24 x 0.24 x 0.00 | B |
| `SM_Sign5` | 0.19 x 0.35 x 0.00 | B |
| `SM_Sign6` | 0.49 x 0.26 x 0.00 | B |
| `SM_Sign7` | 0.13 x 0.23 x 0.00 | B |
| `SM_Sign8` | 0.26 x 0.26 x 0.00 | B |
| `SM_Sign9` | aprox 0.72 | M |
| `SM_SingleDoorFrame` | aprox 2.47 | M |
| `SM_Skrewdriver` | 0.30 x 0.04 x 0.04 | B |
| `SM_SmallBox1` | 0.08 x 0.14 x 0.17 | B |
| `SM_SmallBox2` | 0.18 x 0.07 x 0.10 | B |
| `SM_SmallBox3` | 0.35 x 0.09 x 0.35 | B |
| `SM_SmallBox4` | 0.07 x 0.06 x 0.14 | B |
| `SM_SmallJar` | 0.08 x 0.09 x 0.08 | B |
| `SM_Soap` | 0.12 x 0.23 x 0.10 | B |
| `SM_Soda` | 0.07 x 0.13 x 0.07 | B |
| `SM_Spice` | 0.07 x 0.12 x 0.01 | B |
| `SM_Spoon` | 0.20 x 0.02 x 0.04 | B |
| `SM_StepLadder` | aprox 2.29 | M |
| `SM_Stool1` | 0.48 x 0.46 x 0.48 | B |
| `SM_Stool2` | aprox 0.86 | M |
| `SM_StoreFreezer1` | aprox 2.74 | M |
| `SM_StoreFreezer2` | aprox 2.75 | M |
| `SM_Sugar` | 0.14 x 0.22 x 0.10 | B |
| `SM_Suitcase1` | 0.59 x 0.39 x 0.21 | B |
| `SM_Suitcase2` | aprox 1.03 | M |
| `SM_Switch` | 0.12 x 0.06 x 0.12 | B |
| `SM_Switch1` | 0.02 x 0.10 x 0.10 | B |
| `SM_Switch2` | 0.02 x 0.10 x 0.10 | B |
| `SM_Table1` | aprox 2.02 | M |
| `SM_Table1_1` | aprox 1.09 | M |
| `SM_Table2` | aprox 1.39 | M |
| `SM_Tea1` | 0.10 x 0.18 x 0.07 | B |
| `SM_Tea2` | 0.10 x 0.17 x 0.07 | B |
| `SM_Tire1` | aprox 0.75 | M |
| `SM_Tire2` | aprox 0.89 | M |
| `SM_Toilet` | aprox 0.98 | M |
| `SM_Tomato1` | 0.09 x 0.08 x 0.08 | B |
| `SM_Tomato2` | 0.06 x 0.05 x 0.06 | B |
| `SM_Toolbox` | aprox 0.83 | M |
| `SM_Towel1` | 0.32 x 0.08 x 0.15 | B |
| `SM_Towel2` | 0.27 x 0.09 x 0.33 | B |
| `SM_Towel3` | 0.27 x 0.09 x 0.47 | B |
| `SM_TrashBin` | aprox 0.74 | M |
| `SM_Trashbag1` | aprox 0.91 | M |
| `SM_Trashbag2` | aprox 1.03 | M |
| `SM_Trashbag3` | aprox 1.04 | M |
| `SM_Tray` | 0.33 x 0.02 x 0.53 | B |
| `SM_Tree1` | aprox 1.58 | M |
| `SM_Tree2` | aprox 1.84 | M |
| `SM_Tree3` | aprox 2.22 | M |
| `SM_Tree4` | aprox 2.65 | M |
| `SM_Tube1` | aprox 1.64 | M |
| `SM_Tube1_1` | aprox 2.02 | M |
| `SM_Tube2` | aprox 1.05 | M |
| `SM_Tube2_1` | aprox 2.0 | M |
| `SM_Tube3` | 0.07 x 0.41 x 0.41 | B |
| `SM_Vegetables` | 0.24 x 0.21 x 0.03 | B |
| `SM_Vent1` | aprox 1.62 | M |
| `SM_Vent2` | 0.40 x 0.03 x 0.40 | B |
| `SM_Vent3` | 0.13 x 0.01 x 0.50 | B |
| `SM_WCSign1` | aprox 0.93 | M |
| `SM_WCSign2` | 0.17 x 0.38 x 0.02 | B |
| `SM_WCSign3` | 0.17 x 0.38 x 0.02 | B |
| `SM_WarehouseGate` | aprox 8.02 | M |
| `SM_WarehouseShelfDouble` | aprox 5.37 | M |
| `SM_WarehouseShelfSingle` | aprox 2.96 | M |
| `SM_Water` | 0.13 x 0.50 x 0.30 | B |
| `SM_WetFloorSign` | aprox 1.03 | M |
| `SM_Wine` | 0.08 x 0.34 x 0.08 | B |
| `SM_WoodenDoor1` | aprox 2.34 | M |
| `SM_WoodenDoorFrame` | aprox 2.65 | M |
| `SM_WoodenPallet` | aprox 1.58 | M |
| `SM_WoodenShelvings1` | aprox 2.61 | M |
| `SM_WoodenShelvings2` | aprox 2.85 | M |
| `SM_Wrench` | 0.38 x 0.02 x 0.09 | B |
| `SM_Yogurt` | 0.09 x 0.11 x 0.09 | B |

