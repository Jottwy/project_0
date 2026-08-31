using System;
using System.Globalization;

namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// La costura contra el lobby propio de Steam. Existe por lo mismo que
    /// <see cref="ISteamLobbyQuery"/>: para que las reglas de publicación —publicar una sola vez,
    /// no tocar después de retirar, retirarse en el teardown— se puedan probar sin Steam.
    ///
    /// La implementación real es <c>FacepunchSteamLobbyHost</c>, que delega en
    /// <c>SteamLobbyManager</c>. **Hay un solo lobby y un solo dueño**: el botón "Invite via
    /// Steam" y el anuncio automático usan el mismo, así que no pueden existir dos.
    /// </summary>
    public interface ISteamLobbyHost
    {
        bool IsAvailable { get; }

        /// Hay lobby propio abierto AHORA.
        bool HasLobby { get; }

        /// <summary>
        /// Asegura el lobby con ese destino. Devuelve true sólo cuando ya existe: la creación es
        /// asíncrona, así que el primer intento suele devolver false y el llamante reintenta.
        /// </summary>
        bool EnsureLobby(string ip, int port);

        bool SetData(string key, string value);

        /// <summary>Cierra el lobby propio. Idempotente y segura sin Steam.</summary>
        void CloseLobby();
    }

    /// <summary>
    /// Publica la partida del host en Steam. Es la otra mitad del navegador: sin esto, la lista
    /// está vacía por mucho descubrimiento que haya.
    ///
    /// Lo que NO hace: decidir cuándo. Eso lo lleva <see cref="HostAnnouncementDriver"/> a partir
    /// de la fase de sesión, porque un anuncio gobernado por un botón se queda vivo el día que
    /// alguien sale por otro camino.
    /// </summary>
    public sealed class SteamLobbyPublisher : ILobbyPublisher
    {
        private readonly ISteamLobbyHost _host;
        private LobbyPublication _publication;
        private bool _published;

        public SteamLobbyPublisher(ISteamLobbyHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public bool IsPublishing => _published && _host.HasLobby;

        public LobbyId PublishedId { get; private set; } = LobbyId.None;

        /// Cuántas veces se ha escrito la ficha entera. Observable para la suite: publicar dos
        /// veces seguidas no puede contar dos.
        public int PublishCount { get; private set; }

        public int TouchCount { get; private set; }

        public bool Publish(LobbyPublication publication, int players, LobbyStatus status, double nowUnix)
        {
            if (!publication.Endpoint.IsValid) return false;
            if (publication.MaxPlayers <= 0) return false;

            if (IsPublishing)
            {
                // Ya está anunciado: esto es una actualización, no un segundo lobby.
                _publication = publication;
                WriteAll(players, status, nowUnix);
                return true;
            }

            if (!_host.EnsureLobby(publication.Endpoint.Host, publication.Endpoint.Port))
            {
                // Steam todavía está creándolo (o no hay Steam). No se marca como publicado: el
                // conductor reintenta, y así una creación fallida no deja un anuncio fantasma en
                // el estado de este objeto.
                _publication = publication;
                return false;
            }

            _publication = publication;
            _published = true;
            PublishCount++;
            WriteAll(players, status, nowUnix);
            return true;
        }

        public void Touch(int players, LobbyStatus status, double nowUnix)
        {
            // Después de Withdraw esto no puede escribir nada: `_published` es falso y el host ya
            // no tiene lobby. Es la regla que impide resucitar un anuncio retirado.
            if (!IsPublishing) return;

            TouchCount++;
            WriteAll(players, status, nowUnix);
        }

        public void Withdraw()
        {
            if (!_published && !_host.HasLobby)
            {
                PublishedId = LobbyId.None;
                return;
            }

            _host.CloseLobby();
            _published = false;
            PublishedId = LobbyId.None;
        }

        private void WriteAll(int players, LobbyStatus status, double nowUnix)
        {
            int safePlayers = players < 0 ? 0 : players;
            if (safePlayers > _publication.MaxPlayers) safePlayers = _publication.MaxPlayers;

            _host.SetData(SteamLobbyKeys.Game, SteamLobbyKeys.GameValue);
            _host.SetData(SteamLobbyKeys.ConnectIp, _publication.Endpoint.Host);
            _host.SetData(SteamLobbyKeys.ConnectPort, Num(_publication.Endpoint.Port));
            _host.SetData(SteamLobbyKeys.Name, _publication.Name);
            _host.SetData(SteamLobbyKeys.WireVersion, _publication.Version);
            _host.SetData(SteamLobbyKeys.Players, Num(safePlayers));
            _host.SetData(SteamLobbyKeys.MaxPlayers, Num(_publication.MaxPlayers));
            _host.SetData(SteamLobbyKeys.Map, _publication.Map);
            _host.SetData(SteamLobbyKeys.State, SteamLobbyMapper.StateToText(status));
            _host.SetData(SteamLobbyKeys.AnnouncedAt,
                ((long)nowUnix).ToString(CultureInfo.InvariantCulture));
        }

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Las claves de metadata, en un sitio sin Steam dentro para que el publicador y sus tests no
    /// tengan que arrastrar Steamworks. <c>SteamLobbyManager</c> declara las mismas constantes
    /// para el lado que sí habla con Steam; si divergen, la lista sale vacía — de ahí el test que
    /// las compara.
    /// </summary>
    public static class SteamLobbyKeys
    {
        public const string Game = "bs_game";
        public const string GameValue = "backrooms_survival";
        public const string ConnectIp = "connect_ip";
        public const string ConnectPort = "connect_port";

        /// La dirección LAN del host, ADEMÁS de `connect_ip`. Cuando el host consigue un mapeo
        /// UPnP confirmado, `connect_ip` pasa a ser su IP pública — y un joiner de la MISMA red que
        /// llame a esa IP pública sólo llega si el router hace hairpin/NAT loopback, cosa que
        /// muchos routers domésticos no hacen. Esta clave es el respaldo para ese caso.
        public const string LanIp = "bs_lan_ip";
        public const string HostName = "host_name";
        public const string Name = "bs_name";
        public const string WireVersion = "bs_wire";
        public const string Players = "bs_players";
        public const string MaxPlayers = "bs_max";
        public const string Map = "bs_map";
        public const string State = "bs_state";
        public const string AnnouncedAt = "bs_at";
    }
}
