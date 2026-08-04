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

        // ADR-051 point 7 — the skin-break burst. Built lazily on the first reveal and reused.
        private const int BreakParticleCount = 90;
        private const float BreakFlashTime = 0.22f;
        private const float BreakFlashIntensity = 6f;
        private bool _breakVfxBuilt;
        private ParticleSystem _breakParticles;
        private Light _breakFlash;
        private float _breakFlashLeft;

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

            // SAFETY NET, and it is not theoretical: a bake once serialised a null controller onto
            // the nested body and the revealed creature T-posed through a whole play-test while the
            // console filled with "Animator is not playing an AnimatorController". The body runs the
            // SAME controller as the disguise by design (that is the entire reason the reveal costs
            // no animation work), so borrowing it here makes the runtime immune to that bake.
            if (_bodyAnimator != null && _bodyAnimator.runtimeAnimatorController == null
                && _vendorAnimator != null)
            {
                _bodyAnimator.runtimeAnimatorController = _vendorAnimator.runtimeAnimatorController;
            }
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

            // Outside the `revealed` guard on purpose: the flash outlives the frame that started it
            // and has to keep decaying even if the creature re-dresses mid-fade.
            TickBreakFlash();
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
            BurstSkinBreak();
            Scream();
        }

        /// <summary>
        /// ADR-051 point 7 — THE SKIN DOES NOT SWAP, IT BREAKS. A burst of blood and a flash of
        /// light on the reveal edge, so the mesh substitution reads as something tearing its way
        /// out rather than as a model popping.
        ///
        /// Built in code and not from an authored prefab for the same reason `PhantomRealFormBuilder`
        /// generates its stand-in material: there is no VFX asset in the repo, and a hook that
        /// depends on one nobody made is a hook that silently does nothing. Everything here is
        /// created once, lazily, and reused — a reveal is not a moment to be allocating.
        /// </summary>
        private void BurstSkinBreak()
        {
            EnsureBreakVfx();
            if (_breakParticles != null)
            {
                // At the chest, not the pivot: the pivot is at the feet and the burst would come
                // out of the floor.
                _breakParticles.transform.position = transform.position + Vector3.up * 1.4f;
                _breakParticles.Emit(BreakParticleCount);
            }
            if (_breakFlash != null)
            {
                _breakFlash.transform.position = transform.position + Vector3.up * 1.4f;
                _breakFlashLeft = BreakFlashTime;
                _breakFlash.enabled = true;
            }
        }

        private void EnsureBreakVfx()
        {
            if (_breakVfxBuilt)
                return;
            _breakVfxBuilt = true;

            var go = new GameObject("SkinBreakVfx");
            go.transform.SetParent(transform, false);

            _breakParticles = go.AddComponent<ParticleSystem>();
            var main = _breakParticles.main;
            main.duration = 1f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 7f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.16f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.45f, 0.02f, 0.02f), new Color(0.75f, 0.06f, 0.04f));
            main.gravityModifier = 1.6f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = BreakParticleCount * 2;

            var emission = _breakParticles.emission;
            emission.enabled = false; // burst-only, driven by Emit()
            var shape = _breakParticles.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.25f;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                // Sprites-Default is always present at runtime and needs no asset in the repo.
                var shader = Shader.Find("Sprites/Default");
                if (shader != null)
                    renderer.material = new Material(shader);
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
            }

            var lightGo = new GameObject("SkinBreakFlash");
            lightGo.transform.SetParent(transform, false);
            _breakFlash = lightGo.AddComponent<Light>();
            _breakFlash.type = LightType.Point;
            _breakFlash.color = new Color(1f, 0.35f, 0.25f);
            _breakFlash.range = 9f;
            _breakFlash.intensity = 0f;
            _breakFlash.enabled = false;
        }

        /// <summary>Decays the flash. A constant light would turn every revealed creature into a
        /// lamp, which is the opposite of what the hard-cutoff audio curves exist to avoid.</summary>
        private void TickBreakFlash()
        {
            if (_breakFlash == null || _breakFlashLeft <= 0f)
                return;
            _breakFlashLeft -= Time.deltaTime;
            if (_breakFlashLeft <= 0f)
            {
                _breakFlash.intensity = 0f;
                _breakFlash.enabled = false;
                return;
            }
            _breakFlash.intensity = BreakFlashIntensity * (_breakFlashLeft / BreakFlashTime);
        }

        // ADR-048 point 6: the scream is now EMITTED by the backend as `vocal_kind = 0` and played
        // by ProxyVocalHook, so every client hears it at the same instant instead of each one
        // inferring it from its own reception of the `revealed` level. This hook keeps the VISUAL
        // and yields the AUDIO. The clips below stay as the fallback for a prefab that has not been
        // re-baked with the new hook yet — wire ProxyVocalHook and leave `_screamClips` empty.

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
            _screamSource = ProxyAudioSourceFactory.CreateHardCutoffSource(transform, "PhantomScream",
                _screamMinDistance, _screamMaxDistance);
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
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return false;

            return view.revealed;
        }
    }
}
