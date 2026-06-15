using System.Linq;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// A base class for all manager scripts, providing utility methods for singleton instantiation, initialization, and management of the manager's root transform.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.Manager)]
    public abstract class Manager : ScriptableObject
    {
        private static Transform _managersRoot;

        public const string ManagersPath = "Managers/";
        protected const string CreateMenuPath = "Polymind Games/Managers/";

        /// <summary>
        /// Reloads the managers root if it already exists, allowing for reinitialization if needed.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reload()
        {
            if (_managersRoot != null)
                DestroyImmediate(_managersRoot);
        }

        /// <summary>
        /// Creates a new child transform under the managers root.
        /// </summary>
        /// <param name="name">The name for the new transform.</param>
        /// <returns>The created transform.</returns>
        protected static Transform CreateChildTransformForManager(string name)
        {
            if (_managersRoot == null)
                GetManagersRoot();

            var newTransform = new GameObject(name).transform;
            newTransform.parent = _managersRoot;
            return newTransform;
        }

        /// <summary>
        /// Gets or creates the root transform for managers, ensuring it exists in the scene and persists across loads.
        /// </summary>
        /// <returns>The transform of the managers root.</returns>
        protected static Transform GetManagersRoot()
        {
            if (_managersRoot == null)
            {
                var managersRootObj = new GameObject("Managers")
                {
                    tag = TagConstants.GameController
                };

                _managersRoot = managersRootObj.transform;
                DontDestroyOnLoad(managersRootObj);
            }

            return _managersRoot;
        }
    }

    /// <summary>
    /// A generic version of the `Manager` class, representing a singleton manager for a specific type.
    /// </summary>
    /// <typeparam name="T">The type of the manager. Must be derived from `Manager<T>`.</typeparam>
    [DefaultExecutionOrder(ExecutionOrderConstants.Manager)]
    public abstract class Manager<T> : Manager where T : Manager<T>
    {
        public static T Instance { get; private set; }

        /// <summary>
        /// Called when the manager is initialized. Override this to provide additional setup logic.
        /// </summary>
        protected virtual void OnInitialized() { }

        /// <summary>
        /// Loads an existing instance of the manager if available, or creates a new one if not.
        /// Calls the OnInitialized method after loading or creating the instance.
        /// </summary>
        protected static void LoadOrCreateInstance()
        {
            if (Instance == null)
            {
                Instance = LoadInstance();
                if (Instance == null)
                    Instance = CreateInstance<T>();
            }

            Instance.OnInitialized();
        }

        /// <summary>
        /// Creates a new instance of the manager if it doesn't exist.
        /// Calls the OnInitialized method after creating the instance.
        /// </summary>
        protected static void CreateInstance()
        {
            if (Instance == null)
                Instance = CreateInstance<T>();

            Instance.OnInitialized();
        }

        /// <summary>
        /// Loads an existing manager instance from the Resources folder or finds the first one if multiple exist.
        /// </summary>
        /// <returns>The loaded manager instance, or null if no instance was found.</returns>
        private static T LoadInstance()
        {
            T instance;

            string path = ManagersPath + typeof(T).Name;
            instance = Resources.Load<T>(path);

            if (instance == null)
            {
                var managers = Resources.LoadAll<T>(ManagersPath);
                instance = managers.FirstOrDefault();
            }

            return instance;
        }
    }
}