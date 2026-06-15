using System.Runtime.CompilerServices;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Base class for implementing melee attack behaviors. Provides core functionality for attack initiation, animation management, and attack-related calculations.
    /// </summary>
    public abstract class MeleeAttackBehaviour : MonoBehaviour
    {
        [SerializeField, Range(0f, 5f)]
        [Tooltip("The cooldown time between attacks.")]
        private float _attackCooldown = 0.75f;

        [SerializeField, Range(0, 10)]
        [Tooltip("The index of the attack animation.")]
        private int _attackAnimIndex;

        [SerializeField, Range(0.1f, 2f)]
        [Tooltip("The speed of the attack animation.")]
        private float _attackAnimSpeed = 1f;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("The amount of stamina used per attack.")]
        private float _attackStaminaUsage = 0.075f;
        
        [SerializeField, Range(0f, 1f)]
        [Tooltip("The amount of durability used per hit.")]
        private float _hitDurabilityUsage = 0.01f;
        
        /// <summary>
        /// The cooldown time between attacks.
        /// </summary>
        public float AttackCooldown => _attackCooldown;

        /// <summary>
        /// The amount of stamina used per attack.
        /// </summary>
        public float AttackStaminaUsage => _attackStaminaUsage;
        
        /// <summary>
        /// The amount of durability used per hit
        /// </summary>
        public float HitDurabilityUsage => _hitDurabilityUsage;

        /// <summary>
        /// The component responsible for wielding the weapon.
        /// </summary>
        protected IWieldable Wieldable { get; private set; }

        /// <summary>
        /// Attempts to perform an attack with the specified accuracy.
        /// </summary>
        /// <param name="accuracy">The accuracy of the attack.</param>
        /// <param name="hitCallback">Optional callback invoked when a successful hit occurs.</param>
        /// <returns>True if the attack was successfully initiated; otherwise, false.</returns>
        public abstract bool TryAttack(float accuracy, UnityAction hitCallback = null);

        /// <summary>
        /// Cancels the current attack and stops any ongoing attack actions.
        /// </summary>
        public abstract void CancelAttack();

        /// <summary>
        /// Initializes the <see cref="Wieldable"/> property by retrieving the IWieldable component.
        /// </summary>
        protected virtual void Awake() => Wieldable = GetComponent<IWieldable>();

        /// <summary>
        /// Generates a ray for the attack with a spread based on the specified accuracy.
        /// </summary>
        /// <param name="accuracy">The accuracy of the attack, where 1 is perfectly accurate and 0 is fully spread.</param>
        /// <param name="hitOffset">An optional offset to apply to the ray's hit point.</param>
        /// <returns>A <see cref="Ray"/> representing the attack direction.</returns>
        protected Ray GetUseRay(float accuracy, Vector3 hitOffset = default(Vector3))
        {
            float spread = Mathf.Lerp(1f, 5f, 1 - accuracy);
            var headTransform = Wieldable.Character.GetTransformOfBodyPoint(BodyPoint.Head);
            return PhysicsUtility.GenerateRay(headTransform, spread, hitOffset);
        }

        /// <summary>
        /// Generates a ray for the attack with a spread range based on the specified accuracy.
        /// </summary>
        /// <param name="accuracy">The accuracy of the attack, where 1 is perfectly accurate and 0 is fully spread.</param>
        /// <param name="minSpread">The minimum spread value.</param>
        /// <param name="maxSpread">The maximum spread value.</param>
        /// <param name="hitOffset">An optional offset to apply to the ray's hit point.</param>
        /// <returns>A <see cref="Ray"/> representing the attack direction.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected Ray GetUseRay(float accuracy, float minSpread, float maxSpread, in Vector3 hitOffset)
        {
            float spread = Mathf.Lerp(minSpread, maxSpread, 1 - accuracy);
            var headTransform = Wieldable.Character.GetTransformOfBodyPoint(BodyPoint.Head);
            return PhysicsUtility.GenerateRay(headTransform, spread, hitOffset);
        }
        
        /// <summary>
        /// Retrieves the current velocity of the character's motor controller, if available.
        /// </summary>
        /// <returns>The character's velocity as a <see cref="Vector3"/>.</returns>
        protected Vector3 GetCharacterVelocity()
        {
            return Wieldable.Character.TryGetCC(out IMotorCC motor) ? motor.Velocity : Vector3.zero;
        }

        /// <summary>
        /// Plays the attack animation with the configured index and speed.
        /// </summary>
        protected void PlayAttackAnimation()
        {
            var animator = Wieldable.Animator;
            animator.SetFloat(AnimationConstants.AttackIndex, _attackAnimIndex);
            animator.SetFloat(AnimationConstants.AttackSpeed, _attackAnimSpeed);
            animator.SetTrigger(AnimationConstants.Attack);
        }

        /// <summary>
        /// Plays the hit animation to indicate that the attack has hit its target.
        /// </summary>
        protected void PlayHitAnimation()
        {
            var animator = Wieldable.Animator;
            animator.SetTrigger(AnimationConstants.AttackHit);
        }
    }
}