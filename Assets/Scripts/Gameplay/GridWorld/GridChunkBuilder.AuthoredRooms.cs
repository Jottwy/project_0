// Partición de GridChunkBuilder: salas AUTORADAS a mano (RoomPool). El resto vive en
// GridChunkBuilder.cs (raíz), .Placement.cs, .WallVariants.cs, .Tinting.cs y .Props.cs.
// TODOS los campos estáticos (incl. _roomPool y los scratch) viven en el raíz: el orden de
// inicialización de estáticos entre partials es indefinido.
using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    public static partial class GridChunkBuilder
    {
        /// <summary>
        /// Una sala del pool ya resuelta contra un sitio del chunk: qué prefab, dónde y con qué
        /// giro. Rect en TILES de 5 m, con `tx1`/`tz1` EXCLUSIVOS.
        /// </summary>
        private struct RoomPlan
        {
            public GameObject prefab;
            public Vector3 localCenter;
            public float yaw;
            public int tx0, tz0, tx1, tz1;

            public bool ContainsTile(int tx, int tz) =>
                tx >= tx0 && tx < tx1 && tz >= tz0 && tz < tz1;
        }

        /// <summary>El pool autorado, o null si nadie ha horneado una sala todavía.</summary>
        private static RoomPool AuthoredRoomPool()
        {
            if (_roomPoolLoaded)
                return _roomPool;
            _roomPoolLoaded = true;
            _roomPool = Resources.Load<RoomPool>("Rooms/RoomPool");
            return _roomPool;
        }

        /// <summary>
        /// Coloca la sala autorada que el BACKEND ya decidió para este chunk.
        ///
        /// Desde ADR-083 enmienda 1 el cliente NO elige nada: el servidor reserva el sitio, vacía el
        /// interior, lo cierra con un anillo y excava el pasillo, y manda por el wire qué entrada del
        /// pool va y con qué giro. Aquí solo se instancia.
        ///
        /// Antes de eso el cliente sorteaba por su cuenta, emparejando el footprint del pool contra
        /// las zonas selladas del generador. Ese camino se retiró entero, y con motivo: exigía
        /// igualdad EXACTA de footprint contra unas zonas que miden 3 × 3 tiles, así que no colocaba
        /// ni una sala en todo el mundo. Reintroducirlo reabriría además la divergencia que ADR-081
        /// ya dejó anotada como deuda — dos implementaciones de la misma regla en dos lenguajes.
        ///
        /// El plan sale de <see cref="AuthoredRoomRegistry"/> y no del mensaje: una reconstrucción
        /// (chunk revisitado, o el reintento tras el gate de zona) tiene que ver la misma sala.
        /// </summary>
        private static void PlanAuthoredRooms(int chunkX, int chunkZ, int layerIndex,
            List<RoomPlan> into)
        {
            into.Clear();
            if (!AuthoredRoomRegistry.TryGetRoom(chunkX, chunkZ, layerIndex,
                    out int tx0, out int tz0, out int entry, out int quarter))
                return;

            var pool = AuthoredRoomPool();
            if (pool == null || pool.rooms == null)
                return;

            // Un índice fuera de rango significa que el pool del cliente y el manifiesto que leyó el
            // backend NO son el mismo. Es exactamente el fallo que el digest del handshake existe
            // para cazar; aquí, al menos, no se instancia una sala equivocada en silencio.
            if (entry < 0 || entry >= pool.rooms.Length)
            {
                if (!_loggedAuthoredRoomMismatch)
                {
                    _loggedAuthoredRoomMismatch = true;
                    Debug.LogError($"[GridChunkBuilder] El backend pidió la sala {entry} y el pool " +
                                   $"tiene {pool.rooms.Length}. Pool y manifiesto desparejados: " +
                                   "ejecuta Backrooms ▸ Export Room Manifest y reinicia la sesión.");
                }
                return;
            }

            var room = pool.rooms[entry];
            if (room == null || room.prefab == null || room.tilesX < 1 || room.tilesZ < 1)
                return;

            // Footprint YA GIRADO, misma cuenta que `ManifestRoom::footprint_cells` en Rust: un
            // cuarto impar intercambia los ejes.
            bool swapped = quarter == 1 || quarter == 3;
            int tw = swapped ? room.tilesZ : room.tilesX;
            int th = swapped ? room.tilesX : room.tilesZ;

            into.Add(new RoomPlan
            {
                prefab = room.prefab,
                // El pivote de la sala es el CENTRO de su footprint (contrato de la herramienta de
                // autoría): con el centro, girar 90° no descoloca la pieza.
                localCenter = new Vector3((tx0 + tw * 0.5f) * Ts, 0f, (tz0 + th * 0.5f) * Ts),
                yaw = quarter * 90f,
                tx0 = tx0,
                tz0 = tz0,
                tx1 = tx0 + tw,
                tz1 = tz0 + th,
            });
        }

        /// <summary>Instancia las salas ya planificadas bajo el root del chunk.</summary>
        private static void PlaceAuthoredRooms(Transform parent, List<RoomPlan> plans)
        {
            for (int i = 0; i < plans.Count; i++)
                Instantiate(plans[i].prefab, parent, plans[i].localCenter, plans[i].yaw);
        }

        /// <summary>True si (tx, tz) cae dentro de alguna sala autorada de este chunk.</summary>
        private static bool IsAuthoredRoomTile(List<RoomPlan> plans, int tx, int tz)
        {
            for (int i = 0; i < plans.Count; i++)
                if (plans[i].ContainsTile(tx, tz)) return true;
            return false;
        }
    }
}
