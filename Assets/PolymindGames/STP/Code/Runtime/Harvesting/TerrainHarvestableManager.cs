using PolymindGames.SurfaceSystem;
using PolymindGames.PoolingSystem;
using System.Collections.Generic;
using PolymindGames.SaveSystem;
using UnityEngine;
using System.Text;
using System;

namespace PolymindGames.ResourceHarvesting
{
    [ExecuteAlways, DisallowMultipleComponent]
    [RequireComponent(typeof(TerrainDataManager))]
    public sealed class TerrainHarvestableManager : MonoBehaviour, ISaveableComponent, IHarvestableResourcesHandler
    {
#if UNITY_EDITOR
        [EditorButton(nameof(Refresh))]
#endif
        [SerializeField, IgnoreParent]
        [ReorderableList(Draggable = false, FixedSize = true)]
        private TreeHarvestablePair[] _treeHarvestableData;

        private readonly Dictionary<int, IHarvestableResource> _activeHarvestables = new();
        private TerrainDataManager _terrainDataManager;
        private int _harvestablesCount;

        /// <inheritdoc/>
        public int ResourcesCount
        {
            get
            {
                if (_harvestablesCount == -1)
                    _harvestablesCount = CalculateHarvestablesCount();

                return _harvestablesCount;
            }
        }

        /// <inheritdoc/>
        public int FindResourceIndex(Vector3 worldPosition, float radius)
        {
            int treeIndex = _terrainDataManager.GetNearestTreeIndexAt(worldPosition, radius);
            return _activeHarvestables.ContainsKey(treeIndex) ? -1 : treeIndex;
        }

        /// <inheritdoc/>
        public Bounds GetHarvestBoundsAt(int resourceIndex)
        {
            if (resourceIndex == -1)
                return default(Bounds);

            var harvestBounds = GetResourceDefinitionAt(resourceIndex).HarvestBounds;
            harvestBounds.center += _terrainDataManager.GetTreeWorldPosition(resourceIndex);
            return harvestBounds;
        }

        /// <inheritdoc/>
        public HarvestableResourceDefinition GetResourceDefinitionAt(int resourceIndex)
        {
            int prototypeIndex = _terrainDataManager.GetTreeInstance(resourceIndex).prototypeIndex;
            return _treeHarvestableData[prototypeIndex].Definition;
        }

        /// <inheritdoc/>
        public bool CanHarvestAt(float harvestPower, Vector3 worldPosition)
        {
            int treeIndex = _terrainDataManager.GetNearestTreeIndexAt(worldPosition);
            if (treeIndex == -1) return false;

            var definition = GetResourceDefinitionAt(treeIndex);
            return definition.IsHarvestPowerSufficient(harvestPower) && IsWithinHarvestBounds(worldPosition, treeIndex, definition);
        }

        /// <inheritdoc/>
        public bool TryHarvestAt(float toolStrength, float harvestAmount, in DamageArgs harvestArgs)
        {
            if (!CanHarvestAt(toolStrength, harvestArgs.HitPoint))
                return false;

            int treeIndex = _terrainDataManager.GetNearestTreeIndexAt(harvestArgs.HitPoint);
            var definition = GetResourceDefinitionAt(treeIndex);

            if (!IsWithinHarvestBounds(harvestArgs.HitPoint, treeIndex, definition))
                return false;

            var resource = CreateResourceForTree(treeIndex, definition);
            return resource.TryHarvest(toolStrength, harvestAmount, in harvestArgs);
        }

        private bool IsWithinHarvestBounds(Vector3 worldPosition, int treeIndex, HarvestableResourceDefinition definition)
        {
            Bounds bounds = definition.HarvestBounds;
            bounds.center += _terrainDataManager.GetTreeWorldPosition(treeIndex);
            return bounds.Contains(worldPosition);
        }

        private IHarvestableResource CreateResourceForTree(int treeIndex, HarvestableResourceDefinition definition)
        {
            IHarvestableResource resource = SpawnHarvestable(treeIndex, definition);
            _activeHarvestables.Add(treeIndex, resource);
            _terrainDataManager.DisableTree(treeIndex);
            return resource;
        }

        private IHarvestableResource SpawnHarvestable(int treeIndex, HarvestableResourceDefinition definition)
        {
            var (position, rotation, scale) = _terrainDataManager.GetTreeWorldTransform(treeIndex);

            IHarvestableResource harvestable = PoolManager.Instance.Get(definition.Prefab, position, rotation);
            harvestable.transform.localScale = scale;

            harvestable.Respawned += OnResourceRespawn;
            return harvestable;
        }

        private void OnResourceRespawn(IHarvestableResource resource)
        {
            int treeIndex = -1;
            foreach (var pair in _activeHarvestables)
            {
                if (pair.Value == resource)
                    treeIndex = pair.Key;
            }

            _terrainDataManager.EnableTree(treeIndex);
            resource.gameObject.GetComponent<Poolable>().Release();
            resource.Respawned -= OnResourceRespawn;
            _activeHarvestables.Remove(treeIndex);
        }

