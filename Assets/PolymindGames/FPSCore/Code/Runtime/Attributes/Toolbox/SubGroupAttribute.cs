using System.Diagnostics;
using System;

namespace UnityEngine
{
    [Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
    public sealed class SubGroupAttribute : ToolboxArchetypeAttribute
    {
        public override ToolboxAttribute[] Process()
        {
            return new ToolboxAttribute[]
            {
                new BeginGroupAttribute(), new EndGroupAttribute()
            };
        }
    }
}