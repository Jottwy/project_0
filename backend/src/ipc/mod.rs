//! Local IPC between the Unity client and this Rust backend.
//!
//! Transport: TCP on 127.0.0.1:7777.
//! Framing:   4-byte big-endian length prefix, then a MessagePack body.
//! Encoding:  MessagePack via `rmp_serde::to_vec_named` (maps keyed by field
//!            name), so the C# side (MessagePack-CSharp, keyAsPropertyName) and
//!            the internally-tagged enums below line up.
//!
//! This module defines the wire schema; the server lives in `ipc::server`.
//! Schema mirrors CLAUDE_CODE_INSTRUCTIONS.md "MessagePack Schema".

pub mod server;

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::world::chunk::InterLayerVolumeV0;
use crate::world::graph::verticality::VerticalDebugMarkerV0;
use crate::world::grid_gen::RoomZone;
use crate::world::volumetric_grid::VolumetricGridViewV0;

pub const DEFAULT_IPC_ADDR: &str = "127.0.0.1:7777";

pub fn resolve_ipc_addr() -> String {
    if let Ok(addr) = std::env::var("IPC_ADDR") {
        if !addr.trim().is_empty() {
            return addr;
        }
    }

    if let Ok(port) = std::env::var("IPC_PORT") {
        if !port.trim().is_empty() {
            return format!("127.0.0.1:{}", port.trim());
        }
    }

    DEFAULT_IPC_ADDR.to_string()
}

// ───────────────────────── Unity → Rust ─────────────────────────

/// Anything the Unity client can send to the backend.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ClientMessage {
    /// Per-frame movement / look / queued actions.
    Input(PlayerInput),
    /// A discrete gameplay action (craft, pickup, attack, …).
    Action(PlayerAction),
    /// UI lifecycle events (pause, save, quit, …).
    UiEvent(UiEvent),
    /// Fase 4.1 — ask the backend to generate one chunk via `grid_gen` and return
    /// it as a 5 m tile-wall bitmask (see `ServerMessage::ChunkData`). Independent
    /// of the legacy `world/` ChunkView path.
    RequestChunk { cx: i32, cz: i32, layer: u8 },
    /// ADR-095 — pide el chunk de WorldGen3: la LISTA DE PIEZAS colocadas, no geometría.
    ///
    /// Variante propia y no un campo en `RequestChunk` (regla R4): WG2 y WG3 conviven hasta el
    /// borrado, y mezclarlos en un mensaje obligaría a que el día del borrado hubiera que tocar el
    /// camino que se queda. Sin `layer`, y no es un olvido: con columnas de tramos la capa deja de
    /// existir como restricción de geometría (ADR-095 D2), así que un chunk de WG3 es uno solo y
    /// cubre toda la altura.
    RequestWg3Chunk { cx: i32, cz: i32 },
    /// ADR-046 — one encoded voice frame from the LOCAL player's microphone.
    ///
    /// A top-level variant rather than a `PlayerAction`, for the same reason as
    /// `RequestChunk`: actions carry a `action_type` string plus a nested map, and this
    /// travels 25 times a second while someone is speaking. `data` is opaque here — the
    /// backend never decodes audio, it only forwards bytes.
    Voice {
        seq: u16,
        /// `serde_bytes` is REQUIRED, not decoration: a bare `Vec<u8>` deserializes only from a
        /// msgpack array and rejects the bin the client writes, with
        /// `invalid type: byte array, expected a sequence`.
        #[serde(default, with = "serde_bytes")]
        data: Vec<u8>,
    },
    /// ADR-068 — the local player painted a spray on a wall.
    ///
    /// A top-level variant and NOT a `PlayerAction`, for a hard technical reason on top of the
    /// one `Voice` gives: a `PlayerAction` carries its payload in a `serde_json::Value`, and
    /// `Value` has no byte-string type — the `bin` blob holding the stroke points would fail to
    /// decode into it. Every other placement in this project (`stp_place`, `stp_drop`, …) is an
    /// action precisely because its payload is all numbers.
    SprayPlace(SprayPlaceRequest),
    /// ADR-078 — el jugador local está pintando AHORA MISMO y esto es el trozo nuevo del trazo.
    /// Variante propia y no `PlayerAction` por lo mismo que `SprayPlace`: lleva un blob binario.
    SprayDraft(SprayDraftRequest),
}

/// ADR-078 — un trozo de trazo en vivo, del cliente a su propio backend, que lo reparte.
///
/// NADA de esto se valida ni se guarda: es presentación efímera y la autoridad sigue siendo
/// `SprayPlace`. Lo único que el backend decide es a QUIÉN se lo manda (por distancia) — ver
/// `network::sync::spray_draft_destinations`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SprayDraftRequest {
    /// El mismo id que llevará la pintada definitiva: es lo que empareja borrador y pintada
    /// para que el receptor retire uno al llegar la otra.
    pub place_id: u64,
    pub layer: u8,
    /// Ancla en coordenadas de MUNDO (ADR-078 decisión 4), no chunk-local.
    pub anchor: [f32; 3],
    pub yaw: f32,
    pub color: u8,
    pub width: f32,
    pub first_index: u16,
    /// Pares (u, v) en milímetros sobre el plano del ancla, `i16` little-endian en un blob:
    /// 4 bytes por punto, el mismo formato que los puntos de un trazo en ADR-068.
    ///
    /// `serde_bytes` NO es decoración: un `Vec<u8>` pelado solo deserializa desde un ARRAY de
    /// msgpack y RECHAZA el `bin` que escribe el cliente, con `invalid type: byte array,
    /// expected a sequence`. Está escrito en `ClientMessage::Voice` y en `SprayStroke::points`,
    /// y se vuelve a pagar cada vez que se olvida.
    #[serde(with = "serde_bytes")]
    pub points_mm: Vec<u8>,
}

