using System.Linq;
using UnityEngine;
using UnityEditor;
using System.IO;

namespace PolymindGames.Editor
{
    public static class DataDefinitionAssetUtility
    {
        private const string DefaultCreationPath = "Assets/TEMP/Resources/Definitions";

        public static T CreateDefinition<T>(T template, string suffix = "") where T : DataDefinition<T>
        {
            string name = template == null ? typeof(T).Name + suffix : template.name + suffix;
            T newDefinition = template == null ? ScriptableObject.CreateInstance<T>() : Object.Instantiate(template);
            newDefinition.name = name;
            newDefinition.Validate_EditorOnly(new DataDefinition.ValidationContext(true,
                DataDefinition.ValidationTrigger.Created));
            DataDefinition<T>.AddDefinition_EditorOnly(newDefinition);
            return newDefinition;
        }

        public static void SaveDefinition<T>(T definition, string assetName) where T : DataDefinition<T>
        {
            string newDefinitionPath = GetUniqueAssetPath<T>(assetName);
            AssetDatabase.CreateAsset(definition, newDefinitionPath);
        }

        public static void DeleteDefinition<T>(T definition) where T : DataDefinition<T>
        {
            DataDefinition<T>.RemoveDefinition_EditorOnly(definition);
            var assetPath = AssetDatabase.GetAssetPath(definition);
            Object.DestroyImmediate(definition, true);
            AssetDatabase.MoveAssetToTrash(assetPath);
        }
        
        public static void RefreshAllDefinitions()
        {
            var definitions = Resources.LoadAll<DataDefinition>(string.Empty);
            foreach (var definition in definitions)
            {
                definition.ValidateInEditor();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Generates a unique path for creating a new asset of type <typeparamref name="T"/>.
        /// </summary>
        /// <returns>A unique path for the asset.</returns>
        public static string GetUniqueAssetPath<T>(string assetName) where T : DataDefinition<T>
        {
            string rootPath = GetAssetRootPath<T>();
            string assetPath = Path.Combine(rootPath, $"{assetName}.asset");
            return AssetDatabase.GenerateUniqueAssetPath(assetPath);
        }

        /// <summary>
        /// Determines the root path for creating assets of type <typeparamref name="T"/>.
        /// </summary>
        /// <returns>The root path for asset creation.</returns>
        private static string GetAssetRootPath<T>() where T : DataDefinition<T>
        {
            var existingDefinitions = DataDefinition<T>.Definitions;

            // Use the directory of the first existing definition if available
            if (existingDefinitions.Length > 0)
            {
                string path = AssetDatabase.GetAssetPath(existingDefinitions[0]);
                if (!string.IsNullOrEmpty(path))
                    return Path.GetDirectoryName(path);
            }

            // Fallback: Find or create a Resources/Definitions folder
            string resourcesFolder = AssetDatabaseUtility.GetAllFoldersByName("Resources").FirstOrDefault();
            string definitionFolderName = typeof(T).Name.Replace("Definition", "");
            string creationPath = !string.IsNullOrEmpty(resourcesFolder)
                ? Path.Combine(resourcesFolder, "Definitions", definitionFolderName)
                : Path.Combine(DefaultCreationPath, definitionFolderName);

            AssetDatabaseUtility.EnsureFolderExists(creationPath);
            return creationPath;
        }
    }
}