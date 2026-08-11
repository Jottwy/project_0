using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.MovementSystem;
using PolymindGames.WieldableSystem;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Enmienda a ADR-065 — the LOCAL player's held light casts real shadows. Exactly one light in
    /// the whole scene is ever promoted, and it is always the one the player is carrying.
    ///
    /// WHY THIS EXISTS AS AN EXTERNAL HOOK. The torch's Light is not on the wieldable prefab: it is
    /// the <c>FireLight</c> child of the NESTED prefab <c>FPS_VFX_SmallFire</c>, which
    /// <c>STP_Wieldable_WoodenTorch</c> instances (hence no <c>!u!108</c> in the wieldable's own
    /// YAML — only `stripped` records). That same VFX prefab is shared by the campfire, the standing
    /// torch and the Marlin's muzzle flash, so editing it would hand shadows to every fire in the
    /// game at once — N shadow-casting lights, precisely what ADR-065 and ADR-050 forbid. It is also
    /// vendor content: a `.unitypackage` reimport would silently revert it. We correct from outside
    /// instead, per the project's standing rule on PolymindGames assets.
    ///
    /// WHAT STAYS SHADOWLESS, and it is not incidental — it is the whole reason the amendment is
    /// safe: worldgen lamps (<c>BackroomsLighting</c>) and peer torches (<c>ProxyLightHook</c>,
    /// ADR-042) both hardcode <c>LightShadows.None</c> at creation and are never touched here. So do
    /// placed campfires and standing torches — this hook only ever looks at the ACTIVE WIELDABLE.
    /// Turning `m_AdditionalLightShadowsSupported` on in `PC_RPAsset` makes shadows POSSIBLE; this
    /// file is the only thing in the project that makes one HAPPEN.
    ///
    /// ITEM-AGNOSTIC, same philosophy as <c>PlayerPoseTransmitter.ReadLightOn</c> (ADR-042): we never
    /// ask "is this the torch?", we take the strongest Light that is actually emitting under the
    /// equipped wieldable. A lantern, a flare or a flashlight works the day it exists, with no table.
    ///
    /// NOT A PER-FRAME COST. <c>LightEffect</c> writes intensity/range/color every frame but never
    /// touches <c>shadows</c>, so the promotion survives untouched; and it drives
    /// <c>Light.enabled</c>, so an unlit torch renders no shadow map without this hook doing anything.
    /// Only an EDGE (wieldable swap, equip, rig rebuild) writes to a Light.
    ///
    /// Self-bootstraps on its own DontDestroyOnLoad object (mirrors <c>PlayerPoseTransmitter</c> /
    /// <c>PhantomAttackHandler</c>): no scene wiring, and immune to the STP rig rebuild. Fully
    /// removable — delete the file and the torch goes back to lighting without casting.
    /// </summary>
    public sealed class TorchShadowCaster : MonoBehaviour
    {
        private static TorchShadowCaster _instance;

        // Read-only diagnostics mirror (no behavioural coupling), same convention as PlayerPoseTransmitter.
        public static bool IsCasting { get; private set; }

        // Equip/swap is a deliberate human action; re-resolving 4x a second is well inside the
        // reaction window and keeps the hierarchy walk off the frame budget.
        private const float ResolveInterval = 0.25f;

        // ── Shadow tuning ────────────────────────────────────────────────────────────────────────
        // Tier High = 1024 px per cube face (see PC_RPAsset). A point light needs SIX faces, and URP
        // clamps the granted resolution to what the atlas can tile: with the old 2048 atlas the
        // ceiling was 2048/3 ≈ 682 px and the request would have been silently downgraded, which is
        // why the atlas moved to 4096 in the same change.
        private const LightShadowResolution ShadowRes = LightShadowResolution.High;
        // Not 1.0: a torch is an area source with real bounce around it, and pitch-black cores read
        // as a bug rather than as darkness.
        private const float ShadowStrength = 0.9f;
        // Custom bias is FORCED, not a preference. The pipeline defaults (depth 0.1 / normal 0.5)
        // are tuned for the 50 m directional cascade; applied to a 7.5 m flame they peter-pan the
        // shadows clean off their casters. These are scaled for the near field.
        private const float ShadowDepthBias = 0.05f;
        private const float ShadowNormalBias = 0.2f;
        // The flame sits ~0.3 m from the hand and can be brought right up to a wall; a large near
        // plane clips the very geometry the player is looking at.
        private const float ShadowNearPlane = 0.05f;

        private CharacterControllerMotor _motor;
        private ICharacter _character;
        private IWieldablesControllerCC _wieldablesController;

        // Light cache keyed on the wieldable REFERENCE (ADR-042's pattern): an equip / holster /
        // rig rebuild re-scans, and nothing per-frame walks the hierarchy.
        private Object _lightsCachedFor;
        private Light[] _wieldableLights = System.Array.Empty<Light>();

        // The single promoted light, plus the prefab value we overwrote. We restore rather than
        // assume None: the source is vendor content and its serialized value is not ours to predict.
        private Light _promoted;
        private LightShadows _shadowsBefore;
        private LightShadowResolution _resolutionBefore;
        private float _strengthBefore, _depthBiasBefore, _normalBiasBefore, _nearPlaneBefore;
        private bool _pipelineSettingsBefore;

        private float _accum;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[TorchShadowCaster]");
            _instance = go.AddComponent<TorchShadowCaster>();
            DontDestroyOnLoad(go);
        }

        // Hard singleton: two instances would fight over which light is promoted and could leave a
        // demoted-by-one / promoted-by-other light casting after the torch is gone.
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        private void Update()
        {
            _accum += Time.unscaledDeltaTime;
            if (_accum < ResolveInterval)
                return;
            _accum = 0f;

            var target = ResolveEmittingLight();

            // Still the same live light: nothing to write.
            if (_promoted != null && _promoted == target)
                return;

            // Only restore a light that is still alive. A DESTROYED one reads null through Unity's
            // overloaded ==, and writing to it would throw MissingReferenceException.
            if (_promoted != null)
                Restore(_promoted);
            _promoted = null;
            IsCasting = false;

            if (target != null)
                Promote(target);
        }

        // Leaving a promoted vendor light casting after this hook dies would be a shadow nobody owns.
        private void OnDestroy()
        {
            if (_instance != this)
                return;

            if (_promoted != null)
                Restore(_promoted);
            _promoted = null;
            IsCasting = false;
            _instance = null;
        }

        /// <summary>
        /// The Light under the ACTIVE wieldable that is actually emitting, or null.
        ///
        /// Selection is by RANGE, with the incumbent winning ties. Range because a wieldable can
        /// carry more than one Light (a firearm's muzzle flash is one) and the sustained source is
        /// always the widest. Incumbent-wins because <c>LightEffect</c> ramps `range` from 0 during
        /// its fade-in, so a plain max would flap between candidates for the length of the fade and
        /// rewrite shadow state every tick.
        /// </summary>
        private Light ResolveEmittingLight()
        {
            // Re-resolve LIVE (lesson 2 of PlayerPoseTransmitter): the STP rig is rebuilt at runtime,
            // which destroys the cached motor; Unity's == reports it null and we re-find.
            if (_motor == null)
            {
                _motor = LocalPlayerLocator.Find<CharacterControllerMotor>();
                _character = null;
                _wieldablesController = null;
            }
            if (_motor == null)
                return null;

            if (_character == null)
                _character = _motor.GetComponentInParent<ICharacter>();
            if (_wieldablesController == null && _character != null)
                _wieldablesController = _character.GetCC<IWieldablesControllerCC>();
            if (_wieldablesController == null)
                return null;

            // A DESTROYED wieldable still hands back a non-null interface reference (C# `?.` does not
            // see Unity's fake-null), and touching `.gameObject` on it throws. Test the Unity
            // lifetime first — NullWieldable is not a UnityEngine.Object, so it falls through to the
            // null-gameObject branch. Verbatim from PlayerPoseTransmitter.ReadLightOn.
            var wieldable = _wieldablesController.ActiveWieldable;
            bool destroyed = wieldable is Object uo && uo == null;
            var go = (wieldable == null || destroyed) ? null : wieldable.gameObject;
            if (go == null)
            {
                _lightsCachedFor = null;
                _wieldableLights = System.Array.Empty<Light>();
                return null;
            }

            if (!ReferenceEquals(_lightsCachedFor, go))
            {
                _lightsCachedFor = go;
                // `true` includes INACTIVE children on purpose (a flame object can start disabled),
                // which is why the loop below also has to test activeInHierarchy.
                _wieldableLights = go.GetComponentsInChildren<Light>(true);
            }

            Light best = null;
            float bestRange = -1f;
            for (int i = 0; i < _wieldableLights.Length; i++)
            {
                var l = _wieldableLights[i];
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy)
                    continue;

                if (l == _promoted)
                    return l; // incumbent still emitting — no churn during LightEffect's fade

                if (l.range > bestRange)
                {
                    bestRange = l.range;
                    best = l;
                }
            }

            return best;
        }

        private void Promote(Light light)
        {
            _shadowsBefore = light.shadows;
            _resolutionBefore = light.shadowResolution;
            _strengthBefore = light.shadowStrength;
            _depthBiasBefore = light.shadowBias;
            _normalBiasBefore = light.shadowNormalBias;
            _nearPlaneBefore = light.shadowNearPlane;

            light.shadows = LightShadows.Soft;
            light.shadowResolution = ShadowRes;
            light.shadowStrength = ShadowStrength;
            light.shadowBias = ShadowDepthBias;
            light.shadowNormalBias = ShadowNormalBias;
            light.shadowNearPlane = ShadowNearPlane;

            // URP keeps per-light shadow data in a companion component and ADDS it if missing —
            // which it is here, since this Light was authored under Built-in.
            var data = light.GetUniversalAdditionalLightData();
            _pipelineSettingsBefore = data.usePipelineSettings;
            data.usePipelineSettings = false; // else the bias constants above are ignored
            data.softShadowQuality = SoftShadowQuality.High;

            _promoted = light;
            IsCasting = true;
        }

        private void Restore(Light light)
        {
            light.shadows = _shadowsBefore;
            light.shadowResolution = _resolutionBefore;
            light.shadowStrength = _strengthBefore;
            light.shadowBias = _depthBiasBefore;
            light.shadowNormalBias = _normalBiasBefore;
            light.shadowNearPlane = _nearPlaneBefore;

            if (light.TryGetComponent<UniversalAdditionalLightData>(out var data))
                data.usePipelineSettings = _pipelineSettingsBefore;
        }
    }
}
