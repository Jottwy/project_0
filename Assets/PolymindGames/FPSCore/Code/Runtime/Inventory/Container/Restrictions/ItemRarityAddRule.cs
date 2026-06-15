using UnityEngine;

namespace PolymindGames.InventorySystem
{
    [CreateAssetMenu(menuName = CreateMenuPath + "Item Rarity Restriction")]
    public sealed class ItemRarityAddRule : ContainerRestriction
    {
        [SpaceArea(3f)]
        [SerializeField, ReorderableList(HasLabels = false)]
        private ItemRarityLevel[] _allowedRarities;
        
        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount)
        {
            return AllowsItem(item) ? requestedCount : 0;
        }
        
        private bool AllowsItem(Item item)
        {
            return _allowedRarities.Contains(item.Definition.Rarity);
        }
    }
}