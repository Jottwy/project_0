using System.Diagnostics;
using UnityEngine;
using System;

namespace PolymindGames
{
    [Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class DataReferenceAttribute : PropertyAttribute 
    {
        public Type DataType { get; set; }
        public bool HasAssetReference { get; set; }
        public bool HasLabel { get; set; } = true;
        public bool HasIcon { get; set; } = true;
        public string NullElement { get; set; } = "Empty";
    }
}