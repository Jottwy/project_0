//! El sobre del relay y sus mensajes — ADR-117 D4.
//!
//! **Es un protocolo SUYO, y ésa es la decisión.** No se reutiliza ni un `PacketPayload` del juego
//! para gobernar el relay. Si se reutilizara, cualquier cambio del wire de gameplay arrastraría al
//! relay y al revés, y el relay dejaría de poder ser opaco al payload — que es justo lo que lo
//! mantiene fuera de la autoridad (ADR-117 D2).
//!
//! ## El sobre, 16 bytes fijos
//!
//! ```text
//! offset  bytes  campo
//!  0..2     2    magic     b"BR"
//!  2..3     1    version   RELAY_PROTOCOL_VERSION
//!  3..4     1    msg_type  MessageType
//!  4..12    8    session_id (u64 big-endian)
//! 12..14    2    src  (u16 BE) — quién lo manda
//! 14..16    2    dst  (u16 BE) — a quién va (0 = el relay mismo)
//! 16..      n    cuerpo, según msg_type
//! ```
//!
//! Big-endian en todo, como el `PacketHeader` del juego: es el orden de red, y tener dos criterios
//! distintos en el mismo proceso es una fuente de bugs que no compensa ni un ciclo de CPU.
//!
//! `src` y `dst` van en la cabecera FIJA y no en el cuerpo de `Data` a propósito: el camino
//! caliente del relay es «leer 16 bytes, decidir destino, reenviar el búfer tal cual». Con el
//! destino dentro del cuerpo habría que decodificar el payload entero para saber a dónde va, y el
//! relay volvería a tener que entender lo que transporta.
//!
//! ## El tamaño, y su relación con ADR-113
//!
//! El payload de gameplay sigue topado en **1200 B** (`SAFE_DATAGRAM_BYTES`, invariante de
//! ADR-113, aplicada por el emisor ANTES de envolver). El datagrama que sale al cable mide hasta
//! **1216 B** = 1200 + 16 de sobre. Ver la enmienda 3 de ADR-113: sigue por debajo de la MTU
//! mínima de IPv6 (1280) y muy lejos de los 1472 de Ethernet, así que no se fragmenta nada.
//!
//! **El relay no fragmenta ni reensambla jamás.** Un payload que llegue por encima del techo se
//! descarta y se cuenta. Fragmentar aquí reintroduciría el modo de fallo exacto que ADR-113 cerró.

use std::fmt;

/// Marca de protocolo. Dos bytes que separan un datagrama de relay de cualquier otra cosa que
/// llegue al puerto — un escáner, un paquete de gameplay mal dirigido, ruido.
pub const RELAY_MAGIC: [u8; 2] = *b"BR";

/// Versión del protocolo de relay. **No es la versión de wire del juego**, que viaja aparte en
/// `SessionCreate`/`SessionJoin`: el relay puede evolucionar sin tocar el juego y al revés.
pub const RELAY_PROTOCOL_VERSION: u8 = 1;

/// El sobre, en bytes. Fijo por diseño: el camino caliente lee esto y no mira más.
pub const ENVELOPE_BYTES: usize = 16;

/// Techo del payload de gameplay. Espejo de `network::protocol::SAFE_DATAGRAM_BYTES` en el
/// backend (ADR-113). Está DUPLICADO a propósito y no importado: este crate no depende del
/// servidor —es al revés—, y un relay que arrastrara el árbol del juego para leer una constante
/// dejaría de ser un binario aparte. El test `el_techo_del_payload_es_el_de_adr_113` lo fija.
pub const MAX_GAMEPLAY_PAYLOAD_BYTES: usize = 1200;

/// Lo máximo que puede medir un datagrama de relay en el cable. ADR-113 enmienda 3.
pub const MAX_RELAY_DATAGRAM_BYTES: usize = ENVELOPE_BYTES + MAX_GAMEPLAY_PAYLOAD_BYTES;

/// Bytes del token de sesión (ADR-117 D9).
pub const TOKEN_BYTES: usize = 16;

/// Identidad de un peer **dentro del relay**, y no la del juego.
///
/// Son dos numeraciones distintas a propósito, porque no pueden ser la misma: el `peer_id` del
/// juego lo asigna el HOST en su handshake (`allocate_peer_id`), y ese handshake sólo puede viajar
/// cuando el transporte ya funciona. Si el relay necesitara el id de juego para admitir a alguien,
/// haría falta el transporte para tener el id y el id para tener transporte.
///
/// Así que el relay reparte los suyos —1 es siempre el host, 2 en adelante los joiners— y el id de
/// juego viaja DENTRO del payload, donde el relay ni lo mira. El backend traduce entre los dos en
/// un solo sitio (`network::transport`).
pub type PeerId = u16;

/// Identidad de una sesión de relay. La genera el host y viaja por la metadata del lobby.
pub type SessionId = u64;

/// `dst` reservado para «este mensaje es para el relay, no para otro peer».
pub const RELAY_ENDPOINT: PeerId = 0;

