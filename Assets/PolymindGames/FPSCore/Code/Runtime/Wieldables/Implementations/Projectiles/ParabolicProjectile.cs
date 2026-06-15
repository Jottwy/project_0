using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    public sealed class ParabolicProjectile : ParabolicProjectileBehaviour
    {
        [SerializeField, SpaceArea]
        private AnimationCurve _sizeOverTime = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        
        [SerializeField]
        [Tooltip("The TrailRenderer component to be used for the projectile.")]
        private TrailRenderer _trailRenderer;

        [SerializeField]
        [Tooltip("The ParticleSystem component to be used for the projectile.")]
        private ParticleSystem _particleSystem;
        
        protected override void OnLaunched()
        {
            _trailRenderer.Clear();
            _trailRenderer.emitting = true;
            _particleSystem.Play(false);
        }

        protected override void OnHit(in RaycastHit hit)
        {
            _trailRenderer.emitting = false;
            _trailRenderer.time = 0f;
            _particleSystem.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
        
        protected override void Update()
        {
            base.Update();

            float t = Time.time.Normalize(StartTime, StartTime + MaxAirTime);
            CachedTransform.localScale = Vector3.one * _sizeOverTime.Evaluate(t);
        }

        protected override void Awake()
        {
            base.Awake();
            _trailRenderer.emitting = false;
        }

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_trailRenderer == null)
            {
                _trailRenderer = GetComponentInChildren<TrailRenderer>();
                if (_trailRenderer == null)
                    _trailRenderer = gameObject.AddComponent<TrailRenderer>();
            }
        }
#endif
        #endregion
    }
}