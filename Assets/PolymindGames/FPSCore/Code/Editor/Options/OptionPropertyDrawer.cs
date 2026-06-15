using Toolbox.Editor.Drawers;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Options.Editor
{
    [CustomPropertyDrawer(typeof(Option<>), true)]
    public sealed class OptionPropertyDrawer : PropertyDrawerBase
    {
        protected override void OnGUISafe(Rect position, SerializedProperty property, GUIContent label) =>
            EditorGUI.PropertyField(position, property.FindPropertyRelative("_value"), label);

        protected override float GetPropertyHeightSafe(SerializedProperty property, GUIContent label) =>
            EditorGUI.GetPropertyHeight(property.FindPropertyRelative("_value"), label);
    }
}