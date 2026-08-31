# Networking Invariants

Reglas que el sistema **debe** cumplir siempre. Cada una está escrita para poder convertirse
directamente en un test, y la columna «Fijada por» dice cuál lo hace hoy. Una entrada sin test es
deuda declarada, no una regla de adorno.

Verificado el 2026-08-30 contra `cargo test` (1189 pass), `clippy --all-targets -D warnings` y
`fmt --check` limpios.

---

## I1 — Transporte: ningún datagrama fiable fragmenta

> Ningún datagrama emitido por un camino **fiable** puede superar `SAFE_DATAGRAM_BYTES` (1200 B).

Caminos fiables: `reliable`, `deferred_reliable`, `retransmit`, `broadcast_reliable`
(`NetworkManager::is_reliable_kind`).

**Por qué 1200 y no 1472.** 1472 es MTU Ethernet menos cabeceras IP+UDP: el mejor caso. PPPoE da
1492 (→1464) y una VPN menos. 1200 es el mínimo garantizado que adopta QUIC, y es el único número
defendible sin sondear la red de cada jugador.

**Por qué solo los fiables.** Un no-fiable sobredimensionado se pierde y se auto-cura al siguiente
envío. Uno fiable se reenvía cinco veces **con el mismo tamaño**, se pierde las cinco, y agota
`MAX_RETRIES` — expulsando al peer.

| Fijada por |
|---|
| `no_world_sync_page_can_exceed_the_transport_budget` |
| `a_real_world_sync_puts_nothing_oversized_on_the_wire` (sockets reales, contador `oversized_reliable_count`) |

**Limitación conocida:** los caminos NO fiables (`broadcast_unreliable`, `unreliable_to`,
`relay_as`) **siguen pudiendo superar 1200 B**. Medido en físico: 31.004 datagramas oversized por
sesión, máx. 1881 B, mayoría `broadcast_unreliable` (`ChunkState` + roster). Ver «Riesgos» abajo.

---

## I2 — WorldSync: se aplica completo o no se aplica

> Un chunk paginado **nunca** se aplica a medias, y reunir sus páginas devuelve exactamente el
> mismo contenido y orden que el chunk original.

La capa reliable es **at-least-once y sin orden**: la página 1 puede adelantar a la 0, y cualquiera
puede llegar duplicada. Por eso `ChunkPageAssembler` no entrega nada hasta tenerlas todas — el
mismo patrón que `RosterAssembler` (ADR-060).

| Fijada por |
|---|
| `paging_loses_nothing_and_keeps_order` |
| `pages_assemble_out_of_order_and_survive_duplicates` |
| `an_incomplete_chunk_is_never_applied` |
| `a_superseded_revision_does_not_pollute_the_new_one` |
| `the_whole_world_actually_lands_on_the_joiner` |

---

## I3 — Ninguna emisión en lote monopoliza el bucle

> Todo emisor que produzca más de un datagrama seguido debe ceder (`yield_now`) entre ellos.

Sin esto, `send_world_sync` sacaba los 32 de la ventana —~35 KB— sin una sola oportunidad de
ejecución para nadie: medido, **0 cesiones**. El receptor no podía drenar su socket ni devolver
ACKs, y cada reintento reproducía la misma ráfaga hasta agotar `MAX_RETRIES` (~6 s tras entrar).

Puntos obligados: `send_world_sync`, `pump_deferred_reliable`, `process_retransmits`,
`broadcast_chunk_states`.

| Fijada por |
|---|
| `the_world_sync_burst_yields_instead_of_filling_the_window_in_one_go` |
| `a_retransmit_wave_is_not_re_sent_as_one_unbroken_burst` |
| `a_large_initial_world_sync_never_exhausts_max_retries` |

---

## I4 — La ventana fiable se respeta y nada se descarta en silencio

> En vuelo hay como mucho `WINDOW_SIZE` (32). Lo que no cabe se **aparca**, nunca se tira.

| Fijada por | `the_reliable_window_is_still_respected_after_the_fix` |
|---|---|

---

## I5 — Un endpoint no enrutable nunca se convierte en destino

