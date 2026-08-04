using BackroomsSurvival.Gameplay.GridWorld;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// [D] Body grounding (slice 1): pins this remote proxy's body to the RENDERED floor.
    ///
    /// The proxy root is positioned from the backend pose, re-grounded to the rendered floor by
    /// RemotePlayerManager (root Y = backendY − PlayerBaseY), but what is RENDERED and walked is
    /// the client-side ChunkStreamer — a DIFFERENT world (see STATE.md debt). The two floor heights
    /// don't match exactly, so the proxy's feet still float above or sink into the visible floor by
    /// a small residual. This hook raycasts down to the rendered floor and offsets the
    /// <c>Pelvis</c> bone (the whole skeleton with it) so the feet rest on what is actually drawn.
    ///
    /// The rig is GENERIC (not Humanoid) → no <c>OnAnimatorIK</c>; the Pelvis bone is resolved BY
    /// NAME and cached (same approach as ProxyPitchHook). The bone is "Pelvis", NOT "Hips" — this
    /// rig (MTP_PlayerViewer / RemotePlayerAvatar) has no "Hips" (verified in the prefab; same fact
    /// CorpseSpawner relies on). The shift is applied in <c>LateUpdate</c>
    /// AFTER the Animator writes the pose, as an additive world-space Y offset recomputed from
    /// scratch each frame (the Animator re-writes Pelvis first, so nothing accumulates). Only Pelvis
    /// is touched — the networked root stays under RemotePlayerManager (pose lerp) and
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

        private Transform _pelvis;
        private bool _hasRig;
        private float _offset;    // smoothed world-Y shift applied to Pelvis this frame
        private float _offsetVel; // SmoothDamp state

        [Header("Diagnostics — invisible-mesh hunt (2026-08-01), TEMPORARY")]
        [Tooltip("Logs Pelvis drift + cull state ~2x/s. It exists to answer ONE open question: does " +
                 "the Pelvis accumulate while the proxy is culled? Untick (or delete this block) " +
                 "once that is settled. Must run in a BUILD, or with the Scene View NOT framing the " +
                 "proxy — the Scene View camera counts for isVisible and hides the bug.")]
        [SerializeField] private bool _logCullDiagnostics = true;

        /// Pelvis local Y in the vendor bind pose (MTP_PlayerViewer.prefab). A healthy proxy stays
        /// near this; monotonic drift away from it is the accumulation bug.
        private const float BindPosePelvisLocalY = 0.950322f;

        private Animator _animator;
        private SkinnedMeshRenderer _sampleRenderer;
        private float _nextDiagAt;
        private Camera _camera;
        private float _nextCameraProbeAt;
        private bool _armedLogged;

        private void Awake()
        {
            // "Pelvis" — this rig has no "Hips" bone (verified in MTP_PlayerViewer.prefab; the
            // old "Hips" lookup silently no-op'd, which was the remote-proxy floating bug).
            _pelvis = FindBone("Pelvis");
            _hasRig = _pelvis != null; // no pelvis → nothing to ground

            _animator = GetComponentInParent<Animator>();
            // NOT GetComponentInChildren(true): that returns the FIRST renderer including DISABLED
            // ones, and the vendor rig's first child renderer is a head accessory. A disabled
            // wardrobe piece always reports isVisible=false, so the alarm fired 34 times in one
            // session while measuring a switched-off beard. Sample the BODY instead: the largest
            // ENABLED renderer, re-resolved on enable because clothing toggles at runtime.
            _sampleRenderer = ResolveBodyRenderer();
        }

        // Re-arm for pool reuse: clear the offset so a recycled proxy never starts pre-shifted.
        private void OnEnable()
        {
            _offset = 0f;
            _offsetVel = 0f;
            // Clothing toggles at runtime (ProxyClothingHook), so which renderer is the "body" can
            // change between pool reuses. Re-resolve rather than trust the Awake-time pick.
            _sampleRenderer = ResolveBodyRenderer();
            _armedLogged = false;
        }

        private void LateUpdate()
        {
            if (!_hasRig)
                return;

            float target = ResolveOffset();
            _offset = Mathf.SmoothDamp(_offset, target, ref _offsetVel, _smoothTime);

            if (!Mathf.Approximately(_offset, 0f))
                _pelvis.position += new Vector3(0f, _offset, 0f);

            LogCullDiagnostics();
        }

        /// ALARM, not a heartbeat. The first version logged every sample, and 42 % of them read
        /// `meshVisible=False` — which meant nothing, because `Renderer.isVisible` is false whenever
        /// NO camera sees the proxy, and a wandering NPC is off-camera most of the time. That log
        /// could not tell "behind you" from "in front of you and not drawn", so it could not settle
        /// anything.
        ///
        /// This version fires ONLY on the contradiction: the proxy's body is inside the camera
        /// viewport, and the renderer still reports itself invisible. That is the bug and nothing
        /// else. Silence now means "never reproduced", which is the answer we are buying.
        ///
        /// One "armed" line is emitted per proxy so that silence is never confused with the
        /// instrumentation being dead or the fix not having applied.
        private void LogCullDiagnostics()
        {
            if (!_logCullDiagnostics || _sampleRenderer == null)
                return;

            if (!_armedLogged)
            {
                _armedLogged = true;
                Debug.Log(
                    $"[GROUND] armed on '{name}' — updateOffscreen={_sampleRenderer.updateWhenOffscreen} " +
                    $"cullMode={(_animator != null ? _animator.cullingMode.ToString() : "<no-animator>")}. " +
                    "From here on it only speaks when the body is ON SCREEN and the mesh is NOT drawn.");
            }

            var cam = ResolveCamera();
            if (cam == null)
                return;

            // Feet AND head: a body framed only from the waist up still counts as "you should be
            // seeing this". If neither point is in the viewport it is legitimately off-camera.
            Vector3 feet = transform.position;
            if (!InViewport(cam, feet) && !InViewport(cam, feet + Vector3.up * 1.8f))
                return;
            if (_sampleRenderer.isVisible)
                return; // on screen and drawing — healthy, stay quiet

            if (Time.unscaledTime < _nextDiagAt)
                return; // rate-limit the alarm itself, never the detection
            _nextDiagAt = Time.unscaledTime + 0.5f;

            // Bounds are the payload: if their centre has wandered away from the body, the renderer
            // is being culled against a box that is no longer where the character is.
            Bounds b = _sampleRenderer.bounds;
            Debug.LogWarning(
                $"[GROUND-ANOMALY] on screen but not drawn — dist={Vector3.Distance(cam.transform.position, feet):F1} " +
                $"root=({feet.x:F2},{feet.y:F2},{feet.z:F2}) " +
                $"boundsCentre=({b.center.x:F2},{b.center.y:F2},{b.center.z:F2}) boundsSize=({b.size.x:F2},{b.size.y:F2},{b.size.z:F2}) " +
                $"centreOffBody={Vector3.Distance(b.center, feet + Vector3.up * 0.9f):F2} " +
                $"pelvisLocalY={_pelvis.localPosition.y:F3} drift={_pelvis.localPosition.y - BindPosePelvisLocalY:+0.000;-0.000} " +
                $"offset={_offset:F3} updateOffscreen={_sampleRenderer.updateWhenOffscreen} " +
                $"cullMode={(_animator != null ? _animator.cullingMode.ToString() : "<no-animator>")}");
        }

        // Camera.main walks the scene, so it is probed at most once a second and cached.
        private Camera ResolveCamera()
        {
            if (_camera != null)
                return _camera;
            if (Time.unscaledTime < _nextCameraProbeAt)
                return null;
            _nextCameraProbeAt = Time.unscaledTime + 1f;
            _camera = Camera.main;
            return _camera;
        }

        /// The biggest ENABLED skinned renderer — the body, not a hat. Disabled renderers are
        /// skipped because their `isVisible` is permanently false and would fake an anomaly.
        private SkinnedMeshRenderer ResolveBodyRenderer()
        {
            SkinnedMeshRenderer best = null;
            float bestVolume = -1f;
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (smr == null || !smr.enabled)
                    continue;
                Vector3 s = smr.bounds.size;
                float volume = s.x * s.y * s.z;
                if (volume > bestVolume)
                {
                    bestVolume = volume;
                    best = smr;
                }
            }
            return best;
        }

        private static bool InViewport(Camera cam, Vector3 world)
        {
            Vector3 v = cam.WorldToViewportPoint(world);
            return v.z > 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f;
        }

        /// <summary>
        /// Desired Pelvis Y shift this frame: the gap between the rendered floor (raycast) and the
        /// proxy feet level (root.position.y, already re-grounded by RemotePlayerManager), weighted
        /// down to zero as the body goes airborne.
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

        private Transform FindBone(string boneName) => ProxyRigUtil.FindBone(transform, boneName);
    }
}
