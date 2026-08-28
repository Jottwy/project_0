namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-061 — the client's mirror of the backend's <c>WIRE_SCHEMA_VERSION</c>
    /// (backend/src/ipc/server.rs). The backend is the authority on the number; this constant
    /// has to be bumped in lockstep with it, in the same change.
    ///
    /// Why a gate exists at all: every Parse in IPCMessages.cs skips keys it doesn't recognize
    /// and leaves absent ones at their C# default, which is what makes additive schema changes
    /// safe — and also what turns a REAL mismatch into plausible-looking data (a failed
    /// remote_players parse is byte-for-byte indistinguishable from "no remote players",
    /// STABILITY_AUDIT_CURRENT.md R4). The per-field contract stays as it is; this checks the
    /// envelope once per connection instead.
    ///
    /// Exact equality, not a floor: deployment is lockstep (a single exe under Builds/Backend/,
    /// ADR-047), so a mismatch is a desynced build, never a legitimately older peer.
    ///
    /// Public (not internal) so the EditMode suite can reach it: the compile-check builds each
    /// assembly with a `_check` suffix, so an InternalsVisibleTo friend name would never match —
    /// the same criterion already applied to SessionEndHandler.ReadReason.
    /// </summary>
    public static class WireSchema
    {
        /// <summary>Revision this client speaks. Must equal the backend's constant.</summary>
        public const uint Expected = 47;

        /// <summary>
        /// A backend that omits <c>schema_version</c> decodes as 0, which is deliberately NOT
        /// compatible: an absent field must fail the gate rather than pass it by default.
        /// </summary>
        public static bool IsCompatible(uint backendVersion) => backendVersion == Expected;

        /// <summary>Names both sides — the log line has to say which build to rebuild.</summary>
        public static string MismatchReason(uint backendVersion) =>
            $"wire_schema_mismatch backend=v{backendVersion} client=v{Expected}";
    }
}
