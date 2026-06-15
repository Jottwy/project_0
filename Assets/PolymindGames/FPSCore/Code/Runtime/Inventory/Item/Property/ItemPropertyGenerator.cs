using System;
using UnityEngine;
using Random = UnityEngine.Random;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Generates an item property instance based on a few parameters.
    /// </summary>
    [Serializable]
    public struct ItemPropertyGenerator
    {
        [SerializeField]
        private int _itemPropertyId;

        [SerializeField]
        private bool _useRandomValue;

        [SerializeField]
        private Vector2 _valueRange;

        public ItemPropertyGenerator(ItemPropertyDefinition property)
        {
            _itemPropertyId = new DataIdReference<ItemPropertyDefinition>(property);
            _useRandomValue = false;
            _valueRange = Vector2.zero;
        }
        
        public readonly DataIdReference<ItemPropertyDefinition> Property => _itemPropertyId;

        public readonly ItemProperty GenerateItemProperty()
        {
            return new ItemProperty(Property.Def, GetValue());
        }

        public readonly double GetValue()
        {
            return ItemPropertyDefinition.GetWithId(_itemPropertyId).Type switch
            {
                ItemPropertyType.Integer => _useRandomValue ? Random.Range((int)_valueRange.x, (int)_valueRange.y) : _valueRange.x,
                ItemPropertyType.Float => _useRandomValue ? Random.Range(_valueRange.x, _valueRange.y) : _valueRange.x,
                ItemPropertyType.Double => _useRandomValue ? Random.Range(_valueRange.x, _valueRange.y) : _valueRange.x,
                ItemPropertyType.Boolean => _valueRange.x,
                ItemPropertyType.Item => _valueRange.x,
                _ => _valueRange.x
            };
        }
    }
}