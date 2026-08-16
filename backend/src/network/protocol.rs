//! P2P packet definitions (UDP, MessagePack encoded).
//! 12-byte header + MessagePack payload. See ARCHITECTURE_V1.md §5.

use serde::{Deserialize, Serialize};

use crate::world::chunk::ChunkLayoutV1;

pub const HEADER_SIZE: usize = 12;
pub const MAX_PACKET_SIZE: usize = 65535;

/// 12-byte packet header (ARCHITECTURE_V1.md §5.1).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct PacketHeader {
    pub packet_type: u16,
    pub sender_id: u16,
    pub sequence: u32,
    pub timestamp: u32, // ms since session start
}

impl PacketHeader {
    pub fn new(packet_type: u16, sender_id: u16, sequence: u32, timestamp: u32) -> Self {
        Self {
            packet_type,
            sender_id,
            sequence,
            timestamp,
        }
    }

    pub fn to_bytes(self) -> [u8; HEADER_SIZE] {
        let mut buf = [0u8; HEADER_SIZE];
        buf[0..2].copy_from_slice(&self.packet_type.to_be_bytes());
        buf[2..4].copy_from_slice(&self.sender_id.to_be_bytes());
        buf[4..8].copy_from_slice(&self.sequence.to_be_bytes());
        buf[8..12].copy_from_slice(&self.timestamp.to_be_bytes());
        buf
    }

    pub fn from_bytes(buf: &[u8]) -> Option<Self> {
        if buf.len() < HEADER_SIZE {
            return None;
        }
        Some(Self {
            packet_type: u16::from_be_bytes([buf[0], buf[1]]),
            sender_id: u16::from_be_bytes([buf[2], buf[3]]),
            sequence: u32::from_be_bytes([buf[4], buf[5], buf[6], buf[7]]),
            timestamp: u32::from_be_bytes([buf[8], buf[9], buf[10], buf[11]]),
        })
    }
}

/// Packet type codes (ARCHITECTURE_V1.md §5.2).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u16)]
pub enum PacketType {
    // Connection (0x00-0x0F)
    Discover = 0x00,
    PeerIntro = 0x01,
    Handshake = 0x02,
    HandshakeAck = 0x03,
    WorldSync = 0x04,
    Heartbeat = 0x05,
    Disconnect = 0x06,
    PeerList = 0x07,
    // State (0x10-0x1F)
    PlayerUpdate = 0x10,
    ChunkState = 0x11,
    ChunkDelta = 0x12,
    EntityUpdate = 0x13,
    StatUpdate = 0x14,
    InventorySync = 0x15,
    StpItemList = 0x16,
    StpPickupRequest = 0x17,
    StpPickupGranted = 0x18,
    StpDropRequest = 0x19,
    StpBuildingList = 0x1A,
    StpPlaceRequest = 0x1B,
    StpBuildAddRequest = 0x1C,
    /// ADR-037: retire a placed-but-unbuilt piece from the authoritative roster.
    StpDemolishRequest = 0x1D,
    // Actions (0x20-0x2F)
    Interact = 0x20,
    Attack = 0x21,
    Pickup = 0x22,
    Drop = 0x23,
    Craft = 0x24,
    PlaceStabilizer = 0x25,
    PlaceAnchor = 0x26,
    RepairAnchor = 0x27,
    UseConsumable = 0x28,
    // World (0x30-0x3F)
    ChunkTransfer = 0x30,
    ChunkTransferAck = 0x31,
    ChunkTeleport = 0x32,
    ChunkGenerate = 0x33,
    AnchorBroadcast = 0x34,
    StabilizerBroadcast = 0x35,
    // ADR-060: goteo del snapshot de mundo. 0x36 lleva UN chunk estampado con la revision;
    // 0x37 cierra el goteo con el contador total. Sustituyen al envío monolítico de 0x04
    // (WorldSync), cuyo decode se conserva una versión.
    WorldSyncChunk = 0x36,
    WorldSyncEnd = 0x37,
    // STP Carryables (0x40-0x4F)
    StpCarryableList = 0x40,
    StpCarryablePickupRequest = 0x41,
    StpCarryablePickupGranted = 0x42,
    StpCarryableDropRequest = 0x43,
    StpHarvestableList = 0x44,
    StpHarvestHitRequest = 0x45,
    // ADR-028 Fase E: host-authoritative corpse relay (same block — loot/world-object family)
    CorpseList = 0x46,
    CorpseSpawnRequest = 0x47,
    CorpseTakeRequest = 0x48,
    CorpseTakeResult = 0x49,
    // ADR-029 V0: PvP hit candidate -> host validation -> victim-applied damage
    PvpHitCandidate = 0x4A,
    PvpDamageGrant = 0x4B,
    PvpHitRejected = 0x4C,
    // ADR-047: the robapieles reaches across backends. 0x4D carries a phantom's blow to the
    // backend that owns the victim's health; 0x4E carries a joiner's gunshot back to the host,
    // the only backend that simulates phantoms. 0x50 is reserved (ADR-046).
    //
    // ADR-050 claims 0x4F, the slot ADR-047 deliberately stopped short of: a joiner's struggle out
    // of a grab, travelling the same joiner→host direction and for the same reason as 0x4E.
    PhantomAttackGrant = 0x4D,
    NoiseReport = 0x4E,
    StruggleReport = 0x4F,
    // ADR-046: proximity voice. Claims the slot ADR-047 reserved above.
    VoiceFrame = 0x50,
    // ADR-068: pintadas de spray. 0x51 lleva la peticion de un joiner al host (unico que
    // valida y acuna); 0x52 lleva la pintada YA aceptada del host a todos los peers.
    SprayPlaceRequest = 0x51,
    SprayPlaced = 0x52,
    /// ADR-068: 0x53 lo manda un joiner al cargar un chunk — "que hay pintado aqui". La
    /// geometria la deriva cada peer del seed; una pintada NO, asi que hay que preguntar.
    SprayChunkRequest = 0x53,
    /// ADR-078: 0x54 es el trazo EN VIVO, no fiable. Deliberadamente fuera de `is_reliable`:
    /// son ~10 paquetes por segundo mientras dura el trazo y no pueden ocupar la ventana de 32
    /// huecos, igual que el `NoiseReport` de 0x4E.
    SprayDraft = 0x54,
    // Reliability (0xF0-0xFF)
    Ack = 0xF0,
    Nack = 0xF1,
    Ping = 0xF2,
}

impl PacketType {
    /// Mitad INVERSA del contrato de wire: `PacketPayload::type_code()` escribe el opcode,
    /// esto lo lee de vuelta.
    ///
    /// NO es codigo muerto pese a tener CERO llamadores de produccion. El crate lleva un
    /// `#![allow(dead_code)]` global (main.rs), asi que nada avisa, y sus unicos consumidores
    /// son los centinelas de opcode: `stp_demolish_request_round_trip` fija 0x1D (ADR-037) y
    /// `the_voice_opcode_belongs_to_voice_and_to_nothing_else` fija que 0x50 es VoiceFrame
    /// (ADR-046) y que 0x4F sigue libre (ADR-047). Borrar esta funcion en una limpieza de
    /// codigo muerto se lleva por delante esas garantias.
    ///
    /// Los brazos de abajo son una tabla paralela a los discriminantes de `PacketType` y a los
    /// de `PacketPayload::type_code()`. Nada obliga a que las tres coincidan; hoy coinciden.
    /// Un opcode nuevo se anade en LAS TRES.
    pub fn from_u16(v: u16) -> Option<Self> {
        match v {
            0x00 => Some(Self::Discover),
            0x01 => Some(Self::PeerIntro),
            0x02 => Some(Self::Handshake),
            0x03 => Some(Self::HandshakeAck),
            0x04 => Some(Self::WorldSync),
            0x05 => Some(Self::Heartbeat),
            0x06 => Some(Self::Disconnect),
            0x07 => Some(Self::PeerList),
            0x10 => Some(Self::PlayerUpdate),
            0x11 => Some(Self::ChunkState),
            0x12 => Some(Self::ChunkDelta),
            0x13 => Some(Self::EntityUpdate),
            0x14 => Some(Self::StatUpdate),
            0x15 => Some(Self::InventorySync),
            0x16 => Some(Self::StpItemList),
            0x17 => Some(Self::StpPickupRequest),
            0x18 => Some(Self::StpPickupGranted),
            0x19 => Some(Self::StpDropRequest),
            0x1A => Some(Self::StpBuildingList),
            0x1B => Some(Self::StpPlaceRequest),
            0x1C => Some(Self::StpBuildAddRequest),
            0x1D => Some(Self::StpDemolishRequest),
            0x20 => Some(Self::Interact),
            0x21 => Some(Self::Attack),
            0x22 => Some(Self::Pickup),
            0x23 => Some(Self::Drop),
            0x24 => Some(Self::Craft),
            0x25 => Some(Self::PlaceStabilizer),
            0x26 => Some(Self::PlaceAnchor),
            0x27 => Some(Self::RepairAnchor),
            0x28 => Some(Self::UseConsumable),
            0x30 => Some(Self::ChunkTransfer),
            0x31 => Some(Self::ChunkTransferAck),
            0x32 => Some(Self::ChunkTeleport),
            0x33 => Some(Self::ChunkGenerate),
            0x34 => Some(Self::AnchorBroadcast),
            0x35 => Some(Self::StabilizerBroadcast),
            0x36 => Some(Self::WorldSyncChunk),
            0x37 => Some(Self::WorldSyncEnd),
            0x40 => Some(Self::StpCarryableList),
            0x41 => Some(Self::StpCarryablePickupRequest),
            0x42 => Some(Self::StpCarryablePickupGranted),
            0x43 => Some(Self::StpCarryableDropRequest),
            0x44 => Some(Self::StpHarvestableList),
            0x45 => Some(Self::StpHarvestHitRequest),
            0x46 => Some(Self::CorpseList),
            0x47 => Some(Self::CorpseSpawnRequest),
            0x48 => Some(Self::CorpseTakeRequest),
            0x49 => Some(Self::CorpseTakeResult),
            0x4A => Some(Self::PvpHitCandidate),
            0x4B => Some(Self::PvpDamageGrant),
            0x4C => Some(Self::PvpHitRejected),
            0x4D => Some(Self::PhantomAttackGrant),
            0x4E => Some(Self::NoiseReport),
            0x4F => Some(Self::StruggleReport),
            0x50 => Some(Self::VoiceFrame),
            0x51 => Some(Self::SprayPlaceRequest),
            0x52 => Some(Self::SprayPlaced),
            0x53 => Some(Self::SprayChunkRequest),
            0x54 => Some(Self::SprayDraft),
            0xF0 => Some(Self::Ack),
            0xF1 => Some(Self::Nack),
            0xF2 => Some(Self::Ping),
            _ => None,
        }
    }
}

