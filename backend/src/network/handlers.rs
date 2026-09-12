//! Inbound packets: the `handle_packet` dispatch, the handshake exchange and the peer-roster
//! accessors those log lines lean on. Split out of `mod.rs` verbatim, except that
//! `handle_packet` and `handle_handshake` are `pub(super)` — they were private and are still
//! called from `mod.rs` (`process_incoming`) and `tests.rs`, no longer the same module.

use std::net::SocketAddr;
use std::time::{Duration, Instant};

use log::{debug, info, warn};

use super::peer::PeerConnection;
use super::protocol::{PacketPayload, PeerInfo, SessionConfig};
use super::reliability::is_reliable;
use super::{IncomingPacket, NetworkEvent, NetworkManager, PeerId};

impl NetworkManager {
    // ─── Packet handling ───

    /// TAREA 4 (2026-08-31): ¿le devolvemos un ACK a este emisor?
    ///
    /// El host ACKea a cualquiera —es el centro de la estrella—. Un joiner sólo al host.
    ///
    /// La tercera rama, `host_peer_id.is_none()`, NO es una concesión: es la ventana de arranque.
    /// El `HandshakeAck` es lo único que le dice a un joiner quién es el host, y UDP no ordena
    /// nada — un `WorldSyncChunk` (0x36, fiable y con secuencia) puede adelantarlo. Callar ahí
    /// costaría una retransmisión por cada chunk adelantado, y la ventana se cierra sola en el
    /// instante en que se procesa el ACK del handshake. Antes de ese punto el único que nos
    /// escribe es el host al que le pedimos entrar.
    fn may_ack_sender(&self, sender_id: PeerId) -> bool {
        self.is_host || self.host_peer_id.is_none() || self.host_peer_id == Some(sender_id)
    }

    /// P7.3 (2026-09-02): ¿aceptamos de este emisor un estado AUTORITATIVO (rosters STP,
    /// `ChunkState`) que sustituye al nuestro?
    ///
    /// Un host NUNCA espeja: él es la autoridad. Un joiner sólo espeja a su host, con la misma
    /// ventana de arranque que `may_ack_sender` —los rosters pueden adelantar al `HandshakeAck`—.
    ///
    /// El fallo que cierra, medido en playtest: el host recibió rosters vacíos de otro nodo de
    /// esta misma máquina estampados con `sender_id = 1` (su propio id) y los aplicó tal cual —
    /// `stp_harvestables` y `stp_items` quedaron a cero a 10 Hz, los golpes al mueble dieron
    /// `stp_harvest_hit_no_target` y el desmontaje nunca soltó nada. Los brazos decían «joiners
    /// mirror it» y lo aplicaban a cualquiera.
    fn accepts_authority_from(&self, sender_id: PeerId) -> bool {
        !self.is_host && (self.host_peer_id.is_none() || self.host_peer_id == Some(sender_id))
    }

    /// Traza del rechazo, acotada a una línea por segundo y emisor. Reutiliza el throttle de los
    /// latidos a propósito: no añade campos al `NetworkManager`, y un emisor que nos manda estado
    /// ajeno a 10 Hz llenaría el log en segundos sin la cota.
    fn note_authority_ignored(&mut self, sender_id: PeerId, kind: &str) {
        let should_log = self
            .last_keepalive_trace_at
            .get(&sender_id)
            .map(|last| last.elapsed() >= Duration::from_secs(1))
            .unwrap_or(true);
        if !should_log {
            return;
        }
        self.last_keepalive_trace_at
            .insert(sender_id, Instant::now());
        warn!(
            "MPTRACE step=AUTH event=authoritative_state_ignored self_id={} sender_id={} kind={} is_host={} host_peer_id={:?}",
            self.local_id, sender_id, kind, self.is_host, self.host_peer_id
        );
    }

