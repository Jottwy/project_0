# STATE.md — Estado vivo del proyecto
> Escrito por `/checkpoint` al cierre de cada sesión. **Tope: 200 líneas y 20 KB**, ≤ 160 caracteres
> por línea. La única sección que desborda es «Últimas tandas», y su exceso se traslada VERBATIM a
> `docs/SESSION-LOG.md`, donde vive todo el histórico. Aquí sólo lo vigente.

## Estado
- **WorldGen3 es el mundo servido.** Wire **61** en las dos puntas (`ipc/server.rs:38`, `WireSchema.cs:25`) — ADR-129 lo subió el 06-09.
- **Contrato WG3 v1 = Alpha 1** (`docs/WG3-ALPHA1-ROADMAP.md`): días 1–2 hechos; 3 y 5 APARCADOS detrás de la tanda de oficinas (Joel, 06-09).
- Suite del 06-09 con las OCHO sesiones de oficina fusionadas: `cargo test --bin backrooms_server` **1414/1414 (89 ign.)**, `CompileCheckClient` 0.
- **El commit tiene gate** (`tools/dev/validate-scope.ps1`, hook PreToolUse): rojo = el commit no se ejecuta. Alcance por `git diff --cached`.
- Alpha 1 itch nov 2026 · Next Fest feb 2027 · EA primavera 2027 (`docs/SCALING-ROADMAP.md:196-198`). E0 de red cerrada y medida.

## Próximo paso ÚNICO
- **Día 3 del contrato, el RENDIMIENTO**: fundido por chunk (una malla por chunk y submalla, colliders combinados); hoy cada macizo es un
  GameObject con su collider y una región lleva ~3 000 (nota desde F0 en `Wg3MeshBuilder`). Medir draw calls, tiempo de construcción y
  memoria ANTES y después. Bloquea r3 (vertical, wire 62) y las 34 plantas. La r2b está HECHA (`5bc25920`), sin ver en juego.

## En curso
- **ADR-128, mundo ×2: tercera pasada, NO commiteado.** 24 tests en rojo; `MAX_SEGMENT_M` no se toca (D3 anulado), `bounds()` en metros
  de MUNDO con `plan_bounds()`. **Sigue sin verificarse que lo servido salga ×2.** Parche verbatim en `docs/SESSION-LOG.md`.
- **Contrato WG3 v1**, 5 días. Día 1 (`65d267c3`) y día 2 (`c7c9dd01`, `510223b8`, ADR-129) cerrados; el día 4 de gramática lo sustituyó
  ADR-129 (rampa, tabique diagonal y anti-enfilada pasan a v2 salvo decisión). Quedan día 3 (rendimiento) y día 5 (verificación, etiqueta).
- **Tanda de oficinas (06-09)**: las ocho sesiones FUSIONADAS en `migration/worldgraph-v1` (33.ª tanda) · queda ADR-130 r2b cliente, día 3, r3, r4.
- **Saneamiento: B1–B4 fusionados en `migration/worldgraph-v1`** (06-09). Queda B5 (cabeza: el agujero de autoridad) y la lista de Joel para podar ramas.
- **Migración STP servidor-autoritativo**: Steps 1–2 y slice 3.1 (plumbing) hechos y verificados. Falta slice 3.2
  (capa L2 de predicción), reescribir los 8 call sites de `Inventory` y retirar `PlayerController.cs` (DEPRECATED por ADR-009).
- ADR-014 fase 2 (borrado diferido 200 ms + reserva host-only anti-duplicado): backend implementado, **pendiente de playtest**.
- ADR-123 (agacharse y conductos) PROPUESTO, pendiente de Joel. ADR-127 (rampa de techo, wire 61) propuesto para el día 4.

## Riesgos abiertos
- **Autoridad del servidor: los tres agujeros CERRADOS** (`78e156e6` dueño al demoler; `ebb42911`/`bb3c7e5c` cantidad y posición contra el
  roster; aportar material exige dueño y alcance, 5 tests). Queda UNA línea: `process_stp_demolish` valida dueño pero no distancia.
- **Espejos C#↔Rust sin oráculo.** Sin `the_identity_mirror_golden_values` (B4-b), `Wg3Identity.cs` queda verde sin nada que lo
  contraste; igual el hash de `ChunkLootRoll` y los goldens de `scale`/`density`. Un oráculo JSON común es una sesión: **B5**.
- **Relay sin VPS**: `DefaultRelayAddress` vacío y nadie ha entrado por relay en internet. Dos jugadores en redes
  distintas hoy NO se juntan (medido en el playtest del 02-09). ADR-117 está en código, no en servicio.
