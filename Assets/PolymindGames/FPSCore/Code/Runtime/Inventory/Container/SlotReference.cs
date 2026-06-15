using System.Collections.Generic;
using System.Collections;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Specifies the type of change that occurred in an inventory slot.
    /// </summary>
    public enum SlotChangeType
    {
        /// <summary>
        /// The item in the slot was changed (e.g., replaced with a different item or removed).
        /// </summary>
        ItemChanged,

        /// <summary>
        /// The count of items in the slot was changed (e.g., items were added or removed from the stack).
        /// </summary>
        CountChanged
    }

    /// <summary>
    /// Represents a method that handles changes to an item slot, such as when the item or its quantity changes.
    /// </summary>
    /// <param name="args">A reference to the slot where the change occurred.</param>
    /// <param name="changeType">The type of change that occurred (item or count).</param>
    public delegate void SlotChangedDelegate(in SlotReference args, SlotChangeType changeType);

    /// <summary>
    /// Represents a reference to a specific slot within an item container.
    /// </summary>
    public readonly struct SlotReference : IEquatable<SlotReference>
    {
        /// <summary>
        /// The container that this slot belongs to.
        /// </summary>
        public readonly IItemContainer Container;

        /// <summary>
        /// The index of the slot within the container.
        /// </summary>
        public readonly int Index;

        /// <summary>
        /// Represents an invalid or unassigned slot reference.
        /// </summary>
        public static readonly SlotReference Null = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="SlotReference"/> struct.
        /// </summary>
        /// <param name="container">The container that holds the slot.</param>
        /// <param name="index">The index of the slot within the container.</param>
        /// <exception cref="ArgumentNullException">Thrown when the container is null.</exception>
        /// <exception cref="IndexOutOfRangeException">Thrown when the index is out of range.</exception>
        public SlotReference(IItemContainer container, int index)
        {
            Container = container ?? throw new ArgumentNullException(nameof(container));
            Index = index < container.SlotsCount ? index : throw new IndexOutOfRangeException(nameof(index));
        }

        /// <summary>
        /// Event triggered when a change occurs in this slot.
        /// </summary>
        public event SlotChangedDelegate Changed
        {
            add => Container?.AddSlotChangedListener(Index, value);
            remove => Container?.RemoveSlotChangedListener(Index, value);
        }

        /// <summary>
        /// Gets the item stack currently in the referenced slot.
        /// </summary>
        /// <returns>The item stack in the slot, or an empty stack if invalid.</returns>
        public ItemStack GetStack() => Container?.GetItemAtIndex(Index) ?? ItemStack.Null;
        
        /// <summary>
        /// Gets the item stack count currently in the referenced slot.
        /// </summary>
        /// <returns>The item stack count in the slot, or 0 if invalid.</returns>
        public int GetCount() => Container?.GetItemAtIndex(Index).Count ?? 0;

        /// <summary>
        /// Gets the item currently in the referenced slot.
        /// </summary>
        /// <returns>The item in the slot, or null if the slot is empty or invalid.</returns>
        public Item GetItem() => Container?.GetItemAtIndex(Index).Item;

        /// <summary>
        /// Sets an item stack in the referenced slot.
        /// </summary>
        /// <param name="stack">The item stack to place in the slot.</param>
        /// <returns>The number of items successfully set in the slot.</returns>
        public int SetItem(ItemStack stack) => Container?.SetItemAtIndex(Index, stack) ?? 0;

        /// <summary>
        /// Adjusts the item count of the stack by a given amount.
        /// </summary>
        /// <param name="adjustment">The amount to adjust the item count by.</param>
        /// <returns>The remaining item count after the adjustment.</returns>
        public int AdjustStack(int adjustment) => Container?.AdjustStackAtIndex(Index, adjustment) ?? 0;

        /// <summary>
        /// Clears the slot by setting it to an empty item stack.
        /// </summary>
        public void Clear() => SetItem(ItemStack.Null);

        /// <summary>
        /// Attempts to get the item in the slot.
        /// </summary>
        /// <param name="item">The item found in the slot, or null if empty.</param>
        /// <returns>True if an item exists in the slot, otherwise false.</returns>
        public bool TryGetItem(out Item item)
        {
            item = GetItem();
            return item != null;
        }

        /// <summary>
        /// Attempts to get the item stack in the slot.
        /// </summary>
        /// <param name="stack">The item stack found in the slot.</param>
        /// <returns>True if the stack is valid (not empty), otherwise false.</returns>
        public bool TryGetStack(out ItemStack stack)
        {
            stack = GetStack();
            return stack.HasItem();
        }

        /// <summary>
        /// Determines whether this slot reference is valid.
        /// </summary>
        /// <returns>True if the slot reference is valid, otherwise false.</returns>
        public bool IsValid() => Container != null;

        /// <summary>
        /// Checks if the referenced slot is empty.
        /// </summary>
        public bool HasItem() => GetItem() != null;

        public static bool operator ==(SlotReference x, SlotReference y) => x.Container == y.Container && x.Index == y.Index;
        public static bool operator !=(SlotReference x, SlotReference y) => x.Container != y.Container || x.Index != y.Index;

        public bool Equals(SlotReference other) => Equals(Container, other.Container) && Index == other.Index;

        public override bool Equals(object obj) => obj is SlotReference other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Container, Index);

        public override string ToString()
        {
            var item = GetStack();
            string itemStr = item.HasItem() ? $"Item: {item}" : "No Item";
            return $"Container: {Container.Name}, Index: {Index}, {itemStr}";
        }
    }
    
    public struct SlotEnumerator : IEnumerator<SlotReference>
    {
        private readonly IItemContainer _container;
        private int _currentIndex;

        public SlotEnumerator(IItemContainer container)
        {
            _container = container;
            _currentIndex = -1;
        }

        public SlotReference Current => new(_container, _currentIndex);
        object IEnumerator.Current => Current;

        public bool MoveNext() => ++_currentIndex < _container.SlotsCount;
        public void Reset() => _currentIndex = -1;
        public void Dispose() { }
    }
        
    public readonly struct SlotEnumerable : IEnumerable<SlotReference>
    {
        private readonly IItemContainer _container;

        public SlotEnumerable(IItemContainer container) => _container = container;

        private SlotEnumerator GetEnumerator() => new(_container);
    
        IEnumerator<SlotReference> IEnumerable<SlotReference>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}