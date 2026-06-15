using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents a rule where items must belong to specific categories to be added.
    /// </summary>
    [CreateAssetMenu(menuName = CreateMenuPath + "Category Restriction")]
    public sealed class CategoryContainerRestriction : ContainerRestriction
    {
        [SpaceArea(3f)]
        [SerializeField, ReorderableList(HasLabels = false)]
        private DataIdReference<ItemCategoryDefinition>[] _validCategories
            = Array.Empty<DataIdReference<ItemCategoryDefinition>>();

        private CategoryContainerRestriction() { }
        
        public static CategoryContainerRestriction Create(DataIdReference<ItemCategoryDefinition>[] validCategories)
        {
            var instance = CreateInstance<CategoryContainerRestriction>();
            instance._validCategories = validCategories;
            return instance;
        }
        
        /// <summary>
        /// Gets the valid categories for this rule.
        /// </summary>
        public DataIdReference<ItemCategoryDefinition>[] ValidCategories => _validCategories;

        /// <inheritdoc/>
        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount)
        {
            return AllowsItem(item) ? requestedCount : 0;
        }

        private bool AllowsItem(Item item)
        {
            var definition = item.Definition;
            foreach (var category in _validCategories)
            {
                if (definition.ParentGroup != category.Def)
                    return false;
            }

            return true;
        }
    }
}