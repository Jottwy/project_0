using System.Runtime.CompilerServices;
using PolymindGames.ProceduralMotion;
using UnityEngine;

namespace PolymindGames
{
    [AddComponentMenu("Polymind Games/Damage/Explosion")]
    public sealed class ExplosionEffect : MonoBehaviour
    {
        [SerializeField, Range(0f, 1000f)]
        private float _force = 20f;

        [SerializeField, Range(0f, 1000f)]
        private float _damage = 90f;

        [SerializeField, Range(0f, 100f)]
        private float _radius = 7f;

        [SerializeField]
        private LayerMask _layerMask = LayerConstants.DamageableMask;

        [Title("Effects")]
        [SerializeField]
        private AudioData _audio = new(null);

        [SerializeField]
        private ParticleSystem _particles;

        [SerializeField]
        private LightEffect _lightEffect;

        [SerializeField]
        private ShakeData _shakeData;

        public void Detonate() => Detonate(null);

        public void Detonate(IDamageSource source)
        {
            Vector3 position = transform.position;
            ApplyExplosion(source, position);
            StartEffects(position);
        }

        private void ApplyExplosion(IDamageSource source, Vector3 position)
        {
            int collidersCount = PhysicsUtility.OverlapSphereOptimized(
                position,
                _radius,
                out var colliders,
                _layerMask,
                QueryTriggerInteraction.Collide
            );

            for (var i = 0; i < collidersCount; i++)
            {
                Collider col = colliders[i];
                HandleDamage(col, source);
                HandleImpact(col, position);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void HandleDamage(Collider col, IDamageSource source)
        {
            if (col.TryGetComponent(out IDamageHandler handler))
            {
                (float damage, DamageArgs context) = CalculateDamage(col.transform, source);
                handler.HandleDamage(damage, context);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void HandleImpact(Collider col, Vector3 position)
        {
            if (col.TryGetComponent(out IDamageImpactHandler impactHandler))
            {
                Vector3 hitPoint = impactHandler.transform.position;
                float forceMultiplier = (1f - Vector3.Distance(hitPoint, position) / _radius) * _force;
                Vector3 hitForce = (hitPoint - position + Vector3.up).normalized * forceMultiplier;

                impactHandler.HandleImpact(hitForce, hitForce);
            }
            else if (col.attachedRigidbody != null)
            {
                col.attachedRigidbody.AddExplosionForce(_force, position, _radius, 1f, ForceMode.Impulse);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (float, DamageArgs) CalculateDamage(Transform hit, IDamageSource source)
        {
            Vector3 hitPosition = hit.position;
            Vector3 explosionCenter = transform.position;

            float sqrDistance = (explosionCenter - hitPosition).sqrMagnitude;
            float sqrRadius = _radius * _radius;

            float distanceFactor = 1f - Mathf.Clamp01(sqrDistance / sqrRadius);
            float damage = _damage * distanceFactor;

            DamageArgs args = new(
                DamageType.Explosive,
                source,
                hitPosition,
                (hitPosition - explosionCenter).normalized * _force
            );

            return (damage, args);
        }

        private void OnEnable() => StopEffects();

        private void StartEffects(Vector3 position)
        {
            if (_lightEffect != null)
                _lightEffect.Play();
            _particles.Play(true);
            ShakeZone.PlayOneShotAtPosition(_shakeData, position, _radius * 2f);
            
            AudioManager.Instance.PlayClip3D(_audio, position).minDistance = _radius;
        }

        private void StopEffects()
        {
            if (_lightEffect != null)
                _lightEffect.Stop();
            _particles.Stop(true);
        }

        #region Editor
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, _radius);
            Gizmos.color = Color.white;
        }
#endif
        #endregion
    }
}