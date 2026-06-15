using System;
using System.Diagnostics;

namespace PolymindGames
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    [Conditional("UNITY_EDITOR")]
    public sealed class RequireCharacterComponentAttribute : Attribute
    {
        public RequireCharacterComponentAttribute(params Type[] types)
        {
            Types = types;
        }

        public Type[] Types { get; }
    }
}
