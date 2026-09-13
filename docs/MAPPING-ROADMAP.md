> **PROPUESTO — 2026-09-13.** Diseño de la cartografía en papel. **Cero código.** Nada de aquí es
> ley hasta que salga su ADR (M0). Calendario frente al contrato WG3 v1: lo decide Joel.
> Segunda pasada el mismo día: **D5 cerrada** (objeto + corcho), perfil de letra generado (§8b),
> panel de copia y volcado (§8), jerarquía de almacenaje con archivador y caja (§7), asalto a la base.
> Maqueta jugable: artifact «Libreta del cartógrafo». Plan de prototipado: `docs/MAPPING-PROTOTYPE.md`.

# MAPPING-ROADMAP.md — mapas en papel, dibujados de memoria

> Sesión de diseño con Joel, 2026-09-13. Ordena la idea original, el sistema de recuerdo añadido
> después y lo aprovechable de un texto externo (respuesta de IA de buscador) que Joel aportó.
> Juego en **primera persona, cooperativo multijugador con host** (esto responde a la pregunta con
> la que acababa ese texto).

---

## 0. La idea en una frase

No hay minimapa. El jugador **dibuja en papel, con lo que lleva, lo que recuerda haber recorrido**.
Las hojas son objetos físicos: se pierden, se copian, se cambian, se clavan en un corcho. Cuando
el mundo se mueve (displacement), las hojas no se enteran. **Orientarse lo pone el jugador.**

## 1. Pilares (lo que decide cualquier duda)

1. **El juego no te ubica: te reconoces tú si tu mapa es bueno** (enmendado 2026-09-13, D12). Sin punto
   de posición gratis, sin corregir hojas viejas, sin tachar zonas por ti. «Ubicarme» (§3b) solo acierta
   si lo dibujado coincide con lo que acabas de ver.
2. **Se dibuja lo recordado, no lo que existe.** Solo lo recorrido en los últimos X segundos.
3. **Todo cuesta:** papel, tinta y segundos quieto y vulnerable.
4. **Las hojas son items, no datos del jugador.** Si mueres, tu mapa se queda en el cadáver; lo
   clavado o archivado en la base sigue ahí (D5).
5. **El caos es gratis:** el color y el papel son los que llevas en ese momento; no se unifica.

## 2. Qué se adopta del texto externo y qué no

| Idea | Veredicto | Motivo |
|---|---|---|
| Libreta para explorar, carpeta/corcho para la base | **Sí** | Separa campo y gestión; es tu idea con nombre |
| Arrastrar hojas en el corcho para unirlas a mano | **Sí** | Pilar 1: la conexión la deduce el jugador |
| Dibujar deja indefenso unos segundos, con sonido de boli | **Sí** | Pilar 3 |
| Mesa de cartografía para copiar | **Sí, opcional** | Copiar en mesa rápido; en campo lento |
| Comparar mapas en el suelo con otro jugador | **Sí** | Coop sin compartir pantalla |
| Si muere el mapeador, recuperar su cuerpo es misión | **Sí** | Sale solo del pilar 4 |
| Notas de texto e iconos | **Sí** | §6 |
| Suministros de oficina como loot (lore M.E.G.) | **Sí** | Loot en oficinas y bases |
| Dibujos que se alteran en zonas anómalas | **Sí, tarde** | Versión diegética de "zona corrupta", M7 |
| **Tachar solo** la hoja cuando el chunk cambia | **No** | Rompe el pilar 1: se descubre, no se avisa |
| Sprite dibujado previamente por cada chunk | **No** | WG3 es procedural; sale del ráster |
| "Mapear zona" = el chunk entero | **No** | Sustituido por el recuerdo (§3) |

## 3. Sistema de recuerdo

**Qué se guarda.** Búfer circular en el cliente: una muestra cada 0,5 s con `(chunk, planta, celda)`
y las celdas **vistas** desde ahí (radio corto, línea de visión sobre el ráster, no física).
Con X = 60 s son 120 muestras.

**Qué se olvida.** La muestra más vieja que X sale del búfer. Más de X segundos sin dibujar = perdido.

