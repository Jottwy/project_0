//! Peer connection management: state tracking, reliable queue, heartbeat, timeout.
//! See ARCHITECTURE_V1.md §11.1.

use std::collections::VecDeque;
use std::net::SocketAddr;
use std::time::{Duration, Instant};

use super::reliability::{MAX_RETRIES, RETRANSMIT_BACKOFF_MS, WINDOW_SIZE};
use super::PeerId;

const HEARTBEAT_TIMEOUT: Duration = Duration::from_secs(5);

/// A reliable packet awaiting acknowledgement.
///
/// Deliberately carries NO destination: `NetworkManager::process_retransmits` resolves it from
/// the live `PeerConnection::addr` at resend time, so a peer that re-binds behind NAT drags its
/// pending queue to the new address instead of retransmitting into the void.
#[derive(Debug, Clone)]
pub struct ReliablePacket {
    pub sequence: u32,
    pub data: Vec<u8>,
    pub sent_at: Instant,
    pub retries: u8,
    pub next_retry_at: Instant,
}

/// State for a single peer connected to this backend (host-as-server star, not a mesh).
#[derive(Debug)]
pub struct PeerConnection {
    pub id: PeerId,
    pub name: String,
    pub addr: SocketAddr,
    pub latency_ms: u16,
    pub last_heartbeat: Instant,
    pub reliable_queue: VecDeque<ReliablePacket>,
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
    /// ADR-023: cosmetic held item ID (0 = empty hands), set from PlayerUpdate; relayed, not
    /// authoritative. NOTE: set in handle_packet, NOT in update_player_state — so the phantom
    /// (ADR-016) keeps empty hands.
    pub held_item: i32,
    /// ADR-024: cosmetic hit-reaction counter (monotonic, wrapping; 0 = never hit), set from
    /// PlayerUpdate; relayed, not authoritative. NOTE: set in handle_packet, NOT in
    /// update_player_state — so the phantom (ADR-016) never flinches.
    pub hit_seq: u8,
    /// ADR-028 post-E3: cosmetic dead flag — SERVER-derived on the owning backend
    /// (`player.stats.is_dead()`, ADR-025), relayed so observers hide the standing proxy while
    /// its corpse lies there. NOT authoritative for any gameplay decision. NOTE: set in
    /// handle_packet, NOT in update_player_state — so the phantom (ADR-016) never hides.
    pub dead: bool,
    /// ADR-038: cosmetic "showing its real form" flag. UNLIKE every other pose field, this one is
    /// written for the PHANTOM on purpose: `PhantomDriver` seals it from its own state machine
    /// (`Sprint`/`Statue`), while `handle_packet` sets it from a relayed PlayerUpdate so joiners
    /// see the same thing. `update_player_state` stays untouched, like the fields above — the
    /// driver writes this one next to its `update_player_state` call, not inside it.
    pub revealed: bool,
    /// ADR-048: monotonic vocalisation counter, written by `PhantomDriver` (host) and by
    /// `handle_packet` from a relayed PlayerUpdate (joiner), exactly like `revealed`. A REAL peer
    /// never bumps it, so it stays 0 and its proxy never makes a sound.
    pub vocal_seq: u8,
    /// ADR-048: which voice the last bump was. Meaningless on its own — always read with `vocal_seq`.
    pub vocal_kind: u8,
    /// ADR-042: cosmetic "held wieldable is lit" flag, set from PlayerUpdate; relayed, not
    /// authoritative. NOTE: set in handle_packet, NOT in update_player_state — so the phantom
    /// (ADR-016) never carries a lit torch.
    pub light_on: bool,
    /// ADR-042: cosmetic shot counter (monotonic, wrapping; 0 = never fired), set from
    /// PlayerUpdate; relayed, not authoritative. NOTE: set in handle_packet, NOT in
    /// update_player_state — so the phantom (ADR-016) never fires a gun.
    pub fire_seq: u8,
    /// ADR-044: cosmetic sustained-state bits (aiming/reloading), set from PlayerUpdate; relayed,
    /// not authoritative. NOTE: set in handle_packet, NOT in update_player_state — so the phantom
    /// (ADR-016) never aims and never reloads.
    pub buttons: u16,
    /// ADR-044: cosmetic melee-swing counter, set from PlayerUpdate; relayed, not authoritative.
    /// NOTE: set in handle_packet, NOT in update_player_state — so the phantom never swings a
    /// weapon it does not carry.
    pub melee_seq: u8,
    /// ADR-049: cosmetic carry state, set from PlayerUpdate; relayed, not authoritative.
    /// NOTE: set in handle_packet, NOT in update_player_state — so the phantom never appears to
    /// haul building material it does not have.
    pub carry_def: i32,
    pub carry_count: u8,
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
            position: [0.0, 1.8, 0.0],
            rotation: 0.0,
            animation: "idle".into(),
            crouch: false,
            pitch: 0,
            equipment: [0; 4],
            held_item: 0,
            hit_seq: 0,
            dead: false,
            revealed: false,
            vocal_seq: 0,
            vocal_kind: 0,
            light_on: false,
            fire_seq: 0,
            buttons: 0,
            melee_seq: 0,
            carry_def: 0,
            carry_count: 0,
        }
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
