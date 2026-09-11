# STATE.md — Estado vivo del proyecto
> Escrito por `/checkpoint` al cierre de cada sesión. **Tope: 200 líneas y 20 KB**, ≤ 160 caracteres
> por línea. La única sección que desborda es «Últimas tandas», y su exceso se traslada VERBATIM a
> `docs/SESSION-LOG.md`, donde vive todo el histórico. Aquí sólo lo vigente.

## Estado
- **WorldGen3 es el mundo servido.** Wire **63** en las dos puntas (`ipc/server.rs`, `WireSchema.cs`), confirmado en los logs del 11-09.
- **Contrato WG3 v1 = Alpha 1** (`docs/WG3-ALPHA1-ROADMAP.md`): días 1–2 hechos; 3 y 5 APARCADOS detrás de la tanda de oficinas (Joel, 06-09).
- **Multijugador por Steam, punta a punta y SIN LAG** (10-09, build 25230867): **253,8 → 13,9 KB/s**, cola 1,9 s → **0**. Suite 1442/1442.
- **El commit tiene gate** (`tools/dev/validate-scope.ps1`, hook PreToolUse): rojo = el commit no se ejecuta. Alcance por `git diff --cached`.
- Alpha 1 itch nov 2026 · Next Fest feb 2027 · EA primavera 2027 (`docs/SCALING-ROADMAP.md:196-198`). E0 de red cerrada y medida.

## Próximo paso ÚNICO
- **Tramos en vez de poses para lo previsible** (ADR pendiente, bump de wire). Con 16 el relay es el 57 % de 185,6 KB/s y 9 de cada 10 parejas
  son una CRIATURA: la que recorre un pasillo no necesita cadencia sino «de aquí a allí a esta velocidad», y el cliente interpola. **A todos por
  igual** — ADR-074 prohíbe que el filtro distinga la fuente. PVS ya ENCENDIDO (ADR-140, `ad7d3c57`), oculta el 24,3 %.

## En curso
- **ADR-128, mundo ×2: tercera pasada, NO commiteado.** 24 tests en rojo; `MAX_SEGMENT_M` no se toca (D3 anulado), `bounds()` en metros
  de MUNDO con `plan_bounds()`. **Sigue sin verificarse que lo servido salga ×2.** Parche verbatim en `docs/SESSION-LOG.md`.
- **Contrato WG3 v1**, 5 días. Día 1 (`65d267c3`) y día 2 (`c7c9dd01`, `510223b8`, ADR-129) cerrados; el día 4 de gramática lo sustituyó
  ADR-129 (rampa, tabique diagonal y anti-enfilada pasan a v2 salvo decisión). Quedan día 3 (rendimiento) y día 5 (verificación, etiqueta).
- **Oficinas (33.ª) y B1–B4 FUSIONADOS en `migration/worldgraph-v1`** (06-09). Queda ADR-130 r2b, día 3, r3, r4, B5 (autoridad) y podar ramas (Joel).
- ADR-123 (agacharse y conductos) PROPUESTO, pendiente de Joel. ADR-127 (rampa de techo, wire 61) propuesto para el día 4.
- **Migración STP servidor-autoritativo**: Steps 1–2 y slice 3.1 (plumbing) hechos y verificados. Falta slice 3.2
  (capa L2 de predicción), reescribir los 8 call sites de `Inventory` y retirar `PlayerController.cs` (DEPRECATED por ADR-009).
- ADR-014 fase 2 (borrado diferido 200 ms + reserva host-only anti-duplicado): backend implementado, **pendiente de playtest**. Sin ver en Play:
  linterna (ADR-133) y venda (`bb9e3cc1`).

## Riesgos abiertos
- **Autoridad del servidor: los tres agujeros CERRADOS** (`78e156e6` dueño al demoler; `ebb42911`/`bb3c7e5c` cantidad y posición contra el
  roster; aportar material exige dueño y alcance, 5 tests). Queda UNA línea: `process_stp_demolish` valida dueño pero no distancia.
