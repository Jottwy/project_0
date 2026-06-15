using PolymindGames.InventorySystem;
using System;

namespace PolymindGames
{
    /// <summary>
    /// A collection of filter methods for filtering items based on different criteria.
    /// </summary>
    public static partial class ItemFilters
    {
        /// <summary>
        /// Filters items by their name, ignoring case.
        /// </summary>
        public static Func<Item, bool> WithName(string name)
        {
            return item => StringComparer.OrdinalIgnoreCase.Equals(item.Name, name);
        }

        /// <summary>
        /// Filters items by their ID.
        /// </summary>
        public static Func<Item, bool> WithId(int id)
        {
            return item => item.Id == id;
        }

        /// <summary>
        /// Filters items by their associated tag name.
        /// </summary>
        public static Func<Item, bool> WithTag(string name)
        {
            return item => item.Definition.Tag == name;
        }

        /// <summary>
        /// Filters items by their name, ignoring case. This is essentially a duplicate of <see cref="WithName"/>.
        /// </summary>
        public static Func<Item, bool> WithData(string name)
        {
            return item => StringComparer.OrdinalIgnoreCase.Equals(item.Name, name);
        }
    }

    /// <summary>
    /// A collection of filter methods for filtering item slots based on different criteria.
    /// </summary>
    public static partial class ItemSlotFilters
    {
        /// <summary>
        /// A filter function that returns <c>true</c> if the item slot is empty (does not contain an item).
        /// </summary>
        public static readonly Func<SlotReference, bool> Empty = slot => !slot.HasItem();
        
        public static readonly Func<SlotReference, bool> EmptyOrLast =
            slot => !slot.HasItem() || slot.Index == slot.Container.SlotsCount - 1;

        /// <summary>
        /// Filters item slots that contain a specific item.
        /// </summary>
        public static Func<SlotReference, bool> WithItem(Item item)
        {
            return slot => slot.GetItem() == item;
        }
        
        /// <summary>
        /// Filters item slots that contain a specific item definition.
        /// </summary>
        public static Func<SlotReference, bool> WithItemId(int itemId)
        {
            return slot => slot.GetItem()?.Id == itemId;
        }
        
        /// <summary>
        /// Filters item slots that contain a specific item definition.
        /// </summary>
        public static Func<SlotReference, bool> WithItemDefinition(ItemDefinition itemDef)
        {
            return slot => slot.GetItem()?.Definition == itemDef;
        }

        /// <summary>
        /// Filters item slots based on a custom item filter.
        /// </summary>
        public static Func<SlotReference, bool> WithItemFilter(Func<Item, bool> itemFilter)
        {
            return slot => slot.TryGetItem(out var item) && itemFilter(item);
        }
    }

    /// <summary>
    /// A collection of filter methods for filtering item slots based on different criteria.
    /// </summary>
    public static partial class ItemContainerFilters
    {
        /// <summary>
        /// Filters item containers by their name, ignoring case.
        /// </summary>
        public static Func<IItemContainer, bool> WithName(string customName)
        {
            return container => StringComparer.OrdinalIgnoreCase.Equals(container.Name, customName);
        }

        /// <summary>
        /// A filter function that returns <c>true</c> if the container does not have a tag-based item add rule.
        /// </summary>
        public static readonly Func<IItemContainer, bool> WithoutTag = containers => !containers.TryGetRestriction(out TagContainerRestriction tagRule) || !tagRule.HasTags;

        /// <summary>
        /// Filters item containers that have a tag-based item add rule with the specified tag.
        /// </summary>
        public static Func<IItemContainer, bool> WithTag(DataIdReference<ItemTagDefinition> tag)
        {
            return container => container.TryGetRestriction(out TagContainerRestriction tagRestriction)
                ? tag.IsNull || tagRestriction.Tags.Contains(tag)
                : tag.IsNull;
        }
    }
}