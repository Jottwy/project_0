using PolymindGames.InventorySystem;
using UnityEngine;
using System;
using UnityEngine.Serialization;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Displays progress bars for item properties.
    /// </summary>
    [Serializable]
    public sealed class ItemPropertyProgressBarDisplay
    {
        /// <summary>
        /// Represents a progress bar bound to an item property.
        /// </summary>
        [Serializable]
        private sealed class PropertyProgressBar
        {
            [FormerlySerializedAs("Property")]
            [SerializeField]
            [Tooltip("The property definition associated with this progress bar.")]
            private DataIdReference<ItemPropertyDefinition> _property;

            [FormerlySerializedAs("ProgressBar")]
            [SerializeField, IgnoreParent]
            [Tooltip("The UI element that displays the property progress.")]
            private ProgressBarUI _progressBar;

            [FormerlySerializedAs("MaxValue")]
            [SerializeField, Range(0f, 100f)]
            [Tooltip("The maximum value for this property to fully fill the progress bar.")]
            private float _maxValue;

            private ItemProperty _itemProperty;

            /// <summary>
            /// Attaches this progress bar to an item and updates the progress accordingly.
            /// </summary>
            /// <param name="item">The item containing the property to bind to.</param>
            public void AttachToItem(Item item) => AttachToProperty(item?.GetProperty(_property));

            /// <summary>
            /// Attaches this progress bar to a specific item property.
            /// </summary>
            /// <param name="property">The item property to bind to.</param>
            private void AttachToProperty(ItemProperty property)
            {
                if (_itemProperty != null)
                {
                    _itemProperty.Changed -= UpdateProgressBar;
                }

                _itemProperty = property;

                if (_itemProperty != null)
                {
                    _itemProperty.Changed += UpdateProgressBar;
                    _progressBar.SetActive(true);
                    UpdateProgressBar(_itemProperty);
                }
                else
                {
                    _progressBar.SetActive(false);
                }
            }

            /// <summary>
            /// Updates the progress bar based on the property's current value.
            /// </summary>
            /// <param name="property">The property whose value has changed.</param>
            private void UpdateProgressBar(ItemProperty property)
            {
                float value = Mathf.Clamp(property.Float, 0f, _maxValue);
                float fillAmount = value / _maxValue;
                _progressBar.SetFillAmount(fillAmount);
            }
        }

        [SerializeField]
        [ReorderableList, IgnoreParent]
        [Tooltip("The list of progress bars bound to different item properties.")]
        private PropertyProgressBar[] _propertyProgressBars;

        /// <summary>
        /// Updates the progress bar information for the given item.
        /// </summary>
        /// <param name="item">The item whose property progress bars should be updated.</param>
        public void UpdateInfo(Item item)
        {
            foreach (var propertyProgressBar in _propertyProgressBars)
            {
                propertyProgressBar.AttachToItem(item);
            }
        }
    }
}