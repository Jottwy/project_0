# STATE.md — Estado vivo del proyecto
> Escrito por `/checkpoint` al cierre de cada sesión. **Tope: 200 líneas y 20 KB**, ≤ 160 caracteres
> por línea. La única sección que desborda es «Últimas tandas», y su exceso se traslada VERBATIM a
> `docs/SESSION-LOG.md`, donde vive todo el histórico. Aquí sólo lo vigente.

## Estado
- **WorldGen3 es el mundo servido.** Wire **69** en las dos puntas (`ipc/server.rs:38`, `WireSchema.cs:25`) — ADR-149 enm. 7, ropa y rotura (15-09).
- **Contrato WG3 v1 = Alpha 1** (`docs/WG3-ALPHA1-ROADMAP.md`): días 1–2 hechos; 3 y 5 APARCADOS detrás de la tanda de oficinas (Joel, 06-09).
- **Multijugador por Steam, SIN LAG** (10-09): **253,8 → 13,9 KB/s**, cola → **0**. **Build 25238618 SUBIDA con todo (wire 63), SIN rama: Joel la habilita.**
- **El commit tiene gate** (`tools/dev/validate-scope.ps1`, hook PreToolUse): rojo = el commit no se ejecuta. Alcance por `git diff --cached`.
- Alpha 1 itch nov 2026 · Next Fest feb 2027 · EA primavera 2027 (`docs/SCALING-ROADMAP.md:196-198`). E0 de red cerrada y medida.

## Próximo paso ÚNICO
- **Tramos en vez de poses para lo previsible** (ADR pendiente, bump de wire). Con 16 el relay es el 57 % de 185,6 KB/s y 9 de cada 10 parejas
  son una CRIATURA: la que recorre un pasillo no necesita cadencia sino «de aquí a allí a esta velocidad», y el cliente interpola. **A todos por
  igual** — ADR-074 prohíbe que el filtro distinga la fuente. PVS ya ENCENDIDO (ADR-140, `ad7d3c57`), oculta el 24,3 %.

## En curso
- **ADR-128, mundo ×2: tercera pasada, NO commiteado.** 24 tests en rojo; `MAX_SEGMENT_M` no se toca (D3 anulado), `bounds()` en metros
  de MUNDO con `plan_bounds()`. **Sigue sin verificarse que lo servido salga ×2.** Parche verbatim en `docs/SESSION-LOG.md`.
- **Contrato WG3 v1**, 5 días. Días 1 (`65d267c3`) y 2 (`c7c9dd01`, `510223b8`, ADR-129) cerrados; el día 4 de gramática lo sustituyó ADR-129
  (rampa, tabique diagonal y anti-enfilada a v2 salvo decisión). Quedan día 3 (rendimiento) y día 5 (verificación, etiqueta).
- **Oficinas (33.ª) y B1–B4 FUSIONADOS en `migration/worldgraph-v1`** (06-09). Queda ADR-130 r2b, día 3, r3, r4, B5 (autoridad) y podar ramas (Joel).
- ADR-123 (agacharse y conductos) PROPUESTO, pendiente de Joel. ADR-127 (rampa de techo, wire 61) propuesto para el día 4.
- **Migración STP servidor-autoritativo**: Steps 1–2 y slice 3.1 (plumbing) hechos y verificados. Falta slice 3.2
  (capa L2 de predicción), reescribir los 8 call sites de `Inventory` y retirar `PlayerController.cs` (DEPRECATED por ADR-009).
- ADR-014 fase 2 (borrado diferido 200 ms, reserva anti-duplicado): backend hecho, **sin playtest**. Sin ver en Play: linterna (`2d1b0118`), venda, crafteo.
- **ADR-145 completo en código** (D1-D7), enmienda de estado escrita. Sin ver en Play; EditMode sin ejecutar.

