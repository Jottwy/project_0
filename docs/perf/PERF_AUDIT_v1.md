# PERF_AUDIT_v1 — Auditoría de rendimiento y viabilidad de técnicas (2026-09-10)

> **Modo: solo análisis.** Ni una línea de código de producción, escena, asset, `ProjectSettings` ni wire
> se ha tocado. La única pieza nueva es la sonda `Assets/_PerfProbe/PerfProbe.cs` (solo compila con
> `DEVELOPMENT_BUILD`, se activa con `PERFPROBE=1`, borrable) y este directorio `docs/perf/`.
>
> **Regla de lectura:** cada cifra de este documento es MEDIDA (con el run que la produjo) o lleva la
> etiqueta **ESTIMADO** con el método, o **NO MEDIDO**. Las tablas crudas viven en
> `docs/perf/raw/summary_tables.md` (generadas por script desde los CSV de `docs/perf/raw/probe/`).

## 0. Inventario (leído del repositorio, sin medir)

### 0.1 Pipeline de render

| Elemento | Valor real | Fuente |
|---|---|---|
| Editor / RP | Unity 6000.0.71f1, URP 17.0.4, **Render Graph ACTIVO** (`m_EnableRenderCompatibilityMode: 0`) | `ProjectVersion.txt`, `UniversalRenderPipelineGlobalSettings.asset:288` |
| RP asset activo | `PC_RPAsset` para TODOS los tiers (los 5 niveles de calidad tienen `customRenderPipeline: {fileID: 0}`) | `GraphicsSettings.asset:39`, `QualitySettings.asset` |
| Renderer | `PC_Renderer`, **Forward+** (`m_RenderingMode: 2`), Native RenderPass ON, Depth priming OFF | `PC_Renderer.asset` |
| Prepasses | `m_RequireDepthTexture: 1` **y** `m_RequireOpaqueTexture: 1` (downsampling 2×) | `PC_RPAsset.asset` |
| SSAO | Renderer Feature activa: `Source: 0` (**DepthNormals** → prepass de normales de toda la geometría), `Downsample: 0` (resolución completa), 2 samples, blur quality 0 | `PC_Renderer.asset` |
| Antialiasing | MSAA **2×** en el RP asset + **SMAA High** en la cámara del jugador (`m_Antialiasing: 2`, quality 2). TAA solo en el preset Ultra del menú (ADR-134) | `FPS_PlayerCamera.prefab:120` |
| STP / upscaling | `m_UpscalingFilter: 0` (Auto). El menú ofrece Off / FSR 1.0 / STP; Very Low–Medium usan FSR 1.0 a 0,60–0,85 | `GraphicsQualityPresets.cs` |
| Motion vectors | Nadie los pide: MotionBlur existe en `DefaultVolumeProfile` con `intensity 0`; sin TAA por defecto. La cámara tiene `m_AllowDynamicResolution: 0` | `DefaultVolumeProfile.asset` |
| Sombras | Luz principal 2048, 4 cascadas, distancia 50 m, soft **High** (`m_SoftShadowQuality: 3`) — pero la direccional de `STP_Showcase` tiene `m_Shadows.m_Type: 0` (NINGUNA), así que las cascadas no se usan. Luces adicionales: atlas **4096**, tier alto 1024, soft. En WG3 proyecta sombra **la primera encendida de cada tramo grande**, puntual con `LightShadows.Soft` (6 caras por luz) | `PC_RPAsset.asset`, `STP_Showcase.unity`, `Wg3SceneAssembler.cs:592` |
| Luces por objeto | Forward+: `m_AdditionalLightsPerObjectLimit: 4` no aplica; tope de clúster 512 luces visibles (`[wg3-diag] censo de luces`) | `Wg3ChunkStreamer.cs:330` |
| GPU Resident Drawer | **OFF** (`m_GPUResidentDrawerMode: 0`), sin occlusion culling GPU | `PC_RPAsset.asset` |
| SRP Batcher | ON; dynamic batching OFF; LOD cross-fade ON | `PC_RPAsset.asset` |
| Post | Volumen runtime `BackroomsPostProcess` (vignette, grano, bloom, color, tonemapping, prioridad 100) + `STP_DemoProfile_URP` del `STP_WorldManager` (bloom, fog de Oasis, chromatic aberration, DoF OFF) | `BackroomsPostProcess.cs:80-118` |
| API gráfica | Windows sin lista explícita → automático = **Direct3D 11**; `gfx-threading-mode=6` (split jobs) NO soportado en D3D11 y cae a `ClientWorkerJobs` (línea 8 de cualquier `Player.log`) | `ProjectSettings.asset`, `boot.config` |
| Escena de juego | `STP_Showcase.unity` está casi vacía (11 GameObjects, 2 luces, 1 MeshRenderer): TODO el mundo se instancia en runtime desde `Wg3ChunkStreamer` (radio 1 = 3×3 chunks de 50 m) | `STP_Showcase.unity`, `Wg3ChunkStreamer.cs:27` |

### 0.2 Scripts

| Métrica | Valor | Fuente |
|---|---|---|
| Ficheros con `Update()` / `LateUpdate()` / `FixedUpdate()` — proyecto (`Assets/Scripts`) | 72 / 16 / 0 | grep |
| Ídem `Assets/_Migration/STPIntegration` (proxies remotos) | 29 ficheros con Update/LateUpdate: **25 hooks distintos por avatar remoto** (`ProxyLocomotionFeeder`, `ProxyFootstepHook`, …), cada uno con su `Update` | `RemotePlayerManager.cs`, `RemoteAvatar/*.cs` |
| Ídem vendor (`Assets/PolymindGames`) | 31 / 15 / 14 | grep |
| `FPS_Player.prefab` | 62 MonoBehaviours | grep |
| Animator de proxies | `AnimatorCullingMode.AlwaysAnimate` + `updateWhenOffscreen = true` en TODOS los SkinnedMeshRenderer de cada remoto | `RemotePlayerManager.cs:611-615` |
| Burst / Jobs | **0 usos** de `BurstCompile`/`IJob*` en `Assets/` (Burst llega como dependencia de `com.unity.collections` 2.6.2; `com.unity.burst` no está en el manifest). Sin `NativeArray` en código propio. Toda la generación de mallas de WG3 (`Wg3MeshBuilder.SetVertices/SetTriangles`) y el ensamblado (`Wg3SceneAssembler`) corren en el hilo principal en el callback `OnWg3Chunk` | `Packages/manifest.json`, `Wg3ChunkStreamer.cs:453` |
| Update manager | No existe. `MaterialPropertyBlock` solo en `LampFlicker` (BackroomsLighting.cs) y en el generador WG2 (apagado) | grep |
| Física | `Fixed Timestep 0,02` (50 Hz), solver 6/1, `AutoSyncTransforms` OFF. Colisión de WG3: `BoxCollider` por volumen y `MeshCollider` convexo por prisma (`AddColliders`, `Wg3SceneAssembler.cs:1420-1447`) | `DynamicsManager.asset` |

### 0.3 Memoria y assets

| Métrica | Valor | Fuente |
|---|---|---|
| Texturas importadas | **1 055** (566 vendor STP, 325 AK Studio oficina, 120 Grocery). `textureCompression` normal en 1 036 (= BC1/BC3 en Windows, **ni una en BC7**), 13 HQ, 6 sin comprimir. `crunched` 0. Ninguna con override de formato para Standalone salvo 2 | script sobre `.meta` |
| Tamaños | maxTextureSize 2048 en 634, **4096 en 44** (31 albedo + 13 normales), 1024 en 158. 219 mapas de normales (BC3 = 1 B/px) | ídem |
| Mipmap streaming | `streamingMipmapsActive: 0` en los 5 tiers de calidad; 450 texturas llevan `streamingMipmaps: 1` en el importador, que sin el sistema activo no hace nada | `QualitySettings.asset` |
| Huella si TODO reside (ESTIMADO: min(fuente, maxSize)² × bpp × 1,33 mips) | **2 271 MB** (STP 1 299, oficina 573, Grocery 359) | script |
| Audio | 290 clips, **todos `DecompressOnLoad`** (259 Vorbis, 31 ADPCM), 221 con `preloadAudioData`. Las tres fuentes >25 MB (bosque día/noche, ambiente Level 0 de 10-30 min) no se referencian desde el build | script sobre `.meta` |
| Addressables | **No instalado**. `Resources.Load` en 24 sitios del proyecto (prefabs de atrezo `Wg3Props`, materiales, salas, loot, shader del portal) + 14 del vendor. `Assets/Resources` 6,5 MB de fuente, pero **`resources.assets.resS` del build pesa 1 556 MB** de 2,2 GB de `_Data`: lo que cuelga de Resources (atrezo → packs de oficina y supermercado enteros) va al build y se resuelve por índice al arrancar | `du` sobre `Builds/Build_steam` |
| Build | 2,2 GB `_Data` (resources 1,56 GB + sharedassets0 220 MB + sharedassets1 105 MB); zip 1,25 GB | ídem |

