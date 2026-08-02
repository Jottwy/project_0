using BackroomsSurvival.Net;
using PolymindGames.Options;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-046 — los ajustes de voz dentro del sistema de opciones DEL JUEGO, no en un panel
    /// aparte. Hereda de <see cref="UserOptions{T}"/> del vendor, así que se guardan en el mismo
    /// fichero que el resto de opciones y heredan gratis "Aplicar" y "Restaurar valores".
    ///
    /// SIN EDITAR UN SOLO ARCHIVO DE STP, que es regla dura del proyecto: el asmdef
    /// `BackroomsSurvival` ya referencia `PolymindGames`, así que heredar basta. Y no hace falta
    /// crear el asset: <c>UserOptionsPersistence</c> hace <c>Resources.Load("Options/VoiceOptions")</c>
    /// y, si no lo encuentra, cae a una instancia con los valores por defecto de este archivo.
    ///
    /// EL MICRÓFONO ELEGIDO NO ESTÁ AQUÍ, y es deliberado por dos motivos. Uno técnico:
    /// <see cref="Option{V}"/> exige <c>struct</c> y la persistencia solo recorre campos
    /// <c>IOption</c>, así que un <c>string</c> ni siquiera se guardaría. Y otro de diseño: el
    /// dispositivo es propio de CADA MÁQUINA, y arrastrar "Analogue 1 + 2" a un PC donde no existe
    /// no es una preferencia, es una avería. Se queda en PlayerPrefs, que es local por naturaleza.
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/Options/Voice Options", fileName = nameof(VoiceOptions))]
    public sealed class VoiceOptions : UserOptions<VoiceOptions>
    {
        [SerializeField]
        [Tooltip("Micrófono encendido. OPT-IN: arranca apagado y solo el jugador lo abre.")]
        private Option<bool> _micEnabled = new Option<bool>(false);

        [SerializeField]
        [Tooltip("Voz abierta por detección de actividad. Desmarcado = pulsar para hablar.")]
        private Option<bool> _openMic = new Option<bool>(false);

        [SerializeField]
        [Tooltip("Puerta de ruido: silencia el fondo entre palabras.")]
        private Option<bool> _noiseGate = new Option<bool>(true);

        [SerializeField]
        [Tooltip("Nivel automático: iguala el volumen entre jugadores.")]
        private Option<bool> _autoGain = new Option<bool>(true);

        [SerializeField]
        [Tooltip("Umbral de activación de la voz abierta (0..0,2 RMS).")]
        private Option<float> _activationThreshold = new Option<float>(0.02f);

        [SerializeField]
        [Tooltip("Tecla de hablar, como código de UnityEngine.InputSystem.Key. 55 = V.")]
        private Option<int> _pushToTalkKey = new Option<int>((int)UnityEngine.InputSystem.Key.V);

        [SerializeField]
        [Tooltip("Tecla que ENCIENDE Y APAGA el micrófono, distinta de la de hablar. 60 = F5.")]
        private Option<int> _toggleMicKey = new Option<int>((int)UnityEngine.InputSystem.Key.F5);

        [SerializeField]
        [Tooltip("Canal de un micrófono multicanal: -1 automático, -2 mezcla, ≥0 canal fijo.")]
        private Option<int> _channel = new Option<int>(-1);

        public Option<bool> MicEnabled => _micEnabled;
        public Option<bool> OpenMic => _openMic;
        public Option<bool> NoiseGate => _noiseGate;
        public Option<bool> AutoGain => _autoGain;
        public Option<float> ActivationThreshold => _activationThreshold;
        public Option<int> PushToTalkKey => _pushToTalkKey;
        public Option<int> ToggleMicKey => _toggleMicKey;
        public Option<int> Channel => _channel;

        /// <summary>
        /// Vuelca las opciones sobre el componente vivo.
        ///
        /// Se busca el componente en vez de guardar una referencia porque <c>VoiceCapture</c> lo
        /// añade <c>GameBootstrap</c> en RUNTIME y este objeto es un ScriptableObject que existe
        /// antes que la escena. Que no haya componente (menú principal, escena sin
        /// <c>GameBootstrap</c>) es un caso NORMAL, no un error: los valores quedan guardados y se
        /// aplican en cuanto exista.
        /// </summary>
        protected override void Apply()
        {
            var capture = FindFirstObjectByType<VoiceCapture>();
            if (capture == null) return;
            capture.ApplyOptions(this);
        }
    }
}
