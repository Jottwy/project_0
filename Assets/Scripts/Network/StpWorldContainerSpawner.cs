using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.WorldGen3;
using PolymindGames.InventorySystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// El último eslabón de la cadena worldgen → prop → contenedor → tabla → loot: coge lo que
    /// <see cref="ChunkContainerRoll"/> decide (puro, determinista, ya probado headless) y lo
    /// convierte en un contenedor de verdad en el mundo.
    ///
    /// **No construye ningún contenedor nuevo: siembra un COFRE de ADR-028.** El backend ya trae
    /// todo lo que un contenedor necesita y que este trabajo no tiene que escribir — qué queda
    /// dentro, quién saqueó, la guarda contra dos jugadores abriendo a la vez, la persistencia, el
    /// despawn al vaciarse y `chest_already_seeded`, que impide sembrar dos veces en el mismo sitio
    /// incluso después de reiniciar. Lo único que cambia respecto a <see cref="StpChestSpawner"/>
    /// es QUIÉN elige el sitio y el contenido: allí es un puñado alrededor del host al arrancar,
    /// aquí es el papel del espacio de WG3, chunk a chunk, según el jugador explora.
    ///
    /// Va aparte de `ChunkLootManager` a propósito: aquel reparte el loot SUELTO con números que
    /// Joel ya validó en partida, y meterle otra responsabilidad dentro es la forma más barata de
    /// mover sin querer un balance que está dado por bueno. Este componente se autoarranca y se
    /// puede borrar entero sin tocar nada más, igual que `StpChestSpawner`.
    ///
    /// Estructura copiada de `StpChestSpawner` porque el problema es el mismo: puerta de host,
    /// calentamiento, y reintento mientras el suelo de esa columna todavía no está dibujado.
    /// </summary>
    public sealed class StpWorldContainerSpawner : MonoBehaviour
    {
        private static StpWorldContainerSpawner _instance;

        public string gameplayScene = "STP_Showcase";
        [Min(0f)] public float warmupSeconds = 3f;

        /// <summary>Radio de siembra en chunks alrededor del jugador. 1 = el anillo 3×3, el mismo
        /// que ya carga el loot suelto: sembrar más lejos gastaría intentos en columnas cuyo suelo
        /// no está dibujado, que es justo lo que el rayo no puede contestar.</summary>
        private const int RingRadius = 1;

        /// <summary>Cada cuánto se barre el anillo. No hay prisa: un mueble que aparece un segundo
        /// más tarde no se nota, y barrer cada frame gastaría rayos para nada.</summary>
        private const float ScanIntervalSeconds = 2f;

        /// <summary>Cuánto se puede alejar el mueble del punto sorteado buscando suelo. Es la misma
        /// idea que el reintento del loot suelto: el sorteo dice la esquina, el rayo dice el
        /// centímetro.</summary>
        private const float PlacementSearchRadius = 6f;

        /// <summary>Espacio de ids propio para el `request_id`, fuera del de `StpChestSpawner`
        /// ("CHEST" &lt;&lt; 8) y del pequeño incremental de `world_interact`, que comparten el
        /// mismo `processed_interactions` en el servidor. Un choque de id ahí no da error: hace
        /// que el segundo cofre se descarte como duplicado y no aparezca nunca.</summary>
        private const long RequestIdBase = 0x43_4F_4E_54_41_49L << 8; // "CONTAI" << 8

        /// <summary>Chunks ya resueltos — sembrados, o sin mueble que sembrar. No se reintentan.
        /// Un chunk cuyo suelo aún no está dibujado NO entra aquí: se queda fuera para volver a
        /// mirarlo, que es la misma distinción entre «aquí no hay» y «todavía no se sabe» que
        /// gobierna el loot suelto.</summary>
        private readonly HashSet<(int cx, int cz)> _settled = new HashSet<(int cx, int cz)>();

        private float _warmupEnd;
        private bool _warmedUp;
        private float _nextScanAt;
        /// <summary>La semilla con la que se llenó `_settled`. Este componente sobrevive al
        /// teardown de sesión (ADR-056) porque es `DontDestroyOnLoad`, así que sin esto las
        /// columnas selladas en la partida anterior seguirían selladas en la siguiente — y la
        /// nueva, que es OTRO mundo, nacería sub-sembrada sin que nada lo dijera.</summary>
        private long _settledSeed;
        private ZoneLootTable _lootTable;
        private bool _lootTableLoaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpWorldContainerSpawner]");
            _instance = go.AddComponent<StpWorldContainerSpawner>();
            DontDestroyOnLoad(go);
        }

        private void OnEnable()
        {
            _warmupEnd = Time.unscaledTime + warmupSeconds;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        /// <summary>La tabla por PAPEL, la misma que sirve al loot suelto (ADR-108 D4). Carga
        /// perezosa y con caída a los valores del código: un asset que nunca se ha serializado
        /// llega con el array vacío, y ese caso tiene que dar el perfil del código y no el perfil 0
        /// para los siete papeles — la trampa que ya documenta la enmienda 4 de ADR-108.</summary>
        private ZoneLootProfile ProfileForStyle(byte style)
        {
            if (!_lootTableLoaded)
            {
                _lootTable = Resources.Load<ZoneLootTable>("Loot/ZoneLootTable");
                _lootTableLoaded = true;
            }
            if (_lootTable != null)
                return _lootTable.ProfileForStyle(style);

            var fallback = ChunkLootRoll.DefaultStyleLootProfiles();
            return fallback[Mathf.Clamp(style, 0, fallback.Length - 1)];
        }

        private void Update()
        {
            var init = NetworkInitializer.Instance;
            if (init == null || init.CurrentRole != NetworkInitializer.Role.Host)
                return; // host-only, igual que todo el sembrado: un joiner los recibe replicados

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            if (SceneManager.GetActiveScene().name != gameplayScene)
                return;

            // Sin WG3 no hay papeles que preguntar, y el papel es la puerta entera de este sistema.
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
                return; // sin instantánea todavía no hay semilla, y sembrar sin ella daría otro mundo
            long worldSeed = state.worldSeed;
            if (worldSeed != _settledSeed)
            {
                _settled.Clear();
                _settledSeed = worldSeed;
            }

            Scan(ipc, streamer, cam.transform.position, worldSeed);
        }

        private void Scan(IPCClient ipc, Wg3ChunkStreamer streamer, Vector3 around, long worldSeed)
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

                    // El rayo del sorteo va a la altura del jugador: el papel se pregunta por la
                    // vertical donde ESTÁ, no por una cota fija — con plantas, una cota fija
                    // contestaría siempre por la baja.
                    float rayY = around.y;
                    Vector3 centre = Vector3.zero;
                    bool got = ChunkContainerRoll.RollContainerByStyle(
                        worldSeed, col.cx, col.cz,
                        (u, v) =>
                        {
                            centre = new Vector3((col.cx + u) * side, rayY, (col.cz + v) * side);
                            if (!streamer.TryGetStyle(centre, out byte st)) return (byte?)null;
                            return st;
                        },
                        ProfileForStyle,
                        out var entry, out bool spaceKnown);

                    if (!spaceKnown)
                    {
                        // Igual que el loot suelto: con el chunk montado y sin NADA en esa vertical
                        // a ninguna cota, la respuesta ya es definitiva y se sella. Si hay espacio
                        // pero en otra planta, se espera — el jugador puede subir.
                        if (streamer.ChunkIsBuilt(centre) && !streamer.AnySpaceInColumn(centre))
                            _settled.Add(col);
                        continue;
                    }

                    if (!got)
                    {
                        _settled.Add(col); // este chunk no lleva mueble, y eso ya es definitivo
                        continue;
                    }

                    if (!LootPlacement.TryFindWalkablePoint(centre, 0f, PlacementSearchRadius, out Vector3 at))
                        continue; // el suelo aún no está dibujado ahí: se reintenta en el próximo barrido

                    var loot = Resolve(entry.Contents);
                    if (loot.Count == 0)
                    {
                        // Un contenedor vacío sería INMORTAL (ADR-028 post-E3) y el servidor lo
                        // rechazaría de todos modos. Se sella para no reintentarlo eternamente.
                        _settled.Add(col);
                        continue;
                    }

                    ipc.SendSpawnWorldChest(RequestIdFor(col.cx, col.cz), at, loot);
                    _settled.Add(col);
                    Debug.Log($"[StpWorldContainerSpawner] {entry.Prop} en chunk ({col.cx},{col.cz}) " +
                              $"a {at:F1} con {loot.Count} objeto(s).");
                }
            }
        }

        /// <summary>Id de petición estable para un chunk: el servidor deduplica por él, así que
        /// tiene que ser el MISMO en cada arranque o el mundo se llenaría de muebles repetidos
        /// cada vez que se recarga la partida. (La segunda red es del servidor: rechaza sembrar
        /// donde ya hay un cofre.)</summary>
        private static long RequestIdFor(int cx, int cz)
        {
            // Mezcla los 64 bits ANTES de recortar. La versión ingenua —`cx << 20 ^ cz`, recortado
            // a 36 bits— tiraba los bits altos de `cx` fuera de la máscara, así que dos chunks
            // separados por un múltiplo de 65 536 acuñaban el MISMO id. Es lejos, pero el modo de
            // fallo no es un duplicado visible (de eso ya protege `chest_already_seeded` en el
            // servidor): es que el segundo mueble, legítimo, se descarte como duplicado y no
            // aparezca nunca — silencioso, y del tipo que no se encuentra mirando el sitio.
            ulong h = (ulong)(uint)cx * 0x9E37_79B9_7F4A_7C15UL
                    ^ ((ulong)(uint)cz + 0xC4CE_B9FE_1A85_EC53UL);
            h ^= h >> 29;
            h *= 0xBF58_476D_1CE4_E5B9UL;
            h ^= h >> 32;
            return RequestIdBase + (long)(h & 0xF_FFFF_FFFFUL);
        }

        /// <summary>Nombres → ids de definición STP. Un nombre que no resuelve se salta con aviso,
        /// misma tolerancia que los otros sembradores: un catálogo a medio autorar no debe impedir
        /// que aparezca el resto del contenedor.</summary>
        private static List<CorpseLootStack> Resolve(List<string> names)
        {
            var loot = new List<CorpseLootStack>(names.Count);
            foreach (string name in names)
            {
                var def = ItemDefinition.GetWithName(name);
                if (def == null)
                {
                    Debug.LogWarning($"[StpWorldContainerSpawner] item '{name}' no está en la base de definiciones; saltado.");
                    continue;
                }
                loot.Add(new CorpseLootStack { itemId = def.Id, quantity = 1 });
            }
            return loot;
        }
    }
}
