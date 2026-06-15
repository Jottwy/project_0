using System.Diagnostics;
using System;

namespace UnityEngine
{
    [Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class ShowForRenderPipelineAttribute : ToolboxConditionAttribute
    {
        public enum Type
        {
            BuiltIn = 0,
            Hdrp = 1,
            Urp = 2
        }
        
        public ShowForRenderPipelineAttribute(Type type)
        {
            PipelineType = type;
        }
        
        public Type PipelineType { get; }
    }
}