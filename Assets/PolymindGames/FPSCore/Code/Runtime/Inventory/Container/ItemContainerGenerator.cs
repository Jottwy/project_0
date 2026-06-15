using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Generates an item container with specified properties and restrictions.
    /// </summary>
    [Serializable]
    public sealed class ItemContainerGenerator
#if UNITY_EDITOR
        : ISerializationCallbackReceiver
#endif
    {
        [Tooltip("The default name assigned to the generated container.")]
        public string Name = "Container";

        [Tooltip("Defines whether the container can hold multiple items in one stack or not.")]
        public bool AllowStacking = true;
        
        [Range(1, 100)]
        [Tooltip("Defines the number of item slots in the container (e.g., Holster: 6, Backpack: 25).")]
        public int MaxSlotCount = 1;

        [Range(1f, 1000f)]
        [Tooltip("Defines the maximum weight capacity of the container. A negative value means no weight limit.")]
        public float MaxWeightLimit = ItemContainer.Builder.MaxWeightLimit;

        [SpaceArea]
        [ReorderableList(ListStyle.Lined, HasLabels = false, Foldable = true)]
        [Tooltip("Defines restrictions that restrict what items can be stored in the container.")]
        public ContainerRestriction[] Restrictions;

        [IgnoreParent, SpaceArea]
        [ReorderableList(ListStyle.Lined, Foldable = true)]
        [Tooltip("A list of predefined item generators used to populate the container with initial items.")]
        public ItemGenerator[] PredefinedItems;

        [SpaceArea]
        [Tooltip("The loot table used to generate additional random items for this container.")]
        public LootTable LootTable;

        [BeginIndent]
        [SerializeField, Range(0.1f, 10f)]
        [HideIf(nameof(LootTable), false)]
        public float RarityWeight = 1f;

        [SerializeField, Range(0, 255)]
        [HideIf(nameof(LootTable), false)]
        [Tooltip("The minimum number of items that can be generated from the loot table.")]
        public byte MinLootCount;

        [EndIndent]
        [SerializeField, Range(1, 255)]
        [HideIf(nameof(LootTable), false)]
        [Tooltip("The maximum number of items that can be generated from the loot table.")]
        public byte MaxLootCount;

        /// <summary>
        /// Creates and initializes an item container with specified properties, inventory association, and optional predefined items.
        /// </summary>
        /// <param name="inventory">The inventory to associate with the container.</param>
        /// <param name="populateWithItems">Determines whether predefined and loot table items should be added.</param>
        /// <param name="customName">Optional custom name for the container. Defaults to the container's standard name if null or empty.</param>
        /// <returns>A newly created and initialized item container.</returns>
        public IItemContainer GenerateContainer(IInventory inventory, bool populateWithItems = true, string customName = null)
        {
            var container = new ItemContainer.Builder()
                .WithInventory(inventory)
                .WithName(string.IsNullOrEmpty(customName) ? Name : customName)
                .WithMaxWeight(MaxWeightLimit)
                .WithAllowStacking(AllowStacking)
                .WithSize(MaxSlotCount)
                .WithRestrictions(Restrictions)
                .Build();

            if (populateWithItems)
                PopulateContainerWithItems(container);

            return container;
        }

        /// <summary>
        /// Populates the given item container with predefined items and, if applicable, loot table items.
        /// </summary>
        /// <param name="container">The item container to populate.</param>
        public void PopulateContainerWithItems(IItemContainer container)
        {
            // Add predefined items
            foreach (var itemGenerator in PredefinedItems)
                container.AddItem(itemGenerator.GenerateItem(Restrictions));

            // Add randomized loot from the loot table, if available
            if (LootTable != null)
                LootTable.AddLootToContainer(container, UnityEngine.Random.Range(MinLootCount, MaxLootCount + 1), RarityWeight);
        }

#if UNITY_EDITOR
        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            MaxSlotCount = Mathf.Max(1, MaxSlotCount);
            MinLootCount = (byte)Mathf.Max(MinLootCount, 0);
            MaxLootCount = (byte)Mathf.Max(MaxLootCount, 1, MinLootCount);
        }
#endif
    }
}