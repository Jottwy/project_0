using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-021: tilts this remote proxy's head/neck (and, at extreme angles, the upper spine) to
    /// match the peer's networked camera pitch (<c>view.pitch</c>, degrees, quantized to 1°). The
    /// proxy rig is GENERIC (not Humanoid), so there is no <c>Animator.GetBoneTransform</c>; bones are
    /// resolved BY NAME (Head/Neck/UpperSpine/MiddleSpine of the MaleSurvivor skeleton) and cached.
    ///
    /// The bend is applied in <c>LateUpdate</c> (AFTER the Animator writes the locomotion/jump/pickup
    /// pose) as an ADDITIVE world-space rotation around the avatar's right axis (<c>transform.right</c>,
    /// already yaw-oriented by RemotePlayerManager). Going through world space — instead of a bone's
    /// arbitrary local axis — makes "nodding" correct regardless of how the Generic rig was authored.
    /// Rotations are applied root→leaf so the bend accumulates naturally: the head ends at the full
    /// pitch, neck and spine ease the curve. Head carries 60% of the nod, neck 40%.
    ///
    /// The pitch is read from the RemotePlayerManager view whose root is this GameObject — same lookup
    /// as ProxyCrouchHook, no change to RemotePlayerManager. Attach to the avatar root (same GameObject
    /// as the Animator). Removable: delete the file and the proxy simply looks forward; locomotion,
    /// jump, pickup and crouch are unaffected.
    /// </summary>
    public sealed class ProxyPitchHook : MonoBehaviour
    {
        [Header("Tuning")]
        [Tooltip("Lerp speed of the applied pitch (higher = snappier).")]
        [SerializeField, Min(0f)] private float _lerpSpeed = 10f;

        [Tooltip("Above this absolute pitch (degrees) the upper spine starts to lean.")]
        [SerializeField, Min(0f)] private float _spineLeanThreshold = 45f;

        [Tooltip("Maximum spine lean (degrees) reached at ±90° pitch (ADR-021 cap).")]
        [SerializeField, Min(0f)] private float _maxSpineLean = 30f;

        [Tooltip("Flip the nod direction. The right-axis sign depends on the Generic rig; calibrate in play-test.")]
        [SerializeField] private bool _invertPitch;

        // Head carries 60% of the nod, neck 40% (added rotations; root→leaf accumulation puts the
        // head tip at the full pitch).
        private const float HeadShare = 0.6f;
        private const float NeckShare = 0.4f;

        private RemotePlayerManager _manager;
        private Transform _head;
        private Transform _neck;
        private Transform _upperSpine;
        private Transform _middleSpine;
        private bool _hasRig;
        private float _current;

        private void Awake()
        {
            _head = FindBone("Head");
            _neck = FindBone("Neck");
            _upperSpine = FindBone("UpperSpine");
            _middleSpine = FindBone("MiddleSpine");
            _hasRig = _head != null; // no head bone → nothing to drive
        }

        // Re-arm for pool reuse: clear the applied pitch so a recycled proxy never starts pre-tilted.
        private void OnEnable() => _current = 0f;

        private void LateUpdate()
        {
            if (!_hasRig)
                return;

            float target = ResolvePitch();
            float t = 1f - Mathf.Exp(-Mathf.Max(0f, _lerpSpeed) * Time.deltaTime);
            _current = Mathf.Lerp(_current, target, t);

            float p = _invertPitch ? -_current : _current;
            Vector3 axis = transform.right; // yaw-oriented right of the avatar root

            // Root→leaf so bends accumulate. Spine lean (only past the threshold) first, then the
            // neck/head nod on top.
            float lean = SpineLean(p);
            ApplyBend(_middleSpine, lean * 0.5f, axis);
            ApplyBend(_upperSpine, lean * 0.5f, axis);
            ApplyBend(_neck, p * NeckShare, axis);
            ApplyBend(_head, p * HeadShare, axis);
        }

        /// <summary>This proxy's networked pitch (degrees), via the RemotePlayerManager view whose root is us.</summary>
        private float ResolvePitch()
        {
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return 0f;

            return view.pitch;
        }

        // Spine lean: zero below the threshold, ramping to ±_maxSpineLean at ±90°.
        private float SpineLean(float pitch)
        {
            float abs = Mathf.Abs(pitch);
            if (abs <= _spineLeanThreshold)
                return 0f;
            float span = Mathf.Max(1f, 90f - _spineLeanThreshold);
            float frac = Mathf.Clamp01((abs - _spineLeanThreshold) / span);
            return Mathf.Sign(pitch) * frac * _maxSpineLean;
        }

        private static void ApplyBend(Transform bone, float degrees, Vector3 axis) =>
            ProxyRigUtil.ApplyBend(bone, degrees, axis);

        private Transform FindBone(string boneName) => ProxyRigUtil.FindBone(transform, boneName);
    }
}
