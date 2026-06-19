# STATE.md — Estado vivo del proyecto
> Actualizado por /checkpoint al cierre de cada sesión. Leído al inicio de cada sesión.

## Última sesión
- Fecha: 2026-06-18
- Hecho: Sistema de animación de proxies remotos COMPLETO (locomoción + jump + pickup), 100% client-side salvo el sello de pickup en backend (ADR-011 VALIDADA). Controller custom que reemplaza el AnimatorOverrideController vendor. Build Roslyn 2-etapas (Assembly-CSharp + Assembly-CSharp-Editor) en verde; backend Rust recompilado (GNU) y copiado a Builds/Backend/.

## Próximo paso (UNO solo)
- Play-testear Fase 2 (ADR-014 VALIDADA 2026-06-19, implementada en backend): el item desaparece ~0.20s tras recogerlo (no al instante) y un item reservado rechaza peticiones concurrentes (un solo ganador). Luego commit del backend Fase 2 + el dedup de grants (ADR-011 follow-up). El hito proxy-anim ya está commiteado (proxy-anim-v1).

## En curso / a medias
- Migración STP servidor-autoritativo: Steps 1–2 + Step 3 slice 3.1 (plumbing de protocolo) DONE y verificados. Falta slice 3.2 (capa L2 de predicción) y la reescritura de los 8 call sites de Inventory.
- PlayerController.cs (proyecto Backrooms) marcado DEPRECATED por ADR-009 — pendiente de borrar/stub en slice 3.2 (conflicto con autoridad de movimiento de STP).
- Fase 2 (retraso desaparición item + reserva anti-duplicado): backend IMPLEMENTADO (ADR-014 — pending_pickups host-only, borrado diferido PICKUP_REMOVE_DELAY=200ms, drenado por tick, purga en set_stp_items, rechazo de reservados). Pendiente play-test.

## Estado actual — Sistema de animación de proxies (DONE 2026-06-18)
- **Locomoción:** `RemoteAvatar/ProxyLocomotionFeeder.cs` — escribe `MovementSpeed` derivado de la velocidad planar (delta de transform.position interpolado por RemotePlayerManager / dt), mapeo a tiers 0/1/3 (Idle/Walk/Run) con deadzone+SmoothDamp y guard de teleport XZ. NO toca red.
- **Jump:** `RemoteAvatar/ProxyJumpFeeder.cs` — derivado de velocidad vertical (deltaY/dt) client-side, edge-detection con latch, guard de teleport VERTICAL (chunk displacement/respawn no disparan falso jump) y discriminación rampa/escalera (umbral jumpVelocityUp por encima de la Y sostenida de rampa). Dispara trigger "Jump". NO toca red.
- **Pickup:** FULL-BODY en Base Layer (NO upper-body: la AvatarMask amputaba el gesto que flexiona piernas para recoger del suelo). Señal vía campo `animation:String` desde backend (ADR-011). `RemoteAvatar/ProxyPickupHook.cs` con edge-detection sobre `rp.animation=="pickup"` (latch que distingue null/desconocido de no-pickup → fix del doble-trigger). Estado Pickup con Speed=2 (gesto a ~0.58s). Input lock LOCAL durante el gesto: `RemoteAvatar/LocalPickupInputLock.cs` (self-bootstrap) engancha el evento IPC `stp_pickup_granted` (confirmación local) y usa el `AddStateBlocker` nativo de STP (bloquea Walk/Run/Jump; el motor sigue vivo → PlayerPoseTransmitter sigue emitiendo la pose quieta). Duración del lock horneada del clip (clip.length/PickupSpeed) en `ProxyPickupHook.GestureDuration`, leída del prefab en Resources — NO hardcodeada.
- **Controller:** `RemoteAvatar/ProxyLocomotionController.controller` (custom, reemplaza el AnimatorOverrideController vendor STP_MaleSurvivor), generado por `Editor/ProxyAnimatorControllerBuilder.cs`. Base Layer: Movement (BlendTree 1D-Simple, MovementSpeed, idle@0/walk@1/run@3) + Jump + Pickup, los tres FULL-BODY. Una sola capa, sin UpperBody, sin máscara.
- **Binding durable:** `Editor/RemoteAvatarPrefabBuilder.cs` (menú "Backrooms ▸ Build Remote Avatar Prefab") asigna el controller custom a la variante y lo PERSISTE (RecordPrefabInstancePropertyModifications + re-binding sobre el asset guardado vía EnsureControllerBound). Resuelto el bug por el que el prefab corría el override vendor (walk/run vacíos → T-pose). El builder también cablea, idempotente y durable, los 3 feeders/hook (Wire* mirrors) con sus valores por defecto.
- **Backend (ADR-011):** sello `last_pickup_at` en `NetworkManager` (backend/src/network/mod.rs), sellado en `process_stp_pickup` rama local Y en la rama joiner `StpPickupGranted` (game_loop.rs), y prioridad `animation="pickup"` durante ~1s en `broadcast_player_update` (sync.rs). Sin campo nuevo de schema. `backrooms_server.exe` recompilado (toolchain GNU) y copiado a `Builds/Backend/`.

