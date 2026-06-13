using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Player;
using BackroomsSurvival.Gameplay.World;
using UnityEngine;
using UnityEngine.UI;

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

        // Backrooms atmosphere: global render settings (set in its Awake) plus
        // per-chunk fluorescent lights placed as chunks stream in.
        private BackroomsLighting _lighting;
        private readonly HashSet<Transform> _litChunks = new HashSet<Transform>();

        private static GameObject SpawnPlayer()
        {
            // Root
            var root = new GameObject("Player");
            root.tag = "Player";
            var cc = root.AddComponent<CharacterController>();
            cc.radius = 0.4f;
            cc.height = 1.8f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            root.transform.position = new Vector3(25f, 2f, 25f); // chunk centre

            // Camera child
            var camGo = new GameObject("PlayerCamera");
            camGo.transform.SetParent(root.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.7f, 0f);
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 80f;
            cam.nearClipPlane = 0.1f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.tag = "MainCamera";
            camGo.AddComponent<AudioListener>();

            // God mode label (Canvas → Text)
            var canvasGo = new GameObject("HUD");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();
            canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            canvasGo.transform.SetParent(root.transform);

            var labelGo = new GameObject("GodModeLabel");
            labelGo.transform.SetParent(canvasGo.transform, false);
            var label = labelGo.AddComponent<Text>();
            label.text      = "[GOD MODE]";
            label.color     = Color.red;
            label.fontSize  = 24;
            label.alignment = TextAnchor.UpperCenter;
            var rt = labelGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.9f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            label.enabled   = false;

            // PlayerController — qualified: the legacy BackroomsSurvival.Gameplay
            // .PlayerController shadows the using directive from the enclosing
            // namespace, so the bare name binds to the wrong type.
            var pc = root.AddComponent<Player.PlayerController>();
            pc.playerCamera = cam;
            pc.godModeLabel = label;

            Debug.Log("[GridTestWorld] Auto-spawned Player at (25,2,25)");
            return root;
        }

        private void Start()
        {
            // Adding the component runs its Awake immediately: skybox off, flat
            // yellow ambient, sun disabled, dense fog. Held to light new chunks.
            _lighting = gameObject.AddComponent<BackroomsLighting>();

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

            // Hook up the player — auto-spawn if not present in scene.
            var playerGo = GameObject.FindWithTag("Player");
            if (playerGo == null)
                playerGo = SpawnPlayer();
            _streamer.playerTransform = playerGo.transform;
        }

        // Light chunks as they appear. The ChunkStreamer parents every chunk root
        // under its own transform; a chunk that streams out is Destroyed (with its
        // child lights), so we just prune null entries and skip already-lit roots.
        private void Update()
        {
            if (_lighting == null || _streamer == null) return;

            _litChunks.RemoveWhere(t => t == null);

            int tiles = GridConstants.ChunkCells / 2; // 20 cells → 10 tiles per side
            var container = _streamer.transform;
            for (int i = 0; i < container.childCount; i++)
            {
                var chunk = container.GetChild(i);
                if (!_litChunks.Add(chunk)) continue; // already lit
                _lighting.PlaceFluorescentLights(
                    chunk, tiles, tiles,
                    GridVisualConstants.TileSize,
                    GridVisualConstants.CellHeight * 2f); // 2 × 2.0 m = 4 m ceiling
            }
        }
    }
}
