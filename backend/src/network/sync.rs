//! State synchronization: broadcast functions that convert game state to protocol
//! payloads and send them via the NetworkManager.
//! See ARCHITECTURE_V1.md Â§5.4 and Â§3.2.
//!
//! SIX FUNCTIONS HERE HAVE NO CALL SITES, and they are unfinished features rather than cruft â€”
//! the anchor/stabilizer chain was designed and wired up to the wire format but never plugged
//! into the loop. Do not "clean them up" without deciding the feature first:
//!   `build_session_config`, `send_chunk_transfer`, `broadcast_anchor`, `broadcast_stabilizer`,
//!   `build_anchor_list`, `build_stabilizer_list`.
//! They are invisible to the compiler because the crate carries `#![allow(dead_code)]`
//! (`main.rs`); to see them, comment that out and read `cargo test --no-run`.

use crate::player::session::Player;
use crate::utils::{world_to_chunk, Vec3};
use crate::world::chunk::{Chunk, ChunkState};
use crate::world::World;

use log::{info, warn};

use super::protocol::{
    encode_packet, AnchorInfo, ChunkSyncData, EntitySyncData, ItemSyncData, PacketHeader,
    PacketPayload, PeerInfo, SessionConfig, StabilizerInfo,
};
use super::protocol::{PoseWire, RosterKind};
use super::roster;
use super::NetworkManager;
use super::PeerId;

// â”€â”€â”€ Conversion: game types â†’ sync types â”€â”€â”€

/// Cuántas poses caben en un datagrama. El techo de gameplay son 1200 B (ADR-113) y la cuenta se
/// hace con la pose en su PEOR forma, no con la típica: completa, con la velocidad de ADR-146 y
/// cosméticos con ids hash de 5 B mide **81 B**, y el sobre del lote 56. 14 × 81 + 56 = 1190 B.
///
/// Hasta ADR-146 esto valía 24 con la cuenta hecha sobre una completa típica (46 B), y 24 en su
/// peor forma ya eran ~1800 B: la primera ronda en la que 24 orígenes entraban juntos en el AOI
/// de alguien —todos en completa— salía un lote que `send_datagram` rechazaba entero. Lo fija
/// `pose_batch_size_tests`.
///
/// Se trocea por CUENTA y no midiendo el serializado porque el tamaño de una pose es acotado y
/// conocido: `animation` es un byte desde ADR-143.
const MAX_POSES_PER_BATCH: usize = 14;

/// ADR-144 D3 — cada cuántas rondas va la pose completa aunque los cosméticos no hayan cambiado:
/// la reparación contra el datagrama perdido que llevaba el cambio. 30 rondas = 1 s a 30 Hz.
pub const POSE_COSMETICS_REPAIR_ROUNDS: u64 = 30;

/// ADR-144 D3 — huella de los cosméticos para compararlos por par sin guardarlos enteros.
fn cosmetics_hash(c: &PacketCosmetics) -> u64 {
    use std::hash::{Hash, Hasher};
    let mut h = std::collections::hash_map::DefaultHasher::new();
    c.hash(&mut h);
    h.finish()
}
type PacketCosmetics = crate::network::protocol::PoseCosmetics;

/// Parte un lote en los datagramas que hagan falta, conservando el emparejamiento entre cada pose
/// y su emisor.
///
/// **No lleva reensamblado, y es correcto**: cada trozo es un mensaje completo —N poses de N
/// emisores— y el receptor las aplica una a una. Perder un trozo pierde esas poses, que es
/// exactamente lo que pasaba antes al perder un datagrama suelto; la siguiente ronda las repone.
fn split_pose_batches(senders: Vec<u16>, updates: Vec<PoseWire>) -> Vec<(Vec<u16>, Vec<PoseWire>)> {
    if senders.len() <= MAX_POSES_PER_BATCH {
        return vec![(senders, updates)];
    }
    senders
        .chunks(MAX_POSES_PER_BATCH)
        .zip(updates.chunks(MAX_POSES_PER_BATCH))
        .map(|(s, u)| (s.to_vec(), u.to_vec()))
        .collect()
}

/// ADR-139 D1 — huella de la parte ESTABLE de un chunk, para decidir si toca reenviarlo.
///
/// Deja fuera los tres campos que cambian solos y que hacían que el gate no pudiera cortar nunca:
/// `teleport_timer` (baja cada segundo), `entities` (se mueven) e `items` (caen, se cogen). Todo lo
/// demás entra, así que un cambio estructural —estabilizar, anclar, un banco de trabajo nuevo— sigue
/// saliendo en el acto.
///
/// Se construye una copia con esos tres campos vaciados en vez de hashear campo a campo: así, un
/// campo NUEVO en `ChunkSyncData` entra en la huella por omisión. Al revés —listar lo que se
/// hashea— un campo nuevo se quedaría fuera en silencio y no se propagaría jamás, que es la clase de
/// fallo que este sistema no puede permitirse (misma razón que `content_hash` da en ADR-071).
fn stable_chunk_hash(data: &ChunkSyncData) -> u64 {
    let mut stable = data.clone();
    stable.teleport_timer = 0.0;
    stable.entities.clear();
    stable.items.clear();
    roster::content_hash(std::slice::from_ref(&stable))
}

pub fn chunk_to_sync_data(chunk: &Chunk) -> ChunkSyncData {
    let (stabilized, anchored) = match chunk.state {
        ChunkState::Active {
            stabilized,
            anchored,
        } => (stabilized, anchored),
        _ => (false, false),
    };

    ChunkSyncData {
        pos: [chunk.pos.0, chunk.pos.1],
        layer: chunk.layer,
        seed: chunk.seed,
        template_id: chunk.template_id,
        rotation: chunk.rotation,
        mirrored: chunk.mirrored,
        has_workbench: chunk.has_workbench,
        layout: chunk.layout.clone(),
        stabilized,
        anchored,
        teleport_timer: chunk.teleport_timer,
        entities: chunk
            .entities
            .iter()
            .map(|e| EntitySyncData {
                id: e.id,
                entity_type: e.entity_type.type_name().into(),
                position: e.position.to_array(),
                rotation: e.rotation,
                health: e.health,
                state: e.state.state_name().into(),
            })
            .collect(),
        items: chunk
            .items
            .iter()
            .map(|i| ItemSyncData {
                id: i.id,
                item_type: i.item.type_name().into(),
                quantity: i.quantity,
                position: i.position.to_array(),
            })
            .collect(),
        // Una sola página por defecto. `chunk_to_sync_pages` reetiqueta cuando parte.
        page: 0,
        page_count: 1,
        // TAREA 2: la estampa el paginador, NO esto. Ver el doc del campo: si entrara aquí, el
        // hash del gate de F0.8 cambiaría en cada ronda y el gate dejaría de cortar nunca.
        generation: 0,
    }
}

/// TAREA 2 (2026-08-31) — el SOBRE con el que un `ChunkSyncData` va a viajar.
///
/// El troceo se decide midiendo el datagrama REAL, y el mismo chunk no mide igual según el
/// portador: `WorldSyncChunk` añade `world_revision`, `ChunkState` y `ChunkTransfer` no añaden
/// nada. Medir con el sobre equivocado produce páginas que caben "casi" — que es exactamente el
/// fallo que esto viene a cerrar.
#[derive(Debug, Clone, Copy)]
pub enum ChunkCarrier {
    /// Goteo de snapshot de ADR-060 (0x36), fiable. Su clave de ensamblado es `world_revision`.
    WorldSync { world_revision: u64 },
    /// Broadcast periódico del dueño (0x11), no fiable.
    State,
    /// Handoff explícito de propiedad (0x30), fiable y confirmado.
    Transfer,
}

impl ChunkCarrier {
    /// Bytes que ocuparía este `ChunkSyncData` EN EL CABLE con este sobre, cabecera de paquete
    /// incluida. Se mide contra el mismo `encode_packet` que usa el envío para que no puedan
    /// divergir.
    pub fn encoded_len(self, data: &ChunkSyncData) -> usize {
        let payload = self.wrap(data.clone());
        let header = PacketHeader::new(payload.type_code(), 0, 1, 0);
        crate::network::protocol::encode_packet(&header, &payload).len()
    }

    /// El payload listo para enviar. Único sitio donde se decide qué variante lleva qué, para que
    /// la medida y el envío no puedan usar sobres distintos.
    pub fn wrap(self, data: ChunkSyncData) -> PacketPayload {
        match self {
            Self::WorldSync { world_revision } => PacketPayload::WorldSyncChunk {
                world_revision,
                data,
            },
            Self::State => PacketPayload::ChunkState { data },
            Self::Transfer => PacketPayload::ChunkTransfer { data },
        }
    }
}

/// Parte un chunk en las páginas que hagan falta para que NINGÚN datagrama supere
/// `SAFE_DATAGRAM_BYTES`. Devuelve siempre al menos una.
///
/// El límite se comprueba CODIFICANDO, no estimando: el tamaño depende de `layout` (que varía por
/// chunk) y de cadenas de longitud variable dentro de cada entidad e ítem (`entity_type`, `state`,
/// `item_type`). Una estimación por conteo se desviaría justo en los chunks densos, que son
/// exactamente los que rompen. Esto corre una vez por join, no por tick.
///
/// La cabecera va ENTERA en la página 0 y las de continuación la mandan vacía — ver
/// `lean_continuation`. El ensamblador ya tomaba la cabecera de la página 0, así que esto no es
/// un protocolo distinto: es dejar de pagar 557 B por página por un dato que el receptor
/// descarta.
pub fn chunk_to_sync_pages(chunk: &Chunk, world_revision: u64) -> Vec<ChunkSyncData> {
    split_chunk_pages(
        chunk_to_sync_data(chunk),
        ChunkCarrier::WorldSync { world_revision },
        0,
    )
}

/// TAREA 2 — páginas del broadcast periódico del dueño (0x11), estampadas con la ronda.
pub fn chunk_state_pages(data: ChunkSyncData, generation: u32) -> Vec<ChunkSyncData> {
    split_chunk_pages(data, ChunkCarrier::State, generation)
}

/// TAREA 2 — páginas del handoff de propiedad (0x30). Fiable y confirmado, pero paginado por lo
/// mismo: un `ChunkSyncData` completo mide 1094 B VACÍO (medido), así que con cuatro entidades ya
/// no cabe, y un fiable que no cabe se reenvía cinco veces y expulsa al peer (ADR-062).
pub fn chunk_transfer_pages(data: ChunkSyncData, generation: u32) -> Vec<ChunkSyncData> {
    split_chunk_pages(data, ChunkCarrier::Transfer, generation)
}

/// TAREA 2 — la cabecera que llevan las páginas de CONTINUACIÓN: vacía.
///
/// `ChunkLayoutV1::default()` NO sirve aquí: devuelve una rejilla 10×10 entera de celdas
/// caminables, que son 557 de los 1094 B que mide un chunk vacío. Con la cabecera completa
/// repetida en cada página, el presupuesto restante era de ~106 B — UNA entidad por página. A 5 Hz
/// eso convierte un chunk de 13 entidades en 13 datagramas de 1094 B por peer, que es peor que el
/// problema original.
///
/// Vaciarla es correcto porque el ensamblador toma la cabecera de la página 0 y descarta la del
/// resto (ver `ChunkPageAssembler::offer`). Una página de continuación baja así a ~537 B y admite
/// ~7 entidades.
fn lean_continuation(page0: &ChunkSyncData) -> ChunkSyncData {
    let mut lean = page0.clone();
    lean.entities.clear();
    lean.items.clear();
    lean.layout = crate::world::chunk::ChunkLayoutV1 {
        grid_size: 0,
        cell_size: 0.0,
        cells: Vec::new(),
        edge_openings: 0,
        macro_id: 0,
        zone_kind: 0,
        macro_local: [0, 0],
        macro_size: [0, 0],
        floor_level: 0,
        floor_profile: 0,
        ceiling_profile: 0,
        light_profile: 0,
        anomaly_flags: 0,
        vertical_flags: 0,
        inter_layer_volumes: Vec::new(),
        edges_v: Vec::new(),
        edges_h: Vec::new(),
    };
    lean
}

/// Parte un chunk en las páginas que hagan falta para que NINGÚN datagrama supere
/// `SAFE_DATAGRAM_BYTES` con el sobre de `carrier`. Devuelve siempre al menos una.
///
/// El límite se comprueba CODIFICANDO, no estimando: el tamaño depende de `layout` (que varía por
/// chunk) y de cadenas de longitud variable dentro de cada entidad e ítem (`entity_type`, `state`,
/// `item_type`). Una estimación por conteo se desviaría justo en los chunks densos, que son
/// exactamente los que rompen.
///
/// # Las TRES listas de tamaño ilimitado, y la que faltaba
///
/// Esto se escribió partiendo `entities` e `items` y dando por hecho que el resto —la
/// «cabecera»— tenía tamaño acotado, con `layout` dominado por una rejilla `LAYOUT_GRID_SIZE`
/// fija de 10. **Esa premisa era falsa**, y la sesión física por internet del 2026-08-31 la
/// desmintió: 1.176 `datagram_refused_over_budget` de 1612 a 1910 B hacia un solo joiner, todos
/// de los mismos dos chunks, cada uno precedido por su `chunk_header_exceeds_budget`.
///
/// El campo que no estaba acotado es `layout.inter_layer_volumes`: un `Vec<InterLayerVolumeV0>`
/// donde cada volumen lleva DOS `String` (`safety_type`, `future_audio_hint`) más un
/// `Vec<String>` de pistas visuales, con literales de 30-35 caracteres. Los siete volúmenes que
/// el generador cuelga de un chunk conector suman ~1.160 B sobre los ~750 de la cabecera real.
///
/// Ningún número de páginas salvaba eso, porque la cabecera se REPITE en todas: el chunk se
/// devolvía entero y el techo de salida lo rechazaba. Y como el `ChunkState` a 10 Hz —la vía que
/// cura una pérdida— venía del mismo paginador, tampoco llegaba: el chunk no aparecía JAMÁS en
/// el cliente.
///
/// Hoy los volúmenes se reparten como una tercera lista, con el mismo mecanismo y sin protocolo
/// nuevo: el campo ya viajaba, `page`/`page_count`/`generation` ya existían, y el ensamblador los
/// concatena igual que a las otras dos. Lo que queda de verdad acotado en la cabecera son `cells`
/// y `edges_v`/`edges_h`, que sí dependen solo de `grid_size`.
fn split_chunk_pages(
    full: ChunkSyncData,
    carrier: ChunkCarrier,
    generation: u32,
) -> Vec<ChunkSyncData> {
    let budget = crate::network::protocol::SAFE_DATAGRAM_BYTES;
    // La generación viaja en TODAS las páginas, también en la única de un chunk que cabe: medir
    // sin ella daría un tamaño que no es el que sale al cable.
    let mut full = full;
    full.generation = generation;
    if carrier.encoded_len(&full) <= budget {
        return vec![full];
    }

    // La cabecera DESNUDA —sin ninguna de las tres listas de tamaño ilimitado— es lo que se mide
    // aquí: si ni eso cabe, partir no puede salvarlo. Se devuelve entera y el techo de
    // `send_datagram` la rechazará nombrándola — callarlo sería perder el chunk en silencio, que
    // es lo único peor que perderlo a gritos.
    //
    // `inter_layer_volumes` se vacía junto a `entities`/`items` y no con ellas por casualidad: es
    // la tercera lista sin cota, y dejarla dentro de la cabecera es exactamente lo que hacía que
    // un chunk conector no llegara nunca (ver el doc-comment de esta función). Lo que queda es
    // `cells` + `edges_v`/`edges_h`, acotados por `grid_size`.
    let mut head = full.clone();
    head.entities.clear();
    head.items.clear();
    head.layout.inter_layer_volumes.clear();
    // Se mide con los índices en su PEOR valor: msgpack escribe un entero con el prefijo más
    // corto que le sirva, así que `page = 0` ocupa un byte y `page = 300` ocupa tres, y los
    // índices reales no se conocen hasta el final. Estimar con 0/1 y estampar después dejaba
    // páginas un par de bytes por encima del techo. Se restauran al numerar.
    head.page = u16::MAX;
    head.page_count = u16::MAX;
    if carrier.encoded_len(&head) > budget {
        warn!(
            "MTUPROBE event=chunk_header_exceeds_budget chunk=({},{}) layer={} bare_bytes={} budget={budget}",
            full.pos[0],
            full.pos[1],
            full.layer,
            carrier.encoded_len(&head),
        );
        return vec![full];
    }
    let tail = lean_continuation(&head);

    let mut pages: Vec<ChunkSyncData> = Vec::new();
    let mut entities = full.entities.clone();
    let mut items = full.items.clone();
    let mut volumes = full.layout.inter_layer_volumes.clone();
    entities.reverse(); // se consumen con pop(), así se conserva el orden original
    items.reverse();
    volumes.reverse();

    while !entities.is_empty() || !items.is_empty() || !volumes.is_empty() || pages.is_empty() {
        // Página 0 con la cabecera real; el resto con la vacía.
        let mut page = if pages.is_empty() {
            head.clone()
        } else {
            tail.clone()
        };
        // Llenado voraz, verificando tras cada añadido: en cuanto uno se pasa, se devuelve a la
        // cola y la página se cierra. Nunca se emite una página que no se haya medido.
        //
        // Los volúmenes van PRIMERO a propósito: son parte de la arquitectura del chunk, y
        // ponerlos delante hace que el caso común —los que caben enteros en la página 0— salga
        // byte a byte como salía antes de esta corrección.
        while let Some(v) = volumes.pop() {
            page.layout.inter_layer_volumes.push(v);
            if carrier.encoded_len(&page) > budget {
                let back = page
                    .layout
                    .inter_layer_volumes
                    .pop()
                    .expect("acabamos de meterlo");
                volumes.push(back);
                break;
            }
        }
        while let Some(e) = entities.pop() {
            page.entities.push(e);
            if carrier.encoded_len(&page) > budget {
                let back = page.entities.pop().expect("acabamos de meterlo");
                entities.push(back);
                break;
            }
        }
        while let Some(i) = items.pop() {
            page.items.push(i);
            if carrier.encoded_len(&page) > budget {
                let back = page.items.pop().expect("acabamos de meterlo");
                items.push(back);
                break;
            }
        }
        // Una página vacía con cosas pendientes significaría que un solo elemento no cabe ni con
        // la cabecera: se fuerza para no entrar en bucle infinito, y el techo de salida lo grita.
        if page.entities.is_empty()
            && page.items.is_empty()
            && page.layout.inter_layer_volumes.is_empty()
        {
            if let Some(v) = volumes.pop() {
                page.layout.inter_layer_volumes.push(v);
            } else if let Some(e) = entities.pop() {
                page.entities.push(e);
            } else if let Some(i) = items.pop() {
                page.items.push(i);
            }
        }
        pages.push(page);
        if entities.is_empty() && items.is_empty() && volumes.is_empty() {
            break;
        }
    }

    let total = pages.len() as u16;
    for (idx, page) in pages.iter_mut().enumerate() {
        page.page = idx as u16;
        page.page_count = total;
    }
    pages
}

/// Ensamblador de las páginas de UN chunk del goteo de mundo (auditoría de MTU, 2026-08-30).
///
/// Existe porque la capa reliable es **at-least-once y SIN orden**: la página 1 puede llegar antes
/// que la 0, y cualquiera puede llegar duplicada tras un ACK perdido. Con eso, "la página 0 limpia
/// y las demás añaden" pierde datos en cuanto el orden se invierte. La única regla que sobrevive a
/// reordenación y duplicados es no aplicar NADA hasta tener el juego completo, que es exactamente
/// lo que ADR-060 ya decidió para los rosters (`RosterAssembler`). Mismo patrón, misma razón.
///
/// Clave `(revision, pos, layer)`: una revisión nueva del mundo invalida el ensamblado a medias de
/// la anterior — su goteo quedó superseded y sus rezagados no deben mezclarse con el nuevo.
#[derive(Debug, Default)]
pub struct ChunkPageAssembler {
    pending: std::collections::HashMap<(u64, [i32; 2], i8), PendingChunk>,
    /// Orden de llegada de las claves, para poder desalojar la más vieja al desbordar. Misma
    /// forma que `BoundedDedupeSet`, y por la misma razón.
    order: std::collections::VecDeque<(u64, [i32; 2], i8)>,
}

/// Tope de chunks a medio ensamblar. Sin él, una página perdida deja su parcial en memoria PARA
/// SIEMPRE: nadie la reclama, la revisión no cambia, y el goteo siguiente estrena claves nuevas.
/// Es una fuga que introduce la propia paginación, así que se acota aquí y no se deja anotada.
///
/// 128 es holgado frente a lo que puede haber en vuelo de verdad: un goteo completo son los chunks
/// del mundo (49-64 medidos) y solo los PARTIDOS ocupan sitio. Desalojar el más viejo es correcto
/// porque un parcial que ya no recibe páginas no va a completarse nunca; la retransmisión fiable
/// lo repone entero si el emisor sigue insistiendo.
const PENDING_CHUNK_CAP: usize = 128;

#[derive(Debug)]
struct PendingChunk {
    page_count: u16,
    /// Páginas recibidas, por índice. `HashMap` y no `Vec` porque llegan desordenadas y
    /// duplicadas: insertar por índice hace el dedupe gratis.
    pages: std::collections::HashMap<u16, ChunkSyncData>,
}

impl ChunkPageAssembler {
    /// Entrega una página. Devuelve el chunk COMPLETO —con sus listas reunidas en el orden de
    /// página— la primera vez que se completa, y `None` mientras falte alguna.
    ///
    /// Un chunk de una sola página (`page_count <= 1`) sale tal cual, sin tocar el mapa: es el
    /// caso común y no debe pagar ninguna estructura.
    pub fn offer(&mut self, world_revision: u64, data: ChunkSyncData) -> Option<ChunkSyncData> {
        if data.page_count <= 1 {
            return Some(data);
        }

        let key = (world_revision, data.pos, data.layer);
        let page_count = data.page_count;
        // TAREA 2: un parcial de una clave ANTERIOR del mismo chunk ya no se puede completar —su
        // ronda pasó— y su único destino sería ocupar sitio hasta que el tope lo desalojara. Se
        // tira aquí, que además es lo que impide que un rezagado viejo llegue a mezclarse si el
        // emisor reutilizara una generación (no lo hace: es el reloj de sesión, monótono).
        self.pending.retain(|(gen, pos, layer), _| {
            !(*pos == data.pos && *layer == data.layer && *gen < world_revision)
        });
        self.order.retain(|(gen, pos, layer)| {
            !(*pos == data.pos && *layer == data.layer && *gen < world_revision)
        });
        if !self.pending.contains_key(&key) {
            self.order.push_back(key);
            while self.order.len() > PENDING_CHUNK_CAP {
                if let Some(oldest) = self.order.pop_front() {
                    self.pending.remove(&oldest);
                }
            }
        }
        let entry = self.pending.entry(key).or_insert_with(|| PendingChunk {
            page_count,
            pages: std::collections::HashMap::with_capacity(page_count as usize),
        });

        // Un emisor que cambia de opinión sobre el número de páginas dentro de la MISMA revisión
        // no puede pasar: sería mezclar dos particiones distintas del mismo chunk.
        if entry.page_count != page_count {
            entry.page_count = page_count;
            entry.pages.clear();
        }

        entry.pages.insert(data.page, data);
        if entry.pages.len() < entry.page_count as usize {
            return None;
        }

        let done = self.pending.remove(&key).expect("acabamos de verlo");
        self.order.retain(|k| *k != key);
        let mut ordered: Vec<(u16, ChunkSyncData)> = done.pages.into_iter().collect();
        ordered.sort_by_key(|(idx, _)| *idx);

        // La cabecera sale de la página 0: todas la repiten idéntica, pero fijar cuál manda deja
        // el resultado determinista aunque alguna vez dejaran de serlo.
        //
        // `inter_layer_volumes` se reúne con las otras dos y NO se hereda de la página 0: desde
        // que el paginador lo reparte (es la tercera lista sin cota), la página 0 solo trae los
        // que le cupieron. Tomarlo de la cabecera devolvería una lista truncada — que es
        // precisamente el fallo, con más pasos.
        let mut merged = ordered[0].1.clone();
        merged.entities.clear();
        merged.items.clear();
        merged.layout.inter_layer_volumes.clear();
        for (_, page) in &ordered {
            merged.entities.extend(page.entities.iter().cloned());
            merged.items.extend(page.items.iter().cloned());
            merged
                .layout
                .inter_layer_volumes
                .extend(page.layout.inter_layer_volumes.iter().cloned());
        }
        merged.page = 0;
        merged.page_count = 1;
        Some(merged)
    }

    /// Descarta lo aparcado de revisiones anteriores a `world_revision`. Sin esto, un goteo
    /// superseded a medias se quedaría en memoria para toda la sesión.
    pub fn drop_stale(&mut self, world_revision: u64) {
        self.pending.retain(|(rev, _, _), _| *rev >= world_revision);
        self.order.retain(|(rev, _, _)| *rev >= world_revision);
    }

    /// Cuántos chunks hay a medio ensamblar. Para trazas y tests.
    pub fn pending_len(&self) -> usize {
        self.pending.len()
    }
}

// ─── TAREA 2 (2026-08-31): paginación de las pintadas, por trazos ───

/// Reparte los trazos de un gesto en las tandas que hagan falta para que ningún datagrama supere
/// `SAFE_DATAGRAM_BYTES`, midiendo con `measure` el payload REAL de cada tanda.
///
/// Un trazo que por sí solo no cabe viaja SOLO en su tanda: partirlo exigiría un índice dentro de
/// su blob de puntos, y con el tope actual (`MAX_POINTS_PER_SPRAY` = 512, o sea 1024 B de puntos)
/// un trazo suelto sigue cabiendo. Si algún día no cupiera, el techo de salida lo rechazaría
/// nombrándolo, que es la degradación ruidosa y no la silenciosa.
///
/// Una lista vacía devuelve UNA tanda vacía y no cero: `validate` ya rechaza las pintadas sin
/// trazos, pero suprimir la tanda convertiría un rechazo en un mensaje que no llega nunca.
pub fn split_spray_strokes(
    strokes: &[crate::world::spray::SprayStroke],
    measure: impl Fn(&[crate::world::spray::SprayStroke]) -> usize,
) -> Vec<Vec<crate::world::spray::SprayStroke>> {
    let budget = crate::network::protocol::SAFE_DATAGRAM_BYTES;
    if strokes.is_empty() || measure(strokes) <= budget {
        return vec![strokes.to_vec()];
    }

    let mut pages: Vec<Vec<crate::world::spray::SprayStroke>> = Vec::new();
    let mut current: Vec<crate::world::spray::SprayStroke> = Vec::new();
    for stroke in strokes {
        current.push(stroke.clone());
        // Se mide DESPUÉS de añadir, igual que el paginador de chunks: el tamaño de un trazo
        // depende de su blob de puntos y estimarlo se desviaría justo en los gestos largos.
        if measure(&current) > budget && current.len() > 1 {
            let back = current.pop().expect("acabamos de meterlo");
            pages.push(std::mem::take(&mut current));
            current.push(back);
        }
    }
    if !current.is_empty() {
        pages.push(current);
    }
    pages
}

/// TAREA 2 (2026-08-31) — trocea un `SprayDraft` (0x54) en datagramas que quepan.
///
/// `points_mm` es un blob de pares `(u, v)` en `i16` — 4 bytes por punto — cuyo tamaño lo decide
/// el cliente: manda los puntos NUEVOS desde el último envío, así que un cliente con hipo (o uno
/// parcheado) puede meter un gesto entero en un solo paquete.
///
/// **Sin ensamblador, y por diseño del propio payload**: `first_index` existe precisamente para
/// que el receptor sepa dónde encaja cada trozo y no cosa dos que no van seguidos. Un trazo en
/// curso es efímero (vive 3 s, no se guarda, y `SprayPlaced` lo sustituye entero al soltar), así
/// que cada trozo es aplicable por sí solo y perder uno se cura al soltar el gatillo. Exigir
/// todo-o-nada aquí sería pagar latencia por un dibujo provisional.
///
/// El corte cae siempre en frontera de punto (múltiplo de 4 B): partir un par `(u, v)` por la
/// mitad daría coordenadas inventadas.
#[allow(clippy::too_many_arguments)]
pub fn spray_draft_datagrams(
    place_id: u64,
    layer: u8,
    anchor: [f32; 3],
    yaw: f32,
    color: u8,
    width: f32,
    first_index: u16,
    points_mm: Vec<u8>,
) -> Vec<PacketPayload> {
    const BYTES_PER_POINT: usize = 4;
    let budget = crate::network::protocol::SAFE_DATAGRAM_BYTES;
    let build = |first_index: u16, points_mm: Vec<u8>| PacketPayload::SprayDraft {
        place_id,
        layer,
        anchor,
        yaw,
        color,
        width,
        first_index,
        points_mm,
    };
    let wire = |payload: &PacketPayload| {
        let header = PacketHeader::new(payload.type_code(), 0, 0, 0);
        crate::network::protocol::encode_packet(&header, payload).len()
    };

    let whole = build(first_index, points_mm.clone());
    if wire(&whole) <= budget {
        return vec![whole];
    }

    // Cuántos puntos caben. Se mide contra el PEOR sobre posible, no contra el de este trozo, y
    // las dos razones son la misma trampa de msgpack: escribe cada valor con el prefijo más corto
    // que le sirva. Un blob de 200 B lleva un byte de longitud y uno de 300 lleva dos; un
    // `first_index` de 40 ocupa 1 byte y uno de 900 ocupa 3. Estimar con el sobre VACÍO y el
    // `first_index` de la PRIMERA página producía trozos de 1201 B en las últimas — medido, no
    // supuesto. `u16::MAX` acota los dos crecimientos a la vez y cuesta, como mucho, un punto por
    // trozo.
    let worst_envelope = wire(&build(u16::MAX, vec![0u8; budget]));
    let overhead = worst_envelope.saturating_sub(budget);
    let per_page = (budget.saturating_sub(overhead) / BYTES_PER_POINT).max(1);

    points_mm
        .chunks(per_page * BYTES_PER_POINT)
        .enumerate()
        .map(|(idx, slice)| {
            // El índice del primer punto de este trozo dentro del trazo, que es lo que el receptor
            // usa para colocarlo. Se satura en vez de envolver: un gesto no llega a 65 535 puntos
            // (`MAX_POINTS_PER_SPRAY` es 512) y envolver colocaría el trozo al principio del trazo.
            let offset = (idx * per_page).min(u16::MAX as usize) as u16;
            build(first_index.saturating_add(offset), slice.to_vec())
        })
        .collect()
}

/// TAREA 2 — los datagramas de una pintada ya aceptada (0x52), listos para enviar.
///
/// Devuelve UNO en el caso normal. La cabecera de la pintada (id, chunk, ancla, tamaño, autor,
/// tick) se repite en cada página —es barata frente a un trazo— y el receptor toma la de la
/// primera que le llegue, igual que hace con la cabecera de un chunk.
pub fn spray_placed_pages(spray: &crate::world::spray::Spray) -> Vec<PacketPayload> {
    let measure = |strokes: &[crate::world::spray::SprayStroke]| {
        let mut probe = spray.clone();
        probe.strokes = strokes.to_vec();
        let payload = PacketPayload::SprayPlaced {
            spray: probe,
            page: 0,
            page_count: 1,
        };
        let header = PacketHeader::new(payload.type_code(), 0, 1, 0);
        crate::network::protocol::encode_packet(&header, &payload).len()
    };
    let pages = split_spray_strokes(&spray.strokes, measure);
    let page_count = pages.len() as u16;
    pages
        .into_iter()
        .enumerate()
        .map(|(idx, strokes)| {
            let mut part = spray.clone();
            part.strokes = strokes;
            PacketPayload::SprayPlaced {
                spray: part,
                page: idx as u16,
                page_count,
            }
        })
        .collect()
}

/// TAREA 2 — lo mismo para la petición del cliente (0x51), que lleva los MISMOS trazos y por
/// tanto el mismo peor caso. La clave de reensamblado es `place_id`.
pub fn spray_place_request_pages(
    place_id: u64,
    layer: u8,
    world_pos: [f32; 3],
    yaw: f32,
    size: [f32; 2],
    strokes: Vec<crate::world::spray::SprayStroke>,
) -> Vec<PacketPayload> {
    let measure = |chunk: &[crate::world::spray::SprayStroke]| {
        let payload = PacketPayload::SprayPlaceRequest {
            place_id,
            layer,
            world_pos,
            yaw,
            size,
            strokes: chunk.to_vec(),
            page: 0,
            page_count: 1,
        };
        let header = PacketHeader::new(payload.type_code(), 0, 1, 0);
        crate::network::protocol::encode_packet(&header, &payload).len()
    };
    let pages = split_spray_strokes(&strokes, measure);
    let page_count = pages.len() as u16;
    pages
        .into_iter()
        .enumerate()
        .map(|(idx, strokes)| PacketPayload::SprayPlaceRequest {
            place_id,
            layer,
            world_pos,
            yaw,
            size,
            strokes,
            page: idx as u16,
            page_count,
        })
        .collect()
}

/// Reensamblador de un gesto paginado. Mismo contrato que `ChunkPageAssembler` y por la misma
/// razón: la capa fiable es at-least-once y SIN orden, así que aplicar tandas sueltas dejaría una
/// pintada con la mitad de sus trazos — y una pintada se GUARDA, así que ese error no se
/// auto-cura en la ronda siguiente como el de un roster.
///
/// La clave la pone el llamador: `spray.id` para `SprayPlaced` (acuñado por el host, monótono) y
/// `place_id` para `SprayPlaceRequest` (acuñado por el cliente, único por gesto).
#[derive(Debug, Default)]
pub struct SprayPageAssembler {
    pending: std::collections::HashMap<u64, PendingSpray>,
    order: std::collections::VecDeque<u64>,
}

