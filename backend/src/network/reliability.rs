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
///
/// ADR-039 — INVARIANTE, en los dos sentidos: un tipo está aquí **si y solo si** existe al menos
/// un sitio que lo envía con `send_reliable` / `broadcast_reliable`. Las dos mitades de esa
/// decisión viven en archivos distintos y divergir es exactamente el bug que ADR-039 cierra: los
/// 16 códigos de petición/veredicto de gameplay ya se enviaban como fiables, pero esta función
/// —cuyo ÚNICO uso es decidir si el receptor emite el ACK— no los reconocía. El emisor reenviaba
/// 6 veces y al agotar `MAX_RETRIES` la cola entera del peer se purgaba, llevándose por delante
/// los paquetes que sí estaban bien clasificados. `reliability_covers_every_reliably_sent_type`
/// fija la lista.
///
/// NO entran los cinco rosters completos (`StpItemList` 0x16, `StpBuildingList` 0x1A,
/// `StpCarryableList` 0x40, `StpHarvestableList` 0x44, `CorpseList` 0x46): son broadcasts
/// idempotentes y auto-curados a 10 Hz, y su pérdida se corrige en el siguiente envío.
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
        | 0x36..=0x37   // WorldSyncChunk / WorldSyncEnd — el goteo de ADR-060 viaja fiable
        // ─── ADR-039: familia petición/veredicto de gameplay ───
        // Enumerados uno a uno y no por rangos: cada código lindante es un roster que debe
        // quedar FUERA (0x1A, 0x40, 0x44, 0x46), así que la inclusión tiene que ser un acto
        // deliberado y legible, no el efecto lateral de un `..=`.
        | 0x17          // StpPickupRequest
        | 0x18          // StpPickupGranted
        | 0x19          // StpDropRequest
        | 0x1B          // StpPlaceRequest
        | 0x1C          // StpBuildAddRequest
        | 0x1D          // StpDemolishRequest        (ADR-037)
        | 0x41          // StpCarryablePickupRequest
        | 0x42          // StpCarryablePickupGranted
        | 0x43          // StpCarryableDropRequest
        | 0x45          // StpHarvestHitRequest
        | 0x47          // CorpseSpawnRequest        (ADR-028 Fase E)
        | 0x48          // CorpseTakeRequest         (ADR-028 Fase E)
        | 0x49          // CorpseTakeResult          (ADR-028 Fase E)
        | 0x4A          // PvpHitCandidate           (ADR-029)
        | 0x4B          // PvpDamageGrant            (ADR-029)
        | 0x4C          // PvpHitRejected            (ADR-029)
        | 0x4D          // PhantomAttackGrant        (ADR-047)
        // 0x4E NoiseReport is DELIBERATELY absent: it is sent with `send_unreliable_to`, so
        // listing it would break this function's "si y solo si" invariant in the other
        // direction. A transient stimulus must not occupy the 32-slot reliable window; a lost
        // noise self-heals on the next shot.
    )
}

#[cfg(test)]
mod tests {
    use super::is_reliable;

    /// Los 16 tipos que ADR-039 incorporó. La lista se derivó cruzando los sitios de
    /// `send_reliable`/`broadcast_reliable` (18 en `game_loop.rs`/`sync.rs`) contra lo que esta
    /// función reconocía. Al añadir un envío fiable de un tipo nuevo, va aquí Y en `is_reliable`.
    const GAMEPLAY_REQUEST_FAMILY: [u16; 17] = [
        0x17, 0x18, 0x19, 0x1B, 0x1C, 0x1D, 0x41, 0x42, 0x43, 0x45, 0x47, 0x48, 0x49, 0x4A, 0x4B,
        0x4C, 0x4D,
    ];

    /// Los cinco rosters completos. Van a 10 Hz, son idempotentes y se auto-curan: hacerlos
    /// fiables llenaría la ventana de 32 sin ganar nada. Su exclusión es tan deliberada como la
    /// inclusión de los de arriba, y por eso se asserta.
    const FULL_ROSTERS: [u16; 5] = [0x16, 0x1A, 0x40, 0x44, 0x46];

    #[test]
    fn reliability_covers_every_reliably_sent_type() {
        for code in GAMEPLAY_REQUEST_FAMILY {
            assert!(
                is_reliable(code),
                "0x{code:02x} se envia con send_reliable pero el receptor no lo ACKearia: \
                 reenvio x6 y purga de la cola entera al agotar MAX_RETRIES (ADR-039)"
            );
        }
    }

    #[test]
    fn full_rosters_stay_unreliable() {
        for code in FULL_ROSTERS {
            assert!(
                !is_reliable(code),
                "0x{code:02x} es un roster completo a 10 Hz, idempotente y auto-curado: \
                 hacerlo fiable llena la ventana sin ganar nada (ADR-039)"
            );
        }
    }

    /// ADR-060: el goteo de snapshot viaja fiable — 0x36/0x37 se emiten con
    /// `send_reliable_queued`, que encola en la MISMA ventana que `send_reliable`, asi que el
    /// receptor tiene que ACKearlos o el emisor los reenvia x6 y purga la cola (ADR-039).
    #[test]
    fn the_world_drip_is_reliable() {
        for code in [0x36u16, 0x37] {
            assert!(
                is_reliable(code),
                "0x{code:02x} se emite por la cola fiable (ADR-060) y el receptor debe ACKearlo"
            );
        }
    }

    /// Lo que ya era fiable antes de ADR-039 debe seguir siendolo: el cambio es aditivo.
    #[test]
    fn pre_existing_reliable_types_are_untouched() {
        for code in [
            0x04u16, 0x06, 0x15, 0x20, 0x28, 0x2F, 0x30, 0x31, 0x34, 0x35,
        ] {
            assert!(
                is_reliable(code),
                "0x{code:02x} era fiable antes de ADR-039"
            );
        }
    }

    /// Y lo que nunca fue fiable sigue sin serlo: la pose a 10 Hz y los heartbeats no se
    /// encolan jamas — un ACK por cada uno seria el doble de datagramas por nada.
    #[test]
    fn high_frequency_traffic_stays_unreliable() {
        for code in [0x05u16, 0x10, 0x11, 0x07, 0xF0] {
            assert!(!is_reliable(code), "0x{code:02x} no debe ser fiable");
        }
    }

    /// ADR-047: `NoiseReport` (0x4E) se envia con `send_unreliable_to`, asi que meterlo aqui
    /// romperia el "si y solo si" de ADR-039 por el otro lado — el receptor ACKearia algo que el
    /// emisor no encola, y nadie retransmitiria nunca. La mitad negativa del invariante importa
    /// tanto como la positiva, y sin este test la clasificacion se puede "arreglar" al reves.
    #[test]
    fn the_noise_stimulus_stays_unreliable() {
        assert!(
            !is_reliable(0x4E),
            "0x4E NoiseReport se envia sin fiabilidad a proposito (ADR-047 D3): un estimulo \
             transitorio no debe ocupar la ventana de 32, y un ruido perdido se auto-cura con \
             el siguiente disparo"
        );
    }
}
