//! Outgoing packets: the `NetworkManager` send surface and the single `send_to` choke point.
//! Split out of `mod.rs` verbatim, except that `broadcast_destinations`, `send_datagram` and
//! `send_raw_to` are `pub(super)` — they were private and are still called from `mod.rs`,
//! `handlers.rs` and `tests.rs`, which are no longer the same module.

use std::net::SocketAddr;

use log::{info, warn};

use super::peer::PeerConnection;
use super::protocol::{encode_packet, PacketHeader, PacketPayload};
use super::{reliability, NetworkManager, PeerId};

/// Carga útil UDP máxima que cabe en una trama Ethernet sin fragmentar: 1500 de MTU − 20 de
/// cabecera IPv4 − 8 de cabecera UDP. Por encima de esto el datagrama viaja en trozos y la
/// pérdida de UNO cualquiera lo pierde entero.
pub const ETHERNET_SAFE_PAYLOAD: usize = 1472;

impl NetworkManager {
    /// MTUPROBE: contabiliza el tamaño de salida y avisa, como mucho una vez por segundo, del
    /// mayor datagrama sobredimensionado visto. No decide nada — solo hace visible una propiedad
    /// que hasta ahora no aparecía en ningún log.
    fn note_datagram_size(&self, len: usize, kind: &str, addr: SocketAddr) {
        use std::sync::atomic::Ordering;
        // El máximo se sigue SIEMPRE, no solo por encima del umbral: "ninguno pasó de 1472" y
        // "el mayor midió 1460" son la misma línea de log en el primer caso y datos muy
        // distintos para decidir. Sin el máximo real no se puede saber cuánto margen queda.
        self.max_datagram_bytes.fetch_max(len, Ordering::Relaxed);
        if len > ETHERNET_SAFE_PAYLOAD {
            self.oversized_datagrams.fetch_add(1, Ordering::Relaxed);
        }

        let now_ms = self.session_start.elapsed().as_millis() as u64;
        let last = self.last_mtu_warn_ms.load(Ordering::Relaxed);
        if now_ms.saturating_sub(last) < 1000 {
            return;
        }
        if self
            .last_mtu_warn_ms
            .compare_exchange(last, now_ms, Ordering::Relaxed, Ordering::Relaxed)
            .is_err()
        {
            return;
        }
        let oversized = self.oversized_datagrams.load(Ordering::Relaxed);
        let max_seen = self.max_datagram_bytes.load(Ordering::Relaxed);
        if oversized > 0 {
            warn!(
                "MTUPROBE event=oversized_datagram self_id={} kind={} dest={} bytes={} safe_max={} total_oversized={} max_seen={}",
                self.local_id, kind, addr, len, ETHERNET_SAFE_PAYLOAD, oversized, max_seen
            );
        } else {
            info!(
                "MTUPROBE event=datagram_size_watermark self_id={} last_kind={} last_bytes={} safe_max={} max_seen={} oversized=0",
                self.local_id, kind, len, ETHERNET_SAFE_PAYLOAD, max_seen
            );
        }
    }

    /// Qué etiquetas de `send_datagram` viajan por la vía fiable. Lista explícita y no un
    /// `!= "unreliable"`: añadir un camino nuevo debe obligar a decidir a qué lado pertenece, no
    /// heredar una respuesta por descarte.
    fn is_reliable_kind(kind: &str) -> bool {
        matches!(
            kind,
            "reliable" | "deferred_reliable" | "retransmit" | "broadcast_reliable"
        )
    }

    // ─── Destino legal ───

    /// TAREA 4 (2026-08-31) — ¿puede salir un datagrama de gameplay hacia este peer?
    ///
    /// Existe porque hasta aquí la estrella se sostenía en DOS sitios distintos: el filtro de
    /// `broadcast_destinations` (B, 2026-08-31) y la convención de que los ~30 envíos dirigidos de
    /// un joiner escriben el literal `1`. Lo segundo no es una garantía, es una costumbre: un
    /// envío dirigido nuevo que tomara el id de un peer cualquiera reabriría el camino
    /// Joiner→Joiner sin tocar una sola línea de los filtros que lo prohíben.
    ///
    /// Las cuatro condiciones, y por qué cada una:
    /// - **fantasma** (ADR-016/043): su `addr` es la inerte `127.0.0.1:1`; un datagrama ahí vuelve
    ///   como `WSAECONNRESET` sobre el socket del emisor (H10, medido en 1.073.132 líneas).
    /// - **`relay_only`** (ADR-079): gemelo del anterior en el lado receptor, misma addr inerte.
    /// - **dirección enrutable**: una sin especificar significa "esta máquina" al enviar, así que
    ///   un peer registrado en ella recibiría los latidos que iban para el peer REAL.
    /// - **estrella** (ADR-015/056): un joiner sólo tiene una ruta de gameplay, el host. El host
    ///   no se filtra: reemitir a todos es exactamente su papel.
    pub(super) fn is_gameplay_destination(&self, peer_id: PeerId) -> bool {
        self.peers
            .get(&peer_id)
            .is_some_and(|p| self.peer_is_gameplay_destination(p))
    }

