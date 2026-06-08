//! P2P networking domain (UDP mesh).
//!
//! `NetworkManager` owns the UDP socket, tracks peer connections, handles the
//! reliability layer, and produces `NetworkEvent`s for the game loop.

pub mod peer;
pub mod protocol;
pub mod reliability;
pub mod sync;

use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::Instant;

use log::{debug, info, warn};
use tokio::net::UdpSocket;
use tokio::sync::mpsc;

use peer::PeerConnection;
use protocol::{
    encode_packet, decode_packet, ChunkSyncData, PacketHeader, PacketPayload, PeerInfo,
    SessionConfig, HEADER_SIZE,
};
use reliability::is_reliable;

/// Identifier for a peer within a session.
pub type PeerId = u16;

/// High-level events produced by the network layer for the game loop.
#[derive(Debug, Clone)]
pub enum NetworkEvent {
    PeerConnected {
        id: PeerId,
        name: String,
    },
    PeerDisconnected {
        id: PeerId,
        reason: String,
    },
    RemotePlayerUpdate {
        id: PeerId,
        position: [f32; 3],
        rotation: f32,
        animation: String,
    },
    WorldSyncReceived {
        chunks: Vec<ChunkSyncData>,
    },
    ChunkTransferReceived {
        from: PeerId,
        data: ChunkSyncData,
    },
    ChunkTransferAckReceived {
        from: PeerId,
        pos: [i32; 2],
    },
    ChunkTeleportReceived {
        old_pos: [i32; 2],
        new_pos: [i32; 2],
        new_seed: u64,
    },
    AnchorBroadcastReceived {
        chunk_pos: [i32; 2],
        durability: f32,
        installed_by: String,
    },
    StabilizerBroadcastReceived {
        chunk_pos: [i32; 2],
        tier: u8,
        remaining_hours: f32,
    },
    HandshakeReceived {
        from_addr: SocketAddr,
        player_name: String,
    },
}

/// Incoming packet from the receive loop.
struct IncomingPacket {
    addr: SocketAddr,
    header: PacketHeader,
    payload: PacketPayload,
}

/// The central networking coordinator. Owns the UDP socket, tracks peers,
/// handles reliability, and bridges between raw packets and game-level events.
pub struct NetworkManager {
    socket: Arc<UdpSocket>,
    pub local_id: PeerId,
    pub is_host: bool,
    pub peers: HashMap<PeerId, PeerConnection>,
    incoming_rx: mpsc::Receiver<IncomingPacket>,
    pub session_start: Instant,
    next_peer_id: PeerId,
    pub world_seed: u64,
    global_sequence: u32,
    pub local_name: String,
}

impl NetworkManager {
    /// Bind a UDP socket and start the receive loop.
    pub async fn bind(
        port: u16,
        local_id: PeerId,
        world_seed: u64,
        is_host: bool,
    ) -> std::io::Result<Self> {
        let addr: SocketAddr = format!("0.0.0.0:{port}").parse().unwrap();
        let socket = Arc::new(UdpSocket::bind(addr).await?);
        let local_addr = socket.local_addr()?;
        info!("P2P UDP socket bound on {local_addr}");

        let (tx, rx) = mpsc::channel::<IncomingPacket>(512);
        let recv_socket = socket.clone();
        tokio::spawn(receive_loop(recv_socket, tx));

        Ok(Self {
            socket,
            local_id,
            is_host,
            peers: HashMap::new(),
            incoming_rx: rx,
            session_start: Instant::now(),
            next_peer_id: if is_host { 2 } else { 0 },
            world_seed,
            global_sequence: 0,
            local_name: format!("Player{local_id}"),
        })
    }

    pub fn local_addr(&self) -> SocketAddr {
        self.socket.local_addr().unwrap_or_else(|_| "0.0.0.0:0".parse().unwrap())
    }

    fn timestamp(&self) -> u32 {
        self.session_start.elapsed().as_millis() as u32
    }

    fn next_sequence(&mut self) -> u32 {
        self.global_sequence = self.global_sequence.wrapping_add(1);
        self.global_sequence
    }

