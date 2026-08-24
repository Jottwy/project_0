//! `NetworkEvent`: the game-level events the network layer hands to the game loop.
//!
//! Split out of `mod.rs` verbatim; `network` re-exports it, so `network::NetworkEvent`
//! is still the only path anyone outside this module uses.

use super::protocol::ChunkSyncData;
use super::PeerId;

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
        crouch: bool,
        pitch: i8,
        equipment: [i32; 4],
        held_item: i32,
        hit_seq: u8,
        dead: bool,
        revealed: bool,
        light_on: bool,
        fire_seq: u8,
        buttons: u16,
        melee_seq: u8,
        /// ADR-048: cosmetic vocalisation counter + which voice it was.
        vocal_seq: u8,
        vocal_kind: u8,
        /// ADR-049: cosmetic carry state — definition id being hauled and how many units.
        carry_def: i32,
        carry_count: u8,
        /// ADR-094: cosmetic species tag (0 human, 1 faceling adulto, 2 faceling niño).
        species: u8,
    },
    WorldInteractRequest {
        requester_id: PeerId,
        request_id: u64,
        target_id: u32,
        target_kind: String,
        interaction_type: String,
        player_position: [f32; 3],
    },
    /// Phase 2: a joiner asks the host to pick up an STP item (host-authoritative).
    StpPickupRequest {
        item_id: u32,
        requester_id: PeerId,
    },
    /// Phase 2: the host grants an STP pickup to this (recoger) peer.
    StpPickupGranted {
        item_id: u32,
        def_id: i32,
        count: u16,
    },
    /// Phase 3: a joiner asks the host to spawn a dropped STP item in the world.
    /// ADR-070: `position` is the HAND, not a resting place, and `velocity` is the throw impulse —
    /// where the object ends up is the host's call, not the dropper's.
    StpDropRequest {
        drop_id: u64,
        def_id: i32,
        count: u16,
        position: [f32; 3],
        rotation: f32,
        velocity: [f32; 3],
    },
    /// Phase B1: a joiner asks the host to place an STP building piece in the world.
    StpPlaceRequest {
        place_id: u64,
        def_id: i32,
        position: [f32; 3],
        rotation: f32,
        group_id: u32,
        is_group: bool,
        /// ADR-081: quién pide colocar, tomado de la CABECERA del paquete y nunca del payload
        /// (mismo motivo que `SprayPlaceRequest.requester_id`, ADR-068). Es la identidad contra
        /// la que se comprueba la propiedad del claim, así que dejar que el payload la declarase
        /// permitiría construir dentro del territorio de otro sin más que mentir en un campo.
        requester_id: PeerId,
    },
    /// Phase B2: a joiner asks the host to add one unit of a build material to a piece.
    StpBuildAddRequest {
        add_id: u64,
        building_id: u32,
        material_id: i32,
    },
    /// ADR-037: a joiner asks the host to retire a placed-but-unbuilt piece it just cancelled.
    StpDemolishRequest {
        demolish_id: u64,
        building_id: u32,
    },
    /// ADR-068: a joiner asks the host to paint a spray. `requester_id` comes from the packet
    /// HEADER, not the payload — the host validates the reach against THAT peer's known position,
    /// so a client cannot claim to be painting from someone else's spot.
    SprayPlaceRequest {
        place_id: u64,
        layer: u8,
        world_pos: [f32; 3],
        yaw: f32,
        size: [f32; 2],
        strokes: Vec<crate::world::spray::SprayStroke>,
        requester_id: u16,
    },
    /// ADR-068: the host accepted a spray (anybody's) and this peer must show it.
    SprayPlacedReceived {
        spray: crate::world::spray::Spray,
    },
    /// ADR-078: a chunk of a stroke somebody is painting RIGHT NOW. `painter_id` comes from the
    /// packet HEADER, like every other spray event — on the host it decides who gets a relayed
    /// copy (by distance to THAT peer), and on a joiner it says whose stroke this is.
    SprayDraftReceived {
        place_id: u64,
        layer: u8,
        anchor: [f32; 3],
        yaw: f32,
        color: u8,
        width: f32,
        first_index: u16,
        points_mm: Vec<u8>,
        painter_id: u16,
    },
    /// ADR-068: a joiner loaded a chunk and asks what is painted on it. `requester_id` comes from
    /// the packet HEADER — the reply goes back to that peer alone, not to everyone.
    SprayChunkRequest {
        cx: i32,
        cz: i32,
        layer: u8,
        requester_id: u16,
    },
    /// Phase B2.5: a joiner asks the host to pick up a world carryable (host-authoritative).
    StpCarryablePickupRequest {
        carryable_id: u32,
        requester_id: PeerId,
    },
    /// Phase B2.5: the host grants a carryable pickup to this peer (it carries it in hand).
    StpCarryablePickupGranted {
        carryable_id: u32,
        def_id: i32,
    },
    /// Phase B2.5: a joiner asks the host to spawn a dropped carryable in the world.
    StpCarryableDropRequest {
        drop_id: u64,
        def_id: i32,
        position: [f32; 3],
        rotation: f32,
    },
    /// Phase B2.6: a joiner reports a harvest hit on a scene harvestable (host-authoritative).
    StpHarvestHitRequest {
        hit_id: u64,
        harvestable_id: u32,
        amount: f32,
    },
    /// ADR-028 Fase E: a joiner's player died — it asks the host to spawn the corpse.
    CorpseSpawnRequest {
        request_id: u64,
        requester_id: PeerId,
        owner_name: String,
        position: [f32; 3],
        equipment: [i32; 4],
        held_item: i32,
        items: Vec<crate::world::corpse::CorpseStack>,
    },
    /// ADR-028 Fase E: a joiner asks the host to take a stack from a corpse.
    CorpseTakeRequest {
        request_id: u64,
        requester_id: PeerId,
        corpse_id: u32,
        item_index: u32,
        quantity: u16,
        requester_pos: [f32; 3],
    },
    /// ADR-028 Fase E: the host's verdict for OUR CorpseTakeRequest (we are the requester).
    CorpseTakeResult {
        request_id: u64,
        accepted: bool,
        corpse_id: u32,
        item_index: u32,
        item_id: i32,
        quantity: u16,
        corpse_empty: bool,
        reason: String,
    },
    /// ADR-094 punto 4: the host tells us a faceling child robbed us. WE pick what is lost — the
    /// host cannot know, it only mirrors a mirror of our inventory (ADR-045).
    StealCommand {
        request_id: u64,
        victim_id: u32,
        thief_id: u32,
    },
    /// ADR-094 punto 4: our victim answered a StealCommand with what it actually lost. Only the
    /// host ever sees this — it is the half that lets the thief carry the loot and, later, drop it.
    StealReport {
        request_id: u64,
        victim_id: u32,
        thief_id: u32,
        def_id: i32,
        count: u16,
    },
    /// ADR-028 Fase E: the host's full corpse roster (10 Hz) — mirror it into world.corpses.
    CorpseListReceived {
        corpses: Vec<crate::world::corpse::CorpseData>,
    },
    /// ADR-093 (E2): the host's periodic Level 4 region broadcast — a joiner mirrors it
    /// verbatim into `net.level4`, same trust as the other rosters above.
    Level4StateReceived {
        epoch: u32,
        window_open: bool,
        return_dest: [f32; 3],
    },
    /// ADR-093 (E2): a peer asks to cross a Level 4 door. `requester_id` comes from the packet
    /// HEADER, not the payload — same reason as `StpPlaceRequest.requester_id` (ADR-081): the
    /// host resolves the destination against THAT peer's own known position, and the payload
    /// must not get a vote in who is crossing.
    Level4DoorRequest {
        requester_id: PeerId,
        request_id: u64,
        door: u8,
    },
    /// ADR-093 (E2): the host's verdict for OUR `Level4DoorRequest` — where we land.
    Level4DoorVerdict {
        request_id: u64,
        dest: [f32; 3],
    },
    /// ADR-029 V0: a remote peer's backend forwarded a PvP hit candidate to us (the host) for
    /// validation. All authority logic (dedupe, the 11-step validation order, grant/reject
    /// dispatch) lives in game_loop.rs, same split as the corpse relay above.
    PvpHitCandidate {
        request_id: u64,
        attacker_id: u32,
        victim_id: u32,
        weapon_id: i32,
        damage: f32,
        origin: [f32; 3],
        direction: [f32; 3],
        client_tick: Option<u32>,
        hit_position: Option<[f32; 3]>,
    },
    /// ADR-029 V0: the host validated a PvP hit against OUR local player and granted the
    /// damage. We are the victim's backend — apply it via `PlayerStats::take_damage`.
    PvpDamageGrant {
        request_id: u64,
        attacker_id: u32,
        victim_id: u32,
        weapon_id: i32,
        damage: f32,
        reason: String,
    },
    /// ADR-029 V0: the host rejected OUR PvP hit candidate. We are the shooter's backend —
    /// surface the reason to our own Unity, never apply damage.
    PvpHitRejected {
        request_id: u64,
        attacker_id: u32,
        victim_id: u32,
        reason: String,
    },
    /// ADR-047: a robapieles simulated by the host struck OUR local player. We are the victim's
    /// own backend — apply it here via `PlayerStats::take_damage`, never anywhere else.
    PhantomAttackGrant {
        request_id: u64,
        victim_id: u32,
        kind: u8,
        damage: f32,
        impulse: [f32; 2],
    },
    /// ADR-047: a joiner reported a noise to us (the host). Only the host simulates phantoms, so
    /// this is the sole way a joiner's gunshot can ever reach one.
    NoiseReported {
        position: [f32; 3],
        loudness: f32,
    },
    /// ADR-050 point 9: a joiner broke out of a grab. Only the host simulates phantoms, so this is
    /// the sole way a joiner's struggle can reach the creature holding it.
    StruggleReported {
        victim: PeerId,
    },
    /// ADR-046: a voice frame arrived from `speaker`. On a joiner the host has already decided
    /// we are close enough to hear it; on the host this is a peer talking, and the host is the
    /// one that decides who else gets a copy.
    ///
    /// `speaker` comes from the packet HEADER, never from the payload — on a relayed frame it is
    /// the id the host stamped via `send_unreliable_as`, which is exactly the peer whose proxy
    /// the audio belongs to.
    VoiceReceived {
        speaker: PeerId,
        seq: u16,
        data: Vec<u8>,
    },
    /// DEPRECADO por ADR-060 (ver `PacketPayload::WorldSync`): solo lo produce el decode del
    /// monolito 0x04, conservado una versión. Su handler marca la completitud del drip
    /// (`note_monolith`) para que un mundo llegado entero también abra el gate de spawn.
    WorldSyncReceived {
        world_seed: u64,
        world_revision: u64,
        chunks: Vec<ChunkSyncData>,
    },
    /// ADR-060: un chunk del goteo de snapshot. Se aplica por upsert al llegar; la completitud
    /// se cuenta en `WorldSyncProgress` por (pos, layer) — nunca por conteo de paquetes, porque
    /// la capa reliable es at-least-once y los duplicados por retransmisión son legales.
    WorldSyncChunkReceived {
        world_revision: u64,
        data: ChunkSyncData,
    },
    /// ADR-060: cierre del goteo. El snapshot está completo cuando este evento llegó Y los
    /// chunks distintos aplicados de `world_revision` alcanzan `chunk_count`.
    WorldSyncEndReceived {
        world_revision: u64,
        chunk_count: u32,
    },
    /// HANDOFF de propiedad de un chunk (`ChunkTransfer`, 0x30): se aplica Y se confirma con un
    /// `ChunkTransferAck` fiable, porque el emisor cede la autoridad y quiere saber que llegó.
    ///
    /// DISTINTO de `ChunkStateReceived` a propósito. Los dos payloads se fundían aquí ("Treat as a
    /// chunk transfer for now") y el ack salía también para el broadcast periódico: medido en
    /// sesión de 2 backends, 8 267 descartes por ventana llena en 40 s (~820 acks/s). Ver el
    /// comentario de `ChunkStateReceived`.
    ChunkTransferReceived {
        from: PeerId,
        data: ChunkSyncData,
    },
    /// BROADCAST periódico del estado de un chunk (`ChunkState`, 0x11, unreliable, cada tick del
    /// dueño). Se aplica igual que un handoff — mismo `apply_chunk_sync` — pero NO se confirma:
    /// el emisor no espera respuesta, nadie lee el ack (`ChunkTransferAckReceived` solo hace
    /// `debug!`), y a ~820 chunks/s los acks llenaban permanentemente la ventana reliable de 32
    /// del joiner, de modo que sus envíos fiables de gameplay (pickup, place, corpse, PvP) se
    /// descartaban en silencio contra el mismo `send_reliable`.
    ChunkStateReceived {
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
    /// Corrección adosada a ADR-060 (docs/DECISIONS.md, 2026-08-10): a joiner's `Handshake` was
    /// rejected BEFORE it got registered (session full, or now also a `WIRE_SCHEMA_VERSION`
    /// mismatch). Before this event existed the joiner never learned why — the raw `Disconnect`
    /// packet had nowhere to land (it isn't from a registered peer, so the normal
    /// `PeerDisconnected` path finds nothing to remove) and `retry_pending_connection` kept
    /// re-sending the same doomed handshake every second forever. This event is purely internal
    /// bookkeeping — it never crosses the wire itself, unlike everything else in this enum that
    /// mirrors a `PacketPayload` variant.
    ConnectRejected {
        reason: String,
    },
}
