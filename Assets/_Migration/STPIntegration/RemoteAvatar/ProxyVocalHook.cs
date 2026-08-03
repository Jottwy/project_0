using BackroomsSurvival.Net;
using PolymindGames; // AudioManager / AudioChannel
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-048 — plays the robapieles' voice off the networked <c>vocalSeq</c> counter.
    ///
    /// WHY A COUNTER AND NOT THE REVEAL FLAG. <see cref="ProxyRevealHook"/> used to infer the scream
    /// from the edge of <c>revealed</c>, which works for exactly one sound: the one that happens in
    /// SPRINT. ADR-038 fixes <c>revealed</c> as a level derived ONLY from Sprint/Statue and forbids
    /// deriving it from anything else, and the sounds the design wants now — a shriek while it
    /// closes on you following a shot, a grunt when it hears one — happen WITH THE DISGUISE ON.
    /// Hanging them off the reveal would mean revealing a creature that must still look like a
    /// player, i.e. destroying the disguise in order to make a noise.
    ///
    /// DELTA, NEVER LEVEL. The counter is monotonic and wrapping (and never lands back on 0, which
    /// is the "never vocalised" sentinel). A sound modelled as a boolean is a sound lost with the
    /// first dropped datagram at 10 Hz; a delta survives it. Same reasoning as ADR-024's hit_seq and
    /// ADR-044's melee_seq.
    ///
    /// NO BURST REPLAY. A delta greater than 1 means a network gap, not four screams in 100 ms — so
    /// a gap plays ONE sound, exactly like ProxyMeleeHook treats a swing backlog.
    ///
    /// SENTINEL ON FIRST SAMPLE. <c>_lastSeq</c> starts at a value no counter can hold, so a proxy
    /// that spawns — or a late joiner receiving a roster where a creature already vocalised — never
    /// fires for a sound that happened before it was watching. That is the specific failure the
    /// pose-relay hook convention calls out.
    ///
    /// HARD CUTOFF, deliberately (ADR-042): Unity's default logarithmic rolloff never truly reaches
    /// zero, and a creature audible from anywhere stops being a scare and becomes a radar.
    ///
    /// For every real player the counter stays 0 forever, so this hook costs one int comparison per
    /// frame and never touches an AudioSource. Removable: delete the file and the creature simply
    /// goes quiet; nothing else breaks.
    /// </summary>
    public sealed class ProxyVocalHook : MonoBehaviour
    {
        /// <summary>Voice ids, mirroring the VOCAL_* constants in the backend's game_loop.rs.</summary>
        private const int KindReveal = 0;
        private const int KindSearchShriek = 1;
        private const int KindNoiseGrunt = 2;
        private const int KindStalkBreath = 3;
        private const int KindCount = 4;

        // No counter can hold this, so the first sample is always "no change" and never a trigger.
        private const int NoSample = int.MinValue;

        [Header("Voices (index = vocal_kind)")]
        [Tooltip("0 = reveal scream, 1 = search shriek, 2 = noise grunt, 3 = stalking breath. " +
                 "One clip is picked at random from the bank for that kind. An empty bank is silent.")]
        [SerializeField] private VoiceBank[] _voices = new VoiceBank[KindCount];

        [Header("Falloff")]
        [Tooltip("Metres of full volume before the voice starts falling off.")]
        [SerializeField, Min(0f)] private float _minDistance = 4f;

        [Tooltip("Hard cutoff (m). A shriek must carry much further than a footstep and must still END.")]
        [SerializeField, Min(1f)] private float _maxDistance = 45f;

        [Tooltip("Random pitch spread, so the same handful of clips never reads as a handful of clips.")]
        [SerializeField, Range(0f, 0.4f)] private float _pitchVariation = 0.14f;

        [Header("While revealed")]
        [Tooltip("Full-volume radius once the disguise is off (m). It is not pretending any more.")]
        [SerializeField, Min(0f)] private float _revealedMinDistance = 9f;

        [Tooltip("Hard cutoff while revealed (m). Wider, but it MUST still end — a scream audible " +
                 "from anywhere stops being a scare and becomes a radar (ADR-042).")]
        [SerializeField, Min(1f)] private float _revealedMaxDistance = 70f;

        [Tooltip("Volume multiplier while revealed. Left at 1 by default and the extra loudness comes " +
                 "from the WIDER CURVE above (full volume out to 9 m instead of 4), because the scream " +
                 "clips are already mastered near peak — multiplying them would clip, not impress. " +
                 "Raise it only if you also re-master the clips with headroom, like PhantomStep_* has.")]
        [SerializeField, Range(1f, 3f)] private float _revealedVolume = 1f;

        [Tooltip("Pitch multiplier while revealed. Below 1 drops it into a bigger chest — the same " +
                 "clip an octave down reads as a much larger animal.")]
        [SerializeField, Range(0.5f, 1f)] private float _revealedPitch = 0.82f;

        [System.Serializable]
        private struct VoiceBank
        {
            public AudioClip[] Clips;
        }

        private RemotePlayerManager _manager;
        private AudioSource _source;
        private int _lastSeq = NoSample;
        private bool _rangeIsRevealed;

        // Re-arm for pool reuse: a recycled proxy must not scream for its previous occupant, and
        // must not inherit its counter either.
        private void OnEnable()
        {
            _lastSeq = NoSample;
            if (_source != null)
                _source.Stop();
        }

        private void Update()
        {
            if (!TryResolveVoice(out int seq, out int kind))
                return;

            // 0 is the "never vocalised" sentinel, not a sound. A real player sits here forever.
            if (seq == 0)
            {
                _lastSeq = 0;
                return;
            }

            if (_lastSeq == NoSample)
            {
                // First sample: adopt whatever the creature is already on WITHOUT playing it.
                _lastSeq = seq;
                return;
            }

            if (seq == _lastSeq)
                return;

            _lastSeq = seq;
            Play(kind); // one sound per change, however big the gap
        }

        private void Play(int kind)
        {
            if (_voices == null || kind < 0 || kind >= _voices.Length)
                return;

            var clips = _voices[kind].Clips;
            if (clips == null || clips.Length == 0)
                return;

            var clip = clips[Random.Range(0, clips.Length)];
            if (clip == null)
                return;

            // Revealed, it carries further, hits harder and sits lower. Resolved at PLAY time, which
            // is rare — the per-frame path above never asks.
            bool revealed = ResolveRevealed();
            var src = EnsureSource();
            ApplyRange(revealed);

            float pitch = _pitchVariation > 0f
                ? 1f + Random.Range(-_pitchVariation, _pitchVariation)
                : 1f;
            src.pitch = revealed ? pitch * _revealedPitch : pitch;
            src.PlayOneShot(clip, revealed ? _revealedVolume : 1f);
        }

        /// <summary>Swap the distance curve, and only on a change — see ProxyFootstepHook.</summary>
        private void ApplyRange(bool revealed)
        {
            if (_rangeIsRevealed == revealed && _source != null)
                return;
            _rangeIsRevealed = revealed;
            ProxyAudioCurves.ApplyHardCutoff(_source,
                revealed ? _revealedMinDistance : _minDistance,
                revealed ? _revealedMaxDistance : _maxDistance);
        }

        /// <summary>Is this proxy showing its real form? Own lookup, so the hook stays removable.</summary>
        private bool ResolveRevealed()
        {
            if (_manager == null)
                _manager = GetComponentInParent<RemotePlayerManager>();
            if (_manager == null)
                return false;

            foreach (var kvp in _manager.ActivePlayers)
            {
                var view = kvp.Value;
                if (view != null && view.root == transform)
                    return view.revealed;
            }
            return false;
        }

        private AudioSource EnsureSource()
        {
            if (_source != null)
                return _source;

            // Parented to the proxy so the voice tracks the thing that made it: a cry that hangs
            // where the creature WAS points the player at empty corridor.
            var go = new GameObject("PhantomVoice");
            go.transform.SetParent(transform, false);

            _source = go.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 1f; // at 0 it would be dead-centre at any distance
            _source.dopplerLevel = 0f;
            ProxyAudioCurves.ApplyHardCutoff(_source, _minDistance, _maxDistance);

            // No manager (a bare test scene) → still audible, just unmixed. Never an exception.
            var audio = AudioManager.Instance;
            _source.outputAudioMixerGroup = audio != null ? audio.GetMixerGroup(AudioChannel.Sfx) : null;
            return _source;
        }

        /// <summary>This proxy's networked voice fields, via the view whose root is this GameObject.</summary>
        private bool TryResolveVoice(out int seq, out int kind)
        {
            seq = 0;
            kind = 0;

            if (_manager == null)
                _manager = GetComponentInParent<RemotePlayerManager>();
            if (_manager == null)
                return false;

            foreach (var kvp in _manager.ActivePlayers)
            {
                var view = kvp.Value;
                if (view != null && view.root == transform)
                {
                    seq = view.vocalSeq;
                    kind = view.vocalKind;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Kind ids, exposed so the prefab builder can label the banks it wires.</summary>
        public static string KindName(int kind) => kind switch
        {
            KindReveal => "Reveal",
            KindSearchShriek => "SearchShriek",
            KindNoiseGrunt => "NoiseGrunt",
            KindStalkBreath => "StalkBreath",
            _ => "Unknown",
        };
    }
}
