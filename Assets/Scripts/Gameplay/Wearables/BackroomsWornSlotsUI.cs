using System.Collections.Generic;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// PROTOTIPO (ADR-147 enm. 1): la sección de la espalda enseña solo los huecos que da la mochila puesta y dice cuál
    /// es, cuántos huecos y cuántos kg lleva contra su máximo. El contenedor precreado tiene siempre 27 huecos; lo que
    /// capa de verdad es <see cref="WornCapacityRestriction"/>, esto solo esconde lo que no se puede usar.
    ///
    /// Pulido 5 (Joel, 2026-09-13): al ponerte la prenda los huecos nuevos se DESPLIEGAN uno detrás de otro con un
    /// pequeño rebote, y al quitártela se recogen en orden inverso antes de desaparecer. La primera colocación va de
    /// golpe.
    /// </summary>
    [RequireComponent(typeof(ItemContainerUI))]
    public sealed class BackroomsWornSlotsUI : MonoBehaviour
    {
        private const float AppearSeconds = 0.24f;
        private const float HideSeconds = 0.14f;
        private const float StaggerStep = 0.045f;
        private const float StaggerTotal = 0.24f;

        [SerializeField]
        private string _ownerContainer = "Back";

        // D14: huecos que se ven sin nada puesto (las 2 manos de la barra).
        [SerializeField, Range(0, 9)]
        private int _baseSlots;

        [SerializeField]
        private TextMeshProUGUI _title;

        [SerializeField]
        private TextMeshProUGUI _count;

        private readonly Dictionary<ItemSlotUIBase, (float start, bool appear)> _anims = new();
        private readonly List<ItemSlotUIBase> _finished = new();
        private readonly List<ItemSlotUIBase> _toShow = new();
        private readonly List<ItemSlotUIBase> _toHide = new();
        private ItemContainerUI _ui;
        private IItemContainer _owner;
        private IItemContainer _storage;
        private bool _placed;

        private void Awake()
        {
            _ui = GetComponent<ItemContainerUI>();
            _ui.AttachedContainerChanged += OnAttached;
            if (_ui.Container != null) OnAttached(_ui.Container);
        }

        private void OnDestroy()
        {
            if (_ui != null) _ui.AttachedContainerChanged -= OnAttached;
            Unbind();
        }

        private void OnAttached(IItemContainer storage)
        {
            Unbind();
            _storage = storage;
            _owner = storage?.Inventory?.FindContainer(ItemContainerFilters.WithName(_ownerContainer));
            if (_owner != null) _owner.SlotChanged += OnChanged;
            if (_storage != null) _storage.SlotChanged += OnChanged;
            Refresh();
        }

        private void Unbind()
        {
            if (_owner != null) _owner.SlotChanged -= OnChanged;
            if (_storage != null) _storage.SlotChanged -= OnChanged;
            _owner = null;
            _storage = null;
        }

        private void OnChanged(in SlotReference slot, SlotChangeType changeType) => Refresh();

        private void Refresh()
        {
            Item worn = null;
            if (_owner != null && _owner.SlotsCount > 0 && _owner.GetItemAtIndex(0).HasItem())
                worn = _owner.GetItemAtIndex(0).Item;
            WearableCapacityData capacity = null;
            worn?.Definition.TryGetDataOfType(out capacity);
            int slots = _baseSlots + (capacity?.Slots ?? 0);

            var slotsUI = _ui.ItemSlotsUI;
            if (slotsUI != null && slotsUI.Count > 0)
            {
                bool animate = _placed && isActiveAndEnabled;
                _toShow.Clear();
                _toHide.Clear();
                for (int i = 0; i < slotsUI.Count; i++)
                {
                    var slot = slotsUI[i];
                    bool show = i < slots;
                    bool hiding = _anims.TryGetValue(slot, out var anim) && !anim.appear;
                    bool visible = slot.gameObject.activeSelf && !hiding;
                    if (show == visible) continue;
                    if (!animate)
                    {
                        _anims.Remove(slot);
                        slot.transform.localScale = Vector3.one;
                        slot.gameObject.SetActive(show);
                        continue;
                    }
                    if (show) _toShow.Add(slot);
                    else _toHide.Add(slot);
                }

                float now = Time.unscaledTime;
                for (int k = 0; k < _toShow.Count; k++)
                {
                    var slot = _toShow[k];
                    slot.gameObject.SetActive(true);
                    Center(slot);
                    slot.transform.localScale = new Vector3(0f, 0f, 1f);
                    _anims[slot] = (now + StaggerDelay(k, _toShow.Count), true);
                }
                // Se recogen del último al primero.
                for (int k = 0; k < _toHide.Count; k++)
                {
                    var slot = _toHide[_toHide.Count - 1 - k];
                    Center(slot);
                    _anims[slot] = (now + StaggerDelay(k, _toHide.Count), false);
                }
                _placed = true;
            }

            int used = 0;
            if (_storage != null)
                for (int i = 0; i < Mathf.Min(slots, _storage.SlotsCount); i++)
                    if (_storage.GetItemAtIndex(i).HasItem()) used++;

            if (_title != null)
                _title.text = worn != null ? $"ESPALDA · {worn.Name.ToUpperInvariant()}" : "ESPALDA · SIN MOCHILA";
            if (_count != null)
                _count.text = capacity != null
                    ? $"{used}/{slots} huecos · {_storage?.Weight ?? 0f:0.#}/{capacity.MaxKg:0.#} kg"
                    : "ponte una mochila";
        }

        private void Update()
        {
            if (_anims.Count == 0) return;
            float now = Time.unscaledTime;
            _finished.Clear();
            foreach (var pair in _anims)
            {
                var slot = pair.Key;
                if (slot == null) { _finished.Add(slot); continue; }
                var (start, appear) = pair.Value;
                float u = (now - start) / (appear ? AppearSeconds : HideSeconds);
                if (u < 0f) continue;
                float c = Mathf.Clamp01(u);
                float scale = appear ? EaseOutBack(c) : 1f - c * c;
                slot.transform.localScale = new Vector3(scale, scale, 1f);
                if (u >= 1f) _finished.Add(slot);
            }
            foreach (var slot in _finished)
            {
                if (slot != null)
                {
                    if (!_anims[slot].appear) slot.gameObject.SetActive(false);
                    slot.transform.localScale = Vector3.one;
                }
                _anims.Remove(slot);
            }
        }

        /// <summary>Escalar desde el centro del hueco, no desde su esquina; la rejilla recoloca con el pivote nuevo.</summary>
        private static void Center(ItemSlotUIBase slot)
        {
            var rt = (RectTransform)slot.transform;
            var center = new Vector2(0.5f, 0.5f);
            if (rt.pivot == center) return;
            rt.pivot = center;
            if (rt.parent is RectTransform parent) LayoutRebuilder.MarkLayoutForRebuild(parent);
        }

        /// <summary>Curva con un pequeño rebote al final: 0 → algo más de 1 → 1.</summary>
        public static float EaseOutBack(float u)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float x = u - 1f;
            return 1f + c3 * x * x * x + c1 * x * x;
        }

        /// <summary>Retraso del hueco <paramref name="k"/> de <paramref name="count"/>: uno detrás de otro, sin pasar de un cuarto de segundo en total.</summary>
        public static float StaggerDelay(int k, int count)
        {
            if (k <= 0 || count <= 1) return 0f;
            return Mathf.Min(StaggerStep * k, StaggerTotal * k / (count - 1));
        }
    }
}