    pub(super) async fn handle_packet(&mut self, pkt: IncomingPacket) -> Option<NetworkEvent> {
        let sender_id = pkt.header.sender_id;

        // Send ACK for reliable packets.
        //
        // TAREA 4 (2026-08-31): ÚLTIMA SUPERFICIE DIRECTA. Un ACK no pasa por
        // `is_gameplay_destination` —va a `pkt.addr` crudo, sin mirar la tabla de peers—, así que
        // era el único datagrama que un joiner podía seguir mandando a otro joiner: bastaba con
        // que el par tuviera un build viejo, sin el filtro de estrella, emitiendo fiables
        // directos. Un ACK no es tráfico de gameplay, pero es un datagrama a un endpoint que la
        // topología dice que no existe, y en Windows lo que rebota vuelve como `WSAECONNRESET`
        // sobre nuestro propio socket (H10). Se calla.
        if is_reliable(pkt.header.packet_type)
            && pkt.header.sequence > 0
            && self.may_ack_sender(sender_id)
        {
            let ack = PacketPayload::Ack {
                acked_sequence: pkt.header.sequence,
            };
            self.send_raw_to(pkt.addr, &ack).await;
        }

        // Update heartbeat for known peers by logical peer id. The socket address is transport only.
        //
        // The address is NOT adopted when it already belongs to a DIFFERENT known peer. Reason:
        // the ADR-015 pose relay (`send_unreliable_as`) re-emits peer B's PlayerUpdate towards C
        // from the HOST's socket while stamping `sender_id = B`. Adopting unconditionally made C
        // overwrite `peers[B].addr` with the host's address 10x/second, so every joiner ended up
        // believing every other joiner lived at the host — there was no direct route left to
        // discover. Refusing only the addresses that another peer already owns keeps genuine NAT
        // rebinding working (a new, unclaimed address is still adopted).
        let relayed_from_other_peer = self
            .peers
            .iter()
            .any(|(id, p)| *id != sender_id && p.addr == pkt.addr);
        let mut log_last_seen_update = false;
        // HBTRACE: el hueco REAL entre este paquete y el anterior de ese mismo peer, medido antes
        // de refrescar. Es el número que decide si se llega o no a los 5 s, y hasta ahora la traza
        // imprimía `last_seen_ms=0` fijo — el único valor que jamás puede diagnosticar nada.
        let mut gap_ms: u128 = 0;
        let mut hb_known_peer = false;
        // TAREA 4: la traza decía `addr_adopted=!relayed_from_other_peer`, que desde que un
        // `relay_only` tampoco adopta ya no es la misma pregunta. Se registra lo que de verdad
        // pasó, no la mitad de la condición que la decide.
        let mut addr_adopted = false;
        if let Some(peer) = self.peers.get_mut(&sender_id) {
            hb_known_peer = true;
            gap_ms = peer.last_heartbeat.elapsed().as_millis();
            // TAREA 4 (2026-08-31): un `relay_only` NO adopta direcciones. Su `addr` es el
            // centinela inerte por contrato (ADR-079) y toda la superficie de envío se apoya en
            // esa marca, no en la dirección; dejar que un datagrama entrante estampe ahí una
            // dirección real convierte el centinela en un endpoint con pinta de legítimo. Hoy
            // sólo lo impedía de rebote `relayed_from_other_peer` —porque las poses del fantasma
            // llegan desde el socket del host, que ya es otro peer conocido—, o sea que la
            // protección dependía de que el host estuviera registrado, no del contrato.
            if !relayed_from_other_peer && !peer.relay_only {
                peer.addr = pkt.addr;
                addr_adopted = true;
            }
            peer.record_heartbeat();
            let should_log = self
                .last_keepalive_trace_at
                .get(&sender_id)
                .map(|last| last.elapsed() >= Duration::from_secs(1))
                .unwrap_or(true);
            if should_log {
                self.last_keepalive_trace_at
                    .insert(sender_id, Instant::now());
                log_last_seen_update = true;
            }
        }
        // HBTRACE: el latido explícito, separado del refresco genérico. Sin esta línea un
        // `Heartbeat` que llega y uno que no se distinguen solo por ausencia, y la ausencia
        // también la produce un peer que simplemente no está registrado con ese `sender_id`.
        if matches!(pkt.payload, PacketPayload::Heartbeat) {
            if hb_known_peer {
                info!(
                    "HBTRACE event=HEARTBEAT_RECEIVED self_id={} peer_id={} from={} gap_ms={}",
                    self.local_id, sender_id, pkt.addr, gap_ms
                );
            } else {
                warn!(
                    "HBTRACE event=HEARTBEAT_RECEIVED_FROM_UNKNOWN self_id={} sender_id={} from={} registered_ids={:?}",
                    self.local_id,
                    sender_id,
                    pkt.addr,
                    self.peer_ids()
                );
            }
        }
        if log_last_seen_update {
            // Bajo el mismo throttle de 1/s que la traza de al lado: `record_heartbeat` corre en
            // CADA paquete entrante (10-60 Hz con poses y chunks), y sin acotarlo esta línea sería
            // el nuevo suelo de ruido del log en vez de la evidencia que se viene a leer.
            info!(
                "HBTRACE event=LAST_SEEN_UPDATED self_id={} peer_id={} from={} packet_type=0x{:02X} gap_ms={}",
                self.local_id, sender_id, pkt.addr, pkt.header.packet_type, gap_ms
            );
            info!(
                "MPTRACE step=N event=peer_last_seen_update reason=packet_received self_id={} peer_id={} endpoint={} addr_adopted={} last_seen_ms={gap_ms} peer_count={} remote_players_ids={:?}",
                self.local_id,
                sender_id,
                pkt.addr,
                addr_adopted,
                self.peers.len(),
                self.peer_ids()
            );
        }

        // Payload → event dispatch. `dispatch_payload!` exists for ONE reason: seventeen of
        // these arms were the same copy — destructure `PacketPayload::X`, rebuild
        // `NetworkEvent::X` from the same fields under the same names. A `macro_rules!` cannot
        // expand into match arms, only into a whole `match`, so the invocation alternates
        // `{ arms written out verbatim }` with `[ the 1:1 variants ]` and every arm keeps the
        // exact position it had when each one was spelled out by hand.
        //
        // A `[ ... ]` entry expands to EXACTLY
        //     PacketPayload::X { a, b } => Some(NetworkEvent::X { a, b }),
        // so an arm belongs there only when it does nothing else. Anything that touches `self`,
        // logs, renames the variant (`CorpseList` → `CorpseListReceived`, the whole `*Received`
        // family) or fills a field from the HEADER instead of the payload (`VoiceFrame`'s
        // `speaker`, `ChunkTransfer`'s `from`) stays written out inside a `{ ... }` group.
        macro_rules! dispatch_payload {
            ($({ $($verbatim:tt)* } [ $($variant:ident { $($field:ident),* $(,)? }),* $(,)? ])*) => {
                match pkt.payload {
                    $(
                        $($verbatim)*
                        $(PacketPayload::$variant { $($field),* } =>
                            Some(NetworkEvent::$variant { $($field),* }),)*
                    )*
                }
            };
        }

        dispatch_payload!(
        {
            // ADR-140 D4: un lote de poses ya se abrió en `process_incoming`, que lo reparte en
            // paquetes sueltos con el emisor de cada entrada en la cabecera. Si uno llega hasta
            // aquí es que se coló por otro camino, y no hay nada que hacer con él.
            PacketPayload::PlayerUpdateBatch { .. } => None,

            PacketPayload::Handshake {
                player_name,
                version,
                room_manifest_digest,
                platform_id,
                invited_by,
            } => {
                info!(
                    "Received handshake from addr={} sender_id={} name={} platform_id={} invited_by={}",
                    pkt.addr, sender_id, player_name, platform_id, invited_by
                );
                info!(
                    "MPTRACE step=B event=host_receive_handshake self_id={} sender_id={} assigned_id=<pending> peer_id=<pending> endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
                    self.local_id,
                    sender_id,
                    pkt.addr,
                    self.peers.len(),
                    self.peer_ids()
                );
                self.handle_handshake(
                    pkt.addr,
                    sender_id,
                    player_name,
                    version,
                    room_manifest_digest,
                    HandshakeIdentity {
                        platform_id,
                        invited_by,
                    },
                )
                .await
            }

            PacketPayload::HandshakeAck {
                assigned_id,
                world_seed,
                config: _,
                peers,
                anchors: _,
                stabilizers: _,
                phantom_density_scale,
                assigned_spawn,
            } => self.handle_handshake_ack(
                pkt.addr,
                sender_id,
                assigned_id,
                world_seed,
                peers,
                phantom_density_scale,
                assigned_spawn,
            ),

            PacketPayload::Heartbeat => {
                // Already updated heartbeat above.
                None
            }

            PacketPayload::Disconnect { reason } => {
                if let Some(peer) = self.peers.remove(&sender_id) {
                    self.purge_peer_state(sender_id);
                    info!(
                        "Peer {} ({}) disconnected: {}",
                        peer.name, peer.addr, reason
                    );
                    info!(
                        "MPTRACE step=L event=peer_removed reason=disconnect_packet self_id={} peer_id={} endpoint={} peer_count_before=<unknown> peer_count_after={} remote_players_ids={:?}",
                        self.local_id,
                        sender_id,
                        peer.addr,
                        self.peers.len(),
                        self.peer_ids()
                    );
                    Some(NetworkEvent::PeerDisconnected {
                        id: sender_id,
                        reason,
                    })
                } else if !self.is_host
                    && self.peers.is_empty()
                    && self.pending_connect_addr == Some(pkt.addr)
                {
                    // Corrección adosada a ADR-060: nuestro propio handshake pre-registro fue
                    // rechazado (session full o version mismatch). Sin esto, `retry_pending_
                    // connection` reenviaba el mismo handshake cada 1s para siempre y Unity nunca
                    // se enteraba — ver doc-comment de `NetworkEvent::ConnectRejected`.
                    warn!(
                        "MPTRACE step=A2 event=joiner_connect_rejected self_id={} endpoint={} reason={}",
                        self.local_id, pkt.addr, reason
                    );
                    self.pending_connect_addr = None;
                    // El intento ya tiene veredicto: sin esto el presupuesto de
                    // `CONNECT_TIMEOUT` seguiría corriendo y emitiría un segundo motivo
                    // ("nadie contestó") encima del real ("session full").
                    self.pending_connect_started_at = None;
                    Some(NetworkEvent::ConnectRejected { reason })
                } else {
                    None
                }
            }

            PacketPayload::PeerList { peers } => {
                // Host-as-server relay: the host periodically sends the full roster so each
                // joiner learns about ALL peers, not just the host. Insert peers we don't know
                // yet (the other joiners) using the address the host reported, and refresh
                // positions for the ones we already track. build_world_state reads net.peers,
                // so this is exactly what makes the other joiners appear in our world_state.
                for info in &peers {
                    if info.id == self.local_id {
                        continue; // never track ourselves as a remote
                    }
                    // ADR-146 D3: con un tramo vivo, la foto del roster no pisa la extrapolación.
                    let roster_pos = self.roster_position(info.id, info.position);
                    if info.relay_only {
                        // ADR-079: entrada solo-relay (el fantasma del host). Se registra para
                        // que las poses relayadas (ADR-015) tengan dónde aplicar — sin esto la
                        // rama PlayerUpdate las descartaba y el joiner nunca vio al robapieles.
                        // La addr del wire es un placeholder y NO se usa: se estampa la inerte
                        // local (H10: un datagrama a una addr inerte envenena el socket), y toda
                        // la superficie de envío salta `relay_only`. El heartbeat se refresca en
                        // cada roster; cuando el fantasma despawnea deja de venir y
                        // check_timeouts lo cosecha a los 5 s — retirada silenciosa
                        // (`player_left` no tiene suscriptores en Unity).
                        let entry = self.peers.entry(info.id).or_insert_with(|| {
                            let mut conn = PeerConnection::new(
                                info.id,
                                info.name.clone(),
                                super::INERT_PEER_ADDR,
                            );
                            conn.relay_only = true;
                            conn
                        });
                        entry.relay_only = true;
                        let rot = entry.rotation;
                        let anim = entry.animation;
                        entry.update_player_state(roster_pos, rot, anim);
                        continue;
                    }
                    if let Some(peer) = self.peers.get_mut(&info.id) {
                        let rot = peer.rotation;
                        let anim = peer.animation;
                        peer.update_player_state(roster_pos, rot, anim);
                    } else if let Ok(addr) = info.addr.parse::<SocketAddr>() {
                        // Auditoría de heartbeat (2026-08-30): la dirección tiene que ser de
                        // ALGUIEN. Una sin especificar (`0.0.0.0`) significa "esta máquina" al
                        // enviar: adoptarla convierte cada latido dirigido a ese peer en un
                        // latido a uno mismo, y el peer real deja de recibir nada — expulsión
                        // por heartbeat timeout con la red intacta. Inofensivo en una sola
                        // máquina, mortal en cuanto hay dos.
                        if !super::sync::is_routable_peer_addr(&addr) {
                            warn!(
                                "HBTRACE event=ROSTER_ADDR_REJECTED self_id={} peer_id={} advertised_addr={} reason=unroutable",
                                self.local_id, info.id, info.addr
                            );
                            continue;
                        }
                        let mut conn = PeerConnection::new(info.id, info.name.clone(), addr);
                        conn.update_player_state(info.position, 0.0, "idle".into());
                        self.peers.insert(info.id, conn);
                        // Un compañero nuevo. Sale como evento propio y por la cola, porque este
                        // brazo puede descubrir varios en un datagrama y sólo devuelve uno; y
                        // sólo si NO estaba ya cuando entramos (el primer roster los trae a
                        // todos). Los `relay_only` salieron por el `continue` de arriba: un
                        // robapieles no «se une a la partida».
                        if !self.present_at_join.remove(&info.id) {
                            self.push_pending_event(NetworkEvent::PeerDiscovered {
                                id: info.id,
                                name: info.name.clone(),
                            });
                        }
                    }
                }
                None
            }

            PacketPayload::StpItemList {
                items,
                generation,
                page,
                page_count,
                cell,
            } => {
                // ADR-074 fase 2: una página YA NO aplica nada. Se acumula por celda y el
                // roster no cambia hasta que llega el cierre de la ronda, que es lo único
                // que dice qué celdas debían venir (y por tanto cuáles están vacías).
                if !self.accepts_authority_from(sender_id) {
                    self.note_authority_ignored(sender_id, "stp_items");
                    return None;
                }
                self.roster_assemblers.items.accept_page(
                    cell,
                    generation,
                    page,
                    page_count,
                    items,
                );
                None
            }

            PacketPayload::StpBuildingList {
                buildings,
                generation,
                page,
                page_count,
                cell,
            } => {
                // ADR-074 fase 2: una página YA NO aplica nada. Se acumula por celda y el
                // roster no cambia hasta que llega el cierre de la ronda, que es lo único
                // que dice qué celdas debían venir (y por tanto cuáles están vacías).
                if !self.accepts_authority_from(sender_id) {
                    self.note_authority_ignored(sender_id, "stp_buildings");
                    return None;
                }
                self.roster_assemblers.buildings.accept_page(
                    cell,
                    generation,
                    page,
                    page_count,
                    buildings,
                );
                None
            }

        }
        [
        ]
        {
            // ADR-081 llevado también a APORTAR MATERIAL, que era el último de los tres verbos de
            // construcción sin dueño: colocar y demoler ya se comprobaban y añadir no. Por eso este
            // arm no puede vivir en la lista 1:1 de arriba — necesita `sender_id`.
            PacketPayload::StpBuildAddRequest {
                add_id,
                building_id,
                material_id,
            } => Some(NetworkEvent::StpBuildAddRequest {
                add_id,
                building_id,
                material_id,
                requester_id: sender_id,
            }),

            // Alcance de cosecha: el host lo mide contra la pose conocida de quien manda el golpe,
            // así que `requester_id` sale de la CABECERA y no del payload.
            PacketPayload::StpHarvestHitRequest {
                hit_id,
                harvestable_id,
                amount,
            } => Some(NetworkEvent::StpHarvestHitRequest {
                hit_id,
                harvestable_id,
                amount,
                requester_id: sender_id,
            }),

            // ADR-145 D3: alta idempotente, sin comprobación de alcance — mismo upsert que
            // `set_stp_harvestables`, pero alcanzable también por un joiner.
            PacketPayload::StpRegisterHarvestableRequest { id, position } => {
                Some(NetworkEvent::StpRegisterHarvestableRequest { id, position })
            }

            // ADR-081 llevado a la demolición: el dueño se comprueba contra la CABECERA. Por eso
            // este arm no puede vivir en la lista 1:1 de arriba — necesita `sender_id`, que el
            // payload no trae ni debe traer.
            PacketPayload::StpDemolishRequest {
                demolish_id,
                building_id,
            } => Some(NetworkEvent::StpDemolishRequest {
                demolish_id,
                building_id,
                requester_id: sender_id,
            }),

            // ADR-081: igual que los dos de abajo, `requester_id` sale de la CABECERA — es contra
            // esa identidad contra la que el host comprueba la propiedad del claim, y el payload
            // no puede tener voz en quién dice ser el que construye.
            PacketPayload::StpPlaceRequest {
                place_id,
                def_id,
                position,
                rotation,
                group_id,
                is_group,
            } => Some(NetworkEvent::StpPlaceRequest {
                place_id,
                def_id,
                position,
                rotation,
                group_id,
                is_group,
                requester_id: sender_id,
            }),

            // ADR-068: `requester_id` sale de la CABECERA, no del payload — por eso este arm no
            // puede vivir en la lista 1:1 de abajo. Es lo que impide que un cliente reclame estar
            // pintando desde la posición de otro para saltarse el tope de alcance.
            // TAREA 2 (2026-08-31): las dos direcciones de la pintada llegan paginadas por trazos
            // cuando el gesto no cabe en un datagrama. Un gesto de una sola tanda —el caso
            // normal— sale por el atajo de `offer` sin tocar ninguna estructura.
            PacketPayload::SprayPlaceRequest {
                place_id,
                layer,
                world_pos,
                yaw,
                size,
                strokes,
                page,
                page_count,
            } => self
                .spray_place_pages
                .offer(place_id, page, page_count, strokes)
                .map(|strokes| NetworkEvent::SprayPlaceRequest {
                    place_id,
                    layer,
                    world_pos,
                    yaw,
                    size,
                    strokes,
                    requester_id: sender_id,
                }),

            PacketPayload::SprayPlaced {
                spray,
                page,
                page_count,
            } => {
                // El id lo acuña el host y es la clave; los trazos se reponen enteros al
                // completarse, así que la pintada que se entrega nunca es una parcial.
                let key = spray.id as u64;
                self.spray_placed_pages
                    .offer(key, page, page_count, spray.strokes.clone())
                    .map(|strokes| {
                        let mut spray = spray;
                        spray.strokes = strokes;
                        NetworkEvent::SprayPlacedReceived { spray }
                    })
            }

            // ADR-093 (E2): `Level4State` se renombra a `*Received` — mismo motivo que
            // `CorpseList` -> `CorpseListReceived`: es un roster que se mirroriza, no una
            // petición que este backend haya hecho.
            PacketPayload::Level4State {
                epoch,
                window_open,
                return_dest,
            } => Some(NetworkEvent::Level4StateReceived {
                epoch,
                window_open,
                return_dest,
            }),

            // ADR-093 (E2): igual que `SprayPlaceRequest`, `requester_id` sale de la CABECERA —
            // el host resuelve el destino contra la posición YA CONOCIDA de ese peer, nunca
            // contra nada que el payload pudiera decir.
            PacketPayload::Level4DoorRequest { request_id, door } => {
                Some(NetworkEvent::Level4DoorRequest {
                    requester_id: sender_id,
                    request_id,
                    door,
                })
            }

            // ADR-078: el `painter_id` sale de la CABECERA por el mismo motivo que en
            // `SprayPlaceRequest` — el host reparte copias por distancia a ESE peer, y dejar que
            // el payload dijera quién pinta permitiría dibujar en nombre de otro.
            PacketPayload::SprayDraft {
                place_id,
                layer,
                anchor,
                yaw,
                color,
                width,
                first_index,
                points_mm,
            } => Some(NetworkEvent::SprayDraftReceived {
                place_id,
                layer,
                anchor,
                yaw,
                color,
                width,
                first_index,
                points_mm,
                painter_id: sender_id,
            }),

            // También lleva `requester_id` de la cabecera: la respuesta vuelve a ESE peer y no
            // a todos, así que el remitente es parte del evento.
            PacketPayload::SprayChunkRequest { cx, cz, layer } => {
                Some(NetworkEvent::SprayChunkRequest {
                    cx,
                    cz,
                    layer,
                    requester_id: sender_id,
                })
            }

            PacketPayload::StpCarryableList {
                carryables,
                generation,
                page,
                page_count,
                cell,
            } => {
                // ADR-074 fase 2: una página YA NO aplica nada. Se acumula por celda y el
                // roster no cambia hasta que llega el cierre de la ronda, que es lo único
                // que dice qué celdas debían venir (y por tanto cuáles están vacías).
                if !self.accepts_authority_from(sender_id) {
                    self.note_authority_ignored(sender_id, "stp_carryables");
                    return None;
                }
                self.roster_assemblers.carryables.accept_page(
                    cell,
                    generation,
                    page,
                    page_count,
                    carryables,
                );
                None
            }

        }
        [
            StpCarryablePickupRequest { carryable_id, requester_id },
            StpCarryablePickupGranted { carryable_id, def_id },
            StpCarryableDropRequest { drop_id, def_id, position, rotation },
        ]
        {

            PacketPayload::StpHarvestableList {
                harvestables,
                generation,
                page,
                page_count,
                cell,
            } => {
                // ADR-074 fase 2: una página YA NO aplica nada. Se acumula por celda y el
                // roster no cambia hasta que llega el cierre de la ronda, que es lo único
                // que dice qué celdas debían venir (y por tanto cuáles están vacías).
                if !self.accepts_authority_from(sender_id) {
                    self.note_authority_ignored(sender_id, "stp_harvestables");
                    return None;
                }
                self.roster_assemblers.harvestables.accept_page(
                    cell,
                    generation,
                    page,
                    page_count,
                    harvestables,
                );
                None
            }

        }
        [
            StpPickupRequest { item_id, requester_id },
            // ADR-028 Fase E: corpse relay — 1:1 payload→event mapping; all the authority
            // logic (dedupe, spawn/take, verdict relay, mirroring) lives in game_loop, which
            // owns World (corpses live in world.corpses, not in NetworkManager).
            CorpseSpawnRequest { request_id, requester_id, owner_name, position, equipment,
                held_item, items },
            CorpseTakeRequest { request_id, requester_id, corpse_id, item_index, quantity,
                requester_pos },
            CorpseTakeResult { request_id, accepted, corpse_id, item_index, item_id, quantity,
                corpse_empty, reason },
            // ADR-094 punto 4. Ambos 1:1 puros: la decisión (qué se pierde, quién carga el botín)
            // vive en game_loop, que es quien tiene el inventario y los drivers.
            StealCommand { request_id, victim_id, thief_id },
            StealReport { request_id, victim_id, thief_id, def_id, count },
        ]
        {

            // ADR-060 (d): a diferencia de los otros cuatro rosters, éste no muta `self` sino que
            // emite un evento que `game_loop` aplica. El ensamblado vive igualmente aquí (es donde
            // está el buffer) y el evento se emite SOLO con la generación completa: una página
            // suelta convertida en evento borraría los cadáveres de las demás páginas.
            PacketPayload::CorpseList {
                corpses,
                generation,
                page,
                page_count,
                cell,
            } => {
                // ADR-074 fase 2: igual que los otros cuatro, la página sólo se acumula. El evento
                // sale en el cierre de la ronda (`RosterScopeEnd`), no aquí.
                self.roster_assemblers
                    .corpses
                    .accept_page(cell, generation, page, page_count, corpses);
                None
            }

            // ADR-074 fase 2 — el cierre de una ronda de roster: aquí y sólo aquí se adopta lo
            // acumulado, con la regla de tres líneas de `accept_scope_end`.
            PacketPayload::RosterScopeEnd {
                kind,
                generation,
                cells,
            } => {
                if !self.accepts_authority_from(sender_id) {
                    self.note_authority_ignored(sender_id, "roster_scope_end");
                    return None;
                }
                let a = &mut self.roster_assemblers;
                match kind {
                    crate::network::protocol::RosterKind::ITEMS => {
                        self.stp_items = a.items.accept_scope_end(generation, &cells);
                        None
                    }
                    crate::network::protocol::RosterKind::BUILDINGS => {
                        self.stp_buildings = a.buildings.accept_scope_end(generation, &cells);
                        None
                    }
                    crate::network::protocol::RosterKind::CARRYABLES => {
                        self.stp_carryables = a.carryables.accept_scope_end(generation, &cells);
                        None
                    }
                    crate::network::protocol::RosterKind::HARVESTABLES => {
                        self.stp_harvestables = a.harvestables.accept_scope_end(generation, &cells);
                        None
                    }
                    crate::network::protocol::RosterKind::CORPSES => {
                        Some(NetworkEvent::CorpseListReceived {
                            corpses: a.corpses.accept_scope_end(generation, &cells),
                        })
                    }
                    // Un `kind` desconocido se ignora en vez de romper: un peer más nuevo puede
                    // mandar un sexto roster, y perder su cierre sólo cuesta no adoptarlo.
                    _ => None,
                }
            }

        }
        [
            // ADR-029 V0: PvP relay — 1:1 payload→event mapping; all authority logic
            // (dedupe, validation order, grant/reject dispatch) lives in game_loop, which
            // owns Player/PlayerStats (health lives there, not in NetworkManager).
            PvpHitCandidate { request_id, attacker_id, victim_id, weapon_id, damage, origin,
                direction, client_tick, hit_position },
            PvpDamageGrant { request_id, attacker_id, victim_id, weapon_id, damage, reason },
            PvpHitRejected { request_id, attacker_id, victim_id, reason },
            // ADR-047 — decode only. Every authority check (are we really the victim? is this a
            // retransmit? are we invulnerable?) lives in game_loop.rs, the same split the PvP
            // family above uses.
            PhantomAttackGrant { request_id, victim_id, kind, damage, impulse },
        ]
        {

            PacketPayload::NoiseReport { position, loudness } => {
                Some(NetworkEvent::NoiseReported { position, loudness })
            }

            // ADR-050 point 9. The sender IS the victim — the transport already knows who that is,
            // so the packet carries nothing and there is no field to validate or forge.
            PacketPayload::StruggleReport => {
                Some(NetworkEvent::StruggleReported { victim: sender_id })
            }

            PacketPayload::VoiceFrame { seq, data } => Some(NetworkEvent::VoiceReceived {
                speaker: sender_id,
                seq,
                data,
            }),

            // Sale de la lista 1:1 porque rellena un campo desde la CABECERA, igual que
            // `VoiceFrame`: el host valida el drop contra la pose que él tiene del emisor, y
            // ésa se busca por el `sender_id` del datagrama, no por nada que venga dentro.
            PacketPayload::StpDropRequest {
                drop_id,
                def_id,
                count,
                position,
                rotation,
                velocity,
            } => Some(NetworkEvent::StpDropRequest {
                drop_id,
                def_id,
                count,
                position,
                rotation,
                velocity,
                requester_id: sender_id,
            }),

        }
        [
            StpPickupGranted { item_id, def_id, count },
            Level4DoorVerdict { request_id, dest },
        ]
        {

            PacketPayload::PlayerUpdate {
                position,
                rotation,
                animation,
                crouch,
                pitch,
                equipment,
                held_item,
                hit_seq,
                dead,
                revealed,
                light_on,
                fire_seq,
                buttons,
                melee_seq,
                vocal_seq,
                vocal_kind,
                carry_def,
                carry_count,
                species,
            } => {
                if let Some(peer) = self.peers.get_mut(&sender_id) {
                    peer.update_player_state(position, rotation, animation);
                    peer.crouch = crouch; // ADR-020: cosmetic crouch, alongside the pose
                    peer.pitch = pitch; // ADR-021: cosmetic camera pitch, alongside the pose
                    peer.equipment = equipment; // ADR-022: cosmetic clothing, alongside the pose
                    peer.held_item = held_item; // ADR-023: cosmetic held item, alongside the pose
                    peer.hit_seq = hit_seq; // ADR-024: cosmetic hit-reaction counter, alongside the pose
                    peer.dead = dead; // ADR-028 post-E3: cosmetic dead flag, alongside the pose
                    peer.revealed = revealed; // ADR-038: cosmetic real-form flag, alongside the pose
                    peer.light_on = light_on; // ADR-042: cosmetic held-light flag, alongside the pose
                    peer.fire_seq = fire_seq; // ADR-042: cosmetic shot counter, alongside the pose
                    peer.buttons = buttons; // ADR-044: cosmetic aim/reload bits, alongside the pose
                    peer.melee_seq = melee_seq; // ADR-044: cosmetic swing counter, alongside the pose
                    peer.vocal_seq = vocal_seq; // ADR-048: cosmetic vocalisation counter, alongside the pose
                    peer.vocal_kind = vocal_kind; // ADR-048: which voice the last bump was
                    peer.carry_def = carry_def; // ADR-049: cosmetic carry state, alongside the pose
                    peer.carry_count = carry_count; // ADR-049: plain assignments, not a struct literal — a dropped line relays 0 forever
                    peer.species = species; // ADR-094: cosmetic species tag, alongside the pose
                }
                let should_log = self
                    .last_transform_trace_at
                    .get(&sender_id)
                    .map(|last| last.elapsed() >= Duration::from_secs(1))
                    .unwrap_or(true);
                if should_log {
                    self.last_transform_trace_at
                        .insert(sender_id, Instant::now());
                    // Shares the MPTRACE 1 s window on purpose: this used to fire on EVERY
                    // PlayerUpdate (10 Hz per peer), and the MPTRACE line below is a strict
                    // superset of it. stdout/stderr are PIPED to Unity (see ipc/server.rs), so a
                    // per-packet log is backpressure on the game, not just noise.
                    info!(
                        "Received player update from peer id={} pos=({:.2}, {:.2}, {:.2})",
                        sender_id, position[0], position[1], position[2]
                    );
                    info!(
                        "MPTRACE step=S event=receive_player_update self_id={} peer_id={} sender_id={} endpoint={} peer_count={} pos=({:.2},{:.2},{:.2}) rot={:.2}",
                        self.local_id,
                        sender_id,
                        sender_id,
                        pkt.addr,
                        self.peers.len(),
                        position[0],
                        position[1],
                        position[2],
                        rotation
                    );
                }
                Some(NetworkEvent::RemotePlayerUpdate {
                    id: sender_id,
                    position,
                    rotation,
                    animation,
                    crouch,
                    pitch,
                    equipment,
                    held_item,
                    hit_seq,
                    dead,
                    revealed,
                    light_on,
                    fire_seq,
                    buttons,
                    melee_seq,
                    vocal_seq,
                    vocal_kind,
                    carry_def,
                    carry_count,
                    species,
                })
            }

            PacketPayload::WorldSync {
                world_seed,
                world_revision,
                chunks,
            } => {
                info!(
                    "MPTRACE step=Y event=receive_world_snapshot self_id={} from_peer={} revision={} chunks={} entities={} items={}",
                    self.local_id,
                    sender_id,
                    world_revision,
                    chunks.len(),
                    chunks.iter().map(|c| c.entities.len()).sum::<usize>(),
                    chunks.iter().map(|c| c.items.len()).sum::<usize>()
                );
                Some(NetworkEvent::WorldSyncReceived {
                    world_seed,
                    world_revision,
                    chunks,
                })
            }

            // ADR-060: goteo de snapshot. El chunk es 1:1 payload→evento; el End loguea el
            // punto de medida del join (el gemelo de step=Y del monolito de arriba).
            PacketPayload::WorldSyncChunk {
                world_revision,
                data,
            } => {
                // Auditoría de MTU: un chunk denso viaja en páginas. NO se aplica ninguna hasta
                // tenerlas todas — la capa reliable no garantiza orden, así que aplicar por
                // separado dejaría el chunk a medias cuando la página 1 adelanta a la 0.
                let pages = data.page_count;
                self.chunk_pages
                    .offer(world_revision, data)
                    .map(|merged| NetworkEvent::WorldSyncChunkReceived {
                        world_revision,
                        data: merged,
                    })
                    .or_else(|| {
                        debug!(
                            "MTUPROBE event=chunk_page_buffered self_id={} pages={} pending_chunks={}",
                            self.local_id,
                            pages,
                            self.chunk_pages.pending_len()
                        );
                        None
                    })
            }

            PacketPayload::WorldSyncEnd {
                world_revision,
                chunk_count,
            } => {
                info!(
                    "MPTRACE step=Y event=receive_world_drip_end self_id={} from_peer={} revision={} chunk_count={}",
                    self.local_id, sender_id, world_revision, chunk_count
                );
                Some(NetworkEvent::WorldSyncEndReceived {
                    world_revision,
                    chunk_count,
                })
            }

            // El "Treat as a chunk transfer for now" que vivía aquí fundía el broadcast periódico
            // con el handoff de propiedad, y con ello heredaba su ACK FIABLE: a ~820 chunks/s eso
            // llenaba permanentemente la ventana de 32 del receptor. Ahora cada uno tiene su
            // evento; la APLICACIÓN sigue siendo la misma en ambos, solo cambia si se confirma.
            // TAREA 2 (2026-08-31): los dos portadores de un chunk completo pasan ahora por su
            // ensamblador. Un chunk de UNA página —el caso común— sale por el atajo de `offer` sin
            // tocar ninguna estructura, así que esto no cuesta nada en régimen normal.
            //
            // NO se aplica ninguna página suelta, y ésa es la decisión entera: `apply_chunk_sync`
            // hace `entities.clear()` + reconstruir, así que aplicar media partición no deja el
            // chunk "a medias", lo deja MAL — con una lista de entidades que nunca existió y que
            // el receptor da por buena. La clave es `(generación, pos, capa)`: una ronda nueva
            // desaloja lo que quedara a medias de la anterior.
            PacketPayload::ChunkState { data } => {
                // P7.3: misma puerta que los rosters. Un `ChunkState` es emisión SÓLO de host y
                // su aplicación es `entities.clear()` + reconstruir: aplicado en un host, borra
                // las entidades que él mismo gobierna.
                if !self.accepts_authority_from(sender_id) {
                    self.note_authority_ignored(sender_id, "chunk_state");
                    return None;
                }
                let pages = data.page_count;
                let generation = data.generation as u64;
                self.chunk_state_pages
                    .offer(generation, data)
                    .map(|merged| NetworkEvent::ChunkStateReceived {
                        from: sender_id,
                        data: merged,
                    })
                    .or_else(|| {
                        debug!(
                            "MTUPROBE event=chunk_state_page_buffered self_id={} from_peer={} pages={} pending_chunks={}",
                            self.local_id,
                            sender_id,
                            pages,
                            self.chunk_state_pages.pending_len()
                        );
                        None
                    })
            }

            PacketPayload::ChunkTransfer { data } => {
                let pages = data.page_count;
                let generation = data.generation as u64;
                self.chunk_transfer_pages
                    .offer(generation, data)
                    .map(|merged| NetworkEvent::ChunkTransferReceived {
                        from: sender_id,
                        data: merged,
                    })
                    .or_else(|| {
                        debug!(
                            "MTUPROBE event=chunk_transfer_page_buffered self_id={} from_peer={} pages={} pending_chunks={}",
                            self.local_id,
                            sender_id,
                            pages,
                            self.chunk_transfer_pages.pending_len()
                        );
                        None
                    })
            }

            PacketPayload::ChunkTransferAck { pos } => {
                Some(NetworkEvent::ChunkTransferAckReceived {
                    from: sender_id,
                    pos,
                })
            }

            PacketPayload::ChunkTeleport {
                old_pos,
                new_pos,
                new_seed,
            } => Some(NetworkEvent::ChunkTeleportReceived {
                old_pos,
                new_pos,
                new_seed,
            }),

            PacketPayload::AnchorBroadcast {
                chunk_pos,
                durability,
                installed_by,
            } => Some(NetworkEvent::AnchorBroadcastReceived {
                chunk_pos,
                durability,
                installed_by,
            }),

            PacketPayload::StabilizerBroadcast {
                chunk_pos,
                tier,
                remaining_hours,
            } => Some(NetworkEvent::StabilizerBroadcastReceived {
                chunk_pos,
                tier,
                remaining_hours,
            }),

            PacketPayload::Ack { acked_sequence } => {
                // RELTRACE: `matched=false` es señal, no ruido — significa un ACK para algo que
                // ya no está en la ventana (duplicado tras un reenvío, o llegado tarde). Sin
                // esta línea, "el ACK no vuelve" y "el ACK vuelve y no encuentra su paquete" se
                // ven exactamente igual desde fuera, y llevan a arreglos opuestos.
                let self_id = self.local_id;
                if let Some(peer) = self.peers.get_mut(&sender_id) {
                    let matched = peer.process_ack(acked_sequence);
                    let in_flight = peer.reliable_queue.len();
                    let deferred = peer.deferred_reliable.len();
                    info!(
                        "RELTRACE event=ACK_RECEIVED self_id={self_id} peer_id={sender_id} seq={acked_sequence} matched={matched} in_flight={in_flight} deferred={deferred}"
                    );
                } else {
                    warn!(
                        "RELTRACE event=ACK_FROM_UNKNOWN self_id={self_id} sender_id={sender_id} seq={acked_sequence} from={}",
                        pkt.addr
                    );
                }
                None
            }

            PacketPayload::Nack {
                requested_sequence: _,
            } => {
                // Auditoría (H12a, 2026-08-10): confirmado no-op — nadie en este crate construye
                // un `PacketPayload::Nack` (grep sin resultados fuera del propio decode/encode).
                // Se conserva el decode a propósito, sin implementar el reenvío: retirar la
                // variante tocaría el enum de wire de `protocol.rs` y por la regla dura #7
                // (cambio de API pública = ADR) eso es un ADR de limpieza, no una corrección
                // adosada. No compensa para un opcode que ya no se emite.
                None
            }

            PacketPayload::Ping { send_time } => {
                // Respond with the same timestamp so the sender can measure RTT.
                let pong = PacketPayload::Ping { send_time };
                self.send_raw_to(pkt.addr, &pong).await;
                None
            }

            // Action packets — forward to game loop as-is.
            PacketPayload::Interact {
                requester_id,
                request_id,
                target_id,
                target_kind,
                interaction_type,
                player_position,
            } => {
                info!(
                    "MPTRACE step=AE event=host_receive_interact_request self_id={} requester_id={} target_id={} request_id={} kind={} type={}",
                    self.local_id,
                    requester_id,
                    target_id,
                    request_id,
                    target_kind,
                    interaction_type
                );
                Some(NetworkEvent::WorldInteractRequest {
                    requester_id,
                    request_id,
                    target_id,
                    target_kind,
                    interaction_type,
                    player_position,
                })
            }

            PacketPayload::Attack { .. }
            | PacketPayload::Pickup { .. }
            | PacketPayload::Drop { .. }
            | PacketPayload::Craft { .. }
            | PacketPayload::PlaceStabilizer { .. }
            | PacketPayload::PlaceAnchor
            | PacketPayload::ChunkDelta { .. }
            | PacketPayload::EntityUpdate { .. } => {
                // These will be processed when full action handling is wired up.
                None
            }
        }
        []
        )
    }

