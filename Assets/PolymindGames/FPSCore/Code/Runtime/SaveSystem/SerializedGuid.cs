using System.Runtime.InteropServices;
using UnityEngine;
using System;

namespace PolymindGames.SaveSystem
{
    [Serializable]
    [StructLayout(LayoutKind.Explicit)]
    public struct SerializedGuid : IEquatable<Guid>
    {
        [FieldOffset(0)]
        public Guid Guid;

        [SerializeField, FieldOffset(0)]
        private int _guidPart1;

        [SerializeField, FieldOffset(4)]
        private int _guidPart2;

        [SerializeField, FieldOffset(8)]
        private int _guidPart3;

        [SerializeField, FieldOffset(12)]
        private int _guidPart4;

        public SerializedGuid(Guid guid)
        {
            _guidPart1 = 0;
            _guidPart2 = 0;
            _guidPart3 = 0;
            _guidPart4 = 0;
            Guid = guid;
        }

        public static SerializedGuid Empty => new(Guid.Empty);

        public static implicit operator Guid(SerializedGuid other) => other.Guid;
        public static implicit operator SerializedGuid(Guid other) => new(other);
        public static bool operator ==(SerializedGuid x, SerializedGuid y) => x.Guid == y.Guid;
        public static bool operator ==(SerializedGuid x, Guid y) => x.Guid == y;
        public static bool operator !=(SerializedGuid x, SerializedGuid y) => x.Guid != y.Guid;
        public static bool operator !=(SerializedGuid x, Guid y) => x.Guid != y;

        public readonly bool Equals(Guid other) => Guid == other;
        public override readonly int GetHashCode() => Guid.GetHashCode();
        public override readonly string ToString() => Guid.ToString();

        public override readonly bool Equals(object obj)
        {
            return obj switch
            {
                null => false,
                SerializedGuid serializedGuid => serializedGuid == Guid,
                Guid guid => guid == Guid,
                _ => false
            };

        }
    }
}