/// Qué mensaje es. Los nueve del encargo de R1, más `Connect` como saludo de descubrimiento.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum MessageType {
    /// Peer → relay. «¿Estás vivo y hablas mi versión?». Sin sesión todavía.
    Connect = 1,
    /// Relay → peer. La respuesta a `Connect` y **el único no** a `SessionCreate`/`SessionJoin`:
    /// el sí de esos dos es `PeerReady`, que además lleva datos. Un `Auth{Ok}` como respuesta a
    /// una creación obligaría a un segundo viaje para saber qué id se ha asignado.
    Auth = 2,
    /// Host → relay. Abre la sesión.
    SessionCreate = 3,
    /// Joiner → relay. Entra en una sesión ya abierta.
    SessionJoin = 4,
    /// Relay → peer. Admitido: aquí está tu id y el del host.
    PeerReady = 5,
    /// Ambos sentidos. Mantiene viva la asociación y el agujero del NAT.
    Heartbeat = 6,
    /// Ambos sentidos. El payload de gameplay, opaco.
    Data = 7,
    /// Relay → host. Ha entrado alguien.
    PeerJoined = 8,
    /// Relay → peers. Alguien se ha ido o ha caducado.
    PeerDisconnected = 9,
    /// Relay → peers. La sesión se acabó.
    SessionClosed = 10,
}

impl TryFrom<u8> for MessageType {
    type Error = DecodeError;

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            1 => Ok(MessageType::Connect),
            2 => Ok(MessageType::Auth),
            3 => Ok(MessageType::SessionCreate),
            4 => Ok(MessageType::SessionJoin),
            5 => Ok(MessageType::PeerReady),
            6 => Ok(MessageType::Heartbeat),
            7 => Ok(MessageType::Data),
            8 => Ok(MessageType::PeerJoined),
            9 => Ok(MessageType::PeerDisconnected),
            10 => Ok(MessageType::SessionClosed),
            other => Err(DecodeError::UnknownType(other)),
        }
    }
}

/// Por qué el relay dice que no. Viaja como `u8` y **se registra por nombre**: un rechazo mudo es
/// indistinguible de un datagrama perdido, y ésa es la diferencia entre diagnosticar en un minuto
/// y en una tarde.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum AuthStatus {
    /// Todo en orden. Como respuesta a `Connect` significa «relay vivo, versión compatible».
    Ok = 0,
    /// El token no coincide con el de la sesión. **No dice si la sesión existe**: distinguirlo
    /// convertiría el relay en un oráculo para enumerar sesiones ajenas.
    Denied = 1,
    /// La versión de wire del juego no es la de la sesión. Un joiner con otro build entraría a un
    /// mundo que no sabe leer, y el fallo aparecería mucho más tarde y sin relación aparente.
    WireVersionMismatch = 2,
    /// La versión del PROTOCOLO DE RELAY no es la nuestra.
    ProtocolVersionMismatch = 3,
    /// La sesión está llena (aforo de la partida).
    SessionFull = 4,
    /// El relay está lleno (aforo de la máquina).
    RelayFull = 5,
    /// Demasiadas peticiones desde esa dirección.
    RateLimited = 6,
    /// Un peer con ese id ya está en la sesión y sigue vivo.
    DuplicatePeer = 7,
}

impl TryFrom<u8> for AuthStatus {
    type Error = DecodeError;

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(AuthStatus::Ok),
            1 => Ok(AuthStatus::Denied),
            2 => Ok(AuthStatus::WireVersionMismatch),
            3 => Ok(AuthStatus::ProtocolVersionMismatch),
            4 => Ok(AuthStatus::SessionFull),
            5 => Ok(AuthStatus::RelayFull),
            6 => Ok(AuthStatus::RateLimited),
            7 => Ok(AuthStatus::DuplicatePeer),
            other => Err(DecodeError::BadEnum("AuthStatus", other)),
        }
    }
}

impl AuthStatus {
    /// El nombre LITERAL para el log, en mayúsculas, como los códigos de ADR-112: se busca con
    /// grep copiando lo que dice la documentación.
    pub fn name(self) -> &'static str {
        match self {
            AuthStatus::Ok => "OK",
            AuthStatus::Denied => "DENIED",
            AuthStatus::WireVersionMismatch => "WIRE_VERSION_MISMATCH",
            AuthStatus::ProtocolVersionMismatch => "PROTOCOL_VERSION_MISMATCH",
            AuthStatus::SessionFull => "SESSION_FULL",
            AuthStatus::RelayFull => "RELAY_FULL",
            AuthStatus::RateLimited => "RATE_LIMITED",
            AuthStatus::DuplicatePeer => "DUPLICATE_PEER",
        }
    }
}

/// Por qué un peer deja de estar.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum DisconnectReason {
    /// Dejó de latir.
    Timeout = 0,
    /// Se fue por su propio pie.
    Left = 1,
    /// La sesión entera se cerró.
    SessionClosed = 2,
}

impl TryFrom<u8> for DisconnectReason {
    type Error = DecodeError;

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(DisconnectReason::Timeout),
            1 => Ok(DisconnectReason::Left),
            2 => Ok(DisconnectReason::SessionClosed),
            other => Err(DecodeError::BadEnum("DisconnectReason", other)),
        }
    }
}

impl DisconnectReason {
    pub fn name(self) -> &'static str {
        match self {
            DisconnectReason::Timeout => "TIMEOUT",
            DisconnectReason::Left => "LEFT",
            DisconnectReason::SessionClosed => "SESSION_CLOSED",
        }
    }
}