    pub(super) async fn handle_handshake(
        &mut self,
        from_addr: SocketAddr,
        sender_id: PeerId,
        player_name: String,
        version: String,
        room_manifest_digest: String,
        identity: HandshakeIdentity,
    ) -> Option<NetworkEvent> {
        if !self.is_host {
            // Only the host accepts handshakes.
            //
            // NETPROBE (diagnóstico temporal): este `return None` era mudo. Un backend arrancado
            // con CONNECT_TO puesto (fuga de entorno heredado de Unity, ver el comentario de
            // `LaunchBackendProcess`) queda con is_host=false, escucha en 7778 y descarta TODOS
            // los handshakes sin dejar rastro — indistinguible desde fuera de "no llegó nada".
            warn!(
                "NETPROBE event=handshake_dropped reason=not_host self_id={} sender_id={} from={} name={} version={}",
                self.local_id, sender_id, from_addr, player_name, version
            );
            return None;
        }

        // Corrección adosada a ADR-060 (docs/DECISIONS.md, 2026-08-10): `version` used to be
        // ignored entirely (`_version`). Compared before the duplicate-peer branches below on
        // purpose — a mismatched joiner never got registered, so it can't fall into either of
        // those reconnect paths. Same rejection mechanism as "session full": raw `Disconnect`,
        // never entering `self.peers`.
        let expected_version = crate::ipc::server::WIRE_SCHEMA_VERSION.to_string();
        if version != expected_version {
            warn!(
                "MPTRACE step=B2 event=host_reject_handshake_version_mismatch self_id={} sender_id={} endpoint={} host_version={} joiner_version={}",
                self.local_id, sender_id, from_addr, expected_version, version
            );
            let mismatch = PacketPayload::Disconnect {
                reason: format!("wire schema mismatch: host={expected_version} joiner={version}"),
            };
            self.send_raw_to(from_addr, &mismatch).await;
            return None;
        }

        // ADR-083 enmienda 1, punto 4 — el pool de salas autoradas tiene que ser el MISMO en los dos
        // builds. No viaja por la red: sale de un fichero que va dentro del build, y cada peer
        // genera el mundo por su cuenta desde el seed. Con pools distintos, uno pinta una sala donde
        // el otro pinta otra y nadie se entera hasta que alguien se choca con nada.
        //
        // Rechazo RUIDOSO, nunca degradación en silencio: lo prohíbe el ADR. Mismo mecanismo y mismo
        // sitio que el de versión de wire, y por delante del registro del peer.
        let expected_digest = crate::world::grid_gen::active_manifest()
            .map(|m| m.digest.clone())
            .unwrap_or_default();
        if room_manifest_digest != expected_digest {
            warn!(
                "MPTRACE step=B2 event=host_reject_handshake_room_manifest_mismatch self_id={} sender_id={} endpoint={} host_digest={} joiner_digest={}",
                self.local_id,
                sender_id,
                from_addr,
                if expected_digest.is_empty() { "<sin manifiesto>" } else { &expected_digest },
                if room_manifest_digest.is_empty() { "<sin manifiesto>" } else { &room_manifest_digest }
            );
            let mismatch = PacketPayload::Disconnect {
                reason: format!(
                    "room manifest mismatch: host={} joiner={}",
                    if expected_digest.is_empty() {
                        "<none>"
                    } else {
                        &expected_digest
                    },
                    if room_manifest_digest.is_empty() {
                        "<none>"
                    } else {
                        &room_manifest_digest
                    }
                ),
            };
            self.send_raw_to(from_addr, &mismatch).await;
            return None;
        }

        if let Some(existing) = self.peers.get(&sender_id) {
            info!(
                "Duplicate handshake from addr={} sender_id={} peer_id={}",
                from_addr, sender_id, existing.id
            );
            info!(
                "MPTRACE step=C event=host_peer_already_registered self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
                self.local_id,
                sender_id,
                existing.id,
                existing.id,
                from_addr,
                self.peers.len(),
                self.peer_ids()
            );
            self.send_handshake_ack(from_addr, sender_id, existing.id)
                .await;
            return None;
        }

        if let Some(existing) = self.peers.values().find(|p| p.addr == from_addr) {
            info!(
                "Duplicate handshake from addr={} sender_id={} already assigned id={}",
                from_addr, sender_id, existing.id
            );
            info!(
                "MPTRACE step=C event=host_peer_already_registered_by_endpoint self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
                self.local_id,
                sender_id,
                existing.id,
                existing.id,
                from_addr,
                self.peers.len(),
                self.peer_ids()
            );
            self.send_handshake_ack(from_addr, sender_id, existing.id)
                .await;
            return None;
        }

        // Aforo. `max_players` existía en SessionConfig y en WorldConfig, y NO se consultaba en
        // ningún sitio del árbol: el host aceptaba handshakes indefinidamente. Se aplica aquí,
        // el único punto donde entra un peer NUEVO — las dos ramas de arriba son reconexiones de
        // alguien ya admitido y no deben rebotar nunca.
        //
        // Se compara contra `real_peer_count()` para que un fantasma (ADR-016) no consuma plaza,
        // y contra `max_players - 1` porque el host ocupa una y no está en `peers`. El valor sale
        // de `SessionConfig::default()`, que es exactamente el que el propio HandshakeAck
        // anuncia en `build_handshake_ack` — anunciar 50 y admitir infinitos era la incoherencia.
        let capacity = (SessionConfig::default().max_players as usize).saturating_sub(1);
        if self.real_peer_count() >= capacity {
            warn!(
                "MPTRACE step=B2 event=host_reject_handshake_session_full self_id={} sender_id={} endpoint={} real_peer_count={} capacity={}",
                self.local_id,
                sender_id,
                from_addr,
                self.real_peer_count(),
                capacity
            );
            let full = PacketPayload::Disconnect {
                reason: "session full".into(),
            };
            self.send_raw_to(from_addr, &full).await;
            return None;
        }

        let assigned_id = self.allocate_peer_id(sender_id);

        info!(
            "New peer connecting sender_id={} name={} from {} -> assigned id {}",
            sender_id, player_name, from_addr, assigned_id
        );

        // Add the peer.
        let peer = PeerConnection::new(assigned_id, player_name.clone(), from_addr);
        self.peers.insert(assigned_id, peer);
        // ADR-141: se apunta para que los emisores le sirvan el mundo A ÉL. Antes esto lo cubría la
        // condición `joined` del gate, que abría la puerta y retransmitía a TODOS — la tormenta que
        // tumbaba la partida al sexto jugador. Va aquí, en el ÚNICO sitio donde el host admite a un
        // peer de verdad: los otros `peers.insert` son fantasmas y criaturas (ADR-016/043/079), que
        // no reciben nada y no deben pedir un mundo.
        self.pending_full_sync
            .insert(assigned_id, crate::network::roster::ROSTER_CHANGE_BURST);
        info!(
            "MPTRACE step=C event=host_register_peer self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
            self.local_id,
            sender_id,
            assigned_id,
            assigned_id,
            from_addr,
            self.peers.len(),
            self.peer_ids()
        );

        // ADR-136 D3 — la identidad de plataforma, si la hay. Un 0 nunca entra en el mapa: es
        // «sin identidad», no una identidad que valga 0.
        if identity.platform_id != 0 {
            self.platform_ids.insert(identity.platform_id, assigned_id);
        }

        // ADR-116 D3 — el reparto se decide AQUÍ, antes de construir el ack, porque construirlo es
        // `&self`. Un `None` no es un error: significa candidatos agotados (D7) y el joiner nacerá
        // en el origen, que es el comportamiento de siempre. ADR-136: si viene invitado, el punto
        // es al lado del invitador y el reparto queda de respaldo.
        let _ = self.assign_spawn_point_for(assigned_id, identity.invited_by);

        // Send HandshakeAck with world info.
        self.send_handshake_ack(from_addr, sender_id, assigned_id)
            .await;

        Some(NetworkEvent::PeerConnected {
            id: assigned_id,
            name: player_name,
        })
    }

