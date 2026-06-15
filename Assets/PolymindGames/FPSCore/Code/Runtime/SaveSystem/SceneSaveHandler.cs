using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PolymindGames.SaveSystem
{
    /// <summary>
    /// Manages saving and loading operations for a scene, handling registered saveable objects.
    /// </summary>
    [SelectionBase]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(ExecutionOrderConstants.Manager)]
    public class SceneSaveHandler : MonoBehaviour
    {
        [SerializeField, InLineEditor]
        private LevelDefinition _levelDefinition;
        
        [SpaceArea]
        [SerializeField, ReorderableList(HasLabels = false)]
        [Tooltip("Prioritized saveables are saved and loaded before other saveables.")]
        private SaveableObject[] _prioritySaveables;

        private static readonly Dictionary<Scene, SceneSaveHandler> _sceneHandlers = new();
        private readonly Dictionary<SerializedGuid, SaveableObject> _saveableObjectsByGuid = new();
        private readonly List<SaveableObject> _allSaveableObjects = new(256);

        /// <summary>
        /// Retrieves the <see cref="SceneSaveHandler"/> for the specified scene.
        /// </summary>
        /// <param name="scene">The scene to get the save handler for.</param>
        /// <returns>The corresponding <see cref="SceneSaveHandler"/>.</returns>
        public static SceneSaveHandler GetHandler(Scene scene)
            => _sceneHandlers[scene];

        /// <summary>
        /// Tries to retrieve the <see cref="SceneSaveHandler"/> for the specified scene.
        /// </summary>
        /// <param name="scene">The scene to look up.</param>
        /// <param name="handler">The found save handler, or null if not found.</param>
        /// <returns>True if a handler exists for the scene, otherwise false.</returns>
        public static bool TryGetHandler(Scene scene, out SceneSaveHandler handler)
            => _sceneHandlers.TryGetValue(scene, out handler);

        /// <summary>
        /// Gets the scene associated with this handler.
        /// </summary>
        public Scene Scene => gameObject.scene;

        public LevelDefinition LevelDefinition => _levelDefinition;

        /// <summary>
        /// Collects scene-specific metadata. Can be overridden in derived classes.
        /// </summary>
        /// <returns>A dictionary containing scene metadata, or null if not used.</returns>
        public virtual Dictionary<string, object> CollectSceneInfoData() => null;

        /// <summary>
        /// Registers a saveable object, adding it to the save system.
        /// </summary>
        /// <param name="saveable">The saveable object to register.</param>
        public void RegisterSaveable(SaveableObject saveable)
        {
            if (_saveableObjectsByGuid.TryAdd(saveable.InstanceGuid, saveable))
                _allSaveableObjects.Add(saveable);
        }

        /// <summary>
        /// Unregisters a saveable object, removing it from the save system.
        /// </summary>
        /// <param name="saveable">The saveable object to unregister.</param>
        public void UnregisterSaveable(SaveableObject saveable)
        {
            if (!_saveableObjectsByGuid.Remove(saveable.InstanceGuid) || !_allSaveableObjects.Remove(saveable))
                Debug.LogWarning($"Saveable object ({saveable.name}) is not registered and cannot be unregistered.");
        }

        /// <summary>
        /// Generates save data for the scene, including all registered saveable objects.
        /// </summary>
        /// <returns>A <see cref="SceneSaveData"/> object containing serialized scene data.</returns>
        public SceneSaveData GenerateSceneSaveData()
        {
            var pathBuilder = new StringBuilder(64);
            var objectSaveDataArray = new ObjectSaveData[_allSaveableObjects.Count];

            for (int i = 0; i < _allSaveableObjects.Count; i++)
                objectSaveDataArray[i] = _allSaveableObjects[i].GenerateSaveData(pathBuilder);

            string sceneName = gameObject.scene.name;
            return new SceneSaveData(sceneName, objectSaveDataArray);
        }

        /// <summary>
        /// Applies saved scene data, restoring registered saveable objects.
        /// </summary>
        /// <param name="sceneSaveData">The save data to apply.</param>
        public void ApplySceneSaveData(SceneSaveData sceneSaveData)
        {
            // Create a lookup dictionary for the save data
            var objectSaveDataLookup = new Dictionary<SerializedGuid, ObjectSaveData>(sceneSaveData.ObjectData.Length);
            foreach (var objectData in sceneSaveData.ObjectData)
                objectSaveDataLookup.Add(objectData.InstanceGuid, objectData);

            // Update or destroy existing saveable objects
            foreach (var existingSaveable in _allSaveableObjects)
            {
                if (objectSaveDataLookup.TryGetValue(existingSaveable.InstanceGuid, out var saveData))
                {
                    existingSaveable.ApplySaveData(saveData);
                    objectSaveDataLookup.Remove(existingSaveable.InstanceGuid);
                }
                else
                {
                    Destroy(existingSaveable.gameObject);
                }
            }

            // Instantiate and configure new saveable objects
            foreach (var remainingData in objectSaveDataLookup.Values)
            {
                InstantiateSaveableFromData(remainingData);
            }
        }

        /// <summary>
        /// Instantiates a saveable object from its saved data.
        /// </summary>
        /// <param name="saveData">The saved data used to reconstruct the object.</param>
        private static void InstantiateSaveableFromData(ObjectSaveData saveData)
        {
            if (SaveableDatabase.Instance.TryGetPrefabWithGuid(saveData.PrefabGuid, out var prefab))
            {
                var instance = Instantiate(prefab);
                instance.ApplySaveData(saveData);
            }
   #if DEBUG
            else
            {
                Debug.LogWarning($"Prefab with GUID {saveData.PrefabGuid} not found.");
            }
   #endif
        }

        /// <summary>
        /// Registers this scene save handler on enable.
        /// </summary>
        private void OnEnable()
        {
            _sceneHandlers.Add(gameObject.scene, this);
            foreach (var prioritizedSaveable in _prioritySaveables)
                RegisterSaveable(prioritizedSaveable);
        }

        /// <summary>
        /// Unregisters this scene save handler on disable.
        /// </summary>
        private void OnDisable()
        {
            _sceneHandlers.Remove(gameObject.scene);
        }
    }
}
