using PolymindGames.PoolingSystem;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Projectile Fire-System")]
    public sealed class FirearmProjectileFireSystem : FirearmFireSystemBehaviour
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

        [SerializeField, Range(1f, 1000f), SpaceArea(3f)]
        private float _speed = 75f;

        [SerializeField, Range(0f, 100f)]
        [Tooltip("The gravity for the projectile.")]
        private float _gravity = 9.8f;

        private Transform _headTransform;

        public override void Fire(float accuracy, IFirearmImpactEffect effect)
        {
            float triggerCharge = Firearm.Trigger.TriggerCharge;
            
            // Spawn Projectile(s).
            float spread = Mathf.Lerp(_minSpread, _maxSpread, 1f - accuracy);
            for (int i = 0; i < _count; i++)
            {
                Ray ray = PhysicsUtility.GenerateRay(_headTransform, spread);
                IProjectile projectile = PoolManager.Instance.Get(_projectile, ray.origin, Quaternion.LookRotation(ray.direction));

                var context = new LaunchContext(ray.origin, ray.direction * (_speed * triggerCharge), Vector3.zero, _gravity);
                projectile.Launch(Wieldable.Character, context, effect);
            }

            var animator = Wieldable.Animator;
            animator.SetBool(AnimationConstants.IsEmpty, false);
            animator.SetTrigger(AnimationConstants.Shoot);
        }

        public override LaunchContext GetLaunchContext()
        {
            float triggerCharge = Firearm.Trigger.TriggerCharge;
            return new LaunchContext(_headTransform.position, _headTransform.forward * (_speed * triggerCharge), Vector3.zero, _gravity);
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
                PoolManager.Instance.RegisterPool(_projectile, new SceneObjectPool<ProjectileBehaviour>(_projectile, gameObject.scene, PoolCategory.Projectiles, 4, 16));
        }
    }
}