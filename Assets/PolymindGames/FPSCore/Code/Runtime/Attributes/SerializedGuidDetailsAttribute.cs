using System;
using System.Diagnostics;

namespace PolymindGames
{
    [AttributeUsage(AttributeTargets.Field)]
    [Conditional("UNITY_EDITOR")]
    public sealed class SerializedGuidDetailsAttribute : Attribute
    {
        public bool DisableForPrefabs { get; }
        public bool HasNewGuidButton { get; }
        
        public SerializedGuidDetailsAttribute(bool disableForPrefabs, bool hasNewGuidButton)
        {
            DisableForPrefabs = disableForPrefabs;
            HasNewGuidButton = hasNewGuidButton;
        }
    }
}
