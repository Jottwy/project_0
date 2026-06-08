using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    public sealed class HUDUpdater : MonoBehaviour
    {
        private Canvas _canvas;

        // Connection
        private Text _connectionText;

        // Info line
        private Text _infoText;

        // Stat bars
        private Image _healthFill, _hungerFill, _thirstFill, _sanityFill;
        private Text _healthLabel, _hungerLabel, _thirstLabel, _sanityLabel;

        // Crosshair
        private Image _crosshair;

        // Event feed
        private readonly List<Text> _feedTexts = new List<Text>();
        private readonly List<string> _feedEntries = new List<string>();
        private const int MaxFeed = 6;

        private static readonly Color HealthColor = new Color(0.85f, 0.20f, 0.20f);
        private static readonly Color HungerColor = new Color(0.90f, 0.55f, 0.15f);
        private static readonly Color ThirstColor = new Color(0.20f, 0.55f, 0.90f);
        private static readonly Color SanityColor = new Color(0.65f, 0.30f, 0.85f);
        private static readonly Color BgBar = new Color(0.15f, 0.15f, 0.15f, 0.7f);

        private void Start()
        {
            _canvas = new GameObject("HUDCanvas").AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 80;
            DontDestroyOnLoad(_canvas.gameObject);

            var scaler = _canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            BuildConnectionStatus();
            BuildInfoLine();
            BuildStatBars();
            BuildCrosshair();
            BuildEventFeed();
        }

        private void OnEnable()
        {
            IPCClient.Instance.AddEventListener(OnGameEvent);
        }

        private void OnDisable()
        {
            if (IPCClient.Instance != null)
                IPCClient.Instance.RemoveEventListener(OnGameEvent);
        }

        private void OnGameEvent(GameEventMsg ev)
        {
            _feedEntries.Add(ev.eventType);
            if (_feedEntries.Count > MaxFeed) _feedEntries.RemoveAt(0);
        }

        private void Update()
        {

            var ipc = IPCClient.Instance;

            // Connection.
            _connectionText.text = ipc.IsConnected
                ? $"<color=#66FF66>●</color>  Connected  {ipc.serverAddress}:{ipc.port}"
                : $"<color=#FFCC55>○</color>  Connecting to {ipc.serverAddress}:{ipc.port}...";

            var state = ipc.LatestState;
            if (state != null && state.localPlayer != null)
            {
                var lp = state.localPlayer;

                _infoText.text =
                    $"tick {state.tick}   " +
                    $"pos ({lp.position.x:0.0}, {lp.position.y:0.0}, {lp.position.z:0.0})   " +
                    $"chunks {state.visibleChunks.Count}  entities {state.visibleEntities.Count}  items {state.visibleItems.Count}";

                UpdateBar(_healthFill, _healthLabel, "Health", lp.stats.health);
                UpdateBar(_hungerFill, _hungerLabel, "Hunger", lp.stats.hunger);
                UpdateBar(_thirstFill, _thirstLabel, "Thirst", lp.stats.thirst);
                UpdateBar(_sanityFill, _sanityLabel, "Sanity", lp.stats.sanity);
            }
            else
            {
                _infoText.text = "Waiting for world state...";
            }

            // Event feed.
            for (int i = 0; i < _feedTexts.Count; i++)
            {
                if (i < _feedEntries.Count)
                {
                    _feedTexts[i].text = $"• {_feedEntries[_feedEntries.Count - 1 - i]}";
                    _feedTexts[i].gameObject.SetActive(true);
                }
                else
                {
                    _feedTexts[i].gameObject.SetActive(false);
                }
            }
        }

        // ─── Builders ───

        private void BuildConnectionStatus()
        {
            _connectionText = CreateText("Connection",
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(14f, -12f), new Vector2(500f, 22f), 13, TextAnchor.UpperLeft);
        }

        private void BuildInfoLine()
        {
            _infoText = CreateText("Info",
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(14f, -34f), new Vector2(700f, 22f), 12, TextAnchor.UpperLeft);
            _infoText.color = new Color(0.8f, 0.8f, 0.8f);
        }

        private void BuildStatBars()
        {
            float startY = -62f;
            float barW = 220f;
            float barH = 18f;
            float gap = 24f;

            (_healthFill, _healthLabel) = CreateBar("Health", startY, barW, barH, HealthColor);
            (_hungerFill, _hungerLabel) = CreateBar("Hunger", startY - gap, barW, barH, HungerColor);
            (_thirstFill, _thirstLabel) = CreateBar("Thirst", startY - gap * 2, barW, barH, ThirstColor);
            (_sanityFill, _sanityLabel) = CreateBar("Sanity", startY - gap * 3, barW, barH, SanityColor);
        }

        private (Image fill, Text label) CreateBar(string name, float y, float w, float h, Color color)
        {
            // Background
            var bgGo = new GameObject($"Bar_{name}_Bg");
            bgGo.transform.SetParent(_canvas.transform, false);
            var bgRt = bgGo.AddComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(0f, 1f);
            bgRt.anchorMax = new Vector2(0f, 1f);
            bgRt.pivot = new Vector2(0f, 1f);
            bgRt.anchoredPosition = new Vector2(14f, y);
            bgRt.sizeDelta = new Vector2(w, h);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.color = BgBar;
            bgImg.raycastTarget = false;

            // Fill
            var fillGo = new GameObject($"Bar_{name}_Fill");
            fillGo.transform.SetParent(bgGo.transform, false);
            var fillRt = fillGo.AddComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = new Vector2(1f, 1f);
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            fillRt.pivot = new Vector2(0f, 0.5f);
            var fillImg = fillGo.AddComponent<Image>();
            fillImg.color = color;
            fillImg.raycastTarget = false;

            // Label
            var label = CreateText($"Bar_{name}_Label",
                Vector2.zero, Vector2.one, new Vector2(0f, 0.5f),
                new Vector2(6f, 0f), Vector2.zero, 12, TextAnchor.MiddleLeft);
            label.transform.SetParent(bgGo.transform, false);
            var lrt = label.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(6f, 0f);
            lrt.offsetMax = Vector2.zero;

            return (fillImg, label);
        }

        private void BuildCrosshair()
        {
            var go = new GameObject("Crosshair");
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(4f, 4f);
            _crosshair = go.AddComponent<Image>();
            _crosshair.color = new Color(1f, 1f, 1f, 0.7f);
            _crosshair.raycastTarget = false;
        }

        private void BuildEventFeed()
        {
            for (int i = 0; i < MaxFeed; i++)
            {
                var txt = CreateText($"Feed_{i}",
                    new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                    new Vector2(14f, 16f + i * 18f), new Vector2(500f, 18f), 12, TextAnchor.LowerLeft);
                txt.color = new Color(0.9f, 0.9f, 0.9f, 0.8f);
                txt.gameObject.SetActive(false);
                _feedTexts.Add(txt);
            }
        }

        // ─── Helpers ───

        private static void UpdateBar(Image fill, Text label, string name, float value)
        {
            float pct = Mathf.Clamp01(value / 100f);
            fill.rectTransform.anchorMax = new Vector2(pct, 1f);
            label.text = $"{name}  {value:0}";

            // Flash low stats.
            if (value < 20f)
            {
                float flash = Mathf.Sin(Time.time * 4f) * 0.5f + 0.5f;
                var c = fill.color;
                c.a = Mathf.Lerp(0.5f, 1f, flash);
                fill.color = c;
            }
        }

        private Text CreateText(string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
            Vector2 pos, Vector2 size, int fontSize, TextAnchor alignment)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var txt = go.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = fontSize;
            txt.color = Color.white;
            txt.alignment = alignment;
            txt.raycastTarget = false;
            txt.supportRichText = true;

            // Shadow for readability.
            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            shadow.effectDistance = new Vector2(1f, -1f);

            return txt;
        }

        private void OnDestroy()
        {
            if (_canvas != null) Destroy(_canvas.gameObject);
        }
    }
}
