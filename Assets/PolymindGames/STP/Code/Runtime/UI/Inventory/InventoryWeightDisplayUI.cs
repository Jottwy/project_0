using PolymindGames.InventorySystem;
using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Displays the current weight of an inventory and updates a progress bar and text UI.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InventoryWeightDisplayUI : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("The default inventory UI this component is attached to.")]
        private InventoryUI _inventoryUI;
        
        [SpaceArea]
        [SerializeField, Range(0, 5)]
        [Tooltip("The number of decimal places to show for the weight.")]
        private int _decimals = 2;

        [SerializeField]
        [Tooltip("The progress bar UI to visually represent the weight percentage.")]
        private ProgressBarUI _weightBar;

        [SerializeField]
        [Tooltip("The text element to display the weight in numerical format.")]
        private TextMeshProUGUI _weightText;

        private IInventory _inventory;

        /// <summary>
        /// Attaches the weight display to a specific inventory.
        /// </summary>
        public void AttachToInventory(IInventory inventory)
        {
            if (_inventory == inventory)
                return;

            DetachFromInventory();

            _inventory = inventory;

            if (_inventory != null)
            {
                _inventory.Changed += UpdateWeightDisplay;
                UpdateWeightDisplay();
            }
        }

        /// <summary>
        /// Detaches the weight display from the current inventory.
        /// </summary>
        public void DetachFromInventory()
        {
            if (_inventory != null)
            {
                _inventory.Changed -= UpdateWeightDisplay;
                _inventory = null;
            }
        }

        /// <summary>
        /// Updates the weight display based on the current inventory's weight and capacity.
        /// </summary>
        private void UpdateWeightDisplay()
        {
            float maxWeight = _inventory.MaxWeight;
            float currentWeight = _inventory.Weight;

            // Update text and progress bar
            _weightText.text = $"{Math.Round(currentWeight, _decimals)} / {maxWeight} {ItemDefinition.WeightUnit}";
            _weightBar.SetFillAmount(maxWeight > 0 ? currentWeight / maxWeight : 0f);
        }

        private void Start()
        {
            if (_inventoryUI != null)
            {
                AttachToInventory(_inventoryUI.Inventory);
                _inventoryUI.AttachedInventoryChanged += AttachToInventory;
            }
        }

        private void OnDestroy()
        {
            DetachFromInventory();

            if (_inventoryUI != null)
                _inventoryUI.AttachedInventoryChanged -= AttachToInventory;
        }

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_inventoryUI == null)
                _inventoryUI = GetComponentInParent<InventoryUI>();
        }
#endif
        #endregion
    }
}