        private int CalculateHarvestablesCount()
        {
            int count = 0;
            int instancesCount = _terrainDataManager.TreeInstancesCount;
            for (int i = 0; i < instancesCount; i++)
            {
                if (_treeHarvestableData[_terrainDataManager.GetTreeInstance(i).prototypeIndex].Definition != null)
                    ++count;
            }

            return count;
        }

        private void Start()
        {
            if (!Application.isPlaying)
                return;

            _terrainDataManager = GetComponent<TerrainDataManager>();
            UpdateTreeHarvestableData(_terrainDataManager.TreePrototypes);

            var scene = gameObject.scene;
            foreach (var data in _treeHarvestableData)
            {
                var definition = data.Definition;
                if (!PoolManager.Instance.HasPool(definition.Prefab))
                {
                    var pool = new SceneObjectPool<HarvestableResource>(definition.Prefab, scene, PoolCategory.HarvestableResources, 4, 32, onPostProcessPrefab: PostProcessHarvestable);
                    PoolManager.Instance.RegisterPool(definition.Prefab, pool);
                }
            }

            return;

            static void PostProcessHarvestable(Component harvestable)
            {
                if (harvestable.TryGetComponent(out SaveableObject saveableObject))
                {
                    saveableObject.IsSaveable = false;
                    saveableObject.ClearGuid();
                }
            }
        }

        /// <summary>
        /// Updates the internal tree-to-harvestable data list based on the provided tree prototypes.
        /// Reuses existing pairs if available; creates new pairs if not.
        /// </summary>
        /// <param name="treePrototypes">The array of current tree prototypes from the terrain.</param>
        /// <returns>True if the harvestable data was updated; otherwise, false.</returns>
        private bool UpdateTreeHarvestableData(TreePrototype[] treePrototypes)
        {
            if (treePrototypes == null || treePrototypes.Length == _treeHarvestableData.Length)
                return false;

            var updatedPairs = new TreeHarvestablePair[treePrototypes.Length];

            for (int i = 0; i < treePrototypes.Length; i++)
            {
                var prototypePrefab = treePrototypes[i].prefab;

                // Reuse matching data from the previous array if possible
                var existingPair = Array.Find(_treeHarvestableData, pair => pair.PrototypeTree == prototypePrefab);

                updatedPairs[i] = existingPair.PrototypeTree != null
                    ? existingPair
                    : new TreeHarvestablePair(prototypePrefab, null);
            }

            _treeHarvestableData = updatedPairs;
            return true;
        }

        #region Save & Load
        void ISaveableComponent.LoadMembers(object data)
        {
            var saveDataArray = (HarvestableSaveData[])data;
            foreach (var saveData in saveDataArray)
            {
                // Retrieve the definition for the tree/resource by its index in the save data
                var definition = GetResourceDefinitionAt(saveData.TreeIndex);

                // Spawn a new harvestable resource based on the saved TreeIndex and definition
                IHarvestableResource resource = CreateResourceForTree(saveData.TreeIndex, definition);

                if (resource.gameObject.TryGetComponent(out SaveableObject saveableObject))
                {
                    saveableObject.ApplySaveData(saveData.SaveData);
                }
            }
        }

        object ISaveableComponent.SaveMembers()
        {
            var saveDataArray = new HarvestableSaveData[_activeHarvestables.Count];

            if (saveDataArray.Length > 0)
            {
                var pathBuilder = new StringBuilder();

                int index = 0;
                foreach (var pair in _activeHarvestables)
                {
                    // Extract and serialize component data from the resource's transform
                    var harvestableTransform = pair.Value.transform;
                    if (harvestableTransform.TryGetComponent(out SaveableObject saveableObject))
                    {
                        var saveData = saveableObject.GenerateSaveData(pathBuilder);
                        saveDataArray[index] = new HarvestableSaveData(pair.Key, saveData);
                    }

                    ++index;
                }
            }

            return saveDataArray;
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        private TerrainData _terrainData;

        private void Reset() => Refresh();
        private void OnValidate() => Refresh();

        private void Refresh()
        {
            if (_terrainData == null)
                _terrainData = GetComponent<Terrain>().terrainData;
            
            if (UpdateTreeHarvestableData(_terrainData.treePrototypes))
                UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
        #endregion

        #region Internal Types
        [Serializable]
        private struct HarvestableSaveData
        {
            public int TreeIndex;
            public ObjectSaveData SaveData;

            public HarvestableSaveData(int treeIndex, ObjectSaveData saveData)
            {
                TreeIndex = treeIndex;
                SaveData = saveData;
            }
        }

        [Serializable]
        private struct TreeHarvestablePair
        {
            [Disable]
            public GameObject PrototypeTree;
            
            [IndentArea]
            public HarvestableResourceDefinition Definition;

            public TreeHarvestablePair(GameObject prototypeTree, HarvestableResourceDefinition definition)
            {
                PrototypeTree = prototypeTree;
                Definition = definition;
            }
        }
        #endregion
    }
}