/// Por qué muere una sesión.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum CloseReason {
    /// El host se fue. **Es el caso normal**: sin host no hay autoridad, y ADR-056 ya dice que la
    /// sesión termina — el relay no inventa una migración de host que el juego no tiene.
    HostLeft = 0,
    /// Nadie dio señales durante demasiado tiempo.
    Idle = 1,
    /// El relay se está apagando.
    RelayShutdown = 2,
}

impl TryFrom<u8> for CloseReason {
    type Error = DecodeError;

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(CloseReason::HostLeft),
            1 => Ok(CloseReason::Idle),
            2 => Ok(CloseReason::RelayShutdown),
            other => Err(DecodeError::BadEnum("CloseReason", other)),
        }
    }
}

impl CloseReason {
    pub fn name(self) -> &'static str {
        match self {
            CloseReason::HostLeft => "HOST_LEFT",
            CloseReason::Idle => "IDLE",
            CloseReason::RelayShutdown => "RELAY_SHUTDOWN",
        }
    }
}

/// El secreto de sesión (ADR-117 D9).
///
/// **Su `Debug` NO enseña el valor, y eso no es cosmética.** El encargo dice «no registrar
/// secretos», y la forma en que un secreto acaba en un log no es que alguien escriba
/// `info!("{token}")`: es que alguien imprima con `{:?}` una estructura que lo contiene tres
/// niveles más abajo. Aquí no se puede: el tipo se niega. El test
/// `el_token_no_se_puede_imprimir_por_accidente` lo fija.
#[derive(Clone, Copy)]
pub struct SessionToken([u8; TOKEN_BYTES]);

impl SessionToken {
    pub fn new(bytes: [u8; TOKEN_BYTES]) -> Self {
        Self(bytes)
    }

    /// Los bytes crudos. Existe para codificar y para las pruebas; **no para registrar**.
    pub fn as_bytes(&self) -> &[u8; TOKEN_BYTES] {
        &self.0
    }

    /// Parsea 32 caracteres hexadecimales. Es el formato en el que viaja por la metadata del
    /// lobby de Steam, que sólo guarda cadenas.
    pub fn from_hex(hex: &str) -> Option<Self> {
        let hex = hex.trim();
        if hex.len() != TOKEN_BYTES * 2 {
            return None;
        }
        let mut out = [0u8; TOKEN_BYTES];
        for (i, slot) in out.iter_mut().enumerate() {
            *slot = u8::from_str_radix(hex.get(i * 2..i * 2 + 2)?, 16).ok()?;
        }
        Some(Self(out))
    }

    /// A hexadecimal. **Sólo para dárselo a quien tiene que publicarlo**, nunca para un log.
    pub fn to_hex(self) -> String {
        let mut s = String::with_capacity(TOKEN_BYTES * 2);
        for byte in self.0 {
            s.push_str(&format!("{byte:02x}"));
        }
        s
    }
}

/// Comparación en **tiempo constante**. Un `==` normal sale en cuanto encuentra el primer byte
/// distinto, y eso le dice a quien mide el tiempo cuántos bytes acertó — se adivina un token byte
/// a byte en 16×256 intentos en vez de 2^128. El coste de no tener este defecto son 16
/// operaciones OR.
impl PartialEq for SessionToken {
    fn eq(&self, other: &Self) -> bool {
        let mut diff = 0u8;
        for i in 0..TOKEN_BYTES {
            diff |= self.0[i] ^ other.0[i];
        }
        diff == 0
    }
}

impl Eq for SessionToken {}

impl fmt::Debug for SessionToken {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "SessionToken(<oculto>)")
    }
}

/// Lo que lleva un datagrama de relay, ya interpretado.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RelayMessage {
    Connect,
    Auth {
        status: AuthStatus,
    },
    SessionCreate {
        token: SessionToken,
        wire_version: u16,
    },
    SessionJoin {
        token: SessionToken,
        wire_version: u16,
    },
    PeerReady {
        assigned_peer: PeerId,
        host_peer: PeerId,
    },
    Heartbeat,
    /// El payload de gameplay. Opaco: el relay no lo mira ni lo puede mirar.
    Data {
        payload: Vec<u8>,
    },
    PeerJoined {
        peer: PeerId,
    },
    PeerDisconnected {
        peer: PeerId,
        reason: DisconnectReason,
    },
    SessionClosed {
        reason: CloseReason,
    },
}

impl RelayMessage {
    pub fn message_type(&self) -> MessageType {
        match self {
            RelayMessage::Connect => MessageType::Connect,
            RelayMessage::Auth { .. } => MessageType::Auth,
            RelayMessage::SessionCreate { .. } => MessageType::SessionCreate,
            RelayMessage::SessionJoin { .. } => MessageType::SessionJoin,
            RelayMessage::PeerReady { .. } => MessageType::PeerReady,
            RelayMessage::Heartbeat => MessageType::Heartbeat,
            RelayMessage::Data { .. } => MessageType::Data,
            RelayMessage::PeerJoined { .. } => MessageType::PeerJoined,
            RelayMessage::PeerDisconnected { .. } => MessageType::PeerDisconnected,
            RelayMessage::SessionClosed { .. } => MessageType::SessionClosed,
        }
    }
}

/// Un datagrama de relay entero: sobre + mensaje.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RelayFrame {
    pub session_id: SessionId,
    pub src: PeerId,
    pub dst: PeerId,
    pub message: RelayMessage,
}

