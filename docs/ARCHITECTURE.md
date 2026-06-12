# ARCHITECTURE.md — Arquitectura validada
> Documento corto (<200 líneas). El detalle fino vive en el código; aquí solo contratos y fronteras.

## Capas
1. Cliente Unity (presentación, predicción local, generación visual de chunks)
2. Protocolo cliente↔servidor (schema versionado — ver ADR-004)
3. Servidor Rust (autoridad: mundo, regiones, entidades, persistencia)
4. Almacenamiento (TBD)

## Sistemas núcleo (cambios requieren ADR + auditoría)
- Generación procedural / chunk displacement / estabilización por tiers
- Red y replicación
- Persistencia de mundo
- Sistema de regiones/niveles

## Contratos entre capas
- (rellenar al validar ADR-003 y ADR-004)

## Presupuestos de rendimiento (objetivos)
- Cliente: 60 fps en hardware medio; generación de chunk < X ms en hilo de jobs (definir X en Sesión 1).
- Servidor: tick rate y nº de jugadores por región: definir tras ADR-003.
