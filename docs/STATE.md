# STATE.md — Estado vivo del proyecto
> Escrito por `/checkpoint` al cierre de cada sesión. **Tope: 200 líneas y 20 KB**, ≤ 160 caracteres
> por línea. La única sección que desborda es «Últimas tandas», y su exceso se traslada VERBATIM a
> `docs/SESSION-LOG.md`, donde vive todo el histórico. Aquí sólo lo vigente.

## Estado
- **WorldGen3 es el mundo servido.** Wire **61** en las dos puntas (`ipc/server.rs:38`, `WireSchema.cs:25`) — ADR-129 lo subió el 06-09.
- **Contrato WG3 v1 = Alpha 1** (`docs/WG3-ALPHA1-ROADMAP.md`): días 1–2 hechos; 3 y 5 APARCADOS detrás de la tanda de oficinas (Joel, 06-09).
- Suite del 06-09: **1398/1398 (85 ignorados)**; `a_region_is_worth_its_size` es FLAKY (600 ms de reloj bajo carga; solo mide 178–261).
- **El commit tiene gate** (`tools/dev/validate-scope.ps1`, hook PreToolUse): rojo = el commit no se ejecuta. Alcance por `git diff --cached`.
- Alpha 1 itch nov 2026 · Next Fest feb 2027 · EA primavera 2027 (`docs/SCALING-ROADMAP.md:196-198`). E0 de red cerrada y medida.

## Próximo paso ÚNICO
- **ADR-131, los VIGILANTES sentados**: escribir el ADR y luego servidor (espécie `Watcher` en `network/faceling.rs`, nace sentado en
  una silla de puesto, neutral, «sentado» en un bit libre de pose) y cliente (pose sentada horneada por script, cabeza que sigue ±90° en
  `LateUpdate`, salto de pose al perderte). Después: ADR-130 r2 decay, día 3 (fundido), r3 streaming vertical (wire 62), r4 torre.

## En curso
- **ADR-128, mundo ×2: tercera pasada, NO commiteado.** 24 tests en rojo; `MAX_SEGMENT_M` no se toca (D3 anulado), `bounds()` en metros
  de MUNDO con `plan_bounds()`. **Sigue sin verificarse que lo servido salga ×2.** Parche verbatim en `docs/SESSION-LOG.md`.
- **Contrato WG3 v1**, 5 días. Día 1 (`65d267c3`) y día 2 (`c7c9dd01`, `510223b8`, ADR-129) cerrados; el día 4 de gramática lo sustituyó
  ADR-129 (rampa, tabique diagonal y anti-enfilada pasan a v2 salvo decisión). Quedan día 3 (rendimiento) y día 5 (verificación, etiqueta).
- **Tanda de oficinas (06-09)**: 1 falso techo + cubículos y 1b variantes HECHAS (`b02df08f`, ADR-129 enm. 1) · 2 ADR-131 · 3 ADR-130 r2 · r3 wire 62 · r4.
- **Saneamiento: B1–B4 fusionados en `migration/worldgraph-v1`** (06-09). Queda B5 (cabeza: el agujero de autoridad) y la lista de Joel para podar ramas.
- **Migración STP servidor-autoritativo**: Steps 1–2 y slice 3.1 (plumbing) hechos y verificados. Falta slice 3.2
  (capa L2 de predicción), reescribir los 8 call sites de `Inventory` y retirar `PlayerController.cs` (DEPRECATED por ADR-009).
- ADR-014 fase 2 (borrado diferido 200 ms + reserva host-only anti-duplicado): backend implementado, **pendiente de playtest**.
- ADR-123 (agacharse y conductos) PROPUESTO, pendiente de Joel. ADR-127 (rampa de techo, wire 61) propuesto para el día 4.

## Riesgos abiertos
- **Autoridad del servidor: los tres agujeros CERRADOS** (`78e156e6` dueño al demoler; `ebb42911`/`bb3c7e5c` cantidad y posición contra el
  roster; aportar material exige dueño y alcance, 5 tests). Queda UNA línea: `process_stp_demolish` valida dueño pero no distancia.
- **Espejos C#↔Rust sin oráculo.** Sin `the_identity_mirror_golden_values` (B4-b), `Wg3Identity.cs` queda verde sin
  nada que lo contraste; igual el hash de `ChunkLootRoll` (dos copias que divergían en negativos) y los goldens de
  `scale`/`density`. Un oráculo JSON común es una sesión: **B5**.
