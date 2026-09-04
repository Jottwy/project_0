# STATE.md — Estado vivo del proyecto
> Escrito por `/checkpoint` al cierre de cada sesión. **Tope: 200 líneas y 20 KB**, ≤ 160 caracteres
> por línea. La única sección que desborda es «Últimas tandas», y su exceso se traslada VERBATIM a
> `docs/SESSION-LOG.md`, donde vive todo el histórico. Aquí sólo lo vigente.

## Estado
- **WorldGen3 es el mundo servido.** Wire **60** en las dos puntas (`ipc/server.rs:38`, `WireSchema.cs:25`).
- **Contrato en marcha: WG3 v1 = Alpha 1** en una semana de cierre (`docs/WG3-ALPHA1-ROADMAP.md`). Día 1 hecho.
- Suite: `cargo test --release --bin backrooms_server` 1377/1377, `world::wg3` 157/157, clippy `-D warnings` y fmt limpios.
- Cliente: `CompileCheckClient` 0 errores en las 4 asambleas. Backend desplegado a `Builds/Backend` (SHA256 16BFA3F1…).
- Alpha 1 itch nov 2026 · Next Fest feb 2027 · EA primavera 2027 (`docs/SCALING-ROADMAP.md:196-198`). E0 de red cerrada y medida.

## Próximo paso ÚNICO
- **Días 1–2 del contrato: materiales del Nivel 0 en el cliente** — techo de placas 60×60, paneles fluorescentes
  60×120 en fila (hoy luminaria puntual, ADR-107), madera, y una pasada a los tintes por rol (Frente A).
- Antes hace falta **la decisión de Joel sobre la madera**: material de decoración para todo (sin wire) o estilo propio (wire 61).

## En curso
- **ADR-128, el mundo a doble escala: núcleo escrito, PROBADO y NO commiteado.** Dos sistemas de unidades (PLAN
  intacto, MUNDO ×2) con la conversión en un solo punto, `Wg3ServedWorld`; `REGION_CHUNKS` 3→6, `MAX_SEGMENT_M`
  25→12,5 de plan; espejos `Wg3Identity.RegionM`/`Wg3LiveBootstrap.RegionMeters` 150→300. Compila y sirve, pero
  **32 tests de `world::wg3` en rojo** (sondan el ráster con constantes del plan) y **dos síntomas que no son de
  test**: 0 celdas de planta alta en la mancha, y un atrio de 5,80 m — lo mismo que antes del ×2, o sea que la
  vertical no escala en todo el camino (sospecha `SLAB_THICKNESS_M` y las alturas en metros de `segment`). Código
  revertido, `Builds/Backend` sano (3A3BA36C); el parche íntegro vive en `scratchpad/scale_patch.py`. Verbatim en
  `docs/SESSION-LOG.md`.
- **Contrato WG3 v1**, 5 días. Día 1 cerrado: el atrio ya no se abre a la nada (ADR-104 enm. 3, `65d267c3`).
  Días 1–2 materiales · día 3 rendimiento (fundido por chunk) · día 4 rampa + tabique diagonal + anti-enfilada · día 5 verificación y etiqueta `wg3-v1-alpha1`.
- **Saneamiento del proyecto** (auditoría del 2026-09-04): **B1 cerrado** (arranque de sesión). Quedan B3 repo
  (ramas y worktrees, pendiente de Joel), B2 docs y gates, B4 código muerto, y los tres agujeros de autoridad.
- **Migración STP servidor-autoritativo**: Steps 1–2 y slice 3.1 (plumbing) hechos y verificados. Falta slice 3.2
  (capa L2 de predicción) y reescribir los 8 call sites de `Inventory`.
- `PlayerController.cs` marcado DEPRECATED por ADR-009: se borra o se deja en stub dentro de la slice 3.2.
- ADR-014 fase 2 (borrado diferido 200 ms + reserva host-only anti-duplicado): backend implementado, **pendiente de playtest**.
- ADR-123 (agacharse y conductos) PROPUESTO, pendiente de Joel. ADR-127 (rampa de techo, wire 61) propuesto para el día 4.

