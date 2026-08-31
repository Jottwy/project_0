using System;
using BackroomsSurvival.Lobbies;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// El navegador de servidores. Este fichero es ciclo de vida y estado; el montaje de la
    /// pantalla está en <c>ServerBrowserUI.Build.cs</c>, igual que en JoinSessionUI.
    ///
    /// Cuatro reglas que sostienen todo lo demás:
    ///
    ///  1. **No sabe de red.** Habla con <see cref="ServerBrowserViewModel"/>, que habla con un
    ///     <see cref="ILobbyDirectory"/>. Hoy hay un mock detrás; mañana, HTTP. Este fichero no
    ///     cambia.
    ///  2. **No decide si hay sesión.** Lee <see cref="SessionState"/> y se lo PASA al view
    ///     model; no se hace una copia de su criterio ni arranca nada por su cuenta. El único
    ///     camino de conexión sigue siendo `JoinSessionUI` → `NetworkInitializer`.
    ///  3. **No bloquea.** No hay esperas ni corrutinas de espera: el directorio avanza en
    ///     <see cref="ServerBrowserViewModel.Tick"/> y el resultado llega dentro de un `Update`.
    ///  4. **Repinta sólo cuando cambia algo.** El view model avisa por evento y aquí se marca
    ///     sucio; las filas se reconstruyen una vez por frame como mucho.
    ///
    /// El reloj: <see cref="Time.unscaledTimeAsDouble"/>. No es tiempo Unix — es un reloj
    /// monótono del proceso, y basta porque el TTL sólo compara diferencias. Cuando entre el
    /// directorio real, su timestamp habrá que TRADUCIRLO a este reloj al parsear (ver
    /// `docs/SERVER_BROWSER.md`), no mezclar los dos.
    /// </summary>
    public sealed partial class ServerBrowserUI : MonoBehaviour
    {
        /// Refresco automático mientras el panel está abierto. En cero, sólo manual.
        public float AutoRefreshSeconds = 15f;

        private ServerBrowserViewModel _viewModel;
        private Func<string> _playerNameProvider;
        private string _clientVersion = "";

        private bool _rowsDirty;
        private double _nextAutoRefresh;
        private string _actionMessage = "";

        /// <summary>
        /// Hay un intento de entrada pedido DESDE aquí y todavía sin veredicto. Se resuelve
        /// mirando la FASE, no inventando un estado propio: es la máquina de sesión la que sabe
        /// si se entró, si falló o si el teardown ya terminó.
        /// </summary>
        private bool _awaitingJoinOutcome;

        public ServerBrowserViewModel ViewModel => _viewModel;
        public bool IsOpen => _panel != null && _panel.activeSelf;

        /// <summary>Qué hacer al pulsar "Volver". Lo inyecta el bootstrap.</summary>
        public Action OnBackRequested;

        /// <summary>
        /// Inyección de dependencias a mano. Llamar ANTES del primer <c>Start</c> (lo hace
        /// <see cref="ServerBrowserBootstrap"/>); si nadie llama, se monta con el mock por
        /// defecto para que la pantalla sea usable en el editor tal cual.
        /// </summary>
        public void Configure(ILobbyDirectory directory, ILobbyJoinSink joinSink,
            string clientVersion, Func<string> playerNameProvider)
        {
            _clientVersion = string.IsNullOrWhiteSpace(clientVersion) ? "" : clientVersion.Trim();
            _playerNameProvider = playerNameProvider;

            // Desuscribir SIEMPRE antes de sustituir: un segundo Configure sobre el mismo
            // componente dejaba dos manejadores vivos sobre el mismo evento y cada cambio
            // repintaba dos veces.
            if (_viewModel != null) _viewModel.Changed -= MarkRowsDirty;

            _viewModel = new ServerBrowserViewModel(
                directory ?? MockLobbyDirectory.CreateDefault(_clientVersion), _clientVersion, joinSink);
            _viewModel.Changed += MarkRowsDirty;
            _rowsDirty = true;
        }

        private void Awake()
        {
            if (_viewModel == null)
            {
                Configure(null, new JoinSessionLobbyJoinSink(),
                    ServerBrowserBootstrap.ClientVersion, null);
            }
        }

        private void Start()
        {
            BuildUI();
            UiEventSystem.Ensure();
            Open();
        }

        private void OnDestroy()
        {
            if (_viewModel == null) return;
            _viewModel.Changed -= MarkRowsDirty;
            _viewModel.CancelRefresh();
        }

        public void Open()
        {
            BuildUI();
            if (_panel != null) _panel.SetActive(true);
            _actionMessage = "";
            _nextAutoRefresh = AutoRefreshSeconds > 0f
                ? Time.unscaledTimeAsDouble + AutoRefreshSeconds
                : double.MaxValue;

            // El navegador es un panel de sesión más: mientras se ve, el cursor va libre. La
            // regla entera vive en SessionCursor y aquí no se escribe Cursor.* a mano.
            SessionCursor.Apply(menuVisible: true, phase: SessionState.Phase);
            _viewModel.Open(Time.unscaledTimeAsDouble);
        }

        /// <summary>
        /// Cerrar el panel. NO toca la sesión, ni el backend, ni el cursor si hay partida: si se
        /// cierra estando en el menú el cursor sigue libre porque la fase lo dice, y si se cierra
        /// porque se entró en el mundo, lo captura por la misma regla.
        /// </summary>
        public void Close()
        {
            _viewModel?.Close();
            if (_panel != null) _panel.SetActive(false);
            SessionCursor.Apply(menuVisible: JoinSessionUI.IsAnyMenuVisible, phase: SessionState.Phase);
        }

        /// <summary>El botón [Volver]. Cierra el navegador y devuelve el panel de multijugador.</summary>
        public void RequestBack()
        {
            Close();
            OnBackRequested?.Invoke();
        }

        private void Update()
        {
            if (_viewModel == null) return;

            double now = Time.unscaledTimeAsDouble;

            if (_awaitingJoinOutcome) ResolveJoinOutcome();
            if (!IsOpen) return;

            _viewModel.Tick(now);

            if (AutoRefreshSeconds > 0f && !_viewModel.IsRefreshing && !_viewModel.IsJoining &&
                now >= _nextAutoRefresh)
            {
                RequestRefresh();
            }

            if (!_rowsDirty) return;
            _rowsDirty = false;
            RebuildRows();
        }

        public void RequestRefresh()
        {
            if (_viewModel == null || _viewModel.IsJoining) return;

            double now = Time.unscaledTimeAsDouble;
            _nextAutoRefresh = AutoRefreshSeconds > 0f ? now + AutoRefreshSeconds : double.MaxValue;
            _actionMessage = "";
            _viewModel.Refresh(now);
        }

        /// <summary>
        /// El botón [Entrar]. Todo lo que decide vive en el view model y en
        /// <see cref="LobbyJoinRouter"/>; aquí sólo se le pasa el permiso del ciclo de sesión y
        /// se pinta el veredicto.
        /// </summary>
        public void RequestJoinSelected()
        {
            if (_viewModel == null) return;

            string playerName = _playerNameProvider != null ? _playerNameProvider() : "Player";
            LobbyJoinRequestResult result = _viewModel.RequestJoin(
                playerName, Time.unscaledTimeAsDouble, SessionState.Current.CanStart);

            _actionMessage = result.Message;
            if (result.Started)
            {
                Debug.Log("[ServerBrowser] " + result.Message);

                // Se esconde, no se destruye: el panel de conexión (que pinta Connecting… y, si
                // falla, Retry) tiene que quedar a la vista, y este objeto sigue mirando la fase
                // para devolverse a un estado usable pase lo que pase.
                _awaitingJoinOutcome = true;
                if (_panel != null) _panel.SetActive(false);
            }
            else
            {
                Debug.Log($"[ServerBrowser] Join rechazado: {result.Status} ({result.Reason}) — {result.Message}");
            }

            MarkRowsDirty();
        }

        /// <summary>
        /// El veredicto del intento, leído de la FASE. Entrar en el mundo lo cierra; cualquier
        /// final (Failed, Disconnected, o un teardown que ya volvió a Menu) devuelve el navegador
        /// a un estado reutilizable.
        ///
        /// El error NO se pinta aquí: de eso ya se encarga el panel de conexión, que es el único
        /// sitio con [Retry] y [Back to menu]. Duplicarlo sería el segundo camino de recuperación.
        /// </summary>
        private void ResolveJoinOutcome()
        {
            SessionPhase phase = SessionState.Phase;

            if (SessionStateMachine.IsInWorld(phase))
            {
                _awaitingJoinOutcome = false;
                _viewModel.NotifyJoinSucceeded();
                Close();
                return;
            }

            if (SessionStateMachine.IsLive(phase)) return; // Starting / Connecting: todavía nada

            _awaitingJoinOutcome = false;
            string reason = SessionState.Current.Reason;
            _viewModel.NotifyJoinFailed(reason);
            _actionMessage = string.IsNullOrEmpty(reason) ? "" : "Último intento: " + reason;
            Debug.Log($"[ServerBrowser] Intento terminado en fase {phase} ({reason}); panel reutilizable.");
            MarkRowsDirty();
        }

        private void MarkRowsDirty() => _rowsDirty = true;

        /// <summary>Texto de la línea de estado. Público porque es exactamente lo que se testea.</summary>
        public string ComposeStatusLine()
        {
            if (_viewModel == null) return "";

            switch (_viewModel.State)
            {
                case ServerBrowserState.Idle:
                    return string.IsNullOrEmpty(_actionMessage) ? "Pulsa Refrescar." : _actionMessage;
                case ServerBrowserState.Loading: return "Buscando partidas…";
                case ServerBrowserState.Joining: return _viewModel.StatusMessage;
                case ServerBrowserState.Error:
                    return _viewModel.StatusMessage +
                           (string.IsNullOrEmpty(_viewModel.ErrorDetail) ? "" : " (" + _viewModel.ErrorDetail + ")");
                case ServerBrowserState.Empty:
                    return string.IsNullOrEmpty(_actionMessage) ? _viewModel.StatusMessage : _actionMessage;
                default:
                    if (!string.IsNullOrEmpty(_actionMessage)) return _actionMessage;
                    int hidden = _viewModel.HiddenByFilterCount;
                    string shown = _viewModel.Visible.Count + " servidor" +
                                   (_viewModel.Visible.Count == 1 ? "" : "es");
                    return hidden > 0 ? shown + " (" + hidden + " ocultos por el filtro)" : shown;
            }
        }
    }
}
