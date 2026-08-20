using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-083 enmienda 1 — qué SALA AUTORADA va en cada chunk, según el backend.
    ///
    /// Gemelo de <see cref="BuildRoomRegistry"/>, con el mismo papel y por el mismo motivo: el dato
    /// llega con el CHUNK (respuesta a `RequestChunk`) y el constructor lo necesita más tarde, en
    /// cada reconstrucción. Sin registro, un chunk revisitado —o el reintento tras el gate de zona—
    /// se construiría sin sala aunque el backend ya haya vaciado el hueco, y quedaría una caja vacía
    /// con su pasillo y nada dentro.
    ///
    /// Solo GANA entradas: el emplazamiento es puro por seed, así que un chunk que se descarga y se
    /// vuelve a pedir trae exactamente lo mismo y no hay nada que invalidar.
    /// </summary>
    public static class AuthoredRoomRegistry
    {
        /// <summary>
        /// Las salas de UN chunk, en el orden en que las mandó el backend. Ese orden es contrato
        /// (ADR-083 enmienda 3): el constructor instancia por índice.
        /// </summary>
        private static readonly Dictionary<(int cx, int cz), GridChunkDataMsg.AuthoredRoom[]> _rooms =
            new Dictionary<(int, int), GridChunkDataMsg.AuthoredRoom[]>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _rooms.Clear();

        /// <summary>Diagnóstico: cuántos CHUNKS con sala autorada se han visto en esta sesión.</summary>
        public static int KnownRoomCount => _rooms.Count;

        /// <summary>
        /// Vacía el registro. Para los tests EditMode, que fabrican chunks a mano y no pueden
        /// heredar los de otro test. Público por el mismo motivo que su gemelo: el compile-check
        /// headless construye la asamblea con sufijo `_check` y un `InternalsVisibleTo` no casaría.
        /// </summary>
        public static void Clear_EditorTestsOnly() => _rooms.Clear();

        /// <summary>
        /// Vacía el registro al arrancar una conexión NUEVA. Sin esto, reconectar a otro mundo
        /// (seed distinta) sin reiniciar Unity deja salas fantasma del mundo anterior. Se llama
        /// junto a <see cref="BuildRoomRegistry.ResetForNewConnection"/>, que tiene la misma causa.
        /// </summary>
        public static void ResetForNewConnection() => _rooms.Clear();

        /// <summary>
        /// Registra (o no) las salas de un chunk recién llegado. Solo capa 0: es la única en la que
        /// el backend las talla, y llamar con otra capa no debe borrar lo que ya se sabe de la
        /// columna.
        /// </summary>
        public static void Observe(GridChunkDataMsg chunk)
        {
            if (chunk == null || chunk.layer != 0
                || chunk.authoredRooms == null || chunk.authoredRooms.Length == 0)
                return;

            _rooms[(chunk.cx, chunk.cz)] = chunk.authoredRooms;
        }

        /// <summary>
        /// Las salas de (cx, cz), o null si ese chunk no tiene ninguna o aún no ha llegado.
        ///
        /// Devuelve el array del registro SIN copiar: es la ruta de reconstrucción de chunk y una
        /// copia por llamada sería basura por chunk construido. Quien lo reciba solo lo lee.
        /// </summary>
        public static GridChunkDataMsg.AuthoredRoom[] GetRooms(int cx, int cz, int layer)
        {
            if (layer != 0 || !_rooms.TryGetValue((cx, cz), out var rooms))
                return null;
            return rooms;
        }
    }
}