### 0.4 Red

| Métrica | Valor | Fuente |
|---|---|---|
| Wire | **62** en las dos puntas (ADR-137 D1 aplicado el 10-09: MsgPack posicional, `to_vec` en vez de `to_vec_named`) | `WireSchema.cs:25`, `ipc/server.rs:38` |
| Envío de pose local | 30 Hz cliente→backend por IPC (`PlayerPoseTransmitter.SendHz = 30`) | `PlayerPoseTransmitter.cs:41` |
| Reparto host→peers | tick 60 Hz; poses a **20 Hz** desde `84e6bb15` (`NET_BROADCAST_EVERY` 6→3); `world_state` 10 Hz; rosters STP 10 Hz cuando cambia el hash (3 rondas) | `game_loop.rs:47-69` |
| Tamaño de pose | `PlayerUpdate` 246 B con nombres → **76 B** posicional (medido en `protocol.rs`, `player_update_named_vs_positional_size`) | ADR-137 |
| Tráfico medido | Antes (09-09): 253,8 KB/s de subida del host saturando el default de Valve (256 KB/s), 483 KB en cola = **1,9 s de retraso**, ping 24 ms. Después (10-09, misma partida): **1,2 KB/s y cola cero** (commit `84e6bb15`). Hoy en la partida real de Joel por SDR (`Player.log`, 13:xx): ping 11-26 ms, calidad 100 %, `Bytes buffered: 0` | ADR-137, `84e6bb15`, `Player.log` |
| Cuantización | Ninguna: posición/rotación viajan como floats; `animation` como `String` en cada pose (ADR-137 Q2). Sin delta compression; AOI de poses 100 m (ADR-074); rosters se reenvían enteros al cambiar una pieza (`roster.rs:158`) | ADR-137 «lo que NO arregla» |
| Entidades | `PHANTOM_ACTIVE_CAP = 6` robapieles activos; `remote_players_count=12` en el log del 10-09 (11 criaturas + 1 humano por el mismo stream) | `phantom.rs:687` |
| IPC Unity↔backend | Loopback, MsgPack con claves a mano (ADR-137 D4 lo deja fuera); `world_state` 10 Hz con rosters clonados enteros para el cliente local (`perf-baseline.md` §«por IPC») | `IPCClient.cs` |

### 0.5 Shaders

| Métrica | Valor | Fuente |
|---|---|---|
| Fuentes | 25 `.shader` + 10 `.shadergraph` (8+6 del vendor, 1 en `Assets/Shaders`, 1 portal en Resources) | find |
| Variantes en el build (release 11:51, `build_interp_20hz.log`) | 712 pasos compilados; `Universal Render Pipeline/Lit` ForwardLit fragment: espacio 61 152 952 320 → **13 824 variantes tras stripping**; suma de «After scriptable stripping» ≈ **20 416** variantes; 0 compiladas (todo caché local) | build log |
| Precalentado | **Ninguno**: 0 `ShaderVariantCollection`, 0 `GraphicsStateCollection`, `m_PreloadedShaders: []`, sin `WarmupAllShaders` | grep |
| Keywords globales | Solo `_FOV`/`_FOVEnabled` del viewmodel (`CameraFOVHandler`, `Shader.SetGlobalFloat`) y el viento de CTI; ningún `Shader.EnableKeyword` global | grep |
| Stripping | `m_StripUnusedVariants: 1`, debug variants stripped, prefiltering Forward+ = 2 | Global settings |

### 0.6 Settings

| Ajuste | Valor | Nota |
|---|---|---|
| `vSyncCount` | 0 en los 5 tiers, **pero** `GraphicsOptions` del vendor aplica `_vSyncMode = 1` en runtime (cap -1 → `vSyncCount = 1`): **el juego sale con VSync ON** a 60 Hz en este monitor | `GraphicsOptions.cs:46`, `GraphicsOptions.asset` |
| `targetFrameRate` | -1 (sin límite) salvo que el jugador ponga cap | ídem |
| `maxQueuedFrames` | Default (2 en D3D11, confirmado por la sonda: `maxQueuedFrames=2`) | `S1.log` |
| GC | `gcIncremental: 1`; `gc-max-time-slice=3` ms en `boot.config` | `ProjectSettings.asset:852` |
| Scripting | **Mono** (`scriptingBackend Standalone: 0`), .NET Standard 2.1, sin `allowUnsafeCode` | `ProjectSettings.asset:841` |
| Graphics jobs | ON para Windows (`m_GraphicsJobs: 1`), modo 0 | `ProjectSettings.asset` |
| Frame Timing Stats | `enableFrameTimingStats: 0` (solo hace falta para release; en development build FrameTimingManager funciona) | ídem |
| Quality tiers | 5 niveles de `QualitySettings` (Very Low…Ultra) que **no cambian el RP asset**; los presupuestos reales los aplica `BackroomsGraphicsApplier` sobre el único `PC_RPAsset` (ADR-134 enm. 1): renderScale 0,60–1,25, sombras 30–120 m, cascadas 1–4, MSAA off–8×, mip limit por `TextureQuality` | `GraphicsQualityPresets.cs:119-153` |
| Default de calidad | `High` = espejo de `PC_RPAsset` (MSAA 2×, SMAA, 50 m, 4 cascadas, sombras de luces adicionales ON) | ídem |
| Audio | DSP buffer 1024, 32 voces reales / 512 virtuales | `AudioManager.asset` |
| Física | 50 Hz, `Maximum Allowed Timestep 0,333` | `TimeManager.asset` |

### 0.7 Hardware de medición

| | |
|---|---|
| CPU | AMD Ryzen 7 7800X3D (8C/16T) |
| GPU | AMD Radeon RX 7900 GRE, 16 GB, driver 32.0.31041.1004, D3D11 |
| RAM | 64 GB |
| Pantalla | 1920×1080 @ 60 Hz |
| Equipo de gama baja real | **No existe.** La gama baja se EXTRAPOLA (§1.6) desde el escalado medido de GPU con `renderScale` 0,6 / 1,0 / 2,0 y desde las cifras de memoria residente. |

## 1. Medición base

### 1.1 Método

- **Build de desarrollo** (`DevPlayerBuild.BuildWindows64`, `BuildOptions.Development`), NO editor, hecho desde el clon
  principal (HEAD `84e6bb15`, con su WIP sin commitear de `STP_Showcase.unity` y cuatro materiales; la sonda se copió allí y se
  borró al terminar). Salida `Builds/PerfDev/` (ignorado por git). Backend `Builds/Backend/backrooms_server.exe` de las 11:53.
- **Sonda** `Assets/_PerfProbe/PerfProbe.cs`: por frame, `FrameTimingManager` (cpuFrame / cpuMain / cpuRender / gpu),
  `ProfilerRecorder` de contadores (`GC Allocated In Frame`, `Draw Calls Count`, `SetPass Calls Count`, `Triangles Count`,
  `Total Used Memory`, `System Used Memory`, `Texture Memory`, `Mesh Memory`, `Render Textures Bytes`, `Used Buffers Bytes`, …)
  y de marcadores del hilo principal sumados por frame (`Update.ScriptRunBehaviourUpdate`, `Physics.Simulate`,
  `Animators.Update`, `PostLateUpdate.FinishFrameRendering`, `Semaphore.WaitForSignal`, `GC.Collect`,
  `Shader.CreateGPUProgram`, `WaitForTargetFPS`, …). Un CSV por run, un JSON de resumen, un censo de renderers/luces
  (recorriendo los chunks del streamer porque `FindObjectsByType` ignora `DontSave`) y, en dos runs, una captura binaria del
  Profiler de 300 frames (`Builds/Captures/perf/*.raw`, 100 MB cada una, fuera del repo).
- **Autopiloto** por inyección de estado del Input System (`InputSystem.QueueStateEvent`): W+Shift, giros de 180° cada 2,5 s,
  botón izquierdo sostenido en «attack». Verificado: 88 m recorridos y 360° de guiñada en 20 s.
- **Grabación**: 60 s tras 15 s de calentamiento desde «mundo listo» (primer chunk bajo la cámara), 1920×1080 ventana,
  **VSync forzado a 0** (salvo el run `_vsync1`), semilla 42, host en solitario con SESSION_MODE=host. Los robapieles del
  backend entran solos (11–20 «remotos» según el run: ver columna).
