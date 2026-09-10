use super::*;
use std::time::Duration;

// ADR-136: el bloque de identidad del handshake vive en `handlers`, que no lo reexporta.
use super::handlers::HandshakeIdentity;

// `SessionConfig` ya no lo importa `mod.rs` (lo consume `handlers.rs`), y `use super::*`
// solo alcanza lo que está en el ámbito de `mod.rs`.
use super::protocol::SessionConfig;

/// Get the loopback address for a NetworkManager (replaces 0.0.0.0 with 127.0.0.1).
fn loopback_addr(net: &NetworkManager) -> SocketAddr {
    let mut addr = net.local_addr();
    addr.set_ip(std::net::Ipv4Addr::LOCALHOST.into());
    addr
}

#[tokio::test]
async fn bind_and_local_addr() {
    let net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let addr = net.local_addr();
    assert_ne!(addr.port(), 0);
}

// P0-2: the joiner's own PHANTOM_DENSITY_SCALE never mattered to the draw before this (only the
// host ever calls it), but it must not survive the handshake either — the host's value always
// wins, same precedent as world_seed. Proven end-to-end: two DIFFERING local values, real
// handshake, then the same deterministic draw over both post-adoption values must agree — which
// it would NOT if the joiner had kept its own.
#[tokio::test]
async fn joiner_adopts_the_hosts_phantom_density_scale_and_it_changes_the_draw() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.phantom_density_scale = 8.0;
    let host_addr = loopback_addr(&host);

    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner.phantom_density_scale = 1.0; // differs from the host's on purpose

    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;

    assert_eq!(
        joiner.phantom_density_scale, 8.0,
        "the joiner must adopt the host's value, not keep its own"
    );

    // The draw is a pure function of its arguments (world_seed, block, layer, density_scale) —
    // see phantom_spawn::draw_into's doc-comment. With BOTH sides now agreeing on the value,
    // both must draw the identical population for the same block/layer.
    let mut host_drawn = Vec::new();
    crate::world::phantom_spawn::draw_into(
        host.world_seed,
        (0, 0),
        0,
        host.phantom_density_scale,
        false,
        &mut host_drawn,
    );
    let mut joiner_drawn = Vec::new();
    crate::world::phantom_spawn::draw_into(
        joiner.world_seed,
        (0, 0),
        0,
        joiner.phantom_density_scale,
        false,
        &mut joiner_drawn,
    );
    assert_eq!(
        host_drawn, joiner_drawn,
        "same world_seed + same adopted density_scale must draw the same population"
    );

    // Negative control: the joiner's OWN pre-adoption value would have drawn something
    // different — otherwise the assertion above would be vacuous (both empty, or a value this
    // block/layer's density happens not to affect).
    let mut joiner_drawn_with_own_value = Vec::new();
    crate::world::phantom_spawn::draw_into(
        joiner.world_seed,
        (0, 0),
        0,
        1.0,
        false,
        &mut joiner_drawn_with_own_value,
    );
    assert_ne!(
        host_drawn, joiner_drawn_with_own_value,
        "setup bug: density_scale 8.0 vs 1.0 must draw a different population at (0,0)/layer 0, \
         or this test cannot tell adoption apart from coincidence"
    );
}

#[tokio::test]
async fn two_peers_handshake_and_sync() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.local_name = "HostPlayer".into();
    let host_addr = loopback_addr(&host);

    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner.local_name = "JoinerPlayer".into();

    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;

    let host_events = host.process_incoming().await;
    assert!(
        host_events
            .iter()
            .any(|e| matches!(e, NetworkEvent::PeerConnected { .. })),
        "host should see PeerConnected, got: {host_events:?}"
    );
    assert_eq!(host.peer_count(), 1);

    tokio::time::sleep(Duration::from_millis(100)).await;
    let joiner_events = joiner.process_incoming().await;
    assert!(
        joiner_events
            .iter()
            .any(|e| matches!(e, NetworkEvent::PeerConnected { .. })),
        "joiner should see PeerConnected, got: {joiner_events:?}"
    );
    assert_eq!(joiner.peer_count(), 1);
    assert_ne!(joiner.local_id, 0, "joiner should have an assigned ID");
}

/// ADR-045 Fase 2: the host knows its `world_seed` from its own launch args at construction; a
/// joiner does not until the host tells it via `HandshakeAck`. `world_seed_known` exists so
/// player-file resolution (`game_loop::run`) can poll a plain field instead of inferring the
/// moment from an event — this fixes the STARTING value both roles construct with.
#[tokio::test]
async fn world_seed_known_starts_true_for_host_false_for_joiner() {
    let host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    assert!(
        host.world_seed_known,
        "the host already knows its own seed at construction"
    );

    let joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    assert!(
        !joiner.world_seed_known,
        "a joiner does not know the world's seed until the host's HandshakeAck arrives"
    );
}

/// Contrapartida sobre sockets reales: tras un handshake completo, el JOINER debe tener
/// `world_seed_known == true` — es la señal exacta que `game_loop::run` espera antes de intentar
/// resolver la ruta del fichero de jugador de ADR-045 Fase 2.
#[tokio::test]
async fn handshake_ack_marks_world_seed_known_for_the_joiner() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);

    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    assert!(!joiner.world_seed_known);

    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;

    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;

    assert!(
        joiner.world_seed_known,
        "world_seed_known must flip to true once the joiner has processed the HandshakeAck"
    );
    assert_eq!(
        joiner.world_seed, 42,
        "and it must carry the host's actual seed"
    );
}

/// ADR-056: the starting value of `host_peer_id` for both roles. A host points at nobody (it IS
/// the host); a joiner does not know the host's peer id until the `HandshakeAck` arrives. Same
/// shape as `world_seed_known` above, and pinned for the same reason: `PeerDisconnected` polls
/// this plain field instead of hardcoding peer `1`.
#[tokio::test]
async fn host_peer_id_starts_none_for_both_roles() {
    let host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    assert!(
        host.host_peer_id.is_none(),
        "the host has no host peer to point at"
    );

    let joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    assert!(
        joiner.host_peer_id.is_none(),
        "a joiner does not know the host's peer id until the HandshakeAck arrives"
    );
}

/// Over real sockets: after a full handshake the joiner must know WHICH peer is the host, and it
/// must be the same peer it registered — that identity is what lets a later disconnect be told
/// apart from any other peer leaving.
#[tokio::test]
async fn handshake_ack_records_the_host_peer_id_for_the_joiner() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);

    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    assert!(joiner.host_peer_id.is_none());

    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;

    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;

    let host_id = joiner
        .host_peer_id
        .expect("host_peer_id must be set once the HandshakeAck is processed");
    assert!(
        joiner.peers.contains_key(&host_id),
        "the recorded host id must be a peer the joiner actually tracks"
    );
    assert!(
        host.host_peer_id.is_none(),
        "the host itself never records one, even after peers connect"
    );
}

/// ADR-056: the goodbye reaches peers as a real `Disconnect`, so they act on the departure
/// immediately instead of waiting out the 5 s heartbeat timeout. Uses real sockets because the
/// point of the test is that the datagram is on the wire BEFORE the sender would exit — nothing
/// re-sends it later.
#[tokio::test]
async fn goodbye_broadcast_disconnects_peers_without_waiting_for_timeout() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);

    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;
    assert_eq!(
        joiner.peer_count(),
        1,
        "joiner should be connected to start"
    );

    super::sync::broadcast_goodbye(&host, "clean_shutdown").await;

    tokio::time::sleep(Duration::from_millis(100)).await;
    let events = joiner.process_incoming().await;

    assert!(
        events.iter().any(|e| matches!(
            e,
            NetworkEvent::PeerDisconnected { reason, .. } if reason == "clean_shutdown"
        )),
        "the goodbye must raise PeerDisconnected carrying its reason, got: {events:?}"
    );
    assert_eq!(
        joiner.peer_count(),
        0,
        "and the departing peer must be gone, not merely reported"
    );
}

/// The goodbye must never be addressed to a phantom (ADR-016/043): their `addr` is an inert
/// loopback port and every datagram aimed at one poisons the sender's socket on Windows. It
/// reuses `broadcast_destinations`, and this pins that it keeps doing so.
#[tokio::test]
async fn goodbye_destinations_exclude_phantoms() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let real_id = 2;
    host.peers.insert(
        real_id,
        PeerConnection::new(real_id, "Joiner".into(), "127.0.0.1:9999".parse().unwrap()),
    );
    let phantom_id = host.spawn_phantom("Skinwalker", [0.0, 0.0, 0.0], None);

    let dests = host.broadcast_destinations();
    assert!(
        dests.iter().any(|(id, _)| *id == real_id),
        "the real peer must be addressed"
    );
    assert!(
        !dests.iter().any(|(id, _)| *id == phantom_id),
        "a phantom must never be addressed by the goodbye"
    );
}

// ADR-028 Fase E: the corpse relay's three network hops over real sockets —
// (1) joiner → host CorpseSpawnRequest (reliable), (2) host → all CorpseList
// broadcast (mirror), (3) host → requester CorpseTakeResult (reliable) surfacing
// as the requester-side event. Authority/dedupe logic is unit-tested in game_loop
// (apply_corpse_* helpers); this covers the wire + handle_packet event mapping.
#[tokio::test]
async fn corpse_relay_hops_round_trip_between_peers() {
    use crate::world::corpse::CorpseStack;

    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 1004, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;
    assert_eq!(joiner.local_id, 1004);

    // Hop 1: joiner forwards its death-loot snapshot to the host.
    let spawn = PacketPayload::CorpseSpawnRequest {
        request_id: 1,
        requester_id: joiner.local_id,
        owner_name: "Joel".into(),
        position: [-22.0, 1.8, 9.0],
        equipment: [0, -1, -2, -3],
        held_item: -99,
        items: vec![CorpseStack {
            item_id: -12345,
            quantity: 3,
            props: Vec::new(),
        }],
    };
    joiner.send_reliable(1, &spawn).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    let host_events = host.process_incoming().await;
    assert!(
        host_events.iter().any(|e| matches!(
            e,
            NetworkEvent::CorpseSpawnRequest {
                request_id: 1,
                requester_id: 1004,
                ..
            }
        )),
        "host should receive the spawn request, got: {host_events:?}"
    );

    // Hop 2: host broadcasts the roster; the joiner mirrors it.
    let mut world = crate::world::World::new(42);
    let corpse_id = world.spawn_corpse(
        1004,
        "Joel".into(),
        crate::utils::Vec3::new(-22.0, 1.8, 9.0),
        [0, -1, -2, -3],
        -99,
        vec![CorpseStack {
            item_id: -12345,
            quantity: 3,
            props: Vec::new(),
        }],
    );
    super::sync::broadcast_corpses(&mut host, &world).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    let joiner_events = joiner.process_incoming().await;
    let mirrored = joiner_events.iter().find_map(|e| match e {
        NetworkEvent::CorpseListReceived { corpses } => Some(corpses),
        _ => None,
    });
    let mirrored = mirrored.expect("joiner should receive the corpse roster");
    assert_eq!(mirrored.len(), 1);
    assert_eq!(mirrored[0].id, corpse_id);
    assert_eq!(mirrored[0].owner_name, "Joel");
    assert_eq!(mirrored[0].items[0].item_id, -12345);

    // Hop 3: host sends the take verdict back to the requester only.
    let verdict = PacketPayload::CorpseTakeResult {
        request_id: 2,
        accepted: true,
        corpse_id,
        item_index: 0,
        item_id: -12345,
        quantity: 1,
        corpse_empty: false,
        reason: String::new(),
    };
    host.send_reliable(1004, &verdict).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    let joiner_events = joiner.process_incoming().await;
    assert!(
        joiner_events.iter().any(|e| matches!(
            e,
            NetworkEvent::CorpseTakeResult {
                request_id: 2,
                accepted: true,
                item_id: -12345,
                ..
            }
        )),
        "joiner should receive the take verdict, got: {joiner_events:?}"
    );
}

#[tokio::test]
async fn host_honors_requested_peer_id_when_available() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);

    let mut joiner = NetworkManager::bind(0, 4876, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;

    host.process_incoming().await;
    assert!(
        host.peers.contains_key(&4876),
        "host should register the joiner by requested NET_ID"
    );

    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;
    assert_eq!(joiner.local_id, 4876);
}

#[tokio::test]
async fn host_assigns_fallback_id_on_requested_id_conflict() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);

    let mut joiner = NetworkManager::bind(0, 1, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;

    host.process_incoming().await;
    let assigned_id = host
        .peers
        .keys()
        .next()
        .copied()
        .expect("host should register a peer");
    assert_ne!(assigned_id, 1);

    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;
    assert_eq!(joiner.local_id, assigned_id);
}

#[tokio::test]
async fn player_update_round_trip() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;

    let payload = PacketPayload::PlayerUpdate {
        position: [10.0, 1.8, 20.0],
        rotation: 45.0,
        animation: "walk".into(),
        crouch: false,
        pitch: 0,
        equipment: [0; 4],
        held_item: 0,
        hit_seq: 0,
        dead: false,
        revealed: false,
        vocal_seq: 0,
        vocal_kind: 0,
        light_on: false,
        fire_seq: 0,
        buttons: 0,
        melee_seq: 0,
        carry_def: 0,
        carry_count: 0,
        species: 0,
    };
    host.broadcast_unreliable(&payload).await;

    tokio::time::sleep(Duration::from_millis(100)).await;

    let events = joiner.process_incoming().await;
    let update = events
        .iter()
        .find(|e| matches!(e, NetworkEvent::RemotePlayerUpdate { .. }));
    assert!(
        update.is_some(),
        "joiner should see player update, got: {events:?}"
    );
    if let Some(NetworkEvent::RemotePlayerUpdate {
        position, rotation, ..
    }) = update
    {
        assert_eq!(*position, [10.0, 1.8, 20.0]);
        assert_eq!(*rotation, 45.0);
    }
}

/// LA COMPROBACIÓN PENDIENTE de `systems/perf-baseline.md`: ¿converge hoy el estado del mundo con
/// una base mediana, o está roto y no nos hemos enterado?
///
/// ```text
/// cargo test --release five_rosters_converge -- --ignored --nocapture
/// ```
///
/// Por qué no lo responde ningún test existente: los que hay emiten UN roster aislado. El juego
/// emite los CINCO seguidos en la misma ronda (`game_loop.rs`), y la medida que ADR-060 (d) dejó
/// escrita —"a partir de ~56 páginas empezaba a perderse al menos una por ronda"— se hizo sobre un
/// roster solo. Con una base de 1000 piezas son ~124 páginas de golpe entre dos rosters, y ~124 KB
/// contra un buffer de recepción de ~64 KB.
///
/// Sondea, no afirma: mide en cuántas rondas converge cada roster sobre sockets UDP reales. Si
/// alguno no converge, el juego tiene hoy un mundo que no se replica, y eso pesa más que cualquier
/// número de jugadores.
#[tokio::test]
#[ignore = "sonda de medición: imprime, no afirma"]
async fn five_rosters_converge() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 2001, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;

    // Barrido: la "base seria" de perf-baseline.md y múltiplos de ella, para encontrar DÓNDE
    // rompe en vez de solo comprobar un punto. Un mundo que converge a 1× y no a 6× tiene un
    // techo, y saber dónde está vale más que saber que hoy va bien.
    for scale in [1usize, 3, 6, 10] {
        five_rosters_converge_at(&mut host, &mut joiner, scale).await;
    }
}

async fn five_rosters_converge_at(
    host: &mut NetworkManager,
    joiner: &mut NetworkManager,
    scale: usize,
) {
    // La "base seria" de perf-baseline.md, más los otros tres rosters con una población plausible.
    let buildings_n = 1000 * scale;
    let items_n = 300 * scale;
    let carryables_n = 200 * scale;
    let harvestables_n = 100 * scale;
    host.stp_buildings = (0..buildings_n as u32)
        .map(|id| protocol::StpBuildingInfo {
            id,
            def_id: -4996552,
            position: [id as f32, 1.8, 0.0],
            rotation: 90.0,
            group_id: id / 8,
            owner_id: 0,
            added: vec![protocol::StpBuildProgress {
                material_id: -1234,
                count: 4,
            }],
        })
        .collect();
    host.stp_items = (0..items_n as u32)
        .map(|id| protocol::StpItemInfo {
            id,
            def_id: -52379,
            count: 3,
            position: [id as f32, 1.8, 0.0],
            rotation: 0.0,
            settling: false,
        })
        .collect();
    host.stp_carryables = (0..carryables_n as u32)
        .map(|id| protocol::StpCarryableInfo {
            id,
            def_id: 7,
            position: [id as f32, 1.8, 0.0],
            rotation: 0.0,
        })
        .collect();
    host.stp_harvestables = (0..harvestables_n as u32)
        .map(|id| protocol::StpHarvestableInfo {
            id,
            position: [id as f32, 1.8, 0.0],
            remaining: 3.0,
        })
        .collect();

    println!("--- x{scale}: {buildings_n} piezas, {items_n} items, {carryables_n} carryables, {harvestables_n} harvestables ---");

    const ROUNDS: usize = 30;
    let mut converged_at = [None::<usize>; 4];
    for round in 1..=ROUNDS {
        // ADR-071: el gate cortaría de la ronda 4 en adelante. Se rearma porque lo que se mide
        // aquí es la CONVERGENCIA del transporte, no la cadencia — con el gate puesto, el juego
        // real reintenta cada 3 s en vez de cada 100 ms, así que estas rondas son las del latido.
        host.roster_gates = Default::default();
        sync::broadcast_stp_items(host).await;
        sync::broadcast_stp_buildings(host).await;
        sync::broadcast_stp_carryables(host).await;
        sync::broadcast_stp_harvestables(host).await;
        tokio::time::sleep(Duration::from_millis(30)).await;
        joiner.process_incoming().await;

        for (i, (got, want)) in [
            (joiner.stp_items.len(), items_n),
            (joiner.stp_buildings.len(), buildings_n),
            (joiner.stp_carryables.len(), carryables_n),
            (joiner.stp_harvestables.len(), harvestables_n),
        ]
        .into_iter()
        .enumerate()
        {
            if converged_at[i].is_none() && got == want {
                converged_at[i] = Some(round);
            }
        }
        if converged_at.iter().all(|c| c.is_some()) {
            break;
        }
    }

    for (name, at, got, want) in [
        ("items", converged_at[0], joiner.stp_items.len(), items_n),
        (
            "buildings",
            converged_at[1],
            joiner.stp_buildings.len(),
            buildings_n,
        ),
        (
            "carryables",
            converged_at[2],
            joiner.stp_carryables.len(),
            carryables_n,
        ),
        (
            "harvestables",
            converged_at[3],
            joiner.stp_harvestables.len(),
            harvestables_n,
        ),
    ] {
        match at {
            Some(r) => println!("  {name:<13} convergió en la ronda {r} ({got}/{want})"),
            None => println!(
                "  {name:<13} NO CONVERGIÓ en {ROUNDS} rondas ({got}/{want}) ← roster roto"
            ),
        }
    }
    println!();
}

/// SONDA DE MEDICIÓN (no afirma, imprime). Cierra la aritmética que `systems/perf-baseline.md`
/// dejó a medias: el relay de poses es O(N²) y nadie lo había medido.
///
/// ```text
/// cargo test --release pose_relay_cost -- --ignored --nocapture
/// ```
///
/// Por qué se mide aparte de los rosters: el coste de los rosters crece con el TAMAÑO DEL MUNDO
/// (lineal en jugadores); éste crece con el CUADRADO de los jugadores y es indiferente al mundo.
/// Son las dos curvas que deciden el techo, y se cruzan en algún punto — este número dice dónde.
#[tokio::test]
#[ignore = "sonda de medición: imprime, no afirma"]
async fn pose_relay_cost() {
    let pose = PacketPayload::PlayerUpdate {
        position: [10.0, 1.8, 20.0],
        rotation: 45.0,
        animation: "walk".into(),
        crouch: false,
        pitch: 3,
        equipment: [1, 2, 3, 4],
        held_item: -52379,
        hit_seq: 7,
        dead: false,
        revealed: false,
        vocal_seq: 2,
        vocal_kind: 1,
        light_on: true,
        fire_seq: 5,
        buttons: 3,
        melee_seq: 4,
        carry_def: -111,
        carry_count: 2,
        species: 0,
    };
    // +12 B de cabecera de paquete (ver el doc de ROSTER_PAGE_BUDGET_BYTES).
    let bytes = rmp_serde::to_vec_named(&pose).unwrap().len() + 12;
    const RELAY_HZ: f64 = 10.0;

    println!("\n=== sonda: coste del relay de poses (ADR-015), O(N²) ===");
    println!("PlayerUpdate serializado + cabecera: {bytes} B | relay: {RELAY_HZ} Hz\n");
    println!("  peers | datagramas/s | poses del host | + rosters = total salida");
    for n in [2usize, 4, 8, 16, 32, 64] {
        // Cada destino real recibe la pose de todos MENOS la suya: n×(n−1) por ronda.
        let per_round = n * n.saturating_sub(1);
        let dgrams = per_round as f64 * RELAY_HZ;
        let poses_kbps = per_round as f64 * bytes as f64 * RELAY_HZ / 1024.0;
        let rosters_kbps = 94.0 * n as f64; // medido en perf-baseline.md, tras ADR-071
        println!(
            "  {n:>5} | {dgrams:>12.0} | {poses_kbps:>9.0} KB/s | {:>6.1} Mbps",
            (poses_kbps + rosters_kbps) * 8.0 / 1024.0
        );
    }
    println!("\n(rosters = base seria de 1000 piezas con un jugador construyendo, tras ADR-071)");
}

/// ADR-070: the throw impulse has to SURVIVE the joiner→host hop over a real socket. It is the
/// only piece of a drop the host cannot reconstruct — position it could clamp, rotation it could
/// ignore, but a velocity that decodes to zero silently downgrades every joiner's throw into a
/// vertical drop, and nothing would fail. Non-default on all three axes on purpose: a per-axis
/// serialization slip would otherwise hide behind a symmetric vector.
#[tokio::test]
async fn stp_drop_request_carries_the_throw_velocity_across_the_wire() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 1007, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;

    let payload = PacketPayload::StpDropRequest {
        drop_id: 77,
        def_id: -52379,
        count: 3,
        position: [4.0, 2.1, -6.0], // the HAND, not a resting place (ADR-070 decision 1)
        rotation: 90.0,
        velocity: [1.5, 3.25, -2.75],
    };
    joiner.send_reliable(1, &payload).await;
    tokio::time::sleep(Duration::from_millis(150)).await;

    let events = host.process_incoming().await;
    let drop = events
        .iter()
        .find(|e| matches!(e, NetworkEvent::StpDropRequest { .. }));
    assert!(
        drop.is_some(),
        "host should see the drop request, got: {events:?}"
    );
    if let Some(NetworkEvent::StpDropRequest {
        position,
        velocity,
        requester_id,
        ..
    }) = drop
    {
        assert_eq!(*position, [4.0, 2.1, -6.0]);
        assert_eq!(
            *velocity,
            [1.5, 3.25, -2.75],
            "the impulse must arrive intact — a zeroed one is an invisible downgrade to a dead drop"
        );
        // El emisor sale de la CABECERA del datagrama y no del payload —que no lo lleva—, y es lo
        // que permite medir el drop contra la pose que el host tiene de ESE peer. Si esto se
        // rellenara con un dato elegido por quien envía, la puerta de proximidad no validaría nada.
        assert_eq!(
            *requester_id, 1007,
            "el drop tiene que llegar atribuido a quien lo mandó"
        );
    }
}

/// ADR-043's rule ("a phantom is a sender, never a receiver") applied to the last broadcast
/// that still ignored it. A regression here is not cosmetic and does not look like a network
/// bug: aiming a datagram at a phantom's inert `127.0.0.1:1` floods the sender's own socket
/// with WSAECONNRESET, and since the host's OWN pose and the peer roster are the two things
/// that ride `broadcast_unreliable`, the host goes invisible to every joiner while the joiners
/// keep seeing each other through the already-filtered relay.
#[tokio::test]
async fn a_phantom_is_never_a_broadcast_destination() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;

    let joiner_id = *host.peers.keys().next().expect("joiner should be a peer");
    let phantom_id = host.spawn_phantom("Victima", [2.0, 1.8, 0.0], None);

    let dests = host.broadcast_destinations();
    assert!(
        dests.iter().any(|(id, _)| *id == joiner_id),
        "the real joiner must still receive broadcasts, got: {dests:?}"
    );
    assert!(
        !dests.iter().any(|(id, _)| *id == phantom_id),
        "a phantom must never be a broadcast destination, got: {dests:?}"
    );
    // Assert the ADDRESS too, not just the id: port 1 is the inert loopback port whose ICMP
    // unreachable is the actual failure mode, so this catches a future phantom that gets a
    // real id but keeps the dead addr.
    assert!(
        !dests.iter().any(|(_, addr)| addr.port() == 1),
        "no broadcast may be aimed at the inert phantom port, got: {dests:?}"
    );
}

#[tokio::test]
async fn reliable_packet_ack() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;

    let joiner_id = host
        .peers
        .keys()
        .next()
        .copied()
        .expect("host should have a peer");

    let payload = PacketPayload::Disconnect {
        reason: "test".into(),
    };
    host.send_reliable(joiner_id, &payload).await;
    assert_eq!(host.peers[&joiner_id].reliable_queue.len(), 1);

    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;

    tokio::time::sleep(Duration::from_millis(100)).await;
    host.process_incoming().await;
    assert_eq!(
        host.peers.get(&joiner_id).map(|p| p.reliable_queue.len()),
        Some(0),
        "reliable queue should be empty after ACK"
    );
}

/// ADR-062: agotar MAX_RETRIES desconecta al peer. Antes se le retenía con la cola vaciada,
/// que es justamente el estado silencioso (conectado pero mudo por la vía reliable, sin evento
/// y sin recuperación) que el ADR elimina. El evento es lo que hace observable la caída: sin él
/// `game_loop` y Unity siguen renderizando a un jugador que ya no recibe mundo.
#[tokio::test]
async fn reliable_retransmit_exhaustion_evicts_peer() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let peer_id = 2;
    let addr: SocketAddr = "127.0.0.1:9999".parse().unwrap();
    host.peers
        .insert(peer_id, PeerConnection::new(peer_id, "Joiner".into(), addr));

    let payload = PacketPayload::Disconnect {
        reason: "test reliable packet".into(),
    };
    host.send_reliable(peer_id, &payload).await;

    {
        let peer = host.peers.get_mut(&peer_id).unwrap();
        for packet in peer.reliable_queue.iter_mut() {
            packet.retries = reliability::MAX_RETRIES;
            packet.next_retry_at = Instant::now() - Duration::from_millis(1);
        }
    }

    let events = host.process_retransmits().await;

    assert!(
        !host.peers.contains_key(&peer_id),
        "reliable retransmit exhaustion must evict the peer, not leave it connected and mute"
    );
    assert_eq!(
        events.len(),
        1,
        "the eviction must be observable as exactly one event, got: {events:?}"
    );
    assert!(
        matches!(
            &events[0],
            NetworkEvent::PeerDisconnected { id, reason }
                if *id == peer_id && reason == "reliable retransmit exhausted"
        ),
        "the event must carry its own reason, distinct from a heartbeat timeout, got: {:?}",
        events[0]
    );
}

/// ADR-062 §guarda de fantasmas: el robapieles (ADR-016) NO se evicta por esta vía — su ciclo de
/// vida lo gestiona el sistema phantom, y sacarlo del mapa desde la capa de red lo haría
/// desaparecer del mundo en silencio. Conserva el comportamiento heredado (purgar y seguir), que
/// el ADR declara explícitamente como no-verificado-seguro en vez de darlo por cubierto.
///
/// El foco aquí es el EVENTO: un fantasma nunca hizo handshake, así que emitir `PeerDisconnected`
/// por él haría que `game_loop` anunciara la salida de un jugador que nadie vio entrar. La purga
/// explícita de la cola aparcada en esa misma rama la fija
/// `exhausted_retries_purge_a_phantoms_deferred_queue_without_evicting_it`.
#[tokio::test]
async fn reliable_retransmit_exhaustion_does_not_evict_a_phantom() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let phantom_id = host.spawn_phantom("Skinwalker", [0.0, 1.8, 0.0], None);

    {
        let peer = host.peers.get_mut(&phantom_id).unwrap();
        peer.queue_reliable(0, vec![0u8; 8]);
        for packet in peer.reliable_queue.iter_mut() {
            packet.retries = reliability::MAX_RETRIES;
            packet.next_retry_at = Instant::now() - Duration::from_millis(1);
        }
    }

    let events = host.process_retransmits().await;

    assert!(
        host.peers.contains_key(&phantom_id),
        "a phantom must survive reliable exhaustion — the phantom system owns its lifecycle"
    );
    assert!(
        events.is_empty(),
        "a phantom raises no PeerDisconnected: it never handshook, so nothing consumes it"
    );
    assert_eq!(
        host.peers[&phantom_id].reliable_queue.len(),
        0,
        "its in-flight queue is still purged"
    );
}

