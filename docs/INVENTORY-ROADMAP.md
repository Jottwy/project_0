# INVENTORY-ROADMAP.md — El inventario del superviviente

> Diseño, no código. Abierto el 2026-09-12 con Joel. Lo marcado **DECIDIDO** es de Joel; lo marcado **IDEA** está
> anotado para no perderlo y NO está aprobado. Nada aquí sustituye a un ADR: todo lo que toque wire, schema de
> guardado o autoridad lleva el suyo antes de escribir código (CLAUDE.md, regla 7).

Maquetas (privadas, en claude.ai):
- **Vigente — Inventario del superviviente** (pantalla única con todo lo decidido): https://claude.ai/code/artifact/a6e37c3e-3c76-4cd7-a5ff-2c7be05d96e2
- Histórico: carpeta de expediente (descartada) https://claude.ai/code/artifact/fa50a401-6765-4ac0-80d0-919825e05d2a ·
  mochila abierta con tres tipos https://claude.ai/code/artifact/fa8937b4-f523-47da-abb4-9388f93de1eb ·
  inventario ordenado por el cuerpo https://claude.ai/code/artifact/a938f1c3-0c7d-4d0b-88c8-89f663d325ce

## Decisiones

### D1 — `TAB` abre una pantalla única de tres columnas — DECIDIDO (2026-09-12)
Se descartó la carpeta de expediente con pestañas: es un menú disfrazado, ata la estética a las oficinas, se tiraba
entera en fase 2 y tres de sus pestañas eran de sistemas inexistentes. También se descartó separar equipo e
inventario en pestañas: la acción más común es coger una prenda de un contenedor y ponérsela, y eso exige verlos a la vez.

| Izquierda | Centro | Derecha |
|---|---|---|
| Personaje 3D en tiempo real con 9 slots alrededor, cada uno a la altura de su parte y con línea hasta ella | Lo que llevas: la mochila arriba, los bolsillos de cada prenda debajo en orden del cuerpo | Contenedor abierto (uno a la vez) y suelo a 1,5 m; etiqueta del objeto seleccionado |

- **Abajo, fijo siempre** (también desnudo): manos (2), funda y barra de carga.
- **Slots del cuerpo**: cabeza, cara, torso, encima, espalda, cintura, manos, piernas, pies. Nunca cambian de sitio.
  Cada slot muestra su ocupación («2/4»). Reutiliza `STP_UI_CharacterPreview.prefab` del vendor, con más slots.
- **Fondo**: el mundo oscurecido, **sin desenfoque**, y **no se pausa** (el desenfoque borra la silueta que llega por el pasillo).
- **Tres modos del centro**:
  - *Todo* (por defecto): un único desplazamiento para todo el centro. Nunca un desplazamiento propio dentro de la
    mochila: cortaría el render. Personaje, manos, funda, carga y contenedor no se desplazan jamás.
  - *Foco*: clic en un slot del muñeco abre solo esa prenda, en grande, sobre su silueta. «← Todo» vuelve.
  - *Lista*: las mismas cosas en filas con cantidad, tamaño, peso y ubicación. Escala a cualquier número de huecos.
- **No hay límite de huecos por espacio de pantalla** (Joel): al final de partida se lleva mucho; lo que limita es el peso (D6).
- **Interacción**: clic en objeto y clic en hueco = mover; doble clic en una prenda = ponérsela (si el slot está
  ocupado, se cambian); clic derecho en un slot = quitársela al primer hueco válido; arrastrar, como tercera vía.

### D2 — Tres tipos de mochila — DECIDIDO (2026-09-12)
Cada tipo tiene su dibujo, sus compartimentos y su número de huecos. Es botín: encontrar una mejor se ve.

| Tipo | Compartimentos (tamaño máximo, D7) | Huecos | Peso vacía | Reparto (D6) |
|---|---|---|---|---|
| Bolsa de tela | principal 3×2 (grande) | 6 | 0,2 kg | −10 % |
| Mochila de oficina | principal 4×3 (grande), lateral ×2 de 1 (mediano), frontal 4 (pequeño) | 18 | 1,1 kg | 0 % |
| Mochila de montaña | tapa 3 (pequeño), principal 4×4 (voluminoso), lateral ×2 de 2 (mediano), frontal 4 (pequeño) | 27 | 2,4 kg | +20 % |

Cifras de maqueta, sin balancear.

### D3 — El fondo de la mochila es un render del modelo 3D — DECIDIDO (2026-09-12)
No una ilustración: así la fase 1 casa con la fase 2. Exige un modelo por tipo, render cenital con la misma luz y la
rejilla de casillas autorada **sobre ese render**.

