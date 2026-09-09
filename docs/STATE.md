# STATE.md — Estado vivo del proyecto
> Escrito por `/checkpoint` al cierre de cada sesión. **Tope: 200 líneas y 20 KB**, ≤ 160 caracteres
> por línea. La única sección que desborda es «Últimas tandas», y su exceso se traslada VERBATIM a
> `docs/SESSION-LOG.md`, donde vive todo el histórico. Aquí sólo lo vigente.

## Estado
- **WorldGen3 es el mundo servido.** Wire **61** en las dos puntas (`ipc/server.rs:38`, `WireSchema.cs:25`) — ADR-129 lo subió el 06-09.
- **Contrato WG3 v1 = Alpha 1** (`docs/WG3-ALPHA1-ROADMAP.md`): días 1–2 hechos; 3 y 5 APARCADOS detrás de la tanda de oficinas (Joel, 06-09).
- Suite del 09-09 (rama ADR-136): `cargo test --bin backrooms_server` **1438/1438 (91 ign.)**, clippy limpio; arnés headless 381/382; CompileCheck 0 ×4.
- **El commit tiene gate** (`tools/dev/validate-scope.ps1`, hook PreToolUse): rojo = el commit no se ejecuta. Alcance por `git diff --cached`.
- Alpha 1 itch nov 2026 · Next Fest feb 2027 · EA primavera 2027 (`docs/SCALING-ROADMAP.md:196-198`). E0 de red cerrada y medida.

## Próximo paso ÚNICO
- **Día 3, rebanada 2: los TRAMOS** (1 179 en la ventana medida, uno por objeto). Arrastran luces, zumbido, ambiente y el nombre `seg_` del
  que dependen el arnés de ADR-098 (`Wg3LiveBootstrap.cs:125`) y el diagnóstico por jerarquía: hay que darles otra vía ANTES de tocarlos.
  Los macizos YA están fundidos (4 718 → **550 renderers**). Del día 3 faltan los otros dos números del contrato: tiempo de chunk y memoria.

## En curso
- **ADR-128, mundo ×2: tercera pasada, NO commiteado.** 24 tests en rojo; `MAX_SEGMENT_M` no se toca (D3 anulado), `bounds()` en metros
  de MUNDO con `plan_bounds()`. **Sigue sin verificarse que lo servido salga ×2.** Parche verbatim en `docs/SESSION-LOG.md`.
- **Contrato WG3 v1**, 5 días. Día 1 (`65d267c3`) y día 2 (`c7c9dd01`, `510223b8`, ADR-129) cerrados; el día 4 de gramática lo sustituyó
  ADR-129 (rampa, tabique diagonal y anti-enfilada pasan a v2 salvo decisión). Quedan día 3 (rendimiento) y día 5 (verificación, etiqueta).
- **Oficinas (33.ª) y B1–B4 FUSIONADOS en `migration/worldgraph-v1`** (06-09). Queda ADR-130 r2b, día 3, r3, r4, B5 (autoridad) y podar ramas (Joel).
- ADR-123 (agacharse y conductos) PROPUESTO, pendiente de Joel. ADR-127 (rampa de techo, wire 61) propuesto para el día 4.
- **Migración STP servidor-autoritativo**: Steps 1–2 y slice 3.1 (plumbing) hechos y verificados. Falta slice 3.2
  (capa L2 de predicción), reescribir los 8 call sites de `Inventory` y retirar `PlayerController.cs` (DEPRECATED por ADR-009).
- ADR-014 fase 2 (borrado diferido 200 ms + reserva host-only anti-duplicado): backend implementado, **pendiente de playtest**.
- **ADR-136 VERIFICADO en Play el 09-09** (build 25217339): invitación, nacer junto al invitador y aviso de entrada, los tres. Sin ver: linterna y venda.

## Riesgos abiertos
- **Autoridad del servidor: los tres agujeros CERRADOS** (`78e156e6` dueño al demoler; `ebb42911`/`bb3c7e5c` cantidad y posición contra el
  roster; aportar material exige dueño y alcance, 5 tests). Queda UNA línea: `process_stp_demolish` valida dueño pero no distancia.
- **Espejos C#↔Rust sin oráculo.** Sin `the_identity_mirror_golden_values` (B4-b), `Wg3Identity.cs` queda verde sin nada que lo
  contraste; igual el hash de `ChunkLootRoll` y los goldens de `scale`/`density`. Un oráculo JSON común es una sesión: **B5**.
- **Steam (ADR-135 enm. 2) VERIFICADO el 09-09 con redes y cuentas distintas: el criterio físico ya se cumple.** Sin probar el relay propio
  (ADR-117 sin VPS), que ya NO bloquea Alpha 1. **`invited_by` sin prueba** (ADR-136 enm. 1 (a)): cualquiera nace a 2 m de una identidad viva.