/// H11 (auditoria): el carve-out de fantasmas en `process_retransmits` (ADR-062) solo importa
/// si la cola reliable de un fantasma puede llenarse en primer lugar — y en la práctica no
/// puede: `broadcast_destinations` (send.rs:78) y `broadcast_reliable` (send.rs:160) filtran
/// fantasmas ANTES de encolar nada. Fija el invariante desde el otro lado de
/// `reliable_retransmit_exhaustion_does_not_evict_a_phantom`: aquel prueba que la evicción no
/// le pasa a un fantasma; este prueba que la cola que dispararía esa evicción nunca debería
/// crecer para empezar.
#[tokio::test]
async fn broadcasts_skip_phantoms_so_their_reliable_queue_never_grows() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let real_addr: SocketAddr = "127.0.0.1:9700".parse().unwrap();
    host.peers
        .insert(2, PeerConnection::new(2, "Real".into(), real_addr));
    let phantom_id = host.spawn_phantom("Skinwalker", [0.0, 1.8, 0.0], None);

    assert!(
        !host
            .broadcast_destinations()
            .iter()
            .any(|(id, _)| *id == phantom_id),
        "un fantasma nunca es destino de broadcast_destinations"
    );

    host.broadcast_reliable(&PacketPayload::Heartbeat).await;

    assert_eq!(
        host.peers[&phantom_id].reliable_queue.len(),
        0,
        "broadcast_reliable no debe encolar nada para un fantasma"
    );
    assert_eq!(
        host.peers[&2].reliable_queue.len(),
        1,
        "un peer real SI recibe el broadcast reliable"
    );
}

#[tokio::test]
async fn peer_timeout_detection() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    // Manually add a peer with an old heartbeat.
    let addr: SocketAddr = "127.0.0.1:9999".parse().unwrap();
    let mut peer = PeerConnection::new(2, "OldPeer".into(), addr);
    peer.last_heartbeat = Instant::now() - Duration::from_secs(10);
    net.peers.insert(2, peer);

    let events = net.check_timeouts();
    assert_eq!(events.len(), 1);
    assert!(matches!(
        &events[0],
        NetworkEvent::PeerDisconnected { id: 2, .. }
    ));
    assert_eq!(net.peer_count(), 0);
}

/// `PeerId` gets reused after a disconnect (`allocate_peer_id` just hands out the next free
/// number), so any PeerId-keyed state that outlives the disconnect would silently apply to
/// whichever different player inherits the number next. Covers both call sites of
/// `purge_peer_state` implicitly (timeout here; the `Disconnect`-packet arm in `handlers.rs`
/// calls the same one-line helper) and includes a negative control — a peer that stays
/// connected must keep its state untouched, or a purge-everything bug would pass silently.
#[tokio::test]
async fn peer_timeout_purges_orphaned_peer_keyed_state() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();

    let gone_addr: SocketAddr = "127.0.0.1:9999".parse().unwrap();
    let mut gone = PeerConnection::new(2, "Gone".into(), gone_addr);
    gone.last_heartbeat = Instant::now() - Duration::from_secs(10);
    net.peers.insert(2, gone);
    net.voice_echo.insert(2, vec![1, 2, 3]);
    net.pending_struggles.insert(2);
    net.processed_corpse_requests.insert((2, 7));
    net.last_keepalive_trace_at.insert(2, Instant::now());
    net.last_transform_trace_at.insert(2, Instant::now());

    let staying_addr: SocketAddr = "127.0.0.1:9998".parse().unwrap();
    net.peers
        .insert(3, PeerConnection::new(3, "Staying".into(), staying_addr));
    net.voice_echo.insert(3, vec![9]);
    net.pending_struggles.insert(3);
    net.processed_corpse_requests.insert((3, 9));
    net.last_keepalive_trace_at.insert(3, Instant::now());
    net.last_transform_trace_at.insert(3, Instant::now());

    let events = net.check_timeouts();
    assert_eq!(events.len(), 1);

    assert!(
        !net.voice_echo.contains_key(&2),
        "voice_echo del peer desconectado debe purgarse"
    );
    assert!(
        !net.pending_struggles.contains(&2),
        "pending_struggles del peer desconectado debe purgarse"
    );
    assert!(
        !net.processed_corpse_requests.contains(&(2, 7)),
        "processed_corpse_requests del peer desconectado debe purgarse"
    );
    assert!(!net.last_keepalive_trace_at.contains_key(&2));
    assert!(!net.last_transform_trace_at.contains_key(&2));

    assert!(
        net.voice_echo.contains_key(&3),
        "el peer que sigue conectado no debe perder su estado"
    );
    assert!(net.pending_struggles.contains(&3));
    assert!(net.processed_corpse_requests.contains(&(3, 9)));
    assert!(net.last_keepalive_trace_at.contains_key(&3));
    assert!(net.last_transform_trace_at.contains_key(&3));
}

/// ADR-049. El único test de toda la cadena que caza el modo de fallo que el compilador NO ve.
///
/// La rama `PlayerUpdate` de `handle_packet` sella catorce campos cosméticos en el peer con
/// asignaciones sueltas, no con un literal de struct. Olvidar una compila limpio, pasa clippy y
/// pasa el resto de la suite — y deja el campo a 0 para siempre, que es exactamente el bug que
/// ADR-049 existe para cerrar: el peer sigue sin verse cargando nada. El round-trip de
/// `protocol.rs` tampoco lo caza, porque prueba el códec y no el sellado.
///
/// Valor no-default y NEGATIVO a propósito: los `def_id` se acuñan con `Random.Range` sobre todo
/// el rango de i32, así que un test con un positivo pequeño no distinguiría un error de signo.
#[tokio::test]
async fn player_update_carries_carry_state_to_the_peer() {
    let mut net = NetworkManager::bind(0, 3, 42, false).await.unwrap();
    let peer_addr: SocketAddr = "127.0.0.1:7100".parse().unwrap();
    net.peers
        .insert(2, PeerConnection::new(2, "PeerB".into(), peer_addr));

    assert_eq!(net.peers[&2].carry_def, 0, "arranca con las manos vacias");
    assert_eq!(net.peers[&2].carry_count, 0);

    let packet = IncomingPacket {
        addr: peer_addr,
        header: PacketHeader::new(protocol::PacketType::PlayerUpdate as u16, 2, 0, 0),
        payload: PacketPayload::PlayerUpdate {
            position: [1.0, 1.8, 2.0],
            rotation: 0.0,
            animation: "idle".into(),
            crouch: false,
            pitch: 0,
            equipment: [0; 4],
            held_item: 0,
            hit_seq: 0,
            dead: false,
            revealed: false,
            vocal_seq: 0,
            vocal_kind: 0,
            light_on: false,
            fire_seq: 0,
            buttons: 0,
            melee_seq: 0,
            carry_def: -1208217892,
            carry_count: 3,
            species: 0,
        },
    };
    net.handle_packet(packet).await;

    assert_eq!(
        net.peers[&2].carry_def, -1208217892,
        "el sello de carry_def en handle_packet no ha corrido"
    );
    assert_eq!(
        net.peers[&2].carry_count, 3,
        "el sello de carry_count en handle_packet no ha corrido"
    );
}

/// El relay de poses de ADR-015 reemite el `PlayerUpdate` de B hacia C desde el socket
/// DEL HOST, sellado con `sender_id = B`. Antes, `handle_packet` adoptaba esa `addr` sin
/// condición, así que C acababa creyendo que B vive en la dirección del host — diez veces
/// por segundo, y para todos los joiners a la vez. No quedaba ninguna ruta directa que
/// descubrir. Invisible con 1 host + 1 joiner, porque el relay sale por la puerta
/// `peers.len() < 2`.
#[tokio::test]
async fn relayed_packet_does_not_steal_the_relays_address() {
    let mut net = NetworkManager::bind(0, 3, 42, false).await.unwrap();
    let host_addr: SocketAddr = "127.0.0.1:7000".parse().unwrap();
    let peer_b_addr: SocketAddr = "127.0.0.1:7001".parse().unwrap();
    net.peers
        .insert(1, PeerConnection::new(1, "Host".into(), host_addr));
    net.peers
        .insert(2, PeerConnection::new(2, "PeerB".into(), peer_b_addr));

    // Llega la pose de B, pero por el socket del host (eso es exactamente lo que hace
    // send_unreliable_as).
    let relayed = IncomingPacket {
        addr: host_addr,
        header: PacketHeader::new(protocol::PacketType::PlayerUpdate as u16, 2, 0, 0),
        payload: PacketPayload::PlayerUpdate {
            position: [1.0, 1.8, 2.0],
            rotation: 0.0,
            animation: "idle".into(),
            crouch: false,
            pitch: 0,
            equipment: [0; 4],
            held_item: 0,
            hit_seq: 0,
            dead: false,
            revealed: false,
            vocal_seq: 0,
            vocal_kind: 0,
            light_on: false,
            fire_seq: 0,
            buttons: 0,
            melee_seq: 0,
            carry_def: 0,
            carry_count: 0,
            species: 0,
        },
    };
    net.handle_packet(relayed).await;

    assert_eq!(
        net.peers[&2].addr, peer_b_addr,
        "la direccion de B no puede ser suplantada por la del relay"
    );
    assert_eq!(net.peers[&1].addr, host_addr, "la del host no se toca");
    // Y la pose SI se aplica: rechazar la direccion no puede costar el dato.
    assert_eq!(net.peers[&2].position, [1.0, 1.8, 2.0]);
}

/// `can_queue_reliable` existia desde la Fase 3 y no lo llamaba nadie, asi que la cola
/// fiable crecia sin tope por peer. Importa porque al llegar a MAX_RETRIES el barrido la
/// vacia ENTERA (`peer.reliable_queue.clear()`), o sea que dejar que se llene no retrasa la
/// perdida: la agranda.
#[tokio::test]
async fn reliable_send_respects_the_window_instead_of_growing_unbounded() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let addr: SocketAddr = "127.0.0.1:7005".parse().unwrap();
    let mut peer = PeerConnection::new(2, "PeerB".into(), addr);
    for seq in 0..reliability::WINDOW_SIZE as u32 {
        peer.queue_reliable(seq, vec![0u8; 8]);
    }
    net.peers.insert(2, peer);
    assert!(
        !net.peers[&2].can_queue_reliable(),
        "ventana llena de partida"
    );

    net.send_reliable(2, &PacketPayload::Heartbeat).await;

    assert_eq!(
        net.peers[&2].reliable_queue.len(),
        reliability::WINDOW_SIZE,
        "con la ventana llena, el paquete nuevo se descarta — no se encola"
    );
}

/// `max_players` vivia en SessionConfig y en WorldConfig y no lo consultaba NADIE: el host
/// anunciaba 50 en su HandshakeAck y admitia peers indefinidamente.
#[tokio::test]
async fn handshake_is_rejected_when_the_session_is_full() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let capacity = (SessionConfig::default().max_players as usize).saturating_sub(1);
    for i in 0..capacity {
        let id = (i + 2) as PeerId;
        let addr: SocketAddr = format!("127.0.0.1:{}", 8000 + i).parse().unwrap();
        host.peers
            .insert(id, PeerConnection::new(id, format!("P{id}"), addr));
    }
    assert_eq!(host.real_peer_count(), capacity);

    let newcomer: SocketAddr = "127.0.0.1:9500".parse().unwrap();
    host.handle_handshake(
        newcomer,
        0,
        "TooMany".into(),
        crate::ipc::server::WIRE_SCHEMA_VERSION.to_string(),
        String::new(),
        HandshakeIdentity::default(),
    )
    .await;

    assert_eq!(
        host.real_peer_count(),
        capacity,
        "con el aforo lleno no se admite un peer mas"
    );
    assert!(
        !host.peers.values().any(|p| p.addr == newcomer),
        "el rechazado no puede quedar registrado"
    );
}

/// La contrapartida: con sitio libre, el handshake SI admite al peer nuevo.
#[tokio::test]
async fn handshake_is_accepted_when_there_is_room() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let newcomer: SocketAddr = "127.0.0.1:9501".parse().unwrap();

    host.handle_handshake(
        newcomer,
        0,
        "Joiner".into(),
        crate::ipc::server::WIRE_SCHEMA_VERSION.to_string(),
        String::new(),
        HandshakeIdentity::default(),
    )
    .await;

    assert_eq!(host.real_peer_count(), 1);
    assert!(host.peers.values().any(|p| p.addr == newcomer));
}

/// Corrección adosada a ADR-060 (docs/DECISIONS.md, 2026-08-10): antes de este gate `version`
/// se ignoraba por completo (`_version`). Un joiner con un schema distinto no debe quedar
/// registrado ni recibir un HandshakeAck.
#[tokio::test]
async fn handshake_is_rejected_on_wire_schema_mismatch() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let newcomer: SocketAddr = "127.0.0.1:9502".parse().unwrap();

    host.handle_handshake(
        newcomer,
        0,
        "OldBuild".into(),
        "0.1.0".into(),
        String::new(),
        HandshakeIdentity::default(),
    )
    .await;

    assert_eq!(
        host.real_peer_count(),
        0,
        "un mismatch de version no puede quedar registrado"
    );
    assert!(
        !host.peers.values().any(|p| p.addr == newcomer),
        "el rechazado no puede quedar registrado"
    );
}

/// Corrección adosada a ADR-060: el `Disconnect` pre-registro que rechaza un handshake (session
/// full o version mismatch) llegaba a un joiner que no tenía a nadie registrado en `self.peers`
/// para "desconectar" — se perdía en silencio y `retry_pending_connection` reenviaba el mismo
/// handshake cada 1s para siempre. Este es el único camino por el que un joiner puede aprender
/// que su conexión fue rechazada.
#[tokio::test]
async fn pending_connection_stops_retrying_after_rejection() {
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let host_addr: SocketAddr = "127.0.0.1:9600".parse().unwrap();
    joiner.pending_connect_addr = Some(host_addr);

    let rejection = IncomingPacket {
        addr: host_addr,
        header: PacketHeader::new(protocol::PacketType::Disconnect as u16, 1, 0, 0),
        payload: PacketPayload::Disconnect {
            reason: "session full".into(),
        },
    };
    let event = joiner.handle_packet(rejection).await;

    assert!(
        matches!(event, Some(NetworkEvent::ConnectRejected { reason }) if reason == "session full"),
        "el Disconnect pre-registro debe traducirse en ConnectRejected, no perderse"
    );
    assert_eq!(
        joiner.pending_connect_addr, None,
        "sin esto retry_pending_connection seguiria reenviando el handshake muerto"
    );
}

/// ADR-060: la variante encolada NO descarta con la ventana llena — aparca. El contraste con
/// `reliable_send_respects_the_window_instead_of_growing_unbounded` es la decision entera:
/// mismo estado de partida, destino distinto del paquete nuevo.
#[tokio::test]
async fn queued_reliable_send_defers_instead_of_dropping_when_the_window_is_full() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let addr: SocketAddr = "127.0.0.1:7015".parse().unwrap();
    let mut peer = PeerConnection::new(2, "PeerB".into(), addr);
    for seq in 0..reliability::WINDOW_SIZE as u32 {
        peer.queue_reliable(seq, vec![0u8; 8]);
    }
    net.peers.insert(2, peer);

    net.send_reliable_queued(2, &PacketPayload::Heartbeat).await;

    assert_eq!(
        net.peers[&2].reliable_queue.len(),
        reliability::WINDOW_SIZE,
        "la ventana en vuelo no crece"
    );
    assert_eq!(
        net.peers[&2].deferred_reliable.len(),
        1,
        "el paquete nuevo espera aparcado, no se descarta"
    );
}

/// ADR-060: con diferidos pendientes, un envio encolado nuevo se aparca AUNQUE la ventana tenga
/// hueco — saltarse la cola romperia el orden FIFO del lote.
#[tokio::test]
async fn queued_reliable_send_never_jumps_ahead_of_the_deferred_queue() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let addr: SocketAddr = "127.0.0.1:7016".parse().unwrap();
    let mut peer = PeerConnection::new(2, "PeerB".into(), addr);
    peer.defer_reliable(999, vec![0u8; 8]);
    net.peers.insert(2, peer);
    assert!(net.peers[&2].can_queue_reliable(), "ventana con hueco");

    net.send_reliable_queued(2, &PacketPayload::Heartbeat).await;

    assert_eq!(
        net.peers[&2].deferred_reliable.len(),
        2,
        "el nuevo se pone DETRAS del diferido, no en el aire"
    );
    assert_eq!(net.peers[&2].reliable_queue.len(), 0);
}

/// ADR-060: el drenaje avanza a velocidad de ventana. Con la ventana llena nada se mueve; al
/// ACKearse en vuelo, el siguiente pump pasa aparcados al aire hasta rellenar el hueco, en orden.
#[tokio::test]
async fn the_pump_drains_deferred_packets_as_acks_open_the_window() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let addr: SocketAddr = "127.0.0.1:7017".parse().unwrap();
    let mut peer = PeerConnection::new(2, "PeerB".into(), addr);
    for seq in 0..reliability::WINDOW_SIZE as u32 {
        peer.queue_reliable(seq, vec![0u8; 8]);
    }
    peer.defer_reliable(100, vec![1u8; 8]);
    peer.defer_reliable(101, vec![2u8; 8]);
    net.peers.insert(2, peer);

    net.pump_deferred_reliable().await;
    assert_eq!(
        net.peers[&2].deferred_reliable.len(),
        2,
        "ventana llena: nada se mueve"
    );

    // Dos ACKs abren dos huecos.
    net.peers.get_mut(&2).unwrap().process_ack(0);
    net.peers.get_mut(&2).unwrap().process_ack(1);
    net.pump_deferred_reliable().await;

    assert_eq!(
        net.peers[&2].deferred_reliable.len(),
        0,
        "los dos aparcados pasaron al aire"
    );
    assert_eq!(
        net.peers[&2].reliable_queue.len(),
        reliability::WINDOW_SIZE,
        "y ahora estan en la ventana, esperando su propio ACK"
    );
    assert!(
        net.peers[&2]
            .reliable_queue
            .iter()
            .any(|p| p.sequence == 100)
            && net.peers[&2]
                .reliable_queue
                .iter()
                .any(|p| p.sequence == 101),
        "los que viajaron son exactamente los aparcados, en orden FIFO"
    );
}

/// ADR-060 + ADR-062: al agotar MAX_RETRIES los aparcados mueren junto con la cola en vuelo —
/// bombearlos hacia un enlace muerto solo repetiria la agonia ventana a ventana. Desde ADR-062
/// eso ocurre porque el peer entero sale del mapa y se lleva ambas colas consigo; ninguna
/// sobrevive al evict.
#[tokio::test]
async fn exhausted_retries_purge_the_deferred_queue_with_the_inflight_one() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let addr: SocketAddr = "127.0.0.1:7018".parse().unwrap();
    let mut peer = PeerConnection::new(2, "PeerB".into(), addr);
    peer.queue_reliable(0, vec![0u8; 8]);
    peer.defer_reliable(100, vec![1u8; 8]);
    // Fuerza el agotamiento: retries al limite y proximo reintento vencido.
    for pkt in peer.reliable_queue.iter_mut() {
        pkt.retries = reliability::MAX_RETRIES;
        pkt.next_retry_at = std::time::Instant::now() - Duration::from_millis(1);
    }
    net.peers.insert(2, peer);

    net.process_retransmits().await;

    assert!(
        !net.peers.contains_key(&2),
        "el peer sale del mapa y ambas colas mueren con el"
    );
}

/// La rama que SI retiene: un fantasma no se evicta desde aqui (ADR-016 — su ciclo de vida lo
/// lleva el sistema phantom), asi que ahi la purga de los aparcados tiene que ser EXPLICITA.
/// Sin este test, `purge_deferred` puede desaparecer de esa rama y nada falla: el otro test
/// pasa por el evict del peer real, que no la ejecuta.
#[tokio::test]
async fn exhausted_retries_purge_a_phantoms_deferred_queue_without_evicting_it() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let phantom_id = net.spawn_phantom("Victima", [2.0, 1.8, 0.0], None);
    {
        let peer = net.peers.get_mut(&phantom_id).unwrap();
        peer.queue_reliable(0, vec![0u8; 8]);
        peer.defer_reliable(100, vec![1u8; 8]);
        for pkt in peer.reliable_queue.iter_mut() {
            pkt.retries = reliability::MAX_RETRIES;
            pkt.next_retry_at = std::time::Instant::now() - Duration::from_millis(1);
        }
    }

    net.process_retransmits().await;

    assert!(
        net.peers.contains_key(&phantom_id),
        "un fantasma no se evicta desde aqui (ADR-016)"
    );
    assert_eq!(
        net.peers[&phantom_id].reliable_queue.len(),
        0,
        "en vuelo purgada"
    );
    assert_eq!(
        net.peers[&phantom_id].deferred_reliable.len(),
        0,
        "aparcados purgados con ella"
    );
}

/// ADR-060 (d) end-to-end sobre sockets reales: un roster que ANTES no cabia en un datagrama
/// UDP (65 507 B) ahora llega entero y se aplica entero. 4000 carryables son ~200 KB: con el
/// envio monolitico esto era un `WSAEMSGSIZE` y la replicacion de carryables se detenia PARA
/// SIEMPRE, sin mas rastro que un warn 1/s.
#[tokio::test]
async fn an_oversized_roster_now_survives_the_datagram_limit_and_arrives_whole() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let joiner_addr = loopback_addr(&joiner);
    host.peers
        .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));

    host.stp_carryables = (0..4000)
        .map(|id| protocol::StpCarryableInfo {
            id,
            def_id: 7,
            position: [id as f32, 1.8, 0.0],
            rotation: 0.0,
        })
        .collect();
    let monolithic_bytes = rmp_serde::to_vec_named(&host.stp_carryables).unwrap().len();
    assert!(
        monolithic_bytes > 65_507,
        "setup: el roster tiene que superar el limite del datagrama ({monolithic_bytes} B) o el \
         test no prueba nada"
    );

    // Rondas repetidas, como el juego real (10 Hz): una pagina perdida deja su generacion
    // incompleta y la ronda siguiente la sustituye entera. Eso es la autocuracion declarada.
    //
    // ADR-071: el gate de emision cuenta TIEMPO REAL, y este bucle comprime dos segundos de juego
    // en unos 400 ms — sin rearmarlo, de la ronda 4 en adelante no saldria nada y el test dejaria
    // de probar la autocuracion en silencio (pasaria igual, porque 4000 elementos entran en una
    // sola ronda). Se rearma a proposito: lo que se prueba aqui es el reensamblado, no el gate,
    // que tiene sus propios tests en roster.rs.
    for _ in 0..20 {
        host.roster_gates.carryables = Default::default();
        sync::broadcast_stp_carryables(&mut host).await;
        tokio::time::sleep(Duration::from_millis(20)).await;
        joiner.process_incoming().await;
        if joiner.stp_carryables.len() == 4000 {
            break;
        }
    }

    assert_eq!(
        joiner.stp_carryables.len(),
        4000,
        "el roster tiene que llegar ENTERO, no truncado"
    );
    assert_eq!(
        joiner.stp_carryables[0].id, 0,
        "y en el orden del emisor: la pagina 0 va primero"
    );
    assert_eq!(joiner.stp_carryables[3999].id, 3999);
}

/// La mitad negativa: mientras el roster esta a medias, el joiner conserva el ANTERIOR. Aplicar
/// media lista le borraria la otra mitad de los objetos del mundo — peor que esperar 100 ms.
#[tokio::test]
async fn a_partially_arrived_roster_never_replaces_the_previous_one() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let joiner_addr = loopback_addr(&joiner);
    host.peers
        .insert(2, PeerConnection::new(2, "Joiner".into(), joiner_addr));

    // El joiner ya tiene un roster aplicado de una ronda anterior.
    joiner.stp_carryables = vec![protocol::StpCarryableInfo {
        id: 999,
        def_id: 1,
        position: [0.0, 0.0, 0.0],
        rotation: 0.0,
    }];

    // El host emite un roster grande, pero solo se procesa UNA pasada: llegan paginas sueltas,
    // nunca todas.
    host.stp_carryables = (0..2000)
        .map(|id| protocol::StpCarryableInfo {
            id,
            def_id: 7,
            position: [id as f32, 1.8, 0.0],
            rotation: 0.0,
        })
        .collect();
    sync::broadcast_stp_carryables(&mut host).await;
    tokio::time::sleep(Duration::from_millis(20)).await;

    // Se drena el socket UNA sola vez y a proposito no se espera a la generacion completa.
    joiner.process_incoming().await;

    assert!(
        joiner.stp_carryables.len() == 1 || joiner.stp_carryables.len() == 2000,
        "o sigue el roster viejo, o esta el nuevo COMPLETO: nunca una lista truncada (habia {})",
        joiner.stp_carryables.len()
    );
}

/// La contrapartida: por debajo de la ventana, un envio fiable SI se encola.
#[tokio::test]
async fn reliable_send_queues_normally_below_the_window() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let addr: SocketAddr = "127.0.0.1:7006".parse().unwrap();
    net.peers
        .insert(2, PeerConnection::new(2, "PeerB".into(), addr));

    net.send_reliable(2, &PacketPayload::Heartbeat).await;

    assert_eq!(net.peers[&2].reliable_queue.len(), 1);
}

/// La contrapartida: una direccion que NO pertenece a ningun otro peer si se adopta, para
/// que un re-binding de NAT genuino siga funcionando.
#[tokio::test]
async fn unclaimed_address_is_still_adopted_after_nat_rebind() {
    let mut net = NetworkManager::bind(0, 3, 42, false).await.unwrap();
    let old_addr: SocketAddr = "127.0.0.1:7001".parse().unwrap();
    let rebound_addr: SocketAddr = "127.0.0.1:7099".parse().unwrap();
    net.peers
        .insert(2, PeerConnection::new(2, "PeerB".into(), old_addr));

    let rebound = IncomingPacket {
        addr: rebound_addr,
        header: PacketHeader::new(protocol::PacketType::Heartbeat as u16, 2, 0, 0),
        payload: PacketPayload::Heartbeat,
    };
    net.handle_packet(rebound).await;

    assert_eq!(
        net.peers[&2].addr, rebound_addr,
        "una direccion sin duenio debe adoptarse (re-binding de NAT)"
    );
}

// ─── ADR-016: phantom peers ───

#[tokio::test]
async fn phantom_counts_in_roster_but_not_in_real_count() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let pid = host.spawn_phantom("Robapieles_Test", [10.0, 1.8, 5.0], None);

    // Renders as a peer (the roster / build_world_state includes it)…
    assert_eq!(host.peer_count(), 1);
    // …but the internal count gates (sanity, joiner spawn) must not count it.
    assert_eq!(host.real_peer_count(), 0);
    assert!(host.is_phantom(pid));
    // Id is in the dedicated phantom range, clear of real ids and the local id.
    assert_ne!(pid, host.local_id);
    assert!(pid >= PHANTOM_ID_BASE);
    // The phantom is reachable as a normal PeerConnection (so it renders).
    let p = &host.peers[&pid];
    assert_eq!(p.name, "Robapieles_Test");
    // XZ may be snapped to a grid_gen-walkable cell, and Y is grounded to the grid_gen floor
    // + the player's stand height (ADR-018). NOTE: this assertion used to freeze `+ 0.1`,
    // which contradicted the line above and put the phantom 1.7 m into the floor once the
    // client subtracted PlayerBaseY from its pose (2026-08-01 play-test). The comment was
    // right and the number was wrong.
    let expected_y =
        crate::world::grid_gen::grid_floor_y(0) + crate::world::collision::PLAYER_BASE_Y;
    assert_eq!(
        p.position[1], expected_y,
        "spawn Y must be the grid_gen floor + the player stand height, like any real peer"
    );
}

