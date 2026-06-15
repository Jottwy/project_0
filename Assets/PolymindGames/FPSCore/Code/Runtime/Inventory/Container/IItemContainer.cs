using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents a container that holds a collection of item slots, allowing for item management, addition, removal, and queries.
    /// </summary>
    public interface IItemContainer : IEnumerable<ItemStack>
    {
        /// <summary>
        /// Gets the list of rules that determine how items can be added to the container.
        /// </summary>
        IReadOnlyList<ContainerRestriction> Restrictions { get; }

        /// <summary>
        /// Gets the inventory to which this container belongs.
        /// </summary>
        IInventory Inventory { get; }

        /// <summary>
        /// Gets the name of the container.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the maximum capacity of the container.
        /// </summary>
        int SlotsCount { get; }

        /// <summary>
        /// Gets the current weight of items in the container.
        /// </summary>
        float Weight { get; }

        /// <summary>
        /// Gets the maximum weight capacity of the container.
        /// </summary>
        float MaxWeight { get; }

        /// <summary>
        /// Occurs when the container's state has changed.
        /// </summary>
        event UnityAction Changed;

        /// <summary>
        /// Occurs when any item slot in the container has changed.
        /// </summary>
        event SlotChangedDelegate SlotChanged;

        /// <summary>
        /// Adds a listener for slot change events at the specified index.
        /// </summary>
        /// <param name="index">The index of the slot to listen to.</param>
        /// <param name="callback">The callback to invoke when the slot changes.</param>
        void AddSlotChangedListener(int index, SlotChangedDelegate callback);

        /// <summary>
        /// Removes a listener for slot change events at the specified index.
        /// </summary>
        /// <param name="index">The index of the slot to stop listening to.</param>
        /// <param name="callback">The callback to remove.</param>
        void RemoveSlotChangedListener(int index, SlotChangedDelegate callback);

        /// <summary>
        /// Retrieves the item stack at the specified index.
        /// </summary>
        /// <param name="index">The index of the slot to retrieve.</param>
        /// <returns>The item stack at the specified slot.</returns>
        ItemStack GetItemAtIndex(int index);

        /// <summary>
        /// Sets a new item stack at the specified index, replacing any existing stack.
        /// </summary>
        /// <param name="index">The index of the slot to update.</param>
        /// <param name="newStack">The new item stack to set.</param>
        /// <returns>The previous item count in the slot.</returns>
        int SetItemAtIndex(int index, ItemStack newStack);

        /// <summary>
        /// Adjusts the item count of the stack at the specified index by a given amount.
        /// </summary>
        /// <param name="index">The index of the slot to adjust.</param>
        /// <param name="adjustment">The amount to adjust the item count by.</param>
        /// <returns>The remaining item count after the adjustment.</returns>
        int AdjustStackAtIndex(int index, int adjustment);

        /// <summary>
        /// Adds an item to the container and returns the added count and any rejection context.
        /// </summary>
        (int addedCount, string rejectReason) AddItem(ItemStack stack);

        /// <summary>
        /// Adds multiple items with the specified ID to the container.
        /// </summary>
        (int addedCount, string rejectReason) AddItemsById(int itemId, int amount);
        
        /// <summary>
        /// Checks how many of a specific item can be added and provides a rejection reason if it cannot.
        /// </summary>
        (int allowedCount, string rejectReason) GetAllowedCount(ItemStack stack);

        /// <summary>
        /// Checks if a specific item is contained in the container.
        /// </summary>
        bool ContainsItem(Item item);
        
        /// <summary>
        /// Removes a specific item and its stack from the container.
        /// </summary>
        int RemoveItem(ItemStack stack);

        /// <summary>
        /// Removes a specified amount of items matching the provided ID.
        /// </summary>
        int RemoveItemsById(int itemId, int amount) => RemoveItems(ItemFilters.WithId(itemId), amount);

        /// <summary>
        /// Removes a specified amount of items matching the provided filter.
        /// </summary>
        int RemoveItems(Func<Item, bool> filter, int amount);

        /// <summary>
        /// Checks if the container contains at least one item matching the specified ID.
        /// </summary>
        bool ContainsItemById(int itemId) => ContainsItem(ItemFilters.WithId(itemId));

        /// <summary>
        /// Checks if the container contains at least one item matching the provided filter.
        /// </summary>
        bool ContainsItem(Func<Item, bool> filter);

        /// <summary>
        /// Gets the total count of items that match the specified ID.
        /// </summary>
        int GetItemCountById(int itemId) => GetItemCount(ItemFilters.WithId(itemId));

        /// <summary>
        /// Gets the total count of items that match the provided filter.
        /// </summary>
        int GetItemCount(Func<Item, bool> filter);

        /// <summary>
        /// Sorts the items in the container according to the specified comparison.
        /// </summary>
        void SortItems(Comparison<ItemStack> comparison);

        /// <summary>
        /// Clears all items from the container.
        /// </summary>
        void Clear();
        
        /// <summary>
        /// The maximum number of slots any container can have. 
        /// </summary>
        const int MaxSlotsCount = 128;
    }
    
    public static class ItemContainerExtensions
    {
        /// <summary>
        /// Returns a garbage-free enumerable for iterating through slots.
        /// </summary>
        public static SlotEnumerable GetSlots(this IItemContainer container) => new(container);
        
        public static SlotReference FindSlot(this IItemContainer container, Func<SlotReference, bool> filter)
        {
            for (int i = 0; i < container.SlotsCount; i++)
            {
                var slot = container.GetSlot(i);
                if (filter(slot))
                    return slot;
            }

            return default(SlotReference);
        }

        public static SlotReference GetSlot(this IItemContainer container, int index) => new(container, index);

        public static T GetRestriction<T>(this IItemContainer container) where T : ContainerRestriction
        {
            foreach (var restriction in container.Restrictions)
            {
                if (restriction is T containerRestriction)
                    return containerRestriction;
            }

            return null;
        }
        
        public static bool HasRestriction<T>(this IItemContainer container) where T : ContainerRestriction
        {
            return container.GetRestriction<T>() != null;
        }

        public static bool TryGetRestriction<T>(this IItemContainer container, out T addRule) where T : ContainerRestriction
        {
            addRule = container.GetRestriction<T>();
            return addRule != null;
        }

        /// <summary>
        /// Determines if the container has a specific tag.
        /// </summary>
        public static bool HasTag(this IItemContainer container, DataIdReference<ItemTagDefinition> tag)
        {
            return container.TryGetRestriction<TagContainerRestriction>(out var tagAddRule)
                   && tagAddRule.Tags.Contains(tag);
        }

        /// <summary>
        /// Checks if the container is full (i.e. all slots are occupied).
        /// </summary>
        public static bool IsFull(this IItemContainer container)
        {
            for (int i = 0; i < container.SlotsCount; i++)
            {
                if (!container.GetItemAtIndex(i).HasItem())
                    return false;
            }
            
            return true;
        }

        /// <summary>
        /// Checks if the container is empty (i.e. all slots are unoccupied).
        /// </summary>
        public static bool IsEmpty(this IItemContainer container) => container.Weight < Mathf.Epsilon;
    }
}