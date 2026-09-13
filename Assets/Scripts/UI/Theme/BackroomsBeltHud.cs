using PolymindGames;
using PolymindGames.UserInterface;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// El cinturón en juego (Joel, 2026-09-13 noche): con TAB abierto va en su caja con rótulo y huecos de inventario;
    /// al cerrar se compacta en una transición corta a huecos más grandes, sin rótulo, y la caja se ajusta a lo que
    /// ocupan. Es la misma pieza (<c>HotbarUI</c>) en los dos estados; el vendor sigue decidiendo cuándo se ve.
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

        private IInventoryInspectionManagerCC _inspection;
        private float _t;
        private float _target;

        protected override void OnCharacterAttached(ICharacter character)
        {
            _inspection = character.GetCC<IInventoryInspectionManagerCC>();
            if (_inspection == null) return;
            _inspection.InspectionStarted += Open;
            _inspection.InspectionEnded += Close;
            _t = _target = _inspection.IsInspecting ? 1f : 0f;
            Apply();
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            if (_inspection == null) return;
            _inspection.InspectionStarted -= Open;
            _inspection.InspectionEnded -= Close;
            _inspection = null;
        }

        private void Open() => _target = 1f;

        private void Close() => _target = 0f;

        private void Update()
        {
            if (Mathf.Approximately(_t, _target)) return;
            _t = Mathf.MoveTowards(_t, _target, Time.unscaledDeltaTime / Mathf.Max(0.01f, _duration));
            Apply();
        }

        private void Apply()
        {
            if (_layout == null || _box == null) return;
            float eased = _t * _t * (3f - 2f * _t);
            var (cell, top, width, height) = Measure(CountSlots(), eased, _gameCell, _inventoryCell, _gameTop, _inventoryTop, _pad, _gap);

            foreach (Transform child in _layout.transform)
                if (child.gameObject.activeSelf && child.GetComponent<ItemSlotUIBase>() != null)
                    ((RectTransform)child).sizeDelta = new Vector2(cell, cell);
            _layout.padding = new RectOffset(Mathf.RoundToInt(_pad), Mathf.RoundToInt(_pad), Mathf.RoundToInt(top), Mathf.RoundToInt(_pad));
            _layout.spacing = _gap;
            _box.sizeDelta = new Vector2(width, height);
            if (_title != null) _title.alpha = eased;
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
