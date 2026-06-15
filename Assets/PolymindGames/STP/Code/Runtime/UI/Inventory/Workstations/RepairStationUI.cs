using PolymindGames.InventorySystem;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class RepairStationUI : WorkstationInspectorBaseUI<RepairStation>
    {
        [SerializeField, SceneObjectOnly, Title("Settings")]
        private ItemContainerUI _container;

        [SerializeField, SceneObjectOnly]
        private SelectableButton _repairButton;

        [SerializeField, SceneObjectOnly, Title("Required Items")]
        private Transform _requiredItemsRoot;

        [SerializeField, PrefabObjectOnly]
        private RequirementUI _requiredItemTemplate;

        [SerializeField]
        private Color _enoughItemsColor = Color.gray;

        [SerializeField]
        private Color _notEnoughItemsColor = new(0.7f, 0f, 0f, 0.7f);

        [SerializeField, SceneObjectOnly, Title("Messages")]
        private TextMeshProUGUI _messageText;

        [SerializeField]
        private string _placeItemText = "Place item to repair";

        [SerializeField]
        private string _fullyRepairedText = "Fully Repaired";

        [SerializeField]
        private string _requiredItemsText = "Required Items";

        private RequirementUI[] _requirementsUI;

        protected override void OnCharacterAttached(ICharacter character)
        {
            // Activate the message text
            _messageText.gameObject.SetActive(true);

            // Instantiate the required item UIs and set them inactive
            _requirementsUI = new RequirementUI[4];
            for (int i = 0; i < _requirementsUI.Length; i++)
            {
                _requirementsUI[i] = Instantiate(_requiredItemTemplate, _requiredItemsRoot);
                _requirementsUI[i].ToggleVisibility(false);
            }

            // Subscribe to the repair button's selection event
            _repairButton.Clicked += OnRepairBtnClicked;
        }

        protected override void OnInspectionStarted(RepairStation workstation)
        {
            _container.AttachToContainer(workstation.GetContainers()[0]);
            workstation.GetContainers()[0].SlotChanged += OnRepairStationSlotChanged;
            Character.Inventory.Changed += UpdateUI;
            
            UpdateUI();
        }

        protected override void OnInspectionEnded(RepairStation workstation)
        {
            _container.DetachFromContainer();
            workstation.GetContainers()[0].SlotChanged -= OnRepairStationSlotChanged;
            Character.Inventory.Changed -= UpdateUI;
        }

        private void OnRepairBtnClicked(SelectableButton buttonSelectable)
        {
            if (Workstation.RepairDuration > 0.01f)
            {
                var repairParams = new CustomActionArgs("Repairing Item...", Workstation.RepairDuration, true, RepairItem, null);
                ActionManagerUI.Instance.StartAction(repairParams);
            }
            else
                RepairItem();
        }

        private void RepairItem() => Workstation.RepairItem(Character);
        private void OnRepairStationSlotChanged(in SlotReference slot, SlotChangeType type) => UpdateUI();

        private void UpdateUI()
        {
            bool hasItemToRepair = Workstation.ItemToRepair != null;
            bool canRepairItem = Workstation.CanRepairItem();
            bool hasEnoughItems = UpdateRequirementsUI();
            
            _messageText.text = hasItemToRepair
                ? canRepairItem ? _requiredItemsText : _fullyRepairedText
                : _placeItemText;

            _repairButton.IsInteractable = canRepairItem && hasEnoughItems;
        }

        private bool UpdateRequirementsUI()
        {
            bool hasEnoughItems = true;
            for (int i = 0; i < _requirementsUI.Length; i++)
            {
                if (!UpdateRequirementUI(i))
                    hasEnoughItems = false;
            }

            return hasEnoughItems;
        }

        private bool UpdateRequirementUI(int index)
        {
            bool isRequirementActive = Workstation.RepairRequirements.Count > index && Workstation.ItemToRepair != null;
            _requirementsUI[index].ToggleVisibility(isRequirementActive);
            
            if (isRequirementActive)
            {
                CraftRequirement requirement = Workstation.RepairRequirements[index];
                bool hasEnoughItems = Character.Inventory.GetItemCountById(requirement.Item) >= requirement.Amount;
                
                var definition = requirement.Item.Def;
                _requirementsUI[index].SetIconAndAmount(definition.Icon, $"x{requirement.Amount}", hasEnoughItems ? _enoughItemsColor : _notEnoughItemsColor);
                
                return hasEnoughItems;
            }

            return true;
        }
    }
}