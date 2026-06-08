//! Authoritative game loop. Runs at 60hz, processes IPC input from Unity,
//! simulates local state (world, entities, stats), manages P2P networking,
//! and streams `WorldState` back at 10hz. See ARCHITECTURE_V1.md §6.1.

use std::time::Duration;

use log::{debug, info};
use tokio::sync::{broadcast, mpsc};
use tokio::time::{interval, MissedTickBehavior};

use crate::ipc::{
    ClientMessage, GameEvent, LocalPlayerState, PlayerInput, RemotePlayerState, ServerMessage,
    StatsView, WorldState,
};
use crate::network::sync;
use crate::network::{NetworkEvent, NetworkManager};
use crate::player::Player;
use crate::utils::Vec3;
use crate::world::World;

const TICK_HZ: u64 = 60;
const TICK_DURATION: Duration = Duration::from_nanos(1_000_000_000 / TICK_HZ);
/// WorldState to Unity at 10hz.
const WORLD_STATE_EVERY: u64 = 6;
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

pub async fn run(
    mut from_clients: mpsc::Receiver<ClientMessage>,
    to_clients: broadcast::Sender<ServerMessage>,
    mut net: NetworkManager,
) {
    let mut player = Player::new(net.local_id, &net.local_name);
    let mut world = World::new(net.world_seed);
    let dt = 1.0 / TICK_HZ as f32;
    let entity_dt = dt * ENTITY_TICK_EVERY as f32;

    let mut ticker = interval(TICK_DURATION);
    ticker.set_missed_tick_behavior(MissedTickBehavior::Skip);

    let mut tick: u64 = 0;
    let mut last_input = PlayerInput::default();

    // Bootstrap: load chunks around spawn.
    world.update_ownership(player.position, player.id);

    info!(
        "Game loop started at {TICK_HZ}hz (tick = {TICK_DURATION:?}), peer_id={}, host={}",
        net.local_id, net.is_host
    );

    loop {
        ticker.tick().await;

        // ─── PHASE 1: RECEIVE (IPC + Network) ───
        while let Ok(msg) = from_clients.try_recv() {
            match msg {
                ClientMessage::Input(input) => last_input = input,
                ClientMessage::Action(action) => {
                    debug!("action received: {}", action.action_type);
                    handle_action(&action.action_type, &mut player, &mut world);
                }
                ClientMessage::UiEvent(ev) => {
                    debug!("ui event: {}", ev.event_type);
                }
            }
        }

        // Process incoming network packets.
        let net_events = net.process_incoming().await;
        for event in net_events {
            handle_network_event(event, &mut player, &mut world, &mut net, &to_clients).await;
        }

        // ─── PHASE 2: SIMULATE ───
        apply_movement(&mut player, &last_input, dt);

        // Entity AI at 10hz.
        if tick % ENTITY_TICK_EVERY == 0 {
            let (damage, events) = world.tick_entities(entity_dt, player.position, player.id);
            if damage > 0.0 {
                player.stats.take_damage(damage);
            }
            for ev in events {
                let _ = to_clients.send(ServerMessage::Event(ev));
            }
            world.tick_respawns(entity_dt);
        }

        // Ownership + teleportation at 1hz.
        if tick % SLOW_TICK_EVERY == 0 {
            world.update_ownership(player.position, player.id);
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
                                + offset
                                    .as_array()
                                    .and_then(|a| a[0].as_i64())
                                    .unwrap_or(0) as i32,
                            old_pos[1]
                                + offset
                                    .as_array()
                                    .and_then(|a| a[1].as_i64())
                                    .unwrap_or(0) as i32,
                        ];
                        sync::broadcast_chunk_teleport(&net, old_pos, new_pos, 0).await;
                    }
                }
            }
        }

        // Stats with real context from the world.
        let ctx = world.stat_context_for(player.position, net.peer_count() as u32);
        player.stats.update(dt, &ctx);

        // Death → respawn.
        if player.stats.is_dead() {
            info!("Player died — respawning");
            player.stats = crate::player::stats::PlayerStats::on_respawn();
            player.position = Vec3::new(0.0, 1.8, 0.0);
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
        }

        // Broadcast chunk states at 5hz.
        if tick % CHUNK_BROADCAST_EVERY == 0 {
            sync::broadcast_chunk_states(&net, &world, player.position).await;
        }

        // Heartbeat every 1s.
        if tick % HEARTBEAT_EVERY == 0 {
            net.send_heartbeats().await;

            // Check timeouts.
            let timeout_events = net.check_timeouts();
            for event in timeout_events {
                handle_network_event(event, &mut player, &mut world, &mut net, &to_clients).await;
            }
        }

        // Process reliable retransmits.
        if tick % ENTITY_TICK_EVERY == 0 {
            net.process_retransmits().await;
        }

        // ─── PHASE 4: SEND — WorldState to Unity at 10hz ───
        if tick % WORLD_STATE_EVERY == 0 {
            let snapshot = build_world_state(tick, &player, &world, &net);
            let _ = to_clients.send(ServerMessage::WorldState(snapshot));
        }

        tick = tick.wrapping_add(1);
    }
}

