namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents the result of attempting to add an item to a container or inventory.
    /// </summary>
    public enum ItemPickupAddResult
    {
        /// <summary>Failed to add the item.</summary>
        Failed = 0,

        /// <summary>Partially added the item (e.g., limited by container capacity).</summary>
        AddedPartial = 1,

        /// <summary>Successfully added the full item stack.</summary>
        AddedFull = 2
    }
    
    public static class ItemPickupUtility
    {
        /// <summary>
        /// Attempts to add an item to the given item container.
        /// </summary>
        public static ItemPickupAddResult PickUpItem(IItemContainer container, ItemStack stack, out MessageArgs messageArgs)
        {
            if (!stack.HasItem())
            {
                messageArgs = new MessageArgs(MsgType.Error, "Item instance is null.");
                return ItemPickupAddResult.Failed;
            }

            (int addedCount, string rejectReason) = container.AddItem(stack);

            if (addedCount > 0)
            {
                messageArgs = new MessageArgs(MsgType.Info, FormatPickupMessage(stack.Item, addedCount), stack.Item.Definition.Icon);
                return addedCount == stack.Count ? ItemPickupAddResult.AddedFull : ItemPickupAddResult.AddedPartial;
            }

            messageArgs = new MessageArgs(MsgType.Error, rejectReason);
            return ItemPickupAddResult.Failed;
        }

        /// <summary>
        /// Attempts to add an item to the given inventory.
        /// </summary>
        public static ItemPickupAddResult PickUpItem(IInventory inventory, ItemStack stack, out MessageArgs messageArgs)
        {
            if (!stack.HasItem())
            {
                messageArgs = new MessageArgs(MsgType.Error, "Item instance is null.");
                return ItemPickupAddResult.Failed;
            }
            
            (int addedCount, string rejectReason) = inventory.AddItem(stack);

            if (addedCount > 0)
            {
                messageArgs = new MessageArgs(MsgType.Info, FormatPickupMessage(stack.Item, addedCount), stack.Item.Definition.Icon);
                return addedCount == stack.Count ? ItemPickupAddResult.AddedFull : ItemPickupAddResult.AddedPartial;
            }

            messageArgs = new MessageArgs(MsgType.Error, rejectReason);
            return ItemPickupAddResult.Failed;
        }

        /// <summary>
        /// Formats the pickup message to include item name and count.
        /// </summary>
        private static string FormatPickupMessage(Item item, int addedCount)
        {
            return item.IsStackable ? $"Picked Up {item.Name} x {addedCount}" : $"Picked Up {item.Name}";
        }
    }
}