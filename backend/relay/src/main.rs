//! `backrooms_relay` — el proceso del relay UDP de respaldo (ADR-117).
//!
//! Lee la configuración del entorno, abre el socket y le cede el control al bucle de
//! [`backrooms_relay::server::serve`]. Deliberadamente tonto: todo lo que decide algo está en
//! `session`, y todo lo que habla con la red está en `server`.
//!
//! Variables de entorno, todas con valor por defecto:
//!
//! ```text
//! RELAY_BIND                 dirección de escucha        (0.0.0.0:7790)
//! RELAY_MAX_SESSIONS         sesiones simultáneas         (256)
//! RELAY_MAX_PEERS            peers por sesión, host incl. (50)
//! RELAY_PEER_TIMEOUT_SECS    sin latido = muerto          (8)
//! RELAY_IDLE_TIMEOUT_SECS    sesión sin tráfico = cerrada (120)
//! RELAY_HANDSHAKES_PER_SEC   por IP, en la puerta         (5)
//! RUST_LOG                   filtro de log                (info)
//! ```

use std::time::Duration;

use backrooms_relay::server;
use backrooms_relay::session::{RelayLimits, RelayTable};
use log::{error, info};
use tokio::net::UdpSocket;

/// El puerto por defecto. **Lejos de los del juego** (7777 IPC, 7778/7779 gameplay) a propósito:
/// el relay puede acabar conviviendo con un backend en la misma máquina durante las pruebas, y un
/// choque de puertos ahí se diagnostica fatal.
const DEFAULT_BIND: &str = "0.0.0.0:7790";

#[tokio::main]
async fn main() {
    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    let bind = env_string("RELAY_BIND", DEFAULT_BIND);
    let limits = RelayLimits {
        max_sessions: env_usize("RELAY_MAX_SESSIONS", 256),
        max_peers_per_session: env_usize("RELAY_MAX_PEERS", 50),
        peer_timeout: Duration::from_secs(env_u64("RELAY_PEER_TIMEOUT_SECS", 8)),
        session_idle_timeout: Duration::from_secs(env_u64("RELAY_IDLE_TIMEOUT_SECS", 120)),
        handshakes_per_second_per_ip: env_u64("RELAY_HANDSHAKES_PER_SEC", 5) as u32,
    };

    info!(
        "Backrooms Survival relay v{} — bind={bind} max_sessions={} max_peers={} \
         peer_timeout={}s idle_timeout={}s handshakes_per_sec={}",
        env!("CARGO_PKG_VERSION"),
        limits.max_sessions,
        limits.max_peers_per_session,
        limits.peer_timeout.as_secs(),
        limits.session_idle_timeout.as_secs(),
        limits.handshakes_per_second_per_ip,
    );

    let socket = match UdpSocket::bind(&bind).await {
        Ok(socket) => socket,
        Err(e) => {
            // Sin socket no hay relay. Se dice qué dirección se intentó, que es lo que hace falta
            // para diagnosticarlo: casi siempre es un puerto ocupado o un permiso.
            error!("RELAY event=bind_failed bind={bind} error={e}");
            std::process::exit(1);
        }
    };

    server::serve(socket, RelayTable::new(limits), shutdown_signal()).await;
}

/// Ctrl-C. En Windows `tokio::signal::ctrl_c` cubre también el cierre de la consola, que es como
/// se va a parar esto durante las pruebas.
async fn shutdown_signal() {
    if let Err(e) = tokio::signal::ctrl_c().await {
        error!("RELAY event=signal_handler_failed error={e}");
        // Sin manejador de señales, este futuro nunca se resuelve y el relay sigue sirviendo. Es
        // lo correcto: la alternativa sería apagarse por no poder escuchar un Ctrl-C.
        std::future::pending::<()>().await;
    }
}

fn env_string(key: &str, fallback: &str) -> String {
    std::env::var(key)
        .ok()
        .filter(|v| !v.trim().is_empty())
        .unwrap_or_else(|| fallback.to_string())
}

/// Un valor ilegible NO se convierte en el de por defecto en silencio: se avisa. Un relay que se
/// traga `RELAY_MAX_PEERS=cincuenta` y sirve con otro número es un relay que miente sobre su
/// propia configuración.
fn env_u64(key: &str, fallback: u64) -> u64 {
    match std::env::var(key) {
        Err(_) => fallback,
        Ok(raw) => match raw.trim().parse::<u64>() {
            Ok(value) => value,
            Err(_) => {
                error!("RELAY event=bad_config key={key} value={raw:?} — se usa {fallback}");
                fallback
            }
        },
    }
}

fn env_usize(key: &str, fallback: usize) -> usize {
    env_u64(key, fallback as u64) as usize
}
