# DECISIONS.md — Registro de decisiones de arquitectura (ADR)
> Solo se AÑADE. Nunca se edita ni borra. Para revertir una decisión: nuevo ADR que la sustituye.
> Formato: ADR-NNN | Fecha | Estado (validada/propuesta/sustituida por ADR-XXX)

## ADR-001 — Cliente Unity 6 + URP
Estado: validada. Cliente en Unity 6 con URP. Generación procedural en cliente usa Jobs/Burst donde aplique.

## ADR-002 — Backend en Rust
Estado: validada. Lógica autoritativa de mundo y persistencia en Rust. Async runtime: tokio (salvo ADR posterior).

## ADR-003 — Topología de red
Estado: PROPUESTA, pendiente de validar.
Tensión detectada: el diseño previo era P2P, pero "MMO persistente" exige autoridad y persistencia centralizadas.
Candidata: híbrida — servidor Rust autoritativo (estado de mundo, regiones, persistencia) + gestión de interés/proximidad.
Acción: sesión de auditoría con modelo de máximo nivel ANTES de escribir código de red.

## ADR-004 — Formato de chunk y seams procedurales
Estado: pendiente. Decidir: tamaño de chunk, determinismo de seed, contrato de bordes entre chunks/regiones, versionado del formato serializado.

## ADR-005 — IPC cliente↔servidor (grid_gen → Unity)
Estado: propuesta, pendiente de validar.
Contexto: el render de tiles ya existe; falta el transporte real Rust→Unity (hoy se cargan chunks binarios desde StreamingAssets vía GridTestWorld, sin IPC).
Decidir: protocolo y framing de mensajes, tick rate de envío de chunks/estado, y reparto de autoridad (qué valida el backend vs. el cliente). Reemplaza el camino StreamingAssets de Fase 3.

## ADR-006 — Colisión Rust de celdas Wall (slab fino)
Estado: propuesta, pendiente de validar.
Contexto: el render de muros es un slab fino de 0.2 m (WallThickness) en el borde/centro del tile; la colisión Rust sigue tratando la celda Wall como sólida completa.
Decidir: modelar la colisión de celdas Wall como slab fino de 0.2 m para casar con el render. Hoy no hay desfase porque el render de tiles no añade colliders; bloquea Fase 4 (colisión).

## ADR-007 — Parámetros de generación configurables
Estado: propuesta, pendiente de validar.
Contexto: grid_gen tiene perfiles por capa hardcodeados (densidad de muros, zonas, voids) y LayerHeight fijo a 15 m.
Decidir: exponer densidad de muros por capa, % de conexiones entre capas y LayerHeight revisable. REQUIERE decidir evolución incremental vs. reescritura del backend Rust (grid_gen) antes de tocar código.
Resolución (2026-06-12, commits c1301f6 + bb94833): aprobada (implementada — plumbing completo, cableado al algoritmo diferido). LayerRules serializable + JSON defaults en StreamingAssets; LAYER_HEIGHT_M y GridConstants.LayerHeight = 4 m (invariante con MAX_CEILING_UNITS retirado; MAX_CEILING_UNITS sigue 6, Cell struct/IPC intactos). Campos inter_layer_*/wall_density/corridor_ratio presentes pero sin cablear a generate_layer; load_profiles sin conectar end-to-end (bloqueado por ADR-005 IPC).

## ADR-008 — Render de celdas Pit como hueco vertical (Hollow)
Estado: propuesta
Contexto: el render de tiles agrupa 2×2 celdas; las celdas Pit se clasificaban como tile Open (Floor + Ceiling), tapando el hueco. El backend Rust emite Pit como celdas aisladas de 2.5 m; medición sobre seed 42 (36 chunks): 54 Pit, siempre 1 por tile (nunca en bloque 2×2), 28 en tiles sin Wall.
Decisión: cualquier celda Pit en un tile sin Wall clasifica el tile como Hollow (solo VoidEdge, sin Floor ni Ceiling), abriéndolo en vertical. Wall mantiene prioridad (Pit+Wall → Border); Stair sigue como Open.
Consecuencias / qué prohíbe: a granularidad de tile (5 m) un Pit aislado abre el tile completo → el hueco se ve 4× el Pit real (2.5 m) y borra el suelo de 3 celdas walkable que la colisión Rust sí sostiene. Sin desfase hoy (render-only, sin colliders); en Fase 4+ (colisión) exige render sub-tile del Pit o que el worldgen Rust alinee los Pit a bloques 2×2 de tile. Sustituye el comportamiento "Pit cuenta como Open" del tile system.
Resolución (2026-06-13): aprobada e implementada en GridChunkBuilder.ClassifyTile (umbral pitCount >= 1, reutiliza la rama Hollow) + GridChunkBuilderTests.

