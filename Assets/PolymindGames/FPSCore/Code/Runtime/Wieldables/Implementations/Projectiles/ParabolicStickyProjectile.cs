using PolymindGames.SurfaceSystem;
using UnityEngine;
using System;
using UnityEngine.Serialization;

namespace PolymindGames.WieldableSystem
{
    using Random = UnityEngine.Random;

    /// <summary>
    /// A projectile behavior that allows the projectile to stick to surfaces upon impact and includes a trail and penetration effect.
    /// </summary>
    public class ParabolicStickyProjectile : ParabolicProjectileBehaviour, IDamageHandler
    {   
        [SerializeField, NotNull, Title("References")]
        private Rigidbody _rigidbody;
        
        [SerializeField, NotNull]
        private Collider _collider;

        [SerializeField, NotNull]
        [Tooltip("Reference to the TrailRenderer component used to render the trail effect.")]
        private TrailRenderer _trailRenderer;

        [SerializeField, Range(0, 1f), SpaceArea]
        [Tooltip("Delay in seconds before the trail is enabled after the projectile is launched.")]
        private float _visualsEnableDelay = 0.15f;

        [Title("Penetration")]
        [SerializeField, Range(0, 1f)]
        [Tooltip("Strength of the penetration effect when the projectile hits a surface.")]
        private float _penetrationStrength = 0.3f;

        [SerializeField, Range(0, 10f)]
        [Tooltip("Offset distance for the penetration effect, adjusting how deep the projectile appears to penetrate a surface.")]
        private float _penetrationOffset = 0.15f;

        [SerializeField, Range(0f, 100f)]
        private float _minimumMass = 3f;

        [FormerlySerializedAs("_twangSettings")]
        [SerializeField]
        [Tooltip("Settings for the twang effect applied when the projectile hits a surface.")]
        private TwangEffect _twangEffect;

        private SurfaceHitType _hitSurface;
        private Transform _penetratedTransform;
        private Vector3 _penetrationPositionOffset;
        private Quaternion _penetrationRotationOffset;

        /// <summary>
        /// Sticks the projectile into the hit object.
        /// </summary>
        /// <param name="hit">The RaycastHit information from the hit.</param>
        public void StickProjectile(RaycastHit hit)
        {
            CachedTransform.position = hit.point + CachedTransform.forward * _penetrationOffset;
            
            _penetratedTransform = hit.collider.transform;
            _penetrationPositionOffset = _penetratedTransform.InverseTransformPoint(CachedTransform.position);
            _penetrationRotationOffset = Quaternion.Inverse(_penetratedTransform.rotation) * CachedTransform.rotation;

            _hitSurface = _penetratedTransform.gameObject.isStatic ? SurfaceHitType.Static : SurfaceHitType.Dynamic;

            // Animate the projectile if necessary.
            _twangEffect.StartEffect(CachedTransform);

            Update();
        }
        
        /// <summary>
        /// Unsticks the projectile from any surface it is attached to.
        /// Resets the collider and rigidbody settings.
        /// </summary>
        public void UnstickProjectile()
        {
            Vector3 forward = CachedTransform.forward;
            CachedTransform.position -= forward * _penetrationOffset;
            
            _collider.isTrigger = false;
            _rigidbody.isKinematic = false;
            _hitSurface = SurfaceHitType.None;
            
            _rigidbody.AddForce(-forward, ForceMode.Impulse);
            _rigidbody.AddTorque(Random.rotation.eulerAngles * 15f, ForceMode.Impulse);
        }

        /// <summary>
        /// Called when the projectile is launched.
        /// Initializes the trail, collider, and rigidbody settings.
        /// </summary>
        protected override void OnLaunched()
        {
            // Set the scale to zero to avoid visual artefacts and interpolate it back to one
            CachedTransform.localScale = Vector3.zero;
            
            // Disable the trail renderer.
            _trailRenderer.emitting = false;
            
            // Set rigidbody to kinematic and disable the collider.
            _rigidbody.isKinematic = true;
            _collider.enabled = false;
            _collider.isTrigger = true;

            // Reset penetration-related properties.
            _penetratedTransform = null;
            _hitSurface = SurfaceHitType.None;

            _twangEffect.ClearEffect();
        }

        /// <summary>
        /// Called when the projectile hits an object.
        /// Handles different hit scenarios and updates the projectile's state accordingly.
        /// </summary>
        /// <param name="hit">The RaycastHit information from the hit.</param>
        protected override void OnHit(in RaycastHit hit)
        {
            // Make sure the scale is back to normal
            CachedTransform.localScale = Vector3.one;
            
            // Stop the trail from emitting.
            _trailRenderer.emitting = false;

            // Update the position based on the hit point and penetration offset.
            _collider.enabled = true;

            // Get the surface definition from the hit.
            var surface = SurfaceManager.Instance.GetSurfaceFromHit(in hit);

            // Handle hit scenarios.
            if (HandleSurfacePenetration(in hit, surface))
                return;

            // Stick the projectile to the surface.
            StickProjectile(hit);

            // Handles the case where the projectile hits a surface with penetration resistance.
            // Returns: True if the surface penetration was handled, otherwise false.
            bool HandleSurfacePenetration(in RaycastHit hit, SurfaceDefinition surfaceDef)
            {
                if (surfaceDef.PenetrationResistance > _penetrationStrength)
                {
                    UnstickProjectile();
                    return true;
                }

                var rigidB = hit.rigidbody;
                if (rigidB != null && rigidB.mass < _minimumMass)
                {
                    UnstickProjectile();
                    return true;
                }
                
                return false;
            }
        }

        /// <summary>
        /// Initializes the projectile components.
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            _trailRenderer.emitting = false;
        }

