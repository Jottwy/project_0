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

        /// <summary>ADR-061 — first frame of every IPC connection, carrying the backend's
        /// <see cref="WireSchema"/> revision. Written before the write loop starts, so it
        /// precedes any world_state already buffered on the broadcast channel.</summary>
        public const string Hello = "hello";

        /// <summary>ADR-068 — UNA pintada que el host acaba de aceptar. Llega suelta, no dentro
        /// de un roster: una pintada son ~1,9 KB. Es la única vía por la que aparece sin
        /// recargar el chunk, porque el <c>chunk_data</c> de esa pared ya viajó.</summary>
        public const string SprayPlaced = "spray_placed";

        /// <summary>ADR-078 — un trozo del trazo que OTRO jugador está pintando ahora mismo.
        /// Efímero: se dibuja como previa y se tira al llegar el <c>spray_placed</c> con el
        /// mismo <c>place_id</c>, o a los tres segundos sin noticias.</summary>
        public const string SprayDraft = "spray_draft";
    }
}