impl RelayFrame {
    /// Un mensaje dirigido al relay mismo.
    pub fn to_relay(session_id: SessionId, src: PeerId, message: RelayMessage) -> Self {
        Self {
            session_id,
            src,
            dst: RELAY_ENDPOINT,
            message,
        }
    }

    /// Un mensaje para otro peer.
    pub fn to_peer(session_id: SessionId, src: PeerId, dst: PeerId, message: RelayMessage) -> Self {
        Self {
            session_id,
            src,
            dst,
            message,
        }
    }

    /// Serializa. Falla **sólo** por un payload por encima del techo, que es un defecto del
    /// emisor y no una condición de red — la misma doctrina que `send_datagram` en el backend.
    pub fn encode(&self) -> Result<Vec<u8>, EncodeError> {
        if let RelayMessage::Data { payload } = &self.message {
            if payload.len() > MAX_GAMEPLAY_PAYLOAD_BYTES {
                return Err(EncodeError::PayloadTooLarge {
                    bytes: payload.len(),
                    budget: MAX_GAMEPLAY_PAYLOAD_BYTES,
                });
            }
        }

        let body_len = match &self.message {
            RelayMessage::Connect | RelayMessage::Heartbeat => 0,
            RelayMessage::Auth { .. } | RelayMessage::SessionClosed { .. } => 1,
            RelayMessage::PeerJoined { .. } => 2,
            RelayMessage::PeerDisconnected { .. } => 3,
            RelayMessage::PeerReady { .. } => 4,
            RelayMessage::SessionCreate { .. } | RelayMessage::SessionJoin { .. } => {
                TOKEN_BYTES + 2
            }
            RelayMessage::Data { payload } => payload.len(),
        };

        let mut buf = Vec::with_capacity(ENVELOPE_BYTES + body_len);
        buf.extend_from_slice(&RELAY_MAGIC);
        buf.push(RELAY_PROTOCOL_VERSION);
        buf.push(self.message.message_type() as u8);
        buf.extend_from_slice(&self.session_id.to_be_bytes());
        buf.extend_from_slice(&self.src.to_be_bytes());
        buf.extend_from_slice(&self.dst.to_be_bytes());

        match &self.message {
            RelayMessage::Connect | RelayMessage::Heartbeat => {}
            RelayMessage::Auth { status } => buf.push(*status as u8),
            RelayMessage::SessionCreate {
                token,
                wire_version,
            }
            | RelayMessage::SessionJoin {
                token,
                wire_version,
            } => {
                buf.extend_from_slice(token.as_bytes());
                buf.extend_from_slice(&wire_version.to_be_bytes());
            }
            RelayMessage::PeerReady {
                assigned_peer,
                host_peer,
            } => {
                buf.extend_from_slice(&assigned_peer.to_be_bytes());
                buf.extend_from_slice(&host_peer.to_be_bytes());
            }
            RelayMessage::Data { payload } => buf.extend_from_slice(payload),
            RelayMessage::PeerJoined { peer } => buf.extend_from_slice(&peer.to_be_bytes()),
            RelayMessage::PeerDisconnected { peer, reason } => {
                buf.extend_from_slice(&peer.to_be_bytes());
                buf.push(*reason as u8);
            }
            RelayMessage::SessionClosed { reason } => buf.push(*reason as u8),
        }

        debug_assert_eq!(buf.len(), ENVELOPE_BYTES + body_len);
        Ok(buf)
    }

    /// Interpreta un datagrama entero. **Estricta a propósito**: cada cuerpo tiene una longitud
    /// exacta y una longitud que no cuadra es un rechazo, no un «leo lo que pueda». Esto lee bytes
    /// que vienen de internet, de cualquiera, sin autenticar todavía.
    pub fn decode(buf: &[u8]) -> Result<Self, DecodeError> {
        let header = Envelope::peek(buf)?;
        let body = &buf[ENVELOPE_BYTES..];

        let message = match header.msg_type {
            MessageType::Connect => {
                Self::expect_len(body, 0, "Connect")?;
                RelayMessage::Connect
            }
            MessageType::Heartbeat => {
                Self::expect_len(body, 0, "Heartbeat")?;
                RelayMessage::Heartbeat
            }
            MessageType::Auth => {
                Self::expect_len(body, 1, "Auth")?;
                RelayMessage::Auth {
                    status: AuthStatus::try_from(body[0])?,
                }
            }
            MessageType::SessionCreate | MessageType::SessionJoin => {
                Self::expect_len(body, TOKEN_BYTES + 2, "SessionCreate/SessionJoin")?;
                let mut token = [0u8; TOKEN_BYTES];
                token.copy_from_slice(&body[..TOKEN_BYTES]);
                let wire_version = u16::from_be_bytes([body[TOKEN_BYTES], body[TOKEN_BYTES + 1]]);
                let token = SessionToken::new(token);
                if header.msg_type == MessageType::SessionCreate {
                    RelayMessage::SessionCreate {
                        token,
                        wire_version,
                    }
                } else {
                    RelayMessage::SessionJoin {
                        token,
                        wire_version,
                    }
                }
            }
            MessageType::PeerReady => {
                Self::expect_len(body, 4, "PeerReady")?;
                RelayMessage::PeerReady {
                    assigned_peer: u16::from_be_bytes([body[0], body[1]]),
                    host_peer: u16::from_be_bytes([body[2], body[3]]),
                }
            }
            MessageType::Data => {
                if body.len() > MAX_GAMEPLAY_PAYLOAD_BYTES {
                    return Err(DecodeError::PayloadTooLarge {
                        bytes: body.len(),
                        budget: MAX_GAMEPLAY_PAYLOAD_BYTES,
                    });
                }
                RelayMessage::Data {
                    payload: body.to_vec(),
                }
            }
            MessageType::PeerJoined => {
                Self::expect_len(body, 2, "PeerJoined")?;
                RelayMessage::PeerJoined {
                    peer: u16::from_be_bytes([body[0], body[1]]),
                }
            }
            MessageType::PeerDisconnected => {
                Self::expect_len(body, 3, "PeerDisconnected")?;
                RelayMessage::PeerDisconnected {
                    peer: u16::from_be_bytes([body[0], body[1]]),
                    reason: DisconnectReason::try_from(body[2])?,
                }
            }
            MessageType::SessionClosed => {
                Self::expect_len(body, 1, "SessionClosed")?;
                RelayMessage::SessionClosed {
                    reason: CloseReason::try_from(body[0])?,
                }
            }
        };

        Ok(Self {
            session_id: header.session_id,
            src: header.src,
            dst: header.dst,
            message,
        })
    }

