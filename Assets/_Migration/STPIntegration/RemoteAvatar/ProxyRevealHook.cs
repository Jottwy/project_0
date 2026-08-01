using BackroomsSurvival.Gameplay; // MaterialHelper
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-038: swaps this proxy's skinned materials for a "real form" look while its networked
    /// <c>revealed</c> flag is true — the robapieles (ADR-016) breaking out of its stolen skin as it
    /// freezes (STATUE) or lunges (SPRINT). For every real player the flag is permanently false, so
    /// this hook costs one comparison per frame and never touches a renderer.
    ///
    /// V1 IS A MATERIAL SWAP, NOT A MESH SWAP. The avatar's mesh is skinned to the vendor
    /// MTP_PlayerViewer skeleton; a separately rigged humanoid has a different bone hierarchy, so
    /// swapping the mesh here would deform it. Keeping the same renderers means the reveal costs no
    /// animation: the phantom keeps running with the proxy's own clips while it looks wrong.
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
        [Header("Real form")]
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

        private void Awake()
        {
            // Cache the renderers AND their material arrays once. Renderers can carry several
            // materials (body + clothing submeshes), so the originals are stored per renderer.
            _renderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _originalMaterials = new Material[_renderers.Length][];
            for (int i = 0; i < _renderers.Length; i++)
                _originalMaterials[i] = _renderers[i].sharedMaterials;
        }

        // Re-arm for pool reuse: a recycled proxy must never start wearing the real form.
        private void OnEnable()
        {
            if (_lastRevealed)
                Restore();
            _lastRevealed = false;
        }

        private void Update()
        {
            bool revealed = ResolveRevealed();
            if (revealed == _lastRevealed)
                return; // level unchanged — the common case for every real player, every frame

            _lastRevealed = revealed;
            if (revealed)
                Apply();
            else
                Restore();
        }

        private void Apply()
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
