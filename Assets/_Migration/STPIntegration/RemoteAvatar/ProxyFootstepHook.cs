using BackroomsSurvival.Net; // RemotePlayerManager — the reveal flag rides the pose relay
using PolymindGames;
using PolymindGames.SurfaceSystem;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-042: makes a remote player AUDIBLE when they walk or run. Unlike every other hook in this
    /// folder, this one reads NO networked field and costs NOTHING on the wire — the observer already
    /// has the peer's position at 10 Hz and, crucially, is standing in the SAME world, so its own
    /// raycast resolves the surface underfoot better than any relayed value could.
    ///
    /// Why the vendor's own component cannot be reused: <see cref="FootstepsController"/> is a
    /// <c>CharacterBehaviour</c> with <c>[RequireCharacterComponent(IMovementControllerCC, IMotorCC)]</c>,
    /// and a proxy (MTP_PlayerViewer variant) has neither and never will. What IS reusable is the
    /// surface database: <c>GetSurfaceFromHit</c> and <c>TryGetEffectPair</c> are public, so we resolve
    /// the EXACT clip the vendor would have played. Nothing inside PolymindGames is modified.
    ///
    /// PLAY-TEST FIX (2026-08-02): the audio does NOT go through <c>PlayEffectFromHit</c> any more.
    /// That route ends in <c>AudioManager.PlayClip3D</c>, whose pooled 3D sources pin
    /// <c>minDistance = 2</c> and leave <c>maxDistance</c> at Unity's default 500 — so a footstep still
    /// carried a few percent of its volume across half a chunk, and remote players were audible from
    /// absurdly far away. The volume was never the problem; the CUTOFF was. Retuning the pool was not
    /// an option (shared with impacts and every other surface effect), so, exactly like the gunshot
    /// hook, this one owns its source and its curve: a hard <c>maxDistance</c> that actually silences
    /// a step instead of thinning it forever.
    ///
    /// A tight cutoff is also what stealth NEEDS: footsteps have to be a SHORT-range cue, or crouching
    /// buys nothing.
    ///
    /// The step trigger is a DISTANCE accumulator, not an Animation Event. Events on the Mixamo clips
    /// would be higher fidelity (sound on the exact contact frame), but those clips are IMPORTED vendor
    /// assets: the events would live in the FBX .meta and a re-import or package update would silently
    /// delete them, leaving peers mute again with no failing test to say so. A distance accumulator
    /// lives in this file and cannot be deleted by an importer.
    ///
    /// Crouching needs no special case and gets none: a crouched peer moves slowly, and the vendor's
    /// own volume curve (speed clamped into a min..max band) already makes slow movement quiet. That
    /// is the same curve the local player uses, so sneaking sounds like sneaking for free.
    ///
    /// Teleport guard mirrors <see cref="ProxyLocomotionFeeder"/> exactly, and here it matters MORE:
    /// a chunk displacement moves the avatar many metres in one frame, and without the guard that
    /// distance would land in the accumulator and fire a burst of footsteps at a peer who never moved.
    ///
    /// Attach to the avatar root. Removable by design: delete the file and remote players go silent
    /// again; locomotion, jump, pickup, crouch, pitch, clothing, held item, flinch and reveal are all
    /// unaffected, and no networked field becomes dead (there is none).
    /// </summary>
    public sealed class ProxyFootstepHook : MonoBehaviour
    {
        [Header("Stride (metres of travel per footstep)")]
        [Tooltip("Stride at walking pace. Shorter than the run stride: walking takes more, closer steps per metre.")]
        [SerializeField, Min(0.1f)] private float _walkStride = 0.85f;

        [Tooltip("Stride at running pace.")]
        [SerializeField, Min(0.1f)] private float _runStride = 1.35f;

        [Header("Speed → gait (m/s, mirrors ProxyLocomotionFeeder)")]
        [Tooltip("Planar speed below this is standing still: no steps, and the accumulator bleeds off.")]
        [SerializeField, Min(0f)] private float _deadzoneSpeed = 0.1f;

        [Tooltip("Planar speed at or below which the gait is a walk (WalkFootstep clips).")]
        [SerializeField, Min(0f)] private float _walkSpeed = 1.5f;

        [Tooltip("Planar speed at or above which the gait is a run (RunFootstep clips).")]
        [SerializeField, Min(0f)] private float _runSpeed = 4.5f;

        [Header("Volume (mirrors FootstepsController)")]
        [Tooltip("Speed mapped to the quietest audible footstep.")]
        [SerializeField, Min(0f)] private float _minSpeedForVolume = 1f;

        [Tooltip("Speed mapped to a full-volume footstep. Above this the volume does not grow.")]
        [SerializeField, Min(0.01f)] private float _maxSpeedForVolume = 7f;

        [Header("Distance curve (the fix for 'peers audible from absurdly far')")]
        [Tooltip("Full-volume radius, in metres. Small on purpose: a footstep is an intimate sound.")]
        [SerializeField, Min(0.1f)] private float _minDistance = 1.5f;

        [Tooltip("HARD cutoff, in metres — beyond this a step is genuinely SILENT. Lower it if peers " +
                 "are still audible too far.")]
        [SerializeField, Min(1f)] private float _maxDistance = 22f;

        [Tooltip("Use a custom rolloff curve that actually reaches zero. Unity's Logarithmic mode does " +
                 "NOT silence at maxDistance — it stops attenuating there and holds that level for " +
                 "ever (minDistance/maxDistance, ~7 % here), which is why steps followed peers across " +
                 "the level. Turn off only to compare against the old behaviour.")]
        [SerializeField] private bool _hardCutoff = true;

        [Tooltip("Fallback rolloff when Hard Cutoff is off.")]
        [SerializeField] private AudioRolloffMode _rolloff = AudioRolloffMode.Logarithmic;

        [Header("Fall impact (ADR-044, mirrors FootstepsController.PlayFallImpactEffects)")]
        [Tooltip("Downward speed (m/s) below which a landing is silent — stepping off a kerb should " +
                 "not thud.")]
        [SerializeField, Min(0f)] private float _minFallImpactSpeed = 4f;

        [Tooltip("Downward speed mapped to a full-volume landing.")]
        [SerializeField, Min(0.01f)] private float _maxFallImpactSpeed = 11f;

        [Tooltip("Vertical speed at which the proxy counts as airborne (falling).")]
        [SerializeField, Min(0f)] private float _airborneSpeed = 2f;

        [Tooltip("Single-frame VERTICAL jump (m) treated as a teleport — a layer change or a respawn " +
                 "must never be heard as a landing from orbit.")]
        [SerializeField, Min(0f)] private float _verticalTeleportDistance = 2.5f;

        [Header("Ground probe (mirrors FootstepsController.CheckGround)")]
        [Header("Revealed creature (ADR-038 / ADR-048)")]
        [Tooltip("Footfalls used INSTEAD of the surface's own while this proxy is revealed. Empty ⇒ " +
                 "it keeps the human steps and only the range and volume below change.")]
        [SerializeField] private AudioClip[] _revealedSteps;

        [Tooltip("Volume multiplier while revealed. It is heavier than you and it is not hiding any more.")]
        [SerializeField, Range(1f, 4f)] private float _revealedVolume = 2.1f;

        [Tooltip("Full-volume radius while revealed (m). Wider than a human's, so the sound arrives " +
                 "before the thing does.")]
        [SerializeField, Min(0.1f)] private float _revealedMinDistance = 6f;

        [Tooltip("Hard cutoff while revealed (m). MUST still end: a footfall audible across the map " +
                 "stops being dread and becomes a tracker — the exact failure ADR-042 cost a playtest for.")]
        [SerializeField, Min(1f)] private float _revealedMaxDistance = 55f;

        [Tooltip("Random pitch spread per heavy step, so four clips do not read as four clips.")]
        [SerializeField, Range(0f, 0.3f)] private float _revealedPitchVariation = 0.09f;

        [SerializeField] private LayerMask _layerMask = LayerConstants.SimpleSolidObjectsMask;
        [SerializeField, Range(0.01f, 1f)] private float _raycastDistance = 0.3f;
        [SerializeField, Range(0.01f, 0.5f)] private float _raycastRadius = 0.3f;

        [Header("Teleport guard")]
        [Tooltip("Single-frame XZ jump (m) above which the move is a teleport (chunk displacement / " +
                 "respawn): the distance is discarded instead of being spent on footsteps.")]
        [SerializeField, Min(0f)] private float _teleportDistance = 2.0f;

        private Vector3 _prevPos;
        private bool _hasPrevPos;
        private float _travelled;
        private AudioSource _source;
        private RemotePlayerManager _manager;
        // Tri-state via the source being null: forces the first ApplyRange to actually apply.
        private bool _rangeIsRevealed;
        private bool _airborne;
        private float _peakFallSpeed;

        // Re-baseline for pool reuse and for the spawn snap: the first frame after enable only
        // captures the position, so a recycled proxy never spends a previous occupant's travel.
        private void OnEnable()
        {
            _hasPrevPos = false;
            _travelled = 0f;
            _airborne = false;
            _peakFallSpeed = 0f;
        }

        private void LateUpdate()
        {
            Vector3 pos = transform.position;

            if (!_hasPrevPos)
            {
                _prevPos = pos;
                _hasPrevPos = true;
                return;
            }

            Vector3 delta = pos - _prevPos;
            float planarDist = new Vector3(delta.x, 0f, delta.z).magnitude;
            float verticalDelta = delta.y;
            _prevPos = pos;

            // Teleport: discard the jump entirely. Adding it would fire several steps at once.
            if (planarDist > _teleportDistance)
            {
                _travelled = 0f;
                _airborne = false;
                _peakFallSpeed = 0f;
                return;
            }

            float dt = Time.deltaTime;
            float speed = dt > 0f ? planarDist / dt : 0f;

            // ADR-044: landing runs BEFORE the standing-still gate on purpose. A peer dropping
            // straight down has a planar speed of ~0, so anything behind that gate would never see
            // the landing at all — the impact would be silent precisely when the fall was vertical.
            TrackFall(verticalDelta, dt);

            // Standing still: drop the partial stride so a peer who stops and starts again does not
            // land a step the instant they move.
            if (speed <= _deadzoneSpeed)
            {
                _travelled = 0f;
                return;
            }

            bool running = speed >= _runSpeed;
            float stride = Mathf.Max(0.1f, running ? _runStride : _walkStride);

            _travelled += planarDist;
            if (_travelled < stride)
                return;

            // One step per crossing, not one per whole stride consumed: at a very low framerate a
            // single frame can cover several strides, and firing that many clips at once reads as a
            // stutter rather than as running.
            _travelled -= stride;
            PlayStep(speed, running);
        }

        /// <summary>
        /// ADR-044: reconstructs the peer's fall from the proxy's vertical motion and plays the
        /// surface's own landing thud, mirroring <c>FootstepsController.PlayFallImpactEffects</c>
        /// (same thresholds, same volume ramp, same surface database).
        ///
        /// The peak downward speed is REMEMBERED across the fall rather than read at the moment of
        /// contact: the frame where the proxy touches down has already been decelerated by the
        /// interpolation in RemotePlayerManager, so sampling only at impact would report a gentle
        /// landing for every drop, however long.
        /// </summary>
        private void TrackFall(float verticalDelta, float dt)
        {
            // A layer change or respawn moves the avatar metres in one frame. Never a fall.
            if (Mathf.Abs(verticalDelta) > _verticalTeleportDistance)
            {
                _airborne = false;
                _peakFallSpeed = 0f;
                return;
            }

            float vy = dt > 0f ? verticalDelta / dt : 0f;

            if (vy < -_airborneSpeed)
            {
                _airborne = true;
                _peakFallSpeed = Mathf.Max(_peakFallSpeed, -vy);
                return;
            }

            if (!_airborne)
                return;

            // Descent stopped → contact.
            _airborne = false;
            float impact = _peakFallSpeed;
            _peakFallSpeed = 0f;

            if (impact < _minFallImpactSpeed)
                return; // a small hop is not a thud

            if (!CheckGround(out RaycastHit hit))
                return; // stopped mid-air (ledge grab, network jitter): no contact, no sound

            var manager = SurfaceManager.Instance;
            if (manager == null)
                return;

            var surface = manager.GetSurfaceFromHit(in hit);
            if (surface == null || !surface.TryGetEffectPair(SurfaceEffectType.FallImpact, out var data))
                return;

            var resource = data.AudioEffect.Clip;
            if (resource == null)
                return;

            // Vendor's own ramp: the span between the two thresholds is the normalising range.
            float volume = Mathf.Min(1f,
                impact / Mathf.Max(0.01f, _maxFallImpactSpeed - _minFallImpactSpeed));
            volume *= data.AudioEffect.Volume;

            var src = EnsureSource();
            if (resource is AudioClip clip)
                src.PlayOneShot(clip, volume);
            else
            {
                src.resource = resource;
                src.volume = volume;
                src.Play();
            }
        }

        /// <summary>
        /// Probes the ground under the proxy, resolves the SAME clip the vendor would play for that
        /// surface, and plays it on this proxy's own source — same probe shape, same surface database,
        /// same effect types and same volume curve as <see cref="FootstepsController"/>, but with a
        /// distance curve we control.
        /// </summary>
        private void PlayStep(float speed, bool running)
        {
            // ── The revealed creature does not walk like a person ──────────────────────────────
            // Resolved HERE and not per frame: a step is a rare event, and the view lookup is a
            // dictionary walk. Every real player takes the cheap branch below forever.
            bool revealed = ResolveRevealed();
            ApplyRange(revealed);

            if (revealed && _revealedSteps != null && _revealedSteps.Length > 0)
            {
                if (!CheckGround(out _))
                    return; // still needs contact: it must not thud through the air

                var heavy = _revealedSteps[Random.Range(0, _revealedSteps.Length)];
                if (heavy == null)
                    return;

                // Deliberately NOT surface-derived. The point is that this thing sounds the same
                // whatever it is walking on — it is not wearing the floor's shoes, and a footfall
                // that changes with the carpet reads as a player, which is exactly the tell the
                // reveal is supposed to have dropped.
                float heavyVol = Mathf.Clamp(speed, _minSpeedForVolume, _maxSpeedForVolume)
                                 / Mathf.Max(0.01f, _maxSpeedForVolume);
                var heavySrc = EnsureSource();
                heavySrc.pitch = 1f + Random.Range(-_revealedPitchVariation, _revealedPitchVariation);
                // NOT clamped to 1: the clips are mastered at ~0.55 peak precisely so this multiply
                // has headroom. Clamping here would have silently cancelled the whole boost at run
                // speed, where `heavyVol` is already 1.
                heavySrc.PlayOneShot(heavy, heavyVol * _revealedVolume);
                return;
            }

            var manager = SurfaceManager.Instance;
            if (manager == null)
                return; // no surface database in this scene → silence, never an exception

            if (!CheckGround(out RaycastHit hit))
                return; // airborne (jump, fall, gap in the floor): no contact, no sound

            var surface = manager.GetSurfaceFromHit(in hit);
            var effect = running ? SurfaceEffectType.RunFootstep : SurfaceEffectType.WalkFootstep;
            if (surface == null || !surface.TryGetEffectPair(effect, out var effectData))
                return; // this surface has no footstep authored → silence

            var resource = effectData.AudioEffect.Clip;
            if (resource == null)
                return;

            float volume = Mathf.Clamp(speed, _minSpeedForVolume, _maxSpeedForVolume)
                           / Mathf.Max(0.01f, _maxSpeedForVolume);
            volume *= effectData.AudioEffect.Volume; // keep the surface's own authored level

            var src = EnsureSource();
            if (resource is AudioClip clip)
            {
                // PlayOneShot so a fast run does not cut its own previous step short.
                src.PlayOneShot(clip, volume);
            }
            else
            {
                // AudioRandomContainer (Unity 6) and friends: only the resource path can play them.
                src.resource = resource;
                src.volume = volume;
                src.Play();
            }
        }

        /// <summary>Builds this proxy's own footstep source, lazily — a peer who never moves never
        /// allocates one. Parented to the proxy so steps track the walker.</summary>
        /// <summary>
        /// This proxy's networked reveal flag, via the RemotePlayerManager view whose root is us.
        /// Same lookup shape as ProxyRevealHook and ProxyVocalHook — each hook resolves its own so
        /// that any of them can be deleted without breaking the others (they are removable by
        /// design), and none of them is on a per-frame path here anyway.
        /// </summary>
        private bool ResolveRevealed()
        {
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return false;

            return view.revealed;
        }

        /// <summary>
        /// Swap the distance curve between human and creature ranges, and ONLY on a change.
        /// Re-applying an AnimationCurve every step would allocate for nothing, and the source is
        /// shared with the fall-impact path so it has to be correct there too.
        /// </summary>
        private void ApplyRange(bool revealed)
        {
            if (_rangeIsRevealed == revealed && _source != null)
                return;

            _rangeIsRevealed = revealed;
            var src = EnsureSource();
            float min = revealed ? _revealedMinDistance : _minDistance;
            float max = revealed ? _revealedMaxDistance : _maxDistance;

            ProxyAudioSourceFactory.ApplyRolloff(src, _hardCutoff, _rolloff, min, max);
            if (!revealed)
                src.pitch = 1f; // the disguise never pitches its steps
        }

        private AudioSource EnsureSource()
        {
            if (_source != null)
                return _source;

            _source = ProxyAudioSourceFactory.CreateChildSource(transform, "ProxyFootstep");
            ProxyAudioSourceFactory.ApplyRolloff(_source, _hardCutoff, _rolloff,
                _minDistance, _maxDistance);
            ProxyAudioSourceFactory.RouteToSfx(_source);
            return _source;
        }

        // Raycast first, spherecast as fallback — the vendor's own order: the cheap probe handles
        // flat ground, and the sphere catches edges where a proxy stands half off a step.
        private bool CheckGround(out RaycastHit hit)
        {
            var ray = new Ray(transform.position + Vector3.up * 0.3f, Vector3.down);
            bool hitSomething = Physics.Raycast(ray, out hit, _raycastDistance, _layerMask,
                QueryTriggerInteraction.Ignore);

            if (!hitSomething)
            {
                hitSomething = Physics.SphereCast(ray, _raycastRadius, out hit, _raycastDistance,
                    _layerMask, QueryTriggerInteraction.Ignore);
            }

            return hitSomething;
        }
    }
}
