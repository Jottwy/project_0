using System.Collections.Generic;
using BackroomsSurvival.Gameplay; // MaterialHelper
using BackroomsSurvival.Net;
using PolymindGames; // AudioManager / AudioChannel
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-038: swaps this proxy's skinned materials for a "real form" look while its networked
    /// <c>revealed</c> flag is true — the robapieles (ADR-016) breaking out of its stolen skin as it
    /// freezes (STATUE) or lunges (SPRINT). For every real player the flag is permanently false, so
    /// this hook costs one comparison per frame and never touches a renderer.
    ///
    /// V1 WAS A MATERIAL SWAP, NOT A MESH SWAP. The avatar's mesh is skinned to the vendor
    /// MTP_PlayerViewer skeleton; a separately rigged humanoid has a different bone hierarchy, so
    /// swapping the mesh here would deform it. Keeping the same renderers means the reveal costs no
    /// animation: the phantom keeps running with the proxy's own clips while it looks wrong.
    ///
    /// V2 SWAPS THE WHOLE BODY, for exactly the reason V1 could not swap the mesh: the real-form
    /// asset carries its OWN rig, so it arrives as a separate body (baked as an inactive child by
    /// RemoteAvatarPrefabBuilder.WireRealForm) with its OWN Animator. Reveal hides the vendor
    /// renderers and activates it. V1 survives as the fallback when no body is wired — delete the
    /// child and the material swap is still there.
    ///
    /// HIDING USES forceRenderingOff, NOT enabled. ProxyClothingHook owns the renderers' enabled
    /// flags (a hat not worn is a disabled renderer); toggling them here would restore the phantom
    /// wearing every garment in the wardrobe. forceRenderingOff is orthogonal and nothing else in
    /// the project writes it. Known gap: a renderer INSTANTIATED while revealed (ProxyHeldItemHook
    /// swapping the held weapon mid-lunge) is not in the captured set and stays visible.
    ///
    /// IT ANIMATES LIKE A PLAYER, because it plays the player's own clips. The proxy's clip set is
    /// HUMANOID (MaleSurvivor and every clip FBX import as Human), and Humanoid clips are muscle
    /// curves rather than bone paths, so they retarget onto any valid Humanoid avatar. The real form
    /// is rigged as Humanoid and runs the SAME ProxyLocomotionController; this hook only mirrors the
    /// locomotion FLOATS across, so idle/walk/run/crouch come for free and there is no second
    /// velocity estimator, no second controller and no duplicated clips to drift.
    ///
    /// THE SCREAM RIDES THE SAME EDGE. The backend raises `revealed` on entering SPRINT, so the
    /// disguise dropping and the lunge starting are one instant: a one-shot on that transition is the
    /// attack sound, with no attack event and nothing added to the wire.
    ///
    /// REVERSIBLE by construction: <c>revealed</c> is a derived LEVEL (not a latch) — the backend
    /// drops it back to false when the phantom returns to WANDER/STALK, and this hook restores the
    /// cached originals. No sentinel is needed (unlike ProxyHitReactionHook): a freshly instantiated
    /// proxy already wears its original materials, which is exactly what <c>_lastRevealed=false</c>
    /// means, so the first sample can only trigger a change if the phantom really is revealed.
    ///
    /// The flag is read from the RemotePlayerManager view whose root is this GameObject — same
    /// lookup as ProxyCrouchHook/ProxyPickupHook. Attach to the avatar root. Removable: delete the
    /// file and the phantom still reveals on the wire, it just never looks different.
    /// </summary>
    public sealed class ProxyRevealHook : MonoBehaviour
    {
        [Header("Real form (V2 — body swap)")]
        [Tooltip("Inactive child holding the real-form body and its own Animator. Baked by " +
                 "RemoteAvatarPrefabBuilder. Left empty, the hook falls back to the V1 material swap.")]
        [SerializeField] private GameObject _realFormBody;

        [Header("Scream")]
        [Tooltip("One is picked at random when the disguise drops. Empty ⇒ the reveal is silent.")]
        [SerializeField] private AudioClip[] _screamClips;

        [Tooltip("Metres of full volume around the phantom before the scream starts falling off.")]
        [SerializeField, Min(0f)] private float _screamMinDistance = 4f;

        [Tooltip("Hard cutoff (m). A shriek must carry much further than a footstep, but it must " +
                 "still END — an audible tail at 200 m turns a scare into a map-wide tracker.")]
        [SerializeField, Min(1f)] private float _screamMaxDistance = 45f;

        [Tooltip("Random pitch spread per scream, so the same three clips never read as three clips.")]
        [SerializeField, Range(0f, 0.4f)] private float _screamPitchVariation = 0.14f;

        [Header("Real form (V1 — material swap fallback)")]
        [Tooltip("Material worn while revealed. Left empty, a pale skinless stand-in is generated " +
                 "at runtime so the hook is visible before any material is authored.")]
        [SerializeField] private Material _realFormMaterial;

        // Pale yellow, no features — the canonical skinless look, used when nothing is authored.
        private static readonly Color FallbackColor = new Color(0.86f, 0.83f, 0.55f, 1f);

        private RemotePlayerManager _manager;
        private SkinnedMeshRenderer[] _renderers;
        private Material[][] _originalMaterials;
        private Material _runtimeFallback;
        private bool _lastRevealed;

        private Animator _vendorAnimator;
        private Animator _bodyAnimator;
        private int _hashMovementSpeed;
        private int _hashCrouched;
        private Renderer[] _hidden;
        private AudioSource _screamSource;

        private void Awake()
        {
            // Cache the renderers AND their material arrays once. Renderers can carry several
            // materials (body + clothing submeshes), so the originals are stored per renderer.
            _renderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _originalMaterials = new Material[_renderers.Length][];
            for (int i = 0; i < _renderers.Length; i++)
                _originalMaterials[i] = _renderers[i].sharedMaterials;

            _vendorAnimator = GetComponent<Animator>();
            _hashMovementSpeed = Animator.StringToHash("MovementSpeed");
            _hashCrouched = Animator.StringToHash("Crouched");
            if (_realFormBody != null)
                _bodyAnimator = _realFormBody.GetComponentInChildren<Animator>(true);
        }

        // Re-arm for pool reuse: a recycled proxy must never start wearing the real form, nor keep a
        // previous occupant's scream ringing.
        private void OnEnable()
        {
            if (_lastRevealed)
                Restore();
            _lastRevealed = false;
            if (_realFormBody != null)
                _realFormBody.SetActive(false);
            if (_screamSource != null)
                _screamSource.Stop();
        }

        private void Update()
        {
            bool revealed = ResolveRevealed();
            if (revealed != _lastRevealed)
            {
                _lastRevealed = revealed;
                if (revealed)
                    Apply();
                else
                    Restore();
            }

            // Only a revealed phantom pays for this; every real player stops at the comparison above.
            if (revealed)
                MirrorLocomotion();
        }

        /// <summary>
        /// Copies the disguise Animator's locomotion parameters onto the real form's, so the creature
        /// walks, runs and idles off the SAME signals the stolen skin does. The clips are Humanoid, so
        /// the player's own clip set retargets onto the creature's rig with nothing else to author.
        ///
        /// Floats only. Jump/Pickup are TRIGGERS and a trigger cannot be read back off an Animator, so
        /// a revealed phantom does not jump or mime a pickup — declared, and no loss: both belong to
        /// the disguise, and the reveal is over in a lunge.
        /// </summary>
        private void MirrorLocomotion()
        {
            if (_bodyAnimator == null || _vendorAnimator == null)
                return;

            _bodyAnimator.SetFloat(_hashMovementSpeed, _vendorAnimator.GetFloat(_hashMovementSpeed));
            _bodyAnimator.SetFloat(_hashCrouched, _vendorAnimator.GetFloat(_hashCrouched));
        }

        private void Apply()
        {
            if (_realFormBody != null)
            {
                ApplyBody();
                return;
            }

            ApplyMaterial();
        }

        // Capture the renderers at reveal time rather than at Awake: anything the held-item hook
        // instantiated since spawn is a real part of the disguise and has to disappear with it.
        private void ApplyBody()
        {
            _hidden = CollectDisguiseRenderers();
            for (int i = 0; i < _hidden.Length; i++)
                _hidden[i].forceRenderingOff = true;

            _realFormBody.SetActive(true);
            MirrorLocomotion(); // seed the pose in the same frame, so it never appears mid-idle
            Scream();
        }

        private void RestoreBody()
        {
            if (_hidden != null)
            {
                for (int i = 0; i < _hidden.Length; i++)
                {
                    if (_hidden[i] != null)
                        _hidden[i].forceRenderingOff = false;
                }
                _hidden = null;
            }

            if (_realFormBody != null)
                _realFormBody.SetActive(false);
        }

        /// <summary>Every renderer of the stolen skin — the whole proxy except the real-form body.</summary>
        private Renderer[] CollectDisguiseRenderers()
        {
            var all = GetComponentsInChildren<Renderer>(true);
            var kept = new List<Renderer>(all.Length);
            var bodyRoot = _realFormBody != null ? _realFormBody.transform : null;
            foreach (var r in all)
            {
                if (r == null)
                    continue;
                if (bodyRoot != null && r.transform.IsChildOf(bodyRoot))
                    continue;
                kept.Add(r);
            }
            return kept.ToArray();
        }

        /// <summary>
        /// One-shot on the reveal itself. The backend raises <c>revealed</c> the moment the phantom
        /// enters SPRINT, so "the disguise drops" and "it lunges at you" are the same instant — the
        /// scream needs no attack event of its own and costs nothing on the wire.
        ///
        /// Its own AudioSource, created lazily, with the SAME hard-cutoff curve the rest of the proxy
        /// audio uses (ADR-042): Unity's default logarithmic rolloff never truly reaches zero, and a
        /// scream audible from anywhere stops being a scare and becomes a radar.
        /// </summary>
        private void Scream()
        {
            if (_screamClips == null || _screamClips.Length == 0)
                return;

            var clip = _screamClips[Random.Range(0, _screamClips.Length)];
            if (clip == null)
                return;

            var src = EnsureScreamSource();
            src.pitch = _screamPitchVariation > 0f
                ? 1f + Random.Range(-_screamPitchVariation, _screamPitchVariation)
                : 1f;
            src.PlayOneShot(clip);
        }

        private AudioSource EnsureScreamSource()
        {
            if (_screamSource != null)
                return _screamSource;

            // Parented to the proxy so the scream tracks the thing that made it: a shriek that hangs
            // where the lunge STARTED points the player at empty corridor.
            var go = new GameObject("PhantomScream");
            go.transform.SetParent(transform, false);

            _screamSource = go.AddComponent<AudioSource>();
            _screamSource.playOnAwake = false;
            _screamSource.loop = false;
            _screamSource.spatialBlend = 1f; // at 0 it would be dead-centre at any distance
            _screamSource.dopplerLevel = 0f;
            ProxyAudioCurves.ApplyHardCutoff(_screamSource, _screamMinDistance, _screamMaxDistance);

            // No manager (a bare test scene) → still audible, just unmixed. Never an exception.
            var audio = AudioManager.Instance;
            _screamSource.outputAudioMixerGroup = audio != null ? audio.GetMixerGroup(AudioChannel.Sfx) : null;
            return _screamSource;
        }

        private void ApplyMaterial()
        {
            var mat = _realFormMaterial != null ? _realFormMaterial : ResolveFallback();
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null)
                    continue;
                // One entry per submesh: assigning a shorter array would leave stale slots.
                var swapped = new Material[_originalMaterials[i].Length];
                for (int s = 0; s < swapped.Length; s++)
                    swapped[s] = mat;
                r.sharedMaterials = swapped;
            }
        }

        private void Restore()
        {
            if (_realFormBody != null || _hidden != null)
            {
                RestoreBody();
                return;
            }

            if (_renderers == null)
                return;
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r != null)
                    r.sharedMaterials = _originalMaterials[i];
            }
        }

        private Material ResolveFallback()
        {
            if (_runtimeFallback == null)
                _runtimeFallback = MaterialHelper.MakeLit(FallbackColor);
            return _runtimeFallback;
        }

        /// <summary>This proxy's networked reveal flag, via the RemotePlayerManager view whose root is us.</summary>
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
    }
}