- Todo el material: `docs/perf/raw/probe/*.csv.gz|json|tsv` (32 MB; los CSV por frame van comprimidos),
  `docs/perf/raw/summary_tables.md` (generado por `docs/perf/raw/summarize.py`, tablas completas de las 10 corridas),
  logs de player en `docs/perf/raw/logs/` (ignorados por git).

**Tres avisos de lectura.** (a) Los runs NO son idénticos entre sí: el punto de aparición, qué luces con sombra hay cerca y
cuántas criaturas están activas cambian de un run a otro (draw calls de 11 k a 30 k, remotos de 0 a 20). Se comparan
tendencias dentro de un run y órdenes de magnitud entre runs, no décimas. (b) `cpuRenderThread` de `FrameTimingManager`
sale pequeño (3–6 ms) mientras el hilo principal espera 8–20 ms en `Semaphore.WaitForSignal` dentro de
`PostLateUpdate.FinishFrameRendering`: con graphics jobs en modo `ClientWorkerJobs`, esa espera es el trabajo de render
repartido en jobs (culling, sombras, emisión), no el hilo de render clásico. (c) `Shader.CreateGPUProgram` sale 0,00 en el
hilo principal en TODOS los runs: en D3D11 la compilación va en el hilo de render y esta sonda no la ve; solo la captura
`.raw` puede atribuirla.

### 1.2 Escenarios y resultado principal

| Escenario | Estado | Run(s) |
|---|---|---|
| S1 quieto mirando pasillo | **MEDIDO** | `S1`, `S1_census` (censo + raw), `S1_mq1` |
| S2 sprint + giros por zona densa | **MEDIDO** (zona de oficinas de la semilla 42; «OFFICE» no se puede elegir a mano) | `S2`, `S2_raw`, `S2_rs200`, `S2_rs060_sh0`, `S2_vsync1` |
| S3 combate: 4 jugadores + fantasmas + disparos | **MEDIDO A MEDIAS**: host con 3 peers conectados a nivel de proceso y **56–57 entidades remotas** replicadas, golpes continuos (puños). Los 3 joiners **no entraron al mundo**: `BACKSTOP: el backend local no confirmó session_joined … en 25 s`; el host registra `SEND_FAIL illegal_gameplay_destination kind=relay_as peer_id=61006/61010` y el joiner acaba en `HEARTBEAT_TIMEOUT`. Es el arnés `RunMultiInstancePlaytest.ps1` con el build de HOY: **bloqueo reportado, no arreglado** (fuera de alcance) | `S3_uuid_perfS3-1` (host) |
| S4 chunk displacement activo | **NO MEDIDO — no observable en WG3.** `tick_teleportation` re-siembra el mapa `chunks` heredado, pero `RequestWg3Chunk` genera el chunk solo de `(net.world_seed, coord)` (`game_loop.rs:1293-1300`) y el streamer de Unity no escucha ningún evento de desplazamiento: hoy el desplazamiento no cambia ni un vértice del mundo servido. Medirlo exige antes decidir cómo se representa en WG3 (ADR) | — |
| S5 carga inicial + streaming | **MEDIDO** (autopiloto «explore», grabación desde mundo listo, sin calentamiento) | `S5`, fases 1–2 de todos los runs |

**Frametime (ms) y cuello de botella** (1080p, 7900 GRE + 7800X3D, VSync off):

| run | frames | dt p50 | p95 | p99 | max | cpuMain p50 | GPU p50 | GPU p95 | % frames GPU>CPU | tirones >2×med | GC.Collect en 60 s | remotos | draw calls | tris |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| S1 quieto | 4134 | **13,92** | 20,36 | 23,50 | 39,3 | 13,90 | 7,99 | 11,96 | 3 % | 8 | 112 (máx 3,6 ms) | 11 | 11 396 | 1,19 M |
| S2 sprint | 4148 | **14,52** | 21,82 | 25,65 | 33,5 | 14,51 | 4,93 | 10,42 | 1 % | 8 | 107 (máx 4,8) | 11 | 12 384 | 2,27 M |
| S3 host, 56 remotos, golpes | 2206 | **24,12** | 41,83 | 46,31 | 57,1 | 24,11 | 6,26 | 22,93 | 7 % | 9 | 169 (máx 4,1) | 56 | 24 287 | 7,92 M |
| S5 explore + streaming | 5945 | **9,44** | 14,24 | 17,58 | **356** | 9,40 | 4,89 | 6,54 | 0 % | 35 | 77 (máx 9,9) | 0–13 | 29 991 | 4,77 M |
| S2 renderScale 2,0 (≈4K) | 2476 | 24,31 | 34,35 | 38,28 | 41,0 | 14,15 | **24,15** | 30,66 | **84 %** | 0 | 79 | 19 | 15 601 | 4,52 M |
| S2 renderScale 0,6 + sombras 0 m | 4955 | 11,28 | 18,55 | 21,34 | 294 | 11,26 | 4,14 | 9,30 | 2 % | 22 | 126 (máx 6,6) | 18–20 | 12 963 | 3,75 M |
| S2 VSync ON (como se distribuye) | 3588 | 16,65 | **16,75** | 17,28 | 33,4 | 12,49 | 7,52 | 9,66 | 0 % | 12 | 94 | 15 | 22 430 | 5,76 M |
| S1 maxQueuedFrames=1 | 3284 | 17,80 | 22,43 | 26,25 | 53,1 | 17,79 | 7,23 | 9,43 | 0 % | 2 | 88 | 15 | 24 068 | 4,11 M |

**Lectura:** en el equipo de desarrollo, a 1080p, el juego está **limitado por CPU en el hilo principal** en todos los
escenarios (la GPU solo manda cuando se cuadruplican los píxeles). Entre 9 y 24 ms de hilo principal, de los que los scripts
propios son 2–5 ms; el resto es **render del lado CPU**: `FinishFrameRendering` 6–14 ms de media, con `Semaphore.WaitForSignal`
5–20 ms (espera a los jobs de culling/sombras/emisión). Los 60 fps solo se sostienen en solitario; con la carga de S3 el host
cae a 41 fps de mediana y 24 fps en p95.

### 1.3 Reparto del hilo principal (ms, media / p95 sobre la grabación)

| run | ScriptUpdate | ScriptLateUpdate | Physics.Simulate | Animators | FinishFrameRendering | Semaphore.WaitForSignal | GC.Collect | WaitForTargetFPS | PlayerLoop |
|---|---|---|---|---|---|---|---|---|---|
| S1 | 2,79 / 4,36 | 0,42 / 0,58 | 0,81 / 1,67 | 0,29 / 0,45 | 8,44 / 13,79 | 10,55 / 16,06 | 0,07 (p99 2,6) | 0 | 14,51 / 20,35 |
| S2 | 2,46 / 4,12 | 0,42 / 0,60 | 1,12 / 1,99 | 0,27 / 0,47 | 8,43 / 14,37 | 9,48 / 15,60 | 0,07 | 0 | 14,45 / 21,80 |
| S3 host | **5,20 / 7,63** | **2,30 / 3,15** | **2,10 / 2,50** | 0,79 / 0,95 | **14,12 / 28,00** | **20,45 / 35,13** | 0,22 (p95 2,8) | 0 | 27,19 / 41,82 |
| S5 | 2,13 / 3,05 | 0,20 / 0,43 | 0,39 / 1,63 | 0,09 / 0,24 | 6,06 / 9,16 | 5,28 / 9,74 | 0,03 | 0 | 10,10 / 14,21 |
| S2 rs 2,0 | 2,44 | 0,61 | 1,19 | 0,30 | 18,04 / 28,02 | 14,38 / 23,75 | 0,08 | 0 | — |
| S2 VSync | 2,26 | 0,48 | 1,31 | 0,25 | 7,46 / 12,42 | 11,86 / 13,97 | — | **3,47 / 6,36** | — |

Los marcadores `Culling`, `Gfx.WaitForRenderThread` y `RenderPipelineManager.DoRenderLoop_Internal` no existen con ese nombre
en el player (salen `n/a`/0): la atribución fina del render del lado CPU queda para la captura `.raw`.

**Coste de los remotos, MEDIDO dentro de S5** (los robapieles entran y salen durante el run):

| remotos activos | frames | dt p50 | Semaphore.WaitForSignal media | Physics.Simulate | draw calls p50 |
|---|---|---|---|---|---|
| 0 | 3517 | 9,08 | 3,92 | 0,07 | 30 013 |
| 5 | 112 | 7,61 | 4,70 | 0,11 | 13 350 |
| 11 | 927 | 10,51 | 6,74 | 0,84 | 17 449 |
| 12 | 1078 | 11,56 | 7,85 | 0,97 | 13 859 |