/// ADR-068 — what the client asks the host to paint. The host is the authority: it validates
/// every cap (`world::spray::Spray::validate_from`) before minting an id, so nothing here is
/// trusted as sent.
///
/// Coordinates arrive in WORLD space and the host converts them to chunk-local before storing
/// (ADR-068 decision 3). The client does not compute the chunk — deriving it in one place keeps
/// a client that rounds differently from anchoring a spray to the wrong chunk.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SprayPlaceRequest {
    /// Client-generated dedup key, same role as `stp_place`'s `place_id`: a reliable
    /// retransmit must paint exactly one spray. `0` = no dedup.
    #[serde(default)]
    pub place_id: u64,
    pub layer: u8,
    pub world_pos: [f32; 3],
    pub yaw: f32,
    pub size: [f32; 2],
    pub strokes: Vec<crate::world::spray::SprayStroke>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct PlayerInput {
    pub movement: [f32; 3], // normalized world-space direction (legacy path)
    pub look_delta: [f32; 2],
    pub sprint: bool,
    #[serde(default)]
    pub actions: Vec<String>,

    // ADR-009 client-prediction fields. Optional (serde default) so a legacy
    // movement-direction client still decodes; the STP client sends an
    // authoritative pose for server validation (Option B). When `input_seq != 0`
    // the game loop takes the prediction path instead of integrating `movement`.
    #[serde(default)]
    pub input_seq: u32,
    #[serde(default)]
    pub client_tick: u32,
    #[serde(default)]
    pub position: [f32; 3],
    #[serde(default)]
    pub velocity: [f32; 3],
    #[serde(default)]
    pub move_state: u8, // 0 idle, 1 walk, 2 run, 3 crouch, 4 jump
    #[serde(default)]
    pub look: [f32; 2], // pitch, yaw — INPUT, not server-corrected (ADR-009 §8)
    #[serde(default)]
    pub buttons: u16,
    /// ADR-020: client-reported crouch (cosmetic; relayed to peers, not authoritative).
    #[serde(default)]
    pub crouch: bool,
    /// ADR-022: client-reported worn clothing item IDs [Head, Torso, Legs, Feet] (0 = empty);
    /// cosmetic, relayed to peers, not authoritative.
    #[serde(default)]
    pub equipment: [i32; 4],
    /// ADR-023: client-reported held item ID (0 = empty hands); cosmetic, relayed to peers,
    /// not authoritative.
    #[serde(default)]
    pub held_item: i32,
    /// ADR-024: client-reported hit-reaction counter (monotonic, wrapping); incremented on each
    /// local DamageReceived. Cosmetic, relayed to peers, not authoritative.
    #[serde(default)]
    pub hit_seq: u8,
    /// ADR-042: client-reported "my active wieldable is emitting light" — any enabled `Light`
    /// under it. Cosmetic, relayed to peers, not authoritative.
    #[serde(default)]
    pub light_on: bool,
    /// ADR-042: client-reported shot counter (monotonic, wrapping); incremented on each native
    /// `IFirearmTrigger.Shoot`. Cosmetic, relayed to peers, not authoritative — the phantom hears
    /// through the separate `report_noise` action (ADR-041), never through this.
    #[serde(default)]
    pub fire_seq: u8,
    /// ADR-044: client-reported melee-swing counter (monotonic, wrapping). Sampled as a RISING EDGE
    /// of `MeleeWeapon.IsUsing` — that class exposes no swing event, unlike `IFirearmTrigger.Shoot`.
    /// Cosmetic, relayed to peers, not authoritative: it does not feed the hit validation of ADR-029.
    #[serde(default)]
    pub melee_seq: u8,
    /// ADR-049: client-reported carry state — `carry_def` is the `CarryableDefinition` id being
    /// hauled (0 = empty hands), `carry_count` how many units. A LEVEL, not a counter. Client-origin
    /// on purpose: `process_stp_carryable_pickup` keeps no per-player carry state to derive it from,
    /// and the field concedes nothing — no material, no placement, no collision.
    #[serde(default)]
    pub carry_def: i32,
    #[serde(default)]
    pub carry_count: u8,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PlayerAction {
    pub action_type: String,
    #[serde(default)]
    pub data: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UiEvent {
    pub event_type: String,
}

// ───────────────────────── Rust → Unity ─────────────────────────

/// Anything the backend can push to the Unity client.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ServerMessage {
    /// Full renderable snapshot (stats/chunks/entities), sent at the slow cadence.
    WorldState(WorldState),
    /// ADR-009 §2: 20 Hz movement-domain delta — authoritative pose + input ack,
    /// consumed by the client reconciler. Separate from the full WorldState.
    DeltaUpdate(MovementDelta),
    /// Immediate one-off event (chunk_teleported, entity_killed, …).
    Event(GameEvent),
    /// Result of a requested action.
    ActionResult(ActionResult),
    /// Fase 4.1 — minimal grid_gen chunk payload (reply to `RequestChunk`).
    ChunkData(GridChunkData),
    /// ADR-046 — one encoded voice frame from a REMOTE peer, already filtered by
    /// distance at the host. Travels on its own broadcast channel, never on the one
    /// carrying world state (see `ipc::server::run`).
    PeerVoice(PeerVoice),
    /// ADR-061 — first frame of every IPC connection, before any world state. Never goes
    /// through a broadcast channel: `handle_connection` writes it straight to the socket.
    Hello(ServerHello),
    /// ADR-068 — a spray the host ACCEPTED, with its minted id and tick. Sent live so the
    /// painter sees the authoritative version (and every other client sees it appear) without
    /// waiting to reload the chunk. The bulk hydration path is `GridChunkData::sprays`.
    SprayPlaced(crate::world::spray::Spray),
    /// ADR-078 — trozo de un trazo que OTRO jugador está pintando ahora. Efímero: el cliente lo
    /// dibuja como previa y lo tira al llegar el `SprayPlaced` con el mismo `place_id`.
    SprayDraft(SprayDraftView),
    /// ADR-095 — el chunk de WorldGen3: qué piezas hay puestas y dónde. Respuesta a
    /// `RequestWg3Chunk`.
    Wg3Chunk(Wg3ChunkView),
}

/// ADR-095 — una pieza colocada, tal y como viaja.
///
/// ONCE BYTES. Es la propiedad que hace barato el paradigma entero: el catálogo ya está en el build
/// de las dos partes, así que por el cable solo va QUÉ pieza, girada CÓMO y puesta DÓNDE. Mandar la
/// geometría —o la chuleta rasterizada— sería mandar por sesión y por chunk algo que el cliente ya
/// tiene en disco.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Wg3PlacementWire {
    /// Índice en el manifiesto. La cadena `id` NO viaja: identificar por nombre costaría bytes por
    /// chunk para nombrar algo que las dos partes ya tienen indexado igual.
    pub piece: u16,
    /// Cuartos de vuelta, horario visto desde +Y. 0..=3.
    pub rotation: u8,
    /// Esquina mínima de la huella girada, en CENTÍMETROS ENTEROS.
    ///
    /// Enteros y no `f32` por la misma razón que en `wg3::placement`: esto se compara entre dos
    /// procesos y tiene que coincidir bit a bit. Un flotante acumulado a lo largo de una cadena de
    /// piezas no lo garantiza, y la divergencia saldría como una pared medio metro corrida en un
    /// solo cliente.
    pub origin_x_cm: i32,
    pub origin_z_cm: i32,

    /// ADR-097 — cota del SUELO de la pieza, en centímetros. Sin esto toda pieza va a cero y la
    /// verticalidad solo existe DENTRO de una: es el agujero que fundó WG3, heredado en otra forma.
    pub origin_y_cm: i32,
}

/// ADR-098 — una boca de un tramo generado. Misma parametrización que la de una pieza: lado más
/// offset recorriendo el perímetro en horario desde `(0, D)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Wg3OpeningWire {
    pub side: u8,
    pub offset_cm: i32,
    pub width_cm: i32,
}

/// ADR-098 — un TRAMO generado: geometría que el servidor sintetiza donde el catálogo no puede
/// encajar una pieza.
///
/// **Es lo único que no es un índice de catálogo**, y por eso viaja entero. Un conector no se elige:
/// se genera con la longitud, los quiebros y el ancho que hagan falta, así que las dos partes no
/// pueden tenerlo horneado de antemano. Lo que viaja son los NÚMEROS —rectángulo, cota, altura y
/// bocas—; la geometría la deriva cada lado con la misma regla, y de que no se desvíen responde el
/// oráculo de conectores.
///
/// No confundir con la celda del ráster (`WG3_CELL_M`, 0,5 m) ni con la celda de rejilla de WG2:
/// esto es una pieza rectangular que nadie dibujó.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Wg3SegmentWire {
    /// Esquina mínima de la huella, en centímetros de mundo.
    pub x_cm: i32,
    pub z_cm: i32,
    pub size_x_cm: i32,
    pub size_z_cm: i32,
    /// Cota del SUELO (ADR-097), en las mismas unidades que la de una colocación.
    pub floor_y_cm: i32,
    /// Altura LIBRE, de suelo a techo.
    pub height_cm: i32,
    /// Aspecto. El servidor no lo interpreta: es el gancho para que el cliente vista los conectores.
    pub style: u8,
    pub openings: Vec<Wg3OpeningWire>,
}

