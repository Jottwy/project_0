# Networking & Session Architecture

Lo que este documento describe está verificado contra el código, los tests o mediciones reales. Lo
que no se ha podido demostrar aparece marcado como tal. Las reglas que nunca deben romperse viven
aparte, en [`NETWORKING_INVARIANTS.md`](NETWORKING_INVARIANTS.md).

Última verificación: 2026-08-30 — `cargo test` 1191 pass, `clippy --all-targets -D warnings` y
`fmt --check` limpios, compile-check C# 0 errores en los 4 assemblies.

---

## 1. Las dos conexiones, y por qué confundirlas costó tres bugs

```
 Unity HOST                                Unity JOINER
     │ TCP 127.0.0.1:7777                       │ TCP 127.0.0.1:7778
     │ (IPC, privado)                           │ (IPC, privado)
 Backend HOST  ◀═══ UDP 0.0.0.0:7778 ═══▶  Backend JOINER
 is_host=true      CONNECT_TO=host:7778     is_host=false, UDP 0.0.0.0:7779
```

| | IPC | P2P |
|---|---|---|
| Protocolo | TCP | UDP |
| Extremos | Unity ↔ **su propio** backend | backend ↔ backend |
| Bind | `127.0.0.1` | `0.0.0.0` |
| Qué demuestra que esté arriba | que el proceso hijo arrancó | que **hay sesión** |

**El IPC hace bind local a propósito**: es el canal privado de control de esa partida y no tiene
autenticación. Exponerlo no arregla nada de LAN — el LAN va por UDP.
**El P2P hace bind en `0.0.0.0` a propósito**: con `127.0.0.1` el host funcionaría en su máquina y
sería invisible desde cualquier otra (I10).

## 2. Quién arranca qué

Unity **no manda "host"/"join" por IPC**: lanza un proceso backend hijo con un entorno **cerrado**
(`BuildChildEnvironment` — solo las variables declaradas por lanzamiento más `SystemRoot`; nada se
hereda). El rol lo decide una sola cosa: si el backend ve `CONNECT_TO`, es joiner.

| Variable | Host | Joiner |
|---|---|---|
| `IPC_PORT` | 7777 | 7778 |
| `NET_PORT` | 7778 | 7779 |
| `NET_ID` | 1 | `1000 + pid%60000` |
| `CONNECT_TO` | *(ausente)* | `<ip host>:<NET_PORT host>` |

TCP 7778 y UDP 7778 no chocan: protocolos distintos. Si un puerto está ocupado, Unity salta al
siguiente libre y **escribe el elegido de vuelta en el campo de la UI**. El backend NO busca puerto:
si el que le dan está ocupado, muere ruidosamente (I10).

El campo «puerto» de la UI significa **dos cosas distintas**: hosteando es *mi* puerto de escucha;
uniéndose es *el puerto del host*.

## 3. Handshake y asignación de IDs

```
 JOINER                                    HOST
 Handshake ──────────────────────────────▶ ¿is_host? ¿version? ¿room_manifest_digest? ¿aforo?
   player_name, version, digest,             cualquiera falla → Disconnect con motivo
   platform_id, invited_by (ADR-136)        allocate_peer_id (honra el pedido si está libre)
                                            platform_ids[platform_id] = id   (si ≠ 0)
                                            assigned_spawn: al lado del invitador (roster) o reparto
 handle_handshake_ack ◀────────────────── HandshakeAck(assigned_id, world_seed, peers, assigned_spawn, …)
   local_id = assigned_id
   world_seed = el del host
   present_at_join = peers del ack (quién ya estaba: no se anuncian)
   registra al host  ──IPC──▶ GameEvent "session_joined"
```

El `assigned_id` del host manda: el `NET_ID` que Unity generó es una **petición**, no un hecho.