- **Crafteo P1 sin cerrar**: las recetas consumen un enum Rust de 9 variantes abstractas, no items (ADR-064). Sin esto, minar no sirve.
- **ADR-009 L2 a medias**: falta la predicción/reconciliación del CLIENTE (`MovementReconciler` se borró y nunca se
  reemplazó; el `delta_update` sí lo consume `AuthoritativePoseApplier`). Causa raíz de la salud al borde de la muerte y del respawn invisible.
- **Dos mundos de colisión** (histórico): el backend colisiona contra su generador y el cliente contra lo que streamea. Con WG3 hay que remedirlo.
- `handle_spawn_world_chest` (`game_loop.rs`) y `World::spawn_corpse` (`world/corpse.rs`) aceptan la posición del cliente sin validar andabilidad.
- Sin anti-cheat de posesión ni cantidad en `consume_item` (ADR-030): trust-the-client asumido y documentado.
- `IPCClient.cs`: cuatro `catch { }` mudos en los notificadores de listeners — pueden tragar fallos hoy mismo.
- La suite EditMode arrastra rojos conocidos (el del backend pide `BACKROOMS_VERBOSE_LOG=1`), y en cargo `phantom_sprints_after_patience_exceeded`
  **flaquea con la máquina cargada** (06-09): rojo 2 de 5 corridas completas, verde 3/3 aislado. Tiempo real dentro de un test.
- **Auditoría del 02-09, tres ALTO sin corregir** (`AUDIT-2026-08-28.md`): A28-29 un sobre «Relayed» se cree sin comparar el origen UDP con
  el relay (`classify_inbound`); A28-30 descripción UPnP sin tope (`StackOverflowException`); A28-31 `spawnedOnDeplete` nunca vuelve a `false`.
- **Ramas viejas CERRADO** (06-09, 34.ª tanda). Aparcados a propósito por Joel: tres wip del 03-09 con base vieja y rebase pendiente —
  teselado estocástico (`558afb54`), decals de suciedad (`cb61b99c`), sonda de cajas (`1c8237ff`).

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
- **Sin ver en juego (06-09)**: monitor y despacho oscuro, recepción, techo roto, decaimiento del cliente en B3 (r2b), pasada en Play del
  audio (clips sintéticos); los carteles SÍ (espejo cazado). El runner del editor ya NO es deuda: le faltaba FOCO (34.ª tanda).
- Menores: `MPTRACE` sin commitear en cuatro ficheros del vendor STP; `TODO(balance)` de loot; doc-comments stale.

## Últimas tandas

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

### 2026-09-06 — 32.ª tanda: la planta abierta de oficina (ADR-105 enm. 21), y la costura de 664 que destapó
- La sala grande no salía por ARITMÉTICA: el nº de hojas es `área / objetivo`, y agrandar una agranda TODAS las de la zona (ADR-119 D2).
  Se funden DOS hermanas sin banda, una por planta, 300–500 m² y carácter Office: árbol, bandas y candidatos a forjado quedan idénticos.
- Barrido 27 regiones antes → después: **4,2 → 4,2 plantas**, 270 → 268 espacios, mancha 99,7 %, 6,4 islas, nav 100 %, 27/27. Sin wire.
- Prioridades: atrio > sala (si no nacía `Void`); sala > vacío y > mordisco de ADR-120 (380 → 196 m²); pozo > sala, y ahí pierde la marca.
- Va DIÁFANA (la holgura de los cubículos rechazaba 5 de 6 columnas) y los puestos cubren la sala entera con pasillo transversal: 91 salas
  en 209 plantas (44 %), media 418 m², 15–18 puestos. Interruptor del ANTES: `WG3_NO_OPEN_PLAN=1`.
- **Bug de la enm. 18**: la holgura de boca era un CUADRADO y la junta entre tramos hermanos (15 m) tapaba la sala; ahora es una franja.
- **Costura de 664 cerrada**: canto de losa = planta de ARRIBA (de la 1 arriba); y una cama ya no ancla en repisa sin altura libre.
### 2026-09-06 — 31.ª tanda: ADR-130 rebanada 2a — el decaimiento del servidor (ADR-130 enm. 1)
- `decay_of(space)` por la COTA (la calle está en 0) y `d²` contra el fondo SERVIDO; `knobs_of` deja de devolver la fila de `KNOBS` y
  devuelve una COPIA movida por `decayed`: los 22 sitios del relleno decaen sin tocar ni un emisor. Agujeros de forjado 0,26 → 0,80.
- **El atrezo NO decae, y es corrección a D4**: con `props` al 0,55 la (0,0) bajaba de 60 sillas a 43 y los VIGILANTES de ADR-131 se
  quedaban sin sitio donde sentarse. Con 30 sótanos la curva reparte; con 3 manda ADR-131. Los cubículos, igual.
- **Boquetes** (`decay_breaches`): carve de 1–2 m, +30 a +215, `decay·0,5` por pared, sólo entre DOS tramos (fuera hay tierra) y lejos de
  bocas y de lo ya recortado — un boquete sobre una ventana le quita el antepecho y la vuelve puerta; lo cazó su test.
