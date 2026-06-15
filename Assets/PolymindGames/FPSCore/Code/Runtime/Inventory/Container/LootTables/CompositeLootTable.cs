using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    [CreateAssetMenu(menuName = CreateMenuName + "Composite Loot Table")]
    public sealed class CompositeLootTable : LootTable
    {
        [SerializeField, ReorderableList, IgnoreParent]
        [Tooltip("List of sub-loot tables with their weights.")]
        private SubLootTableEntry[] _subLootTables = Array.Empty<SubLootTableEntry>();

        [SerializeField, HideInInspector]
        private float _totalWeight;

        /// <inheritdoc />
        public override List<ItemStack> GenerateLoot(IReadOnlyList<ContainerRestriction> restrictions, int amount, float rarityWeight = 1f)
        {
            if (_subLootTables.Length == 0 || amount <= 0)
                return new List<ItemStack>();

            var loot = new List<ItemStack>();
            for (int i = 0; i < amount; i++)
            {
                var selectedLootTable = GetRandomLootTable();
                if (selectedLootTable != null)
                {
                    var stack = selectedLootTable.GenerateLoot(restrictions, rarityWeight);
                    if (stack.HasItem())
                        loot.Add(stack);
                }
            }

            return loot;
        }

        /// <inheritdoc />
        public override ItemStack GenerateLoot(IReadOnlyList<ContainerRestriction> restrictions, float rarityWeight = 1f)
        {
            var selectedLootTable = GetRandomLootTable();
            return selectedLootTable != null ? selectedLootTable.GenerateLoot(restrictions, rarityWeight) : ItemStack.Null;
        }

        /// <inheritdoc />
        public override int AddLootToInventory(IInventory inventory, int amount, float rarityWeight = 1f)
        {
            if (inventory == null || amount <= 0)
                return 0;

            int addedCount = 0;
            foreach (var container in inventory.Containers)
            {
                addedCount += AddLootToContainer(container, amount - addedCount, rarityWeight);
                if (addedCount >= amount)
                    break;
            }

            return addedCount;
        }

        /// <inheritdoc />
        public override int AddLootToContainer(IItemContainer container, int amount, float rarityWeight = 1f)
        {
            if (container == null || amount <= 0)
                return 0;

            int addedCount = 0;
            for (int i = 0; i < amount; i++)
            {
                var selectedLootTable = GetRandomLootTable();
                if (selectedLootTable != null)
                    addedCount += selectedLootTable.AddLootToContainer(container, 1, rarityWeight);
            }

            return addedCount;
        }

        /// <summary>
        /// Selects a random loot table based on weighted probabilities.
        /// </summary>
        /// <returns>The selected loot table.</returns>
        private LootTable GetRandomLootTable()
        {
            if (_subLootTables.Length == 0 || _totalWeight <= 0f)
                return null;

            float randomValue = UnityEngine.Random.Range(0f, _totalWeight);
            float cumulativeWeight = 0f;

            foreach (var entry in _subLootTables)
            {
                cumulativeWeight += entry.Weight;
                if (randomValue <= cumulativeWeight)
                    return entry.LootTable;
            }

            return null;
        }

        #region Internal Types
        [Serializable]
        private sealed class SubLootTableEntry
        {
            [InLineEditor]
            [Tooltip("The loot table to include in this composite table.")]
            public LootTable LootTable;

            [Suffix("%"), Range(0f, 100f), NewLabel(" ")]
            [Tooltip("The relative weight of this loot table compared to others.")]
            public float Weight = 1f;
        }
        #endregion
        
        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            _totalWeight = _subLootTables.Sum(entry => entry.Weight);
        }
#endif
        #endregion
    }
}