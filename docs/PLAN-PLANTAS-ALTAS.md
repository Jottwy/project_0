# PLAN-PLANTAS-ALTAS.md — plan de tandas para poblar las plantas altas

---

## ⚑ T0 EJECUTADA (2026-08-30) — HIPÓTESIS CONFIRMADA, con dos correcciones

> Reproducible con `tools/run-t0-probes.ps1`. **El fichero de medidas de T0 ya no existe**: al volver
> a pasar las sondas tras T5 el mismo día, el script las escribió con el mismo nombre y lo
> sobrescribió (ahora acepta `-Label`, y la medida posterior está en
> [`measurements/T5-plantas-altas-2026-08-30.txt`](measurements/T5-plantas-altas-2026-08-30.txt)).
> Las cifras de T0 son las de esta sección, tomadas antes de tocar producción. Tres sondas `#[ignore]` en `world/wg3/tests.rs`:
> `probe_wg3_storey_to_wg2_layer`, `probe_population_by_storey`, `probe_storey_change_evicts_by_layer`.
> Cero cambios en producción. Suite 1140/1140, clippy `--all-targets -D warnings` y fmt limpios.

**El resultado, en una línea: 13 plantas por encima de la baja en las cuatro regiones auditadas,
las 13 vacías, 932 espacios servidos sin una sola criatura.**

| | Medido |
|---|---|
| Primera planta muda | **la 1** — la segunda planta, la que se anda desde ADR-102 |
| Densidad en esa planta | `0.00` adultos, `0.00` manadas, `0.00` robapieles |
| Plantas por encima de la baja | 13 servidas · **13 vacías · 0 pobladas** |
| Espacios servidos arriba, sin criaturas | **932** |
| Subir de planta desaloja lo de abajo | **3 de 5 transiciones** (0→1, 1→2, 3→4) |

### Corrección 1 — el mecanismo NO era el que decía este plan

Este documento afirmaba que `world_pos_to_layer` era `round((y − PLAYER_BASE_Y) / 4)`. **Es otra
función.** Hay dos con ese nombre y el reparto usa la de `grid_gen/collision.rs:285`:

```rust
pub fn world_pos_to_layer(y: f32) -> u8 {
    let max = LAYER_PROFILES.len().saturating_sub(1) as u8;   // = 3
    ((y / LAYER_HEIGHT_M) as u8).min(max)                     // TRUNCA, y no resta PLAYER_BASE_Y
}
```

La otra —`collision.rs:552`, que sí redondea y sí resta la base— es la del jugador contra la rejilla.
Misma conclusión, distinto camino, y la diferencia importa para T2: **el que hay que sustituir es el
de `grid_gen`, y satura a 3**, así que las plantas 4 a 9 comparten capa y son indistinguibles.

### Corrección 2 — el mundo tiene plantas BAJO la cota 0, y no estaban en ningún inventario

La sonda encontró **una planta −1 en las cuatro regiones**, con 113 tramos entre todas. WG3-ROADMAP
da «plantas bajo la cota base» (ADR-104 D5) como 🔴 NO EXISTE. No es un edificio hacia abajo: son
peldaños y celdas de conector que BAJAN, y ninguno se apoya en la cota −332 (`en_cota = 0` en las
cuatro). Pero es geometría servida, pisable y por debajo de cero.

Y ahí hay un fallo de coordenadas latente que T2 hereda si no se dice: `(y / 4.0) as u8` con `y`
negativa **satura a 0 en Rust**, así que un jugador a −1,52 m se clasifica en la capa 0 igual que uno
en la planta baja. Por eso la primera versión de esta medida dijo «4 plantas altas pobladas» cuando
las pobladas eran cero: eran las −1 repitiendo la población de la baja.

### Corrección 3 — cómo se cuenta una planta (fallo de la propia sonda, cazado y arreglado)

La primera versión agrupó los tramos por `floor_y_cm` distinto y dio **32 «plantas» en la región
(−1,2)**, con cotas de 25, 51 y 76 cm. No son plantas: son los **peldaños** de las escaleras y las
celdas de los conectores que suben desde que ADR-098 enmienda 1 encendió `climb`. La sonda agrupa
ahora por `floor_y_cm.div_euclid(STOREY_HEIGHT_CM)` y publica dos columnas —tramos totales y tramos
**en la cota exacta** de la planta— porque son números distintos y el reparto necesita el segundo.

