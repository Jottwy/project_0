using System;
using PolymindGames.Editor;
using UnityEditor;
using UnityEngine;
using System.Linq;

namespace PolymindGames.WieldableSystem.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(Wieldable), true)]
    public class WieldableEditor : FoldoutBaseTypeEditor<Wieldable>
    {
        [SerializeField]
        private AnimationClip _equipClip;
        
        [SerializeField]
        private AnimationClip _holsterClip;

        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();
            
            GUILayout.Space(3f);
            GUILayout.BeginHorizontal(EditorStyles.helpBox);
            
            bool isDebugMode = Wieldable.IsDebugMode;
            isDebugMode = GUILayout.Toggle(isDebugMode, "Debug Mode", GUIStyles.Button);
            Wieldable.EnableDebugMode(isDebugMode);
            
            // Disable the button when the application is not playing or if the target is a prefab.
            if (GUILayout.Button("Reset Animation Durations"))
                ResetAnimationDurations();
            
            GUILayout.EndHorizontal();
        }
        
        /// <summary>
        /// Resets the durations of various reload animations based on their playback speed.
        /// </summary>
        private void ResetAnimationDurations()
        {
            WieldableAnimator animator = ((Wieldable)target).GetComponentInChildren<WieldableAnimator>();

            if (animator == null)
            {
                Debug.LogWarning("No Wieldable Animator found on this wieldable.");
                return;
            }

            serializedObject.Update();
            
            UpdateField("_equipDuration", animator, "Equip Speed", _equipClip);
            UpdateField("_holsterDuration", animator, "Holster Speed", _holsterClip);

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Updates the specified duration field based on the animation clip length and speed multiplier.
        /// </summary>
        private void UpdateField(string fieldName, WieldableAnimator animator, string speedParam, AnimationClip templateClip)
        {
            AnimationClip overrideClip = GetOverrideClip(animator, templateClip);
            if (overrideClip == null)
                return;

            float speedMultiplier = GetSpeedMultiplier(animator, speedParam);
            serializedObject.FindProperty(fieldName).floatValue = (float)Math.Round(overrideClip.length / speedMultiplier, 2);
        }
        
        /// <summary>
        /// Finds the override animation clip corresponding to the provided template clip.
        /// </summary>
        private static AnimationClip GetOverrideClip(WieldableAnimator animator, AnimationClip templateClip)
        {
            foreach (var clipPair in animator.OverrideClips.Clips)
            {
                if (clipPair.Original == templateClip)
                {
                    return clipPair.Override;
                }
            }

            return null;
        }

        /// <summary>
        /// Retrieves the reload speed multiplier from the animator's default parameters.
        /// </summary>
        private static float GetSpeedMultiplier(WieldableAnimator animator, string speedParam)
        {
            return animator.OverrideClips.DefaultParameters
                .FirstOrDefault(parameter => parameter.Name == speedParam)?.Value ?? 1f;
        }
    }
}