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

        [Tooltip("ADR-108 D4 — un perfil por PAPEL del espacio (style 0-6: 0 sin papel propio, " +
                 "1 espina, 2 pasillo/cruce, 3 nave, 4 servicio/almacen, 5 callejon, 6 escalera). " +
                 "Es lo que usa WorldGen3, donde zone_kind ya no existe. VACIO NO ES UN FALLO: " +
                 "sin autorar, se sirven los valores por defecto del codigo, que es justo lo " +
                 "contrario de lo que pasa con 'profiles' (ese asset ya esta serializado y sus " +
                 "defaults de codigo no aplican). Rellenarlo aqui es autorado de contenido.")]
        public ZoneLootProfile[] styleProfiles = System.Array.Empty<ZoneLootProfile>();

        /// <summary>
        /// ADR-108 D4 — perfil por PAPEL. Un array vacío cae a los valores por defecto del código
        /// (<see cref="ChunkLootRoll.DefaultStyleLootProfiles"/>), NO al perfil 0: el asset se
        /// serializó antes de que este campo existiera, así que vacío es el estado normal hasta que
        /// alguien lo autore, y servir un único perfil para los siete papeles anularía en silencio
        /// toda la decisión. Fuera de rango se CLAMPA, igual que <see cref="Profile"/>.
        /// </summary>
        public ZoneLootProfile ProfileForStyle(int style)
        {
            ZoneLootProfile[] table = styleProfiles == null || styleProfiles.Length == 0
                ? ChunkLootRoll.DefaultStyleLootProfiles()
                : styleProfiles;
            int i = Mathf.Clamp(style, 0, table.Length - 1);
            return table[i];
        }

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