## ADR-009 — Protocolo player/stat/world + predicción cliente (migración STP)
Estado: PROPUESTA, pendiente de validar.
Comparte la capa de transporte con ADR-005 (TCP/MessagePack, mismo canal 127.0.0.1:7777).

Contexto: la integración STP exige el sub-protocolo bidireccional jugador↔servidor. Decisión arquitectónica: STP es la capa cliente y conduce la experiencia; Rust posee TODO el estado autoritativo. `PlayerController.cs` (proyecto Backrooms) queda deprecado; `PlayerMovementController` (STP) pasa a ser el sistema de movimiento con predicción cliente.

Decisión:
1. Transporte (sin cambios): se reutiliza IPCClient. Framing = prefijo longitud 4 bytes big-endian + cuerpo MessagePack. Solo se amplía el catálogo de mensajes. Comparte transporte con ADR-005.
2. Catálogo de mensajes
   - Cliente→Servidor: PlayerInputMessage { inputSeq:u32, clientTick:u32, position:[f32;3], velocity:[f32;3], moveState:u8 (idle/walk/run/crouch/jump), look:[f32;2] (pitch,yaw), buttons:u16 }; UseItemMessage { itemId, slot }; CraftRequestMessage { blueprintId }; InteractMessage { targetId, targetKind, interactionType }.
   - Servidor→Cliente: StateUpdateMessage (snapshot completo al unirse); DeltaUpdateMessage (parcial por tick, incluye ackInputSeq); ServerEventMessage (muerte, chunk displacement, etc.).
3. Tick rates (server-driven, NO Unity-frame): movimiento/posición 20 Hz (50 ms); stats (health/hunger/thirst/stamina/sanity) 5 Hz (200 ms); mundo (doors/resources/building) 5 Hz (200 ms); tiempo (day/dayTime) 1 Hz (1000 ms).
4. Reparto de autoridad: el servidor posee TODO el estado autoritativo. El cliente solo predice movimiento; el resto es display interpolado. Ningún MonoBehaviour escribe estado autoritativo localmente.
5. Predicción de movimiento (rollback + replay):
   - Cada frame: input aplicado localmente, sin esperar al servidor.
   - Cada tick (50 ms): se envía PlayerInputMessage y se almacena (input + pose predicha) en un ring buffer circular de 32 ticks (ventana 1.6 s @ 20 Hz), indexado por inputSeq.
   - Al recibir snapshot con ackInputSeq = N: se compara la pose autoritativa en N contra la pose predicha en N (no la actual → absorbe la ventana de latencia ≥ 50 ms).
   - Si |Δ| > 0.15 m: rollback completo a la pose autoritativa en N, seguido de replay de TODOS los inputs almacenados de N+1 hasta el frame actual, en orden, regenerando la pose corregida. La corrección se aplica interpolada sobre 2–3 frames (lerp); asignación directa (transform.position = …) prohibida.
   - Si |Δ| ≤ 0.15 m: sin corrección.
6. Interpolación de stats (5 Hz): cada stat mantiene _targetValue/_maxValue del último snapshot; el display se interpola sobre la ventana de 200 ms y corre ~50 ms por detrás para acotar el valor sin overshoot. Prohibido drenar por deltaTime.
7. Tiempo (1 Hz): day/dayTime se interpolan sobre 1000 ms; DayNightCycle y Ambience consumen el valor interpolado (siguen 100% locales).
8. Look (pitch/yaw) = INPUT, no estado corregido: CharacterLookHandler produce pitch/yaw que viajan como campo look en PlayerInputMessage. NO hay sink L2 para mirada; queda fuera de IMovementCorrectionSink.
9. Sanity: float [0..1] en el bloque de stats a 5 Hz. Consumidor cliente: SanityEffectsController (específico de Backrooms, Step 3). Efectos visuales/audio de alucinación conducidos por el valor interpolado.
10. Fronteras de capa (declaración; reescritura de call sites = Step 3): L2→L3: IMovementCorrectionSink { ApplyAuthoritative(pos, vel, atTick) } (solo posición/velocidad, SIN look); IStatTarget { SetServerTarget(value, max) }; IWorldStateSink { ApplyServerState(state) }. L3→L2: emisores de PlayerInputMessage/UseItem/Craft/Interact. IInventoryWriter: solo se DECLARA la frontera de los 8 call sites.
11. Save: las 6 implementaciones ISaveableComponent (HealthManager, Inventory, CraftingManager, CharacterControllerMotor, PlayerMovementController, CharacterLookHandler) pasan a no-op. Rust es la única fuente de verdad.

