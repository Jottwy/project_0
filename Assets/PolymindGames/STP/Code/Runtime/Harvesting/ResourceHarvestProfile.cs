using UnityEngine;
using System;

namespace PolymindGames.ResourceHarvesting
{
    /// <summary>
    /// Defines how effective a tool is at harvesting a specific type of resource.
    /// </summary>
    [Serializable]
    public sealed class ResourceHarvestProfile
    {
        [Tooltip("Type of resources this tool can harvest effectively.")]
        public HarvestableResourceType ResourceType;

        [Range(0f, 1f)]
        [Tooltip("The strength of the harvesting action, affecting how effective the harvesting is.")]
        public float HarvestPower = 0.2f;

        [Range(0f, 1f)]
        [Tooltip("The amount of resource to be harvested in one action.")]
        public float YieldPerHit = 0.2f;
    }
}