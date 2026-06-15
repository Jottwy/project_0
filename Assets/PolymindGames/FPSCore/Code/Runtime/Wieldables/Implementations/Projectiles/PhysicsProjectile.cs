using UnityEngine.Events;
using UnityEngine;
using System;
using UnityEngine.Serialization;

namespace PolymindGames.WieldableSystem
{
    public sealed class PhysicsProjectile : PhysicsProjectileBehaviour
    {
        [SerializeField]
        private DetonationMode _detonationMode;

        [FormerlySerializedAs("_detonateDelay")]
        [SerializeField, Range(0f, 100f)]
        [HideIf(nameof(_detonationMode), DetonationMode.OnImpact)]
        private float _detonationDelay = 5f;

        [SerializeField]
        private DetonationFlags _detonationFlags;

        [SerializeField, Range(0f, 1f)]
        private float _impactVelocityMultiplier = 0.5f;

        [SerializeField, SpaceArea]
        private TrailRenderer _trailRenderer;

        [SerializeField]
        private AudioData _impactAudio = new(null);

        [SerializeField, SpaceArea]
        private UnityEvent<ICharacter> _launchEvent;

        [SerializeField]
        private UnityEvent<ICharacter> _detonateEvent;

        private Collider _collider;

        protected override void OnLaunched()
        {
            if ((_detonationFlags & DetonationFlags.DisableCollider) != 0)
                _collider.enabled = true;

            if (_trailRenderer != null)
            {
                _trailRenderer.Clear();
                _trailRenderer.emitting = true;
            }

            _launchEvent.Invoke(Character);

            if (_detonationMode == DetonationMode.AfterDelay)
                CoroutineUtility.InvokeDelayed(this, Detonate, _detonationDelay);
        }

        protected override void OnHit(Collision hit)
        {
            AudioManager.Instance.PlayClip3D(_impactAudio, hit.GetContact(0).point);

            Rigidbody.linearVelocity *= _impactVelocityMultiplier;
            Rigidbody.angularVelocity *= _impactVelocityMultiplier;
            
            if (_trailRenderer != null)
                _trailRenderer.emitting = false;

            switch (_detonationMode)
            {
                case DetonationMode.OnImpact:
                    Detonate();
                    break;
                case DetonationMode.OnImpactAfterDelay:
                    CoroutineUtility.InvokeDelayed(this, Detonate, _detonationDelay);
                    break;
                case DetonationMode.AfterDelay:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        protected override void Awake()
        {
            base.Awake();
            _collider = GetComponent<Collider>();
            if (_trailRenderer != null)
                _trailRenderer.emitting = false;
        }

        private void Detonate()
        {
            if ((_detonationFlags & DetonationFlags.DisableCollider) != 0)
                _collider.enabled = false;

            if ((_detonationFlags & DetonationFlags.FreezeRigidbody) != 0)
                Rigidbody.isKinematic = true;

            _detonateEvent.Invoke(Character);
        }

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            _trailRenderer = GetComponentInChildren<TrailRenderer>();
            if (_trailRenderer == null)
                _trailRenderer = gameObject.AddComponent<TrailRenderer>();
        }
#endif
        #endregion

        #region Internal Types
        private enum DetonationMode
        {
            AfterDelay,
            OnImpact,
            OnImpactAfterDelay
        }

        [Flags]
        private enum DetonationFlags
        {
            None = 0,
            FreezeRigidbody = 1,
            DisableCollider = 2
        }
        #endregion
    }
}