        /// <summary>
        /// Updates the projectile state each frame.
        /// </summary>
        protected override void Update()
        {
            base.Update();

            if (InAir)
            {
                InterpolateScale();
                return;
            }

            switch (_hitSurface)
            {
                case SurfaceHitType.None:
                    break;
                case SurfaceHitType.Dynamic:
                    UpdateDynamicHit();
                    break;
                case SurfaceHitType.Static:
                    UpdateStaticHit();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void InterpolateScale()
        {
            float t = Time.time.Normalize(StartTime, StartTime + _visualsEnableDelay);
            CachedTransform.localScale = Vector3.Lerp(Vector3.zero, Vector3.one, t);
            
            if (t > 0.99f && !_trailRenderer.emitting)
            {
                _trailRenderer.Clear();
                _trailRenderer.emitting = true;
            }
        }

        /// <summary>
        /// While the penetrated transform is not null this projectile will stick to it and apply a twang animation.
        /// </summary>
        private void UpdateDynamicHit()
        {
            if (_penetratedTransform != null && _penetratedTransform.gameObject.activeSelf)
            {
                Vector3 position = _penetratedTransform.position + _penetratedTransform.TransformVector(_penetrationPositionOffset);
                Quaternion rotation = _penetratedTransform.rotation * _penetrationRotationOffset * _twangEffect.GetRotation();

                CachedTransform.SetPositionAndRotation(position, rotation);
            }
            else
                UnstickProjectile();
        }

        /// <summary>
        /// Apply a twang animation.
        /// </summary>
        private void UpdateStaticHit()
        {
            if (!_twangEffect.IsPlaying())
                return;

            Vector3 position = _penetratedTransform.position + _penetratedTransform.TransformVector(_penetrationPositionOffset);
            Quaternion rotation = _penetratedTransform.rotation * _penetrationRotationOffset * _twangEffect.GetRotation();
            CachedTransform.SetPositionAndRotation(position, rotation);
        }
        
        #region IDamageReceiver Implementation
        ICharacter IDamageHandler.Character => Character;

        DamageResult IDamageHandler.HandleDamage(float damage, in DamageArgs args)
        {
            if (_hitSurface != SurfaceHitType.None)
            {
                UnstickProjectile();
                return DamageResult.Normal;
            }

            return DamageResult.Ignored;
        }
        #endregion

        #region Internal Types
        /// <summary>
        /// Struct to hold the settings for the twang effect.
        /// </summary>
        [Serializable]
        private sealed class TwangEffect
        {
            [FormerlySerializedAs("Duration")]
            [SerializeField, Range(0f, 10f)]
            [Tooltip("Duration of the twang effect in seconds.")]
            private float _effectDuration;

            [FormerlySerializedAs("Range")]
            [SerializeField, Range(0f, 10f)]
            [Tooltip("Range of the rotation applied during the twang effect.")]
            private float _initialRotationRange;

            [FormerlySerializedAs("_audio")]
            [FormerlySerializedAs("Audio")]
            [SerializeField]
            [Tooltip("Audio configuration for the twang sound.")]
            private AudioData _effectAudio;

            private float _currentVelocity;
            private float _currentRotationRange;
            private float _effectEndTime;

            /// <summary>
            /// Starts the twang effect with audio and initializes its state.
            /// </summary>
            /// <param name="transform">Transform of the object applying the effect.</param>
            public void StartEffect(Transform transform)
            {
                // Play the twang audio at the given position.
                AudioManager.Instance.PlayClip3D(_effectAudio, transform.position);

                // Initialize rotation range and calculate the effect's stop time.
                _effectEndTime = Time.time + _effectDuration;
                _currentRotationRange = _initialRotationRange;
                _currentVelocity = 0f;
            }

            /// <summary>
            /// Calculates the current rotation for the twang effect.
            /// Returns identity when the effect has ended.
            /// </summary>
            /// <returns>The interpolated rotation.</returns>
            public Quaternion GetRotation()
            {
                if (!IsPlaying())
                {
                    _currentRotationRange = 0f;
                    return Quaternion.identity;
                }

                // Compute random rotational offsets within the current range.
                Quaternion randomRotation = Quaternion.Euler(
                    Random.Range(-_currentRotationRange, _currentRotationRange),
                    Random.Range(-_currentRotationRange, _currentRotationRange),
                    0f
                );

                // Gradually reduce the rotation range for a smoother stop.
                float remainingTime = Mathf.Max(0f, _effectEndTime - Time.time);
                _currentRotationRange = Mathf.SmoothDamp(_currentRotationRange, 0f, ref _currentVelocity, remainingTime);

                return randomRotation;
            }

            /// <summary>
            /// Checks whether the twang effect has completed.
            /// </summary>
            /// <returns>True if the effect duration has passed; otherwise, false.</returns>
            public bool IsPlaying() => Time.time < _effectEndTime;

            public void ClearEffect() => _effectEndTime = 0f;
        }

        /// <summary>
        /// Enum to define the type of surface hit by the projectile.
        /// </summary>
        private enum SurfaceHitType
        {
            [Tooltip("No surface hit.")]
            None,

            [Tooltip("A dynamic (moving) surface hit.")]
            Dynamic,

            [Tooltip("A static (non-moving) surface hit.")]
            Static
        }
		#endregion

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_trailRenderer == null)
            {
                _trailRenderer = GetComponentInChildren<TrailRenderer>();
                if (_trailRenderer == null)
                    _trailRenderer = gameObject.AddComponent<TrailRenderer>();
            }

            if (_rigidbody == null)
                _rigidbody = GetComponentInChildren<Rigidbody>();

            if (_collider == null)
                _collider = GetComponentInChildren<Collider>();
        }
#endif
        #endregion
    }
}