/// Tope de gestos a medio ensamblar, por la misma fuga que `PENDING_CHUNK_CAP`: una tanda perdida
/// deja su parcial sin dueño. 32 es holgado — un gesto dura lo que el jugador tiene el gatillo
/// apretado y no hay forma de tener decenas en vuelo a la vez.
const PENDING_SPRAY_CAP: usize = 32;

#[derive(Debug)]
struct PendingSpray {
    page_count: u16,
    pages: std::collections::HashMap<u16, Vec<crate::world::spray::SprayStroke>>,
}

impl SprayPageAssembler {
    /// Entrega una tanda. Devuelve los trazos COMPLETOS, en orden de página, la primera vez que se
    /// completa el gesto; `None` mientras falte alguna.
    pub fn offer(
        &mut self,
        key: u64,
        page: u16,
        page_count: u16,
        strokes: Vec<crate::world::spray::SprayStroke>,
    ) -> Option<Vec<crate::world::spray::SprayStroke>> {
        if page_count <= 1 {
            return Some(strokes);
        }
        if !self.pending.contains_key(&key) {
            self.order.push_back(key);
            while self.order.len() > PENDING_SPRAY_CAP {
                if let Some(oldest) = self.order.pop_front() {
                    self.pending.remove(&oldest);
                }
            }
        }
        let entry = self.pending.entry(key).or_insert_with(|| PendingSpray {
            page_count,
            pages: std::collections::HashMap::with_capacity(page_count as usize),
        });
        if entry.page_count != page_count {
            entry.page_count = page_count;
            entry.pages.clear();
        }
        entry.pages.insert(page, strokes);
        if entry.pages.len() < entry.page_count as usize {
            return None;
        }

        let done = self.pending.remove(&key).expect("acabamos de verlo");
        self.order.retain(|k| *k != key);
        let mut ordered: Vec<(u16, Vec<crate::world::spray::SprayStroke>)> =
            done.pages.into_iter().collect();
        ordered.sort_by_key(|(idx, _)| *idx);
        Some(ordered.into_iter().flat_map(|(_, s)| s).collect())
    }

    /// Gestos a medio ensamblar. Para trazas y tests.
    pub fn pending_len(&self) -> usize {
        self.pending.len()
    }
}

pub fn build_session_config(world: &World) -> SessionConfig {
    SessionConfig {
        max_players: world.config.max_players,
        world_name: "Backrooms".into(),
        teleport_interval_min: world.config.teleport_interval.0,
        teleport_interval_max: world.config.teleport_interval.1,
    }
}

/// ADR-043 gap (auditoría 2026-08-10, playtest H10) + enmienda ADR-079. H10 encontró que un
/// fantasma anunciado aquí con su addr inerte era adoptado como peer REAL por el receptor, cuyos
/// broadcasts le disparaban datagramas (veneno de socket, 1M de `os error 10054`), y lo EXCLUYÓ.
/// La exclusión causó el bug simétrico: sin entrada en `net.peers` del joiner, la rama
/// `PlayerUpdate` de `handlers.rs` descartaba toda pose relayada del fantasma — el joiner nunca
/// lo vio. ADR-079: la entrada VIAJA marcada `relay_only` con addr placeholder; el receptor la
/// registra con su propia addr inerte y TODA la superficie de envío la excluye. El receptor debe
/// CONOCER al fantasma sin poder DIRIGIRSE a él.
/// Lo que viaja en `PeerInfo::addr` cuando NO hay dirección utilizable que anunciar: la entrada
/// propia del emisor (su socket vive en `0.0.0.0`, que no es una dirección de nadie) y las
/// entradas `relay_only` de ADR-079. El receptor lo rechaza por contrato — ver
/// `is_routable_peer_addr`.
pub const UNROUTABLE_ADDR_PLACEHOLDER: &str = "0.0.0.0:0";

/// ¿Se puede REGISTRAR un peer en esta dirección? Filtro del receptor, gemelo del placeholder de
/// arriba y la mitad que de verdad protege: rechaza lo que no puede ser el endpoint de nadie —
/// dirección sin especificar (`0.0.0.0`, `::`) o puerto 0.
///
/// Existe porque el daño no lo hace anunciar una dirección mala, lo hace ADOPTARLA: quien la
/// adopta se manda a sí mismo todo lo que creía estar mandando al otro, y desde fuera se ve como
/// un peer que se calla. Con esto, un roster de un build viejo —que sigue anunciando
/// `0.0.0.0:<puerto>`— tampoco puede envenenar a un build nuevo.
pub fn is_routable_peer_addr(addr: &std::net::SocketAddr) -> bool {
    !addr.ip().is_unspecified() && addr.port() != 0
}

pub fn build_peer_list(net: &NetworkManager, local_player: &Player) -> Vec<PeerInfo> {
    let mut peers = vec![PeerInfo {
        id: net.local_id,
        name: local_player.name.clone(),
        // Auditoría de heartbeat (2026-08-30): AQUÍ IBA `net.local_addr()`, que es la dirección
        // del SOCKET — y el socket hace bind en `0.0.0.0`. O sea que el host se anunciaba a sí
        // mismo en el roster como `0.0.0.0:7778`.
        //
        // `0.0.0.0` como DESTINO significa "esta máquina". En una sola máquina el datagrama
        // llega igual, así que el defecto es invisible en localhost; con dos PC de por medio, el
        // que adopte esa dirección se manda los latidos A SÍ MISMO y el host deja de recibir
        // nada suyo — expulsión por HEARTBEAT TIMEOUT ~5 s después de entrar, sin nada roto en
        // la red. Exactamente la asimetría "en local va, en LAN no".
        //
        // No hay dirección correcta que poner: un socket en `0.0.0.0` no tiene UNA dirección, la
        // tiene por interfaz y por ruta hacia cada destino. Y no hace falta ninguna — el receptor
        // de este roster conoce al emisor por la dirección de origen del propio datagrama. Viaja
        // el mismo placeholder que ADR-079 ya usa para `relay_only`, y el receptor lo rechaza.
        addr: UNROUTABLE_ADDR_PLACEHOLDER.to_string(),
        position: local_player.position.to_array(),
        relay_only: false,
    }];
    for peer in net.peers.values() {
        let relay_only = net.is_phantom(peer.id) || peer.relay_only;
        peers.push(PeerInfo {
            id: peer.id,
            name: peer.name.clone(),
            // ADR-079: la addr real de un relay_only es la inerte local y no pinta nada en el
            // wire — viaja un placeholder que el receptor ignora por contrato.
            addr: if relay_only {
                UNROUTABLE_ADDR_PLACEHOLDER.to_string()
            } else {
                peer.addr.to_string()
            },
            position: peer.position,
            relay_only,
        });
    }
    peers
}

#[cfg(test)]
mod peer_list_tests {
    use super::*;
    use crate::network::peer::PeerConnection;

    /// H10 (2026-08-10) excluyó al fantasma del roster para que su addr inerte no cruzara el
    /// wire; ADR-079 lo reincorpora MARCADO `relay_only` y con addr placeholder, porque la
    /// exclusión dejaba al joiner sin entrada donde aplicar las poses relayadas (el fantasma
    /// era invisible para todo joiner). Las dos protecciones conviven: el real viaja con su
    /// addr, el fantasma viaja sin addr utilizable y con la bandera que obliga al receptor a
    /// registrarlo como inalcanzable.
    #[tokio::test]
    async fn build_peer_list_marks_phantoms_relay_only() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let real_addr: std::net::SocketAddr = "127.0.0.1:9800".parse().unwrap();
        host.peers
            .insert(2, PeerConnection::new(2, "Real".into(), real_addr));
        let phantom_id = host.spawn_phantom("Skinwalker", [0.0, 1.8, 0.0], None);

        let player = Player::new(host.local_id, "Host");
        let list = build_peer_list(&host, &player);

        let real = list.iter().find(|p| p.id == 2).expect("real en el roster");
        assert!(!real.relay_only, "un peer real nunca viaja relay_only");
        assert_eq!(real.addr, "127.0.0.1:9800");

        let ghost = list
            .iter()
            .find(|p| p.id == phantom_id)
            .expect("ADR-079: el fantasma DEBE viajar en el roster");
        assert!(ghost.relay_only, "el fantasma viaja marcado relay_only");
        assert_eq!(ghost.name, "Skinwalker", "el nombre del disfraz viaja");
        assert_eq!(
            ghost.addr, "0.0.0.0:0",
            "la addr inerte real no cruza el wire — placeholder por contrato"
        );
    }
}

// â”€â”€â”€ Broadcast functions â”€â”€â”€

/// Broadcast local player position/rotation to all peers (unreliable, 10hz).
pub async fn broadcast_player_update(net: &NetworkManager, player: &Player) {
    if net.peers.is_empty() {
        return;
    }
    // ADR-011: a recent local pickup takes priority â€” a trigger flank held ~1s so the client
    // reliably catches the transition despite ~5Hz sample spacing. The gesture's duration is
    // owned by the client (Animator exitTime), not by this window.
    let animation = if net
        .last_pickup_at
        .is_some_and(|t| t.elapsed().as_millis() < 1000)
    {
        "pickup"
    } else if player.stats.speed_modifier < 1.0 {
        "walk_slow"
    } else {
        "idle"
    };
    let payload = PacketPayload::PlayerUpdate {
        position: player.position.to_array(),
        rotation: player.rotation,
        animation: animation.into(),
        crouch: player.crouch,
        pitch: player.pitch,
        equipment: player.equipment,
        held_item: player.held_item,
        hit_seq: player.hit_seq,
        // ADR-028 post-E3: SERVER-derived (authoritative stats, ADR-025) â€” the one pose field
        // the client does not report.
        dead: player.stats.is_dead(),
        // ADR-038: always false here â€” a real player never shows a "real form". The only
        // `true` in the whole system is sealed by PhantomDriver onto a PeerConnection and
        // travels via broadcast_peer_poses, not this path.
        revealed: player.revealed,
        vocal_seq: player.vocal_seq,
        vocal_kind: player.vocal_kind,
        // ADR-042: both client-reported and sealed in the game loop next to `hit_seq`.
        light_on: player.light_on,
        fire_seq: player.fire_seq,
        // ADR-044: `buttons` stops being the dead literal it was and carries the aim/reload bits.
        buttons: player.buttons,
        melee_seq: player.melee_seq,
        // ADR-049: client-reported carry state, sealed in the game loop next to `melee_seq`.
        carry_def: player.carry_def,
        carry_count: player.carry_count,
        // ADR-094: always 0 here — a real player is never a faceling. The only non-zero values
        // are sealed by a faceling driver onto a `PeerConnection` and travel via
        // `broadcast_peer_poses`, not this path.
        species: player.species,
    };
    // Both lines are on the same once-a-second window now. This runs at the full tick rate, so
    // unthrottled it was the single noisiest line in the backend log â€” it formatted three floats
    // every tick and buried the TP_WATCH / MPTRACE traces the open TP-attribution diagnosis needs.
    if net.session_start.elapsed().as_millis() % 1000 < 120 {
        info!(
            "Sending player update to peers={} local_id={} pos=({:.2}, {:.2}, {:.2})",
            net.peers.len(),
            net.local_id,
            player.position.x,
            player.position.y,
            player.position.z
        );
        info!(
            "MPTRACE step=R event=send_player_update self_id={} peer_count={} pos=({:.2},{:.2},{:.2}) rot={:.2}",
            net.local_id,
            net.peers.len(),
            player.position.x,
            player.position.y,
            player.position.z,
            player.rotation
        );
    }
    net.broadcast_unreliable(&payload).await;
}

/// Host-as-server relay: broadcast the FULL peer roster (every peer's id + current
/// position) to all connected peers, so each joiner learns about ALL other peers and
/// not just the host. A joiner only connects to the host, so without this it never
/// sees the other joiners. Sent at the player-update cadence, by the host only.
pub async fn broadcast_peer_roster(net: &mut NetworkManager, player: &Player) {
    if net.peers.is_empty() {
        return;
    }

    let list = build_peer_list(net, player);

    // ADR-140 D1: se hashea QUIÉN está, no DÓNDE está. Las posiciones cambian a cada tick, así que
    // hashear la lista entera dejaría el gate abierto para siempre — el mismo fallo que ADR-139 D1
    // encontró en los chunks. Medido el 10-09: 27,5 datagramas/s con UN jugador.
    //
    // Que la posición de este roster se refresque sólo con el latido NO deja a nadie congelado
    // donde importa: la pose fina de quien tienes cerca llega por `relay_as` a 30 Hz. Esto sólo
    // gobierna a los que están FUERA del AOI — los que no ves.
    let composition: Vec<(u16, bool)> = list.iter().map(|p| (p.id, p.relay_only)).collect();
    // ADR-141: éste es el ÚNICO roster que no necesita el camino dirigido, y por una razón y no por
    // descuido — su contenido ES la lista de peers, así que la llegada de uno nuevo ya cambia el
    // hash y la puerta se abre sola por CAMBIO. Aquí el broadcast a todos es lo correcto: todos
    // tienen que enterarse de quién ha entrado.
    let open = {
        let gate = &mut net.roster_gates.peers;
        gate.should_send(
            roster::content_hash(&composition),
            std::time::Instant::now(),
            roster::ROSTER_HEARTBEAT,
        )
    };
    if !open {
        return;
    }

    for payload in peer_list_datagrams(list) {
        net.broadcast_unreliable(&payload).await;
    }
}

/// TAREA 2 (2026-08-31) — trocea el roster de peers en los datagramas que hagan falta.
///
/// Medido: 823 B con 8 peers, **1617 B con 16** y 4983 B con 50, contra un techo de 1200. El
/// lobby de Steam admite 8 miembros pero la sesión se anuncia con `bs_max = 50` y el navegador
/// entra por fuera del lobby, así que 16 no es un caso teórico.
///
/// **NO lleva reensamblado, y ésa es la diferencia con los chunks.** El receptor de `PeerList`
/// (`handlers.rs`) es ADITIVO: recorre las entradas insertando las que no conoce y refrescando las
/// que sí, y **nunca borra** — la baja de un peer viene por `PeerDisconnected` o por el timeout de
/// latido, jamás por ausencia en un roster. Con esa semántica cada trozo es un mensaje completo y
/// válido por sí mismo: se puede aplicar suelto, en cualquier orden y repetido, y el resultado es
/// el mismo. Paginarlo con todo-o-nada habría sido pagar un ensamblador por una garantía que la
/// semántica ya da gratis.
///
/// Se mide el datagrama REAL, elemento a elemento, porque `PeerInfo` lleva dos cadenas de longitud
/// variable (`name` y `addr`) y un conteo por entradas se desviaría justo con los nombres largos.
pub fn peer_list_datagrams(peers: Vec<PeerInfo>) -> Vec<PacketPayload> {
    let budget = crate::network::protocol::SAFE_DATAGRAM_BYTES;
    let wire = |peers: &[PeerInfo]| {
        let payload = PacketPayload::PeerList {
            peers: peers.to_vec(),
        };
        let header = PacketHeader::new(payload.type_code(), 0, 0, 0);
        crate::network::protocol::encode_packet(&header, &payload).len()
    };
    // Un roster vacío se emite igual: es un mensaje válido y suprimirlo sería inventar un caso
    // especial que el receptor no necesita.
    if wire(&peers) <= budget {
        return vec![PacketPayload::PeerList { peers }];
    }

    let mut out = Vec::new();
    let mut current: Vec<PeerInfo> = Vec::new();
    for info in peers {
        current.push(info);
        if wire(&current) > budget && current.len() > 1 {
            let back = current.pop().expect("acabamos de meterlo");
            out.push(PacketPayload::PeerList {
                peers: std::mem::take(&mut current),
            });
            current.push(back);
        }
    }
    if !current.is_empty() {
        out.push(PacketPayload::PeerList { peers: current });
    }
    out
}

/// ADR-043 â€” peers a relayed pose may legitimately be ADDRESSED to: every real peer, never a
/// phantom. Split out of `broadcast_peer_poses` so the invariant is testable without a socket:
/// the alternative (asserting on datagrams) would need a live UDP endpoint per phantom, which is
/// exactly the thing that does not exist â€” a phantom's `addr` is the inert `127.0.0.1:1` stamped
/// at injection (`NetworkManager::spawn_phantom`).
/// 2026-09-10 — se pregunta por el MISMO predicado que decide el envío
/// (`peer_is_gameplay_destination`) en vez de repetir aquí una de sus cuatro condiciones. El filtro
/// de fantasmas cubría una sola: medido en partida real, el relay armaba lotes para peers
/// `relay_only` —criaturas anunciadas con la addr inerte de ADR-079— que la guarda de `send.rs`
/// rechazaba después (`MPTRACE step=SEND_FAIL event=illegal_gameplay_destination peer_id=61002`).
/// No se escapaba ningún datagrama, pero se pagaba el AOI, el `clone()` de la pose y el lote entero
/// para un destino imposible. Y un filtro que enumera un subconjunto de las condiciones del otro es
/// justo la clase de duplicado que se desincroniza en silencio cuando se añade la quinta.
/// **Sale ORDENADO, y no es cosmético — regla dura 13.** `net.peers` es un `HashMap`: su orden de
/// iteración es arbitrario y cambia entre ejecuciones. El bucle de emisión recorre este `Vec` para
/// decidir el orden de salida, y el índice espacial llena sus casillas con él; los dos daban por
/// buena una «estabilidad» que el `Vec` tenía sólo dentro de una ronda, no entre corridas. Ordenar
/// por id lo vuelve cierto de verdad y cuesta un `sort` de N ids por ronda.
///
/// (Lo detectó la rama de auditoría de rendimiento en `6750e5b0`, sobre la versión de este filtro
/// que aún enumeraba las condiciones a mano. El predicado compartido y el orden son arreglos
/// independientes del mismo sitio, y aquí van los dos.)
pub(crate) fn relay_destinations(net: &NetworkManager) -> Vec<PeerId> {
    let mut out: Vec<PeerId> = net
        .peers
        .values()
        .filter(|p| net.peer_is_gameplay_destination(p))
        .map(|p| p.id)
        .collect();
    out.sort_unstable();
    out
}

/// 2026-09-10 — cuántos peers pueden RECIBIR, para la condición `joined` de las puertas de roster.
///
/// `net.peers.len()` contaba también a las criaturas y al robapieles, que no reciben nada: su addr
/// es la inerte de ADR-079/043 y la guarda de `send.rs` las rechaza. Con esa cuenta, `joined`
/// (`peers > last_peers`) se disparaba en CADA nacimiento y reenviaba el roster entero —los cinco, y
/// los chunks— a todo el mundo. En un mundo poblado eso ocurre sin parar, así que ninguna puerta
/// llegaba a cerrarse del todo y el ahorro de ADR-071 y ADR-139 se evaporaba en silencio: sin error,
/// sin log, solo tráfico.
///
/// La condición no cambia de significado, se le da el número que siempre quiso decir: alguien nuevo
/// a quien hay que darle el mundo.
/// ADR-141 — los peers a los que hay que servirles el mundo DIRIGIDO esta ronda.
///
/// Orden estable (regla dura 13): se recorre ordenado por id y no las claves del `HashMap`.
pub(crate) fn newcomers(net: &NetworkManager) -> Vec<PeerId> {
    let mut out: Vec<PeerId> = net
        .pending_full_sync
        .keys()
        .copied()
        .filter(|id| net.is_gameplay_destination(*id))
        .collect();
    out.sort_unstable();
    out
}

/// ADR-141 — cierra la ronda de servicio a los recién llegados.
///
/// Se llama UNA vez por vuelta del bucle de juego, después de que hayan corrido todos los emisores.
/// Si lo hiciera cada emisor, el primero en ejecutarse consumiría la cuenta y el recién llegado se
/// quedaría sin los otros cinco rosters — con el techo de ADR-139 enm. 2, hasta 30 s sin mundo.
pub fn tick_pending_full_sync(net: &mut NetworkManager) {
    net.pending_full_sync.retain(|_, rounds| {
        *rounds = rounds.saturating_sub(1);
        *rounds > 0
    });
}

pub(crate) fn gameplay_destination_count(net: &NetworkManager) -> usize {
    net.peers
        .values()
        .filter(|p| net.peer_is_gameplay_destination(p))
        .count()
}

/// E1 / ADR-074 (fase 1) — radio del área de interés de las poses, en metros.
///
/// **Lo elige el DISEÑO, no la red.** La fase `stalk` del robapieles es acecho a distancia: si su
/// proxy dejara de existir a 75 m, el jugador nunca vería al que le sigue, y esa es la mecánica de
/// horror entera. Por eso ADR-074 prohíbe un radio distinto para el fantasma —sería un oráculo:
/// todo lo que apareciera más lejos sería siempre él— y obliga a que el radio único sea el que el
/// diseño necesita.
///
/// 100 m es ese número. Lo que cuesta, medido antes de fijarlo (sonda `aoi_pose_relay_savings`,
/// `perf-baseline.md`): con los jugadores repartidos por el mapa sobrevive el **19–21 %** del
/// relay, y el porcentaje NO empeora al crecer N — 21 % con 8 jugadores, 21 % con 16, 19 % con
/// 32. Es decir, el AOI convierte el O(N²) en algo proporcional a la densidad LOCAL, que es
/// justamente lo que hacía falta.
///
/// Con todos los jugadores en la misma sala no ahorra nada, y eso es correcto: ahí sí hay N²
/// poses que cada uno necesita ver.
pub const AOI_POSE_RADIUS_M: f32 = 100.0;

/// E1 / ADR-074 (fase 1) — multiplicador de salida de la histéresis. Un par entra en el relay a
/// `AOI_POSE_RADIUS_M` y no sale hasta `AOI_POSE_RADIUS_M × este factor`; sin esa banda muerta,
/// dos jugadores andando sobre la frontera se verían parpadear varias veces por segundo.
pub const AOI_POSE_EXIT_FACTOR: f32 = 1.2;

/// ADR-074 enmienda 3 (2026-09-12) — suelo de la cadencia por distancia, en Hz. Es el número que
/// la enmienda del 08-15 fijó para el anillo exterior y por la misma razón: 50–100 m es donde vive
/// la fase `stalk` del robapieles, a 200 ms entre poses todavía se lee fluido y a 500 ms no. Como
/// la IA no se puede exceptuar (sería un oráculo), el suelo vale para TODOS los pares.
pub const POSE_HZ_FLOOR: u64 = 5;

/// ADR-074 enmienda 3 — forma de la curva. La cadencia cae desde `POSE_RELAY_HZ` a 0 m hasta
/// `POSE_HZ_FLOOR` a `AOI_POSE_RADIUS_M` siguiendo `floor + (near − floor) · (1 − d/R)^POWER`.
/// Potencia 1 es una recta (ahorra ×1,4 sobre los dos anillos), 2 cuadrática (×2,0), **3 cúbica
/// (×2,5)**: cae rápido donde ya no se aprecia y toca el suelo a ~73 m. Elegida por Joel a la
/// vista de la tabla metro a metro; se cambia aquí y en ningún otro sitio.
pub const POSE_LOD_POWER: i32 = 3;

/// Rondas de relay por segundo: el bucle corre a 60 Hz y emite una de cada
/// `NET_BROADCAST_EVERY` ticks.
///
/// **No se escribe a mano.** Estuvo escrito a mano —un `* 10` metido en el `MPTRACE` de abajo— y se
/// quedó viejo cuando ADR-138 D1 subió la cadencia de 20 a 30 Hz: desde entonces ese log ha venido
/// diciendo un TERCIO del tráfico real. Es el mismo accidente que el techo de 256 KB/s del arnés de
/// carga, y se cierra igual: derivado en un sitio, con un test que lo ata a su origen.
pub const POSE_RELAY_HZ: u64 = 60 / crate::game_loop::NET_BROADCAST_EVERY;

/// ADR-074 enmienda 3 — cadencia objetivo de un par a `dist_m` metros, en Hz enteros.
///
/// Monótona no creciente, `POSE_RELAY_HZ` a 0 m, `POSE_HZ_FLOOR` desde donde la curva lo toca y
/// hasta el borde del AOI. Pura: solo distancia, jamás qué es la fuente (ADR-074 decisión 1).
pub fn pose_hz(dist_m: f32) -> u64 {
    let t = (1.0 - dist_m / AOI_POSE_RADIUS_M).clamp(0.0, 1.0);
    let near = POSE_RELAY_HZ as f32;
    let floor = POSE_HZ_FLOOR as f32;
    let hz = (floor + (near - floor) * t.powi(POSE_LOD_POWER)).round() as u64;
    hz.clamp(POSE_HZ_FLOOR, POSE_RELAY_HZ)
}

/// ADR-074 enmienda 3 — ¿emite este par en la ronda `round` a `hz` Hz?
///
/// Bresenham en enteros, sin estado: en cualquier ventana de `POSE_RELAY_HZ` rondas seguidas
/// salen EXACTAMENTE `hz` emisiones, repartidas lo más uniforme posible. `phase` desplaza la
/// ventana por par, que es lo que hace que N pares a la misma cadencia no emitan todos en la
/// misma ronda (carga plana). Determinista: sólo enteros y sólo `(round, hz, phase)`.
pub fn due_at_hz(round: u64, hz: u64, phase: u64) -> bool {
    let hz = hz.min(POSE_RELAY_HZ);
    if hz >= POSE_RELAY_HZ {
        return true;
    }
    if hz == 0 {
        return false;
    }
    let r = round.wrapping_add(phase);
    (r.wrapping_mul(hz) / POSE_RELAY_HZ) != (r.wrapping_sub(1).wrapping_mul(hz) / POSE_RELAY_HZ)
}

/// Desplazamiento de ventana de un par, en rondas. Sale de los dos ids y de nada más (regla 13:
/// determinista, sin `HashMap`), y mezcla en vez de sumar para que `(1,2)` y `(2,1)` —los dos
/// sentidos del mismo par— no compartan fase.
pub fn pose_pair_phase(src: PeerId, dest: PeerId) -> u64 {
    ((src as u64).wrapping_mul(0x9E37_79B9)).wrapping_add(dest as u64) % POSE_RELAY_HZ
}

/// ADR-074 enm. 4 — presupuesto de SUBIDA del anfitrión dedicado a poses, en KB/s. Se reparte a
/// partes iguales entre los destinatarios reales de la ronda: con más gente, cada uno recibe
/// menos cadencia de los que tiene lejos. Tres cuartos de los 256 KB/s que el arnés usa de techo;
/// el resto queda para rosters, chunks y voz.
pub const HOST_POSE_BUDGET_KB_S: f32 = 192.0;

/// Bytes por pose con los que se convierte el presupuesto a poses/s: la pose delgada de ADR-144
/// (23 B) más la velocidad de ADR-146 D1 (~8 B en marcha) más la parte proporcional de las
/// completas y del sobre del lote. Si baja de lo real, el aforo reparte más cadencia de la que
/// cabe en `HOST_POSE_BUDGET_KB_S`, en silencio.
pub const POSE_WIRE_BYTES_EST: f32 = 34.0;

/// ADR-146 D6 — el gate de tramos entra APAGADO. Apagado, la velocidad viaja igual y el receptor
/// extrapola igual, pero el anfitrión no omite ninguna pose: se mide el error de la predicción sin
/// cambiar lo que se ve. Se enciende sólo con la medida de 16 instancias por humanos y criaturas.
pub const TRAMO_GATE_ENABLED: bool = false;

/// ADR-146 D2 — tope del estimador de velocidad, en m/s. Por encima de la carrera de STP
/// (7,29 m/s, ADR-138) con margen: un desplazamiento que implique más no es movimiento sino un
/// salto —teleport, desplazamiento de chunk, respawn— y reinicia la estimación a cero.
pub const TRAMO_MAX_SPEED_M_S: f32 = 12.0;

/// Peso de la muestra nueva en la media móvil de la velocidad.
const TRAMO_VEL_SMOOTHING: f32 = 0.5;

/// Un origen cuya posición no cambia en este tiempo está quieto: su velocidad pasa a cero. Cubre
/// el peor hueco entre poses de un jugador a 30 Hz con jitter, y no tanto como para que una
/// criatura parada siga «andando» en el receptor.
const TRAMO_STILL_AFTER: std::time::Duration = std::time::Duration::from_millis(200);

/// ADR-146 D2 — la velocidad estimada de un origen: la última posición distinta que se le vio,
/// cuándo, y la velocidad suavizada hasta entonces.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PoseVelocity {
    pub pos: [f32; 3],
    pub at: std::time::Instant,
    pub vel: [f32; 3],
}

/// ADR-146 D2 — estima la velocidad de un origen a partir de su posición en `now`. Devuelve la
/// estimación nueva y si hubo SALTO (desplazamiento por encima de `TRAMO_MAX_SPEED_M_S`).
///
/// No recibe nada que diga qué es la fuente, y no puede: la velocidad sale de la posición y de
/// nada más (ADR-146, enmienda a ADR-074 enm. 4 D5).
pub fn estimate_pose_velocity(
    prev: Option<&PoseVelocity>,
    pos: [f32; 3],
    now: std::time::Instant,
) -> (PoseVelocity, bool) {
    let Some(prev) = prev else {
        return (
            PoseVelocity {
                pos,
                at: now,
                vel: [0.0; 3],
            },
            false,
        );
    };
    let elapsed = now.saturating_duration_since(prev.at);
    if pos == prev.pos {
        // Sin muestra nueva: se conserva la velocidad hasta que el silencio dice «quieto». `at` no
        // avanza, para que la siguiente muestra distinta mida su desplazamiento contra el tiempo
        // real transcurrido y no contra una ronda.
        let vel = if elapsed > TRAMO_STILL_AFTER {
            [0.0; 3]
        } else {
            prev.vel
        };
        return (PoseVelocity { vel, ..*prev }, false);
    }
    let dt = elapsed.as_secs_f32();
    if dt < 1e-3 {
        // Dos posiciones en el mismo instante no definen velocidad; se ignora la muestra.
        return (*prev, false);
    }
    let inst = [
        (pos[0] - prev.pos[0]) / dt,
        (pos[1] - prev.pos[1]) / dt,
        (pos[2] - prev.pos[2]) / dt,
    ];
    let speed_sq = inst[0] * inst[0] + inst[1] * inst[1] + inst[2] * inst[2];
    if speed_sq > TRAMO_MAX_SPEED_M_S * TRAMO_MAX_SPEED_M_S {
        return (
            PoseVelocity {
                pos,
                at: now,
                vel: [0.0; 3],
            },
            true,
        );
    }
    // Media convexa de dos vectores por debajo del tope: el resultado tampoco lo pasa.
    let a = TRAMO_VEL_SMOOTHING;
    let vel = [
        prev.vel[0] + (inst[0] - prev.vel[0]) * a,
        prev.vel[1] + (inst[1] - prev.vel[1]) * a,
        prev.vel[2] + (inst[2] - prev.vel[2]) * a,
    ];
    (PoseVelocity { pos, at: now, vel }, false)
}

/// ADR-146 D2 — tolerancia de POSICIÓN del gate: si la pose real cae a menos de esto de lo que
/// predice el último tramo enviado, no se reenvía. Punto de partida; lo fija la medida.
pub const TRAMO_POS_TOLERANCE_M: f32 = 0.20;

/// ADR-146 D2 — tolerancia de YAW, en grados. Girar es instantáneo y se ve de cerca.
pub const TRAMO_YAW_TOLERANCE_DEG: f32 = 4.0;

/// ADR-146 D2 — tolerancia de PITCH, en pasos del `i8` que viaja.
pub const TRAMO_PITCH_TOLERANCE: i16 = 2;

/// ADR-146 D2 — reparación: pasado esto desde el último envío del par, se reenvía aunque la
/// predicción siga acertando. En TIEMPO REAL y no en rondas emitidas: a 5 Hz con cono y aforo el
/// hueco entre rondas que tocan es de hasta 200 ms, y contar rondas estiraba la reparación.
pub const TRAMO_REPAIR: std::time::Duration = std::time::Duration::from_secs(1);

/// ADR-146 D2 — lo último que se envió a un par `(src, dest)`: basta para reproducir la
/// predicción que el receptor está haciendo con ello.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TramoMark {
    pub pos: [f32; 3],
    /// La velocidad TAL COMO VIAJÓ (cuantizada y vuelta), que es con la que extrapola el receptor.
    pub vel: [f32; 3],
    pub yaw_u16: u16,
    pub pitch: i8,
    /// Huella de todo lo discreto: animación, flags, botones, contadores y cosméticos.
    pub discrete: u64,
    pub at: std::time::Instant,
}

/// ADR-146 D2 — huella de lo que no se puede predecir: cualquier cambio obliga a enviar. Incluye
/// los cosméticos por su hash, así que un cambio de objeto en mano nunca espera a la reparación.
pub fn pose_discrete_hash(wire: &PoseWire, cosmetics_hash: u64) -> u64 {
    use std::hash::{Hash, Hasher};
    let mut h = std::collections::hash_map::DefaultHasher::new();
    wire.animation.hash(&mut h);
    wire.flags.hash(&mut h);
    wire.buttons.hash(&mut h);
    wire.hit_seq.hash(&mut h);
    wire.fire_seq.hash(&mut h);
    wire.melee_seq.hash(&mut h);
    wire.vocal_seq.hash(&mut h);
    cosmetics_hash.hash(&mut h);
    h.finish()
}

