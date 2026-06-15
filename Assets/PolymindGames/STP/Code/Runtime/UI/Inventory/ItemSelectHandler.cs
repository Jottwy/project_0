using PolymindGames.InventorySystem;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public sealed class ItemSelectHandler : ItemSelector, ICharacterUIBehaviour
    {
        [SerializeField, NotNull]
        private SelectableGroupBase _itemSlotsGroup;

        private ICharacterUI _characterUI;

        #region ICharacterUIBehaviour implementation
        protected override void Awake()
        {
            base.Awake();
            _characterUI = gameObject.GetComponentInParent<ICharacterUI>();
            _characterUI?.AddBehaviour(this);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            _characterUI?.RemoveBehaviour(this);
        }
        
        void ICharacterUIBehaviour.OnCharacterChanged(ICharacter prevCharacter, ICharacter newCharacter)
        {
            if (prevCharacter != null)
            {
                var inspection = prevCharacter.GetCC<IInventoryInspectionManagerCC>();
                inspection.InspectionPostStarted -= OnInspectionPostStarted;
                inspection.InspectionEnded -= OnInspectionStopped;
            }
            
            if (newCharacter != null)
            {
                var inspection = newCharacter.GetCC<IInventoryInspectionManagerCC>();
                inspection.InspectionPostStarted += OnInspectionPostStarted;
                inspection.InspectionEnded += OnInspectionStopped;
            }
        }
        #endregion

        private void OnInspectionPostStarted()
        {
            SetSelectedSlot(_itemSlotsGroup.Selected);
            RaiseSelectedEvent();
            
            _itemSlotsGroup.SelectedChanged += SetSelectedSlot;
            _itemSlotsGroup.HighlightedChanged += SetHighlightedSlot;
        }

        private void OnInspectionStopped()
        {
            SetSelectedSlot(null);
            
            _itemSlotsGroup.SelectedChanged -= SetSelectedSlot;
            _itemSlotsGroup.HighlightedChanged -= SetHighlightedSlot;
        }

        private void SetSelectedSlot(SelectableButton buttonSelectable)
        {
            var slot = buttonSelectable == null ? null : buttonSelectable.GetComponent<ItemSlotUIBase>();

            if (SelectedSlot != null)
            {
                SelectedSlot.Slot.Changed -= OnSlotChanged;
            }

            if (slot != null && slot.HasSlot)
            {
                slot.Slot.Changed += OnSlotChanged;
                SelectedSlot = slot;
            }
            else
            {
                SelectedSlot = null;
            }
        }

        private void SetHighlightedSlot(SelectableButton highlighted)
        {
            var slot = highlighted == null ? null : highlighted.GetComponent<ItemSlotUIBase>();
            HighlightedSlot = slot;
        }

        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType) => RaiseSelectedEvent();
    }
}