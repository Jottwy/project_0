using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Lobbies
{
    public enum ServerBrowserState
    {
        /// Aún no se ha pedido nada (o el panel está cerrado).
        Idle = 0,

        /// Consulta en vuelo. Es el estado que la UI TIENE que poder pintar sin bloquear.
        Loading = 1,

        /// Hay lista y hay filas visibles.
        Ready = 2,

        /// La consulta fue bien y no hay nada que enseñar (o el filtro lo escondió todo).
        Empty = 3,

        /// La consulta falló. `StatusMessage` lleva el aviso, `ErrorDetail` el motivo técnico.
        Error = 4,

        /// Se pidió entrar y el intento está en marcha. A partir de aquí manda
        /// `SessionStateMachine`; el navegador sólo espera el veredicto.
        Joining = 5,
    }

    /// <summary>
    /// El navegador de servidores SIN Unity: estado, lista visible, selección, filtro, orden y la
    /// PETICIÓN de entrada. Todo lo que se puede equivocar vive aquí, y aquí es donde lo prueba la
    /// suite; el MonoBehaviour de encima sólo pinta lo que este objeto ya decidió.
    ///
    /// No conoce el directorio real: habla con <see cref="ILobbyDirectory"/>. No conoce la red ni
    /// la máquina de estados de sesión: lo máximo que entrega hacia fuera es un
    /// <see cref="LobbyEndpoint"/> a través de <see cref="LobbyJoinRouter"/>, y el permiso para
    /// arrancar (`CanStart`) se lo PASA quien llama. Meter `SessionState` aquí dentro sería el
    /// segundo sitio que decide si hay sesión viva, que es exactamente el fallo que
    /// `SessionStateMachine` vino a cerrar.
    /// </summary>
    public sealed class ServerBrowserViewModel
    {
        /// Cada cuánto se vuelve a podar por TTL. Un servidor caducado no se puede quedar en la
        /// tabla hasta el próximo refresco manual, pero repodar en cada frame es basura gratis.
        public const double PruneIntervalSeconds = 1d;

        public const string NoServersMessage = "No se encontraron partidas.";
        public const string FilteredOutMessage = "Ningún servidor pasa el filtro.";
        public const string DiscoveryFailedMessage = "No se pudieron obtener las partidas.";
        public const string SessionBusyMessage = "Ya hay una sesión en marcha.";
        public const string JoinInFlightMessage = "Ya hay un intento de conexión en marcha.";

        private readonly ILobbyDirectory _directory;
        private readonly LobbyJoinRouter _joinRouter;
        private readonly List<Lobby> _visible = new List<Lobby>();

        private LobbyList _raw = LobbyList.Empty;
        private LobbyId _selectedId = LobbyId.None;
        private double _nextPruneUnix;
        private int _generation;

        public readonly LobbyFilter Filter = new LobbyFilter();
        public readonly LobbySort Sort = new LobbySort(LobbySortKey.Ping);

        /// <summary>Se dispara cuando cambia algo que la UI pinta.</summary>
        public event Action Changed;

        /// <summary>
        /// Cuántos manejadores hay colgados de <see cref="Changed"/>. Existe para que la suite
        /// pueda comprobar que reconfigurar el panel NO deja el view model anterior escuchando —
        /// un repintado doble es invisible a ojo y se acumula en cada reapertura.
        /// </summary>
        public int ChangedListenerCount => Changed == null ? 0 : Changed.GetInvocationList().Length;

        public ServerBrowserViewModel(ILobbyDirectory directory, string clientVersion,
            ILobbyJoinSink joinSink = null)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            ClientVersion = string.IsNullOrWhiteSpace(clientVersion) ? "" : clientVersion.Trim();
            Filter.ClientVersion = ClientVersion;
            _joinRouter = new LobbyJoinRouter(joinSink, ClientVersion);
            StatusMessage = "";
            ErrorDetail = "";
        }

        public string ClientVersion { get; }
        public ServerBrowserState State { get; private set; } = ServerBrowserState.Idle;

        /// Lo que se le enseña al humano. Los tres textos fijos están arriba como constantes.
        public string StatusMessage { get; private set; }

        /// El motivo técnico del último fallo de discovery. Se guarda aparte porque el aviso al
        /// jugador es siempre el mismo y el detalle sólo sirve para el log.
        public string ErrorDetail { get; private set; }

        public ILobbyDirectory Directory => _directory;

        /// Filas que la UI pinta, ya filtradas y ordenadas.
        public IReadOnlyList<Lobby> Visible => _visible;

        /// Todo lo anunciado y todavía vigente, antes del filtro. La diferencia con
        /// <see cref="Visible"/> es lo que el filtro está escondiendo, y eso se enseña.
        public int TotalCount => _raw.Count;

        public int HiddenByFilterCount => _raw.Count - _visible.Count;

        public LobbyId SelectedId => _selectedId;

        public Lobby Selected
        {
            get
            {
                if (!_selectedId.IsValid) return null;
                return _raw.TryGet(_selectedId, out Lobby lobby) ? lobby : null;
            }
        }

        public bool IsRefreshing => _directory.IsRefreshing;

        /// <summary>Hay un intento de entrada en marcha pedido DESDE este navegador.</summary>
        public bool IsJoining => State == ServerBrowserState.Joining;

        /// <summary>
        /// Abrir el panel. Cancela lo que hubiera en vuelo (una respuesta de la vez anterior
        /// repintaría una lista que ya nadie miraba), limpia el veredicto viejo y pide lista.
        /// </summary>
        public void Open(double nowUnix)
        {
            CancelRefresh();
            ErrorDetail = "";
            State = ServerBrowserState.Idle;
            StatusMessage = "";
            Refresh(nowUnix);
        }

        /// <summary>
        /// Cerrar el panel. NO toca la sesión: cerrar un navegador no es abandonar una partida.
        /// Idempotente y sin efectos fuera de este objeto.
        /// </summary>
        public void Close()
        {
            CancelRefresh();
            if (State == ServerBrowserState.Joining) return; // el intento sigue vivo fuera
            State = ServerBrowserState.Idle;
            StatusMessage = "";
            Raise();
        }

        /// <summary>Pide la lista. Un segundo refresco mientras hay uno en vuelo lo sustituye.</summary>
        public void Refresh(double nowUnix)
        {
            int generation = ++_generation;
            State = ServerBrowserState.Loading;
            StatusMessage = "Buscando partidas…";
            Raise();

            _directory.Refresh(nowUnix, result => OnResult(generation, nowUnix, result));
        }

        /// <summary>
        /// Avanza el directorio y repoda por TTL. Se llama desde el `Update` del panel; nunca
        /// bloquea y nunca espera.
        /// </summary>
        public void Tick(double nowUnix)
        {
            _directory.Tick(nowUnix);

            if (nowUnix < _nextPruneUnix) return;
            _nextPruneUnix = nowUnix + PruneIntervalSeconds;

            if (State == ServerBrowserState.Idle || State == ServerBrowserState.Loading) return;

            LobbyList pruned = _raw.WithoutExpired(nowUnix);
            if (ReferenceEquals(pruned, _raw)) return;

            _raw = pruned;
            Rebuild();
            if (State == ServerBrowserState.Ready || State == ServerBrowserState.Empty) UpdateReadyState();

            Raise();
        }

        public void CancelRefresh()
        {
            // Sube la generación ANTES de cancelar: si la cancelación llega con la vieja, el
            // manejador la descarta sin repintar.
            _generation++;
            _directory.CancelRefresh();
            if (State == ServerBrowserState.Loading)
            {
                State = _raw.IsEmpty ? ServerBrowserState.Idle : ServerBrowserState.Ready;
                StatusMessage = "";
                Raise();
            }
        }

        /// <summary>
        /// Vuelve a derivar la lista visible. La llama la UI cuando el humano toca un filtro o
        /// una cabecera de orden — no hace red.
        /// </summary>
        public void ApplyFilterAndSort()
        {
            Rebuild();
            if (State == ServerBrowserState.Ready || State == ServerBrowserState.Empty) UpdateReadyState();
            Raise();
        }

        /// <summary>
        /// Selecciona por id. Devuelve false si ese lobby no está VISIBLE: seleccionar algo que
        /// la tabla no enseña es como se acaba pulsando Join sobre una fila que el jugador cree
        /// que filtró.
        /// </summary>
        public bool Select(LobbyId id)
        {
            for (int i = 0; i < _visible.Count; i++)
            {
                if (_visible[i].Id != id) continue;

                if (_selectedId != id)
                {
                    _selectedId = id;
                    Raise();
                }

                return true;
            }

            return false;
        }

        public void ClearSelection()
        {
            if (!_selectedId.IsValid) return;
            _selectedId = LobbyId.None;
            Raise();
        }

        public LobbyJoinability EvaluateSelected(double nowUnix, bool passwordSupplied = false)
        {
            Lobby lobby = Selected;
            if (lobby == null) return LobbyJoinability.Expired;
            return lobby.EvaluateJoinability(ClientVersion, nowUnix, passwordSupplied);
        }

        /// <summary>
        /// LA FRONTERA. Es lo único que el navegador entrega al camino de conexión: un host y un
        /// puerto, y sólo si el lobby admite entrar. Todo lo demás (nombre, mapa, ping) se queda
        /// en la UI.
        /// </summary>
        public bool TryGetSelectedEndpoint(double nowUnix, out LobbyEndpoint endpoint,
            out LobbyJoinability reason, bool passwordSupplied = false)
        {
            endpoint = LobbyEndpoint.None;
            reason = EvaluateSelected(nowUnix, passwordSupplied);
            if (reason != LobbyJoinability.Joinable) return false;

            endpoint = Selected.Endpoint;
            return true;
        }

        /// <summary>
        /// Pide entrar en el lobby seleccionado.
        ///
        /// `lifecycleAllowsStart` es `SessionState.Current.CanStart`, y lo pasa la UI: el
        /// navegador NO consulta la máquina de estados por su cuenta ni se hace una copia de su
        /// criterio. Con eso, el gate se puede probar entero sin Unity.
        ///
        /// El orden de los dos rechazos propios importa: primero "ya estoy intentando entrar"
        /// (doble clic en el mismo botón) y luego "hay una sesión viva", porque el primero es
        /// culpa del navegador y el segundo es un hecho del proceso.
        /// </summary>
        public LobbyJoinRequestResult RequestJoin(string playerName, double nowUnix, bool lifecycleAllowsStart)
        {
            if (State == ServerBrowserState.Joining)
            {
                return Reject(new LobbyJoinRequestResult(LobbyJoinRequestStatus.SessionBusy,
                    LobbyJoinability.Joinable, JoinInFlightMessage));
            }

            if (!lifecycleAllowsStart)
            {
                return Reject(new LobbyJoinRequestResult(LobbyJoinRequestStatus.SessionBusy,
                    LobbyJoinability.Joinable, SessionBusyMessage));
            }

            Lobby lobby = Selected;
            LobbyJoinRequestResult result = _joinRouter.Request(lobby, playerName, nowUnix);

            if (!result.Started)
            {
                // Una ficha caducada deja de ser seleccionable en el acto: si no, el jugador se
                // queda pulsando Entrar sobre una fila que ya no describe nada.
                if (result.Reason == LobbyJoinability.Expired && lobby != null)
                {
                    _raw = _raw.WithoutExpired(nowUnix);
                    Rebuild();
                    if (State == ServerBrowserState.Ready || State == ServerBrowserState.Empty) UpdateReadyState();
                }

                return Reject(result);
            }

            State = ServerBrowserState.Joining;
            StatusMessage = result.Message;
            Raise();
            return result;
        }

        /// <summary>
        /// El intento que salió de aquí ha fallado (timeout, backend muerto, rechazo). Devuelve
        /// el panel a un estado USABLE: nada de quedarse en "Conectando…" para siempre, que es
        /// como se consigue un botón Entrar muerto sin nadie que lo rearme.
        /// </summary>
        public void NotifyJoinFailed(string reason)
        {
            if (State != ServerBrowserState.Joining) return;
            UpdateReadyState();
            StatusMessage = string.IsNullOrEmpty(reason) ? "No se pudo conectar." : reason;
            Raise();
        }

        /// <summary>El intento entró. El panel deja de estar en Joining y se cierra desde la UI.</summary>
        public void NotifyJoinSucceeded()
        {
            if (State != ServerBrowserState.Joining) return;
            UpdateReadyState();
            Raise();
        }

        private LobbyJoinRequestResult Reject(LobbyJoinRequestResult result)
        {
            StatusMessage = result.Message;
            Raise();
            return result;
        }

        private void OnResult(int generation, double nowUnix, LobbyDirectoryResult result)
        {
            // Respuesta de una consulta que ya no manda (la sustituyó otro refresh, o se canceló).
            if (generation != _generation) return;
            if (result.Status == LobbyDirectoryStatus.Cancelled) return;

            if (result.Status == LobbyDirectoryStatus.Failed)
            {
                State = ServerBrowserState.Error;
                StatusMessage = DiscoveryFailedMessage;
                ErrorDetail = result.ErrorMessage ?? "";

                // La lista anterior NO se borra: una lista vieja con un aviso encima es más útil
                // que una tabla vacía, y el TTL ya se encarga de que no envejezca sin avisar.
                Rebuild();
                Raise();
                return;
            }

            ErrorDetail = "";
            _raw = result.Lobbies.WithoutExpired(nowUnix);
            _nextPruneUnix = nowUnix + PruneIntervalSeconds;
            Rebuild();
            UpdateReadyState();
            Raise();
        }

        /// Ready y Empty son el MISMO estado con distinto número de filas; separarlos en dos
        /// sitios es como se consigue una tabla vacía que sigue diciendo "listo".
        private void UpdateReadyState()
        {
            State = _visible.Count > 0 ? ServerBrowserState.Ready : ServerBrowserState.Empty;
            StatusMessage = State == ServerBrowserState.Empty
                ? (_raw.Count > 0 ? FilteredOutMessage : NoServersMessage)
                : "";
        }

        private void Rebuild()
        {
            _visible.Clear();
            List<Lobby> filtered = Filter.Apply(_raw);
            Sort.Apply(filtered);
            _visible.AddRange(filtered);

            if (!_selectedId.IsValid) return;

            // La selección sólo sobrevive mientras su fila sigue en la tabla. Un lobby que
            // caducó, que dejó de anunciarse o que el filtro escondió deja de estar seleccionado
            // — si no, el botón de entrar apuntaría a algo que el jugador ya no ve.
            for (int i = 0; i < _visible.Count; i++)
            {
                if (_visible[i].Id == _selectedId) return;
            }

            _selectedId = LobbyId.None;
        }

        private void Raise() => Changed?.Invoke();
    }
}
