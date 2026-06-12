using System.Text;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Temporary Phase 3.2C debug HUD for POI visual verification.
    /// Reads only the latest client-side world snapshot; does not query or alter backend state.
    /// </summary>
    public sealed class PoiDebugHud : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public bool enablePoiDebugHud = true;
#else
        public bool enablePoiDebugHud = false;
#endif

       

        private const float FallbackChunkSize = 50f;
        private const long Seed7778 = 7778L;

        private static readonly Vector3 Seed7778LandmarkCenter = new Vector3(-150f, 1.8f, -100f);
        private static readonly Vector3 Seed7778AnomalyCenter = new Vector3(175f, 1.8f, -125f);
        private static readonly Vector3 Seed7778DangerCenter = new Vector3(400f, 1.8f, 450f);

        private Canvas _canvas;
        private Text _text;
        private ChunkRenderer _chunkRenderer;
        private readonly StringBuilder _sb = new StringBuilder(512);

        private void Start()
        {
            _chunkRenderer = FindFirstObjectByType<ChunkRenderer>();

            _canvas = new GameObject("PoiDebugCanvas").AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 90;
            DontDestroyOnLoad(_canvas.gameObject);

            var scaler = _canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            _text = CreateText();
            _canvas.gameObject.SetActive(enablePoiDebugHud);
        }

        private void Update()
        {
            
            if (!enablePoiDebugHud || _text == null)
                return;

            if (!IPCClient.TryGetInstance(out var ipc) || ipc.LatestState == null || ipc.LatestState.localPlayer == null)
            {
                _text.text = "POI DEBUG\nWaiting for world state...";
                return;
            }

            var state = ipc.LatestState;
            Vector3 pos = state.localPlayer.position;
            float chunkSize = ResolveChunkSize();
            int chunkX = Mathf.FloorToInt(pos.x / chunkSize);
            int chunkZ = Mathf.FloorToInt(pos.z / chunkSize);

            ChunkViewMsg current = FindCurrentChunk(state, chunkX, chunkZ);
            int layer = current != null ? current.layer : 0;

            _sb.Length = 0;
            _sb.Append("POI DEBUG\n");
            _sb.Append("pos (")
                .Append(pos.x.ToString("0.0")).Append(", ")
                .Append(pos.y.ToString("0.0")).Append(", ")
                .Append(pos.z.ToString("0.0")).Append(")\n");
            _sb.Append("chunk (").Append(chunkX).Append(", ").Append(chunkZ).Append(")  layer ").Append(layer).Append('\n');

            if (current != null)
            {
                _sb.Append("template_id ").Append(current.templateId)
                    .Append("  zone_kind ").Append(current.zoneKind)
                    .Append("  macro_id ").Append(current.macroId).Append('\n');
                _sb.Append("POI: ").Append(PoiLabel(current.templateId));
            }
            else
            {
                _sb.Append("template_id <not loaded>  zone_kind <not loaded>  macro_id <not loaded>\n");
                _sb.Append("POI: none");
            }

            if (state.worldSeed == Seed7778)
            {
                _sb.Append('\n');
                AppendSeed7778Distances(pos);
            }

            _text.text = _sb.ToString();
        }

        private float ResolveChunkSize()
        {
            if (_chunkRenderer == null)
                _chunkRenderer = FindFirstObjectByType<ChunkRenderer>();

            return _chunkRenderer != null && _chunkRenderer.chunkSize > 0f
                ? _chunkRenderer.chunkSize
                : FallbackChunkSize;
        }

        private static ChunkViewMsg FindCurrentChunk(WorldStateMsg state, int chunkX, int chunkZ)
        {
            ChunkViewMsg fallback = null;
            for (int i = 0; i < state.visibleChunks.Count; i++)
            {
                var cv = state.visibleChunks[i];
                if (cv.pos[0] != chunkX || cv.pos[1] != chunkZ)
                    continue;

                if (cv.layer == 0)
                    return cv;

                if (fallback == null)
                    fallback = cv;
            }

            return fallback;
        }

        private static string PoiLabel(int templateId)
        {
            switch (templateId)
            {
                case 18: return "POI LANDMARK";
                case 19: return "POI ANOMALY";
                case 20: return "POI DANGER POCKET";
                case 21: return "POI SAFE POCKET";
                default: return "none";
            }
        }

        private void AppendSeed7778Distances(Vector3 pos)
        {
            _sb.Append("seed 7778 dist landmark ").Append(Vector3.Distance(pos, Seed7778LandmarkCenter).ToString("0.0")).Append("m\n");
            _sb.Append("seed 7778 dist anomaly  ").Append(Vector3.Distance(pos, Seed7778AnomalyCenter).ToString("0.0")).Append("m\n");
            _sb.Append("seed 7778 dist danger   ").Append(Vector3.Distance(pos, Seed7778DangerCenter).ToString("0.0")).Append('m');
        }

        private Text CreateText()
        {
            var go = new GameObject("PoiDebugText");
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-16f, -16f);
            rt.sizeDelta = new Vector2(430f, 150f);

            var txt = go.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = 13;
            txt.color = new Color(0.82f, 0.95f, 1f, 0.95f);
            txt.alignment = TextAnchor.UpperRight;
            txt.raycastTarget = false;
            txt.supportRichText = false;

            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.effectDistance = new Vector2(1f, -1f);

            return txt;
        }

        private void OnDestroy()
        {
            if (_canvas != null)
                Destroy(_canvas.gameObject);
        }
    }
}