Queda anotado porque es la regla 7 de WG3-ROADMAP otra vez: **contar colocaciones no es medir el
mundo.** Con el primer agrupado, la decisión de T3 se habría tomado sobre un mundo de 32 plantas que
no existe.

### Lo que esto cambia en T1 y T2

- **T2 sube a ser lo primero que toca código, y T1 deja de bloquearla.** El sorteo devuelve cero
  *antes* de preguntar por ningún espacio, así que `spaces_at_xz` no arregla nada por sí solo: hoy no
  hay ni un hueco sorteado que necesite elegir planta. T1 sigue haciendo falta —para elegir en qué
  planta cae cada hueco— pero es la segunda mitad, no la primera.
- **T2 tiene un objetivo medible que antes no tenía**: las 13 plantas vacías y los 932 espacios. La
  sonda vuelve a pasarse en T6 y se resta.
- **T2 crece con la retirada.** No basta con que el sorteo mire la planta: `!= m.layer` / `!= pack.layer`
  desaloja en 3 de 5 transiciones, así que arreglar sólo el sorteo dejaría a las criaturas naciendo
  arriba y evaporándose al llegar. Es la misma tanda.
- **Y una que no estaba en el plan:** la planta −1 necesita decidirse. Poblarla, o dejarla fuera a
  propósito y decir por qué. Hoy «se puebla» por un desbordamiento a 0, que no es una decisión.

---

> Plan de trabajo, no ADR. La decisión es [ADR-110 D3](DECISIONS.md) (aprobada 2026-08-30) y el
> hueco está anotado desde [ADR-109 D5](DECISIONS.md) y [VERTICALITY-ROADMAP D5](VERTICALITY-ROADMAP.md).
> Escrito antes de tocar código porque toca worldgen y autoridad de criaturas — regla dura 4.
>
> **Cero wire.** Un faceling viaja como peer y su posición ya es un `[f32;3]` cualquiera; poblar una
> planta alta no añade ni un campo. Si alguna tanda acaba pidiendo wire, para y abre ADR (regla 7).

---

## ⚑ AUDITORÍA (2026-08-30) — T2-T5 estaban INCOMPLETAS: tres defectos, tres arreglos

> Pedida al no fiarse de los checks en verde. **La sospecha era correcta**: la suite pasaba y el
> comportamiento vertical seguía roto. `cargo test` 1159/1159, clippy `-D warnings` y fmt limpios.

### Lo que fallaba de verdad

| | Defecto | Evidencia | Estado |
|---|---|---|---|
| **A** | El sitio donde nace una criatura **ignora su planta**: `standable_near` busca en anillos de 24 m con `floor_below`, que **sólo sabe bajar** | 3 de 49 criaturas —y 3 de 13 con el jugador en la planta 2— físicamente en otra planta | corregido |
| **B** | El despertar clasifica por planta DISCRETA y la retirada usaba `same_level`, una distancia CONTINUA. **Dos predicados para la misma pregunta** | discrepan en **6 de 14** peldaños; con el caso construido, un faceling a 8 m en tu misma planta se retiraba | corregido |
| **C** | La planta del JUGADOR salía de `space_at` (el espacio que **contiene el cuerpo**) y la de la CRIATURA del **suelo que pisa**. **Dos reglas distintas** | en un peldaño a 306 cm el jugador daba planta **1** y la criatura del mismo peldaño planta **0** | corregido |

**El defecto C es el que explica por qué los verdes mentían.** Los tests de T2-T5 comprobaban
funciones sueltas (`storey_of_floor_cm`, `spaces_at_xz`, el sorteo) y todas eran correctas por
separado. Lo que nadie medía es que **el jugador y la criatura respondían a «¿en qué planta estás?»
con reglas diferentes** — y eso sólo se ve ejecutando `sync_population` entero.

### Dos correcciones a mí mismo, por si vuelve la pregunta

1. **No hay churn infinito.** Lo supuse al ver el defecto A y es falso: la criatura mal colocada se
   retira una vez y **no vuelve**, porque su chunk sigue en `taken` por sus hermanas vivas. El
   síntoma real es más barato de ver: **aparece delante de ti y desaparece un segundo después**, y
   esa plaza del cap se pierde para siempre.
