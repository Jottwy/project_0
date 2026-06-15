using UnityEngine;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents a rule where items must have specific properties to be added.
    /// </summary>
    [CreateAssetMenu(menuName = CreateMenuPath + "Property Restriction")]
    public sealed class PropertyContainerRestriction : ContainerRestriction
    {
        [SpaceArea(3f)]
        [DataReference(NullElement = "")]
        [SerializeField, ReorderableList(HasLabels = false)]
        private DataIdReference<ItemPropertyDefinition>[] _requiredProperties;

        private PropertyContainerRestriction() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="PropertyContainerRestriction"/> class with the required properties.
        /// </summary>
        public static PropertyContainerRestriction Create(params DataIdReference<ItemPropertyDefinition>[] requiredProperties)
        {
            var instance = CreateInstance<PropertyContainerRestriction>();
            instance._requiredProperties = requiredProperties;
            return instance;
        }
        
        /// <summary>
        /// Gets the required properties that items must have to be added.
        /// </summary>
        public DataIdReference<ItemPropertyDefinition>[] RequiredProperties => _requiredProperties;

        /// <inheritdoc/>
        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount)
        {
            return AllowsItem(item) ? requestedCount : 0;
        }
        
        private bool AllowsItem(Item item)
        {
            var definition = item.Definition;
            foreach (var property in _requiredProperties)
            {
                if (!definition.HasProperty(property))
                    return false;
            }

            return true;
        }
    }
}