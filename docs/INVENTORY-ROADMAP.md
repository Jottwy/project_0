# INVENTORY-ROADMAP.md — El inventario del superviviente

> Diseño, no código. Abierto el 2026-09-12 con Joel. Lo marcado **DECIDIDO** es de Joel; lo marcado **IDEA** está
> anotado para no perderlo y NO está aprobado. Nada aquí sustituye a un ADR: todo lo que toque wire, schema de
> guardado o autoridad lleva el suyo antes de escribir código (CLAUDE.md, regla 7).

Maquetas (privadas, en claude.ai):
- **Vigente — Inventario del superviviente** (pantalla única con todo lo decidido): https://claude.ai/code/artifact/a6e37c3e-3c76-4cd7-a5ff-2c7be05d96e2
- **Laboratorio de casillas** (anatomía de la casilla, consumo por uso, chaqueta que se rompe por zonas): https://claude.ai/code/artifact/954d887c-bbd3-4705-820f-575533665636
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
- **La funda sale del equipo** (sustituye a «2 de base + 2 con cinturón», 2026-09-13): desnudo 0; trabilla del
  pantalón (cargo, de trabajo) +1; cinturón de herramientas +2 (sin bolsillos, ocupa la cintura y compite con la
  riñonera); chaleco o arnés +2; anilla lateral de la mochila de montaña +1, **solo herramientas largas y desenfundado
  lento**. Tope 6 (teclas 1–6). Cada hueco muestra de qué prenda sale; lo que cuelga se va con la prenda (D5). Crece en la
  barra fija de abajo, no en el centro.
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
- **Lista cerrada de propiedades del equipo**: huecos, funda, reparto, protección, abrigo. Como mucho dos por prenda. Nada de
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

## Desgaste, consumo y rotura (2026-09-12, laboratorio de casillas)

### D9 — Desgaste en porcentaje continuo — DECIDIDO
Herramientas y ropa tienen condición 0–100 % continua (no cinco estados discretos). Cada uso o golpe resta; la
barra de la casilla es continua. Recomendado, sin confirmar: entero en pantalla y decimales ocultos por debajo.
A 0 % nada desaparece: queda **inservible y se desmonta** en piezas (tela, chatarra, muelle).
Vendor: ya hay propiedad de durabilidad en los items, `WieldableDurabilityDepleter`, `RepairStation` y `DismantleAction`.

### D10 — Los consumibles se gastan por uso y el icono lo enseña — DECIDIDO
Cinta, hilo, gas, agua: se gastan en porcentaje según el tamaño del trabajo, no por unidades (el vendor gasta
unidades: esto es propio). **El dibujo de la casilla cambia con lo que queda**: el rollo de cinta adelgaza, el nivel
de agua y de gas se ve a través del envase, el carrete de hilo se vacía; la herramienta se oxida y se mella.
La cinta a 0 % deja el **tubo de cartón**; la botella vacía queda para rellenar.
Cifras de laboratorio: parche pequeño −10 % de cinta, grande −25 %; coser un corte −8 % de hilo, un desgarro −15 % y 1 tela.

Anatomía de la casilla, siempre en el mismo sitio: (1) dibujo según lo que queda, (2) medidor de tamaño (D7),
(3) cantidad solo si se apila (D8), (4) barra continua — condición o cantidad restante —, (5) una marca de estado
como mucho (parcheado, roto, húmedo, sucio, vacío).

### D11 — La ropa se rompe donde recibe el golpe — DECIDIDO en dirección, sin ADR
Joel lo quiere, sabiendo que es caro. Todas estas consecuencias entran:
- Cada prenda se divide en **zonas con su protección** (chaqueta de laboratorio: pecho izq./der., costado izq./der.,
  manga izq./der.; protección base 20 %).
- Un disparo o una puñalada **dentro de un bolsillo lo destroza y su contenido cae**; fuera de un bolsillo solo abre
  agujero. Laboratorio: desgarro deja la zona a 0 % de protección, corte al 40 %.
- **La cinta tapa el agujero y devuelve parte de la protección (60 % de la base), pero NO devuelve el bolsillo** (Joel).
- Reparar con materiales; inservible se desmonta.

