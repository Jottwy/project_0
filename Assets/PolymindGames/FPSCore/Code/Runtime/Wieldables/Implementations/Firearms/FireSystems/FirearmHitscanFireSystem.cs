using PolymindGames.PoolingSystem;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Hitscan Fire-System")]
    public class FirearmHitscanFireSystem : FirearmFireSystemBehaviour
    {
        [SerializeField, Range(1, 30), Title("Ray")]
        [Tooltip("The amount of rays that will be sent in the world")]
        private int _rayCount = 1;

        [SerializeField, Range(0f, 100f)]
        private float _minSpread = 1f;

        [SerializeField, Range(0f, 100f)]
        private float _maxSpread = 2f;

        [SerializeField, Range(0f, 10000f)]
        private float _maxDistance = 300f;

        [SerializeField, PrefabObjectOnly, Title("Visual Effects")]
        private ProjectileTracer _tracerPrefab;

        [SerializeField, Range(0f, 1000f)]
        private float _tracerSpeed = 250f;

        private const float MaxTracerDistance = 2000f;
        private Transform _headTransform;

        public override void Fire(float accuracy, IFirearmImpactEffect effect)
        {
            float spread = Mathf.Lerp(_minSpread, _maxSpread, 1f - accuracy);
            for (int i = 0; i < _rayCount; i++)
            {
                Ray ray = PhysicsUtility.GenerateRay(_headTransform, spread);

                var tracer = _tracerPrefab != null
                    ? PoolManager.Instance.Get(_tracerPrefab, ray.origin, Quaternion.LookRotation(ray.direction))
                    : null;

                if (PhysicsUtility.RaycastOptimized(ray, _maxDistance, out RaycastHit hit, LayerConstants.SolidObjectsMask, Wieldable.Character.transform, QueryTriggerInteraction.UseGlobal))
                {
                    effect.TriggerHitEffect(in hit, ray.direction, float.PositiveInfinity, hit.distance);

                    if (tracer != null)
                        tracer.Initialize(ray.origin, hit.point, _tracerSpeed);
                }
                else if (tracer != null)
                    tracer.Initialize(ray.origin, ray.GetPoint(MaxTracerDistance), _tracerSpeed);
            }

            var animator = Wieldable.Animator;
            animator.SetBool(AnimationConstants.IsEmpty, false);
            animator.SetTrigger(AnimationConstants.Shoot);
        }

        public override LaunchContext GetLaunchContext()
        {
            return new LaunchContext(_headTransform.position, _headTransform.forward * 1000f, Vector3.zero, 0f);
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            _headTransform = Wieldable.Character.GetTransformOfBodyPoint(BodyPoint.Head);
        }

        protected override void Awake()
        {
            base.Awake();

            if (_tracerPrefab != null && !PoolManager.Instance.HasPool(_tracerPrefab))
                PoolManager.Instance.RegisterPool(_tracerPrefab, new SceneObjectPool<ProjectileTracer>(_tracerPrefab, gameObject.scene, PoolCategory.Projectiles, 8, 16));
        }
    }
}