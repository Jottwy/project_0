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

(plantilla)
## ADR-NNN — Título
Estado: propuesta
Contexto: …
Decisión: …
Consecuencias / qué prohíbe: …
