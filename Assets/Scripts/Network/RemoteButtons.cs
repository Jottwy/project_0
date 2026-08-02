namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-044: bit layout of the relayed <c>buttons</c> field — the peer's SUSTAINED cosmetic
    /// states. One definition shared by the writer (<see cref="PlayerPoseTransmitter"/>) and the
    /// readers (the proxy hooks), because a bitfield with the meaning of each bit written down in
    /// two places is a bug waiting for the first person who adds a third.
    ///
    /// The field itself is not new: it has existed in the IPC frame since ADR-009, written as a
    /// literal 0 and read by nobody. ADR-044 gives it meaning instead of adding a bool per state,
    /// so this costs no extra field on the wire and leaves 14 bits for whatever comes next.
    ///
    /// Bits are APPEND-ONLY. A value already assigned here must never be reused for something else:
    /// a peer running an older build keeps sending the old meaning, and a recycled bit would make
    /// its avatar do the wrong thing rather than simply do nothing.
    ///
    /// TRANSIENT actions do NOT belong here. A level bit cannot tell two chained events from one
    /// held one — that is what the counters (<c>hit_seq</c>, <c>fire_seq</c>, <c>melee_seq</c>) are for.
    /// </summary>
    public static class RemoteButtons
    {
        /// <summary>Aiming down sights.</summary>
        public const int Aiming = 1 << 0;

        /// <summary>Reloading — tactically the most valuable bit here: it says "vulnerable now".</summary>
        public const int Reloading = 1 << 1;

        public static bool Has(int buttons, int bit) => (buttons & bit) != 0;
    }
}
