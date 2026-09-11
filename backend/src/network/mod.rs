//! Networking domain over UDP. Topology is a host-as-server STAR, not a mesh: a joiner only
//! connects to the host, and the host re-emits each peer's pose to the others (ADR-015 relay,
//! `sync::broadcast_peer_poses`). See docs/NETWORK_ARCHITECTURE_CURRENT.md.
//!
//! `NetworkManager` owns the UDP socket, tracks peer connections, handles the
//! reliability layer, and produces `NetworkEvent`s for the game loop.

/// ADR-117 D10: la secuencia de conexión de un joiner —directa, LAN, relay— con su presupuesto
/// por etapa y su motivo por escrito.
pub mod connect;
mod events;
mod faceling;
mod handlers;
pub mod peer;
mod phantom;
pub mod protocol;
/// ADR-117: el lado cliente del relay — registro, latido y estado del enlace. Sin sockets: quien
/// manda los bytes que decide es `NetworkManager::pump_relay`.
pub mod relay_client;
pub mod reliability;
pub mod roster;
mod send;
pub mod sync;
/// ADR-117: las direcciones sintéticas con las que un peer alcanzable sólo por relay se parece a
/// cualquier otro. Aquí no hay E/S: es la traducción, no el transporte.
pub mod transport;

pub use events::NetworkEvent;

use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::{Duration, Instant};

use log::{debug, error, info, warn};
use tokio::net::UdpSocket;
use tokio::sync::mpsc;

use peer::PeerConnection;
use protocol::{decode_packet, encode_packet, PacketHeader, PacketPayload, HEADER_SIZE};

/// Identifier for a peer within a session.
pub type PeerId = u16;

/// ADR-016: base id for injected phantom peers (the robapieles). Chosen ABOVE the real-peer
/// id space so it never collides: host=1, host-assigned fallbacks from 2 up, and joiner
/// NET_IDs = 1000 + pid%60000 ∈ [1000, 60999] (`NetworkInitializer.GenerateDebugNetId`).
/// 0xF000 (61440) clears that range with room to spare in the u16 id space.
const PHANTOM_ID_BASE: PeerId = 0xF000;

/// ADR-094: base id for injected faceling peers (adultos y niños). Same real-id ceiling as
/// `PHANTOM_ID_BASE` (60999), placed BELOW it with room to spare — collision-freedom in
/// practice comes from `allocate_faceling_id`'s probe-and-skip against `self.peers` (the same
/// guarantee `allocate_phantom_id` relies on), not from the two ranges never touching.
const FACELING_ID_BASE: PeerId = 61000;

/// ADR-016/ADR-079: the inert, non-routable address stamped on peers nobody may send to — the
/// host stamps it on injected phantoms at spawn, and a joiner stamps it on `relay_only` PeerList
/// entries (NEVER the addr that came over the wire). 127.0.0.1:1 is never a real peer endpoint;
/// every send path must skip such peers anyway (H10: a datagram at a dead loopback port comes
/// back as WSAECONNRESET on Windows and poisons the sender's own socket).
pub(crate) const INERT_PEER_ADDR: std::net::SocketAddr = std::net::SocketAddr::V4(
    std::net::SocketAddrV4::new(std::net::Ipv4Addr::LOCALHOST, 1),
);

/// Cuánto insiste un joiner con su handshake antes de declarar el intento muerto
/// (`NetworkEvent::ConnectTimedOut`). Presupuesto de PARED, no cuenta de reintentos: lo que le
/// importa al jugador es cuántos segundos lleva mirando "Joining…", y el ritmo de reenvío
/// (1 s, `retry_pending_connection`) es un detalle que puede cambiar sin mover este número.
///
/// 15 s sale de medir el peor caso legítimo: el host tarda ~1-2 s en generar el mundo antes de
/// que su bucle empiece a leer datagramas, así que un join lanzado A LA VEZ que el host tiene
/// que sobrevivir a esa ventana con holgura de un orden de magnitud. Por abajo el límite es la
/// paciencia: más de ~20 s y el jugador ya ha decidido que el juego está colgado.
pub const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

/// F0.5 (E0, ADR-073): tope de los nueve sets de dedupe de peticiones que se migraron. ADR-029 ya
/// exigía poda para los suyos de PvP; el resto se quedó sin ella y crecía durante TODA la sesión —
/// una fuga lenta pero real (cada pickup, drop, colocación, demolición, cosecha y pintada de la
/// partida dejaba su id dentro para siempre).
///
/// **`processed_corpse_requests` se queda deliberadamente como `HashSet`**: es el único que además
/// viaja como parámetro a `apply_corpse_spawn_request`/`apply_corpse_take_request`, así que
/// migrarlo obliga a cambiar dos firmas y ~13 construcciones en `game_loop/tests.rs`. Es un
/// refactor de otra forma y tamaño que este fix, y su fuga es la más pequeña de las diez (una
/// entrada por petición de cadáver, no por objeto del mundo). Queda anotado, no olvidado.
///
/// 512 es holgado por dos órdenes de magnitud frente a lo que hay que recordar: un duplicado solo
/// puede llegar por retransmisión fiable, que muere tras 5 intentos con backoff (~3 s,
/// `reliability.rs`), o por la ventana de 32 en vuelo por peer. Para que una expulsión causara un
/// doble procesamiento haría falta que un retransmit llegara 512 peticiones DESPUÉS de la suya, y
/// a esas alturas el emisor hace rato que se rindió.
pub const DEDUPE_CAP: usize = 512;

/// ADR-029 V0: a size-bounded, insertion-ordered dedupe set. Unlike the older `processed_*`
/// `HashSet`s elsewhere in this module (which grow unbounded for the session's lifetime),
/// ADR-029 explicitly requires PvP dedupe structures to have pruning by size or age — this
/// evicts the oldest entry once `cap` is exceeded, in O(1) amortized per insert.
#[derive(Debug)]
pub struct BoundedDedupeSet<K: std::hash::Hash + Eq + Copy> {
    order: std::collections::VecDeque<K>,
    set: std::collections::HashSet<K>,
    cap: usize,
}

impl<K: std::hash::Hash + Eq + Copy> BoundedDedupeSet<K> {
    pub fn with_capacity(cap: usize) -> Self {
        Self {
            order: std::collections::VecDeque::with_capacity(cap),
            set: std::collections::HashSet::with_capacity(cap),
            cap,
        }
    }

    /// Returns `true` if `key` was newly inserted (not a duplicate). A duplicate does NOT
    /// refresh its position in the eviction order (first-seen wins the recency slot).
    pub fn insert(&mut self, key: K) -> bool {
        if !self.set.insert(key) {
            return false;
        }
        self.order.push_back(key);
        if self.order.len() > self.cap {
            if let Some(oldest) = self.order.pop_front() {
                self.set.remove(&oldest);
            }
        }
        true
    }

    /// F0.5: `HashSet::contains` para los sitios que consultan antes de decidir, sin insertar.
    pub fn contains(&self, key: &K) -> bool {
        self.set.contains(key)
    }

    /// F0.5: `HashSet::is_empty`, usado como "¿he procesado ya algo en esta sesión?".
    pub fn is_empty(&self) -> bool {
        self.set.is_empty()
    }
}

/// ADR-060 (d): los cinco ensambladores de roster, agrupados en un solo campo de
/// `NetworkManager` en vez de cinco sueltos.
#[derive(Debug, Default)]
pub struct RosterAssemblers {
    pub items: roster::RosterAssembler<protocol::StpItemInfo>,
    pub buildings: roster::RosterAssembler<protocol::StpBuildingInfo>,
    pub carryables: roster::RosterAssembler<protocol::StpCarryableInfo>,
    pub harvestables: roster::RosterAssembler<protocol::StpHarvestableInfo>,
    pub corpses: roster::RosterAssembler<crate::world::corpse::CorpseData>,
}

/// Incoming packet from the receive loop.
struct IncomingPacket {
    addr: SocketAddr,
    header: PacketHeader,
    payload: PacketPayload,
}

/// The central networking coordinator. Owns the UDP socket, tracks peers,
/// handles reliability, and bridges between raw packets and game-level events.
/// ADR-071: the per-roster send gates. Grouped in their own struct so a `broadcast_*` can borrow
/// ONE gate mutably while the roster it guards is still borrowed from `NetworkManager` — with the
/// gates inline as five fields the borrow checker would be right to complain.
#[derive(Debug)]
pub struct RosterGates {
    pub items: crate::network::roster::RosterGate,
    pub buildings: crate::network::roster::RosterGate,
    pub carryables: crate::network::roster::RosterGate,
    pub harvestables: crate::network::roster::RosterGate,
    /// 2026-09-10: con retroceso del latido. Medido en la partida de 59 min, este roster emitía 989
    /// veces —cadencia de puro latido, nada cambiaba— pero cada emisión son 22 páginas: 21.772
    /// datagramas y el 8 % del tráfico, todo cadáveres que llevaban una hora quietos. Un cadáver
    /// nuevo cambia el hash y sigue saliendo en el acto. Ver `STATIC_ROSTER_HEARTBEAT_CAP`.
    pub corpses: crate::network::roster::RosterGate,
    /// ADR-093 (E2): gate del broadcast de `Level4State`.
    pub level4: crate::network::roster::RosterGate,
    /// ADR-140 D1: gate del roster de PEERS. Es el único emisor que crece N² por su cuenta — su
    /// lista contiene N peers y se manda a N peers—, y medido el 10-09 iba a **27,5 datagramas por
    /// segundo con UN jugador dentro**.
    pub peers: crate::network::roster::RosterGate,
}

/// `Default` a mano porque una de las siete puertas no es la de serie: la de cadáveres lleva
/// retroceso del latido. Derivarlo y ajustarla después, en cada sitio que construya un
/// `NetworkManager`, es cómo se pierde en un `NetworkManager::bind` nuevo sin que nada avise.
impl Default for RosterGates {
    fn default() -> Self {
        Self {
            items: Default::default(),
            buildings: Default::default(),
            carryables: Default::default(),
            harvestables: Default::default(),
            corpses: crate::network::roster::RosterGate::with_backoff(
                crate::network::roster::STATIC_ROSTER_HEARTBEAT_CAP,
            ),
            level4: Default::default(),
            peers: Default::default(),
        }
    }
}