## Riesgos abiertos
- **Tres agujeros de autoridad en el servidor, lo más grave del proyecto hoy.** `process_stp_build_add` y
  `process_stp_demolish` (`backend/src/game_loop.rs:5086-5186`) no comprueban `owner_id` ni distancia;
  `process_stp_harvest_hit` (`:5278-5312`) acepta el `amount` del cliente; `process_authoritative_interaction`
  (`:5399-5443`) se fía de la posición que declara el paquete. Anula el territorio de ADR-081. ~1 día de trabajo.
- **Relay sin VPS**: `DefaultRelayAddress` vacío y nadie ha entrado por relay en internet. Dos jugadores en redes
  distintas hoy NO se juntan (medido en el playtest del 02-09). ADR-117 está en código, no en servicio.
- **Crafteo P1 sin cerrar**: las recetas consumen un enum Rust de 9 variantes abstractas, no items; ADR-064 validada
  con slice 1 diferido y cero efecto en juego. Sin esto, minar no sirve para nada y los tres tiers son texto muerto.
- **ADR-009 L2 a medias**: `MovementReconciler` y el consumidor de `delta_update` se borraron y nunca se
  reemplazaron. Causa raíz compartida de la salud al borde de la muerte y del respawn invisible. Plan con ADR, no parche.
- **Dos mundos de colisión** (histórico, revisar si WG3 lo cierra): el backend colisiona contra su generador y el
  cliente contra lo que streamea. Anotado desde 2026-06-19; con WG3 como autoridad hay que volver a medirlo.
- `handle_spawn_world_chest` (`game_loop.rs`) y `World::spawn_corpse` (`world/corpse.rs`) aceptan la posición del
  cliente sin validar andabilidad. Ausencia total de gate, no el desajuste 2,5/5 m.
- Sin anti-cheat de posesión ni cantidad en `consume_item` (ADR-030): trust-the-client asumido y documentado.
- `IPCClient.cs`: cuatro `catch { }` mudos en los notificadores de listeners — pueden tragar fallos hoy mismo.
- La suite EditMode arrastra rojos conocidos; el del lanzamiento del backend sólo pasa con `BACKROOMS_VERBOSE_LOG=1`.
- **Trabajo en paralelo sin integrar**: `claude/unity-lighting-cadence-bfdd2a` (cadencia de luces, 2 commits, 7 tests
  sin ejecutar) va 51 commits por detrás y está fuera del contrato; y `migration/worldgraph-v1` ya lleva ADR-128
  (doble escala) escrito por otra sesión. Al integrar hay que regenerar `DECISIONS-INDEX.md` o el gate sale en rojo.

## NO tocar
> Detalle completo, verbatim, en `docs/SESSION-LOG.md` (bloques «NO tocar» y «Última sesión» de 2026-08-03).
- **Robapieles, seis invariantes (ADR-038):** `revealed` sin latch; el detector de atasco NO es `MoveResult::blocked`
  sino avance proyectado (el caso real es deslizar contra la pared); alcance de ataque ≠ radio de cuerpo, con
  `segment_is_clear` obligatorio; los rangos de `PhantomTraits` centrados en 1,0 a propósito (test sobre 400 criaturas);
  `vocal_seq` nunca vuelve a 0 y no va en ráfaga; el asesino del agarre lo resuelve el CLIENTE, sin tocar el wire.
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

## Deuda declarada
- **`#![allow(dead_code)]` de crate** (`backend/src/main.rs:23`): enmascara ~137 warnings. Inventario de 2026-08-10
  (112 únicos) caducado. Bajarlo a `#[allow]` por módulo es sesión propia: puede poner `clippy -D warnings` en rojo.
- **Sin consumidores de producción**: `wg3::identity::LevelProfile`/`LevelAnchor`/`ANCHOR_PROFILES`
  (`identity.rs:81-172`) y `SpaceRole::Junction`, que no se asigna nunca.
