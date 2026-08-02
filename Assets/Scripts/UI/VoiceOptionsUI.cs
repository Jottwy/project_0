using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using PolymindGames.UserInterface;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
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
        [SerializeField] private Toggle _selfTestToggle;
        [SerializeField] private TMP_Dropdown _pttKeyDropdown;
        [SerializeField] private TMP_Dropdown _toggleKeyDropdown;

        /// <summary>
        /// Teclas ofrecidas para hablar y para activar. Es una lista CURADA y no el enum entero:
        /// UnityEngine.InputSystem.Key tiene cientos de valores y un desplegable con todos es
        /// inservible. Están las que la gente usa de verdad para hablar —laterales, alcanzables
        /// con la mano izquierda sin soltar el movimiento— más las de función para el interruptor.
        /// </summary>
        private static readonly Key[] KeyChoices =
        {
            Key.V, Key.B, Key.C, Key.X, Key.Z, Key.T, Key.G, Key.H, Key.R, Key.F,
            Key.CapsLock, Key.LeftAlt, Key.LeftCtrl, Key.LeftShift, Key.Tab, Key.Backquote,
            Key.F1, Key.F2, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12,
        };

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

            FillKeys(_pttKeyDropdown);
            FillKeys(_toggleKeyDropdown);
            if (_pttKeyDropdown != null)
                _pttKeyDropdown.onValueChanged.AddListener(i =>
                    UserOptions.PushToTalkKey.SetValue((int)KeyChoices[Mathf.Clamp(i, 0, KeyChoices.Length - 1)]));
            if (_toggleKeyDropdown != null)
                _toggleKeyDropdown.onValueChanged.AddListener(i =>
                    UserOptions.ToggleMicKey.SetValue((int)KeyChoices[Mathf.Clamp(i, 0, KeyChoices.Length - 1)]));

            // El auto-test NO pasa por las opciones ni por "Aplicar": es una prueba momentanea, y
            // guardarlo significaria volver a entrar en la partida oyendote a ti mismo sin
            // acordarte de por que. Se manda directo al componente, como el microfono.
            if (_selfTestToggle != null)
                _selfTestToggle.onValueChanged.AddListener(v =>
                {
                    var vc = Capture();
                    if (vc != null) vc.SelfTest = v;
                });

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
                    ? "Voz no disponible en esta escena"
                    : !vc.MicEnabled
                        ? "Micrófono apagado"
                        : string.IsNullOrEmpty(vc.ActiveDevice)
                            ? "Encendido pero SIN captura — mira la consola"
                            : vc.IsTransmitting
                                ? $"TRANSMITIENDO — nivel {vc.InputLevel:F3}"
                                : vc.SelfTest
                                    ? $"Auto-test: te oyes a ti — nivel {vc.InputLevel:F3}"
                                    : $"Escuchando — nivel {vc.InputLevel:F3}";
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

            SelectKey(_pttKeyDropdown, UserOptions.PushToTalkKey);
            SelectKey(_toggleKeyDropdown, UserOptions.ToggleMicKey);

            var capture = Capture();
            if (_selfTestToggle != null && capture != null)
                _selfTestToggle.SetIsOnWithoutNotify(capture.SelfTest);

            RefreshDevices();
            RefreshChannels();
        }

        private static void FillKeys(TMP_Dropdown dd)
        {
            if (dd == null) return;
            var names = new List<string>(KeyChoices.Length);
            foreach (var k in KeyChoices) names.Add(k.ToString());
            dd.ClearOptions();
            dd.AddOptions(names);
        }

        /// <summary>Una tecla guardada que no esta en la lista curada (un ajuste viejo, o editado a
        /// mano) cae a la primera en vez de dejar el desplegable en un indice que no corresponde a
        /// lo que el juego esta usando de verdad.</summary>
        private static void SelectKey(TMP_Dropdown dd, int keyCode)
        {
            if (dd == null) return;
            int idx = System.Array.IndexOf(KeyChoices, (Key)keyCode);
            dd.SetValueWithoutNotify(idx >= 0 ? idx : 0);
            dd.RefreshShownValue();
        }

        private void RefreshDevices()
        {
            if (_deviceDropdown == null) return;
            var vc = Capture();

            _devices.Clear();
            _devices.Add("Automático");
            _devices.AddRange(VoiceCapture.Devices);

            var labels = new List<string> { "Automático" };
            for (int i = 1; i < _devices.Count; i++) labels.Add(ShortName(_devices[i]));

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

        /// <summary>
        /// Windows nombra los micrófonos repitiendo el aparato dentro de un paréntesis
        /// ("OBSBOT Tiny2 Microphone (5- OBSBOT Tiny2 Audio)"). La fila del vendor tiene UNA línea
        /// útil, así que ese nombre completo se monta encima del siguiente y el desplegable se
        /// vuelve ilegible. Se corta el paréntesis, que es justo la parte redundante, y se acota
        /// la longitud. El nombre COMPLETO se sigue usando para abrir el dispositivo: lo que se
        /// recorta es solo lo que se pinta.
        /// </summary>
        private static string ShortName(string device)
        {
            if (string.IsNullOrEmpty(device)) return device;
            int paren = device.IndexOf('(');
            string s = paren > 0 ? device.Substring(0, paren).Trim() : device.Trim();
            return s.Length <= 34 ? s : s.Substring(0, 33) + "…";
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
