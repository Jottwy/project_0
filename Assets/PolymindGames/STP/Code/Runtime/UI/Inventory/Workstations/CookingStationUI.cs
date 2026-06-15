using PolymindGames.WorldManagement;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class CookingStationUI : WorkstationInspectorBaseUI<CookingStation>
    {
        [SerializeField, Title("References")]
        private FuelSelectorUI _fuelSelector;

        [SerializeField]
        private ItemContainerUI _itemContainer;

        [SerializeField]
        private SelectableButton _startFireBtn;

        [SerializeField]
        private SelectableButton _addFuelBtn;

        [SerializeField]
        private SelectableButton _extinguishBtn;

        [SerializeField]
        private TextMeshProUGUI _descriptionText;

        private const string ExtinguishFire = "Extinguishing Fire...";
        private const string IgnitingFire = "Igniting Fire...";

        protected override void OnInspectionStarted(CookingStation workstation)
        {
            // Attach item container to the workstation container
            _itemContainer.AttachToContainer(workstation.GetContainers()[0]);
            
            // Attach fuel selector to the character's inventory
            _fuelSelector.AttachToInventory(Character.Inventory);

            // Subscribe to events for updating description and buttons
            workstation.CookingUpdated += UpdateDescription;
            workstation.CookingStarted += UpdateUI;
            workstation.CookingStopped += UpdateUI;

            // Update description and buttons
            UpdateUI();
        }

        protected override void OnInspectionEnded(CookingStation workstation)
        {
            // Detach item container from the workstation container
            _itemContainer.DetachFromContainer();
            
            // Detach fuel selector from the character's inventory
            _fuelSelector.DetachFromInventory();

            // Unsubscribe from events
            workstation.CookingUpdated -= UpdateDescription;
            workstation.CookingStarted -= UpdateUI;
            workstation.CookingStopped -= UpdateUI;
        }

        private void Start()
        {
            // Subscribe button events
            _startFireBtn.Clicked += StartCooking;
            _extinguishBtn.Clicked += StopCooking;
            _addFuelBtn.Clicked += _ => AddFuel();
        }

        /// <summary>
        /// Starts the cooking process if fuel is selected.
        /// </summary>
        private void StartCooking(SelectableButton buttonSelectable)
        {
            if (_fuelSelector.SelectedFuel == null)
                return;

            if (Character.Inventory.ContainsItemById(_fuelSelector.SelectedFuel.Item))
            {
                float delay = Workstation.QueueStartCooking() + 0.01f;
                ActionManagerUI.Instance.StartAction(new CustomActionArgs(IgnitingFire, delay, true, AddFuel, Workstation.CancelQueues));
            }
        }

        /// <summary>
        /// Stops the cooking process.
        /// </summary>
        private void StopCooking(SelectableButton buttonSelectable)
        {
            float delay = Workstation.QueueStopCooking() + 0.01f;
            ActionManagerUI.Instance.StartAction(new CustomActionArgs(ExtinguishFire, delay, true, UpdateDescription, Workstation.CancelQueues));
        }

        /// <summary>
        /// Updates the visibility of buttons based on the cooking status.
        /// </summary>
        private void UpdateUI()
        {
            bool isCookingActive = Workstation.IsCookingActive;
            _startFireBtn.gameObject.SetActive(!isCookingActive);
            _addFuelBtn.gameObject.SetActive(isCookingActive);
            _extinguishBtn.IsInteractable = isCookingActive;
            UpdateDescription();
        }

        /// <summary>
        /// Updates the description text based on the cooking station description.
        /// </summary>
        private void UpdateDescription()
        {
            if (Workstation.IsCookingActive)
            {
                float realSecondsLeft = (int)(World.Instance.Time.DayTimeIncrementPerSecond * (1440 * Workstation.CookingTimeLeft));
                _descriptionText.text = $"Game Duration: {WorldExtensions.FormatMinuteWithSuffixes(Workstation.CookingTimeLeft)}\nReal Duration: {realSecondsLeft}s";
            }
            else
            {
                _descriptionText.text = string.Empty;
            }
        }

        /// <summary>
        /// Adds fuel to the cooking station.
        /// </summary>
        private void AddFuel()
        {
            if (_fuelSelector.SelectedFuel == null)
                return;

            if (Character.Inventory.RemoveItemsById(_fuelSelector.SelectedFuel.Item, 1) > 0)
            {
                Workstation.AddFuel((int)_fuelSelector.SelectedFuel.Capacity);
                UpdateDescription();
            }
        }
    }
}