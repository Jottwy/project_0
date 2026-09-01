//! La tabla de sesiones del relay: quién está, quién puede hablar con quién, y cuándo dejar de
//! creérselo — ADR-117 D6 y D9.
//!
//! **Sin sockets dentro, y ésa es la razón de que este módulo exista aparte del binario.** Entra
//! «llegó este búfer desde esta dirección en este instante» y salen ACCIONES; el `main` es quien
//! las ejecuta. Todo lo que de verdad se puede equivocar —la autenticación, la estrella, los
//! timeouts, el aforo, el límite de tasa— se prueba así en microsegundos y sin red, que es la
//! única forma de tener tests que no dependan de que la máquina de quien los corre tenga puertos
//! libres.
//!
//! **El relay no simula nada** (ADR-117 D2). Aquí no hay mundo, ni jugadores, ni reglas: hay
//! direcciones, identidades de transporte y una tabla de a quién reenviar. El payload es opaco y
//! ni siquiera se copia — ver [`Action::Forward`].
//!
//! ## Lo que impide que esto sea un relay abierto
//!
//! Cuatro cosas, y ninguna sobra:
//!
//! 1. **Token de sesión** de 16 bytes (D9). Sin él no se crea ni se entra en ninguna sesión, así
//!    que quien no ha visto el lobby no tiene nada que hacer aquí.
//! 2. **Límite de tasa por IP** sobre los tres mensajes que no exigen ser ya un peer conocido.
//! 3. **Silencio** ante cualquier cosa que no se reconozca. Un datagrama de basura no recibe ni un
//!    byte de respuesta: sin respuesta no hay amplificación posible.
//! 4. **Aforo** de sesiones y de peers por sesión, para que una sola partida no se lleve la
//!    máquina por delante.
//!
//! ## Limitación conocida: el rebinding de NAT
//!
//! Un peer se identifica por su dirección de origen. Si su NAT le cambia el puerto a mitad de
//! partida, sus datagramas pasan a llegar desde una dirección desconocida y el relay los ignora
//! hasta que caduca. El latido cada 2 s mantiene vivo el binding, que es lo que hace que el caso
//! sea raro; la recuperación es volver a entrar. Se anota porque es real y no se finge cubierto.

use std::collections::HashMap;
use std::net::{IpAddr, SocketAddr};
use std::time::{Duration, Instant};

use log::{debug, info, warn};

use crate::protocol::{
    AuthStatus, CloseReason, DisconnectReason, Envelope, MessageType, PeerId, RelayFrame,
    RelayMessage, SessionId, SessionToken, RELAY_ENDPOINT,
};

/// El host es SIEMPRE el peer 1 de su sesión de relay. No se sortea ni se negocia: quien crea la
/// sesión es el host, y tener su id fijo hace que la regla de la estrella —«todo joiner habla con
/// el 1»— sea una comparación y no una búsqueda.
pub const HOST_PEER_ID: PeerId = 1;

/// El primer id que se reparte a un joiner.
pub const FIRST_JOINER_PEER_ID: PeerId = 2;

/// Los topes. Todos con valor por defecto y todos ajustables desde el binario, porque el tamaño de
/// la máquina que acabe alojando esto no se sabe todavía.
#[derive(Debug, Clone)]
pub struct RelayLimits {
    /// Sesiones simultáneas en toda la máquina.
    pub max_sessions: usize,

    /// Peers por sesión, **host incluido**. Espejo de `SessionConfig::default().max_players` del
    /// backend (50): un relay más generoso que la partida no sirve de nada, y uno más tacaño
    /// rechazaría a gente que el host sí admite.
    pub max_peers_per_session: usize,

    /// Sin latido durante esto, el peer se da por muerto. Cuatro latidos de margen sobre el
    /// intervalo de 2 s: uno perdido no puede expulsar a nadie.
    pub peer_timeout: Duration,

