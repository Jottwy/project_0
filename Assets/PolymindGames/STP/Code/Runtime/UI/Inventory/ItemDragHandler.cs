using PolymindGames.InventorySystem;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/user-interface/behaviours/ui_inventory#item-drag-handler")]
    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public sealed class ItemDragHandler : ItemDragger
    {
        [SerializeField, PrefabObjectOnly]
        [Tooltip("Slot template prefab instantiated when an item is dragged.")]
        private ItemSlotUIBase _dragVisualTemplate;

        [SerializeField, Range(1f, 100f)]
        private float _dragSpeed = 15f;

        private RectTransform _dragParentTransform;
        private ItemSlotUIBase _dragVisual;
        private Vector2 _dragOffset;

        /// <summary>
        /// The currently dragged item stack.
        /// </summary>
        private ItemStack CurrentDraggedStack => _dragVisual.Slot.GetStack();

        /// <inheritdoc/>
        public override void OnDragStart(ItemSlotUIBase sourceSlot, Vector2 pointerPosition, bool isSplittingStack = false)
        {
            if (IsDragging || !sourceSlot.HasItem)
                return;

            BeginDrag(sourceSlot, pointerPosition, isSplittingStack);
        }

        /// <inheritdoc/>
        public override void OnDrag(Vector2 pointerPosition)
        {
            if (IsDragging)
                UpdateDragVisual(pointerPosition);
        }

        /// <inheritdoc/>
        public override void CancelDrag(ItemSlotUIBase sourceSlot)
        {
            if (IsDragging)
            {
                ReturnItemToSlot(sourceSlot.Slot);
                ClearDrag();
            }
        }

        /// <inheritdoc/>
        public override void OnDragEnd(ItemSlotUIBase sourceSlot, ItemSlotUIBase targetSlot, GameObject dropTarget)
        {
            if (!IsDragging)
                return;

            if (targetSlot != null)
            {
                HandleItemDrop(sourceSlot, targetSlot);
            }
            else if (dropTarget != null)
            {
                ReturnItemToSlot(sourceSlot.Slot);
            }
            else
            {
                DropItemOutside(sourceSlot);
            }

            ClearDrag();
        }
        
        /// <summary>
        /// Begins dragging an item from a source slot.
        /// </summary>
        private void BeginDrag(ItemSlotUIBase sourceSlot, Vector2 pointerPosition, bool isSplittingStack)
        {
            var originalStack = sourceSlot.Slot.GetStack();
            bool shouldSplitStack = isSplittingStack && originalStack.Count > 1;

            var draggedStack = shouldSplitStack ? SplitStack(sourceSlot.Slot) : originalStack;
            if (!shouldSplitStack) sourceSlot.Slot.Clear();

            _dragVisual.gameObject.SetActive(true);
            _dragVisual.Slot.SetItem(draggedStack);

            UpdateDragVisual(pointerPosition, false);
            IsDragging = true;
        }

        /// <summary>
        /// Splits a stack in half when dragging.
        /// </summary>
        private ItemStack SplitStack(SlotReference sourceSlot)
        {
            int splitAmount = Mathf.FloorToInt(sourceSlot.GetStack().Count / 2f);
            sourceSlot.AdjustStack(-splitAmount);
            return new ItemStack(new Item(sourceSlot.GetItem()), splitAmount);
        }

        /// <summary>
        /// Updates the drag visual position based on pointer movement.
        /// </summary>
        private void UpdateDragVisual(Vector2 pointerPosition, bool smoothMovement = true)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_dragParentTransform, pointerPosition, null, out Vector2 localPoint))
                return;

            float lerpSpeed = smoothMovement ? Time.deltaTime * _dragSpeed : 1f;
            _dragVisual.transform.localPosition = Vector3.Lerp(
                _dragVisual.transform.localPosition,
                localPoint + (Vector2)_dragParentTransform.InverseTransformVector(_dragOffset),
                lerpSpeed
            );

            _dragOffset = Vector2.Lerp(_dragOffset, Vector2.zero, lerpSpeed * 0.5f);
        }

        /// <summary>
        /// Handles dropping an item onto another slot.
        /// </summary>
        private void HandleItemDrop(ItemSlotUIBase sourceSlot, ItemSlotUIBase targetSlot)
        {
            if (targetSlot == sourceSlot || !CanDropItem(targetSlot))
            {
                ReturnItemToSlot(sourceSlot.Slot);
                return;
            }

            ExecuteItemDrop(sourceSlot, targetSlot);
            targetSlot.Selectable.Select();
        }

        /// <summary>
        /// Checks if the current item stack can be placed in the target slot.
        /// </summary>
        private bool CanDropItem(ItemSlotUIBase targetSlot) =>
            targetSlot.Slot.Container?.GetAllowedCount(CurrentDraggedStack).allowedCount > 0;

        /// <summary>
        /// Executes an item drop operation, handling merging, swapping, or setting items.
        /// </summary>
        private void ExecuteItemDrop(ItemSlotUIBase sourceSlot, ItemSlotUIBase targetSlot)
        {
            if (!targetSlot.HasItem)
            {
                int added = targetSlot.Slot.SetItem(CurrentDraggedStack);
                if (added != CurrentDraggedStack.Count)
                {
                    sourceSlot.Slot.SetItem(new ItemStack(CurrentDraggedStack.Item, CurrentDraggedStack.Count - added));
                }
            }
            else if (CanMergeStacks(targetSlot.Slot.GetStack(), CurrentDraggedStack))
            {
                MergeStacks(sourceSlot.Slot, targetSlot.Slot);
            }
            else
            {
                SwapItems(sourceSlot.Slot, targetSlot.Slot);
            }
        }

        /// <summary>
        /// Determines if two stacks can be merged.
        /// </summary>
        private static bool CanMergeStacks(ItemStack existingStack, ItemStack incomingStack) =>
            existingStack.Item.Id == incomingStack.Item.Id &&
            existingStack.Item.IsStackable &&
            existingStack.Count < existingStack.Item.StackSize;

        /// <summary>
        /// Handles dropping an item outside an inventory.
        /// </summary>
        private void DropItemOutside(ItemSlotUIBase sourceSlot)
        {
            var parentInventory = sourceSlot.Slot.Container.Inventory;
            var targetInventory = parentInventory ?? GameMode.Instance.LocalPlayer.Inventory;
            targetInventory?.DropItem(_dragVisual.Slot.GetStack());
        }

        /// <summary>
        /// Clears the drag visual and resets dragging state.
        /// </summary>
        private void ClearDrag()
        {
            _dragVisual.gameObject.SetActive(false);
            _dragVisual.Slot.SetItem(ItemStack.Null);
            IsDragging = false;
        }

        /// <summary>
        /// Merges two item stacks together.
        /// </summary>
        private void MergeStacks(in SlotReference sourceSlot, in SlotReference targetSlot)
        {
            var draggedStack = CurrentDraggedStack;
            int addedAmount = targetSlot.AdjustStack(draggedStack.Count);
            draggedStack.Count -= addedAmount;
            if (draggedStack.Count > 0)
            {
                sourceSlot.SetItem(draggedStack);
            }
        }

        /// <summary>
        /// Swaps items between two slots.
        /// </summary>
        private void SwapItems(in SlotReference sourceSlot, in SlotReference targetSlot)
        {
            ReturnItemToSlot(sourceSlot);
            sourceSlot.TransferOrSwapWithSlot(targetSlot);
        }

        /// <summary>
        /// Returns the dragged item back to the original slot if not placed elsewhere.
        /// </summary>
        private void ReturnItemToSlot(in SlotReference sourceSlot)
        {
            if (sourceSlot.HasItem())
            {
                sourceSlot.AdjustStack(CurrentDraggedStack.Count);
            }
            else
            {
                sourceSlot.SetItem(CurrentDraggedStack);
            }
        }
        
        protected override void Awake()
        {
            base.Awake();
            InitializeDragVisual();
        }

        /// <summary>
        /// Initializes the drag visual for displaying the dragged item.
        /// </summary>
        private void InitializeDragVisual()
        {
#if DEBUG
            if (_dragVisualTemplate == null)
            {
                Debug.LogError("Drag visual template is not assigned. Assign a prefab in the inspector.", gameObject);
                return;
            }
#endif
            _dragParentTransform = (RectTransform)transform.parent;
            var dragContainer = new ItemContainer.Builder().WithSize(1).Build();

            _dragVisual = Instantiate(_dragVisualTemplate, _dragParentTransform);
            _dragVisual.AttachToSlot(dragContainer.GetSlot(0));
            _dragVisual.gameObject.SetActive(false);
        }
    }
}