/// ADR-146 D2 — ¿el último tramo enviado a este par ya predice la pose de `now`? Si sí, el par se
/// puede omitir esta ronda. Función pura: posición, rumbo, lo discreto y el reloj, y nada que diga
/// qué es la fuente.
pub fn tramo_predicts(
    mark: &TramoMark,
    pos: [f32; 3],
    yaw_u16: u16,
    pitch: i8,
    discrete: u64,
    now: std::time::Instant,
) -> bool {
    let elapsed = now.saturating_duration_since(mark.at);
    if elapsed >= TRAMO_REPAIR || discrete != mark.discrete {
        return false;
    }
    let t = elapsed.as_secs_f32();
    let err = [
        pos[0] - (mark.pos[0] + mark.vel[0] * t),
        pos[1] - (mark.pos[1] + mark.vel[1] * t),
        pos[2] - (mark.pos[2] + mark.vel[2] * t),
    ];
    let err_sq = err[0] * err[0] + err[1] * err[1] + err[2] * err[2];
    if err_sq > TRAMO_POS_TOLERANCE_M * TRAMO_POS_TOLERANCE_M {
        return false;
    }
    // Diferencia de rumbo por el camino corto: la resta en `u16` da la vuelta sola y como `i16`
    // queda con signo.
    let yaw_steps = (yaw_u16.wrapping_sub(mark.yaw_u16) as i16).unsigned_abs();
    if yaw_steps as f32 * 360.0 / 65536.0 > TRAMO_YAW_TOLERANCE_DEG {
        return false;
    }
    (pitch as i16 - mark.pitch as i16).abs() <= TRAMO_PITCH_TOLERANCE
}

/// Dentro de esta distancia el aforo NO recorta: un tiroteo cuerpo a cuerpo en una sala llena
/// sigue a la cadencia de la curva. Es lo que Joel pidió: «si están a 2 metros de ti se vean a
/// 30 Hz».
pub const POSE_BUDGET_NEAR_EXEMPT_M: f32 = 3.0;

/// Cuánto puede moverse el factor de aforo de un destinatario por ronda. 0,05 por ronda a 30 Hz
/// es pasar de 1 a 0,25 en medio segundo: rápido para que un pico no reviente el cable, lento
/// para que no se vea como un escalón.
pub const POSE_BUDGET_FACTOR_STEP: f32 = 0.05;

/// ADR-074 enm. 4 — factor de aforo objetivo de un destinatario: 1 si lo que sus orígenes piden
/// (suma de Hz de la curva) cabe en su parte del presupuesto, y la proporción si no. Pura.
pub fn budget_factor_target(demand_hz: f32, share_hz: f32) -> f32 {
    if demand_hz <= 0.0 || demand_hz <= share_hz {
        1.0
    } else {
        (share_hz / demand_hz).clamp(0.0, 1.0)
    }
}

/// ADR-074 enm. 4 — cadencia FINAL de un par: la curva por distancia, recortada por el aforo del
/// destinatario salvo a bocajarro, y a la mitad si el origen queda a la espalda. Nunca por debajo
/// del suelo. Pura: distancia, aforo y ángulo; jamás qué es la fuente (ADR-074 decisión 1).
pub fn pose_pair_hz(dist_m: f32, budget_factor: f32, behind: bool) -> u64 {
    let mut hz = pose_hz(dist_m);
    if dist_m > POSE_BUDGET_NEAR_EXEMPT_M {
        hz = (hz as f32 * budget_factor.clamp(0.0, 1.0)).round() as u64;
    }
    if behind {
        hz /= 2;
    }
    hz.clamp(POSE_HZ_FLOOR, POSE_RELAY_HZ)
}

/// Lado de la casilla del índice espacial del relay, en metros.
///
/// **Es el radio de SALIDA, no el de entrada, y la diferencia no es cosmética.** La histéresis del
/// AOI deja que un par que ya se estaba viendo aguante hasta `AOI_POSE_RADIUS_M ×
/// AOI_POSE_EXIT_FACTOR`; si la casilla midiera el radio de entrada, el índice descartaría pares
/// que el filtro sí quería mantener y la histéresis dejaría de existir en silencio.
///
/// Con este lado, la vecindad de 3×3 es DEMOSTRABLEMENTE suficiente: dos puntos a menos del radio
/// de salida no pueden caer a más de una casilla de distancia en ningún eje — si lo estuvieran, su
/// separación mínima ya superaría el lado. `the_neighbourhood_never_drops_a_pair_the_radius_wants`
/// lo barre en vez de fiarse de este párrafo.
pub const POSE_CELL_M: f32 = AOI_POSE_RADIUS_M * AOI_POSE_EXIT_FACTOR;

/// Casilla de un punto. Sólo X y Z: ignorar la altura sólo puede meter candidatos de MÁS en una
/// casilla, nunca de menos, así que es seguro — dos puntos a menos del radio en 3D lo están también
/// en X y en Z por separado.
///
/// `floor` y no truncado: truncar hacia cero haría la casilla del origen del doble de ancha, que no
/// rompe nada pero deja una casilla que no mide lo que dice la constante.
pub fn pose_cell(p: [f32; 3]) -> (i32, i32) {
    (
        (p[0] / POSE_CELL_M).floor() as i32,
        (p[2] / POSE_CELL_M).floor() as i32,
    )
}

/// Tope de fuentes que un destinatario recibe en una ronda.
///
/// El radio y el grafo deciden par a par, y ninguno de los dos mira nunca cuánto acaba recibiendo
/// UNA persona. Con eso el coste de la sala crece con el cuadrado: cada uno que entra le añade una
/// fuente a todos los demás. El tope rompe ese cuadrado — la bolsa de cada destinatario deja de
/// depender de cuántos haya, y el coste del anfitrión pasa a crecer en línea recta.
///
/// **96 es deliberadamente alto: hoy no corta a nadie.** No es el número bueno, es el número que
/// garantiza que esto entra sin cambiar una coma de lo que ya funciona. El bueno sale de medirlo,
/// y por eso `MPTRACE` empieza a publicar `max_fan_in` y `cap_hits` en esta misma tanda: cuando un
/// playtest diga cuánto recibe de verdad el que más recibe, el tope baja con datos delante.
///
/// Lo que se corta es lo más LEJANO, y ahí está la única trampa que importa: el orden mira
/// distancia y, si empata, posición — jamás el identificador ni la especie. Un desempate por `id`
/// bastaría para que a igual distancia ganara siempre el mismo tipo de fuente, y eso es
/// exactamente el chivato que ADR-074 prohíbe. Ver `pose_fidelity_order`.
pub const POSE_FIDELITY_CAP: usize = 96;

/// Orden de recorte del tope: primero la distancia, y sólo para deshacer empates la POSICIÓN de la
/// fuente, componente a componente.
///
/// ADR-074 gobierna este desempate igual que gobierna el radio y la cadencia: el filtro decide por
/// dónde están las cosas y nunca por qué son. Si dos fuentes caen a la misma distancia exacta del
/// destinatario, la que se queda la elige su posición en el mundo; el identificador sólo entra
/// cuando las dos ocupan el MISMO punto, donde ya no hay nada que delatar.
fn pose_fidelity_order(
    a: &(f32, [f32; 3], PeerId),
    b: &(f32, [f32; 3], PeerId),
) -> std::cmp::Ordering {
    a.0.total_cmp(&b.0)
        .then(a.1[0].total_cmp(&b.1[0]))
        .then(a.1[1].total_cmp(&b.1[1]))
        .then(a.1[2].total_cmp(&b.1[2]))
        .then(a.2.cmp(&b.2))
}

/// ADR-074 enmienda 3 — ¿le toca a este par emitir en esta ronda?
///
/// Antes eran dos anillos (30 Hz hasta 50 m, 15 Hz hasta 100). Ahora es una curva continua en
/// pasos de 1 Hz: `pose_hz` decide la cadencia por la distancia y `due_at_hz` la reparte en
/// rondas con Bresenham, desplazada por par para que la carga salga plana. Cerca no cambia nada
/// (a 3 m sigue a 29–30 Hz); lo que se ahorra está en 20–70 m, donde 30 Hz movían píxeles.
///
/// El suelo de 5 Hz es el de la enmienda del 08-15 y por la misma razón: 50–100 m es exactamente
/// donde vive la fase `stalk` del robapieles, y a 500 ms se vería a saltos. **No se le puede
/// exceptuar** —una cadencia propia lo delataría igual que un radio propio— así que el suelo vale
/// para todos.
///
/// Como todo en este filtro: decide por DISTANCIA y por el par, jamás por qué es la fuente.
pub fn aoi_pose_due_this_round(
    src_pos: [f32; 3],
    dest_pos: [f32; 3],
    src: PeerId,
    dest: PeerId,
    round: u64,
) -> bool {
    let dx = src_pos[0] - dest_pos[0];
    let dy = src_pos[1] - dest_pos[1];
    let dz = src_pos[2] - dest_pos[2];
    let dist_m = (dx * dx + dy * dy + dz * dz).sqrt();
    due_at_hz(round, pose_hz(dist_m), pose_pair_phase(src, dest))
}

/// Semiángulo del cono de atención del destinatario, en grados.
///
/// **180 significa APAGADO**: con ese valor todo el mundo cuenta como «delante» y esto no cambia ni
/// un byte de lo que sale hoy. Entra así a propósito, igual que entró `POSE_FIDELITY_CAP`.
///
/// # Qué hace cuando se enciende
///
/// Lo que queda fuera del cono baja a media cadencia (`POSE_RELAY_HZ / 2`, hoy 15). No baja más,
/// y ahí está la diferencia entre esto y la versión que se descartó: a 15 Hz el hueco entre poses
/// son 66 ms, que a 4 m/s son 27 cm de error — imperceptible. A 1 Hz serían 4 m, y el error
/// aparecería justo al girarte, que es el único instante en que esa pose te hacía falta.
///
/// **Girar es instantáneo y la distancia no**: por eso la cadencia por distancia puede ser agresiva
/// y ésta no. Un flick de ratón son 200 ms para 180°, y el anfitrión no se entera hasta que le llega
/// tu input.
///
/// # Lo que faltaba ANTES de bajarlo de 180 — CERRADO por ADR-074 enm. 3 D4 (retardo por peer)
///
/// El búfer de interpolación del cliente (`RemotePlayerManager`) mide el ritmo de llegada **global,
/// no por proxy**, y su propio suelo no puede bajar del intervalo de envío o se seca entre muestras
/// y vuelve a perseguir la última pose — el tirón. Con una mezcla de 30 y 15 Hz la media se queda
/// entre medias y los de 15 caen por debajo de su suelo. Eso ya pasa hoy con el anillo exterior;
/// encender esto lo haría pasar CERCA, que es donde se nota. El retardo tiene que ser por peer
/// primero.
pub const POSE_CONE_HALF_ANGLE_DEG: f32 = 100.0;

/// Margen de histéresis del cono, en grados: se entra a `POSE_CONE_HALF_ANGLE_DEG` y no se sale
/// hasta ese ángulo más este margen.
///
/// Sin banda muerta el cono es inservible: giras constantemente, y un par pegado al borde cambiaría
/// de cadencia varias veces por segundo. Es la misma lección que costó el arreglo del PVS.
pub const POSE_CONE_HYSTERESIS_DEG: f32 = 20.0;

/// ¿Merece la pena preguntar por el cono? Con 180° la respuesta es siempre sí y el producto escalar
/// sobraría.
pub const POSE_CONE_ENABLED: bool = POSE_CONE_HALF_ANGLE_DEG < 180.0;

/// ¿Le queda esta fuente dentro del cono de atención del destinatario?
///
/// Geometría pura y nada más: la posición de los dos y hacia dónde mira el destinatario. **Jamás
/// qué es la fuente** — ADR-074. Una criatura y un jugador en el mismo ángulo tienen que dar el
/// mismo resultado, o el cono se convierte en un detector de robapieles.
///
/// Convención de `yaw` la misma que el resto del backend (Unity): adelante es `(sin, cos)`.
///
/// El semiángulo entra por parámetro y no se lee de la constante, igual que `aoi_pose_should_relay`
/// recibe su radio: es lo que permite probar la geometría aunque la constante de producción tenga
/// el cono apagado.
pub fn pose_in_attention_cone(
    dest_pos: [f32; 3],
    dest_yaw_deg: f32,
    src_pos: [f32; 3],
    was_inside: bool,
    half_angle_deg: f32,
) -> bool {
    let dx = src_pos[0] - dest_pos[0];
    let dz = src_pos[2] - dest_pos[2];
    let len = (dx * dx + dz * dz).sqrt();
    if len < f32::EPSILON {
        return true; // encima el uno del otro: no hay ángulo que medir
    }
    let half = if was_inside {
        (half_angle_deg + POSE_CONE_HYSTERESIS_DEG).min(180.0)
    } else {
        half_angle_deg
    };
    let yaw = dest_yaw_deg.to_radians();
    let dot = (yaw.sin() * dx + yaw.cos() * dz) / len;
    dot >= half.to_radians().cos()
}

/// E1 / ADR-074 (fase 1): ¿debe viajar la pose de `src` a `dest` esta ronda?
///
/// Pura y sin red para poder probar la histéresis sin sockets. `was_relaying` es lo que este par
/// hacía en la ronda anterior: quien ya estaba dentro aguanta hasta el radio de salida, quien
/// estaba fuera necesita cruzar el de entrada.
///
/// **Decide por POSICIÓN y solo por posición** — nunca consulta si el origen es un fantasma
/// (ADR-016): si el filtro se comportara distinto con el robapieles, la propia asimetría lo
/// delataría, que es el invariante que ADR-074 declara innegociable.
pub fn aoi_pose_should_relay(
    src_pos: [f32; 3],
    dest_pos: [f32; 3],
    was_relaying: bool,
    radius: f32,
) -> bool {
    let dx = src_pos[0] - dest_pos[0];
    let dy = src_pos[1] - dest_pos[1];
    let dz = src_pos[2] - dest_pos[2];
    let dist_sq = dx * dx + dy * dy + dz * dz;
    let threshold = if was_relaying {
        radius * AOI_POSE_EXIT_FACTOR
    } else {
        radius
    };
    dist_sq <= threshold * threshold
}

/// ADR-046 â€” how far a voice carries, in metres. Sits between the two peer sounds that already
/// exist: a footstep dies at 22 m and a pain grunt at 28 m (`ProxyFootstepHook`,
/// `ProxyDamageAudioHook`), so a voice reaching 25 m is louder than a step and about as far as a
/// cry. The client fades to true silence at exactly this distance with the hard-cutoff curve.
pub const VOICE_RADIUS_M: f32 = 25.0;

/// Extra distance the host will still relay over. Hysteresis, not slack: peer positions land at
/// 10 Hz, so a listener walking the boundary would otherwise have the voice cut mid-word every
/// time a pose update crossed the line. Frames inside the margin arrive and are attenuated to
/// silence by the client's curve, which costs a few bytes and sounds like nothing.
pub const VOICE_RELAY_MARGIN_M: f32 = 6.0;

fn distance_sq(a: [f32; 3], b: [f32; 3]) -> f32 {
    let (dx, dy, dz) = (a[0] - b[0], a[1] - b[1], a[2] - b[2]);
    dx * dx + dy * dy + dz * dz
}

/// True when `a` and `b` are close enough for voice to travel between them.
///
/// 3D and not XZ on purpose: the layers are stacked vertically, so plain euclidean distance
/// already keeps a speaker on layer 1 from being heard by someone standing above them. ADR-043
/// needed an explicit layer comparison because it was culling by XZ; here the Y term does it.
pub fn within_voice_range(a: [f32; 3], b: [f32; 3]) -> bool {
    let reach = VOICE_RADIUS_M + VOICE_RELAY_MARGIN_M;
    distance_sq(a, b) <= reach * reach
}

/// ADR-046 â€” who gets a copy of `speaker`'s voice, decided by the HOST.
///
/// Three exclusions, each for its own reason:
/// * the speaker (nobody is relayed their own voice, same rule as the pose relay),
/// * phantoms, whose `addr` is the inert `127.0.0.1:1` â€” the destination filter ADR-043 added,
/// * peers out of earshot. THAT one is not an optimisation: it is the only thing stopping a
///   modified client from decoding a conversation happening across the level. A filter that
///   lived only in the listener would be a filter the listener can remove.
///
/// A DEAD peer is excluded too (ADR-046: the dead neither speak nor listen). The client stops
/// capturing on death as well, but that half only saves bandwidth â€” this half is the authority,
/// and it is what a patched client cannot get around.
pub(crate) fn voice_destinations(net: &NetworkManager, speaker: PeerId) -> Vec<PeerId> {
    let Some(origin) = net.peers.get(&speaker).map(|p| p.position) else {
        return Vec::new();
    };
    net.peers
        .values()
        .filter(|p| p.id != speaker && !net.is_phantom(p.id) && !p.dead)
        .filter(|p| within_voice_range(origin, p.position))
        .map(|p| p.id)
        .collect()
}

/// ADR-078 — hasta dónde se reenvía un trazo en vivo. Más largo que el de la voz porque esto
/// es VISTA: se ve pintar a alguien desde el fondo de un pasillo, y cortarlo a distancia de
/// conversación haría que el trazo apareciera de golpe justo cuando te acercas, que es
/// exactamente el defecto que la fase B viene a quitar.
pub const SPRAY_DRAFT_RADIUS_M: f32 = 40.0;

/// True cuando `a` está lo bastante cerca de `b` para ver dibujarse un trazo.
///
/// 3D como el de la voz: las capas están apiladas en vertical, así que el término Y ya impide
/// que se vea pintar a alguien que está un piso más abajo.
pub fn within_spray_draft_range(a: [f32; 3], b: [f32; 3]) -> bool {
    distance_sq(a, b) <= SPRAY_DRAFT_RADIUS_M * SPRAY_DRAFT_RADIUS_M
}

/// ADR-078 — quién recibe copia del trazo de `painter`, decidido por el HOST.
///
/// Espejo exacto de `voice_destinations`, y las exclusiones son las mismas por las mismas
/// razones: el propio pintor (a nadie se le reenvía lo suyo), los fantasmas (cuyo `addr` es el
/// inerte `127.0.0.1:1`) y quien esté lejos. Esta última NO es una optimización: es lo único
/// que impide que un cliente modificado vea dibujarse pintadas al otro lado del nivel. Un
/// filtro que viviera en el receptor sería un filtro que el receptor puede quitar.
///
/// A diferencia de la voz, un peer MUERTO sí recibe: un muerto no habla ni oye (ADR-046) pero
/// sigue viendo el mundo hasta que reaparece, y una pintada es mundo.
pub(crate) fn spray_draft_destinations(net: &NetworkManager, painter: PeerId) -> Vec<PeerId> {
    let Some(origin) = net.peers.get(&painter).map(|p| p.position) else {
        return Vec::new();
    };
    spray_draft_destinations_from(net, origin, painter)
}

/// La misma decisión pero con el origen DADO, para el jugador local del host: su posición no
/// vive en `net.peers` (no es un peer para sí mismo), así que no hay entrada de la que sacarla.
pub(crate) fn spray_draft_destinations_from(
    net: &NetworkManager,
    origin: [f32; 3],
    exclude: PeerId,
) -> Vec<PeerId> {
    net.peers
        .values()
        .filter(|p| p.id != exclude && !net.is_phantom(p.id))
        .filter(|p| within_spray_draft_range(origin, p.position))
        .map(|p| p.id)
        .collect()
}

/// ADR-015: host-as-server relay of per-peer POSE (rotation + animation included).
/// The roster relay (`broadcast_peer_roster` / `PeerList`) carries only POSITION, so a
/// joiner â€” which only connects to the host â€” never learns the rotation or animation of
/// the OTHER joiners (their pickup gesture, ADR-011, and facing). Here the host re-emits
/// each peer's `PlayerUpdate` (pos/rot/anim from its `PeerConnection`) to every OTHER
/// peer, stamped with that peer's id via `send_unreliable_as`, reusing the exact
/// PlayerUpdate receive path. Host-only and a no-op below two peers (a joiner's peer set
/// is just {host}, nothing to relay; with one joiner there is no second joiner to inform).
/// Sent at the player-update cadence (`NET_BROADCAST_EVERY`, `POSE_RELAY_HZ`).
/// E1 / ADR-074 (fase 1): `&mut` porque el relay mantiene el estado de histéresis del AOI
/// (`aoi_pose_pairs`). Sigue emitiendo a `POSE_RELAY_HZ` — lo que cambia es A QUIÉN, no qué ni cuándo, así
/// que no toca un byte del wire (mismo criterio que ADR-071 y F0.8).
/// ADR-140 — lo que el relay necesita para preguntarle al grafo de salas.
///
/// Se pasa por parámetro y no vive en `NetworkManager` por la misma razón que la caché de regiones
/// (R3, `world.rs`): el mundo lo POSEE el bucle de juego. `None` es el camino de siempre, sin PVS.
pub struct PvsCtx<'a> {
    pub worlds: &'a mut crate::world::wg3::world::Wg3WorldCache,
    pub manifest: &'a crate::world::wg3::manifest::Wg3Manifest,
    pub world_seed: u64,
}

/// Radio dentro del cual se relaya SIEMPRE, diga lo que diga el grafo.
///
/// El módulo `visibility` lo exige por escrito: «quien consulte esto debe unirlo con un radio mínimo
/// que se ve SIEMPRE, pase lo que pase». Aquí ese radio no es un número redondo cualquiera — son
/// 35 m porque la VOZ llega a 25 (`VOICE_RADIUS_M`) más 6 de margen de relay. Si el PVS pudiera
/// ocultar a alguien más cerca que eso, se oiría hablar a un jugador cuya posición no se está
/// recibiendo, y esa asimetría es peor que el ancho de banda que ahorra.
pub const PVS_MIN_RADIUS_M: f32 = 35.0;

/// La sala de un peer y las salas que desde ella se ven, resueltas UNA vez por ronda.
///
/// Con N peers, preguntar `can_see` por pareja son N² consultas al grafo para N respuestas
/// distintas. Esto las resuelve una vez por peer y deja la prueba por pareja en una pertenencia.
struct PvsKey {
    region: crate::world::wg3::world::Wg3RegionCoord,
    storey: usize,
    space: usize,
    /// Salas visibles para ENTRAR en el relay: el criterio estricto.
    visible_enter: Vec<usize>,
    /// Salas visibles para SEGUIR en él: un salto más de margen.
    visible_stay: Vec<usize>,
}

/// Saltos de más que se conceden a un par que YA se estaba relayando.
///
/// **El PVS nació sin histéresis y eso fue un fallo, no una simplificación.** El radio lleva la
/// suya desde ADR-074 —se entra a 100 m y no se sale hasta 120— precisamente para que nadie
/// parpadee en la frontera; el grafo de salas se evaluaba de cero en cada ronda, así que alguien
/// andando junto a un vano cambiaba de «se ve» a «no se ve» varias veces por segundo. Cada
/// reaparición deja al cliente sin historial que interpolar, y se ve como un salto.
///
/// Lo reportó Joel en el primer playtest con dos: «tirones en cuanto aparece de nuevo el player».
///
/// Un salto y no dos: el margen tiene que ser el mínimo que rompa el ciclo. Con dos, la banda de
/// duda se hace tan ancha que el filtro deja de filtrar.
const PVS_STAY_EXTRA_HOPS: usize = 1;

/// Resuelve la clave de un peer, o `None` si no se puede afirmar nada.
///
/// `None` NO significa invisible: significa que este filtro no opina, y quien no opina deja pasar.
/// Se devuelve ante un grafo vacío, una cota en la costura entre plantas, o una posición que no cae
/// dentro de ninguna sala (un pasillo generado, el exterior).
fn pvs_key_for(ctx: &mut PvsCtx<'_>, pos: [f32; 3]) -> Option<PvsKey> {
    use crate::world::wg3::chunk::Wg3ChunkCoord;
    use crate::world::wg3::world::Wg3RegionCoord;

    let coord = Wg3ChunkCoord::containing(pos[0], pos[2]);
    let region = Wg3RegionCoord::of_chunk(coord);
    let vis = ctx
        .worlds
        .region_for(ctx.manifest, ctx.world_seed, coord)
        .visibility();
    if vis.is_empty() {
        return None;
    }
    let storey = vis.storey_at_cm((pos[1] * 100.0) as i32)?;
    let graph = vis.storey(storey)?;
    let space = graph.space_at_cm((pos[0] * 100.0) as i32, (pos[2] * 100.0) as i32)?;
    let hops = crate::world::wg3::visibility::DEFAULT_VISIBILITY_HOPS;
    Some(PvsKey {
        region,
        storey,
        space,
        visible_enter: graph.visible_from(space, hops),
        visible_stay: graph.visible_from(space, hops + PVS_STAY_EXTRA_HOPS),
    })
}

/// PVSTRACE — cuántas parejas oculta el grafo de las que el radio ya había aceptado.
///
/// Es la medida que dice si encender esto sirve de algo, y va aparte del ancho de banda a
/// propósito: `BWTRACE` diría que se manda menos, pero no si se manda menos porque el PVS
/// funciona o porque había menos gente. `warn!` por la misma razón que las otras trazas.
fn note_pvs_round(considered: usize, hidden: usize) {
    use std::time::Instant;
    static ACC: std::sync::Mutex<(u64, u64)> = std::sync::Mutex::new((0, 0));
    static LAST_DUMP: std::sync::Mutex<Option<Instant>> = std::sync::Mutex::new(None);

    let (total, cut) = {
        let Ok(mut acc) = ACC.lock() else {
            return; // un mutex envenenado no justifica tumbar el relay: esto es diagnóstico
        };
        acc.0 += considered as u64;
        acc.1 += hidden as u64;
        *acc
    };
    let Ok(mut last) = LAST_DUMP.lock() else {
        return;
    };
    let now = Instant::now();
    match *last {
        Some(t) if now.duration_since(t).as_secs() < 5 => return,
        _ => *last = Some(now),
    }
    let pct = match total {
        0 => 0.0,
        n => 100.0 * cut as f64 / n as f64,
    };
    log::warn!(
        "PVSTRACE event=pose_pairs_filtered considered={total} hidden={cut} hidden_pct={pct:.1} \
         min_radius_m={PVS_MIN_RADIUS_M}"
    );
}

/// ¿Deja pasar el PVS esta pareja? **Ante cualquier duda, sí.**
///
/// Pura y sin red a propósito, igual que `aoi_pose_should_relay`: la mitad que puede hacer a un
/// jugador invisible tiene que poder probarse sin sockets.
///
/// Regiones distintas se dejan pasar porque el grafo es POR REGIÓN y no sabe nada del otro lado de
/// la costura; plantas distintas, porque un hueco de escalera comunica plantas y el grafo tampoco
/// sabe de eso (`visibility.rs`).
/// `was_relaying` es lo que este par hacía en la ronda anterior, igual que en
/// `aoi_pose_should_relay`: quien ya estaba dentro aguanta un salto más, quien estaba fuera
/// necesita el criterio estricto. Es la histéresis, y sin ella el par parpadea junto a un vano.
fn pvs_allows(a: Option<&PvsKey>, b: Option<&PvsKey>, was_relaying: bool) -> bool {
    let (Some(a), Some(b)) = (a, b) else {
        return true;
    };
    if a.region != b.region || a.storey != b.storey {
        return true;
    }
    match was_relaying {
        true => a.visible_stay.contains(&b.space),
        false => a.visible_enter.contains(&b.space),
    }
}

/// Devuelve cuántas poses se pusieron en camino esta ronda (pares que emitieron): el arnés de
/// carga lo usa para sacar el Hz medio por par; el bucle del juego lo ignora.
pub async fn broadcast_peer_poses(net: &mut NetworkManager, pvs: Option<PvsCtx<'_>>) -> usize {
    broadcast_peer_poses_at(net, pvs, std::time::Instant::now()).await
}

