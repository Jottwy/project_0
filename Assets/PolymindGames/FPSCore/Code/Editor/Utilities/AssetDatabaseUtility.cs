using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System.IO;
using System;

namespace PolymindGames.Editor
{
    using UnityObject = UnityEngine.Object;

    /// <summary>
    /// Utility class for working with assets in the Unity Editor.
    /// </summary>
    public static class AssetDatabaseUtility
    {
        public static void ValidateAllAssets(string folderPath = "Assets/PolymindGames/")
        {
            if (string.IsNullOrEmpty(folderPath))
                return;

            // Convert absolute path to relative
            if (folderPath.StartsWith(Application.dataPath))
                folderPath = "Assets" + folderPath.Substring(Application.dataPath.Length);

            var searchFolders = new[] { folderPath };
            var prefabPaths = AssetDatabase.FindAssets("t:Prefab", searchFolders);
            var scriptablePaths = AssetDatabase.FindAssets("t:ScriptableObject", searchFolders);

            int validatedCount = 0;

            foreach (var guid in prefabPaths)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = PrefabUtility.LoadPrefabContents(path);
                
                // Look for MonoBehaviours that implement IEditorValidate
                var validateables = prefab.GetComponentsInChildren<IEditorValidate>(true);

                foreach (var validateable in validateables)
                {
                    validateable.ValidateInEditor();
                    validatedCount++;
                }

                if (validateables.Length > 0)
                {
                    PrefabUtility.SaveAsPrefabAsset(prefab, path);
                }

                PrefabUtility.UnloadPrefabContents(prefab);
            }
            
            foreach (var guid in scriptablePaths)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                ScriptableObject obj = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (obj is IEditorValidate validateable)
                {
                    validateable.ValidateInEditor();
                    EditorUtility.SetDirty(obj);
                    validatedCount++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Validated {validatedCount} asset(s).");
        }
        
        /// <summary>
        /// Finds all files and folders under "Assets/" that contain the specified keyword.
        /// </summary>
        public static List<string> FindAssetPathsContainingString(string relativeRootPath, string keyword)
        {
            List<string> matchingPaths = new List<string>();

            string fullRootPath = Path.Combine(Application.dataPath, relativeRootPath.Substring("Assets/".Length));

            if (!Directory.Exists(fullRootPath))
            {
                Debug.LogError($"Directory does not exist: {relativeRootPath}");
                return matchingPaths;
            }

            // Directories
            foreach (string dir in Directory.GetDirectories(fullRootPath, "*", SearchOption.AllDirectories))
            {
                string relPath = ConvertToAssetRelativePath(dir);
                if (relPath.Contains(keyword))
                    matchingPaths.Add(relPath);
            }

            // Files
            foreach (string file in Directory.GetFiles(fullRootPath, "*", SearchOption.AllDirectories))
            {
                string relPath = ConvertToAssetRelativePath(file);
                if (relPath.Contains(keyword))
                    matchingPaths.Add(relPath);
            }

            return matchingPaths;
        }

