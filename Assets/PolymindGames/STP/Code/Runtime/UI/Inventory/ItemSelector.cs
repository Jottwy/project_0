using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public abstract class ItemSelector : MonoSingleton<ItemSelector>
    {
        private ItemSlotUIBase _highlightedSlot;
        private ItemSlotUIBase _selectedSlot;

        /// <summary>
        /// Gets or sets the currently selected item slot.
        /// Triggers the <see cref="SelectedSlotChanged"/> event when changed.
        /// </summary>
        public ItemSlotUIBase SelectedSlot
        {
            get => _selectedSlot;
            protected set
            {
                if (_selectedSlot == value)
                    return;

                _selectedSlot = value;
                SelectedSlotChanged?.Invoke(value);
            }
        }

        /// <summary>
        /// Gets or sets the currently highlighted item slot.
        /// Triggers the <see cref="HighlightedSlotChanged"/> event when changed.
        /// </summary>
        public ItemSlotUIBase HighlightedSlot
        {
            get => _highlightedSlot;
            protected set
            {
                if (_highlightedSlot == value)
                    return;

                _highlightedSlot = value;
                HighlightedSlotChanged?.Invoke(value);
            }
        }

        /// <summary>
        /// Event triggered when the selected slot changes.
        /// </summary>
        public event UnityAction<ItemSlotUIBase> SelectedSlotChanged;

        /// <summary>
        /// Event triggered when the highlighted slot changes.
        /// </summary>
        public event UnityAction<ItemSlotUIBase> HighlightedSlotChanged;

        /// <summary>
        /// Manually raises the <see cref="SelectedSlotChanged"/> event for the current selected slot.
        /// </summary>
        protected void RaiseSelectedEvent() => SelectedSlotChanged?.Invoke(SelectedSlot);
    }
}