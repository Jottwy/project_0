# Base de rendimiento — medición 2026-08-14

> **Esto son MEDIDAS, no estimaciones.** Reproducibles:
> ```
> cd backend && cargo test --release roster_relay_cost -- --ignored --nocapture
> ```
> La sonda vive en `backend/src/network/roster.rs` (`roster_relay_cost`, `#[ignore]`). No afirma
> nada: imprime. Si cambia el coste, se vuelve a correr y se actualiza este documento.
>
> Encargo de Joel (2026-08-13): "si queremos escalar más rendimiento habrá que ver cómo
> optimizamos para el MMO todo lo que tenemos al máximo". Decisión suya: **medir primero**.

## Resumen en una frase

El cuello de botella no es el render, ni la física, ni el worldgen: es que **el host difunde los
cinco rosters ENTEROS a 10 Hz a todos los peers, sin filtro de distancia y sin comprobar si algo
ha cambiado**. Es el único coste del proyecto que crece con el tamaño del mundo **y** con el
número de jugadores a la vez, y con una base modesta ya excede la subida de una conexión
doméstica.

## Lo medido

`backend/src/game_loop.rs:1485` — cada 10 Hz, si hay peers, el host emite en bloque:
`broadcast_stp_items`, `broadcast_stp_buildings`, `broadcast_stp_carryables`,
`broadcast_stp_harvestables` y `broadcast_corpses`. Ninguno mira si su roster cambió.

Tamaño serializado por elemento (MessagePack, `to_vec_named`):

| struct | bytes |
|---|---|
| `StpBuildingInfo` (1 material) | 96 |
| `StpItemInfo` | 73 |
| `StpCarryableInfo` | 52 |

**Solo piezas + items** (los otros tres rosters SUMAN sobre esto, no están en la tabla):

| escenario | páginas/ronda | bytes/ronda | por peer | 4 peers | 16 peers | 32 peers |
|---|---|---|---|---|---|---|
| mundo temprano (100 piezas + 40 items) | 14 | 12,5 KB | 122 KB/s | 0,5 MB/s | 1,9 MB/s | 3,8 MB/s |
| base seria (1000 + 300) | 124 | 120 KB | **1170 KB/s** | 4,6 MB/s | 18,3 MB/s | 36,6 MB/s |
| mundo maduro (4000 + 1000) | 477 | 472 KB | 4606 KB/s | 18,0 MB/s | 72,0 MB/s | 143,9 MB/s |

CPU de `paginate` (release, incluye serializar cada elemento para medirlo): 0,9 / 7,8 / 27,2 ms por
segundo. **La CPU no es el problema**; los bytes sí.

### Lo que estos números significan

- **4,6 MB/s = 37 Mbps de SUBIDA** para 4 jugadores con una base de 1000 piezas. Por encima de lo
  que da una conexión doméstica típica. El techo real de hoy es **cooperativo de 2–4 jugadores con
  una base pequeña**, no un MMO.
- ~~**Peor que ancho de banda: probablemente ya no converge.**~~ **FALSO — comprobado el
  2026-08-14, ver "Techo de convergencia" abajo.** Esta advertencia salió de leer mal el comentario
  de `broadcast_stp_items`: la cifra de *"~56 páginas"* describe el comportamiento **sin** el
  `yield_now` entre páginas, que YA está puesto. Con él, el techo real medido está en torno a
  4800 elementos de mundo, muy por encima de la base seria. Se deja tachada y no borrada porque la
  conclusión errónea llegó a comunicarse.
- **4960 datagramas/s con 4 peers**, 39.680 con 32.

### El mismo roster, otra vez, por IPC

`build_world_state` (10 Hz) hace `stp_items.clone()`, `stp_buildings.clone()`,
`stp_carryables.clone()` — rosters enteros — para el cliente **local**: otros 1169 KB/s con la base
seria (clon: 63 µs/ronda). No es red, pero es CPU y memoria por tick.

`visible_corpses` es la excepción: `world.visible_corpse_views(player.position)` **sí** filtra por
proximidad. **La cura ya existe en el código; simplemente solo se le aplicó a los cadáveres.**

### Lado cliente (analizado, NO ejecutado)