#[tokio::test]
async fn phantom_survives_timeout_when_refreshed() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let pid = host.spawn_phantom("Robapieles_Test", [0.0, 1.8, 0.0], None);
    // Force its heartbeat stale (it never receives real packets), then refresh as the
    // game loop does each heartbeat-tick.
    host.peers.get_mut(&pid).unwrap().last_heartbeat = Instant::now() - Duration::from_secs(10);
    host.refresh_phantom_heartbeats();

    let events = host.check_timeouts();
    assert!(events.is_empty(), "refreshed phantom must not time out");
    assert!(host.peers.contains_key(&pid));
}

#[tokio::test]
async fn phantom_is_reaped_without_refresh() {
    // Sanity check that the refresh is load-bearing: without it the timeout reaps it.
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let pid = host.spawn_phantom("Robapieles_Test", [0.0, 1.8, 0.0], None);
    host.peers.get_mut(&pid).unwrap().last_heartbeat = Instant::now() - Duration::from_secs(10);

    let events = host.check_timeouts();
    assert_eq!(events.len(), 1);
    assert!(!host.peers.contains_key(&pid));
}

#[tokio::test]
async fn broadcast_reliable_skips_phantom() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    // A real peer with a routable test addr…
    let real_id = 2;
    let addr: SocketAddr = "127.0.0.1:9999".parse().unwrap();
    host.peers
        .insert(real_id, PeerConnection::new(real_id, "Real".into(), addr));
    // …and a phantom alongside it.
    let phantom_id = host.spawn_phantom("Robapieles_Test", [0.0, 1.8, 0.0], None);

    let payload = PacketPayload::AnchorBroadcast {
        chunk_pos: [0, 0],
        durability: 1.0,
        installed_by: "test".into(),
    };
    host.broadcast_reliable(&payload).await;

    assert_eq!(
        host.peers[&real_id].reliable_queue.len(),
        1,
        "real peer must receive the reliable broadcast"
    );
    assert_eq!(
        host.peers[&phantom_id].reliable_queue.len(),
        0,
        "phantom must be skipped by reliable broadcast"
    );
}

#[tokio::test]
async fn pose_relay_addresses_real_peers_only_but_still_relays_phantom_poses() {
    // ADR-043 — the UNRELIABLE relay used to send to phantoms too (unlike the reliable one
    // above), and a phantom's addr is the inert 127.0.0.1:1, so every such datagram was a
    // syscall into a dead loopback port. Both halves are asserted, because dropping phantoms
    // as SOURCES too would be the easy over-correction and would make them invisible.
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let real_id = 2;
    let addr: SocketAddr = "127.0.0.1:9999".parse().unwrap();
    host.peers
        .insert(real_id, PeerConnection::new(real_id, "Real".into(), addr));
    let ghost_a = host.spawn_phantom("Robapieles_A", [0.0, 1.8, 0.0], None);
    let ghost_b = host.spawn_phantom("Robapieles_B", [40.0, 1.8, 40.0], None);

    let dests = super::sync::relay_destinations(&host);

    assert_eq!(
        dests,
        vec![real_id],
        "only real peers may be addressed, got {dests:?}"
    );
    // …and the counterpart: the phantoms are still in `peers`, so the relay's SOURCE loop
    // still emits their poses to that real peer. Without this the creature stops existing
    // for every joiner.
    assert!(
        host.peers.contains_key(&ghost_a) && host.peers.contains_key(&ghost_b),
        "phantoms must remain relay SOURCES"
    );
}

/// ADR-046 — la voz de un joiner llega al host y se atribuye al hablante SEGÚN LA CABECERA,
/// no según nada que venga dentro del payload. Esa distinción es de seguridad: si el id del
/// hablante viajara en el cuerpo, un cliente modificado podría firmar su audio como si fuera
/// otro jugador. Sockets reales, no simulacro.
#[tokio::test]
async fn voice_from_a_joiner_arrives_attributed_to_the_header_sender() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 1004, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;
    assert_eq!(joiner.local_id, 1004);

    let audio: Vec<u8> = (0..120u16).map(|i| (i * 31 + 7) as u8).collect();
    joiner
        .send_unreliable_to(
            1,
            &PacketPayload::VoiceFrame {
                seq: 4242,
                data: audio.clone(),
            },
        )
        .await;
    tokio::time::sleep(Duration::from_millis(150)).await;

    let events = host.process_incoming().await;
    let got = events
        .iter()
        .find_map(|e| match e {
            NetworkEvent::VoiceReceived { speaker, seq, data } => Some((*speaker, *seq, data)),
            _ => None,
        })
        .unwrap_or_else(|| panic!("host no recibio la voz del joiner, eventos: {events:?}"));

    assert_eq!(got.0, 1004, "el hablante sale del sender_id de la cabecera");
    assert_eq!(got.1, 4242);
    assert_eq!(got.2, &audio, "el audio cruza el socket byte a byte");
}

/// La otra mitad del viaje: el host RELAYA la voz de un tercero sellada con el id de ESE
/// tercero (`send_unreliable_as`, el mecanismo de ADR-015). Sin el sellado, un joiner
/// atribuiría al host todo lo que dice cualquier otro jugador y el audio se pegaría al proxy
/// equivocado. Aquí el host habla EN NOMBRE del peer 1007, que ni siquiera tiene socket.
#[tokio::test]
async fn a_relayed_voice_frame_is_attributed_to_the_speaker_not_to_the_host() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 1004, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;

    const OTHER_SPEAKER: PeerId = 1007;
    host.send_unreliable_as(
        OTHER_SPEAKER,
        joiner.local_id,
        &PacketPayload::VoiceFrame {
            seq: 5,
            data: vec![9; 60],
        },
    )
    .await;
    tokio::time::sleep(Duration::from_millis(150)).await;

    let events = joiner.process_incoming().await;
    let speaker = events
        .iter()
        .find_map(|e| match e {
            NetworkEvent::VoiceReceived { speaker, .. } => Some(*speaker),
            _ => None,
        })
        .unwrap_or_else(|| panic!("el joiner no recibio la voz relayada: {events:?}"));

    assert_eq!(
        speaker, OTHER_SPEAKER,
        "la voz relayada debe atribuirse al hablante, no al host que la reenvia"
    );
}

// ─────────────────────── ADR-068 — spray sobre la red real ───────────────────────

/// Una pintada de prueba, con blob de puntos reconocible para poder seguirlo por el cable.
fn test_spray(id: u32, cx: i32, cz: i32) -> crate::world::spray::Spray {
    crate::world::spray::Spray {
        id,
        cx,
        cz,
        layer: 0,
        local_pos: [12.5, 1.6, 33.0],
        yaw: 90.0,
        size: [1.5, 1.0],
        author: 7,
        tick: 42,
        strokes: vec![crate::world::spray::SprayStroke {
            color: 3,
            width: 6,
            points: vec![0, 0, 128, 200, 255, 255],
        }],
    }
}

/// ADR-068 — los TRES saltos de spray sobre dos backends de verdad, con handshake real y
/// sockets UDP reales. Hasta aquí la mitad multijugador solo tenía tests de lógica suelta: esto
/// es lo que prueba que el paquete cruza, que el opcode se mapea al evento correcto y que el
/// `requester_id` sale de la CABECERA y no del payload — el detalle que impide que un cliente
/// reclame estar pintando desde el sitio de otro.
///
/// Mismo alcance que `corpse_relay_hops_round_trip_between_peers`: wire + mapeo de eventos. La
/// autoridad (validación de topes, acuñado, desalojo) se prueba en `game_loop`.
#[tokio::test]
async fn spray_hops_round_trip_between_peers() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 2077, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;
    assert_eq!(joiner.local_id, 2077);

    // Salto 1: el joiner PIDE pintar. Nada de lo que manda es autoridad.
    let request = PacketPayload::SprayPlaceRequest {
        place_id: 77,
        layer: 0,
        world_pos: [137.5, 1.6, -80.0],
        yaw: 90.0,
        size: [1.5, 1.0],
        strokes: vec![crate::world::spray::SprayStroke {
            color: 3,
            width: 6,
            points: vec![0, 0, 128, 200, 255, 255],
        }],
        page: 0,
        page_count: 1,
    };
    joiner.send_reliable(1, &request).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    let host_events = host.process_incoming().await;

    let requester = host_events
        .iter()
        .find_map(|e| match e {
            NetworkEvent::SprayPlaceRequest {
                place_id: 77,
                requester_id,
                strokes,
                ..
            } => {
                assert_eq!(
                    strokes[0].points,
                    vec![0, 0, 128, 200, 255, 255],
                    "el blob de puntos debe cruzar byte a byte"
                );
                Some(*requester_id)
            }
            _ => None,
        })
        .unwrap_or_else(|| panic!("el host no recibio la peticion: {host_events:?}"));
    assert_eq!(
        requester, 2077,
        "el peticionario sale de la CABECERA: es lo que impide reclamar la posicion de otro"
    );

    // Salto 2: el host difunde la pintada YA aceptada, con su id acuñado.
    host.send_reliable(
        2077,
        &PacketPayload::SprayPlaced {
            spray: test_spray(500, 2, -2),
            page: 0,
            page_count: 1,
        },
    )
    .await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    let joiner_events = joiner.process_incoming().await;

    let received = joiner_events
        .iter()
        .find_map(|e| match e {
            NetworkEvent::SprayPlacedReceived { spray } => Some(spray.clone()),
            _ => None,
        })
        .unwrap_or_else(|| panic!("el joiner no recibio la pintada: {joiner_events:?}"));
    assert_eq!(received.id, 500, "el id lo acuña el host y viaja intacto");
    assert_eq!((received.cx, received.cz), (2, -2));
    assert_eq!(
        received.local_pos,
        [12.5, 1.6, 33.0],
        "sigue siendo LOCAL al chunk"
    );
    assert_eq!(received.strokes[0].points, vec![0, 0, 128, 200, 255, 255]);

    // Salto 3: el joiner carga un chunk y pregunta qué hay pintado. Sin esto, quien se une a un
    // mundo ya pintado ve paredes limpias: la geometría se deriva del seed, una pintada no.
    joiner
        .send_reliable(
            1,
            &PacketPayload::SprayChunkRequest {
                cx: 2,
                cz: -2,
                layer: 0,
            },
        )
        .await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    let host_events = host.process_incoming().await;

    let asked_by = host_events
        .iter()
        .find_map(|e| match e {
            NetworkEvent::SprayChunkRequest {
                cx: 2,
                cz: -2,
                layer: 0,
                requester_id,
            } => Some(*requester_id),
            _ => None,
        })
        .unwrap_or_else(|| panic!("el host no recibio la peticion de chunk: {host_events:?}"));
    assert_eq!(
        asked_by, 2077,
        "la respuesta vuelve a ESE peer, asi que el remitente es parte del evento"
    );
}

/// ADR-078 — el trazo EN VIVO cruza, no es fiable, y el pintor sale de la CABECERA.
///
/// Las tres cosas juntas porque las tres son el ADR: si el borrador viajara fiable ocuparía la
/// ventana de 32 para entregar un dato que caduca en 100 ms, y si el pintor saliera del payload
/// un cliente podría dibujar en nombre de otro (el mismo agujero que `SprayPlaceRequest` cerró
/// sacando el `requester_id` de la cabecera).
#[tokio::test]
async fn a_live_spray_draft_hops_unreliably_and_is_stamped_by_its_sender() {
    use crate::network::protocol::PacketType;
    use crate::network::reliability::is_reliable;

    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 4242, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;
    assert_eq!(joiner.local_id, 4242);

    let draft = PacketPayload::SprayDraft {
        place_id: 77,
        layer: 0,
        anchor: [137.5, 1.6, -80.0],
        yaw: 90.0,
        color: 3,
        width: 0.08,
        first_index: 12,
        // (-1200, 340), (0, 0), (32767, -32768) como i16 little-endian: los extremos del
        // rango entran a proposito, son el limite del formato.
        points_mm: vec![0x50, 0xFB, 0x54, 0x01, 0, 0, 0, 0, 0xFF, 0x7F, 0x00, 0x80],
    };
    assert_eq!(draft.type_code(), 0x54);
    assert_eq!(PacketType::from_u16(0x54), Some(PacketType::SprayDraft));
    assert!(
        !is_reliable(0x54),
        "un borrador fiable ocuparia la ventana de 32 para entregar algo ya caducado"
    );

    joiner.send_unreliable_to(1, &draft).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    let host_events = host.process_incoming().await;

    let (painter, points, first) = host_events
        .iter()
        .find_map(|e| match e {
            NetworkEvent::SprayDraftReceived {
                place_id: 77,
                painter_id,
                points_mm,
                first_index,
                ..
            } => Some((*painter_id, points_mm.clone(), *first_index)),
            _ => None,
        })
        .unwrap_or_else(|| panic!("el host no recibio el borrador: {host_events:?}"));

    assert_eq!(
        painter, 4242,
        "el pintor sale de la CABECERA: es lo que impide dibujar en nombre de otro"
    );
    assert_eq!(
        points,
        vec![0x50, 0xFB, 0x54, 0x01, 0, 0, 0, 0, 0xFF, 0x7F, 0x00, 0x80],
        "el blob cruza byte a byte, extremos del i16 incluidos"
    );
    assert_eq!(
        first, 12,
        "sin el indice, dos trozos no consecutivos se cosen"
    );
}

/// ADR-078 — el peor caso de un borrador (64 puntos) cabe de sobra en un datagrama. El tope no
/// es decoración: sin él, un cliente con un pico de lag acumularía puntos y mandaría un paquete
/// que no cruza, justo cuando la red ya va mal.
#[tokio::test]
async fn a_worst_case_spray_draft_survives_a_real_datagram() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 4343, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;

    // 64 puntos = 256 bytes. El cliente no puede mandar mas en un paquete (ADR-078 decision 6).
    let points: Vec<u8> = (0..256).map(|i| (i * 7 % 251) as u8).collect();
    let draft = PacketPayload::SprayDraft {
        place_id: u64::MAX,
        layer: 3,
        anchor: [-4096.0, 12.5, 4096.0],
        yaw: 359.9,
        color: 15,
        width: 0.25,
        first_index: u16::MAX - 64,
        points_mm: points.clone(),
    };

    joiner.send_unreliable_to(1, &draft).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    let host_events = host.process_incoming().await;

    let received = host_events
        .iter()
        .find_map(|e| match e {
            NetworkEvent::SprayDraftReceived { points_mm, .. } => Some(points_mm.clone()),
            _ => None,
        })
        .unwrap_or_else(|| panic!("el borrador de peor caso no cruzo: {host_events:?}"));
    assert_eq!(received, points, "los 64 puntos cruzan enteros");
}

/// ADR-068 — una pintada REAL (32 trazos, 512 puntos) tiene que caber en un datagrama. Es la
/// razón por la que viaja una por paquete y no en un roster: el presupuesto medido son ~1,9 KB,
/// y los rosters de ADR-060 ya tuvieron que paginarse por reventar con elementos más ligeros.
/// Si alguien sube los topes sin mirar, esto se cae antes que la replicación en producción.
#[tokio::test]
async fn a_worst_case_spray_survives_a_real_datagram() {
    use crate::world::spray::{SprayStroke, MAX_POINTS_PER_SPRAY, MAX_STROKES_PER_SPRAY};

    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 3300, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    joiner.process_incoming().await;

    let per_stroke = MAX_POINTS_PER_SPRAY / MAX_STROKES_PER_SPRAY;
    let mut worst = test_spray(999, 0, 0);
    worst.strokes = (0..MAX_STROKES_PER_SPRAY)
        .map(|_| SprayStroke {
            color: 0,
            width: 8,
            points: vec![7u8; per_stroke * 2],
        })
        .collect();
    assert_eq!(worst.validate(), Ok(()), "el peor caso debe ser valido");

    // TAREA 2 (2026-08-31): el peor caso YA NO CABE en un datagrama (1923 B medidos contra un
    // techo de 1200), así que sale paginado por trazos. La afirmación del test no cambia —el peor
    // caso declarado tiene que cruzar ENTERO—; lo que cambia es que ahora cruza porque se trocea
    // y se reensambla, en vez de porque IP lo fragmentaba y colaba.
    let pages = crate::network::sync::spray_placed_pages(&worst);
    assert!(
        pages.len() > 1,
        "preparación: el peor caso tiene que necesitar más de una página"
    );
    for payload in &pages {
        host.send_reliable(3300, payload).await;
    }
    tokio::time::sleep(Duration::from_millis(200)).await;
    let events = joiner.process_incoming().await;

    let got = events
        .iter()
        .find_map(|e| match e {
            NetworkEvent::SprayPlacedReceived { spray } => Some(spray.clone()),
            _ => None,
        })
        .unwrap_or_else(|| panic!("el peor caso no cruzo el datagrama: {events:?}"));

    assert_eq!(got.strokes.len(), MAX_STROKES_PER_SPRAY);
    assert_eq!(got.point_count(), MAX_POINTS_PER_SPRAY);
}

// ─── E1 / ADR-074: el AOI de poses, verificado sobre sockets UDP REALES ───

/// Coloca a un peer ya conectado en una posición concreta, como haría su `PlayerUpdate`.
fn place_peer(net: &mut NetworkManager, id: PeerId, pos: [f32; 3]) {
    if let Some(p) = net.peers.get_mut(&id) {
        p.position = pos;
    }
}

/// Cuenta los `PlayerUpdate` que un peer recibe de verdad por el socket.
async fn drain_pose_updates(net: &mut NetworkManager) -> usize {
    net.process_incoming()
        .await
        .iter()
        .filter(|e| matches!(e, NetworkEvent::RemotePlayerUpdate { .. }))
        .count()
}

/// **La verificación de E1 que las sondas no dan**: tres backends reales hablando por UDP, y se
/// cuenta lo que cada joiner RECIBE — no lo que el emisor cree que manda.
///
/// El montaje reproduce el caso que el AOI existe para cortar: dos joiners lejos el uno del otro
/// (300 m, muy por encima de `AOI_POSE_RADIUS_M`) pero ambos cerca del host. Sin AOI, el host
/// relaya la pose de cada uno al otro; con AOI, no. Y lo que NO puede pasar es que deje de llegar
/// la pose del host, que está al lado de los dos.
#[tokio::test]
async fn far_apart_joiners_stop_receiving_each_others_poses_over_real_sockets() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut a = NetworkManager::bind(0, 3001, 0, false).await.unwrap();
    let mut b = NetworkManager::bind(0, 3002, 0, false).await.unwrap();

    a.initiate_connection(host_addr).await;
    b.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    a.process_incoming().await;
    b.process_incoming().await;
    assert_eq!(host.peers.len(), 2, "setup: los dos joiners conectados");

    // Lejos entre sí, cerca del host: A en el origen, B a 300 m.
    place_peer(&mut host, 3001, [0.0, 1.8, 0.0]);
    place_peer(&mut host, 3002, [300.0, 1.8, 0.0]);

    // Dos rondas, porque el anillo exterior emite en rondas alternas (LOD): con una sola no se
    // podría distinguir "filtrado por AOI" de "esta ronda no le tocaba".
    for _ in 0..2 {
        crate::network::sync::broadcast_peer_poses(&mut host).await;
        tokio::time::sleep(Duration::from_millis(60)).await;
    }
    let a_got = drain_pose_updates(&mut a).await;
    let b_got = drain_pose_updates(&mut b).await;

    assert_eq!(
        a_got, 0,
        "A no puede recibir NADA de B, que está a 300 m: es el ahorro entero de E1 \
         (recibidos: {a_got})"
    );
    assert_eq!(b_got, 0, "y simétricamente B tampoco de A");

    // Control positivo, y es el que impide que este test pase por estar todo roto: acercamos B a
    // 20 m de A y su pose TIENE que empezar a llegar.
    place_peer(&mut host, 3002, [20.0, 1.8, 0.0]);
    for _ in 0..2 {
        crate::network::sync::broadcast_peer_poses(&mut host).await;
        tokio::time::sleep(Duration::from_millis(60)).await;
    }
    let a_now = drain_pose_updates(&mut a).await;
    assert!(
        a_now > 0,
        "con B a 20 m, su pose tiene que llegarle a A — si no, el filtro está cortando de más"
    );
}

/// El invariante de ADR-016 medido donde importa, en los datagramas: un fantasma (id ≥ 0xF000)
/// dentro del AOI se relaya EXACTAMENTE igual que un jugador a la misma distancia. Si el AOI lo
/// tratara distinto, el joiner podría distinguirlo sin verlo.
#[tokio::test]
async fn a_phantom_inside_the_aoi_is_relayed_like_any_player() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 4001, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    joiner.process_incoming().await;

    // Fantasma sintético al lado del joiner (ADR-016: entra fuera del handshake).
    let phantom_id = host.spawn_phantom("robapieles", [10.0, 1.8, 0.0], None);
    // El snap de ADR-018 puede moverlo a la celda caminable más próxima; se recoloca al lado del
    // joiner para que la prueba sea sobre el AOI y no sobre dónde aterrizó.
    place_peer(&mut host, phantom_id, [10.0, 1.8, 0.0]);
    place_peer(&mut host, 4001, [0.0, 1.8, 0.0]);

    for _ in 0..2 {
        crate::network::sync::broadcast_peer_poses(&mut host).await;
        tokio::time::sleep(Duration::from_millis(60)).await;
    }
    assert!(
        drain_pose_updates(&mut joiner).await > 0,
        "la pose del fantasma cercano tiene que llegar como la de cualquier peer: tratarlo \
         distinto lo delataría"
    );
}

/// ADR-079, extremo a extremo sobre sockets reales: el roster lleva la entrada `relay_only`, el
/// joiner REGISTRA al fantasma (nombre del disfraz incluido, addr inerte local), y la siguiente
/// pose relayada APLICA sobre esa entrada — que es exactamente lo que faltaba: el test de arriba
/// probaba que la pose LLEGABA, pero sin entrada en `net.peers` el receptor la descartaba y el
/// robapieles fue invisible para todo joiner desde ADR-016.
#[tokio::test]
async fn a_relay_only_roster_entry_makes_the_phantom_visible_to_the_joiner() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 4001, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    joiner.process_incoming().await;

    let phantom_id = host.spawn_phantom("Skinwalker", [10.0, 1.8, 0.0], None);
    place_peer(&mut host, phantom_id, [10.0, 1.8, 0.0]);
    place_peer(&mut host, 4001, [0.0, 1.8, 0.0]);

    // El roster con la entrada relay_only (10 Hz en producción; aquí un envío bastan).
    let host_player = crate::player::session::Player::new(host.local_id, "Host");
    crate::network::sync::broadcast_peer_roster(&mut host, &host_player).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    joiner.process_incoming().await;

    let entry = joiner
        .peers
        .get(&phantom_id)
        .expect("ADR-079: el joiner registra al fantasma del roster");
    assert!(entry.relay_only, "registrado como inalcanzable");
    assert_eq!(entry.name, "Skinwalker", "el nombre del disfraz llegó");
    assert_eq!(
        entry.addr, INERT_PEER_ADDR,
        "la addr es la inerte LOCAL, nunca la del wire"
    );

    // La pose relayada aplica sobre la entrada — incluidos los cosméticos que sella el driver.
    host.peers.get_mut(&phantom_id).unwrap().revealed = true;
    for _ in 0..2 {
        crate::network::sync::broadcast_peer_poses(&mut host).await;
        tokio::time::sleep(Duration::from_millis(60)).await;
    }
    joiner.process_incoming().await;
    assert!(
        joiner.peers.get(&phantom_id).unwrap().revealed,
        "la pose relayada APLICA: el joiner ve exactamente lo que ve el host"
    );

    // Inalcanzable de verdad: fuera de todo destino de broadcast y fuera del conteo de reales.
    assert!(
        joiner
            .broadcast_destinations()
            .iter()
            .all(|(id, _)| *id != phantom_id),
        "ni un datagrama del joiner puede apuntar al fantasma (H10)"
    );
    assert_eq!(
        joiner.real_peer_count(),
        1,
        "el fantasma no cuenta como jugador en el joiner (solo el host)"
    );
}

/// ADR-079: ni `send_reliable` ni `broadcast_reliable` encolan jamás hacia una entrada
/// `relay_only` — una cola reliable a un inalcanzable agota reintentos y ADR-062 lo evictaría,
/// que con el roster re-insertándolo sería un bucle evict/re-add perpetuo.
#[tokio::test]
async fn no_reliable_is_ever_queued_to_a_relay_only_peer() {
    let mut joiner = NetworkManager::bind(0, 4001, 0, false).await.unwrap();
    let mut conn = PeerConnection::new(0xF000, "Skinwalker".into(), INERT_PEER_ADDR);
    conn.relay_only = true;
    joiner.peers.insert(0xF000, conn);

    joiner
        .send_reliable(0xF000, &PacketPayload::Heartbeat)
        .await;
    joiner
        .send_reliable_queued(0xF000, &PacketPayload::Heartbeat)
        .await;
    joiner.broadcast_reliable(&PacketPayload::Heartbeat).await;

    let peer = joiner.peers.get(&0xF000).unwrap();
    assert!(peer.reliable_queue.is_empty(), "cola reliable intacta");
    assert!(peer.deferred_reliable.is_empty(), "cola diferida intacta");
}

/// ADR-079: el ciclo de vida es el silencio — cuando el fantasma despawnea (o sale del roster),
/// la entrada deja de refrescar heartbeat y `check_timeouts` la cosecha a los 5 s, igual que a
/// cualquier peer. Sin protocolo de despedida: el fantasma nunca handshakeó (ADR-016).
#[tokio::test]
async fn a_silent_relay_only_entry_is_reaped_by_the_heartbeat_timeout() {
    let mut joiner = NetworkManager::bind(0, 4001, 0, false).await.unwrap();
    let mut conn = PeerConnection::new(0xF000, "Skinwalker".into(), INERT_PEER_ADDR);
    conn.relay_only = true;
    conn.last_heartbeat = std::time::Instant::now() - Duration::from_secs(10);
    joiner.peers.insert(0xF000, conn);

    let events = joiner.check_timeouts();
    assert_eq!(events.len(), 1, "una cosecha, un evento");
    assert!(
        joiner.peers.is_empty(),
        "la entrada silenciosa desaparece del mapa del joiner"
    );
}

/// ADR-083 enmienda 1, punto 4 y verificacion (g): un joiner cuyo pool de salas autoradas no case
/// con el del host se RECHAZA, no se degrada en silencio.
///
/// Sin esto, los dos peers generan el mundo desde el mismo seed pero con catalogos distintos: uno
/// pinta una sala donde el otro pinta otra, y el fallo no se ve hasta que alguien se choca con nada.
#[tokio::test]
async fn handshake_is_rejected_on_room_manifest_mismatch() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let newcomer: SocketAddr = "127.0.0.1:9503".parse().unwrap();

    // El host de este test no tiene manifiesto (sin variable de entorno), asi que su digest es
    // vacio; el joiner dice traer uno. Es justo el caso de dos builds desparejados.
    host.handle_handshake(
        newcomer,
        0,
        "OtroPool".into(),
        crate::ipc::server::WIRE_SCHEMA_VERSION.to_string(),
        "digest-de-otro-build".into(),
        HandshakeIdentity::default(),
    )
    .await;

    assert_eq!(
        host.real_peer_count(),
        0,
        "un pool desparejado no puede quedar registrado"
    );
    assert!(
        !host.peers.values().any(|p| p.addr == newcomer),
        "el rechazado no puede quedar registrado"
    );
}

// ─── El silencio: un handshake que nadie contesta ──────────────────────────────────────────
//
// Auditoría de conectividad (2026-08-30). `ConnectRejected` cubría el rechazo EXPLÍCITO (session
// full, versión de wire, pool de salas): llega un `Disconnect` y hay paquete que interpretar.
// Sobre UDP el modo de fallo dominante no es ése — es el SILENCIO: IP equivocada, puerto
// equivocado, host apagado, firewall entrante bloqueando, NAT sin redirección. Ese camino no
// existía: `retry_pending_connection` reenviaba el mismo handshake muerto cada segundo durante
// toda la partida y nadie se enteraba nunca, mientras el backend del joiner le servía a Unity un
// mundo local en solitario. Reproducido con el binario real contra 192.0.2.1 (TEST-NET-1):
// attempt=12 a los 12 s, cero errores, `build_world_state` sirviendo con remote_players=0.

/// Simula un intento que arrancó hace `by` sin tocar el reloj real.
fn age_pending_connect(net: &mut NetworkManager, by: Duration) {
    net.pending_connect_started_at = Instant::now().checked_sub(by);
}

