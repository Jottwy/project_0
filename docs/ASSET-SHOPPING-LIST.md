# Lista de compra de assets — Backrooms Survival MMO

> Para ir a la tienda con una lista, no a mirar. Ordenada por impacto visual por euro.
> Los términos de búsqueda van en inglés (la tienda lo está). Precios: órdenes de
> magnitud, verifícalos — se mueven y hay rebajas constantes.
> Fecha: 2026-08-25.

## Reglas antes de pagar

Tres filtros. Si un asset falla uno, no lo compres por bonito que sea el tráiler.

1. **Escala.** El mundo va en tiles de 2,5 m. Una pieza que no encaje en esa rejilla canta
   a la primera. Mira las dimensiones, no las capturas.
2. **Un solo pase de material encima.** El "asset flip" no viene de comprar, viene de
   mezclar cinco packs con cinco temperaturas de color. Todo lo que entre se repinta con
   tus materiales y tu luz. Descarta sin pena lo que no admita ese pase.
3. **Colisión.** Cualquier pieza que pase por STP necesita el collider en la RAÍZ del
   prefab. Un collider en un hijo bloquea la construcción en silencio.

Y una regla de proceso: **commit antes de importar**. Importar packs sobre esta escena ya
pisó las escenas demo enteras una vez. Desmarca todo lo que no sea arte al importar.

---

## Compra 1 — Luz, materiales y decals *(la más importante, y la más barata)*

En este género el arte no está en los modelos: está en la uniformidad. Moqueta repetida,
fluorescente zumbando, la misma pared amarilla otra vez. Esto es lo que hace que un
escritorio comprado se lea como tuyo.

| Qué | Buscar | Nota |
|---|---|---|
| Pack de decals de deterioro | `decal grunge pack URP`, `water stain decal` | Manchas de humedad, cercos de gotera, rozaduras. Máximo impacto por euro de toda la lista. |
| Texturas de suelo | `carpet texture pack PBR`, `office carpet` | Moqueta institucional, linóleo, baldosa de sala técnica. |
| Texturas de pared/techo | `drywall texture`, `ceiling tile PBR` | Placa de techo desmontable — la textura más reconocible del género. |
| Fluorescentes | `fluorescent ceiling light`, `office light fixture` | **Con difusor, sin difusor, uno colgando, uno roto.** Que traiga las variantes o no sirve. |

Orden de magnitud: gratis a ~40 € cada uno. Presupuesto realista: **50-80 €** el bloque.

---

## Compra 2 — Props de oficina

Sirve al Level 4 y a `ZONE_OFFICE` de Level 0.

**Núcleo (sin esto no se lee a oficina):**
- Escritorio + silla giratoria
- Mampara de cubículo *(resuelve la compartimentación visual de las cajas de 30 m del
  Level 4 aunque no toques el generador)*
- Archivador de 2 y 4 cajones
- Estantería metálica de archivo
- Puerta de oficina con marco (normal, doble batiente, cortafuegos con barra antipánico)

**Relleno (nadie lo mira dos veces, pero su ausencia se nota):**
- Monitor CRT, teclado, teléfono de sobremesa
- Fotocopiadora / impresora
- Fuente de agua *(icónica, y rima con la mecánica de sed)*
- Máquina expendedora
- Pizarra blanca y corcho con papeles
- Planta de plástico
- Mesa de reuniones con sillas · sofá de sala de espera
- Papelera, torre de cajas de cartón, perchero, extintor
- Sillas apiladas, mesa plegable

Buscar: `office props pack`, `modular office interior`, `cubicle office kit`.
Orden de magnitud: **40-90 €** un pack decente que cubra el 80 % de la lista.
Preferible UN pack que cubra mucho a cuatro packs que se contradigan entre sí.

---

## Compra 3 — Señalética y ambiente industrial

Barato y es lo único que da sensación de que el sitio tuvo un propósito antes.

- Señales EXIT, números de sala, flechas, planos de evacuación
- Rejillas de ventilación y conductos *(rompen el techo plano, hoy una superficie muerta
  en toda la región del Level 4)*
- Tuberías, bajantes, cableado suelto, cuadros eléctricos
- Escaleras y barandillas
- Carro de limpieza, fregona y cubo · barriles · palés
- Rejillas de suelo y desagües · radiadores
- Muebles tapados con sábanas

