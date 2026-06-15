using System.Collections.Generic;

namespace PolymindGames.InventorySystem
{
    public static class ItemTransfering
    {
        /// <summary>
        /// Moves the item from the current slot to the specified inventory.
        /// </summary>
        public static int TransferItemToInventory(this SlotReference slot, IInventory inventory)
        {
            if (!slot.HasItem())
                return 0;

            int addedCount = inventory.AddItem(slot.GetStack()).addedCount;
            slot.AdjustStack(-addedCount);

            return addedCount;
        }

        /// <summary>
        /// Transfers or swaps an item from the current slot to a container with a matching tag.
        /// Only transfers items if both the item and container share a compatible tag.
        /// </summary>
        public static bool TransferOrSwapToTaggedContainer(this SlotReference slot, IReadOnlyList<IItemContainer> targetContainers)
        {
            if (targetContainers.Count == 0)
                return false;
            
            var currentContainer = slot.Container;
            var itemTag = slot.GetItem().Definition.Tag;

            // Skip transfer if the item has no tag
            if (itemTag.IsNull)
                return false;

            foreach (var container in targetContainers)
            {
                if (container == currentContainer)
                    continue;

                // Transfer only if the container accepts the item's tag
                if (container.TryGetRestriction<TagContainerRestriction>(out var tagRestriction) &&
                    tagRestriction.Tags.Contains(itemTag))
                {
                    if (slot.TransferOrSwapWithContainer(container))
                        return true;
                }
            }

            return false;
        }


        /// <summary>
        /// Transfers an item from the current slot to the first available container without tag restrictions.
        /// </summary>
        public static bool TransferOrSwapToUntaggedContainer(this SlotReference slot, IReadOnlyList<IItemContainer> targetContainers)
        {
            var currentContainer = slot.Container;

            foreach (var container in targetContainers)
            {
                if (container == currentContainer)
                    continue;
                
                // Skip containers that have tag restrictions
                if (container.TryGetRestriction<TagContainerRestriction>(out var tagRestriction) && tagRestriction.Tags.Length > 0)
                    continue;

                if (slot.TransferOrSwapWithContainer(container))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Attempts to transfer the item to a container or swap it with an existing item in the container.
        /// </summary>
        public static bool TransferOrSwapWithContainer(this SlotReference slot, IItemContainer targetContainer)
        {
            var parentContainer = slot.Container;
            if (parentContainer == targetContainer)
                return false;

            int addedCount = targetContainer.AddItem(slot.GetStack()).addedCount;
            if (addedCount > 0)
            {
                slot.AdjustStack(-addedCount);
                return true;
            }

            var targetSlot = targetContainer.FindSlot(ItemSlotFilters.EmptyOrLast);
            return TransferOrSwapWithSlot(slot, targetSlot);
        }

        /// <summary>
        /// Attempts to swap items between the original slot and a target slot.
        /// </summary>
        public static bool TransferOrSwapWithSlot(this SlotReference slot, in SlotReference targetSlot)
        {
            if (!slot.IsValid() || !targetSlot.IsValid())
                return false;

            var targetStack = targetSlot.GetStack();
            var originalStack = slot.GetStack();

            bool canSwap =
                (!targetStack.HasItem() || slot.Container.GetAllowedCount(targetStack).allowedCount > 0) &&
                (!originalStack.HasItem() || targetSlot.Container.GetAllowedCount(originalStack).allowedCount > 0);

            if (canSwap)
            {
                slot.SetItem(targetStack);
                targetSlot.SetItem(originalStack);
                return true;
            }

            return false;
        }
    }
}