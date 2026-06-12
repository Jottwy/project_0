# STATE.md — Estado vivo del proyecto
> Actualizado por /checkpoint al cierre de cada sesión. Leído al inicio de cada sesión.

## Última sesión
- Fecha: 2025-06-12
- Hecho: Cierre mini-fase Paredes Finas. Thin-wall mesher completo con per-cell instances. Fallback chunkMin para alturas coherentes (Fases 1-3 completas).

## Próximo paso (UNO solo)
- Fase 4: ADR colisión Rust (thin-wall slab 0.2 m centrado) + IPC cliente↔backend.

## En curso / a medias
- (vacío)

## Fase 3 cierre: Paredes Finas

**Mini-fase Paredes Finas: DONE**
Commits: f0100d7, b013032, 50119c9, ef8c3c2, e6c6bb2

Hechos:
- WallGreedyMesher: per-cell BuildChunkMesh, WallThickness=0.2m, Inset=1.15m, ComputeWallInsets
- GridChunkBuilder: FloorCeiling en celdas Wall expuestas
- fix: chunkMin fallback para alturas coherentes en layers 1-3

**ADR pendiente Fase 4:** colisión Rust de celdas Wall debe usar slab fino (0.2 m centrado) para casar con render. Requiere ADR antes de implementar colisión.

**Nota futura (Fase 4 ADR):** evaluar si hardcodear altura de muro a 1 unidad fija (5 m) en lugar del fallback chunkMin.

## Decisiones recientes
- Ver docs/DECISIONS.md (ADR-001..004).

## Riesgos abiertos
- ADR-003 (topología de red) sin validar: bloquea diseño de persistencia y regiones.

## NO tocar
- (sistemas validados se listan aquí cuando existan)
