using PolymindGames.PoolingSystem;
using PolymindGames.WieldableSystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    public abstract class ParabolicProjectileBehaviour : ProjectileBehaviour
    {
        [SerializeField]
        private bool _matchRotation;

        [SerializeField, Range(0.1f, 100f)]
        [Tooltip("If the projectile remains in the air for this specified amount of time without a collision, it will be returned to the pool or destroyed.")]
        private float _maxAirTime = 5f;

        private IFirearmImpactEffect _effect;
        private UnityAction _hitCallback;
        private Vector3 _lerpStartPos;
        private Vector3 _lerpTargetPos;
        private Vector3 _startDirection;
        private Vector3 _startPosition;
        private Poolable _poolable;
        private Vector3 _torque;
        private RaycastHit _hit;
        private float _gravity;
        private float _speed;
        private float _startTime;
        private int _layerMask;

        protected Transform CachedTransform { get; private set; }
        protected ICharacter Character { get; private set; }
        protected float MaxAirTime => _maxAirTime;
        protected bool InAir { get; private set; }
        protected float StartTime => _startTime;

        public sealed override void Launch(ICharacter character, in LaunchContext context, IFirearmImpactEffect effect, UnityAction hitCallback = null)
        {
            _startTime = -1f;
            _startPosition = context.Origin;
            _lerpStartPos = context.Origin;
            _lerpTargetPos = context.Origin;
            _startDirection = context.Velocity.normalized;
            _torque = context.Torque;
            _speed = context.Velocity.magnitude;
            _gravity = context.Gravity;
            _layerMask = context.LayerMask;

            _hitCallback = hitCallback;
            _effect = effect;

            if (!ReferenceEquals(_poolable, null))
                _poolable.Release(_maxAirTime);

            InAir = true;
            Character = character;
            OnLaunched();

            FixedUpdate();
        }

        protected virtual void Awake()
        {
            CachedTransform = transform;
            _poolable = GetComponent<Poolable>();
        }

        protected virtual void FixedUpdate()
        {
            if (!InAir)
                return;

            if (_startTime < 0f)
                _startTime = Time.time;

            float currentTime = Time.time - _startTime;
            float nextTime = currentTime + Time.fixedDeltaTime;

            Vector3 currentPoint = EvaluateParabola(currentTime);
            Vector3 nextPoint = EvaluateParabola(nextTime);

            Vector3 direction = nextPoint - currentPoint;
            float distance = direction.magnitude;
            Ray ray = new Ray(currentPoint, direction);

            if (PhysicsUtility.RaycastOptimized(ray, distance, out _hit, _layerMask, Character.transform, QueryTriggerInteraction.UseGlobal))
            {
                _effect ??= GetComponent<IFirearmImpactEffect>();
                _effect?.TriggerHitEffect(in _hit, ray.direction, direction.magnitude, (_startPosition - _hit.point).magnitude);

                InAir = false;
                _hitCallback?.Invoke();
                OnHit(in _hit);
                
                if (!ReferenceEquals(_poolable, null))
                {
                    _poolable.Release(_poolable.DefaultReleaseDelay);
                }
            }
            else
            {
                _lerpStartPos = currentPoint;
                _lerpTargetPos = nextPoint;
            }
        }

        protected virtual void Update()
        {
            if (!InAir)
                return;

            float delta = Time.time - Time.fixedTime;
            if (delta < Time.fixedDeltaTime)
            {
                float t = delta / Time.fixedDeltaTime;
                CachedTransform.localPosition = Vector3.Lerp(_lerpStartPos, _lerpTargetPos, t);
            }
            else
                CachedTransform.localPosition = _lerpTargetPos;

            if (_matchRotation)
            {
                Vector3 velocity = _lerpTargetPos - _lerpStartPos;
                if (velocity != Vector3.zero)
                {
                    Vector3 torque = _startTime < 0f
                        ? Vector3.zero
                        : EvaluateRotation(Time.time - _startTime);

                    CachedTransform.rotation = Quaternion.LookRotation(velocity) * Quaternion.Euler(torque);
                }
            }
        }

        protected virtual void OnLaunched() { }
        protected virtual void OnHit(in RaycastHit hit) { }

        private Vector3 EvaluateParabola(float time)
        {
            Vector3 point = _startPosition + _startDirection * (_speed * time);
            Vector3 gravity = Vector3.down * (_gravity * time * time);
            return point + gravity;
        }

        private Vector3 EvaluateRotation(float time)
        {
            return _torque * (_speed * time);
        }
    }
}