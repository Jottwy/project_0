using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Network instance id for a replicated STP world carryable (Phase B2.5). Mirrors
    /// <see cref="NetworkItemInstance"/>: replicated carryables carry a host-assigned id so
    /// StpCarryableReplicator can reconcile them and the drop watcher can tell replicated
    /// carryables apart from freshly-dropped ones.
    /// </summary>
    public sealed class NetworkCarryableInstance : MonoBehaviour
    {
        public uint id;
    }
}
