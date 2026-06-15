using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.IO;
using UnityEngine;

namespace PolymindGames.Options
{
    /// <summary>
    /// Base class for managing user options stored as ScriptableObjects.
    /// </summary>
    public abstract class UserOptions : ScriptableObject
    {
        protected const string CreateMenuPath = "Polymind Games/Options/";

        /// <summary>
        /// Saves the current options to a file and applies them.
        /// </summary>
        public void Save()
        {
            SaveToFile();
            Apply();
        }

        /// <summary>
        /// Restores the default values for all options and applies them.
        /// </summary>
        public void RestoreDefaults()
        {
            var defaultOptionsInstance = UserOptionsPersistence.LoadDefaultOptionsAsset(GetType());
            defaultOptionsInstance.Reset();

            var defaultOptions = ExtractOptions(defaultOptionsInstance);
            var currentOptions = ExtractOptions(this);

            for (int i = 0; i < currentOptions.Count; i++)
            {
                currentOptions[i].BoxedValue = defaultOptions[i].BoxedValue;
            }

            Apply();
        }

        /// <summary>
        /// Applies changes to the options.
        /// </summary>
        protected virtual void Apply() { }

        /// <summary>
        /// Reset changes to the options.
        /// </summary>
        protected virtual void Reset() { }

        /// <summary>
        /// Loads options from a file and applies them.
        /// </summary>
        protected void Load()
        {
            LoadFromFile();
            Apply();
        }

        /// <summary>
        /// Saves the current options to a JSON file.
        /// </summary>
        private void SaveToFile()
        {
            string json = JsonUtility.ToJson(this, true);
            File.WriteAllText(UserOptionsPersistence.GetSavePath(GetType()), json);
        }

        /// <summary>
        /// Loads the options from a JSON file if it exists.
        /// </summary>
        private void LoadFromFile()
        {
            string savePath = UserOptionsPersistence.GetSavePath(GetType());
            if (!File.Exists(savePath))
                return;

            string json = File.ReadAllText(savePath);
            JsonUtility.FromJsonOverwrite(json, this);
        }

        /// <summary>
        /// Extracts all fields implementing <see cref="IOption"/> from the given <see cref="UserOptions"/> instance.
        /// </summary>
        /// <param name="optionsInstance">The <see cref="UserOptions"/> instance to extract fields from.</param>
        /// <returns>A list of <see cref="IOption"/> objects representing the extracted fields.</returns>
        private static List<IOption> ExtractOptions(UserOptions optionsInstance)
        {
            var extractedOptions = new List<IOption>();
            var fields = optionsInstance.GetType()
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderBy(field => field.MetadataToken);

            foreach (var field in fields)
            {
                if (typeof(IOption).IsAssignableFrom(field.FieldType))
                {
                    IOption option = (IOption)field.GetValue(optionsInstance);
                    if (option != null)
                    {
                        extractedOptions.Add(option);
                    }
                }
            }

            return extractedOptions;
        }
    }

    /// <summary>
    /// Base class for managing user options stored as ScriptableObjects, supporting persistence and default settings.
    /// </summary>
    /// <typeparam name="T">Type of the user options.</typeparam>
    public abstract class UserOptions<T> : UserOptions where T : UserOptions<T>
    {
        private static T _instance;

        /// <summary>
        /// Singleton instance of the user options.
        /// Ensures the options are initialized and loaded as needed.
        /// </summary>
        public static T Instance
        {
            get
            {
                EnsureInstanceInitialized();
                return _instance;
            }
        }

        /// <summary>
        /// Ensures that the singleton instance is created and initialized.
        /// </summary>
        protected static void EnsureInstanceInitialized()
        {
            if (_instance == null)
                _instance = CreateInstance();
        }

        /// <summary>
        /// Creates the user options instance by loading from file or falling back to default asset.
        /// </summary>
        /// <returns>The created and initialized user options instance.</returns>
        private static T CreateInstance()
        {
            bool saveFileExists = File.Exists(UserOptionsPersistence.GetSavePath<T>());
            T instance = saveFileExists ? CreateInstanceFromFile() : CreateInstanceFromDefaultAsset();
            return instance;
        }

        /// <summary>
        /// Loads the user options instance from a saved file.
        /// </summary>
        /// <returns>The user options instance loaded from file.</returns>
        private static T CreateInstanceFromFile()
        {
            var instance = ScriptableObject.CreateInstance<T>();
            instance.Load();
            return instance;
        }

        /// <summary>
        /// Instantiates the user options from a default asset or creates a new instance if no asset is available.
        /// </summary>
        /// <returns>The instantiated or newly created user options instance.</returns>
        private static T CreateInstanceFromDefaultAsset()
        {
            var instance = UserOptionsPersistence.LoadAndCopyDefaultOptionsAsset<T>();
            instance.Reset();
            instance.Save();
            return instance;
        }
    }
}