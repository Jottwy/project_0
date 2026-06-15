using UnityEngine.Events;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor.SceneManagement;
using UnityEditor;
#endif

namespace PolymindGames
{
    /// <summary>
    /// Provides utility methods for runtime operations in Unity.
    /// </summary>
    public static partial class UnityUtility
    {
        private static Camera _cachedMainCamera;

        /// <summary>
        /// Indicates whether the game is currently in play mode.
        /// </summary>
        public static bool IsQuitting { get; private set; }

        /// <summary>
        /// Gets the main camera, caching it for subsequent calls.
        /// </summary>
        public static Camera CachedMainCamera
        {
            get
            {
                if (_cachedMainCamera == null)
                    _cachedMainCamera = Camera.main;

                return _cachedMainCamera;
            }
        }

        /// <summary>
        /// Locks the cursor.
        /// </summary>
        public static void LockCursor()
        {
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        /// <summary>
        /// Unlocks the cursor.
        /// </summary>
        public static void UnlockCursor()
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            IsQuitting = false;
            Application.quitting += Quit;

            static void Quit()
            {
                IsQuitting = true;
                Application.quitting -= Quit;
            }
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Provides utility methods for editor-specific operations in Unity.
    /// </summary>
    public static partial class UnityUtility
    {
        /// <summary>
        /// Sometimes, when you use Unity's built-in OnValidate, it will spam you with a very annoying warning message,
        /// even though nothing has gone wrong. To avoid this, you can run your OnValidate code through this utility.
        /// Runs <paramref name="onValidateAction"/> once, after all inspectors have been updated.
        /// </summary>
        public static void SafeOnValidate(Object obj, UnityAction onValidateAction)
        {
            EditorApplication.delayCall += OnValidate;
            return;

            void OnValidate()
            {
                EditorApplication.delayCall -= OnValidate;

                // Important: this function could be called after the object has been destroyed
                // (ex: during reinitialization when entering Play Mode, when saving it in a prefab...),
                // so to prevent a potential ArgumentNullException, we check if the object is null.
                // Note: the components we want to modify could also be in this "destroyed" state
                // and trigger an ArgumentNullException.

                // We also check if object is dirty, this is to prevent the scene to be marked
                // as dirty as soon as we load it (because we will dirty some components in this function).

                if (obj == null || !EditorUtility.IsDirty(obj))
                    return;

                onValidateAction();
            }
        }

        /// <summary>
        /// Determines whether a given component is part of a prefab asset or is being edited in prefab mode.
        /// </summary>
        /// <param name="component">The component to check.</param>
        /// <returns>True if the component is part of a prefab asset or is being edited in prefab mode; otherwise, false.</returns>
        public static bool IsAssetOnDisk(Component component)
        {
            return PrefabUtility.IsPartOfPrefabAsset(component) || IsEditingInPrefabMode(component);
        }

        private static bool IsEditingInPrefabMode(Component component)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (component == null || component.gameObject == null)
                return false;

            if (EditorUtility.IsPersistent(component))
            {
                // if the game object is stored on disk, it is a prefab of some kind, despite not returning true for IsPartOfPrefabAsset =/
                return true;
            }

            // If the GameObject is not persistent let's determine which stage we are in first because getting Prefab info depends on it
            var mainStage = StageUtility.GetMainStageHandle();
            var currentStage = StageUtility.GetStageHandle(component.gameObject);
            if (currentStage != mainStage)
            {
                var prefabStage = PrefabStageUtility.GetPrefabStage(component.gameObject);
                if (prefabStage != null)
                    return true;
            }
            return false;
        }
    }
#endif
}