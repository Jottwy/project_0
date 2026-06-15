using UnityEngine.SceneManagement;
using UnityEngine.Pool;
using UnityEngine;
using System;

namespace PolymindGames.PoolingSystem
{
    /// <summary>
    /// Represents an object pool for managing scene objects of type <typeparamref name="T"/>.
    /// This pool will handle instantiation, acquisition, and release of objects, as well as
    /// pre-warming to reduce performance spikes.
    /// </summary>
    public sealed class SceneObjectPool<T> : IPool<T> where T : Component
    {
        private readonly ObjectPool<Poolable> _pool;
        private readonly ScenePoolManager _manager;
        private readonly T _prefab;

        /// <summary>
        /// Initializes the scene object pool with the given prefab, scene, category, initial size, and maximum size.
        /// </summary>
        /// <param name="prefab">The prefab for the objects being pooled.</param>
        /// <param name="scene">The scene where the pool will be used.</param>
        /// <param name="category">The category under which the pool will be registered.</param>
        /// <param name="initialSize">The initial size of the pool.</param>
        /// <param name="maxSize">The maximum size of the pool.</param>
        /// <param name="releaseDelay"></param>
        /// <param name="onPostProcessPrefab"></param>
        public SceneObjectPool(T prefab, Scene scene, PoolCategory category, int initialSize, int maxSize, float releaseDelay = 0f, Action<T> onPostProcessPrefab = null)
        {
            if (!ScenePoolManager.TryGetManagerForScene(scene, out _manager))
            {
                throw new NullReferenceException("The scene pool could not be found.");
            }

            var parent = _manager.AddPool(this, category);
            var poolableInterface = new PoolableAdapter(this);

            _prefab = prefab;
            var prefabCopy = UnityEngine.Object.Instantiate(prefab, parent);
            prefabCopy.gameObject.SetActive(false);
            onPostProcessPrefab?.Invoke(prefabCopy);
            var poolablePrefab = prefabCopy.gameObject.GetOrAddComponent<Poolable>();
            poolablePrefab.SetParentPool(poolableInterface, releaseDelay);

            // Create the object pool with pre-warming and object management actions
            _pool = new ObjectPool<Poolable>(
                createFunc: () =>
                {
                    var instance = UnityEngine.Object.Instantiate(poolablePrefab, parent);
                    instance.SetParentPool(poolableInterface);
                    return instance;
                },
                actionOnGet: poolable =>
                {
                    poolable.gameObject.SetActive(true);
                    poolable.OnAcquired();
                },
                actionOnRelease: poolable =>
                {
                    poolable.gameObject.SetActive(false);
                    poolable.OnReleased();
                },
                actionOnDestroy: poolable =>
                {
                    if (poolable != null)
                        UnityEngine.Object.Destroy(poolable.gameObject);
                },
                collectionCheck: false,
                defaultCapacity: initialSize,
                maxSize: maxSize
            );
        }

        /// <inheritdoc/>
        public T Get()
        {
            return _pool.Get().GetComponent<T>();
        }

        /// <inheritdoc/>
        public T Get(Vector3 position, Quaternion rotation, Transform parent = null)
        {
            var poolable = Get();
            var poolableTransform = poolable.transform;

            if (!ReferenceEquals(parent, null))
            {
                poolableTransform.SetParent(parent);
            }

            poolableTransform.SetPositionAndRotation(position, rotation);

            return poolable;
        }

        /// <inheritdoc/>
        public void Release(T instance)
        {
            if (instance.TryGetComponent<Poolable>(out var poolable))
            {
                poolable.Release();
            }
            else
            {
                Debug.LogWarning($"Attempted to release {instance.name} but it has no Poolable component.");
            }
        }

        /// <inheritdoc/>
        public int InactiveCount => _pool.CountInactive;

        /// <inheritdoc/>
        public void Clear() => _pool.Clear();

        /// <inheritdoc/>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var poolable = _pool.Get();
                _pool.Release(poolable);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _pool.Dispose();
            _manager.RemovePool(this);
            PoolManager.Instance.UnregisterPool(_prefab);
        }

        /// <summary>
        /// Internal adapter to expose only the <see cref="IPool{Poolable}"/> methods.
        /// </summary>
        private sealed class PoolableAdapter : IPool<Poolable>
        {
            private readonly SceneObjectPool<T> _parent;

            public PoolableAdapter(SceneObjectPool<T> parent) => _parent = parent;

            public Poolable Get() => _parent._pool.Get();

            public Poolable Get(Vector3 position, Quaternion rotation, Transform parent = null)
            {
                var poolable = Get();
                var poolableTransform = poolable.transform;

                if (!ReferenceEquals(parent, null))
                {
                    poolableTransform.SetParent(parent);
                }

                poolableTransform.SetPositionAndRotation(position, rotation);
                return poolable;
            }

            public void Release(Poolable instance) => _parent._pool.Release(instance);
            public int InactiveCount => _parent._pool.CountInactive;
            public void Prewarm(int count) => _parent.Prewarm(count);
            public void Clear() => _parent.Clear();
            public void Dispose() => _parent.Dispose();
        }
    }
}