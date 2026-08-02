using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-044: shows a peer's SUSTAINED weapon stance — aiming down sights and reloading — from the
    /// relayed <c>view.buttons</c> bitfield.
    ///
    /// Of the two, RELOADING is the one that matters. It is the single most valuable piece of
    /// tactical information one player can read off another: it says "vulnerable, right now". Aiming
    /// is the cheaper half — it mostly tells you where their attention is.
    ///
    /// Both are LEVELS, not events, which is why they ride bits and not a counter: they have a
    /// duration, and the observer needs to know the state at every moment rather than be told once.
    /// Bit meanings come from <see cref="RemoteButtons"/> — never re-declared here, so the writer and
    /// this reader cannot drift apart.
    ///
    /// Procedural poses rather than clips, same reasoning as ProxyMeleeHook: the proxy's Animator has
    /// no aim or reload state, and adding one means authoring a layer on a controller the builder
    /// rebuilds from scratch on every bake. Aiming raises and tucks the arms toward the sightline;
    /// reloading drops them and works the off hand. Bones are resolved BY NAME (GENERIC rig) and a
    /// missing bone is a silent no-op.
    ///
    /// Runs in LateUpdate AFTER the Animator, like every other pose hook here, and blends in and out
    /// over <see cref="_blendTime"/> so a state change reads as a movement instead of a snap.
    /// NO SENTINEL and none needed: these are levels, and a freshly pooled proxy starts at buttons 0,
    /// which is exactly "neither" — the honest state rather than an assumed one.
    ///
    /// Removable: delete the file and peers aim and reload invisibly; nothing else changes.
    /// </summary>
    public sealed class ProxyStanceHook : MonoBehaviour
    {
        [Header("Aiming pose")]
        [Tooltip("Degrees the upper arms rotate toward the sightline while aiming.")]
        [SerializeField, Min(0f)] private float _aimArmRaise = 38f;

        [Tooltip("Degrees the forearms tuck in while aiming.")]
        [SerializeField, Min(0f)] private float _aimForearmTuck = 22f;

        [Header("Reload pose")]
        [Tooltip("Degrees the weapon arm drops while reloading.")]
        [SerializeField, Min(0f)] private float _reloadArmDrop = 30f;

        [Tooltip("Degrees the OFF hand swings in to work the magazine.")]
        [SerializeField, Min(0f)] private float _reloadOffHandSwing = 45f;

        [Header("Blending")]
        [Tooltip("Seconds to blend a stance in or out. A snap reads as a teleporting arm.")]
        [SerializeField, Min(0.01f)] private float _blendTime = 0.18f;

        [Tooltip("Flip the rotation direction. The axis sign depends on the Generic rig; calibrate in " +
                 "play-test, same as ProxyHitReactionHook and ProxyMeleeHook.")]
        [SerializeField] private bool _invert;

        private RemotePlayerManager _manager;
        private Transform _upperArmR, _forearmR, _upperArmL, _forearmL;
        private bool _hasRig;
        private float _aimWeight;
        private float _reloadWeight;

        private void Awake()
        {
            _upperArmR = FindBone("UpperArm.R");
            _forearmR = FindBone("LowerArm.R");
            _upperArmL = FindBone("UpperArm.L");
            _forearmL = FindBone("LowerArm.L");
            _hasRig = _upperArmR != null || _upperArmL != null;
        }

        // Re-arm for pool reuse: a recycled proxy must not inherit a previous occupant's stance.
        private void OnEnable()
        {
            _aimWeight = 0f;
            _reloadWeight = 0f;
        }

        private void LateUpdate()
        {
            if (!_hasRig)
                return;

            int buttons = ResolveButtons();

            // Reloading WINS over aiming when both bits are set. They can legitimately overlap for a
            // frame or two around the transition, and the reload is the pose that carries the
            // information worth reading.
            bool reloading = RemoteButtons.Has(buttons, RemoteButtons.Reloading);
            bool aiming = !reloading && RemoteButtons.Has(buttons, RemoteButtons.Aiming);

            float step = Time.deltaTime / Mathf.Max(0.01f, _blendTime);
            _aimWeight = Mathf.MoveTowards(_aimWeight, aiming ? 1f : 0f, step);
            _reloadWeight = Mathf.MoveTowards(_reloadWeight, reloading ? 1f : 0f, step);

            if (_aimWeight <= 0f && _reloadWeight <= 0f)
                return; // fully at rest: never touches a bone, so it cannot fight the other hooks

            float sign = _invert ? -1f : 1f;
            Vector3 axis = transform.right;

            // Aim: both arms up and in, toward where the sights would be.
            if (_aimWeight > 0f)
            {
                float w = _aimWeight * sign;
                ApplyBend(_upperArmR, -_aimArmRaise * w, axis);
                ApplyBend(_upperArmL, -_aimArmRaise * w, axis);
                ApplyBend(_forearmR, -_aimForearmTuck * w, axis);
                ApplyBend(_forearmL, -_aimForearmTuck * w, axis);
            }

            // Reload: weapon arm drops, off hand comes across to the magazine.
            if (_reloadWeight > 0f)
            {
                float w = _reloadWeight * sign;
                ApplyBend(_upperArmR, _reloadArmDrop * w, axis);
                ApplyBend(_upperArmL, -_reloadOffHandSwing * w, axis);
                ApplyBend(_forearmL, -_reloadOffHandSwing * 0.5f * w, axis);
            }
        }

        /// <summary>This proxy's networked button bits, via the RemotePlayerManager view whose root is
        /// us — the same lookup as every other hook here.</summary>
        private int ResolveButtons()
        {
            if (_manager == null)
                _manager = GetComponentInParent<RemotePlayerManager>();
            if (_manager == null)
                return 0;

            foreach (var kvp in _manager.ActivePlayers)
            {
                var view = kvp.Value;
                if (view != null && view.root == transform)
                    return view.buttons;
            }
            return 0;
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
