using PolymindGames.InventorySystem;
using PolymindGames.PoolingSystem;
using UnityEngine.Serialization;
using System.Collections;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Manages a melee attack that throws a projectile, including projectile behavior and visual effects.
    /// </summary>
    [AddComponentMenu("Polymind Games/Wieldables/Melee/Attacks/Melee Throw Attack")]
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault1)]
    public sealed class MeleeThrowAttack : MeleeAttackBehaviour
    {
        [SerializeField, Range(0f, 5f), Title("Throw Settings")]
        [Tooltip("Time to spawn the projectile after initiating the throw.")]
        private float _throwDelay = 0.5f;

        [SpaceArea]
        [SerializeField, Range(0f, 100f)]
        [Tooltip("Minimum spread angle for the projectile.")]
        private float _throwMinSpread = 1f;

        [SerializeField, Range(0f, 100f)]
        [Tooltip("Maximum spread angle for the projectile.")]
        private float _throwMaxSpread = 1f;

        [SerializeField]
        [Tooltip("Offset position for where the projectile is thrown from.")]
        private Vector3 _throwPositionOffset = Vector3.zero;

        [SerializeField]
        [Tooltip("Offset rotation for the projectile upon throwing.")]
        private Vector3 _throwRotationOffset = Vector3.zero;

        [SerializeField, SpaceArea]
        [Tooltip("Audio to play when the projectile starts its throw.")]
        private AudioData _throwStartAudio;

        [SerializeField]
        [Tooltip("Audio to play when the projectile is thrown.")]
        private DelayedAudioData _throwAudio;
        
        [SerializeField, PrefabObjectOnly, Title("Projectile Settings")]
        [Tooltip("Prefab for the projectile to be thrown.")]
        private ProjectileBehaviour _projectile;
        
        [SerializeField, ChildObjectOnly]
        [Tooltip("Effect to apply to the projectile when thrown.")]
        private FirearmImpactEffectBehaviour _impactEffect;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("Speed at which the projectile travels.")]
        private float _projectileSpeed = 50f;

        [SerializeField, Range(0f, 100f)]
        [Tooltip("Gravity affecting the projectile.")]
        private float _projectileGravity = 9.81f;

        [SerializeField]
        [Tooltip("Torque applied to the projectile for rotational effects.")]
        private Vector3 _projectileTorque;

        [Title("Item Settings")]
        [SerializeField, Range(1, 10)]
        [Tooltip("Number of items consumed from the inventory each time a projectile is thrown.")]
        private int _itemsConsumedPerThrow = 1;

        [FormerlySerializedAs("_linkItemToProjectile"), SerializeField]
        [Tooltip("Whether to link the thrown item to the projectile (e.g., for pickups).")]
        private bool _shouldLinkItemToProjectile = true;

        private IWieldableItem _wieldableItem;
        private Coroutine _throwRoutine;

        /// <summary>
        /// Initiates the throw attack, playing the throw animation and sound, and starts the coroutine to handle the throw.
        /// </summary>
        /// <param name="accuracy">The accuracy of the throw, influencing the spread.</param>
        /// <param name="throwCallback">Callback to invoke once the throw is completed.</param>
        /// <returns>True if the attack was initiated successfully; otherwise, false.</returns>
        public override bool TryAttack(float accuracy, UnityAction throwCallback = null)
        {
            if (_wieldableItem != null)
            {
                int itemCount = _wieldableItem.Slot.GetCount();
                if (itemCount < _itemsConsumedPerThrow)
                    return false;
                
                if (itemCount == _itemsConsumedPerThrow)
                    Wieldable.Animator.SetBool(AnimationConstants.IsThrown, true);
            }
            
            PlayAttackAnimation();

            var audioPlayer = Wieldable.Audio;
            audioPlayer.PlayClip(_throwStartAudio, BodyPoint.Hands);
            audioPlayer.PlayClip(_throwAudio, BodyPoint.Hands);
            
            _throwRoutine = StartCoroutine(ThrowProjectile(accuracy, throwCallback));
            return true;
        }

        /// <summary>
        /// Cancels the current throw attack coroutine, if any.
        /// </summary>
        public override void CancelAttack()
        {
            CoroutineUtility.StopCoroutine(this, ref _throwRoutine);
            Wieldable.Animator.SetBool(AnimationConstants.IsThrown, false);
        }

        /// <summary>
        /// Coroutine to handle the actual throwing of the projectile after the specified delay.
        /// </summary>
        /// <param name="accuracy">The accuracy of the throw, influencing the spread.</param>
        /// <param name="throwCallback">Callback to invoke once the projectile is thrown.</param>
        /// <returns>An enumerator for coroutine execution.</returns>
        private IEnumerator ThrowProjectile(float accuracy, UnityAction throwCallback)
        {
            yield return new WaitForTime(_throwDelay);

            // Create the ray for the projectile's trajectory.
            var ray = GetUseRay(accuracy, _throwMinSpread, _throwMaxSpread, _throwPositionOffset);
            var rotation = Quaternion.LookRotation(ray.direction) * Quaternion.Euler(_throwRotationOffset);

            // Instantiate the projectile and configure its launch context.
            IProjectile projectile = PoolManager.Instance.Get(_projectile, ray.origin, rotation);
            var context = new LaunchContext(ray.origin, velocity: ray.direction * _projectileSpeed + GetCharacterVelocity(), _projectileTorque, _projectileGravity);
            projectile.Launch(Wieldable.Character, context, _impactEffect, throwCallback);

            if (_wieldableItem != null)
                ConsumeItemFromInventory(projectile, _itemsConsumedPerThrow);

            PlayHitAnimation();
            _throwRoutine = null;
        }

        /// <summary>
        /// Attempts to consume the required number of items from the user's inventory when a projectile is thrown.
        /// Optionally links the consumed item to the projectile if configured to do so.
        /// </summary>
        /// <param name="projectile">The projectile being thrown.</param>
        /// <param name="consumeCount"></param>
        private void ConsumeItemFromInventory(IProjectile projectile, int consumeCount)
        {
            var inventorySlot = _wieldableItem.Slot;
            if (inventorySlot.GetCount() < consumeCount)
                return;

            var originalItem = inventorySlot.GetItem();
            int actuallyRemoved = inventorySlot.AdjustStack(-consumeCount);

            if (actuallyRemoved == consumeCount && _shouldLinkItemToProjectile)
            {
                // If it's the last of the stack, we can use the same reference
                bool isLastInStack = inventorySlot.GetCount() == 0;
                var itemToLink = isLastInStack ? originalItem : new Item(originalItem);
                var itemStack = new ItemStack(itemToLink, consumeCount);

                LinkItemToProjectile(projectile, itemStack);
            }
        }

        /// <summary>
        /// Links the given item stack to the projectile if it supports item pickups.
        /// </summary>
        /// <param name="projectile">The projectile to attach the item to.</param>
        /// <param name="stack">The item stack to attach.</param>
        private static void LinkItemToProjectile(IProjectile projectile, ItemStack stack)
        {
            if (!projectile.gameObject.TryGetComponent<ItemPickup>(out var pickup))
            {
                Debug.LogError(
                    "No ItemPickup component found on the projectile. Add one or disable item-to-projectile linking.",
                    projectile.gameObject
                );
                return;
            }

            pickup.AttachItem(stack);
        }

        protected override void Awake()
        {
            base.Awake();

            _wieldableItem = GetComponent<IWieldableItem>();
            
            if (!PoolManager.Instance.HasPool(_projectile))
            {
                if (_shouldLinkItemToProjectile && _projectile.TryGetComponent(out ItemPickup pickup))
                {
                    pickup.ResetOnAcquiredFromPool = false;
                }

                PoolManager.Instance.RegisterPool(_projectile, new SceneObjectPool<ProjectileBehaviour>(_projectile, gameObject.scene, PoolCategory.Projectiles, 4, 8));
            }
        }

        private void OnEnable()
        {
            Wieldable.Animator.SetBool(AnimationConstants.IsThrown, false);
        }
    }
}