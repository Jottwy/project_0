# WG3-ROADMAP.md — plan de trabajo de WorldGen3

> Escrito el 2026-08-28 al cerrar la sesión de ADR-102 (plantas apiladas), con el mundo de dos plantas
> ya andado por Joel en `WorldGen3Live`.
>
> **Por qué existe este fichero.** Hasta hoy el roadmap de WG3 era la cadena de ADRs 095 → 102: cada
> paso salía del anterior y la siguiente sesión no tenía que elegir. Eso se acabó. Lo que queda son
> cuatro frentes que **no dependen entre sí** y compiten por el mismo tiempo, así que hace falta
> escribir el orden en vez de deducirlo.
>
> Las decisiones vinculantes están en [`DECISIONS.md`](DECISIONS.md), ADR-095 a ADR-102 con sus
> enmiendas. Este fichero no decide nada: ordena, y dice qué medir.

---

## Actualización 2026-08-29 — léela antes que nada de lo de abajo

Cuatro cosas cambiaron en un día y dejan parte de este fichero desfasado. Lo de abajo **no se borra**
porque sus medidas siguen siendo buenas; lo que cambia es el estado y el orden.

| Frente | Estado |
|---|---|
| **A — identidad visual por papel** | ✅ **HECHO.** `b5c6cfc3`, `2c0d5dce`, `a8de06c6`. Verificado EN JUEGO: los siete papeles se distinguen desde el fondo del tramo |
| **B — WG3 como autoridad** | 🔴 Sin empezar. **Sigue siendo el techo de todo lo demás** |
| **C — variedad de contenido** | Reencuadrado: ver §7, el catálogo de formas por coste |
| **D — la rareza Backrooms** | ▶️ **DESAPARCADO.** Es ADR-103, aprobado por Joel, sin código |

**Y hay dos ADRs nuevos que no existían cuando se escribió este fichero:**

- **ADR-103** — la identidad de subnivel (Level 0, 0.1, 0.2, 0.3) como **perfil de perillas** sobre un
  campo de mezcla. Aprobado. Enmienda 2: el mecanismo del Frente A **sí da**, pero **el papel se quedó
  el eje de tono**, así que la identidad se muda al AIRE — niebla, ambiente, color de plafón, en ese
  orden y con presupuesto medido.
- **ADR-104** — la verticalidad: salas a doble altura, atrios, agujeros por los que caer, megapilares,
  y el edificio creciendo hacia abajo. PROPUESTA.

**Tres reglas que salieron medidas y que no hay que volver a descubrir:**

1. **Un delta de albedo del 10 % no existe.** Para distinguir dos cosas, tono o material, nunca
   claridad — el ruido que mete sólo la luz entre dos paredes de la misma captura es del 29,5 %.
2. **Forma libre en la malla, forma cara en la colisión, y el umbral son 50 cm.**
3. **La niebla es presupuesto compartido**, no un valor: gasta de la legibilidad del papel y de
   encontrar por dónde subir. `rho ≤ 0,045`, y `≤ 0,030` mientras la escalera dependa de su rodapié.

**Reparto real del mundo servido** (504 tramos, medido en cliente contra servidor): oficina 31 %,
**escalera 23 %**, servicio 16 %, pasillo 11 %, callejón 10 %, nave 5 %, **espina 4 %**. Las dos cifras
en negrita son las que hay que vigilar: la espina es el papel que existe para decir por dónde ir y es el
más raro del mundo, y el 23 % de escalera **debería BAJAR** si la verticalidad de ADR-104 sustituye
escaleras en vez de sumarse a ellas.

**Doblar la escalera está PARADO y medido**, por si vuelve la pregunta: `probe_stair_sites_if_doubled`
(dentro de `a8de06c6`) dice que pasa de 252 a 339 espacios candidatos, **+35 %**, y que **lo que ata es
el largo y no el ancho**. No se hace porque no era lo pedido.

---

## 0. Dónde estamos (medido, no supuesto)

