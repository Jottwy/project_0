# BORRADOR de ADR — Subframe Rendering v1 (reproyección híbrida adaptativa)

> **PROPUESTA, y la propuesta es NO HACERLO AHORA.** No está en `docs/DECISIONS.md` ni tiene número. Existe para dejar
> escrito por qué se aparca y qué medición lo reabriría, de modo que la idea no vuelva a discutirse desde cero.

## Contexto

La técnica: dibujar la escena estática a media tasa y reproyectarla con su profundidad, dibujando a tasa completa solo la
capa dinámica (viewmodel, jugadores remotos, robapieles, partículas), con decisión adaptativa por error de reproyección,
late latching de la cámara, guard band, relleno de desoclusión con sesgo de fondo y un multiplicador de iluminación para el
parpadeo fluorescente. Encaja bien con Backrooms sobre el papel: pasillos casi estáticos, cámara que gira más de lo que
traslada, iluminación que cambia poco salvo por el parpadeo.

## Lo que dice la medición (`docs/perf/PERF_AUDIT_v1.md` §1.2)

| | GPU p50 | Hilo principal p50 | Frames con GPU > CPU |
|---|---|---|---|
| S1 quieto | 7,99 ms | 13,90 ms | 3 % |
| S2 sprint | 4,93 ms | 14,51 ms | 1 % |
| S3 host con 56 remotos | 6,26 ms | 24,11 ms | 7 % |
| S2 a renderScale 2,0 (≈4K) | 24,15 ms | 14,15 ms | 84 % |

A 1080p **la GPU está ociosa**: el cuello es la emisión de render en el hilo principal (`FinishFrameRendering` 8–14 ms y
`Semaphore.WaitForSignal` 9–20 ms) sobre 20 147 renderers vivos, de los que 12 587 son paneles de techo sueltos. Los
subframes no recortan eso: la capa estática debe seguir emitiéndose en el frame «real», y el frame «real» sigue costando lo
mismo. Lo que sí lo recorta —fundir esos paneles y cullear por planta— cuesta 2–3 días y está en el plan pre-Alpha.

Y bajar la resolución tampoco es la palanca: a 0,6× píxeles la GPU solo cae de 4,93 a 4,14 ms, porque a esta resolución
domina el coste FIJO (24 caras de mapa de sombra, prepass DepthNormals de SSAO a resolución completa, clustering Forward+ de
300–360 luces). Un subframe reproyectado no evita ese coste fijo en el frame real, y el parpadeo de 300+ luces obliga a
frames reales frecuentes.

## Decisión propuesta

**D1 — APLAZAR.** No se escribe una línea de subframes para Alpha 1 (nov 2026).

**D2 — La condición de reapertura es una medición, no una fecha.** Se vuelve a evaluar cuando se cumplan las dos:
(a) las técnicas de culling por planta, fusión de paneles, tope de luces con sombra y SSAO a media resolución estén dentro y
medidas; (b) un equipo **real** de gama media o baja mida, con la sonda y el escenario S2, **GPU p50 > hilo principal p50**
en más del 50 % de los frames. Hoy no hay ese equipo (§1.6 es extrapolación declarada).

**D3 — Si se reabre, cuatro requisitos previos, todos ausentes hoy**: motion vectors (nadie los pide: sin TAA por defecto ni
motion blur), separación de capas compatible con el `_FOV`/`_FOVEnabled` GLOBAL del viewmodel (ADR-077 enm. 2), historia de
color y profundidad persistente en Render Graph 17 con `RTHandle` propios, y una política de «frame real forzado» para
fogonazos, explosiones y el parpadeo de `LampFlicker`.

**D4 — Como paquete vendible de URP: viable, pero no como subproducto de este proyecto.** Los cuatro acoplamientos que
habría que convertir en puntos de extensión antes de empaquetar: el warp de FOV global del viewmodel de FPSCore, las
máscaras de planta por Rendering Layers de WG3, el parpadeo por `MaterialPropertyBlock` (que además rompe el batching), y
que todo el mundo nace `DontSave` sin estáticos ni motion vectors. Un paquete se vende demostrando ganancia en un proyecto
GPU-bound; este no lo es.

## Consecuencias

- Se libera el mes largo que costaría, y se gasta en lo que la medición sí señala.
- Queda escrito el número que habría que ver para cambiar de idea, con el escenario y la herramienta con que medirlo.

## Preguntas abiertas

- **Q1** — ¿Se acepta el aplazamiento con la condición de reapertura de D2, o Joel quiere una prueba de concepto igualmente?
- **Q2** — Si el interés real es el paquete vendible y no el juego, eso es un proyecto aparte con su propia escena
  GPU-bound: ¿se abre como tal o se descarta?
