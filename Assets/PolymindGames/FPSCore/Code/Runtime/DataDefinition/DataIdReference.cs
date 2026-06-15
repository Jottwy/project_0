using UnityEngine;
using System;

namespace PolymindGames
{
    /// <summary>
    /// Represents a reference to a data definition identified by an integer ID.
    /// </summary>
    /// <typeparam name="T">The type of data definition associated with the reference.</typeparam>
    [Serializable]
    public struct DataIdReference<T> : IEquatable<DataIdReference<T>> where T : DataDefinition<T>
    {
        [SerializeField]
        private int _value;
        
        /// <summary>
        /// A predefined null reference for initializing or representing absence of reference.
        /// </summary>
        public static readonly DataIdReference<T> NullRef = new(0);
        
        public readonly T Def => DataDefinition<T>.GetWithId(_value);
        public readonly bool IsNull => _value == NullRef._value;
        public readonly int Id => _value;
        public readonly string Name => IsNull ? string.Empty : Def.Name;
        public readonly Sprite Icon => IsNull ? null : Def.Icon;
        public readonly string Description => IsNull ? string.Empty : Def.Description;
        
		#region Constructors
        public DataIdReference(T def)
        {
            _value = def != null ? def.Id : NullRef._value;
        }

        public DataIdReference(int id)
        {
            _value = id;
        }

        public DataIdReference(string name)
        {
            _value = DataDefinition<T>.TryGetWithName(name, out var def)
                ? def.Id
                : NullRef._value;
        }
		#endregion

		#region Operators
        public static bool operator ==(DataIdReference<T> x, DataIdReference<T> y) => x._value == y._value;
        public static bool operator ==(DataIdReference<T> x, T y) => y != null && x._value == y.Id;
        public static bool operator ==(DataIdReference<T> x, int y) => x._value == y;
        public static bool operator ==(DataIdReference<T> x, string y) => x.Name == y;

        public static bool operator !=(DataIdReference<T> x, DataIdReference<T> y) => x._value != y._value;
        public static bool operator !=(DataIdReference<T> x, T y) => y != null && x._value != y.Id;
        public static bool operator !=(DataIdReference<T> x, int y) => x._value != y;
        public static bool operator !=(DataIdReference<T> x, string y) => x.Name != y;

        public static implicit operator DataIdReference<T>(int value) => new(value);
        public static implicit operator DataIdReference<T>(string value) => new(value);
        public static implicit operator DataIdReference<T>(T value) => new(value);

        public static implicit operator int(DataIdReference<T> reference) => reference.Id;
        public static implicit operator T(DataIdReference<T> reference) => reference.Def;
		#endregion

		#region IEquatable Implementation
        public readonly bool Equals(DataIdReference<T> other) => _value == other._value;

        public override readonly bool Equals(object obj)
        {
            return obj switch
            {
                DataIdReference<T> reference => _value == reference._value,
                int val => _value == val,
                _ => false
            };

        }

        public override readonly int GetHashCode() => _value.GetHashCode();
        public override readonly string ToString() => _value != NullRef._value ? Name : string.Empty;
		#endregion
    }
}