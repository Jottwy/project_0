using UnityEngine;

namespace PolymindGames.InventorySystem
{
    public class WieldableItemPickup : ItemPickup
    {
        /// <inheritdoc/>
        protected override void OnInteracted(IInteractable interactable, ICharacter character)
        {
            // Attempt to find a container that accepts wieldable items in the character's inventory
            var holsterContainer = character.Inventory.FindContainer(ItemContainerFilters.WithTag(ItemConstants.WieldableTag));

            // If no suitable holster container is found, use the base method to handle interaction
            if (holsterContainer == null)
            {
                base.OnInteracted(interactable, character);
                return;
            }

            var wieldableInventory = character.GetCC<IWieldableInventoryCC>();

            // If the holster container is not full, attempt to pick up the attached item
            if (!AttachedItem.Item.IsStackable && holsterContainer.IsFull())
            {
                wieldableInventory.DropWieldable(true);
            }

            if (PickUpAttachedItem(character, holsterContainer) != ItemPickupAddResult.Failed)
            {
                SelectAttachedItem(holsterContainer, wieldableInventory);
            }
            else
            {
                base.OnInteracted(interactable, character);
            }
        }

        /// <summary>
        /// Selects the attached item from the holster container and updates the wieldable inventory.
        /// </summary>
        /// <param name="holsterContainer">The container holding the wieldable item.</param>
        /// <param name="wieldableInventory">The inventory that manages the wieldable items.</param>
        private void SelectAttachedItem(IItemContainer holsterContainer, IWieldableInventoryCC wieldableInventory)
        {
            // Find the slot in the holster container that contains the attached item
            var slot = holsterContainer.FindSlot(ItemSlotFilters.WithItem(AttachedItem.Item));
            if (!slot.IsValid())
                slot = holsterContainer.FindSlot(ItemSlotFilters.WithItemId(AttachedItem.Item.Id));

            // If the slot is valid, select the item at that index in the wieldable inventory
            if (slot.IsValid() && slot.Index != wieldableInventory.SelectedIndex)
            {
                wieldableInventory.SelectAtIndex(slot.Index, false);
            }
        }
    }
}