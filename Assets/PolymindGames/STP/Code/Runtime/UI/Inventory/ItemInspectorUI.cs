using UnityEngine;

namespace PolymindGames.UserInterface
{
    public sealed class ItemInspectorUI : ItemInspectorBaseUI
    {
        [SerializeField, IgnoreParent, Title("Item Info")]
        [Tooltip("The component responsible for displaying the item name.")]
        private ItemNameDisplay _nameDisplay;

        [SerializeField, IgnoreParent]
        [Tooltip("The component responsible for displaying the item description.")]
        private ItemDescriptionDisplay _descriptionDisplay;

        [SerializeField, IgnoreParent]
        [Tooltip("The component responsible for displaying the item icon.")]
        private ItemIconDisplay _iconDisplay;

        [SerializeField, IgnoreParent]
        [Tooltip("The component responsible for displaying the item weight.")]
        private ItemWeightDisplay _weightDisplay;
        
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
            _iconDisplay.UpdateInfo(item);
            _weightDisplay.UpdateInfo(stack);
        }
        
        private void OnEnable()
        {
            ItemSelector.Instance.SelectedSlotChanged += SetInspectedSlot;
            if (ItemSelector.Instance.SelectedSlot != null)
                SetInspectedSlot(ItemSelector.Instance.SelectedSlot);
        }

        private void OnDisable()
        {
            ItemSelector.Instance.SelectedSlotChanged -= SetInspectedSlot;
        }
    }
}