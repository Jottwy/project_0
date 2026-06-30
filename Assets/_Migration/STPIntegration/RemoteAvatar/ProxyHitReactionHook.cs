using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-024: plays a cosmetic hit-reaction (flinch) on this remote proxy when the peer takes
    /// LOCAL damage. The peer's transmitter bumps a monotonic counter on each DamageReceived; it
    /// rides every pose as <c>view.hitSeq</c>. This hook change-detects the counter (a delta means a
    /// new hit) and fires a short procedural recoil — no clip, no Animator state, no authority.
    ///
    /// Slice 1 PLACEHOLDER (mirrors the pose-offset placeholders of ProxyPitchHook/ProxyCrouchHook):
    /// an instant impulse that decays over <c>_recoverTime</c>, applied in <c>LateUpdate</c> (AFTER the
    /// Animator) as an ADDITIVE world-space rotation of the upper/middle spine around the avatar's
    /// right axis (<c>transform.right</c>, yaw-oriented by RemotePlayerManager) — a quick torso jerk
    /// that eases back. Bones are resolved BY NAME (Generic rig, no GetBoneTransform), same as
    /// ProxyPitchHook; no-op if missing. Directional flinch (DamageArgs), a real flinch clip and
    /// per-severity scaling are deferred to Slice 2 (presentation only, no new ADR).
    ///
    /// The counter is read from the RemotePlayerManager view whose root is this GameObject — same
    /// lookup as ProxyCrouchHook/ProxyPitchHook, no change to RemotePlayerManager. Attach to the
    /// avatar root (same GameObject as the Animator). A sentinel makes the FIRST observed value never
    /// flinch (no spurious flinch on spawn/join/pool-reuse). Removable: delete the file and the proxy
    /// never flinches; locomotion/jump/pickup/crouch/pitch/clothing/held-item are unaffected.
    /// </summary>
    public sealed class ProxyHitReactionHook : MonoBehaviour
    {
        [Header("Tuning")]
        [Tooltip("Peak recoil angle (degrees) at the instant of a hit.")]
        [SerializeField, Min(0f)] private float _magnitude = 18f;

        [Tooltip("Seconds for the recoil to decay back to rest.")]
        [SerializeField, Min(0.01f)] private float _recoverTime = 0.3f;

        [Tooltip("Flip the recoil direction. The right-axis sign depends on the Generic rig; calibrate in play-test.")]
        [SerializeField] private bool _invert;

        // Sentinel: the first observed counter value arms _lastSeen without flinching.
        private const int Unseen = int.MinValue;

        private RemotePlayerManager _manager;
        private Transform _upperSpine;
        private Transform _middleSpine;
        private bool _hasRig;
        private int _lastSeen = Unseen;
        private float _impulse; // 1 at the instant of a hit, decays to 0

        private void Awake()
        {
            _upperSpine = FindBone("UpperSpine");
            _middleSpine = FindBone("MiddleSpine");
            _hasRig = _upperSpine != null || _middleSpine != null;
        }

        // Re-arm for pool reuse: a recycled proxy must not flinch on its first sample, nor carry a
        // mid-recoil pose from a previous occupant.
        private void OnEnable()
        {
            _lastSeen = Unseen;
            _impulse = 0f;
        }

        private void LateUpdate()
        {
            if (!_hasRig)
                return;

            int seq = ResolveHitSeq();
            if (_lastSeen == Unseen)
                _lastSeen = seq;             // first sample: arm, never flinch
            else if (seq != _lastSeen)
            {
                _lastSeen = seq;
                _impulse = 1f;               // new hit → kick the recoil
            }

            if (_impulse <= 0f)
                return;                       // at rest: zero per-frame cost, never fights other hooks

            _impulse = Mathf.Max(0f, _impulse - Time.deltaTime / Mathf.Max(0.01f, _recoverTime));

            float degrees = _magnitude * _impulse;
            if (_invert)
                degrees = -degrees;
            Vector3 axis = transform.right;   // yaw-oriented right of the avatar root

            // Split the recoil across both spine bones (root→leaf) so it reads as a torso jerk.
            ApplyBend(_middleSpine, degrees * 0.5f, axis);
            ApplyBend(_upperSpine, degrees * 0.5f, axis);
        }

        /// <summary>This proxy's networked hit counter, via the RemotePlayerManager view whose root is us.</summary>
        private int ResolveHitSeq()
        {
            if (_manager == null)
                _manager = GetComponentInParent<RemotePlayerManager>();
            if (_manager == null)
                return _lastSeen == Unseen ? 0 : _lastSeen;

            foreach (var kvp in _manager.ActivePlayers)
            {
                var view = kvp.Value;
                if (view != null && view.root == transform)
                    return view.hitSeq;
            }
            return _lastSeen == Unseen ? 0 : _lastSeen;
        }

        private static void ApplyBend(Transform bone, float degrees, Vector3 axis)
        {
            if (bone == null || Mathf.Approximately(degrees, 0f))
                return;
            bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;
        }

        private Transform FindBone(string boneName)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == boneName)
                    return t;
            }
            return null;
        }
    }
}
