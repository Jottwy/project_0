using PolymindGames.InventorySystem;
using System.Collections.Generic;
using PolymindGames.SaveSystem;
using UnityEngine;

namespace PolymindGames
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/interaction/interactable/demo-interactables")]
    public sealed class RepairStation : Workstation, ISaveableComponent
    {
        [SerializeField, Range(0f, 25f), Title("Repairing")]
        [Tooltip("The time it takes to repair an item at this station.")]
        private float _repairDuration = 1f;

        [SerializeField]
        [Tooltip("Repair sound to be played after successfully repairing an item.")]
        private AudioData _repairAudio = new(null);

        private readonly List<CraftRequirement> _repairRequirements = new();
        private readonly IItemContainer[] _containers = new IItemContainer[1];

        public IReadOnlyList<CraftRequirement> RepairRequirements => _repairRequirements;
        public Item ItemToRepair => _containers[0].GetSlot(0).GetItem();
        public float RepairDuration => _repairDuration;

        public override IReadOnlyList<IItemContainer> GetContainers()
        {
            _containers[0] ??= GenerateDefaultContainer();
            return _containers;
        }

        /// <summary>
        /// Checks if the attached item can be repaired
        /// </summary>
        public bool CanRepairItem()
        {
            if (ItemToRepair == null)
                return false;

            // Get the durability property of the item
            ItemProperty durability = ItemToRepair.GetProperty(ItemConstants.Durability);

            // Check if the item's durability is less than max durability.
            return !Mathf.Approximately(durability.Float, 1f);
        }

        public void RepairItem(ICharacter character)
        {
            // Remove required items from the character's inventory
            foreach (var req in _repairRequirements)
                character.Inventory.RemoveItemsById(req.Item, req.Amount);

            // Set the durability of the item to maximum
            ItemToRepair.GetProperty(ItemConstants.Durability).Float = 1f;

            // Play repair audio if available
            AudioManager.Instance.PlayClip3D(_repairAudio, transform.position);
        }
        
        private IItemContainer GenerateDefaultContainer()
        {
            var container = new ItemContainer.Builder()
                .WithName(nameof(RepairStation))
                .WithSize(1)
                .WithAllowStacking(false)
                .WithRestriction(ItemDataTypeAddRule.Create(typeof(CraftingData)))
                .WithRestriction(PropertyContainerRestriction.Create(ItemConstants.Durability))
                .Build();
            
            container.SlotChanged += OnSlotChanged;
            return container;
        }

        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType)
        {
            // Clear the list of repair requirements
            _repairRequirements.Clear();
            
            if (!slot.TryGetItem(out var item))
                return;

            // Get the current durability of the item
            var durabilityProperty = item.GetProperty(ItemConstants.Durability);
            float durability = Mathf.Clamp01(durabilityProperty.Float);

            if (durability > 0.99f)
                return;

            // Calculate and add repair requirements based on crafting data blueprint
            var craftData = item.Definition.GetDataOfType<CraftingData>();
            foreach (var requirement in craftData.Blueprint)
            {
                // Calculate the required amount based on the current durability
                float missingDurability = 1f - durability;
                int requiredAmount = Mathf.RoundToInt(requirement.Amount * missingDurability);
            
                // Add the repair requirement to the list
                if (requiredAmount > 0)
                {
                    _repairRequirements.Add(new CraftRequirement(requirement.Item, requiredAmount));
                }
            }
        }

        #region Save & Load
        void ISaveableComponent.LoadMembers(object data)
        {
            if (data is ItemContainer container)
            {
                _containers[0] = container;
                container.InitializeAfterDeserialization(null, null);
                container.SlotChanged += OnSlotChanged;
                OnSlotChanged(container.GetSlot(0), SlotChangeType.ItemChanged);
            }
        }

        object ISaveableComponent.SaveMembers() => _containers[0];
        #endregion
    }
}