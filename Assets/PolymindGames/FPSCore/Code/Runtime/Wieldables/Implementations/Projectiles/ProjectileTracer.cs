using PolymindGames.PoolingSystem;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Manages the visual tracer effect of a projectile, including movement, scaling, and pooling behavior.
    /// </summary>
    public sealed class ProjectileTracer : MonoBehaviour
    {
        [SerializeField, Range(0.1f, 100f)]
        [Tooltip("The maximum duration the projectile can stay in the air before being returned to the pool or destroyed.")]
        private float _maxAirTime = 2f;

        [SerializeField]
        [Tooltip("Defines how the size of the tracer changes over time.")]
        private AnimationCurve _sizeOverTime = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [SerializeField, Title("References")]
        [Tooltip("The TrailRenderer component for visualizing the projectile's path.")]
        private TrailRenderer _trailRenderer;

        [SerializeField]
        [Tooltip("The ParticleSystem component for visualizing additional effects.")]
        private ParticleSystem _particleSystem;

        private Transform _transformCache;
        private Vector3 _currentPosition;
        private Vector3 _targetPosition;
        private Poolable _poolable;
        private float _startTime;
        private float _speed;

        /// <summary>
        /// Initializes the tracer with starting and target positions and a specified movement speed.
        /// </summary>
        /// <param name="startPosition">The starting position of the tracer.</param>
        /// <param name="targetPosition">The end position of the tracer.</param>
        /// <param name="speed">The speed at which the tracer moves.</param>
        public void Initialize(Vector3 startPosition, Vector3 targetPosition, float speed)
        {
            _transformCache.position = startPosition;
            _transformCache.localScale = Vector3.one * _sizeOverTime.Evaluate(0f);

            _currentPosition = startPosition;
            _targetPosition = targetPosition;
            _startTime = Time.time;
            _speed = speed;

            _trailRenderer.Clear();
            _trailRenderer.emitting = true;
            _particleSystem.Play(false);

            if (!ReferenceEquals(_poolable, null))
                _poolable.Release(_maxAirTime);
        }

        private void Update()
        {
            // Move the tracer towards the target position.
            _currentPosition = Vector3.MoveTowards(_currentPosition, _targetPosition, _speed * Time.deltaTime);

            // Calculate the normalized time for size scaling.
            float normalizedTime = Mathf.Clamp01((Time.time - _startTime) / _maxAirTime);

            // Update the transform's position and scale.
            _transformCache.position = _currentPosition;
            _transformCache.localScale = Vector3.one * _sizeOverTime.Evaluate(normalizedTime);

            // Check if the tracer has exceeded its maximum air time and should be returned to the pool.
            if (Time.time - _startTime > _maxAirTime || Vector3.Distance(_currentPosition, _targetPosition) < 0.1f)
            {
                _trailRenderer.emitting = false;
                _trailRenderer.time = 0f;
                _particleSystem.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);

                if (!ReferenceEquals(_poolable, null))
                    _poolable.Release(0.1f);
            }
        }

        private void Awake()
        {
            _trailRenderer.emitting = false;
            _poolable = GetComponent<Poolable>();
            _transformCache = transform;
        }

        #region Editor Logic
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_trailRenderer == null)
                _trailRenderer = GetComponent<TrailRenderer>();

            if (_particleSystem == null)
                _particleSystem = GetComponent<ParticleSystem>();
        }
#endif
        #endregion
    }
}