- **Relay sin VPS**: `DefaultRelayAddress` vacío y nadie ha entrado por relay en internet. Dos jugadores en redes
  distintas hoy NO se juntan (medido en el playtest del 02-09). ADR-117 está en código, no en servicio.
- **Crafteo P1 sin cerrar**: las recetas consumen un enum Rust de 9 variantes abstractas, no items (ADR-064). Sin esto, minar no sirve.
- **ADR-009 L2 a medias**: falta la predicción/reconciliación del CLIENTE (`MovementReconciler` se borró y nunca se
  reemplazó; el `delta_update` sí lo consume `AuthoritativePoseApplier`). Causa raíz de la salud al borde de la muerte y del respawn invisible.
- **Dos mundos de colisión** (histórico): el backend colisiona contra su generador y el cliente contra lo que streamea. Con WG3 hay que remedirlo.
- `handle_spawn_world_chest` (`game_loop.rs`) y `World::spawn_corpse` (`world/corpse.rs`) aceptan la posición del cliente sin validar andabilidad.
- Sin anti-cheat de posesión ni cantidad en `consume_item` (ADR-030): trust-the-client asumido y documentado.
- `IPCClient.cs`: cuatro `catch { }` mudos en los notificadores de listeners — pueden tragar fallos hoy mismo.
- La suite EditMode arrastra rojos conocidos; el del lanzamiento del backend sólo pasa con `BACKROOMS_VERBOSE_LOG=1`.
- **Auditoría del 02-09, tres ALTO sin corregir** (`AUDIT-2026-08-28.md`): A28-29 un sobre «Relayed» se cree sin comparar el origen UDP con
  el relay (`classify_inbound`); A28-30 descripción UPnP sin tope (`StackOverflowException`); A28-31 `spawnedOnDeplete` nunca vuelve a `false`.
- **Ramas sin fusionar con trabajo dentro** (06-09): `angry-jackson` (95), `gallant-einstein` (87), `happy-carson` (79); `layout-validator` (4) ya
  está dentro de occluders. Tres experimentos del 03-09 en rama `wip`: stochastic tiling (`558afb54`), decals (`cb61b99c`), sonda de cajas
  (`1c8237ff`). `wf_09042814-13d-4` sucio desde el 27-08. Lista de Joel.

## NO tocar
> Detalle completo, verbatim, en `docs/SESSION-LOG.md` (bloques «NO tocar» y «Última sesión» de 2026-08-03).
- **Robapieles, seis invariantes (ADR-038):** `revealed` sin latch; el atasco es avance proyectado, NO `MoveResult::blocked` (el caso real
  es deslizar contra la pared); alcance de ataque ≠ radio de cuerpo con `segment_is_clear`; rangos de `PhantomTraits` centrados en 1,0
  (test sobre 400 criaturas); `vocal_seq` nunca vuelve a 0 ni va en ráfaga; el asesino del agarre lo resuelve el CLIENTE, sin tocar el wire.
- **Cadena de respawn y muerte**: `RespawnRequester` + `AuthoritativePoseApplier` + gate `SnapPending` en
  `PlayerPoseTransmitter`. Dependencias cruzadas; cambiar sin re-test da rubber-banding.
- **`PHASE1_GOLDENS`** (`grid_gen/tests.rs`): 16 huellas FNV-1a, 4 capas × 4 semillas, jamás regeneradas.
- **Medias paredes**: `MinKneeWallHeight` 1,2 m y `MinLintelClearance` 1,9 m salen del salto real de `FPS_Player.prefab`; bajarlos exige enmienda a ADR-033.
- **`straight_bias` / `branch_persistence`** en `LAYER_PROFILES`: activarlos cambia la topología de todo mundo ya
  generado — breaking change de semilla, exige ADR.
