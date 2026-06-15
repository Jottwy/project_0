using System.Diagnostics;
using UnityEngine;
using System;

namespace PolymindGames
{
    [AttributeUsage(AttributeTargets.Field)]
    [Conditional("UNITY_EDITOR")]
    public sealed class SpritePreviewAttribute : PropertyAttribute
    {
    }
}