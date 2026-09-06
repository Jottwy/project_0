using System;
using System.Collections.Generic;
using BackroomsSurvival.WorldGen3;
using PolymindGames; // AudioManager / AudioChannel — el mixer del juego
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Audio
{
    /// <summary>
    /// Detalle sonoro de la planta de oficinas: fuentes PUNTUALES por sala, encima del
    /// ambiente global. Solo cliente — nada de esto viaja por el cable ni cambia el wire.
    ///
    /// POR QUÉ EXISTE. El ambiente de oficina que hay hoy es global (el drone de zona) y el
    /// zumbido de <see cref="FluorescentHumDirector"/> es por lámpara: los dos son
    /// continuos y ninguno da la sensación de que el edificio esté HABITADO hace un rato.
    /// Lo que la da son sucesos raros y localizables — un teléfono que suena dos despachos
    /// más allá y calla, una impresora que arranca en el archivo. Este director pone cuatro:
    /// aire acondicionado (continuo, salas con falso techo), impresora, teléfono y crujido
    /// de silla (episódicos).
    ///
    /// LA CLASIFICACIÓN ES DEL CLIENTE, Y ES DERIVADA. El servidor no manda «esto es un
    /// archivo»: manda tramos y atrezo (ADR-129). El papel de cada sala se deduce aquí de lo
    /// que hay dentro — mesas, sillas, archivadores — y de la altura libre, que es la huella
    /// del falso techo de oficina (ADR-105 enm. 18, 270–300 cm por sala en pasos de 10).
    /// Derivar en vez de pedir un campo nuevo es lo que deja este detalle SIN bump de wire.
    ///
    /// DETERMINISTA POR POSICIÓN Y COTA. Qué sala suena, con qué y cada cuánto sale de
    /// <see cref="Wg3Hash"/> sembrado con <c>worldSeed</c>, la posición en cm y el
    /// <c>floorYCm</c> — dos plantas superpuestas no comparten sorteo. Revisitar un chunk no
    /// re-baraja nada y dos clientes clasifican igual la misma sala. Lo que NO está
    /// sincronizado entre clientes es el INSTANTE de cada suceso: la fase es determinista
    /// pero se ancla al reloj local, así que dos jugadores oyen el mismo teléfono con el
    /// mismo ritmo, no a la vez. Sincronizarlo pediría reloj común, o sea wire; queda fuera.
    ///
    /// PRESUPUESTO FIJO, por el mismo motivo que el zumbido: una AudioSource por mueble es
    /// un pico de cientos al montar un chunk. <see cref="SourceBudget"/> fuentes para todo
    /// el mundo, repartidas por cercanía, y lo episódico manda sobre lo continuo — un
    /// teléfono que suena vale más que un aire que ya estabas oyendo.
    ///
    /// CERO FUENTES HUÉRFANAS: viven como hijas de este director y el lote muere con la raíz
    /// del chunk que lo dio de alta. No hay baja explícita que se pueda olvidar.
    /// </summary>
    public sealed class OfficeAmbienceDirector : MonoBehaviour
    {
        /// <summary>Fuentes para TODO el mundo. Seis y no ocho: este sistema convive con las
        /// ocho del zumbido, y el presupuesto de voces es compartido con los pasos y el
        /// vendor.</summary>
        public const int SourceBudget = 6;

        /// <summary>Qué suena. El orden es el de <see cref="ClipResources"/>.</summary>
        public enum Kind : byte
        {
            /// <summary>Continuo. Una sala con falso techo, la rejilla soplando.</summary>
            AirCon = 0,
            /// <summary>Episódico. La impresora del archivo arrancando sola.</summary>
            Printer = 1,
            /// <summary>Episódico. Un despacho lejano; el que llega desde el pasillo.</summary>
            Phone = 2,
            /// <summary>Episódico. Una silla de cubículo asentándose.</summary>
            ChairCreak = 3,
        }

        private const int KindCount = 4;

        // ── Tabla por tipo ──────────────────────────────────────────────────────
        //
        // VOLÚMENES EN EL ORDEN DE 0,05, no de 0,3. Es la lección ya pagada dos veces con el
        // zumbido: un ambiente autorado al nivel de un SFX se come la mezcla y hay que
        // bajarlo a mano después. El alcance es CORTO a propósito — estas fuentes son
        // detalle de sala, no capa de fondo; la única que se estira es el teléfono, porque
        // «lejano» es su gracia.

        private static readonly float[] KindVolume = { 0.045f, 0.060f, 0.055f, 0.040f };
        private static readonly float[] KindMinDistance = { 1.5f, 1.0f, 2.0f, 1.0f };
        private static readonly float[] KindMaxDistance = { 9.0f, 12.0f, 18.0f, 7.0f };

        // Periodos de los episódicos, en segundos: [mínimo, mínimo+rango).
        private static readonly float[] PeriodMin = { 0f, 45f, 180f, 25f };
        private static readonly float[] PeriodSpan = { 0f, 75f, 240f, 45f };

        /// <summary>Rutas bajo Resources de clips AUTORADOS que sustituyen a los
        /// sintetizados; el índice es el de <see cref="Kind"/>. Soltar un .wav MONO ahí es
        /// todo lo que hace falta para retirar el placeholder — cero código.</summary>
        public static readonly string[] ClipResources =
        {
            "Audio/OfficeAirCon",
            "Audio/OfficePrinter",
            "Audio/OfficePhone",
            "Audio/OfficeChairCreak",
        };

        /// <summary>Volumen global encima de la tabla. 0 apaga el sistema sin desmontarlo.</summary>
        public static float MasterVolume
        {
            get => _masterVolume;
            set => _masterVolume = Mathf.Clamp(value, 0f, 4f);
        }
        private static float _masterVolume = 1f;

        // ── Clasificación de salas ──────────────────────────────────────────────

        /// <summary>Un tramo servido, en las unidades del cable (cm de MUNDO).</summary>
        public struct RoomSpec
        {
            public int xCm, zCm, sizeXCm, sizeZCm, floorYCm, heightCm;
        }

        /// <summary>Un mueble de ADR-129, en las unidades del cable.</summary>
        public struct PropSpec
        {
            public int xCm, yCm, zCm;
            public byte kind;
        }

        /// <summary>Una fuente colocada: dónde, qué, y cada cuánto (0 = continua).</summary>
        public struct Emitter
        {
            public Vector3 position; // MUNDO, en metros
            public Kind kind;
            public float period;  // segundos entre sucesos; 0 en los continuos
            public float phase01; // 0..1 del primer suceso dentro del periodo
        }

        // Los `kind` del atrezo (espejo de Wg3PropMsg; se copian y no se referencian para no
        // atar el audio al parser del cable).
        private const byte PropDesk = 1, PropChair = 2, PropCabinet = 3, PropShelf = 4,
            PropBox = 7, PropMonitor = 9, PropPhone = 11, PropChairFallen = 14;

        // Sales. Una por decisión, como manda la regla R3 de WG3: dos sorteos en el mismo
        // punto no pueden quedar correlacionados.
        private const uint SaltAirCon = 0x4F41_4331u;
        private const uint SaltPrinter = 0x4F50_5231u;
        private const uint SaltPhone = 0x4F50_4831u;
        private const uint SaltCreak = 0x4F43_5231u;
        private const uint SaltAnchor = 0x4F41_4E43u;
        private const uint SaltPeriod = 0x4F50_4552u;

        /// <summary>Tope del falso techo de oficina (ADR-105 enm. 18: 270–300 cm).</summary>
        public const int OfficeCeilingMaxCm = 300;

        /// <summary>Probabilidad de que un despacho con teléfono sea el que suena. Baja a
        /// propósito: un teléfono cada dos salas deja de ser un suceso y pasa a ser fondo.</summary>
        public const float PhoneChance = 0.18f;

        /// <summary>Probabilidad de que un archivo tenga impresora.</summary>
        public const float PrinterChance = 0.50f;

        /// <summary>Media planta de holgura: un mueble de la planta de arriba no cuenta como
        /// mueble de esta sala. La costura entre plantas está en 332 cm.</summary>
        private const int SameStoreyCm = 250;

        /// <summary>
        /// Clasifica UNA sala y añade sus fuentes a <paramref name="into"/>. Pura, sin Unity
        /// más allá de <c>Vector3</c>: es lo que permite ejercitarla desde EditMode sin
        /// escena ni motor de audio.
        ///
        /// El orden de <paramref name="props"/> NO influye en el resultado (regla dura 13):
        /// el ancla de cada fuente es el mueble de hash mínimo, no el primero que aparezca.
        /// </summary>
        public static void Classify(int worldSeed, RoomSpec room, IReadOnlyList<PropSpec> props,
            List<Emitter> into)
        {
            if (into == null) return;

            int desks = 0, chairs = 0, storage = 0, officeProps = 0;
            int bestChair = -1, bestStorage = -1, bestPhone = -1;
            ulong bestChairH = ulong.MaxValue, bestStorageH = ulong.MaxValue, bestPhoneH = ulong.MaxValue;

            int n = props?.Count ?? 0;
            for (int i = 0; i < n; i++)
            {
                PropSpec p = props[i];
                if (p.xCm < room.xCm || p.xCm > room.xCm + room.sizeXCm) continue;
                if (p.zCm < room.zCm || p.zCm > room.zCm + room.sizeZCm) continue;
                if (Math.Abs(p.yCm - room.floorYCm) > SameStoreyCm) continue;

                switch (p.kind)
                {
                    case PropDesk: desks++; officeProps++; break;
                    case PropChair:
                    case PropChairFallen: chairs++; officeProps++; break;
                    case PropCabinet:
                    case PropShelf:
                    case PropBox: storage++; officeProps++; break;
                    case PropMonitor: officeProps++; break;
                }

                // Anclas por hash mínimo — independiente del orden de la lista.
                ulong h = Wg3Hash.Mix(worldSeed, p.xCm, p.zCm, unchecked((int)(SaltAnchor ^ p.kind)));
                if ((p.kind == PropChair || p.kind == PropChairFallen) && h < bestChairH)
                {
                    bestChairH = h; bestChair = i;
                }
                if ((p.kind == PropCabinet || p.kind == PropShelf || p.kind == PropBox) && h < bestStorageH)
                {
                    bestStorageH = h; bestStorage = i;
                }
                if (p.kind == PropPhone && h < bestPhoneH)
                {
                    bestPhoneH = h; bestPhone = i;
                }
            }

            float areaM2 = room.sizeXCm * room.sizeZCm * 1e-4f;
            float floorM = room.floorYCm * 0.01f;

            // 1. AIRE ACONDICIONADO — la sala tiene falso techo.
            //
            // El 300 SOLO no distingue: `CEILING_MIN_CM` es 300 y media planta del mundo mide
            // eso sin ser oficina. Por debajo de 300 sí es inequívoco (nadie más recorta el
            // techo), y justo en 300 se exige además que la sala esté AMUEBLADA. Sin esa
            // segunda condición, todo pasillo ancho del mundo tendría rejilla.
            bool falseCeiling = room.heightCm <= OfficeCeilingMaxCm
                                && (room.heightCm < OfficeCeilingMaxCm || officeProps >= 2)
                                && areaM2 >= 9f;
            if (falseCeiling)
            {
                into.Add(new Emitter
                {
                    // Centro de la sala, colgada del plenum: la rejilla está EN el falso techo.
                    position = new Vector3(
                        (room.xCm + room.sizeXCm * 0.5f) * 0.01f,
                        floorM + room.heightCm * 0.01f - 0.20f,
                        (room.zCm + room.sizeZCm * 0.5f) * 0.01f),
                    kind = Kind.AirCon,
                    period = 0f,
                    phase01 = 0f,
                });
            }

            // 2. CUBÍCULOS — mesas y sillas en cantidad. Una silla cruje, y solo una: el
            //    presupuesto no da para que la sala entera se asiente a la vez, y con dos
            //    fuentes iguales a tres metros deja de sonar a mueble y suena a bucle.
            bool cubicles = desks >= 3 && chairs >= 3;
            if (cubicles && bestChair >= 0)
            {
                PropSpec c = props[bestChair];
                AddEpisodic(worldSeed, into, Kind.ChairCreak, SaltCreak, 1f,
                    new Vector3(c.xCm * 0.01f, c.yCm * 0.01f + 0.50f, c.zCm * 0.01f), c.xCm, c.zCm, room.floorYCm);
            }

            // 3. ARCHIVO — armarios y cajas sin puestos de trabajo.
            if (!cubicles && storage >= 3 && desks <= 1 && bestStorage >= 0)
            {
                PropSpec s = props[bestStorage];
                AddEpisodic(worldSeed, into, Kind.Printer, SaltPrinter, PrinterChance,
                    new Vector3(s.xCm * 0.01f, s.yCm * 0.01f + 0.90f, s.zCm * 0.01f), s.xCm, s.zCm, room.floorYCm);
            }

            // 4. DESPACHO — uno o dos puestos, con teléfono, y pequeño. Es la sala que se
            //    oye desde fuera; por eso su alcance es el largo de la tabla.
            if (!cubicles && desks >= 1 && desks <= 2 && bestPhone >= 0 && areaM2 <= 60f)
            {
                PropSpec p = props[bestPhone];
                AddEpisodic(worldSeed, into, Kind.Phone, SaltPhone, PhoneChance,
                    new Vector3(p.xCm * 0.01f, p.yCm * 0.01f + 0.02f, p.zCm * 0.01f), p.xCm, p.zCm, room.floorYCm);
            }
        }

        private static void AddEpisodic(int worldSeed, List<Emitter> into, Kind kind, uint salt,
            float chance, Vector3 position, int xCm, int zCm, int floorYCm)
        {
            // La COTA entra en la mezcla: dos salas superpuestas en plantas distintas caen en
            // el mismo (x,z) y sin ella sortearían igual.
            var pick = new Wg3Hash.Stream(Wg3Hash.Mix(worldSeed, xCm, zCm, floorYCm) ^ salt);
            if (pick.Next01() >= chance) return;

            var timing = new Wg3Hash.Stream(Wg3Hash.Mix(worldSeed, xCm, zCm, floorYCm) ^ SaltPeriod ^ salt);
            int k = (int)kind;
            into.Add(new Emitter
            {
                position = position,
                kind = kind,
                period = PeriodMin[k] + timing.Next01() * PeriodSpan[k],
                phase01 = timing.Next01(),
            });
        }

        /// <summary>
        /// Clasifica un chunk entero. El barrido es cuadrático (salas × muebles) y eso es
        /// aceptable porque corre UNA vez por chunk montado, en el mismo frame que ya paga
        /// instanciar ese atrezo — no cada pasada.
        /// </summary>
        public static void BuildEmitters(int worldSeed, IReadOnlyList<RoomSpec> rooms,
            IReadOnlyList<PropSpec> props, List<Emitter> into)
        {
            if (rooms == null || into == null) return;
            for (int i = 0; i < rooms.Count; i++) Classify(worldSeed, rooms[i], props, into);
        }

        // ── Registro desde el streaming ─────────────────────────────────────────

        private sealed class Batch
        {
            public Transform root;    // raíz del chunk; null (destruida) ⇒ lote retirado
            public Emitter[] emitters;
            public float[] nextAt;    // reloj local del suceso siguiente, por emisor
            public int id;            // monótono: una clave vieja no aliasa un lote nuevo
        }

        private readonly List<Batch> _batches = new List<Batch>();
        private int _nextBatchId;

        private const int EmitterIndexBits = 12;
        private const int EmitterIndexMask = (1 << EmitterIndexBits) - 1;
        private const long NoKey = -1L;

        /// <summary>
        /// Da de alta las fuentes de un chunk. El lote muere con
        /// <paramref name="chunkRoot"/>: no hay baja explícita.
        /// </summary>
        public static void RegisterChunk(Transform chunkRoot, List<Emitter> emitters)
        {
            if (chunkRoot == null || emitters == null || emitters.Count == 0) return;
            var director = EnsureInstance();
            if (director == null) return;

            int n = Mathf.Min(emitters.Count, EmitterIndexMask);
            var arr = new Emitter[n];
            var next = new float[n];
            float now = Time.unscaledTime;
            for (int i = 0; i < n; i++)
            {
                arr[i] = emitters[i];
                next[i] = arr[i].period > 0f ? now + arr[i].phase01 * arr[i].period : 0f;
            }

            director._batches.Add(new Batch
            {
                root = chunkRoot,
                emitters = arr,
                nextAt = next,
                id = director._nextBatchId++,
            });
        }

        // ── Ciclo de vida ───────────────────────────────────────────────────────

        private static OfficeAmbienceDirector _instance;
        private static bool _quitting;
        private static readonly AudioClip[] _clips = new AudioClip[KindCount];

        private static OfficeAmbienceDirector EnsureInstance()
        {
            if (_instance != null) return _instance;
            if (_quitting) return null;
            var go = new GameObject("OfficeAmbienceDirector");
            _instance = go.AddComponent<OfficeAmbienceDirector>();
            return _instance;
        }

        // Los estáticos sobreviven a "Enter Play Mode" sin domain reload.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _quitting = false;
            _instance = null;
            for (int i = 0; i < _clips.Length; i++) _clips[i] = null;
        }

        private enum SlotMode : byte { Idle = 0, Loop = 1, OneShot = 2 }

        private sealed class Slot
        {
            public AudioSource src;
            public Transform tr;
            public long key = NoKey;
            public SlotMode mode;
            public float target;   // volumen destino del continuo
            public float busyUntil; // reloj local hasta el que el one-shot ocupa el hueco
        }

        private readonly Slot[] _slots = new Slot[SourceBudget];
        private bool _routed;
        private float _routeDeadline;
        private bool _routeReported;

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(this); return; }
            _instance = this;

            for (int i = 0; i < _slots.Length; i++)
            {
                var go = new GameObject("OfficeAmbienceSource" + i);
                go.transform.SetParent(transform, false);

                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                src.spatialBlend = 1f;  // 3D completo: la fuente se tiene que poder señalar
                src.spread = 0f;
                src.dopplerLevel = 0f;  // ni la rejilla ni el teléfono se mueven
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = 1f;
                src.maxDistance = 12f;
                src.volume = 0f;

                _slots[i] = new Slot { src = src, tr = go.transform };
            }

            _routeDeadline = Time.unscaledTime + 5f;
            RouteToAmbienceMixer();
        }

        /// <summary>
        /// Enruta el pool por el grupo <c>Ambience</c> del mixer del juego.
        ///
        /// NO ES COSMÉTICO: una AudioSource sin <c>outputAudioMixerGroup</c> sale por el
        /// Master del motor, se salta la cadena que atraviesa todo lo demás y ninguna bajada
        /// de volumen la corrige. Es el bug que ya costó tres commits con el zumbido.
        ///
        /// Nunca <c>AudioMixer.SetFloat</c>: un nombre no expuesto deja un error CON STACK
        /// por llamada (229 k líneas en una sesión). Aquí solo se pide el grupo.
        ///
        /// Perezoso y reintentable, y con ACUSE por escrito: una línea cuando lo consigue —
        /// que es justo lo que hay que poder buscar en el log para confirmar que ninguna
        /// fuente salió por Master— y un aviso si a los 5 s sigue sin manager.
        /// </summary>
        private void RouteToAmbienceMixer()
        {
            if (_routed) return;
            var mgr = AudioManager.Instance;
            var group = mgr != null ? mgr.GetMixerGroup(AudioChannel.Ambience) : null;
            if (group == null)
            {
                if (!_routeReported && Time.unscaledTime > _routeDeadline)
                {
                    _routeReported = true;
                    Debug.LogWarning("[OfficeAmbience] sin grupo Ambience: las " + SourceBudget +
                                     " fuentes están saliendo por MASTER y ningún slider las baja.");
                }
                return;
            }

            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i] != null) _slots[i].src.outputAudioMixerGroup = group;
            _routed = true;
            Debug.Log($"[OfficeAmbience] {_slots.Length} fuentes enrutadas a '{group.name}' (Ambience); 0 por Master.");
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void OnApplicationQuit() => _quitting = true;

        // ── Bucle ───────────────────────────────────────────────────────────────

        private const float ReassignInterval = 0.25f;
        private const float LoopFadeSeconds = 0.35f;

        /// <summary>Media planta: una fuente de la planta de al lado no se oye a través del
        /// forjado. El corte por |dy| es lo que impide que un teléfono con 18 m de alcance
        /// suene desde el piso de arriba.</summary>
        private const float SameStoreyM = 2.6f;

        private Transform _listener;
        private float _listenerRetry;
        private float _refreshTimer;

        private readonly List<Candidate> _due = new List<Candidate>();
        private readonly List<Candidate> _loops = new List<Candidate>();

        private struct Candidate
        {
            public long key;
            public float distance;
            public Vector3 position;
            public Kind kind;
            public int batch;
            public int index;
        }

        private static readonly Comparison<Candidate> ByDistance =
            (a, b) => a.distance.CompareTo(b.distance);

        private void Update()
        {
            if (_slots[0] == null) return; // copia duplicada a medio destruir

            float dt = Time.unscaledDeltaTime;
            float now = Time.unscaledTime;

            _refreshTimer -= dt;
            if (_refreshTimer <= 0f)
            {
                _refreshTimer = ReassignInterval;
                RouteToAmbienceMixer(); // no-op en cuanto lo consigue
                PruneDeadBatches();
                if (ResolveListener()) Reassign(_listener.position, now);
                else ReleaseAll();
            }

            DriveSlots(dt, now);
        }

        private void PruneDeadBatches()
        {
            for (int b = _batches.Count - 1; b >= 0; b--)
                if (_batches[b].root == null) _batches.RemoveAt(b);
        }

        // Encuentra el AudioListener y lo cachea. Nunca lo crea, lo mueve ni lo destruye.
        private bool ResolveListener()
        {
            if (_listener != null) return true;
            _listenerRetry -= ReassignInterval;
            if (_listenerRetry > 0f) return false;
            _listenerRetry = 0.5f;
            var al = FindAnyObjectByType<AudioListener>();
            if (al != null) _listener = al.transform;
            return _listener != null;
        }

        private void ReleaseAll()
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].mode == SlotMode.Loop) _slots[i].target = 0f;
        }

        private void Reassign(Vector3 ear, float now)
        {
            _due.Clear();
            _loops.Clear();

            for (int b = 0; b < _batches.Count; b++)
            {
                Batch batch = _batches[b];
                for (int i = 0; i < batch.emitters.Length; i++)
                {
                    Emitter e = batch.emitters[i];
                    int k = (int)e.kind;

                    Vector3 d = e.position - ear;
                    if (Mathf.Abs(d.y) > SameStoreyM) continue; // aislamiento entre plantas
                    float dist = d.magnitude;
                    if (dist > KindMaxDistance[k]) continue;

                    long key = ((long)batch.id << EmitterIndexBits) | (uint)(i & EmitterIndexMask);
                    var cand = new Candidate
                    {
                        key = key, distance = dist, position = e.position,
                        kind = e.kind, batch = b, index = i,
                    };

                    if (e.period <= 0f) { _loops.Add(cand); continue; }
                    if (now >= batch.nextAt[i]) _due.Add(cand);
                }
            }

            // Los episódicos primero y por cercanía: un teléfono a 4 m manda sobre un aire
            // que ya venías oyendo. Si no cabe, el suceso SE PIERDE y se reprograma — no se
            // encola, porque una cola de sucesos vencidos se vacía toda de golpe al liberarse
            // un hueco y suena a fallo, no a oficina.
            _due.Sort(ByDistance);
            for (int i = 0; i < _due.Count; i++)
            {
                Candidate c = _due[i];
                Batch batch = _batches[c.batch];
                batch.nextAt[c.index] = now + batch.emitters[c.index].period;

                int slot = FreeOrLoopSlot(now);
                if (slot < 0) continue;
                StartOneShot(_slots[slot], c, now);
            }

            // Y los continuos con lo que sobre, por cercanía.
            _loops.Sort(ByDistance);
            int taken = 0;
            for (int i = 0; i < _loops.Count && taken < _slots.Length; i++)
            {
                Candidate c = _loops[i];
                if (HoldsKey(c.key)) { taken++; continue; }
                int slot = FreeSlot(now);
                if (slot < 0) break;
                StartLoop(_slots[slot], c);
                taken++;
            }

            // Un continuo cuya fuente ya no es candidata se apaga con fundido.
            for (int s = 0; s < _slots.Length; s++)
            {
                Slot slot = _slots[s];
                if (slot.mode != SlotMode.Loop) continue;
                if (!StillCandidate(slot.key)) slot.target = 0f;
            }
        }

        private bool HoldsKey(long key)
        {
            for (int s = 0; s < _slots.Length; s++)
                if (_slots[s].mode == SlotMode.Loop && _slots[s].key == key && _slots[s].target > 0f)
                    return true;
            return false;
        }

        private bool StillCandidate(long key)
        {
            for (int i = 0; i < _loops.Count; i++) if (_loops[i].key == key) return true;
            return false;
        }

        private int FreeSlot(float now)
        {
            for (int s = 0; s < _slots.Length; s++)
                if (_slots[s].mode == SlotMode.Idle) return s;
            return -1;
        }

        // Para un episódico: primero un hueco libre, y si no hay, el continuo MÁS LEJANO.
        private int FreeOrLoopSlot(float now)
        {
            int free = FreeSlot(now);
            if (free >= 0) return free;
            int worst = -1;
            float worstDist = -1f;
            for (int s = 0; s < _slots.Length; s++)
            {
                if (_slots[s].mode != SlotMode.Loop) continue;
                float d = DistanceOf(_slots[s].key);
                if (d > worstDist) { worstDist = d; worst = s; }
            }
            return worst;
        }

        private float DistanceOf(long key)
        {
            for (int i = 0; i < _loops.Count; i++) if (_loops[i].key == key) return _loops[i].distance;
            return float.MaxValue;
        }

        private void StartLoop(Slot slot, Candidate c)
        {
            AudioClip clip = ResolveClip(c.kind);
            if (clip == null) return;
            int k = (int)c.kind;

            slot.key = c.key;
            slot.mode = SlotMode.Loop;
            slot.tr.position = c.position;
            slot.src.clip = clip;
            slot.src.loop = true;
            slot.src.minDistance = KindMinDistance[k];
            slot.src.maxDistance = KindMaxDistance[k];
            slot.src.volume = 0f;
            slot.target = KindVolume[k] * _masterVolume;
            slot.src.Play();
        }

        private void StartOneShot(Slot slot, Candidate c, float now)
        {
            AudioClip clip = ResolveClip(c.kind);
            if (clip == null) return;
            int k = (int)c.kind;

            // Un continuo desalojado se corta aquí: es el precio declarado de que lo
            // episódico mande, y dura lo que dure el suceso.
            slot.src.Stop();
            slot.src.clip = null;
            slot.src.loop = false;
            slot.key = c.key;
            slot.mode = SlotMode.OneShot;
            slot.tr.position = c.position;
            slot.src.minDistance = KindMinDistance[k];
            slot.src.maxDistance = KindMaxDistance[k];
            slot.src.volume = 1f; // el nivel va en el PlayOneShot, no aquí
            slot.target = 0f;
            slot.busyUntil = now + clip.length + 0.05f;
            slot.src.PlayOneShot(clip, KindVolume[k] * _masterVolume);
        }

        private void DriveSlots(float dt, float now)
        {
            float step = dt / LoopFadeSeconds;
            for (int s = 0; s < _slots.Length; s++)
            {
                Slot slot = _slots[s];
                switch (slot.mode)
                {
                    case SlotMode.Loop:
                        slot.src.volume = Mathf.MoveTowards(slot.src.volume, slot.target, step);
                        if (slot.target <= 0f && slot.src.volume <= 0f)
                        {
                            slot.src.Stop();
                            slot.src.clip = null;
                            slot.mode = SlotMode.Idle;
                            slot.key = NoKey;
                        }
                        break;

                    case SlotMode.OneShot:
                        if (now >= slot.busyUntil)
                        {
                            slot.mode = SlotMode.Idle;
                            slot.key = NoKey;
                            slot.src.volume = 0f;
                        }
                        break;
                }
            }
        }

        // ── Clips ───────────────────────────────────────────────────────────────

        /// <summary>Frecuencia de muestreo de los clips sintetizados.</summary>
        public const int ClipSampleRate = 44100;

        /// <summary>
        /// El clip del tipo: AUTORADO si existe bajo Resources, sintetizado si no.
        ///
        /// El estéreo se RECHAZA con aviso: Unity no espacializa un clip de dos canales, y
        /// esa es la forma clásica de que un sistema 3D suene plano sin decir nada.
        /// </summary>
        private static AudioClip ResolveClip(Kind kind)
        {
            int k = (int)kind;
            if (_clips[k] != null) return _clips[k];

            var authored = Resources.Load<AudioClip>(ClipResources[k]);
            if (authored != null)
            {
                if (authored.channels == 1) { _clips[k] = authored; return _clips[k]; }
                Debug.LogWarning($"[OfficeAmbience] '{ClipResources[k]}' tiene {authored.channels} " +
                                 "canales. La espacialización 3D exige MONO — se usa el placeholder " +
                                 "sintetizado. Reimporta el clip como mono.");
            }

            float[] data = RenderSamples(kind, ClipSampleRate);
            var clip = AudioClip.Create("OfficeAmbience_" + kind, data.Length, 1, ClipSampleRate, false);
            clip.SetData(data, 0);
            _clips[k] = clip;
            return clip;
        }

        /// <summary>
        /// PLACEHOLDER SINTÉTICO, y está declarado como deuda: no hay en <c>Assets</c> ni un
        /// clip de rejilla, impresora, timbre o crujido que reutilizar (se buscaron: el pack
        /// del vendor solo trae impactos, pasos y foley del jugador). Estas cuatro funciones
        /// existen para que el sistema se pueda oír y medir HOY; sustituirlas es soltar
        /// cuatro .wav mono en <c>Resources/Audio</c> con los nombres de
        /// <see cref="ClipResources"/>.
        ///
        /// Mono, deterministas (<c>System.Random</c> con semilla fija) y sin dependencias del
        /// motor de audio, para poder ejercitarlas desde EditMode.
        /// </summary>
        public static float[] RenderSamples(Kind kind, int sampleRate)
        {
            switch (kind)
            {
                case Kind.AirCon: return RenderAirConSamples(sampleRate, 2);
                case Kind.Printer: return RenderPrinterSamples(sampleRate);
                case Kind.Phone: return RenderPhoneRingSamples(sampleRate);
                default: return RenderChairCreakSamples(sampleRate);
            }
        }

        /// <summary>
        /// Rejilla de aire: dos parciales graves (el motor del fan-coil) más ruido rosa
        /// pasado por un paso-bajo, que es el soplido. LOOPEABLE SIN CLICK con los dos
        /// mecanismos del zumbido — los tonos cierran exacto porque son múltiplos enteros de
        /// 1/<paramref name="seconds"/>, y el ruido se funde sobre su propia cola.
        /// </summary>
        public static float[] RenderAirConSamples(int sampleRate, int seconds)
        {
            int sc = Mathf.Max(256, sampleRate * Mathf.Max(1, seconds));
            var buf = new float[sc];
            const double TwoPi = 2.0 * Math.PI;

            // 24 y 48 Hz: el fundamental del ventilador y su segundo. Ambos enteros por
            // segundo, así que el bucle empalma exacto.
            float[] hz = { 24f, 48f, 96f };
            float[] amp = { 0.30f, 0.18f, 0.06f };
            for (int p = 0; p < hz.Length; p++)
            {
                double w = TwoPi * hz[p] / sampleRate;
                for (int i = 0; i < sc; i++) buf[i] += (float)(amp[p] * Math.Sin(w * i));
            }

            int fade = Mathf.Min(2048, sc / 4);
            int total = sc + fade;
            var air = new float[total];
            var rng = new System.Random(90210);
            // Paso-bajo de un polo: el soplido es todo grave y medio, sin el filo del siseo.
            float lp = 0f;
            for (int i = 0; i < total; i++)
            {
                float x = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp += 0.06f * (x - lp);
                air[i] = lp * 1.9f;
            }
            for (int i = 0; i < fade; i++)
            {
                float w = (float)i / fade;
                air[i] = air[i] * w + air[sc + i] * (1f - w);
            }
            for (int i = 0; i < sc; i++) buf[i] += air[i];

            return Normalize(buf, 0.80f);
        }

        /// <summary>
        /// Impresora láser arrancando: el motor sube de vueltas (barrido de 180 a 300 Hz con
        /// modulación de amplitud, que es el zumbido del rodillo), pasan dos hojas —dos
        /// ráfagas de ruido filtrado— y el motor cae.
        /// </summary>
        public static float[] RenderPrinterSamples(int sampleRate)
        {
            int sc = Mathf.Max(256, (int)(sampleRate * 2.6f));
            var buf = new float[sc];
            const double TwoPi = 2.0 * Math.PI;
            var rng = new System.Random(31415);

            double phase = 0.0;
            float paperLp = 0f;
            for (int i = 0; i < sc; i++)
            {
                float t = (float)i / sc;

                // Envolvente del motor: arranque de 0,15 s, meseta y caída de 0,25 s.
                float motorEnv = Mathf.Clamp01(t / 0.06f) * Mathf.Clamp01((1f - t) / 0.10f);
                float f = Mathf.Lerp(180f, 300f, Mathf.Clamp01(t * 1.6f));
                phase += TwoPi * f / sampleRate;
                // Diente de sierra pobre (fundamental + segundo): un seno puro suena a test,
                // no a motor.
                float motor = (float)(Math.Sin(phase) * 0.5 + Math.Sin(2.0 * phase) * 0.22);
                // AM del rodillo, 11 Hz: es lo que delata que hay algo GIRANDO.
                float am = 1f + 0.35f * (float)Math.Sin(TwoPi * 11.0 * i / sampleRate);
                buf[i] = motor * am * motorEnv * 0.55f;

                // Dos hojas: ruido paso-bajo con envolvente corta.
                float sheet = Burst(t, 0.42f, 0.16f) + Burst(t, 0.74f, 0.16f);
                float x = (float)(rng.NextDouble() * 2.0 - 1.0);
                paperLp += 0.25f * (x - paperLp);
                buf[i] += paperLp * sheet * 0.8f;
            }

            return Normalize(buf, 0.85f);
        }

        /// <summary>
        /// Timbre de teléfono de sobremesa: dos tonos (1 000 y 1 250 Hz) con trémolo de 20 Hz
        /// —la campana de dos golpes— en dos ráfagas de 1 s separadas por medio segundo. Es
        /// el patrón que se reconoce desde un pasillo aunque esté a −26 dB.
        /// </summary>
        public static float[] RenderPhoneRingSamples(int sampleRate)
        {
            int sc = Mathf.Max(256, (int)(sampleRate * 2.5f));
            var buf = new float[sc];
            const double TwoPi = 2.0 * Math.PI;

            for (int i = 0; i < sc; i++)
            {
                float t = (float)i / sampleRate;
                // Ráfagas: [0,00–1,00] y [1,50–2,50].
                float gate = (t < 1.0f) ? 1f : (t >= 1.5f ? 1f : 0f);
                if (gate <= 0f) continue;
                // Bordes suavizados: un corte seco en una senoide es un click.
                float local = t < 1.0f ? t : t - 1.5f;
                float env = Mathf.Clamp01(local / 0.02f) * Mathf.Clamp01((1.0f - local) / 0.04f);
                float warble = 0.5f + 0.5f * (float)Math.Sin(TwoPi * 20.0 * i / sampleRate);
                float tone = (float)(Math.Sin(TwoPi * 1000.0 * i / sampleRate) * 0.55
                                     + Math.Sin(TwoPi * 1250.0 * i / sampleRate) * 0.45);
                buf[i] = tone * (0.35f + 0.65f * warble) * env;
            }

            return Normalize(buf, 0.85f);
        }

        /// <summary>
        /// Crujido de silla: stick-slip. Ruido con una resonancia que barre de 300 a 560 Hz
        /// y una envolvente irregular a tirones — un crujido continuo suena a puerta de
        /// película; lo que suena a mueble es el tartamudeo.
        /// </summary>
        public static float[] RenderChairCreakSamples(int sampleRate)
        {
            int sc = Mathf.Max(256, (int)(sampleRate * 0.9f));
            var buf = new float[sc];
            const double TwoPi = 2.0 * Math.PI;
            var rng = new System.Random(2718);

            // Resonador de dos polos barrido: y[n] = x[n] + 2r·cos(w)·y[n-1] − r²·y[n-2].
            float y1 = 0f, y2 = 0f;
            const float R = 0.985f;
            for (int i = 0; i < sc; i++)
            {
                float t = (float)i / sc;
                float f = Mathf.Lerp(300f, 560f, t);
                float w = (float)(TwoPi * f / sampleRate);
                float a1 = 2f * R * Mathf.Cos(w);
                float a2 = -R * R;

                float x = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.05f;
                float y = x + a1 * y1 + a2 * y2;
                y2 = y1; y1 = y;

                // Tirones a 17 Hz recortados: la madera agarra y suelta, no desliza.
                float grip = Mathf.Max(0f, (float)Math.Sin(TwoPi * 17.0 * i / sampleRate) - 0.25f);
                float env = Mathf.Clamp01(t / 0.05f) * Mathf.Clamp01((1f - t) / 0.35f);
                buf[i] = y * grip * env;
            }

            return Normalize(buf, 0.75f);
        }

        // Envolvente de ráfaga centrada en `at` con anchura `width`, en t normalizado.
        private static float Burst(float t, float at, float width)
        {
            float d = Mathf.Abs(t - at) / (width * 0.5f);
            return d >= 1f ? 0f : (1f - d) * (1f - d);
        }

        private static float[] Normalize(float[] buf, float peakTarget)
        {
            float peak = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                float a = buf[i] < 0f ? -buf[i] : buf[i];
                if (a > peak) peak = a;
            }
            if (peak > 1e-6f)
            {
                float norm = peakTarget / peak;
                for (int i = 0; i < buf.Length; i++) buf[i] *= norm;
            }
            return buf;
        }
    }
}
