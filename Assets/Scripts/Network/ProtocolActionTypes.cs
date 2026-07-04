namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Canonical <c>action_type</c> string tags for the {type:"action", action_type, data}
    /// IPC frames sent by <see cref="IPCClient"/>. Each value MUST match the tag the Rust
    /// backend matches on (backend/src/ipc). These are wire values: do NOT change a value
    /// without an ADR — the protocol is versioned (see CONVENTIONS.md "Protocolo").
    /// </summary>
    internal static class ProtocolActionTypes
    {
        public const string WorldInteract = "world_interact";
        public const string SetStpItems = "set_stp_items";
        public const string StpPickup = "stp_pickup";
        public const string StpDrop = "stp_drop";
        public const string StpPlace = "stp_place";
        public const string StpBuildAdd = "stp_build_add";
        public const string SetStpCarryables = "set_stp_carryables";
        public const string StpCarryablePickup = "stp_carryable_pickup";
        public const string StpCarryableDrop = "stp_carryable_drop";
        public const string SetStpHarvestables = "set_stp_harvestables";
        public const string StpHarvestHit = "stp_harvest_hit";
        /// <summary>ADR-025 Slice B: report REAL local damage to the authoritative backend.</summary>
        public const string ReportDamage = "report_damage";
        /// <summary>ADR-025 respawn-on-demand: ask the server to respawn (honored only while dead).</summary>
        public const string RespawnRequest = "respawn_request";
        /// <summary>ADR-028 Fase B: report the death-loot snapshot (full inventory + equipment
        /// + held item, raw STP item ids) so the server spawns the authoritative corpse.</summary>
        public const string ReportDeathLoot = "report_death_loot";
        /// <summary>ADR-028 Fase D: report a loot withdrawal from a corpse's container (backend
        /// action implemented Fase A) so the server's CorpseData mirrors the local take.</summary>
        public const string TakeCorpseItem = "take_corpse_item";
    }
}
