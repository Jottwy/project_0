using PolymindGames.InventorySystem;
using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    [RequireComponent(typeof(IFirearm))]
    [AddComponentMenu("Polymind Games/Wieldables/Firearms/Item-Based Attachments")]
    public sealed class FirearmItemBasedAttachments : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("The audio data for the mode change.")]
        private AudioData _changeModeAudio = new(null);

        [SerializeField]
        [Tooltip("Determines if there's an animation for mode change.")]
        private bool _changeModeAnimation = true;

        [SerializeField, SpaceArea]
        [Tooltip("The configurations for attachment items.")]
        [ReorderableList(ListStyle.Lined, elementLabel: "Config", Foldable = false)]
        private AttachmentItemConfigurations[] _configurations;

        private IWieldableItem _wieldableItem;
        private Item _attachedItem;

        private void OnAttachedSlotChanged(SlotReference slot)
        {
            if (_attachedItem != null)
            {
                foreach (var config in _configurations)
                    config.DetachFromItem(_attachedItem);
            }

            var item = slot.GetItem();
            if (item != null)
            {
                foreach (var config in _configurations)
                    config.AttachToItem(item);
            }

            _attachedItem = item;
        }
        
        private void Awake()
        {
            _wieldableItem = GetComponentInParent<IWieldableItem>();

            if (_wieldableItem == null)
            {
                Debug.LogError($"No wieldable item found for {GetType().Name}!", gameObject);
                return;
            }

            _wieldableItem.AttachedSlotChanged += OnAttachedSlotChanged;
            OnAttachedSlotChanged(_wieldableItem.Slot);

            foreach (var config in _configurations)
            {
                config.AttachmentChangedCallback = OnAttachmentChanged;
            }
        }

        private void OnAttachmentChanged()
        {
            _wieldableItem.Wieldable.Audio.PlayClip(_changeModeAudio, BodyPoint.Hands);

            if (_changeModeAnimation)
            {
                _wieldableItem.Wieldable.Animator.SetTrigger(AnimationConstants.ChangeMode);
            }
        }

        #region Internal Types
        [Serializable]
        public sealed class AttachmentItemConfigurations
        {
            [SerializeField, NewLabel("Property")]
            private DataIdReference<ItemPropertyDefinition> _attachmentTypeProperty;

            [SerializeField, SpaceArea]
            [ReorderableList(ListStyle.Lined), LabelByChild("_attachment")]
            private AttachmentItemConfiguration[] _configurations;

            private AttachmentItemConfiguration _activeAttachment;
            
            public UnityAction AttachmentChangedCallback;

            public void AttachToItem(Item item)
            {
                if (item.TryGetProperty(_attachmentTypeProperty, out var property))
                {
                    AttachConfigurationWithID(property.ItemId);
                    property.Changed += OnPropertyChanged;
                }
            }

            public void DetachFromItem(Item item)
            {
                if (item.TryGetProperty(_attachmentTypeProperty, out var property))
                    property.Changed -= OnPropertyChanged;
            }

            private void OnPropertyChanged(ItemProperty property)
            {
                AttachConfigurationWithID(property.ItemId);
                AttachmentChangedCallback?.Invoke();
            }

            private void AttachConfigurationWithID(int itemId)
            {
                foreach (var config in _configurations)
                {
                    if (config.ItemId != itemId)
                        continue;

                    if (_activeAttachment != null)
                        _activeAttachment.Attachment.Detach();
                    
                    config.Attachment.Attach();
                    _activeAttachment = config;
                    return;
                }
            }
        }

        [Serializable]
        public sealed class AttachmentItemConfiguration
        {
            [SerializeField]
            private DataIdReference<ItemDefinition> _item;

            [SerializeField, NotNull]
            [Tooltip("This be just one attachment or a group in the form of a ''Firearm Mode''.")]
            private FirearmAttachment _attachment;

            public FirearmAttachment Attachment => _attachment;
            public int ItemId => _item;
        }
        #endregion
    }
}