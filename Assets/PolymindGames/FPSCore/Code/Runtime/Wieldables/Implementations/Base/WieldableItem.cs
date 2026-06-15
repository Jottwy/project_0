using PolymindGames.InventorySystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [RequireComponent(typeof(IWieldable))]
    [AddComponentMenu("Polymind Games/Wieldables/Behaviours/Wieldable Item")]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault3)]
    public class WieldableItem : MonoBehaviour, IWieldableItem
    {
        [SerializeField]
        [DataReference(HasLabel = true, HasIcon = true, HasAssetReference = true)]
        private DataIdReference<ItemDefinition> _referencedItem;

        private ItemProperty _durabilityProperty;
        private ActionBlockHandler _useBlocker;
        private IWieldable _wieldable;

        public SlotReference Slot { get; private set; }
        public DataIdReference<ItemDefinition> ReferencedItem => _referencedItem;
        
        public IWieldable Wieldable
        {
            get
            {
                if (_wieldable == null)
                {
                    _wieldable = GetComponent<Wieldable>();
                    OnInitialized(_wieldable);
                }
                return _wieldable;
            }
        }

        public event UnityAction<SlotReference> AttachedSlotChanged;

        public void AttachToSlot(SlotReference slot)
        {
            if (Slot == slot)
                return;

            Slot = slot;
            AttachedSlotChanged?.Invoke(slot);

            OnItemChanged(slot.GetStack());
        }

        protected virtual void OnItemChanged(ItemStack stack)
        {
            if (_useBlocker != null)
            {
                HandleDurability(stack);
            }
        }

        private void HandleDurability(ItemStack stack)
        {
            if (_durabilityProperty != null)
            {
                _durabilityProperty.Changed -= OnDurabilityChanged;
                _durabilityProperty = null;
                _useBlocker.RemoveBlocker(this);
            }

            if (stack.Item?.TryGetProperty(ItemConstants.Durability, out _durabilityProperty) ?? false)
            {
                _durabilityProperty.Changed += OnDurabilityChanged;
                OnDurabilityChanged(_durabilityProperty);
            }
        }

        private void OnDurabilityChanged(ItemProperty property)
        {
            if (property.Float < 0.01f)
            {
                _useBlocker.AddBlocker(this);
            }
            else
            {
                _useBlocker.RemoveBlocker(this);
            }
        }

        protected virtual void OnInitialized(IWieldable wieldable)
        {
            _useBlocker = wieldable is IUseInputHandler useInputHandler ? useInputHandler.UseBlocker : null;
        }

        private void OnDisable()
        {
            AttachToSlot(SlotReference.Null);
        }
    }
}