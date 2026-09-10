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

/// Pone todos los chunks en `Active`, que es como están los que rodean a un jugador.
///
/// Sin esto el goteo los salta (`if !chunk.is_active() { continue }`) y el arnés mide CERO — que no
/// es «los chunks no cuestan nada» sino «no se midió». Pasó en la primera corrida.
fn activate_all_chunks(world: &mut crate::world::World) {
    use crate::world::chunk::ChunkState;
    for chunk in world.chunks.values_mut() {
        chunk.state = ChunkState::Active {
            stabilized: false,
            anchored: false,
        };
    }
}

/// Mueve a todos los peers un paso, como haría su `PlayerUpdate`.
///
/// Importa para el AOI: con todos quietos, la histéresis se estabiliza y el coste baja de forma
/// artificial. Un jugador real no para, así que la medida honesta es con movimiento.
fn step_all_peers(net: &mut NetworkManager, tick: usize) {
    let ids: Vec<PeerId> = net.peers.keys().copied().collect();
    for (i, id) in ids.iter().enumerate() {
        let phase = tick as f32 * 0.1 + i as f32 * 0.7;
        if let Some(p) = net.peers.get_mut(id) {
            let base = p.position;
            p.position = [
                base[0] + phase.cos() * 0.3,
                base[1],
                base[2] + phase.sin() * 0.3,
            ];
        }
    }
}