    // El octavo argumento es el punto repartido de ADR-116. Son los campos del `HandshakeAck`
    // desestructurado uno a uno, que es lo que hace legible el sitio de llamada; agruparlos en un
    // struct sería inventar un tipo para callar a clippy sobre un desempaquetado del protocolo.
    #[allow(clippy::too_many_arguments)]
    fn handle_handshake_ack(
        &mut self,
        from_addr: SocketAddr,
        sender_id: PeerId,
        assigned_id: PeerId,
        world_seed: u64,
        peers: Vec<PeerInfo>,
        phantom_density_scale: f32,
        assigned_spawn: Option<[f32; 3]>,
    ) -> Option<NetworkEvent> {
        if self.is_host {
            return None; // Host doesn't receive handshake acks.
        }

        // ADR-116 D3 — el punto que nos repartió el anfitrión. Se guarda tal cual: bajarlo al
        // suelo es trabajo de `run()`, que es donde vive el ráster de WG3 (D6). `None` = anfitrión
        // anterior a ADR-116 o candidatos agotados; en los dos casos se cae al origen (D7).
        self.assigned_spawn_from_host = assigned_spawn.map(crate::utils::Vec3::from_array);
        if let Some(p) = self.assigned_spawn_from_host {
            info!(
                "ADR-116: el anfitrión reparte a este cliente en ({:.1},{:.1},{:.1})",
                p.x, p.y, p.z
            );
        }

        info!(
            "Handshake ACK received from {} sender_id={} assigned_id={}, world_seed={}, {} peers",
            from_addr,
            sender_id,
            assigned_id,
            world_seed,
            peers.len()
        );
        info!(
            "MPTRACE step=E event=joiner_receive_handshake_ack self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
            self.local_id,
            sender_id,
            assigned_id,
            sender_id,
            from_addr,
            self.peers.len(),
            self.peer_ids()
        );

        // Update our local ID to the one assigned by the host.
        self.local_id = assigned_id;
        self.world_seed = world_seed;
        // ADR-045 Fase 2: the joiner now knows its world_seed — the other half (identity_key,
        // via set_identity) may already be waiting on this in game_loop::run.
        self.world_seed_known = true;
        // P0-2: the host's value always wins — same precedent as world_seed above. Our own
        // env-derived value never reaches PhantomDriver (nothing consumes it before this).
        if self.phantom_density_scale != phantom_density_scale {
            warn!(
                "P0-2: local PHANTOM_DENSITY_SCALE {} differs from host's {}; adopting host's value",
                self.phantom_density_scale, phantom_density_scale
            );
        }
        self.phantom_density_scale = phantom_density_scale;
        self.pending_connect_addr = None;
        // Entramos: el presupuesto de `CONNECT_TIMEOUT` deja de correr aquí. El guard de
        // `retry_pending_connection` (`!self.peers.is_empty()`) ya cubriría esto, pero dejar el
        // marcador puesto haría que un futuro lector creyera que hay un intento vivo.
        self.pending_connect_started_at = None;

        // Quiénes estaban ya: la lista del ack sirve para eso y para nada más (los peers reales
        // se registran cuando llega el roster con su dirección verificada, no desde aquí). Los
        // `relay_only` no cuentan —tampoco se anuncian nunca— y el propio id tampoco.
        self.present_at_join = peers
            .iter()
            .filter(|p| !p.relay_only && p.id != assigned_id)
            .map(|p| p.id)
            .collect();

        // Add the host as a peer.
        let host_peer = PeerConnection::new(sender_id, "Host".to_string(), from_addr);
        self.peers.insert(sender_id, host_peer);
        // ADR-056: remember WHICH peer that is, so a later `PeerDisconnected` can tell the host
        // falling over from any other peer leaving. Set here and nowhere else — this is the one
        // place a joiner learns the host's identity first-hand.
        self.host_peer_id = Some(sender_id);
        info!(
            "MPTRACE step=F event=joiner_register_host self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
            self.local_id,
            sender_id,
            assigned_id,
            sender_id,
            from_addr,
            self.peers.len(),
            self.peer_ids()
        );

        Some(NetworkEvent::PeerConnected {
            id: sender_id,
            name: "Host".into(),
        })
    }

