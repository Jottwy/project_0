---
paths:
  - "backend/src/world/wg3/*.rs"
  - "Assets/Scripts/WorldGen3/*.cs"
---

# WorldGen3: lo que ha mordido de verdad

Aplica al tocar la gramática, la geometría o el ráster de WG3. No sustituye a los ADR-095…128 (la
ley); recoge las trampas que ya costaron sesiones y que ningún test grita por su nombre.

## 1. Las unidades mienten si vienes de `grid_gen`

La celda del ráster de WG3 mide **0,5 m**; la de `grid_gen` medía **2,5 m**. Toda constante
heredada cambió de significado sin cambiar de nombre, y esto mordió tres veces en un día. Antes de
reutilizar un número de la generación vieja, comprueba en qué unidad está.

Desde ADR-128 hay además **dos sistemas de unidades**: PLAN (`plan.rs`, `fill.rs`, `route.rs`,
`segment.rs` — ni un número cambia) y MUNDO. La conversión vive en **UN** punto,
`Wg3ServedWorld` (`world.rs`). Escalar «a mano» duplicando constantes es exactamente lo que ese
ADR descarta: son 248 con unidades y los tests fijan invariantes sobre casi todas.

## 2. El mundo servido sale del PLAN, no de `compose_region`

Desde ADR-100 lo que se sirve lo produce `plan_region`. `compose_region` sigue existiendo como
**oráculo de paridad** y no se borra: mide, no sirve.

## 3. Un macizo nuevo necesita una FORMA que ninguna otra tenga

Los tests clasifican los macizos por su forma, no por su nombre: 15 faldón/dintel/arco · 20 pretil ·
25 pilastra · 30 división · 35 parteluz · 40 viga · ≥ 200 pilar. Si el macizo nuevo comparte
dimensiones con otro, los tests de forma dejan de distinguirlos y pasan en verde sobre un mundo
roto.

## 4. Centro local: no lo restes dos veces

`AssembleSolid` (C#) entrega el centro del volumen **ya en coordenadas locales** y
`Wg3MeshBuilder.Build`/`AddColliders` volvían a restarle el origen. Desde wire 50 hasta el
2026-09-04, **ningún** pilar, pretil, viga ni rejilla se dibujó en su sitio: todos apilados en
(0,0), con el ráster del servidor dejando paredes invisibles. Una semana de enmiendas se juzgó con
capturas que no podían enseñarlo. Verificación que lo delata: captura en (3, 0, 14) mirando −Z.

## 5. Antes y después de tocar el plan o el relleno: el barrido

`many_seeds_produce_valid_regions` (`validate_tests.rs:73`) con `WG3_SWEEP_SEEDS=3` da las
**27 regiones**, y `wg3::validate` (`validate.rs`, ADR-118) es la puerta. Se anotan las MEDIAS
(mancha mayor, islas, nav, cotas) antes y después: un cambio de gramática que mejora una región y
empeora otras veintiséis se ve sólo en la media. Ciegos y ensanches cuestan plantas.

`SERVED_SEED` (`tests.rs:1171`) trunca a 32 bits y vale **42** en la práctica: si una sonda no
reproduce lo que ves en juego, empieza por ahí.

## 6. Dos cuentas obligatorias antes de tocar el reparto de tamaños

El número de hojas es **área / objetivo**, no el área de cada clase; y el campo de escala
(`scale_at`, `scale.rs:63`) es un **trapecio**, no una rampa. Hacer las dos cuentas antes de mover
`TARGET_AREA_M2` (`plan.rs:417`) evita el ciclo entero de medir, no entender el resultado y revertir.

## 7. Nada de esto se ve en el editor sin arrancar

`FindObjectsByType` **no** ve lo que lleva `HideFlags.DontSave`, y todo lo que WG3 crea en runtime
lo lleva. Un arnés que barra la escena por tipo sale ciego: hay que bajar por la jerarquía desde el
streamer. Y buscar por GUID en los `.unity` da cero para un componente creado en runtime: eso no
significa que no exista.
