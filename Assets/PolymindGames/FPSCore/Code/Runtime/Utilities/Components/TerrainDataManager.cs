using UnityEngine;
using System;

namespace PolymindGames.SurfaceSystem
{
    /// <summary>
    /// Manages terrain-related data and operations for optimized queries and transformations. 
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public sealed class TerrainDataManager : MonoBehaviour
    {
        private TreeInstance[] _currentTreeInstances;
        private Coroutine _refreshColliderCoroutine;
        private TerrainLayer[] _terrainlayers;
        private TreePrototype[] _treePrototypes;
        private Vector2[] _worldTreePositions;
        private Vector3 _terrainWorldOrigin;
        private TerrainCollider _collider;
        private TerrainData _terrainData;
        private Vector2 _alphamapSize;
        private Vector3 _terrainSize;
        private QueryCache _cache;

        private const float MinTreeHeightThreshold = 0.25f;
        private const float TreeSearchRadius = 1f;
        private const float MinTreeScale = 0.9f;
        private const float MaxTreeScale = 1.1f;

        /// <summary>
        /// Gets the current tree prototypes on the terrain.
        /// </summary>
        public TreePrototype[] TreePrototypes => _treePrototypes;

        /// <summary>
        /// Gets the current layers on the terrain.
        /// </summary>
        public TerrainLayer[] Layers => _terrainlayers;

        /// <summary>
        /// Gets the count of current tree instances.
        /// </summary>
        public int TreeInstancesCount => _currentTreeInstances.Length;

        /// <summary>
        /// Returns the index of the dominant terrain layer at a given world position.
        /// If the position is cached, it returns the cached layer index; otherwise, it calculates it.
        /// </summary>
        /// <param name="worldPosition">The world position to check for the dominant terrain layer.</param>
        /// <returns>The index of the dominant terrain layer.</returns>
        public int GetDominantTerrainLayerAt(Vector3 worldPosition)
        {
            if (_cache.IsValid(worldPosition))
            {
                return _cache.LayerIndex;
            }

            Vector2Int alphaCoords = WorldToAlphaMapCoords(worldPosition);
            int layerIndex = GetLayerWithMaxBlend(alphaCoords);

            UpdateCache(worldPosition, layerIndex, -1);
            return layerIndex;
        }

        /// <summary>
        /// Returns the index of the nearest tree at a given world position, or -1 if none is found.
        /// If the position is cached, it returns the cached tree index; otherwise, it calculates it.
        /// </summary>
        /// <param name="worldPosition">The world position to check for the nearest tree.</param>
        /// <param name="radius">The radius to use.</param>
        /// <returns>The index of the nearest tree, or -1 if no tree is found.</returns>
        public int GetNearestTreeIndexAt(Vector3 worldPosition, float radius = TreeSearchRadius)
        {
            if (_cache.IsValid(worldPosition))
                return _cache.TreeIndex;

            if (!IsAboveTerrain(worldPosition))
                return -1;

            Vector2 target2D = new Vector2(worldPosition.x, worldPosition.z);
            int treeIndex = radius > 0.51f ? FindClosestTreeIndexLinear(target2D, radius) : FindClosestTreeIndexBinary(target2D, radius);

            UpdateCache(worldPosition, _cache.LayerIndex, treeIndex);
            return treeIndex;
        }

        /// <summary>
        /// Retrieves the tree instance at a specific index.
        /// </summary>
        /// <param name="index">The index of the tree instance to retrieve.</param>
        /// <returns>The TreeInstance at the specified index.</returns>
        public TreeInstance GetTreeInstance(int index)
        {
            return _currentTreeInstances[index];
        }

        /// <summary>
        /// Enables a tree instance at the specified index and updates its scale.
        /// Optionally queues a refresh of the collider.
        /// </summary>
        /// <param name="index">The index of the tree instance to enable.</param>
        /// <param name="queueColliderRefresh">Whether to queue a collider refresh (default is true).</param>
        public void EnableTree(int index, bool queueColliderRefresh = true)
        {
            var instance = _currentTreeInstances[index];
            float scale = UnityEngine.Random.Range(MinTreeScale, MaxTreeScale);
            instance.widthScale = scale;
            instance.heightScale = scale;
            _currentTreeInstances[index] = instance;
            _terrainData.SetTreeInstance(index, instance);

            if (queueColliderRefresh)
                QueueColliderRefresh();
        }

        /// <summary>
        /// Disables a tree instance at the specified index by setting its scale to zero.
        /// Optionally queues a refresh of the collider.
        /// </summary>
        /// <param name="index">The index of the tree instance to disable.</param>
        /// <param name="queueColliderRefresh">Whether to queue a collider refresh (default is true).</param>
        public void DisableTree(int index, bool queueColliderRefresh = true)
        {
            var instance = _currentTreeInstances[index];
            instance.widthScale = 0f;
            instance.heightScale = 0f;
            _currentTreeInstances[index] = instance;
            _terrainData.SetTreeInstance(index, instance);

            if (queueColliderRefresh)
                QueueColliderRefresh();
        }

        /// <summary>
        /// Retrieves the world position, rotation, and scale of a tree instance.
        /// </summary>
        /// <param name="treeIndex">The index of the tree instance in the terrain's instance list.</param>
        /// <returns>A tuple containing the tree's position, rotation, and scale in world space.</returns>
        public (Vector3 position, Quaternion rotation, Vector3 scale) GetTreeWorldTransform(int treeIndex)
        {
            var treeInstance = _currentTreeInstances[treeIndex];

            // Calculate world position
            Vector3 position = _terrainWorldOrigin + new Vector3(
                treeInstance.position.x * _terrainSize.x,
                treeInstance.position.y * _terrainSize.y,
                treeInstance.position.z * _terrainSize.z
            );

            // Calculate world rotation
            Quaternion rotation = Quaternion.Euler(0f, treeInstance.rotation * Mathf.Rad2Deg, 0f);

            // Calculate world scale
            Vector3 scale = new Vector3(treeInstance.widthScale, treeInstance.heightScale, treeInstance.widthScale);

            return (position, rotation, scale);
        }

        /// <summary>
        /// Updates the transform of a tree instance in the terrain by converting world-space 
        /// position, rotation, and scale into the terrain's normalized coordinates.
        /// </summary>
        /// <param name="treeIndex">The index of the tree instance to update.</param>
        /// <param name="worldPosition">The world position to set for the tree instance.</param>
        /// <param name="worldRotation">The world rotation to set for the tree instance.</param>
        /// <param name="worldScale">The world scale to set for the tree instance.</param>
        /// <param name="refreshCollider"></param>
        public void SetTreeTransform(int treeIndex, Vector3 worldPosition, Quaternion worldRotation, Vector3 worldScale, bool refreshCollider = true)
        {
            // Calculate normalized local position within the terrain
            Vector3 normalizedPosition = new Vector3(
                (worldPosition.x - _terrainWorldOrigin.x) / _terrainSize.x,
                (worldPosition.y - _terrainWorldOrigin.y) / _terrainSize.y,
                (worldPosition.z - _terrainWorldOrigin.z) / _terrainSize.z
            );

            // Extract the Y-axis rotation in radians
            float normalizedRotation = worldRotation.eulerAngles.y * Mathf.Deg2Rad;

            // Extract width and height scales
            float widthScale = worldScale.x; // Assuming uniform scaling on X and Z
            float heightScale = worldScale.y;

            // Update the tree instance with the new transform data
            var newInstance = _currentTreeInstances[treeIndex];
            newInstance.position = normalizedPosition;
            newInstance.rotation = normalizedRotation;
            newInstance.widthScale = widthScale;
            newInstance.heightScale = heightScale;

            _currentTreeInstances[treeIndex] = newInstance;
            _terrainData.SetTreeInstance(treeIndex, newInstance);

            if (refreshCollider)
                QueueColliderRefresh();
        }

        /// <summary>
        /// Retrieves the world rotation of a tree instance.
        /// </summary>
        /// <param name="treeIndex">The index of the tree instance in the terrain's instance list.</param>
        /// <returns>The rotation of the tree instance in world space as a Quaternion.</returns>
        public Quaternion GetTreeWorldRotation(int treeIndex)
        {
            var treeInstance = _currentTreeInstances[treeIndex];
            return Quaternion.Euler(0f, treeInstance.rotation * Mathf.Rad2Deg, 0f);
        }

        /// <summary>
        /// Retrieves the world scale of a tree instance.
        /// </summary>
        /// <param name="treeIndex">The index of the tree instance in the terrain's instance list.</param>
        /// <returns>A Vector3 representing the scale of the tree instance in world space.</returns>
        public Vector3 GetTreeWorldScale(int treeIndex)
        {
            var treeInstance = _currentTreeInstances[treeIndex];
            return new Vector3(treeInstance.widthScale, treeInstance.heightScale, treeInstance.widthScale);
        }

        /// <summary>
        /// Converts the normalized local position of a tree instance to its world position.
        /// </summary>
        /// <param name="treeIndex">The index of the tree instance in the terrain's instance list.</param>
        /// <returns>The world position of the tree instance as a Vector3.</returns>
        public Vector3 GetTreeWorldPosition(int treeIndex)
        {
            var treeInstance = _currentTreeInstances[treeIndex];

            // Convert normalized local tree position to world position
            Vector3 localPosition = new Vector3(
                treeInstance.position.x * _terrainSize.x, // X-axis position
                treeInstance.position.y * _terrainSize.y, // Y-axis height (elevation)
                treeInstance.position.z * _terrainSize.z  // Z-axis position
            );

            // Return the world position, offset by terrain origin
            return _terrainWorldOrigin + localPosition;
        }

        /// <summary>
        /// Updates the shared cache with the latest query results.
        /// </summary>
        private void UpdateCache(Vector3 position, int layerIndex, int treeIndex)
            => _cache = new QueryCache(position, layerIndex, treeIndex);

        /// <summary>
        /// Converts a world position to alpha map coordinates for terrain layers.
        /// </summary>
        private Vector2Int WorldToAlphaMapCoords(Vector3 worldPosition)
        {
            int alphaX = (int)((worldPosition.x - _terrainWorldOrigin.x) / _terrainSize.x * _alphamapSize.x);
            int alphaZ = (int)((worldPosition.z - _terrainWorldOrigin.z) / _terrainSize.z * _alphamapSize.y);
            return new Vector2Int(alphaX, alphaZ);
        }

        /// <summary>
        /// Checks if the world position is above the terrain height, accounting for a threshold.
        /// </summary>
        private bool IsAboveTerrain(Vector3 worldPosition)
        {
            Vector2Int alphaCoords = WorldToAlphaMapCoords(worldPosition);
            float terrainHeight = _terrainData.GetHeight(alphaCoords.x, alphaCoords.y) + _terrainWorldOrigin.y;
            return worldPosition.y > terrainHeight + MinTreeHeightThreshold;
        }

        /// <summary>
        /// Retrieves the layer with the highest blend value at the specified alpha map coordinates.
        /// </summary>
        private int GetLayerWithMaxBlend(Vector2Int coords)
        {
            float[,,] splatData = _terrainData.GetAlphamaps(coords.x, coords.y, 1, 1);
            int dominantLayer = 0;
            float maxBlend = 0f;

            for (int i = 0; i < splatData.GetLength(2); i++)
            {
                if (splatData[0, 0, i] > maxBlend)
                {
                    maxBlend = splatData[0, 0, i];
                    dominantLayer = i;
                }
            }

            return dominantLayer;
        }

        /// <summary>
        /// Finds the index of the closest tree within the specified search radius.
        /// Returns -1 if no tree is found within the radius.
        /// </summary>
        private int FindClosestTreeIndexLinear(Vector2 targetPosition, float radius = TreeSearchRadius)
        {
            int closestTreeIndex = -1;
            float closestDistance = radius * radius; // Start with radius as the max valid distance

            for (int i = 0; i < _worldTreePositions.Length; i++)
            {
                float distance = (_worldTreePositions[i] - targetPosition).sqrMagnitude;

                if (distance < closestDistance) // If this tree is closer than the closest one found so far
                {
                    closestDistance = distance;
                    closestTreeIndex = i;
                }
            }

            return closestTreeIndex;
        }

        /// <summary>
        /// Finds the index of the closest tree within the specified search radius.
        /// Returns -1 if no tree is found within the radius.
        /// </summary>
        private int FindClosestTreeIndexBinary(Vector2 targetPosition, float radius = TreeSearchRadius)
        {
            int left = 0;
            int right = _worldTreePositions.Length - 1;
            radius *= radius;

            // Step 1: Binary search to narrow the range of potential trees
            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                float distance = (_worldTreePositions[mid] - targetPosition).sqrMagnitude;

                // If we find a tree within the radius, narrow down further using linear search
                if (distance < radius)
                    return mid;

                // Adjust the search bounds based on the target position
                if (_worldTreePositions[mid].x < targetPosition.x)
                    left = mid + 1;
                else
                    right = mid - 1;
            }

            return -1; // No tree within the radius
        }