    /// La misma decisión con el peer ya prestado, para los barridos que iteran `peers.values()`
    /// y no pueden volver a indexar el mapa sin pelearse con el préstamo.
    pub(super) fn peer_is_gameplay_destination(&self, peer: &PeerConnection) -> bool {
        !self.is_phantom(peer.id)
            && !peer.relay_only
            && super::sync::is_routable_peer_addr(&peer.addr)
            && (self.is_host || Some(peer.id) == self.host_peer_id)
    }

    /// Un descarte silencioso es lo que este proyecto ya ha pagado dos veces, así que un destino
    /// ilegal se dice — pero acotado a una línea por segundo GLOBAL: si un camino nuevo se pone a
    /// emitir a 10 Hz hacia un peer prohibido, la evidencia tiene que ser legible, no el nuevo
    /// suelo de ruido. Mismo mecanismo que `last_send_error_log_ms`.
    fn note_illegal_destination(&self, peer_id: PeerId, kind: &str) {
        use std::sync::atomic::Ordering;
        let now_ms = self.session_start.elapsed().as_millis() as u64;
        let last = self.last_illegal_dest_log_ms.load(Ordering::Relaxed);
        if now_ms.saturating_sub(last) < 1000 {
            return;
        }
        if self
            .last_illegal_dest_log_ms
            .compare_exchange(last, now_ms, Ordering::Relaxed, Ordering::Relaxed)
            .is_err()
        {
            return;
        }
        let (relay_only, addr) = self
            .peers
            .get(&peer_id)
            .map(|p| (p.relay_only, Some(p.addr)))
            .unwrap_or((false, None));
        warn!(
            "MPTRACE step=SEND_FAIL event=illegal_gameplay_destination self_id={} kind={} peer_id={} is_host={} host_peer_id={:?} phantom={} relay_only={} endpoint={:?} registered={}",
            self.local_id,
            kind,
            peer_id,
            self.is_host,
            self.host_peer_id,
            self.is_phantom(peer_id),
            relay_only,
            addr,
            self.peers.contains_key(&peer_id)
        );
    }

    // ─── Send methods ───

    /// Send an unreliable packet to a specific peer.
    pub async fn send_unreliable_to(&self, peer_id: PeerId, payload: &PacketPayload) {
        if !self.is_gameplay_destination(peer_id) {
            self.note_illegal_destination(peer_id, "unreliable_to");
            return;
        }
        if let Some(peer) = self.peers.get(&peer_id) {
            let seq = 0; // unreliable packets don't need meaningful sequence
            let header =
                PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
            let data = encode_packet(&header, payload);
            self.send_datagram(&data, peer.addr, "unreliable_to").await;
        }
    }

    /// ADR-015: send an unreliable packet to `dest_peer` stamped with an ARBITRARY
    /// `sender_id` in the header (instead of `self.local_id`). The host uses this to
    /// RELAY another peer's `PlayerUpdate` "on behalf of" that peer, so a joiner —
    /// which only connects to the host — still learns the rotation+animation of the
    /// OTHER joiners (PeerInfo/PeerList carry only position). The receive path is
    /// unchanged: the destination updates `peers[sender_id]` exactly as for a genuine
    /// PlayerUpdate. Also the mechanism ADR-016 will reuse to propagate a phantom
    /// peer's pose (sender_id = phantom_id) — kept generic over `sender_id`/`dest`.
    pub async fn send_unreliable_as(
        &self,
        sender_id: PeerId,
        dest_peer: PeerId,
        payload: &PacketPayload,
    ) {
        let data = self.encode_relay_as(sender_id, payload);
        self.send_prepared_unreliable(dest_peer, &data).await;
    }

