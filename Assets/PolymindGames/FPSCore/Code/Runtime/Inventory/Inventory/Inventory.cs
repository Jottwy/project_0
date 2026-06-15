using System.Collections.Generic;
using PolymindGames.SaveSystem;
using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    [DisallowMultipleComponent]
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/player/modules-and-behaviours/inventory")]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault3)]
    public class Inventory : MonoBehaviour, IInventory, ISaveableComponent
    {
        [SerializeField, Range(0f, 1000f)]
        [Tooltip("The maximum weight capacity of the inventory.")]
        private float _maxWeight = 30f;

        [SerializeField]
        private ItemAction _dropAction;

        [SpaceArea]
        [SerializeField, LabelByChild("Name"), ReorderableList]
        [Tooltip("The startup data for initializing the inventory.")]
        private ItemContainerGenerator[] _defaultContainers;

        [NonSerialized]
        private ItemContainer[] _containers;
        private ICharacter _character;
        private float? _weight;

        /// <inheritdoc/>
        public IReadOnlyList<IItemContainer> Containers => _containers;

        /// <inheritdoc/>
        public float Weight
        {
            get
            {
                if (_weight == null)
                {
                    _weight = 0f;
                    foreach (var container in _containers)
                        _weight += container.Weight;
                }

                return _weight.Value;
            }
        }

        /// <inheritdoc/>
        public float MaxWeight => _maxWeight;

        /// <inheritdoc/>
        public event UnityAction Changed;

        /// <inheritdoc/>
        public event SlotChangedDelegate SlotChanged;

        /// <inheritdoc/>
        public IItemContainer FindContainer(Func<IItemContainer, bool> filter)
        {
            foreach (var container in _containers)
            {
                if (filter(container))
                    return container;
            }
            
            return null;
        }

        /// <inheritdoc/>
        public List<IItemContainer> FindContainers(Func<IItemContainer, bool> filter)
        {
            var list = new List<IItemContainer>();
            foreach (var container in _containers)
            {
                if (filter(container))
                    list.Add(container);
            }
            
            return list;
        }

        /// <inheritdoc/>
        public (int addedCount, string rejectReason) AddItem(ItemStack stack)
        {
            int addedCount = 0;
            string rejectReason = ContainerRestriction.InventoryFullRejection;
            foreach (var container in _containers)
            {
                (int added, string reject) = container.AddItem(stack);

                rejectReason = reject;
                addedCount += added;

                if (addedCount == stack.Count)
                    break;
            }

            return (addedCount, rejectReason);
        }

        /// <inheritdoc/>
        public (int addedCount, string rejectReason) AddItemsById(int itemId, int amount)
        {
            int addedCount = 0;
            string rejectReason = ContainerRestriction.InventoryFullRejection;
            foreach (var container in _containers)
            {
                (int added, string reject) = container.AddItemsById(itemId, amount - addedCount);

                rejectReason = reject;
                addedCount += added;

                if (addedCount == amount)
                    break;
            }

            return (addedCount, rejectReason);
        }

        /// <inheritdoc/>
        public bool ContainsItem(Item item)
        {
            foreach (var container in _containers)
            {
                if (container.ContainsItem(item))
                    return true;
            }

            return false;
        }

        /// <inheritdoc/>
        public int RemoveItem(ItemStack stack)
        {
            foreach (var container in _containers)
            {
                int removedCount = container.RemoveItem(stack);
                Debug.Log(removedCount);
                if (removedCount != 0)
                    return removedCount;
            }

            return 0;
        }

        /// <inheritdoc/>
        public int RemoveItemsById(int id, int amount)
        {
            if (amount <= 0)
                return 0;

            int removedCount = 0;
            foreach (var container in _containers)
            {
                int removedNow = container.RemoveItemsById(id, amount);
                removedCount += removedNow;

                if (removedCount == amount)
                    break;
            }

            return removedCount;
        }

        /// <inheritdoc/>
        public int RemoveItems(Func<Item, bool> filter, int amount)
        {
            if (amount <= 0)
                return 0;

            int removedCount = 0;
            foreach (var container in _containers)
            {
                int removedNow = container.RemoveItems(filter, amount);
                removedCount += removedNow;

                if (removedNow == removedCount)
                    break;
            }

            return removedCount;
        }

        /// <inheritdoc/>
        public bool ContainsItemById(int itemId)
        {
            foreach (var container in _containers)
            {
                if (container.ContainsItemById(itemId))
                    return true;
            }

            return false;
        }

        /// <inheritdoc/>
        public bool ContainsItem(Func<Item, bool> filter)
        {
            foreach (var container in _containers)
            {
                if (container.ContainsItem(filter))
                    return true;
            }

            return false;
        }

        /// <inheritdoc/>
        public int GetItemCountById(int itemId)
        {
            int totalCount = 0;
            foreach (var container in _containers)
            {
                totalCount += container.GetItemCountById(itemId);
            }

            return totalCount;
        }

        /// <inheritdoc/>
        public int GetItemCount(Func<Item, bool> filter)
        {
            int totalCount = 0;
            foreach (var container in _containers)
            {
                totalCount += container.GetItemCount(filter);
            }

            return totalCount;
        }

        /// <inheritdoc/>
        public virtual void DropItem(ItemStack stack)
        {
            if (!stack.HasItem() || _dropAction == null)
                return;

            SlotReference parentSlot = SlotReference.Null;
            foreach (var container in _containers)
            {
                var slot = container.FindSlot(ItemSlotFilters.WithItem(stack.Item));
                if (slot.IsValid())
                {
                    parentSlot = slot;
                    break;
                }
            }

            _dropAction.Perform(_character, parentSlot, stack);
        }

        /// <inheritdoc/>
        public void SortItems(Comparison<ItemStack> comparison)
        {
            foreach (var container in _containers)
            {
                container.SortItems(comparison);
            }
        }

        /// <inheritdoc/>
        public void Clear()
        {
            foreach (var container in _containers)
            {
                container.Clear();
            }
        }

        /// <summary>
        /// Initializes the inventory system, setting up default item containers and retrieving the associated character.
        /// </summary>
        protected virtual void Start()
        {
            _character = GetComponentInParent<ICharacter>();

            if (_containers == null)
            {
                _containers = CreateDefaultContainers();
                PopulateDefaultContainers(_containers);
            }
        }

        /// <summary>
        /// Populates the provided containers with predefined items and loot.
        /// </summary>
        /// <param name="containers">The item containers to populate.</param>
        private void PopulateDefaultContainers(ItemContainer[] containers)
        {
            for (int i = 0; i < _defaultContainers.Length; ++i)
            {
                _defaultContainers[i].PopulateContainerWithItems(containers[i]);
            }
        }

        /// <summary>
        /// Creates and returns the default item containers based on predefined settings.
        /// </summary>
        /// <returns>An array of newly created <see cref="ItemContainer"/> objects.</returns>
        private ItemContainer[] CreateDefaultContainers()
        {
            if (_defaultContainers.Length == 0)
            {
                Debug.LogWarning("No default containers found. Returning an empty array.");
                return Array.Empty<ItemContainer>();
            }

            var containers = new ItemContainer[_defaultContainers.Length];

            for (int i = 0; i < containers.Length; ++i)
            {
                var container = (ItemContainer)_defaultContainers[i].GenerateContainer(this, false);
                container.Changed += OnContainerChanged;
                container.SlotChanged += OnSlotChanged;
                containers[i] = container;
            }

            return containers;
        }

        /// <summary>
        /// Invoked when a container changes, propagating the event to subscribers.
        /// </summary>
        private void OnContainerChanged()
        {
            _weight = null;
            Changed?.Invoke();
        }

        /// <summary>
        /// Invoked when an item slot changes, propagating the event to subscribers.
        /// </summary>
        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType)
        {
            SlotChanged?.Invoke(slot, changeType);
        }

        #region Save & Load
        // ADR-009: STP save disabled — Rust is the single source of truth.
        void ISaveableComponent.LoadMembers(object data) { }
        object ISaveableComponent.SaveMembers() => null;
        #endregion
    }
}