2. **La métrica por `PeerId` no vale.** Los ids se **reciclan** dentro del mismo tick —un despawn
   libera el id y el siguiente spawn lo reutiliza—, así que «nacidos/retirados» contados por id no
   distinguen «se quedó» de «murió y nació otro en su sitio». La identidad estable es la POSICIÓN.

### Un hallazgo que NO es de este plan

ADR-094 despierta la oficina entera cuando su punto más cercano entra en el radio de activación
(70 m), y un chunk mide 50 m: **algunos miembros nacen a más de los 100 m del radio de
desactivación** y se retiran al tick siguiente. Medido: 2 criaturas a 101,8 m y 108,9 m. Es
preexistente, no tiene que ver con las plantas y no se ha tocado — pero hizo fallar un test de esta
auditoría por un motivo que no era el suyo, y por eso queda escrito.

### Medición del pipeline REAL, planta a planta y región a región

Ejecutando `AdultDriver::sync_population` entero (no el sorteo a mano, que es lo que medían T0-T5):

| región | planta 0 | 1 | 2 | 3 | 4 | en otra planta |
|---|---|---|---|---|---|---|
| (0,0) | 19 | 7 | 12 | 8 | 3 | **0** |
| (1,0) | 30 | 8 | 3 | 3 | 1 | **0** |
| (0,1) | 30 | 9 | 1 | 4 | — | **0** |
| (−1,2) | 12 | 4 | 4 | — | — | **0** |

Y el recorrido vertical 0→1→2→3→0 sobre una misma vertical: las plantas físicas coinciden con las
asignadas en cada paso (`{0:19}`, `{1:9}`, `{2:13}`, `{3:8}`, `{0:19}`), con **cero rotación de
población** con el jugador quieto (antes: 1 retirada por planta).

### Tests de regresión añadidos, todos con dientes comprobados

- `every_creature_is_physically_on_the_storey_it_was_assigned` — falla con A revertido (3 de 49).
- `what_the_draw_populates_the_retirement_must_keep` — falla con B revertido. **El escenario se
  construye a mano**: el sorteo real casi nunca lo produce, porque cerca de una escalera las
  criaturas nacen EN los peldaños, a la altura del jugador. Una primera versión de este test pasaba
  con el fallo puesto y por eso se tiró.
- `a_stationary_player_sees_no_population_churn`
- `the_ground_floor_cannot_monopolise_the_cap_of_an_upper_storey`
- `the_phantom_populates_upper_storeys_on_the_right_floor`

---

## ⚑ T2, T1, T3, T4 y T5 EJECUTADAS (2026-08-30) — el eje, los espacios, la densidad, el robapieles y el cap

> Medida posterior: [`measurements/T5-plantas-altas-2026-08-30.txt`](measurements/T5-plantas-altas-2026-08-30.txt).
> `cargo test` **1154/1154**, clippy `--all-targets -D warnings` y fmt limpios. **Cero wire.**
> T6 queda fuera de esta ejecución.

### El antes y el después, en una tabla

| | T0 (antes) | T5 (ahora) |
|---|---|---|
| Plantas por encima de la baja, pobladas | **0 de 13** | **6 de 13** con población, 13 alcanzables |
| Primera planta muda | la **1** | ninguna por el eje |
| Población planta baja (25 regiones) | 60 | **60 — intacta** |
| Población plantas altas (25 regiones) | 0 | **34** |
| Banda ±25 % contra WG2 (planta baja) | 68 vs 72 | **68 vs 72, verde** |
| Retirada al subir de planta | desaloja en **3 de 5** | no desaloja |
| Población bajo la cota 0 | heredaba la de planta baja | **0, excluida explícitamente** |

### T2 — el eje deja de ser la capa de WG2

`storey_of_floor_cm` (en `wg3::plan`) es el eje nuevo, con `div_euclid` para que las cotas negativas
no caigan en la planta 0. El reparto pregunta por la planta del jugador vía `wg3_player_storey`, que
resuelve por ESPACIO (`space_at`) y sólo cae a la aritmética si no hay espacio bajo el jugador.

