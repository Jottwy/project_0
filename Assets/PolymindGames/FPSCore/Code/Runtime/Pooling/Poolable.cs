using UnityEngine;
using System;

namespace PolymindGames.PoolingSystem
{
    /// <summary>
    /// Represents an object that can be pooled and managed by an object pool.
    /// This class contains logic for acquiring, releasing, and auto-releasing objects,
    /// as well as notifying listeners during acquisition and release events.
    /// </summary>
    public sealed class Poolable : MonoBehaviour
    {
        [SerializeField, Range(0f, 500f)]
        [Tooltip("The time it takes for this object to return to the pool or self-destruct if it's not part of one.")]
        private float _autoReleaseDelay = 0f;

        private IPoolableListener[] _listeners;
        private IPool<Poolable> _parentPool;
        private bool _isActive;

        public float DefaultReleaseDelay => _autoReleaseDelay;

        /// <summary>
        /// Forces the object to release either immediately or after a specified delay.
        /// </summary>
        /// <param name="delay">The delay in seconds before the object is released (default is 0).</param>
        public void Release(float delay = 0f)
        {
            if (delay < Mathf.Epsilon)
            {
                ReleaseOrDestroy();
            }
            else
            {
                if (ScenePoolManager.TryGetManagerForScene(gameObject.scene, out var scenePool))
                    scenePool.AddActivePoolable(this, delay);
            }
        }

        /// <summary>
        /// Sets the parent pool for this object.
        /// Throws an exception if the object already has a parent pool.
        /// </summary>
        /// <param name="pool">The pool to assign as the parent pool.</param>
        /// <param name="releaseDelay"></param>
        internal void SetParentPool(IPool<Poolable> pool, float releaseDelay = 0f)
        {
            if (_parentPool != null)
                throw new InvalidOperationException("This object already has a parent pool.");

            _parentPool = pool ?? throw new NullReferenceException("Cannot assign a null parent.");
            _autoReleaseDelay = releaseDelay > Mathf.Epsilon ? releaseDelay : _autoReleaseDelay;
        }

        /// <summary>
        /// Called when the object is acquired from the pool.
        /// Notifies all listeners and starts the auto-release coroutine if necessary.
        /// </summary>
        internal void OnAcquired()
        {
            _isActive = true;
            
            foreach (var listener in _listeners)
            {
                listener.OnAcquired();
            }

            // Start auto-release if the delay is greater than zero.
            if (_autoReleaseDelay > Mathf.Epsilon)
            {
                if (ScenePoolManager.TryGetManagerForScene(gameObject.scene, out var scenePool))
                    scenePool.AddActivePoolable(this, _autoReleaseDelay);
            }

        }

        /// <summary>
        /// Called when the object is released back to the pool.
        /// Stops any ongoing auto-release coroutine and notifies listeners.
        /// </summary>
        internal void OnReleased()
        {
            _isActive = false;
            foreach (var listener in _listeners)
            {
                listener.OnReleased();
            }
        }

        /// <summary>
        /// Releases the object back to its parent pool or destroys it if no parent pool is set.
        /// </summary>
        internal void ReleaseOrDestroy()
        {
            if (_parentPool != null)
            {
                _parentPool.Release(this);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            if (!_isActive)
                return;
            
            if (ScenePoolManager.TryGetManagerForScene(gameObject.scene, out var scenePool))
                scenePool.RemoveActivePoolable(this);
        }

        private void Awake() => _listeners = GetComponents<IPoolableListener>() ?? Array.Empty<IPoolableListener>();
    }
}