    /// Una sesión sin un solo datagrama durante esto se cierra. Cubre el caso feo: un host que
    /// desaparece sin despedirse dejaría su sesión ocupando sitio para siempre.
    pub session_idle_timeout: Duration,

    /// Cuántos mensajes de los que NO exigen ser ya un peer (`Connect`, `SessionCreate`,
    /// `SessionJoin`) se atienden por segundo y por IP.
    pub handshakes_per_second_per_ip: u32,
}

impl Default for RelayLimits {
    fn default() -> Self {
        Self {
            max_sessions: 256,
            max_peers_per_session: 50,
            peer_timeout: Duration::from_secs(8),
            session_idle_timeout: Duration::from_secs(120),
            handshakes_per_second_per_ip: 5,
        }
    }
}

/// Contadores. Es toda la observabilidad del relay y por eso son los números que hay que poder
/// mirar sin adivinar: cuánto entra, cuánto se reenvía, y **por qué** se descarta lo que se
/// descarta — cada motivo por separado, porque cada uno manda a mirar un sitio distinto.
#[derive(Debug, Default, Clone, PartialEq, Eq)]
pub struct RelayStats {
    pub datagrams_in: u64,
    pub malformed: u64,
    pub forwarded: u64,
    pub forwarded_bytes: u64,
    pub sessions_created: u64,
    pub sessions_closed: u64,
    pub peers_admitted: u64,
    pub peers_timed_out: u64,
    pub auth_denied: u64,
    pub rate_limited: u64,
    /// Un joiner intentó hablar con otro joiner. **Nunca debería subir**: si sube, hay un cliente
    /// modificado o un build sin el filtro de estrella (ADR-015).
    pub star_violations: u64,
    /// Datagramas de alguien que no es un peer conocido de la sesión que dice.
    pub unknown_sender: u64,
}

/// Lo que el relay decide hacer con un datagrama. El binario las ejecuta; este módulo no toca la
/// red.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Action {
    /// Un mensaje de control: hay que codificarlo y mandarlo.
    Send { to: SocketAddr, frame: RelayFrame },

    /// Reenviar el datagrama **original**, con el origen y el destino reescritos in situ
    /// (`protocol::rewrite_src`/`rewrite_dst`). No se vuelve a codificar nada y el payload no se
    /// copia: son dos campos de 2 bytes sobre el búfer que el binario ya tiene en la mano.
    Forward {
        to: SocketAddr,
        src: PeerId,
        dst: PeerId,
    },
}

#[derive(Debug, Clone)]
struct PeerSlot {
    id: PeerId,
    addr: SocketAddr,
    last_seen: Instant,
}

#[derive(Debug)]
struct Session {
    token: SessionToken,
    wire_version: u16,
    peers: HashMap<PeerId, PeerSlot>,
    next_peer_id: PeerId,
    last_activity: Instant,
}

impl Session {
    fn host_addr(&self) -> Option<SocketAddr> {
        self.peers.get(&HOST_PEER_ID).map(|p| p.addr)
    }
}

#[derive(Debug, Clone, Copy)]
struct RateBucket {
    window_start: Instant,
    count: u32,
}

/// La tabla entera. Un solo dueño (el bucle del binario), sin `Arc` ni bloqueos: el relay es de un
/// hilo por diseño — reenviar un datagrama es leer 16 bytes y llamar a `send_to`, y repartir eso
/// entre hilos costaría más en sincronización de lo que ahorra.
#[derive(Debug)]
pub struct RelayTable {
    limits: RelayLimits,
    sessions: HashMap<SessionId, Session>,
    by_addr: HashMap<SocketAddr, (SessionId, PeerId)>,
    rate: HashMap<IpAddr, RateBucket>,
    stats: RelayStats,
}

impl RelayTable {
    pub fn new(limits: RelayLimits) -> Self {
        Self {
            limits,
            sessions: HashMap::new(),
            by_addr: HashMap::new(),
            rate: HashMap::new(),
            stats: RelayStats::default(),
        }
    }

