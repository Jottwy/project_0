using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Network instance id for a replicated STP world item (Phase 1). STP item pickups
    /// only carry a TYPE id (DataIdReference&lt;ItemDefinition&gt;) and are pooled, so this
    /// companion gives each spawned instance a stable, host-assigned network id used for
    /// reconciliation (spawn now; pickup/drop sync in later phases).
    /// </summary>
    public sealed class NetworkItemInstance : MonoBehaviour
    {
        public uint id;
    }
}
