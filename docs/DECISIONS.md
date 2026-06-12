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

(plantilla)
## ADR-NNN — Título
Estado: propuesta
Contexto: …
Decisión: …
Consecuencias / qué prohíbe: …
