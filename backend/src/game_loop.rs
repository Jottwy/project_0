//! Authoritative game loop. Runs at 60hz, processes IPC input from Unity,
//! simulates local state (world, entities, stats), manages P2P networking,
//! and streams `WorldState` back at 10hz. See ARCHITECTURE_V1.md §6.1.

use std::collections::{HashMap, HashSet};
use std::time::{Duration, Instant};

use log::{debug, info};
use tokio::sync::{broadcast, mpsc};
use tokio::time::{interval, MissedTickBehavior};

use crate::ipc::{
    ClientMessage, GameEvent, GridChunkData, LocalPlayerState, MovementDelta, PlayerAction,
    PlayerInput, RemotePlayerState, ServerMessage, StatsView, WorldState,
};
use crate::network::sync;
use crate::network::{NetworkEvent, NetworkManager, PeerId};
use crate::player::Player;
use crate::utils::{world_to_chunk, ChunkPos, Vec3, CHUNK_SIZE};
use crate::world::collision::{resolve_safe_spawn, Level0Collision};
use crate::world::grid_gen::{resolve_move_grid_gen, world_pos_to_layer, GridGenChunkCache};
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
/// ADR-014: delay between granting a pickup and removing the item from `stp_items` (the
/// "juicy frame" of the gesture). Host-only. COUPLED to PickupSpeed (x2 in
/// ProxyAnimatorControllerBuilder) → if PickupSpeed changes, re-tune this by hand.
const PICKUP_REMOVE_DELAY: Duration = Duration::from_millis(600);

/// Forces the god-traversal COLLISION BYPASS on (the player's claimed pose is trusted without
/// clamping against the BACKEND world, which doesn't match the rendered ChunkStreamer — the known
/// world-migration debt). Kept `true` on purpose until that migration so movement isn't
/// rubber-banded against phantom walls. NOTE (ADR-016 slice 1): this no longer gates death —
/// that is a SEPARATE flag (`DEV_INVINCIBLE`), so the robapieles can kill the host while the
/// collision bypass stays on.
const DEV_GOD_TRAVERSAL_HARDCODED: bool = true;

/// ADR-016 slice 2: phantom walk speed (m/s). Calibrated to human walking so the client's
/// velocity-derived locomotion (ADR-013) reads it as walking, not teleporting — the proxy
/// teleport guards only trip on chunk-displacement-scale jumps, far above this. Calibrable.
const PHANTOM_WALK_SPEED: f32 = 3.0;
/// ADR-016 slice 2: on a head-on block the phantom re-orients by a random turn in
/// [90°, 270°] — never straight back into the wall, never a no-op — so it never stalls and
/// wanders the maze (no smart pathing yet).
const PHANTOM_TURN_MIN: f32 = std::f32::consts::FRAC_PI_2;
const PHANTOM_TURN_MAX: f32 = std::f32::consts::PI * 1.5;
/// Initial heading (yaw, radians) of a freshly driven phantom: +X (east of the host spawn).
const PHANTOM_INITIAL_HEADING: f32 = std::f32::consts::FRAC_PI_2;
/// ADR-016 slice 4: how long the phantom holds the FAKED "pickup" animation. Aligned with
/// ADR-011's ~1s trigger-flank window (the real gesture's duration is client-owned, via the
/// Animator exitTime). Movement is paused for this window so it reads like a player that is
/// input-locked while picking up. PURE presentation — no real pickup state is ever touched.
const PHANTOM_PICKUP_GESTURE: Duration = Duration::from_millis(1000);
/// ADR-016 slice 4: cooldown between faked pickups while patrolling (theater — no item needed).
/// Calibrable; small enough to see it recur in play-test.
const PHANTOM_PICKUP_INTERVAL: Duration = Duration::from_secs(6);
/// ADR-016 (tell phase): behavioral tell #2 — the phantom periodically goes UNNATURALLY STILL
/// (a "stare"): it stops dead, holding idle + a fixed facing for `PHANTOM_STARE_DURATION`, on a
/// near-perfect `PHANTOM_STARE_INTERVAL` cadence. Subtle (rare, brief) but learnable — humans
/// don't freeze like a metronome. Calibrable: raise the interval / lower the duration to make it
/// subtler. PURELY BEHAVIORAL — no wire flag; observable only by watching, never by reading packets.
const PHANTOM_STARE_INTERVAL: Duration = Duration::from_secs(30);
const PHANTOM_STARE_DURATION: Duration = Duration::from_millis(2500);
/// ADR-016 — OBSERVATION LEASH (TEMPORARY, play-test only): keep the phantom within this radius
/// (m) of its spawn so it stays in view to observe the cloned name, the stare, and two same-named
/// players. If it drifts past this, its heading is re-aimed at the spawn instead of wandering off.
/// NOT the final behavior: the robapieles must roam freely — free wander returns once the
/// world→backend migration fixes collision (today it phases through visibly-rendered walls).
/// Calibrable.
const PHANTOM_WANDER_RADIUS: f32 = 9.0;

// ADR-016 slice 2 — detection + chase. D1=(a): distance + a forward view cone ONLY, with NO
// geometry line-of-sight. The phantom's collision is against the BACKEND world, not the
// rendered ChunkStreamer, so a geometric raycast would test the WRONG walls; until the
// world→backend migration, distance+angle is the honest prototype. Enter chase within
// DETECT_RADIUS while the target is inside the cone; leave past LOSE_RADIUS (hysteresis so it
// doesn't flicker at the edge). Chase moves faster than the wander walk.
const PHANTOM_DETECT_RADIUS: f32 = 15.0;
const PHANTOM_LOSE_RADIUS: f32 = 25.0;
const PHANTOM_DETECT_HALF_FOV: f32 = std::f32::consts::FRAC_PI_3; // 60° half → 120° cone

// ADR-016 slice 3a — Stalker FSM (Wander / Spotted / Stalk / Sprint). Peek/Search land in 3b.
const PHANTOM_STALK_DISTANCE: f32 = 9.0; // STALK keeps roughly this gap from the player
const PHANTOM_SPRINT_SPEED: f32 = 9.0; // top sprint speed (vs walk 3.0)
const PHANTOM_SPRINT_RAMP: f32 = 1.5; // seconds to ramp WALK → SPRINT
const PHANTOM_SPOTTED_MIN: f32 = 3.0; // SPOTTED stare duration range (s)
const PHANTOM_SPOTTED_MAX: f32 = 8.0;
const PHANTOM_STALK_PATIENCE: f32 = 25.0; // seconds stalking before it lunges
const PHANTOM_WANDER_PAUSE_MIN: f32 = 3.0; // WANDER "looking at a wall" pause range (s)
const PHANTOM_WANDER_PAUSE_MAX: f32 = 12.0;
const PHANTOM_WANDER_PAUSE_CHANCE: f32 = 0.007; // per-tick (≈20% over 3 s at 10 Hz)
const PHANTOM_SPRINT_RANDOM_CHANCE: f32 = 0.008; // per-tick unpredictable lunge

