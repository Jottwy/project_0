using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents a restriction where items must have or must not have specific tags to be added.
    /// </summary>
    [CreateAssetMenu(menuName = CreateMenuPath + "Tag Restriction")]
    public sealed class TagContainerRestriction : ContainerRestriction
    {
        public enum AllowType : byte
        {
            WithTags,
            WithoutTags
        }

        [SerializeField]
        private AllowType _allowType;

        [SerializeField, ReorderableList(HasLabels = false)]
        [SpaceArea, DataReference(NullElement = "")]
        private DataIdReference<ItemTagDefinition>[] _tags = Array.Empty<DataIdReference<ItemTagDefinition>>();

        private TagContainerRestriction() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="TagContainerRestriction"/> class with the specified allow type and tags.
        /// </summary>
        /// <param name="allowType">The rule type determining whether the item must have or must not have the specified tags.</param>
        /// <param name="tags">The tags associated with the rule.</param>
        public static TagContainerRestriction Create(AllowType allowType, params DataIdReference<ItemTagDefinition>[] tags)
        {
            var instance = CreateInstance<TagContainerRestriction>();
            instance._allowType = allowType;
            instance._tags = tags;
            return instance;
        }

        /// <summary>
        /// Gets the tags associated with the rule.
        /// </summary>
        public DataIdReference<ItemTagDefinition>[] Tags => _tags;

        public bool HasTags => _tags.Length > 0;

        /// <inheritdoc/>
        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount)
        {
            var targetTag = item.Definition.Tag;
            switch (_allowType)
            {
                case AllowType.WithTags:
                    foreach (var tag in _tags)
                    {
                        if (targetTag == tag)
                            return requestedCount;
                    }
                    return 0;
                case AllowType.WithoutTags:
                    foreach (var tag in _tags)
                    {
                        if (targetTag == tag)
                            return 0;
                    }
                    return requestedCount;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}