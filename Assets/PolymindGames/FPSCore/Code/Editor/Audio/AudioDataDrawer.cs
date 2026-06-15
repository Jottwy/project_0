using UnityEngine.Audio;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    [CustomPropertyDrawer(typeof(AudioData))]
    public class AudioDataDrawer : PropertyDrawer
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
            float clipWidth = totalWidth * 0.65f;
            const float ButtonWidth = 30f;
            const float Padding = 3f;

            // Calculate available space for the volume rect
            float volumeWidth = totalWidth - clipWidth - ButtonWidth - (2 * Padding);

            // Define rectangles for each element
            Rect clipRect = new Rect(position.x, position.y, clipWidth, position.height);
            Rect volumeRect = new Rect(clipRect.xMax + Padding, position.y, volumeWidth, position.height);
            Rect buttonRect = new Rect(position.x + position.width - ButtonWidth, position.y, ButtonWidth, position.height);

            var clipProperty = property.FindPropertyRelative("Clip");
            var volumeProperty = property.FindPropertyRelative("Volume");

            EditorGUI.PropertyField(clipRect, clipProperty, GUIContent.none);
            EditorGUI.PropertyField(volumeRect, volumeProperty, GUIContent.none);

            volumeProperty.floatValue = Mathf.Clamp01(volumeProperty.floatValue);

            Rect volumeRectLabel = new Rect(volumeRect.x - 2f, volumeRect.y, volumeRect.width, volumeRect.height);
            EditorGUI.LabelField(volumeRectLabel, volumeRect.width > 65f ? "Volume" : "Vol", GUIStyles.MiniLabelSuffix);

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