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
    // Montaje de ESTA pantalla: jerarquia, layout y cableado de callbacks.
    // Deliberadamente NO incluye ApplySelectedLocalConfigToUi ni ParseHostPortFromUi,
    // que estan fisicamente en medio de este bloque pero son camino de RED (los llaman
    // nueve sitios del flujo host/join), no construccion de UI.
    public sealed partial class JoinSessionUI
    {
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
            _ipField = CreateInputField(_panel.transform, "IPField", "Host IP / Join IP", "127.0.0.1");
            _portField = CreateInputField(_panel.transform, "PortField", "Host Port / Join Port", "7778");
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

            // Steam invite button — camino ADITIVO, fila propia bajo Host/Join. Solo
            // aparece si SteamClient.Init tuvo éxito; sin Steam el panel queda idéntico
            // al de siempre.
            _steamInviteButton = CreateButton(_panel.transform, "SteamInviteBtn", "Invite via Steam",
                new Color(0.10f, 0.36f, 0.46f));
            var steamLayout = _steamInviteButton.GetComponent<LayoutElement>();
            if (steamLayout != null) steamLayout.preferredWidth = InputWidth;
            RegisterButtonCallbacks(_steamInviteButton);
            _steamInviteButton.onClick.AddListener(OnSteamInviteClicked);
            _steamInviteButton.gameObject.SetActive(SteamLobbyManager.IsAvailable);

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
    }
}