- **Espejos C#↔Rust sin oráculo.** Sin `the_identity_mirror_golden_values` (B4-b), `Wg3Identity.cs` queda verde sin nada que lo
  contraste; igual el hash de `ChunkLootRoll` y los goldens de `scale`/`density`. Un oráculo JSON común es una sesión: **B5**.
- **Steam (ADR-135 enm. 2) VERIFICADO el 09-09 con redes y cuentas distintas: el criterio físico ya se cumple.** Sin probar el relay propio
  (ADR-117 sin VPS), que ya NO bloquea Alpha 1. **`invited_by` sin prueba** (ADR-136 enm. 1 (a)): cualquiera nace a 2 m de una identidad viva.
- **Crafteo P1 sin cerrar**: las recetas consumen un enum Rust de 9 variantes abstractas, no items (ADR-064). Sin esto, minar no sirve.
- **ADR-009 L2 a medias**: `MovementReconciler` sigue borrado, pero **NO era la causa del lag** (RTT real 24 ms, movimiento client-authoritative).
  Lo vivo de ese hueco: salud al borde de la muerte y respawn invisible. **LOD de entidades DESCARTADO con medida**: 0,2 % del tick (10-09).
- **Dos mundos de colisión** (histórico): el backend colisiona contra su generador y el cliente contra lo que streamea. Con WG3 hay que remedirlo.
- `handle_spawn_world_chest` (`game_loop.rs`) y `World::spawn_corpse` (`world/corpse.rs`) aceptan la posición del cliente sin validar andabilidad.
- Sin anti-cheat de posesión ni cantidad en `consume_item` (ADR-030): trust-the-client asumido y documentado.
- `IPCClient.cs`: cuatro `catch { }` mudos en los notificadores de listeners — pueden tragar fallos hoy mismo.
- **EditMode: 1 440 tests, 12 rojos** medidos el 08-09 headless, y los MISMOS sobre la base de la rama (por eso se puede afirmar que son ajenos):
  `Wg3Composer` ×3, `Wg3DensityField` ×2, `Wg3ScaleField`, `StorageRackDisplay` ×2, `Wg3LightCadence`, `IgdProtocol`, `OfficeAmbience`,
  `ZoneAmbienceSet`; +2 saltados de `NetworkInitializer`. En cargo, `phantom_sprints_after_patience_exceeded` **flaquea con la máquina cargada**.
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
- **Dado por bueno por Joel (05/06-09)**: luces 2,7/3,2 con alcance 11/9 m y ambiente cálido plano; techos 300/380/1,15; `UvPerMetre` 0,5; feeder 3P 1,5/4,5.
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
  STP; `TODO(balance)` de loot; doc-comments stale.

## Últimas tandas

### 2026-09-11 — 43.ª tanda: el colapso de 8 jugadores CERRADO, y 16 aguantan (BuildID 25257405, sin `SetLive`)
- **Dos cachés se vaciaban ENTEROS al pasar del tope** (rásteres y REGIONES de WG3), con el mismo comentario justificándolo y la misma premisa
  escrita para un jugador quieto. El de regiones tiraba `plan_region`, el generador. `cre_block` peor tick **4 473 → 401 ms**, bloqueos 84 → 3,
  expulsiones 12 → 0. ADR-106 D3 ya pedía desalojo por distancia; lo escrito era `clear()`.
- **ADR-141** y **ADR-140 ENCENDIDO** (PVS por salas, oculta el 24,3 %). 16 instancias aguantan: 185,6 KB/s, 2 bloqueos, 0 expulsiones.
  **Nadie ha verificado EN JUEGO que el PVS no haga invisible a alguien** — el arnés no renderiza y no puede verlo.
- **ADR-142 enm. 1: su D3 era falso** (cachear el sorteo: 0,43 ms contra 950 del reparto). El relay NO crece con N² sino con las CRIATURAS
  (×1,56 población → ×1,54 tráfico). El arnés no era reproducible: cada instancia se restauraba donde la dejó la corrida anterior (ADR-045).
- `LOOPTRACE`/`SYNCTRACE`/`PVSTRACE` nuevos, en `warn!` (devolver a `info!`). SIN commitear ni verificar: el filtro de la pose del anfitrión.

