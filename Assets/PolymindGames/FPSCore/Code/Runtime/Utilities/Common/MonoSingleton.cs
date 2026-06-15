using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// A generic base class for creating a singleton MonoBehaviour without persistence between scenes.
    /// </summary>
    /// <typeparam name="T">The type of the singleton class.</typeparam>
    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public abstract class MonoSingleton<T> : MonoBehaviour, IMonoBehaviour where T : MonoSingleton<T>
    {
        private static T _instance;

        /// <summary>
        /// Gets the singleton instance of the MonoBehaviour.
        /// </summary>
        public static T Instance
        {
            get
            {
                if (_instance == null)
                    Debug.LogError($"No instance of {typeof(T)} found in the scene.");

                return _instance;
            }
        }

        /// <summary>
        /// Indicates whether the singleton instance exists.
        /// </summary>
        public static bool HasInstance => _instance != null;

        /// <summary>
        /// Initializes the singleton instance. Called when the MonoBehaviour is enabled.
        /// </summary>
        protected virtual void Awake()
        {
            if (_instance == null)
            {
                _instance = this as T;
            }
            else if (_instance != this)
            {
                Debug.LogWarning($"Duplicate instance of {typeof(T)} detected. Destroying the new instance.");
                Destroy(this);
            }
        }

        /// <summary>
        /// Cleans up the singleton instance when the MonoBehaviour is destroyed.
        /// </summary>
        protected virtual void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}