## Riesgos abiertos
- **ADR-155 enm. 3 vs ADR-105 enm. 22**: base de validación 6/306 quedó vieja (ahora 0/306); remedir L1e si se retoma.
- **`MAX_POSES_PER_BATCH` 14→8** (ADR-149 enm. 7, `garments`): sin medir datagramas/s con N=50.
- **Autoridad del servidor CERRADA, también la última línea** (12-09): dueño al demoler (`78e156e6`), cantidad y posición contra el roster
  (`ebb42911`/`bb3c7e5c`), y ya las TRES rutas de construcción miden alcance y rechazan sin pose (`STP_PICKUP_MAX_DISTANCE`, 8 tests).
- **Espejos C#↔Rust sin oráculo.** Sin `the_identity_mirror_golden_values` (B4-b), `Wg3Identity.cs` queda verde sin nada que lo
  contraste; igual el hash de `ChunkLootRoll` y los goldens de `scale`/`density`. Un oráculo JSON común es una sesión: **B5**.
- **Steam (ADR-135 enm. 2) VERIFICADO el 09-09 con redes y cuentas distintas: el criterio físico ya se cumple.** Sin probar el relay propio
  (ADR-117 sin VPS), que ya NO bloquea Alpha 1. **`invited_by` sin prueba** (ADR-136 enm. 1 (a)): cualquiera nace a 2 m de una identidad viva.
- **Crafteo P1 cerrado en código (ADR-064 enm. 1, 12-09)**: `craft_item` valida y muta `stp_inventory`; 12 recetas con oráculo JSON. **Sin ver en Play.**
- **ADR-009 L2 a medias**: `MovementReconciler` sigue borrado, pero **NO era la causa del lag** (RTT real 24 ms, movimiento client-authoritative).
  Lo vivo de ese hueco: salud al borde de la muerte y respawn invisible. **LOD de entidades DESCARTADO con medida**: 0,2 % del tick (10-09).
- **Dos mundos de colisión** (histórico): el backend colisiona contra su generador y el cliente contra lo que streamea. Con WG3 hay que remedirlo.
- `handle_spawn_world_chest` (`game_loop.rs`) y `World::spawn_corpse` (`world/corpse.rs`) aceptan la posición del cliente sin validar andabilidad.
- Sin anti-cheat de posesión ni cantidad en `consume_item` (ADR-030): trust-the-client asumido y documentado.
- `IPCClient.cs`: cuatro `catch { }` mudos en los notificadores de listeners — pueden tragar fallos hoy mismo.
- **EditMode: 1 528 tests, 12 rojos** medidos el 12-09 headless: los 12 del 08-09 (`Wg3Composer` ×3, `Wg3DensityField` ×2, `Wg3ScaleField`,
  `StorageRackDisplay` ×2, `Wg3LightCadence`, `IgdProtocol` flaky, `OfficeAmbience`, `ZoneAmbienceSet`). El 13.º, `ViewmodelWarpTests` por la
  venda con URP/Lit, ARREGLADO el 12-09 (`BR_Bandage_FP_Material`). En cargo, `phantom_sprints_after_patience_exceeded` **flaquea cargado**.
- **Auditoría 02-09, tres ALTO** (`AUDIT-2026-08-28.md`): A28-29 «Relayed» sin comparar UDP; A28-30 UPnP sin tope; A28-31 spawnedOnDeplete no resetea.
- **Ramas viejas CERRADO** (06-09). Aparcados a propósito: teselado estocástico (`558afb54`), decals (`cb61b99c`), sonda de cajas (`1c8237ff`).

## NO tocar
> Detalle completo, verbatim, en `docs/SESSION-LOG.md` (bloques «NO tocar» y «Última sesión» de 2026-08-03).
- **Robapieles, seis invariantes (ADR-038):** `revealed` sin latch; el atasco es avance proyectado, NO `MoveResult::blocked` (el caso real
  es deslizar contra la pared); alcance de ataque ≠ radio de cuerpo con `segment_is_clear`; rangos de `PhantomTraits` centrados en 1,0
  (test sobre 400 criaturas); `vocal_seq` nunca vuelve a 0 ni va en ráfaga; el asesino del agarre lo resuelve el CLIENTE, sin tocar el wire.
