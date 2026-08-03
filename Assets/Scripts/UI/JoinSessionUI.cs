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

        private const float InputWidth = 300f;
        private const float InputHeight = 40f;
        private const float ButtonWidth = 120f;
        private const float ButtonHeight = 40f;
        private const float Spacing = 10f;
        private const float PanelPadding = 24f;

        private static JoinSessionUI _instance;
        private bool _autoHostRequested;
        private bool _wasConnected;
        private bool _built;
        private Coroutine _autoHostCoroutine;

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
            EnsureEventSystem();
            _wasConnected = IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected;

            if (_wasConnected)
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
                SetState(PanelState.StartingHost, "Starting host...");
                ShowMenu("Starting host...");
                SetUiInteractable(false);
                string playerName = Environment.GetEnvironmentVariable("NET_NAME");
                EnsureInitializer().StartAsHost(string.IsNullOrWhiteSpace(playerName) ? PlayerName : playerName);
                return;
            }

            if (TryGetConnectTo(out string connectIp, out int connectPort))
            {
                SetState(PanelState.Joining, "Joining...");
                ShowMenu("Joining...");
                SetUiInteractable(false);
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

        private void Update()
        {
            bool connected = IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected;

            if (connected)
            {
                if (_state != PanelState.Connected)
                {
                    Debug.Log("[JoinSessionUI] UI hidden on IPC connected");
                    HandleConnected();
                }
                _wasConnected = true;
                return;
            }

            if (_wasConnected)
            {
                Debug.Log("[JoinSessionUI] IPC disconnected -> showing session UI");
                SetState(PanelState.Disconnected, "Disconnected");
                ShowMenu("Disconnected");
                SetUiInteractable(true);
                _wasConnected = false;
                return;
            }

            var init = NetworkInitializer.Instance;
            if (init == null || _statusText == null) return;

            if (_state == PanelState.StartingHost || _state == PanelState.Joining)
            {
                if (!string.IsNullOrEmpty(init.StatusMessage) &&
                    (init.StatusMessage.StartsWith("Error") || init.StatusMessage.StartsWith("Timeout") ||
                     init.StatusMessage.StartsWith("Backend exited")))
                {
                    // El arranque falló DESPUÉS de haber publicado el lobby: dejarlo abierto
                    // anunciaría un ip:puerto muerto a quien acepte el invite. No-op si no
                    // hay Steam o no hay lobby, así que el flujo manual no lo nota.
                    SteamLobbyManager.Instance?.LeaveLobby();
                    SetState(PanelState.Disconnected, init.StatusMessage);
                    ShowMenu(init.StatusMessage);
                    SetUiInteractable(true);
                }
            }
        }

        /// <summary>
        /// Sets the scene to load once connected. Empty keeps pure overlay behavior.
        /// Call before the connection succeeds (e.g. from NetworkMenuBootstrap).
        /// </summary>
        public void SetGameplayScene(string sceneName) => _gameplayScene = sceneName;

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
            if (string.IsNullOrWhiteSpace(_gameplayScene)) return; // overlay mode
            if (SceneManager.GetActiveScene().name == _gameplayScene) return; // already there

            var level = LevelManager.Instance;
            if (level == null || level.IsLoadingOrSaving()) return;

            _loadingGameplay = true;
            Debug.Log($"[JoinSessionUI] Connected in '{SceneManager.GetActiveScene().name}' -> CreateGame(\"{_gameplayScene}\")");
            level.CreateGame(_gameplayScene);
        }

        private void OnHostClicked()
        {
            Debug.Log("[JoinSessionUI] Host clicked");
            Debug.Log("[JoinSessionUI] role efectivo=host");
            Debug.Log("[JoinSessionUI] CONNECT_TO=<none>");
            CancelAutoHostBecauseUserInteracted();
            var init = EnsureInitializer();
            string playerName = string.IsNullOrWhiteSpace(_nameField.text) ? "Host" : _nameField.text;
            int hostListenPort = ParseHostPortFromUi(7778);
            Debug.Log($"[JoinSessionUI] Host listen port input={hostListenPort}");
            SetState(PanelState.StartingHost, "Starting host...");
            ShowMenu("Starting host...");
            SetUiInteractable(false);
            init.StartAsHostOnPort(playerName, hostListenPort);
            ApplySelectedLocalConfigToUi(init, updateServerPort: true);
        }

        private void OnJoinClicked()
        {
            Debug.Log("[JoinSessionUI] Join clicked");
            CancelAutoHostBecauseUserInteracted();
            var init = EnsureInitializer();
            string ip = string.IsNullOrWhiteSpace(_ipField.text) ? "127.0.0.1" : _ipField.text;
            int port = ParseHostPortFromUi(7778);
            string playerName = string.IsNullOrWhiteSpace(_nameField.text) ? "Player" : _nameField.text;
            Debug.Log("[JoinSessionUI] role efectivo=joiner");
            Debug.Log($"[JoinSessionUI] CONNECT_TO={ip}:{port}");

            SetState(PanelState.Joining, "Joining...");
            ShowMenu("Joining...");
            SetUiInteractable(false);
            init.StartAsJoiner(ip, port, playerName);
            ApplySelectedLocalConfigToUi(init, updateServerPort: false);
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
                SetState(PanelState.StartingHost, "Starting host + Steam lobby...");
                ShowMenu("Starting host + Steam lobby...");
                SetUiInteractable(false);
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

            if (init.CurrentRole != NetworkInitializer.Role.None)
            {
                Debug.LogWarning($"[JoinSessionUI] Steam join ignored: {init.CurrentRole} session already active.");
                return;
            }

            // Reflejar en los campos lo que llegó por el lobby: el humano ve de dónde
            // salieron los valores, y un Disconnect + Join manual reintenta lo mismo.
            if (_ipField != null) _ipField.SetTextWithoutNotify(ip);
            if (_portField != null) _portField.SetTextWithoutNotify(port.ToString());
            if (_nameField != null) _nameField.SetTextWithoutNotify(playerName);

            Debug.Log("[JoinSessionUI] role efectivo=joiner (steam)");
            Debug.Log($"[JoinSessionUI] CONNECT_TO={ip}:{port}");

            SetState(PanelState.Joining, "Joining via Steam...");
            ShowMenu("Joining via Steam...");
            SetUiInteractable(false);
            init.StartAsJoiner(ip, port, playerName);
            ApplySelectedLocalConfigToUi(init, updateServerPort: false);
        }

        private void OnDisconnectClicked()
        {
            var init = NetworkInitializer.Instance;
            if (init != null) init.Shutdown();
            // El lobby publica un ip:puerto que acaba de morir; dejarlo abierto invitaría
            // a un backend inexistente.
            SteamLobbyManager.Instance?.LeaveLobby();
            SetState(PanelState.Disconnected, "Disconnected");
            ShowMenu("Disconnected");
            SetUiInteractable(true);
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
            if (init.HasBackendProcess || init.CurrentRole != NetworkInitializer.Role.None)
            {
                Debug.Log("[JoinSessionUI] Auto-host skipped; backend/session already exists");
                yield break;
            }

            SetState(PanelState.StartingHost, "Starting local host...");
            SetUiInteractable(false);
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
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            SetUiInteractable(true);
            Debug.Log(
                $"[JoinSessionUI] UI shown interactable={_panelCanvasGroup?.interactable} " +
                $"blocksRaycasts={_panelCanvasGroup?.blocksRaycasts}");
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
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
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
            if (_ipField != null) _ipField.interactable = value;
            if (_portField != null) _portField.interactable = value;
            if (_nameField != null) _nameField.interactable = value;
        }

        public void SetInteractable(bool value) => SetUiInteractable(value);

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
