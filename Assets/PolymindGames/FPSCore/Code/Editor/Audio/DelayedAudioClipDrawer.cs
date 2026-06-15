using UnityEngine.Audio;
using UnityEngine;
using UnityEditor;

namespace PolymindGames.Editor
{
    [CustomPropertyDrawer(typeof(DelayedAudioData))]
    public class DelayedAudioClipDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            Rect originalRect = position;
            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);

            if (originalRect != position)
            {
                position.x -= 50f;
                position.width += 50f;
            }

            // Calculate widths for clip and button
            float totalWidth = position.width;
            float clipWidth = totalWidth * 0.4f;
            const float ButtonWidth = 30f;
            const float Padding = 3f;

            // Calculate available space for volume and delay
            float availableWidth = totalWidth - clipWidth - ButtonWidth - (3 * Padding);
            float floatWidth = availableWidth / 2f;

            // Define rectangles for each element
            Rect clipRect = new Rect(position.x, position.y, clipWidth, position.height);
            Rect volumeRect = new Rect(clipRect.xMax + Padding, position.y, floatWidth, position.height);
            Rect delayRect = new Rect(volumeRect.xMax + Padding, position.y, floatWidth, position.height);
            Rect buttonRect = new Rect(position.x + position.width - ButtonWidth, position.y, ButtonWidth, position.height);

            var clipProperty = property.FindPropertyRelative("Clip");
            var volumeProperty = property.FindPropertyRelative("Volume");
            var delayProperty = property.FindPropertyRelative("Delay");
            
            EditorGUI.PropertyField(clipRect, clipProperty, GUIContent.none);
            EditorGUI.PropertyField(volumeRect, volumeProperty, GUIContent.none);
            EditorGUI.PropertyField(delayRect, delayProperty, GUIContent.none);

            volumeProperty.floatValue = Mathf.Clamp01(volumeProperty.floatValue);
            delayProperty.floatValue = Mathf.Clamp(delayProperty.floatValue, 0f, 20f);

            Rect volumeRectLabel = new Rect(volumeRect.x - 2f, volumeRect.y, volumeRect.width, volumeRect.height);
            Rect delayRectLabel = new Rect(delayRect.x - 2f, delayRect.y, delayRect.width, delayRect.height);
            
            EditorGUI.LabelField(volumeRectLabel, volumeRect.width > 70f ? "Volume" : "Vol", GUIStyles.MiniLabelSuffix);
            EditorGUI.LabelField(delayRectLabel, delayRect.width > 70f ? "Delay" : "Del", GUIStyles.MiniLabelSuffix);
            
            if (GUI.Button(buttonRect, AudioPlaybackEditorUtility.PlayButtonStyle))
            {
                AudioResource clip = clipProperty.objectReferenceValue as AudioResource;
                float volume = volumeProperty.floatValue;

                if (clip != null)
                {
                    AudioPlaybackEditorUtility.PlayClip(clip, volume);
                }
                else
                {
                    Debug.LogWarning("No audio clip assigned to preview.");
                }
            }

            EditorGUI.EndProperty();
        }
    }
}