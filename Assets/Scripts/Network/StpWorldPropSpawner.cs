using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.WorldGen3;
using PolymindGames.InventorySystem;
using PolymindGames.ResourceHarvesting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-114 D9 — siembra los muebles DESMONTABLES del mundo: escritorio, estantería y silla.
    ///
    /// Gemelo de <see cref="StpWorldContainerSpawner"/> y a propósito separado de él: aquel siembra
    /// contenedores y éste desmontables, con **sorteo y sal propios**, porque compartirlos movería
    /// `ContainerChance` — un número de balance ya en juego — y pondría los dos muebles siempre en
    /// la misma esquina.
    ///
    /// **Corre en TODOS los clientes, no sólo en el host.** El mueble en sí no viaja por el cable
    /// (ADR-114 D3): el sorteo es determinista, así que cada cliente instancia el suyo en el mismo
    /// sitio. Lo único que es del host es REGISTRARLO — dar de alta su id y su salud en el backend,
    /// que es quien manda sobre `remaining` (D1). Un joiner instancia el mueble y espera a que el
    /// roster le diga qué id tiene, por el mismo emparejamiento por proximidad que ya usan los
    /// árboles.
    ///
    /// **Este componente no sabe nada de herramientas.** La puerta del destornillador la mira el
    /// vendor en el cliente que golpea (`HarvestPower` contra `_requiredPower`, ADR-114 D6), y por
    /// eso el backend nunca aprende qué herramienta se usó: ADR-023 queda intacto.
    /// </summary>
    public sealed class StpWorldPropSpawner : MonoBehaviour
    {
        private static StpWorldPropSpawner _instance;

        public string gameplayScene = "STP_Showcase";
        [Min(0f)] public float warmupSeconds = 3f;

        /// <summary>Anillo 3×3, el mismo que el loot suelto y los contenedores: sembrar más lejos
        /// gasta rayos en columnas cuyo suelo no está dibujado.</summary>
        private const int RingRadius = 1;
        private const float ScanIntervalSeconds = 2f;
        private const float PlacementSearchRadius = 6f;

        /// <summary>Dónde viven los prefabs de mueble. **Todavía no existen**: son 3 de los 7
        /// assets que ADR-114 deja para una sola pasada de editor. Hasta que estén, esto avisa una
        /// vez por prop y no siembra — la misma tolerancia a catálogo a medio autorar que ya tienen
        /// los otros sembradores.</summary>
        private const string PropResourcePath = "Props/Dismantle/";

        private readonly HashSet<(int cx, int cz)> _settled = new HashSet<(int cx, int cz)>();
        private readonly Dictionary<DismantleProp, GameObject> _prefabs =
            new Dictionary<DismantleProp, GameObject>();
        private readonly HashSet<DismantleProp> _warned = new HashSet<DismantleProp>();

        /// <summary>SÓLO PLAYTEST — estado del almacén de muebles por celda de 200 m
        /// (<see cref="ChunkDepotRoll"/>): hasta qué candidato se ha mirado y si ya está decidido.
        /// Se vacía con la semilla, como <see cref="_settled"/>.</summary>
        private sealed class DepotState
        {
            public int NextCandidate;
            public bool Done;
        }

        private readonly Dictionary<(int cx, int cz), DepotState> _depots =
            new Dictionary<(int cx, int cz), DepotState>();

        private float _warmupEnd;
        private bool _warmedUp;
        private float _nextScanAt;
        /// <summary>La semilla con la que se llenó `_settled`. Sin esto, las columnas selladas en la
        /// partida anterior seguirían selladas en la siguiente —que es OTRO mundo— y nacería
        /// sub-sembrada sin que nada lo dijera. Mismo fallo que ya se corrigió en el sembrador de
        /// contenedores.</summary>
        private long _settledSeed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpWorldPropSpawner]");
            _instance = go.AddComponent<StpWorldPropSpawner>();
            DontDestroyOnLoad(go);
        }

        private void OnEnable() => _warmupEnd = Time.unscaledTime + warmupSeconds;

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void Update()
        {
            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            if (SceneManager.GetActiveScene().name != gameplayScene)
                return;

            // Sin WG3 no hay papeles que preguntar, y el papel es la puerta entera del sorteo.
            if (!ipc.Wg3Enabled)
                return;

            var streamer = Wg3ChunkStreamer.Active;
            if (streamer == null)
                return;

            if (!_warmedUp)
            {
                if (Time.unscaledTime < _warmupEnd)
                    return;
                _warmedUp = true;
            }

            if (Time.unscaledTime < _nextScanAt)
                return;
            _nextScanAt = Time.unscaledTime + ScanIntervalSeconds;

            var cam = Camera.main;
            if (cam == null)
                return;

            var state = ipc.LatestState;
            if (state == null || state.worldSeed == 0)
                return; // sin semilla, sembrar daría otro mundo
            long worldSeed = state.worldSeed;
            if (worldSeed != _settledSeed)
            {
                _settled.Clear();
                _depots.Clear();
                _settledSeed = worldSeed;
            }

            var init = NetworkInitializer.Instance;
            bool isHost = init != null && init.CurrentRole == NetworkInitializer.Role.Host;

            Scan(ipc, streamer, cam.transform.position, worldSeed, isHost);
            if (ChunkDepotRoll.DepotEnabled)
                ScanDepots(ipc, streamer, cam.transform.position, worldSeed, isHost);
        }

        /// <summary>
        /// SÓLO PLAYTEST — el almacén de muebles: en cada celda de 200 m alrededor del jugador
        /// (3×3), recorre los candidatos de <see cref="ChunkDepotRoll"/> EN ORDEN y acepta el
        /// primero cuyo espacio sea elegible y grande. Un candidato cuyo chunk no está montado
        /// detiene la celda —no se salta, porque saltarlo haría que dos clientes eligieran salas
        /// distintas según qué chunks hubieran visto—; se reintenta en el barrido siguiente.
        /// </summary>
        private void ScanDepots(IPCClient ipc, Wg3ChunkStreamer streamer, Vector3 around, long worldSeed,
            bool isHost)
        {
            int pcx = ChunkDepotRoll.CellOf(around.x);
            int pcz = ChunkDepotRoll.CellOf(around.z);

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    var cell = (cx: pcx + dx, cz: pcz + dz);
                    if (!_depots.TryGetValue(cell, out var st))
                    {
                        st = new DepotState();
                        _depots[cell] = st;
                    }
                    if (st.Done)
                        continue;

                    while (st.NextCandidate < ChunkDepotRoll.CandidateCount)
                    {
                        var c = ChunkDepotRoll.CandidateAt(worldSeed, cell.cx, cell.cz, st.NextCandidate);
                        // A la altura del jugador, como el sorteo normal: con plantas, una cota fija
                        // contestaría siempre por la baja.
                        var probe = new Vector3(c.X, around.y, c.Z);
                        if (!streamer.ChunkIsBuilt(probe))
                            break; // todavía no: se espera, no se salta

                        bool accepted = false;
                        if (streamer.TryGetSpace(probe, out Bounds box, out byte style) &&
                            ChunkDepotRoll.StyleIsEligible(style))
                        {
                            var slots = ChunkDepotRoll.Layout(worldSeed, cell.cx, cell.cz,
                                box.min.x, box.min.z, box.max.x, box.max.z);
                            if (slots.Count > 0)
                            {
                                PlaceDepot(ipc, cell.cx, cell.cz, box.min.y, slots, worldSeed, isHost);
                                accepted = true;
                            }
                        }

                        st.NextCandidate++;
                        if (accepted)
                        {
                            st.Done = true;
                            break;
                        }
                    }

                    if (!st.Done && st.NextCandidate >= ChunkDepotRoll.CandidateCount)
                    {
                        st.Done = true;
                        Debug.LogWarning($"[StpWorldPropSpawner] celda de almacén ({cell.cx},{cell.cz}): ninguno " +
                                         $"de los {ChunkDepotRoll.CandidateCount} candidatos cae en un espacio " +
                                         "elegible y grande; esta celda se queda sin almacén.");
                    }
                }
            }
        }

        /// <summary>Instancia los muebles del almacén a ras del suelo del espacio (la caja del
        /// espacio tiene el suelo en `min.y`) y, sólo en el host, los da de alta con id determinista
        /// por (celda, slot). Mismo camino que <see cref="Place"/> mueble a mueble.</summary>
        private void PlaceDepot(IPCClient ipc, int cellX, int cellZ, float floorY,
            List<ChunkDepotRoll.Slot> slots, long worldSeed, bool isHost)
        {
            var sync = StpHarvestableSyncManager.Instance;
            int placed = 0, registered = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                var prefab = PrefabFor(s.Prop);
                if (prefab == null)
                    continue;

                var at = new Vector3(s.X, floorY, s.Z);
                var go = Instantiate(prefab, at, Quaternion.Euler(0f, s.Rotation, 0f));
                go.name = $"[Depot]{s.Prop}_{cellX}_{cellZ}_{i}";
                placed++;

                var hr = go.GetComponent<HarvestableResource>();
                if (hr == null || !isHost || sync == null)
                    continue;

                var drops = ResolveMaterials(s.Prop);
                if (drops.Count == 0)
                    continue;

                uint id = ChunkDepotRoll.NetIdFor(worldSeed, cellX, cellZ, i);
                if (sync.RegisterHostProp(ipc, id, hr, drops))
                    registered++;
            }

            Debug.Log($"[StpWorldPropSpawner] almacén de muebles en la celda ({cellX},{cellZ}): " +
                      $"{placed} muebles a cota {floorY:F2}, {registered} registrados.");
        }

        private void Scan(IPCClient ipc, Wg3ChunkStreamer streamer, Vector3 around, long worldSeed,
            bool isHost)
        {
            float side = Wg3ChunkStreamer.ChunkSize;
            int px = Mathf.FloorToInt(around.x / side);
            int pz = Mathf.FloorToInt(around.z / side);

            for (int dx = -RingRadius; dx <= RingRadius; dx++)
            {
                for (int dz = -RingRadius; dz <= RingRadius; dz++)
                {
                    var col = (cx: px + dx, cz: pz + dz);
                    if (_settled.Contains(col))
                        continue;

                    // El rayo del sorteo va a la altura del jugador: con plantas, una cota fija
                    // contestaría siempre por la baja.
                    float rayY = around.y;
                    Vector3 centre = Vector3.zero;
                    bool got = ChunkDismantleRoll.RollPropByStyle(
                        worldSeed, col.cx, col.cz,
                        (u, v) =>
                        {
                            centre = new Vector3((col.cx + u) * side, rayY, (col.cz + v) * side);
                            if (!streamer.TryGetStyle(centre, out byte st)) return (byte?)null;
                            return st;
                        },
                        out var entry, out bool spaceKnown);

                    if (!spaceKnown)
                    {
                        // Con el chunk montado y sin NADA en esa vertical a ninguna cota, la
                        // respuesta ya es definitiva. Si hay espacio en otra planta, se espera.
                        if (streamer.ChunkIsBuilt(centre) && !streamer.AnySpaceInColumn(centre))
                            _settled.Add(col);
                        continue;
                    }

                    if (!got)
                    {
                        _settled.Add(col); // este chunk no lleva mueble desmontable
                        continue;
                    }

                    if (!LootPlacement.TryFindWalkablePoint(centre, 0f, PlacementSearchRadius, out Vector3 at))
                        continue; // el suelo aún no está dibujado ahí: se reintenta

                    if (Place(ipc, entry, at, worldSeed, col.cx, col.cz, isHost))
                        _settled.Add(col);
                }
            }
        }

        private bool Place(IPCClient ipc, ChunkDismantleRoll.Entry entry, Vector3 at, long worldSeed,
            int cx, int cz, bool isHost)
        {
            var prefab = PrefabFor(entry.Prop);
            if (prefab == null)
                return true; // sin prefab no habrá mueble nunca: se sella y no se reintenta

            var go = Instantiate(prefab, at, Quaternion.Euler(0f, entry.Rotation, 0f));
            go.name = $"[Dismantle]{entry.Prop}_{cx}_{cz}";

            var hr = go.GetComponent<HarvestableResource>();
            if (hr == null)
            {
                Debug.LogWarning($"[StpWorldPropSpawner] el prefab de {entry.Prop} no lleva " +
                                 "HarvestableResource en la RAÍZ; no se puede desmontar.");
                return true;
            }

            // Sólo el host da de alta la salud: es la autoridad (D1). El joiner deja el mueble
            // instanciado y el emparejamiento por proximidad del sync manager le pondrá el id
            // cuando llegue en el roster.
            if (!isHost)
                return true;

            var sync = StpHarvestableSyncManager.Instance;
            if (sync == null)
                return true;

            uint id = ChunkDismantleRoll.NetIdFor(worldSeed, cx, cz);
            var drops = ResolveMaterials(entry.Prop);
            if (drops.Count == 0)
            {
                // Un mueble que no suelta nada se desmontaría en balde. Se deja plantado igual
                // (es mobiliario), pero no se registra: sin registro no hay golpe que contar.
                return true;
            }

            if (sync.RegisterHostProp(ipc, id, hr, drops))
            {
                Debug.Log($"[StpWorldPropSpawner] {entry.Prop} en chunk ({cx},{cz}) a {at:F1}, " +
                          $"id={id}, {drops.Count} material(es).");
            }
            return true;
        }

        /// <summary>Nombres de la tabla de ADR-114 D8 → ids de definición. Un material que aún no
        /// está autorado se salta con aviso, misma tolerancia que el resto de sembradores: un
        /// catálogo a medio hacer no debe impedir que caiga el resto.</summary>
        private static List<NetworkHarvestableInstance.ItemDrop> ResolveMaterials(DismantleProp prop)
        {
            var drops = new List<NetworkHarvestableInstance.ItemDrop>();
            foreach (var m in ChunkDismantleRoll.MaterialsFor(prop))
            {
                var def = ItemDefinition.GetWithName(m.Name);
                if (def == null)
                {
                    Debug.LogWarning($"[StpWorldPropSpawner] material '{m.Name}' no está en la base " +
                                     "de definiciones; saltado.");
                    continue;
                }
                drops.Add(new NetworkHarvestableInstance.ItemDrop { defId = def.Id, count = m.Count });
            }
            return drops;
        }

        private GameObject PrefabFor(DismantleProp prop)
        {
            if (_prefabs.TryGetValue(prop, out var cached))
                return cached;

            var loaded = Resources.Load<GameObject>(PropResourcePath + prop);
            _prefabs[prop] = loaded;
            if (loaded == null && _warned.Add(prop))
            {
                Debug.LogWarning($"[StpWorldPropSpawner] falta el prefab '{PropResourcePath}{prop}'. " +
                                 "Es uno de los assets que ADR-114 deja para la pasada de editor; " +
                                 "hasta que exista, ese mueble no se siembra.");
            }
            return loaded;
        }
    }
}