/// ADR-101 — un VANO EXCAVADO: materia que se le quita a la geometría ya construida.
///
/// **Viaja la CAJA, no «qué pared de qué pieza».** Describirlo como «la pieza 12, lado norte, offset
/// 340» obligaría al cliente a resolver quién es la dueña —que puede estar en otro chunk— y a
/// reconstruir el mapeo de lados por rotación, que sería la tercera copia de un mapeo que ya se ha
/// desviado antes. La caja es exactamente el dato que el ráster del servidor ya consume
/// (`Wg3RasterBuilder::carve_box`), así que las dos partes hacen la misma operación sobre el mismo
/// número y no hay nada que derivar dos veces.
///
/// Existe porque el plan decide dónde va cada puerta y una pieza del catálogo trae sus bocas donde
/// las puso quien la dibujó: sin excavar, una pieza colocada en un espacio planificado nace SELLADA.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Wg3CarveWire {
    pub x_cm: i32,
    pub z_cm: i32,
    pub size_x_cm: i32,
    pub size_z_cm: i32,
    /// Banda vertical que se abre. **NO llega al suelo**: sin esa guarda el vano se lleva la losa
    /// sobre la que se anda y abre un agujero por el que se cae en vez de una puerta.
    pub bottom_y_cm: i32,
    pub top_y_cm: i32,
}

/// ADR-095 — lo que WG3 entrega por chunk.
///
/// Sin `layer`: con columnas de tramos (D2) la capa deja de existir como restricción, así que un
/// chunk cubre toda la altura. Es la simplificación que se cobra D2 sola, y también es lo que hace
/// que este mensaje no pueda mezclarse con `GridChunkData` aunque se pareciesen.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Wg3ChunkView {
    pub cx: i32,
    pub cz: i32,
    /// Vacío es un resultado VÁLIDO y frecuente: un chunk donde no cae ninguna pieza. El cliente
    /// tiene que saber distinguirlo de "todavía no ha llegado", que es lo que distingue un mundo
    /// con huecos de un mundo a medio cargar.
    #[serde(default)]
    pub placements: Vec<Wg3PlacementWire>,

    /// ADR-098 — los tramos generados de los que este chunk es dueño. Se omite vacío, que es el caso
    /// de la inmensa mayoría de chunks: un conector cruza el mundo de vez en cuando, no siempre.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub segments: Vec<Wg3SegmentWire>,

    /// ADR-101 — los vanos excavados que TOCAN este chunk.
    ///
    /// **Tocan, no pertenecen**, y es la única lista de este mensaje que no sigue la regla del
    /// centro: un vano se abre justo en la frontera entre dos piezas, y ésa es exactamente la clase
    /// de sitio donde cae también una frontera de chunk. Perderlo por un centímetro dejaría la
    /// puerta abierta por un lado y tapiada por el otro.
    ///
    /// Que uno viaje dos veces es correcto y barato: restar dos veces la misma caja da lo mismo que
    /// restarla una. La idempotencia es otra razón para que viaje la caja y no una referencia.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub carves: Vec<Wg3CarveWire>,
}

/// ADR-078 — lo que el backend entrega a Unity por cada trozo de trazo ajeno. Es
/// `SprayDraftRequest` más de quién es: sin el `painter` el cliente no podría separar dos
/// trazos simultáneos que compartieran `place_id` por casualidad.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SprayDraftView {
    pub painter: u16,
    pub place_id: u64,
    pub layer: u8,
    pub anchor: [f32; 3],
    pub yaw: f32,
    pub color: u8,
    pub width: f32,
    pub first_index: u16,
    #[serde(with = "serde_bytes")]
    pub points_mm: Vec<u8>,
}

/// ADR-061 — the schema revision this backend speaks, so Unity can refuse a desynced build
/// instead of decoding it into silent defaults (a failed `remote_players` parse is otherwise
/// indistinguishable from "no remote players", STABILITY_AUDIT_CURRENT.md R4).
///
/// Just the number: a build string would duplicate what the startup log already prints and
/// invite logic over strings. The client skips unknown keys, so adding fields here later stays
/// additive.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ServerHello {
    pub schema_version: u32,

    /// ADR-095 D3 — si este backend sirve mundo de WorldGen3.
    ///
    /// La bandera viaja en el saludo y no se deduce: el cliente tiene que saber A QUÉ MUNDO se ha
    /// conectado antes de pedir un solo chunk, o pediría por el camino equivocado y se quedaría
    /// esperando una respuesta que nadie va a mandar.
    ///
    /// `skip_serializing_if` con WG3 apagado, que es el estado de toda sesión de hoy: así el saludo
    /// sale BYTE A BYTE igual que en v45. El lector de C# ya salta claves desconocidas, así que no
    /// hacía falta para que la puerta de ADR-061 aguante — pero esa puerta es lo único que informa
    /// de un desajuste de versión, y no se le añade ni un byte de superficie por una bandera que
    /// hoy está apagada en todas partes.
    #[serde(default, skip_serializing_if = "is_false")]
    pub wg3_enabled: bool,

    /// ADR-095 — digest del manifiesto que este backend cargó, o vacío si no hay.
    ///
    /// **Es lo que impide el fallo silencioso más caro del sistema.** Cliente y servidor hornean el
    /// catálogo por separado; si el del cliente no es el del servidor, la geometría que se dibuja y
    /// la que bloquea son de mundos distintos, nada da error, y el síntoma es atravesar paredes que
    /// se ven. Comparar dos cadenas en el saludo lo convierte en un rechazo con motivo.
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub wg3_manifest_digest: String,
}

/// Para `skip_serializing_if` sobre un `bool`. Existe porque serde entrega `&bool` y el idioma con
/// `std::ops::Not::not` ahí se lee peor de lo que ahorra.
fn is_false(b: &bool) -> bool {
    !*b
}

/// ADR-046 — a voice frame on its way to the local Unity client. `peer_id` is the
/// speaker, so the client can attach the audio to that peer's proxy; `seq` is what
/// lets the receiver detect loss and reorder (the transport is deliberately
/// unreliable). `data` is opaque: the backend never decodes audio.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerVoice {
    pub peer_id: u16,
    pub seq: u16,
    /// See `ClientMessage::Voice::data` — same adapter, same reason, and here it also decides
    /// what goes OUT: without it this would serialize as an array of integers, ~1.5× the bytes
    /// and undecodable by the client's `ReadBin`.
    #[serde(default, with = "serde_bytes")]
    pub data: Vec<u8>,
}

