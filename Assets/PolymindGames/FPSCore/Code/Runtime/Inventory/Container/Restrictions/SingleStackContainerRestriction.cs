using UnityEngine;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents a restriction where each item type can have only one stack in the inventory or container.
    /// </summary>
    [CreateAssetMenu(menuName = CreateMenuPath + "Single Stack Restriction")]
    public sealed class SingleStackContainerRestriction : ContainerRestriction
    {
        private static SingleStackContainerRestriction _default;
        public static SingleStackContainerRestriction Default => _default ??= Create();
    
        private SingleStackContainerRestriction() { }

        public static SingleStackContainerRestriction Create() => CreateInstance<SingleStackContainerRestriction>();

        /// <inheritdoc/>
        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount)
        {
            int count = container.GetItemCountById(item.Id);

            // Calculate the remaining space in the stack
            int remainingStack = item.StackSize - count;

            // Return the minimum between remaining space and requested count
            return Mathf.Min(remainingStack, requestedCount);
        }
    }
}