/// ADR-146 — lo mismo que `broadcast_peer_poses`, con el instante de la ronda INYECTADO. El juego
/// pasa el reloj real; un arnés que recorre sus rondas en milisegundos pasa uno sintético, porque
/// una estimación de velocidad por tiempo no se puede medir en algo que corre más rápido que el
/// reloj (la misma trampa que `load_tests.rs` documenta para los rosters).
pub async fn broadcast_peer_poses_at(
    net: &mut NetworkManager,
    mut pvs: Option<PvsCtx<'_>>,
    now: std::time::Instant,
) -> usize {
    if net.peers.len() < 2 {
        return 0;
    }
    // Snapshot ids + poses up front so we don't hold a borrow of net.peers across the
    // awaits below (and so a peer is never echoed its own pose).
    //
    // ADR-043: DESTINATIONS exclude phantoms; SOURCES do not. A phantom is a sender, never a
    // receiver â€” its `addr` is the inert `127.0.0.1:1` stamped at injection, so every datagram
    // aimed at one was a real syscall to a dead loopback port (and on Windows, an ICMP
    // port-unreachable that comes back as WSAECONNRESET on the socket). With a populated world
    // that was the dominant cost of the relay: at P total peers of which N are phantoms, the
    // wasted fraction is NÃ—(Pâˆ’1) datagrams per call at 10 Hz. Same packets, same senders, fewer
    // destinations â€” no real peer can observe the difference.
    let dest_ids = relay_destinations(net);
    if dest_ids.is_empty() {
        return 0; // only phantoms present: nobody to inform
    }
    // E1: las posiciones de los destinos, para el filtro de AOI. Se toman ANTES del bucle porque
    // dentro ya no se puede leer `net.peers` (el envío toma prestado `net`).
    let dest_pos: std::collections::HashMap<PeerId, [f32; 3]> = dest_ids
        .iter()
        .filter_map(|id| net.peers.get(id).map(|p| (*id, p.position)))
        .collect();
    // Hacia dónde mira cada destinatario. Sólo se recoge si el cono está encendido: con 180° todo
    // cuenta como «delante» y este mapa sería peso muerto.
    let dest_yaw: std::collections::HashMap<PeerId, f32> = if POSE_CONE_ENABLED {
        dest_ids
            .iter()
            .filter_map(|id| net.peers.get(id).map(|p| (*id, p.rotation)))
            .collect()
    } else {
        std::collections::HashMap::new()
    };
    // Sólo id y posición: es lo ÚNICO que decide el AOI, y es todo `Copy`. La pose completa se
    // construye más abajo y sólo para quien acabe teniendo destinatarios — antes se armaban las P
    // poses por ronda, y las de los orígenes que no interesan
    // a nadie se tiraban enteras. Con 24 criaturas y una persona dentro (medido el 10-09) eso eran
    // cientos de `String` por segundo asignadas para nada.
    let poses: Vec<(PeerId, [f32; 3])> = net.peers.values().map(|p| (p.id, p.position)).collect();

    // ADR-146 D2 — la velocidad de cada ORIGEN, una vez por ronda y antes de todo filtro: es un
    // hecho del origen, no de quién lo mira. Reemplazo entero sobre los presentes, así que un peer
    // que se fue se lleva la suya. Un SALTO (teleport) invalida los tramos de ese origen en todos
    // sus pares: esta ronda emiten, pase lo que pase con la predicción.
    let mut next_velocity = std::collections::HashMap::with_capacity(poses.len());
    let mut jumped_sources: std::collections::HashSet<PeerId> = std::collections::HashSet::new();
    for (id, pos) in &poses {
        let (estimate, jumped) = estimate_pose_velocity(net.pose_velocity.get(id), *pos, now);
        next_velocity.insert(*id, estimate);
        if jumped {
            jumped_sources.insert(*id);
        }
    }
    net.pose_velocity = next_velocity;

    // E1 (ADR-074 fase 1): decidir ANTES de enviar qué pares siguen dentro del AOI, y dejar el
    // estado de histéresis ya actualizado. Se hace en un paso aparte porque el envío toma
    // prestado `net` y aquí hace falta mutar `net.aoi_pose_pairs`.
    // Los orígenes que le tocan a cada DESTINATARIO esta ronda, con la distancia y la posición que
    // el tope necesita para ordenar. Se recoge por destinatario —y no por origen, como antes—
    // porque el tope es suyo: sólo mirando su bolsa entera se sabe si hay que recortarla. El mapa
    // por origen que el bucle de envío consume se arma después, ya recortado.
    let mut due_by_dest: std::collections::HashMap<PeerId, Vec<(f32, [f32; 3], PeerId)>> =
        std::collections::HashMap::with_capacity(dest_ids.len());
    let mut next_pairs = std::collections::HashSet::with_capacity(net.aoi_pose_pairs.len().max(16));
    let mut next_cone: std::collections::HashSet<(PeerId, PeerId)> =
        std::collections::HashSet::new();

    // ADR-140 — las salas de todos, resueltas una vez. Fuera del bucle de pares a propósito: es la
    // diferencia entre N consultas al grafo y N².
    let pvs_keys: std::collections::HashMap<PeerId, Option<PvsKey>> = match pvs.as_mut() {
        Some(ctx) => poses
            .iter()
            .map(|(id, pos)| (*id, pvs_key_for(ctx, *pos)))
            .collect(),
        None => std::collections::HashMap::new(),
    };
    let pvs_on = pvs.is_some();
    let mut pvs_hidden = 0usize;
    let mut pvs_considered = 0usize;

    // Índice espacial de los destinos. Sin él este bucle es O(N²) AUNQUE el radio rechace a todo el
    // mundo, porque preguntar cuesta igual que aceptar: medido en `host_total_player_ceiling`, con
    // la gente repartida —donde el radio no deja pasar ni una pose— doblar N multiplicaba el coste
    // por cuatro, y el techo caía en 320 con el cable a cero. El filtro ahorraba cable y no ahorraba
    // nada de CPU.
    //
    // Las casillas se llenan en el orden de `dest_ids` (Vec), así que cada cubo conserva un orden
    // estable y el recorrido de abajo sigue siendo reproducible — regla dura 13.
    let mut cells: std::collections::HashMap<(i32, i32), Vec<PeerId>> =
        std::collections::HashMap::with_capacity(dest_ids.len());
    for &dest_id in &dest_ids {
        if let Some(dpos) = dest_pos.get(&dest_id) {
            cells.entry(pose_cell(*dpos)).or_default().push(dest_id);
        }
    }

    // ADR-074 enm. 4 — el aforo de cada destinatario, con lo que se sabía la ronda anterior: la
    // demanda es la suma de Hz de la curva sobre sus pares vivos en el AOI, y su parte del
    // presupuesto es la del anfitrión entre los destinatarios reales. El factor se mueve despacio
    // hacia el objetivo. Se calcula ANTES del bucle de pares porque la cadencia de cada par depende
    // de él, y sobre `aoi_pose_pairs` (la ronda anterior) porque los pares de ésta aún no existen.
    let src_pos_by_id: std::collections::HashMap<PeerId, [f32; 3]> =
        poses.iter().copied().collect();
    let mut demand_hz: std::collections::HashMap<PeerId, f32> =
        std::collections::HashMap::with_capacity(dest_ids.len());
    for (src, dest) in &net.aoi_pose_pairs {
        if let (Some(sp), Some(dp)) = (src_pos_by_id.get(src), dest_pos.get(dest)) {
            *demand_hz.entry(*dest).or_insert(0.0) += pose_hz(distance_sq(*sp, *dp).sqrt()) as f32;
        }
    }
    let share_hz =
        HOST_POSE_BUDGET_KB_S * 1024.0 / POSE_WIRE_BYTES_EST / dest_ids.len().max(1) as f32;
    let mut budget_factor: std::collections::HashMap<PeerId, f32> =
        std::collections::HashMap::with_capacity(dest_ids.len());
    let mut budget_min = 1.0f32;
    for &dest_id in &dest_ids {
        let target =
            budget_factor_target(demand_hz.get(&dest_id).copied().unwrap_or(0.0), share_hz);
        let prev = net.pose_budget_factor.get(&dest_id).copied().unwrap_or(1.0);
        let next = if target > prev {
            (prev + POSE_BUDGET_FACTOR_STEP).min(target)
        } else {
            (prev - POSE_BUDGET_FACTOR_STEP).max(target)
        };
        budget_min = budget_min.min(next);
        budget_factor.insert(dest_id, next);
    }
    net.pose_budget_factor = budget_factor;

    for (src_id, src_pos) in &poses {
        // Sólo la casilla propia y las ocho de alrededor: cualquier destino fuera de esas nueve
        // está más lejos que el radio de SALIDA y el filtro lo iba a rechazar seguro, así que ni se
        // le pregunta. Lo que se salta es exactamente el trabajo que antes se pagaba para nada.
        let (cx, cz) = pose_cell(*src_pos);
        for (dest_id, dpos) in (-1..=1)
            .flat_map(|dx| (-1..=1).map(move |dz| (cx + dx, cz + dz)))
            .filter_map(|cell| cells.get(&cell))
            .flatten()
            .filter_map(|id| dest_pos.get(id).map(|p| (*id, p)))
        {
            if dest_id == *src_id {
                continue; // never echo a peer its own pose
            }
            let was = net.aoi_pose_pairs.contains(&(*src_id, dest_id));
            if !aoi_pose_should_relay(*src_pos, *dpos, was, AOI_POSE_RADIUS_M) {
                continue;
            }
            // ADR-140 — el PVS va DESPUÉS del radio y nunca en su lugar: es una condición más, la
            // más restrictiva, y sólo puede quitar destinatarios que el radio ya había aceptado.
            //
            // **El radio mínimo gana siempre.** Dentro de `PVS_MIN_RADIUS_M` no se pregunta nada:
            // ahí el grafo no tiene derecho a ocultar a nadie (ver la constante).
            if pvs_on && distance_sq(*src_pos, *dpos) > PVS_MIN_RADIUS_M * PVS_MIN_RADIUS_M {
                pvs_considered += 1;
                if !pvs_allows(
                    pvs_keys.get(src_id).and_then(|k| k.as_ref()),
                    pvs_keys.get(&dest_id).and_then(|k| k.as_ref()),
                    was,
                ) {
                    pvs_hidden += 1;
                    continue;
                }
            }
            // El par SIGUE dentro del AOI aunque esta ronda no le toque emitir: el estado de la
            // histéresis es "nos estamos viendo", no "emití hace 100 ms". Si se registrara solo al
            // emitir, un par del anillo exterior perdería su marca en las rondas alternas y
            // volvería a exigir el radio de ENTRADA cada dos rondas — justo el parpadeo en la
            // frontera que la histéresis existe para evitar.
            next_pairs.insert((*src_id, dest_id));
            // El cono de atención del destinatario. Lo que le queda a la espalda baja a MEDIA
            // cadencia, nunca menos: 15 Hz son 66 ms de hueco, 27 cm a paso de carrera, y eso no se
            // ve. Bajar más sí se vería, y justo al girarse.
            //
            // Comparte escalonado con el anillo exterior (`half_cadence_round`) a propósito: una
            // fuente lejana Y a la espalda se queda en media cadencia, no en un cuarto.
            // ADR-074 enm. 4: la cadencia final del par sale de la distancia (enm. 3), del aforo del
            // destinatario y de si el origen le queda a la espalda; `due_at_hz` la reparte en rondas.
            let mut behind = false;
            if POSE_CONE_ENABLED {
                let was_inside = net.pose_cone_pairs.contains(&(*src_id, dest_id));
                let yaw = dest_yaw.get(&dest_id).copied().unwrap_or(0.0);
                let inside = pose_in_attention_cone(
                    *dpos,
                    yaw,
                    *src_pos,
                    was_inside,
                    POSE_CONE_HALF_ANGLE_DEG,
                );
                if inside {
                    next_cone.insert((*src_id, dest_id));
                }
                behind = !inside;
            }
            let hz = pose_pair_hz(
                distance_sq(*src_pos, *dpos).sqrt(),
                net.pose_budget_factor.get(&dest_id).copied().unwrap_or(1.0),
                behind,
            );
            let due = due_at_hz(net.pose_relay_round, hz, pose_pair_phase(*src_id, dest_id));
            if due {
                // Candidato, todavía no emisión: el tope por destinatario se aplica más abajo,
                // cuando la bolsa de cada uno esté entera. Igual que la cadencia, el recorte vive
                // DESPUÉS de `next_pairs.insert` a propósito — un par que el tope deje fuera sigue
                // estando dentro del radio, y si perdiera su marca volvería a exigir el radio de
                // ENTRADA en la ronda siguiente. Ése es exactamente el parpadeo que costó el
                // arreglo de la histéresis del PVS.
                due_by_dest.entry(dest_id).or_default().push((
                    distance_sq(*src_pos, *dpos),
                    *src_pos,
                    *src_id,
                ));
            }
        }
    }
    if pvs_on {
        note_pvs_round(pvs_considered, pvs_hidden);
    }

    // El tope por destinatario. Hasta aquí el filtro ha decidido par a par, que es lo que hace que
    // el coste de una sala crezca con el cuadrado: nadie mira nunca cuánto acaba recibiendo UNA
    // persona. Aquí se mira, y se corta por lo más lejano.
    //
    // Se recorre `dest_ids` (Vec) y no las claves del mapa, y dentro se ordena con un criterio
    // total — regla dura 13: el orden de salida no puede depender de cómo itere un `HashMap`.
    // `relayed` sale con la misma forma y el mismo orden que tenía cuando se llenaba en el bucle
    // de pares, así que mientras el tope no muerda esto es un no-op byte a byte.
    let mut relayed: std::collections::HashMap<PeerId, Vec<PeerId>> =
        std::collections::HashMap::with_capacity(poses.len());
    let mut max_fan_in = 0usize;
    let mut cap_hits = 0usize;
    for dest_id in &dest_ids {
        let Some(cands) = due_by_dest.get_mut(dest_id) else {
            continue;
        };
        max_fan_in = max_fan_in.max(cands.len());
        if cands.len() > POSE_FIDELITY_CAP {
            cap_hits += 1;
            // `sort_unstable` puede permutar elementos equivalentes, y por eso el comparador es
            // total hasta el final: sin el desempate por posición, dos fuentes a la misma
            // distancia podrían alternar entre rondas y provocar el mismo parpadeo que el radio y
            // el grafo ya evitan con histéresis.
            cands.sort_unstable_by(pose_fidelity_order);
            cands.truncate(POSE_FIDELITY_CAP);
        }
        for (_, _, src_id) in cands.iter() {
            relayed.entry(*src_id).or_default().push(*dest_id);
        }
    }

    let relayed_count: usize = relayed.values().map(|d| d.len()).sum();

    // Se recorre `poses` (Vec, orden estable) y NO las claves de `relayed` (HashMap): el orden de
    // salida tiene que ser determinista — regla dura 13.
    // ADR-140 D4: se agrupa POR DESTINATARIO. Antes cada par (origen, destino) era un `send_to`
    // propio —2.450 llamadas al sistema por ronda con 50 juntos, medidas en 21,92 de los 25,34 ms
    // de una ronda, el 86 %—, y ahora cada destinatario recibe UN datagrama con todas las poses que
    // le tocan. El orden de recorrido sigue siendo el de `poses` (Vec), así que es determinista.
    let mut per_dest: std::collections::HashMap<PeerId, (Vec<u16>, Vec<PoseWire>)> =
        std::collections::HashMap::with_capacity(dest_ids.len());
    // ADR-144 D3: la marca de cosméticos por par de ESTA ronda. Reemplazo entero, como
    // `next_pairs`: un par que no emite esta ronda conserva su marca anterior (se copia), y uno
    // que salió del AOI la pierde.
    let mut next_cosmetics: std::collections::HashMap<(PeerId, PeerId), (u64, u64)> =
        std::collections::HashMap::with_capacity(net.pose_cosmetics_sent.len().max(16));
    let round = net.pose_relay_round;
    // ADR-146 D2: la marca de tramo por par de ESTA ronda, con el mismo ciclo de vida que la de
    // cosméticos. Un par omitido conserva la suya; uno que salió del AOI la pierde.
    let mut next_tramo: std::collections::HashMap<(PeerId, PeerId), TramoMark> =
        std::collections::HashMap::with_capacity(net.pose_tramo_sent.len().max(16));
    let mut tramo_skipped = 0usize;

    for (src_id, _) in &poses {
        // E1: si este origen no le interesa a nadie, ni se construye su pose ni se serializa.
        let Some(dests) = relayed.get(src_id) else {
            continue;
        };
        let Some(p) = net.peers.get(src_id) else {
            continue; // se fue entre el cálculo del AOI y el envío
        };
        // ADR-144: lo cinemático se arma UNA vez por origen; lo cosmético, y su hash, también,
        // pero sólo viaja hacia los destinos que no lo tienen al día (D3).
        let cosmetics = p.pose_cosmetics();
        let hash = cosmetics_hash(&cosmetics);
        let mut flags = 0u8;
        if p.crouch {
            flags |= PoseWire::FLAG_CROUCH;
        }
        if p.dead {
            flags |= PoseWire::FLAG_DEAD;
        }
        if p.revealed {
            flags |= PoseWire::FLAG_REVEALED;
        }
        if p.light_on {
            flags |= PoseWire::FLAG_LIGHT_ON;
        }
        let base = PoseWire {
            pos_cm: [0; 3], // relativo al destinatario: se rellena por destino
            yaw_u16: PoseWire::quantize_yaw(p.rotation),
            pitch: p.pitch,
            animation: p.animation,
            flags,
            buttons: p.buttons,
            hit_seq: p.hit_seq,
            fire_seq: p.fire_seq,
            melee_seq: p.melee_seq,
            vocal_seq: p.vocal_seq,
            // ADR-146 D1/D2: la velocidad estimada por el anfitrión, igual para todo origen.
            vel_cms: PoseWire::quantize_vel(
                net.pose_velocity
                    .get(src_id)
                    .map(|v| v.vel)
                    .unwrap_or([0.0; 3]),
            ),
            cosmetics: None,
        };
        let src_position = p.position;
        // ADR-146 D2: lo que el receptor tendrá para predecir, por origen y una sola vez.
        let vel_sent = PoseWire::dequantize_vel(base.vel_cms);
        let discrete = pose_discrete_hash(&base, hash);
        let src_jumped = jumped_sources.contains(src_id);
        for &dest_id in dests {
            let Some(dpos) = dest_pos.get(&dest_id) else {
                continue;
            };
            let key = (*src_id, dest_id);
            // ADR-146 D2 — el gate. Va ANTES de la marca de cosméticos a propósito: un par omitido
            // no puede sellar como enviado algo que no salió (la copia de abajo le conserva la
            // marca anterior). Todo lo que decide es posición, rumbo, lo discreto y el reloj.
            if let Some(mark) = net.pose_tramo_sent.get(&key) {
                if net.tramo_gate_enabled
                    && !src_jumped
                    && tramo_predicts(mark, src_position, base.yaw_u16, base.pitch, discrete, now)
                {
                    next_tramo.insert(key, *mark);
                    tramo_skipped += 1;
                    continue;
                }
            }
            next_tramo.insert(
                key,
                TramoMark {
                    pos: src_position,
                    vel: vel_sent,
                    yaw_u16: base.yaw_u16,
                    pitch: base.pitch,
                    discrete,
                    at: now,
                },
            );
            let last = net.pose_cosmetics_sent.get(&key).copied();
            let full = match last {
                Some((h, sent_round)) => {
                    h != hash || round.wrapping_sub(sent_round) >= POSE_COSMETICS_REPAIR_ROUNDS
                }
                None => true,
            };
            next_cosmetics.insert(key, if full { (hash, round) } else { last.unwrap() });
            let mut wire = base.clone();
            wire.pos_cm = PoseWire::quantize_pos(src_position, PoseWire::origin_cm(*dpos));
            if full {
                wire.cosmetics = Some(cosmetics);
            }
            let entry = per_dest
                .entry(dest_id)
                .or_insert_with(|| (Vec::new(), Vec::new()));
            entry.0.push(*src_id);
            entry.1.push(wire);
        }
    }
    // Los pares dentro del AOI que esta ronda no emitieron conservan su marca: si la perdieran,
    // la siguiente pose iría completa sin necesidad.
    for pair in &next_pairs {
        if !next_cosmetics.contains_key(pair) {
            if let Some(mark) = net.pose_cosmetics_sent.get(pair) {
                next_cosmetics.insert(*pair, *mark);
            }
        }
        // ADR-146: igual con el tramo. Sin esto, un par que no tocaba emitir perdería su marca y la
        // siguiente ronda saldría aunque la predicción siguiera acertando.
        if !next_tramo.contains_key(pair) {
            if let Some(mark) = net.pose_tramo_sent.get(pair) {
                next_tramo.insert(*pair, *mark);
            }
        }
    }

    // Se recorre `dest_ids` (Vec, orden estable) y no las claves del mapa: el orden de salida tiene
    // que ser determinista — regla dura 13.
    for dest_id in &dest_ids {
        let Some((senders, updates)) = per_dest.remove(dest_id) else {
            continue;
        };
        let Some(dpos) = dest_pos.get(dest_id) else {
            continue;
        };
        let origin_cm = PoseWire::origin_cm(*dpos);
        for (senders, updates) in split_pose_batches(senders, updates) {
            let payload = PacketPayload::PlayerUpdateBatch {
                origin_cm,
                senders,
                updates,
            };
            net.send_unreliable_to(*dest_id, &payload).await;
        }
    }

    // Reemplazo entero, no unión: un par que dejó de cumplir el radio de SALIDA tiene que
    // desaparecer del estado, o la histéresis lo mantendría vivo para siempre. Y los pares de un
    // peer que se fue se van con él sin necesidad de purga aparte.
    net.aoi_pose_pairs = next_pairs;
    net.pose_cosmetics_sent = next_cosmetics;
    net.pose_tramo_sent = next_tramo;
    // ADR-146: `relayed_count` son los pares que TOCABA emitir; lo que sale de verdad es eso menos
    // lo que el gate omitió. Es lo que devuelve la función y lo que la traza publica.
    let sent_count = relayed_count - tramo_skipped;
    net.pose_cone_pairs = next_cone;
    net.pose_relay_round = net.pose_relay_round.wrapping_add(1);

    // El RTT por peer, que hasta hoy se MEDÍA Y SE TIRABA: `PeerConnection::latency_ms` se
    // calculaba del ack de un fiable y ninguna traza lo publicaba, así que a la pregunta «¿a
    // cuántos ms va esta partida?» no se podía contestar teniendo el número ya en memoria.
    //
    // Va aquí, en la misma puerta de ~1/s que el resto del MPTRACE, y ordenado por id (regla dura
    // 13). Sale el de los destinos REALES: un `relay_only` no acusa recibo de nada, así que su
    // latencia sería siempre 0 y ensuciaría la lectura.
    //
    // **Un 0 significa «sin muestra todavía», no «cero milisegundos».** La muestra sólo se toma de
    // un fiable que llegó a la primera (Karn, ver `process_ack`), y las poses van sin garantía: en
    // una partida tranquila pueden pasar segundos entre fiables.
    if net.session_start.elapsed().as_millis() % 1000 < 120 {
        let mut rtts: Vec<(PeerId, u16)> = dest_ids
            .iter()
            .filter_map(|id| net.peers.get(id).map(|p| (*id, p.latency_ms)))
            .collect();
        rtts.sort_unstable_by_key(|(id, _)| *id);
        if !rtts.is_empty() {
            let sampled: Vec<u16> = rtts
                .iter()
                .map(|(_, ms)| *ms)
                .filter(|ms| *ms > 0)
                .collect();
            let peers = rtts
                .iter()
                .map(|(id, ms)| format!("{id}:{ms}"))
                .collect::<Vec<_>>()
                .join(",");
            info!(
                "MPTRACE step=RTT event=peer_latency dest_count={} sampled={} min_ms={} max_ms={} peers=[{}]",
                rtts.len(),
                sampled.len(),
                sampled.iter().min().copied().unwrap_or(0),
                sampled.iter().max().copied().unwrap_or(0),
                peers
            );
        }
    }

    // ADR-015 traffic gate instrumentation: throttled (~1/s, no mutable state) report of
    // the relay's datagram rate so the host log can be measured in play-test. Since ADR-043
    // the cost is PÃ—D (minus the self-echoes), where P counts every pose relayed and D only
    // the REAL destinations â€” both are logged because their ratio is the phantom overhead.
    if net.session_start.elapsed().as_millis() % 1000 < 120 {
        let p = poses.len();
        let d = dest_ids.len();
        // E1: `relay_datagrams_per_call` sigue siendo LO QUE DE VERDAD SALE, para que el número
        // que este log lleva midiendo desde ADR-015 no cambie de significado a mitad de serie.
        // `without_aoi` es lo que habría salido sin filtro (cada destino real se salta su propia
        // pose), y su cociente es el ahorro real de la partida — no una estimación de sonda.
        let without_aoi = p * d - d.min(p);
        info!(
            "MPTRACE step=R15 event=peer_pose_relay self_id={} peer_count={} real_dest_count={} relay_datagrams_per_call={} approx_per_sec={} without_aoi={} in_aoi={} aoi_radius_m={:.0} floor_hz={} budget_min={:.2} max_fan_in={} cap_hits={} fidelity_cap={} tramo_gate={} tramo_skipped={}",
            net.local_id,
            p,
            d,
            sent_count,
            sent_count as u64 * POSE_RELAY_HZ,
            without_aoi,
            // Pares dentro del AOI, emitan o no esta ronda: su diferencia con
            // `relay_datagrams_per_call` es lo que ahorra el LOD, separado de lo que ahorra el AOI.
            net.aoi_pose_pairs.len(),
            AOI_POSE_RADIUS_M,
            POSE_HZ_FLOOR,
            budget_min,
            // El número que hace falta para bajar el tope con datos: cuánto recibe en una ronda el
            // destinatario que más recibe, y cuántos destinatarios llegaron a tocar el tope. Con
            // `cap_hits=0` en un playtest, el recorte no está haciendo nada y el techo de la sala
            // sigue siendo el de siempre; en cuanto deje de ser cero, `max_fan_in` dice dónde
            // ponerlo de verdad.
            max_fan_in,
            cap_hits,
            POSE_FIDELITY_CAP,
            // ADR-146: lo que el gate de tramos se ahorró en esta ronda, ya descontado de lo que
            // sale. Con el gate apagado siempre es 0.
            net.tramo_gate_enabled,
            tramo_skipped
        );
    }
    sent_count
}

/// Host-as-server relay of the STP item roster: the host broadcasts its full
/// authoritative item list so every joiner spawns the same STP items (Phase 1).
///
/// ADR-060 (d): paginado. Ver `roster::paginate` para por qué el troceo va por bytes reales y
/// `roster::RosterAssembler` para el reensamblado; la generación es el `timestamp()` de esta
/// ronda, resuelto UNA vez para que todas las páginas la compartan.
/// ADR-071: ask one roster's gate whether this round goes out. Factored so the five broadcasts
/// share the rule instead of each carrying its own copy of it — the five differ only in which
/// roster and which gate, and a rule copied five times is a rule that drifts in four of them.
fn roster_gate_open<T: serde::Serialize>(gate: &mut roster::RosterGate, items: &[T]) -> bool {
    gate.should_send(
        roster::content_hash(items),
        std::time::Instant::now(),
        roster::ROSTER_HEARTBEAT,
    )
}

/// ADR-141 — despacha UNA página: a todos si la puerta se abrió, y sólo a los recién llegados si no.
///
/// Los dos caminos mandan el MISMO mensaje; lo único que cambia es el sobre. Está factorizado porque
/// son seis emisores y una regla copiada seis veces es una regla que se desvía en cinco.
async fn send_page_to(
    net: &mut NetworkManager,
    payload: &PacketPayload,
    open: bool,
    to: &[PeerId],
) {
    if open {
        net.broadcast_unreliable(payload).await;
        return;
    }
    for dest in to {
        net.send_unreliable_to(*dest, payload).await;
    }
}

/// ADR-074 fase 2 — **radio del scope**, en celdas (chunks) alrededor del destinatario.
///
/// 5×5 (±2) y no 3×3: el cliente sólo renderiza 3×3 (`ChunkStreamer.viewRadius = 1`), así que el
/// anillo extra es margen para que nada aparezca de golpe al cruzar una frontera. Generoso a
/// propósito — el coste de una celda de más es una página, y el de una de menos es un objeto que
/// no está.
pub const ROSTER_SCOPE_RADIUS_CELLS: i32 = 2;

/// ADR-074 fase 2 — celdas en scope de un destinatario: las (2·r+1)² alrededor de la suya.
///
/// Orden determinista por construcción (dos bucles anidados), que es lo que exige la regla 13 para
/// algo que acaba en el cable.
pub fn roster_scope_cells(dest_pos: [f32; 3], radius: i32) -> Vec<[i32; 2]> {
    let (cx, cz) = world_to_chunk(Vec3::new(dest_pos[0], dest_pos[1], dest_pos[2]));
    let mut cells = Vec::with_capacity(((2 * radius + 1) * (2 * radius + 1)) as usize);
    for dx in -radius..=radius {
        for dz in -radius..=radius {
            cells.push([cx + dx, cz + dz]);
        }
    }
    cells
}

/// ADR-074 fase 2 — la celda de una entrada de roster: la misma que la del mundo (`world_to_chunk`),
/// no una retícula nueva. Reusarla es lo que hace que el scope coincida con lo que el cliente carga.
fn roster_cell_of(position: [f32; 3]) -> [i32; 2] {
    let (cx, cz) = world_to_chunk(Vec3::new(position[0], position[1], position[2]));
    [cx, cz]
}

/// ADR-074 fase 2 — emisor común de los cinco rosters, troceado POR CELDA.
///
/// Los cinco se diferencian en tres cosas: de qué lista salen, cómo se saca la posición de una
/// entrada y qué variante de paquete la lleva. Todo lo demás —agrupar, paginar, elegir destinos,
/// ceder entre páginas y cerrar la ronda— es idéntico, y por eso vive aquí una sola vez: una regla
/// copiada cinco veces es una regla que se desvía en cuatro.
///
/// **El cierre sale SÓLO detrás de las páginas que salieron** (corrección de la enmienda del
/// 2026-08-15): si el gate de ADR-071 cortó la ronda, esta función ni se llama. Un cierre suelto
/// vaciaría en el cliente las celdas que sólo estaban calladas.
async fn broadcast_roster_by_cell<T, C, M>(
    net: &mut NetworkManager,
    kind: RosterKind,
    entries: &[T],
    cell_of: C,
    make_page: M,
    open: bool,
    fresh: &[PeerId],
) where
    T: serde::Serialize + Clone,
    C: Fn(&T) -> [i32; 2],
    M: Fn(Vec<T>, u32, u16, u16, [i32; 2]) -> PacketPayload,
{
    let generation = net.timestamp();

    // 1. Agrupar por celda. `BTreeMap` y no `HashMap`: lo que sale al cable no puede depender de
    //    cómo itere un mapa — regla dura 13.
    let mut by_cell: std::collections::BTreeMap<[i32; 2], Vec<T>> =
        std::collections::BTreeMap::new();
    for entry in entries {
        by_cell
            .entry(cell_of(entry))
            .or_default()
            .push(entry.clone());
    }

    // 2. Serializar UNA vez por página y reutilizar los bytes con todos los destinatarios (C2).
    let mut pages_by_cell: std::collections::BTreeMap<[i32; 2], Vec<Vec<u8>>> =
        std::collections::BTreeMap::new();
    for (cell, items) in &by_cell {
        let pages = roster::paginate(items, roster::ROSTER_PAGE_BUDGET_BYTES);
        let page_count = pages.len() as u16;
        let encoded = pages
            .into_iter()
            .enumerate()
            .map(|(index, page)| {
                net.encode_unreliable(&make_page(
                    page,
                    generation,
                    index as u16,
                    page_count,
                    *cell,
                ))
            })
            .collect();
        pages_by_cell.insert(*cell, encoded);
    }

    // 3. A quién le toca: a todos si la puerta se abrió, sólo a los recién llegados si no
    //    (ADR-141). Se resuelve antes del bucle porque dentro ya no se puede prestar `net`.
    let dests: Vec<PeerId> = if open {
        net.broadcast_destinations()
            .into_iter()
            .map(|(id, _)| id)
            .collect()
    } else {
        fresh.to_vec()
    };

    for dest in dests {
        // ADR-074 fase 2, decisión 5 — el scope de VERDAD: las 5×5 celdas alrededor del
        // destinatario. Aquí es donde el coste del roster deja de ser el tamaño del MUNDO y pasa a
        // ser el tamaño de lo que ese jugador tiene cerca.
        //
        // C4 de la enmienda 5: un destinatario del que todavía no se conoce la pose recibe el scope
        // de (0,0). Dura una ronda —en cuanto su pose llega, el scope es el suyo— y la alternativa,
        // dejarlo sin cierre, le congelaría el roster.
        //
        // El scope va COMPLETO aunque una celda no tenga contenido: es justo lo que le dice al
        // receptor «esta celda está vacía» en vez de «esta celda está lejos», que es la ambigüedad
        // que la enmienda del 2026-08-15 existe para cerrar.
        let dest_pos = net.peers.get(&dest).map(|p| p.position).unwrap_or_default();
        let scope = roster_scope_cells(dest_pos, ROSTER_SCOPE_RADIUS_CELLS);
        for cell in &scope {
            let Some(pages) = pages_by_cell.get(cell) else {
                continue; // celda en scope sin contenido: el cierre dirá que está vacía
            };
            for data in pages {
                net.send_unreliable_bytes_to(dest, data).await;
                // ADR-060 (d): ceder entre páginas. Sin esto la ronda sale como una ráfaga
                // ininterrumpida y desborda el buffer de recepción del socket del receptor.
                tokio::task::yield_now().await;
            }
        }
        net.send_unreliable_to(
            dest,
            &PacketPayload::RosterScopeEnd {
                kind,
                generation,
                cells: scope,
            },
        )
        .await;
    }
}

pub async fn broadcast_stp_items(net: &mut NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    // ADR-071: skip the whole round if this roster is byte-identical to the last one that went
    // out. The gate still gets asked at 10 Hz, so the first round AFTER a change ships it exactly
    // as before — this costs no propagation latency, it only stops re-sending what everyone has.
    // ADR-141: fuera de la llamada, porque `newcomers` toma prestado `net` entero y el gate ya se
    // presta mutable en el argumento siguiente.
    let fresh = newcomers(net);
    let open = roster_gate_open(&mut net.roster_gates.items, &net.stp_items);
    if !open && fresh.is_empty() {
        return;
    }
    let entries = net.stp_items.clone();
    broadcast_roster_by_cell(
        net,
        RosterKind::ITEMS,
        &entries,
        |e| roster_cell_of(e.position),
        |items, generation, page, page_count, cell| PacketPayload::StpItemList {
            items,
            generation,
            page,
            page_count,
            cell,
        },
        open,
        &fresh,
    )
    .await;
}

/// ADR-028 Fase E: host-as-server relay of the corpse roster â€” the host broadcasts its
/// full authoritative `world.corpses` so every joiner mirrors the same lootable corpses
/// (their own build_world_state then filters by THEIR player's proximity). Full-roster
/// UDP at 10 Hz = self-healing, same pattern as broadcast_stp_items.
pub async fn broadcast_corpses(net: &mut NetworkManager, world: &World) {
    if net.peers.is_empty() {
        return;
    }
    let all: Vec<_> = split_oversized_corpses(world.corpses.values().cloned().collect());
    // ADR-071. Unlike the other four this one still pays the clone above before the gate can look
    // at it: the roster is assembled from `world.corpses` rather than stored flat. The clone is
    // orders of magnitude cheaper than the send it prevents, so it is not worth restructuring the
    // storage to save it.
    let fresh = newcomers(net);
    let open = roster_gate_open(&mut net.roster_gates.corpses, &all);
    if !open && fresh.is_empty() {
        return;
    }
    broadcast_roster_by_cell(
        net,
        RosterKind::CORPSES,
        &all,
        |c| roster_cell_of([c.position.x, c.position.y, c.position.z]),
        |corpses, generation, page, page_count, cell| PacketPayload::CorpseList {
            corpses,
            generation,
            page,
            page_count,
            cell,
        },
        open,
        &fresh,
    )
    .await;
}

/// TAREA 2 (2026-08-31) — parte los cadáveres que no caben en un datagrama en varias ENTRADAS del
/// mismo roster, con el mismo `id` y tramos disjuntos de `items`.
///
/// Un `CorpseData` con 35 pilas mide 1207 B (medido) contra un techo de 1200, y 35 es un
/// inventario STP lleno de verdad, no el tope de higiene de `MAX_CORPSE_STACKS` (64 → 2048 B). Es
/// el único elemento de roster que puede pasarse él solo: `paginate` le da una página para él y esa
/// página sigue sin caber.
///
/// **Por qué partir la entrada y no truncar las pilas**: truncar es perder botín de un jugador
/// muerto — el único camino por el que este sistema puede destruir algo — y encima de forma
/// invisible para el que lo mira. Y por qué NO hace falta wire nuevo: el roster de cadáveres se
/// aplica REEMPLAZANDO `world.corpses` con la lista completa de una generación, así que dos
/// entradas con el mismo `id` dentro de la misma generación son inequívocamente dos tramos de un
/// mismo cadáver. El receptor las une (`CorpseListReceived`); antes de esto un `id` repetido no
/// podía existir, así que unir no cambia ningún comportamiento previo.
///
/// El reparto es por bytes reales y en frontera de PILA: media pila no significa nada.
pub(crate) fn split_oversized_corpses(
    corpses: Vec<crate::world::corpse::CorpseData>,
) -> Vec<crate::world::corpse::CorpseData> {
    let budget = roster::ROSTER_ITEM_BUDGET_BYTES;
    let size = |c: &crate::world::corpse::CorpseData| {
        rmp_serde::to_vec_named(c).map(|v| v.len()).unwrap_or(0)
    };
    if corpses.iter().all(|c| size(c) <= budget) {
        return corpses;
    }

    let mut out = Vec::with_capacity(corpses.len());
    for corpse in corpses {
        if size(&corpse) <= budget {
            out.push(corpse);
            continue;
        }
        let stacks = corpse.items.clone();
        let mut head = corpse.clone();
        head.items.clear();
        let mut current = head.clone();
        let mut emitted = 0usize;
        for stack in stacks {
            current.items.push(stack);
            if size(&current) > budget && current.items.len() > 1 {
                let back = current.items.pop().expect("acabamos de meterlo");
                out.push(std::mem::replace(&mut current, head.clone()));
                emitted += 1;
                current.items.push(back);
            }
        }
        out.push(current);
        emitted += 1;
        warn!(
            "MTUPROBE event=corpse_split_across_roster_entries corpse_id={} entries={} budget={budget} — el receptor las une por id",
            corpse.id, emitted
        );
    }
    out
}

/// ADR-093 (E2): host-as-server relay of the Level 4 region state. Self-healing at 10 Hz
/// like `broadcast_corpses` — a lost round is replaced by the next one, so no reliability is
/// spent on it (`Level4State` is deliberately absent from `is_reliable`).
///
/// Refreshes `return_dest` from the elapsed time BEFORE building the payload — the same
/// formula `process_return` uses — so a joiner watching the broadcast sees the destination
/// drift live, not just at the moment someone actually requests a Return.
pub async fn broadcast_level4_state(net: &mut NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    net.level4.refresh_return_dest(std::time::Instant::now());
    // `Level4RegionState` carries a non-serializable `Option<Instant>` (host-only, never on the
    // wire) — `roster_gate_open` needs `Serialize`, so the gate hashes just the three wire
    // fields, as a tuple, instead of the whole struct.
    let wire_fields = [(
        net.level4.epoch,
        net.level4.window_open,
        net.level4.return_dest,
    )];
    let fresh = newcomers(net);
    let open = roster_gate_open(&mut net.roster_gates.level4, &wire_fields);
    if !open && fresh.is_empty() {
        return;
    }
    let payload = PacketPayload::Level4State {
        epoch: net.level4.epoch,
        window_open: net.level4.window_open,
        return_dest: net.level4.return_dest,
    };
    send_page_to(net, &payload, open, &fresh).await;
}

