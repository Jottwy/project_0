# EDITOR-MENUS.md — Qué hace cada menú del Editor

> Catálogo de las 22 entradas `[MenuItem]` del proyecto. Antes existía solo disperso en
> `STATE.md`, así que la única forma de saber qué hacía un menú era abrir su fichero.
>
> Ninguna de estas entradas es código muerto aunque nadie las referencie: las invoca Unity por
> atributo, y varias también `-executeMethod` desde línea de comandos.

## Aviso que vale por todo el fichero

**`-executeMethod` exige el editor CERRADO.** Con el editor abierto el proceso falla por el lock
de `Temp/UnityLockfile` (que además `tasklist` no siempre delata). Desde el editor abierto, usa
el menú.

## Bake de prefabs (`Backrooms/Build …`)

Reconstruyen assets. Son los que más daño hacen si se ejecutan a destiempo.

| Menú | Fichero |
|---|---|
| `Backrooms/Build Remote Avatar Prefab` | `_Migration/STPIntegration/Editor/RemoteAvatarPrefabBuilder.cs` |
| `Backrooms/Build Corpse Avatar Prefab` | `_Migration/STPIntegration/Editor/CorpseAvatarPrefabBuilder.cs` |
| `Backrooms/Build Phantom Real Form` | `_Migration/STPIntegration/Editor/PhantomRealFormBuilder.cs` |
| `Backrooms/Build Proxy Animator Controller` | `_Migration/STPIntegration/Editor/ProxyAnimatorControllerBuilder.cs` |

**Trampa documentada, y costó dos regresiones en playtest:** el paso que construye el
AnimatorController corre PRIMERO y lo rehace con `DeleteAsset` + `CreateAnimatorControllerAtPath`.
Cualquier paso posterior que pida el controller **por ruta** (`LoadAssetAtPath`) recibe `null`,
porque el AssetDatabase no lo ha reimportado dentro del mismo frame — y un `ImportAsset` síncrono
no lo rescata. **Es intermitente**, así que un bake limpio no prueba nada. Ver `docs/STATE.md`
("NO tocar") para las dos guardas que hoy lo mitigan.

**Verificar un bake por `grep` del nombre de componente sobre el `.prefab` da CERO siempre.**
Unity serializa componentes por GUID de script, no por nombre: hay que buscar el bloque
`MonoBehaviour` por su `m_Script` o por sus campos.

## Creadores de assets (`Backrooms/Create …`)

Patrón **crear-si-falta**: no re-siembran un asset existente, así que re-ejecutarlos es seguro y
no pisa valores ya ajustados a mano.

| Menú | Fichero |
|---|---|
| `Backrooms/Create Building Pieces` | `Editor/BackroomsBuildingPieceCreator.cs` |
| `Backrooms/Create Carryables` | `Editor/BackroomsCarryableCreator.cs` |
| `Backrooms/Create Grid Prefabs` | `Editor/GridPrefabCreator.cs` |
| `Backrooms/Create Layer Visuals` | `Editor/BackroomsLayerVisualsCreator.cs` |
| `Backrooms/Create Zone Loot Table` | `Editor/ZoneLootTableCreator.cs` |
| `Backrooms/Create Chunk Template` | `Editor/BackroomsEditorMenu.cs` |
| `Backrooms/Create JoinSession Prefab` | `Editor/JoinSessionPrefabCreator.cs` |

> `Create JoinSession Prefab` está marcado en la auditoría como **copia divergida** de
> `JoinSessionUI.BuildUI`: produce un prefab que no coincide con lo que el runtime construye.
> Ver `docs/AUDIT-2026-08-03.md`.

## Escenas de prueba

Estas **sí** crean/modifican escenas. No son idempotentes en el mismo sentido.

| Menú | Fichero |
|---|---|
| `Backrooms/Create Grid Render Test Scene` | `Editor/GridTestSceneCreator.cs` |
| `Backrooms/Create Vertical Shaft Test` | `Editor/VerticalShaftTestMenu.cs` |
| `Backrooms/Create Vertical Shaft Grid Test` | `Editor/VerticalShaftTestMenu.cs` |
| `Backrooms/Setup Network Flow` | `Editor/NetworkFlowSetup.cs` |

## Herramientas y diagnóstico

