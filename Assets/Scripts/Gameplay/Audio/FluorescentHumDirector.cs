using System;
using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using PolymindGames; // AudioManager / AudioChannel — el mixer del juego
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Audio
{
    /// <summary>
    /// Primer sistema de audio ambiental del proyecto — zumbido de fluorescente
    /// espacializado, con presupuesto FIJO de <see cref="SourceBudget"/> AudioSource para
    /// todo el mundo.
    ///
    /// POR QUÉ ASÍ Y NO UNA FUENTE POR LÁMPARA. Eso ya se intentó y se apagó: el
    /// <c>Awake</c> de <see cref="FluorescentAudio"/> está comentado entero con el motivo
    /// escrito ("cientos de AudioSource simultáneos, uno por lámpara × muchas lámparas ×
    /// muchos chunks"). Con densidad 1.0 en ZONE_NORMAL un chunk son ~100 lámparas y hay
    /// decenas de chunks cargados; el presupuesto no es una optimización posterior, es la
    /// restricción de la que sale el diseño. Las lámparas fuera del presupuesto NO suenan:
    /// su contribución la cubrirá el drone de fondo, que no existe todavía.
    ///
    /// CERO AudioSource HUÉRFANAS POR CONSTRUCCIÓN. Los AudioSource viven SOLO en hijos de
    /// este director y nunca bajo un chunk, así que el <c>Destroy(chunkRoot)</c> del
    /// streamer no puede dejar ninguna colgando — que es el patrón que produce fugas. Lo
    /// que se registra por chunk es un LOTE de datos (posiciones + pitch), y el lote se
    /// retira solo cuando su <c>root</c> pasa a null.
    ///
    /// NO TOCA EL AudioListener, y esto es deliberado. El director viejo
    /// (<see cref="BackroomsAudioSystem"/>) sigue desconectado justamente porque su
    /// <c>ConsolidateListener</c> remonta el listener en la raíz del jugador y destruye el
    /// de la cámara: la raíz no sigue el pitch de cámara, así que la panorámica quedaba
    /// mal orientada. Aquí el listener se LEE y nada más.
    ///
    /// Fuera de alcance por decisión: drone de fondo, reverb de pasillo, pasos, y el
    /// sonido del flicker. Puramente local y cosmético — no cruza el wire.
    /// </summary>
    public sealed class FluorescentHumDirector : MonoBehaviour
    {
        /// <summary>Fuentes simultáneas. El presupuesto entero del sistema.</summary>
        public const int SourceBudget = 8;

        /// <summary>"Este hueco no está sonando" / "esta lámpara no tiene identidad".</summary>
        public const long NoKey = -1L;

        // Baldosa de 5 m: con 7 m de alcance la lámpara vecina ya se oye débil mientras
        // estás bajo la tuya, así que al caminar el zumbido PASA de un panel al siguiente
        // en vez de cortar. Bajarlo a 5 deja huecos audibles entre paneles a densidad baja.
        private const float MinDistance    = 1f;
        private const float MaxDistance    = 7f;
        private const float MaxDistanceSqr = MaxDistance * MaxDistance;

        /// <summary>
        /// Margen en METROS que un aspirante debe sacarle al peor titular para robarle el
        /// hueco. Sin él, dos lámparas casi equidistantes se turnan la fuente cada 0,25 s y
        /// el zumbido parpadea de sitio mientras el jugador está quieto en el umbral.
        /// </summary>
        private const float HysteresisMetres = 1.5f;

        // Reasignar 4 veces por segundo basta: a velocidad de marcha (~4 m/s) el jugador
        // recorre 1 m entre pasadas, muy por debajo de la histéresis.
        private const float ReassignInterval = 0.25f;

        // Fade al reasignar. Mover una fuente que está sonando a otra lámpara de golpe da
        // un chasquido; 0,12 s lo tapa sin que se perciba como desvanecido.
        private const float RetargetFadeSeconds = 0.12f;

        /// <summary>
        /// Desviación máxima de pitch por lámpara (±2,5 %). Si todas suenan idénticas y en
        /// fase el resultado es un zumbido uniforme y artificial en vez de un espacio con
        /// muchas fuentes. Más de ~4 % y el detune se oye como avería, no como variación.
        /// </summary>
        public const float PitchSpread = 0.025f;

        // Sal del hash de pitch ("HMPT"). Propia, para no correlacionar el pitch con el
        // tinte por tile ni con el jitter de densidad de lámparas.
        private const uint PitchSalt = 0x484D5054U;

        // Bits de índice de lámpara dentro de la clave. Un chunk son 10×10 tiles ⇒ ≤100
        // lámparas; 12 bits (4096) sobra y deja el id de lote en los 52 restantes de long,
        // que no se agota en ninguna sesión concebible.
        private const int LampIndexBits = 12;
        private const int LampIndexMask = (1 << LampIndexBits) - 1;

        /// <summary>
        /// Volumen global del zumbido, encima del que autora la capa/zona. Parametrizable
        /// en runtime (consola de depuración, opciones futuras); 0 apaga el sistema entero
        /// sin desmontarlo.
        /// </summary>
        public static float MasterVolume
        {
            get => _masterVolume;
            set => _masterVolume = Mathf.Clamp(value, 0f, 4f);
        }
        private static float _masterVolume = 1f;

        /// <summary>
        /// Ruta bajo Resources de un clip AUTORADO que sustituye al sintetizado. Si no
        /// existe se sintetiza (ver <see cref="RenderHumSamples"/>), así que meter un .wav
        /// real de fluorescente es soltar el archivo ahí — cero código. El clip cargado se
        /// verifica MONO: un estéreo no se espacializa en Unity y es el error clásico de
        /// este sistema, así que se rechaza con aviso en vez de sonar plano en silencio.
        /// </summary>
        public const string AuthoredClipResource = "Audio/FluorescentHum";

        // ── Registro de lámparas ────────────────────────────────────────────────

        /// <summary>
        /// Un chunk de lámparas encendidas. Struct + arrays en vez de un componente por
        /// lámpara: añadir un MonoBehaviour a cada luminaria multiplicaría el coste de
        /// AddComponent del streaming por nada — el director no necesita que la lámpara
        /// tenga comportamiento, solo dónde está.
        /// </summary>
        private struct LampBatch
        {
            public Transform root;      // raíz del chunk; null (destruida) ⇒ lote retirado
            public int       layer;     // capa macro, para aislar verticalmente
            public Vector3[] positions; // MUNDO, muestreadas al registrar
            public float[]   pitches;
            public float     volume;    // volumen autorado de la capa/zona de ESTE chunk
            public int       count;
            public int       id;        // monótono: una clave vieja no puede aliasar un lote nuevo
        }

        private readonly List<LampBatch> _batches = new List<LampBatch>();
        private int _nextBatchId;

        // ── Estado del pool ─────────────────────────────────────────────────────

        private sealed class PoolSlot
        {
            public AudioSource src;
            public Transform   tr;
            public long        liveKey = NoKey; // lo que la fuente está reproduciendo AHORA
            public float       baseVolume;
            public float       envelope;        // 0..1, el fade de reasignación
        }

        private readonly PoolSlot[]     _slots     = new PoolSlot[SourceBudget];
        private readonly HumSlotState[] _selection = new HumSlotState[SourceBudget];

        // Reusados cada pasada para no asignar: la lista queda en ~15 elementos tras el
        // culling por alcance, así que ambos se estabilizan en su capacidad enseguida.
        private readonly List<HumCandidate>       _candidates = new List<HumCandidate>();
        private readonly Dictionary<long, LampRef> _refs      = new Dictionary<long, LampRef>();

        private struct LampRef
        {
            public Vector3 position;
            public float   pitch;
            public float   volume;
        }

        private Transform _listener;
        private float     _listenerRetry;
        private float     _refreshTimer;

        private static AudioClip                _sharedClip;
        private static FluorescentHumDirector   _instance;
        private static bool                     _quitting;

        // ── Alta desde el worldgen ──────────────────────────────────────────────

        /// <summary>
        /// Da de alta las lámparas ENCENDIDAS de un chunk. Llamado por
        /// <c>BackroomsLighting.PlaceFluorescentLights</c> una vez por chunk, con las
        /// posiciones ya en espacio de mundo. Las listas son del llamante y se COPIAN — no
        /// se retienen.
        ///
        /// El lote muere cuando <paramref name="chunkRoot"/> se destruye; no hace falta
        /// darlo de baja explícitamente, que es justo lo que hace imposible la fuga.
        ///
        /// Las lámparas rotas no se registran: un tubo apagado no zumba.
        /// </summary>
        public static void RegisterChunkLamps(Transform chunkRoot, int worldLayer,
            List<Vector3> worldPositions, List<float> pitches, float humVolume)
        {
            if (chunkRoot == null || worldPositions == null || worldPositions.Count == 0) return;
            if (humVolume <= 0f) return; // zona sin zumbido autorado — ni se registra

            var director = EnsureInstance();
            if (director == null) return;

            int n = worldPositions.Count;
            var pos = new Vector3[n];
            var pit = new float[n];
            for (int i = 0; i < n; i++)
            {
                pos[i] = worldPositions[i];
                pit[i] = (pitches != null && i < pitches.Count) ? pitches[i] : 1f;
            }

            director._batches.Add(new LampBatch
            {
                root      = chunkRoot,
                layer     = worldLayer,
                positions = pos,
                pitches   = pit,
                volume    = humVolume,
                count     = n,
                id        = director._nextBatchId++,
            });
        }

        /// <summary>
        /// Pitch determinista de la lámpara del tile GLOBAL
        /// (<paramref name="globalTileX"/>, <paramref name="globalTileZ"/>): 1 ± <see
        /// cref="PitchSpread"/>. Determinista a propósito — todos los clientes oyen el
        /// mismo panel igual, y revisitar un chunk no re-baraja el detune.
        /// </summary>
        public static float PitchFor(int globalTileX, int globalTileZ)
        {
            float h = GridChunkBuilder.Hash01(globalTileX, globalTileZ, PitchSalt);
            return 1f + (h - 0.5f) * 2f * PitchSpread;
        }

        // ── Ciclo de vida ───────────────────────────────────────────────────────

        // Auto-arranque perezoso: el director solo debe existir cuando hay lámparas, y su
        // único llamante es el worldgen. Vive en la escena activa (NO DontDestroyOnLoad):
        // al descargar la escena muere con ella y el siguiente chunk lo recrea, que es un
        // ciclo de vida más corto y con menos sitios donde dejar estado colgando.
        private static FluorescentHumDirector EnsureInstance()
        {
            if (_instance != null) return _instance;
            if (_quitting) return null;
            var go = new GameObject("FluorescentHumDirector");
            _instance = go.AddComponent<FluorescentHumDirector>();
            return _instance;
        }

        // Los estáticos sobreviven a "Enter Play Mode" sin domain reload, así que _quitting
        // de la sesión anterior dejaría el director sin poder arrancar nunca más.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _quitting = false;
            _instance = null;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(this); return; }
            _instance = this;

            var clip = ResolveClip();
            for (int i = 0; i < _slots.Length; i++)
            {
                var go = new GameObject("HumSource" + i);
                go.transform.SetParent(transform, false);

                var src = go.AddComponent<AudioSource>();
                src.clip         = clip;
                src.loop         = true;
                src.playOnAwake  = false;
                src.spatialBlend = 1f;   // 3D completo: el panel se tiene que poder señalar
                src.spread       = 0f;   // fuente puntual — máxima localización
                src.dopplerLevel = 0f;   // la lámpara no se mueve
                src.minDistance  = MinDistance;
                src.maxDistance  = MaxDistance;
                src.rolloffMode  = AudioRolloffMode.Custom;
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, BuildRolloffCurve());
                src.volume       = 0f;

                // Play + Pause de entrada para que a partir de aquí todo sea UnPause/Pause:
                // un Play() sobre una fuente ya sonando la REINICIA y se oye el ataque.
                src.Play();
                src.Pause();

                _slots[i]     = new PoolSlot { src = src, tr = go.transform };
                _selection[i] = HumSlotState.Free;
            }

            RouteToAmbienceMixer();
        }

        /// <summary>
        /// Enruta el pool por el grupo <c>Ambience</c> del mixer del juego.
        ///
        /// NO ES COSMÉTICO, ERA EL BUG. Sin grupo, una AudioSource sale por el Master del
        /// motor y se salta la cadena que SÍ atraviesan los pasos y todos los SFX del
        /// jugador (que van por <c>Sfx</c>, ver <c>ProxyAudioSourceFactory.RouteToSfx</c>).
        /// El zumbido no estaba "alto": estaba FUERA de la mezcla, así que competía contra
        /// un mundo ya atenuado por las opciones de audio y ninguna bajada de
        /// <c>humVolume</c> lo movía de primer plano — se bajó de 0.35 a 0.005 (−37 dB) sin
        /// que se notara la diferencia, que es exactamente el síntoma de un bus equivocado.
        ///
        /// Ambience y no Sfx a propósito: el canal existe para "ambient background sounds"
        /// y es el que el jugador baja cuando quiere menos fondo sin perder los efectos.
        ///
        /// Perezoso y reintentable: el director nace con el primer chunk y
        /// <see cref="AudioManager"/> se auto-crea por RuntimeInitializeOnLoadMethod, pero
        /// depender del orden sería frágil. Sin manager (escena de test pelada) el zumbido
        /// sigue oyéndose, solo que sin mezclar — nunca una excepción.
        /// </summary>
        private void RouteToAmbienceMixer()
        {
            if (_routed) return;
            var mgr = AudioManager.Instance;
            if (mgr == null) return;
            var group = mgr.GetMixerGroup(AudioChannel.Ambience);
            if (group == null) return;

            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i] != null) _slots[i].src.outputAudioMixerGroup = group;
            _routed = true;
        }

        private bool _routed;

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void OnApplicationQuit() => _quitting = true;

        // ── Bucle ───────────────────────────────────────────────────────────────

        private void Update()
        {
            // Una segunda copia añadida a mano se auto-destruye en Awake ANTES de construir
            // el pool, pero Destroy difiere al fin de frame y este Update aún corre.
            if (_slots[0] == null) return;

            // unscaled: con timeScale 0 (pausa) el audio sigue corriendo, y un envelope
            // congelado a mitad de fade dejaría una fuente a medio volumen para siempre.
            float dt = Time.unscaledDeltaTime;

            _refreshTimer -= dt;
            bool refresh = _refreshTimer <= 0f;
            if (refresh)
            {
                _refreshTimer = ReassignInterval;
                RouteToAmbienceMixer(); // no-op en cuanto lo consigue
            }

            if (!ResolveListener())
            {
                // Sin listener no hay a qué acercarse, pero los lotes SÍ hay que retirarlos:
                // el streaming sigue corriendo y la lista crecería sin techo hasta que
                // apareciera un listener (o para siempre, en una escena que no tenga).
                if (refresh) PruneDeadBatches();
                for (int i = 0; i < _selection.Length; i++) _selection[i] = HumSlotState.Free;
                DriveSlots(dt);
                return;
            }

            if (refresh) Reassign(_listener.position);

            DriveSlots(dt);
        }

        // Un lote muere con la raíz de su chunk. Único punto de retirada del sistema.
        private void PruneDeadBatches()
        {
            for (int b = _batches.Count - 1; b >= 0; b--)
                if (_batches[b].root == null) _batches.RemoveAt(b);
        }

        // Encuentra el AudioListener de la escena y lo CACHEA. Nunca lo crea, lo mueve ni
        // lo destruye — ver la cabecera de la clase.
        private bool ResolveListener()
        {
            if (_listener != null) return true;
            _listenerRetry -= Time.unscaledDeltaTime;
            if (_listenerRetry > 0f) return false;
            _listenerRetry = 0.5f;
            var al = FindAnyObjectByType<AudioListener>();
            if (al != null) _listener = al.transform;
            return _listener != null;
        }

        private void Reassign(Vector3 ear)
        {
            // Aislamiento vertical POR CAPA, no por distancia. Las capas están a 4 m
            // (GridConstants.LayerHeight) y la lámpara cuelga a 3,7 m del suelo de la suya,
            // así que la lámpara de la capa de ABAJO queda a 1,95 m del oído — MÁS CERCA en
            // vertical que la de tu propia capa (2,05 m). Ningún corte por |dy| las separa;
            // el índice de capa sí, y es el mismo criterio que el cullingMask de las luces.
            int earLayer = Mathf.FloorToInt(ear.y / GridConstants.LayerHeight);

            _candidates.Clear();
            _refs.Clear();

            for (int b = _batches.Count - 1; b >= 0; b--)
            {
                var batch = _batches[b];
                if (batch.root == null)
                {
                    // Chunk descargado: el lote entero se retira aquí. Sus fuentes vuelven
                    // al pool por el paso 1 del selector (su clave deja de ser candidata).
                    _batches.RemoveAt(b);
                    continue;
                }
                if (batch.layer != earLayer) continue; // aislamiento vertical, ver arriba

                for (int i = 0; i < batch.count; i++)
                {
                    float sqr = (batch.positions[i] - ear).sqrMagnitude;
                    if (sqr > MaxDistanceSqr) continue;

                    long key = ((long)batch.id << LampIndexBits) | (uint)(i & LampIndexMask);
                    _candidates.Add(new HumCandidate { key = key, distance = Mathf.Sqrt(sqr) });
                    _refs[key] = new LampRef
                    {
                        position = batch.positions[i],
                        pitch    = batch.pitches[i],
                        volume   = batch.volume,
                    };
                }
            }

            SelectSlots(_candidates, _selection, HysteresisMetres);
        }

        private void DriveSlots(float dt)
        {
            float step = dt / RetargetFadeSeconds;

            for (int i = 0; i < _slots.Length; i++)
            {
                var slot    = _slots[i];
                long wanted = _selection[i].key;

                if (slot.liveKey != wanted)
                {
                    slot.envelope -= step;
                    if (slot.envelope <= 0f)
                    {
                        slot.envelope = 0f;
                        slot.liveKey  = wanted;
                        if (wanted == NoKey)
                        {
                            slot.baseVolume = 0f;
                            if (slot.src.isPlaying) slot.src.Pause();
                        }
                        else if (_refs.TryGetValue(wanted, out var r))
                        {
                            slot.tr.position = r.position;
                            slot.src.pitch   = r.pitch;
                            slot.baseVolume  = r.volume;
                            if (!slot.src.isPlaying) slot.src.UnPause();
                        }
                        else
                        {
                            // La referencia desapareció entre la selección y el fade (chunk
                            // descargado a mitad). Se queda callado; la próxima pasada le
                            // dará otra lámpara o lo dejará libre.
                            slot.liveKey    = NoKey;
                            slot.baseVolume = 0f;
                            if (slot.src.isPlaying) slot.src.Pause();
                        }
                    }
                }
                else if (wanted != NoKey && slot.envelope < 1f)
                {
                    slot.envelope = Mathf.Min(1f, slot.envelope + step);
                }

                slot.src.volume = slot.baseVolume * slot.envelope * _masterVolume;
            }
        }

        // ── Selección (pura, y por eso testeable) ───────────────────────────────

        /// <summary>Una lámpara compitiendo por un hueco del pool.</summary>
        public struct HumCandidate
        {
            public long  key;
            public float distance; // metros al oído
        }

        /// <summary>Un hueco del pool visto por el selector.</summary>
        public struct HumSlotState
        {
            public long  key;      // <see cref="NoKey"/> ⇒ libre
            public float distance; // sin sentido cuando está libre

            public static HumSlotState Free => new HumSlotState { key = NoKey, distance = 0f };
        }

        // Cacheado: List.Sort(Comparison<T>) asignaría un delegado por llamada si se pasara
        // un lambda en el sitio, y esto corre 4 veces por segundo para siempre.
        private static readonly Comparison<HumCandidate> ByDistance =
            (a, b) => a.distance.CompareTo(b.distance);

        /// <summary>
        /// Reparte los huecos de <paramref name="slots"/> entre las lámparas más cercanas de
        /// <paramref name="candidates"/>, con histéresis. Función PURA sobre sus dos
        /// argumentos (ordena la lista in-place y reescribe el array); pública para que los
        /// tests EditMode la ejerciten sin escena — el compile-check construye la asamblea
        /// con sufijo <c>_check</c>, así que un <c>InternalsVisibleTo</c> nunca casaría.
        ///
        /// <paramref name="candidates"/> viene YA filtrada por capa y por alcance: una
        /// lámpara ausente de la lista es una lámpara que no debe sonar, y el paso 1 libera
        /// a su titular por eso mismo (chunk descargado o jugador fuera de alcance son el
        /// mismo caso desde aquí).
        /// </summary>
        public static void SelectSlots(List<HumCandidate> candidates, HumSlotState[] slots,
            float hysteresis)
        {
            if (slots == null) return;
            if (candidates == null || candidates.Count == 0)
            {
                for (int s = 0; s < slots.Length; s++) slots[s] = HumSlotState.Free;
                return;
            }

            // 1. Refrescar titulares. El que ya no es candidato suelta el hueco.
            for (int s = 0; s < slots.Length; s++)
            {
                if (slots[s].key == NoKey) continue;
                int at = IndexOfKey(candidates, slots[s].key);
                if (at < 0) slots[s] = HumSlotState.Free;
                else slots[s].distance = candidates[at].distance;
            }

            // 2. Más cercanas primero.
            candidates.Sort(ByDistance);

            // 3. Repartir. Como la lista va ascendente, en cuanto un aspirante no logra
            //    superar al peor titular por el margen, ninguno posterior lo hará: se corta.
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (SlotHolding(slots, c.key) >= 0) continue; // ya suena: se queda donde está

                int free = FirstFree(slots);
                if (free >= 0)
                {
                    slots[free] = new HumSlotState { key = c.key, distance = c.distance };
                    continue;
                }

                int worst = WorstHolder(slots);
                if (worst < 0) break;
                if (slots[worst].distance > c.distance + hysteresis)
                    slots[worst] = new HumSlotState { key = c.key, distance = c.distance };
                else
                    break;
            }
        }

        private static int IndexOfKey(List<HumCandidate> candidates, long key)
        {
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i].key == key) return i;
            return -1;
        }

        private static int SlotHolding(HumSlotState[] slots, long key)
        {
            for (int s = 0; s < slots.Length; s++)
                if (slots[s].key == key) return s;
            return -1;
        }

        private static int FirstFree(HumSlotState[] slots)
        {
            for (int s = 0; s < slots.Length; s++)
                if (slots[s].key == NoKey) return s;
            return -1;
        }

        private static int WorstHolder(HumSlotState[] slots)
        {
            int best = -1;
            float far = float.NegativeInfinity;
            for (int s = 0; s < slots.Length; s++)
            {
                if (slots[s].key == NoKey) continue;
                if (slots[s].distance > far) { far = slots[s].distance; best = s; }
            }
            return best;
        }

        // ── Clip ────────────────────────────────────────────────────────────────

        // Curva logarítmica a mano sobre [0, maxDistance]. Unity evalúa la curva de rolloff
        // custom en distancia/maxDistance ∈ [0,1] e IGNORA minDistance, así que la meseta
        // de campo cercano va escrita en la propia curva (hasta 1 m).
        private static AnimationCurve BuildRolloffCurve() => new AnimationCurve(
            new Keyframe(0f,                        1.00f), // 0 m
            new Keyframe(MinDistance / MaxDistance, 1.00f), // 1 m — bajo el panel
            new Keyframe(0.30f,                     0.55f), // 2,1 m
            new Keyframe(0.50f,                     0.25f), // 3,5 m — media baldosa larga
            new Keyframe(0.75f,                     0.07f), // 5,25 m
            new Keyframe(1f,                        0f));   // 7 m — disuelto

        private static AudioClip ResolveClip()
        {
            if (_sharedClip != null) return _sharedClip;

            var authored = Resources.Load<AudioClip>(AuthoredClipResource);
            if (authored != null)
            {
                // Verificación explícita: Unity no espacializa un clip estéreo (se mezcla
                // tal cual a los dos canales), así que un .wav estéreo mataría en silencio
                // toda la localización del sistema. Se rechaza y se avisa.
                if (authored.channels == 1)
                {
                    _sharedClip = authored;
                    return _sharedClip;
                }
                Debug.LogWarning($"[FluorescentHum] '{AuthoredClipResource}' tiene " +
                                 $"{authored.channels} canales. La espacialización 3D exige MONO — " +
                                 "se usa el zumbido sintetizado. Reimporta el clip como mono.");
            }

            var data = RenderHumSamples(ClipSampleRate, ClipSeconds);
            _sharedClip = AudioClip.Create("FluorescentHum", data.Length, 1, ClipSampleRate, false);
            _sharedClip.SetData(data, 0);
            return _sharedClip;
        }

        /// <summary>Frecuencia de muestreo del clip sintetizado.</summary>
        public const int ClipSampleRate = 44100;

        /// <summary>
        /// Duración del clip sintetizado. 2 s en vez de 0,5: el loop es igual de exacto
        /// (ver <see cref="RenderHumSamples"/>) pero el patrón del siseo tarda cuatro veces
        /// más en delatarse como bucle. Coste: un único buffer compartido de ~353 KB para
        /// TODAS las lámparas, no uno por fuente.
        /// </summary>
        public const int ClipSeconds = 2;

        // Ballast magnético: el zumbido de fluorescente es el DOBLE de la red (120 Hz),
        // no la red misma (60 Hz), y lo que lo hace molesto son los armónicos superiores,
        // no el fundamental. Amplitudes decrecientes tipo diente de sierra.
        private static readonly float[] PartialHz  = { 120f, 240f, 360f, 480f, 600f, 720f };
        private static readonly float[] PartialAmp = { 0.45f, 0.30f, 0.22f, 0.16f, 0.11f, 0.07f };

        /// <summary>
        /// Sintetiza el zumbido: parciales de ballast + un pelo de siseo de tubo,
        /// normalizado a ±0,85. MONO por construcción (un solo canal de floats) y
        /// LOOPEABLE SIN CLICK, con dos mecanismos distintos porque son dos problemas
        /// distintos:
        ///
        /// - Los TONOS cierran exacto: cada parcial es múltiplo de 120 Hz y
        ///   <paramref name="seconds"/> es entero, así que <c>freq × seconds</c> es un
        ///   número entero de ciclos y la última muestra empalma con la primera sin más.
        ///   Un crossfade sobre ellos sería peor: introduciría el batido que evita.
        /// - El SISEO no puede cerrar exacto (es ruido), así que se rinde una cola extra y
        ///   se funde sobre la cabeza. Al ir sumado APARTE, esa fusión no toca los tonos.
        ///
        /// Determinista (System.Random con semilla fija) y sin dependencias de Unity, para
        /// poder ejercitarla desde EditMode sin escena ni motor de audio.
        /// </summary>
        public static float[] RenderHumSamples(int sampleRate, int seconds)
        {
            int sc = Mathf.Max(256, sampleRate * Mathf.Max(1, seconds));
            var buf = new float[sc];

            // 1. Tonos — bucle exacto por construcción.
            const double TwoPi = 2.0 * Math.PI;
            for (int p = 0; p < PartialHz.Length; p++)
            {
                double w = TwoPi * PartialHz[p] / sampleRate;
                double a = PartialAmp[p];
                for (int i = 0; i < sc; i++)
                    buf[i] += (float)(a * Math.Sin(w * i));
            }

            // 2. Siseo de tubo: ruido blanco pasado por un paso-alto de un polo (empuja la
            //    energía arriba, donde vive el "fizz" de un tubo viejo), con cola extra para
            //    poder fundirlo sobre sí mismo.
            int fade  = Mathf.Min(512, sc / 4);
            int total = sc + fade;
            var fizz  = new float[total];
            var rng   = new System.Random(12060);
            const float HighPass = 0.92f;
            float prevIn = 0f, prevOut = 0f;
            for (int i = 0; i < total; i++)
            {
                float x = (float)(rng.NextDouble() * 2.0 - 1.0);
                prevOut = HighPass * (prevOut + x - prevIn);
                prevIn  = x;
                fizz[i] = prevOut * 0.045f;
            }
            for (int i = 0; i < fade; i++)
            {
                float w = (float)i / fade;
                fizz[i] = fizz[i] * w + fizz[sc + i] * (1f - w);
            }
            for (int i = 0; i < sc; i++) buf[i] += fizz[i];

            // 3. Normalizar a ±0,85 — margen para que el pitch por lámpara y el volumen de
            //    zona no lleven la suma de varias fuentes a recortar.
            float peak = 0f;
            for (int i = 0; i < sc; i++)
            {
                float a = buf[i] < 0f ? -buf[i] : buf[i];
                if (a > peak) peak = a;
            }
            if (peak > 1e-6f)
            {
                float norm = 0.85f / peak;
                for (int i = 0; i < sc; i++) buf[i] *= norm;
            }

            return buf;
        }
    }
}