/// ADR-070: the host-only simulation state of ONE falling item. Pairs with the `StpItemInfo` of
/// the same `id` in `stp_items`, which holds the replicated half (position + the `settling` flag).
#[derive(Debug, Clone, Copy)]
pub struct SettlingItem {
    pub id: u32,
    /// Metres per second. Integrated with gravity each tick; the item sleeps when this goes quiet.
    pub velocity: crate::utils::Vec3,
    /// Consecutive ticks spent below the sleep speed. Consecutive on purpose: a single slow frame
    /// mid-bounce (at the top of an arc the vertical speed passes through zero) must not read as
    /// "come to rest".
    pub quiet_ticks: u8,
    /// Ticks lived. The hard budget of ADR-070 decision 2 — an item that cannot settle on its own
    /// gets put to sleep anyway rather than simulating forever in some pathological corner.
    pub age_ticks: u16,
}

pub struct NetworkManager {
    socket: Arc<UdpSocket>,
    pub local_id: PeerId,
    pub is_host: bool,
    pub peers: HashMap<PeerId, PeerConnection>,
    /// Host-authoritative STP world items, replicated to peers (Phase 1). On the
    /// host it is set from the IPC `set_stp_items` action; on joiners from the
    /// relayed `StpItemList` packet. build_world_state mirrors it to the client.
    pub stp_items: Vec<crate::network::protocol::StpItemInfo>,
    /// ADR-070: the falling items, host-only. Deliberately a SIDE list rather than fields on
    /// `StpItemInfo`: velocity and the age counter are simulation state that nothing outside this
    /// process needs, and `StpItemInfo` is a wire struct — putting them there would ship two
    /// useless vectors per item in every 10 Hz relay. An entry is dropped the moment the item
    /// falls asleep, so the common case (a world full of settled loot) carries an EMPTY list and
    /// the whole feature costs nothing.
    pub settling_items: Vec<crate::network::SettlingItem>,
    /// ADR-071: un gate por roster, host-only. Corta la emisión de un roster que no ha cambiado
    /// desde la última que salió. Uno por roster y no uno global porque se mueven a ritmos muy
    /// distintos: los items cambian cada vez que alguien suelta algo, las piezas construidas de
    /// una base no cambian en horas.
    pub roster_gates: RosterGates,
    /// F0.8 (enmienda ADR-073/074): el mismo gate de ADR-071, aplicado al emisor que la sonda
    /// `host_uplink_baseline` destapó como el 77 % de la subida del host — `broadcast_chunk_states`
    /// reenviaba el `ChunkSyncData` COMPLETO de cada chunk propio a 5 Hz sin mirar si había
    /// cambiado.
    ///
    /// Uno POR CHUNK y no uno por ronda: un chunk con una entidad moviéndose cambia en cada ronda,
    /// y con un gate global arrastraría a los otros ~48 que llevan horas idénticos — que es
    /// justamente el gasto que este fix elimina. Clave `(x, z, layer)`, la misma tripleta con la
    /// que `WorldSyncProgress` cuenta completitud.
    pub chunk_gates: std::collections::HashMap<(i32, i32, i8), crate::network::roster::RosterGate>,
    /// ADR-141 — peers que acaban de entrar y a los que todavía hay que servirles el mundo, con las
    /// rondas que les quedan de servicio.
    ///
    /// Existe porque `RosterGate` es por ROSTER y no por destinatario: su condición `joined` sólo
    /// sabía abrir la puerta, y abrirla significaba retransmitir a TODOS. Medido en dos playtests de
    /// 8 instancias, cada entrada multiplicaba por seis o por diez el tráfico de chunks (de 10-20 a
    /// 95-122 pkt/s) y al sexto jugador la ráfaga se tragaba los latidos de los demás: todos se
    /// declaraban muertos en el mismo milisegundo.
    ///
    /// Rondas y no un instante límite: lo que hace falta garantizar es que al recién llegado le pase
    /// por delante CADA emisor —los cinco rosters y los chunks—, y eso se cuenta en vueltas del
    /// bucle, no en segundos. Con un plazo de tiempo, un bucle lento le dejaría el mundo a medias
    /// hasta el siguiente latido, que desde ADR-139 enm. 2 puede tardar 30 s.
    pub pending_full_sync: std::collections::HashMap<PeerId, u8>,
    /// F0.1 (enmienda ADR-073, E0): `broadcast_world_sync` (el goteo del mundo ENTERO, fiable,
    /// chunk a chunk) se disparaba directo desde cada pickup/drop legacy — 84,9 KB por goteo a
    /// CADA peer, medido. `true` marca "el mundo cambió desde el último goteo despachado"; el
    /// tick lo consume como mucho una vez por `WORLD_SYNC_COALESCE_WINDOW`, ver
    /// `sync::maybe_flush_world_sync`. Host-only, como el propio `broadcast_world_sync`.
    pub world_sync_dirty: bool,
    /// F0.1: instante del último goteo coalescido despachado. `None` = nunca — la primera marca
    /// dirty de la sesión dispara sin esperar a la ventana, igual que ADR-071 no hace esperar al
    /// heartbeat a la primera ronda.
    pub world_sync_last_sent: Option<std::time::Instant>,
    /// E1 / ADR-074 (fase 1): los pares `(origen, destino)` cuya pose se está relayando ahora
    /// mismo. Es el estado de la HISTÉRESIS, que vive en la autoridad y no en el cliente: un par
    /// entra al acercarse a `AOI_POSE_RADIUS_M` y no sale hasta pasar `AOI_POSE_RADIUS_M × 1,2`.
    ///
    /// Sin histéresis, dos jugadores caminando justo sobre la frontera se verían aparecer y
    /// desaparecer varias veces por segundo. Y vive AQUÍ y no en Unity porque dos radios (uno del
    /// host y otro del cliente) pueden discrepar, y el que discrepa produce exactamente el
    /// parpadeo que la histéresis viene a evitar (ADR-074 decisión 2).
    pub aoi_pose_pairs: std::collections::HashSet<(PeerId, PeerId)>,
    /// E1 / ADR-074 (enmienda): ronda del relay de poses, para la cadencia LOD. Los pares del
    /// anillo exterior emiten una de cada dos rondas, escalonados por paridad — ver
    /// `sync::aoi_pose_due_this_round`.
    pub pose_relay_round: u64,
    /// F0.3 (E0, ADR-073): eventos producidos FUERA del camino de recepción, que
    /// `process_incoming` emite junto a los suyos. Lo usa `send_verdict` al desbordar la
    /// cola de un peer: así el desborde termina en la misma `PeerDisconnected` que ya manejan
    /// ADR-056 (fin de sesión en un joiner) y el teardown del host, en vez de estrenar un
    /// segundo camino de desconexión que habría que mantener en paralelo. Y desde 2026-09-09
    /// también el brazo `PeerList`, que descubre VARIOS peers en un solo datagrama y sólo puede
    /// devolver un evento: los `PeerDiscovered` salen por aquí en la pasada siguiente.
    pending_events: Vec<NetworkEvent>,
    /// Los peers que YA estaban cuando este joiner entró, según el `HandshakeAck`. El primer
    /// roster del anfitrión los trae a todos, y sin esta lista cada uno se anunciaría como si
    /// acabara de entrar. Se consume al descubrirlos (uno por id), así un id reutilizado tras una
    /// baja (`allocate_peer_id` los recicla) vuelve a anunciarse, que es lo correcto.
    ///
    /// Mejor esfuerzo, no garantía: `trim_handshake_ack` recorta la lista para que el datagrama
    /// quepa, así que con muchos peers alguno de los presentes puede quedar fuera y anunciarse
    /// tarde. Degrada a un aviso de más, nunca a uno de menos.
    present_at_join: std::collections::HashSet<PeerId>,
    /// Phase 3: client-generated drop ids already processed by the host, so a
    /// duplicated `stp_drop` (watcher race OR reliable retransmit) spawns one item.
    pub processed_stp_drops: BoundedDedupeSet<u64>,
    /// Phase B1: host-authoritative STP building pieces, replicated to peers. On the
    /// host it grows from the IPC `stp_place` action; on joiners from the relayed
    /// `StpBuildingList` packet. build_world_state mirrors it to the client.
    pub stp_buildings: Vec<crate::network::protocol::StpBuildingInfo>,
    /// Phase B1: client-generated place ids already processed by the host, so a
    /// duplicated `stp_place` (reliable retransmit) spawns exactly one piece.
    pub processed_stp_places: BoundedDedupeSet<u64>,
    /// Phase B2: client-generated add ids already processed by the host, so a
    /// duplicated `stp_build_add` (reliable retransmit) advances progress exactly once.
    pub processed_stp_build_adds: BoundedDedupeSet<u64>,
    /// ADR-037: client-generated demolish ids already processed by the host, so a duplicated
    /// `stp_demolish` (reliable retransmit) can never retire a SECOND piece — building ids are
    /// handed out by a monotonic allocator, but this set is what stops a late retransmit from
    /// acting twice.
    pub processed_stp_demolishes: BoundedDedupeSet<u64>,
    /// Phase B3: quantized world pose-cells already occupied by a group piece, so two
    /// players placing on the SAME socket (distinct place_ids) yield exactly one piece —
    /// the host accepts the first and rejects the rest. Key = (x,y,z,yaw) quantized.
    pub occupied_stp_cells: std::collections::HashSet<(i32, i32, i32, i32)>,
    /// ADR-041: noises reported this tick as `(position, loudness_metres)`, drained by
    /// `PhantomDriver`. Host-only and sim-only — never serialized, never sent to a peer, never
    /// persisted. It lives here for the same reason `processed_stp_*` does: the IPC action handler
    /// has `net` in scope but not the driver, and threading the driver through would touch every
    /// caller of `handle_action` for one field.
    pub pending_noises: Vec<([f32; 3], f32)>,
    /// Phase B2.5: host-authoritative STP world carryables, replicated to peers. On the
    /// host it is set from `set_stp_carryables` and grows from drops; on joiners from the
    /// relayed `StpCarryableList` packet. build_world_state mirrors it to the client.
    pub stp_carryables: Vec<crate::network::protocol::StpCarryableInfo>,
    /// Phase B2.5: client-generated carryable drop ids already processed by the host (dedup).
    pub processed_stp_carryable_drops: BoundedDedupeSet<u64>,
    /// Phase B2.6: host-authoritative STP scene harvestables (health), replicated to peers.
    /// On the host it is set from `set_stp_harvestables` and reduced by `stp_harvest_hit`;
    /// on joiners from the relayed `StpHarvestableList` packet.
    pub stp_harvestables: Vec<crate::network::protocol::StpHarvestableInfo>,
    /// Phase B2.6: client-generated harvest-hit ids already processed by the host (dedup).
    pub processed_stp_harvest_hits: BoundedDedupeSet<u64>,
    /// ADR-114 D5: cuándo se agotó cada harvestable, para devolverlo a la vida 15 min después.
    ///
    /// Vive FUERA de `StpHarvestableInfo` a propósito: ese struct es el que viaja por el cable, y
    /// ADR-114 D3 fija que la regeneración no cuesta ni un byte nuevo — el reloj es de este lado y
    /// lo único que se replica es el `remaining` volviendo a 1.
    ///
    /// EN MEMORIA, no se persiste (D5): un mundo recargado arranca con los muebles agotados que
    /// guardó y empieza a contar desde la carga. Sólo el host la llena; en un joiner queda vacía
    /// porque su roster lo escribe el relay, no un golpe.
    pub depleted_harvestables_at: HashMap<u32, std::time::Instant>,
    /// ADR-115 — qué puntos de loot ya se llevaron, y cuándo (en segundos de tiempo de mundo).
    ///
    /// A diferencia de `depleted_harvestables_at`, esto SÍ se persiste: es la pieza entera del
    /// ADR. Vive aquí, junto a los rosters STP, porque es el mismo tipo de estado —del host,
    /// guardado, y consultado por las mismas rutas— y porque `build_save` ya recoge de aquí.
    pub loot_marks: crate::world::loot_marks::LootMarkStore,
    /// ADR-115 — las columnas con cofre VIVO en la ronda anterior. Diferencia contra la ronda
    /// actual = cofres vaciados = marcas nuevas. En memoria a propósito: tras cargar arranca
    /// vacío y la primera ronda no marca nada, que es justo lo que debe pasar (un cofre que
    /// llega del save no es un cofre que alguien acaba de vaciar).
    pub seen_chest_chunks: std::collections::HashSet<(i32, i32)>,
    /// ADR-115 D3 — el tiempo jugado que traía el save, la BASE del reloj de mundo. El reloj
    /// completo es `play_time_base_seconds + tick / TICK_HZ`; sin la base, cada reinicio contaría
    /// desde cero y una marca de hace dos horas parecería recién puesta.
    pub play_time_base_seconds: u64,
    /// ADR-116 — qué punto de reparto le tocó a cada unidad de spawn, en el ANFITRIÓN.
    ///
    /// Por peer y no una lista suelta porque el handshake tiene tres caminos (peer nuevo,
    /// duplicado por id, duplicado por endpoint) y los tres construyen el mismo `HandshakeAck`:
    /// sin memoria por peer, un reintento de conexión gastaría una unidad nueva y movería el punto
    /// del mismo jugador entre dos paquetes que deberían decir lo mismo.
    ///
    /// En memoria: la asignación sólo manda en el PRIMER spawn (D8), y a partir de ahí la posición
    /// del jugador la guarda su propio fichero (ADR-045).
    pub assigned_spawns: std::collections::HashMap<PeerId, crate::utils::Vec3>,
    /// ADR-116 — la siguiente unidad de spawn a repartir. Hoy unidad = jugador; el día que existan
    /// squads será unidad = squad, y este contador no cambia (D10).
    pub next_spawn_unit: u32,
    /// ADR-136 D1 — la identidad de plataforma PROPIA (`PEER_IDENTITY`), opaca: un número que dos
    /// peers pueden comparar y nada más. Viaja en el handshake y, en el anfitrión, es lo que hace
    /// que «me invitó el anfitrión» resuelva por el mismo camino que «me invitó un cliente»: se
    /// compara con esto antes de mirar el mapa. `0` = sin identidad.
    pub local_platform_id: u64,
    /// ADR-136 D2 — a quién señala este joiner como invitador (`INVITED_BY`). `0` = nadie.
    pub invited_by: u64,
    /// ADR-136 D3 — identidad → peer, sólo en el anfitrión. Un `0` nunca entra, y la entrada se
    /// va con el peer (`purge_peer_state`): un id reciclado no puede heredar la identidad de otro.
    pub platform_ids: std::collections::HashMap<u64, PeerId>,
    /// ADR-136 D4 — la posición del jugador LOCAL, que el bucle de juego copia aquí antes de
    /// procesar la red. El `NetworkManager` no conoce al `Player`, y el punto de un invitado por
    /// el anfitrión tiene que salir del ROSTER: esto es la entrada del anfitrión en él.
    pub local_position: [f32; 3],
    /// ADR-116 D3 — el punto que el ANFITRIÓN nos asignó, en un joiner. `None` en el anfitrión
    /// (que se reparte solo, D9) y en un joiner cuyo anfitrión es anterior a ADR-116.
    pub assigned_spawn_from_host: Option<crate::utils::Vec3>,
    /// ADR-068: the world's sprays, indexed by chunk. It lives beside the STP rosters because
    /// it is the same kind of state — host-authoritative, replicated, persisted — but it is
    /// NOT relayed per tick: sprays hydrate with the chunk (`GridChunkData::sprays`) and a new
    /// one travels alone (`ServerMessage::SprayPlaced`). On the host it grows from
    /// `ClientMessage::SprayPlace`; on joiners it will grow from the relayed packet.
    pub sprays: crate::world::spray::SprayStore,
    /// ADR-068: client-generated place ids already accepted, so a reliable retransmit of one
    /// painting paints exactly one spray. Same dedup pattern as `processed_stp_places`.
    pub processed_spray_places: BoundedDedupeSet<u64>,
    /// ADR-068 (joiner-only): chunks whose sprays this peer has already asked the host for.
    /// Unity re-requests a chunk every time it streams back in, and without this each pass
    /// would re-ask for the same sprays it already holds.
    pub requested_spray_chunks: std::collections::HashSet<(i32, i32, u8)>,
    /// ADR-011 follow-up: host-assigned item ids whose StpPickupGranted the joiner already
    /// processed, so a reliable retransmit of the grant never re-stamps last_pickup_at (which
    /// would duplicate the proxy "pickup" window). Same dedup pattern as the processed_stp_* above.
    pub processed_stp_pickup_grants: BoundedDedupeSet<u32>,
    /// ADR-028 Fase E (host-only): (requester, request_id) pairs of corpse spawn/take requests
    /// already processed, so a reliable retransmit spawns exactly one corpse / takes exactly one
    /// stack. Keyed by requester too (request ids are per-peer counters, not globally unique).
    pub processed_corpse_requests: std::collections::HashSet<(PeerId, u64)>,
    /// ADR-028 Fase E (joiner-only): request_ids whose CorpseTakeResult we already surfaced to
    /// our Unity, so a reliable retransmit of the verdict never double-fires the IPC event
    /// (a duplicated corpse_item_taken would double-shift CorpseLootSync's index mirror).
    pub processed_corpse_results: BoundedDedupeSet<u64>,
    /// ADR-093 (E2): host-authoritative Level 4 region state (epoch, ventana, destino de
    /// vuelta). Host-authoritative como `stp_buildings`; un joiner mirroriza el broadcast
    /// verbatim (`Level4StateReceived`), sin procesar puertas él mismo.
    pub level4: crate::world::level4_layout::Level4RegionState,
    /// ADR-060 (joiner-only en la práctica): completitud del goteo de snapshot de mundo.
    /// El gate de spawn del joiner consulta `is_complete()`; el host nunca la toca (resuelve
    /// su spawn en el bootstrap, antes del loop).
    pub world_sync_progress: sync::WorldSyncProgress,
    /// Auditoría de MTU: reúne las páginas de un chunk del goteo antes de aplicarlo. Vive aquí,
    /// junto a `world_sync_progress`, porque es estado del MISMO goteo y muere con él.
    pub chunk_pages: sync::ChunkPageAssembler,
    /// TAREA 2 (2026-08-31): lo mismo para el broadcast periódico (0x11) y para el handoff de
    /// propiedad (0x30), que desde esta tanda también viajan paginados.
    ///
    /// Uno POR PORTADOR y no uno compartido: la clave de ensamblado es `(generación, pos, capa)` y
    /// la generación de los dos es el mismo reloj de sesión, así que un `ChunkState` y un
    /// `ChunkTransfer` del mismo chunk en el mismo milisegundo colisionarían en la misma clave y
    /// podrían coserse entre sí. Cuesta dos mapas vacíos y quita una clase entera de fallo.
    pub chunk_state_pages: sync::ChunkPageAssembler,
    pub chunk_transfer_pages: sync::ChunkPageAssembler,
    /// TAREA 2 (2026-08-31): reensamblado de las pintadas paginadas por trazos. Dos, por las dos
    /// direcciones: `spray_place_pages` es la petición del cliente (clave `place_id`, sólo la usa
    /// el host) y `spray_placed_pages` la pintada ya aceptada (clave `spray.id`, la usan todos).
    pub spray_place_pages: sync::SprayPageAssembler,
    pub spray_placed_pages: sync::SprayPageAssembler,
    /// ADR-060 (d), joiner-only: reensamblado de los cinco rosters paginados. Un roster solo se
    /// aplica cuando su generación está completa — aplicar media lista BORRARÍA la otra mitad de
    /// los objetos del joiner, que es peor que esperar los 100 ms a la ronda siguiente.
    pub roster_assemblers: RosterAssemblers,
    /// ADR-028 Fase E (joiner-only): monotonic source for our corpse request ids.
    pub next_corpse_request_id: u64,
    /// ADR-029 V0 (host-only): (attacker_id, request_id) pairs of PvP hit candidates already
    /// validated, so a reliable retransmit of `PvpHitCandidate` never grants/rejects twice.
    /// Size-bounded (see `BoundedDedupeSet`), unlike the older unbounded `processed_*` sets —
    /// required explicitly by ADR-029 ("las estructuras de dedupe deben tener poda").
    pub processed_pvp_hits: BoundedDedupeSet<(u32, u64)>,
    /// ADR-029 V0 (victim-side, host or joiner): (attacker_id, request_id) pairs of
    /// `PvpDamageGrant` already applied to this backend's own `PlayerStats`, so a reliable
    /// retransmit of the grant never doubles the damage. This is the LOAD-BEARING defensive
    /// dedupe — the victim's own backend is the final authority over its own health.
    pub processed_pvp_grants: BoundedDedupeSet<(u32, u64)>,
    /// ADR-047 (victim-side, host or joiner): `request_id`s of `PhantomAttackGrant` already
    /// applied to this backend's own `PlayerStats`, so a reliable retransmit never doubles a
    /// robapieles' blow. A bare `u64` is enough where PvP needs a pair: the host is the sole
    /// minter of these ids, so they are unique without an attacker to disambiguate them.
    pub processed_phantom_grants: BoundedDedupeSet<u64>,
    /// ADR-047 (host-only): monotonic minter for the `request_id` above. Never reset — a restart
    /// gets a fresh backend and a fresh dedupe set, so the two stay consistent.
    pub next_phantom_attack_request_id: u64,
    /// ADR-094 punto 4 (victim-side): `request_id`s of `StealCommand` already served. Load-bearing
    /// in a way the other dedupe sets are not — these are RELIABLE and retransmitted, and serving
    /// one twice does not repeat a cosmetic, it takes a SECOND item out of the player's bag.
    pub processed_steal_commands: BoundedDedupeSet<u64>,
    /// ADR-094 punto 4 (host-side): `request_id`s of `StealReport` already applied, so a
    /// retransmitted report never hands the same loot to the thief twice.
    pub processed_steal_reports: BoundedDedupeSet<u64>,
    /// ADR-094 punto 4 (host-only): minter for the pair above. Same shape and same reasoning as
    /// `next_phantom_attack_request_id` — the host is the sole minter, so a bare `u64` is unique.
    pub next_steal_request_id: u64,
    /// ADR-014 (host-only): reserved pickups awaiting their deferred removal.
    /// item_id → (requester_id, remove_at). The item stays in `stp_items` (visible) until
    /// remove_at, but a second request for a reserved item is rejected — the reservation is the
    /// dedup now that the removal is deferred. Never points at a vanished item (purged on removal).
    pub pending_pickups: std::collections::HashMap<u32, (PeerId, Instant)>,
    /// ADR-016 (host-only, backend-only): ids of injected "phantom" peers (the robapieles).
    /// A phantom lives in `peers` and renders like a real player, but is excluded from
    /// `real_peer_count` (so it doesn't contaminate internal count gates) and skipped by
    /// reliable broadcasts (its addr is inert). INVARIANT: this mark NEVER crosses the wire —
    /// it is not in `PeerInfo` (P2P) nor `RemotePlayerState` (IPC); pure host-side state. A
    /// joiner therefore cannot tell a phantom from a real peer (and its own set stays empty).
    pub phantom_ids: std::collections::HashSet<PeerId>,
    /// ADR-094 (host-only, backend-only): ids of injected "faceling" peers (adultos y niños de
    /// oficina). Same shape and same reason as `phantom_ids` above — never crosses the wire,
    /// used only to identify a synthetic peer for heartbeat refresh and PvP damage routing.
    /// `PeerConnection::relay_only` (not this set) is what every send/count/roster site already
    /// gates on, so a faceling needs no changes there — see `network::faceling`.
    pub faceling_ids: std::collections::HashSet<PeerId>,
    /// ADR-050 point 9 — victims who reported struggling out of a grab this tick, drained by
    /// `PhantomDriver::tick_grab`.
    ///
    /// A SET keyed by victim and not a flag or a queue: mashing produces many reports for the same
    /// grab and only the first can matter, and one player breaking free must never release the
    /// creature holding somebody else. Host-only — it is the only backend that simulates phantoms,
    /// so a joiner's struggle arrives here as a `StruggleReport` packet.
    pub pending_struggles: std::collections::HashSet<PeerId>,
    /// ADR-053 — the last thing each real player said, kept so a robapieles can say it back.
    ///
    /// ONE packet per speaker, overwritten: this is a stolen scrap of voice, not a recording, and
    /// a rolling buffer would be a per-player audio log living in server memory for no extra
    /// effect. Opus bytes are passed through untouched — the backend never decodes them (it has no
    /// codec and wants none), so the "distortion" is the client's job.
    ///
    /// Host-only in practice: only the host relays voice and only the host simulates phantoms.
    pub voice_echo: std::collections::HashMap<PeerId, Vec<u8>>,
    /// ADR-053 — sequence number for the echoes, and it has to be its OWN monotonic counter.
    ///
    /// The first version borrowed `next_phantom_attack_request_id`, which only moves when a blow is
    /// routed to a REMOTE victim — so in a solo session it sits at 0 forever, every echo went out
    /// with the same `seq`, and the client's jitter buffer (which orders and de-duplicates BY seq,
    /// exactly as a voice stream should) would treat the second one onwards as a repeat and drop
    /// it. The creature would have said your words back exactly once per session.
    pub voice_echo_seq: u16,
    incoming_rx: mpsc::Receiver<IncomingPacket>,
    pub session_start: Instant,
    /// Throttle for `send_datagram`'s failure log: millis since `session_start` of the last
    /// line emitted. Atomic (not a plain field) because the send helper takes `&self` — several
    /// broadcast paths do. One line per second GLOBALLY: a send that fails usually keeps
    /// failing at the broadcast cadence, and the point is to make it visible, not to become the
    /// new noise floor.
    last_send_error_log_ms: std::sync::atomic::AtomicU64,
    /// MTUPROBE (auditoría de heartbeat, 2026-08-30). Atómicos y no campos normales por lo mismo
    /// que `last_send_error_log_ms`: los toca `send_datagram`, que es `&self`.
    oversized_datagrams: std::sync::atomic::AtomicU64,
    /// Solo los FIABLES por encima del techo. Separado del total porque son los únicos que no se
    /// auto-curan, y por tanto los únicos sobre los que hay una invariante que un test puede fijar.
    oversized_reliable: std::sync::atomic::AtomicU64,
    /// TAREA 2 (2026-08-31): datagramas que el techo de `SAFE_DATAGRAM_BYTES` RECHAZÓ antes del
    /// `send_to` real, fiables y no fiables. Separado de `oversized_datagrams` porque ése cuenta
    /// contra la MTU de Ethernet (1472) y sólo observa; éste cuenta lo que NO se ha enviado, que
    /// es sobre lo que hay una invariante: en régimen normal tiene que ser 0, y cualquier valor
    /// distinto nombra un emisor sin acotar.
    refused_datagrams: std::sync::atomic::AtomicU64,
    max_datagram_bytes: std::sync::atomic::AtomicUsize,
    last_mtu_warn_ms: std::sync::atomic::AtomicU64,
    /// TAREA 4 (2026-08-31): throttle de `note_illegal_destination`. Atómico por lo mismo que los
    /// de arriba — lo toca la superficie de envío, que en su mitad no fiable es `&self`.
    last_illegal_dest_log_ms: std::sync::atomic::AtomicU64,
    /// Igual que el de arriba, para la traza de `ChunkStateReceived`. Hace falta un throttle REAL
    /// (una línea por segundo) y no el `elapsed % 1000 < 120` que usan las trazas de pose: aquél
    /// deja pasar una VENTANA de 120 ms, y a ~820 chunks/s eso son ~60 líneas por segundo, no una
    /// — medido, 2 847 líneas en 45 s antes de cambiarlo.
    last_chunk_state_log_ms: std::sync::atomic::AtomicU64,
    /// ADR-011: when the LOCAL player last confirmed a pickup. `broadcast_player_update`
    /// emits animation="pickup" while inside the ~1s window — a trigger flank for the proxy,
    /// NOT the gesture duration (the client owns that via the Animator exitTime).
    pub last_pickup_at: Option<Instant>,
    next_peer_id: PeerId,
    pub world_seed: u64,
    /// ADR-045 Fase 2: whether `world_seed` above is actually known yet. The host knows it from
    /// its own launch args at construction (`true` from the start); a joiner does not learn it
    /// until `handle_handshake_ack` writes it, where this flips to `true` in the same place.
    /// Exists so player-file resolution (which needs `world_seed` + `identity_key` together, see
    /// `game_loop::run`) can poll a plain field instead of inferring the moment from an event.
    pub world_seed_known: bool,
    /// ADR-056: which peer is the host, from this backend's point of view. `None` on the host
    /// itself (it IS the host — nobody to point at) and on a joiner until its `HandshakeAck`
    /// arrives, where `handle_handshake_ack` fills it in with the same `sender_id` it registers
    /// as the host peer.
    ///
    /// Same shape and same reason as `world_seed_known` above: `PeerDisconnected` has to answer
    /// "was that the host?" and the host's id is only implicit today (peer `1` by convention,
    /// spelled as a literal in ~15 call sites that an earlier audit asked NOT to grow). A plain
    /// field lets the handler compare instead of hardcoding a sixteenth.
    pub host_peer_id: Option<PeerId>,
    /// P0-2: density multiplier for the phantom population draw. Same shape as `world_seed` —
    /// read once from env at boot (or from a loaded save, which wins), travels in the
    /// HandshakeAck, and the joiner adopts the host's value. Defaults to 1.0 (no scaling) so
    /// every existing test constructing a `NetworkManager` directly keeps today's behavior.
    pub phantom_density_scale: f32,
    global_sequence: u32,
    pub local_name: String,
    pending_connect_addr: Option<SocketAddr>,
    /// Cuándo se pidió la conexión. Marca el arranque del presupuesto de `CONNECT_TIMEOUT` —
    /// sin esto no hay forma de distinguir "llevo un segundo intentando" de "llevo diez
    /// minutos", que es justo lo que hacía indistinguible un join en curso de uno muerto.
    pending_connect_started_at: Option<Instant>,
    last_handshake_sent_at: Option<Instant>,
    handshake_attempts: u32,
    last_keepalive_trace_at: HashMap<PeerId, Instant>,
    /// RELTRACE: última foto de la cola reliable emitida por peer (throttle 1/s).
    last_reliable_queue_log_at: HashMap<PeerId, Instant>,
    last_transform_trace_at: HashMap<PeerId, Instant>,
    /// ADR-117: el enlace con el relay, o `None` si esta sesión va directa.
    ///
    /// **`None` es el caso de siempre y no cambia ni un byte de comportamiento**: `send_datagram`
    /// sólo lo consulta cuando el destino es una dirección sintética, y sin enlace no hay peers
    /// con direcciones sintéticas.
    pub(crate) relay: Option<transport::RelayLink>,
    /// ADR-117: quien mantiene el registro y el latido contra el relay. `None` = sesión directa.
    relay_client: Option<relay_client::RelayClient>,
    /// ADR-117 D10: las vías por las que intentar entrar, en orden. `None` = un solo destino, que
    /// es el comportamiento de siempre.
    connect_sequence: Option<connect::ConnectSequence>,
    /// Los mensajes de CONTROL del relay que ha ido recogiendo el `receive_loop`. Van por un canal
    /// aparte del de gameplay a propósito: un `PeerReady` no es un paquete de juego y no puede
    /// pasar por `decode_packet`, que lo descartaría como ilegible.
    relay_control_rx: mpsc::Receiver<backrooms_relay::protocol::RelayFrame>,
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
        info!("UDP bound on {local_addr}");
        // NETPROBE (diagnóstico temporal): el puerto REAL, junto al rol. `NET_PORT` puede no ser
        // el que se pidió — `NetworkInitializer.SelectLaunchConfig` salta al siguiente puerto libre
        // si el tecleado está ocupado, y el joiner remoto seguiría apuntando al que ya no escucha.
        info!(
            "NETPROBE event=socket_bound requested_port={port} actual_local_addr={local_addr} role={} self_id={local_id}",
            if is_host { "host" } else { "joiner" }
        );
        info!(
            "MPTRACE step=P event=network_state_init reason=bind self_id_before=<none> self_id_after={} peer_count_before=0 peer_count_after=0 endpoint={} role={}",
            local_id,
            local_addr,
            if is_host { "host" } else { "joiner" }
        );

