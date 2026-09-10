//! ADR-140 D3 — **cuánto emite el anfitrión con N jugadores**, medido en vez de extrapolado.
//!
//! # Por qué existe
//!
//! Toda la tanda del lag (ADR-137/138/139) se midió con partidas de una y dos personas, y
//! `SessionMaxPlayers = 50` **no se ha probado nunca**. Las cuentas de «50 no caben» eran
//! extrapolaciones lineales desde dos jugadores, y en esta misma tanda la extrapolación falló tres
//! veces: el `MovementReconciler`, el LOD de entidades y el roster de STP.
//!
//! Levantar 50 clientes de Unity no es viable —entre 1,3 y 3 GB cada uno, medido—, pero **no hace
//! falta**: la pregunta es cuántos bytes tendría que poner el anfitrión en el cable, y eso se
//! responde montando el `NetworkManager` real con N peers y contando lo que emite de verdad.
//!
//! # Qué mide y qué NO
//!
//! **Sí**: los bytes que el host emitiría por segundo, con el AOI y los gates reales corriendo, y
//! cómo crecen con N. Es la magnitud que decidió toda esta tanda.
//!
//! **No**: la carga de CPU del cliente, el coste de renderizar 50 avatares, ni la red de verdad
//! (aquí todo va por loopback). Un veredicto de «caben 50» de este arnés significa «el anfitrión
//! puede emitirlo», no «el juego va fino con 50».
//!
//! Se corre a mano porque tarda y no es una regresión:
//! `cargo test --bin backrooms_server load_tests -- --ignored --nocapture`

use super::peer::PeerConnection;
use super::{NetworkManager, PeerId};
use std::net::SocketAddr;

/// Reparto de posiciones de los peers sintéticos.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Spread {
    /// Todos en la misma sala: el peor caso del relay, donde el AOI no filtra nada y el coste es
    /// N×(N−1) entero.
    SameRoom,
    /// Repartidos por el mapa, más lejos entre sí que `AOI_POSE_RADIUS_M`.
    Scattered,
}

/// Registra `count` peers sintéticos en un host ya montado, sin handshake.
///
/// Van a la dirección inerte a propósito (la misma que usan los facelings): lo que se quiere medir
/// es cuántos BYTES prepara el emisor, y `send_datagram` los contabiliza antes de decidir la vía.
/// Así el arnés no necesita 50 sockets vivos al otro lado.
fn register_synthetic_peers(net: &mut NetworkManager, count: usize, spread: Spread) {
    for i in 0..count {
        let id = 3000 + i as PeerId;
        let addr: SocketAddr = super::INERT_PEER_ADDR;
        let mut conn = PeerConnection::new(id, format!("bot{i}"), addr);
        let pos = match spread {
            // Un corro de 10 m: todos dentro del AOI de todos.
            Spread::SameRoom => {
                let angle = i as f32 * 0.7;
                [angle.cos() * 10.0, 1.8, angle.sin() * 10.0]
            }
            // Una rejilla con 250 m de paso: muy por encima del radio del AOI.
            Spread::Scattered => {
                let row = (i / 8) as f32;
                let col = (i % 8) as f32;
                [col * 250.0, 1.8, row * 250.0]
            }
        };
        conn.update_player_state(pos, 0.0, "idle".into());
        net.peers.insert(id, conn);
    }
}

/// Emite las rondas que caben en un segundo de juego y devuelve los KB/s que salieron.
///
/// Se cuentan los bytes en el mismo punto que `BWTRACE` en producción, así que el número es
/// comparable con el que sale de una partida real.
async fn measure_kb_per_second(net: &mut NetworkManager, spread: Spread, count: usize) -> f64 {
    let before = super::send::sent_bytes_total();

    // Un segundo de poses a 30 Hz (ADR-138 D1).
    for _ in 0..30 {
        super::sync::broadcast_peer_poses(net).await;
    }

    let after = super::send::sent_bytes_total();
    let kb = (after - before) as f64 / 1024.0;
    println!(
        "  N={count:>3}  {spread:?}  ->  {kb:>8.1} KB/s de poses  \
         ({:.1} % de un techo de 256 KB/s)",
        100.0 * kb / 256.0
    );
    kb
}

/// **La medida que responde a «¿caben 50?»**, para las dos formas de estar repartidos.
///
/// No afirma un veredicto: imprime la curva. El veredicto lo pone quien la lea, contra el techo que
/// tenga la conexión del anfitrión de verdad.
#[tokio::test]
#[ignore = "arnés de carga: se corre a mano, no es una regresión"]
async fn host_outgoing_traffic_by_player_count() {
    println!("\n=== ADR-140 D3 — emisión del anfitrión por número de jugadores ===");
    println!("Sólo el relay de poses (`relay_as`). El broadcast de chunks y rosters va aparte.\n");

    for spread in [Spread::SameRoom, Spread::Scattered] {
        for count in [2usize, 8, 16, 32, 50] {
            let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
            register_synthetic_peers(&mut host, count, spread);
            assert_eq!(host.peers.len(), count, "preparación: los N peers dentro");

            measure_kb_per_second(&mut host, spread, count).await;
        }
        println!();
    }
}