    pub fn stats(&self) -> &RelayStats {
        &self.stats
    }

    pub fn session_count(&self) -> usize {
        self.sessions.len()
    }

    pub fn peer_count(&self) -> usize {
        self.by_addr.len()
    }

    /// Los peers de una sesión, ordenados. Para los tests y para el volcado de métricas.
    pub fn session_peers(&self, id: SessionId) -> Vec<PeerId> {
        let mut ids: Vec<PeerId> = self
            .sessions
            .get(&id)
            .map(|s| s.peers.keys().copied().collect())
            .unwrap_or_default();
        ids.sort_unstable();
        ids
    }

    /// Un datagrama acaba de llegar. Nunca entra en pánico y nunca bloquea.
    pub fn handle(&mut self, from: SocketAddr, buf: &[u8], now: Instant) -> Vec<Action> {
        self.stats.datagrams_in += 1;

        let env = match Envelope::peek(buf) {
            Ok(env) => env,
            Err(e) => {
                // SILENCIO. Un puerto público recibe basura y escáneres a todas horas; contestarle
                // a cada uno sería regalar amplificación y confirmar que aquí hay algo.
                self.stats.malformed += 1;
                debug!("RELAY event=datagram_dropped reason=malformed from={from} error={e}");
                return Vec::new();
            }
        };

        match env.msg_type {
            MessageType::Data => self.on_data(from, &env, buf.len(), now),
            MessageType::Heartbeat => self.on_heartbeat(from, &env, now),
            MessageType::Connect => self.on_connect(from, now),
            MessageType::SessionCreate | MessageType::SessionJoin => {
                match RelayFrame::decode(buf) {
                    Ok(frame) => self.on_session_message(from, frame, now),
                    Err(e) => {
                        self.stats.malformed += 1;
                        debug!(
                            "RELAY event=datagram_dropped reason=bad_body from={from} error={e}"
                        );
                        Vec::new()
                    }
                }
            }
            // Auth, PeerReady, PeerJoined, PeerDisconnected y SessionClosed los emite el RELAY.
            // Que lleguen de fuera significa un cliente confundido o alguien probando: se cuentan
            // y se callan.
            other => {
                self.stats.malformed += 1;
                debug!(
                    "RELAY event=datagram_dropped reason=relay_only_message from={from} type={other:?}"
                );
                Vec::new()
            }
        }
    }

    /// El paso del tiempo: expulsa a los que dejaron de latir y cierra lo que sobra. Lo llama el
    /// binario una vez por segundo.
    pub fn tick(&mut self, now: Instant) -> Vec<Action> {
        let mut actions = Vec::new();

        // 1) Peers muertos. Se recogen primero porque no se puede mutar la tabla mientras se
        //    recorre, y porque el host caído cierra la sesión ENTERA — hay que decidirlo después
        //    de saber quién falta.
        let mut dead: Vec<(SessionId, PeerId)> = Vec::new();
        for (sid, session) in &self.sessions {
            for peer in session.peers.values() {
                if now.duration_since(peer.last_seen) >= self.limits.peer_timeout {
                    dead.push((*sid, peer.id));
                }
            }
        }

        for (sid, pid) in dead {
            self.stats.peers_timed_out += 1;
            warn!("RELAY event=peer_timeout session={sid} peer={pid}");
            actions.extend(self.remove_peer(sid, pid, DisconnectReason::Timeout, now));
        }

        // 2) Sesiones ociosas. Cubre al host que se evapora sin despedirse.
        let idle: Vec<SessionId> = self
            .sessions
            .iter()
            .filter(|(_, s)| {
                now.duration_since(s.last_activity) >= self.limits.session_idle_timeout
            })
            .map(|(id, _)| *id)
            .collect();
        for sid in idle {
            info!("RELAY event=session_idle session={sid}");
            actions.extend(self.close_session(sid, CloseReason::Idle));
        }

        // 3) Cubos de tasa caducados. Sin esto, la memoria del relay crece con cada IP que le
        //    escriba una vez en la vida — que en un puerto público es una fuga lenta y garantizada.
        self.rate
            .retain(|_, bucket| now.duration_since(bucket.window_start) < Duration::from_secs(60));

        actions
    }

