using PolymindGames.InventorySystem;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// TODO: Implement
    /// </summary>
    public sealed class WieldableThrowableHandler : CharacterBehaviour, IWieldableThrowableHandlerCC
    {
        #region Internal Types
        private sealed class ThrowableSlot
        {
            public readonly int ItemId;
            public readonly List<SlotReference> Slots;

            public ThrowableSlot(int itemId, SlotReference slot)
            {
                ItemId = itemId;
                Slots = new List<SlotReference>
                {
                    slot
                };
            }
        }
        #endregion

        [SerializeField, ReorderableList()]
        [DataReference(NullElement = ItemTagDefinition.Untagged, HasLabel = false)]
        private DataIdReference<ItemTagDefinition>[] _containerTags;

        private readonly List<IItemContainer> _containers = new();
        private readonly List<ThrowableSlot> _throwableSlots = new();
        private IWieldableInventoryCC _selection;
        private IWieldablesControllerCC _controller;
        private int _selectedIndex;

        public int SelectedIndex
        {
            get => _selectedIndex;
            private set
            {
                if (_selectedIndex != value)
                {
                    _selectedIndex = Mathf.Clamp(value, 0, _throwableSlots.Count - 1);
                    ThrowableIndexChanged?.Invoke();
                }
            }
        }

        public event UnityAction ThrowableCountChanged;
        public event UnityAction ThrowableIndexChanged;
        public event UnityAction<Throwable> OnThrow;

        public bool TryThrow()
        {
            if (!enabled || _controller.State != WieldableControllerState.None || _throwableSlots.Count == 0 || _throwableSlots[_selectedIndex].Slots.Count == 0)
                return false;

            var selectedSlot = _throwableSlots[_selectedIndex].Slots[0];
            var throwable = (Throwable)_selection.GetWieldableWithId(selectedSlot.GetItem().Id);
            if (_controller.TryEquipWieldable(throwable))
            {
                throwable.Use(WieldableInputPhase.Start);
                OnThrow?.Invoke(throwable);
                return true;
            }

            return false;
        }

        public void SelectNext(bool next)
        {
            float delta = next ? 1f : -1f;
            SelectedIndex = (int)Mathf.Repeat(_selectedIndex + delta, _throwableSlots.Count);
        }

        public Throwable GetThrowableAtIndex(int index)
        {
            if (_throwableSlots.Count == 0)
                return null;

            if (index >= _throwableSlots.Count || index < 0)
            {
                Debug.LogError("Index outside of range");
                return null;
            }

            if (_throwableSlots[index].Slots.Count > 0)
                return _selection.GetWieldableWithId(_throwableSlots[index].Slots[0].GetItem().Id) as Throwable;

            return null;
        }

        public int GetThrowableCountAtIndex(int index)
        {
            if (_throwableSlots.Count > index)
            {
                int count = 0;

                foreach (var slot in _throwableSlots[index].Slots)
                    count += slot.GetStack().Count;

                return count;
            }

            return 0;
        }

        protected override void OnBehaviourStart(ICharacter character)
        {
            _selection = character.GetCC<IWieldableInventoryCC>();
            _controller = character.GetCC<IWieldablesControllerCC>();
            var inventory = character.Inventory;

            foreach (var containerTag in _containerTags)
            {
                var validContainers = inventory.FindContainers(ItemContainerFilters.WithTag(containerTag));
                _containers.AddRange(validContainers);
            }

            foreach (var container in _containers)
            {
                int slotIndex = 0;
                foreach (var itemStack in container)
                {
                    if (itemStack.HasItem() && _selection.GetWieldableWithId(itemStack.Item.Id) != null)
                        AddSlot(container.GetSlot(slotIndex));

                    ++slotIndex;
                }
            }
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            foreach (var container in _containers)
            {
                container.SlotChanged += OnSlotChanged;
            }
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            foreach (var container in _containers)
            {
                container.SlotChanged -= OnSlotChanged;
            }
        }

        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType)
        {
            if (slot.TryGetItem(out var item) && _selection.GetWieldableWithId(item.Id) is Throwable)
            {
                if (AddSlot(slot))
                {
                    OnThrowableCountChanged();
                }
            }
            else if (RemoveSlot(slot))
            {
                OnThrowableCountChanged();
            }
        }

        private bool AddSlot(in SlotReference slot)
        {
            int itemId = slot.GetItem().Id;
            foreach (var throwableSlot in _throwableSlots)
            {
                if (throwableSlot.ItemId != itemId)
                    continue;
                
                if (throwableSlot.Slots.Contains(slot))
                    return false;

                throwableSlot.Slots.Add(slot);
                return true;
            }

            _throwableSlots.Add(new ThrowableSlot(itemId, slot));
            return true;
        }

        private bool RemoveSlot(in SlotReference slot)
        {
            foreach (var throwableSlot in _throwableSlots)
            {
                if (throwableSlot.Slots.Remove(slot))
                    return true;
            }

            return false;
        }

        private void OnThrowableCountChanged()
        {
            ThrowableCountChanged?.Invoke();
            SelectedIndex = Mathf.Min(_selectedIndex, _throwableSlots.Count);
        }
    }
}