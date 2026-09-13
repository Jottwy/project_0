using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using TMPro;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// PROTOTIPO (ADR-147 enm. 1): la sección de la espalda enseña solo los huecos que da la mochila puesta y dice cuál
    /// es, cuántos huecos y cuántos kg lleva contra su máximo. El contenedor precreado tiene siempre 27 huecos; lo que
    /// capa de verdad es <see cref="WornCapacityRestriction"/>, esto solo esconde lo que no se puede usar.
    /// </summary>
    [RequireComponent(typeof(ItemContainerUI))]
    public sealed class BackroomsWornSlotsUI : MonoBehaviour
    {
        [SerializeField]
        private string _ownerContainer = "Back";

        [SerializeField]
        private TextMeshProUGUI _title;

        [SerializeField]
        private TextMeshProUGUI _count;

        private ItemContainerUI _ui;
        private IItemContainer _owner;
        private IItemContainer _storage;

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
            int slots = capacity?.Slots ?? 0;

            var slotsUI = _ui.ItemSlotsUI;
            if (slotsUI != null)
                for (int i = 0; i < slotsUI.Count; i++)
                    slotsUI[i].gameObject.SetActive(i < slots);

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
    }
}