**Frescura = calidad del trazo.**
- 0–20 s: trazo firme.
- 20–60 s: tembloroso, discontinuo, con huecos (semilla por hoja: el mismo recuerdo da el mismo dibujo).

**Qué se dibuja.** Las celdas recordadas y **las paredes que las bordean**. Nada más: una puerta
que no cruzaste es una pared abierta a nada.

**Varias zonas en un recuerdo.** La hoja abierta recibe **solo la parte de su zona**. El resto sigue
en el búfer hasta caducar: si cambias de hoja a tiempo, dibujas la otra zona. Presión para llevar
papel suficiente.

**Hoja en blanco.** Se asigna a la zona donde estás **ahora**.

**Hoja ya usada.** No se reemplaza: **se añade encima**, con la herramienta actual. Si esa zona
cambió por displacement, el trazo nuevo no casa con el viejo, y ese es el aviso (pilar 1).

**Lo dibujado sale del búfer**, así que no se paga dos veces.

**Interrupción.** Recibir daño corta el dibujo. Lo ya trazado se queda; lo no trazado vuelve al búfer.

**Flechas de borde.** Si en el recuerdo pasas andando de una zona a otra (o subes una escalera), eso
**demuestra** que están juntas. Al dibujar, la hoja recibe una flecha a lápiz «→ sigue» en el punto del
borde por donde saliste, y guarda el enlace. Es la única forma de que el plano maestro (§7b) sepa qué
hoja va al lado de cuál. El recuerdo guarda la posición del jugador en cada muestra para poder
detectarlo.

**Ganchos futuros, fuera de este plan:** miedo o cordura acortan X; zonas corruptas meten celdas falsas.

## 3b. Ubicarme (reconocimiento)

Acción de la libreta. Tarda unos segundos quieto. Compara **tu recuerdo reciente** (últimos ~8 s) con las
**paredes dibujadas** de cada hoja que llevas, en su posición:

| Coincidencia | Resultado |
|---|---|
| ≥ 70 % | Círculo pequeño «estás aquí» con tu tinta; la libreta pasa sola a esa hoja |
| 40–70 % | Círculo grande y dudoso |
| < 40 % | Nada: «no reconoces este sitio, en tu mapa esto está en blanco» → atajo «coger hoja y mapear» |

- Si has visto muy poco, no hay veredicto: hay que mirar alrededor.
- **Premia mapear bien** y **encaja con el displacement sin código extra**: un chunk movido deja de coincidir.
- El círculo es tinta y se queda en la hoja. La marca «estás aquí» del plano caduca a los 30 s.
- Umbrales de partida para playtest (D14).

## 4. La hoja

- **Unidad: una hoja = un chunk × una planta** (recomendación, ver D2). Chunk WG3 = 50 m
  (`WG3_CHUNK_M`); la región son 3×3 chunks = 150 m, demasiado para una hoja legible.
- **Capas:** cada vez que se dibuja se crea una capa `{herramienta, celdas, frescura}`. Las capas no
  se funden: se pintan en orden, y de ahí sale la mezcla de colores.
- **Papel:** afecta a cómo se ve, no a la información (salvo el post-it).
- **Etiqueta:** la escribe el jugador (ver D1). El juego no pone coordenadas.

## 5. Material: herramientas y papel

**Herramientas** (item con propiedad `ink` 0..1, mismo patrón que la durabilidad del vendor):

| Herramienta | Trazo | Nota |
|---|---|---|
| Boli azul / negro / rojo | Fino | El más común |
| Rotulador | Grueso, gasta rápido | Tapa detalle |
| Fluorescente | Translúcido | Bueno para marcar, malo para paredes |
| Lápiz | Gris, se ve peor | Único borrable (con goma) |

El gasto es por celda de pared **nueva** trazada. Sin tinta no hay dibujo.

**Papel:**

| Papel | Efecto |
|---|---|
| Folio | Base |
| Cuadriculado | Dibuja la rejilla: se lee mejor la escala |
| Post-it | Solo cabe un trozo del chunk (recorta) |
| Arrugado / manchado | Ruido visual |

