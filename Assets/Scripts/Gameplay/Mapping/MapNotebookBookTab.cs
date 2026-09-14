using System.Collections.Generic;
using PolymindGames.UserInterface;
using PolymindGames.WieldableSystem;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>
    /// P0.5 de MAPPING-PROTOTYPE — pestaña «Notas» en el libro de supervivencia del vendor, montada en RUNTIME sin
    /// tocar sus assets: la libreta diegética (MAPPING-ROADMAP D22, opción B de §3.9).
    /// </summary>
    /// <remarks>
    /// Verificado en el vendor (MAPPING-PROTOTYPE §5.1): las pestañas son <see cref="SelectableButton"/> hijos directos
    /// del objeto con el <see cref="SelectableGroupBase"/>, y un botón se registra solo en su <c>Awake</c>. No se
    /// clona una pestaña del vendor porque sus feedbacks llevan <c>SetActive</c> sobre SU panel: se construye una con el
    /// mismo tamaño, fondo y letra. El panel propio va junto a <c>BuildingContent</c> y se muestra con
    /// <see cref="SelectableGroupBase.SelectedChanged"/>.
    ///
    /// Con el libro abierto el contexto de input solo admite UI y el propio libro: no se puede andar. Enfundarlo
    /// cancela un dibujo a medias (lo trazado se queda). El libro no usa <c>BR_UIWarp</c> (sólo el reloj), así que
    /// la regla 14 no aplica aquí.
    /// </remarks>
    public sealed class MapNotebookBookTab : MonoBehaviour
    {
        /// <summary>
        /// Joel, 2026-09-14: «por ahora desactivar todas las construcciones». Se OCULTAN las pestañas de construir del
        /// libro; el sistema de construcción y su red no se tocan. A <c>false</c> para volver a verlas.
        /// </summary>
        public const bool HideConstructionTabs = true;
        private static readonly string[] ConstructionTabNames = { "BuildingTab", "FireTab", "ShelterTab", "WorkstationTab", "StorageTab" };

        private const string TemplateTabName = "BuildingTab";
        private const string TemplateContentName = "BuildingContent";
        private const float CrosshairSeconds = 4f;
        private const int MaxSheetButtons = 8;
        private static readonly Color ButtonColour = new Color(0.23f, 0.21f, 0.18f, 0.85f);
        private static readonly Color CrossColour = new Color(0.85f, 0.1f, 0.1f, 1f);

        private MapMemorySampler _sampler;
        private MapNotebook _notebook;
        private uint _paperArgb;

        private WieldableTool _wieldable;
        private SelectableGroupBase _group;
        private SelectableButton _tab;
        private Transform _contentParent;
        private GameObject _panel;
        private RawImage _sheetImage;
        private RectTransform _cross;
        private Image[] _crossBars;
        private TextMeshProUGUI _status;
        private TMP_FontAsset _font;
        private RectTransform _sheetRow;
        private Button _takeButton;
        private Button _drawButton;
        private Button _locateButton;
        private TextMeshProUGUI _takeLabel;
        private readonly List<Button> _sheetButtons = new List<Button>();

        private readonly MapSheetRaster _raster = new MapSheetRaster();
        private Texture2D _texture;
        private Texture2D _leavingTexture;
        private MapPageFlip _flip;
        private bool _restoreAfterFlip;
        private UnityEngine.Audio.AudioResource _flipSound;

        public WieldableTool Wieldable => _wieldable;
        private int _paintedVersion = -1;
        private int _paintedSheet = -1;
        private int _sheetButtonsFor = -1;

        /// <summary>Monta la pestaña en <paramref name="book"/>. Devuelve null (y lo dice en el log) si el libro no tiene la forma esperada.</summary>
        public static MapNotebookBookTab Attach(SurvivalBookUI book, MapMemorySampler sampler, MapNotebook notebook, uint paperArgb,
            UnityEngine.Audio.AudioResource flipSound = null)
        {
            var existing = book.GetComponent<MapNotebookBookTab>();
            if (existing != null) return existing;

            SelectableButton templateTab = FindChild<SelectableButton>(book.transform, TemplateTabName);
            Transform templateContent = FindChild<Transform>(book.transform, TemplateContentName);
            SelectableGroupBase group = templateTab != null && templateTab.transform.parent != null
                ? templateTab.transform.parent.GetComponent<SelectableGroupBase>()
                : null;
            if (templateTab == null || templateContent == null || group == null)
            {
                Debug.LogWarning($"MAPBOOK attach=fail tab={templateTab != null} content={templateContent != null} group={group != null}");
                return null;
            }

            var tab = book.gameObject.AddComponent<MapNotebookBookTab>();
            tab._sampler = sampler;
            tab._notebook = notebook;
            tab._paperArgb = paperArgb;
            tab._flipSound = flipSound;
            tab._wieldable = book.GetComponentInParent<WieldableTool>();
            tab._group = group;
            tab.Build((RectTransform)templateTab.transform, (RectTransform)templateContent);
            Debug.Log($"MAPBOOK attach=ok group={group.name} content={templateContent.parent.name}");
            return tab;
        }

        private static T FindChild<T>(Transform root, string name) where T : Component
        {
            foreach (T component in root.GetComponentsInChildren<T>(true))
                if (component.name == name) return component;
            return null;
        }

        private void Build(RectTransform templateTab, RectTransform templateContent)
        {
            var templateText = templateTab.GetComponentInChildren<TextMeshProUGUI>(true);
            _font = templateText != null ? templateText.font : TMP_Settings.defaultFontAsset;

            // Pestaña: mismo tamaño y fondo que la plantilla; el SelectableButton se añade con el padre ya puesto para
            // que su Awake la registre en el grupo.
            var tabGo = new GameObject("NotesTab", typeof(RectTransform));
            tabGo.layer = templateTab.gameObject.layer;
            var tabRect = (RectTransform)tabGo.transform;
            tabRect.SetParent(templateTab.parent, false);
            CopyRect(templateTab, tabRect);
            tabRect.SetSiblingIndex(templateTab.GetSiblingIndex() + 1);
            var templateImage = templateTab.GetComponent<Image>();
            var tabImage = tabGo.AddComponent<Image>();
            if (templateImage != null)
            {
                tabImage.sprite = templateImage.sprite;
                tabImage.type = templateImage.type;
                tabImage.color = templateImage.color;
            }

            TextMeshProUGUI tabLabel = AddText(tabRect, "Notas", templateText != null ? templateText.fontSize : 12f);
            if (templateText != null)
            {
                tabLabel.color = templateText.color;
                tabLabel.alignment = templateText.alignment;
            }

            _tab = tabGo.AddComponent<SelectableButton>();

            // Panel: hermano de BuildingContent, a toda la página.
            _contentParent = templateContent.parent;
            _panel = new GameObject("NotesContent", typeof(RectTransform));
            _panel.layer = templateContent.gameObject.layer;
            var panel = (RectTransform)_panel.transform;
            panel.SetParent(_contentParent, false);
            CopyRect(templateContent, panel);
            _panel.SetActive(false);

            var sheetArea = NewRect("SheetArea", panel, new Vector2(0.04f, 0.25f), new Vector2(0.96f, 0.98f));
            var sheetGo = new GameObject("Sheet", typeof(RectTransform));
            sheetGo.layer = _panel.layer;
            var sheetRect = (RectTransform)sheetGo.transform;
            sheetRect.SetParent(sheetArea, false);
            sheetRect.anchorMin = Vector2.zero;
            sheetRect.anchorMax = Vector2.one;
            sheetRect.offsetMin = Vector2.zero;
            sheetRect.offsetMax = Vector2.zero;
            _sheetImage = sheetGo.AddComponent<RawImage>();
            _sheetImage.color = Color.white;
            var fitter = sheetGo.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = 1f;

            _cross = NewRect("Crosshair", sheetRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            _cross.sizeDelta = new Vector2(12f, 12f);
            _crossBars = new[]
            {
                Bar(_cross, new Vector2(0f, 0.45f), new Vector2(0.35f, 0.55f)),
                Bar(_cross, new Vector2(0.65f, 0.45f), new Vector2(1f, 0.55f)),
                Bar(_cross, new Vector2(0.45f, 0f), new Vector2(0.55f, 0.35f)),
                Bar(_cross, new Vector2(0.45f, 0.65f), new Vector2(0.55f, 1f)),
            };
            _cross.gameObject.SetActive(false);

            // Página 3D sobre la hoja: pasa al cambiar de hoja.
            byte paperR = (byte)(_paperArgb >> 16), paperG = (byte)(_paperArgb >> 8), paperB = (byte)_paperArgb;
            _flip = MapPageFlip.Create(sheetRect, new Color32(paperR, paperG, paperB, 255));

            _sheetRow = NewRect("Sheets", panel, new Vector2(0.04f, 0.17f), new Vector2(0.96f, 0.24f));
            var sheetLayout = _sheetRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            sheetLayout.spacing = 2f;
            sheetLayout.childForceExpandWidth = false;
            sheetLayout.childControlWidth = false;
            sheetLayout.childControlHeight = true;

            var actions = NewRect("Actions", panel, new Vector2(0.04f, 0.085f), new Vector2(0.96f, 0.16f));
            var actionLayout = actions.gameObject.AddComponent<HorizontalLayoutGroup>();
            actionLayout.spacing = 3f;
            actionLayout.childForceExpandWidth = true;
            actionLayout.childControlWidth = true;
            actionLayout.childControlHeight = true;
            _takeButton = NewButton(actions, "Coger hoja", 7f, OnTake, out _takeLabel);
            _drawButton = NewButton(actions, "Dibujar", 7f, OnDraw, out _);
            _locateButton = NewButton(actions, "Ubicarme", 7f, OnLocate, out _);

            var statusRect = NewRect("Status", panel, new Vector2(0.04f, 0.0f), new Vector2(0.96f, 0.08f));
            _status = AddText(statusRect, "", 6f);
            _status.enableAutoSizing = true;
            _status.fontSizeMin = 4f;
            _status.fontSizeMax = 6f;
            _status.alignment = TextAlignmentOptions.MidlineLeft;
            _status.color = new Color(0.15f, 0.13f, 0.1f, 1f);

            if (HideConstructionTabs)
            {
                foreach (string tabName in ConstructionTabNames)
                {
                    SelectableButton hidden = FindChild<SelectableButton>(templateTab.parent, tabName);
                    if (hidden != null) hidden.gameObject.SetActive(false);
                }
            }

            _group.SelectedChanged += OnSelectedChanged;
            if (_wieldable != null)
            {
                _wieldable.HolsteringStarted += OnHolstering;
                _wieldable.EquippingStarted += OnEquipping;
            }
        }

        private void OnDestroy()
        {
            if (_group != null) _group.SelectedChanged -= OnSelectedChanged;
            if (_wieldable != null)
            {
                _wieldable.HolsteringStarted -= OnHolstering;
                _wieldable.EquippingStarted -= OnEquipping;
            }

            if (_texture != null) Destroy(_texture);
            if (_leavingTexture != null) Destroy(_leavingTexture);
        }

        private void OnSelectedChanged(SelectableButton selected)
        {
            bool mine = selected != null && selected == _tab;
            if (mine)
            {
                for (int i = 0; i < _contentParent.childCount; i++)
                {
                    Transform child = _contentParent.GetChild(i);
                    if (child.gameObject != _panel && child.name.EndsWith("Content")) child.gameObject.SetActive(false);
                }

                _paintedVersion = -1;
            }

            _panel.SetActive(mine);
            if (mine) Debug.Log($"MAPBOOK tab=notes sheets={_notebook.Sheets.Count}");
        }

        private void OnHolstering() => _notebook.Cancel("Has guardado el libro: dibujo cancelado, lo trazado se queda.");

        private void OnEquipping()
        {
            _paintedVersion = -1;
            // SurvivalBookUI se suscribió antes (en su Awake) y ya hizo SelectDefault: sin pestañas de construir, o si se
            // abrió con N, el libro se abre en «Notas».
            if (HideConstructionTabs || _openOnNotes) _group.SelectSelectable(_tab);
            _openOnNotes = false;
        }

        private bool _openOnNotes;

        /// <summary>El próximo abrir del libro cae en «Notas» (tecla N).</summary>
        public void OpenOnNotesNextTime() => _openOnNotes = true;

        private MapHere Here => _sampler != null
            ? new MapHere(_sampler.HasSample, _sampler.LastCellX, _sampler.LastCellZ, _sampler.LastStorey)
            : default;

        private void OnTake()
        {
            bool mapHere = _notebook.OfferMapHere;
            if (_notebook.TakeSheet(_sampler.Memory, Here) && mapHere) OnDraw();
            Debug.Log($"MAPBOOK click=take map_here={mapHere} sheets={_notebook.Sheets.Count}");
        }

        private void OnDraw()
        {
            _notebook.StartDrawing(_sampler.Memory, Time.timeAsDouble, _sampler.memorySeconds / 3.0);
            Debug.Log($"MAPBOOK click=draw pending={_notebook.DrawCount}");
        }

        private void OnLocate()
        {
            MapLocateResult result = _notebook.Locate(_sampler.Memory, Here, Time.timeAsDouble, Time.unscaledTime);
            Debug.Log($"MAPBOOK click=locate result={result}");
        }

        private MapMemory _captureMemory;

        private MapMemory Memory => _captureMemory != null ? _captureMemory : _sampler != null ? _sampler.Memory : null;

        /// <summary>
        /// Herramienta de capturas en editor (sin Play, <c>MappingBookCaptureTool</c>): muestra «Notas» y pinta con
        /// <paramref name="memory"/> en vez del muestreador.
        /// </summary>
        public void RefreshForCapture(MapMemory memory)
        {
            _captureMemory = memory;
            if (!_panel.activeSelf) OnSelectedChanged(_tab);
            Refresh();
        }

        /// <summary>Herramienta de capturas: congela el último paso de página en <paramref name="progress"/> (0..1).</summary>
        public void PoseFlipForCapture(float progress) => _flip.PoseForCapture(progress);

        private void Update()
        {
            MapMemory memory = Memory;
            if (memory == null) return;
            if (_notebook.Drawing) _notebook.Step(memory, Time.deltaTime);
            Refresh();
        }

        private void Refresh()
        {
            if (_restoreAfterFlip && !_flip.Playing)
            {
                _sheetImage.texture = _texture;
                _restoreAfterFlip = false;
            }

            if (!_panel.activeInHierarchy) return;

            bool drawing = _notebook.Drawing;
            _takeButton.interactable = !drawing;
            _drawButton.interactable = !drawing && _notebook.CurrentIndex >= 0;
            _locateButton.interactable = !drawing;
            _takeLabel.text = _notebook.OfferMapHere ? "Mapear aquí" : "Coger hoja";

            if (_sheetButtonsFor != _notebook.Sheets.Count) RebuildSheetButtons();
            if (_notebook.CurrentIndex >= 0 &&
                (_notebook.Version != _paintedVersion || _notebook.CurrentIndex != _paintedSheet))
                Repaint();

            _status.text = $"{_notebook.Status}  ·  tinta {Mathf.Clamp01(_notebook.Pen.Ink) * 100f:F0} %";
            _sheetImage.enabled = _notebook.CurrentIndex >= 0;
            UpdateCrosshair();
        }

        private void Repaint()
        {
            bool turning = _texture != null && _paintedSheet >= 0 && _paintedSheet != _notebook.CurrentIndex;
            bool forward = _notebook.CurrentIndex > _paintedSheet;
            _paintedVersion = _notebook.Version;
            _paintedSheet = _notebook.CurrentIndex;
            if (_texture == null)
            {
                _texture = NewSheetTexture();
                _sheetImage.texture = _texture;
            }

            if (turning)
            {
                // La hoja vieja, copiada ANTES de pintar la nueva en _texture.
                if (_leavingTexture == null) _leavingTexture = NewSheetTexture();
                Graphics.CopyTexture(_texture, _leavingTexture);
                if (forward)
                {
                    // Avanzar: la vieja se levanta y se va a la izquierda; debajo ya está la nueva.
                    _flip.Play(_leavingTexture, true);
                }
                else
                {
                    // Volver: la nueva llega desde la izquierda; debajo sigue la vieja hasta que se posa.
                    _sheetImage.texture = _leavingTexture;
                    _flip.Play(_texture, false);
                    _restoreAfterFlip = true;
                }

                if (Application.isPlaying && _flipSound != null && PolymindGames.AudioManager.Instance != null)
                    PolymindGames.AudioManager.Instance.PlayClip2D(_flipSound);
                Debug.Log($"MAPBOOK flip to_sheet={_notebook.CurrentSheet.Id} forward={forward}");
            }

            _raster.DrawSheet(_notebook.CurrentSheet, _paperArgb, Memory.CellsPerChunk);
            _texture.LoadRawTextureData(_raster.Rgba);
            _texture.Apply(false);

            for (int i = 0; i < _sheetButtons.Count; i++)
                _sheetButtons[i].image.color = SheetIndexOf(i) == _notebook.CurrentIndex
                    ? new Color(0.55f, 0.16f, 0.12f, 0.9f)
                    : ButtonColour;
        }

        private Texture2D NewSheetTexture() =>
            new Texture2D(_raster.Size, _raster.Size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

        private int SheetIndexOf(int button) => Mathf.Max(0, _notebook.Sheets.Count - MaxSheetButtons) + button;

        private void RebuildSheetButtons()
        {
            foreach (Button button in _sheetButtons)
            {
                if (Application.isPlaying) Destroy(button.gameObject);
                else DestroyImmediate(button.gameObject);
            }
            _sheetButtons.Clear();
            _sheetButtonsFor = _notebook.Sheets.Count;

            int first = Mathf.Max(0, _notebook.Sheets.Count - MaxSheetButtons);
            for (int i = first; i < _notebook.Sheets.Count; i++)
            {
                int index = i;
                Button button = NewButton(_sheetRow, _notebook.Sheets[i].Id.ToString(), 6f, () => _notebook.Select(index), out _);
                ((RectTransform)button.transform).sizeDelta = new Vector2(14f, 0f);
                _sheetButtons.Add(button);
            }

            _paintedVersion = -1;
        }

        private void UpdateCrosshair()
        {
            float left = _notebook.FixTime + CrosshairSeconds - Time.unscaledTime;
            bool show = _notebook.HasFix && _notebook.FixSheetIndex == _notebook.CurrentIndex && left > 0f;
            _cross.gameObject.SetActive(show);
            if (!show) return;

            float elapsed = CrosshairSeconds - left;
            float alpha = Mathf.Clamp01(left);
            if (elapsed < 1f && Mathf.Repeat(elapsed * 4f, 1f) > 0.6f) alpha *= 0.25f;
            foreach (Image bar in _crossBars) bar.color = new Color(CrossColour.r, CrossColour.g, CrossColour.b, alpha);

            int cellsPerChunk = Memory.CellsPerChunk;
            var anchor = new Vector2(_notebook.FixLocalX / cellsPerChunk, _notebook.FixLocalZ / cellsPerChunk);
            _cross.anchorMin = anchor;
            _cross.anchorMax = anchor;
            _cross.anchoredPosition = Vector2.zero;
        }

        private static void CopyRect(RectTransform from, RectTransform to)
        {
            to.anchorMin = from.anchorMin;
            to.anchorMax = from.anchorMax;
            to.pivot = from.pivot;
            to.sizeDelta = from.sizeDelta;
            to.anchoredPosition = from.anchoredPosition;
            to.localRotation = from.localRotation;
            to.localScale = from.localScale;
        }

        private RectTransform NewRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        private Image Bar(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            RectTransform rect = NewRect("Bar", parent, anchorMin, anchorMax);
            var image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        private TextMeshProUGUI AddText(RectTransform parent, string text, float size)
        {
            RectTransform rect = NewRect("Text", parent, Vector2.zero, Vector2.one);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.font = _font;
            label.fontSize = size;
            label.text = text;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            return label;
        }

        private Button NewButton(RectTransform parent, string text, float size, UnityEngine.Events.UnityAction onClick,
            out TextMeshProUGUI label)
        {
            RectTransform rect = NewRect(text, parent, Vector2.zero, Vector2.one);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = ButtonColour;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);
            label = AddText(rect, text, size);
            label.color = new Color(0.95f, 0.93f, 0.88f, 1f);
            return button;
        }
    }
}