    /// Initiate a connection to a remote peer (joiner → host).
    pub async fn initiate_connection(&self, addr: SocketAddr) {
        info!("Initiating connection to {addr}");
        let payload = PacketPayload::Handshake {
            player_name: self.local_name.clone(),
            version: "0.1.0".into(),
        };
        let header = PacketHeader::new(payload.type_code(), self.local_id, 0, self.timestamp());
        let data = encode_packet(&header, &payload);
        let _ = self.socket.send_to(&data, addr).await;
    }

    /// Process all incoming packets and return game-level events.
    pub async fn process_incoming(&mut self) -> Vec<NetworkEvent> {
        let mut incoming = Vec::new();
        while let Ok(pkt) = self.incoming_rx.try_recv() {
            incoming.push(pkt);
        }

        let mut events = Vec::new();
        for pkt in incoming {
            events.extend(self.handle_packet(pkt).await);
        }
        events
    }

    /// Send heartbeats to all peers.
    pub async fn send_heartbeats(&self) {
        let payload = PacketPayload::Heartbeat;
        self.broadcast_unreliable(&payload).await;
    }

    /// Check for timed-out peers. Returns disconnect events.
    pub fn check_timeouts(&mut self) -> Vec<NetworkEvent> {
        let mut events = Vec::new();
        let timed_out: Vec<PeerId> = self
            .peers
            .values()
            .filter(|p| p.is_timed_out())
            .map(|p| p.id)
            .collect();

        for id in timed_out {
            if let Some(peer) = self.peers.remove(&id) {
                info!("Peer {} ({}) timed out", peer.name, peer.addr);
                events.push(NetworkEvent::PeerDisconnected {
                    id,
                    reason: "heartbeat timeout".into(),
                });
            }
        }
        events
    }

    /// Retransmit reliable packets that haven't been ACKed.
    pub async fn process_retransmits(&mut self) {
        let peer_ids: Vec<PeerId> = self.peers.keys().copied().collect();
        let mut dead_peers = Vec::new();

        for pid in peer_ids {
            if let Some(peer) = self.peers.get_mut(&pid) {
                let (retransmits, peer_dead) = peer.collect_retransmits();
                if peer_dead {
                    dead_peers.push(pid);
                    continue;
                }
                for data in retransmits {
                    let addr = peer.addr;
                    debug!("Retransmitting {} bytes to {}", data.len(), addr);
                    let _ = self.socket.send_to(&data, addr).await;
                }
            }
        }

        for pid in dead_peers {
            if let Some(peer) = self.peers.remove(&pid) {
                warn!(
                    "Peer {} ({}) dropped: too many retransmit failures",
                    peer.name, peer.addr
                );
            }
        }
    }

    // ─── Send methods ───

    /// Send an unreliable packet to a specific peer.
    pub async fn send_unreliable_to(&self, peer_id: PeerId, payload: &PacketPayload) {
        if let Some(peer) = self.peers.get(&peer_id) {
            let seq = 0; // unreliable packets don't need meaningful sequence
            let header = PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
            let data = encode_packet(&header, payload);
            let _ = self.socket.send_to(&data, peer.addr).await;
        }
    }

    /// Broadcast an unreliable packet to all connected peers.
    pub async fn broadcast_unreliable(&self, payload: &PacketPayload) {
        let header = PacketHeader::new(payload.type_code(), self.local_id, 0, self.timestamp());
        let data = encode_packet(&header, payload);
        for peer in self.peers.values() {
            let _ = self.socket.send_to(&data, peer.addr).await;
        }
    }

    /// Send a reliable packet to a specific peer (queued for ACK tracking).
    pub async fn send_reliable(&mut self, peer_id: PeerId, payload: &PacketPayload) {
        let seq = self.next_sequence();
        let header = PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
        let data = encode_packet(&header, payload);

        if let Some(peer) = self.peers.get_mut(&peer_id) {
            let _ = self.socket.send_to(&data, peer.addr).await;
            peer.queue_reliable(seq, data);
        }
    }

    /// Broadcast a reliable packet to all peers.
    pub async fn broadcast_reliable(&mut self, payload: &PacketPayload) {
        let peer_addrs: Vec<(PeerId, SocketAddr)> =
            self.peers.iter().map(|(id, p)| (*id, p.addr)).collect();

        for (pid, addr) in peer_addrs {
            let seq = self.next_sequence();
            let header =
                PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
            let data = encode_packet(&header, payload);
            let _ = self.socket.send_to(&data, addr).await;
            if let Some(peer) = self.peers.get_mut(&pid) {
                peer.queue_reliable(seq, data);
            }
        }
    }