Todo entra en las tablas de loot con escasez (memo DayZ): oficinas y bases, poco y repartido.

## 6. Marcas y notas

- **Iconos:** loot, peligro, escalera arriba/abajo, pozo, salida, base, calavera.
- **Tachón de zona corrupta:** herramienta de marca manual, con la tinta que lleves.
- **Texto corto** tecleado sobre la hoja, escrito con el **perfil de letra** del autor (§8b) y el
  color de la herramienta.
- Las marcas también gastan tinta (poco).

## 7. Dónde viven las hojas

Jerarquía de menor a mayor. Cada nivel **solo admite papel** (filtro por categoría con las
restricciones de `ItemContainerGenerator` del vendor, no un contenedor nuevo).

| Objeto | Dónde | Regla |
|---|---|---|
| Hoja | Inventario / suelo | La unidad. Item con peso; cae al morir |
| Taco de folios | Inventario (loot) | Montón de hojas en blanco con **contador**; se encuentra en escritorios. Lo que sale de él son hojas sueltas |
| Libreta | Inventario | **24 páginas fijas con contador** («3/24»); ligera; se pueden arrancar; no admite hojas nuevas |
| Carpeta | Inventario o archivador | Hojas sueltas reordenables con separadores; pesa más |
| Archivador | Base (pieza STP) | Guarda carpetas. Destino natural del **volcado** (§8). Por niveles: cartón o metálico con cerradura |
| Caja de oficina | Base (pieza STP) | Organiza archivadores. Solo organiza, no protege |
| Corcho/pared | Base (pieza STP) | Para **ver y unir** hojas, no para almacenar. Mapa maestro visible en el mundo |
| Mesa de cartografía | Base (pieza STP) | Copiar rápido; comparar dos hojas a pantalla completa |

**Asalto a la base.** Si destruyen la base, el papel **no desaparece: cae al suelo como botín**. Un
rival puede robarte el mapa, y eso es contenido (robo de información). Lo que protege es el nivel del
contenedor: el archivador metálico con cerradura aguanta; el corcho y el archivador de cartón, no.
Hereda la deuda de contenedores sin sincronizar (FARMING-ROADMAP §0).

**Alturas:** en libreta y carpeta, pestañas por planta. En el plano maestro, un tablero por planta,
unido por las flechas de escalera.

## 7b. Plano maestro, pasar a limpio y plano de bolsillo

El corcho de colocar libre se queda como **mesa de trabajo** (teorías, post-its, hojas sin conexión). El
mapa de verdad es el **plano maestro** de la base: se **ordena solo, pero solo con lo demostrado** (D13).

**Colocación.** La zona de la base es el ancla. Una hoja se coloca sola si sus flechas de borde la
conectan con algo ya colocado. Las que no conectan quedan **por colocar**, en el margen.

**Pasar a limpio.** En la mesa de la base, una hoja sucia se convierte en su **versión limpia**: líneas
rectas, tinta negra fina, marcas y notas legibles. Gasta un folio, tinta y tiempo. La limpia vive en
el plano.
- Una limpia más nueva de la misma zona **tapa a la vieja**, que pasa al archivador; no se tira.
- Si la nueva **no coincide** con la vieja (displacement), el plano no elige: las dos quedan marcadas
  **en conflicto** hasta que el jugador decida cuál vale.

**Las tres capas del plano**, de abajo arriba:

| Capa | Qué es | Cómo se ve |
|---|---|---|
| **Nada** | Zona sin mapear o sin conexión | En blanco, «sin mapear»: te pide mapearla |
| **Borrador** | Hojas que llevas encima y no están pasadas a limpio | Fantasma translúcido con su tinta: «esto lo recuerdas, pero no está en la base» |
| **Limpia** | Versión pasada a limpio en la base | Opaca, **por encima** de cualquier borrador |

Encima de todo, «estás aquí» si hace menos de 30 s que te ubicaste.

**En la base** se ve el plano maestro vivo. **Fuera**, solo si llevas un **plano de bolsillo**: una copia
plegada de las limpias en el momento de hacerla (gasta varios folios y tinta). Sin él, fuera solo ves tus
borradores y la nada (D15). El plano de bolsillo es un objeto: cae al morir y se desactualiza.