    /// Cierra una sesión y avisa a todo el mundo. Idempotente.
    pub fn close_session(&mut self, id: SessionId, reason: CloseReason) -> Vec<Action> {
        let Some(session) = self.sessions.remove(&id) else {
            return Vec::new();
        };

        self.stats.sessions_closed += 1;
        info!(
            "RELAY event=session_closed session={id} reason={} peers={}",
            reason.name(),
            session.peers.len()
        );

        let mut actions = Vec::with_capacity(session.peers.len());
        for peer in session.peers.values() {
            self.by_addr.remove(&peer.addr);
            actions.push(Action::Send {
                to: peer.addr,
                frame: RelayFrame::to_peer(
                    id,
                    RELAY_ENDPOINT,
                    peer.id,
                    RelayMessage::SessionClosed { reason },
                ),
            });
        }
        actions
    }

    // ─── Mensajes ──────────────────────────────────────────────────────────────────────────

    fn on_connect(&mut self, from: SocketAddr, now: Instant) -> Vec<Action> {
        if let Some(denied) = self.rate_limit(from, 0, now) {
            return denied;
        }
        // La respuesta (17 B) es un byte más grande que la pregunta (16 B). Merece decirse: es un
        // factor de amplificación de 1,06, contra los 50-500x que hacen útil un reflector, y
        // además topado por el límite de tasa. No es una vía de ataque; es un ping.
        vec![Action::Send {
            to: from,
            frame: RelayFrame::to_peer(
                0,
                RELAY_ENDPOINT,
                0,
                RelayMessage::Auth {
                    status: AuthStatus::Ok,
                },
            ),
        }]
    }

    fn on_session_message(
        &mut self,
        from: SocketAddr,
        frame: RelayFrame,
        now: Instant,
    ) -> Vec<Action> {
        let session_id = frame.session_id;
        if let Some(denied) = self.rate_limit(from, session_id, now) {
            return denied;
        }

        match frame.message {
            RelayMessage::SessionCreate {
                token,
                wire_version,
            } => self.on_session_create(from, session_id, token, wire_version, now),
            RelayMessage::SessionJoin {
                token,
                wire_version,
            } => self.on_session_join(from, session_id, token, wire_version, now),
            _ => Vec::new(),
        }
    }

