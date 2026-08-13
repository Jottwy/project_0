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
    /// SetFloat NO FALLA EN SILENCIO — este archivo lo daba por hecho y era FALSO. Unity
    /// emite <c>"Exposed name does not exist: X"</c> CON STACK TRACE en cada llamada con un
    /// nombre que el mixer no conoce. Con siete escrituras por frame eso produjo 229.725
    /// errores y un Editor.log de 386 MB en una sola sesión, que es la misma mecánica que ya
    /// tumbó el editor hoy. Por eso <see cref="ResolveParamNames"/> descubre los nombres UNA
    /// vez con <c>GetFloat</c> (ese sí es mudo) y, si falta alguno, el driver se declara
    /// MUDO y deja de escribir del todo. La degradación silenciosa hay que construirla; no
    /// viene de fábrica.
    ///
    /// Unity no permite añadir efectos ni exponer parámetros desde código, solo escribir los
    /// ya expuestos: el efecto y sus siete parámetros se autoran en el asset a mano
    /// (docs/systems/reverb-mixer.md).
    /// </summary>
    public sealed class ReverbMixerDriver : MonoBehaviour
    {
        // Nombres EXACTOS de los parámetros expuestos en FPS_AudioMixer. Cambiar uno aquí
        // sin cambiarlo en el asset apaga esa dimensión del reverb en silencio.
        public const string ParamDry          = "RvbDry";
        public const string ParamRoom         = "RvbRoom";
        public const string ParamRoomHF       = "RvbRoomHF";
        public const string ParamDecay        = "RvbDecay";
        public const string ParamLevel        = "RvbLevel";
        public const string ParamReflect      = "RvbReflect";
        public const string ParamReflectDelay = "RvbReflectDelay";

        /// <summary>
        /// Reverb apagado. −10000 es el suelo del SFX Reverb de Unity: silencio real, no
        /// "muy bajito". Es el estado de arranque y el de una capa que no autora nada, para
        /// que añadir este sistema no cambie cómo suena nada hasta que alguien lo autore.
        ///
        /// LA UNIDAD ES mB (MILIBELIOS), NO dB — es la trampa de este efecto y el Inspector
        /// lo dice en pequeño: <c>100 mB = 1 dB</c>, así que el suelo son −100 dB y un
        /// reverb discreto está en torno a −2000 (= −20 dB), no en −20. Autorar pensando en
        /// dB deja valores 100 veces cortos, o sea reverb a todo trapo donde se quería un
        /// susurro. Solo <c>decay</c> escapa: ese sí va en segundos.
        /// </summary>
        public const float RoomSilent = -10000f;

        /// <summary>
        /// CONSTANTE DE TIEMPO de la mezcla al cruzar de zona, no la duración total: el
        /// suavizado es exponencial, así que en <c>BlendTau</c> segundos se recorre el 63 %
        /// del camino y en 3× el 95 %. El comentario anterior decía "transición en 1,5 s" y
        /// era falso — se documenta bien porque de aquí sale cuánto tarda de verdad en
        /// asentarse una sala, y con 1,5 s son ~4,5 s hasta que deja de moverse.
        ///
        /// Un salto instantáneo de cola se oye como un corte; esto lo cruza por debajo del
        /// umbral. Mismo espíritu que el fade de reasignación del zumbido, otra escala.
        /// </summary>
        private const float BlendTau = 1.5f;

        /// <summary>
        /// Un suavizado exponencial es ASINTÓTICO: nunca llega al objetivo. Por debajo de
        /// este resto se engancha de golpe, para que el estado quede exacto y las escrituras
        /// al mixer paren en vez de perseguir la última milésima para siempre.
        /// </summary>
        private const float SnapEpsilon = 0.5f;

        /// <summary>
        /// Los siete mandos del SFX Reverb que este sistema gobierna.
        ///
        /// <see cref="reflect"/> Y <see cref="reflectDelay"/> SON LOS QUE DECIDEN SI UN SITIO
        /// SUENA A SITIO. Las reflexiones tempranas son los primeros rebotes: lo que te dice
        /// que hay una pared a dos metros. Con <c>reflect</c> mudo solo queda la cola difusa,
        /// y eso NO se percibe como una habitación sino como un efecto de reverb encima del
        /// sonido — un vacío irreal, sin superficies.
        ///
        /// Que eso sea un fallo o el objetivo DEPENDE DE LA ZONA, y por eso son autorables en
        /// vez de constantes: en un pasillo o una oficina el vacío delata el efecto, pero en
        /// BLACKOUT y en el PIT es exactamente la sensación que se busca — validado en juego.
        /// </summary>
        public struct RoomTone
        {
            public float dry;          // mB, −10000..0    — cuánto del seco pasa (0 = todo)
            public float room;         // mB, −10000..0    — presencia general del reverb
            public float roomHF;       // mB, −10000..0    — cuánto agudo sobrevive a la sala
            public float decay;        // s,  0.1..20      — largo de la cola
            public float level;        // mB, −10000..2000 — nivel de la cola tardía
            public float reflect;      // mB, −10000..1000 — rebotes tempranos (−10000 = vacío)
            public float reflectDelay; // s,  0..0.3       — a qué distancia está la pared

            /// <summary>Sala muda: el estado de "aquí no hay reverb autorado".</summary>
            public static RoomTone Silent => new RoomTone
            {
                dry = 0f, room = RoomSilent, roomHF = 0f, decay = 1f, level = 0f,
                reflect = RoomSilent, reflectDelay = 0.02f,
            };
        }

        /// <summary>
        /// Distancia a la pared más cercana → retardo del primer rebote, en segundos.
        /// El sonido va a ~343 m/s y recorre IDA Y VUELTA, de ahí el ×2. Existe para que
        /// autorar una zona sea decir "las paredes están a 2,5 m" en vez de adivinar un
        /// número en milisegundos.
        /// </summary>
        public static float ReflectDelayForMetres(float metres) =>
            Mathf.Clamp(metres * 2f / 343f, 0f, 0.3f);

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
        /// <param name="zoneKind">
        /// Solo para el acuse. Va como argumento y no se deduce aquí porque distinguir
        /// "la zona es −1" de "la zona es 0 pero el asset no trae sus valores" es
        /// exactamente lo que costó una ronda de diagnóstico: los dos casos producen los
        /// mismos números y sin el zone_kind no se pueden separar.
        /// </param>
        public static void SetRoom(RoomTone tone, int zoneKind = int.MinValue)
        {
            var d = Ensure();
            if (d == null) return;
            bool changed = !d._hasTarget || !Same(d._target, tone) || zoneKind != d._zone;
            d._target    = tone;
            d._zone      = zoneKind;
            d._hasTarget = true;
            if (changed) d._announcePending = true;
        }

        private int _zone = int.MinValue;

        private static bool Same(RoomTone a, RoomTone b) =>
            a.dry == b.dry && a.room == b.room && a.roomHF == b.roomHF &&
            a.decay == b.decay && a.level == b.level &&
            a.reflect == b.reflect && a.reflectDelay == b.reflectDelay;

        /// <summary>
        /// Acuse de sala: una línea cuando el objetivo CAMBIA, y los valores no salen de lo
        /// que creemos haber escrito sino de <c>GetFloat</c> sobre el mixer — o sea, lo que
        /// el efecto tiene de verdad. Es la diferencia entre "el código pidió esto" y "el
        /// reverb suena así", que es justo donde se escondió el fallo del zumbido durante
        /// cuatro rondas. Cero salida mientras no se cruce de zona.
        /// </summary>
        private void AnnounceRoom()
        {
            if (!_announcePending || _mixer == null || _muted || _names == null) return;
            _announcePending = false;
            // Detrás del mismo flag que el espejo del backend: durante el afinado de
            // salas es la ÚNICA forma de saber qué zona estás pisando —no hay HUD que
            // lo diga— pero fuera de eso es ruido, y el ruido en el log ya costó un
            // editor. Se enciende con BACKROOMS_VERBOSE_LOG=1.
            if (!Net.NetworkInitializer.VerboseBackendLog) return;

            float room, decay, refl, delay, dry;
            bool ok = _mixer.GetFloat(_names[1], out room)
                    & _mixer.GetFloat(_names[3], out decay)
                    & _mixer.GetFloat(_names[5], out refl)
                    & _mixer.GetFloat(_names[6], out delay)
                    & _mixer.GetFloat(_names[0], out dry);

            if (!ok)
            {
                Debug.LogWarning("[Reverb] el mixer NO devuelve los parámetros: reverb mudo. " +
                                 "Ver docs/systems/reverb-mixer.md.");
                return;
            }
            string z = _zone == int.MinValue ? "?" : (_zone < 0 ? "DESCONOCIDA" : _zone.ToString());
            Debug.Log($"[Reverb] zona={z}  pedido room={_target.room:F0} decay={_target.decay:F2} " +
                      $"reflect={_target.reflect:F0} delay={_target.reflectDelay:F4} " +
                      $"dry={_target.dry:F0}  |  EN EL MIXER room={room:F0} decay={decay:F2} " +
                      $"reflect={refl:F0} delay={delay:F4} dry={dry:F0}");
        }

        private bool _announcePending;

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
            ResolveParamNames();
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
            float t = Mathf.Clamp01(Time.unscaledDeltaTime / BlendTau);

            // El objetivo efectivo es la sala de la ZONA teñida por el aislamiento actual.
            // Se recalcula cada pasada porque el aislamiento deriva de forma continua,
            // mientras que _target solo cambia al cruzar de zona.
            var goal = IsolationDirector.Colour(_target);

            bool moved = false;
            Approach(ref _current.dry,          goal.dry,          t, ref moved);
            Approach(ref _current.room,         goal.room,         t, ref moved);
            Approach(ref _current.roomHF,       goal.roomHF,       t, ref moved);
            Approach(ref _current.level,        goal.level,        t, ref moved);
            Approach(ref _current.reflect,      goal.reflect,      t, ref moved);
            // decay y reflectDelay viven en segundos, no en mB: su epsilon es otro y hay que
            // escalarlo, o "medio milibelio" sería un abismo en una cola de 0,6 s.
            Approach(ref _current.decay,        goal.decay,        t, ref moved, 1e-4f);
            Approach(ref _current.reflectDelay, goal.reflectDelay, t, ref moved, 1e-5f);

            // Sin movimiento no se reescribe: una vez asentada la sala, el driver deja de
            // tocar el mixer hasta el siguiente cambio de zona.
            if (moved) Write(_current);
            else AnnounceRoom(); // ya asentado: el mixer tiene el valor final, no uno a medias
        }

        private static void Approach(ref float current, float target, float t, ref bool moved,
            float epsilon = SnapEpsilon)
        {
            if (Mathf.Abs(target - current) <= epsilon)
            {
                if (current == target) return;
                current = target;   // engancha: el exponencial nunca llegaría solo
                moved   = true;
                return;
            }
            current = Mathf.Lerp(current, target, t);
            moved   = true;
        }

        // SetFloat devuelve false si el parámetro no está expuesto en el asset. Se ignora a
        // propósito: el sistema tiene que poder existir antes de que el mixer esté autorado.
        /// <summary>
        /// Nombres alternativos por mando, en orden de preferencia. Unity bautiza los
        /// parámetros expuestos como <c>MyExposedParam N</c> y hay que renombrarlos A MANO en
        /// el editor; renombrarlos editando el YAML del <c>.mixer</c> NO basta —el runtime
        /// siguió usando los viejos— y eso dejó el sistema escribiendo nombres inexistentes.
        /// Probar candidatos hace que funcione con o sin renombrado, y sobrevive a que el
        /// vendor pise el asset y Unity los vuelva a numerar.
        ///
        /// El sufijo numérico sale del ORDEN en que se expusieron: Dry, Room, Room HF, Decay,
        /// Reverb. Es un respaldo, no un contrato — por eso los nombres propios van primero.
        /// </summary>
        private static readonly string[][] ParamCandidates =
        {
            new[] { ParamDry,          "MyExposedParam"   },
            new[] { ParamRoom,         "MyExposedParam 1" },
            new[] { ParamRoomHF,       "MyExposedParam 2" },
            new[] { ParamDecay,        "MyExposedParam 3" },
            new[] { ParamLevel,        "MyExposedParam 4" },
            new[] { ParamReflect       },  // añadidos después: sin variante numerada
            new[] { ParamReflectDelay  },
        };

        // Nombre que de verdad responde para cada mando, o null si ninguno. Resuelto una vez.
        private string[] _names;
        private bool _muted;

        /// <summary>
        /// Descubre qué nombre acepta el mixer para cada mando. Si falta alguno el sistema
        /// se declara MUDO y deja de escribir.
        ///
        /// QUE DEJE DE ESCRIBIR ES EL PUNTO, no un detalle: <c>SetFloat</c> sobre un nombre
        /// inexistente NO falla en silencio como este archivo daba por hecho — Unity emite un
        /// error CON STACK TRACE en cada llamada. Con el driver escribiendo siete por frame
        /// eso dejó 229.725 errores y un Editor.log de 386 MB, que es exactamente la
        /// mecánica que ya tumbó el editor una vez hoy.
        /// </summary>
        private void ResolveParamNames()
        {
            if (_names != null || _mixer == null) return;
            _names = new string[ParamCandidates.Length];
            int missing = 0;

            for (int i = 0; i < ParamCandidates.Length; i++)
            {
                foreach (var candidate in ParamCandidates[i])
                {
                    // GetFloat es la sonda barata: a diferencia de SetFloat, un nombre
                    // desconocido devuelve false sin escribir nada en el log.
                    if (!_mixer.GetFloat(candidate, out _)) continue;
                    _names[i] = candidate;
                    break;
                }
                if (_names[i] == null) missing++;
            }

            if (missing == 0) return;
            _muted = true;
            Debug.LogWarning(
                $"[Reverb] el mixer no expone {missing} de {ParamCandidates.Length} " +
                "parámetros: reverb MUDO y sin escrituras (evita inundar el log). Suele " +
                "bastar con reimportar FPS_AudioMixer; si no, ver docs/systems/reverb-mixer.md.");
        }

        private void Write(RoomTone t)
        {
            if (_mixer == null || _muted || _names == null) return;
            Set(0, t.dry);    Set(1, t.room);  Set(2, t.roomHF); Set(3, t.decay);
            Set(4, t.level);  Set(5, t.reflect); Set(6, t.reflectDelay);
        }

        private void Set(int slot, float value)
        {
            var n = _names[slot];
            if (n != null) _mixer.SetFloat(n, value);
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
            return mix.GetFloat(ParamDry,     out _) && mix.GetFloat(ParamRoom,    out _) &&
                   mix.GetFloat(ParamRoomHF,  out _) && mix.GetFloat(ParamDecay,   out _) &&
                   mix.GetFloat(ParamLevel,   out _) && mix.GetFloat(ParamReflect, out _) &&
                   mix.GetFloat(ParamReflectDelay, out _);
        }
    }
}