Pendiente de Joel (recomendación entre paréntesis):
1. ¿Coser devuelve el bolsillo? (sí, con hilo y, si es desgarro, tela: arreglo rápido = cinta, arreglo bueno = coser; protección al 90 %).
2. ¿Lo que cae por el agujero va al suelo del mundo en el acto? (sí, se oye y se puede recoger).
3. ¿Qué decide si el golpe dio en el bolsillo? (probabilidad dentro de la zona del cuerpo según lo que cubre el
   bolsillo, antes que el punto exacto sobre la malla).
4. ¿La condición afecta a algo más que la protección (abrigo, reparto)? Cada efecto entra en la lista cerrada de D6.

**Depende del cuerpo por zonas**: hoy hay un solo hitbox en el tronco; sin saber que el golpe fue en el costado
izquierdo, la ropa no puede romperse ahí. D11 va detrás de ese ADR.

### En el modelo 3D — PROPUESTO
**El estado manda y el modelo lo pinta.** La prenda guarda por zona sana/corte/desgarro/parcheada/cosida y por
bolsillo entero/roto: unos pocos bits, que son la verdad de juego, guardado y red. El shader dibuja a partir de ese
estado y de una semilla por impacto; ningún cliente necesita la posición exacta (mismo patrón que ADR-143).

| Técnica | Veredicto |
|---|---|
| **Falso agujero**: el shader oscurece con borde deshilachado por ruido; cinta y costura como capas encima | **Empezar aquí.** A distancia de juego se lee como agujero |
| Recorte real (alpha clip) | Solo si el falso se queda corto. **Sin comprobar** si el cuerpo bajo la ropa está oculto (se vería el vacío) |
| Piezas intactas/rotas pre-modeladas | Mucho arte por prenda; solo prendas icónicas |
| Decals de URP | **No**: en mallas animadas el decal resbala |
| Tela simulada o malla que se rasga | Fuera de alcance |

Cómo sabe el shader dónde está cada zona sin depender de las UV (las de Meshy suelen venir desordenadas): **color de
vértice por zona** pintado en Blender; centros de los agujeros como array corto (≤16 por prenda) vía
`MaterialPropertyBlock`; **pose de reposo del vértice guardada en un canal extra al importar** (`AssetPostprocessor`)
para que la marca no resbale con la animación.

Condicionantes del proyecto:
- **Primera persona**: las mangas viven dentro de cada wieldable (memoria `fp-arms-live-inside-each-wieldable`) y todo
  lo colgado de los brazos 1P warpea (regla 14, ADR-077 enm. 2). El shader de daño necesita variante warpeada y el
  estado se reaplica a los brazos de cada arma al cambiar.
- **Red**: nunca en la pose (65 B, medida al byte); mensaje fiable solo cuando cambia. Entra en el guardado: ADR y
  bump de wire, junto a la enmienda de ADR-022.

Troceado:
- **A** — solo inventario: zonas, bolsillos rotos, cinta y costura en casillas y muñeco. Sin 3D.
- **B** — spike visual local: falso agujero, cinta y costura en **una sola prenda** (chaqueta de trabajo), 3P y brazos
  1P, sin red. Decide si merece la pena.
- **C** — los demás lo ven: mensaje fiable, guardado, ADR.
- **D** — recorte real o piezas rotas en prendas concretas, solo si el falso agujero no basta.

## Pulido del reparto — rondas con Joel (2026-09-12) — DECIDIDO (aprobado sobre la maqueta v3, 2026-09-13)
Greybox de referencia a 1920×1080: https://claude.ai/code/artifact/1d0e788e-5e30-4f44-a990-6a7d2fe049b1 ·
comparativa del centro: https://claude.ai/code/artifact/28fb1654-d17f-4ebe-b74b-168f475ecedd

Rejilla: margen seguro 48 px; Personaje 440 px fijo; Lo que llevas 920 px flexible (único desplazamiento); Alrededor
400 px fijo; separación 32 px; barra inferior 132 px; casilla 72 px (hueco 8, mínimo 56); slot del cuerpo 80 px;
cabecera de ventana 36 px. Sin contenedor abierto, su ventana queda como hueco atenuado: nada salta de sitio.