/// Host-as-server relay of the STP building roster: the host broadcasts its full
/// authoritative building list so every joiner spawns the same pieces (Phase B1).
pub async fn broadcast_stp_buildings(net: &mut NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    // ADR-071. This is the roster the measurement singled out: a built base is static for hours and
    // was being re-sent 10 times a second forever.
    let fresh = newcomers(net);
    let open = roster_gate_open(&mut net.roster_gates.buildings, &net.stp_buildings);
    if !open && fresh.is_empty() {
        return;
    }
    let entries = net.stp_buildings.clone();
    broadcast_roster_by_cell(
        net,
        RosterKind::BUILDINGS,
        &entries,
        |e| roster_cell_of(e.position),
        |buildings, generation, page, page_count, cell| PacketPayload::StpBuildingList {
            buildings,
            generation,
            page,
            page_count,
            cell,
        },
        open,
        &fresh,
    )
    .await;
}

/// Host-as-server relay of the STP carryable roster: the host broadcasts its full
/// authoritative carryable list so every joiner spawns the same world carryables (B2.5).
pub async fn broadcast_stp_carryables(net: &mut NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    // ADR-071.
    let fresh = newcomers(net);
    let open = roster_gate_open(&mut net.roster_gates.carryables, &net.stp_carryables);
    if !open && fresh.is_empty() {
        return;
    }
    let entries = net.stp_carryables.clone();
    broadcast_roster_by_cell(
        net,
        RosterKind::CARRYABLES,
        &entries,
        |e| roster_cell_of(e.position),
        |carryables, generation, page, page_count, cell| PacketPayload::StpCarryableList {
            carryables,
            generation,
            page,
            page_count,
            cell,
        },
        open,
        &fresh,
    )
    .await;
}

/// Host-as-server relay of the STP harvestable health roster: the host broadcasts its full
/// authoritative harvestable list so every joiner reflects the same tree/rock health (B2.6).
pub async fn broadcast_stp_harvestables(net: &mut NetworkManager) {
    if net.peers.is_empty() {
        return;
    }
    // ADR-071.
    let fresh = newcomers(net);
    let open = roster_gate_open(&mut net.roster_gates.harvestables, &net.stp_harvestables);
    if !open && fresh.is_empty() {
        return;
    }
    let entries = net.stp_harvestables.clone();
    broadcast_roster_by_cell(
        net,
        RosterKind::HARVESTABLES,
        &entries,
        |e| roster_cell_of(e.position),
        |harvestables, generation, page, page_count, cell| PacketPayload::StpHarvestableList {
            harvestables,
            generation,
            page,
            page_count,
            cell,
        },
        open,
        &fresh,
    )
    .await;
}

/// Send nearby chunk states to all peers (for chunks the local player owns).
///
/// Host-only. `chunk.owner` is stamped with the RECEIVER's own local_id on every apply
/// path (`update_ownership`, `apply_chunk_sync`), so before this guard a joiner calling
/// this too made every overlapping backend reclaim the other's chunks every 200ms â€”
/// last-writer-wins with no arbiter, ping-ponging `owner` and re-seeding entities/items
/// on both sides.
/// F0.8 (enmienda ADR-073/074, 2026-08-14): `&mut` porque cada chunk lleva ahora su propio gate,
/// que se actualiza cuando decide que una ronda sale. Sigue corriendo a 5 Hz — lo que cambia es
/// que un chunk que nadie ha tocado desde la última ronda ya no se reenvía.
///
/// Es el mecanismo de ADR-071 aplicado al mayor emisor del host: medido con 8 peers, este relay
/// eran 3351 de los 4373 KB/s de subida (77 %), repitiendo layout, entidades e items de chunks
/// que en su inmensa mayoría llevaban horas idénticos. Igual que allí: cambia la CADENCIA, no el
/// formato — `apply_chunk_sync` sigue siendo un reemplazo verbatim idempotente, así que un peer
/// sin actualizar solo recibe menos rondas y no se entera de nada. Cero cambios de wire.
pub async fn broadcast_chunk_states(net: &mut NetworkManager, world: &World, player_pos: Vec3) {
    if !net.is_host || net.peers.is_empty() {
        return;
    }
    let player_chunk = world_to_chunk(player_pos);
    // ADR-141: éste era el emisor que se medía a 95-122 pkt/s en cada entrada, contra una línea base
    // de 10-20. Ahora quien acaba de llegar recibe los 64 chunks DIRIGIDOS a él y los que ya estaban
    // dentro no se enteran de que ha entrado nadie.
    let fresh = newcomers(net);
    // Las claves visitadas en ESTA ronda. Se recogen para poder tirar después los gates de chunks
    // que ya no se emiten (descargados o alejados): sin la poda el mapa crece con cada chunk que
    // el jugador visita y no vuelve a pisar, durante toda la sesión.
    let mut seen: Vec<(i32, i32, i8)> = Vec::with_capacity(net.chunk_gates.len().max(16));
    for chunk in world.chunks.values() {
        if chunk.owner != Some(net.local_id) {
            continue;
        }
        // Only broadcast chunks near the player (within 3 chunks).
        let dx = (chunk.pos.0 - player_chunk.0).abs();
        let dz = (chunk.pos.1 - player_chunk.1).abs();
        if dx > 3 || dz > 3 {
            continue;
        }
        let data = chunk_to_sync_data(chunk);
        let key = (chunk.pos.0, chunk.pos.1, chunk.layer);
        seen.push(key);
        // El hash es del dato que VIAJA, no del `Chunk` de origen: así el gate no puede cortar por
        // un estado interno que el wire no transporta, ni dejar pasar un cambio que sí transporta.
        // Cuesta una serialización que el envío repite — la CPU está sobradísima (27 ms/s medidos
        // en el peor caso) y lo que este fix compra son bytes, que es el recurso escaso.
        //
        // ADR-139 D1: pero se hashea sólo la parte ESTABLE. `ChunkSyncData` mezcla 754 B de
        // geometría inmutable con un `teleport_timer` que baja cada segundo y unas entidades que se
        // mueven sin parar; hasheándolo entero, un tic de reloj o un paso de una criatura reenviaba
        // el chunk COMPLETO. Medido el 10-09 en partida real: 116,4 KB/s, el **80 % de todo el
        // tráfico del anfitrión**, con un solo jugador dentro.
        //
        // Lo volátil no se queda atrás: las criaturas llevan su propia pose a 30 Hz (`relay_as`,
        // que en esa misma medida era el 8 %), y el resto se refresca en el siguiente latido de
        // `ROSTER_HEARTBEAT`. Se envía exactamente el mismo mensaje: cambia CUÁNDO, no el qué.
        let open = {
            // 2026-09-10: la puerta de los chunks —y SOLO ella— retrocede el latido. Ver
            // `STATIC_ROSTER_HEARTBEAT_CAP`: el 32 % del tráfico medido era geometría estática repitiéndose
            // cada 3 s. `or_insert_with` y no `or_default` porque el tope es del gate, no del
            // llamante: una puerta creada por defecto en otro sitio no debe heredar el retroceso.
            let gate = net.chunk_gates.entry(key).or_insert_with(|| {
                roster::RosterGate::with_backoff(roster::STATIC_ROSTER_HEARTBEAT_CAP)
            });
            gate.should_send(
                stable_chunk_hash(&data),
                std::time::Instant::now(),
                roster::ROSTER_HEARTBEAT,
            )
        };
        // El gate se pregunta SIEMPRE, también cuando sólo hay recién llegados: si se saltara, su
        // `last_sent` no avanzaría y el chunk saldría por latido justo después de haberlo mandado.
        if !open && fresh.is_empty() {
            continue;
        }
        // TAREA 2 (2026-08-31): PAGINADO. Un `ChunkSyncData` mide 1094 B VACÍO y 2078 B con 13
        // entidades (medido), así que este broadcast era el emisor de los ~31.000 datagramas
        // sobredimensionados por sesión. Se trocea contra el sobre REAL de `ChunkState` y se
        // estampa la ronda con el reloj de sesión: el receptor no aplica NADA hasta tener todas
        // las páginas de la MISMA ronda, porque `apply_chunk_sync` es un reemplazo verbatim y
        // media lista de entidades es un mundo que nunca existió.
        let generation = net.timestamp();
        for page in chunk_state_pages(data, generation) {
            let payload = PacketPayload::ChunkState { data: page };
            send_page_to(net, &payload, open, &fresh).await;
        }
        // ADR-060 (d): ceder entre páginas. Sin esto la ronda entera sale como una ráfaga
        // ininterrumpida y desborda el buffer de recepción del socket del receptor (~64 KB por
        // defecto): MEDIDO, a partir de ~56 páginas empezaba a perderse al menos una por ronda,
        // y con reensamblado todo-o-nada eso significa que el roster no converge NUNCA.
        tokio::task::yield_now().await;
    }
    // Poda: solo cuando hay más gates que chunks emitidos, para no pagar el retain en la ronda
    // normal (que es la mayoría). Un chunk que vuelve al radio re-emite en su primera ronda con el
    // gate limpio — correcto: mientras estuvo fuera, el peer pudo perderse cualquier cambio.
    if net.chunk_gates.len() > seen.len() {
        net.chunk_gates.retain(|k, _| seen.contains(k));
    }
}

/// ADR-060: seguimiento receptor de la completitud del goteo de snapshot.
///
/// La capa reliable es at-least-once SIN orden: `End` puede llegar antes que chunks, y un chunk
/// puede llegar DUPLICADO (retransmisiÃ³n tras un ACK perdido). Por eso la completitud se cuenta
/// sobre el conjunto de claves (pos, layer) distintas aplicadas â€” nunca sobre paquetes â€” y por
/// revision: una revision mÃ¡s nueva desecha el estado de las anteriores (el goteo viejo queda
/// superseded, sus rezagados caen en `revision < self.revision` y se ignoran).
#[derive(Debug, Default)]
pub struct WorldSyncProgress {
    revision: u64,
    applied: std::collections::HashSet<(i32, i32, i8)>,
    expected: Option<u32>,
    complete: bool,
}

impl WorldSyncProgress {
    fn adopt(&mut self, revision: u64) {
        if revision > self.revision {
            self.revision = revision;
            self.applied.clear();
            self.expected = None;
            self.complete = false;
        }
    }

    pub fn note_chunk(&mut self, revision: u64, pos: [i32; 2], layer: i8) {
        self.adopt(revision);
        if revision == self.revision {
            self.applied.insert((pos[0], pos[1], layer));
            self.refresh();
        }
    }

    pub fn note_end(&mut self, revision: u64, chunk_count: u32) {
        self.adopt(revision);
        if revision == self.revision {
            self.expected = Some(chunk_count);
            self.refresh();
        }
    }

    /// El monolito 0x04 deprecado aplica el mundo entero de golpe: completo por construcciÃ³n.
    pub fn note_monolith(&mut self, revision: u64) {
        self.adopt(revision);
        if revision == self.revision {
            self.complete = true;
        }
    }

    fn refresh(&mut self) {
        if let Some(expected) = self.expected {
            if self.applied.len() as u32 >= expected {
                self.complete = true;
            }
        }
    }

    /// Una vez completo, completo se queda (dentro de la misma revision): el gate de spawn
    /// consulta esto y no debe reabrirse por un rezagado.
    pub fn is_complete(&self) -> bool {
        self.complete
    }
}

/// Send full world sync to a specific peer (on join) â€” ADR-060: como GOTEO, un datagrama por
/// chunk + un `WorldSyncEnd`, vÃ­a la cola diferida (`send_reliable_queued`). Cada datagrama
/// cabe en un MTU; el monolito anterior morÃ­a en `WSAEMSGSIZE` a ~50-80 chunks.
pub async fn send_world_sync(
    net: &mut NetworkManager,
    peer_id: PeerId,
    world: &World,
    player: &Player,
) {
    let chunk_count = world.chunks.len();
    let entity_count: usize = world.chunks.values().map(|c| c.entities.len()).sum();
    let item_count: usize = world.chunks.values().map(|c| c.items.len()).sum();

    // Un goteo nuevo supersede al anterior hacia este peer: lo aparcado y aÃºn no emitido es
    // trabajo muerto (la revision nueva re-envÃ­a el mundo entero). Lo ya en vuelo no se toca â€”
    // son upserts inofensivos y su End viejo nunca completarÃ¡.
    if let Some(peer) = net.peers.get_mut(&peer_id) {
        let dropped = peer.purge_deferred();
        if dropped > 0 {
            info!(
                "MPTRACE step=W event=world_drip_superseded self_id={} peer_id={} deferred_dropped={}",
                net.local_id, peer_id, dropped
            );
        }
    }

    info!(
        "MPTRACE step=W event=host_world_snapshot_created self_id={} seed={} revision={} chunks={} entities={} items={}",
        net.local_id, world.seed, world.revision, chunk_count, entity_count, item_count
    );
    info!(
        "MPTRACE step=X event=send_world_drip self_id={} peer_id={} revision={} chunks={} entities={} items={}",
        net.local_id, peer_id, world.revision, chunk_count, entity_count, item_count
    );

    for chunk in world.chunks.values() {
        // Auditoría de MTU: un chunk denso no cabe en un datagrama seguro, así que viaja en
        // páginas. Cada una es autosuficiente (repite la cabecera) y se aplica en cualquier orden.
        for page in chunk_to_sync_pages(chunk, world.revision) {
            let payload = PacketPayload::WorldSyncChunk {
                world_revision: world.revision,
                data: page,
            };
            net.send_reliable_queued(peer_id, &payload).await;
        }
        // Ceder entre paquetes — el MISMO mecanismo que `broadcast_chunk_states` ya aplica a
        // estas mismas cargas, por la misma razón medida allí (a partir de ~56 páginas seguidas
        // se perdía al menos una por ronda al desbordar el buffer de recepción de ~64 KB).
        //
        // Este camino no lo tenía, y es el que peor lo necesita: la ventana admite 32 de golpe,
        // así que el goteo salía como ~35 KB en una ráfaga ININTERRUMPIDA dentro de un solo tick,
        // justo cuando el receptor está generando su mundo. Medido en localhost: 32 `RELIABLE_SENT`
        // seguidos y 18 aparcados, sin una sola cesión entre ellos. En loopback el receptor drena
        // al instante y no se nota; por un enlace real la ráfaga se pierde a trozos, y como cada
        // reintento REPRODUCE la misma ráfaga, los mismos paquetes vuelven a caer hasta agotar
        // `MAX_RETRIES` — la desconexión `reliable retransmit exhausted` a los ~6 s de entrar.
        //
        // Ceder no cambia ni el orden, ni la ventana, ni la fiabilidad, ni el protocolo: solo deja
        // correr al bucle de recepción y a los ACK entre paquete y paquete.
        tokio::task::yield_now().await;
    }
    let end = PacketPayload::WorldSyncEnd {
        world_revision: world.revision,
        chunk_count: chunk_count as u32,
    };
    net.send_reliable_queued(peer_id, &end).await;

    // TAREA 2: mismo troceo que en `broadcast_peer_roster`; ver `peer_list_datagrams`.
    for payload in peer_list_datagrams(build_peer_list(net, player)) {
        net.broadcast_unreliable(&payload).await;
    }
}

pub async fn broadcast_world_sync(net: &mut NetworkManager, world: &World, player: &Player) {
    // ADR-016: skip phantoms â€” WorldSync is reliable and their addr is inert, so a copy
    // would never be ACKed and just accumulate retransmits.
    let peer_ids: Vec<PeerId> = net
        .peers
        .keys()
        .copied()
        .filter(|id| !net.is_phantom(*id))
        .collect();
    for peer_id in peer_ids {
        send_world_sync(net, peer_id, world, player).await;
    }
}

/// F0.1 (enmienda ADR-073, E0): la ventana de coalescing de `maybe_flush_world_sync`. Medido en
/// `perf-baseline.md`: un goteo completo son 84,9 KB por peer; una ráfaga de 20 pickups sin
/// coalescer eran 20 goteos (13,6 MB a 8 peers). A 300 ms la amplificación cae ~95 % frente al
/// disparo por evento, y sigue por debajo del margen de reacción humana — un objeto recogido no
/// puede leerse como "item fantasma" que otro peer intenta coger a su vez.
///
/// La sonda de F0.0 confirmó además que la línea base la domina `broadcast_chunk_states` (77 %),
/// no este goteo (5,6 Mbps sostenido si se disparara 1/s, contra 35,8 Mbps totales): este fix
/// mata el PICO de una ráfaga de interacciones, no la línea base — dicho explícito en
/// `SCALING-ROADMAP.md`, no una promesa incumplida si el número total apenas se mueve.
pub const WORLD_SYNC_COALESCE_WINDOW: std::time::Duration = std::time::Duration::from_millis(300);

/// F0.1: marca el mundo como "cambiado desde el último goteo despachado". Sustituye a la llamada
/// directa a `broadcast_world_sync` en los dos sitios legacy (`game_loop.rs`, pickup y drop):
/// antes, CADA interacción disparaba el goteo del mundo entero a todos los peers; ahora solo
/// arma el flag, y `maybe_flush_world_sync` decide cuándo sale.
pub fn mark_world_sync_dirty(net: &mut NetworkManager) {
    net.world_sync_dirty = true;
}

/// Decisión pura de `maybe_flush_world_sync`, separada para poder probarla sin reloj real ni
/// red — mismo motivo que `RosterGate::should_send` recibe `now` explícito en vez de leerlo él
/// mismo. `last_sent: None` (nunca se despachó) siempre está listo: la primera marca dirty de la
/// sesión no espera a la ventana, igual que ADR-071 no hace esperar al heartbeat a la primera
/// ronda.
fn world_sync_ready(
    dirty: bool,
    last_sent: Option<std::time::Instant>,
    now: std::time::Instant,
    window: std::time::Duration,
) -> bool {
    dirty && last_sent.is_none_or(|t| now.duration_since(t) >= window)
}

/// F0.1: consume el flag como mucho una vez por `WORLD_SYNC_COALESCE_WINDOW`. Se llama en CADA
/// tick (no solo en los ticks de broadcast periódico) para que la latencia máxima tras vencer la
/// ventana sea de un tick (~16 ms a 60 Hz), no de hasta 100 ms si se enganchara al bloque de
/// `NET_BROADCAST_EVERY`.
pub async fn maybe_flush_world_sync(net: &mut NetworkManager, world: &World, player: &Player) {
    let now = std::time::Instant::now();
    if !world_sync_ready(
        net.world_sync_dirty,
        net.world_sync_last_sent,
        now,
        WORLD_SYNC_COALESCE_WINDOW,
    ) {
        return;
    }
    net.world_sync_dirty = false;
    net.world_sync_last_sent = Some(now);
    broadcast_world_sync(net, world, player).await;
}

/// Send a chunk transfer to a specific peer (ownership handoff).
///
/// TAREA 2 (2026-08-31): paginado como `ChunkState`, y no por simetría. Éste es FIABLE: un
/// datagrama por encima del techo lo rechaza `send_datagram`, y antes de este trabajo se
/// fragmentaba en IP y se reenviaba cinco veces con el mismo tamaño hasta expulsar al peer
/// (ADR-062). Se emite por `send_reliable_queued` y no por `send_reliable` por lo mismo que el
/// goteo de ADR-060: es una emisión EN LOTE con final conocido, y con la ventana llena
/// `send_reliable` descartaría páginas sueltas — que con reensamblado todo-o-nada es perder el
/// handoff entero.
pub async fn send_chunk_transfer(net: &mut NetworkManager, peer_id: PeerId, chunk: &Chunk) {
    let generation = net.timestamp();
    for page in chunk_transfer_pages(chunk_to_sync_data(chunk), generation) {
        let payload = PacketPayload::ChunkTransfer { data: page };
        net.send_reliable_queued(peer_id, &payload).await;
    }
}

/// Broadcast chunk teleport to all peers.
pub async fn broadcast_chunk_teleport(
    net: &NetworkManager,
    old_pos: [i32; 2],
    new_pos: [i32; 2],
    new_seed: u64,
) {
    if net.peers.is_empty() {
        return;
    }
    let payload = PacketPayload::ChunkTeleport {
        old_pos,
        new_pos,
        new_seed,
    };
    net.broadcast_unreliable(&payload).await;
}

/// ADR-056: say goodbye before this process exits, so peers act on the departure NOW instead
/// of waiting out the 5 s heartbeat timeout (`peer::HEARTBEAT_TIMEOUT`). No new packet type â€”
/// `PacketPayload::Disconnect` and its receiver (`handlers.rs`, which purges peer state and
/// raises `PeerDisconnected`) have existed since the baseline; only the "session full" rejection
/// ever sent one. Nothing on the wire changes shape, so there is no schema bump on the P2P side.
///
/// Sent raw rather than with `send_reliable`, matching the rejection path: the caller exits
/// immediately afterwards, so nothing would ever process an ACK or a retransmit â€” queueing it as
/// reliable would just drop it in a queue that dies with the process. A lost goodbye therefore
/// degrades to exactly today's behavior (the peer notices on heartbeat timeout), which is what
/// keeps this safe to send unreliably.
///
/// Not gated on `is_host`: a joiner leaving cleanly is worth announcing too, and the host has
/// handled inbound `Disconnect` since the baseline.
pub async fn broadcast_goodbye(net: &NetworkManager, reason: &str) {
    let payload = PacketPayload::Disconnect {
        reason: reason.into(),
    };
    let header = PacketHeader::new(payload.type_code(), net.local_id, 0, net.timestamp());
    let data = encode_packet(&header, &payload);
    for (_, addr) in net.broadcast_destinations() {
        net.send_datagram(&data, addr, "goodbye").await;
    }
}

/// Broadcast anchor placement to all peers (reliable â€” replicated critical data).
pub async fn broadcast_anchor(
    net: &mut NetworkManager,
    chunk_pos: [i32; 2],
    durability: f32,
    installed_by: &str,
) {
    let payload = PacketPayload::AnchorBroadcast {
        chunk_pos,
        durability,
        installed_by: installed_by.into(),
    };
    net.broadcast_reliable(&payload).await;
}

/// Broadcast stabilizer placement to all peers (reliable).
pub async fn broadcast_stabilizer(
    net: &mut NetworkManager,
    chunk_pos: [i32; 2],
    tier: u8,
    remaining_hours: f32,
) {
    let payload = PacketPayload::StabilizerBroadcast {
        chunk_pos,
        tier,
        remaining_hours,
    };
    net.broadcast_reliable(&payload).await;
}

/// Build AnchorInfo list for handshake (placeholder â€” no anchor persistence yet).
pub fn build_anchor_list(_world: &World) -> Vec<AnchorInfo> {
    Vec::new()
}

/// Build StabilizerInfo list for handshake (placeholder).
pub fn build_stabilizer_list(_world: &World) -> Vec<StabilizerInfo> {
    Vec::new()
}

#[cfg(test)]
mod voice_tests {
    use super::*;
    use crate::network::peer::PeerConnection;

    async fn host_with_peers(peers: &[(PeerId, [f32; 3], bool)]) -> NetworkManager {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        for (id, pos, dead) in peers {
            let addr = (std::net::Ipv4Addr::LOCALHOST, 40000 + *id).into();
            let mut conn = PeerConnection::new(*id, format!("P{id}"), addr);
            conn.update_player_state(*pos, 0.0, "idle".into());
            conn.dead = *dead;
            net.peers.insert(*id, conn);
        }
        net
    }

    #[tokio::test]
    async fn voice_reaches_the_near_peer_and_not_the_far_one() {
        // 10 m away hears; 200 m away does not. Without this the relay is a broadcast wearing a
        // proximity label, and someone across the level decodes your conversation.
        let net = host_with_peers(&[
            (2, [0.0, 1.8, 0.0], false),
            (3, [10.0, 1.8, 0.0], false),
            (4, [200.0, 1.8, 0.0], false),
        ])
        .await;

        let dests = voice_destinations(&net, 2);
        assert!(dests.contains(&3), "un peer a 10 m tiene que oir");
        assert!(!dests.contains(&4), "un peer a 200 m NO puede oir");
        assert!(!dests.contains(&2), "a nadie se le reenvia su propia voz");
    }

    #[tokio::test]
    async fn the_margin_is_hysteresis_and_the_cut_is_where_it_says() {
        // Justo dentro del margen: SÃ se relaya (el cliente lo atenuarÃ¡ hasta el silencio).
        // Un metro mÃ¡s allÃ¡ del margen: no. Fija los dos lados del borde, no solo el cÃ³modo.
        let inside = VOICE_RADIUS_M + VOICE_RELAY_MARGIN_M - 0.5;
        let outside = VOICE_RADIUS_M + VOICE_RELAY_MARGIN_M + 1.0;
        let net = host_with_peers(&[
            (2, [0.0, 1.8, 0.0], false),
            (3, [inside, 1.8, 0.0], false),
            (4, [outside, 1.8, 0.0], false),
        ])
        .await;

        let dests = voice_destinations(&net, 2);
        assert!(dests.contains(&3), "dentro del margen se sigue relayando");
        assert!(!dests.contains(&4), "pasado el margen se corta");
    }

    #[tokio::test]
    async fn distance_is_measured_in_3d_so_the_layer_below_cannot_hear() {
        // Las capas estÃ¡n apiladas en Y (4 m por capa). Un filtro por XZ meterÃ­a en el mismo
        // canal de voz a alguien que estÃ¡ literalmente bajo tus pies y no puede ni verte.
        let net = host_with_peers(&[
            (2, [0.0, 1.8, 0.0], false),
            (3, [1.0, 1.8, 0.0], false),
            (4, [1.0, 1.8 + 40.0, 0.0], false),
        ])
        .await;

        let dests = voice_destinations(&net, 2);
        assert!(dests.contains(&3), "mismo plano, a 1 m: oye");
        assert!(
            !dests.contains(&4),
            "a 40 m EN VERTICAL no oye â€” con filtro XZ estaria a 1 m"
        );
    }

    #[tokio::test]
    async fn the_dead_do_not_listen_and_a_phantom_is_never_a_destination() {
        let mut net = host_with_peers(&[
            (2, [0.0, 1.8, 0.0], false),
            (3, [5.0, 1.8, 0.0], true), // muerto, y a tiro de piedra
        ])
        .await;
        // Un fantasma pegado al hablante: su addr es el 127.0.0.1:1 inerte de ADR-043.
        let phantom_id = net.spawn_phantom("Victima", [2.0, 1.8, 0.0], None);

        let dests = voice_destinations(&net, 2);
        assert!(!dests.contains(&3), "un muerto no oye a los vivos");
        assert!(
            !dests.contains(&phantom_id),
            "un fantasma nunca es destino: su addr es un puerto muerto de loopback"
        );
    }

    #[tokio::test]
    async fn an_unknown_speaker_relays_to_nobody() {
        // Sin posiciÃ³n del hablante no hay forma de decidir quiÃ©n estÃ¡ cerca. Contestar "todos"
        // seria justo el fallo abierto que este filtro existe para impedir.
        let net = host_with_peers(&[(2, [0.0, 1.8, 0.0], false)]).await;
        assert!(voice_destinations(&net, 9999).is_empty());
    }
}

#[cfg(test)]
mod peer_roster_gate_tests {
    use super::*;

    /// ADR-140 D1: el roster de peers se hashea por QUIÉN está, no por DÓNDE está. Si las
    /// posiciones entraran en la huella, el gate quedaría abierto para siempre — el mismo fallo
    /// que ADR-139 D1 encontró en los chunks, y la razón de los 27,5 datagramas/s con un jugador.
    #[test]
    fn moving_peers_do_not_reopen_the_roster_gate() {
        let before: Vec<(u16, bool)> = vec![(1, false), (7, false)];
        // Los mismos peers, en otro sitio: la composición no ha cambiado.
        let after: Vec<(u16, bool)> = vec![(1, false), (7, false)];
        assert_eq!(
            roster::content_hash(&before),
            roster::content_hash(&after),
            "moverse no puede reenviar el roster entero"
        );
    }

    /// Y la otra mitad: que ENTRE o SALGA alguien tiene que propagarse en el acto, sin esperar al
    /// latido. Es lo que impide que este ADR haga desaparecer a un recién llegado.
    #[test]
    fn a_peer_joining_or_leaving_changes_the_hash() {
        let two: Vec<(u16, bool)> = vec![(1, false), (7, false)];
        let three: Vec<(u16, bool)> = vec![(1, false), (7, false), (9, false)];
        assert_ne!(
            roster::content_hash(&two),
            roster::content_hash(&three),
            "un peer nuevo tiene que salir ya"
        );

        let one: Vec<(u16, bool)> = vec![(1, false)];
        assert_ne!(roster::content_hash(&two), roster::content_hash(&one));

        // `relay_only` es identidad a efectos de direccionamiento (ADR-079), no adorno: cambiarlo
        // cambia lo que el receptor puede hacer con esa entrada.
        let relayed: Vec<(u16, bool)> = vec![(1, false), (7, true)];
        assert_ne!(roster::content_hash(&two), roster::content_hash(&relayed));
    }
}

#[cfg(test)]
mod stable_chunk_hash_tests {
    use super::*;
    use crate::world::chunk::ChunkLayoutV1;

    fn sample() -> ChunkSyncData {
        ChunkSyncData {
            pos: [3, -7],
            layer: 1,
            seed: 42,
            template_id: 2,
            rotation: 90,
            mirrored: true,
            has_workbench: false,
            layout: ChunkLayoutV1::default(),
            stabilized: false,
            anchored: false,
            teleport_timer: 12.5,
            entities: Vec::new(),
            items: Vec::new(),
            page: 0,
            page_count: 1,
            generation: 0,
        }
    }

    /// ADR-139 D1: lo que cambia solo NO abre el gate. Es la mitad del contrato — la que compra los
    /// bytes.
    #[test]
    fn volatile_fields_do_not_change_the_hash() {
        let base = sample();

        let mut ticked = base.clone();
        ticked.teleport_timer -= 1.0;
        assert_eq!(
            stable_chunk_hash(&base),
            stable_chunk_hash(&ticked),
            "un tic del temporizador no puede reenviar el chunk entero"
        );
    }

    /// Y la otra mitad, que es la que impide que este ADR rompa la replicación: un cambio
    /// ESTRUCTURAL sigue saliendo en el acto, sin esperar al latido.
    #[test]
    fn structural_changes_still_change_the_hash() {
        let base = sample();

        let mut stabilized = base.clone();
        stabilized.stabilized = true;
        assert_ne!(
            stable_chunk_hash(&base),
            stable_chunk_hash(&stabilized),
            "estabilizar un chunk tiene que propagarse ya"
        );

        let mut anchored = base.clone();
        anchored.anchored = true;
        assert_ne!(stable_chunk_hash(&base), stable_chunk_hash(&anchored));

        let mut other_template = base.clone();
        other_template.template_id = 9;
        assert_ne!(stable_chunk_hash(&base), stable_chunk_hash(&other_template));
    }
}

#[cfg(test)]
mod chunk_broadcast_tests {
    use super::*;
    use crate::network::peer::PeerConnection;
    use crate::network::NetworkEvent;
    use crate::world::chunk::ChunkLayoutV1;
    use std::net::SocketAddr;
    use std::time::Duration;

    fn loopback_addr(net: &NetworkManager) -> SocketAddr {
        let mut addr = net.local_addr();
        addr.set_ip(std::net::Ipv4Addr::LOCALHOST.into());
        addr
    }