**La retirada reutiliza `same_level`, que ya existía**: es el sustituto que ADR-108 creó para
`world_pos_to_layer(x) == layer` y que con WG3 compara cotas contra media planta. No se inventó
ningún mecanismo nuevo — el que hacía falta llevaba escrito desde ADR-108 y la retirada era el único
sitio que se quedó sin migrar.

### T1 — los espacios de la planta correcta

`spaces_at_xz` (ordenado por cota, con desempate determinista) y `space_on_storey_at_xz`. Y la mitad
que faltaba: `wg3_spawn_point` y `wg3_floor_point` seguían llamando a `lowest_space_at_xz`, así que
**con el eje ya arreglado el reparto apuntaba arriba y nacía abajo**. Una manada sorteada para la
planta 2 se repartía por el suelo de la baja.

### T3 — densidad PLANA, y la caída sale de la cobertura

Medido sobre 25 regiones (`probe_storey_coverage_and_population`):

| planta | cobertura | se quedan | por región |
|---|---|---|---|
| 0 | 85,8 % | 60 | 2,40 |
| 1 | 40,7 % | 13 | 0,52 |
| 2 | 21,8 % | 13 | 0,52 |
| 3 | 14,5 % | 8 | 0,32 |
| 4 | 7,4 % | 0 | 0,00 |

**El peso por planta es 1,0 en todas.** La pirámide de población no sale de una constante: sale de que
el edificio se estrecha al subir (ADR-102 D3). La tasa de aceptación por espacio DISPONIBLE se
mantiene entre el 7 y el 17 % en todas las plantas, o sea la misma densidad por metro cuadrado
construido. Añadir además un multiplicador decreciente contaría el estrechamiento dos veces.

**La planta baja no se tocó.** Sigue en 2,40 por región y la banda de ±25 % contra WG2 sigue verde.
Vaciarla para conseguir simetría vertical era el atajo y se descartó.

**Sobre la banda de ±25 %:** no hubo que reinterpretarla. Ya estaba medida sobre el eje 0, o sea la
PLANTA BAJA, así que sigue vigilando exactamente lo que debe. Lo único que se corrigió es que
consultaba `lowest_space_at_xz` —que en las verticales que bajan de cero contesta un peldaño de la
planta −1— y ahora pregunta por la planta 0 explícitamente.

### T4 — el robapieles, que no es una copia del faceling

Mismo eje y misma retirada, pero **constante de densidad propia** (`wg3_storey_phantom_density`) para
que una medida no se pueda atribuir al cambio equivocado, y su métrica es otra: es UNO, no una
población, así que no se le compara contra ninguna banda.

**Y un fallo que sólo tenía él: Level 4.** La reserva vive en la **capa 0 de WG2**
(`level4::REGION_LAYER`), así que pasarle la **planta 0** de WG3 hacía que `block_is_in_region`
contestara que sí, y `level4_spot_is_usable` exigiera entonces que el punto cayera dentro de una
geometría que el mundo servido **no contiene** (ADR-109 dejó de generar WG2; ADR-110 D1 decidió no
portar el Level 4). El resultado habría sido la planta baja de esa zona sin un solo robapieles, por un
filtro midiendo otro mundo. Con WG3 mandando, Level 4 no se consulta.

### T5 — el cap se gasta por cercanía, no por orden de bucle

`FACELING_ACTIVE_CAP` se gastaba dentro de `for cx { for cz { ... } }`, así que lo que se quedaba
fuera no era lo más lejano: era **lo último del recorrido**. La solución no fue subir el cap, sino la
que **el robapieles ya usaba** (`PhantomDriver` junta `PhantomCandidate` y ordena por distancia):
ahora hay `AdultCandidate` y `PackCandidate` con el mismo patrón.

**La unidad es el CHUNK y no el adulto**, que es lo que respeta ADR-094: una oficina despierta entera.

**El test tiene dientes, y se comprobó desactivando el `sort`**: sin él, el chunk a **6,4 m —el más
cercano— no despertaba** mientras sí lo hacían otros más lejanos. El test exige que lo despertado sea
un prefijo exacto de la lista ordenada por distancia.

### Lo que sigue abierto

