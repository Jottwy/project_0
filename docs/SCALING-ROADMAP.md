# Hoja de ruta de escalado — de host-as-server al MMO (ADR-073)

> Documento VIVO. La ley es [ADR-073](DECISIONS.md) (etapas, gates, topología aplazada) y
> [ADR-074](DECISIONS.md) (interest management, en propuesta). Este doc lleva el detalle
> operativo: qué fix toca qué línea, qué se mide y contra qué número. Los datos de medición
> viven en [systems/perf-baseline.md](systems/perf-baseline.md); aquí solo se citan.
>
> Encargo de Joel (2026-08-14): escalar poco a poco lo actual hacia el MMO survival — grupos
> pequeños hoy, más sólido cada etapa, miles por mapa como horizonte — preparando el terreno
> sin romper lo que funciona.

## Dónde estamos (medido, no estimado)

- **Topología**: host-as-server. Cada cliente lanza su `backrooms_server.exe`; backends hablan
  por UDP directo (estrella, host = autoridad y relay, ADR-015). Sin NAT traversal, sin
  cifrado, sin autenticación de paquetes.
- **Techo hoy (tras ADR-071)**: ~8 jugadores domésticos, ~16 con fibra, ~32 en VPS (estimación
  derivada; el gate de E0 la convierte en medida). El cuello es el **ancho de banda de subida
  del host**, no la CPU (27 ms/s en el peor caso medido).
- **Los dos asesinos**: relay de poses O(N²) (242 B × 10 Hz, domina a partir de ~16 peers) y
  cinco rosters enteros sin filtro de distancia (la convergencia muere EN SILENCIO entre 4800
  y 9600 elementos de mundo).
- **Autoridad real hoy**: posición e inventario trust-the-client; PvP validado por host (11
  pasos, LoS = stub); salud aplicada por la víctima; sin módulo anticheat.
- La historia completa (plan A–F del 2026-08-01 y su refutación del 08-02) está en
  `SESSION-LOG.md`; los números de este doc son los de la refutación.

## Las etapas

### E0 — Saneamiento sin cambio de wire

**Objetivo**: 8–12 domésticos ESTABLES. No sube el techo teórico; elimina los desplomes por
amplificación. `WIRE_SCHEMA_VERSION` queda en 32.

**Gate**: kbps de subida del host con 8 peers, **contando headers UDP/IP:
`datagramas × (payload + 28)`** (28 B de header sobre payloads de pose de ~30–60 B son el
30–50 % del tráfico real). Antes capturado por F0.0, después al cierre. Además: ráfaga de 20
pickups sin rondas de world_sync amplificadas, 0 descartes de veredicto, autosave sin hitch
>16 ms (o el número medido que F0.4 documente).

**Fixes, en orden y con su tarea:**

> **Estado (2026-08-15): ETAPA 0 CERRADA.** Gate pasado: la subida del host con 8 peers baja de
> **35,8 Mbps a 10,4–12,5** (3,4×/2,9× menos). Dos de los siete fixes se cerraron SIN escribir
> código porque la medición dijo que no hacían falta (F0.4, F0.6), y uno que no estaba en el plan
> —F0.8— resultó ser el 77 % del problema. Pendiente: la parte 2 de F0.7 (LoS del PvP), que
> necesita una decisión de diseño de Joel. Siguiente etapa: E1 (ADR-074).

