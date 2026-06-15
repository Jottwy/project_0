using UnityEngine.UI;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    [Serializable]
    public sealed class ProgressBarUI
    {
        [SerializeField]
        private GameObject _background;

        [SerializeField, NotNull]
        private Image _fillImage;

        [SerializeField]
        private Gradient _fillColorGradient;

        private bool? _isActive;
        
        /// <summary>
        /// Toggles the visibility and activity of the progress bar.
        /// </summary>
        /// <param name="isActive">Indicates whether the progress bar is active.</param>
        public void SetActive(bool isActive)
        {
            if (_isActive.HasValue && _isActive == isActive)
                return;

            if (_background != null)
                _background.SetActive(isActive);

            _fillImage.enabled = isActive;
            _isActive = isActive;
        }

        /// <summary>
        /// Sets the fill amount and updates the color based on the gradient.
        /// </summary>
        /// <param name="fillAmount">The fill amount (0 to 1).</param>
        public void SetFillAmount(float fillAmount)
        {
            _fillImage.color = _fillColorGradient.Evaluate(fillAmount);
            _fillImage.fillAmount = fillAmount;
        }

        /// <summary>
        /// Sets the alpha transparency of the fill image.
        /// </summary>
        /// <param name="alpha">The alpha value (0 to 1).</param>
        public void SetAlpha(float alpha)
        {
            Color currentColor = _fillImage.color;
            _fillImage.color = new Color(currentColor.r, currentColor.g, currentColor.b, alpha);
        }
    }
}