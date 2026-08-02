namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Top-level <c>type</c> discriminators for inbound (server → client) IPC frames,
    /// matched in <see cref="IPCClient"/>.Dispatch(). These mirror the Rust backend's
    /// <c>ServerMessage</c> enum (backend/src/ipc/mod.rs), which serializes with
    /// <c>#[serde(tag = "type", rename_all = "snake_case")]</c> — so each value here is the
    /// snake_case form of a variant name (WorldState → "world_state", …). The authoritative
    /// source is that enum, not a literal; do NOT change a value without an ADR (the protocol
    /// is versioned, see CONVENTIONS.md "Protocolo").
    ///
    /// Distinct from <see cref="ProtocolActionTypes"/>: those are the <c>action_type</c> sub-tag
    /// of outbound "action" frames; these are the top-level frame <c>type</c> the client reads.
    /// </summary>
    internal static class ProtocolMessageTypes
    {
        public const string WorldState = "world_state";
        public const string DeltaUpdate = "delta_update";
        public const string ChunkData = "chunk_data";
        public const string Event = "event";
        public const string ActionResult = "action_result";

        /// <summary>ADR-046 — one voice frame from a remote peer, already filtered by distance
        /// at the host. Arrives on the backend's SEPARATE voice channel, so a burst of audio
        /// cannot evict a world-state message or an event.</summary>
        public const string PeerVoice = "peer_voice";
    }
}
