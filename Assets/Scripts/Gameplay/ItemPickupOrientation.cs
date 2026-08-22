using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Per-ITEM upright-rotation correction, shared by every place that instantiates an
    /// <c>ItemDefinition.Pickup</c> and wants it to read as standing up rather than however its
    /// mesh happens to rest at <c>Quaternion.identity</c>. Not per-slot, not per-spawn-site: the
    /// same physical item should look the same whether it is on a storage rack shelf
    /// (<c>StorageRackDisplay</c>) or lying on the ground where a player dropped it
    /// (<c>StpItemReplicator</c>) — one wrong-looking bottle report from Joel's playtest covered
    /// both, since it is the same root cause in two spawn sites.
    ///
    /// Measured with Backrooms/Diagnostics/Measure Item Pickup (Assets/Editor/
    /// BackroomsStorageRackProbe.cs, editor-only, not reachable from here): Almond Water rests
    /// long-axis-on-Z (0.072x0.072x0.230 m — correct for lying on the ground as SHIPPED, wrong for
    /// standing on a shelf) and needs -90° on X to stand up; Spray Can already rests
    /// long-axis-on-Y (0.066x0.190x0.066 m) and needs no correction. Add an entry here only for an
    /// item whose pickup shows up lying down — most pickups are expected to already rest upright,
    /// which is why the default is identity, not the exception.
    /// </summary>
    public static class ItemPickupOrientation
    {
        public static Quaternion UprightCorrectionFor(string itemName) => itemName switch
        {
            "Almond Water" => Quaternion.Euler(-90f, 0f, 0f),
            _ => Quaternion.identity,
        };

        /// <summary>
        /// Half the item's own standing height, post-<see cref="UprightCorrectionFor"/> — every
        /// pickup mesh measured so far has its PIVOT AT ITS GEOMETRIC CENTRE, not at its base
        /// (confirmed: Measure Item Pickup reports centre=(0,0,0) for both known items). Spawning at
        /// a slot/anchor's Y with no correction plants the item's MIDDLE there, sinking half of it
        /// below the shelf/ground surface — Joel, 2026-08-22, on both the rack and world drops:
        /// "spawnean... al punto del medio... no es en la base". Add this to the Y position (after
        /// the upright rotation) so the item's BASE lands on the surface instead of its centre.
        /// Almond Water: 0.230 m tall standing → 0.115. Spray Can: 0.190 m tall standing → 0.095.
        /// </summary>
        public static float PivotHalfHeightFor(string itemName) => itemName switch
        {
            "Almond Water" => 0.115f,
            "Spray Can" => 0.095f,
            _ => 0f,
        };
    }
}
