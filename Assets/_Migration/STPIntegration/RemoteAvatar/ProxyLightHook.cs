using BackroomsSurvival.Gameplay;
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
    /// state the field claims. Only an EDGE touches the Light's enabled state; a peer with no torch
    /// costs one bool comparison per frame. The one per-frame write is the ADR-130 storey layer
    /// (<c>TorchShadowCaster.ApplyStoreyLayer</c>), and only while lit, and only when the peer
    /// crosses a storey — without it the light illuminates B3 alone, wherever the peer stands.
    ///
    /// Removable: delete the file and peers stop casting light; nothing else changes.
    ///
    /// PASO 2 DE LA LINTERNA (2026-09-14): cuando lo que hay en la mano es un objeto de agarre MEDIDO
    /// (<see cref="ProxyHeldItemHook.TryGetBeam"/>, hoy la linterna), la misma luz pasa a FOCO blanco y se coloca
    /// cada fotograma en la lente, apuntando por el eje del objeto. Antes el vecino con linterna llevaba el punto
    /// naranja de la antorcha pegado a la mano (captura del arnés). La luz sigue colgando del hueso —el modelo
    /// se reconstruye al cambiar de objeto— y sólo se mueve en mundo; sin sombras, como exige ADR-042. Corre
    /// después de ProxyHeldItemHook (<c>DefaultExecutionOrder</c>) para leer la lente ya colocada.
    /// </summary>
    [DefaultExecutionOrder(50)]
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

        [Header("Haz (linterna): los mismos números que el foco de primera persona")]
        [SerializeField] private Color _beamColor = new Color(0.93f, 0.95f, 1f);
        [SerializeField, Min(0f)] private float _beamIntensity = 4f;
        [SerializeField, Min(0f)] private float _beamRange = 18f;
        [SerializeField, Range(1f, 179f)] private float _beamSpotAngle = 55f;
        [SerializeField, Range(0f, 179f)] private float _beamInnerSpotAngle = 22f;

        private RemotePlayerManager _manager;
        private Transform _hand;
        private Light _light;
        private bool _applied;
        private ProxyHeldItemHook _held;
        private bool _isBeam;

        private void Awake()
        {
            _hand = FindBone(_handBoneName);
            _held = GetComponent<ProxyHeldItemHook>();
        }

        // Foco en la lente mientras la mano lleve un objeto medido; si no, la luz de antorcha de siempre.
        private void LateUpdate()
        {
            if (_light == null || !_light.enabled)
                return;

            if (_held != null && _held.TryGetBeam(out Vector3 lens, out Quaternion aim))
            {
                ConfigureAsBeam(true);
                _light.transform.SetPositionAndRotation(lens, aim);
            }
            else
            {
                ConfigureAsBeam(false);
                _light.transform.localPosition = _localOffset;
                _light.transform.localRotation = Quaternion.identity;
            }
        }

        private void ConfigureAsBeam(bool beam)
        {
            if (_isBeam == beam)
                return;
            _isBeam = beam;
            if (beam)
            {
                _light.type = LightType.Spot;
                _light.color = _beamColor;
                _light.intensity = _beamIntensity;
                _light.range = _beamRange;
                _light.spotAngle = _beamSpotAngle;
                _light.innerSpotAngle = _beamInnerSpotAngle;
            }
            else
            {
                _light.type = LightType.Point;
                _light.color = _color;
                _light.intensity = _intensity;
                _light.range = _range;
            }
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

            // ADR-130: la luz nace con la capa de render por defecto (bit 0), que con los tres
            // sótanos es B3 y ninguna otra planta — el peer alumbraría en el aire en la calle y en
            // B1/B2. Sigue a la cota del peer cada frame mientras está encendida; es un entero
            // comparado antes de escribirse (mismo criterio que TorchShadowCaster para el local).
            if (_applied && _light != null)
                TorchShadowCaster.ApplyStoreyLayer(_light);

            if (!TryResolveLightOn(out bool on) || on == _applied)
                return;

            _applied = on;

            if (on && _light == null)
                _light = CreateLight();

            if (_light != null)
            {
                _light.enabled = on;
                // El primer frame encendida también tiene que salir en su planta, no en B3.
                if (on)
                    TorchShadowCaster.ApplyStoreyLayer(_light);
            }
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
            _isBeam = false;
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