| # | Fix | Dónde | Tarea |
|---|---|---|---|
| F0.0 | ✅ Sonda de kbps de subida (commit cero, línea base ANTES de tocar nada; mide también el payload de un world_sync completo) | nueva, estilo `roster_relay_cost` | (a) |
| **F0.8** | ✅ **Gate por chunk en `broadcast_chunk_states`** (mecanismo de ADR-071 por chunk). Añadido tras la medición de F0.0: era el **77 %** de la subida | `sync.rs:583`, `mod.rs` (`chunk_gates`) | (b) |
| F0.1 | ✅ Coalescing de `broadcast_world_sync` por pickup/drop: flag dirty + ventana de 300 ms, consumido en cada tick. **Medición de F0.0: la línea base NO la domina este goteo (5,6 Mbps sostenido contra 35,8 totales), así que no hubo que parar** — mata el pico de una ráfaga, no la línea base | `game_loop.rs` (pickup/drop), `sync.rs` (`maybe_flush_world_sync`) | (b) |
| F0.2 | ✅ Cachear serialización SOLO en el relay de poses (payload una vez por origen). El análogo en `broadcast_reliable` NO se hace: E1 hace ese payload por-destinatario y lo mataría. Test de igualdad byte a byte | `sync.rs` (`broadcast_peer_poses`), `send.rs` (`encode_relay_as`) | (b) |
| F0.3 | ✅ Los 6 veredictos (pickup, carryable, corpse, PvP concedido/rechazado, fantasma) a `send_verdict` → cola diferida con cap 256. Desborde = FATAL para el peer (desconexión + resync, patrón ADR-062), nunca descarte. Tests: ráfaga legítima de 70 NO desconecta; desborde SÍ | `send.rs` (`send_verdict`), 6 sitios en `game_loop.rs` | (b) |
| F0.4 | ❌ **CERRADO SIN CÓDIGO, medido.** El autosave completo cuesta **1,42 ms** en el peor caso (chunk saturado de pintadas) contra 16,6 ms de presupuesto, y corre una vez cada 3 min. No había hitch que arreglar | `persistence/save.rs` (sonda) | (c) |
| F0.5 | ✅ Nueve `HashSet processed_*` a `BoundedDedupeSet` cap 512. `processed_corpse_requests` se queda como `HashSet` a propósito (viaja como parámetro a dos funciones; migrarlo obliga a tocar ~13 construcciones de tests) | `network/mod.rs` | (c) |
| F0.6 | ❌ **CERRADO SIN CÓDIGO, medido.** El clon de rosters cuesta 55 µs por ronda con la base seria y 163 µs con mundo maduro: un 1 % de un tick, una vez cada 6. Lo que sí duele es que el CLIENTE reprocese el roster cada frame — cura 3 de este mismo doc, que es trabajo de Unity | `game_loop.rs` | (c) |
| F0.7 | ✅ (parte 1) Distancia en pickup STP a **8 m**, con la decisión extraída a `pickup_within_reach` (pura, 4 tests). ⏸️ (parte 2) LoS real en PvP: **parado a propósito**, necesita decisión de diseño — ver abajo | `game_loop.rs` | (c) |

**Secuenciación** (una tarea por sesión): **(a) ✅** F0.0 + este doc + ADR-073/074 · **(b) ✅**
F0.1–F0.3 + F0.8 · **(c)** F0.4–F0.7 + re-medición del gate y actualización de perf-baseline.md.

**Lo que la tarea (b) enseñó, y que vale para la (c):** el orden correcto es medir → decidir →
implementar, y no al revés. F0.8 no estaba en el plan (nadie había medido `broadcast_chunk_states`
y resultó ser el 77 %), mientras que F0.1 —el fix que abría la lista por intuición— resultó ser
un pico, no una línea base. Ninguna de las dos cosas se sabía antes de la sonda de F0.0.

**Y lo que confirmó la tarea (c): medir también sirve para NO trabajar.** De los cuatro fixes que
quedaban, dos se cerraron sin escribir una línea porque el número dijo que el problema no existía
(F0.4: 1,42 ms de 16,6; F0.6: 1 % de un tick). El plan los daba por necesarios; la sonda los
desmintió en diez minutos. **Balance de la Etapa 0: 7 fixes planeados, 5 implementados, 2
descartados con datos, 1 no planeado que resultó ser el más importante.**

### La parte 2 de F0.7 (LoS del PvP): por qué está parada

El paso 11 de ADR-029 es un stub que nunca rechaza, así que hoy se puede disparar a través de una
pared. La herramienta obvia ya existe —`segment_is_clear` (`grid_gen/nav.rs`), que usa la IA del
robapieles— pero **replica la regla de CAMINABILIDAD, no la de visibilidad**, y consulta
`blocked_cells`, donde viven las piezas construidas por los jugadores. Usarla tal cual rechazaría
disparos por encima de un muro bajo o una valla construida: un falso rechazo en PvP es peor que
el agujero que cierra, exactamente el mismo criterio por el que el radio de pickup lleva margen.

Las opciones, para decidir con Joel: (a) LoS solo contra geometría generada, ignorando
`blocked_cells` — pide una variante de `segment_is_clear`; (b) LoS completo incluyendo
construcciones, aceptando que no se dispara por encima de piezas bajas; (c) dejarlo como está
hasta E3, donde el anticheat se hace en serio con el servidor dedicado. Requiere enmienda a
ADR-029 en cualquiera de los tres casos.

### E1 — Interest management (ADR-074, VALIDADA; fase 1 IMPLEMENTADA 2026-08-15)

