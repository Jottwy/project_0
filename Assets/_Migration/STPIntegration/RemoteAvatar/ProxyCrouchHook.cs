using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-020 slice 3 (real clips): drives the Animator's "Crouched" blend parameter (0..1) from this
    /// remote player's networked crouch flag, lerped. The proxy controller's locomotion is a 2D
    /// FreeformCartesian BlendTree (MovementSpeed × Crouched, built by ProxyAnimatorControllerBuilder),
    /// so Crouched=1 plays crouch-idle / crouch-walk over the SAME velocity-derived locomotion, while
    /// pickup/jump (Any-State) still take over full-body briefly and return to the crouched blend.
    /// Replaces the earlier pose-offset placeholder.
    ///
    /// The crouch flag is read from the RemotePlayerManager view whose root is this GameObject — same
    /// lookup as ProxyPickupHook, no change to RemotePlayerManager. Attach to the avatar root (same
    /// GameObject as the Animator). Removable: delete the file and the proxy never crouches (Crouched
    /// stays 0); locomotion + jump + pickup are unaffected.
    /// </summary>
    public sealed class ProxyCrouchHook : MonoBehaviour
    {
        [Header("Animator")]
        [Tooltip("Animator that owns the Crouched blend param. Auto-filled from this GameObject if empty.")]
        [SerializeField] private Animator _animator;

        [Tooltip("Lerp speed of the Crouched blend (higher = snappier).")]
        [SerializeField, Min(0f)] private float _lerpSpeed = 10f;

        private const string CrouchedParam = "Crouched";
        private static readonly int CrouchedHash = Animator.StringToHash(CrouchedParam);

        private RemotePlayerManager _manager;
        private float _current;
        private bool _hasParam;

        private void Awake()
        {
            if (_animator == null)
                _animator = GetComponent<Animator>();
            _hasParam = HasCrouchedParam();
        }

        // Re-arm for pool reuse: clear the blend so a recycled proxy never starts pre-crouched.
        private void OnEnable()
        {
            _current = 0f;
            if (_hasParam && _animator != null)
                _animator.SetFloat(CrouchedHash, 0f);
        }

        private void Update()
        {
            if (!_hasParam || _animator == null)
                return;

            float target = ResolveCrouch() ? 1f : 0f;
            float t = 1f - Mathf.Exp(-Mathf.Max(0f, _lerpSpeed) * Time.deltaTime);
            _current = Mathf.Lerp(_current, target, t);
            _animator.SetFloat(CrouchedHash, _current);
        }

        /// <summary>This proxy's networked crouch flag, via the RemotePlayerManager view whose root is us.</summary>
        private bool ResolveCrouch()
        {
            if (_manager == null)
                _manager = GetComponentInParent<RemotePlayerManager>();
            if (_manager == null)
                return false;

            foreach (var kvp in _manager.ActivePlayers)
            {
                var view = kvp.Value;
                if (view != null && view.root == transform)
                    return view.crouch;
            }
            return false;
        }

        // True only if the controller actually exposes a "Crouched" float param (the builder omits it
        // when the crouch clips are missing). Avoids per-frame SetFloat warnings on a missing param.
        private bool HasCrouchedParam()
        {
            if (_animator == null || _animator.runtimeAnimatorController == null)
                return false;

            foreach (var p in _animator.parameters)
            {
                if (p.type == AnimatorControllerParameterType.Float && p.name == CrouchedParam)
                    return true;
            }
            return false;
        }

#if UNITY_EDITOR
        private void Reset() => _animator = GetComponent<Animator>();
#endif
    }
}