Dependencia con ADR-003 (topología de red): asume un servidor único autoritativo. Si ADR-003 se resuelve a topología distribuida, el mecanismo ackInputSeq requiere revisión.

Consecuencias / qué prohíbe:
- Prohíbe que cualquier MonoBehaviour escriba estado autoritativo localmente.
- Prohíbe drenar stats por deltaTime (pasa a interpolación server-tick).
- Prohíbe snap de posición en reconciliación: siempre rollback+replay con corrección interpolada.
- Cambio de wire (breaking): PlayerInputMessage ahora lleva position+velocity+moveState+look, donde el SendInput actual solo mandaba dirección de movimiento. El backend Rust debe parsear el nuevo schema y emitir ackInputSeq. Requiere bump de versión de schema del canal de input.
- Asume latencia mínima de 50 ms; los interpoladores corren por detrás por diseño.
- Requiere inputSeq monotónico en cliente y eco del último inputSeq procesado por el servidor.
- Convive con ADR-005 (transporte de chunks, mismo canal) y desbloquea load_profiles (ADR-007) end-to-end.
- Deprecación: Scripts/Gameplay/PlayerController.cs se borra/stub (conflicto con autoridad STP).

Resolución (2026-06-13): validada. Decisión arquitectónica confirmada por el arquitecto (STP = capa cliente, Rust autoritativo). apply_movement = Opción B: el cliente envía position+velocity, el servidor valida (speed cap = rechaza si |velocity| > maxSpeed + 0.5 m/s de tolerancia; colisión = verifica que la posición no intersecta geometría estática) y devuelve pose autoritativa (clamp si necesario) con ackInputSeq; sin integración física server-side (confía en predicción cliente, recorta outliers). Step 3 desbloqueado en orden A→C→B→C#→D.

Enmienda (2026-06-14): §5 (Predicción de movimiento) SUSTITUIDA. Se retira el "rollback + replay of all buffered inputs". Nuevo modelo — corrección posicional: cuando |Δ| > 0.15 m entre la pose autoritativa y la predicha en el tick ackeado, aplicar delta/3 por frame durante 3 frames vía CharacterController.Move (sin replay). La velocidad se corrige de inmediato al valor autoritativo. Razón: el estado interno de CharacterController (isGrounded, skinWidth, stepOffset, colisión contra colliders Unity vivos) no es snapshotable y un replay completo no es determinista; la corrección posicional suave es aceptable para un juego de supervivencia a 20 Hz. Ring buffer reducido de 32 a 8 ticks (400 ms): ahora solo sirve para DETECTAR desync (comparar pose autoritativa vs. predicha en el tick ackeado), no para replay. (Sustituye §5 y el tamaño de ring buffer declarado en §5 del bloque Decisión; el resto de §5 —predicción local cada frame, envío por tick, umbral 0.15 m, corrección interpolada sobre 2-3 frames sin snap— se mantiene.)

## ADR-010 — Hitreg event-driven con compensación de lag
Estado: PROPUESTA, pendiente de validar. Sistema FUTURO, no implementado.
Decisión:
- Los eventos de disparo (shoot) NO pasan por el game loop de 20 Hz: se procesan inmediatamente al llegar, en un event_loop dedicado.
- Tasa efectiva de hitreg = framerate del cliente (objetivo 120 Hz client-side).
- Requiere un buffer PositionHistory por jugador: VecDeque<(u64, Vec3)>. 200 ms de histórico @ 20 Hz = 60 snapshots totales (15 jugadores), memoria trivial.
- El servidor rebobina el estado al client_timestamp, hace raycast y confirma el impacto.
- El daño se aplica al stat health (visible en el siguiente tick de stats a 5 Hz).
- El game loop (20 Hz) queda sin cambios.
- Schema ShootMessage (a tipar en ipc/mod.rs al implementar): { shooter_id: u32, timestamp: u64, origin: [f32;3], direction: [f32;3], weapon_id: u8 }.
Bloqueado por: diseño del sistema de combate (no iniciado).
No bloquea: nada del roadmap actual.