- **La planta −1.** Hay 113 tramos bajo la cota 0 en las cuatro regiones auditadas y **ninguna
  política de población**. Hoy quedan explícitamente excluidos. Poblarlos o no es una decisión de
  diseño (ADR-104 D5 da las plantas bajo rasante como no implementadas).
- **La planta 4 sale a cero.** Con un 7,4 % de cobertura espera medio adulto, así que es rara por
  pequeña, no vacía por rota — la 3, con el doble de cobertura, sí puebla. Cuánta gente debe vivir en
  un núcleo de torre es contenido y se decide con ojos.
- **Nada de esto se ha ANDADO.** Todo lo de arriba es verde en sondas y suite; en los tres estados de
  WG3-ROADMAP sigue en 🟡 MEDIDO hasta que una persona lo vea en partida.

---

## 1. La hipótesis que este plan viene a comprobar, y que puede cambiarlo entero

El enunciado que veníamos arrastrando es *«el reparto elige el espacio MÁS BAJO de la columna, así
que las plantas altas nacen vacías»* (ADR-109 D5). Es cierto, y **probablemente no es el problema
principal.** Leyendo el código hay un segundo mecanismo, más arriba, que apagaría la población mucho
antes de que la elección de espacio llegue a importar:

- `AdultDriver::sync_population` y `ChildDriver::sync_population` calculan
  `let layer = world_pos_to_layer(p.y)` **del JUGADOR**, y con ese `layer` llaman al sorteo.
- `world_pos_to_layer` es `round((y - PLAYER_BASE_Y) / LAYER_HEIGHT_M)` con `LAYER_HEIGHT_M = 4.0`.
- `FACELING_ADULT_LAYER_DENSITY = [2.0, 0.0, 0.0, 0.0]` y
  `FACELING_CHILD_PACK_LAYER_PROBABILITY = [0.5, 0.0, 0.0, 0.0]`: **todo lo que no sea la capa 0 es
  cero**, con un comentario que dice por qué («unreachable») y que era verdad en WG2.

Las plantas de WG3 miden 332 cm. Poniendo los números juntos:

| Planta WG3 | Y del suelo | `world_pos_to_layer` | Densidad de adultos |
|---:|---:|---:|---:|
| 0 | 0,00 | 0 | 2,0 |
| 1 | 3,32 | **1** | **0,0** |
| 2 | 6,64 | 2 | 0,0 |
| 3 | 9,96 | 2 | 0,0 |
| 4 | 13,28 | 3 | 0,0 |
| 5 | 16,60 | 4 → *clamp* | 0,0 |

**Si esto es cierto, el síntoma real no es «las plantas altas nacen vacías» sino «subir una planta
vacía el mundo de criaturas»** — y ya pasaría en la SEGUNDA planta, la que Joel lleva andando desde
ADR-102. Los que estaban activos abajo además se retiran al subir, porque la condición de retirada es
`world_pos_to_layer(p.y) != pack.layer`.

**No se implementa nada hasta que una sonda diga si es verdad.** Es la regla 5 de WG3-ROADMAP con otro
disfraz: una sonda ciega no da error, da un cero tranquilizador — y aquí el cero tranquilizador sería
concluir «ya está, solo hay que elegir mejor el espacio» y arreglar la mitad pequeña del problema.

---

## 2. Lo que NO se toca

| | Por qué |
|---|---|
| El 8:1 dentro/fuera de oficina | ADR-094 enm. 5, calibrado. Es la mitad de lo que hace que entrar en una oficina signifique algo |
| La sonda de banda ±25 % de población total | ADR-109 D5. Es lo único que impidió el cambio de balance del 350 %, y este plan tiene el mismo riesgo con otra cara |
| El wire | Cero cambios. Ver cabecera |
| `world_pos_to_layer` como función | No se borra aquí. Se le quitan **estos** consumidores; los otros 13 son su propia deuda |
| Que el sorteo sea PURO y por semilla | Es lo que hace que subir y bajar no reubique a nadie. Toda tanda mantiene determinismo por (semilla, chunk, planta, índice) |

---

## 3. Las tandas

Seis, una preocupación cada una, ninguna por encima de ~300 líneas de diff (regla 5).

### T0 — MEDIR. Cero código de producción — ✅ HECHA (2026-08-30, ver cabecera)