    fn on_session_create(
        &mut self,
        from: SocketAddr,
        id: SessionId,
        token: SessionToken,
        wire_version: u16,
        now: Instant,
    ) -> Vec<Action> {
        if let Some(existing) = self.sessions.get_mut(&id) {
            // Reintento del mismo host: la creación es idempotente. Es el mismo precedente que
            // `handle_handshake` en el backend, y por el mismo motivo — sobre UDP el primer
            // intento se pierde a menudo, y un reintento no puede tirar la sesión que ya existe.
            if existing.token == token && existing.host_addr() == Some(from) {
                existing.last_activity = now;
                if let Some(host) = existing.peers.get_mut(&HOST_PEER_ID) {
                    host.last_seen = now;
                }
                return vec![Self::peer_ready(id, from, HOST_PEER_ID)];
            }

            // Otra dirección pidiendo crear una sesión que ya existe. Con el token correcto es un
            // host que cambió de IP; sin él, alguien probando. No se distingue en la respuesta a
            // propósito.
            self.stats.auth_denied += 1;
            warn!(
                "RELAY event=session_create_denied session={id} from={from} reason={}",
                AuthStatus::Denied.name()
            );
            return vec![Self::auth(id, from, AuthStatus::Denied)];
        }

        if self.sessions.len() >= self.limits.max_sessions {
            self.stats.auth_denied += 1;
            warn!(
                "RELAY event=session_create_denied session={id} from={from} reason={} sessions={}",
                AuthStatus::RelayFull.name(),
                self.sessions.len()
            );
            return vec![Self::auth(id, from, AuthStatus::RelayFull)];
        }

        let mut peers = HashMap::new();
        peers.insert(
            HOST_PEER_ID,
            PeerSlot {
                id: HOST_PEER_ID,
                addr: from,
                last_seen: now,
            },
        );
        self.sessions.insert(
            id,
            Session {
                token,
                wire_version,
                peers,
                next_peer_id: FIRST_JOINER_PEER_ID,
                last_activity: now,
            },
        );
        self.by_addr.insert(from, (id, HOST_PEER_ID));
        self.stats.sessions_created += 1;
        self.stats.peers_admitted += 1;
        // El token NO aparece aquí, y no puede aparecer ni por accidente: su `Debug` no lo enseña.
        info!("RELAY event=session_created session={id} host={from} wire={wire_version}");

        vec![Self::peer_ready(id, from, HOST_PEER_ID)]
    }

    fn on_session_join(
        &mut self,
        from: SocketAddr,
        id: SessionId,
        token: SessionToken,
        wire_version: u16,
        now: Instant,
    ) -> Vec<Action> {
        let max_peers = self.limits.max_peers_per_session;
        let Some(session) = self.sessions.get_mut(&id) else {
            // **El mismo `Denied` que un token malo.** Distinguir «no existe» de «token
            // incorrecto» convertiría el relay en un oráculo para enumerar sesiones ajenas.
            self.stats.auth_denied += 1;
            warn!("RELAY event=session_join_denied session={id} from={from} reason=DENIED");
            return vec![Self::auth(id, from, AuthStatus::Denied)];
        };

        if session.token != token {
            self.stats.auth_denied += 1;
            warn!("RELAY event=session_join_denied session={id} from={from} reason=DENIED");
            return vec![Self::auth(id, from, AuthStatus::Denied)];
        }

        if session.wire_version != wire_version {
            // Se corta AQUÍ y no más tarde. Con otra versión de wire el joiner entraría a un mundo
            // que no sabe leer, y el fallo aparecería mucho después, sin relación aparente con
            // esto.
            self.stats.auth_denied += 1;
            warn!(
                "RELAY event=session_join_denied session={id} from={from} reason={} theirs={wire_version} ours={}",
                AuthStatus::WireVersionMismatch.name(),
                session.wire_version
            );
            return vec![Self::auth(id, from, AuthStatus::WireVersionMismatch)];
        }

        // Reintento desde la misma dirección: idempotente, se le repite su `PeerReady`. Sin esto,
        // un `PeerReady` perdido —cosa normal en UDP— haría que el reintento consumiera una plaza
        // nueva y el joiner acabara con dos identidades.
        if let Some(existing) = session.peers.values().find(|p| p.addr == from) {
            let peer_id = existing.id;
            session.last_activity = now;
            if let Some(slot) = session.peers.get_mut(&peer_id) {
                slot.last_seen = now;
            }
            return vec![Self::peer_ready(id, from, peer_id)];
        }

        if session.peers.len() >= max_peers {
            self.stats.auth_denied += 1;
            warn!(
                "RELAY event=session_join_denied session={id} from={from} reason={} peers={}",
                AuthStatus::SessionFull.name(),
                session.peers.len()
            );
            return vec![Self::auth(id, from, AuthStatus::SessionFull)];
        }

        let peer_id = session.next_peer_id;
        session.next_peer_id = session.next_peer_id.saturating_add(1);
        session.peers.insert(
            peer_id,
            PeerSlot {
                id: peer_id,
                addr: from,
                last_seen: now,
            },
        );
        session.last_activity = now;
        let host_addr = session.host_addr();

        self.by_addr.insert(from, (id, peer_id));
        self.stats.peers_admitted += 1;
        info!("RELAY event=peer_joined session={id} peer={peer_id} from={from}");

        let mut actions = vec![Self::peer_ready(id, from, peer_id)];
        if let Some(host) = host_addr {
            actions.push(Action::Send {
                to: host,
                frame: RelayFrame::to_peer(
                    id,
                    RELAY_ENDPOINT,
                    HOST_PEER_ID,
                    RelayMessage::PeerJoined { peer: peer_id },
                ),
            });
        }
        actions
    }

