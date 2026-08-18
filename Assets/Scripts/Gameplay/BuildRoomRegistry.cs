using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-081 enmienda 5 — dónde están las HABITACIONES CONSTRUIBLES, según el backend.
    ///
    /// Mismo papel que <see cref="ZoneRegistry"/> y por el mismo motivo: el cliente necesita saber
    /// una cosa del mundo que solo el backend deriva. La diferencia es la fuente — la zona viaja en
    /// el snapshot de 10 Hz y esto viaja con el CHUNK, en la respuesta a `RequestChunk`, porque es
    /// donde ya viajan `room_zones` y las pintadas y porque es el mensaje que el render consume.
    ///
    /// Se puebla desde <see cref="GridChunkDataMsg"/> y solo GANA entradas: un chunk que se
    /// descarga y se vuelve a pedir trae lo mismo (el emplazamiento es puro por seed), así que no
    /// hay nada que invalidar. Un chunk que aún no ha llegado simplemente no está, y la puerta de
    /// construcción trata "no sé" como "no" — ver <c>BuildPermission</c>.
    /// </summary>
    public static class BuildRoomRegistry
    {
        /// <summary>Lado de la habitación en metros. Espejo de `ROOM_SIZE_M`
        /// (backend/src/world/grid_gen/build_rooms.rs): 3 tiles de 5 m.</summary>
        public const float RoomSizeMeters = GridChunkDataMsg.BuildRoomTiles * 5f;

        private readonly struct Room
        {
            public readonly float MinX;
            public readonly float MinZ;
            public readonly int TileX;
            public readonly int TileZ;

            public Room(float minX, float minZ, int tileX, int tileZ)
            {
                MinX = minX;
                MinZ = minZ;
                TileX = tileX;
                TileZ = tileZ;
            }

            public bool Contains(float x, float z) =>
                x >= MinX && x < MinX + RoomSizeMeters && z >= MinZ && z < MinZ + RoomSizeMeters;
        }

        private static readonly Dictionary<(int cx, int cz), Room> _rooms =
            new Dictionary<(int, int), Room>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _rooms.Clear();

        /// <summary>Diagnóstico: cuántas habitaciones se han visto en esta sesión.</summary>
        public static int KnownRoomCount => _rooms.Count;

        /// <summary>
        /// Vacía el registro. Existe para los tests EditMode, que fabrican chunks a mano y no pueden
        /// heredar los de otro test — y es público porque el compile-check headless construye la
        /// asamblea con sufijo `_check`, así que un `InternalsVisibleTo` nunca casaría (mismo motivo
        /// que en `WireSchema`). En runtime no lo llama nadie: el reset entre sesiones lo hace
        /// `ResetStatics`.
        /// </summary>
        public static void Clear_EditorTestsOnly() => _rooms.Clear();

        /// <summary>
        /// Registra (o no) la habitación de un chunk recién llegado. Solo capa 0: es la única en la
        /// que el backend las talla, y llamar con otra capa no debe borrar lo que ya se sabe de la
        /// columna.
        /// </summary>
        public static void Observe(GridChunkDataMsg chunk)
        {
            if (chunk == null || chunk.layer != 0 || !chunk.hasBuildRoom)
                return;

            float minX = chunk.cx * SprayMsg.ChunkSize + chunk.buildRoomTileX * 5f;
            float minZ = chunk.cz * SprayMsg.ChunkSize + chunk.buildRoomTileZ * 5f;
            _rooms[(chunk.cx, chunk.cz)] = new Room(minX, minZ, chunk.buildRoomTileX, chunk.buildRoomTileZ);
        }

        /// <summary>
        /// Tile de origen de la habitación de (cx, cz), en TILES dentro del chunk. False si ese
        /// chunk no tiene sala o si aún no ha llegado.
        ///
        /// Lo consume el CONSTRUCTOR del chunk: es lo que le dice qué tiles pintar con material
        /// propio y dónde no colgar lámparas. Solo capa 0, igual que el registro.
        /// </summary>
        public static bool TryGetRoomTile(int cx, int cz, int layer, out int tileX, out int tileZ)
        {
            tileX = -1;
            tileZ = -1;
            if (layer != 0 || !_rooms.TryGetValue((cx, cz), out var room))
                return false;

            tileX = room.TileX;
            tileZ = room.TileZ;
            return true;
        }

        /// <summary>
        /// Esquina de menor x/z de la habitación del chunk que contiene <paramref name="worldPosition"/>.
        /// False si ese chunk no tiene sala (o aún no ha llegado).
        ///
        /// Lo consume el dibujo del borde: se pinta el rectángulo REAL que el backend talló, no una
        /// aproximación centrada en el marcador, así que lo que se ve es exactamente lo que el host
        /// aplica.
        /// </summary>
        public static bool TryGetRoomOrigin(Vector3 worldPosition, out Vector3 origin)
        {
            int cx = Mathf.FloorToInt(worldPosition.x / SprayMsg.ChunkSize);
            int cz = Mathf.FloorToInt(worldPosition.z / SprayMsg.ChunkSize);
            if (_rooms.TryGetValue((cx, cz), out var room))
            {
                origin = new Vector3(room.MinX, 0f, room.MinZ);
                return true;
            }

            origin = default;
            return false;
        }

        /// <summary>
        /// ¿Está <paramref name="worldPosition"/> dentro de una habitación construible conocida?
        ///
        /// Espejo de `position_in_build_room` del host. La Y no entra: la habitación es una
        /// superficie de suelo, igual que allí.
        ///
        /// Un chunk cuya respuesta aún no ha llegado responde `false` — el mismo criterio
        /// conservador que la zona desconocida: decir "sí" sin saberlo pondría al jugador a colocar
        /// piezas que el host va a rechazar.
        /// </summary>
        public static bool Contains(Vector3 worldPosition)
        {
            int cx = Mathf.FloorToInt(worldPosition.x / SprayMsg.ChunkSize);
            int cz = Mathf.FloorToInt(worldPosition.z / SprayMsg.ChunkSize);
            return _rooms.TryGetValue((cx, cz), out var room)
                   && room.Contains(worldPosition.x, worldPosition.z);
        }
    }
}
