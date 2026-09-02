//! El lado cliente del relay: registrarse, mantener el latido, y enterarse de lo que pasa —
//! ADR-117 D9 y D10.
//!
//! **Puro y sin sockets, igual que la tabla del relay.** Entra «ha pasado este tiempo» o «ha
//! llegado este marco de control» y sale «manda estos bytes» o «pasó esto». Quien los manda es
//! `NetworkManager::pump_relay`. Así el registro entero —los reintentos, el presupuesto, el
//! rechazo, el latido— se prueba sin abrir un puerto.
//!
//! ## El ciclo
//!
//! ```text
//! Registering ──PeerReady──▶ Ready ──SessionClosed──▶ Closed
//!      │                       │
//!      ├──Auth(no)──▶ Denied   └──(sin latido, lo decide el relay)
//!      └──10 s sin respuesta──▶ TimedOut
//! ```
//!
//! `Denied` y `TimedOut` son estados finales y distintos a propósito: el primero significa «el
//! relay contestó que no» —token, versión de wire, aforo— y el segundo «el relay no contestó».
//! Mandan a mirar sitios opuestos, y ADR-117 D10 exige que el motivo del fallo se pueda nombrar.

use std::net::SocketAddr;
use std::time::{Duration, Instant};

use backrooms_relay::protocol::{
    AuthStatus, CloseReason, DisconnectReason, PeerId as RelayPeerId, RelayFrame, RelayMessage,
    SessionId, SessionToken,
};
use backrooms_relay::session::HOST_PEER_ID;
use log::{info, warn};

use super::transport::RelayLink;

/// Cada cuánto se repite el registro mientras no haya respuesta. UDP pierde datagramas y el primer
/// intento se pierde a menudo; el relay trata los reintentos como idempotentes.
pub const REGISTER_RETRY_INTERVAL: Duration = Duration::from_secs(1);

/// Cuánto se insiste antes de rendirse. Diez segundos son diez intentos: si el relay no ha
/// contestado a ninguno, no está o no nos llega.
pub const REGISTER_TIMEOUT: Duration = Duration::from_secs(10);

/// Cada cuánto se late. Cuatro veces por debajo del `peer_timeout` de 8 s del relay: hacen falta
/// **cuatro** latidos perdidos seguidos para que nos den por muertos.
pub const HEARTBEAT_INTERVAL: Duration = Duration::from_secs(2);

/// Con qué se presenta este backend ante el relay.
#[derive(Debug, Clone)]
pub struct RelayConfig {
    pub relay_addr: SocketAddr,
    pub session_id: SessionId,
    pub token: SessionToken,

    /// La versión de wire del JUEGO. El relay la usa para no dejar entrar a un build que no podría
    /// leer el mundo que sirve este host.
    pub wire_version: u16,

    /// `true` crea la sesión (y este backend será el peer 1); `false` entra en una existente.
    pub as_host: bool,
}

/// En qué punto está el registro.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RelayClientState {
    Registering,
    Ready {
        my_peer: RelayPeerId,
        host_peer: RelayPeerId,
    },
    /// El relay contestó que no. `AuthStatus` dice exactamente cuál de los siete motivos.
    Denied(AuthStatus),
    /// El relay no contestó a ninguno de los intentos.
    TimedOut,
    /// La sesión terminó.
    Closed(CloseReason),
}

impl RelayClientState {
    /// Ya no hay nada más que intentar por este camino.
    pub fn is_final(&self) -> bool {
        matches!(
            self,
            RelayClientState::Denied(_) | RelayClientState::TimedOut | RelayClientState::Closed(_)
        )
    }

    /// El nombre para el log y para `fallback_reason`, en el formato de ADR-112.
    pub fn name(&self) -> &'static str {
        match self {
            RelayClientState::Registering => "REGISTERING",
            RelayClientState::Ready { .. } => "READY",
            RelayClientState::Denied(status) => status.name(),
            RelayClientState::TimedOut => "RELAY_NO_RESPONSE",
            RelayClientState::Closed(_) => "SESSION_CLOSED",
        }
    }
}

/// Lo que le pasa al enlace y que alguien de fuera tiene que saber.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RelayClientEvent {
    /// El relay nos admitió. A partir de aquí se puede hablar con los demás peers.
    Ready {
        my_peer: RelayPeerId,
        host_peer: RelayPeerId,
    },
    Denied(AuthStatus),
    TimedOut,
    /// Ha entrado alguien (sólo le llega al host).
    PeerJoined(RelayPeerId),
    PeerLeft(RelayPeerId, DisconnectReason),
    Closed(CloseReason),
}

/// El cliente. Uno por sesión de relay.
#[derive(Debug)]
pub struct RelayClient {
    config: RelayConfig,
    state: RelayClientState,
    started_at: Option<Instant>,
    last_register_at: Option<Instant>,
    last_heartbeat_at: Option<Instant>,
    attempts: u32,
}

impl RelayClient {
    pub fn new(config: RelayConfig) -> Self {
        Self {
            config,
            state: RelayClientState::Registering,
            started_at: None,
            last_register_at: None,
            last_heartbeat_at: None,
            attempts: 0,
        }
    }

    pub fn state(&self) -> &RelayClientState {
        &self.state
    }

    pub fn config(&self) -> &RelayConfig {
        &self.config
    }

    pub fn attempts(&self) -> u32 {
        self.attempts
    }

    /// El enlace que usa `send_datagram`, o `None` mientras no estemos admitidos. **Es la única
    /// puerta**: sin `PeerReady` no hay enlace, y sin enlace no sale ni un datagrama al relay.
    pub fn link(&self) -> Option<RelayLink> {
        let RelayClientState::Ready { my_peer, .. } = self.state else {
            return None;
        };
        Some(RelayLink {
            relay_addr: self.config.relay_addr,
            session_id: self.config.session_id,
            my_peer,
        })
    }