    /// F0.2 (E0, ADR-073): la mitad de `send_unreliable_as` que NO depende del destino.
    ///
    /// El relay de poses es O(N²) y re-serializaba el MISMO `PlayerUpdate` una vez por cada par
    /// (origen, destino): con 8 peers son 56 serializaciones por ronda a 10 Hz para 8 payloads
    /// distintos. Nada del contenido cambia entre destinos —el header lleva el id del ORIGEN, y
    /// la secuencia de un no-fiable es 0—, así que encodear una vez por origen produce
    /// exactamente los mismos bytes en el aire. No es un cambio de wire: es la misma cadena de
    /// bytes, calculada una vez en vez de D veces.
    ///
    /// Deliberadamente NO se hace lo análogo en `broadcast_reliable`: allí cada peer necesita su
    /// propia `sequence` en el header (es lo que rastrea el ACK), y además E1 (ADR-074) va a
    /// hacer el payload de los rosters dependiente del destinatario, con lo que ese cacheo
    /// tendría dos meses de vida. Este sobrevive a E1: la pose de A es idéntica para todo destino
    /// dentro de su AOI.
    pub(super) fn encode_relay_as(&self, sender_id: PeerId, payload: &PacketPayload) -> Vec<u8> {
        let header = PacketHeader::new(payload.type_code(), sender_id, 0, self.timestamp());
        encode_packet(&header, payload)
    }

    /// F0.2: la otra mitad — enviar bytes YA encodeados a un destino. `send_unreliable_as` se
    /// define sobre estas dos para que no existan dos caminos de encode que puedan divergir; el
    /// test `a_cached_relay_encode_is_byte_identical_to_the_per_destination_one` lo congela.
    pub(super) async fn send_prepared_unreliable(&self, dest_peer: PeerId, data: &[u8]) {
        if !self.is_gameplay_destination(dest_peer) {
            self.note_illegal_destination(dest_peer, "relay_as");
            return;
        }
        if let Some(peer) = self.peers.get(&dest_peer) {
            self.send_datagram(data, peer.addr, "relay_as").await;
        }
    }

    /// Broadcast an unreliable packet to all connected peers.
    pub async fn broadcast_unreliable(&self, payload: &PacketPayload) {
        let header = PacketHeader::new(payload.type_code(), self.local_id, 0, self.timestamp());
        let data = encode_packet(&header, payload);
        for (_, addr) in self.broadcast_destinations() {
            self.send_datagram(&data, addr, "broadcast_unreliable")
                .await;
        }
    }

