# STATE.md — Estado vivo del proyecto
> Escrito por `/checkpoint` al cierre de cada sesión. **Tope: 200 líneas y 20 KB**, ≤ 160 caracteres
> por línea. La única sección que desborda es «Últimas tandas», y su exceso se traslada VERBATIM a
> `docs/SESSION-LOG.md`, donde vive todo el histórico. Aquí sólo lo vigente.

## Estado
- **WorldGen3 es el mundo servido.** Wire **66** en las dos puntas (`ipc/server.rs:38`, `WireSchema.cs:25`) — ADR-074 fase 2 lo subió el 12-09.
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
- **Auditoría del 02-09, tres ALTO sin corregir** (`AUDIT-2026-08-28.md`): A28-29 un sobre «Relayed» se cree sin comparar el origen UDP con
  el relay (`classify_inbound`); A28-30 descripción UPnP sin tope (`StackOverflowException`); A28-31 `spawnedOnDeplete` nunca vuelve a `false`.
- **Ramas viejas CERRADO** (06-09, 34.ª tanda). Aparcados a propósito por Joel: tres wip del 03-09 con base vieja y rebase pendiente —
  teselado estocástico (`558afb54`), decals de suciedad (`cb61b99c`), sonda de cajas (`1c8237ff`).

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
- **`docs/DECISIONS.md`** (1,38 MB) ilegible entero; se lee por `DECISIONS-INDEX.md` + `grep`. Alternativa sin decidir: un fichero por ADR.
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

### 2026-09-13 — 55.ª tanda: manos sobre objetos — `Tools ▸ Interaction Authoring` y API para Claude (ADR-149 PROPUESTA)
- **Horneado, no Animation Rigging**: perfil en espacio del objeto, IK de dos huesos + dedos acoplados, coste de naturalidad barrido.
  API JSON (`tools/dev/HandInteraction.ps1`), puente con editor abierto, CLI headless, ventana. Guía: `docs/systems/hand-interaction.md`.
- **Linterna a dos manos: `NO_NATURAL_GRIP`** (coste 8,7, tope 6): con la derecha centrada en 180 mm no cabe otro puño; no se forzó.
  Ningún asset de juego cambiado; perfil sin hornear en `Assets/Data/HandInteraction/`. EditMode 23/23 con un horneado forzado, revertido.
- **Hallazgo sin tocar**: muñeca derecha del idle de la linterna a 129° antebrazo/metacarpo (destornillador 26°), medido con `pose`.

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

### 2026-09-12 — 51.ª tanda: el relay deja de ser cuadrático, y la animación deja de ser texto (wire 64)
- **Índice espacial** (`7972d207`): el bucle de pares era O(N²) aunque el radio rechazara a todos — preguntar cuesta igual que aceptar. Casillas
  del radio de SALIDA (con el de entrada la histéresis se rompe en silencio). Repartidos **~310 → ~3.500** y lineal; en sala sigue 22 (muro: cable).
- **Tope por destinatario** (`c4452964`) y **cono de atención** (`bae5ffe6`), los dos APAGADOS. El cono da **−35 %, sala 22 → 27**; no se enciende
  hasta que el búfer del cliente mida el ritmo POR PEER (hoy mezclar 30 y 15 Hz cerca lo secaría). Un test exige que siga apagado.
- **ADR-143, wire 64** (`47b0eccb`): animación como byte. Pose **74 → 65 B (12 % de TODAS)** y muere el `clone()` por pose. El cliente reconstruye
  la misma cadena: `ProxyPickupHook` y los tests de EditMode pasan SIN tocarlos. Tres constantes mentían (256 KB/s, y `MPTRACE` decía **⅓** del real).
- **Techos con veredicto**: sala **22**, emparejados **~330**, repartidos **~3.500**. El coste va con los PARES que se ven, no con los jugadores:
  20 en diez parejas cuestan **13× menos** que 20 juntos. Corregidas dos estimaciones mías: en sala el aforo sube por la RAÍZ del ahorro, no en proporción.

### 2026-09-12 — 50.ª tanda: la suite EditMode al día — poses reales en los tests de proxies y la venda que no warpeaba
- **`RemotePlayerManagerTests` 5 rojos → 8/8** (`07fcf69a`): mandaban `position = Vector3.zero` y el gestor lo descarta desde el 10-09 como «peer
  sin pose» (`RemotePlayerManager.cs:280`); ahora `RealPose` (3, 0, 14) y `TheLiteralOriginDoesNotSpawnAProxy` fija la guarda. Gestor intacto.
- **Suite completa headless: 1 528 tests, 12 rojos conocidos, 2 saltados** (`cea357c1`); destapó el 13.º, `ViewmodelWarpTests`: el rollo de la venda
  llevaba `BR_Bandage_Material` (URP/Lit) en la mano — regla 14 rota desde que nació la venda.
- **Venda arreglada** (`31f3e897`): `BR_Bandage_FP_Material` (`LitFieldOfView`, `_SmoothnessIntensity` 0,08) para el rollo y la banda de los brazos 1P;
  `BandageVisual.Attach(firstPerson:)`, menú `Backrooms/Venda/Rewarp venda`, `Rewarp held items` cubre 4. 5/5. El de mundo queda para el proxy.
- **Método**: headless desde el worktree con `Library` en junction funciona con el editor cerrado (reimporta ~1 min, deja 54 `Materials/` del super);
  `-executeMethod` para el rewarp. Pusheado el tronco `07fcf69a..31f3e897` con dos commits de la otra sesión (`d89b21a0`, `ca32d825`).

### 2026-09-12 — 49.ª tanda: saneamiento — el índice estancado del clon, la rama FOV fusionada y el inventario de sesiones
- **Clon principal**: `sync.rs`, `network/tests.rs`, `STATE.md`, `SESSION-LOG.md` y `SERVER_BROWSER.md` ESTACIONADOS en la versión de `9ff790df`
  (la restauración del WIP de la 48.ª): un commit habría revertido `6750e5b0` y la 48.ª. Restaurados a HEAD; el WIP de Unity de Joel (12 ficheros) intacto.
- **`claude/fov-bug-animated-objects-3fe716` fusionada** (ADR-077 enm. 5 `db5c1946`, 13 `.meta` huérfanos, `packages-lock`): conflicto sólo en 4 docs;
  los dos apéndices de `DECISIONS.md` conservados (16 333 + 78 = 16 411 líneas), índice regenerado. Su tanda del 09-09 va VERBATIM a `SESSION-LOG.md`.
- **Sin fusionar y SIN commitear**: `showcase-lighting-broken` (lámparas 3×3, relleno a 1,2 m y 0,30, 5 `.mat`, 54 carpetas `Materials/`) y
  `wf_09042814-13d-4` (crafting sobre base del 08-27). Decisión de Joel. `J:/wg3_*`, `skeptic-boxes` y las tres wip del 03-09 no se tocan.

> **Dos sesiones en paralelo el 10-09** convergen aquí: una atacó el lag de red (42.ª–43.ª abajo, wire acabó en
> **63** con ADR-140), la otra midió y tocó el cliente (44.ª–47.ª). Renumeradas por orden cronológico real.
