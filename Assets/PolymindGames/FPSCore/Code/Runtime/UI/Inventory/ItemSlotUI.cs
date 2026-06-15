using PolymindGames.InventorySystem;
using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [AddComponentMenu("Polymind Games/User Interface/Slots/Item Slot")]
    public class ItemSlotUI : ItemSlotUIBase
    {
        [SerializeField, IgnoreParent]
        private ItemIconDisplay _iconDisplay;

        [SerializeField, IgnoreParent]
        private ItemStackDisplay _itemStackDisplay;

        [SerializeField, IgnoreParent, SpaceArea]
        private ItemPropertyProgressBarDisplay _propertyDisplay;

        [SerializeField, IgnoreParent, SpaceArea]
        private ItemPropertyTextDisplay _propertyTextDisplay;

        /// <summary>
        /// Gets the background icon image associated with the item icon display.
        /// </summary>
        public Image BgIconImage => _iconDisplay.BgIconImage;

        /// <summary>
        /// Updates the UI elements based on the given item. 
        /// If the item is null, it clears the displays.
        /// </summary>
        protected override void UpdateUI(in ItemStack stack, SlotChangeType changeType)
        {
            var item = stack.Item;

            if (changeType == SlotChangeType.ItemChanged)
            {
                _iconDisplay.UpdateInfo(item);
                _propertyDisplay.UpdateInfo(item);
                _propertyTextDisplay.UpdateInfo(item);
            }
            
            _itemStackDisplay.UpdateInfo(stack);
        }
    }
}