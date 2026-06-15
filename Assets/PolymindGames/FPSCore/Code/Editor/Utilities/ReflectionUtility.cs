using System.Collections.Generic;
using System.Reflection;
using System;
using UnityEngine;

namespace PolymindGames.Editor
{
    public static class ReflectionUtility
    {
        /// <summary>
        /// Retrieves the names of all serialized fields for a given type, including inherited fields.
        /// </summary>
        /// <param name="type">The type to inspect for serialized fields.</param>
        /// <returns>A list of serialized field names.</returns>
        public static List<string> GetSerializedFieldNames(this Type type)
        {
            var fieldNames = new List<string>();

            // Traverse the type hierarchy up to the base class.
            while (type != null && type != typeof(object))
            {
                // Get all fields with BindingFlags to include non-public fields marked with [SerializeField].
                FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                foreach (var field in fields)
                {
                    // Check if the field is serialized (public or marked with [SerializeField]).
                    if (field.IsPublic || Attribute.IsDefined(field, typeof(SerializeField)))
                    {
                        fieldNames.Add(field.Name);
                    }
                }

                // Move to the base type.
                type = type.BaseType;
            }

            return fieldNames;
        }
        
        /// <summary>
        /// Returns the FieldInfo matching 'name' from either type 't' itself or its most-derived 
        /// base type (unlike 'System.Type.GetField'). Returns null if no match is found.
        /// </summary>
        public static FieldInfo GetPrivateField(this Type t, string name)
        {
            const BindingFlags Flags = BindingFlags.Instance |
                                       BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly;

            int iterations = 0;

            FieldInfo fi;
            while ((fi = t.GetField(name, Flags)) == null && (t = t.BaseType) != null && iterations < 12)
                iterations++;
            return fi;
        }

        public static void SetFieldValue(this object source, string fieldName, object value)
        {
            Type sourceType = source.GetType();
            FieldInfo field = sourceType.GetPrivateField(fieldName);
            field.SetValue(source, value);
        }
    }
}