    /// Addresses an unreliable broadcast may legitimately be sent to: every real peer, never a
    /// phantom. Split out — like `sync::relay_destinations` and for the same reason — so the
    /// invariant is testable without a socket.
    ///
    /// This closes the LAST hole in ADR-043's rule ("DESTINATIONS exclude phantoms; SOURCES do
    /// not"). ADR-043 applied the filter to the pose relay, ADR-046 to the voice relay and
    /// ADR-016 to WorldSync, but `broadcast_unreliable` — the path the HOST'S OWN pose and the
    /// peer roster take — was still addressing phantoms.
    ///
    /// It is not a micro-optimisation. A phantom's `addr` is the inert `127.0.0.1:1` stamped at
    /// injection, so every datagram aimed at one is a real syscall to a dead loopback port; on
    /// Windows the ICMP port-unreachable comes back as WSAECONNRESET **on the socket**. With the
    /// world populated by default since ADR-043 (five phantoms, ~10 call sites, 10-20 Hz) that
    /// poisoned the host's own socket — measured at 1,073,132 `os error 10054` lines in a single
    /// play-test log. The symptom is precisely asymmetric and is what it cost to find: joiners saw
    /// each other (relayed through the already-filtered path) while the HOST was invisible to
    /// everyone, because its own pose and the roster are the two things that ride this one.
    ///
    /// Before ADR-043 a single phantom lived behind an env flag that defaults OFF, so the defect
    /// was unreachable rather than absent.
    pub(super) fn broadcast_destinations(&self) -> Vec<(PeerId, SocketAddr)> {
        self.peers
            .values()
            // TAREA 4 (2026-08-31): las cuatro condiciones viven ahora en
            // `peer_is_gameplay_destination` — este barrido y los cuatro envíos DIRIGIDOS aplican
            // exactamente la misma, que es lo que impide que una superficie nueva herede media.
            //
            // ADR-079: relay_only is the receiver-side twin of the phantom mark — same inert
            // addr, same H10 poison if addressed. Both are filtered there.
            //
            // Auditoría de heartbeat (2026-08-30): una dirección sin especificar significa "esta
            // máquina" al enviar, así que un peer registrado en ella recibiría sus propios
            // latidos y el peer REAL ninguno. `is_routable_peer_addr` ya lo impide en el
            // registro; esto lo hace imposible también para cualquier futuro camino que escriba
            // `peer.addr` sin pasar por allí.
            //
            // B (2026-08-31) — LA TOPOLOGÍA ES UNA ESTRELLA, Y AQUÍ NO LO ERA.
            //
            // Un joiner registra a los OTROS joiners con la dirección real que el host le reporta
            // en `PeerList` (`handlers.rs:219-221`; `PeerInfo` lleva `addr`). Sin este filtro,
            // todo lo que pasa por `broadcast_unreliable` —su propia pose, la primera— salía
            // también DIRECTAMENTE a cada uno de ellos. Eso contradice ADR-015, que dice que el
            // host reemite, y sólo era alcanzable con 3+ jugadores: con uno solo, el único peer
            // del joiner ES el host y no había nada que distinguir.
            //
            // Coste real, no teórico: entre redes distintas nadie ha perforado nada, así que el
            // NAT tira esos datagramas y en Windows cada rebote vuelve como `WSAECONNRESET`
            // (10054) SOBRE EL SOCKET del emisor — el mismo mecanismo que ADR-043 midió en
            // 1.073.132 líneas en un solo playtest con direcciones inertes.
            //
            // El host no se filtra: reemitir a todos es precisamente su papel.
            .filter(|p| self.peer_is_gameplay_destination(p))
            .map(|p| (p.id, p.addr))
            .collect()
    }

    /// Send a reliable packet to a specific peer (queued for ACK tracking).
    pub async fn send_reliable(&mut self, peer_id: PeerId, payload: &PacketPayload) {
        let seq = self.next_sequence();
        let header = PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
        let data = encode_packet(&header, payload);

        // Igual que en process_retransmits: se resuelve la dirección con un préstamo INMUTABLE,
        // se envía, y solo después se vuelve a pedir el mutable para encolar el reenvío.
        // TAREA 4: mismo predicado que el resto de la superficie de envío. Encolar un fiable
        // hacia un destino ilegal es peor que emitir un no-fiable hacia él: no se descarta, se
        // reenvía cinco veces y termina expulsando al peer (ADR-062).
        if !self.is_gameplay_destination(peer_id) {
            self.note_illegal_destination(peer_id, "reliable");
            return;
        }
        let Some(peer) = self.peers.get(&peer_id) else {
            return;
        };
        let addr = peer.addr;

        // Control de ventana. `can_queue_reliable` existía desde la Fase 3 y NO lo llamaba
        // nadie: la cola crecía sin tope por peer, y al llegar a MAX_RETRIES el barrido la
        // vacía ENTERA (`peer.reliable_queue.clear()`), tirando también lo que aún era
        // recuperable. Con la ventana llena se descarta el paquete NUEVO y se dice en el log,
        // en vez de acumular presión hasta el borrado masivo.
        if !peer.can_queue_reliable() {
            warn!(
                "MPTRACE step=SEND_FAIL event=reliable_window_full self_id={} peer_id={} type=0x{:02x} in_flight={} window={} dropped_bytes={}",
                self.local_id,
                peer_id,
                payload.type_code(),
                peer.reliable_queue.len(),
                reliability::WINDOW_SIZE,
                data.len()
            );
            return;
        }

        // TAREA 2: si el techo lo rechazó, NO se encola. Reenviarlo cinco veces produciría cinco
        // rechazos idénticos y la expulsión del peer por `MAX_RETRIES`, que es un daño mucho mayor
        // que el paquete perdido. El `error!` del rechazo ya nombra el tipo y el tamaño.
        if !self.send_datagram(&data, addr, "reliable").await {
            return;
        }
        if let Some(peer) = self.peers.get_mut(&peer_id) {
            peer.queue_reliable(seq, data);
        }
    }