> Ni `0.0.0.0` ni el puerto 0 pueden registrarse como dirección de un peer ni recibir un datagrama.

`0.0.0.0` **como destino significa «esta máquina»**: adoptarlo hace que los latidos se manden a uno
mismo y el peer real deje de recibir — invisible en una sola máquina, mortal con dos.

| Fijada por |
|---|
| `the_roster_never_advertises_an_unroutable_endpoint` |
| `a_roster_entry_with_an_unroutable_addr_is_never_registered` |
| `a_peer_stranded_on_an_unroutable_addr_is_never_a_heartbeat_destination` |

---

## I6 — El roster no puede pisar lo aprendido en el handshake

> La dirección por la que se alcanza al host la fija el `HandshakeAck`. Ningún roster posterior
> puede cambiarla.

| Fijada por | `a_roster_can_never_overwrite_the_host_endpoint_learned_from_the_handshake` (sockets reales) |
|---|---|

---

## I7 — Ningún intento de conexión es infinito

> Un joiner insiste como mucho `CONNECT_TIMEOUT` (15 s) y después **cierra con un motivo
> accionable**. Nunca reintenta en silencio.

| Fijada por |
|---|
| `silent_handshake_gives_up_after_the_connect_budget` |
| `connect_budget_does_not_expire_early` |
| `the_host_never_times_out_its_own_listen` |
| `a_completed_handshake_stops_the_connect_budget` |
| `an_explicit_rejection_wins_over_the_timeout` |
| `a_silent_connect_timeout_ends_the_session_with_an_actionable_reason` |

---

## I8 — «Conectado» significa sesión, no tubería

> Para un **Joiner**, `IPCClient.IsConnected` **no** es sesión. Hace falta `session_joined`, que su
> backend emite solo al registrar al host tras el `HandshakeAck`.
> Para **Host/autosolo** el IPC local sí es la sesión: su propio backend es el servidor.

| Fijada por |
|---|
| `HostDoesNotWaitForAHandshakeItNeverSends` (EditMode) |
| `JoinerIsNotConnectedUntilTheHandshakeCompletes` (EditMode) |
| `SessionJoinedOpensTheGate`, `AnUnrelatedEventDoesNotOpenTheGate` (EditMode) |
| `a_joiner_announces_session_joined_when_it_registers_the_host` |
| `session_joined_is_only_for_the_joiners_own_entry` |

---

## I9 — Liveness: un peer activo nunca es expulsado

> `HEARTBEAT_TIMEOUT` = 5 s frente a una cadencia de latido de 1 s. Cualquier paquete entrante
> refresca `last_heartbeat`, no solo el `Heartbeat`.

| Fijada por |
|---|
| `a_live_heartbeat_actually_reaches_the_host_and_refreshes_its_last_seen` (sockets reales) |
| `an_active_peer_is_never_reaped_by_the_liveness_scan` |
| `real_silence_past_the_threshold_does_reap_the_peer` (control positivo) |

---

## I10 — Bind: el P2P escucha en todas las interfaces, el IPC solo en local

> El socket P2P hace bind en `0.0.0.0` (si no, el LAN es imposible). El IPC hace bind en
> `127.0.0.1` (canal privado sin autenticación; exponerlo no arregla nada de LAN).
> El puerto pedido es el puerto escuchado: un puerto ocupado **falla ruidosamente**, no se reubica.

| Fijada por |
|---|
| `the_p2p_socket_binds_every_interface_not_just_loopback` |
| `the_requested_port_is_the_port_that_gets_bound` |
| `an_occupied_port_fails_loudly_instead_of_binding_somewhere_else` |

---

## I11 — El wire tiene una sola versión, y las dos mitades la comparten

