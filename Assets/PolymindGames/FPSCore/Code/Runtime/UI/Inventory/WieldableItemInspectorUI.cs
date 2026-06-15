using UnityEngine;

namespace PolymindGames.UserInterface
{
    public sealed class WieldableItemInspectorUI : ItemInspectorBaseUI
    {
        [SerializeField, IgnoreParent, Title("Item Info")]
        [Tooltip("The component responsible for displaying the item name.")]
        private ItemNameDisplay _nameDisplay;

        [SerializeField, IgnoreParent]
        [Tooltip("The component responsible for displaying the item description.")]
        private ItemDescriptionDisplay _descriptionDisplay;

        [SerializeField, IgnoreParent]
        [Tooltip("The component responsible for displaying the item weight.")]
        private ItemWeightDisplay _weightDisplay;

        [SerializeField, IgnoreParent]
        [Tooltip("The component responsible for displaying the item firemode.")]
        private ItemFiremodeDisplay _firemodeDisplay;
        
        /// <summary>
        /// Updates the UI elements with the item information from the specified slot.
        /// </summary>
        /// <param name="slot">The item slot to inspect.</param>
        protected override void UpdateInspectionUI(ItemSlotUIBase slot)
        {
            var stack = slot.Slot.GetStack();
            var item = stack.Item;
            _nameDisplay.UpdateInfo(item);
            _descriptionDisplay.UpdateInfo(item);
            _weightDisplay.UpdateInfo(stack);
            _firemodeDisplay.UpdateInfo(item);
        }
    }
}