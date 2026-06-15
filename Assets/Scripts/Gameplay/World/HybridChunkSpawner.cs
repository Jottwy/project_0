using BackroomsSurvival.Gameplay.GridWorld;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.World
{
    /// <summary>
    /// Hybrid chunk spawner — a thin, additive wrapper over the existing procedural
    /// pipeline (WorldGenerator + GridChunkBuilder, both núcleo and left untouched).
    /// Each <see cref="SpawnChunk"/> picks, by chance, a handcrafted prefab (30%) or
    /// a freshly generated procedural chunk column (70%).
    ///
    /// This component does NOT replace ChunkStreamer; attach it to a GameObject and
    /// call SpawnChunk from your own placement code ("desde arriba"). Deterministic:
    /// the same seed + position always yields the same procedural chunk.
    /// </summary>
    public sealed class HybridChunkSpawner : MonoBehaviour
    {
        [Header("Handcrafted (≈30%)")]
        [Tooltip("Drag your handcrafted chunk prefabs here. Empty slots are skipped.")]
        [SerializeField] private GameObject[] handcraftedChunkPrefabs = new GameObject[0];
        [Range(0f, 1f)]
        [SerializeField] private float handcraftedChance = 0.3f;

        [Header("Procedural (≈70%)")]
        [Tooltip("Number of stacked layers per procedural chunk column.")]
        [SerializeField] private int layerCount = 4;
        [Tooltip("Optional per-layer configs (0..n). Leave empty for built-in defaults.")]
        [SerializeField] private LayerConfig[] layerConfigs = new LayerConfig[0];

        private GridPrefabSet _prefabs;
        private LayerConfig _defaultConfig;

        private const float ChunkSide = GridConstants.ChunkCells * GridConstants.CellSize; // 50 m

        /// <summary>
        /// Spawn one chunk at <paramref name="position"/>. With probability
        /// <c>handcraftedChance</c> instantiates a random handcrafted prefab,
        /// otherwise generates a procedural chunk seeded by <paramref name="seed"/>.
        /// Returns the spawned root (may be null if a handcrafted spawn finds no
        /// usable prefab).
        /// </summary>
        public GameObject SpawnChunk(Vector3 position, int seed)
        {
            bool hasHandcrafted = handcraftedChunkPrefabs != null && handcraftedChunkPrefabs.Length > 0;
            if (hasHandcrafted && Random.value < handcraftedChance)
                return SpawnHandcrafted(position);

            return GenerateProcedural(position, seed);
        }

        // ── 30%: handcrafted prefab ───────────────────────────────────────────
        private GameObject SpawnHandcrafted(Vector3 position)
        {
            var prefab = PickHandcrafted();
            if (prefab == null) return null;
            var go = Instantiate(prefab, position, Quaternion.identity);
            go.name = $"HandcraftedChunk_{prefab.name}";
            return go;
        }

        // Random start, then scan for the first non-null slot (tolerates gaps the
        // designer left in the array).
        private GameObject PickHandcrafted()
        {
            int n = handcraftedChunkPrefabs.Length;
            int start = Random.Range(0, n);
            for (int i = 0; i < n; i++)
            {
                var p = handcraftedChunkPrefabs[(start + i) % n];
                if (p != null) return p;
            }
            return null;
        }

        // ── 70%: delegate to the existing procedural generator (additive) ─────
        private GameObject GenerateProcedural(Vector3 position, int seed)
        {
            if (_prefabs == null) _prefabs = GridPrefabSet.LoadFromResources();

            var root = new GameObject($"ProceduralChunk_{seed}");
            root.transform.position = position;

            if (_prefabs == null || _prefabs.floor == null)
            {
                Debug.LogWarning("[HybridChunkSpawner] Grid prefabs missing in Resources; " +
                                 "returning an empty procedural chunk.");
                return root;
            }

            // Chunk-cell coords derived from the placement, mirroring ChunkStreamer,
            // so content stays spatially coherent and deterministic.
            int cx = Mathf.FloorToInt(position.x / ChunkSide);
            int cz = Mathf.FloorToInt(position.z / ChunkSide);

            int layers = Mathf.Max(1, layerCount);
            for (int layer = 0; layer < layers; layer++)
            {
                var cfg    = GetConfig(layer);
                var data   = WorldGenerator.GenerateChunk(seed, cx, cz, layer, cfg);
                var origin = position + new Vector3(0f, layer * GridConstants.LayerHeight, 0f);
                var layerGo = GridChunkBuilder.Build(data, _prefabs, origin, $"Layer_{layer}", layer, layers);
                layerGo.transform.SetParent(root.transform, worldPositionStays: true);
            }
            return root;
        }

        private LayerConfig GetConfig(int layer)
        {
            if (layerConfigs != null && layer < layerConfigs.Length && layerConfigs[layer] != null)
                return layerConfigs[layer];

            // Neutral fallback (one cached instance) when no LayerConfig is authored.
            if (_defaultConfig == null) _defaultConfig = ScriptableObject.CreateInstance<LayerConfig>();
            return _defaultConfig;
        }
    }
}