    /// Qué datagrama toca mandar ahora, si toca alguno. No manda nada: sólo lo prepara.
    pub fn poll(&mut self, now: Instant) -> Option<Vec<u8>> {
        match self.state {
            RelayClientState::Registering => self.poll_registering(now),
            RelayClientState::Ready { my_peer, .. } => self.poll_heartbeat(now, my_peer),
            _ => None,
        }
    }

    fn poll_registering(&mut self, now: Instant) -> Option<Vec<u8>> {
        let started = *self.started_at.get_or_insert(now);

        if now.duration_since(started) >= REGISTER_TIMEOUT {
            warn!(
                "RELAY event=register_timed_out relay={} session={:#x} attempts={} elapsed_ms={}",
                self.config.relay_addr,
                self.config.session_id,
                self.attempts,
                now.duration_since(started).as_millis()
            );
            self.state = RelayClientState::TimedOut;
            return None;
        }

        let due = self
            .last_register_at
            .map(|last| now.duration_since(last) >= REGISTER_RETRY_INTERVAL)
            .unwrap_or(true);
        if !due {
            return None;
        }

        self.last_register_at = Some(now);
        self.attempts += 1;

        // El token NO se registra, y no se puede colar por accidente: su `Debug` no lo enseña.
        info!(
            "RELAY event=registering relay={} session={:#x} role={} attempt={}",
            self.config.relay_addr,
            self.config.session_id,
            if self.config.as_host {
                "host"
            } else {
                "joiner"
            },
            self.attempts
        );

        let message = if self.config.as_host {
            RelayMessage::SessionCreate {
                token: self.config.token,
                wire_version: self.config.wire_version,
            }
        } else {
            RelayMessage::SessionJoin {
                token: self.config.token,
                wire_version: self.config.wire_version,
            }
        };

        // `src` 0: todavía no tenemos id — nos lo va a dar el relay. El relay no se fía del `src`
        // de nadie de todas formas.
        RelayFrame::to_relay(self.config.session_id, 0, message)
            .encode()
            .ok()
    }

    fn poll_heartbeat(&mut self, now: Instant, my_peer: RelayPeerId) -> Option<Vec<u8>> {
        let due = self
            .last_heartbeat_at
            .map(|last| now.duration_since(last) >= HEARTBEAT_INTERVAL)
            .unwrap_or(true);
        if !due {
            return None;
        }

        self.last_heartbeat_at = Some(now);
        RelayFrame::to_relay(self.config.session_id, my_peer, RelayMessage::Heartbeat)
            .encode()
            .ok()
    }

    /// Interpreta un marco de control del relay.
    pub fn on_control(&mut self, frame: RelayFrame, now: Instant) -> Option<RelayClientEvent> {
        if frame.session_id != self.config.session_id
            && !matches!(frame.message, RelayMessage::Auth { .. })
        {
            // Un marco de otra sesión: resto de un enlace anterior. No es nuestro.
            return None;
        }

        match frame.message {
            RelayMessage::PeerReady {
                assigned_peer,
                host_peer,
            } => {
                if matches!(self.state, RelayClientState::Ready { .. }) {
                    // Repetido: el relay contesta a cada reintento y los nuestros se cruzan con su
                    // primera respuesta. No es un cambio de estado.
                    return None;
                }
                info!(
                    "RELAY event=ready relay={} session={:#x} my_peer={assigned_peer} host_peer={host_peer} attempts={}",
                    self.config.relay_addr, self.config.session_id, self.attempts
                );
                self.state = RelayClientState::Ready {
                    my_peer: assigned_peer,
                    host_peer,
                };
                self.last_heartbeat_at = Some(now);
                Some(RelayClientEvent::Ready {
                    my_peer: assigned_peer,
                    host_peer,
                })
            }

            RelayMessage::Auth { status } => {
                if status == AuthStatus::Ok {
                    // Respuesta a un `Connect`, que este cliente no manda. No dice nada.
                    return None;
                }
                warn!(
                    "RELAY event=denied relay={} session={:#x} reason={}",
                    self.config.relay_addr,
                    self.config.session_id,
                    status.name()
                );
                self.state = RelayClientState::Denied(status);
                Some(RelayClientEvent::Denied(status))
            }

            RelayMessage::PeerJoined { peer } => {
                info!(
                    "RELAY event=peer_joined session={:#x} peer={peer}",
                    self.config.session_id
                );
                Some(RelayClientEvent::PeerJoined(peer))
            }

            RelayMessage::PeerDisconnected { peer, reason } => {
                info!(
                    "RELAY event=peer_left session={:#x} peer={peer} reason={}",
                    self.config.session_id,
                    reason.name()
                );
                Some(RelayClientEvent::PeerLeft(peer, reason))
            }

            RelayMessage::SessionClosed { reason } => {
                warn!(
                    "RELAY event=session_closed session={:#x} reason={}",
                    self.config.session_id,
                    reason.name()
                );
                self.state = RelayClientState::Closed(reason);
                Some(RelayClientEvent::Closed(reason))
            }

            // Lo demás lo emiten los clientes, no el relay.
            _ => None,
        }
    }

    /// La dirección sintética del host de esta sesión. Es a donde un joiner tiene que mandar su
    /// handshake de juego una vez el relay lo ha admitido.
    pub fn host_synthetic_addr(&self) -> SocketAddr {
        super::transport::synthetic_addr(self.config.session_id, HOST_PEER_ID)
    }
}

#[cfg(test)]
#[path = "relay_client_tests.rs"]
mod tests;
