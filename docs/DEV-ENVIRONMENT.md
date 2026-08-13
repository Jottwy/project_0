# docs/DEV-ENVIRONMENT.md — entorno de esta máquina y trampas de herramienta

Rutas exactas, comandos que funcionan, y los fallos que **no dan error** y por eso se
re-descubren a base de resultados absurdos.

NO documenta decisiones ni estado: para eso, `STATE.md` y `DECISIONS.md`.

## Unity

`C:\UnityInstall\6000.0.71f1\Editor\Unity.exe`

El Hub solo tiene registrado 6000.0.70f1 (`%APPDATA%\UnityHub\editors-v2.json`), que **no**
es la del proyecto (`ProjectSettings/ProjectVersion.txt`). Derivar la ruta del Hub da la
versión equivocada.

**El editor tiene que estar CERRADO** para batchmode. `tasklist` puede no mostrar
`Unity.exe` con el lock vivo; la prueba real es abrir `Temp\UnityLockfile` en exclusiva
(es lo que hace `RunMultiInstancePlaytest.ps1`).

### Tests EditMode

```
Unity.exe -runTests -batchmode -nographics -projectPath <proj> -testPlatform EditMode ^
  -testResults <xml> -logFile <log>
```

El XML tarda en aparecer después de que el proceso parezca terminado — espera a que
`Unity.exe` salga de verdad antes de leerlo. `Test run completed. Exiting with code 2`
en el log significa que hubo fallos, aunque el wrapper reporte 0.

### Build de desarrollo

```
Unity.exe -batchmode -quit -nographics -projectPath <proj> ^
  -executeMethod BackroomsSurvival.EditorTools.DevPlayerBuild.BuildWindows64 ^
  -buildOutput <exe> -logFile <log>
```

`-buildWindows64Player` (el que usa el arnés) produce **siempre RELEASE**. Hace falta el
build de desarrollo para que `PoiDebugHud` se encienda — es el único sitio donde se leen
en pantalla el `layer` y el `zone_kind` del chunk donde está el jugador.

Ojo: `PoiDebugHud` **sí** está compilado en un build de release. Su `#if` solo cambia el
valor por defecto de `enablePoiDebugHud`; la clase existe, apagada.

## Playtest

```
.\tools\dev\RunMultiInstancePlaytest.ps1 -InstanceCount 1 -WorldSeed 42 -ResetSaves -Force
```

Desde un prompt de PowerShell **limpio**: si en esa misma sesión se pusieron a mano
`SAVE_PATH`, `IPC_PORT` o `NET_PORT`, se filtran al proceso hijo, el auto-host no dispara
y el juego se queda en el menú (se ve como spam infinito de `No instance of
PolymindGames.GameMode found in the scene`, que en el menú es normal). Si pasa: entrar por
**Host** a mano, o relanzar desde un shell nuevo.

`pwsh` no existe aquí: es PowerShell 5.1, y el script se invoca directo con `.\`.

**El check de frescura del arnés da FALSO NEGATIVO en builds incrementales.** Compara el
timestamp de `BackroomsSurvivalMMO.exe`, que es solo el stub lanzador y no cambia entre
builds. La prueba real de que tu código está dentro es
`Builds/BackroomsSurvivalMMO_Data/Managed/*.dll`.

## Backend

`cargo` desde la RAÍZ del repo con `--manifest-path backend/Cargo.toml`. Si el cwd se
queda en `backend/`, los hooks del repo se resuelven con ruta relativa y fallan.

`cargo test` **sí** relinka `target/release/backrooms_server.exe`. Despliegue:
`.\tools\dev\CopyReleaseBackendToBuild.ps1`.

## Trampas de test que no dan error

**`CompileCheckClient.sh` da FALSO VERDE cuando el `.csproj` está viejo.** Todo su conjunto de
referencias sale del `.csproj`, y ése lo **regenera Unity al refrescar**. Si el `.asmdef` cambió,
o el fichero es nuevo, y Unity no ha refrescado desde entonces, el arnés compila contra las
referencias de ANTES. Ocurrió el 2026-08-13: `SprayLootTests.cs` usaba `PolymindGames` sin que
`EditModeTests.asmdef` lo referenciara y el script dijo `errors: 0` tres veces seguidas; en
cuanto Unity recompiló y regeneró el csproj, el MISMO script reprodujo los dos `CS0246`.

**Regla:** tras tocar un `.asmdef` —o al añadir a un fichero el primer `using` de otro
assembly— este script no dice la verdad hasta que Unity refresque. Mirar el `.asmdef` cuesta
menos. Ojo además con los nombres: el assembly del vendor se llama `PolymindGames` aunque su
fichero sea `PolymindFPSCore.Runtime.asmdef`.

**`Collider.bounds` en EditMode.** Lo devuelve PhysX, que solo se entera de un cambio de
transform cuando algo llama a `Physics.SyncTransforms()` — automático antes de cada
FixedUpdate en Play Mode, y en EditMode no lo llama nadie. Sin eso, todo `BoxCollider`
recién creado por `CreatePrimitive` reporta el cubo unitario en el origen, `[-0.5, 0.5]`.
Rompió dos tests con números que no existían en la geometría y, peor, hacía pasar en falso
otros dos (`min.y == -0.5 <= 0`, `size.x == 1.0 > 0.9`). `Renderer.bounds` no tiene el
problema: ese sí sale del transform.

**`Mathf.RoundToInt` no es `f32::round` de Rust.** El de C# delega en `Math.Round`, cuyo
default es banker's rounding (mitad al PAR); el de Rust redondea mitad LEJOS de cero. Con
un valor que cae exactamente en `.5` dan respuestas OPUESTAS. Para espejar lógica del
backend en un test de cliente: `(int)Mathf.Floor(x + 0.5f)`.

**`PlaceholderFactory` cae a `Cabinet()` para un tipo desconocido.** Una errata en un asset
("filecabinet" por "filecab") amuebla la zona entera de armarios grises sin un solo error.
Discrimínalo por el NOMBRE de la raíz (`Prop_<tipo>`), no contando renderers: hay tipos
legítimos de una sola pieza, igual que `cabinet`.

**Los arrays indexados por `zone_kind` usan `Mathf.Clamp`.** Fuera de rango no lanzan:
sirven la ÚLTIMA entrada. Y ampliar el inicializador de C# no basta — hay assets con el
array **horneado en YAML** (`Resources/LayerVisuals/Layer0_Vestibulo.asset`,
`Resources/Loot/ZoneLootTable.asset`), y el inicializador nunca vuelve a aplicarse sobre
un asset ya serializado.

## Método que ha funcionado en este repo

Diagnóstico y medición antes de tocar. Instrumentación temporal que se **borra** tras
leerla, verificando `git diff` vacío. Commits pequeños con `revisor-diffs` antes de cada
uno — en la serie ZONE_OFFICE cazó tres bloqueantes reales, incluido uno que habría puesto
un test en rojo en su primera ejecución. `DECISIONS.md` solo con Edit anclado y
`@(Get-Content).Count` verificado antes y después (`Measure-Object -Line` no cuenta líneas
vacías y falsea la comprobación).
