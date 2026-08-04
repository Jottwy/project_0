using BackroomsSurvival.Net;
using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-044: makes a peer AUDIBLE when they are hurt or die.
    ///
    /// COSTS NOTHING ON THE WIRE. The signal has been arriving since ADR-024: <c>view.hitSeq</c> is a
    /// monotonic counter bumped on the victim's own <c>DamageReceived</c>, and <c>view.dead</c> is
    /// server-derived. Until now the counter only drove the VISUAL flinch in
    /// <see cref="ProxyHitReactionHook"/>, so you watched a peer take a hit in complete silence. The
    /// gap was never data — it was that the data went unused.
    ///
    /// Deliberately a SEPARATE hook rather than three lines inside ProxyHitReactionHook: that one is
    /// a bone-space procedural recoil with its own tuning, this one is audio with its own distance
    /// curve, and the folder's whole discipline is that each file can be deleted on its own without
    /// taking anything else with it. They share the counter, not the code — and because each keeps
    /// its OWN sentinel, deleting either leaves the other correct.
    ///
    /// Clips ship with the project (FPSCore's own <c>FPS_Human_GenericDamage1..3</c>), so unlike the
    /// gunshot of ADR-042 this is audible out of the box with nothing to author. Hurt clips are
    /// picked at random so repeated hits do not machine-gun the same sample.
    ///
    /// NO DEATH CRY HERE, and it is not an oversight — it is unreachable from this component and the
    /// reason is worth writing down so nobody "fixes" it back in. ADR-028 post-E3 has
    /// RemotePlayerManager call <c>SetActive(false)</c> on the proxy ROOT in the very same statement
    /// block that flips <c>view.dead</c>, because the corpse becomes the visible body. This hook
    /// lives on that root, so by the time any <c>Update</c> could observe <c>dead == true</c> the
    /// component is already disabled. That is deterministic, not a race: a death clip added here
    /// would simply never play. Its correct home is the corpse side (CorpseSpawner), and it needs one
    /// extra guard before it is correct there — corpses are also instantiated when JOINING a session
    /// with saved bodies in the roster, and a naive hook would greet a joiner with a death cry for
    /// every corpse already lying on the floor. Deferred with that requirement stated.
    ///
    /// Range is short on purpose: a grunt is an intimate sound and must not become a wallhack that
    /// tells you someone is hurt across the level.
    /// </summary>
    public sealed class ProxyDamageAudioHook : MonoBehaviour
    {
        [Header("Clips")]
        [Tooltip("Pain grunts. One is chosen at random per hit; leave empty for silence.")]
        [SerializeField] private AudioClip[] _hurtClips = System.Array.Empty<AudioClip>();

        [SerializeField, Range(0f, 1f)] private float _hurtVolume = 0.85f;

        [Header("Distance curve")]
        [Tooltip("Full-volume radius, in metres.")]
        [SerializeField, Min(0.1f)] private float _minDistance = 4f;

        [Tooltip("HARD cutoff, in metres — beyond this the grunt is SILENT. Short on purpose: knowing " +
                 "someone is hurt is tactical information, so it must not carry across the level.")]
        [SerializeField, Min(1f)] private float _maxDistance = 28f;

        [Tooltip("Per-hit random pitch spread, so repeated grunts are not the same sample stamped twice.")]
        [SerializeField, Range(0f, 0.2f)] private float _pitchVariation = 0.07f;

        // Sentinel: the first observed counter value arms without playing. Without it a late-joiner
        // would hear a grunt from every peer that has ever been hit, the moment their proxy spawns.
        private const int Unseen = int.MinValue;

        private RemotePlayerManager _manager;
        private AudioSource _source;
        private int _lastSeen = Unseen;

        // Re-arm for pool reuse: a recycled proxy must not grunt on its first sample.
        private void OnEnable()
        {
            _lastSeen = Unseen;
            if (_source != null)
                _source.Stop();
        }

        private void Update()
        {
            if (!TryResolveView(out var view))
                return;

            int seq = view.hitSeq;
            if (_lastSeen == Unseen)
            {
                _lastSeen = seq; // first sample: arm, never play
                return;
            }
            if (seq == _lastSeen)
                return;

            _lastSeen = seq;
            if (view.dead)
                return; // corpses do not grunt (defensive: the root is normally already inactive)

            Play(PickHurtClip(), _hurtVolume);
        }

        private AudioClip PickHurtClip()
        {
            if (_hurtClips == null || _hurtClips.Length == 0)
                return null;
            return _hurtClips[Random.Range(0, _hurtClips.Length)];
        }

        private void Play(AudioClip clip, float volume)
        {
            if (clip == null)
                return;

            var src = EnsureSource();
            src.pitch = _pitchVariation > 0f ? 1f + Random.Range(-_pitchVariation, _pitchVariation) : 1f;
            src.PlayOneShot(clip, volume);
        }

        /// <summary>Builds this proxy's own voice source, lazily. Uses the shared hard-cutoff curve so
        /// the grunt genuinely stops at maxDistance — Unity's Logarithmic mode would hold a constant
        /// floor for ever instead (see <see cref="ProxyAudioCurves"/>).</summary>
        private AudioSource EnsureSource()
        {
            if (_source != null)
                return _source;

            var go = new GameObject("ProxyDamageVoice");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 1.5f, 0f); // roughly head height, not the feet

            _source = go.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 1f;
            _source.dopplerLevel = 0f;
            ProxyAudioCurves.ApplyHardCutoff(_source, _minDistance, _maxDistance);

            var audio = AudioManager.Instance;
            _source.outputAudioMixerGroup = audio != null ? audio.GetMixerGroup(AudioChannel.Sfx) : null;
            return _source;
        }

        /// <summary>This proxy's networked view, via the RemotePlayerManager whose child we are — the
        /// same lookup as every other hook here.</summary>
        private bool TryResolveView(out RemotePlayerView view)
            => ProxyViewLookup.TryResolve(transform, ref _manager, out view);
    }
}