Sonda `probe_population_by_storey`: para una región conocida, con el jugador colocado en el suelo de
cada planta 0..N, contar (a) cuántos adultos y cuántas manadas despierta el reparto, y (b) en qué
planta caen realmente. Y una segunda medida, la que decide el orden de todo lo demás: **cuántos
espacios hay por planta** en las regiones de referencia — si la planta 4 tiene tres espacios, poblarla
es un detalle; si tiene cuarenta, es media población del mundo.

Salida esperada: una tabla planta × (espacios, adultos, manadas). **Si la columna de adultos es
`2,0 / 0 / 0 / 0…`, la hipótesis de §1 queda confirmada y T2 pasa a ser lo primero que cambia el
juego.** Si no lo es, este plan se reescribe con la medida delante antes de seguir.

### T1 — El eje de planta: `spaces_at_xz`

Hoy `Wg3ServedWorld::lowest_space_at_xz` devuelve el espacio de suelo más bajo de la vertical, y su
propio doc dice qué hacer: *«el día que el reparto sepa de plantas, esto se sustituye por
`space_at`»*. Ese día es éste.

Entra `spaces_at_xz(x, z) -> impl Iterator<Item = &Wg3Segment>` (o `Vec`), **ordenado por
`floor_y_cm`**, con el mismo desempate por área que ya usan `space_at` y `lowest_space_at_xz` — que
existe por una razón que sigue valiendo: que la respuesta no dependa del orden del vector, que no es
el mismo en las dos partes.

`lowest_space_at_xz` se queda como está mientras tenga consumidores; se retira en T5 si no le queda
ninguno. Tests: una columna de N plantas devuelve N espacios en orden creciente de cota, y un punto
en el vacío del plan devuelve vacío (no un `None` que alguien confunda con «planta baja»).

### T2 — El eje de población deja de ser la CAPA y pasa a ser la PLANTA

La tanda que arregla lo de §1, y la única que puede cambiar el juego por sí sola.

- En los dos `sync_population`, con WG3 mandando, el eje deja de salir de `world_pos_to_layer(p.y)`.
  El jugador está en la planta cuyo espacio lo contiene (`space_at`, que ya existe y ya considera la
  cota), no en la capa de 4 m en la que cae su Y.
- La densidad deja de indexarse por capa. `FACELING_ADULT_LAYER_DENSITY` y
  `FACELING_CHILD_PACK_LAYER_PROBABILITY` **no se tocan en el camino de WG2** — siguen siendo suyas y
  siguen siendo correctas ahí. Con WG3 entra su equivalente por planta (§4).
- La retirada por lejanía usa la misma noción de planta que la activación, o una criatura se despierta
  y se retira el mismo tick.

**El aviso de determinismo, y por qué aquí es seguro.** La semilla del sorteo es
`chunk_seed_layer(seed, (cx,cz), layer)`: cambiar el eje cambia la semilla y por tanto la población
concreta de cada chunk. **No rompe nada persistido** porque los facelings no se persisten a propósito
(el doc de `faceling_spawn` lo argumenta entero, heredado de `phantom_spawn`/ADR-043): son un sorteo
puro y perezoso. Lo que sí hay que conservar es la propiedad de dentro de una partida — subir a la
planta 2 y volver a bajar tiene que devolver la misma planta baja, y eso lo da que el eje sea la
planta y no la Y cruda.

### T3 — Cuánta gente vive arriba: el peso por planta

Ya con el eje bueno, la decisión de contenido. Perilla `FACELING_STOREY_WEIGHT`: qué fracción de la
densidad de planta baja le toca a cada planta por encima.

Tres formas, y **la elige Joel con la tabla de T0 delante**, no este documento:

- **(a) Plana** — todas las plantas igual que la baja. Simple; multiplica la población del mundo por
  el número de plantas, así que obliga a bajar la densidad base para no romper la banda ±25 %.
- **(b) Decreciente** — cada planta un factor de la de abajo (p. ej. 0,6). «Cuanto más arriba, más
  vacío», que es una lectura Backrooms defendible y además es barata en población.
