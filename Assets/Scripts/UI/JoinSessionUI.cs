using System;
using System.Collections;
using BackroomsSurvival.Net;
using PolymindGames;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    public sealed partial class JoinSessionUI : MonoBehaviour
    {
        public enum PanelState { Idle, ManualEditing, StartingHost, Joining, Connected, Disconnected }

        public bool enableAutoSolo = false;
        public float autoSoloDelaySeconds = 0.25f;

        [SerializeField]
        [Tooltip("Scene to load (via STP LevelManager) once the IPC connection succeeds. " +
                 "Empty = stay in the current scene (pure overlay mode). Set by NetworkMenuBootstrap " +
                 "for the menu->connect->gameplay flow.")]
        private string _gameplayScene = "";
        private bool _loadingGameplay;

        private Canvas _canvas;
        private GameObject _panel;
        private CanvasGroup _panelCanvasGroup;
        private InputField _ipField;
        private InputField _portField;
        private InputField _nameField;
        private Button _hostButton;
        private Button _joinButton;
        private Button _steamInviteButton;
        private Button _disconnectButton;
        private Button _retryButton;
        private Button _backButton;
        private Text _titleText;
        private Text _statusText;

        private PanelState _state = PanelState.Idle;
        public PanelState State => _state;

        public string ServerIP => _ipField != null ? _ipField.text : "127.0.0.1";
        public string Port => _portField != null ? _portField.text : "7778";
        public string PlayerName => _nameField != null ? _nameField.text : "Player";
        public bool IsVisible => _panel != null && _panel.activeInHierarchy &&
                                 _panelCanvasGroup != null && _panelCanvasGroup.alpha > 0.01f;
        public static bool IsAnyMenuVisible => _instance != null && _instance.IsVisible;

        /// <summary>
        /// La IP que el host tiene escrita, o null si el panel no existe. Es lo que publica el
        /// anuncio de Steam (`ServerBrowserBootstrap.ResolveHostEndpoint`), que corre **cada
        /// frame de partida**: resolverlo con `FindFirstObjectByType&lt;JoinSessionUI&gt;` recorría
        /// la escena entera por frame para llegar a este mismo objeto, que es
        /// `DontDestroyOnLoad` y ya se conoce a sí mismo.
        /// </summary>
        public static string CurrentServerIP => _instance != null ? _instance.ServerIP : null;

        private const float InputWidth = 300f;
        private const float InputHeight = 40f;
        private const float ButtonWidth = 120f;
        private const float ButtonHeight = 40f;
        private const float Spacing = 10f;
        private const float PanelPadding = 24f;

        private static JoinSessionUI _instance;
        private bool _autoHostRequested;
        private bool _built;
        private Coroutine _autoHostCoroutine;

        /// Ultima fase PINTADA. Sirve para reaccionar solo a los cambios; ver Update.
        private SessionPhase _renderedPhase = SessionPhase.Menu;

        /// Texto fijo del intento en curso ("Connecting to 192.168.1.40:7778"), al que
        /// RefreshProgressText le pega el contador. Se guarda porque el contador llega desde
        /// NetworkInitializer sin saber a donde se estaba llamando.
        private string _attemptLabel = "";

        /// <summary>
        /// El ULTIMO intento, tal cual se pidio. Es lo que hace que "Reintentar" sea un boton y
        /// no "vuelve a escribir la IP": un timeout ya deja los campos como estaban, pero el
        /// auto-join de Steam los rellena solo y un reintento manual tendria que adivinarlos.
        /// </summary>
        private struct Attempt
        {
            public bool IsJoin;
            public string Ip;
            public int Port;
            public string PlayerName;
            public bool Valid;
        }
        private Attempt _lastAttempt;

        // ─── El gate de "conectado" ─────────────────────────────────────────────────────────
        //
        // `IPCClient.IsConnected` significa "Unity habló con SU PROPIO backend por TCP en
        // 127.0.0.1". Para un Host eso ES la sesión: su backend es el servidor. Para un Joiner no
        // prueba absolutamente nada de la sesión — el backend local acepta ese TCP y sirve un
        // mundo aunque el handshake UDP contra el host no haya salido nunca de la máquina.
        //
        // Ese era el fallo: con una IP inalcanzable el panel pasaba a "Connected", cargaba la
        // escena de juego y metía al jugador en un mundo local en solitario, sin geometría WG3 y
        // sin un solo error, mientras el backend reenviaba el handshake muerto en silencio.
        //
        // Un Joiner ya no entra hasta que su backend confirma `session_joined`, que solo se emite
        // al registrar al host tras el HandshakeAck. Si nadie contesta, el backend agota
        // `CONNECT_TIMEOUT` y manda `session_ended` con el motivo, que es lo que se ve abajo.
        ///
        /// El gate en si YA NO VIVE AQUI: lo lleva <see cref="SessionState"/>, porque tenerlo
        /// duplicado en el panel es como se consigue que una copia se olvide de reiniciarse. Lo
        /// que queda son lecturas.
        private string _lastSessionEndReason = "";
        private IPCClient _eventSourceIpc;

        private const string SessionJoinedEvent = "session_joined";
        private const string SessionEndedEvent = "session_ended";

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[JoinSessionUI] Existing JoinSessionUI detected; destroying duplicate component");
                Destroy(this);
                return;
            }

            _instance = this;
            int activeCount = FindObjectsByType<JoinSessionUI>(FindObjectsSortMode.None).Length;
            Debug.Log($"[JoinSessionUI] JoinSessionUI created (active={activeCount})");
        }

        private void Start()
        {
            BuildUI();
            UiEventSystem.Ensure();
            // Se siembra con la fase ACTUAL, no con un valor imposible: RenderPhase solo tiene que
            // correr en los CAMBIOS. Sembrarlo a -1 haria que el primer Update repintara la fase
            // Menu encima del panel que este mismo metodo acaba de montar, y lo cerraria.
            _renderedPhase = SessionState.Phase;

            if (SessionStateMachine.IsInWorld(_renderedPhase))
            {
                HandleConnected();
                return;
            }

            string sessionMode = Environment.GetEnvironmentVariable("SESSION_MODE");
            bool autoSoloEnabled = IsAutoSoloEnabled();
            string startupMode = DetermineStartupMode(sessionMode, autoSoloEnabled);
            Debug.Log($"[JoinSessionUI] Session startup mode: {startupMode}");
            Debug.Log($"[JoinSessionUI] AutoSolo enabled={autoSoloEnabled}");

            if (IsSessionMode(sessionMode, "host"))
            {
                string playerName = Environment.GetEnvironmentVariable("NET_NAME");
                string hostName = string.IsNullOrWhiteSpace(playerName) ? PlayerName : playerName;
                _lastAttempt = new Attempt { IsJoin = false, Port = ParseHostPortFromUi(7778), PlayerName = hostName, Valid = true };
                BeginAttemptUi(PanelState.StartingHost, "Starting host…");
                EnsureInitializer().StartAsHost(hostName);
                return;
            }

            if (TryGetConnectTo(out string connectIp, out int connectPort))
            {
                _lastAttempt = new Attempt { IsJoin = true, Ip = connectIp, Port = connectPort, PlayerName = PlayerName, Valid = true };
                BeginAttemptUi(PanelState.Joining, $"Connecting to {connectIp}:{connectPort}…");
                EnsureInitializer().StartAsJoiner(connectIp, connectPort, PlayerName);
                return;
            }

            if (autoSoloEnabled && ShouldAutoSolo())
            {
                SetState(PanelState.Idle, "Starting local host...");
                ShowMenu("Starting local host...");
                _autoHostCoroutine = StartCoroutine(AutoSoloRoutine());
            }
            else
            {
                Debug.Log("[JoinSessionUI] No auto-host: waiting for user action");
                SetState(PanelState.Idle, "Choose Host or Join");
                ShowMenu("Choose Host or Join");
            }
        }

        /// <summary>
        /// El panel PINTA la fase; no la decide. Antes deducia el estado de la sesion de tres
        /// senales sueltas (`ipc.IsConnected`, un latch `_wasConnected` y el texto de
        /// `NetworkInitializer.StatusMessage`) y con eso no se podia distinguir "joiner esperando
        /// handshake" de "conectado", ni "salida voluntaria" de "el host se cayo". Ahora la fase
        /// la lleva <see cref="SessionState"/> y aqui solo se reacciona a los CAMBIOS.
        /// </summary>
        private void Update()
        {
            IPCClient.TryGetInstance(out var ipc);
            EnsureSessionEventSubscription(ipc);

            SessionPhase phase = SessionState.Phase;
            if (phase != _renderedPhase)
            {
                SessionPhase previous = _renderedPhase;
                _renderedPhase = phase;
                Debug.Log($"[JoinSessionUI] Session phase {previous} -> {phase}");
                RenderPhase(phase);
                return;
            }

            RefreshProgressText(phase);
        }

        private void RenderPhase(SessionPhase phase)
        {
            switch (phase)
            {
                case SessionPhase.Starting:
                case SessionPhase.Connecting:
                    // El texto lo pone quien arranco el intento (OnJoinClicked sabe el ip:puerto,
                    // OnHostClicked el puerto de escucha). Repintarlo aqui lo borraria por uno
                    // generico, que es justo la informacion que hace falta mientras se espera.
                    break;

                case SessionPhase.Connected:
                    HandleConnected();
                    break;

                case SessionPhase.InGame:
                    // Ya se oculto al pasar por Connected; se reafirma la politica de cursor por
                    // si la carga de escena dejo a alguien mas escribiendola.
                    SessionCursor.Apply(IsVisible, phase);
                    break;

                case SessionPhase.Disconnecting:
                    // Ventana corta pero real: sin bloquear, un Join pulsado aqui llegaria al
                    // gate de la maquina y se perderia sin explicacion.
                    SetUiInteractable(false);
                    break;

                case SessionPhase.Failed:
                    ShowRecoverablePanel(phase, FormatFailure(SessionState.Current.Reason), isError: true);
                    break;

                case SessionPhase.Disconnected:
                    ShowRecoverablePanel(phase, FormatSessionEnd(SessionState.Current.Reason), isError: false);
                    break;

                case SessionPhase.Menu:
                    // Salida voluntaria al menu: el menu del juego ya es la UI, asi que el panel
                    // se retira en vez de anunciar "Sesion terminada" encima. Vuelve con el boton
                    // Multiplayer (NetworkMenuBootstrap.ShowConnectPanel).
                    SetState(PanelStateFor(phase, NetworkInitializer.Role.None), "Choose Host or Join");
                    HideMenu();
                    break;
            }
        }

        /// <summary>
        /// Lo unico que se repinta por frame: el contador de espera. Sale de
        /// <see cref="NetworkInitializer.StatusMessage"/>, que es quien mide el arranque del
        /// proceso — pero YA NO decide nada, solo describe.
        /// </summary>
        private void RefreshProgressText(SessionPhase phase)
        {
            if (phase != SessionPhase.Starting && phase != SessionPhase.Connecting) return;
            if (_statusText == null) return;

            var init = NetworkInitializer.Instance;
            if (init == null || string.IsNullOrEmpty(init.StatusMessage)) return;
            if (!init.StatusMessage.StartsWith("Connecting... (")) return;

            _statusText.text = $"{_attemptLabel} {init.StatusMessage}";
        }

        /// <summary>
        /// El panel de un final recuperable: mensaje, y los DOS botones que faltaban. Sin
        /// Reintentar, recuperarse de un timeout obligaba a reescribir ip y puerto; sin Volver al
        /// menu, el unico camino de salida era el que ya estaba roto.
        /// </summary>
        private void ShowRecoverablePanel(SessionPhase phase, string message, bool isError)
        {
            Debug.LogWarning($"[JoinSessionUI] Session over -> panel recuperable ({message})");
            // El arranque pudo fallar DESPUES de publicar el lobby: dejarlo abierto anunciaria un
            // ip:puerto muerto a quien acepte el invite. No-op sin Steam o sin lobby.
            // Por el publicador PRIMERO y `LeaveLobby` despues — ver el comentario de orden en
            // `SessionEndHandler`. Las dos son idempotentes.
            ServerBrowserBootstrap.WithdrawAnnouncement();
            SteamLobbyManager.Instance?.LeaveLobby();
            SetState(PanelStateFor(phase, NetworkInitializer.Role.None), message);
            ShowMenu(message);
            SetControlsVisible(true);
            SetUiInteractable(true);
            if (_statusText != null)
                _statusText.color = isError ? new Color(1f, 0.5f, 0.3f) : new Color(1f, 0.35f, 0.35f);
        }

        /// <summary>
        /// LA correspondencia fase de sesion -> estado del panel. Pura y estatica para que la
        /// suite EditMode pueda comprobar la invariante que importa: NINGUNA fase que no sea "en
        /// el mundo" puede pintarse como <see cref="PanelState.Connected"/>. Es la version
        /// generalizada del fallo original (un joiner mostrando "Connected" por tener solo el IPC
        /// local arriba) — antes eran seis `SetState` sueltos y la invariante no se podia
        /// enunciar, solo revisar a ojo uno por uno.
        ///
        /// <see cref="SessionPhase.Disconnecting"/> se pinta como Disconnected a proposito: es lo
        /// que el jugador esta viendo pasar, y dejar el estado anterior mientras se desmonta la
        /// sesion es como se acaba enseniando "Connected" sobre una sesion que ya no existe.
        /// </summary>
        public static PanelState PanelStateFor(SessionPhase phase, NetworkInitializer.Role role)
        {
            switch (phase)
            {
                case SessionPhase.Menu: return PanelState.Idle;
                case SessionPhase.Starting:
                case SessionPhase.Connecting:
                    return role == NetworkInitializer.Role.Joiner ? PanelState.Joining : PanelState.StartingHost;
                case SessionPhase.Connected:
                case SessionPhase.InGame:
                    return PanelState.Connected;
                default:
                    return PanelState.Disconnected;
            }
        }

        private static string FormatFailure(string reason) =>
            string.IsNullOrEmpty(reason) ? "Could not connect." : $"Could not connect: {reason}";

        private static string FormatSessionEnd(string reason) =>
            string.IsNullOrEmpty(reason) ? "Session ended." : $"Session ended: {reason}";

        /// <summary>
        /// ¿Hay SESIÓN, no solo tubería? Host y autosolo: su propio backend es el servidor, así
        /// que el IPC local sí es la sesión y nada cambia para ellos (localhost sigue exactamente
        /// igual de rápido que antes). Joiner: hace falta el `session_joined` que su backend emite
        /// al registrar al host, porque su IPC local está arriba tanto si el host existe como si no.
        ///
        /// Sin <see cref="NetworkInitializer"/> se responde que sí: es el modo overlay y el de los
        /// arneses de prueba, donde no hay proceso hijo ni rol que consultar.
        /// </summary>
        private bool IsSessionEstablished()
        {
            var init = NetworkInitializer.Instance;
            if (init == null) return true;
            return IsSessionEstablished(init.CurrentRole, SessionState.Current.JoinerConfirmed);
        }

        /// <summary>
        /// La regla, aparte del cableado. Pura y sin Unity dentro para que la suite EditMode la
        /// pruebe sin levantar un backend — el rol solo se puede fijar arrancando uno, así que
        /// probando el método de instancia no se probaría la rama del joiner, que es la única que
        /// importa aquí.
        ///
        /// Pública por el mismo criterio que <c>NetworkInitializer.BuildChildEnvironment</c> y
        /// <c>SessionEndHandler.ReadReason</c>: el compile-check construye cada assembly con
        /// sufijo <c>_check</c>, así que un <c>InternalsVisibleTo</c> nunca acierta el nombre.
        /// </summary>
        public static bool IsSessionEstablished(NetworkInitializer.Role role, bool joinerConfirmed)
        {
            // Host y autosolo: su propio backend ES el servidor, así que su IPC local sí es la
            // sesión. Joiner: hace falta el handshake, y solo lo confirma `session_joined`.
            if (role != NetworkInitializer.Role.Joiner) return true;
            return joinerConfirmed;
        }

        /// Observables para la suite de regresión: son el estado que decide si se entra al mundo.
        /// Lectura directa de la máquina — el panel ya no guarda una copia.
        public bool JoinerSessionConfirmed => SessionState.Current.JoinerConfirmed;
        public string LastSessionEndReason => _lastSessionEndReason;
        /// Fase que el panel tiene PINTADA. Observable para probar que la UI no se queda por
        /// detrás del estado real.
        public SessionPhase RenderedPhase => _renderedPhase;

        /// Se resuscribe cuando cambia la instancia de IPCClient (la crea el bootstrap que corra
        /// primero, y una sesión nueva puede traer otra), en vez de asumir un orden de arranque —
        /// mismo criterio que <see cref="SessionEndHandler"/>.
        private void EnsureSessionEventSubscription(IPCClient ipc)
        {
            if (ReferenceEquals(_eventSourceIpc, ipc)) return;

            if (_eventSourceIpc != null)
                _eventSourceIpc.RemoveEventListener(OnSessionEvent);

            _eventSourceIpc = ipc;

            if (_eventSourceIpc != null)
                _eventSourceIpc.AddEventListener(OnSessionEvent);
        }

        /// Pública por el mismo motivo que <see cref="IsSessionEstablished(NetworkInitializer.Role,bool)"/>:
        /// es el camino REAL por el que el gate cambia de valor, y la suite EditMode tiene que
        /// poder recorrerlo con un evento de verdad en vez de escribir el campo por detrás.
        public void OnSessionEvent(GameEventMsg ev)
        {
            if (ev.eventType == SessionJoinedEvent)
            {
                // La señal se aplica a la máquina, que es la dueña del gate. `SessionEndHandler`
                // hace lo mismo con el mismo evento: los dos escriben el MISMO campo por el mismo
                // método idempotente, así que da igual cuál llegue primero — que es exactamente
                // lo que no se podía decir cuando cada uno guardaba su copia.
                SessionState.Current.NotifySessionJoined();
                _lastSessionEndReason = "";
                Debug.Log("[JoinSessionUI] session_joined — handshake completado, entrando a la sesión");
                return;
            }

            if (ev.eventType != SessionEndedEvent) return;

            // Solo se GUARDA el motivo; el teardown es de SessionEndHandler y no se duplica aquí.
            // Este componente es el dueño del panel, así que es el que tiene que poder decir por
            // qué se cayó — antes el motivo moría en su log y el jugador leía "Disconnected".
            _lastSessionEndReason = SessionEndHandler.ReadReason(ev);
            Debug.LogWarning($"[JoinSessionUI] session_ended: {_lastSessionEndReason}");
        }

        private void OnDisable()
        {
            if (_eventSourceIpc == null) return;
            _eventSourceIpc.RemoveEventListener(OnSessionEvent);
            _eventSourceIpc = null;
        }

        /// <summary>
        /// Sets the scene to load once connected. Empty keeps pure overlay behavior.
        /// Call before the connection succeeds (e.g. from NetworkMenuBootstrap).
        /// </summary>
        public void SetGameplayScene(string sceneName) => _gameplayScene = sceneName;

        /// <summary>
        /// ADR-056: the session is over — make this component usable for a NEW one. Resets
        /// fields ONLY; the component and its GameObject must survive.
        ///
        /// Why the survival matters: the env-var auto-connect (SESSION_MODE / CONNECT_TO) lives
        /// in <see cref="Start"/>, which runs once per component. This one lives on the
        /// DontDestroyOnLoad "NetworkSession" object, and NetworkMenuBootstrap.ShowConnectPanel
        /// early-returns when it finds an existing instance instead of adding another — so going
        /// back to the menu cannot re-fire the auto-connect. Destroying this component (or its
        /// object) would break exactly that: the next ShowConnectPanel would build a fresh
        /// instance whose Start() auto-connects again, looping, and taking the multi-instance
        /// playtest harness down with it.
        ///
        /// _loadingGameplay is the one that actually blocks a second session: it latches true on
        /// the first CreateGame and TryLoadGameplayScene early-returns on it forever. _state and
        /// the panel's visibility are left to Update(), which already flips to Disconnected and
        /// re-shows the panel once the IPC connection drops.
        /// </summary>
        public void ResetForNewSession()
        {
            _loadingGameplay = false;
            _state = PanelState.Disconnected;
            // El gate del joiner es estado de sesión y muere con ella, pero ya no vive aquí: lo
            // reinicia `SessionStateMachine.NotifyLeaveComplete`/`RequestStart`. Lo que sí es de
            // este componente son las dos latas locales.
            _lastSessionEndReason = "";
            _attemptLabel = "";
            Debug.Log("[JoinSessionUI] Session state reset — ready for a new session");
        }

        /// <summary>
        /// Connection succeeded. From the menu (any scene that is NOT the gameplay scene)
        /// this loads the gameplay scene via STP's LevelManager — the connection persists
        /// because IPCClient/NetworkInitializer are DontDestroyOnLoad. Once already in the
        /// gameplay scene (or in overlay mode) it just hides the panel. The scene guard +
        /// _loadingGameplay flag prevent a reload loop.
        /// </summary>
        private void HandleConnected()
        {
            if (_state != PanelState.Connected)
                SetState(PanelState.Connected, "Connected");

            HideMenu();
            TryLoadGameplayScene();
        }

        private void TryLoadGameplayScene()
        {
            if (_loadingGameplay) return;

            // YA estamos en el mundo: modo overlay (sin escena de destino) o la sesión se
            // estableció con la escena de juego ya cargada (dar a Play dentro de ella, que es como
            // se prueba a diario). No hay carga de escena, así que `activeSceneChanged` no va a
            // disparar y la fase se quedaría en Connected para siempre — y entonces el siguiente
            // cambio de escena, el de SALIR, se leería como "acabo de entrar al mundo" y el
            // teardown no correría nunca. Se marca aquí.
            string activeScene = SceneManager.GetActiveScene().name;
            if (string.IsNullOrWhiteSpace(_gameplayScene) || activeScene == _gameplayScene)
            {
                SessionState.Current.NotifyEnteredWorld(activeScene);
                return;
            }

            var level = LevelManager.Instance;
            if (level == null || level.IsLoadingOrSaving()) return;

            _loadingGameplay = true;
            Debug.Log($"[JoinSessionUI] Connected in '{SceneManager.GetActiveScene().name}' -> CreateGame(\"{_gameplayScene}\")");
            level.CreateGame(_gameplayScene);
        }

        /// <summary>
        /// El gate de la UI contra la doble acción. NO es el único: el de verdad está en
        /// <see cref="NetworkInitializer.StartAsJoiner"/>/<c>StartAsHost</c>, que es por donde
        /// pasan también el auto-join de Steam y los arranques por variable de entorno. Éste
        /// existe para que el segundo clic no pinte "Joining…" encima de un intento en curso —
        /// sin él la UI mentiría aunque el backend no se duplicara.
        /// </summary>
        private bool RejectIfBusy(string what)
        {
            if (SessionState.Current.CanStart) return false;

            Debug.LogWarning($"[JoinSessionUI] {what} ignorado: fase {SessionState.Phase}.");
            return true;
        }

        private void OnHostClicked()
        {
            Debug.Log("[JoinSessionUI] Host clicked");
            if (RejectIfBusy("Host")) return;
            Debug.Log("[JoinSessionUI] role efectivo=host");
            Debug.Log("[JoinSessionUI] CONNECT_TO=<none>");
            CancelAutoHostBecauseUserInteracted();
            var init = EnsureInitializer();
            string playerName = string.IsNullOrWhiteSpace(_nameField.text) ? "Host" : _nameField.text;
            int hostListenPort = ParseHostPortFromUi(7778);
            Debug.Log($"[JoinSessionUI] Host listen port input={hostListenPort}");
            _lastAttempt = new Attempt { IsJoin = false, Port = hostListenPort, PlayerName = playerName, Valid = true };
            BeginAttemptUi(PanelState.StartingHost, $"Starting host on port {hostListenPort}…");
            init.StartAsHostOnPort(playerName, hostListenPort);
            ApplySelectedLocalConfigToUi(init, updateServerPort: true);
        }

        private void OnJoinClicked()
        {
            Debug.Log("[JoinSessionUI] Join clicked");
            if (RejectIfBusy("Join")) return;
            CancelAutoHostBecauseUserInteracted();
            var init = EnsureInitializer();
            string ip = string.IsNullOrWhiteSpace(_ipField.text) ? "127.0.0.1" : _ipField.text;
            int port = ParseHostPortFromUi(7778);
            string playerName = string.IsNullOrWhiteSpace(_nameField.text) ? "Player" : _nameField.text;
            Debug.Log("[JoinSessionUI] role efectivo=joiner");
            Debug.Log($"[JoinSessionUI] CONNECT_TO={ip}:{port}");

            // El destino, a la vista mientras se espera. Un "Joining…" pelado no deja distinguir
            // "está tardando" de "estoy llamando a la IP equivocada", y el segundo es el caso
            // frecuente. La espera está acotada por CONNECT_TIMEOUT en el backend.
            _lastAttempt = new Attempt { IsJoin = true, Ip = ip, Port = port, PlayerName = playerName, Valid = true };
            BeginAttemptUi(PanelState.Joining, $"Connecting to {ip}:{port}…");
            init.StartAsJoiner(ip, port, playerName);
            ApplySelectedLocalConfigToUi(init, updateServerPort: false);
        }

        /// <summary>
        /// El panel de "intento en marcha". Los cinco arranques lo montaban a mano con las mismas
        /// cuatro líneas EN ESE ORDEN, y el orden importa: <see cref="ShowMenu"/> re-habilita la
        /// interacción como efecto lateral documentado, así que el bloqueo tiene que ir DESPUÉS.
        /// Copiado cinco veces es como se consigue que el sexto se lo salte.
        /// </summary>
        private void BeginAttemptUi(PanelState state, string message)
        {
            _attemptLabel = message;
            _renderedPhase = SessionPhase.Starting; // lo que este método deja pintado
            SetState(state, message);
            ShowMenu(message);
            SetUiInteractable(false);
        }

        /// <summary>Reintenta EL MISMO intento. Es el botón [Retry] del panel de error.</summary>
        private void OnRetryClicked()
        {
            Debug.Log("[JoinSessionUI] Retry clicked");
            if (RejectIfBusy("Retry")) return;
            if (!_lastAttempt.Valid)
            {
                SetState(PanelState.ManualEditing, "Nothing to retry — choose Host or Join");
                return;
            }

            var init = EnsureInitializer();
            if (_lastAttempt.IsJoin)
            {
                BeginAttemptUi(PanelState.Joining, $"Connecting to {_lastAttempt.Ip}:{_lastAttempt.Port}…");
                init.StartAsJoiner(_lastAttempt.Ip, _lastAttempt.Port, _lastAttempt.PlayerName);
                ApplySelectedLocalConfigToUi(init, updateServerPort: false);
            }
            else
            {
                BeginAttemptUi(PanelState.StartingHost, $"Starting host on port {_lastAttempt.Port}…");
                init.StartAsHostOnPort(_lastAttempt.PlayerName, _lastAttempt.Port);
                ApplySelectedLocalConfigToUi(init, updateServerPort: true);
            }
        }

        /// <summary>
        /// [Back to menu]. Sólo acusa recibo del final y cierra el panel: los recursos ya los
        /// soltó el teardown al entrar en Disconnected/Failed, así que esto NO vuelve a limpiar
        /// nada — si lo hiciera, el segundo clic tendría efectos.
        /// </summary>
        private void OnBackToMenuClicked()
        {
            Debug.Log("[JoinSessionUI] Back to menu clicked");
            if (!SessionState.Current.AcknowledgeAndReturnToMenu())
            {
                // Fase viva: esto es un abandono de verdad, no un acuse de recibo.
                SessionEndHandler.LeaveCurrentSession("user left to menu", showPanel: false);
                return;
            }
            _renderedPhase = SessionPhase.Menu;
            SetState(PanelState.Idle, "Choose Host or Join");
            HideMenu();
        }

        /// <summary>
        /// Botón "Invite via Steam". Si aún no se es host, arranca el host por el MISMO
        /// camino que <see cref="OnHostClicked"/> (StartAsHost) y solo después publica el
        /// lobby: el puerto que se publica tiene que ser el ya seleccionado. Si ya se es
        /// host, se limita a crear/refrescar el lobby y abrir el overlay.
        /// El botón Host clásico no se toca ni se desvía.
        /// </summary>
        private void OnSteamInviteClicked()
        {
            var steam = SteamLobbyManager.Instance;
            if (steam == null || !SteamLobbyManager.IsAvailable)
            {
                Debug.LogWarning("[JoinSessionUI] Steam invite ignored: Steam unavailable.");
                SetState(PanelState.ManualEditing, "Steam unavailable");
                return;
            }

            CancelAutoHostBecauseUserInteracted();
            var init = EnsureInitializer();

            if (init.CurrentRole == NetworkInitializer.Role.Joiner)
            {
                Debug.LogWarning("[JoinSessionUI] Steam invite ignored: this instance is a joiner.");
                SetState(PanelState.ManualEditing, "Only the host can invite");
                return;
            }

            string ip = (_ipField == null || string.IsNullOrWhiteSpace(_ipField.text)) ? "127.0.0.1" : _ipField.text;

            if (init.CurrentRole != NetworkInitializer.Role.Host)
            {
                Debug.Log("[JoinSessionUI] Steam invite: no host yet, starting one first");
                Debug.Log("[JoinSessionUI] role efectivo=host (steam invite)");
                string playerName = SteamLobbyManager.SanitizePlayerName(SteamLobbyManager.SteamPersonaName);
                int hostListenPort = ParseHostPortFromUi(7778);
                if (RejectIfBusy("Steam invite host")) return;
                _lastAttempt = new Attempt { IsJoin = false, Port = hostListenPort, PlayerName = playerName, Valid = true };
                BeginAttemptUi(PanelState.StartingHost, "Starting host + Steam lobby…");
                init.StartAsHostOnPort(playerName, hostListenPort);
                ApplySelectedLocalConfigToUi(init, updateServerPort: true);
            }

            // LastSelectedNetPort es el puerto UDP realmente elegido por SelectLaunchConfig,
            // que puede diferir del tecleado si estaba ocupado. Publicar el tecleado dejaría
            // el lobby apuntando a un puerto muerto.
            int connectPort = init.LastSelectedNetPort > 0 ? init.LastSelectedNetPort : ParseHostPortFromUi(7778);
            Debug.Log($"[JoinSessionUI] Steam lobby publish {ip}:{connectPort}");
            steam.CreateLobbyAndOpenInvite(ip, connectPort);
        }

        /// <summary>
        /// Entrada del auto-join de Steam. Devuelve false si no hay panel vivo, para que
        /// <see cref="SteamLobbyManager"/> caiga en StartAsJoiner directo — el destino es
        /// el mismo método en ambos casos, nunca un segundo camino de conexión.
        /// </summary>
        public static bool TryBeginSteamJoin(string ip, int port, string playerName)
        {
            if (_instance == null) return false;
            _instance.BeginSteamJoin(ip, port, playerName);
            return true;
        }

        private void BeginSteamJoin(string ip, int port, string playerName)
        {
            CancelAutoHostBecauseUserInteracted();
            var init = EnsureInitializer();

            // El gate es la FASE, no el rol: tras un teardown el rol vuelve a None mientras el
            // teardown sigue corriendo, y un auto-join que llegara en esa ventana se colaba.
            if (RejectIfBusy($"Steam join ({init.CurrentRole} activo)")) return;

            // Reflejar en los campos lo que llegó por el lobby: el humano ve de dónde
            // salieron los valores, y un Disconnect + Join manual reintenta lo mismo.
            if (_ipField != null) _ipField.SetTextWithoutNotify(ip);
            if (_portField != null) _portField.SetTextWithoutNotify(port.ToString());
            if (_nameField != null) _nameField.SetTextWithoutNotify(playerName);

            Debug.Log("[JoinSessionUI] role efectivo=joiner (steam)");
            Debug.Log($"[JoinSessionUI] CONNECT_TO={ip}:{port}");

            _lastAttempt = new Attempt { IsJoin = true, Ip = ip, Port = port, PlayerName = playerName, Valid = true };
            BeginAttemptUi(PanelState.Joining, $"Connecting to {ip}:{port} (Steam)…");
            init.StartAsJoiner(ip, port, playerName);
            ApplySelectedLocalConfigToUi(init, updateServerPort: false);
        }

        /// <summary>
        /// Abandonar por el botón del panel. Va por el MISMO teardown que el fin de sesión del
        /// backend y que el `Quit to Menu` del vendor — antes se limitaba a `init.Shutdown()`, que
        /// mata el backend pero no para el IPC, no rearma el panel y no suelta el cursor.
        /// Idempotente: el segundo clic no encuentra sesión viva y no hace nada.
        /// </summary>
        private void OnDisconnectClicked()
        {
            SessionEndHandler.LeaveCurrentSession("user disconnected", showPanel: true);
        }

        private NetworkInitializer EnsureInitializer()
        {
            var init = NetworkInitializer.Instance;
            if (init == null)
            {
                var go = new GameObject("NetworkInitializer");
                init = go.AddComponent<NetworkInitializer>();
            }
            return init;
        }

        private IEnumerator AutoSoloRoutine()
        {
            _autoHostRequested = true;
            Debug.Log("[JoinSessionUI] AutoSolo requested");

            yield return new WaitForSeconds(autoSoloDelaySeconds);

            if (_state == PanelState.ManualEditing) yield break;
            if (_state != PanelState.Idle) yield break;
            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                SetState(PanelState.Connected, "Connected");
                HideMenu();
                yield break;
            }

            var init = EnsureInitializer();
            if (init.HasBackendProcess || !SessionState.Current.CanStart)
            {
                Debug.Log("[JoinSessionUI] Auto-host skipped; backend/session already exists");
                yield break;
            }

            _lastAttempt = new Attempt { IsJoin = false, Port = ParseHostPortFromUi(7778), PlayerName = "Host", Valid = true };
            BeginAttemptUi(PanelState.StartingHost, "Starting local host…");
            Debug.Log("[JoinSessionUI] role efectivo=autosolo");
            Debug.Log("[JoinSessionUI] CONNECT_TO=<none>");
            init.StartAsAutoSolo("Host");
            ApplySelectedLocalConfigToUi(init, updateServerPort: true);
        }

        private bool ShouldAutoSolo()
        {
            if (!IsAutoSoloEnabled()) return false;
            if (_autoHostRequested) return false;
            if (HasExplicitJoinEnvironment()) return false;

            var init = NetworkInitializer.Instance;
            if (init != null && (init.HasBackendProcess || init.CurrentRole != NetworkInitializer.Role.None))
                return false;

            return true;
        }

        private static bool HasExplicitJoinEnvironment()
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONNECT_TO")))
                return true;

            string role = Environment.GetEnvironmentVariable("SESSION_MODE");
            return !string.IsNullOrWhiteSpace(role) &&
                   role.Equals("join", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsAutoSoloEnabled()
        {
            string value = Environment.GetEnvironmentVariable("AUTO_SOLO");
            return enableAutoSolo || value == "1" || IsTruthy(value);
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSessionMode(string sessionMode, string expected)
        {
            return !string.IsNullOrWhiteSpace(sessionMode) &&
                   sessionMode.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string DetermineStartupMode(string sessionMode, bool autoSoloEnabled)
        {
            if (autoSoloEnabled) return "autosolo";
            if (IsSessionMode(sessionMode, "host")) return "env-host";
            if (IsSessionMode(sessionMode, "join"))
                return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONNECT_TO")) ? "menu/manual" : "env-join";
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONNECT_TO"))) return "env-join";
            return "menu/manual";
        }

        private static bool TryGetConnectTo(out string ip, out int port)
        {
            ip = null;
            port = 0;

            string connectTo = Environment.GetEnvironmentVariable("CONNECT_TO");
            if (string.IsNullOrWhiteSpace(connectTo))
                return false;

            int colon = connectTo.LastIndexOf(':');
            if (colon <= 0 || colon >= connectTo.Length - 1)
            {
                Debug.LogError($"[JoinSessionUI] Invalid CONNECT_TO value: {connectTo}");
                return false;
            }

            if (!int.TryParse(connectTo.Substring(colon + 1), out port))
            {
                Debug.LogError($"[JoinSessionUI] Invalid CONNECT_TO port: {connectTo}");
                return false;
            }

            ip = connectTo.Substring(0, colon);
            return true;
        }

        private void SetState(PanelState state, string message)
        {
            if (_state != state)
                Debug.Log($"[JoinSessionUI] UI state changed: {_state} -> {state}");

            // Arranca un intento NUEVO: se borra el veredicto del anterior. El gate del joiner ya
            // no se toca aquí — lo reinicia `SessionStateMachine.RequestStart`, que es el único
            // sitio por el que empieza un intento.
            if (state == PanelState.StartingHost || state == PanelState.Joining)
                _lastSessionEndReason = "";

            _state = state;

            if (_statusText == null) return;

            switch (state)
            {
                case PanelState.Idle:
                    _titleText.text = "BACKROOMS SURVIVAL";
                    _statusText.text = message ?? "";
                    _statusText.color = Color.white;
                    SetControlsVisible(true);
                    SetUiInteractable(true);
                    break;
                case PanelState.ManualEditing:
                    _titleText.text = "BACKROOMS SURVIVAL";
                    _statusText.text = message ?? "";
                    _statusText.color = Color.white;
                    SetControlsVisible(true);
                    SetUiInteractable(true);
                    break;
                case PanelState.StartingHost:
                    _statusText.text = string.IsNullOrEmpty(message) ? "Starting host..." : message;
                    _statusText.color = new Color(1f, 0.85f, 0.3f);
                    SetControlsVisible(false);
                    SetUiInteractable(false);
                    break;
                case PanelState.Joining:
                    _statusText.text = string.IsNullOrEmpty(message) ? "Joining..." : message;
                    _statusText.color = new Color(1f, 0.85f, 0.3f);
                    SetControlsVisible(false);
                    SetUiInteractable(false);
                    break;
                case PanelState.Connected:
                    _statusText.text = string.IsNullOrEmpty(message) ? "Connected" : message;
                    _statusText.color = new Color(0.4f, 1f, 0.4f);
                    break;
                case PanelState.Disconnected:
                    _statusText.text = string.IsNullOrEmpty(message) ? "Disconnected" : message;
                    _statusText.color = new Color(1f, 0.35f, 0.35f);
                    SetControlsVisible(true);
                    SetUiInteractable(true);
                    break;
            }
        }

        /// <summary>
        /// Muestra el panel y, de paso, RE-HABILITA toda la interaccion. El efecto lateral es
        /// deliberado (lo necesitan los caminos Idle/Disconnected), pero no lo dice el nombre:
        /// quien quiera el panel visible y BLOQUEADO tiene que llamar SetUiInteractable(false)
        /// DESPUES de ShowMenu. Ver el comentario junto a SetUiInteractable(true), mas abajo.
        /// </summary>
        public void ShowMenu(string message)
        {
            if (_panel != null) _panel.SetActive(true);
            if (_panelCanvasGroup != null)
            {
                _panelCanvasGroup.alpha = 1f;
                _panelCanvasGroup.interactable = true;
                _panelCanvasGroup.blocksRaycasts = true;
            }
            if (_statusText != null) _statusText.text = message ?? "";
            // Un panel visible SIEMPRE libera el cursor, sea cual sea la fase. La regla entera
            // vive en SessionCursor; aquí no se escribe Cursor.* a mano para que no vuelva a
            // haber cinco sitios compitiendo por él.
            SessionCursor.Apply(menuVisible: true, phase: SessionState.Phase);
            // A PROPOSITO: los caminos que vuelven al menu (Update -> Disconnected, Update ->
            // error de arranque y OnDisconnectClicked) quieren el panel usable y se apoyan en
            // esta linea. Los que NO lo quieren tienen que deshacerlo ELLOS con
            // SetUiInteractable(false) inmediatamente despues de ShowMenu; hoy lo hacen los seis
            // arranques de host/join: los dos de Start (SESSION_MODE=host y CONNECT_TO),
            // OnHostClicked, OnJoinClicked, OnSteamInviteClicked y BeginSteamJoin.
            SetUiInteractable(true);
            // OJO, ESTE LOG NO ES EVIDENCIA DEL ESTADO FINAL DEL PANEL: se emite justo despues
            // de la linea de arriba, o sea ANTES de la correccion del llamante. En TODOS los
            // arranques de host/join imprime interactable=True y el panel acaba en False.
            // (SetState tampoco ayuda a leerlo: sale antes del switch si _statusText es null,
            // asi que sus ramas de interactividad solo corren una vez BuildUI ha creado el panel.)
            Debug.Log(
                $"[JoinSessionUI] UI shown interactable={_panelCanvasGroup?.interactable} " +
                $"blocksRaycasts={_panelCanvasGroup?.blocksRaycasts}");
        }

        /// <summary>
        /// Reabrir el panel desde el menú (botón Multiplayer). A diferencia de
        /// <see cref="ShowMenu"/>, que sólo lo hace visible, esto lo devuelve a un estado de
        /// PARTIDA: controles visibles, campos usables y sin el veredicto de la sesión anterior.
        ///
        /// Sin esto, reabrirlo tras una sesión lo enseñaba con el `PanelState` en el que lo dejó
        /// la última transición — y si esa fue `Connected` (que no toca la visibilidad de los
        /// controles), el panel salía SIN los botones de Host y Join. Ése era el callejón sin
        /// salida: menú abierto, panel visible, nada que pulsar.
        /// </summary>
        public void ShowConnectMenu()
        {
            if (!SessionState.Current.CanStart)
            {
                // Hay sesión viva: se enseña lo que hay, no un panel de arranque que mentiría.
                ShowMenu(_attemptLabel);
                return;
            }

            SetState(PanelState.Idle, "Choose Host or Join");
            ShowMenu("Choose Host or Join");
            SetControlsVisible(true);
            SetUiInteractable(true);
        }

        public void HideMenu()
        {
            if (_panel != null) _panel.SetActive(false);
            if (_panelCanvasGroup != null)
            {
                _panelCanvasGroup.alpha = 0f;
                _panelCanvasGroup.interactable = false;
                _panelCanvasGroup.blocksRaycasts = false;
            }
            // ESTA LÍNEA ERA EL FALLO DEL RATÓN. Capturaba el cursor SIEMPRE, también al ocultar
            // el panel estando en el menú: volver al menú con el panel cerrado dejaba el ratón
            // preso sobre una pantalla que solo se usa con el ratón. Ahora decide la fase.
            SessionCursor.Apply(menuVisible: false, phase: SessionState.Phase);
        }

        public void SetUiInteractable(bool value)
        {
            if (_panelCanvasGroup != null)
            {
                _panelCanvasGroup.interactable = value;
                _panelCanvasGroup.blocksRaycasts = value;
            }

            if (_hostButton != null) _hostButton.interactable = value;
            if (_joinButton != null) _joinButton.interactable = value;
            if (_steamInviteButton != null) _steamInviteButton.interactable = value;
            if (_disconnectButton != null) _disconnectButton.interactable = value;
            if (_retryButton != null) _retryButton.interactable = value;
            if (_backButton != null) _backButton.interactable = value;
            if (_ipField != null) _ipField.interactable = value;
            if (_portField != null) _portField.interactable = value;
            if (_nameField != null) _nameField.interactable = value;
        }

        public void CancelAutoHostBecauseUserInteracted()
        {
            if (_autoHostCoroutine != null)
            {
                StopCoroutine(_autoHostCoroutine);
                _autoHostCoroutine = null;
            }

            if (_autoHostRequested || _state == PanelState.Idle)
                Debug.Log("[JoinSessionUI] Auto-host cancelled by user interaction");

            _autoHostRequested = false;

            if (_state == PanelState.Idle)
                SetState(PanelState.ManualEditing, "");
        }

        public static bool IsUserEditingInput()
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == null) return false;

            return selected.GetComponent<InputField>() != null ||
                   selected.GetComponent("TMP_InputField") != null;
        }

        private void SetControlsVisible(bool visible)
        {
            if (_ipField != null) _ipField.gameObject.SetActive(visible);
            if (_portField != null) _portField.gameObject.SetActive(visible);
            if (_nameField != null) _nameField.gameObject.SetActive(visible);
            if (_hostButton != null) _hostButton.gameObject.SetActive(visible);
            if (_joinButton != null) _joinButton.gameObject.SetActive(visible);
            // Sin Steam el botón nunca reaparece, aunque el resto de controles vuelvan.
            if (_steamInviteButton != null)
                _steamInviteButton.gameObject.SetActive(visible && SteamLobbyManager.IsAvailable);
            if (_disconnectButton != null) _disconnectButton.gameObject.SetActive(false);

            // [Retry] y [Back to menu] SOLO en los dos finales recuperables. Enseñarlos en Idle
            // sería ofrecer reintentar algo que no ha pasado; esconderlos ahí es lo que los hace
            // legibles como "esto se ha caído, y esto es lo que puedes hacer".
            bool recoverable = visible &&
                (SessionState.Phase == SessionPhase.Failed || SessionState.Phase == SessionPhase.Disconnected);
            if (_retryButton != null) _retryButton.gameObject.SetActive(recoverable && _lastAttempt.Valid);
            if (_backButton != null) _backButton.gameObject.SetActive(recoverable);
        }


        private void ApplySelectedLocalConfigToUi(NetworkInitializer init, bool updateServerPort)
        {
            if (init == null) return;

            Debug.Log($"[JoinSessionUI] Selected IPC_PORT={init.LastSelectedIpcPort}");
            Debug.Log($"[JoinSessionUI] Selected NET_PORT={init.LastSelectedNetPort}");
            Debug.Log($"[JoinSessionUI] Selected NET_ID={init.LastSelectedNetId}");

            if (updateServerPort && _portField != null && init.LastSelectedNetPort > 0)
                _portField.SetTextWithoutNotify(init.LastSelectedNetPort.ToString());
        }

        private int ParseHostPortFromUi(int fallback)
        {
            if (_portField == null || string.IsNullOrWhiteSpace(_portField.text))
                return fallback;

            return int.TryParse(_portField.text, out int parsed) ? parsed : fallback;
        }


        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_canvas != null) Destroy(_canvas.gameObject);
        }
    }
}
