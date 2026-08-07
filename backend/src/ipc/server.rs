//! TCP IPC server (localhost:7777) bridging the Unity client and the game loop.
//!
//! Each connection runs two tasks:
//!   * a read loop  — decode incoming frames → `ClientMessage` → game loop (mpsc)
//!   * a write loop — `ServerMessage` from the game loop (broadcast) → encode → socket
//!
//! See CLAUDE_CODE_INSTRUCTIONS.md Task 1.2 and ARCHITECTURE_V1.md §12.

use std::net::SocketAddr;

use log::{info, warn};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};
use tokio::net::TcpListener;
use tokio::sync::{broadcast, mpsc};

use super::{decode, encode, ClientMessage, ServerMessage};

/// Reject absurd frame sizes (protects against a malformed length prefix).
const MAX_FRAME_BYTES: usize = 16 * 1024 * 1024;

/// ADR-009 wire schema revision — the number Unity and the backend agree on.
///
/// The full v2→v22 changelog (what each bump added, why, and how the previous version
/// degrades) lives in `docs/systems/ipc-wire-schema.md`, together with the rule that a
/// P2P-only change bumps this counter too. Bumping this constant means appending an entry
/// there in the same commit — and the CODE is the authority on the number, not the ADR.
const WIRE_SCHEMA_VERSION: u32 = 22;

/// Run the IPC server until a fatal accept error.
///
/// * `to_game`  — channel to forward decoded client messages to the game loop.
/// * `state_tx` — broadcast of outbound world state; each connection subscribes.
/// * `voice_tx` — ADR-046: broadcast of inbound peer voice, SEPARATE from `state_tx` and for
///   one structural reason. `state_tx` holds 256 slots and, when it overflows, `recv()` returns
///   `Lagged` and the oldest messages are gone — Events included, `player_died` among them (see
///   the handler at the bottom of `write_loop`). Voice arrives at 25 Hz per speaker and would be
///   the loudest producer on that channel, so sharing it would let a burst of audio delete a
///   death event. On its own channel an overflow can only cost audio, which is the one payload
///   here that is worthless once late.
pub async fn run(
    to_game: mpsc::Sender<ClientMessage>,
    state_tx: broadcast::Sender<ServerMessage>,
    voice_tx: broadcast::Sender<ServerMessage>,
    ipc_addr: String,
) -> std::io::Result<()> {
    let listener = TcpListener::bind(&ipc_addr).await?;
    info!("IPC server listening on {ipc_addr} (wire schema v{WIRE_SCHEMA_VERSION})");

    loop {
        let (stream, peer) = listener.accept().await?;
        // Local loopback: disable Nagle so small input frames go out immediately.
        let _ = stream.set_nodelay(true);
        info!("Unity client connected: {peer}");

        let to_game = to_game.clone();
        let state_rx = state_tx.subscribe();
        let voice_rx = voice_tx.subscribe();

        tokio::spawn(async move {
            let (reader, writer) = stream.into_split();
            let mut read_task = tokio::spawn(read_loop(reader, to_game, peer));
            let mut write_task = tokio::spawn(write_loop(writer, state_rx, voice_rx));

            // When either half ends (disconnect/error), tear down the other.
            tokio::select! {
                _ = &mut read_task => write_task.abort(),
                _ = &mut write_task => read_task.abort(),
            }
            info!("Unity client disconnected: {peer}");
        });
    }
}

/// Decode length-prefixed MessagePack frames into `ClientMessage`s.
async fn read_loop(
    mut reader: OwnedReadHalf,
    to_game: mpsc::Sender<ClientMessage>,
    peer: SocketAddr,
) {
    let mut len_buf = [0u8; 4];
    loop {
        // Read the 4-byte big-endian length prefix.
        if reader.read_exact(&mut len_buf).await.is_err() {
            break; // EOF / connection closed
        }
        let len = u32::from_be_bytes(len_buf) as usize;
        if len == 0 || len > MAX_FRAME_BYTES {
            warn!("Dropping {peer}: invalid frame length {len}");
            break;
        }

        // Read the MessagePack body.
        let mut body = vec![0u8; len];
        if reader.read_exact(&mut body).await.is_err() {
            break;
        }

        match decode::<ClientMessage>(&body) {
            Ok(msg) => {
                if to_game.send(msg).await.is_err() {
                    // Game loop is gone; nothing more to do.
                    break;
                }
            }
            Err(e) => warn!("Failed to decode client message from {peer}: {e}"),
        }
    }
}

