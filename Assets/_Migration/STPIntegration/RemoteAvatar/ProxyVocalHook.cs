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
        /// The long-range answer to a gunshot. Gets its OWN curve, far wider than anything else here.
        private const int KindDistantAnswer = 4;
        /// After a kill.
        private const int KindSated = 5;
        /// ADR-050: the hungry moan. The only reading the player gets of the hunger model, and
        /// deliberately not a warning of an imminent charge — it says the animal in front of you is
        /// now the kind that eats, not that it moves in three seconds.
        private const int KindHungryMoan = 6;
        /// ADR-050: out of breath mid-charge. This one IS actionable — it marks the window where
        /// the creature drops to a heavy walk and ground can be bought back.
        private const int KindWinded = 7;
        /// ADR-051: the scream WHILE it comes apart, a beat before the skin actually tears. Distinct
        /// from KindReveal, which now lands on the tear itself — this one still comes out of
        /// something wearing a player's face, and that is what makes it work.
        private const int KindUnmaskScream = 8;
        /// ADR-080 point 3: the swing that did NOT land. Rides the ordinary (revealed) curve rather
        /// than a wide one — a claw missing you is by definition happening at arm's length, so it has
        /// no business being audible across the level.
        private const int KindStrikeMiss = 9;
        private const int KindCount = 10;

        // No counter can hold this, so the first sample is always "no change" and never a trigger.
        private const int NoSample = int.MinValue;

        [Header("Voices (index = vocal_kind)")]
        [Tooltip("0 = reveal scream, 1 = search shriek, 2 = noise grunt, 3 = stalking breath. " +
                 "One clip is picked at random from the bank for that kind. An empty bank is silent.")]
        [SerializeField] private VoiceBank[] _voices = new VoiceBank[KindCount];

        [Header("Falloff")]
        [Tooltip("ADR-094 Enmienda 10. Off, distance is only a volume change, which reads as a " +
                 "near sound turned down rather than as a far one — the flatness the 2026-08-25 " +
                 "play-test reported. On, the voice also loses treble and widens with distance, " +
                 "the way air and a corridor actually treat a sound. OFF by default so the " +
                 "robapieles, whose mix is already signed off, does not change under anyone.")]
        [SerializeField] private bool _distanceColour;

        [Tooltip("How wide the source reads at maximum distance, in degrees. Well short of 360 " +
                 "on purpose: the pack has to stay LOCATABLE.")]
        [SerializeField, Range(0f, 120f)] private float _maxSpread = 45f;

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

        [Header("The distant answer (kind 4)")]
        [Tooltip("Full-volume radius for the answer roar (m). Huge, because this sound's whole job " +
                 "is to arrive from where a rifle shot arrives from.")]
        [SerializeField, Min(1f)] private float _answerMinDistance = 70f;

        [Tooltip("Hard cutoff for the answer roar (m). Matched to the rifle's own audible range " +
                 "(NoiseReporter: 500 m). It still ENDS — but out here, that end is far away.")]
        [SerializeField, Min(1f)] private float _answerMaxDistance = 500f;

        [Tooltip("Pitch multiplier for the answer roar. Deep: low frequencies are what actually " +
                 "survive distance, and it is also what makes the thing sound enormous.")]
        [SerializeField, Range(0.4f, 1f)] private float _answerPitch = 0.7f;

        [Tooltip("Volume multiplier for the answer roar. The clips are mastered with headroom for it.")]
        [SerializeField, Range(1f, 3f)] private float _answerVolume = 1.7f;

        [System.Serializable]
        private struct VoiceBank
        {
            public AudioClip[] Clips;

            /// <summary>
            /// ADR-094 Enmienda 9 — OPTIONAL per-kind falloff. Left off (the default), a bank
            /// plays on whichever of the three shared curves above the creature's state selects,
            /// which is what every robapieles voice has always done and still does.
            ///
            /// The facelings need something the robapieles never did: the SAME creature making
            /// sounds that belong at completely different ranges. Its distant chant has to carry
            /// across the floor and its whisper must die a few metres away, and no single curve
            /// can be both. A per-kind override is the smallest way to say that without giving
            /// the two species different hooks — an override left empty costs one bool test.
            /// </summary>
            public bool OverrideRange;
            public float MinDistance;
            public float MaxDistance;
            public float Volume;
            public float Pitch;
        }

        private RemotePlayerManager _manager;
        private AudioSource _source;
        private int _lastSeq = NoSample;
        private RangeMode _rangeMode = RangeMode.Normal;
        private float _kindMin, _kindMax;

        // Re-arm for pool reuse: a recycled proxy must not scream for its previous occupant, and
        // must not inherit its counter either.
        private void OnEnable()
        {
            _lastSeq = NoSample;
            _rangeMode = RangeMode.Normal;
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

            var src = EnsureSource();

            float pitch = _pitchVariation > 0f
                ? 1f + Random.Range(-_pitchVariation, _pitchVariation)
                : 1f;

            // ADR-094 Enmienda 9 — a bank carrying its own range wins outright, and deliberately
            // does not consult `revealed`: the kinds that use this are a faceling's, and a
            // faceling has no disguise to drop. Checked before the reveal lookup so the override
            // path costs nothing extra.
            var bank = _voices[kind];
            if (bank.OverrideRange)
            {
                ApplyExplicitRange(bank.MinDistance, bank.MaxDistance);
                src.pitch = pitch * (bank.Pitch > 0f ? bank.Pitch : 1f);
                src.PlayOneShot(clip, bank.Volume > 0f ? bank.Volume : 1f);
                return;
            }

            // Revealed, it carries further, hits harder and sits lower. Resolved at PLAY time, which
            // is rare — the per-frame path above never asks.
            bool revealed = ResolveRevealed();

            // The answer roar ignores `revealed` entirely: it is emitted BY a creature that is still
            // wearing a stolen face, from far enough away that you will never see which player it
            // came out of. That is the whole scare — something answered your gunshot, and any of the
            // figures out there could have been it.
            if (kind == KindDistantAnswer)
            {
                ApplyRange(RangeMode.Answer);
                src.pitch = pitch * _answerPitch;
                src.PlayOneShot(clip, _answerVolume);
                return;
            }

            ApplyRange(revealed ? RangeMode.Revealed : RangeMode.Normal);
            src.pitch = revealed ? pitch * _revealedPitch : pitch;
            src.PlayOneShot(clip, revealed ? _revealedVolume : 1f);
        }

        private enum RangeMode { Normal, Revealed, Answer, PerKind }

        /// <summary>
        /// ADR-094 Enmienda 9 — applies a bank's own curve. Unlike <see cref="ApplyRange"/> this
        /// cannot skip on an unchanged mode, because two different kinds both land on
        /// <c>PerKind</c> with different numbers; it compares the numbers instead.
        /// </summary>
        private void ApplyExplicitRange(float min, float max)
        {
            if (_rangeMode == RangeMode.PerKind && _source != null
                && Mathf.Approximately(_kindMin, min) && Mathf.Approximately(_kindMax, max))
                return;

            _rangeMode = RangeMode.PerKind;
            _kindMin = min;
            _kindMax = max;
            ProxyAudioCurves.ApplyHardCutoff(_source, min, max);
            ApplyDistanceColour(max);
        }

        /// <summary>
        /// ADR-094 Enmienda 10 — re-derives the air-absorption cutoff for the range now in force.
        ///
        /// It has to follow the range rather than be set once, because the same source plays
        /// banks that carry eleven metres and banks that carry eighty, and the amount of air a
        /// sound crosses is the whole input to how dark it should get.
        /// </summary>
        private void ApplyDistanceColour(float maxDistance)
        {
            if (!_distanceColour || _source == null)
                return;

            ProxyAudioCurves.ApplyDistanceColour(_source, maxDistance, _maxSpread);
        }

        /// <summary>Swap the distance curve, and only on a change — see ProxyFootstepHook.</summary>
        private void ApplyRange(RangeMode mode)
        {
            if (_rangeMode == mode && _source != null)
                return;
            _rangeMode = mode;

            float min = _minDistance, max = _maxDistance;
            if (mode == RangeMode.Revealed) { min = _revealedMinDistance; max = _revealedMaxDistance; }
            else if (mode == RangeMode.Answer) { min = _answerMinDistance; max = _answerMaxDistance; }

            ProxyAudioCurves.ApplyHardCutoff(_source, min, max);
            ApplyDistanceColour(max);
        }

        /// <summary>Is this proxy showing its real form? Own lookup, so the hook stays removable.</summary>
        private bool ResolveRevealed()
        {
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return false;

            return view.revealed;
        }

        private AudioSource EnsureSource()
        {
            if (_source != null)
                return _source;

            // Parented to the proxy so the voice tracks the thing that made it: a cry that hangs
            // where the creature WAS points the player at empty corridor.
            _source = ProxyAudioSourceFactory.CreateHardCutoffSource(transform, "PhantomVoice",
                _minDistance, _maxDistance);
            return _source;
        }

        /// <summary>This proxy's networked voice fields, via the view whose root is this GameObject.</summary>
        private bool TryResolveVoice(out int seq, out int kind)
        {
            seq = 0;
            kind = 0;

            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return false;

            seq = view.vocalSeq;
            kind = view.vocalKind;
            return true;
        }
    }
}
