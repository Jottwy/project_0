using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    public sealed class MinimapRenderer : MonoBehaviour
    {
        [Header("Layout")]
        public int mapSize = 200;
        public int cellSize = 8;
        public int playerDotSize = 6;

        private Canvas _canvas;
        private RectTransform _mapPanel;
        private Image _bgImage;
        private Image _playerDot;
        private readonly Dictionary<long, Image> _chunkCells = new Dictionary<long, Image>();

        private static readonly Color ChunkRandom = new Color(0.55f, 0.50f, 0.40f, 0.8f);
        private static readonly Color ChunkStabilized = new Color(0.40f, 0.70f, 0.40f, 0.8f);
        private static readonly Color ChunkAnchored = new Color(0.40f, 0.60f, 0.90f, 0.8f);
        private static readonly Color PlayerColor = new Color(1f, 0.9f, 0.2f, 1f);
        private static readonly Color BgColor = new Color(0.08f, 0.08f, 0.10f, 0.75f);

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        private void Start()
        {
            _canvas = new GameObject("MinimapCanvas").AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 50;
            DontDestroyOnLoad(_canvas.gameObject);

            var scaler = _canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            // Background panel (bottom-right).
            var panelGo = new GameObject("MinimapPanel");
            panelGo.transform.SetParent(_canvas.transform, false);
            _mapPanel = panelGo.AddComponent<RectTransform>();
            _mapPanel.anchorMin = new Vector2(1f, 0f);
            _mapPanel.anchorMax = new Vector2(1f, 0f);
            _mapPanel.pivot = new Vector2(1f, 0f);
            _mapPanel.anchoredPosition = new Vector2(-16f, 16f);
            _mapPanel.sizeDelta = new Vector2(mapSize, mapSize);

            _bgImage = panelGo.AddComponent<Image>();
            _bgImage.color = BgColor;
            _bgImage.raycastTarget = false;

            // Player dot (always centered).
            var dotGo = new GameObject("PlayerDot");
            dotGo.transform.SetParent(_mapPanel, false);
            var dotRt = dotGo.AddComponent<RectTransform>();
            dotRt.anchorMin = new Vector2(0.5f, 0.5f);
            dotRt.anchorMax = new Vector2(0.5f, 0.5f);
            dotRt.sizeDelta = new Vector2(playerDotSize, playerDotSize);
            _playerDot = dotGo.AddComponent<Image>();
            _playerDot.color = PlayerColor;
            _playerDot.raycastTarget = false;

            // Border (thin lines around the map)
            CreateBorderEdge(_mapPanel, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)); // bottom
            CreateBorderEdge(_mapPanel, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 1)); // top
            CreateBorderEdge(_mapPanel, new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 0)); // left
            CreateBorderEdge(_mapPanel, new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, 0)); // right

            // Label
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(_mapPanel, false);
            var lrt = labelGo.AddComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0f, 1f);
            lrt.anchorMax = new Vector2(1f, 1f);
            lrt.pivot = new Vector2(0f, 0f);
            lrt.anchoredPosition = new Vector2(4f, 2f);
            lrt.sizeDelta = new Vector2(0, 16);
            var txt = labelGo.AddComponent<Text>();
            txt.text = "MAP";
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = 11;
            txt.color = new Color(0.7f, 0.7f, 0.7f, 0.9f);
            txt.alignment = TextAnchor.LowerLeft;
            txt.raycastTarget = false;
        }

        private void LateUpdate()
        {
            if (!IPCClient.TryGetInstance(out var ipc))
                return;

            var state = ipc.LatestState;
            if (state == null || state.localPlayer == null) return;

            var playerPos = state.localPlayer.position;
            int playerCx = Mathf.FloorToInt(playerPos.x / 50f);
            int playerCz = Mathf.FloorToInt(playerPos.z / 50f);

            var alive = new HashSet<long>();

            foreach (var cv in state.visibleChunks)
            {
                long key = Key(cv.pos[0], cv.pos[1]);
                alive.Add(key);

                if (!_chunkCells.TryGetValue(key, out var cell))
                {
                    cell = CreateCell();
                    _chunkCells[key] = cell;
                }

                // Position relative to player chunk.
                int dx = cv.pos[0] - playerCx;
                int dz = cv.pos[1] - playerCz;
                var rt = cell.rectTransform;
                rt.anchoredPosition = new Vector2(
                    mapSize * 0.5f + dx * cellSize - cellSize * 0.5f,
                    mapSize * 0.5f + dz * cellSize - cellSize * 0.5f);

                cell.color = cv.state switch
                {
                    "anchored" => ChunkAnchored,
                    "stabilized" => ChunkStabilized,
                    _ => ChunkRandom
                };
                cell.gameObject.SetActive(true);
            }

            // Hide stale cells.
            var stale = new List<long>();
            foreach (var kv in _chunkCells)
            {
                if (!alive.Contains(kv.Key))
                {
                    kv.Value.gameObject.SetActive(false);
                    stale.Add(kv.Key);
                }
            }
            foreach (long k in stale) _chunkCells.Remove(k);

            // Sub-chunk offset for the player dot.
            float subX = (playerPos.x / 50f - playerCx) - 0.5f;
            float subZ = (playerPos.z / 50f - playerCz) - 0.5f;
            _playerDot.rectTransform.anchoredPosition = new Vector2(
                subX * cellSize, subZ * cellSize);
        }

        private Image CreateCell()
        {
            var go = new GameObject("Cell");
            go.transform.SetParent(_mapPanel, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(cellSize - 1, cellSize - 1);
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            // Ensure cells render behind player dot.
            go.transform.SetAsFirstSibling();
            return img;
        }

        private static void CreateBorderEdge(Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta)
        {
            var go = new GameObject("Border");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.sizeDelta = sizeDelta;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.4f, 0.4f, 0.4f, 0.8f);
            img.raycastTarget = false;
        }

        private void OnDestroy()
        {
            if (_canvas != null) Destroy(_canvas.gameObject);
        }
    }
}