Con 30 k draws y 0 remotos el frame vale 9,1 ms; con la mitad de draws y 12 remotos, 11,6. **El número de draw calls por sí
solo no explica el frametime** (correlación draws↔dt dentro de cada run: −0,12 a 0,40): pesan más las entidades animadas y lo
que arrastran (física, sombras de sus mallas, hooks). Extrapolando S2_vsync1 (15 remotos, 12,5 ms) → S3 (56 remotos, 24,1 ms):
**≈ 0,28 ms de hilo principal por entidad remota** (ESTIMADO por diferencia entre dos runs no idénticos).

### 1.4 Render: qué hay en pantalla (censo `S1_census`, 3×3 chunks, 12 chunks construidos)

| Métrica | Valor |
|---|---|
| Renderers vivos en la zona cargada | **20 147** (16 453 dentro de los chunks WG3) — visibles 8 251 |
| …de los cuales **paneles de techo** (`LayerLampEmissive`, 1,2 × 0,6 m, un GameObject + MeshRenderer cada uno) | **12 587 (5 711 visibles)** — el 62 % de todos los renderers y el 69 % de los visibles |
| …papeles de atrezo (`URP_M_OfficePapers`, ADR-129) | 3 096 (453 visibles) |
| …macizos fundidos por (planta, estilo, aspecto, loseta) | ~550 renderers (Floor/Ceiling/Wall × 7 estilos), 100–330 visibles cada clase |
| Luces | **3 102 en la zona** (3 101 de WG3), 294–310 encendidas por el radio de 30 m del streamer, **6–8 con sombra suave** (24 caras de mapa de sombra: el player avisa `Reduced additional punctual light shadows resolution by 2 to make 24 shadow maps fit in the 4096x4096 shadow atlas`) |
| Censo de Forward+ (`[wg3-diag]`) | 429–440 luces vivas, **297–359 dentro del frustum** de 512 posibles |
| Colliders WG3 | 19 410 (`BoxCollider` por volumen + `MeshCollider` convexo por prisma) |
| Transforms WG3 | 24 796 GameObjects |
| Materiales distintos visibles | 121 (S1) / 209 (S3); SetPass 152–227 → **el SRP Batcher funciona**; `MaterialPropertyBlock` 0 en el censo (solo `LampFlicker` lo usa, y en el frame censado ninguno estaba activo) |
| Triángulos de malla visibles (MeshFilter) | 236 k; el contador `Triangles Count` da 1,2–7,9 M porque cuenta prepass de DepthNormals (SSAO), color y las 24 caras de sombra |
| Draw calls | 11–30 k por frame con SetPass < 230: el coste es de EMISIÓN (CPU), no de cambio de estado |

### 1.5 Memoria, GC, carga y streaming

| Métrica | S1 | S2 | S3 host | S5 |
|---|---|---|---|---|
| Total Used Memory (Unity) | 3 176 MB | 3 216 | 3 325 | 3 234 |
| System Used Memory (working set) | 1 752 MB | 1 816 | 1 877 | 1 855 |
| Texture Memory | **2 024 MB** | 2 041 | 1 985 | 2 026 |
| Mesh Memory | 192 MB | 209 | 213 | 222 |
| Render Textures | 122 MB (441 a rs 2,0; 40 a rs 0,6) | 139 | 83 | 124 |
| Used Buffers (GPU) | 149 MB | 164 | 301 | 163 |
| GC heap usado | 38 MB | 38 | 43 | 40 |
| GC alloc por frame, media / p95 | **70 KB / 241 KB** | 74 / 258 | **264 / 637** | 49 / 150 |
| GC.Collect (incremental) en 60 s | 112 | 107 | **169** | 77 |

- **Texturas: 2,02 GB residentes ≈ el 89 % de los 2,27 GB que ocuparía TODO el catálogo de texturas del proyecto** (§0.3).
  Con mip streaming apagado y los packs enteros colgando de `Resources`, el juego carga prácticamente todas sus texturas al
  arrancar, se vean o no. En una GPU de 4 GB no cabe con los RT y los buffers; en una de 6 GB va justo.
- **GC: patrón medido en S1** — línea base de **39 KB y 454 asignaciones EN CADA FRAME** con el jugador quieto, más una
  ráfaga de ~240 KB **cada 6–7 frames (10 Hz = `world_state`)**; total 5,0 MB/s → una recolección incremental cada ~0,5 s
  (3–10 ms cada una). En S3 la línea sube a 264 KB/frame. La atribución por pila exige la captura `.raw` (no se puede sin
  deep profile); lo que sí está localizado por lectura: `StpBuildingReplicator/StpItemReplicator/StpCarryableReplicator.LateUpdate`
  crean `HashSet`+`List` por frame y `AddedKey` una `string` por pieza; los 25 hooks por remoto; y la decodificación del
  `world_state` a 10 Hz en `IPCClient` (nuevas listas por snapshot).
- **Carga**: mundo listo a **4,0–5,4 s desde el arranque** (menú → host automático → primer chunk). Tres tirones en todos los
  runs: 1,2–1,6 s en t≈0,8 s (carga de la escena de juego + índice de `Resources`), 0,9–1,2 s en t≈1,5 s y **0,85–1,06 s en
  t≈3,5–4,5 s = los 9 chunks iniciales construidos en UN frame**.
- **Streaming (S5 y S2_rs060)**: `+5 chunks` = frame de **356 ms** (`ScriptUpdate` 334 ms, **19,4 MB** asignados);
  `+3 chunks` = 294 ms (15 MB); `+1 chunk` = 75 ms (55 ms, 3,8 MB). **≈ 55–70 ms y ≈ 3,8 MB de basura por chunk, todo en el
  hilo principal** dentro de `OnWg3Chunk` (ensamblado de macizos, paneles, atrezo, 19 k colliders / 9 chunks).
- **Tirones no atribuidos**: en S1/S2, 8 frames de 28–39 ms con `Semaphore.WaitForSignal` de 20–34 ms y sin GC ni chunk.
  Candidatos: compilación de variantes de shader en el hilo de render (D3D11) o re-render de sombras al entrar un robapieles
  en el radio de una luz con sombra. **Verificable en `Builds/Captures/perf/S1_census_perf_S1_census.raw`** (abrir en el
  Profiler → Load).
- **Shader hitches**: `Shader.CreateGPUProgram` en el hilo principal 0,00 ms en todos los runs (ver aviso 1.1c).

### 1.6 Gama baja y gama media (EXTRAPOLADO, método explícito)

No hay equipo de gama baja. Extrapolación desde dos hechos medidos: (1) el escalado de GPU con la resolución —
0,6× píxeles: GPU 4,14 ms; 1,0×: 4,93; 4,0×: 24,15 — es decir, **a 1080p y por debajo la GPU está dominada por costes fijos
por frame** (24 caras de sombra puntual, prepass DepthNormals de SSAO, clustering Forward+ de 300–360 luces), y solo por
encima escala con los píxeles (≈ 6,4 ms por 1080p-equivalente extra); (2) el hilo principal es 12–24 ms en un 7800X3D.

| Perfil (supuesto) | Factor GPU vs 7900 GRE | Factor CPU 1 hilo vs 7800X3D | GPU 1080p ESTIMADA | Hilo principal ESTIMADO (S2 / S3) | Veredicto |
|---|---|---|---|---|---|
| Gama media (RTX 3060 / R5 5600) | ×3,5 | ×1,3 | ≈ 17 ms | ≈ 19 / 31 ms | GPU y CPU rozan los 60 fps en solitario; S3 no llega |
| Gama baja (GTX 1650 / i5-8400, 4 GB VRAM) | ×8 | ×1,8 | ≈ 33–39 ms **incluso a renderScale 0,6** (coste fijo) | ≈ 26 / 43 ms | 30 fps solo si se recortan los costes fijos de GPU; **2 GB de texturas no caben** en 4 GB con RT+buffers |

Los factores son ratios de throughput teórico redondeados a la baja (ESTIMADO); no sustituyen a una medición real, que
debe hacerse en cuanto exista un equipo de gama baja (es el primer hueco de esta auditoría).

## 2. Evaluación por técnica

Leyenda de veredicto: **IMPLEMENTAR** (problema medido y ganancia estimable), **APLAZAR** (problema real pero no el mayor,
o dependencia sin cumplir), **DESCARTAR** (el problema no aparece en las mediciones o el coste no compensa).

### GPU / render

**1. Subframes híbridos adaptativos** (reproyección de capa estática + capa dinámica a tasa completa)