## Estado actual — Integración STP servidor-autoritativo (Steps 1–2 DONE)
- Decisión de arquitectura: STP es la capa cliente y conduce la experiencia; Rust posee TODO el estado autoritativo. PlayerController.cs (Backrooms) DEPRECATED.
- Step 1 (Audit): 18 MonoBehaviours con estado server-authoritative; ~30 métodos que escriben estado (candidatos a request). 5 fricciones top: Inventory.Add/Remove (8 call sites), drenaje de stats en Update(), conflicto PlayerMovementController↔PlayerController, ISaveableComponent en 6 clases, CraftingManager.OnCraftItemEnd acoplado.
- Step 2 (Capas): L0 Server / L1 Network (IPCClient, existe) / L2 Sync (NEW: MovementReconciler 20Hz, StatInterpolator 5Hz, WorldStateApplier 5Hz, TimeSync 1Hz) / L3 STP Gameplay (predicho/display) / L4 Presentation (local-only). L2 es la ÚNICA capa que escribe en componentes STP. Contratos declarados (sin reescribir call sites todavía).
- ADR-009 (VALIDADA 2026-06-13, §5 ENMENDADA 2026-06-14): protocolo player/stat/world + predicción cliente. Predicción (§5 enmendada): ring buffer 8 ticks/400 ms solo para detectar desync; corrección posicional delta/3 sobre 3 frames vía CharacterController.Move + velocidad inmediata; SIN rollback/replay (estado interno de CharacterController no snapshotable → replay no determinista). Umbral reconciliación 0.15 m, look=input (sin sink L2), sanity 5Hz→SanityEffectsController. Comparte transporte con ADR-005. Dependencia ADR-003 (asume servidor único; si distribuido, revisar ackInputSeq).
- Checklist backend A–E (prereq Step 3): A=schema ipc/mod.rs (extender PlayerInput, +stamina en StatsView, +ack_input_seq); B=game_loop.rs (guardar input_seq, apply_movement Opción B = validar pose cliente + clamp, NO integración física server-side; tick rates 10Hz→20/5/5/1); C=player/stats.rs (+stamina, drenaje sanity 5Hz); D=server.rs (sin cambios funcionales, es transporte; opcional versión de schema en handshake); E=bump schema + build/tests verdes. server.rs queda casi intacto.
- apply_movement = Opción B (decidida): cliente envía position+velocity; servidor valida (speed cap: rechaza si |velocity| > maxSpeed + 0.5 m/s tolerancia; colisión: verifica que la posición no intersecta geometría estática) y devuelve pose autoritativa (clamp si necesario) con ackInputSeq. Confía en la predicción cliente, recorta outliers.
- ADR-009: VALIDADA (2026-06-13). Slice 3.1 (plumbing) DONE: A=ipc/mod.rs (PlayerInput +input_seq/client_tick/position/velocity/move_state/look/buttons serde(default); StatsView +stamina; LocalPlayerState +ack_input_seq). C=stats.rs (PlayerStats +stamina, regen 8/s + use_stamina, drain run 15/s). C#=IPCClient.SendPlayerInput + StatsMsg.stamina + LocalPlayerMsg.ackInputSeq. D=server.rs WIRE_SCHEMA_VERSION=2 (campos serde(default) → v1 interopera). Verificado: cargo check + tests ipc (4/4) verdes; Roslyn C# verde.
- B=game_loop.rs (refactor pre-3.2, 2026-06-14): apply_movement→Option<u32> envuelve apply_client_authoritative_move (Opción B); gate input_seq!=0 ELIMINADO (input_seq ahora 0-based) y path legacy de integración por dirección BORRADO. last_input→received_input + has_received_input; last_accepted_input_seq:u32 (init u32::MAX = "nada aceptado aún"). El ack del snapshot = último input ACEPTADO (None en rechazo por speed cap, no avanza el ack). apply_movement solo corre con has_received_input (evita arrastrar al origen con el PlayerInput default pos=[0,0,0]). CollisionResultKind retirado del import (solo lo usaba el path legacy).
- Slice 3.2 — stream delta 20 Hz (Rust) DONE (2026-06-14): ipc/mod.rs ServerMessage::DeltaUpdate(MovementDelta{tick, ack_input_seq, position, velocity}); tag wire "delta_update". game_loop.rs: MOVEMENT_DELTA_EVERY=3 (20 Hz, 60/3), emite el delta antes del WorldState; trackea authoritative_velocity (= received_input.velocity si aceptado, Vec3::ZERO si rechazado por speed cap). El C# IPCClient ignora "delta_update" sin error (switch sin default) hasta que el reconciler lo consuma. Verificado: cargo test ipc 5/5 (incl. movement_delta_round_trips).
- ADR-010 (PROPUESTA, 2026-06-14): hitreg event-driven con compensación de lag (event_loop fuera del game loop 20 Hz; PositionHistory VecDeque<(u64,Vec3)>; ShootMessage). Sistema FUTURO; bloqueado por diseño de combate; no bloquea nada del roadmap.
- Slice 3.2 — MovementReconciler (C#) DONE (2026-06-14): (1) IPCMessages.cs MovementDeltaMsg + IPCClient case "delta_update" → AddMovementDeltaListener (drenado en Update main-thread). (2) MovementReconciler.cs [DefaultExecutionOrder(100)] (corre tras el motor): envía PlayerInput a 20 Hz con inputSeq 0-based (el reconciler ES el sender), snapshot en ring buffer de 8; on delta busca por ackInputSeq, si |error|>0.15 m arranca corrección posicional error/N por N frames vía motor.Move y motor.SetVelocity inmediata. ackInputSeq==uint.MaxValue → skip. move_state mapeado STP→ADR (STP Walk=2/Run=3 ≠ ADR run=2). (3) CharacterControllerMotor: +Move(Vector3)→CollisionFlags y +SetVelocity(v) (fija _simulatedVelocity+_velocity+_lastPosition=pos, anti-spike); Update() intacto. (4) BackroomsSurvival.asmdef +ref "PolymindGames" (acíclico). Verificado Roslyn: PolymindGames fresco (con edición del motor) 624 src 0 err → BackroomsSurvival 0 err. NOTA metodológica: el csproj correcto es BackroomsSurvival (no Assembly-CSharp); con ediciones a PolymindGames sin recompilar Unity, hay que overridear el DLL stale con uno csc'd fresco (CS0433 si coexisten).
- DECISION (autonomous): MovementReconciler is the sender of PlayerInputMessage at 20Hz. It observes the realized pose from CharacterControllerMotor after each Update() and reports it. PlayerMovementController owns prediction; Reconciler owns reporting.
- Slice 3.2 — StatInterpolator (C#) DONE (2026-06-14): StatInterpolator.cs (BackroomsSurvival.Gameplay) con 4 binders anidados (StatBinder base de buffer 2-muestras: render en now−50 ms, Clamp01 → sin overshoot; mantiene último valor si para la señal). Hunger/Thirst binders: TakeControl→enabled=false + suscriben HealthManager.Respawn → OnRespawn hace SnapTo(max) (jump duro, no SetTarget que rampearía); ReleaseControl→enabled=true + desuscribe. Stamina binder: SetServerControlled(true) (skip drain en Update, conserva setter→StaminaChanged+blocking+audio); escala server/100 (STP stamina es 0..1, server 0..100). Health binder: SetHealthSilent (sin DamageReceived/HealthRestored); Take/Release no-op (health no drena). Toma control con _ipc.IsConnected, libera al desconectar (fallback a drain local). Suscribe AddStateListener → lee state.localPlayer.stats (5 Hz). Sanity FUERA (va a SanityEffectsController). Ediciones STP: StaminaManager +_serverControlled +SetServerControlled(bool) +guard en Update; HealthManager +SetHealthSilent(float). Hooks PÚBLICOS (no internal — cross-assembly BackroomsSurvival↔PolymindGames). Verificado Roslyn: PolymindGames fresco + BackroomsSurvival 0 err.
- Respawn (RESUELTO 2026-06-14): PlayerStats::on_respawn ahora hunger/thirst=100 (antes 50), casando con el SnapTo(max=100) del binder → respawn converge a full en vez de caer a 50. sanity sigue en 50. cargo check verde.
- Connection gate DONE (2026-06-14): GameMode.Start() espera conexión antes de spawnear el player. NO se referencia IPCClient directo (sería ciclo de assemblies: GameMode∈PolymindGames, IPCClient∈BackroomsSurvival que ya referencia PolymindGames). Solución desacoplada: GameBootGate.cs (nuevo, PolymindGames) = static Func<bool> IsReady, default ()=>true; GameMode hace polling sobre GameBootGate.IsReady() con timeout 10 s (offline fallback), preservando el yield original. GameBootGateBinder.cs (nuevo, BackroomsSurvival) = MonoBehaviour scene-scoped que en Awake asigna IsReady=()=>IPCClient conectado y en OnDestroy restaura ()=>true. ATAR a la escena BackroomsWithSTP (no a escenas STP standalone, que conservan default always-ready → spawn inmediato). Verificado Roslyn: PolymindGames + BackroomsSurvival 0 err. (Intento previo de usar IPCClient directo en GameMode falló con CS0103 por el ciclo — confirmado.)
- Slice 3.2 — ISaveableComponent no-op DONE (2026-06-14): las 6 clases (HealthManager, Inventory, CraftingManager, CharacterControllerMotor, PlayerMovementController, CharacterLookHandler) → LoadMembers(){} vacío, SaveMembers()=>null; clases SaveData privadas muertas eliminadas; interfaz y firmas (explícitas vs públicas) intactas. Save STP deshabilitado, Rust única fuente de verdad. NOTA: StaminaManager también implementa ISaveableComponent pero NO estaba en la lista de 6 (su save sigue activo; afinar si se quiere). Verificado: cargo check verde; Roslyn PolymindGames fresco + BackroomsSurvival 0 err.
- Pendiente: slice 3.2 — MovementReconciler/StatInterpolator/TimeSync en C# + borrar PlayerController.cs. Nota: el reconciler debe tratar ack_input_seq==u32::MAX (uint.MaxValue en C#) como "sin ack todavía".

## Estado actual — Sistema de aristas edge-based (DONE)
- Edge-based walls: DONE (capa Unity-only, sin ADR; contrato Rust intacto).
- Todos los tiles tienen suelo y techo garantizado (cero huecos).
- Paredes = paneles 0.2 m en los bordes ENTRE tiles (no bloques). Builder emite solo aristas N/E (regla de no-duplicar: cada arista física se renderiza una vez); S/W las emite el tile/chunk vecino.
- GenerateChunk devuelve ChunkData{cells,walls}; cells salen Corridor/Pillar (sin celdas Wall). Conectividad por BFS + aperturas de seam fijas {1,5,9}.
- ADR-008 (Pit→Hollow) preservado: rama Hollow viva, pero el generador no emite pits.
- Verticalidad: PENDIENTE — se hará vía superestructuras (StructureDefinition sigue como esquema de autoría, sin consumidor en este generador).
- Verificado: build + EditMode tests en verde (Roslyn headless). NO ejecutado en Play (pendiente validación visual: maze sin huecos, paneles finos, seams alineadas).

## Estado actual — Sistema de Tiles 5m (DONE)
- Render migrado a tiles de 5×5 m (2×2 celdas Rust)
- Wall: 5×4×0.2 m, piezas independientes (LEGO), altura uniforme 4 m
- Floor/Ceiling: prefabs separados 5×0.2×5 m, ceiling fijo a 4.04 m
- Pillar 4 m, VoidEdge 5 m
- WallGreedyMesher: DEPRECATED (referencia hasta Fase 5)
- Eliminados: FloorCeiling, Stair, CeilingStep, fascias
- Verificado en Play (a 15 m de capa): sin huecos, sin z-fighting. LayerHeight ahora 4 m por ADR-007 — sin re-Play a esa altura.

## ADR-007 — implementado parcial
- LayerHeight = 4 m en Rust+Unity ✓
- LayerRules serializable + 4 campos nuevos ✓
- JSON defaults en StreamingAssets ✓
- Cableado wall_density/inter_layer al algoritmo: PENDIENTE
- load_profiles no conectado end-to-end hasta ADR-005 IPC

## Deuda conocida
- (OBSOLETO con edge-based) Celda Wall solitaria/diagonal no emite pared — ya no aplica: las paredes son flags de arista, no se derivan de celdas Wall.
- (OBSOLETO con edge-based) Tile Solid en borde de chunk no emite pared exterior — ya no hay tiles Solid; cada chunk emite sus aristas N/E.
- StructureValidator.cs comenta `ProceduralWorldGenerator.TryPlaceStructures` (método retirado en edge-based); el validador sigue funcionando (autónomo, MiniJson). Comentario stale a corregir.
- Archivo WallGreedyMesherTests.cs contiene GridTileClassificationTests (rename diferido)
- Tests Network/RemotePlayer fallan (preexistente, no relacionado con grid)
- Comentarios de código (GridChunkBuilder/WallGreedyMesher/GridTestWorld) y memoria etiquetan el sistema de tiles como "ADR-001"; el ADR-001 real (DECISIONS.md) es Unity+URP. Mal-etiquetado a corregir.
- ADR-011: la implementación divergió del texto del ADR en dos puntos, pendiente de ENMIENDA (DECISIONS.md es append-only, no se edita el ADR; se añade enmienda como en ADR-009): (a) el timestamp se selló en `NetworkManager.last_pickup_at`, NO en `Player` (motivo: NetworkManager está threadeado en los 3 puntos de sello/lectura; Player no llega sin cambiar firmas); (b) se añadió un sello ADICIONAL en la rama del joiner (`NetworkEvent::StpPickupGranted`) además del de `process_stp_pickup`, necesario porque en P2P multi-backend el joiner confirma su propio pickup ahí (sin él, un joiner que recoge no animaría en pantallas ajenas).
- ProxyPickupHook.cs conserva un doc-comment stale que dice "upper-body Pickup" (ahora es full-body) — corregir.

## ADRs pendientes (numeración alineada con DECISIONS.md)
- ADR-003: Topología de red (propuesta, ya en DECISIONS.md) — bloquea persistencia/regiones
- ADR-004: Formato de chunk y seams procedurales (pendiente, ya en DECISIONS.md)
- ADR-005: IPC cliente↔servidor (protocolo, tick rate, autoridad) — bloquea conectar load_profiles end-to-end
- ADR-006: Colisión Rust de celdas Wall (slab fino 0.2 m centrado)
- ADR-009: Protocolo player/stat/world + predicción cliente (migración STP) — VALIDADA (2026-06-13). Comparte transporte con ADR-005. Slice 3.1 (plumbing) implementada y verde; slice 3.2 (predicción L2) pendiente.
- ADR-012: AnimatorController custom de proxies (reemplaza el override vendor) — VALIDADA (2026-06-18), ya en DECISIONS.md.
- ADR-013: animación de proxies velocity-derived (locomoción + jump client-side, cero cambios de red) — VALIDADA (2026-06-18), ya en DECISIONS.md.
- ADR-011: ENMIENDA (2026-06-18) anexada reconciliando registro↔implementación (sello en NetworkManager + rama joiner).

## Decisiones recientes
- Ver docs/DECISIONS.md (ADR-001..007). ADR-007 aprobada (implementada parcial); ADR-005/006 propuestas.

## Riesgos abiertos
- ADR-003 (topología de red) sin validar: bloquea diseño de persistencia y regiones.
- ADR-007: params nuevos sin cablear al algoritmo y load_profiles sin conectar end-to-end (espera ADR-005 IPC).
- Fase 2 implementada (ADR-014 VALIDADA 2026-06-19): borrado diferido ~0.20s + reserva host-only anti-duplicado. Pendiente play-test. (El dedup de StpPickupGranted retransmitido — ADR-011 follow-up — también ya implementado.)
- Fase 3 (aim/look sync — rotación de cámara remota → orientación del proxy; requiere campo de rotación nuevo en el paquete de pose → cambio de schema → ADR) — bloqueada por cambio de protocolo.

## NO tocar
- Modelo de datos Rust (celdas 2.5 m): la conversión celda→tile vive SOLO en Unity (tileX = cellX / 2).