    pub fn peer_count(&self) -> usize {
        self.peers.len()
    }

    /// Orphan cleanup on disconnect (explicit `Disconnect` packet or heartbeat timeout).
    /// `PeerId` is reused after a peer leaves (`allocate_peer_id` just hands out the next free
    /// number), so any state keyed by `PeerId` that outlives the disconnect would silently
    /// apply to whichever different player inherits the number next. `pending_pickups` is
    /// excluded on purpose — it self-purges on its own ~400ms deadline regardless of connection
    /// (ADR-014 drain in `game_loop.rs`), so there is nothing here for it to leak.
    pub(super) fn purge_peer_state(&mut self, id: PeerId) {
        self.voice_echo.remove(&id);
        self.pending_struggles.remove(&id);
        self.processed_corpse_requests
            .retain(|(peer, _)| *peer != id);
        self.last_keepalive_trace_at.remove(&id);
        self.last_transform_trace_at.remove(&id);
        // TAREA 4 (2026-08-31): LAS MARCAS DE INYECTADO TAMBIÉN SON ESTADO INDEXADO POR PeerId, y
        // eran las dos únicas que sobrevivían a la baja. `despawn_phantom` / `despawn_faceling`
        // limpian su set porque son la ruta ORDENADA de retirada; las cuatro rutas de baja no
        // ordenada (timeout, paquete Disconnect, retransmisión agotada, desborde de veredictos)
        // sacaban al peer del mapa y dejaban su id dentro del set para siempre.
        //
        // Lo que deja un id huérfano ahí: `is_phantom(id)` sigue diciendo que sí sobre un peer que
        // ya no existe, y el `despawn_*` posterior devuelve `false` como si nunca hubiera estado.
        // Hoy no es alcanzable en el bucle normal —`game_loop` refresca el latido de los
        // inyectados antes del barrido, justo para que `check_timeouts` no los coseche— pero
        // "inalcanzable mientras nada se atasque" no es una garantía, y esto es exactamente la
        // forma que tiene el riesgo R3 (peers fantasma acumulados) de ser real.
        //
        // Va aquí y no en las cuatro rutas porque las cuatro pasan por esta función; ése era el
        // motivo de que exista.
        self.phantom_ids.remove(&id);
        self.faceling_ids.remove(&id);
        // ADR-136 D3 — la identidad se va con el peer. Sin esto, el siguiente que heredara el
        // número (`allocate_peer_id` los recicla) sería «la misma persona» para cualquier invitado.
        self.platform_ids.retain(|_, peer| *peer != id);
    }