- **`ChunkRenderer.cs`** (3 925 líneas) inerte desde `GameBootstrap.cs:22`; borrado decidido en
  `docs/AUDIT-2026-08-03.md` y nunca ejecutado. `ChunkLootRoll.cs:14` espeja su hash.
- **`world/volumetric_grid.rs`** (3 702 líneas) sólo vive tras `seed == SHOWCASE_SEED`.
- **`scale` y `density`**: espejos C#↔Rust con golden values copiados a mano en dos suites, sin oráculo JSON que los ate (`scale.rs:147`, `density.rs:225`).
- **Comentarios que mienten**: `MovementReconciler` en `IPCClient.cs:626`, `IPCMessages.Player.cs:70` y `PlayerPoseTransmitter.cs:13` — la clase ya no existe.
- **WG3, del contrato para adelante**: relieve de techo sin hacer aunque `height_cm` es por tramo; el dintel
  (`Wg3Carve` con banda vertical) sigue sin usarse; salas ≥ 300 m² con una sola entrada, 31,6 %; catálogo apagado
  (0,6 piezas por región: las 19 miden para el mundo viejo). Pozos sin salida de la cámara: loot y cuerdas son ADR-126 D6, lista v2.
- **Level 0, no bloqueante**: un solo aire (ADR-103 sin consumidor), props decorativos sin colocar, sin señal de planta
  en HUD, fuga de luz entre plantas, claims sin guarda de aislamiento (ADR-110 D4), contenedores host-local, planta −1.
- **Verticalidad jugable** (cruzar de capa por escalera o hueco): diferida a post-Alpha 1 con ADR propio; nota de
  backlog anclada en `DECISIONS.md`. `require_walkable_above`/`_below` se generan y ningún llamador los consume.
- **Farmeo y almacenaje**: E4 pendiente y Bloque A sin empezar (dos decisiones de Joel); el sync de contenedores
  construidos está diferido y pide ADR nuevo con bump de wire, 2-3 días (`docs/FARMING-ROADMAP.md:451`).
- **Atribución de teleports**: `TP_WATCH`/`RESOLVE_DIAG` siguen activos en `game_loop.rs` marcados «REMOVE after
  diagnosis»; falta un playtest con la instrumentación y LEER los logs. Gate de las partes 1–2 de ADR-026.
- **Entidades PvE (Lurker/Crawler/Shadow) con daño DESACTIVADO** desde 2026-07-07: eran la causa de las muertes
  silenciosas. Implementadas en el backend y apagadas a propósito.
- **`docs/DECISIONS.md`** (1,38 MB) sigue siendo ilegible entero; ya se lee por `DECISIONS-INDEX.md` + `grep`.
  Alternativa sin decidir: partirlo en un fichero por ADR (122 ficheros, ~15 referencias y un hook que rehacer).
- Menores: `MPTRACE` sin commitear en cuatro ficheros del vendor STP; `TODO(balance)` de loot; doc-comments stale.

## Últimas tandas

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

### 2026-09-04 — 18.ª tanda: nivel 1 del catálogo, catorce variaciones sin wire
- Seis commits en `fill.rs`, cada uno con test de forma sobre el ráster servido: medios muros bajo y colgado, laberinto
  en peine, pilastras con zapata y capitel, arcadas y bóvedas, rejillas y ventanas en serie, tarimas y viguetas.
- **Regla que sale de aquí: los tests clasifican los macizos por su FORMA** (15 faldón/dintel/arco, 20 pretil, 25
  pilastra, 30 división, 35 parteluz, 40 viga, ≥ 200 pilar). Todo macizo nuevo necesita una forma que ninguna otra tenga.
- `plan.links` guarda el punto medio de una ruta, no su boca en la pared: `segment_door_points` saca las bocas reales
  y divisiones y pilastras las esquivan.
- Barrido de 27 regiones sin regresión: mancha 99,5 %, islas 1,4, nav 100 %, cotas −0,07 %.
