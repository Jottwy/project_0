using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Pieza 3 — per-zone loot profile table, one <see cref="ZoneLootProfile"/> per
    /// zone_kind (backend/src/world/chunk/surface_profiles.rs ZONE_* constants, indices 0-12).
    /// Consumed by <see cref="Net.ChunkLootManager"/>, which resolves a chunk's zone_kind via
    /// <see cref="ZoneRegistry"/> and looks up the matching profile here before calling
    /// <see cref="ChunkLootRoll.RollItems"/>/<see cref="ChunkLootRoll.RollCarryables"/>.
    ///
    /// First pass (approved plan): a simple 1:1 mapping so the effect is OBSERVABLE in playtest,
    /// not a final design — see <see cref="ChunkLootRoll.DefaultZoneLootProfiles"/> for the values
    /// and docs/STATE.md Pieza 3 for the reasoning. The slot COUNT per cache/zone (ItemsPerCache /
    /// CarryablesPerZone) is NOT part of the profile — see ZoneLootProfile's doc-comment for why.
    ///
    /// Mirrors LayerVisualConfig.zoneTints (same index space, same bounds-safe accessor shape) but
    /// created GripPoseSet-style by its companion editor menu: crear-si-falta, never re-seeds an
    /// existing asset, so a hand-tuned rebalance survives re-running the creator (unlike
    /// BackroomsLayerVisualsCreator, which overwrites every field in place — deliberately NOT
    /// mirrored here).
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/Zone Loot Table", fileName = "ZoneLootTable")]
    public sealed class ZoneLootTable : ScriptableObject
    {
        [Tooltip("One profile per zone_kind, index 0-12 (ZONE_NORMAL..ZONE_OFFICE — backend/src/" +
                 "world/chunk/surface_profiles.rs). CLAMPED, not wrapped: a null/empty array " +
                 "falls back to ZONE_NORMAL's profile, but an array that is merely TOO SHORT " +
                 "silently serves its LAST entry to every higher zone_kind. Adding a zone_kind " +
                 "in Rust means adding a row HERE too, in this asset — the C# default only " +
                 "applies to an asset that has never been serialized.")]
        public ZoneLootProfile[] profiles = ChunkLootRoll.DefaultZoneLootProfiles();

        /// <summary>Bounds-safe lookup. A null/empty array falls back to profile 0 (NORMAL); an
        /// out-of-range index is CLAMPED, so a short array serves its last entry — see the
        /// tooltip on <see cref="profiles"/>, this is a silent-degradation trap, not a fallback.</summary>
        public ZoneLootProfile Profile(int zoneKind)
        {
            if (profiles == null || profiles.Length == 0)
                return ZoneLootProfile.Default;
            int i = Mathf.Clamp(zoneKind, 0, profiles.Length - 1);
            return profiles[i];
        }
    }
}