- **Cadena de respawn y muerte**: `RespawnRequester` + `AuthoritativePoseApplier` + gate `SnapPending` en `PlayerPoseTransmitter`; sin re-test, rubber-banding.
- **`PHASE1_GOLDENS`** (`grid_gen/tests.rs`): 16 huellas FNV-1a, 4 capas × 4 semillas, jamás regeneradas.
- **Medias paredes**: `MinKneeWallHeight` 1,2 m y `MinLintelClearance` 1,9 m salen del salto real de `FPS_Player.prefab`; bajarlos exige enmienda a ADR-033.
- **`straight_bias` / `branch_persistence`** en `LAYER_PROFILES`: activarlos cambia la topología de todo mundo ya generado — breaking de semilla, exige ADR.
- **Clases nativas de STP/PolymindGames**: nunca editarlas — `PolymindGames.asmdef` no puede referenciar `Assembly-CSharp`; hook externo o corregir después.
- **Gate volumétrico near-spawn**: `volumetric_grid` sólo en el chunk del showcase, y sigue deshabilitado.
- Celdas Rust de 2,5 m: la conversión celda→tile vive SÓLO en Unity (`tileX = cellX / 2`). La de WG3 mide 0,5: toda constante heredada cambia de significado.
- `SilentHealthUIBridge` sincroniza `fillAmount` por reflexión: cambios en `HealthUI`/`Health` deben conservar los nombres.
- **Dado por bueno por Joel (05/06-09)**: luces 2,7/3,2 con alcance 11/9 m y ambiente cálido plano; techos 300/380/1,15; `UvPerMetre` 0,5;
  feeder 3P 1,5/4,5; `FistFromTail` del bote a 0,68 (0,55 medido y PEOR, ADR-077 enm. 5).
- **Aplicar gráficos EN EL EDITOR ensucia** `PC_RPAsset.asset` y `QualitySettings.asset`: al acabar, dejar `High` y revertir. En build no pasa.

## Deuda declarada
- **`#![allow(dead_code)]` de crate** (`backend/src/main.rs`): 2026-09-05, **294 warnings únicos y 308 con `--all-targets`** (112/121 el 10-08: ×2,6 en un mes).
  El argumento que lo sostenía («~120 sitios en movimiento») ya no describe lo que hay; bajarlo por módulo es sesión propia y puede poner clippy en rojo.
- **`world/volumetric_grid.rs`** (3 702 líneas) sólo vive tras `seed == SHOWCASE_SEED` pero **NO es borrable**: su campo está en `ChunkView` y lo
  consume `ChunkVisualLifecycle.cs:91-93` (entra en el hash de revisión). Retirarlo es bump de wire con ADR, no un `git rm`. **B5.**
- **`scale`/`density`**: espejos C#↔Rust con goldens copiados a mano, sin oráculo que los ate (`scale.rs:147`, `density.rs:225`). **B5.**
- **WG3 tras el contrato**: relieve de techo y dintel `Wg3Carve` sin usar, salas ≥300 m² con una entrada (31,6 %), catálogo apagado, pozos ciegos.
- **Level 0, no bloqueante**: un solo aire (ADR-103 sin consumidor), props decorativos sin colocar, sin señal de planta
  en HUD, fuga de luz entre plantas, claims sin guarda de aislamiento (ADR-110 D4), contenedores host-local, planta −1.
