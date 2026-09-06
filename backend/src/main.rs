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

// RECUENTO 2026-09-05, medido quitando este `allow` y compilando:
//   294 warnings `dead_code` únicos en el binario
//   308 con `--all-targets` (los 14 de más son código sólo-de-test)
//
// La cifra anterior escrita aquí era la del 2026-08-10: 112 y 121. **Se ha multiplicado por 2,6
// en menos de un mes**, y eso no es un detalle de contabilidad: el argumento con el que este
// `allow` de crate se justificaba —«~120 sitios en movimiento activo, los `#[allow]` por ítem
// serían un diff volátil que se reescribe en cada sesión de migración»— ya no describe lo que
// hay. WorldGen3 ha traído su propio andamio encima del de `grid_gen`, y con casi 300 sitios la
// pregunta deja de ser si el diff sería volátil y pasa a ser cuánto código muerto está tapando
// esta línea.
//
// Sigue siendo scaffelding de migración en su mayoría (generadores V0 alternativos, exportadores
// ASCII de debug, capas de validación que no se cablean todavía), y sigue siendo intencional. Lo
// que cambia es que ya no cabe darlo por bueno sin mirar: bajarlo a `#[allow]` por módulo es una
// sesión propia y puede poner `clippy -D warnings` en rojo, así que va anotado como deuda con
// número, no como nota al pie. Ver `docs/STATE.md` → «Deuda declarada».
#![allow(dead_code)]
// CONVENTIONS.md: `unsafe` prohibido salvo ADR. Espejo del [lints] de Cargo.toml.
#![forbid(unsafe_code)]

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

/// Una dirección opcional del entorno. Una que no parsea **se dice**: tragársela en silencio y
/// seguir sin ella es cómo se pierden veinte minutos buscando por qué no se intentó una vía que
/// estaba configurada.
fn parse_optional_addr(key: &str) -> Option<std::net::SocketAddr> {
    let raw = std::env::var(key).ok()?;
    let raw = raw.trim();
    if raw.is_empty() {
        return None;
    }
    match raw.parse() {
        Ok(addr) => Some(addr),
        Err(e) => {
            error!("CONNECTIVITY event=bad_config key={key} value={raw:?} error={e}");
            None
        }
    }
}