## ADR-011 — Propagación de animación de acción de proxies vía animation:String (pickup)
Estado: VALIDADA (2026-06-17).
Contexto: los proxies derivan locomoción (MovementSpeed) y jump (velocidad vertical) client-only, sin red. Las animaciones de acción (pickup) no tienen firma cinemática → no son derivables; necesitan una señal explícita. No existe señal lado-proxy: el grant de pickup es unicast al solicitante, sin player_id, y en los demás clientes el item solo desaparece del world_state sin atribución.
Decisión: reutilizar el campo de protocolo existente animation:String (peer→peer, ya plumbed end-to-end) para transportar un FLANCO DE DISPARO de acción transitoria, empezando por "pickup". Sin campo nuevo de schema.
Semántica de la ventana (CRÍTICO — evita acoplar duración cliente/servidor): el valor "pickup" en animation NO representa la duración del gesto. Es un flanco de disparo. La duración real de la animación la controla EXCLUSIVAMENTE el cliente (el clip + el exitTime de la transición Pickup→ del Animator). El backend emite animation="pickup" durante una ventana corta (~1s) con el único fin de que el cliente reciba el flanco de forma fiable pese a pérdida/espaciado de samples (~5Hz). El cliente dispara el trigger UNA vez por edge-detection sobre la transición a "pickup" y NO re-dispara mientras el String siga en "pickup". El ~1s debe cubrir holgadamente el intervalo de sample, no la longitud del clip.
Alcance del cambio Rust: Player.last_pickup_at + sello en process_stp_pickup (rama solicitante) + prioridad en sync.rs::broadcast_player_update (pickup sobre walk/idle/walk_slow durante la ventana, luego vuelve a lógica de movimiento). Recompilar backrooms_server.exe y copiar a Builds/Backend/.
Alcance (una acción a la vez — límite explícito): animation:String es un slot ESCALAR: transporta UN estado a la vez. Soporta una acción transitoria simultánea, no varias. La simultaneidad real de acciones (p.ej. recoger MIENTRAS se camina) se resuelve en el CLIENTE vía capa upper-body del Animator (la locomoción sigue en Base Layer conducida por MovementSpeed, independiente del canal de acción). Pero DOS acciones de capa-alta simultáneas (p.ej. pickup + attack) NO son expresables por este canal y quedan FUERA DE ALCANCE; añadirlas requeriría revisión de este ADR (canal dedicado o bitfield).
Alternativas consideradas:
- (A) Campo nuevo dedicado en el schema (p.ej. action_event). Más explícito y extensible a multi-acción, pero rompe wire-compat (bump de versión) y añade superficie de schema para una sola acción hoy. RECHAZADA por coste/beneficio: el reuso de animation:String cubre el caso actual sin tocar schema.
- (B) Heurística client-only: detectar que un stp_item desaparece dentro de un radio del proxy y disparar pickup en el más cercano. RECHAZADA por frágil: carreras, varios jugadores, despawns por otras causas, atribución errónea.
Invariante: el dominio de animation es un enum abierto de strings de presentación; un receptor que no conozca un valor cae a idle (compat hacia atrás intacta). Tipo y campo de wire sin cambios → sin bump de versión.
Consecuencias / qué prohíbe: el backend deja de ser puramente movimiento-derivado para la animación (ahora sella un evento de gameplay). Un cliente viejo ignora "pickup" sin error. Habilita UNA acción transitoria adicional por el mismo canal sin tocar schema; multi-acción simultánea queda explícitamente FUERA (requiere revisión de este ADR).
Dependencias: comparte transporte con ADR-005/009. No afecta a ADR-003.
Enmienda (2026-06-18) — reconcilia registro↔implementación; acota el campo "Alcance del cambio Rust" a lo realmente construido: (1) el timestamp NO vive en Player sino en NetworkManager.last_pickup_at — está threadeado en los tres puntos (sello host, sello joiner, lectura en broadcast); Player no llega a esos handlers sin cambiar firmas. (2) Hay DOS puntos de sello, no uno: rama del solicitante en process_stp_pickup (host) Y recepción de NetworkEvent::StpPickupGranted (joiner) — sin el segundo, en la topología P2P multi-backend un joiner que recoge no animaría en pantallas ajenas. El resto del Alcance (prioridad "pickup" en broadcast_player_update durante ~1s, sin campo nuevo de schema, recompilar+copiar backrooms_server.exe) se mantiene.