    fn expect_len(body: &[u8], want: usize, what: &'static str) -> Result<(), DecodeError> {
        if body.len() == want {
            Ok(())
        } else {
            Err(DecodeError::BadBodyLength {
                what,
                got: body.len(),
                want,
            })
        }
    }
}

/// El sobre solo, sin tocar el cuerpo.
///
/// Existe por el camino caliente del relay: reenviar un `Data` es leer estos 16 bytes, decidir el
/// destino y **mandar el búfer original tal cual**. Decodificar el `RelayFrame` entero copiaría
/// hasta 1200 bytes por datagrama sin ninguna necesidad — el relay no mira el payload, así que
/// tampoco tiene por qué copiarlo.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Envelope {
    pub msg_type: MessageType,
    pub session_id: SessionId,
    pub src: PeerId,
    pub dst: PeerId,
}

impl Envelope {
    /// Lee el sobre sin copiar el cuerpo. Valida magia, versión y tipo; nada más.
    pub fn peek(buf: &[u8]) -> Result<Self, DecodeError> {
        if buf.len() < ENVELOPE_BYTES {
            return Err(DecodeError::TooShort {
                got: buf.len(),
                want: ENVELOPE_BYTES,
            });
        }
        if buf[0..2] != RELAY_MAGIC {
            return Err(DecodeError::BadMagic([buf[0], buf[1]]));
        }
        if buf[2] != RELAY_PROTOCOL_VERSION {
            return Err(DecodeError::BadVersion(buf[2]));
        }
        if buf.len() > MAX_RELAY_DATAGRAM_BYTES {
            return Err(DecodeError::PayloadTooLarge {
                bytes: buf.len() - ENVELOPE_BYTES,
                budget: MAX_GAMEPLAY_PAYLOAD_BYTES,
            });
        }

        let mut session_bytes = [0u8; 8];
        session_bytes.copy_from_slice(&buf[4..12]);

        Ok(Self {
            msg_type: MessageType::try_from(buf[3])?,
            session_id: u64::from_be_bytes(session_bytes),
            src: u16::from_be_bytes([buf[12], buf[13]]),
            dst: u16::from_be_bytes([buf[14], buf[15]]),
        })
    }
}

/// Reescribe el `src` de un búfer ya formado, in situ.
///
/// **El relay NUNCA se fía del `src` que declara el cliente.** Un peer podría poner ahí el id de
/// otro y suplantarlo ante el host; el relay conoce la identidad real por la dirección de origen
/// del datagrama, así que la estampa él antes de reenviar. Dos bytes, sin copiar el payload.
pub fn rewrite_src(buf: &mut [u8], src: PeerId) -> Result<(), DecodeError> {
    if buf.len() < ENVELOPE_BYTES {
        return Err(DecodeError::TooShort {
            got: buf.len(),
            want: ENVELOPE_BYTES,
        });
    }
    buf[12..14].copy_from_slice(&src.to_be_bytes());
    Ok(())
}

/// Reescribe el `dst`. Mismo motivo que [`rewrite_src`]: el receptor tiene que ver a quién iba
/// dirigido según el relay, no según quien lo mandó.
pub fn rewrite_dst(buf: &mut [u8], dst: PeerId) -> Result<(), DecodeError> {
    if buf.len() < ENVELOPE_BYTES {
        return Err(DecodeError::TooShort {
            got: buf.len(),
            want: ENVELOPE_BYTES,
        });
    }
    buf[14..16].copy_from_slice(&dst.to_be_bytes());
    Ok(())
}

