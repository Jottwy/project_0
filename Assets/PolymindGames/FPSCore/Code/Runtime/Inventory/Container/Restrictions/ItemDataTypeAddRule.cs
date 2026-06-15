using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents a restriction where items must have a specific type of data associated with them to be added.
    /// </summary>
    [CreateAssetMenu(menuName = CreateMenuPath + "Item Data Type Restriction")]
    public sealed class ItemDataTypeAddRule : ContainerRestriction
    {
        [SerializeField, ClassImplements(typeof(ItemData), AllowAbstract = false)]
        private SerializedType _type;
    
        private ItemDataTypeAddRule() { }
    
        /// <summary>
        /// Factory method to create an instance of <see cref="ItemDataTypeAddRule"/> for a specific data type.
        /// </summary>
        /// <param name="dataType">The data type that must be associated with items to be added.</param>
        /// <returns>A new instance of <see cref="ItemDataTypeAddRule"/>.</returns>
        /// <exception cref="ArgumentException">Thrown when the type does not implement <see cref="ItemDataOld"/>.</exception>
        public static ItemDataTypeAddRule Create(Type dataType)
        {
            if (!typeof(ItemData).IsAssignableFrom(dataType))
            {
                throw new ArgumentException($"The type '{dataType.FullName}' does not implement '{typeof(ItemData).FullName}'.", nameof(dataType));
            }
    
            var instance = CreateInstance<ItemDataTypeAddRule>();
            instance._type = new SerializedType(dataType);
            return instance;
        }

        public Type DataType => _type;
    
        /// <inheritdoc/>
        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount)
        {
            return AllowsItem(item) ? requestedCount : 0;
        }
        
        private bool AllowsItem(Item item)
        {
            return item.Definition.HasDataOfType(_type.Type);
        }
    }
}