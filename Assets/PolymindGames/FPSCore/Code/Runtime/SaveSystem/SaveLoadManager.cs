using PolymindGames.OdinSerializer;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System;

namespace PolymindGames.SaveSystem
{
    /// <summary>
    /// Manages saving, loading, and deleting game save data using OdinSerializer.
    /// Handles multiple save slots and provides efficient file operations.
    /// </summary>
    public static partial class SaveLoadManager
    {
        public const int MaxSaveFiles = 16;
        private const string SaveMetadataFileExtension = ".meta";
        private const string SaveDataFileExtension = ".sav";
        private const string SaveFileName = "Save";
        private static readonly string _saveDirectoryPath;

        static SaveLoadManager()
        {
            _saveDirectoryPath = Path.Combine(Application.persistentDataPath, "Saves");
            if (!Directory.Exists(_saveDirectoryPath))
                Directory.CreateDirectory(_saveDirectoryPath);
        }
        /// <summary>
        /// Determines whether the specified save file index is within the valid range.
        /// </summary>
        /// <param name="saveIndex">The index of the save file.</param>
        /// <returns>True if the save file index is within the allowed range; otherwise, false.</returns>
        public static bool IsSaveIndexInRange(int saveIndex)
            => saveIndex >= 0 && saveIndex <= MaxSaveFiles;

        /// <summary>
        /// Throws an <see cref="IndexOutOfRangeException"/> if the specified save file index is out of range.
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">
        /// Thrown if the save file index is outside the allowed range (0 to <c>MaxSaveFiles</c>).
        /// </exception>
        public static void ThrowIfSaveIndexOutOfRange(int saveIndex)
        {
            if (!IsSaveIndexInRange(saveIndex))
                throw new IndexOutOfRangeException($"The save file index {saveIndex} is out of range; it needs to be between 0 and {MaxSaveFiles}.");
        }

        /// <summary>
        /// Checks if a save file exists for the specified index.
        /// </summary>
        /// <param name="saveIndex">The index of the save file.</param>
        /// <returns>True if the save file exists; otherwise, false.</returns>
        public static bool DoesSaveFileExist(int saveIndex)
            => IsSaveIndexInRange(saveIndex) && File.Exists(GetSceneDataSavePath(saveIndex));

        /// <summary>
        /// Saves the game data to a file.
        /// </summary>
        /// <param name="gameSaveData">The game data to save.</param>
        /// <param name="saveIndex">At which to save the data.</param>
        public static void SaveDataToFile(GameSaveData gameSaveData, int saveIndex)
        {
            ThrowIfSaveIndexOutOfRange(saveIndex);
            
            string sceneDataPath = GetSceneDataSavePath(saveIndex);
            SaveToFile(gameSaveData.SceneData, sceneDataPath);

            string saveMetadataPath = GetMetadataSavePath(saveIndex);
            SaveToFile(gameSaveData.Metadata, saveMetadataPath);
        }

        /// <summary>
        /// Loads the game data from a file by save index.
        /// </summary>
        /// <param name="saveIndex">The index of the save file to load.</param>
        /// <returns>The loaded game data if available; otherwise, throws an exception.</returns>
        public static GameSaveData LoadSaveDataFromFile(int saveIndex)
        {
            var metadata = LoadMetadataFromFile(saveIndex);
            if (metadata == null)
                throw new KeyNotFoundException($"No save metadata with index {saveIndex} found.");

            var sceneSaveData = LoadSceneDataFromFile(saveIndex);
            if (sceneSaveData == null)
                throw new KeyNotFoundException($"No scene save data with index {saveIndex} found.");

            return new GameSaveData(metadata, sceneSaveData);
        }

        /// <summary>
        /// Loads metadata for multiple save files.
        /// </summary>
        /// <param name="count">The number of save files to load metadata for.</param>
        /// <returns>A list of loaded save metadata.</returns>
        public static List<GameMetadata> LoadMetadatas(int count)
        {
            if (count < 0 || count > MaxSaveFiles)
            {
                Debug.LogError($"The max save files count is {MaxSaveFiles}. You're trying to load {count} of them.");
                return null;
            }

            var saves = new List<GameMetadata>();
            for (int i = 0; i < count; i++)
            {
                var metadata = LoadMetadataFromFile(i);
                if (metadata != null)
                    saves.Add(metadata);
            }

            return saves;
        }

        /// <summary>
        /// Loads metadata for the given file with the given index.
        /// </summary>
        public static GameMetadata LoadMetadata(int saveIndex)
        {
            ThrowIfSaveIndexOutOfRange(saveIndex);
            return LoadMetadataFromFile(saveIndex);
        }

        /// <summary>
        /// Deletes a save file by its index.
        /// </summary>
        /// <param name="saveIndex">The index of the save file to delete.</param>
        public static void DeleteSaveFile(int saveIndex)
        {
            string filePath;

            filePath = GetSceneDataSavePath(saveIndex);
            if (File.Exists(filePath))
                File.Delete(filePath);

            filePath = GetMetadataSavePath(saveIndex);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        
        public static T LoadFromFile<T>(string filePath) where T : class
        {
            if (File.Exists(filePath))
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                T data = SerializationUtility.DeserializeValue<T>(bytes, DataFormat.Binary);
                return data;
            }

            return null;
        }

        public static void SaveToFile<T>(T data, string filePath) where T : class
        {
            using Stream stream = File.Open(filePath, FileMode.Create);
            var context = new SerializationContext();
            var writer = new BinaryDataWriter(stream, context);
            SerializationUtility.SerializeValue(data, writer);
        }

        public static SceneSaveData[] LoadSceneDataFromFile(int saveIndex)
        {
            string filePath = GetSceneDataSavePath(saveIndex);
            return LoadFromFile<SceneSaveData[]>(filePath);
        }

        public static GameMetadata LoadMetadataFromFile(int saveIndex)
        {
            string filePath = GetMetadataSavePath(saveIndex);
            return LoadFromFile<GameMetadata>(filePath);
        }

        private static string GetSavePathForIndex(int saveIndex, string extension)
            => Path.Combine(_saveDirectoryPath, $"{SaveFileName}{saveIndex}{extension}");

        private static string GetSceneDataSavePath(int saveIndex)
            => GetSavePathForIndex(saveIndex, SaveDataFileExtension);

        private static string GetMetadataSavePath(int saveIndex)
            => GetSavePathForIndex(saveIndex, SaveMetadataFileExtension);
    }
}