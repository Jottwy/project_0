using PolymindGames.InventorySystem;
using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SelectableButton))]
    public sealed class ItemBlueprintUI : DataSlotUI<ItemDefinition>
    {
        [SerializeField, IgnoreParent, Title("Info")]
        private ItemNameDisplay _nameDisplay;

        [SerializeField, IgnoreParent]
        private ItemDescriptionDisplay _descriptionDisplay;

        [SerializeField, IgnoreParent]
        private ItemIconDisplay _iconDisplay;

        [SerializeField, IgnoreParent]
        private ItemRequirementDisplay _requirementDisplay;

        [SerializeField, Title("Favorite")]
        private Image _favoriteImage;
        
        [SerializeField]
        private Color _favoriteColor;

        [SerializeField]
        private Color _unfavoriteColor;
        
        [SerializeField, NotNull]
        private SelectableButton _favoriteButton;

        public SelectableButton FavoriteButton => _favoriteButton;

        public void SetFavoriteStatus(bool favorite)
        {
            _favoriteImage.color = favorite ? _favoriteColor : _unfavoriteColor;
        }
        
        protected override void UpdateUI(ItemDefinition definition)
        {
            Item item = definition != null ? Item.GetDummyItem(definition) : null;
            _nameDisplay.UpdateInfo(item);
            _descriptionDisplay.UpdateInfo(item);
            _iconDisplay.UpdateInfo(item);
            _requirementDisplay.UpdateInfo(item);
        }
    }
}