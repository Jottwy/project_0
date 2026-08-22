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
    }
}