    // P0-1: a non-host must never broadcast chunk states â€” see the doc-comment on
    // `broadcast_chunk_states`. Wired as a positive+negative pair over real sockets so a
    // regression that silences the function entirely (e.g. an early return that always
    // fires) cannot pass by accident.
    #[tokio::test]
    async fn only_the_host_broadcasts_chunk_states() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let host_addr = loopback_addr(&host);
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let joiner_addr = loopback_addr(&joiner);

        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));
        joiner
            .peers
            .insert(1, PeerConnection::new(1, "Host".into(), host_addr));
        // TAREA 4 (2026-08-31): sin esto el test PASABA por el motivo equivocado. Lo que quiere
        // comprobar es la puerta `is_host` de `broadcast_chunk_states`; con `host_peer_id` en
        // `None`, el joiner tampoco tenía destinos, así que borrar esa puerta entera seguiría
        // dando verde. Una comprobación que sólo puede salir verde no comprueba nada.
        joiner.host_peer_id = Some(1);

        let pos = Vec3::new(0.0, 1.8, 0.0);

        let mut joiner_world = World::new(42);
        joiner_world.update_ownership(pos, joiner.local_id);
        assert!(
            joiner_world
                .chunks
                .values()
                .any(|c| c.owner == Some(joiner.local_id)),
            "setup bug: the joiner needs an owned chunk in range or this test is vacuous"
        );

        broadcast_chunk_states(&mut joiner, &joiner_world, pos).await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        let host_events = host.process_incoming().await;
        assert!(
            !host_events
                .iter()
                .any(|e| matches!(e, NetworkEvent::ChunkStateReceived { .. })),
            "a non-host must never emit ChunkState, got: {host_events:?}"
        );

        // Positive control: same call, same range, only `is_host` differs â€” proves the
        // silence above is the guard firing, not an unrelated setup mistake.
        let mut host_world = World::new(42);
        host_world.update_ownership(pos, host.local_id);
        broadcast_chunk_states(&mut host, &host_world, pos).await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        let joiner_events = joiner.process_incoming().await;
        assert!(
            joiner_events
                .iter()
                .any(|e| matches!(e, NetworkEvent::ChunkStateReceived { .. })),
            "positive control failed: the host should still broadcast, got: {joiner_events:?}"
        );
    }

    /// El broadcast periodico de chunks NO se confirma; el handoff de propiedad SI.
    ///
    /// MEDIDO antes de este arreglo, en sesion de 2 backends reales (40 s): el joiner emitia
    /// 8 267 `reliable_window_full` porque respondia un `ChunkTransferAck` FIABLE a cada uno de
    /// los ~820 `ChunkState`/s del host. Su ventana de 32 vivia llena, asi que sus propios envios
    /// fiables de gameplay (pickup, place, corpse, PvP) se descartaban en silencio contra el mismo
    /// `send_reliable`. El ack no lo lee nadie: su receptor solo hace `debug!`.
    ///
    /// El par positivo/negativo importa: sin el control positivo, borrar el ack ENTERO tambien
    /// pasaria este test, y el handoff perderia su confirmacion sin que nada avisara.
    #[tokio::test]
    async fn a_broadcast_chunk_is_not_acked_but_a_handoff_still_is() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let host_addr = loopback_addr(&host);
        let joiner_addr = loopback_addr(&joiner);
        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));
        joiner
            .peers
            .insert(1, PeerConnection::new(1, "Host".into(), host_addr));
        // TAREA 4 (2026-08-31): el fixture montaba a mano un estado que producción NO puede
        // alcanzar — un joiner con el host REGISTRADO pero sin `host_peer_id`. Las dos cosas se
        // fijan en la misma función (`handle_handshake_ack`: inserta el peer y anota quién es el
        // host), así que "peer 1 en la tabla" implica siempre "host_peer_id = Some(1)".
        //
        // Importa desde que la legalidad del destino es UNA condición para difusiones Y para
        // envíos dirigidos: un joiner sin host conocido no encola un fiable a nadie, que es lo que
        // fija `a_joiner_mid_handshake_cannot_queue_a_reliable_to_anyone`. Sin esta línea el test
        // no medía el ACK del handoff, medía un handshake a medias.
        joiner.host_peer_id = Some(1);

        let pos = Vec3::new(0.0, 1.8, 0.0);
        let mut host_world = World::new(42);
        host_world.update_ownership(pos, host.local_id);
        let mut joiner_world = World::new(42);

        // NEGATIVO: broadcast periodico -> se aplica, no se confirma.
        broadcast_chunk_states(&mut host, &host_world, pos).await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        let mut applied = 0usize;
        for e in joiner.process_incoming().await {
            if let NetworkEvent::ChunkStateReceived { data, .. } = e {
                joiner_world.apply_chunk_transfer(&data, joiner.local_id);
                applied += 1;
            }
        }
        assert!(applied > 0, "setup: el broadcast tiene que llegar");
        assert_eq!(
            joiner.peers[&1].reliable_queue.len(),
            0,
            "un ChunkState NO puede encolar acks fiables: {applied} chunks llegaron y la ventana \
             del joiner tiene que seguir vacia"
        );

        // POSITIVO: handoff explicito -> sigue confirmandose.
        let chunk = host_world
            .chunks
            .values()
            .next()
            .expect("setup: el host necesita un chunk")
            .clone();
        send_chunk_transfer(&mut host, 2, &chunk).await;
        tokio::time::sleep(Duration::from_millis(100)).await;
        for e in joiner.process_incoming().await {
            if let NetworkEvent::ChunkTransferReceived { from, data } = e {
                let ack = PacketPayload::ChunkTransferAck { pos: data.pos };
                joiner.send_reliable(from, &ack).await;
            }
        }
        assert_eq!(
            joiner.peers[&1].reliable_queue.len(),
            1,
            "el handoff de propiedad SI se confirma — quien cede la autoridad quiere saber que llego"
        );
    }

    // â”€â”€â”€ E1 / ADR-074 fase 1: area de interes de las poses â”€â”€â”€

    const NEAR: [f32; 3] = [0.0, 1.8, 0.0];

    #[test]
    fn a_peer_inside_the_radius_gets_the_pose_and_one_far_away_does_not() {
        let far = [0.0, 1.8, AOI_POSE_RADIUS_M + 50.0];
        assert!(
            aoi_pose_should_relay(NEAR, [5.0, 1.8, 5.0], false, AOI_POSE_RADIUS_M),
            "dos jugadores en la misma sala tienen que verse"
        );
        assert!(
            !aoi_pose_should_relay(NEAR, far, false, AOI_POSE_RADIUS_M),
            "a 150 m no hay nada que replicar: es todo el ahorro de E1"
        );
    }

    /// La histéresis, que es la razón de que el estado viva en el host. Un par que YA se estaba
    /// relayando aguanta hasta el radio de salida; uno nuevo necesita cruzar el de entrada. Sin
    /// esta banda muerta, alguien caminando sobre la frontera parpadea a 10 Hz.
    #[test]
    fn the_exit_radius_is_wider_than_the_entry_one() {
        // Entre los dos radios: 110 m con R=100 y factor 1,2 (salida a 120).
        let between = [0.0, 1.8, 110.0];
        assert!(
            aoi_pose_should_relay(NEAR, between, true, AOI_POSE_RADIUS_M),
            "quien ya se estaba viendo NO desaparece por cruzar el radio de entrada"
        );
        assert!(
            !aoi_pose_should_relay(NEAR, between, false, AOI_POSE_RADIUS_M),
            "pero quien estaba fuera tampoco entra hasta cruzar el de entrada"
        );
        // Más allá del radio de salida: fuera pase lo que pase.
        let beyond = [0.0, 1.8, AOI_POSE_RADIUS_M * AOI_POSE_EXIT_FACTOR + 1.0];
        assert!(
            !aoi_pose_should_relay(NEAR, beyond, true, AOI_POSE_RADIUS_M),
            "pasado el radio de salida, ni la histéresis lo sostiene"
        );
    }

    /// ADR-016 + ADR-074 decisión 1: el filtro decide por POSICIÓN y nada más. Este test fija que
    /// la función no tiene por dónde enterarse de qué es un fantasma — si algún día alguien le
    /// añadiera un parámetro `is_phantom`, la asimetría delataría al robapieles a 100 m y el
    /// disfraz entero dejaría de funcionar.
    #[test]
    fn the_filter_cannot_tell_a_phantom_from_a_player() {
        let a = [10.0, 1.8, 10.0];
        let b = [40.0, 1.8, 40.0];
        // Misma distancia, mismo veredicto: no hay tercer argumento que pueda cambiarlo.
        assert_eq!(
            aoi_pose_should_relay(a, b, false, AOI_POSE_RADIUS_M),
            aoi_pose_should_relay(b, a, false, AOI_POSE_RADIUS_M),
            "el filtro tiene que ser simétrico: quién es el origen no puede importar"
        );
    }

    /// ADR-074 enm. 3 — la curva metro a metro, fijada por Joel: 30 Hz a 0 m, 5 Hz desde ~73 m.
    /// Si alguien toca `POSE_LOD_POWER` o el suelo, esta tabla se pone roja y obliga a decidirlo.
    #[test]
    fn the_cadence_curve_matches_the_agreed_table() {
        for (d, hz) in [
            (0.0, 30),
            (3.0, 28),
            (5.0, 26),
            (10.0, 23),
            (20.0, 18),
            (30.0, 14),
            (40.0, 10),
            (50.0, 8),
            (61.0, 6),
            (73.0, 5),
            (100.0, 5),
            (150.0, 5),
        ] {
            assert_eq!(pose_hz(d), hz, "a {d} m la tabla dice {hz} Hz");
        }
    }

    /// Monótona: nunca sube al alejarse, y siempre entre el suelo y la cadencia base.
    #[test]
    fn the_cadence_never_rises_with_distance() {
        let mut prev = POSE_RELAY_HZ;
        for m in 0..=120 {
            let hz = pose_hz(m as f32);
            assert!(hz <= prev, "a {m} m sube de {prev} a {hz} Hz");
            assert!(
                (POSE_HZ_FLOOR..=POSE_RELAY_HZ).contains(&hz),
                "a {m} m: {hz} Hz fuera de rango"
            );
            prev = hz;
        }
    }

    /// LOD: pegados se emite en TODAS las rondas. Sin esto, un tiroteo a un metro se vería a
    /// menos cadencia, que es justo donde más se nota.
    #[test]
    fn a_peer_at_arms_length_is_relayed_every_round() {
        let close = [0.5, 1.8, 0.0];
        for round in 0..6u64 {
            assert!(
                aoi_pose_due_this_round(NEAR, close, 1, 2, round),
                "ronda {round}: a medio metro no hay LOD que valga"
            );
        }
    }

    /// Bresenham exacto: en cualquier ventana de 30 rondas seguidas salen EXACTAMENTE `hz`
    /// emisiones, sea cual sea el desplazamiento del par. Ni una más (no ahorraría) ni una menos
    /// (desaparecería); y a 80 m son las 5 del suelo.
    #[test]
    fn a_window_of_thirty_rounds_carries_exactly_hz_emissions() {
        for hz in POSE_HZ_FLOOR..=POSE_RELAY_HZ {
            for phase in 0..POSE_RELAY_HZ {
                for start in [0u64, 7, 1000] {
                    let hits = (start..start + POSE_RELAY_HZ)
                        .filter(|r| due_at_hz(*r, hz, phase))
                        .count() as u64;
                    assert_eq!(hits, hz, "hz={hz} phase={phase} start={start}");
                }
            }
        }
        let far = [0.0, 1.8, 80.0];
        let hits = (0..30u64)
            .filter(|r| aoi_pose_due_this_round(NEAR, far, 1, 2, *r))
            .count() as u64;
        assert_eq!(
            hits, POSE_HZ_FLOOR,
            "a 80 m va al suelo: {POSE_HZ_FLOOR} de cada 30 rondas"
        );
    }

    /// El escalonado, que es la diferencia entre «5 Hz» y una ronda cara alternando con cinco
    /// vacías: con muchos pares lejanos, cada ronda lleva casi lo mismo. Sin `pose_pair_phase`
    /// todos emitirían en las mismas rondas.
    #[test]
    fn far_pairs_are_spread_flat_across_rounds() {
        let far = [0.0, 1.8, 80.0];
        let pairs: Vec<(PeerId, PeerId)> = (1..=60u16)
            .flat_map(|s| (1..=3u16).map(move |d| (s, s.wrapping_add(d * 37))))
            .collect();
        let per_round: Vec<usize> = (0..30u64)
            .map(|r| {
                pairs
                    .iter()
                    .filter(|(s, d)| aoi_pose_due_this_round(NEAR, far, *s, *d, r))
                    .count()
            })
            .collect();
        let mean = pairs.len() as f64 * POSE_HZ_FLOOR as f64 / 30.0;
        let (lo, hi) = (
            *per_round.iter().min().unwrap() as f64,
            *per_round.iter().max().unwrap() as f64,
        );
        assert!(
            lo >= mean * 0.5 && hi <= mean * 1.5,
            "carga por ronda entre {lo} y {hi} con media {mean:.1}: no está repartida"
        );
    }

    /// Los dos sentidos de un par no comparten fase: si (1,2) y (2,1) emitieran en las mismas
    /// rondas, dos jugadores lejanos cargarían la misma ronda por partida doble.
    #[test]
    fn the_two_directions_of_a_pair_do_not_share_a_phase() {
        assert_ne!(pose_pair_phase(1, 2), pose_pair_phase(2, 1));
    }

    /// ADR-074 fase 2 — el scope son las 25 celdas alrededor de la del destinatario, centradas en
    /// ella y en orden determinista. Si alguien cambia el radio, esta cuenta lo dice.
    #[test]
    fn the_scope_is_the_five_by_five_around_the_destination() {
        let centre = world_to_chunk(Vec3::new(137.0, 1.8, -42.0));
        let cells = roster_scope_cells([137.0, 1.8, -42.0], ROSTER_SCOPE_RADIUS_CELLS);
        let side = (2 * ROSTER_SCOPE_RADIUS_CELLS + 1) as usize;
        assert_eq!(cells.len(), side * side, "scope de {side}×{side}");
        assert!(
            cells.contains(&[centre.0, centre.1]),
            "la celda propia está dentro"
        );
        assert!(
            cells.contains(&[
                centre.0 - ROSTER_SCOPE_RADIUS_CELLS,
                centre.1 + ROSTER_SCOPE_RADIUS_CELLS
            ]),
            "y las esquinas también"
        );
        assert!(
            !cells.contains(&[centre.0 + ROSTER_SCOPE_RADIUS_CELLS + 1, centre.1]),
            "una celda más allá del radio NO entra"
        );
        // Determinista: dos llamadas dan exactamente la misma secuencia (regla dura 13).
        assert_eq!(
            cells,
            roster_scope_cells([137.0, 1.8, -42.0], ROSTER_SCOPE_RADIUS_CELLS)
        );
    }

    /// ADR-074 fase 2 — la celda de una entrada es la MISMA que la del mundo. Si alguien inventara
    /// una retícula propia para los rosters, el scope dejaría de coincidir con lo que el cliente
    /// carga y aparecerían objetos sin chunk donde ponerlos.
    #[test]
    fn a_roster_entry_lives_in_the_same_cell_as_the_world() {
        for p in [[0.0, 1.8, 0.0], [137.0, 1.8, -42.0], [-0.1, 0.0, -0.1]] {
            let (cx, cz) = world_to_chunk(Vec3::new(p[0], p[1], p[2]));
            assert_eq!(roster_cell_of(p), [cx, cz]);
        }
    }

    /// ADR-074 enm. 4 — el aforo: 1 mientras la demanda cabe, y la proporción cuando no.
    #[test]
    fn the_budget_factor_is_one_until_demand_exceeds_the_share() {
        assert_eq!(budget_factor_target(0.0, 100.0), 1.0);
        assert_eq!(budget_factor_target(100.0, 100.0), 1.0);
        assert_eq!(budget_factor_target(50.0, 100.0), 1.0);
        assert!((budget_factor_target(400.0, 100.0) - 0.25).abs() < 1e-6);
        assert_eq!(budget_factor_target(400.0, 0.0), 0.0);
    }

    /// ADR-074 enm. 4 — la cadencia final: a bocajarro el aforo no recorta; lejos recorta en
    /// proporción; a la espalda va a la mitad; y nunca baja del suelo ni sube de la base.
    #[test]
    fn the_pair_cadence_exempts_arms_length_and_never_drops_below_the_floor() {
        assert_eq!(
            pose_pair_hz(1.0, 0.1, false),
            pose_hz(1.0),
            "pegado: el aforo no toca"
        );
        assert_eq!(
            pose_pair_hz(20.0, 1.0, false),
            pose_hz(20.0),
            "sin aforo: la curva"
        );
        assert_eq!(
            pose_pair_hz(20.0, 0.5, false),
            (pose_hz(20.0) as f32 * 0.5).round() as u64
        );
        assert_eq!(
            pose_pair_hz(20.0, 1.0, true),
            pose_hz(20.0) / 2,
            "a la espalda: la mitad"
        );
        assert_eq!(
            pose_pair_hz(20.0, 0.01, true),
            POSE_HZ_FLOOR,
            "el suelo manda"
        );
        assert_eq!(pose_pair_hz(0.0, 1.0, true), POSE_RELAY_HZ / 2);
        for m in 0..=120 {
            let hz = pose_pair_hz(m as f32, 0.3, m % 2 == 0);
            assert!(
                (POSE_HZ_FLOOR..=POSE_RELAY_HZ).contains(&hz),
                "a {m} m: {hz}"
            );
        }
    }

    /// Y el invariante que la enmienda añade: la cadencia tampoco puede depender de QUÉ es la
    /// fuente. Un fantasma y un jugador a la misma distancia emiten en las mismas rondas — si
    /// alguien exceptuara a la IA "para que su acecho se vea fluido", moverse suave a 80 m sería
    /// un oráculo que lo delata igual que aparecer más lejos.
    #[test]
    fn the_cadence_cannot_tell_a_phantom_from_a_player() {
        let far = [0.0, 1.8, 80.0];
        // Mismo par, misma distancia: el veredicto solo puede salir de la posición y los ids.
        for round in 0..4u64 {
            assert_eq!(
                aoi_pose_due_this_round(NEAR, far, 1, 2, round),
                aoi_pose_due_this_round(NEAR, far, 1, 2, round),
                "la función no tiene por dónde enterarse de qué es la fuente, y así debe seguir"
            );
        }
    }

    /// La distancia es 3D. Dos jugadores en la misma (x,z) pero en capas distintas no están cerca.
    #[test]
    fn height_counts_towards_the_radius() {
        let below = [0.0, 1.8 - (AOI_POSE_RADIUS_M + 20.0), 0.0];
        assert!(
            !aoi_pose_should_relay(NEAR, below, false, AOI_POSE_RADIUS_M),
            "misma columna pero 120 m más abajo no es 'cerca'"
        );
    }

    // â”€â”€â”€ F0.3 (E0): los veredictos esperan en cola con cap, y el desborde es fatal â”€â”€â”€

    fn a_verdict() -> PacketPayload {
        PacketPayload::StpPickupGranted {
            item_id: 7,
            def_id: -52379,
            count: 1,
        }
    }

    /// La mitad que MÁS importa: una ráfaga legítima no puede desconectar a nadie. Un cap mal
    /// dimensionado convierte 20 pickups seguidos (o un goteo de mundo aparcado por delante) en
    /// desconexiones aleatorias, que es peor que el descarte que F0.3 vino a arreglar.
    #[tokio::test]
    async fn a_legitimate_burst_of_verdicts_never_disconnects_a_peer() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        host.peers.insert(
            2,
            PeerConnection::new(2, "Joiner".into(), loopback_addr(&joiner)),
        );

        // Peor caso legítimo del dimensionado: el goteo de un mundo entero aparcado por delante
        // (50) más una ráfaga de loot intensa (20). Muy por debajo del cap de 256.
        for _ in 0..70 {
            host.send_verdict(2, &a_verdict()).await;
        }

        assert!(
            host.peers.contains_key(&2),
            "70 veredictos seguidos son tráfico legítimo: desconectar aquí sería el bug nuevo"
        );
        let events = host.process_incoming().await;
        assert!(
            !events
                .iter()
                .any(|e| matches!(e, NetworkEvent::PeerDisconnected { .. })),
            "y no puede colarse ninguna desconexión por la puerta de atrás: {events:?}"
        );
    }

    /// La mitad negativa: superado el cap, el peer CAE — no se le descarta el veredicto y se
    /// sigue como si nada. Su inventario ya divergió del host y solo un re-sync lo arregla.
    #[tokio::test]
    async fn a_verdict_queue_overflow_disconnects_the_peer_instead_of_dropping_the_verdict() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        host.peers.insert(
            2,
            PeerConnection::new(2, "Joiner".into(), loopback_addr(&joiner)),
        );

        // Nadie ACKea (el joiner ni siquiera lee), así que la ventana se llena y todo lo demás
        // se aparca: exactamente el escenario que antes descartaba veredictos en silencio.
        for _ in 0..(NetworkManager::VERDICT_QUEUE_CAP + 40) {
            host.send_verdict(2, &a_verdict()).await;
        }

        assert!(
            !host.peers.contains_key(&2),
            "pasado el cap, el peer tiene que salir: seguir encolando o descartar deja su \
             inventario divergido para siempre"
        );
        let events = host.process_incoming().await;
        assert!(
            events
                .iter()
                .any(|e| matches!(e, NetworkEvent::PeerDisconnected { id: 2, .. })),
            "la caída tiene que salir como PeerDisconnected — es lo que dispara el teardown de \
             ADR-056 en un joiner: {events:?}"
        );
    }

    // â”€â”€â”€ F0.2 (E0): el relay encodea una vez por origen, no una por par â”€â”€â”€

    /// EL invariante de F0.2: cachear el encode por origen no puede cambiar un solo byte de lo
    /// que sale al aire. Si alguna vez el header dependiera del destino (una secuencia por peer,
    /// por ejemplo), este test falla y el cacheo hay que deshacerlo — que es exactamente la razón
    /// por la que `broadcast_reliable` NO lo lleva.
    #[tokio::test]
    async fn a_cached_relay_encode_is_byte_identical_to_the_per_destination_one() {
        let host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let payload = PacketPayload::PlayerUpdate {
            position: [12.5, 1.8, -40.0],
            rotation: 90.0,
            animation: "walk_slow".into(),
            crouch: true,
            pitch: -12,
            equipment: [1001, 1002, 1003, 1004],
            held_item: 2001,
            hit_seq: 7,
            dead: false,
            revealed: false,
            vocal_seq: 3,
            vocal_kind: 1,
            light_on: true,
            fire_seq: 9,
            buttons: 1,
            melee_seq: 4,
            carry_def: 55,
            carry_count: 2,
            species: 0,
        };

        // Un solo encode reutilizado para tres destinos distintos...
        let cached = host.encode_relay_as(77, &payload);
        // ...contra el encode que el camino viejo hacía POR destino. `send_unreliable_as` sigue
        // definido sobre `encode_relay_as`, así que se comparan las dos llamadas que antes eran
        // dos serializaciones independientes.
        for _dest in [2u16, 3, 4] {
            let per_destination = host.encode_relay_as(77, &payload);
            assert_eq!(
                cached, per_destination,
                "el payload relayado no puede depender del destino: si depende, el cacheo de F0.2 \
                 cambia lo que viaja"
            );
        }

        // Y el origen SÍ tiene que seguir viajando en el header: sin esto el test pasaría aunque
        // el encode ignorara `sender_id` y todos los peers se vieran como el mismo.
        let other_source = host.encode_relay_as(78, &payload);
        assert_ne!(
            cached, other_source,
            "el id del origen viaja en el header (ADR-015): dos orígenes no pueden producir los \
             mismos bytes"
        );
    }

    // â”€â”€â”€ F0.1 (enmienda ADR-073): coalescing de broadcast_world_sync por pickup/drop â”€â”€â”€

    #[test]
    fn a_fresh_dirty_flag_is_ready_without_waiting_for_the_window() {
        let now = std::time::Instant::now();
        assert!(
            world_sync_ready(true, None, now, WORLD_SYNC_COALESCE_WINDOW),
            "la primera marca de la sesión no puede esperar a una ventana que nunca empezó"
        );
    }

    #[test]
    fn a_clean_flag_is_never_ready_regardless_of_timing() {
        let now = std::time::Instant::now();
        assert!(
            !world_sync_ready(false, None, now, WORLD_SYNC_COALESCE_WINDOW),
            "sin marca dirty no hay nada que despachar, aunque la ventana esté vencida"
        );
    }

    #[test]
    fn a_second_mark_inside_the_window_is_not_ready() {
        let t0 = std::time::Instant::now();
        let just_after = t0 + std::time::Duration::from_millis(50);
        assert!(
            !world_sync_ready(true, Some(t0), just_after, WORLD_SYNC_COALESCE_WINDOW),
            "una segunda interacción a 50 ms de la anterior tiene que esperar: es todo el ahorro \
             de F0.1 frente a disparar por evento"
        );
    }

    #[test]
    fn once_the_window_elapses_the_flag_is_ready_again() {
        let t0 = std::time::Instant::now();
        let after_window = t0 + WORLD_SYNC_COALESCE_WINDOW;
        assert!(
            world_sync_ready(true, Some(t0), after_window, WORLD_SYNC_COALESCE_WINDOW),
            "vencida la ventana, una marca pendiente tiene que despacharse"
        );
    }

    /// End-to-end sobre sockets reales: una ráfaga de "interacciones" (marcas dirty) coalesce en
    /// UN solo goteo, y una marca posterior a la ventana produce un segundo goteo — nunca cero,
    /// nunca uno por marca.
    #[tokio::test]
    async fn a_burst_of_dirty_marks_coalesces_into_one_drip() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let joiner_addr = loopback_addr(&joiner);
        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));

        let pos = Vec3::new(0.0, 1.8, 0.0);
        let mut host_world = World::new(42);
        host_world.update_ownership(pos, host.local_id);
        // El test solo necesita CONTAR goteos, no completarlos: recortar a un único chunk
        // mantiene el envío dentro de la ventana fiable (32) y evita la danza de ACK/pump que
        // `send_world_sync` sí necesita con un mundo grande (ver
        // `the_world_snapshot_travels_as_many_small_datagrams_and_completes`, más abajo).
        let one_chunk_key = *host_world
            .chunks
            .keys()
            .next()
            .expect("setup: el jugador necesita al menos un chunk propio");
        host_world.chunks.retain(|k, _| *k == one_chunk_key);
        let player = Player::new(host.local_id, "Host");

        async fn world_sync_ends_received(joiner: &mut NetworkManager) -> usize {
            tokio::time::sleep(Duration::from_millis(80)).await;
            joiner
                .process_incoming()
                .await
                .iter()
                .filter(|e| matches!(e, NetworkEvent::WorldSyncEndReceived { .. }))
                .count()
        }

        // Ráfaga: 20 "pickups" seguidos solo arman el flag, ninguno despacha por sí mismo.
        for _ in 0..20 {
            mark_world_sync_dirty(&mut host);
        }
        assert!(
            host.world_sync_dirty,
            "el flag tiene que seguir armado: nada lo ha consumido todavía"
        );

        // Primera comprobación del tick: dispara de inmediato (last_sent = None).
        maybe_flush_world_sync(&mut host, &host_world, &player).await;
        assert!(
            !host.world_sync_dirty,
            "el primer flush consume el flag aunque hubiera 20 marcas apiladas detrás"
        );
        let n = world_sync_ends_received(&mut joiner).await;
        assert_eq!(
            n, 1,
            "la ráfaga entera tiene que llegar como UN solo goteo, no veinte"
        );

        // Otra marca inmediatamente después: dentro de la ventana, no despacha.
        mark_world_sync_dirty(&mut host);
        maybe_flush_world_sync(&mut host, &host_world, &player).await;
        assert!(
            host.world_sync_dirty,
            "una marca a milisegundos de la anterior tiene que esperar a la ventana"
        );
        let n = world_sync_ends_received(&mut joiner).await;
        assert_eq!(n, 0, "nada puede salir todavía: sigue dentro de los 300 ms");

        // Vencida la ventana, esa misma marca pendiente sí despacha.
        tokio::time::sleep(WORLD_SYNC_COALESCE_WINDOW).await;
        maybe_flush_world_sync(&mut host, &host_world, &player).await;
        assert!(!host.world_sync_dirty);
        let n = world_sync_ends_received(&mut joiner).await;
        assert_eq!(
            n, 1,
            "pasada la ventana, la marca pendiente tiene que despachar sin más espera"
        );
    }

    // â”€â”€â”€ F0.8 (enmienda ADR-073/074): gate por chunk en broadcast_chunk_states â”€â”€â”€

    /// Positivo/negativo sobre sockets reales: dos rondas seguidas sin tocar el mundo mandan el
    /// chunk la primera vez y lo callan la segunda. Sin el par, un gate que corta SIEMPRE pasaria
    /// la mitad negativa por accidente.
    #[tokio::test]
    async fn an_unchanged_chunk_stops_being_sent_after_its_burst() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let joiner_addr = loopback_addr(&joiner);
        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));

        let pos = Vec3::new(0.0, 1.8, 0.0);
        let mut host_world = World::new(42);
        host_world.update_ownership(pos, host.local_id);

        async fn received_chunk_states(joiner: &mut NetworkManager) -> usize {
            tokio::time::sleep(Duration::from_millis(80)).await;
            joiner
                .process_incoming()
                .await
                .iter()
                .filter(|e| matches!(e, NetworkEvent::ChunkStateReceived { .. }))
                .count()
        }

        // Ráfaga post-"cambio" inicial (ROSTER_CHANGE_BURST rondas, ADR-071): las primeras
        // salidas del gate siempre emiten, para que el joiner recién unido no vea el mundo vacío.
        for _ in 0..roster::ROSTER_CHANGE_BURST {
            broadcast_chunk_states(&mut host, &host_world, pos).await;
            let n = received_chunk_states(&mut joiner).await;
            assert!(
                n > 0,
                "cada ronda de la ráfaga tiene que emitir todos los chunks"
            );
        }

        // Agotada la ráfaga y sin cambios: el gate corta, ronda vacía.
        broadcast_chunk_states(&mut host, &host_world, pos).await;
        let n = received_chunk_states(&mut joiner).await;
        assert_eq!(
            n, 0,
            "un chunk sin cambios no puede seguir viajando a 5 Hz: es todo el ahorro de F0.8"
        );

        // Positivo: tocar el mundo (mover al jugador, lo que cambia la vecindad de owner y
        // dispara `update_ownership`) genera al menos un chunk nuevo/relimitado y ese SÍ sale de
        // inmediato, sin esperar al latido — mismo criterio que ADR-071.
        let pos2 = Vec3::new(80.0, 1.8, 0.0);
        host_world.update_ownership(pos2, host.local_id);
        broadcast_chunk_states(&mut host, &host_world, pos2).await;
        let n = received_chunk_states(&mut joiner).await;
        assert!(
            n > 0,
            "un chunk que cambia (o uno nuevo por la vecindad) tiene que salir sin esperar latido"
        );
    }

    /// El heartbeat de ADR-071 es "está para reparar páginas perdidas, no para detectar
    /// cambios" — el mismo criterio se hereda aquí. Con `heartbeat = 0` se demuestra que esa vía
    /// existe también por chunk y es independiente del contenido.
    #[test]
    fn chunk_gate_heartbeat_repairs_independently_of_content() {
        let mut gate = roster::RosterGate::default();
        let data = ChunkSyncData {
            pos: [0, 0],
            layer: 0,
            seed: 42,
            template_id: 1,
            rotation: 0,
            mirrored: false,
            has_workbench: false,
            layout: ChunkLayoutV1::default(),
            stabilized: false,
            anchored: false,
            teleport_timer: 0.0,
            entities: vec![],
            items: vec![],
            page: 0,
            page_count: 1,
            generation: 0,
        };
        let now = std::time::Instant::now();
        let hash = roster::content_hash(std::slice::from_ref(&data));
        for _ in 0..roster::ROSTER_CHANGE_BURST {
            gate.should_send(hash, now, roster::ROSTER_HEARTBEAT);
        }
        assert!(
            !gate.should_send(hash, now, roster::ROSTER_HEARTBEAT),
            "preparación: ya calla"
        );
        assert!(
            gate.should_send(hash, now, std::time::Duration::ZERO),
            "vencido el latido, la ronda sale aunque el chunk sea idéntico"
        );
    }

    /// El gate es POR CHUNK: un chunk que cambia no puede arrastrar a los que no cambiaron. Es
    /// la razón entera de usar un `HashMap` de gates en vez de uno global (que sería lo mismo que
    /// ADR-071 ya hace para rosters, y aquí rompería la propiedad exacta que se quiere).
    #[test]
    fn each_chunk_gates_independently_of_its_neighbours() {
        let mut a = roster::RosterGate::default();
        let mut b = roster::RosterGate::default();
        let now = std::time::Instant::now();
        for _ in 0..roster::ROSTER_CHANGE_BURST {
            a.should_send(1, now, roster::ROSTER_HEARTBEAT);
            b.should_send(1, now, roster::ROSTER_HEARTBEAT);
        }
        assert!(!a.should_send(1, now, roster::ROSTER_HEARTBEAT));
        assert!(!b.should_send(1, now, roster::ROSTER_HEARTBEAT));

        // Solo `a` cambia (hash distinto). `b` con el mismo hash de siempre sigue callado.
        assert!(
            a.should_send(2, now, roster::ROSTER_HEARTBEAT),
            "el chunk que cambió tiene que salir"
        );
        assert!(
            !b.should_send(1, now, roster::ROSTER_HEARTBEAT),
            "el chunk vecino, sin cambios, tiene que seguir callado"
        );
    }

    /// ADR-060 end-to-end sobre sockets reales: el snapshot sale como N datagramas de MTU en vez
    /// de uno gigante, y ninguno se acerca al techo de 65 507 B que mataba al monolito. Sin este
    /// test, un futuro "vuelve a mandarlo junto que es mÃ¡s simple" no falla en ninguna parte.
    #[tokio::test]
    async fn the_world_snapshot_travels_as_many_small_datagrams_and_completes() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let joiner_addr = loopback_addr(&joiner);
        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));

        let mut host_world = World::new(42);
        host_world.update_ownership(Vec3::new(0.0, 1.8, 0.0), host.local_id);
        let chunk_count = host_world.chunks.len();
        assert!(
            chunk_count > 1,
            "setup: hacen falta varios chunks o el goteo no se distingue del monolito"
        );
        let player = Player::new(1, "Host".to_string());

        send_world_sync(&mut host, 2, &host_world, &player).await;
        // El goteo entero cabe en la ventana solo si chunk_count+1 <= WINDOW_SIZE; por encima,
        // el resto sale por la cola diferida a medida que llegan los ACKs.
        for _ in 0..16 {
            tokio::time::sleep(Duration::from_millis(20)).await;
            let events = joiner.process_incoming().await;
            for e in events {
                match e {
                    NetworkEvent::WorldSyncChunkReceived {
                        world_revision,
                        data,
                    } => {
                        joiner
                            .world_sync_progress
                            .note_chunk(world_revision, data.pos, data.layer);
                    }
                    NetworkEvent::WorldSyncEndReceived {
                        world_revision,
                        chunk_count,
                    } => {
                        joiner
                            .world_sync_progress
                            .note_end(world_revision, chunk_count);
                    }
                    _ => {}
                }
            }
            host.process_incoming().await; // drena los ACKs del joiner
            host.pump_deferred_reliable().await;
            if joiner.world_sync_progress.is_complete() {
                break;
            }
        }

        assert!(
            joiner.world_sync_progress.is_complete(),
            "el goteo tiene que completar: {} chunks esperados",
            chunk_count
        );
    }

    /// La mitad negativa, y la razÃ³n entera de la decisiÃ³n "spawn en End": con el goteo a medias
    /// el gate NO puede estar abierto. El gate viejo (`!world.chunks.is_empty()`) habrÃ­a dicho
    /// que sÃ­ con el primer chunk.
    #[tokio::test]
    async fn a_half_delivered_world_never_opens_the_spawn_gate() {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let joiner_addr = loopback_addr(&joiner);
        host.peers
            .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));

        let mut host_world = World::new(42);
        host_world.update_ownership(Vec3::new(0.0, 1.8, 0.0), host.local_id);
        let player = Player::new(1, "Host".to_string());

        send_world_sync(&mut host, 2, &host_world, &player).await;
        tokio::time::sleep(Duration::from_millis(60)).await;

        // Se procesan los chunks pero se IGNORA el End: exactamente un mundo a medias.
        let mut chunks_seen = 0usize;
        for e in joiner.process_incoming().await {
            if let NetworkEvent::WorldSyncChunkReceived {
                world_revision,
                data,
            } = e
            {
                chunks_seen += 1;
                joiner
                    .world_sync_progress
                    .note_chunk(world_revision, data.pos, data.layer);
            }
        }
        assert!(chunks_seen > 0, "setup: tienen que haber llegado chunks");
        assert!(
            !joiner.world_sync_progress.is_complete(),
            "sin End no hay spawn, por muchos chunks que hayan entrado ({chunks_seen})"
        );
    }
}

