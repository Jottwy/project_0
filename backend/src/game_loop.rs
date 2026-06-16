//! Authoritative game loop. Runs at 60hz, processes IPC input from Unity,
//! simulates local state (world, entities, stats), manages P2P networking,
//! and streams `WorldState` back at 10hz. See ARCHITECTURE_V1.md §6.1.

use std::collections::HashSet;
use std::time::Duration;

use log::{debug, info};
use tokio::sync::{broadcast, mpsc};
use tokio::time::{interval, MissedTickBehavior};

use crate::ipc::{
    ClientMessage, GameEvent, LocalPlayerState, MovementDelta, PlayerAction, PlayerInput,
    RemotePlayerState, ServerMessage, StatsView, WorldState,
};
use crate::network::sync;
use crate::network::{NetworkEvent, NetworkManager};
use crate::player::Player;
use crate::utils::{world_to_chunk, ChunkPos, Vec3, CHUNK_SIZE};
use crate::world::collision::{resolve_safe_spawn, Level0Collision};
use crate::world::World;

const TICK_HZ: u64 = 60;
const TICK_DURATION: Duration = Duration::from_nanos(1_000_000_000 / TICK_HZ);
/// WorldState to Unity at 10hz.
const WORLD_STATE_EVERY: u64 = 6;
/// ADR-009 §2: authoritative movement delta to Unity at 20hz (60 / 3).
const MOVEMENT_DELTA_EVERY: u64 = 3;
/// Entity AI runs at 10hz.
const ENTITY_TICK_EVERY: u64 = 6;
/// Ownership + teleportation checked at 1hz.
const SLOW_TICK_EVERY: u64 = 60;
/// Player position broadcast to peers at 10hz.
const NET_BROADCAST_EVERY: u64 = 6;
/// Heartbeat to peers every 1s.
const HEARTBEAT_EVERY: u64 = 60;
/// Chunk state broadcast at 5hz.
const CHUNK_BROADCAST_EVERY: u64 = 12;

const BASE_SPEED: f32 = 5.0;
const SPRINT_MULT: f32 = 1.5;
/// ADR-009 Option B: tolerance added to the speed cap when validating the
/// client-reported velocity before accepting its predicted position.
const SPEED_TOLERANCE: f32 = 0.5;
/// Stamina drained per second while the client reports the run move-state.
const RUN_STAMINA_DRAIN: f32 = 15.0;

/// TEMP DIAGNOSTIC — forces the god-traversal collision bypass on,
/// independent of the DEV_GOD_TRAVERSAL environment variable.
/// MUST be reverted to `false` (or removed) after validation.
const DEV_GOD_TRAVERSAL_HARDCODED: bool = true;

