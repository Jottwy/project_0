namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-093 — which side of a Level 4 door was crossed. Wire values (the byte sent in the
    /// <c>level4_door</c> action's <c>door</c> field) MUST match
    /// <c>world::level4_layout::{DOOR_ENTRY, DOOR_RETURN}</c> on the backend exactly.
    /// </summary>
    public enum Level4Door : byte
    {
        Entry = 0,
        Return = 1,
    }
}