        let (tx, rx) = mpsc::channel::<IncomingPacket>(512);
        // ADR-117: el canal de CONTROL del relay. Pequeño a propósito — por aquí pasan
        // `PeerReady`, `Auth` y `SessionClosed`, que son unos pocos por sesión, no tráfico.
        let (relay_tx, relay_control_rx) = mpsc::channel(32);
        let recv_socket = socket.clone();
        tokio::spawn(receive_loop(recv_socket, tx, relay_tx));

        Ok(Self {
            socket,
            local_id,
            is_host,
            peers: HashMap::new(),
            stp_items: Vec::new(),
            settling_items: Vec::new(),
            roster_gates: RosterGates::default(),
            chunk_gates: std::collections::HashMap::with_capacity(64),
            world_sync_dirty: false,
            world_sync_last_sent: None,
            aoi_pose_pairs: std::collections::HashSet::with_capacity(64),
            pending_full_sync: std::collections::HashMap::new(),
            pose_relay_round: 0,
            pending_events: Vec::new(),
            present_at_join: std::collections::HashSet::new(),
            processed_stp_drops: BoundedDedupeSet::with_capacity(DEDUPE_CAP),
            stp_buildings: Vec::new(),
            processed_stp_places: BoundedDedupeSet::with_capacity(DEDUPE_CAP),
            processed_stp_build_adds: BoundedDedupeSet::with_capacity(DEDUPE_CAP),
            processed_stp_demolishes: BoundedDedupeSet::with_capacity(DEDUPE_CAP),
            occupied_stp_cells: std::collections::HashSet::with_capacity(256),
            pending_noises: Vec::new(),
            stp_carryables: Vec::new(),
            processed_stp_carryable_drops: BoundedDedupeSet::with_capacity(DEDUPE_CAP),
            stp_harvestables: Vec::new(),
            processed_stp_harvest_hits: BoundedDedupeSet::with_capacity(DEDUPE_CAP),
            depleted_harvestables_at: HashMap::new(),
            loot_marks: crate::world::loot_marks::LootMarkStore::new(),
            seen_chest_chunks: std::collections::HashSet::new(),
            play_time_base_seconds: 0,
            assigned_spawns: std::collections::HashMap::new(),
            next_spawn_unit: 0,
            local_platform_id: 0,
            invited_by: 0,
            platform_ids: std::collections::HashMap::new(),
            local_position: [0.0, 1.8, 0.0],
            assigned_spawn_from_host: None,
            sprays: crate::world::spray::SprayStore::new(),
            processed_spray_places: BoundedDedupeSet::with_capacity(DEDUPE_CAP),
            requested_spray_chunks: std::collections::HashSet::with_capacity(128),
            processed_stp_pickup_grants: BoundedDedupeSet::with_capacity(DEDUPE_CAP),
            processed_corpse_requests: std::collections::HashSet::with_capacity(64),
            processed_corpse_results: BoundedDedupeSet::with_capacity(DEDUPE_CAP),
            level4: crate::world::level4_layout::Level4RegionState::default(),
            world_sync_progress: sync::WorldSyncProgress::default(),
            chunk_pages: sync::ChunkPageAssembler::default(),
            chunk_state_pages: sync::ChunkPageAssembler::default(),
            chunk_transfer_pages: sync::ChunkPageAssembler::default(),
            spray_place_pages: sync::SprayPageAssembler::default(),
            spray_placed_pages: sync::SprayPageAssembler::default(),
            roster_assemblers: RosterAssemblers::default(),
            next_corpse_request_id: 1,
            processed_pvp_hits: BoundedDedupeSet::with_capacity(512),
            processed_pvp_grants: BoundedDedupeSet::with_capacity(512),
            processed_phantom_grants: BoundedDedupeSet::with_capacity(512),
            next_phantom_attack_request_id: 1,
            processed_steal_commands: BoundedDedupeSet::with_capacity(DEDUPE_CAP),
            processed_steal_reports: BoundedDedupeSet::with_capacity(DEDUPE_CAP),
            next_steal_request_id: 1,
            pending_pickups: std::collections::HashMap::new(),
            phantom_ids: std::collections::HashSet::new(),
            faceling_ids: std::collections::HashSet::new(),
            pending_struggles: std::collections::HashSet::new(),
            voice_echo: std::collections::HashMap::new(),
            voice_echo_seq: 0,
            incoming_rx: rx,
            session_start: Instant::now(),
            last_send_error_log_ms: std::sync::atomic::AtomicU64::new(0),
            oversized_datagrams: std::sync::atomic::AtomicU64::new(0),
            oversized_reliable: std::sync::atomic::AtomicU64::new(0),
            refused_datagrams: std::sync::atomic::AtomicU64::new(0),
            max_datagram_bytes: std::sync::atomic::AtomicUsize::new(0),
            last_mtu_warn_ms: std::sync::atomic::AtomicU64::new(0),
            last_illegal_dest_log_ms: std::sync::atomic::AtomicU64::new(0),
            last_chunk_state_log_ms: std::sync::atomic::AtomicU64::new(0),
            last_pickup_at: None,
            next_peer_id: if is_host { 2 } else { 0 },
            world_seed,
            world_seed_known: is_host,
            host_peer_id: None,
            phantom_density_scale: 1.0,
            global_sequence: 0,
            local_name: format!("Player{local_id}"),
            pending_connect_addr: None,
            pending_connect_started_at: None,
            last_handshake_sent_at: None,
            handshake_attempts: 0,
            last_keepalive_trace_at: HashMap::new(),
            last_reliable_queue_log_at: HashMap::new(),
            last_transform_trace_at: HashMap::new(),
            relay: None,
            relay_client: None,
            connect_sequence: None,
            relay_control_rx,
        })
    }

    /// Ata este backend a un relay ya autenticado (ADR-117).
    ///
    /// A partir de aquí, todo destino con dirección sintética sale envuelto hacia
    /// `link.relay_addr`. **No cambia nada del camino directo**: los peers con dirección real
    /// siguen recibiendo sus datagramas por donde siempre.
    pub fn attach_relay(&mut self, link: transport::RelayLink) {
        info!(
            "RELAY event=link_attached self_id={} relay={} session={:#x} my_peer={}",
            self.local_id, link.relay_addr, link.session_id, link.my_peer
        );
        self.relay = Some(link);
    }

    /// El enlace de relay, si lo hay. Para el log y para los tests.
    pub fn relay_link(&self) -> Option<&transport::RelayLink> {
        self.relay.as_ref()
    }

    /// Arranca el registro contra un relay (ADR-117). No bloquea y no manda nada todavía: el
    /// primer datagrama sale en el siguiente [`pump_relay`](Self::pump_relay).
    pub fn connect_relay(&mut self, config: relay_client::RelayConfig) {
        info!(
            "RELAY event=connect_requested relay={} session={:#x} role={} self_id={}",
            config.relay_addr,
            config.session_id,
            if config.as_host { "host" } else { "joiner" },
            self.local_id
        );
        self.relay_client = Some(relay_client::RelayClient::new(config));
    }

    /// El estado del registro, o `None` si esta sesión no usa relay.
    pub fn relay_state(&self) -> Option<&relay_client::RelayClientState> {
        self.relay_client.as_ref().map(|c| c.state())
    }

    /// La dirección sintética del host de la sesión de relay. Es a donde un joiner manda su
    /// handshake de JUEGO una vez el relay lo ha admitido.
    pub fn relay_host_addr(&self) -> Option<SocketAddr> {
        self.relay_client.as_ref().map(|c| c.host_synthetic_addr())
    }

    /// Un latido del enlace con el relay: procesa el control recibido y manda lo que toque.
    ///
    /// Lo llama el bucle de juego. Es barato y no bloquea: en el caso normal —enlace establecido y
    /// sin control pendiente— son dos comprobaciones de reloj y ningún envío.
    ///
    /// **Los datagramas del relay NO pasan por `send_datagram`**: no son gameplay, así que no les
    /// toca ni el techo de ADR-113 ni los contadores de tamaño, y sobre todo no pueden pasar por
    /// el envoltorio —serían un sobre dentro de otro sobre—.
    pub async fn pump_relay(&mut self) -> Vec<relay_client::RelayClientEvent> {
        if self.relay_client.is_none() {
            return Vec::new();
        }

        let now = Instant::now();
        let mut events = Vec::new();

        for frame in self.drain_relay_control() {
            if let Some(client) = &mut self.relay_client {
                if let Some(event) = client.on_control(frame, now) {
                    events.push(event);
                }
            }
        }

        // El enlace se abre y se cierra AQUÍ, en un solo sitio, a partir del estado del cliente.
        // Que `send_datagram` no pueda envolver nada sin `PeerReady` es exactamente esta línea.
        let link = self.relay_client.as_ref().and_then(|c| c.link());
        match (&self.relay, &link) {
            (None, Some(open)) => self.attach_relay(open.clone()),
            (Some(_), None) => {
                warn!(
                    "RELAY event=link_dropped self_id={} state={}",
                    self.local_id,
                    self.relay_state().map(|s| s.name()).unwrap_or("<none>")
                );
                self.relay = None;
            }
            _ => {}
        }

        let outgoing = self
            .relay_client
            .as_mut()
            .and_then(|c| c.poll(now))
            .zip(self.relay_client.as_ref().map(|c| c.config().relay_addr));

        if let Some((datagram, relay_addr)) = outgoing {
            if let Err(e) = self.socket.send_to(&datagram, relay_addr).await {
                warn!(
                    "RELAY event=send_failed self_id={} relay={relay_addr} kind=control bytes={} err={e}",
                    self.local_id,
                    datagram.len()
                );
            }
        }

        events
    }

    /// Recoge los mensajes de control del relay que hayan llegado. No bloquea.
    ///
    /// Devuelve marcos crudos: quién los interpreta es el cliente de relay del commit siguiente.
    /// Vaciar el canal aquí ya importa hoy — si nadie lo consumiera, un relay hablador acabaría
    /// llenándolo y bloqueando el `receive_loop`, que es el hilo por el que entra TODO el
    /// gameplay.
    pub fn drain_relay_control(&mut self) -> Vec<backrooms_relay::protocol::RelayFrame> {
        let mut out = Vec::new();
        while let Ok(frame) = self.relay_control_rx.try_recv() {
            out.push(frame);
        }
        out
    }

    /// Cuántos datagramas FIABLES han salido por encima de `SAFE_DATAGRAM_BYTES`. La invariante
    /// de transporte se comprueba contra esto, no contra el log.
    pub fn oversized_reliable_count(&self) -> u64 {
        self.oversized_reliable
            .load(std::sync::atomic::Ordering::Relaxed)
    }

    /// TAREA 2 (2026-08-31): cuántos datagramas ha RECHAZADO el techo de `SAFE_DATAGRAM_BYTES`
    /// antes de llegar al socket. La invariante de la tarea —cero datagramas sobredimensionados—
    /// se comprueba contra esto y no contra el log: un aviso que nadie lee no es una invariante, y
    /// ése era exactamente el estado anterior.
    pub fn refused_datagram_count(&self) -> u64 {
        self.refused_datagrams
            .load(std::sync::atomic::Ordering::Relaxed)
    }

    /// El mayor datagrama que ha SALIDO por el socket. Su pareja: `refused == 0` dice que nadie
    /// intentó pasarse, y este número dice cuánto margen queda antes de que alguien lo intente.
    pub fn max_datagram_seen(&self) -> usize {
        self.max_datagram_bytes
            .load(std::sync::atomic::Ordering::Relaxed)
    }

    pub fn local_addr(&self) -> SocketAddr {
        self.socket
            .local_addr()
            .unwrap_or_else(|_| "0.0.0.0:0".parse().unwrap())
    }

    fn timestamp(&self) -> u32 {
        self.session_start.elapsed().as_millis() as u32
    }

    fn next_sequence(&mut self) -> u32 {
        self.global_sequence = self.global_sequence.wrapping_add(1);
        if self.global_sequence == 0 {
            self.global_sequence = 1;
        }
        self.global_sequence
    }

    /// Initiate a connection to a remote peer (joiner → host).
    pub async fn initiate_connection(&mut self, addr: SocketAddr) {
        self.pending_connect_addr = Some(addr);
        self.pending_connect_started_at = Some(Instant::now());
        self.handshake_attempts = 0;
        info!(
            "NETPROBE event=connect_attempt_started target={addr} timeout_ms={} self_id={}",
            CONNECT_TIMEOUT.as_millis(),
            self.local_id
        );
        self.send_handshake(addr).await;
    }

    /// El gemelo de `initiate_connection` para cuando NO hay dirección a la que conectarse.
    ///
    /// `pub` y no `pub(super)` —a diferencia de `push_pending_event`— porque su único llamante
    /// legítimo está fuera del módulo: es `main.rs`, en la rama en la que `CONNECT_TO` no parsea.
    /// El nombre acota el permiso: no es «empuja el evento que quieras», es «este destino no
    /// vale». Ver `NetworkEvent::ConnectTargetInvalid` para lo que costaba no tenerlo.
    pub fn reject_invalid_connect_target(&mut self, raw: &str, error: &str) {
        error!(
            "NETPROBE event=connect_target_invalid self_id={} raw={raw:?} error={error}",
            self.local_id
        );
        self.push_pending_event(NetworkEvent::ConnectTargetInvalid {
            raw: raw.to_string(),
            error: error.to_string(),
        });
    }

    async fn send_handshake(&mut self, addr: SocketAddr) {
        self.handshake_attempts = self.handshake_attempts.saturating_add(1);
        self.last_handshake_sent_at = Some(Instant::now());
        info!(
            "Sending handshake to {addr} sender_id={} attempt={}",
            self.local_id, self.handshake_attempts
        );
        info!(
            "MPTRACE step=A event=joiner_send_handshake self_id={} sender_id={} assigned_id=<none> peer_id=<none> endpoint={} peer_count={} remote_players_count=<n/a> remote_players_ids=[] attempt={}",
            self.local_id,
            self.local_id,
            addr,
            self.peers.len(),
            self.handshake_attempts
        );
        let payload = PacketPayload::Handshake {
            player_name: self.local_name.clone(),
            // ADR-136 D1/D2 — quién soy y quién me invitó, opacos. Ceros si no hay identidad o
            // nadie invitó, y un anfitrión anterior al ADR los ignora sin enterarse.
            platform_id: self.local_platform_id,
            invited_by: self.invited_by,
            // Reuses the IPC wire schema counter — see the doc-comment on
            // `crate::ipc::server::WIRE_SCHEMA_VERSION` for why this is one counter, not two.
            version: crate::ipc::server::WIRE_SCHEMA_VERSION.to_string(),
            // ADR-083 enmienda 1 punto 4: el pool de salas viaja en el build, no por la red, así
            // que esto es lo único que delata dos builds con pools distintos.
            room_manifest_digest: crate::world::grid_gen::active_manifest()
                .map(|m| m.digest.clone())
                .unwrap_or_default(),
        };
        let header = PacketHeader::new(payload.type_code(), self.local_id, 0, self.timestamp());
        let data = encode_packet(&header, &payload);
        self.send_datagram(&data, addr, "handshake").await;
    }

    pub async fn retry_pending_connection(&mut self) {
        if self.is_host || !self.peers.is_empty() {
            return;
        }

        // ADR-117 D10: con secuencia manda ella —tiene un presupuesto POR ETAPA y varias vías—.
        // Sin secuencia, el camino de abajo es exactamente el de siempre, que es lo que mantiene
        // intactos los tests que llaman a `initiate_connection` a secas.
        if self.connect_sequence.is_some() {
            self.retry_with_sequence().await;
            return;
        }

        let Some(addr) = self.pending_connect_addr else {
            return;
        };

        // El presupuesto. Un rechazo explícito llega como `Disconnect` y ya tenía camino
        // (`ConnectRejected`); el SILENCIO no tenía ninguno, y es el modo de fallo dominante
        // sobre UDP. Sin este corte el bucle reenvía el mismo handshake muerto cada segundo
        // durante toda la partida mientras Unity —que da por "conectado" el IPC con su PROPIO
        // backend— mete al jugador en un mundo local en solitario sin un solo error.
        if let Some(started) = self.pending_connect_started_at {
            let elapsed = started.elapsed();
            if elapsed >= CONNECT_TIMEOUT {
                let attempts = self.handshake_attempts;
                let elapsed_ms = elapsed.as_millis() as u64;
                self.pending_connect_addr = None;
                self.pending_connect_started_at = None;
                warn!(
                    "NETPROBE event=connect_attempt_timed_out target={addr} attempts={attempts} elapsed_ms={elapsed_ms} self_id={}",
                    self.local_id
                );
                warn!(
                    "MPTRACE step=A3 event=joiner_connect_timed_out self_id={} endpoint={addr} attempts={attempts} elapsed_ms={elapsed_ms}",
                    self.local_id
                );
                self.push_pending_event(NetworkEvent::ConnectTimedOut {
                    addr,
                    attempts,
                    elapsed_ms,
                });
                return;
            }
        }

        let should_retry = self
            .last_handshake_sent_at
            .map(|sent| sent.elapsed() >= Duration::from_secs(1))
            .unwrap_or(true);

        if should_retry {
            self.send_handshake(addr).await;
        }
    }

    /// Arranca una conexión por etapas (ADR-117 D10) en vez de contra un solo destino.
    ///
    /// La primera etapa sale ya. Las demás sólo se prueban si la anterior agota su presupuesto.
    pub async fn initiate_sequence(&mut self, sequence: connect::ConnectSequence) {
        let mut sequence = sequence;
        sequence.start(Instant::now());

        let Some(first) = sequence.current() else {
            let reason = sequence.describe_failure();
            warn!(
                "CONNECTIVITY event=no_candidates self_id={} reason={reason}",
                self.local_id
            );
            self.push_pending_event(NetworkEvent::ConnectFailed { reason });
            return;
        };

        info!(
            "CONNECTIVITY transport={} stage=start target={} self_id={}",
            first.stage.name(),
            first.addr,
            self.local_id
        );
        self.connect_sequence = Some(sequence);
        self.initiate_connection(first.addr).await;
    }

    /// La vía por la que se está intentando entrar ahora mismo, si hay secuencia.
    pub fn connect_stage(&self) -> Option<connect::ConnectStage> {
        self.connect_sequence.as_ref().and_then(|s| s.stage())
    }

    /// El reintento cuando manda la secuencia: o se insiste en la etapa de ahora, o se salta a la
    /// siguiente, o se acaba. Nunca se queda callado insistiendo para siempre.
    async fn retry_with_sequence(&mut self) {
        let now = Instant::now();
        let advance = match &mut self.connect_sequence {
            Some(sequence) => sequence.advance_if_expired(now),
            None => return,
        };

        match advance {
            connect::Advance::Stay => {
                // Mismo ritmo de reintento que el camino de siempre: uno por segundo.
                let Some(addr) = self.pending_connect_addr else {
                    return;
                };
                let should_retry = self
                    .last_handshake_sent_at
                    .map(|sent| sent.elapsed() >= Duration::from_secs(1))
                    .unwrap_or(true);
                if should_retry {
                    self.send_handshake(addr).await;
                }
            }

            connect::Advance::Moved { to, reason } => {
                info!(
                    "CONNECTIVITY transport={} stage=fallback target={} fallback_reason={reason} self_id={}",
                    to.stage.name(),
                    to.addr,
                    self.local_id
                );
                self.push_pending_event(NetworkEvent::ConnectStageChanged {
                    stage: to.stage.name(),
                    addr: to.addr,
                    reason,
                });
                // Reinicia el presupuesto y los contadores del intento: la etapa nueva empieza de
                // cero, no hereda los quince segundos de la anterior.
                self.initiate_connection(to.addr).await;
            }

            connect::Advance::Exhausted { reason } => {
                warn!(
                    "CONNECTIVITY event=exhausted self_id={} reason={reason}",
                    self.local_id
                );
                self.pending_connect_addr = None;
                self.pending_connect_started_at = None;
                self.push_pending_event(NetworkEvent::ConnectFailed { reason });
            }
        }
    }

    /// Process all incoming packets and return game-level events.
    pub async fn process_incoming(&mut self) -> Vec<NetworkEvent> {
        let mut incoming = Vec::new();
        while let Ok(pkt) = self.incoming_rx.try_recv() {
            incoming.push(pkt);
        }

        // F0.3: las desconexiones decididas fuera del camino de recepción (hoy: desborde de la
        // cola de veredictos en `send_verdict`) salen por aquí, para que el game loop las vea
        // como cualquier otra `PeerDisconnected` y no haga falta un segundo camino de teardown.
        let mut events: Vec<NetworkEvent> = self.pending_events.drain(..).collect();
        for pkt in incoming {
            // ADR-140 D4: un lote de poses se ABRE aquí, en paquetes sueltos con el emisor de cada
            // entrada en la cabecera, y sigue por el camino de siempre. Se hace en este punto y no
            // en el despacho porque `handle_packet` reparte con una macro donde cada variante ocupa
            // una posición fija: meter ahí una que se expande en N eventos sería la clase de arreglo
            // adosado que esa macro existe para evitar.
            //
            // El emisor de cada pose sale del PAYLOAD, nunca de la cabecera del lote: la cabecera
            // lleva a quien reemite (el anfitrión), y confundir los dos daría todas las poses por
            // suyas.
            if let crate::network::protocol::PacketPayload::PlayerUpdateBatch { senders, updates } =
                &pkt.payload
            {
                // Un lote descuadrado se descarta entero: aplicar la mitad repartiría poses a
                // nombre de quien no es.
                if senders.len() != updates.len() {
                    log::warn!(
                        "MPTRACE step=S event=pose_batch_mismatched self_id={} senders={} updates={} from={}",
                        self.local_id,
                        senders.len(),
                        updates.len(),
                        pkt.addr
                    );
                    continue;
                }
                let addr = pkt.addr;
                let mut header = pkt.header;
                let entries: Vec<(u16, crate::network::protocol::PacketPayload)> = senders
                    .iter()
                    .copied()
                    .zip(updates.iter().cloned())
                    .collect();
                for (sender, payload) in entries {
                    header.sender_id = sender;
                    events.extend(
                        self.handle_packet(IncomingPacket {
                            addr,
                            header,
                            payload,
                        })
                        .await,
                    );
                }
                continue;
            }

            events.extend(self.handle_packet(pkt).await);
        }
        events
    }

    /// F0.3: encola un evento para que `process_incoming` lo emita en su próxima pasada.
    /// `pub(super)` a propósito: los productores legítimos son el camino fatal de
    /// `send_verdict` y el descubrimiento por roster de `PeerList`, no cualquiera que quiera
    /// fabricar eventos de red sintéticos.
    pub(super) fn push_pending_event(&mut self, event: NetworkEvent) {
        self.pending_events.push(event);
    }

    /// Send heartbeats to all peers.
    pub async fn send_heartbeats(&self) {
        let payload = PacketPayload::Heartbeat;
        // HBTRACE: qué destinos entran REALMENTE en la ronda. `broadcast_destinations` filtra
        // fantasmas y `relay_only`, así que un peer real ausente de esta línea es un peer al que
        // nunca se le manda latido — indistinguible desde fuera de "se lo mandé y se perdió".
        let dests = self.broadcast_destinations();
        info!(
            "HBTRACE event=HEARTBEAT_SENT self_id={} peers=[{}] peer_count={} registered_ids={:?}",
            self.local_id,
            dests
                .iter()
                .map(|(id, addr)| format!("{id}@{addr}"))
                .collect::<Vec<_>>()
                .join(","),
            dests.len(),
            self.peer_ids()
        );
        self.broadcast_unreliable(&payload).await;
    }

    /// Check for timed-out peers. Returns disconnect events.
    pub fn check_timeouts(&mut self) -> Vec<NetworkEvent> {
        let mut events = Vec::new();
        let peer_count_before = self.peers.len();
        let ids_before = self.peer_ids();
        let timed_out: Vec<PeerId> = self
            .peers
            .values()
            .filter(|p| p.is_timed_out())
            .map(|p| p.id)
            .collect();

        if !timed_out.is_empty() || peer_count_before > 0 {
            info!(
                "MPTRACE step=O event=peer_cleanup_scan self_id={} peer_count_before={} peer_count_after=<pending> removed_ids={:?} threshold_ms={} peer_ids_before={:?}",
                self.local_id,
                peer_count_before,
                timed_out,
                peer::HEARTBEAT_TIMEOUT.as_millis(),
                ids_before
            );
        }

        // HBTRACE: cuánto le queda a CADA peer vivo. Es la línea que distingue "el latido llega y
        // el margen respira" de "el margen se está agotando y el siguiente hueco lo mata", que
        // desde fuera se ven igual hasta el instante de la expulsión.
        for peer in self.peers.values() {
            info!(
                "HBTRACE event=LIVENESS_SCAN self_id={} peer_id={} endpoint={} last_seen_ms_ago={} threshold_ms={} margin_ms={}",
                self.local_id,
                peer.id,
                peer.addr,
                peer.last_heartbeat.elapsed().as_millis(),
                peer::HEARTBEAT_TIMEOUT.as_millis(),
                peer::HEARTBEAT_TIMEOUT
                    .saturating_sub(peer.last_heartbeat.elapsed())
                    .as_millis()
            );
        }

        for id in timed_out {
            if let Some(peer) = self.peers.remove(&id) {
                self.purge_peer_state(id);
                info!("Peer {} ({}) timed out", peer.name, peer.addr);
                warn!(
                    "HBTRACE event=HEARTBEAT_TIMEOUT self_id={} peer_id={} endpoint={} last_seen_ms_ago={} threshold_ms={}",
                    self.local_id,
                    id,
                    peer.addr,
                    peer.last_heartbeat.elapsed().as_millis(),
                    peer::HEARTBEAT_TIMEOUT.as_millis()
                );
                warn!(
                    "HBTRACE event=PEER_DISCONNECTED self_id={} peer_id={} endpoint={} reason=heartbeat_timeout peer_count_after={}",
                    self.local_id,
                    id,
                    peer.addr,
                    self.peers.len()
                );
                info!(
                    "MPTRACE step=L event=peer_removed reason=heartbeat_timeout self_id={} peer_id={} endpoint={} peer_count_before={} peer_count_after={} remote_players_ids={:?}",
                    self.local_id,
                    id,
                    peer.addr,
                    peer_count_before,
                    self.peers.len(),
                    self.peer_ids()
                );
                events.push(NetworkEvent::PeerDisconnected {
                    id,
                    reason: "heartbeat timeout".into(),
                });
            }
        }

        if peer_count_before > 0 {
            info!(
                "MPTRACE step=M event=peer_registry_snapshot source=check_timeouts self_id={} peer_count={} peer_ids={:?} endpoints={:?}",
                self.local_id,
                self.peers.len(),
                self.peer_ids(),
                self.peer_endpoints()
            );
        }
        events
    }

    /// `true` como mucho una vez por segundo — throttle REAL, con el mismo compare_exchange que
    /// usa `send_datagram` para su log de fallos. Existe porque el broadcast de chunks llega a
    /// ~820/s y su traza tiene que ser legible, no el nuevo suelo de ruido.
    /// Throttle 1/s POR PEER para la foto de la cola reliable. Mismo patrón que
    /// `should_log_chunk_state`, pero con estado por peer: con varios joiners una sola marca
    /// global dejaría a todos menos a uno sin traza justo cuando hace falta comparar.
    fn should_log_reliable_queue(&mut self, peer_id: PeerId) -> bool {
        let now = Instant::now();
        match self.last_reliable_queue_log_at.get(&peer_id) {
            Some(last) if last.elapsed() < Duration::from_secs(1) => false,
            _ => {
                self.last_reliable_queue_log_at.insert(peer_id, now);
                true
            }
        }
    }

    pub fn should_log_chunk_state(&self) -> bool {
        use std::sync::atomic::Ordering;
        let now_ms = self.session_start.elapsed().as_millis() as u64;
        let last = self.last_chunk_state_log_ms.load(Ordering::Relaxed);
        now_ms.saturating_sub(last) >= 1000
            && self
                .last_chunk_state_log_ms
                .compare_exchange(last, now_ms, Ordering::Relaxed, Ordering::Relaxed)
                .is_ok()
    }

    /// ADR-060: drena las colas diferidas hacia la ventana reliable. Por cada peer, mientras
    /// haya hueco en la ventana Y paquetes aparcados, el más antiguo pasa al aire y a la cola
    /// de retransmisión. Llamado desde `process_retransmits` — mismo tick que procesa los ACKs
    /// que abren el hueco, así el drenaje avanza a velocidad de ventana sin timer propio.
    pub async fn pump_deferred_reliable(&mut self) {
        let peer_ids: Vec<PeerId> = self.peers.keys().copied().collect();
        for pid in peer_ids {
            loop {
                // Préstamo corto: decidir y extraer con el mutable, enviar con `&self`.
                let next = match self.peers.get_mut(&pid) {
                    Some(peer) if peer.can_queue_reliable() => {
                        match peer.deferred_reliable.pop_front() {
                            Some(pkt) => Some((peer.addr, pkt)),
                            None => None,
                        }
                    }
                    _ => None,
                };
                let Some((addr, pkt)) = next else {
                    break;
                };
                // ADR-113 / auditoría de integración (2026-08-31): mismo criterio que
                // `send_reliable` y `send_reliable_queued` — lo que el techo rechaza NO se encola.
                // Ésta era la puerta trasera: `send_reliable_queued` sólo mide el tamaño en la
                // rama que ENVÍA, así que un paquete sobredimensionado aparcado con la ventana
                // llena llegaba aquí sin haber pasado por el techo ni una vez, y se encolaba para
                // cinco retransmisiones que tampoco podían salir — expulsando al peer por
                // `MAX_RETRIES` (ADR-062). El `error!` del rechazo ya nombra el tipo y el tamaño.
                let sent = self
                    .send_datagram(&pkt.data, addr, "deferred_reliable")
                    .await;
                if sent {
                    if let Some(peer) = self.peers.get_mut(&pid) {
                        peer.queue_reliable(pkt.sequence, pkt.data);
                    }
                }
                // Misma cesión que en `send_world_sync`: cuando los ACK abren la ventana de golpe,
                // este bucle la vuelve a llenar entera sin respirar — la ráfaga reaparecería aquí
                // aunque el emisor original la hubiera evitado.
                tokio::task::yield_now().await;
            }
        }
    }

    /// Retransmit reliable packets that haven't been ACKed. Returns disconnect events for the
    /// peers whose reliable path died.
    ///
    /// ADR-062: agotar `MAX_RETRIES` DESCONECTA al peer. Antes se vaciaba su cola y se le
    /// retenía, con lo que quedaba conectado pero mudo para siempre por la vía reliable
    /// (WorldSync, chunks, acciones), sin evento y sin ruta de recuperación — `check_timeouts`
    /// no lo reapaba porque cualquier paquete unreliable le refresca `last_heartbeat`.
    pub async fn process_retransmits(&mut self) -> Vec<NetworkEvent> {
        let mut events = Vec::new();
        self.pump_deferred_reliable().await;
        let peer_ids: Vec<PeerId> = self.peers.keys().copied().collect();
        let peer_count_before = self.peers.len();
        let mut failed_reliable_peers = Vec::new();

        for pid in peer_ids {
            // El préstamo mutable de `peers` se cierra ANTES de enviar: `send_datagram` toma
            // `&self`. Se saca la dirección y la lista de reenvíos, y se sale del scope.
            let pending = match self.peers.get_mut(&pid) {
                Some(peer) => {
                    let (retransmits, peer_dead) = peer.collect_retransmits();
                    if peer_dead {
                        failed_reliable_peers.push(pid);
                        continue;
                    }
                    Some((peer.addr, retransmits))
                }
                None => None,
            };
            if let Some((addr, retransmits)) = pending {
                for (seq, retries, data) in retransmits {
                    debug!("Retransmitting {} bytes to {}", data.len(), addr);
                    // RELTRACE: un reenvío es SIEMPRE una pérdida ya ocurrida. Con la secuencia y
                    // el intento, el log distingue el paquete que se pierde siempre (misma seq
                    // subiendo de intento) del goteo de pérdidas repartidas.
                    warn!(
                        "RELTRACE event=RETRANSMIT self_id={} peer_id={} seq={} attempt={}/{} bytes={} dest={}",
                        self.local_id,
                        pid,
                        seq,
                        retries,
                        reliability::MAX_RETRIES,
                        data.len(),
                        addr
                    );
                    self.send_datagram(&data, addr, "retransmit").await;
                    // La cesión que MÁS importa. Los 32 de una ráfaga reciben su plazo de reenvío
                    // en el mismo instante, así que vencen juntos y sin esto se reenviarían como
                    // otra ráfaga idéntica a la que los perdió. Es lo que convierte una pérdida
                    // puntual en cinco pérdidas seguidas del mismo paquete y termina agotando
                    // `MAX_RETRIES` — sin que se reintente ni una vez de más.
                    tokio::task::yield_now().await;
                }
            }
            // RELTRACE: foto de la cola tras el barrido, como mucho 1/s por peer. Los datos se
            // copian ANTES de consultar el throttle: éste toma `&mut self` y el peer sigue
            // prestado inmutable.
            let snapshot = self.peers.get(&pid).and_then(|peer| {
                if peer.reliable_queue.is_empty() && peer.deferred_reliable.is_empty() {
                    None
                } else {
                    Some((
                        peer.reliable_queue.len(),
                        peer.deferred_reliable.len(),
                        peer.oldest_unacked_age_ms().unwrap_or(0),
                    ))
                }
            });
            if let Some((in_flight, deferred, oldest_ms)) = snapshot {
                if self.should_log_reliable_queue(pid) {
                    info!(
                        "RELTRACE event=RELIABLE_QUEUE self_id={} peer_id={} in_flight={} window={} deferred={} oldest_unacked_ms={}",
                        self.local_id,
                        pid,
                        in_flight,
                        reliability::WINDOW_SIZE,
                        deferred,
                        oldest_ms
                    );
                }
            }
        }

        for pid in failed_reliable_peers {
            let self_id = self.local_id;

            // ADR-016: un fantasma NO se evicta desde aquí — su ciclo de vida lo gestiona el
            // sistema phantom (`refresh_phantom_heartbeats` lo mantiene fuera del alcance de
            // `check_timeouts`). Conserva el comportamiento heredado: purgar y seguir. Ver
            // ADR-062 §guarda de fantasmas: eso lo deja en el mismo estado silencioso que este
            // ADR elimina para peers reales, acotado a fantasmas y declarado como tal.
            if self.is_phantom(pid) {
                let ids_after = self.peer_ids();
                if let Some(peer) = self.peers.get_mut(&pid) {
                    let endpoint = peer.addr;
                    let queued = peer.reliable_queue.len();
                    peer.reliable_queue.clear();
                    let deferred_dropped = peer.purge_deferred();
                    warn!(
                        "Phantom {} ({}) reliable queue dropped after too many retransmit failures; phantom retained (deferred dropped: {deferred_dropped})",
                        peer.name, endpoint
                    );
                    info!(
                        "MPTRACE step=L event=peer_reliable_queue_dropped reason=reliable_retransmit_exhausted_phantom_retained self_id={} peer_id={} endpoint={} peer_count_before={} peer_count_after={} queued_reliable_before={} remote_players_ids={:?}",
                        self_id,
                        pid,
                        endpoint,
                        peer_count_before,
                        peer_count_before,
                        queued,
                        ids_after
                    );
                }
                continue;
            }

            // Peer real: mismo camino de desconexión que `check_timeouts` — remove + purge del
            // estado indexado por PeerId + evento. Su cola diferida muere con él al salir del
            // mapa; no hace falta purgarla aparte.
            if let Some(peer) = self.peers.remove(&pid) {
                self.purge_peer_state(pid);
                warn!(
                    "Peer {} ({}) disconnected: reliable retransmit exhausted ({} reliable + {} deferred packets lost)",
                    peer.name,
                    peer.addr,
                    peer.reliable_queue.len(),
                    peer.deferred_reliable.len()
                );
                info!(
                    "MPTRACE step=L event=peer_removed reason=reliable_retransmit_exhausted self_id={} peer_id={} endpoint={} peer_count_before={} peer_count_after={} queued_reliable_before={} remote_players_ids={:?}",
                    self_id,
                    pid,
                    peer.addr,
                    peer_count_before,
                    self.peers.len(),
                    peer.reliable_queue.len(),
                    self.peer_ids()
                );
                events.push(NetworkEvent::PeerDisconnected {
                    id: pid,
                    reason: "reliable retransmit exhausted".into(),
                });
            }
        }

        events
    }
}

