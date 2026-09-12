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
            public short yawDeg;
            public byte kind;
        }

        /// <summary>Una fuente colocada: dónde, qué, y cada cuánto (0 = continua).</summary>
        public struct Emitter
        {
            public Vector3 position; // MUNDO, en metros
            public Kind kind;
            public float yawDeg;  // giro del prop visible, si el tipo tiene uno
            public float period;  // segundos entre sucesos; 0 en los continuos
            public float phase01; // 0..1 del primer suceso dentro del periodo
        }

        // ── El prop que se VE ───────────────────────────────────────────────────
        //
        // «Donde hay sonido de ventilador o aires, debería estar el prop» (Joel, 06-09), y no es
        // sólo cosmética: una fuente puntual invisible es indiagnosticable. Con la rejilla puesta,
        // que el aire suene desplazado o dentro de una viga se VE en una captura en vez de
        // discutirse de oído.
        //
        // El teléfono y la silla NO llevan prop propio: ya están anclados EN el mueble que el
        // servidor colocó (ADR-129), y duplicarlo pondría dos teléfonos en la misma mesa.

        /// <summary>Prefab bajo <c>Resources/Wg3Props</c> que hace visible la fuente, o
        /// <c>null</c> si el tipo ya suena desde un mueble que el servidor puso.</summary>
        public static string VisualPrefabOf(Kind kind)
        {
            switch (kind)
            {
                case Kind.AirCon: return "Vent";
                case Kind.Printer: return "Printer";
                default: return null;
            }
        }

        /// <summary>
        /// Escala del prop visible. La rejilla del pack mide 0,81 m y el techo de oficina está
        /// aplacado a 0,60 (<c>Wg3SceneAssembler.CeilingTileM</c>): a tamaño original se sale de la
        /// placa y se lee como un error de rejilla, no como una salida de aire. 0,60/0,8065.
        /// </summary>
        public static float VisualScaleOf(Kind kind) => kind == Kind.AirCon ? 0.7439f : 1f;

        // LÍMITE CONOCIDO, no resuelto: el cliente no sabe qué hueco del techo está libre — los
        // macizos de ADR-105 (vigas, dinteles) llegan como geometría, no como ocupación. Una
        // rejilla puede caer dentro de una viga. Se ve en cuanto pasa, que es justo lo que da
        // ponerle prop; el arreglo, si aparece, es que el servidor mande el ancla como hace con el
        // atrezo, y eso ya sería wire.

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
            int bestChair = -1, bestStand = -1, bestPhone = -1;
            ulong bestChairH = ulong.MaxValue, bestStandH = ulong.MaxValue, bestPhoneH = ulong.MaxValue;

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
                // La impresora se apoya en algo con SUPERFICIE LIBRE, y de eso hay dos: el armario
                // (0,79 m de alto, contra la pared, nada encima) y la caja (0,29). La estantería
                // NO: son 5 m de balda contra el muro y una impresora en una balda es un error de
                // colocación evidente. Es la razón de que esto no sea el mismo conjunto que
                // `storage`, que sólo cuenta para decidir que la sala ES un archivo.
                if ((p.kind == PropCabinet || p.kind == PropBox) && h < bestStandH)
                {
                    bestStandH = h; bestStand = i;
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
            if (falseCeiling) AddVents(room, areaM2, floorM, into);

            // 2. CUBÍCULOS — mesas y sillas en cantidad. Una silla cruje, y solo una: el
            //    presupuesto no da para que la sala entera se asiente a la vez, y con dos
            //    fuentes iguales a tres metros deja de sonar a mueble y suena a bucle.
            bool cubicles = desks >= 3 && chairs >= 3;
            if (cubicles && bestChair >= 0)
            {
                PropSpec c = props[bestChair];
                AddEpisodic(worldSeed, into, Kind.ChairCreak, SaltCreak, 1f,
                    new Vector3(c.xCm * 0.01f, c.yCm * 0.01f + 0.50f, c.zCm * 0.01f), c.yawDeg,
                    c.xCm, c.zCm, room.floorYCm);
            }

            // 3. ARCHIVO — armarios y cajas sin puestos de trabajo. La impresora se apoya ENCIMA
            //    del mueble, y por eso hace falta un mueble con superficie: sin él no hay
            //    impresora, que es mejor que una impresora flotando a 90 cm de nada.
            if (!cubicles && storage >= 3 && desks <= 1 && bestStand >= 0)
            {
                PropSpec s = props[bestStand];
                float top = s.kind == PropCabinet ? CabinetTopM : BoxTopM;
                AddEpisodic(worldSeed, into, Kind.Printer, SaltPrinter, PrinterChance,
                    new Vector3(s.xCm * 0.01f, s.yCm * 0.01f + top, s.zCm * 0.01f), s.yawDeg,
                    s.xCm, s.zCm, room.floorYCm);
            }

            // 4. DESPACHO — uno o dos puestos, con teléfono, y pequeño. Es la sala que se
            //    oye desde fuera; por eso su alcance es el largo de la tabla.
            if (!cubicles && desks >= 1 && desks <= 2 && bestPhone >= 0 && areaM2 <= 60f)
            {
                PropSpec p = props[bestPhone];
                AddEpisodic(worldSeed, into, Kind.Phone, SaltPhone, PhoneChance,
                    new Vector3(p.xCm * 0.01f, p.yCm * 0.01f + 0.02f, p.zCm * 0.01f), p.yawDeg,
                    p.xCm, p.zCm, room.floorYCm);
            }
        }

        /// <summary>Lo que la rejilla de aire respeta hasta la pared: una placa.</summary>
        private const float VentClearanceM = 0.6f;

        /// <summary>Metros cuadrados por rejilla. Una sola rejilla en una planta diáfana de
        /// 25 × 25 no se oye desde ninguna parte: el alcance son 9 m.</summary>
        private const float VentAreaPerUnitM2 = 80f;

        /// <summary>Tope de rejillas por sala. Son continuas y compiten por el presupuesto de
        /// seis fuentes: una sala no puede quedárselo entero.</summary>
        private const int MaxVentsPerRoom = 4;

        /// <summary>
        /// Las rejillas de aire de una sala con falso techo, EN LOS HUECOS de la retícula de
        /// luminarias.
        ///
        /// Antes esto era «el centro de la sala, más media retícula si cabe», y era a ojo: acertaba
        /// el hueco cuando la cuenta de paneles salía par y lo fallaba cuando salía impar, porque el
        /// origen de la retícula depende del sobrante de la sala, no de su centro. Ahora los huecos
        /// los da <see cref="Wg3CeilingGrid"/>, que es la MISMA función que coloca los paneles: no
        /// pueden discrepar.
        ///
        /// Con una sola luminaria por eje no hay hueco en ese eje y la rejilla se centra, que es lo
        /// que se hace en un despacho pequeño de verdad.
        /// </summary>
        private static void AddVents(RoomSpec room, float areaM2, float floorM, List<Emitter> into)
        {
            float sizeX = room.sizeXCm * 0.01f, sizeZ = room.sizeZCm * 0.01f;
            Wg3CeilingGrid.Solve(sizeX, sizeZ,
                out float pitch, out int cx, out int cz, out float ox, out float oz);

            int gx = Wg3CeilingGrid.GapCount(cx), gz = Wg3CeilingGrid.GapCount(cz);

            // UN SOLO PANEL EN TODA LA SALA. Sin hueco en ningún eje, centrarse es caer JUSTO
            // encima de la luminaria — pasa en cualquier despacho de 3 × 3, que son muchos. Ahí la
            // rejilla se aparta a un lado del panel. Con hueco en al menos un eje no hace falta:
            // la rejilla ya pasa entre dos luminarias por ese eje, y centrarse en el otro es
            // exactamente lo que se quiere.
            bool asideOfPanel = gx == 0 && gz == 0;
            var xs = AxisSlots(gx, ox, pitch, sizeX, asideOfPanel);
            var zs = AxisSlots(gz, oz, pitch, sizeZ, asideOfPanel);
            if (xs.Count == 0 || zs.Count == 0) return; // sala demasiado justa: sin rejilla

            int slots = xs.Count * zs.Count;
            int want = Mathf.Clamp(Mathf.RoundToInt(areaM2 / VentAreaPerUnitM2), 1, MaxVentsPerRoom);
            int n = Mathf.Min(want, slots);

            float y = floorM + room.heightCm * 0.01f; // el pivote de la rejilla es su cara SUPERIOR
            for (int i = 0; i < n; i++)
            {
                // Repartidas por el índice aplanado, no consecutivas: dos rejillas pegadas dejan
                // media sala sin aire y suenan como una.
                int k = Mathf.Min(slots - 1, (int)((i + 0.5f) * slots / n));
                into.Add(new Emitter
                {
                    position = new Vector3(
                        room.xCm * 0.01f + xs[k / zs.Count],
                        y,
                        room.zCm * 0.01f + zs[k % zs.Count]),
                    kind = Kind.AirCon,
                    yawDeg = 0f,
                    period = 0f,
                    phase01 = 0f,
                });
            }
        }

        /// <summary>Media luminaria (0,6 de su lado largo) más media rejilla (0,3): lo que hay que
        /// apartarse de un panel para no tocarlo.</summary>
        private const float PanelClearM = 0.9f;

        // Los puntos utilizables de un eje. Se descarta lo que quede a menos de una placa de la
        // pared, y una lista vacía significa «aquí no cabe rejilla», no «ponla donde sea».
        private static List<float> AxisSlots(int gaps, float origin, float pitch, float size,
            bool asideOfPanel)
        {
            var slots = new List<float>(Mathf.Max(2, gaps));
            if (gaps <= 0)
            {
                // Sin hueco en el eje: o el centro del único panel, o a un lado de él.
                if (asideOfPanel)
                {
                    Add(slots, origin - PanelClearM, size);
                    Add(slots, origin + PanelClearM, size);
                }
                else
                {
                    Add(slots, size * 0.5f, size);
                }
                return slots;
            }
            for (int i = 0; i < gaps; i++) Add(slots, Wg3CeilingGrid.GapAt(origin, pitch, i), size);
            if (slots.Count == 0) Add(slots, size * 0.5f, size);
            return slots;
        }

        private static void Add(List<float> slots, float v, float size)
        {
            if (v >= VentClearanceM && v <= size - VentClearanceM) slots.Add(v);
        }

        /// <summary>Cara superior del armario (`Cupboard`, 0,89 × 0,79 × 0,46) y de la caja de
        /// cartón (0,40 × 0,29 × 0,29), medidas de su BoxCollider. La impresora se apoya ahí.</summary>
        private const float CabinetTopM = 0.79f;
        private const float BoxTopM = 0.29f;

        private static void AddEpisodic(int worldSeed, List<Emitter> into, Kind kind, uint salt,
            float chance, Vector3 position, float yawDeg, int xCm, int zCm, int floorYCm)
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
                yawDeg = yawDeg,
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
            public Kind kind;
            public SlotMode mode;
            public float target;   // volumen destino del continuo, SIN la oclusión
            public float busyUntil; // reloj local hasta el que el one-shot ocupa el hueco
            public AudioLowPassFilter lowPass; // la pared de por medio
            public float occlusion;    // OBJETIVO que fija la sonda: 0 a la vista, 1 tapada
            public float occlusionNow; // el suavizado, que es el que se oye
        }

        // ── Oclusión ────────────────────────────────────────────────────────────
        //
        // Mismo diseño que el zumbido, y aquí importa MÁS: el teléfono alcanza 18 m, o sea que casi
        // siempre suena desde otra sala. Sin filtro, un timbre a 15 m con dos tabiques por medio
        // llega tan nítido como si estuviera en la mesa de al lado, y eso destruye justo la
        // sensación que el alcance largo existe para dar.
        //
        // Se filtra Y se baja: un paso-bajo solo sigue leyéndose como cercano.

        private const float CutoffOpen = 22000f;
        private const float CutoffOccluded = 900f;
        private const float OcclusionTau = 0.25f;  // cruzar un vano no da un salto
        private const float OccludedVolume = 0.45f;

        /// <summary>Una sonda por FRAME rotando entre las seis fuentes: el coste queda plano en
        /// vez de en picos, y cada fuente se revisa ~10 veces por segundo a 60 fps.</summary>
        private int _occlusionCursor;

        /// <summary>
        /// Máscara de capas contra la que se sonda la oclusión. **La pone el llamante** (el
        /// streaming, con <c>GridChunkBuilder.GeoMask</c>); a 0 no se sonda y todo suena abierto.
        ///
        /// Es un campo y no una referencia directa a <c>GridChunkBuilder</c> a propósito: el audio
        /// no tiene por qué saber cómo se llaman las capas del worldgen, y esa dependencia
        /// arrastraba las seis partes de una clase parcial hasta cualquier arnés que quisiera
        /// compilar este fichero sin Unity. La capa que decide qué es «pared» es del mundo, no del
        /// sonido.
        /// </summary>
        public static int GeometryMask { get; set; }

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

                // Un paso-bajo POR FUENTE y no uno global: puedes tener la rejilla a la vista y el
                // teléfono detrás de un tabique en el mismo instante.
                var lp = go.AddComponent<AudioLowPassFilter>();
                lp.cutoffFrequency = CutoffOpen;

                _slots[i] = new Slot { src = src, tr = go.transform, lowPass = lp };
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

        /// <summary>
        /// R6 (12-09) — ya NO es un corte por distancia. Antes era |dy| ≤ 2,6 m, pensado para
        /// impedir que un teléfono con 18 m de alcance sonara desde el piso de arriba, pero con la
        /// planta de WG3 a 3,32 m ese número tenía dos fallos: una fuente cerca del TECHO de tu
        /// propia planta (a más de 2,6 m del oído) se cortaba aunque estuvierais en la misma sala, y
        /// una fuente pegada al SUELO de la planta de arriba (a menos de 2,6 m del oído si tú estás
        /// cerca del techo de la tuya) sonaba a través de la losa. El corte real es el mismo índice
        /// de planta que ya reparte la luz — <see cref="Wg3StoreyLayers.RawStoreyOf"/> — comparado
        /// entre la fuente y el oyente, no una distancia.
        /// </summary>
        private static bool SamePlanta(float sourceY, float earY) =>
            Wg3StoreyLayers.RawStoreyOf(sourceY) == Wg3StoreyLayers.RawStoreyOf(earY);

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

        /// <summary>Qué hacer con la cita de un emisor episódico.</summary>
        public enum ScheduleAction : byte
        {
            /// <summary>Todavía no toca.</summary>
            Wait = 0,
            /// <summary>Toca: compite por un hueco.</summary>
            Fire = 1,
            /// <summary>La cita está podrida: se reprograma SIN sonar.</summary>
            Resync = 2,
        }

        /// <summary>
        /// La regla del horario, aparte del bucle para poder probarla sin escena.
        ///
        /// HORARIO CADUCADO, y es un fallo real que tuvo el sistema: un emisor fuera de alcance no
        /// se mira, así que su cita se queda en el pasado mientras el jugador está lejos. Al entrar
        /// en la sala el suceso estaba vencido y sonaba EN EL ACTO — y siempre, cada vez. Un
        /// teléfono que suena cada vez que cruzas la puerta no es un suceso, es un disparador.
        ///
        /// El umbral es UN periodo entero: por debajo, un retraso normal (el reparto no encontró
        /// hueco, o hubo un tirón de frames) sigue sonando; por encima, la cita es de otra época y
        /// se tira.
        /// </summary>
        public static ScheduleAction ActionFor(float now, float nextAt, float period)
        {
            if (period <= 0f) return ScheduleAction.Wait;
            if (now - nextAt > period) return ScheduleAction.Resync;
            return now >= nextAt ? ScheduleAction.Fire : ScheduleAction.Wait;
        }

        /// <summary>La cita nueva tras un <see cref="ScheduleAction.Resync"/>: la misma fase
        /// determinista del emisor, contada desde ahora.</summary>
        public static float ResyncAt(float now, float period, float phase01) =>
            now + phase01 * period;

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

            StepOcclusionProbe();
            DriveSlots(dt, now);
        }

        /// <summary>
        /// Una sonda por frame, rotando. Contra la geometría del mundo y nada más: ni el atrezo, ni
        /// los jugadores, ni el propio rig deben tapar una fuente, y <c>Ignore</c> evita que un
        /// volumen de disparo cuente como pared.
        /// </summary>
        private void StepOcclusionProbe()
        {
            if (_listener == null || GeometryMask == 0) return;
            _occlusionCursor = (_occlusionCursor + 1) % _slots.Length;
            Slot slot = _slots[_occlusionCursor];
            if (slot.mode == SlotMode.Idle) { slot.occlusion = 0f; return; }

            slot.occlusion = Physics.Linecast(_listener.position, slot.tr.position,
                GeometryMask, QueryTriggerInteraction.Ignore) ? 1f : 0f;
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
                    if (!SamePlanta(e.position.y, ear.y)) continue; // R6 — aislamiento entre plantas
                    float dist = d.magnitude;
                    if (dist > KindMaxDistance[k]) continue;

                    long key = ((long)batch.id << EmitterIndexBits) | (uint)(i & EmitterIndexMask);
                    var cand = new Candidate
                    {
                        key = key, distance = dist, position = e.position,
                        kind = e.kind, batch = b, index = i,
                    };

                    if (e.period <= 0f) { _loops.Add(cand); continue; }

                    switch (ActionFor(now, batch.nextAt[i], e.period))
                    {
                        case ScheduleAction.Resync:
                            batch.nextAt[i] = ResyncAt(now, e.period, e.phase01);
                            break;
                        case ScheduleAction.Fire:
                            _due.Add(cand);
                            break;
                    }
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

                int slot = FreeOrLoopSlot();
                if (slot < 0) continue;
                StartOneShot(_slots[slot], c, now);
            }

            // Y los continuos con lo que sobre, por cercanía.
            //
            // PRIMERO se refresca lo que ya suena y sólo DESPUÉS se llenan huecos, y ese orden es
            // el arreglo de dos fallos: (a) un continuo que empezó a fundirse y vuelve a estar en
            // alcance se quedaba mudo hasta terminar de apagarse, porque nada le devolvía el
            // volumen; (b) `MasterVolume` se leía sólo al arrancar la fuente, así que moverlo en
            // vivo no tocaba nada que ya estuviera sonando.
            _loops.Sort(ByDistance);
            for (int s = 0; s < _slots.Length; s++)
            {
                Slot slot = _slots[s];
                if (slot.mode != SlotMode.Loop) continue;
                slot.target = StillCandidate(slot.key) ? KindVolume[(int)slot.kind] * _masterVolume : 0f;
            }

            for (int i = 0; i < _loops.Count; i++)
            {
                Candidate c = _loops[i];
                if (HoldsKey(c.key)) continue;
                int slot = FreeSlot();
                if (slot < 0) break; // presupuesto agotado: los demás no suenan, y es el diseño
                StartLoop(_slots[slot], c);
            }
        }

        private bool HoldsKey(long key)
        {
            for (int s = 0; s < _slots.Length; s++)
                if (_slots[s].mode == SlotMode.Loop && _slots[s].key == key) return true;
            return false;
        }

        private bool StillCandidate(long key)
        {
            for (int i = 0; i < _loops.Count; i++) if (_loops[i].key == key) return true;
            return false;
        }

        private int FreeSlot()
        {
            for (int s = 0; s < _slots.Length; s++)
                if (_slots[s].mode == SlotMode.Idle) return s;
            return -1;
        }

        // Para un episódico: primero un hueco libre, y si no hay, el continuo MÁS LEJANO.
        private int FreeOrLoopSlot()
        {
            int free = FreeSlot();
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
            slot.kind = c.kind;
            slot.mode = SlotMode.Loop;
            slot.tr.position = c.position;
            slot.src.clip = clip;
            slot.src.loop = true;
            slot.src.minDistance = KindMinDistance[k];
            slot.src.maxDistance = KindMaxDistance[k];
            slot.src.volume = 0f;
            slot.target = KindVolume[k] * _masterVolume;
            SnapOcclusion(slot);
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
            slot.kind = c.kind;
            slot.mode = SlotMode.OneShot;
            slot.tr.position = c.position;
            slot.src.minDistance = KindMinDistance[k];
            slot.src.maxDistance = KindMaxDistance[k];
            slot.src.volume = 1f; // el nivel va en el PlayOneShot, no aquí
            slot.target = 0f;
            slot.busyUntil = now + clip.length + 0.05f;
            SnapOcclusion(slot);
            slot.src.volume = Mathf.Lerp(1f, OccludedVolume, slot.occlusionNow);
            slot.src.PlayOneShot(clip, KindVolume[k] * _masterVolume);
        }

        /// <summary>
        /// Mide la oclusión YA y sin suavizar, al ocupar el hueco.
        ///
        /// Sin esto, un hueco hereda el estado del inquilino anterior: un timbre que empieza al
        /// otro lado de una pared sonaría abierto durante el primer cuarto de segundo —justo el
        /// ataque, que es lo que se oye— y sólo después se cerraría. Y al revés, uno a la vista
        /// entraría filtrado. Una sonda por suceso, no por frame.
        /// </summary>
        private void SnapOcclusion(Slot slot)
        {
            slot.occlusion = _listener != null && GeometryMask != 0 && Physics.Linecast(
                _listener.position, slot.tr.position,
                GeometryMask, QueryTriggerInteraction.Ignore) ? 1f : 0f;
            slot.occlusionNow = slot.occlusion;
            slot.lowPass.cutoffFrequency = Mathf.Lerp(CutoffOpen, CutoffOccluded, slot.occlusionNow);
        }

        private void DriveSlots(float dt, float now)
        {
            float step = dt / LoopFadeSeconds;
            for (int s = 0; s < _slots.Length; s++)
            {
                Slot slot = _slots[s];

                // El suavizado va aquí y no en la sonda porque la sonda solo toca UNA fuente por
                // frame: sin esto, cruzar un vano daría un escalón de filtro y de volumen.
                slot.occlusionNow = Mathf.Lerp(slot.occlusionNow, slot.occlusion,
                    Mathf.Clamp01(dt / OcclusionTau));
                slot.lowPass.cutoffFrequency =
                    Mathf.Lerp(CutoffOpen, CutoffOccluded, slot.occlusionNow);
                float duck = Mathf.Lerp(1f, OccludedVolume, slot.occlusionNow);

                switch (slot.mode)
                {
                    case SlotMode.Loop:
                        slot.src.volume = Mathf.MoveTowards(slot.src.volume, slot.target * duck, step);
                        if (slot.target <= 0f && slot.src.volume <= 0f)
                        {
                            slot.src.Stop();
                            slot.src.clip = null;
                            slot.mode = SlotMode.Idle;
                            slot.key = NoKey;
                        }
                        break;

                    case SlotMode.OneShot:
                        // El nivel del one-shot va en el PlayOneShot; `volume` es el multiplicador
                        // que sí se puede mover con el suceso ya sonando, y es por donde entra la
                        // oclusión de un timbre que empieza a la vista y acaba tras una puerta.
                        slot.src.volume = duck;
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

            // EL RUMBLE ES EL ACOMPAÑAMIENTO, NO EL SONIDO. La primera versión ponía aquí 0,30 y
            // 0,18 y filtraba el ruido a 440 Hz: el resultado era un retumbe con casi toda la
            // energía por debajo de 100 Hz — en unos altavoces de portátil, silencio, y en cascos,
            // un motor, no una rejilla. Una salida de aire real es sobre todo BANDA ANCHA.
            float[] hz = { 24f, 48f, 96f };
            float[] amp = { 0.10f, 0.06f, 0.03f };
            for (int p = 0; p < hz.Length; p++)
            {
                double w = TwoPi * hz[p] / sampleRate;
                for (int i = 0; i < sc; i++) buf[i] += (float)(amp[p] * Math.Sin(w * i));
            }

            int fade = Mathf.Min(2048, sc / 4);
            int total = sc + fade;
            var air = new float[total];
            var rng = new System.Random(90210);
            // Ruido de BANDA, 180–2500 Hz: paso-alto de un polo que quita el barro y paso-bajo que
            // quita el filo del siseo. Lo de abajo ya lo pone el rumble y lo de arriba suena a
            // estática, no a aire.
            const float HighPassA = 0.974f; // ≈180 Hz a 44,1 kHz
            const float LowPassK = 0.356f;  // ≈2 500 Hz
            float hpIn = 0f, hpOut = 0f, lp = 0f;
            for (int i = 0; i < total; i++)
            {
                float x = (float)(rng.NextDouble() * 2.0 - 1.0);
                hpOut = HighPassA * (hpOut + x - hpIn);
                hpIn = x;
                lp += LowPassK * (hpOut - lp);
                // Turbulencia: medio hercio, o sea UN ciclo exacto en los dos segundos del bucle.
                // Sin ella el soplido es una máscara de ruido plana y el oído la deja de oír.
                float wobble = 1f + 0.18f * (float)Math.Sin(TwoPi * 0.5 * i / sampleRate);
                air[i] = lp * 2.6f * wobble;
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
            var rng = new System.Random(1997);

            // UN BADAJO GOLPEANDO DOS CAMPANAS, no dos senos multiplicados por un seno. La primera
            // versión era exactamente eso —1 000 y 1 250 Hz por un trémolo de 20 Hz— y sonaba a
            // tono de prueba: en un timbre real lo que se reconoce es el GOLPE, un ataque
            // instantáneo con cola, no una amplitud que sube y baja suave. El interruptor da 20
            // ciclos por segundo y cada medio ciclo el badajo cambia de campana: 40 golpes.
            //
            // Y cada campana SIGUE SONANDO mientras golpean la otra: los golpes van cada 25 ms y la
            // cola dura 45, así que se solapan sin fundirse en una nota plana. El número está
            // medido entre dos fallos opuestos: con la cola cortada a la duración del golpe el
            // factor de cresta se dispara a 8,6 —nueve decibelios de RMS perdidos para el mismo
            // pico, y el timbre queda flaco—, y con 130 ms el pulso de cada golpe desaparece y
            // vuelve a sonar a tono continuo. A 45 ms: cresta 3,5 y el golpe se sigue oyendo.
            const double StrikesPerSecond = 40.0;
            const float RingTauSeconds = 0.045f;

            // Parciales INARMÓNICOS: es lo que distingue el metal de un tubo. Una campana no tiene
            // armónicos enteros, y con ellos suena a órgano.
            float[] partialRatio = { 1f, 2.76f, 5.40f };
            float[] partialAmp = { 1.00f, 0.26f, 0.11f };

            float decayPerSample = Mathf.Exp(-1f / (RingTauSeconds * sampleRate));
            float ringA = 0f, ringB = 0f; // la energía viva de cada campana
            int lastStrike = -1;

            for (int i = 0; i < sc; i++)
            {
                float t = (float)i / sampleRate;

                ringA *= decayPerSample;
                ringB *= decayPerSample;

                // Ráfagas: [0,00–1,00] y [1,50–2,50]. En el silencio las campanas se apagan solas
                // en vez de cortarse, que es lo que hace un timbre cuando el interruptor abre.
                bool ringing = t < 1.0f || t >= 1.5f;
                double strike = t * StrikesPerSecond;
                int index = (int)strike;
                if (ringing && index != lastStrike)
                {
                    lastStrike = index;
                    if ((index & 1) == 0) ringA = 1f; else ringB = 1f;
                }
                float inStrike = (float)(strike - index);

                // Bordes suavizados: un corte seco es un click.
                float local = t < 1.0f ? t : t - 1.5f;
                float env = ringing
                    ? Mathf.Clamp01(local / 0.01f) * Mathf.Clamp01((1.0f - local) / 0.04f)
                    : 1f;

                double tone = 0.0;
                for (int p = 0; p < partialRatio.Length; p++)
                {
                    double w = TwoPi * partialRatio[p] * i / sampleRate;
                    tone += partialAmp[p] * (ringA * Math.Sin(w * 1000.0)
                                             + ringB * Math.Sin(w * 1250.0));
                }

                // El badajo tocando el metal: unos milisegundos de ruido en el ataque de cada
                // golpe. Sin él el golpe es limpio y vuelve a sonar sintetizado.
                float clapper = ringing && inStrike < 0.12f
                    ? (float)(rng.NextDouble() * 2.0 - 1.0) * (0.12f - inStrike) * 1.6f
                    : 0f;

                buf[i] = (float)tone * 0.55f * env + clapper * env;
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
