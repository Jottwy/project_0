using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// ADR-095 F2 — el consumidor de WorldGen3 en el cliente.
    ///
    /// Pide los chunks alrededor del jugador, recibe la LISTA DE PIEZAS y monta la geometría desde
    /// el catálogo horneado local. Por el cable no viaja geometría: viajan once bytes por pieza y
    /// esta clase los convierte en malla y colisión con el mismo `Wg3Geometry` que ya usa la escena
    /// de prueba — o sea, la misma fuente única que el servidor rasteriza al otro lado.
    ///
    /// **LO QUE NO HACE, Y ES DELIBERADO: no genera nada.** Si el backend no manda una pieza, aquí
    /// no aparece. Es toda la diferencia entre este camino y el de WG2, donde cliente y servidor
    /// derivan el mundo por separado del mismo seed y hay que confiar en que coincidan.
    /// </summary>
    public sealed class Wg3ChunkStreamer : MonoBehaviour
    {
        /// <summary>Lado del chunk en metros. Espejo de `WG3_CHUNK_M` en Rust.</summary>
        public const float ChunkSize = 50f;

        [Header("Streaming")]
        [Tooltip("Radio en chunks alrededor del jugador. 1 = 3×3.")]
        [Range(0, 4)] public int radius = 1;

        [Tooltip("Cada cuánto se revisa qué chunks faltan, en segundos. No hace falta por frame: " +
                 "el jugador tarda segundos en cruzar 50 m.")]
        public float refreshSeconds = 0.5f;

        [Header("Materiales")]
        public Wg3Materials materials = new Wg3Materials();

        public bool spawnLights = true;

        /// <summary>El transform que decide qué chunks se piden. Sin él, la cámara principal.</summary>
        public Transform viewer;

        private readonly Dictionary<Vector2Int, GameObject> _built = new Dictionary<Vector2Int, GameObject>();
        private readonly HashSet<Vector2Int> _requested = new HashSet<Vector2Int>();
        /// <summary>
        /// Mallas POR CHUNK, no una lista plana.
        ///
        /// Con una lista compartida, `Prune` destruía el GameObject del chunk y dejaba sus mallas
        /// vivas: andar por el mundo las acumulaba sin techo, porque solo `ClearAll` las tiraba. Es
        /// exactamente la fuga que este mismo fichero cita de `VerticalShaftChunk` —«destruía los
        /// hijos y nunca los recursos»— colada por la puerta de al lado.
        /// </summary>
        private readonly Dictionary<Vector2Int, List<Mesh>> _meshes =
            new Dictionary<Vector2Int, List<Mesh>>();
        private List<Wg3Piece> _catalog;
        private float _nextRefresh;
        private bool _digestChecked;

        private void OnEnable()
        {
            // Por Wg3ActiveCatalog y no por Wg3Catalog directamente: el exportador del manifiesto
            // hace esta misma pregunta, y si cada uno la respondiera por su cuenta el servidor
            // colocaría de un catálogo y el cliente dibujaría de otro.
            _catalog = Wg3ActiveCatalog.Build(out string catalogSource);
            if (_catalog.Count == 0)
                Debug.LogError($"[WG3] catálogo vacío — {catalogSource}. No se va a dibujar nada.", this);
            var client = IPCClient.Instance;
            if (client != null) client.AddWg3ChunkListener(OnWg3Chunk);
        }

        private void OnDisable()
        {
            var client = IPCClient.Instance;
            if (client != null) client.RemoveWg3ChunkListener(OnWg3Chunk);
            ClearAll();
        }

        private void Update()
        {
            var client = IPCClient.Instance;
            if (client == null || !client.Wg3Enabled) return;

            if (!_digestChecked && !string.IsNullOrEmpty(client.Wg3ManifestDigest))
            {
                _digestChecked = true;
                VerifyDigest(client.Wg3ManifestDigest);
            }

            if (Time.time < _nextRefresh) return;
            _nextRefresh = Time.time + Mathf.Max(0.1f, refreshSeconds);

            Transform eye = viewer != null ? viewer : (Camera.main != null ? Camera.main.transform : null);
            if (eye == null) return;

            Vector2Int centre = ChunkOf(eye.position);
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    var coord = new Vector2Int(centre.x + dx, centre.y + dz);
                    if (_requested.Contains(coord)) continue;
                    // Solo se marca como pedido si la trama SALIÓ. Marcarlo antes deja el chunk
                    // vacío para siempre cuando la escritura falla, que es la trampa que
                    // `SendRequestChunk` ya documenta para WG2.
                    if (client.SendRequestWg3Chunk(coord.x, coord.y)) _requested.Add(coord);
                }

            Prune(centre);
        }

        /// <summary>
        /// **La comparación que evita el fallo silencioso más caro de WG3.**
        ///
        /// Cliente y servidor hornean el catálogo por separado. Si no coinciden, la geometría que
        /// se dibuja y la que bloquea son de mundos distintos: nada da error, y el síntoma es
        /// atravesar paredes que se ven o chocar con aire. Dos cadenas comparadas lo convierten en
        /// un error con motivo, antes de dibujar el primer chunk.
        /// </summary>
        private void VerifyDigest(string backendDigest)
        {
            string local = Wg3Manifest.FromCatalog(_catalog).digest;
            if (local == backendDigest) return;

            Debug.LogError(
                $"[WG3] el catálogo del backend NO es el de este cliente " +
                $"(backend {Short(backendDigest)}, cliente {Short(local)}). La geometría que se " +
                $"dibuje y la que bloquee serán de mundos distintos. Reexporta el manifiesto con " +
                $"«Backrooms ▸ WorldGen3 ▸ Exportar manifiesto» y reinicia el backend.", this);
            enabled = false;
        }

        private static string Short(string digest) =>
            string.IsNullOrEmpty(digest) ? "<vacío>" : digest.Substring(0, Mathf.Min(12, digest.Length));

        private void OnWg3Chunk(Wg3ChunkMsg chunk)
        {
            var coord = new Vector2Int(chunk.cx, chunk.cz);
            _requested.Add(coord);

            // Al rehacer un chunk se van TAMBIÉN sus mallas: recibirlo dos veces (una repetición
            // del servidor, o volver a entrar en el radio) duplicaría los recursos si no.
            if (_built.TryGetValue(coord, out GameObject existing) && existing != null)
                Destroy(existing);
            DestroyMeshesOf(coord);

            // Una lista vacía es un resultado VÁLIDO: un chunk donde no cae ninguna pieza. Se
            // registra igualmente para no volver a pedirlo — sin esto, todo hueco del mundo se
            // pediría una y otra vez cada medio segundo.
            if (chunk.placements.Count == 0)
            {
                _built[coord] = null;
                return;
            }

            var root = new GameObject($"wg3_chunk_{chunk.cx}_{chunk.cz}");
            root.transform.SetParent(transform, false);
            _built[coord] = root;

            var mine = new List<Mesh>();
            _meshes[coord] = mine;

            foreach (Wg3PlacementMsg wire in chunk.placements)
            {
                if (wire.piece < 0 || wire.piece >= _catalog.Count)
                {
                    // El digest ya debería haber cazado esto. Si aún así llega, se avisa y se salta
                    // la pieza: dibujar otra en su sitio sería peor que dejar el hueco, porque el
                    // servidor colisiona la que él cree.
                    Debug.LogError($"[WG3] pieza {wire.piece} fuera del catálogo local ({_catalog.Count})", this);
                    continue;
                }

                var placement = new Wg3Placement
                {
                    piece = _catalog[wire.piece],
                    rotation = wire.rotation & 3,
                    originX = wire.OriginX,
                    originZ = wire.OriginZ,
                    socketState = new byte[_catalog[wire.piece].sockets.Length]
                };

                var single = new Wg3World();
                single.placements.Add(placement);
                Wg3SceneAssembler.Assemble(single, root.transform, materials, mine, spawnLights);

                if (!_hasSpawn)
                {
                    // Centro de la PRIMERA pieza que llega, a la altura de los ojos. Es adonde hay
                    // que llevar al jugador: el andamio deja un chunk de cada tres vacío, así que
                    // el origen del mundo cae en el aire con bastante probabilidad y aparecer ahí
                    // se lee como "el mundo no cargó" cuando lo que pasa es que ahí no hay nada.
                    _spawn = new Vector3(
                        placement.originX + placement.SizeX * 0.5f, 1.0f,
                        placement.originZ + placement.SizeZ * 0.5f);
                    _hasSpawn = true;
                }
            }
        }

        private Vector3 _spawn;
        private bool _hasSpawn;

        /// <summary>Centro de la primera pieza recibida, si ya ha llegado alguna.</summary>
        public bool TryGetSpawnPoint(out Vector3 point)
        {
            point = _spawn;
            return _hasSpawn;
        }

        /// <summary>Tira lo que quedó fuera del radio, con un margen de un chunk. El margen evita
        /// que caminar de un lado a otro de una frontera destruya y reconstruya sin parar.</summary>
        private void Prune(Vector2Int centre)
        {
            int keep = radius + 1;
            var doomed = new List<Vector2Int>();
            foreach (KeyValuePair<Vector2Int, GameObject> kv in _built)
            {
                if (Mathf.Abs(kv.Key.x - centre.x) <= keep && Mathf.Abs(kv.Key.y - centre.y) <= keep)
                    continue;
                doomed.Add(kv.Key);
            }
            foreach (Vector2Int coord in doomed)
            {
                if (_built[coord] != null) Destroy(_built[coord]);
                DestroyMeshesOf(coord);
                _built.Remove(coord);
                _requested.Remove(coord);
            }
        }

        private void ClearAll()
        {
            foreach (KeyValuePair<Vector2Int, GameObject> kv in _built)
                if (kv.Value != null) Destroy(kv.Value);
            _built.Clear();
            _requested.Clear();

            // Y las mallas, que no son hijas de ningún GameObject y por tanto no se van con ellos.
            foreach (KeyValuePair<Vector2Int, List<Mesh>> kv in _meshes)
                foreach (Mesh mesh in kv.Value)
                    if (mesh != null) Destroy(mesh);
            _meshes.Clear();
        }

        private void DestroyMeshesOf(Vector2Int coord)
        {
            if (!_meshes.TryGetValue(coord, out List<Mesh> list)) return;
            foreach (Mesh mesh in list)
                if (mesh != null) Destroy(mesh);
            _meshes.Remove(coord);
        }

        public static Vector2Int ChunkOf(Vector3 world) => new Vector2Int(
            Mathf.FloorToInt(world.x / ChunkSize),
            Mathf.FloorToInt(world.z / ChunkSize));
    }
}
