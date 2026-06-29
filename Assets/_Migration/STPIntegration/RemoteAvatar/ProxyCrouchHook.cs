using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-020 slice 3 (v1 PLACEHOLDER): lowers this remote player's VISUAL by a fixed depth while
    /// its networked crouch flag is true. There are NO crouch animation clips in the project (only
    /// the STP crouch AUDIO + the local CharacterCrouchState logic), so the proxy can't bend its
    /// legs yet; the placeholder communicates "crouched" by silhouette + height (matching the
    /// crouched collider/camera the local owner sees). Real crouch-idle/crouch-walk clips (an
    /// additive layer over the velocity-derived locomotion BlendTree) are a later iteration — that
    /// is presentation-only (ADR-012/013), no new ADR, just assets.
    ///
    /// The offset is applied to a CHILD <see cref="_visual"/>, never to the avatar root: the
    /// RemotePlayerManager overwrites <c>root.position</c> every frame via its pose lerp, so any
    /// offset on the root would be erased. The crouch flag is read from the RemotePlayerManager view
    /// whose root is this GameObject — same lookup as ProxyPickupHook, no change to RemotePlayerManager.
    ///
    /// Attach to the avatar root (same GameObject as the Animator). Removable: delete the file and
    /// the proxy simply never crouches (locomotion + jump + pickup unaffected).
    /// </summary>
    public sealed class ProxyCrouchHook : MonoBehaviour
    {
        [Header("Placeholder pose-offset")]
        [Tooltip("Visual transform lowered while crouched. If empty, auto-resolved at Awake to the " +
                 "first renderable child (never the root, which the pose lerp overwrites).")]
        [SerializeField] private Transform _visual;

        [Tooltip("How far down the visual drops when crouched (meters). Roughly the crouch height " +
                 "delta of the local STP CharacterCrouchState.")]
        [SerializeField, Range(0f, 1.2f)] private float _crouchDepth = 0.5f;

        [Tooltip("Lerp speed of the offset (higher = snappier).")]
        [SerializeField, Min(0f)] private float _lerpSpeed = 10f;

        private RemotePlayerManager _manager;
        private float _baseLocalY;
        private float _currentOffset;
        private bool _hasVisual;

        private void Awake()
        {
            if (_visual == null)
                _visual = ResolveVisual();

            // Only drive a CHILD; if the only candidate is the root itself, do nothing (the pose
            // lerp owns the root). _hasVisual stays false → Update is a no-op.
            _hasVisual = _visual != null && _visual != transform;
            if (_hasVisual)
                _baseLocalY = _visual.localPosition.y;
        }

        // Re-arm for pool reuse: clear the offset so a recycled proxy never starts pre-crouched.
        private void OnEnable() => _currentOffset = 0f;

        private void Update()
        {
            if (!_hasVisual)
                return;

            bool crouch = ResolveCrouch();
            float target = crouch ? -_crouchDepth : 0f;

            float t = 1f - Mathf.Exp(-Mathf.Max(0f, _lerpSpeed) * Time.deltaTime);
            _currentOffset = Mathf.Lerp(_currentOffset, target, t);

            var lp = _visual.localPosition;
            lp.y = _baseLocalY + _currentOffset;
            _visual.localPosition = lp;
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

        /// <summary>
        /// First direct child that renders something (the model), skipping the billboard name tag and
        /// the remote marker the manager parents to the root. Falls back to the first child, then self.
        /// </summary>
        private Transform ResolveVisual()
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                string n = child.name;
                if (n == "NameTag" || n == "RemoteMarker")
                    continue;
                if (child.GetComponentInChildren<Renderer>() != null)
                    return child;
            }
            return transform.childCount > 0 ? transform.GetChild(0) : transform;
        }

#if UNITY_EDITOR
        private void Reset() => _visual = ResolveVisual();
#endif
    }
}