// ADR-016 slice 3b-P1 — STATUE (weeping-angel: freezes while observed) + sound detection.
// All inputs come from data the backend already has (player yaw; target speed derived from the
// position delta — peers send no velocity/move_state). NO wire/IPC change.
const PHANTOM_STATUE_RANGE: f32 = 20.0; // only freezes if the watching player is within this (m)
const PHANTOM_STATUE_LOOK_HALF_FOV: f32 = std::f32::consts::FRAC_PI_6; // 30° half → 60° player cone
const PHANTOM_STATUE_MAX: f32 = 6.0; // max seconds frozen → then it lunges (SPRINT)
const PHANTOM_RUN_SPEED_THRESHOLD: f32 = 4.5; // target speed (m/s) read as "running" (above walk)
const PHANTOM_SOUND_BONUS: f32 = 8.0; // extra detect radius (m) when the player is running
const PHANTOM_SPEED_SANITY_MAX: f32 = 30.0; // ignore deltas above this (teleport/chunk-displace)
const PHANTOM_SPOTTED_SOUND_MIN: f32 = 1.0; // shorter stare when alerted by noise (s)
const PHANTOM_SPOTTED_SOUND_MAX: f32 = 2.0;
// Fluidity (slice 3b-P1 follow-up): ease `heading` toward the player instead of snapping at
// 10 Hz (which reads as lag). rad/s — STALK tracks, SPRINT tracks hard, STATUE turns its head.
const PHANTOM_TURN_SPEED_STALK: f32 = 8.0;
const PHANTOM_TURN_SPEED_SPRINT: f32 = 15.0;
const PHANTOM_TURN_SPEED_STATUE: f32 = 3.0;
// ADR-016 slice 1 (phantom damage) — host-only (joiners = Fase 7 debt). Damage flows through the
// PhantomAttack channel, NEVER the pickup path (ADR-016 invariant).
const PHANTOM_ATTACK_DAMAGE: f32 = 35.0; // frontal SPRINT hit (non-lethal; bounces to STALK)
const PHANTOM_KNOCKBACK_RANGE: f32 = 3.0; // STATUE→SPRINT shove only within this (m)
const PHANTOM_KNOCKBACK_FORCE: f32 = 3.0; // shove speed (m/s); client applies via SetVelocity

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
    // ADR-016 slice 1: death/respawn is now SEPARATE from the collision bypass. God traversal
    // keeps collision off (world-migration debt) while the player can still die (so the phantom
    // can kill). Set DEV_INVINCIBLE to disable death/respawn for debugging. Default OFF.
    let dev_invincible = env_flag_enabled("DEV_INVINCIBLE");
    // ADR-016: debug-only spawn of the phantom (the robapieles). OFF unless DEBUG_SPAWN_PHANTOM
    // is set in the env — a normal build never auto-spawns one (no leftover scaffolding). Kept as
    // the explicit spawn trigger for the identity/tell phases and future play-tests.
    let debug_spawn_phantom = env_flag_enabled("DEBUG_SPAWN_PHANTOM");

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
    let mut processed_interactions: HashSet<(u16, u64)> = HashSet::with_capacity(256);
    // Track the last chunk position used for ownership so we only call
    // update_ownership when the player crosses a chunk boundary, not every tick.
    let mut last_ownership_chunk: Option<ChunkPos> = None;
    // ADR-016 slice 2: host-only driver that walks phantom peers (the robapieles) each
    // entity tick, resolving collision via ADR-017's sim-only chunk cache.
    let mut phantom_driver = PhantomDriver::new(net.world_seed);

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

        // ADR-016 (debug-gated): inject one phantom near the host spawn so it appears as a
        // player (host + joiners, via the ADR-015 relay). It walks (slice 2, collision via
        // ADR-017), fakes pickups (slice 4) and impersonates a victim's NAME (identity phase).
        // The victim is a real connected peer (none at startup → host-name fallback, upgraded by
        // rebind_unbound_victims once a joiner connects). Spawns only when DEBUG_SPAWN_PHANTOM is set.
        if debug_spawn_phantom {
            let phantom_pos = [player.position.x + 3.0, player.position.y, player.position.z];
            let (victim_name, victim_bound) = choose_victim_name(&net);
            let phantom_id = net.spawn_phantom(&victim_name, phantom_pos);
            phantom_driver.add(
                phantom_id,
                PHANTOM_INITIAL_HEADING,
                Vec3::from_array(phantom_pos),
                victim_bound,
            );
        }
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
            "DEV_GOD_TRAVERSAL active: player collision resolution is bypassed (death/respawn now gated separately — see DEV_INVINCIBLE)"
        );
    }
    if dev_invincible {
        info!("DEV_INVINCIBLE active: survival death/respawn is disabled");
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
                ClientMessage::RequestChunk { cx, cz, layer } => {
                    // Fase 4.1: grid_gen is the world source of truth. Generate the
                    // requested chunk (with seam stitching) and reply with the 5 m
                    // tile-wall bitmask. net.world_seed is the shared canonical seed
                    // (env WORLD_SEED, propagated via handshake) — identical on every
                    // peer, so the derived chunk is byte-identical across the session.
                    let walls = crate::world::grid_gen::chunk_tile_walls(net.world_seed, cx, cz, layer);
                    // Broadcast: in this P2P model each player runs its own backend with a
                    // single Unity client, so the only subscriber IS the requester.
                    let _ = to_clients.send(ServerMessage::ChunkData(GridChunkData {
                        cx,
                        cz,
                        layer,
                        walls,
                    }));
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

        // ADR-016 (identity phase): once a real peer is connected, an unbound phantom adopts
        // that peer's NAME (cloning the victim; keeps its own unique id). Host-only; a cheap
        // no-op once every phantom is bound (or when there is no phantom).
        if net.is_host {
            phantom_driver.rebind_unbound_victims(&mut net);
        }

        // A joiner places its local player only once it has connected and the
        // host's world has arrived — never on the empty/pre-sync world, and
        // never at the unsafe chunk-corner origin. ADR-016: real_peer_count so an
        // injected phantom never satisfies this gate (no-op on a joiner, where the
        // phantom is unmarked, but correct on any backend that injects one).
        if !spawn_resolved && net.real_peer_count() > 0 && !world.chunks.is_empty() {
            let res = resolve_safe_spawn(&mut world, preferred_spawn());
            player.position = res.position;
            spawn_resolved = true;
        }

        // ADR-014: drain reserved pickups whose juicy-frame delay elapsed → remove the item now
        // (despawns for all via the stp_items diff at 10Hz). Tolerant: if the item already left
        // stp_items by another path, just drop the (now-orphan) reservation. Host-only in practice
        // (joiners never reserve), so the empty-map check makes it a no-op there.
        if !net.pending_pickups.is_empty() {
            let now = std::time::Instant::now();
            let due: Vec<u32> = net
                .pending_pickups
                .iter()
                .filter(|(_, (_, remove_at))| *remove_at <= now)
                .map(|(item_id, _)| *item_id)
                .collect();
            for item_id in due {
                net.pending_pickups.remove(&item_id);
                if let Some(pos) = net.stp_items.iter().position(|it| it.id == item_id) {
                    net.stp_items.remove(pos);
                }
            }
        }

        // ─── PHASE 2: SIMULATE ───
        // Only apply once a real input has arrived — the default PlayerInput has
        // position [0,0,0], which would otherwise drag the player to the origin
        // before the client's first packet. Track the accepted seq for the ack.
        if has_received_input {
            // ADR-020: record the client-reported crouch (cosmetic; relayed to peers, not validated).
            player.crouch = received_input.crouch;
            // ADR-021: record the client-reported camera pitch, quantized to 1° (cosmetic;
            // relayed to peers, not validated). yaw is consumed as `look[1]` in apply_movement.
            player.pitch = quantize_pitch(received_input.look[0]);
            // ADR-022: record the client-reported worn clothing IDs (cosmetic; relayed to peers,
            // not validated). Read by the client from its inventory equipment slots.
            player.equipment = received_input.equipment;
            // ADR-023: record the client-reported held item ID (cosmetic; relayed to peers,
            // not validated). Read by the client from its wieldable holster slot.
            player.held_item = received_input.held_item;
            let seq = apply_movement(&mut player, &received_input, dt, &world, tick, dev_god_traversal);
            last_accepted_input_seq = seq;
            authoritative_velocity = Vec3::from_array(received_input.velocity);
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

            // ADR-016 slice 2: advance phantom peers (host-only). Each phantom walks and its
            // move is resolved via ADR-017 sim-only collision, so it respects walls/floor even
            // far from the host (where world.chunks is empty). Same 10 Hz as the pose relay.
            if net.is_host {
                let attack =
                    phantom_driver.step(&mut net, entity_dt, player.position, player.rotation);
                // ADR-016 slice 1 — apply the phantom's attack to the HOST player. DEUDA: host
                // only; a joiner's health lives in its own backend (Fase 7). The damage path is
                // SEPARATE from the pickup theater (ADR-016 invariant intact).
                match attack {
                    PhantomAttack::Kill => {
                        let death_pos = player.position.to_array();
                        if !dev_invincible {
                            player.stats.take_damage(100.0); // → is_dead → existing death/respawn
                        }
                        let _ = to_clients.send(ServerMessage::Event(GameEvent {
                            event_type: "phantom_kill".into(),
                            data: serde_json::json!({ "pos": death_pos }),
                        }));
                    }
                    PhantomAttack::Hit(dmg) => {
                        if !dev_invincible {
                            player.stats.take_damage(dmg);
                        }
                        let _ = to_clients.send(ServerMessage::Event(GameEvent {
                            event_type: "phantom_hit".into(),
                            data: serde_json::json!({ "damage": dmg }),
                        }));
                    }
                    PhantomAttack::Knockback(dx, dz) => {
                        // Client-only: it applies the impulse (SetVelocity). Mutating
                        // player.position here would be overwritten by the next client-authoritative
                        // input (ADR-009), so the backend only signals the shove.
                        let _ = to_clients.send(ServerMessage::Event(GameEvent {
                            event_type: "phantom_knockback".into(),
                            data: serde_json::json!({ "dx": dx, "dz": dz }),
                        }));
                    }
                    PhantomAttack::None => {}
                }
            }
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

        // Stats with real context from the world. ADR-016: real_peer_count so a phantom
        // doesn't inflate the host's sanity context (it still renders in the roster).
        let ctx = world.stat_context_for(player.position, net.real_peer_count() as u32);
        if dev_freeze_survival {
            player.stats.speed_modifier = 1.0;
            player.stats.accuracy_modifier = 1.0;
            player.stats.hallucination_intensity = 0.0;
        } else {
            player.stats.update(dt, &ctx);
        }

        // Death → respawn on a validated safe cell (never the unsafe origin).
        // DEV_INVINCIBLE (not god-traversal anymore): survival death and the resulting respawn
        // are skipped (debug only). This is the path a phantom Kill triggers via take_damage(100).
        if !dev_invincible && player.stats.is_dead() {
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
                // ADR-015: relay each peer's full pose (rotation+animation, not just the
                // position the roster carries) so joiners see other joiners gesture/face
                // correctly. Host-only; no-op below two peers.
                sync::broadcast_peer_poses(&net).await;
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

            // ADR-016: keep injected phantoms alive — refresh their heartbeat before the
            // timeout scan so check_timeouts never reaps them (they get no real packets).
            // Runs at 1s, well under the 5s HEARTBEAT_TIMEOUT. No-op without phantoms.
            net.refresh_phantom_heartbeats();

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
            crouch,
            pitch,
            equipment,
            held_item,
        } => {
            debug!(
                "Remote player received: id={}, pos=({:.2}, {:.2}, {:.2}), rot={:.1}, anim={}, crouch={}, pitch={}, equipment={:?}, held_item={}",
                id, position[0], position[1], position[2], rotation, animation, crouch, pitch, equipment, held_item
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
            // ADR-011 follow-up: dedup retransmitted reliable grants. StpPickupGranted is reliable
            // and may arrive multiple times; without this each copy re-stamps last_pickup_at →
            // a duplicated "pickup" window on observers. Same form as processed_stp_drops/places/etc.
            if item_id != 0 && !net.processed_stp_pickup_grants.insert(item_id) {
                info!(
                    "MPTRACE step=SP event=stp_pickup_grant_duplicate item_id={} ignored=true",
                    item_id
                );
                return;
            }
            // ADR-011: our own local player picked up (joiner path: host granted) → stamp window.
            net.last_pickup_at = Some(std::time::Instant::now());
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
            group_id,
            is_group,
        } => {
            if net.is_host {
                process_stp_place(place_id, def_id, position, rotation, group_id, is_group, net);
                // Phase B3: relay immediately so the placer's replicated copy (with its group)
                // arrives within ~RTT, closing the round-trip gap when chaining pieces.
                sync::broadcast_stp_buildings(net).await;
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
) -> u32 {
    player.rotation = input.look[1].rem_euclid(360.0); // yaw is INPUT (ADR-009 §8)
    apply_client_authoritative_move(player, input, dt, world, tick, god_traversal)
}

/// ADR-021: clamp the client-reported camera pitch to [−90, 90]° and quantize to 1°
/// (i8). Cosmetic/host-relay; the 1° step is the wire resolution for the peer broadcast.
fn quantize_pitch(deg: f32) -> i8 {
    deg.clamp(-90.0, 90.0).round() as i8
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
) -> u32 {
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
        return input.input_seq;
    }

    // Collision: verify the claimed position doesn't intersect static geometry.
    // resolve_move slides/clamps against the level; the resolved point is the
    // authoritative pose echoed back to the client.
    let resolved = Level0Collision::resolve_move(world, player.position, claimed);
    player.position = resolved.position;
    input.input_seq
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
            if request_id == 0 {
                info!(
                    "MPTRACE step=AF event=host_validate_interaction result=rejected reason=invalid_request_id target_id={} requester_id={}",
                    target_id,
                    net.local_id
                );
                return;
            }
            if interaction_type != "drop" && target_id == 0 {
                info!(
                    "MPTRACE step=AF event=host_validate_interaction result=rejected reason=invalid_target_id target_id={} requester_id={}",
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
            // ADR-014 invariant: drop reservations for items no longer present, so pending_pickups
            // never points at a vanished item.
            let present: std::collections::HashSet<u32> =
                net.stp_items.iter().map(|it| it.id).collect();
            net.pending_pickups.retain(|item_id, _| present.contains(item_id));
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
            let group_id = action.data.get("group_id").and_then(|v| v.as_u64()).unwrap_or(0) as u32;
            let is_group = action.data.get("is_group").and_then(|v| v.as_bool()).unwrap_or(false);
            if net.is_host {
                process_stp_place(place_id, def_id, position, rotation, group_id, is_group, net);
                // Phase B3: relay immediately so the placer's replicated copy (with its group)
                // arrives within ~RTT, closing the round-trip gap when chaining pieces.
                sync::broadcast_stp_buildings(net).await;
            } else {
                let payload = crate::network::protocol::PacketPayload::StpPlaceRequest {
                    place_id,
                    def_id,
                    position,
                    rotation,
                    group_id,
                    is_group,
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

/// Phase B3: monotonic id source for host-minted STP building GROUPS. Starts at 1 so
/// `group_id == 0` always means "standalone / no group".
static NEXT_STP_GROUP_ID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(1);

fn next_stp_group_id() -> u32 {
    NEXT_STP_GROUP_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

/// Phase B3: quantize a placed pose to a dedup cell. Two clients targeting the same socket
/// instantiate the same prefab on the same authoritative anchor and apply the same authored
/// offset, so they compute an identical snapped pose → identical cell. Distinct sockets are
/// far apart (≫ Q_POS), so there are no false collisions.
fn stp_pose_cell(position: [f32; 3], rotation: f32) -> (i32, i32, i32, i32) {
    const Q_POS: f32 = 0.25; // meters
    const Q_YAW: f32 = 1.0; // degrees
    (
        (position[0] / Q_POS).round() as i32,
        (position[1] / Q_POS).round() as i32,
        (position[2] / Q_POS).round() as i32,
        (rotation / Q_YAW).round() as i32,
    )
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
    req_group_id: u32,
    is_group: bool,
    net: &mut NetworkManager,
) {
    if place_id != 0 && !net.processed_stp_places.insert(place_id) {
        info!(
            "MPTRACE step=BP event=stp_place_duplicate place_id={} ignored=true",
            place_id
        );
        return;
    }

    // Phase B3: concurrency dedup by quantized pose-cell — only for group pieces (free
    // pieces may legitimately stack). The first placement at a cell wins; a second one
    // targeting the same socket (distinct place_id) is rejected, so no duplicate / no
    // doubly-occupied socket.
    if is_group {
        let cell = stp_pose_cell(position, rotation);
        if !net.occupied_stp_cells.insert(cell) {
            info!(
                "MPTRACE step=BP event=stp_place_cell_taken place_id={} cell=({},{},{},{}) rejected=true",
                place_id, cell.0, cell.1, cell.2, cell.3
            );
            return;
        }
    }

    // Phase B3: resolve the group. Free piece → 0; existing group → echo it; new group
    // (group_id == 0 with is_group) → mint a fresh host-authoritative id.
    let group_id = if !is_group {
        0
    } else if req_group_id != 0 {
        req_group_id
    } else {
        next_stp_group_id()
    };

    let id = next_stp_building_id();
    net.stp_buildings.push(crate::network::protocol::StpBuildingInfo {
        id,
        def_id,
        position,
        rotation,
        group_id,
        added: Vec::new(),
    });
    info!(
        "MPTRACE step=BP event=stp_place_spawned id={} place_id={} def_id={} group_id={} pos=({:.2},{:.2},{:.2})",
        id, place_id, def_id, group_id, position[0], position[1], position[2]
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
    // ADR-014: the reservation is the dedup now that the removal is deferred. A request for an
    // item already being channeled (reserved) is rejected just like a not-found one.
    if net.pending_pickups.contains_key(&item_id) {
        info!(
            "MPTRACE step=SP event=stp_pickup_rejected item_id={} requester_id={} reason=reserved",
            item_id, requester_id
        );
        return;
    }

    // The item must still exist. We do NOT remove it here (ADR-014 deferred removal): copy the
    // fields the grant needs, then reserve it; the per-tick drain removes it at remove_at.
    let (def_id, count) = match net.stp_items.iter().find(|it| it.id == item_id) {
        Some(it) => (it.def_id, it.count),
        None => {
            info!(
                "MPTRACE step=SP event=stp_pickup_rejected item_id={} requester_id={} reason=not_found",
                item_id, requester_id
            );
            return;
        }
    };

    info!(
        "MPTRACE step=SP event=stp_pickup_granted item_id={} requester_id={} def_id={} count={}",
        item_id, requester_id, def_id, count
    );

    // ADR-014: reserve — keep the item visible in stp_items until remove_at; concurrent requests
    // are rejected by the contains_key check above. The drain removes it at the juicy frame.
    net.pending_pickups.insert(
        item_id,
        (requester_id, std::time::Instant::now() + PICKUP_REMOVE_DELAY),
    );

    if requester_id == net.local_id {
        // ADR-011: our own local player picked up (host path) → stamp the pickup-anim window.
        net.last_pickup_at = Some(std::time::Instant::now());
        let _ = to_clients.send(ServerMessage::Event(GameEvent {
            event_type: "stp_pickup_granted".into(),
            data: serde_json::json!({
                "item_id": item_id,
                "def_id": def_id,
                "count": count,
            }),
        }));
    } else {
        let payload = crate::network::protocol::PacketPayload::StpPickupGranted {
            item_id,
            def_id,
            count,
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
    let mut remote_players = Vec::with_capacity(net.peer_count());
    for p in net.peers.values() {
        remote_players.push(RemotePlayerState {
            id: p.id,
            name: p.name.clone(),
            position: p.position,
            rotation: p.rotation,
            animation: p.animation.clone(),
            crouch: p.crouch,
            pitch: p.pitch,
            equipment: p.equipment,
            held_item: p.held_item,
        });
    }

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

// ─── ADR-016: phantom driver — movement (2) + faked pickup (4) + victim identity + tell ───

/// ADR-016 (identity phase): pick the name the phantom (robapieles) impersonates. The victim is
/// the first REAL (non-phantom) connected peer; its name is cloned (the phantom keeps its OWN
/// unique id — the id mismatch is the intended subtle tell #1). Returns `(name, bound)`: `bound`
/// is true when a real victim was found, false → host-name fallback (solo), which
/// `rebind_unbound_victims` later upgrades to a real peer once one connects.
fn choose_victim_name(net: &NetworkManager) -> (String, bool) {
    match net.peers.values().find(|p| !net.phantom_ids.contains(&p.id)) {
        Some(p) => (p.name.clone(), true),
        None => (net.local_name.clone(), false),
    }
}

/// ADR-016 — nearest REAL target (non-phantom) to `from` in XZ: the host's own local player
/// (`host_player_pos`/`host_player_rot`, keyed by `net.local_id`) plus every real peer. Returns
/// `(id, position, distance, yaw_deg)` of the closest. The `id` lets the caller look up the
/// target's derived speed (sound detection, slice 3b-P1) and the `yaw` lets it test whether the
/// player is looking back (STATUE). The host player is always a candidate → `Some` in practice.
fn nearest_real_target(
    net: &NetworkManager,
    host_player_pos: Vec3,
    host_player_rot: f32,
    from: Vec3,
) -> Option<(PeerId, Vec3, f32, f32)> {
    let mut best = Some((
        net.local_id,
        host_player_pos,
        from.distance_xz(host_player_pos),
        host_player_rot,
    ));
    for p in net.peers.values() {
        if net.phantom_ids.contains(&p.id) {
            continue;
        }
        let pos = Vec3::from_array(p.position);
        let d = from.distance_xz(pos);
        if best.map_or(true, |(_, _, bd, _)| d < bd) {
            best = Some((p.id, pos, d, p.rotation));
        }
    }
    best
}

/// ADR-016 slice 3b-P1 (STATUE): is the PHANTOM inside the player's forward HORIZONTAL cone —
/// i.e. is the player looking at it? `player_yaw` is degrees (Unity yaw, 0 = +Z). Pitch is not
/// available per-peer (and is discarded for the host), so this is the horizontal cone only:
/// looking up/down does not count. No geometry occlusion (consistent with D1=(a)).
fn player_is_looking_at(player_pos: Vec3, player_yaw: f32, phantom_pos: Vec3) -> bool {
    let dx = phantom_pos.x - player_pos.x;
    let dz = phantom_pos.z - player_pos.z;
    let len = (dx * dx + dz * dz).sqrt();
    if len < f32::EPSILON {
        return true; // on top of each other → counts as looked-at
    }
    let yaw = player_yaw.to_radians();
    // Player forward unit dir (Unity yaw): (sin, cos). dot with the unit to-phantom vector.
    let dot = (yaw.sin() * dx + yaw.cos() * dz) / len;
    dot >= PHANTOM_STATUE_LOOK_HALF_FOV.cos()
}

/// ADR-016 (fluidity): angularly ease `current` heading toward `target` (both yaw radians) by
/// factor `t` in [0,1], via normalize-lerp of the unit direction vectors (nlerp — naturally takes
/// the shorter arc, no angle-wrap special-casing). Returns the blended yaw. Smooths the phantom's
/// turn-to-face so it tracks the player without a 10 Hz snap.
fn lerp_heading(current: f32, target: f32, t: f32) -> f32 {
    let t = t.clamp(0.0, 1.0);
    // dir = (sin(yaw), cos(yaw)) — the Unity yaw convention used throughout the driver.
    let mx = current.sin() * (1.0 - t) + target.sin() * t;
    let mz = current.cos() * (1.0 - t) + target.cos() * t;
    let len = (mx * mx + mz * mz).sqrt();
    if len > 0.001 {
        mx.atan2(mz).rem_euclid(std::f32::consts::TAU)
    } else {
        current // ~diametrically opposite at t≈0.5 → keep current; next tick resolves it
    }
}

/// ADR-016 slice 2: is `target` inside the phantom's forward view cone (heading ± HALF_FOV)?
/// Distance is checked by the caller; this is angle ONLY, with no geometry occlusion (D1=(a)).
fn in_view_cone(heading: f32, from: Vec3, target: Vec3) -> bool {
    let tx = target.x - from.x;
    let tz = target.z - from.z;
    let len = (tx * tx + tz * tz).sqrt();
    if len < f32::EPSILON {
        return true; // target is on top of the phantom → counts as seen
    }
    // Heading unit dir: yaw 0 = +Z, so dir = (sin, _, cos). dot with the unit to-target vector.
    let dot = (heading.sin() * tx + heading.cos() * tz) / len;
    dot >= PHANTOM_DETECT_HALF_FOV.cos()
}

/// ADR-016 slice 3a — the robapieles' behavioral FSM. Drives how it relates to the nearest real
/// player. PEEK/SEARCH (corner-peeking, last-known-position hunting) arrive in slice 3b; until
/// then their would-be transitions fall back to `Wander`. PURELY BEHAVIORAL — no wire flag; the
/// state is observable only by watching, never by reading packets.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum PhantomState {
    /// Erratic patrol: walks its heading, pauses to "look at walls", fakes pickups, does the
    /// metronomic stare tell, and stays near spawn via the observation leash.
    Wander,
    /// Saw the player: freezes and stares straight at them for a randomized window, then STALK.
    Spotted,
    /// Shadows the player at a held distance; its patience runs out into a SPRINT.
    Stalk,
    /// Weeping-angel freeze (slice 3b-P1): the player is looking at it, so it goes dead still
    /// until they look away (→ STALK) or it tires of the game (→ SPRINT). Entered from STALK;
    /// never interrupts a committed SPRINT.
    Statue,
    /// Lunges straight at the player with a ramped speed; "attacks" (anim-only) at point blank.
    Sprint,
}

/// ADR-016 slice 1 (phantom damage) — what `PhantomDriver::step` produced this tick for the HOST
/// player. Returned to the game loop, which owns `player`/`stats`. DEUDA: host-only — a joiner's
/// health lives in its own backend (P2P multi-backend), so this never affects joiners
/// (cross-backend damage authority is Fase 7). The damage path is SEPARATE from the pickup
/// theater (ADR-016 invariant intact).
#[derive(Clone, Copy, PartialEq, Debug)]
enum PhantomAttack {
    /// Nothing happened this tick.
    None,
    /// Frontal point-blank hit: non-lethal `damage` to health; the phantom bounces back to STALK.
    Hit(f32),
    /// Point-blank from BEHIND: lethal (the loop applies 100 dmg → the existing death/respawn).
    Kill,
    /// A shove the CLIENT applies (dx, dz m/s via the motor's SetVelocity). The backend never
    /// mutates the player pose for this — it's client-authoritative and would be overwritten by
    /// the next input (ADR-009), so the backend only signals the direction/force.
    Knockback(f32, f32),
}

/// Host-only driver for phantom peers (the robapieles). Each phantom wanders: it steps along
/// its heading and, on a head-on block, turns to a new heading — so it bumps walls and turns
/// instead of clipping through them. Every step is resolved through `resolve_move_grid_gen` +
/// `GridGenChunkCache`, so the phantom collides against the SAME grid_gen world Unity renders
/// (not world::generator), generated on-demand even far from the host. Periodically the phantom
/// also FAKES a pickup (slice 4): it freezes and
/// flips its animation field to "pickup" for ~1s, then resumes. It does a behavioral TELL #2
/// (an unnatural, near-metronomic "stare"), and clones a real peer's NAME (victim identity;
/// keeps its own id). Slice 2: it also DETECTS the nearest real player (distance + forward cone,
/// no geometry LOS — D1=(a)) and CHASES them at CHASE_SPEED, dropping the wander/stare/pickup
/// theater until it loses them past LOSE_RADIUS.
///
/// SAFETY INVARIANT (ADR-016 slice 4): the faked pickup is PURE THEATER — it touches ONLY the
/// phantom's `animation` field (via `update_player_state`). It NEVER calls `process_stp_pickup`,
/// NEVER inserts into `pending_pickups`, NEVER touches `stp_items`, NEVER credits an inventory,
/// NEVER emits a `stp_pickup_granted`. The phantom has no effect on real game state beyond its
/// own presentation field. (Calling the real pickup path would delete/reserve a real world item.)
///
/// The `GridGenChunkCache` lives here (a host-only game-loop local), not in World/NetworkManager:
/// it is sim-only host state, the least invasive home. The phantom POSE itself lives in its
/// `PeerConnection` (the render source). (world::collision's `SimChunkCache` is unchanged but no
/// longer has a production consumer; it remains for tests / future world::generator entities.)
struct PhantomDriver {
    grid_cache: GridGenChunkCache,
    movers: Vec<PhantomMover>,
    /// Last-tick XZ position of each real target (host + peers), keyed by id. Used to derive each
    /// target's speed (sound detection, slice 3b-P1) — peers never send velocity/move_state, so a
    /// position delta is the only uniform "is it running?" signal. Rebuilt each tick from the
    /// current targets, so disconnected ids drop out automatically.
    prev_target_pos: HashMap<PeerId, Vec3>,
}

/// Per-phantom state: which peer, its heading (yaw, radians), the faked-pickup gesture (slice 4),
/// the "stare" tell (tell phase), and the victim-name binding (identity phase).
struct PhantomMover {
    id: PeerId,
    heading: f32,
    /// Spawn position; the observation leash (PHANTOM_WANDER_RADIUS) re-aims the heading here
    /// when the phantom drifts too far, so it stays in view during play-test.
    spawn_pos: Vec3,
    /// `Some(t)` while faking a pickup: movement is paused and `animation` is held at
    /// "pickup" until `t`. `None` while walking. PURE presentation — never real pickup state.
    pickup_until: Option<Instant>,
    /// Earliest instant the next faked pickup may begin.
    next_pickup_at: Instant,
    /// `Some(t)` while doing the "stare" tell: frozen, unnaturally still ("idle") until `t`.
    /// `None` while walking. The tell is its near-metronomic regularity.
    stare_until: Option<Instant>,
    /// Earliest instant the next stare tell may begin.
    next_stare_at: Instant,
    /// `true` once the name was bound to a real victim (so it is not rebound). `false` = still
    /// on the host-name fallback; adopts a real peer's name when one connects.
    victim_bound: bool,
    /// ADR-016 slice 3a — current FSM state. Replaces the slice-2 `chasing` bool.
    state: PhantomState,
    /// Seconds spent in the current `state` (reset to 0 on every transition). Drives the SPOTTED
    /// stare length, the STALK patience, and the SPRINT speed ramp.
    state_timer: f32,
    /// Randomized SPOTTED stare length (s), drawn in [SPOTTED_MIN, SPOTTED_MAX] on entry.
    spotted_duration: f32,
    /// Last position the player was seen at while STALK/SPRINT — recorded now so slice 3b's
    /// SEARCH/PEEK need no further step() rework.
    last_known_player_pos: Option<Vec3>,
    /// WANDER organic pause: remaining time (s) it stands still "looking at a wall". `is_paused`
    /// gates it.
    wander_pause_timer: f32,
    is_paused: bool,
    /// Smoothed turn target (yaw radians): STALK/SPRINT/STATUE ease `heading` toward this instead
    /// of snapping each tick (fluidity), so it tracks the player without 10 Hz rotational jerk.
    heading_target: f32,
}

impl PhantomDriver {
    fn new(world_seed: u64) -> Self {
        Self {
            grid_cache: GridGenChunkCache::new(world_seed),
            movers: Vec::new(),
            prev_target_pos: HashMap::new(),
        }
    }

    fn add(&mut self, id: PeerId, heading: f32, spawn_pos: Vec3, victim_bound: bool) {
        let now = Instant::now();
        self.movers.push(PhantomMover {
            id,
            heading,
            spawn_pos,
            pickup_until: None,
            next_pickup_at: now + PHANTOM_PICKUP_INTERVAL,
            stare_until: None,
            next_stare_at: now + PHANTOM_STARE_INTERVAL,
            victim_bound,
            state: PhantomState::Wander,
            state_timer: 0.0,
            spotted_duration: 0.0,
            last_known_player_pos: None,
            wander_pause_timer: 0.0,
            is_paused: false,
            heading_target: heading,
        });
    }

    /// ADR-016 (identity phase): once a real (non-phantom) peer is connected, any phantom still
    /// on its fallback name adopts that peer's NAME — cloning the victim's identity while keeping
    /// its OWN unique id (never the victim's id, which would collide the client's `_active[id]`).
    /// The rename rides the existing roster/PeerList + ADR-015 relay (no schema). One-shot per
    /// phantom; cheap no-op once all are bound or no real peer exists.
    fn rebind_unbound_victims(&mut self, net: &mut NetworkManager) {
        if self.movers.iter().all(|m| m.victim_bound) {
            return;
        }
        let victim_name = net
            .peers
            .values()
            .find(|p| !net.phantom_ids.contains(&p.id))
            .map(|p| p.name.clone());
        let Some(victim_name) = victim_name else {
            return;
        };
        for m in self.movers.iter_mut().filter(|m| !m.victim_bound) {
            if let Some(peer) = net.peers.get_mut(&m.id) {
                peer.name = victim_name.clone();
            }
            m.victim_bound = true;
            info!(
                "MPTRACE step=PH5 event=phantom_victim_bound phantom_id={} victim_name={}",
                m.id, victim_name
            );
        }
    }

    /// Advance every phantom one step at `dt` (entity-tick delta). Reads the phantom's
    /// current pose from its PeerConnection, resolves a walk-step through sim-only collision,
    /// and writes the resolved pose (grounded Y from the resolver + facing) back. A fully
    /// blocked step re-orients the heading so the phantom never stalls at a wall.
    fn step(
        &mut self,
        net: &mut NetworkManager,
        dt: f32,
        host_player_pos: Vec3,
        host_player_rot: f32,
    ) -> PhantomAttack {
        let now = Instant::now();

        // ADR-016 slice 3b-P1 — derive each real target's XZ speed from its last-tick position
        // (peers send no velocity/move_state, so this is the uniform "is it running?" signal for
        // sound detection). Teleport / chunk-displacement deltas are clamped out. Computed once
        // per tick; the map is rebuilt from the current targets so stale ids drop out.
        let mut cur_positions: Vec<(PeerId, Vec3)> = Vec::with_capacity(net.peers.len() + 1);
        cur_positions.push((net.local_id, host_player_pos));
        for p in net.peers.values() {
            if !net.phantom_ids.contains(&p.id) {
                cur_positions.push((p.id, Vec3::from_array(p.position)));
            }
        }
        let mut target_speeds: HashMap<PeerId, f32> = HashMap::with_capacity(cur_positions.len());
        for (tid, cur) in &cur_positions {
            let speed = match self.prev_target_pos.get(tid) {
                Some(prev) => {
                    let d = ((cur.x - prev.x).powi(2) + (cur.z - prev.z).powi(2)).sqrt() / dt;
                    if d > PHANTOM_SPEED_SANITY_MAX {
                        0.0 // teleport / chunk displacement — not a footstep
                    } else {
                        d
                    }
                }
                None => 0.0,
            };
            target_speeds.insert(*tid, speed);
        }
        self.prev_target_pos = cur_positions.into_iter().collect();

        // ADR-016 slice 1 — the attack produced this tick; the game loop applies it to the host.
        // One phantom today; with several, the last attacker this tick wins (host-only debt).
        let mut attack = PhantomAttack::None;
        for i in 0..self.movers.len() {
            let id = self.movers[i].id;
            let from = match net.peers.get(&id) {
                Some(p) => Vec3::from_array(p.position),
                None => continue, // phantom no longer present
            };
            // The grid_gen layer to collide against, derived from the phantom's own Y (works on
            // every layer without a hardcoded layer).
            let current_layer = world_pos_to_layer(from.y);

            // Nearest REAL player (the host's own local player + any real peer). D1=(a): distance
            // + a forward view cone only, NO geometry line-of-sight (collision = grid_gen world).
            let target = nearest_real_target(net, host_player_pos, host_player_rot, from);
            self.movers[i].state_timer += dt;

            // ── Gesture freeze (ANY state): the faked-pickup imitation and the SPRINT "attack"
            // are PURE THEATER — only the `animation` field. While active, freeze in place holding
            // "pickup" (the trigger flank the proxy edge-detects, ADR-011). NOTHING real is
            // touched: no process_stp_pickup, no pending_pickups, no stp_items, no grant. ──
            if self.movers[i].pickup_until.map_or(false, |until| now >= until) {
                self.movers[i].pickup_until = None;
            }
            if self.movers[i].pickup_until.is_some() {
                let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                if let Some(peer) = net.peers.get_mut(&id) {
                    peer.update_player_state(from.to_array(), yaw, "pickup".into());
                }
                continue;
            }

            // ADR-016 slice 3a — Stalker FSM. `state` is Copy, so the match holds no borrow.
            let state = self.movers[i].state;
            match state {
                // ── WANDER: erratic patrol + detection. Keeps the slice-4 fake-pickup imitation,
                // the metronomic stare tell, organic "look at a wall" pauses, and the play-test
                // observation leash. Detecting the player (radius + cone) → SPOTTED. ──
                PhantomState::Wander => {
                    // Detection: normally distance + forward cone. A RUNNING target (speed from
                    // delta) is HEARD — detected from farther (DETECT + SOUND_BONUS) AND from any
                    // direction (no cone). Sound-only detection reacts faster (a shorter stare).
                    let (detected, by_sound) = match target {
                        Some((tid, tpos, dist, _)) => {
                            let normal = dist <= PHANTOM_DETECT_RADIUS
                                && in_view_cone(self.movers[i].heading, from, tpos);
                            let running = target_speeds.get(&tid).copied().unwrap_or(0.0)
                                > PHANTOM_RUN_SPEED_THRESHOLD;
                            let sound =
                                running && dist <= PHANTOM_DETECT_RADIUS + PHANTOM_SOUND_BONUS;
                            (normal || sound, sound && !normal)
                        }
                        None => (false, false),
                    };
                    if detected {
                        self.movers[i].state = PhantomState::Spotted;
                        self.movers[i].state_timer = 0.0;
                        let (lo, hi) = if by_sound {
                            (PHANTOM_SPOTTED_SOUND_MIN, PHANTOM_SPOTTED_SOUND_MAX)
                        } else {
                            (PHANTOM_SPOTTED_MIN, PHANTOM_SPOTTED_MAX)
                        };
                        self.movers[i].spotted_duration = lo + rand::random::<f32>() * (hi - lo);
                        self.movers[i].is_paused = false;
                        info!(
                            "MPTRACE step=PH_SPOTTED event=phantom_spotted phantom_id={} dur={:.1} by_sound={}",
                            id, self.movers[i].spotted_duration, by_sound
                        );
                        continue;
                    }

                    // Slice 4: start a faked-pickup gesture when the cooldown elapsed. Stamp the
                    // "pickup" flank now and freeze; the top-of-loop gesture freeze holds the pose
                    // for the rest of the window. (Anim-only — ADR-016 invariant.)
                    if now >= self.movers[i].next_pickup_at {
                        self.movers[i].pickup_until = Some(now + PHANTOM_PICKUP_GESTURE);
                        self.movers[i].next_pickup_at = now + PHANTOM_PICKUP_INTERVAL;
                        info!(
                            "MPTRACE step=PH4 event=phantom_fake_pickup phantom_id={} note=animation_field_only_no_real_pickup",
                            id
                        );
                        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                        if let Some(peer) = net.peers.get_mut(&id) {
                            peer.update_player_state(from.to_array(), yaw, "pickup".into());
                        }
                        continue;
                    }

                    // Tell #2: metronomic unnatural stillness (the tell is its regularity).
                    if self.movers[i].stare_until.map_or(false, |until| now >= until) {
                        self.movers[i].stare_until = None;
                    }
                    if self.movers[i].stare_until.is_none()
                        && now >= self.movers[i].next_stare_at
                    {
                        self.movers[i].stare_until = Some(now + PHANTOM_STARE_DURATION);
                        self.movers[i].next_stare_at = now + PHANTOM_STARE_INTERVAL;
                        info!(
                            "MPTRACE step=PH6 event=phantom_tell_stare phantom_id={} note=behavioral_tell_unnatural_stillness",
                            id
                        );
                    }
                    if self.movers[i].stare_until.is_some() {
                        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                        if let Some(peer) = net.peers.get_mut(&id) {
                            peer.update_player_state(from.to_array(), yaw, "idle".into());
                        }
                        continue;
                    }

                    // Organic pause: stand still "looking at a wall". Count down if paused; else a
                    // small per-tick chance to start one. On resume, turn to a new heading.
                    if self.movers[i].is_paused {
                        self.movers[i].wander_pause_timer -= dt;
                        if self.movers[i].wander_pause_timer <= 0.0 {
                            self.movers[i].is_paused = false;
                            let turn = PHANTOM_TURN_MIN
                                + rand::random::<f32>() * (PHANTOM_TURN_MAX - PHANTOM_TURN_MIN);
                            self.movers[i].heading =
                                (self.movers[i].heading + turn).rem_euclid(std::f32::consts::TAU);
                        } else {
                            let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                            if let Some(peer) = net.peers.get_mut(&id) {
                                peer.update_player_state(from.to_array(), yaw, "idle".into());
                            }
                            continue;
                        }
                    } else if rand::random::<f32>() < PHANTOM_WANDER_PAUSE_CHANCE {
                        self.movers[i].is_paused = true;
                        self.movers[i].wander_pause_timer = PHANTOM_WANDER_PAUSE_MIN
                            + rand::random::<f32>()
                                * (PHANTOM_WANDER_PAUSE_MAX - PHANTOM_WANDER_PAUSE_MIN);
                        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                        if let Some(peer) = net.peers.get_mut(&id) {
                            peer.update_player_state(from.to_array(), yaw, "idle".into());
                        }
                        continue;
                    }

                    // Observation leash (WANDER-only, play-test crutch): re-aim toward spawn if it
                    // drifted past the radius so it stays in view. STALK/SPRINT ignore it.
                    if from.distance_xz(self.movers[i].spawn_pos) > PHANTOM_WANDER_RADIUS {
                        let dx = self.movers[i].spawn_pos.x - from.x;
                        let dz = self.movers[i].spawn_pos.z - from.z;
                        if dx * dx + dz * dz > f32::EPSILON {
                            self.movers[i].heading = dx.atan2(dz).rem_euclid(std::f32::consts::TAU);
                        }
                    }

                    // Walk the heading; a full block (neither axis advanced) re-orients so it
                    // never stalls at a wall. A slide keeps the heading and hugs the wall.
                    let heading = self.movers[i].heading;
                    let dir = Vec3::new(heading.sin(), 0.0, heading.cos());
                    let desired = Vec3::new(
                        from.x + dir.x * PHANTOM_WALK_SPEED * dt,
                        from.y,
                        from.z + dir.z * PHANTOM_WALK_SPEED * dt,
                    );
                    let resolved =
                        resolve_move_grid_gen(&mut self.grid_cache, current_layer, from, desired);
                    let blocked =
                        (resolved.x - from.x).abs() < 1e-4 && (resolved.z - from.z).abs() < 1e-4;
                    if blocked {
                        let turn = PHANTOM_TURN_MIN
                            + rand::random::<f32>() * (PHANTOM_TURN_MAX - PHANTOM_TURN_MIN);
                        self.movers[i].heading = (heading + turn).rem_euclid(std::f32::consts::TAU);
                    }
                    let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                    if let Some(peer) = net.peers.get_mut(&id) {
                        peer.update_player_state(resolved.to_array(), yaw, "idle".into());
                    }
                    if net.session_start.elapsed().as_millis() % 1000 < 120 {
                        info!(
                            "MPTRACE step=PH2 event=phantom_move phantom_id={} pos=({:.2},{:.2},{:.2}) yaw={:.1} blocked={} grid_chunks={}",
                            id, resolved.x, resolved.y, resolved.z, yaw, blocked, self.grid_cache.len()
                        );
                    }
                }

                // ── SPOTTED: frozen stare. Faces the player; exits to STALK once the stare window
                // elapses (deterministic), may unpredictably SPRINT mid-stare; loses the player
                // (past DETECT_RADIUS*1.5) → WANDER. ──
                PhantomState::Spotted => {
                    let still = match target {
                        Some((_, _, dist, _)) => dist <= PHANTOM_DETECT_RADIUS * 1.5,
                        None => false,
                    };
                    if !still {
                        self.movers[i].state = PhantomState::Wander;
                        self.movers[i].state_timer = 0.0;
                        continue;
                    }
                    let (_, tpos, _dist, _) = target.unwrap(); // still ⇒ Some
                    let heading = (tpos.x - from.x)
                        .atan2(tpos.z - from.z)
                        .rem_euclid(std::f32::consts::TAU);
                    self.movers[i].heading = heading;

                    // Stare done → STALK (checked before the random lunge so it's deterministic
                    // once the window passes).
                    if self.movers[i].state_timer >= self.movers[i].spotted_duration {
                        self.movers[i].state = PhantomState::Stalk;
                        self.movers[i].state_timer = 0.0;
                        info!("MPTRACE step=PH_STALK event=phantom_stalk phantom_id={}", id);
                        continue;
                    }
                    // Unpredictable lunge mid-stare (scarier when imprevisible).
                    if rand::random::<f32>() < PHANTOM_SPRINT_RANDOM_CHANCE {
                        self.movers[i].state = PhantomState::Sprint;
                        self.movers[i].state_timer = 0.0;
                        info!(
                            "MPTRACE step=PH_SPRINT event=phantom_sprint phantom_id={} note=from_spotted_random",
                            id
                        );
                        continue;
                    }
                    let yaw = heading.to_degrees().rem_euclid(360.0);
                    if let Some(peer) = net.peers.get_mut(&id) {
                        peer.update_player_state(from.to_array(), yaw, "idle".into());
                    }
                }

                // ── STALK: shadow the player at a held gap. Patience (or an unpredictable roll) →
                // SPRINT; lost past LOSE_RADIUS → WANDER (slice 3b: SEARCH the last-known pos). ──
                PhantomState::Stalk => {
                    let dist_opt = target.map(|(_, _, d, _)| d);
                    if dist_opt.map_or(true, |d| d > PHANTOM_LOSE_RADIUS) {
                        // 3b will SEARCH `last_known_player_pos`; for 3a fall back to WANDER.
                        self.movers[i].state = PhantomState::Wander;
                        self.movers[i].state_timer = 0.0;
                        continue;
                    }
                    let (_, tpos, dist, tyaw) = target.unwrap();
                    self.movers[i].last_known_player_pos = Some(tpos);

                    // STATUE (weeping angel): the player is looking at it (horizontal cone) and is
                    // close → freeze. Entered only from STALK; a committed SPRINT is never frozen.
                    if dist < PHANTOM_STATUE_RANGE && player_is_looking_at(tpos, tyaw, from) {
                        self.movers[i].state = PhantomState::Statue;
                        self.movers[i].state_timer = 0.0;
                        info!(
                            "MPTRACE step=PH_STATUE event=phantom_statue phantom_id={} dist={:.2}",
                            id, dist
                        );
                        continue;
                    }

                    if self.movers[i].state_timer > PHANTOM_STALK_PATIENCE
                        || rand::random::<f32>() < PHANTOM_SPRINT_RANDOM_CHANCE * 2.0
                    {
                        self.movers[i].state = PhantomState::Sprint;
                        self.movers[i].state_timer = 0.0;
                        info!(
                            "MPTRACE step=PH_SPRINT event=phantom_sprint phantom_id={} dist={:.2}",
                            id, dist
                        );
                        continue;
                    }

                    let to_player = (tpos.x - from.x)
                        .atan2(tpos.z - from.z)
                        .rem_euclid(std::f32::consts::TAU);
                    // Ease toward the player instead of snapping (a 10 Hz snap reads as lag);
                    // movement follows the smoothed heading → a curved, less robotic track.
                    self.movers[i].heading_target = to_player;
                    let t = (PHANTOM_TURN_SPEED_STALK * dt).min(1.0);
                    self.movers[i].heading =
                        lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
                    let heading = self.movers[i].heading;
                    // Maintain STALK_DISTANCE: close in if too far, ease back if too near (it
                    // backs away while still facing you — unsettling), else hold.
                    let (move_dir, speed) = if dist > PHANTOM_STALK_DISTANCE + 2.0 {
                        (heading, PHANTOM_WALK_SPEED)
                    } else if dist < PHANTOM_STALK_DISTANCE {
                        (
                            (heading + std::f32::consts::PI).rem_euclid(std::f32::consts::TAU),
                            PHANTOM_WALK_SPEED * 0.6,
                        )
                    } else {
                        (heading, 0.0)
                    };
                    let dir = Vec3::new(move_dir.sin(), 0.0, move_dir.cos());
                    let desired = Vec3::new(
                        from.x + dir.x * speed * dt,
                        from.y,
                        from.z + dir.z * speed * dt,
                    );
                    let resolved =
                        resolve_move_grid_gen(&mut self.grid_cache, current_layer, from, desired);
                    let yaw = heading.to_degrees().rem_euclid(360.0);
                    if let Some(peer) = net.peers.get_mut(&id) {
                        peer.update_player_state(resolved.to_array(), yaw, "idle".into());
                    }
                    if net.session_start.elapsed().as_millis() % 1000 < 120 {
                        info!(
                            "MPTRACE step=PH_STALK event=phantom_stalk_move phantom_id={} pos=({:.2},{:.2},{:.2}) dist={:.2}",
                            id, resolved.x, resolved.y, resolved.z, dist
                        );
                    }
                }

                // ── STATUE: weeping-angel freeze. Dead still while the player keeps looking. They
                // look away → STALK (resumes the hunt, never back to WANDER); tires after
                // STATUE_MAX → SPRINT; loses the player past LOSE_RADIUS → WANDER. Never reached
                // mid-SPRINT (a committed lunge is not frozen). ──
                PhantomState::Statue => {
                    let lost = match target {
                        Some((_, _, dist, _)) => dist > PHANTOM_LOSE_RADIUS,
                        None => true,
                    };
                    if lost {
                        self.movers[i].state = PhantomState::Wander;
                        self.movers[i].state_timer = 0.0;
                        continue;
                    }
                    let (_, tpos, dist, tyaw) = target.unwrap();
                    self.movers[i].last_known_player_pos = Some(tpos);

                    // Tired of the game → lunge (checked before the look test so the timeout always
                    // wins once it elapses). If point-blank, also SHOVE the player — the client
                    // applies the impulse (SetVelocity); the backend only signals the direction.
                    if self.movers[i].state_timer >= PHANTOM_STATUE_MAX {
                        let dx = tpos.x - from.x;
                        let dz = tpos.z - from.z;
                        let len = (dx * dx + dz * dz).sqrt();
                        if dist < PHANTOM_KNOCKBACK_RANGE && len > 0.001 {
                            attack = PhantomAttack::Knockback(
                                dx / len * PHANTOM_KNOCKBACK_FORCE,
                                dz / len * PHANTOM_KNOCKBACK_FORCE,
                            );
                        }
                        self.movers[i].state = PhantomState::Sprint;
                        self.movers[i].state_timer = 0.0;
                        info!(
                            "MPTRACE step=PH_SPRINT event=phantom_sprint phantom_id={} note=from_statue_timeout knockback={}",
                            id,
                            dist < PHANTOM_KNOCKBACK_RANGE
                        );
                        continue;
                    }
                    // Player looked away → resume stalking.
                    if !player_is_looking_at(tpos, tyaw, from) {
                        self.movers[i].state = PhantomState::Stalk;
                        self.movers[i].state_timer = 0.0;
                        info!(
                            "MPTRACE step=PH_STALK event=phantom_statue_release phantom_id={}",
                            id
                        );
                        continue;
                    }
                    // Frozen in place, but it SLOWLY turns its head toward you (creepier than a
                    // fixed facing): position held, only the heading eases toward the player.
                    let to_player = (tpos.x - from.x)
                        .atan2(tpos.z - from.z)
                        .rem_euclid(std::f32::consts::TAU);
                    self.movers[i].heading_target = to_player;
                    let t = (PHANTOM_TURN_SPEED_STATUE * dt).min(1.0);
                    self.movers[i].heading =
                        lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
                    let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                    if let Some(peer) = net.peers.get_mut(&id) {
                        peer.update_player_state(from.to_array(), yaw, "idle".into());
                    }
                }

                // ── SPRINT: ramp WALK→SPRINT straight at the player. Point-blank → anim-only
                // "attack" (ADR-016 invariant) then STALK; lost past LOSE_RADIUS*1.2 → WANDER
                // (slice 3b: SEARCH — it doesn't give up easily mid-lunge). ──
                PhantomState::Sprint => {
                    let dist_opt = target.map(|(_, _, d, _)| d);
                    if dist_opt.map_or(true, |d| d > PHANTOM_LOSE_RADIUS * 1.2) {
                        self.movers[i].state = PhantomState::Wander;
                        self.movers[i].state_timer = 0.0;
                        continue;
                    }
                    let (_, tpos, dist, tyaw) = target.unwrap();
                    self.movers[i].last_known_player_pos = Some(tpos);
                    let to_player = (tpos.x - from.x)
                        .atan2(tpos.z - from.z)
                        .rem_euclid(std::f32::consts::TAU);
                    // Aggressive turn smoothing (faster than STALK) — tracks hard but never snaps.
                    self.movers[i].heading_target = to_player;
                    let t = (PHANTOM_TURN_SPEED_SPRINT * dt).min(1.0);
                    self.movers[i].heading =
                        lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
                    let heading = self.movers[i].heading;

                    // Point-blank "attack". The pickup gesture is the VISUAL only (ADR-016
                    // invariant — the DAMAGE rides the separate PhantomAttack channel, never the
                    // pickup path). Front (player looking) = non-lethal hit; behind = kill.
                    if dist < 1.5 {
                        self.movers[i].pickup_until = Some(now + PHANTOM_PICKUP_GESTURE);
                        self.movers[i].state = PhantomState::Stalk; // bounce off after the strike
                        self.movers[i].state_timer = 0.0;
                        if player_is_looking_at(tpos, tyaw, from) {
                            attack = PhantomAttack::Hit(PHANTOM_ATTACK_DAMAGE);
                            info!(
                                "MPTRACE step=PH_SPRINT event=phantom_hit phantom_id={} dmg={:.0}",
                                id, PHANTOM_ATTACK_DAMAGE
                            );
                        } else {
                            attack = PhantomAttack::Kill;
                            info!(
                                "MPTRACE step=PH_SPRINT event=phantom_kill phantom_id={} note=from_behind",
                                id
                            );
                        }
                        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                        if let Some(peer) = net.peers.get_mut(&id) {
                            peer.update_player_state(from.to_array(), yaw, "pickup".into());
                        }
                        continue;
                    }

                    let ramp = (self.movers[i].state_timer / PHANTOM_SPRINT_RAMP).clamp(0.0, 1.0);
                    let speed =
                        PHANTOM_WALK_SPEED + (PHANTOM_SPRINT_SPEED - PHANTOM_WALK_SPEED) * ramp;
                    let dir = Vec3::new(heading.sin(), 0.0, heading.cos());
                    let desired = Vec3::new(
                        from.x + dir.x * speed * dt,
                        from.y,
                        from.z + dir.z * speed * dt,
                    );
                    let resolved =
                        resolve_move_grid_gen(&mut self.grid_cache, current_layer, from, desired);
                    let yaw = heading.to_degrees().rem_euclid(360.0);
                    if let Some(peer) = net.peers.get_mut(&id) {
                        peer.update_player_state(resolved.to_array(), yaw, "idle".into());
                    }
                    if net.session_start.elapsed().as_millis() % 1000 < 120 {
                        info!(
                            "MPTRACE step=PH_SPRINT event=phantom_sprint_move phantom_id={} pos=({:.2},{:.2},{:.2}) speed={:.1} dist={:.2}",
                            id, resolved.x, resolved.y, resolved.z, speed, dist
                        );
                    }
                }
            }
        }
        attack
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn phantom_driver_walks_via_grid_cache_far_from_host() {
        // Far from the host: the phantom must resolve collision against grid_gen via the
        // on-demand GridGenChunkCache (the host player is parked very far so it never chases).
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [5000.0, 1.8, 5000.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42); // seed source only; the phantom no longer reads world.chunks
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);

        // 200 ticks (20 s sim): a WANDER pause can freeze it for up to 12 s, so this window
        // guarantees at least one walk step → the grid_gen cache is exercised deterministically.
        for _ in 0..200 {
            driver.step(&mut net, 0.1, Vec3::new(100_000.0, 1.8, 100_000.0), 0.0);
        }

        // The driver exercised the grid_gen cache (proves on-demand generation far from host).
        assert!(
            driver.grid_cache.len() > 0,
            "driver must generate grid_gen chunks far from the host"
        );
        // The phantom stayed grounded with a finite pose (never NaN, never an unloaded snap).
        let p = net.peers[&pid].position;
        assert!(
            p[0].is_finite() && p[1].is_finite() && p[2].is_finite(),
            "phantom pose must be finite"
        );
        assert!(p[1] > 0.0, "phantom must be grounded on a real floor, got y={}", p[1]);
    }

    #[tokio::test]
    async fn phantom_transitions_wander_to_spotted_in_radius() {
        // ADR-016 slice 3a: a real player within DETECT_RADIUS and inside the forward cone trips
        // WANDER → SPOTTED in a single step (detection is checked first in WANDER → deterministic
        // regardless of the sim collision at the origin).
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        // PHANTOM_INITIAL_HEADING faces +X (dir = (sin, _, cos) at FRAC_PI_2 = (1, 0, 0)).
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        let player = Vec3::new(6.0, 1.8, 0.0); // 6 m ahead (+X): inside radius and cone

        driver.step(&mut net, 0.1, player, 0.0);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Spotted,
            "a player in radius + cone must trip WANDER → SPOTTED"
        );
        // Entering SPOTTED arms a randomized stare window in [SPOTTED_MIN, SPOTTED_MAX].
        let dur = driver.movers[0].spotted_duration;
        assert!(
            (PHANTOM_SPOTTED_MIN..=PHANTOM_SPOTTED_MAX).contains(&dur),
            "spotted_duration must be seeded in range, got {dur}"
        );
    }

    #[tokio::test]
    async fn phantom_stays_wander_when_player_beyond_radius() {
        // A player well past DETECT_RADIUS → no detection (stays in WANDER).
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        let player = Vec3::new(100.0, 1.8, 0.0); // far beyond the detect/lose radius

        driver.step(&mut net, 0.1, player, 0.0);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Wander,
            "must not engage a player beyond the detect radius"
        );
    }

    #[tokio::test]
    async fn phantom_spotted_to_stalk_after_duration() {
        // ADR-016 slice 3a: once the SPOTTED stare window elapses, the phantom advances to STALK.
        // The duration check precedes the random lunge, so an elapsed window is deterministic.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        // Force a SPOTTED already past its (tiny) stare window, player still in range.
        driver.movers[0].state = PhantomState::Spotted;
        driver.movers[0].spotted_duration = 0.5;
        driver.movers[0].state_timer = 10.0;
        let player = Vec3::new(6.0, 1.8, 0.0); // inside DETECT_RADIUS*1.5

        driver.step(&mut net, 0.1, player, 0.0);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Stalk,
            "an elapsed SPOTTED stare must advance to STALK"
        );
    }

    #[tokio::test]
    async fn phantom_sprints_after_patience_exceeded() {
        // ADR-016 slice 3a: a phantom that has STALKed past PHANTOM_STALK_PATIENCE lunges into
        // SPRINT. The patience check precedes the random roll, so this is deterministic.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        // Force a STALK whose patience has already run out, player inside LOSE_RADIUS.
        driver.movers[0].state = PhantomState::Stalk;
        driver.movers[0].state_timer = PHANTOM_STALK_PATIENCE + 5.0;
        let player = Vec3::new(6.0, 1.8, 0.0);

        driver.step(&mut net, 0.1, player, 0.0);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Sprint,
            "patience exhausted while stalking must trigger SPRINT"
        );
    }

    #[tokio::test]
    async fn phantom_fake_pickup_touches_only_animation_not_real_state() {
        // SAFETY INVARIANT (ADR-016 slice 4): a faked pickup must flip ONLY the phantom's
        // animation field — never the real pickup state. Seed a real item to prove it survives.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        net.stp_items.push(crate::network::protocol::StpItemInfo {
            id: 7,
            def_id: 1,
            count: 1,
            position: [10.5, 1.8, 10.0],
            rotation: 0.0,
        });
        let pid = net.spawn_phantom("Robapieles_Test", [10.0, 1.8, 10.0]);
        let spawn_pos = net.peers[&pid].position; // actual (grid_gen-snapped) spawn position
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(spawn_pos), true);
        // Force the gesture to be due now (instead of after the cooldown).
        driver.movers[0].next_pickup_at = Instant::now();

        driver.step(&mut net, 0.1, Vec3::new(100_000.0, 1.8, 100_000.0), 0.0);

        // It IS faking the gesture: the presentation flank is "pickup"…
        assert_eq!(
            net.peers[&pid].animation, "pickup",
            "faked pickup must set the animation flank"
        );
        // …and the phantom stayed put during the gesture (movement paused).
        assert_eq!(net.peers[&pid].position, spawn_pos);
        // INVARIANT: nothing real changed — the item still exists, no reservation, no grant.
        assert_eq!(net.stp_items.len(), 1, "phantom must NOT remove real items");
        assert!(net.stp_items.iter().any(|it| it.id == 7));
        assert!(net.pending_pickups.is_empty(), "phantom must NOT reserve pickups");
        assert!(
            net.processed_stp_pickup_grants.is_empty(),
            "phantom must NOT process any pickup grant"
        );
    }

    #[tokio::test]
    async fn phantom_clones_victim_name_but_keeps_its_own_id() {
        // ADR-016 identity phase: the phantom impersonates a real peer's NAME but never its id.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();

        // No real peers yet → spawn falls back to the host name, unbound.
        let (name0, bound0) = choose_victim_name(&net);
        assert_eq!(name0, net.local_name, "solo fallback is the host name");
        assert!(!bound0, "fallback spawn must be unbound");
        let pid = net.spawn_phantom(&name0, [0.0, 1.8, 0.0]);
        let mut driver = PhantomDriver::new(42);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::new(0.0, 1.8, 0.0), bound0);

        // A real victim connects with a name.
        let victim_id = 2;
        let addr = "127.0.0.1:9999".parse().unwrap();
        net.peers.insert(
            victim_id,
            crate::network::peer::PeerConnection::new(victim_id, "Joel".into(), addr),
        );

        driver.rebind_unbound_victims(&mut net);

        // The phantom now wears the victim's NAME…
        assert_eq!(net.peers[&pid].name, "Joel", "phantom must clone the victim name");
        // …but keeps its OWN unique phantom id (never the victim's id — the subtle tell).
        assert_ne!(pid, victim_id);
        assert!(net.is_phantom(pid));
        assert!(!net.is_phantom(victim_id));
        // The real victim is untouched.
        assert_eq!(net.peers[&victim_id].name, "Joel");

        // Idempotent: a second rebind does not steal a new victim (bound stays put).
        driver.rebind_unbound_victims(&mut net);
        assert_eq!(net.peers[&pid].name, "Joel");
    }

    #[tokio::test]
    async fn phantom_statue_freezes_when_player_looks() {
        // ADR-016 slice 3b-P1: a STALKing phantom freezes (STATUE) when the player looks at it
        // (within range + horizontal cone). Deterministic — no rand on this path.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver.movers[0].state = PhantomState::Stalk;
        let player = Vec3::new(6.0, 1.8, 0.0); // close, inside STATUE_RANGE
        let player_yaw = 270.0; // faces -X, i.e. toward the phantom near the origin

        driver.step(&mut net, 0.1, player, player_yaw);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Statue,
            "a watched STALKer must freeze into STATUE"
        );
    }

    #[tokio::test]
    async fn phantom_statue_releases_to_stalk_when_player_looks_away() {
        // STATUE resumes STALK (not WANDER) the moment the player looks away.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver.movers[0].state = PhantomState::Statue;
        let player = Vec3::new(6.0, 1.8, 0.0); // close, inside LOSE_RADIUS
        let player_yaw = 90.0; // faces +X, AWAY from the phantom

        driver.step(&mut net, 0.1, player, player_yaw);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Stalk,
            "STATUE must release to STALK when the player looks away"
        );
    }

    #[tokio::test]
    async fn phantom_sound_detection_hears_running_player_outside_cone() {
        // ADR-016 slice 3b-P1: a RUNNING player beyond the normal cone/radius (but within
        // DETECT + SOUND_BONUS) is HEARD → SPOTTED with a short (sound) stare. Speed is derived
        // from the per-tick position delta, so we pre-seed last tick's position.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        // Heading +X; the player is BEHIND (-X) at ~18 m: outside the cone AND beyond
        // DETECT_RADIUS (15), but inside DETECT + SOUND_BONUS (23).
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        // Seed last-tick position 1 m back → 10 m/s this tick (> RUN_THRESHOLD, < sanity cap).
        driver.prev_target_pos.insert(net.local_id, Vec3::new(-19.0, 1.8, 0.0));
        let player = Vec3::new(-18.0, 1.8, 0.0);

        driver.step(&mut net, 0.1, player, 0.0);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Spotted,
            "a running player within sound range must be heard → SPOTTED"
        );
        assert!(
            driver.movers[0].spotted_duration <= PHANTOM_SPOTTED_SOUND_MAX,
            "sound-triggered stare must use the short window, got {}",
            driver.movers[0].spotted_duration
        );
    }

    #[test]
    fn lerp_heading_eases_toward_target_via_shorter_arc() {
        use std::f32::consts::{FRAC_PI_2, TAU};
        // t = 1 → exactly the target; t = 0 → unchanged.
        assert!((lerp_heading(0.0, FRAC_PI_2, 1.0) - FRAC_PI_2).abs() < 1e-3);
        assert!((lerp_heading(1.0, 2.0, 0.0) - 1.0).abs() < 1e-3);
        // A partial ease lands strictly between current and target.
        let mid = lerp_heading(0.0, FRAC_PI_2, 0.5);
        assert!(mid > 0.01 && mid < FRAC_PI_2 - 0.01, "partial ease, got {mid}");
        // Shorter arc: 350° → 10° must cross 0, not swing the long way through 180°.
        let h = lerp_heading(350f32.to_radians(), 10f32.to_radians(), 0.5);
        let dist_to_zero = h.min(TAU - h);
        assert!(dist_to_zero < 0.2, "must take the shorter arc through 0, got {h}");
    }

    #[tokio::test]
    async fn phantom_sprint_kills_from_behind() {
        // ADR-016 slice 1: a point-blank SPRINT while the player is NOT looking (phantom behind)
        // → lethal Kill. Deterministic.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver.movers[0].state = PhantomState::Sprint;
        // Place the player point-blank relative to the phantom's actual (snapped) spawn pos.
        let ppos = net.peers[&pid].position;
        let player = Vec3::new(ppos[0] + 1.0, 1.8, ppos[2]); // ~1 m east: point-blank
        let player_yaw = 90.0; // faces +X, AWAY from the phantom (to its west) → not looking

        let attack = driver.step(&mut net, 0.1, player, player_yaw);

        assert!(matches!(attack, PhantomAttack::Kill), "behind-attack must KILL, got {attack:?}");
    }

    #[tokio::test]
    async fn phantom_sprint_hits_from_front() {
        // Point-blank SPRINT while the player IS looking → non-lethal Hit, bounces to STALK.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver.movers[0].state = PhantomState::Sprint;
        let ppos = net.peers[&pid].position;
        let player = Vec3::new(ppos[0] + 1.0, 1.8, ppos[2]); // ~1 m east: point-blank
        let player_yaw = 270.0; // faces -X, TOWARD the phantom → looking

        let attack = driver.step(&mut net, 0.1, player, player_yaw);

        assert!(
            matches!(attack, PhantomAttack::Hit(d) if (d - PHANTOM_ATTACK_DAMAGE).abs() < 1e-3),
            "frontal attack must HIT for {PHANTOM_ATTACK_DAMAGE}, got {attack:?}"
        );
        assert_eq!(driver.movers[0].state, PhantomState::Stalk, "must bounce to STALK after a hit");
    }

    #[tokio::test]
    async fn phantom_statue_timeout_knocks_back_point_blank() {
        // STATUE that times out while the player is point-blank → SPRINT + a Knockback signal.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver.movers[0].state = PhantomState::Statue;
        driver.movers[0].state_timer = PHANTOM_STATUE_MAX + 1.0;
        let ppos = net.peers[&pid].position;
        let player = Vec3::new(ppos[0] + 2.0, 1.8, ppos[2]); // within PHANTOM_KNOCKBACK_RANGE (3 m)

        let attack = driver.step(&mut net, 0.1, player, 0.0);

        assert!(
            matches!(attack, PhantomAttack::Knockback(_, _)),
            "point-blank STATUE timeout must shove, got {attack:?}"
        );
        assert_eq!(driver.movers[0].state, PhantomState::Sprint);
    }

    #[tokio::test]
    async fn phantom_idle_step_returns_no_attack() {
        // A plain WANDER step far from any player produces no attack.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);

        let attack = driver.step(&mut net, 0.1, Vec3::new(100_000.0, 1.8, 100_000.0), 0.0);

        assert_eq!(attack, PhantomAttack::None);
    }
}
