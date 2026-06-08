using System;
using System.Collections;
using BackroomsSurvival.Net;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    public sealed class JoinSessionUI : MonoBehaviour
    {
        public enum PanelState { Idle, ManualEditing, StartingHost, Joining, Connected, Disconnected }

        public bool autoHostIfNoSession = true;
        public float autoHostDelaySeconds = 0.25f;

        private Canvas _canvas;
        private GameObject _panel;
        private CanvasGroup _panelCanvasGroup;
        private InputField _ipField;
        private InputField _portField;
        private InputField _nameField;
        private Button _hostButton;
        private Button _joinButton;
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
                SetState(PanelState.Connected, "Connected");
                HideMenu();
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

            if (ShouldAutoHost())
            {
                SetState(PanelState.Idle, "Starting local host...");
                ShowMenu("Starting local host...");
                _autoHostCoroutine = StartCoroutine(AutoHostRoutine());
            }
            else
            {
                SetState(PanelState.Idle, "");
                ShowMenu("");
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
                    SetState(PanelState.Connected, "Connected");
                    HideMenu();
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
                    SetState(PanelState.Disconnected, init.StatusMessage);
                    ShowMenu(init.StatusMessage);
                    SetUiInteractable(true);
                }
            }
        }

        private void OnHostClicked()
        {
            Debug.Log("[JoinSessionUI] Host clicked");
            CancelAutoHostBecauseUserInteracted();
            var init = EnsureInitializer();
            string playerName = string.IsNullOrWhiteSpace(_nameField.text) ? "Host" : _nameField.text;
            SetState(PanelState.StartingHost, "Starting host...");
            ShowMenu("Starting host...");
            SetUiInteractable(false);
            init.StartAsHost(playerName);
            ApplySelectedLocalConfigToUi(init, updateServerPort: true);
        }

        private void OnJoinClicked()
        {
            Debug.Log("[JoinSessionUI] Join clicked");
            CancelAutoHostBecauseUserInteracted();
            var init = EnsureInitializer();
            string ip = string.IsNullOrWhiteSpace(_ipField.text) ? "127.0.0.1" : _ipField.text;
            int port = 7778;
            if (!string.IsNullOrWhiteSpace(_portField.text))
                int.TryParse(_portField.text, out port);
            string playerName = string.IsNullOrWhiteSpace(_nameField.text) ? "Player" : _nameField.text;

            SetState(PanelState.Joining, "Joining...");
            ShowMenu("Joining...");
            SetUiInteractable(false);
            init.StartAsJoiner(ip, port, playerName);
            ApplySelectedLocalConfigToUi(init, updateServerPort: false);
        }

        private void OnDisconnectClicked()
        {
            var init = NetworkInitializer.Instance;
            if (init != null) init.Shutdown();
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

        private IEnumerator AutoHostRoutine()
        {
            _autoHostRequested = true;
            Debug.Log("[JoinSessionUI] Auto-host requested");

            yield return new WaitForSeconds(autoHostDelaySeconds);

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
            init.StartAsHost("Host");
            ApplySelectedLocalConfigToUi(init, updateServerPort: true);
        }

        private bool ShouldAutoHost()
        {
            if (!autoHostIfNoSession) return false;
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
            if (_disconnectButton != null) _disconnectButton.gameObject.SetActive(false);
        }

        // ─── UI Construction (VerticalLayoutGroup, fixed sizes) ───

        private void BuildUI()
        {
            if (_built) return;
            _built = true;

            // Canvas
            _canvas = new GameObject("JoinSessionCanvas").AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 200;
            DontDestroyOnLoad(_canvas.gameObject);

            var scaler = _canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            _canvas.gameObject.AddComponent<GraphicRaycaster>();

            // Panel — centered, semi-transparent background
            _panel = new GameObject("Panel");
            _panel.transform.SetParent(_canvas.transform, false);
            var panelRt = _panel.AddComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);

            var panelImg = _panel.AddComponent<Image>();
            panelImg.color = new Color(0.06f, 0.06f, 0.10f, 0.88f);
            panelImg.raycastTarget = true;

            _panelCanvasGroup = _panel.AddComponent<CanvasGroup>();
            _panelCanvasGroup.alpha = 1f;
            _panelCanvasGroup.interactable = true;
            _panelCanvasGroup.blocksRaycasts = true;

            // Vertical layout on panel
            var vlg = _panel.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.spacing = Spacing;
            vlg.padding = new RectOffset(
                (int)PanelPadding, (int)PanelPadding,
                (int)PanelPadding, (int)PanelPadding);
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;

            var csf = _panel.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Title
            _titleText = CreateLabel(_panel.transform, "Title", "BACKROOMS SURVIVAL", 22,
                FontStyle.Bold, Color.white, InputWidth, 36f);

            // Status line
            _statusText = CreateLabel(_panel.transform, "Status", "", 14,
                FontStyle.Normal, new Color(0.7f, 0.7f, 0.7f), InputWidth, 24f);

            // Input fields (300x40)
            _nameField = CreateInputField(_panel.transform, "NameField", "Player Name", "Player");
            _ipField = CreateInputField(_panel.transform, "IPField", "Server IP", "127.0.0.1");
            _portField = CreateInputField(_panel.transform, "PortField", "Port", "7778");
            RegisterInputCallbacks(_nameField, "Player Name");
            RegisterInputCallbacks(_ipField, "IP");
            RegisterInputCallbacks(_portField, "Port");

            // Button row
            var buttonRow = new GameObject("ButtonRow");
            buttonRow.transform.SetParent(_panel.transform, false);
            var rowRt = buttonRow.AddComponent<RectTransform>();
            rowRt.sizeDelta = new Vector2(InputWidth, ButtonHeight);

            var rowLayout = buttonRow.AddComponent<HorizontalLayoutGroup>();
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.spacing = 16f;
            rowLayout.childControlWidth = false;
            rowLayout.childControlHeight = false;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;

            _hostButton = CreateButton(buttonRow.transform, "HostBtn", "Host",
                new Color(0.18f, 0.55f, 0.28f));
            RegisterButtonCallbacks(_hostButton);
            _hostButton.onClick.AddListener(OnHostClicked);

            _joinButton = CreateButton(buttonRow.transform, "JoinBtn", "Join",
                new Color(0.20f, 0.40f, 0.70f));
            RegisterButtonCallbacks(_joinButton);
            _joinButton.onClick.AddListener(OnJoinClicked);

            // Disconnect button (hidden initially)
            _disconnectButton = CreateButton(_panel.transform, "DisconnectBtn", "Disconnect",
                new Color(0.60f, 0.20f, 0.20f));
            RegisterButtonCallbacks(_disconnectButton);
            _disconnectButton.onClick.AddListener(OnDisconnectClicked);
            _disconnectButton.gameObject.SetActive(false);
        }

        private void RegisterInputCallbacks(InputField field, string fieldName)
        {
            if (field == null) return;

            field.onValueChanged.AddListener(_ => CancelAutoHostBecauseUserInteracted());
            AddEventTrigger(field.gameObject, EventTriggerType.Select, _ =>
            {
                Debug.Log($"[JoinSessionUI] Input focused: {fieldName}");
                CancelAutoHostBecauseUserInteracted();
            });
            AddEventTrigger(field.gameObject, EventTriggerType.PointerDown, _ => CancelAutoHostBecauseUserInteracted());
        }

        private void RegisterButtonCallbacks(Button button)
        {
            if (button == null) return;
            AddEventTrigger(button.gameObject, EventTriggerType.PointerDown, _ => CancelAutoHostBecauseUserInteracted());
        }

        private static void AddEventTrigger(GameObject go, EventTriggerType type, UnityEngine.Events.UnityAction<BaseEventData> callback)
        {
            var trigger = go.GetComponent<EventTrigger>();
            if (trigger == null)
                trigger = go.AddComponent<EventTrigger>();

            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(callback);
            trigger.triggers.Add(entry);
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

        public static void EnsureEventSystem()
        {
            var existing = EventSystem.current ?? UnityEngine.Object.FindFirstObjectByType<EventSystem>();
            if (existing != null)
            {
                Debug.Log($"[JoinSessionUI] EventSystem found: {existing.name}");
                EnsureInputModule(existing.gameObject);
                return;
            }

            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            EnsureInputModule(go);
            DontDestroyOnLoad(go);
            Debug.Log("[JoinSessionUI] EventSystem created");
        }

        private static void EnsureInputModule(GameObject go)
        {
#if ENABLE_INPUT_SYSTEM
            if (go.GetComponent<InputSystemUIInputModule>() != null)
                return;

            foreach (var module in go.GetComponents<BaseInputModule>())
                UnityEngine.Object.Destroy(module);

            go.AddComponent<InputSystemUIInputModule>();
            Debug.Log("[JoinSessionUI] EventSystem using InputSystemUIInputModule");
#else
            if (go.GetComponent<BaseInputModule>() != null)
                return;

            go.AddComponent<StandaloneInputModule>();
            Debug.Log("[JoinSessionUI] EventSystem using StandaloneInputModule");
#endif
        }

        private static Text CreateLabel(Transform parent, string name, string text,
            int fontSize, FontStyle style, Color color, float width, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.preferredHeight = height;

            var txt = go.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.color = color;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.text = text;
            txt.raycastTarget = false;
            txt.supportRichText = true;

            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
            shadow.effectDistance = new Vector2(1f, -1f);

            return txt;
        }

        private static InputField CreateInputField(Transform parent, string name,
            string placeholder, string defaultValue)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = InputWidth;
            le.preferredHeight = InputHeight;

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.13f, 0.13f, 0.18f, 1f);

            // Rounded feel via outline
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0.3f, 0.3f, 0.4f, 0.5f);
            outline.effectDistance = new Vector2(1f, -1f);

            // Text child
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(10f, 4f);
            textRt.offsetMax = new Vector2(-10f, -4f);
            var textComp = textGo.AddComponent<Text>();
            textComp.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            textComp.fontSize = 16;
            textComp.color = Color.white;
            textComp.supportRichText = false;
            textComp.raycastTarget = false;

            // Placeholder child
            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(go.transform, false);
            var phRt = phGo.AddComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(10f, 4f);
            phRt.offsetMax = new Vector2(-10f, -4f);
            var phText = phGo.AddComponent<Text>();
            phText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            phText.fontSize = 16;
            phText.fontStyle = FontStyle.Italic;
            phText.color = new Color(0.45f, 0.45f, 0.55f);
            phText.text = placeholder;
            phText.raycastTarget = false;

            var input = go.AddComponent<InputField>();
            input.targetGraphic = bg;
            input.textComponent = textComp;
            input.placeholder = phText;
            input.text = defaultValue;
            input.interactable = true;

            return input;
        }

        private static Button CreateButton(Transform parent, string name,
            string label, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = ButtonWidth;
            le.preferredHeight = ButtonHeight;

            var img = go.AddComponent<Image>();
            img.color = color;

            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.9f, 0.9f, 0.9f);
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f);
            colors.selectedColor = Color.white;
            btn.colors = colors;
            btn.targetGraphic = img;

            var textGo = new GameObject("Label");
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;
            var txt = textGo.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = 16;
            txt.fontStyle = FontStyle.Bold;
            txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.text = label;
            txt.raycastTarget = false;

            return btn;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_canvas != null) Destroy(_canvas.gameObject);
        }
    }
}
