using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-042: makes a peer's lit wieldable actually illuminate the room around them, driven by the
    /// networked <c>view.lightOn</c> flag.
    ///
    /// Why new content is unavoidable here. The torch's light lives ONLY on the first-person wieldable
    /// prefab (<c>STP_Wieldable_WoodenTorch</c>, via <c>LightEffect</c>), and <see cref="ProxyHeldItemHook"/>
    /// instantiates the item's PICKUP prefab, which has zero <c>Light</c> components. So a peer holding
    /// a torch today carries an unlit stick. Instantiating the first-person prefab instead was rejected
    /// in the ADR: it drags arms, motion handlers and a camera rig behind it. We create one Light and
    /// switch it.
    ///
    /// <c>held_item</c> (ADR-023) deliberately does NOT drive this. It says WHAT is in the hand, not
    /// whether it burns — the torch is toggleable (its prefab wires a UnityEvent to
    /// <c>WieldableEffectsController.ToggleEffects</c>), so the level genuinely has to travel.
    ///
    /// SHADOWS ARE OFF AND MUST STAY OFF (the ADR prohibits them): N peers with torches would be N
    /// shadow-casting lights in URP forward, which is the short road to dropping the frame. The light
    /// is also created ONCE and only toggled, never destroyed and rebuilt on every change.
    ///
    /// PARENTING: the light hangs off the hand bone, not off the held model. The model is rebuilt by
    /// ProxyHeldItemHook whenever the held id changes, and a child of it would be destroyed with it —
    /// leaving this hook holding a dangling reference. The bone outlives every item swap. Bone is
    /// resolved BY NAME (the rig is Generic), same as ProxyHeldItemHook/ProxyPitchHook; if the bone is
    /// missing the hook is a silent no-op.
    ///
    /// No sentinel is needed (unlike ProxyHitReactionHook): the light starts disabled and
    /// <c>_applied = false</c> describes that truthfully, so a freshly pooled proxy is already in the
    /// state the field claims. Only an EDGE touches the Light; a peer with no torch costs one bool
    /// comparison per frame.
    ///
    /// Removable: delete the file and peers stop casting light; nothing else changes.
    /// </summary>
    public sealed class ProxyLightHook : MonoBehaviour
    {
        [Header("Anchor")]
        [Tooltip("Hand bone the light is parented to. The bone outlives held-item swaps; the model does not.")]
        [SerializeField] private string _handBoneName = "Hand.R";

        [Tooltip("Local offset from the hand bone, roughly where a torch head sits.")]
        [SerializeField] private Vector3 _localOffset = new Vector3(0f, 0.15f, 0.1f);

        [Header("Light (shadows are OFF by ADR-042 and must stay off)")]
        [SerializeField] private Color _color = new Color(1f, 0.72f, 0.36f);
        [SerializeField, Min(0f)] private float _intensity = 2.5f;
        [SerializeField, Min(0f)] private float _range = 12f;

        private RemotePlayerManager _manager;
        private Transform _hand;
        private Light _light;
        private bool _applied;

        private void Awake()
        {
            _hand = FindBone(_handBoneName);
        }

        // Re-arm for pool reuse: a recycled proxy must not inherit the previous occupant's glow.
        private void OnEnable()
        {
            _applied = false;
            if (_light != null)
                _light.enabled = false;
        }

        private void Update()
        {
            if (_hand == null)
                return;

            if (!TryResolveLightOn(out bool on) || on == _applied)
                return;

            _applied = on;

            if (on && _light == null)
                _light = CreateLight();

            if (_light != null)
                _light.enabled = on;
        }

        /// <summary>Creates the single Light instance, lazily — a peer that never lights anything
        /// never allocates one.</summary>
        private Light CreateLight()
        {
            var go = new GameObject("ProxyHeldLight");
            go.transform.SetParent(_hand, false);
            go.transform.localPosition = _localOffset;

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = _color;
            light.intensity = _intensity;
            light.range = _range;
            light.shadows = LightShadows.None; // ADR-042: never turn this on, see the class doc
            light.enabled = false;
            return light;
        }

        /// <summary>This proxy's networked light flag, via the RemotePlayerManager view whose root is
        /// us — the same lookup as ProxyCrouchHook / ProxyHeldItemHook, so RemotePlayerManager needs
        /// no knowledge of this hook.</summary>
        private bool TryResolveLightOn(out bool lightOn)
        {
            lightOn = false;
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return false;

            lightOn = view.lightOn;
            return true;
        }

        private Transform FindBone(string boneName) => ProxyRigUtil.FindBone(transform, boneName);
    }
}
