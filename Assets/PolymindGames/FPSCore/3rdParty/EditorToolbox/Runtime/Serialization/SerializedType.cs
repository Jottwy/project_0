using UnityEngine.Serialization;
using System;

namespace UnityEngine
{
    /// <summary>
    /// Reference to a class <see cref="System.Type"/> with support for Unity serialization.
    /// </summary>
    [Serializable]
    public sealed class SerializedType : ISerializationCallbackReceiver
    {
        [SerializeField, FormerlySerializedAs("classReference")]
        private string typeReference;

        private Type _type;

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializedType"/> class.
        /// </summary>
        public SerializedType()
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializedType"/> class.
        /// </summary>
        /// <param name="assemblyQualifiedTypeName">Assembly qualified class name.</param>
        public SerializedType(string assemblyQualifiedTypeName)
        {
            Type = !string.IsNullOrEmpty(assemblyQualifiedTypeName)
                ? Type.GetType(assemblyQualifiedTypeName)
                : null;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializedType"/> class.
        /// </summary>
        /// <param name="type">Class type.</param>
        /// <exception cref="ArgumentException">
        /// If <paramref name="type"/> is not a class type.
        /// </exception>
        public SerializedType(Type type)
        {
            Type = type;
        }

        public static string GetReferenceValue(Type type)
        {
            return type != null
                ? type.FullName + ", " + type.Assembly.GetName().Name
                : string.Empty;
        }

        public static Type GetReferenceType(string referenceValue)
        {
            return !string.IsNullOrEmpty(referenceValue)
                ? Type.GetType(referenceValue)
                : null;
        }

        public override string ToString()
        {
            return (Type != null ? Type.FullName : $"(None)") ?? string.Empty;
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (!string.IsNullOrEmpty(typeReference))
            {
                _type = Type.GetType(typeReference);
//                 if (_type == null)
//                 {
// #if UNITY_EDITOR
//                     Debug.LogWarning($"'{typeReference}' was referenced but class type was not found.");
// #endif
//                 }
            }
            else
            {
                _type = null;
            }
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        { }

        /// <summary>
        /// Gets or sets type of class reference.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// If <paramref name="value"/> is not a class type.
        /// </exception>
        public Type Type
        {
            get => _type;
            set
            {
                if (value != null && !value.IsClass)
                {
                    throw new ArgumentException($"'{value.FullName}' is not a class type.", nameof(value));
                }

                _type = value;
                typeReference = GetReferenceValue(value);
            }
        }

        public static implicit operator string(SerializedType typeReference) => typeReference.typeReference;
        public static implicit operator Type(SerializedType typeReference) => typeReference.Type;
        public static implicit operator SerializedType(Type type) => new(type);
    }
}