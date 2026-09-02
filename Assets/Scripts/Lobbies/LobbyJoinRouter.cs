namespace BackroomsSurvival.Lobbies
{
    public enum LobbyJoinRequestStatus
    {
        /// El intento arrancó. A partir de aquí manda la máquina de estados de sesión, no esto.
        Started = 0,

        /// No había nada seleccionado.
        NoSelection = 1,

        /// El lobby dice que no (lleno, versión, caducado…). `Reason` lleva cuál.
        RejectedByLobby = 2,

        /// El lobby admitiría entrar, pero el cliente todavía no sabe hacerlo (contraseñas).
        NotSupportedYet = 3,

        /// El camino de conexión rechazó la petición (no hay panel vivo, sesión ya en marcha…).
        SinkRefused = 4,

        /// El ciclo de sesión no permite arrancar ahora (`SessionState.Current.CanStart` en
        /// falso), o ya hay un intento en marcha pedido desde el propio navegador. Se distingue
        /// de <see cref="SinkRefused"/> porque aquí NO se ha llegado a tocar el camino de
        /// conexión: es un no del navegador, no del panel de Join.
        SessionBusy = 5,
    }

    public readonly struct LobbyJoinRequestResult
    {
        public readonly LobbyJoinRequestStatus Status;
        public readonly LobbyJoinability Reason;
        public readonly string Message;

        public LobbyJoinRequestResult(LobbyJoinRequestStatus status, LobbyJoinability reason, string message)
        {
            Status = status;
            Reason = reason;
            Message = message ?? "";
        }

        public bool Started => Status == LobbyJoinRequestStatus.Started;
    }

    /// <summary>
    /// El agujero por el que el navegador toca el camino de conexión, y el ÚNICO. Recibe un
    /// destino ya validado; no sabe de lobbies, ni de filtros, ni de TTL.
    ///
    /// Existe para que el modelo no dependa del panel de Join: la implementación real vive en
    /// <c>BackroomsSurvival.UI</c> y se inyecta desde arriba.
    /// </summary>
    public interface ILobbyJoinSink
    {
        /// <summary>
        /// Arranca el intento. `failure` explica el no cuando devuelve false.
        ///
        /// ADR-117: `relay` puede ser la vía ÚNICA —un lobby sin `connect_ip`— o el respaldo de
        /// `endpoint`. Quien implemente esto tiene que pasárselo al backend siempre que valga, no
        /// sólo cuando el endpoint falte: el orden de las vías lo decide la secuencia del backend,
        /// no el navegador.
        /// </summary>
        bool TryJoin(LobbyEndpoint endpoint, LobbyRelay relay, string playerName, out string failure);
    }

    /// <summary>
    /// Traduce "el jugador eligió esta fila" a "conéctate a este host y puerto", y es donde se
    /// decide QUÉ no se intenta siquiera.
    ///
    /// Contraseñas: el protocolo actual no lleva ningún campo por el que mandar una, así que un
    /// lobby con contraseña se rechaza aquí con <see cref="LobbyJoinRequestStatus.NotSupportedYet"/>
    /// en vez de intentar entrar y comerse un rechazo del handshake que la UI no sabría explicar.
    /// El punto de integración está documentado en `docs/SERVER_BROWSER.md`.
    /// </summary>
    public sealed class LobbyJoinRouter
    {
        private readonly ILobbyJoinSink _sink;
        private readonly string _clientVersion;

        public LobbyJoinRouter(ILobbyJoinSink sink, string clientVersion)
        {
            _sink = sink;
            _clientVersion = string.IsNullOrWhiteSpace(clientVersion) ? "" : clientVersion.Trim();
        }

        public LobbyJoinRequestResult Request(Lobby lobby, string playerName, double nowUnix)
        {
            if (lobby == null)
            {
                return new LobbyJoinRequestResult(LobbyJoinRequestStatus.NoSelection,
                    LobbyJoinability.Joinable, "Selecciona un servidor.");
            }

            LobbyJoinability verdict = lobby.EvaluateJoinability(_clientVersion, nowUnix);
            if (verdict == LobbyJoinability.PasswordRequired)
            {
                return new LobbyJoinRequestResult(LobbyJoinRequestStatus.NotSupportedYet, verdict,
                    "Este servidor pide contraseña y el cliente todavía no sabe enviarla.");
            }

            if (verdict != LobbyJoinability.Joinable)
            {
                return new LobbyJoinRequestResult(LobbyJoinRequestStatus.RejectedByLobby, verdict,
                    Explain(verdict));
            }

            if (_sink == null)
            {
                return new LobbyJoinRequestResult(LobbyJoinRequestStatus.SinkRefused, verdict,
                    "No hay camino de conexión conectado al navegador.");
            }

            if (!_sink.TryJoin(lobby.Endpoint, lobby.Relay, playerName, out string failure))
            {
                return new LobbyJoinRequestResult(LobbyJoinRequestStatus.SinkRefused, verdict,
                    string.IsNullOrEmpty(failure) ? "No se pudo iniciar la conexión." : failure);
            }

            // Con relay-only no hay endpoint que enseñar, y decir "Conectando a <invalid>…" sería
            // peor que no decir nada.
            return new LobbyJoinRequestResult(LobbyJoinRequestStatus.Started, verdict,
                lobby.IsRelayOnly
                    ? "Conectando por relay…"
                    : "Conectando a " + lobby.Endpoint + "…");
        }

        public static string Explain(LobbyJoinability verdict)
        {
            switch (verdict)
            {
                case LobbyJoinability.Joinable: return "";
                case LobbyJoinability.Expired: return "Ese servidor ya no se anuncia.";
                case LobbyJoinability.Closed: return "La partida está cerrada.";
                case LobbyJoinability.Private: return "La partida es privada.";
                case LobbyJoinability.InvalidEndpoint: return "El servidor anuncia una dirección inválida.";
                case LobbyJoinability.VersionMismatch: return "Versión incompatible.";
                case LobbyJoinability.Full: return "El servidor está lleno.";
                case LobbyJoinability.PasswordRequired: return "Requiere contraseña.";
                default: return "No se puede entrar.";
            }
        }
    }
}
