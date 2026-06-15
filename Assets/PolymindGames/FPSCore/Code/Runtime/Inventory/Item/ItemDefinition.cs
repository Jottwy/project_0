using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    [CreateAssetMenu(menuName = "Polymind Games/Items/Item Definition", fileName = "Item_")]
    public sealed class ItemDefinition : GroupMemberDefinition<ItemDefinition, ItemCategoryDefinition>
    {
        [SerializeField, SpritePreview, SpaceArea]
        [Tooltip("The icon representing this item in the UI, such as inventory slots or menus.")]
        private Sprite _icon;

        [SerializeField, NewLabel("Short Description")]
        [Tooltip("A brief description of the item for quick reference in tooltips or summary views.")]
        private string _description;

        [SerializeField, Multiline]
        [Tooltip("A detailed description of the item, used in more comprehensive UI displays. If left empty, the short description will be displayed instead.")]
        private string _longDescription;

        [SerializeField]
        [Tooltip("The prefab used for the item's physical representation in the game world when it is picked up or dropped.")]
        private ItemPickup _pickup;

        [SerializeField]
        [Tooltip("The prefab used when dropping or spawning this item in stacks greater than one. Helps distinguish stackable items visually in the game world.")]
        private ItemPickup _stackPickup;

        [SerializeField, Range(0.01f, 10f), SpaceArea]
        [Tooltip("The weight of a single unit of this item, in kilograms. This value influences inventory capacity calculations.")]
        private float _weight = 1f;

        [SerializeField, Range(1, 1000)]
        [Tooltip("The maximum number of this item that can be stacked in a single inventory slot. Set to 1 for non-stackable items.")]
        private int _stackSize = 1;

        [SerializeField]
        [DataReference(NullElement = ItemTagDefinition.Untagged)]
        [Tooltip("The tag associated with this item, used to group or filter items by category or behavior. Example: 'Weapon', 'Consumable'.")]
        private DataIdReference<ItemTagDefinition> _tag;

        [SerializeField]
        [Tooltip("The rarity level of this item. Can be used to influence item drop rates, pricing, or visual cues in the UI.")]
        private ItemRarityLevel _rarity;

        [SerializeField, SpaceArea]
        [ReorderableList(ListStyle.Lined, HasLabels = false)]
        [Help("Available actions for this item (the base actions from the parent category are also included)", UnityMessageType.None)]
        [Tooltip("A list of actions that can be performed with this item. These actions are in addition to base actions inherited from the parent category.")]
        private ItemAction[] _actions = Array.Empty<ItemAction>();

        [SerializeField]
        [ReorderableList(ListStyle.Lined, HasLabels = false)]
        [Help("Data that can be changed at runtime (not shared between item instances)", UnityMessageType.None)]
        [Tooltip("Dynamic properties that can change during runtime for individual instances of this item. These properties are not shared across all items of this type.")]
        private ItemPropertyGenerator[] _properties = Array.Empty<ItemPropertyGenerator>();

        [SerializeReference]
        [ReorderableList(elementLabel: "Data")]
        [ReferencePicker(typeof(ItemData), TypeGrouping.ByFlatName)]
        [Help("Data that is shared between all item instances of this type.", UnityMessageType.None)]
        [Tooltip("Static data shared between all instances of this item type. Example: predefined bonuses, crafting recipes etc.")]
        private ItemData[] _data;

        public const string WeightUnit = "KG";
        
        /// <summary>
        /// The icon representing the item definition.
        /// </summary>
        public override Sprite Icon => _icon;

        /// <summary>
        /// The description of the item definition.
        /// </summary>
        public override string Description => _description;

        /// <summary>
        /// The color associated with the item definition.
        /// </summary>
        public override Color Color => Rarity.Color;

        /// <summary>
        /// The long description of the item, which may be different from the short description.
        /// </summary>
        public string LongDescription => string.IsNullOrEmpty(_longDescription)
            ? _description
            : _longDescription;

        /// <summary>
        /// The pickup prefab associated with the item.
        /// </summary>
        public ItemPickup Pickup => _pickup;

        /// <summary>
        /// The pickup prefab associated with stacking multiple instances of the item.
        /// </summary>
        public ItemPickup StackPickup => _stackPickup;

        /// <summary>
        /// The maximum stack size for the item definition.
        /// </summary>
        public int StackSize => _stackSize;

        /// <summary>
        /// The weight of the item.
        /// </summary>
        public float Weight => _weight;

        /// <summary>
        /// The tag associated with the item definition.
        /// </summary>
        public DataIdReference<ItemTagDefinition> Tag => _tag;

        /// <summary>
        /// The rarity level of the item definition.
        /// </summary>
        public ItemRarityLevel Rarity
        {
            get
            {
                if (_rarity == null)
                    _rarity = ItemRarityLevel.DefaultRarity;

                return _rarity;
            }
        }

        /// <summary>
        /// The actions that can be performed with the item definition.
        /// </summary>
        public ItemAction[] Actions => _actions;

        /// <summary>
        /// All additional data associated with the item definition.
        /// </summary>
        public ItemData[] Data => _data;

        public ItemPickup GetPickupForItemCount(int count)
        {
            if (count > 1)
                return _stackPickup != null ? _stackPickup : _pickup;

            return _pickup;
        }

        #region Item Tag (Methods)
        public static List<ItemDefinition> GetAllItemsWithTag(ItemTagDefinition tag)
        {
            if (tag == null) return null;
            int tagId = tag.Id;
            if (tagId == 0) return null;

            var items = new List<ItemDefinition>();

            foreach (var item in Definitions)
            {
                if (item._tag == tagId)
                    items.Add(item);
            }

            return items;
        }
        #endregion

        #region Item Data (Methods)
        /// <summary>
        /// Tries to return an item data of type T.
        /// </summary>
        public bool TryGetDataOfType<T>(out T data) where T : ItemData
        {
            foreach (var itemData in _data)
            {
                if (itemData is T matchingData)
                {
                    data = matchingData;
                    return true;
                }
            }

            data = null;
            return false;
        }

        /// <summary>
        /// Returns an item data of the given type (if available).
        /// </summary>
        public T GetDataOfType<T>() where T : ItemData
        {
            foreach (var itemData in _data)
            {
                if (itemData is T matchingData)
                    return matchingData;
            }

            return null;
        }

        /// <summary>
        /// Checks if this item has an item data of type T attached.
        /// </summary>
        public bool HasDataOfType(Type type)
        {
            foreach (var itemData in _data)
            {
                if (itemData.GetType() == type)
                    return true;
            }
            
            return false;
        }
        #endregion

        #region Item Actions (Methods)
        /// <summary>
        /// Tries to return all of the items and item actions of type T.
        /// </summary>
        public static bool GetAllItemsWithAction<T>(out List<ItemDefinition> itemList, out List<T> actionList) where T : ItemAction
        {
            var items = Definitions;
            itemList = new List<ItemDefinition>();
            actionList = new List<T>();

            foreach (var item in items)
            {
                if (item.TryGetItemAction<T>(out var action))
                {
                    itemList.Add(item);
                    actionList.Add(action);
                }
            }

            return itemList.Count > 0;
        }

        /// <summary>
        /// Tries to return an item action of type T.
        /// </summary>
        public bool TryGetItemAction<T>(out T action) where T : ItemAction
        {
            action = GetActionOfType<T>();
            return false;
        }

        /// <summary>
        /// Returns an item action of the given type (if available).
        /// </summary>
        public T GetActionOfType<T>() where T : ItemAction
        {
            foreach (var action in _actions)
            {
                if (action is T acc)
                    return acc;
            }

            var parentActions = ParentGroup.BaseActions;
            foreach (var action in parentActions)
            {
                if (action is T acc)
                    return acc;
            }

            return null;
        }

        /// <summary>
        /// Checks if this item has an item action of type T attached.
        /// </summary>
        public bool HasActionOfType(Type type)
        {
            foreach (var action in _actions)
            {
                if (action.GetType() == type)
                    return true;
            }

            var parentActions = ParentGroup.BaseActions;
            foreach (var action in parentActions)
            {
                if (action.GetType() == type)
                    return true;
            }

            return false;
        }
        #endregion

        #region Item Properties (Methods)
        public static List<ItemDefinition> GetAllItemsWithProperty(ItemPropertyDefinition property)
        {
            if (property == null) return null;
            int propId = property.Id;
            if (propId == 0) return null;

            var items = new List<ItemDefinition>();

            foreach (var item in Definitions)
            {
                if (item.HasProperty(propId))
                    items.Add(item);
            }

            return items;
        }

        public ItemPropertyGenerator[] GetPropertyGenerators() => _properties;

        public bool HasProperty(DataIdReference<ItemPropertyDefinition> property)
        {
            foreach (var prop in _properties)
            {
                if (prop.Property == property)
                    return true;
            }

            return false;
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        public override void Validate_EditorOnly(in ValidationContext validationContext)
        {
            base.Validate_EditorOnly(in validationContext);

            switch (validationContext.Trigger)
            {
                case ValidationTrigger.Refresh:
                    CollectionExtensions.RemoveDuplicates(ref _actions);
                    RemoveDuplicateProperties();
                    break;
                case ValidationTrigger.Created:
                    if (HasParentGroup && _tag.IsNull)
                        _tag = ParentGroup.DefaultTag;
                    break;
                case ValidationTrigger.Duplicated:
                default:
                    return;
            }
        }

        private void RemoveDuplicateProperties() 
        {
            for (int i = 0; i < _properties.Length; i++)
            {
                for (int j = 0; j < _properties.Length; j++)
                {
                    if (i == j)
                        continue;

                    if (_properties[i].Property == _properties[j].Property)
                    {
                        _properties[j] = new ItemPropertyGenerator(null);
                        break;
                    }
                }
            }
        }
#endif
        #endregion
    }
}