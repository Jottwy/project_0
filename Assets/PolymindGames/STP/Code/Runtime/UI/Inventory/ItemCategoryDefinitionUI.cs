using PolymindGames.InventorySystem;
using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class ItemCategoryDefinitionUI : DataSlotUI<ItemCategoryDefinition>
    {
        [SerializeField]
        [Tooltip("UI element to display the category's name.")]
        private TextMeshProUGUI _nameText;

        [SerializeField]
        [Tooltip("UI element to display the category's description.")]
        private TextMeshProUGUI _descriptionText;

        [SerializeField]
        [Tooltip("UI element to display the category's icon.")]
        private Image _iconImage;

        /// <summary>
        /// Updates the UI to reflect the provided item category definition.
        /// </summary>
        /// <param name="definition">The item category definition to use for updating the UI.</param>
        protected override void UpdateUI(ItemCategoryDefinition definition)
        {
            if (definition != null)
            {
                if (_nameText != null)
                    _nameText.text = definition.Name;

                if (_descriptionText != null)
                    _descriptionText.text = definition.Description;

                if (_iconImage != null)
                {
                    _iconImage.enabled = true;
                    _iconImage.sprite = definition.Icon;
                }
            }
            else
            {
                if (_nameText != null)
                    _nameText.text = string.Empty;

                if (_descriptionText != null)
                    _descriptionText.text = string.Empty;

                if (_iconImage != null)
                    _iconImage.enabled = false;
            }
        }
    }
}