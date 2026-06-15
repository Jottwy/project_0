using System.Runtime.CompilerServices;
using PolymindGames.SurfaceSystem;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Basic Impact-Effect")]
    public sealed class FirearmBasicImpactEffect : FirearmImpactEffectBehaviour
    {
        [SerializeField, Title("Damage")]
        [Tooltip("The type of damage inflicted by this projectile.")]
        private DamageType _damageType = DamageType.Ballistic;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("The amount of damage inflicted at close range.")]
        private float _damage = 15f;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("The impact force applied to rigidbodies upon collision.")]
        private float _force = 15f;
        
        public override void TriggerHitEffect(in RaycastHit hit, Vector3 hitDirection, float speed, float travelledDistance)
        {
            Vector3 hitPoint = hit.point;
            Vector3 hitForce = hitDirection * _force;
            Rigidbody rigidB = hit.rigidbody;

            HandleDamageAndImpact(hit.collider, rigidB, hitPoint, hitForce);

            // Spawn visual effects based on the surface hit.
            SurfaceManager.Instance.PlayEffectFromHit(in hit, _damageType.GetSurfaceEffectType(), parentEffects: rigidB != null);
        }

        public override void TriggerHitEffect(Collision collision, float travelledDistance)
        {
            var contact = collision.GetContact(0);
            Vector3 hitPoint = contact.point;
            Vector3 hitForce = -contact.normal * _force;
            Rigidbody rigidB = collision.rigidbody;

            HandleDamageAndImpact(collision.collider, rigidB, hitPoint, hitForce);

            // Spawn visual effects based on the collision.
            SurfaceManager.Instance.PlayEffectFromCollision(collision, _damageType.GetSurfaceEffectType(), parentEffects: rigidB != null);
        }

        /// <summary>
        /// Handles damage and impact mechanics on the colliding object.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void HandleDamageAndImpact(Collider col, Rigidbody rigidB, Vector3 hitPoint, Vector3 hitForce)
        {
            // Apply damage if the object can receive damage.
            if (col.TryGetComponent(out IDamageHandler damageHandler))
            {
                DamageArgs args = new(_damageType, Wieldable.Character, hitPoint, hitForce);
                damageHandler.HandleDamage(_damage, args);
            }

            // Apply impact force if the object can handle impacts.
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
    }
}