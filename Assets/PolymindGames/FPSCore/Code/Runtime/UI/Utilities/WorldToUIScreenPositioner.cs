using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// UI component that positions and orients a RectTransform to follow a world-space target.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault2)]
    public sealed class WorldToUIScreenPositioner : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Specifies whether to follow a Transform or a specific world position.")]
        private TargetingMode _targetingMode = TargetingMode.FollowTransform;

        [SerializeField]
        [ShowIf(nameof(_targetingMode), TargetingMode.FollowTransform)]
        [Tooltip("The Transform of the 3D object to follow.")]
        private Transform _targetTransform;

        [SerializeField]
        [ShowIf(nameof(_targetingMode), TargetingMode.FollowPosition)]
        [Tooltip("The world position to follow if not following a Transform.")]
        private Vector3 _targetPosition;

        [SerializeField, Title("Settings")]
        private UpdateMode _updateMode = UpdateMode.UpdatePosition;

        [SerializeField]
        private bool _interpolate = true;

        [SerializeField, Range(0f, 5f)]
        [HideIf(nameof(_updateMode), UpdateMode.None, Comparison = UnityComparisonMethod.Equal)]
        [Tooltip("Duration after which the component will be disabled if no target is set.")]
        private float _disableDuration = 0.3f;

        [SerializeField]
        [ShowIf(nameof(_updateMode), UpdateMode.UpdatePosition, Comparison = UnityComparisonMethod.Mask)]
        [Tooltip("Offset applied to the screen position of the UI element.")]
        private Vector3 _offset;

        [SerializeField, Range(0f, 10f)]
        [ShowIf(nameof(_updateMode), UpdateMode.UpdateScale, Comparison = UnityComparisonMethod.Mask)]
        [Tooltip("Minimum scale of the UI element based on distance.")]
        private float _minScale = 0.5f;

        [SerializeField, Range(0f, 10f)]
        [ShowIf(nameof(_updateMode), UpdateMode.UpdateScale, Comparison = UnityComparisonMethod.Mask)]
        [Tooltip("Maximum scale of the UI element based on distance.")]
        private float _maxScale = 1.5f;

        [SerializeField, Range(0f, 100f)]
        [ShowIf(nameof(_updateMode), UpdateMode.UpdateScale, Comparison = UnityComparisonMethod.Mask)]
        [Tooltip("Reference distance at which the UI element is at normal scale (1.0).")]
        private float _referenceDistance = 10f;

        private RectTransform _rectTransformParent;
        private RectTransform _rectTransform;
        private Camera _mainCamera;
        private float _disableTimer;
        private Vector3 _startLerpPosition = Vector3.zero;
        private Vector3 _endLerpPosition = Vector3.zero;
        private Quaternion _startLerpRotation = Quaternion.identity;
        private Quaternion _endLerpRotation = Quaternion.identity;
        private float _startLerpScale = 1f;
        private float _endLerpScale = 1f;

        /// <summary>
        /// Sets the target Transform for the UI element to follow and enables the component if the target is valid.
        /// </summary>
        public void SetTargetTransform(Transform target, Vector3 offset = default(Vector3))
        {
            _targetingMode = TargetingMode.FollowTransform;
            _mainCamera = UnityUtility.CachedMainCamera;
            _targetTransform = target;
            
            if (target != null)
            {
                _targetPosition = offset;
                _disableTimer = float.MaxValue;
                enabled = true;
            }
            else
            {
                _targetPosition = _endLerpPosition;
                _disableTimer = Time.unscaledTime + _disableDuration;
            }
        }

        /// <summary>
        /// Sets the target world position for the UI element to follow and enables the component if the position is valid.
        /// </summary>
        public void SetTargetPosition(Vector3? target)
        {
            _targetingMode = TargetingMode.FollowPosition;
            _mainCamera = UnityUtility.CachedMainCamera;

            if (target.HasValue)
            {
                _targetPosition = target.Value;
                _disableTimer = float.MaxValue;
                enabled = true;
            }
            else
            {
                _disableTimer = Time.unscaledTime + _disableDuration;
            }
        }

        /// <summary>
        /// Initializes RectTransform components and disables the script by default.
        /// </summary>
        private void Awake()
        {
            _rectTransform = (RectTransform)transform;
            _rectTransformParent = (RectTransform)_rectTransform.parent;
            _mainCamera = UnityUtility.CachedMainCamera;
            enabled = false;
        }

        /// <summary>
        /// Updates the transform position at fixed intervals, disabling the component if the timer runs out.
        /// </summary>
        private void FixedUpdate()
        {
            if (_disableTimer < Time.unscaledTime)
            {
                enabled = false;
                return;
            }
            
            CalculateTransformTargets();
        }

        /// <summary>
        /// Interpolates and updates the transform during the frame update.
        /// </summary>
        private void LateUpdate() => UpdateTransform();

        /// <summary>
        /// Calculates the target position, rotation, and scale.
        /// </summary>
        private void CalculateTransformTargets()
        {
            Vector3 targetPosition = GetTargetPosition();
            Quaternion targetRotation = GetTargetRotation(targetPosition);
            float targetScale = GetTargetScale(targetPosition);
            
            _startLerpPosition = _endLerpPosition;
            _startLerpRotation = _endLerpRotation;
            _startLerpScale = _endLerpScale;
            _endLerpPosition = targetPosition;
            _endLerpRotation = targetRotation;
            _endLerpScale = targetScale;
        }

        /// <summary>
        /// Interpolates between the current and target transform values based on delta time.
        /// </summary>
        private void UpdateTransform()
        {
            float delta = Time.time - Time.fixedTime;
            if (delta < Time.fixedDeltaTime && _interpolate)
            {
                float t = delta / Time.fixedDeltaTime;
                Vector3 interpolatedPosition = Vector3.Lerp(_startLerpPosition, _endLerpPosition, t);
                Quaternion interpolatedRotation = Quaternion.Lerp(_startLerpRotation, _endLerpRotation, t);
                float interpolatedScale = Mathf.Lerp(_startLerpScale, _endLerpScale, t);

                ApplyTransform(interpolatedPosition, interpolatedRotation, interpolatedScale);
            }
            else
            {
                ApplyTransform(_endLerpPosition, _endLerpRotation, _endLerpScale);
            }
        }

        /// <summary>
        /// Applies the calculated position, rotation, and scale to the RectTransform.
        /// </summary>
        private void ApplyTransform(Vector3 position, Quaternion rotation, float scale)
        {
            if ((_updateMode & UpdateMode.UpdatePosition) != 0)
            {
                Vector2 screenPosition = _mainCamera.WorldToScreenPoint(position) + _offset;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_rectTransformParent, screenPosition, null, out Vector2 localPosition))
                {
                    _rectTransform.anchoredPosition = localPosition;
                }
            }

            if ((_updateMode & UpdateMode.UpdateRotation) != 0)
                _rectTransform.rotation = rotation;

            if ((_updateMode & UpdateMode.UpdateScale) != 0)
                _rectTransform.localScale = Vector3.one * scale;
        }

        /// <summary>
        /// Retrieves the target position based on the current targeting mode.
        /// </summary>
        private Vector3 GetTargetPosition()
        {
            return _targetingMode switch
            {
                TargetingMode.FollowTransform => _targetTransform != null ? _targetTransform.TransformPoint(_targetPosition) : _targetPosition,
                TargetingMode.FollowPosition => _targetPosition,
                _ => _targetPosition
            };
        }

        /// <summary>
        /// Calculates the target rotation to make the UI element face the camera.
        /// </summary>
        private Quaternion GetTargetRotation(Vector3 worldPosition)
        {
            Vector3 directionToCamera = (_mainCamera.transform.position - worldPosition).normalized;
            return Quaternion.LookRotation(directionToCamera, Vector3.up);
        }

        /// <summary>
        /// Calculates the scale of the UI element based on the distance to the camera.
        /// </summary>
        private float GetTargetScale(Vector3 worldPosition)
        {
            float distanceToCamera = Vector3.Distance(_mainCamera.transform.position, worldPosition);
            return Mathf.Lerp(_maxScale, _minScale, distanceToCamera / _referenceDistance);
        }

        #region Internal Types
        private enum TargetingMode
        {
            FollowTransform,
            FollowPosition
        }

        [Flags]
        private enum UpdateMode
        {
            None = 0,
            UpdatePosition = 1,
            UpdateRotation = 2,
            UpdateScale = 4,
        }
        #endregion
    }
}