        /// <summary>
        /// Calculates the world-space positions of all tree instances on the terrain.
        /// </summary>
        private Vector2[] CalculateWorldTreePositions()
        {
            TreeInstance[] trees = _terrainData.treeInstances;
            Vector2[] positions = new Vector2[trees.Length];

            Vector2 terrainOrigin = new Vector2(_terrainWorldOrigin.x, _terrainWorldOrigin.z);
            Vector2 terrainSize = new Vector2(_terrainSize.x, _terrainSize.z);

            for (int i = 0; i < trees.Length; i++)
            {
                Vector3 normalizedPosition = trees[i].position;
                positions[i] = Vector2.Scale(new Vector2(normalizedPosition.x, normalizedPosition.z), terrainSize) + terrainOrigin;
            }

            return positions;
        }

        private void QueueColliderRefresh()
        {
            if (_refreshColliderCoroutine == null)
            {
                _refreshColliderCoroutine = CoroutineUtility.InvokeNextFrame(this, RefreshCollider);
            }
        }

        private void RefreshCollider()
        {
            // REVISIT: Unfortunately, this does not work anymore.
            // float[,] heights = _collider.terrainData.GetHeights(0,0,0,0);
            // _collider.terrainData.SetHeights(0, 0, heights);

            _collider.enabled = false;
            _collider.enabled = true;
            _refreshColliderCoroutine = null;
        }

