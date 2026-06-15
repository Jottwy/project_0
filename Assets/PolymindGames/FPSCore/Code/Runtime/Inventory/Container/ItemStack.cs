using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents a stack of items, containing an item type and a quantity.
    /// </summary>
    [Serializable]
    public struct ItemStack
    {
        /// <summary>
        /// The type of item in this stack.
        /// </summary>
        public Item Item;

        /// <summary>
        /// The number of items in this stack.
        /// </summary>
        public int Count;

        /// <summary>
        /// Represents an empty or null item stack.
        /// </summary>
        public static readonly ItemStack Null = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemStack"/> struct.
        /// </summary>
        /// <param name="item">The item type in the stack.</param>
        /// <param name="stackCount">The number of items in the stack. Defaults to 1.</param>
        public ItemStack(Item item, int stackCount = 1)
        {
            Item = item;
            Count = item == null ? 0 : Mathf.Clamp(stackCount, 0, item.StackSize);
        }

        /// <summary>
        /// Determines whether this item stack is valid (i.e., has a valid item and a positive count).
        /// </summary>
        /// <returns>True if the stack contains a valid item with a count greater than zero, otherwise false.</returns>
        public readonly bool HasItem() => Count > 0;

        /// <summary>
        /// Calculates the total weight of the items in this stack.
        /// </summary>
        /// <returns>The total weight of the stack, or 0 if the stack is invalid.</returns>
        public readonly float GetTotalWeight() => HasItem() ? Item.Weight * Count : 0;
        
        public override string ToString() => $"{Item} x {Count}";
        
        public static bool operator ==(ItemStack x, ItemStack y) => x.Item == y.Item && x.Count == y.Count;
        public static bool operator !=(ItemStack x, ItemStack y) => x.Item != y.Item || x.Count != y.Count;
        
        public readonly bool Equals(ItemStack other) => Equals(Item, other.Item) && Count == other.Count;
        
        public override bool Equals(object obj) => obj is ItemStack other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Item, Count);
    }
}