**Asalto:** las limpias del plano maestro caen al suelo como el corcho.

**Plano de bolsillo, en detalle** (D16, D17):
- Se hace en la mesa eligiendo un **recorte** (plantas o zonas). Cuesta **1 folio por cada 4 zonas** + tinta + tiempo.
- Lleva la fecha escrita. Es una foto: tras un displacement **miente sin avisar**.
- **Desgaste por uso:** cada vez que se abre, y mientras está abierto, se marcan los pliegues. Con desgaste
  alto las zonas del pliegue dejan de leerse; al 100 % se deshace y hay que rehacerlo.
- **Base marcada por defecto**, con opción de quitarla en la mesa. Robado con la base marcada = el rival
  sabe dónde vives.
- Cae al morir, se saquea y se recupera del cadáver.

## 7c. Dos teclas: Notas y Mapa

Propuesta de Joel, adoptada (D20):
- **`N` Notas (lo provisional):** libreta, hojas sueltas y post-its. Aquí se dibuja, se anota y se ubica.
- **`M` Mapa (lo de la base):** el plano maestro en la base; fuera, el plano de bolsillo o la tablet. **Sin
  ninguno de los dos, fuera de la base `M` no abre nada**: se desbloquea llevando un mapa.
- `Tab` sigue siendo el inventario.

**Post-its organizados:** el **bloc** es un contador de post-its en blanco y la **tablilla** (carpeta de
post-its) es donde se ordenan los escritos. Desde la tablilla se pegan encima de la hoja abierta (se
despegan sin dejar rastro), se clavan en el corcho o se tiran. Pegarlos en la pared del mundo sigue siendo D11.

## 7d. Mejoras de la base: del corcho a la pantalla

Progresión de la base cartográfica (D21). **Ningún dispositivo da tu posición en directo**: enseñan el
plano maestro y los avistamientos por «Ubicarme». El «estás aquí» sigue siendo ganado.

| Mejora | Requiere | Qué hace |
|---|---|---|
| Corcho | Madera | Mesa de trabajo: teorías, post-its, hojas sin conexión |
| Mesa de cartografía | Madera + metal | Pasar a limpio, copiar rápido, plano de bolsillo |
| Proyector y pantalla | Circuit + Cable + Battery | Plano maestro en grande en la pared; recibe avistamientos de la facción sin radio |
| Antena | Metal + Cable | Alcance de radios y tablets a todo el nivel |
| Tablet enlazada | Circuit + Battery; proyector | El plano maestro **vivo** mientras hay cobertura; sin ella, la última sincronización. Gasta batería. Si te la roban, se **desvincula** desde la base |
| Reloj enlazado | Circuit; antena o proyector | En la muñeca: avisos de avistamientos y flecha a la base **solo** tras ubicarte (caduca a los 60 s) |

- Papel frente a tablet: el plano de bolsillo no gasta energía pero envejece y se rompe; la tablet se
  actualiza sola pero depende de batería y cobertura.
- **Dependencias:** los materiales electrónicos son del crafteo de ADR-064, **diferido** (FARMING-ROADMAP).
  El reloj de muñeca ya está señalado como superficie diegética en GAME-LOOP-GDD P2.

## 8. Multijugador y rol

- **Pasar:** entregas la hoja, la libreta o la carpeta. Si te fías, le dejas la libreta y la copia él.
- **Copiar: un único panel de tres huecos**, desde el menú contextual de cualquier objeto de papel:

  ```
  [ ORIGEN ]            →  [ DESTINO ]                      +  [ HERRAMIENTA ]
  hoja, libreta           taco (hojas sueltas), páginas         boli, rotulador…
  o carpeta               de la libreta o carpeta de archivador
  ```

  - Hoja a hoja = una copia. **Libreta o carpeta a otro destino = volcado**: casillas para elegir qué
    hojas pasan; cada una gasta una hoja en blanco y tinta.
  - El panel enseña el coste antes de confirmar: hojas, tinta y **tiempo quieto**.
  - En cualquier sitio: lento (partida: 20 s por hoja). En la mesa de cartografía: rápido (6 s).
  - La copia sale con la tinta, el papel y el **perfil de letra de quien copia**. Nunca es idéntica.