**Centro (Lo que llevas)**
- Reparto **B**: la mochila arriba a todo el ancho. Con la mochila de montaña hay que bajar; se aceptó a cambio de la
  mochila a tamaño completo (se descartaron lado a lado, casillas que encogen y compartimentos por pestañas).
- Debajo, **cada prenda con bolsillos es una fila** a todo el ancho: nombre, ocupación y tamaño máximo a la izquierda,
  huecos en línea a la derecha. Si una prenda tiene más huecos, la fila se alarga; plegada ocupa una línea.
- Secciones **plegables a mano** con clic en la cabecera, recordadas entre aperturas. Una sección plegada **se abre sola
  mientras llevas cogido algo que cabe en ella** y se vuelve a plegar al soltarlo.
- Foco: **clic en el slot del muñeco** (otro clic o «← Todo» vuelve); enseña **solo esa prenda, en grande**.

**Personaje**
- **Reutilizar la vista previa del vendor**: `CharacterPreviewUI` (cámara a RenderTexture) y
  `CharacterPreviewRotationHandlerUI` (arrastrar gira, la rueda acerca). No se diseña una nueva.
- 9 slots **a la altura de su parte, con línea fina** hasta ella.
- Conmutador **Ropa | Heridas** en la cabecera de la columna.

**Alrededor**
- Objeto: **tooltip corto al pasar** (nombre y condición; `ItemTooltipUI`) **+ panel fijo abajo al seleccionar** con el detalle.
- Acciones (usar, reparar, desmontar, tirar, dividir pila) como **botones en ese panel** (`ItemActionsUI`). El clic
  derecho queda reservado para poner y quitar prendas.
- Varios contenedores al alcance: **pestañas en la misma ventana**; el suelo sigue debajo.

**Barra inferior**
- **Manos = transporte**, aparte de la funda: 2 huecos para lo que no se guarda. Lo voluminoso ocupa las dos (se
  dibuja como un bloque) y **bloquea la funda** mientras lo cargas.
- Funda: los huecos que dé el equipo (D4), hasta 6; los que no tienes salen tachados.
- Carga: **barra continua con kg reales y dos marcas de tramo** que se desplazan con el reparto (D6).

**Sin mochila y al empezar**
- Sin mochila puesta desaparece su sección: quedan los bolsillos de la ropa, las manos y la funda que den las prendas.
  Desnudo = solo 2 manos.
- **Una mochila que no llevas puesta sigue siendo contenedor**: en el suelo o dentro de otro contenedor aparece como
  pestaña más en Alrededor («Mochila · suelo») y se puede sacar y meter sin ponérsela. En las manos va cerrada.
- **No se aparece desnudo**: camiseta (1 bolsillo diminuto) y vaqueros (2 pequeños), sin mochila ni funda.

**HUD en juego (inventario cerrado)**
- Punto de mira; **arco de aliento bajo el punto de mira, solo mientras se gasta o recupera**; aviso de interacción.
- **Funda compacta abajo al centro que se atenúa a los 3 s** (aparece al cambiar de herramienta o recoger).
- **Lo recogido, arriba a la derecha**: 3 líneas como mucho, con destino («+ Venda ×2 → chaqueta», «✕ Botella: no cabe»).
- Viñeta de urgencias sin números. El reloj de muñeca vive en el brazo 1P, no en el canvas.
- Con `TAB` abierto el HUD de juego se oculta salvo la viñeta.

## D12 — Estilo visual: «Bajo el fluorescente» — DECIDIDO (2026-09-13)
Maqueta: https://claude.ai/code/artifact/2dd29608-2b1b-4ca8-9637-0886eb24c976 (mismo reparto aprobado, otra piel).
La interfaz parece de oficina bajo un tubo fluorescente, no de videojuego ni de película de terror.
- **La luz del nivel guía**: selección y huecos válidos brillan con el verde pálido de los fluorescentes; el tubo
  parpadea de vez en cuando (desactivable por accesibilidad).
- **Etiquetas Dymo**: títulos de columna, pestañas, tooltip, botones y badges en cinta embosada negra, ligeramente
  torcida; los «no se puede» en cinta roja.