/// Por qué no se pudo leer un datagrama. Cada variante manda a mirar un sitio distinto.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DecodeError {
    /// Ni siquiera hay sobre. Ruido, un escáner, o un datagrama truncado.
    TooShort { got: usize, want: usize },
    /// No es nuestro. Lo normal en un puerto público: alguien probando cosas.
    BadMagic([u8; 2]),
    /// Es nuestro pero de otra versión del protocolo de relay.
    BadVersion(u8),
    /// Tipo de mensaje que no existe.
    UnknownType(u8),
    /// El cuerpo no mide lo que ese tipo de mensaje exige.
    BadBodyLength {
        what: &'static str,
        got: usize,
        want: usize,
    },
    /// Un valor de enumerado fuera de rango.
    BadEnum(&'static str, u8),
    /// Por encima del techo de ADR-113.
    PayloadTooLarge { bytes: usize, budget: usize },
}

impl fmt::Display for DecodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            DecodeError::TooShort { got, want } => {
                write!(f, "datagrama de {got} B: no llega ni al sobre de {want} B")
            }
            DecodeError::BadMagic(got) => {
                write!(f, "magia {got:02X?}, se esperaba {RELAY_MAGIC:02X?}")
            }
            DecodeError::BadVersion(got) => write!(
                f,
                "versión de protocolo de relay {got}, esta build habla la {RELAY_PROTOCOL_VERSION}"
            ),
            DecodeError::UnknownType(got) => write!(f, "tipo de mensaje desconocido {got}"),
            DecodeError::BadBodyLength { what, got, want } => {
                write!(f, "{what}: cuerpo de {got} B, se esperaban {want} B")
            }
            DecodeError::BadEnum(name, got) => write!(f, "{name}: valor {got} fuera de rango"),
            DecodeError::PayloadTooLarge { bytes, budget } => {
                write!(f, "payload de {bytes} B por encima del techo de {budget} B")
            }
        }
    }
}

impl std::error::Error for DecodeError {}

/// Lo único que puede fallar al serializar.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum EncodeError {
    PayloadTooLarge { bytes: usize, budget: usize },
}

impl fmt::Display for EncodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            EncodeError::PayloadTooLarge { bytes, budget } => write!(
                f,
                "payload de {bytes} B por encima del techo de {budget} B (ADR-113): el emisor de \
                 este mensaje no está acotado"
            ),
        }
    }
}

impl std::error::Error for EncodeError {}

#[cfg(test)]
mod tests {
    use super::*;

    fn token() -> SessionToken {
        SessionToken::new([
            0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A,
            0x0B, 0x0C,
        ])
    }

    fn roundtrip(frame: RelayFrame) {
        let bytes = frame.encode().expect("codifica");
        let back = RelayFrame::decode(&bytes).expect("decodifica");
        assert_eq!(frame, back, "el ida y vuelta tiene que dar lo mismo");
    }

    #[test]
    fn el_sobre_mide_dieciseis_bytes_exactos() {
        // Si esto cambia, cambia el tamaño máximo en el cable y hay que volver a ADR-113 enm. 3.
        let bytes = RelayFrame::to_relay(1, 1, RelayMessage::Heartbeat)
            .encode()
            .unwrap();
        assert_eq!(bytes.len(), ENVELOPE_BYTES);
        assert_eq!(ENVELOPE_BYTES, 16);
    }

    #[test]
    fn el_techo_del_payload_es_el_de_adr_113() {
        // Espejo de `network::protocol::SAFE_DATAGRAM_BYTES`. Duplicado a propósito (ver la
        // constante); este test es lo que impide que las dos copias se separen sin que nadie lo
        // note.
        assert_eq!(MAX_GAMEPLAY_PAYLOAD_BYTES, 1200);
        assert_eq!(MAX_RELAY_DATAGRAM_BYTES, 1216);
    }

    #[test]
    fn un_payload_en_el_techo_cabe_y_da_1216_en_el_cable() {
        let frame = RelayFrame::to_peer(
            7,
            2,
            1,
            RelayMessage::Data {
                payload: vec![0xAB; MAX_GAMEPLAY_PAYLOAD_BYTES],
            },
        );
        let bytes = frame
            .encode()
            .expect("1200 B es exactamente el techo, cabe");
        assert_eq!(bytes.len(), MAX_RELAY_DATAGRAM_BYTES);
        roundtrip(frame);
    }

    #[test]
    fn un_payload_por_encima_del_techo_no_se_codifica() {
        let frame = RelayFrame::to_peer(
            7,
            2,
            1,
            RelayMessage::Data {
                payload: vec![0; MAX_GAMEPLAY_PAYLOAD_BYTES + 1],
            },
        );
        assert_eq!(
            frame.encode(),
            Err(EncodeError::PayloadTooLarge {
                bytes: 1201,
                budget: 1200
            })
        );
    }

    #[test]
    fn un_payload_por_encima_del_techo_tampoco_se_decodifica() {
        // El emisor podría no ser nuestro. Un relay que aceptara esto acabaría reenviando un
        // datagrama fragmentable, que es justo lo que ADR-113 cerró.
        let mut bytes = RelayFrame::to_peer(1, 2, 1, RelayMessage::Data { payload: vec![] })
            .encode()
            .unwrap();
        bytes.extend_from_slice(&vec![0u8; MAX_GAMEPLAY_PAYLOAD_BYTES + 1]);
        assert!(matches!(
            RelayFrame::decode(&bytes),
            Err(DecodeError::PayloadTooLarge { .. })
        ));
    }