    pub fn peer_ids(&self) -> Vec<PeerId> {
        let mut ids: Vec<PeerId> = self.peers.keys().copied().collect();
        ids.sort_unstable();
        // ADR-146 D3 — la base del tramo también es estado indexado por PeerId: sin esto, el que
        // heredara el número extrapolaría desde la última pose del anterior.
        self.relay_tramo_base.remove(&id);
        ids
    }

    pub fn peer_endpoints(&self) -> Vec<String> {
        let mut endpoints: Vec<String> = self
            .peers
            .values()
            .map(|p| format!("{}={}", p.id, p.addr))
            .collect();
        endpoints.sort();
        endpoints
    }

    /// ADR-116 D3/D4 — el punto de reparto de un peer, elegido una sola vez y recordado.
    ///
    /// **Idempotente por peer**: el handshake tiene tres caminos (peer nuevo, duplicado por id,
    /// duplicado por endpoint) y los tres mandan un `HandshakeAck`. Sin esta memoria, un reintento
    /// de conexión —que es lo normal cuando se pierde el primer datagrama— gastaría una unidad
    /// nueva y le diría al mismo jugador dos sitios distintos.
    ///
    /// Lo ocupado son los puntos YA repartidos más las posiciones de los peers vivos. Las dos
    /// cosas: un jugador restaurado de su fichero está donde nadie lo sorteó (D8), así que no
    /// aparece en la primera lista y tiene que aparecer en la segunda.
    ///
    /// ADR-136 D4/D5/D6 — el punto de un peer que dice venir invitado: **al lado del invitador**,
    /// o el reparto de siempre si no se puede.
    ///
    /// El invitado entra en la unidad de spawn del invitador (ADR-116 D10): no consume unidad
    /// (`next_spawn_unit` no avanza) y la separación mínima no se mide entre ellos. El punto sí se
    /// recuerda en `assigned_spawns`, por lo mismo que los demás: los tres caminos del handshake
    /// tienen que dar el MISMO punto.
    ///
    /// Con `invited_by == 0` es exactamente `assign_spawn_point`.
    pub fn assign_spawn_point_for(
        &mut self,
        peer: PeerId,
        invited_by: u64,
    ) -> Option<crate::utils::Vec3> {
        if let Some(p) = self.assigned_spawns.get(&peer) {
            return Some(*p);
        }
        if invited_by == 0 {
            return self.assign_spawn_point(peer);
        }

        match self.inviter_position(invited_by) {
            Ok((by, anchor)) => {
                let p = crate::world::spawn_distribution::beside(self.world_seed, peer, anchor);
                self.assigned_spawns.insert(peer, p);
                // Enm. 1, Q2 (a): la invitación honrada deja rastro. Es lo único que hace visible
                // un `invited_by` inventado.
                info!(
                    "SPAWN invite={invited_by} by={by} peer={peer} resolved=yes at=({:.1},{:.1},{:.1})",
                    p.x, p.y, p.z
                );
                Some(p)
            }
            Err(reason) => {
                // D6 — nunca bloquea la entrada: se reparte como siempre, con aviso.
                warn!(
                    "SPAWN invite={invited_by} peer={peer} resolved=no reason={reason}; se reparte como siempre"
                );
                self.assign_spawn_point(peer)
            }
        }
    }

