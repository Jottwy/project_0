# WorldGen3 v1 — cierre para Alpha 1

Acordado con Joel el 2026-09-04 (noche), tras la 20.ª tanda: «si le dedicamos otra semana más a
pulir el worldgen3 podríamos cerrarlo… para Alpha 1». **Contrato:** en una semana WG3 queda
CONGELADO como v1 para Alpha 1 — look, rendimiento y verificación en verde, la gramática ya aprobada
dentro — y lo que no cabe va a la lista v2, escrita aquí y no en la cabeza de nadie. Un mundo
procedural de un MMO persistente no se cierra: se congela por versiones.

Punto de partida: wire 60, commits `6de69a71` (marco a 10), `8351a7e4` + `6f8d4d2f` (pozos, ADR-126),
`384f0751` (madera, ADR-105 enm. 16), `522d6fc4` (masas, ADR-105 enm. 17). Nota visual de Joel tras
la tanda: 7/10. Galería de 76 capturas y mapa de pilares/pozos publicados como artifacts.

## Semana de cierre (orden fijo; cada día cierra en verde antes de pasar al siguiente)

### Día 1 — El bug de los techos negros en los atrios
En la galería `w61_53_sala` y `w61_65_paseo` (naves de dos plantas, `clear` 640, vano de atrio
[332, 640]) el techo tiene un trozo negro. Sospechas, por orden: la planta alta alrededor del vano no
se construye a tiempo en la captura (`ChunkIsBuilt` mira sólo el chunk del ojo); el techo a 6,40 no se
emite o no se dibuja; las capas de renderizado por planta (`Wg3StoreyLayers`) recortan la vista. Es un
BUG, no una mejora: sin esto no hay cierre. Reproducir en juego, no sólo en captura.

### Días 1–2 — Materiales del Nivel 0 (cliente)
Lo que más nota da por hora: techo de placas 60×60; paneles fluorescentes 60×120 en fila en vez de la
lámpara puntual de ADR-107 (que es el elemento más reconocible de las diez referencias); **madera**
para tablas y listones (Joel elige: material de decoración = madera para todo, sin wire, o estilo
«madera» sólo para tablas y listones, wire 61); una pasada a los tintes por rol (Frente A) para que el
amarillo domine sin borrar la separación por tono. Todo con captura antes/después.

### Día 3 — Rendimiento
Cada macizo es hoy un GameObject con su collider; una región lleva del orden de 3 000. El fundido por
chunk (una malla por chunk y submalla, colliders combinados) lleva pendiente desde F0 (nota en
`Wg3MeshBuilder`). Medir antes (draw calls, tiempo de construcción de chunk, memoria) y después; sin
número no hay «cerrado».

### Día 4 — Gramática ya aprobada que falta
Rampa (ADR-122, wire 61 si no lo tomó la madera), tabique diagonal (ADR-121 D5.2), anti-enfilada de
puertas (plan sobre `plan.links` respetando `door_fits_in`, `doors_fit_cells` y los mordiscos de
ADR-120; primero la métrica). Cortas, con ADR hecho.

### Día 5 — Verificación de cierre
Barrido de 27 regiones (`many_seeds_produce_valid_regions`, `WG3_SWEEP_SEEDS=3`) con las medias
anotadas frente a `baseline_sweep`; galería de 100 capturas por carácter (los cinco); playtest a dos
jugadores en WG3 con robapieles y facelings encima (cruzar pozos, agacharse NO existe: ADR-123 sigue
propuesto); auditoría con `auditor-arquitectura` contra ARCHITECTURE.md; STATE.md y este documento
actualizados; etiqueta de git `wg3-v1-alpha1`.

## Lo que NO cabe: lista v2 (decidido, no opinable)

- **Verticalidad.** El servidor sirve 10 plantas y sólo 4 son reales (ADR-102 D3); cómo subir es una
  sesión de plan entera (VERTICALITY-ROADMAP D1).
- **Menos pasillos (ADR-124).** Probado tres veces y revertido: `CORRIDOR_DEPTH` 2 rompe el
  enrutador (6/300). Es una sesión de `route.rs`, no una constante.
- **Pozos D6 (ADR-126).** Loot en la cámara, cuerdas o poleas para bajar y subir, luz y sonido del
  fondo. Sin eso el pozo es trampa, no contenido.
- **Catálogo de salas autoradas** apagado por deuda de wire desde ADR-100.
- **Agacharse y conductos (ADR-123)**, propuesto, sin aprobar.
- **Carácter «Nivel 0 puro».** Joel NO lo quiere: el mundo es híbrido y todo suma.

## Reglas de la semana
- Un cambio visual por vez, captura antes/después, y lo que Joel da por bueno no se toca sin avisar.
- Nada nuevo de gramática fuera del día 4. Si aparece una idea buena, va a la lista v2.
- Rojo de `cargo test` es nuevo siempre; el barrido de 27 regiones se corre al cerrar cada día.