> **Fase 1 (AOI de poses) + cadencia LOD hechas y en verde**, sin tocar wire — cambian a quién se
> envía y cada cuánto, no qué. `AOI_POSE_RADIUS_M = 100` con histéresis de salida ×1,2 en la
> autoridad; dentro de 50 m a 10 Hz, en el anillo 50–100 m a ~5 Hz escalonado por paridad; el
> cliente hace snap al reentrar. Medido antes de fijar el radio (ver `perf-baseline.md`): con los
> jugadores repartidos sobrevive el **19–21 %** del relay y **el porcentaje no empeora al crecer
> N**; el LOD recorta ~37,5 % de lo que quede.
>
> **Fase 2 EN CURSO.** Diseño cerrado (enmienda a ADR-074: el scope de celdas viaja explícito) y
> **receptor implementado con tests** (`CellRosterAssembler`), pero todavía no cableado: es código
> aditivo que aún no usa nadie.
>
> **Lo que falta, en orden:** (1) los cinco emisores agrupan por celda y mandan solo el scope de
> cada peer — dejan de ser `broadcast` y pasan a ser por-peer, que es el cambio de forma más
> grande; (2) los cinco receptores en `handlers.rs` + el consumidor del cierre; (3) el espejo C#
> de los replicadores; (4) bump 33 → 34 con `WireSchema.Expected` en el mismo commit. El wire ya
> escrito (campo `cell` en los cinco paquetes + opcode `RosterScopeEnd 0x54`) está aparcado en el
> scratchpad de la sesión, fuera del repo, porque sin (1) y (2) rompe diez sitios.
>
> Ahí está el 54–57 % de la subida del host, y arregla de paso el techo de convergencia (hoy los
> rosters dejan de replicarse EN SILENCIO entre 4800 y 9600 elementos).


**Objetivo**: 16–24 doméstico / 32–48 VPS con jugadores dispersos (agrupados ≈ hoy: el AOI
concentra capacidad, no la crea). El detalle es ley en ADR-074; lo esencial:

- AOI de poses con **radio global único** dimensionado por el diseño (el stalk del robapieles
  manda; sin `R_pose_phantom` — sería un oráculo). Histéresis en la AUTORIDAD (R y R×1,2),
  el cliente solo reacciona al stream.
- Cadencia LOD (10 Hz cerca / 2 Hz anillo / heartbeat 1 Hz) + on-change con épsilon.
- Rosters por celda con hash y generación POR CELDA (el `RosterAssembler` deja el todo-o-nada
  global — ese es el cambio de wire principal).
- Política de cliente obligatoria: despawn/reentrada de proxies, snap de interpolación,
  `ResetCosmetics` para los ~30 `Proxy*Hook`.
- **Bump +1** único, `WireSchema.Expected` mismo commit.

**Gate**: `relay_datagrams_per_call` −70 % con 8 peers dispersos; kbps de F0.0 antes/después;
convergencia con >9600 elementos; fantasmas indistinguibles.

### E2 — Transporte: trait + SteamNetworkingSockets/SDR

**Objetivo**: NO sube el techo (el cuello sigue siendo el uplink). Da: internet real sin abrir
puertos (NAT traversal), cifrado, anti-DDoS de Valve, e identidad SteamId autenticada a nivel
de canal — muere la falsificación de `sender_id` que hoy puede hacer cualquier proceso LAN.

- Trait `Transport` sobre el choke point `send_datagram` (`send.rs:180+`, ya único). Hacerlo
  ANTES de SNS convierte E2 en "escribir una implementación", no cirugía.
- Tabla PeerId(u16)↔SteamId(u64) en el borde — el wire interno no cambia de keying.
- **UDP crudo sigue siendo backend de PRIMERA CLASE** (las builds de itch.io no tienen Steam;
  regla de coexistencia de ADR-034/038 aplicada al transporte).
- El lobby de Steam deja de publicar `connect_ip`/`connect_port` en claro.
- Requiere ADR propio + bump probable. Coste estimado 5–8 sesiones.

**Gate**: dos máquinas en NATs domésticos reales conectan sin configuración; captura confirma
cifrado; test de suplantación de `sender_id` falla; build sin Steam sigue jugando por UDP.

### GATE DE TOPOLOGÍA (E2 → E3) — la decisión aplazada

