using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using PolymindGames.UserInterface;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// ADR-046 — pestaña "Voz" del menú de opciones del juego. Hereda de
    /// <see cref="UserOptionsUI{T}"/> del vendor, así que "Aplicar" y "Restaurar valores" ya
    /// funcionan sin escribir nada, y **no toca un solo archivo de STP**.
    ///
    /// TODOS LOS CAMPOS SON OPCIONALES, a propósito. Esta pestaña se autora a mano en el prefab, y
    /// un panel a medio montar no puede tirar excepciones por cada control que falte: así se puede
    /// enganchar un control, entrar a probarlo, y seguir. Cada uso va con su comprobación de nulo;
    /// no es defensa por si acaso, es la condición para poder montarlo por partes.
    ///
    /// El selector de micrófono NO viaja en <see cref="VoiceOptions"/> (es de cada máquina, ver el
    /// doc de esa clase): se lee y se escribe directamente sobre <see cref="VoiceCapture"/>.
    /// </summary>
    public sealed class VoiceOptionsUI : UserOptionsUI<VoiceOptions>
    {
        [Header("Micrófono (no se guarda en las opciones: es local a esta máquina)")]
        [SerializeField] private Dropdown _deviceDropdown;
        [SerializeField] private Dropdown _channelDropdown;
        [SerializeField] private Image _levelFill;
        [SerializeField] private Text _levelText;

        [Header("Ajustes")]
        [SerializeField] private Toggle _micEnabledToggle;
        [SerializeField] private Toggle _openMicToggle;
        [SerializeField] private Toggle _noiseGateToggle;
        [SerializeField] private Toggle _autoGainToggle;
        [SerializeField] private Slider _thresholdSlider;
        [SerializeField] private Text _thresholdValue;

        private readonly List<string> _devices = new List<string>();
        private VoiceCapture _capture;

        private VoiceCapture Capture()
        {
            if (_capture == null) _capture = FindFirstObjectByType<VoiceCapture>();
            return _capture;
        }

        protected override void Start()
        {
            base.Start();

            if (_micEnabledToggle != null)
                _micEnabledToggle.onValueChanged.AddListener(v => UserOptions.MicEnabled.SetValue(v));
            if (_openMicToggle != null)
                _openMicToggle.onValueChanged.AddListener(v => UserOptions.OpenMic.SetValue(v));
            if (_noiseGateToggle != null)
                _noiseGateToggle.onValueChanged.AddListener(v => UserOptions.NoiseGate.SetValue(v));
            if (_autoGainToggle != null)
                _autoGainToggle.onValueChanged.AddListener(v => UserOptions.AutoGain.SetValue(v));
            if (_thresholdSlider != null)
            {
                _thresholdSlider.minValue = 0f;
                _thresholdSlider.maxValue = 0.2f;
                _thresholdSlider.onValueChanged.AddListener(v =>
                {
                    UserOptions.ActivationThreshold.SetValue(v);
                    if (_thresholdValue != null) _thresholdValue.text = v.ToString("F3");
                });
            }
            if (_channelDropdown != null)
                _channelDropdown.onValueChanged.AddListener(i =>
                    UserOptions.Channel.SetValue(i == 0 ? -1 : i == 1 ? -2 : i - 2));

            // El dispositivo se aplica al instante y NO pasa por "Aplicar": cambiar de micrófono
            // es una acción de prueba —quieres oír si ese sirve— y obligar a confirmar convertiría
            // "probar los 15 micros" en 15 confirmaciones.
            if (_deviceDropdown != null)
                _deviceDropdown.onValueChanged.AddListener(i =>
                {
                    var vc = Capture();
                    if (vc != null) vc.Device = i <= 0 ? "" : _devices[i];
                });
        }

        private void Update()
        {
            var vc = Capture();
            if (vc == null) return;

            if (_levelFill != null)
            {
                _levelFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(vc.InputLevel * 4f), 1f);
                _levelFill.color = vc.IsTransmitting
                    ? new Color(0.4f, 0.9f, 0.45f)
                    : new Color(0.55f, 0.55f, 0.5f);
            }

            if (_levelText != null)
            {
                // Lo que el jugador necesita saber en una sola línea, y en este orden: si le oyen,
                // si el micro capta algo, y si el nivel automático está compensando.
                _levelText.text = !vc.MicEnabled
                    ? "micrófono apagado"
                    : string.IsNullOrEmpty(vc.ActiveDevice)
                        ? "encendido pero SIN captura"
                        : vc.IsTransmitting
                            ? $"transmitiendo — nivel {vc.InputLevel:F3}"
                            : $"nivel {vc.InputLevel:F3}";
            }
        }

        protected override void ResetUIState()
        {
            if (_micEnabledToggle != null) _micEnabledToggle.SetIsOnWithoutNotify(UserOptions.MicEnabled);
            if (_openMicToggle != null) _openMicToggle.SetIsOnWithoutNotify(UserOptions.OpenMic);
            if (_noiseGateToggle != null) _noiseGateToggle.SetIsOnWithoutNotify(UserOptions.NoiseGate);
            if (_autoGainToggle != null) _autoGainToggle.SetIsOnWithoutNotify(UserOptions.AutoGain);
            if (_thresholdSlider != null) _thresholdSlider.SetValueWithoutNotify(UserOptions.ActivationThreshold);
            if (_thresholdValue != null) _thresholdValue.text = ((float)UserOptions.ActivationThreshold).ToString("F3");

            RefreshDevices();
            RefreshChannels();
        }

        private void RefreshDevices()
        {
            if (_deviceDropdown == null) return;
            var vc = Capture();

            _devices.Clear();
            _devices.Add("(automático)");
            _devices.AddRange(VoiceCapture.Devices);

            var labels = new List<string> { "(automático)" };
            for (int i = 1; i < _devices.Count; i++)
            {
                int rate = VoiceCapture.PickCaptureRate(_devices[i]);
                labels.Add(rate == VoiceCapture.SampleRate
                    ? $"{_devices[i]}  [{rate / 1000} kHz]"
                    : $"{_devices[i]}  [{rate / 1000} kHz → remuestreo]");
            }

            _deviceDropdown.ClearOptions();
            _deviceDropdown.AddOptions(labels);

            // Un dispositivo guardado que ya no existe cae a "automático" en vez de dejar el
            // desplegable mintiendo sobre lo que se abrió de verdad.
            int idx = 0;
            if (vc != null && !string.IsNullOrEmpty(vc.Device))
            {
                int found = _devices.IndexOf(vc.Device);
                idx = found >= 0 ? found : 0;
            }
            _deviceDropdown.SetValueWithoutNotify(idx);
        }

        private void RefreshChannels()
        {
            if (_channelDropdown == null) return;
            var vc = Capture();
            int ch = vc != null ? vc.Channels : 1;

            // Solo tiene sentido en dispositivos multicanal. En un micro normal sería ruido; en una
            // interfaz de audio es LA opción que decide si se oye algo.
            _channelDropdown.gameObject.SetActive(ch > 1);
            if (ch <= 1) return;

            var opts = new List<string> { "automático (el más fuerte)", "mezcla de todos" };
            for (int c = 0; c < ch; c++) opts.Add("canal " + (c + 1));
            _channelDropdown.ClearOptions();
            _channelDropdown.AddOptions(opts);

            int sel = UserOptions.Channel == -1 ? 0
                : UserOptions.Channel == -2 ? 1
                : Mathf.Clamp(UserOptions.Channel + 2, 0, opts.Count - 1);
            _channelDropdown.SetValueWithoutNotify(sel);
        }
    }
}
