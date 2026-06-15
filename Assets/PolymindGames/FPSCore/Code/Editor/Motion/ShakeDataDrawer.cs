using PolymindGames.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.ProceduralMotion.Editor
{
    [CustomPropertyDrawer(typeof(ShakeData))]
    public class ShakeDataDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            // Preserve original position and adjust prefix label
            Rect originalRect = position;
            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);

            if (originalRect != position)
            {
                position.x -= 50f;
                position.width += 50f;
            }

            const float Padding = 3f;

            float totalWidth = position.width;
            float profileWidth = totalWidth * 0.5f;
            float durationWidth = totalWidth * 0.25f - Padding;
            float multiplierWidth = totalWidth * 0.25f - Padding - 1f;

            // Define rectangles for each element
            Rect profileRect = new Rect(position.x, position.y, profileWidth, position.height);
            Rect durationRect = new Rect(profileRect.xMax + Padding, position.y, durationWidth, position.height);
            Rect multiplierRect = new Rect(durationRect.xMax + Padding, position.y, multiplierWidth, position.height);

            // Serialized properties
            var profileClip = property.FindPropertyRelative("Profile");
            EditorGUI.PropertyField(profileRect, profileClip, GUIContent.none);

            var durationProperty = property.FindPropertyRelative("Duration");
            durationProperty.floatValue = Mathf.Clamp(durationProperty.floatValue, 0f, 10f);
            EditorGUI.PropertyField(durationRect, durationProperty, GUIContent.none);
            
            Rect durationRectLabel = new Rect(durationRect.x - 2f, durationRect.y, durationRect.width, durationRect.height);
            EditorGUI.LabelField(durationRectLabel, durationRect.width > 65f ? "Seconds" : "Sec", GUIStyles.MiniLabelSuffix);

            var multiplierProperty = property.FindPropertyRelative("Multiplier");
            multiplierProperty.floatValue = Mathf.Clamp(multiplierProperty.floatValue, 0f, 10f);
            EditorGUI.PropertyField(multiplierRect, multiplierProperty, GUIContent.none);
            
            Rect multiplierRectLabel = new Rect(multiplierRect.x - 2f, multiplierRect.y, multiplierRect.width, multiplierRect.height);
            EditorGUI.LabelField(multiplierRectLabel, multiplierRect.width > 65f ? "Multiplier" : "Mod", GUIStyles.MiniLabelSuffix);

            EditorGUI.EndProperty();
        }
    }
}