| Campo | Contenido |
|---|---|
| Problema que ataca | Coste de GPU por frame. **Las mediciones lo descartan como cuello a 1080p**: GPU 4,1–8,0 ms frente a 12–24 ms de hilo principal; solo el 1–7 % de los frames son GPU-bound (84 % únicamente a 4K-equivalente). En gama baja (§1.6) la GPU sí mandaría, pero por costes FIJOS (sombras, prepass, luces), que la reproyección no elimina en el frame «real» y que además hay que rehacer en cada fogonazo/parpadeo |
| Ganancia estimada | Sobre el equipo medido: **≈ 0 ms en el hilo principal** (la emisión de draws se mantiene si la capa estática se sigue dibujando para tener profundidad/motion vectors; si se salta, el ahorro sería el de no emitir, ver técnica 4 que lo consigue sin reproyección). GPU: hasta −50 % en frames reproyectados, NO MEDIDO |
| Esfuerzo | 4–6 semanas: pase custom de Render Graph con historia de color+profundidad, generación de motion vectors (hoy nadie los pide: sin TAA/motion blur), separación de capas (viewmodel con `_FOV` global de ADR-077, jugadores/fantasmas, partículas), decisión adaptativa, relleno de desoclusión, política de «frame real» en combate y con el parpadeo de 300+ luces (multiplicador por luz, no global) |
| Riesgo | Alto: SMAA/MSAA 2× y opaque texture interactúan con la reproyección; ghosting en pasillos repetitivos (mismo patrón = error de reproyección invisible); todo el mundo es runtime `DontSave` y sin motion vectors; Render Graph 17 no expone historia persistente sin `RTHandle` propios |
| Dependencias | Motion vectors, capas de render (Rendering Layers ya se usan para plantas), técnica 6 (pacing), medición en hardware objetivo |
| Compatible con Alpha 1 | **No** |
| Veredicto | **DESCARTAR para Alpha 1; APLAZAR a evaluación pre-Next Fest SOLO si, tras las técnicas 2/4/5, un equipo real de gama media sigue GPU-bound** (§3.4) |

**2. Amortización temporal** (sombras estáticas solo al cambiar, SSAO alterno a media resolución, time-slicing de probes)

| Campo | Contenido |
|---|---|
| Problema que ataca | 6–8 luces puntuales con sombra suave = 24 caras de mapa de sombra re-renderizadas cada frame sobre geometría estática (aviso del atlas, `Triangles Count` ×3–6 respecto a los triángulos visibles); SSAO a resolución completa (`Downsample: 0`) con prepass DepthNormals de toda la geometría |
| Ganancia estimada | Cachear las caras de sombra de luces estáticas (solo re-render si entra un caster dinámico): GPU −1,5 a −3 ms y CPU de emisión proporcional a las caras ahorradas (ESTIMADO: 24 caras × geometría cercana; NO MEDIDO por separado). SSAO `Downsample: 1`: −0,3 a −1 ms GPU (ESTIMADO por el coste típico de SSAO a resolución completa a 1080p; NO MEDIDO). Probes: no hay reflection probes en WG3 → n/a |
| Esfuerzo | Tope de luces con sombra + SSAO downsample: 0,5 día. Cacheo de caras de sombra: 1–2 semanas (URP no lo trae para puntuales; exige pase custom o «shadow caching» manual con `LightShadows.None` + cookies horneadas) |
| Riesgo | Bajo para el tope y el downsample; medio para el cacheo (robapieles y jugadores deben seguir proyectando) |
| Dependencias | Ninguna |
| Compatible con Alpha 1 | Sí (tope + downsample); cacheo post-Alpha |
| Veredicto | **IMPLEMENTAR** el tope de luces con sombra (2 por escena, por distancia) y `Downsample: 1` ya; **APLAZAR** el cacheo |

**3. Resolución dinámica ligada a tiempos de GPU**

| Campo | Contenido |
|---|---|
| Problema que ataca | GPU-bound. Medido: a 0,6× píxeles la GPU baja solo un 16 % (4,93 → 4,14 ms): **la palanca de la resolución es corta** mientras dominen los costes fijos. Solo empieza a pagar por encima de 1080p |
| Ganancia estimada | En el equipo medido: 0 (no es GPU-bound). En gama baja: −5 a −8 ms de los ≈ 33–39 estimados, y solo tras recortar los costes fijos (2, 5) |
| Esfuerzo | 2–3 días (URP 17: `renderScale` dinámico + FSR/STP ya cableados por ADR-134; controlador por `gpuFrameTime` de `FrameTimingManager`; la cámara tiene `m_AllowDynamicResolution: 0`) |
| Riesgo | Bajo; el menú de ADR-134 ya expone escala y upscaler |
| Dependencias | Técnicas 2 y 5 primero; medición en hardware de gama baja |
| Compatible con Alpha 1 | Post-Alpha |
| Veredicto | **APLAZAR** |

**4. GPU Resident Drawer / BatchRendererGroup** para geometría repetitiva

| Campo | Contenido |
|---|---|
| Problema que ataca | **Emisión de 11–30 k draw calls por frame en CPU** (`FinishFrameRendering` 6–14 ms + `Semaphore.WaitForSignal`). Censo: 12 587 paneles de techo idénticos (5 711 visibles) y 3 096 papeles como renderers sueltos; los macizos ya van fundidos (~550 renderers). SetPass 152–227 demuestra que el SRP Batcher hace su parte: lo que falta es reducir el número de emisiones |
| Ganancia estimada | Los paneles son ≈ el 50–69 % de los renderers visibles; quitarlos de la emisión individual debería recortar la parte proporcional de `FinishFrameRendering`: **ESTIMADO −3 a −5 ms de hilo principal en S1/S2** (método: proporción de renderers visibles atribuible a paneles × tiempo de emisión; la correlación draws↔dt es débil, así que el rango es ancho y hay que confirmarlo con un build de prueba) |
| Esfuerzo | Vía A, **fundir los paneles por chunk** como ya hace `AssembleSolids` (1–2 días; el parpadeo pasa de `MaterialPropertyBlock` por panel a un canal de vértice/UV2 con identificador leído por el shader). Vía B, **GRD** (`m_GPUResidentDrawerMode: 1`, medio día): instancia mallas idénticas sin tocar código, pero **`LampFlicker` usa `MaterialPropertyBlock`, que expulsa a esos paneles del GRD** (el 40 % de los vivos parpadea) y el GRD no ayuda a los prepasses de sombra más que la fusión |
| Riesgo | Bajo (visual idéntico); A obliga a rehacer el parpadeo; B depende de compatibilidad de shaders (Lit y los del vendor lo son; `Backrooms/GridWallOffset` por comprobar) |
| Dependencias | Ninguna |
| Compatible con Alpha 1 | Sí |
| Veredicto | **IMPLEMENTAR (vía A primero; B como toggle de prueba de medio día para medir la diferencia)** |

**5. Culling por grafo de salas (WorldGraph) vs occlusion culling estándar**

| Campo | Contenido |
|---|---|
| Problema que ataca | 429–440 luces vivas y 297–359 en el frustum con un tope de 512; 8 251 renderers visibles de 20 147. El streamer enciende luces por **esfera de 30 m en 3D**, que atraviesa las hasta 10 plantas servidas (ADR-102): la mayoría de lo que se procesa está en otra planta, tapado por forjados. El occlusion culling de Unity exige hornear estáticos y no sirve para un mundo `DontSave` generado en runtime |
| Ganancia estimada | Cull por planta (Y del ojo ± 1 planta, usando las máscaras de planta que ya existen, `Wg3StoreyLayers`): luces 429 → ≈ 130–150 y renderers visibles en proporción (**ESTIMADO ≈ −65 %**, método: 3 de 10 plantas); efecto en ms: proporcional al recorte de emisión y de clustering, **NO MEDIDO**, verificable con la sonda en un build de prueba. Culling por portales/salas: post-Alpha |
| Esfuerzo | Cull por planta: 1–2 días (streamer + `lightCullDistance`). Portales sobre el grafo de espacios (`TryGetSpace`): 2–3 semanas |
| Riesgo | Bajo para plantas (la costura de 664 y las rampas requieren ±1 planta, ADR-130); medio para portales (pozos y huecos verticales) |
| Dependencias | Ninguna para plantas |
| Compatible con Alpha 1 | Sí (plantas) |
| Veredicto | **IMPLEMENTAR** cull por planta; **APLAZAR** portales |

### Frame pacing / latencia

**6. `maxQueuedFrames = 1`, limitador adaptativo por P99, política de VSync/VRR**