    /// F0.3 (E0, ADR-073): tope de la cola diferida de VEREDICTOS de un peer, antes de tratar el
    /// desborde como condición fatal de ese peer.
    ///
    /// Dimensionado contra la peor ráfaga LEGÍTIMA medida, con margen ×3 — un cap corto convierte
    /// una ráfaga honesta en desconexiones aleatorias, que es peor que el bug que F0.3 cierra. El
    /// peor caso legítimo es un goteo de mundo completo aparcado por delante (49 chunks + End =
    /// 50 entradas, medido en `perf-baseline.md`) más una ráfaga de loot intensa (~20 veredictos
    /// entre pickups, tomas de cadáver y veredictos PvP): ~70, ×3 ≈ 210, redondeado a 256.
    pub const VERDICT_QUEUE_CAP: usize = 256;

    /// F0.3: envío de un VEREDICTO host→cliente (pickup concedido, resultado de una toma de
    /// cadáver, daño PvP concedido o rechazado, ataque de fantasma concedido).
    ///
    /// El defecto que cierra: estos iban por `send_reliable`, que con la ventana llena (32)
    /// **DESCARTA el paquete nuevo** y solo deja un warn. Un veredicto perdido no se auto-cura
    /// —no hay roster que lo reponga— así que el cliente se queda creyendo que tiene un objeto
    /// que el host ya no le da, o al revés. Ahora van por la cola diferida (ADR-060), que los
    /// entrega en cuanto los ACKs abren hueco.
    ///
    /// **Política de desborde: fatal para ese peer, nunca descarte.** Si su cola supera
    /// `VERDICT_QUEUE_CAP`, ese peer lleva ya cientos de veredictos sin confirmar: su inventario
    /// y el del host divergieron hace rato, y seguir descartando (aunque sea con log) es
    /// reintroducir el bug con más pasos. Se le desconecta por el MISMO camino que ADR-062 usa al
    /// agotar retransmisiones —`peers.remove` + `purge_peer_state` + `PeerDisconnected`—, que en
    /// un joiner termina la sesión (ADR-056) y le hace re-sincronizar entero al volver.
    pub async fn send_verdict(&mut self, peer_id: PeerId, payload: &PacketPayload) {
        let queued = self
            .peers
            .get(&peer_id)
            .map(|p| p.deferred_reliable.len())
            .unwrap_or(0);
        if queued >= Self::VERDICT_QUEUE_CAP {
            self.drop_peer_over_verdict_backlog(peer_id, queued);
            return;
        }
        self.send_reliable_queued(peer_id, payload).await;
    }

    /// F0.3: el desborde de arriba. Separado para que el camino fatal se lea de un vistazo y sea
    /// testeable sin fabricar 256 veredictos reales.
    fn drop_peer_over_verdict_backlog(&mut self, peer_id: PeerId, queued: usize) {
        let Some(peer) = self.peers.remove(&peer_id) else {
            return;
        };
        self.purge_peer_state(peer_id);
        log::warn!(
            "Peer {} ({}) disconnected: verdict queue overflow ({} deferred >= cap {}) — its \
             inventory state has diverged from the host's and cannot be reconciled in place",
            peer.name,
            peer.addr,
            queued,
            Self::VERDICT_QUEUE_CAP
        );
        log::info!(
            "MPTRACE step=F03 event=peer_removed reason=verdict_queue_overflow self_id={} peer_id={} endpoint={} queued_deferred={} cap={} peer_count_after={}",
            self.local_id,
            peer_id,
            peer.addr,
            queued,
            Self::VERDICT_QUEUE_CAP,
            self.peers.len()
        );
        self.push_pending_event(crate::network::NetworkEvent::PeerDisconnected {
            id: peer_id,
            reason: "verdict queue overflow".into(),
        });
    }

