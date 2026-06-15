using UnityEngine.Serialization;
using UnityEngine;

namespace PolymindGames.ResourceHarvesting
{
    /// <summary>
    /// Represents a definition for a harvestable resource in the game.
    /// Contains properties such as name, icon, prefab references, and harvesting settings.
    /// </summary>
    [CreateAssetMenu(menuName = "Polymind Games/Harvesting/Harvestable Resource Definition", fileName = "Harvestable_")]
    public sealed class HarvestableResourceDefinition : ScriptableObject
    {
        [SerializeField, NewLabel("Name")]
        [Tooltip("The name of the harvestable resource.")]
        private string _harvestableName;

        [SerializeField, SpritePreview]
        [Tooltip("The icon associated with the harvestable resource.")]
        private Sprite _icon;

        [FormerlySerializedAs("_type"),SerializeField]
        [Tooltip("The type of the harvestable resource.")]
        private HarvestableResourceType _resourceType;

        [FormerlySerializedAs("_requiredStrength"),SerializeField, Range(0f, 1f)]
        [Tooltip("The strength required to successfully gather this item. A value between 0 and 1.")]
        private float _requiredPower = 0.1f;

        [SerializeField, PrefabObjectOnly, Title("References")]
        [Tooltip("The prefab used for the resource.")]
        private HarvestableResource _prefab;

        [SerializeField, Range(0, 100), Title("Settings")]
        [Tooltip("The number of days required for the resource to respawn.")]
        private int _respawnDays = 1;

        [SerializeField]
#if UNITY_EDITOR
        [EditorButton(nameof(GetBoundsFromCollider))]
#endif
        [Tooltip("The bounds used for harvesting interactions.")]
        private Bounds _harvestBounds;
        
        /// <summary>
        /// Gets the name of the harvestable resource.
        /// </summary>
        public string Name => _harvestableName;

        /// <summary>
        /// Gets the icon associated with the harvestable resource.
        /// </summary>
        public Sprite Icon => _icon;

        /// <summary>
        /// Gets the type of the harvestable resource.
        /// </summary>
        public HarvestableResourceType ResourceType => _resourceType;

        /// <summary>
        /// Gets the required strength to interact with this harvestable.
        /// </summary>
        public float RequiredPower => _requiredPower;

        /// <summary>
        /// Gets the bounds used for harvesting interactions.
        /// </summary>
        public Bounds HarvestBounds => _harvestBounds;

        /// <summary>
        /// Gets the prefab associated with the harvestable resource.
        /// </summary>
        public HarvestableResource Prefab => _prefab;

        /// <summary>
        /// Gets the number of days required for the harvestable resource to respawn.
        /// </summary>
        public int RespawnDays => _respawnDays;

        /// <summary>
        /// Checks if the provided harvest power is sufficient to harvest this resource.
        /// </summary>
        /// <param name="providedHarvestPower">The harvest power available (e.g., from a tool).</param>
        /// <returns><c>true</c> if the power is equal to or greater than required; otherwise, <c>false</c>.</returns>
        public bool IsHarvestPowerSufficient(float providedHarvestPower)
            => providedHarvestPower >= _requiredPower;
        
        #region Editor
#if UNITY_EDITOR
        private void GetBoundsFromCollider()
        {
            if (_prefab != null)
            {
                var bounds = _prefab.GetComponentInChildren<MeshRenderer>()?.bounds ?? default(Bounds);
                _harvestBounds = bounds;
            }
        }
#endif
        #endregion
    }
}