#[tokio::test]
async fn silent_handshake_gives_up_after_the_connect_budget() {
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let dead_host: SocketAddr = "192.0.2.1:7778".parse().unwrap();
    joiner.initiate_connection(dead_host).await;
    age_pending_connect(&mut joiner, CONNECT_TIMEOUT + Duration::from_secs(1));

    joiner.retry_pending_connection().await;
    let events = joiner.process_incoming().await;

    assert!(
        events.iter().any(|e| matches!(
            e,
            NetworkEvent::ConnectTimedOut { addr, attempts, .. }
                if *addr == dead_host && *attempts >= 1
        )),
        "un handshake sin respuesta tiene que rendirse con diagnostico, no reintentar para siempre: {events:?}"
    );
    assert_eq!(
        joiner.pending_connect_addr, None,
        "tras el veredicto no puede quedar un intento vivo reenviando"
    );
}

/// **REPRODUCE «Could not connect: no session confirmation» (sesión física, 2026-08-31 20:52).**
///
/// El valor real que llegó al backend, copiado byte a byte de su cabecera de log:
/// `CONNECT_TO=31.4.149.48\n:7778`. Un salto de línea entre la IP y el puerto, porque
/// `JoinSessionUI` lee `_ipField.text` sin `Trim()` y `NetworkInitializer` lo interpola tal cual.
///
/// Lo que este test fija NO es el parseo —que falla y debe fallar— sino **el silencio que viene
/// después**: `main.rs` registra el error y sigue, así que `initiate_connection` no se llama,
/// `pending_connect_addr` se queda en `None`, y el presupuesto de `CONNECT_TIMEOUT` **nunca
/// arranca**. El backend jamás emite `ConnectTimedOut`, o sea que Unity nunca recibe
/// `session_ended` ni `session_joined`: se queda 25 s en «Joining…» hasta que salta el backstop,
/// que es la única cosa en todo el sistema que llega a enterarse.
///
/// Una configuración fatal degradaba a un cuelgue mudo de 25 segundos en vez de a un fallo
/// inmediato y con nombre. Eso es lo que este test exige; el `Trim()` del lado Unity solo quita
/// este disparador concreto, y por eso no basta con él.
#[tokio::test]
async fn an_unparseable_connect_target_ends_the_session_instead_of_going_silent() {
    let from_the_field = "31.4.149.48\n:7778";
    let err = from_the_field
        .parse::<SocketAddr>()
        .expect_err("setup: es exactamente el valor que main.rs rechazó en campo");

    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    joiner.reject_invalid_connect_target(from_the_field, &err.to_string());

    // Nada de lo que sigue puede arrancar un intento: no hay destino que valga.
    assert_eq!(joiner.pending_connect_addr, None);
    assert_eq!(
        joiner.handshake_attempts, 0,
        "no se manda un handshake a una dirección que no existe"
    );

    let events = joiner.process_incoming().await;
    let named = events.iter().find_map(|e| match e {
        NetworkEvent::ConnectTargetInvalid { raw, error } => Some((raw.clone(), error.clone())),
        _ => None,
    });
    let (raw, error) = named.expect(
        "un destino imparseable tiene que producir un veredicto INMEDIATO: sin él, Unity espera \
         25 s y le dice al jugador «no session confirmation», que no señala a nada",
    );
    assert_eq!(raw, from_the_field, "el motivo enseña el valor literal");
    assert!(
        !error.is_empty(),
        "y por qué no parseó, que es lo accionable"
    );

    // Y el veredicto se emite UNA vez: no puede convertirse en un goteo por tick.
    let again = joiner.process_incoming().await;
    assert!(
        !again
            .iter()
            .any(|e| matches!(e, NetworkEvent::ConnectTargetInvalid { .. })),
        "el veredicto es único, no un latido"
    );
}

#[tokio::test]
async fn connect_budget_does_not_expire_early() {
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let host: SocketAddr = "127.0.0.1:9601".parse().unwrap();
    joiner.initiate_connection(host).await;
    // Dentro del presupuesto por un margen holgado: el host tarda ~1-2 s en generar el mundo
    // antes de leer datagramas, y rendirse ahí seria romper el join legitimo simultaneo.
    age_pending_connect(&mut joiner, CONNECT_TIMEOUT / 3);

    joiner.retry_pending_connection().await;
    let events = joiner.process_incoming().await;

    assert!(
        !events
            .iter()
            .any(|e| matches!(e, NetworkEvent::ConnectTimedOut { .. })),
        "dentro del presupuesto se sigue insistiendo, no se abandona: {events:?}"
    );
    assert_eq!(
        joiner.pending_connect_addr,
        Some(host),
        "el intento sigue vivo mientras quede presupuesto"
    );
}

#[tokio::test]
async fn the_host_never_times_out_its_own_listen() {
    // El host no "conecta" a nadie: escucha. Un presupuesto que se le aplicara mataria la sesion
    // de quien hostea en solitario esperando a que llegue alguien.
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.pending_connect_addr = Some("127.0.0.1:9602".parse().unwrap());
    age_pending_connect(&mut host, CONNECT_TIMEOUT * 10);

    host.retry_pending_connection().await;
    let events = host.process_incoming().await;

    assert!(
        !events
            .iter()
            .any(|e| matches!(e, NetworkEvent::ConnectTimedOut { .. })),
        "el host no tiene intento de conexion que pueda agotarse: {events:?}"
    );
}

#[tokio::test]
async fn a_completed_handshake_stops_the_connect_budget() {
    // El caso que NO puede romperse: se entro dentro de plazo y luego pasa el tiempo. Sin limpiar
    // el marcador, un jugador dentro de la partida recibiria un "nadie contesto" a los 15 s.
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let host_addr = loopback_addr(&host);

    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    let joined = joiner.process_incoming().await;

    assert!(
        joined
            .iter()
            .any(|e| matches!(e, NetworkEvent::PeerConnected { .. })),
        "el handshake de control tenia que completarse: {joined:?}"
    );
    assert_eq!(
        joiner.pending_connect_started_at, None,
        "entrar tiene que parar el reloj del presupuesto"
    );

    age_pending_connect(&mut joiner, CONNECT_TIMEOUT * 10);
    joiner.retry_pending_connection().await;
    let after = joiner.process_incoming().await;
    assert!(
        !after
            .iter()
            .any(|e| matches!(e, NetworkEvent::ConnectTimedOut { .. })),
        "quien ya esta en la sesion no puede recibir un timeout de conexion: {after:?}"
    );
}

#[tokio::test]
async fn an_explicit_rejection_wins_over_the_timeout() {
    // Los dos veredictos son excluyentes: si el host dijo POR QUE, ese motivo es el que tiene que
    // leer el jugador. Sin limpiar el marcador, el presupuesto seguia corriendo y a los 15 s
    // encima del "session full" real caia un "nadie contesto" que lo contradecia.
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let host_addr: SocketAddr = "127.0.0.1:9603".parse().unwrap();
    joiner.initiate_connection(host_addr).await;

    let rejection = IncomingPacket {
        addr: host_addr,
        header: PacketHeader::new(protocol::PacketType::Disconnect as u16, 1, 0, 0),
        payload: PacketPayload::Disconnect {
            reason: "session full".into(),
        },
    };
    joiner.handle_packet(rejection).await;

    assert_eq!(joiner.pending_connect_started_at, None);

    age_pending_connect(&mut joiner, CONNECT_TIMEOUT * 10);
    joiner.retry_pending_connection().await;
    let events = joiner.process_incoming().await;
    assert!(
        !events
            .iter()
            .any(|e| matches!(e, NetworkEvent::ConnectTimedOut { .. })),
        "un rechazo explicito no puede quedar tapado por un timeout posterior: {events:?}"
    );
}

// ─── Bind: la interfaz, no solo el puerto ──────────────────────────────────────────────────

#[tokio::test]
async fn the_p2p_socket_binds_every_interface_not_just_loopback() {
    // Es la propiedad que hace posible el LAN: escuchando en 127.0.0.1 el host funcionaria en su
    // propia maquina y seria invisible desde cualquier otra, y el sintoma —"a mi me va, a mi
    // amigo no"— no senala al bind por ningun lado. Se comprueba la direccion REAL del socket,
    // no la cadena que se le paso a `bind`.
    let net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let addr = net.local_addr();

    assert!(
        addr.ip().is_unspecified(),
        "el socket P2P tiene que escuchar en 0.0.0.0 para que el LAN llegue, no en {}",
        addr.ip()
    );
    assert_ne!(addr.port(), 0, "el puerto efectivo tiene que ser real");
}

#[tokio::test]
async fn the_requested_port_is_the_port_that_gets_bound() {
    // "El puerto configurable se respeta": lo que se teclea en la UI viaja como NET_PORT y tiene
    // que ser el que acabe en el socket. Un desvio silencioso aqui manda al joiner remoto a un
    // puerto donde ya no escucha nadie.
    // La sonda es un socket de `std`, no un `NetworkManager`: soltar un manager NO cierra su
    // socket — `receive_loop` se queda con un clon del `Arc` — y el puerto seguiria ocupado.
    // El de `std` se cierra al soltarse, y UDP no tiene TIME_WAIT que retrase el reuso.
    let free_port = {
        let probe = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        probe.local_addr().unwrap().port()
    };

    let net = NetworkManager::bind(free_port, 1, 42, true).await.unwrap();
    assert_eq!(
        net.local_addr().port(),
        free_port,
        "el puerto pedido y el puerto escuchado tienen que ser el mismo"
    );
}

#[tokio::test]
async fn an_occupied_port_fails_loudly_instead_of_binding_somewhere_else() {
    // El backend NO busca puerto libre por su cuenta: si el que le dan esta ocupado, tiene que
    // fallar. Elegir otro en silencio es como un host acaba escuchando en un puerto que nadie
    // sabe, y `main` lo convierte en un `expect` visible en el log en vez de una sesion fantasma.
    let holder = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let taken = holder.local_addr().port();

    let second = NetworkManager::bind(taken, 2, 42, true).await;
    assert!(
        second.is_err(),
        "un puerto ya ocupado tiene que dar error de bind, no reubicarse en silencio"
    );
}

// ─── Liveness: el latido y la dirección a la que va ────────────────────────────────────────
//
// Auditoría de heartbeat (2026-08-30). Síntoma físico entre dos PC de la misma LAN: handshake
// correcto, sesión establecida, y ~5 s después expulsión por HEARTBEAT TIMEOUT. En una sola
// máquina no ocurría nunca.
//
// Causa: `build_peer_list` anunciaba la entrada PROPIA del emisor con `net.local_addr()`, que es
// la dirección del socket — y el socket hace bind en `0.0.0.0`. Quien adopta esa dirección como
// endpoint de un peer se manda a sí mismo lo que cree estar mandando al otro, porque `0.0.0.0`
// como destino significa "esta máquina". Con los dos procesos en el mismo PC el datagrama llega
// igual y el defecto es invisible; con un cable de por medio, el otro extremo deja de recibir y
// reapa por silencio. Nada de la red está roto: el latido sale hacia el sitio equivocado.

/// Envejece el último visto de un peer sin tocar el reloj real.
fn age_last_seen(net: &mut NetworkManager, id: PeerId, by: Duration) {
    let peer = net.peers.get_mut(&id).expect("peer registrado");
    peer.last_heartbeat = Instant::now().checked_sub(by).expect("instante válido");
}

#[tokio::test]
async fn the_roster_never_advertises_an_unroutable_endpoint() {
    // La mitad emisora. `net.local_addr()` de un socket en 0.0.0.0 NO es la dirección de nadie, y
    // era lo que viajaba.
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let real_addr: SocketAddr = "127.0.0.1:9810".parse().unwrap();
    host.peers
        .insert(2, peer::PeerConnection::new(2, "Real".into(), real_addr));
    let player = crate::player::Player::new(host.local_id, "Host");

    let list = sync::build_peer_list(&host, &player);

    let own = list
        .iter()
        .find(|p| p.id == host.local_id)
        .expect("el emisor va en su propio roster");
    let parsed: SocketAddr = own.addr.parse().expect("addr parseable");
    assert!(
        !sync::is_routable_peer_addr(&parsed),
        "la entrada propia no puede anunciar un endpoint adoptable: {}",
        own.addr
    );

    let real = list.iter().find(|p| p.id == 2).expect("el peer real va");
    assert_eq!(
        real.addr, "127.0.0.1:9810",
        "el peer real sí viaja con su dirección de verdad — esto NO se toca"
    );
}

#[tokio::test]
async fn a_roster_entry_with_an_unroutable_addr_is_never_registered() {
    // La mitad receptora, que es la que de verdad protege: un build viejo sigue anunciando
    // `0.0.0.0:<puerto>` y no puede envenenar a uno nuevo.
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let host_addr: SocketAddr = "127.0.0.1:9811".parse().unwrap();

    let roster = IncomingPacket {
        addr: host_addr,
        header: PacketHeader::new(protocol::PacketType::PeerList as u16, 1, 0, 0),
        payload: PacketPayload::PeerList {
            peers: vec![
                protocol::PeerInfo {
                    id: 1,
                    name: "Host".into(),
                    addr: "0.0.0.0:7778".into(),
                    position: [0.0, 1.8, 0.0],
                    relay_only: false,
                },
                protocol::PeerInfo {
                    id: 7,
                    name: "PuertoCero".into(),
                    addr: "192.168.1.40:0".into(),
                    position: [0.0, 1.8, 0.0],
                    relay_only: false,
                },
                protocol::PeerInfo {
                    id: 8,
                    name: "Bueno".into(),
                    addr: "192.168.1.41:7779".into(),
                    position: [0.0, 1.8, 0.0],
                    relay_only: false,
                },
            ],
        },
    };
    joiner.handle_packet(roster).await;

    assert!(
        !joiner.peers.contains_key(&1),
        "0.0.0.0 significa 'esta máquina' al enviar: registrarlo es mandarse los latidos a uno mismo"
    );
    assert!(
        !joiner.peers.contains_key(&7),
        "el puerto 0 tampoco es el endpoint de nadie"
    );
    assert_eq!(
        joiner.peers.get(&8).map(|p| p.addr.to_string()),
        Some("192.168.1.41:7779".to_string()),
        "y una dirección buena sí tiene que registrarse — el filtro no puede comerse el caso sano"
    );
}

#[tokio::test]
async fn a_roster_can_never_overwrite_the_host_endpoint_learned_from_the_handshake() {
    // El caso EXACTO del fallo físico, con dos sockets de verdad: el joiner entra, el host le
    // manda su roster, y la dirección por la que el joiner alcanza al host tiene que seguir
    // siendo la del handshake. Si el roster la pisa, el siguiente latido no sale de la máquina.
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let host_addr = loopback_addr(&host);

    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    joiner.process_incoming().await;

    let learned = joiner
        .peers
        .get(&1)
        .map(|p| p.addr)
        .expect("el host queda registrado por el HandshakeAck");
    assert!(sync::is_routable_peer_addr(&learned));

    // El host emite su roster, tal cual lo hace al terminar el world sync.
    let host_player = crate::player::Player::new(host.local_id, "Host");
    let roster = PacketPayload::PeerList {
        peers: sync::build_peer_list(&host, &host_player),
    };
    host.broadcast_unreliable(&roster).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    joiner.process_incoming().await;

    assert_eq!(
        joiner.peers.get(&1).map(|p| p.addr),
        Some(learned),
        "el roster no puede cambiar por dónde se alcanza al host"
    );
}

#[tokio::test]
async fn a_live_heartbeat_actually_reaches_the_host_and_refreshes_its_last_seen() {
    // Prueba de extremo a extremo del latido sobre sockets reales: sale, llega, y mueve
    // `last_heartbeat` en el receptor. Cubre A (nunca sale), B (sale y no llega) y C (llega y no
    // actualiza) de una vez, porque las tres se ven igual desde fuera.
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let host_addr = loopback_addr(&host);

    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    joiner.process_incoming().await;
    let joiner_id = joiner.local_id;

    // Se envejece al joiner en el host hasta el borde del umbral y se comprueba que UN latido lo
    // devuelve a cero. Sin el refresco, el siguiente `check_timeouts` lo expulsaría.
    age_last_seen(
        &mut host,
        joiner_id,
        peer::HEARTBEAT_TIMEOUT - Duration::from_millis(200),
    );
    assert!(
        host.peers[&joiner_id].last_heartbeat.elapsed() > Duration::from_secs(4),
        "el peer tiene que estar al borde para que la prueba signifique algo"
    );

    joiner.send_heartbeats().await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    host.process_incoming().await;

    assert!(
        host.peers[&joiner_id].last_heartbeat.elapsed() < Duration::from_millis(500),
        "el latido llegó y tiene que haber reseteado el último visto"
    );
    assert!(
        host.check_timeouts().is_empty(),
        "y con el último visto fresco nadie puede ser expulsado"
    );
    assert!(host.peers.contains_key(&joiner_id));
}

#[tokio::test]
async fn an_active_peer_is_never_reaped_by_the_liveness_scan() {
    // Falso positivo: el escaneo corre a 1 Hz sobre un umbral de 5 s. Un peer recién visto no
    // puede caer por un error de comparación ni de unidades.
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.peers.insert(
        2,
        peer::PeerConnection::new(2, "Vivo".into(), "127.0.0.1:9812".parse().unwrap()),
    );

    for _ in 0..8 {
        host.peers.get_mut(&2).unwrap().record_heartbeat();
        age_last_seen(&mut host, 2, Duration::from_millis(900));
        assert!(
            host.check_timeouts().is_empty(),
            "900 ms de silencio están MUY dentro del umbral de 5 s"
        );
    }
    assert!(host.peers.contains_key(&2));
}

#[tokio::test]
async fn real_silence_past_the_threshold_does_reap_the_peer() {
    // Control positivo: el mecanismo tiene que seguir matando lo que de verdad está muerto. Sin
    // esto, "arreglar" los falsos positivos podría haber apagado la detección entera.
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.peers.insert(
        2,
        peer::PeerConnection::new(2, "Muerto".into(), "127.0.0.1:9813".parse().unwrap()),
    );

    age_last_seen(
        &mut host,
        2,
        peer::HEARTBEAT_TIMEOUT + Duration::from_millis(100),
    );
    let events = host.check_timeouts();

    assert!(
        events.iter().any(|e| matches!(
            e,
            NetworkEvent::PeerDisconnected { id: 2, reason } if reason == "heartbeat timeout"
        )),
        "un peer realmente callado tiene que caer, y con ese motivo exacto: {events:?}"
    );
    assert!(!host.peers.contains_key(&2));
}

#[tokio::test]
async fn a_peer_stranded_on_an_unroutable_addr_is_never_a_heartbeat_destination() {
    // Última línea: aunque un futuro camino escribiera `peer.addr` sin pasar por el filtro del
    // roster, la ronda de latidos no puede dirigirse a "esta máquina" creyendo que va al otro.
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.peers.insert(
        2,
        peer::PeerConnection::new(2, "Envenenado".into(), "0.0.0.0:7778".parse().unwrap()),
    );
    host.peers.insert(
        3,
        peer::PeerConnection::new(3, "Sano".into(), "192.168.1.41:7779".parse().unwrap()),
    );

    let dests = host.broadcast_destinations();
    let ids: Vec<PeerId> = dests.iter().map(|(id, _)| *id).collect();

    assert!(!ids.contains(&2), "0.0.0.0 no es destino de nadie");
    assert!(
        ids.contains(&3),
        "y el peer sano sigue recibiendo su latido"
    );
}

// ─── El goteo inicial de mundo, sobre sockets reales ───────────────────────────────────────
//
// Auditoría de la vía reliable (2026-08-30). Fallo físico entre dos PC: handshake correcto,
// `session_joined` correcto, latidos correctos, y a los ~6 s de entrar
// `reliable retransmit exhausted (3 reliable + 0 deferred packets lost)`.
//
// Lo medido con instrumentación RELTRACE en localhost: `send_world_sync` emitía 32
// `RELIABLE_SENT` SEGUIDOS —la ventana entera, ~35 KB— dentro de un solo tick y sin una sola
// cesión, más 18 aparcados. `broadcast_chunk_states` manda EXACTAMENTE las mismas cargas y sí
// cede entre páginas, con un comentario que registra la medida que lo obligó (a partir de ~56
// páginas seguidas se perdía al menos una por ronda al desbordar el buffer de recepción). El
// goteo no tenía esa cesión. Y como cada reintento reproducía la misma ráfaga, los mismos
// paquetes volvían a caer hasta agotar `MAX_RETRIES`.
//
// Descartado con medida, no por argumento: el datagrama mayor de todo el goteo son 1205 bytes
// contra 1472 de MTU de Ethernet, así que no había fragmentación (`MTUPROBE ... oversized=0`).

/// Un mundo con `n` chunks reales, que es lo que hace grande al goteo.
fn world_with_chunks(n: i32) -> crate::world::World {
    let mut world = crate::world::World::new(42);
    for i in 0..n {
        world.ensure_chunk((i % 8, i / 8));
    }
    world
}

/// Deja correr al par host↔joiner: cada vuelta drena recepción en los dos lados y barre
/// retransmisiones en el host, igual que hace el bucle de juego.
async fn pump_pair(
    host: &mut NetworkManager,
    joiner: &mut NetworkManager,
    rounds: usize,
) -> Vec<NetworkEvent> {
    let mut events = Vec::new();
    for _ in 0..rounds {
        tokio::time::sleep(Duration::from_millis(20)).await;
        // El seguimiento de completitud lo alimenta el bucle de juego, no `process_incoming`;
        // aquí se hace lo mismo que hace él, para no medir el goteo contra un contador que nadie
        // está moviendo.
        for e in joiner.process_incoming().await {
            match e {
                NetworkEvent::WorldSyncChunkReceived {
                    world_revision,
                    data,
                } => joiner
                    .world_sync_progress
                    .note_chunk(world_revision, data.pos, data.layer),
                NetworkEvent::WorldSyncEndReceived {
                    world_revision,
                    chunk_count,
                } => joiner
                    .world_sync_progress
                    .note_end(world_revision, chunk_count),
                _ => {}
            }
        }
        host.process_incoming().await;
        events.extend(host.process_retransmits().await);
    }
    events
}

async fn connected_pair() -> (NetworkManager, NetworkManager) {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let host_addr = loopback_addr(&host);
    joiner.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    joiner.process_incoming().await;
    assert!(
        host.peers.contains_key(&2) && joiner.peers.contains_key(&1),
        "el par tiene que quedar conectado antes de medir el goteo"
    );
    (host, joiner)
}

#[tokio::test]
async fn a_large_initial_world_sync_never_exhausts_max_retries() {
    // La prueba que faltaba: goteo grande de verdad (60 chunks, por encima de los 49 del mundo
    // que rompió en físico), entregado entero, con el peer VIVO al final.
    let (mut host, mut joiner) = connected_pair().await;
    let world = world_with_chunks(60);
    let player = crate::player::Player::new(1, "Host");

    sync::send_world_sync(&mut host, 2, &world, &player).await;
    let events = pump_pair(&mut host, &mut joiner, 60).await;

    assert!(
        !events.iter().any(|e| matches!(
            e,
            NetworkEvent::PeerDisconnected { reason, .. } if reason == "reliable retransmit exhausted"
        )),
        "el goteo inicial no puede matar al peer al que se lo estás mandando: {events:?}"
    );
    assert!(
        host.peers.contains_key(&2),
        "y el peer tiene que seguir en la sesión después de sincronizar"
    );

    let peer = &host.peers[&2];
    assert!(
        peer.reliable_queue.is_empty(),
        "todo el goteo tenía que quedar confirmado; quedan {} sin ACK",
        peer.reliable_queue.len()
    );
    assert!(
        peer.deferred_reliable.is_empty(),
        "y la cola diferida tenía que drenar entera; quedan {}",
        peer.deferred_reliable.len()
    );
}

#[tokio::test]
async fn the_whole_world_actually_lands_on_the_joiner() {
    // Fiabilidad y orden intactos: ceder no puede costar un chunk. Se comprueba contra el
    // seguimiento de completitud del receptor, que es quien decide si el mundo llegó entero.
    let (mut host, mut joiner) = connected_pair().await;
    let chunks = 60;
    let world = world_with_chunks(chunks);
    let player = crate::player::Player::new(1, "Host");

    sync::send_world_sync(&mut host, 2, &world, &player).await;
    pump_pair(&mut host, &mut joiner, 60).await;

    assert!(
        joiner.world_sync_progress.is_complete(),
        "el goteo tiene que completarse en el receptor, no solo salir del emisor"
    );
}

#[tokio::test]
async fn the_world_sync_burst_yields_instead_of_filling_the_window_in_one_go() {
    // La propiedad que de verdad se corrigió, medida en vez de argumentada: cuántos paquetes
    // salen SIN que nada más pueda correr entre medias.
    //
    // Un contador dentro de una tarea concurrente cuenta las oportunidades de ejecución que el
    // goteo cede. Sin las cesiones el goteo entero cabe entre dos puntos de espera y el contador
    // se queda en cero; con ellas, el runtime intercala — que es exactamente lo que permite al
    // bucle de recepción drenar el socket y a los ACK volver.
    let (mut host, mut joiner) = connected_pair().await;
    let world = world_with_chunks(60);
    let player = crate::player::Player::new(1, "Host");

    let ticks = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let ticks_bg = ticks.clone();
    let bg = tokio::spawn(async move {
        for _ in 0..10_000 {
            ticks_bg.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            tokio::task::yield_now().await;
        }
    });

    sync::send_world_sync(&mut host, 2, &world, &player).await;
    let interleaved = ticks.load(std::sync::atomic::Ordering::Relaxed);
    bg.abort();

    assert!(
        interleaved >= 32,
        "el goteo tiene que ceder al menos una vez por paquete de la ventana; solo cedió {interleaved} veces"
    );

    // Y el goteo sigue entregando lo mismo pese a ceder.
    pump_pair(&mut host, &mut joiner, 60).await;
    assert!(joiner.world_sync_progress.is_complete());
}

#[tokio::test]
async fn a_retransmit_wave_is_not_re_sent_as_one_unbroken_burst() {
    // El segundo punto de ráfaga, y el que convierte una pérdida puntual en cinco seguidas: los
    // paquetes de una misma ráfaga reciben su plazo de reenvío en el mismo instante, así que
    // vencen juntos. Sin cesión, la ola de reenvío es idéntica a la ráfaga que los perdió.
    let (mut host, _joiner) = connected_pair().await;
    let world = world_with_chunks(40);
    let player = crate::player::Player::new(1, "Host");

    // Se emite el goteo y NO se drena al receptor: nadie confirma, así que todo vence a la vez.
    sync::send_world_sync(&mut host, 2, &world, &player).await;
    let in_flight = host.peers[&2].reliable_queue.len();
    assert!(in_flight > 1, "hace falta más de un paquete en vuelo");
    tokio::time::sleep(Duration::from_millis(260)).await;

    let ticks = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let ticks_bg = ticks.clone();
    let bg = tokio::spawn(async move {
        for _ in 0..10_000 {
            ticks_bg.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            tokio::task::yield_now().await;
        }
    });
    host.process_retransmits().await;
    let interleaved = ticks.load(std::sync::atomic::Ordering::Relaxed);
    bg.abort();

    assert!(
        interleaved >= in_flight,
        "la ola de reenvío tiene que ceder entre paquetes; {in_flight} reenvíos y solo {interleaved} cesiones"
    );
}

#[tokio::test]
async fn the_reliable_window_is_still_respected_after_the_fix() {
    // Control: ceder no puede haber relajado el backpressure. La ventana sigue siendo 32 y lo
    // que no cabe sigue APARCÁNDOSE (nunca descartándose), que es el contrato de ADR-060.
    let (mut host, _joiner) = connected_pair().await;
    let world = world_with_chunks(60);
    let player = crate::player::Player::new(1, "Host");

    sync::send_world_sync(&mut host, 2, &world, &player).await;

    let peer = &host.peers[&2];
    assert_eq!(
        peer.reliable_queue.len(),
        reliability::WINDOW_SIZE,
        "en vuelo tiene que haber exactamente la ventana, ni uno más"
    );
    // El total esperado se DERIVA de la paginación, no se escribe a mano. La versión anterior
    // fijaba 61 (60 chunks + End) porque entonces un chunk era siempre un paquete; desde la
    // auditoría de MTU un chunk denso viaja en varias páginas, así que ese número codificaba una
    // suposición que la paginación invalida a propósito. Lo que se comprueba —que no se descarta
    // nada— es lo mismo; lo que cambia es de dónde sale el número.
    let expected: usize = world
        .chunks
        .values()
        .map(|c| sync::chunk_to_sync_pages(c, world.revision).len())
        .sum::<usize>()
        + 1; // + WorldSyncEnd
    assert!(
        expected > world.chunks.len(),
        "setup: este mundo tiene que tener algún chunk paginado o el test no prueba nada"
    );
    assert_eq!(
        peer.reliable_queue.len() + peer.deferred_reliable.len(),
        expected,
        "todas las páginas + End: nada puede haberse descartado por el camino"
    );
}

