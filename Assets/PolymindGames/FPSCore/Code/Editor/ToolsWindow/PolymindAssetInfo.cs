using PolymindGames.SaveSystem;
using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor;
using UnityEngine;
using System.Linq;
using System;
using System.IO;
using UnityEditor.Build.Profile;

namespace PolymindGames.Editor
{
    [CreateAssetMenu(menuName = "Polymind Games/Editor/Asset Info", fileName = "Asset Info")]
    public sealed class PolymindAssetInfo : ScriptableObject
    {
        [Serializable]
        public struct SiteInfo
        {
            public string Name;
            public string Url;

            public SiteInfo(string name, string url)
            {
                Name = name;
                Url = url;
            }
        }

        [SerializeField, BeginGroup]
        private string _assetName;

        [SerializeField]
        private Texture2D _icon;

        [SerializeField, Multiline]
        private string _description;

        [SerializeField]
        private int _priority;

        [SerializeField]
        private string _versionString;

        [SerializeField, EndGroup]
        private bool _requiresValidation;

        [SerializeField, ReorderableList(HasLabels = false)]
        private SerializedScene[] _scenes = Array.Empty<SerializedScene>();

        [SerializeField, BeginGroup]
        private string _storeUrl;

        [SerializeField, EndGroup]
        private string _docsUrl;

        [EditorButton(nameof(SetAsActiveAsset))]
        [SerializeField, ReorderableList(elementLabel: "Site")]
        private SiteInfo[] _extraSites = Array.Empty<SiteInfo>();

        public const string YoutubeURL = "https://www.youtube.com/channel/UCYqSdzP7URQzOVlWr-M5Krg";
        public const string DiscordURL = "https://discord.com/invite/pkwPNEy";
        public const string SupportURL = "mailto:" + "Polymindgames@gmail.com";

        public Texture2D Icon => _icon;
        public string AssetName => _assetName;
        public string VersionStr => _versionString;

        public bool RequiresValidation
        {
            get => _requiresValidation;
            set => _requiresValidation = value;
        }

        public string StoreUrl => _storeUrl;
        public string DocsUrl => _docsUrl;
        public string Description => _description;
        public IEnumerable<SerializedScene> Scenes => _scenes;
        public IEnumerable<SiteInfo> ExtraSites => _extraSites;

        public static bool IsUnityVersionValid()
        {
#if UNITY_6000_0_OR_NEWER
            return true;
#else
            return false;
#endif
        }

        public static PolymindAssetInfo[] GetAll()
        {
            var assetInfos = Resources.LoadAll<PolymindAssetInfo>("Editor/");
            return assetInfos != null ? assetInfos.OrderByDescending(info => info._priority).ToArray() : Array.Empty<PolymindAssetInfo>();
        }

        public bool AreRequirementsMet()
        {
            return IsUnityVersionValid() && !_requiresValidation;
        }

#if UNITY_EDITOR
        private void SetAsActiveAsset()
        {
            DataDefinitionAssetUtility.RefreshAllDefinitions();

            var saveableDatabase = Resources.LoadAll<SaveableDatabase>(Manager.ManagersPath).FirstOrDefault();
            saveableDatabase?.SetPrefabs_Editor(SaveableDatabase.FindAllSaveableObjectPrefabs());
            
            PlayerSettings.companyName = "Polymind Games";
            PlayerSettings.productName = _assetName;
            PlayerSettings.bundleVersion = _versionString;

            Texture2D[] icons = new Texture2D[8];
            icons[0] = _icon;
            for (int i = 1; i < 8; i++)
                icons[i] = null;

            PlayerSettings.SetIcons(NamedBuildTarget.Standalone, icons, IconKind.Any);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            ReplaceBuildScenes(_scenes.Select(scene => GetScenePathByName(scene.SceneReference.name)).ToArray());
        }
        
        /// <summary>
        /// Finds the path of a scene by its file name (e.g., "MainMenu" without .unity).
        /// </summary>
        public static string GetScenePathByName(string sceneName)
        {
            string[] guids = AssetDatabase.FindAssets("t:Scene " + sceneName);
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == sceneName)
                {
                    return path;
                }
            }

            return null; // Not found
        }
        
        /// <summary>
        /// Replaces all scenes in the Build Settings with the given scene paths.
        /// </summary>
        /// <param name="scenePaths">Array of scene asset paths (e.g., "Assets/Scenes/Main.unity").</param>
        public static void ReplaceBuildScenes(string[] scenePaths)
        {
            EditorBuildSettingsScene[] newScenes = new EditorBuildSettingsScene[scenePaths.Length];

            for (int i = 0; i < scenePaths.Length; i++)
            {
                newScenes[i] = new EditorBuildSettingsScene(scenePaths[i], true);
            }

            EditorBuildSettings.scenes = newScenes;

            Debug.Log("Build settings scenes updated.");
        }
#endif
    }
}