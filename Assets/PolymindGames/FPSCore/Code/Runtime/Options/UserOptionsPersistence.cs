using UnityEngine;
using System.IO;
using System;

namespace PolymindGames.Options
{
    /// <summary>
    /// Provides utility methods for persisting and retrieving user options as ScriptableObjects.
    /// </summary>
    public static class UserOptionsPersistence
    {
        public const string OptionsAssetPath = "Options/";
        private const string SaveFileExtension = "json";

        /// <summary>
        /// Gets the save path for the specified user options type.
        /// </summary>
        /// <typeparam name="T">The type of user options.</typeparam>
        /// <returns>The save path for the user options file.</returns>
        public static string GetSavePath<T>() where T : UserOptions => GetSavePath(typeof(T));

        /// <summary>
        /// Gets the save path for the specified user options type.
        /// </summary>
        /// <param name="optionsType">The type of user options.</param>
        /// <returns>The save path for the user options file.</returns>
        public static string GetSavePath(Type optionsType)
        {
            if (!typeof(UserOptions).IsAssignableFrom(optionsType))
                throw new ArgumentException($"{optionsType} must inherit from {nameof(UserOptions)}", nameof(optionsType));

            return Path.Combine(GetSaveDirectoryPath(), $"{optionsType.Name}.{SaveFileExtension}");
        }

        /// <summary>
        /// Ensures the save directory for user options exists and returns its path.
        /// </summary>
        /// <returns>The save directory path.</returns>
        private static string GetSaveDirectoryPath()
        {
            string directoryPath = Path.Combine(Application.persistentDataPath, "Options");

            if (!Directory.Exists(directoryPath))
                Directory.CreateDirectory(directoryPath);

            return directoryPath;
        }

        /// <summary>
        /// Loads and copies an instance of the specified user options type from assets.
        /// </summary>
        public static T LoadAndCopyDefaultOptionsAsset<T>() where T : UserOptions<T> =>
            LoadAndCopyDefaultOptionsAsset(typeof(T)) as T;

        /// <summary>
        /// Loads and copies an instance of the specified user options type from assets.
        /// </summary>
        public static UserOptions LoadAndCopyDefaultOptionsAsset(Type optionsType)
        {
            ValidateOptionsType(optionsType);

            string assetPath = $"{OptionsAssetPath}{optionsType.Name}";
            var asset = Resources.Load(assetPath, optionsType) as UserOptions;

            return asset != null ? UnityEngine.Object.Instantiate(asset) : CreateInstance(optionsType);
        }

        /// <summary>
        /// Loads an instance of the specified user options type from assets.
        /// </summary>
        public static T LoadDefautOptionsAsset<T>() where T : UserOptions<T> =>
            LoadDefaultOptionsAsset(typeof(T)) as T;

        /// <summary>
        /// Loads an instance of the specified user options type from assets.
        /// </summary>
        public static UserOptions LoadDefaultOptionsAsset(Type optionsType)
        {
            ValidateOptionsType(optionsType);

            string assetPath = $"{OptionsAssetPath}{optionsType.Name}";
            var asset = Resources.Load(assetPath, optionsType) as UserOptions;

            return asset != null ? asset : CreateInstance(optionsType);
        }

        /// <summary>
        /// Validates that the given type is a valid user options type.
        /// </summary>
        private static void ValidateOptionsType(Type optionsType)
        {
            if (!typeof(UserOptions).IsAssignableFrom(optionsType))
                throw new ArgumentException($"{optionsType} must inherit from {nameof(UserOptions)}", nameof(optionsType));
        }
        
        /// <summary>
        /// Creates a new instance of the specified user options type.
        /// </summary>
        private static UserOptions CreateInstance(Type optionsType) =>
            ScriptableObject.CreateInstance(optionsType) as UserOptions;
    }
}