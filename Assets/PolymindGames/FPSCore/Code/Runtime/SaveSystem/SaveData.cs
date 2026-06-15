using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames.SaveSystem
{
    /// <summary>
    /// Represents the save data for the entire game, including game-specific metadata and all scene data.
    /// </summary>
    [Serializable]
    public sealed class GameSaveData
    {
        [SerializeField]
        private GameMetadata _metadata;

        [SerializeField]
        private SceneSaveData[] _sceneSaveDataArray;

        /// <summary>
        /// Initializes a new instance of the <see cref="GameSaveData"/> class with metadata and scene save data.
        /// </summary>
        /// <param name="metadata">General information about the game save (e.g., save name, timestamp).</param>
        /// <param name="sceneSaveDataArray">The save data for all scenes in the game.</param>
        public GameSaveData(GameMetadata metadata, params SceneSaveData[] sceneSaveDataArray)
        {
            _metadata = metadata;
            _sceneSaveDataArray = sceneSaveDataArray;
        }

        /// <summary>
        /// Gets the array of scene save data.
        /// </summary>
        public SceneSaveData[] SceneData => _sceneSaveDataArray;

        /// <summary>
        /// Gets the general metadata about the game save.
        /// </summary>
        public GameMetadata Metadata => _metadata;
    }
    
    /// <summary>
    /// Represents the save data for a single scene, including the scene name and data for all saveable objects within it.
    /// </summary>
    [Serializable]
    public sealed class SceneSaveData
    {
        [SerializeField]
        private string _sceneName;

        [SerializeField]
        private ObjectSaveData[] _objectSaveDataArray;

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneSaveData"/> class with a scene name and save data for objects.
        /// </summary>
        /// <param name="sceneName">The name of the scene.</param>
        /// <param name="objectSaveDataArray">The save data for all objects in the scene.</param>
        public SceneSaveData(string sceneName, ObjectSaveData[] objectSaveDataArray)
        {
            _sceneName = sceneName;
            _objectSaveDataArray = objectSaveDataArray;
        }

        /// <summary>
        /// Gets the name of the scene.
        /// </summary>
        public string SceneName => _sceneName;

        /// <summary>
        /// Gets the array of save data for all objects in the scene.
        /// </summary>
        public ObjectSaveData[] ObjectData => _objectSaveDataArray;
    }

    /// <summary>
    /// Represents the save data for an individual object, including its transform, components, and physics data.
    /// </summary>
    [Serializable]
    public sealed class ObjectSaveData
    {
        /// <summary>
        /// Gets or sets the unique identifier for the object's prefab.
        /// </summary>
        public SerializedGuid PrefabGuid;

        /// <summary>
        /// Gets or sets the unique identifier for the specific instance of the object.
        /// </summary>
        public SerializedGuid InstanceGuid;

        /// <summary>
        /// Gets or sets the primary transform data of the object.
        /// </summary>
        public SerializedTransformData Transform;

        /// <summary>
        /// Gets or sets the additional transform data for any child objects or related transforms.
        /// </summary>
        public SerializedTransformData[] AdditionalTransforms;

        /// <summary>
        /// Gets or sets the serialized data for all components attached to the object.
        /// </summary>
        public SerializedComponentData[] ComponentData;

        /// <summary>
        /// Gets or sets the serialized data for any rigidbodies associated with the object.
        /// </summary>
        public SerializedRigidbodyData[] RigidbodyData;
    }

    /// <summary>
    /// Contains metadata for a game save, including its identifier, name, timestamp, thumbnail, and associated scene data.
    /// </summary>
    [Serializable]
    public sealed class GameMetadata
    {
        [SerializeField]
        private SerializedGuid _saveId;
        
        [SerializeField]
        private int _saveIndex;

        [SerializeField]
        private string _sceneName;        
        
        [SerializeField]
        private string _levelName;

        [SerializeField]
        private SerializedImage _thumbnail;

        [SerializeField]
        private SerializedDateTime _saveTimestamp;

        [SerializeField]
        private SerializedDictionary<string, string> _sceneData;

        /// <summary>
        /// Constructor to initialize a new instance of GameSaveInfo.
        /// </summary>
        public GameMetadata(Guid saveId, int saveIndex, string sceneName, string levelName, DateTime saveTime, Texture2D thumbnail, Dictionary<string, object> sceneData)
        {
            _sceneName = sceneName;
            _levelName = levelName;
            _saveId = new SerializedGuid(saveId);
            _saveIndex = saveIndex;
            _saveTimestamp = new SerializedDateTime(saveTime);
            _thumbnail = new SerializedImage(thumbnail);
            _sceneData = SerializeSceneData(sceneData);
        }
        
        private GameMetadata() { }

        /// <summary>
        /// Unique identifier for the save.
        /// </summary>
        public Guid SaveId => _saveId;

        /// <summary>
        /// The index of this save.
        /// </summary>
        public int SaveIndex => _saveIndex;

        /// <summary>
        /// The name of the scene this save is associated with.
        /// </summary>
        public string SceneName => _sceneName;
        
        /// <summary>
        /// The name of the map this save is associated with.
        /// </summary>
        public string LevelName => _levelName;

        /// <summary>
        /// Timestamp of when the save was created.
        /// </summary>
        public DateTime SaveTimestamp => _saveTimestamp;

        /// <summary>
        /// Screenshot associated with this save.
        /// </summary>
        public Texture2D Thumbnail => _thumbnail;

        /// <summary>
        /// Attempts to retrieve a specific saved value by key.
        /// </summary>
        /// <typeparam name="T">The type of the value.</typeparam>
        /// <param name="key">The key of the value to retrieve.</param>
        /// <param name="value">The retrieved value if the key exists.</param>
        /// <returns>True if the value exists; otherwise, false.</returns>
        public bool TryGetSavedValue<T>(string key, out T value)
        {
            if (_sceneData.TryGetValue(key, out var jsonData))
            {
                value = JsonUtility.FromJson<T>(jsonData);
                return true;
            }

            value = default(T);
            return false;
        }

        /// <summary>
        /// Serializes scene-related data into a dictionary format.
        /// </summary>
        /// <param name="sceneData">The raw scene data to serialize.</param>
        /// <returns>A serialized dictionary of scene data.</returns>
        private static SerializedDictionary<string, string> SerializeSceneData(Dictionary<string, object> sceneData)
        {
            var serializedData = new SerializedDictionary<string, string>();

            if (sceneData == null) return serializedData;

            foreach (var entry in sceneData)
            {
                if (entry.Value != null)
                {
                    string json = JsonUtility.ToJson(entry.Value);
                    serializedData.Add(entry.Key, json);
                }
            }

            return serializedData;
        }
    }
}