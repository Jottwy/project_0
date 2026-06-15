using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    public enum ItemPropertyType
    {
        Boolean,
        Integer,
        Float,
        Double,
        Item
    }

    /// <summary>
    /// Item properties hold values that can be changed and manipulated at runtime resulting in dynamic behaviour (float, bool and integer).
    /// </summary>
    [Serializable]
    public sealed class ItemProperty
    {
        [SerializeField]
        private int _id;

        [SerializeField]
        private double _value;
        
        public int Id => _id;
        public string Name => ItemPropertyDefinition.GetWithId(_id).Name;

        public bool Boolean
        {
            get => _value > 0f;
            set => SetInternalValue(value ? 1f : 0f);
        }

        public int Integer
        {
            get => (int)_value;
            set => SetInternalValue(value);
        }

        public double Double
        {
            get => _value;
            set => SetInternalValue(value);
        }

        public float Float
        {
            get => (float)_value;
            set => SetInternalValue(value);
        }

        public int ItemId
        {
            get => (int)_value;
            set => SetInternalValue(value);
        }

        public ItemProperty(ItemPropertyDefinition definition, double value)
        {
            _id = definition.Id;
            _value = value;
        }

        public event PropertyChangedDelegate Changed;

        public ItemProperty Clone() => (ItemProperty)MemberwiseClone();

        private void SetInternalValue(double value)
        {
            double oldValue = _value;
            _value = value;

            if (Math.Abs(oldValue - _value) > 0.001)
                Changed?.Invoke(this);
        }
    }

    public delegate void PropertyChangedDelegate(ItemProperty property);
}