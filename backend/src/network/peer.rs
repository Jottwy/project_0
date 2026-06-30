//! Peer connection management: state tracking, reliable queue, heartbeat, timeout.
//! See ARCHITECTURE_V1.md §11.1.

use std::collections::VecDeque;
use std::net::SocketAddr;
use std::time::{Duration, Instant};

use super::reliability::{MAX_RETRIES, RETRANSMIT_BACKOFF_MS, WINDOW_SIZE};
use super::PeerId;

const HEARTBEAT_TIMEOUT: Duration = Duration::from_secs(5);

/// A reliable packet awaiting acknowledgement.
#[derive(Debug, Clone)]
pub struct ReliablePacket {
    pub sequence: u32,
    pub data: Vec<u8>,
    pub dest: SocketAddr,
    pub sent_at: Instant,
    pub retries: u8,
    pub next_retry_at: Instant,
}

/// State for a single connected peer in the mesh.
#[derive(Debug)]
pub struct PeerConnection {
    pub id: PeerId,
    pub name: String,
    pub addr: SocketAddr,
    pub latency_ms: u16,
    pub last_heartbeat: Instant,
    pub reliable_queue: VecDeque<ReliablePacket>,
    pub sequence_counter: u32,
    // Remote player state (updated by PlayerUpdate packets)
    pub position: [f32; 3],
    pub rotation: f32,
    pub animation: String,
    /// ADR-020: cosmetic crouch state, set from PlayerUpdate; relayed, not authoritative.
    pub crouch: bool,
    /// ADR-021: cosmetic camera pitch (degrees, −90..90, quantized to 1°), set from
    /// PlayerUpdate; relayed, not authoritative.
    pub pitch: i8,
    /// ADR-022: cosmetic worn clothing item IDs [Head, Torso, Legs, Feet] (0 = empty), set
    /// from PlayerUpdate; relayed, not authoritative. NOTE: set in handle_packet, NOT in
    /// update_player_state — so the phantom (ADR-016) keeps default clothing.
    pub equipment: [i32; 4],
    pub connected_at: Instant,
}

impl PeerConnection {
    pub fn new(id: PeerId, name: String, addr: SocketAddr) -> Self {
        let now = Instant::now();
        Self {
            id,
            name,
            addr,
            latency_ms: 0,
            last_heartbeat: now,
            reliable_queue: VecDeque::new(),
            sequence_counter: 0,
            position: [0.0, 1.8, 0.0],
            rotation: 0.0,
            animation: "idle".into(),
            crouch: false,
            pitch: 0,
            equipment: [0; 4],
            connected_at: now,
        }
    }

    pub fn next_sequence(&mut self) -> u32 {
        self.sequence_counter = self.sequence_counter.wrapping_add(1);
        self.sequence_counter
    }

    pub fn record_heartbeat(&mut self) {
        self.last_heartbeat = Instant::now();
    }

    pub fn is_timed_out(&self) -> bool {
        self.last_heartbeat.elapsed() > HEARTBEAT_TIMEOUT
    }

    pub fn can_queue_reliable(&self) -> bool {
        self.reliable_queue.len() < WINDOW_SIZE
    }

    /// Queue a reliable packet for ACK tracking + retransmit.
    pub fn queue_reliable(&mut self, sequence: u32, data: Vec<u8>) {
        let now = Instant::now();
        let backoff_ms = RETRANSMIT_BACKOFF_MS.first().copied().unwrap_or(200);
        self.reliable_queue.push_back(ReliablePacket {
            sequence,
            data,
            dest: self.addr,
            sent_at: now,
            retries: 0,
            next_retry_at: now + Duration::from_millis(backoff_ms),
        });
    }

    /// Process an ACK: remove the acknowledged packet from the queue.
    /// Returns true if the sequence was found.
    pub fn process_ack(&mut self, acked_sequence: u32) -> bool {
        if let Some(idx) = self
            .reliable_queue
            .iter()
            .position(|p| p.sequence == acked_sequence)
        {
            let pkt = self.reliable_queue.remove(idx).unwrap();
            let rtt = pkt.sent_at.elapsed();
            self.latency_ms = rtt.as_millis().min(u16::MAX as u128) as u16;
            true
        } else {
            false
        }
    }

    /// Collect packets that need retransmission. Returns (data, timed_out_peer).
    /// If a packet exceeds MAX_RETRIES, returns the peer as timed out.
    pub fn collect_retransmits(&mut self) -> (Vec<Vec<u8>>, bool) {
        let now = Instant::now();
        let mut to_send = Vec::new();
        let mut peer_dead = false;

        for pkt in self.reliable_queue.iter_mut() {
            if now >= pkt.next_retry_at {
                pkt.retries += 1;
                if pkt.retries > MAX_RETRIES {
                    peer_dead = true;
                    break;
                }
                let backoff_idx = (pkt.retries as usize - 1).min(RETRANSMIT_BACKOFF_MS.len() - 1);
                pkt.next_retry_at = now + Duration::from_millis(RETRANSMIT_BACKOFF_MS[backoff_idx]);
                to_send.push(pkt.data.clone());
            }
        }

        (to_send, peer_dead)
    }

    pub fn update_player_state(&mut self, position: [f32; 3], rotation: f32, animation: String) {
        self.position = position;
        self.rotation = rotation;
        self.animation = animation;
        self.record_heartbeat();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn test_addr() -> SocketAddr {
        "127.0.0.1:9999".parse().unwrap()
    }

    #[test]
    fn new_peer_not_timed_out() {
        let peer = PeerConnection::new(1, "Test".into(), test_addr());
        assert!(!peer.is_timed_out());
    }

    #[test]
    fn sequence_increments() {
        let mut peer = PeerConnection::new(1, "Test".into(), test_addr());
        assert_eq!(peer.next_sequence(), 1);
        assert_eq!(peer.next_sequence(), 2);
        assert_eq!(peer.next_sequence(), 3);
    }

    #[test]
    fn reliable_queue_and_ack() {
        let mut peer = PeerConnection::new(1, "Test".into(), test_addr());
        peer.queue_reliable(1, vec![1, 2, 3]);
        peer.queue_reliable(2, vec![4, 5, 6]);
        assert_eq!(peer.reliable_queue.len(), 2);

        assert!(peer.process_ack(1));
        assert_eq!(peer.reliable_queue.len(), 1);

        assert!(!peer.process_ack(99)); // unknown sequence
        assert_eq!(peer.reliable_queue.len(), 1);

        assert!(peer.process_ack(2));
        assert_eq!(peer.reliable_queue.len(), 0);
    }

    #[test]
    fn window_size_limit() {
        let mut peer = PeerConnection::new(1, "Test".into(), test_addr());
        for i in 0..WINDOW_SIZE {
            assert!(peer.can_queue_reliable());
            peer.queue_reliable(i as u32, vec![0]);
        }
        assert!(!peer.can_queue_reliable());
    }

    #[test]
    fn update_player_state_refreshes_heartbeat() {
        let mut peer = PeerConnection::new(1, "Test".into(), test_addr());
        let old_hb = peer.last_heartbeat;
        std::thread::sleep(Duration::from_millis(10));
        peer.update_player_state([1.0, 2.0, 3.0], 90.0, "walk".into());
        assert!(peer.last_heartbeat > old_hb);
        assert_eq!(peer.position, [1.0, 2.0, 3.0]);
    }
}
