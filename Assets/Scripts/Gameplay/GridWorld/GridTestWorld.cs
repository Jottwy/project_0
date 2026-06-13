using BackroomsSurvival.Gameplay.Player;
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
            cam.tag = "MainCamera";
            camGo.AddComponent<AudioListener>();

            // God mode label (Canvas → Text)
            var canvasGo = new GameObject("HUD");
            canvasGo.transform.SetParent(root.transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();
            canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            var labelGo = new GameObject("GodModeLabel");
            labelGo.transform.SetParent(canvasGo.transform, false);
            var rt = labelGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -10f);
            rt.sizeDelta = new Vector2(0f, 60f);
            var label = labelGo.AddComponent<Text>();
            label.text      = "[GOD MODE]";
            label.color     = Color.red;
            label.fontSize  = 28;
            label.fontStyle = FontStyle.Bold;
            label.alignment = TextAnchor.UpperCenter;
            label.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.enabled   = false;

            // PlayerController
            var pc = root.AddComponent<PlayerController>();
            pc.playerCamera = cam;
            pc.godModeLabel = label;

            Debug.Log("[GridTestWorld] Auto-spawned Player at (25,2,25)");
            return root;
        }

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

            // Hook up the player — auto-spawn if not present in scene.
            var playerGo = GameObject.FindWithTag("Player");
            if (playerGo == null)
                playerGo = SpawnPlayer();
            _streamer.playerTransform = playerGo.transform;
        }
    }
}