    fn on_heartbeat(&mut self, from: SocketAddr, env: &Envelope, now: Instant) -> Vec<Action> {
        let Some((sid, pid)) = self.resolve(from, env) else {
            return Vec::new();
        };

        if let Some(session) = self.sessions.get_mut(&sid) {
            session.last_activity = now;
            if let Some(peer) = session.peers.get_mut(&pid) {
                peer.last_seen = now;
            }
        }

        // Se contesta al latido, y no es cortesía: es lo que le permite al peer medir el RTT del
        // relay y lo que mantiene abierto el agujero del NAT en el sentido relay → peer.
        vec![Action::Send {
            to: from,
            frame: RelayFrame::to_peer(sid, RELAY_ENDPOINT, pid, RelayMessage::Heartbeat),
        }]
    }

    fn on_data(
        &mut self,
        from: SocketAddr,
        env: &Envelope,
        bytes: usize,
        now: Instant,
    ) -> Vec<Action> {
        let Some((sid, pid)) = self.resolve(from, env) else {
            return Vec::new();
        };

        // ─── LA ESTRELLA (ADR-117 D6, ADR-015) ───
        //
        // Se impone AQUÍ y no se delega en el cliente. ADR-015 y la limpieza del 2026-08-31 ya
        // cerraron las superficies joiner→joiner del backend; un relay que reenviara a ciegas las
        // volvería a abrir, y encima con la bendición de la infraestructura.
        //
        // Un joiner que apunte a otro joiner NO se «corrige» redirigiéndolo al host: se descarta y
        // se cuenta. Corregirlo en silencio escondería exactamente el caso que este contador
        // existe para delatar.
        let dst = env.dst;
        if pid != HOST_PEER_ID && dst != HOST_PEER_ID {
            self.stats.star_violations += 1;
            warn!(
                "RELAY event=star_violation session={sid} from_peer={pid} to_peer={dst} — un joiner \
                 solo puede hablar con el host"
            );
            return Vec::new();
        }

        let Some(session) = self.sessions.get_mut(&sid) else {
            return Vec::new();
        };
        session.last_activity = now;
        if let Some(peer) = session.peers.get_mut(&pid) {
            peer.last_seen = now;
        }

        let Some(target) = session.peers.get(&dst) else {
            // El destino se fue entre que el emisor lo conoció y ahora. Normal en una
            // desconexión; no es un fallo de nadie.
            debug!("RELAY event=forward_dropped reason=unknown_destination session={sid} to_peer={dst}");
            return Vec::new();
        };

        self.stats.forwarded += 1;
        self.stats.forwarded_bytes += bytes as u64;
        vec![Action::Forward {
            to: target.addr,
            src: pid,
            dst,
        }]
    }

    // ─── Auxiliares ────────────────────────────────────────────────────────────────────────

