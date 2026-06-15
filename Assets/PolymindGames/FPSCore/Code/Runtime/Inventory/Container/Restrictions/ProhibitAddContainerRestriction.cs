using UnityEngine;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents a rule where items cannot be added only removed.
    /// </summary>
    [CreateAssetMenu(menuName = CreateMenuPath + "Prohibit Add Restriction")]
    public sealed class ProhibitAddContainerRestriction : ContainerRestriction
    {
        private static ProhibitAddContainerRestriction _default;
        public static ProhibitAddContainerRestriction Default => _default ??= Create();
        
        private ProhibitAddContainerRestriction() { }
        
        /// <inheritdoc/>
        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount) => 0;

        public static ProhibitAddContainerRestriction Create() => CreateInstance<ProhibitAddContainerRestriction>();
    }
}