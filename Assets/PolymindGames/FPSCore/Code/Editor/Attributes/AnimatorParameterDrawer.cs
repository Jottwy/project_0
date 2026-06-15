using Toolbox.Editor.Drawers;
using UnityEditor.Animations;
using System.Linq;
using UnityEngine;
using UnityEditor;
using System;

namespace PolymindGames.Editor
{
    [CustomPropertyDrawer(typeof(AnimatorParameterAttribute))]
    public sealed class AnimatorParameterDrawer : PropertyDrawerBase
    {
        private AnimatorParameterAttribute AnimatorParameterAttribute => (AnimatorParameterAttribute)attribute;

        protected override void OnGUISafe(Rect position, SerializedProperty property, GUIContent label)
        {
            var animator = GetAnimatorController(property);
            if (animator == null)
                return;
            
            var parameters = animator.parameters;

            if (parameters.Length == 0)
            {
                NoParamsLabel(position, property);
                return;
            }

            var eventNames = parameters
                .Where(t => CanAddEventName(t.type, property))
                .Select(t => t.name).ToArray();

            if (eventNames.Length == 0)
            {
                NoParamsLabel(position, property);
                return;
            }
            
            int matchIndex = Array.FindIndex(eventNames, eventName => eventName.Equals(property.stringValue));
            
            if (matchIndex != -1)
                AnimatorParameterAttribute.SelectedValue = matchIndex;

            AnimatorParameterAttribute.SelectedValue = EditorGUI.IntPopup(position, label.text, AnimatorParameterAttribute.SelectedValue, eventNames, SetOptionValues(eventNames));

            property.stringValue = eventNames[AnimatorParameterAttribute.SelectedValue];
        }

        private bool CanAddEventName(AnimatorControllerParameterType animatorControllerParameterType, SerializedProperty property)
        {
            string sourceHandle = AnimatorParameterAttribute.ParameterTypeFieldName;
            if (!string.IsNullOrEmpty(sourceHandle))
            {
                if (ValueExtractionHelper.TryGetValue(sourceHandle, property, out var value, out var hasMixedValues))
                    AnimatorParameterAttribute.ParameterType = (AnimatorControllerParameterType)value;
            }

            return animatorControllerParameterType == AnimatorParameterAttribute.ParameterType;
        }

        private static int[] SetOptionValues(string[] eventNames)
        {
            int[] optionValues = new int[eventNames.Length];
            for (int i = 0; i < eventNames.Length; i++)
            {
                optionValues[i] = i;
            }
            return optionValues;
        }

        private static AnimatorController GetAnimatorController(SerializedProperty property)
        {
            var component = property.serializedObject.targetObject as Component;

            if (component == null)
                throw new InvalidCastException("Couldn't cast targetObject");

            // Try get animator in children
            var anim = component.GetComponentInChildren<Animator>();

            // Try get animator in parent
            if (anim == null)
                anim = component.GetComponentInParent<Animator>();

            return anim != null ? anim.runtimeAnimatorController as AnimatorController : null;
        }

        private void NoParamsLabel(Rect position, SerializedProperty property)
        {
            EditorGUI.LabelField(position, $"Animator has no {AnimatorParameterAttribute.ParameterType} params", EditorStyles.miniLabel);
            property.stringValue = string.Empty;
        }
    }
}