### D4 — Cada prenda da sus bolsillos — DECIDIDO (2026-09-12)
Sin chaqueta no hay esos cuatro bolsillos. Prendas y mochilas son la misma regla: **un objeto que se lleva puesto y
contiene objetos**. Encaja con la escasez del loot y hace de la ropa botín. Ejemplos de maqueta: chaqueta 4, chaleco 2,
riñonera 2, cargo 4, vaqueros 2, bota 1 (la caña); gorra, mascarilla, guantes y polo, ninguno.
- **El cinturón no da bolsillos: da funda.** Sin cinturón la funda tiene 2 huecos; con él, 4. Crece en la barra fija
  de abajo, no en el centro. Ocupa el slot de cintura (compite con la riñonera).
- Hoy la ropa son 4 slots cosméticos (ADR-022, `equipment:[i32;4]` en la pose). Los slots nuevos (cara, encima,
  manos, espalda, cintura) son **enmienda a ADR-022 y bump de wire**.

### D5 — Lo de dentro se va con la prenda — DECIDIDO (2026-09-12), opción (a)
Quitarte la chaqueta o la mochila no vacía sus bolsillos: van con ella a donde la dejes, y otro jugador se la lleva
con todo (estilo DayZ). Es lo que pide la fase 2 (mochila soltada, saqueable). La opción (b), volcar el contenido al
quitarla, se descartó porque la fase 2 la tiraba.
**Pide ADR propio**: objetos que contienen objetos cambian el schema de guardado (ADR-032, `stp_inventory`) y cómo
viaja un contenedor por el mundo.

### D6 — El peso solo frena; el equipo mueve los tramos — DECIDIDO (2026-09-12)
- **El peso nunca impide guardar** y **nunca se reduce**: 1 kg es 1 kg en la barra (la «reducción de peso» por mochila de
  Project Zomboid confunde al jugador).
- **El máximo** que se carga lo da la Fuerza del cuerpo, y solo ella.
- **Tres tramos**: ligero / cargado (−aliento) / sobrecargado (no corre). Base de maqueta con máximo 24 kg: cargado
  desde 14 kg, sobrecargado desde 20 kg.
- **Reparto**: única propiedad de carga del equipo, solo en mochilas y cinturones. Multiplica los umbrales, no el
  máximo. Aditivo con **tope +25 %**. Ejemplo: montaña (+20 %) + cinturón (+5 %) ⇒ cargado desde 17,5 kg.
- **Lista cerrada de propiedades del equipo**: huecos, reparto, protección, abrigo. Como mucho dos por prenda. Nada de
  bonus sueltos («+10 % velocidad»). Una propiedad nueva se decide aquí, no se cuela en un objeto.
- El cálculo de carga vive en **una función pura con test**; más adelante la puede validar el servidor.

### D7 — Qué cabe dónde: tamaño por objeto, máximo por compartimento — DECIDIDO (2026-09-12)
Todas las casillas son iguales (nada de tetris). Cada objeto tiene uno de cinco tamaños y cada compartimento admite
hasta uno:

| Tamaño | Ejemplos | Compartimentos que lo admiten |
|---|---|---|
| 1 diminuto | balas, pilas, mechero, pastillas | todos |
| 2 pequeño | navaja, venda, barrita, papel, cinta, lata pequeña | bolsillos de ropa, riñonera, caña de bota, frontal y tapa |
| 3 mediano | botella, linterna, destornillador, tela | laterales de mochila, funda |
| 4 grande | chatarra, taza, prenda doblada | principal de mochila |
| 5 voluminoso | batería de coche, tubería, caja de herramientas | manos (**ocupa las dos**) o principal de la mochila de montaña |

- **Tamaño y no listas por objeto**: para el jugador se ve igual, pero una lista hay que mantenerla a mano en cada
  objeto nuevo. Las pocas excepciones reales van por **etiqueta**: en la funda solo cuelgan herramientas.
- **Se ve antes de fallar**: cada compartimento lleva un medidor de cinco rayas; al coger un objeto, las casillas donde
  cabe se iluminan y las demás se tachan en rojo. Si se insiste, la etiqueta dice por qué («Agua de almendras es mediano.
  Chaqueta · bolsillos admite hasta pequeño»).
- Vendor: `ItemContainer` admite restricciones (`WithRestriction(ContainerRestriction)`, `ItemContainer.cs:75`). **Sin
  comprobar** que el tamaño se monte encima sin tocar el vendor.

### D8 — Stackeo mínimo — DECIDIDO (2026-09-12)
Con el loot a cuenta gotas, la presión de espacio es buena: cada objeto es un objeto.

| Qué | Se apila | Tope |
|---|---|---|
| Munición, tornillos, clavos (diminutos contables) | sí | 20–30 |
| Pilas, pastillas | sí, poco | 4–6 |
| Todo lo que tiene estado (venda limpia o sucia, botella con sus ml, linterna con carga, lata abierta) | nunca | 1 |
| Resto (chatarra, tela, cinta, comida) | no | 1 |

