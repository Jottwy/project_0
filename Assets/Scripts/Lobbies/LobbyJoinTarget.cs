using System;
using System.Globalization;

namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// Todo lo que un lobby de Steam anuncia para ENTRAR, leído de su metadata: el endpoint directo,
    /// la LAN alternativa, la sesión de relay (ADR-117) y la vía Steam (ADR-135).
    ///
    /// Existe por ADR-136 D8: la ruta de invitación (`HandleLobbyEntered`) leía sólo `connect_ip` y
    /// `connect_port` y llamaba a la sobrecarga de tres argumentos, así que una invitación entraba
    /// **sin relay y sin vía Steam**, a pelo contra `ip:puerto`, mientras el navegador de servidores
    /// (`SteamLobbyRecord.TryMap`) sí leía las tres. Aquí está la lectura UNA vez, con las mismas
    /// claves de <see cref="SteamLobbyKeys"/> que usa el navegador, y sin Steamworks para poder
    /// probarla en el arnés.
    ///
    /// Mismas reglas de bloque que el navegador: media sesión de relay no es relay, y un `SteamId`
    /// ilegible es «sin vía Steam», no un error.
    /// </summary>
    public readonly struct LobbyJoinTarget
    {
        /// Endpoint directo, o `null` si el host no publicó ninguno defendible (ADR-117 D7).
        public readonly string Ip;

        public readonly int Port;

        /// `bs_lan_ip`, para el reintento por hairpin. `null` si no la hay.
        public readonly string FallbackIp;

        public readonly LobbyRelay Relay;

        public readonly LobbySteamHost SteamHost;

        public LobbyJoinTarget(string ip, int port, string fallbackIp, LobbyRelay relay, LobbySteamHost steamHost)
        {
            Ip = ip;
            Port = port;
            FallbackIp = fallbackIp;
            Relay = relay;
            SteamHost = steamHost;
        }

        public bool HasDirect => Ip != null && Port > 0;

        /// ADR-117 D7 y ADR-135: basta con UNA de las tres vías.
        public bool HasSomeWayIn => HasDirect || Relay.IsValid || SteamHost.IsValid;

        /// <summary>
        /// Lee las claves del lobby. `getData` es `lobby.GetData` de Facepunch, o cualquier
        /// diccionario en un test; una clave ausente devuelve `null` o vacío y las dos valen igual.
        /// </summary>
        public static LobbyJoinTarget FromMetadata(Func<string, string> getData)
        {
            if (getData == null) throw new ArgumentNullException(nameof(getData));

            string ip = Clean(getData(SteamLobbyKeys.ConnectIp));
            int port = ParsePort(getData(SteamLobbyKeys.ConnectPort));
            // Un endpoint directo son las DOS cosas: una IP sin puerto no es a dónde llamar.
            if (ip == null || port == 0)
            {
                ip = null;
                port = 0;
            }

            var relay = new LobbyRelay(
                getData(SteamLobbyKeys.RelayAddr),
                getData(SteamLobbyKeys.RelaySession),
                getData(SteamLobbyKeys.RelayToken));
            var steamHost = new LobbySteamHost(
                ParseSteamId(getData(SteamLobbyKeys.SteamHost)),
                getData(SteamLobbyKeys.SteamAuth));

            return new LobbyJoinTarget(ip, port, Clean(getData(SteamLobbyKeys.LanIp)), relay, steamHost);
        }

        private static string Clean(string raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

        private static int ParsePort(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)) return 0;
            return port > 0 && port <= 65535 ? port : 0;
        }

        private static ulong ParseSteamId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0UL;
            return ulong.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong id) ? id : 0UL;
        }

        /// Sin token ni secreto: esto se registra.
        public override string ToString() =>
            (HasDirect ? Ip + ":" + Port : "<sin directo>") +
            (FallbackIp != null ? " lan=" + FallbackIp : "") +
            " relay " + Relay + ", " + SteamHost;
    }
}
