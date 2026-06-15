using System.Diagnostics;
using System;

namespace PolymindGames
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    [Conditional("UNITY_EDITOR")]
    public sealed class OptionalCharacterComponentAttribute : Attribute
    {
        public OptionalCharacterComponentAttribute(params Type[] types)
        {
            Types = types;
        }

        public Type[] Types { get; }
    }
}