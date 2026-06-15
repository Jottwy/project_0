using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents a restriction where items must have a specific type of action associated with them to be added.
    /// </summary>
    [CreateAssetMenu(menuName = CreateMenuPath + "Item Action Type Restriction")]
    public sealed class ItemActionTypeAddRule : ContainerRestriction
    {
        [SerializeField, ClassImplements(typeof(ItemAction), AllowAbstract = false)]
        private SerializedType _type;

        private ItemActionTypeAddRule() { }
    
        /// <summary>
        /// Factory method to create an instance of <see cref="ItemActionTypeAddRule"/> for a specific data type.
        /// </summary>
        /// <param name="actionType">The data type that must be associated with items to be added.</param>
        /// <returns>A new instance of <see cref="ItemActionTypeAddRule"/>.</returns>
        /// <exception cref="ArgumentException">Thrown when the type does not implement <see cref="ItemDataOld"/>.</exception>
        public static ItemActionTypeAddRule Create(Type actionType)
        {
            if (!typeof(ItemAction).IsAssignableFrom(actionType))
            {
                throw new ArgumentException($"The type '{actionType.FullName}' does not implement '{typeof(ItemData).FullName}'.", nameof(actionType));
            }
    
            var instance = CreateInstance<ItemActionTypeAddRule>();
            instance._type = new SerializedType(actionType);
            return instance;
        }

        public Type ActionType => _type;
    
        /// <inheritdoc/>
        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount)
        {
            return AllowsItem(item) ? requestedCount : 0;
        }

        private bool AllowsItem(Item item)
        {
            return item.Definition.HasActionOfType(_type.Type);
        }
    }
}