namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// Lo que el host sabe de sí mismo en un instante. Se lo pasa la UI; aquí no se consulta
    /// nada, que es lo que hace comprobables las reglas de abajo.
    /// </summary>
    public readonly struct HostAnnouncementState
    {
        /// Somos el host de una sesión.
        public readonly bool IsHost;

        /// La sesión está establecida de verdad (Connected o InGame). Antes de eso no hay nada
        /// que anunciar: el endpoint todavía puede cambiar.
        public readonly bool IsEstablished;

        public readonly LobbyEndpoint Endpoint;
        public readonly string Name;
        public readonly string WireVersion;
        public readonly int Players;
        public readonly int MaxPlayers;
        public readonly string Map;
        public readonly LobbyStatus Status;

        /// <summary>
        /// ADR-117: hay relay configurado para esta build, así que la partida se puede anunciar
        /// aunque no haya endpoint directo — se entra por el relay.
        /// </summary>
        public readonly bool HasRelay;

        /// <summary>
        /// ADR-135: el túnel de Steam está abierto y publicado, así que se puede entrar por él
        /// aunque no haya endpoint directo.
        /// </summary>
        public readonly bool HasSteamHost;

        /// <summary>Sin relay. Es la forma que tenía este tipo antes de ADR-117.</summary>
        public HostAnnouncementState(bool isHost, bool isEstablished, LobbyEndpoint endpoint,
            string name, string wireVersion, int players, int maxPlayers, string map, LobbyStatus status)
            : this(isHost, isEstablished, endpoint, name, wireVersion, players, maxPlayers, map,
                status, false)
        {
        }

        public HostAnnouncementState(bool isHost, bool isEstablished, LobbyEndpoint endpoint,
            string name, string wireVersion, int players, int maxPlayers, string map,
            LobbyStatus status, bool hasRelay)
            : this(isHost, isEstablished, endpoint, name, wireVersion, players, maxPlayers, map,
                status, hasRelay, false)
        {
        }

        public HostAnnouncementState(bool isHost, bool isEstablished, LobbyEndpoint endpoint,
            string name, string wireVersion, int players, int maxPlayers, string map,
            LobbyStatus status, bool hasRelay, bool hasSteamHost)
        {
            IsHost = isHost;
            IsEstablished = isEstablished;
            Endpoint = endpoint;
            Name = name;
            WireVersion = wireVersion;
            Players = players;
            MaxPlayers = maxPlayers;
            Map = map;
            Status = status;
            HasRelay = hasRelay;
            HasSteamHost = hasSteamHost;
        }

        /// <summary>
        /// Se anuncia si hay POR DÓNDE ENTRAR, que desde ADR-117 son varias cosas y no una. Sin
        /// ninguna sigue sin anunciarse: publicar un lobby al que nadie puede entrar le cuesta al
        /// jugador el tiempo de descubrirlo y no le ahorra nada (ADR-112).
        /// </summary>
        public bool ShouldAnnounce =>
            IsHost && IsEstablished && (Endpoint.IsValid || HasRelay || HasSteamHost);
    }

    /// <summary>
    /// Quién decide cuándo se anuncia una partida y cuándo se retira. **Lo gobierna el ciclo de
    /// sesión, no un botón**: un anuncio atado a un botón sobrevive al día en que alguien sale por
    /// otro camino (el `Quit to Menu` del vendor, el backend que muere, una desconexión), y eso es
    /// exactamente un lobby fantasma.
    ///
    /// Sin Unity dentro: recibe el estado ya resuelto y un reloj. Las once reglas de robustez que
    /// importan —no publicar dos veces, retirarse en el teardown, no tocar después de retirar— se
    /// prueban aquí.
    /// </summary>
    public sealed class HostAnnouncementDriver
    {
        /// Cada cuánto se renueva el anuncio. Es el latido que mantiene frescos los contadores.
        public const double TouchIntervalSeconds = 10d;

        /// Cada cuánto se reintenta una publicación que no cuajó. La creación del lobby es
        /// asíncrona, así que el primer intento casi nunca la tiene lista.
        public const double RetryIntervalSeconds = 2d;

        private readonly ILobbyPublisher _publisher;
        private double _nextActionUnix;

        /// <summary>
        /// Hay una publicación EMPEZADA que todavía no ha cuajado: `Publish` devolvió false porque
        /// Steam sigue creando el lobby de forma asíncrona.
        ///
        /// Existe por un lobby fantasma real y difícil de ver: si la sesión termina en esa ventana
        /// —el host cancela, el backend muere—, `IsPublishing` es false (nunca llegó a serlo), así
        /// que la rama de retirada no se disparaba; y un par de segundos después la creación
        /// aterrizaba y dejaba en Steam un lobby público, joinable, apuntando a un endpoint muerto
        /// y **sin nadie que lo cierre** hasta que se cierre el proceso. La retirada tiene que
        /// cubrir "lo estaba intentando", no sólo "lo consiguió".
        /// </summary>
        private bool _publishPending;

        public HostAnnouncementDriver(ILobbyPublisher publisher)
        {
            _publisher = publisher;
        }

        public ILobbyPublisher Publisher => _publisher;

        /// Cuántas veces se ha retirado el anuncio. Observable para la suite.
        public int WithdrawCount { get; private set; }

        public void Update(HostAnnouncementState state, double nowUnix)
        {
            if (_publisher == null) return;

            if (!state.ShouldAnnounce)
            {
                // Cubre TODO lo que no es "host con sesión viva": teardown, vuelta al menú,
                // backend muerto, rol joiner, endpoint todavía sin resolver. Y cubre además la
                // publicación a medias (`_publishPending`): `Withdraw` es idempotente y cierra
                // igual el lobby que Steam acabe de crear tarde.
                if (_publisher.IsPublishing || _publishPending)
                {
                    _publisher.Withdraw();
                    WithdrawCount++;
                    _publishPending = false;
                }

                _nextActionUnix = 0d;
                return;
            }

            if (!_publisher.IsPublishing)
            {
                if (nowUnix < _nextActionUnix) return;

                var publication = new LobbyPublication(state.Name, state.WireVersion, state.MaxPlayers,
                    state.Map, "Unknown", LobbyPrivacy.Public, false, state.Endpoint,
                    Lobby.DefaultTtlSeconds, state.HasRelay, state.HasSteamHost);

                bool ok = _publisher.Publish(publication, state.Players, state.Status, nowUnix);
                _publishPending = !ok;
                _nextActionUnix = nowUnix + (ok ? TouchIntervalSeconds : RetryIntervalSeconds);
                return;
            }

            _publishPending = false;

            if (nowUnix < _nextActionUnix) return;

            _publisher.Touch(state.Players, state.Status, nowUnix);
            _nextActionUnix = nowUnix + TouchIntervalSeconds;
        }

        /// <summary>Retirada explícita, para cuando el proceso se va a cerrar.</summary>
        public void ForceWithdraw()
        {
            if (_publisher == null) return;
            if (!_publisher.IsPublishing && !_publishPending) return;

            _publisher.Withdraw();
            WithdrawCount++;
            _publishPending = false;
            _nextActionUnix = 0d;
        }
    }
}