### 2026-09-10 — 42.ª tanda: de «extremadamente lag» a 13,9 KB/s (detalle completo en `SESSION-LOG.md`)
- **253,8 → 13,9 KB/s (−94,5 %)**, cola **1,9 s → 0**, jitter 70 → 7 ms, enlace del 99 % al **5,4 %**. Partida real por Steam, dos redes.
- **Medir descartó tres optimizaciones «obvias»**: `MovementReconciler` (RTT real 24 ms), LOD de entidades (0,2 % del tick) y el relay de
  poses (8 %). El culpable era `ChunkState`, con el **92 %**, y sólo salió al desglosar el `BWTRACE` por opcode.
- **ADR-137** (wire posicional, pose 246 → 76 B, wire 61→62), **ADR-138** (reproducción diferida con retardo adaptativo + 30 Hz) y
  **ADR-139 D1** (el gate del chunk hashea sólo lo estable: 215 → 12,7 pkt/s, sin tocar el wire).
- Dos bugs de Joel: el que entra nacía en **(0,0,0)**, y los contadores sumaban criaturas como jugadores. Y el arnés de playtest **nunca
  arrancó sin clicks** pese a documentarlo. Suite 1442/1442, CompileCheck 0 ×4, 16 commits, seis builds a Steam.

### 2026-09-09 — 41.ª tanda: la invitación te deja AL LADO de quien te invitó (ADR-136), y el aviso de quién entra
- **VERIFICADO EN PLAY por Joel** (build 25217339, sin `SetLive`): invitación por overlay, nacer junto al invitador y el cartel de entrada. Y el
  precio, dicho por él: «extremadamente lag» — pasa a próximo paso y a Riesgos con nombre.
- **R0, el fallo que habría hundido todo lo demás**: `HandleLobbyEntered` usaba la sobrecarga de TRES argumentos, así que una invitación entraba
  sin relay ni túnel de Steam mientras el navegador sí los pasaba. `LobbyJoinTarget` lee las mismas claves para las dos rutas.
- **R1/R3 sin bump de wire** (ADR-116 D3): `platform_id`/`invited_by` con `serde(default)`; mapa identidad→peer con el host DENTRO (por eso «host
  invita» y «cliente invita» son el mismo código); punto desde el ROSTER (D4); `Invited` gana a `Restored`. El aviso, `PeerDiscovered` por roster.
- **Convergencia y dos trampas**: tronco + luces por planta + ADR-077 fusionados; `DECISIONS.md` chocó dos veces y se resolvió conservando los dos
  apéndices. El `.csproj` no listaba 10 `.cs` nuevos (CompileCheck habría dado falso verde) y `rustfmt` reescribió el WIP de `wg3` (revertido).

### 2026-09-09 — 40.ª tanda: primer playtest real de Steam — redes y cuentas distintas (ADR-135 enm. 2)
- Joel probó con un segundo equipo en OTRA red y OTRA cuenta de Steam: conexión bidireccional, `transport=steam`. Primera vez que el
  criterio físico de ADR-117 D9 (dos jugadores en redes distintas se juntan) se cumple de verdad — la vía Steam lo cierra, no el relay propio.
- Dos bugs del bombeo, cazados en el mismo playtest (`4da8889e`): `Poll()` era `void` y se tragaba su excepción (203 excepciones a máxima
  velocidad tras invalidar Steam el socket); nadie cerraba el túnel con Alt+F4 (`IsBackground`). Arreglo: `Poll()` → `bool`, `Application.quitting`.
- Sin cambios de wire ni de las decisiones D1-D11. `SteamTunnelTests` 23/23; arnés 368/369 (1 rojo preexistente ajeno, `IgdProtocol`). CompileCheck 0×4.

### 2026-09-08 — 38.ª tanda: la venda aplicada — un ESTADO por brazo, no un efecto (`bb9e3cc1`, en tronco)
- No existía nada médico: salud escalar, cero heridas por zona, cero item. `PlayerMedicalState` es objeto plano con singleton estático — un
  MonoBehaviour se lo lleva el rig de STP al reconstruirse. El lado del golpe sale del impacto y luego de la FUERZA, que va al revés.
