using System;
using Concentus;
using Concentus.Enums;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-046 Fase 3 — captura el micrófono local, lo codifica en Opus y lo manda al backend
    /// propio, que decide a quién llega (el filtro de proximidad autoritativo vive en el host).
    ///
    /// MICRÓFONO OPT-IN. Arranca CERRADO y solo se abre cuando el jugador lo enciende: es dato
    /// personal, y un componente que empieza a grabar por su cuenta no es aceptable ni aunque
    /// nadie llegue a oírlo. Mientras transmite hay señal visible (<see cref="IsTransmitting"/>,
    /// que el HUD lee).
    ///
    /// DOS MODOS, los dos pedidos: push-to-talk (defecto) y voz abierta con detección de
    /// actividad. PTT es el defecto porque no cuesta ancho de banda cuando nadie habla y porque
    /// en un juego de sigilo un micro siempre abierto delata a quien está escondido sin que él
    /// lo decida.
    ///
    /// CERO ALLOCS EN EL CAMINO CALIENTE: un buffer de captura, uno de PCM y uno de salida, los
    /// tres reservados una vez. La API de Concentus 2.x es <c>Span</c>, así que se codifica
    /// directamente sobre ellos sin copias intermedias.
    ///
    /// Removible: borra el archivo y nadie puede hablar. Nada más del sistema depende de él.
    /// </summary>
    public sealed class VoiceCapture : MonoBehaviour
    {
        // ── Formato. Cambiar cualquiera de los tres rompe la simetría con el reproductor ──

        /// <summary>
        /// 48 kHz mono, la tasa NATIVA de Opus.
        ///
        /// Empezó siendo 16 kHz y hubo que cambiarlo con evidencia en la mano: **los 15 micrófonos
        /// de la máquina de desarrollo reportan `48000-48000` y ninguno admite 16 kHz**. No es una
        /// máquina rara — en Windows el driver expone la tasa del mezclador y esa es 48 kHz. Pedir
        /// 16 kHz apagaba la captura en TODOS los dispositivos.
        ///
        /// A 48 kHz no hay remuestreo en el camino común: micro → Opus → decodificador → salida,
        /// todo a la misma tasa. Un dispositivo que NO admita 48 kHz se captura a la suya y se
        /// remuestrea aquí (ver <see cref="_needsResample"/>), para no dejar fuera a nadie.
        /// </summary>
        public const int SampleRate = 48000;

        /// <summary>20 ms por trama, el tamaño canónico de Opus para VOIP.</summary>
        public const int FrameSamples = SampleRate / 50; // 960

        /// <summary>Dos tramas por datagrama. A una sola, la cabecera IP/UDP (28 B) pesaría casi
        /// tanto como la carga útil (~30-60 B); a dos, el coste relativo se parte por la mitad a
        /// cambio de 20 ms de latencia. Medido: una trama de tono puro sale a 30 B.</summary>
        public const int FramesPerPacket = 2;

        // ── Ajustes ──

        [Header("Micrófono (opt-in: arranca CERRADO)")]
        [Tooltip("Dispositivo de captura. Vacío = el que Unity considere por defecto.")]
        [SerializeField] private string _device = "";

        [Header("Activación")]
        [Tooltip("Voz abierta por detección de actividad. Desmarcado = push-to-talk.")]
        [SerializeField] private bool _openMic;

        [Tooltip("Tecla de push-to-talk.")]
        [SerializeField] private Key _pushToTalkKey = Key.V;

        [Tooltip("Enciende y apaga el micrófono. Existe porque todavía NO hay pantalla de " +
                 "opciones y el micrófono es opt-in: sin esto no habría forma de encenderlo en " +
                 "una build. F3 y F4 ya están cogidas (NetworkDebugHud, ajustes gráficos).")]
        [SerializeField] private Key _toggleMicKey = Key.F5;

        [Tooltip("Alterna push-to-talk / voz abierta en caliente, para poder comparar los dos " +
                 "modos dentro de la misma partida en vez de relanzar.")]
        [SerializeField] private Key _toggleModeKey = Key.F6;

        [Tooltip("Umbral de energía RMS (0..1) por encima del cual la voz abierta transmite. Un " +
                 "umbral fijo y bajo convierte un ventilador en emisión continua.")]
        [Range(0f, 0.2f)]
        [SerializeField] private float _activationThreshold = 0.02f;

        [Tooltip("Segundos que sigue transmitiendo tras caer bajo el umbral. Sin esta cola, la " +
                 "voz abierta corta el final de cada frase.")]
        [Range(0f, 2f)]
        [SerializeField] private float _releaseSeconds = 0.3f;

        [Header("Procesado")]
        [Tooltip("Puerta de ruido: silencia el fondo entre palabras. Con histéresis y liberación " +
                 "suave, no un corte seco.")]
        [SerializeField] private bool _noiseGate = true;

        [Tooltip("Nivel automático (AGC): iguala el volumen entre jugadores. Solo adapta mientras " +
                 "hay voz, nunca amplificando el silencio.")]
        [SerializeField] private bool _autoGain = true;

        [Header("Códec")]
        /// <remarks>
        /// 32 kbps medido sobre voz sintética (fundamental + formantes + envolvente silábica, NO
        /// un tono puro, que comprime irrealmente bien): 80,8 B por trama = 3,9 KB/s. Frente a los
        /// 24 kbps anteriores es +0,9 KB/s, despreciable al lado de los 2,7-4,1 Mbps por peer que
        /// el juego ya mueve leyendo chunks.
        /// </remarks>
        [Tooltip("Bitrate objetivo en bps. Opus es VBR: en silencio baja solo.")]
        [Range(8000, 64000)]
        [SerializeField] private int _bitrate = 32000;

        /// <remarks>
        /// Barrido medido bajo el Mono de Unity a 48 kHz, ms por trama de 20 ms: complejidad 3 =
        /// 0,79; **5 = 1,44**; 7 = 2,15; 9 = 2,45. Subir de 5 a 7 cuesta **+50 % de CPU** por una
        /// ganancia sutil — en Opus el bitrate pesa mucho más en la calidad percibida que la
        /// complejidad. Y esto corre en el hilo principal: 2,15 ms son el 13 % de un frame a
        /// 60 fps, que es un precio que la calidad extra no paga.
        /// </remarks>
        [Tooltip("Complejidad del codificador (0-10). 5 es el punto de mejor calidad por ms.")]
        [Range(0, 10)]
        [SerializeField] private int _complexity = 5;

        // ── Estado público, leído por el HUD ──

        /// <summary>True mientras el micro está abierto Y saliendo audio. Es lo que el indicador
        /// debe mostrar: "te están oyendo", no "tienes el micro encendido".</summary>
        public bool IsTransmitting { get; private set; }

        /// <summary>Micrófono habilitado por el jugador. Persistido en PlayerPrefs.</summary>
        public bool MicEnabled
        {
            get => _micEnabled;
            set
            {
                if (_micEnabled == value) return;
                _micEnabled = value;
                PlayerPrefs.SetInt(PrefMicEnabled, value ? 1 : 0);
                if (value) StartDevice();
                else StopDevice();
                MirrorToOptions();
            }
        }

        public bool OpenMic
        {
            get => _openMic;
            set { _openMic = value; PlayerPrefs.SetInt(PrefOpenMic, value ? 1 : 0); MirrorToOptions(); }
        }

        /// <summary>Micrófonos que ve el sistema. Se consulta en vivo y no se cachea: enchufar
        /// unos cascos USB con la partida abierta cambia esta lista.</summary>
        public static string[] Devices => Microphone.devices;

        /// <summary>
        /// Vuelca los ajustes del menú del juego sobre este componente. Lo llama
        /// <c>VoiceOptions.Apply()</c>.
        ///
        /// El MICRÓFONO se aplica el ÚLTIMO a propósito: su setter reabre la captura, y hacerlo
        /// antes de fijar canal y modo reabriría el dispositivo con los valores viejos y otra vez
        /// con los nuevos — dos aperturas por cada cambio de cualquier ajuste.
        /// </summary>
        public void ApplyOptions(object options)
        {
            if (options is not Gameplay.VoiceOptions o) return;
            // Sin esta guarda, cada setter escribiria de vuelta en las opciones, eso llamaria a
            // Apply() otra vez y entrariamos en bucle. La direccion aqui es UNA: opciones ->
            // componente.
            _applyingOptions = true;
            try { ApplyOptionsCore(o); } finally { _applyingOptions = false; }
        }

        private void ApplyOptionsCore(Gameplay.VoiceOptions o)
        {
            _openMic = o.OpenMic.Value;
            _noiseGate = o.NoiseGate.Value;
            _autoGain = o.AutoGain.Value;
            _activationThreshold = Mathf.Clamp(o.ActivationThreshold.Value, 0f, 0.2f);
            _pushToTalkKey = (Key)o.PushToTalkKey.Value;
            _toggleMicKey = (Key)o.ToggleMicKey.Value;
            bool channelChanged = _channel != o.Channel.Value;
            _channel = o.Channel.Value;

            bool want = o.MicEnabled.Value;
            if (want != _micEnabled)
            {
                MicEnabled = want;
            }
            else if (want && channelChanged)
            {
                // Cambiar de canal exige reabrir: el número de canales y el buffer intercalado se
                // fijan al abrir el dispositivo.
                StopDevice();
                StartDevice();
            }
        }

        /// <summary>Dispositivo elegido; vacío = el primero que reporte Unity. Cambiarlo reabre la
        /// captura en caliente, sin relanzar.</summary>
        public string Device
        {
            get => _device;
            set
            {
                if (_device == value) return;
                _device = value ?? "";
                PlayerPrefs.SetString(PrefDevice, _device);
                if (_micEnabled) { StopDevice(); StartDevice(); }
            }
        }

        /// <summary>Nombre del dispositivo REALMENTE abierto (puede no ser <see cref="Device"/> si
        /// éste está vacío o ya no existe). Vacío mientras la captura está parada.</summary>
        public string ActiveDevice =>
            _winmm != null && _winmm.IsRunning ? _winmm.DeviceName
            : _clip != null ? _activeDevice : "";

        /// <summary>Nivel de entrada de la última trama capturada, 0..1 (RMS). Es lo que pinta el
        /// medidor: si esto no se mueve al hablar, el problema está ANTES del códec.</summary>
        public float InputLevel { get; private set; }

        /// <summary>Canales que expone el dispositivo abierto. 1 mientras no hay captura.</summary>
        public int Channels => _clip != null ? _channels : 1;

        /// <summary>
        /// Qué se hace con un dispositivo de varios canales: <c>-1</c> automático (se queda con el
        /// más fuerte muestra a muestra), <c>-2</c> mezcla de todos, <c>≥0</c> un canal fijo.
        ///
        /// El automático es el defecto porque resuelve sin configuración el caso que motivó todo
        /// esto: una interfaz de audio con el micrófono en UNA de sus dos entradas.
        /// </summary>
        public int Channel
        {
            get => _channel;
            set { _channel = value; PlayerPrefs.SetInt(PrefChannel, value); MirrorToOptions(); }
        }

        public static string ChannelName(int channel) =>
            channel == -1 ? "automático" : channel == -2 ? "mezcla" : "canal " + (channel + 1);

        public bool NoiseGate
        {
            get => _noiseGate;
            set { _noiseGate = value; PlayerPrefs.SetInt(PrefGate, value ? 1 : 0); MirrorToOptions(); }
        }

        public bool AutoGain
        {
            get => _autoGain;
            set
            {
                _autoGain = value;
                PlayerPrefs.SetInt(PrefAgc, value ? 1 : 0);
                if (!value) _dynamics.Reset();
                MirrorToOptions();
            }
        }

        /// <summary>Ganancia que el AGC está aplicando ahora mismo. El panel la enseña porque es
        /// la respuesta a "¿por qué se me oye distinto que antes?".</summary>
        public float AutoGainValue => _dynamics.Gain;

        public float ActivationThreshold
        {
            get => _activationThreshold;
            set { _activationThreshold = Mathf.Clamp(value, 0f, 0.2f); PlayerPrefs.SetFloat(PrefThreshold, _activationThreshold); MirrorToOptions(); }
        }

        public Key PushToTalkKey
        {
            get => _pushToTalkKey;
            set { _pushToTalkKey = value; PlayerPrefs.SetInt(PrefPttKey, (int)value); MirrorToOptions(); }
        }

        /// <summary>Tecla que enciende y apaga el micrófono. SEPARADA de la de hablar a propósito:
        /// una se mantiene pulsada durante una frase y la otra se pulsa una vez cada media hora, y
        /// compartirlas haría imposible tener el micro abierto sin hablar.</summary>
        public Key ToggleMicKey
        {
            get => _toggleMicKey;
            set { _toggleMicKey = value; PlayerPrefs.SetInt(PrefToggleKey, (int)value); MirrorToOptions(); }
        }

        /// <summary>
        /// AUTO-TEST: te oyes a ti mismo. La trama recién codificada se decodifica y se reproduce
        /// en local, así que probar la voz deja de necesitar un segundo jugador.
        ///
        /// QUÉ CUBRE Y QUÉ NO, porque un test que miente es peor que ninguno: cubre micrófono,
        /// captura, codificación, decodificación y salida de audio. **NO cubre** la red, ni el
        /// filtro de proximidad del host, ni el <c>AudioSource</c> en el proxy, ni la curva de
        /// corte duro — todo eso vive en `RemoteVoicePlayer` y sigue exigiendo dos jugadores.
        /// Si te oyes aquí y no te oyen allí, el fallo está en esa mitad y no en el micro.
        /// </summary>
        public bool SelfTest
        {
            get => _selfTest;
            set
            {
                if (_selfTest == value) return;
                _selfTest = value;
                if (!value) StopMonitor();
                Debug.Log($"[VoiceCapture] Auto-test {(value ? "ACTIVADO: te oyes a ti mismo" : "desactivado")}");
            }
        }

        private const string PrefMicEnabled = "voice.mic_enabled";
        private const string PrefOpenMic = "voice.open_mic";
        private const string PrefDevice = "voice.device";
        private const string PrefThreshold = "voice.threshold";
        private const string PrefPttKey = "voice.ptt_key";
        private const string PrefChannel = "voice.channel";
        private const string PrefToggleKey = "voice.toggle_key";
        private const string PrefGate = "voice.gate";
        private const string PrefAgc = "voice.agc";

        // ── Interior ──

        private IPCClient _ipc;
        private IOpusEncoder _encoder;
        private AudioClip _clip;
        private string _activeDevice;
        private bool _micEnabled;

        private float[] _interleaved;    // tal cual lo entrega el dispositivo: muestras × canales
        private float[] _capture;        // ya en UN canal, a la tasa del APARATO
        private short[] _pcm;            // la misma ventana a la tasa del CÓDEC, PCM 16 bits
        private int _channels = 1;
        private int _channel = -1;       // -1 automático (canal más fuerte), -2 mezcla, ≥0 fijo
        private int _captureRate = SampleRate;
        private int _inputFrameSamples = FrameSamples;
        private bool _needsResample;
        private float _resampleTail;     // última muestra de la trama previa, para no chasquear
        private byte[] _packet;          // salida codificada (FramesPerPacket tramas)
        private int _packetBytes;        // bytes usados de _packet
        private int _framesInPacket;

        private int _readHead;           // posición ya consumida del clip circular
        private ushort _seq;
        private float _holdUntil;        // cola de la detección de actividad
        private bool _warnedNoDevice;
        private float _levelPeak;
        private int _levelFrames;
        private float _nextLevelLog;

        private readonly VoiceDynamics _dynamics = new VoiceDynamics();
        private bool _applyingOptions;

        // Backend alternativo. Ver ApplySilenceFallback() para POR QUE existe.
        private WinMmCapture _winmm;
        private short[] _winmmScratch;
        private int _silentFrames;
        private int _winmmFill;
        private bool _fallbackTried;

        /// <summary>Tramas seguidas de silencio DIGITAL EXACTO que se toleran antes de cambiar de
        /// backend. 150 son 3 s: suficiente para no confundir a alguien que simplemente no habla
        /// —el silencio real de una sala nunca es 0,0000 exacto— y poco para no hacer esperar.</summary>
        private const int SilentFramesBeforeFallback = 150;

        /// <summary>
        /// Refleja en las opciones del juego un cambio hecho por tecla o por el panel de
        /// diagnostico, para que el menu no muestre algo distinto de lo que esta pasando. No
        /// llama a Save(): guardar en disco por cada pulsacion de F5 seria escribir un fichero
        /// por tecla; el sistema del vendor ya persiste al aplicar desde el menu.
        /// </summary>
        private void MirrorToOptions()
        {
            if (_applyingOptions) return;
            var o = Gameplay.VoiceOptions.Instance;
            if (o == null) return;
            o.MicEnabled.SetValue(_micEnabled);
            o.OpenMic.SetValue(_openMic);
            o.NoiseGate.SetValue(_noiseGate);
            o.AutoGain.SetValue(_autoGain);
            o.ActivationThreshold.SetValue(_activationThreshold);
            o.PushToTalkKey.SetValue((int)_pushToTalkKey);
            o.ToggleMicKey.SetValue((int)_toggleMicKey);
            o.Channel.SetValue(_channel);
        }



        // Auto-test (monitor local)
        private bool _selfTest;
        private IOpusDecoder _monitorDecoder;
        private AudioSource _monitorSource;
        private short[] _monitorRing;
        private short[] _monitorFrame;
        private int _monitorWrite;
        private int _monitorRead;

        private void Awake()
        {
            _pcm = new short[FrameSamples];
            // Una trama Opus nunca pasa de 1275 B por definición del formato; dos de ellas más sus
            // cabeceras de longitud caben en 4096 con holgura. Se reserva UNA vez.
            _packet = new byte[4096];

            _micEnabled = PlayerPrefs.GetInt(PrefMicEnabled, 0) != 0;
            _openMic = PlayerPrefs.GetInt(PrefOpenMic, _openMic ? 1 : 0) != 0;
            _device = PlayerPrefs.GetString(PrefDevice, _device ?? "");
            _activationThreshold = PlayerPrefs.GetFloat(PrefThreshold, _activationThreshold);
            _pushToTalkKey = (Key)PlayerPrefs.GetInt(PrefPttKey, (int)_pushToTalkKey);
            _toggleMicKey = (Key)PlayerPrefs.GetInt(PrefToggleKey, (int)_toggleMicKey);
            _channel = PlayerPrefs.GetInt(PrefChannel, _channel);
            _noiseGate = PlayerPrefs.GetInt(PrefGate, _noiseGate ? 1 : 0) != 0;
            _autoGain = PlayerPrefs.GetInt(PrefAgc, _autoGain ? 1 : 0) != 0;

            // ARRANCA HABLANDO, siempre, incluso sin micrófonos. Hasta ahora este componente solo
            // escribía al encenderse, así que "no hay VoiceCapture en la escena" y "el micro está
            // apagado" producían EXACTAMENTE el mismo log: nada. Esa ambigüedad costó un playtest.
            var devices = Microphone.devices;
            Debug.Log($"[VoiceCapture] Listo. Microfonos detectados: {devices.Length}" +
                      (devices.Length > 0 ? " -> " + string.Join(" | ", devices) : " (NINGUNO)") +
                      $". Micro={(_micEnabled ? "ON" : "OFF")} ({_toggleMicKey} lo alterna), " +
                      $"modo={(_openMic ? "voz abierta" : "push-to-talk " + _pushToTalkKey)}.");
        }

        private void OnEnable()
        {
            // ADR-046: la 2.x de Concentus intenta cargar un libopus NATIVO si lo encuentra. El
            // motivo entero de elegir códec gestionado era no depender de un binario por
            // plataforma, así que se apaga explícitamente.
            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            if (_micEnabled) StartDevice();
        }

        private void OnDisable() => StopDevice();

        private void Update()
        {
            PollHotkeys();

            if (!_micEnabled || _clip == null)
            {
                IsTransmitting = false;
                return;
            }
            if (_ipc == null && !IPCClient.TryGetInstance(out _ipc)) return;

            if (_winmm != null && _winmm.IsRunning)
            {
                PumpWinMm();
                return;
            }

            // El clip del micro es CIRCULAR. Sin consumir por tramas completas y sin detectar el
            // envolvimiento, se leen muestras rancias o repetidas y la voz suena entrecortada.
            int writeHead = Microphone.GetPosition(_activeDevice);
            if (writeHead < 0) return;

            int available = writeHead - _readHead;
            if (available < 0) available += _clip.samples;

            // Si nos hemos quedado más de medio clip por detrás, el escritor nos ha dado la vuelta
            // y lo que hay en medio ya es basura: saltar es preferible a reproducir audio
            // corrupto y quedarse permanentemente atrás.
            if (available > _clip.samples / 2)
            {
                _readHead = writeHead;
                return;
            }

            // Se consume en bloques del tamaño que dicte la TASA DEL DISPOSITIVO, no la del códec:
            // a 44,1 kHz una trama de 20 ms son 882 muestras, no 960.
            //
            // `available`, `_readHead` y `_clip.samples` están en MUESTRAS POR CANAL; el buffer de
            // `GetData`, en cambio, se llena INTERCALADO y por eso mide `muestras × canales`.
            // Confundir las dos unidades es exactamente el bug que rompía las interfaces estéreo.
            while (available >= _inputFrameSamples)
            {
                _clip.GetData(_interleaved, _readHead);
                _readHead = (_readHead + _inputFrameSamples) % _clip.samples;
                available -= _inputFrameSamples;
                DownmixToMono();
                ProcessFrame();
            }
        }

        /// <summary>
        /// Cambia a <see cref="WinMmCapture"/> cuando el dispositivo entrega SILENCIO DIGITAL
        /// EXACTO durante varios segundos.
        ///
        /// El caso es real y medido: una interfaz de audio expone sus entradas como un endpoint
        /// estéreo ("Analogue 1 + 2") y `Microphone.Start` **no admite pedir número de canales**.
        /// Unity negocia mono, el driver le devuelve ceros, y la propia prueba de Windows sobre el
        /// mismo aparato da 99 % de volumen. No hay parámetro que tocar desde la API de Unity.
        ///
        /// La condición es CERO EXACTO y no "bajo": el ruido de una sala real, por callada que
        /// esté, nunca da 0,0000 en 150 tramas seguidas. Un micrófono silenciado por hardware sí
        /// lo daría, y en ese caso el cambio de backend es inofensivo — la señal sigue sin existir
        /// y lo único que se pierde es una línea de log.
        ///
        /// Se intenta UNA sola vez por sesión de captura: si el otro backend tampoco da señal, el
        /// problema no es el backend, y reintentar en bucle solo llenaría el log.
        /// </summary>
        private void ApplySilenceFallback()
        {
            if (_fallbackTried || !WinMmCapture.IsSupported) return;
            if (InputLevel > 0f) { _silentFrames = 0; return; }
            if (++_silentFrames < SilentFramesBeforeFallback) return;

            _fallbackTried = true;
            Debug.LogWarning($"[VoiceCapture] '{_activeDevice}' lleva {_silentFrames} tramas de " +
                             "silencio digital exacto; Microphone no puede negociar sus canales. " +
                             "Cambiando a captura WinMM.");

            // Dos canales por defecto: es la configuración de las interfaces de audio, que son
            // justo los aparatos donde `Microphone` falla. La mezcla a mono ya la hace
            // DownmixToMono con el modo "canal más fuerte", que resuelve el micro en UNA entrada.
            int channels = Mathf.Max(2, _channels);
            var winmm = new WinMmCapture();
            if (!winmm.Start(_activeDevice, SampleRate, channels))
            {
                winmm.Dispose();
                Debug.LogWarning("[VoiceCapture] La captura WinMM tampoco pudo abrir el dispositivo.");
                return;
            }

            if (!string.IsNullOrEmpty(_activeDevice)) Microphone.End(_activeDevice);
            _clip = null;

            _winmm = winmm;
            _captureRate = winmm.SampleRate;
            _channels = winmm.Channels;
            _needsResample = _captureRate != SampleRate;
            _inputFrameSamples = Mathf.Max(1, Mathf.RoundToInt(_captureRate / 50f));
            _capture = new float[_inputFrameSamples];
            _interleaved = new float[_inputFrameSamples * _channels];
            _winmmScratch = new short[_inputFrameSamples * _channels * 4];
            _winmmFill = 0;
            _resampleTail = 0f;
            _dynamics.Reset();
        }

        /// <summary>Drena el backend WinMM y procesa las tramas completas que salgan.</summary>
        private void PumpWinMm()
        {
            int need = _inputFrameSamples * _channels;
            int got = _winmm.Read(_winmmScratch, _winmmScratch.Length - _winmmFill);
            _winmmFill += got;

            while (_winmmFill >= need)
            {
                // 16 bits con signo a -1..1, tal como los entrega el driver: intercalados.
                for (int i = 0; i < need; i++)
                    _interleaved[i] = _winmmScratch[i] / (float)short.MaxValue;

                // Lo que sobra se desplaza al principio. Con 4 tramas de holgura esto copia unos
                // pocos cientos de muestras y solo cuando el driver entrega de más.
                _winmmFill -= need;
                if (_winmmFill > 0) Array.Copy(_winmmScratch, need, _winmmScratch, 0, _winmmFill);

                DownmixToMono();
                ProcessFrame();
            }
        }

        /// <summary>
        /// Pasa la trama intercalada del dispositivo a un canal.
        ///
        /// EXISTE POR UNA AVERÍA REAL, no por completitud: una interfaz de audio expone sus
        /// entradas como un dispositivo ESTÉREO ("Analogue 1 + 2"), y leer `L,R,L,R` como si
        /// fueran muestras seguidas da el doble de tono y, con el micrófono en una sola entrada,
        /// una de cada dos muestras a cero. No suena mal: no suena a nada. Un dispositivo virtual
        /// mono por delante lo tapaba, y por eso "funcionaba con Voicemod".
        ///
        /// El modo por defecto es MÁS FUERTE POR MUESTRA y no la media, y la diferencia importa
        /// justo en el caso que motivó esto: con el micro en la entrada 1 y la 2 en silencio, la
        /// media entrega la mitad de nivel, mientras que el máximo entrega el canal bueno intacto.
        /// Para un micrófono estéreo de verdad las dos opciones son equivalentes al oído.
        /// </summary>
        private void DownmixToMono()
        {
            int ch = _channels;
            if (ch <= 1)
            {
                System.Array.Copy(_interleaved, _capture, _inputFrameSamples);
                return;
            }

            int pick = _channel; // -1 = automático (más fuerte), -2 = mezcla, ≥0 = canal fijo
            for (int i = 0; i < _inputFrameSamples; i++)
            {
                int b = i * ch;
                float v;
                if (pick >= 0)
                {
                    v = _interleaved[b + Mathf.Min(pick, ch - 1)];
                }
                else if (pick == -2)
                {
                    float sum = 0f;
                    for (int c = 0; c < ch; c++) sum += _interleaved[b + c];
                    v = sum / ch;
                }
                else
                {
                    v = _interleaved[b];
                    for (int c = 1; c < ch; c++)
                    {
                        float s = _interleaved[b + c];
                        if (s < 0f ? -s > (v < 0f ? -v : v) : s > (v < 0f ? -v : v)) v = s;
                    }
                }
                _capture[i] = v;
            }
        }

        private void ProcessFrame()
        {
            // El nivel se mide SIEMPRE, hable o no y transmita o no: es el dato que separa "el
            // micrófono no capta nada" de "capta pero no sale". Sin él, las dos averías se ven
            // igual desde fuera. Se mide sobre la señal CAPTURADA, antes de remuestrear.
            double sum = 0;
            for (int i = 0; i < _inputFrameSamples; i++) sum += (double)_capture[i] * _capture[i];
            InputLevel = Mathf.Sqrt((float)(sum / _inputFrameSamples));

            ApplySilenceFallback();

            // Traza periódica del nivel. El medidor del panel ya lo enseña, pero un log es lo
            // único que sobrevive a la sesión y lo único que se puede pedir por escrito cuando
            // alguien reporta "no me oyen": separa "el dispositivo entrega silencio" de todo lo
            // demás sin tener que estar delante de la pantalla.
            if (InputLevel > _levelPeak) _levelPeak = InputLevel;
            _levelFrames++;
            if (Time.unscaledTime >= _nextLevelLog)
            {
                _nextLevelLog = Time.unscaledTime + 2f;
                Debug.Log($"[VoiceCapture] nivel pico={_levelPeak:F4} en {_levelFrames} tramas " +
                          $"device='{_activeDevice}' canales={_channels}" +
                          (_levelPeak < 0.0005f ? "  <-- EL DISPOSITIVO ENTREGA SILENCIO" : ""));
                _levelPeak = 0f;
                _levelFrames = 0;
            }

            ApplyGateAndGain();

            bool open = ShouldTransmit();
            IsTransmitting = open;
            if (!open)
            {
                // Descartar el paquete a medias: mandar media ráfaga al soltar la tecla haría que
                // el oyente reprodujera un fragmento suelto sin su continuación.
                _framesInPacket = 0;
                _packetBytes = 0;
                return;
            }

            FillPcmFromCapture();

            // El formato del datagrama (cabecera de longitud por trama) vive en VoicePacket, que
            // es el mismo código que usa el reproductor para deshacerlo.
            int offset = _packetBytes;
            int payloadStart = VoicePacket.PayloadOffset(_packet, offset);
            if (payloadStart < 0) { _framesInPacket = 0; _packetBytes = 0; return; }

            int room = _packet.Length - payloadStart;
            int written;
            try
            {
                written = _encoder.Encode(
                    new ReadOnlySpan<short>(_pcm),
                    FrameSamples,
                    new Span<byte>(_packet, payloadStart, room),
                    room);
            }
            catch (OpusException e)
            {
                Debug.LogWarning($"[VoiceCapture] Opus encode failed: {e.Message}");
                _framesInPacket = 0;
                _packetBytes = 0;
                return;
            }
            if (written <= 0) return;

            int end = VoicePacket.SealFrame(_packet, offset, written);
            if (end < 0) { _framesInPacket = 0; _packetBytes = 0; return; }

            // Auto-test: decodifica ESTA trama, la recién codificada, y la manda al monitor. Se
            // hace aquí y no sobre el datagrama entero para que lo que oyes sea exactamente lo que
            // sale del codificador, sin que el empaquetado pueda tapar un fallo suyo.
            if (_selfTest) MonitorFrame(payloadStart, written);

            _packetBytes = end;
            _framesInPacket++;

            if (_framesInPacket >= FramesPerPacket)
            {
                _ipc.SendVoice(_seq, _packet, _packetBytes);
                _seq++; // envuelve a propósito: el receptor compara diferencias, no absolutos
                _framesInPacket = 0;
                _packetBytes = 0;
            }
        }

        /// <summary>
        /// El ÚNICO camino para encender el micrófono hasta que exista pantalla de opciones. Va
        /// antes de la puerta de `_micEnabled` en <see cref="Update"/> a propósito: si estuviera
        /// después, la tecla de encendido no funcionaría precisamente cuando está apagado.
        ///
        /// Se loguea cada cambio porque en una build no hay inspector donde mirar el estado, y
        /// "no me oyen" es indistinguible de "tengo el micro cerrado" sin esa línea.
        /// </summary>
        private void PollHotkeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb[_toggleMicKey].wasPressedThisFrame)
            {
                MicEnabled = !MicEnabled;
                Debug.Log($"[VoiceCapture] Microfono {(MicEnabled ? "ENCENDIDO" : "APAGADO")} " +
                          $"({_toggleMicKey}); modo={(_openMic ? "voz abierta" : "push-to-talk")}" +
                          (MicEnabled && !_openMic ? $", manten {_pushToTalkKey} para hablar" : ""));
            }

            if (kb[_toggleModeKey].wasPressedThisFrame)
            {
                OpenMic = !OpenMic;
                Debug.Log($"[VoiceCapture] Modo = {(_openMic ? "VOZ ABIERTA" : "PUSH-TO-TALK")} ({_toggleModeKey})");
            }
        }

        /// <summary>
        /// Puerta de ruido y nivel automático. La lógica vive en <see cref="VoiceDynamics"/>, que
        /// es pura y por tanto EJECUTABLE en un arnés: el procesado de audio se rompe en silencio,
        /// así que tenerlo aquí dentro habría significado no poder afirmar nada sobre él.
        /// </summary>
        private void ApplyGateAndGain()
        {
            _dynamics.GateEnabled = _noiseGate;
            _dynamics.AutoGainEnabled = _autoGain;
            float dt = _inputFrameSamples / (float)_captureRate; // duración REAL de esta trama
            _dynamics.Process(_capture, _inputFrameSamples, InputLevel, dt,
                _activationThreshold, _releaseSeconds);
        }

        /// <summary>
        /// Pasa la trama capturada a PCM de 16 bits, remuestreando a <see cref="SampleRate"/> si
        /// el dispositivo captura a otra tasa.
        ///
        /// El camino común NO remuestrea: casi todo micrófono de Windows entrega 48 kHz, que es
        /// justo la tasa del códec, así que el `if` de abajo sale por la rama barata.
        ///
        /// Cuando sí hay que remuestrear, es interpolación lineal con **una muestra de historia
        /// entre tramas** (`_resampleTail`). Sin esa historia, cada trama empezaría interpolando
        /// contra un cero y se metería un chasquido cada 20 ms — 50 clics por segundo, que se oyen
        /// como un zumbido y no como lo que son. Lineal y no un filtro polifásico porque esto es
        /// la RAMA RARA: el aliasing que introduce está por encima de la banda de la voz y no
        /// merece pagar un filtro que casi nadie va a ejecutar.
        /// </summary>
        private void FillPcmFromCapture()
        {
            if (!_needsResample)
            {
                for (int i = 0; i < FrameSamples; i++)
                {
                    float s = _capture[i];
                    if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                    _pcm[i] = (short)(s * short.MaxValue);
                }
                return;
            }

            double ratio = (double)_inputFrameSamples / FrameSamples;
            for (int i = 0; i < FrameSamples; i++)
            {
                double src = i * ratio - 1.0; // -1: el índice 0 cae sobre la muestra de historia
                int i0 = (int)System.Math.Floor(src);
                float frac = (float)(src - i0);
                float a = SampleAt(i0);
                float b = SampleAt(i0 + 1);
                float s = a + (b - a) * frac;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                _pcm[i] = (short)(s * short.MaxValue);
            }
            _resampleTail = _capture[_inputFrameSamples - 1];
        }

        /// <summary>Índice -1 = la última muestra de la trama anterior; fuera del rango por arriba
        /// se satura a la última, que solo puede ocurrir en la muestra final por redondeo.</summary>
        private float SampleAt(int i)
        {
            if (i < 0) return _resampleTail;
            return i < _inputFrameSamples ? _capture[i] : _capture[_inputFrameSamples - 1];
        }

        private bool ShouldTransmit()
        {
            if (!_openMic)
            {
                var kb = Keyboard.current;
                // Input System: UnityEngine.Input.GetKey lanza con el input viejo deshabilitado.
                return kb != null && kb[_pushToTalkKey].isPressed;
            }

            // `InputLevel` ya lo calculó ProcessFrame para esta misma trama.
            if (InputLevel >= _activationThreshold) _holdUntil = Time.time + _releaseSeconds;
            return Time.time < _holdUntil;
        }

        // ── Auto-test: el lazo local micro → códec → altavoz ──────────────────────────
        //
        // Deliberadamente 2D y con su propio anillo, en vez de reutilizar el `Speaker` de
        // RemoteVoicePlayer: aquel cuelga de un proxy de peer y aquí no hay proxy que colgar.
        // La contrapartida está escrita en el doc de SelfTest — esto NO prueba el camino del
        // proxy ni la curva de distancia.

        private void MonitorFrame(int payloadStart, int written)
        {
            EnsureMonitor();
            if (_monitorSource == null) return;

            int samples;
            try
            {
                samples = _monitorDecoder.Decode(
                    new ReadOnlySpan<byte>(_packet, payloadStart, written),
                    new Span<short>(_monitorFrame), FrameSamples, false);
            }
            catch (OpusException e)
            {
                Debug.LogWarning($"[VoiceCapture] Auto-test: fallo al decodificar: {e.Message}");
                return;
            }
            if (samples <= 0) return;

            for (int i = 0; i < samples; i++)
            {
                _monitorRing[_monitorWrite] = _monitorFrame[i];
                _monitorWrite = _monitorWrite + 1 == _monitorRing.Length ? 0 : _monitorWrite + 1;
            }
            if (!_monitorSource.isPlaying) _monitorSource.Play();
        }

        private void EnsureMonitor()
        {
            if (_monitorSource != null) return;

            _monitorDecoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);
            _monitorFrame = new short[FrameSamples];
            _monitorRing = new short[FrameSamples * 25]; // ~0,5 s
            _monitorWrite = 0;
            _monitorRead = 0;

            var go = new GameObject("VoiceSelfTest");
            go.transform.SetParent(transform, false);
            _monitorSource = go.AddComponent<AudioSource>();
            _monitorSource.spatialBlend = 0f; // 2D: es TU voz, no viene de ningún sitio del mundo
            _monitorSource.loop = true;
            _monitorSource.playOnAwake = false;
            _monitorSource.clip = AudioClip.Create("VoiceSelfTest", SampleRate, 1, SampleRate,
                true, OnMonitorRead);
        }

        /// <summary>Hilo de AUDIO. Cero asignaciones, cero API de Unity.</summary>
        private void OnMonitorRead(float[] data)
        {
            int have = _monitorWrite - _monitorRead;
            if (have < 0) have += _monitorRing.Length;

            for (int i = 0; i < data.Length; i++)
            {
                if (have <= 0) { data[i] = 0f; continue; }
                data[i] = _monitorRing[_monitorRead] / (float)short.MaxValue;
                _monitorRead = _monitorRead + 1 == _monitorRing.Length ? 0 : _monitorRead + 1;
                have--;
            }
        }

        private void StopMonitor()
        {
            if (_monitorSource == null) return;
            _monitorSource.Stop();
            Destroy(_monitorSource.gameObject);
            _monitorSource = null;
            _monitorDecoder = null;
        }

        private void StartDevice()
        {
            if (_clip != null) return;
            if (Microphone.devices.Length == 0)
            {
                if (!_warnedNoDevice)
                {
                    _warnedNoDevice = true;
                    Debug.LogWarning("[VoiceCapture] No microphone device; voice stays off.");
                }
                return;
            }

            // Un dispositivo guardado que ya no existe (cascos desenchufados) no puede dejar la voz
            // muerta en silencio: se cae al primero disponible y se dice.
            _activeDevice = ResolveDevice();

            // NUNCA se rechaza un micrófono. La versión anterior exigía la tasa del códec y se
            // apagaba si no la tenía; medido en la máquina de desarrollo, **los 15 dispositivos
            // reportaban `48000-48000` y ninguno servía**. Ahora se captura a lo que el aparato
            // dé —lo más cerca posible de la tasa del códec— y se remuestrea si hace falta.
            _captureRate = PickCaptureRate(_activeDevice);
            _inputFrameSamples = Mathf.Max(1, Mathf.RoundToInt(_captureRate / 50f)); // 20 ms
            _needsResample = _captureRate != SampleRate;
            _capture = new float[_inputFrameSamples];
            _resampleTail = 0f;

            _encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _encoder.Bitrate = _bitrate;
            _encoder.Complexity = _complexity;
            _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            // FEC en banda + 10 % de pérdida declarada: el transporte es NO FIABLE a propósito
            // (ADR-039), así que el códec tiene que asumir que va a perder paquetes.
            _encoder.UseInbandFEC = true;
            _encoder.PacketLossPercent = 10;
            // DTX: en silencio el codificador emite tramas mínimas en vez de callar del todo, y
            // eso mantiene vivo el estado del decodificador del otro lado.
            _encoder.UseDTX = true;

            _clip = Microphone.Start(_activeDevice, true, 1, _captureRate);
            if (_clip == null)
            {
                Debug.LogWarning($"[VoiceCapture] Microphone.Start devolvio null para " +
                                 $"'{_activeDevice}' a {_captureRate} Hz. Revisa los permisos de " +
                                 "microfono de Windows para esta aplicacion.");
                return;
            }
            // El número de canales NO se puede saber antes de abrir: `GetDeviceCaps` solo informa
            // de frecuencias. Por eso se lee aquí y los buffers se dimensionan después.
            _channels = Mathf.Max(1, _clip.channels);
            _interleaved = new float[_inputFrameSamples * _channels];

            _readHead = 0;
            _framesInPacket = 0;
            _packetBytes = 0;
            Debug.Log($"[VoiceCapture] Mic ON device='{_activeDevice}' captura={_captureRate} Hz" +
                      (_needsResample ? $" -> remuestreo a {SampleRate} Hz" : " (sin remuestreo)") +
                      $", canales={_channels}" +
                      (_channels > 1 ? $" (mezcla={ChannelName(_channel)})" : "") +
                      $", {_inputFrameSamples} muestras/trama, " +
                      $"modo={(_openMic ? "voz abierta" : "push-to-talk " + _pushToTalkKey)}");
        }

        /// <summary>Dispositivo pedido si existe; si no, el primero. Nunca devuelve un nombre que
        /// el sistema ya no reconoce, porque `Microphone.Start` con uno así falla en silencio.</summary>
        private string ResolveDevice()
        {
            var devices = Microphone.devices;
            if (string.IsNullOrEmpty(_device)) return devices[0];
            foreach (var d in devices)
                if (d == _device) return d;

            Debug.LogWarning($"[VoiceCapture] El microfono guardado '{_device}' ya no existe; " +
                             $"usando '{devices[0]}'.");
            return devices[0];
        }

        /// <summary>
        /// Tasa de captura para un dispositivo: la del códec si entra en su rango, y si no la más
        /// cercana que admita.
        ///
        /// `0-0` significa "sin restricción" en la API de Unity, no "no soporta nada" — tratarlo
        /// como un rango real dejaría fuera a los dispositivos más permisivos, que son justo los
        /// que no dan problema.
        /// </summary>
        public static int PickCaptureRate(string device)
        {
            Microphone.GetDeviceCaps(device, out int minFreq, out int maxFreq);
            if (minFreq == 0 && maxFreq == 0) return SampleRate;
            if (maxFreq <= 0) return SampleRate;
            return Mathf.Clamp(SampleRate, Mathf.Max(1, minFreq), maxFreq);
        }

        private void StopDevice()
        {
            IsTransmitting = false;
            InputLevel = 0f;
            // Sin este reset, la ganancia adaptada al micrófono ANTERIOR se aplicaría a la primera
            // trama del siguiente, que con un salto de sensibilidad entre aparatos satura.
            _dynamics.Reset();
            if (_winmm != null) { _winmm.Dispose(); _winmm = null; }
            _winmmFill = 0;
            _silentFrames = 0;
            _fallbackTried = false;
            StopMonitor();
            if (_clip == null) return;
            if (!string.IsNullOrEmpty(_activeDevice)) Microphone.End(_activeDevice);
            _clip = null;
            _encoder = null; // sin Dispose: IOpusEncoder no es IDisposable en 2.2.2
            _framesInPacket = 0;
            _packetBytes = 0;
        }
    }
}
