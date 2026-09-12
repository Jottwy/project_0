# AUDIO-PROPAGATION-ROADMAP.md — sonido que dobla esquinas y eco a distancia

> Estado: PROPUESTO, 2026-09-12. Nace de una petición de Joel en la misma sesión que cerró R3-R6
> (luces sin fuga entre paredes, aislamiento de audio por planta real de WG3, oclusión por cuenta
> de paredes, pasos por superficie, presupuesto de sombras). Ninguna fase de aquí está
> implementada todavía; esto es el análisis de viabilidad que Joel pidió antes de decidir cuánto
> construir.

## Lo que ya existe hoy (12-09) y lo que NO

**Ya existe**, tras la tanda de esta sesión:
- Oclusión CERCANA por cuenta de paredes (`AudioOcclusionMath`, ≤ 18 m): un teléfono o un zumbido
  suena progresivamente más tapado según cuántos tabiques cruza el rayo oyente↔fuente en línea
  recta. No rodea esquinas: si el rayo directo está limpio, suena claro aunque el camino real —
  siguiendo el pasillo— sea mucho más largo.
- Reverb por SALA/ZONA (`ReverbMixerDriver`): un preset de reverb por `zone_kind`, sin conocer la
  planta ni la distancia real al sonido.
- Aislamiento de audio por PLANTA real de WG3 (R6, este mismo cierre): el zumbido y el ambiente ya
  no suenan a través de un forjado.

**No existe nada de esto:**
- Ningún sistema trata un evento sonoro FUERTE Y LEJANO (disparo, demolición, un grito de
  robapieles) de forma distinta a una fuente normal. Medido en este cierre: todo `AudioSource` con
  `maxDistance` explícito en el proyecto vive entre 7 y 18 m
  (`FluorescentHumDirector`, `OfficeAmbienceDirector`, `SpraySfx`) — nada apunta a "que se oiga
  desde lejos, apagado, como un eco".
- Ninguna propagación que rodee una esquina: la oclusión de hoy es un rayo recto, no un camino por
  el grafo de salas.
- Ningún middleware de acústica (Steam Audio, Meta XR Audio, Wwise): todo es `AudioSource` +
  `AudioMixer` + los filtros vanilla de Unity (`AudioLowPassFilter`, el efecto `SFX Reverb`).

## Las dos peticiones, y por qué son cosas distintas

1. **"Que entre plantas solo se oiga lo que hay en la capa"** — YA CERRADO en este mismo cierre
   (R6): el zumbido y el ambiente de oficina separan por `Wg3StoreyLayers.RawStoreyOf`, el mismo
   índice que ya reparte la luz.
2. **"Propagación tipo Siege, y eco para cosas que pasan muy lejos"** — esto es lo que cubre este
   roadmap. Es un problema de ESCALA distinta: no son 18 m con un tabique de por medio, son
   decenas o cientos de metros, probablemente fuera del radio de streaming activo, y el objetivo
   no es "oclusión correcta" sino una SENSACIÓN — que algo lejano se lea como lejano sin que el
   jugador pueda localizarlo con precisión, que es justo lo que hace un eco real.

## Fases

### Fase 0 — Diagnóstico de emisores candidatos (medio día, sin código de producción)

Localizar TODO evento que debería oírse lejos y no lo hace hoy: disparo de armas, demolición y
construcción STP (ADR ya cierra la autoridad, falta el sonido), la vocalización del robapieles
(`vocal_seq`, ADR-038 — **NO tocar sus seis invariantes**, esto es sólo escuchar, no modificar el
sistema), cualquier explosión si existe. Para cada uno: volumen de origen, `maxDistance` actual,
curva de rolloff (lineal/logarítmica). Sin este barrido, cualquier diseño de "eco a distancia" es
teórico — literalmente no se sabe todavía CUÁNTOS emisores tocaría ni si ya tienen algo parecido.

**Gate de salida**: una tabla de emisores con sus números actuales, en este mismo documento o en
`docs/SESSION-LOG.md`.

### Fase 1 — Eco de largo alcance, sin geometría (2-3 días, IMPLEMENTABLE ya)

La pieza barata de verdad. Cuando un evento "fuerte" ocurre (umbral de volumen de origen, no todos
los sonidos lo disparan), además de su `AudioSource` normal con su alcance de siempre, se lanza UN
sonido secundario:
- **Sin espacializar por la curva de distancia normal de Unity**: una curva propia donde el
  volumen cae con la distancia pero nunca llega a exactamente cero hasta muy lejos (cientos de
  metros) — la sensación de "algo pasó, muy lejos" en vez de un corte duro.
