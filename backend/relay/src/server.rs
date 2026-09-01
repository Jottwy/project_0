//! El bucle del relay: socket, reloj y ejecución de las acciones que decide [`RelayTable`].
//!
//! **Aquí no se decide nada.** Todo lo que se puede equivocar vive en `session`, que no toca la
//! red; esto es la mano que ejecuta. La separación no es estética: es lo que permite que el
//! comportamiento se pruebe entero sin abrir un puerto, y que lo que se prueba CON puerto sea sólo
//! esto — que un datagrama entra por un socket y sale por otro.
//!
//! Vive en la lib y no en `main.rs` por lo mismo: un bucle dentro de `fn main` no se puede
//! arrancar desde un test.

use std::net::SocketAddr;
use std::time::{Duration, Instant};

use log::{error, info, warn};
use tokio::net::UdpSocket;

use crate::protocol::{rewrite_dst, rewrite_src, CloseReason, MAX_RELAY_DATAGRAM_BYTES};
use crate::session::{Action, RelayTable};

/// Cada cuánto se buscan peers muertos y sesiones ociosas. Un segundo: el timeout más corto que se
/// vigila es de 8 s, así que basta de sobra y no cuesta nada.
pub const TICK_INTERVAL: Duration = Duration::from_secs(1);

/// Cada cuánto se vuelca la línea de métricas.
pub const METRICS_INTERVAL: Duration = Duration::from_secs(60);

/// El búfer de recepción, **un byte más grande que el máximo legal**.
///
/// No es un despiste. `recv_from` TRUNCA en silencio lo que no cabe: con un búfer de exactamente
/// 1216 B, un datagrama de 1300 llegaría recortado a 1216 y pasaría por un mensaje válido, con el
/// payload cortado por la mitad. Con un byte de más, lo que se pasa del techo se ve —`n` sale
/// 1217— y se puede rechazar en vez de reenviar basura.
const RECV_BUFFER_BYTES: usize = MAX_RELAY_DATAGRAM_BYTES + 1;

/// Corre el relay hasta que `shutdown` se resuelva. Al salir cierra todas las sesiones vivas.
///
/// Un solo hilo lógico y sin bloqueos: reenviar un datagrama es leer 16 bytes y llamar a
/// `send_to`, y repartir eso entre tareas costaría más en sincronización de lo que ahorra.
pub async fn serve(
    socket: UdpSocket,
    mut table: RelayTable,
    shutdown: impl std::future::Future<Output = ()>,
) {
    let local = socket
        .local_addr()
        .map(|a| a.to_string())
        .unwrap_or_else(|_| "<desconocido>".to_string());
    info!("RELAY event=connected listening={local}");

    let mut buf = vec![0u8; RECV_BUFFER_BYTES];
    let mut tick = tokio::time::interval(TICK_INTERVAL);
    let mut metrics = tokio::time::interval(METRICS_INTERVAL);
    // La primera pulsación de un `interval` es inmediata; sin esto, el arranque volcaría una línea
    // de métricas todas a cero.
    metrics.tick().await;

    tokio::pin!(shutdown);

    loop {
        tokio::select! {
            received = socket.recv_from(&mut buf) => {
                match received {
                    Ok((n, from)) => {
                        let actions = table.handle(from, &buf[..n], Instant::now());
                        execute(&socket, &mut buf[..n], actions).await;
                    }
                    Err(e) => {
                        // En Windows, un ICMP «puerto inalcanzable» de un peer que se fue vuelve
                        // como error de RECEPCIÓN sobre nuestro propio socket (WSAECONNRESET), y
                        // no significa que el socket esté roto. Se registra y se sigue: rendirse
                        // aquí tiraría el relay entero porque un cliente cerró el juego.
                        warn!("RELAY event=recv_error error={e}");
                    }
                }
            }

            _ = tick.tick() => {
                let actions = table.tick(Instant::now());
                // Un `tick` no nace de ningún datagrama, así que no hay búfer que reenviar: sus
                // acciones son siempre `Send`. Se le pasa un búfer vacío a propósito — si alguna
                // vez saliera un `Forward` de aquí, se vería en el log en vez de reenviar basura.
                execute(&socket, &mut [], actions).await;
            }

            _ = metrics.tick() => {
                let s = table.stats();
                info!(
                    "RELAY event=metrics sessions={} peers={} in={} forwarded={} forwarded_bytes={} \
                     malformed={} unknown_sender={} star_violations={} auth_denied={} rate_limited={} \
                     peers_timed_out={} sessions_created={} sessions_closed={}",
                    table.session_count(),
                    table.peer_count(),
                    s.datagrams_in,
                    s.forwarded,
                    s.forwarded_bytes,
                    s.malformed,
                    s.unknown_sender,
                    s.star_violations,
                    s.auth_denied,
                    s.rate_limited,
                    s.peers_timed_out,
                    s.sessions_created,
                    s.sessions_closed,
                );
            }

            _ = &mut shutdown => {
                info!("RELAY event=shutdown_requested sessions={}", table.session_count());
                break;
            }
        }
    }

    // Despedirse cuesta un datagrama por peer y le ahorra a cada cliente los 8 s de timeout
    // preguntándose si el relay sigue ahí.
    for id in table.session_ids() {
        let actions = table.close_session(id, CloseReason::RelayShutdown);
        execute(&socket, &mut [], actions).await;
    }
    info!("RELAY event=disconnected listening={local}");
}

/// Ejecuta las acciones. `datagram` es el búfer RECIBIDO, que un `Forward` reescribe in situ.
async fn execute(socket: &UdpSocket, datagram: &mut [u8], actions: Vec<Action>) {
    for action in actions {
        match action {
            Action::Send { to, frame } => match frame.encode() {
                Ok(bytes) => send(socket, &bytes, to).await,
                Err(e) => {
                    // Sólo puede pasar por un payload sobredimensionado, y el relay no fabrica
                    // ninguno: si esto salta, el defecto está en el propio relay.
                    error!("RELAY event=encode_failed to={to} error={e}");
                }
            },
            Action::Forward { to, src, dst } => {
                // Los dos únicos bytes que el relay toca de un datagrama de gameplay. El payload
                // no se copia ni se mira.
                if let Err(e) = rewrite_src(datagram, src) {
                    error!("RELAY event=forward_failed to={to} error={e}");
                    continue;
                }
                if let Err(e) = rewrite_dst(datagram, dst) {
                    error!("RELAY event=forward_failed to={to} error={e}");
                    continue;
                }
                send(socket, datagram, to).await;
            }
        }
    }
}

async fn send(socket: &UdpSocket, bytes: &[u8], to: SocketAddr) {
    if let Err(e) = socket.send_to(bytes, to).await {
        // Un envío fallido no puede tirar el relay: el destino puede haberse ido hace un
        // milisegundo, y el `tick` ya se encarga de recogerlo.
        warn!(
            "RELAY event=send_failed to={to} bytes={} error={e}",
            bytes.len()
        );
    }
}
