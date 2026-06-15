using PolymindGames.InventorySystem;
using PolymindGames.PoolingSystem;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Complex Projectile Fire-System")]
    public sealed class FirearmComplexProjectileFireSystem : FirearmFireSystemBehaviour
    {
        [SerializeField, NotNull, Title("Projectile")]
        private ProjectileBehaviour _projectile;

        [SerializeField, Range(1, 30)]
        [Tooltip("The amount of projectiles that will be spawned in the world")]
        private int _count = 1;

        [SerializeField, Range(0f, 100f), SpaceArea(3f)]
        private float _minSpread = 0.75f;

        [SerializeField, Range(0f, 100f)]
        private float _maxSpread = 1.5f;

        [SerializeField]
        private Vector3 _spawnPositionOffset = Vector3.zero;

        [SerializeField]
        private Vector3 _spawnRotationOffset = Vector3.zero;

        [SerializeField, Range(0f, 10f), SpaceArea(3f)]
        private float _inheritedSpeed = 1f;

        [SerializeField, Range(1f, 1000f)]
        private float _speed = 75f;

        [SerializeField]
        private Vector3 _torque;

        [SerializeField, Range(0f, 100f)]
        [Tooltip("The gravity for the projectile.")]
        private float _gravity = 9.8f;

        [SerializeField, Range(0f, 100f)] 
        [Title("Durability")]
        private float _durabilityUsage;

        private ItemProperty _durabilityProperty;
        private Transform _headTransform;

        public override void Fire(float accuracy, IFirearmImpactEffect effect)
        {
            var character = Wieldable.Character;
            Vector3 inheritedVelocity = character.TryGetCC(out IMotorCC motor) ? motor.Velocity * _inheritedSpeed : Vector3.zero;
            Transform headTransform = Wieldable.Character.GetTransformOfBodyPoint(BodyPoint.Head);

            float speed = Mathf.Clamp(Firearm.Trigger.TriggerCharge, 0.1f, 1f) * _speed;

            // Spawn Projectile(s).
            float spread = Mathf.Lerp(_minSpread, _maxSpread, 1f - accuracy);
            for (int i = 0; i < _count; i++)
            {
                Ray ray = PhysicsUtility.GenerateRay(headTransform, spread);

                Vector3 origin = ray.origin + headTransform.TransformVector(_spawnPositionOffset);
                Quaternion rotation = Quaternion.LookRotation(ray.direction) * Quaternion.Euler(_spawnRotationOffset);
                IProjectile projectile = PoolManager.Instance.Get(_projectile, origin, rotation);

                var context = new LaunchContext(origin, ray.direction * speed + inheritedVelocity, _torque, _gravity);
                projectile.Launch(character, context, effect);
            }

            // Reduce the durability
            if (_durabilityProperty != null)
                _durabilityProperty.Float -= _durabilityUsage;

            var animator = Wieldable.Animator;
            animator.SetBool(AnimationConstants.IsEmpty, false);
            animator.SetTrigger(AnimationConstants.Shoot);
        }

        public override LaunchContext GetLaunchContext()
        {
            float speed = Firearm.Trigger.TriggerCharge * _speed;
            return new LaunchContext(_headTransform.position, _headTransform.forward * speed, Vector3.zero, _gravity);
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            _headTransform = Wieldable.Character.GetTransformOfBodyPoint(BodyPoint.Head);
        }

        protected override void Awake()
        {
            base.Awake();

            if (!PoolManager.Instance.HasPool(_projectile))
            {
                PoolManager.Instance.RegisterPool(_projectile, new SceneObjectPool<ProjectileBehaviour>(_projectile, gameObject.scene, PoolCategory.Projectiles, 3, 10));
            }

            if (TryGetComponent(out IWieldableItem wieldableItem))
            {
                wieldableItem.AttachedSlotChanged += slot => _durabilityProperty = slot.GetItem()?.GetProperty(ItemConstants.Durability);
            }
        }
    }
}