- **Volcado al archivador:** la forma buena de guardar una expedición. La libreta vuelve a salir; lo
  archivado se queda en la base.
- **Comparar:** dos hojas de la misma zona superpuestas en el visor.
- **Cartógrafo:** no es una clase. Sale de que las hojas son escasas, valiosas y se pierden.

## 8b. Perfil de letra (generado, no fuentes)

Cada jugador tiene una letra propia, igual que elige colores con RGB. **No se eligen fuentes: la letra
se genera.**

- **Esqueletos de trazo** por carácter (un trazo sin grosor, estilo fuentes Hershey, de dominio
  público, o propios). Mayúsculas, acentos, `ñ`, cifras y signos `¿?¡!`.
- **Semilla** del jugador: desplaza los puntos de control de cada letra. Así tu `a` es siempre tu `a`,
  distinta de la de cualquier otro.
- **Parámetros** 0..1: inclinación, anchura, redondez (tensión del Catmull-Rom), personalidad (cuánto
  deforma la semilla), pulso (temblor), presión (grosor), espaciado, línea base ondulada, ligado entre letras.
- **Ruido por instancia:** la misma letra nunca sale igual dos veces, pero se reconoce.
- **El pulso y la presión también afectan a las paredes que dibujas**, no solo al texto.
- Cada capa y cada nota guardan el perfil de su autor. Consecuencia de rolplay gratis: reconocer la
  letra de un cartógrafo, o sospechar de un mapa falsificado.
- **Tamaño:** semilla (32 bits) + 9 parámetros cuantizados a 4 bits ≈ **8 bytes por jugador**.
- **Legibilidad:** el pulso tiene tope. Opción de accesibilidad **en el cliente** para leer notas ajenas
  en letra limpia; no cambia la hoja.
- **Unity:** esqueleto → Catmull-Rom → ruido → **una malla de quads por hoja** horneada a
  RenderTexture cuando la hoja cambia. **No** un `LineRenderer` por trazo: cientos de componentes por
  hoja. Botones «Generar letra» (todo aleatorio) y «Variar» (pequeña mutación) en la creación de personaje.

## 8c. Facción: plano compartido y avistamientos

**Dependencia dura:** hoy **no existe sistema de facciones** (buscado en docs y código el 2026-09-13).
Necesita su propio ADR antes: pertenencia, permisos sobre contenedores de la base y comunicación (D19).

- **Plano maestro compartido:** la base es de la facción. Cualquier miembro pasa a limpio y cada limpia
  guarda su autor, que se reconoce por la letra.
- **Último avistamiento por radio (D18):** cuando un aliado hace «Ubicarme» con éxito y lleva radio, su
  posición se transmite. En el mapa sale su marca con nombre y una edad que se apaga (90 s).
  **Si está perdido, no transmite nada.** La radio tiene alcance (o antena) y pilas.
- **Pedir posición por radio:** el aliado responde con **el nombre que la facción le puso a la zona**
  («estoy por "escaleras"»), o que no lo sabe.
- **En persona:** se le ve en primera persona; nada en el mapa. Comparar hojas en el suelo sigue valiendo.
- **Red:** el avistamiento es un mensaje nuevo (quién, dónde, cuándo) = bump de `WIRE_SCHEMA_VERSION` + ADR.

## 9. Técnica (a fijar en el ADR de M0)

**Qué existe hoy (verificado 2026-09-13):**
- `ItemProperty` (vendor) solo guarda números (`Boolean/Integer/Float/Double/Item`, un `double`):
  **no cabe una hoja**. Sí cabe su identificador.
- `InventoryReporter`/`InventoryRestorer` ya persisten y restauran propiedades por instancia:
  un `sheet_id` entero viaja gratis con el inventario.
- Los contenedores construidos **no sincronizan contenido** (FARMING-ROADMAP §0): el corcho hereda
  esa deuda.
