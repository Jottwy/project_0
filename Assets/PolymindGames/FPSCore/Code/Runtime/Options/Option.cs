using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames.Options
{
    public interface IOption
    {
        object BoxedValue { get; set; }
    }

    [Serializable]
    public sealed class Option<V> : IOption where V : struct, IEquatable<V>
    {
        [SerializeField]
        private V _value;

        public Option(V value = default(V))
        {
            _value = value;
        }

        object IOption.BoxedValue
        {
            get => _value;
            set => SetValue(value as V? ?? default(V));
        }
        
        public V Value => _value;

        public void SetValue(V value)
        {
            if (_value.Equals(value))
                return;

            _value = value;
            Changed?.Invoke(value);
        }

        public event UnityAction<V> Changed;

        public static implicit operator V(Option<V> option) => option.Value;
    }
}