/// Encode outbound `ServerMessage`s and write them to the socket.
async fn write_loop(
    mut writer: OwnedWriteHalf,
    mut state_rx: broadcast::Receiver<ServerMessage>,
    mut voice_rx: broadcast::Receiver<ServerMessage>,
) {
    // Throttle for the per-WorldState diagnostic logs below. They used to fire on EVERY
    // WorldState (10 Hz × 2 lines = 20 log lines/s) INSIDE this hot path; stdout/stderr are
    // PIPED to Unity (OutputDataReceived → Debug.Log), so when Unity's main thread stalls the
    // pipe fills and the blocked log write stalls this whole loop → the 256-slot broadcast
    // buffer overflows → "IPC write loop lagged, skipped N" (dropping deltas AND events, e.g.
    // player_died). Keeping the trace at 1 per 2 s preserves the diagnostic value at 1/40th
    // of the log pressure.
    let mut next_ws_log = std::time::Instant::now();
    // ADR-046: voice rides its own receiver. `broadcast::Receiver::recv` is documented as
    // cancel-safe, which is what makes it legal to poll both in a `select!` — the loser of a
    // race has provably received nothing, so no frame is lost between iterations. `select!`
    // picks randomly among ready branches, so neither channel can starve the other.
    //
    // `voice_open` exists because a closed voice channel must NOT take the connection down:
    // the client would lose world state too, over an audio channel it may never have used.
    // A disabled branch simply stops being polled.
    let mut voice_open = true;
    loop {
        let received = tokio::select! {
            r = state_rx.recv() => r,
            r = voice_rx.recv(), if voice_open => match r {
                Err(broadcast::error::RecvError::Closed) => {
                    voice_open = false;
                    warn!("IPC voice channel closed; world state keeps flowing");
                    continue;
                }
                other => other,
            },
        };
        match received {
            Ok(msg) => match encode(&msg) {
                Ok(frame) => {
                    if let ServerMessage::WorldState(ws) = &msg {
                        let now = std::time::Instant::now();
                        if now >= next_ws_log {
                            next_ws_log = now + std::time::Duration::from_secs(2);
                            let remote_ids: Vec<u16> =
                                ws.remote_players.iter().map(|p| p.id).collect();
                            info!(
                                "MPTRACE step=I event=ipc_serialize_world_state self_id=<unknown> sender_id=<none> assigned_id=<none> peer_id=<none> endpoint=ipc peer_count=<unknown> remote_players_count={} remote_players_ids={:?} seed={} revision={} chunks={} entities={} items={}",
                                ws.remote_players.len(),
                                remote_ids,
                                ws.world_seed,
                                ws.world_revision,
                                ws.visible_chunks.len(),
                                ws.visible_entities.len(),
                                ws.visible_items.len()
                            );
                            let layout_chunks = ws
                                .visible_chunks
                                .iter()
                                .filter(|chunk| chunk.layout_cells.len() == 100)
                                .count();
                            info!(
                                "MPTRACE step=CC event=chunk_layout_sync chunks={} layout_chunks={} fields=edge_openings,macro_id,zone_kind,floor_profile,layout_cells",
                                ws.visible_chunks.len(),
                                layout_chunks
                            );
                        }
                    }
                    if writer.write_all(&frame).await.is_err() {
                        break;
                    }
                }
                Err(e) => warn!("Failed to encode server message: {e}"),
            },
            Err(broadcast::error::RecvError::Closed) => break,
            Err(broadcast::error::RecvError::Lagged(skipped)) => {
                // Unity fell behind; older snapshots are stale anyway. NOTE: this drops
                // whatever was oldest in the broadcast buffer — including Events (player_died).
                // The throttled logging above + the 256 capacity make this rare; if it still
                // fires, events may have been lost and TP_WATCH/DEATH_FLOW readings around this
                // timestamp are suspect. ADR-046 keeps voice OFF this channel so a burst of
                // audio can never be the cause of that loss; a lagged voice channel reports
                // here too, but the only thing it can have dropped is audio.
                warn!("IPC write loop lagged, skipped {skipped} messages");
            }
        }
    }
}
