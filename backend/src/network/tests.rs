use super::*;
use std::time::Duration;

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
        &mut host_drawn,
    );
    let mut joiner_drawn = Vec::new();
    crate::world::phantom_spawn::draw_into(
        joiner.world_seed,
        (0, 0),
        0,
        joiner.phantom_density_scale,
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
    let phantom_id = host.spawn_phantom("Skinwalker", [0.0, 0.0, 0.0]);

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
        }],
    );
    super::sync::broadcast_corpses(&host, &world).await;
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
    let phantom_id = host.spawn_phantom("Victima", [2.0, 1.8, 0.0]);

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

#[tokio::test]
async fn reliable_retransmit_failure_does_not_remove_peer() {
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

    host.process_retransmits().await;

    assert!(
        host.peers.contains_key(&peer_id),
        "reliable retransmit exhaustion must not remove a connected peer"
    );
    assert_eq!(host.peers[&peer_id].reliable_queue.len(), 0);
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
    host.handle_handshake(newcomer, 0, "TooMany".into(), "0.1.0".into())
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

    host.handle_handshake(newcomer, 0, "Joiner".into(), "0.1.0".into())
        .await;

    assert_eq!(host.real_peer_count(), 1);
    assert!(host.peers.values().any(|p| p.addr == newcomer));
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
    let pid = host.spawn_phantom("Robapieles_Test", [10.0, 1.8, 5.0]);

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
    let pid = host.spawn_phantom("Robapieles_Test", [0.0, 1.8, 0.0]);
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
    let pid = host.spawn_phantom("Robapieles_Test", [0.0, 1.8, 0.0]);
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
    let phantom_id = host.spawn_phantom("Robapieles_Test", [0.0, 1.8, 0.0]);

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
    let ghost_a = host.spawn_phantom("Robapieles_A", [0.0, 1.8, 0.0]);
    let ghost_b = host.spawn_phantom("Robapieles_B", [40.0, 1.8, 40.0]);

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
