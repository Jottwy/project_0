using UnityEngine;
using System;

namespace PolymindGames
{
    /// <summary>
    /// A serializable struct wrapper for an enum type that provides equality comparison based on its underlying enum value.
    /// </summary>
    /// <typeparam name="TEnum">The enum type being wrapped.</typeparam>
    [Serializable]
    public struct EquatableEnum<TEnum> : IEquatable<EquatableEnum<TEnum>> where TEnum : struct, Enum
    {
        [SerializeField]
        private TEnum _value;
        
        /// <summary>
        /// The wrapped enum value.
        /// </summary>
        public readonly TEnum Value => _value;

        public EquatableEnum(TEnum value)
        {
            _value = value;
        }

        public override readonly bool Equals(object obj)
        {
            if (obj is EquatableEnum<TEnum> other)
                return Equals(other);

            return false;
        }
        
        public readonly bool Equals(EquatableEnum<TEnum> other) =>
            _value.Equals(other._value);

        public override readonly int GetHashCode() => _value.GetHashCode();
        
        public static implicit operator TEnum(EquatableEnum<TEnum> value) => value._value;
        public static implicit operator EquatableEnum<TEnum>(TEnum value) => new(value);

        public static bool operator ==(EquatableEnum<TEnum> left, EquatableEnum<TEnum> right) =>
            left.Equals(right);

        public static bool operator !=(EquatableEnum<TEnum> left, EquatableEnum<TEnum> right) =>
            !(left == right);
    }
}