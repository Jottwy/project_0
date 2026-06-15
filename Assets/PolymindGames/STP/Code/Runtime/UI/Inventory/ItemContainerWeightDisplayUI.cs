using PolymindGames.InventorySystem;
using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Displays the current weight of an item container and updates a progress bar and text UI.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ItemContainerWeightDisplayUI : MonoBehaviour
    {
        [SerializeField, Range(0, 5)]
        [Tooltip("The number of decimal places to show for the weight.")]
        private int _decimals = 2;

        [SerializeField]
        [Tooltip("The progress bar UI to visually represent the weight percentage.")]
        private ProgressBarUI _weightBar;

        [SerializeField]
        [Tooltip("The text element to display the weight in numerical format.")]
        private TextMeshProUGUI _weightText;

        [SerializeField]
        [Tooltip("The default item container UI this component is attached to.")]
        private ItemContainerUI _containerUI;

        private IItemContainer _itemContainer;

        /// <summary>
        /// Attaches the weight display to a specific item container.
        /// </summary>
        public void AttachToContainer(IItemContainer container)
        {
            if (_itemContainer == container)
                return;

            DetachFromContainer();

            _itemContainer = container;

            if (_itemContainer != null)
            {
                _itemContainer.Changed += OnContainerChanged;
                UpdateWeightDisplay();
            }
        }

        /// <summary>
        /// Detaches the weight display from the current item container.
        /// </summary>
        public void DetachFromContainer()
        {
            if (_itemContainer != null)
            {
                _itemContainer.Changed -= OnContainerChanged;
                _itemContainer = null;
            }
        }

        /// <summary>
        /// Called when the attached container's weight changes to update the display.
        /// </summary>
        private void OnContainerChanged()
        {
            UpdateWeightDisplay();
        }

        /// <summary>
        /// Updates the weight display based on the current container's weight and capacity.
        /// </summary>
        private void UpdateWeightDisplay()
        {
            if (_itemContainer == null)
                return;

            float maxWeight = _itemContainer.MaxWeight;
            float currentWeight = _itemContainer.Weight;

            // Update text and progress bar
            _weightText.text = $"{Math.Round(currentWeight, _decimals)} / {maxWeight} {ItemDefinition.WeightUnit}";
            _weightBar.SetFillAmount(maxWeight > 0 ? currentWeight / maxWeight : 0f);
        }

        private void Start()
        {
            if (_containerUI != null)
            {
                AttachToContainer(_containerUI.Container);
                _containerUI.AttachedContainerChanged += OnAttachedContainerChanged;
            }
        }

        private void OnDestroy()
        {
            DetachFromContainer();

            if (_containerUI != null)
                _containerUI.AttachedContainerChanged -= OnAttachedContainerChanged;
        }

        private void OnAttachedContainerChanged(IItemContainer container)
        {
            AttachToContainer(container);
        }

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_containerUI == null)
                _containerUI = GetComponentInParent<ItemContainerUI>();
        }
#endif
        #endregion
    }
}