using System.Collections.Generic;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents a loot table that generates and distributes items based on predefined rules.
    /// </summary>
    public abstract class LootTable : ScriptableObject
    {
        protected const string CreateMenuName = "Polymind Games/Items/Loot Tables/";

        /// <summary>
        /// Generates a collection of items based on the loot table rules and the given restrictions.
        /// </summary>
        /// <param name="restrictions">A list of rules that determine how items can be added to a container.</param>
        /// <param name="amount">The total number of item stacks to generate.</param>
        /// <param name="rarityWeight">
        /// A multiplier that affects item rarity selection. Values greater than 1 increase the likelihood 
        /// of rarer items appearing, while values less than 1 favor common items. Default is 1 (no adjustment).
        /// </param>
        /// <returns>A list of generated item stacks.</returns>
        public abstract List<ItemStack> GenerateLoot(IReadOnlyList<ContainerRestriction> restrictions, int amount, float rarityWeight = 1f);

        /// <summary>
        /// Generates a single item based on the loot table rules and the given restrictions.
        /// </summary>
        /// <param name="restrictions">A list of rules that determine how the item can be added to a container.</param>
        /// <param name="rarityWeight">
        /// A multiplier that affects item rarity selection. Values greater than 1 increase the likelihood 
        /// of rarer items appearing, while values less than 1 favor common items. Default is 1 (no adjustment).
        /// </param>
        /// <returns>The generated item stack.</returns>
        public abstract ItemStack GenerateLoot(IReadOnlyList<ContainerRestriction> restrictions, float rarityWeight = 1f);

        /// <summary>
        /// Generates loot and attempts to add it directly to the specified inventory.
        /// </summary>
        /// <param name="inventory">The inventory to receive the generated items.</param>
        /// <param name="amount">The total number of item stacks to generate and add.</param>
        /// <param name="rarityWeight">
        /// A multiplier that affects item rarity selection. Values greater than 1 increase the likelihood 
        /// of rarer items appearing, while values less than 1 favor common items. Default is 1 (no adjustment).
        /// </param>
        /// <returns>The number of item stacks successfully added to the inventory.</returns>
        public abstract int AddLootToInventory(IInventory inventory, int amount, float rarityWeight = 1f);

        /// <summary>
        /// Generates loot and attempts to add it directly to the specified item container.
        /// </summary>
        /// <param name="container">The container to receive the generated items.</param>
        /// <param name="amount">The total number of item stacks to generate and add.</param>
        /// <param name="rarityWeight">
        /// A multiplier that affects item rarity selection. Values greater than 1 increase the likelihood 
        /// of rarer items appearing, while values less than 1 favor common items. Default is 1 (no adjustment).
        /// </param>
        /// <returns>The number of item stacks successfully added to the container.</returns>
        public abstract int AddLootToContainer(IItemContainer container, int amount, float rarityWeight = 1f);
    }

}