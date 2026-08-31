using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Lobbies
{
    public enum SteamQueryState
    {
        /// No hay consulta pedida.
        Idle = 0,

        /// Pedida y sin respuesta todavía.
        Pending = 1,

        /// Hay resultados (pueden ser cero, que es un resultado legítimo).
        Completed = 2,

        /// Steam contestó que no, o reventó.
        Failed = 3,
    }

    /// <summary>
    /// La costura contra Steam. Existe para que <see cref="SteamLobbyDirectory"/> —donde vive
    /// todo lo que se puede equivocar: caducidad de respuestas, timeout, cero resultados, error—
    /// se pueda probar sin cliente de Steam.
    ///
    /// La implementación real es <c>FacepunchSteamLobbyQuery</c>, que envuelve
    /// <c>SteamMatchmaking.LobbyList…RequestAsync()</c>.
    /// </summary>
    public interface ISteamLobbyQuery
    {
        /// Steam vivo y utilizable. False = cliente cerrado, DLL ausente, Init fallido.
        bool IsAvailable { get; }

        /// <summary>Lanza UNA consulta. Si había otra en vuelo, queda abandonada.</summary>
        void Begin(int maxResults);

        /// <summary>Estado de la consulta en vuelo. No bloquea nunca.</summary>
        SteamQueryState Poll(out IReadOnlyList<SteamLobbyRecord> records, out string error);

        /// <summary>Deja de esperar la consulta en vuelo. Idempotente.</summary>
        void Abandon();
    }

    /// <summary>
    /// Descubrimiento REAL por Steam. Es el sustituto de <see cref="MockLobbyDirectory"/> en
    /// producción, y lo único que cambia respecto a él es de dónde salen las fichas: el navegador,
    /// el filtro, el orden, el TTL y el camino de entrada son exactamente los mismos.
    ///
    /// El resultado se entrega SIEMPRE dentro de <see cref="Tick"/>, nunca desde una continuación
    /// asíncrona: es lo que garantiza que el callback aterrice en el hilo de Unity y lo que hace
    /// que una respuesta lenta no pueda repintar una lista más nueva.
    /// </summary>
    public sealed class SteamLobbyDirectory : ILobbyDirectory
    {
        /// Steam no promete contestar. Sin tope, una consulta perdida deja el panel en
        /// "Buscando partidas…" para siempre.
        public const double DefaultTimeoutSeconds = 12d;

        public const string SteamUnavailableMessage =
            "Steam no está disponible. Puedes entrar por IP desde el menú de multijugador.";

        public const string TimeoutMessage = "Steam no respondió a tiempo.";

        private readonly ISteamLobbyQuery _query;
        private readonly int _maxResults;

        private Action<LobbyDirectoryResult> _pending;
        private double _deadlineUnix;
        private bool _failImmediately;
        private string _immediateFailure;

        public SteamLobbyDirectory(ISteamLobbyQuery query, int maxResults = 50,
            double timeoutSeconds = DefaultTimeoutSeconds)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _maxResults = maxResults <= 0 ? 50 : maxResults;
            TimeoutSeconds = timeoutSeconds <= 0d ? DefaultTimeoutSeconds : timeoutSeconds;
        }

        public double TimeoutSeconds { get; }

        public string Description => "steam";

        public bool IsRefreshing => _pending != null;

        public void Refresh(double nowUnix, Action<LobbyDirectoryResult> onCompleted)
        {
            // La anterior se cancela ANTES de pedir la nueva: si la lenta contestara después,
            // repintaría la tabla con datos más viejos.
            CancelRefresh();

            _pending = onCompleted;
            _deadlineUnix = nowUnix + TimeoutSeconds;
            _failImmediately = false;
            _immediateFailure = null;

            if (!_query.IsAvailable)
            {
                // No se contesta aquí: el contrato dice que el resultado llega dentro de Tick, y
                // romperlo sólo para el camino de error haría que el estado "cargando" durara
                // cero frames y la UI no lo pudiera pintar nunca.
                _failImmediately = true;
                _immediateFailure = SteamUnavailableMessage;
                return;
            }

            _query.Begin(_maxResults);
        }

        public void CancelRefresh()
        {
            Action<LobbyDirectoryResult> pending = _pending;
            _pending = null;
            _failImmediately = false;
            _immediateFailure = null;
            if (pending == null) return;

            _query.Abandon();
            pending(LobbyDirectoryResult.Cancelled());
        }

        public void Tick(double nowUnix)
        {
            if (_pending == null) return;

            if (_failImmediately)
            {
                Complete(LobbyDirectoryResult.Failed(_immediateFailure));
                return;
            }

            if (nowUnix > _deadlineUnix)
            {
                _query.Abandon();
                Complete(LobbyDirectoryResult.Failed(TimeoutMessage));
                return;
            }

            SteamQueryState state = _query.Poll(out IReadOnlyList<SteamLobbyRecord> records, out string error);
            switch (state)
            {
                case SteamQueryState.Pending:
                    return;

                case SteamQueryState.Failed:
                    Complete(LobbyDirectoryResult.Failed(string.IsNullOrEmpty(error) ? "Steam falló." : error));
                    return;

                case SteamQueryState.Completed:
                    // Cero resultados es un resultado. "No hay partidas" y "no se pudo preguntar"
                    // son cosas distintas y la UI las pinta distinto.
                    Complete(LobbyDirectoryResult.Ok(Map(records, nowUnix)));
                    return;

                default:
                    // Idle con petición viva: la consulta se perdió por debajo (Steam se cayó
                    // entre Begin y Poll). No se puede esperar a un resultado que nadie va a dar.
                    Complete(LobbyDirectoryResult.Failed(SteamUnavailableMessage));
                    return;
            }
        }

        /// <summary>
        /// Conversión de fichas planas a lobbies. Pública y estática porque es exactamente lo que
        /// prueban los tests de mapeo, y no necesita ni instancia ni Steam.
        /// </summary>
        public static LobbyList Map(IReadOnlyList<SteamLobbyRecord> records, double nowUnix)
        {
            var lobbies = new List<Lobby>(records == null ? 0 : records.Count);
            if (records == null) return LobbyList.Create(lobbies, nowUnix);

            for (int i = 0; i < records.Count; i++)
            {
                if (SteamLobbyMapper.TryMap(records[i], nowUnix, out Lobby lobby)) lobbies.Add(lobby);
            }

            return LobbyList.Create(lobbies, nowUnix);
        }

        private void Complete(LobbyDirectoryResult result)
        {
            Action<LobbyDirectoryResult> pending = _pending;
            _pending = null;
            _failImmediately = false;
            _immediateFailure = null;
            pending?.Invoke(result);
        }
    }
}