async fn handle_network_event(
    event: NetworkEvent,
    player: &mut Player,
    world: &mut World,
    net: &mut NetworkManager,
    to_clients: &broadcast::Sender<ServerMessage>,
) {
    match event {
        NetworkEvent::PeerConnected { id, name } => {
            info!("Peer connected: {} (id={})", name, id);
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "player_joined".into(),
                data: serde_json::json!({ "player_id": id, "name": name }),
            }));

            // If we're the host, send world sync to the new peer.
            if net.is_host {
                sync::send_world_sync(net, id, world, player).await;
            }

            // Update player id if we just got assigned one.
            if !net.is_host && player.id == 0 {
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

        NetworkEvent::WorldSyncReceived { chunks } => {
            info!("Received world sync with {} chunks", chunks.len());
            world.apply_world_sync(&chunks, net.local_id);
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
            debug!("Chunk transfer ACK from {} for [{}, {}]", from, pos[0], pos[1]);
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

fn apply_movement(player: &mut Player, input: &PlayerInput, dt: f32) {
    player.rotation = (player.rotation + input.look_delta[0]).rem_euclid(360.0);
    let dir = Vec3::from_array(input.movement).normalized();
    let sprint = if input.sprint { SPRINT_MULT } else { 1.0 };
    let speed = BASE_SPEED * player.stats.speed_modifier * sprint;
    player.position = player.position.add(dir.scale(speed * dt));
}

fn handle_action(action_type: &str, player: &mut Player, world: &mut World) {
    match action_type {
        "attack" => {
            let player_pos = player.position;
            let attack_range = 3.0f32;
            let damage = 10u8;
            for chunk in world.chunks.values_mut() {
                for entity in chunk.entities.iter_mut() {
                    if entity.is_alive()
                        && entity.position.distance_xz(player_pos) <= attack_range
                    {
                        let killed = entity.take_damage(damage);
                        if killed {
                            let cable_count: u16 = rand::random::<u16>() % 6 + 5;
                            player
                                .inventory
                                .add(crate::player::inventory::Item::Cable, cable_count);
                            info!(
                                "Entity {} killed, dropped {} cable",
                                entity.id, cable_count
                            );
                        }
                        break;
                    }
                }
            }
        }
        "interact" | "pickup" => {
            let player_pos = player.position;
            let pickup_range = 3.0f32;
            for chunk in world.chunks.values_mut() {
                if let Some(idx) = chunk
                    .items
                    .iter()
                    .position(|i| i.position.distance_xz(player_pos) <= pickup_range)
                {
                    let item = chunk.items.remove(idx);
                    let overflow = player.inventory.add(item.item, item.quantity);
                    if overflow > 0 {
                        chunk.items.push(crate::world::chunk::DroppedItem {
                            id: item.id,
                            item: item.item,
                            quantity: overflow,
                            position: item.position,
                        });
                    }
                    info!(
                        "Picked up {} x{}",
                        item.item.type_name(),
                        item.quantity - overflow
                    );
                    break;
                }
            }
        }
        _ => {}
    }
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
    world: &World,
    net: &NetworkManager,
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

    if tick % 60 == 0 && !remote_players.is_empty() {
        debug!(
            "WorldState remote_players={}: {:?}",
            remote_players.len(),
            remote_players
                .iter()
                .map(|p| (p.id, p.name.as_str()))
                .collect::<Vec<_>>()
        );
    }

    WorldState {
        tick,
        local_player: LocalPlayerState {
            position: player.position.to_array(),
            rotation: player.rotation,
            stats: StatsView {
                health: player.stats.health,
                hunger: player.stats.hunger,
                thirst: player.stats.thirst,
                sanity: player.stats.sanity,
            },
            speed_modifier: player.stats.speed_modifier,
            inventory_changed: false,
        },
        remote_players,
        visible_chunks: world.visible_chunk_views(),
        visible_entities: world.visible_entity_views(),
        visible_items: world.visible_item_views(),
    }
}
