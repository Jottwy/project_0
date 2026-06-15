using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    public class FirearmItemPickup : WieldableItemPickup
    {
        [SerializeField, ReorderableList(elementLabel: "Config"), SpaceArea]
        private AttachmentItemConfiguration[] _configurations = Array.Empty<AttachmentItemConfiguration>();

        /// <inheritdoc/>
        public override void AttachItem(ItemStack item)
        {
            base.AttachItem(item);

            foreach (var config in _configurations)
                config.AttachToItem(AttachedItem);
        }

        /// <inheritdoc/>
        protected override ItemStack CreateDefaultItem()
        {
            var baseItem = base.CreateDefaultItem();

            foreach (var config in _configurations)
            {
                if (baseItem.Item.TryGetProperty(config.Property, out var property))
                    property.ItemId = config.CurrentItem;
            }

            return baseItem;
        }
        
        #region Internal Types
        [Serializable]
        private sealed class AttachmentItemConfiguration
        {
            [Tooltip("Attachment Type Property (e.g. Scope Attachment)")]
            public DataIdReference<ItemPropertyDefinition> Property;

            public DataIdReference<ItemDefinition> CurrentItem;

            [SpaceArea]
            [ReorderableList, LabelByChild("Object")]
            public ItemVisualsPair[] ItemVisuals;

            public void AttachToItem(ItemStack item)
            {
                if (item.Item.TryGetProperty(Property, out var property))
                    EnableConfigurationWithID(property.ItemId);
            }

            public void EnableConfigurationWithID(int id)
            {
                for (var i = 0; i < ItemVisuals.Length; i++)
                {
                    bool enable = ItemVisuals[i].Item == id;
                    ItemVisuals[i].Object.SetActive(enable);
                }
            }
        }

        [Serializable]
        private struct ItemVisualsPair
        {
            public DataIdReference<ItemDefinition> Item;
            public GameObject Object;
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();

            if (!Application.isPlaying && ItemDefinition.Initialized)
            {
                UnityUtility.SafeOnValidate(this, () =>
                {
                    foreach (var config in _configurations)
                        config.AttachToItem(CreateDefaultItem());
                });
            }
        }
#endif
        #endregion
    }
}