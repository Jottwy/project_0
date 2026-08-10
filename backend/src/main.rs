//! Backrooms Survival — distributed P2P co-op survival horror backend.
//!
//! Phases 1-2 (Foundation + World): IPC, game loop, world sim, entity AI.
//! Phase 3 (Networking): UDP host-as-server star with reliability layer (NOT a mesh —
//! joiners connect only to the host, which relays their poses to each other, ADR-015).
//!
//! Environment variables:
//!   NET_PORT    — UDP port for P2P networking (default: 7778)
//!   NET_ID      — Local peer ID (default: 1 = host)
//!   NET_NAME    — Player name (default: "Player{NET_ID}")
//!   CONNECT_TO  — Peer address to join on startup (e.g. "127.0.0.1:7778")
//!   WORLD_SEED  — World generation seed (default: 42)

// Auditoría (2026-08-10): inventario medido quitando este `allow` y compilando —
// 112 warnings `dead_code` únicos en el binario, 121 en total contando lo que solo aparece
// bajo `--all-targets` (código de test-only). La inmensa mayoría es scaffolding de la
// migración `grid_gen`/world-graph en curso (ver `docs/STATE.md`): generadores V0 alternativos,
// exportadores ASCII de debug, capas de validación de nivel 0 que aún no se cablean —
// intencional, no descuido acumulado. `#[allow]` selectivos por-item quedan diferidos a
// cuando la migración cierre: con ~120 sitios en movimiento activo, targeted allows serían un
// diff enorme y volátil que se reescribiría en cada sesión de migración. Ver STATE.md
// "Auditoría: dead_code inventariado".
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

/// Emit unambiguous runtime build/identity telemetry at startup so logs prove
/// exactly which backend binary is running (kills "stale backend" ambiguity).
fn log_runtime_identity(world_seed: u64) {
    let build_name = std::env::var("BACKROOMS_SERVER_BUILD_NAME")
        .unwrap_or_else(|_| format!("{}-{}", env!("CARGO_PKG_NAME"), env!("CARGO_PKG_VERSION")));
    let exe_path = std::env::current_exe()
        .map(|p| p.display().to_string())
        .unwrap_or_else(|_| "<unresolved>".to_string());

    info!(
        "MPTRACE step=RUNTIME event=runtime_build_identity build_name={} name={} version={} git={} built_unix={}",
        build_name,
        env!("CARGO_PKG_NAME"),
        env!("CARGO_PKG_VERSION"),
        option_env!("BACKROOMS_GIT_HASH").unwrap_or("unknown"),
        option_env!("BACKROOMS_BUILD_TIMESTAMP").unwrap_or("unknown"),
    );
    if exe_path == "<unresolved>" {
        error!("MPTRACE step=RUNTIME event=runtime_backend_exe_identity exe=<unresolved> reason=current_exe_failed");
    } else {
        info!("MPTRACE step=RUNTIME event=runtime_backend_exe_identity exe={exe_path}");
    }
    info!("MPTRACE step=RUNTIME event=runtime_world_seed_confirmed world_seed={world_seed}");

    let rubik_enabled = world_seed == world::volumetric_grid::SHOWCASE_SEED;
    info!("MPTRACE step=RUNTIME event=runtime_rubik_grid_enabled enabled={rubik_enabled} showcase_seed={}", world::volumetric_grid::SHOWCASE_SEED);
    info!("MPTRACE step=RUNTIME event=runtime_legacy_visfix_disabled disabled=true");
    info!("MPTRACE step=RUNTIME event=runtime_legacy_interlayer_render_disabled disabled=true");

    // For the showcase seed, surface the RubikGrid counts up-front so the
    // identity block and the grid metrics appear together at startup.
    if rubik_enabled {
        world::volumetric_grid::log_showcase_once(true);
    }
}

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

    log_runtime_identity(world_seed);
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

    // ADR-045 fix: IPC server → game loop, "this backend's own local Unity client just
    // disconnected" — see the doc comment in `ipc::server::run` and the receiving end in
    // `game_loop::run`. Small buffer: this is a rare, idempotent wake-up signal, not a data
    // channel.
    let (local_disconnect_tx, local_disconnect_rx) = mpsc::channel::<()>(4);

    // Game loop → Unity (world snapshots). Capacity 256 (was 64): idle traffic alone is
    // ~30 msg/s (20 Hz delta + 10 Hz world_state), so 64 was only ~2 s of writer stall before
    // "IPC write loop lagged" dropped messages — including Events (player_died). 256 rides out
    // an ~8 s stall (Unity scene-load/GC hitches back-pressuring the piped stdout logs).
    let (state_tx, _) = broadcast::channel::<ipc::ServerMessage>(256);

    // ADR-046 — voice → Unity, on its OWN channel. Not a style choice: `state_tx` drops its
    // OLDEST messages on overflow (Events included), and voice at 25 Hz per speaker would be
    // the loudest producer on it. Capacity 64 rather than 256 because a voice frame that waited
    // two seconds is worthless — a shorter queue drops it sooner instead of playing it late.
    let (voice_tx, _) = broadcast::channel::<ipc::ServerMessage>(64);

    // IPC server task (Unity ↔ Rust on localhost:7777).
    let ipc_state_tx = state_tx.clone();
    let ipc_voice_tx = voice_tx.clone();
    let ipc_handle = tokio::spawn(async move {
        if let Err(e) = ipc::server::run(
            to_game_tx,
            ipc_state_tx,
            ipc_voice_tx,
            ipc_addr,
            local_disconnect_tx,
        )
        .await
        {
            error!("IPC server terminated: {e}");
        }
    });

    // P2P networking.
    let mut net = NetworkManager::bind(net_port, net_id, world_seed, is_host)
        .await
        .expect("Failed to bind P2P UDP socket");

    // Set player name.
    let net_name = std::env::var("NET_NAME").unwrap_or_else(|_| format!("Player{net_id}"));
    net.local_name = net_name;

    // P0-2: launch-time value for the phantom population draw. A loaded save overrides this
    // (game_loop::run, same precedent as world_seed); the joiner side adopts the host's value
    // from the HandshakeAck regardless of what it read here.
    net.phantom_density_scale = std::env::var("PHANTOM_DENSITY_SCALE")
        .ok()
        .and_then(|v| v.parse::<f32>().ok())
        .unwrap_or(1.0)
        .max(0.0);

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
    let game_handle = tokio::spawn(game_loop::run(
        to_game_rx,
        state_tx,
        voice_tx,
        net,
        local_disconnect_rx,
    ));

    // If either core task ends, the process should come down with it.
    tokio::select! {
        _ = ipc_handle => error!("IPC task exited; shutting down"),
        _ = game_handle => error!("Game loop exited; shutting down"),
    }
}
