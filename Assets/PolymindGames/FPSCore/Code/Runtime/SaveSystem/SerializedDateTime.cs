using System.Runtime.InteropServices;
using System.Globalization;
using UnityEngine;
using System;

namespace PolymindGames.SaveSystem
{
    [StructLayout(LayoutKind.Explicit), Serializable]
    public struct SerializedDateTime : IEquatable<DateTime>
    {
        [FieldOffset(0)]
        public DateTime DateTime;

        [FieldOffset(0), SerializeField]
        private ulong _ticks;
        
        
        public SerializedDateTime(DateTime time)
        {
            _ticks = 0;
            DateTime = time;
        }

        public static implicit operator DateTime(SerializedDateTime other) => other.DateTime;
        public static implicit operator SerializedDateTime(DateTime other) => new(other);
        public static bool operator ==(SerializedDateTime x, SerializedDateTime y) => x.DateTime == y.DateTime;
        public static bool operator ==(SerializedDateTime x, DateTime y) => x.DateTime == y;
        public static bool operator !=(SerializedDateTime x, SerializedDateTime y) => x.DateTime != y.DateTime;
        public static bool operator !=(SerializedDateTime x, DateTime y) => x.DateTime != y;

        public readonly bool Equals(DateTime other) => DateTime == other;
        public override readonly int GetHashCode() => DateTime.GetHashCode();
        public override readonly string ToString() => DateTime.ToString(CultureInfo.InvariantCulture);

        public override readonly bool Equals(object obj)
        {
            return obj switch
            {
                null => false,
                SerializedDateTime serializedDateTime => serializedDateTime == DateTime,
                DateTime dateTime => dateTime == DateTime,
                _ => false
            };

        }
    }
}