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
    /// and a proxy (MTP_PlayerViewer variant) has neither and never will. What IS reusable is the part
    /// that matters: <c>SurfaceManager.Instance.PlayEffectFromHit</c> is a plain static call, so the
    /// remote footstep goes through the exact same surface database, the same clips and the same
    /// volume curve as the local player's. Nothing inside PolymindGames is modified.
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

        [Header("Ground probe (mirrors FootstepsController.CheckGround)")]
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

        // Re-baseline for pool reuse and for the spawn snap: the first frame after enable only
        // captures the position, so a recycled proxy never spends a previous occupant's travel.
        private void OnEnable()
        {
            _hasPrevPos = false;
            _travelled = 0f;
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
            _prevPos = pos;

            // Teleport: discard the jump entirely. Adding it would fire several steps at once.
            if (planarDist > _teleportDistance)
            {
                _travelled = 0f;
                return;
            }

            float dt = Time.deltaTime;
            float speed = dt > 0f ? planarDist / dt : 0f;

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
        /// Probes the ground under the proxy and plays the surface's own footstep audio. Same probe
        /// shape, same effect types and same volume curve as <see cref="FootstepsController"/> — the
        /// point is that a remote step is indistinguishable from a local one, not merely similar.
        /// </summary>
        private void PlayStep(float speed, bool running)
        {
            var manager = SurfaceManager.Instance;
            if (manager == null)
                return; // no surface database in this scene → silence, never an exception

            if (!CheckGround(out RaycastHit hit))
                return; // airborne (jump, fall, gap in the floor): no contact, no sound

            float volume = Mathf.Clamp(speed, _minSpeedForVolume, _maxSpeedForVolume)
                           / Mathf.Max(0.01f, _maxSpeedForVolume);

            var effect = running ? SurfaceEffectType.RunFootstep : SurfaceEffectType.WalkFootstep;
            manager.PlayEffectFromHit(in hit, effect, SurfaceEffectPlayFlags.Audio, volume);
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
