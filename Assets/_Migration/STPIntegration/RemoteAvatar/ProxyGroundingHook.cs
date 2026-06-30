using BackroomsSurvival.Gameplay.GridWorld;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// [D] Body grounding (slice 1): pins this remote proxy's body to the RENDERED floor.
    ///
    /// The proxy root is positioned from the backend pose (floor_player_y), but what is RENDERED
    /// and walked is the client-side ChunkStreamer — a DIFFERENT world (see STATE.md debt). The
    /// two floor heights don't match, so the proxy's feet float above or sink into the visible
    /// floor. This hook raycasts down to the rendered floor and offsets the <c>Hips</c> bone (the
    /// whole skeleton with it) so the feet rest on what is actually drawn.
    ///
    /// The rig is GENERIC (not Humanoid) → no <c>OnAnimatorIK</c>; the Hips bone is resolved BY
    /// NAME and cached (same approach as ProxyPitchHook). The shift is applied in <c>LateUpdate</c>
    /// AFTER the Animator writes the pose, as an additive world-space Y offset recomputed from
    /// scratch each frame (the Animator re-writes Hips first, so nothing accumulates). Only Hips is
    /// touched — the networked root stays under RemotePlayerManager (pose lerp) and
    /// ProxyLocomotionFeeder (XZ velocity), so this is purely cosmetic: zero network, zero schema,
    /// zero ADR.
    ///
    /// AIRBORNE FADE: the correction weight fades to zero as the body gets far from the rendered
    /// floor (<c>|gap|</c> beyond <see cref="_airborneFade"/>) — a jump (ProxyJumpFeeder), a
    /// chunk-displacement teleport or a respawn lifts the body well above the floor, so the proxy is
    /// never glued to the ground mid-air; near the apex |gap| is largest, so grounding fully releases
    /// there. SmoothDamp on the applied offset hides the transition.
    ///
    /// The raycast uses <see cref="GridChunkBuilder.GeoMask"/> (the floor/ceiling Unity layers
    /// {0,14,15,16}); the proxy's own CapsuleColliders live on layer 12, OUTSIDE that mask, so a
    /// plain downward ray never self-hits. Attach to the avatar root (same GameObject as the
    /// Animator). Removable: delete the file and the proxy falls back to the raw backend Y.
    /// </summary>
    public sealed class ProxyGroundingHook : MonoBehaviour
    {
        [Header("Raycast")]
        [Tooltip("Height above the avatar root the down-ray starts from (m).")]
        [SerializeField, Min(0f)] private float _rayUp = 1.0f;

        [Tooltip("How far below the root the ray probes for floor (m).")]
        [SerializeField, Min(0f)] private float _rayDown = 3.0f;

        [Header("Grounding")]
        [Tooltip("Full grounding while |gap| (rendered floor − backend Y) stays within this (m).")]
        [SerializeField, Min(0f)] private float _groundSnapMax = 0.5f;

        [Tooltip("Above this |gap| the body is treated as airborne (jump / displacement) and the " +
                 "correction fades to zero, so the proxy isn't glued to the floor mid-air (m).")]
        [SerializeField, Min(0f)] private float _airborneFade = 1.5f;

        [Tooltip("SmoothDamp time of the applied vertical offset (lower = snappier).")]
        [SerializeField, Min(0f)] private float _smoothTime = 0.1f;

        private Transform _hips;
        private bool _hasRig;
        private float _offset;    // smoothed world-Y shift applied to Hips this frame
        private float _offsetVel; // SmoothDamp state

        private void Awake()
        {
            _hips = FindBone("Hips");
            _hasRig = _hips != null; // no hips → nothing to ground
        }

        // Re-arm for pool reuse: clear the offset so a recycled proxy never starts pre-shifted.
        private void OnEnable()
        {
            _offset = 0f;
            _offsetVel = 0f;
        }

        private void LateUpdate()
        {
            if (!_hasRig)
                return;

            float target = ResolveOffset();
            _offset = Mathf.SmoothDamp(_offset, target, ref _offsetVel, _smoothTime);

            if (!Mathf.Approximately(_offset, 0f))
                _hips.position += new Vector3(0f, _offset, 0f);
        }

        /// <summary>
        /// Desired Hips Y shift this frame: the gap between the rendered floor (raycast) and the
        /// backend feet level (root.position.y), weighted down to zero as the body goes airborne.
        /// </summary>
        private float ResolveOffset()
        {
            Vector3 origin = transform.position + Vector3.up * _rayUp;
            float maxDist = _rayUp + _rayDown;

            // QueryTriggerInteraction.Ignore: never ground on trigger volumes (item pickups, zones).
            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, maxDist,
                    GridChunkBuilder.GeoMask, QueryTriggerInteraction.Ignore))
                return 0f; // no floor info → don't shift

            float gap = hit.point.y - transform.position.y;
            return gap * AirborneWeight(Mathf.Abs(gap));
        }

        // 1 while |gap| ≤ _groundSnapMax, ramping linearly to 0 by _airborneFade. Keeps jumps /
        // teleports unglued (the body's gap grows past the band) without a separate velocity track.
        private float AirborneWeight(float absGap)
        {
            if (absGap <= _groundSnapMax)
                return 1f;
            float span = Mathf.Max(0.0001f, _airborneFade - _groundSnapMax);
            return 1f - Mathf.Clamp01((absGap - _groundSnapMax) / span);
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
