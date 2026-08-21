#if UNITY_EDITOR
using BackroomsSurvival.Gameplay.GridWorld;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Hornea la sala que ENCIENDE el multi-chunk de ADR-084.
    ///
    /// Todo el código de salas repartidas entre chunks está escrito y commiteado, y hasta que exista
    /// una sala que no quepa en un chunk es INVISIBLE: el mundo se comporta exactamente igual que
    /// antes. El pool tenía `room_0` (5 × 5 tiles, cabe de sobra) y `room_1` (32 × 32 = 160 m, que
    /// pasa del cap absoluto de 2 × 2 chunks y por eso no se coloca nunca). Faltaba justo el rango
    /// intermedio, y eso es lo que crea este menú.
    ///
    /// **Por qué un creador y no hornearla a mano en la ventana de autoría:** así queda escrito de
    /// qué tamaño es y POR QUÉ ese tamaño, y se puede rehacer idéntica. La forma no se inventa —
    /// se copia la de `room_1`, que es la que autoró Joel; lo único que cambia es la medida.
    /// </summary>
    internal static class BackroomsMultiChunkRoomCreator
    {
        private const string PoolPath = "Assets/Resources/Rooms/RoomPool.asset";

        /// <summary>
        /// 12 × 12 tiles = 60 m. La elección tiene tres restricciones y sale del hueco entre ellas:
        ///
        /// - **&gt; 6 × 6 tiles**, o cabría en un chunk y no ejercitaría nada.
        /// - **≤ 16 × 16 tiles**, el cap real de 2 × 2 chunks (`MAX_FOOTPRINT_CELLS_MULTI_CHUNK`).
        /// - **holgada dentro del cap**: a 16 × 16 la reserva ocupa 36 de las 38 celdas de la ventana
        ///   y solo queda UN origen legal por eje, así que la sala saldría siempre en el mismo sitio
        ///   relativo y casi no dejaría laberinto alrededor. A 12 × 12 hay cinco orígenes por eje.
        ///
        /// Su reserva mide 28 celdas y la frontera de chunk cae en la 20, así que **cruza siempre**.
        /// </summary>
        private const int Tiles = 12;

        /// <summary>Máximo que ADR-085 admite: por encima, la capa más alta —la única que dibuja
        /// techo— se quedaría abierta. Mismo valor que `room_0` y `room_1`.</summary>
        private const float HeightMeters = 12f;

        [MenuItem("Backrooms/Create Multi-Chunk Room")]
        public static void Create()
        {
            var def = StyleSource();
            def.tilesX = Tiles;
            def.tilesZ = Tiles;
            def.heightMeters = HeightMeters;

            // CUATRO vanos, uno por lado, y no es estética. ADR-084 punto 4 SUPRIME la apertura de
            // costura en cada borde de chunk que la sala tapa, y una sala de 60 m tapa hasta cuatro.
            // Devolver una sola puerta a cambio empobrece la zona entera; con una por lado, la sala
            // sustituye a las costuras que se come. Cada abertura excava su propio pasillo
            // (ADR-083 enmienda 1), así que las cuatro conectan de verdad.
            int sides = Mathf.Max(4, def.sides);
            var holes = new RoomDefinition.WallHole[4];
            for (int i = 0; i < holes.Length; i++)
            {
                holes[i] = new RoomDefinition.WallHole
                {
                    // Repartidos en cuartos de perímetro y desplazados un octavo, para caer a mitad
                    // de lado y no en una esquina: un vano en la esquina se recorta contra los dos
                    // lados y puede desaparecer (es la trampa de `spanCorners`).
                    side = (sides / 8 + i * sides / 4) % sides,
                    along = 0.5f,
                    baseY = 0f,
                    level = 0,
                    // Un tile de ancho es lo que excava el backend, así que el hueco visible debe
                    // ser al menos eso o se ve pared donde se cruza.
                    width = 2.47f,
                    height = 4f,
                    grateBars = 0,
                    // En planta de muchas facetas cada lado mide poco más de un metro: sin esto el
                    // vano se recorta contra su propia pared hasta desaparecer y la sala nace
                    // sellada aunque el manifiesto diga que tiene puerta.
                    spanCorners = true,
                };
            }
            def.holes = holes;

            if (!RoomAuthoringWindow.SaveGeneratedRoom(def, out string message))
            {
                Debug.LogError($"[MultiChunkRoom] no se horneó: {message}");
                return;
            }

            Debug.Log($"[MultiChunkRoom] {Tiles}×{Tiles} tiles ({Tiles * 5} m), {HeightMeters} m de " +
                      $"alto, {holes.Length} vanos. {message}\n" +
                      "Siguiente paso OBLIGATORIO: Backrooms ▸ Export Room Manifest, o el backend " +
                      "sigue leyendo el manifiesto viejo y la sala no existe para el mundo.");
        }

        /// <summary>
        /// La forma se copia de la última sala del pool que la tenga guardada, para que la nueva no
        /// desentone con lo ya autorado. Si el pool está vacío se cae a los valores por defecto de
        /// <see cref="RoomDefinition"/>, que dan una caja limpia.
        ///
        /// Se clona por JSON y no se referencia: la `definition` del pool es un asset vivo y
        /// mutarla cambiaría la sala de la que se copia.
        /// </summary>
        private static RoomDefinition StyleSource()
        {
            var pool = AssetDatabase.LoadAssetAtPath<RoomPool>(PoolPath);
            if (pool?.rooms != null)
            {
                for (int i = pool.rooms.Length - 1; i >= 0; i--)
                {
                    var d = pool.rooms[i]?.definition;
                    if (d != null && d.tilesX > 0)
                        return JsonUtility.FromJson<RoomDefinition>(JsonUtility.ToJson(d));
                }
            }
            return new RoomDefinition();
        }
    }
}
#endif
