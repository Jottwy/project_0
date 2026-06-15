using UnityEngine;

namespace PolymindGames.UserInterface
{
    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public abstract class ItemDragger : MonoSingleton<ItemDragger>
    {
        /// <summary>
        /// Gets a value indicating whether an item is currently being dragged.
        /// </summary>
        public bool IsDragging { get; protected set; }

        /// <summary>
        /// Called when dragging starts.
        /// </summary>
        /// <param name="sourceSlot">The item slot where the drag started.</param>
        /// <param name="pointerPosition">The pointer position at the start of the drag.</param>
        /// <param name="splitItemStack">Whether the drag involves splitting the item stack.</param>
        public abstract void OnDragStart(ItemSlotUIBase sourceSlot, Vector2 pointerPosition, bool splitItemStack = false);

        /// <summary>
        /// Cancels the current drag operation.
        /// </summary>
        /// <param name="sourceSlot">The slot where the drag started.</param>
        public abstract void CancelDrag(ItemSlotUIBase sourceSlot);

        /// <summary>
        /// Called while dragging is in progress.
        /// </summary>
        /// <param name="pointerPosition">The current pointer position.</param>
        public abstract void OnDrag(Vector2 pointerPosition);

        /// <summary>
        /// Called when dragging ends.
        /// </summary>
        /// <param name="sourceSlot">The slot where the drag started.</param>
        /// <param name="dropSlot">The slot where the item was dropped.</param>
        /// <param name="dropObject">The game object where the item was dropped.</param>
        public abstract void OnDragEnd(ItemSlotUIBase sourceSlot, ItemSlotUIBase dropSlot, GameObject dropObject);
    }
}