// ─── Sync data types (shared between protocol payloads) ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerInfo {
    pub id: u16,
    pub name: String,
    pub addr: String,
    pub position: [f32; 3],
}

/// A host-authoritative STP world item instance, replicated to all peers so each
/// client reconstructs the same STP `ItemPickup` (`def_id` → `ItemDefinition` →
/// prefab). `id` is the network instance id (host-assigned, monotonic); `def_id`
/// is the STP `ItemDefinition` id (stable across instances). Phase 1.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StpItemInfo {
    pub id: u32,
    pub def_id: i32,
    pub count: u16,
    pub position: [f32; 3],
    pub rotation: f32,
    /// ADR-070: this item is still falling, so `position` MOVES between relays and the client
    /// must interpolate toward it instead of pinning the transform. Flips to false once the host
    /// puts it to sleep, which is the client's cue to fix the final position and hand the
    /// Rigidbody back to `isKinematic`.
    ///
    /// `serde(default)` = false → everything that already exists (chunk loot, corpse drops, world
    /// chests) keeps being born settled at zero cost, with no migration. Only the TRANSLATION is
    /// authoritative: the roll of the model while it falls is cosmetic and stays client-side (ADR-070
    /// decision 3), which is why no orientation field rides along here.
    #[serde(default)]
    pub settling: bool,
}

/// A host-authoritative STP building piece, replicated to all peers so each client
/// reconstructs the same placed piece (`def_id` → `BuildingPieceDefinition` → prefab).
/// `id` is the network instance id (host-assigned, monotonic); `def_id` is the STP
/// `BuildingPieceDefinition` id (stable across instances). Phase B1.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StpBuildingInfo {
    pub id: u32,
    pub def_id: i32,
    pub position: [f32; 3],
    pub rotation: f32,
    /// Phase B3: host-authoritative group identity. All pieces of one structure share a
    /// `group_id` so every client buckets them into one `BuildingPieceGroup` and rebuilds
    /// the socket cohesion. `0` = standalone (free pieces, e.g. campfire). Host-assigned.
    #[serde(default)]
    pub group_id: u32,
    /// Phase B2: host-authoritative construction progress — how many units of each
    /// build material have been accepted for this piece. Clients derive completion by
    /// comparing against the prefab-authored required amounts. Omitted when empty.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub added: Vec<StpBuildProgress>,
}

/// Phase B2: one (material → accepted count) entry of a building piece's construction
/// progress. `material_id` is the STP `BuildMaterialDefinition` id (stable).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StpBuildProgress {
    pub material_id: i32,
    pub count: u16,
}

/// A host-authoritative STP world carryable (log/stone/metal pile), replicated to all
/// peers so each client reconstructs the same `CarryablePickup` (`def_id` →
/// `CarryableDefinition` → pickup prefab). `id` is the network instance id (host-assigned,
/// monotonic); `def_id` is the STP `CarryableDefinition` id (stable). Phase B2.5.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StpCarryableInfo {
    pub id: u32,
    pub def_id: i32,
    pub position: [f32; 3],
    pub rotation: f32,
}

/// A host-authoritative STP scene-placed harvestable (tree/rock prefab), replicated to all
/// peers so each client reflects the same construction-resource health. `id` is the network
/// instance id (host-assigned); clients map it to their local harvestable by position
/// proximity. `remaining` is the host-authoritative harvest amount (1.0 full → 0.0 depleted).
/// Phase B2.6.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StpHarvestableInfo {
    pub id: u32,
    pub position: [f32; 3],
    pub remaining: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SessionConfig {
    pub max_players: u16,
    pub world_name: String,
    pub teleport_interval_min: f32,
    pub teleport_interval_max: f32,
}