El mundo servido de WG3 sale del **plan**, no del compositor por bocas: `Wg3ServedWorld::plan_region`
→ `plan::plan_building` → `fill::fill_building`. Dos plantas, con escaleras que se suben.

Cifras del mundo servido, cuatro regiones de referencia:

| | (0,0) | (1,0) | (0,1) | (−1,2) |
|---|---:|---:|---:|---:|
| superficie andable | 109 % | 116 % | 116 % | 114 % |
| mancha andable mayor | 100 % | 99 % | 100 % | 99 % |
| planta alta alcanzada | 100 % | 100 % | 100 % | 99 % |
| escaleras | 2 | 2 | 3 | 5 |

Por encima del 100 % porque ya hay más suelo que región. Segunda planta en **46 de 49** regiones.
Punto de partida de la auditoría del 2026-08-28: **21 / 26 / 3,5 / 24 %**.

**Aviso de comparabilidad.** ADR-102 enmienda 1 cambió dos constantes de MEDIDA —`CEILING_CAP_M`
6,0 → 7,0 y `WALK_STEP_M` 0,20 → 0,27— porque las viejas declaraban insubible un pozo de escalera.
Las cifras de arriba **no son comparables** con las de ADRs anteriores a ADR-102.

### Lo que NO hay que volver a tocar

| | |
|---|---|
| El plan decide antes que la pieza | ADR-100. Es la corrección que desatascó el sistema entero |
| Reparto por chunk y rasterizado | XZ puro y columnas de tramos; aguantan plantas sin cambios |
| El wire | v49. Las plantas viajaron sin bump: la cota ya estaba en los tres datos |
| Vanos excavados | ADR-101, los dos lados restan la misma caja |
| Contrato de junta | ADR-096 cumplido; las puertas se cruzan |
| Altura de planta | 332 cm = 308 libres + **dos** losas. Contarlas mal fue el peor artefacto visual |
| Huella de peldaño | 60 cm porque la celda del ráster mide 50, no por comodidad |

---

## 1. Frente A — identidad visual por papel

**Sin ADR. Es lo que más se nota por lo poco que cuesta.**

Un pasillo, un almacén y una nave se dibujan idénticos. El plan ya clasifica cada espacio y
`fill::style_of` le pone número —espinazo 1, pasillo 2, nave 3, servicio 4, callejón 5— pero
`SpaceRole::Stair` cae en el `_ => 0` de una oficina. Y peor: **`style` viaja por el cable, el cliente
lo parsea en `Wg3GeneratedSegment.style` y no lo usa nadie.**

- A1. Número propio para `Stair` en `fill::style_of`. Una línea.
- A2. Que `Wg3SceneAssembler` elija material por `style`. Es el trabajo de verdad: hoy
  `Wg3Materials` tiene un juego único.
- A3. Medir: no hay métrica de esto. La verificación es andarlo y saber dónde estás sin mirar el mapa.

**Por qué primero.** La primera partida con dos plantas se resumió en «no sé dónde ir a subir». La
mitad de eso era que había una escalera por región —arreglado— y la otra mitad es que **nada se
distingue de nada**, que sigue entero.

---

## 2. Frente B — WG3 como autoridad

**Requiere ADR nuevo antes de tocar código** (regla dura #7: cambia la superficie de validación
cliente↔servidor).

Hoy `BACKROOMS_WG3=1` es **aditiva**: colisión, movimiento, navegación y spawn siguen resolviéndose
contra WG2. Consecuencias medidas:

- En el juego real, subir una planta **te congela**. La capa sale de la Y (`layer_from_player_y`,
  `LAYER_HEIGHT_M = 4.0`), `update_ownership` sólo genera la capa 0, un chunk ausente es sólido, y el
  resultado es `Blocked` → `position = from`.
- Las dos plantas **sólo se andan en `WorldGen3Live`**, porque `Wg3TestPlayer` usa un
  `CharacterController` contra los colliders del cliente y nunca transmite pose.
