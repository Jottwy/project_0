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
            // ADR-117: la regla pasa de «endpoint válido» a «alguna vía de entrada». Un lobby
            // relay-only no tiene endpoint y se entra igual; uno sin NINGUNA vía sigue sin
            // publicarse, que es la defensa de ADR-112 intacta — publicar algo a lo que nadie
            // puede entrar le cuesta al jugador el tiempo de descubrirlo y no le ahorra nada.
            // ADR-135 suma la tercera vía a la misma regla.
            if (!publication.Endpoint.IsValid && !publication.HasRelay && !publication.HasSteamHost) return false;
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
            // ADR-117 D7: sin endpoint directo la clave va VACÍA, no con un relleno. Steam
            // devuelve cadena vacía para una clave ausente, así que las dos formas se leen igual
            // en el navegador; escribirla vacía deja además el rastro de que el host la consideró.
            _host.SetData(SteamLobbyKeys.ConnectIp, _publication.Endpoint.Host ?? "");
            _host.SetData(SteamLobbyKeys.ConnectPort,
                _publication.Endpoint.IsValid ? Num(_publication.Endpoint.Port) : "");
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

        /// <summary>
        /// El relay de ADR-117, en tres claves. Se publican SÓLO cuando el backend del host
        /// confirma que el relay le ha admitido (`relay_ready`); antes no existen, y un lobby sin
        /// ellas es exactamente lo que era antes de ADR-117.
        ///
        /// Van aparte de `connect_ip` a propósito: un endpoint directo y una sesión de relay no
        /// son la misma cosa ni se sustituyen. Un lobby puede tener las dos —lo normal cuando el
        /// host tiene UPnP y además relay—, sólo una, o ninguna.
        /// </summary>
        public const string RelayAddr = "bs_relay_addr";

        public const string RelaySession = "bs_relay_session";

        /// <summary>
        /// El secreto de sesión, en 32 hexadecimales (ADR-117 D9).
        ///
        /// **Está en la metadata PÚBLICA del lobby, y eso es deliberado en R1.** No defiende de
        /// quien ve el lobby —que es justo quien tiene derecho a entrar— sino de que el relay sea
        /// un relay abierto: sin token no se crea ni se entra en ninguna sesión, así que nadie que
        /// no haya visto la lista puede usarlo de reflector. Es el mismo nivel de confianza que la
        /// partida ya tenía. La autenticación por tickets de Steam es R2 y tendrá su ADR.
        /// </summary>
        public const string RelayToken = "bs_relay_token";

        /// <summary>
        /// ADR-135: el `SteamId` del host, en decimal. **Clave explícita y no `Lobby.Owner`**: la
        /// propiedad de un lobby de Steam migra cuando el dueño se va, y el túnel tiene que apuntar
        /// al proceso que sirve el mundo, no al miembro más antiguo.
        /// </summary>
        public const string SteamHost = "bs_steam_host";

        /// <summary>
        /// El secreto de sesión con el que ese host autoriza el túnel (ADR-135 D4', enmienda 1).
        ///
        /// Está en la metadata PÚBLICA del lobby, igual que <see cref="RelayToken"/> y por el mismo
        /// motivo: no defiende de quien ve el lobby —que es quien tiene derecho a entrar— sino de
        /// que el túnel sea un túnel abierto. Es lo que permite que el navegador entre **sin
        /// hacerse miembro del lobby**.
        /// </summary>
        public const string SteamAuth = "bs_steam_auth";

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
