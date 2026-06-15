using System.Collections.Generic;
using UnityEngine.Events;
using System;
using System.Collections;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents an inventory system that contains multiple item containers, with functionality to manage items across all containers.
    /// </summary>
    public interface IInventory : IMonoBehaviour
    {
        /// <summary>
        /// Gets the list of item containers in the inventory.
        /// </summary>
        IReadOnlyList<IItemContainer> Containers { get; }

        /// <summary>
        /// Gets the current total weight of the items in the inventory.
        /// </summary>
        float Weight { get; }

        /// <summary>
        /// Gets the maximum weight capacity of the inventory.
        /// </summary>
        float MaxWeight { get; }

        /// <summary>
        /// Event triggered when the inventory is changed, such as an item being added or removed.
        /// </summary>
        event UnityAction Changed;

        /// <summary>
        /// Event triggered when an item slot in any container is changed, e.g., when an item is added, removed, or updated in a slot.
        /// </summary>
        event SlotChangedDelegate SlotChanged;

        /// <summary>
        /// Finds the first container that matches the provided filter.
        /// </summary>
        IItemContainer FindContainer(Func<IItemContainer, bool> filter);

        /// <summary>
        /// Finds all containers that match the provided filter.
        /// </summary>
        List<IItemContainer> FindContainers(Func<IItemContainer, bool> filter);

        /// <summary>
        /// Adds an item to the inventory, returns the added count and any reject context.
        /// </summary>
        (int addedCount, string rejectReason) AddItem(ItemStack stack);
        
        /// <summary>
        /// Adds multiple items with the specified ID to the inventory, returns the allowed added count and any reject context.
        /// </summary>
        (int addedCount, string rejectReason) AddItemsById(int itemId, int amount);

        /// <summary>
        /// Checks if a specific item is contained in the inventory.
        /// </summary>
        bool ContainsItem(Item item);
        
        /// <summary>
        /// Removes a specific item from the inventory.
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
        /// Checks if the inventory contains at least one item matching the specified ID.
        /// </summary>
        bool ContainsItemById(int itemId) => ContainsItem(ItemFilters.WithId(itemId));

        /// <summary>
        /// Checks if the inventory contains at least one item that matches the provided filter.
        /// </summary>
        bool ContainsItem(Func<Item, bool> filter);

        /// <summary>
        /// Gets the total count of items matching the specified ID.
        /// </summary>
        int GetItemCountById(int itemId) => GetItemCount(ItemFilters.WithId(itemId));

        /// <summary>
        /// Gets the total count of items that match the provided filter.
        /// </summary>
        int GetItemCount(Func<Item, bool> filter);

        /// <summary>
        /// Removes and drops the item in the world.
        /// </summary>
        void DropItem(ItemStack stack);

        /// <summary>
        /// Sorts the items in the inventory according to the provided criteria.
        /// </summary>
        void SortItems(Comparison<ItemStack> comparison);
        
        /// <summary>
        /// Clears all the slots.
        /// </summary>
        void Clear();
    }

    public sealed class DefaultInventory : IInventory
    {
        public DefaultInventory(GameObject parent)
        {
            gameObject = parent;
        }
        
        public GameObject gameObject { get; }
        public Transform transform => gameObject.transform;
        public bool enabled { get => true; set { } }

        public Coroutine StartCoroutine(IEnumerator routine)
            => CoroutineUtility.StartGlobalCoroutine(routine);
        
        /// <inheritdoc/>
        public IReadOnlyList<IItemContainer> Containers => Array.Empty<IItemContainer>();

        /// <inheritdoc/>
        public float Weight => 0f;
        
        /// <inheritdoc/>
        public float MaxWeight => 0f;

        /// <inheritdoc/>
        public event UnityAction Changed { add { } remove { } }
        
        /// <inheritdoc/>
        public event SlotChangedDelegate SlotChanged { add { } remove { } }

        /// <inheritdoc/>
        public IItemContainer FindContainer(Func<IItemContainer, bool> filter) => null;
        
        /// <inheritdoc/>
        public List<IItemContainer> FindContainers(Func<IItemContainer, bool> filter) => new();

        /// <inheritdoc/>
        public (int addedCount, string rejectReason) AddItem(ItemStack stack)
            => (0, ContainerRestriction.InventoryFullRejection);

        /// <inheritdoc/>
        public (int addedCount, string rejectReason) AddItemsById(int itemId, int count)
            => (0, ContainerRestriction.InventoryFullRejection);

        /// <inheritdoc/>
        public bool ContainsItem(Item item) => false;

        /// <inheritdoc/>
        public int RemoveItem(ItemStack stack) => 0;
        
        /// <inheritdoc/>
        public int RemoveItems(Func<Item, bool> filter, int amount) => 0;
        
        /// <inheritdoc/>
        public bool ContainsItem(Func<Item, bool> filter) => false;
        
        /// <inheritdoc/>
        public int GetItemCount(Func<Item, bool> filter) => 0;

        /// <inheritdoc/>
        public void DropItem(ItemStack stack) { }

        /// <inheritdoc/>
        public void SortItems(Comparison<ItemStack> comparison) { } 
        
        /// <inheritdoc/>
        public void Clear() { }
    }
}