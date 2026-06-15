using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class RequirementUI : MonoBehaviour
    {
        [SerializeField]
        private Image _iconImg;

        [SerializeField]
        private TextMeshProUGUI _amountText;
        
        /// <summary>
        /// Sets the icon and amount text with an optional color.
        /// </summary>
        public void SetIconAndAmount(Sprite icon, string amountText, Color? textColor = null)
        {
            _iconImg.sprite = icon;
            _amountText.text = amountText;

            if (textColor.HasValue)
                _amountText.color = textColor.Value;
        }

        /// <summary>
        /// Updates only the amount text.
        /// </summary>
        public void SetAmount(string amountText)
        {
            _amountText.text = amountText;
        }

        /// <summary>
        /// Toggles the visibility of the UI element.
        /// </summary>
        public void ToggleVisibility(bool isVisible)
        {
            gameObject.SetActive(isVisible);
        }
    }
}