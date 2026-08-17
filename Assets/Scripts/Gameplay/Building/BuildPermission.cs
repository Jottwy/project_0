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
    /// La zona sale de <see cref="ZoneRegistry"/>, que ya guarda el `zone_kind` de la CAPA 0 de cada
    /// columna leído del snapshot IPC. Que sea la capa 0 no es un atajo: es exactamente el criterio
    /// que aplica el host (ver `position_is_buildable`), y mantenerlos en fase es lo que evita que el
    /// aviso del cliente y la decisión del host discrepen justo donde el jugador está mirando.
    /// </summary>
    public static class BuildPermission
    {
        /// <summary>
        /// `ZONE_SAFE` del backend (`world/chunk/surface_profiles.rs`). Es el único `zone_kind`
        /// construible del mundo — decisión de ADR-081, pieza 1.
        /// </summary>
        public const byte BuildableZoneKind = 2;

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
        /// Espejo de `CLAIM_BLOCK_M` (backend/src/game_loop.rs): 2 × 2 tiles de 5 m.
        ///
        /// Enmienda 4 a ADR-081. Un lado de 10 m son exactamente 2 de las paredes construibles que
        /// existen (8 para cerrar el recinto), y divide EXACTO los 50 m del chunk — así ninguna
        /// casilla queda partida entre dos chunks, que era el coste declarado de los 15 m.
        /// </summary>
        public const float ClaimBlockMeters = 10f;

        /// <summary>
        /// True si <paramref name="worldPosition"/> cae en una columna de zona construible.
        ///
        /// Una zona todavía DESCONOCIDA (el snapshot con ese chunk aún no ha llegado) cuenta como no
        /// construible a propósito: es un transitorio de arranque de un par de frames, y decir "sí"
        /// mientras no se sabe pondría al jugador a colocar piezas que el host va a rechazar.
        ///
        /// Solo esta función depende de <see cref="ZoneRegistry"/>, que se puebla del snapshot IPC en
        /// vivo y por tanto es Play-only — misma frontera declarada que `ChunkLootRollTests`. Las dos
        /// mitades comprobables sin Play viven aparte, abajo, y son las que los tests EditMode cubren.
        /// </summary>
        public static bool CanBuildAt(Vector3 worldPosition)
        {
            var (cx, cz) = ChunkOf(worldPosition);
            if (!ZoneRegistry.TryGetZone(cx, cz, out byte zoneKind) || !IsBuildableZone(zoneKind))
                return false;

            // ADR-081 fase 3, espejo de la regla del host (`process_stp_place`): en zona construible
            // solo se construye dentro del claim propio. Sin claim, lo único colocable es el marcador
            // — y eso lo decide `CanPlaceAt`, no esta función, que responde a "¿puedo construir aquí?"
            // y por tanto habla de piezas normales.
            return ClaimOwnerAt(worldPosition) == LocalPeerId();
        }

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
            /// <summary>El snapshot IPC todavía no ha traído el `zone_kind` de esta columna.</summary>
            ZoneUnknown,
            /// <summary>La columna es de una zona que no se construye.</summary>
            ZoneNotBuildable,
            /// <summary>Terreno sin reclamar y la pieza no es el marcador.</summary>
            Unclaimed,
            /// <summary>El bloque tiene dueño y no eres tú.</summary>
            ClaimedByOther,
            /// <summary>Es el marcador, pero este bloque ya está reclamado.</summary>
            AlreadyClaimed,
        }

        /// <summary>
        /// La regla completa con su motivo. `CanPlaceAt` es esta función mirando solo si el veredicto
        /// es <see cref="Verdict.Allowed"/>; el motivo lo consume la traza de diagnóstico.
        /// </summary>
        public static Verdict Explain(Vector3 worldPosition, int defId)
        {
            var (cx, cz) = ChunkOf(worldPosition);
            if (!ZoneRegistry.TryGetZone(cx, cz, out byte zoneKind))
                return Verdict.ZoneUnknown;
            if (!IsBuildableZone(zoneKind))
                return Verdict.ZoneNotBuildable;

            ushort owner = ClaimOwnerAt(worldPosition);
            if (defId == ClaimMarkerDefId)
                return owner == 0 ? Verdict.Allowed : Verdict.AlreadyClaimed;

            if (owner == 0)
                return Verdict.Unclaimed;

            return owner == LocalPeerId() ? Verdict.Allowed : Verdict.ClaimedByOther;
        }

        /// <summary>
        /// Bloque de la rejilla GLOBAL de claims que contiene <paramref name="worldPosition"/>.
        /// Espejo de `claim_block` (backend/src/game_loop.rs).
        ///
        /// `FloorToInt` y no un cast: truncar hacia cero haría el bloque del origen del doble de
        /// ancho en las dos direcciones, comiéndose el de sus vecinos al oeste y al norte.
        /// </summary>
        public static (int bx, int bz) ClaimBlockOf(Vector3 worldPosition) => (
            Mathf.FloorToInt(worldPosition.x / ClaimBlockMeters),
            Mathf.FloorToInt(worldPosition.z / ClaimBlockMeters));

        /// <summary>Esquina de menor coordenada del bloque, en el mundo.</summary>
        public static Vector3 ClaimBlockOrigin(int bx, int bz) =>
            new Vector3(bx * ClaimBlockMeters, 0f, bz * ClaimBlockMeters);

        /// <summary>
        /// Dueño del claim que cubre <paramref name="worldPosition"/>, o 0 si el terreno está libre.
        ///
        /// Derivado de los marcadores replicados, exactamente como lo deriva el host de su propia
        /// lista — no hay tabla de claims que pueda desincronizarse. Se compara el BLOQUE, no la
        /// distancia: un claim es una casilla de una rejilla de suelo, y por eso la Y no entra.
        ///
        /// Sin snapshot IPC devuelve 0 (terreno libre); es el mismo transitorio de arranque que la
        /// zona desconocida, y lo cubre el hecho de que la zona se comprueba ANTES en las dos
        /// funciones de arriba.
        /// </summary>
        public static ushort ClaimOwnerAt(Vector3 worldPosition)
        {
            if (!IPCClient.TryGetInstance(out var ipc) || ipc.LatestState == null)
                return 0;

            var block = ClaimBlockOf(worldPosition);
            var buildings = ipc.LatestState.stpBuildings;
            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                if (b.defId != ClaimMarkerDefId)
                    continue;

                if (ClaimBlockOf(b.position) == block)
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

        /// <summary>La regla en sí: de los 13 `zone_kind` del backend, solo uno se construye.</summary>
        public static bool IsBuildableZone(byte zoneKind) => zoneKind == BuildableZoneKind;
    }
}
