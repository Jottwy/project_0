using UnityEngine;
using System;

namespace PolymindGames.SurfaceSystem
{
    [RequireComponent(typeof(TerrainDataManager))]
    [AddComponentMenu("Polymind Games/Surfaces/Terrain Surface Identity")]
    public sealed class TerrainSurfaceIdentity : SurfaceIdentity<TerrainCollider>
    {
        [IgnoreParent, SerializeField]
        [ReorderableList(Draggable = false, FixedSize = true)]
        private LayerSurfacePair[] _layerSurfaceData = Array.Empty<LayerSurfacePair>();
        
#if UNITY_EDITOR
        [EditorButton(nameof(Refresh))]
#endif
        [IgnoreParent, SerializeField, SpaceArea]
        [ReorderableList(Draggable = false, FixedSize = true)]
        private TreeSurfacePair[] _treeSurfaceData = Array.Empty<TreeSurfacePair>();

        private TerrainDataManager _terrainDataManager;

        protected override SurfaceDefinition GetSurfaceFromHit(TerrainCollider col, in RaycastHit hit)
            => GetSurfaceAtPosition(hit.point);

        protected override SurfaceDefinition GetSurfaceFromCollision(TerrainCollider col, Collision collision)
            => GetSurfaceAtPosition(collision.GetContact(0).point);

        private SurfaceDefinition GetSurfaceAtPosition(Vector3 worldPosition)
        {
            // Check if there's a nearby tree at the given position
            int treeIndex = _terrainDataManager.GetNearestTreeIndexAt(worldPosition);
            if (treeIndex != -1)
            {
                int prototypeIndex = _terrainDataManager.GetTreeInstance(treeIndex).prototypeIndex;
                return _treeSurfaceData[prototypeIndex].Surface.Def;
            }

            // Otherwise, return the dominant terrain layer surface
            int layerIndex = _terrainDataManager.GetDominantTerrainLayerAt(worldPosition);
            return _layerSurfaceData[layerIndex].Surface.Def;
        }

        protected override void Start()
        {
            base.Start();

            if (Application.isPlaying)
            {
                _terrainDataManager = GetComponent<TerrainDataManager>();
                UpdateTerrainLayerSurfaceData(_terrainDataManager.Layers);
                UpdateTreePrototypeSurfaceData(_terrainDataManager.TreePrototypes);
            }
        }

        /// <summary>
        /// Updates the layer-to-surface data pairs based on the current terrain layers.
        /// Returns true if the list was changed.
        /// </summary>
        /// <param name="terrainLayers">The terrain layers to sync.</param>
        /// <returns>True if the layer-surface data was updated; otherwise, false.</returns>
        private bool UpdateTerrainLayerSurfaceData(TerrainLayer[] terrainLayers)
        {
            if (terrainLayers == null || terrainLayers.Length == _layerSurfaceData.Length)
                return false;

            _layerSurfaceData = BuildLayerSurfacePairs(terrainLayers, _layerSurfaceData);
            return true;
        }

        /// <summary>
        /// Updates the tree-to-surface data pairs based on the current tree prototypes.
        /// Returns true if the list was changed.
        /// </summary>
        /// <param name="treePrototypes">The tree prototypes to sync.</param>
        /// <returns>True if the tree-surface data was updated; otherwise, false.</returns>
        private bool UpdateTreePrototypeSurfaceData(TreePrototype[] treePrototypes)
        {
            if (treePrototypes == null || treePrototypes.Length == _treeSurfaceData.Length)
                return false;

            _treeSurfaceData = BuildTreeSurfacePairs(treePrototypes, _treeSurfaceData);
            return true;
        }

        /// <summary>
        /// Creates a new list of layer-surface pairs, reusing existing matches where possible.
        /// </summary>
        /// <param name="layers">The current terrain layers.</param>
        /// <param name="existingPairs">The existing layer-surface pairs.</param>
        /// <returns>An updated array of <see cref="LayerSurfacePair"/>.</returns>
        private static LayerSurfacePair[] BuildLayerSurfacePairs(TerrainLayer[] layers, LayerSurfacePair[] existingPairs)
        {
            var result = new LayerSurfacePair[layers.Length];

            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                var match = Array.Find(existingPairs, pair => pair.Layer == layer);
                result[i] = match.Layer != null
                    ? match
                    : new LayerSurfacePair(layer, DataIdReference<SurfaceDefinition>.NullRef);
            }

            return result;
        }

        /// <summary>
        /// Creates a new list of tree-surface pairs, reusing existing matches where possible.
        /// </summary>
        /// <param name="prototypes">The current tree prototypes.</param>
        /// <param name="existingPairs">The existing tree-surface pairs.</param>
        /// <returns>An updated array of <see cref="TreeSurfacePair"/>.</returns>
        private static TreeSurfacePair[] BuildTreeSurfacePairs(TreePrototype[] prototypes, TreeSurfacePair[] existingPairs)
        {
            var result = new TreeSurfacePair[prototypes.Length];

            for (int i = 0; i < prototypes.Length; i++)
            {
                var prefab = prototypes[i].prefab;
                var match = Array.Find(existingPairs, pair => pair.Tree == prefab);
                result[i] = match.Tree != null
                    ? match
                    : new TreeSurfacePair(prefab, DataIdReference<SurfaceDefinition>.NullRef);
            }

            return result;
        }

        #region Editor
#if UNITY_EDITOR
        private TerrainData _terrainData;

        private void Reset() => Refresh();
        private void OnValidate() => Refresh();

        private void Refresh()
        {
            if (_terrainData == null)
                _terrainData = GetComponent<Terrain>().terrainData;
            
            bool changed = false;
            changed |= UpdateTerrainLayerSurfaceData(_terrainData.terrainLayers);
            changed |= UpdateTreePrototypeSurfaceData(_terrainData.treePrototypes);
            
            if (changed)
                UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
        #endregion

        #region Internal Types
        [Serializable]
        private struct LayerSurfacePair
        {
            [Disable]
            public TerrainLayer Layer;
            
            [IndentArea, DataReference(NullElement = "")]
            public DataIdReference<SurfaceDefinition> Surface;

            public LayerSurfacePair(TerrainLayer layer, DataIdReference<SurfaceDefinition> surface)
            {
                Layer = layer;
                Surface = surface;
            }
        }

        [Serializable]
        private struct TreeSurfacePair
        {
            [Disable]
            public GameObject Tree;
            
            [IndentArea, DataReference(NullElement = "")]
            public DataIdReference<SurfaceDefinition> Surface;

            public TreeSurfacePair(GameObject tree, DataIdReference<SurfaceDefinition> surface)
            {
                Tree = tree;
                Surface = surface;
            }
        }
    #endregion
    }
}