using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// Fires the proxy's full-body Jump (Animator trigger "Jump") from reconstructed vertical
    /// velocity (deltaY / dt of the network-driven Transform) — client-only, no packet/Rust.
    /// Sibling of ProxyLocomotionFeeder; never touches it (locomotion stays on MovementSpeed).
    ///
    /// Edge-triggered with an <c>_airborne</c> latch: fires ONCE on grounded→airborne
    /// (vy >= jumpVelocityUp) and re-arms only after landing (vy &lt;= landVelocity), so one jump
    /// = one trigger. Discrimination criteria baked in:
    ///   * RAMP/STAIR: their vertical speed is SUSTAINED but MODERATE, kept below jumpVelocityUp,
    ///     so it never crosses the threshold; only an impulsive jump spike does.
    ///   * VERTICAL TELEPORT (layer change / respawn): a single-frame |dy| beyond
    ///     verticalTeleportDistance is rejected and re-baselined — this covers the gap left by
    ///     ProxyLocomotionFeeder's XZ-only teleport guard (which never sees Y).
    ///
    /// Attach to the avatar root (same GameObject as the Animator). Removable: delete the file
    /// and the proxy simply never plays jump.
    /// </summary>
    public sealed class ProxyJumpFeeder : MonoBehaviour
    {
        [Header("Animator")]
        [Tooltip("Animator that owns the Jump trigger. Auto-filled from this GameObject if empty.")]
        [SerializeField] private Animator _animator;

        [Header("Vertical thresholds (m/s)")]
        [Tooltip("Upward vertical speed that counts as a jump impulse. Set above the max sustained " +
                 "ramp/stair climb speed and below the jump apex velocity.")]
        [SerializeField] private float _jumpVelocityUp = 2.5f;
        [Tooltip("Vertical speed at/below which the proxy is considered grounded again (re-arms the next jump).")]
        [SerializeField] private float _landVelocity = -0.5f;

        [Header("Vertical teleport guard")]
        [Tooltip("Single-frame |deltaY| (m) above which the move is a teleport (layer change / respawn): " +
                 "no jump, re-baseline. Set above the largest plausible single-frame jump rise.")]
        [SerializeField, Min(0f)] private float _verticalTeleportDistance = 3.0f;

        private int _hashJump;
        private float _prevY;
        private bool _hasPrevY;
        private bool _airborne;

        private void Awake()
        {
            _hashJump = Animator.StringToHash("Jump");
            if (_animator == null)
                _animator = GetComponent<Animator>();
        }

        // Re-baseline on enable so pool reuse (RemotePlayerManager toggles SetActive) and the
        // spawn snap never fire a spurious jump: first LateUpdate just captures Y.
        private void OnEnable()
        {
            _hasPrevY = false;
            _airborne = false;
        }

        private void LateUpdate()
        {
            if (_animator == null)
                return;

            float y = transform.position.y;

            if (!_hasPrevY)
            {
                _prevY = y;
                _hasPrevY = true;
                return;
            }

            float dy = y - _prevY;
            _prevY = y;

            // Vertical teleport (layer change / respawn): re-baseline, never a jump.
            if (Mathf.Abs(dy) > _verticalTeleportDistance)
            {
                _airborne = false;
                return;
            }

            float dt = Time.deltaTime;
            float vy = dt > 0f ? dy / dt : 0f;

            if (!_airborne && vy >= _jumpVelocityUp)
            {
                _animator.SetTrigger(_hashJump);
                _airborne = true;
            }
            else if (_airborne && vy <= _landVelocity)
            {
                _airborne = false;
            }
        }

#if UNITY_EDITOR
        private void Reset() => _animator = GetComponent<Animator>();
#endif
    }
}
