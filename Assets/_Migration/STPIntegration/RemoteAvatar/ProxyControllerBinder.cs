using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// BUG 1 fix (T-pose in the BUILD, not the editor): guarantees the proxy's custom
    /// AnimatorController is BOTH included in the player build AND bound at runtime.
    ///
    /// The proxy prefab is a VARIANT of the vendor MTP_PlayerViewer. Its custom controller was applied
    /// only as the Animator's <c>m_Controller</c> override (plus the EnsureControllerBound editor
    /// workaround). That override survives Editor Play but NOT the player build: the build either strips
    /// the controller (its only reference was a variant-override objectReference, and it lives outside
    /// Resources) or fails to apply the override, so the Animator falls back to the inherited vendor
    /// AnimatorOverrideController (empty walk/run) → T-pose. Worked in editor, T-posed in build.
    ///
    /// Two guarantees here: (1) the serialized <see cref="_controller"/> reference is a HARD build
    /// dependency — the controller can never be stripped; (2) assigning it in Awake makes the binding
    /// independent of whether the variant override survived. Runs before the feeders/hooks (negative
    /// execution order) so they read parameters off the correct controller.
    ///
    /// Wired by RemoteAvatarPrefabBuilder. Removable: delete the file and the (editor-only) variant
    /// override is the fallback.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class ProxyControllerBinder : MonoBehaviour
    {
        [Header("Build-safe controller binding")]
        [Tooltip("Animator to bind. Auto-filled from this GameObject if empty.")]
        [SerializeField] private Animator _animator;

        [Tooltip("The custom ProxyLocomotionController, referenced as a hard build dependency so it is " +
                 "never stripped, and re-bound at runtime so the build never falls back to the vendor controller.")]
        [SerializeField] private RuntimeAnimatorController _controller;

        private void Awake()
        {
            if (_animator == null)
                _animator = GetComponent<Animator>();

            if (_animator != null && _controller != null && _animator.runtimeAnimatorController != _controller)
                _animator.runtimeAnimatorController = _controller;
        }

#if UNITY_EDITOR
        private void Reset() => _animator = GetComponent<Animator>();
#endif
    }
}
