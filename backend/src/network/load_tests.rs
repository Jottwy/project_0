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

/// Techo de emisión del túnel, en KB/s.
///
/// **Este arnés dio 256 KB/s por buenos y era falso.** `SendRateMaxBytesPerSec` lleva puesto
/// 1 MB/s en `FacepunchSteamTunnel.cs`; 256 KB/s es el valor por defecto de Valve, que este
/// proyecto ya no usa. Todo porcentaje impreso por una corrida anterior a este commit está
/// inflado ×4 — los KB/s en crudo siguen siendo válidos, la lectura «% del techo» no.
///
/// Con nombre y en un solo sitio a propósito: la constante vivía escrita a mano en dos `println!`
/// distintos, que es exactamente cómo se queda vieja sin que nadie lo note.
const STEAM_SEND_RATE_MAX_KB_S: f64 = 1024.0;

/// Presupuesto de CPU de un tick a 60 Hz, en milisegundos.
const TICK_BUDGET_MS: f64 = 16.67;

/// Reparto de posiciones de los peers sintéticos.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Spread {
    /// Todos en la misma sala: el peor caso del relay, donde el AOI no filtra nada y el coste es
    /// N×(N−1) entero.
    SameRoom,
    /// Repartidos por el mapa, más lejos entre sí que `AOI_POSE_RADIUS_M`.
    Scattered,
    /// N personas en corrillos de `group`, con los corrillos MUY separados entre sí (600 m, cinco
    /// veces el lado de la casilla del índice).
    ///
    /// Es el caso real: la gente ni se amontona toda en una sala ni anda perfectamente sola. Va en
    /// parejas, en tríos, en grupetes. Y es el que responde a «¿20 en diez parejas cuesta lo mismo
    /// que 20 en una sala?».
    Clustered { group: usize },
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
            // Corrillos de `group` en un corro de 3 m, y los corrillos a 600 m unos de otros para
            // que NINGÚN par de corrillos distintos entre en el radio del otro.
            Spread::Clustered { group } => {
                let g = i / group.max(1);
                let m = i % group.max(1);
                let angle = m as f32 * 2.4;
                [
                    (g % 40) as f32 * 600.0 + angle.cos() * 3.0,
                    1.8,
                    (g / 40) as f32 * 600.0 + angle.sin() * 3.0,
                ]
            }
        };
        // Orientaciones repartidas y no todas a 0: con el cono de atención apagado esto no cambia
        // ni un byte (el relay no mira `rotation`), pero deja el arnés listo para medirlo el día
        // que se encienda — con todos mirando al mismo sitio, el reparto de quién está a la espalda
        // de quién sale degenerado y el ahorro medido sería mentira.
        conn.update_player_state(pos, (i as f32 * 37.0) % 360.0, "idle".into());
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
        super::sync::broadcast_peer_poses(net, None).await;
    }

    let after = super::send::sent_bytes_total();
    let kb = (after - before) as f64 / 1024.0;
    println!(
        "  N={count:>3}  {spread:?}  ->  {kb:>8.1} KB/s de poses  \
         ({:.1} % del techo de {STEAM_SEND_RATE_MAX_KB_S:.0} KB/s)",
        100.0 * kb / STEAM_SEND_RATE_MAX_KB_S
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
            super::sync::broadcast_peer_poses(&mut host, None).await;
        }
        let kb = (super::send::sent_bytes_total() - before) as f64 / 1024.0;
        println!(
            "  N={count:>3} moviéndose  ->  {kb:>8.1} KB/s  ({:.0} % del techo)",
            100.0 * kb / STEAM_SEND_RATE_MAX_KB_S
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
            super::sync::broadcast_peer_poses(&mut host, None).await;
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

/// **Hasta dónde llega la CPU con la gente REPARTIDA**, que es el caso que decide el tope de una
/// sesión.
///
/// Juntos en una sala el muro es el ancho de banda y llega pronto: el relay crece con N² y a ~22
/// se toca el megabyte de `SendRateMax`. Repartidos, el AOI y el PVS dejan las poses en CERO
/// (medido), así que el límite deja de ser el cable y pasa a ser el propio bucle — y eso nadie lo
/// había buscado: el arnés paraba en 50, donde todavía va al 20 %.
///
/// Sube hasta reventar y para en cuanto una ronda se come el presupuesto de 16,67 ms. El número que
/// salga NO es «jugadores que caben»: es cuántos peers puede recorrer el emisor, que es la mitad
/// del problema. La otra mitad —simular sus criaturas, servirles chunks— va aparte.
#[tokio::test]
#[ignore = "arnés de carga"]
async fn host_cpu_ceiling_with_scattered_players() {
    println!("\n=== Techo de CPU con jugadores REPARTIDOS (presupuesto: 16,67 ms) ===\n");

    for count in [50usize, 100, 200, 400, 800, 1600] {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        register_synthetic_peers(&mut host, count, Spread::Scattered);
        fill_stp_rosters(&mut host, 300, 150);
        let mut world = crate::world::World::new(42);
        for i in 0..64 {
            world.ensure_chunk((i % 8, i / 8));
        }

        let started = std::time::Instant::now();
        const ROUNDS: u32 = 20;
        for tick in 0..ROUNDS as usize {
            step_all_peers(&mut host, tick);
            super::sync::broadcast_peer_poses(&mut host, None).await;
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
        let pct = 100.0 * per_round_ms / 16.67;
        println!(
            "  N={count:>5}  ->  {per_round_ms:>8.2} ms por ronda  ({pct:>5.0} % del presupuesto)"
        );
        if per_round_ms > 16.67 {
            println!("\n  REVENTADO en N={count}: una ronda ya no cabe en un tick.\n");
            return;
        }
    }
    println!("\n  No reventó: el tope está por encima del último N probado.\n");
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
            super::sync::broadcast_peer_poses(&mut host, None).await;
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

/// **El techo total: cuantos jugadores aguanta el anfitrion, y que muro toca primero.**
///
/// Los demas arneses de este fichero miden un eje cada uno y dejan el veredicto al lector. Este
/// responde la pregunta entera, porque la respuesta honesta necesita los dos ejes a la vez: **no
/// hay un numero de jugadores, hay dos, y cual manda depende de como esten repartidos.**
///
///   - Juntos en una sala, el muro es el CABLE. El relay crece con N^2 porque cada uno que entra le
///     anyade una fuente a todos los demas, y el megabyte por segundo de `SendRateMax` se llena
///     mucho antes de que la CPU se entere.
///   - Repartidos, el AOI y el PVS dejan las poses casi en cero y el cable deja de ser el problema.
///     Entonces el muro es el propio bucle, que recorre peers aunque no les mande nada.
///
/// Corta en cuanto uno de los dos presupuestos revienta y dice CUAL fue. Un techo sin decir que lo
/// causo no sirve para optimizar: se ataca el eje equivocado.
///
/// # Por que aqui NO se llenan los rosters
///
/// La primera version de este arnes llamaba tambien a `broadcast_stp_items` y compania con 300
/// objetos y 150 piezas, y daba a `Scattered` por reventado en N=50 con 1915 KB/s — un numero que
/// contradice de frente que las poses repartidas salgan a cero.
///
/// No era ancho de banda: **la puerta de los rosters es por TIEMPO** (`RosterGate::should_send`
/// mira un `Instant`), y este arnes recorre sus 30 rondas en unos milisegundos de reloj real. La
/// puerta abre UNA vez, se emite un volcado entero de roster a los 50, y dividirlo por «un segundo
/// simulado» convierte una rafaga en una tasa que no existe.
///
/// Es una trampa general de este fichero: **una puerta por tiempo no se puede medir en un arnes que
/// corre mas rapido que el reloj.** Asi que aqui se mide SOLO el relay de poses, que es lo que
/// crece con N y lo que toda esta tanda ha estado optimizando. El coste de los rosters tiene su
/// propio arnes (`loot_and_buildings_by_world_age`) y se lee en KB por rafaga, nunca por segundo.
///
/// # Lo que destapo, y lo que se arreglo con ello (2026-09-12)
///
/// Juntos: 20 aguanta, 24 revienta el cable. Confirma por medida el ~22 que hasta entonces era una
/// extrapolacion desde los 505 KB/s de N=16. No ha cambiado desde entonces y no deberia: en una
/// sala todos caen en la misma casilla del indice y el muro es el cable, no la CPU.
///
/// Repartidos, la primera corrida dio 300 al 95 % del tick y 320 reventado — **corrigiendo a la
/// baja el ~600 que se venia diciendo**, que salia de extrapolar linealmente desde N=50. No era
/// lineal: doblar N multiplicaba el coste por ~4.
///
/// La causa: el bucle de pares era O(N^2) **aunque el AOI rechazara todo**, porque preguntar cuesta
/// igual que aceptar. El AOI ahorraba cable y no ahorraba nada de CPU, justo en el caso —gente
/// repartida— donde el cable ya no es el problema.
///
/// Con el indice espacial puesto (casillas del radio de SALIDA, vecindad de 3x3), la misma escalera
/// da lineal y el techo se mueve un orden de magnitud:
///
/// ```text
///          antes          despues
///    200    6,97 ms        1,08 ms
///    400   revienta        2,15 ms
///   1600        --         7,13 ms
///   3200        --        14,51 ms   <- ultimo que aguanta
///   4000        --        19,59 ms   <- revienta
/// ```
///
/// Techo repartido: de ~310 a ~3.500. Y ahora SI es lineal (~0,005 ms por peer), asi que por
/// primera vez extrapolar desde esta curva no es una mentira.
///
/// Nota sobre el tope por destinatario (`POSE_FIDELITY_CAP` = 96): en la rama juntos los dos muros
/// llegan antes de que nadie junte 96 fuentes, y en la repartida el AOI ya corto casi todo. Esta
/// corrida es tambien la comprobacion independiente de que el tope sigue entrando apagado.
///
/// # Dos avisos mas sobre lo que este numero NO es
///
///   - Es lo que el EMISOR aguanta. Simular las criaturas de esa gente y servirles chunks va
///     aparte, y en una partida real llega antes.
///   - Va en perfil de depuracion, como el resto del fichero. Los milisegundos son pesimistas
///     contra release; valen para comparar entre escalones, no como cifra absoluta.
///
/// `cargo test --bin backrooms_server host_total_player_ceiling -- --ignored --nocapture`
#[tokio::test]
#[ignore = "arnés de carga: se corre a mano, no es una regresión"]
async fn host_total_player_ceiling() {
    // 30 rondas = un segundo simulado, igual que `measure_kb_per_second`, para que los KB/s salgan
    // comparables con el resto del fichero. El coste por ronda va contra un tick de 60 Hz.
    const ROUNDS: u32 = 30;

    println!("\n=== TECHO TOTAL DE JUGADORES (solo relay de poses) ===");
    println!(
        "Presupuestos: {STEAM_SEND_RATE_MAX_KB_S:.0} KB/s de cable, {TICK_BUDGET_MS:.2} ms por ronda.\n"
    );

    for spread in [Spread::SameRoom, Spread::Scattered] {
        // Escalones finos donde se espera el muro de cada forma: el cable llega pronto juntos, la
        // CPU tarda muchisimo repartidos.
        let ladder: &[usize] = match spread {
            Spread::SameRoom => &[8, 12, 16, 20, 24, 28, 32, 40, 48, 64, 96, 128],
            Spread::Scattered => &[50, 100, 200, 400, 800, 1600, 2400, 3200, 4000, 5000, 6000],
            // Los corrillos tienen su propio arnes (`cost_by_how_people_group_up`), que separa el
            // efecto del TAMANYO del grupo del efecto del numero total.
            Spread::Clustered { .. } => &[],
        };

        println!("--- {spread:?} ---");
        let mut last_ok: Option<usize> = None;
        let mut verdict: Option<(usize, &str)> = None;

        for &count in ladder {
            let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
            register_synthetic_peers(&mut host, count, spread);

            let before = super::send::sent_bytes_total();
            let started = std::time::Instant::now();
            for tick in 0..ROUNDS as usize {
                step_all_peers(&mut host, tick);
                super::sync::broadcast_peer_poses(&mut host, None).await;
            }
            let ms = started.elapsed().as_secs_f64() * 1000.0 / ROUNDS as f64;
            let kb = (super::send::sent_bytes_total() - before) as f64 / 1024.0;

            println!(
                "  N={count:>5}  ->  {kb:>8.1} KB/s ({:>5.0} %)   {ms:>7.3} ms/ronda ({:>5.1} %)",
                100.0 * kb / STEAM_SEND_RATE_MAX_KB_S,
                100.0 * ms / TICK_BUDGET_MS
            );

            if kb > STEAM_SEND_RATE_MAX_KB_S {
                verdict = Some((count, "el CABLE (ancho de banda)"));
                break;
            }
            if ms > TICK_BUDGET_MS {
                verdict = Some((count, "la CPU del emisor"));
                break;
            }
            last_ok = Some(count);
        }

        match (verdict, last_ok) {
            (Some((broke_at, what)), Some(ok)) => println!(
                "\n  REVENTADO en N={broke_at}: el muro fue {what}.\n  \
                 Ultimo N que aguanto entero: {ok}.\n"
            ),
            (Some((broke_at, what)), None) => println!(
                "\n  REVENTADO ya en el primer escalon (N={broke_at}): el muro fue {what}.\n"
            ),
            (None, Some(ok)) => println!(
                "\n  NO reventó: aguantó hasta N={ok}, el último escalón probado.\n  \
                 El techo real está por encima — sube la escalera si hace falta el número.\n"
            ),
            (None, None) => println!("\n  Escalera vacía: nada que medir.\n"),
        }
    }
}

/// **Lo que de verdad decide el coste no es cuanta gente hay, sino con cuanta gente se ve cada
/// uno.** Este arnes lo separa en dos preguntas.
///
/// Primero, a MISMO numero de jugadores, cambiar como se agrupan: 20 en diez parejas contra 20 en
/// una sola sala. Mismo censo, coste muy distinto.
///
/// Despues, fijado el corrillo en parejas, subir N hasta reventar — porque si el coste va con la
/// densidad LOCAL y no con el total, el techo de la gente emparejada deberia parecerse al de la
/// gente sola y no al de la sala.
///
/// La cuenta de servilleta es `N x k`, donde k es cuanta gente tienes dentro del radio. En una sala
/// k vale N-1 y por eso sale el cuadrado; en parejas k vale 1 y sale lineal. El cuadrado no es una
/// propiedad del numero de jugadores, es una propiedad de la AGLOMERACION.
///
/// # Lo que dio (2026-09-12)
///
/// Veinte personas, cambiando SOLO como se agrupan:
///
/// ```text
///   10 corrillos de  2  ->    60,4 KB/s
///    5 corrillos de  4  ->   138,9 KB/s
///    4 corrillos de  5  ->   178,1 KB/s
///    2 corrillos de 10  ->   374,4 KB/s
///    1 corrillo  de 20  ->   788,1 KB/s
/// ```
///
/// Trece veces de diferencia con el MISMO censo. El corrillo de 20 clava el control de `SameRoom`
/// al decimal (788,1), que es lo que valida que el reparto nuevo mide lo que dice.
///
/// Dividido entre pares dirigidos (N x (grupo-1)) sale casi constante: 2,07 KB/s por par en el
/// corrillo de 20 contra 3,02 en parejas. **Las parejas salen algo MAS caras por par**, y tiene
/// explicacion: desde ADR-140 D4 cada destinatario recibe UN datagrama con todas las poses que le
/// tocan, asi que un destino con 19 fuentes reparte la cabecera entre 19 y uno con 1 fuente la
/// paga entera. Amortizacion de cabecera, no un efecto del filtro.
///
/// Techo con todo el mundo emparejado: 320 aguanta, 340 revienta, y **el muro es el CABLE**, no la
/// CPU. Es la diferencia con la gente del todo sola, donde los bytes son literalmente cero y el
/// techo (~3.500) lo pone el bucle. Emparejados, cada uno relaya a su pareja, asi que los bytes
/// existen y crecen en linea recta: 3,02 KB/s por persona, y 1024/3,02 = 339 predice el reventon
/// exacto.
///
/// Los tres techos juntos, que es el resumen util:
///
/// ```text
///   todos en una sala     ~22   <- cable
///   todos emparejados    ~330   <- cable
///   todos solos        ~3.500   <- CPU
/// ```
///
/// `cargo test --bin backrooms_server cost_by_how_people_group_up -- --ignored --nocapture`
#[tokio::test]
#[ignore = "arnés de carga: se corre a mano, no es una regresión"]
async fn cost_by_how_people_group_up() {
    const ROUNDS: u32 = 30;

    async fn measure(count: usize, spread: Spread) -> (f64, f64) {
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        register_synthetic_peers(&mut host, count, spread);
        let before = super::send::sent_bytes_total();
        let started = std::time::Instant::now();
        for tick in 0..ROUNDS as usize {
            step_all_peers(&mut host, tick);
            super::sync::broadcast_peer_poses(&mut host, None).await;
        }
        let ms = started.elapsed().as_secs_f64() * 1000.0 / ROUNDS as f64;
        let kb = (super::send::sent_bytes_total() - before) as f64 / 1024.0;
        (kb, ms)
    }

    println!("\n=== 1. VEINTE personas, repartidas de distintas maneras ===\n");
    for group in [2usize, 4, 5, 10, 20] {
        let (kb, ms) = measure(20, Spread::Clustered { group }).await;
        let corrillos = 20 / group;
        println!(
            "  20 en {corrillos:>2} corrillos de {group:>2}  ->  {kb:>7.1} KB/s ({:>4.0} %)   {ms:>6.3} ms/ronda",
            100.0 * kb / STEAM_SEND_RATE_MAX_KB_S
        );
    }
    let (kb_room, ms_room) = measure(20, Spread::SameRoom).await;
    println!(
        "  20 en UNA sala (control)       ->  {kb_room:>7.1} KB/s ({:>4.0} %)   {ms_room:>6.3} ms/ronda",
        100.0 * kb_room / STEAM_SEND_RATE_MAX_KB_S
    );

    println!("\n=== 2. Techo con la gente EMPAREJADA (corrillos de 2) ===\n");
    let mut last_ok: Option<usize> = None;
    for count in [20usize, 50, 100, 200, 260, 300, 320, 340, 360, 400] {
        let (kb, ms) = measure(count, Spread::Clustered { group: 2 }).await;
        println!(
            "  N={count:>5}  ->  {kb:>8.1} KB/s ({:>5.0} %)   {ms:>7.3} ms/ronda ({:>5.1} %)",
            100.0 * kb / STEAM_SEND_RATE_MAX_KB_S,
            100.0 * ms / TICK_BUDGET_MS
        );
        if kb > STEAM_SEND_RATE_MAX_KB_S {
            println!("\n  REVENTADO en N={count}: el muro fue el CABLE.\n");
            return;
        }
        if ms > TICK_BUDGET_MS {
            println!("\n  REVENTADO en N={count}: el muro fue la CPU del emisor.\n");
            return;
        }
        last_ok = Some(count);
    }
    println!(
        "\n  NO reventó: aguantó hasta N={:?}, el último escalón probado.\n",
        last_ok.unwrap_or(0)
    );
}

/// **De que esta hecha una pose, byte a byte.** Sin esto, "recortar detalle a lo que no miras" es
/// una idea sin cifra: no se puede decidir que quitar hasta saber que cuesta cada cosa.
///
/// Mide sobre `rmp_serde`, el serializador de verdad, no sobre una estimacion de tamanyos de tipo.
///
/// # Lo que NO se puede quitar, y es la mitad util del resultado
///
/// La pose no lleva solo aspecto: lleva CONTADORES DE EVENTO (`hit_seq`, `vocal_seq`, `fire_seq`,
/// `melee_seq`). Son justo lo que hay que saber de quien tienes detras — un disparo, un golpe, una
/// vocalizacion. Recortarlos no ahorra bytes, borra eventos. Se quedan.
///
/// Lo que si sobra a la espalda es aspecto puro: la animacion (que ademas el cliente deriva de la
/// velocidad desde ADR-013, no de este campo), el pitch de camara, la ropa y lo que lleva en la
/// mano. Nada de eso cambia lo que pasa; solo lo que se ve, y no lo estas viendo.
///
/// `cargo test --bin backrooms_server pose_byte_breakdown -- --ignored --nocapture`
#[tokio::test]
#[ignore = "arnés de carga: se corre a mano, no es una regresión"]
async fn pose_byte_breakdown() {
    use super::protocol::PacketPayload;

    fn full() -> PacketPayload {
        PacketPayload::PlayerUpdate {
            position: [1234.5, 1.8, -987.25],
            rotation: 137.5,
            animation: super::protocol::PoseAnim::WALK_SLOW,
            crouch: false,
            pitch: -12,
            equipment: [101, 202, 303, 404],
            held_item: 77,
            hit_seq: 3,
            dead: false,
            revealed: false,
            vocal_seq: 5,
            vocal_kind: 2,
            light_on: true,
            fire_seq: 9,
            buttons: 0b0010_1101,
            melee_seq: 4,
            carry_def: 12,
            carry_count: 2,
            species: 0,
        }
    }

    /// La misma pose con SOLO el aspecto vaciado. Los contadores de evento siguen todos.
    fn skinny() -> PacketPayload {
        match full() {
            PacketPayload::PlayerUpdate {
                position,
                rotation,
                crouch,
                hit_seq,
                dead,
                revealed,
                vocal_seq,
                vocal_kind,
                light_on,
                fire_seq,
                buttons,
                melee_seq,
                species,
                ..
            } => PacketPayload::PlayerUpdate {
                position,
                rotation,
                animation: super::protocol::PoseAnim::IDLE,
                crouch,
                pitch: 0,
                equipment: [0; 4],
                held_item: 0,
                hit_seq,
                dead,
                revealed,
                vocal_seq,
                vocal_kind,
                light_on,
                fire_seq,
                buttons,
                melee_seq,
                carry_def: 0,
                carry_count: 0,
                species,
            },
            _ => unreachable!(),
        }
    }

    let n_full = rmp_serde::to_vec(&full()).unwrap().len();
    let n_skinny = rmp_serde::to_vec(&skinny()).unwrap().len();

    println!("\n=== De qué está hecha una pose (MessagePack real) ===\n");
    println!("  pose completa           {n_full:>4} B");
    println!("  pose flaca (sin aspecto){n_skinny:>4} B");
    println!(
        "  ahorro                  {:>4} B  ({:.0} % del total)",
        n_full - n_skinny,
        100.0 * (n_full - n_skinny) as f64 / n_full as f64
    );

    println!("\n  Campo a campo, lo que cuesta vaciar cada cosa:");
    for (name, payload) in [
        ("animation (ADR-143: u8)", {
            let mut p = full();
            if let PacketPayload::PlayerUpdate { animation, .. } = &mut p {
                *animation = super::protocol::PoseAnim::IDLE;
            }
            p
        }),
        ("equipment [i32;4]", {
            let mut p = full();
            if let PacketPayload::PlayerUpdate { equipment, .. } = &mut p {
                *equipment = [0; 4];
            }
            p
        }),
        ("held_item + carry", {
            let mut p = full();
            if let PacketPayload::PlayerUpdate {
                held_item,
                carry_def,
                carry_count,
                ..
            } = &mut p
            {
                *held_item = 0;
                *carry_def = 0;
                *carry_count = 0;
            }
            p
        }),
        ("pitch (i8)", {
            let mut p = full();
            if let PacketPayload::PlayerUpdate { pitch, .. } = &mut p {
                *pitch = 0;
            }
            p
        }),
    ] {
        let n = rmp_serde::to_vec(&payload).unwrap().len();
        println!("    {name:<22} -> -{:>2} B", n_full - n);
    }
    println!();
}