| Campo | Contenido |
|---|---|
| Problema que ataca | Regularidad. Medido: con VSync ON (como se distribuye) dt p50/p95/p99 = 16,65 / 16,75 / 17,28 ms, **el run más estable de todos**; con VSync OFF el mismo escenario da p95 21,8 y p99 25,7. `maxQueuedFrames = 1`: **NO CONCLUYENTE** — el run tenía el doble de draws y 4 remotos más que su pareja (24 068 vs 11 396); hay que repetirlo con escena idéntica |
| Ganancia estimada | Latencia −1 frame con `maxQueuedFrames = 1` (teórica, NO MEDIDA); regularidad: ya la da VSync en un monitor de 60 Hz |
| Esfuerzo | 0,5 día (política: VSync ON por defecto, cap opcional, `maxQueuedFrames` expuesto en Custom) + 0,5 día de re-medición |
| Riesgo | Bajo |
| Dependencias | Ninguna |
| Compatible con Alpha 1 | Sí |
| Veredicto | **IMPLEMENTAR** la política (mantener VSync ON, cap por `targetFrameRate`, `maxQueuedFrames` tras re-medir) |

### CPU

**7. Eliminación de allocs por frame + Incremental GC**

| Campo | Contenido |
|---|---|
| Problema que ataca | Medido: 39 KB y 454 asignaciones por frame en reposo + 240 KB por snapshot a 10 Hz = 5 MB/s; 107–169 `GC.Collect` incrementales por minuto de 3–10 ms; en S3 264 KB/frame. Incremental GC ya está activo (`gc-max-time-slice=3`): sin él serían pausas de decenas de ms |
| Ganancia estimada | Quitar la ráfaga de 10 Hz (pooling de las listas del `world_state` en `IPCClient`, sin tocar el formato: ADR-137 D4 solo prohíbe cambiar el wire, no reutilizar buffers) y los `HashSet`/`List` por frame de los tres replicadores: **−80 % de basura → recolecciones de 2/s a <0,5/s** (ESTIMADO por la partición medida 39 KB base + 240 KB/10 Hz). En ms: −0,1 a −0,3 ms de media y −3 a −10 ms en los frames de recolección |
| Esfuerzo | 2–3 días tras leer la captura `.raw` (atribución por pila de las 454 asignaciones de base) |
| Riesgo | Bajo; el vendor STP tiene sus propias asignaciones fuera de alcance |
| Dependencias | Captura `.raw` abierta en el Profiler (30 min de Joel) |
| Compatible con Alpha 1 | Sí |
| Veredicto | **IMPLEMENTAR** |

**8. Update manager centralizado**

| Campo | Contenido |
|---|---|
| Problema que ataca | 25 hooks con `Update` por avatar remoto → 1 400 llamadas por frame en S3; `ScriptUpdate` sube de 2,5 a 5,2 ms y `LateUpdate` de 0,4 a 2,3 entre S2 y S3 (≈ 4,6 ms por 45 remotos extra) |
| Ganancia estimada | El overhead de despacho por `Update` es del orden de 1–2 µs por llamada: **≈ 1,5–3 ms en S3, ≈ 0,3 ms en solitario** (ESTIMADO; NO MEDIDO). El grueso de esos 4,6 ms es trabajo de los hooks, no el despacho |
| Esfuerzo | 3–5 días (29 ficheros de `_Migration/STPIntegration/RemoteAvatar`) |
| Riesgo | Medio: orden de ejecución entre hooks y el rig de STP |
| Dependencias | Se subsume en la técnica 9 |
| Compatible con Alpha 1 | Post-Alpha |
| Veredicto | **APLAZAR** (hacerlo como parte de 9, no solo) |

**9. LOD de lógica** (IA, animación, audio, `Animator.cullingMode`)

| Campo | Contenido |
|---|---|
| Problema que ataca | Medido: ≈ 0,28 ms de hilo principal por entidad remota; `Animators.Update` 0,79 ms y `Physics.Simulate` 2,1 ms en S3; TODOS los remotos con `AlwaysAnimate` + `updateWhenOffscreen` (`RemotePlayerManager.cs:611-615`, puesto para arreglar el rig) y 25 hooks activos a cualquier distancia. La IA de las criaturas vive en Rust (no aplica aquí) |
| Ganancia estimada | Con 56 remotos: hooks y animación fuera de 25–30 m a cadencia reducida y `CullUpdateTransforms` para los no visibles: **ESTIMADO −4 a −7 ms en S3** (método: 0,28 ms × remotos lejanos, asumiendo que 2/3 están fuera del radio; NO MEDIDO). En solitario, ≈ −1 ms |
| Esfuerzo | 2 días |
| Riesgo | **Medio**: los seis invariantes del robapieles (ADR-038) y el agarre lo resuelve el CLIENTE; `ProxyRevealHook`, `ProxyGrabHook`, `ProxyMeleeHook` NO pueden dormirse dentro del alcance de ataque; `AlwaysAnimate` se puso por un fallo visual del rig que hay que re-verificar con `CullUpdateTransforms` |
| Dependencias | Ninguna técnica; sí una lista explícita de hooks «gameplay» que nunca duermen |
| Compatible con Alpha 1 | Sí |
| Veredicto | **IMPLEMENTAR** |

**10. Ampliación de Burst/Jobs**

| Campo | Contenido |
|---|---|
| Problema que ataca | Medido: **55–70 ms y 3,8 MB por chunk** en el hilo principal (`OnWg3Chunk` → `Wg3SceneAssembler` + `Wg3MeshBuilder.SetVertices/SetTriangles` + 19 k colliders / 9 chunks); 356 ms por 5 chunks; 0,85–1,06 s en la carga inicial. Burst/Jobs: 0 usos hoy. IA de fantasmas: en Rust (n/a). Interpolación de remotos: trivial (n/a). Audio espacial: `AudioManager.Update` 0,05–0,08 ms (n/a) |
| Ganancia estimada | Construcción de mallas en jobs (`Mesh.MeshData` + Burst) y colliders/instanciación time-sliced a ≤ 4 ms por frame: **tirón de 356 ms → ninguno >20 ms** (ESTIMADO: el trabajo no desaparece, se reparte; el ensamblado de GameObjects sigue en el hilo principal y se trocea). También quita 19 MB de basura por evento (`NativeArray`) |
| Esfuerzo | 1–2 semanas (mallas 3–4 días; time-slicing de colliders/props 3–4 días; tests de que el chunk se ve igual) |
| Riesgo | Medio: el orden de construcción afecta a los hooks que buscan el suelo (ADR-136, spawn) y a `ChunkIsBuilt`; determinismo visual sin cambios (misma malla) |
| Dependencias | Ninguna; habilita la 16 |
| Compatible con Alpha 1 | Post-Alpha (salvo que los tirones de streaming se declaren bloqueantes) |
| Veredicto | **IMPLEMENTAR post-Alpha** |

### RAM

**11. Streaming por chunks con presupuesto (Addressables)**

| Campo | Contenido |
|---|---|
| Problema que ataca | 1,56 GB de `resources.assets.resS` y 2 GB de texturas residentes porque el atrezo (`Resources/Wg3Props` → packs completos) y los datos del vendor cuelgan de `Resources`. Working set medido 1,75–1,88 GB: **no es un problema en 16 GB de RAM; sí lo será en 8 GB** con el navegador y Steam abiertos |
| Ganancia estimada | RAM −0,5 a −1 GB (ESTIMADO: lo que hoy reside sin verse; NO MEDIDO) y tiempo de arranque −0,5 a −1 s (índice de Resources) |
| Esfuerzo | 2–3 semanas: paquete nuevo, 24 `Resources.Load` propios + 14 del vendor, empaquetado por zona, tests |
| Riesgo | Medio (vendor STP carga por `Resources`, no editable; `MeshyImports` gitignored, memoria del proyecto) |
| Dependencias | Técnica 13 primero (cubre la VRAM sin refactor) |
| Compatible con Alpha 1 | No |
| Veredicto | **APLAZAR a pre-Next Fest** |

**12. Datos compactos, ScriptableObjects compartidos, import settings de audio**

| Campo | Contenido |
|---|---|
| Problema que ataca | Audio: 290 clips `DecompressOnLoad` → **≈ 48 MB de PCM** (ESTIMADO por tamaño de fuente; los tres de >25 MB no van al build). Structs/NativeArray: ningún hotspot medido fuera de la construcción de chunks (técnica 10) |
| Ganancia estimada | < 50 MB de RAM; 0 ms |
| Esfuerzo | 0,5 día (pasar ambientes largos a `Streaming`) |
| Riesgo | Nulo |
| Dependencias | — |
| Compatible con Alpha 1 | Sí, pero no cambia nada medible |
| Veredicto | **DESCARTAR como técnica de rendimiento** (hacerlo como limpieza si se toca el audio) |

### VRAM

**13. Mipmap streaming con presupuesto por calidad**