`StpBuildingReplicator.LateUpdate` recorre `state.stpBuildings` **entero en cada frame**, no en cada
snapshot, y llama a `AddedKey(b.added)` por pieza — que construye un `StringBuilder` y un `string`
para cualquier pieza con materiales. Con 1000 piezas construidas a 60 fps son **~60.000
alocaciones por segundo** de basura pura para detectar un cambio que llega a 10 Hz.
`StpItemReplicator.LateUpdate` reserva además un `HashSet` y una `List` nuevos por frame.

**Sin medir en ejecución**: no hay captura de Profiler. El coste algorítmico es cierto; su peso en
ms por frame no está medido.

## Techo de convergencia — medido 2026-08-14, sockets UDP reales

Sonda `five_rosters_converge` (`backend/src/network/tests.rs`, `#[ignore]`). Emite los **cinco
rosters seguidos**, como hace el game loop, y cuenta en qué ronda el joiner tiene cada uno
completo. Ningún test previo cubría esto: todos emitían un roster aislado.

| mundo | elementos totales | resultado |
|---|---|---|
| x1 — 1000 piezas, 300 items, 200 carryables, 100 harvestables | 1600 | convergen los 4 en la **ronda 1** |
| x3 — 3000 / 900 / 600 / 300 | 4800 | convergen los 4 en la **ronda 1** |
| x6 — 6000 / 1800 / 1200 / 600 | 9600 | **3 de 4 NO convergen en 30 rondas** |
| x10 — 10000 / 3000 / 2000 / 1000 | 16000 | **ninguno converge** |

**El techo está entre 4800 y 9600 elementos de mundo.** Por debajo, todo llega a la primera. Por
encima, el roster deja de replicarse **en silencio**: el joiner conserva el último completo que
recibió (comportamiento correcto por diseño — nunca aplica una lista truncada), así que el síntoma
en juego es "las construcciones nuevas no le aparecen a nadie", sin error ni log.

**Es un techo OPTIMISTA**: la sonda corre sobre loopback, sin latencia ni pérdida real. En internet
será antes.

## Coste del relay de poses — medido 2026-08-14

`broadcast_peer_poses` reenvía la pose de cada peer a todos los demás: **O(N²)**. `PlayerUpdate`
serializado + cabecera = **242 B**, a 10 Hz.

| peers | datagramas/s | poses | + rosters = salida total |
|---|---|---|---|
| 4 | 120 | 28 KB/s | 3,2 Mbps |
| 8 | 560 | 132 KB/s | 6,9 Mbps |
| 16 | 2400 | 567 KB/s | 16,2 Mbps |
| 32 | 9920 | 2344 KB/s | 41,8 Mbps |
| 64 | 40320 | 9529 KB/s | 121,4 Mbps |

Las poses superan a los rosters a partir de ~16 jugadores: por debajo manda el tamaño del mundo,
por encima manda el número de jugadores. Ambas curvas las corta lo mismo — filtrar por distancia.

## Qué NO se ha medido

- Cliente en ejecución: frame time, drawcalls, GC real. Requiere sesión con Profiler.
- Los tres rosters que faltan (carryables, harvestables, corpses): la tabla es un **suelo**, no un
  total.
- Coste del tick del game loop completo bajo carga de jugadores.
- Worldgen / streaming de chunks.
- Cuántas páginas se pierden de verdad en una sesión real.

## Curas, por impacto medido

1. ~~**No reenviar un roster que no ha cambiado.**~~ **HECHA — ADR-071 (2026-08-14).** Ver
   "Después de ADR-071" abajo.
2. **Filtrar por distancia los cuatro rosters que no lo hacen**, extendiendo el patrón que
   `visible_corpse_views` ya aplica a los cadáveres. Convierte el coste de "tamaño del mundo" en
   "tamaño de lo que ve el jugador".
3. **Cliente: no reprocesar el roster si la generación no cambió.** Elimina las ~60.000
   alocaciones/s sin tocar la red.