- **Verticalidad jugable** (escalera o hueco entre capas): diferida a post-Alpha 1 con ADR propio; `require_walkable_above`/`_below` sin consumidor.
- **Farmeo y almacenaje**: E4 y Bloque A sin empezar (dos decisiones de Joel); el sync de contenedores construidos pide ADR y bump de wire, 2-3 días.
- **Pendiente de la tanda del lag** (10-09): `PeerList` a 27,5 datagramas/s con UN jugador; **D2** de ADR-139 (partir `ChunkState`, con bump) sin
  decidir; el `layout` viaja aunque el cliente GENERA el chunk. **`animation` NO es peso muerto** (ADR-137 enm. 2): lo consume `ProxyPickupHook` y
  es el teatro del robapieles. `BWTRACE`/`ENTTRACE` en `warn!` a propósito: devolver a `info!` al terminar.
- **Atribución de teleports**: `TP_WATCH`/`RESOLVE_DIAG` activos en `game_loop.rs` («REMOVE after diagnosis»); falta playtest y LEER los logs (ADR-026).
- **Entidades PvE (Lurker/Crawler/Shadow) con daño DESACTIVADO** desde 2026-07-07: eran la causa de las muertes silenciosas. Apagadas a propósito.
- **`docs/DECISIONS.md`** (1,38 MB) ilegible; se lee por `DECISIONS-INDEX.md` + `grep`. ADR-149 enm. 1-8 en `###`: invisibles a ambos (nota-puntero 15-09).
- **`STOREY_HEIGHT_CM` (332) no sube con un número** (380–480: 1/9 regiones válidas); `storey_of_floor_cm` clasifica una planta ABAJO en la costura de 664.
- **Sin ver en juego (06-09)**: monitor y despacho oscuro, recepción, techo roto, decaimiento del cliente en B3 (r2b), audio en Play; carteles SÍ.
- **Venda**: sólo el daño LOCAL abre heridas (el autoritativo llega por `SetHealthSilent` sin evento, ADR-025), así que hoy no deja herida que
  vendar; cerrarlo es wire con ADR. El estado es VOLÁTIL por alcance (Joel): persistirlo toca el schema de guardado.
- **«Additional Light Shadows» promete más de lo que hace**: las luces de WG3 nacen `DontSave` y `FindObjectsByType` no las ve (ADR-134, «lo que queda fuera»).
- **Audio + gráficos MEZCLADOS de idioma**: gráficos en inglés (ADR-134, decisión Joel), voz en español (ADR-046).
- **El gate de C# miente en un worktree**: `csproj` y `Library` son del clon principal (`docs/DEV-ENVIRONMENT.md`), y el `.csproj` es una FOTO:
  tras tocar un `.asmdef` da falso rojo o falso verde hasta que Unity refresque. Menores: `MPTRACE` sin commitear en cuatro ficheros del vendor
  STP; `TODO(balance)` de loot; doc-comments stale; el agarre TOCA pero no RODEA (el bote cuelga de la tapa) y el pulgar entra 12,8 mm sin puerta.

## Últimas tandas

### 2026-09-15 — 56.ª tanda: consolidación multiparallel — mapping, ropa, laberinto
- **Fusión multiparallel a `migration/worldgraph-v1`**: Mapping P1c (7 commits, IPC hojas, wire 70 SIN activar), BookOpen bit 9 (`ProxyBookHold.cs`, sin ADR,
  bit libre ADR-044), ADR-155 L0–L1e (código hecho, `MAZE_BIOME_ENABLED = false` a propósito), ADR-105 enm. 22 visto bueno (separación 200 cm, pozos).
- **ADR-149 enm. 7 + ADR-022**: ropa (`outer:i32`, rotura por zona), dos sesiones en paralelo (`058320d0`,`49dd4ade`). **Wire 68→69**.
- **Base validación: 300/306 → 306/306 regiones** tras ADR-105 enm. 22 (fix `well_mouth_carves` + regla divisiones; 27/27 ✓).
- **Build headless**: matar `backrooms_server.exe`, cerrar Editor (permiso Joel). Cargo 1597/0/107, rustfmt ✓, C# 0 errores, release ✓. Backend deployado,
  cliente Steam BuildID 25310397 SIN SetLive. Editor cerrado al terminar.

