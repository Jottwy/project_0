using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// ADR-081 fase 1 — ¿se puede construir en esta posición del mundo?
    ///
    /// ESTO NO ES AUTORIDAD. La autoridad vive en `process_stp_place` del backend, que aplica esta
    /// misma regla contra el resolutor puro por seed (`zone_density::zone_kind_for`) y rechaza sin
    /// preguntar al cliente. Lo de aquí es únicamente FEEDBACK: sirve para que el jugador se entere
    /// antes de gastar el gesto, y para no mandarle al host una petición que el host ya va a tirar.
    /// Si esta clase mintiera diciendo que sí, no pasaría nada: el host seguiría diciendo que no.
    ///
    /// Desde la enmienda 5 el sitio construible NO es una zona sino una HABITACIÓN tallada
    /// (<see cref="BuildRoomRegistry"/>), y su posición llega del backend con el propio chunk. El
    /// cliente no la deriva porque no puede: el emplazamiento sortea con el `StdRng` de Rust.
    /// </summary>
    public static class BuildPermission
    {

        /// <summary>
        /// Lo que ve el jugador cuando la regla le para, además del fantasma en rojo de
        /// <see cref="BuildPlacementFeedback"/>. Lo emite `StpBuildingPlacementWatcher` al descartar
        /// una colocación que el host iba a rechazar; vive aquí para que la regla tenga UN texto.
        /// </summary>
        public const string DeniedMessage = "Aquí no se puede construir.";

        /// <summary>
        /// Espejo de `CLAIM_MARKER_DEF_ID` (backend/src/game_loop.rs) — el id de
        /// `Assets/Resources/Definitions/BuildingPiece/BR_Claim Marker.asset`, autorado por
        /// "Backrooms ▸ Create Building Pieces". Si ese asset se regenerase mintaría un id nuevo y
        /// las dos constantes dejarían de casar a la vez; el menú se niega a regenerarlo por eso.
        /// </summary>
        public const int ClaimMarkerDefId = -1977919096;


        /// <summary>
        /// La regla completa, con la pieza en la mano: el marcador se coloca en terreno sin reclamar,
        /// todo lo demás dentro del claim propio. Espejo literal de la puerta del host.
        /// </summary>
        public static bool CanPlaceAt(Vector3 worldPosition, int defId) =>
            Explain(worldPosition, defId) == Verdict.Allowed;

        /// <summary>Por qué la regla dice que no. Existe para poder TRAZARLO: sin esto, "no me deja
        /// construir" es indistinguible entre cuatro causas muy distintas.</summary>
        public enum Verdict
        {
            Allowed,
            /// <summary>El chunk de esta columna aún no ha llegado: no se sabe qué hay.</summary>
            ZoneUnknown,
            /// <summary>El chunk se conoce y aquí no hay habitación construible.</summary>
            ZoneNotBuildable,
            /// <summary>Terreno sin reclamar y la pieza no es el marcador.</summary>
            Unclaimed,
            /// <summary>La habitación tiene dueño y no eres tú.</summary>
            ClaimedByOther,
            /// <summary>Es el marcador, pero esta habitación ya está reclamada.</summary>
            AlreadyClaimed,
        }

        /// <summary>
        /// La regla completa con su motivo. `CanPlaceAt` es esta función mirando solo si el veredicto
        /// es <see cref="Verdict.Allowed"/>; el motivo lo consume la traza de diagnóstico.
        /// </summary>
        public static Verdict Explain(Vector3 worldPosition, int defId)
        {
            // ADR-108 D6 — con WG3 mandando, el sitio construible es un ESPACIO con papel de
            // servicio, almacén o callejón sin salida. La habitación tallada de WG2 no existe en
            // este mundo, así que `BuildRoomRegistry` diría que no en todas partes mientras el host
            // dice que sí en otras: dos reglas sobre dos mapas distintos.
            if (Wg3Active(out var streamer))
            {
                if (!streamer.ChunkIsBuilt(worldPosition))
                    return Verdict.ZoneUnknown;
                if (!streamer.TryGetSpace(worldPosition, out Bounds box, out byte style) ||
                    !IsBuildableStyle(style))
                    return Verdict.ZoneNotBuildable;

                ushort ownerWg3 = ClaimOwnerAtWg3(streamer, box);
                if (defId == ClaimMarkerDefId)
                    return ownerWg3 == 0 ? Verdict.Allowed : Verdict.AlreadyClaimed;
                if (ownerWg3 == 0)
                    return Verdict.Unclaimed;
                return ownerWg3 == LocalPeerId() ? Verdict.Allowed : Verdict.ClaimedByOther;
            }

            var (cx, cz) = ChunkOf(worldPosition);
            if (!ChunkKnown(cx, cz))
                return Verdict.ZoneUnknown;
            if (!BuildRoomRegistry.Contains(worldPosition))
                return Verdict.ZoneNotBuildable;

            ushort owner = ClaimOwnerAt(worldPosition);
            if (defId == ClaimMarkerDefId)
                return owner == 0 ? Verdict.Allowed : Verdict.AlreadyClaimed;

            if (owner == 0)
                return Verdict.Unclaimed;

            return owner == LocalPeerId() ? Verdict.Allowed : Verdict.ClaimedByOther;
        }

        /// <summary>
        /// ADR-108 D6 — los papeles en los que SÍ se construye. Espejo de `WG3_BUILDABLE_STYLES`
        /// (backend/src/game_loop.rs): servicio/almacén (4) y callejón sin salida (5), o sea los
        /// espacios que cuelgan del mundo sin sostenerlo. La espina, el pasillo y el cruce quedan
        /// fuera porque construir ahí le corta el paso a todos los demás.
        /// </summary>
        public static bool IsBuildableStyle(byte style) => style == 4 || style == 5;

        /// <summary>El streamer de WG3 si este mundo es de WG3. `Wg3Enabled` lo dice el backend en el
        /// saludo: misma fuente de verdad que usa el resto del cliente, no una bandera nueva.</summary>
        private static bool Wg3Active(out BackroomsSurvival.WorldGen3.Wg3ChunkStreamer streamer)
        {
            streamer = null;
            if (IPCClient.Instance is not { Wg3Enabled: true }) return false;
            streamer = BackroomsSurvival.WorldGen3.Wg3ChunkStreamer.Active;
            return streamer != null;
        }

        /// <summary>
        /// Dueño del claim de WG3 que cubre ese espacio. Espejo de `claim_owner_at_wg3`: el claim es
        /// el ESPACIO, y dos posiciones son del mismo claim si caen en la misma caja. La caja se
        /// compara por su esquina en centímetros —lo mismo que compara el host— y no por igualdad de
        /// `Bounds`, que es de floats.
        /// </summary>
        private static ushort ClaimOwnerAtWg3(
            BackroomsSurvival.WorldGen3.Wg3ChunkStreamer streamer, Bounds box)
        {
            if (!IPCClient.TryGetInstance(out var ipc) || ipc.LatestState == null)
                return 0;

            var key = KeyOfBox(box);
            var buildings = ipc.LatestState.stpBuildings;
            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                if (b.defId != ClaimMarkerDefId)
                    continue;
                var pos = new Vector3(b.position[0], b.position[1], b.position[2]);
                if (!streamer.TryGetSpace(pos, out Bounds other, out _))
                    continue;
                if (KeyOfBox(other) == key)
                    return b.ownerId;
            }
            return 0;
        }

        private static (int x, int z, int y) KeyOfBox(Bounds box) => (
            Mathf.RoundToInt(box.min.x * 100f),
            Mathf.RoundToInt(box.min.z * 100f),
            Mathf.RoundToInt(box.min.y * 100f));

        /// <summary>
        /// Identidad del claim que cubre <paramref name="worldPosition"/>. Espejo de `claim_key`
        /// (backend/src/game_loop.rs).
        ///
        /// Enmienda 5: **la HABITACIÓN es el claim**. Como el backend talla como mucho UNA por
        /// chunk, la coordenada de chunk la identifica — dos posiciones son del mismo claim si y
        /// solo si están en el mismo chunk. Fuera la rejilla de bloques de 10 m de la enmienda 4:
        /// reclamar una casilla abstracta dejó de tener sentido cuando la sala pasó a estar tallada.
        /// </summary>
        public static (int cx, int cz) ClaimKeyOf(Vector3 worldPosition) => ChunkOf(worldPosition);

        /// <summary>
        /// Dueño del claim que cubre <paramref name="worldPosition"/>, o 0 si el terreno está libre.
        ///
        /// Derivado de los marcadores replicados, exactamente como lo deriva el host de su propia
        /// lista — no hay tabla de claims que pueda desincronizarse. Se compara la HABITACIÓN (vía
        /// su chunk), no la distancia: un claim es una sala, y por eso la Y no entra.
        ///
        /// Sin snapshot IPC devuelve 0 (terreno libre); es el mismo transitorio de arranque que la
        /// zona desconocida, y lo cubre el hecho de que la zona se comprueba ANTES en las dos
        /// funciones de arriba.
        /// </summary>
        public static ushort ClaimOwnerAt(Vector3 worldPosition)
        {
            if (!IPCClient.TryGetInstance(out var ipc) || ipc.LatestState == null)
                return 0;

            var key = ClaimKeyOf(worldPosition);
            var buildings = ipc.LatestState.stpBuildings;
            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                if (b.defId != ClaimMarkerDefId)
                    continue;

                if (ClaimKeyOf(b.position) == key)
                    return b.ownerId;
            }

            return 0;
        }

        /// <summary>
        /// El `PeerId` de este cliente — el mismo `NET_ID` que el backend estampa en la cabecera de
        /// cada paquete y que acaba en `owner_id`. 0 si no hay sesión: nunca dueño de nada.
        /// </summary>
        public static ushort LocalPeerId()
        {
            int netId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
            return netId > 0 && netId <= ushort.MaxValue ? (ushort)netId : (ushort)0;
        }

        /// <summary>
        /// Columna de chunk que contiene <paramref name="worldPosition"/>.
        ///
        /// `FloorToInt` y no un cast a int: truncar hacia cero manda todo el intervalo (-50, 50) al
        /// chunk 0, o sea que la mitad oeste/norte del mundo leería la zona de su vecino. Público a
        /// propósito para poder probarlo — el compile-check headless construye la asamblea con sufijo
        /// `_check`, así que un `InternalsVisibleTo` nunca casaría (mismo motivo que en `WireSchema`).
        /// </summary>
        public static (int cx, int cz) ChunkOf(Vector3 worldPosition) => (
            Mathf.FloorToInt(worldPosition.x / SprayMsg.ChunkSize),
            Mathf.FloorToInt(worldPosition.z / SprayMsg.ChunkSize));

        /// <summary>
        /// ¿Ha llegado ya el chunk de esta columna? Distingue "todavía no lo sé" de "sé que no", que
        /// es la diferencia entre un transitorio de carga y una regla del juego — y sin ella la
        /// traza de diagnóstico no podría decir cuál de las dos te paró.
        ///
        /// Se pregunta a <see cref="ZoneRegistry"/> porque su fuente (el snapshot de 10 Hz) llega
        /// para TODO chunk cargado, mientras que el registro de habitaciones solo gana una entrada
        /// cuando el chunk resulta tener sala: sin este desempate, un chunk perfectamente conocido y
        /// sin habitación sería indistinguible de uno que aún no ha llegado.
        /// </summary>
        public static bool ChunkKnown(int cx, int cz) => ZoneRegistry.TryGetZone(cx, cz, out _);
    }
}