    /// ADR-060: reliable con espera en vez de descarte. Igual que `send_reliable` salvo el caso
    /// de ventana llena: el paquete se aparca en la cola diferida FIFO del peer y
    /// `pump_deferred_reliable` lo emitirá cuando los ACKs abran hueco. También se aparca si ya
    /// HAY diferidos aunque la ventana tenga sitio — saltarse la cola rompería el FIFO.
    ///
    /// Para tráfico suelto `send_reliable` sigue siendo el default correcto: su descarte-con-warn
    /// es la protección de ADR-039 contra colas sin tope. Esta variante existe para EMISIONES EN
    /// LOTE con final conocido (el goteo de WorldSync), donde el emisor purga y re-encola en
    /// bloque y el tope real es el tamaño del lote.
    pub async fn send_reliable_queued(&mut self, peer_id: PeerId, payload: &PacketPayload) {
        let seq = self.next_sequence();
        let header = PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
        let data = encode_packet(&header, payload);

        // TAREA 4: mismo contrato que send_reliable, y con una razón extra propia — lo que aquí
        // no se puede enviar no se descarta, se APARCA, así que un destino ilegal llenaría la
        // cola diferida hasta el tope fatal de `VERDICT_QUEUE_CAP`.
        if !self.is_gameplay_destination(peer_id) {
            self.note_illegal_destination(peer_id, "deferred_reliable");
            return;
        }
        let Some(peer) = self.peers.get(&peer_id) else {
            return;
        };
        let addr = peer.addr;

        if !peer.can_queue_reliable() || !peer.deferred_reliable.is_empty() {
            let bytes = data.len();
            if let Some(peer) = self.peers.get_mut(&peer_id) {
                peer.defer_reliable(seq, data);
                log::info!(
                    "RELTRACE event=RELIABLE_DEFERRED self_id={} peer_id={peer_id} seq={seq} type=0x{:02X} bytes={bytes} in_flight={} deferred={}",
                    self.local_id,
                    payload.type_code(),
                    peer.reliable_queue.len(),
                    peer.deferred_reliable.len()
                );
            }
            return;
        }

        let bytes = data.len();
        // TAREA 2: mismo criterio que en `send_reliable` — lo rechazado por el techo no se encola.
        if !self.send_datagram(&data, addr, "reliable").await {
            return;
        }
        if let Some(peer) = self.peers.get_mut(&peer_id) {
            peer.queue_reliable(seq, data);
            log::info!(
                "RELTRACE event=RELIABLE_SENT self_id={} peer_id={peer_id} seq={seq} type=0x{:02X} bytes={bytes} in_flight={} window={} deferred={}",
                self.local_id,
                payload.type_code(),
                peer.reliable_queue.len(),
                reliability::WINDOW_SIZE,
                peer.deferred_reliable.len()
            );
        }
    }

    /// Broadcast a reliable packet to all peers.
    ///
    /// ADR-016: phantom peers are skipped — their addr is inert (nobody listens), so a
    /// reliable packet to one would never be ACKed and just pile up retransmits. Real
    /// peers still receive it.
    pub async fn broadcast_reliable(&mut self, payload: &PacketPayload) {
        // TAREA 4 (2026-08-31): ÉSTE ERA EL AGUJERO LATENTE DE LA ESTRELLA. Filtraba fantasmas y
        // `relay_only` pero NO el rol, así que un joiner habría emitido un fiable directo a cada
        // uno de sus pares — y un fiable no se pierde y ya está: se reenvía cinco veces y termina
        // expulsando al peer (ADR-062). Hoy no era alcanzable (`broadcast_anchor` y
        // `broadcast_stabilizer`, sus únicos llamadores, no tienen ni un call site), y por eso
        // exactamente había que cerrarlo ahora: el día que alguien los llame no va a acordarse.
        //
        // ADR-079: relay_only peers are as unACKable as phantoms — a reliable packet to one would
        // pile retransmits until ADR-062 evicted it, then the roster re-adds it: an evict/re-add
        // loop for free. Ambas condiciones viven ya en `peer_is_gameplay_destination`.
        let peer_addrs: Vec<(PeerId, SocketAddr)> = self
            .peers
            .values()
            .filter(|p| self.peer_is_gameplay_destination(p))
            .map(|p| (p.id, p.addr))
            .collect();

        for (pid, addr) in peer_addrs {
            let seq = self.next_sequence();
            let header =
                PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
            let data = encode_packet(&header, payload);
            // TAREA 2: mismo criterio que en `send_reliable`.
            if !self.send_datagram(&data, addr, "broadcast_reliable").await {
                continue;
            }
            if let Some(peer) = self.peers.get_mut(&pid) {
                peer.queue_reliable(seq, data);
            }
        }
    }

