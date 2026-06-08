//! Selective reliability layer over UDP (ACK / retransmit). Phase 3 scaffolding.
//! See ARCHITECTURE_V1.md §5.3.

/// Retransmit backoff schedule in milliseconds (exponential).
pub const RETRANSMIT_BACKOFF_MS: [u64; 4] = [200, 400, 800, 1600];
/// Drop the peer after this many failed retransmits.
pub const MAX_RETRIES: u8 = 5;
/// Maximum reliable packets in flight per peer.
pub const WINDOW_SIZE: usize = 32;
/// Receiver must ACK a reliable packet within this many milliseconds.
pub const ACK_DEADLINE_MS: u64 = 100;

/// Whether a packet type must be delivered reliably (ARCHITECTURE_V1.md §5.3).
pub fn is_reliable(packet_type: u16) -> bool {
    // Actions (0x20-0x2F), chunk transfers / anchor & stabilizer broadcasts,
    // world sync, inventory sync, and graceful disconnect are reliable.
    matches!(packet_type,
        0x04            // WorldSync
        | 0x06          // Disconnect
        | 0x15          // InventorySync
        | 0x20..=0x2F   // Actions
        | 0x30..=0x31   // ChunkTransfer / Ack
        | 0x34..=0x35   // Anchor / Stabilizer broadcasts
    )
}
