using PolymindGames.InventorySystem;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/user-interface/behaviours/ui_interaction")]
    public sealed class ItemPickupInteractionPromptUI : CharacterUIBehaviour, IInteractablePrompt
    {
        [SerializeField, NotNull]
        private UIPanel _panel;

        [SerializeField]
        private WorldToUIScreenPositioner _screenPositioner;
        
        [SerializeField, IgnoreParent, Title("Item Info")]
        private ItemNameDisplay _nameDisplay;

        [SerializeField, IgnoreParent]
        private ItemDescriptionDisplay _descriptionDisplay;

        [SerializeField, IgnoreParent]
        private ItemIconDisplay _iconDisplay;

        [SerializeField, IgnoreParent]
        private ItemStackDisplay _stackDisplay;

        [SerializeField, IgnoreParent]
        private ItemWeightDisplay _weightDisplay;
        
        [SerializeField]
        [Tooltip("An image that used in showing the time the current interactable has been interacted with.")]
        private Image _interactProgressImg;
        
        [SerializeField, SpaceArea]
        private UnityEvent _onItemChanged;

        public bool TrySetHoverable(IHoverable hoverable)
        {
            if (hoverable != null && hoverable.gameObject.TryGetComponent(out ItemPickup itemPickup))
            {
                SetItemPickup(itemPickup);
                
                if (_screenPositioner != null)
                    _screenPositioner.SetTargetTransform(itemPickup.transform, hoverable.CenterOffset);
            
                _panel.Show();
                return true;
            }

            if (_screenPositioner != null)
                _screenPositioner.SetTargetTransform(null);
                
            _panel.Hide();
            return false;
        }

        private void SetItemPickup(ItemPickup pickup)
        {
            var stack = pickup.AttachedItem;
            _nameDisplay.UpdateInfo(stack.Item);
            _descriptionDisplay.UpdateInfo(stack.Item);
            _iconDisplay.UpdateInfo(stack.Item);
            _stackDisplay.UpdateInfo(stack);
            _weightDisplay.UpdateInfo(stack);
            _onItemChanged.Invoke();
        }

        public void SetInteractionProgress(float progress) => _interactProgressImg.fillAmount = progress;

        protected override void Awake()
        {
            base.Awake();
            _interactProgressImg.fillAmount = 0f;
        }
    }
}