// ─── El techo de transporte, y la paginación que lo hace cumplir ───────────────────────────
//
// Auditoría de MTU (2026-08-30). Medido en la sesión física de 9 min: 31.004 datagramas por
// encima de 1472 B, máximo 1881 B, y los ÚNICOS 4 reenvíos de toda la sesión fueron los 4
// datagramas oversized — ninguno por debajo del límite se perdió. Los fiables oversized eran
// todos `type=0x36` (`WorldSyncChunk`).
//
// Reparto de bytes medido de un chunk real: cabecera fija (con `layout`) 754 B, ~87 B por
// entidad. Por eso la unidad de división son las listas y no la cabecera: repetirla en cada
// página cuesta menos que cualquier esquema que la separase, y hace cada página autosuficiente,
// que es lo que permite aplicarlas fuera de orden.

fn dense_chunk_world(entities_per_chunk: usize) -> crate::world::World {
    let mut world = crate::world::World::new(42);
    for i in 0..6 {
        let chunk = world.ensure_chunk((i % 3, i / 3));
        // Entidades sintéticas SOBRE un chunk real: lo que desborda el datagrama es la lista, y
        // la sesión física llegó a ~13 entidades en un chunk.
        for n in 0..entities_per_chunk {
            let e = crate::world::entity::Entity::new(
                (i as u32) * 1000 + n as u32,
                crate::world::entity::EntityType::Lurker,
                crate::utils::Vec3::new(n as f32, 1.8, i as f32),
            );
            chunk.entities.push(e);
        }
    }
    world
}

/// Los volúmenes entre capas que el generador REAL cuelga de un chunk conector
/// (`world::levels::level_0::v30a_showcase::add_connector_inter_layer_volumes`). Se reproducen
/// aquí con sus cadenas literales porque el generador es privado y lo que rompe es el TAMAÑO de
/// esas cadenas, no la lógica que las elige: copiarlas es copiar exactamente lo que viaja.
fn showcase_inter_layer_volumes(target_layer: i8) -> Vec<crate::world::chunk::InterLayerVolumeV0> {
    use crate::world::chunk::{InterLayerVolumeKindV0 as K, InterLayerVolumeV0};
    let mk = |index: u32,
              kind: K,
              min: [u8; 2],
              max: [u8; 2],
              safety: &str,
              audio: &str,
              hints: &[&str]| InterLayerVolumeV0 {
        volume_id: index,
        kind,
        base_chunk: [-4, -2],
        involved_layers: vec![0, target_layer],
        footprint_cell_min: min,
        footprint_cell_max: max,
        safety_type: safety.into(),
        future_audio_hint: audio.into(),
        visual_flags: 0x0f,
        visual_hints: hints.iter().map(|h| (*h).to_string()).collect(),
    };
    vec![
        mk(
            0,
            K::ServiceShaft,
            [2, 0],
            [8, 10],
            "BACKEND_AUTHORED_VISUAL_NO_FALL",
            "service_shaft_hum_from_lower_layer",
            &["shaft_walls", "railing_runs", "matched_receiving_space"],
        ),
        mk(
            1,
            K::StackedCorridorPair,
            [2, 0],
            [8, 10],
            "BACKEND_AUTHORED_ALIGNMENT",
            "stacked_corridor_air_path",
            &["matching_corridor_axis", "ceiling_floor_alignment"],
        ),
        mk(
            2,
            K::UnderfloorServiceZone,
            [1, 1],
            [9, 9],
            "VISUAL_HINT_ONLY",
            "underfloor_service_void",
            &["open_floor_service_grates", "subfloor_cable_trays"],
        ),
        mk(
            3,
            K::AtriumStack,
            [1, 1],
            [9, 9],
            "BACKEND_AUTHORED_BLOCKED_SHAFT_NO_FALL",
            "atrium_vertical_reverb",
            &["shared_opening", "shaft_wall_panels", "lower_room_cues"],
        ),
        mk(
            4,
            K::OverlookRoom,
            [1, 1],
            [9, 9],
            "VISUAL_OVERLOOK_WITH_RAILING",
            "lower_room_floor_reflection",
            &[
                "visible_lower_room",
                "overlook_railings",
                "depth_floor_patch",
            ],
        ),
        mk(
            5,
            K::GiantPillarSpan,
            [1, 1],
            [9, 9],
            "STRUCTURAL_VISUAL_SUPPORT",
            "pillar_span_occlusion",
            &[
                "layer_spanning_pillars",
                "pillar_caps_visible_across_layers",
            ],
        ),
        mk(
            6,
            K::CeilingActivityZone,
            [1, 1],
            [9, 9],
            "VISUAL_HINT_ONLY",
            "muffled_ceiling_activity",
            &["ceiling_service_panels", "upper_layer_activity_hint"],
        ),
    ]
}

/// **EL FALLO MEDIDO EN LA SESIÓN FÍSICA POR INTERNET DEL 2026-08-31 (19:02–19:17 UTC).**
///
/// 1.176 `datagram_refused_over_budget` hacia un solo joiner (31.4.151.22:7695), de 1612 a 1910 B,
/// y cada uno precedido 1:1 por su `chunk_header_exceeds_budget`. Dos chunks —(-4,-1) y (-4,-2)—
/// no llegaron NUNCA: ni por el goteo fiable del join, ni por el `ChunkState` a 10 Hz que debería
/// curarlo.
///
/// La causa no es la densidad de entidades, que es lo que `split_chunk_pages` sabe partir. Es que
/// la CABECERA del chunk no es de tamaño fijo: `ChunkLayoutV1.inter_layer_volumes` es un `Vec`
/// sin cota, y cada volumen lleva DOS `String` (`safety_type`, `future_audio_hint`) más un
/// `Vec<String>` de pistas visuales. Siete volúmenes —lo que el generador cuelga de un chunk
/// conector— suman ~1.160 B sobre los ~750 de la cabecera medida cuando se escribió la
/// paginación, y el comentario de `split_chunk_pages` que dice que esto «solo puede ocurrir si
/// `layout` crece más allá de lo medido (hoy `LAYOUT_GRID_SIZE` es 10 y fijo)» mira al campo
/// equivocado: `layout` creció por otro sitio.
///
/// Vaciar `entities`/`items` es deliberado: aísla los volúmenes como ÚNICA causa del desborde.
/// Antes de la corrección este test fallaba con una página de 2.701 B — peor que los 1.910 de
/// campo, porque los chunks reales llevaban menos volúmenes que los siete del generador.
#[test]
fn a_chunk_whose_layout_carries_inter_layer_volumes_never_fragments() {
    let mut world = dense_chunk_world(0);
    for chunk in world.chunks.values_mut() {
        chunk.entities.clear();
        chunk.items.clear();
        chunk.layout.inter_layer_volumes = showcase_inter_layer_volumes(-1);
    }

    for chunk in world.chunks.values() {
        for page in &sync::chunk_to_sync_pages(chunk, 1) {
            let bytes = encoded_bytes(page);
            assert!(
                bytes <= protocol::SAFE_DATAGRAM_BYTES,
                "un chunk con {} volúmenes entre capas produce una página de {bytes} B > {} B: \
                 `send_datagram` la RECHAZA y el chunk no llega jamás — ni por el goteo fiable \
                 ni por el ChunkState que debería curarlo",
                chunk.layout.inter_layer_volumes.len(),
                protocol::SAFE_DATAGRAM_BYTES
            );
        }
    }
}

/// No perder nada es la mitad del contrato; la otra mitad es reunirlo IGUAL. Un volumen que se
/// pierde o se reordena entre páginas no rompe ningún test de tamaño y deja al cliente pintando
/// una arquitectura que el servidor no tiene.
#[test]
fn the_inter_layer_volumes_survive_the_round_trip_through_the_assembler() {
    let mut world = dense_chunk_world(0);
    for chunk in world.chunks.values_mut() {
        chunk.entities.clear();
        chunk.items.clear();
        chunk.layout.inter_layer_volumes = showcase_inter_layer_volumes(-1);
    }

    for chunk in world.chunks.values() {
        let original = sync::chunk_to_sync_data(chunk);
        let pages = sync::chunk_to_sync_pages(chunk, 1);
        assert!(
            pages.len() > 1,
            "setup: siete volúmenes no caben en una página"
        );

        let mut asm = sync::ChunkPageAssembler::default();
        let mut merged = None;
        for page in &pages {
            if let Some(done) = asm.offer(1, page.clone()) {
                merged = Some(done);
            }
        }
        let merged = merged.expect("con todas las páginas tiene que ensamblar");

        assert_eq!(
            merged.layout.inter_layer_volumes, original.layout.inter_layer_volumes,
            "los volúmenes se reúnen exactamente, en su orden y con sus cadenas"
        );
        assert_eq!(merged.layout.cells, original.layout.cells);
        assert_eq!(merged.layout.edges_v, original.layout.edges_v);
        assert_eq!(merged.layout.edges_h, original.layout.edges_h);
    }
}

/// Las tres listas a la vez. Paginarlas por separado es fácil; lo que rompe es que compartan
/// presupuesto — una página puede acabar llevando el último volumen y las tres primeras entidades.
#[test]
fn volumes_entities_and_items_all_reassemble_from_the_same_pages() {
    let mut world = dense_chunk_world(40);
    for chunk in world.chunks.values_mut() {
        chunk.layout.inter_layer_volumes = showcase_inter_layer_volumes(-2);
    }

    for chunk in world.chunks.values() {
        let original = sync::chunk_to_sync_data(chunk);
        let pages = sync::chunk_to_sync_pages(chunk, 1);
        for page in &pages {
            let bytes = encoded_bytes(page);
            assert!(
                bytes <= protocol::SAFE_DATAGRAM_BYTES,
                "página de {bytes} B con volúmenes Y entidades"
            );
        }

        let mut asm = sync::ChunkPageAssembler::default();
        let mut merged = None;
        for page in &pages {
            if let Some(done) = asm.offer(1, page.clone()) {
                merged = Some(done);
            }
        }
        let merged = merged.expect("ensambla");

        let got: Vec<u32> = merged.entities.iter().map(|e| e.id).collect();
        let want: Vec<u32> = original.entities.iter().map(|e| e.id).collect();
        assert_eq!(got, want, "las entidades no se pierden ni se reordenan");
        assert_eq!(merged.items.len(), original.items.len());
        assert_eq!(
            merged.layout.inter_layer_volumes, original.layout.inter_layer_volumes,
            "y los volúmenes tampoco"
        );
    }
}

/// La quimera que `generation` existe para impedir, ahora que también parte los volúmenes: coser
/// la página 0 de una ronda con la 1 de otra produciría una lista de volúmenes que nunca existió.
#[test]
fn two_generations_of_volume_pages_never_splice_into_one_chunk() {
    let mut world = dense_chunk_world(0);
    for chunk in world.chunks.values_mut() {
        chunk.entities.clear();
        chunk.items.clear();
        chunk.layout.inter_layer_volumes = showcase_inter_layer_volumes(-1);
    }
    let chunk = world.chunks.values().next().expect("hay chunks");

    let data = sync::chunk_to_sync_data(chunk);
    let round_a = sync::chunk_state_pages(data.clone(), 7);
    let mut altered = data;
    altered.layout.inter_layer_volumes[0].safety_type = "DIFFERENT_IN_ROUND_B".into();
    let round_b = sync::chunk_state_pages(altered, 9);
    assert!(
        round_a.len() > 1 && round_b.len() > 1,
        "setup: ambas parten"
    );

    let mut asm = sync::ChunkPageAssembler::default();
    // Página 0 de A, luego TODA la ronda B. Lo único que puede salir es B entera.
    assert!(
        asm.offer(7, round_a[0].clone()).is_none(),
        "una página suelta no ensambla nada"
    );
    let mut merged = None;
    for page in &round_b {
        if let Some(done) = asm.offer(9, page.clone()) {
            merged = Some(done);
        }
    }
    let merged = merged.expect("la ronda B completa sí ensambla");
    assert_eq!(
        merged.layout.inter_layer_volumes[0].safety_type, "DIFFERENT_IN_ROUND_B",
        "el resultado es B ENTERA: ni un volumen de la ronda A se ha colado"
    );
    assert_eq!(
        asm.pending_len(),
        0,
        "y el parcial de la ronda vieja no se queda ocupando sitio"
    );
}

/// El techo, medido sobre el sobre REAL de cada portador y no solo sobre el de `WorldSyncChunk`:
/// `ChunkState` es el que emitió los 1.172 rechazos de la sesión física.
#[test]
fn no_carrier_produces_an_oversized_page_from_a_volume_heavy_chunk() {
    let mut world = dense_chunk_world(12);
    for chunk in world.chunks.values_mut() {
        chunk.layout.inter_layer_volumes = showcase_inter_layer_volumes(-1);
    }

    for chunk in world.chunks.values() {
        let data = sync::chunk_to_sync_data(chunk);
        let budget = protocol::SAFE_DATAGRAM_BYTES;

        for page in &sync::chunk_state_pages(data.clone(), 3) {
            let len = sync::ChunkCarrier::State.encoded_len(page);
            assert!(len <= budget, "ChunkState: {len} B > {budget} B");
        }
        for page in &sync::chunk_transfer_pages(data.clone(), 3) {
            let len = sync::ChunkCarrier::Transfer.encoded_len(page);
            assert!(len <= budget, "ChunkTransfer: {len} B > {budget} B");
        }
        for page in &sync::chunk_to_sync_pages(chunk, 1) {
            let len = sync::ChunkCarrier::WorldSync { world_revision: 1 }.encoded_len(page);
            assert!(len <= budget, "WorldSyncChunk: {len} B > {budget} B");
        }
    }
}

/// El otro extremo del contrato, extremo a extremo por el camino FIABLE real
/// (`send_chunk_transfer` → `send_reliable_queued`): un chunk con los siete volúmenes sale ENTERO,
/// en varias páginas, sin que el techo rechace ni una.
///
/// **Lo que este test NO demuestra**, y por eso se dice aquí: con la ventana holgada las páginas
/// salen directas, así que no llega a ejercitar la rama de aparcado. Lo que cubre el aparcado es
/// `a_deferred_reliable_refused_by_the_ceiling_is_never_queued_either` (ADR-113 enm. 1). Los dos
/// juntos son la comprobación que pide la regla 13: ni se toca `send_reliable_queued` ni
/// `pump_deferred_reliable`, se demuestra que ya no tienen nada sobredimensionado que manejar.
#[tokio::test]
async fn a_volume_heavy_chunk_travels_whole_through_the_reliable_path() {
    let (mut host, _joiner) = connected_pair().await;
    let peer_id = *host.peers.keys().next().expect("hay un peer");

    let mut world = dense_chunk_world(0);
    for chunk in world.chunks.values_mut() {
        chunk.entities.clear();
        chunk.items.clear();
        chunk.layout.inter_layer_volumes = showcase_inter_layer_volumes(-1);
    }
    let chunk = world.chunks.values().next().expect("hay chunks").clone();

    sync::send_chunk_transfer(&mut host, peer_id, &chunk).await;
    host.pump_deferred_reliable().await;

    assert_eq!(
        host.refused_datagram_count(),
        0,
        "ni una página del handoff de un chunk con volúmenes cruza el techo"
    );
    assert_eq!(
        host.peers[&peer_id].deferred_reliable.len(),
        0,
        "y no queda ninguna aparcada"
    );
    assert!(
        host.peers[&peer_id].reliable_queue.len() > 1,
        "setup: el chunk se partió de verdad, así que hay varias páginas en vuelo"
    );
}

/// La medida que la tarea pide como evidencia: el tamaño MÁXIMO que produce el paginador sobre el
/// peor chunk que sabemos construir —siete volúmenes reales Y 40 entidades— por los tres
/// portadores. Falla si algún día se acerca al techo, que es cuando hay que mirar otra vez.
#[test]
fn the_worst_page_the_paginator_can_produce_stays_under_the_ceiling() {
    let mut world = dense_chunk_world(40);
    for chunk in world.chunks.values_mut() {
        chunk.layout.inter_layer_volumes = showcase_inter_layer_volumes(-1);
    }

    let mut worst = 0usize;
    for chunk in world.chunks.values() {
        let data = sync::chunk_to_sync_data(chunk);
        for page in &sync::chunk_state_pages(data.clone(), 3) {
            worst = worst.max(sync::ChunkCarrier::State.encoded_len(page));
        }
        for page in &sync::chunk_transfer_pages(data.clone(), 3) {
            worst = worst.max(sync::ChunkCarrier::Transfer.encoded_len(page));
        }
        for page in &sync::chunk_to_sync_pages(chunk, 1) {
            worst =
                worst.max(sync::ChunkCarrier::WorldSync { world_revision: 1 }.encoded_len(page));
        }
    }

    println!(
        "PAGINA MAXIMA OBSERVADA: {worst} B (techo {})",
        protocol::SAFE_DATAGRAM_BYTES
    );
    assert!(
        worst <= protocol::SAFE_DATAGRAM_BYTES,
        "página máxima {worst} B > {} B",
        protocol::SAFE_DATAGRAM_BYTES
    );
}

fn encoded_bytes(data: &protocol::ChunkSyncData) -> usize {
    let payload = PacketPayload::WorldSyncChunk {
        world_revision: 1,
        data: data.clone(),
    };
    let header = PacketHeader::new(payload.type_code(), 1, 1, 0);
    protocol::encode_packet(&header, &payload).len()
}

#[test]
fn no_world_sync_page_can_exceed_the_transport_budget() {
    // La invariante central. 40 entidades por chunk es ~3x lo peor visto en físico.
    let world = dense_chunk_world(40);
    for chunk in world.chunks.values() {
        let pages = sync::chunk_to_sync_pages(chunk, 1);
        assert!(!pages.is_empty(), "siempre al menos una página");
        for page in &pages {
            let bytes = encoded_bytes(page);
            assert!(
                bytes <= protocol::SAFE_DATAGRAM_BYTES,
                "una página de WorldSync no puede fragmentar: {bytes} B > {} B",
                protocol::SAFE_DATAGRAM_BYTES
            );
        }
    }
}

#[test]
fn a_chunk_that_already_fits_still_travels_as_a_single_page() {
    // Control: la paginación no puede encarecer el caso común. Un chunk que CABE sigue siendo un
    // solo datagrama, sin cabeceras repetidas ni ensamblado en el receptor.
    //
    // TAREA 2 (2026-08-31): el chunk de control se vacía a propósito. Antes bastaba con
    // `dense_chunk_world(0)`, pero un chunk generado trae su botín y hoy eso ya no cabe: la
    // cabecera desnuda mide ~1050 B de los 1200 del techo (557 son `layout`), así que un puñado de
    // items la cruza. Ese margen es un hecho medido del formato, no un fallo de este test — lo que
    // el test defiende es que por debajo del techo NO se pagina, y para eso hace falta un chunk que
    // esté por debajo del techo.
    let mut world = dense_chunk_world(0);
    for chunk in world.chunks.values_mut() {
        chunk.entities.clear();
        chunk.items.clear();
    }
    for chunk in world.chunks.values() {
        let bare = sync::ChunkCarrier::WorldSync { world_revision: 1 }
            .encoded_len(&sync::chunk_to_sync_data(chunk));
        assert!(
            bare <= protocol::SAFE_DATAGRAM_BYTES,
            "invariante de formato: la cabecera desnuda de un chunk tiene que caber en un              datagrama, o no hay paginación que lo salve ({bare} B > {} B)",
            protocol::SAFE_DATAGRAM_BYTES
        );
        let pages = sync::chunk_to_sync_pages(chunk, 1);
        assert_eq!(pages.len(), 1, "un chunk que cabe no se parte");
        assert_eq!(pages[0].page, 0);
        assert_eq!(pages[0].page_count, 1);
    }
}

#[test]
fn paging_loses_nothing_and_keeps_order() {
    // Fiabilidad: partir y reunir tiene que devolver EXACTAMENTE lo que había, en el mismo orden.
    let world = dense_chunk_world(40);
    for chunk in world.chunks.values() {
        let original = sync::chunk_to_sync_data(chunk);
        let pages = sync::chunk_to_sync_pages(chunk, 1);
        assert!(pages.len() > 1, "setup: este chunk tiene que partirse");

        let mut asm = sync::ChunkPageAssembler::default();
        let mut merged = None;
        for page in &pages {
            if let Some(done) = asm.offer(1, page.clone()) {
                merged = Some(done);
            }
        }
        let merged = merged.expect("con todas las páginas tiene que ensamblar");

        let ids: Vec<u32> = merged.entities.iter().map(|e| e.id).collect();
        let want: Vec<u32> = original.entities.iter().map(|e| e.id).collect();
        assert_eq!(ids, want, "ni se pierde ni se reordena una entidad");
        assert_eq!(merged.items.len(), original.items.len());
        assert_eq!(merged.pos, original.pos);
        assert_eq!(merged.layer, original.layer);
        assert_eq!(merged.layout.cells, original.layout.cells);
    }
}

#[test]
fn pages_assemble_out_of_order_and_survive_duplicates() {
    // La razón ENTERA de ensamblar en vez de aplicar página a página: la capa reliable es
    // at-least-once y SIN orden. Si la 1 adelanta a la 0, o la 0 llega dos veces tras un ACK
    // perdido, el resultado tiene que ser el mismo.
    let world = dense_chunk_world(40);
    let chunk = world.chunks.values().next().expect("hay chunks");
    let original = sync::chunk_to_sync_data(chunk);
    let mut pages = sync::chunk_to_sync_pages(chunk, 1);
    assert!(pages.len() > 1);

    pages.reverse(); // orden invertido
    let mut asm = sync::ChunkPageAssembler::default();
    let mut merged = None;
    // La primera se entrega DOS veces: duplicado real de una retransmisión.
    let dup = pages[0].clone();
    for page in std::iter::once(dup).chain(pages.iter().cloned()) {
        if let Some(done) = asm.offer(1, page) {
            merged = Some(done);
        }
    }
    let merged = merged.expect("desordenado y con duplicado tiene que ensamblar igual");
    let ids: Vec<u32> = merged.entities.iter().map(|e| e.id).collect();
    let want: Vec<u32> = original.entities.iter().map(|e| e.id).collect();
    assert_eq!(
        ids, want,
        "el orden lo fija el índice de página, no la llegada"
    );
    assert_eq!(asm.pending_len(), 0, "no puede quedar nada aparcado");
}

#[test]
fn an_incomplete_chunk_is_never_applied() {
    // Pérdida silenciosa: con una página en vuelo, el ensamblador NO debe entregar nada. Aplicar
    // lo que hay dejaría el chunk con la mitad de sus entidades y sin forma de saberlo.
    let world = dense_chunk_world(40);
    let chunk = world.chunks.values().next().expect("hay chunks");
    let pages = sync::chunk_to_sync_pages(chunk, 1);
    assert!(pages.len() > 1);

    let mut asm = sync::ChunkPageAssembler::default();
    for page in pages.iter().take(pages.len() - 1) {
        assert!(
            asm.offer(1, page.clone()).is_none(),
            "sin la última página no se entrega nada"
        );
    }
    assert_eq!(asm.pending_len(), 1, "queda exactamente un chunk aparcado");
}

#[test]
fn a_superseded_revision_does_not_pollute_the_new_one() {
    // Una revisión nueva invalida el goteo anterior: sus rezagados no pueden mezclarse con el
    // nuevo ni quedarse en memoria para toda la sesión.
    let world = dense_chunk_world(40);
    let chunk = world.chunks.values().next().expect("hay chunks");
    let pages = sync::chunk_to_sync_pages(chunk, 1);

    let mut asm = sync::ChunkPageAssembler::default();
    asm.offer(1, pages[0].clone());
    assert_eq!(asm.pending_len(), 1);

    asm.drop_stale(2);
    assert_eq!(
        asm.pending_len(),
        0,
        "lo aparcado de una revisión vieja se tira"
    );
}

#[tokio::test]
async fn a_real_world_sync_puts_nothing_oversized_on_the_wire() {
    // Extremo a extremo sobre sockets reales: se sincroniza un mundo denso ENTERO y se comprueba
    // que el emisor no produjo un solo datagrama fiable por encima del techo. Es la diferencia
    // entre "las páginas miden bien" y "lo que sale por el socket mide bien".
    let (mut host, mut joiner) = connected_pair().await;
    let world = dense_chunk_world(40);
    let player = crate::player::Player::new(1, "Host");

    sync::send_world_sync(&mut host, 2, &world, &player).await;
    let events = pump_pair(&mut host, &mut joiner, 80).await;

    assert_eq!(
        host.oversized_reliable_count(),
        0,
        "ningún datagrama fiable puede superar {} B",
        protocol::SAFE_DATAGRAM_BYTES
    );
    assert!(
        !events.iter().any(|e| matches!(
            e,
            NetworkEvent::PeerDisconnected { reason, .. } if reason == "reliable retransmit exhausted"
        )),
        "y el goteo no puede matar al peer: {events:?}"
    );
    assert!(
        joiner.world_sync_progress.is_complete(),
        "el mundo tiene que llegar completo pese a ir paginado"
    );
}

#[test]
fn a_lost_page_cannot_leak_memory_forever() {
    // La fuga que introduce la propia paginación: una página perdida deja su parcial aparcado y
    // nadie lo reclama —la revisión no cambia y el goteo siguiente estrena claves nuevas—. Se
    // acota con desalojo del más viejo, igual que `BoundedDedupeSet`.
    let world = dense_chunk_world(40);
    let chunk = world.chunks.values().next().expect("hay chunks");
    let pages = sync::chunk_to_sync_pages(chunk, 1);
    assert!(pages.len() > 1, "setup: hace falta un chunk partido");

    let mut asm = sync::ChunkPageAssembler::default();
    // 500 chunks distintos, de cada uno solo la primera página: ninguno completa jamás.
    for n in 0..500i32 {
        let mut partial = pages[0].clone();
        partial.pos = [n, n];
        assert!(asm.offer(1, partial).is_none());
    }

    assert!(
        asm.pending_len() <= 128,
        "lo aparcado tiene que estar acotado; hay {}",
        asm.pending_len()
    );
}

#[test]
fn the_cap_never_drops_a_chunk_that_is_still_completing() {
    // Control: el desalojo no puede comerse un chunk que SÍ está llegando. Se completa uno
    // mientras otros 300 parciales entran y salen por el tope.
    let world = dense_chunk_world(40);
    let chunk = world.chunks.values().next().expect("hay chunks");
    let pages = sync::chunk_to_sync_pages(chunk, 1);

    let mut asm = sync::ChunkPageAssembler::default();
    for page in pages.iter().take(pages.len() - 1) {
        assert!(asm.offer(1, page.clone()).is_none());
    }
    // Ruido por debajo del tope: el chunk real sigue siendo de los más recientes.
    for n in 0..100i32 {
        let mut partial = pages[0].clone();
        partial.pos = [1000 + n, 1000 + n];
        asm.offer(1, partial);
    }
    let last = pages.last().expect("hay páginas").clone();
    assert!(
        asm.offer(1, last).is_some(),
        "el chunk que estaba completándose no puede haber sido desalojado"
    );
}

// ─────────────────────────────────────────────────────────────────────────────
// B — La topología es una ESTRELLA, y los destinos tienen que decirlo
// ─────────────────────────────────────────────────────────────────────────────

/// Peer de mentira con una dirección enrutable distinta por id, para poder distinguir destinos.
fn fake_peer(id: PeerId, last_octet: u8) -> PeerConnection {
    PeerConnection::new(
        id,
        format!("peer{id}"),
        format!("192.168.1.{last_octet}:7778").parse().unwrap(),
    )
}