- **Clases nativas de STP/PolymindGames**: nunca editarlas — `PolymindGames.asmdef` no puede referenciar `Assembly-CSharp`; hook externo o corregir después.
- **Gate volumétrico near-spawn**: `volumetric_grid` sólo en el chunk del showcase, y sigue deshabilitado.
- Celdas Rust de 2,5 m: la conversión celda→tile vive SÓLO en Unity (`tileX = cellX / 2`). La de WG3 mide 0,5: toda constante heredada cambia de significado.
- `SilentHealthUIBridge` sincroniza `fillAmount` por reflexión: cambios en `HealthUI`/`Health` deben conservar los nombres.
- **Dado por bueno por Joel (05/06-09)**: luces 2,7/3,2 con alcance 11/9 m y ambiente cálido plano; techos 300/380/1,15; `UvPerMetre` 0,5; feeder 3P 1,5/4,5.

## Deuda declarada
- **`#![allow(dead_code)]` de crate** (`backend/src/main.rs`): recontado el 2026-09-05, **294 warnings únicos en el
  binario y 308 con `--all-targets`** — eran 112/121 el 10-08, o sea ×2,6 en un mes. El argumento que lo sostenía
  («~120 sitios en movimiento») ya no describe lo que hay. Bajarlo por módulo es sesión propia y puede poner clippy en rojo.
- **`world/volumetric_grid.rs`** (3 702 líneas) sólo vive tras `seed == SHOWCASE_SEED`, pero **NO es borrable**: su
  campo está en `ChunkView` y lo consume `ChunkVisualLifecycle.cs:91-93` (entra en el hash de revisión). Retirarlo
  es bump de wire con ADR, no un `git rm`. **B5.**
- **`scale` y `density`**: espejos C#↔Rust con golden values copiados a mano en dos suites, sin oráculo JSON que los ate (`scale.rs:147`, `density.rs:225`).
- **WG3, del contrato para adelante**: relieve de techo sin hacer aunque `height_cm` es por tramo; el dintel
  (`Wg3Carve` con banda vertical) sigue sin usarse; salas ≥ 300 m² con una sola entrada, 31,6 %; catálogo apagado
  (0,6 piezas por región: las 19 miden para el mundo viejo). Pozos sin salida de la cámara: loot y cuerdas son ADR-126 D6, lista v2.
- **Level 0, no bloqueante**: un solo aire (ADR-103 sin consumidor), props decorativos sin colocar, sin señal de planta
  en HUD, fuga de luz entre plantas, claims sin guarda de aislamiento (ADR-110 D4), contenedores host-local, planta −1.
- **Verticalidad jugable** (cruzar de capa por escalera o hueco): diferida a post-Alpha 1 con ADR propio; nota de
  backlog anclada en `DECISIONS.md`. `require_walkable_above`/`_below` se generan y ningún llamador los consume.
- **Farmeo y almacenaje**: E4 pendiente y Bloque A sin empezar (dos decisiones de Joel); el sync de contenedores
  construidos está diferido y pide ADR nuevo con bump de wire, 2-3 días (`docs/FARMING-ROADMAP.md:451`).
- **Atribución de teleports**: `TP_WATCH`/`RESOLVE_DIAG` activos en `game_loop.rs` («REMOVE after diagnosis»); falta playtest y LEER los logs (ADR-026).
- **Entidades PvE (Lurker/Crawler/Shadow) con daño DESACTIVADO** desde 2026-07-07: eran la causa de las muertes silenciosas. Apagadas a propósito.
- **`docs/DECISIONS.md`** (1,38 MB) ilegible entero; se lee por `DECISIONS-INDEX.md` + `grep`. Alternativa sin decidir: un fichero por ADR.
- **El gate de C# no valida nada en un worktree recién creado**: `*.csproj` y `Library/` los genera Unity y viven sólo en el clon principal
  (`CompileCheckClient.sh` daba `MISSING csproj`). Arreglado y documentado (`c99db43a`, `docs/DEV-ENVIRONMENT.md`): copiar `.csproj`, unir `Library`.
- **`STOREY_HEIGHT_CM` (332) no sube con un número** (380–480: 1/9 regiones válidas); `storey_of_floor_cm` clasifica una planta ABAJO en la costura de 664.
- **El runner de tests del editor no contesta** (06-09); el arnés .NET corrió `ProxyLocomotionMathTests` 15/15 pero NO `Wg3LightCadenceTests` (ECall nativa).
- Menores: `MPTRACE` sin commitear en cuatro ficheros del vendor STP; `TODO(balance)` de loot; doc-comments stale.

## Últimas tandas

