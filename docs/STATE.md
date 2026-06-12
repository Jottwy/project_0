# STATE.md — Estado vivo del proyecto
> Actualizado por /checkpoint al cierre de cada sesión. Leído al inicio de cada sesión.

## Última sesión
- Fecha: 2026-06-12
- Hecho: Migración del render al sistema de TILES 5 m (4 commits de código + regeneración de prefabs). WallGreedyMesher deprecado. Verificado en Play.

## Próximo paso (UNO solo)
- Fase 4: validar ADR-005 (IPC cliente↔servidor) antes de cablear grid_gen → Unity.

## En curso / a medias
- (vacío)

## Estado actual — Sistema de Tiles 5m (DONE)
- Render migrado a tiles de 5×5 m (2×2 celdas Rust)
- Wall: 5×4×0.2 m, piezas independientes (LEGO), altura uniforme 4 m
- Floor/Ceiling: prefabs separados 5×0.2×5 m, ceiling fijo a 4.04 m
- Pillar 4 m, VoidEdge 5 m
- WallGreedyMesher: DEPRECATED (referencia hasta Fase 5)
- Eliminados: FloorCeiling, Stair, CeilingStep, fascias
- Verificado en Play: sin huecos, sin z-fighting, capas separadas 15 m

## Deuda conocida
- Celda Wall solitaria/diagonal no emite pared (regla: ambas celdas del lado deben ser Wall)
- Tile Solid en borde de chunk no emite pared exterior
- Archivo WallGreedyMesherTests.cs contiene GridTileClassificationTests (rename diferido)
- Tests Network/RemotePlayer fallan (preexistente, no relacionado con grid)
- Comentarios de código (GridChunkBuilder/WallGreedyMesher/GridTestWorld) y memoria etiquetan el sistema de tiles como "ADR-001"; el ADR-001 real (DECISIONS.md) es Unity+URP. Mal-etiquetado a corregir.

## ADRs pendientes (numeración alineada con DECISIONS.md)
- ADR-003: Topología de red (propuesta, ya en DECISIONS.md) — bloquea persistencia/regiones
- ADR-004: Formato de chunk y seams procedurales (pendiente, ya en DECISIONS.md)
- ADR-005: IPC cliente↔servidor (protocolo, tick rate, autoridad)
- ADR-006: Colisión Rust de celdas Wall (slab fino 0.2 m centrado)
- ADR-007: Parámetros de generación configurables (densidad de muros por capa, % conexiones entre capas, LayerHeight revisable) — REQUIERE decidir evolución vs. reescritura del backend Rust

## Decisiones recientes
- Ver docs/DECISIONS.md (ADR-001..007; 005-007 son propuestas nuevas de esta sesión).

## Riesgos abiertos
- ADR-003 (topología de red) sin validar: bloquea diseño de persistencia y regiones.
- ADR-007 sin decidir: evolución incremental vs. reescritura del backend grid_gen condiciona el roadmap.

## NO tocar
- Modelo de datos Rust (celdas 2.5 m): la conversión celda→tile vive SOLO en Unity (tileX = cellX / 2).
