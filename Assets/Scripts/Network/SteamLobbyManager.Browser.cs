using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Lo que el navegador de servidores necesita de Steam, en una partición aparte para no
    /// tocar el spike original.
    ///
    /// El spike publicaba tres claves (ip, puerto, nombre de host) porque su único consumidor era
    /// el auto-join por invitación: quien recibía el invite ya sabía a qué partida iba. Un
    /// navegador tiene que decidir ANTES de entrar —si cabe, si la versión sirve, si sigue viva—
    /// y para eso hacen falta más metadatos.
    ///
    /// **El lobby sigue siendo UNO y con un solo dueño.** Todo pasa por `_hostedLobby`, así que
    /// el botón "Invite via Steam" y el anuncio automático del navegador no pueden crear dos.
    /// </summary>
    public sealed partial class SteamLobbyManager
    {
        /// Marca de juego. Sin esto, la consulta devolvería los lobbies de CUALQUIERA que esté
        /// usando el mismo App ID — y en desarrollo ése es Spacewar (480), el id público de
        /// pruebas de Valve, compartido por todo el que prueba Steamworks. Es el filtro que hace
        /// la lista utilizable. Con el App ID propio (ver <see cref="SteamAppConfig"/>) deja de
        /// ser imprescindible, pero se mantiene: es gratis y protege del día en que alguien
        /// arranque con `BS_STEAM_APPID=480`.
        public const string GameKey = "bs_game";
        public const string GameValue = "backrooms_survival";

        /// Versión de WIRE, no del build. Ver <see cref="WireSchema.Expected"/>.
        public const string WireVersionKey = "bs_wire";

        public const string LobbyNameKey = "bs_name";
        public const string PlayersKey = "bs_players";
        public const string MaxPlayersKey = "bs_max";
        public const string MapKey = "bs_map";
        public const string StateKey = "bs_state";

        /// Segundos Unix del último anuncio. Es el reloj DEL HOST, no el nuestro: quien lo lea
        /// tiene que traducirlo (ver `docs/SERVER_BROWSER.md`, §reloj).
        public const string AnnouncedAtKey = "bs_at";

        /// La dirección LAN del host. Ver `SteamLobbyKeys.LanIp`: cuando `connect_ip` es la IP
        /// pública, ésta es la que sirve a un joiner de la misma red.
        public const string LanIpKey = "bs_lan_ip";

        /// ADR-117: las tres del relay. Espejo de `SteamLobbyKeys.Relay*`, y `SteamLobbyKeyParity`
        /// comprueba que no se separen — si divergen, el host publicaría con unas claves y el
        /// navegador leería con otras, y el relay quedaría invisible sin un solo error.
        public const string RelayAddrKey = "bs_relay_addr";

        public const string RelaySessionKey = "bs_relay_session";

        public const string RelayTokenKey = "bs_relay_token";

        /// Tope de resultados de una consulta. Steam no promete devolverlos todos.
        public const int DefaultMaxQueryResults = 50;

        public static bool HasHostedLobby => _instance != null && _instance._hostedLobby.HasValue;

        public static ulong HostedLobbyId =>
            _instance != null && _instance._hostedLobby.HasValue ? _instance._hostedLobby.Value.Id.Value : 0UL;

        /// <summary>
        /// Asegura el lobby propio con ese ip:puerto. Devuelve false si Steam no está, si la
        /// creación falló o si todavía está en vuelo — el llamante reintenta en el siguiente
        /// latido, que es lo que hace <c>SteamLobbyPublisher</c>.
        ///
        /// No es `async`: el publicador vive en un `Update` y no puede esperar. La creación se
        /// dispara y el resultado se recoge en la siguiente llamada por
        /// <see cref="HasHostedLobby"/>.
        /// </summary>
        public static bool TryEnsureHostedLobby(string connectIp, int connectPort)
        {
            if (!IsAvailable) return false;
            if (HasHostedLobby) return true;

            _ = _instance.CreateSteamLobby(connectIp, connectPort);
            return false;
        }

        /// <summary>Escribe un metadato en el lobby propio. False si no hay lobby o Steam se fue.</summary>
        public static bool TrySetHostedData(string key, string value)
        {
            if (!IsAvailable || _instance == null || !_instance._hostedLobby.HasValue) return false;

            try
            {
                _instance._hostedLobby.Value.SetData(key, value ?? "");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SteamLobbyManager] SetData({key}) failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Cierra el lobby propio y deja de anunciarlo. Idempotente y segura sin Steam: es la que
        /// llama el teardown, que también corre cuando Steam nunca arrancó.
        ///
        /// Ojo con la diferencia respecto a <see cref="LeaveLobby"/>: aquélla suelta ADEMÁS el
        /// lobby ajeno en el que se hubiera entrado por invitación, y eso no es cosa de retirar
        /// un anuncio.
        /// </summary>
        public static void CloseHostedLobby()
        {
            if (_instance == null) return;

            // El epoch sube SIEMPRE, también sin lobby que cerrar: una creación en vuelo tiene que
            // quedar desautorizada aunque el teardown llegue antes de que Steam conteste. Ver
            // `_lobbyEpoch`.
            _instance._lobbyEpoch++;

            if (!_instance._hostedLobby.HasValue) return;

            Lobby hosted = _instance._hostedLobby.Value;
            bool sameAsCurrent = _instance._currentLobby.HasValue &&
                                 _instance._currentLobby.Value.Id.Value == hosted.Id.Value;

            TryLeave(hosted);
            _instance._hostedLobby = null;
            if (sameAsCurrent) _instance._currentLobby = null;

            Debug.Log($"[SteamLobbyManager] Hosted lobby {hosted.Id.Value} closed (anuncio retirado).");
        }

        /// <summary>
        /// Pide la lista de lobbies de ESTE juego. Devuelve null si Steam no está disponible; el
        /// llamante distingue "no hay Steam" de "no hay partidas", que no es lo mismo.
        /// </summary>
        public static Task<Lobby[]> QueryLobbiesAsync(int maxResults = DefaultMaxQueryResults)
        {
            if (!IsAvailable) return null;

            try
            {
                return SteamMatchmaking.LobbyList
                    .FilterDistanceWorldwide()
                    .WithKeyValue(GameKey, GameValue)
                    .WithMaxResults(maxResults <= 0 ? DefaultMaxQueryResults : maxResults)
                    .RequestAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SteamLobbyManager] LobbyList.RequestAsync threw: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Traduce lo que Steam devuelve a fichas planas, SIN tocar el modelo de lobby. La
        /// conversión de verdad (validación, TTL, versión) vive en <c>SteamLobbyMapper</c>, que
        /// no depende de Steam y por eso se puede probar.
        /// </summary>
        public static List<Lobbies.SteamLobbyRecord> ToRecords(Lobby[] lobbies)
        {
            var records = new List<Lobbies.SteamLobbyRecord>();
            if (lobbies == null) return records;

            for (int i = 0; i < lobbies.Length; i++)
            {
                Lobby lobby = lobbies[i];
                try
                {
                    records.Add(new Lobbies.SteamLobbyRecord
                    {
                        Id = lobby.Id.Value,
                        ConnectIp = lobby.GetData(ConnectIpKey),
                        ConnectPort = lobby.GetData(ConnectPortKey),
                        HostName = lobby.GetData(HostNameKey),
                        Name = lobby.GetData(LobbyNameKey),
                        WireVersion = lobby.GetData(WireVersionKey),
                        Players = lobby.GetData(PlayersKey),
                        MaxPlayers = lobby.GetData(MaxPlayersKey),
                        Map = lobby.GetData(MapKey),
                        State = lobby.GetData(StateKey),
                        AnnouncedAt = lobby.GetData(AnnouncedAtKey),
                        LanIp = lobby.GetData(LanIpKey),
                        RelayAddr = lobby.GetData(RelayAddrKey),
                        RelaySession = lobby.GetData(RelaySessionKey),
                        RelayToken = lobby.GetData(RelayTokenKey),
                        MemberCount = lobby.MemberCount,
                        MemberCapacity = lobby.MaxMembers,
                    });
                }
                catch (Exception e)
                {
                    // Un lobby que se cae entre la consulta y la lectura no puede llevarse la
                    // lista entera por delante.
                    Debug.LogWarning($"[SteamLobbyManager] Lobby {lobby.Id.Value} unreadable: {e.Message}");
                }
            }

            return records;
        }
    }
}
