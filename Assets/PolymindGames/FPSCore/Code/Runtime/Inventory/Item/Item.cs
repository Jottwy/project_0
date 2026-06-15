using System.Runtime.Serialization;
using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents a basic inventory item with properties and an item definition.
    /// </summary>
    [Serializable]
    public sealed class Item : IDeserializationCallback
    {
        [SerializeField]
        private int _id;

        [SerializeField]
        private ItemProperty[] _properties;

        [NonSerialized]
        private ItemDefinition _definition;

        private static readonly Item _dummyItem = new();

        /// <summary>
        /// Default constructor that creates an empty item.
        /// </summary>
        public Item()
        {
            _id = 0;
            _properties = Array.Empty<ItemProperty>();
        }

        /// <summary>
        /// Initializes an item from an item definition.
        /// </summary>
        /// <param name="itemDef">The item definition to use.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="itemDef"/> is null.</exception>
        public Item(ItemDefinition itemDef)
        {
            if (itemDef == null)
                throw new ArgumentNullException(nameof(itemDef), "Cannot create an item from a null item definition.");

            _id = itemDef.Id;
            _definition = itemDef;

            // Initialize item properties from definition.
            _properties = GenerateProperties(itemDef.GetPropertyGenerators());
        }

        /// <summary>
        /// Copy constructor that creates a new instance from an existing item.
        /// </summary>
        /// <param name="item">The item to copy.</param>
        public Item(Item item)
        {
            _id = item._id;
            _definition = item._definition;
            _properties = CloneProperties(item._properties);
        }

        /// <summary>
        /// Retrieves a dummy item associated with a given definition.
        /// </summary>
        /// <param name="definition">The item definition to associate with the dummy item.</param>
        /// <returns>A dummy item instance.</returns>
        public static Item GetDummyItem(ItemDefinition definition)
        {
            _dummyItem._id = definition?.Id ?? 0;
            _dummyItem._definition = definition;
            return _dummyItem;
        }

        /// <summary>
        /// Gets the unique identifier of the item.
        /// </summary>
        public int Id => _id;

        /// <summary>
        /// Gets the item definition associated with this item.
        /// </summary>
        public ItemDefinition Definition => _definition;

        /// <summary>
        /// Gets the name of the item.
        /// </summary>
        public string Name => _id == 0 ? string.Empty : Definition.Name;

        /// <summary>
        /// Gets a value indicating whether the item is stackable.
        /// </summary>
        public bool IsStackable => Definition.StackSize > 1;

        /// <summary>
        /// Gets the maximum stack size of the item.
        /// </summary>
        public int StackSize => Definition.StackSize;

        /// <summary>
        /// Gets the weight of the item.
        /// </summary>
        public float Weight => Definition.Weight;

        public override string ToString() => Name;

        /// <summary>
        /// Retrieves a property by its ID.
        /// </summary>
        /// <param name="id">The ID of the property to retrieve.</param>
        /// <returns>The matching property, or null if not found.</returns>
        public ItemProperty GetProperty(int id)
        {
            foreach (var property in _properties)
            {
                if (property.Id == id)
                    return property;
            }
            return null;
        }

        /// <summary>
        /// Attempts to retrieve a property by its ID.
        /// </summary>
        /// <param name="id">The ID of the property to retrieve.</param>
        /// <param name="itemProperty">The retrieved property, or null if not found.</param>
        /// <returns>True if the property was found; otherwise, false.</returns>
        public bool TryGetProperty(int id, out ItemProperty itemProperty)
        {
            foreach (var property in _properties)
            {
                if (property.Id == id)
                {
                    itemProperty = property;
                    return true;
                }
            }

            itemProperty = null;
            return false;
        }

        /// <summary>
        /// Clones the provided item properties.
        /// </summary>
        /// <param name="properties">The properties to clone.</param>
        /// <returns>A new array of cloned properties.</returns>
        private static ItemProperty[] CloneProperties(ItemProperty[] properties)
        {
            if (properties == null || properties.Length == 0)
                return Array.Empty<ItemProperty>();

            var clonedProperties = new ItemProperty[properties.Length];
            for (int i = 0; i < properties.Length; i++)
                clonedProperties[i] = properties[i].Clone();

            return clonedProperties;
        }

        /// <summary>
        /// Generates item properties from the provided property generators.
        /// </summary>
        /// <param name="propertyGenerators">An array of property generators.</param>
        /// <returns>An array of generated properties.</returns>
        private static ItemProperty[] GenerateProperties(ItemPropertyGenerator[] propertyGenerators)
        {
            if (propertyGenerators == null || propertyGenerators.Length == 0)
                return Array.Empty<ItemProperty>();

            var properties = new ItemProperty[propertyGenerators.Length];
            for (int i = 0; i < propertyGenerators.Length; i++)
                properties[i] = propertyGenerators[i].GenerateItemProperty();

            return properties;
        }
        
        public void OnDeserialization(object sender) => _definition = ItemDefinition.GetWithId(_id);
    }
}