        /// <summary>
        /// Initializes terrain data and caches tree positions.
        /// </summary>
        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;

            var terrain = GetComponent<Terrain>();
            _collider = GetComponent<TerrainCollider>();
            _terrainWorldOrigin = terrain.GetPosition();
            _terrainData = terrain.terrainData;
            _alphamapSize = new Vector2(_terrainData.alphamapWidth, _terrainData.alphamapHeight);
            _terrainSize = _terrainData.size;
            _terrainlayers = _terrainData.terrainLayers;
            _treePrototypes = _terrainData.treePrototypes;

#if UNITY_EDITOR
            _originalTreeInstances = _terrainData.treeInstances;
#endif

            _worldTreePositions = CalculateWorldTreePositions();
            Array.Sort(_worldTreePositions, (a, b) => a.x.CompareTo(b.x));

            _currentTreeInstances = _terrainData.treeInstances;
            Array.Sort(_currentTreeInstances, (a, b) => a.position.x.CompareTo(b.position.x));
        }

        #region Editor
#if UNITY_EDITOR
        private TreeInstance[] _originalTreeInstances;

        private void OnDisable()
        {
            // Restores the original tree instances when the object is disabled.
            if (_originalTreeInstances != null)
            {
                _terrainData.treeInstances = _originalTreeInstances;
                _originalTreeInstances = null;
            }
        }
#endif
        #endregion

        #region Internal Types
        /// <summary>
        /// A unified cache structure to store the last queried world position and its corresponding results.
        /// </summary>
        private readonly struct QueryCache
        {
            public readonly Vector3 Position;
            public readonly int LayerIndex;
            public readonly int TreeIndex;

            public QueryCache(Vector3 position, int layerIndex, int treeIndex)
            {
                Position = position;
                LayerIndex = layerIndex;
                TreeIndex = treeIndex;
            }

            public readonly bool IsValid(Vector3 position) => position.ApproximatelyEquals(Position);
        }
        #endregion
    }
}