/// Fase 4.1 — minimal backend-authoritative chunk: a 10×10 grid of 5 m tiles,
/// each a wall bitmask. Derived from grid_gen's 20×20 grid of 2.5 m cells by
/// `crate::world::grid_gen::chunk_tile_walls`. This is the NEW clean world path;
/// it shares nothing with the legacy `world/` `ChunkView`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GridChunkData {
    pub cx: i32,
    pub cz: i32,
    pub layer: u8,
    /// `walls[x][z]`: per-tile bitmask. Low nibble (`0x0F`) = edge walls: N=1
    /// (−Z), S=2 (+Z), E=4 (+X), W=8 (−X). High nibble (`0x10..0x80`, ADR-033/
    /// Pillar enmienda "Opción (c)") = which of the tile's four 2.5 m sub-cells
    /// is `CellType::Pillar`: `0x10` NW, `0x20` NE, `0x40` SW, `0x80` SE. The
    /// Unity consumer MUST use this same axis convention and bit mapping or Z
    /// will mirror / pillars will render in the wrong sub-cell.
    pub walls: [[u8; 10]; 10],
    /// ADR-034 — rects de Fase 4 con su `RoomType`, en coordenadas de CELDA
    /// (2.5 m), NO de tile. Campo ADITIVO: `walls` queda intacto (su byte está
    /// lleno y blindado por ADR-033/Pillar), así que esto viaja aparte en vez
    /// de robar bits. Omitido del wire cuando está vacío (`num_open_zones == 0`)
    /// — un cliente sin soporte simplemente no ve la clave, mismo patrón que
    /// `volumetric_grid`/`vertical_debug_markers`.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub room_zones: Vec<RoomZone>,
    /// ADR-068 — the sprays painted on this chunk, in RENDER ORDER (oldest first, so the
    /// newest paints over the rest). This is the bulk hydration path: sprays ride the chunk
    /// the client already asks for instead of a per-tick roster, because unlike the STP
    /// rosters a spray is ~1,9 KB and 64 of them per chunk at 10 Hz would be absurd.
    ///
    /// Unlike `walls`/`room_zones`, this is NOT derivable from the seed — it is player-made
    /// state the host owns. Omitted from the wire when empty, so a chunk nobody has painted
    /// costs exactly what it cost before this field existed.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub sprays: Vec<crate::world::spray::Spray>,
    /// ADR-081 enmienda 5 — la HABITACIÓN CONSTRUIBLE de este chunk, si la tiene:
    /// `[tile_x, tile_z, door_side]`, en TILES de 5 m dentro del chunk.
    ///
    /// Viaja por el wire y no se re-deriva en el cliente, que es lo que se hace con todo lo demás
    /// que sale del seed (los carteles, la escalera de OFFICE). Aquí no se puede: el emplazamiento
    /// sortea con `StdRng` —ChaCha— y eso no se replica en C# sin reimplementar el generador entero.
    /// Se eligió mandar 3 bytes antes que mantener dos generadores de números aleatorios en fase.
    ///
    /// Campo ADITIVO y omitido cuando el chunk no tiene sala, mismo patrón que `room_zones` y
    /// `sprays`: un chunk sin habitación cuesta exactamente lo que costaba antes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub build_room: Option<[u8; 3]>,
    /// ADR-083 enmienda 1 — las SALAS AUTORADAS de este chunk, cada una
    /// `[tile_x, tile_z, entry, quarter, anchor_cx, anchor_cz]`. `entry` es el índice
    /// en `RoomPool.rooms` (y en el manifiesto) que dice qué prefab instanciar;
    /// `quarter` el giro en cuartos de vuelta.
    ///
    /// **`tile_x`/`tile_z` van en tiles de 5 m relativos al CHUNK ANCLA, no a este**
    /// (wire 40 → 41, ADR-084 enmienda 1 punto 2). Una sala que cruza chunks llega en
    /// los cuatro que cubre y en todos con la MISMA pareja de números; el chunk local
    /// se saca restando. El ancla no está aquí por el tamaño: el cliente **necesita**
    /// saber de quién es la sala o los cuatro chunks instancian el prefab y salen
    /// cuatro salas superpuestas. Con las coordenadas del ancla, ese dato ES el
    /// identificador de deduplicación — un solo campo hace las dos cosas, y un
    /// desplazamiento con signo habría necesitado además un id aparte.
    ///
    /// PLURAL desde ADR-083 enmienda 3 (wire 38 → 39), que es la forma que el punto 2
    /// del ADR base pedía desde el principio: la enmienda 1 lo estrechó a una sola
    /// porque su slice colocaba una sola. El ORDEN es contrato — el cliente instancia
    /// por índice de esta lista.
    ///
    /// Viaja por el wire en vez de re-derivarse en el cliente, por el mismo motivo
    /// que `build_room`: el sorteo usa `StdRng` —ChaCha— y replicarlo en C# sería
    /// mantener dos generadores en fase. Antes de esto el cliente SÍ sorteaba, con
    /// su propio hash y emparejando footprints contra las zonas selladas; ese camino
    /// se retira aquí, y con él la posibilidad de que cliente y servidor discrepen
    /// sobre qué sala hay en un sitio.
    ///
    /// Campo ADITIVO y omitido cuando el chunk no tiene salas, mismo patrón que
    /// `room_zones`, `sprays` y `build_room`: un chunk sin sala cuesta exactamente lo
    /// que costaba en wire 37, y sigue costándolo tras el paso a plural.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub authored_rooms: Vec<[i32; 6]>,
}