    #[test]
    fn ida_y_vuelta_de_todos_los_mensajes() {
        roundtrip(RelayFrame::to_relay(0, 0, RelayMessage::Connect));
        roundtrip(RelayFrame::to_peer(
            9,
            0,
            0,
            RelayMessage::Auth {
                status: AuthStatus::Denied,
            },
        ));
        roundtrip(RelayFrame::to_relay(
            0x0123_4567_89AB_CDEF,
            1,
            RelayMessage::SessionCreate {
                token: token(),
                wire_version: 55,
            },
        ));
        roundtrip(RelayFrame::to_relay(
            42,
            0,
            RelayMessage::SessionJoin {
                token: token(),
                wire_version: 55,
            },
        ));
        roundtrip(RelayFrame::to_peer(
            42,
            RELAY_ENDPOINT,
            7,
            RelayMessage::PeerReady {
                assigned_peer: 7,
                host_peer: 1,
            },
        ));
        roundtrip(RelayFrame::to_relay(42, 7, RelayMessage::Heartbeat));
        roundtrip(RelayFrame::to_peer(
            42,
            7,
            1,
            RelayMessage::Data {
                payload: vec![1, 2, 3, 4, 5],
            },
        ));
        roundtrip(RelayFrame::to_peer(
            42,
            RELAY_ENDPOINT,
            1,
            RelayMessage::PeerJoined { peer: 7 },
        ));
        roundtrip(RelayFrame::to_peer(
            42,
            RELAY_ENDPOINT,
            1,
            RelayMessage::PeerDisconnected {
                peer: 7,
                reason: DisconnectReason::Timeout,
            },
        ));
        roundtrip(RelayFrame::to_peer(
            42,
            RELAY_ENDPOINT,
            7,
            RelayMessage::SessionClosed {
                reason: CloseReason::HostLeft,
            },
        ));
    }

    #[test]
    fn un_data_vacio_es_legitimo() {
        // No es un caso de laboratorio: un keepalive de payload cero es la forma barata de
        // mantener abierto el agujero del NAT por la ruta de datos.
        roundtrip(RelayFrame::to_peer(
            1,
            2,
            1,
            RelayMessage::Data { payload: vec![] },
        ));
    }

    #[test]
    fn el_sobre_se_lee_sin_copiar_el_cuerpo() {
        let frame = RelayFrame::to_peer(
            0xAABB_CCDD_EEFF_0011,
            300,
            1,
            RelayMessage::Data {
                payload: vec![9; 500],
            },
        );
        let bytes = frame.encode().unwrap();
        let env = Envelope::peek(&bytes).unwrap();
        assert_eq!(env.msg_type, MessageType::Data);
        assert_eq!(env.session_id, 0xAABB_CCDD_EEFF_0011);
        assert_eq!(env.src, 300);
        assert_eq!(env.dst, 1);
    }

    #[test]
    fn se_rechaza_lo_que_no_es_nuestro() {
        assert!(matches!(
            Envelope::peek(&[]),
            Err(DecodeError::TooShort { got: 0, .. })
        ));
        assert!(matches!(
            Envelope::peek(&[0u8; ENVELOPE_BYTES - 1]),
            Err(DecodeError::TooShort { .. })
        ));

        let mut bytes = RelayFrame::to_relay(1, 1, RelayMessage::Heartbeat)
            .encode()
            .unwrap();
        bytes[0] = b'X';
        assert!(matches!(
            RelayFrame::decode(&bytes),
            Err(DecodeError::BadMagic(_))
        ));

        let mut bytes = RelayFrame::to_relay(1, 1, RelayMessage::Heartbeat)
            .encode()
            .unwrap();
        bytes[2] = 99;
        assert_eq!(RelayFrame::decode(&bytes), Err(DecodeError::BadVersion(99)));

        let mut bytes = RelayFrame::to_relay(1, 1, RelayMessage::Heartbeat)
            .encode()
            .unwrap();
        bytes[3] = 200;
        assert_eq!(
            RelayFrame::decode(&bytes),
            Err(DecodeError::UnknownType(200))
        );
    }

    #[test]
    fn un_cuerpo_que_no_mide_lo_suyo_es_un_rechazo_no_una_lectura_parcial() {
        // Estricto a propósito: esto lee bytes de cualquiera. Un `SessionCreate` recortado con un
        // token de 8 bytes no puede leerse "hasta donde llegue".
        let mut bytes = RelayFrame::to_relay(
            1,
            1,
            RelayMessage::SessionCreate {
                token: token(),
                wire_version: 55,
            },
        )
        .encode()
        .unwrap();
        bytes.truncate(ENVELOPE_BYTES + 8);
        assert!(matches!(
            RelayFrame::decode(&bytes),
            Err(DecodeError::BadBodyLength {
                got: 8,
                want: 18,
                ..
            })
        ));

        // Y uno con bytes de más tampoco.
        let mut bytes = RelayFrame::to_relay(1, 1, RelayMessage::Heartbeat)
            .encode()
            .unwrap();
        bytes.push(0);
        assert!(matches!(
            RelayFrame::decode(&bytes),
            Err(DecodeError::BadBodyLength {
                got: 1,
                want: 0,
                ..
            })
        ));
    }

    #[test]
    fn un_enumerado_fuera_de_rango_no_se_convierte_en_un_valor_valido() {
        let mut bytes = RelayFrame::to_peer(
            1,
            0,
            1,
            RelayMessage::Auth {
                status: AuthStatus::Ok,
            },
        )
        .encode()
        .unwrap();
        bytes[ENVELOPE_BYTES] = 250;
        assert_eq!(
            RelayFrame::decode(&bytes),
            Err(DecodeError::BadEnum("AuthStatus", 250))
        );
    }

