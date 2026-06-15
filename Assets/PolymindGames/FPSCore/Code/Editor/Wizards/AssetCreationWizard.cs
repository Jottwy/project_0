using UnityEditor;
using UnityEngine;
using System.IO;

namespace PolymindGames.Editor
{
    public abstract class AssetCreationWizard : EditorWizardBase
    {
        private const string DefaultCreationPath = "Assets/PoylmindGames/Creations/";
        private string _creationPath;
        
        protected abstract string GetCreationFolderName();

        protected void SaveGameObjectWithName(GameObject gameObject, string prefix = null)
        {
            _creationPath ??= CreateCreationFolder();

            string gameObjectName = string.IsNullOrEmpty(prefix)
                ? $"{gameObject.name.Replace(" ", "")}"
                : $"{prefix}_{gameObject.name.Replace(" ", "")}";
            
            string creationPath = Path.Combine(_creationPath, $"{gameObjectName}.prefab");
            PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, creationPath, InteractionMode.UserAction);
            
            Debug.Log($"Created Game Object with name ''{gameObjectName}'' at path: {creationPath}");
        }

        protected T SaveScriptableObjectWithName<T>(T scriptableObject, string prefix = null) where T : ScriptableObject
        {
            _creationPath ??= CreateCreationFolder();

            string scriptableObjectName = string.IsNullOrEmpty(prefix) ? $"{scriptableObject.name}" : $"{prefix}_{scriptableObject.name}";
            string creationPath = Path.Combine(_creationPath, $"{scriptableObjectName}.asset");

            AssetDatabase.CreateAsset(scriptableObject, creationPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Created {ObjectNames.NicifyVariableName(typeof(T).Name)} with name ''{scriptableObjectName}'' at path: {creationPath}");

            return AssetDatabase.LoadAssetAtPath<T>(creationPath);
        }

        private string CreateCreationFolder()
        {
            AssetDatabaseUtility.EnsureFolderExists(DefaultCreationPath);
            string creationFolder = GetCreationFolderName();
            AssetDatabase.CreateFolder(DefaultCreationPath.TrimEnd('/'), creationFolder);
            return Path.Combine(DefaultCreationPath, creationFolder);
        }
    }
}