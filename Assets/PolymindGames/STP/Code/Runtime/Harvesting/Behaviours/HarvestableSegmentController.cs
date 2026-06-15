using PolymindGames.SaveSystem;
using UnityEngine;

namespace PolymindGames.ResourceHarvesting
{
    /// <summary>
    /// Manages segments that can be disabled in response to harvesting events. 
    /// Enables or disables segments based on the harvested amount.
    /// </summary>
    public sealed class HarvestableSegmentController : MonoBehaviour, ISaveableComponent
    {
        private enum SegmentDisableType
        {
            Position,
            Normal
        }

        [SerializeField]
        private SegmentDisableType _segmentDisableType = SegmentDisableType.Position;
    
        [SpaceArea]
        [SerializeField, ReorderableList(HasLabels = false)]
        private Transform[] _segments;

        private IHarvestableResource _harvestable;
        private int _disabledSegmentsCount;

        private void Awake()
        {
            _harvestable = GetComponentInParent<IHarvestableResource>();

            if (_harvestable == null)
            {
                Debug.LogError("No parent harvestable found.", gameObject);
                return;
            }
            
            _harvestable.Harvested += OnHarvest;
            _harvestable.Respawned += _ => ToggleAllSegments(true);            
            
            ToggleAllSegments(true);
        }

        /// <summary>
        /// Handles the harvesting event. Disables segments based on the amount harvested.
        /// If fully harvested, disables all segments.
        /// </summary>
        /// <param name="amount">The amount harvested.</param>
        /// <param name="args">Additional information about the damage.</param>
        private void OnHarvest(float amount, in DamageArgs args)
        {
            if (_harvestable.HarvestableState == HarvestableState.FullyHarvested)
            {
                ToggleAllSegments(false);
                return;
            }

            int targetDisabledCount = Mathf.FloorToInt((1f - _harvestable.RemainingHarvestAmount) * _segments.Length);
            int segmentsToDisable = targetDisabledCount - _disabledSegmentsCount;

            if (segmentsToDisable > 0)
                DisableSegments(args.HitPoint, segmentsToDisable);
        }

        /// <summary>
        /// Disables a specific number of segments starting from the ones closest to the given position.
        /// </summary>
        /// <param name="hitPosition">The position from which to find the closest segment.</param>
        /// <param name="amountToDisable">The number of segments to disable.</param>
        private void DisableSegments(Vector3 hitPosition, int amountToDisable)
        {
            for (int i = 0; i < amountToDisable; i++)
            {
                Transform closestSegment = _segmentDisableType switch
                {
                    SegmentDisableType.Position => FindClosestSegmentBasedOnPosition(hitPosition),
                    SegmentDisableType.Normal => FindClosestSegmentBasedOnNormal(hitPosition),
                    _ => null
                };
                    
                if (closestSegment == null)
                    break;

                ToggleSegment(closestSegment, false);
                _disabledSegmentsCount++;
            }
        }

        /// <summary>
        /// Finds the closest active segment to the given world position.
        /// </summary>
        /// <param name="hitPosition">The reference position to compare against.</param>
        /// <returns>The closest segment, or null if all are disabled.</returns>
        private Transform FindClosestSegmentBasedOnPosition(Vector3 hitPosition)
        {
            Transform closestSegment = null;
            float closestDistance = float.PositiveInfinity;

            foreach (var segment in _segments)
            {
                if (!IsSegmentEnabled(segment))
                    continue;

                float distance = Vector3.Distance(segment.position, hitPosition);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestSegment = segment;
                }
            }

            return closestSegment;
        }

        private Transform FindClosestSegmentBasedOnNormal(Vector3 hitPosition)
        {
            float largestAngle = 0f;
            Transform bestSegment = null;
            Vector3 chopPointNormal = (hitPosition - transform.position).WithY(0f).normalized;
            
            Debug.DrawRay(hitPosition, Vector3.down, Color.red, 3f);

            foreach (var segment in _segments)
            {
                if (!IsSegmentEnabled(segment)) continue;

                Vector3 segmentNormal = (transform.position - segment.position).WithY(0f).normalized;
                float angle = Vector3.Angle(chopPointNormal, segmentNormal);

                if (angle > largestAngle)
                {
                    largestAngle = angle;
                    bestSegment = segment;
                }
            }

            return bestSegment;
        }

        /// <summary>
        /// Checks if a segment is enabled.
        /// </summary>
        /// <param name="segment">The segment to check.</param>
        /// <returns>True if the segment is disabled; otherwise, false.</returns>
        private static bool IsSegmentEnabled(Transform segment) => segment.localScale != Vector3.zero;

        /// <summary>
        /// Toggles the active state of a single segment by changing its scale.
        /// </summary>
        /// <param name="segment">The segment to toggle.</param>
        /// <param name="enable">Whether to enable or disable the segment.</param>
        private static void ToggleSegment(Transform segment, bool enable)
        {
            segment.localScale = enable ? Vector3.one : Vector3.zero;
        }

        /// <summary>
        /// Toggles the active state of all segments.
        /// </summary>
        /// <param name="enable">Whether to enable or disable all segments.</param>
        private void ToggleAllSegments(bool enable)
        {
            foreach (var segment in _segments)
                ToggleSegment(segment, enable);

            _disabledSegmentsCount = enable ? 0 : _segments.Length;
        }

        #region Save & Load
        void ISaveableComponent.LoadMembers(object data)
        {
            if (data == null)
                return;

            var segmentEnabledFlags = (bool[])data;
            _disabledSegmentsCount = 0;
            for (int i = 0; i < segmentEnabledFlags.Length; i++)
            {
                if (!segmentEnabledFlags[i])
                {
                    ToggleSegment(_segments[i], false);
                    ++_disabledSegmentsCount;
                }
            }
        }

        object ISaveableComponent.SaveMembers()
        {
            if (_disabledSegmentsCount == 0)
                return null;

            var segmentEnabledFlags = new bool[_segments.Length];
            for (int i = 0; i < segmentEnabledFlags.Length; i++)
                segmentEnabledFlags[i] = IsSegmentEnabled(_segments[i]);

            return segmentEnabledFlags;
        }
        #endregion
    }
}