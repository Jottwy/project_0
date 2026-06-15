using PolymindGames.SaveSystem;
using UnityEngine.UI;
using UnityEngine;
using TMPro;
using UnityEngine.Serialization;

namespace PolymindGames.UserInterface
{
    public sealed class LevelDefinitionUI : DataSlotUI<LevelDefinition>
    {
        [SerializeField, NotNull]
        [Tooltip("UI element to display the level's thumbnail image.")]
        private Image _thumbnailImage;

        [FormerlySerializedAs("_levelNameText")]
        [SerializeField, NotNull]
        [Tooltip("UI element to display the scene's name.")]
        private TextMeshProUGUI _nameText;

        [FormerlySerializedAs("_levelDescriptionText")]
        [SerializeField, NotNull]
        [Tooltip("UI element to display the scene's description.")]
        private TextMeshProUGUI _descriptionText;

        /// <summary>
        /// Updates the UI elements to reflect the provided game level definition.
        /// </summary>
        /// <param name="definition">The game level definition to display.</param>
        protected override void UpdateUI(LevelDefinition definition)
        {
            if (definition == null)
            {
                ClearUI();
                return;
            }

            _thumbnailImage.sprite = definition.LevelIcon;
            _nameText.text = definition.LevelName;
            _descriptionText.text = definition.LevelDescription;
        }

        /// <summary>
        /// Clears the UI elements when no valid data is available.
        /// </summary>
        private void ClearUI()
        {
            _thumbnailImage.sprite = null;
            _nameText.text = string.Empty;
            _descriptionText.text = string.Empty;
        }
    }
}