### 2026-09-13 — 55.ª tanda (paralela): el inventario nuevo también en STP_Showcase, solo la interfaz y por enganche
- **Opción A de Joel, sin tocar el YAML del vendor** (`482b1ffd`, `5b513fd2`): `BackroomsShowcasePlayerUi` instancia `BR_UI_Player` y el `GameMode` la
  adopta por `PlayerUI.Instance ?? SpawnPlayerUI()` (`GameMode.cs:102`). Mochilas, carga y cuerpo siguen SOLO en `BR_InventoryTest` (ADR-147 enm. 1).
- **La opción B pide cerrar ADR-147 §4**: base 30→9 deja fuera de rango los huecos 9-29 (`InventoryRestorer.cs:318`), tope de 64 pilas, paquete sin guardar.
- `BackroomsHideUnboundContainers` esconde 8 paneles sin contenedor, por LISTA: Alrededor también es `ItemContainerUI`. Las secciones saltan la rejilla apagada.
- **Bug destapado**: `BackroomsWornSlotsUI` sin contenedor dueño enseñaba 2 de los 6 huecos de la funda y escondía lo de los otros 4 → `VisibleSlots`.
- **Trampa**: Play entra SIN recargar escena (`EditorSettings.asset`, opciones = 2) y `sceneLoaded` no salta para la abierta: todo enganche de escena
  cubre también `AfterSceneLoad`. Verificado: compile 4/4, EditMode 39/39, Play de Joel («8 paneles escondidos», se ve bien).
- **SIN push**: el tronco va 75 commits por delante de origin, con wire 67 (`d22cf334`), ADR-146, ADR-149 R0/R1, mapping y WG3 A1-A4. Joel decide.

### 2026-09-13 — 55.ª tanda: manos sobre objetos — `Tools > Interaction Authoring` y API para Claude (ADR-150 PROPUESTA)
- **Horneado, no Animation Rigging**: perfil en espacio del objeto, IK de dos huesos + dedos acoplados, coste de naturalidad barrido.
  API JSON (`tools/dev/HandInteraction.ps1`), puente con editor abierto, CLI headless, ventana. Guía: `docs/systems/hand-interaction.md`.
- **Enm. 4: sin tembleque y destornillador**: el antebrazo temblaba por un salto de signo de la torsión (160–178°), ahora continua +
  test genérico; destornillador por `Regrip` sin penetraciones (coste 2,60, agarre de precisión); los `Template_Attack` no se hornean.
- **Derecha rehecha (enm. 2, rol `Regrip`)**: la muñeca de 129° pasa a antebrazo −10°, coste 4,47, pomo despejado; 24/25 verdes (1 saltado).
  Trampa: el offset del modelo es ENTRADA de la búsqueda y Regrip lo reescribe (4,46 → 186); ahora `baseNodeLocal*` en el perfil.
- **Izquierda en la manivela (enm. 3)**: pomo en reposo a 270° (barrido de 8), agarre desde abajo coste 4,70; la cuerda lo sigue girando y
  deslizando la mano, peor fase coste 16 (palma rozando a 0,23). **Sin ver en Play.** Nuevo: reposo de prueba, clip de acción, dedos recogidos.

### 2026-09-12 — 54.ª tanda: techos medidos otra vez tras la fase 2 — en release la CPU no es el muro
- **Release** (`9dc2d867`; los 2 de CPU en `f9a7814e`): sala aguanta **64** y revienta en 96 por CABLE (2 091 KB/s); emparejados ≥**400** (último
  escalón); repartidos **6 000** al 28 % del tick sin reventar, 1 600 con rosters al 47 %. N=50 juntos: **3,0 ms/ronda** (poses 2,00 + rosters 1,56).
- Sustituye los techos de la 51.ª (22 / ~330 / ~3 500). **Debug miente en CPU**: el mismo arnés daba sala 48 «muro CPU» y 50 juntos al 118 %.
  Techos de CPU sólo con `cargo test --release --bin backrooms_server <arnés> -- --ignored --nocapture --test-threads=1`.