### 2026-09-06 — 28.ª tanda: variantes de sala — la oficina deja de ser una sola sala repetida (ADR-129 enm. 1)
- Cinco variantes por hash con pesos por carácter en `KNOBS` (`variants: [f32; 5]`, probabilidades ABSOLUTAS): reuniones, recepción,
  archivo, comedor, servidores. `office_variants` corre ANTES que los cubículos, que les quitaban los despachos grandes.
- Cinco `kind` nuevos (15–19: mesa larga, mostrador, microondas, nevera, rack) SIN bump de wire — `kind` es un `u8`. Prefabs horneados con
  bounds medidos; `recentre` en el builder sólo para ellos (el rack traía el pivote en el borde). Rack = `SM_WarehouseShelfSingle`.
- **Centrar a pelo daba CERO salas de reuniones en (0,0)**: el centro de una sala grande lo ocupa un pilar. Ahora rejilla de un metro por
  distancia; archivo y servidores prueban dos separaciones de pared (5 y 30) porque a ras choca la pilastra.
- `props_clear_solids_and_mouths` cazó un fallo PREEXISTENTE de los cubículos: la papelera comía 2 cm de la mampara lateral. Barrido 27/27:
  pisable −0,04 %, mancha 99,7 % = 99,7 %, islas 6,4 → 6,3, nav 100 %. Capturas `Temp/captures/var_*.png` (la de recepción, tapada).

### 2026-09-06 — 27.ª tanda: la planta de oficinas, rebanada 1 — falso techo y cubículos (ADR-105 enm. 18)
- `b02df08f`: `office_ceiling_cm` (270, 300) sólo en despachos/servicios/almacenes del carácter Office, por sala en pasos de 10; naves
  y circulación no cambian. 669 de 898 despachos bajo 3,00 en 39 semillas. El forjado (332) no se toca.
- `office_cubicles`: celdas 2,60 × 2,40, mampara 12 × 140 (el 8 era el barrote de rejilla), pasillo 1,50, filas espalda con espalda;
  mesa + silla (15 % caída) + monitor/teclado/teléfono/bandeja + papelera; una de cada siete vacía. Un metro + grosor a los macizos previos.
- Capturas `Temp/captures/cub_*.png` (B2/B3 de (0,0)): se lee como oficina. Sonda `probe_cubicle_spots`. Barrido 27/27, islas 6,4 = 6,4.
- Plan de la tanda acordado con Joel (cinco rebanadas, memoria `wg3-oficinas-tanda-plan`); el cierre v1 queda detrás.

### 2026-09-06 — 26.ª tanda: las dos ramas que quedaban, fusionadas — occluders y cadencia de luces
- `feat/occluders` (8 commits del 03/04-09): desalineación de vanos (`DOOR_MISALIGN_CHANCE` 0,75), oclusores intra-espacio
  (`OCCLUDER_DENSITY` 1,0, grosor 45 porque el 40 es la viga) y métricas de layout. Conflictos en `fill.rs`/`plan.rs` re-aplicados.
- Destapó un bug de HEAD: el vano «lado a lado» del atrio (ADR-104 enm. 3) se cortaba en la banda ENTERA, también sobre el
  muro compartido con otro atrio sin sala encima y cuando un pasillo sólo rozaba la esquina. Ahora se recorta sólo bajo cada sala.
- Los oclusores esquivan ahora pozos, ventanas y bocas de tramo (los emisores de HEAD son posteriores a la rama); bloques sin longitud múltiplo de 50.
- `unity-lighting-cadence` (3): tubos muertos, jitter en celda, tinte ±200 K, parpadeo, sobre 11 m / 2,7 y paneles; tubo muerto = sin Light.
- Verificación: cargo 1393/1393, clippy y fmt limpios, barrido 27/27 (pisable −0,6 %, mancha 99,7 %, islas 6,4 = 6,4);
  CompileCheck 0. Los 7 tests de la cadencia NO corrieron: `CorrelatedColorTemperatureToRGB` es nativa; pide el editor.

### 2026-09-06 — 25.ª tanda: la fusión — seis commits sueltos de cuatro sesiones y el saneamiento sobre la rama principal
- Suelto desde el 02/03-09 y ahora commiteado: `.meta` huérfanos del relay (`c2cd88af`), A28-29…33 al registro (`0a3cede5`), facelings
  que nacían fuera del radio de retirada (`2440ef25`, 13/13), materiales del Nivel 0 (`5d281833`), escena re-guardada (`674fdba1`).