        /// <summary>
        /// Deletes all files and folders under "Assets/" that contain the specified keyword.
        /// </summary>
        public static void DeleteAssetPathsContainingString(string relativeRootPath, string keyword)
        {
            List<string> paths = FindAssetPathsContainingString(relativeRootPath, keyword);

            // Reverse sort so files are deleted before folders
            paths.Sort((a, b) => b.Length.CompareTo(a.Length));

            foreach (string path in paths)
            {
                if (AssetDatabase.DeleteAsset(path))
                {
                    Debug.Log($"Deleted: {path}");
                }
            }

            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Converts an absolute file path to a Unity asset-relative path (starting with "Assets/").
        /// </summary>
        private static string ConvertToAssetRelativePath(string absolutePath)
        {
            absolutePath = absolutePath.Replace("\\", "/");
            string projectPath = Application.dataPath.Replace("Assets", "");
            if (absolutePath.StartsWith(projectPath))
            {
                return absolutePath.Substring(projectPath.Length);
            }
            return null;
        }
        
        /// <summary>
        /// Retrieves all subfolder paths matching the specified folder name.
        /// </summary>
        /// <param name="folderName">The name of the folder to search for (e.g., "Resources").</param>
        /// <returns>A list of paths to the matching folders.</returns>
        public static List<string> GetAllFoldersByName(string folderName)
        {
            List<string> matchingFolders = new List<string>();

            // Get all folders in the project
            string[] allFolders = AssetDatabase.GetSubFolders("Assets");

            foreach (string folderPath in allFolders)
            {
                // Check if the folder name matches
                if (folderPath.Contains(folderName))
                {
                    matchingFolders.Add(folderPath);
                }
            }

            return matchingFolders;
        }
        
        /// <summary>
        /// Finds all .unitypackage files under the specified folder path.
        /// </summary>
        /// <param name="folderPath">The folder path to search for .unitypackage files.</param>
        /// <returns>A list containing the paths of all found .unitypackage files.</returns>
        public static List<string> FindAllPackages(string folderPath)
        {
            string[] packageGUIDs = AssetDatabase.FindAssets("t:DefaultAsset", new string[] { folderPath });
            var packagePaths = new List<string>();

            // Convert GUIDs to file paths
            foreach (string packageGuid in packageGUIDs)
            {
                string packagePath = AssetDatabase.GUIDToAssetPath(packageGuid);

                if (packagePath.EndsWith(".unitypackage"))
                {
                    packagePaths.Add(packagePath);
                }
            }

            return packagePaths;
        }

        /// <summary>
        /// Finds and returns the most similar asset name wise.
        /// </summary>
        public static UnityObject FindClosestMatchingObjectWithName(Type assetType, string nameToCompare, string ignoredStr)
        {
            var guids = AssetDatabase.FindAssets($"t:{assetType.Name}");

            int mostSimilarIndex = -1;
            int similarityValue = int.MaxValue;

            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                string assetName = AssetPathToName(assetPath, ignoredStr);
                
                int similarity = assetName.DamerauLevenshteinDistanceTo(nameToCompare);

                if (similarity < similarityValue)
                {
                    similarityValue = similarity;
                    mostSimilarIndex = i;
                }
            }

            return mostSimilarIndex != -1
                ? AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guids[mostSimilarIndex]), assetType)
                : null;
        }

        /// <summary>
        /// Finds and returns the most similar prefab with the given component name wise.
        /// </summary>
        public static Component FindClosestMatchingPrefab(Type componentType, string nameToCompare, string ignoredStr)
        {
            var guids = AssetDatabase.FindAssets("t:Prefab");

            int similarityValue = int.MaxValue;
            int mostSimilarIndex = -1;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var component = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent(componentType);
                if (component == null)
                    continue;

                string prefabName = AssetPathToName(path, ignoredStr);
                int similarity = prefabName.DamerauLevenshteinDistanceTo(nameToCompare);

                if (similarity < similarityValue)
                {
                    similarityValue = similarity;
                    mostSimilarIndex = i;
                }
            }

            return mostSimilarIndex != -1
                ? AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guids[mostSimilarIndex])).GetComponent(componentType)
                : null;
        }

        public static string AssetPathToName(string path, string ignoredStr)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(ignoredStr))
                name = name.Replace(ignoredStr, "");

            return name;
        }
        
        public static void DeleteAllAssetsInFolder(string folderPath, bool includeFolder)
        {
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                Debug.LogWarning("No folder with path: ''{folderPath}'' found.");
                return;
            }

            DeleteAllAssetsInFolder(folderPath);

            if (includeFolder)
                AssetDatabase.DeleteAsset(folderPath);
        }

        private static void DeleteAllAssetsInFolder(string folderPath)
        {
            string[] assetPaths = AssetDatabase.FindAssets("", new[] { folderPath });

            foreach (string assetPath in assetPaths)
            {
                string path = AssetDatabase.GUIDToAssetPath(assetPath);
                if (AssetDatabase.IsValidFolder(path))
                {
                    // Recursively delete contents of subfolders
                    DeleteAllAssetsInFolder(path);
                }
                else
                {
                    // Delete asset
                    AssetDatabase.DeleteAsset(path);
                }
            }
        }

        public static List<string> FindAllAssetsWithExtension(string extension, string[] paths)
        {
            string[] guids = AssetDatabase.FindAssets("", paths);
            var matchingAssetPaths = new List<string>();
            
            foreach (string guid in guids)
            {
                // Check if the asset path ends with the specified extension
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (assetPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    matchingAssetPaths.Add(assetPath);
            }

            return matchingAssetPaths;
        }

        public static void DeleteAssets(string[] assetGuids)
        {
            foreach (string guid in assetGuids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                AssetDatabase.DeleteAsset(assetPath);
            }
        }     
        
        /// </summary>
        /// <param name="assetPath">The path of the asset to rename.</param>
        /// <param name="newName">The new name to set for the asset.</param>
        /// <returns>The unique name assigned to the asset.</returns>
        public static string RenameAssetWithUniqueName(string assetPath, string newName)
        {
            // Generate a unique name if the new name already exists
            string uniquePath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(Path.GetDirectoryName(assetPath) ?? string.Empty, newName + Path.GetExtension(assetPath)));

            // Rename the asset
            AssetDatabase.RenameAsset(assetPath, Path.GetFileName(uniquePath));

            // Refresh the AssetDatabase to apply the changes
            AssetDatabase.Refresh();

            // Return the unique name
            return Path.GetFileNameWithoutExtension(uniquePath);
        }

        /// <summary>
        /// Ensures that the specified folder exists in the Unity Asset Database.
        /// If the folder does not exist, it is created.
        /// </summary>
        /// <param name="folderPath">The relative path to the folder (e.g., "Assets/MyFolder").</param>
        public static void EnsureFolderExists(string folderPath)
        {
            // Ensure the path is relative to the project's Asset folder
            if (!folderPath.StartsWith("Assets"))
            {
                Debug.LogError("Folder path must be relative to the Assets folder.");
                return;
            }

            // Check if the folder already exists
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                // Create the folder recursively
                string parentFolderPath = Path.GetDirectoryName(folderPath);
                string folderName = Path.GetFileName(folderPath);
                if (parentFolderPath != null && !string.IsNullOrEmpty(folderName))
                {
                    EnsureFolderExists(parentFolderPath); // Ensure parent folder exists
                    AssetDatabase.CreateFolder(parentFolderPath, folderName);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    Debug.Log($"Folder '{folderPath}' created successfully.");
                }
                else
                {
                    Debug.LogError("Invalid folder path.");
                }
            }
            else
            {
                Debug.Log($"Folder '{folderPath}' already exists.");
            }
        }
    }
}