/// **EL TEST QUE REPRODUCE EL DEFECTO.** `broadcast_destinations` devolvía TODOS los peers
/// registrados sin mirar el rol, y un joiner registra a los otros joiners **con la dirección real
/// que el host le reporta** en `PeerList` (`handlers.rs:219-221`, `PeerInfo.addr`). Resultado: con
/// 3+ jugadores, cada joiner emitía su pose también DIRECTAMENTE a los demás joiners.
///
/// Eso no es la arquitectura: ADR-015 dice estrella, el host reemite. Y entre redes distintas esos
/// datagramas los tira el NAT — el juego seguía funcionando por la estrella, pero cada rebote
/// vuelve en Windows como `WSAECONNRESET (10054)` sobre el socket del emisor, que es exactamente
/// el mecanismo que ADR-043 midió en 1.073.132 líneas de un solo playtest.
#[tokio::test]
async fn a_joiner_only_broadcasts_to_the_host() {
    let mut joiner = NetworkManager::bind(0, 7, 42, false).await.unwrap();
    joiner.peers.insert(1, fake_peer(1, 40));
    joiner.host_peer_id = Some(1);
    joiner.peers.insert(9, fake_peer(9, 41));

    let dests: Vec<PeerId> = joiner
        .broadcast_destinations()
        .into_iter()
        .map(|(id, _)| id)
        .collect();

    assert_eq!(
        dests,
        vec![1],
        "un joiner sólo habla con el host; el peer 9 es otro joiner y no es asunto suyo"
    );
}

/// La otra mitad de la estrella: el host sí habla con todos. Si el filtro se aplicara a los dos
/// roles, los joiners dejarían de recibir nada y el arreglo sería peor que el defecto.
#[tokio::test]
async fn the_host_broadcasts_to_every_real_peer() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.peers.insert(2, fake_peer(2, 41));
    host.peers.insert(3, fake_peer(3, 42));

    let mut dests: Vec<PeerId> = host
        .broadcast_destinations()
        .into_iter()
        .map(|(id, _)| id)
        .collect();
    dests.sort_unstable();

    assert_eq!(dests, vec![2, 3], "el host reemite a todos: es la estrella");
}

/// Un roster viejo no puede abrir rutas directas. Aunque el host haya reportado la dirección real
/// de otro joiner —y lo hace, `PeerInfo` lleva `addr`—, registrarla no la convierte en un destino.
#[tokio::test]
async fn a_stale_roster_cannot_create_a_direct_joiner_to_joiner_route() {
    let mut joiner = NetworkManager::bind(0, 7, 42, false).await.unwrap();
    joiner.peers.insert(1, fake_peer(1, 40));
    joiner.host_peer_id = Some(1);

    // Lo que haría el manejador de PeerList con el roster del host.
    for (id, octet) in [(2u16, 41u8), (3, 42), (4, 43)] {
        joiner.peers.insert(id, fake_peer(id, octet));
    }

    let dests: Vec<PeerId> = joiner
        .broadcast_destinations()
        .into_iter()
        .map(|(id, _)| id)
        .collect();

    assert_eq!(dests, vec![1], "cuatro peers en el roster, un solo destino");
}

/// Tres jugadores es donde el defecto se hacía alcanzable: con uno solo, el único peer del joiner
/// ES el host y no había nada que distinguir.
#[tokio::test]
async fn three_players_produce_no_peer_to_peer_traffic() {
    let mut a = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    a.peers.insert(1, fake_peer(1, 40));
    a.host_peer_id = Some(1);
    a.peers.insert(3, fake_peer(3, 42)); // el otro joiner, B

    let mut b = NetworkManager::bind(0, 3, 42, false).await.unwrap();
    b.peers.insert(1, fake_peer(1, 40));
    b.host_peer_id = Some(1);
    b.peers.insert(2, fake_peer(2, 41)); // el otro joiner, A

    for (label, net) in [("A", &a), ("B", &b)] {
        let dests: Vec<PeerId> = net
            .broadcast_destinations()
            .into_iter()
            .map(|(id, _)| id)
            .collect();
        assert_eq!(dests, vec![1], "el joiner {label} no debe emitir a su par");
    }
}

/// Un joiner que todavía no ha completado el handshake no tiene `host_peer_id`, y entonces no
/// tiene a quién difundir. Es correcto que no salga nada: emitir a ciegas a lo que hubiera en el
/// mapa es justo la ruta que este trabajo cierra.
#[tokio::test]
async fn a_joiner_without_a_known_host_broadcasts_nowhere() {
    let mut joiner = NetworkManager::bind(0, 7, 42, false).await.unwrap();
    joiner.peers.insert(9, fake_peer(9, 41));
    assert!(joiner.host_peer_id.is_none());

    assert!(
        joiner.broadcast_destinations().is_empty(),
        "sin host conocido no hay destino legítimo"
    );
}

// ─────────────────────────────────────────────────────────────────────────────
// TAREA 4 (2026-08-31) — HARDENING: la estrella, por construcción y no por convención
//
// Los cinco tests de arriba fijan `broadcast_destinations`, que era la ÚNICA superficie con el
// filtro de rol. Los ~30 envíos dirigidos de un joiner no lo tenían: eran seguros sólo porque
// todos escriben el literal `1`. Eso no es una garantía, es una costumbre — y una costumbre no
// sobrevive al siguiente sistema que tome el id de un peer de una lista.
// ─────────────────────────────────────────────────────────────────────────────

/// Un socket UDP vivo en loopback, para poder observar lo que de verdad sale al aire.
async fn live_socket() -> (tokio::net::UdpSocket, SocketAddr) {
    let sock = tokio::net::UdpSocket::bind("127.0.0.1:0").await.unwrap();
    let addr = sock.local_addr().unwrap();
    (sock, addr)
}

/// ¿Llegó ALGO a este socket? La ausencia de un datagrama sólo se puede medir con un plazo; 200 ms
/// son tres órdenes de magnitud más que una entrega por loopback, que es de microsegundos.
async fn received_anything(sock: &tokio::net::UdpSocket) -> bool {
    let mut buf = [0u8; 4096];
    tokio::time::timeout(Duration::from_millis(200), sock.recv_from(&mut buf))
        .await
        .is_ok()
}

/// Un fiable cualquiera con secuencia > 0, que es la condición que dispara el ACK del receptor.
fn reliable_from(sender: PeerId, addr: SocketAddr) -> IncomingPacket {
    IncomingPacket {
        addr,
        header: PacketHeader::new(protocol::PacketType::WorldSyncEnd as u16, sender, 77, 0),
        payload: PacketPayload::WorldSyncEnd {
            world_revision: 1,
            chunk_count: 0,
        },
    }
}

/// LA MATRIZ, a 2, 3 y 4 jugadores. `HOST ve A`, `HOST ve B`, `A ve HOST` y `B ve HOST` son
/// destinos legales; `A ve B` y `B ve A` tienen que ocurrir por la reemisión del host, así que
/// como DESTINO son ilegales en los dos sentidos.
///
/// A dos jugadores no distingue nada —el único peer de un joiner ES el host—, y por eso el defecto
/// que cerró b997eadb vivió tanto: nunca se había jugado a tres. Se recorre igualmente para que el
/// caso trivial quede fijado como caso, y no como ausencia de caso.
#[tokio::test]
async fn the_star_matrix_holds_at_two_three_and_four_peers() {
    for joiners in 1..=3u16 {
        let joiner_ids: Vec<PeerId> = (2..2 + joiners).collect();
        let total = joiners + 1;

        // El host alcanza a todos: reemitir es exactamente su papel.
        let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        for (i, id) in joiner_ids.iter().enumerate() {
            host.peers.insert(*id, fake_peer(*id, 41 + i as u8));
        }
        for id in &joiner_ids {
            assert!(
                host.is_gameplay_destination(*id),
                "a {total} jugadores el host tiene que alcanzar al peer {id}"
            );
        }

        // Cada joiner alcanza a UNO: el host.
        for me in &joiner_ids {
            let mut net = NetworkManager::bind(0, *me, 42, false).await.unwrap();
            net.peers.insert(1, fake_peer(1, 40));
            net.host_peer_id = Some(1);
            // El roster del host le da la dirección REAL de los otros joiners. Registrarla es
            // correcto —hay que poder pintarlos—; convertirla en destino es lo que no.
            for (i, other) in joiner_ids.iter().enumerate() {
                if other != me {
                    net.peers.insert(*other, fake_peer(*other, 41 + i as u8));
                }
            }

            assert!(
                net.is_gameplay_destination(1),
                "a {total} jugadores el joiner {me} tiene que alcanzar al host"
            );
            for other in joiner_ids.iter().filter(|o| *o != me) {
                assert!(
                    !net.is_gameplay_destination(*other),
                    "a {total} jugadores el joiner {me} NO puede alcanzar al joiner {other}: \
                     eso lo hace el relay del host (ADR-015)"
                );
            }

            let dests: Vec<PeerId> = net
                .broadcast_destinations()
                .into_iter()
                .map(|(id, _)| id)
                .collect();
            assert_eq!(dests, vec![1], "joiner {me}: un solo destino de difusión");
        }
    }
}

/// **EL TEST QUE REPRODUCE EL AGUJERO LATENTE.** Las seis superficies de envío, medidas por lo que
/// sale al aire y por lo que se encola, no por lo que dice un filtro.
///
/// `broadcast_reliable` era la peor de las seis: filtraba fantasmas y `relay_only` pero NO el rol.
/// Un fiable hacia un par inalcanzable no se pierde y ya está — se reenvía cinco veces y termina
/// expulsando al peer (ADR-062). Hoy no era alcanzable porque sus dos llamadores
/// (`broadcast_anchor` / `broadcast_stabilizer`) no tienen ni un call site, y ésa es justamente la
/// razón de cerrarlo ahora: el día que alguien los llame, nadie se va a acordar de esto.
#[tokio::test]
async fn no_send_surface_lets_a_joiner_reach_another_joiner() {
    let (host_sock, host_addr) = live_socket().await;
    let (peer_sock, peer_addr) = live_socket().await;

    let mut a = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    a.peers
        .insert(1, PeerConnection::new(1, "Host".into(), host_addr));
    a.host_peer_id = Some(1);
    a.peers
        .insert(3, PeerConnection::new(3, "JoinerB".into(), peer_addr));

    let unreliable = PacketPayload::Heartbeat;
    let reliable = PacketPayload::WorldSyncEnd {
        world_revision: 1,
        chunk_count: 0,
    };

    a.send_unreliable_to(3, &unreliable).await;
    let relayed = a.encode_relay_as(2, &unreliable);
    a.send_prepared_unreliable(3, &relayed).await;
    a.send_reliable(3, &reliable).await;
    a.send_reliable_queued(3, &reliable).await;
    a.broadcast_unreliable(&unreliable).await;
    a.broadcast_reliable(&reliable).await;

    assert!(
        !received_anything(&peer_sock).await,
        "seis superficies de envío y ni un datagrama puede llegar al otro joiner"
    );
    assert!(
        a.peers[&3].reliable_queue.is_empty(),
        "tampoco se encola un fiable hacia él: encolarlo es reenviarlo cinco veces y \
         terminar expulsándolo (ADR-062)"
    );
    assert!(
        a.peers[&3].deferred_reliable.is_empty(),
        "ni se aparca en la cola diferida, que además tiene tope fatal (VERDICT_QUEUE_CAP)"
    );

    // La otra mitad: la estrella sigue viva. Un arreglo que también callara hacia el host sería
    // peor que el defecto.
    assert!(
        received_anything(&host_sock).await,
        "el joiner tiene que seguir hablando con el host"
    );
    assert_eq!(
        a.peers[&1].reliable_queue.len(),
        1,
        "y el broadcast fiable sí encoló para el host"
    );
}

/// El contrapeso del anterior, medido en el aire: el host alcanza a los dos joiners. Si el filtro
/// se aplicara a los dos roles, los joiners dejarían de recibir nada.
#[tokio::test]
async fn the_host_still_reaches_every_joiner() {
    let (a_sock, a_addr) = live_socket().await;
    let (b_sock, b_addr) = live_socket().await;

    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.peers
        .insert(2, PeerConnection::new(2, "A".into(), a_addr));
    host.peers
        .insert(3, PeerConnection::new(3, "B".into(), b_addr));

    host.broadcast_unreliable(&PacketPayload::Heartbeat).await;

    assert!(received_anything(&a_sock).await, "el host alcanza a A");
    assert!(received_anything(&b_sock).await, "el host alcanza a B");
}

/// ENDPOINT SAFETY. Un roster puede anunciar cualquier cosa: es una AFIRMACIÓN del host, no una
/// dirección observada. Las tres que no pueden ser el endpoint de nadie remoto:
///
/// - `0.0.0.0` — significa "esta máquina" al enviar, así que adoptarla convierte cada latido
///   dirigido a ese peer en un latido a uno mismo. Se rechaza en el REGISTRO (auditoría de
///   heartbeat, 2026-08-30): no llega a existir.
/// - `127.0.0.1` y `169.254.x.x` (APIPA) — no se pueden rechazar en el registro sin romper la
///   partida en una sola máquina, que va por loopback de verdad. Se registran, se pintan, y no
///   son destino: la topología los deja fuera antes que la dirección.
#[tokio::test]
async fn a_roster_advertising_loopback_or_apipa_never_yields_a_destination() {
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let host_addr: SocketAddr = "127.0.0.1:9700".parse().unwrap();
    joiner
        .peers
        .insert(1, PeerConnection::new(1, "Host".into(), host_addr));
    joiner.host_peer_id = Some(1);

    let advertised = |id: PeerId, name: &str, addr: &str| protocol::PeerInfo {
        id,
        name: name.into(),
        addr: addr.into(),
        position: [0.0; 3],
        relay_only: false,
    };
    let roster = IncomingPacket {
        addr: host_addr,
        header: PacketHeader::new(protocol::PacketType::PeerList as u16, 1, 0, 0),
        payload: PacketPayload::PeerList {
            peers: vec![
                advertised(3, "loopback", "127.0.0.1:7778"),
                advertised(4, "apipa", "169.254.13.7:7778"),
                advertised(5, "unspecified", "0.0.0.0:7778"),
            ],
        },
    };
    joiner.handle_packet(roster).await;

    assert!(
        !joiner.peers.contains_key(&5),
        "una dirección sin especificar no se registra siquiera"
    );
    for id in [3u16, 4] {
        assert!(
            joiner.peers.contains_key(&id),
            "el peer {id} sí se registra: el joiner tiene que poder pintarlo"
        );
        assert!(
            !joiner.is_gameplay_destination(id),
            "...pero no es un destino: es otro joiner, y da igual qué dirección anuncie"
        );
    }

    let dests: Vec<PeerId> = joiner
        .broadcast_destinations()
        .into_iter()
        .map(|(id, _)| id)
        .collect();
    assert_eq!(dests, vec![1], "sigue habiendo un solo destino: el host");
}

/// STALE ENDPOINT. La `addr` de un `relay_only` es el centinela inerte POR CONTRATO (ADR-079), y
/// toda la superficie de envío se apoya en la marca. Un datagrama entrante no puede estampar ahí
/// una dirección real: eso convierte el centinela en un endpoint con pinta de legítimo.
///
/// Antes sólo lo impedía de rebote `relayed_from_other_peer` —las poses del fantasma llegan desde
/// el socket del host, que ya es otro peer conocido—, o sea que la protección dependía de que el
/// host estuviera registrado, no del contrato.
#[tokio::test]
async fn a_relay_only_peer_never_adopts_an_endpoint() {
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let mut ghost = PeerConnection::new(9, "Robapieles".into(), INERT_PEER_ADDR);
    ghost.relay_only = true;
    joiner.peers.insert(9, ghost);

    let spoofed: SocketAddr = "127.0.0.1:9711".parse().unwrap();
    let beat = IncomingPacket {
        addr: spoofed,
        header: PacketHeader::new(protocol::PacketType::Heartbeat as u16, 9, 0, 0),
        payload: PacketPayload::Heartbeat,
    };
    joiner.handle_packet(beat).await;

    assert_eq!(
        joiner.peers[&9].addr, INERT_PEER_ADDR,
        "la addr de un relay_only no se adopta jamás"
    );
    assert!(
        !joiner.is_gameplay_destination(9),
        "y sigue sin ser un destino"
    );
}

/// EL ACK ERA LA ÚLTIMA SUPERFICIE DIRECTA. No pasa por `is_gameplay_destination` —va a `pkt.addr`
/// crudo, sin mirar la tabla de peers—, así que un par con un build viejo, sin el filtro de
/// estrella, emitiendo fiables directos, se llevaba un ACK directo de vuelta. Un ACK no es tráfico
/// de gameplay, pero sí un datagrama a un endpoint que la topología dice que no existe, y en
/// Windows lo que rebota vuelve como `WSAECONNRESET` sobre el socket propio (H10).
#[tokio::test]
async fn a_joiner_acks_the_host_and_nobody_else() {
    let (host_sock, host_addr) = live_socket().await;
    let (peer_sock, peer_addr) = live_socket().await;

    let mut a = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    a.peers
        .insert(1, PeerConnection::new(1, "Host".into(), host_addr));
    a.host_peer_id = Some(1);
    a.peers
        .insert(3, PeerConnection::new(3, "JoinerB".into(), peer_addr));

    a.handle_packet(reliable_from(3, peer_addr)).await;
    assert!(
        !received_anything(&peer_sock).await,
        "un joiner no le devuelve ni un ACK a otro joiner"
    );

    a.handle_packet(reliable_from(1, host_addr)).await;
    assert!(
        received_anything(&host_sock).await,
        "al host sí: sin ACK, el host reenvía seis veces y acaba expulsándonos (ADR-039/062)"
    );
}

/// La ventana de arranque, que es por lo que la guarda no es un `== host_peer_id` a secas. El
/// `HandshakeAck` es lo único que le dice a un joiner quién es el host, y UDP no ordena nada: un
/// `WorldSyncChunk` puede adelantarlo. Callar ahí costaría una retransmisión por cada chunk
/// adelantado, y no cierra ningún agujero — antes de ese punto el único que nos escribe es el host
/// al que le hemos pedido entrar.
#[tokio::test]
async fn a_joiner_still_acks_before_it_knows_who_the_host_is() {
    let (host_sock, host_addr) = live_socket().await;

    let mut a = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    assert!(
        a.host_peer_id.is_none(),
        "precondición: handshake sin cerrar"
    );

    a.handle_packet(reliable_from(1, host_addr)).await;
    assert!(
        received_anything(&host_sock).await,
        "sin host conocido todavía, el ACK tiene que salir igual"
    );
}

/// Y el host ACKea a cualquiera: es el centro de la estrella, todos le hablan directamente.
#[tokio::test]
async fn the_host_acks_every_sender() {
    let (a_sock, a_addr) = live_socket().await;

    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.peers
        .insert(2, PeerConnection::new(2, "A".into(), a_addr));

    host.handle_packet(reliable_from(2, a_addr)).await;
    assert!(
        received_anything(&a_sock).await,
        "el host ACKea a todo el que le escribe"
    );
}

/// HEARTBEAT — NINGÚN FALSO POSITIVO EN SESIÓN ESTABLE, a 3 y a 4 jugadores.
///
/// Es la consecuencia que el fix de la estrella no dejó probada: desde que A no le manda NADA a
/// sus pares, lo ÚNICO que mantiene vivos a B y C en la tabla de A es el roster del host a 10 Hz
/// (y las poses relayadas). Si ese refresco no ocurriera, A expulsaría a sus pares a los 5 s con
/// la red intacta — y el síntoma sería idéntico al de una pérdida de paquetes.
///
/// Sin dormir 5 s: se coloca a los pares AL BORDE del umbral y se comprueba que el roster los
/// devuelve al principio.
#[tokio::test]
async fn the_hosts_roster_keeps_silent_peers_alive_at_three_and_four() {
    for joiners in 2..=3u16 {
        let mut a = NetworkManager::bind(0, 2, 42, false).await.unwrap();
        let host_addr: SocketAddr = "127.0.0.1:9720".parse().unwrap();
        a.peers
            .insert(1, PeerConnection::new(1, "Host".into(), host_addr));
        a.host_peer_id = Some(1);

        let others: Vec<PeerId> = (3..2 + joiners).collect();
        for id in &others {
            a.peers.insert(*id, fake_peer(*id, 40 + *id as u8));
        }

        let almost = Instant::now() - (peer::HEARTBEAT_TIMEOUT - Duration::from_millis(200));
        for id in &others {
            a.peers.get_mut(id).unwrap().last_heartbeat = almost;
            assert!(
                !a.peers[id].is_timed_out(),
                "precondición: el peer {id} está al borde, todavía no muerto"
            );
        }

        let roster = IncomingPacket {
            addr: host_addr,
            header: PacketHeader::new(protocol::PacketType::PeerList as u16, 1, 0, 0),
            payload: PacketPayload::PeerList {
                peers: others
                    .iter()
                    .map(|id| protocol::PeerInfo {
                        id: *id,
                        name: format!("peer{id}"),
                        addr: format!("192.168.1.{}:7778", 40 + id),
                        position: [1.0, 1.8, 2.0],
                        relay_only: false,
                    })
                    .collect(),
            },
        };
        a.handle_packet(roster).await;

        for id in &others {
            assert!(
                a.peers[id].last_heartbeat > almost,
                "a {} jugadores, el roster del host tiene que refrescar al peer {id}: es lo \
                 único que le queda desde que la estrella calló el tráfico directo",
                joiners + 1
            );
        }
        assert!(
            a.check_timeouts().is_empty(),
            "a {} jugadores no expira nadie con la sesión estable",
            joiners + 1
        );
        assert_eq!(
            a.peer_ids().len(),
            1 + others.len(),
            "y la tabla queda entera"
        );
    }
}

/// LA MITAD DIRIGIDA de `a_joiner_without_a_known_host_broadcasts_nowhere`. Aquélla fija que un
/// joiner a medio handshake no DIFUNDE a nadie; ésta, que tampoco ENCOLA un fiable a nadie.
///
/// No es simetría por gusto: la vía fiable es la que hace daño. Un no-fiable a un destino que no
/// existe se pierde y se auto-cura al siguiente envío; un fiable se encola, se reenvía cinco veces
/// contra el mismo vacío y termina expulsando al peer (ADR-062).
///
/// El estado no es alcanzable en producción —`handle_handshake_ack` inserta al host y anota
/// `host_peer_id` en la misma función, así que nunca hay lo uno sin lo otro— y por eso el test
/// existe: es la garantía de que el día que alguien registre un peer por otra vía, la vía fiable
/// no se abra sola. Es también lo que se rompió al corregir los dos fixtures de
/// `sync::chunk_broadcast_tests`, que montaban a mano ese estado imposible.
#[tokio::test]
async fn a_joiner_mid_handshake_cannot_queue_a_reliable_to_anyone() {
    let mut joiner = NetworkManager::bind(0, 7, 42, false).await.unwrap();
    joiner.peers.insert(1, fake_peer(1, 40));
    joiner.peers.insert(9, fake_peer(9, 41));
    assert!(
        joiner.host_peer_id.is_none(),
        "precondición: handshake sin cerrar"
    );

    let reliable = PacketPayload::WorldSyncEnd {
        world_revision: 1,
        chunk_count: 0,
    };
    for id in [1u16, 9] {
        joiner.send_reliable(id, &reliable).await;
        joiner.send_reliable_queued(id, &reliable).await;
        assert!(
            joiner.peers[&id].reliable_queue.is_empty(),
            "sin host conocido no se encola un fiable ni siquiera hacia el peer {id}"
        );
        assert!(
            joiner.peers[&id].deferred_reliable.is_empty(),
            "tampoco se aparca en la cola diferida hacia el peer {id}"
        );
    }

    // Y en cuanto el handshake cierra, el host —y sólo el host— vuelve a ser destino.
    joiner.host_peer_id = Some(1);
    joiner.send_reliable(1, &reliable).await;
    joiner.send_reliable(9, &reliable).await;
    assert_eq!(
        joiner.peers[&1].reliable_queue.len(),
        1,
        "cerrado el handshake, el fiable al host sí sale"
    );
    assert!(
        joiner.peers[&9].reliable_queue.is_empty(),
        "y el otro joiner sigue sin serlo: eso no lo abre ningún handshake"
    );
}

/// GHOST PEER. `phantom_ids` y `faceling_ids` son estado indexado por `PeerId`, igual que
/// `voice_echo` o `pending_struggles`, y eran las dos únicas marcas que sobrevivían a una baja.
///
/// `despawn_phantom` limpia su set porque es la retirada ORDENADA. Las cuatro rutas de baja NO
/// ordenada —timeout de latido, paquete `Disconnect`, retransmisión agotada, desborde de
/// veredictos— sacaban al peer del mapa y dejaban su id dentro del set para siempre: `is_phantom`
/// seguía diciendo que sí sobre alguien que ya no existe, y el `despawn_*` posterior devolvía
/// `false` como si nunca hubiera estado.
///
/// En el bucle normal no es alcanzable —`game_loop` refresca el latido de los inyectados antes del
/// barrido, justo para eso—, pero "inalcanzable mientras nada se atasque" no es una garantía, y
/// ésta es la forma exacta que tiene R3 de ser real.
#[tokio::test]
async fn an_unclean_removal_leaves_no_injected_mark_behind() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let ghost = host.spawn_phantom("Robapieles", [0.0, 1.8, 0.0], None);
    assert!(host.is_phantom(ghost), "precondición: está marcado");
    assert!(
        host.peers.contains_key(&ghost),
        "precondición: está en la tabla"
    );

    // Una de las cuatro bajas no ordenadas. Las cuatro pasan por `purge_peer_state`, que es donde
    // vive el arreglo, así que ejercitar una las cubre.
    let bye = IncomingPacket {
        addr: INERT_PEER_ADDR,
        header: PacketHeader::new(protocol::PacketType::Disconnect as u16, ghost, 0, 0),
        payload: PacketPayload::Disconnect {
            reason: "baja no ordenada".into(),
        },
    };
    host.handle_packet(bye).await;

    assert!(
        !host.peers.contains_key(&ghost),
        "precondición del arreglo: la baja sí sacaba al peer del mapa"
    );
    assert!(
        !host.is_phantom(ghost),
        "y ahora tampoco deja la marca: un id que ya no existe no puede seguir siendo fantasma"
    );
    assert_eq!(
        host.real_peer_count(),
        0,
        "la contabilidad no se desajusta por el camino"
    );
}

// ═══ TAREA 2 (2026-08-31): NINGÚN DATAGRAMA UDP SALIENTE PASA DE `SAFE_DATAGRAM_BYTES` ═══
//
// La invariante se comprueba SIEMPRE contra `refused_datagram_count()` y `max_datagram_seen()`, no
// contra el log: un aviso que nadie lee no es una invariante, y era exactamente el estado anterior
// (31.004 datagramas sobredimensionados por sesión, máximo 1881 B, con su warn puntual).

/// Como `pump_pair`, pero devuelve los eventos del JOINER en vez de los del host: lo que estos
/// tests comprueban es qué le llega al que recibe, no qué se le retransmite al que emite.
async fn pump_to_joiner(
    host: &mut NetworkManager,
    joiner: &mut NetworkManager,
    rounds: usize,
) -> Vec<NetworkEvent> {
    let mut events = Vec::new();
    for _ in 0..rounds {
        tokio::time::sleep(Duration::from_millis(20)).await;
        events.extend(joiner.process_incoming().await);
        host.process_incoming().await;
        host.process_retransmits().await;
        host.pump_deferred_reliable().await;
    }
    events
}

/// El techo, en el punto donde no se puede esquivar. Un datagrama por encima del presupuesto NO
/// sale por el socket — se rechaza ANTES del `send_to`, no se registra y ya está.
#[tokio::test]
async fn an_oversized_datagram_is_refused_before_the_socket() {
    let net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let dest = loopback_addr(&net);

    let ok = net
        .send_datagram(
            &vec![0u8; protocol::SAFE_DATAGRAM_BYTES],
            dest,
            "unreliable_to",
        )
        .await;
    assert!(ok, "justo en el techo todavía sale");
    assert_eq!(net.refused_datagram_count(), 0);

    let sent = net
        .send_datagram(
            &vec![0u8; protocol::SAFE_DATAGRAM_BYTES + 1],
            dest,
            "unreliable_to",
        )
        .await;
    assert!(!sent, "un byte por encima del techo NO sale");
    assert_eq!(
        net.refused_datagram_count(),
        1,
        "y queda contado, que es lo que un test puede afirmar"
    );
}

