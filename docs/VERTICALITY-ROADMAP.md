# Verticality Roadmap — geometrías verticales de WG3

Plan de trabajo, no ADR: aquí se recogen las decisiones ABIERTAS y lo medido.
Cuando una decisión toque protocolo o contradiga un ADR, se abre ADR propio
antes de tocar código (regla 7). Fecha de apertura: 2026-08-29.

## Contexto medido (2026-08-29)

- `REGION_STOREYS = 10` servido desde `ba094f30`. El edificio sube solo hasta
  donde el hueco de escalera encaja: barrido de 49 regiones da 1 planta ×3,
  2 ×11, 3 ×22, 4 ×13. **El tope real hoy es 4**, y la causa es ADR-102 D3:
  la huella de cada planta se queda un lado del espinazo de la de abajo, así
  que decae geométricamente y a la quinta no cabe escalera.
- Coste de `plan_building` + `fill_building`: **microsegundos por región**
  (sonda `probe_ten_storeys_cost`). La altura no es problema de rendimiento.
- El raster es lista de spans `i16` en cm: tope físico ±327 m. Sobra.
- Dos bugs de ≥3 plantas ya cazados y arreglados (aterrizaje partido,
  megapilar sobre la salida). Lección: **todo invariante probado a 2 plantas
  hay que re-probarlo al añadir pasadas** — los tests ahora barren el plan
  SERVIDO (`the_served_storey_count_is_coherent_in_every_region`).

## D1 — ¿Cómo se llega a 8-10 plantas de verdad? (ABIERTA, toca ADR-102 D3)

El estrechamiento actual es LEY (D3: silueta de edificio, no losas apiladas).
Opciones, las dos de 5 líneas:

- **(a) Estrechar menos**: cada planta conserva una fracción mínima (p. ej.
  nunca menos del 60 % de la de abajo) en vez de "un lado del espinazo".
  Diff pequeño en `upper_bounds`, pero cambia la silueta de TODO el mundo y
  contradice la letra de D3 → enmienda a ADR-102.
- **(b) Torres**: el estrechamiento se mantiene, pero donde la huella ya no
  da para planta completa se permite un núcleo pequeño (1-2 espacios +
  escalera) que sigue subiendo. Silueta de "zigurat con torre". Más código
  (un modo de planta mínima en `plan_storey`), no toca la regla general.

Ninguna avanza sin decidir. Ambas sin cambio de wire.

## D2 — Rampas (ABIERTA)

El raster solo conoce cajas alineadas a ejes. Dos maneras:

- **(a) Rampa escalonada**: micro-peldaños de ≤27 cm (`MAX_WALK_STEP_CM` ya
  gobierna). Cero cambio de wire ni de colisión — es una escalera con otra
  huella. Visualmente se lee escalonada de cerca.
- **(b) Rampa de verdad**: primitiva nueva inclinada → wire nuevo, raster
  nuevo, colisión nueva en cliente y servidor. Caro. Solo si (a) se ve mal.

Recomendación: (a) primero, medir con los ojos.

## D3 — Caídas y huecos al vacío (ABIERTA)

Agujeros por el suelo con minicaminos: un paso en falso y caes. Piezas:

1. **Generación**: hoy `atrium_carves` ya abre vacíos de doble altura; esto
   es generalizarlo a N plantas (pozo que atraviesa varias) + dejar bandas
   de suelo estrechas (el "minicamino"). Primitivas actuales bastan
   (`Wg3Carve` ya viaja por wire).
2. **Daño de caída**: existe (el backend resuelve Y); verificar umbrales con
   caídas de >2 plantas.
3. **Criaturas**: `nav::floor_at` ya devuelve `None` sin suelo — no se tiran
   solas. Verificar que la persecución no las empuje al borde (la recta del
   robapieles mide el cuerpo desde `a1e45b09`, debería valer).
4. **Loot/sellado**: columna sin espacio se sella como "vacío terminal" —
   el vacío intencionado NO debe sellarse distinto del actual; revisar
   `AnySpaceInColumn` cuando el pozo atraviese todas las plantas.

## D4 — Conductos de ventilación como entrada (ABIERTA)

Paso bajo (<1,7 m) que obliga a agacharse. La nav de criaturas no modela
postura. Dos maneras:

- **(a) Túnel bajo = solo jugador**: altura 1,1-1,3 m; las criaturas lo
  descartan gratis (su altura de cuerpo no cabe: `headroom` ya lo filtra).
  Refugio jugable sin tocar nav. El agacharse del jugador ya existe en STP.
- **(b) Modelar crouch en nav**: caro, y solo aporta que las criaturas te
  sigan dentro. En contra del terror de "aquí no entran" como mecánica.

Recomendación: (a), y es una decisión de diseño de juego, no técnica.

## D5 — Poblar las plantas altas (✅ HECHO Y MEDIDO — ADR-110 D3, 2026-08-30)

**Estado: 🟡 MEDIDO.** Verde en sondas y suite; **nadie lo ha visto en partida**, así que en los tres
estados de WG3-ROADMAP no es ✅ todavía.

Se hizo en cinco tandas (T2, T1, T3, T4, T5) — detalle y evidencia en
[`PLAN-PLANTAS-ALTAS.md`](PLAN-PLANTAS-ALTAS.md). Suite 1154/1154, clippy `--all-targets -D warnings`
y fmt limpios, **cero wire**.

| | antes | ahora |
|---|---|---|
| Plantas altas pobladas (4 regiones) | **0 de 13** | **6 de 13**, las 13 alcanzables |
| Población planta baja (25 regiones) | 60 | **60 — intacta** |
| Población plantas altas (25 regiones) | 0 | **34** |
| Retirada al subir de planta | desaloja en 3 de 5 | no desaloja |

**El enunciado de esta sección era incompleto y conviene decirlo**, porque es la lección: se creía
que el fallo era «el reparto elige el espacio MÁS BAJO de la columna». Eso era cierto y era la mitad
pequeña. La grande: el reparto indexaba la densidad por la **capa de 4 m de WG2**, que con plantas de
3,32 da capa 1 —densidad CERO— ya en la segunda planta, y satura a 3 de la cuarta en adelante. O sea
que el síntoma real no era «las altas nacen vacías» sino **«subir una planta vaciaba el mundo de
criaturas»**. Lo destapó T0 midiendo antes de tocar nada.

Guardas que se respetaron y siguen verdes: la banda de **±25 %** sobre la población de la planta baja
(ADR-109 D5) y el **8:1** dentro/fuera de oficina (ADR-094 enm. 5).

**Lo que queda abierto de aquí:** la **planta −1** (113 tramos bajo la cota 0 en las cuatro regiones)
queda excluida del reparto a propósito, porque ADR-104 D5 da las plantas bajo rasante como no
implementadas y poblarlas sería inventarse una decisión de diseño. Y sigue pendiente la **máscara de
luz entre plantas** (Rendering Layers, sin empezar), que no la toca ninguna de estas tandas.

## Orden propuesto — REVISADO por ADR-110 (2026-08-30)

1. **D5 — poblar las plantas altas.** Sube a lo primero: D1 ya está hecho
   (torres, hasta 6 plantas servidas), así que hay plantas vacías HOY.
2. D3 — vacíos y caídas: es rareza y perspectiva, la dirección que ADR-110
   D2 acaba de elegir.
3. D2 rampas escalonadas + D4 conductos (independientes, baratos). D4 suma
   perspectiva —mirar desde un conducto— por muy poco.

Orden anterior (D1 → D5 → D3 → D2/D4), superado: D1 se resolvió con torres.