pub async fn run(
    mut from_clients: mpsc::Receiver<ClientMessage>,
    to_clients: broadcast::Sender<ServerMessage>,
    mut net: NetworkManager,
) {
    let mut player = Player::new(net.local_id, &net.local_name);
    let mut world = World::new(net.world_seed);
    let dt = 1.0 / TICK_HZ as f32;
    let entity_dt = dt * ENTITY_TICK_EVERY as f32;
    let dev_freeze_survival = env_flag_enabled("DEV_FREEZE_SURVIVAL");
    let dev_god_traversal = DEV_GOD_TRAVERSAL_HARDCODED || env_flag_enabled("DEV_GOD_TRAVERSAL");

    let mut ticker = interval(TICK_DURATION);
    ticker.set_missed_tick_behavior(MissedTickBehavior::Skip);

    let mut tick: u64 = 0;
    let mut received_input = PlayerInput::default();
    let mut has_received_input = false;
    // ADR-009: ack the last input the server actually ACCEPTED (not merely
    // received). u32::MAX = "nothing accepted yet"; input_seq is 0-based, so the
    // first real input (seq 0) is processed correctly.
    let mut last_accepted_input_seq: u32 = u32::MAX;
    // Authoritative velocity echoed in the 20 Hz delta: the accepted client
    // velocity, or zero when the speed cap rejected the move (held in place).
    let mut authoritative_velocity = Vec3::ZERO;
    let mut processed_interactions: HashSet<(u16, u64)> = HashSet::new();
    // Track the last chunk position used for ownership so we only call
    // update_ownership when the player crosses a chunk boundary, not every tick.
    let mut last_ownership_chunk: Option<ChunkPos> = None;

    // Bootstrap: host/solo creates the authoritative initial structure before
    // loading the surrounding ownership radius. Joiners wait for host WorldSync.
    //
    // `spawn_resolved` tracks whether the player has been placed on a validated
    // safe cell yet. The host resolves immediately after generation; a joiner
    // resolves once it has connected and received the host's world.
    let mut spawn_resolved = false;
    if net.is_host {
        world.generate_initial_structures(player.id);
        world.update_ownership(player.position, player.id);
        let res = resolve_safe_spawn(&mut world, preferred_spawn());
        player.position = res.position;
        spawn_resolved = true;
        // Reload ownership around the validated spawn so the streamed radius is
        // centred on where the player actually stands.
        world.update_ownership(player.position, player.id);
    }

    info!(
        "Game loop started at {TICK_HZ}hz (tick = {TICK_DURATION:?}), peer_id={}, host={}",
        net.local_id, net.is_host
    );
    info!(
        "MPTRACE step=V26 event=level0_runtime_patch_active version=phase_2_6 build_marker=spawn_safety_layout_polish"
    );
    if dev_freeze_survival {
        info!(
            "DEV_FREEZE_SURVIVAL active: hunger/thirst/sanity decay and player damage are disabled"
        );
    }
    if dev_god_traversal {
        info!(
            "DEV_GOD_TRAVERSAL active: collision resolution and survival death/respawn are disabled"
        );
    }
    // TEMP god-traversal audit trace: always log exe path + raw env value +
    // effective bypass state so "which backend / which env" is provable.
    info!(
        "MPTRACE step=GODT event=god_traversal_audit exe={} env_value={:?} collision_bypass_enabled={}",
        std::env::current_exe()
            .map(|p| p.display().to_string())
            .unwrap_or_else(|_| "<unknown>".into()),
        std::env::var("DEV_GOD_TRAVERSAL").ok(),
        dev_god_traversal
    );

    loop {
        ticker.tick().await;

        // ─── PHASE 1: RECEIVE (IPC + Network) ───
        while let Ok(msg) = from_clients.try_recv() {
            match msg {
                ClientMessage::Input(input) => {
                    received_input = input;
                    has_received_input = true;
                }
                ClientMessage::Action(action) => {
                    debug!("action received: {}", action.action_type);
                    handle_action(
                        &action,
                        &mut player,
                        &mut world,
                        &mut net,
                        &to_clients,
                        &mut processed_interactions,
                    )
                    .await;
                }
                ClientMessage::UiEvent(ev) => {
                    debug!("ui event: {}", ev.event_type);
                }
            }
        }

        // Process incoming network packets.
        let net_events = net.process_incoming().await;
        for event in net_events {
            handle_network_event(
                event,
                &mut player,
                &mut world,
                &mut net,
                &to_clients,
                &mut processed_interactions,
            )
            .await;
        }

        // A joiner places its local player only once it has connected and the
        // host's world has arrived — never on the empty/pre-sync world, and
        // never at the unsafe chunk-corner origin.
        if !spawn_resolved && net.peer_count() > 0 && !world.chunks.is_empty() {
            let res = resolve_safe_spawn(&mut world, preferred_spawn());
            player.position = res.position;
            spawn_resolved = true;
        }

        // ─── PHASE 2: SIMULATE ───
        // Only apply once a real input has arrived — the default PlayerInput has
        // position [0,0,0], which would otherwise drag the player to the origin
        // before the client's first packet. Track the accepted seq for the ack.
        if has_received_input {
            match apply_movement(&mut player, &received_input, dt, &world, tick, dev_god_traversal)
            {
                Some(seq) => {
                    last_accepted_input_seq = seq;
                    authoritative_velocity = Vec3::from_array(received_input.velocity);
                }
                // Speed cap removed: apply_movement always accepts the pose, so this
                // arm is now unreachable; kept for match exhaustiveness.
                None => authoritative_velocity = Vec3::ZERO,
            }
        }
        // Only refresh chunk ownership when the player crosses a chunk boundary.
        // Calling update_ownership every tick caused ~2 FPS churn from constant
        // chunk rebuild. Startup fires because last_ownership_chunk starts as None.
        {
            let current_chunk = world_to_chunk(player.position);
            if last_ownership_chunk != Some(current_chunk) {
                let reason = if last_ownership_chunk.is_none() {
                    "startup"
                } else {
                    "chunk_changed"
                };
                world.update_ownership(player.position, player.id);
                let loaded_chunks = world.chunks.len();
                info!(
                    "MPTRACE step=STREAM event=ownership_refresh player_chunk=({},{}) loaded_chunks={} reason={}",
                    current_chunk.0, current_chunk.1, loaded_chunks, reason
                );
                last_ownership_chunk = Some(current_chunk);
            }
        }
        if tick % 60 == 0 {
            info!(
                "MPTRACE step=Q event=local_transform_from_ipc self_id={} pos=({:.2},{:.2},{:.2}) rot={:.2}",
                net.local_id,
                player.position.x,
                player.position.y,
                player.position.z,
                player.rotation
            );
        }

        // Entity AI at 10hz.
        if tick % ENTITY_TICK_EVERY == 0 {
            let (damage, events) = world.tick_entities(entity_dt, player.position, player.id);
            if !dev_freeze_survival && damage > 0.0 {
                player.stats.take_damage(damage);
            }
            for ev in events {
                if dev_freeze_survival && ev.event_type == "damage_taken" {
                    continue;
                }
                let _ = to_clients.send(ServerMessage::Event(ev));
            }
            world.tick_respawns(entity_dt);
        }

        // Ownership is now handled per-chunk-boundary above; only teleportation
        // and other slow-tick work runs here.
        if tick % SLOW_TICK_EVERY == 0 && (net.is_host || net.peer_count() == 0) {
            let events = world.tick_teleportation();
            for ev in &events {
                let _ = to_clients.send(ServerMessage::Event(ev.clone()));
            }
            // Broadcast teleport events to peers.
            for ev in &events {
                if let Some(data) = ev.data.as_object() {
                    if let (Some(pos), Some(offset)) =
                        (data.get("chunk_pos"), data.get("new_offset"))
                    {
                        let old_pos = [
                            pos.as_array().and_then(|a| a[0].as_i64()).unwrap_or(0) as i32,
                            pos.as_array().and_then(|a| a[1].as_i64()).unwrap_or(0) as i32,
                        ];
                        let new_pos = [
                            old_pos[0]
                                + offset.as_array().and_then(|a| a[0].as_i64()).unwrap_or(0) as i32,
                            old_pos[1]
                                + offset.as_array().and_then(|a| a[1].as_i64()).unwrap_or(0) as i32,
                        ];
                        sync::broadcast_chunk_teleport(&net, old_pos, new_pos, 0).await;
                    }
                }
            }
        }

        // Stats with real context from the world.
        let ctx = world.stat_context_for(player.position, net.peer_count() as u32);
        if dev_freeze_survival {
            player.stats.speed_modifier = 1.0;
            player.stats.accuracy_modifier = 1.0;
            player.stats.hallucination_intensity = 0.0;
        } else {
            player.stats.update(dt, &ctx);
        }

        // Death → respawn on a validated safe cell (never the unsafe origin).
        // DEV_GOD_TRAVERSAL: survival death and the resulting respawn are
        // skipped so debug traversal is never interrupted.
        if !dev_god_traversal && player.stats.is_dead() {
            info!("Player died — respawning");
            player.stats = crate::player::stats::PlayerStats::on_respawn();
            let res = resolve_safe_spawn(&mut world, preferred_spawn());
            player.position = res.position;
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "player_died".into(),
                data: serde_json::json!({ "death_pos": player.position.to_array() }),
            }));
            world.update_ownership(player.position, player.id);
        }

        // Stat warnings at 1hz.
        if tick % SLOW_TICK_EVERY == 0 {
            emit_stat_warnings(&player, &to_clients);
        }

        // ─── PHASE 3: NETWORK SEND ───

        // Broadcast player position to peers at 10hz.
        if tick % NET_BROADCAST_EVERY == 0 {
            sync::broadcast_player_update(&net, &player).await;
            // Host-as-server relay: the host re-advertises the FULL peer roster (ids +
            // current positions) so every joiner learns about ALL other peers, not just
            // the host. Without this a joiner — which only connects to the host — never
            // sees the other joiners. (A joiner's own roster is just {self, host}, so
            // only the host has anything to relay.)
            if net.is_host {
                sync::broadcast_peer_roster(&net, &player).await;
                sync::broadcast_stp_items(&net).await;
                sync::broadcast_stp_buildings(&net).await;
                sync::broadcast_stp_carryables(&net).await;
                sync::broadcast_stp_harvestables(&net).await;
            }
        }

        // Broadcast chunk states at 5hz.
        if tick % CHUNK_BROADCAST_EVERY == 0 {
            sync::broadcast_chunk_states(&net, &world, player.position).await;
        }

        // Heartbeat every 1s.
        if tick % HEARTBEAT_EVERY == 0 {
            net.retry_pending_connection().await;
            net.send_heartbeats().await;

            // Check timeouts.
            let timeout_events = net.check_timeouts();
            for event in timeout_events {
                handle_network_event(
                    event,
                    &mut player,
                    &mut world,
                    &mut net,
                    &to_clients,
                    &mut processed_interactions,
                )
                .await;
            }
        }

        // Process reliable retransmits.
        if tick % ENTITY_TICK_EVERY == 0 {
            net.process_retransmits().await;
        }

        // ─── PHASE 4: SEND ───

        // ADR-009 §2: authoritative movement delta at 20hz for the client
        // reconciler — pose + accepted-input ack, decoupled from the full snapshot.
        if tick % MOVEMENT_DELTA_EVERY == 0 {
            let _ = to_clients.send(ServerMessage::DeltaUpdate(MovementDelta {
                tick,
                ack_input_seq: last_accepted_input_seq,
                position: player.position.to_array(),
                velocity: authoritative_velocity.to_array(),
            }));
        }

        // Full WorldState (stats/chunks/entities) to Unity at 10hz.
        if tick % WORLD_STATE_EVERY == 0 {
            let snapshot =
                build_world_state(tick, &player, &mut world, &net, last_accepted_input_seq);
            let _ = to_clients.send(ServerMessage::WorldState(snapshot));
        }

        tick = tick.wrapping_add(1);
    }
}