/// El motivo de que `send_datagram` devuelva algo. Un fiable rechazado por el techo no puede
/// quedarse encolado: se reenviaría cinco veces con el MISMO tamaño, se rechazaría las cinco, y al
/// agotar `MAX_RETRIES` ADR-062 expulsaría al peer — un datagrama demasiado grande acabaría
/// echando a un jugador de la partida.
#[tokio::test]
async fn a_refused_reliable_is_never_queued_for_retransmission() {
    let (mut host, _joiner) = connected_pair().await;
    let peer_id = *host.peers.keys().next().expect("hay un peer");

    // Un payload que no cabe de ninguna manera: 4000 caracteres de motivo.
    let huge = PacketPayload::Disconnect {
        reason: "x".repeat(4000),
    };
    host.send_reliable(peer_id, &huge).await;

    assert_eq!(host.refused_datagram_count(), 1, "el techo lo rechazó");
    assert_eq!(
        host.peers[&peer_id].reliable_queue.len(),
        0,
        "y NO se encoló: encolarlo sería expulsar al peer en ~3 s por retransmisiones que \
         tampoco pueden salir"
    );
}

/// **EL AGUJERO DE LA AUDITORÍA DE INTEGRACIÓN (2026-08-31).** El mismo daño que el test de
/// arriba, por la puerta de al lado.
///
/// `send_reliable_queued` sólo pasa por el techo en la rama que ENVÍA. Cuando la ventana está
/// llena —o ya hay diferidos— aparca el paquete sin medirlo, y quien lo saca es
/// `pump_deferred_reliable`, que encolaba para retransmisión **sin mirar si había salido**. Un
/// veredicto sobredimensionado aparcado en una ráfaga acababa exactamente donde ADR-113 dice que
/// no puede acabar: cinco rechazos idénticos y el peer expulsado por `MAX_RETRIES`.
///
/// Se siembra la cola diferida directamente porque es el estado que hay que reproducir: llegar a
/// él por la API pública exige llenar la ventana Y vaciarla con ACKs en el mismo test, y eso
/// probaría el drenaje, no el contrato del drenaje.
#[tokio::test]
async fn a_deferred_reliable_refused_by_the_ceiling_is_never_queued_either() {
    let (mut host, _joiner) = connected_pair().await;
    let peer_id = *host.peers.keys().next().expect("hay un peer");

    let huge = PacketPayload::Disconnect {
        reason: "x".repeat(4000),
    };
    let header = PacketHeader::new(huge.type_code(), host.local_id, 7, host.timestamp());
    let data = encode_packet(&header, &huge);
    assert!(data.len() > protocol::SAFE_DATAGRAM_BYTES);
    host.peers
        .get_mut(&peer_id)
        .expect("el peer sigue ahí")
        .defer_reliable(7, data);

    host.pump_deferred_reliable().await;

    assert_eq!(
        host.refused_datagram_count(),
        1,
        "el techo lo rechazó también saliendo de la cola diferida"
    );
    assert_eq!(
        host.peers[&peer_id].reliable_queue.len(),
        0,
        "y NO se encoló: mismo criterio que send_reliable, o el techo tiene una puerta trasera"
    );
    assert!(
        host.peers[&peer_id].deferred_reliable.is_empty(),
        "y no vuelve a la cola diferida: reintentarlo eternamente es el otro modo de fallo"
    );
}

// ─── ChunkState: instantánea COMPLETA y reemplazable, sin versionado propio ───

/// El emisor que producía los ~31.000 datagramas sobredimensionados por sesión. Extremo a extremo
/// sobre sockets reales: el chunk denso llega ENTERO y ni un datagrama pasó del techo.
#[tokio::test]
async fn a_dense_chunk_broadcast_puts_nothing_oversized_on_the_wire() {
    let (mut host, mut joiner) = connected_pair().await;
    let origin = crate::utils::Vec3::new(0.0, 1.8, 0.0);
    let mut owned = dense_chunk_world(13); // 13 entidades = lo peor visto en la sesión física
    owned.update_ownership(origin, host.local_id);

    sync::broadcast_chunk_states(&mut host, &owned, origin).await;
    let events = pump_to_joiner(&mut host, &mut joiner, 40).await;

    assert_eq!(
        host.refused_datagram_count(),
        0,
        "ni un datagrama del broadcast puede pasar de {} B",
        protocol::SAFE_DATAGRAM_BYTES
    );
    assert!(
        host.max_datagram_seen() <= protocol::SAFE_DATAGRAM_BYTES,
        "el mayor datagrama emitido fue de {} B",
        host.max_datagram_seen()
    );
    let applied: Vec<_> = events
        .iter()
        .filter_map(|e| match e {
            NetworkEvent::ChunkStateReceived { data, .. } => Some(data),
            _ => None,
        })
        .collect();
    assert!(
        !applied.is_empty(),
        "el chunk tiene que llegar pese a ir paginado: {events:?}"
    );
    for data in applied {
        let want = owned
            .chunks
            .values()
            .find(|c| c.pos == (data.pos[0], data.pos[1]) && c.layer == data.layer)
            .expect("el chunk aplicado tiene que existir en el origen");
        assert_eq!(
            data.entities.len(),
            want.entities.len(),
            "un chunk reensamblado no puede perder entidades"
        );
        assert_eq!(data.items.len(), want.items.len());
        assert_eq!(
            data.layout.cells, want.layout.cells,
            "la cabecera sale de la página 0 y tiene que ser la real, no la vacía de continuación"
        );
    }
}

/// Todo-o-nada, desordenado y con duplicados: la razón entera de ensamblar en vez de aplicar
/// página a página. `apply_chunk_sync` hace `entities.clear()` y reconstruye, así que aplicar una
/// página suelta no deja el chunk a medias — lo deja MAL.
#[test]
fn chunk_state_pages_assemble_out_of_order_and_survive_duplicates() {
    let world = dense_chunk_world(20);
    let chunk = world.chunks.values().next().expect("hay chunks");
    let full = sync::chunk_to_sync_data(chunk);
    let mut pages = sync::chunk_state_pages(full.clone(), 7);
    assert!(
        pages.len() > 1,
        "preparación: este chunk tiene que partirse"
    );
    for page in &pages {
        let bytes = sync::ChunkCarrier::State.encoded_len(page);
        assert!(
            bytes <= protocol::SAFE_DATAGRAM_BYTES,
            "una página de ChunkState no puede fragmentar: {bytes} B"
        );
        assert_eq!(page.generation, 7, "todas las páginas llevan su ronda");
    }

    pages.reverse();
    let dup = pages[0].clone();
    let mut asm = sync::ChunkPageAssembler::default();
    let mut merged = None;
    for page in std::iter::once(dup).chain(pages.iter().cloned()) {
        if let Some(done) = asm.offer(7, page) {
            merged = Some(done);
        }
    }
    let merged = merged.expect("desordenado y con duplicado tiene que ensamblar igual");
    let ids: Vec<u32> = merged.entities.iter().map(|e| e.id).collect();
    let want: Vec<u32> = full.entities.iter().map(|e| e.id).collect();
    assert_eq!(
        ids, want,
        "el orden lo fija el índice de página, no la llegada"
    );
    assert_eq!(merged.items.len(), full.items.len());
    assert_eq!(asm.pending_len(), 0, "no puede quedar nada aparcado");
}

/// Pérdida de una página: no se entrega NADA. Aplicar lo que hay dejaría el chunk con media lista
/// de entidades y sin forma de saberlo.
#[test]
fn an_incomplete_chunk_state_is_never_applied() {
    let world = dense_chunk_world(20);
    let chunk = world.chunks.values().next().expect("hay chunks");
    let pages = sync::chunk_state_pages(sync::chunk_to_sync_data(chunk), 3);
    assert!(pages.len() > 1);

    let mut asm = sync::ChunkPageAssembler::default();
    for page in pages.iter().take(pages.len() - 1) {
        assert!(
            asm.offer(3, page.clone()).is_none(),
            "sin la última página no se entrega nada"
        );
    }
    assert_eq!(asm.pending_len(), 1, "queda exactamente un chunk aparcado");
}

/// LA RAZÓN DE `generation`, y el fallo que sin ella no se puede evitar.
///
/// La ronda N entrega su página 0 y pierde el resto; la ronda N+1 pierde su página 0 y entrega el
/// resto. Sin discriminante de ronda los índices no se pisan y el ensamblador cose un chunk
/// QUIMERA: una lista de entidades que nunca existió, que además se aplica como buena porque el
/// reemplazo es verbatim. Con `generation` cada ronda es su propia clave y lo único que puede
/// pasar es que ninguna complete, que es la pérdida honesta.
#[test]
fn pages_of_two_different_rounds_never_merge_into_a_chimera() {
    let world = dense_chunk_world(20);
    let chunk = world.chunks.values().next().expect("hay chunks");
    let data = sync::chunk_to_sync_data(chunk);

    let round_a = sync::chunk_state_pages(data.clone(), 100);
    let round_b = sync::chunk_state_pages(data, 200);
    assert!(round_a.len() > 1, "preparación: la ronda A se parte");

    let mut asm = sync::ChunkPageAssembler::default();
    // Página 0 de A, y luego TODAS las de B menos la 0.
    assert!(asm.offer(100, round_a[0].clone()).is_none());
    let mut merged = None;
    for page in round_b.iter().skip(1) {
        if let Some(done) = asm.offer(200, page.clone()) {
            merged = Some(done);
        }
    }
    assert!(
        merged.is_none(),
        "una página de la ronda 100 JAMÁS puede completar la ronda 200"
    );
    assert_eq!(
        asm.pending_len(),
        1,
        "y el parcial de la ronda vieja se desaloja al llegar la nueva del mismo chunk"
    );
}

/// El handoff de propiedad (0x30) es FIABLE y va confirmado. Paginarlo no puede romper su ACK, que
/// es lo que le dice al que cede la autoridad que el otro la tiene.
#[tokio::test]
async fn a_paged_chunk_transfer_arrives_whole_and_is_still_acked() {
    let (mut host, mut joiner) = connected_pair().await;
    let peer_id = *host.peers.keys().next().expect("hay un peer");
    let world = dense_chunk_world(20);
    let chunk = world.chunks.values().next().expect("hay chunks").clone();
    let want_entities = chunk.entities.len();

    sync::send_chunk_transfer(&mut host, peer_id, &chunk).await;
    let events = pump_to_joiner(&mut host, &mut joiner, 40).await;

    assert_eq!(
        host.refused_datagram_count(),
        0,
        "un handoff paginado no puede producir un datagrama fuera de presupuesto"
    );
    assert_eq!(host.oversized_reliable_count(), 0);
    let received = events
        .iter()
        .find_map(|e| match e {
            NetworkEvent::ChunkTransferReceived { data, .. } => Some(data),
            _ => None,
        })
        .unwrap_or_else(|| panic!("el handoff tiene que llegar entero: {events:?}"));
    assert_eq!(
        received.entities.len(),
        want_entities,
        "reensamblado sin pérdida"
    );
}

// ─── PeerList: aditivo, así que se trocea SIN ensamblador ───

/// Un roster grande sale en varios datagramas y cada uno cabe. Medido: 16 peers ya son 1617 B.
#[test]
fn a_crowded_peer_roster_travels_in_several_datagrams_all_within_budget() {
    let peers: Vec<protocol::PeerInfo> = (0..50)
        .map(|id| protocol::PeerInfo {
            id,
            name: format!("Jugador_Con_Nombre_Largo_{id:02}"),
            addr: "192.168.100.200:65535".into(),
            position: [1.0, 2.0, 3.0],
            relay_only: false,
        })
        .collect();

    let datagrams = sync::peer_list_datagrams(peers.clone());
    assert!(datagrams.len() > 1, "50 peers no caben en un datagrama");
    let mut seen = Vec::new();
    for payload in &datagrams {
        let header = protocol::PacketHeader::new(payload.type_code(), 1, 0, 0);
        let bytes = protocol::encode_packet(&header, payload).len();
        assert!(
            bytes <= protocol::SAFE_DATAGRAM_BYTES,
            "un trozo de PeerList midió {bytes} B"
        );
        let PacketPayload::PeerList { peers } = payload else {
            panic!("todos los trozos son PeerList");
        };
        assert!(!peers.is_empty(), "ningún trozo puede ir vacío");
        seen.extend(peers.iter().map(|p| p.id));
    }
    let want: Vec<u16> = peers.iter().map(|p| p.id).collect();
    assert_eq!(seen, want, "ni se pierde ni se reordena un peer");
}

/// Y la razón de que NO lleve ensamblador: cada trozo es un mensaje completo. Se aplica suelto, en
/// cualquier orden y repetido, y el resultado es el mismo — que es lo que mantiene vivos los
/// latidos de los pares aunque un trozo se pierda (I17).
#[tokio::test]
async fn every_peer_list_datagram_is_applicable_on_its_own() {
    let (mut host, mut joiner) = connected_pair().await;
    let peers: Vec<protocol::PeerInfo> = (10..40)
        .map(|id| protocol::PeerInfo {
            id,
            name: format!("Jugador_Con_Nombre_Largo_{id:02}"),
            addr: format!("192.168.4.{}:7778", id as u8),
            position: [1.0, 2.0, 3.0],
            relay_only: false,
        })
        .collect();
    let datagrams = sync::peer_list_datagrams(peers.clone());
    assert!(datagrams.len() > 1);

    // Se emite SOLO el último trozo. Con reensamblado todo-o-nada no se aplicaría nada.
    let tail = datagrams.last().expect("hay trozos").clone();
    host.broadcast_unreliable(&tail).await;
    pump_to_joiner(&mut host, &mut joiner, 20).await;

    let PacketPayload::PeerList { peers: tail_peers } = tail else {
        panic!("es un PeerList");
    };
    for info in &tail_peers {
        assert!(
            joiner.peers.contains_key(&info.id),
            "el peer {} tiene que registrarse con un solo trozo",
            info.id
        );
    }
}

// ─── HandshakeAck: se RECORTA, no se pagina ───

/// Con la sesión llena el ack se pasaba del techo (1791 B con 16 peers) y se perdía entero: es la
/// única respuesta al handshake y no tiene reintento propio, así que el joiner agotaba
/// `CONNECT_TIMEOUT` en una sesión perfectamente viva. Se recorta la lista de peers, que es una
/// pista que el receptor ni lee, y NUNCA la cabecera, que es de lo que depende entrar.
#[tokio::test]
async fn a_handshake_ack_for_a_crowded_session_fits_and_keeps_its_header() {
    let mut host = NetworkManager::bind(0, 1, 4242, true).await.unwrap();
    for id in 2..34u16 {
        host.peers.insert(id, fake_peer(id, id as u8));
    }
    let mut joiner = NetworkManager::bind(0, 900, 0, false).await.unwrap();
    joiner.initiate_connection(loopback_addr(&host)).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    let events = joiner.process_incoming().await;

    assert_eq!(
        host.refused_datagram_count(),
        0,
        "el ack de una sesión llena tiene que caber"
    );
    assert!(host.max_datagram_seen() <= protocol::SAFE_DATAGRAM_BYTES);
    assert!(
        joiner.host_peer_id.is_some(),
        "y el joiner tiene que seguir sabiendo quién es el host: {events:?}"
    );
    assert_eq!(
        joiner.world_seed, 4242,
        "la cabecera del ack no se recorta jamás"
    );
}

// ─── Pintadas: paginadas por trazos, en las dos direcciones ───

/// Reordenación, duplicado y pérdida sobre el peor caso declarado.
#[test]
fn spray_pages_survive_reorder_and_duplicates_and_never_apply_half() {
    use crate::world::spray::{SprayStroke, MAX_POINTS_PER_SPRAY, MAX_STROKES_PER_SPRAY};

    let per_stroke = MAX_POINTS_PER_SPRAY / MAX_STROKES_PER_SPRAY;
    let mut worst = test_spray(4242, 1, 1);
    worst.strokes = (0..MAX_STROKES_PER_SPRAY)
        .map(|_| SprayStroke {
            color: 0,
            width: 8,
            points: vec![7u8; per_stroke * 2],
        })
        .collect();

    let mut pages = sync::spray_placed_pages(&worst);
    assert!(pages.len() > 1, "el peor caso no cabe en un datagrama");
    for payload in &pages {
        let header = protocol::PacketHeader::new(payload.type_code(), 1, 1, 0);
        let bytes = protocol::encode_packet(&header, payload).len();
        assert!(
            bytes <= protocol::SAFE_DATAGRAM_BYTES,
            "una página de SprayPlaced midió {bytes} B"
        );
    }

    let unwrap = |p: &PacketPayload| match p {
        PacketPayload::SprayPlaced {
            spray,
            page,
            page_count,
        } => (spray.id as u64, *page, *page_count, spray.strokes.clone()),
        _ => panic!("son SprayPlaced"),
    };

    // Sin la última: no se entrega nada.
    let mut asm = sync::SprayPageAssembler::default();
    for payload in pages.iter().take(pages.len() - 1) {
        let (k, p, n, st) = unwrap(payload);
        assert!(
            asm.offer(k, p, n, st).is_none(),
            "media pintada no se aplica"
        );
    }
    assert_eq!(asm.pending_len(), 1);

    // Y completas, al revés y con un duplicado, dan exactamente el original.
    pages.reverse();
    let dup = pages[0].clone();
    let mut asm = sync::SprayPageAssembler::default();
    let mut merged = None;
    for payload in std::iter::once(&dup).chain(pages.iter()) {
        let (k, p, n, st) = unwrap(payload);
        if let Some(done) = asm.offer(k, p, n, st) {
            merged = Some(done);
        }
    }
    let merged = merged.expect("con todas tiene que ensamblar");
    assert_eq!(merged.len(), MAX_STROKES_PER_SPRAY);
    assert_eq!(
        merged.iter().map(|s| s.points.len()).sum::<usize>(),
        worst.strokes.iter().map(|s| s.points.len()).sum::<usize>(),
        "el orden lo fija el índice de página, no la llegada"
    );
    assert_eq!(asm.pending_len(), 0);
}

/// La petición del cliente (0x51) lleva los MISMOS trazos y por tanto el mismo peor caso.
#[test]
fn a_spray_place_request_is_paged_by_the_same_rule() {
    use crate::world::spray::{SprayStroke, MAX_POINTS_PER_SPRAY, MAX_STROKES_PER_SPRAY};

    let per_stroke = MAX_POINTS_PER_SPRAY / MAX_STROKES_PER_SPRAY;
    let strokes: Vec<SprayStroke> = (0..MAX_STROKES_PER_SPRAY)
        .map(|_| SprayStroke {
            color: 1,
            width: 4,
            points: vec![3u8; per_stroke * 2],
        })
        .collect();
    let pages = sync::spray_place_request_pages(77, 0, [1.0, 2.0, 3.0], 90.0, [2.0, 2.0], strokes);
    assert!(pages.len() > 1);
    let mut total = 0usize;
    for payload in &pages {
        let header = protocol::PacketHeader::new(payload.type_code(), 1, 1, 0);
        assert!(protocol::encode_packet(&header, payload).len() <= protocol::SAFE_DATAGRAM_BYTES);
        if let PacketPayload::SprayPlaceRequest { strokes, .. } = payload {
            total += strokes.len();
        }
    }
    assert_eq!(total, MAX_STROKES_PER_SPRAY, "no se pierde un trazo");
}

/// El trazo EN VIVO (0x54) es efímero y sí se aplica suelto — por eso se trocea sin ensamblador,
/// apoyándose en `first_index`, que existe justo para esto.
#[test]
fn a_long_spray_draft_is_split_on_point_boundaries_with_the_right_first_index() {
    // 900 puntos = 3600 B de blob, muy por encima del techo.
    let points: Vec<u8> = (0..3600).map(|i| (i % 251) as u8).collect();
    let pages = sync::spray_draft_datagrams(5, 0, [0.0, 0.0, 0.0], 0.0, 2, 0.1, 40, points.clone());
    assert!(pages.len() > 1);

    let mut rebuilt: Vec<u8> = Vec::new();
    let mut expected_index = 40u16;
    for payload in &pages {
        let header = protocol::PacketHeader::new(payload.type_code(), 1, 0, 0);
        let bytes = protocol::encode_packet(&header, payload).len();
        assert!(bytes <= protocol::SAFE_DATAGRAM_BYTES, "trozo de {bytes} B");
        let PacketPayload::SprayDraft {
            first_index,
            points_mm,
            ..
        } = payload
        else {
            panic!("son SprayDraft");
        };
        assert_eq!(
            points_mm.len() % 4,
            0,
            "el corte cae en frontera de punto o las coordenadas se inventan"
        );
        assert_eq!(
            *first_index, expected_index,
            "el índice tiene que encadenar"
        );
        expected_index += (points_mm.len() / 4) as u16;
        rebuilt.extend_from_slice(points_mm);
    }
    assert_eq!(rebuilt, points, "no se pierde ni se reordena un punto");
}

// ─── Voz: el único campo del wire cuyo tamaño lo decide el cliente ───

#[tokio::test]
async fn an_oversized_voice_frame_can_never_reach_the_socket() {
    let net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let dest = loopback_addr(&net);

    // El tope del productor deja el datagrama por debajo del techo con holgura.
    let at_cap = PacketPayload::VoiceFrame {
        seq: 1,
        data: vec![0u8; protocol::MAX_VOICE_FRAME_BYTES],
    };
    let header = protocol::PacketHeader::new(at_cap.type_code(), 1, 0, 0);
    let bytes = protocol::encode_packet(&header, &at_cap).len();
    assert!(
        bytes <= protocol::SAFE_DATAGRAM_BYTES,
        "un frame en el tope del productor midió {bytes} B"
    );

    // Y si un camino se saltara el tope, el techo lo para igual.
    let over = PacketPayload::VoiceFrame {
        seq: 2,
        data: vec![0u8; protocol::SAFE_DATAGRAM_BYTES],
    };
    let data = protocol::encode_packet(&header, &over);
    assert!(!net.send_datagram(&data, dest, "unreliable_to").await);
    assert_eq!(net.refused_datagram_count(), 1);
}

// ─── Rosters: el cadáver es el único elemento que se pasa él solo ───

/// Un inventario STP lleno mide 1207 B en un `CorpseList` de un solo cadáver (medido), contra un
/// techo de 1200. Se parte en varias ENTRADAS del mismo roster y el receptor las une por id: sin
/// eso, el `collect` de `CorpseListReceived` se quedaba con la última y borraba botín en silencio.
#[test]
fn a_full_inventory_corpse_survives_the_roster_without_losing_loot() {
    let corpse = crate::world::corpse::CorpseData {
        id: 9,
        owner_id: 3,
        owner_name: "Jugador_Con_Nombre_Largo".into(),
        position: crate::utils::Vec3::new(1.0, 2.0, 3.0),
        equipment: [1, 2, 3, 4],
        held_item: 9,
        items: (0..crate::world::corpse::MAX_CORPSE_STACKS)
            .map(|i| crate::world::corpse::CorpseStack {
                item_id: 1000 + i as i32,
                quantity: 5,
                props: Vec::new(),
            })
            .collect(),
        is_chest: false,
    };
    let entries = sync::split_oversized_corpses(vec![corpse.clone()]);
    assert!(
        entries.len() > 1,
        "un cadáver lleno no cabe en un datagrama"
    );

    for page in super::roster::paginate(&entries, super::roster::ROSTER_PAGE_BUDGET_BYTES) {
        let payload = PacketPayload::CorpseList {
            corpses: page,
            generation: 1,
            page: 0,
            page_count: 1,
        };
        let header = protocol::PacketHeader::new(payload.type_code(), 1, 0, 0);
        let bytes = protocol::encode_packet(&header, &payload).len();
        assert!(
            bytes <= protocol::SAFE_DATAGRAM_BYTES,
            "una página de CorpseList midió {bytes} B"
        );
    }

    // La unión por id que hace `CorpseListReceived`.
    let mut merged: std::collections::HashMap<u32, crate::world::corpse::CorpseData> =
        std::collections::HashMap::new();
    for entry in entries {
        match merged.entry(entry.id) {
            std::collections::hash_map::Entry::Occupied(mut slot) => {
                slot.get_mut().items.extend(entry.items);
            }
            std::collections::hash_map::Entry::Vacant(slot) => {
                slot.insert(entry);
            }
        }
    }
    assert_eq!(merged.len(), 1, "sigue siendo UN cadáver");
    assert_eq!(
        merged[&9].items.len(),
        corpse.items.len(),
        "no se pierde una sola pila de botín"
    );
}

/// Barrido: una ronda de TODOS los emisores periódicos del host, con mundo denso, roster grande y
/// cadáveres llenos, sin un solo datagrama fuera de presupuesto. Es la afirmación que da sentido a
/// la tarea; los tests de arriba dicen por qué cada pieza la cumple.
#[tokio::test]
async fn a_full_broadcast_round_puts_nothing_oversized_on_the_wire() {
    let (mut host, mut joiner) = connected_pair().await;
    for id in 20..40u16 {
        host.peers.insert(id, fake_peer(id, id as u8));
    }
    let origin = crate::utils::Vec3::new(0.0, 1.8, 0.0);
    let mut world = dense_chunk_world(13);
    world.update_ownership(origin, host.local_id);
    for n in 0..4u32 {
        world.corpses.insert(
            n,
            crate::world::corpse::CorpseData {
                id: n,
                owner_id: 3,
                owner_name: "Jugador_Con_Nombre_Largo".into(),
                position: crate::utils::Vec3::new(1.0, 2.0, 3.0),
                equipment: [1, 2, 3, 4],
                held_item: 9,
                items: (0..crate::world::corpse::MAX_CORPSE_STACKS)
                    .map(|i| crate::world::corpse::CorpseStack {
                        item_id: 1000 + i as i32,
                        quantity: 5,
                        props: Vec::new(),
                    })
                    .collect(),
                is_chest: false,
            },
        );
    }
    let player = crate::player::Player::new(1, "Host");

    sync::broadcast_player_update(&host, &player).await;
    sync::broadcast_peer_roster(&mut host, &player).await;
    sync::broadcast_chunk_states(&mut host, &world, origin).await;
    sync::broadcast_corpses(&mut host, &world).await;
    sync::broadcast_stp_items(&mut host).await;
    sync::broadcast_stp_buildings(&mut host).await;
    sync::broadcast_stp_carryables(&mut host).await;
    sync::broadcast_stp_harvestables(&mut host).await;
    sync::broadcast_level4_state(&mut host).await;
    sync::send_world_sync(&mut host, joiner.local_id, &world, &player).await;
    pump_to_joiner(&mut host, &mut joiner, 60).await;

    assert_eq!(
        host.refused_datagram_count(),
        0,
        "OBJETIVO DE LA TAREA: cero datagramas UDP sobredimensionados. El mayor emitido midió {} B",
        host.max_datagram_seen()
    );
    assert_eq!(host.oversized_reliable_count(), 0);
    assert!(
        host.max_datagram_seen() <= protocol::SAFE_DATAGRAM_BYTES,
        "máximo emitido {} B > {} B",
        host.max_datagram_seen(),
        protocol::SAFE_DATAGRAM_BYTES
    );
}

// ─── ADR-117: los dos hooks del relay ──────────────────────────────────────────────────────
//
// Aquí se prueba lo que `transport` no puede: que el datagrama envuelto SALE de verdad por el
// socket hacia el relay, y que uno que ENTRA envuelto acaba tratado como un peer normal. El papel
// del relay lo hace un socket cualquiera que se limita a mirar y a hablar — no hace falta el relay
// de verdad para probar los hooks, y meterlo aquí mezclaría dos cosas que fallan por motivos
// distintos.

use backrooms_relay::protocol::{
    MessageType as RelayMessageType, RelayFrame, RelayMessage, ENVELOPE_BYTES,
};
use tokio::net::UdpSocket as TestUdpSocket;

const TEST_RELAY_SESSION: u64 = 0x00C0_FFEE;

/// Un socket que hace de relay: recibe lo que el backend le manda y le contesta.
async fn fake_relay() -> (TestUdpSocket, SocketAddr) {
    let socket = TestUdpSocket::bind("127.0.0.1:0").await.unwrap();
    let addr = socket.local_addr().unwrap();
    (socket, addr)
}

fn test_link(relay_addr: SocketAddr, my_peer: u16) -> transport::RelayLink {
    transport::RelayLink {
        relay_addr,
        session_id: TEST_RELAY_SESSION,
        my_peer,
    }
}

