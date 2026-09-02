using System;
using System.Globalization;

namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// Un lobby de Steam tal cual sale de la consulta: cadenas planas y dos contadores. No sabe
    /// nada del modelo ni de Steam — existe para que la conversión se pueda probar sin cliente de
    /// Steam, que es la mitad del trabajo que de verdad se puede equivocar.
    ///
    /// Todo son `string` porque eso es literalmente lo que Steam guarda: `Lobby.GetData` devuelve
    /// cadena vacía para una clave ausente, nunca null, y nunca valida nada.
    /// </summary>
    public struct SteamLobbyRecord
    {
        public ulong Id;
        public string ConnectIp;
        public string ConnectPort;
        public string HostName;
        public string Name;
        public string WireVersion;
        public string Players;
        public string MaxPlayers;
        public string Map;
        public string State;
        public string AnnouncedAt;

        /// `bs_lan_ip`: la dirección LAN del host, cuando la publica. Sólo sirve si el joiner está
        /// en la misma red; ver <see cref="LobbyEndpoint.Alternate"/>.
        public string LanIp;

        /// ADR-117: las tres del relay, tal cual salen de Steam. Vacías cuando el host no tiene
        /// relay, que es un lobby perfectamente normal.
        public string RelayAddr;

        public string RelaySession;

        public string RelayToken;

        /// Lo que Steam sabe por sí mismo, sin metadatos. Es el respaldo cuando el host no
        /// publicó contadores.
        public int MemberCount;

        public int MemberCapacity;
    }

    /// <summary>
    /// De lobby de Steam a <see cref="Lobby"/>. Pura, sin Steam y sin Unity dentro.
    ///
    /// Tres decisiones que no son estilo:
    ///
    /// 1. **Se rechaza poco.** Sólo lo que no se puede ni pintar: sin id, o sin forma de saber el
    ///    aforo. Un lobby con el puerto ilegible SE LISTA con endpoint inválido, porque el jugador
    ///    tiene que ver que ese servidor existe y está mal anunciado, no quedarse buscándolo. El
    ///    veredicto de entrada ya lo bloquea (`InvalidEndpoint`).
    /// 2. **Sin versión de wire NO hay compatibilidad.** Un lobby que no publica `bs_wire` es de
    ///    un build viejo (o de otro juego colado por el App ID compartido): se lista, se ve, y
    ///    `EvaluateJoinability` lo marca `VersionMismatch`. Rellenarlo con la nuestra sería
    ///    inventarse que es compatible.
    /// 3. **El sello de tiempo es NUESTRO reloj, no el del host.** El host publica `bs_at` con su
    ///    hora Unix, y dos relojes distintos no se pueden restar. El TTL mide "cuánto hace que lo
    ///    vimos en una consulta", que es lo que el navegador puede afirmar de verdad.
    /// </summary>
    public static class SteamLobbyMapper
    {
        /// TTL de una ficha vista en una consulta. Holgado a propósito respecto al refresco
        /// automático del panel (15 s): el TTL está para que una lista OLVIDADA envejezca, no
        /// para pelearse con la cadencia normal.
        public const float LobbyTtlSeconds = 60f;

        public const string IdPrefix = "steam-";
        public const string DefaultName = "Partida de Steam";

        /// Steam no expone ping en el resultado de una consulta de lobbies, así que no hay ping.
        /// Inventarlo sería peor que no tenerlo: el orden por ping mentiría.
        public const int Ping = Lobby.UnknownPing;

        public static bool TryMap(SteamLobbyRecord record, double nowUnix, out Lobby lobby)
        {
            lobby = null;
            if (record.Id == 0UL) return false;

            int maxPlayers = ParseInt(record.MaxPlayers, record.MemberCapacity);
            if (maxPlayers <= 0) return false;

            int players = ParseInt(record.Players, record.MemberCount);
            string name = FirstNonEmpty(record.Name, record.HostName, DefaultName);
            string version = record.WireVersion == null ? "" : record.WireVersion.Trim();
            string map = FirstNonEmpty(record.Map, "Unknown");
            int port = ParseInt(record.ConnectPort, 0);

            return Lobby.TryCreate(
                IdPrefix + record.Id.ToString(CultureInfo.InvariantCulture),
                name,
                version,
                players,
                maxPlayers,
                map,
                // Steam no dice de qué región es un lobby sin medir latencia, y no la medimos.
                // "Unknown" es lo que hay; poner un continente inventado ensuciaría el filtro.
                "Unknown",
                Ping,
                LobbyPrivacy.Public,
                requiresPassword: false,
                record.ConnectIp,
                port,
                nowUnix,
                LobbyTtlSeconds,
                ParseState(record.State),
                // No se valida: es una dirección de LAN, así que ni es pública ni tiene por qué
                // parecerlo. Lo único que se hace con ella es reintentar cuando la principal no
                // contestó, y para entonces no hay nada que perder.
                record.LanIp,
                // ADR-117: la sesión de relay, si el host publicó las tres claves. `LobbyRelay`
                // se encarga de que dos de tres no cuenten — media sesión de relay no sirve para
                // entrar y anunciarla como si sirviera sería peor que no tenerla.
                new LobbyRelay(record.RelayAddr, record.RelaySession, record.RelayToken),
                out lobby);
        }

        /// <summary>Lo que el host publica en <c>bs_state</c>, y su vuelta.</summary>
        public static string StateToText(LobbyStatus status)
        {
            switch (status)
            {
                case LobbyStatus.Waiting: return "open";
                case LobbyStatus.InProgress: return "ingame";
                case LobbyStatus.Closed: return "closed";
                default: return "unknown";
            }
        }

        public static LobbyStatus ParseState(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return LobbyStatus.Unknown;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "open": return LobbyStatus.Waiting;
                case "ingame": return LobbyStatus.InProgress;
                case "closed": return LobbyStatus.Closed;
                default: return LobbyStatus.Unknown;
            }
        }

        private static int ParseInt(string raw, int fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback;
        }

        private static string FirstNonEmpty(params string[] candidates)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(candidates[i])) return candidates[i].Trim();
            }

            return "";
        }
    }
}
