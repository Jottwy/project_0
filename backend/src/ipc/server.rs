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

/// ADR-009 wire schema revision. Bumped when the player input/state schema
/// changes; the transport itself is unchanged (still length-prefixed
/// MessagePack). v2 adds the client-prediction fields to PlayerInput and
/// ack_input_seq/stamina to the snapshot. v3 (ADR-020) adds `crouch:bool` to
/// PlayerInput and RemotePlayerState — all `serde(default)`, so v1/v2 clients
/// still interoperate (a missing `crouch` decodes to false). v4 (ADR-021) adds
/// `pitch:i8` to RemotePlayerState (and the P2P PlayerUpdate) — reusing the
/// existing `PlayerInput.look[0]` for input, also `serde(default)` (missing
/// `pitch` decodes to 0 = looking forward). v5 (ADR-022) adds `equipment:[i32;4]`
/// (worn clothing item IDs) to PlayerInput, RemotePlayerState and the P2P
/// PlayerUpdate — all `serde(default)`, so older clients interoperate (a missing
/// `equipment` decodes to [0,0,0,0] = no clothing).
const WIRE_SCHEMA_VERSION: u32 = 5;

/// Run the IPC server until a fatal accept error.
///
/// * `to_game`  — channel to forward decoded client messages to the game loop.
/// * `state_tx` — broadcast of outbound world state; each connection subscribes.
pub async fn run(
    to_game: mpsc::Sender<ClientMessage>,
    state_tx: broadcast::Sender<ServerMessage>,
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

        tokio::spawn(async move {
            let (reader, writer) = stream.into_split();
            let mut read_task = tokio::spawn(read_loop(reader, to_game, peer));
            let mut write_task = tokio::spawn(write_loop(writer, state_rx));

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
async fn write_loop(mut writer: OwnedWriteHalf, mut state_rx: broadcast::Receiver<ServerMessage>) {
    loop {
        match state_rx.recv().await {
            Ok(msg) => match encode(&msg) {
                Ok(frame) => {
                    if let ServerMessage::WorldState(ws) = &msg {
                        let remote_ids: Vec<u16> = ws.remote_players.iter().map(|p| p.id).collect();
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
                    if writer.write_all(&frame).await.is_err() {
                        break;
                    }
                }
                Err(e) => warn!("Failed to encode server message: {e}"),
            },
            Err(broadcast::error::RecvError::Closed) => break,
            Err(broadcast::error::RecvError::Lagged(skipped)) => {
                // Unity fell behind; older snapshots are stale anyway.
                warn!("IPC write loop lagged, skipped {skipped} messages");
            }
        }
    }
}
