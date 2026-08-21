#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-083 enmienda 1, punto 8 — exporta el MANIFIESTO del pool de salas a
    /// <c>Assets/StreamingAssets/room_manifest.json</c>, que es como la geometría autorada llega
    /// al backend.
    ///
    /// Por qué un fichero y no el wire: el pool viaja en el build y no cambia por partida, así que
    /// mandarlo por conexión sería pagarlo una vez por cliente. Y por qué NO al revés (que el
    /// cliente se lo mande al servidor al conectar): eso haría al cliente autoridad de la geometría
    /// de colisión del servidor — un cliente modificado borraría paredes, y un joiner no puede
    /// aportarlo. ADR-083 ya lo rechazó como alternativa (A).
    ///
    /// El manifiesto es DERIVADO: sale del pool horneado y nunca se escribe a mano (lo prohíbe el
    /// ADR). Se regenera entero en cada exportación, no se parchea — así borrar una sala del pool
    /// se refleja de verdad en vez de dejar una entrada fantasma que el backend seguiría colocando.
    /// </summary>
    internal static class RoomManifestExporter
    {
        private const string PoolPath = "Assets/Resources/Rooms/RoomPool.asset";
        private const string RoomFolder = "Assets/Resources/Rooms";
        private const string StreamingFolder = "Assets/StreamingAssets";
        private const string ManifestPath = StreamingFolder + "/room_manifest.json";

        /// <summary>
        /// Cap de footprint del backend, en TILES. NO es una regla que este exportador aplique — la
        /// aplica el backend, que es quien conoce `CHUNK_CELLS` y el ancho de anillo y margen. Aquí
        /// solo sirve para AVISAR: una sala más grande se exporta igual y el backend la descarta en
        /// silencio, y sin este aviso parecería que el horneado falló.
        ///
        /// **16, no 7, desde ADR-084 T3**: el cap dejó de ser el del chunk y pasó a ser el de 2 × 2
        /// chunks (16 × 16 tiles = 80 m). Espejo de `MAX_FOOTPRINT_CELLS_MULTI_CHUNK` (32 celdas de
        /// 2,5 m) en `backend/src/world/grid_gen/authored_rooms.rs`.
        ///
        /// Son 16 y no 17 por la PARIDAD: el origen del footprint se sortea en tiles, así que a 17
        /// tiles el único sitio donde cabría la reserva empieza en celda impar y no existe. El cap
        /// que se anuncia tiene que ser el que se coloca — lo contrario es una sala que se hornea,
        /// se exporta y no aparece nunca.
        /// </summary>
        internal const int BackendFootprintCapTiles = 16;

        /// <summary>
        /// A partir de este lado (en tiles) una sala se considera GRANDE y se le pide más de un vano.
        ///
        /// Es el cap de lo que cabe DENTRO de un chunk (`MAX_FOOTPRINT_CELLS`, 12 celdas): por encima
        /// de él la sala cruza fronteras, se come costuras y sus paredes son largas. Por debajo el
        /// problema no se da — `room_0` mide 5 × 5 con un solo vano y la sonda le cuenta 0
        /// incomunicadas; avisar de ella sería ruido que enseña a ignorar el aviso.
        /// </summary>
        internal const int BackendInChunkCapTiles = 6;

        /// <summary>Vanos mínimos que se le piden a una sala grande. Ver el aviso.</summary>
        internal const int MinDoorwaysForBigRooms = 2;

        /// <summary>
        /// Una sala vista por el backend. Nombres en snake_case a propósito: son claves de JSON que
        /// `serde` lee al otro lado, mismo criterio que los espejos de <c>IPCMessages</c>, no
        /// identificadores de C#.
        /// </summary>
        [Serializable]
        private sealed class ManifestRoom
        {
            /// <summary>Índice en <see cref="RoomPool.rooms"/>. ES lo que viaja por el wire.</summary>
            public int index;

            /// <summary>Solo para poder leer el fichero y los logs. El backend no lo interpreta.</summary>
            public string id;

            public int tiles_x;
            public int tiles_z;

            /// <summary>
            /// Altura autorada, en metros (ADR-085). De ella deduce el backend cuántas capas invade
            /// la sala y a cuáles hay que decirles que no le pinten el suelo encima.
            ///
            /// Es <c>RoomDefinition.heightMeters</c>, la altura de REFERENCIA, no la local bajo un
            /// techo inclinado: <c>ceilingTilt</c> redistribuye —un lado gana lo que el otro pierde—
            /// y usar la local haría que la capa invadida dependiera de dónde mires.
            ///
            /// Llevaba horneándose en <see cref="RoomPool.RoomEntry.heightMeters"/> desde el
            /// principio sin que nadie la leyera. Esto es lo que faltaba para que sirviera.
            /// </summary>
            public float height_meters;

            /// <summary>
            /// TODAS las aberturas por las que se puede entrar, no solo una.
            ///
            /// Una sala con dos puertas y un solo túnel deja la segunda dando contra el margen
            /// macizo: se ve el vano y detrás un bloque cerrado. El backend excava un pasillo por
            /// CADA una de estas.
            /// </summary>
            public ManifestDoorway[] doorways;
        }

        /// <summary>Una abertura de la sala, vista desde los cuatro giros posibles.</summary>
        [Serializable]
        internal sealed class ManifestDoorway
        {
            /// <summary>Lado al que da con giro <c>q · 90°</c>, en convención del backend.</summary>
            public int[] side_by_quarter;

            /// <summary>
            /// Tile de 5 m por el que sale, contado desde la esquina de menor x/z del footprint YA
            /// GIRADO, a lo largo de ese lado.
            ///
            /// Viaja porque **no se puede recalcular en el backend**: al girar la sala, la puerta se
            /// mueve a otro tile del lado, y una cuenta del tipo "el de en medio" solo acierta con
            /// giro 0. Sale de la posición REAL del boquete en el contorno, rotada.
            /// </summary>
            public int[] tile_by_quarter;
        }

        [Serializable]
        private sealed class Manifest
        {
            /// <summary>
            /// SHA-256 en hex minúscula del JSON de <see cref="rooms"/>, tal cual lo escribió esta
            /// exportación. El backend lo trata como una CADENA OPACA: lo compara con el del peer y
            /// nada más. No lo recalcula, y eso es deliberado — recalcularlo obligaría a que C# y
            /// Rust coincidieran byte a byte en una forma canónica, que es la clase de duplicación
            /// que este proyecto ya paga cara. Contra la edición a mano no protege el digest, lo
            /// prohíbe el ADR.
            /// </summary>
            public string digest;

            public ManifestRoom[] rooms;
        }

        /// <summary>Envoltorio solo para digerir el array: <c>JsonUtility</c> no serializa un
        /// array en la raíz.</summary>
        [Serializable]
        private sealed class RoomsOnly
        {
            public ManifestRoom[] rooms;
        }

        /// <summary>
        /// Vuelve a hornear las salas del pool que NO tienen por dónde entrarse, para que el
        /// horneado les ponga la puerta (`EnsureDoorway`) y pasen a ser colocables.
        ///
        /// Existe porque una sala sellada no se puede arreglar desde el manifiesto: el vano tiene
        /// que estar en la MALLA, y eso solo lo da rehornear. Las salas viejas se quedan en el pool
        /// y no molestan — el exportador las descarta, así que no aparecen en el mundo; bórralas a
        /// mano cuando confirmes que las nuevas te valen.
        ///
        /// Solo alcanza a las que guardaron su modelo. Una sala montada a mano no se puede
        /// regenerar, y se dice cuál.
        /// </summary>
        [MenuItem("Backrooms/Rebake Sealed Rooms")]
        private static void RebakeSealedRooms()
        {
            var pool = AssetDatabase.LoadAssetAtPath<RoomPool>(PoolPath);
            if (pool == null)
            {
                Debug.LogError($"[RoomManifestExporter] No hay pool en {PoolPath}.");
                return;
            }

            // Copia del array ANTES de empezar: el horneado añade entradas al pool, y recorrer el
            // array vivo mientras crece rehornearía las que se acaban de crear, sin fin.
            var before = (RoomPool.RoomEntry[])pool.rooms.Clone();
            int rebaked = 0, skipped = 0;

            foreach (var entry in before)
            {
                if (entry == null || DoorwayByQuarter(entry, out _, out _))
                    continue; // ya tiene puerta, o no es una entrada válida

                if (entry.definition == null)
                {
                    skipped++;
                    Debug.LogWarning($"[RoomManifestExporter] {entry.id} está sellada y no guardó " +
                                     "su modelo: hay que rehacerla a mano en la herramienta.");
                    continue;
                }

                // CLON del modelo: `EnsureDoorway` lo muta, y el que vive en el pool es el registro
                // de cómo se horneó la entrada VIEJA.
                var clone = JsonUtility.FromJson<RoomDefinition>(JsonUtility.ToJson(entry.definition));
                if (RoomAuthoringWindow.SaveGeneratedRoom(clone, null, out string message, out _))
                {
                    rebaked++;
                    Debug.Log($"[RoomManifestExporter] {entry.id} rehorneada con puerta → {message}");
                }
                else
                {
                    skipped++;
                    Debug.LogError($"[RoomManifestExporter] {entry.id} no se pudo rehornear: {message}");
                }
            }

            if (rebaked == 0 && skipped == 0)
            {
                Debug.Log("[RoomManifestExporter] Ninguna sala sellada: todas tienen por dónde entrar.");
                return;
            }

            // El horneado ya reexporta el manifiesto por su cuenta, pero se fuerza aquí para que el
            // resultado final refleje TODAS las salas nuevas y no solo la última.
            if (Export(out string exportMessage))
                Debug.Log($"[RoomManifestExporter] {rebaked} rehorneada(s), {skipped} sin arreglar. {exportMessage}");
        }

        [MenuItem("Backrooms/Export Room Manifest")]
        private static void ExportMenu()
        {
            if (Export(out string message))
                Debug.Log($"[RoomManifestExporter] {message}");
            else
                Debug.LogError($"[RoomManifestExporter] {message}");
        }

        [MenuItem("Backrooms/Clean Missing Rooms")]
        private static void CleanMissingRoomsMenu()
        {
            if (RemoveMissingPrefabEntries(out string message))
                Debug.Log($"[RoomManifestExporter] {message}");
            else
                Debug.LogWarning($"[RoomManifestExporter] {message}");
        }

        /// <summary>
        /// Quita del pool las entradas cuyo prefab ya no existe (borrado a mano desde el Project
        /// window, por ejemplo). Unity no lo limpia solo: el YAML del pool se queda con el GUID
        /// muerto hasta que algo lo reescribe. Reexporta el manifiesto si algo cambió, para que
        /// nunca quede desparejado del pool (ver cabecera del fichero).
        /// </summary>
        internal static bool RemoveMissingPrefabEntries(out string message)
        {
            var pool = AssetDatabase.LoadAssetAtPath<RoomPool>(PoolPath);
            if (pool == null)
            {
                message = $"No hay pool en {PoolPath}.";
                return false;
            }

            var kept = new List<RoomPool.RoomEntry>(pool.rooms.Length);
            var removed = new List<string>();
            foreach (var entry in pool.rooms)
            {
                if (entry == null || entry.prefab == null)
                {
                    removed.Add(entry == null ? "(entrada vacía)" : entry.id);
                    continue;
                }
                kept.Add(entry);
            }

            if (removed.Count == 0)
            {
                message = "Ninguna entrada colgante -- el pool ya está limpio.";
                return true;
            }

            pool.rooms = kept.ToArray();
            EditorUtility.SetDirty(pool);
            AssetDatabase.SaveAssets();
            Export(out string exportMessage);

            message = $"Quitadas {removed.Count} entrada(s) sin prefab: {string.Join(", ", removed)}. {exportMessage}";
            return true;
        }

        /// <summary>
        /// Al borrar un <c>room_N.prefab</c> desde el Project window, quita su entrada del pool en
        /// el mismo momento -- sin esto el pool se queda con un GUID muerto hasta que alguien se
        /// acuerde de correr "Clean Missing Rooms" a mano, que es justo como se generaron room_0/
        /// room_1/room_2 colgantes. Solo mira dentro de la carpeta de salas: cualquier otro prefab
        /// borrado en el proyecto no interesa aquí.
        /// </summary>
        private sealed class RoomPoolCleaner : AssetModificationProcessor
        {
            private static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
            {
                if (!assetPath.StartsWith(RoomFolder, StringComparison.Ordinal)
                    || !assetPath.EndsWith(".prefab", StringComparison.Ordinal))
                    return AssetDeleteResult.DidNotDelete;

                string id = Path.GetFileNameWithoutExtension(assetPath);
                var pool = AssetDatabase.LoadAssetAtPath<RoomPool>(PoolPath);
                if (pool != null && pool.rooms != null)
                {
                    var kept = Array.FindAll(pool.rooms, e => e == null || e.id != id);
                    if (kept.Length != pool.rooms.Length)
                    {
                        pool.rooms = kept;
                        EditorUtility.SetDirty(pool);
                        AssetDatabase.SaveAssets();
                        Export(out string exportMessage);
                        Debug.Log($"[RoomManifestExporter] '{id}' borrado -- entrada quitada del pool. {exportMessage}");
                    }
                }
                return AssetDeleteResult.DidNotDelete;
            }
        }

        /// <summary>
        /// Regenera el manifiesto entero desde el pool. Devuelve false con el motivo en
        /// <paramref name="message"/>; en éxito el mensaje es la línea de resumen.
        /// </summary>
        internal static bool Export(out string message)
        {
            var pool = AssetDatabase.LoadAssetAtPath<RoomPool>(PoolPath);
            if (pool == null)
            {
                message = $"No hay pool en {PoolPath}. Hornea una sala antes de exportar.";
                return false;
            }

            var rooms = new List<ManifestRoom>();
            var skipped = new List<string>();
            var oversized = new List<string>();
            var underDoored = new List<string>();

            for (int i = 0; i < pool.rooms.Length; i++)
            {
                var entry = pool.rooms[i];
                if (entry == null || entry.tilesX < 1 || entry.tilesZ < 1)
                {
                    skipped.Add($"[{i}] entrada vacía o con footprint inválido");
                    continue;
                }

                // El vano REAL de la sala, no el ancla: el ancla es un transform que se coloca a ojo
                // y puede no corresponder con ningún hueco de la geometría. `room_0` se horneó así —
                // ancla apuntando a +z y `holes` vacío — y el mundo la colocaba como una caja
                // sellada. Desde ADR-083 enmienda 1 el horneado garantiza que haya vano
                // (`EnsureDoorway`), así que esto solo falla con salas horneadas antes.
                if (!DoorwayByQuarter(entry, out var doorways, out string why))
                {
                    skipped.Add($"[{i}] {entry.id}: {why}");
                    continue;
                }

                if (entry.tilesX > BackendFootprintCapTiles || entry.tilesZ > BackendFootprintCapTiles)
                    oversized.Add($"{entry.id} ({entry.tilesX}×{entry.tilesZ})");

                // Una sala grande con un solo vano nace SELLADA a menudo, y no avisa nadie: el
                // backend excava un pasillo por abertura hasta el laberinto, y si esa única
                // abertura da a un sitio del que no se sale, la sala queda incomunicada. Medido con
                // `probe_unreachable_rooms`: una sala de 10×10 tiles con un vano sale incomunicada
                // 6 de 55 veces; la misma medida con cuatro vanos, 0 de 58.
                //
                // Se agrava con ADR-084: una sala multi-chunk SUPRIME la apertura de costura de cada
                // borde de chunk que tapa, hasta cuatro. Devolver una sola puerta a cambio empobrece
                // la zona aunque la sala sí se alcance.
                if ((entry.tilesX > BackendInChunkCapTiles || entry.tilesZ > BackendInChunkCapTiles)
                    && doorways.Count < MinDoorwaysForBigRooms)
                    underDoored.Add($"{entry.id} ({entry.tilesX}×{entry.tilesZ}, {doorways.Count} vano/s)");

                rooms.Add(new ManifestRoom
                {
                    index = i,
                    id = entry.id,
                    tiles_x = entry.tilesX,
                    tiles_z = entry.tilesZ,
                    height_meters = entry.heightMeters,
                    doorways = doorways.ToArray(),
                });
            }

            var array = rooms.ToArray();
            string roomsJson = JsonUtility.ToJson(new RoomsOnly { rooms = array });
            string manifestJson = JsonUtility.ToJson(
                new Manifest { digest = Sha256Hex(roomsJson), rooms = array }, prettyPrint: true);

            BackroomsEditorFolders.EnsureFolder(StreamingFolder);
            File.WriteAllText(ManifestPath, manifestJson, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(ManifestPath);

            foreach (string s in skipped)
                Debug.LogWarning($"[RoomManifestExporter] Fuera del manifiesto — {s}");
            if (underDoored.Count > 0)
                Debug.LogWarning("[RoomManifestExporter] Salas grandes con pocos vanos: " +
                                 $"{string.Join(", ", underDoored)}. Se exportan, pero una sala " +
                                 "grande de un solo vano nace INCOMUNICADA a menudo (medido: 6 de " +
                                 "55) y, si cruza chunks, encima se come las costuras que tapa. " +
                                 $"Ponle al menos {MinDoorwaysForBigRooms} boquetes, mejor uno por lado.");
            if (oversized.Count > 0)
                Debug.LogWarning($"[RoomManifestExporter] Por encima del cap de " +
                                 $"{BackendFootprintCapTiles}×{BackendFootprintCapTiles} tiles " +
                                 $"(ADR-084, 2×2 chunks): {string.Join(", ", oversized)}. Se exportan, " +
                                 "pero el backend NO las colocará: no hay sitio para ellas en el mundo.");

            message = $"{array.Length} sala(s) → {ManifestPath}" +
                      (skipped.Count > 0 ? $", {skipped.Count} descartada(s) — ver avisos." : ".");
            return true;
        }

        /// <summary>
        /// Altura máxima del borde inferior de una abertura para seguir contando como paso.
        ///
        /// No es 0: una puerta con un escalón de 30 cm se cruza andando, y exigir el ras exacto
        /// descartaba aberturas perfectamente buenas — el vano real de la primera sala redonda tenía
        /// `baseY = 0.3` y el mundo la trataba como pared.
        /// </summary>
        private const float MaxDoorStepHeight = 0.5f;

        /// <summary>Tope de aberturas que el backend sabe atender. Espejo de `MAX_DOORWAYS`.</summary>
        private const int MaxDoorways = 8;

        /// <summary>
        /// TODAS las aberturas practicables de la sala, cada una con su lado y su tile para los
        /// cuatro giros, sacados de los BOQUETES reales del modelo.
        ///
        /// El proceso, por abertura y giro: se localiza su centro sobre el contorno interior, se rota
        /// ese punto igual que se rotará la sala, y se mira (a) hacia qué lado cardinal da y (b) en
        /// qué tile del footprint ya girado cae. Lo segundo es lo que no se puede recalcular en el
        /// backend: al rotar, la puerta cambia de tile, y "el tile de en medio" solo acierta a 0°.
        /// </summary>
        internal static bool DoorwayByQuarter(RoomPool.RoomEntry entry,
            out List<ManifestDoorway> doorways, out string why)
        {
            doorways = null;

            var def = entry.definition;
            if (def == null)
            {
                why = "sin modelo guardado (sala montada a mano); no se puede localizar su vano";
                return false;
            }

            var contour = def.InnerContour();
            if (contour == null || contour.Length < 3)
            {
                why = "contorno degenerado";
                return false;
            }

            doorways = new List<ManifestDoorway>();
            int ignored = 0;

            foreach (var h in def.holes ?? System.Array.Empty<RoomDefinition.WallHole>())
            {
                // Una reja no vale (no se pasa), ni una ventana (no llega al suelo), ni un vano
                // estrecho. Un escalón de hasta MaxDoorStepHeight sí: se cruza andando.
                if (h == null || h.baseY > MaxDoorStepHeight || h.grateBars != 0 || h.width < 1f)
                    continue;

                int s = ((h.side % contour.Length) + contour.Length) % contour.Length;
                Vector2 p0 = contour[s], p1 = contour[(s + 1) % contour.Length];

                // Un vano más ancho que su propia pared se recorta contra ella y puede no existir en
                // la malla. Declararlo como puerta manda un pasillo contra un muro liso, que es peor
                // que no ponerlo: `spanCorners` es lo que lo salva, y sin él no se cuenta.
                if (!h.spanCorners && (p1 - p0).magnitude + 1e-3f < h.width)
                {
                    ignored++;
                    continue;
                }

                Vector2 mid = Vector2.Lerp(p0, p1, Mathf.Clamp01(h.along));
                Vector2 outward = RoomDefinition.OutwardNormal(p0, p1);

                var sides = new int[4];
                var tiles = new int[4];
                bool degenerate = false;
                for (int q = 0; q < 4; q++)
                {
                    var rot = Quaternion.Euler(0f, q * 90f, 0f);
                    Vector3 n = rot * new Vector3(outward.x, 0f, outward.y);
                    Vector3 p = rot * new Vector3(mid.x, 0f, mid.y);

                    float ax = Mathf.Abs(n.x), az = Mathf.Abs(n.z);
                    if (ax < 1e-3f && az < 1e-3f)
                    {
                        degenerate = true;
                        break;
                    }
                    bool alongX = ax < az; // muro norte/sur ⇒ la puerta se desplaza en X
                    sides[q] = ax >= az
                        ? (n.x >= 0f ? 3 : 2)   // este (+x) : oeste (−x)
                        : (n.z >= 0f ? 1 : 0);  // norte (+z) : sur (−z)

                    // Footprint YA GIRADO: un cuarto impar intercambia los ejes.
                    bool swapped = q == 1 || q == 3;
                    int tilesX = swapped ? entry.tilesZ : entry.tilesX;
                    int tilesZ = swapped ? entry.tilesX : entry.tilesZ;

                    // El pivote es el CENTRO del footprint, así que la esquina de menor coordenada
                    // está en −mitad. De ahí sale el tile, clampado por si el vano roza la esquina.
                    int span = alongX ? tilesX : tilesZ;
                    float half = span * 5f * 0.5f;
                    float offset = (alongX ? p.x : p.z) + half;
                    tiles[q] = Mathf.Clamp(Mathf.FloorToInt(offset / 5f), 0, span - 1);
                }
                if (degenerate)
                {
                    ignored++;
                    continue;
                }

                doorways.Add(new ManifestDoorway { side_by_quarter = sides, tile_by_quarter = tiles });
                if (doorways.Count == MaxDoorways)
                    break;
            }

            if (doorways.Count == 0)
            {
                why = ignored > 0
                    ? $"tiene {ignored} abertura(s) pero ninguna practicable (recortadas contra su " +
                      "propia pared, o con la normal degenerada)"
                    : "no tiene ninguna abertura a ras de suelo: es una caja sellada";
                doorways = null;
                return false;
            }

            if (ignored > 0)
                Debug.LogWarning($"[RoomManifestExporter] {entry.id}: {ignored} abertura(s) " +
                                 "descartada(s) por no existir de verdad en la malla — un vano más " +
                                 "ancho que su pared necesita `spanCorners`.");

            why = null;
            return true;
        }

        private static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
#endif
