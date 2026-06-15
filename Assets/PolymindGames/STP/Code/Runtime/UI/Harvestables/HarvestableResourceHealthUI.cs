using PolymindGames.ResourceHarvesting;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Manages the UI for displaying harvestable resource health, controlling visibility and positioning based on player proximity and angle.
    /// </summary>
    public sealed class HarvestableResourceHealthUI : CharacterUIBehaviour
    {
        [SerializeField]
        [Tooltip("Positions the UI element on the screen relative to the world position of the harvestable resource.")]
        private WorldToUIScreenPositioner _uiPositioner;

        [SerializeField]
        [Tooltip("The panel UI that contains the health indicator and related elements.")]
        private UIPanel _healthIndicatorPanel;

        [SerializeField]
        [Tooltip("The image component that displays the icon representing the harvestable resource.")]
        private Image _resourceIconImage;

        [SerializeField]
        [Tooltip("The fill bar UI component that visually represents the health of the harvestable resource.")]
        private ProgressBarUI _healthProgressBar;

        [SerializeField, Range(0.01f, 1f), Title("Settings")]
        [Tooltip("How often should this component search for harvestables.")]
        private float _searchRefreshInterval = 0.1f;

        [SerializeField, Range(0f, 120f)]
        [Tooltip("The angle within which the health indicator is visible relative to the player's view.")]
        private float _visibilityAngle = 60f;

        [SerializeField, Range(0f, 30f)]
        [Tooltip("The maximum distance from the player at which the health indicator is still visible.")]
        private float _visibilityDistance = 5.5f;

        private HarvestableResourceReference _harvestableResource;
        private ResourceHarvestProfile[] _harvestProfiles;
        private Vector3 _previousHarvestPosition;
        private Transform _characterTransform;
        private Coroutine _updateCoroutine;

        private static HarvestableResourceHealthUI _instance;

        /// <summary>
        /// Shows the harvestable resource health indicator UI with the specified resource types.
        /// </summary>
        public static void ShowIndicator(ResourceHarvestProfile[] harvestProfiles)
        {
            if (_instance != null && _instance.enabled)
            {
                _instance.StartDetection(harvestProfiles);
            }
        }

        /// <summary>
        /// Hides the harvestable resource health indicator UI.
        /// </summary>
        public static void HideIndicator()
        {
            if (_instance != null && _instance.enabled)
            {
                _instance.ResetState();
            }
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            base.OnCharacterAttached(character);
            _characterTransform = character.transform;
        }

        protected override void Awake()
        {
            base.Awake();

            // Set this instance as the singleton instance
            _instance = this;
            _uiPositioner.enabled = false;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            // Clear the singleton instance if this object is the current instance
            if (_instance == this)
            {
                _instance = null;
            }
        }
        
        private void StartDetection(ResourceHarvestProfile[] harvestProfiles)
        {
            _harvestProfiles = harvestProfiles;
            CoroutineUtility.StartOrReplaceCoroutine(this, UpdateDetection(), ref _updateCoroutine);
        }
        
        private void ResetState()
        {
            _harvestProfiles = null;
            _harvestableResource = default(HarvestableResourceReference);
            ToggleCanvas(false, Vector3.zero);

            StopAllCoroutines();
        }

        /// <summary>
        /// Regularly checks for the closest harvestable resource and updates the UI accordingly.
        /// </summary>
        private IEnumerator UpdateDetection()
        {
            float timer = 0f;
            while (_harvestProfiles != null)
            {
                if (timer < Time.time)
                {
                    UpdateHarvestableDetection();
                    timer = Time.time + _searchRefreshInterval;
                }

                float fillAmount = _harvestableResource.IsValid ? _harvestableResource.GetRemainingHarvestAmount() : 0f;
                _healthProgressBar.SetFillAmount(fillAmount);
                yield return null;
            }
        }
        
        private void UpdateHarvestableDetection()
        {
            var resource = GetClosestResource(out var harvestPosition);
            if (_harvestableResource == resource)
                return;
            
            if (resource.IsValid)
            {
                _resourceIconImage.sprite = resource.GetResourceDefinition().Icon;
            }

            ToggleCanvas(resource.IsValid, harvestPosition);
            _harvestableResource = resource;
        }

        /// <summary>
        /// Finds the closest harvestable resource within the detection radius and calculates the optimal harvesting point.
        /// </summary>
        /// <param name="harvestPosition">The precise position on the resource where harvesting should occur.</param>
        /// <returns>The context of the closest harvestable resource, or default if none are found.</returns>
        private HarvestableResourceReference GetClosestResource(out Vector3 harvestPosition)
        {
            Vector3 targetPosition = _characterTransform.position + new Vector3(0, 0.3f, 0f);

            // Find colliders within the detection radius
            const int LayerMask = LayerConstants.SimpleSolidObjectsMask;
            int count = PhysicsUtility.OverlapSphereOptimized(targetPosition, _visibilityDistance, out var colliders, LayerMask);

            HarvestableResourceReference closestResource = default(HarvestableResourceReference);
            harvestPosition = Vector3.zero;
            float closestScore = float.MaxValue;

            // Iterate through colliders to find the closest valid harvestable resource
            bool hasCheckedTerrain = false;
            for (int i = 0; i < count; ++i)
            {
                if (colliders[i] is TerrainCollider)
                {
                    if (hasCheckedTerrain)
                    {
                        continue;
                    }
                    
                    hasCheckedTerrain = true;
                }

                var context = HarvestableResourceReference.Create(colliders[i].gameObject, targetPosition, _visibilityDistance);
                if (!context.IsValid || !TryFindProfileWithResourceType(context.ResourceType, out var profile))
                {
                    continue;
                }

                Vector3 harvestCenter = context.GetHarvestBounds().center;
                if (!context.CanHarvestAt(profile.HarvestPower, harvestCenter))
                {
                    continue;
                }

                float distanceToResource = Vector3.Distance(targetPosition, harvestCenter);
                float angleToResource = Vector3.Angle(harvestCenter - targetPosition, _characterTransform.forward);

                // Check if the harvestable is within the distance and angle limits
                if (distanceToResource < _visibilityDistance && angleToResource < _visibilityAngle)
                {
                    // Use a combination of distance and angle to determine the "score" for proximity
                    const float DistanceScoreMod = 1.5f;
                    float score = (distanceToResource / _visibilityDistance * DistanceScoreMod) + angleToResource / _visibilityAngle;

                    if (score < closestScore)
                    {
                        closestResource = context;
                        closestScore = score;
                        harvestPosition = harvestCenter;
                    }
                }
            }

            return closestResource;
        }

        private void ToggleCanvas(bool enable, Vector3 targetPosition)
        {
            if (enable)
            {
                _healthIndicatorPanel.Show();
                _uiPositioner.SetTargetPosition(targetPosition);
            }
            else
            {
                _healthIndicatorPanel.Hide();
                _uiPositioner.SetTargetPosition(null);
            }
        }
        
        private bool TryFindProfileWithResourceType(HarvestableResourceType resourceType, out ResourceHarvestProfile profile)
        {
            foreach (var harvestProfile in _harvestProfiles)
            {
                if (harvestProfile.ResourceType == resourceType)
                {
                    profile = harvestProfile;
                    return true;
                }
            }
            
            profile = null;
            return false;
        }
    }
}