/// Llena los rosters de STP como una partida avanzada: objetos por el suelo y una base construida.
///
/// Son las dos cosas que más crecen con el tiempo de juego y que un playtest de diez minutos nunca
/// enseña: nadie ha jugado lo bastante como para tener 400 piezas puestas.
fn fill_stp_rosters(net: &mut NetworkManager, items: usize, buildings: usize) {
    use super::protocol::{StpBuildingInfo, StpItemInfo};

    net.stp_items = (0..items)
        .map(|i| StpItemInfo {
            id: i as u32,
            def_id: (i % 40) as i32,
            count: 1,
            position: [(i % 50) as f32 * 4.0, 0.0, (i / 50) as f32 * 4.0],
            rotation: 0.0,
            settling: false,
        })
        .collect();

    net.stp_buildings = (0..buildings)
        .map(|i| StpBuildingInfo {
            id: i as u32,
            def_id: (i % 20) as i32,
            position: [(i % 30) as f32 * 2.0, 0.0, (i / 30) as f32 * 2.0],
            rotation: 0.0,
            group_id: (i / 12) as u32,
            owner_id: 1,
            added: Vec::new(),
        })
        .collect();
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

/// **Con la gente EN MOVIMIENTO**, que es el caso honesto: con todos quietos la histéresis del AOI
/// se asienta y el coste sale más bajo de lo que sería en juego.
#[tokio::test]
#[ignore = "arnés de carga"]
async fn moving_players_cost_more_than_still_ones() {
    println!("\n=== Poses con movimiento (30 Hz, un segundo) ===\n");

    for count in [8usize, 16, 32, 50] {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        register_synthetic_peers(&mut host, count, Spread::SameRoom);

        let before = super::send::sent_bytes_total();
        for tick in 0..30 {
            step_all_peers(&mut host, tick);
            super::sync::broadcast_peer_poses(&mut host).await;
        }
        let kb = (super::send::sent_bytes_total() - before) as f64 / 1024.0;
        println!(
            "  N={count:>3} moviéndose  ->  {kb:>8.1} KB/s  ({:.0} % del techo)",
            100.0 * kb / 256.0
        );
    }
    println!();
}

/// **El goteo de chunks**, que era el 80 % del tráfico antes de ADR-139 D1. Aquí se ve cómo escala
/// con jugadores Y con tamaño del mundo cargado — dos ejes que crecen a la vez en una partida larga.
#[tokio::test]
#[ignore = "arnés de carga"]
async fn chunk_streaming_by_players_and_world_size() {
    println!("\n=== Goteo de chunks: jugadores × mundo cargado (10 rondas) ===\n");

    for chunks in [16i32, 64, 144] {
        for count in [8usize, 32, 50] {
            let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
            register_synthetic_peers(&mut host, count, Spread::Scattered);
            let mut world = crate::world::World::new(42);
            for i in 0..chunks {
                world.ensure_chunk((i % 12, i / 12));
            }
            activate_all_chunks(&mut world);

            let before = super::send::sent_bytes_total();
            for _ in 0..10 {
                super::sync::broadcast_chunk_states(
                    &mut host,
                    &world,
                    crate::utils::Vec3::new(0.0, 1.8, 0.0),
                )
                .await;
            }
            let kb = (super::send::sent_bytes_total() - before) as f64 / 1024.0;
            // Un cero aquí NO significa «los chunks salen gratis»: significa que no se midió.
            // Pasa con estos peers sintéticos —dirección inerte— y está sin resolver; hasta que lo
            // esté, el arnés lo dice en vez de dejar un 0,0 que alguien lea como resultado.
            if kb == 0.0 {
                println!("  mundo={chunks:>4} chunks  N={count:>3}  ->  NO MEDIDO (nada salió; ver nota)");
            } else {
                println!("  mundo={chunks:>4} chunks  N={count:>3}  ->  {kb:>8.1} KB en 10 rondas");
            }
        }
    }
    println!();
}

/// **Una partida AVANZADA**: objetos por el suelo y una base construida. Es lo que un playtest de
/// diez minutos no puede enseñar, porque nadie ha jugado lo bastante para tener 400 piezas puestas.
#[tokio::test]
#[ignore = "arnés de carga"]
async fn loot_and_buildings_by_world_age() {
    println!(
        "\n=== Rosters de STP: objetos y construcciones acumulados (10 rondas, 32 jugadores) ===\n"
    );

    for (items, buildings) in [(50usize, 20usize), (300, 150), (1000, 400)] {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        register_synthetic_peers(&mut host, 32, Spread::Scattered);
        fill_stp_rosters(&mut host, items, buildings);

        let before = super::send::sent_bytes_total();
        for _ in 0..10 {
            super::sync::broadcast_stp_items(&mut host).await;
            super::sync::broadcast_stp_buildings(&mut host).await;
        }
        let kb = (super::send::sent_bytes_total() - before) as f64 / 1024.0;
        println!("  {items:>5} objetos + {buildings:>4} piezas  ->  {kb:>8.1} KB en 10 rondas");
    }
    println!();
}

/// **Dónde se van los milisegundos**, fase por fase. Sin esto, optimizar es adivinar — y en esta
/// tanda adivinar ya falló tres veces.
#[tokio::test]
#[ignore = "arnés de carga"]
async fn cpu_breakdown_by_phase() {
    println!("\n=== Reparto de CPU por fase (20 rondas, 300 objetos + 150 piezas) ===\n");

    for count in [8usize, 32, 50] {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        register_synthetic_peers(&mut host, count, Spread::SameRoom);
        fill_stp_rosters(&mut host, 300, 150);
        let mut world = crate::world::World::new(42);
        for i in 0..64 {
            world.ensure_chunk((i % 8, i / 8));
        }
        activate_all_chunks(&mut world);

        const ROUNDS: u32 = 20;
        let mut poses_ms = 0.0;
        let mut chunks_ms = 0.0;
        let mut rosters_ms = 0.0;

        for tick in 0..ROUNDS as usize {
            step_all_peers(&mut host, tick);

            let t = std::time::Instant::now();
            super::sync::broadcast_peer_poses(&mut host).await;
            poses_ms += t.elapsed().as_secs_f64() * 1000.0;

            let t = std::time::Instant::now();
            super::sync::broadcast_chunk_states(
                &mut host,
                &world,
                crate::utils::Vec3::new(0.0, 1.8, 0.0),
            )
            .await;
            chunks_ms += t.elapsed().as_secs_f64() * 1000.0;

            let t = std::time::Instant::now();
            super::sync::broadcast_stp_items(&mut host).await;
            super::sync::broadcast_stp_buildings(&mut host).await;
            rosters_ms += t.elapsed().as_secs_f64() * 1000.0;
        }

        let r = ROUNDS as f64;
        let total = (poses_ms + chunks_ms + rosters_ms) / r;
        println!(
            "  N={count:>3}  total {total:>6.2} ms  =  poses {:>6.2}  chunks {:>5.2}  rosters {:>5.2}",
            poses_ms / r,
            chunks_ms / r,
            rosters_ms / r
        );
    }
    println!();
}

/// **El coste en CPU del anfitrión**, no en bytes: cuánto tarda una ronda completa de emisión con
/// todo cargado a la vez. El presupuesto de un tick a 60 Hz son 16,67 ms.
#[tokio::test]
#[ignore = "arnés de carga"]
async fn host_cpu_per_broadcast_round() {
    println!("\n=== CPU del anfitrión por ronda de emisión (presupuesto: 16,67 ms) ===\n");

    for count in [8usize, 32, 50] {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        register_synthetic_peers(&mut host, count, Spread::SameRoom);
        fill_stp_rosters(&mut host, 300, 150);
        let mut world = crate::world::World::new(42);
        for i in 0..64 {
            world.ensure_chunk((i % 8, i / 8));
        }

        let started = std::time::Instant::now();
        const ROUNDS: u32 = 20;
        for tick in 0..ROUNDS as usize {
            step_all_peers(&mut host, tick);
            super::sync::broadcast_peer_poses(&mut host).await;
            super::sync::broadcast_chunk_states(
                &mut host,
                &world,
                crate::utils::Vec3::new(0.0, 1.8, 0.0),
            )
            .await;
            super::sync::broadcast_stp_items(&mut host).await;
            super::sync::broadcast_stp_buildings(&mut host).await;
        }
        let per_round_ms = started.elapsed().as_secs_f64() * 1000.0 / ROUNDS as f64;
        println!(
            "  N={count:>3}  ->  {per_round_ms:>7.2} ms por ronda  ({:.0} % del presupuesto)",
            100.0 * per_round_ms / 16.67
        );
    }
    println!();
}
