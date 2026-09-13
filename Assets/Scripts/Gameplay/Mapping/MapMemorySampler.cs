using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Net;
using BackroomsSurvival.WorldGen3;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>
    /// P0.1 de MAPPING-PROTOTYPE — alimenta <see cref="MapMemory"/> con lo que el jugador tiene
    /// alrededor. Cada <see cref="sampleSeconds"/>: una caja por celda de 0,5 m en el disco de
    /// <see cref="radiusM"/>, a la altura del cuerpo, contra la geometría; y la línea de visión se
    /// resuelve después sobre esa rejilla, sin raycasts.
    /// </summary>
    /// <remarks>
    /// **El cliente no tiene el ráster de WG3** (lo calcula el servidor), así que se sondean los
    /// colliders que monta <c>Wg3SceneAssembler.AddColliders</c>.
    ///
    /// **Los colliders del propio jugador se descartan a mano.** <c>GeoMask</c> incluye la capa
    /// Default, que es donde vive el <c>CharacterController</c> de <c>Wg3TestPlayer</c>: sin el
    /// filtro, la celda del jugador saldría siempre pared.
    ///
    /// Local y sin red: no toca protocolo ni guardado.
    /// </remarks>
    public sealed class MapMemorySampler : MonoBehaviour
    {
        private const float CellSizeM = 0.5f;

        [Tooltip("A quién se sigue. Vacío = el CharacterController del jugador local.")]
        public Transform target;

        [Header("Recuerdo")]
        [Tooltip("Radio de lo que se ve, en metros (MAPPING-PROTOTYPE §2.8: 5 m).")]
        public float radiusM = 5f;
        public float memorySeconds = 60f;
        public float sampleSeconds = 0.5f;

        [Header("Sondeo")]
        [Tooltip("Franja del cuerpo que cuenta como pared, en metros sobre el suelo.")]
        public float probeBottomM = 0.5f;
        public float probeTopM = 1.6f;
        [Tooltip("Contra qué se sondea. Nada marcado = la geometría del mundo (GridChunkBuilder.GeoMask).")]
        public LayerMask probeMask;

        [Header("Medida")]
        [Tooltip("Cada cuánto se escribe la línea MAPMEM en el log. 0 = nunca.")]
        public float logEverySeconds = 10f;

        public MapMemory Memory { get; private set; }
        public int LastCellX { get; private set; }
        public int LastCellZ { get; private set; }
        public int LastStorey { get; private set; }
        public int LastVisibleCells { get; private set; }
        public int LastWallCells { get; private set; }
        public double LastSampleMs { get; private set; }
        public bool HasSample { get; private set; }

        private int _radiusCells;
        private bool[] _occupied;
        private int[] _cellX;
        private int[] _cellZ;
        private MapCellKind[] _kinds;
        private readonly Collider[] _hits = new Collider[8];
        private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();

        private float _nextSample;
        private float _nextLookup;
        private float _nextLog;
        private int _windowSamples;
        private double _windowMs;
        private double _windowMaxMs;
        private int _windowGc0;

        private void Awake()
        {
            // NUNCA como inicializador de campo: tocar GridChunkBuilder dispara su constructor
            // estático, que crea objetos de Unity, y desde un inicializador de MonoBehaviour eso lanza
            // TypeInitializationException y deja el tipo inservible para todo el dominio (medido
            // 2026-09-13 al crear la escena de playtest).
            if (probeMask.value == 0) probeMask = GridChunkBuilder.GeoMask;

            _radiusCells = Mathf.CeilToInt(radiusM / CellSizeM);
            int side = 2 * _radiusCells + 1;
            _occupied = new bool[side * side];
            _cellX = new int[side * side];
            _cellZ = new int[side * side];
            _kinds = new MapCellKind[side * side];

            // Las constantes del mundo, nunca números propios: si cambian allí, el recuerdo las sigue.
            Memory = new MapMemory(CellSizeM, Wg3ChunkStreamer.ChunkSize, Wg3StoreyLayers.StoreyM,
                memorySeconds, sampleSeconds, side * side);

            _nextLog = Time.time + logEverySeconds;
            _windowGc0 = System.GC.CollectionCount(0);
        }

        private void Update()
        {
            if (Time.time < _nextSample) return;
            _nextSample = Time.time + sampleSeconds;

            if (target == null && Time.time >= _nextLookup)
            {
                // Sin buscar por frame: se reintenta cada 2 s mientras no haya jugador.
                _nextLookup = Time.time + 2f;
                CharacterController controller = LocalPlayerLocator.Find<CharacterController>();
                if (controller != null) target = controller.transform;
            }

            if (target == null) return;

            _watch.Restart();
            Sample(target.position);
            _watch.Stop();

            LastSampleMs = _watch.Elapsed.TotalMilliseconds;
            _windowSamples++;
            _windowMs += LastSampleMs;
            if (LastSampleMs > _windowMaxMs) _windowMaxMs = LastSampleMs;

            if (logEverySeconds > 0f && Time.time >= _nextLog) LogWindow();
        }

        private void Sample(Vector3 position)
        {
            float floorY = Physics.Raycast(position + Vector3.up * 0.5f, Vector3.down, out RaycastHit floor, 4f,
                probeMask, QueryTriggerInteraction.Ignore)
                ? floor.point.y
                : position.y - 1f;

            int originX = Memory.CellOf(position.x);
            int originZ = Memory.CellOf(position.z);
            // +5 cm: justo en la cota de una losa, la planta no debe depender del redondeo.
            int storey = Memory.StoreyOf(floorY + 0.05f);

            int side = 2 * _radiusCells + 1;
            int radiusSq = _radiusCells * _radiusCells;
            float centreY = floorY + (probeBottomM + probeTopM) * 0.5f;
            var halfExtents = new Vector3(CellSizeM * 0.48f, (probeTopM - probeBottomM) * 0.5f, CellSizeM * 0.48f);

            for (int dz = -_radiusCells; dz <= _radiusCells; dz++)
            {
                for (int dx = -_radiusCells; dx <= _radiusCells; dx++)
                {
                    int index = (dz + _radiusCells) * side + dx + _radiusCells;
                    _occupied[index] = dx * dx + dz * dz <= radiusSq &&
                                       IsWall(new Vector3((originX + dx + 0.5f) * CellSizeM, centreY,
                                           (originZ + dz + 0.5f) * CellSizeM), halfExtents);
                }
            }

            int count = MapMemory.CollectVisible(_occupied, _radiusCells, originX, originZ, _cellX, _cellZ, _kinds);
            Memory.AddSample(Time.timeAsDouble, storey, _cellX, _cellZ, _kinds, count);

            int walls = 0;
            for (int i = 0; i < count; i++)
                if (_kinds[i] == MapCellKind.Wall) walls++;

            LastCellX = originX;
            LastCellZ = originZ;
            LastStorey = storey;
            LastVisibleCells = count;
            LastWallCells = walls;
            HasSample = true;
        }

        private bool IsWall(Vector3 centre, Vector3 halfExtents)
        {
            int hits = Physics.OverlapBoxNonAlloc(centre, halfExtents, _hits, Quaternion.identity, probeMask,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                if (!_hits[i].transform.IsChildOf(target)) return true;
            }

            return false;
        }

        private void LogWindow()
        {
            int gc0 = System.GC.CollectionCount(0);
            MapZone zone = Memory.ZoneOfCell(LastCellX, LastCellZ, LastStorey);
            Debug.Log($"MAPMEM samples={Memory.SampleCount} visible={LastVisibleCells} walls={LastWallCells} " +
                      $"zone={zone} ms_avg={(_windowSamples > 0 ? _windowMs / _windowSamples : 0):F3} " +
                      $"ms_max={_windowMaxMs:F3} gc0_collections={gc0 - _windowGc0} window_samples={_windowSamples}");

            _nextLog = Time.time + logEverySeconds;
            _windowSamples = 0;
            _windowMs = 0;
            _windowMaxMs = 0;
            _windowGc0 = gc0;
        }
    }
}
