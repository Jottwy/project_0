using Toolbox.Editor.Drawers;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.SaveSystem.Editor
{
    [CustomPropertyDrawer(typeof(SerializedDateTime))]
    public sealed class SerializedDateTimeDrawer : PropertyDrawerBase
    {
        protected override void OnGUISafe(Rect position, SerializedProperty property, GUIContent label)
        {
            label = EditorGUI.BeginProperty(position, label, property);
            var fieldPosition = EditorGUI.PrefixLabel(position, label);
            var ticksProperty = property.FindPropertyRelative("_ticks");
            DateTime dateTime = new DateTime(ticksProperty.longValue);
            EditorGUI.BeginChangeCheck();
            var dateTimeString = EditorGUI.DelayedTextField(fieldPosition, dateTime.ToString(CultureInfo.InvariantCulture));
            if (EditorGUI.EndChangeCheck())
            {
                if (DateTime.TryParse(dateTimeString, out var newDateTime))
                {
                    ticksProperty.serializedObject.Update();
                    ticksProperty.longValue = newDateTime.Ticks;
                    ticksProperty.serializedObject.ApplyModifiedProperties();
                }
            }

            EditorGUI.EndProperty();
        }
    }
}
