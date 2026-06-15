using PolymindGames.InventorySystem;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    public sealed class ItemAttachmentsUI : CharacterUIBehaviour
    {
        [SerializeField, NotNull]
        [Tooltip("The template for creating attachment slots UI.")]
        private ItemSlotUI _attachmentTemplate;

        [SerializeField]
        [Tooltip("The root transform under which attachment slots will be instantiated.")]
        private RectTransform _attachmentSlotsRoot;

        [SpaceArea]
        [SerializeField, ReorderableList(elementLabel: "Attachment Type Slot")]
        [Tooltip("List of attachment slots that define where items can be attached.")]
        private AttachmentSlot[] _attachmentSlots = Array.Empty<AttachmentSlot>();

        private IInventoryInspectionManagerCC _inspectionManager;
        private bool _isActive;

        /// <summary>
        /// Initializes the component, setting the inspected slot to null.
        /// </summary>
        private void Start() => SetInspectedSlot(null);

        /// <summary>
        /// Sets the slot to inspect and updates attachment slots based on the item.
        /// </summary>
        /// <param name="slot">The slot to inspect. If null or invalid, all attachment slots are detached.</param>
        private void SetInspectedSlot(ItemSlotUIBase slot)
        {
            if (slot != null && slot.Slot.TryGetItem(out var wieldableItem))
            {
                foreach (var attachmentSlot in _attachmentSlots)
                    AttachSlot(wieldableItem, attachmentSlot);
            }
            else
            {
                foreach (var attachmentSlot in _attachmentSlots)
                    DetachSlot(attachmentSlot);
            }
        }

        private void AttachSlot(Item wieldableItem, AttachmentSlot attachmentSlot)
        {
            if (!wieldableItem.TryGetProperty(attachmentSlot.FirearmAttachmentProperty, out var wieldableProperty))
            {
                DetachSlot(attachmentSlot);
                return;
            }

            var attachmentItem = wieldableProperty.ItemId != 0 
                ? new Item(ItemDefinition.GetWithId(wieldableProperty.ItemId)) 
                : null;

            attachmentSlot.SlotUI.Slot.SetItem(new ItemStack(attachmentItem));

            if (!attachmentSlot.IsActive)
                ActivateSlot(attachmentSlot);
        }

        private void ActivateSlot(AttachmentSlot attachmentSlot)
        {
            var slot = attachmentSlot.SlotUI.Slot;
            slot.Changed += OnSlotChanged;
            _inspectionManager?.InspectContainer(slot.Container);
            attachmentSlot.SetActive(true);
        }

        private void DetachSlot(AttachmentSlot attachmentSlot)
        {
            if (!attachmentSlot.IsActive)
                return;

            var slot = attachmentSlot.SlotUI.Slot;
            slot.Changed -= OnSlotChanged;
            attachmentSlot.SetActive(false);
            _inspectionManager?.RemoveContainerFromInspection(slot.Container);
        }

        /// <summary>
        /// Handles changes to a slot.
        /// </summary>
        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType)
        {
            if (changeType == SlotChangeType.CountChanged)
                return;
            
            var selectedSlot = ItemSelector.Instance.SelectedSlot;
            var attachmentSlot = FindAttachmentSlot(slot);
            if (selectedSlot.Slot.GetItem().TryGetProperty(attachmentSlot.FirearmAttachmentProperty, out var itemProperty))
                itemProperty.ItemId = slot.GetItem()?.Id ?? 0;
        }

        /// <summary>
        /// Finds the attachment slot corresponding to the given item slot.
        /// </summary>
        /// <param name="slot">The slot to match.</param>
        /// <returns>The matching attachment slot, or null if none is found.</returns>
        private AttachmentSlot FindAttachmentSlot(SlotReference slot) =>
            Array.Find(_attachmentSlots, attachmentSlot => attachmentSlot.SlotUI.Slot == slot);

        /// <summary>
        /// Subscribes to the item selector's selected slot changed event when enabled.
        /// </summary>
        private void OnEnable()
        {
            ItemSelector.Instance.SelectedSlotChanged += SetInspectedSlot;
            if (ItemSelector.Instance.SelectedSlot != null)
                SetInspectedSlot(ItemSelector.Instance.SelectedSlot);
        }

        /// <summary>
        /// Unsubscribes from the item selector's selected slot changed event when disabled.
        /// </summary>
        private void OnDisable() => ItemSelector.Instance.SelectedSlotChanged -= SetInspectedSlot;

        protected override void OnCharacterAttached(ICharacter character)
        {
            base.OnCharacterAttached(character);
            _inspectionManager = character.GetCC<IInventoryInspectionManagerCC>();
        }
        
        /// <summary>
        /// Initializes attachment slots by instantiating templates and setting up their properties.
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            
            Transform root = _attachmentSlotsRoot != null ? _attachmentSlotsRoot : transform;
            foreach (var attachmentSlot in _attachmentSlots)
            {
                ItemSlotUI slot = Instantiate(_attachmentTemplate, root);
                slot.BgIconImage.sprite = attachmentSlot.BackgroundIcon;
                slot.gameObject.SetActive(false);
                attachmentSlot.SlotUI = slot;

                var container = new ItemContainer.Builder()
                    .WithSize(1)
                    .WithName(attachmentSlot.AttachmentTag.Name)
                    .WithRestriction(TagContainerRestriction.Create(TagContainerRestriction.AllowType.WithTags, attachmentSlot.AttachmentTag))
                    .Build();

                slot.AttachToSlot(container.GetSlot(0));
            }
        }

        #region Internal Types
        /// <summary>
        /// Represents a slot for an attachment in the UI, including background icon and property.
        /// </summary>
        [Serializable]
        private sealed class AttachmentSlot
        {
            [Tooltip("The background icon displayed for this attachment slot.")]
            public Sprite BackgroundIcon;

            [Tooltip("The item property linked to this attachment slot.")]
            public DataIdReference<ItemPropertyDefinition> FirearmAttachmentProperty;

            public DataIdReference<ItemTagDefinition> AttachmentTag;

            [NonSerialized]
            public ItemSlotUIBase SlotUI;
            
            /// <summary>
            /// Gets whether the attachment slot is currently active in the UI.
            /// </summary>
            public bool IsActive => SlotUI.gameObject.activeSelf;

            /// <summary>
            /// Sets the active state of the attachment slot.
            /// </summary>
            /// <param name="value">The value to set the active state to.</param>
            public void SetActive(bool value) => SlotUI.gameObject.SetActive(value);
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            // Check for duplicates...
            foreach (var attachmentSlot in _attachmentSlots)
            {
                foreach (var slot in _attachmentSlots)
                {
                    if (slot == attachmentSlot)
                        continue;

                    if (slot.FirearmAttachmentProperty == attachmentSlot.FirearmAttachmentProperty)
                    {
                        slot.FirearmAttachmentProperty = new DataIdReference<ItemPropertyDefinition>(string.Empty);
                        return;
                    }
                }
            }
        }
#endif
        #endregion

    }
}