using Toolbox.Editor;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.WieldableSystem.Editor
{
    [CustomEditor(typeof(WieldableAnimator), true)]
    public sealed class WieldableAnimatorEditor : ToolboxEditor
    {
        private WieldableAnimator _wieldableAnimator;


        public override void DrawCustomInspector()
        {
            if (_wieldableAnimator.Animator == null)
                EditorGUILayout.HelpBox("Assign an animator controller", MessageType.Error);

            base.DrawCustomInspector();

            if (_wieldableAnimator.Animator != null)
            {
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                {
                    EditorGUILayout.Space();

                    if (CanShowSetupAnimatorButton())
                        DrawSetupGUI();
                }
            }
        }

        private void OnEnable() => _wieldableAnimator = (WieldableAnimator)target;

        private void DrawSetupGUI()
        {
            EditorGUILayout.Space();
            ToolboxEditorGui.DrawLine();

            GUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.HelpBox("Animator is not properly set up", MessageType.Warning);

            if (GUILayout.Button("Fix First Person Settings"))
            {
                FixModelAndAnimator();

                EditorUtility.SetDirty(_wieldableAnimator.gameObject);
                PrefabUtility.RecordPrefabInstancePropertyModifications(_wieldableAnimator.gameObject);
            }

            GUILayout.EndVertical();

            ToolboxEditorGui.DrawLine();
        }

        private bool CanShowSetupAnimatorButton()
        {
            if (_wieldableAnimator == null)
                return false;

            var animator = _wieldableAnimator.Animator;

            bool canShow = animator.GetComponentInChildren<SkinnedMeshRenderer>(true).motionVectorGenerationMode != MotionVectorGenerationMode.ForceNoMotion;
            canShow |= animator.cullingMode != AnimatorCullingMode.AlwaysAnimate;
            canShow |= animator.gameObject.layer != LayerConstants.ViewModel;

            foreach (var renderer in animator.GetComponentsInChildren<MeshRenderer>())
            {
                canShow |= renderer.gameObject.layer != LayerConstants.ViewModel;
                canShow |= renderer.shadowCastingMode != ShadowCastingMode.Off;

                if (canShow)
                    return true;
            }

            return canShow;
        }

        private void FixModelAndAnimator()
        {
            var animator = _wieldableAnimator.Animator;
            var skinnedRenderers = animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var renderers = animator.GetComponentsInChildren<MeshRenderer>(true);

            if (animator != null)
            {
                _wieldableAnimator.gameObject.SetLayersInChildren(LayerConstants.ViewModel);

                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode = AnimatorUpdateMode.Normal;
                animator.applyRootMotion = false;
            }

            if (skinnedRenderers != null)
            {
                foreach (var skinRenderer in skinnedRenderers)
                {
                    skinRenderer.updateWhenOffscreen = true;
                    skinRenderer.shadowCastingMode = ShadowCastingMode.Off;
                    skinRenderer.skinnedMotionVectors = false;
                    skinRenderer.allowOcclusionWhenDynamic = false;
                    skinRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                }
            }

            if (renderers != null)
            {
                foreach (var renderer in renderers)
                {
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.allowOcclusionWhenDynamic = false;
                }
            }
        }
    }
}