using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Network instance id for a replicated STP building piece (Phase B1). Mirrors
    /// <see cref="NetworkItemInstance"/>: STP placed pieces only carry a TYPE id
    /// (BuildingPieceDefinition.Id), so this companion gives each spawned instance a
    /// stable, host-assigned network id used for reconciliation by StpBuildingReplicator.
    /// </summary>
    public sealed class NetworkBuildingInstance : MonoBehaviour
    {
        public uint id;
    }
}