fn env_flag_enabled(name: &str) -> bool {
    std::env::var(name)
        .map(|value| {
            let value = value.trim();
            value == "1"
                || value.eq_ignore_ascii_case("true")
                || value.eq_ignore_ascii_case("yes")
                || value.eq_ignore_ascii_case("on")
        })
        .unwrap_or(false)
}

async fn handle_network_event(
    event: NetworkEvent,
    player: &mut Player,
    world: &mut World,
    net: &mut NetworkManager,
    to_clients: &broadcast::Sender<ServerMessage>,
    processed_interactions: &mut HashSet<(u16, u64)>,
) {
    match event {
        NetworkEvent::PeerConnected { id, name } => {
            info!("Peer connected id={} name={}", id, name);
            info!(
                "MPTRACE step=G event=peer_registry_after_connected self_id={} sender_id=<event> assigned_id=<event> peer_id={} endpoint=<registered> peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
                net.local_id,
                id,
                net.peer_count(),
                net.peer_ids()
            );
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "player_joined".into(),
                data: serde_json::json!({ "player_id": id, "name": name }),
            }));

            // If we're the host, send world sync to the new peer.
            if net.is_host {
                sync::send_world_sync(net, id, world, player).await;
            } else if id == 1 {
                world.reset_for_remote_world(net.world_seed, 1);
                info!(
                    "MPTRACE step=Z event=apply_world_snapshot self_id={} revision={} chunks=0 entities=0 items=0 reason=joiner_reset_for_host_seed seed={}",
                    net.local_id,
                    world.revision,
                    world.seed
                );
            }

            // Keep the local player identity aligned with the peer id assigned by the host.
            if !net.is_host && player.id != net.local_id {
                player.id = net.local_id;
            }
        }

        NetworkEvent::PeerDisconnected { id, reason } => {
            info!("Peer disconnected: id={}, reason={}", id, reason);
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "player_left".into(),
                data: serde_json::json!({ "player_id": id, "name": "", "reason": reason }),
            }));
        }

        NetworkEvent::RemotePlayerUpdate {
            id,
            position,
            rotation,
            animation,
        } => {
            debug!(
                "Remote player received: id={}, pos=({:.2}, {:.2}, {:.2}), rot={:.1}, anim={}",
                id, position[0], position[1], position[2], rotation, animation
            );
            // Player state is tracked in PeerConnection; WorldState builder reads it.
        }

        NetworkEvent::WorldSyncReceived {
            world_seed,
            world_revision,
            chunks,
        } => {
            info!(
                "Received world sync with seed={}, revision={}, {} chunks",
                world_seed,
                world_revision,
                chunks.len()
            );
            world.apply_world_sync(world_seed, world_revision, &chunks, net.local_id);
        }

        NetworkEvent::StpPickupRequest {
            item_id,
            requester_id,
        } => {
            if net.is_host {
                process_stp_pickup(item_id, requester_id, net, to_clients).await;
            }
        }

        NetworkEvent::StpPickupGranted {
            item_id,
            def_id,
            count,
        } => {
            // We are the recoger: surface the grant to our Unity, which credits the
            // local STP inventory (StpPickupController). The item already vanished via
            // the host's stp_items removal.
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "stp_pickup_granted".into(),
                data: serde_json::json!({
                    "item_id": item_id,
                    "def_id": def_id,
                    "count": count,
                }),
            }));
        }

        NetworkEvent::StpDropRequest {
            drop_id,
            def_id,
            count,
            position,
            rotation,
        } => {
            if net.is_host {
                process_stp_drop(drop_id, def_id, count, position, rotation, net);
            }
        }

        NetworkEvent::StpPlaceRequest {
            place_id,
            def_id,
            position,
            rotation,
        } => {
            if net.is_host {
                process_stp_place(place_id, def_id, position, rotation, net);
            }
        }

        NetworkEvent::StpBuildAddRequest {
            add_id,
            building_id,
            material_id,
        } => {
            if net.is_host {
                process_stp_build_add(add_id, building_id, material_id, net);
            }
        }

        NetworkEvent::StpCarryablePickupRequest {
            carryable_id,
            requester_id,
        } => {
            if net.is_host {
                process_stp_carryable_pickup(carryable_id, requester_id, net, to_clients).await;
            }
        }

        NetworkEvent::StpCarryablePickupGranted {
            carryable_id,
            def_id,
        } => {
            // We are the recoger: surface the grant to our Unity, which carries the
            // carryable in hand (StpCarryablePickupController). It already vanished for
            // everyone via the host's stp_carryables removal.
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "stp_carryable_pickup_granted".into(),
                data: serde_json::json!({
                    "carryable_id": carryable_id,
                    "def_id": def_id,
                }),
            }));
        }

        NetworkEvent::StpCarryableDropRequest {
            drop_id,
            def_id,
            position,
            rotation,
        } => {
            if net.is_host {
                process_stp_carryable_drop(drop_id, def_id, position, rotation, net);
            }
        }

        NetworkEvent::StpHarvestHitRequest {
            hit_id,
            harvestable_id,
            amount,
        } => {
            if net.is_host {
                process_stp_harvest_hit(hit_id, harvestable_id, amount, net);
            }
        }

        NetworkEvent::WorldInteractRequest {
            requester_id,
            request_id,
            target_id,
            target_kind,
            interaction_type,
            player_position,
        } => {
            if !net.is_host {
                return;
            }

            process_authoritative_interaction(
                requester_id,
                request_id,
                target_id,
                &target_kind,
                &interaction_type,
                Vec3::from_array(player_position),
                world,
                net,
                player,
                processed_interactions,
            )
            .await;
        }

        NetworkEvent::ChunkTransferReceived { from, data } => {
            info!(
                "Received chunk transfer [{}, {}] from peer {}",
                data.pos[0], data.pos[1], from
            );
            world.apply_chunk_transfer(&data, net.local_id);

            // ACK the transfer.
            let ack = crate::network::protocol::PacketPayload::ChunkTransferAck { pos: data.pos };
            net.send_reliable(from, &ack).await;
        }

        NetworkEvent::ChunkTransferAckReceived { from, pos } => {
            debug!(
                "Chunk transfer ACK from {} for [{}, {}]",
                from, pos[0], pos[1]
            );
        }

        NetworkEvent::ChunkTeleportReceived {
            old_pos,
            new_pos: _,
            new_seed,
        } => {
            world.apply_remote_teleport(old_pos, new_seed);
        }

        NetworkEvent::AnchorBroadcastReceived {
            chunk_pos,
            durability: _,
            installed_by: _,
        } => {
            world.set_chunk_anchored(chunk_pos);
        }

        NetworkEvent::StabilizerBroadcastReceived {
            chunk_pos,
            tier: _,
            remaining_hours: _,
        } => {
            world.set_chunk_stabilized(chunk_pos);
        }

        NetworkEvent::HandshakeReceived { .. } => {
            // Handled internally by NetworkManager.
        }
    }
}