- **Arneses que mienten**: `pose_byte_breakdown` mide el `PlayerUpdate` viejo (65 B), no `PoseWire`; el goteo de chunks sale NO MEDIDO (peer inerte).
- Cadencia media: 50 juntos 9,2 Hz, nave de 100 m 3,4 Hz (el objetivo de 100 ms pide suelo ~15). **Techo de criaturas SIN arnés.**
- Estudio de mundo único / 100 000 jugadores / zonas: artifact `de32d0ff` (claude.ai/code/artifact), sin ADR ni doc en el repo.

### 2026-09-12 — 53.ª tanda: más jugadores por partida — la subida del host de 4 786 a ~320 KB/s con 50 juntos (wire 66)
- **El techo es la SUBIDA del host, no la CPU** (2,7 ms/ronda con 50). Plan P0–P4 de Joel: P0 fusión del lag, P1 cadencia cúbica (ADR-074 enm. 3),
  P2 pose delgada (ADR-144, 23/45 B), aforo por destino + cono ENCENDIDO (enm. 4), P3 rosters por celda con scope 5×5 (enm. 5–6). P4 VPS bloqueado.
- **N=50 juntos**: 4 786 → 612 KB/s (régimen ~320, suelo 5 Hz). **Rosters** (`2eab4fc7`, `eda78987`, enm. 6): N=32 en 1 km², ~1300 → 95,8 KB.
- **Host+joiner reales sin Unity** 6/6 (cliente IPC en Python). **NADA visto en Play** (Steam en wire 63): probar fronteras de celda y remotos a 5 Hz.
- **Objetivo siguiente de Joel: remotos a ≤100 ms con 50 juntos** (suelo ~15 Hz). ADR-146 cubre tramos + extrapolación; SIN ADR: pose por
  diferencias (23 → ~12 B), presupuesto según la línea del host (hoy 192 fijo) y VPS. Tramos + diferencias: ~170 KB/s a 15 Hz.
- **Steam sin `SetLive`**: 25272104 (wire 64) y 25272796 (+ traza RTT). Con wire 66 la próxima pide cliente reconstruido, desde el CLON PRINCIPAL.
- **RTT por peer en `MPTRACE step=RTT`** (Karn: nunca de un reenviado; 0 = sin muestra). **`a_region_is_worth_its_size` FLAKY**: rojo en suite, verde solo.

### 2026-09-12 — 52.ª tanda: la linterna sale de los cofres y el crafteo deja de ser mudo para el servidor (ADR-064 enm. 1)
- **Linterna** (`2d1b0118`): no era bug, era `RestrictCacheCatalog`; entra en la pool de cofres junto al destornillador (~1/15), gate intacto.
- **ADR-064 enm. 1** (`122140bf`): el menú de crafteo YA existía y crafteaba client-local (11 recetas vendor); la puerta es `craft_item`
  fire-and-forget, servidor valida y MUTA `stp_inventory` (el save es correcto antes del `report_inventory`), sin wire. Rust `18867acd`, Unity `531ad943`.
- **Dos trampas medidas**: `Character` mapea componentes con `baseType.Assembly.GetTypes()` (un `ICraftingManagerCC` fuera del ensamblado vendor
  revienta) → fichero AÑADIDO en territorio vendor, fila 8; y `SaveAsPrefabAsset` se niega en `STP_Player.prefab` (script perdido `13af2440…`
  preexistente) → swap por GUID en el texto. Primer espejo C#↔Rust con oráculo JSON común (patrón para B5). `cargo test` 1 502/0, EditMode 32/32.

> **Dos sesiones en paralelo el 10-09** convergen aquí: una atacó el lag de red (42.ª–43.ª abajo, wire acabó en
> **63** con ADR-140), la otra midió y tocó el cliente (44.ª–47.ª). Renumeradas por orden cronológico real.
