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
        /// <summary>ADR-069: a bed finished being BUILT (not merely placed) at a position. The
        /// respawn point is armed here, never on stp_place — planting the blueprint used to hand
        /// out a respawn for free.</summary>
        public const string BedConstructed = "bed_constructed";
        public const string StpDemolish = "stp_demolish";
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
        /// <summary>ADR-028 amendment (world chests): host seeds one supply chest (position +
        /// loot picked client-side; host-only server-side, deduped by request_id).</summary>
        public const string SpawnWorldChest = "spawn_world_chest";
        /// <summary>ADR-029 Fase 1: report a candidate PvP hit. Unity never applies PvP damage.</summary>
        public const string PvpHitCandidate = "pvp_hit_candidate";
        /// <summary>ADR-030: report a consumed item (eat/drink) so the server-authoritative
        /// hunger/thirst/health restore by a fixed amount. Unity never applies the restore
        /// itself — StatInterpolator (ADR-009 L2) only displays what the server reports back.</summary>
        public const string ConsumeItem = "consume_item";
        /// <summary>ADR-032: graceful save-on-quit. Sent by NetworkInitializer during teardown
        /// (before it force-kills the backend process) so the host persists the world immediately
        /// instead of waiting for the 3-min autosave timer. The backend saves synchronously (if
        /// host) and then exits the process.</summary>
        public const string SaveAndShutdown = "save_and_shutdown";
        /// <summary>ADR-032 amendment: debounced on-change report of the REAL STP inventory
        /// (every container, raw item ids) so world persistence can save it — the legacy Rust
        /// inventory is disconnected from the game. Trust-the-client, same level as
        /// report_death_loot. Sent by InventoryReporter; restored via the inventory_restored
        /// event consumed by InventoryRestorer.</summary>
        public const string ReportInventory = "report_inventory";

        /// ADR-041: a noise at a position, with a loudness in METRES (that radius is the whole
        /// contract — the backend never interprets weapon types, so the weapon table stays here
        /// where the weapon definitions live). Perception stimulus only: it mutates no game state.
        public const string ReportNoise = "report_noise";

        /// ADR-050: the local player struggled out of a grab. Carries NO data — the victim is
        /// whoever's backend this is, so there is nothing to send and nothing to forge. The backend
        /// decides whether that actually frees anyone and answers with `phantom_grab_release`.
        public const string ReportStruggle = "report_struggle";

        /// <summary>ADR-045 Fase 1: the client's own identity key ("uuid:{guid}" normally, or a
        /// PLAYER_IDENTITY_KEY env-var override for local multi-instance playtesting — see
        /// PlayerIdentity), sent to its own backend so it can resolve which player file to
        /// load/write. IPC only, never P2P — the backend never relays this to peers.</summary>
        public const string SetIdentity = "set_identity";

        /// <summary>ADR-093 E3: cross a Level 4 door (entry or return). IPC-only, no wire
        /// bump — same additive-data pattern as bed_constructed/report_noise.</summary>
        public const string Level4Door = "level4_door";
    }
}
