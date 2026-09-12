# BORRADOR de ADR — Perf Baseline & Budget v1

> **PROPUESTA. No está en `docs/DECISIONS.md` y no se le ha asignado número.** Si Joel lo acepta, se añade con
> **Edit anclado** al final de `DECISIONS.md` (regla dura #11) con el número que toque, y este fichero se borra.

## Contexto

Hasta el 2026-09-10 no existía ninguna medición del CLIENTE en ejecución. `docs/systems/perf-baseline.md` mide el backend
(bytes de rosters, relay de poses, ráster de colisión) y dice literalmente, en «Qué NO se ha medido»: *«Cliente en ejecución:
frame time, drawcalls, GC real. Requiere sesión con Profiler.»* La auditoría de `docs/perf/PERF_AUDIT_v1.md` cubre ese hueco
con un build de desarrollo instrumentado (`FrameTimingManager` + `ProfilerRecorder`), no con estimaciones.

Lo que sale de esa medición, en una frase: **el juego está limitado por el hilo principal de CPU, no por la GPU**, y el
coste no está en la lógica propia (2–5 ms) sino en la EMISIÓN de render de un mundo hecho de decenas de miles de objetos
sueltos.

## Decisiones propuestas

**D1 — La línea base del cliente queda registrada y es reproducible.** Los números de `PERF_AUDIT_v1.md` §1 son la
referencia contra la que se compara cualquier cambio de rendimiento. La sonda (`Assets/_PerfProbe/PerfProbe.cs`) y los
escenarios S1/S2/S3/S5 son el procedimiento; el equipo de medición es el de desarrollo (7800X3D + RX 7900 GRE, 1080p),
declarado en la propia tabla. Una cifra sin run que la respalde no entra en este ADR.

**D2 — Presupuestos por frame, por gama.** Los de `PERF_AUDIT_v1.md` §4. Son OBJETIVO, no invariante: hoy se incumplen
draw calls (12 400 contra 4 000), GC (74 KB contra 16), luces en frustum (297–359 contra 150) y el tirón de streaming
(356 ms contra 33). Sirven para decidir qué se toca, no para poner un test en rojo.

**D3 — Ningún trabajo de rendimiento entra sin un problema que aparezca en la medición.** Corolario aplicado el mismo día:
los **subframes híbridos adaptativos se aplazan** porque la GPU no es el cuello (0–7 % de frames GPU-bound a 1080p), y la
compresión BC7 y el empaquetado de audio se descartan porque no mueven ninguna métrica medida.

**D4 — El orden de ataque para Alpha 1 es el de `PERF_AUDIT_v1.md` §3.2**, con su gate por paso: culling por planta y
sombras (1), fusión de los paneles de techo (2), LOD de lógica de remotos (3), allocs (4), mip streaming y pacing (5),
warmup de shaders si la captura del Profiler lo confirma (6). Ninguno necesita wire ni ADR nuevo, salvo que (3) tenga que
tocar los invariantes del robapieles de ADR-038, en cuyo caso va enmienda.

**D5 — Dos huecos declarados, no tapados.** (a) **No hay equipo de gama baja**: toda la columna de gama baja es
extrapolación con método explícito, y se sustituye por medición en cuanto exista hardware. (b) **El escenario S3 de cuatro
jugadores no se pudo medir entero**: el arnés `RunMultiInstancePlaytest.ps1` levanta los cuatro procesos pero los tres
joiners no entran al mundo (`BACKSTOP: el backend local no confirmó session_joined en 25 s`, `SEND_FAIL
illegal_gameplay_destination kind=relay_as`, y `HEARTBEAT_TIMEOUT` en el joiner). Lo medido de S3 es el host con 56–57
entidades remotas replicadas, que es carga real pero no son cuatro clientes. **Arreglar el arnés es tarea propia.**

## Consecuencias

- `docs/systems/perf-baseline.md` deja de tener el hueco del cliente: se enlaza `docs/perf/PERF_AUDIT_v1.md` desde su
  sección «Qué NO se ha medido».
- Cualquier futura afirmación de «esto va más rápido» se acompaña de un run de la sonda antes/después con el mismo
  escenario, o no se afirma.
- El presupuesto de gama baja condiciona decisiones de contenido: 2 GB de texturas residentes y 12 587 paneles por 3×3
  chunks son decisiones de generación, no de render.

## Preguntas abiertas

- **Q1** — ¿Se aceptan los presupuestos de §4 como objetivo de Alpha 1, sabiendo que hoy se incumplen cuatro de ellos?
- **Q2** — ¿La sonda se queda en el repositorio (`tools/dev/perf/` + `Assets/_PerfProbe/`) como herramienta permanente, o
  se borra al cerrar la auditoría y se re-crea cuando haga falta?
- **Q3** — ¿Arreglar el arnés de cuatro instancias entra antes que las optimizaciones, dado que sin él no hay gate para el
  LOD de lógica de remotos?