**Dónde nace el joiner (ADR-116 + ADR-136).** El anfitrión decide y el punto viaja en el ack. Sin
invitación, reparto por celda de identidad con separación mínima (ADR-116). Con `invited_by ≠ 0`, el
anfitrión busca esa identidad en su mapa —él mismo incluido, por `local_position`— y da un punto a
`INVITE_SPAWN_OFFSET_M` (2 m) de la posición **del roster** del invitador, sin gastar unidad de
reparto (ADR-116 D10). Si no puede (identidad desconocida, invitador ido, fantasma) cae al reparto
con `SPAWN invite=… resolved=no reason=…`. Toda invitación honrada deja `resolved=yes` en el log del
anfitrión: `invited_by` es una afirmación del cliente sin prueba (ADR-136 enm. 1, Q2 (a)).

En el joiner, `SpawnSource::Invited` gana a la posición del fichero en los dos órdenes (enm. 1, Q1).
Límite conocido: el ack no dice si la invitación se honró o cayó al reparto, así que un invitado cuyo
invitador ya no estaba nace en el punto repartido en vez de en su posición guardada.

**Identidades y variables de entorno.** `PEER_IDENTITY` (propia, host y joiner) e `INVITED_BY`
(sólo desde el overlay de Steam), ambas `u64` opacos para el backend; ausentes = 0. Unity pone el
`SteamId` (`SteamLobbyManager.LocalSteamId`) y el backend no sabe que es de Steam (ADR-135 D2).

**Aviso de entrada.** `player_joined` lo emite el host al dar la mano y, desde 2026-09-09, también
el joiner al descubrir a un compañero por el roster (`NetworkEvent::PeerDiscovered`); `is_host` marca
al anfitrión para que un joiner no lo anuncie como si se hubiera unido. Unity lo pinta con el cartel
del vendor (`PlayerJoinNotifier`).

## 4. Estado de sesión (Unity)

`SessionStateMachine` es la única fuente de verdad de la fase:

`Menu → Starting → Connecting → Connected → InGame`, más `Disconnecting`, `Disconnected`, `Failed`.

La regla que importa (I8): **`Connected` para un joiner exige `session_joined`**, no basta el IPC.
La decisión pura vive en `JoinSessionUI.IsSessionEstablished(role, joinerConfirmed)`; el flag lo
guarda `SessionState`.

**Por qué.** `IPCClient.IsConnected` solo dice que Unity habló con su propio backend por TCP local,
y en un joiner eso ocurre exista o no el host. Con esa condición, un Join a una IP inalcanzable
pasaba a "Connected", cargaba la escena y metía al jugador en un mundo local en solitario, sin
geometría WG3 y sin un solo error. Reproducido con el binario real contra 192.0.2.1: 11 handshakes
sin respuesta, cero avisos.

## 5. Heartbeat y timeouts

| Constante | Valor | Dónde |
|---|---|---|
| Cadencia de latido | 1 s | `HEARTBEAT_EVERY = 60` @ 60 Hz |
| `HEARTBEAT_TIMEOUT` | 5 s | `peer.rs` |
| `CONNECT_TIMEOUT` | 15 s | `network/mod.rs` |
| `RETRANSMIT_BACKOFF_MS` | 200/400/800/1600 | `reliability.rs` |
| `MAX_RETRIES` | 5 | `reliability.rs` |

`last_heartbeat` lo refresca **cualquier** paquete entrante de ese peer, no solo el `Heartbeat`.
Medido en dos procesos durante 60 s: peor hueco 115 ms contra un umbral de 5000 — margen 43×.

Un intento de conexión **nunca es infinito** (I7): agotado `CONNECT_TIMEOUT`, el backend emite
`session_ended` con un motivo que nombra destino, intentos y las causas reales.

## 6. Reliable transport

Ventana de 32 en vuelo por peer. Lo que no cabe se **aparca** en `deferred_reliable`, nunca se
descarta (ADR-060). Los ACK abren hueco y `pump_deferred_reliable` drena.

**Todo emisor en lote cede entre datagramas** (I3). Sin eso, `send_world_sync` sacaba los 32 de la
ventana —~35 KB— con **0 oportunidades de ejecución** para nadie: el receptor no podía drenar su
socket ni devolver ACKs, y cada reintento reproducía la misma ráfaga hasta agotar `MAX_RETRIES`.
Síntoma físico: `reliable retransmit exhausted` ~6 s después de entrar. `broadcast_chunk_states` ya
cedía; el goteo no.