- **Crafteo P1 sin cerrar**: las recetas consumen un enum Rust de 9 variantes abstractas, no items (ADR-064). Sin esto, minar no sirve.
- **ADR-009 L2 a medias — y el 09-09 se cobró en un playtest real por Steam: «extremadamente lag» (Joel).** Sin predicción/reconciliación
  del CLIENTE (`MovementReconciler` borrado y nunca repuesto) el movimiento espera el ida y vuelta. También: salud al borde y respawn invisible.
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
- **`scale` y `density`**: espejos C#↔Rust con golden values copiados a mano en dos suites, sin oráculo JSON que los ate (`scale.rs:147`, `density.rs:225`).
- **WG3, del contrato para adelante**: relieve de techo sin hacer aunque `height_cm` es por tramo; el dintel (`Wg3Carve`, banda vertical) sin usar;
  salas ≥ 300 m² con una entrada, 31,6 %; catálogo apagado (0,6 piezas/región: las 19 miden el mundo viejo); pozos sin salida — ADR-126 D6, lista v2.
- **Level 0, no bloqueante**: un solo aire (ADR-103 sin consumidor), props decorativos sin colocar, sin señal de planta
  en HUD, fuga de luz entre plantas, claims sin guarda de aislamiento (ADR-110 D4), contenedores host-local, planta −1.
- **Verticalidad jugable** (escalera o hueco entre capas): diferida a post-Alpha 1 con ADR propio; `require_walkable_above`/`_below` sin consumidor.
- **Farmeo y almacenaje**: E4 y Bloque A sin empezar (dos decisiones de Joel); el sync de contenedores construidos pide ADR y bump de wire, 2-3 días.
- **PULIDO CLIENTE-SERVIDOR, sin empezar y ahora el techo real de la experiencia** (Joel, 09-09): la partida por Steam funciona entera y va
  con lag muy alto. **Nada medido**: hacen falta RTT, tick y jitter ANTES de tocar una línea. Primer sospechoso, ADR-009 L2; segundo, el LOD de ADR-074.
- **Atribución de teleports**: `TP_WATCH`/`RESOLVE_DIAG` activos en `game_loop.rs` («REMOVE after diagnosis»); falta playtest y LEER los logs (ADR-026).
- **Entidades PvE (Lurker/Crawler/Shadow) con daño DESACTIVADO** desde 2026-07-07: eran la causa de las muertes silenciosas. Apagadas a propósito.
- **`docs/DECISIONS.md`** (1,38 MB) ilegible entero; se lee por `DECISIONS-INDEX.md` + `grep`. Alternativa sin decidir: un fichero por ADR.
- **`STOREY_HEIGHT_CM` (332) no sube con un número** (380–480: 1/9 regiones válidas); `storey_of_floor_cm` clasifica una planta ABAJO en la costura de 664.
- **Sin ver en juego (06-09)**: monitor y despacho oscuro, recepción, techo roto, decaimiento del cliente en B3 (r2b), audio en Play; carteles SÍ.
- **Venda**: sólo el daño LOCAL abre heridas — el de entidades y robapieles es autoritativo y llega por `SetHealthSilent` sin evento (ADR-025), así
  que hoy no deja herida que vendar; cerrarlo es wire con ADR. Y el estado es VOLÁTIL por alcance (Joel): persistirlo toca el schema de guardado.
- **«Additional Light Shadows» promete más de lo que hace**: las luces de WG3 nacen `DontSave` y `FindObjectsByType` no las ve (ADR-134, «lo que queda fuera»).
- **Audio + gráficos MEZCLADOS de idioma**: gráficos en inglés (ADR-134, decisión Joel), voz en español (ADR-046).
- **El gate de C# miente en un worktree**: `csproj` y `Library` son del clon principal (receta en `docs/DEV-ENVIRONMENT.md`), y el `.csproj`
  es una FOTO: tras tocar un `.asmdef` da falso rojo o falso verde hasta que Unity refresque.
- Menores: `MPTRACE` sin commitear en cuatro ficheros del vendor STP; `TODO(balance)` de loot; doc-comments stale.

## Últimas tandas

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

### 2026-09-07 — 36.ª tanda: la linterna de manivela (ADR-133), de cero a en la mano del vecino, en rama aparte
- Seis commits en `claude/crank-flashlight-model-1d8632` (`5063f09e`…`c5d9b8ad`), SIN fusionar. Carga en `Durability` (como el bote, ADR-068);
  `BR_Battery Health` nueva, sorteada 0,8–1 y persistida. Hereda de `Wieldable`: `FPSWieldablesInput.cs:137` hace `as IUseInputHandler`; un puente no ve `Hold`.
- Parpadeo por `intensity`, NUNCA `enabled` (ADR-042 relaya `light_on` a 10 Hz; ADR-080 detecta por él). Joel: 0,40 velocidad, 18–22 s/vuelta, 60 s × salud,
  ruido 10 m/vuelta (`WorldNoise.CrankLoudness`), bit 6 `RemoteButtons.Cranking`, nace al 10–50 %.