- **Red gratis**: bits 7/8 de `buttons` (ADR-044), sin campo, sin bump, sin ADR y sin una línea de Rust (`.claude/rules/red-wire-y-autoridad.md` §2).
- **Los brazos de 1P NO son del jugador: cada wieldable trae SU copia del esqueleto** (12 prefabs, `Forearm.*`; el 3P usa `LowerArm.*`), así que el
  hook vigila el wieldable ACTIVO. Y la venda se apaga con la MALLA del brazo: es su propio renderer y flotaría sola al guardar el arma.
- **El primer horneado del avatar BORRÓ el `RealForm`** del robapieles (falta `MeshyImports`, gitignored), con exit 0 y sólo un aviso: se vio como
  −81 líneas de diff. Re-horneado y verificado. Radio de 3P **medido sobre la malla** tras dos estimaciones a ojo fallidas en sentidos opuestos.
  15 tests nuevos verdes, CompileCheck 0 ×4, capturas en los dos rigs; los hooks siguen sin verse en Play.

### 2026-09-08 — 39.ª tanda: menú de calidad gráfica (ADR-134 enmienda 1)
- Seis escalones + Custom + 17 ajustes en la pestaña Graphics; ni DLSS ni raytracing: son HDRP-only, así que la fila es Off/FSR 1.0/STP.
  (`GraphicsQualityPresets.cs`, `BackroomsGraphicsOptionsUI.cs`, `GraphicsOptionsRowsBuilder.cs`): patrón ADR-046.
- BUG: `onValueChanged` con `_writingWidgets` bajada rebota al Ultra. Arreglo:
  `SetValueWithoutNotify`/`SetIsOnWithoutNotify` (`BackroomsGraphicsOptionsUI.cs`), test sin él rojo (1/6).
- ADR-134+1: UN solo pipeline en caliente. CAPACIDADES (soportes) vs PRESUPUESTOS (escala/muestras/dist/atlas).
  Antialiasing por cámara; «sin sombras» = dist 0 (`BackroomsGraphicsApplier.cs`).
- Verificado EN PLAY (STP_Showcase): Very Low (0,6x/1/0 m), Ultra (1,25x/8/120 m), High (1x/2/50 m).
  EditMode headless 26/26 en tres fixtures. CompileCheck 0 ×4.

### 2026-09-07 — 37.ª tanda: la mano al milímetro y la cuerda con la izquierda (ADR-133 enm. 1)
- Joel: «muy arriba, no orgánico», mano «al milímetro» con foto de referencia, y la otra mano girando la manivela. Cuatro clips HORNEADOS por código
  (`BackroomsCrankFlashlightPoseBaker.cs`): idle/equipar/enfundar muestrean la antorcha del FBX del vendor y recolocan el brazo; la cuerda es propia.
- Puño+tubo rígidos bajo `Hand.R`; alabeo BARRIDO por torsión de muñeca ≈ vendor (63° → −65°); IK de dos huesos; los dedos se cierran por CONTACTO
  (primer ángulo que no penetra, dos pasadas, una sola vez): todas las falanges a 7 mm eje-piel. La pila de dedos va 5,4 cm delante del origen de `Torch`.
- Manivela al arco LIBRE de la mano (−88°, izquierda): la órbita del pomo a 9 mm de aire de la derecha. Capa «Crank» en controller copiado del
  `Template_Tool`; el wieldable dibuja la manivela desde la FASE del Animator en `LateUpdate`. Izquierda por IK sobre el pomo, hombro adelantado 64 cm.
- Ocho tests nuevos (`CrankFlashlightAnimationTests`) + 15/15 de item; CompileCheck 0 ×4. Idle de 26 MB → 4,7 (curvas constantes a dos claves).
- Sin ver en Play: `FistFromEye` (0,10, −0,11, 0,36) y lente 8°↓/8°← son diseño; el fundido de la izquierda (0,3 s) y el bamboleo (1,2°) piden ojo.

