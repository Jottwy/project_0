using PolymindGames.UserInterface;
using Toolbox.Editor.Drawers;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.UISystem.Editor
{
    /// <summary>
    /// This is a PropertyDrawer for NavigationUI. It is implemented using the standard Unity PropertyDrawer framework.
    /// </summary>
    [CustomPropertyDrawer(typeof(SelectableNavigation), true)]
    public sealed class NavigationUIDrawer : PropertyDrawerBase
    {
        private static readonly GUIContent _navigationContent = EditorGUIUtility.TrTextContent("Navigation");

        protected override void OnGUISafe(Rect pos, SerializedProperty prop, GUIContent label)
        {
            Rect drawRect = pos;
            drawRect.height = EditorGUIUtility.singleLineHeight;
            drawRect.width -= 6f;

            SerializedProperty navigation = prop.FindPropertyRelative("_mode");
            SerializedProperty wrapAround = prop.FindPropertyRelative("_wrapAround");
            SelectableNavigation.NavMode navMode = GetNavigationUIMode(navigation);

            EditorGUI.PropertyField(drawRect, navigation, _navigationContent);
            
            ++EditorGUI.indentLevel;
            drawRect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            switch (navMode)
            {
                case SelectableNavigation.NavMode.Horizontal:
                case SelectableNavigation.NavMode.Vertical:
                    {
                        EditorGUI.PropertyField(drawRect, wrapAround);
                        drawRect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
                    }
                    break;
                case SelectableNavigation.NavMode.Explicit:
                    {
                        SerializedProperty selectOnUp = prop.FindPropertyRelative("_selectOnUp");
                        SerializedProperty selectOnDown = prop.FindPropertyRelative("_selectOnDown");
                        SerializedProperty selectOnLeft = prop.FindPropertyRelative("_selectOnLeft");
                        SerializedProperty selectOnRight = prop.FindPropertyRelative("_selectOnRight");

                        EditorGUI.PropertyField(drawRect, selectOnUp);
                        drawRect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
                        EditorGUI.PropertyField(drawRect, selectOnDown);
                        drawRect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
                        EditorGUI.PropertyField(drawRect, selectOnLeft);
                        drawRect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
                        EditorGUI.PropertyField(drawRect, selectOnRight);
                        drawRect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
                    }
                    break;
            }

            --EditorGUI.indentLevel;
        }

        protected override float GetPropertyHeightSafe(SerializedProperty prop, GUIContent label)
        {
            SerializedProperty navigation = prop.FindPropertyRelative("_mode");
            if (navigation == null)
                return EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            SelectableNavigation.NavMode navMode = GetNavigationUIMode(navigation);

            switch (navMode)
            {
                case SelectableNavigation.NavMode.None:
                    return EditorGUIUtility.singleLineHeight;
                case SelectableNavigation.NavMode.Horizontal:
                case SelectableNavigation.NavMode.Vertical:
                    return 2 * EditorGUIUtility.singleLineHeight + 2 * EditorGUIUtility.standardVerticalSpacing;
                case SelectableNavigation.NavMode.Explicit:
                    return 5 * EditorGUIUtility.singleLineHeight + 5 * EditorGUIUtility.standardVerticalSpacing;
                default:
                    return EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            }
        }

        private static SelectableNavigation.NavMode GetNavigationUIMode(SerializedProperty navigation)
        {
            return (SelectableNavigation.NavMode)navigation.enumValueIndex;
        }
    }
}