- Barrido 27/27 con **islas 6,4 → 6,0** y nav 100 %; pisable −0,3 %. `fill` 13 → 19 ms (los pares de tramos). Suite **1407/1407**.
- Fuera, declarado: `WEIRD_SPREAD` por profundidad (vive en la subdivisión del plan) y TODO el cliente (r2b), que pisa la rama de materiales.

### 2026-09-06 — 30.ª tanda: el falso techo ROTO (ADR-105 enm. 20), servidor y cliente, sin wire
- `office_decay`, el último emisor del relleno, en las salas con `office_ceiling_cm`: placas caídas (60×60×**4**, decoración
  con giro), baldosa de suelo técnico levantada (60×60×**9**), placas colgando (prop 21) y UNA luminaria descolgada por planta (prop 22).
- Lo que cuelga es prop y no macizo porque `Wg3Solid` sólo gira en Y; y no es prefab porque el pack no trae placa ni luminaria: lo
  construye `AssembleHungDecay` con el material de techo o de luminaria, bisagra en el borde de arriba, 34°/22°. Kind nuevo ≠ wire nuevo.
- Densidad por sala con `SALT_DECAY`, y sube con la profundidad (ADR-130 D4, `d²` contra el fondo SERVIDO): calle 0,25 piezas por sala, B3 1,56.
- Nada frena: decoración y props, así que el barrido sale IDÉNTICO (27/27, pisable 183 756, mancha 99,7 %, 6,4 islas, nav 100 %). Suite 1399/1399.
- Cliente además: decoración de canto ≤ 15 cm pasa de `Casing` a `Decoration`, pintada con el material de techo o de suelo.
- **Sin captura** (el editor estaba ocupado). Sonda `probe_decay_spots`: (0,0) B2 a −6,64 m y B3 a −9,96. Al fusionar, los kinds pasan a 21 y 22.

### 2026-09-06 — 29.ª tanda: ocho sesiones en paralelo; luces, audio y carteles fusionados
- Joel lanzó a la vez un prompt por punto del «10 de 10» de oficina; el reparto de números (ADR, prop kind, grosor, SALT) llegó tarde.
- `2148fce3` luces (`e38570d6`): monitor azul 1 de 5 SIN Light (el hash lleva la cota: sin ella se encendía la columna entera), despacho
  a oscuras por planta y chunk, emergencia verde donde el plafón está muerto. Medido: +16 draw calls, +4 luces, 0 sombras nuevas.
- `67f7833d` audio (`cab689c3`): `OfficeAmbienceDirector`, 6 AudioSource, papel de sala DERIVADO del atrezo + falso techo, sin wire.
- `03e84ae3` carteles (ADR-129 enm. 1): `PROP_SIGN = 15`, variante en 6 bits de `style`, wire 61; atlas 8×8 (`BakeSignAtlas.py`).
- Conflictos: `Wg3SceneAssembler.cs` (unión) y `DECISIONS.md` (ADR-131 y enmiendas antes que la de 129). cargo 1402/1402, CompileCheck 0.
- Para las cuatro que quedan: carteles se queda el 15; variantes 16–20 (`TABLE_LONG..RACK`), decay 21–22; ADR-129 enm. 2 para variantes.

### 2026-09-06 — 28.ª tanda: los VIGILANTES sentados (ADR-131 + enm. 1) — la planta de oficinas, rebanada 2
- `Watcher` (`species` 3) en `game_loop/watcher.rs`: **sin `step`**, sólo reconcile. Nace en las anclas `PROP_CHAIR` de ADR-129 (sitio,
  cota y giro los da el mundo), cap 48 por cercanía, radios 60/90/8. «Sentado» = **bit 5 de `buttons`** (ADR-044), sin bump de wire. El
  despertar mide la cota con `same_level` como la retirada, o una silla del piso de arriba nace y muere cada segundo (lo cazó un test).
- **Enm. 2**: 0,06 por silla daba **3 vigilantes** por región (60 sillas en total) — «no los veo» (Joel); ahora 0,25 + 0,12 por sótano,
  tope 0,70: **35 por región**, 5 en la calle. **Enm. 3**: la cabeza pasa de salto seco a CUELLO con tope y desenrosque por delante
  (medido: cámara a −116°, cabeza clavada en −89°). Lo que viene encima lo decide ADR-132, con sus decisiones ya en memoria.
- Cliente: `FacelingSeated.anim` horneado por script y `ProxySeatedHook` (override del idle, cabeza, respiración). **Enm. 1**: el
  Animator que se posa lo dice la MALLA, y la altura del asiento se MIDE cada fotograma. Capturas `vig5_*` del B3 de (0,0).
