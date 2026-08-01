using System.Collections;
using BackroomsSurvival.Net;
using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-042: plays a peer's gunshot on their proxy, driven by the networked <c>view.fireSeq</c>
    /// counter (bumped on the shooter's own <c>IFirearmTrigger.Shoot</c>). Which weapon fired comes
    /// from <c>view.heldItem</c> (ADR-023), which already travels — the shot itself carries no weapon id.
    ///
    /// THE COUNTER IS READ AS A DELTA, and that is the whole reason it is a counter and not a flag.
    /// A full-auto weapon at 600 RPM fires ten times a second while the pose relay runs at 10 Hz, so a
    /// boolean edge would drop nearly every shot of a burst. The delta over a wrapping byte survives
    /// that, and survives a dropped packet too: the next pose carries the same total.
    ///
    /// The delta is CAPPED and STAGGERED. Uncapped, a peer reappearing after a network stall would
    /// discharge every missed shot in one frame — two hundred simultaneous detonations. Capped and
    /// spaced, a burst reads as a burst.
    ///
    /// Audio goes through <c>AudioManager.Instance.PlayClip3D</c> at the proxy's position, so it is
    /// spatialised, mixed on the SFX channel and attenuated by the project's own curves — the same
    /// path every other 3D sound in the game takes. Nothing inside PolymindGames is modified.
    ///
    /// A sentinel makes the FIRST observed value never fire (a late-joiner must not hear the whole
    /// history of a peer's magazine on spawn), exactly like <see cref="ProxyHitReactionHook"/>.
    /// Removable: delete the file and peers shoot silently.
    /// </summary>
    public sealed class ProxyFireAudioHook : MonoBehaviour
    {
        [Header("Clips")]
        [Tooltip("Per-weapon fire sounds. Without it (or without a default clip in it) peers stay silent.")]
        [SerializeField] private RemoteWieldableAudioSet _audioSet;

        [Header("Burst handling")]
        [Tooltip("Most shots played from a single counter delta. Caps the catch-up after a stall.")]
        [SerializeField, Range(1, 8)] private int _maxShotsPerSample = 3;

        [Tooltip("Seconds between the shots of one burst, so they read as fire rate and not as one bang.")]
        [SerializeField, Range(0.01f, 0.5f)] private float _burstSpacing = 0.06f;

        // Sentinel: the first observed counter value arms _lastSeen without firing.
        private const int Unseen = int.MinValue;

        private RemotePlayerManager _manager;
        private int _lastSeen = Unseen;

        // Re-arm for pool reuse: a recycled proxy must not fire on its first sample.
        private void OnEnable() => _lastSeen = Unseen;

        private void Update()
        {
            if (!TryResolveView(out var view))
                return;

            int seq = view.fireSeq;
            if (_lastSeen == Unseen)
            {
                _lastSeen = seq; // first sample: arm, never fire
                return;
            }
            if (seq == _lastSeen)
                return;

            // Wrapping byte difference: 250 → 3 is a delta of 9, not of −247.
            int delta = (seq - _lastSeen) & 0xFF;
            _lastSeen = seq;

            int shots = Mathf.Min(delta, _maxShotsPerSample);
            if (shots > 0)
                StartCoroutine(PlayBurst(shots, view.heldItem));
        }

        private IEnumerator PlayBurst(int shots, int heldItem)
        {
            for (int i = 0; i < shots; i++)
            {
                PlayOne(heldItem);
                if (i + 1 < shots)
                    yield return new WaitForSeconds(_burstSpacing);
            }
        }

        private void PlayOne(int heldItem)
        {
            if (_audioSet == null)
                return;
            if (!_audioSet.TryResolve(heldItem, out var clip, out float volume))
                return; // nothing authored for this weapon and no default → silence, never a guess

            var audio = AudioManager.Instance;
            if (audio == null)
                return;

            audio.PlayClip3D(clip, transform.position, volume);
        }

        /// <summary>This proxy's networked view, via the RemotePlayerManager whose child we are — the
        /// same lookup as every other hook here, so RemotePlayerManager needs no knowledge of us.</summary>
        private bool TryResolveView(out RemotePlayerView view)
        {
            view = null;
            if (_manager == null)
                _manager = GetComponentInParent<RemotePlayerManager>();
            if (_manager == null)
                return false;

            foreach (var kvp in _manager.ActivePlayers)
            {
                var v = kvp.Value;
                if (v != null && v.root == transform)
                {
                    view = v;
                    return true;
                }
            }
            return false;
        }
    }
}
