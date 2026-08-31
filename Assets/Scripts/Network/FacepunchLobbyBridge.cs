using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BackroomsSurvival.Lobbies;
using UnityEngine;

// `Lobby` existe en los dos mundos: el nuestro (la ficha del navegador) y el de Steam (el objeto
// de matchmaking). Este fichero es la frontera, así que aquí se nombra el de Steam con alias y no
// se importa `Steamworks.Data` a pelo — importarlo hace ambiguo cada `Lobby` del fichero.
using SteamLobby = Steamworks.Data.Lobby;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Las dos costuras contra Steam, implementadas con Facepunch. Todo lo que puede fallar de
    /// verdad —caducidad de respuestas, timeout, cero resultados, publicar dos veces— vive del
    /// otro lado del interfaz, en `SteamLobbyDirectory` y `SteamLobbyPublisher`, que se prueban
    /// sin Steam. Aquí sólo hay traducción.
    /// </summary>
    public sealed class FacepunchSteamLobbyQuery : ISteamLobbyQuery
    {
        private Task<SteamLobby[]> _task;
        private string _startupError;

        public bool IsAvailable => SteamLobbyManager.IsAvailable;

        public void Begin(int maxResults)
        {
            Abandon();

            _task = SteamLobbyManager.QueryLobbiesAsync(maxResults);
            if (_task == null) _startupError = SteamLobbyDirectory.SteamUnavailableMessage;
        }

        public SteamQueryState Poll(out IReadOnlyList<SteamLobbyRecord> records, out string error)
        {
            records = null;
            error = null;

            if (_startupError != null)
            {
                error = _startupError;
                _startupError = null;
                return SteamQueryState.Failed;
            }

            if (_task == null) return SteamQueryState.Idle;
            if (!_task.IsCompleted) return SteamQueryState.Pending;

            Task<SteamLobby[]> finished = _task;
            _task = null;

            if (finished.IsFaulted)
            {
                error = finished.Exception?.GetBaseException().Message ?? "Steam query failed";
                return SteamQueryState.Failed;
            }

            if (finished.IsCanceled)
            {
                error = "Steam query cancelled";
                return SteamQueryState.Failed;
            }

            // Steam devuelve null cuando la consulta no encontró NADA. Eso es cero lobbies, no un
            // error: `ToRecords(null)` da lista vacía y el navegador dirá "No se encontraron
            // partidas.".
            records = SteamLobbyManager.ToRecords(finished.Result);
            return SteamQueryState.Completed;
        }

        public void Abandon()
        {
            // No se cancela la Task —Facepunch no lo permite— se SUELTA. La respuesta tardía cae
            // en el vacío, que es justo lo que hace que no pueda repintar una consulta posterior.
            _task = null;
            _startupError = null;
        }
    }

    /// <summary>
    /// El lobby propio, delegando en <see cref="SteamLobbyManager"/>. **Un solo lobby y un solo
    /// dueño**: el botón "Invite via Steam" y el anuncio automático del navegador pasan por el
    /// mismo `_hostedLobby`, así que no pueden crear dos.
    /// </summary>
    public sealed class FacepunchSteamLobbyHost : ISteamLobbyHost
    {
        public bool IsAvailable => SteamLobbyManager.IsAvailable;

        public bool HasLobby => SteamLobbyManager.HasHostedLobby;

        public bool EnsureLobby(string ip, int port) => SteamLobbyManager.TryEnsureHostedLobby(ip, port);

        public bool SetData(string key, string value) => SteamLobbyManager.TrySetHostedData(key, value);

        public void CloseLobby() => SteamLobbyManager.CloseHostedLobby();
    }

    /// <summary>
    /// Comprobación de que las dos listas de claves no han divergido. `SteamLobbyKeys` vive sin
    /// Steam dentro (para que el publicador se pueda probar) y `SteamLobbyManager` las repite del
    /// lado que sí habla con Steam; si una de las dos cambia sola, el host publicaría con una
    /// clave y el navegador leería con otra, y la lista saldría **vacía sin un solo error**.
    /// </summary>
    public static class SteamLobbyKeyParity
    {
        public static bool KeysMatch(out string mismatch)
        {
            mismatch = null;

            if (!Same(SteamLobbyKeys.Game, SteamLobbyManager.GameKey, "game", ref mismatch)) return false;
            if (!Same(SteamLobbyKeys.GameValue, SteamLobbyManager.GameValue, "game_value", ref mismatch)) return false;
            if (!Same(SteamLobbyKeys.ConnectIp, SteamLobbyManager.ConnectIpKey, "connect_ip", ref mismatch)) return false;
            if (!Same(SteamLobbyKeys.ConnectPort, SteamLobbyManager.ConnectPortKey, "connect_port", ref mismatch)) return false;
            if (!Same(SteamLobbyKeys.HostName, SteamLobbyManager.HostNameKey, "host_name", ref mismatch)) return false;
            if (!Same(SteamLobbyKeys.Name, SteamLobbyManager.LobbyNameKey, "name", ref mismatch)) return false;
            if (!Same(SteamLobbyKeys.WireVersion, SteamLobbyManager.WireVersionKey, "wire", ref mismatch)) return false;
            if (!Same(SteamLobbyKeys.Players, SteamLobbyManager.PlayersKey, "players", ref mismatch)) return false;
            if (!Same(SteamLobbyKeys.MaxPlayers, SteamLobbyManager.MaxPlayersKey, "max", ref mismatch)) return false;
            if (!Same(SteamLobbyKeys.Map, SteamLobbyManager.MapKey, "map", ref mismatch)) return false;
            if (!Same(SteamLobbyKeys.State, SteamLobbyManager.StateKey, "state", ref mismatch)) return false;
            if (!Same(SteamLobbyKeys.AnnouncedAt, SteamLobbyManager.AnnouncedAtKey, "announced_at", ref mismatch)) return false;

            return true;
        }

        private static bool Same(string a, string b, string label, ref string mismatch)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            mismatch = $"{label}: publisher='{a}' steam='{b}'";
            Debug.LogError("[ServerBrowser] Claves de lobby divergentes — " + mismatch);
            return false;
        }
    }
}
