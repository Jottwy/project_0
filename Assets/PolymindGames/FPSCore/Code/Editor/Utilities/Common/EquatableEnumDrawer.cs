using Toolbox.Editor.Drawers;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    [CustomPropertyDrawer(typeof(EquatableEnum<>))]
    public sealed class EquatableEnumDrawer : PropertyDrawerBase
    {
        protected override void OnGUISafe(Rect position, SerializedProperty property, GUIContent label)
        {
            var prop = property.FindPropertyRelative("_value");
            EditorGUI.PropertyField(position, prop, label);
        }

        protected override float GetPropertyHeightSafe(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }
    }
}