Con medidas reales de 16+ jugadores se decide la fundación del MMO: **servidores propios
dedicados** (el backend ya corre headless; el candidato favorecido por la evidencia actual,
registrada en ADR-073) **vs hosts-cliente por zona** (idea original de Joel; hoy en contra:
host de zona = árbitro con trampa indetectable, churn doméstico exige migración de autoridad
que el código destruye activamente, y el ahorro son decenas de €/mes contra cientos de
sesiones de complejidad). La decisión la registra el ADR de E3.

### E3 — Dedicado + autoridad real

**Objetivo**: 32–40 por instancia VPS (prometer 40, medir).

- Flag `--dedicated`: el backend arranca sin cliente Unity ni `player` local (cada uso nuevo
  del player local en `game_loop.rs` es deuda directa contra esta etapa).
- Auth de joiners por Steam auth ticket. Identidad de persistencia migra a `steam:{id}` —
  ADR-045 lo dejó diseñado, cero cambio de schema.
- Anticheat pasa de stub a real: speed cap server-side, validación de pose, inventario
  server-side (el TODO explícito de `game_loop.rs:5114-5115`; probablemente con bump).
- Requiere ADRs propios. Coste 6–10 sesiones + primer coste de infra recurrente.

**Gate**: soak 24 h en VPS con 8–16 reales + bots; cliente modificado (teleport, pickup a
50 m) rechazado con log; caída del proceso ≠ pérdida de progreso.

### E4 — Instancias por capa (arquitectura de lanzamiento EA)

**Objetivo**: 100+ concurrentes TOTALES en instancias de 25–40. Las capas del lore SON shards
naturales — esto encaja con Backrooms mejor que un mapa único.

- Directorio/matchmaking simple (lista de instancias por capa), N procesos por VPS.
- Persistencia de personaje CENTRAL (el JSON por jugador de ADR-045 migra a un store propio;
  Steam Cloud NO sirve para esto — es sync de archivos del cliente, manipulable).
- Cambio de capa = re-join a otra instancia (el patrón "relevo con re-join" ya recomendado).

**Gate**: 2 instancias, un jugador cruza de capa con personaje íntegro en <10 s, test de dupe
explícito, monitorización por instancia.

### E5 — (Condicional) Zonas continuas con handoff: "miles en un mismo mapa"

Solo si el diseño exige mapa continuo sin cortes Y E4 está estable en producción Y hay >200
concurrentes de demanda Y presupuesto de infra. Procesos de zona con autoridad territorial,
handoff de entidades, persistencia y eventos cross-zona. Familia de ADRs propia; decenas de
sesiones. **Lo que no va a pasar nunca: un host doméstico sirviendo miles** (uplink de
5–50 Mbps contra necesidades de orden Gbps).

## Calendario contra hitos

| Hito | Fecha | Qué necesita |
|---|---|---|
| Alpha 1 (itch.io) | noviembre 2026 | **solo E0** (8–12 estables). E1 detrás si sobra tiempo; E2 NO se cuela antes |
| Steam Next Fest | **febrero 2027** (en oct 2026 NO se participa: se usa una vez por juego) | E1 validada, E2 si llegó |
| Early Access | primavera 2027 | E3 operativo; E4 si hay demanda |

## Qué da Steam de verdad (y qué no)

| Pieza | Qué es | Etapa |
|---|---|---|
| Steam Cloud | Sync de ARCHIVOS de save del cliente entre sus máquinas. Manipulable por el usuario. Útil para saves/settings; **inútil como persistencia del MMO** (esa es nuestra, E4) | ya / nunca como BD |
| SteamNetworkingSockets / SDR | Transporte: NAT traversal, cifrado, relay anti-DDoS, SteamId autenticado | E2 |
| Auth tickets / Web API | El servidor verifica quién conecta | E3 |
| VAC / Game Bans | Detección cliente-side complementaria; NO sustituye validación server-side | E3+ |
| Servidores de juego | **Steam no los da.** Los pagas tú; Valve pone SDR delante | — |

## Reglas permanentes (ADR-073, vigentes desde ya)

1. Lógica de autoridad nueva se escribe contra «la autoridad», no contra `is_host` ni el
   `player` local del host.
2. Persistencia y mensajes nuevos identifican por clave opaca `uuid:`/`steam:` (ADR-045),
   nunca por `PeerId`.
3. Todo sistema de red nuevo entra con su contador de bytes/datagramas (precedente:
   `relay_datagrams_per_call`). Lo que no se mide no pasa gates.
4. UDP crudo de primera clase en cualquier trabajo de transporte.
5. Ninguna etapa se implementa sin su ADR validado ni sin el gate anterior pasado.
