using System;
using System.Collections.Generic;
using BackroomsSurvival.Net;
using Concentus;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-046 Fase 4 — reproduce la voz de cada peer en SU proxy, en 3D.
    ///
    /// Drena la cola de <see cref="IPCClient.PeerVoice"/>, decodifica con Opus y alimenta un
    /// <see cref="AudioSource"/> colgado del proxy que ya gestiona <see cref="RemotePlayerManager"/>.
    /// El backend ya ha filtrado por distancia (el host es la autoridad), así que aquí no se
    /// vuelve a decidir QUIÉN se oye: solo CÓMO de fuerte, que es geometría local.
    ///
    /// LA CURVA ES OBLIGATORIA Y ES EL PUNTO QUE MÁS FÁCIL SE ROMPE. Con el rolloff logarítmico
    /// por defecto de Unity, <c>maxDistance</c> NO silencia: la atenuación PARA ahí y se queda
    /// clavada en <c>minDistance/maxDistance</c> para siempre, así que la voz de alguien lejísimos
    /// te seguiría por todo el nivel a volumen constante — la negación literal de un chat de
    /// proximidad. <see cref="ProxyAudioCurves.ApplyHardCutoff"/> existe justamente porque esto ya
    /// pasó con los pasos (ADR-042).
    ///
    /// SOLO LEE la vista de <see cref="RemotePlayerManager"/>, como los ocho hooks del proxy, y
    /// por eso se puede borrar este archivo y el resto sigue funcionando.
    ///
    /// VIVE AQUÍ Y NO EN <c>Assets/Scripts</c> por una restricción de ensamblados, no por gusto:
    /// <see cref="ProxyAudioCurves"/> es de Assembly-CSharp y un asmdef (BackroomsSurvival) NO
    /// puede referenciar Assembly-CSharp. Duplicar la curva estaba descartado — es justamente lo
    /// que ADR-046 obliga a reutilizar. Por lo mismo se auto-registra en vez de pasar por
    /// GameBootstrap, que tampoco puede verlo desde el otro lado de la frontera; mismo patrón que
    /// <see cref="CorpseSpawner"/>, en este mismo directorio.
    /// </summary>
    public sealed class RemoteVoicePlayer : MonoBehaviour
    {
        private static RemoteVoicePlayer _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;
            var go = new GameObject("[RemoteVoicePlayer]");
            _instance = go.AddComponent<RemoteVoicePlayer>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
        }
        [Header("Distancia")]
        [Tooltip("Radio de volumen pleno.")]
        [SerializeField, Min(0.1f)] private float _minDistance = 3f;

        [Tooltip("Distancia a la que la voz es SILENCIO (no 'bajita'). Debe coincidir con " +
                 "VOICE_RADIUS_M del backend: más aquí y se oiría a quien el host ya no relaya; " +
                 "menos y se cortaría antes de lo que el host permite.")]
        [SerializeField, Min(1f)] private float _maxDistance = 25f;

        [Header("Jitter")]
        [Tooltip("Tramas de 20 ms que se acumulan antes de empezar a sonar. Sin colchón, la " +
                 "variación de latencia se oye como chasquidos; con demasiado, la conversación " +
                 "se vuelve incómoda.")]
        [Range(1, 10)]
        [SerializeField] private int _jitterFrames = 3;

        /// <summary>Tope del anillo por hablante. ~1 s a 50 tramas/s: pasado eso, quien está
        /// escuchando ya ha perdido el hilo y lo correcto es tirar lo viejo, no acumular retardo.</summary>
        private const int RingFrames = 50;

        private IPCClient _ipc;
        private RemotePlayerManager _manager;
        private readonly Dictionary<int, Speaker> _speakers = new Dictionary<int, Speaker>();
        private readonly HashSet<int> _muted = new HashSet<int>();
        private readonly List<int> _scratchIds = new List<int>();

        /// <summary>
        /// Silencia a un peer LOCALMENTE, solo para esta sesión. No se persiste a propósito: los
        /// ids de peer se reparten por orden de llegada desde un contador de proceso, así que
        /// guardar "silenciado el 3" silenciaría a un desconocido en la partida siguiente. Un mute
        /// duradero necesita la clave de identidad de ADR-045.
        /// </summary>
        public void SetMuted(int peerId, bool muted)
        {
            if (muted) _muted.Add(peerId);
            else _muted.Remove(peerId);
        }

        public bool IsMuted(int peerId) => _muted.Contains(peerId);

        private void OnEnable() => OpusCodecFactory.AttemptToUseNativeLibrary = false;

        private void Update()
        {
            if (_ipc == null && !IPCClient.TryGetInstance(out _ipc)) return;

            while (_ipc.PeerVoice.TryDequeue(out var msg))
            {
                if (msg == null || msg.data.Length == 0) continue;
                if (_muted.Contains(msg.peerId)) continue;
                GetSpeaker(msg.peerId)?.Push(msg);
            }

            // Retirar hablantes cuyo proxy ya no existe (se fue, murió, o salió del streaming).
            // Sin esto quedarían AudioSource huérfanos sonando en el origen del mundo.
            _scratchIds.Clear();
            foreach (var kv in _speakers)
                if (kv.Value.Root == null) _scratchIds.Add(kv.Key);
            foreach (int id in _scratchIds)
            {
                _speakers[id].Destroy();
                _speakers.Remove(id);
            }
        }

        private Speaker GetSpeaker(int peerId)
        {
            if (_speakers.TryGetValue(peerId, out var s)) return s;

            // Sin proxy no hay dónde colgar el audio. Se descarta en vez de inventar una fuente
            // en el aire: el peer está fuera del rango de streaming, o sea más lejos que la voz.
            // Cacheado: esto se pregunta por cada peer que empieza a hablar, y `FindObjectOfType`
            // (además de estar obsoleto en Unity 6) recorre la escena entera.
            if (_manager == null) _manager = FindFirstObjectByType<RemotePlayerManager>();
            var mgr = _manager;
            if (mgr == null) return null;
            if (!mgr.ActivePlayers.TryGetValue(peerId, out var view) || view?.root == null) return null;

            s = new Speaker(view.root, _minDistance, _maxDistance, _jitterFrames);
            _speakers[peerId] = s;
            return s;
        }

        private void OnDisable()
        {
            foreach (var s in _speakers.Values) s.Destroy();
            _speakers.Clear();
        }

        /// <summary>
        /// Un hablante: su decodificador, su anillo de PCM y su AudioSource en el proxy.
        ///
        /// El anillo se ESCRIBE desde el hilo principal (Update) y se LEE desde el HILO DE AUDIO
        /// (PCMReaderCallback). Por eso no asigna nada en la lectura, los índices son campos
        /// simples y el único estado compartido son dos enteros: el escritor solo avanza el suyo
        /// después de haber escrito las muestras, así que el lector nunca ve un hueco a medio
        /// llenar. Un lock aquí sería un lock en el hilo de audio, que es exactamente lo que
        /// produce chasquidos.
        /// </summary>
        private sealed class Speaker
        {
            internal Transform Root { get; }

            private readonly IOpusDecoder _decoder;
            private readonly AudioSource _source;
            private readonly GameObject _go;
            private readonly short[] _ring = new short[RingFrames * VoiceCapture.FrameSamples];
            private readonly short[] _decoded = new short[VoiceCapture.FrameSamples];
            private readonly int _jitterSamples;

            private int _write;
            private int _read;
            private bool _playing;
            private ushort _lastSeq;
            private bool _haveSeq;

            internal Speaker(Transform root, float minDistance, float maxDistance, int jitterFrames)
            {
                Root = root;
                _jitterSamples = jitterFrames * VoiceCapture.FrameSamples;
                _decoder = OpusCodecFactory.CreateDecoder(VoiceCapture.SampleRate, 1);

                _go = new GameObject("RemoteVoice");
                _go.transform.SetParent(root, false);
                _go.transform.localPosition = Vector3.up * 1.5f; // a la altura de la cabeza

                _source = _go.AddComponent<AudioSource>();
                _source.spatialBlend = 1f; // 3D puro: en 2D la voz sonaría igual desde cualquier sitio
                _source.loop = true;
                _source.playOnAwake = false;
                _source.dopplerLevel = 0f; // la voz de alguien corriendo no debe cambiar de tono
                ProxyAudioCurves.ApplyHardCutoff(_source, minDistance, maxDistance);
                _source.clip = AudioClip.Create("RemoteVoice", VoiceCapture.SampleRate, 1,
                    VoiceCapture.SampleRate, true, OnAudioRead);
            }

            internal void Push(PeerVoiceMsg msg)
            {
                // Hueco en la secuencia = tramas perdidas. Se le pide al decodificador que las
                // reconstruya (PLC) en vez de dejar un silencio abrupto. Tope de 3: un peer que
                // vuelve tras un corte no debe soltar segundos de relleno de golpe.
                if (_haveSeq)
                {
                    int gap = (ushort)(msg.seq - _lastSeq) - 1;
                    if (gap > 0)
                    {
                        if (gap > 3) gap = 3;
                        for (int i = 0; i < gap; i++) DecodeInto(null, 0, 0);
                    }
                    // Una trama que llega TARDE se tira: reproducirla fuera de orden suena peor
                    // que no reproducirla. (ushort)(a-b) envuelve, así que esto sigue siendo
                    // correcto al pasar de 65535 a 0.
                    else if ((ushort)(msg.seq - _lastSeq) > 32768) return;
                }
                _lastSeq = msg.seq;
                _haveSeq = true;

                int offset = 0;
                while (true)
                {
                    int len = VoicePacket.ReadFrameLength(msg.data, offset, msg.data.Length);
                    if (len < 0) break;
                    DecodeInto(msg.data, offset + VoicePacket.FrameHeaderBytes, len);
                    offset += VoicePacket.FrameHeaderBytes + len;
                }

                if (!_playing && Available() >= _jitterSamples)
                {
                    _playing = true;
                    _source.Play();
                }
            }

            /// <summary>Decodifica una trama al anillo. <paramref name="data"/> null = trama
            /// perdida, que en Opus se pide con un span vacío y produce ocultación.</summary>
            private void DecodeInto(byte[] data, int offset, int len)
            {
                int samples;
                try
                {
                    var input = data == null
                        ? default
                        : new ReadOnlySpan<byte>(data, offset, len);
                    samples = _decoder.Decode(input, new Span<short>(_decoded),
                        VoiceCapture.FrameSamples, false);
                }
                catch (OpusException)
                {
                    // Un datagrama corrupto no puede tumbar la voz de este peer para siempre.
                    return;
                }
                if (samples <= 0) return;

                for (int i = 0; i < samples; i++)
                {
                    _ring[_write] = _decoded[i];
                    _write = _write + 1 == _ring.Length ? 0 : _write + 1;
                }

                // Desbordar el anillo significa que el oyente va MUY por detrás. Se empuja la
                // lectura hasta el borde: preferimos un salto audible a una latencia creciente
                // que nunca se recupera.
                if (Available() < 0) _read = _write;
            }

            private int Available()
            {
                int a = _write - _read;
                return a < 0 ? a + _ring.Length : a;
            }

            /// <summary>Corre en el HILO DE AUDIO. Cero asignaciones, cero API de Unity.</summary>
            private void OnAudioRead(float[] data)
            {
                int have = Available();
                for (int i = 0; i < data.Length; i++)
                {
                    if (have <= 0)
                    {
                        // Sin datos = silencio, nunca basura del anillo. Un underrun se oye como
                        // un hueco; repetir muestras viejas se oye como un zumbido.
                        data[i] = 0f;
                        continue;
                    }
                    data[i] = _ring[_read] / (float)short.MaxValue;
                    _read = _read + 1 == _ring.Length ? 0 : _read + 1;
                    have--;
                }
            }

            internal void Destroy()
            {
                if (_source != null) _source.Stop();
                if (_go != null) UnityEngine.Object.Destroy(_go);
            }
        }
    }
}
