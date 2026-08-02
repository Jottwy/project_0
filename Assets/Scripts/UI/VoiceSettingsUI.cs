using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// ADR-046 — panel de ajustes de voz. **F7** lo abre y lo cierra, ESC lo cierra.
    ///
    /// EXISTE PORQUE EL DIAGNÓSTICO ERA IMPOSIBLE. El micrófono es opt-in y sin pantalla de
    /// opciones no había forma de elegir dispositivo, ver si capta, ni distinguir "el micro está
    /// apagado" de "el componente no está en la escena": las dos cosas eran silencio. Lo que este
    /// panel aporta no es comodidad, es OBSERVABILIDAD — el medidor de nivel y el auto-test
    /// convierten cada avería en una pregunta con respuesta.
    ///
    /// Construido por código, mismo idioma que <c>BackroomsGraphicsSettings</c> (F4): sin prefab
    /// que bakear, sin dependencia de menús del vendor, y se puede borrar el archivo entero sin
    /// romper la voz — que sigue funcionando por teclas.
    /// </summary>
    public sealed class VoiceSettingsUI : MonoBehaviour
    {
        private static Font _uiFont;
        private static Font UiFont => _uiFont ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private GameObject _panel;
        private bool _visible;

        private VoiceCapture _voice;
        private Dropdown _deviceDropdown;
        private Text _statusText;
        private Text _levelText;
        private Image _levelFill;
        private Text _pttLabel;
        private Toggle _micToggle;
        private Toggle _modeToggle;
        private Toggle _selfTestToggle;
        private Slider _thresholdSlider;
        private Text _thresholdValue;

        /// <summary>Cuando es true, la próxima tecla pulsada se asigna como push-to-talk.</summary>
        private bool _rebinding;

        /// <summary>Nombres reales, tal cual los espera <c>VoiceCapture.Device</c>.</summary>
        private readonly List<string> _deviceOptions = new List<string>();

        /// <summary>Los mismos, decorados con su tasa. Se pintan estos y se ASIGNAN los de arriba:
        /// mandar la etiqueta como nombre de dispositivo no encontraría ninguno.</summary>
        private readonly List<string> _deviceLabels = new List<string>();

        private void Start()
        {
            EnsureEventSystem();
            BuildOverlay();
            SetVisible(false);
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            // El rebind se atiende ANTES del toggle del panel: si no, pulsar F7 para asignar F7
            // cerraría el panel en vez de asignar la tecla.
            if (_rebinding)
            {
                CaptureRebind(kb);
                return;
            }

            if (kb.f7Key.wasPressedThisFrame) SetVisible(!_visible);
            else if (_visible && kb.escapeKey.wasPressedThisFrame) SetVisible(false);

            if (_visible) RefreshLive();
        }

        private void CaptureRebind(Keyboard kb)
        {
            foreach (var control in kb.allKeys)
            {
                if (!control.wasPressedThisFrame) continue;
                _rebinding = false;
                if (control.keyCode == Key.Escape)
                {
                    UpdatePttLabel();
                    return;
                }
                Voice().PushToTalkKey = control.keyCode;
                UpdatePttLabel();
                return;
            }
        }

        private VoiceCapture Voice()
        {
            if (_voice == null) _voice = FindFirstObjectByType<VoiceCapture>();
            return _voice;
        }

        private void SetVisible(bool v)
        {
            _visible = v;
            if (_panel != null) _panel.SetActive(v);
            if (v) { RefreshDevices(); RefreshFromComponent(); }
        }

        // ── Estado en vivo ───────────────────────────────────────────────────────

        private void RefreshLive()
        {
            var vc = Voice();
            if (vc == null)
            {
                // Este caso NO es teórico: si `GameBootstrap` no está en la escena jugada, el
                // componente no existe y hasta ahora eso se manifestaba como silencio sin más.
                _statusText.text = "SIN COMPONENTE VoiceCapture EN LA ESCENA";
                _statusText.color = new Color(1f, 0.5f, 0.4f);
                return;
            }

            float level = vc.InputLevel;
            _levelFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(level * 4f), 1f);
            _levelFill.color = vc.IsTransmitting
                ? new Color(0.4f, 0.9f, 0.45f)
                : new Color(0.55f, 0.55f, 0.5f);
            _levelText.text = $"nivel {level:F3}";

            if (!vc.MicEnabled)
            {
                _statusText.text = "MICRÓFONO APAGADO";
                _statusText.color = new Color(0.75f, 0.75f, 0.7f);
            }
            else if (string.IsNullOrEmpty(vc.ActiveDevice))
            {
                _statusText.text = "ENCENDIDO PERO SIN CAPTURA — mira la consola";
                _statusText.color = new Color(1f, 0.6f, 0.35f);
            }
            else if (vc.IsTransmitting)
            {
                _statusText.text = "TRANSMITIENDO";
                _statusText.color = new Color(0.45f, 0.95f, 0.5f);
            }
            else
            {
                _statusText.text = "escuchando: " + vc.ActiveDevice;
                _statusText.color = new Color(0.85f, 0.85f, 0.78f);
            }
        }

        private void RefreshDevices()
        {
            if (_deviceDropdown == null) return;
            var vc = Voice();

            _deviceOptions.Clear();
            _deviceOptions.Add("(automático)");
            _deviceOptions.AddRange(VoiceCapture.Devices);

            // Se etiqueta con la tasa REAL a la que se abriría cada uno. Ya no hay dispositivos
            // "inservibles" —los que no dan 48 kHz se remuestrean— pero verlo explica por qué uno
            // suena distinto a otro sin tener que leerse el log.
            _deviceLabels.Clear();
            _deviceLabels.Add("(automático)");
            for (int i = 1; i < _deviceOptions.Count; i++)
            {
                int rate = VoiceCapture.PickCaptureRate(_deviceOptions[i]);
                _deviceLabels.Add(rate == VoiceCapture.SampleRate
                    ? $"{_deviceOptions[i]}  [{rate / 1000} kHz]"
                    : $"{_deviceOptions[i]}  [{rate / 1000} kHz → remuestreo]");
            }

            _deviceDropdown.ClearOptions();
            _deviceDropdown.AddOptions(_deviceLabels);

            int idx = 0;
            if (vc != null && !string.IsNullOrEmpty(vc.Device))
            {
                int found = _deviceOptions.IndexOf(vc.Device);
                // Un dispositivo guardado que YA NO EXISTE (cascos desenchufados) cae a
                // automático en vez de dejar el desplegable mintiendo sobre lo que se abrió.
                idx = found >= 0 ? found : 0;
            }
            _deviceDropdown.SetValueWithoutNotify(idx);
        }

        private void RefreshFromComponent()
        {
            var vc = Voice();
            if (vc == null) return;
            _micToggle.SetIsOnWithoutNotify(vc.MicEnabled);
            _modeToggle.SetIsOnWithoutNotify(vc.OpenMic);
            _selfTestToggle.SetIsOnWithoutNotify(vc.SelfTest);
            _thresholdSlider.SetValueWithoutNotify(vc.ActivationThreshold);
            _thresholdValue.text = vc.ActivationThreshold.ToString("F3");
            UpdatePttLabel();
        }

        private void UpdatePttLabel()
        {
            var vc = Voice();
            _pttLabel.text = _rebinding
                ? "pulsa una tecla…  (ESC cancela)"
                : "hablar: " + (vc != null ? vc.PushToTalkKey.ToString() : "?");
        }

        // ── Construcción ─────────────────────────────────────────────────────────

        private static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        private void BuildOverlay()
        {
            var canvasGo = new GameObject("BackroomsVoiceCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5001; // por encima del panel de gráficos, nunca debajo
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // Izquierda, para no solaparse con el panel de gráficos (que va a la derecha).
            _panel = NewRect("Panel", canvasGo.transform);
            var prt = (RectTransform)_panel.transform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0f, 0.5f);
            prt.sizeDelta = new Vector2(420f, 470f);
            prt.anchoredPosition = new Vector2(40f, 0f);
            _panel.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.88f);

            var title = Label(_panel.transform, "VOZ DE PROXIMIDAD", Vector2.zero, 20, FontStyle.Bold,
                420f, TextAnchor.UpperCenter);
            var trt = (RectTransform)title.transform;
            trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f); trt.pivot = new Vector2(0.5f, 1f);
            trt.sizeDelta = new Vector2(0f, 30f); trt.anchoredPosition = new Vector2(0f, -8f);

            float y = -44f;
            var res = new DefaultControls.Resources();

            // ── Estado + medidor: lo PRIMERO, porque es lo que contesta "¿por qué no me oyen?"
            _statusText = Label(_panel.transform, "…", new Vector2(14f, y), 15, FontStyle.Bold, 392f, TextAnchor.MiddleLeft);
            y -= 24f;

            var meterBg = NewRect("MeterBg", _panel.transform);
            var mrt = (RectTransform)meterBg.transform;
            mrt.anchorMin = mrt.anchorMax = mrt.pivot = new Vector2(0f, 1f);
            mrt.anchoredPosition = new Vector2(14f, y);
            mrt.sizeDelta = new Vector2(392f, 14f);
            meterBg.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);

            var fill = NewRect("MeterFill", meterBg.transform);
            var frt = (RectTransform)fill.transform;
            frt.anchorMin = Vector2.zero; frt.anchorMax = new Vector2(0f, 1f);
            frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;
            _levelFill = fill.AddComponent<Image>();
            _levelFill.color = new Color(0.55f, 0.55f, 0.5f);
            y -= 18f;

            _levelText = Label(_panel.transform, "nivel 0.000", new Vector2(14f, y), 13, FontStyle.Normal, 392f, TextAnchor.MiddleLeft);
            y -= 26f;

            // ── Dispositivo
            Label(_panel.transform, "Micrófono", new Vector2(14f, y), 15, FontStyle.Bold, 392f, TextAnchor.MiddleLeft);
            y -= 24f;

            var ddGo = DefaultControls.CreateDropdown(res);
            ddGo.transform.SetParent(_panel.transform, false);
            var ddrt = (RectTransform)ddGo.transform;
            ddrt.anchorMin = ddrt.anchorMax = ddrt.pivot = new Vector2(0f, 1f);
            ddrt.anchoredPosition = new Vector2(14f, y);
            ddrt.sizeDelta = new Vector2(392f, 28f);
            _deviceDropdown = ddGo.GetComponent<Dropdown>();
            _deviceDropdown.onValueChanged.AddListener(i =>
            {
                var vc = Voice();
                if (vc == null) return;
                vc.Device = i <= 0 ? "" : _deviceOptions[i];
            });
            y -= 36f;

            // ── Interruptores
            _micToggle = Row(res, "Micrófono encendido  (F5)", ref y, on =>
            {
                var vc = Voice(); if (vc != null) vc.MicEnabled = on;
            });

            _modeToggle = Row(res, "Voz abierta en vez de pulsar  (F6)", ref y, on =>
            {
                var vc = Voice(); if (vc != null) vc.OpenMic = on;
            });

            _selfTestToggle = Row(res, "Auto-test: oírme a mí mismo", ref y, on =>
            {
                var vc = Voice(); if (vc != null) vc.SelfTest = on;
            });

            var help = Label(_panel.transform,
                "El auto-test prueba micro, códec y salida. NO prueba la red\nni la distancia: eso sigue necesitando dos jugadores.",
                new Vector2(34f, y), 12, FontStyle.Italic, 372f, TextAnchor.UpperLeft);
            help.color = new Color(0.7f, 0.7f, 0.65f);
            ((RectTransform)help.transform).sizeDelta = new Vector2(372f, 32f);
            y -= 38f;

            // ── Tecla de hablar
            _pttLabel = Label(_panel.transform, "hablar: V", new Vector2(14f, y), 15, FontStyle.Normal, 250f, TextAnchor.MiddleLeft);
            var btnGo = DefaultControls.CreateButton(res);
            btnGo.transform.SetParent(_panel.transform, false);
            var brt = (RectTransform)btnGo.transform;
            brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0f, 1f);
            brt.anchoredPosition = new Vector2(270f, y);
            brt.sizeDelta = new Vector2(136f, 26f);
            var btnText = btnGo.GetComponentInChildren<Text>();
            btnText.text = "Cambiar tecla";
            btnText.font = UiFont;
            btnText.fontSize = 13;
            btnGo.GetComponent<Button>().onClick.AddListener(() => { _rebinding = true; UpdatePttLabel(); });
            y -= 34f;

            // ── Umbral de voz abierta
            Label(_panel.transform, "Umbral de voz abierta", new Vector2(14f, y), 14, FontStyle.Normal, 260f, TextAnchor.MiddleLeft);
            _thresholdValue = Label(_panel.transform, "0.020", new Vector2(300f, y), 14, FontStyle.Normal, 106f, TextAnchor.MiddleRight);
            y -= 22f;

            var slGo = DefaultControls.CreateSlider(res);
            slGo.transform.SetParent(_panel.transform, false);
            var srt = (RectTransform)slGo.transform;
            srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0f, 1f);
            srt.anchoredPosition = new Vector2(14f, y);
            srt.sizeDelta = new Vector2(392f, 16f);
            _thresholdSlider = slGo.GetComponent<Slider>();
            _thresholdSlider.minValue = 0f;
            _thresholdSlider.maxValue = 0.2f;
            _thresholdSlider.onValueChanged.AddListener(v =>
            {
                var vc = Voice(); if (vc != null) vc.ActivationThreshold = v;
                _thresholdValue.text = v.ToString("F3");
            });
        }

        private Toggle Row(DefaultControls.Resources res, string label, ref float y,
            UnityEngine.Events.UnityAction<bool> onChanged)
        {
            var go = DefaultControls.CreateToggle(res);
            go.transform.SetParent(_panel.transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(14f, y);
            rt.sizeDelta = new Vector2(392f, 22f);

            var text = go.GetComponentInChildren<Text>();
            text.text = label;
            text.font = UiFont;
            text.fontSize = 14;
            text.color = new Color(0.92f, 0.90f, 0.84f);

            var toggle = go.GetComponent<Toggle>();
            toggle.onValueChanged.AddListener(onChanged);
            y -= 28f;
            return toggle;
        }

        private static Text Label(Transform parent, string text, Vector2 pos, int size,
            FontStyle style, float width, TextAnchor align)
        {
            var go = NewRect("Label", parent);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, 22f);
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