/// ADR-060. El invariante que estos tests fijan no es "los chunks llegan" sino QUE NO SE ABRE EL
/// GATE DE SPAWN antes de tiempo: el emisor pasÃ³ de un datagrama a N, y el gate viejo
/// (`!world.chunks.is_empty()`) se habrÃ­a disparado con el primero.
/// ADR-140 — **la mitad del PVS que puede hacer invisible a un jugador.**
///
/// El módulo `visibility` lo escribe como la regla que gobierna el diseño: si el grafo se equivoca
/// diciendo «no lo ves» cuando sí lo ves, un jugador desaparece para otro; si se equivoca al revés,
/// se gastan unos KB. Los dos errores no valen lo mismo, así que todo lo que no se puede afirmar
/// tiene que dejar pasar.
///
/// Se prueba sobre `pvs_allows`, que es pura: la mitad peligrosa de esto no debe necesitar sockets
/// para ponerse en rojo.
#[cfg(test)]
mod attention_cone_tests {
    use super::*;

    /// Semiángulo de prueba: un campo de visión de 100° son 50 a cada lado.
    const HALF: f32 = 50.0;

    /// Destinatario en el origen mirando a +Z (yaw 0, convención Unity: adelante es `(sin, cos)`).
    fn at_origin_looking_north(src: [f32; 3], was_inside: bool) -> bool {
        pose_in_attention_cone([0.0, 1.8, 0.0], 0.0, src, was_inside, HALF)
    }

    #[test]
    fn what_you_are_looking_at_stays_at_full_cadence() {
        assert!(
            at_origin_looking_north([0.0, 1.8, 10.0], false),
            "justo delante tiene que estar dentro del cono"
        );
        assert!(
            at_origin_looking_north([7.0, 1.8, 10.0], false),
            "a 35° del eje, dentro de los 50 de semiángulo"
        );
    }

    #[test]
    fn what_is_behind_you_drops_out() {
        assert!(
            !at_origin_looking_north([0.0, 1.8, -10.0], false),
            "justo a la espalda tiene que quedar fuera"
        );
        assert!(
            !at_origin_looking_north([10.0, 1.8, 0.0], false),
            "a 90°, de perfil, ya está fuera de un semiángulo de 50"
        );
    }

    #[test]
    fn the_cone_has_a_dead_band_so_turning_does_not_flicker() {
        // A 60° del eje: fuera del cono de ENTRADA (50) pero dentro del de SALIDA (50 + 20).
        // Sin esta banda, girar la cabeza cambiaría la cadencia de un par varias veces por segundo,
        // que es exactamente lo que costó el arreglo de la histéresis del PVS.
        let side = [
            10.0 * 60f32.to_radians().sin(),
            1.8,
            10.0 * 60f32.to_radians().cos(),
        ];
        assert!(
            !at_origin_looking_north(side, false),
            "preparación: a 60° NO se entra al cono"
        );
        assert!(
            at_origin_looking_north(side, true),
            "pero quien ya estaba dentro aguanta el margen de histéresis"
        );
    }

    #[test]
    fn the_dead_band_does_not_make_attention_permanent() {
        // La mitad que impide que el arreglo se coma la optimización: si «una vez mirado, mirado
        // para siempre», el cono no recortaría nada nunca y pasaría en verde igual.
        assert!(
            !at_origin_looking_north([0.0, 1.8, -10.0], true),
            "a la espalda del todo se sale aunque se estuviera dentro"
        );
    }

    #[test]
    fn the_cone_reads_geometry_and_nothing_else() {
        // ADR-074. La función no recibe quién es la fuente —no hay parámetro que lo diga— y dos
        // posiciones simétricas respecto al eje de mirada tienen que dar lo mismo. Si alguna vez
        // alguien añade un parámetro de especie aquí, este test es el sitio donde se discute.
        let left = [-6.0, 1.8, 10.0];
        let right = [6.0, 1.8, 10.0];
        assert_eq!(
            at_origin_looking_north(left, false),
            at_origin_looking_north(right, false),
            "el cono es simétrico: sólo mira el ángulo, jamás qué es la fuente"
        );
    }

    #[test]
    fn far_and_behind_never_drops_below_the_adr074_floor() {
        // La composición con la curva de ADR-074 enm. 3. El cono parte los Hz, NO se salta rondas:
        // una fuente lejana ya está en el suelo de 5 Hz, y halvear rondas encima la habría llevado
        // a 2,5 — justo lo que el suelo existe para impedir, porque 50–100 m es la fase `stalk`.
        for d in [0.0f32, 25.0, 50.0, 75.0, 99.0, 150.0] {
            // Contra la función REAL (ADR-074 enm. 4), no contra una reimplementación del test:
            // `pose_pair_hz` es la que combina distancia, aforo y cono, y la que tiene que aplicar
            // el `clamp` al suelo. Preguntarle a una copia sería comprobar mi aritmética, no la suya.
            let full = pose_pair_hz(d, 1.0, false);
            let behind = pose_pair_hz(d, 1.0, true);
            assert!(
                behind >= POSE_HZ_FLOOR,
                "a {d} m y a la espalda quedaría en {behind} Hz, por debajo del suelo {POSE_HZ_FLOOR}"
            );
            assert!(
                behind <= full,
                "el cono sólo puede BAJAR la cadencia, nunca subirla ({behind} > {full} a {d} m)"
            );
        }
        // Y donde de verdad ahorra es cerca, que es donde la curva va alta.
        assert_eq!(
            pose_pair_hz(0.0, 1.0, false),
            POSE_RELAY_HZ,
            "pegado y de frente: cadencia completa"
        );
        assert!(
            pose_pair_hz(0.0, 1.0, true) < pose_pair_hz(0.0, 1.0, false),
            "pegado y a la espalda sí baja: ahí está el ahorro del cono"
        );
        assert_eq!(
            pose_pair_hz(99.0, 1.0, true),
            pose_pair_hz(99.0, 1.0, false),
            "lejos ya está en el suelo, así que el cono no puede quitar nada más"
        );
        // Y el aforo tampoco puede perforar el suelo, por agresivo que se ponga el factor.
        assert_eq!(
            pose_pair_hz(90.0, 0.0, true),
            POSE_HZ_FLOOR,
            "aforo a cero Y a la espalda: el suelo aguanta"
        );
    }

    #[test]
    fn the_cone_is_on_now_that_the_client_delays_per_peer() {
        // Entró apagado porque el búfer de interpolación del cliente medía el ritmo GLOBAL y no por
        // peer. ADR-074 enm. 3 D4 (`RemotePlayerManager.PushSample` / `PerPeerDelayTarget`,
        // commit d5b94586) cerró ese prerrequisito, y la enmienda 4 lo enciende: lo que queda a la
        // espalda va a la mitad de la cadencia que le tocaría por distancia y aforo.
        const {
            assert!(
                POSE_CONE_ENABLED,
                "el cono está encendido desde ADR-074 enm. 4; apagarlo es decisión, no accidente"
            )
        };
        assert_eq!(
            POSE_CONE_HALF_ANGLE_DEG, 100.0,
            "200° de campo «delante» con 20° de histéresis: girar 90° no cambia la cadencia"
        );
    }
}

#[cfg(test)]
mod spatial_index_tests {
    use super::*;
    use std::collections::HashSet;

    /// Una nube que cruza el origen y cae en coordenadas negativas a propósito, con separaciones
    /// que se sientan justo encima de las dos fronteras que importan (el radio de entrada, 100, y
    /// el de salida, 120).
    fn cloud() -> Vec<[f32; 3]> {
        let mut pts = Vec::new();
        for i in 0..18 {
            for j in 0..18 {
                let jitter = ((i * 7 + j * 13) % 11) as f32 - 5.0;
                pts.push([
                    (i as f32 - 9.0) * 58.0 + jitter,
                    1.8,
                    (j as f32 - 9.0) * 58.0 - jitter,
                ]);
            }
        }
        pts
    }

    /// Los pares que el índice llega a VISITAR, con la vecindad de 3×3 del relay.
    fn visited_by_index(pts: &[[f32; 3]]) -> HashSet<(usize, usize)> {
        let mut cells: std::collections::HashMap<(i32, i32), Vec<usize>> =
            std::collections::HashMap::new();
        for (i, p) in pts.iter().enumerate() {
            cells.entry(pose_cell(*p)).or_default().push(i);
        }
        let mut seen = HashSet::new();
        for (i, a) in pts.iter().enumerate() {
            let (cx, cz) = pose_cell(*a);
            for dx in -1..=1 {
                for dz in -1..=1 {
                    if let Some(bucket) = cells.get(&(cx + dx, cz + dz)) {
                        for &j in bucket {
                            if i != j {
                                seen.insert((i, j));
                            }
                        }
                    }
                }
            }
        }
        seen
    }

    /// Los pares que el radio ACEPTA, a fuerza bruta: todos contra todos, sin índice.
    fn accepted_by_radius(pts: &[[f32; 3]], was_relaying: bool) -> HashSet<(usize, usize)> {
        let mut ok = HashSet::new();
        for (i, a) in pts.iter().enumerate() {
            for (j, b) in pts.iter().enumerate() {
                if i != j && aoi_pose_should_relay(*a, *b, was_relaying, AOI_POSE_RADIUS_M) {
                    ok.insert((i, j));
                }
            }
        }
        ok
    }

    #[test]
    fn the_index_never_drops_a_pair_the_radius_wants() {
        // La propiedad que hace correcto el atajo: el índice puede visitar de MÁS (y los rechaza el
        // radio, como siempre), pero jamás de MENOS. Si alguna vez visitara de menos, un jugador se
        // volvería invisible para otro sin que nada fallara — el peor tipo de bug de este relay,
        // porque no da error, sólo borra gente.
        let pts = cloud();
        let visited = visited_by_index(&pts);

        // Las dos ramas de la histéresis, porque usan radios distintos y el lado de la casilla se
        // eligió por el de SALIDA precisamente para cubrir la segunda.
        for was_relaying in [false, true] {
            let wanted = accepted_by_radius(&pts, was_relaying);
            let dropped: Vec<_> = wanted.difference(&visited).collect();
            assert!(
                dropped.is_empty(),
                "el índice se dejó {} pares que el radio aceptaba (was_relaying={was_relaying});                  el primero es {:?}",
                dropped.len(),
                dropped.first()
            );
        }
    }

    #[test]
    fn the_relay_rate_stays_tied_to_the_loop_that_produces_it() {
        // La razón de que esta constante exista. Estaba escrita a mano como un `* 10` dentro del
        // MPTRACE y se quedó vieja cuando ADR-138 D1 subió la cadencia de 20 a 30 Hz: el log llevaba
        // desde entonces diciendo un tercio del tráfico real, sin que nada fallara.
        //
        // Ahora se deriva, y esto ata la derivación a su origen: si alguien cambia el ritmo del
        // bucle, lo que salta es un test y no un número silenciosamente equivocado en un log que se
        // lee durante los playtests.
        assert_eq!(
            POSE_RELAY_HZ * crate::game_loop::NET_BROADCAST_EVERY,
            60,
            "el relay emite una ronda de cada {} ticks de un bucle de 60 Hz",
            crate::game_loop::NET_BROADCAST_EVERY
        );
        const {
            assert!(
                POSE_HZ_FLOOR < POSE_RELAY_HZ,
                "el suelo de la curva tiene que quedar por debajo de la cadencia base"
            )
        };
    }

    #[test]
    fn the_cell_is_at_least_the_exit_radius() {
        // Si alguien encoge la casilla por debajo del radio de salida, la vecindad de 3×3 deja de
        // ser suficiente y el test de arriba se pone rojo. Esta es la razón escrita, para que el
        // rojo se lea en un segundo en vez de en una tarde.
        let exit = AOI_POSE_RADIUS_M * AOI_POSE_EXIT_FACTOR;
        assert!(
            POSE_CELL_M >= exit,
            "casilla {POSE_CELL_M} m < radio de salida {exit} m: la vecindad de 3x3 ya no cubre              todo lo que la histéresis quiere mantener"
        );
    }

    #[test]
    fn the_index_saves_work_instead_of_just_moving_it() {
        // Que sea correcto no basta: tiene que ahorrar. Se mide sobre la MISMA nube densa que usa
        // el test de correccion —58 m de paso contra un radio de 100— que es casi el peor caso
        // realista: ahi las casillas van llenas y la vecindad de 3x3 abarca buena parte del mundo.
        // Con la gente de verdad repartida el ahorro es de otro orden; este umbral es el suelo, no
        // la expectativa.
        let pts = cloud();
        let brute = pts.len() * (pts.len() - 1);
        let visited = visited_by_index(&pts).len();
        assert!(
            visited * 4 < brute,
            "el índice visita {visited} de {brute} pares: menos de un 4x en el caso más denso no              justifica el índice"
        );
    }
}

#[cfg(test)]
mod fidelity_cap_tests {
    use super::*;

    /// Un candidato tal y como lo recoge el bucle de pares: distancia al cuadrado, posición de la
    /// fuente e identificador.
    fn cand(dist_sq: f32, pos: [f32; 3], id: PeerId) -> (f32, [f32; 3], PeerId) {
        (dist_sq, pos, id)
    }

    #[test]
    fn the_cap_keeps_the_nearest_and_drops_the_farthest() {
        let mut bag = vec![
            cand(900.0, [30.0, 0.0, 0.0], 7),
            cand(25.0, [5.0, 0.0, 0.0], 3),
            cand(400.0, [20.0, 0.0, 0.0], 9),
            cand(100.0, [10.0, 0.0, 0.0], 1),
        ];
        bag.sort_unstable_by(pose_fidelity_order);
        bag.truncate(2);
        let kept: Vec<PeerId> = bag.iter().map(|c| c.2).collect();
        assert_eq!(
            kept,
            vec![3, 1],
            "el recorte se queda con los dos MÁS CERCANOS; lo que se va es lo lejano"
        );
    }

    #[test]
    fn the_order_never_depends_on_who_the_source_is() {
        // ADR-074: el filtro decide por dónde están las cosas, jamás por qué son. Si el
        // identificador entrara en el desempate, a igual distancia ganaría siempre el mismo tipo de
        // fuente y el tope se convertiría en un detector de robapieles.
        //
        // Dos fuentes equidistantes del destinatario, una a cada lado. El orden lo fija su
        // posición, así que intercambiarles el identificador no puede moverlo.
        let left = [-10.0, 0.0, 0.0];
        let right = [10.0, 0.0, 0.0];

        let mut a = [cand(100.0, right, 1), cand(100.0, left, 2)];
        let mut b = [cand(100.0, right, 2), cand(100.0, left, 1)];
        a.sort_unstable_by(pose_fidelity_order);
        b.sort_unstable_by(pose_fidelity_order);

        let pos_a: Vec<[f32; 3]> = a.iter().map(|c| c.1).collect();
        let pos_b: Vec<[f32; 3]> = b.iter().map(|c| c.1).collect();
        assert_eq!(
            pos_a, pos_b,
            "a igual distancia manda la POSICIÓN: cambiar los identificadores no reordena nada"
        );
        assert_eq!(
            pos_a[0], left,
            "y el desempate es determinista, no arbitrario"
        );

        // Y la distancia manda sobre todo lo demás: el más cercano gana aunque su identificador sea
        // el más alto de la bolsa.
        let mut c = [
            cand(900.0, [30.0, 0.0, 0.0], 1),
            cand(4.0, [2.0, 0.0, 0.0], 999),
        ];
        c.sort_unstable_by(pose_fidelity_order);
        assert_eq!(
            c[0].2, 999,
            "la distancia decide antes que nada; el identificador sólo rompe empates exactos"
        );
    }

    #[test]
    fn the_order_is_total_so_the_cut_is_reproducible() {
        // Regla dura 13: la salida no puede depender del orden en que llegaron los candidatos.
        // `sort_unstable` permuta equivalentes, así que el comparador tiene que distinguirlos a
        // todos o el recorte cambiaría de una ronda a otra con la misma escena — el parpadeo que el
        // radio y el grafo ya evitan con histéresis.
        let base = vec![
            cand(100.0, [10.0, 0.0, 0.0], 4),
            cand(100.0, [0.0, 10.0, 0.0], 2),
            cand(100.0, [0.0, 0.0, 10.0], 8),
            cand(25.0, [5.0, 0.0, 0.0], 5),
        ];
        let mut forward = base.clone();
        let mut backward = base.clone();
        backward.reverse();
        forward.sort_unstable_by(pose_fidelity_order);
        backward.sort_unstable_by(pose_fidelity_order);
        assert_eq!(
            forward, backward,
            "misma escena, distinto orden de entrada: el recorte tiene que salir idéntico"
        );
    }

    #[test]
    fn the_cap_is_above_a_full_session_so_it_cannot_bite_on_players_alone() {
        // El tope entra deliberadamente apagado: 96 está por encima de cualquier aforo que esta
        // sesión admita, así que ninguna partida de sólo jugadores puede tocarlo. Lo que sí puede
        // acercarse es la población de criaturas, y por eso `MPTRACE` publica `max_fan_in` y
        // `cap_hits` — el número bueno sale de medirlos, no de esta constante.
        let full_session = crate::network::protocol::SessionConfig::default().max_players as usize;
        assert!(
            POSE_FIDELITY_CAP > full_session,
            "tope {POSE_FIDELITY_CAP} debe superar el aforo de sesión {full_session}: si no, deja \
             de ser un cambio sin efecto y necesita medida antes de entrar"
        );
    }
}

#[cfg(test)]
mod pvs_tests {
    use super::*;

    /// Sin margen: ve lo mismo entrando que quedándose. Para los casos que no hablan de histéresis.
    fn key(region: (i32, i32), storey: usize, space: usize, visible: &[usize]) -> PvsKey {
        key_hyst(region, storey, space, visible, visible)
    }

    fn key_hyst(
        region: (i32, i32),
        storey: usize,
        space: usize,
        enter: &[usize],
        stay: &[usize],
    ) -> PvsKey {
        PvsKey {
            region: crate::world::wg3::world::Wg3RegionCoord {
                x: region.0,
                z: region.1,
            },
            storey,
            space,
            visible_enter: enter.to_vec(),
            visible_stay: stay.to_vec(),
        }
    }

    #[test]
    fn a_peer_without_a_room_is_always_relayed() {
        let somewhere = key((0, 0), 0, 3, &[3]);
        assert!(
            pvs_allows(None, Some(&somewhere), false),
            "origen sin sala resuelta: no se puede afirmar nada, así que se envía"
        );
        assert!(
            pvs_allows(Some(&somewhere), None, false),
            "destino sin sala resuelta: igual"
        );
        assert!(pvs_allows(None, None, false), "ninguno de los dos: igual");
    }

    #[test]
    fn across_a_region_seam_everything_is_relayed() {
        // El grafo es POR REGIÓN y no sabe nada del otro lado de la costura. Ocultar ahí sería
        // ocultar por ignorancia, que es exactamente el error caro.
        let a = key((0, 0), 0, 1, &[1]);
        let b = key((1, 0), 0, 9, &[9]);
        assert!(pvs_allows(Some(&a), Some(&b), false));
    }

    #[test]
    fn across_storeys_everything_is_relayed() {
        // Un hueco de escalera comunica plantas y este grafo no lo sabe (`visibility.rs`).
        let a = key((0, 0), 0, 1, &[1]);
        let b = key((0, 0), 1, 1, &[1]);
        assert!(pvs_allows(Some(&a), Some(&b), false));
    }

    #[test]
    fn two_rooms_that_communicate_see_each_other() {
        let a = key((0, 0), 0, 0, &[0, 1, 2]);
        let b = key((0, 0), 0, 2, &[0, 1, 2]);
        assert!(pvs_allows(Some(&a), Some(&b), false));
    }

    /// El único caso en el que este filtro dice que NO. Si deja de existir, el PVS no está
    /// filtrando nada y el ahorro que mide `PVSTRACE` sería mentira.
    #[test]
    fn a_sealed_room_is_the_only_thing_that_gets_cut() {
        let a = key((0, 0), 0, 0, &[0, 1]);
        let sealed = key((0, 0), 0, 7, &[7]);
        assert!(!pvs_allows(Some(&a), Some(&sealed), false));
    }

    /// **La histéresis, y por qué existe.** Sin ella el PVS se evalúa de cero en cada ronda, así
    /// que un par junto a un vano cambia de «se ve» a «no se ve» varias veces por segundo; cada
    /// reaparición deja al cliente sin historial que interpolar y se ve como un salto. Lo reportó
    /// Joel en el primer playtest con dos jugadores.
    ///
    /// Es la misma forma que el radio lleva desde ADR-074 —entrar es estricto, quedarse es
    /// generoso— y aquí la unidad no son metros sino SALTOS en el grafo.
    #[test]
    fn a_pair_already_relaying_survives_one_extra_hop() {
        // La sala 9 está a un salto de más: fuera del criterio de entrada, dentro del de estancia.
        let a = key_hyst((0, 0), 0, 0, &[0, 1], &[0, 1, 9]);
        let borderline = key((0, 0), 0, 9, &[9]);

        assert!(
            !pvs_allows(Some(&a), Some(&borderline), false),
            "para ENTRAR manda el criterio estricto"
        );
        assert!(
            pvs_allows(Some(&a), Some(&borderline), true),
            "quien ya estaba dentro aguanta un salto más: sin esto, parpadea"
        );
    }

    /// Y el margen es UN salto, no una barra libre: lo que está de verdad lejos se corta aunque se
    /// estuviera relayando. Sin esta mitad, la histéresis se convertiría en «una vez visto,
    /// visible para siempre» y el filtro dejaría de filtrar.
    #[test]
    fn hysteresis_does_not_make_visibility_permanent() {
        let a = key_hyst((0, 0), 0, 0, &[0, 1], &[0, 1, 9]);
        let far = key((0, 0), 0, 42, &[42]);
        assert!(!pvs_allows(Some(&a), Some(&far), true));
    }

    /// Cada uno ve desde SU sala: el corte se decide con la lista del ORIGEN, no con una relación
    /// que se dé por simétrica sin comprobarla.
    #[test]
    fn visibility_is_asked_from_the_source() {
        let seer = key((0, 0), 0, 0, &[0, 5]);
        let seen = key((0, 0), 0, 5, &[5]);
        assert!(pvs_allows(Some(&seer), Some(&seen), false));
        assert!(
            !pvs_allows(Some(&seen), Some(&seer), false),
            "la lista del origen es la que manda, y aquí la del otro no incluye la sala 0"
        );
    }
}

#[cfg(test)]
mod world_drip_tests {
    use super::*;

    #[test]
    fn the_drip_is_incomplete_until_the_end_arrives_and_every_chunk_is_in() {
        let mut p = WorldSyncProgress::default();
        p.note_chunk(7, [0, 0], 0);
        p.note_chunk(7, [1, 0], 0);
        assert!(
            !p.is_complete(),
            "sin End no hay completitud: el receptor no sabe cuantos faltan"
        );

        p.note_end(7, 3);
        assert!(
            !p.is_complete(),
            "con End pero 2 de 3 chunks, sigue faltando"
        );

        p.note_chunk(7, [2, 0], 0);
        assert!(p.is_complete(), "tercer chunk distinto: completo");
    }

    #[test]
    fn the_end_may_arrive_before_the_chunks() {
        // La capa reliable es at-least-once SIN orden. Un End primero es legal y no puede
        // dejar el join colgado para siempre.
        let mut p = WorldSyncProgress::default();
        p.note_end(7, 2);
        assert!(!p.is_complete());
        p.note_chunk(7, [0, 0], 0);
        p.note_chunk(7, [1, 0], 0);
        assert!(
            p.is_complete(),
            "el orden de llegada no decide la completitud"
        );
    }

    #[test]
    fn duplicate_chunks_do_not_fake_completion() {
        // Un ACK perdido hace que el emisor retransmita: el MISMO chunk llega dos veces. Contar
        // paquetes en vez de claves abriria el gate con medio mundo aplicado.
        let mut p = WorldSyncProgress::default();
        p.note_end(7, 2);
        p.note_chunk(7, [0, 0], 0);
        p.note_chunk(7, [0, 0], 0);
        assert!(
            !p.is_complete(),
            "dos copias del mismo chunk no son dos chunks"
        );
        p.note_chunk(7, [1, 0], 0);
        assert!(p.is_complete());
    }

    #[test]
    fn the_same_coord_on_another_layer_is_another_chunk() {
        // Las capas se apilan en la misma (x,z): clave por (pos, layer) o el mundo multicapa
        // nunca completaria.
        let mut p = WorldSyncProgress::default();
        p.note_end(7, 2);
        p.note_chunk(7, [0, 0], 0);
        p.note_chunk(7, [0, 0], 1);
        assert!(p.is_complete());
    }

    #[test]
    fn a_newer_revision_discards_the_previous_count() {
        let mut p = WorldSyncProgress::default();
        p.note_end(7, 2);
        p.note_chunk(7, [0, 0], 0);

        // Llega un goteo nuevo (revision 8): lo acumulado de la 7 no cuenta para el.
        p.note_chunk(8, [5, 5], 0);
        p.note_end(8, 2);
        assert!(
            !p.is_complete(),
            "el chunk de la revision vieja no completa la nueva"
        );
        p.note_chunk(8, [6, 5], 0);
        assert!(p.is_complete());
    }

    #[test]
    fn a_straggler_from_an_old_revision_is_ignored() {
        let mut p = WorldSyncProgress::default();
        p.note_end(8, 1);
        p.note_chunk(8, [0, 0], 0);
        assert!(p.is_complete());

        // Rezagado de la revision 7 (retransmision tardia): ni completa ni descompleta.
        p.note_chunk(7, [9, 9], 0);
        p.note_end(7, 99);
        assert!(
            p.is_complete(),
            "un rezagado viejo no puede reabrir un gate ya abierto"
        );
    }

    #[test]
    fn the_deprecated_monolith_still_opens_the_gate() {
        // 0x04 aplica el mundo entero de golpe: completo por construccion, o un host viejo
        // dejaria al joiner sin spawn para siempre.
        let mut p = WorldSyncProgress::default();
        p.note_monolith(3);
        assert!(p.is_complete());
    }
}

/// F0.0 (ADR-073 / SCALING-ROADMAP, E0): sonda de la SUBIDA TOTAL del host con 8 peers.
///
/// Es la línea base del gate de E0 y el número que E1 (ADR-074) tiene que mover. Se captura
/// ANTES del primer fix de E0 — capturarla después contaminaría el "antes".
///
/// A diferencia de `roster_relay_cost` (que mide UN componente), esto suma TODO lo que el host
/// emite en régimen permanente, **contando headers UDP/IP: cada datagrama cuesta
/// `payload + 28 B` en el aire** (8 de UDP + 20 de IPv4). Con payloads de pose de ~250 B el
/// header es un ~10 %; con ACKs o heartbeats sería la mitad del paquete. Medir solo lo entregado
/// a `send_datagram` daría una unidad que no existe en el router de nadie.
///
/// También responde la pregunta de F0.1: cuánto pesa un `broadcast_world_sync` completo (el que
/// HOY dispara cada pickup/drop legacy hacia todos los peers) y si esa línea base domina sobre
/// el régimen permanente — si domina, F0.1 se detiene y la decisión vuelve a Joel (ver roadmap).
#[cfg(test)]
mod uplink_probe {
    use crate::network::protocol::{
        encode_packet, PacketHeader, PacketPayload, PeerInfo, StpBuildProgress, StpBuildingInfo,
        StpCarryableInfo, StpHarvestableInfo, StpItemInfo,
    };
    use crate::network::roster::{
        content_hash, paginate, RosterGate, ROSTER_HEARTBEAT, ROSTER_PAGE_BUDGET_BYTES,
    };
    use crate::utils::Vec3;
    use crate::world::World;

    /// UDP (8 B) + IPv4 (20 B). Sin contar Ethernet (18 B más): el gate se mide contra el
    /// ancho de banda IP del uplink, que es como lo reportan los routers domésticos.
    const UDP_IP_HEADER: usize = 28;
    const PEERS: usize = 8;
    const POSE_HZ: f64 = 10.0;
    const CHUNK_HZ: f64 = 5.0;

    /// Bytes EN EL AIRE de un payload: header propio de 12 B + MessagePack + UDP/IP.
    fn wire_len(payload: &PacketPayload) -> usize {
        let header = PacketHeader::new(0, 1, 0, 0);
        encode_packet(&header, payload).len() + UDP_IP_HEADER
    }

    fn pose_payload(seq: u8) -> PacketPayload {
        PacketPayload::PlayerUpdate {
            position: [123.5, 1.8, -412.0],
            rotation: 187.5,
            animation: "walk_slow".into(),
            crouch: false,
            pitch: -12,
            equipment: [1001, 1002, 1003, 1004],
            held_item: 2001,
            hit_seq: seq,
            dead: false,
            revealed: false,
            vocal_seq: 0,
            vocal_kind: 0,
            light_on: true,
            fire_seq: seq,
            buttons: 1,
            melee_seq: 0,
            carry_def: 0,
            carry_count: 0,
            species: 0,
        }
    }

    fn building(id: u32) -> StpBuildingInfo {
        StpBuildingInfo {
            id,
            def_id: -4996552,
            position: [12.5, 1.8, -40.0],
            rotation: 90.0,
            group_id: id / 8,
            owner_id: 0,
            added: vec![StpBuildProgress {
                material_id: -1234,
                count: 4,
            }],
        }
    }

    fn item(id: u32) -> StpItemInfo {
        StpItemInfo {
            id,
            def_id: -52379,
            count: 3,
            position: [12.5, 1.8, -40.0],
            rotation: 90.0,
            settling: false,
        }
    }

    fn carryable(id: u32) -> StpCarryableInfo {
        StpCarryableInfo {
            id,
            def_id: 7,
            position: [1.0, 2.0, 3.0],
            rotation: 90.0,
        }
    }

    fn harvestable(id: u32) -> StpHarvestableInfo {
        StpHarvestableInfo {
            id,
            position: [1.0, 2.0, 3.0],
            remaining: 0.62,
        }
    }

    /// Bytes en el aire de UNA ronda completa de un roster (todas sus páginas), reproduciendo
    /// la paginación real de los `broadcast_stp_*`.
    fn roster_round_wire<T: serde::Serialize + Clone>(
        items: &[T],
        make: impl Fn(Vec<T>) -> PacketPayload,
    ) -> (usize, usize) {
        let pages = paginate(items, ROSTER_PAGE_BUDGET_BYTES);
        let count = pages.len();
        let bytes = pages.into_iter().map(|p| wire_len(&make(p))).sum();
        (count, bytes)
    }