- **Huecos hundidos**: rebaje oscuro con sombra interior, sin marcos ni biseles; el objeto (render) descansa dentro.
- **Cámara barata**: grano, viñeta y tinte amarillo verdoso **también sobre la interfaz**.
- **Etiqueta de almacén** (papel con agujero) para el panel del objeto: lo único claro de la pantalla.
- **Barra inferior con objetos**: la funda es una cincha de nylon; la carga, una regla con marcas.
- **Evitar**: neón, glitch exagerado, tipografías de terror.

Tokens de la maqueta: fondo `#15140C`, tinta `#EBE7D3`, luz fluorescente `#DFE9B8`, cinta Dymo `#121212` sobre
`#F1F0E8`, cinta roja `#8F231C`, papel de almacén `#E9E2C7`. Tipos: Barlow Semi Condensed (títulos y nombres) e IBM Plex
Mono (cintas, cifras); ambas OFL, requieren generar sus SDF de TextMeshPro.
**Sin comprobar en Unity**: un canvas Screen Space Overlay no recibe el post-proceso de URP, así que grano, viñeta y
tinte sobre la UI piden overlay propio o canvas en Screen Space Camera.

**Del vendor se reutiliza** (confirmar pieza a pieza al implementar): `ItemSlotUI`, `ItemPropertyProgressBarDisplay`
(barra de condición), `ItemWeightDisplay` / `InventoryWeightDisplayUI`, `ItemDragger` / `ItemDragHandler`,
`ItemSelector`, `ItemTooltipUI`, `ItemActionsUI`, `HotbarUI` (funda), `StorageStationUI` (contenedor),
`RepairStationUI`, `CraftingUI`. Todo vía hook externo o subclase fuera del ensamblado vendor, nunca editándolo.

## Implementación (estado)
- **Rebanada 0** (`116baf75`): tema, sprites y SDF. `BackroomsUiThemeTests` 4/4.
- **Rebanada 1a**: variante `Assets/Prefabs/UI/BR_UI_Player.prefab` de `STP_UI_Player` con la piel aplicada por
  `BackroomsInventoryUiBuilder` (fondo `BR_Backdrop`, fuentes, huecos, cintas, papel del inspector, cincha de la funda,
  regla de carga) y el `GameMode` de `STP_Showcase` apuntando a ella (4 líneas de override). Captura sin Play:
  `Backrooms/UI/Capturar inventario`. **Sin ver en Play todavía.**
- **Rebanada 1b**: `Relayout` en el builder mueve los tres grupos del vendor a las columnas del greybox (personaje
  440 px izquierda; mochila y carga en el centro; inspector anclado FUERA de su rect en la columna derecha, 400 px;
  funda en la barra inferior). Dos trampas medidas: reparentar dentro de un prefab anidado NO se guarda como override
  (Unity lo descarta sin avisar); un override viejo de la variante no se deshace al reaplicar, hay que escribir el valor.
- **Tubería** `tools/dev/InventoryUiPipeline.sh`: compile check (con los .cs nuevos que la foto del csproj no ve) →
  Unity headless tema + variante + captura (`BackroomsUiPipeline.RunAll`, sin `-nographics`) → tests
  (`BackroomsUiThemeTests`, `BackroomsInventoryVariantTests`) → limpieza. Retira lockfiles huérfanos. Logs en
  `Builds/uipipeline/` (nunca bajo `Temp/`: el headless lo borra).
- **Pendiente**: feedback de selección con la luz del tubo (`SelectableButtonFeedback` por referencia serializada),
  papel del inspector a la altura del greybox, ver TODO en Play (preview 3D, fondo al inspeccionar, tooltip).
- **Escena de pruebas** (Joel, 2026-09-13): mientras dura la migración, la variante se prueba en
  `Assets/Scenes/BR_InventoryTest.unity` (`Backrooms/UI/Build Inventory Test Scene`) y **`STP_Showcase` sigue con el
  inventario del vendor**; un test lo exige. Tubería de una pasada: `bash tools/dev/InventoryUiPipeline.sh`.
