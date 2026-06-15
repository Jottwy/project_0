using PolymindGames.SurfaceSystem;
using System.Collections;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Basic melee attack behavior with adjustable attack parameters and impact effects.
    /// </summary>
    [AddComponentMenu("Polymind Games/Wieldables/Melee/Attacks/Melee Basic Attack")]
    public sealed class BasicMeleeAttack : MeleeAttackBehaviour
    {
        [SerializeField, Range(0f, 5f), Title("Attack Settings")]
        [Tooltip("The delay before the attack is executed.")]
        private float _attackDelay = 0.2f;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("The radius of the attack area.")]
        private float _attackRadius = 0.1f;

        [SerializeField, Range(0f, 5f)]
        [Tooltip("The maximum distance of the attack.")]
        private float _attackDistance = 0.5f;

        [SerializeField]
        [Tooltip("Audio data to play when the attack is executed.")]
        private DelayedAudioData _attackAudio = new(null);

        [SerializeField, Title("Hit Settings")]
        [Tooltip("The type of damage inflicted by the attack.")]
        private DamageType _hitDamageType = DamageType.Blunt;

        [SerializeField, MinMaxSlider(0, 100f)]
        [Tooltip("The range of damage values applied by the attack.")]
        private Vector2 _hitDamageRange = new(15, 20);

        [SerializeField, Range(0f, 100f)]
        [Tooltip("The force applied to objects when the attack hits.")]
        private float _hitForce = 30f;

        [SerializeField]
        [Tooltip("Audio data to play when the attack hits.")]
        private AudioData _hitAudio = new(null);

        private Coroutine _attackRoutine;

        /// <summary>
        /// Cancels the current attack and stops any ongoing attack actions.
        /// </summary>
        public override void CancelAttack() => CoroutineUtility.StopCoroutine(this, ref _attackRoutine);

        /// <summary>
        /// Attempts to perform an attack with the specified accuracy.
        /// </summary>
        /// <param name="accuracy">The accuracy of the attack, affecting the spread and hit chance.</param>
        /// <param name="hitCallback">Optional callback invoked when a successful hit occurs.</param>
        /// <returns>True if the attack was successfully initiated; otherwise, false.</returns>
        public override bool TryAttack(float accuracy, UnityAction hitCallback = null)
        {
            PlayAttackAnimation();
            Wieldable.Audio.PlayClip(_attackAudio, BodyPoint.Hands);

            _attackRoutine = StartCoroutine(Hit(accuracy, hitCallback));

            return true;
        }

        /// <summary>
        /// Coroutine that handles the attack logic, including delay, hit detection, and applying damage and effects.
        /// </summary>
        /// <param name="accuracy">The accuracy of the attack.</param>
        /// <param name="hitCallback">Optional callback invoked when a successful hit occurs.</param>
        /// <returns>An enumerator for coroutine execution.</returns>
        private IEnumerator Hit(float accuracy, UnityAction hitCallback)
        {
            yield return new WaitForTime(_attackDelay);

            Ray ray = GetUseRay(accuracy);
            if (PhysicsUtility.SphereCastOptimized(ray, _attackRadius, _attackDistance, out var hit, LayerConstants.SolidObjectsMask, Wieldable.Character.transform, QueryTriggerInteraction.UseGlobal))
            {
                HandleDamageAndImpact(hit.collider, hit.rigidbody, hit.point, ray.direction * _hitForce);
                SurfaceManager.Instance.PlayEffectFromHit(in hit, _hitDamageType.GetSurfaceEffectType(), parentEffects: hit.rigidbody != null);

                Wieldable.Audio.PlayClip(_hitAudio, BodyPoint.Hands);

                PlayHitAnimation();

                hitCallback?.Invoke();
            }

            _attackRoutine = null;
        }

        /// <summary>
        /// Handles the application of damage and impact effects on the hit object.
        /// </summary>
        private void HandleDamageAndImpact(Collider col, Rigidbody rigidB, Vector3 hitPoint, Vector3 hitForce)
        {
            // Apply damage if the object can receive damage.
            if (col.TryGetComponent(out IDamageHandler receiver))
            {
                DamageArgs args = new(_hitDamageType, Wieldable.Character, hitPoint, hitForce);
                float damage = _hitDamageRange.GetRandomFromRange();
                receiver.HandleDamage(damage, args);
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