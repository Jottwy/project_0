using System;

namespace PolymindGames.InventorySystem
{
    public static partial class ItemSorters
    {
        public static readonly Comparison<ItemStack> ByName = (a, b) => CompareName(a.Item?.Definition.Name, b.Item?.Definition.Name);
        public static readonly Comparison<ItemStack> ByRarity = (a, b) => b.Item.Definition.Rarity.RarityValue.CompareTo(a.Item.Definition.Rarity.RarityValue);
        public static readonly Comparison<ItemStack> ByWeight = (a, b) => a.GetTotalWeight().CompareTo(b.GetTotalWeight());
        public static readonly Comparison<ItemStack> ByStack = (a, b) => a.Count.CompareTo(b.Count);
        public static readonly Comparison<ItemStack> ByFullName = (a, b) => CompareName(a.Item?.Definition.FullName, b.Item?.Definition.FullName);

        private static int CompareName(string name1, string name2)
        {
            if (string.IsNullOrEmpty(name1))
                return 1;

            if (string.IsNullOrEmpty(name2))
                return -1;

            return string.Compare(name1, name2, StringComparison.InvariantCultureIgnoreCase);
        }
    }
}