| Campo | Contenido |
|---|---|
| Problema que ataca | **Texture Memory 2,02 GB medidos = 89 % del catálogo entero** con `streamingMipmapsActive: 0` en los 5 tiers. En 4 GB de VRAM (gama baja) no cabe junto a 122–441 MB de RT y 150–300 MB de buffers |
| Ganancia estimada | Con presupuesto 1 024 MB (High) / 512 MB (Low): VRAM de texturas **−50 a −75 %** (ESTIMADO: el presupuesto ES la cota; NO MEDIDO el pop-in). 0 ms de CPU relevantes (el streaming cuesta ~0,1–0,3 ms) |
| Esfuerzo | 1 día (activar en `QualitySettings` por tier, `streamingMipmaps: 1` en las 605 texturas que no lo tienen, presupuesto en `GraphicsQualityPresets`) + 1 día de validación visual en pasillos |
| Riesgo | Bajo: pop-in de mips en texturas de 4096 al girar; los papeles y carteles (legibilidad) pueden necesitar `Mip streaming: off` individual |
| Dependencias | Ninguna |
| Compatible con Alpha 1 | Sí |
| Veredicto | **IMPLEMENTAR** |

**14. Compresión BC7/BC5/BC4, texture arrays/atlas**

| Campo | Contenido |
|---|---|
| Problema que ataca | Ninguno medido: las 1 036 texturas ya van en BC1/BC3 (mismo tamaño que BC7, que solo mejora calidad); SetPass 152–227 con SRP Batcher demuestra que los cambios de material no cuestan; 121–209 materiales visibles |
| Ganancia estimada | 0 MB (BC7 = BC3 en tamaño); BC4 para máscaras de un canal: < 30 MB (ESTIMADO) |
| Esfuerzo | Reimportar 1 055 texturas: horas de importación; atlas: semanas |
| Riesgo | Bajo |
| Dependencias | — |
| Compatible con Alpha 1 | — |
| Veredicto | **DESCARTAR** (calidad, no rendimiento) |

**15. Render targets a resolución interna/media**

| Campo | Contenido |
|---|---|
| Problema que ataca | RT 122 MB a 1080p / 441 MB a 4K-equivalente; SSAO a resolución completa; opaque texture ya a 2×; MSAA 2× duplica el color |
| Ganancia estimada | SSAO `Downsample: 1`: RT −15 MB y GPU −0,3 a −1 ms (ESTIMADO; NO MEDIDO). El resto lo cubre `renderScale` (ADR-134) |
| Esfuerzo | Minutos |
| Riesgo | Nulo (ligera pérdida de detalle en el AO) |
| Dependencias | — |
| Compatible con Alpha 1 | Sí |
| Veredicto | **IMPLEMENTAR** (junto con la técnica 2) |

### Disco / cargas

**16. Addressables LZ4, carga anticipada por dirección de avance en el grafo**

| Campo | Contenido |
|---|---|
| Problema que ataca | Carga 4,0–5,4 s con tres tirones de ~1 s; streaming: 55–70 ms por chunk. El streamer pide el 3×3 alrededor del ojo cada 0,5 s sin mirar la dirección |
| Ganancia estimada | Prefetch por dirección (radio 2 hacia delante): elimina la espera de chunk al cruzar, pero **no el tirón de construirlo** — sin la técnica 10 solo mueve el tirón antes. LZ4: −0,3 a −0,8 s de carga (ESTIMADO; NO MEDIDO) |
| Esfuerzo | Prefetch: 1 día en el streamer (tras la 10). LZ4: parte de la 11 |
| Riesgo | Bajo |
| Dependencias | Técnica 10 (prefetch), 11 (LZ4) |
| Compatible con Alpha 1 | Post-Alpha |
| Veredicto | **APLAZAR** |

**17. Precalentado de shaders y reducción de keywords**

| Campo | Contenido |
|---|---|
| Problema que ataca | 20 416 variantes en el build (13 824 solo en `Lit/ForwardLit` fragment), 0 precalentado; en D3D11 la creación de programas ocurre en el hilo de render y **no se ve desde el hilo principal** (0,00 ms medidos). Los 8 tirones de 28–39 ms por run con `Semaphore.WaitForSignal` de 20–34 ms son el candidato natural, **sin confirmar** |
| Ganancia estimada | Si la captura confirma compilaciones: tirones de 28–39 ms → 0 tras el primer minuto (NO MEDIDO). Si no las confirma: 0 |
| Esfuerzo | 1 día: `ShaderVariantCollection` grabada en una vuelta por Level 0 + `WarmUp` en la pantalla de carga; `GraphicsStateCollection` (PSO tracing) solo aplica a D3D12/Vulkan, y el build va en D3D11 |
| Riesgo | Bajo |
| Dependencias | **Abrir `S1_census_perf_S1_census.raw` y mirar el hilo de render en los frames largos** antes de invertir el día |
| Compatible con Alpha 1 | Sí |
| Veredicto | **IMPLEMENTAR, condicionado a la captura** |

### Red

**18. Delta compression, cuantización relativa al chunk, frecuencia por relevancia**

