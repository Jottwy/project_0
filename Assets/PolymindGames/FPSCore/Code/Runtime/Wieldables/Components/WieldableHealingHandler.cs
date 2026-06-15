using PolymindGames.InventorySystem;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [RequireCharacterComponent(typeof(IWieldableInventoryCC), typeof(IWieldablesControllerCC))]
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault1)]
    public sealed class WieldableHealingHandler : CharacterBehaviour, IWieldableHealingHandlerCC
    {
        [SerializeField, ReorderableList]
        [DataReference(NullElement = ItemTagDefinition.Untagged, HasLabel = false)]
        private DataIdReference<ItemTagDefinition>[] _containerTags;

        private readonly List<IItemContainer> _containers = new();
        private readonly List<SlotReference> _healSlots = new();
        private IWieldableInventoryCC _selection;
        private IWieldablesControllerCC _controller;
        private HealingWieldable _healingWieldable;
        private int _healsCount;

        public int HealsCount
        {
            get => _healsCount;
            private set
            {
                if (value != _healsCount)
                {
                    _healsCount = value;
                    HealsCountChanged?.Invoke(value);
                }
            }
        }

        public event UnityAction<int> HealsCountChanged;

        public bool TryHeal()
        {
            if (_healSlots.Count == 0
                || Character.HealthManager.IsFullHealth()
                || _controller.State != WieldableControllerState.None)
                return false;
            
            if (_healSlots[0].TryGetItem(out var item))
            {
                _healingWieldable = (HealingWieldable)_selection.GetWieldableWithId(item.Id);
                if (_controller.TryEquipWieldable(_healingWieldable, 1f, Heal))
                    return true;
            }

            return false;
        }

        private void Heal()
        {
            _healingWieldable.Heal(OnHeal);
            // var wieldableItem = _healingWieldable.GetComponent<WieldableItem>();
            // wieldableItem.SetSlot(_healSlots[0].GetItem());
        }

        private void OnHeal()
        {
            // Remove the healing item from the inventory.
            _healSlots[0].AdjustStack(-1);

            // Holster the healing wieldable.
            _controller.TryHolsterWieldable(_healingWieldable);
        }

        protected override void OnBehaviourStart(ICharacter character)
        {
            _selection = character.GetCC<IWieldableInventoryCC>();
            _controller = character.GetCC<IWieldablesControllerCC>();
            var inventory = character.Inventory;

            foreach (var containerTag in _containerTags)
                _containers.AddRange(inventory.FindContainers(ItemContainerFilters.WithTag(containerTag)));

            foreach (var container in _containers)
            {
                foreach (var slot in container.GetSlots())
                {
                    if (slot.TryGetItem(out var item) && _selection.GetWieldableWithId(item.Id) is HealingWieldable)
                        _healSlots.Add(slot);
                }
            }
            
            RecalculateHealsCount();
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
            if (slot.HasItem())
            {
                if (_healSlots.Contains(slot))
                {
                    HealsCountChanged?.Invoke(RecalculateHealsCount());
                }
                else if (_selection.GetWieldableWithId(slot.GetItem().Id) is HealingWieldable)
                {
                    _healSlots.Add(slot);
                    RecalculateHealsCount();
                }
            }
            else
            {
                if (_healSlots.Remove(slot))
                    HealsCountChanged?.Invoke(RecalculateHealsCount());
            }
        }

        private int RecalculateHealsCount()
        {
            int count = 0;
            foreach (var healSlot in _healSlots)
            {
                if (healSlot.TryGetStack(out var stack))
                    count += stack.Count;
            }

            HealsCount = count;
            return count;
        }
    }
}