**Trampa conocida** (memoria `itemstack-stacksize-bulk-add-clamp`): con `StackSize = 1`, `AddItemsById`/`AddItem` de N>1
recortan a 1 en silencio. Si casi todo pasa a no apilable, `InventoryRestorer.cs` tiene que añadir de uno en uno.

## Fases

| Fase | Qué | Toca |
|---|---|---|
| **1** | Pantalla de D1 en 2D sobre render fijo de la mochila; tres tipos; bolsillos por prenda; tamaños; carga con reparto; stackeo | cliente + ADR de D4 (ADR-022) y D5 (ADR-032) |
| **2** | La mochila se suelta en el suelo, cámara mirando dentro, objetos 3D en las mismas casillas; los demás te ven agachado | ADR (bolsa con contenido en el mundo) |

La fase 2 va **después de Alpha 1**. El estado sostenido «agachado con la mochila» cabe en los bits libres de
`buttons` (ADR-044), sin campo nuevo.

**Lo primero de la fase 1 es investigar el vendor**: `ItemContainer` fija el tamaño al construirse (`ItemContainer.cs:124`)
y `Inventory` crea sus contenedores una sola vez desde `_defaultContainers` (`Inventory.cs:279-282`), sin alta ni baja en
runtime. Como no se edita el vendor (memoria `stp-no-direct-edits`): o se precrean contenedores al máximo y se capan por
prenda, o los bolsillos viven en código propio fuera de `Inventory`.

## Riesgos de la fase 1
- Cada tipo de mochila es un modelo, un render y una rejilla autorada: tres es asumible, veinte no.
- Iconos de nuestros items: el vendor trae los suyos; los propios hay que renderizarlos con la misma luz y ángulo.
- Un render cenital puede quedar plano: validarlo con el primer tipo antes de hacer los otros dos.
- Un solo contenedor externo a la vez: saquear cadáver y cajón juntos no cabe.
- La mochila de montaña con equipo completo obliga a desplazar en *Todo*; *Foco* y *Lista* lo compensan. Medirlo en juego.

## Armonía del HUD — IDEA (2026-09-12)
Un objeto del personaje por uso; ningún dato en dos sitios salvo las urgencias, que avisan con el cuerpo (pantalla,
sonido, cojera).

| Objeto | Para qué | Contenido |
|---|---|---|
| Reloj de muñeca | cómo estás | vitales a vista rápida; más pantallas cuando existan condición y nutrición. Solo se mira, no se clica (ADR-077: el warp rompe el puntero) |
| Inventario (`TAB`) | qué llevas y cómo está tu cuerpo | este documento |
| Libro | qué sabes | planos de construcción (existe) + recetas aprendidas |

## Sistemas anotados, sin empezar — IDEA (2026-09-12)
Nada de esto estaba escrito antes. Dirección: realismo a lo SCUM, adaptado a los Backrooms. Cada uno necesita ADR.
- **Cuerpo por zonas** (16, estilo Project Zomboid). **Dirección preferida por Joel: se ve en el mismo muñeco del
  inventario**, con un botón «Ropa / Heridas» que pinta las zonas sobre el cuerpo y atenúa la ropa. Lesiones: rasguño,
  corte, herida profunda, sangrado, fractura, esguince, quemadura, contusión, infección; tratamientos: venda limpia o
  sucia, desinfectante, sutura, férula, analgésico. Efectos por zona: piernas y pies → correr, saltar, ruido; brazos y
  manos → golpe con herramienta, apuntar, crafteo; tronco → aliento máximo y carga; cabeza → visión. Generaliza la
  venda por brazo (bits 7/8). Recomendado: autoridad del servidor, como la salud (ADR-025).
- **Condición** en tres escalas: aliento (segundos, la stamina actual), fatiga muscular por grupo (minutos: piernas,
  brazos, tronco), cansancio (horas). Pulso que une esfuerzo y miedo.
- **Nutrición**: estómago con digestión en cola; reservas de carbohidratos, proteínas, grasas, agua, vitaminas,
  minerales con banda sana. A la larga: peso, grasa y músculo → Fuerza, Agilidad, Resistencia, Constitución. Físico
  visible en el muñeco y en el 3P (**sin comprobar que el avatar tenga blendshapes**). Toca `ConsumableSpec` (ADR-030).
  Abierto: ¿el físico cambia en horas de juego o en días reales? Con sesiones de 45–90 min, días reales no se ven.
- **Research en ordenador**: sentarse en un PC de oficina y navegar un «internet de los Backrooms» para aprender recetas
  avanzadas; se craftean luego en el banco. Lo básico (venda, antorcha, férula) se sabe sin investigar. Investigar
  **desbloquea**, no informa: un wiki de fans existirá igual. La web del juego se genera del mismo JSON de recetas que
  usan servidor y cliente (`CraftingRecipesOracle`); **nada de navegador real embebido** (plugin de pago, sin conexión no
  va, superficie de seguridad). Las recetas aprendidas van al guardado: ADR.