| Campo | Contenido |
|---|---|
| Problema que ataca | Medido por la tanda de lag del 10-09 (ADR-137): 253,8 KB/s → 1,2 KB/s de subida del host al pasar a MsgPack posicional; hoy `Bytes buffered: 0` y ping 11–26 ms en la partida real por SDR. Queda el crecimiento N×(N−1): ADR-137 cuenta ≈ 1 MB/s de subida para 50 en la misma sala con payload sano. Sin delta ni cuantización; `animation` viaja como `String`; rosters enteros al cambiar una pieza. **Del lado cliente**, el `world_state` a 10 Hz es la ráfaga de 240 KB de basura de la técnica 7 |
| Ganancia estimada | Deltas + cuantización (posición en cm relativa al chunk = 3×u16, giro u16): pose 76 B → ≈ 20–30 B (ESTIMADO aritmético); relevancia por distancia ya existe (AOI 100 m, ADR-074) y su tabla de ahorro está medida en `perf-baseline.md` |
| Esfuerzo | Deltas 1 semana; cuantización 2–3 días; cada uno con bump de wire y ADR (regla dura #7) |
| Riesgo | Medio: compatibilidad con `serde(default)` (ADR-137 D3 lo exige probado), reconciliación con ADR-009 L2 pendiente |
| Dependencias | ADR-137 aceptado (D1 ya en el tronco, wire 62); orden propuesto en ADR-137 Q3: D1 → deltas → rosters |
| Compatible con Alpha 1 | Deltas/rosters sí (es el «próximo paso único» de STATE); cuantización relativa al chunk: post-Alpha |
| Veredicto | **IMPLEMENTAR por la vía de ADR-137** (no es de esta auditoría planificarlo); en el cliente, pooling del snapshot (técnica 7) |

## 3. Priorización

### 3.1 Matriz impacto / esfuerzo

| Técnica | Impacto medido/estimado | Esfuerzo | Riesgo | Cubo |
|---|---|---|---|---|
| 5 Cull de luces/renderers por planta | Alto (−65 % luces/renderers procesados, ESTIMADO) | 1–2 d | Bajo | pre-Alpha |
| 4 Paneles de techo fundidos (o GRD) | Alto (−3 a −5 ms hilo principal, ESTIMADO) | 1–2 d | Bajo | pre-Alpha |
| 2+15 Tope de luces con sombra + SSAO downsample | Medio (GPU −2 a −4 ms ESTIMADO; caras de sombra 24 → 12) | 0,5 d | Bajo | pre-Alpha |
| 9 LOD de lógica de remotos | Alto en S3 (−4 a −7 ms), bajo en solitario | 2 d | Medio (ADR-038) | pre-Alpha |
| 7 Allocs (snapshot 10 Hz + replicadores) | Medio (GC 2/s → <0,5/s) | 2–3 d | Bajo | pre-Alpha |
| 13 Mip streaming con presupuesto | Alto en gama baja (VRAM −50 %), 0 ms | 1–2 d | Bajo | pre-Alpha |
| 6 Política de pacing | Medio (regularidad ya medida con VSync) | 0,5 d | Bajo | pre-Alpha |
| 17 Warmup de shaders | Condicional (tirones de 28–39 ms) | 1 d | Bajo | pre-Alpha si la captura lo confirma |
| 10 Burst/Jobs para chunks | Alto en streaming (356 ms → <20 ms) | 1–2 sem | Medio | post-Alpha |
| 16 Prefetch por dirección / LZ4 | Medio | 1 d + (11) | Bajo | post-Alpha |
| 18 Deltas / rosters / cuantización | Alto en N jugadores | 1–2 sem + ADR | Medio | post-Alpha (vía ADR-137) |
| 3 Resolución dinámica | Bajo hasta recortar costes fijos | 2–3 d | Bajo | post-Alpha |
| 8 Update manager | Bajo (1,5–3 ms solo en S3) | 3–5 d | Medio | post-Alpha, dentro de 9 |
| 11 Addressables | Medio en 8 GB de RAM | 2–3 sem | Medio | pre-Next Fest |
| 2b Cacheo de caras de sombra | Medio | 1–2 sem | Medio | pre-Next Fest |
| 1 Subframes híbridos | Nulo en el cuello medido | 4–6 sem | Alto | descartado para Alpha; evaluación condicional pre-Next Fest |
| 12 Audio / datos compactos | Nulo (48 MB) | 0,5 d | Nulo | descartado |
| 14 BC7 / atlas | Nulo (mismo tamaño) | días | Bajo | descartado |

### 3.2 Orden recomendado, con el dato que lo justifica

1. **Luces y sombras por planta + tope de sombras + SSAO a media resolución** (5, 2, 15) — 2–3 días. Justificación: 297–359
   luces en el frustum de 512, 24 caras de sombra por frame, y la GPU dominada por costes fijos (§1.6). Es lo único que
   mejora a la vez CPU (emisión), GPU y la viabilidad en gama baja. **Gate**: repetir S1/S2 con la sonda; objetivo
   cpuMain p50 < 11 ms y luces en frustum < 150.
2. **Paneles de techo fundidos** (4) — 1–2 días. 12 587 renderers → ~40 por chunk. **Gate**: draw calls p50 < 5 000 en S1.
3. **LOD de lógica de remotos** (9) — 2 días. Justificación: 0,28 ms por remoto y S3 a 41 fps. **Gate**: S3 host p50 < 16,7 ms
   con 56 remotos (o 4 jugadores reales + criaturas cuando el arnés vuelva).
4. **Allocs** (7) — 2–3 días, tras leer la captura. **Gate**: GC.Collect < 30 por minuto en S1, alloc/frame p50 < 16 KB.
5. **Mip streaming** (13) + **pacing** (6) — 1–2 días. **Gate**: Texture Memory < 1 GB en High; p99 de dt ≤ 1,1 × p50 con VSync.
6. **Warmup de shaders** (17) — 1 día, solo si la captura muestra `CreateGPUProgram` en los frames largos.

Total pre-Alpha: **9–13 días** de trabajo, todos sin wire ni ADR nuevo salvo si 9 toca los invariantes de ADR-038 (entonces
enmienda). Post-Alpha: 10 → 16 → 3 → 18 (vía ADR-137). Pre-Next Fest: 11, 2b, y la re-evaluación de 1 en hardware real.

### 3.3 Cubos

- **pre-Alpha 1**: 5, 4, 2 (tope), 15, 9, 7, 13, 6, 17 (condicionado).
- **post-Alpha**: 10, 16, 3, 8 (dentro de 9), 18 (deltas y rosters con ADR).
- **pre-Next Fest (feb 2027)**: 11, 2b (cacheo de sombras), medición en gama baja real, decisión sobre 1.
- **descartado**: 1 para Alpha 1, 12, 14.

### 3.4 Subframes: ¿es la GPU el cuello?

**No.** GPU p50 4,1–8,0 ms frente a 12–24 ms de hilo principal; frames GPU-bound 0–7 % a 1080p; 84 % solo a 4K-equivalente.
La única pieza que apunta a GPU es la extrapolación a gama baja, y ahí el coste es fijo (sombras, prepass, clustering), que
los subframes no eliminan porque el frame «real» tiene que seguir haciéndolo y el parpadeo de 300+ luces obliga a frames
reales frecuentes. **Recomendación explícita: APLAZAR; no invertir hasta que (a) 5/4/2 estén dentro y (b) un equipo de gama
media real mida GPU > CPU en S2.**

### 3.5 ¿Subframes como paquete genérico de URP?

Lo genérico (pase de Render Graph con historia de color/profundidad, motion vectors, decisión adaptativa, relleno de
desoclusión, late latching) no depende del proyecto y podría empaquetarse. Lo que hoy lo impediría, si se construyera aquí
primero: el viewmodel con `_FOV`/`_FOVEnabled` GLOBALES de FPSCore (ADR-077: la capa dinámica tendría que conocer el warp),
las máscaras de planta por Rendering Layers de WG3 (la capa estática se filtra por planta, no por objeto), el parpadeo de
`LampFlicker` por `MaterialPropertyBlock` (el «multiplicador de iluminación» sería un shader propio), y que todo el mundo es
`DontSave` sin motion vectors ni estáticos. Un paquete vendible tendría que ofrecer esas cuatro cosas como puntos de
extensión (capas por máscara, hook de warp, multiplicador por luz como `VolumeComponent`) y demostrar ganancia en un
proyecto GPU-bound, que este no es. **Viable como producto, pero no como subproducto de este proyecto en 2026.**

## 4. Presupuestos propuestos por frame (para el ADR «Perf Baseline & Budget v1»)

Objetivo: gama media a 60 fps (16,7 ms) en High; gama baja a 30 fps (33 ms) en Low con renderScale 0,6. Los presupuestos
salen de la base medida en el equipo de desarrollo dividida por los factores de §1.6, y son **propuesta**, no ley.

| Presupuesto | Gama media (60 fps, 1080p High) | Gama baja (30 fps, 0,6× Low) | Hoy en el equipo de desarrollo (S2 / S3) |
|---|---|---|---|
| Hilo principal (cpuMain p50) | ≤ 12 ms | ≤ 25 ms | 14,5 / 24,1 ms |
| …de los que scripts propios (Update+LateUpdate) | ≤ 4 ms | ≤ 6 ms | 2,9 / 7,5 ms |
| …render CPU (`FinishFrameRendering`) | ≤ 4 ms | ≤ 8 ms | 8,4 / 14,1 ms |
| GPU p50 | ≤ 12 ms | ≤ 24 ms | 4,9 / 6,3 ms (7900 GRE) |
| GC alloc por frame | ≤ 16 KB (1 MB/s) | ≤ 16 KB | 74 / 264 KB |
| GC.Collect durante juego | 0 por minuto en reposo, ≤ 6 en combate | ídem | 107 / 169 |
| Draw calls | ≤ 4 000 | ≤ 2 500 | 12 400 / 24 300 |
| SetPass | ≤ 300 | ≤ 200 | 227 / 207 |
| Luces en frustum | ≤ 150 | ≤ 80 | 297–359 |
| Luces con sombra | ≤ 2 | 0–1 | 6–8 |
| VRAM total (texturas + RT + buffers) | ≤ 3 GB | ≤ 1,5 GB | 2,3 GB |
| RAM working set | ≤ 2,5 GB | ≤ 2 GB | 1,8–1,9 GB |
| Tirón máximo (dt) en streaming | ≤ 33 ms | ≤ 50 ms | 356 ms |
| Carga hasta mundo listo | ≤ 8 s | ≤ 15 s | 4,0–5,4 s |
| Subida del host, 8 peers (gate E0, ADR-073) | según `perf-baseline.md` | — | 1,2 KB/s en solitario tras ADR-137 |

## 5. Entregables y limpieza

- `docs/perf/PERF_AUDIT_v1.md` (este documento), `docs/perf/raw/summary_tables.md`, `docs/perf/raw/probe/*` (CSV por frame,
  resúmenes JSON, censos TSV), `docs/perf/raw/logs/*.log` (ignorados por git), capturas del Profiler en
  `Builds/Captures/perf/S1_census_perf_S1_census.raw` y `S2_raw_perf_S2_raw.raw` (100 MB cada una; fuera del repo).
- `Assets/_PerfProbe/PerfProbe.cs`: sonda reutilizable (env `PERFPROBE=1` + `PERFPROBE_SCENARIO/SECONDS/AUTOPILOT/RENDERSCALE/
  SHADOWDIST/VSYNC/MAXQUEUED/RAW/OUT/QUIT`); solo compila en development build. El analizador se conserva en
  `docs/perf/raw/summarize.py` (lee los CSV comprimidos y regenera `summary_tables.md`); los lanzadores de PowerShell
  (`RunScenario.ps1`, `RunMatrix.ps1`, `RunS3.ps1`) quedaron en el scratchpad de la sesión — si se quieren permanentes,
  van a `tools/dev/perf/` en una tarea propia.
- Borradores de ADR, **solo propuesta**: `docs/perf/ADR-DRAFT-perf-baseline-budget-v1.md` y
  `docs/perf/ADR-DRAFT-subframe-rendering-v1.md`. NO se ha tocado `docs/DECISIONS.md`.
- El clon principal queda como estaba (la copia temporal de `_PerfProbe` se borró; `Builds/PerfDev/` es un build de
  desarrollo reutilizable, ignorado por git). Ningún proceso ajeno se tocó: la partida de Steam de Joel siguió corriendo
  durante toda la medición en sus puertos (7777/7778); la sonda usó 8877+.
