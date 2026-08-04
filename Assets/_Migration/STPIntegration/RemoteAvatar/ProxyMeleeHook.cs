using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.SurfaceSystem;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-044: shows and sounds a peer's melee swing, driven by the networked <c>view.meleeSeq</c>
    /// counter (bumped on the attacker's own rising edge of <c>MeleeWeapon.IsUsing</c>).
    ///
    /// This was the largest remaining hole: melee is a COMBAT action, and until now a peer burying an
    /// axe in someone swung nothing and made no sound at all. Everything else about them was already
    /// visible — clothes, held item, crouch, aim — which made the silence worse, not better: the
    /// avatar looked correct while doing nothing.
    ///
    /// The counter is read as a DELTA for the same reason as the gunshot (ADR-042): the pose relay
    /// runs at 10 Hz and a fast weapon can land two swings inside one interval, so a level flag would
    /// silently merge them. Unlike gunfire, the delta is NOT replayed as a burst — a human cannot
    /// swing four times in 100 ms, so a large delta means a network stall, and animating four swings
    /// at once would look like a glitch rather than like a flurry. One swing per sample.
    ///
    /// The visual is a procedural arm arc, not a clip: the proxy's Animator has no swing state, and
    /// adding one means authoring a layer on a vendor controller the builder rebuilds from scratch on
    /// every bake (see STATE.md on the 342 lines of fileID churn). Bones are resolved BY NAME because
    /// the rig is GENERIC, same as ProxyPitchHook/ProxyHitReactionHook; missing bone = silent no-op.
    ///
    /// Audio reuses the surface database the same way the footsteps do, so a swing sounds like the
    /// weapon it is without this hook owning any clip: <c>SurfaceEffectType.Slash</c> is the vendor's
    /// own type for a blade through the air.
    ///
    /// Removable: delete the file and peers swing invisibly again; nothing else changes.
    /// </summary>
    public sealed class ProxyMeleeHook : MonoBehaviour
    {
        [Header("Swing arc")]
        [Tooltip("Peak rotation (degrees) of the arm at the top of the swing.")]
        [SerializeField, Min(0f)] private float _magnitude = 70f;

        [Tooltip("Seconds the swing takes from start to rest.")]
        [SerializeField, Min(0.05f)] private float _duration = 0.35f;

        [Tooltip("Flip the arc direction. The right-axis sign depends on the Generic rig; calibrate " +
                 "in play-test, same as ProxyHitReactionHook's _invert.")]
        [SerializeField] private bool _invert;

        [Header("Audio")]
        [Tooltip("Volume of the whoosh. Resolved from the surface database, so no clip is owned here.")]
        [SerializeField, Range(0f, 1f)] private float _volume = 0.7f;

        [Tooltip("Full-volume radius, in metres.")]
        [SerializeField, Min(0.1f)] private float _minDistance = 3f;

        [Tooltip("HARD cutoff, in metres — beyond this a swing is SILENT. A whoosh is a close-quarters " +
                 "sound; hearing one across a room would be noise, not information.")]
        [SerializeField, Min(1f)] private float _maxDistance = 18f;

        // Sentinel: the first observed counter value arms without swinging, so a late-joiner does not
        // watch every peer replay a swing the moment their proxy spawns.
        private const int Unseen = int.MinValue;

        private RemotePlayerManager _manager;
        private Transform _upperArm;
        private Transform _forearm;
        private bool _hasRig;
        private int _lastSeen = Unseen;
        private float _progress = 1f; // 1 == at rest
        private AudioSource _source;

        private void Awake()
        {
            _upperArm = FindBone("UpperArm.R");
            _forearm = FindBone("LowerArm.R");
            _hasRig = _upperArm != null || _forearm != null;
        }

        // Re-arm for pool reuse: a recycled proxy must not swing on its first sample, nor inherit a
        // mid-swing pose from a previous occupant.
        private void OnEnable()
        {
            _lastSeen = Unseen;
            _progress = 1f;
            if (_source != null)
                _source.Stop();
        }

        private void LateUpdate()
        {
            int seq = ResolveMeleeSeq();
            if (_lastSeen == Unseen)
                _lastSeen = seq;              // first sample: arm, never swing
            else if (seq != _lastSeen)
            {
                _lastSeen = seq;
                _progress = 0f;               // one swing per sample, however large the delta
                PlayWhoosh();
            }

            if (!_hasRig || _progress >= 1f)
                return;                        // at rest: zero per-frame cost, never fights other hooks

            _progress = Mathf.Min(1f, _progress + Time.deltaTime / Mathf.Max(0.05f, _duration));

            // Fast out, slow back: a strike accelerates into the target and recovers gently. A
            // symmetric curve reads as a wave, not as a blow.
            float t = _progress;
            float arc = t < 0.35f
                ? Mathf.SmoothStep(0f, 1f, t / 0.35f)
                : Mathf.SmoothStep(1f, 0f, (t - 0.35f) / 0.65f);

            float degrees = _magnitude * arc * (_invert ? -1f : 1f);
            Vector3 axis = transform.right;   // yaw-oriented right of the avatar root

            // Split across both arm bones so the whole limb travels instead of the elbow snapping.
            ApplyBend(_upperArm, degrees * 0.6f, axis);
            ApplyBend(_forearm, degrees * 0.4f, axis);
        }

        /// <summary>Plays the vendor's own Slash effect for the surface under the peer — no clip is
        /// owned here, so a swing sounds like whatever the world says it should.</summary>
        private void PlayWhoosh()
        {
            var manager = SurfaceManager.Instance;
            if (manager == null)
                return;

            // The swing happens at the peer, so the surface underfoot is the right sample: it is what
            // the vendor uses for every other body-relative effect and it needs no extra probe.
            var ray = new Ray(transform.position + Vector3.up * 0.3f, Vector3.down);
            if (!Physics.Raycast(ray, out RaycastHit hit, 0.6f, ~0, QueryTriggerInteraction.Ignore))
                return;

            var surface = manager.GetSurfaceFromHit(in hit);
            if (surface == null || !surface.TryGetEffectPair(SurfaceEffectType.Slash, out var data))
                return;

            var resource = data.AudioEffect.Clip;
            if (resource == null)
                return;

            var src = EnsureSource();
            float volume = _volume * data.AudioEffect.Volume;
            if (resource is AudioClip clip)
                src.PlayOneShot(clip, volume);
            else
            {
                src.resource = resource;
                src.volume = volume;
                src.Play();
            }
        }

        private AudioSource EnsureSource()
        {
            if (_source != null)
                return _source;

            var go = new GameObject("ProxyMeleeWhoosh");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 1.2f, 0f); // chest height, where the arc is

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

        /// <summary>This proxy's networked swing counter, via the RemotePlayerManager view whose root
        /// is us — the same lookup as every other hook here.</summary>
        private int ResolveMeleeSeq()
        {
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return _lastSeen == Unseen ? 0 : _lastSeen;

            return view.meleeSeq;
        }

        private static void ApplyBend(Transform bone, float degrees, Vector3 axis)
        {
            if (bone == null || Mathf.Approximately(degrees, 0f))
                return;
            bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;
        }

        private Transform FindBone(string boneName)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == boneName)
                    return t;
            }
            return null;
        }
    }
}