- DOS mallas padre-hijo. Tres fallos cazados por CAPTURA (`BackroomsCrankFlashlightShot.cs`, sin Play): pivote en el POMO (grosor 0,0132/0,0071),
  eje Z que barría por dentro (→ X), separación a ojo (→ derivada de las dos mallas, 0,0438). Mallas 93 k + 97 k tris (24 MB): piden remesh.
- Pickup propio + icono (`tools/dev/MakeItemIcon.py`): el heredado DABA UNA ANTORCHA al recoger. `ProxyCrankHook` en runtime sobre el modelo de mano
  (`ProxyHeldItemHook.cs`), sin rehornear. `BackroomsEditModeFixtureRunner` (Test Runner muerto): 13/13. Loot: no sale hasta levantar `RestrictCacheCatalog`.

### 2026-09-07 — 35.ª tanda: el día 3 — UV al mundo, macizos fundidos y caras enterradas
- **La costura de textura NO la arreglaba el fundido**: la UV arrancaba en (0,0) por cara y la FASE se reiniciaba en cada caja. Ahora
  proyecta la esquina en MUNDO, módulo el periodo (sin él, a 5 km la UV vale 2 500 y el float pierde el milímetro). `UvPerMetre` 0,5 intacto.
- **Fundido por (máscara de planta, estilo, aspecto, loseta) y NO por chunk**: el chunk no se parte en Y y mete 7-8 plantas, y un Renderer
  tiene UNA máscara. Fuera, con test: el invisible de ADR-129 D2 y los prismas de ADR-125. **4 718 macizos → 550 renderers** en Play.
- **Caras enterradas podadas**: ésa es la causa del z-fighting, no el número de mallas. Sólo si OTRA caja cubre la cara ENTERA — un falso
  positivo es un agujero por el que se ve dentro de una pared. Tapar NO es mutuo, y hay test.
- Antes (sonda `probe_solids_per_chunk`): 5 119 macizos y 1 401 tramos en la (0,0), 90 % fundibles. Capturas `perf_*` sin agujeros y con la
  retícula del suelo continua entre cajas. EditMode 38/39: el rojo de `cor_ramp` es PREEXISTENTE (lee volúmenes, no la malla).

### 2026-09-06 — 34.ª tanda: el decaimiento del CLIENTE (ADR-130 r2b) y el cierre de las ramas viejas
- **Los «95/87/79 sin fusionar» eran falsa alarma**: `git cherry` deja 0, 1 y 2 propios. Podadas angry-jackson, gallant-einstein y
  happy-carson (PR #1 cerrado, su fix cae sobre `architecture/` legacy) y awesome-kare, que BORRABA ADR-094 vivo. `nightly-audit-base` dentro.
- **r2b, `5bc25920`, sin wire y sin campo nuevo**: `DecayOfFloor` espeja `fill::decay_of_floor` y sus constantes (3,32 y 3) ya estaban en
  `Wg3StoreyLayers`. Plafones `off + (1−off)·decay·0,80`, parpadeo del 40 % de los vivos, color a su luminancia por `1−0,5·decay`, 45 % de
  luminarias arrancadas (sal `PMIS`, con la COTA en el hash).
- **Mover UMBRALES, no añadir tiradas** (el orden es contrato), y **el gris va DESPUÉS del producto**: el tinte es un cociente en torno a (1,1,1).
- `cargo test` **1414/1414**, `CompileCheck` 4/4, EditMode `Wg3LightCadence` **12/12**, y el barrido del tronco YA fusionado (que nadie
  había medido): 27/27, 4,2 plantas, 268 espacios, mancha 99,7 %, islas 6,0, nav 100 %, pisable 182 857 (−0,2 %). Sin ver en juego.

### 2026-09-06 — 33.ª tanda: las ocho sesiones de oficina, fusionadas en un solo tronco
- Once merges en `migration/worldgraph-v1` (`0394e426`): variantes, materiales, deterioro, sala grande y los incrementos de audio, carteles,
  ADR-131 y decaimiento r2a. Tres sesiones hicieron fast-forward del tronco por su cuenta a mitad: dos merges de vuelta (`5ffdf755`, `0394e426`).
- Reparto final tras los choques: prop kinds carteles 15, variantes 16–20, techo roto 21–22; ADR-105 enm. 19 materiales, 20 techo roto,
  21 planta abierta; ADR-129 enm. 1 carteles, 2 variantes; sales 09 deterioro, 0A lámpara, 0B boquetes, 0C variantes, 0D carteles.
- `fill.rs` se entremezcló dos veces (carteles y deterioro en distinto orden a cada lado): reconstruido aplicando las inserciones ancladas por contexto.
- Dos rojos al juntar sala grande con variantes: `office_variants` se quedaba la planta abierta; el test de bocas medía cuadrados, no franjas.
- Los 6 `.meta` de las fusiones commiteados (`fb72c0aa`). Sin barrido de 27 regiones sobre el tronco fusionado: cada rama midió el suyo.