- **Sólo graves**: low-pass agresivo (< 150 Hz). Es lo que de verdad viaja lejos en el mundo real
  y lo que MÁS BARATO simula "distancia" sin necesitar trazar el camino real del sonido.
- **Delay proporcional a la distancia** (340 m/s: a 200 m, ~0,6 s de retraso). Dramático, jugable,
  y gratis de calcular — sólo una resta y una división.
- **Reverb largo** propio (un preset de cola larga en `SFX Reverb`, o un bus dedicado si el
  existente no da el rango).

No necesita saber NADA de geometría entre emisor y oyente — sólo la distancia recta y un guion de
sonido. Ese es su límite también: todos los eventos lejanos sonarán parecido entre sí a menos que
se varíe el timbre por tipo de evento (arma vs demolición vs robapieles).

**Gate de salida**: un evento de prueba (disparo o explosión sintética) audible y con la sensación
de lejanía a 100-300 m, verificado en Play.

### Fase 2 — Generalizar la cuenta de paredes de hoy a largo alcance (1 semana)

`AudioOcclusionMath` (de este mismo cierre) ya sabe contar cuántas paredes cruza un rayo recto.
Escalarlo a distancias largas exige:
- Cachear el resultado (un rayo de 300 m con `RaycastNonAlloc` no es gratis si se repite cada
  frame para muchos emisores lejanos a la vez) — recalcular sólo cuando emisor u oyente se muevan
  lo suficiente, no cada frame.
- Sigue sin rodear esquinas: es "cuántas paredes hay en la línea recta entre aquí y allá", que ya
  es mejor que nada pero no es lo que hace Siege.

**Gate de salida**: un evento lejano detrás de tres o cuatro salas se oye más apagado que el mismo
evento a la misma distancia en línea abierta.

### Fase 3 — Propagación por esquinas: la pieza cara (semanas, DECISIÓN GRANDE)

Esto es lo que de verdad separa "un rayo cruza N paredes" de "el sonido dobla la esquina del
pasillo", que es la parte de Siege que de verdad impresiona. Exige, escrito a mano:
- Un grafo de conectividad sala-a-sala (¿reutilizable de algo que ya calcule alcanzabilidad para
  navegación en WG3, tipo `nav_reach`? — a comprobar en Fase 0 extendida antes de escribir nada).
- Un pathfinding acústico: la ruta más corta por ese grafo desde la fuente hasta el oyente, con una
  pérdida acumulada por cada tramo/vano cruzado.
- Reproducir el sonido "doblado" — desde el punto de la esquina más cercana al oyente en esa ruta,
  no desde la posición real de la fuente. Es el truco estándar de audio por portales, y es la
  parte que de verdad cambia el comportamiento (el sonido parece venir de la esquina, no del
  origen real, que es exactamente cómo se percibe un sonido que dobla un pasillo).

**Alternativa real, y probablemente mejor**: adoptar un middleware de acústica geométrica (Steam
Audio es gratis y de código abierto parcialmente, tiene oclusión por geometría y reverb por
convolución de verdad) en vez de escribir el grafo y el pathfinding a mano. Coste de integrarlo:
un plugin nativo por plataforma, aprender su API, probablemente 3-5 días — mucho más barato en
tiempo que escribirlo, a cambio de una dependencia nueva y de que el proyecto deja de ser
vanilla Unity Audio para este sistema.

**Esta fase NO se empieza sin decisión explícita de Joel**, y sin haber visto en juego el
resultado de las fases 0-2 — puede que la sensación de "lejos con eco" de la Fase 1 ya baste para
lo que se busca y esto nunca haga falta.

## Recomendación de orden

1. Fase 0 (medio día) y Fase 1 (2-3 días): esta sesión o la próxima, retorno inmediato y barato.
2. Fase 2 (1 semana): extensión natural de lo que ya se escribió hoy (`AudioOcclusionMath`).
3. Fase 3: aparcada hasta ver 1 y 2 en juego. Si hace falta, decidir ENTONCES entre escribirla a
   mano o adoptar Steam Audio — no antes, con datos de juego reales delante y no en abstracto.

## No tocar mientras se construye esto

- Los seis invariantes del robapieles (ADR-038), en particular `vocal_seq` — la Fase 0 sólo
  ESCUCHA ese sistema para medirlo, no lo modifica.
- Los valores de oclusión ya dados por buenos hoy (`OccludedVolume`/`CutoffOccluded` por
  director) — la Fase 1 es un sistema PARALELO, no una extensión de esos números.
