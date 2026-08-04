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
    /// PLAY-TEST FIX (2026-08-02) — "a shot from far away still sounds close". It did, and volume was
    /// never going to fix it: a quiet, BRIGHT, instantaneous bang reads to the ear as a small gun
    /// nearby, not as a rifle far off. Distance is carried by three cues, and only one of them was
    /// present. The other two are now:
    ///   • TRAVEL TIME. Sound moves at ~343 m/s, so a shot at 200 m is heard 0.58 s after it happened.
    ///     Nothing else separates "far" from "quiet" as hard as arriving late.
    ///   • AIR ABSORPTION. Air eats the high frequencies first, which is why distant gunfire is a dull
    ///     BOOM and never a crisp CRACK. Modelled as a low-pass whose cutoff closes with distance,
    ///     interpolated on sqrt so it muffles early instead of staying bright until the last metre.
    ///   • Spread widens with distance too, so a far shot stops being a pinpoint in the stereo field.
    /// All three are computed ONCE per shot against the listener's position — a gunshot is an impulse,
    /// so a snapshot is correct and there is nothing to update per frame.
    ///
    /// KNOWN LIMIT, declared: there is still no occlusion. A shot behind a wall is attenuated and
    /// muffled by DISTANCE only, so geometry does not block it. That needs a linecast feeding the same
    /// filter; deferred deliberately, it is a bigger feature than this hook.
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
        private AudioSource _echo;
        private static AudioListener _listener;

        // Re-arm for pool reuse: a recycled proxy must not fire on its first sample, nor keep a
        // previous occupant's shot ringing.
        private void OnEnable()
        {
            _lastSeen = Unseen;
            if (_crack != null) _crack.Stop();
            if (_tail != null) _tail.Stop();
            if (_echo != null) _echo.Stop();
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

            // Snapshot the distance ONCE: a gunshot is an impulse, so the three distance cues are
            // fixed at the moment it is fired and there is nothing to track per frame.
            float distance = DistanceToListener();
            float travel = _audioSet.TravelTime(distance);
            float cutoff = _audioSet.CutoffForDistance(distance);
            float spread = _audioSet.SpreadForDistance(distance);

            StartCoroutine(PlayDelayed(EnsureCrack(), clip, volume, travel, cutoff, spread));

            bool hasTail = _audioSet.TryResolveTail(heldItem, out var tailClip);
            if (hasTail)
            {
                // The tail is already the far-field component, so it gets muffled harder still: the
                // boom should read as rolling in from a distance even when the crack is nearby.
                StartCoroutine(PlayDelayed(EnsureTail(), tailClip, _audioSet.tailVolume,
                    travel + _audioSet.tailDelay, cutoff * 0.6f, spread));
            }

            // Corridor slap-back. Reflections carry the tail clip when there is one (a boom bounces
            // better than a crack) and otherwise re-use the shot itself.
            float echo = _audioSet.EchoStrength(distance);
            if (_audioSet.echoTaps > 0 && echo > 0.01f)
            {
                var echoClip = hasTail ? tailClip : clip;
                float level = volume * echo;
                float tapCutoff = cutoff;
                float delay = travel + _audioSet.echoFirstDelay;

                for (int tap = 0; tap < _audioSet.echoTaps; tap++)
                {
                    level *= _audioSet.echoFalloff;
                    tapCutoff *= _audioSet.echoCutoffScale;
                    if (level < 0.01f)
                        break; // inaudible: stop spawning coroutines nobody will hear

                    // Reflections arrive from the room, not from the shooter, so they are played as
                    // wide as the source allows — a slap-back that is still a pinpoint reads as a
                    // stutter in the sample rather than as a space.
                    StartCoroutine(PlayDelayed(EnsureEcho(), echoClip, level, delay, tapCutoff,
                        Mathf.Max(spread, 90f)));
                    delay += _audioSet.echoSpacing;
                }
            }
        }

        private IEnumerator PlayDelayed(AudioSource src, AudioClip clip, float volume, float delay,
            float cutoff, float spread)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);
            if (src == null || clip == null)
                yield break; // proxy released mid-flight (peer left while the sound was travelling)

            src.spread = spread;
            var filter = src.GetComponent<AudioLowPassFilter>();
            if (filter != null)
                filter.cutoffFrequency = Mathf.Clamp(cutoff, 10f, 22000f);

            // Per-shot detune. Caveat, accepted: PlayOneShot voices already ringing on this source
            // follow the new pitch, so a burst bends very slightly. At ±5 % that is inaudible, and the
            // alternative — one AudioSource per voice — costs far more than the artifact is worth.
            float v = _audioSet != null ? _audioSet.pitchVariation : 0f;
            src.pitch = v > 0f ? 1f + Random.Range(-v, v) : 1f;

            // PlayOneShot, not Play: the shots of a burst must overlap and ring out on top of each
            // other. Play() would cut the previous shot dead at every trigger pull.
            src.PlayOneShot(clip, volume);
        }

        /// <summary>Distance from this proxy to the listener. Cached statically because every proxy
        /// asks the same question and there is exactly one listener in the scene.</summary>
        private float DistanceToListener()
        {
            if (_listener == null)
                _listener = FindFirstObjectByType<AudioListener>();
            if (_listener == null)
                return 0f; // no listener yet → treat as point-blank rather than guessing
            return Vector3.Distance(transform.position, _listener.transform.position);
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

        // Reflections get their own source so their spread and cutoff never fight the direct sound.
        private AudioSource EnsureEcho()
        {
            if (_echo == null)
                _echo = CreateSource("ProxyShotEcho", _audioSet.tailMinDistance);
            return _echo;
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

            ProxyAudioSourceFactory.ApplyRolloff(src, _audioSet.hardCutoff, _audioSet.rolloff,
                minDistance, _audioSet.maxDistance);

            src.spread = _audioSet.spread;
            src.dopplerLevel = 0f; // a gunshot is an impulse; doppler on it is pure artifact

            // Air absorption. Created open (22 kHz = inaudible as a filter) and closed per shot from
            // the firing distance — see PlayDelayed.
            var filter = go.AddComponent<AudioLowPassFilter>();
            filter.cutoffFrequency = 22000f;
            // No manager (a bare test scene) → still audible, just unmixed. Never an exception.
            var audio = AudioManager.Instance;
            src.outputAudioMixerGroup = audio != null ? audio.GetMixerGroup(AudioChannel.Sfx) : null;
            return src;
        }

        /// <summary>This proxy's networked view, via the RemotePlayerManager whose child we are — the
        /// same lookup as every other hook here, so RemotePlayerManager needs no knowledge of us.</summary>
        private bool TryResolveView(out RemotePlayerView view)
            => ProxyViewLookup.TryResolve(transform, ref _manager, out view);
    }
}
