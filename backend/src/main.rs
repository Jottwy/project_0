//! Backrooms Survival — distributed P2P co-op survival horror backend.
//!
//! Phases 1-2 (Foundation + World): IPC, game loop, world sim, entity AI.
//! Phase 3 (Networking): UDP P2P mesh with reliability layer.
//!
//! Environment variables:
//!   NET_PORT    — UDP port for P2P networking (default: 7778)
//!   NET_ID      — Local peer ID (default: 1 = host)
//!   NET_NAME    — Player name (default: "Player{NET_ID}")
//!   CONNECT_TO  — Peer address to join on startup (e.g. "127.0.0.1:7778")
//!   WORLD_SEED  — World generation seed (default: 42)

#![allow(dead_code)]

mod crafting;
mod game_loop;
mod ipc;
mod network;
mod persistence;
mod player;
mod utils;
mod world;

use log::{error, info};
use tokio::sync::{broadcast, mpsc};

use network::NetworkManager;

#[tokio::main]
async fn main() {
    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    info!(
        "Backrooms Survival backend v{} starting",
        env!("CARGO_PKG_VERSION")
    );

    // Configuration from environment.
    let net_port: u16 = std::env::var("NET_PORT")
        .ok()
        .and_then(|p| p.parse().ok())
        .unwrap_or(7778);
    let net_id: u16 = std::env::var("NET_ID")
        .ok()
        .and_then(|p| p.parse().ok())
        .unwrap_or(1);
    let world_seed: u64 = std::env::var("WORLD_SEED")
        .ok()
        .and_then(|p| p.parse().ok())
        .unwrap_or(42);
    let connect_to = std::env::var("CONNECT_TO").ok();
    let is_host = connect_to.is_none();
    let ipc_addr_env = std::env::var("IPC_ADDR").ok();
    let ipc_port_env = std::env::var("IPC_PORT").ok();
    let ipc_addr = ipc::resolve_ipc_addr();

    info!(
        "Config: IPC_ADDR={}, IPC_ADDR_ENV={}, IPC_PORT={}, NET_PORT={}, NET_ID={}, role={}",
        ipc_addr,
        ipc_addr_env.as_deref().unwrap_or("<unset>"),
        ipc_port_env.as_deref().unwrap_or("<unset>"),
        net_port,
        net_id,
        if is_host { "host" } else { "joiner" }
    );

    // Unity → game loop (input / actions).
    let (to_game_tx, to_game_rx) = mpsc::channel::<ipc::ClientMessage>(1024);

    // Game loop → Unity (world snapshots).
    let (state_tx, _) = broadcast::channel::<ipc::ServerMessage>(64);

    // IPC server task (Unity ↔ Rust on localhost:7777).
    let ipc_state_tx = state_tx.clone();
    let ipc_handle = tokio::spawn(async move {
        if let Err(e) = ipc::server::run(to_game_tx, ipc_state_tx, ipc_addr).await {
            error!("IPC server terminated: {e}");
        }
    });

    // P2P networking.
    let mut net = NetworkManager::bind(net_port, net_id, world_seed, is_host)
        .await
        .expect("Failed to bind P2P UDP socket");

    // Set player name.
    let net_name = std::env::var("NET_NAME")
        .unwrap_or_else(|_| format!("Player{net_id}"));
    net.local_name = net_name;

    info!(
        "Networking: NET_PORT={}, NET_ID={}, host={}, seed={}",
        net_port, net_id, is_host, world_seed
    );

    // If joining an existing session, initiate handshake.
    if let Some(addr_str) = connect_to {
        match addr_str.parse() {
            Ok(addr) => {
                net.initiate_connection(addr).await;
                info!("Connecting to peer at {addr_str}");
            }
            Err(e) => {
                error!("Invalid CONNECT_TO address '{addr_str}': {e}");
            }
        }
    }

    // Game loop task (drives the whole simulation).
    let game_handle = tokio::spawn(game_loop::run(to_game_rx, state_tx, net));

    // If either core task ends, the process should come down with it.
    tokio::select! {
        _ = ipc_handle => error!("IPC task exited; shutting down"),
        _ = game_handle => error!("Game loop exited; shutting down"),
    }
}