> `ipc::server::WIRE_SCHEMA_VERSION` (Rust) y `WireSchema.Expected` (C#) son iguales, siempre, en
> el mismo commit. Cualquier cambio de wire las sube y añade entrada en
> `docs/systems/ipc-wire-schema.md`.

| Fijada por | `the_csharp_mirror_declares_the_same_wire_schema_version` |
|---|---|

Versión actual: **52**.

---

## I12 — La topología es una ESTRELLA, y los destinos lo dicen

Un joiner sólo direcciona al host. El host direcciona a todos. **Ningún joiner emite gameplay
directamente a otro joiner.**

**Síntoma que lo destapó:** ninguno visible en partida — el juego funcionaba, porque el relay del
host (ADR-015) llevaba igualmente los datos. El coste era invisible y real: tráfico duplicado y, en
Windows, un `WSAECONNRESET (10054)` **sobre el socket del emisor** por cada datagrama que el NAT del
otro joiner tiraba. Mismo mecanismo que ADR-043 midió en 1.073.132 líneas en una sola sesión.

**Causa raíz:** `broadcast_destinations` devolvía todos los peers registrados sin mirar el rol, y un
joiner **registra a los otros joiners con su dirección real** — el host se la reporta en `PeerList`
(`handlers.rs:219-221`; `PeerInfo` lleva `addr`, y ese campo existe porque el host sí lo necesita).

**Sólo alcanzable con 3+ jugadores.** Con uno solo, el único peer del joiner ES el host y no había
nada que distinguir. Por eso nunca apareció: no se ha jugado nunca a tres.

**Impone:** `send.rs:broadcast_destinations` → `.filter(|p| self.is_host || Some(p.id) == self.host_peer_id)`.
`host_peer_id` ya existía (ADR-056) y lo fija el `HandshakeAck` (`handlers.rs:1096`); no hizo falta
estado nuevo.

**Prueba:** `a_joiner_only_broadcasts_to_the_host` (el que reproducía el fallo: daba `[1, 9]`),
`the_host_broadcasts_to_every_real_peer`, `a_stale_roster_cannot_create_a_direct_joiner_to_joiner_route`,
`three_players_produce_no_peer_to_peer_traffic`, `a_joiner_without_a_known_host_broadcasts_nowhere`.

**Trade-off:** un joiner sin `host_peer_id` (handshake sin completar) no difunde a nadie. Es
deliberado — emitir a ciegas a lo que hubiera en el mapa es la ruta que esto cierra.

## I13 — Sólo el backend decide quién es un jugador remoto

Unity **pinta todo lo que llega en `world_state.remote_players`** y no vuelve a filtrar por
identidad.

**Síntoma:** «el host es invisible para los joiners», mientras los joiners se veían entre sí. La
asimetría era la pista y despistó: parecía de red y no lo era.

**Causa raíz, en cuatro pasos verificados:** Unity **propone** un id por `NET_ID` y lo guarda en
`LastSelectedNetId` (`NetworkInitializer.cs:989`); el host **asigna** el de verdad
(`handlers.rs:1221`) y el backend del joiner lo adopta (`self.local_id = assigned_id`,
`handlers.rs:1071`); **nada se lo cuenta a Unity** — `WorldState` (`ipc/mod.rs:570`) no tiene ningún
campo con el id local; y Unity filtraba `remote_players` contra ese valor obsoleto. Como el backend
construye la lista recorriendo `net.peers` (`game_loop.rs:7185`) y **un nodo nunca se registra a sí
mismo**, esa lista ya venía sin el local: el segundo filtro no podía aportar nada correcto, sólo
quitar. El valor por defecto de `NetworkInitializer.netId` es **1**, que es el id del host.

**Impone:** `RemotePlayerManager.ShouldTrackRemote`, que ahora es `true` y lleva el porqué escrito.

**Prueba:** `RemotePlayerRosterTests` — 8 casos; 5 fallaban antes del cambio, incluido
`TheHostIsRenderedEvenIfOurStaleSelfIdCollidesWithIts`.

**Limitación anotada, NO arreglada:** `LastSelectedNetId` sigue siendo el id *propuesto* y lo leen
otros cinco sitios (`RemotePvpHitbox`, `StpBuildMaterialWatcher`, `StpBuildingPlacementWatcher`,
`NetworkHarvestableInstance`, `IPCClient:603`). Comparten la misma fuente de error. Arreglarlo de
raíz es llevar el id asignado por el IPC, y eso es **cambio de wire + ADR**.

## I14 — Un destino de gameplay se decide en UN sitio, y la estrella es una de sus condiciones

Todo datagrama de gameplay sale hacia un peer que satisface las cuatro condiciones de
`peer_is_gameplay_destination` (`send.rs`): no es fantasma, no es `relay_only`, su dirección es
enrutable, y **o somos el host, o es el host**.

**Síntoma:** ninguno todavía. Esto no arregla un fallo observado, cierra la forma en que I12 se
podía perder.

**Causa raíz de la fragilidad:** I12 puso el filtro de rol en `broadcast_destinations`, que cubre
las difusiones. Los **envíos dirigidos** (`send_unreliable_to`, `send_prepared_unreliable`,
`send_reliable`, `send_reliable_queued`) no lo tenían: eran seguros porque los ~30 sitios que un
joiner ejecuta escriben el literal `1`. Eso es una costumbre, no una garantía — un sistema nuevo
que sacara el id de una lista de peers reabría la ruta Joiner→Joiner sin tocar ni una línea de los
filtros que la prohíben.

Y había un agujero **latente y peor**: `broadcast_reliable` filtraba fantasmas y `relay_only` pero
NO el rol. Un joiner habría emitido un fiable directo a cada par, y un fiable no se pierde y ya
está: se reenvía cinco veces y termina expulsando al peer (ADR-062). No era alcanzable porque sus
dos únicos llamadores (`broadcast_anchor`, `broadcast_stabilizer` en `sync.rs`) no tienen ni un
call site — que es exactamente la razón de cerrarlo antes de que alguien los llame.

**Impone:** `NetworkManager::is_gameplay_destination` / `peer_is_gameplay_destination`, invocadas
como early-return en los cuatro envíos dirigidos y como filtro en las dos difusiones. Un destino
rechazado se registra (`note_illegal_destination`, MPTRACE `illegal_gameplay_destination`), acotado
a una línea por segundo global: un descarte silencioso es lo que este proyecto ya ha pagado dos
veces.

**Prueba:** `the_star_matrix_holds_at_two_three_and_four_peers` (la matriz completa a 2, 3 y 4
jugadores, los dos roles), `no_send_surface_lets_a_joiner_reach_another_joiner` (**las seis
superficies medidas por lo que sale al socket y por lo que se encola**, no por lo que dice un
filtro), `the_host_still_reaches_every_joiner`,
`a_roster_advertising_loopback_or_apipa_never_yields_a_destination`,
`a_relay_only_peer_never_adopts_an_endpoint`. Los cinco tests de I12 siguen valiendo sin cambios:
la condición de rol es literalmente la misma, sólo cambió de sitio.

**Trade-off:** los envíos dirigidos pagan una consulta más al mapa de peers. A 10 Hz y con la
tabla en memoria, no es medible.

**Endpoint safety, y por qué NO se rechaza loopback en Rust:** una dirección anunciada en un
roster es una AFIRMACIÓN del host, no una dirección observada. `0.0.0.0` se rechaza en el registro
(significa «esta máquina» al enviar). `127.0.0.1` y APIPA `169.254.x.x` **no se pueden rechazar
ahí**: la partida en una sola máquina va por loopback de verdad y quedaría rota. Se registran, se
pintan, y no son destino porque la topología los deja fuera antes que la dirección. El rechazo de
loopback/APIPA vive donde sí corresponde, en el lado que PUBLICA: `LobbyEndpointPolicy.cs`.

## I15 — Un joiner sólo le devuelve ACKs al host

El ACK de un paquete fiable va a `pkt.addr` **crudo**, sin pasar por la tabla de peers ni por I14.
Era la última superficie por la que un joiner podía mandar un datagrama a otro joiner.

**Síntoma:** ninguno observado; alcanzable sólo con un par ejecutando un build anterior al fix de
la estrella, que emitiera fiables directos.

**Causa raíz:** el ACK se emite antes del despacho del payload y sólo mira dos cosas — que el tipo
sea fiable y que la secuencia sea > 0. Ninguna de las dos dice nada de quién lo mandó. No es
tráfico de gameplay, pero es un datagrama hacia un endpoint que la topología dice que no existe, y
en Windows lo que rebota vuelve como `WSAECONNRESET` sobre el socket propio (H10).

**Impone:** `NetworkManager::may_ack_sender` (`handlers.rs`). El host ACKea a cualquiera: es el
centro de la estrella. Un joiner, sólo al host.

**La tercera rama NO es una concesión:** con `host_peer_id == None` se ACKea igual. El
`HandshakeAck` es lo único que le dice a un joiner quién es el host, y UDP no ordena nada — un
`WorldSyncChunk` (fiable, con secuencia) puede adelantarlo. Callar ahí costaría una retransmisión
por cada chunk adelantado y no cierra ningún agujero: antes de ese punto el único que nos escribe
es el host al que le hemos pedido entrar.

**Prueba:** `a_joiner_acks_the_host_and_nobody_else`,
`a_joiner_still_acks_before_it_knows_who_the_host_is`, `the_host_acks_every_sender`. Los tres
miden el ACK en un socket UDP vivo, no en un flag.

## I16 — La dirección de un `relay_only` no se adopta jamás

Un peer marcado `relay_only` (ADR-079) conserva el centinela inerte pase lo que pase por el socket.

**Causa raíz:** `handle_packet` adopta `pkt.addr` en cualquier peer conocido cuya dirección no
pertenezca ya a OTRO peer. Al fantasma sólo lo protegía de rebote esa segunda condición —sus poses
relayadas llegan desde el socket del host, que ya es un peer conocido—, o sea que la protección
dependía de que el host estuviera registrado, no del contrato. Un datagrama que estampara ahí una
dirección real convertiría el centinela en un endpoint con pinta de legítimo.

**Impone:** la condición `&& !peer.relay_only` en la adopción (`handlers.rs`). La traza MPTRACE
`peer_last_seen_update` dejó de imprimir `addr_adopted=!relayed_from_other_peer` —que desde este
cambio ya no es la misma pregunta— y registra lo que de verdad pasó.

**Prueba:** `a_relay_only_peer_never_adopts_an_endpoint`.

## I17 — Ningún falso timeout de heartbeat en sesión estable

Con la estrella aplicada, un joiner **no le manda nada a sus pares**. Lo único que los mantiene
vivos en su tabla es el roster del host a 10 Hz (`broadcast_peer_roster`) y las poses relayadas
(ADR-015): las dos refrescan `last_heartbeat` del peer al que se refieren.

**Por qué es una invariante y no una obviedad:** es la consecuencia que I12 no dejó probada. Si ese
refresco fallara, cada joiner expulsaría a sus pares a los 5 s **con la red intacta**, y el síntoma
sería indistinguible de una pérdida de paquetes.

**Impone:** `broadcast_peer_roster` (host, cadencia de `NET_BROADCAST_EVERY`, 10 Hz) contra
`HEARTBEAT_TIMEOUT` (5 s). El margen es de 50 rondas.

**Prueba:** `the_hosts_roster_keeps_silent_peers_alive_at_three_and_four` — coloca a los pares al
borde del umbral y comprueba que el roster los devuelve al principio, a 3 y a 4 jugadores. Sin
dormir 5 s.

**Lo que esta invariante le exige a cualquiera que toque `PeerList` (auditoría de MTU, misma
fecha):** el roster no cabe siempre en un datagrama. Medido: 1 peer 130 B, 4 → 427 B, 8 → 823 B,
**16 → 1617 B**; el techo de 1200 B se cruza entre 11 y 12 peers. Con el tope del lobby de Steam
(8) cabe entero; por el navegador de servidores, que anuncia `bs_max = 50`, no.

Partirlo es obligatorio, pero **el reensamblado todo-o-nada NO es una opción aquí**, y es una
diferencia real frente a los cinco rosters de ADR-060: si una generación no llega a completarse,
NINGÚN `last_heartbeat` se refresca, y a los 5 s cada joiner expulsa a todos sus pares con la red
intacta — un síntoma idéntico al de una pérdida de paquetes que no lo es. La forma correcta, y la
que se implementó, es que cada trozo sea un `PeerList` **entero y válido por sí mismo**: el
receptor ya es aditivo (inserta lo que no conoce, refresca lo que sí, y **nunca borra** — la baja
llega por `PeerDisconnected` o por timeout, jamás por ausencia en un roster), así que el latido se
refresca al recibir el trozo y no hay nada que completar. Con 50 rondas de margen, perder un trozo
suelto es inocuo.

Por la misma razón `HandshakeAck` **recorta** su lista de peers en vez de paginarla: un joiner no
puede depender de reensamblar N datagramas antes de estar conectado, y `assigned_id` / `world_seed`
/ `config` viajan intactos. Hoy es seguro porque `handle_handshake_ack` ni lee esa lista (sólo
registra al host) y el roster autoritativo llega por el relay en los ~100 ms siguientes. **Queda
como trampa anotada:** el día que alguien empiece a leerla, un recorte silencioso es un joiner con
media sesión. Cada recorte deja línea de log; esa línea es la única defensa.

## Riesgos abiertos (invariantes que NO existen todavía)

| # | Hueco | Evidencia |
|---|---|---|
| R1 | Los caminos **no fiables** pueden superar 1200 B | 31.004 oversized/sesión, máx. 1881 B; 824 muestras `broadcast_unreliable`, 366 `unreliable_to`, 13 `relay_as` |
| R2 | Paginar `ChunkState` (no fiable) daría chunks **parcialmente** aplicados si se pierde una página, en vez de la pérdida total actual. Requiere decidir cuál de los dos males se prefiere | análisis, sin medir |
| R3 | Ciclo de vida de peers sin invariante propia: no hay test que garantice que no quedan peers fantasma tras N conexiones/desconexiones | host con 22 peers y 5 ids reales al final de una sesión de 20 min con varios clientes — **no confirmado como fuga**. **Parcialmente cerrado** por la auditoría de la Tarea 4: las marcas `phantom_ids`/`faceling_ids` eran el único estado indexado por `PeerId` que sobrevivía a una baja no ordenada, y ya se limpian en `purge_peer_state` (prueba: `an_unclean_removal_leaves_no_injected_mark_behind`). Lo que **sigue abierto** es la ventana de peer duplicado descrita en R5 |
| R5 | **Peer duplicado tras reconexión desde un puerto nuevo.** El deduplicado del handshake casa por `sender_id` O por `addr`. Un cliente que se reinicia sale con puerto de origen nuevo y `NET_ID` nuevo (`GenerateDebugNetId` = 1000 + pid%60000), así que no casa por ninguna de las dos: se le asigna un id nuevo y la entrada vieja sobrevive hasta que la coseche el timeout de latido. Ventana acotada (≤ 5 s) pero real — durante ella el jugador aparece dos veces en el roster, y el host relaya poses a un endpoint muerto | análisis del código, sin medir; no reproducido en partida |
| R6 | **Los ~30 envíos de un joiner escriben el literal `1`, no `host_peer_id`.** Coinciden porque `NET_ID` por defecto es 1 y el host nunca lo cambia, pero son dos fuentes de verdad para el mismo número. Un host lanzado con `NET_ID` distinto haría que todos esos envíos apuntaran a un peer inexistente — y desde I14 eso ya no es un fallo silencioso: sale por `illegal_gameplay_destination` con `registered=false` | análisis del código; no reproducido — exigiría lanzar el host con `NET_ID≠1` a mano |
| R7 | **Ventana de peer duplicado** — ver R5. No se cierra sin decidir qué identifica a un jugador entre reconexiones, y eso hoy no existe: el handshake sólo tiene `sender_id` (propuesto) y la dirección de origen | ídem |
| R4 | La paginación de WorldSync está validada por test, no por una partida real que la ejerza: los mundos probados en físico no tenían chunks lo bastante densos | `chunk_page_buffered = 0` en la corrida de dos procesos |
