using BackroomsSurvival.Net;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    public sealed class JoinSessionUI : MonoBehaviour
    {
        public enum PanelState { Visible, Connecting, Connected, Error }

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

        private PanelState _state = PanelState.Visible;
        public PanelState State => _state;

        public string ServerIP => _ipField != null ? _ipField.text : "127.0.0.1";
        public string Port => _portField != null ? _portField.text : "7778";
        public string PlayerName => _nameField != null ? _nameField.text : "Player";

        private const float InputWidth = 300f;
        private const float InputHeight = 40f;
        private const float ButtonWidth = 120f;
        private const float ButtonHeight = 40f;
        private const float Spacing = 10f;
        private const float PanelPadding = 24f;

        private void Start()
        {
            BuildUI();
            SetState(PanelState.Visible);
        }

        private void Update()
        {
            if (_state != PanelState.Connecting) return;

            var init = NetworkInitializer.Instance;
            if (init == null) return;

            _statusText.text = init.StatusMessage;

            if (init.IsBackendReady)
                SetState(PanelState.Connected);
            else if (init.StatusMessage.StartsWith("Error") || init.StatusMessage.StartsWith("Timeout"))
                SetState(PanelState.Error);
        }

        private void OnHostClicked()
        {
            var init = EnsureInitializer();
            string playerName = string.IsNullOrWhiteSpace(_nameField.text) ? "Host" : _nameField.text;
            init.StartAsHost(playerName);
            SetState(PanelState.Connecting);
        }

        private void OnJoinClicked()
        {
            var init = EnsureInitializer();
            string ip = string.IsNullOrWhiteSpace(_ipField.text) ? "127.0.0.1" : _ipField.text;
            int port = 7778;
            if (!string.IsNullOrWhiteSpace(_portField.text))
                int.TryParse(_portField.text, out port);
            string playerName = string.IsNullOrWhiteSpace(_nameField.text) ? "Player" : _nameField.text;

            init.StartAsJoiner(ip, port, playerName);
            SetState(PanelState.Connecting);
        }

        private void OnDisconnectClicked()
        {
            var init = NetworkInitializer.Instance;
            if (init != null) init.Shutdown();
            SetState(PanelState.Visible);
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

        private void SetState(PanelState state)
        {
            _state = state;

            bool showInputs = state == PanelState.Visible || state == PanelState.Error;
            _ipField.gameObject.SetActive(showInputs);
            _portField.gameObject.SetActive(showInputs);
            _nameField.gameObject.SetActive(showInputs);
            _hostButton.gameObject.SetActive(showInputs);
            _joinButton.gameObject.SetActive(showInputs);
            _disconnectButton.gameObject.SetActive(state == PanelState.Connected || state == PanelState.Connecting);

            switch (state)
            {
                case PanelState.Visible:
                    _titleText.text = "BACKROOMS SURVIVAL";
                    _statusText.text = "";
                    _statusText.color = Color.white;
                    _panelCanvasGroup.alpha = 1f;
                    _panelCanvasGroup.interactable = true;
                    _panelCanvasGroup.blocksRaycasts = true;
                    _panel.SetActive(true);
                    SetCursorFree(true);
                    break;
                case PanelState.Connecting:
                    _statusText.text = "Connecting...";
                    _statusText.color = new Color(1f, 0.85f, 0.3f);
                    break;
                case PanelState.Connected:
                    _statusText.text = "Connected!";
                    _statusText.color = new Color(0.4f, 1f, 0.4f);
                    _panelCanvasGroup.interactable = false;
                    _panelCanvasGroup.blocksRaycasts = false;
                    _panel.SetActive(false);
                    SetCursorFree(false);
                    break;
                case PanelState.Error:
                    var init = NetworkInitializer.Instance;
                    _statusText.text = init != null ? init.StatusMessage : "Connection failed";
                    _statusText.color = new Color(1f, 0.35f, 0.35f);
                    break;
            }
        }

        private static void SetCursorFree(bool free)
        {
            Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = free;
        }

        // ─── UI Construction (VerticalLayoutGroup, fixed sizes) ───

        private void BuildUI()
        {
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
            _hostButton.onClick.AddListener(OnHostClicked);

            _joinButton = CreateButton(buttonRow.transform, "JoinBtn", "Join",
                new Color(0.20f, 0.40f, 0.70f));
            _joinButton.onClick.AddListener(OnJoinClicked);

            // Disconnect button (hidden initially)
            _disconnectButton = CreateButton(_panel.transform, "DisconnectBtn", "Disconnect",
                new Color(0.60f, 0.20f, 0.20f));
            _disconnectButton.onClick.AddListener(OnDisconnectClicked);
            _disconnectButton.gameObject.SetActive(false);
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

            var input = go.AddComponent<InputField>();
            input.textComponent = textComp;
            input.placeholder = phText;
            input.text = defaultValue;

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
            if (_canvas != null) Destroy(_canvas.gameObject);
        }
    }
}
