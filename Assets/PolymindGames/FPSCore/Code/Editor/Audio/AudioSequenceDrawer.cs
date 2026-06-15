using JetBrains.Annotations;
using Toolbox.Editor.Drawers;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    [UsedImplicitly]
    public sealed class AudioSequenceDrawer : ToolboxTargetTypeDrawer
    {
        // private readonly GUILayoutOption[] _mergeButtonLayoutOptions = { GUILayout.Width(100f) };
        private AudioSequence _sequence;

        public override void OnGui(SerializedProperty property, GUIContent label)
        {
            property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, label, true);
            if (property.isExpanded)
            {
                ToolboxEditorGui.DrawPropertyChildren(property);

                EditorGUILayout.Space();
                if (GUILayout.Button(AudioPlaybackEditorUtility.PlayButtonStyle))
                {
                    _sequence = (AudioSequence)property.boxedValue;
                    AudioPlaybackEditorUtility.PlaySequence(_sequence);
                }

                // if (GUILayout.Button("Merge", _mergeButtonLayoutOptions))
                // {
                //     _sequence = (AudioSequence)property.boxedValue;
                //     var audioClip = AudioPlaybackEditorUtility.CombineDelayedAudioClips(_sequence.Clips);
                //
                //     AudioPlaybackEditorUtility.SaveAudioClipAsWavUsingDialog(audioClip);
                // }
            }

            // Cleanup any audio playback utility resources
            AudioPlaybackEditorUtility.Cleanup();
        }

        public override Type GetTargetType() => typeof(AudioSequence);
        public override bool UseForChildren() => false;
    }
}