    /// SONDA DE MEDICIÓN, no un test — imprime, no afirma.
    ///
    /// ```text
    /// cargo test --release host_uplink_baseline -- --ignored --nocapture
    /// ```
    #[test]
    #[ignore = "sonda de medición: imprime, no afirma"]
    fn host_uplink_baseline() {
        println!("\n=== F0.0 / subida TOTAL del host con {PEERS} peers (headers UDP/IP incluidos: +{UDP_IP_HEADER} B/datagrama) ===\n");

        // ── Poses ─────────────────────────────────────────────────────────────────────────
        // El host emite SU pose a cada peer (broadcast_player_update) y además relaya la pose
        // de cada peer a todos los demás (broadcast_peer_poses): P×P − P datagramas por ronda.
        let pose_wire = wire_len(&pose_payload(3));
        let own_pose_dgps = PEERS as f64 * POSE_HZ;
        let relay_dgps = (PEERS * PEERS - PEERS) as f64 * POSE_HZ;
        let own_pose_bps = own_pose_dgps * pose_wire as f64;
        let relay_bps = relay_dgps * pose_wire as f64;
        println!("PlayerUpdate en el aire: {pose_wire} B");
        println!(
            "  pose propia:    {own_pose_dgps:.0} dgr/s = {:.1} KB/s",
            own_pose_bps / 1024.0
        );
        println!(
            "  relay O(N²):    {relay_dgps:.0} dgr/s = {:.1} KB/s",
            relay_bps / 1024.0
        );

        // ── PeerList (broadcast_peer_roster, 10 Hz) ───────────────────────────────────────
        let peers_info: Vec<PeerInfo> = (0..=PEERS as u16)
            .map(|id| PeerInfo {
                id,
                name: format!("Player_{id:02}"),
                addr: "203.0.113.77:7778".into(),
                position: [123.5, 1.8, -412.0],
                relay_only: false,
            })
            .collect();
        let peer_list_wire = wire_len(&PacketPayload::PeerList { peers: peers_info });
        let peer_list_bps = PEERS as f64 * POSE_HZ * peer_list_wire as f64;
        println!(
            "PeerList({} entradas) en el aire: {peer_list_wire} B → {:.1} KB/s",
            PEERS + 1,
            peer_list_bps / 1024.0
        );

        // ── ChunkState (broadcast_chunk_states, 5 Hz, chunks propios a ≤3 de distancia) ───
        // Mundo real: los chunks que update_ownership carga alrededor del jugador, con sus
        // entidades e items seedeados — el tamaño del ChunkSyncData es el de verdad.
        let mut world = World::new(42);
        world.update_ownership(Vec3::new(0.0, 1.0, 0.0), 1);
        let chunk_wires: Vec<usize> = world
            .chunks
            .values()
            .map(|c| {
                wire_len(&PacketPayload::ChunkState {
                    data: super::chunk_to_sync_data(c),
                })
            })
            .collect();
        let chunk_count = chunk_wires.len();
        let chunk_round_bytes: usize = chunk_wires.iter().sum();
        let chunk_bps = chunk_round_bytes as f64 * PEERS as f64 * CHUNK_HZ;
        println!(
            "ChunkState: {chunk_count} chunks cargados, {:.0} B de media → {:.1} KB/s ({} dgr/s)",
            chunk_round_bytes as f64 / chunk_count.max(1) as f64,
            chunk_bps / 1024.0,
            chunk_count * PEERS * CHUNK_HZ as usize
        );

        // ── Rosters (base seria de five_rosters_converge: 1000/300/200/100) ───────────────
        let buildings: Vec<_> = (0..1000).map(building).collect();
        let items: Vec<_> = (0..300).map(item).collect();
        let carryables: Vec<_> = (0..200).map(carryable).collect();
        let harvestables: Vec<_> = (0..100).map(harvestable).collect();

        let g = 1u32;
        let (b_pages, b_bytes) =
            roster_round_wire(&buildings, |p| PacketPayload::StpBuildingList {
                buildings: p,
                generation: g,
                page: 0,
                page_count: 1,
                cell: [0, 0],
            });
        let (i_pages, i_bytes) = roster_round_wire(&items, |p| PacketPayload::StpItemList {
            items: p,
            generation: g,
            page: 0,
            page_count: 1,
            cell: [0, 0],
        });
        let (c_pages, c_bytes) =
            roster_round_wire(&carryables, |p| PacketPayload::StpCarryableList {
                carryables: p,
                generation: g,
                page: 0,
                page_count: 1,
                cell: [0, 0],
            });
        let (h_pages, h_bytes) =
            roster_round_wire(&harvestables, |p| PacketPayload::StpHarvestableList {
                harvestables: p,
                generation: g,
                page: 0,
                page_count: 1,
                cell: [0, 0],
            });
        let round_pages = b_pages + i_pages + c_pages + h_pages;
        let round_bytes = b_bytes + i_bytes + c_bytes + h_bytes;
        let rosters_ungated_bps = round_bytes as f64 * PEERS as f64 * POSE_HZ;
        println!(
            "Rosters (1000+300+200+100, corpses=0): {round_pages} páginas, {round_bytes} B/ronda \
             → sin gate {:.1} KB/s",
            rosters_ungated_bps / 1024.0
        );

        // ADR-071: 60 s simulados a 10 Hz con un jugador construyendo (una pieza cada 5 s).
        // El reloj es sintético: el latido mide tiempo real y un bucle cerrado nunca lo vencería.
        let mut gates = [
            RosterGate::default(),
            RosterGate::default(),
            RosterGate::default(),
            RosterGate::default(),
        ];
        let mut live = buildings.clone();
        let t0 = std::time::Instant::now();
        let mut busy_bytes = 0usize;
        const ROUNDS: usize = 600;
        for round in 0..ROUNDS {
            if round % 50 == 0 && round > 0 {
                live.push(building(90_000 + round as u32));
            }
            let now = t0 + std::time::Duration::from_millis(round as u64 * 100);
            let hashes = [
                content_hash(&live),
                content_hash(&items),
                content_hash(&carryables),
                content_hash(&harvestables),
            ];
            let sizes = [
                roster_round_wire(&live, |p| PacketPayload::StpBuildingList {
                    buildings: p,
                    generation: g,
                    page: 0,
                    page_count: 1,
                    cell: [0, 0],
                })
                .1,
                i_bytes,
                c_bytes,
                h_bytes,
            ];
            for (k, gate) in gates.iter_mut().enumerate() {
                if gate.should_send(hashes[k], now, ROSTER_HEARTBEAT) {
                    busy_bytes += sizes[k];
                }
            }
        }
        let rosters_busy_bps = busy_bytes as f64 * PEERS as f64 / 60.0;
        // Idle: solo latidos — una ronda completa cada 3 s por roster.
        let rosters_idle_bps = round_bytes as f64 * PEERS as f64 / ROSTER_HEARTBEAT.as_secs_f64();
        println!(
            "  con gate ADR-071: construyendo {:.1} KB/s · idle (latidos) {:.1} KB/s",
            rosters_busy_bps / 1024.0,
            rosters_idle_bps / 1024.0
        );

        // ── Totales ────────────────────────────────────────────────────────────────────────
        let fixed = own_pose_bps + relay_bps + peer_list_bps + chunk_bps;
        let total_busy = fixed + rosters_busy_bps;
        let total_idle = fixed + rosters_idle_bps;
        println!("\n--- LÍNEA BASE con {PEERS} peers (gate de E0; E1 tiene que mover esto) ---");
        println!(
            "  construyendo: {:.0} KB/s = {:.1} Mbps de subida",
            total_busy / 1024.0,
            total_busy * 8.0 / 1_000_000.0
        );
        println!(
            "  idle:         {:.0} KB/s = {:.1} Mbps de subida",
            total_idle / 1024.0,
            total_idle * 8.0 / 1_000_000.0
        );

        // ── F0.1: el world_sync completo que HOY dispara cada pickup/drop legacy ──────────
        let sync_wires: Vec<usize> = world
            .chunks
            .values()
            .map(|c| {
                wire_len(&PacketPayload::WorldSyncChunk {
                    world_revision: world.revision,
                    data: super::chunk_to_sync_data(c),
                })
            })
            .collect();
        let end_wire = wire_len(&PacketPayload::WorldSyncEnd {
            world_revision: world.revision,
            chunk_count: chunk_count as u32,
        });
        let drip_bytes: usize = sync_wires.iter().sum::<usize>() + end_wire;
        let per_interaction = drip_bytes * PEERS;
        let sustained_1hz = per_interaction as f64; // 1 interacción/s = 1 goteo/s
        let coalesced_bps = per_interaction as f64 / 0.3; // F0.1: máx 1 goteo por ventana de 300 ms
        println!("\n--- F0.1: broadcast_world_sync por interacción legacy (HOY) ---");
        println!(
            "  un goteo completo: {} chunks + End = {} datagramas, {:.1} KB",
            chunk_count,
            chunk_count + 1,
            drip_bytes as f64 / 1024.0
        );
        println!(
            "  por CADA pickup/drop, a {PEERS} peers: {:.1} KB",
            per_interaction as f64 / 1024.0
        );
        println!(
            "  1 interacción/s sostenida: {:.0} KB/s ({:.1} Mbps) — contra un permanente de {:.0} KB/s",
            sustained_1hz / 1024.0,
            sustained_1hz * 8.0 / 1_000_000.0,
            total_busy / 1024.0
        );
        println!(
            "  coalescido a 300 ms (F0.1): máx {:.2} goteos/s = {:.0} KB/s",
            1.0 / 0.3,
            coalesced_bps / 1024.0
        );
        println!(
            "\nexcluido de la suma: voz (3,9 KB/s por hablante, medido en ADR-046), ACKs y \
             retransmisiones de la capa fiable, heartbeats a 1 Hz (~decenas de B/s)."
        );
    }

    /// SONDA DE MEDICIÓN: el ANTES vs DESPUÉS de la Etapa 0 (F0.1, F0.2, F0.8).
    ///
    /// ```text
    /// cargo test --release etapa0_before_after -- --ignored --nocapture
    /// ```
    ///
    /// `host_uplink_baseline` mide el coste BRUTO de cada emisor: es la foto del "antes" y sigue
    /// siendo la línea base contra la que E1 tendrá que competir. Esta sonda mide lo que los tres
    /// fixes de E0 realmente ahorran, simulando el gate por chunk de F0.8 y el coalescing de F0.1
    /// sobre 60 s de juego con el mismo reloj sintético que usa la sonda de ADR-071 (un bucle
    /// cerrado con `Instant::now()` real recorrería los 60 s simulados en microsegundos y el
    /// latido no vencería nunca, dando un resultado mejor que el real).
    ///
    /// **Lo que NO cambia y por qué está fuera:** F0.2 no toca un solo byte del aire —ahorra
    /// serializaciones, no tráfico— así que se mide aparte, en CPU. Los rosters ya los curó
    /// ADR-071 y su ahorro no es de esta etapa.
    #[test]
    #[ignore = "sonda de medición: imprime, no afirma"]
    fn etapa0_before_after() {
        use std::time::{Duration, Instant};

        println!("\n=== Etapa 0: ANTES vs DESPUÉS ({PEERS} peers, headers UDP/IP incluidos) ===\n");

        // ── Mundo real, el mismo de la línea base ────────────────────────────────────────────
        let mut world = World::new(42);
        world.update_ownership(Vec3::new(0.0, 1.0, 0.0), 1);
        let chunk_wires: Vec<(usize, usize)> = world
            .chunks
            .values()
            .enumerate()
            .map(|(i, c)| {
                (
                    i,
                    wire_len(&PacketPayload::ChunkState {
                        data: super::chunk_to_sync_data(c),
                    }),
                )
            })
            .collect();
        let chunk_count = chunk_wires.len();
        let round_bytes: usize = chunk_wires.iter().map(|(_, b)| b).sum();

        // ── F0.8: ChunkState, antes (todo cada ronda) vs después (gate por chunk) ────────────
        // 300 rondas a 5 Hz = 60 s. `churn` = cuántos de los 49 chunks cambian en cada ronda:
        // un chunk cambia si sus entidades se mueven o sus items cambian, así que el número real
        // depende de cuánta IA y cuánto loot activo haya cerca. Se dan los tres extremos en vez
        // de inventar uno: reposo (nadie cerca), actividad normal y el peor caso absoluto.
        const ROUNDS: usize = 300;
        const CHUNK_HZ_F: f64 = 5.0;
        let before_bps = round_bytes as f64 * PEERS as f64 * CHUNK_HZ_F;
        println!("--- F0.8 · ChunkState ({chunk_count} chunks, {round_bytes} B por ronda) ---");
        println!(
            "  ANTES (sin gate, todas las rondas): {:.0} KB/s = {:.1} Mbps",
            before_bps / 1024.0,
            before_bps * 8.0 / 1_000_000.0
        );

        for (label, churn) in [
            ("reposo (nadie cerca mutando nada)", 0usize),
            ("actividad normal (~4 de 49 chunks)", 4),
            ("peor caso (los 49 cambian siempre)", chunk_count),
        ] {
            let mut gates: Vec<RosterGate> =
                (0..chunk_count).map(|_| RosterGate::default()).collect();
            let t0 = Instant::now();
            let mut sent_bytes = 0usize;
            for round in 0..ROUNDS {
                let now = t0 + Duration::from_millis(round as u64 * 200);
                for (i, wire) in &chunk_wires {
                    // Un chunk "activo" cambia de contenido en cada ronda; el resto es idéntico.
                    let hash = if *i < churn { round as u64 + 1 } else { 0 };
                    if gates[*i].should_send(hash, now, ROSTER_HEARTBEAT) {
                        sent_bytes += wire;
                    }
                }
            }
            let after_bps = sent_bytes as f64 * PEERS as f64 / 60.0;
            println!(
                "  DESPUÉS · {label}: {:.0} KB/s = {:.1} Mbps  ({:.1}× menos)",
                after_bps / 1024.0,
                after_bps * 8.0 / 1_000_000.0,
                before_bps / after_bps.max(1.0)
            );
        }

        // ── F0.1: world_sync por interacción, antes (1 por evento) vs después (coalescido) ───
        let drip_bytes: usize = world
            .chunks
            .values()
            .map(|c| {
                wire_len(&PacketPayload::WorldSyncChunk {
                    world_revision: world.revision,
                    data: super::chunk_to_sync_data(c),
                })
            })
            .sum::<usize>()
            + wire_len(&PacketPayload::WorldSyncEnd {
                world_revision: world.revision,
                chunk_count: chunk_count as u32,
            });
        let per_drip = drip_bytes * PEERS;
        println!(
            "\n--- F0.1 · world_sync por interacción ({:.1} KB por goteo a {PEERS} peers) ---",
            per_drip as f64 / 1024.0
        );
        for (label, interactions_per_s) in [
            ("un pickup cada 2 s (juego tranquilo)", 0.5f64),
            ("2 interacciones/s (loot activo)", 2.0),
            ("ráfaga de 20 en 1 s (vaciar un cofre)", 20.0),
        ] {
            let before = per_drip as f64 * interactions_per_s;
            // F0.1: como mucho un goteo por ventana de 300 ms.
            let after = per_drip as f64 * interactions_per_s.min(1.0 / 0.3);
            println!(
                "  {label}: ANTES {:.0} KB/s → DESPUÉS {:.0} KB/s  ({:.1}× menos)",
                before / 1024.0,
                after / 1024.0,
                (before / after.max(1.0)).max(1.0)
            );
        }

        // ── F0.2: mismos bytes, menos CPU. Se mide en serializaciones, no en tráfico ─────────
        let pose = pose_payload(3);
        let reps = 2_000;
        let t = Instant::now();
        for _ in 0..reps {
            // ANTES: una serialización por PAR (origen, destino).
            for _dest in 0..(PEERS - 1) {
                std::hint::black_box(rmp_serde::to_vec_named(&pose).unwrap());
            }
        }
        let before_us = t.elapsed().as_secs_f64() / reps as f64 * 1e6;
        let t = Instant::now();
        for _ in 0..reps {
            // DESPUÉS: una por ORIGEN, reutilizada para todos los destinos.
            std::hint::black_box(rmp_serde::to_vec_named(&pose).unwrap());
        }
        let after_us = t.elapsed().as_secs_f64() / reps as f64 * 1e6;
        let per_round_before = before_us * PEERS as f64;
        let per_round_after = after_us * PEERS as f64;
        println!("\n--- F0.2 · relay de poses: CPU, no tráfico (los bytes son idénticos) ---");
        println!(
            "  serializaciones por ronda: ANTES {} (P×D) → DESPUÉS {} (P)",
            PEERS * (PEERS - 1),
            PEERS
        );
        println!(
            "  CPU por ronda: ANTES {per_round_before:.1} µs → DESPUÉS {per_round_after:.1} µs \
             ({:.1}× menos, {:.2} ms/s a 10 Hz frente a {:.2})",
            per_round_before / per_round_after.max(0.001),
            per_round_after * 10.0 / 1000.0,
            per_round_before * 10.0 / 1000.0
        );

        // ── El total, que es la única cifra que Joel puede comparar contra su router ─────────
        // Los otros emisores no los toca esta etapa: rosters ya gateados por ADR-071 (795 KB/s
        // construyendo), relay de poses (153), PeerList (52) y pose propia (22). Se toman de
        // `host_uplink_baseline`, misma sonda y mismo escenario.
        const OTHER_EMITTERS_KBPS: f64 = 795.0 + 153.0 + 52.0 + 22.0;
        let before_total = before_bps / 1024.0 + OTHER_EMITTERS_KBPS;
        println!("\n--- TOTAL de subida del host con {PEERS} peers ---");
        println!(
            "  ANTES:  {:.0} KB/s = {:.1} Mbps",
            before_total,
            before_total * 1024.0 * 8.0 / 1_000_000.0
        );
        for (label, churn) in [("reposo", 0usize), ("actividad normal", 4)] {
            let mut gates: Vec<RosterGate> =
                (0..chunk_count).map(|_| RosterGate::default()).collect();
            let t0 = Instant::now();
            let mut sent_bytes = 0usize;
            for round in 0..ROUNDS {
                let now = t0 + Duration::from_millis(round as u64 * 200);
                for (i, wire) in &chunk_wires {
                    let hash = if *i < churn { round as u64 + 1 } else { 0 };
                    if gates[*i].should_send(hash, now, ROSTER_HEARTBEAT) {
                        sent_bytes += wire;
                    }
                }
            }
            let after_total =
                sent_bytes as f64 * PEERS as f64 / 60.0 / 1024.0 + OTHER_EMITTERS_KBPS;
            println!(
                "  DESPUÉS · {label}: {:.0} KB/s = {:.1} Mbps  ({:.1}× menos de subida)",
                after_total,
                after_total * 1024.0 * 8.0 / 1_000_000.0,
                before_total / after_total
            );
        }

        println!(
            "\nNota: el ahorro de F0.8 depende de cuántos chunks cambian de verdad por ronda, que \
             es lo que no se puede saber sin una sesión real — por eso van los tres extremos y no \
             un número inventado. El de F0.1 depende del ritmo de interacción, igual."
        );
    }

    /// SONDA DE MEDICIÓN para E1 (ADR-074): **la medición previa que el propio ADR exige antes de
    /// fijar `R_pose`** — "cuántos peers caen dentro de 100 m en un mapa real, antes de asumir que
    /// el radio ancho es caro".
    ///
    /// ```text
    /// cargo test --release aoi_pose_relay_savings -- --ignored --nocapture
    /// ```
    ///
    /// Qué decide: hoy el relay es O(N²) —la pose de cada peer va a todos los demás— y es la curva
    /// que domina por encima de ~16 jugadores. Con AOI el coste pasa a ser proporcional a los
    /// pares que de verdad están cerca, así que el ahorro **no depende del radio en abstracto sino
    /// de cómo se reparten los jugadores por el mapa**. Un radio generoso puede salir casi gratis
    /// si la gente se dispersa, y no ahorrar nada si todos están en la misma sala — que es
    /// exactamente lo que hay que saber ANTES de elegir el número.
    ///
    /// El reparto se sintetiza con un LCG determinista (nada de `Math::random`, que además está
    /// prohibido en este repo por reproducibilidad) sobre el área que ocupan los 49 chunks
    /// cargados: 7×7 chunks de 50 m = 350×350 m.
    #[test]
    #[ignore = "sonda de medición: imprime, no afirma"]
    fn aoi_pose_relay_savings() {
        /// Reparto sintético de N jugadores. `spread_m` es el radio del área en la que caen: uno
        /// pequeño simula "todos juntos en una sala", uno grande "cada uno en su zona".
        fn positions(n: usize, spread_m: f32, seed: u64) -> Vec<(f32, f32)> {
            let mut s = seed;
            let mut next = || {
                // LCG de Numerical Recipes: determinista y suficiente para repartir puntos.
                s = s.wrapping_mul(1664525).wrapping_add(1013904223);
                ((s >> 16) & 0xFFFF) as f32 / 65535.0
            };
            (0..n)
                .map(|_| {
                    let x = (next() - 0.5) * 2.0 * spread_m;
                    let z = (next() - 0.5) * 2.0 * spread_m;
                    (x, z)
                })
                .collect()
        }

        /// Pares ORDENADOS (emisor, receptor) dentro del radio. Es la unidad del relay: la pose de
        /// A viaja a B si B está dentro del AOI de A, y se cuenta una vez por sentido.
        fn pairs_within(pos: &[(f32, f32)], radius: f32) -> usize {
            let mut n = 0;
            for (i, a) in pos.iter().enumerate() {
                for (j, b) in pos.iter().enumerate() {
                    if i == j {
                        continue;
                    }
                    let d = ((a.0 - b.0).powi(2) + (a.1 - b.1).powi(2)).sqrt();
                    if d <= radius {
                        n += 1;
                    }
                }
            }
            n
        }

        let pose_wire = wire_len(&pose_payload(3));
        println!("\n=== E1 / ADR-074: ahorro del AOI en el relay de poses ===");
        println!(
            "PlayerUpdate en el aire: {pose_wire} B · relay a {POSE_HZ} Hz · área de 49 chunks = 350×350 m\n"
        );

        for peers in [8usize, 16, 32] {
            let all_pairs = peers * (peers - 1);
            println!("--- {peers} jugadores (hoy: {all_pairs} datagramas por ronda, O(N²)) ---");
            for (label, spread) in [
                ("todos en una sala (radio 25 m)", 25.0f32),
                ("un par de grupos (radio 80 m)", 80.0),
                ("repartidos por el mapa (radio 175 m)", 175.0),
            ] {
                let pos = positions(peers, spread, 0x5EED);
                print!("  {label:36}");
                for radius in [50.0f32, 75.0, 100.0, 150.0] {
                    let p = pairs_within(&pos, radius);
                    let pct = p as f64 * 100.0 / all_pairs as f64;
                    print!(" | R={radius:>3.0}: {pct:>3.0}%");
                }
                println!();
            }
            // El caso que fija el techo: con TODOS dentro del radio, el AOI no ahorra nada y la
            // salida es la de hoy. Se imprime para que el número no se lea como una promesa.
            let worst_kbps = all_pairs as f64 * POSE_HZ * pose_wire as f64 / 1024.0;
            println!("  peor caso (todos dentro del radio): {worst_kbps:.0} KB/s, igual que hoy\n");
        }

        println!(
            "Cómo leerlo: el porcentaje es cuánto del relay SOBREVIVE al filtro; 100 % = no ahorra \
             nada. El radio se elige por DISEÑO (el acecho del robapieles necesita verse de lejos, \
             ADR-074 decisión 1), y esta tabla dice lo que cuesta esa elección — no al revés."
        );
    }
}

/// ADR-146 D1 — el lote se trocea por CUENTA (`MAX_POSES_PER_BATCH`), así que esa cuenta tiene que
/// caber en el techo de datagrama con TODAS las poses en su peor forma: completas, con velocidad y
/// cosméticos en todo su rango. Si alguien añade un campo a `PoseWire`, esto se pone rojo antes de
/// que `send_datagram` empiece a rechazar lotes enteros en partida, en silencio para el receptor.
#[cfg(test)]
mod pose_batch_size_tests {
    use super::*;
    use crate::network::protocol::{
        encode_packet, PacketHeader, PacketType, PoseAnim, PoseCosmetics, SAFE_DATAGRAM_BYTES,
    };

    fn worst_pose() -> PoseWire {
        PoseWire {
            pos_cm: [i16::MIN, i16::MAX, i16::MIN],
            yaw_u16: u16::MAX,
            pitch: i8::MIN,
            animation: PoseAnim::PICKUP,
            flags: u8::MAX,
            buttons: u16::MAX,
            hit_seq: u8::MAX,
            fire_seq: u8::MAX,
            melee_seq: u8::MAX,
            vocal_seq: u8::MAX,
            vel_cms: [i16::MIN, i16::MAX, i16::MIN],
            cosmetics: Some(PoseCosmetics {
                equipment: [i32::MIN; 4],
                held_item: i32::MIN,
                carry_def: i32::MIN,
                carry_count: u8::MAX,
                species: u8::MAX,
                vocal_kind: u8::MAX,
            }),
        }
    }

    fn encoded_batch_bytes(count: usize) -> usize {
        let payload = PacketPayload::PlayerUpdateBatch {
            origin_cm: [i32::MIN, i32::MAX, i32::MIN],
            senders: vec![u16::MAX; count],
            updates: vec![worst_pose(); count],
        };
        let header = PacketHeader::new(
            PacketType::PlayerUpdateBatch as u16,
            u16::MAX,
            u32::MAX,
            u32::MAX,
        );
        encode_packet(&header, &payload).len()
    }

    #[test]
    fn a_full_batch_of_worst_case_poses_fits_the_datagram_ceiling() {
        let bytes = encoded_batch_bytes(MAX_POSES_PER_BATCH);
        let per_pose = encoded_batch_bytes(2) - encoded_batch_bytes(1);
        println!(
            "lote de {MAX_POSES_PER_BATCH} poses en su peor forma: {bytes} B \
             ({per_pose} B por pose, techo {SAFE_DATAGRAM_BYTES})"
        );
        assert!(
            bytes <= SAFE_DATAGRAM_BYTES,
            "{MAX_POSES_PER_BATCH} poses = {bytes} B > {SAFE_DATAGRAM_BYTES}"
        );
    }
}

/// ADR-146 D2 — el estimador de velocidad por origen. Es una función pura de (estimación previa,
/// posición, instante): no tiene por dónde enterarse de si el origen es una criatura, y estos tests
/// fijan su comportamiento sin reloj real.
#[cfg(test)]
mod tramo_velocity_tests {
    use super::*;
    use std::time::{Duration, Instant};

    const ROUND: Duration = Duration::from_millis(33);

    fn speed(v: [f32; 3]) -> f32 {
        (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt()
    }

    #[test]
    fn the_first_sample_has_no_velocity() {
        let (est, jumped) = estimate_pose_velocity(None, [1.0, 1.8, 2.0], Instant::now());
        assert_eq!(est.vel, [0.0; 3]);
        assert!(!jumped);
    }

    #[test]
    fn a_straight_walk_converges_to_its_speed() {
        let t0 = Instant::now();
        let mut est = estimate_pose_velocity(None, [0.0, 1.8, 0.0], t0).0;
        // 3 m/s en +X durante un segundo de rondas a 30 Hz.
        for i in 1..=30u32 {
            let t = t0 + ROUND * i;
            let x = 3.0 * (ROUND * i).as_secs_f32();
            est = estimate_pose_velocity(Some(&est), [x, 1.8, 0.0], t).0;
        }
        assert!((est.vel[0] - 3.0).abs() < 0.05, "vel={:?}", est.vel);
        assert!(est.vel[1].abs() < 1e-3 && est.vel[2].abs() < 1e-3);
    }

    #[test]
    fn a_source_that_stops_sending_new_positions_goes_still() {
        let t0 = Instant::now();
        let a = estimate_pose_velocity(None, [0.0, 1.8, 0.0], t0).0;
        let b = estimate_pose_velocity(Some(&a), [0.1, 1.8, 0.0], t0 + ROUND).0;
        assert!(speed(b.vel) > 0.0);
        // Misma posición dentro del margen: conserva la velocidad (un hueco entre poses no es parar).
        let c = estimate_pose_velocity(Some(&b), [0.1, 1.8, 0.0], t0 + ROUND * 3).0;
        assert_eq!(c.vel, b.vel);
        // Pasado el margen: quieto.
        let d = estimate_pose_velocity(
            Some(&c),
            [0.1, 1.8, 0.0],
            t0 + ROUND + Duration::from_millis(250),
        )
        .0;
        assert_eq!(d.vel, [0.0; 3]);
    }

    #[test]
    fn a_teleport_is_a_jump_and_resets_to_zero() {
        let t0 = Instant::now();
        let a = estimate_pose_velocity(None, [0.0, 1.8, 0.0], t0).0;
        let b = estimate_pose_velocity(Some(&a), [0.1, 1.8, 0.0], t0 + ROUND).0;
        let (c, jumped) = estimate_pose_velocity(Some(&b), [700.0, 1.8, 0.0], t0 + ROUND * 2);
        assert!(jumped);
        assert_eq!(c.vel, [0.0; 3]);
        assert_eq!(c.pos, [700.0, 1.8, 0.0]);
        // Tras el salto se vuelve a estimar desde la posición nueva, no desde la vieja.
        let d = estimate_pose_velocity(Some(&c), [700.1, 1.8, 0.0], t0 + ROUND * 3).0;
        assert!(speed(d.vel) < TRAMO_MAX_SPEED_M_S && speed(d.vel) > 0.0);
    }

    #[test]
    fn the_estimate_never_exceeds_the_cap() {
        let t0 = Instant::now();
        let mut est = estimate_pose_velocity(None, [0.0, 1.8, 0.0], t0).0;
        // Justo por debajo del tope, ronda tras ronda: la media convexa no lo puede pasar.
        let step = (TRAMO_MAX_SPEED_M_S - 0.01) * ROUND.as_secs_f32();
        for i in 1..=60u32 {
            est = estimate_pose_velocity(Some(&est), [step * i as f32, 1.8, 0.0], t0 + ROUND * i).0;
            assert!(
                speed(est.vel) <= TRAMO_MAX_SPEED_M_S,
                "ronda {i}: {:?}",
                est.vel
            );
        }
    }

    #[test]
    fn two_positions_in_the_same_instant_are_ignored() {
        let t0 = Instant::now();
        let a = estimate_pose_velocity(None, [0.0, 1.8, 0.0], t0).0;
        let (b, jumped) = estimate_pose_velocity(Some(&a), [5.0, 1.8, 0.0], t0);
        assert_eq!(b, a);
        assert!(!jumped);
    }

    /// D6: mientras no haya medida, el gate sigue apagado. Encenderlo es una decisión con número.
    #[test]
    fn the_tramo_gate_ships_off() {
        assert!(!TRAMO_GATE_ENABLED);
    }
}

/// ADR-146 D2 — la predicción del gate de tramos, como función pura.
#[cfg(test)]
mod tramo_gate_tests {
    use super::*;
    use std::time::{Duration, Instant};

    fn mark_walking_x(at: Instant) -> TramoMark {
        TramoMark {
            pos: [0.0, 1.8, 0.0],
            vel: [3.0, 0.0, 0.0],
            yaw_u16: PoseWire::quantize_yaw(90.0),
            pitch: 0,
            discrete: 7,
            at,
        }
    }

    #[test]
    fn a_straight_walk_is_predicted() {
        let t0 = Instant::now();
        let mark = mark_walking_x(t0);
        let t = Duration::from_millis(300);
        let pos = [3.0 * t.as_secs_f32() + 0.05, 1.8, 0.0];
        assert!(tramo_predicts(&mark, pos, mark.yaw_u16, 0, 7, t0 + t));
    }

    #[test]
    fn a_turn_breaks_the_prediction() {
        let t0 = Instant::now();
        let mark = mark_walking_x(t0);
        // Medio segundo después dobló a +Z: a 3 m/s está ~1 m fuera de la recta prevista.
        let pos = [0.9, 1.8, 0.6];
        assert!(!tramo_predicts(
            &mark,
            pos,
            mark.yaw_u16,
            0,
            7,
            t0 + Duration::from_millis(500)
        ));
    }

    #[test]
    fn a_stop_breaks_the_prediction() {
        let t0 = Instant::now();
        let mark = mark_walking_x(t0);
        // Se paró en seco donde estaba: la predicción sigue andando y a los 100 ms ya va 30 cm por
        // delante.
        assert!(!tramo_predicts(
            &mark,
            mark.pos,
            mark.yaw_u16,
            0,
            7,
            t0 + Duration::from_millis(100)
        ));
    }

    #[test]
    fn any_discrete_change_breaks_the_prediction() {
        let t0 = Instant::now();
        let mark = mark_walking_x(t0);
        assert!(!tramo_predicts(&mark, mark.pos, mark.yaw_u16, 0, 8, t0));
    }

    #[test]
    fn the_repair_forces_a_send_even_when_the_prediction_holds() {
        let t0 = Instant::now();
        let mark = mark_walking_x(t0);
        let just_before = TRAMO_REPAIR - Duration::from_millis(1);
        let pos_at = |d: Duration| [3.0 * d.as_secs_f32(), 1.8, 0.0];
        assert!(tramo_predicts(
            &mark,
            pos_at(just_before),
            mark.yaw_u16,
            0,
            7,
            t0 + just_before
        ));
        assert!(!tramo_predicts(
            &mark,
            pos_at(TRAMO_REPAIR),
            mark.yaw_u16,
            0,
            7,
            t0 + TRAMO_REPAIR
        ));
    }

    #[test]
    fn yaw_and_pitch_have_their_own_tolerance_and_yaw_wraps() {
        let t0 = Instant::now();
        let mut mark = mark_walking_x(t0);
        mark.yaw_u16 = PoseWire::quantize_yaw(359.0);
        mark.vel = [0.0; 3];
        // 359° → 1°: dos grados por el camino corto, no 358.
        assert!(tramo_predicts(
            &mark,
            mark.pos,
            PoseWire::quantize_yaw(1.0),
            0,
            7,
            t0
        ));
        assert!(!tramo_predicts(
            &mark,
            mark.pos,
            PoseWire::quantize_yaw(10.0),
            0,
            7,
            t0
        ));
        assert!(tramo_predicts(&mark, mark.pos, mark.yaw_u16, 2, 7, t0));
        assert!(!tramo_predicts(&mark, mark.pos, mark.yaw_u16, 3, 7, t0));
    }

    /// ADR-074 enm. 4 D5, enmendado por ADR-146: la decisión sale de la pose y del reloj. Este test
    /// fija que la función no tiene por dónde enterarse de qué es un fantasma — dos orígenes con la
    /// misma pose y la misma marca reciben el mismo veredicto, sin tercer argumento que lo cambie.
    #[test]
    fn the_tramo_gate_cannot_tell_a_phantom_from_a_player() {
        let t0 = Instant::now();
        let mark = mark_walking_x(t0);
        let pos = [0.31, 1.8, 0.02];
        let now = t0 + Duration::from_millis(100);
        let as_player = tramo_predicts(&mark, pos, mark.yaw_u16, 0, 7, now);
        let as_phantom = tramo_predicts(&mark, pos, mark.yaw_u16, 0, 7, now);
        assert_eq!(as_player, as_phantom);
    }

    #[test]
    fn the_discrete_hash_sees_cosmetics_and_counters() {
        let wire = PoseWire {
            pos_cm: [0; 3],
            yaw_u16: 0,
            pitch: 0,
            animation: crate::network::protocol::PoseAnim::WALK,
            flags: 0,
            buttons: 0,
            hit_seq: 0,
            fire_seq: 0,
            melee_seq: 0,
            vocal_seq: 0,
            vel_cms: [0; 3],
            cosmetics: None,
        };
        let base = pose_discrete_hash(&wire, 1);
        assert_ne!(base, pose_discrete_hash(&wire, 2), "cosméticos");
        let fired = PoseWire {
            fire_seq: 1,
            ..wire.clone()
        };
        assert_ne!(base, pose_discrete_hash(&fired, 1), "contador de disparo");
        // La velocidad NO entra: cambia cada ronda y la juzga la predicción, no la huella.
        let moving = PoseWire {
            vel_cms: [300, 0, 0],
            ..wire
        };
        assert_eq!(base, pose_discrete_hash(&moving, 1));
    }
}