    /// ADR-136 D4 — dónde está el invitador según el ROSTER: el anfitrión mismo, o un peer vivo y
    /// real. Nunca una posición que haya escrito el invitado.
    fn inviter_position(
        &self,
        invited_by: u64,
    ) -> Result<(PeerId, crate::utils::Vec3), &'static str> {
        if self.local_platform_id != 0 && invited_by == self.local_platform_id {
            return Ok((
                self.local_id,
                crate::utils::Vec3::from_array(self.local_position),
            ));
        }
        let Some(&id) = self.platform_ids.get(&invited_by) else {
            return Err("unknown_identity");
        };
        let Some(peer) = self.peers.get(&id) else {
            return Err("inviter_gone");
        };
        if peer.relay_only || self.is_phantom(id) {
            return Err("inviter_not_a_player");
        }
        Ok((id, crate::utils::Vec3::from_array(peer.position)))
    }

    /// `None` = no quedaban candidatos válidos; el joiner aplicará el fallback de D7 por su cuenta.
    pub fn assign_spawn_point(&mut self, peer: PeerId) -> Option<crate::utils::Vec3> {
        if let Some(p) = self.assigned_spawns.get(&peer) {
            return Some(*p);
        }

        let mut occupied: Vec<crate::utils::Vec3> =
            self.assigned_spawns.values().copied().collect();
        occupied.extend(
            self.peers
                .values()
                .filter(|p| !self.is_phantom(p.id) && !p.relay_only)
                .map(|p| crate::utils::Vec3::from_array(p.position)),
        );

        let unit = self.next_spawn_unit;
        let chosen =
            crate::world::spawn_distribution::choose_spawn(self.world_seed, unit, &occupied);
        match chosen {
            Some(p) => {
                self.next_spawn_unit = unit.saturating_add(1);
                self.assigned_spawns.insert(peer, p);
                info!(
                    "ADR-116: unidad {} -> peer {} en ({:.1},{:.1},{:.1})",
                    unit, peer, p.x, p.y, p.z
                );
                Some(p)
            }
            None => {
                // La unidad se gasta igual: si no se gastara, el siguiente en entrar volvería a
                // probar la misma secuencia agotada y todos acabarían en el origen a la vez.
                self.next_spawn_unit = unit.saturating_add(1);
                warn!(
                    "ADR-116 D7: sin candidato válido para el peer {peer} (unidad {unit}); \
                     nacerá en el spawn del origen"
                );
                None
            }
        }
    }

    /// Build the `HandshakeAck` payload for `assigned_id` from the current peer table.
    /// Single source for the three handshake paths (new peer / duplicate by id / duplicate by
    /// endpoint), which previously carried byte-identical copies of this block.
    fn build_handshake_ack(&self, assigned_id: PeerId) -> PacketPayload {
        let ack = PacketPayload::HandshakeAck {
            assigned_id,
            world_seed: self.world_seed,
            config: SessionConfig::default(),
            peers: self
                .peers
                .values()
                .map(|p| {
                    // ADR-079: misma regla que build_peer_list — la marca viaja y la addr
                    // inerte de un fantasma no. handle_handshake_ack ignora esta lista hoy
                    // (solo registra al host), pero una superficie que emite peers al wire
                    // sin la regla es exactamente cómo nació el agujero H10.
                    let relay_only = self.is_phantom(p.id) || p.relay_only;
                    PeerInfo {
                        id: p.id,
                        name: p.name.clone(),
                        addr: if relay_only {
                            "0.0.0.0:0".to_string()
                        } else {
                            p.addr.to_string()
                        },
                        position: p.position,
                        relay_only,
                    }
                })
                .collect(),
            anchors: vec![],
            stabilizers: vec![],
            phantom_density_scale: self.phantom_density_scale,
            // ADR-116 D3. Se LEE aquí y se ELIGE en `handle_handshake`, porque construir el ack es
            // `&self`: así los tres caminos del handshake (nuevo, duplicado por id, duplicado por
            // endpoint) mandan el MISMO punto en vez de sortear uno por datagrama.
            assigned_spawn: self.assigned_spawns.get(&assigned_id).map(|p| p.to_array()),
        };
        self.trim_handshake_ack(ack)
    }

    /// TAREA 2 (2026-08-31) — recorta la lista de peers del `HandshakeAck` hasta que el datagrama
    /// quepa en `SAFE_DATAGRAM_BYTES`.
    ///
    /// Medido: 997 B con 8 peers y **1791 B con 16**, contra un techo de 1200. Y este datagrama no
    /// se puede permitir el lujo de perderse: es la ÚNICA respuesta al handshake, va por
    /// `send_raw_to` (no fiable, sin reintento propio), y sin él el joiner agota
    /// `CONNECT_TIMEOUT` y ve "no se pudo conectar" en una sesión que estaba perfectamente viva.
    ///
    /// **Recortar es aquí lo correcto, y no es truncar en silencio.** La lista de peers de este
    /// paquete es una PISTA de arranque que hoy el receptor ni siquiera lee —
    /// `handle_handshake_ack` sólo registra al host— y el roster autoritativo llega por el relay
    /// de `PeerList` a 10 Hz, o sea dentro de los 100 ms siguientes. Lo que NO se puede recortar
    /// es `assigned_id`, `world_seed`, `config` ni `phantom_density_scale`: sin ellos el joiner no
    /// entra, y por eso se quita de la lista y nunca de la cabecera. Cada recorte deja su línea
    /// con cuántos quedaron fuera.
    ///
    /// Paginarlo habría sido peor: exigiría que el joiner supiera esperar N datagramas ANTES de
    /// estar conectado, que es justo el momento en el que menos garantías hay.
    fn trim_handshake_ack(&self, ack: PacketPayload) -> PacketPayload {
        let budget = crate::network::protocol::SAFE_DATAGRAM_BYTES;
        let wire = |payload: &PacketPayload| {
            let header =
                super::protocol::PacketHeader::new(payload.type_code(), self.local_id, 0, 0);
            super::protocol::encode_packet(&header, payload).len()
        };
        if wire(&ack) <= budget {
            return ack;
        }
        let PacketPayload::HandshakeAck {
            assigned_id,
            world_seed,
            config,
            mut peers,
            anchors,
            stabilizers,
            phantom_density_scale,
            assigned_spawn,
        } = ack
        else {
            return ack;
        };
        let full = peers.len();
        while !peers.is_empty() {
            peers.pop();
            let probe = PacketPayload::HandshakeAck {
                assigned_id,
                world_seed,
                config: config.clone(),
                peers: peers.clone(),
                anchors: anchors.clone(),
                stabilizers: stabilizers.clone(),
                phantom_density_scale,
                // ADR-116: el recorte se lleva PEERS, nunca el reparto. Sin punto, el joiner
                // nacería en el origen y el reparto se perdería justo en la sesión llena, que es
                // cuando más falta hace.
                assigned_spawn,
            };
            if wire(&probe) <= budget {
                warn!(
                    "MTUPROBE event=handshake_ack_peers_trimmed self_id={} assigned_id={} kept={} of={} bytes={} budget={budget} — los que faltan llegan por el relay de PeerList (10 Hz)",
                    self.local_id,
                    assigned_id,
                    peers.len(),
                    full,
                    wire(&probe)
                );
                return probe;
            }
        }
        // Ni con la lista vacía cabe: la cabecera sola se pasó del techo. No hay nada más que
        // quitar sin romper el handshake, así que sale y el techo de salida lo rechazará
        // nombrándolo — que es lo que hace falta para que se vea.
        let bare = PacketPayload::HandshakeAck {
            assigned_id,
            world_seed,
            config,
            peers,
            anchors,
            stabilizers,
            phantom_density_scale,
            assigned_spawn,
        };
        log::error!(
            "MTUPROBE event=handshake_ack_exceeds_budget_bare self_id={} assigned_id={} bytes={} budget={budget}",
            self.local_id,
            assigned_id,
            wire(&bare)
        );
        bare
    }

    /// Send the `HandshakeAck` for `assigned_id` to `from_addr`, preceded by the two log lines
    /// that have always accompanied it (`Sending handshake ACK` + `MPTRACE step=D`).
    ///
    /// Serves the same three handshake paths as `build_handshake_ack` (new peer / duplicate by
    /// id / duplicate by endpoint): each kept a byte-identical copy of this tail, differing only
    /// in the NAME of the id passed (`existing.id` vs `assigned_id`). The order is verbatim —
    /// payload first (so its peer snapshot predates the logs), then the two logs, then the
    /// datagram — and both logs still read `self.peers`/`self.peer_ids()` at send time, so the
    /// new-peer path keeps reporting the roster WITH the peer just inserted.
    ///
    /// `&self` because `build_handshake_ack` and `send_raw_to` are both `&self`; that is what
    /// lets the two duplicate branches call it while still holding their `&PeerConnection`.
    async fn send_handshake_ack(
        &self,
        from_addr: SocketAddr,
        sender_id: PeerId,
        assigned_id: PeerId,
    ) {
        let ack_payload = self.build_handshake_ack(assigned_id);
        info!(
            "Sending handshake ACK to {} assigned_id={}",
            from_addr, assigned_id
        );
        info!(
            "MPTRACE step=D event=host_send_handshake_ack self_id={} sender_id={} assigned_id={} peer_id={} endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
            self.local_id,
            sender_id,
            assigned_id,
            assigned_id,
            from_addr,
            self.peers.len(),
            self.peer_ids()
        );
        self.send_raw_to(from_addr, &ack_payload).await;
    }

    fn allocate_peer_id(&mut self, requested_id: PeerId) -> PeerId {
        if requested_id != 0
            && requested_id != self.local_id
            && !self.peers.contains_key(&requested_id)
        {
            return requested_id;
        }

        while self.next_peer_id == 0
            || self.next_peer_id == self.local_id
            || self.peers.contains_key(&self.next_peer_id)
        {
            self.next_peer_id = self.next_peer_id.wrapping_add(1);
            if self.next_peer_id < 2 {
                self.next_peer_id = 2;
            }
        }

        let assigned_id = self.next_peer_id;
        self.next_peer_id = self.next_peer_id.wrapping_add(1);
        if self.next_peer_id < 2 {
            self.next_peer_id = 2;
        }
        assigned_id
    }
}