/// La configuración del relay, o `None` si esta partida no lo usa (ADR-117).
///
/// **Un relay mal configurado no impide jugar.** Falta una variable, el token no es hexadecimal,
/// la dirección no parsea: se nombra el problema y se sigue sin relay, porque la partida directa
/// y la de LAN no tienen por qué caerse con él.
fn read_relay_config(is_host: bool) -> Option<network::relay_client::RelayConfig> {
    let relay_addr = parse_optional_addr("RELAY_ADDR")?;

    let session_raw = std::env::var("RELAY_SESSION").ok()?;
    let session_raw = session_raw.trim();
    let session_id = match session_raw.strip_prefix("0x") {
        Some(hex) => u64::from_str_radix(hex, 16).ok(),
        None => session_raw.parse::<u64>().ok(),
    };
    let Some(session_id) = session_id else {
        error!("CONNECTIVITY event=bad_config key=RELAY_SESSION value={session_raw:?}");
        return None;
    };

    let token_raw = std::env::var("RELAY_TOKEN").ok()?;
    // El VALOR no se registra ni cuando está mal: un token roto sigue siendo un secreto.
    let Some(token) = backrooms_relay::protocol::SessionToken::from_hex(&token_raw) else {
        error!(
            "CONNECTIVITY event=bad_config key=RELAY_TOKEN reason=no_son_32_hex len={}",
            token_raw.trim().len()
        );
        return None;
    };

    info!(
        "CONNECTIVITY event=relay_configured relay={relay_addr} session={session_id:#x} role={}",
        if is_host { "host" } else { "joiner" }
    );

    Some(network::relay_client::RelayConfig {
        relay_addr,
        session_id,
        token,
        // La MISMA versión que viaja en el handshake del juego. El relay la usa para no dejar
        // entrar a un build que no sabría leer el mundo que sirve este host.
        wire_version: ipc::server::WIRE_SCHEMA_VERSION as u16,
        as_host: is_host,
    })
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
    // ADR-117 D7: con un lobby relay-only NO hay `CONNECT_TO` —el host no publicó ningún endpoint
    // directo porque no tenía ninguno defendible— y hasta aquí el rol se deducía SÓLO de esa
    // variable: sin ella, host. O sea que un joiner por relay habría arrancado como host y se
    // habría puesto a servir un mundo en solitario, que es exactamente el fallo mudo que ADR-111
    // y el `session_joined` de ADR-056 vinieron a cerrar.
    //
    // `RELAY_ROLE` desempata. Ausente, todo se comporta como siempre.
    let relay_role = std::env::var("RELAY_ROLE")
        .ok()
        .map(|v| v.trim().to_lowercase());
    let is_host = match relay_role.as_deref() {
        Some("joiner") => false,
        Some("host") => true,
        _ => connect_to.is_none(),
    };
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

    // ADR-095 D3 — se lee UNA vez y la comparten el saludo y el bucle de juego. Si cada uno lo
    // leyera por su cuenta, el saludo podría anunciar un mundo y el bucle servir otro, y la
    // discrepancia solo se vería como chunks que nunca llegan.
    let wg3 = world::wg3::config::Wg3Config::from_env();

    // IPC server task (Unity ↔ Rust on localhost:7777).
    let ipc_state_tx = state_tx.clone();
    let ipc_voice_tx = voice_tx.clone();
    let ipc_wg3 = wg3.clone();
    let ipc_handle = tokio::spawn(async move {
        if let Err(e) = ipc::server::run(
            to_game_tx,
            ipc_state_tx,
            ipc_voice_tx,
            ipc_addr,
            local_disconnect_tx,
            ipc_wg3,
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

    // ADR-117: el relay de respaldo. Sin `RELAY_ADDR` esto no hace absolutamente nada y todo lo
    // de abajo se comporta como antes de este ADR.
    let relay = read_relay_config(is_host);
    if let Some(config) = relay.clone() {
        net.connect_relay(config);
    }

    // If joining an existing session, initiate handshake.
    if let Some(addr_str) = connect_to {
        match addr_str.parse::<std::net::SocketAddr>() {
            Ok(addr) => {
                // ADR-117 D10: con relay o con LAN alternativa hay SECUENCIA; sin ninguna de las
                // dos, un solo destino y el mismo presupuesto de siempre.
                let lan = parse_optional_addr("CONNECT_LAN");
                let relay_target = net.relay_host_addr();
                if lan.is_some() || relay_target.is_some() {
                    net.initiate_sequence(network::connect::ConnectSequence::new(
                        Some(addr),
                        lan,
                        relay_target,
                    ))
                    .await;
                } else {
                    net.initiate_connection(addr).await;
                }
                info!("Connecting to peer at {addr_str}");
            }
            Err(e) => {
                // Registrarlo y seguir era el fallo: sin `initiate_connection` no arranca el
                // presupuesto de `CONNECT_TIMEOUT`, así que este proceso se quedaba sirviendo un
                // mundo en solitario sin decirle NUNCA nada a Unity. Ver
                // `NetworkEvent::ConnectTargetInvalid`.
                error!("Invalid CONNECT_TO address '{addr_str}': {e}");
                net.reject_invalid_connect_target(&addr_str, &e.to_string());
            }
        }
    } else if !is_host {
        // Sin `CONNECT_TO` pero con relay: es el lobby relay-only de ADR-117 D7, donde el host no
        // publicó ningún endpoint directo porque no tenía ninguno defendible.
        if let Some(target) = net.relay_host_addr() {
            net.initiate_sequence(network::connect::ConnectSequence::new(
                None,
                parse_optional_addr("CONNECT_LAN"),
                Some(target),
            ))
            .await;
        }
    }

    // Game loop task (drives the whole simulation).
    let game_handle = tokio::spawn(game_loop::run(
        to_game_rx,
        state_tx,
        voice_tx,
        net,
        local_disconnect_rx,
        wg3,
    ));

    // If either core task ends, the process should come down with it.
    tokio::select! {
        _ = ipc_handle => error!("IPC task exited; shutting down"),
        _ = game_handle => error!("Game loop exited; shutting down"),
    }
}
