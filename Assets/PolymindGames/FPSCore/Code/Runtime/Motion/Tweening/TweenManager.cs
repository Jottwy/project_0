using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine.Pool;
using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    using Object = UnityEngine.Object;
    
    /// <summary>
    /// Manages tweens, including their pooling and ticking (updating) logic.
    /// </summary>
    internal sealed class TweenManager : Manager<TweenManager>
    {
        #region Initialization
        /// <summary>
        /// Handles the ticking update for tweens.
        /// </summary>
        private sealed class TickHandler : MonoBehaviour
        {
            public event UnityAction OnTick;

            private void Update() => OnTick?.Invoke();
        }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
#else
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        private static void Initialize() => CreateInstance();

        protected override void OnInitialized()
        {
            InitializeTicking();
            InitializeTweenPools();
        }

        /// <summary>
        /// Sets up the ticking system and attaches the tick handler.
        /// </summary>
        private void InitializeTicking()
        {
            _activeTweens.Clear();
            _tweenListCache.Clear();
            _tweensByParent.Clear();

            var rootObject = CreateChildTransformForManager("TweenManager").gameObject;
            _tickHandler = rootObject.AddComponent<TickHandler>();
            _tickHandler.OnTick += UpdateTweens;
            
            for (int i = 0; i < 4; ++i)
                _tweenListCache.Push(new List<ITween>(4));
        }

        /// <summary>
        /// Initializes the tween pools or disposes old pools if necessary.
        /// </summary>
        private void InitializeTweenPools()
        {
            if (_tweenPools != null)
                DisposeTweenPools();

            _tweenPools = CreateDefaultTweenPools();
        }

        /// <summary>
        /// Creates the default pools for common tween types.
        /// </summary>
        private static Dictionary<Type, IDisposable> CreateDefaultTweenPools()
        {
            return new Dictionary<Type, IDisposable>
            {
                [typeof(float)] = new ObjectPool<Tween<float>>(() => new FloatTween(), defaultCapacity: DefaultPoolSize, maxSize: MaxPoolSize),
                [typeof(Color)] = new ObjectPool<Tween<Color>>(() => new ColorTween(), defaultCapacity: DefaultPoolSize, maxSize: MaxPoolSize),
                [typeof(Vector2)] = new ObjectPool<Tween<Vector2>>(() => new Vector2Tween(), defaultCapacity: DefaultPoolSize, maxSize: MaxPoolSize),
                [typeof(Vector3)] = new ObjectPool<Tween<Vector3>>(() => new Vector3Tween(), defaultCapacity: DefaultPoolSize, maxSize: MaxPoolSize),
                [typeof(Quaternion)] = new ObjectPool<Tween<Quaternion>>(() => new QuaternionTween(), defaultCapacity: DefaultPoolSize, maxSize: MaxPoolSize),
            };
        }

        /// <summary>
        /// Disposes all existing tween pools.
        /// </summary>
        private static void DisposeTweenPools()
        {
            foreach (var pool in _tweenPools.Values)
            {
                pool.Dispose();
            }
            _tweenPools.Clear();
        }
        #endregion

        #region Ticking
        private readonly Dictionary<Object, List<ITween>> _tweensByParent = new(12);
        private readonly Stack<List<ITween>> _tweenListCache = new(4);
        private readonly List<ITween> _activeTweens = new(12);
        private TickHandler _tickHandler;

        /// <summary>
        /// Registers a tween in the active list.
        /// </summary>
        internal void RegisterTween(ITween tween)
        {
            _activeTweens.Add(tween);
        }

        /// <summary>
        /// Unregisters a tween, removing it from tracking.
        /// </summary>
        internal bool UnregisterTween(ITween tween)
        {
            if (!_activeTweens.Remove(tween))
                return false;

            var parent = tween.GetParent();
            if (parent != null && _tweensByParent.TryGetValue(parent, out var list) && list.Remove(tween))
            {
                if (list.Count == 0)
                {
                    _tweensByParent.Remove(parent);
                    RecycleTweenList(list);
                }
            }

            return true;
        }

        /// <summary>
        /// Reassigns a tween to a new parent, ensuring it is tracked correctly.
        /// </summary>
        internal void AssignTweenParent(ITween tween, Object prevParent = null)
        {
            if (prevParent != null && _tweensByParent.TryGetValue(prevParent, out var oldList))
            {
                oldList.Remove(tween);
                if (oldList.Count == 0)
                {
                    _tweensByParent.Remove(prevParent);
                    RecycleTweenList(oldList);
                }
            }

            var newParent = tween.GetParent();
            if (newParent == null)
                return;

            if (!_tweensByParent.TryGetValue(newParent, out var newList))
            {
                newList = GetOrCreateTweenList();
                _tweensByParent[newParent] = newList;
            }

            newList.Add(tween);
        }

        /// <summary>
        /// Stops and removes all tweens associated with a given parent.
        /// </summary>
        internal void StopAndClearTweensForParent(Object parent, TweenResetBehavior behavior)
        {
            if (_tweensByParent.TryGetValue(parent, out var list))
            {
                _tweensByParent.Remove(parent);
                
                foreach (var tween in list)
                    tween.Release(behavior);

                RecycleTweenList(list);
            }
        }

        /// <summary>
        /// Checks if a given parent has any active tweens.
        /// </summary>
        internal bool HasTweensForParent(Object parent)
            => _tweensByParent.TryGetValue(parent, out var list) && list.Count > 0;

        /// <summary>
        /// Retrieves a tween list from the cache or creates a new one.
        /// </summary>
        private List<ITween> GetOrCreateTweenList()
        {
            return _tweenListCache.Count > 0 ? _tweenListCache.Pop() : new List<ITween>(4);
        }

        /// <summary>
        /// Recycles a tween list by clearing and storing it for reuse.
        /// </summary>
        private void RecycleTweenList(List<ITween> list)
        {
            list.Clear();
            _tweenListCache.Push(list);
        }

        /// <summary>
        /// Updates all active and unscaled tweens by calling their tick method.
        /// </summary>
        private void UpdateTweens()
        {
            for (int i = _activeTweens.Count - 1; i >= 0; i--)
                _activeTweens[i].Tick();
        }
        #endregion

        #region Pooling
        private const int DefaultPoolSize = 4;
        private const int MaxPoolSize = 16;

        private static Dictionary<Type, IDisposable> _tweenPools;

        /// <summary>
        /// Retrieves a tween of type T from the pool. 
        /// </summary>
        internal static Tween<T> GetTween<T>() where T : struct
        {
            Type targetType = typeof(T);

            // Attempt to get the pool for the specified type
            if (_tweenPools.TryGetValue(targetType, out var pool))
            {
                if (pool is ObjectPool<Tween<T>> typedPool)
                {
                    return typedPool.Get();
                }

                Debug.LogError($"Tween pool for type {targetType} is invalid.");
            }
            else
            {
                Debug.LogError($"No tween pool found for type {targetType}. Please initialize the pool for this type.");
            }

            return null;
        }

        /// <summary>
        /// Releases a tween back to the pool.
        /// </summary>
        internal static void ReleaseTween<T>(Tween<T> tween) where T : struct
        {
            Type targetType = typeof(T);

            // Check if the pool exists for the specified type
            if (_tweenPools.TryGetValue(targetType, out var pool))
            {
                if (pool is ObjectPool<Tween<T>> typedPool)
                {
                    typedPool.Release(tween);
                }
                else
                {
                    Debug.LogError($"Tween pool for type {targetType} is invalid.");
                }
            }
            else
            {
                Debug.LogError($"No pool found to release the tween of type {targetType}. Please initialize the pool.");
            }
        }
        #endregion
    }
}