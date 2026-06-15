using System.Runtime.CompilerServices;
using PolymindGames.SurfaceSystem;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Standard Impact-Effect")]
    public class FirearmStandardImpactEffect : FirearmImpactEffectBehaviour
    {
        [SerializeField, Title("Damage")]
        [Tooltip("The type of damage inflicted by this projectile.")]
        private DamageType _damageType = DamageType.Ballistic;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("The maximum damage inflicted at close range.")]
        private float _damage = 15f;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("The impact force applied to rigidbodies upon collision.")]
        private float _force = 15f;

        [SerializeField, Title("Falloff")]
        [Tooltip("The type of falloff used for damage and impact force.")]
        private FalloffType _falloffType;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("The minimum threshold for falloff calculation.")]
        private float _minFalloffThreshold = 20f;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("The maximum threshold for falloff calculation.")]
        private float _maxFalloffThreshold = 100f;
        
        public override void TriggerHitEffect(in RaycastHit hit, Vector3 hitDirection, float speed, float travelledDistance)
        {
            // Calculate impulse and damage based on speed and distance.
            (float impulse, float damage) = GetImpulseAndDamage(speed, travelledDistance);

            Vector3 hitForce = hitDirection * impulse;
            Vector3 hitPoint = hit.point;
            Rigidbody rigidB = hit.rigidbody;

            HandleDamageAndImpact(hit.collider, rigidB, damage, hitPoint, hitForce);

            // Spawn visual effects based on the surface hit.
            SurfaceManager.Instance.PlayEffectFromHit(in hit, _damageType.GetSurfaceEffectType(), parentEffects: rigidB != null);
        }

        public override void TriggerHitEffect(Collision collision, float travelledDistance)
        {
            // Calculate impulse and damage based on collision velocity and distance.
            (float impulse, float damage) = GetImpulseAndDamage(collision.relativeVelocity.magnitude, travelledDistance);

            var contact = collision.GetContact(0);
            Vector3 hitForce = -contact.normal * impulse;
            Vector3 hitPoint = contact.point;
            Rigidbody rigidB = collision.rigidbody;

            HandleDamageAndImpact(collision.collider, rigidB, damage, hitPoint, hitForce);

            // Spawn visual effects based on the collision.
            SurfaceManager.Instance.PlayEffectFromCollision(collision, _damageType.GetSurfaceEffectType(), parentEffects: rigidB != null);
        }

        /// <summary>
        /// Handles damage and impact mechanics on the colliding object.
        /// </summary>
        /// <param name="col">Collider of the object hit.</param>
        /// <param name="rigidB">Rigidbody of the object hit.</param>
        /// <param name="damage">Damage amount to apply.</param>
        /// <param name="hitPoint">Point of impact.</param>
        /// <param name="hitForce">Force applied at the point of impact.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void HandleDamageAndImpact(Collider col, Rigidbody rigidB, float damage, Vector3 hitPoint, Vector3 hitForce)
        {
            // Apply damage to any found damage handler.
            if (col.TryGetComponent(out IDamageHandler damageHandler))
                damageHandler.HandleDamage(damage, new DamageArgs(_damageType, Wieldable.Character, hitPoint, hitForce));

            // Apply an impact impulse.
            if (col.TryGetComponent(out IDamageImpactHandler impactHandler))
            {
                impactHandler.HandleImpact(hitPoint, hitForce);
            }
            else if (rigidB != null)
            {
                // If no impact handler, directly apply force to the rigidbody.
                rigidB.AddForceAtPosition(hitForce, hitPoint, ForceMode.Impulse);
            }
        }

        /// <summary>
        /// Calculates the impulse and damage based on the projectile's speed and distance travelled.
        /// </summary>
        /// <param name="speed">Projectile speed or collision velocity.</param>
        /// <param name="distance">Distance travelled by the projectile.</param>
        /// <returns>A tuple containing impulse force and damage.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (float impulse, float damage) GetImpulseAndDamage(float speed, float distance)
        {
            // Calculate the falloff damage and impulse mod.
            float falloffMod = _falloffType switch
            {
                FalloffType.Distance => (distance - _minFalloffThreshold) / (_maxFalloffThreshold - _minFalloffThreshold),
                FalloffType.Speed => (speed - _minFalloffThreshold) / (_maxFalloffThreshold - _minFalloffThreshold),
                _ => 1f
            };

            falloffMod = 1 - Mathf.Clamp01(falloffMod);

            float impulse = _force * falloffMod;
            float damage = _damage * falloffMod;

            return (impulse, damage);
        }

        #region Internal Types
        private enum FalloffType : byte
        {
            Distance,
            Speed
        }
        #endregion
    }
}