Las curas 2 y 3 son cambios de protocolo o de semántica de replicación → **ADR antes de código**
(regla dura #7).

## Después de ADR-071 — medido, mismo escenario y misma sonda

El gate de emisión corta la ronda si el roster es byte-idéntico al último que salió, con ráfaga de
3 rondas tras cada cambio y latido de 3 s. Simulando **60 s con un jugador construyendo
activamente** (una pieza nueva cada 5 s, que es actividad alta, no el caso favorable):

**Emiten 48 de 600 rondas — el 8,0 %.**

| escenario | antes, por peer | después, por peer | 4 peers, antes → después |
|---|---|---|---|
| mundo temprano | 122 KB/s | 10 KB/s | 0,5 → 0,04 MB/s |
| base seria | 1170 KB/s | **94 KB/s** | 4,6 → **0,37 MB/s** |
| mundo maduro | 4606 KB/s | 368 KB/s | 18,0 → 1,4 MB/s |

**12,5× menos tráfico.** La base seria con 4 jugadores pasa de 37 Mbps de subida —por encima de lo
que da una conexión doméstica— a **3 Mbps**. En reposo, sin nadie construyendo, solo quedan los
latidos (3,3 % de las rondas).

Lo que esto NO cambia: una ronda que SÍ sale sigue costando lo mismo (sigue siendo el roster
entero, para todos los peers, sin filtro de distancia) y el cliente sigue reprocesando el roster
completo cada frame. Son las curas 2 y 3, intactas. Y el riesgo de convergencia por número de
páginas tampoco se toca: cuando una ronda sale, salen las 124 páginas igual.

## Nota sobre ADR-070

La física de caída de los objetos soltados **añade** CPU al host (acotada: 32 ítems, ~3 s cada uno,
y cero cuando no cae nada). Está declarada como deuda en el propio ADR. No aparece en esta
medición porque es irrelevante al lado de la tabla de arriba.

## F0.0 — Línea base de subida TOTAL del host (2026-08-14, ADR-073 / gate de E0)

> Sonda `host_uplink_baseline` (`backend/src/network/sync.rs`, `#[ignore]`):
> ```
> cd backend && cargo test --release host_uplink_baseline -- --ignored --nocapture
> ```
> A diferencia de `roster_relay_cost` (un componente), esto suma TODO lo que el host emite en
> régimen permanente con **8 peers**, **contando headers UDP/IP (`datagramas × (payload + 28)`)**
> — la unidad que ve el router, no la que ve `send_datagram`. Escenario: base seria
> (1000 piezas + 300 items + 200 carryables + 100 harvestables), mundo de 49 chunks cargados.

| componente | cadencia | dgr/s | KB/s |
|---|---|---|---|
| pose propia (280 B en el aire) | 10 Hz | 80 | 22 |
| relay de poses O(N²) | 10 Hz | 560 | 153 |
| PeerList (9 entradas, 671 B) | 10 Hz | 80 | 52 |
| **ChunkState (49 chunks × 1751 B de media)** | **5 Hz** | **1960** | **3351** |
| rosters con gate ADR-071, construyendo | 10 Hz gateado | — | 795 |
| rosters con gate ADR-071, idle (latidos 3 s) | — | — | 387 |

**TOTAL con 8 peers: 4373 KB/s = 35,8 Mbps de subida construyendo; 3965 KB/s = 32,5 Mbps idle.**

### El hallazgo que esta sonda destapa: ChunkState era el mayor coste y NADIE lo había medido

`broadcast_chunk_states` (`sync.rs:574`) reenvía cada 200 ms el `ChunkSyncData` COMPLETO
(layout, entidades, items) de cada chunk propio a ≤3 chunks del jugador, a TODOS los peers,
**sin mirar si algo cambió**. Son 3,35 MB/s de los 4,37 totales — el 77 % de la subida del host
— repitiendo un dato que casi nunca cambia. Ni esta tabla ni ninguna medición anterior lo
recogía: `roster_relay_cost` miraba rosters y poses. Es EXACTAMENTE el patrón que ADR-071 curó
en los rosters (gate por hash + heartbeat, cadencia no formato, sin bump), sin aplicar aquí.
**Cura candidata (decisión de Joel pendiente): F0.8, gate por hash + heartbeat por chunk en
`broadcast_chunk_states`, mismo criterio "cuándo, no qué" de ADR-071/F0.1.**

### F0.1 — el world_sync por interacción, medido (la duda que el fix debía resolver)

- Un goteo completo: 49 chunks + End = 50 datagramas, **84,9 KB**; a 8 peers = **678,9 KB por
  CADA pickup/drop legacy** (hoy).
- 1 interacción/s sostenida = 679 KB/s (5,6 Mbps) — **NO domina** sobre el permanente de
  4373 KB/s (lo domina ChunkState). Una ráfaga de 20 pickups en 1 s = 13,6 MB hoy;
  coalescida a 300 ms quedan ≤4 goteos. **Veredicto: el coalescing de F0.1 procede tal cual
  (mata el pico); la línea base la domina ChunkState, que tiene su propia cura candidata F0.8.**

Excluido de la suma: voz (3,9 KB/s por hablante, ADR-046), ACKs/retransmisiones de la capa
fiable, heartbeats a 1 Hz (~decenas de B/s). El escenario de chunks es un mundo recién
generado en el origen; una sesión larga con más entidades por chunk pesa MÁS, no menos.

## Etapa 0 — ANTES vs DESPUÉS (2026-08-14, mismo escenario y misma sonda)

> Sonda `etapa0_before_after` (`backend/src/network/sync.rs`, `#[ignore]`):
> ```
> cd backend && cargo test --release etapa0_before_after -- --ignored --nocapture
> ```
> Simula 60 s (300 rondas a 5 Hz) con el mismo reloj sintético que la sonda de ADR-071 — un
> bucle cerrado con `Instant::now()` real recorrería los 60 s en microsegundos y el latido no
> vencería nunca, dando un resultado mejor que el real.

### El titular

| escenario | subida del host con 8 peers | contra el antes |
|---|---|---|
| **ANTES de la Etapa 0** | **4373 KB/s = 35,8 Mbps** | — |
| DESPUÉS · en reposo (nadie mutando chunks cerca) | **1268 KB/s = 10,4 Mbps** | **3,4× menos** |
| DESPUÉS · actividad normal (~4 de 49 chunks vivos) | **1527 KB/s = 12,5 Mbps** | **2,9× menos** |

35,8 Mbps estaban por encima de lo que sube una conexión doméstica típica; 10–12,5 Mbps caben.

### De dónde sale, fix por fix

**F0.8 — `ChunkState` (el 77 % del antes: 3351 KB/s):**

| | KB/s | Mbps | |
|---|---|---|---|
| ANTES (sin gate, las 300 rondas) | 3351 | 27,4 | — |
| DESPUÉS · reposo | 246 | 2,0 | **13,6× menos** |
| DESPUÉS · ~4 de 49 chunks activos | 500 | 4,1 | **6,7× menos** |
| DESPUÉS · peor caso, los 49 cambian siempre | 3351 | 27,4 | 1,0× (sin ahorro, por diseño) |

El peor caso está en la tabla a propósito: **el gate no inventa ancho de banda, solo deja de
repetir lo que no cambió.** Con los 49 chunks mutando en cada ronda el coste es idéntico al de
antes, y eso es correcto — ahí sí hay 49 chunks de información nueva que enviar.

**F0.1 — `world_sync` por interacción (678,9 KB por goteo a 8 peers):**

| ritmo de interacción | ANTES | DESPUÉS | |
|---|---|---|---|
| un pickup cada 2 s (juego tranquilo) | 339 KB/s | 339 KB/s | 1,0× |
| 2 interacciones/s (loot activo) | 1358 KB/s | 1358 KB/s | 1,0× |
| ráfaga de 20 en 1 s (vaciar un cofre) | 13 577 KB/s | 2263 KB/s | **6,0× menos** |

La ventana de 300 ms deja pasar hasta 3,3 goteos/s, así que **por debajo de ese ritmo F0.1 no
ahorra nada, y no debe**: cada interacción aislada se propaga igual de rápido que antes. Lo que
corta es la ráfaga, que es donde estaba el pico de 13,6 MB/s.

**F0.2 — relay de poses: CPU, no tráfico.** Los bytes en el aire son idénticos (congelado por
test). Serializaciones por ronda: **56 (P×D) → 8 (P)**; CPU **27,1 µs → 4,4 µs por ronda**, o
0,27 → 0,04 ms/s a 10 Hz. Irrelevante hoy (la CPU sobra) y creciente con N²: a 32 peers serían
992 serializaciones por ronda en vez de 32.

### Lo que estos números NO dicen

- **No suben el techo de jugadores por sí solos.** Bajan la subida a un tercio en el escenario
  medido, lo que da margen; el techo real lo fija E1 (ADR-074), que ataca el crecimiento con N.
- El churn de chunks (cuántos cambian por ronda) **no está medido en sesión real** — por eso van
  tres extremos y no un número. Con IA activa cerca del jugador estará por encima del caso de
  reposo; medirlo pide una partida instrumentada.
- El escenario sigue siendo el mundo recién generado de 49 chunks. Una base grande mueve la
  aguja de los rosters, no de esta tabla.