fn preferred_spawn() -> Vec3 {
    // Centre of the starter chunk (0,0). The resolver snaps this to the nearest
    // validated safe cell; Y is recomputed from the chunk floor.
    Vec3::new(CHUNK_SIZE * 0.5, 1.8, CHUNK_SIZE * 0.5)
}

/// ADR-009 Option B: apply the client's authoritative-pose input and return the
/// accepted `input_seq` (`None` when the speed cap rejected the move). The legacy
/// direction-integration path was removed with the `input_seq` gate — `input_seq`
/// is now 0-based and every input is a prediction packet.
fn apply_movement(
    player: &mut Player,
    input: &PlayerInput,
    dt: f32,
    world: &World,
    tick: u64,
    god_traversal: bool,
) -> Option<u32> {
    player.rotation = input.look[1].rem_euclid(360.0); // yaw is INPUT (ADR-009 §8)
    apply_client_authoritative_move(player, input, dt, world, tick, god_traversal)
}

/// ADR-009 Option B authoritative-move validation. The client owns prediction and
/// its position, so the reported pose is ALWAYS applied — it is never discarded.
///   * speed cap  — REMOVED. It used to `return None` when the finite-difference
///                  velocity exceeded the sprint cap, holding the player at the last
///                  accepted pose; on the remote avatar that surfaced as teleport
///                  JUMPS between accepted poses. Velocity now only drives stamina.
///   * collision  — still clamps the claimed position against static level geometry
///                  (slides, never freezes).
/// No server-side physics integration is performed. Always returns the accepted
/// `input_seq` (`Some`).
fn apply_client_authoritative_move(
    player: &mut Player,
    input: &PlayerInput,
    dt: f32,
    world: &World,
    _tick: u64,
    god_traversal: bool,
) -> Option<u32> {
    // Run-drain stamina from the reported move-state (2 == run).
    if input.move_state == 2 {
        player.stats.use_stamina(RUN_STAMINA_DRAIN * dt);
    }

    let claimed = Vec3::from_array(input.position);
    // Velocity is intentionally NOT used to gate the pose (see doc above): a velocity
    // cap that rejected the whole pose caused visible teleport jumps. Coop trusts the
    // client position; wall collision below is the only spatial constraint.

    if god_traversal {
        player.position = claimed;
        return Some(input.input_seq);
    }

    // Collision: verify the claimed position doesn't intersect static geometry.
    // resolve_move slides/clamps against the level; the resolved point is the
    // authoritative pose echoed back to the client.
    let resolved = Level0Collision::resolve_move(world, player.position, claimed);
    player.position = resolved.position;
    Some(input.input_seq)
}

