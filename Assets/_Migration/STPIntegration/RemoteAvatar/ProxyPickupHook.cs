using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// Fires the proxy's full-body Pickup (Animator trigger "Pickup") when this remote player's
    /// network animation string transitions to "pickup" (ADR-011). The string is a TRIGGER FLANK
    /// the backend holds ~1s so the client reliably catches it despite ~5Hz samples; the gesture's
    /// duration is owned by the Animator (Pickup→Movement exitTime, clip at PickupSpeed), not the window.
    ///
    /// Edge detection with a <c>_wasPickup</c> latch: SetTrigger once on the false→"pickup"
    /// transition, never again while the string stays "pickup"; re-arms when it leaves "pickup".
    /// Same latch pattern as ProxyJumpFeeder. OnEnable resets the latch for pool reuse.
    ///
    /// The per-proxy animation string is read from the RemotePlayerManager view whose root is this
    /// GameObject (no modification to RemotePlayerManager — it already publishes view.animationState).
    /// Attach to the avatar root (same GameObject as the Animator). Removable: delete the file and
    /// the proxy simply never plays pickup (locomotion + jump unaffected).
    /// </summary>
    public sealed class ProxyPickupHook : MonoBehaviour
    {
        [Header("Animator")]
        [Tooltip("Animator that owns the Pickup trigger. Auto-filled from this GameObject if empty.")]
        [SerializeField] private Animator _animator;

        [Header("Local input-lock timing")]
        [Tooltip("Gesture duration (pickup clip length ÷ playback speed), baked by " +
                 "RemoteAvatarPrefabBuilder. LocalPickupInputLock reads it off this Resources prefab " +
                 "to freeze the LOCAL player for exactly the gesture. Fallback value if unbaked.")]
        [SerializeField] private float _gestureDuration = 0.6f;

        /// <summary>Pickup gesture length in seconds (clip ÷ speed), baked at build time.</summary>
        public float GestureDuration => _gestureDuration;

        private const string PickupAnim = "pickup";

        private int _hashPickup;
        private RemotePlayerManager _manager;
        private bool _wasPickup;

        private void Awake()
        {
            _hashPickup = Animator.StringToHash("Pickup");
            if (_animator == null)
                _animator = GetComponent<Animator>();
        }

        // Re-arm the latch on enable so pool reuse never swallows or replays a stale flank.
        private void OnEnable() => _wasPickup = false;

        private void Update()
        {
            if (_animator == null)
                return;

            string anim = ResolveAnimationState();
            // FIX (double-trigger): a null reading means "unknown this frame" (the view wasn't
            // resolved), NOT "not pickup". Treating null as not-pickup re-armed the latch mid-window
            // → a second SetTrigger → the queued trigger replayed the clip on exit. Leave the latch
            // untouched on null; only a DEFINITE non-"pickup" reading re-arms it.
            if (anim == null)
                return;

            bool isPickup = anim == PickupAnim;

            if (isPickup && !_wasPickup)
                _animator.SetTrigger(_hashPickup); // edge: exactly once per transition into "pickup"

            _wasPickup = isPickup;
        }

        /// <summary>This proxy's network animation string, via the RemotePlayerManager view whose root is us.</summary>
        private string ResolveAnimationState()
        {
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return null;

            return view.animationState;
        }

#if UNITY_EDITOR
        private void Reset() => _animator = GetComponent<Animator>();
#endif
    }
}
