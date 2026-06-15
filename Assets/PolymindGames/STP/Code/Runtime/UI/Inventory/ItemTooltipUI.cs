using PolymindGames.InventorySystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    public sealed class ItemTooltipUI : CharacterUIBehaviour
    {
        [SerializeField, NotNull]
        [Tooltip("The CanvasGroup component used for controlling the visibility of the tooltip.")]
        private CanvasGroup _canvasGroup;

        [SerializeField, Range(1f, 100f)]
        [Tooltip("The speed at which the tooltip is shown or hidden.")]
        private float _showSpeed = 10f;

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
        [Tooltip("The component responsible for displaying the item stack information.")]
        private ItemStackDisplay _stackDisplay;

        [SerializeField, IgnoreParent]
        [Tooltip("The component responsible for displaying the item weight.")]
        private ItemWeightDisplay _weightDisplay;

        [SerializeField, SpaceArea]
        [Tooltip("Event invoked when the displayed item in the tooltip changes.")]
        private UnityEvent _onItemChanged;
        
        private RectTransform _cachedRect;
        private RectTransform _cachedRectParent;
        private bool _isActive;
        private bool _wasDragging;

        protected override void Awake()
        {
            base.Awake();

            enabled = false;
            _canvasGroup.alpha = 0f;
            _cachedRect = (RectTransform)transform;
            _cachedRectParent = (RectTransform)_cachedRect.parent;
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            var inspection = character.GetCC<IInventoryInspectionManagerCC>();
            inspection.InspectionStarted += OnInventoryInspectionStarted;
            inspection.InspectionEnded += OnInventoryInspectionEnded;
            
            ItemSelector.Instance.HighlightedSlotChanged += UpdateTooltipInfo;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            var inspection = character.GetCC<IInventoryInspectionManagerCC>();
            inspection.InspectionStarted -= OnInventoryInspectionStarted;
            inspection.InspectionEnded -= OnInventoryInspectionEnded;
            
            if (ItemSelector.HasInstance)
                ItemSelector.Instance.HighlightedSlotChanged -= UpdateTooltipInfo;
        }

        private void OnInventoryInspectionStarted() => enabled = true;

        private void OnInventoryInspectionEnded()
        {
            _canvasGroup.alpha = 0f;
            _isActive = false;
            enabled = false;
        }

        private void UpdateTooltipInfo(ItemSlotUIBase slot)
        {
            var stack = slot?.Slot.GetStack() ?? ItemStack.Null;
            var item = stack.Item;
            
            _isActive = stack.HasItem();
            _nameDisplay.UpdateInfo(item);
            _descriptionDisplay.UpdateInfo(item);
            _iconDisplay.UpdateInfo(item);
            _stackDisplay.UpdateInfo(stack);
            _weightDisplay.UpdateInfo(stack);
            _onItemChanged.Invoke();
        }

        private void LateUpdate()
        {
            bool isDragging = ItemDragger.Instance.IsDragging;

            if (_wasDragging && !isDragging)
                UpdateTooltipInfo(ItemSelector.Instance.HighlightedSlot);

            UpdatePosition(RaycastManagerUI.Instance.GetCursorPosition());

            bool isActive = _isActive && !isDragging;
            float targetAlpha = isActive ? 1f : 0f;
            float lerpSpeed = isActive ? _showSpeed : _showSpeed * 1.5f;

            _canvasGroup.alpha = Mathf.Lerp(_canvasGroup.alpha, targetAlpha, Time.deltaTime * lerpSpeed);
            _wasDragging = isDragging;
        }

        private void UpdatePosition(Vector2 pointerPosition)
        {
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_cachedRectParent, pointerPosition, null, out Vector2 position))
                _cachedRect.anchoredPosition = position;
        }
    }
}