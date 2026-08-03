using System.Collections.Generic;
using BackroomsSurvival.Net;
using PolymindGames.WieldableSystem;
using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-049: shows the planks a peer is hauling on their 3P proxy, by stacking
    /// <c>view.carryCount</c> copies of <c>view.carryDef</c>'s pickup model on the left-hand bone.
    ///
    /// LEFT hand, and that is not a coin flip: <c>WieldableCarrySettings.TargetSocket</c> is
    /// <c>LeftHand</c> on the carryable definitions, and <c>Hand.R</c> is already spoken for by
    /// <see cref="ProxyHeldItemHook"/>. The two are mutually exclusive anyway — that hook stands down
    /// while this one has planks, because a peer cannot hold a rifle and a stack of drywall at once.
    ///
    /// A LEVEL hook, not an event one. Carrying is a sustained state, so there is no counter and no
    /// delta: the visuals are rebuilt whenever the (definition, count) pair changes and left alone
    /// otherwise. A dropped datagram costs nothing — the next one restores the truth.
    ///
    /// It also POSES the arm (see <see cref="ApplyCarryPose"/>), because the models alone are not
    /// enough: hanging off a hand driven by the locomotion blend tree, a walking peer swings a stack
    /// of drywall like empty hands.
    ///
    /// It shares <c>UpperArm.L</c> / <c>LowerArm.L</c> with <see cref="ProxyStanceHook"/>, and that is
    /// safe rather than lucky: both apply a DELTA to whatever rotation they find and both write
    /// nothing at all at weight 0. A peer carrying planks is not aiming or reloading anyway, so in
    /// practice only one is ever above zero — and if they did overlap the poses would add instead of
    /// one erasing the other.
    ///
    /// Removable by design: delete the file and peers carry invisibly again. Nothing else breaks.
    /// </summary>
    public sealed class ProxyCarryHook : MonoBehaviour
    {
        [Header("Anchor")]
        [Tooltip("Hand bone on the MaleSurvivor skeleton to stack the carried planks on. Left, " +
                 "matching WieldableCarrySettings.TargetSocket on the carryable definitions.")]
        [SerializeField] private string _handBoneName = "Hand.L";

        [Header("Placement")]
        [Tooltip("Per-slot placement of the stack. Calibrate the asset live during Play.")]
        [SerializeField] private CarryPoseSet _carryPoses;

        [Tooltip("Hard cap on instantiated models, whatever the wire says. A corrupt or hostile " +
                 "carry_count must cost one clamp, not an unbounded Instantiate loop on every peer.")]
        [SerializeField, Range(1, 16)] private int _maxModels = 8;

        [Header("Carry pose (ADR-049 tanda B)")]
        [Tooltip("Degrees the left upper arm raises to hold the stack against the shoulder.")]
        [SerializeField, Min(0f)] private float _armRaise = 42f;

        [Tooltip("Degrees the left forearm folds up under the stack.")]
        [SerializeField, Min(0f)] private float _forearmFold = 55f;

        [Tooltip("Seconds to blend the pose in or out. A snap reads as a teleporting arm.")]
        [SerializeField, Min(0.01f)] private float _blendTime = 0.2f;

        [Tooltip("Flip the rotation direction. The axis sign is a property of the rig, not of the " +
                 "maths; calibrate in play-test exactly like ProxyStanceHook and ProxyMeleeHook.")]
        [SerializeField] private bool _invert;

        // Sentinel that no real definition id can equal, so the FIRST observed value always applies —
        // including a peer carrying nothing. A late joiner has to see the planks that were already
        // there, which is exactly the case a "skip the first sample" guard would break.
        private const int Unset = int.MinValue;

        private RemotePlayerManager _manager;
        private Transform _hand;
        private Transform _upperArmL, _forearmL;
        private readonly List<GameObject> _instances = new();
        private int _appliedDef = Unset;
        private int _appliedCount = -1;
        private float _poseWeight;

        /// <summary>True while this proxy has planks in hand. Read by <see cref="ProxyHeldItemHook"/>
        /// so the two never render into the same pair of hands.</summary>
        public bool IsCarrying => _instances.Count > 0;

        private void Awake()
        {
            foreach (var tr in GetComponentsInChildren<Transform>(true))
            {
                if (tr.name == _handBoneName)
                    _hand = tr;
                else if (tr.name == "UpperArm.L")
                    _upperArmL = tr;
                else if (tr.name == "LowerArm.L")
                    _forearmL = tr;
            }
        }

        // Re-arm for pool reuse: drop the stack and force a rebuild against the fresh peer. Without
        // this a recycled proxy keeps the previous owner's planks until their counts happen to differ.
        private void OnEnable()
        {
            ClearInstances();
            _appliedDef = Unset;
            _appliedCount = -1;
            _poseWeight = 0f;
        }

        private void OnDisable() => ClearInstances();

        // Change-detect the (definition, count) pair and rebuild the stack when either moves.
        private void Update()
        {
            if (_hand == null)
                return;

            if (!TryResolveCarry(out int def, out int count))
                return;

            count = Mathf.Clamp(count, 0, _maxModels);
            if (def == _appliedDef && count == _appliedCount)
                return;

            ClearInstances();
            if (def != 0 && count > 0)
                BuildStack(def, count);

            _appliedDef = def;
            _appliedCount = count;
        }

        // AFTER the Animator, same ordering as every other bone hook here. Two jobs, and the pose one
        // must run even with no planks left, or the arm would stay locked up after they are dropped.
        private void LateUpdate()
        {
            PlaceStack();
            ApplyCarryPose();
        }

        /// <summary>
        /// ADR-049 tanda B: folds the left arm up under the stack so a walking peer stops swinging the
        /// planks around like empty hands.
        ///
        /// Procedural and not a clip, and that is forced rather than preferred: there is no
        /// third-person carry animation anywhere in the project, and the vendor's carry
        /// AnimatorOverrideController drives the FIRST-PERSON viewmodel — its clips are Generic and
        /// bound to FP_Arms, so there is nothing to retarget onto this rig. A Mixamo carry clip WOULD
        /// work (this rig is Humanoid, whatever the stale "GENERIC" comments on the sibling hooks
        /// say), but that is content the repo does not have.
        ///
        /// At weight 0 it touches NO bone. That is the discipline ProxyStanceHook set and it is what
        /// lets seventeen hooks share one skeleton: a hook that writes its rest pose every frame is
        /// indistinguishable from a hook that is fighting you.
        /// </summary>
        private void ApplyCarryPose()
        {
            if (_upperArmL == null && _forearmL == null)
                return;

            float step = Time.deltaTime / Mathf.Max(0.01f, _blendTime);
            _poseWeight = Mathf.MoveTowards(_poseWeight, _instances.Count > 0 ? 1f : 0f, step);
            if (_poseWeight <= 0f)
                return;

            float w = _poseWeight * (_invert ? -1f : 1f);
            var axis = transform.right;
            ApplyBend(_upperArmL, -_armRaise * w, axis);
            ApplyBend(_forearmL, -_forearmFold * w, axis);
        }

        private static void ApplyBend(Transform bone, float degrees, Vector3 axis)
        {
            if (bone == null || Mathf.Approximately(degrees, 0f))
                return;
            bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;
        }

        /// <summary>Placement of the stack, re-applied every frame so editing the CarryPoseSet
        /// calibrates live during Play.</summary>
        private void PlaceStack()
        {
            if (_instances.Count == 0)
                return;

            var scale = _carryPoses != null ? SafeScale(_carryPoses.modelLocalScale) : Vector3.one;
            for (int i = 0; i < _instances.Count; i++)
            {
                var instance = _instances[i];
                if (instance == null)
                    continue;

                var slot = _carryPoses != null ? _carryPoses.Resolve(i) : null;
                var t = instance.transform;
                t.localPosition = slot != null ? slot.localPosition : Vector3.zero;
                t.localRotation = Quaternion.Euler(slot != null ? slot.localEuler : Vector3.zero);
                t.localScale = scale;
            }
        }

        /// <summary>Resolves the carryable's pickup model and stacks <paramref name="count"/> of it.</summary>
        private void BuildStack(int def, int count)
        {
            var definition = CarryableDefinition.GetWithId(def);
            var pickup = definition != null ? definition.Pickup : null;
            if (pickup == null)
                return; // unknown definition (peer has an asset we do not) → empty hands, gracefully

            for (int i = 0; i < count; i++)
            {
                var go = Instantiate(pickup.gameObject, _hand, false);
                NeutralizeToVisualOnly(go);
                SetLayerRecursive(go, _hand.gameObject.layer);
                _instances.Add(go);
            }
        }

        /// <summary>
        /// Strips every non-visual component so a pickup becomes an inert prop: no physics, no
        /// interaction, no saveable identity. MeshFilter/MeshRenderer/LODGroup are not
        /// MonoBehaviours, so they survive — the static mesh is exactly what we want.
        ///
        /// Shadows are turned off BY HAND here, and that line is load-bearing. The local player's own
        /// planks get it from <c>CarryablePickup.OnPickUp</c>, but that component is one of the ones
        /// destroyed above, so on a proxy nothing would ever run it: every peer would drag four extra
        /// shadow casters that the local player does not pay for.
        /// </summary>
        private static void NeutralizeToVisualOnly(GameObject go)
        {
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                Destroy(rb);
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                Destroy(col);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                Destroy(mb);
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        private static Vector3 SafeScale(Vector3 s) =>
            (s.x == 0f && s.y == 0f && s.z == 0f) ? Vector3.one : s;

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursive(child.gameObject, layer);
        }

        private void ClearInstances()
        {
            for (int i = 0; i < _instances.Count; i++)
            {
                if (_instances[i] != null)
                    Destroy(_instances[i]);
            }

            _instances.Clear();
        }

        /// <summary>This proxy's networked carry state, via the RemotePlayerManager view whose root
        /// is us — same lookup as <see cref="ProxyHeldItemHook"/>.</summary>
        private bool TryResolveCarry(out int carryDef, out int carryCount)
        {
            carryDef = 0;
            carryCount = 0;
            if (_manager == null)
                _manager = GetComponentInParent<RemotePlayerManager>();
            if (_manager == null)
                return false;

            foreach (var kvp in _manager.ActivePlayers)
            {
                var view = kvp.Value;
                if (view != null && view.root == transform)
                {
                    carryDef = view.carryDef;
                    carryCount = view.carryCount;
                    return true;
                }
            }

            return false;
        }
    }
}