- Las primitivas de colisión de WG3 existen y **no las llama nadie**: `is_solid_at`,
  `blocked_standing_at`, `floor_below`, `headroom_above_floor` en `raster.rs`.
- 606 eventos `faceling_unwedged` en tres minutos: las criaturas navegan la rejilla invisible de WG2.

Lo que el ADR tendrá que decidir, como mínimo: dónde vive el ráster de región en el bucle de juego,
cómo se elige la fuente en el seam del jugador (`game_loop.rs`, `apply_client_authoritative_move`
recibe hoy sólo `&World`), qué pasa con el concepto de CAPA —en WG3 no hay pisos de 4 m—, y qué se
hace con `resolve_safe_spawn`, el fantasma y los facelings, que también leen la rejilla.

**Es el ADR más grande que queda.** Nada de lo demás lo necesita, pero sin él las dos plantas son
decorado en el juego de verdad.

---

## 3. Frente C — variedad de contenido

**Sin ADR. Es problema de contenido, no de algoritmo, y conviene no confundirlo.**

El catálogo tiene 19 piezas con huellas fijas (9 × 9, 13 × 10, 42 × 30…) y el plan produce
rectángulos de la medida que pide la arquitectura. Que una huella autorada caiga dentro de la
tolerancia de un espacio planificado es casualidad: **hoy se colocan 5-14 piezas por región de ~170
espacios**. Todo lo demás son tramos generados, que son correctos y son iguales.

Dos caminos, y forzarlo desde el relleno no es ninguno de los dos:

- C1. **Que existan piezas de las medidas que el plan pide.** Mirar el histograma de tamaños que ya
  imprime `probe_region_plan` y autorar contra él.
- C2. **Que el plan se ajuste a las medidas que existen.** Sesgar `TARGET_AREA_M2` y los cortes hacia
  huellas del catálogo. Más barato, y hace el mundo más regular — que es justo lo contrario de lo que
  pide el frente D.

---

## 4. Frente D — la rareza Backrooms

**Aparcado por decisión de Joel** («primero empecemos por ver cómo queda bien hecho»). Sigue vigente.

El mundo sale muy ortogonal y ordenado. Hoy la rareza viene sólo de las zonas `Weird` del campo de
escala y de los vacíos intencionados. Perillas identificadas y **no tocadas**: `WEIRD_SPREAD`,
`TARGET_AREA_M2`, `VOID_CHANCE_WEIRD`, `clear_height_cm`.

Ojo con el orden: esto choca de frente con C2. Decidir cuál manda antes de tocar ninguno de los dos.

---

## 5. Cabos sueltos, con nombre

No son frentes, son cosas que hay que hacer y que no caben en ninguno:

- **La fuga de luz entre plantas.** Los plafones de `Wg3SceneAssembler` no proyectan sombra y
  alcanzan hasta 21,75 m contra una losa de 12 cm. Se arregla por **culling por planta** —el índice
  sale de `originY`, no viaja por el wire— y **no** bajando alcance e intensidad, que son valores que
  Joel ya validó mirándolos.
- **`Wg3CarvingTests`**: los 5 tests de ADR-101 compilan verdes y **no se han ejecutado nunca**.
  Piden el editor cerrado: `Unity.exe -runTests -batchmode -testPlatform EditMode -testFilter Wg3Carving`.
- **Escalera de ida y vuelta.** Un tiro recto pide 12,6 m de sala, y por eso sólo hay **2-5 sitios por
  región** donde cabe. Medido: harían falta ~9 escaleras bien repartidas para que la mediana andando
  hasta la más cercana baje de 30 m; hoy quedan 2-5. Doblar la escalera es lo único que sube ese techo.
- **`Goal::JoinIslands` / `best_bridge`** siguen en pie sirviendo a `compose_region`, que ya no sirve
  el mundo. Se retiran cuando la ruta planificada esté demostrada en juego.
