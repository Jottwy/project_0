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
    /// AUDIO USES ITS OWN SOURCES, NOT <c>AudioManager.PlayClip3D</c>, and that is not a preference.
    /// The manager's pooled 3D sources are configured for footsteps and impacts: it pins
    /// <c>minDistance = 2</c> and never touches <c>maxDistance</c> or <c>rolloffMode</c>, so they keep
    /// Unity's defaults (logarithmic, 500 m). Logarithmic attenuation is roughly
    /// <c>minDistance / distance</c>, so a 2 m radius leaves ~10 % of the volume at 20 m and ~4 % at
    /// 50 m — a rifle that dies inside one room. Widening the curve on the source the manager RETURNS
    /// would be worse than useless: those sources are POOLED and reused, and <c>PlayClip3D</c> only
    /// re-stamps <c>minDistance</c>, so a changed <c>maxDistance</c>/<c>rolloffMode</c> would stay stuck
    /// on the source and leak into every footstep and bullet impact that borrowed it next.
    ///
    /// So the hook owns two sources, created lazily on the proxy: a CRACK (near, directional) and an
    /// optional TAIL (far, enveloping, larger full-volume radius, slightly delayed). That gap between
    /// the two radii is what gives distance its depth — up close you mostly hear the crack, far away
    /// the tail is what survives. Both are routed through <c>AudioManager.GetMixerGroup(Sfx)</c>, so the
    /// player's own effects-volume setting still applies. Nothing inside PolymindGames is modified.
    ///
    /// KNOWN LIMIT, declared: there is no occlusion. A shot behind a wall attenuates by distance only,
    /// so it is heard through geometry. Fixing it means a linecast plus an AudioLowPassFilter per
    /// source; deferred deliberately, it is a bigger feature than this hook.
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
        private AudioSource _crack;
        private AudioSource _tail;

        // Re-arm for pool reuse: a recycled proxy must not fire on its first sample, nor keep a
        // previous occupant's shot ringing.
        private void OnEnable()
        {
            _lastSeen = Unseen;
            if (_crack != null) _crack.Stop();
            if (_tail != null) _tail.Stop();
        }

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

            // PlayOneShot, not Play: the shots of a burst must overlap and ring out on top of each
            // other. Play() would cut the previous shot dead at every trigger pull.
            EnsureCrack().PlayOneShot(clip, volume);

            if (_audioSet.TryResolveTail(heldItem, out var tailClip))
                StartCoroutine(PlayTail(tailClip));
        }

        private IEnumerator PlayTail(AudioClip tailClip)
        {
            float delay = _audioSet.tailDelay;
            if (delay > 0f)
                yield return new WaitForSeconds(delay);
            EnsureTail().PlayOneShot(tailClip, _audioSet.tailVolume);
        }

        private AudioSource EnsureCrack()
        {
            if (_crack == null)
                _crack = CreateSource("ProxyShotCrack", _audioSet.minDistance);
            return _crack;
        }

        private AudioSource EnsureTail()
        {
            if (_tail == null)
                _tail = CreateSource("ProxyShotTail", _audioSet.tailMinDistance);
            return _tail;
        }

        /// <summary>
        /// Builds one 3D source with the authored curve. Lazy: a peer who never fires never allocates
        /// one. Parented to the proxy so the sound tracks the shooter while it rings out — a burst
        /// from someone running past should sweep, not hang where the first round left.
        /// </summary>
        private AudioSource CreateSource(string label, float minDistance)
        {
            var go = new GameObject(label);
            go.transform.SetParent(transform, false);

            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 1f; // fully 3D; at 0 the shot would be heard dead-centre at any distance
            src.rolloffMode = _audioSet.rolloff;
            src.minDistance = minDistance;
            src.maxDistance = _audioSet.maxDistance;
            src.spread = _audioSet.spread;
            src.dopplerLevel = 0f; // a gunshot is an impulse; doppler on it is pure artifact
            // No manager (a bare test scene) → still audible, just unmixed. Never an exception.
            var audio = AudioManager.Instance;
            src.outputAudioMixerGroup = audio != null ? audio.GetMixerGroup(AudioChannel.Sfx) : null;
            return src;
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