/// ADR-136 D1/D2 — lo que un handshake dice de QUIÉN es el peer, en bloque. Los dos campos viajan
/// juntos y se leen juntos; un bloque y no dos argumentos sueltos porque son la misma decisión (la
/// identidad opaca y a quién señala con ella), y así `handle_handshake` no crece de dos en dos.
#[derive(Debug, Clone, Copy, Default)]
pub(super) struct HandshakeIdentity {
    pub platform_id: u64,
    pub invited_by: u64,
}

/// P7.3 — la puerta de autoridad de los rosters. Viven aquí y no en `tests.rs` por el índice
/// compartido: aquel fichero lo tiene sucio otra sesión y un hunk mío dentro acabaría en su commit.
#[cfg(test)]
mod authority_tests {
    use std::net::SocketAddr;

    use super::super::protocol::{
        PacketHeader, PacketPayload, PacketType, StpHarvestableInfo, StpItemInfo,
    };
    use super::super::{IncomingPacket, NetworkManager};

    fn harvestable(id: u32) -> StpHarvestableInfo {
        StpHarvestableInfo {
            id,
            position: [18.9, 0.0, 77.8],
            remaining: 1.0,
        }
    }

    fn item(id: u32) -> StpItemInfo {
        StpItemInfo {
            id,
            def_id: 808575401,
            count: 1,
            position: [26.2, 0.1, 26.8],
            rotation: 0.0,
            settling: false,
        }
    }

    /// Una generación completa de UNA página: lo que un roster de verdad manda casi siempre.
    fn empty_harvestable_roster(sender_id: u16, addr: SocketAddr) -> IncomingPacket {
        IncomingPacket {
            addr,
            header: PacketHeader::new(PacketType::StpHarvestableList as u16, sender_id, 0, 0),
            payload: PacketPayload::StpHarvestableList {
                harvestables: Vec::new(),
                generation: 1,
                page: 0,
                page_count: 1,
                cell: [0, 0],
            },
        }
    }

    /// ADR-074 fase 2: una página ya no aplica nada por sí sola — hace falta el cierre de la
    /// ronda, que es lo que dice qué celdas debían venir. Los tests de autoridad mandan los dos.
    fn roster_scope_end(
        sender_id: u16,
        addr: SocketAddr,
        kind: crate::network::protocol::RosterKind,
    ) -> IncomingPacket {
        IncomingPacket {
            addr,
            header: PacketHeader::new(PacketType::RosterScopeEnd as u16, sender_id, 0, 0),
            payload: PacketPayload::RosterScopeEnd {
                kind,
                generation: 1,
                cells: vec![[0, 0]],
            },
        }
    }

    fn empty_item_roster(sender_id: u16, addr: SocketAddr) -> IncomingPacket {
        IncomingPacket {
            addr,
            header: PacketHeader::new(PacketType::StpItemList as u16, sender_id, 0, 0),
            payload: PacketPayload::StpItemList {
                items: Vec::new(),
                generation: 1,
                page: 0,
                page_count: 1,
                cell: [0, 0],
            },
        }
    }

    /// El caso medido en el playtest del 2026-09-02: el host tenía la silla y sus propios
    /// destornilladores, y le llegaron rosters vacíos de otro nodo estampados con SU id.
    #[tokio::test]
    async fn un_host_nunca_espeja_un_roster_ajeno() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        host.stp_harvestables = vec![harvestable(143236849)];
        host.stp_items = vec![item(1073741824)];
        let elsewhere: SocketAddr = "192.168.1.33:64078".parse().unwrap();

        // Con su propio id, con el de un joiner cualquiera, y con uno desconocido.
        for sender in [1u16, 2, 39] {
            let ev = host
                .handle_packet(empty_harvestable_roster(sender, elsewhere))
                .await;
            assert!(ev.is_none());
            let ev = host
                .handle_packet(empty_item_roster(sender, elsewhere))
                .await;
            assert!(ev.is_none());
        }

        assert_eq!(
            host.stp_harvestables.len(),
            1,
            "el host es la autoridad: un roster ajeno no puede vaciarle los muebles"
        );
        assert_eq!(host.stp_harvestables[0].id, 143236849);
        assert_eq!(
            host.stp_items.len(),
            1,
            "ni los objetos que él mismo materializó"
        );
    }

    #[tokio::test]
    async fn un_joiner_solo_espeja_a_su_host() {
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let host_addr: SocketAddr = "127.0.0.1:9812".parse().unwrap();
        joiner.host_peer_id = Some(1);
        joiner.stp_harvestables = vec![harvestable(7)];

        // De otro que no es su host: intacto.
        joiner
            .handle_packet(empty_harvestable_roster(9, host_addr))
            .await;
        joiner
            .handle_packet(roster_scope_end(
                9,
                host_addr,
                crate::network::protocol::RosterKind::HARVESTABLES,
            ))
            .await;
        assert_eq!(
            joiner.stp_harvestables.len(),
            1,
            "un joiner no espeja a cualquiera, sólo a su host"
        );

        // De su host: se aplica, como siempre.
        joiner
            .handle_packet(empty_harvestable_roster(1, host_addr))
            .await;
        joiner
            .handle_packet(roster_scope_end(
                1,
                host_addr,
                crate::network::protocol::RosterKind::HARVESTABLES,
            ))
            .await;
        assert!(
            joiner.stp_harvestables.is_empty(),
            "el roster del host sí sustituye al del joiner — esto NO cambia"
        );
    }

    /// La ventana de arranque de `may_ack_sender`, respetada: antes del `HandshakeAck` el joiner
    /// no sabe quién es su host, y un roster puede adelantar al ack. Cerrar la puerta ahí
    /// costaría una ronda entera de roster por cada uno que llegara antes.
    #[tokio::test]
    async fn antes_del_ack_el_joiner_sigue_aceptando() {
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let host_addr: SocketAddr = "127.0.0.1:9813".parse().unwrap();
        assert!(joiner.host_peer_id.is_none());
        joiner.stp_harvestables = vec![harvestable(7)];

        joiner
            .handle_packet(empty_harvestable_roster(1, host_addr))
            .await;
        joiner
            .handle_packet(roster_scope_end(
                1,
                host_addr,
                crate::network::protocol::RosterKind::HARVESTABLES,
            ))
            .await;
        assert!(
            joiner.stp_harvestables.is_empty(),
            "sin host conocido todavía, el roster se aplica (ventana pre-ack)"
        );
    }
}
