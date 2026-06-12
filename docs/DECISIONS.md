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

(plantilla)
## ADR-NNN — Título
Estado: propuesta
Contexto: …
Decisión: …
Consecuencias / qué prohíbe: …
