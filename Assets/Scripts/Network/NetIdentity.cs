using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-111 — **la identidad de red de este cliente, y de dónde sale.**
    ///
    /// Hay DOS números y hasta ADR-111 Unity sólo conocía el primero:
    ///
    /// <list type="number">
    ///   <item><b>PROPUESTO</b> — <see cref="NetworkInitializer.LastSelectedNetId"/>, el
    ///   <c>NET_ID</c> con el que Unity lanzó su backend. Es una <i>petición</i>: por defecto 1,
    ///   o lo que hubiera en el entorno.</item>
    ///   <item><b>ASIGNADO</b> — el que el host acuña en el handshake
    ///   (<c>allocate_peer_id</c>) y que el backend del joiner adopta
    ///   (<c>self.local_id = assigned_id</c>). Es el que viaja en la cabecera de cada paquete, el
    ///   que acaba en cada <c>owner_id</c> y el que el host usa para deduplicar. <b>Es la única
    ///   identidad autoritativa.</b></item>
    /// </list>
    ///
    /// Los dos coinciden en el host (nadie le asigna nada: la suya es la que se dio al hacer
    /// bind) y pueden no coincidir en un joiner — si el propuesto ya estaba cogido, el host
    /// asigna otro sin avisar a Unity. Con el defecto (1, que es el id del host) un joiner que
    /// se quedara con el propuesto se creía literalmente el host.
    ///
    /// El asignado llega por <c>world_state.local_player_id</c> (10 Hz) y se adopta en
    /// <see cref="IPCClient"/>. Mientras no haya llegado ningún snapshot, <see cref="Local"/>
    /// devuelve el propuesto: es lo único que hay, y para el host es además correcto.
    ///
    /// <b>Es estado ESTÁTICO a propósito</b>, y no un campo de <see cref="NetworkInitializer"/>:
    /// lo leen catorce sitios repartidos por tres ensamblados lógicos (red, construcción,
    /// gameplay), varios de ellos <c>static</c> ellos mismos (<c>MintDropId</c>,
    /// <c>BuildPermission.LocalPeerId</c>), y <see cref="Resolve"/> tiene que poder probarse sin
    /// crear un <c>GameObject</c>.
    /// </summary>
    public static class NetIdentity
    {
        /// <summary>«Todavía no lo sé». Nunca es un id válido: <c>allocate_peer_id</c> jamás
        /// devuelve 0.</summary>
        public const int Unknown = 0;

        /// <summary>
        /// Primer id reservado a las CRIATURAS. Espejo de `FACELING_ID_BASE`
        /// (`backend/src/network/mod.rs:55`), con los robapieles por encima en `PHANTOM_ID_BASE`
        /// (0xF000 = 61440): todo id a partir de aquí es una entidad, nunca una persona.
        ///
        /// Existe porque las criaturas viajan por el MISMO stream que los jugadores —comparten
        /// `PlayerUpdate`—, así que sin este corte «cuántos hay conectados» cuenta facelings y
        /// robapieles. Medido el 10-09 en partida real: el contador decía 24 con UNA persona
        /// dentro.
        /// </summary>
        public const int CreatureIdBase = 61000;

        /// <summary>¿Este id es de una persona? Ver <see cref="CreatureIdBase"/>.</summary>
        public static bool IsHuman(int id) => id > Unknown && id < CreatureIdBase;

        /// <summary>
        /// Escrito por el hilo de red de <see cref="IPCClient"/> (al parsear el snapshot) y leído
        /// desde el hilo principal. Un solo escritor, un <c>int</c>: <c>volatile</c> basta y no
        /// hace falta <c>Interlocked</c> — mismo criterio que <c>IPCClient._connectionEpoch</c>.
        /// </summary>
        private static volatile int _assigned;

        /// <summary>El id que el backend dice tener. <see cref="Unknown"/> si aún no habló.</summary>
        public static int Assigned => _assigned;

        /// <summary>El <c>NET_ID</c> que Unity pidió al lanzar el backend. NO es autoritativo.</summary>
        public static int Proposed =>
            NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : Unknown;

        /// <summary>
        /// **El id que debe usar todo consumidor.** El asignado en cuanto se conoce; el propuesto
        /// mientras tanto.
        /// </summary>
        public static int Local => Resolve(_assigned, Proposed);

        /// <summary>
        /// La regla, aislada y pura para poder probarla: <b>el asignado siempre gana</b>. El
        /// propuesto es sólo el puente hasta el primer snapshot — nunca lo pisa después, ni
        /// siquiera si el propuesto «parece» más razonable.
        /// </summary>
        public static int Resolve(int assigned, int proposed)
        {
            if (assigned > 0)
                return assigned;
            return proposed > 0 ? proposed : Unknown;
        }

        /// <summary>
        /// Adopta el id que vino en <c>world_state.local_player_id</c>. Idempotente: llega 10
        /// veces por segundo y sólo el cambio deja rastro en el log.
        ///
        /// Rechaza lo que no puede ser un <c>PeerId</c> (0 o fuera de <c>u16</c>): un backend
        /// viejo, que no manda el campo, decodifica 0 — y un 0 adoptado como identidad haría que
        /// los prefijos de id de petición de todos los clientes colapsaran al mismo espacio.
        /// </summary>
        /// <returns>true si la identidad ha cambiado con esta llamada.</returns>
        public static bool Adopt(int assignedId)
        {
            if (assignedId <= 0 || assignedId > ushort.MaxValue)
                return false;

            int previous = _assigned;
            if (previous == assignedId)
                return false;

            _assigned = assignedId;
            // El log NO lee `Proposed`: esto lo llama el hilo de red de `IPCClient`, y `Proposed`
            // toca `NetworkInitializer.Instance`, que es un `UnityEngine.Object` y sólo se puede
            // consultar con garantías desde el hilo principal. El propuesto ya sale en el log de
            // lanzamiento y en el HUD de depuración.
            Debug.Log(
                $"[NetIdentity] id autoritativo {(previous == Unknown ? "recibido" : "corregido")}: " +
                $"{previous} → {assignedId}");
            return true;
        }

        /// <summary>
        /// Olvida el id asignado. Se llama al ABRIR una conexión IPC, no al cerrarla: cada sesión
        /// lanza un backend nuevo y el host puede asignar otro id: conservarlo entre conexiones es
        /// exactamente el fallo obsoleto que ADR-111 arregla, sólo que una sesión más tarde.
        /// </summary>
        public static void ResetForNewConnection()
        {
            if (_assigned != Unknown)
                Debug.Log($"[NetIdentity] conexión nueva: se olvida el id asignado {_assigned}");
            _assigned = Unknown;
        }

        /// <summary>
        /// Los estáticos sobreviven a la recarga de escena y, en el editor, al Play anterior.
        /// Mismo patrón (y mismo motivo) que <c>PlayerIdentity.ResetStatics</c>.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _assigned = Unknown;
    }
}
