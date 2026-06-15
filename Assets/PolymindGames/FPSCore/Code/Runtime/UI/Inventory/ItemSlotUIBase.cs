using PolymindGames.InventorySystem;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [DisallowMultipleComponent]
    public abstract class ItemSlotUIBase : MonoBehaviour
    {
        private SelectableButton _selectable;
        private SlotReference _itemSlot;

        /// <summary>
        /// Indicates whether there is an item in the current slot.
        /// </summary>
        public bool HasItem => HasSlot && _itemSlot.HasItem();

        /// <summary>
        /// Indicates whether an ItemSlot is assigned to this UI.
        /// </summary>
        public bool HasSlot => _itemSlot.IsValid();

        /// <summary>
        /// Gets the assigned ItemSlot. Logs an error in debug mode if no slot is assigned.
        /// </summary>
        public SlotReference Slot => _itemSlot;

        /// <summary>
        /// Gets the SelectableButton component attached to this GameObject.
        /// </summary>
        public SelectableButton Selectable
        {
            get
            {
                if (_selectable == null)
                    _selectable = GetComponent<SelectableButton>();

                return _selectable;
            }
        }

        /// <summary>
        /// Assigns a new ItemSlot to this UI and updates the UI accordingly.
        /// </summary>
        /// <param name="slot">The new ItemSlot to associate with this UI.</param>
        public void AttachToSlot(SlotReference slot)
        {
            if (_itemSlot == slot)
                return;

            // Unsubscribe from the old ItemSlot's events
            if (_itemSlot.IsValid())
            {
                _itemSlot.Changed -= HandleItemChanged;
            }

            _itemSlot = slot;

            // Subscribe to the new ItemSlot's events and update UI
            if (_itemSlot.IsValid())
            {
                _itemSlot.Changed += HandleItemChanged;
                UpdateUI(_itemSlot.GetStack(), SlotChangeType.ItemChanged);
            }
            else
            {
                UpdateUI(ItemStack.Null, SlotChangeType.ItemChanged);
            }
        }

        /// <summary>
        /// Handles changes in the associated ItemSlot and updates the UI.
        /// </summary>
        private void HandleItemChanged(in SlotReference slot, SlotChangeType changeType)
        {
            UpdateUI(slot.GetStack(), changeType);
        }

        /// <summary>
        /// Updates the UI based on the given item. This method must be implemented by derived classes.
        /// </summary>
        protected abstract void UpdateUI(in ItemStack stack, SlotChangeType changeType);

        /// <summary>
        /// Cleans up by unassigning the current ItemSlot when this UI is destroyed.
        /// </summary>
        protected virtual void OnDestroy()
        {
            AttachToSlot(SlotReference.Null);
        }
        
        protected virtual void Start()
        {
            if (!_itemSlot.IsValid())
            {
                AttachToSlot(SlotReference.Null);
            }
        }

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            if (!gameObject.HasComponent<SelectableButton>())
                gameObject.AddComponent<SelectableButton>();
        }
#endif
        #endregion
    }
}