- Animación 3P fase 1 (`cf3d8543`): 15/15 en arnés .NET porque el runner del editor no contestó; `m_IsKinematic` del prefab intactos.
- `chore/saneamiento-arranque` (22 commits, B1–B4) fusionada con `--no-ff`; único conflicto `docs/STATE.md`, resuelto al formato denso con
  las tandas 23–25 y el verbatim de 4j–4l en `SESSION-LOG.md`. Índice de ADR regenerado: 129 y 130 faltaban (170 entradas).
- Sin fusionar y medido: `feat/occluders` choca en `fill.rs`/`plan.rs`; `unity-lighting-cadence` en `Wg3SceneAssembler.cs` (fusionadas en la 26.ª).
- Cierre de 22 sesiones (28-08 → 06-09) en `SESSION-LOG.md`; tres experimentos a rama: `558afb54` teselado, `cb61b99c` decals, `1c8237ff` sonda.

### 2026-09-06 — 24.ª tanda: la foto de la oficina (ADR-129, wire 61) y los sótanos en el plan (ADR-130 rebanada 1)
- Día 2 del cierre: `c7c9dd01` UV a 0,5/m (un factor); `510223b8` placas de 60 cm y paneles 60×120 en rejilla por tramo (mallas emisivas).
- **ADR-129, wire 61** (`99c566dd`, `2328a19e`): `Wg3Prop` (posición, giro, `kind`, estilo) que `office_props` emite por sala esquivando bocas,
  macizos y pozos; lo que frena lleva macizo INVISIBLE (`STYLE_HIDDEN_BIT` 0x40). Cliente: `Resources/Wg3Props/<Kind>`. 15 502 anclas / 39 semillas.
- **ADR-130 rebanada 1** (`11cc3444`, `897ac86a`): `basements_for` ((rx·3+rz·5) mod 4 == 0), `REGION_BASEMENTS = 3`, `ground`; la calle de
  una torre no se hunde, bajo tierra ni atrios ni pozos. wg3 162/162. Capturas `d_b3_hall_*` a −9,96 con luz, moqueta y puerta.
- Queda de la foto: variedad por `kind`, desorden, avisos, tintes (Joel), suciedad. El wire 62 lo disputan la rampa y el streaming vertical.

### 2026-09-05 — 23.ª tanda: los techos al canon, la luz sin dueño y la megasala (ADR-104 enm. 4–5)
- `42993519`: mediana de techo 2,50 → 3,10 m (`CEILING_MIN_CM` 300, `MAX` 380, `SKEW` 1,15); `[nav]` mide la componente MAYOR; `rect_of` gira.
- `af82069c`: nadie ponía `RenderSettings` en WG3 (era el camino de WG2): ahora `Wg3ChunkStreamer.OnEnable`; alcance de lámpara 6 → 11 m
  porque a 3 m de altura una puntual de 6 deja 3 de radio útil. `2d66a544`: la luz pide la máscara de SU VOLUMEN (`ForLightIn`), no la del suelo.
- `a4c2c1f4`: megasala hasta 16,36 m sólo sin planta encima (`void_storeys_above`); `CEILING_CAP_M` 7 → 17. `957d8a18`: la sonda medía
  STOREYS = 2, no el mundo (49 regiones: 27 de 3 plantas). `f250600f`: luces al doble (2,7 / 3,2) a petición de Joel tras el playtest.

### 2026-09-05 — 22.ª tanda: el saneamiento entero (B2–B4) y el primer rojo que caza el gate
- **B2, gate de commit** (`9a5c680e`): `validate-scope.ps1` por alcance de `git diff --cached`, disparado por un
  PreToolUse; cuatro hooks fusionados en dos y *fail closed*. El parser del índice de ADR perdía en SILENCIO
  cualquier encabezado que no encajara (`## ADR-121 y ADR-122`): 164 en el fichero, 163 en el índice.
