using PolymindGames; // AudioManager — dueño del AudioMixer del juego
using UnityEngine;
using UnityEngine.Audio;

namespace BackroomsSurvival.Gameplay.Audio
{
    /// <summary>
    /// Reverb de sala por zona, escrito sobre el efecto <c>SFX Reverb</c> del grupo
    /// <c>Master</c> del mixer.
    ///
    /// POR QUÉ EN EL BUS Y NO EN UNA AudioReverbZone. Una zona anclada al jugador produce
    /// reverb CONSTANTE: suena igual en un pasillo estrecho que en una nave, y eso delata el
    /// efecto en vez de describir el espacio. Aquí el reverb es una propiedad del sitio —
    /// lo dicta el <c>zone_kind</c> del chunk donde estás, igual que la niebla y la luz
    /// ambiental (ADR-066).
    ///
    /// POR QUÉ MASTER Y NO Ambience. La jerarquía del mixer es
    /// <c>Master → {Effects, Ambience}</c>, con <c>UI</c> colgando aparte de la raíz. El
    /// zumbido de las lámparas sale por Ambience y los pasos por Effects: son grupos
    /// HERMANOS, así que un reverb en cualquiera de los dos mojaría uno y dejaría el otro
    /// seco — y una sala en la que los pasos reverberan pero el zumbido no (o al revés)
    /// canta inmediatamente. Master es el único punto que los une, y por suerte no lleva la
    /// UI, así que los menús quedan secos sin hacer nada.
    ///
    /// DEGRADA EN SILENCIO SI EL MIXER NO ESTÁ PREPARADO. Los cinco parámetros tienen que
    /// estar EXPUESTOS en el asset del mixer con los nombres de abajo; Unity no permite
    /// añadir efectos ni exponer parámetros desde código, solo escribir los ya expuestos.
    /// Si faltan, <c>SetFloat</c> devuelve false y esto no hace nada — sin excepción y sin
    /// spam de log, que en este proyecto ya costó un editor.
    /// </summary>
    public sealed class ReverbMixerDriver : MonoBehaviour
    {
        // Nombres EXACTOS de los parámetros expuestos en FPS_AudioMixer. Cambiar uno aquí
        // sin cambiarlo en el asset apaga esa dimensión del reverb en silencio.
        public const string ParamDry    = "RvbDry";
        public const string ParamRoom   = "RvbRoom";
        public const string ParamRoomHF = "RvbRoomHF";
        public const string ParamDecay  = "RvbDecay";
        public const string ParamLevel  = "RvbLevel";

        /// <summary>
        /// Reverb apagado. −10000 dB es el suelo del SFX Reverb de Unity: silencio real, no
        /// "muy bajito". Es el estado de arranque y el de una capa que no autora nada, para
        /// que añadir este sistema no cambie cómo suena nada hasta que alguien lo autore.
        /// </summary>
        public const float RoomSilent = -10000f;

        /// <summary>
        /// Segundos de transición al cruzar de zona. Un salto instantáneo de cola es
        /// audible como corte; 1,5 s lo cruza por debajo del umbral sin que se perciba como
        /// desvanecido. Mismo espíritu que el fade de reasignación del zumbido, otra escala.
        /// </summary>
        private const float BlendSeconds = 1.5f;

        /// <summary>Los cinco mandos del SFX Reverb que este sistema gobierna.</summary>
        public struct RoomTone
        {
            public float dry;     // dB, −10000..0   — cuánto del seco pasa (0 = todo)
            public float room;    // dB, −10000..0   — presencia general del reverb
            public float roomHF;  // dB, −10000..0   — cuánto agudo sobrevive a la sala
            public float decay;   // s,  0.1..20     — largo de la cola
            public float level;   // dB, −10000..2000 — nivel de la cola tardía

            /// <summary>Sala muda: el estado de "aquí no hay reverb autorado".</summary>
            public static RoomTone Silent => new RoomTone
            {
                dry = 0f, room = RoomSilent, roomHF = 0f, decay = 1f, level = 0f,
            };
        }

        private static ReverbMixerDriver _instance;
        private static bool _quitting;

        private AudioMixer _mixer;
        private RoomTone   _current = RoomTone.Silent;
        private RoomTone   _target  = RoomTone.Silent;
        private bool       _hasTarget;
        private float      _mixerRetry;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _quitting = false;
            _instance = null;
        }