async fn handle_action(
    action: &PlayerAction,
    player: &mut Player,
    world: &mut World,
    net: &mut NetworkManager,
    to_clients: &broadcast::Sender<ServerMessage>,
    processed_interactions: &mut HashSet<(u16, u64)>,
) {
    match action.action_type.as_str() {
        "world_interact" => {
            let target_id = json_u32(&action.data, "target_id").unwrap_or(0);
            let request_id = json_u64(&action.data, "request_id").unwrap_or(0);
            let target_kind = json_str(&action.data, "target_kind").unwrap_or("item");
            let interaction_type = json_str(&action.data, "interaction_type").unwrap_or("pickup");
            info!(
                "MPTRACE step=AC event=interact_request_from_ipc self_id={} target_id={} kind={} type={} request_id={}",
                net.local_id,
                target_id,
                target_kind,
                interaction_type,
                request_id
            );

            // pickup needs an existing target_id; drop has none (it creates an item).
            if request_id == 0 || (interaction_type != "drop" && target_id == 0) {
                info!(
                    "MPTRACE step=AF event=host_validate_interaction result=rejected reason=invalid_request target_id={} requester_id={}",
                    target_id,
                    net.local_id
                );
                return;
            }

            if net.is_host {
                process_authoritative_interaction(
                    net.local_id,
                    request_id,
                    target_id,
                    target_kind,
                    interaction_type,
                    player.position,
                    world,
                    net,
                    player,
                    processed_interactions,
                )
                .await;
            } else {
                let payload = crate::network::protocol::PacketPayload::Interact {
                    requester_id: net.local_id,
                    request_id,
                    target_id,
                    target_kind: target_kind.into(),
                    interaction_type: interaction_type.into(),
                    player_position: player.position.to_array(),
                };
                info!(
                    "MPTRACE step=AD event=send_interact_request_to_host self_id={} host_id=1 target_id={} request_id={}",
                    net.local_id,
                    target_id,
                    request_id
                );
                net.send_reliable(1, &payload).await;
            }
        }
        "attack" => {
            let player_pos = player.position;
            let attack_range = 3.0f32;
            let damage = 10u8;
            for chunk in world.chunks.values_mut() {
                for entity in chunk.entities.iter_mut() {
                    if entity.is_alive() && entity.position.distance_xz(player_pos) <= attack_range
                    {
                        let killed = entity.take_damage(damage);
                        if killed {
                            let cable_count: u16 = rand::random::<u16>() % 6 + 5;
                            player
                                .inventory
                                .add(crate::player::inventory::Item::Cable, cable_count);
                            info!("Entity {} killed, dropped {} cable", entity.id, cable_count);
                        }
                        break;
                    }
                }
            }
        }
        "interact" | "pickup" => {
            debug!("legacy local pickup ignored; use world_interact with target_id");
        }
        // Phase 1: the host Unity registers the authoritative STP item list. Gated to
        // the host (joiners' lists come only via the relayed StpItemList packet, so a
        // joiner sending this is ignored and cannot diverge the world).
        "set_stp_items" => {
            if !net.is_host {
                return;
            }
            let items: Vec<crate::network::protocol::StpItemInfo> = action
                .data
                .get("items")
                .cloned()
                .and_then(|v| serde_json::from_value(v).ok())
                .unwrap_or_default();
            info!(
                "MPTRACE step=SI event=host_set_stp_items self_id={} count={}",
                net.local_id,
                items.len()
            );
            net.stp_items = items;
        }
        // Phase 2: a client asks to pick up an STP item. Host-authoritative: the host
        // validates and removes it (vanishes for all via the stp_items relay) and grants
        // it to the requester; a joiner forwards the request to the host.
        "stp_pickup" => {
            let item_id = json_u32(&action.data, "item_id").unwrap_or(0);
            if item_id == 0 {
                return;
            }
            if net.is_host {
                process_stp_pickup(item_id, net.local_id, net, to_clients).await;
            } else {
                let payload = crate::network::protocol::PacketPayload::StpPickupRequest {
                    item_id,
                    requester_id: net.local_id,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase 3: a client dropped an STP item from its inventory. Client-authoritative over
        // its own inventory; the host assigns a fresh net id and adds it to stp_items, which
        // the Phase 1 relay propagates so everyone spawns the same pickup (with the Phase 2 gate).
        "stp_drop" => {
            let drop_id = action.data.get("drop_id").and_then(|v| v.as_u64()).unwrap_or(0);
            let def_id = action.data.get("def_id").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
            let count = action
                .data
                .get("count")
                .and_then(|v| v.as_u64())
                .unwrap_or(1)
                .max(1) as u16;
            let position: [f32; 3] = serde_json::from_value(
                action.data.get("position").cloned().unwrap_or(serde_json::Value::Null),
            )
            .unwrap_or([0.0, 0.0, 0.0]);
            let rotation = action.data.get("rotation").and_then(|v| v.as_f64()).unwrap_or(0.0) as f32;
            if net.is_host {
                process_stp_drop(drop_id, def_id, count, position, rotation, net);
            } else {
                let payload = crate::network::protocol::PacketPayload::StpDropRequest {
                    drop_id,
                    def_id,
                    count,
                    position,
                    rotation,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase B1: a client placed an STP building piece. The host assigns a fresh net id
        // and adds it to stp_buildings, which the Phase B1 relay propagates so everyone
        // spawns the same piece (StpBuildingReplicator). A joiner forwards to the host.
        "stp_place" => {
            let place_id = action.data.get("place_id").and_then(|v| v.as_u64()).unwrap_or(0);
            let def_id = action.data.get("def_id").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
            let position: [f32; 3] = serde_json::from_value(
                action.data.get("position").cloned().unwrap_or(serde_json::Value::Null),
            )
            .unwrap_or([0.0, 0.0, 0.0]);
            let rotation = action.data.get("rotation").and_then(|v| v.as_f64()).unwrap_or(0.0) as f32;
            if net.is_host {
                process_stp_place(place_id, def_id, position, rotation, net);
            } else {
                let payload = crate::network::protocol::PacketPayload::StpPlaceRequest {
                    place_id,
                    def_id,
                    position,
                    rotation,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase B2: a client added one unit of build material to a piece. The host advances
        // the piece's authoritative progress (added[material]) and the relay propagates it so
        // every client derives the same construction state. A joiner forwards to the host.
        "stp_build_add" => {
            let add_id = action.data.get("add_id").and_then(|v| v.as_u64()).unwrap_or(0);
            let building_id = action.data.get("building_id").and_then(|v| v.as_u64()).unwrap_or(0) as u32;
            let material_id = action.data.get("material_id").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
            if building_id == 0 {
                return;
            }
            if net.is_host {
                process_stp_build_add(add_id, building_id, material_id, net);
            } else {
                let payload = crate::network::protocol::PacketPayload::StpBuildAddRequest {
                    add_id,
                    building_id,
                    material_id,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase B2.5: the host Unity registers the authoritative carryable list (host-only;
        // a joiner sending this is ignored so it can't diverge the world).
        "set_stp_carryables" => {
            if !net.is_host {
                return;
            }
            let carryables: Vec<crate::network::protocol::StpCarryableInfo> = action
                .data
                .get("carryables")
                .cloned()
                .and_then(|v| serde_json::from_value(v).ok())
                .unwrap_or_default();
            info!(
                "MPTRACE step=CY event=host_set_stp_carryables self_id={} count={}",
                net.local_id,
                carryables.len()
            );
            net.stp_carryables = carryables;
        }
        // Phase B2.5: a client asks to pick up a world carryable. Host-authoritative: the
        // host removes it (vanishes for all via the relay) and grants it to the requester,
        // who carries it in hand. A joiner forwards the request to the host.
        "stp_carryable_pickup" => {
            let carryable_id = json_u32(&action.data, "carryable_id").unwrap_or(0);
            if carryable_id == 0 {
                return;
            }
            if net.is_host {
                process_stp_carryable_pickup(carryable_id, net.local_id, net, to_clients).await;
            } else {
                let payload = crate::network::protocol::PacketPayload::StpCarryablePickupRequest {
                    carryable_id,
                    requester_id: net.local_id,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase B2.5: a client dropped a carryable in the world. The host assigns a fresh id
        // and adds it to stp_carryables, which the relay propagates so everyone spawns it.
        "stp_carryable_drop" => {
            let drop_id = action.data.get("drop_id").and_then(|v| v.as_u64()).unwrap_or(0);
            let def_id = action.data.get("def_id").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
            let position: [f32; 3] = serde_json::from_value(
                action.data.get("position").cloned().unwrap_or(serde_json::Value::Null),
            )
            .unwrap_or([0.0, 0.0, 0.0]);
            let rotation = action.data.get("rotation").and_then(|v| v.as_f64()).unwrap_or(0.0) as f32;
            if net.is_host {
                process_stp_carryable_drop(drop_id, def_id, position, rotation, net);
            } else {
                let payload = crate::network::protocol::PacketPayload::StpCarryableDropRequest {
                    drop_id,
                    def_id,
                    position,
                    rotation,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase B2.6: the host Unity registers the authoritative scene-harvestable list
        // (host-only; remaining starts full). A joiner sending this is ignored.
        "set_stp_harvestables" => {
            if !net.is_host {
                return;
            }
            #[derive(serde::Deserialize)]
            struct HarvestableSpec {
                id: u32,
                position: [f32; 3],
            }
            let specs: Vec<HarvestableSpec> = action
                .data
                .get("harvestables")
                .cloned()
                .and_then(|v| serde_json::from_value(v).ok())
                .unwrap_or_default();
            net.stp_harvestables = specs
                .into_iter()
                .map(|s| crate::network::protocol::StpHarvestableInfo {
                    id: s.id,
                    position: s.position,
                    remaining: 1.0,
                })
                .collect();
            info!(
                "MPTRACE step=HV event=host_set_stp_harvestables self_id={} count={}",
                net.local_id,
                net.stp_harvestables.len()
            );
        }
        // Phase B2.6: a client reports a harvest hit. Host-authoritative: the host reduces the
        // harvestable's `remaining` and the relay propagates it. A joiner forwards to the host.
        "stp_harvest_hit" => {
            let hit_id = action.data.get("hit_id").and_then(|v| v.as_u64()).unwrap_or(0);
            let harvestable_id = action.data.get("harvestable_id").and_then(|v| v.as_u64()).unwrap_or(0) as u32;
            let amount = action.data.get("amount").and_then(|v| v.as_f64()).unwrap_or(0.0) as f32;
            if harvestable_id == 0 {
                return;
            }
            if net.is_host {
                process_stp_harvest_hit(hit_id, harvestable_id, amount, net);
            } else {
                let payload = crate::network::protocol::PacketPayload::StpHarvestHitRequest {
                    hit_id,
                    harvestable_id,
                    amount,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        _ => {}
    }
}

/// Monotonic id source for host-spawned dropped STP items. Starts high so it never
/// collides with the low, host-Unity-assigned ids of the Phase 1 spawn ring.
static NEXT_STP_DROP_ID: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(0x4000_0000);

fn next_stp_drop_id() -> u32 {
    NEXT_STP_DROP_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

/// Phase 3: host adds a dropped STP item to the authoritative `stp_items` list. The 10 Hz
/// relay (broadcast_stp_items) propagates it to all peers, where StpItemReplicator spawns it.
/// Deduped by the client-generated `drop_id`: a repeated request (watcher race OR reliable
/// retransmit when the host is slow) is ignored, so one logical drop spawns exactly one item.
fn process_stp_drop(
    drop_id: u64,
    def_id: i32,
    count: u16,
    position: [f32; 3],
    rotation: f32,
    net: &mut NetworkManager,
) {
    if drop_id != 0 && !net.processed_stp_drops.insert(drop_id) {
        info!(
            "MPTRACE step=SD event=stp_drop_duplicate drop_id={} ignored=true",
            drop_id
        );
        return;
    }

    let id = next_stp_drop_id();
    net.stp_items.push(crate::network::protocol::StpItemInfo {
        id,
        def_id,
        count,
        position,
        rotation,
    });
    info!(
        "MPTRACE step=SD event=stp_drop_spawned id={} drop_id={} def_id={} count={} pos=({:.2},{:.2},{:.2})",
        id, drop_id, def_id, count, position[0], position[1], position[2]
    );
}

/// Monotonic id source for host-spawned STP building pieces. Lives in its own high range
/// so building ids never collide with item ids (the two lists are independent, but a
/// distinct range keeps logs unambiguous).
static NEXT_STP_BUILDING_ID: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(0x6000_0000);

fn next_stp_building_id() -> u32 {
    NEXT_STP_BUILDING_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

/// Phase B1: host adds a placed STP building piece to the authoritative `stp_buildings`
/// list. The 10 Hz relay (broadcast_stp_buildings) propagates it to all peers, where
/// StpBuildingReplicator spawns it. Deduped by the client-generated `place_id`: a repeated
/// request (reliable retransmit when the host is slow) is ignored, so one logical placement
/// spawns exactly one piece.
fn process_stp_place(
    place_id: u64,
    def_id: i32,
    position: [f32; 3],
    rotation: f32,
    net: &mut NetworkManager,
) {
    if place_id != 0 && !net.processed_stp_places.insert(place_id) {
        info!(
            "MPTRACE step=BP event=stp_place_duplicate place_id={} ignored=true",
            place_id
        );
        return;
    }

    let id = next_stp_building_id();
    net.stp_buildings.push(crate::network::protocol::StpBuildingInfo {
        id,
        def_id,
        position,
        rotation,
        added: Vec::new(),
    });
    info!(
        "MPTRACE step=BP event=stp_place_spawned id={} place_id={} def_id={} pos=({:.2},{:.2},{:.2})",
        id, place_id, def_id, position[0], position[1], position[2]
    );
}

/// Phase B2: host advances the authoritative construction progress of a building piece.
/// Accumulates one unit of `material_id` into the piece's `added` list. Deduped by the
/// client-generated `add_id` (reliable retransmit safe). The 10 Hz relay propagates the
/// updated `stp_buildings`, where StpBuildingReplicator derives the per-client progress.
fn process_stp_build_add(
    add_id: u64,
    building_id: u32,
    material_id: i32,
    net: &mut NetworkManager,
) {
    if add_id != 0 && !net.processed_stp_build_adds.insert(add_id) {
        info!(
            "MPTRACE step=BM event=stp_build_add_duplicate add_id={} ignored=true",
            add_id
        );
        return;
    }

    let building = match net.stp_buildings.iter_mut().find(|b| b.id == building_id) {
        Some(b) => b,
        None => {
            info!(
                "MPTRACE step=BM event=stp_build_add_no_building building_id={} add_id={} ignored=true",
                building_id, add_id
            );
            return;
        }
    };

    match building.added.iter_mut().find(|p| p.material_id == material_id) {
        Some(p) => p.count = p.count.saturating_add(1),
        None => building
            .added
            .push(crate::network::protocol::StpBuildProgress { material_id, count: 1 }),
    }

    info!(
        "MPTRACE step=BM event=stp_build_add building_id={} material_id={} add_id={}",
        building_id, material_id, add_id
    );
}

/// Monotonic id source for host-spawned STP carryables. Lives in its own high range so
/// carryable ids never collide with item/building ids.
static NEXT_STP_CARRYABLE_ID: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(0x7000_0000);

fn next_stp_carryable_id() -> u32 {
    NEXT_STP_CARRYABLE_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

/// Phase B2.5: host adds a dropped carryable to the authoritative `stp_carryables` list.
/// The 10 Hz relay (broadcast_stp_carryables) propagates it to all peers, where
/// StpCarryableReplicator spawns it. Deduped by the client-generated `drop_id`.
fn process_stp_carryable_drop(
    drop_id: u64,
    def_id: i32,
    position: [f32; 3],
    rotation: f32,
    net: &mut NetworkManager,
) {
    if drop_id != 0 && !net.processed_stp_carryable_drops.insert(drop_id) {
        info!(
            "MPTRACE step=CY event=stp_carryable_drop_duplicate drop_id={} ignored=true",
            drop_id
        );
        return;
    }

    let id = next_stp_carryable_id();
    net.stp_carryables.push(crate::network::protocol::StpCarryableInfo {
        id,
        def_id,
        position,
        rotation,
    });
    info!(
        "MPTRACE step=CY event=stp_carryable_drop_spawned id={} drop_id={} def_id={} pos=({:.2},{:.2},{:.2})",
        id, drop_id, def_id, position[0], position[1], position[2]
    );
}

/// Phase B2.5: host-authoritative carryable pickup. If the carryable still exists, remove it
/// (which despawns it for everyone via the relay) and grant it to the requester — directly to
/// the host's own Unity, or reliably to a joiner. A second request for an already-taken
/// carryable finds nothing and is rejected (race-safe; the removal is the dedup).
async fn process_stp_carryable_pickup(
    carryable_id: u32,
    requester_id: crate::network::PeerId,
    net: &mut NetworkManager,
    to_clients: &broadcast::Sender<ServerMessage>,
) {
    let pos = match net.stp_carryables.iter().position(|c| c.id == carryable_id) {
        Some(p) => p,
        None => {
            info!(
                "MPTRACE step=CY event=stp_carryable_pickup_rejected carryable_id={} requester_id={} reason=not_found",
                carryable_id, requester_id
            );
            return;
        }
    };

    let carryable = net.stp_carryables.remove(pos);
    info!(
        "MPTRACE step=CY event=stp_carryable_pickup_granted carryable_id={} requester_id={} def_id={}",
        carryable_id, requester_id, carryable.def_id
    );

    if requester_id == net.local_id {
        let _ = to_clients.send(ServerMessage::Event(GameEvent {
            event_type: "stp_carryable_pickup_granted".into(),
            data: serde_json::json!({
                "carryable_id": carryable_id,
                "def_id": carryable.def_id,
            }),
        }));
    } else {
        let payload = crate::network::protocol::PacketPayload::StpCarryablePickupGranted {
            carryable_id,
            def_id: carryable.def_id,
        };
        net.send_reliable(requester_id, &payload).await;
    }
}

/// Phase B2.6: host reduces a scene harvestable's authoritative `remaining` by `amount`.
/// Deduped by the client-generated `hit_id` so a reliable retransmit (or two clients hitting
/// the same tree) never double-counts. The 10 Hz relay propagates the updated health, and the
/// host Unity spawns the resource carryables when it crosses to depleted (B2.6 → B2.5).
fn process_stp_harvest_hit(
    hit_id: u64,
    harvestable_id: u32,
    amount: f32,
    net: &mut NetworkManager,
) {
    if hit_id != 0 && !net.processed_stp_harvest_hits.insert(hit_id) {
        info!(
            "MPTRACE step=HV event=stp_harvest_hit_duplicate hit_id={} ignored=true",
            hit_id
        );
        return;
    }

    let harvestable = match net.stp_harvestables.iter_mut().find(|h| h.id == harvestable_id) {
        Some(h) => h,
        None => {
            info!(
                "MPTRACE step=HV event=stp_harvest_hit_no_target harvestable_id={} hit_id={} ignored=true",
                harvestable_id, hit_id
            );
            return;
        }
    };

    harvestable.remaining = (harvestable.remaining - amount.abs()).max(0.0);
    info!(
        "MPTRACE step=HV event=stp_harvest_hit harvestable_id={} amount={:.3} remaining={:.3} hit_id={}",
        harvestable_id, amount, harvestable.remaining, hit_id
    );
}

/// Phase 2: host-authoritative STP pickup. If the item still exists, remove it (which
/// despawns it for everyone via the stp_items relay) and grant it to the requester —
/// directly to the host's own Unity, or reliably to a joiner. A second request for an
/// already-taken item finds nothing and is rejected (race-safe; the removal is the dedup).
async fn process_stp_pickup(
    item_id: u32,
    requester_id: crate::network::PeerId,
    net: &mut NetworkManager,
    to_clients: &broadcast::Sender<ServerMessage>,
) {
    let pos = match net.stp_items.iter().position(|it| it.id == item_id) {
        Some(p) => p,
        None => {
            info!(
                "MPTRACE step=SP event=stp_pickup_rejected item_id={} requester_id={} reason=not_found",
                item_id, requester_id
            );
            return;
        }
    };

    let item = net.stp_items.remove(pos);
    info!(
        "MPTRACE step=SP event=stp_pickup_granted item_id={} requester_id={} def_id={} count={}",
        item_id, requester_id, item.def_id, item.count
    );

    if requester_id == net.local_id {
        let _ = to_clients.send(ServerMessage::Event(GameEvent {
            event_type: "stp_pickup_granted".into(),
            data: serde_json::json!({
                "item_id": item_id,
                "def_id": item.def_id,
                "count": item.count,
            }),
        }));
    } else {
        let payload = crate::network::protocol::PacketPayload::StpPickupGranted {
            item_id,
            def_id: item.def_id,
            count: item.count,
        };
        net.send_reliable(requester_id, &payload).await;
    }
}

async fn process_authoritative_interaction(
    requester_id: u16,
    request_id: u64,
    target_id: u32,
    target_kind: &str,
    interaction_type: &str,
    requester_pos: Vec3,
    world: &mut World,
    net: &mut NetworkManager,
    player: &Player,
    processed_interactions: &mut HashSet<(u16, u64)>,
) {
    if requester_id != net.local_id && !net.peers.contains_key(&requester_id) {
        info!(
            "MPTRACE step=AF event=host_validate_interaction result=rejected reason=unknown_requester target_id={} requester_id={} request_id={}",
            target_id,
            requester_id,
            request_id
        );
        return;
    }

    if !processed_interactions.insert((requester_id, request_id)) {
        info!(
            "MPTRACE step=AF event=host_validate_interaction result=rejected reason=duplicate_request target_id={} requester_id={} request_id={}",
            target_id,
            requester_id,
            request_id
        );
        return;
    }

    match interaction_type {
        "pickup" => {
            if target_kind != "item" {
                info!(
                    "MPTRACE step=AF event=host_validate_interaction result=rejected reason=unsupported kind={} type={} target_id={} requester_id={}",
                    target_kind,
                    interaction_type,
                    target_id,
                    requester_id
                );
                return;
            }

            match world.interact_with_item(target_id, requester_pos, 5.0) {
                Ok((item_type, quantity)) => {
                    info!(
                        "MPTRACE step=AF event=host_validate_interaction result=accepted reason=ok target_id={} requester_id={} item_type={} quantity={}",
                        target_id,
                        requester_id,
                        item_type,
                        quantity
                    );
                    info!(
                        "MPTRACE step=AG event=world_object_state_changed revision={} target_id={} active=false kind=item requester_id={}",
                        world.revision,
                        target_id,
                        requester_id
                    );
                    info!(
                        "MPTRACE step=AH event=worldsync_after_interaction revision={} items={} entities={}",
                        world.revision,
                        world.visible_item_views().len(),
                        world.visible_entity_views().len()
                    );
                    sync::broadcast_world_sync(net, world, player).await;
                }
                Err(reason) => {
                    info!(
                        "MPTRACE step=AF event=host_validate_interaction result=rejected reason={} target_id={} requester_id={}",
                        reason,
                        target_id,
                        requester_id
                    );
                }
            }
        }
        // Drop: the client carries the item type name in `target_kind`. The host spawns
        // the item into the world at the requester's position and propagates it via the
        // SAME broadcast_world_sync the pickup uses, so A, B and C all see it appear.
        // NOTE: inventory ownership is NOT validated here — the backend does not track
        // per-peer inventories yet (the client removes from its own UI). Deferred.
        "drop" => {
            let item = item_from_type_name(target_kind);
            match world.spawn_dropped_item(requester_pos, item, 1) {
                Some(item_id) => {
                    info!(
                        "MPTRACE step=AF event=host_validate_interaction result=accepted reason=drop requester_id={} item_type={} item_id={} pos=({:.2},{:.2},{:.2})",
                        requester_id,
                        target_kind,
                        item_id,
                        requester_pos.x,
                        requester_pos.y,
                        requester_pos.z
                    );
                    info!(
                        "MPTRACE step=AH event=worldsync_after_interaction revision={} items={} entities={}",
                        world.revision,
                        world.visible_item_views().len(),
                        world.visible_entity_views().len()
                    );
                    sync::broadcast_world_sync(net, world, player).await;
                }
                None => {
                    info!(
                        "MPTRACE step=AF event=host_validate_interaction result=rejected reason=drop_chunk_not_loaded requester_id={} pos=({:.2},{:.2},{:.2})",
                        requester_id,
                        requester_pos.x,
                        requester_pos.y,
                        requester_pos.z
                    );
                }
            }
        }
        _ => {
            info!(
                "MPTRACE step=AF event=host_validate_interaction result=rejected reason=unsupported kind={} type={} target_id={} requester_id={}",
                target_kind,
                interaction_type,
                target_id,
                requester_id
            );
        }
    }
}

/// Map the wire item-type name (shared with `Item::type_name`) to an `Item` for drops.
fn item_from_type_name(name: &str) -> crate::player::inventory::Item {
    use crate::player::inventory::Item;
    match name {
        "circuit" => Item::Circuit,
        "battery" => Item::Battery,
        "cable" => Item::Cable,
        "food" => Item::Food,
        "water" => Item::Water,
        "medicine" => Item::Medicine,
        "tool" => Item::Tool,
        _ => Item::Metal,
    }
}

fn json_u64(value: &serde_json::Value, key: &str) -> Option<u64> {
    value.get(key).and_then(|v| v.as_u64())
}

fn json_u32(value: &serde_json::Value, key: &str) -> Option<u32> {
    json_u64(value, key).and_then(|v| u32::try_from(v).ok())
}

fn json_str<'a>(value: &'a serde_json::Value, key: &str) -> Option<&'a str> {
    value.get(key).and_then(|v| v.as_str())
}

fn emit_stat_warnings(player: &Player, to_clients: &broadcast::Sender<ServerMessage>) {
    let warnings = [
        ("hunger", player.stats.hunger),
        ("thirst", player.stats.thirst),
        ("sanity", player.stats.sanity),
    ];
    for (stat, value) in warnings {
        if value < 20.0 {
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "stat_warning".into(),
                data: serde_json::json!({ "stat": stat, "value": value }),
            }));
        }
    }
}

fn build_world_state(
    tick: u64,
    player: &Player,
    world: &mut World,
    net: &NetworkManager,
    ack_input_seq: u32,
) -> WorldState {
    let remote_players: Vec<RemotePlayerState> = net
        .peers
        .values()
        .map(|p| RemotePlayerState {
            id: p.id,
            name: p.name.clone(),
            position: p.position,
            rotation: p.rotation,
            animation: p.animation.clone(),
        })
        .collect();

    if tick % 30 == 0 {
        let remote_ids: Vec<u16> = remote_players.iter().map(|p| p.id).collect();
        info!(
            "WorldState remote_players={} ids={:?}",
            remote_players.len(),
            remote_ids
        );
        info!(
            "MPTRACE step=H event=build_world_state self_id={} sender_id=<none> assigned_id=<none> peer_id=<none> endpoint=<none> peer_count={} remote_players_count={} remote_players_ids={:?}",
            net.local_id,
            net.peer_count(),
            remote_players.len(),
            remote_ids
        );
        for remote in &remote_players {
            info!(
                "MPTRACE step=T event=worldstate_remote_transform self_id={} remote_id={} pos=({:.2},{:.2},{:.2}) rot={:.2}",
                net.local_id,
                remote.id,
                remote.position[0],
                remote.position[1],
                remote.position[2],
                remote.rotation
            );
        }
    }

    WorldState {
        tick,
        world_seed: world.seed,
        world_revision: world.revision,
        local_player: LocalPlayerState {
            position: player.position.to_array(),
            rotation: player.rotation,
            stats: StatsView {
                health: player.stats.health,
                hunger: player.stats.hunger,
                thirst: player.stats.thirst,
                sanity: player.stats.sanity,
                stamina: player.stats.stamina,
            },
            speed_modifier: player.stats.speed_modifier,
            inventory_changed: false,
            ack_input_seq,
        },
        remote_players,
        visible_chunks: world.visible_chunk_views(),
        visible_entities: world.visible_entity_views(),
        visible_items: world.visible_item_views(),
        vertical_debug_markers: world.vertical_debug_marker_views(),
        stp_items: net.stp_items.clone(),
        stp_buildings: net.stp_buildings.clone(),
        stp_carryables: net.stp_carryables.clone(),
        stp_harvestables: net.stp_harvestables.clone(),
    }
}
