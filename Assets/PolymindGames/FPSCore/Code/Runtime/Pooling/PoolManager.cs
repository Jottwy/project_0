using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames.PoolingSystem
{
    /// <summary>
    /// Manages multiple pools of objects, allowing for efficient object acquisition and release.
    /// </summary>
    public sealed class PoolManager : Manager<PoolManager>
    {
        private readonly Dictionary<int, IPoolBase> _poolsByTemplate = new();
        private readonly Dictionary<Type, IPoolBase> _poolsByType = new();

        #region Initialization
        static PoolManager()
        {
            // Application.quitting += () =>
            // {
            //     if (Instance != null)
            //         Instance.DisposePools();
            // };
        }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
#else
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        private static void Init() => LoadOrCreateInstance();

        protected override void OnInitialized()
        {
            DisposePools();
        }

        private void DisposePools()
        {
            ClearAllPools();
            _poolsByTemplate.Clear();
            _poolsByType.Clear();
        }
        #endregion

        /// <summary>
        /// Registers a pool for objects of a specific type.
        /// </summary>
        public void RegisterPool<T>(IPool<T> pool) where T : class
        {
            _poolsByType.TryAdd(typeof(T), pool);
        }

        /// <summary>
        /// Registers a pool for objects by a template instance.
        /// </summary>
        public void RegisterPool<T>(T template, IPool<T> pool) where T : class
        {
            _poolsByTemplate.TryAdd(template.GetHashCode(), pool);
        }

        /// <summary>
        /// Unregisters a pool by object type.
        /// </summary>
        public void UnregisterPool<T>() where T : class
        {
            _poolsByType.Remove(typeof(T));
        }
        
        /// <summary>
        /// Unregisters a pool by template instance.
        /// </summary>
        public void UnregisterPool<T>(T template) where T : class
        {
            _poolsByTemplate.Remove(template.GetHashCode());
        }

        /// <summary>
        /// Returns true if a pool for the given type exists.
        /// </summary>
        public bool HasPool<T>() where T : class
        {
            return _poolsByType.ContainsKey(typeof(T));
        }
        
        /// <summary>
        /// Returns true if a pool for the given template exists.
        /// </summary>
        public bool HasPool<T>(T template) where T : class
        {
            return _poolsByTemplate.ContainsKey(template.GetHashCode());
        }

        /// <summary>
        /// Gets a pool by object type.
        /// </summary>
        public IPool<T> GetPool<T>() where T : class
        {
            if (_poolsByType.TryGetValue(typeof(T), out var pool))
                return (IPool<T>)pool;

            return null;
        }

        /// <summary>
        /// Gets a pool by template instance.
        /// </summary>
        public IPool<T> GetPool<T>(T template) where T : class
        {
            if (_poolsByTemplate.TryGetValue(template.GetHashCode(), out var pool))
                return (IPool<T>)pool;

            return null;
        }

        /// <summary>
        /// Gets an object from the pool by type.
        /// </summary>
        public T Get<T>() where T : class
        {
            if (_poolsByType.TryGetValue(typeof(T), out var pool))
                return ((IPool<T>)pool).Get();

            throw new InvalidOperationException($"No pool found for type {typeof(T).Name}. Ensure it's registered.");
        }
        
        /// <summary>
        /// Tries to get an object from the pool by type.
        /// </summary>
        public bool TryGet<T>(out T instance) where T : class
        {
            if (_poolsByType.TryGetValue(typeof(T), out var pool))
            {
                instance = ((IPool<T>)pool).Get();
                return true;
            }

            instance = null;
            return false;
        }

        /// <summary>
        /// Gets an object from the pool by template instance.
        /// </summary>
        public T Get<T>(T template) where T : class
        {
            if (_poolsByTemplate.TryGetValue(template.GetHashCode(), out var pool))
                return ((IPool<T>)pool).Get();

            throw new InvalidOperationException($"No pool found for template of type {typeof(T).Name}. Ensure it's registered.");
        }
        
        /// <summary>
        /// Tries to get an object from the pool by template instance.
        /// </summary>
        public bool TryGet<T>(T template, out T instance) where T : class
        {
            if (ReferenceEquals(template, null))
            {
                instance = null;
                return false;
            }

            if (_poolsByTemplate.TryGetValue(template.GetHashCode(), out var pool))
            {
                instance = ((IPool<T>)pool).Get();
                return true;
            }

            instance = null;
            return false;
        }
        
        /// <summary>
        /// Initializes the position, rotation, and parent of a pool object after it's acquired.
        /// </summary>
        public T Get<T>(Vector3 position, Quaternion rotation, Transform parent = null) where T : class
        {
            if (_poolsByType.TryGetValue(typeof(T), out var pool))
            {
                T instance = ((IPool<T>)pool).Get(position, rotation, parent);
                return instance;
            }

            throw new InvalidOperationException($"No pool found for type {typeof(T).Name}. Ensure it's registered.");
        }
        
        /// <summary>
        /// Initializes the position, rotation, and parent of a pool object after it's acquired.
        /// </summary>
        public T Get<T>(T template, Vector3 position, Quaternion rotation, Transform parent = null) where T : class
        {
            if (_poolsByTemplate.TryGetValue(template.GetHashCode(), out var pool))
            {
                T instance = ((IPool<T>)pool).Get(position, rotation, parent);
                return instance;
            }

            throw new InvalidOperationException($"No pool found for type {typeof(T).Name}. Ensure it's registered.");
        }

        /// <summary>
        /// Releases an object back to the pool by type.
        /// </summary>
        public void Release<T>(T instance) where T : class
        {
            if (_poolsByType.TryGetValue(typeof(T), out var pool))
                ((IPool<T>)pool).Release(instance);
        }

        /// <summary>
        /// Releases an object back to the pool by template instance.
        /// </summary>
        public void Release<T>(T template, T instance) where T : class
        {
            if (_poolsByTemplate.TryGetValue(template.GetHashCode(), out var pool))
                ((IPool<T>)pool).Release(instance);
        }

        /// <summary>
        /// Clears all pools.
        /// </summary>
        public void ClearAllPools()
        {
            foreach (var pool in _poolsByTemplate.Values)
                pool.Clear();

            foreach (var pool in _poolsByType.Values)
                pool.Clear();
        }

        /// <summary>
        /// Gets the current inactive count for a specific pool type.
        /// </summary>
        public int GetInactiveCount<T>() where T : class
        {
            if (_poolsByType.TryGetValue(typeof(T), out var pool))
                return ((IPool<T>)pool).InactiveCount;

            return 0;
        }
        
        /// <summary>
        /// Gets the current inactive count for a specific pool template.
        /// </summary>
        public int GetInactiveCount<T>(object template) where T : class
        {
            if (_poolsByTemplate.TryGetValue(template.GetHashCode(), out var pool))
                return ((IPool<T>)pool).InactiveCount;

            return 0;
        }
    }
}