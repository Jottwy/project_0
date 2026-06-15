using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// A class responsible for mixing multiple motion effects applied to a target transform.
    /// The motions can be blended based on the specified weight multiplier, and it supports various mixing modes.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault2)]
    public sealed class MotionMixer : MonoBehaviour, IMotionMixer
    {
        [SerializeField, InLineEditor]
        [Tooltip("The target transform to apply mixed motions to.")]
        private Transform _targetTransform;

        [SerializeField]
        [Tooltip("The mixing mode that defines how motions are blended together.")]
        private MixMode _mixMode = MixMode.FixedLerpUpdate;

        [SerializeField, Title("Offset")]
        [Tooltip("The pivot offset applied when mixing motions.")]
        private Vector3 _pivotOffset;

        [SerializeField]
        [Tooltip("The position offset applied when mixing motions.")]
        private Vector3 _positionOffset;

        [SerializeField]
        [Tooltip("The rotation offset applied when mixing motions.")]
        private Vector3 _rotationOffset;

        private readonly List<IMixedMotion> _motions = new();
        private readonly Dictionary<Type, IMixedMotion> _motionsDict = new();

        private Vector3 _startPosition = Vector3.zero;
        private Vector3 _targetPosition = Vector3.zero;
        private Quaternion _startRotation = Quaternion.identity;
        private Quaternion _targetRotation = Quaternion.identity;
        private float _weightMultiplier = 1f;
        private bool _hasTargetTransform;

        /// <inheritdoc/>
        public Transform TargetTransform => _targetTransform;

        /// <inheritdoc/>
        public float WeightMultiplier
        {
            get => _weightMultiplier;
            set
            {
                value = Mathf.Clamp01(value);
                _weightMultiplier = value;

                // Update the multiplier for each motion
                foreach (var motion in _motions)
                    motion.Multiplier = value;
            }
        }

        /// <inheritdoc/>
        public Vector3 PivotOffset => _pivotOffset;

        /// <inheritdoc/>
        public Vector3 PositionOffset => _positionOffset;

        /// <inheritdoc/>
        public Vector3 RotationOffset => _rotationOffset;

        /// <inheritdoc/>
        public void ResetMixer(Transform targetTransform, Vector3 pivotOffset, Vector3 positionOffset, Vector3 rotationOffset)
        {
            _targetTransform = targetTransform;
            _pivotOffset = pivotOffset;
            _positionOffset = positionOffset;
            _rotationOffset = rotationOffset;
            _hasTargetTransform = targetTransform != null;
        }

        /// <inheritdoc/>
        public bool TryGetMotion<T>(out T motion) where T : class, IMixedMotion
        {
            if (_motionsDict.TryGetValue(typeof(T), out var mixedMotion))
            {
                motion = (T)mixedMotion;
                return true;
            }

            motion = null;
            return false;
        }

        /// <inheritdoc/>
        public T GetMotion<T>() where T : class, IMixedMotion
        {
            if (_motionsDict.TryGetValue(typeof(T), out var mixedMotion))
                return (T)mixedMotion;

            Debug.LogError($"No motion of type ''{nameof(T)}'' found, use ''{nameof(TryGetMotion)}'' instead if the expected motion can be null.");
            return null;
        }

        /// <inheritdoc/>
        public void AddMotion(IMixedMotion motion)
        {
            if (motion == null)
                return;

            var motionType = motion.GetType();
            if (!_motionsDict.ContainsKey(motionType))
            {
                _motionsDict.Add(motionType, motion);
                _motions.Add(motion);
                motion.Multiplier = _weightMultiplier;
            }
        }

        /// <inheritdoc/>
        public void RemoveMotion(IMixedMotion motion)
        {
            if (motion == null)
                return;

            if (_motionsDict.Remove(motion.GetType()))
                _motions.Remove(motion);
        }

        private void Awake()
        {
            _hasTargetTransform = _targetTransform != null;
        }

        private void Update()
        {
            if (!_hasTargetTransform)
                return;
            
            switch (_mixMode)
            {
                case MixMode.FixedLerpUpdate:
                    UpdateInterpolation();
                    return;
                case MixMode.Update:
                    UpdateMotions(false, Time.deltaTime);
                    break;
            }
        }

        private void LateUpdate()
        {
            if (!_hasTargetTransform)
                return;
            
            switch (_mixMode)
            {
                case MixMode.FixedLerpLateUpdate:
                    UpdateInterpolation();
                    return;
                case MixMode.LateUpdate:
                    UpdateMotions(false, Time.deltaTime);
                    break;
            }
        }

        private void FixedUpdate()
        {
            if (!_hasTargetTransform)
                return;
        
            switch (_mixMode)
            {
                case MixMode.FixedUpdate:
                    UpdateMotions(false, Time.fixedDeltaTime);
                    break;
                case MixMode.FixedLerpUpdate:
                case MixMode.FixedLerpLateUpdate:
                    UpdateMotions(true, Time.fixedDeltaTime);
                    break;
            }
        }

        private void UpdateInterpolation()
        {
            float delta = Time.time - Time.fixedTime;
            if (delta < Time.fixedDeltaTime)
            {
                float t = delta / Time.fixedDeltaTime;
                Vector3 targetPosition = Vector3.Lerp(_startPosition, _targetPosition, t);
                Quaternion targetRotation = Quaternion.Lerp(_startRotation, _targetRotation, t);
                _targetTransform.SetLocalPositionAndRotation(targetPosition, targetRotation);
            }
            else
            {
                _targetTransform.SetLocalPositionAndRotation(_targetPosition, _targetRotation);
            }
        }

        private void UpdateMotions(bool lerp, float deltaTime)
        {
            Vector3 targetPos = _pivotOffset;
            Quaternion targetRot = Quaternion.identity;

            foreach (var motion in _motions)
            {
                motion.UpdateMotion(deltaTime);
                targetPos += targetRot * motion.GetPosition(deltaTime);
                targetRot *= motion.GetRotation(deltaTime);
            }

            targetPos = targetPos - targetRot * _pivotOffset + _positionOffset;
            targetRot *= Quaternion.Euler(_rotationOffset);

            if (lerp)
            {
                _startPosition = _targetPosition;
                _startRotation = _targetRotation;
                _targetPosition = targetPos;
                _targetRotation = targetRot;
            }
            else
            {
                _targetTransform.SetLocalPositionAndRotation(targetPos, targetRot);
            }
        }

        #region Internal Types
        private enum MixMode
        {
            Update = 1,
            LateUpdate = 2,
            FixedUpdate = 3,
            FixedLerpUpdate = 4,
            FixedLerpLateUpdate = 5
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!Application.isPlaying && _targetTransform != null)
                _targetTransform.SetLocalPositionAndRotation(_positionOffset, Quaternion.Euler(_rotationOffset));
        }

        private void Reset() => _targetTransform = transform;

        private void OnDrawGizmos()
        {
            Color pivotColor = new(0.1f, 1f, 0.1f, 0.5f);
            const float PivotRadius = 0.08f;

            var prevColor = UnityEditor.Handles.color;
            UnityEditor.Handles.color = pivotColor;
            UnityEditor.Handles.SphereHandleCap(0, transform.TransformPoint(_pivotOffset), Quaternion.identity, PivotRadius, EventType.Repaint);
            UnityEditor.Handles.color = prevColor;
        }
#endif
        #endregion
    }
}