using PolymindGames.PoolingSystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [RequireComponent(typeof(Rigidbody))]
    public abstract class PhysicsProjectileBehaviour : ProjectileBehaviour
    {
        private IFirearmImpactEffect _effect;
        private UnityAction _hitCallback;
        private Rigidbody _rigidbody;
        private Poolable _poolable;
        private Vector3 _gravity;
        private Vector3 _origin;
        
        private const float MaxAirLifeTime = 5f;
        
        protected ICharacter Character { get; private set; }
        protected bool InAir { get; private set; }
        protected Rigidbody Rigidbody => _rigidbody;

        public sealed override void Launch(ICharacter character, in LaunchContext context, IFirearmImpactEffect effect, UnityAction hitCallback = null)
        {
            _origin = context.Origin;
            
            _hitCallback = hitCallback;
            _effect = effect;

            _rigidbody.position = context.Origin;
            _rigidbody.rotation = transform.rotation;
            _rigidbody.isKinematic = false;
            _rigidbody.linearVelocity = context.Velocity;
            _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            _rigidbody.angularVelocity = context.Torque;
            
            _poolable?.Release(MaxAirLifeTime);
            enabled = true;
            
            InAir = true;
            Character = character;
            OnLaunched();
        }

        protected virtual void OnLaunched() { }
        protected virtual void OnHit(Collision hit) { }

        protected virtual void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            _poolable = GetComponent<Poolable>();
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        protected virtual void OnCollisionEnter(Collision collision)
        {
            if (!InAir)
                return;

            _poolable?.Release(_poolable.DefaultReleaseDelay);

            float travelledDistance = Vector3.Distance(_origin, transform.position);

            _effect ??= GetComponent<IFirearmImpactEffect>();
            _effect?.TriggerHitEffect(collision, travelledDistance);

            InAir = false;
            
            _rigidbody.interpolation = RigidbodyInterpolation.None;

            _hitCallback?.Invoke();
            OnHit(collision);

            enabled = false;
        }
    }
}