- ADR-067 (displacement) está en PROPUESTA, sin código.

**Modelo:**
- Item `MapSheet` con propiedades `sheet_id` (0 = en blanco) y `paper_type`.
- Store del host `map_sheets`: `sheet_id → { zone: (chunk_x, chunk_z, storey)?, paper, layers[], marks[], label }`.
  Capas como rejilla de bits de celdas con RLE. El orden de capas y marcas se conserva; nada sale
  de un `HashMap` sin ordenar (regla 13).
- La hoja se guarda como **foto**, nunca se regenera desde la semilla: con displacement, regenerar
  "arreglaría" hojas viejas y mataría la mecánica.

**Autoridad:**
- El recuerdo vive en el cliente: solo usa geometría que el cliente ya tiene cargada.
  Es un juego cooperativo; el antitrampas no es prioridad.
- El host es dueño del store, asigna `sheet_id` y descuenta papel y tinta.
- El payload de la hoja se pide al host **al abrirla**, no viaja con cada inventario.

**Visor:** animación de primera persona sacando libreta y boli. Lo que va en los brazos warpea
(regla 14: `_FP_Mat`). La hoja se renderiza a textura con un shader de tinta.

**ADR obligatorio antes de código (regla 7):** paquetes nuevos (pedir hoja, dibujar, copiar)
con bump de `WIRE_SCHEMA_VERSION` + `WireSchema.Expected` en el mismo commit; sección nueva
en el autosave (enmienda de ADR-032).

## 10. Decisiones abiertas (Joel)

- **D1 Etiqueta.** Recomendado: la escribe el jugador; el juego no pone coordenadas. Pistas
  diegéticas (letreros de planta) si hacen falta.
- **D2 Unidad.** Recomendado: chunk (50 m) × planta. Alternativa: región (150 m), ilegible en una hoja.
- **D3 X del recuerdo.** Partida: 60 s, con 20 s de trazo firme. Parametrizable.
- **D4 Qué cuenta como visto.** Partida: celdas pisadas + radio de visión de ~3 m. ¿Más amplio en salas grandes?
- **D5 Perder hojas al morir. CERRADA (Joel, 2026-09-13): objeto.** Lo que llevas cae en el cadáver
  y se recupera; lo clavado o archivado en la base no se pierde al morir, pero sí se puede perder en un
  asalto (§7). «Calco» queda de reserva si en playtest duele demasiado.
- **D6 Texto libre en multijugador.** ¿Límite de caracteres, filtro? Afecta a hojas que cambian de mano.
- **D7 Corcho.** Recomendado: posición libre (no rejilla), para que unirlas sea deducción.
- **D8 Calcar frente a copiar.** ¿Dos acciones (calcar rápido, solo paredes, con papel de calco; copiar
  lento, todo) o solo copiar? Recomendado: solo copiar en la primera versión.
- **D9 Asalto.** ¿El papel del suelo se queda para siempre o caduca? ¿El archivador metálico aguanta
  siempre o solo un tiempo? Depende del sistema de asalto, que no existe todavía.
- **D10 Páginas de la libreta.** Partida: 24. ¿Tamaños distintos (libreta pequeña y cuaderno grande)?
- **D11 Post-its (propuesta).** Tres usos más allá del papel pequeño: pegado **encima de una hoja** (capa
  temporal que se arranca), pegado **en una pared del mundo** (lo ven otros jugadores en primera persona;
  migas de pan que se pueden quitar o falsificar; toca red, **ADR propio**) y pegado **en el corcho**
  entre hojas. Baratos y de un solo uso: la moneda pequeña del sistema.
- **D12 Pilar 1 enmendado. CERRADA (Joel, 2026-09-13):** ubicarse se gana por reconocimiento (§3b).
- **D13 Plano solo con conexiones demostradas. CERRADA (Joel, 2026-09-13):** el jugador no fuerza la
  posición de una hoja en el plano; para teorías está el corcho.
- **D14 Umbrales de «Ubicarme».** Partida: 70 % / 40 %, recuerdo reciente de 8 s. Aceptados como punto
  de partida; se ajustan en playtest.
