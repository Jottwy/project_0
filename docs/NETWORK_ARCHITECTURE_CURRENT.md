# Network Architecture — Current State

Revisado end-to-end el 2026-08-30 contra el código y contra el binario en ejecución, durante la
auditoría de conectividad Host/Join. Lo de aquí abajo es lo que hace el sistema, no lo que se
pretendía que hiciera.

## Overview

Cada cliente es una aplicación Unity emparejada con un proceso backend de Rust **en su propia
máquina**. Los Unity no se hablan entre sí jamás. El tráfico de red vive entre los backends, por
UDP, en topología de ESTRELLA con el host de servidor (no es una malla — ADR-015: el host reemite
la pose de cada joiner a los demás).

```
 +-----------------+                         +-----------------+
 | Unity Client A  |                         | Unity Client B  |
 |     (HOST)      |                         |    (JOINER)     |
 +--------+--------+                         +--------+--------+
          | TCP 127.0.0.1:7777                        | TCP 127.0.0.1:7778
          | (IPC, privado)                            | (IPC, privado)
 +--------+--------+                         +--------+--------+
 | Backend A       |  UDP 0.0.0.0:7778       | Backend B       |
 | is_host = true  |<========================| is_host = false |
 +-----------------+   CONNECT_TO=A:7778     +-----------------+ UDP 0.0.0.0:7779
```

Las dos conexiones son de naturaleza distinta y confundirlas es el error que costó esta auditoría:

| | IPC | P2P |
|---|---|---|
| Protocolo | TCP | UDP |
| Extremos | Unity ↔ **su propio** backend | backend ↔ backend |
| Bind | `127.0.0.1` — a propósito | `0.0.0.0` — a propósito |
| Alcance | nunca sale de la máquina | LAN / internet |
| Qué demuestra que esté arriba | que el proceso hijo arrancó | que **hay sesión** |

## Quién arranca qué

Unity **no manda "host" ni "join" por IPC**. Unity *lanza un proceso backend hijo* con un entorno
CERRADO (`NetworkInitializer.BuildChildEnvironment`: exactamente las variables declaradas por
lanzamiento, más `SystemRoot`; nada se hereda del entorno de Unity, porque un `CONNECT_TO` heredado
ya arrancó una vez un "host" que en realidad era joiner).

El **rol lo decide una sola cosa**: si el backend ve `CONNECT_TO`, es joiner; si no, es host
(`main.rs`, `let is_host = connect_to.is_none()`).

| Variable | Host | Joiner | Qué es |
|---|---|---|---|
| `IPC_PORT` / `IPC_ADDR` | 7777 | 7778 | TCP con SU Unity |
| `NET_PORT` | 7778 | 7779 | UDP propio, bind `0.0.0.0` |
| `NET_ID` | 1 | `1000 + pid%60000` | id de peer provisional |
| `CONNECT_TO` | *(ausente)* | `<ip host>:<NET_PORT host>` | a quién saludar |
| `WORLD_SEED` | 42 | *(lo impone el host en el ACK)* | mundo |

Los puertos del joiner salen de `ipcPort + joinerNetPortOffset` y `netPort + joinerNetPortOffset`
(offset 1). **TCP 7778 y UDP 7778 no chocan**: son protocolos distintos y el SO los cuenta aparte.

Si un puerto está ocupado, `NetworkInitializer.SelectLaunchConfig` salta al siguiente libre y
**escribe el `NET_PORT` elegido de vuelta en el campo de la UI** — lo que se ve en pantalla tras
hostear es el puerto real, no el tecleado. El backend NO busca puerto por su cuenta: si el que le
dan está ocupado, muere con `Failed to bind P2P UDP socket: ... AddrInUse` (verificado). Elegir otro
en silencio sería cómo un host acaba escuchando donde nadie le busca.

## El campo "puerto" de la UI significa dos cosas distintas

Es la trampa de la pantalla y conviene tenerla presente:

- Pulsando **Host**: es *mi* puerto UDP de escucha (`StartAsHostOnPort` → `NET_PORT`).
- Pulsando **Join**: es *el puerto del host* (`StartAsJoiner` → `CONNECT_TO`), no el mío. El mío lo
  elige Unity solo.

El campo IP solo se lee en el camino de Join. Hosteando se ignora, y con razón: el host escucha en
todas las interfaces.

## Bind: por qué cada uno donde está

- **P2P → `0.0.0.0:{NET_PORT}`** (`NetworkManager::bind`). Es lo que hace posible el LAN. Con
  `127.0.0.1` el host funcionaría en su propia máquina y sería invisible desde cualquier otra, y el
  síntoma —"a mí me va, a mi amigo no"— no señala al bind por ningún lado. Hay test de regresión que
  comprueba la dirección REAL del socket, no la cadena que se le pasó.
- **IPC → `127.0.0.1:{IPC_PORT}`** (`ipc::server::run`, `resolve_ipc_addr`). Local **a propósito**:
  es el canal privado entre un Unity y su propio backend, lleva el control entero de esa partida y
  no tiene autenticación de ninguna clase. Exponerlo a la red no arregla nada de LAN — el LAN va por
  UDP — y regalaría el control del cliente a quien esté en la misma red.

## La secuencia real de un Join