- **B2 docs** (`5a80d295`): 10 ficheros a `docs/archive/` con cabecera CONGELADO; de `AGENTS.md` sobrevive UNA regla (la 13) y de `LEEME.md` ninguna.
- **B3** (`b4d3112c`) 50,1 MB fuera; `STP/Demo` NO se toca (dentro vive `STP_Showcase.unity`, la escena real). **B4**:
  `ChunkRenderer.cs` (3 925 líneas), el campo de identidad de ADR-103, `SpaceRole::Junction`, `BackroomsWithSTP.unity`
  y tres comentarios que mandaban a clases borradas. El gate cazó su primer rojo: un test llevaba veintitantos
  commits en rojo, tapado por su guarda de cobertura (la serie, medida commit a commit, en el log).

### 2026-09-04 — 21.ª tanda: auditoría del proyecto y B1 del saneamiento (arranque de sesión)
- **La regla dura #1 era incumplible, no cara.** `Read` rechaza `STATE.md` entero (tope 256 KB) y todo
  tramo de más de 25 000 tokens: 500 líneas medían 51 211. `Riesgos abiertos` y `Estado actual` no entraban nunca.
- `77801e2a` traslado verbatim de 595 370 B a `SESSION-LOG.md` (verificado byte a byte); `36c04939`
  compresión: **688 532 → 11 587 B, 2 588 → 121 líneas**. El verbatim de lo conservado también está en el log.
- `68831a61` `tools/dev/CheckStateBudget.py` (topes y presupuesto de 36 KB) + `GenDecisionsIndex.py` →
  `docs/DECISIONS-INDEX.md` (7 917 B; 105 números, 163 encabezados) + `ARCHITECTURE.md` con contratos reales.
- `7534b0a2` el hook de Stop baja a solo `fmt`: corría clippy + tests en CADA parada, sin bloquear nunca.
- Auditoría completa (Alpha 1, código, repo, flujo) en el plan de la sesión; B2–B5 sin empezar.

### 2026-09-04 — 20.ª tanda: ningún macizo se había visto nunca; wire 56→60, pozos del Nivel 0
- **El hallazgo (`5aa8fc07`): desde wire 50 ningún macizo se dibujaba en su sitio** — `AssembleSolid` daba el centro local
  y `Wg3MeshBuilder` lo restaba otra vez: todo apilado en (0,0). Una semana de enmiendas juzgada con capturas ciegas.
- **Wire 56** (`faec7ffe`): `Wg3Solid { yaw_deg, shape }` — caja, cilindro, media luna, octógono (ADR-121 D1, ADR-125);
  cuadrados girados no dan círculo, de ahí el byte. **57**: arco de puerta. **58**: marco con `STYLE_DECOR_BIT`.
- **Wire 59–60: POZOS del Nivel 0** (ADR-126 + enm. 1). Rejilla en salas ≥ 7 m de la planta baja, 10–50 m de caída y
  cámara oscura; el daño lo calcula el vendor. Enm. 1: pozos 2×2 a paso 2,5, pasillo de una celda.
- **Día 1 del cierre, la misma noche** (`65d267c3`): el atrio no se abre a la nada (ADR-104 enm. 3) — el techo estaba, el
  negro era el muro alto quitado sin sala arriba. Barrido 27 regiones: mancha 99,6 %, 1,3 islas, nav 100 %. Joel: 7/10.

### 2026-09-04 — 19.ª tanda: zonas con carácter, laberinto de rejilla, y ADR-124 revertido tres veces
- **Enm. 14** (`8d423f81`): `fill::Character` {abierto, oficina, nave, laberinto, raro} por el campo de densidad, con
  la tabla `KNOBS` de todas las probabilidades por carácter. Macizos por espacio: laberinto 6,5 → 1,8, raro 9,7 → 3,4.
- **Enm. 15** (`2d4259c4`): laberinto de rejilla por árbol de expansión sobre celdas de 2,5 m — conectividad por
  construcción. Divisiones 1 028 → 3 729 en 27 regiones.
- **ADR-124 «menos pasillos» probado tres veces y revertido** (`c3f5b043`): `CORRIDOR_DEPTH` 2 da lo pedido pero rompe
  6 de 300 regiones. **El enrutador es el límite, no una constante**: es una sesión de `route.rs`.
- Lección de medida: la repetición LOCAL sube con la zonificación y la GLOBAL baja. Antes de vender un «50 %», decir cuál.
