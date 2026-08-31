using System.Collections.Generic;
using BackroomsSurvival.Lobbies;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    // Montaje de la pantalla del navegador de servidores: jerarquía, layout y cableado de
    // callbacks. Nada de aquí decide nada — todo lo que se puede equivocar está en
    // ServerBrowserViewModel, que se prueba sin Unity.
    //
    // El panel es RESPONSIVE por anclas (ocupa una fracción de la pantalla, no un tamaño fijo en
    // píxeles) y las filas se REUTILIZAN: refrescar no destruye ni crea GameObjects mientras el
    // número de servidores no cambie.
    public sealed partial class ServerBrowserUI
    {
        private sealed class Row
        {
            public GameObject Go;
            public Image Background;
            public Button Button;
            public Text Name;
            public Text Players;
            public Text Ping;
            public Text Map;
            public Text Region;
            public Text Version;
            public Text Status;
            public LobbyId Id;
        }

        private const float RowHeight = 28f;
        private const float ToolbarHeight = 34f;
        private const float HeaderHeight = 40f;
        private const float FooterHeight = 46f;

        private static readonly Color PanelColor = new Color(0.06f, 0.06f, 0.10f, 0.94f);
        private static readonly Color RowColor = new Color(0.11f, 0.11f, 0.16f, 1f);
        private static readonly Color RowAltColor = new Color(0.14f, 0.14f, 0.19f, 1f);
        private static readonly Color RowSelectedColor = new Color(0.16f, 0.32f, 0.46f, 1f);
        private static readonly Color RowBlockedText = new Color(0.62f, 0.45f, 0.45f, 1f);
        private static readonly Color NormalText = new Color(0.88f, 0.88f, 0.92f, 1f);
        private static readonly Color DimText = new Color(0.6f, 0.6f, 0.68f, 1f);
        private static readonly Color ButtonColor = new Color(0.20f, 0.24f, 0.34f, 1f);
        private static readonly Color AccentColor = new Color(0.20f, 0.46f, 0.30f, 1f);

        /// Topes de ping del botón cíclico. `AnyPing` primero: el filtro arranca sin filtrar.
        private static readonly int[] PingSteps = { LobbyFilter.AnyPing, 60, 120, 250 };

        private static readonly LobbySortKey[] SortKeys =
        {
            LobbySortKey.Ping, LobbySortKey.Players, LobbySortKey.Name,
            LobbySortKey.Map, LobbySortKey.Region, LobbySortKey.Version,
        };

        private readonly List<Row> _rows = new List<Row>();

        private Canvas _canvas;
        private GameObject _panel;
        private RectTransform _listContent;
        private Text _statusText;
        private Text _selectionText;
        private Text _hideFullLabel;
        private Text _hidePasswordLabel;
        private Text _compatibleLabel;
        private Text _pingLabel;
        private Text _sortLabel;
        private Button _joinButton;
        private Button _backButton;
        private Button _refreshButton;
        private Text _joinLabel;
        private InputField _searchField;
        private bool _built;
        private int _pingStep;
        private int _sortIndex;

        private void BuildUI()
        {
            if (_built) return;
            _built = true;

            _canvas = new GameObject("ServerBrowserCanvas").AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Por debajo de JoinSessionUI (200): cuando un join arranca, el panel de conexión
            // tiene que quedar ENCIMA de la lista, no debajo.
            _canvas.sortingOrder = 190;

            var scaler = _canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            _canvas.gameObject.AddComponent<GraphicRaycaster>();

            _panel = new GameObject("Panel");
            _panel.transform.SetParent(_canvas.transform, false);
            var panelRt = _panel.AddComponent<RectTransform>();
            // Anclas relativas = responsive de verdad: el panel ocupa la misma FRACCIÓN de la
            // pantalla a 1280x720 y a 3840x2160.
            panelRt.anchorMin = new Vector2(0.07f, 0.10f);
            panelRt.anchorMax = new Vector2(0.93f, 0.90f);
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            var panelImg = _panel.AddComponent<Image>();
            panelImg.color = PanelColor;

            var vlg = _panel.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(16, 16, 14, 14);
            vlg.spacing = 8f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            BuildHeader(_panel.transform);
            BuildToolbar(_panel.transform);
            BuildColumnHeader(_panel.transform);
            BuildList(_panel.transform);
            BuildFooter(_panel.transform);
        }

        private void BuildHeader(Transform parent)
        {
            GameObject row = CreateRowContainer(parent, "Header", HeaderHeight, 12f);

            Text title = CreateText(row.transform, "Title", "Server Browser", 26, FontStyle.Bold,
                NormalText, TextAnchor.MiddleLeft);
            title.gameObject.GetComponent<LayoutElement>().flexibleWidth = 1f;

            CreateText(row.transform, "Version", "cliente v" + _clientVersion, 14, FontStyle.Normal,
                DimText, TextAnchor.MiddleRight).gameObject.GetComponent<LayoutElement>()
                .preferredWidth = 220f;

            // "Volver" y no "Cerrar": el navegador se abre DESDE el panel de multijugador y tiene
            // que devolverlo, no dejar la pantalla en negro.
            CreateButton(row.transform, "Back", "Volver", ButtonColor, 90f, 30f, RequestBack);
        }

        private void BuildToolbar(Transform parent)
        {
            GameObject row = CreateRowContainer(parent, "Toolbar", ToolbarHeight, 8f);

            _refreshButton = CreateButton(row.transform, "Refresh", "Refrescar", ButtonColor, 110f, 28f,
                RequestRefresh);

            _searchField = CreateInput(row.transform, "Search", "Buscar por nombre o mapa…", 260f, 28f);
            _searchField.onValueChanged.AddListener(value =>
            {
                _viewModel.Filter.SearchText = value;
                _viewModel.ApplyFilterAndSort();
            });

            _hideFullLabel = CreateToggleButton(row.transform, "HideFull", 150f, () =>
            {
                _viewModel.Filter.HideFull = !_viewModel.Filter.HideFull;
                _viewModel.ApplyFilterAndSort();
                RefreshToolbarLabels();
            });

            _hidePasswordLabel = CreateToggleButton(row.transform, "HidePassword", 190f, () =>
            {
                _viewModel.Filter.HidePasswordProtected = !_viewModel.Filter.HidePasswordProtected;
                _viewModel.ApplyFilterAndSort();
                RefreshToolbarLabels();
            });

            _compatibleLabel = CreateToggleButton(row.transform, "CompatibleOnly", 190f, () =>
            {
                _viewModel.Filter.HideIncompatibleVersion = !_viewModel.Filter.HideIncompatibleVersion;
                _viewModel.ApplyFilterAndSort();
                RefreshToolbarLabels();
            });

            _pingLabel = CreateToggleButton(row.transform, "MaxPing", 150f, () =>
            {
                _pingStep = (_pingStep + 1) % PingSteps.Length;
                _viewModel.Filter.MaxPingMs = PingSteps[_pingStep];
                _viewModel.ApplyFilterAndSort();
                RefreshToolbarLabels();
            });

            _sortLabel = CreateToggleButton(row.transform, "Sort", 210f, () =>
            {
                // Un solo botón cicla clave y dirección: asc, desc, siguiente clave. Con seis
                // claves, dos botones separados serían dos sitios donde perder el estado.
                if (!_viewModel.Sort.Descending)
                {
                    _viewModel.Sort.Descending = true;
                }
                else
                {
                    _viewModel.Sort.Descending = false;
                    _sortIndex = (_sortIndex + 1) % SortKeys.Length;
                    _viewModel.Sort.Key = SortKeys[_sortIndex];
                }

                _viewModel.ApplyFilterAndSort();
                RefreshToolbarLabels();
            });

            RefreshToolbarLabels();
        }

        private void BuildColumnHeader(Transform parent)
        {
            GameObject row = CreateRowContainer(parent, "Columns", 22f, 6f);
            CreateCell(row.transform, "Name", "SERVIDOR", 0f, 1f, TextAnchor.MiddleLeft, DimText);
            CreateCell(row.transform, "Players", "JUGADORES", 100f, 0f, TextAnchor.MiddleCenter, DimText);
            CreateCell(row.transform, "Ping", "PING", 70f, 0f, TextAnchor.MiddleCenter, DimText);
            CreateCell(row.transform, "Map", "MAPA", 130f, 0f, TextAnchor.MiddleLeft, DimText);
            CreateCell(row.transform, "Region", "REGIÓN", 110f, 0f, TextAnchor.MiddleLeft, DimText);
            CreateCell(row.transform, "Version", "VERSIÓN", 110f, 0f, TextAnchor.MiddleLeft, DimText);
            CreateCell(row.transform, "Status", "ESTADO", 150f, 0f, TextAnchor.MiddleLeft, DimText);
        }

        private void BuildList(Transform parent)
        {
            var viewportGo = new GameObject("List");
            viewportGo.transform.SetParent(parent, false);
            var le = viewportGo.AddComponent<LayoutElement>();
            le.flexibleHeight = 1f;
            le.preferredHeight = 300f;

            var bg = viewportGo.AddComponent<Image>();
            bg.color = new Color(0.03f, 0.03f, 0.06f, 0.9f);

            var scroll = viewportGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            var maskGo = new GameObject("Viewport");
            maskGo.transform.SetParent(viewportGo.transform, false);
            var maskRt = maskGo.AddComponent<RectTransform>();
            maskRt.anchorMin = Vector2.zero;
            maskRt.anchorMax = Vector2.one;
            maskRt.offsetMin = new Vector2(4f, 4f);
            maskRt.offsetMax = new Vector2(-4f, -4f);
            maskGo.AddComponent<RectMask2D>();

            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(maskGo.transform, false);
            _listContent = contentGo.AddComponent<RectTransform>();
            _listContent.anchorMin = new Vector2(0f, 1f);
            _listContent.anchorMax = new Vector2(1f, 1f);
            _listContent.pivot = new Vector2(0.5f, 1f);

            var contentVlg = contentGo.AddComponent<VerticalLayoutGroup>();
            contentVlg.spacing = 2f;
            contentVlg.childControlWidth = true;
            contentVlg.childControlHeight = true;
            contentVlg.childForceExpandWidth = true;
            contentVlg.childForceExpandHeight = false;

            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = maskRt;
            scroll.content = _listContent;
        }

        private void BuildFooter(Transform parent)
        {
            GameObject row = CreateRowContainer(parent, "Footer", FooterHeight, 12f);

            _statusText = CreateText(row.transform, "Status", "", 16, FontStyle.Normal,
                NormalText, TextAnchor.MiddleLeft);
            _statusText.gameObject.GetComponent<LayoutElement>().flexibleWidth = 1f;

            _selectionText = CreateText(row.transform, "Selection", "", 14, FontStyle.Italic,
                DimText, TextAnchor.MiddleRight);
            _selectionText.gameObject.GetComponent<LayoutElement>().preferredWidth = 420f;

            _backButton = CreateButton(row.transform, "BackFooter", "Volver al menú", ButtonColor,
                150f, 34f, RequestBack);

            _joinButton = CreateButton(row.transform, "Join", "Entrar", AccentColor, 130f, 34f,
                RequestJoinSelected);
            _joinLabel = _joinButton.GetComponentInChildren<Text>();
        }

        /// <summary>
        /// Repinta la tabla. Reutiliza filas: sólo crea GameObjects cuando hacen falta MÁS de las
        /// que ya existen, y las sobrantes se apagan. Un refresh que no cambia el número de
        /// servidores no asigna memoria de UI.
        /// </summary>
        private void RebuildRows()
        {
            if (_listContent == null || _viewModel == null) return;

            IReadOnlyList<Lobby> visible = _viewModel.Visible;
            double now = Time.unscaledTimeAsDouble;

            while (_rows.Count < visible.Count) _rows.Add(CreateRow(_listContent));

            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                if (i >= visible.Count)
                {
                    if (row.Go.activeSelf) row.Go.SetActive(false);
                    continue;
                }

                Lobby lobby = visible[i];
                LobbyJoinability verdict = lobby.EvaluateJoinability(_clientVersion, now);
                bool selected = lobby.Id == _viewModel.SelectedId;
                Color textColor = verdict == LobbyJoinability.Joinable ? NormalText : RowBlockedText;

                row.Id = lobby.Id;
                if (!row.Go.activeSelf) row.Go.SetActive(true);
                row.Background.color = selected ? RowSelectedColor : (i % 2 == 0 ? RowColor : RowAltColor);

                row.Name.text = (lobby.RequiresPassword ? "[PW] " : "") + lobby.Name;
                row.Players.text = lobby.Players + "/" + lobby.MaxPlayers;
                row.Ping.text = lobby.HasPing ? lobby.PingMs + " ms" : "—";
                row.Map.text = lobby.Map;
                row.Region.text = lobby.Region;
                row.Version.text = lobby.Version;
                row.Status.text = verdict == LobbyJoinability.Joinable
                    ? DescribeStatus(lobby)
                    : LobbyJoinRouter.Explain(verdict);

                row.Name.color = textColor;
                row.Players.color = lobby.IsFull ? RowBlockedText : textColor;
                row.Ping.color = PingColor(lobby, textColor);
                row.Map.color = textColor;
                row.Region.color = textColor;
                row.Version.color = lobby.IsCompatibleWith(_clientVersion) ? textColor : RowBlockedText;
                row.Status.color = verdict == LobbyJoinability.Joinable ? DimText : RowBlockedText;
            }

            if (_statusText != null) _statusText.text = ComposeStatusLine();

            Lobby sel = _viewModel.Selected;
            if (_selectionText != null)
            {
                _selectionText.text = sel == null
                    ? "Ningún servidor seleccionado"
                    : sel.Name + " — " + sel.Endpoint;
            }

            bool joining = _viewModel.State == ServerBrowserState.Joining;
            bool loading = _viewModel.State == ServerBrowserState.Loading;
            bool lifecycleIdle = Net.SessionState.Current.CanStart;

            if (_refreshButton != null) _refreshButton.interactable = !joining;
            if (_backButton != null) _backButton.interactable = !joining;

            if (_joinButton != null)
            {
                // Tres condiciones y las tres tienen que verse en el botón: que el lobby admita
                // entrar, que no haya ya un intento en marcha, y que el CICLO DE SESIÓN lo
                // permita. Sin la tercera, el botón invita a pulsar algo que el gate de fase va a
                // rechazar en silencio.
                bool canJoin = !joining && !loading && lifecycleIdle && sel != null &&
                               sel.EvaluateJoinability(_clientVersion, now) == LobbyJoinability.Joinable;
                _joinButton.interactable = canJoin;
                if (_joinLabel != null) _joinLabel.text = joining ? "Conectando…" : "Entrar";
            }
        }

        private static string DescribeStatus(Lobby lobby)
        {
            switch (lobby.Status)
            {
                case LobbyStatus.Waiting: return "En el lobby";
                case LobbyStatus.InProgress: return "En partida";
                case LobbyStatus.Closed: return "Cerrada";
                default: return "—";
            }
        }

        private static Color PingColor(Lobby lobby, Color fallback)
        {
            if (!lobby.HasPing) return DimText;
            if (lobby.PingMs < 80) return new Color(0.45f, 0.80f, 0.50f);
            if (lobby.PingMs < 180) return new Color(0.85f, 0.78f, 0.40f);
            return new Color(0.85f, 0.45f, 0.40f);
        }

        private void RefreshToolbarLabels()
        {
            if (_viewModel == null) return;

            if (_hideFullLabel != null)
                _hideFullLabel.text = Check(_viewModel.Filter.HideFull) + " Ocultar llenos";
            if (_hidePasswordLabel != null)
                _hidePasswordLabel.text = Check(_viewModel.Filter.HidePasswordProtected) + " Ocultar con clave";
            if (_compatibleLabel != null)
                _compatibleLabel.text = Check(_viewModel.Filter.HideIncompatibleVersion) + " Sólo compatibles";
            if (_pingLabel != null)
            {
                _pingLabel.text = _viewModel.Filter.MaxPingMs == LobbyFilter.AnyPing
                    ? "Ping: cualquiera"
                    : "Ping < " + _viewModel.Filter.MaxPingMs + " ms";
            }

            if (_sortLabel != null)
                _sortLabel.text = "Orden: " + _viewModel.Sort.Key + (_viewModel.Sort.Descending ? " ↓" : " ↑");
        }

        private static string Check(bool value) => value ? "[x]" : "[ ]";

        // ─── Fábricas uGUI. Genéricas: no saben nada de lobbies. ───

        private static GameObject CreateRowContainer(Transform parent, string name, float height, float spacing)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;

            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = spacing;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            return go;
        }

        private static Text CreateText(Transform parent, string name, string content, int fontSize,
            FontStyle style, Color color, TextAnchor anchor, bool withLayoutElement = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            // La etiqueta de un botón se estira por anclas, no por layout: darle un LayoutElement
            // la metería en el HorizontalLayoutGroup del botón y se colocaría dos veces.
            if (withLayoutElement) go.AddComponent<LayoutElement>();

            var txt = go.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.color = color;
            txt.alignment = anchor;
            txt.text = content;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Truncate;
            txt.raycastTarget = false;
            return txt;
        }

        private static Text CreateCell(Transform parent, string name, string content,
            float preferredWidth, float flexibleWidth, TextAnchor anchor, Color color)
        {
            Text txt = CreateText(parent, name, content, 15, FontStyle.Normal, color, anchor);
            var le = txt.gameObject.GetComponent<LayoutElement>();
            le.preferredWidth = preferredWidth;
            le.flexibleWidth = flexibleWidth;
            le.minWidth = preferredWidth > 0f ? preferredWidth : 120f;
            return txt;
        }

        private static Button CreateButton(Transform parent, string name, string label, Color color,
            float width, float height, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minWidth = width;
            le.preferredHeight = height;

            var img = go.AddComponent<Image>();
            img.color = color;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            if (onClick != null) btn.onClick.AddListener(onClick);

            Text txt = CreateText(go.transform, "Label", label, 15, FontStyle.Bold, NormalText,
                TextAnchor.MiddleCenter, withLayoutElement: false);
            var txtRt = txt.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = Vector2.zero;
            txtRt.offsetMax = Vector2.zero;
            return btn;
        }

        private Text CreateToggleButton(Transform parent, string name, float width,
            UnityEngine.Events.UnityAction onClick)
        {
            Button btn = CreateButton(parent, name, "", ButtonColor, width, 28f, onClick);
            return btn.GetComponentInChildren<Text>();
        }

        private static InputField CreateInput(Transform parent, string name, string placeholder,
            float width, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minWidth = 120f;
            le.flexibleWidth = 1f;
            le.preferredHeight = height;

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.13f, 0.13f, 0.18f, 1f);

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(8f, 3f);
            textRt.offsetMax = new Vector2(-8f, -3f);
            var textComp = textGo.AddComponent<Text>();
            textComp.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            textComp.fontSize = 15;
            textComp.color = NormalText;
            textComp.supportRichText = false;

            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(go.transform, false);
            var phRt = phGo.AddComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(8f, 3f);
            phRt.offsetMax = new Vector2(-8f, -3f);
            var phText = phGo.AddComponent<Text>();
            phText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            phText.fontSize = 15;
            phText.fontStyle = FontStyle.Italic;
            phText.color = DimText;
            phText.text = placeholder;
            phText.raycastTarget = false;

            var input = go.AddComponent<InputField>();
            input.targetGraphic = bg;
            input.textComponent = textComp;
            input.placeholder = phText;
            return input;
        }

        private Row CreateRow(Transform parent)
        {
            GameObject go = CreateRowContainer(parent, "Row", RowHeight, 6f);

            var bg = go.AddComponent<Image>();
            bg.color = RowColor;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            // La transición es NONE a propósito: el color de la fila lo decide RebuildRows
            // (seleccionada / bloqueada / cebra), y el Button lo pisaría en cada hover.
            btn.transition = Selectable.Transition.None;

            var row = new Row
            {
                Go = go,
                Background = bg,
                Button = btn,
                Name = CreateCell(go.transform, "Name", "", 0f, 1f, TextAnchor.MiddleLeft, NormalText),
                Players = CreateCell(go.transform, "Players", "", 100f, 0f, TextAnchor.MiddleCenter, NormalText),
                Ping = CreateCell(go.transform, "Ping", "", 70f, 0f, TextAnchor.MiddleCenter, NormalText),
                Map = CreateCell(go.transform, "Map", "", 130f, 0f, TextAnchor.MiddleLeft, NormalText),
                Region = CreateCell(go.transform, "Region", "", 110f, 0f, TextAnchor.MiddleLeft, NormalText),
                Version = CreateCell(go.transform, "Version", "", 110f, 0f, TextAnchor.MiddleLeft, NormalText),
                Status = CreateCell(go.transform, "Status", "", 150f, 0f, TextAnchor.MiddleLeft, DimText),
            };

            // El id se lee de la fila EN EL CLIC, no se captura: la fila se reutiliza para otro
            // servidor en el siguiente refresco y un id capturado dejaría el clic apuntando al
            // lobby que ocupaba esa posición hace dos listas.
            btn.onClick.AddListener(() =>
            {
                if (_viewModel != null && _viewModel.Select(row.Id)) MarkRowsDirty();
            });

            return row;
        }
    }
}
