# STATE.md — Estado vivo del proyecto
> Actualizado por /checkpoint al cierre de cada sesión. Leído al inicio de cada sesión.

## Última sesión
- Fecha: 2026-06-12
- Hecho: Migración del render al sistema de TILES 5 m (prefabs + código). WallGreedyMesher deprecado. ADR-007 plumbing: LayerRules→JSON configurable, LayerHeight 15→4 m.

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
- Verificado en Play (a 15 m de capa): sin huecos, sin z-fighting. LayerHeight ahora 4 m por ADR-007 — sin re-Play a esa altura.

## ADR-007 — implementado parcial
- LayerHeight = 4 m en Rust+Unity ✓
- LayerRules serializable + 4 campos nuevos ✓
- JSON defaults en StreamingAssets ✓
- Cableado wall_density/inter_layer al algoritmo: PENDIENTE
- load_profiles no conectado end-to-end hasta ADR-005 IPC

## Deuda conocida
- Celda Wall solitaria/diagonal no emite pared (regla: ambas celdas del lado deben ser Wall)
- Tile Solid en borde de chunk no emite pared exterior
- Archivo WallGreedyMesherTests.cs contiene GridTileClassificationTests (rename diferido)
- Tests Network/RemotePlayer fallan (preexistente, no relacionado con grid)
- Comentarios de código (GridChunkBuilder/WallGreedyMesher/GridTestWorld) y memoria etiquetan el sistema de tiles como "ADR-001"; el ADR-001 real (DECISIONS.md) es Unity+URP. Mal-etiquetado a corregir.

## ADRs pendientes (numeración alineada con DECISIONS.md)
- ADR-003: Topología de red (propuesta, ya en DECISIONS.md) — bloquea persistencia/regiones
- ADR-004: Formato de chunk y seams procedurales (pendiente, ya en DECISIONS.md)
- ADR-005: IPC cliente↔servidor (protocolo, tick rate, autoridad) — bloquea conectar load_profiles end-to-end
- ADR-006: Colisión Rust de celdas Wall (slab fino 0.2 m centrado)

## Decisiones recientes
- Ver docs/DECISIONS.md (ADR-001..007). ADR-007 aprobada (implementada parcial); ADR-005/006 propuestas.

## Riesgos abiertos
- ADR-003 (topología de red) sin validar: bloquea diseño de persistencia y regiones.
- ADR-007: params nuevos sin cablear al algoritmo y load_profiles sin conectar end-to-end (espera ADR-005 IPC).

## NO tocar
- Modelo de datos Rust (celdas 2.5 m): la conversión celda→tile vive SOLO en Unity (tileX = cellX / 2).
