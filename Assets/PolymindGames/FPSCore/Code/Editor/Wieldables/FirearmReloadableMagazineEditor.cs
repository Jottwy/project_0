using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem.Editor
{
    [CustomEditor(typeof(FirearmReloadableMagazineBehaviour), true)]
    public class FirearmReloadableMagazineEditor : ToolboxEditor
    {
        [SerializeField]
        private AnimationClip _beginReloadClip;
        
        [SerializeField]
        private AnimationClip _reloadClip;
        
        [SerializeField]
        private AnimationClip _endReloadClip;
        
        [SerializeField]
        private AnimationClip _emptyReloadClip;

        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            ToolboxEditorGui.DrawLine();
            
            // Disable the button when the application is not playing or if the target is a prefab.
            if (GUILayout.Button("Reset Animation Durations"))
            {
                ResetAnimationDurations();
            }
        }

        /// <summary>
        /// Resets the durations of various reload animations based on their playback speed.
        /// </summary>
        private void ResetAnimationDurations()
        {
            WieldableAnimator animator = GetWieldableAnimator();
            
            if (animator == null)
            {
                Debug.LogWarning("No Wieldable Animator found on the parent wieldable.");
                return;
            }

            serializedObject.Update();
            
            UpdateDurationField("_reloadDuration", "_reloadAnimSpeed", animator, _reloadClip);
            UpdateDurationField("_reloadLoopDuration", "_reloadLoopAnimSpeed", animator, _reloadClip);
            UpdateDurationField("_reloadBeginDuration", "_reloadBeginAnimSpeed", animator, _beginReloadClip);
            UpdateDurationField("_reloadEndDuration", "_reloadEndAnimSpeed", animator, _endReloadClip);
            UpdateDurationField("_emptyReloadDuration", "_emptyReloadAnimSpeed", animator, _emptyReloadClip);

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Updates the specified duration field based on the animation clip length and speed multiplier.
        /// </summary>
        private void UpdateDurationField(string fieldName, string speedFieldName, WieldableAnimator animator, AnimationClip templateClip)
        {
            AnimationClip overrideClip = GetOverrideClip(animator, templateClip);
            if (overrideClip == null)
                return;

            SerializedProperty durationProperty = serializedObject.FindProperty(fieldName);
            if (durationProperty == null)
                return;

            SerializedProperty speedProperty = serializedObject.FindProperty(speedFieldName);
            float speedMultiplier = speedProperty == null ? GetReloadSpeedMultiplier(animator) : speedProperty.floatValue;
            durationProperty.floatValue = (float)Math.Round(overrideClip.length / speedMultiplier, 2);
        }

        /// <summary>
        /// Retrieves the reload speed multiplier from the animator's default parameters.
        /// </summary>
        private static float GetReloadSpeedMultiplier(WieldableAnimator animator)
        {
            foreach (var parameter in animator.OverrideClips.DefaultParameters)
            {
                if (parameter.Hash == AnimationConstants.ReloadSpeed)
                {
                    return parameter.Value;
                }
            }

            return 1f; // Default speed multiplier if not found.
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

            return null; // No override found.
        }

        /// <summary>
        /// Gets the WieldableAnimator component from the parent or child hierarchy.
        /// </summary>
        private WieldableAnimator GetWieldableAnimator()
        {
            IWieldable wieldable = ((MonoBehaviour)target).GetComponentInParent<IWieldable>();
            return wieldable != null ? wieldable.gameObject.GetComponentInChildren<WieldableAnimator>() : null;
        }
    }
}