- **Rebanada 1c** (2026-09-13): `Relayout` sigue la rejilla del greybox al píxel a 1920×1080 — cabeceras de 36 px a todo
  el ancho (PERSONAJE, LO QUE LLEVAS, ALREDEDOR) con su caja 8 px debajo hasta y=876; sección «Espalda · Mochila» con
  casillas de 72/8; Alrededor = estación (492 px) + panel del objeto (284); barra inferior Manos (placeholder, 2 huecos) ·
  Funda · Carga · Atajos en x 48/256/808/1564; muñeco como franja central del render (uvRect, sin estirar). Conmutador
  **Ropa | Heridas** placeholder (`BackroomsBodyViewToggle`, 19 zonas de ejemplo, sin ADR). Trampa: los hijos del vendor
  no se reordenan en la variante; lo nuestro que va DETRÁS se mueve con `SetSiblingIndex`. Iteración con el editor
  abierto: la captura abre escena ADITIVA y vuelca los rects a `Builds/Captures/inventario_tab.txt`.
  Falta del greybox: 9 slots del cuerpo con líneas (hoy 5, ADR-022), filas de bolsillos por prenda (D4), «Todo | Lista» y
  contador de huecos en la cabecera, pestañas de contenedor y suelo, marcas de tramo en la carga (D6).
- **Ajustes de Joel (2026-09-13)** — DECIDIDO: la columna derecha es un conmutador **ALREDEDOR | CRAFTEO** (misma
  ventana; abrir una estación elige la vista, sin estación se respeta la última; `BackroomsAroundViewToggle`). La funda se
  llama **Cinturón** y va **centrada en pantalla** (es la misma pieza en TAB y en juego). La **carga total** va en una línea
  a la derecha de la cabecera de Lo que llevas («CARGA ▬ x / 40 KG»); **cada prenda tendrá su propia barra** en su fila.
  La barra inferior queda Manos · Cinturón · Atajos.

## Rondas del 2026-09-13 (tarde) — DECIDIDO por Joel
- **D5 confirmado**: al quitarte una prenda o mochila, lo de dentro se queda dentro (estilo DayZ), no cae (Unturned).
- **D6 enmendado — máximo por prenda**: cada prenda y mochila tiene huecos Y un máximo de kg propio que **impide** meter
  más en ella (se ilumina en rojo con el motivo). El total del cuerpo, arriba en la cabecera, sigue sin impedir: solo frena.
  La cabecera suma el peso de todo lo que llevas; cada fila de prenda enseña el suyo contra su máximo.
- **Reparto confirmado**: mochilas con los mismos huecos se diferencian por el reparto (mueve los tramos), nunca
  reduciendo kg.
- **D13 — secciones plegables y reordenables a gusto**: cada sección (mochila, cada prenda) se pliega con clic en su
  cabecera y se reordena arrastrándola; orden y plegado se guardan **en el PC** (preferencia de interfaz, sin servidor).
- **Mochilas de prueba**: varios tipos (básicas y detalladas) que solo van en la espalda, con distintos huecos, máximo
  y reparto, usando una malla existente de placeholder mientras no haya modelo.
- Orden de trabajo: **ADR-147** (PROPUESTA: contenedores precreados al final y capados por lo equipado, contenido que viaja con la prenda) → mochilas de prueba →
  bolsillos por prenda → secciones plegables/reordenables (necesitan varias secciones para tener sentido).
- **Prototipo de mochilas (ADR-147 enm. 1, 2026-09-13)** — solo en `BR_InventoryTest`, sin guardado ni red: tres mochilas de
  prueba (`BR_Cloth Bag` 6 / `BR_Office Backpack` 18 / `BR_Hiking Backpack` 27, icono y pickup de camiseta) con tag
  «Back Equipment»; el jugador de la escena lleva `Back` y `BackStorage` al final de sus contenedores (override de escena,
  `STP_Player` intacto); `WornCapacityRestriction` capa huecos y kg por la mochila puesta y no deja meter una mochila en otra;
  `BackroomsWornStorage` empaqueta/desempaqueta al quitar/poner (en memoria, inerte con backend); en la variante, hueco de
  espalda real y sección «ESPALDA · …» que enseña solo los huecos que da la mochila. Los 30 del vendor quedan como
  «BOLSILLOS · PROVISIONAL». Tests `BackroomsBackpackPrototypeTests` (condiciones de la auditoría incluidas).
  Pendiente: probar en Play; hueco de peso declarado (un paquete no suma kg).