    /// Send a raw encoded packet to an address (used for handshake responses
    /// before the peer is in the peers map).
    async fn send_raw_to(&self, addr: SocketAddr, payload: &PacketPayload) {
        let seq = 0;
        let header = PacketHeader::new(payload.type_code(), self.local_id, seq, self.timestamp());
        let data = encode_packet(&header, payload);
        let _ = self.socket.send_to(&data, addr).await;
    }

    // ─── Packet handling ───

    async fn handle_packet(&mut self, pkt: IncomingPacket) -> Vec<NetworkEvent> {
        let sender_id = pkt.header.sender_id;

        // Send ACK for reliable packets.
        if is_reliable(pkt.header.packet_type) && pkt.header.sequence > 0 {
            let ack = PacketPayload::Ack {
                acked_sequence: pkt.header.sequence,
            };
            self.send_raw_to(pkt.addr, &ack).await;
        }

        // Update heartbeat for known peers.
        if let Some(peer) = self.peer_by_addr_mut(pkt.addr) {
            peer.record_heartbeat();
        }

        match pkt.payload {
            PacketPayload::Handshake {
                player_name,
                version,
            } => self.handle_handshake(pkt.addr, player_name, version).await,

            PacketPayload::HandshakeAck {
                assigned_id,
                world_seed,
                config: _,
                peers,
                anchors: _,
                stabilizers: _,
            } => self.handle_handshake_ack(pkt.addr, sender_id, assigned_id, world_seed, peers),

            PacketPayload::Heartbeat => {
                // Already updated heartbeat above.
                vec![]
            }

            PacketPayload::Disconnect { reason } => {
                let mut events = Vec::new();
                if let Some(peer) = self.peers.remove(&sender_id) {
                    info!("Peer {} ({}) disconnected: {}", peer.name, peer.addr, reason);
                    events.push(NetworkEvent::PeerDisconnected {
                        id: sender_id,
                        reason,
                    });
                }
                events
            }

            PacketPayload::PeerList { .. } => {
                // Update peer knowledge (future: add new peers we don't know about).
                vec![]
            }

            PacketPayload::PlayerUpdate {
                position,
                rotation,
                animation,
            } => {
                if let Some(peer) = self.peers.get_mut(&sender_id) {
                    peer.update_player_state(position, rotation, animation.clone());
                }
                vec![NetworkEvent::RemotePlayerUpdate {
                    id: sender_id,
                    position,
                    rotation,
                    animation,
                }]
            }

            PacketPayload::WorldSync { chunks } => {
                vec![NetworkEvent::WorldSyncReceived { chunks }]
            }

            PacketPayload::ChunkState { data } => {
                // Treat as a chunk transfer for now.
                vec![NetworkEvent::ChunkTransferReceived {
                    from: sender_id,
                    data,
                }]
            }

            PacketPayload::ChunkTransfer { data } => {
                vec![NetworkEvent::ChunkTransferReceived {
                    from: sender_id,
                    data,
                }]
            }

            PacketPayload::ChunkTransferAck { pos } => {
                vec![NetworkEvent::ChunkTransferAckReceived {
                    from: sender_id,
                    pos,
                }]
            }

            PacketPayload::ChunkTeleport {
                old_pos,
                new_pos,
                new_seed,
            } => vec![NetworkEvent::ChunkTeleportReceived {
                old_pos,
                new_pos,
                new_seed,
            }],

            PacketPayload::AnchorBroadcast {
                chunk_pos,
                durability,
                installed_by,
            } => vec![NetworkEvent::AnchorBroadcastReceived {
                chunk_pos,
                durability,
                installed_by,
            }],

            PacketPayload::StabilizerBroadcast {
                chunk_pos,
                tier,
                remaining_hours,
            } => vec![NetworkEvent::StabilizerBroadcastReceived {
                chunk_pos,
                tier,
                remaining_hours,
            }],

            PacketPayload::Ack { acked_sequence } => {
                if let Some(peer) = self.peers.get_mut(&sender_id) {
                    peer.process_ack(acked_sequence);
                }
                vec![]
            }

            PacketPayload::Nack { requested_sequence: _ } => {
                // Future: retransmit the requested packet.
                vec![]
            }

            PacketPayload::Ping { send_time } => {
                // Respond with the same timestamp so the sender can measure RTT.
                let pong = PacketPayload::Ping { send_time };
                self.send_raw_to(pkt.addr, &pong).await;
                vec![]
            }

            // Action packets — forward to game loop as-is.
            PacketPayload::Interact { .. }
            | PacketPayload::Attack { .. }
            | PacketPayload::Pickup { .. }
            | PacketPayload::Drop { .. }
            | PacketPayload::Craft { .. }
            | PacketPayload::PlaceStabilizer { .. }
            | PacketPayload::PlaceAnchor
            | PacketPayload::ChunkDelta { .. }
            | PacketPayload::EntityUpdate { .. } => {
                // These will be processed when full action handling is wired up.
                vec![]
            }
        }
    }

