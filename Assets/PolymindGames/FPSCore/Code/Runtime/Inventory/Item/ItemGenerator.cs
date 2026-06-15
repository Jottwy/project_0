using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Generates an item instance based on a few parameters.
    /// </summary>
    [Serializable]
    public struct ItemGenerator
#if UNITY_EDITOR
        : ISerializationCallbackReceiver
#endif
    {
        public enum ItemGenerationMethod : byte
        {
            Specific,
            Random,
            RandomFromCategory
        }

        [SerializeField, HideLabel, BeginHorizontal]
        [Tooltip("Defines how the item is generated. Options: Specific (fixed item), Random (any item), or RandomFromCategory (random item from a specified category).")]
        private ItemGenerationMethod _method;

        [HideLabel]
        [SerializeField, DataReference()]
        [ShowIf(nameof(_method), ItemGenerationMethod.Specific)]
        [Tooltip("Select a specific item to be generated. Only used if the generation method is set to 'Specific'.")]
        private ItemDefinition _item;

        [HideLabel]
        [SerializeField, DataReference(NullElement = "")]
        [ShowIf(nameof(_method), ItemGenerationMethod.RandomFromCategory)]
        [Tooltip("Select a category to randomly pick an item from. Only used if the generation method is 'RandomFromCategory'.")]
        private ItemCategoryDefinition _category;

        [EndHorizontal, HideLabel]
        [SerializeField, MinMaxSlider(0, 100)]
        private Vector2Int _count;

        public readonly ItemStack GenerateItem(IReadOnlyList<ContainerRestriction> restrictions = null)
        {
            switch (_method)
            {
                case ItemGenerationMethod.Specific:
                    return GenerateSpecificItem();
                case ItemGenerationMethod.RandomFromCategory:
                    return GenerateRandomItemFromCategory(restrictions ?? Array.Empty<ContainerRestriction>());
                case ItemGenerationMethod.Random:
                    return GenerateRandomItem(restrictions ?? Array.Empty<ContainerRestriction>());
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Generates a specific item based on the predefined item definition.
        /// </summary>
        private readonly ItemStack GenerateSpecificItem()
        {
            return CreateItemStack(_item);
        }

        /// <summary>
        /// Generates a random item from a specific category while applying restrictions.
        /// </summary>
        private readonly ItemStack GenerateRandomItemFromCategory(IReadOnlyList<ContainerRestriction> restrictions)
        {
            if (_category == null || _category.Members.Length == 0)
            {
                Debug.LogError("The assigned category is invalid or empty.");
                return default(ItemStack);
            }
            
            if (restrictions.Count > 0)
            {
                var closure = new RestrictionClosure(restrictions);
                var itemDef = _category.Members.SelectRandomFiltered(closure.AllowsItemDef);
                
                if (itemDef == null)
                {
                    Debug.LogError($"Couldn't find a random item in the {_category.Name} category. Ensure it has valid members.");
                    return ItemStack.Null;
                }
                
                return CreateItemStack(itemDef);
            }
            else
            {
                var itemDef = _category.Members.SelectRandom();
                return CreateItemStack(itemDef);
            }
        }

        /// <summary>
        /// Generates a completely random item from any available category while applying restrictions.
        /// </summary>
        private readonly ItemStack GenerateRandomItem(IReadOnlyList<ContainerRestriction> restrictions)
        {
            var category = ItemCategoryDefinition.Definitions.SelectRandomFiltered(c => c.Members.Length > 0);

            if (category == null)
            {
                Debug.LogError("Couldn't find a random item. Ensure there are item categories with valid members.");
                return ItemStack.Null;
            }

            var closure = new RestrictionClosure(restrictions);
            var itemDef = category.Members.SelectRandomFiltered(closure.AllowsItemDef);

            if (itemDef == null)
            {
                Debug.LogError("Couldn't find a valid item definition in the project.");
                return ItemStack.Null;
            }

            return CreateItemStack(itemDef);
        }

        /// <summary>
        /// Creates an ItemStack with a random count within the defined range.
        /// </summary>
        private readonly ItemStack CreateItemStack(ItemDefinition itemDef)
        {
            int itemCount = _count.GetRandomFromRange();

            if (itemCount == 0 || itemDef == null)
                return ItemStack.Null;

            return new ItemStack(new Item(itemDef), itemCount);
        }

        /// <summary>
        /// Helper struct to check if an item meets container restrictions.
        /// </summary>
        private readonly struct RestrictionClosure
        {
            private readonly IReadOnlyList<ContainerRestriction> _restrictions;

            public RestrictionClosure(IReadOnlyList<ContainerRestriction> restrictions)
            {
                _restrictions = restrictions ?? Array.Empty<ContainerRestriction>();
            }

            /// <summary>
            /// Determines whether an item definition satisfies all restrictions.
            /// </summary>
            public bool AllowsItemDef(ItemDefinition definition)
            {
                if (_restrictions.Count == 0)
                    return true;

                var dummyItem = Item.GetDummyItem(definition);
                foreach (var restriction in _restrictions)
                {
                    if (restriction.GetAllowedCount(null, dummyItem, 1) < 1)
                        return false;
                }

                return true;
            }
        }

#if UNITY_EDITOR
        readonly void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            _count = new Vector2Int(Mathf.Max(_count.x, 0), Mathf.Max(_count.y, 1));
        }
#endif
    }
}