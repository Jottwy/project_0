using System.Collections.Generic;
using UnityEngine;

namespace PolymindGames.SaveSystem
{
    [DefaultExecutionOrder(ExecutionOrderConstants.Manager)]
    [CreateAssetMenu(menuName = CreateMenuPath + "Saveable Database", fileName = AssetName)]
    public sealed class SaveableDatabase : Manager<SaveableDatabase>, IEditorValidate
    {
        [SerializeField, ScrollableItems(0, 10), Disable]
        [Help("List of all prefabs with the ''Saveable Object'' script attached.")]
        private SaveableObject[] _saveablePrefabs;

        private const string AssetName = "SaveablePrefabsDatabase";
        private Dictionary<SerializedGuid, SaveableObject> _prefabsLookup;

        #region Initialization
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void Init() => LoadOrCreateInstance();

        protected override void OnInitialized()
        {
            if (_prefabsLookup == null || _prefabsLookup.Count != _saveablePrefabs.Length)
                _prefabsLookup = CreatePrefabsLookup();
        }

        private Dictionary<SerializedGuid, SaveableObject> CreatePrefabsLookup()
        {
            var prefabsLookup = new Dictionary<SerializedGuid, SaveableObject>(_saveablePrefabs.Length);
            foreach (var saveable in _saveablePrefabs)
            {
#if UNITY_EDITOR
                if (saveable == null)
                {
                    ResetPrefabs();
                    break;
                }
#endif

                prefabsLookup.Add(saveable.PrefabGuid, saveable);
            }

            return prefabsLookup;
        }


        #endregion

        /// <summary>
        /// Attempts to retrieve a prefab associated with the given GUID.
        /// </summary>
        /// <param name="guid">The GUID of the prefab to look up.</param>
        /// <param name="prefab">The corresponding SaveableObject if found.</param>
        /// <returns>True if the prefab was found; otherwise, false.</returns>
        public bool TryGetPrefabWithGuid(SerializedGuid guid, out SaveableObject prefab)
        {
            return _prefabsLookup.TryGetValue(guid, out prefab);
        }

        #region Editor
#if UNITY_EDITOR
        public void SetPrefabs_Editor(SaveableObject[] saveableObjects)
        {
            _saveablePrefabs = saveableObjects;
            _prefabsLookup = CreatePrefabsLookup();
            UnityEditor.EditorUtility.SetDirty(this);
        }
        
        public static SaveableObject[] FindAllSaveableObjectPrefabs()
        {
            var saveablePrefabs = new List<SaveableObject>();
            var allPrefabs = UnityEditor.AssetDatabase.FindAssets("t:prefab");

            foreach (var prefabGuid in allPrefabs)
            {
                var gameObject = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(UnityEditor.AssetDatabase.GUIDToAssetPath(prefabGuid));
                if (gameObject.TryGetComponent<SaveableObject>(out var saveable))
                {
                    var guid = new System.Guid(prefabGuid);
                    if (saveable.PrefabGuid != guid)
                    {
                        UnityEditor.EditorUtility.SetDirty(saveable);
                        saveable.PrefabGuid = guid;
                    }

                    saveablePrefabs.Add(saveable);
                }
            }

            return saveablePrefabs.ToArray();
        }
        
        private void ResetPrefabs()
        {
            SetPrefabs_Editor(FindAllSaveableObjectPrefabs());
            _prefabsLookup = CreatePrefabsLookup();
        }
        
        void IEditorValidate.ValidateInEditor()
        {
            SetPrefabs_Editor(FindAllSaveableObjectPrefabs());
        }
#endif
        #endregion
    }
}