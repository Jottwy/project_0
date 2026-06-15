using System;
using UnityEngine;

namespace PolymindGames.PoolingSystem
{
    /// <summary>
    /// Represents a base interface for an object pool.
    /// Provides functionality for managing pool size, pre-warming, and clearing the pool.
    /// </summary>
    public interface IPoolBase : IDisposable
    {
        /// <summary>
        /// Gets the count of inactive objects currently in the pool.
        /// </summary>
        int InactiveCount { get; }

        /// <summary>
        /// Pre-warms the pool by acquiring and releasing a specified number of objects.
        /// Ensures the pool is populated with a minimum number of objects.
        /// </summary>
        /// <param name="count">The number of objects to pre-warm in the pool.</param>
        void Prewarm(int count);

        /// <summary>
        /// Clears the pool, releasing and disposing of all objects in it.
        /// </summary>
        void Clear();
    }

    /// <summary>
    /// Represents a generic object pool interface for managing instances of type <typeparamref name="T"/>.
    /// Inherits from <see cref="IPoolBase"/> to include basic pool management functionality.
    /// </summary>
    /// <typeparam name="T">The type of objects to be pooled. It must be a reference type.</typeparam>
    public interface IPool<T> : IPoolBase where T : class
    {
        /// <summary>
        /// Acquires an object from the pool.
        /// </summary>
        /// <returns>An object from the pool.</returns>
        T Get();
        
        /// <summary>
        /// Acquires an object from the pool.
        /// </summary>
        /// <returns>An object from the pool.</returns>
        T Get(Vector3 position, Quaternion rotation, Transform parent = null);

        /// <summary>
        /// Releases an object back into the pool, making it available for future use.
        /// </summary>
        /// <param name="instance">The object to release back into the pool.</param>
        void Release(T instance);
    }
}