impl Default for SessionConfig {
    fn default() -> Self {
        Self {
            max_players: 50,
            world_name: "Backrooms".into(),
            teleport_interval_min: 120.0,
            teleport_interval_max: 600.0,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AnchorInfo {
    pub chunk_pos: [i32; 2],
    pub durability: f32,
    pub installed_by: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StabilizerInfo {
    pub chunk_pos: [i32; 2],
    pub tier: u8,
    pub remaining_hours: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChunkSyncData {
    pub pos: [i32; 2],
    #[serde(default)]
    pub layer: i8,
    pub seed: u64,
    pub template_id: u8,
    pub rotation: u16,
    pub mirrored: bool,
    pub has_workbench: bool,
    #[serde(default)]
    pub layout: ChunkLayoutV1,
    pub stabilized: bool,
    pub anchored: bool,
    pub teleport_timer: f32,
    pub entities: Vec<EntitySyncData>,
    pub items: Vec<ItemSyncData>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EntitySyncData {
    pub id: u32,
    pub entity_type: String,
    pub position: [f32; 3],
    pub rotation: f32,
    pub health: u8,
    pub state: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ItemSyncData {
    pub id: u32,
    pub item_type: String,
    pub quantity: u16,
    pub position: [f32; 3],
}

fn default_phantom_density_scale() -> f32 {
    1.0
}

/// ADR-060 (d): un roster sin campos de paginación es el de un emisor pre-paginación, que mandaba
/// UNA página con la lista entera. El default de `u16` sería 0, y un `page_count` de 0 es
/// incoherente (el ensamblador lo descartaría), así que este roster nunca se aplicaría.
fn default_page_count() -> u16 {
    1
}

// ─── Packet payload (MessagePack body after the 12-byte header) ───

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PacketPayload {
    // Connection
    Handshake {
        player_name: String,
        version: String,
    },
    HandshakeAck {
        assigned_id: u16,
        world_seed: u64,
        config: SessionConfig,
        peers: Vec<PeerInfo>,
        anchors: Vec<AnchorInfo>,
        stabilizers: Vec<StabilizerInfo>,
        /// P0-2: density multiplier for the phantom population draw (`PHANTOM_DENSITY_SCALE`),
        /// travels alongside `world_seed` for the same reason — the draw is a pure function of
        /// both, so a joiner with a differing local env would derive a different population.
        /// Appended last + serde(default) → a peer built before P0-2 omits it and decodes to
        /// 1.0 (no scaling, same as the env var's own default).
        #[serde(default = "default_phantom_density_scale")]
        phantom_density_scale: f32,
    },
    /// DEPRECADO por ADR-060 (goteo `WorldSyncChunk`/`WorldSyncEnd`). Ningún emisor queda;
    /// el decode se conserva una versión y luego se retira el variant entero.
    WorldSync {
        world_seed: u64,
        world_revision: u64,
        chunks: Vec<ChunkSyncData>,
    },
    /// ADR-060: un chunk del goteo de snapshot. Estampado con `world_revision` porque la capa
    /// reliable es at-least-once SIN orden — la completitud se cuenta POR REVISION, nunca por
    /// secuencia de llegada. Aplica por `apply_chunk_sync` (upsert), sin ack de aplicación:
    /// la redundancia del `ChunkTransferAck` del handoff es exactamente lo que se evita.
    WorldSyncChunk {
        world_revision: u64,
        data: ChunkSyncData,
    },
    /// ADR-060: cierre del goteo. El receptor declara el snapshot completo cuando recibió este
    /// payload Y los chunks aplicados de `world_revision` alcanzan `chunk_count`; el spawn del
    /// joiner se gatea en esa completitud (decisión "spawn en End"). Una revision más nueva
    /// desecha el estado de conteo de las anteriores.
    WorldSyncEnd {
        world_revision: u64,
        chunk_count: u32,
    },
    Heartbeat,
    Disconnect {
        reason: String,
    },
    PeerList {
        peers: Vec<PeerInfo>,
    },
    /// ADR-060 (d): paginado. `generation` identifica la ronda de emisión (una por broadcast, a
    /// 10 Hz); el receptor solo aplica el roster cuando tiene sus `page_count` páginas. Los tres
    /// campos son `serde(default)`, así que un roster de una página (el caso normal) decodifica
    /// como `gen=0, page=0, page_count=1`… salvo que `page_count` por defecto sería 0, que es
    /// incoherente — de ahí `default_page_count`, que devuelve 1: un emisor viejo mandaba
    /// exactamente eso, una página única con el roster entero.
    StpItemList {
        items: Vec<StpItemInfo>,
        #[serde(default)]
        generation: u32,
        #[serde(default)]
        page: u16,
        #[serde(default = "default_page_count")]
        page_count: u16,
    },
    StpPickupRequest {
        item_id: u32,
        requester_id: u16,
    },
    StpPickupGranted {
        item_id: u32,
        def_id: i32,
        count: u16,
    },
    StpDropRequest {
        drop_id: u64,
        def_id: i32,
        count: u16,
        position: [f32; 3],
        rotation: f32,
        /// ADR-070: the throw impulse the dropper reported (view direction × force). The position
        /// above is now the HAND, not a floor-snapped resting place — the host decides where the
        /// object comes to rest. `serde(default)` → a peer that omits it drops the object straight
        /// down from the hand, which is the correct degradation and still falls.
        #[serde(default)]
        velocity: [f32; 3],
    },
    /// ADR-060 (d): paginado — ver `StpItemList`. Éste es el roster que el doc-comment de
    /// `send_datagram` señalaba como el primero en reventar (~800 piezas colocadas).
    StpBuildingList {
        buildings: Vec<StpBuildingInfo>,
        #[serde(default)]
        generation: u32,
        #[serde(default)]
        page: u16,
        #[serde(default = "default_page_count")]
        page_count: u16,
    },
    StpPlaceRequest {
        place_id: u64,
        def_id: i32,
        position: [f32; 3],
        rotation: f32,
        /// Phase B3: the group the client attached to (0 = new group, host mints one).
        group_id: u32,
        /// Phase B3: true if the piece is a GroupBuildingPiece (sockets/cohesion); false
        /// for free pieces (no group, no pose-cell dedup — free pieces may stack).
        is_group: bool,
    },
    StpBuildAddRequest {
        add_id: u64,
        building_id: u32,
        material_id: i32,
    },
    /// ADR-068: a joiner asks the host to paint a spray. Everything here is a REQUEST — the host
    /// re-derives the chunk, re-validates every cap against the REQUESTER's own known position
    /// and mints the id. The joiner never anchors, never validates and never numbers.
    SprayPlaceRequest {
        place_id: u64,
        layer: u8,
        world_pos: [f32; 3],
        yaw: f32,
        size: [f32; 2],
        strokes: Vec<crate::world::spray::SprayStroke>,
    },
    /// ADR-068: a spray the host ACCEPTED, on its way to every peer. Travels one per packet and
    /// not as a roster, unlike `StpBuildingList`: a spray is ~1,9 KB, so even a modest chunk's
    /// worth would blow the datagram that ADR-060 (d) already had to paginate for far lighter
    /// elements.
    SprayPlaced {
        spray: crate::world::spray::Spray,
    },
    /// ADR-068: "que hay pintado en este chunk". El host responde con un `SprayPlaced` por
    /// pintada, no con una lista: ver el porque en el doc de `SprayPlaced`.
    SprayChunkRequest {
        cx: i32,
        cz: i32,
        layer: u8,
    },
    /// ADR-078 (fase B de ADR-068): trozo de un trazo que se esta pintando AHORA. Efimero: no
    /// entra en `SprayStore`, no se guarda y no cuenta para ningun tope — la autoridad sigue
    /// siendo `SprayPlaced`, que llega entera al soltar y sustituye lo dibujado.
    ///
    /// Solo los puntos NUEVOS desde el ultimo envio (`first_index`), no el gesto entero: mandar
    /// el gesto completo a 10 Hz seria 1,9 KB x 10 x peers.
    ///
    /// `anchor` + `yaw` definen el plano y los puntos van en MILIMETROS sobre el (pares i16 en
    /// `points_mm`). Anclaje en MUNDO y no chunk-local al reves que la pintada: esto vive 3 s y
    /// muere, y resolver chunk en cada envio seria trabajo para nada (ADR-078 decision 4).
    SprayDraft {
        place_id: u64,
        layer: u8,
        anchor: [f32; 3],
        yaw: f32,
        color: u8,
        width: f32,
        /// Indice del primer punto de este paquete dentro del trazo, para que el receptor sepa
        /// si se ha perdido algo por el camino y no cosa dos trozos que no van seguidos.
        first_index: u16,
        /// Pares (u, v) en milimetros sobre el plano del ancla, como `i16` little-endian
        /// empaquetados en un BLOB: 4 bytes por punto. Blob y no `Vec<i16>` por lo mismo que
        /// ADR-068 paso los puntos de un trazo a binario — un array de enteros msgpack cuesta
        /// hasta 3 bytes por valor (6 B/punto) y ademas obliga a que las dos puntas se pongan de
        /// acuerdo en el tipo, cuando el cliente ya escribe `bin` para el otro camino.
        #[serde(with = "serde_bytes")]
        points_mm: Vec<u8>,
    },
    /// ADR-037: the sender cancelled a placed-but-unbuilt piece. Only the host acts on it;
    /// it removes the entry from `stp_buildings` and the existing 10 Hz relay makes every
    /// client's replicator destroy its copy through the stale-sweep it already runs.
    StpDemolishRequest {
        demolish_id: u64,
        building_id: u32,
    },

    // State
    PlayerUpdate {
        position: [f32; 3],
        rotation: f32,
        animation: String,
        /// ADR-020: cosmetic crouch (appended last + serde(default) → a v2 peer that
        /// omits it decodes to false; wire-compat across the v2→v3 schema bump).
        #[serde(default)]
        crouch: bool,
        /// ADR-021: cosmetic camera pitch in degrees (−90..90, quantized to 1°). Appended
        /// last + serde(default) → a v3 peer that omits it decodes to 0 (looking forward);
        /// wire-compat across the v3→v4 schema bump.
        #[serde(default)]
        pitch: i8,
        /// ADR-022: cosmetic worn clothing item IDs [Head, Torso, Legs, Feet] (0 = empty).
        /// Appended last + serde(default) → a v4 peer that omits it decodes to [0,0,0,0]
        /// (no clothing); wire-compat across the v4→v5 schema bump.
        #[serde(default)]
        equipment: [i32; 4],
        /// ADR-023: cosmetic held item ID (0 = empty hands). Appended last + serde(default) →
        /// a v5 peer that omits it decodes to 0 (empty hands); wire-compat across the v5→v6
        /// schema bump.
        #[serde(default)]
        held_item: i32,
        /// ADR-024: cosmetic hit-reaction counter (monotonic, wrapping; 0 = never hit). Appended
        /// last + serde(default) → a v6 peer that omits it decodes to 0 (no flinch); wire-compat
        /// across the v6→v7 schema bump.
        #[serde(default)]
        hit_seq: u8,
        /// ADR-028 post-E3: cosmetic dead flag, SERVER-derived on the owning backend
        /// (`player.stats.is_dead()`) — observers hide the standing proxy while dead. Appended
        /// last + serde(default) → a v9 peer that omits it decodes to false (never hides);
        /// wire-compat across the v9→v10 schema bump.
        #[serde(default)]
        dead: bool,
        /// ADR-038: cosmetic "showing its real form" flag — true only while the robapieles
        /// (ADR-016) is in `Sprint`/`Statue`, sealed by `PhantomDriver` and relayed like any
        /// other pose field. Appended last + serde(default) → a v11 peer that omits it decodes
        /// to false (never reveals); wire-compat across the v11→v12 schema bump.
        #[serde(default)]
        revealed: bool,
        /// ADR-042: cosmetic "the wieldable in my hands is lit" flag. Appended last +
        /// serde(default) → a v12 peer that omits it decodes to false (never lit); wire-compat
        /// across the v12→v13 schema bump.
        #[serde(default)]
        light_on: bool,
        /// ADR-042: cosmetic shot counter (monotonic, wrapping; 0 = never fired). Appended last +
        /// serde(default) → a v12 peer that omits it decodes to 0 (silent); wire-compat across
        /// the v12→v13 schema bump.
        #[serde(default)]
        fire_seq: u8,
        /// ADR-044: cosmetic sustained-state bits (0 = aiming, 1 = reloading). Appended last +
        /// serde(default) → a v14 peer that omits it decodes to 0 (neither); wire-compat across
        /// the v14→v15 schema bump.
        #[serde(default)]
        buttons: u16,
        /// ADR-044: cosmetic melee-swing counter (monotonic, wrapping; 0 = never swung). Appended
        /// last + serde(default) → a v14 peer that omits it decodes to 0; wire-compat across the
        /// v14→v15 schema bump.
        #[serde(default)]
        melee_seq: u8,
        /// ADR-048: cosmetic vocalisation counter (monotonic, wrapping; 0 = never vocalised).
        /// Appended last + serde(default) → a v17 peer that omits it decodes to 0 (silent);
        /// wire-compat across the v17→v18 schema bump.
        #[serde(default)]
        vocal_seq: u8,
        /// ADR-048: which voice the last bump was. Read ONLY together with `vocal_seq`.
        #[serde(default)]
        vocal_kind: u8,
        /// ADR-049: cosmetic carry state — the `CarryableDefinition` id this peer is hauling
        /// (0 = empty hands) and how many units. A LEVEL, not a counter: a dropped datagram is
        /// corrected by the next one, so there is nothing to sequence. Appended last +
        /// serde(default) → a v18 peer that omits them decodes to (0, 0) (empty-handed);
        /// wire-compat across the v18→v19 schema bump.
        #[serde(default)]
        carry_def: i32,
        #[serde(default)]
        carry_count: u8,
    },
    ChunkState {
        data: ChunkSyncData,
    },
    ChunkDelta {
        pos: [i32; 2],
        entities: Vec<EntitySyncData>,
        items: Vec<ItemSyncData>,
    },
    EntityUpdate {
        chunk_pos: [i32; 2],
        entities: Vec<EntitySyncData>,
    },

    // Actions
    Interact {
        requester_id: u16,
        request_id: u64,
        target_id: u32,
        target_kind: String,
        interaction_type: String,
        player_position: [f32; 3],
    },
    Attack {
        target_entity_id: Option<u32>,
    },
    Pickup {
        item_id: u32,
    },
    Drop {
        slot: u8,
        quantity: u16,
    },
    Craft {
        recipe: String,
    },
    PlaceStabilizer {
        slot: u8,
    },
    PlaceAnchor,

    // World
    ChunkTransfer {
        data: ChunkSyncData,
    },
    ChunkTransferAck {
        pos: [i32; 2],
    },
    ChunkTeleport {
        old_pos: [i32; 2],
        new_pos: [i32; 2],
        new_seed: u64,
    },
    AnchorBroadcast {
        chunk_pos: [i32; 2],
        durability: f32,
        installed_by: String,
    },
    StabilizerBroadcast {
        chunk_pos: [i32; 2],
        tier: u8,
        remaining_hours: f32,
    },
    /// ADR-060 (d): paginado — ver `StpItemList`.
    StpCarryableList {
        carryables: Vec<StpCarryableInfo>,
        #[serde(default)]
        generation: u32,
        #[serde(default)]
        page: u16,
        #[serde(default = "default_page_count")]
        page_count: u16,
    },
    StpCarryablePickupRequest {
        carryable_id: u32,
        requester_id: u16,
    },
    StpCarryablePickupGranted {
        carryable_id: u32,
        def_id: i32,
    },
    StpCarryableDropRequest {
        drop_id: u64,
        def_id: i32,
        position: [f32; 3],
        rotation: f32,
    },
    /// ADR-060 (d): paginado — ver `StpItemList`.
    StpHarvestableList {
        harvestables: Vec<StpHarvestableInfo>,
        #[serde(default)]
        generation: u32,
        #[serde(default)]
        page: u16,
        #[serde(default = "default_page_count")]
        page_count: u16,
    },
    StpHarvestHitRequest {
        hit_id: u64,
        harvestable_id: u32,
        amount: f32,
    },

    // ADR-028 Fase E: host-authoritative corpse relay. Corpses reuse the storage type
    // (`world::corpse::CorpseData`) directly on the wire — same precedent as ChunkLayoutV1.
    /// Host → all: the full authoritative corpse roster, broadcast at 10 Hz (self-healing,
    /// same pattern as StpItemList). Joiners mirror it verbatim into their `world.corpses`.
    /// ADR-060 (d): paginado — ver `StpItemList`. Un `CorpseData` lleva su inventario entero,
    /// así que aquí el tamaño por elemento es el más variable de los cinco.
    CorpseList {
        corpses: Vec<crate::world::corpse::CorpseData>,
        #[serde(default)]
        generation: u32,
        #[serde(default)]
        page: u16,
        #[serde(default = "default_page_count")]
        page_count: u16,
    },
    /// Joiner → host (reliable): "my player died with this loot snapshot — spawn the corpse".
    /// The host dedupes by (sender, request_id): reliable retransmits spawn exactly one corpse.
    CorpseSpawnRequest {
        request_id: u64,
        requester_id: u16,
        owner_name: String,
        position: [f32; 3],
        equipment: [i32; 4],
        held_item: i32,
        items: Vec<crate::world::corpse::CorpseStack>,
    },
    /// Joiner → host (reliable): "take `quantity` from stack `item_index` of corpse `corpse_id`".
    /// `requester_pos` is the claimed position (same trust level as Interact.player_position);
    /// the host validates it against the corpse's frozen death position. Deduped like spawn.
    CorpseTakeRequest {
        request_id: u64,
        requester_id: u16,
        corpse_id: u32,
        item_index: u32,
        quantity: u16,
        requester_pos: [f32; 3],
    },
    /// Host → requester (reliable): the verdict for one CorpseTakeRequest. The joiner backend
    /// dedupes by request_id and emits the SAME IPC event Fase D already consumes
    /// (corpse_item_taken / corpse_take_rejected) to its own Unity.
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

    // ADR-029 V0: PvP hit candidate -> host validation -> victim-applied damage. The health
    // mutation itself never crosses this enum — only the candidate report, the validated
    // grant, and the rejection travel P2P; `PlayerStats::take_damage` runs locally on
    // whichever backend owns the affected player (see game_loop.rs authority split).
    /// Shooter backend -> host (reliable): "I hit this proxy" — a CANDIDATE only, never
    /// authoritative. `origin`/`hit_position`/`client_tick` are debug/feedback, not validated.
    PvpHitCandidate {
        request_id: u64,
        attacker_id: u32,
        victim_id: u32,
        weapon_id: i32,
        damage: f32,
        origin: [f32; 3],
        direction: [f32; 3],
        #[serde(default)]
        client_tick: Option<u32>,
        #[serde(default)]
        hit_position: Option<[f32; 3]>,
    },
    /// Host -> victim backend (reliable): damage already validated/clamped by host authority.
    /// The victim backend is STILL free to reject it locally (see `victim_invulnerable`
    /// re-check, ADR-029 invulnerability amendment) before calling `PlayerStats::take_damage`.
    PvpDamageGrant {
        request_id: u64,
        attacker_id: u32,
        victim_id: u32,
        weapon_id: i32,
        damage: f32,
        reason: String,
    },
    /// Host -> shooter backend (reliable): a candidate was rejected. `reason` is one of the
    /// stable values documented in game_loop.rs (`duplicate`, `attacker_missing`,
    /// `victim_missing`, `victim_dead`, `victim_invulnerable`, `invalid_weapon`,
    /// `invalid_damage`, `invalid_direction`, `too_far`, `line_of_sight_failed`,
    /// `not_authority`, `self_hit`, `stale_or_malformed`).
    PvpHitRejected {
        request_id: u64,
        attacker_id: u32,
        victim_id: u32,
        reason: String,
    },

    /// ADR-047. Host -> victim backend (reliable): a robapieles landed a blow on the player that
    /// backend owns. The host is the ONLY simulator of phantoms (ADR-016), but ADR-025 makes the
    /// victim's own backend the only writer of its health — so the decision travels and the
    /// mutation stays home, exactly like `PvpDamageGrant`.
    ///
    /// NO phantom id, deliberately: ADR-016 §1's hard invariant is that the "this is a phantom"
    /// mark never crosses the wire, and nothing consumes an attacker here — `PhantomAttackHandler`
    /// reads none of its three events for it. A field with no reader is a bad price for weakening
    /// an invariant.
    ///
    /// `kind`: 0 = hit (`damage`), 1 = kill, 2 = knockback (`impulse`, m/s, client-applied),
    /// 3 = grab start (`damage` carries the escape window in SECONDS, not damage — ADR-050),
    /// 4 = grab release (no payload beyond the envelope), 5 = knockdown (`damage` carries stun
    /// SECONDS same trick as kind 3, `impulse` carries the shove — ADR-076; zero health touch).
    /// `request_id` is minted by the host and is the dedupe key on its own.
    PhantomAttackGrant {
        request_id: u64,
        victim_id: u32,
        kind: u8,
        damage: f32,
        impulse: [f32; 2],
    },

    /// ADR-047. Joiner -> host (UNRELIABLE): a noise happened at `position`, audible for
    /// `loudness` metres. What travels is a NOISE IN A PLACE, never a player's position — that
    /// distinction is the whole design of ADR-041, so there is no player id here.
    ///
    /// Unreliable on purpose: a transient stimulus must not occupy the 32-slot reliable window
    /// (an automatic weapon would fill it, and blowing `MAX_RETRIES` purges the peer's entire
    /// reliable queue, taking pickups/corpses/PvP with it). A lost noise self-heals on the next
    /// shot.
    NoiseReport {
        position: [f32; 3],
        loudness: f32,
    },
    /// ADR-050 point 9 — a joiner broke out of a grab. Travels joiner→host, like `NoiseReport` and
    /// for the same reason: the host is the only backend that simulates phantoms.
    ///
    /// CARRIES NOTHING, and that is the security property rather than an oversight. The victim is
    /// the sender, which the transport already knows, so there is no field to forge: the worst a
    /// modified client can do is claim to have escaped a grab that does not exist, which drains
    /// into an empty set. Compare `NoiseReport`, which does carry a position and needs clamping.
    ///
    /// RELIABLE, unlike the noise: a dropped noise self-heals on the next shot, a dropped struggle
    /// is a death the player earned their way out of.
    StruggleReport,

    /// ADR-046 — one encoded voice frame, relayed by the host on behalf of the speaker
    /// (`send_unreliable_as`, the same ADR-015 mechanism the pose relay uses).
    ///
    /// There is NO speaker id in the payload: the header's `sender_id` already carries it, and
    /// a second copy inside would be a field a modified client could disagree with — it could
    /// claim to be someone else's voice.
    ///
    /// There is no position either. The listener already receives the speaker's pose at 10 Hz
    /// and attaches the audio to that peer's proxy, so shipping coordinates alongside every
    /// frame would be paying for a value the receiver already has, 25 times a second — the same
    /// reasoning that kept footsteps off the wire in ADR-042.
    ///
    /// Unreliable, and this one is not a preference: a retransmitted voice frame arrives after
    /// the moment it belonged to, and blowing `MAX_RETRIES` purges the peer's ENTIRE reliable
    /// queue (ADR-039), taking pickups, corpses and PvP verdicts with it. A lost frame is
    /// covered by the decoder's packet-loss concealment.
    VoiceFrame {
        seq: u16,
        #[serde(with = "serde_bytes")]
        data: Vec<u8>,
    },

    // Reliability
    Ack {
        acked_sequence: u32,
    },
    /// Auditoría (H12a, 2026-08-10): decodifica pero nunca lo emite nadie en este crate — el
    /// handler es no-op a propósito (`handlers.rs`, brazo `PacketPayload::Nack`). Retirar la
    /// variante es cambio de enum de wire (regla dura #7 = ADR), no una corrección adosada.
    Nack {
        requested_sequence: u32,
    },
    Ping {
        send_time: u32,
    },
}

impl PacketPayload {
    /// Returns the wire type code for this payload variant.
    pub fn type_code(&self) -> u16 {
        match self {
            Self::Handshake { .. } => PacketType::Handshake as u16,
            Self::HandshakeAck { .. } => PacketType::HandshakeAck as u16,
            Self::WorldSync { .. } => PacketType::WorldSync as u16,
            Self::WorldSyncChunk { .. } => PacketType::WorldSyncChunk as u16,
            Self::WorldSyncEnd { .. } => PacketType::WorldSyncEnd as u16,
            Self::Heartbeat => PacketType::Heartbeat as u16,
            Self::Disconnect { .. } => PacketType::Disconnect as u16,
            Self::PeerList { .. } => PacketType::PeerList as u16,
            Self::StpItemList { .. } => PacketType::StpItemList as u16,
            Self::StpPickupRequest { .. } => PacketType::StpPickupRequest as u16,
            Self::StpPickupGranted { .. } => PacketType::StpPickupGranted as u16,
            Self::StpDropRequest { .. } => PacketType::StpDropRequest as u16,
            Self::StpBuildingList { .. } => PacketType::StpBuildingList as u16,
            Self::StpPlaceRequest { .. } => PacketType::StpPlaceRequest as u16,
            Self::StpBuildAddRequest { .. } => PacketType::StpBuildAddRequest as u16,
            Self::StpDemolishRequest { .. } => PacketType::StpDemolishRequest as u16,
            Self::SprayPlaceRequest { .. } => PacketType::SprayPlaceRequest as u16,
            Self::SprayPlaced { .. } => PacketType::SprayPlaced as u16,
            Self::SprayChunkRequest { .. } => PacketType::SprayChunkRequest as u16,
            Self::SprayDraft { .. } => PacketType::SprayDraft as u16,
            Self::PlayerUpdate { .. } => PacketType::PlayerUpdate as u16,
            Self::ChunkState { .. } => PacketType::ChunkState as u16,
            Self::ChunkDelta { .. } => PacketType::ChunkDelta as u16,
            Self::EntityUpdate { .. } => PacketType::EntityUpdate as u16,
            Self::Interact { .. } => PacketType::Interact as u16,
            Self::Attack { .. } => PacketType::Attack as u16,
            Self::Pickup { .. } => PacketType::Pickup as u16,
            Self::Drop { .. } => PacketType::Drop as u16,
            Self::Craft { .. } => PacketType::Craft as u16,
            Self::PlaceStabilizer { .. } => PacketType::PlaceStabilizer as u16,
            Self::PlaceAnchor => PacketType::PlaceAnchor as u16,
            Self::ChunkTransfer { .. } => PacketType::ChunkTransfer as u16,
            Self::ChunkTransferAck { .. } => PacketType::ChunkTransferAck as u16,
            Self::ChunkTeleport { .. } => PacketType::ChunkTeleport as u16,
            Self::AnchorBroadcast { .. } => PacketType::AnchorBroadcast as u16,
            Self::StabilizerBroadcast { .. } => PacketType::StabilizerBroadcast as u16,
            Self::StpCarryableList { .. } => PacketType::StpCarryableList as u16,
            Self::StpCarryablePickupRequest { .. } => PacketType::StpCarryablePickupRequest as u16,
            Self::StpCarryablePickupGranted { .. } => PacketType::StpCarryablePickupGranted as u16,
            Self::StpCarryableDropRequest { .. } => PacketType::StpCarryableDropRequest as u16,
            Self::StpHarvestableList { .. } => PacketType::StpHarvestableList as u16,
            Self::StpHarvestHitRequest { .. } => PacketType::StpHarvestHitRequest as u16,
            Self::CorpseList { .. } => PacketType::CorpseList as u16,
            Self::CorpseSpawnRequest { .. } => PacketType::CorpseSpawnRequest as u16,
            Self::CorpseTakeRequest { .. } => PacketType::CorpseTakeRequest as u16,
            Self::CorpseTakeResult { .. } => PacketType::CorpseTakeResult as u16,
            Self::PvpHitCandidate { .. } => PacketType::PvpHitCandidate as u16,
            Self::PvpDamageGrant { .. } => PacketType::PvpDamageGrant as u16,
            Self::PvpHitRejected { .. } => PacketType::PvpHitRejected as u16,
            Self::PhantomAttackGrant { .. } => PacketType::PhantomAttackGrant as u16,
            Self::NoiseReport { .. } => PacketType::NoiseReport as u16,
            Self::StruggleReport => PacketType::StruggleReport as u16,
            Self::VoiceFrame { .. } => PacketType::VoiceFrame as u16,
            Self::Ack { .. } => PacketType::Ack as u16,
            Self::Nack { .. } => PacketType::Nack as u16,
            Self::Ping { .. } => PacketType::Ping as u16,
        }
    }
}

// ─── Wire encoding / decoding ───

/// Encode a packet: 12-byte header + MessagePack payload.
pub fn encode_packet(header: &PacketHeader, payload: &PacketPayload) -> Vec<u8> {
    let header_bytes = header.to_bytes();
    let payload_bytes = rmp_serde::to_vec_named(payload).expect("payload serialization");
    let mut buf = Vec::with_capacity(HEADER_SIZE + payload_bytes.len());
    buf.extend_from_slice(&header_bytes);
    buf.extend_from_slice(&payload_bytes);
    buf
}

/// Decode a packet from raw bytes: header + MessagePack payload.
pub fn decode_packet(data: &[u8]) -> Result<(PacketHeader, PacketPayload), String> {
    let header =
        PacketHeader::from_bytes(data).ok_or_else(|| "packet too short for header".to_string())?;
    let payload: PacketPayload =
        rmp_serde::from_slice(&data[HEADER_SIZE..]).map_err(|e| format!("payload decode: {e}"))?;
    Ok((header, payload))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn header_round_trip() {
        let h = PacketHeader::new(0x02, 1, 42, 1000);
        let bytes = h.to_bytes();
        let h2 = PacketHeader::from_bytes(&bytes).unwrap();
        assert_eq!(h, h2);
    }

    #[test]
    fn packet_type_from_u16_round_trip() {
        assert_eq!(
            PacketType::from_u16(PacketType::Handshake as u16),
            Some(PacketType::Handshake)
        );
        assert_eq!(
            PacketType::from_u16(PacketType::Ack as u16),
            Some(PacketType::Ack)
        );
        assert_eq!(PacketType::from_u16(0xFF), None);
    }

    // ADR-037. Non-default values on purpose: a variant that only ever round-trips zeros would
    // pass even if the two fields were swapped on the wire.
    #[test]
    fn stp_demolish_request_round_trip() {
        let payload = PacketPayload::StpDemolishRequest {
            demolish_id: 7_000_000_042,
            building_id: 0x6000_0009,
        };
        let header = PacketHeader::new(payload.type_code(), 1, 1, 100);
        let data = encode_packet(&header, &payload);
        let (h2, p2) = decode_packet(&data).unwrap();

        assert_eq!(h2.packet_type, PacketType::StpDemolishRequest as u16);
        assert_eq!(
            PacketType::from_u16(0x1D),
            Some(PacketType::StpDemolishRequest),
            "0x1D must decode back to the variant — the code is the wire contract"
        );
        match p2 {
            PacketPayload::StpDemolishRequest {
                demolish_id,
                building_id,
            } => {
                assert_eq!(demolish_id, 7_000_000_042);
                assert_eq!(building_id, 0x6000_0009);
            }
            other => panic!("wrong payload decoded: {other:?}"),
        }
    }

    #[test]
    fn handshake_packet_round_trip() {
        let payload = PacketPayload::Handshake {
            player_name: "TestPlayer".into(),
            version: "0.1.0".into(),
        };
        let header = PacketHeader::new(payload.type_code(), 1, 1, 100);
        let data = encode_packet(&header, &payload);
        let (h2, p2) = decode_packet(&data).unwrap();
        assert_eq!(h2.packet_type, PacketType::Handshake as u16);
        assert_eq!(h2.sender_id, 1);
        match p2 {
            PacketPayload::Handshake {
                player_name,
                version,
            } => {
                assert_eq!(player_name, "TestPlayer");
                assert_eq!(version, "0.1.0");
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn handshake_ack_round_trip() {
        let payload = PacketPayload::HandshakeAck {
            assigned_id: 2,
            world_seed: 42,
            config: SessionConfig::default(),
            peers: vec![PeerInfo {
                id: 1,
                name: "Host".into(),
                addr: "127.0.0.1:7778".into(),
                position: [0.0, 1.8, 0.0],
            }],
            anchors: vec![],
            stabilizers: vec![],
            phantom_density_scale: 2.5,
        };
        let header = PacketHeader::new(payload.type_code(), 1, 1, 200);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::HandshakeAck {
                assigned_id,
                world_seed,
                phantom_density_scale,
                ..
            } => {
                assert_eq!(assigned_id, 2);
                assert_eq!(world_seed, 42);
                assert_eq!(phantom_density_scale, 2.5);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn player_update_round_trip() {
        let payload = PacketPayload::PlayerUpdate {
            position: [10.0, 1.8, 20.0],
            rotation: 90.0,
            animation: "walk".into(),
            crouch: true,
            pitch: -45,
            equipment: [101, 202, 303, 404],
            held_item: 12345,
            hit_seq: 7,
            dead: true,
            revealed: true,
            light_on: true,
            fire_seq: 9,
            buttons: 0b11,
            melee_seq: 4,
            vocal_seq: 6,
            vocal_kind: 2,
            carry_def: -1208217892,
            carry_count: 3,
        };
        let header = PacketHeader::new(payload.type_code(), 3, 100, 5000);
        let data = encode_packet(&header, &payload);
        let (h2, p2) = decode_packet(&data).unwrap();
        assert_eq!(h2.sender_id, 3);
        match p2 {
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
            } => {
                assert_eq!(position, [10.0, 1.8, 20.0]);
                assert_eq!(rotation, 90.0);
                assert_eq!(animation, "walk");
                assert!(crouch);
                assert_eq!(pitch, -45);
                assert_eq!(equipment, [101, 202, 303, 404]);
                assert_eq!(held_item, 12345);
                assert_eq!(hit_seq, 7);
                assert!(dead);
                assert!(revealed);
                assert!(light_on);
                assert_eq!(fire_seq, 9);
                assert_eq!(buttons, 0b11);
                // ADR-048: non-default on BOTH, so a field silently dropped from the wire fails
                // here rather than looking like a creature that simply never vocalised.
                assert_eq!(vocal_seq, 6);
                assert_eq!(vocal_kind, 2);
                assert_eq!(melee_seq, 4);
                // ADR-049: same discipline, and `carry_def` is deliberately a real negative
                // definition id — the ids are Random.Range over the whole i32 range, so a test that
                // only ever saw small positives would not catch a width or sign mistake on the wire.
                assert_eq!(carry_def, -1208217892);
                assert_eq!(carry_count, 3);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn heartbeat_round_trip() {
        let payload = PacketPayload::Heartbeat;
        let header = PacketHeader::new(payload.type_code(), 1, 50, 3000);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        assert!(matches!(p2, PacketPayload::Heartbeat));
    }

    #[test]
    fn chunk_transfer_round_trip() {
        let payload = PacketPayload::ChunkTransfer {
            data: ChunkSyncData {
                pos: [3, 2],
                layer: 0,
                seed: 12345,
                template_id: 4,
                rotation: 90,
                mirrored: true,
                has_workbench: false,
                layout: ChunkLayoutV1::default(),
                stabilized: false,
                anchored: false,
                teleport_timer: 300.0,
                entities: vec![EntitySyncData {
                    id: 1,
                    entity_type: "lurker".into(),
                    position: [160.0, 0.0, 110.0],
                    rotation: 45.0,
                    health: 50,
                    state: "idle".into(),
                }],
                items: vec![ItemSyncData {
                    id: 10,
                    item_type: "metal".into(),
                    quantity: 1,
                    position: [155.0, 0.0, 115.0],
                }],
            },
        };
        let header = PacketHeader::new(payload.type_code(), 1, 200, 10000);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::ChunkTransfer { data } => {
                assert_eq!(data.pos, [3, 2]);
                assert_eq!(data.entities.len(), 1);
                assert_eq!(data.items.len(), 1);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn world_sync_round_trip_includes_seed_revision_and_counts() {
        let payload = PacketPayload::WorldSync {
            world_seed: 1234,
            world_revision: 7,
            chunks: vec![ChunkSyncData {
                pos: [0, 0],
                layer: 0,
                seed: 1234,
                template_id: 1,
                rotation: 0,
                mirrored: false,
                has_workbench: true,
                layout: ChunkLayoutV1::default(),
                stabilized: false,
                anchored: false,
                teleport_timer: 300.0,
                entities: vec![],
                items: vec![],
            }],
        };
        let header = PacketHeader::new(payload.type_code(), 1, 300, 10000);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::WorldSync {
                world_seed,
                world_revision,
                chunks,
            } => {
                assert_eq!(world_seed, 1234);
                assert_eq!(world_revision, 7);
                assert_eq!(chunks.len(), 1);
            }
            _ => panic!("wrong variant"),
        }
    }

    // ADR-060. Valores no-default a propósito (patrón ADR-037): un chunk con revision 0 y
    // pos [0,0] pasaría aunque los campos se cruzaran en el wire.
    #[test]
    fn world_sync_chunk_round_trip_keeps_revision_and_chunk() {
        let payload = PacketPayload::WorldSyncChunk {
            world_revision: 9,
            data: ChunkSyncData {
                pos: [-2, 5],
                layer: 1,
                seed: 777,
                template_id: 3,
                rotation: 180,
                mirrored: true,
                has_workbench: true,
                layout: ChunkLayoutV1::default(),
                stabilized: true,
                anchored: false,
                teleport_timer: 42.5,
                entities: vec![],
                items: vec![ItemSyncData {
                    id: 4,
                    item_type: "cloth".into(),
                    quantity: 2,
                    position: [1.0, 0.0, 2.0],
                }],
            },
        };
        let header = PacketHeader::new(payload.type_code(), 1, 400, 10000);
        assert_eq!(
            header.packet_type, 0x36,
            "opcode de WorldSyncChunk (ADR-060)"
        );
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::WorldSyncChunk {
                world_revision,
                data,
            } => {
                assert_eq!(world_revision, 9);
                assert_eq!(data.pos, [-2, 5]);
                assert_eq!(data.layer, 1);
                assert_eq!(data.items.len(), 1);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn world_sync_end_round_trip_keeps_revision_and_count() {
        let payload = PacketPayload::WorldSyncEnd {
            world_revision: 9,
            chunk_count: 137,
        };
        let header = PacketHeader::new(payload.type_code(), 1, 401, 10000);
        assert_eq!(header.packet_type, 0x37, "opcode de WorldSyncEnd (ADR-060)");
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::WorldSyncEnd {
                world_revision,
                chunk_count,
            } => {
                assert_eq!(world_revision, 9);
                assert_eq!(chunk_count, 137);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn interact_request_round_trip_includes_stable_ids() {
        let payload = PacketPayload::Interact {
            requester_id: 7,
            request_id: 7001,
            target_id: 42,
            target_kind: "item".into(),
            interaction_type: "pickup".into(),
            player_position: [1.0, 2.0, 3.0],
        };
        let header = PacketHeader::new(payload.type_code(), 7, 301, 10000);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::Interact {
                requester_id,
                request_id,
                target_id,
                target_kind,
                interaction_type,
                player_position,
            } => {
                assert_eq!(requester_id, 7);
                assert_eq!(request_id, 7001);
                assert_eq!(target_id, 42);
                assert_eq!(target_kind, "item");
                assert_eq!(interaction_type, "pickup");
                assert_eq!(player_position, [1.0, 2.0, 3.0]);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn ack_round_trip() {
        let payload = PacketPayload::Ack { acked_sequence: 42 };
        let header = PacketHeader::new(payload.type_code(), 2, 0, 100);
        let data = encode_packet(&header, &payload);
        let (_, p2) = decode_packet(&data).unwrap();
        match p2 {
            PacketPayload::Ack { acked_sequence } => assert_eq!(acked_sequence, 42),
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn type_code_matches_packet_type() {
        let p = PacketPayload::Handshake {
            player_name: "x".into(),
            version: "0.1.0".into(),
        };
        assert_eq!(p.type_code(), PacketType::Handshake as u16);

        let p = PacketPayload::Ack { acked_sequence: 0 };
        assert_eq!(p.type_code(), PacketType::Ack as u16);
    }

    // ADR-028 Fase E: all four corpse-relay payloads must round-trip, including negative
    // raw STP item ids (DataIdReference hashes) in every id-bearing field.
    #[test]
    fn corpse_payloads_round_trip() {
        use crate::utils::Vec3;
        use crate::world::corpse::{CorpseData, CorpseStack};

        let corpse = CorpseData {
            id: 7,
            owner_id: 1004,
            owner_name: "Joel".into(),
            position: Vec3::new(-22.3, 1.8, 9.7),
            equipment: [0, -2328174, -2864101, -3870361],
            held_item: -1159981804,
            items: vec![CorpseStack {
                item_id: -12345,
                quantity: 3,
                // ADR-072: valor NO default a propósito — la regla del round-trip (la misma de la
                // pose relay): un campo nuevo se prueba cruzando el wire con un valor que el
                // default no puede enmascarar. El botín de un JOINER llega solo por este salto
                // P2P, así que si las props se perdieran aquí, el host las vería y el joiner no —
                // exactamente la clase de desync que no se nota hasta un playtest de dos.
                props: vec![crate::player::session::ItemPropertyValue {
                    id: -8792658,
                    value: 0.4237,
                }],
            }],
            // ADR-028 amendment (world chests): the flag must survive the P2P mirror hop.
            is_chest: true,
        };

        let list = PacketPayload::CorpseList {
            corpses: vec![corpse.clone()],
            generation: 0,
            page: 0,
            page_count: 1,
        };
        let header = PacketHeader::new(list.type_code(), 1, 1, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &list)).unwrap();
        match decoded {
            PacketPayload::CorpseList { corpses, .. } => {
                assert_eq!(corpses.len(), 1);
                assert_eq!(corpses[0].id, 7);
                assert_eq!(corpses[0].owner_name, "Joel");
                assert_eq!(corpses[0].held_item, -1159981804);
                assert_eq!(corpses[0].items[0].item_id, -12345);
                // ADR-072: el desgaste sobrevive al salto P2P con su valor exacto.
                assert_eq!(corpses[0].items[0].props.len(), 1);
                assert_eq!(corpses[0].items[0].props[0].id, -8792658);
                assert!((corpses[0].items[0].props[0].value - 0.4237).abs() < 1e-9);
                assert!(corpses[0].is_chest);
            }
            _ => panic!("wrong variant"),
        }

        let spawn = PacketPayload::CorpseSpawnRequest {
            request_id: 42,
            requester_id: 1004,
            owner_name: "Joel".into(),
            position: [-22.3, 1.8, 9.7],
            equipment: [0, -2328174, -2864101, -3870361],
            held_item: -1159981804,
            items: vec![CorpseStack {
                item_id: 99,
                quantity: 1,
                // ADR-072: este salto es el JOINER que muere reportando su botín al host. Si las
                // props se perdieran aquí, el cadáver de un joiner tendría items a estreno aunque
                // su propio cliente las hubiera mandado bien por IPC.
                props: vec![crate::player::session::ItemPropertyValue {
                    id: -8792658,
                    value: 0.87,
                }],
            }],
        };
        let header = PacketHeader::new(spawn.type_code(), 1004, 2, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &spawn)).unwrap();
        match decoded {
            PacketPayload::CorpseSpawnRequest {
                request_id,
                requester_id,
                items,
                ..
            } => {
                assert_eq!(request_id, 42);
                assert_eq!(requester_id, 1004);
                assert_eq!(items[0].item_id, 99);
                assert_eq!(
                    items[0].props.len(),
                    1,
                    "ADR-072: el desgaste cruza el salto"
                );
                assert!((items[0].props[0].value - 0.87).abs() < 1e-9);
            }
            _ => panic!("wrong variant"),
        }

        let take = PacketPayload::CorpseTakeRequest {
            request_id: 43,
            requester_id: 1004,
            corpse_id: 7,
            item_index: 2,
            quantity: 1,
            requester_pos: [-21.0, 1.8, 9.0],
        };
        let header = PacketHeader::new(take.type_code(), 1004, 3, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &take)).unwrap();
        match decoded {
            PacketPayload::CorpseTakeRequest {
                request_id,
                corpse_id,
                item_index,
                ..
            } => {
                assert_eq!(request_id, 43);
                assert_eq!(corpse_id, 7);
                assert_eq!(item_index, 2);
            }
            _ => panic!("wrong variant"),
        }

        let result = PacketPayload::CorpseTakeResult {
            request_id: 43,
            accepted: false,
            corpse_id: 7,
            item_index: 2,
            item_id: -12345,
            quantity: 0,
            corpse_empty: false,
            reason: "too_far distance=9.31".into(),
        };
        let header = PacketHeader::new(result.type_code(), 1, 4, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &result)).unwrap();
        match decoded {
            PacketPayload::CorpseTakeResult {
                accepted,
                item_id,
                reason,
                ..
            } => {
                assert!(!accepted);
                assert_eq!(item_id, -12345);
                assert!(reason.starts_with("too_far"));
            }
            _ => panic!("wrong variant"),
        }
    }

    // ADR-029 V0: the three new PvP payloads must round-trip, including negative raw STP
    // weapon ids and the Option<T> debug-only fields (both Some and omitted-via-default).
    #[test]
    fn pvp_hit_candidate_round_trip_with_optional_fields() {
        let payload = PacketPayload::PvpHitCandidate {
            request_id: 501,
            attacker_id: 1004,
            victim_id: 1,
            weapon_id: -2328174,
            damage: 22.5,
            origin: [1.0, 1.8, 2.0],
            direction: [0.0, 0.0, 1.0],
            client_tick: Some(4200),
            hit_position: Some([1.0, 1.8, 5.0]),
        };
        let header = PacketHeader::new(payload.type_code(), 1004, 1, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &payload)).unwrap();
        match decoded {
            PacketPayload::PvpHitCandidate {
                request_id,
                attacker_id,
                victim_id,
                weapon_id,
                damage,
                client_tick,
                hit_position,
                ..
            } => {
                assert_eq!(request_id, 501);
                assert_eq!(attacker_id, 1004);
                assert_eq!(victim_id, 1);
                assert_eq!(weapon_id, -2328174);
                assert_eq!(damage, 22.5);
                assert_eq!(client_tick, Some(4200));
                assert_eq!(hit_position, Some([1.0, 1.8, 5.0]));
            }
            _ => panic!("wrong variant"),
        }

        // A decoder that never set the optional debug fields (older payload shape) must
        // decode them as None via serde(default), not error out. Externally-tagged enum
        // wire shape: {"pvp_hit_candidate": {fields...}} (rename_all = "snake_case").
        let bytes = rmp_serde::to_vec_named(&serde_json::json!({
            "pvp_hit_candidate": {
                "request_id": 502u64,
                "attacker_id": 1u32,
                "victim_id": 1004u32,
                "weapon_id": 1001i32,
                "damage": 10.0f32,
                "origin": [0.0f32, 1.8, 0.0],
                "direction": [0.0f32, 0.0, 1.0],
            }
        }))
        .unwrap();
        let decoded: PacketPayload = rmp_serde::from_slice(&bytes).unwrap();
        match decoded {
            PacketPayload::PvpHitCandidate {
                client_tick,
                hit_position,
                ..
            } => {
                assert_eq!(client_tick, None);
                assert_eq!(hit_position, None);
            }
            _ => panic!("wrong variant"),
        }
    }

    /// ADR-047. Every field carries a DISTINCT non-default value on purpose: a round-trip of
    /// zeros passes just as happily with two fields swapped.
    #[test]
    fn phantom_attack_grant_round_trip() {
        let payload = PacketPayload::PhantomAttackGrant {
            request_id: 77,
            victim_id: 1004,
            kind: 2,
            damage: 35.0,
            impulse: [2.5, -1.5],
        };
        let header = PacketHeader::new(payload.type_code(), 1, 9, 100);
        assert_eq!(
            payload.type_code(),
            0x4D,
            "the opcode is part of the contract"
        );
        let (_, decoded) = decode_packet(&encode_packet(&header, &payload)).unwrap();
        match decoded {
            PacketPayload::PhantomAttackGrant {
                request_id,
                victim_id,
                kind,
                damage,
                impulse,
            } => {
                assert_eq!(request_id, 77);
                assert_eq!(victim_id, 1004);
                assert_eq!(kind, 2);
                assert_eq!(damage, 35.0);
                assert_eq!(impulse, [2.5, -1.5]);
            }
            _ => panic!("wrong variant"),
        }
    }

    /// ADR-076. `kind = 5` (Knockdown) specifically: the whole point of the bump was that this
    /// value used to fall through to the joiner's `_` arm and be misread as damage, so it earns
    /// its own pinned round-trip rather than riding the generic one above.
    #[test]
    fn phantom_attack_grant_round_trip_knockdown() {
        let payload = PacketPayload::PhantomAttackGrant {
            request_id: 909,
            victim_id: 42,
            kind: 5,
            damage: 2.0, // stun seconds, per the doc comment — not health damage
            impulse: [6.0, -3.0],
        };
        let header = PacketHeader::new(payload.type_code(), 1, 9, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &payload)).unwrap();
        match decoded {
            PacketPayload::PhantomAttackGrant {
                request_id,
                victim_id,
                kind,
                damage,
                impulse,
            } => {
                assert_eq!(request_id, 909);
                assert_eq!(victim_id, 42);
                assert_eq!(kind, 5);
                assert_eq!(damage, 2.0);
                assert_eq!(impulse, [6.0, -3.0]);
            }
            _ => panic!("wrong variant"),
        }
    }

    /// ADR-047. Asymmetric position on purpose (see above).
    #[test]
    fn noise_report_round_trip() {
        let payload = PacketPayload::NoiseReport {
            position: [12.5, 1.8, -404.25],
            loudness: 500.0,
        };
        let header = PacketHeader::new(payload.type_code(), 1, 9, 100);
        assert_eq!(
            payload.type_code(),
            0x4E,
            "the opcode is part of the contract"
        );
        let (_, decoded) = decode_packet(&encode_packet(&header, &payload)).unwrap();
        match decoded {
            PacketPayload::NoiseReport { position, loudness } => {
                assert_eq!(position, [12.5, 1.8, -404.25]);
                assert_eq!(loudness, 500.0);
            }
            _ => panic!("wrong variant"),
        }
    }

    /// ADR-047 dejó 0x50 libre reservándolo para ADR-046, que ya lo ha COBRADO. El centinela
    /// cambia de lado sin perder su trabajo: antes fijaba "que nadie lo ocupe", ahora fija "lo
    /// ocupa la voz y solo la voz". Si un paquete de gameplay futuro se lo quedara, dos
    /// funcionalidades decodificarían los bytes de la otra.
    #[test]
    fn the_voice_opcode_belongs_to_voice_and_to_nothing_else() {
        assert_eq!(
            PacketType::from_u16(0x50),
            Some(PacketType::VoiceFrame),
            "0x50 es VoiceFrame (ADR-046)"
        );
        assert_eq!(
            PacketPayload::VoiceFrame {
                seq: 0,
                data: Vec::new()
            }
            .type_code(),
            0x50,
            "el payload y el opcode no pueden discrepar"
        );
        // ADR-050 reclama 0x4F, el hueco en el que ADR-047 se detuvo a proposito. Lo que este test
        // protege sigue siendo lo mismo: que 0x4F y 0x50 son cosas DISTINTAS y ninguna invade a la
        // otra. Que 0x4F dejara de estar libre siempre fue el final previsto de esa reserva.
        assert_eq!(
            PacketType::from_u16(0x4F),
            Some(PacketType::StruggleReport),
            "0x4F es StruggleReport (ADR-050)"
        );
        assert_eq!(
            PacketPayload::StruggleReport.type_code(),
            0x4F,
            "el payload y el opcode no pueden discrepar"
        );
    }

    /// ADR-046 — el audio viaja como BIN de msgpack, no como array de enteros. La diferencia no
    /// es cosmética: cada byte ≥ 128 costaría dos en un array, así que ~1,5× el ancho de banda de
    /// todo el sistema de voz. Y hay un byte de 0xFF en la muestra justamente para que un
    /// serializador que se pase a array falle aquí y no en producción.
    #[test]
    fn voice_frame_round_trips_as_binary() {
        let audio: Vec<u8> = (0..120u16).map(|i| (i * 31 + 7) as u8).collect();
        let payload = PacketPayload::VoiceFrame {
            seq: 65535,
            data: audio.clone(),
        };
        let header = PacketHeader::new(payload.type_code(), 1, 0, 100);
        let wire = encode_packet(&header, &payload);
        let (_, decoded) = decode_packet(&wire).unwrap();
        match decoded {
            PacketPayload::VoiceFrame { seq, data } => {
                assert_eq!(seq, 65535, "el seq debe llegar entero al borde del u16");
                assert_eq!(data, audio, "el audio debe sobrevivir byte a byte");
            }
            _ => panic!("wrong variant"),
        }
    }

    /// Una trama de voz no puede ser fiable. ADR-039 lo dejó medido: al superar `MAX_RETRIES` el
    /// barrido hace `reliable_queue.clear()` y vacía la cola ENTERA del peer, llevándose pickups,
    /// cadáveres y veredictos de PvP. Y un paquete de voz reenviado llega después del momento al
    /// que pertenecía, así que la retransmisión no compra nada a cambio.
    #[test]
    fn voice_never_enters_the_reliable_queue() {
        assert!(
            !crate::network::reliability::is_reliable(0x50),
            "VoiceFrame jamas debe ser fiable (ADR-039/ADR-046)"
        );
    }

    #[test]
    fn pvp_damage_grant_round_trip() {
        let payload = PacketPayload::PvpDamageGrant {
            request_id: 501,
            attacker_id: 1004,
            victim_id: 1,
            weapon_id: -2328174,
            damage: 18.0,
            reason: "validated".into(),
        };
        let header = PacketHeader::new(payload.type_code(), 1, 9, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &payload)).unwrap();
        match decoded {
            PacketPayload::PvpDamageGrant {
                request_id,
                damage,
                reason,
                ..
            } => {
                assert_eq!(request_id, 501);
                assert_eq!(damage, 18.0);
                assert_eq!(reason, "validated");
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn pvp_hit_rejected_round_trip() {
        let payload = PacketPayload::PvpHitRejected {
            request_id: 501,
            attacker_id: 1004,
            victim_id: 1,
            reason: "too_far".into(),
        };
        let header = PacketHeader::new(payload.type_code(), 1, 10, 100);
        let (_, decoded) = decode_packet(&encode_packet(&header, &payload)).unwrap();
        match decoded {
            PacketPayload::PvpHitRejected {
                request_id, reason, ..
            } => {
                assert_eq!(request_id, 501);
                assert_eq!(reason, "too_far");
            }
            _ => panic!("wrong variant"),
        }
    }
}
