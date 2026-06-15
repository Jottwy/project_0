using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    [RequireComponent(typeof(Slider))]
    public sealed class ValueSliderUI : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _valueText;

        [SerializeField]
        private string _valueSuffix;

        private Slider _slider;

        /// <summary>
        /// Initializes the slider and adds a listener to update the value text.
        /// </summary>
        private void OnEnable()
        {
            _slider = GetComponent<Slider>();
            _slider.onValueChanged.AddListener(UpdateValueText);
        }

        /// <summary>
        /// Removes the listener when the object is disabled.
        /// </summary>
        private void OnDisable()
        {
            _slider.onValueChanged.RemoveListener(UpdateValueText);
        }

        /// <summary>
        /// Updates the value display text based on the slider value.
        /// </summary>
        /// <param name="value">The current value of the slider.</param>
        private void UpdateValueText(float value)
        {
            _valueText.text = value.ToString(_slider.wholeNumbers ? "F0" : "F2") + _valueSuffix;
        }
    }
}
