namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Delegate that defines a rule for calculating the allowed addition of items to a container.
    /// </summary>
    /// <param name="container">The container where the item is being added.</param>
    /// <param name="item">The item being added.</param>
    /// <param name="requestedCount">The number of items requested to be added.</param>
    /// <returns>The maximum number of items allowed to be added to the container.</returns>
    public delegate int ItemAddRuleDelegate(IItemContainer container, Item item, int requestedCount);

    /// <summary>
    /// Represents a custom rule for determining how many items can be added to a container.
    /// </summary>
    public sealed class CustomContainerRestriction : ContainerRestriction
    {
        private ItemAddRuleDelegate _ruleDelegate;

        private CustomContainerRestriction() { }

        /// <summary>
        /// Creates a new instance of <see cref="CustomContainerRestriction"/> with the specified delegate.
        /// </summary>
        /// <param name="ruleDelegate">The delegate that defines the rule for calculating allowed additions.</param>
        public static CustomContainerRestriction Create(ItemAddRuleDelegate ruleDelegate)
        {
            var instance = CreateInstance<CustomContainerRestriction>();
            instance._ruleDelegate = ruleDelegate;
            return instance;
        }

        /// <inheritdoc/>
        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount)
        {
            return _ruleDelegate(container, item, requestedCount);
        }
    }
}