        /// <summary>
        /// Fija la sala hacia la que transicionar. Idempotente: llamarlo cada frame con el
        /// mismo tono no reinicia la mezcla, así que el llamante no necesita detectar
        /// cambios — eso lo hace el propio driver comparando con el objetivo vigente.
        /// </summary>
        public static void SetRoom(RoomTone tone)
        {
            var d = Ensure();
            if (d == null) return;
            d._target    = tone;
            d._hasTarget = true;
        }

        /// <summary>Devuelve el reverb a silencio (menús, o capa sin autoría).</summary>
        public static void Silence() => SetRoom(RoomTone.Silent);

        // Auto-arranque perezoso en la escena activa, igual que FluorescentHumDirector: solo
        // existe cuando alguien pide una sala, y muere con la escena.
        private static ReverbMixerDriver Ensure()
        {
            if (_instance != null) return _instance;
            if (_quitting) return null;
            var go = new GameObject("ReverbMixerDriver");
            _instance = go.AddComponent<ReverbMixerDriver>();
            return _instance;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(this); return; }
            _instance = this;
            ResolveMixer();
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            // Dejar el bus como se encontró: el mixer es un ASSET y sus valores persisten
            // entre sesiones de Play en el editor. Sin esto, salir del Play con una nave
            // sonando deja el menú principal reverberando hasta que alguien lo note.
            Write(RoomTone.Silent);
            _instance = null;
        }

        private void OnApplicationQuit() => _quitting = true;

        private void ResolveMixer()
        {
            if (_mixer != null) return;
            var mgr = AudioManager.Instance;
            if (mgr != null) _mixer = mgr.AudioMixer;
        }

        private void Update()
        {
            if (!_hasTarget) return;

            if (_mixer == null)
            {
                // AudioManager se auto-crea por RuntimeInitializeOnLoadMethod, pero depender
                // del orden sería frágil: se reintenta barato hasta que aparezca.
                _mixerRetry -= Time.unscaledDeltaTime;
                if (_mixerRetry > 0f) return;
                _mixerRetry = 0.5f;
                ResolveMixer();
                if (_mixer == null) return;
            }

            // unscaledDeltaTime: el audio no se detiene con timeScale 0, y una mezcla
            // congelada a medias dejaría media cola puesta durante toda la pausa.
            float t = Mathf.Clamp01(Time.unscaledDeltaTime / BlendSeconds);
            _current.dry    = Mathf.Lerp(_current.dry,    _target.dry,    t);
            _current.room   = Mathf.Lerp(_current.room,   _target.room,   t);
            _current.roomHF = Mathf.Lerp(_current.roomHF, _target.roomHF, t);
            _current.decay  = Mathf.Lerp(_current.decay,  _target.decay,  t);
            _current.level  = Mathf.Lerp(_current.level,  _target.level,  t);

            Write(_current);
        }

        // SetFloat devuelve false si el parámetro no está expuesto en el asset. Se ignora a
        // propósito: el sistema tiene que poder existir antes de que el mixer esté autorado.
        private void Write(RoomTone t)
        {
            if (_mixer == null) return;
            _mixer.SetFloat(ParamDry,    t.dry);
            _mixer.SetFloat(ParamRoom,   t.room);
            _mixer.SetFloat(ParamRoomHF, t.roomHF);
            _mixer.SetFloat(ParamDecay,  t.decay);
            _mixer.SetFloat(ParamLevel,  t.level);
        }

        /// <summary>
        /// True si el mixer tiene los cinco parámetros expuestos. Solo para diagnóstico y
        /// para el test de contrato — el camino normal no pregunta, escribe y deja que
        /// SetFloat falle en silencio.
        /// </summary>
        public static bool MixerIsAuthored()
        {
            var mgr = AudioManager.Instance;
            var mix = mgr != null ? mgr.AudioMixer : null;
            if (mix == null) return false;
            return mix.GetFloat(ParamDry,    out _) && mix.GetFloat(ParamRoom,  out _) &&
                   mix.GetFloat(ParamRoomHF, out _) && mix.GetFloat(ParamDecay, out _) &&
                   mix.GetFloat(ParamLevel,  out _);
        }
    }
}
