//! Outgoing packets: the `NetworkManager` send surface and the single `send_to` choke point.
//! Split out of `mod.rs` verbatim, except that `broadcast_destinations`, `send_datagram` and
//! `send_raw_to` are `pub(super)` — they were private and are still called from `mod.rs`,
//! `handlers.rs` and `tests.rs`, which are no longer the same module.

use std::net::SocketAddr;

use log::warn;

use super::protocol::{encode_packet, PacketHeader, PacketPayload};
use super::{reliability, NetworkManager, PeerId};

impl NetworkManager {
    // ─── Send methods ───

    /// Send an unreliable packet to a specific peer.
    pub async fn send_unreliable_to(&self, peer_id: PeerId, payload: &PacketPayload) {
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
        if let Some(peer) = self.peers.get(&dest_peer) {
            let header = PacketHeader::new(payload.type_code(), sender_id, 0, self.timestamp());
            let data = encode_packet(&header, payload);
            self.send_datagram(&data, peer.addr, "relay_as").await;
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
            .filter(|p| !self.is_phantom(p.id))
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

        self.send_datagram(&data, addr, "reliable").await;
        if let Some(peer) = self.peers.get_mut(&peer_id) {
            peer.queue_reliable(seq, data);
        }
    }

    /// Broadcast a reliable packet to all peers.
    ///
    /// ADR-016: phantom peers are skipped — their addr is inert (nobody listens), so a
    /// reliable packet to one would never be ACKed and just pile up retransmits. Real
    /// peers still receive it.
    pub async fn broadcast_reliable(&mut self, payload: &PacketPayload) {
        let peer_addrs: Vec<(PeerId, SocketAddr)> = self
            .peers
            .iter()
            .filter(|(id, _)| !self.phantom_ids.contains(id))
            .map(|(id, p)| (*id, p.addr))
            .collect();

        for (pid, addr) in peer_addrs {
            let seq = self.next_sequence();
            let header =
                PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
            let data = encode_packet(&header, payload);
            self.send_datagram(&data, addr, "broadcast_reliable").await;
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
    /// Takes `&self` (several broadcast paths are `&self`), hence the atomic throttle.
    pub(super) async fn send_datagram(&self, data: &[u8], addr: SocketAddr, kind: &str) {
        let Err(e) = self.socket.send_to(data, addr).await else {
            return;
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