- **`compose_region`** entero es legado con sondas propias. Leer una cifra suya y hablar del mundo
  servido es comparar dos cosas distintas.
- **`a_region_is_worth_its_size`** tiene un tope de tiempo de pared (1000 ms) que mide la contención
  de la suite tanto como el compositor. Si vuelve a molestar, quitarlo en vez de subirlo otra vez.

---

## 6. Orden recomendado

**A → B**, y C/D después de B.

A porque cuesta poco y ataca lo único que Joel ha reportado dos veces («no sé dónde ir»). B porque
sin él las dos plantas no existen en el juego real, y cuanto más contenido se acumule encima de WG2
más cara será la mudanza. C y D mueven la textura del mundo y conviene tener el mundo de verdad antes
de decidir cómo debe sentirse.

---

## 7. Features del mapa, ordenadas por coste real (2026-08-29)

Esto es lo que sustituye al Frente C. Sale de ADR-104 D10, aprobado por Joel, y su valor es que **dice
en qué escalón cae una idea antes de prometer nada**. La frontera la marca una sola cosa: WG3 dibuja
**cajas alineadas a los ejes** y su colisión se **rasteriza a celdas de 50 cm**.

### Escalón 1 — Gratis: son cajas y el relleno ya sabe emitirlas

| Feature | Nota |
|---|---|
| **Megapilares** | Columnas que cruzan las dos plantas de un atrio. Es la mitad de la sensación de masa: un atrio vacío es un hueco, con pilares es masivo |
| **Agujeros en el suelo** | ADR-104 D4. La conexión vertical más barata que existe: una escalera pide 12,6 m de sala, un agujero pide su huella |
| **Mezzanines y entreplantas** | Un forjado parcial dentro de un atrio |
| **Techos artesonados / casetones** | Relieve de techo. Cambia la lectura de una nave sin tocar el suelo |
| **Rejillas y celosías** | Barras. Se ve a través y no se pasa — muy Backrooms, y es la misma caja repetida |
| **Zócalos y escalonados** | Bancos corridos, plataformas, resaltes de pared |

### Escalón 2 — Barato: decoración que el servidor no ve

El servidor sólo necesita saber dónde está **el hueco**; la forma del marco no la mira nadie.

Marcos de puerta, **medias lunas**, arcos, molduras, remates, dinteles, rodapiés. Todo esto es malla
del cliente sobre una puerta que ya existe. **Es el mejor ratio sensación/coste de la lista** y no toca
ni el plan ni el ráster.

### Escalón 3 — Medio: formas libres como pieza AUTORADA

Salas **redondas**, **triangulares**, poligonales, en diagonal. La malla se dibuja en el editor de salas
y se hornea; la forma es libre. **El límite es que su colisión se rasteriza a 50 cm**: un círculo grande
se siente redondo, uno pequeño se siente como una escalera de píxeles. Sirve para salas, **no para
detalles**.

⚠️ **Y hay un cuello que no es de dibujo:** hoy se colocan **de 5 a 14 piezas por región sobre unos 170
espacios**, porque las huellas autoradas no coinciden con las que el plan pide. **Dibujar más piezas sin
mirar antes el histograma de tamaños que ya imprime `probe_region_plan` las deja fuera igual de rápido.**
Ese histograma es el primer paso de este escalón, no el último.

### Escalón 4 — Hoy imposible: geometría no-caja GENERADA

Un pasillo curvo que salga del plan. `PlanRect` es literalmente un rectángulo y la subdivisión BSP no
sabe hacer otra cosa. **No es un ajuste, es otro sistema.** Si alguna vez se quiere, es su propio ADR y
probablemente su propio rasterizador.

### La regla, otra vez porque es la que se olvida

**Forma libre en la malla, forma cara en la colisión, y el umbral son 50 cm.** Toda geometría de WG3
más fina que la celda del ráster **cambia de significado, no de precisión**, al pasar al servidor. Ya
costó una tarde con los peldaños de 30 cm (ADR-097 enmienda 1).