Buscar: `exit sign pack`, `industrial props pack`, `ventilation duct modular`,
`warning sign pack`. Orden de magnitud: **20-50 €**.

---

## Compra 4 — Personaje modular

**Compra CONTENIDO, no un SISTEMA.** El editor en sí son dos días de trabajo sobre el
sistema de ropa que ya funciona y ya se replica por red. Lo que cuesta dinero es la
variedad de prendas: hoy el guardarropa entero son **2 cabezas, 3 torsos, 3 piernas,
1 calzado**.

Qué exigir, en este orden — si falla uno, descarta:

1. **Prendas como mallas skinned separadas** que se activan y desactivan. Si el asset
   fusiona una malla en runtime, no encaja: la ropa viaja por red como 4 enteros.
2. **Rig humanoid de Unity.** Si no, tus animaciones no le sirven.
3. **La configuración cabe en pocos enteros pequeños.** Cuarenta sliders y colores libres
   no caben en el wire actual, y ese rediseño de protocolo es tuyo, no del vendedor.
   Ningún creador de la tienda resuelve multijugador — todos asumen un jugador.

Buscar: `modular character pack`, `modular survivor clothing`, `interchangeable clothing
humanoid`. **NO** buscar `character creator system` — eso trae su propio personaje y
sustituye la capa entera (ropa de STP, agarre en `Hand.R`, ragdoll, re-horneado del
proxy). Eso no son días.

Orden de magnitud: **30-80 €**.

⚠️ Al ampliar el guardarropa hay que re-hornear el prefab del avatar remoto. El arreglo va
en el **builder**, nunca solo en el asset — re-hornear ya borró los 11 flags de kinemática
del ragdoll una vez y el robapieles perdió la piel.

---

## Compra 5 — Audio

Se olvida siempre y en este género pesa tanto como la luz.

- Zumbido de fluorescente (bucle) · parpadeo eléctrico
- Zumbido de aire acondicionado / ventilación
- Goteo, ecos de pasillo, crujidos de estructura
- Impulsos de reverb de espacio grande y cerrado

Buscar: `ambient horror sound pack`, `room tone loops`, `industrial ambience`.
Orden de magnitud: **20-40 €**.

⚠️ Autora el ambiente en el orden de **0,05**, no de 0,3 — muy por debajo de los SFX. Y
toda `AudioSource` nueva tiene que enrutarse a su grupo del mixer: sin
`outputAudioMixerGroup` sale por Master y ninguna bajada de volumen la corrige.

---

## Lo que NO comprar

- **Creadores de personaje con su propio runtime.** Ver compra 4.
- **Packs de props "AAA" de 300 €.** No es donde está tu problema.
- **Cualquier cosa que no puedas repintar** con tus materiales.
- **Modelar tú** sillas, escritorios, archivadores, extintores, papeleras. Es relleno de
  fondo. Compra.

Donde SÍ modelar tú: lo que solo tu juego tiene y define su identidad — el robapieles, y
como mucho una o dos piezas que salgan en todas las capturas.

---

## Orden y presupuesto

| # | Bloque | Presupuesto | Por qué ahí |
|---|---|---|---|
| 1 | Luz, materiales, decals | 50-80 € | Sin esto ningún otro pack salva la escena |
| 2 | Props de oficina | 40-90 € | Level 4 y `ZONE_OFFICE` dejan de ser cajas vacías |
| 3 | Señalética e industrial | 20-50 € | Barato, y da propósito al sitio |
| 5 | Audio | 20-40 € | Pesa tanto como la luz, se olvida siempre |
| 4 | Personaje modular | 30-80 € | Último: es identidad social, no loop |

**Total: 160-340 €**, y con rebajas bastante menos.

Ninguno de los cinco antes que el eslabón de crafteo. Un jugador perdona ir vestido igual
que su amigo; no perdona que minar no sirva para nada.

## Trabajo de código que habilita las compras

Barato, pero sin esto los props comprados no se colocan donde toca:

- **Tabla de props por `zone_kind`.** Hoy la tabla es por CAPA
  (`LayerVisualConfig.PropEntry[]`), no por zona: un chunk de oficina no coloca ni un
  escritorio. Es lo que hace que la oficina tenga muebles de oficina.
- **Piezas autoradas:** ≤ 6×6 tiles y **≥ 2 vanos**, o nacen incomunicadas.
