using UnityEngine.SceneManagement;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.PoolingSystem
{
    public sealed class ScenePoolManager : MonoBehaviour
    {
        private readonly struct PoolableLifetimeEntry
        {
            public readonly Poolable Poolable;
            public readonly float ReleaseTime;

            public PoolableLifetimeEntry(Poolable poolable, float releaseTime)
            {
                Poolable = poolable;
                ReleaseTime = releaseTime;
            }
        }
        
        private static readonly Dictionary<Scene, ScenePoolManager> _managers = new();
        private readonly Dictionary<PoolCategory, Transform> _categories = new();
        private readonly List<PoolableLifetimeEntry> _activePoolables = new();
        private readonly List<IPoolBase> _pools = new();
        private Scene _parentScene;
        private bool _isActive;

        public event UnityAction Destroyed;

        public static bool TryGetManagerForScene(Scene scene, out ScenePoolManager manager)
        {
            if (UnityUtility.IsQuitting)
            {
                manager = null;
                return false;
            }

            if (_managers.TryGetValue(scene, out manager))
                return true;

            if (!scene.isLoaded)
                return false;

            var parent = new GameObject("Scene Pools");
            if (parent.scene != scene)
                SceneManager.MoveGameObjectToScene(parent, scene);
                
            manager = parent.AddComponent<ScenePoolManager>();
            return true;
        }

        public Transform AddPool(IPoolBase pool, PoolCategory category = PoolCategory.None)
        {
            if (!_isActive)
                return null;

            _pools.Add(pool);
            return GetCategoryRoot(category);
        }

        public void RemovePool(IPoolBase pool)
        {
            if (_isActive)
                _pools.Remove(pool);
        }

        public void AddActivePoolable(Poolable poolable, float releaseDelay)
        {
            float releaseTime = Time.time + releaseDelay;
            int foundIndex = _activePoolables.FindIndex(entry => entry.Poolable == poolable);

            if (foundIndex != -1)
            {
                _activePoolables[foundIndex] = new PoolableLifetimeEntry(poolable, releaseTime);
            }
            else
            {
                _activePoolables.Add(new PoolableLifetimeEntry(poolable, releaseTime));
            }
        }

        public void RemoveActivePoolable(Poolable poolable)
        {
            int foundIndex = _activePoolables.FindIndex(entry => entry.Poolable == poolable);
            if (foundIndex != -1)
                _activePoolables.RemoveAt(foundIndex);
        }

        private Transform GetCategoryRoot(PoolCategory category)
        {
            if (category == PoolCategory.None)
                return transform;

            if (!_categories.TryGetValue(category, out var categoryRoot))
            {
                categoryRoot = new GameObject(category.ToString()).transform;
                categoryRoot.SetParent(transform);
                _categories.Add(category, categoryRoot);
            }

            return categoryRoot;
        }
        
        private void Awake()
        {
            _managers.Add(gameObject.scene, this);
            _isActive = true;
        }

        private void Update()
        {
            float currentTime = Time.time;
            for (int i = _activePoolables.Count - 1; i >= 0; --i)
            {
                var entry = _activePoolables[i];
                if (entry.ReleaseTime < currentTime)
                {
                    _activePoolables.RemoveAt(i);
                    entry.Poolable.ReleaseOrDestroy();
                }
            }
        }

        private void OnDestroy()
        {
            _managers.Remove(gameObject.scene);
            _isActive = false;

            foreach (var pool in _pools)
                pool.Dispose();
            
            Destroyed?.Invoke();
        }
    }
}