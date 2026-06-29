using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Fase 5D — standalone "Visual Effects" overlay for the PPv2 post-process (<see
    /// cref="BackroomsPostProcess"/>). Self-contained: a centre-right dark panel with its own
    /// ScrollRect, built from code (no vendor menu dependency). F4 toggles it, ESC closes it.
    /// Four sections (Vignette / Grain / Bloom / Color Grading), each a toggle + label + slider
    /// + numeric %. Values drive BackroomsPostProcess, which persists them to PlayerPrefs (bp_*).
    /// </summary>
    public sealed class BackroomsGraphicsSettings : MonoBehaviour
    {
        // Lazy-init, NOT a static field initializer: Resources.GetBuiltinResource can run in a
        // context where it's not allowed (same class of bug as MaterialPropertyBlock created
        // from a constructor). Resolved on first use instead.
        private static Font _uiFont;
        private static Font UiFont => _uiFont ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private GameObject _panel;
        private bool _visible;

        private void Start()
        {
            EnsureEventSystem();
            BuildOverlay();
            SetVisible(false);
        }

        private void Update()
        {
            // New Input System: UnityEngine.Input.GetKeyDown throws when old input is disabled.
            var kb = Keyboard.current;
            if (kb == null)
                return;
            if (kb.f4Key.wasPressedThisFrame)
                SetVisible(!_visible);
            else if (_visible && kb.escapeKey.wasPressedThisFrame)
                SetVisible(false);
        }

        private void SetVisible(bool v)
        {
            _visible = v;
            if (_panel != null) _panel.SetActive(v);
        }

        private static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        // ── Overlay construction ────────────────────────────────────────────────

        private void BuildOverlay()
        {
            // Canvas (overlay, above the HUD).
            var canvasGo = new GameObject("BackroomsVfxCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // Panel: 400×500, centre-right (does not cover the left HUD).
            _panel = NewRect("Panel", canvasGo.transform);
            var prt = (RectTransform)_panel.transform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(1f, 0.5f);
            prt.sizeDelta = new Vector2(400f, 500f);
            prt.anchoredPosition = new Vector2(-40f, 0f);
            _panel.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.85f);

            // Title.
            var title = Label(_panel.transform, "VISUAL EFFECTS", Vector2.zero, 22, FontStyle.Bold, 400f, TextAnchor.UpperCenter);
            title.color = Color.white;
            var trt = (RectTransform)title.transform;
            trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f); trt.pivot = new Vector2(0.5f, 1f);
            trt.sizeDelta = new Vector2(0f, 36f); trt.anchoredPosition = new Vector2(0f, -10f);

            // ScrollRect filling the panel below the title.
            var content = BuildScrollArea(_panel.transform);

            // Sections.
            var pp = BackroomsPostProcess.Instance;
            BuildSection(content, "Vignette",
                pp != null && pp.VignetteEnabled, pp != null ? pp.VignetteIntensity : 0.38f,
                on => BackroomsPostProcess.Instance?.SetVignetteEnabled(on),
                v  => BackroomsPostProcess.Instance?.SetVignetteIntensity(v));
            BuildSection(content, "Grain",
                pp != null && pp.GrainEnabled, pp != null ? pp.GrainIntensity : 0.18f,
                on => BackroomsPostProcess.Instance?.SetGrainEnabled(on),
                v  => BackroomsPostProcess.Instance?.SetGrainIntensity(v));
            BuildSection(content, "Bloom",
                pp != null && pp.BloomEnabled, pp != null ? pp.BloomIntensity : 0.5f,
                on => BackroomsPostProcess.Instance?.SetBloomEnabled(on),
                v  => BackroomsPostProcess.Instance?.SetBloomIntensity(v));
            BuildSection(content, "Color Grading",
                pp != null && pp.ColorGradingEnabled, pp != null ? pp.ColorGradingIntensity : 1f,
                on => BackroomsPostProcess.Instance?.SetColorGradingEnabled(on),
                v  => BackroomsPostProcess.Instance?.SetColorGradingIntensity(v));
        }

        /// <summary>Build an own ScrollRect (viewport+mask, vertical-layout content + size
        /// fitter, dark scrollbar) below the title and return the content transform.</summary>
        private Transform BuildScrollArea(Transform panel)
        {
            var scrollGo = NewRect("Scroll", panel);
            var scrt = (RectTransform)scrollGo.transform;
            scrt.anchorMin = Vector2.zero; scrt.anchorMax = Vector2.one;
            scrt.offsetMin = new Vector2(8f, 8f);
            scrt.offsetMax = new Vector2(-8f, -50f); // leave the top 50 px for the title
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            var viewport = NewRect("Viewport", scrollGo.transform);
            var vprt = (RectTransform)viewport.transform;
            vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one; vprt.pivot = new Vector2(0f, 1f);
            vprt.offsetMin = new Vector2(0f, 0f);
            vprt.offsetMax = new Vector2(-12f, 0f); // room for the scrollbar
            viewport.AddComponent<RectMask2D>();
            scroll.viewport = vprt;

            var contentGo = NewRect("Content", viewport.transform);
            var crt = (RectTransform)contentGo.transform;
            crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f); crt.pivot = new Vector2(0.5f, 1f);
            crt.sizeDelta = Vector2.zero; crt.anchoredPosition = Vector2.zero;
            var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;  vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.spacing = 6f; vlg.padding = new RectOffset(4, 4, 4, 4);
            contentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = crt;

            var sbGo = DefaultControls.CreateScrollbar(new DefaultControls.Resources());
            sbGo.name = "Scrollbar";
            sbGo.transform.SetParent(scrollGo.transform, false);
            var sb = sbGo.GetComponent<Scrollbar>();
            sb.SetDirection(Scrollbar.Direction.BottomToTop, true);
            var sbrt = (RectTransform)sbGo.transform;
            sbrt.anchorMin = new Vector2(1f, 0f); sbrt.anchorMax = new Vector2(1f, 1f); sbrt.pivot = new Vector2(1f, 1f);
            sbrt.sizeDelta = new Vector2(10f, 0f); sbrt.anchoredPosition = Vector2.zero;
            var sbTrack = sbGo.GetComponent<Image>();
            if (sbTrack != null) sbTrack.color = new Color(0f, 0f, 0f, 0.30f);
            if (sb.handleRect != null)
            {
                var hImg = sb.handleRect.GetComponent<Image>();
                if (hImg != null) hImg.color = new Color(0.70f, 0.68f, 0.60f, 0.6f);
            }
            scroll.verticalScrollbar = sb;

            return contentGo.transform;
        }

        /// <summary>One effect section: [toggle] [name] ............ [value%] / [slider].</summary>
        private void BuildSection(Transform content, string name, bool on, float val,
            UnityAction<bool> onToggle, UnityAction<float> onSlide)
        {
            var row = NewRect(name + "_section", content);
            var rle = row.AddComponent<LayoutElement>();
            rle.preferredHeight = 64f; rle.minHeight = 64f; rle.flexibleHeight = 0f;

            var res = new DefaultControls.Resources();

            // Toggle (top-left).
            var tg = DefaultControls.CreateToggle(res);
            tg.transform.SetParent(row.transform, false);
            var trt = (RectTransform)tg.transform;
            trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0f, 1f);
            trt.anchoredPosition = new Vector2(8f, -6f);
            trt.sizeDelta = new Vector2(22f, 22f);
            var tgLabel = tg.transform.Find("Label");
            if (tgLabel != null) tgLabel.gameObject.SetActive(false);
            var toggle = tg.GetComponent<Toggle>();
            toggle.isOn = on;

            // Name (next to the toggle).
            Label(row.transform, name, new Vector2(38f, -8f), 16, FontStyle.Bold, 240f, TextAnchor.MiddleLeft);

            // Value % (top-right).
            var valueGo = NewRect("Value", row.transform);
            var valRt = (RectTransform)valueGo.transform;
            valRt.anchorMin = valRt.anchorMax = valRt.pivot = new Vector2(1f, 1f);
            valRt.anchoredPosition = new Vector2(-10f, -8f);
            valRt.sizeDelta = new Vector2(70f, 22f);
            var valueText = valueGo.AddComponent<Text>();
            valueText.font = UiFont; valueText.fontSize = 15;
            valueText.color = new Color(0.85f, 0.85f, 0.78f);
            valueText.alignment = TextAnchor.MiddleRight;
            valueText.text = Pct(val);

            // Slider (bottom, full width).
            var sl = DefaultControls.CreateSlider(res);
            sl.transform.SetParent(row.transform, false);
            var srt = (RectTransform)sl.transform;
            srt.anchorMin = new Vector2(0f, 1f); srt.anchorMax = new Vector2(1f, 1f); srt.pivot = new Vector2(0.5f, 1f);
            srt.sizeDelta = new Vector2(-20f, 16f);
            srt.anchoredPosition = new Vector2(0f, -38f);
            var slider = sl.GetComponent<Slider>();
            slider.minValue = 0f; slider.maxValue = 1f; slider.value = Mathf.Clamp01(val);

            // Wire: toggle → PP; slider → PP + live value label.
            toggle.onValueChanged.AddListener(onToggle);
            slider.onValueChanged.AddListener(v => { onSlide(v); valueText.text = Pct(v); });
        }

        private static string Pct(float v) => Mathf.RoundToInt(Mathf.Clamp01(v) * 100f) + "%";

        private static Text Label(Transform parent, string text, Vector2 pos, int size,
            FontStyle style, float width, TextAnchor align)
        {
            var go = NewRect("Label", parent);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, 24f);
            var t = go.AddComponent<Text>();
            t.text = text;
            t.font = UiFont;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = new Color(0.92f, 0.90f, 0.84f);
            t.alignment = align;
            return t;
        }

        private static GameObject NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }
    }
}
