using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Base class for item restrictions that determine the conditions under which an item can be added to a container or inventory.
    /// </summary>
    [Serializable]
    public abstract class ContainerRestriction : ScriptableObject
    {
        [SerializeField]
        private string _rejectionReason;

        public const string InventoryFullRejection = "Inventory is full";
        public const string WeightLimitRejection = "Cannot carry more weight";
        public const string ItemNullRejection = "Item Is null";
        protected const string CreateMenuPath = "Polymind Games/Items/Restrictions/";

        /// <summary>
        /// Gets a message explaining why the item cannot be added.
        /// </summary>
        public string RejectionReason
        {
            get => _rejectionReason ?? InventoryFullRejection;
            protected set => _rejectionReason = value;
        }

        /// <summary>
        /// Calculates the maximum number of items that can be added to the specified container based on the given item and requested amount.
        /// </summary>
        /// <param name="container">The container where the item is being added.</param>
        /// <param name="item">The item being added.</param>
        /// <param name="requestedCount">The number of items the user wishes to add.</param>
        /// <returns>The maximum number of items that can be added.</returns>
        public abstract int GetAllowedCount(IItemContainer container, Item item, int requestedCount);
    }
}