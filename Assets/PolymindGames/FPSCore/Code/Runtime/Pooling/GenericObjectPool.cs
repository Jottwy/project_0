using UnityEngine.Pool;
using UnityEngine;
using System;

namespace PolymindGames.PoolingSystem
{
    using Object = UnityEngine.Object;

    public sealed class GenericObjectPool<T> : IPool<T> where T : Object
    {
        private readonly ObjectPool<T> _pool;

        /// <summary>
        /// Initializes the object pool with custom creation, get, release, and destroy actions.
        /// </summary>
        /// <param name="createFunc">A function that creates new instances of the pooled object.</param>
        /// <param name="actionOnGet">An optional action invoked when an object is retrieved from the pool.</param>
        /// <param name="actionOnRelease">An optional action invoked when an object is returned to the pool.</param>
        /// <param name="actionOnDestroy">An optional action invoked when an object is destroyed (default is Unity's Object.Destroy).</param>
        /// <param name="initialSize">The number of objects to prewarm in the pool.</param>
        /// <param name="maxSize">The maximum number of objects allowed in the pool.</param>
        public GenericObjectPool(Func<T> createFunc, Action<T> actionOnGet, Action<T> actionOnRelease, Action<T> actionOnDestroy, int initialSize, int maxSize)
        {
            if (createFunc == null)
                throw new ArgumentNullException(nameof(createFunc));

            _pool = new ObjectPool<T>(
                createFunc: createFunc,
                actionOnGet: actionOnGet,
                actionOnRelease: actionOnRelease,
                actionOnDestroy: instance =>
                {
                    if (instance == null)
                        return;
                    
                    if (actionOnDestroy != null)
                        actionOnDestroy.Invoke(instance);
                    else
                        Object.Destroy(instance);
                },
                collectionCheck: false,
                defaultCapacity: initialSize,
                maxSize: maxSize
            );

            Prewarm(initialSize);
        }

        /// <summary>
        /// Initializes the object pool using a template object for instantiation.
        /// </summary>
        /// <param name="template">The template object used to create instances in the pool.</param>
        /// <param name="initialSize">The number of objects to prewarm in the pool.</param>
        /// <param name="maxSize">The maximum number of objects allowed in the pool.</param>
        public GenericObjectPool(T template, int initialSize, int maxSize)
        {
            _pool = new ObjectPool<T>(
                createFunc: () => Object.Instantiate(template),
                actionOnGet: null,
                actionOnRelease: null,
                actionOnDestroy: Object.Destroy,
                collectionCheck: false,
                defaultCapacity: initialSize,
                maxSize: maxSize
            );

            Prewarm(initialSize);
        }

        /// <inheritdoc/>
        public T Get() => _pool.Get();

        /// <inheritdoc/>
        public T Get(Vector3 position, Quaternion rotation, Transform parent = null)
        {
            T instance = Get();

            Transform instanceTransform = null;
            if (instance is GameObject go)
                instanceTransform = go.transform;
            else if (instance is Component component)
                instanceTransform = component.transform;

            if (!ReferenceEquals(instanceTransform, null))
            {
                if (!ReferenceEquals(parent, null))
                    instanceTransform.SetParent(parent);

                instanceTransform.SetPositionAndRotation(position, rotation);
            }

            return instance;
        }

        /// <inheritdoc/>
        public void Release(T instance) => _pool.Release(instance);

        /// <inheritdoc/>
        public int InactiveCount => _pool.CountInactive;

        /// <inheritdoc/>
        public void Clear() => _pool.Clear();

        /// <inheritdoc/>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var instance = _pool.Get();
                _pool.Release(instance);
            }
        }

        /// <inheritdoc/>
        public void Dispose() => _pool.Clear();
    }
}