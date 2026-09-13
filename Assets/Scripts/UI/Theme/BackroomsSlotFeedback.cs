using System.Collections.Generic;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Pulido 4 (INVENTORY-ROADMAP): respuesta al mover objetos, sin tocar el vendor.
    /// <list type="bullet">
    /// <item>Un hueco que RECIBE un objeto (soltarlo, equiparlo, cogerlo del suelo, desbordes del cinturón) da un rebote
    /// corto. Se detecta comparando el objeto de cada hueco con el del frame anterior, en el mismo contenedor e índice:
    /// reatar contenedores (abrir una caja) no rebota.</item>
    /// <item>Soltar sobre un hueco que no lo admite: destello rojo en ese hueco y una cinta roja encima con el motivo
    /// que da el propio contenedor (lleno, pesa demasiado, no es de ese tipo).</item>
    /// <item>La carga se pone roja y parpadea al acercarse al máximo; en el máximo, rojo fijo.</item>
    /// </list>
    /// </summary>
    public sealed class BackroomsSlotFeedback : CharacterUIBehaviour
    {
        private const float PopSeconds = 0.22f;
        private const float FlashSeconds = 0.45f;
        private const float TagHold = 0.9f;
        private const float TagFade = 0.25f;

        [SerializeField] private RectTransform _rejectTag;
        [SerializeField] private CanvasGroup _rejectGroup;
        [SerializeField] private TextMeshProUGUI _rejectText;
        [SerializeField] private TextMeshProUGUI _loadText;
        [SerializeField] private Image _loadFill;
        [SerializeField] private Color _warnColor = new(0.8f, 0.3f, 0.25f);
        [SerializeField, Range(0.5f, 1f)] private float _warnAt = 0.85f;

        private struct Seen
        {
            public IItemContainer Container;
            public int Index;
            public Item Item;
        }

        private readonly List<ItemSlotUIBase> _slots = new();
        private readonly Dictionary<ItemSlotUIBase, Seen> _seen = new();
        private readonly Dictionary<Transform, float> _pops = new();
        private readonly Dictionary<Image, float> _flashes = new();
        private readonly List<Transform> _donePops = new();
        private readonly List<Image> _doneFlashes = new();
        private readonly List<ItemSlotUIBase> _dragScan = new();
        private readonly List<RaycastResult> _hits = new();
        private PointerEventData _pointer;
        private IInventoryInspectionManagerCC _inspection;
        private InventoryUI _inventoryUI;
        private float _rescanAt;
        private bool _wasDragging;
        private ItemStack _dragged = ItemStack.Null;
        private ItemSlotUIBase _hover;
        private float _tagShownAt = -100f;
        private Color _loadTextBase;
        private Color _loadFillBase;
        private bool _warned;

        protected override void Awake()
        {
            base.Awake();
            _inventoryUI = GetComponentInParent<InventoryUI>();
            if (_rejectGroup != null) _rejectGroup.alpha = 0f;
        }

        protected override void OnCharacterAttached(ICharacter character)
            => _inspection = character.GetCC<IInventoryInspectionManagerCC>();

        protected override void OnCharacterDetached(ICharacter character) => _inspection = null;

        private void LateUpdate()
        {
            float now = Time.unscaledTime;
            bool open = _inspection != null && _inspection.IsInspecting;
            var dragRoot = ItemDragger.HasInstance ? ItemDragger.Instance.transform.parent : null;

            if (now >= _rescanAt)
            {
                GetComponentsInChildren(true, _slots);
                _rescanAt = now + 1f;
            }

            foreach (var slot in _slots)
            {
                if (slot == null || (dragRoot != null && slot.transform.IsChildOf(dragRoot))) continue;
                var reference = slot.Slot;
                var item = slot.HasItem ? reference.GetItem() : null;
                if (open && item != null && _seen.TryGetValue(slot, out var seen) && item != seen.Item
                    && seen.Container == reference.Container && seen.Index == reference.Index && slot.gameObject.activeInHierarchy)
                    _pops[slot.transform] = now;
                _seen[slot] = new Seen { Container = reference.Container, Index = reference.Index, Item = item };
            }

            TrackDrag(dragRoot, now);
            Animate(now);
            UpdateTag(now);
            UpdateLoad(now);
        }

        private void TrackDrag(Transform dragRoot, float now)
        {
            bool dragging = BackroomsDragProbe.TryGetDraggedStack(_dragScan, out var stack);
            if (dragging)
            {
                _dragged = stack;
                _hover = HoverSlot(dragRoot);
            }
            else if (_wasDragging)
            {
                CheckRejected(now);
                _hover = null;
                _dragged = ItemStack.Null;
            }
            _wasDragging = dragging;
        }

        private ItemSlotUIBase HoverSlot(Transform dragRoot)
        {
            var events = EventSystem.current;
            var pointer = Pointer.current;
            if (events == null || pointer == null) return null;
            _pointer ??= new PointerEventData(events);
            _pointer.position = pointer.position.ReadValue();
            _hits.Clear();
            events.RaycastAll(_pointer, _hits);
            foreach (var hit in _hits)
            {
                if (hit.gameObject == null) continue;
                var slot = hit.gameObject.GetComponentInParent<ItemSlotUIBase>();
                if (slot != null && (dragRoot == null || !slot.transform.IsChildOf(dragRoot))) return slot;
            }
            return null;
        }

        private void CheckRejected(float now)
        {
            if (_hover == null || !_hover.HasSlot || !_dragged.HasItem()) return;
            // Entró (colocado o intercambiado): no hay rechazo.
            if (_hover.Slot.GetItem() == _dragged.Item) return;
            var container = _hover.Slot.Container;
            if (container == null) return;
            var (allowed, reason) = container.GetAllowedCount(_dragged);
            if (allowed > 0) return;

            Flash(_hover, now);
            ShowTag((RectTransform)_hover.transform, reason, now);
        }

        private void Flash(ItemSlotUIBase slot, float now)
        {
            var rt = slot.transform.Find("BR_Flash") as RectTransform;
            Image image;
            if (rt == null)
            {
                var go = new GameObject("BR_Flash", typeof(RectTransform), typeof(Image));
                rt = (RectTransform)go.transform;
                rt.SetParent(slot.transform, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                image = go.GetComponent<Image>();
                image.raycastTarget = false;
                if (slot.TryGetComponent<Image>(out var background))
                {
                    image.sprite = background.sprite;
                    image.type = background.type;
                }
            }
            else image = rt.GetComponent<Image>();
            rt.SetAsLastSibling();
            _flashes[image] = now;
        }

        private void ShowTag(RectTransform slot, string reason, float now)
        {
            if (_rejectTag == null || _rejectText == null) return;
            _rejectText.text = string.IsNullOrEmpty(reason) ? "No cabe" : reason;
            _rejectTag.position = slot.TransformPoint(new Vector3(slot.rect.center.x, slot.rect.yMax, 0f));
            _rejectTag.anchoredPosition += new Vector2(0f, 6f);
            _tagShownAt = now;
        }

        private void Animate(float now)
        {
            _donePops.Clear();
            foreach (var pair in _pops)
            {
                if (pair.Key == null) { _donePops.Add(pair.Key); continue; }
                float u = (now - pair.Value) / PopSeconds;
                float scale = u >= 1f ? 1f : PopScale(u);
                pair.Key.localScale = new Vector3(scale, scale, 1f);
                if (u >= 1f) _donePops.Add(pair.Key);
            }
            foreach (var key in _donePops) _pops.Remove(key);

            _doneFlashes.Clear();
            foreach (var pair in _flashes)
            {
                if (pair.Key == null) { _doneFlashes.Add(pair.Key); continue; }
                float u = Mathf.Clamp01((now - pair.Value) / FlashSeconds);
                var color = _warnColor;
                color.a = 0.75f * (1f - u);
                pair.Key.color = color;
                if (u >= 1f) _doneFlashes.Add(pair.Key);
            }
            foreach (var key in _doneFlashes) _flashes.Remove(key);
        }

        private void UpdateTag(float now)
        {
            if (_rejectGroup == null) return;
            float age = now - _tagShownAt;
            float alpha = age < TagHold ? 1f : 1f - Mathf.Clamp01((age - TagHold) / TagFade);
            if (_rejectGroup.alpha != alpha) _rejectGroup.alpha = alpha;
        }

        private void UpdateLoad(float now)
        {
            var inventory = _inventoryUI != null ? _inventoryUI.Inventory : null;
            if (inventory == null || _loadText == null) return;
            float max = inventory.MaxWeight;
            float ratio = max > 0f ? inventory.Weight / max : 0f;
            float warning = LoadWarning(ratio, _warnAt, 0.5f + 0.5f * Mathf.Sin(now * 7f));
            if (warning <= 0f)
            {
                if (!_warned) return;
                _loadText.color = _loadTextBase;
                if (_loadFill != null) _loadFill.color = _loadFillBase;
                _warned = false;
                return;
            }
            if (!_warned)
            {
                _loadTextBase = _loadText.color;
                if (_loadFill != null) _loadFillBase = _loadFill.color;
                _warned = true;
            }
            _loadText.color = Color.Lerp(_loadTextBase, _warnColor, warning);
            if (_loadFill != null) _loadFill.color = Color.Lerp(_loadFillBase, _warnColor, warning);
        }

        /// <summary>Escala del rebote a la fracción <paramref name="u"/> de su duración: sube rápido y se asienta en 1.</summary>
        public static float PopScale(float u)
        {
            u = Mathf.Clamp01(u);
            return 1f + 0.16f * Mathf.Sin(Mathf.PI * u) * (1f - u);
        }

        /// <summary>Cuánto rojo lleva la carga: nada por debajo del aviso, parpadeo cerca del máximo, fijo en él.</summary>
        public static float LoadWarning(float ratio, float warnAt, float pulse)
        {
            if (ratio >= 1f) return 1f;
            if (ratio < warnAt) return 0f;
            return Mathf.Lerp(0.35f, 0.9f, Mathf.Clamp01(pulse));
        }
    }
}
