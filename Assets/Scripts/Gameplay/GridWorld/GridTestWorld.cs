using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Test harness for the procedural world (replaces .bytes loading from Fase 3).
    /// Instantiates a ChunkStreamer driven by the Player in the scene.
    /// Replaced by the real IPC path in Fase 4.
    /// </summary>
    public sealed class GridTestWorld : MonoBehaviour
    {
        [Header("World")]
        public long seed = 42;
        public int layerCount = 4;
        [Tooltip("-1 = all layers; 0..3 = only that layer")]
        public int onlyLayer = -1;

        [Header("Streaming")]
        [Tooltip("Chunks visible in each direction (1 = 3×3 ring)")]
        public int viewRadius = 1;

        [Header("Layer Configs (optional — leave null for built-in defaults)")]
        public LayerConfig layerConfig0;
        public LayerConfig layerConfig1;
        public LayerConfig layerConfig2;
        public LayerConfig layerConfig3;

        private ChunkStreamer _streamer;

        private void Start()
        {
            var streamerGo = new GameObject("ChunkStreamer");
            streamerGo.transform.SetParent(transform, false);

            _streamer = streamerGo.AddComponent<ChunkStreamer>();
            _streamer.seed       = seed;
            _streamer.layerCount = onlyLayer >= 0 ? 1 : layerCount;
            _streamer.viewRadius = viewRadius;
            _streamer.layerConfigs = new LayerConfig[]
            {
                layerConfig0, layerConfig1, layerConfig2, layerConfig3
            };

            // Hook up the player (find by tag first, fallback to Camera.main)
            var playerGo = GameObject.FindWithTag("Player");
            if (playerGo != null)
                _streamer.playerTransform = playerGo.transform;
            else if (Camera.main != null)
                _streamer.playerTransform = Camera.main.transform;
            else
                Debug.LogWarning("[GridTestWorld] No Player or Camera found — streaming disabled.");
        }
    }
}