    async fn handle_handshake(
        &mut self,
        from_addr: SocketAddr,
        player_name: String,
        _version: String,
    ) -> Vec<NetworkEvent> {
        if !self.is_host {
            // Only the host accepts handshakes.
            return vec![];
        }

        // Check if this peer is already connected.
        if self.peers.values().any(|p| p.addr == from_addr) {
            debug!("Duplicate handshake from {from_addr}, ignoring");
            return vec![];
        }

        let assigned_id = self.next_peer_id;
        self.next_peer_id += 1;

        info!(
            "New peer connecting: {} from {} → assigned id {}",
            player_name, from_addr, assigned_id
        );

        // Add the peer.
        let peer = PeerConnection::new(assigned_id, player_name.clone(), from_addr);
        self.peers.insert(assigned_id, peer);

        // Send HandshakeAck with world info.
        let ack_payload = PacketPayload::HandshakeAck {
            assigned_id,
            world_seed: self.world_seed,
            config: SessionConfig::default(),
            peers: self
                .peers
                .values()
                .map(|p| PeerInfo {
                    id: p.id,
                    name: p.name.clone(),
                    addr: p.addr.to_string(),
                    position: p.position,
                })
                .collect(),
            anchors: vec![],
            stabilizers: vec![],
        };
        self.send_raw_to(from_addr, &ack_payload).await;

        vec![NetworkEvent::PeerConnected {
            id: assigned_id,
            name: player_name,
        }]
    }

    fn handle_handshake_ack(
        &mut self,
        from_addr: SocketAddr,
        sender_id: PeerId,
        assigned_id: PeerId,
        world_seed: u64,
        peers: Vec<PeerInfo>,
    ) -> Vec<NetworkEvent> {
        if self.is_host {
            return vec![]; // Host doesn't receive handshake acks.
        }

        info!(
            "Handshake ACK received: assigned_id={}, world_seed={}, {} peers",
            assigned_id,
            world_seed,
            peers.len()
        );

        // Update our local ID to the one assigned by the host.
        self.local_id = assigned_id;
        self.world_seed = world_seed;

        // Add the host as a peer.
        let host_peer = PeerConnection::new(sender_id, format!("Host"), from_addr);
        self.peers.insert(sender_id, host_peer);

        vec![NetworkEvent::PeerConnected {
            id: sender_id,
            name: "Host".into(),
        }]
    }

    fn peer_by_addr_mut(&mut self, addr: SocketAddr) -> Option<&mut PeerConnection> {
        self.peers.values_mut().find(|p| p.addr == addr)
    }

    pub fn peer_count(&self) -> usize {
        self.peers.len()
    }
}

