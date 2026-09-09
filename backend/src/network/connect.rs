//! La secuencia de conexión de un joiner: directa, LAN, Steam, relay — ADR-117 D10 y ADR-135 D6.
//!
//! **Nada de fallback silencioso infinito.** Cada etapa tiene su presupuesto, cada salto dice por
//! qué, y el final —bueno o malo— tiene nombre. Es la corrección de un modo de fallo real y
//! medido: hasta ADR-117 había un solo destino y un solo presupuesto de 15 s, así que un host
//! inalcanzable se veía como quince segundos de nada seguidos de un mensaje que no señalaba a
//! ningún sitio.
//!
//! ```text
//! Direct ──5 s──▶ Lan ──3 s──▶ Steam ──8 s──▶ Relay ──12 s──▶ Failed
//!    │             │             │             │
//!    └─────────────┴─────────────┴─────────────┴──HandshakeAck──▶ Connected
//! ```
//!
//! **Las etapas que no existen se saltan.** Sin `CONNECT_LAN` no hay etapa LAN; sin `CONNECT_STEAM`
//! no hay etapa de Steam; sin relay configurado no hay etapa de relay. Una partida en LAN de toda
//! la vida tiene una sola etapa y se comporta exactamente como antes de estos dos ADR.
//!
//! ## Aquí no hay nada de Steam, y es la decisión (ADR-135 D2)
//!
//! La etapa `Steam` es **una dirección de loopback más**. Quien habla con la red de Valve es un
//! túnel que vive en Unity: escucha en `127.0.0.1:<efímero>`, y lo que le llega sale por Steam
//! hacia el host. Este módulo —y el backend entero— no enlaza Steamworks, no conoce ningún
//! `SteamId` y no cambia de transporte: sigue siendo el mismo UDP con el mismo framing.
//!
//! ## Dónde NO está la heurística de «misma red»
//!
//! No está aquí. El backend hace bind en `0.0.0.0` y no sabe cuál de las diez IPv4 de la máquina
//! es la suya; Unity sí lo sabe —`LocalAddressProbe` ya lo calcula para poder anunciar— así que la
//! decisión de si la LAN del host merece un intento se toma allí, y aquí sólo llega el resultado:
//! si viene `CONNECT_LAN`, se prueba. Poner la heurística en los dos sitios sería tenerla en
//! ninguno.

use std::net::SocketAddr;
use std::time::{Duration, Instant};

/// Por dónde se está intentando entrar.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConnectStage {
    /// El endpoint que anunció el host: su IP pública con reenvío, o lo que escribió el humano.
    Direct,
    /// La dirección LAN del host (`bs_lan_ip`). Sólo llega hasta aquí quien Unity ha decidido que
    /// puede estar en la misma red.
    Lan,
    /// A través de la red de relay de Valve (ADR-135). La dirección es el **loopback** del túnel
    /// que Unity tiene abierto en esta misma máquina, no un endpoint de internet: aquí no hay
    /// Steam, sólo un puerto más al que mandar UDP.
    Steam,
    /// A través del relay (ADR-117).
    Relay,
}

impl ConnectStage {
    /// El nombre que va al log y a `transport=` — el vocabulario del encargo.
    pub fn name(self) -> &'static str {
        match self {
            ConnectStage::Direct => "direct",
            ConnectStage::Lan => "lan",
            ConnectStage::Steam => "steam",
            ConnectStage::Relay => "relay",
        }
    }

    /// Cuánto se insiste antes de pasar a la siguiente.
    ///
    /// Los cuatro números tienen motivo. **Directo, 5 s**: si el puerto está abierto, el
    /// `HandshakeAck` vuelve en decenas de milisegundos incluso entre continentes; cinco segundos
    /// son cinco reintentos y de sobra para distinguir «lento» de «no está». **LAN, 3 s**: si de
    /// verdad es la misma red, contesta en milisegundos. **Steam, 8 s** (ADR-135 D6): el túnel
    /// negocia ruta con Valve —intenta P2P directo y sólo cae al relay si no lo consigue— y el
    /// handshake de juego va DESPUÉS de eso; es un número inicial y el ADR lo declara pendiente de
    /// medir con dos testers en redes distintas. **Relay, 12 s**: es el único que tiene dos pasos
    /// —registrarse (hasta 10 s) y luego el handshake de juego—, así que necesita el presupuesto
    /// del registro más un margen.
    pub fn budget(self) -> Duration {
        match self {
            ConnectStage::Direct => Duration::from_secs(5),
            ConnectStage::Lan => Duration::from_secs(3),
            ConnectStage::Steam => Duration::from_secs(8),
            ConnectStage::Relay => Duration::from_secs(12),
        }
    }
}

/// Un intento: a dónde y por qué vía.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ConnectCandidate {
    pub addr: SocketAddr,
    pub stage: ConnectStage,
}