    #[test]
    fn el_relay_puede_reescribir_origen_y_destino_sin_tocar_el_payload() {
        // Es el camino caliente: 2 bytes, sin decodificar ni copiar los 1200 de gameplay.
        let payload: Vec<u8> = (0..200u16).map(|i| i as u8).collect();
        let mut bytes = RelayFrame::to_peer(
            5,
            999, // el `src` MENTIDO por el cliente
            1,
            RelayMessage::Data {
                payload: payload.clone(),
            },
        )
        .encode()
        .unwrap();

        rewrite_src(&mut bytes, 7).unwrap();
        rewrite_dst(&mut bytes, 1).unwrap();

        let back = RelayFrame::decode(&bytes).unwrap();
        assert_eq!(back.src, 7, "el relay estampa la identidad real");
        assert_eq!(back.dst, 1);
        assert_eq!(
            back.message,
            RelayMessage::Data { payload },
            "el payload no se toca"
        );
    }

    #[test]
    fn reescribir_un_buffer_sin_sobre_falla_en_vez_de_corromper() {
        let mut corto = [0u8; 4];
        assert!(rewrite_src(&mut corto, 1).is_err());
        assert!(rewrite_dst(&mut corto, 1).is_err());
    }

    #[test]
    fn el_token_no_se_puede_imprimir_por_accidente() {
        // ADR-117 D9: «el token no se escribe en ningún log». La forma en que un secreto acaba en
        // un log no es un `info!("{token}")` — es un `{:?}` sobre algo que lo contiene tres
        // niveles más abajo. Aquí el tipo se niega.
        let t = token();
        let debug = format!("{t:?}");
        assert_eq!(debug, "SessionToken(<oculto>)");
        assert!(!debug.contains("de"), "no puede asomar ni un byte en hex");
        assert!(!debug.contains("222"), "ni en decimal");

        // Y tampoco desde dentro del mensaje que lo transporta.
        let frame = RelayFrame::to_relay(
            1,
            1,
            RelayMessage::SessionCreate {
                token: t,
                wire_version: 55,
            },
        );
        assert!(!format!("{frame:?}").contains("deadbeef"));
        assert!(format!("{frame:?}").contains("<oculto>"));
    }

    #[test]
    fn el_token_va_y_vuelve_por_hexadecimal() {
        // Es como viaja por la metadata del lobby de Steam, que sólo guarda cadenas.
        let t = token();
        let hex = t.to_hex();
        assert_eq!(hex, "deadbeef0102030405060708090a0b0c");
        assert_eq!(SessionToken::from_hex(&hex), Some(t));
        assert_eq!(
            SessionToken::from_hex("  deadbeef0102030405060708090a0b0c  "),
            Some(t)
        );
    }

    #[test]
    fn un_token_mal_formado_no_se_acepta_a_medias() {
        assert_eq!(SessionToken::from_hex(""), None);
        assert_eq!(SessionToken::from_hex("deadbeef"), None, "demasiado corto");
        assert_eq!(
            SessionToken::from_hex("deadbeef0102030405060708090a0b0c00"),
            None,
            "demasiado largo"
        );
        assert_eq!(
            SessionToken::from_hex("zzadbeef0102030405060708090a0b0c"),
            None,
            "no es hexadecimal"
        );
    }

    #[test]
    fn los_tokens_se_comparan_enteros() {
        let a = token();
        let mut otros = *a.as_bytes();
        otros[TOKEN_BYTES - 1] ^= 0x01; // difiere SOLO en el último byte
        let b = SessionToken::new(otros);
        assert_ne!(
            a, b,
            "un byte distinto al final basta para que no sea el mismo"
        );
        assert_eq!(a, token());
    }

    #[test]
    fn el_tipo_de_mensaje_sobrevive_al_numero() {
        // El `u8` del cable y el enumerado no se pueden separar: son el contrato con la otra
        // punta, y renumerarlos rompería a todo el que ya esté desplegado.
        for (value, expected) in [
            (1u8, MessageType::Connect),
            (2, MessageType::Auth),
            (3, MessageType::SessionCreate),
            (4, MessageType::SessionJoin),
            (5, MessageType::PeerReady),
            (6, MessageType::Heartbeat),
            (7, MessageType::Data),
            (8, MessageType::PeerJoined),
            (9, MessageType::PeerDisconnected),
            (10, MessageType::SessionClosed),
        ] {
            assert_eq!(MessageType::try_from(value).unwrap(), expected);
            assert_eq!(expected as u8, value);
        }
        assert!(MessageType::try_from(0).is_err());
        assert!(MessageType::try_from(11).is_err());
    }

    #[test]
    fn los_codigos_de_rechazo_viajan_al_log_con_su_nombre() {
        assert_eq!(AuthStatus::Denied.name(), "DENIED");
        assert_eq!(AuthStatus::RateLimited.name(), "RATE_LIMITED");
        assert_eq!(DisconnectReason::Timeout.name(), "TIMEOUT");
        assert_eq!(CloseReason::HostLeft.name(), "HOST_LEFT");
    }
}
