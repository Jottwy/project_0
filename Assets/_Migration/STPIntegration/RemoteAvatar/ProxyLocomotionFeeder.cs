using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// Drives the locomotion BlendTree of a remote-player avatar (proxy) by writing the
    /// vendor <c>MovementSpeed</c> float every frame, derived from the proxy's own
    /// network-driven motion — NOT from a packet field and NOT from STP's native motor.
    ///
    /// Why this exists: the proxy body (MTP_PlayerViewer variant) is a pure visual viewer
    /// with no CharacterControllerMotor / PlayerMovementController, and NOTHING in the
    /// project writes the controller's <c>MovementSpeed</c> parameter. Left alone the float
    /// stays at 0 and the avatar is stuck in Idle. RemotePlayerManager interpolates the
    /// avatar's Transform toward each world_state sample (exp-smoothing lerp), so reading
    /// <see cref="Transform.position"/> per frame yields a clean planar velocity to map onto
    /// the BlendTree's vendor "tier" scale (Idle=0, Walk=1, Run=3 — not m/s, not normalized).
    ///
    /// TELEPORT GUARD (critical): chunk displacement and respawns move the avatar many metres
    /// in a single frame. A raw delta then would read as a huge "run" spike. Any single-frame
    /// XZ jump beyond <see cref="_teleportDistance"/> is treated as a teleport: velocity is not
    /// computed, the tier resets to 0, and the previous position is re-baselined.
    ///
    /// Lives entirely under _Migration/ and depends only on UnityEngine + the Animator; the
    /// vendor prefab/controller are untouched. Attach to the avatar root (where the Animator
    /// is). Fully removable: delete the file and the proxy falls back to a static Idle pose.
    /// </summary>
    public sealed class ProxyLocomotionFeeder : MonoBehaviour
    {
        // Vendor BlendTree thresholds (STP_Template_Human, 1D Simple): the float we feed.
        private const float WalkTier = 1f;
        private const float RunTier = 3f;

        [Header("Animator")]
        [Tooltip("Animator that owns the MovementSpeed BlendTree. Auto-filled from this GameObject if left empty.")]
        [SerializeField] private Animator _animator;

        [Header("Speed → tier mapping (m/s)")]
        [Tooltip("Planar speed below this is treated as standing still (tier 0 / Idle).")]
        [SerializeField, Min(0f)] private float _deadzoneSpeed = 0.1f;
        [Tooltip("Planar speed mapped to tier 1 (Walk). deadzone..walk → 0..1.")]
        [SerializeField, Min(0f)] private float _walkSpeed = 1.5f;
        [Tooltip("Planar speed mapped to tier 3 (Run). walk..run → 1..3; above this clamps to 3.")]
        [SerializeField, Min(0f)] private float _runSpeed = 4.5f;

        [Header("Smoothing")]
        [Tooltip("SmoothDamp time applied to the tier before writing it, to avoid walk↔run flicker at the edges.")]
        [SerializeField, Min(0f)] private float _smoothTime = 0.12f;

        [Header("Teleport guard")]
        [Tooltip("Single-frame XZ jump (m) above which the move is treated as a teleport " +
                 "(chunk displacement / respawn): velocity is not computed and the tier resets to 0. " +
                 "Absolute metres is dt-robust: even running at runSpeed on a low framerate stays well below this.")]
        [SerializeField, Min(0f)] private float _teleportDistance = 2.0f;

        private int _hashMovementSpeed;
        private Vector3 _prevPos;
        private bool _hasPrevPos;
        private float _smoothedTier;
        private float _smoothVelocity;

        private void Awake()
        {
            _hashMovementSpeed = Animator.StringToHash("MovementSpeed");
            if (_animator == null)
                _animator = GetComponent<Animator>();
        }

        // Re-baseline on enable so pool reuse (RemotePlayerManager toggles SetActive) and the
        // initial spawn snap never produce a velocity spike: first LateUpdate just captures pos.
        private void OnEnable()
        {
            _hasPrevPos = false;
            _smoothedTier = 0f;
            _smoothVelocity = 0f;
            WriteTier(0f);
        }

        private void LateUpdate()
        {
            if (_animator == null)
                return;

            Vector3 pos = transform.position;

            if (!_hasPrevPos)
            {
                _prevPos = pos;
                _hasPrevPos = true;
                WriteTier(0f);
                return;
            }

            Vector3 delta = pos - _prevPos;
            float planarDist = new Vector3(delta.x, 0f, delta.z).magnitude;

            // Teleport: skip this frame's velocity, drop to Idle, re-baseline.
            if (planarDist > _teleportDistance)
            {
                _prevPos = pos;
                _smoothedTier = 0f;
                _smoothVelocity = 0f;
                WriteTier(0f);
                return;
            }

            _prevPos = pos;

            float dt = Time.deltaTime;
            float speed = dt > 0f ? planarDist / dt : 0f;
            float targetTier = SpeedToTier(speed);

            _smoothedTier = Mathf.SmoothDamp(_smoothedTier, targetTier, ref _smoothVelocity, _smoothTime);
            WriteTier(_smoothedTier);
        }

        /// <summary>Maps a planar speed (m/s) onto the vendor tier scale: 0 (Idle) / 1 (Walk) / 3 (Run).</summary>
        private float SpeedToTier(float speed)
        {
            if (speed <= _deadzoneSpeed)
                return 0f;

            if (speed <= _walkSpeed)
            {
                float denom = Mathf.Max(0.0001f, _walkSpeed - _deadzoneSpeed);
                return Mathf.Clamp01((speed - _deadzoneSpeed) / denom);
            }

            if (speed <= _runSpeed)
            {
                float denom = Mathf.Max(0.0001f, _runSpeed - _walkSpeed);
                float t = Mathf.Clamp01((speed - _walkSpeed) / denom);
                return Mathf.Lerp(WalkTier, RunTier, t);
            }

            return RunTier;
        }

        private void WriteTier(float value) => _animator.SetFloat(_hashMovementSpeed, value);

#if UNITY_EDITOR
        private void Reset() => _animator = GetComponent<Animator>();
#endif
    }
}