/// Qué ha pasado al mirar el reloj.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Advance {
    /// La etapa de ahora sigue dentro de su presupuesto.
    Stay,
    /// Se ha agotado y se pasa a la siguiente. `reason` es el `fallback_reason` del encargo.
    Moved {
        to: ConnectCandidate,
        reason: String,
    },
    /// No queda ninguna vía. `reason` cuenta la historia entera, etapa por etapa.
    Exhausted { reason: String },
}

/// La secuencia. Se construye con lo que haya y se recorre en orden.
#[derive(Debug, Clone)]
pub struct ConnectSequence {
    candidates: Vec<ConnectCandidate>,
    index: usize,
    stage_started_at: Option<Instant>,
    /// Lo que se ha ido intentando, para poder redactar el motivo final. Un fallo que sólo dice
    /// «no se pudo conectar» obliga a abrir el log; éste se puede leer en el panel.
    attempted: Vec<(ConnectStage, SocketAddr)>,
    exhausted: bool,
}

impl ConnectSequence {
    /// Arma la secuencia. Las vías ausentes simplemente no están.
    ///
    /// El ORDEN es el de ADR-135 D6 y no es negociable aquí: directo y LAN no pagan salto y van
    /// primero; entre las dos vías con salto, Steam va antes que el relay propio porque la red de
    /// Valve no cuesta dinero, está desplegada y autentica, y el relay propio queda como última
    /// vía y como la única para builds fuera de Steam.
    pub fn new(
        direct: Option<SocketAddr>,
        lan: Option<SocketAddr>,
        steam: Option<SocketAddr>,
        relay: Option<SocketAddr>,
    ) -> Self {
        let mut candidates = Vec::with_capacity(4);
        if let Some(addr) = direct {
            candidates.push(ConnectCandidate {
                addr,
                stage: ConnectStage::Direct,
            });
        }
        // La LAN no se prueba si es la misma dirección que la directa: sería repetir el mismo
        // intento fallido y gastar tres segundos del jugador para nada.
        if let Some(addr) = lan {
            if Some(addr) != direct {
                candidates.push(ConnectCandidate {
                    addr,
                    stage: ConnectStage::Lan,
                });
            }
        }
        if let Some(addr) = steam {
            candidates.push(ConnectCandidate {
                addr,
                stage: ConnectStage::Steam,
            });
        }
        if let Some(addr) = relay {
            candidates.push(ConnectCandidate {
                addr,
                stage: ConnectStage::Relay,
            });
        }

        Self {
            candidates,
            index: 0,
            stage_started_at: None,
            attempted: Vec::new(),
            exhausted: false,
        }
    }

    /// Hay al menos una vía que intentar.
    pub fn has_candidates(&self) -> bool {
        !self.candidates.is_empty()
    }

    /// El intento de ahora mismo.
    pub fn current(&self) -> Option<ConnectCandidate> {
        if self.exhausted {
            return None;
        }
        self.candidates.get(self.index).copied()
    }

    pub fn stage(&self) -> Option<ConnectStage> {
        self.current().map(|c| c.stage)
    }

    /// Marca el arranque de la etapa actual. Idempotente: sólo la primera llamada cuenta.
    pub fn start(&mut self, now: Instant) {
        if self.stage_started_at.is_none() {
            self.stage_started_at = Some(now);
            if let Some(c) = self.current() {
                self.attempted.push((c.stage, c.addr));
            }
        }
    }

    /// Mira el reloj y decide si toca cambiar de vía.
    pub fn advance_if_expired(&mut self, now: Instant) -> Advance {
        if self.exhausted {
            return Advance::Stay;
        }

        let Some(current) = self.current() else {
            return Advance::Stay;
        };
        let Some(started) = self.stage_started_at else {
            return Advance::Stay;
        };

        let elapsed = now.duration_since(started);
        if elapsed < current.stage.budget() {
            return Advance::Stay;
        }

        let reason = format!(
            "sin respuesta por {} ({}) en {} s",
            current.stage.name(),
            current.addr,
            elapsed.as_secs()
        );

        self.index += 1;
        self.stage_started_at = None;

        match self.current() {
            Some(next) => {
                self.start(now);
                Advance::Moved {
                    to: next,
                    reason: format!("{reason}; se prueba por {}", next.stage.name()),
                }
            }
            None => {
                self.exhausted = true;
                Advance::Exhausted {
                    reason: self.describe_failure(),
                }
            }
        }
    }

    /// La historia entera, para que el jugador vea qué se intentó y no sólo que falló.
    pub fn describe_failure(&self) -> String {
        if self.attempted.is_empty() {
            return "no había ninguna dirección a la que conectarse".to_string();
        }

        let vias: Vec<String> = self
            .attempted
            .iter()
            .map(|(stage, addr)| format!("{} ({addr})", stage.name()))
            .collect();

        format!(
            "no se pudo entrar por ninguna vía: se intentó {}. Comprueba que el host sigue \
             hosteando y que tu conexión funciona",
            vias.join(", ")
        )
    }

    /// Las vías intentadas, en orden. Para el log y para los tests.
    pub fn attempted(&self) -> &[(ConnectStage, SocketAddr)] {
        &self.attempted
    }
}

#[cfg(test)]
#[path = "connect_tests.rs"]
mod tests;
