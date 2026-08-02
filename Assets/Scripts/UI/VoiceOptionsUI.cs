using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using PolymindGames.UserInterface;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// ADR-046 — los ajustes de voz DENTRO de la pestaña Audio del menú, no en una pestaña aparte.
    ///
    /// Vive como SEGUNDO componente del mismo panel que <c>AudioOptionsUI</c>. Cada uno gestiona su
    /// propio tipo de opciones y ambos escuchan los mismos botones Aplicar/Restaurar, así que los
    /// dos se aplican juntos — que es justo lo que el jugador espera al darle a "Aplicar" estando
    /// en Audio.
    ///
    /// POR QUÉ AQUÍ Y NO EN UNA PESTAÑA PROPIA: una quinta pestaña exigía atarla al conmutador, y
    /// en este prefab **no existe ningún componente ni evento que ate un panel con su pestaña**
    /// (comprobado buscando quién referencia los componentes de `AudioPanel`: nadie). Metiendo las
    /// filas en un panel que ya funciona, ese problema desaparece entero. Y la voz es audio: su
    /// sitio natural es donde están los volúmenes.
    ///
    /// TMP y no uGUI: las filas del vendor usan <see cref="TMP_Dropdown"/> y
    /// <see cref="TextMeshProUGUI"/>. Usar los controles de uGUI habría dado filas que funcionan
    /// pero se ven de otro juego.
    ///
    /// TODOS LOS CAMPOS SON OPCIONALES. El panel lo monta un script de editor y un montaje a medias
    /// no puede tirar excepciones por cada control que falte: así se puede inyectar, entrar a
    /// probar, corregir y repetir.
    /// </summary>
    public sealed class VoiceOptionsUI : UserOptionsUI<VoiceOptions>
    {
        [Header("Dispositivo (local a esta máquina: NO se guarda en las opciones)")]
        [SerializeField] private TMP_Dropdown _deviceDropdown;
        [SerializeField] private TMP_Dropdown _channelDropdown;
        [SerializeField] private TextMeshProUGUI _statusText;
        [SerializeField] private Image _levelFill;

        [Header("Ajustes")]
        [SerializeField] private Toggle _micEnabledToggle;
        [SerializeField] private Toggle _openMicToggle;
        [SerializeField] private Toggle _noiseGateToggle;
        [SerializeField] private Toggle _autoGainToggle;
        [SerializeField] private Slider _thresholdSlider;

        private readonly List<string> _devices = new List<string>();
        private VoiceCapture _capture;
        private int _shownChannels = -1;

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
                // El slider del vendor viene configurado 0..100 para volúmenes; el umbral vive en
                // 0..0,2 RMS. Se mapea aquí en vez de tocar el prefab, para que la fila siga
                // siendo un clon exacto de las demás.
                _thresholdSlider.minValue = 0f;
                _thresholdSlider.maxValue = 100f;
                _thresholdSlider.wholeNumbers = true;
                _thresholdSlider.onValueChanged.AddListener(v =>
                    UserOptions.ActivationThreshold.SetValue(v / 500f));
            }

            if (_channelDropdown != null)
                _channelDropdown.onValueChanged.AddListener(i =>
                    UserOptions.Channel.SetValue(i == 0 ? -1 : i == 1 ? -2 : i - 2));

            // El micrófono se aplica AL INSTANTE y no pasa por "Aplicar": cambiar de dispositivo es
            // una acción de prueba —quieres oír si ese sirve— y obligar a confirmar convertiría
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

            if (_statusText != null)
            {
                _statusText.text = vc == null
                    ? "voz no disponible en esta escena"
                    : !vc.MicEnabled
                        ? "micrófono apagado"
                        : string.IsNullOrEmpty(vc.ActiveDevice)
                            ? "encendido pero SIN captura — mira la consola"
                            : vc.IsTransmitting
                                ? $"TRANSMITIENDO — nivel {vc.InputLevel:F3}"
                                : $"escuchando — nivel {vc.InputLevel:F3}";
            }

            if (vc == null) return;

            if (_levelFill != null)
            {
                _levelFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(vc.InputLevel * 4f), 1f);
                _levelFill.color = vc.IsTransmitting
                    ? new Color(0.4f, 0.9f, 0.45f)
                    : new Color(0.55f, 0.55f, 0.5f);
            }

            // Los canales solo se conocen DESPUÉS de abrir el dispositivo, así que el desplegable
            // no puede construirse una sola vez al mostrar el panel.
            if (vc.Channels != _shownChannels)
            {
                _shownChannels = vc.Channels;
                RefreshChannels();
            }
        }

        protected override void ResetUIState()
        {
            if (_micEnabledToggle != null) _micEnabledToggle.SetIsOnWithoutNotify(UserOptions.MicEnabled);
            if (_openMicToggle != null) _openMicToggle.SetIsOnWithoutNotify(UserOptions.OpenMic);
            if (_noiseGateToggle != null) _noiseGateToggle.SetIsOnWithoutNotify(UserOptions.NoiseGate);
            if (_autoGainToggle != null) _autoGainToggle.SetIsOnWithoutNotify(UserOptions.AutoGain);
            if (_thresholdSlider != null)
                _thresholdSlider.SetValueWithoutNotify(UserOptions.ActivationThreshold * 500f);

            RefreshDevices();
            RefreshChannels();
        }

        private void RefreshDevices()
        {
            if (_deviceDropdown == null) return;
            var vc = Capture();

            _devices.Clear();
            _devices.Add("Automático");
            _devices.AddRange(VoiceCapture.Devices);

            var labels = new List<string> { "Automático" };
            for (int i = 1; i < _devices.Count; i++)
            {
                int rate = VoiceCapture.PickCaptureRate(_devices[i]);
                labels.Add(rate == VoiceCapture.SampleRate
                    ? _devices[i]
                    : $"{_devices[i]} ({rate / 1000} kHz)");
            }

            _deviceDropdown.ClearOptions();
            _deviceDropdown.AddOptions(labels);

            // Un dispositivo guardado que ya no existe (cascos desenchufados) cae a "Automático"
            // en vez de dejar el desplegable mintiendo sobre lo que se abrió de verdad.
            int idx = 0;
            if (vc != null && !string.IsNullOrEmpty(vc.Device))
            {
                int found = _devices.IndexOf(vc.Device);
                idx = found >= 0 ? found : 0;
            }
            _deviceDropdown.SetValueWithoutNotify(idx);
            _deviceDropdown.RefreshShownValue();
        }

        private void RefreshChannels()
        {
            if (_channelDropdown == null) return;
            var vc = Capture();
            int ch = vc != null ? vc.Channels : 1;

            // Solo tiene sentido en dispositivos multicanal: en un micro normal sería ruido, y en
            // una interfaz de audio es LA opción que decide si se oye algo.
            var row = _channelDropdown.transform.parent != null
                ? _channelDropdown.transform.parent.gameObject
                : _channelDropdown.gameObject;
            row.SetActive(ch > 1);
            if (ch <= 1) return;

            var opts = new List<string> { "Automático (canal más fuerte)", "Mezcla de todos" };
            for (int c = 0; c < ch; c++) opts.Add("Canal " + (c + 1));
            _channelDropdown.ClearOptions();
            _channelDropdown.AddOptions(opts);

            int sel = UserOptions.Channel == -1 ? 0
                : UserOptions.Channel == -2 ? 1
                : Mathf.Clamp(UserOptions.Channel + 2, 0, opts.Count - 1);
            _channelDropdown.SetValueWithoutNotify(sel);
            _channelDropdown.RefreshShownValue();
        }
    }
}