/// ADR-009 §2 DeltaUpdate payload: the 20 Hz authoritative movement state the
/// client reconciler needs — position to detect desync, velocity to correct to
/// immediately (amended §5), and `ack_input_seq` to align with its input buffer.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MovementDelta {
    pub tick: u64,
    pub ack_input_seq: u32,
    pub position: [f32; 3],
    pub velocity: [f32; 3],
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldState {
    pub tick: u64,
    pub world_seed: u64,
    pub world_revision: u64,
    pub local_player: LocalPlayerState,
    pub remote_players: Vec<RemotePlayerState>,
    pub visible_chunks: Vec<ChunkView>,
    pub visible_entities: Vec<EntityView>,
    pub visible_items: Vec<ItemView>,
    /// Debug placeholders for the parallel verticality layer (Phase 6.6).
    /// Optional and omitted when empty so the wire stays backward compatible.
    /// Render-as-debug only: no collision, no traversal, no gameplay authority.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub vertical_debug_markers: Vec<VerticalDebugMarkerV0>,

    /// Phase 1 — host-authoritative STP world items, replicated to all peers.
    /// Omitted from the wire when empty (backward compatible).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub stp_items: Vec<crate::network::protocol::StpItemInfo>,

    /// Phase B1 — host-authoritative STP building pieces, replicated to all peers.
    /// Omitted from the wire when empty (backward compatible).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub stp_buildings: Vec<crate::network::protocol::StpBuildingInfo>,

    /// Phase B2.5 — host-authoritative STP world carryables, replicated to all peers.
    /// Omitted from the wire when empty (backward compatible).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub stp_carryables: Vec<crate::network::protocol::StpCarryableInfo>,

    /// Phase B2.6 — host-authoritative STP scene harvestables (health), replicated to peers.
    /// Omitted from the wire when empty (backward compatible).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub stp_harvestables: Vec<crate::network::protocol::StpHarvestableInfo>,

    /// ADR-028 — lootable corpses near the player (global storage in `World::corpses`,
    /// filtered by proximity for bandwidth only — the map itself is never pruned).
    /// Omitted from the wire when empty (backward compatible: a v7 client never sees it).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub visible_corpses: Vec<CorpseView>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LocalPlayerState {
    pub position: [f32; 3],
    pub rotation: f32,
    pub stats: StatsView,
    pub speed_modifier: f32,
    pub inventory_changed: bool,
    /// ADR-009: echo of the last client `input_seq` the server has applied, so
    /// the client reconciler can compare authoritative pose vs. its prediction.
    #[serde(default)]
    pub ack_input_seq: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StatsView {
    pub health: f32,
    pub hunger: f32,
    pub thirst: f32,
    pub sanity: f32,
    /// ADR-009: server-authoritative stamina, interpolated client-side at 5 Hz.
    pub stamina: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RemotePlayerState {
    pub id: u16,
    pub name: String,
    pub position: [f32; 3],
    pub rotation: f32,
    pub animation: String,
    /// ADR-020: cosmetic crouch state of this remote player (host-relayed).
    #[serde(default)]
    pub crouch: bool,
    /// ADR-021: cosmetic camera pitch in degrees (−90..90, quantized to 1°), host-relayed.
    #[serde(default)]
    pub pitch: i8,
    /// ADR-022: cosmetic worn clothing item IDs [Head, Torso, Legs, Feet] (0 = empty), host-relayed.
    #[serde(default)]
    pub equipment: [i32; 4],
    /// ADR-023: cosmetic held item ID (0 = empty hands), host-relayed.
    #[serde(default)]
    pub held_item: i32,
    /// ADR-024: cosmetic hit-reaction counter (monotonic, wrapping; 0 = never hit), host-relayed.
    #[serde(default)]
    pub hit_seq: u8,
    /// ADR-028 post-E3: cosmetic dead flag (server-derived on the owning backend) — the client
    /// hides this peer's standing proxy while true (its corpse is the visible body).
    #[serde(default)]
    pub dead: bool,
    /// ADR-038: cosmetic "showing its real form" flag — true only while the robapieles (ADR-016)
    /// is in `Sprint` or `Statue`. Always false for a real player: it is BACKEND-derived and has
    /// no counterpart in `PlayerInput`, so no client can set it.
    #[serde(default)]
    pub revealed: bool,
    /// ADR-094: cosmetic species tag (0 human, 1 faceling adulto, 2 faceling niño), host-relayed.
    /// The client picks model/animator/audio banks by this value.
    #[serde(default)]
    pub species: u8,
    /// ADR-048: monotonic vocalisation counter (backend→Unity). `ProxyVocalHook` fires on a change.
    #[serde(default)]
    pub vocal_seq: u8,
    /// ADR-048: which voice. 0 reveal-scream, 1 search-shriek, 2 noise-grunt, 3 stalking-breath.
    #[serde(default)]
    pub vocal_kind: u8,
    /// ADR-042: cosmetic "this peer's held wieldable is lit" flag (host-relayed) — the observer
    /// enables a light on the proxy's held model.
    #[serde(default)]
    pub light_on: bool,
    /// ADR-042: cosmetic shot counter (monotonic, wrapping; 0 = never fired), host-relayed. The
    /// observer plays the gunshot on a DELTA, so a full-auto burst that outruns the 10 Hz relay
    /// still lands the right number of shots.
    #[serde(default)]
    pub fire_seq: u8,
    /// ADR-044: cosmetic sustained-state bitfield, host-relayed — bit 0 = aiming, bit 1 = reloading.
    #[serde(default)]
    pub buttons: u16,
    /// ADR-044: cosmetic melee-swing counter (monotonic, wrapping; 0 = never swung), host-relayed.
    #[serde(default)]
    pub melee_seq: u8,
    /// ADR-049: cosmetic carry state, host-relayed. `ProxyCarryHook` renders `carry_count` copies of
    /// `carry_def`'s pickup on the peer's left hand.
    #[serde(default)]
    pub carry_def: i32,
    #[serde(default)]
    pub carry_count: u8,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChunkView {
    pub chunk_schema: u8,
    pub pos: [i32; 2],
    #[serde(default)]
    pub layer: i8,
    pub layer_y: f32,
    pub template_id: u8,
    pub rotation: u16,
    pub mirrored: bool,
    pub state: String,
    pub has_workbench: bool,
    pub layout_grid_size: u8,
    pub layout_cell_size: f32,
    pub layout_cells: Vec<u16>,
    pub edge_openings: u8,
    pub macro_id: u32,
    pub zone_kind: u8,
    pub macro_local: [u8; 2],
    pub macro_size: [u8; 2],
    pub floor_level: i8,
    pub floor_profile: u8,
    pub ceiling_profile: u8,
    pub light_profile: u8,
    pub anomaly_flags: u16,
    pub vertical_flags: u16,
    #[serde(default)]
    pub inter_layer_volumes: Vec<InterLayerVolumeV0>,
    /// Backend-authored volumetric "Rubik grid" architecture (Volumetric V0).
    /// Present only on the near-spawn showcase host chunk; omitted otherwise so
    /// the wire stays backward compatible and unchanged for normal chunks.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub volumetric_grid: Option<VolumetricGridViewV0>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EntityView {
    pub id: u32,
    pub entity_type: String,
    pub position: [f32; 3],
    pub rotation: f32,
    pub state: String,
    pub health_pct: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ItemView {
    pub id: u32,
    pub item_type: String,
    pub position: [f32; 3],
    pub quantity: u16,
}

/// ADR-028 — one loot stack of a corpse. `item_id` is the raw STP item id
/// (`DataIdReference` hash, may be NEGATIVE — same scheme as `equipment`/`held_item`,
/// ADR-022/023), NOT the legacy backend `Item` enum.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ItemStackView {
    pub item_id: i32,
    pub quantity: u16,
    /// ADR-072: las propiedades de instancia del stack (desgaste, munición cargada...). Vacío en
    /// la inmensa mayoría de items, que no tienen ninguna — por eso va al final y con `default`:
    /// un stack sin propiedades cuesta lo mismo en el wire que antes de existir este campo.
    #[serde(default)]
    pub props: Vec<crate::player::session::ItemPropertyValue>,
}

/// ADR-028 — a lootable corpse. `position` is the server-frozen death position (the
/// loot interaction point); the client-side ragdoll is cosmetic and never moves it.
/// `equipment`/`held_item` are the cosmetic snapshot that dresses the ragdoll.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CorpseView {
    pub id: u32,
    pub owner_id: u32,
    pub owner_name: String,
    pub position: [f32; 3],
    pub equipment: [i32; 4],
    pub held_item: i32,
    pub items: Vec<ItemStackView>,
    /// ADR-028 amendment (world chests): crate visual + no dead-player owner client-side.
    #[serde(default)]
    pub is_chest: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GameEvent {
    pub event_type: String,
    #[serde(default)]
    pub data: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ActionResult {
    pub success: bool,
    pub action: String,
    #[serde(default)]
    pub result: Value,
}

// ───────────────────────── Codec helpers ─────────────────────────

/// Encode a server message to a length-prefixed MessagePack frame.
pub fn encode<T: Serialize>(msg: &T) -> Result<Vec<u8>, rmp_serde::encode::Error> {
    let body = rmp_serde::to_vec_named(msg)?;
    let mut frame = Vec::with_capacity(4 + body.len());
    frame.extend_from_slice(&(body.len() as u32).to_be_bytes());
    frame.extend_from_slice(&body);
    Ok(frame)
}

/// Decode a MessagePack frame body (without the length prefix).
pub fn decode<T: for<'de> Deserialize<'de>>(body: &[u8]) -> Result<T, rmp_serde::decode::Error> {
    rmp_serde::from_slice(body)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bare_chunk() -> GridChunkData {
        GridChunkData {
            cx: 0,
            cz: 0,
            layer: 0,
            walls: [[0u8; 10]; 10],
            room_zones: vec![],
            sprays: vec![],
            build_room: None,
            authored_rooms: vec![],
        }
    }

    /// ADR-083 enmienda 1, verificación (f), y enmienda 3 verificación (b): un chunk SIN sala
    /// autorada tiene que costar exactamente lo que costaba en wire 37. Si la clave apareciera
    /// aunque fuese como lista vacía, cada chunk del mundo pagaría el campo — y son la inmensa
    /// mayoría. El paso a plural no puede cambiar eso.
    #[test]
    fn a_chunk_without_an_authored_room_omits_the_key_entirely() {
        let body = rmp_serde::to_vec_named(&bare_chunk()).unwrap();
        let haystack = String::from_utf8_lossy(&body);
        assert!(
            !haystack.contains("authored_room"),
            "la clave viaja en un chunk que no tiene sala"
        );
    }

    /// Y cuando SÍ la hay, sobrevive el viaje con sus SEIS campos en orden. Los dos últimos son las
    /// coordenadas del chunk ancla (wire 41) y van CON SIGNO: un ancla en `(-3, -7)` es tan normal
    /// como una en `(3, 7)`, y con el `u16` de wire 40 habría dado la vuelta en silencio.
    #[test]
    fn an_authored_room_round_trips() {
        let mut chunk = bare_chunk();
        chunk.authored_rooms = vec![[4, 6, 2, 3, -3, -7]];
        let body = rmp_serde::to_vec_named(&chunk).unwrap();
        let decoded: GridChunkData = rmp_serde::from_slice(&body).unwrap();
        assert_eq!(decoded.authored_rooms, vec![[4, 6, 2, 3, -3, -7]]);
    }

    /// ADR-083 enmienda 3 — VARIAS salas en un chunk viajan enteras y EN ORDEN. El orden es
    /// contrato: el cliente instancia por índice de esta lista.
    #[test]
    fn several_authored_rooms_round_trip_in_order() {
        let mut chunk = bare_chunk();
        chunk.authored_rooms = vec![[4, 6, 2, 3, 0, 0], [1, 1, 0, 0, -1, 0], [9, 2, 5, 1, 0, -1]];
        let body = rmp_serde::to_vec_named(&chunk).unwrap();
        let decoded: GridChunkData = rmp_serde::from_slice(&body).unwrap();
        assert_eq!(
            decoded.authored_rooms,
            vec![[4, 6, 2, 3, 0, 0], [1, 1, 0, 0, -1, 0], [9, 2, 5, 1, 0, -1]]
        );
    }

    /// Un backend nuevo tiene que poder leer un chunk viejo sin el campo: es lo que hace que
    /// `#[serde(default)]` no sea decorativo.
    #[test]
    fn a_wire_37_chunk_still_decodes() {
        // Serializa SIN el campo, que es como lo escribía wire 37.
        let old = bare_chunk();
        let body = rmp_serde::to_vec_named(&old).unwrap();
        let decoded: GridChunkData = rmp_serde::from_slice(&body).unwrap();
        assert!(decoded.authored_rooms.is_empty());
    }

    /// ADR-046 Fase 1 — the byte contract with `MsgPackWriter.WriteBin`, pinned against a
    /// HAND-BUILT frame rather than a round-trip through this same serializer. A round-trip
    /// would pass even if both halves agreed on a shape Unity does not emit; these are the
    /// exact bytes the C# writer produces for `{type:"voice", seq, data:<bin>}`.
    fn unity_voice_frame(seq: u16, payload: &[u8]) -> Vec<u8> {
        let mut f = vec![0x83]; // fixmap, 3 entries
        f.push(0xa4);
        f.extend_from_slice(b"type");
        f.push(0xa5);
        f.extend_from_slice(b"voice");
        f.push(0xa3);
        f.extend_from_slice(b"seq");
        f.push(0xcd); // uint16
        f.extend_from_slice(&seq.to_be_bytes());
        f.push(0xa4);
        f.extend_from_slice(b"data");
        // bin8/bin16, exactly as WriteBin chooses the width.
        if payload.len() <= 0xff {
            f.push(0xc4);
            f.push(payload.len() as u8);
        } else {
            f.push(0xc5);
            f.extend_from_slice(&(payload.len() as u16).to_be_bytes());
        }
        f.extend_from_slice(payload);
        f
    }

    #[test]
    fn voice_frame_from_unity_decodes_with_its_bytes_intact() {
        let payload: Vec<u8> = (0..120u16).map(|i| (i * 31 + 7) as u8).collect();
        let frame = unity_voice_frame(0x1234, &payload);

        match decode::<ClientMessage>(&frame).expect("Unity's bin encoding must decode") {
            ClientMessage::Voice { seq, data } => {
                assert_eq!(seq, 0x1234);
                assert_eq!(data, payload, "audio bytes must survive byte for byte");
            }
            other => panic!("decoded as the wrong variant: {other:?}"),
        }
    }

    #[test]
    fn voice_frame_survives_the_bin8_to_bin16_boundary() {
        // 255/256 is where WriteBin switches header width; a decoder that only handled bin8
        // would work in every hand test (a voice frame is ~120 B) and break on the first
        // burst that packs more.
        for len in [0usize, 1, 255, 256, 1024] {
            let payload: Vec<u8> = (0..len).map(|i| (i % 251) as u8).collect();
            match decode::<ClientMessage>(&unity_voice_frame(7, &payload)).unwrap() {
                ClientMessage::Voice { data, .. } => assert_eq!(data.len(), len, "len {len}"),
                other => panic!("wrong variant at len {len}: {other:?}"),
            }
        }
    }

    #[test]
    fn peer_voice_encodes_as_binary_not_as_an_array_of_numbers() {
        // The difference is not cosmetic: an array of 120 integers costs ~1.5× the bytes of a
        // 120 B bin (every value ≥ 128 needs a 2-byte uint8 token), and the client's ReadBin
        // would reject it.
        let msg = ServerMessage::PeerVoice(PeerVoice {
            peer_id: 3,
            seq: 9,
            data: vec![0xff; 40],
        });
        let frame = encode(&msg).expect("PeerVoice must encode");
        let body = &frame[4..];
        assert!(
            body.windows(2).any(|w| w == [0xc4, 40]),
            "expected a bin8 header of length 40 in the encoded body"
        );

        match decode::<ServerMessage>(body).expect("PeerVoice must round-trip") {
            ServerMessage::PeerVoice(v) => {
                assert_eq!(v.peer_id, 3);
                assert_eq!(v.seq, 9);
                assert_eq!(v.data, vec![0xff; 40]);
            }
            other => panic!("decoded as the wrong variant: {other:?}"),
        }
    }

    #[test]
    fn a_flooded_voice_channel_cannot_drop_world_state_messages() {
        // The whole reason ADR-046 gives voice its own broadcast channel: `state_tx` drops its
        // OLDEST messages when it overflows, Events (`player_died`) included. This asserts the
        // isolation directly — overrun the voice channel by 4× and check the state subscriber
        // still receives every message, in order.
        let (state_tx, mut state_rx) = tokio::sync::broadcast::channel::<ServerMessage>(8);
        let (voice_tx, _voice_rx) = tokio::sync::broadcast::channel::<ServerMessage>(4);
        let mut voice_rx = voice_tx.subscribe();

        for i in 0..4u16 {
            state_tx
                .send(ServerMessage::Event(GameEvent {
                    event_type: "player_died".into(),
                    data: serde_json::json!({ "n": i }),
                }))
                .expect("a live subscriber exists");
        }
        for i in 0..16u16 {
            let _ = voice_tx.send(ServerMessage::PeerVoice(PeerVoice {
                peer_id: 1,
                seq: i,
                data: vec![0; 4],
            }));
        }

        for i in 0..4u16 {
            match state_rx.try_recv() {
                Ok(ServerMessage::Event(e)) => {
                    assert_eq!(e.event_type, "player_died");
                    assert_eq!(e.data.get("n").and_then(|v| v.as_u64()), Some(i as u64));
                }
                other => panic!("world state message {i} was lost or reordered: {other:?}"),
            }
        }
        // And the voice channel DID lag — otherwise this test would be proving nothing.
        assert!(
            matches!(
                voice_rx.try_recv(),
                Err(tokio::sync::broadcast::error::TryRecvError::Lagged(_))
            ),
            "the voice channel was expected to overflow; without that the isolation is untested"
        );
    }

    #[test]
    fn player_died_and_respawned_events_encode_with_type_tag() {
        // ADR-025 Slice B diagnosis: prove the death/respawn GameEvents SERIALIZE (encode must
        // not fail on the internally-tagged enum + serde_json::Value payload) and carry the
        // "type":"event" tag + fields the Unity Dispatch switch matches on.
        // ADR-032: session_restored is the third position-arming event (hydration snap) and
        // must keep the exact same wire shape the applier parses.
        for (event_type, key) in [
            ("player_died", "death_pos"),
            ("player_respawned", "position"),
            ("session_restored", "position"),
        ] {
            let msg = ServerMessage::Event(GameEvent {
                event_type: event_type.into(),
                data: serde_json::json!({ key: [22.5f32, 1.8, 22.5] }),
            });
            let frame =
                encode(&msg).unwrap_or_else(|e| panic!("{event_type} failed to encode: {e}"));
            // Decode the body as a generic msgpack map (as Unity's reader does) and check the tag.
            let val: serde_json::Value = rmp_serde::from_slice(&frame[4..])
                .unwrap_or_else(|e| panic!("{event_type} body not a decodable map: {e}"));
            assert_eq!(val.get("type").and_then(|v| v.as_str()), Some("event"));
            assert_eq!(
                val.get("event_type").and_then(|v| v.as_str()),
                Some(event_type)
            );
            assert!(
                val.get("data").and_then(|d| d.get(key)).is_some(),
                "{event_type} data missing {key}"
            );
        }
    }

    #[test]
    fn server_message_round_trips() {
        let msg = ServerMessage::WorldState(WorldState {
            tick: 42,
            world_seed: 42,
            world_revision: 1,
            local_player: LocalPlayerState {
                position: [1.0, 1.8, 2.0],
                rotation: 90.0,
                stats: StatsView {
                    health: 100.0,
                    hunger: 60.0,
                    thirst: 45.0,
                    sanity: 70.0,
                    stamina: 100.0,
                },
                speed_modifier: 1.0,
                inventory_changed: false,
                ack_input_seq: 0,
            },
            remote_players: vec![],
            visible_chunks: vec![],
            visible_entities: vec![],
            visible_items: vec![],
            vertical_debug_markers: vec![],
            stp_items: vec![],
            stp_buildings: vec![],
            stp_carryables: vec![],
            stp_harvestables: vec![],
            visible_corpses: vec![],
        });
        let frame = encode(&msg).unwrap();
        // Strip the 4-byte length prefix before decoding the body.
        let decoded: ServerMessage = decode(&frame[4..]).unwrap();
        match decoded {
            ServerMessage::WorldState(ws) => assert_eq!(ws.tick, 42),
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn world_state_with_chunks_and_entities_round_trips() {
        let msg = ServerMessage::WorldState(WorldState {
            tick: 100,
            world_seed: 42,
            world_revision: 7,
            local_player: LocalPlayerState {
                position: [10.0, 1.8, 20.0],
                rotation: 45.0,
                stats: StatsView {
                    health: 80.0,
                    hunger: 50.0,
                    thirst: 40.0,
                    sanity: 30.0,
                    stamina: 65.0,
                },
                speed_modifier: 0.7,
                inventory_changed: true,
                ack_input_seq: 0,
            },
            remote_players: vec![],
            visible_chunks: vec![ChunkView {
                chunk_schema: 2,
                pos: [0, 0],
                layer: 0,
                layer_y: 0.0,
                template_id: 3,
                rotation: 90,
                mirrored: true,
                state: "random".into(),
                has_workbench: true,
                layout_grid_size: crate::world::chunk::LAYOUT_GRID_SIZE,
                layout_cell_size: crate::world::chunk::LAYOUT_CELL_SIZE,
                layout_cells: vec![crate::world::chunk::CELL_WALKABLE; 100],
                edge_openings: crate::world::chunk::EDGE_NORTH
                    | crate::world::chunk::EDGE_EAST
                    | crate::world::chunk::EDGE_SOUTH
                    | crate::world::chunk::EDGE_WEST,
                macro_id: 0,
                zone_kind: crate::world::chunk::ZONE_NORMAL,
                macro_local: [0, 0],
                macro_size: [1, 1],
                floor_level: 0,
                floor_profile: crate::world::chunk::FLOOR_FLAT,
                ceiling_profile: crate::world::chunk::CEILING_NORMAL,
                light_profile: crate::world::chunk::LIGHT_NORMAL,
                anomaly_flags: 0,
                vertical_flags: 0,
                inter_layer_volumes: vec![],
                volumetric_grid: None,
            }],
            visible_entities: vec![EntityView {
                id: 1,
                entity_type: "lurker".into(),
                position: [12.0, 0.0, 22.0],
                rotation: 180.0,
                state: "idle".into(),
                health_pct: 1.0,
            }],
            visible_items: vec![ItemView {
                id: 10,
                item_type: "metal".into(),
                position: [15.0, 0.0, 18.0],
                quantity: 1,
            }],
            vertical_debug_markers: vec![VerticalDebugMarkerV0 {
                id: 9001,
                kind: "stair".into(),
                world_min: [30.0, 0.0, 30.0],
                world_max: [50.0, 20.0, 50.0],
            }],
            stp_items: vec![],
            stp_buildings: vec![],
            stp_carryables: vec![],
            stp_harvestables: vec![],
            // ADR-028: negative item_id (raw STP DataIdReference hash) must round-trip.
            visible_corpses: vec![CorpseView {
                id: 3,
                owner_id: 7,
                owner_name: "Joel".into(),
                position: [22.5, 1.8, 22.5],
                equipment: [101, 0, -303, 404],
                held_item: -12345,
                items: vec![
                    ItemStackView {
                        item_id: -12345,
                        quantity: 3,
                        props: Vec::new(),
                    },
                    ItemStackView {
                        item_id: 99,
                        quantity: 1,
                        props: Vec::new(),
                    },
                ],
                is_chest: false,
            }],
        });
        let frame = encode(&msg).unwrap();
        let decoded: ServerMessage = decode(&frame[4..]).unwrap();
        match decoded {
            ServerMessage::WorldState(ws) => {
                assert_eq!(ws.tick, 100);
                assert_eq!(ws.world_seed, 42);
                assert_eq!(ws.world_revision, 7);
                assert_eq!(ws.visible_chunks.len(), 1);
                assert_eq!(ws.visible_chunks[0].template_id, 3);
                assert_eq!(ws.visible_entities.len(), 1);
                assert_eq!(ws.visible_entities[0].entity_type, "lurker");
                assert_eq!(ws.visible_items.len(), 1);
                assert_eq!(ws.visible_items[0].item_type, "metal");
                assert_eq!(ws.vertical_debug_markers.len(), 1);
                assert_eq!(ws.vertical_debug_markers[0].id, 9001);
                assert_eq!(ws.vertical_debug_markers[0].kind, "stair");
                assert_eq!(ws.visible_corpses.len(), 1);
                let corpse = &ws.visible_corpses[0];
                assert_eq!(corpse.id, 3);
                assert_eq!(corpse.owner_id, 7);
                assert_eq!(corpse.owner_name, "Joel");
                assert_eq!(corpse.equipment, [101, 0, -303, 404]);
                assert_eq!(corpse.held_item, -12345);
                assert_eq!(corpse.items.len(), 2);
                assert_eq!(corpse.items[0].item_id, -12345);
                assert_eq!(corpse.items[0].quantity, 3);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn client_message_round_trips() {
        let msg = ClientMessage::Input(PlayerInput {
            movement: [0.0, 0.0, 1.0],
            look_delta: [0.5, -0.1],
            sprint: true,
            actions: vec!["interact".into()],
            ..Default::default()
        });
        let body = rmp_serde::to_vec_named(&msg).unwrap();
        let decoded: ClientMessage = decode(&body).unwrap();
        match decoded {
            ClientMessage::Input(input) => {
                assert!(input.sprint);
                assert_eq!(input.movement[2], 1.0);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn movement_delta_round_trips() {
        let msg = ServerMessage::DeltaUpdate(MovementDelta {
            tick: 240,
            ack_input_seq: 57,
            position: [12.0, 1.8, -4.0],
            velocity: [0.0, 0.0, 5.0],
        });
        let frame = encode(&msg).unwrap();
        let decoded: ServerMessage = decode(&frame[4..]).unwrap();
        match decoded {
            ServerMessage::DeltaUpdate(d) => {
                assert_eq!(d.tick, 240);
                assert_eq!(d.ack_input_seq, 57);
                assert_eq!(d.velocity[2], 5.0);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn inter_layer_volume_kind_encodes_as_string_for_unity() {
        let body =
            rmp_serde::to_vec_named(&crate::world::chunk::InterLayerVolumeKindV0::ServiceShaft)
                .unwrap();
        let decoded: serde_json::Value = rmp_serde::from_slice(&body).unwrap();
        assert_eq!(decoded, serde_json::json!("SERVICE_SHAFT"));
    }

    /// ADR-061: `IPCClient.Dispatch` reads "type" as the FIRST key and drops the frame if it
    /// isn't — that assumption is serde-derive's internally-tagged codegen, not an observed
    /// convention, so the hello is pinned byte-for-byte here the way the voice frame is. A
    /// future field added to `ServerHello` must not push the tag out of first position.
    #[test]
    fn hello_frame_puts_the_type_tag_first_on_the_wire() {
        let frame = encode(&ServerMessage::Hello(ServerHello {
            schema_version: 26,
            wg3_enabled: false,
            wg3_manifest_digest: String::new(),
        }))
        .unwrap();

        let body = &frame[4..];
        assert_eq!(
            u32::from_be_bytes(frame[..4].try_into().unwrap()) as usize,
            body.len(),
            "length prefix must match the body"
        );

        let mut expected = vec![0x82]; // fixmap, 2 entries
        expected.push(0xa4);
        expected.extend_from_slice(b"type");
        expected.push(0xa5);
        expected.extend_from_slice(b"hello");
        expected.push(0xae);
        expected.extend_from_slice(b"schema_version");
        expected.push(26); // positive fixint

        assert_eq!(body, expected.as_slice());
    }

    /// ADR-095 — las claves que escribe Rust son EXACTAMENTE las que busca el parser de C#.
    ///
    /// Es el único fallo realista de este mensaje y es silencioso: el contrato de decodificación
    /// del cliente obliga a `else r.Skip()`, así que una clave renombrada aquí no da error al otro
    /// lado — **se salta y el campo queda a su valor por defecto**. Una pieza renombrada saldría
    /// como `piece = 0` en todo el mundo; un `origin_x_cm` renombrado, como todas las piezas
    /// apiladas en el origen del chunk. Nada peta y todo está mal.
    ///
    /// Se comprueba sobre los BYTES y no sobre el struct porque lo que viaja son los bytes: un
    /// `#[serde(rename)]` mal puesto no cambia el struct.
    #[test]
    fn the_wg3_chunk_encodes_the_keys_the_client_parser_looks_for() {
        let frame = encode(&ServerMessage::Wg3Chunk(Wg3ChunkView {
            cx: -3,
            cz: 7,
            placements: vec![Wg3PlacementWire {
                piece: 5,
                rotation: 2,
                origin_x_cm: -12_345,
                origin_z_cm: 6_789,
                // ADR-102 D6 — NO cero. Con la cota a cero este test pasaba igual con la clave
                // renombrada y con el campo perdido, porque cero es también el valor por defecto al
                // que cae el parser de C# cuando se salta una clave. El día que haya dos plantas ese
                // falso verde se lee como "la de arriba se monta pegada a la de abajo", en silencio.
                // 332 es la planta canónica: 320 de altura libre más 12 de losa.
                origin_y_cm: 332,
            }],
            // ADR-098 — el tramo generado va en el mismo mensaje y con sus bocas dentro. Es el único
            // dato de WG3 que no es un índice de catálogo, así que si una de estas claves se
            // renombra el cliente dibuja un pasillo macizo o sin paredes, y en silencio.
            segments: vec![Wg3SegmentWire {
                x_cm: -400,
                z_cm: 250,
                size_x_cm: 1_000,
                size_z_cm: 240,
                floor_y_cm: -18,
                height_cm: 320,
                style: 0,
                openings: vec![Wg3OpeningWire {
                    side: 3,
                    offset_cm: 120,
                    width_cm: 240,
                }],
            }],
            // ADR-101 — y el vano excavado, por la misma razón: si una de sus claves se renombra, el
            // cliente monta la pieza SELLADA mientras el servidor la deja pasar. No peta nada.
            carves: vec![Wg3CarveWire {
                x_cm: -420,
                z_cm: 180,
                size_x_cm: 240,
                size_z_cm: 100,
                bottom_y_cm: 5,
                top_y_cm: 320,
            }],
        }))
        .unwrap();

        let body = String::from_utf8_lossy(&frame).to_string();
        for key in [
            "type",
            "wg3_chunk",
            "cx",
            "cz",
            "placements",
            "piece",
            "rotation",
            "origin_x_cm",
            "origin_z_cm",
            "origin_y_cm",
            "segments",
            "size_x_cm",
            "size_z_cm",
            "floor_y_cm",
            "height_cm",
            "style",
            "openings",
            "side",
            "offset_cm",
            "width_cm",
            "carves",
            "bottom_y_cm",
            "top_y_cm",
        ] {
            assert!(
                body.contains(key),
                "falta la clave {key:?} en el frame: el parser de C# la busca por nombre y, si no \
                 está, la SALTA en silencio"
            );
        }

        // Y el ida y vuelta conserva el signo. Un `u32` por descuido en cualquiera de los dos lados
        // mandaría todo el hemisferio negativo del mundo a coordenadas absurdas.
        let decoded: ServerMessage = decode(&frame[4..]).unwrap();
        match decoded {
            ServerMessage::Wg3Chunk(v) => {
                assert_eq!(-3, v.cx);
                assert_eq!(1, v.placements.len());
                assert_eq!(-12_345, v.placements[0].origin_x_cm);
                assert_eq!(6_789, v.placements[0].origin_z_cm);
                assert_eq!(332, v.placements[0].origin_y_cm);
                assert_eq!(2, v.placements[0].rotation);
                assert_eq!(1, v.segments.len());
                assert_eq!(-400, v.segments[0].x_cm);
                assert_eq!(-18, v.segments[0].floor_y_cm);
                assert_eq!(1, v.segments[0].openings.len());
                assert_eq!(240, v.segments[0].openings[0].width_cm);
            }
            other => panic!("esperaba wg3_chunk, llegó {other:?}"),
        }
    }

    /// ADR-095 — con WG3 apagado el saludo tiene que salir BYTE A BYTE como antes de que existiera.
    ///
    /// No es cosmética: el saludo es lo ÚNICO que informa de un desajuste de versión (ADR-061), así
    /// que es el peor sitio del protocolo para añadir superficie. Un campo que solo aparece cuando
    /// alguien lo enciende no puede romper la puerta de nadie.
    #[test]
    fn the_hello_frame_is_unchanged_while_wg3_is_off() {
        let off = encode(&ServerMessage::Hello(ServerHello {
            schema_version: 46,
            wg3_enabled: false,
            wg3_manifest_digest: String::new(),
        }))
        .unwrap();

        // Fixmap de DOS entradas: `type` y `schema_version`, nada más.
        assert_eq!(0x82, off[4], "el saludo apagado creció de tamaño de mapa");

        let on = encode(&ServerMessage::Hello(ServerHello {
            schema_version: 46,
            wg3_enabled: true,
            wg3_manifest_digest: "abc123".into(),
        }))
        .unwrap();
        assert_eq!(
            0x84, on[4],
            "el saludo encendido debe llevar las cuatro claves"
        );
        assert!(on.len() > off.len());

        // Y el ida y vuelta conserva los dos campos, que es lo que el cliente va a leer.
        let decoded: ServerMessage = decode(&on[4..]).unwrap();
        match decoded {
            ServerMessage::Hello(h) => {
                assert!(h.wg3_enabled);
                assert_eq!("abc123", h.wg3_manifest_digest);
            }
            other => panic!("esperaba hello, llegó {other:?}"),
        }
    }
}