```
 JOINER                                   HOST
 ------                                   ----
 bind 0.0.0.0:7779                        bind 0.0.0.0:7778
 IPC listen 127.0.0.1:7778                IPC listen 127.0.0.1:7777
 Handshake  ───────────────────────────▶  handle_handshake
   player_name                              ¿is_host?            no → descarta (log NETPROBE)
   version = WIRE_SCHEMA_VERSION            ¿version igual?      no → Disconnect
   room_manifest_digest                     ¿digest igual?       no → Disconnect
                                            ¿aforo?              no → Disconnect "session full"
                                            allocate_peer_id + registra peer
 handle_handshake_ack  ◀───────────────── HandshakeAck(assigned_id, world_seed, peers, …)
   local_id = assigned_id
   world_seed = el del host
   registra al host como peer
   ▶ GameEvent "session_joined"  ──IPC──▶ Unity: AHORA sí entra al mundo
 Heartbeat cada 1 s ◀──────────────────▶  timeout de peer a los 5 s
```

Reintento: `retry_pending_connection` reenvía el handshake cada 1 s **con presupuesto**
(`CONNECT_TIMEOUT`, 15 s). Agotado, emite `ConnectTimedOut` → `session_ended` con un motivo que
nombra el destino y las causas reales. Sin ese presupuesto reenviaba para siempre en silencio.

## Cuándo se considera conectado a un jugador

**No cuando su IPC está arriba.** Esa era la causa raíz del fallo de esta auditoría: `IsConnected`
solo dice que Unity habló con su propio backend, y en un joiner eso ocurre exista o no el host.

- **Host / autosolo**: su propio backend es el servidor, así que el IPC local **sí** es la sesión.
- **Joiner**: hace falta `session_joined`, que solo se emite al registrar al host tras el
  HandshakeAck (`JoinSessionUI.IsSessionEstablished`).

## Modos de fallo, y qué se ve en cada uno

| Fallo | Qué pasa | Qué se ve |
|---|---|---|
| Backend no encontrado | no se lanza nada | `Backend executable not found`, panel con el error |
| Puerto UDP ocupado (host) | Unity salta al siguiente libre y actualiza el campo | `Port busy, selected free port N for NET_PORT` |
| Puerto ocupado (backend) | el proceso muere | `Failed to bind P2P UDP socket: … AddrInUse` |
| IP o puerto equivocados | nadie contesta | a los 15 s: `connect_attempt_timed_out` → vuelta al menú con el motivo |
| Host apagado | igual que el anterior | ídem |
| Firewall entrante bloqueando UDP en el host | igual que el anterior | ídem — el motivo nombra el firewall |
| NAT sin redirección (IP pública) | igual que el anterior | ídem — **no hay NAT traversal**, hace falta port forwarding |
| Versión de wire distinta | el host rechaza | `host_reject_handshake_version_mismatch` → `session_ended` |
| Pool de salas distinto | el host rechaza | `host_reject_handshake_room_manifest_mismatch` → `session_ended` |
| Sesión llena | el host rechaza | `host_reject_handshake_session_full` → `session_ended` |
| Backend arrancado con `CONNECT_TO` heredado | cree que es joiner y descarta handshakes | `NETPROBE event=handshake_dropped reason=not_host` |

## Localhost vs LAN

- **Localhost** (dos instancias en una máquina): el tráfico de loopback **no pasa por el firewall de
  Windows**. Por eso un LAN roto puede verse perfecto en local. Probar en localhost NO valida LAN.
- **LAN**: el joiner necesita la IP de red del host (`ipconfig`, la del adaptador Ethernet/Wi-Fi) y
  que el firewall del host deje **entrar UDP** en el `NET_PORT`. `backrooms_server.exe` lo lanza
  Unity con `CreateNoWindow`, así que Windows **no enseña el diálogo** de "permitir acceso": lo
  bloquea callado. Si hace falta regla, es de entrada, UDP, para el ejecutable — nunca desactivar el
  firewall.
- **Internet**: la IP pública del host **no basta**. No hay NAT traversal ni hole punching; hace
  falta redirigir el puerto UDP en el router del host.

## Diagnóstico en dos minutos

Los logs del backend salen por stdout y Unity los reenvía a su consola con prefijo `[Backend]`.
Recetas sobre el log del joiner y del host:

| Pregunta | Buscar |
|---|---|
| ¿El socket abrió, y dónde? | `NETPROBE event=socket_bound` (trae `requested_port` y `actual_local_addr`) |
| ¿El IPC escucha? | `IPC server listening on` |
| ¿Salió algún handshake? | `event=joiner_send_handshake` (trae `attempt=`) |
| ¿Llegó al host? | `NETPROBE event=datagram_received` + `event=host_receive_handshake` |
| ¿El host lo rechazó, y por qué? | `event=host_reject_handshake_` |
| ¿El host lo registró? | `event=host_register_peer` |
| ¿El joiner entró? | `event=joiner_session_joined` |
| ¿Se agotó el intento? | `event=connect_attempt_timed_out` |
| ¿El backend cree que es host? | `role=host` en `socket_bound`; y `handshake_dropped reason=not_host` si no |

## Identity Model

| Concepto | Descripción |
|---------|-------------|
| `NET_ID` / `peer_id` | Identidad lógica de cada backend. Única por sesión. El host reasigna al admitir (`allocate_peer_id`). |
| `player_id` | Identidad de juego, hoy 1:1 con `peer_id`. |
| Dirección IP | Solo transporte. **Nunca identidad de jugador** — dos clientes en localhost comparten IP y tienen `peer_id` distintos. |
| `identity_key` | Persistencia (ADR-045). Independiente de la red. |

## Lo que esta arquitectura NO incluye

Preocupaciones futuras reconocidas, no implementaciones actuales:

- Servidor dedicado o relay
- Migración de host (que el host se vaya termina la sesión — ADR-056)
- NAT traversal / hole punching
- Cifrado o autenticación (ni en P2P ni en IPC)
