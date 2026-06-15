namespace PolymindGames
{
    /// <summary>
    /// Provides methods for components to receive notifications when their parent poolable object is acquired or returned to the pool.
    /// Implement this interface on sub-components that require updates during the pooling lifecycle.
    /// </summary>
    public interface IPoolableListener : IMonoBehaviour
    {
        /// <summary>
        /// Called when the parent poolable object is acquired from the pool.
        /// </summary>
        void OnAcquired();

        /// <summary>
        /// Called when the parent object is returned to the pool.
        /// </summary>
        void OnReleased();
    }
}