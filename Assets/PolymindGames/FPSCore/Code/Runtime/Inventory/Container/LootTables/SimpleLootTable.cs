using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// A simple implementation of a loot table that generates random items from predefined options.
    /// </summary>
    [CreateAssetMenu(menuName = CreateMenuName + "Simple Loot Table")]
    public sealed class SimpleLootTable : LootTable
    {
        [SerializeField, ReorderableList, IgnoreParent]
        [Tooltip("The list of possible items and their probabilities.")]
        private LootEntry[] _lootEntries = Array.Empty<LootEntry>();

        [SerializeField, HideInInspector]
        private float _totalProbability;

        /// <inheritdoc />
        public override List<ItemStack> GenerateLoot(IReadOnlyList<ContainerRestriction> restrictions, int amount, float rarityWeight = 1f)
        {
            if (_lootEntries.Length == 0 || amount <= 0)
                return new List<ItemStack>();

            var loot = new List<ItemStack>();
            for (int i = 0; i < amount; i++)
            {
                var stack = GetRandomItemStack(restrictions, rarityWeight);
                if (stack.HasItem())
                    loot.Add(stack);
            }

            return loot;
        }

        public override ItemStack GenerateLoot(IReadOnlyList<ContainerRestriction> restrictions, float rarityWeight = 1)
        {
            return _lootEntries.Length == 0
                ? ItemStack.Null
                : GetRandomItemStack(restrictions, rarityWeight);
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
            var restrictions = container.Restrictions;

            for (int i = 0; i < amount; i++)
            {
                var stack = GetRandomItemStack(restrictions);
                if (stack.HasItem())
                    addedCount += container.AddItem(stack).addedCount;
            }

            return addedCount;
        }

        /// <summary>
        /// Selects a random item based on the probabilities defined in the loot table and validates against add rules.
        /// </summary>
        /// <param name="restrictions">The rules to validate the item against.</param>
        /// <param name="rarityWeight">TODO: Implement rarity weight</param>
        /// <returns>A randomly selected item or null if no valid item is found.</returns>
        private ItemStack GetRandomItemStack(IReadOnlyList<ContainerRestriction> restrictions, float rarityWeight = 1f)
        {
            float randomValue = UnityEngine.Random.Range(0f, _totalProbability);
            float cumulativeProbability = 0f;

            foreach (var entry in _lootEntries)
            {
                cumulativeProbability += entry.Probability;
                if (randomValue <= cumulativeProbability)
                {
                    var stack = entry.Item.GenerateItem(restrictions);
                    if (!stack.HasItem())
                        continue;
                    
                    return stack;
                }
            }

            return default(ItemStack);
        }

        #region Internal Types
        [Serializable]
        private sealed class LootEntry
        {
            [IgnoreParent]
            [Tooltip("The item that can be looted.")]
            public ItemGenerator Item;

            [Suffix("%"), Range(0f, 100f), NewLabel(" ")]
            [Tooltip("The relative chance of this item being looted.")]
            public float Probability = 1f;
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            _totalProbability = _lootEntries.Sum(entry => entry.Probability);
        }
#endif
        #endregion
    }
}