/// DIAGNÓSTICO TEMPORAL (NETPROBE) — traza de recepción cruda para el test host/join por WAN.
/// Grep `NETPROBE` para quitarlo entero. Existe para separar dos fallos que hoy se ven igual
/// desde fuera: "no llegó ni un datagrama" (router/CGNAT/firewall) frente a "llegó y lo tiró el
/// código" (decode, versión de wire, rol equivocado). No filtra nada: se emite ANTES de decodificar.
///
/// Volumen: primer datagrama de CADA origen a `info!` siempre; el resto de ese mismo origen, como
/// mucho uno cada 5 s. Con un peer real conectado son ~60 paquetes/s y sin la limitación esto
/// ahogaría el log. Las CAÍDAS (tamaño corto, decode fallido) se emiten siempre — son raras y son
/// justo la señal que interesa.
const NETPROBE_THROTTLE: Duration = Duration::from_secs(5);

/// Background task: read UDP datagrams, parse, and forward to the NetworkManager.
async fn receive_loop(
    socket: Arc<UdpSocket>,
    tx: mpsc::Sender<IncomingPacket>,
    relay_tx: mpsc::Sender<backrooms_relay::protocol::RelayFrame>,
) {
    let mut buf = vec![0u8; protocol::MAX_PACKET_SIZE];
    let local = socket
        .local_addr()
        .map(|a| a.to_string())
        .unwrap_or_else(|_| "<unknown>".to_string());
    // NETPROBE: origen → último instante logueado. Sin capacidad reservada a propósito: en una
    // sesión sana son 1-3 entradas (host + joiners); si crece, ese crecimiento ES el hallazgo.
    let mut netprobe_seen: HashMap<SocketAddr, Instant> = HashMap::new();
    let mut netprobe_total: u64 = 0;
    loop {
        match socket.recv_from(&mut buf).await {
            Ok((len, addr)) => {
                // NETPROBE — punto de recepción crudo. Nada por encima de esta línea puede
                // rechazar un paquete: si el datagrama tocó la NIC y el firewall lo dejó pasar,
                // aparece aquí sí o sí.
                netprobe_total += 1;
                let first_from_addr = !netprobe_seen.contains_key(&addr);
                let should_log = first_from_addr
                    || netprobe_seen
                        .get(&addr)
                        .map(|last| last.elapsed() >= NETPROBE_THROTTLE)
                        .unwrap_or(true);
                if should_log {
                    netprobe_seen.insert(addr, Instant::now());
                    info!(
                        "NETPROBE event=datagram_received local={local} from={addr} bytes={len} first_from_this_addr={first_from_addr} total_datagrams={netprobe_total}"
                    );
                }

                // ─── ADR-117: LA ENTRADA POR RELAY ───
                //
                // El otro de los dos únicos puntos que saben que el relay existe. Se decide por la
                // MAGIA del sobre y no por la dirección de origen, y eso importa: este bucle se
                // lanza en `bind`, antes de que nadie sepa si esta sesión va a usar relay.
                //
                // La separación es exacta por construcción — ver `transport::classify_inbound`.
                let (packet_bytes, source_addr) = match transport::classify_inbound(&buf[..len]) {
                    transport::Inbound::Direct => (&buf[..len], addr),
                    transport::Inbound::Relayed {
                        from,
                        payload_offset,
                    } => {
                        // El gameplay empieza detrás del sobre. `from` es la dirección SINTÉTICA
                        // del emisor, así que a partir de aquí todo el backend ve un peer normal.
                        (&buf[payload_offset..len], from)
                    }
                    transport::Inbound::Control(frame) => {
                        if relay_tx.send(frame).await.is_err() {
                            break; // Canal cerrado: el manager se ha soltado.
                        }
                        continue;
                    }
                    transport::Inbound::Junk(why) => {
                        warn!(
                            "RELAY event=datagram_dropped reason=bad_envelope local={local} from={addr} bytes={len} error={why}"
                        );
                        continue;
                    }
                };

                if packet_bytes.len() < HEADER_SIZE {
                    // NETPROBE: antes se descartaba en silencio absoluto.
                    warn!(
                        "NETPROBE event=datagram_dropped reason=shorter_than_header local={local} from={source_addr} bytes={} header_size={HEADER_SIZE}",
                        packet_bytes.len()
                    );
                    continue;
                }
                match decode_packet(packet_bytes) {
                    Ok((header, payload)) => {
                        let pkt = IncomingPacket {
                            addr: source_addr,
                            header,
                            payload,
                        };
                        if tx.send(pkt).await.is_err() {
                            break; // Channel closed, manager dropped.
                        }
                    }
                    Err(e) => {
                        // NETPROBE: subido de `debug!` a `warn!`. En `info` (el filtro por defecto
                        // que NetworkInitializer inyecta con RUST_LOG=info) esta línea era invisible,
                        // así que un paquete que SÍ llegaba y no decodificaba se veía exactamente
                        // igual que un paquete que nunca llegó.
                        warn!(
                            "NETPROBE event=datagram_dropped reason=decode_failed local={local} from={addr} bytes={len} error={e}"
                        );
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
mod tests;

/// ADR-140 D3 — arnés de carga: cuánto emite el anfitrión con N jugadores. Marcado `#[ignore]`,
/// se corre a mano; no es una regresión sino una MEDIDA.
#[cfg(test)]
mod load_tests;