## ADR-012 — AnimatorController custom de proxies (reemplaza el override vendor)
Estado: VALIDADA (2026-06-18, retroactivo — implementado y funcionando).
Contexto: el AnimatorOverrideController vendor (STP_MaleSurvivor) solo intercambia clips; no permite añadir capas ni estados. Su controller base (STP_Template_Human) es vendor → no editable. Para dar a los proxies estados nuevos (Jump, Pickup) hacía falta un controller propio.
Decisión: AnimatorController custom (ProxyLocomotionController) GENERADO por Editor script (Assets/_Migration/STPIntegration/Editor/ProxyAnimatorControllerBuilder.cs), reproducible y versionable. Replica la locomoción vendor (BlendTree 1D-Simple sobre el float MovementSpeed, idle@0/walk@1/run@3 con los clips reales) y añade Jump + Pickup FULL-BODY en la Base Layer (una sola capa, sin máscara). Se asigna al prefab de proxy por RemoteAvatarPrefabBuilder.WireAnimatorController con binding DURABLE (RecordPrefabInstancePropertyModifications + re-binding sobre el asset guardado vía EnsureControllerBound), resolviendo el bug por el que el prefab corría el override vendor (walk/run vacíos → T-pose al moverse).
Alternativa rechazada: Playables API (AnimationLayerMixerPlayable) para superponer clips sin tocar el controller — obligaba a redirigir el SetFloat("MovementSpeed") del feeder de locomoción (tocar la ruta ya funcionando). El controller custom mantiene el feeder intacto.
Consecuencias / qué prohíbe: el proxy ya NO usa la cadena de animación vendor; un update de STP no afecta al controller custom, pero tampoco propaga mejoras del vendor automáticamente. El controller se regenera por menú (Backrooms ▸ Build Remote Avatar Prefab) — NO editar a mano (un rebuild lo sobrescribe). Pickup quedó FULL-BODY (no upper-body): la AvatarMask amputaba el gesto que flexiona piernas para recoger del suelo.
Dependencias: habilita ADR-011 (estado Pickup) y ADR-013 (feeder velocity-derived). Solo capa de presentación (cliente); no toca red ni schema.

## ADR-013 — Animación de proxies derivada de velocidad (locomoción + jump), client-only
Estado: VALIDADA (2026-06-18, retroactivo — implementado y funcionando).
Contexto: los proxies se mueven por writes de Transform desde la red (RemotePlayerManager interpola la pose); no tienen CharacterControllerMotor/PlayerMovementController que calcule velocidad. El parámetro MovementSpeed del BlendTree nunca se escribía → proxy atascado en Idle.
Decisión: alimentar MovementSpeed EXTERNAMENTE, derivado de la velocidad planar reconstruida del delta de la posición interpolada del proxy (deltaXZ/dt), vía MonoBehaviour (Assets/_Migration/STPIntegration/RemoteAvatar/ProxyLocomotionFeeder.cs), mapeado a tiers 0/1/3 con deadzone+SmoothDamp. Jump análogo desde la velocidad VERTICAL (deltaY/dt) con guard de teleport vertical y discriminación rampa/escalera (ProxyJumpFeeder.cs). CERO replicación de animación/velocidad en el packet.
Alternativa rechazada: (A) replicar el estado de animación en el packet — más ancho de banda y acoplado a la frecuencia irregular de red (~5Hz). (B) usar el cálculo nativo de velocidad de STP — inexistente en el proxy (no tiene motor).
Consecuencias / qué prohíbe: la animación se desacopla de la frecuencia de paquetes (corre client-side). La velocidad reconstruida sufre el suavizado del lerp de RemotePlayerManager (positionSmoothing=22) → los umbrales (deadzone/walk/run, jumpVelocityUp) se calibran a ojo en Play, NO en m/s teóricos. Las acciones SIN firma cinemática (p.ej. pickup) NO son derivables y requieren señal explícita (ver ADR-011).
Dependencias: consume el controller custom (ADR-012). Complementa ADR-011. Solo presentación; no toca red ni schema.

(plantilla)
## ADR-NNN — Título
Estado: propuesta
Contexto: …
Decisión: …
Consecuencias / qué prohíbe: …