#[tokio::test]
async fn un_datagrama_a_un_peer_relayado_sale_envuelto_hacia_el_relay() {
    let (relay, relay_addr) = fake_relay().await;

    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.attach_relay(test_link(
        relay_addr,
        backrooms_relay::session::HOST_PEER_ID,
    ));

    // Un joiner que sólo se alcanza por relay: su `addr` es sintética.
    let joiner_addr = transport::synthetic_addr(TEST_RELAY_SESSION, 2);
    host.peers.insert(
        2,
        peer::PeerConnection::new(2, "Joiner".into(), joiner_addr),
    );

    host.send_unreliable_to(2, &PacketPayload::Heartbeat).await;

    let mut buf = [0u8; 2048];
    let (n, from) = tokio::time::timeout(Duration::from_secs(2), relay.recv_from(&mut buf))
        .await
        .expect("el datagrama tenía que llegar al relay")
        .unwrap();
    assert_eq!(from, loopback_addr(&host), "sale por el socket del backend");

    let frame = RelayFrame::decode(&buf[..n]).expect("tiene que ser un sobre de relay legible");
    assert_eq!(frame.session_id, TEST_RELAY_SESSION);
    assert_eq!(frame.dst, 2, "va dirigido al peer 2 del relay");
    assert_eq!(frame.message.message_type(), RelayMessageType::Data);

    // Y dentro va el paquete de juego intacto: el relay no lo mira, pero nosotros sí, para
    // comprobar que nadie lo ha tocado por el camino.
    let RelayMessage::Data { payload } = frame.message else {
        panic!("tenía que ser Data");
    };
    let (header, decoded) = protocol::decode_packet(&payload).expect("el gameplay sigue entero");
    assert_eq!(header.sender_id, 1);
    assert!(matches!(decoded, PacketPayload::Heartbeat));
    assert_eq!(
        payload.len() + ENVELOPE_BYTES,
        n,
        "16 B de sobre, ni uno más"
    );
}

#[tokio::test]
async fn una_direccion_sintetica_jamas_sale_cruda_por_el_socket() {
    // Si una sintética llegara a `send_to`, el sistema intentaría enrutar una IPv6 desde un socket
    // atado a 0.0.0.0 y fallaría con un error que no señala a nada. Sin enlace de relay el envío
    // se RECHAZA, que es ruidoso y diagnosticable.
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    assert!(host.relay_link().is_none());

    host.peers.insert(
        2,
        peer::PeerConnection::new(
            2,
            "Fantasma".into(),
            transport::synthetic_addr(TEST_RELAY_SESSION, 2),
        ),
    );

    // No revienta ni cuelga: se registra y se sigue sirviendo.
    host.send_unreliable_to(2, &PacketPayload::Heartbeat).await;
}

#[tokio::test]
async fn un_handshake_que_llega_por_el_relay_registra_al_peer_con_su_direccion_sintetica() {
    // EL CAMINO COMPLETO DE ENTRADA. El host no sabe que existe un relay más allá de su enlace:
    // `handle_handshake` registra al peer, le asigna id y le contesta, exactamente igual que con
    // un joiner directo.
    let (relay, relay_addr) = fake_relay().await;

    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    host.attach_relay(test_link(
        relay_addr,
        backrooms_relay::session::HOST_PEER_ID,
    ));

    let handshake = protocol::encode_packet(
        &protocol::PacketHeader::new(
            protocol::PacketType::Handshake as u16,
            77, // el `sender_id` que el joiner se pone a sí mismo
            0,
            0,
        ),
        &PacketPayload::Handshake {
            player_name: "Alejandro".into(),
            version: crate::ipc::server::WIRE_SCHEMA_VERSION.to_string(),
            room_manifest_digest: String::new(),
            platform_id: 0,
            invited_by: 0,
        },
    );
    let envuelto = RelayFrame::to_peer(
        TEST_RELAY_SESSION,
        2,
        backrooms_relay::session::HOST_PEER_ID,
        RelayMessage::Data { payload: handshake },
    )
    .encode()
    .unwrap();
    relay.send_to(&envuelto, host_addr).await.unwrap();

    tokio::time::sleep(Duration::from_millis(200)).await;
    let events = host.process_incoming().await;

    assert!(
        events
            .iter()
            .any(|e| matches!(e, NetworkEvent::PeerConnected { .. })),
        "el host tiene que admitirlo como a cualquier otro joiner"
    );

    let registrado = host
        .peers
        .values()
        .find(|p| p.name == "Alejandro")
        .expect("el peer tiene que estar registrado");
    assert_eq!(
        registrado.addr,
        transport::synthetic_addr(TEST_RELAY_SESSION, 2),
        "y con la dirección SINTÉTICA, que es por donde se le contesta"
    );

    // La prueba de que el circuito se cierra: el HandshakeAck vuelve al relay, envuelto y dirigido
    // al peer 2.
    let mut buf = [0u8; 2048];
    let (n, _) = tokio::time::timeout(Duration::from_secs(2), relay.recv_from(&mut buf))
        .await
        .expect("el ack tenía que salir hacia el relay")
        .unwrap();
    let respuesta = RelayFrame::decode(&buf[..n]).unwrap();
    assert_eq!(respuesta.dst, 2);
}

#[tokio::test]
async fn el_control_del_relay_no_llega_a_los_eventos_de_juego() {
    let (relay, relay_addr) = fake_relay().await;
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let host_addr = loopback_addr(&host);
    host.attach_relay(test_link(relay_addr, 1));

    let control = RelayFrame::to_peer(
        TEST_RELAY_SESSION,
        0,
        1,
        RelayMessage::PeerJoined { peer: 5 },
    )
    .encode()
    .unwrap();
    relay.send_to(&control, host_addr).await.unwrap();
    tokio::time::sleep(Duration::from_millis(200)).await;

    let events = host.process_incoming().await;
    assert!(events.is_empty(), "un PeerJoined del relay no es gameplay");

    let control = host.drain_relay_control();
    assert_eq!(
        control.len(),
        1,
        "y sí tiene que llegar por su propio canal"
    );
    assert_eq!(control[0].message, RelayMessage::PeerJoined { peer: 5 });
    assert!(
        host.drain_relay_control().is_empty(),
        "vaciar el canal lo deja vacío"
    );
}

#[tokio::test]
async fn el_camino_directo_no_cambia_con_un_enlace_de_relay_puesto() {
    // La regresión que más importa: un host CON relay atado tiene que seguir sirviendo a un joiner
    // DIRECTO exactamente igual que antes de ADR-117.
    let (_relay, relay_addr) = fake_relay().await;

    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.attach_relay(test_link(relay_addr, 1));
    let host_addr = loopback_addr(&host);

    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;

    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    let events = joiner.process_incoming().await;

    assert!(
        events
            .iter()
            .any(|e| matches!(e, NetworkEvent::PeerConnected { .. })),
        "el joiner directo entra igual que siempre"
    );
    assert_eq!(joiner.world_seed, 42, "y adopta el mundo del host");
}

// ─── ADR-117: R1 de extremo a extremo, con un relay de VERDAD ───────────────────────────────
//
// Los tests de arriba prueban los hooks con un socket que hace de relay. Éstos levantan el relay
// real —el mismo binario que va a correr en el VPS, por su lib— y le hacen atravesar dos
// `NetworkManager` completos. Es la prueba de que R1 funciona; lo único que le falta para ser el
// criterio de aceptación es que las dos máquinas estén en redes distintas, y eso no se puede
// simular desde aquí.

use backrooms_relay::protocol::SessionToken;
use backrooms_relay::session::{RelayLimits, RelayTable};

/// Levanta el relay real en un puerto libre. El `Sender` que devuelve lo apaga al soltarse.
async fn real_relay() -> (SocketAddr, tokio::sync::oneshot::Sender<()>) {
    let socket = TestUdpSocket::bind("127.0.0.1:0").await.unwrap();
    let addr = socket.local_addr().unwrap();
    let (tx, rx) = tokio::sync::oneshot::channel();
    tokio::spawn(async move {
        backrooms_relay::server::serve(socket, RelayTable::new(RelayLimits::default()), async {
            let _ = rx.await;
        })
        .await;
    });
    (addr, tx)
}

fn relay_config(relay_addr: SocketAddr, session: u64, as_host: bool) -> relay_client::RelayConfig {
    relay_client::RelayConfig {
        relay_addr,
        session_id: session,
        token: SessionToken::new([0x5A; 16]),
        wire_version: crate::ipc::server::WIRE_SCHEMA_VERSION as u16,
        as_host,
    }
}

/// Bombea los dos enlaces hasta que los dos estén admitidos, o se rinde.
async fn pump_until_ready(host: &mut NetworkManager, joiner: &mut NetworkManager) {
    for _ in 0..60 {
        host.pump_relay().await;
        joiner.pump_relay().await;
        if host.relay_link().is_some() && joiner.relay_link().is_some() {
            return;
        }
        tokio::time::sleep(Duration::from_millis(30)).await;
    }
    panic!(
        "los dos tenían que quedar admitidos; host={:?} joiner={:?}",
        host.relay_state(),
        joiner.relay_state()
    );
}

#[tokio::test]
async fn dos_backends_se_encuentran_por_el_relay_sin_ninguna_ruta_directa() {
    // ESTE ES R1. El joiner no sabe la dirección del host y no podría alcanzarla aunque la
    // supiera: lo único que conoce es el relay y el identificador de la sesión.
    let (relay_addr, _shutdown) = real_relay().await;
    let session = 0x1234_5678_9ABC_DEF0u64;

    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.connect_relay(relay_config(relay_addr, session, true));

    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner.connect_relay(relay_config(relay_addr, session, false));

    pump_until_ready(&mut host, &mut joiner).await;

    // El relay repartió los ids: el host es el 1 por contrato, el joiner el siguiente.
    assert_eq!(host.relay_link().unwrap().my_peer, 1);
    assert_eq!(joiner.relay_link().unwrap().my_peer, 2);

    // Y ahora el handshake de JUEGO, contra la dirección sintética del host. A partir de aquí no
    // hay nada específico del relay: es el mismo `initiate_connection` de siempre.
    let host_addr = joiner.relay_host_addr().expect("el joiner sabe a dónde ir");
    assert!(transport::is_synthetic(&host_addr));
    joiner.initiate_connection(host_addr).await;

    let mut connected = false;
    for _ in 0..60 {
        host.process_incoming().await;
        let events = joiner.process_incoming().await;
        if events
            .iter()
            .any(|e| matches!(e, NetworkEvent::PeerConnected { .. }))
        {
            connected = true;
            break;
        }
        tokio::time::sleep(Duration::from_millis(30)).await;
    }

    assert!(
        connected,
        "el joiner tenía que entrar a la sesión por el relay"
    );
    assert_eq!(
        joiner.world_seed, 42,
        "y adoptar el mundo del host, como en una conexión directa"
    );
    assert_eq!(
        joiner.host_peer_id,
        Some(1),
        "el host es su peer 1 de juego"
    );

    // El host lo ve como a cualquier otro joiner, con su dirección sintética.
    let registrado = host.peers.values().next().expect("el host lo registró");
    assert!(
        transport::is_synthetic(&registrado.addr),
        "endpoint={} — tiene que ser sintética",
        registrado.addr
    );
}

#[tokio::test]
async fn el_gameplay_viaja_en_los_dos_sentidos_por_el_relay() {
    let (relay_addr, _shutdown) = real_relay().await;
    let session = 0x0F0E_0D0C_0B0A_0908u64;

    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.connect_relay(relay_config(relay_addr, session, true));
    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner.connect_relay(relay_config(relay_addr, session, false));
    pump_until_ready(&mut host, &mut joiner).await;

    joiner
        .initiate_connection(joiner.relay_host_addr().unwrap())
        .await;
    for _ in 0..60 {
        host.process_incoming().await;
        if !joiner.process_incoming().await.is_empty() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(30)).await;
    }
    assert_eq!(host.peer_count(), 1, "el joiner está dentro");

    // Del host al joiner, en el sentido que NO ha probado el handshake. Se usa `Disconnect`
    // porque produce un evento inequívoco al otro lado: si llega, el camino de bajada funciona.
    let joiner_peer = *host.peers.keys().next().unwrap();
    host.send_unreliable_to(
        joiner_peer,
        &PacketPayload::Disconnect {
            reason: "prueba de bajada".into(),
        },
    )
    .await;

    let mut recibido = false;
    for _ in 0..40 {
        let events = joiner.process_incoming().await;
        if events
            .iter()
            .any(|e| matches!(e, NetworkEvent::PeerDisconnected { .. }))
        {
            recibido = true;
            break;
        }
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
    assert!(
        recibido,
        "lo que manda el host tiene que llegar al joiner por el relay"
    );

    // Cero datagramas rechazados por el techo: el sobre del relay NO come presupuesto de gameplay.
    assert_eq!(host.refused_datagram_count(), 0);
    assert_eq!(joiner.refused_datagram_count(), 0);
}

#[tokio::test]
async fn un_token_que_no_es_deja_al_joiner_fuera_con_un_motivo() {
    // El fallo tiene que tener nombre, no ser un silencio de 15 s (ADR-117 D10).
    let (relay_addr, _shutdown) = real_relay().await;
    let session = 0x0102_0304_0506_0708u64;

    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.connect_relay(relay_config(relay_addr, session, true));

    let mut intruso = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    let mut config = relay_config(relay_addr, session, false);
    config.token = SessionToken::new([0xFF; 16]);
    intruso.connect_relay(config);

    let mut denegado = false;
    for _ in 0..40 {
        host.pump_relay().await;
        let events = intruso.pump_relay().await;
        if events
            .iter()
            .any(|e| matches!(e, relay_client::RelayClientEvent::Denied(_)))
        {
            denegado = true;
            break;
        }
        tokio::time::sleep(Duration::from_millis(30)).await;
    }

    assert!(
        denegado,
        "el relay tiene que decir que no, y decirlo pronto"
    );
    assert!(intruso.relay_link().is_none(), "y no abrirle el enlace");
    assert_eq!(intruso.relay_state().unwrap().name(), "DENIED");
}

#[tokio::test]
async fn si_el_relay_no_esta_el_intento_termina_con_un_motivo_y_no_cuelga() {
    // Un puerto donde no hay nadie. Lo que no puede pasar es que se reintente para siempre en
    // silencio, que es exactamente el modo de fallo que ADR-117 D10 prohíbe.
    let vacio: SocketAddr = "127.0.0.1:1".parse().unwrap();
    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner.connect_relay(relay_config(vacio, 0xDEAD, false));

    // No se esperan 10 s de reloj: se comprueba que el presupuesto EXISTE y que mientras corre no
    // hay enlace. El vencimiento en sí lo prueba `relay_client_tests` sumando tiempo.
    for _ in 0..5 {
        joiner.pump_relay().await;
        tokio::time::sleep(Duration::from_millis(20)).await;
    }

    assert!(joiner.relay_link().is_none());
    assert_eq!(joiner.relay_state().unwrap().name(), "REGISTERING");
    assert!(
        !joiner.relay_state().unwrap().is_final(),
        "todavía dentro del presupuesto"
    );
}

// ─── ADR-117 D10: la secuencia, ya cableada al reintento ────────────────────────────────────
//
// El AVANCE por presupuesto se prueba en `connect_tests` con el tiempo inyectado, que es donde se
// puede hacer sin esperar ocho segundos de reloj. Aquí se prueba lo otro: que el `NetworkManager`
// la arranca, la respeta, y que sin ella todo sigue igual que antes.

use crate::network::connect::{ConnectSequence, ConnectStage};

#[tokio::test]
async fn arrancar_una_secuencia_empieza_por_la_via_directa() {
    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    let directa: SocketAddr = "203.0.113.10:7778".parse().unwrap();
    let lan: SocketAddr = "192.168.1.168:7778".parse().unwrap();

    joiner
        .initiate_sequence(ConnectSequence::new(Some(directa), Some(lan), None, None))
        .await;

    assert_eq!(joiner.connect_stage(), Some(ConnectStage::Direct));
}

#[tokio::test]
async fn un_lobby_steam_only_arranca_por_la_etapa_de_steam() {
    // ADR-135: el host no publicó endpoint directo y no hay relay propio configurado. La única vía
    // es el túnel, que para el backend es un puerto de loopback como cualquier otro.
    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    let tunel: SocketAddr = "127.0.0.1:52345".parse().unwrap();

    joiner
        .initiate_sequence(ConnectSequence::new(None, None, Some(tunel), None))
        .await;

    assert_eq!(joiner.connect_stage(), Some(ConnectStage::Steam));
}

#[tokio::test]
async fn una_secuencia_sin_ninguna_via_falla_en_el_acto_en_vez_de_esperar() {
    // Es el caso del lobby sin `connect_ip` y sin relay: no hay nada que intentar, y decirlo en el
    // primer tick es infinitamente mejor que veinte segundos de «Conectando…».
    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();

    joiner
        .initiate_sequence(ConnectSequence::new(None, None, None, None))
        .await;

    let events = joiner.process_incoming().await;
    let fallo = events
        .iter()
        .find_map(|e| match e {
            NetworkEvent::ConnectFailed { reason } => Some(reason.clone()),
            _ => None,
        })
        .expect("tenía que fallar ya");
    assert!(fallo.contains("ninguna dirección"), "{fallo}");
    assert_eq!(joiner.connect_stage(), None);
}

#[tokio::test]
async fn sin_secuencia_el_reintento_es_exactamente_el_de_siempre() {
    // La garantía de no regresión del camino que ya funcionaba: `initiate_connection` a secas no
    // crea secuencia, así que `retry_pending_connection` va por la rama de toda la vida.
    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner
        .initiate_connection("127.0.0.1:1".parse().unwrap())
        .await;

    assert_eq!(joiner.connect_stage(), None, "no hay secuencia");
    joiner.retry_pending_connection().await; // no debe entrar en pánico ni cambiar de vía
    assert_eq!(joiner.connect_stage(), None);
}

#[tokio::test]
async fn un_relay_configurado_no_cambia_nada_hasta_que_el_relay_contesta() {
    // Cablear el relay no puede alterar una partida directa mientras el registro está en vuelo:
    // sin `PeerReady` no hay enlace, y sin enlace `send_datagram` no envuelve nada.
    let (_relay, relay_addr) = fake_relay().await;
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.connect_relay(relay_config(relay_addr, 0x77, true));

    assert!(host.relay_link().is_none(), "todavía no admitido");
    assert_eq!(host.relay_state().unwrap().name(), "REGISTERING");

    let host_addr = loopback_addr(&host);
    let mut joiner = NetworkManager::bind(0, 0, 0, false).await.unwrap();
    joiner.initiate_connection(host_addr).await;

    tokio::time::sleep(Duration::from_millis(200)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    let events = joiner.process_incoming().await;

    assert!(
        events
            .iter()
            .any(|e| matches!(e, NetworkEvent::PeerConnected { .. })),
        "el joiner directo entra igual, con el registro del relay a medias"
    );
}

// ─── Aviso de entrada en un joiner (2026-09-09) ───
//
// En estrella los joiners no se dan la mano entre sí: a un compañero nuevo se le conoce por el
// roster del anfitrión. Sin `PeerDiscovered`, sólo el host se enteraba de quién entraba.

fn roster_from_host(peers: Vec<protocol::PeerInfo>) -> IncomingPacket {
    IncomingPacket {
        addr: "127.0.0.1:9820".parse().unwrap(),
        header: PacketHeader::new(protocol::PacketType::PeerList as u16, 1, 0, 0),
        payload: PacketPayload::PeerList { peers },
    }
}

fn real_peer(id: PeerId, name: &str, port: u16) -> protocol::PeerInfo {
    protocol::PeerInfo {
        id,
        name: name.into(),
        addr: format!("192.168.1.50:{port}"),
        position: [0.0, 1.8, 0.0],
        relay_only: false,
    }
}

#[tokio::test]
async fn a_roster_peer_unknown_at_join_is_announced_once_and_a_phantom_never() {
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();

    let mut phantom = real_peer(9, "Robapieles", 0);
    phantom.addr = "0.0.0.0:0".into();
    phantom.relay_only = true;

    joiner
        .handle_packet(roster_from_host(vec![real_peer(8, "Compi", 7779), phantom]))
        .await;
    let events = joiner.process_incoming().await;

    let discovered: Vec<_> = events
        .iter()
        .filter_map(|e| match e {
            NetworkEvent::PeerDiscovered { id, name } => Some((*id, name.clone())),
            _ => None,
        })
        .collect();
    assert_eq!(
        discovered,
        vec![(8, "Compi".to_string())],
        "el compañero real se anuncia con su nombre; el robapieles no se une a ninguna partida"
    );

    // El mismo roster otra vez —el anfitrión lo reemite periódicamente— no vuelve a anunciar.
    joiner
        .handle_packet(roster_from_host(vec![real_peer(8, "Compi", 7779)]))
        .await;
    let again = joiner.process_incoming().await;
    assert!(
        !again
            .iter()
            .any(|e| matches!(e, NetworkEvent::PeerDiscovered { .. })),
        "un peer ya registrado no es un descubrimiento"
    );
}

#[tokio::test]
async fn a_peer_present_at_join_is_silent_but_its_recycled_id_is_announced_again() {
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    joiner.present_at_join.insert(8);

    joiner
        .handle_packet(roster_from_host(vec![real_peer(8, "Veterano", 7779)]))
        .await;
    let events = joiner.process_incoming().await;
    assert!(
        !events
            .iter()
            .any(|e| matches!(e, NetworkEvent::PeerDiscovered { .. })),
        "quien ya estaba cuando entramos no «se une»: el primer roster los trae a todos"
    );
    assert!(
        joiner.present_at_join.is_empty(),
        "la marca se consume al descubrirlo, una sola vez"
    );

    // Se va, y `allocate_peer_id` le da su número al siguiente que entre: ése SÍ es nuevo.
    joiner.peers.remove(&8);
    joiner
        .handle_packet(roster_from_host(vec![real_peer(8, "Novato", 7781)]))
        .await;
    let events = joiner.process_incoming().await;
    assert!(
        events.iter().any(|e| matches!(
            e,
            NetworkEvent::PeerDiscovered { id: 8, name } if name == "Novato"
        )),
        "un id reciclado es otra persona y se anuncia"
    );
}

#[tokio::test]
async fn the_handshake_ack_records_who_was_already_there() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();

    // Un compañero que ya estaba en la partida y un fantasma inyectado por el host.
    host.peers.insert(
        5,
        PeerConnection::new(5, "Veterano".into(), "192.168.1.60:7779".parse().unwrap()),
    );
    let mut ghost = PeerConnection::new(6, "Robapieles".into(), INERT_PEER_ADDR);
    ghost.relay_only = true;
    host.peers.insert(6, ghost);

    joiner.initiate_connection(loopback_addr(&host)).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    joiner.process_incoming().await;

    assert!(
        joiner.present_at_join.contains(&5),
        "el veterano estaba: {:?}",
        joiner.present_at_join
    );
    assert!(
        !joiner.present_at_join.contains(&6),
        "un relay_only no cuenta como presente porque nunca se anuncia"
    );
    assert!(
        !joiner.present_at_join.contains(&1),
        "el anfitrión no va en su propia lista de peers y no es un compañero"
    );
}

// ─── ADR-136: la invitación te deja al lado de quien te invitó ───

use crate::world::spawn_distribution::INVITE_SPAWN_OFFSET_M;

fn peer_at(id: PeerId, name: &str, addr: &str, position: [f32; 3]) -> PeerConnection {
    let mut conn = PeerConnection::new(id, name.into(), addr.parse().unwrap());
    conn.position = position;
    conn
}

/// D4/D5 — un cliente invita: el invitado nace a `INVITE_SPAWN_OFFSET_M` de la posición del
/// invitador SEGÚN EL ROSTER, sin gastar unidad de reparto, y el punto queda recordado.
#[tokio::test]
async fn an_invited_joiner_is_placed_beside_a_client_inviter() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.platform_ids.insert(111, 5);
    host.peers.insert(
        5,
        peer_at(5, "Invitador", "192.168.1.60:7779", [100.0, 4.7, 200.0]),
    );

    let p = host
        .assign_spawn_point_for(7, 111)
        .expect("un invitador vivo siempre da punto");
    let anchor = crate::utils::Vec3::new(100.0, 4.7, 200.0);
    assert!(
        (p.distance_xz(anchor) - INVITE_SPAWN_OFFSET_M).abs() < 1e-3,
        "a {:.2} m del invitador",
        p.distance_xz(anchor)
    );
    assert_eq!(p.y, 4.7, "misma planta que el invitador");
    assert_eq!(
        host.next_spawn_unit, 0,
        "entra en la unidad del invitador: no gasta otra"
    );
    assert_eq!(
        host.assign_spawn_point_for(7, 111),
        Some(p),
        "idempotente por peer, como el reparto"
    );
}

/// D3 — el anfitrión invita: es una entrada más del mapa, resuelta contra `local_position`.
#[tokio::test]
async fn an_invited_joiner_is_placed_beside_the_host_by_the_same_path() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.local_platform_id = 999;
    host.local_position = [10.0, 1.8, 20.0];

    let p = host
        .assign_spawn_point_for(7, 999)
        .expect("el anfitrión está siempre");
    let anchor = crate::utils::Vec3::new(10.0, 1.8, 20.0);
    assert!((p.distance_xz(anchor) - INVITE_SPAWN_OFFSET_M).abs() < 1e-3);
    assert_eq!(host.next_spawn_unit, 0);
}

/// D6 — invitador desconocido, ya ido, o un fantasma: se reparte como siempre y nunca se bloquea.
#[tokio::test]
async fn an_unresolvable_inviter_falls_back_to_distribution() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();

    // Desconocido.
    let a = host
        .assign_spawn_point_for(7, 12_345)
        .expect("reparto normal");
    assert_eq!(host.next_spawn_unit, 1, "el fallback SÍ gasta unidad");
    assert_eq!(
        a,
        host.assign_spawn_point(7).unwrap(),
        "y es el punto del reparto"
    );

    // Conocido pero ya ido.
    host.platform_ids.insert(111, 5);
    let b = host.assign_spawn_point_for(8, 111).expect("reparto normal");
    assert_eq!(host.next_spawn_unit, 2);
    assert!(
        b.distance_xz(a) >= crate::world::spawn_distribution::MIN_PLAYER_SEPARATION_M,
        "es el reparto de ADR-116, con su separación"
    );

    // Un fantasma inyectado no es un invitador.
    let mut ghost = PeerConnection::new(6, "Robapieles".into(), INERT_PEER_ADDR);
    ghost.relay_only = true;
    host.peers.insert(6, ghost);
    host.platform_ids.insert(222, 6);
    let _ = host.assign_spawn_point_for(9, 222).expect("reparto normal");
    assert_eq!(host.next_spawn_unit, 3);
}

/// D3 — un `0` nunca entra en el mapa, y la identidad se va con el peer.
#[tokio::test]
async fn a_zero_identity_never_enters_the_map_and_the_map_forgets_a_gone_peer() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut anon = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let mut named = NetworkManager::bind(0, 3, 42, false).await.unwrap();
    named.local_platform_id = 424_242;
    let host_addr = loopback_addr(&host);

    anon.initiate_connection(host_addr).await;
    named.initiate_connection(host_addr).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    host.process_incoming().await;

    assert_eq!(
        host.platform_ids.len(),
        1,
        "sólo la identidad real: {:?}",
        host.platform_ids
    );
    let named_id = *host
        .platform_ids
        .get(&424_242)
        .expect("la identidad del que la tiene");
    assert!(host.peers.contains_key(&named_id));

    host.peers.remove(&named_id);
    host.purge_peer_state(named_id);
    assert!(
        host.platform_ids.is_empty(),
        "un id reciclado no puede heredar la identidad de otro"
    );
}

/// El caso entero por el cable: el joiner dice quién le invitó, el anfitrión resuelve contra su
/// propia posición y el punto llega en el ack. Un anfitrión anterior al ADR ignoraría los campos
/// y repartiría como siempre: los `serde(default)` de arriba son lo que lo garantiza.
#[tokio::test]
async fn an_invited_joiner_gets_the_point_through_a_real_handshake() {
    let mut host = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    host.local_platform_id = 999;
    host.local_position = [10.0, 1.8, 20.0];
    let mut joiner = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    joiner.local_platform_id = 424_242;
    joiner.invited_by = 999;

    joiner.initiate_connection(loopback_addr(&host)).await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    host.process_incoming().await;
    tokio::time::sleep(Duration::from_millis(150)).await;
    joiner.process_incoming().await;

    let p = joiner
        .assigned_spawn_from_host
        .expect("el ack trae el punto de la invitación");
    let anchor = crate::utils::Vec3::new(10.0, 1.8, 20.0);
    assert!(
        (p.distance_xz(anchor) - INVITE_SPAWN_OFFSET_M).abs() < 1e-3,
        "a {:.2} m del anfitrión",
        p.distance_xz(anchor)
    );
    assert_eq!(host.next_spawn_unit, 0);
}
