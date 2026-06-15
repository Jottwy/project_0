using UnityEditor.Animations;
using System.Linq;
using UnityEngine;
using UnityEditor;
using System;

namespace PolymindGames.Editor
{
    [CustomPropertyDrawer(typeof(AnimatorParameterTrigger))]
    public sealed class AnimatorParameterTriggerDrawer : PropertyDrawer
    {
        private AnimatorControllerParameterType _animatorParameterType;
        private int _selectedValue;

        private const float Indentation = 6f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (Application.isPlaying)
                return;

            position.height = EditorGUIUtility.singleLineHeight;
            property.isExpanded = true;

            var typeProp = property.FindPropertyRelative("Type");
            var nameProp = property.FindPropertyRelative("Name");
            var valueProp = property.FindPropertyRelative("Value");

            EditorGUI.PropertyField(position, typeProp, true);

            AnimatorControllerParameterType paramType = (AnimatorControllerParameterType)typeProp.intValue;
            position.y = position.yMax + EditorGUIUtility.standardVerticalSpacing;

            // Draw animator param
            if (paramType != _animatorParameterType)
                _selectedValue = 0;

            _animatorParameterType = paramType;

            if (AnimatorParameters(position, nameProp))
            {
                position.x += Indentation;
                position.y = position.yMax + EditorGUIUtility.standardVerticalSpacing;
                position.width -= Indentation;

                switch (paramType)
                {
                    case AnimatorControllerParameterType.Bool:
                        {
                            bool value = !Mathf.Approximately(valueProp.floatValue, 0f);
                            value = EditorGUI.Toggle(position, "Bool ", value);

                            valueProp.floatValue = value ? 1f : 0f;
                            break;
                        }
                    case AnimatorControllerParameterType.Float or AnimatorControllerParameterType.Int:
                        {
                            if (paramType == AnimatorControllerParameterType.Float)
                                valueProp.floatValue = EditorGUI.FloatField(position, "Float ", valueProp.floatValue);
                            else
                            {
                                int value = EditorGUI.IntField(position, "Integer ", Mathf.RoundToInt(valueProp.floatValue));
                                valueProp.floatValue = Mathf.Clamp(value, -9999999, 9999999);
                            }

                            break;
                        }
                    default:
                        EditorGUI.LabelField(position, "Trigger ");
                        break;
                }
            }
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (Application.isPlaying)
                return EditorGUIUtility.singleLineHeight;

            float height = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            return height * 3f;
        }

        private bool AnimatorParameters(Rect position, SerializedProperty property)
        {
            var animatorController = GetAnimatorController(property);
            if (animatorController == null)
            {
                position.height *= 2f;
                EditorGUI.HelpBox(position, $"No animator found..", MessageType.Error);
                return false;
            }

            var parameters = animatorController.parameters;
            if (parameters.Length == 0)
            {
                position.height *= 2f;
                EditorGUI.HelpBox(position, $"No animation parameters found.", MessageType.Warning);
                return false;
            }

            var eventNames = parameters
                .Where(t => CanAddEventName(t.type))
                .Select(t => t.name).ToArray();

            if (eventNames.Length == 0)
            {
                position.height *= 2f;
                EditorGUI.HelpBox(position, $"No animation parameters of type {_animatorParameterType} found.", MessageType.Info);
                return false;
            }

            int matchIndex = Array.FindIndex(eventNames, eventName => eventName.Equals(property.stringValue));

            if (matchIndex != -1)
                _selectedValue = matchIndex;

            _selectedValue = EditorGUI.IntPopup(position, "Param ", _selectedValue, eventNames, SetOptionValues(eventNames));

            property.stringValue = eventNames[_selectedValue];

            return true;
        }

        private static AnimatorController GetAnimatorController(SerializedProperty property)
        {
            if (property.serializedObject.targetObject is not Component component)
                return null;

            // Try get animator in children
            var anim = component.GetComponentInChildren<Animator>();

            // Try get animator in sibling
            if (anim == null && component.transform.parent != null)
                anim = component.transform.parent.GetComponentInChildren<Animator>();

            // Try get animator in parent
            if (anim == null)
                anim = component.GetComponentInParent<Animator>();

            if (anim == null)
            {
                Debug.LogException(new MissingComponentException("Missing Animator Component"));
                return null;
            }

            return anim.runtimeAnimatorController as AnimatorController;
        }

        private bool CanAddEventName(AnimatorControllerParameterType animatorControllerParameterType)
        {
            return (int)animatorControllerParameterType == (int)_animatorParameterType;
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
    }
}