- **D15 Plano fuera de la base. CERRADA (Joel, 2026-09-13):** solo con plano de bolsillo o tablet enlazada.
- **D16 Base marcada en el plano de bolsillo. CERRADA:** marcada por defecto, con opción de quitarla.
- **D17 Desgaste del plano de bolsillo. CERRADA:** sí, por uso.
- **D18 Aliados en el mapa. CERRADA:** último avistamiento por radio tras «Ubicarme»; nunca en directo.
- **D19 Facciones. CERRADA como dependencia:** ADR propio fuera de este roadmap antes de M10.
- **D20 Teclas. CERRADA (propuesta de Joel):** `N` notas provisionales, `M` mapa de la base.
- **D22 El libro de supervivencia de STP como modelo base (propuesta de Joel, 2026-09-13).** Wieldable con UI
  World Space por secciones, input propio, profundidad de campo y sonido de página: base para la libreta y, más
  adelante, carpeta y archivador. Detalle y teclas en MAPPING-PROTOTYPE §3.9 (`B` libro, `N` choca con `FireMode`,
  `M` libre). Abierto: libreta en `N` reasignando `FireMode`, o como pestaña del libro en `B`.
- **D21 Mejoras de la base (propuesta de Joel, adoptada como dirección):** corcho → mesa → proyector →
  antena → tablet → reloj (§7d). Costes y números, en playtest.

## 11. Rebanadas

Cada una se cierra con verificación propia. Nada empieza sin M0.

| # | Qué | Verificación | ADR |
|---|---|---|---|
| **M0** | ADR: modelo de hoja, store, paquetes, save | Auditor de arquitectura | **Sí** |
| **M1** | Recuerdo + hoja + un boli + visor, **local**, sin red ni guardado | EditMode: caducidad del búfer, reparto por zona y planta, frescura; captura del visor | — |
| **M2** | Store del host, persistencia, sync entre peers, hoja cae al morir | Test de ida y vuelta del save; joiner abre la hoja del host | Cubierto por M0 |
| **M3** | Herramientas, tintas y papeles, estética de trazo, loot, **perfil de letra** | Test de gasto de tinta; test de determinismo de la letra (misma semilla = mismo esqueleto); capturas de mezcla | Perfil en el wire: cubierto por M0 |
| **M4** | Marcas, iconos, texto, tachón | Test de orden estable de marcas | — |
| **M5** | Taco con contador, bloc de post-its y tablilla, libreta de 24, carpeta, archivador, caja, corcho, mesa; papel al suelo en un asalto | Filtro «solo papel» con el vendor; contenedores host-local (deuda declarada) | Si se sincronizan contenedores |
| **M6** | Panel de copia, volcado y comparar | Copia ≠ original en tinta/papel/letra, igual en celdas; coste mostrado = coste cobrado | — |
| **M7** | Flechas de borde + «Ubicarme» | Test: cruzar un borde en el recuerdo crea el enlace en ambas hojas; test de umbrales con hoja buena, hoja con huecos y zona movida | — |
| **M8** | Plano maestro: colocación por enlaces, pasar a limpio, conflictos, tres capas, plano de bolsillo | Test: hoja sin enlace nunca se coloca; limpia nueva tapa y archiva la vieja; conflicto si no coinciden | Si el plano se sincroniza |
| **M9** | Displacement + distorsión en zonas corruptas | Bloqueado por ADR-067 S1 | Enmienda ADR-067 |
| **M10** | Radio, último avistamiento, pedir posición; plano compartido de facción | Test: aliado sin ubicar no transmite; avistamiento caduca; fuera de alcance no llega | **Sí** (wire) + ADR de facciones (D19) |
| **M11** | Mejoras electrónicas: proyector, antena, tablet, reloj | Test: tablet sin cobertura congela la última sincronización; desvincular corta el acceso; ningún dispositivo expone posición en directo | Bloqueado por crafteo ADR-064 |

## 12. Fuera de este plan

Cordura que acorte el recuerdo, brújulas y otros instrumentos, planos de lore encontrados ya
dibujados, mercado o precio de mapas, IA de NPC cartógrafos.