    /// The single outgoing-datagram choke point. Every `send_to` in this file goes through
    /// here so that a failed send can never be silent again.
    ///
    /// The eight call sites this replaced were all `let _ = self.socket.send_to(...)`, which
    /// swallowed `EMSGSIZE` — the exact error produced once a full-roster payload outgrows the
    /// 65507 B IPv4 datagram limit (`StpBuildingList` reaches it somewhere around ~800 placed
    /// pieces). That failure is indistinguishable from packet loss: no log, no error, and no
    /// visible relation to anything the player did. Building replication would simply stop one
    /// day, for everyone, permanently. The payload size travels in the log because the size IS
    /// the diagnosis.
    ///
    /// TAREA 2 (2026-08-31) — devuelve si los bytes llegaron a salir por el socket. Lo necesitan
    /// los caminos FIABLES: encolar en `queue_reliable` algo que el techo acaba de rechazar son
    /// cinco retransmisiones idénticas, cinco rechazos idénticos y la expulsión del peer al agotar
    /// `MAX_RETRIES` (ADR-062) — el rechazo sería peor que el problema que evita.
    ///
    /// Takes `&self` (several broadcast paths are `&self`), hence the atomic throttle.
    pub(super) async fn send_datagram(&self, data: &[u8], addr: SocketAddr, kind: &str) -> bool {
        // ─── TAREA 2 (2026-08-31): EL TECHO, APLICADO ANTES DEL `send_to` REAL ───
        //
        // Hasta aquí esto sólo GRITABA, y sólo para los caminos fiables. Un aviso no es una
        // invariante: los no fiables seguían poniendo en el cable datagramas de hasta 1881 B
        // medidos (31.004 por sesión), que IP trocea y de los que basta perder un fragmento para
        // perder el paquete entero — y los 4 únicos reenvíos de la sesión física de 9 min fueron
        // exactamente los 4 datagramas por encima del techo.
        //
        // Ahora se RECHAZA. No es tirar datos a la basura: cada productor de tamaño ilimitado
        // pagina, trocea o acota antes de llegar aquí, así que esto es el fondo de saco que
        // convierte "no debería pasar" en "no puede pasar". Que salte significa que hay un emisor
        // sin acotar, y por eso se registra con `error!` y con su etiqueta: es un defecto del
        // emisor, no una condición de red.
        if data.len() > crate::network::protocol::SAFE_DATAGRAM_BYTES {
            let reliable = Self::is_reliable_kind(kind);
            if reliable {
                self.oversized_reliable
                    .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            }
            self.refused_datagrams
                .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            log::error!(
                "MTUPROBE event=datagram_refused_over_budget self_id={} kind={} reliable={} dest={} bytes={} budget={} — NO se ha enviado: el emisor de este tipo no está acotado",
                self.local_id,
                kind,
                reliable,
                addr,
                data.len(),
                crate::network::protocol::SAFE_DATAGRAM_BYTES
            );
            return false;
        }

        // MTUPROBE — un datagrama por encima de la MTU de Ethernet se fragmenta en IP, y perder UN
        // fragmento pierde el datagrama ENTERO. En loopback la MTU es de 64 KB, así que esto no
        // duele jamás en localhost y sí en cuanto hay un cable de por medio: es la asimetría exacta
        // "en mi máquina va, con mi amigo no". Se cuenta aquí, en el único punto de salida.
        //
        // TAREA 2: DESPUÉS del techo, no antes. La marca de agua tiene que decir cuánto mide lo que
        // de verdad SALE — si contara también lo rechazado, el número que se lee para saber cuánto
        // margen queda estaría midiendo justo lo que no se envió, y `max_seen > 1200` dejaría de
        // ser imposible. Lo rechazado ya lleva su propia línea con su tamaño.
        self.note_datagram_size(data.len(), kind, addr);

        // ─── ADR-117: LA SALIDA POR RELAY ───
        //
        // Es uno de los dos únicos puntos del backend que saben que el relay existe. Una dirección
        // sintética NO se puede pasar a `send_to`: el socket hace bind en `0.0.0.0` (IPv4) y el
        // sistema fallaría al enrutar una IPv6 con un error que no señala a nada.
        //
        // La comprobación va AQUÍ y no en los llamantes porque los llamantes son decenas y los
        // puntos de salida son dos. El techo de ADR-113 ya se ha aplicado arriba, sobre el payload
        // de gameplay: el sobre suma 16 B y el datagrama en el cable llega a 1216 (enmienda 3).
        if crate::network::transport::is_synthetic(&addr) {
            return self.send_via_relay(data, addr, kind).await;
        }

        let Err(e) = self.socket.send_to(data, addr).await else {
            return true;
        };
        use std::sync::atomic::Ordering;
        let now_ms = self.session_start.elapsed().as_millis() as u64;
        let last = self.last_send_error_log_ms.load(Ordering::Relaxed);
        if now_ms.saturating_sub(last) >= 1000
            && self
                .last_send_error_log_ms
                .compare_exchange(last, now_ms, Ordering::Relaxed, Ordering::Relaxed)
                .is_ok()
        {
            warn!(
                "MPTRACE step=SEND_FAIL event=datagram_send_failed self_id={} kind={} dest={} payload_bytes={} err={}",
                self.local_id,
                kind,
                addr,
                data.len(),
                e
            );
        }
        // El socket falló: no salió. Distinto del rechazo por techo (que es culpa del emisor) pero
        // el retorno es el mismo, y por lo mismo — encolar un fiable que no salió no lo arregla.
        false
    }