/// Background task: read UDP datagrams, parse, and forward to the NetworkManager.
async fn receive_loop(socket: Arc<UdpSocket>, tx: mpsc::Sender<IncomingPacket>) {
    let mut buf = vec![0u8; protocol::MAX_PACKET_SIZE];
    loop {
        match socket.recv_from(&mut buf).await {
            Ok((len, addr)) => {
                if len < HEADER_SIZE {
                    continue;
                }
                match decode_packet(&buf[..len]) {
                    Ok((header, payload)) => {
                        let pkt = IncomingPacket {
                            addr,
                            header,
                            payload,
                        };
                        if tx.send(pkt).await.is_err() {
                            break; // Channel closed, manager dropped.
                        }
                    }
                    Err(e) => {
                        debug!("Failed to decode packet from {addr}: {e}");
                    }
                }
            }
            Err(e) => {
                warn!("UDP recv error: {e}");
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    /// Get the loopback address for a NetworkManager (replaces 0.0.0.0 with 127.0.0.1).
    fn loopback_addr(net: &NetworkManager) -> SocketAddr {
        let mut addr = net.local_addr();
        addr.set_ip(std::net::Ipv4Addr::LOCALHOST.into());
        addr
    }

    #[tokio::test]
    async fn bind_and_local_addr() {
        let net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let addr = net.local_addr();
        assert_ne!(addr.port(), 0);
    }

    #[tokio::test]
    async fn two_peers_handshake_and_sync() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        host.local_name = "HostPlayer".into();
        let host_addr = loopback_addr(&host);

        let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
        joiner.local_name = "JoinerPlayer".into();

        joiner.initiate_connection(host_addr).await;
        tokio::time::sleep(Duration::from_millis(200)).await;

        let host_events = host.process_incoming().await;
        assert!(
            host_events
                .iter()
                .any(|e| matches!(e, NetworkEvent::PeerConnected { .. })),
            "host should see PeerConnected, got: {host_events:?}"
        );
        assert_eq!(host.peer_count(), 1);

        tokio::time::sleep(Duration::from_millis(100)).await;
        let joiner_events = joiner.process_incoming().await;
        assert!(
            joiner_events
                .iter()
                .any(|e| matches!(e, NetworkEvent::PeerConnected { .. })),
            "joiner should see PeerConnected, got: {joiner_events:?}"
        );
        assert_eq!(joiner.peer_count(), 1);
        assert_ne!(joiner.local_id, 0, "joiner should have an assigned ID");
    }

    #[tokio::test]
    async fn player_update_round_trip() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let host_addr = loopback_addr(&host);
        let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
        joiner.initiate_connection(host_addr).await;
        tokio::time::sleep(Duration::from_millis(200)).await;
        host.process_incoming().await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        joiner.process_incoming().await;

        let payload = PacketPayload::PlayerUpdate {
            position: [10.0, 1.8, 20.0],
            rotation: 45.0,
            animation: "walk".into(),
        };
        host.broadcast_unreliable(&payload).await;

        tokio::time::sleep(Duration::from_millis(100)).await;

        let events = joiner.process_incoming().await;
        let update = events
            .iter()
            .find(|e| matches!(e, NetworkEvent::RemotePlayerUpdate { .. }));
        assert!(update.is_some(), "joiner should see player update, got: {events:?}");
        if let Some(NetworkEvent::RemotePlayerUpdate {
            position,
            rotation,
            ..
        }) = update
        {
            assert_eq!(*position, [10.0, 1.8, 20.0]);
            assert_eq!(*rotation, 45.0);
        }
    }

    #[tokio::test]
    async fn reliable_packet_ack() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let host_addr = loopback_addr(&host);
        let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
        joiner.initiate_connection(host_addr).await;
        tokio::time::sleep(Duration::from_millis(200)).await;
        host.process_incoming().await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        joiner.process_incoming().await;

        let joiner_id = host
            .peers
            .keys()
            .next()
            .copied()
            .expect("host should have a peer");

        let payload = PacketPayload::Disconnect {
            reason: "test".into(),
        };
        host.send_reliable(joiner_id, &payload).await;
        assert_eq!(host.peers[&joiner_id].reliable_queue.len(), 1);

        tokio::time::sleep(Duration::from_millis(100)).await;
        joiner.process_incoming().await;

        tokio::time::sleep(Duration::from_millis(100)).await;
        host.process_incoming().await;
        assert_eq!(
            host.peers.get(&joiner_id).map(|p| p.reliable_queue.len()),
            Some(0),
            "reliable queue should be empty after ACK"
        );
    }

    #[tokio::test]
    async fn peer_timeout_detection() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        // Manually add a peer with an old heartbeat.
        let addr: SocketAddr = "127.0.0.1:9999".parse().unwrap();
        let mut peer = PeerConnection::new(2, "OldPeer".into(), addr);
        peer.last_heartbeat = Instant::now() - Duration::from_secs(10);
        net.peers.insert(2, peer);

        let events = net.check_timeouts();
        assert_eq!(events.len(), 1);
        assert!(matches!(
            &events[0],
            NetworkEvent::PeerDisconnected { id: 2, .. }
        ));
        assert_eq!(net.peer_count(), 0);
    }
}