| Menú | Fichero | Nota |
|---|---|---|
| `Backrooms/Chunk Editor` | `Editor/BackroomsChunkEditor.cs` | ventana propia |
| `Backrooms/Validate Structures` | `Editor/StructureValidator.cs` | solo lectura |
| `Backrooms/Diagnostics/Measure Building Meshes` | `Editor/BackroomsMeshProbe.cs` | solo lectura |
| `Backrooms/Generate Textures` | `Editor/TextureGenerator.cs` | escribe texturas |
| `Tools/Backrooms/Fix Runtime Materials` | `Editor/BackroomsRuntimeMaterialInstaller.cs` | **fuera del menú `Backrooms/`**, único que cuelga de `Tools/` |
| `Backrooms/Build/Compile and deploy backend now` | `Editor/BackendBuildPreprocessor.cs` | compila Rust y copia el exe a `Builds/Backend/` |
| `Backrooms/Build/Inyectar ajustes de Voz en la pestaña Audio` | `Editor/VoiceOptionsTabBuilder.cs` | modifica el prefab de opciones de STP |
| `Backrooms/Spray/Crear bote de spray` | `Editor/BackroomsSprayCanCreator.cs` | crear-si-falta: `ItemDefinition` + prefab de wieldable (ADR-068 S3). **COMMITEAR lo generado** — el `_id` va en el wire y en los saves. NUNCA EJECUTADO todavía |
| `Backrooms/Spray/Asignar icono del bote` | `Editor/BackroomsSprayCanCreator.cs` | importa `Assets/Art/Items/BR_SprayCan_Icon.png` como Sprite y lo pone de icono. Sin ese PNG avisa y deja el prestado de la antorcha |
| `Backrooms/Spray/Apagar el fuego del bote` | `Editor/BackroomsSprayCanCreator.cs` | apaga llama/brasas/chispas y TODA `Light` heredadas de la antorcha donante. Idempotente. **OJO**: su filtro por nombre incluye "torch", así que también apagó el nodo `WoodenTorch`, que era la MALLA — de ahí que el bote se empuñara vacío hasta el 08-15 |
| `Backrooms/Spray/Aplicar modelo Meshy al bote` | `Editor/BackroomsSprayModelSwapper.cs` | hornea el modelo de `Assets/MeshyImports/` (gitignored) a copias versionadas en `Assets/Art/Items/SprayCan/`: malla `.asset` canónica (0,19 × 0,066 m, de pie) y texturas a 1024. Cuelga la lata del hueso `Hand.R` y **encadena el menú de abajo**. Reejecutable; borra su propio nodo antes de rehacerlo. El encaje fino se ajusta en `GripNudge`/`EulerNudge` del script, NO moviendo el nodo en el prefab |
| `Backrooms/Spray/Crear el bote del suelo` | `Editor/BackroomsSprayPickupCreator.cs` | clona el pickup de la antorcha a `Assets/Prefabs/Items/BR_Pickup_SprayCan.prefab` (NUESTRO, fuera de territorio vendor), le mete la malla horneada, rederiva la cápsula, reapunta `_item` al id del bote y engancha `_pickup` de la definición. Sin esto una lata tirada es una antorcha para todos los clientes y para el loot. Refresca la `SaveableDatabase`: el clon trae el `_prefabGuid` del donante y una clave duplicada revienta el arranque de un BUILD |
| `Backrooms/Spray/Registrar bote en el jugador` | `Editor/SprayCanWieldableRegistrar.cs` | **EDITA PREFABS DEL VENDOR** (`FPS_Player`/`STP_Player`): cuelga el wieldable junto a la antorcha. Sin esto el bote no se puede empuñar. Un reimport del `.unitypackage` lo borra en silencio — reejecutar |
| `Backrooms/Spray/Dar un bote al jugador` | `Editor/SprayCanGiver.cs` | **solo en Play**; mete un bote en el inventario vía `AddItemsById`. Provisional hasta que S4 lo ponga en el loot |
| `Backrooms/Spray/Pintar prueba delante del jugador` | `Editor/SprayTestPainter.cs` | **solo en Play**; fabrica una pintada local (ADR-068 S2), no toca backend ni save |
| `Backrooms/Spray/Borrar pintadas de prueba` | `Editor/SprayTestPainter.cs` | borra las pintadas EN PANTALLA; las reales vuelven al recargar el chunk |

## Deuda anotada

- `Tools/Backrooms/Fix Runtime Materials` es el único que no cuelga de la raíz `Backrooms/`.
  Unificarlo es trivial pero cambia dónde lo busca la gente; no se hizo por eso.
- `EnsureFolder` ya NO está duplicado: los cinco creadores (`BackroomsBuildingPieceCreator`,
  `BackroomsCarryableCreator`, `GridPrefabCreator`, `ZoneLootTableCreator`,
  `BackroomsLayerVisualsCreator`) llaman a `Editor/BackroomsEditorFolders.cs`. La advertencia sigue
  en pie: **no** lo "mejores" a recursivo — `BackroomsCarryableCreator` asegura el padre a mano antes
  que el hijo, y una versión recursiva sí cambiaría comportamiento. `EnsureFolders()` (plural) sigue
  siendo de cada creador: su lista de carpetas es parte de su contrato, y el de
  `BackroomsRuntimeMaterialInstaller` / `TextureGenerator` ni siquiera parte la ruta.
