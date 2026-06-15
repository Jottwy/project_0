using PolymindGames.InventorySystem;
using UnityEngine.Serialization;
using System.Globalization;
using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Displays text values for item properties.
    /// </summary>
    [Serializable]
    public sealed class ItemPropertyTextDisplay
    {
        /// <summary>
        /// Represents a text field bound to an item property.
        /// </summary>
        [Serializable]
        private sealed class PropertyText
        {
            [FormerlySerializedAs("Property")]
            [SerializeField]
            [Tooltip("The property definition associated with this text display.")]
            private DataIdReference<ItemPropertyDefinition> _property;

            [FormerlySerializedAs("Text")]
            [SerializeField, SpaceArea(3f), NotNull]
            [Tooltip("The TextMeshProUGUI component to display the property value.")]
            private TextMeshProUGUI _text;

            [FormerlySerializedAs("Decimals")]
            [SerializeField, Range(0, 10)]
            [Tooltip("The number of decimal places for displaying the property value.")]
            private int _decimals;

            private ItemProperty _attachedProperty;

            /// <summary>
            /// Attaches this text display to an item and updates the text accordingly.
            /// </summary>
            /// <param name="item">The item containing the property to bind to.</param>
            public void AttachToItem(Item item) => AttachToProperty(item?.GetProperty(_property));

            /// <summary>
            /// Attaches this text display to a specific item property.
            /// </summary>
            /// <param name="property">The item property to bind to.</param>
            private void AttachToProperty(ItemProperty property)
            {
                if (_attachedProperty != null)
                {
                    _attachedProperty.Changed -= UpdateTextDisplay;
                }

                _attachedProperty = property;

                if (_attachedProperty != null)
                {
                    _attachedProperty.Changed += UpdateTextDisplay;
                    UpdateTextDisplay(_attachedProperty);
                }
                else
                {
                    _text.text = string.Empty;
                }
            }

            /// <summary>
            /// Updates the text display based on the property's current value.
            /// </summary>
            /// <param name="property">The property whose value has changed.</param>
            private void UpdateTextDisplay(ItemProperty property)
            {
                _text.text = Math.Round(property.Float, _decimals)
                    .ToString(CultureInfo.InvariantCulture);
            }
        }

        [SerializeField]
        [ReorderableList, IgnoreParent]
        [Tooltip("The list of text displays bound to different item properties.")]
        private PropertyText[] _propertyTexts;

        /// <summary>
        /// Updates the text displays for the given item properties.
        /// </summary>
        /// <param name="item">The item whose property text displays should be updated.</param>
        public void UpdateInfo(Item item)
        {
            foreach (var txtProperty in _propertyTexts)
            {
                txtProperty.AttachToItem(item);
            }
        }
    }
}