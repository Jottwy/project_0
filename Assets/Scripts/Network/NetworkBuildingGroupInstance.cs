using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase B3: network identity for a reconstructed STP <c>BuildingPieceGroup</c>. All
    /// replicated pieces sharing a host-assigned <c>group_id</c> are bucketed under one group
    /// GameObject tagged with this companion, so a player chaining a new piece onto the
    /// structure reads the group's id here and reports it back to the host (keeping the whole
    /// structure in one group across every client). Mirrors <see cref="NetworkBuildingInstance"/>
    /// (which tags individual pieces). Added by <see cref="StpBuildingReplicator"/>.
    /// </summary>
    public sealed class NetworkBuildingGroupInstance : MonoBehaviour
    {
        public uint groupId;
    }
}
