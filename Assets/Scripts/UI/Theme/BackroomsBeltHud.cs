using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// El cinturón en juego (Joel, 2026-09-13 noche): con TAB abierto va en su caja con rótulo y huecos de inventario;
    /// al cerrar se compacta en una transición corta a huecos más grandes, sin rótulo, y la caja se ajusta a lo que
    /// ocupan. Es la misma pieza (<c>HotbarUI</c>) en los dos estados.
    ///
    /// Joel (2026-09-14): en juego ocupaba demasiado. Sin TAB se ven SOLO los huecos (la cincha de fondo se funde con la
    /// transición) y el cinturón aparece al cambiar de objeto, al equipar o desequipar y al cerrar TAB, se queda
    /// <see cref="_holdSeconds"/> y se desvanece. El vendor queda en «siempre visible» (<c>_holsterVisibleDuration</c> 0): la
    /// visibilidad en juego la lleva este fundido, y su panel sigue escondiéndolo en menús.
    /// </summary>
    public sealed class BackroomsBeltHud : CharacterUIBehaviour
    {
        [SerializeField] private RectTransform _box;
        [SerializeField] private HorizontalLayoutGroup _layout;
        [SerializeField] private CanvasGroup _title;
        [SerializeField] private RectTransform _selectionFrame;
        [SerializeField] private float _inventoryCell = 72f;
        [SerializeField] private float _gameCell = 88f;
        [SerializeField] private float _inventoryTop = 44f;
        [SerializeField] private float _gameTop = 16f;
        [SerializeField] private float _pad = 16f;
        [SerializeField] private float _gap = 8f;
        [SerializeField] private float _duration = 0.18f;
        [SerializeField] private Graphic _strap;
        [SerializeField] private CanvasGroup _slotsGroup;
        [SerializeField] private CanvasGroup _frameGroup;
        [SerializeField, Range(0f, 10f)] private float _holdSeconds = 2f;
        [SerializeField, Range(0f, 1f)] private float _gameAlpha = 0.85f;
        [SerializeField, Range(1f, 30f)] private float _fadeSharpness = 6f;

        private IInventoryInspectionManagerCC _inspection;
        private float _t;
        private float _target;
        private int _lastCount = -1;
        private float _boxWidth;
        private bool _easeWidth;
        private IWieldableInventoryCC _wieldables;
        private IItemContainer _holster;
        private float _visibleUntil = -1f;
        private float _alpha;
        private float _strapAlpha = -1f;

        protected override void OnCharacterAttached(ICharacter character)
        {
            _wieldables = character.GetCC<IWieldableInventoryCC>();
            if (_wieldables != null) _wieldables.SelectedIndexChanged += OnSelectedIndexChanged;
            _holster = character.Inventory?.FindContainer(ItemContainerFilters.WithName("Holster"));
            if (_holster != null) _holster.SlotChanged += OnHolsterChanged;

            _inspection = character.GetCC<IInventoryInspectionManagerCC>();
            if (_inspection == null) return;
            _inspection.InspectionStarted += Open;
            _inspection.InspectionEnded += Close;
            _t = _target = _inspection.IsInspecting ? 1f : 0f;
            _alpha = _target;
            ApplyAlpha();
            Apply();
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            if (_wieldables != null) _wieldables.SelectedIndexChanged -= OnSelectedIndexChanged;
            if (_holster != null) _holster.SlotChanged -= OnHolsterChanged;
            _wieldables = null;
            _holster = null;
            if (_inspection == null) return;
            _inspection.InspectionStarted -= Open;
            _inspection.InspectionEnded -= Close;
            _inspection = null;
        }

        private void Open() => _target = 1f;

        private void Close()
        {
            _target = 0f;
            Poke();
        }

        private void OnSelectedIndexChanged(int index) => Poke();

        private void OnHolsterChanged(in SlotReference slot, SlotChangeType changeType) => Poke();

        /// <summary>Algo pasó en el cinturón: se ve ya y se queda un rato antes de desvanecerse.</summary>
        private void Poke() => _visibleUntil = Time.unscaledTime + _holdSeconds;

        /// <summary>Opacidad a la que va el cinturón. Pura, con test.</summary>
        public static float FadeTarget(bool inventoryOpen, float now, float visibleUntil, float gameAlpha)
            => inventoryOpen ? 1f : now < visibleUntil ? gameAlpha : 0f;

        private void ApplyAlpha()
        {
            if (_slotsGroup != null) _slotsGroup.alpha = _alpha;
            if (_frameGroup != null) _frameGroup.alpha = _alpha;
        }

        private void Update()
        {
            if (_layout == null) return;
            bool open = _target > 0f || _t > 0.001f;
            float wanted = FadeTarget(open, Time.unscaledTime, _visibleUntil, _gameAlpha);
            if (_alpha != wanted)
            {
                float k = open ? 1f : BackroomsInventorySections.Approach01(_fadeSharpness, Time.unscaledDeltaTime);
                _alpha = BackroomsInventorySections.Settle(_alpha, wanted, k, 0.01f);
                ApplyAlpha();
            }
            bool resized = CountSlots() != _lastCount;
            if (resized && _lastCount >= 0)
            {
                _easeWidth = true;
                Poke(); // ponerse o quitarse un cinturón también lo enseña
            }
            if (Mathf.Approximately(_t, _target) && !resized && !_easeWidth) return;
            _t = Mathf.MoveTowards(_t, _target, Time.unscaledDeltaTime / Mathf.Max(0.01f, _duration));
            Apply();
        }

        private void Apply()
        {
            if (_layout == null || _box == null) return;
            float eased = _t * _t * (3f - 2f * _t);
            _lastCount = CountSlots();
            var (cell, top, width, height) = Measure(_lastCount, eased, _gameCell, _inventoryCell, _gameTop, _inventoryTop, _pad, _gap);

            foreach (Transform child in _layout.transform)
                if (child.gameObject.activeSelf && child.GetComponent<ItemSlotUIBase>() != null)
                    ((RectTransform)child).sizeDelta = new Vector2(cell, cell);
            _layout.padding = new RectOffset(Mathf.RoundToInt(_pad), Mathf.RoundToInt(_pad), Mathf.RoundToInt(top), Mathf.RoundToInt(_pad));
            _layout.spacing = _gap;
            // Pulido 5: al ponerse o quitarse un cinturón la caja se estira o se recoge con curva; al abrir y cerrar TAB
            // sigue al tamaño sin retraso.
            _boxWidth = _easeWidth
                ? BackroomsInventorySections.Settle(_boxWidth, width, BackroomsInventorySections.Approach01(16f, Time.unscaledDeltaTime), 0.5f)
                : width;
            if (_boxWidth == width) _easeWidth = false;
            _box.sizeDelta = new Vector2(_boxWidth, height);
            if (_title != null) _title.alpha = eased;
            if (_strap != null)
            {
                // La cincha de fondo solo con TAB: en juego quedan los huecos.
                if (_strapAlpha < 0f) _strapAlpha = _strap.color.a;
                var c = _strap.color;
                c.a = _strapAlpha * eased;
                _strap.color = c;
            }
            if (_selectionFrame != null) _selectionFrame.sizeDelta = new Vector2(cell + 8f, cell + 8f);
        }

        private int CountSlots()
        {
            int count = 0;
            foreach (Transform child in _layout.transform)
                if (child.gameObject.activeSelf && child.GetComponent<ItemSlotUIBase>() != null) count++;
            return count;
        }

        /// <summary>Medidas del cinturón para <paramref name="eased"/> entre juego (0) e inventario (1). Pura, con test.</summary>
        public static (float cell, float top, float width, float height) Measure(int slots, float eased, float gameCell, float inventoryCell,
            float gameTop, float inventoryTop, float pad, float gap)
        {
            float cell = Mathf.Lerp(gameCell, inventoryCell, eased);
            float top = Mathf.Lerp(gameTop, inventoryTop, eased);
            float width = slots * cell + Mathf.Max(0, slots - 1) * gap + 2f * pad;
            return (cell, top, width, top + cell + pad);
        }
    }
}
