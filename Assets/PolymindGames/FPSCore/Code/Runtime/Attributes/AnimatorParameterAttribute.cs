using System;
using System.Diagnostics;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Animator Parameter attribute.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true)]
    [Conditional("UNITY_EDITOR")]
    public sealed class AnimatorParameterAttribute : PropertyAttribute
    {
        public string ParameterTypeFieldName { get; }
        public int SelectedValue { get; set; }
        public AnimatorControllerParameterType ParameterType { get; set; }

        public AnimatorParameterAttribute(AnimatorControllerParameterType parameterType)
        {
            ParameterType = parameterType;
            ParameterTypeFieldName = string.Empty;
        }
        
        public AnimatorParameterAttribute(string parameterTypeFieldName)
        {
            ParameterTypeFieldName = parameterTypeFieldName;
        }
    }
}
