using System.Runtime.CompilerServices;
using PolymindGames.PoolingSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PolymindGames
{
    /// <summary>
    /// ScriptableObject representing an effect pair consisting of a particle system and an audio clip.
    /// </summary>
    [CreateAssetMenu(menuName = "Polymind Games/Utilities/Effect Pair Config", fileName = "EffectPair_")]
    public sealed class EffectPairConfig : ScriptableObject
    {
        [SerializeField]
        [Tooltip("The particle system to be played.")]
        private ParticleSystem _particles;

        [SerializeField, Range(0, 100), HideIf(nameof(_particles), false)]
        [Tooltip("The number of instances to keep in the pool.")]
        private int _poolCount = 4;

        [SerializeField]
        [Tooltip("The audio data to be played.")]
        private AudioData _audio;
        
        /// <summary>
        /// Plays the particle system and audio at a given position with a specified rotation.
        /// </summary>
        /// <param name="position">The position at which to play the effects.</param>
        /// <param name="rotation">The rotation at which to play the effects.</param>
        public void PlayAtPosition(Vector3 position, Quaternion rotation)
        {
            // Get an instance of the particle system from the pool
            if (_particles != null)
            {
                var instance = GetInstanceFromPool(position, rotation);
                instance.Play(true);
            }

            AudioManager.Instance.PlayClip3D(_audio, position);
        }

        /// <summary>
        /// Retrieves an instance of the particle system from the pool.
        /// </summary>
        /// <returns>An instance of the particle system.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ParticleSystem GetInstanceFromPool(Vector3 position, Quaternion rotation)
        {
            // If the particle system is not already pooled, create a new pool for it
            if (!PoolManager.Instance.HasPool(_particles))
            {
                var pool = new SceneObjectPool<ParticleSystem>(_particles, SceneManager.GetActiveScene(), PoolCategory.VisualEffects, 2, _poolCount);
                PoolManager.Instance.RegisterPool(pool);
                return pool.Get(position, rotation);
            }

            // Otherwise, retrieve an instance of the particle system from the existing pool
            return PoolManager.Instance.Get(_particles, position, rotation);
        }
    }
}