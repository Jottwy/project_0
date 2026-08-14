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

| # | Fix | Dónde | Tarea |
|---|---|---|---|
| F0.0 | Sonda de kbps de subida (commit cero, línea base ANTES de tocar nada; mide también el payload de un world_sync completo) | nueva, estilo `roster_relay_cost` | (a) |
| F0.1 | Coalescing de `broadcast_world_sync` por pickup/drop: flag dirty + cadencia 250–300 ms. **Si F0.0 dice que la línea base domina sobre el pico: PARAR Y PREGUNTAR** (el sync por chunk toca `WorldSyncProgress::is_complete()` = E1 anticipado, se decide con Joel fuera de sesión; E0 mata el pico, no la línea base) | `game_loop.rs:5099`, `:5135` | (b) |
| F0.2 | Cachear serialización SOLO en el relay de poses (payload una vez por src, re-estampar header). El análogo en `broadcast_reliable` NO se hace: E1 hace ese payload por-destinatario y lo mataría | `sync.rs:353-360`; `encode_packet` en `protocol.rs:968` | (b) |
| F0.3 | Veredictos (grants, corpse, PvP) a `send_reliable_queued` con cap por peer. Desborde = condición FATAL del peer (desconexión + resync, patrón ADR-062), nunca descarte. Cap = peor ráfaga legítima medida × 3. Tests: ráfaga legítima NO desconecta; desborde SÍ | `send.rs:104-115`, `:132` | (b) |
| F0.4 | Autosave fuera del tick: medir split serialización/IO primero; escritura en `spawn_blocking` (tmp+rename), guard anti-solape que marca dirty al saltar, JSON compacto. Plan B: serialización troceada con doble buffer o save incremental por colección sucia. Si ni así: gate relajado a número MEDIDO, nunca "sin gate" | `game_loop.rs:1586` | (c) |
| F0.5 | Dedupe sets acotados: los ~10 `HashSet processed_*` a `BoundedDedupeSet` cap 512. `requested_spray_chunks` y `occupied_stp_cells` NO (estado semántico) | `network/mod.rs:148-210` | (c) |
| F0.6 | Sin clones de rosters a 10 Hz en `build_world_state`: cache por `content_hash` (ADR-071) o `Arc<Vec<T>>` (bytes IPC idénticos) | `game_loop.rs:5502-5505` | (c) |
| F0.7 | Anticheat gratis: distancia en pickup STP con margen 7,5–8 m (no 5 m: la posición que el host tiene del cliente va por detrás con RTT) y LoS real en PvP (hoy stub que nunca rechaza) | `game_loop.rs:4966-5030`, `:4137-4145` | (c) |

**Secuenciación** (una tarea por sesión): **(a)** F0.0 + este doc + ADR-073/074 · **(b)**
F0.1–F0.3 · **(c)** F0.4–F0.7 + re-medición del gate y actualización de perf-baseline.md.

### E1 — Interest management (ADR-074, en propuesta)

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