    /// Mete el datagrama en un sobre de relay y lo manda a la dirección REAL del relay.
    ///
    /// Separado de `send_datagram` para que el camino directo —el que hoy funciona— no gane ni una
    /// rama ni una copia. Aquí sí hay una copia del payload, y está justificada en `RelayLink::wrap`.
    async fn send_via_relay(&self, data: &[u8], synthetic: SocketAddr, kind: &str) -> bool {
        let Some(link) = &self.relay else {
            // Hay dos situaciones aquí y son muy distintas, así que no se registran igual.
            //
            // La NORMAL, medida en la prueba en vivo del 2026-09-02: la secuencia arranca la etapa
            // de relay y manda el primer handshake en el mismo instante, cuando el registro
            // todavía está en vuelo (tarda un latido). No es un fallo — el reintento de un segundo
            // después lo manda ya con enlace, que es exactamente lo que pasó— y sacarlo como
            // ERROR sería enseñar un problema donde no lo hay.
            //
            // La ANORMAL: hay un peer con dirección sintética y NO hay ningún registro en curso.
            // Eso sí es un defecto, y en silencio sería un peer que deja de recibir sin motivo
            // visible.
            let registrando = matches!(
                self.relay_client.as_ref().map(|c| c.state()),
                Some(crate::network::relay_client::RelayClientState::Registering)
            );
            if registrando {
                log::debug!(
                    "RELAY event=send_deferred self_id={} kind={kind} dest={synthetic} — el registro \
                     con el relay sigue en vuelo; se reintentará",
                    self.local_id
                );
            } else {
                log::error!(
                    "RELAY event=send_without_link self_id={} kind={kind} dest={synthetic} — hay un \
                     peer relayado registrado pero esta sesión no tiene enlace con el relay",
                    self.local_id
                );
            }
            return false;
        };

        let Some(dst) = crate::network::transport::synthetic_peer(&synthetic) else {
            return false;
        };

        let wrapped = match link.wrap(data, dst) {
            Ok(bytes) => bytes,
            Err(e) => {
                // El techo ya se comprobó en `send_datagram`, así que esto sólo puede saltar si
                // las dos constantes se separan. Es exactamente el fallo que el test de espejo
                // `el_techo_del_payload_es_el_de_adr_113` existe para impedir.
                log::error!(
                    "RELAY event=wrap_failed self_id={} kind={kind} dest_peer={dst} error={e}",
                    self.local_id
                );
                return false;
            }
        };

        if let Err(e) = self.socket.send_to(&wrapped, link.relay_addr).await {
            use std::sync::atomic::Ordering;
            let now_ms = self.session_start.elapsed().as_millis() as u64;
            let last = self.last_send_error_log_ms.load(Ordering::Relaxed);
            if now_ms.saturating_sub(last) >= 1000
                && self
                    .last_send_error_log_ms
                    .compare_exchange(last, now_ms, Ordering::Relaxed, Ordering::Relaxed)
                    .is_ok()
            {
                warn!(
                    "RELAY event=send_failed self_id={} kind={kind} relay={} dest_peer={dst} \
                     payload_bytes={} wire_bytes={} err={e}",
                    self.local_id,
                    link.relay_addr,
                    data.len(),
                    wrapped.len(),
                );
            }
            return false;
        }

        true
    }

    /// Send a raw encoded packet to an address (used for handshake responses
    /// before the peer is in the peers map).
    pub(super) async fn send_raw_to(&self, addr: SocketAddr, payload: &PacketPayload) {
        let seq = 0;
        let header = PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
        let data = encode_packet(&header, payload);
        self.send_datagram(&data, addr, "raw").await;
    }
}
