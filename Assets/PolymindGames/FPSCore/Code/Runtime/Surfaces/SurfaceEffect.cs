using PolymindGames.PoolingSystem;
using UnityEngine;

namespace PolymindGames.SurfaceSystem
{
    /// <summary>
    /// This script defines a surface effect that manages visual effects.
    /// </summary>
    [RequireComponent(typeof(Poolable))]
    public sealed class SurfaceEffect : MonoBehaviour
    {
        [SerializeField, ReorderableList(HasLabels = false)]
        private ParticleSystem[] _particles;

        private Transform _cachedTransform;

        public void Play(in Vector3 position, in Quaternion rotation, Transform parent = null)
        {
            if (!ReferenceEquals(parent, null))
                _cachedTransform.SetParent(parent);
            
            _cachedTransform.SetPositionAndRotation(position, rotation);

            foreach (var particle in _particles)
                particle.Play(false);
        }

        private void Awake() => _cachedTransform = transform;

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            gameObject.layer = LayerConstants.Effect;
        }

        private void OnValidate()
        {
            _particles = GetComponentsInChildren<ParticleSystem>();
        }
#endif
        #endregion
    }
}