    /// Quién manda esto, según la dirección de origen — **nunca según lo que el datagrama dice de
    /// sí mismo**. Un peer podría estampar el `src` de otro para suplantarlo ante el host; la
    /// dirección de origen no se puede falsificar sin estar en el camino.
    fn resolve(&mut self, from: SocketAddr, env: &Envelope) -> Option<(SessionId, PeerId)> {
        let Some((sid, pid)) = self.by_addr.get(&from).copied() else {
            self.stats.unknown_sender += 1;
            debug!("RELAY event=datagram_dropped reason=unknown_sender from={from}");
            return None;
        };

        if sid != env.session_id {
            // La dirección es de una sesión y el datagrama dice ser de otra. O es un búfer viejo
            // de una sesión anterior, o alguien probando.
            self.stats.unknown_sender += 1;
            debug!(
                "RELAY event=datagram_dropped reason=session_mismatch from={from} claimed={} real={sid}",
                env.session_id
            );
            return None;
        }

        Some((sid, pid))
    }

    /// Saca a un peer. Si el que se va es el HOST, la sesión entera muere: sin host no hay
    /// autoridad y ADR-056 ya dice que la sesión termina — el relay no inventa una migración de
    /// host que el juego no tiene.
    fn remove_peer(
        &mut self,
        sid: SessionId,
        pid: PeerId,
        reason: DisconnectReason,
        _now: Instant,
    ) -> Vec<Action> {
        if pid == HOST_PEER_ID {
            return self.close_session(sid, CloseReason::HostLeft);
        }

        let Some(session) = self.sessions.get_mut(&sid) else {
            return Vec::new();
        };
        let Some(gone) = session.peers.remove(&pid) else {
            return Vec::new();
        };
        self.by_addr.remove(&gone.addr);

        let host_addr = session.host_addr();
        info!(
            "RELAY event=peer_disconnected session={sid} peer={pid} reason={}",
            reason.name()
        );

        let Some(host) = host_addr else {
            return Vec::new();
        };
        vec![Action::Send {
            to: host,
            frame: RelayFrame::to_peer(
                sid,
                RELAY_ENDPOINT,
                HOST_PEER_ID,
                RelayMessage::PeerDisconnected { peer: pid, reason },
            ),
        }]
    }

    /// Devuelve `Some(acción de rechazo)` cuando esa IP ha pedido demasiado en el último segundo.
    fn rate_limit(
        &mut self,
        from: SocketAddr,
        session: SessionId,
        now: Instant,
    ) -> Option<Vec<Action>> {
        let ip = from.ip();
        let limit = self.limits.handshakes_per_second_per_ip;
        let bucket = self.rate.entry(ip).or_insert(RateBucket {
            window_start: now,
            count: 0,
        });

        if now.duration_since(bucket.window_start) >= Duration::from_secs(1) {
            bucket.window_start = now;
            bucket.count = 0;
        }

        bucket.count += 1;
        if bucket.count <= limit {
            return None;
        }

        self.stats.rate_limited += 1;
        // Se avisa UNA vez por ventana, en el datagrama que cruza el umbral. Registrar los mil
        // siguientes convertiría un intento de inundación en una inundación del disco.
        if bucket.count == limit + 1 {
            warn!(
                "RELAY event=rate_limited ip={ip} limit_per_second={limit} — se descarta el resto \
                 de esta ventana"
            );
        }
        // Por debajo del umbral se contesta; por encima, SILENCIO. Responder «te he limitado» a
        // cada datagrama de una inundación es participar en ella.
        let _ = session;
        Some(Vec::new())
    }

    fn auth(session: SessionId, to: SocketAddr, status: AuthStatus) -> Action {
        Action::Send {
            to,
            frame: RelayFrame::to_peer(session, RELAY_ENDPOINT, 0, RelayMessage::Auth { status }),
        }
    }

    fn peer_ready(session: SessionId, to: SocketAddr, assigned: PeerId) -> Action {
        Action::Send {
            to,
            frame: RelayFrame::to_peer(
                session,
                RELAY_ENDPOINT,
                assigned,
                RelayMessage::PeerReady {
                    assigned_peer: assigned,
                    host_peer: HOST_PEER_ID,
                },
            ),
        }
    }
}

#[cfg(test)]
#[path = "session_tests.rs"]
mod tests;