## 7. WorldSync y su paginación

El host envía un chunk por paquete fiable, y un `WorldSyncEnd` con el total. El receptor cuenta
claves `(pos, layer)` distintas y compara con ese total.

Desde v52, un chunk denso viaja **en páginas**: cabecera fija repetida (754 B medidos) más un tramo
disjunto de `entities`/`items` (~87 B por entidad). El corte se decide **codificando**, no
estimando. `ChunkPageAssembler` **no aplica nada hasta tener todas las páginas** — la capa reliable
es at-least-once y sin orden, así que "la página 0 limpia y las demás añaden" pierde datos cuando la
1 adelanta a la 0. Lo aparcado está acotado a 128 chunks con desalojo del más viejo: una página
perdida no puede quedarse en memoria para siempre.

## 8. Límites de transporte

`SAFE_DATAGRAM_BYTES = 1200` (mínimo garantizado de QUIC), **no** 1472 (mejor caso de Ethernet, ya
falla con PPPoE o VPN). `MAX_PACKET_SIZE = 65535` se queda como tope de **decodificación**: aceptar
de más es tolerancia, emitir de más es fragmentación.

Medición que lo motivó (sesión física, 9 min): **31.004 datagramas > 1472 B, máximo 1881 B**, y los
únicos 4 reenvíos de toda la sesión fueron esos 4 oversized — ninguno por debajo se perdió.

## 9. Roster y endpoints

El host difunde `PeerList` para que cada joiner conozca a los demás. Dos reglas:

- La entrada **propia** del emisor viaja con placeholder no enrutable: su socket vive en `0.0.0.0`,
  que no es la dirección de nadie. Antes se anunciaba `0.0.0.0:7778` — invisible en una máquina,
  mortal con dos, porque `0.0.0.0` como destino significa «esta máquina» (I5).
- El roster **no puede pisar** la dirección del host aprendida en el handshake (I6).

## 10. Ciclo de vida de un peer

`connect → register → active → (disconnect_packet | heartbeat_timeout | reliable_retransmit_exhausted) → cleanup`

`purge_peer_state` limpia el estado indexado por `PeerId` al salir, porque los ids se reutilizan.

**Medido, no supuesto:** en una sesión de 20 min con varios clientes, los 6 ids reales tenían
`last_seen_ms_ago` de 3–64 ms contra un umbral de 5000 en su último escaneo — todos vivos. Las
salidas registradas fueron 2 `disconnect_packet` y 1 `heartbeat_timeout`. **No se observó fuga de
peers.**

## 11. Observabilidad

Prefijos, y qué contesta cada uno:

| Prefijo | Contesta |
|---|---|
| `NETPROBE` | ¿abrió el socket, y dónde? ¿llegó el datagrama? |
| `HBTRACE` | ¿sale el latido, llega, refresca, y cuánto margen queda? |
| `RELTRACE` | ¿qué fiable salió, qué se confirmó, qué se reenvió, cuánto hay en cola? |
| `MTUPROBE` | ¿algo fragmenta? ¿cuál es el mayor visto? |
| `MPTRACE` | ciclo de vida de sesión, peers y mundo |

El backend escribe **todo, sin filtrar**, a `Builds/PlaytestLogs/backend_<rol>_<fecha>_pid<pid>.log`
con cabecera de entorno. El espejo a la consola de Unity sigue filtrado (INFO se descarta salvo con
`BACKROOMS_VERBOSE_LOG=1`, que una vez dejó un `Editor.log` de 8 GB); los eventos terminales van a
`warn!` y por tanto sí aparecen.

## 12. Errores conocidos, con su forma

| Fallo | Qué se ve |
|---|---|
| IP/puerto mal, host apagado, firewall, NAT | a los 15 s `connect_attempt_timed_out` → menú con motivo |
| Versión de wire distinta | `host_reject_handshake_version_mismatch` → `session_ended` |
| Pool de salas distinto | `host_reject_handshake_room_manifest_mismatch` |
| Puerto ocupado (backend) | `Failed to bind P2P UDP socket: … AddrInUse` |
| `CONNECT_TO` heredado en un host | `handshake_dropped reason=not_host` |