- **(c) Por papel, sin mirar la planta** — la densidad la decide sólo el papel del espacio (que es lo
  que ya hace `wg3_keeps_position`) y la planta no pondera nada. Es la más coherente con ADR-108/109
  y la que menos números nuevos mete; el riesgo es que una torre de oficinas se llene entera.

**Cualquiera de las tres se calibra contra la sonda de banda, no a ojo.** Es el mismo sitio exacto
donde ADR-109 D5 casi se cuela un ×3,5: allí el 39 % de papel de oficina, aquí el número de plantas.

### T4 — El robapieles, el mismo tratamiento

`phantom_spawn::PHANTOM_LAYER_DENSITY = [1.0, 0.0, 0.0, 0.0]` tiene exactamente la misma forma y el
mismo problema, y `spawn_phantom` ya pasó por el mismo arreglo de sitio en ADR-109 D4. Va en tanda
aparte porque es otro sistema con otro cap y otra densidad, y porque mezclarlo con los facelings hace
imposible saber cuál de los dos movió una cifra.

Ojo con lo que NO se hereda: el robapieles es **uno**, no una población, así que la banda ±25 % no es
su métrica. La suya es que siga existiendo y siga encontrándote.

### T5 — Los topes, que son donde esto se rompe en silencio

`FACELING_ACTIVE_CAP = 32` y `FACELING_CHILD_PACK_ACTIVE_CAP = 8` se escribieron para un mundo de una
planta poblada. Con seis, el bucle de activación recorre chunks en orden y **corta al llegar al tope**:
lo que se queda fuera no es aleatorio, es *lo último del bucle*. Eso es un sesgo espacial, no un
recorte — el jugador vería la planta baja llena y las de arriba vacías, y la causa no sería el reparto
sino el orden del `for`.

Esta tanda: medir cuántas veces se toca el tope con la población de T3, y decidir entre subirlo,
repartirlo por planta, o priorizar por cercanía real al jugador en vez de por orden de recorrido.
**Un tope no es un resultado** (regla 7 de WG3-ROADMAP) — si se toca, hay que decirlo.

### T6 — Cierre: medir, andar y escribir

- Volver a pasar `probe_population_by_storey`: la tabla de T0 con las mismas columnas, para poder
  restar.
- La sonda de banda ±25 % en verde, y el 8:1 de oficina intacto.
- `cargo test`, clippy `--all-targets -D warnings`, fmt.
- **Andarlo**: subir tres plantas y encontrarse criaturas arriba. Nada de esto está ✅ hasta que una
  persona lo vea (los tres estados de WG3-ROADMAP).
- Escribir el resultado en `STATE.md` y, si alguna tanda tomó una decisión que no estaba aquí, como
  enmienda a ADR-110 — que es lo que ADR-109 hizo con sus cuatro.

---

## 4. Preguntas abiertas, con nombre

1. **La densidad de planta baja, ¿baja?** Si el mundo pasa de una planta poblada a seis manteniendo la
   densidad por planta, la banda ±25 % obliga a bajar la base. Entonces la planta baja se vacía
   respecto a hoy — y eso lo nota el jugador antes que las plantas altas llenas. Es una decisión de
   diseño, no una consecuencia técnica: **¿el mundo tiene la misma gente repartida en más sitio, o más
   gente?**
2. **¿La banda ±25 % sigue siendo la métrica correcta?** Se calibró contra la población de WG2, que
   era de una planta. Si la respuesta a (1) es «más gente», la banda hay que re-anclarla a propósito y
   dejar dicho contra qué — o deja de proteger nada.
3. **Las manadas de niños y las plantas.** Una manada se decide entera (ADR-109 D5), con el hueco de
   cabeza. Si el hueco de cabeza cae en la planta 3, ¿la manada entera nace en la 3, o se reparte por
   la columna? Lo primero es lo coherente con «se decide entera»; lo segundo daría manadas partidas
   por un forjado, que no son una manada.
4. **Poblar una planta a la que no se puede subir.** Nada garantiza que toda planta tenga escalera
   alcanzable desde donde está el jugador. Criaturas encerradas arriba no son un fallo de este plan
   —el mundo puede tener sitios cerrados— pero sí serían presupuesto de población gastado en nadie.
   `standable_near` no lo cubre: responde «se puede estar de pie», no «se puede llegar».
