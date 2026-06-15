using System.Runtime.Serialization;
using System.Collections.Generic;
using UnityEngine.Events;
using System.Collections;
using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    [Serializable]
    public sealed class ItemContainer : IItemContainer, IDeserializationCallback
    {
        [SerializeField]
        private string _containerName;

        [SerializeField]
        private float _maximumWeight;

        [SerializeField]
        private bool _allowStacking;

        [SerializeField]
        private ItemStack[] _slots;

        [NonSerialized]
        private float _weight;

        [NonSerialized]
        private IInventory _inventory;

        [NonSerialized]
        private ContainerRestriction[] _restrictions;

        [NonSerialized]
        private bool _isInitialized;

        private SlotChangedDelegate[] _slotChangedActions;

		#region Initialization
        public sealed class Builder
        {
            public const float MaxWeightLimit = 1000f;

            private List<ContainerRestriction> _restrictions;
            private float _maximumWeight = MaxWeightLimit;
            private string _containerName = string.Empty;
            private bool _allowStacking = true;
            private IInventory _inventory;
            private int _size;

            public Builder WithInventory(IInventory inventory)
            {
                _inventory = inventory;
                return this;
            }

            public Builder WithName(string name)
            {
                _containerName = name;
                return this;
            }

            public Builder WithSize(int size)
            {
                _size = size;
                return this;
            }

            public Builder WithMaxWeight(float maxWeight)
            {
                _maximumWeight = Mathf.Clamp(maxWeight, 0, MaxWeightLimit);
                return this;
            }

            public Builder WithRestriction(ContainerRestriction restriction)
            {
                if (restriction == null)
                    return this;

                _restrictions ??= new List<ContainerRestriction>(1);
                _restrictions.Add(restriction);
                return this;
            }

            public Builder WithRestrictions(params ContainerRestriction[] restrictions)
            {
                foreach (var restriction in restrictions)
                    WithRestriction(restriction);
                return this;
            }

            public Builder WithAllowStacking(bool allowStacking)
            {
                _allowStacking = allowStacking;
                return this;
            }

            public ItemContainer Build()
            {
                if (_size <= 0)
                    throw new InvalidOperationException("Size must be greater than zero.");

                return new ItemContainer(
                    _inventory,
                    _containerName,
                    _size,
                    _maximumWeight,
                    _allowStacking,
                    _restrictions != null ? _restrictions.ToArray() : Array.Empty<ContainerRestriction>()
                );
            }
        }

        private ItemContainer() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemContainer"/> class.
        /// </summary>
        private ItemContainer(IInventory inventory, string name, int size, float maxWeight, bool allowStacking, params ContainerRestriction[] restrictions)
        {
            _containerName = name;
            _restrictions = restrictions;
            _inventory = inventory;
            _slots = new ItemStack[size];
            _maximumWeight = maxWeight;
            _allowStacking = allowStacking;
            _isInitialized = true;
            _slotChangedActions = new SlotChangedDelegate[size];
        }

        /// <summary>
        /// Initializes the container and sets its parent inventory and add rules.
        /// </summary>
        public void InitializeAfterDeserialization(IInventory parentInventory, params ContainerRestriction[] restrictions)
        {
            if (_isInitialized)
                return;

            _inventory = parentInventory;
            _restrictions = restrictions ?? Array.Empty<ContainerRestriction>();
            _isInitialized = true;
        }
		#endregion

        /// <inheritdoc/>
        public IReadOnlyList<ContainerRestriction> Restrictions => _restrictions;

        /// <inheritdoc/>
        public IInventory Inventory => _inventory;

        /// <inheritdoc/>
        public string Name => _containerName;

        /// <inheritdoc/>
        public int SlotsCount => _slots.Length;

        /// <inheritdoc/>
        public float Weight => _weight;

        /// <inheritdoc/>
        public float MaxWeight => _maximumWeight;

        /// <inheritdoc/>
        public event UnityAction Changed;

        /// <inheritdoc/>
        public event SlotChangedDelegate SlotChanged;

        /// <inheritdoc/>
        public void AddSlotChangedListener(int index, SlotChangedDelegate callback)
        {
#if DEBUG
            if (index < 0 || index > _slots.Length)
                throw new IndexOutOfRangeException(nameof(index));
#endif

            _slotChangedActions[index] += callback;
        }

        /// <inheritdoc/>
        public void RemoveSlotChangedListener(int index, SlotChangedDelegate callback)
        {
#if DEBUG
            if (index < 0 || index > _slots.Length)
                throw new IndexOutOfRangeException(nameof(index));
#endif
            _slotChangedActions[index] -= callback;
        }

        /// <inheritdoc/>
        public ItemStack GetItemAtIndex(int index)
        {
#if DEBUG
            if (index < 0 || index > _slots.Length)
                throw new IndexOutOfRangeException(nameof(index));
#endif
            return _slots[index];
        }

        /// <inheritdoc/>
        public int SetItemAtIndex(int index, ItemStack newStack)
        {
#if DEBUG
            if (index < 0 || index >= _slots.Length)
                throw new ArgumentOutOfRangeException(nameof(index), "Invalid slot index.");
#endif

            return !_slots[index].HasItem()
                ? HandleNewStack(index, newStack)
                : HandleReplaceOrClear(index, newStack);
        }

        /// <inheritdoc/>
        public int AdjustStackAtIndex(int index, int adjustment)
        {
#if DEBUG
            if (index < 0 || index >= _slots.Length)
                throw new ArgumentOutOfRangeException(nameof(index), "Invalid slot index.");
#endif

            if (adjustment == 0 || !_slots[index].HasItem())
                return 0;

            return adjustment < 0
                ? HandleItemRemoval(index, adjustment)
                : HandleItemAddition(index, adjustment);
        }

        /// <inheritdoc/>
        public (int addedCount, string rejectReason) AddItem(ItemStack stack)
        {
            if (!stack.HasItem())
                return (0, ContainerRestriction.ItemNullRejection);

            (int allowedCount, string rejectReason) = GetAllowedCount(stack);
            if (allowedCount == 0)
                return (0, rejectReason);

            int addedCount = 0;

            if (_allowStacking && stack.Item.IsStackable)
            {
                addedCount = AdjustAllStacks(stack.Item.Id, allowedCount);
                if (addedCount == stack.Count)
                    return (addedCount, string.Empty);
            }

            for (int i = 0; i < _slots.Length; ++i)
            {
                ref var slotStack = ref _slots[i];
                if (!slotStack.HasItem() && addedCount != stack.Count)
                    addedCount += SetItemAtIndex(i, new ItemStack(stack.Item, allowedCount - addedCount));
            }

            return (addedCount, addedCount > 0 ? string.Empty : ContainerRestriction.InventoryFullRejection);
        }

        /// <inheritdoc/>
        public (int addedCount, string rejectReason) AddItemsById(int itemId, int amount)
        {
            if (!ItemDefinition.TryGetWithId(itemId, out var itemDef))
                return (0, ContainerRestriction.ItemNullRejection);

            (int allowedCount, string rejectReason) = GetAllowedCount(new ItemStack(Item.GetDummyItem(itemDef), amount));
            if (allowedCount == 0)
                return (0, rejectReason);

            int added = 0;

            if (itemDef.StackSize > 1)
            {
                added += AdjustAllStacks(itemId, allowedCount);

                if (added == allowedCount)
                    return (added, string.Empty);
            }

            for (int i = 0; i < _slots.Length; ++i)
            {
                ref var stack = ref _slots[i];
                if (!stack.HasItem())
                {
                    added += SetItemAtIndex(i, new ItemStack(new Item(itemDef), allowedCount - added));

                    if (added == allowedCount)
                        return (added, string.Empty);
                }
            }

            if (added > 0)
                return (added, string.Empty);

            return (added, ContainerRestriction.InventoryFullRejection);
        }

        /// <inheritdoc/>
        public (int allowedCount, string rejectReason) GetAllowedCount(ItemStack stack)
        {
            if (!stack.HasItem())
                return (0, ContainerRestriction.ItemNullRejection);

            int allowedCount = CalculateMaxItemCountForWeight(stack);
            if (allowedCount == 0)
            {
                return (0, ContainerRestriction.WeightLimitRejection);
            }

            foreach (var restriction in _restrictions)
            {
                allowedCount = restriction.GetAllowedCount(this, stack.Item, allowedCount);
                if (allowedCount <= 0)
                {
                    return (0, restriction.RejectionReason);
                }
            }

            return (allowedCount, string.Empty);
        }

        /// <inheritdoc/>
        public bool ContainsItem(Item item) => Array.FindIndex(_slots, stack => stack.Item == item) != -1;

        /// <inheritdoc/>
        public int RemoveItem(ItemStack stack)
        {
            int index = Array.FindIndex(_slots, itemStack => itemStack.Item == stack.Item);
            return index == -1
                ? 0
                : AdjustStackAtIndex(index, -stack.Count);
        }

        /// <inheritdoc/>
        public int RemoveItemsById(int itemId, int amount)
        {
            int removedTotal = 0;
            for (int i = 0; i < _slots.Length; ++i)
            {
                ref var stack = ref _slots[i];
                if (stack.HasItem() && stack.Item.Id == itemId)
                {
                    removedTotal += AdjustStackAtIndex(i, -(amount - removedTotal));

                    // We've removed all the items, we can stop now
                    if (removedTotal == amount)
                        return removedTotal;
                }
            }

            return removedTotal;
        }

        /// <inheritdoc/>
        public int RemoveItems(Func<Item, bool> filter, int amount)
        {
            int removedTotal = 0;
            for (int i = 0; i < _slots.Length; ++i)
            {
                ref var stack = ref _slots[i];
                if (stack.HasItem() && filter(stack.Item))
                {
                    removedTotal += AdjustStackAtIndex(i, -(amount - removedTotal));

                    // We've removed all the items, we can stop now
                    if (removedTotal == amount)
                        return removedTotal;
                }
            }

            return removedTotal;
        }

        /// <inheritdoc/>
        public bool ContainsItemById(int itemId)
        {
            for (int i = 0; i < _slots.Length; ++i)
            {
                ref var stack = ref _slots[i];
                if (stack.HasItem() && stack.Item.Id == itemId)
                    return true;
            }

            return false;
        }

        /// <inheritdoc/>
        public bool ContainsItem(Func<Item, bool> filter)
        {
            for (int i = 0; i < _slots.Length; ++i)
            {
                ref var stack = ref _slots[i];
                if (stack.HasItem() && filter(stack.Item))
                    return true;
            }

            return false;
        }

        /// <inheritdoc/>
        public int GetItemCountById(int itemId)
        {
            int totalCount = 0;
            for (int i = 0; i < _slots.Length; ++i)
            {
                ref var stack = ref _slots[i];
                if (stack.HasItem() && stack.Item.Id == itemId)
                    totalCount += stack.Count;
            }

            return totalCount;
        }

        /// <inheritdoc/>
        public int GetItemCount(Func<Item, bool> filter)
        {
            int totalCount = 0;
            for (int i = 0; i < _slots.Length; ++i)
            {
                ref var stack = ref _slots[i];
                if (stack.HasItem() && filter(stack.Item))
                    totalCount += stack.Count;
            }

            return totalCount;
        }

        /// <inheritdoc/>
        public void SortItems(Comparison<ItemStack> comparison)
        {
            Array.Sort(_slots, comparison);
            RaiseAllEvents();
        }

        /// <inheritdoc/>
        public void Clear()
        {
            Array.Clear(_slots, 0, _slots.Length);
            RaiseAllEvents();
        }

        private int HandleNewStack(int index, ItemStack newStack)
        {
            if (!_allowStacking)
                newStack.Count = Mathf.Min(newStack.Count, 1);

            int allowedCount = GetAllowedCount(newStack).allowedCount;
            if (allowedCount == 0)
                return 0;

            newStack.Count = allowedCount;
            _slots[index] = newStack;
            _weight += newStack.Item.Weight * allowedCount;
            RaiseChangeEvents(index, SlotChangeType.ItemChanged);

            return allowedCount;
        }

        private int HandleReplaceOrClear(int index, ItemStack newStack)
        {
            if (!newStack.HasItem())
            {
                ref var currentStack = ref _slots[index];

                // Removing the stack from the slot.
                _weight -= currentStack.Item.Weight * currentStack.Count;
                currentStack = ItemStack.Null;
                RaiseChangeEvents(index, SlotChangeType.ItemChanged);
                return 0;
            }

            if (!_allowStacking)
                newStack.Count = Mathf.Min(newStack.Count, 1);

            // Replacing the existing stack.
            var prevStack = _slots[index];
            _weight -= prevStack.Item.Weight * prevStack.Count;
            _slots[index] = ItemStack.Null;

            int allowedCount = GetAllowedCount(newStack).allowedCount;
            if (allowedCount == 0)
                return 0;

            newStack.Count = allowedCount;
            _slots[index] = newStack;
            _weight += newStack.Item.Weight * newStack.Count;
            RaiseChangeEvents(index, SlotChangeType.ItemChanged);

            return allowedCount;
        }

        private int HandleItemRemoval(int index, int countToChange)
        {
            ref var currentStack = ref _slots[index];
            int countToRemove = Mathf.Min(-countToChange, currentStack.Count);

            _weight -= countToRemove * currentStack.Item.Weight;

            currentStack.Count -= countToRemove;
            
            bool isStackEmpty = currentStack.Count == 0;
            if (isStackEmpty)
                currentStack.Item = null;
            
            RaiseChangeEvents(index, isStackEmpty ? SlotChangeType.ItemChanged : SlotChangeType.CountChanged);

            return countToRemove;
        }

        private int HandleItemAddition(int index, int countToChange)
        {
            if (!_allowStacking)
                return 0;

            var currentStack = _slots[index];
            int availableSpace = currentStack.Item.StackSize - currentStack.Count;
            int countToAdd = Mathf.Min(countToChange, availableSpace);
            countToAdd = CalculateMaxItemCountForWeight(new ItemStack(currentStack.Item, countToAdd));

            currentStack.Count += countToAdd;

            _slots[index] = currentStack;
            _weight += currentStack.Item.Weight * countToAdd;
            RaiseChangeEvents(index, SlotChangeType.CountChanged);

            return countToAdd;
        }

        private void RaiseChangeEvents(int index, SlotChangeType changeType)
        {
            var slot = new SlotReference(this, index);
            _slotChangedActions[index]?.Invoke(slot, changeType);
            SlotChanged?.Invoke(slot, changeType);
            Changed?.Invoke();
        }

        private void RaiseAllEvents()
        {
            for (int i = 0; i < _slots.Length; ++i)
            {
                var slot = new SlotReference(this, i);
                _slotChangedActions[i]?.Invoke(slot, SlotChangeType.ItemChanged);
                SlotChanged?.Invoke(slot, SlotChangeType.ItemChanged);
            }

            Changed?.Invoke();
        }

        private int CalculateMaxItemCountForWeight(ItemStack stack)
        {
            return Mathf.Min((int)Mathf.Floor(CalculateAvailableWeight() / stack.Item.Weight), stack.Count);
        }

        private float CalculateAvailableWeight()
        {
            float containerWeight = _maximumWeight - _weight;
            float inventoryWeight = _inventory != null ? _inventory.MaxWeight - _inventory.Weight : float.PositiveInfinity;
            return Mathf.Min(containerWeight, inventoryWeight);
        }

        private int AdjustAllStacks(int itemId, int amount)
        {
            int added = 0;
            for (int i = 0; i < _slots.Length; ++i)
            {
                ref var stack = ref _slots[i];
                if (stack.Item?.Id != itemId)
                    continue;

                added += AdjustStackAtIndex(i, amount);

                // We've added all the items, we can stop now
                if (added == amount)
                    return added;
            }

            return added;
        }

        IEnumerator<ItemStack> IEnumerable<ItemStack>.GetEnumerator() => ((IEnumerable<ItemStack>)_slots).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _slots.GetEnumerator();

        public override string ToString() => $"Name: {_containerName} | Slots: {_slots.Length} | Weight: {_weight}/{_maximumWeight}";

        public void OnDeserialization(object sender)
        {
            _slotChangedActions = new SlotChangedDelegate[_slots.Length];

            float totalWeight = 0f;
            for (int i = 0; i < _slots.Length; ++i)
            {
                ref var stack = ref _slots[i];
                totalWeight += stack.GetTotalWeight();
            }
            _weight = totalWeight;
        }
    }
}