**Localhost no valida LAN**: el tráfico de loopback no pasa por el firewall de Windows, y todas las
IP locales se cortocircuitan a loopback (por eso `192.168.1.40 → 192.168.1.40` en la misma máquina
**no** ejerce la NIC). **La IP pública no basta**: no hay NAT traversal.

## 13. Un síntoma que NO era de red, y cómo se descartó

Reportado: «los clientes no ven al Host». Trazado extremo a extremo con logs reales: el host emitía
pose (1479 muestras), el joiner la recibía (`receive_player_update sender_id=1`), su
`build_world_state` la incluía, y Unity spawneaba el proxy (`spawned id=1, name=Host`) y lo
actualizaba 166 veces sin despawn.

La causa: **el Host estaba una planta por encima**. Host `Y=5.12`, clientes `Y=1.86`; diferencia
3,26 m contra `STOREY_HEIGHT_CM = 332`. Había un forjado entre medias. Ningún cambio aplicado.

## 14. Steam: un tercer canal, y sólo de metadatos

**Steam no transporta juego.** Ni una posición, ni un chunk, ni un latido. Lo único que mueve es la
ficha de una partida —`connect_ip`, `connect_port` y las claves `bs_*`— para que un cliente pueda
descubrir el endpoint **sin que un humano le dicte la IP**. Todo lo del §1 sigue exactamente igual:
IPC en TCP local, P2P en UDP directo entre backends.

```
   Steam (directorio)         ←── metadatos, fuera de banda ──→   Steam (directorio)
        ▲                                                              │
        │ publica al llegar a Connected                                │ consulta al pulsar Refrescar
   Unity HOST ══════════════ UDP directo backend↔backend ═════════════ Unity JOINER
```

### Las dos puertas, un solo camino

| Cómo llega el jugador | Por dónde entra | Qué necesita de Steam |
|---|---|---|
| Escribe IP y puerto → [Join] | `JoinSessionUI.OnJoinClicked` | **nada** |
| Navegador → selecciona → [Entrar] | `LobbyJoinRouter` → `JoinSessionLobbyJoinSink` → `JoinSessionUI.TryBeginSteamJoin` | la consulta de lobbies |
| Acepta una invitación en el overlay | `SteamLobbyManager.HandleGameLobbyJoinRequested` → `TryBeginSteamJoin` | el lobby y el overlay |

Las tres desembocan en `NetworkInitializer.StartAsJoiner` y en la misma
`SessionStateMachine`. **No hay un segundo camino de conexión**, y por eso el navegador no tuvo que
tocar el transporte: lo que aporta es el par IP:puerto, que es lo que el humano tecleaba antes.

### Convivencia: qué pasa si Steam no está

`SteamClient.Init` fallido deja `SteamLobbyManager.IsAvailable` en falso y **nada más**. El proceso
arranca igual, el panel conserva [Host] y [Join], `SessionState.Current.CanStart` sigue en cierto y
el navegador enseña un error explícito en vez de una lista vacía —que parecería que no hay nadie—.
Es la invariante **I15** de [SESSION_INVARIANTS.md](SESSION_INVARIANTS.md).

Al revés no aplica: **Steam no depende de nada del wire**, así que un cambio de esquema no rompe el
descubrimiento; lo único que viaja del wire a Steam es el NÚMERO de versión (`bs_wire`), que el
navegador compara para decidir si un lobby es entrable.

### El App ID no es un detalle de red

Sale de `SteamAppConfig` (entorno → `steam_appid.txt` → constante compilada), **nunca del código de
red**: desarrollo usa Spacewar (480) y producción el id real (5072740) con el mismo binario. El
detalle completo, incluido por qué todos los builds anteriores al 2026-08-31 salieron con Steam
muerto, está en [../SERVER_BROWSER.md](../SERVER_BROWSER.md) §14.
