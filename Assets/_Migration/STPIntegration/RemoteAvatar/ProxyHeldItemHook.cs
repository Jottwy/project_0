using System.Collections.Generic;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-023: shows the peer's held wieldable on their 3P proxy by attaching a model to the
    /// right-hand bone (<c>Hand.R</c>) driven by the networked <c>view.heldItem</c> item ID. Unlike
    /// clothing (ADR-022, a pre-placed wardrobe), the held item is DYNAMIC, so this is new content:
    /// we resolve the item's world <see cref="ItemPickup"/> prefab by id, instantiate its mesh under
    /// the hand, and neutralize the pickup's physics/interaction components (renderers only).
    ///
    /// Slice 2 adds per-CATEGORY grip fidelity: the held item's FPSCore category (de-prefixed
    /// <c>ParentGroup.Name</c>: "Melee"/"Firearms"/"Tools", + fallback) selects a <see cref="GripPoseSet.CategoryGrip"/>
    /// that (a) places the model under Hand.R and (b) curls the 15 right-hand finger bones into a
    /// static grip (parametric curl around a shared bend axis), applied in LateUpdate AFTER the
    /// Animator (same ordering as <see cref="ProxyPitchHook"/>). Placement + finger pose are re-applied
    /// every LateUpdate so editing the <see cref="GripPoseSet"/> asset calibrates live during Play.
    ///
    /// Bones are resolved BY NAME (the rig is GENERIC). LIMITATION: grip is RIGHT-HAND ONLY — two-handed
    /// items (rifle/bow/spear) leave Hand.L free. Reads the held id from the RemotePlayerManager view
    /// whose root is this GameObject (same lookup as <see cref="ProxyClothingHook"/>). Attach to the
    /// avatar root. Removable: delete the file and the proxy shows empty hands; nothing else breaks.
    /// </summary>
    public sealed class ProxyHeldItemHook : MonoBehaviour
    {
        [Header("Anchor")]
        [Tooltip("Hand bone name on the MaleSurvivor skeleton to attach the held model to.")]
        [SerializeField] private string _handBoneName = "Hand.R";

        [Header("Grip configuration (per-category placement + finger pose)")]
        [Tooltip("Per-category grip poses. Calibrate the asset live during Play.")]
        [SerializeField] private GripPoseSet _gripPoses;

        // Right-hand finger bones of the MaleSurvivor skeleton, grouped by the two curl scalars.
        private static readonly string[] ThumbBoneNames =
        {
            "ThumbFinger.1.R", "ThumbFinger.2.R", "ThumbFinger.3.R",
        };
        private static readonly string[] FingerBoneNames =
        {
            "IndexFinger.1.R", "IndexFinger.2.R", "IndexFinger.3.R",
            "MiddleFinger.1.R", "MiddleFinger.2.R", "MiddleFinger.3.R",
            "RingFinger.1.R", "RingFinger.2.R", "RingFinger.3.R",
            "PinkyFinger.1.R", "PinkyFinger.2.R", "PinkyFinger.3.R",
        };

        // Sentinel that never equals a real item ID (0 = empty hands is a valid value), so the
        // first observed value always applies — including a peer holding nothing (0).
        private const int Unset = int.MinValue;

        private RemotePlayerManager _manager;
        private Transform _hand;
        private GameObject _instance;
        private int _applied = Unset;
        private string _category = "";
        // ADR-049: the carry hook owns the peer's hands while there are planks in them.
        private ProxyCarryHook _carryHook;

        private Transform[] _thumbBones;
        private Transform[] _fingerBones;
        private Quaternion[] _thumbBind;
        private Quaternion[] _fingerBind;

        private void Awake()
        {
            var bones = BuildBoneMap();
            bones.TryGetValue(_handBoneName, out _hand);
            _carryHook = GetComponent<ProxyCarryHook>();
            CacheFingerChain(bones, ThumbBoneNames, out _thumbBones, out _thumbBind);
            CacheFingerChain(bones, FingerBoneNames, out _fingerBones, out _fingerBind);
        }

        // Re-arm for pool reuse: drop any held model and force a re-apply against the fresh peer.
        private void OnEnable()
        {
            ClearInstance();
            _applied = Unset;
            _category = "";
        }

        // Change-detect the held id; (re)build the model + resolve its category on change.
        private void Update()
        {
            if (_hand == null)
                return;

            // ADR-049: planks win. A peer cannot be shouldering a stack of drywall and presenting a
            // rifle at the same time, and the carryable's own controller blocks the equivalent
            // locally. `_applied` is reset rather than kept so the held model rebuilds the moment the
            // planks are put down. Update order between the two hooks is undefined, so a single frame
            // of overlap is possible on the transition — both re-evaluate every frame and it closes
            // itself; anything tighter would mean coupling their execution order for one frame.
            if (_carryHook != null && _carryHook.IsCarrying)
            {
                ClearInstance();
                _category = "";
                _applied = Unset;
                return;
            }

            if (!TryResolveHeld(out int id) || id == _applied)
                return;

            ClearInstance();
            _category = "";
            if (id != 0)
            {
                var def = DataDefinition<ItemDefinition>.GetWithId(id);
                if (def != null && def.ParentGroup != null)
                    _category = def.ParentGroup.Name; // de-prefixed: "Melee"/"Firearms"/"Tools"
                _instance = BuildHeldModel(def);
            }
            _applied = id;
        }

        // Apply per-category placement + finger grip AFTER the Animator. Re-applied every frame so
        // GripPoseSet edits calibrate live; only runs while an item is held (empty hands → the
        // locomotion clip drives the hand naturally).
        private void LateUpdate()
        {
            if (_instance == null)
                return;

            var grip = _gripPoses != null ? _gripPoses.Resolve(_category) : null;

            var t = _instance.transform;
            t.localPosition = grip != null ? grip.modelLocalPosition : Vector3.zero;
            t.localRotation = Quaternion.Euler(grip != null ? grip.modelLocalEuler : Vector3.zero);
            t.localScale = grip != null ? SafeScale(grip.modelLocalScale) : Vector3.one;

            Vector3 axis = _gripPoses != null ? _gripPoses.fingerBendAxis : Vector3.forward;
            float sign = (_gripPoses != null && _gripPoses.invertBend) ? -1f : 1f;
            ApplyCurl(_fingerBones, _fingerBind, (grip != null ? grip.fingerCurl : 0f) * sign, axis);
            ApplyCurl(_thumbBones, _thumbBind, (grip != null ? grip.thumbCurl : 0f) * sign, axis);
        }

        /// <summary>Resolves the item's pickup mesh, neutralizes it, and parents it to the hand.</summary>
        private GameObject BuildHeldModel(ItemDefinition def)
        {
            var pickup = def != null ? def.Pickup : null;
            if (pickup == null)
                return null; // unknown item / no world model → empty hands (graceful)

            var go = Instantiate(pickup.gameObject, _hand, false);
            NeutralizeToVisualOnly(go);
            SetLayerRecursive(go, _hand.gameObject.layer);
            return go; // placement is applied in LateUpdate (live-calibratable)
        }

        // Strip every non-visual component so the pickup becomes an inert prop: no physics, no
        // interaction, no pooling. MeshFilter/MeshRenderer/LODGroup are not MonoBehaviours, so
        // they survive; the static mesh (with LODs) is exactly what we want to render.
        private static void NeutralizeToVisualOnly(GameObject go)
        {
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                Destroy(rb);
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                Destroy(col);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                Destroy(mb);
        }

        private static void ApplyCurl(Transform[] bones, Quaternion[] bind, float curlDeg, Vector3 axis)
        {
            if (bones == null)
                return;
            var q = Quaternion.AngleAxis(curlDeg, axis);
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null)
                    bones[i].localRotation = bind[i] * q;
            }
        }

        private static Vector3 SafeScale(Vector3 s)
        {
            // A zero/uninitialized scale would hide the model; fall back to unit scale.
            return (s.x == 0f && s.y == 0f && s.z == 0f) ? Vector3.one : s;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursive(child.gameObject, layer);
        }

        private void ClearInstance()
        {
            if (_instance != null)
                Destroy(_instance);
            _instance = null;
        }

        // Single traversal → name→Transform map (the finger/hand bones are resolved off this).
        private Dictionary<string, Transform> BuildBoneMap()
        {
            var map = new Dictionary<string, Transform>(64);
            foreach (var tr in GetComponentsInChildren<Transform>(true))
                map[tr.name] = tr; // last-wins is fine; bone names are unique on this rig
            return map;
        }

        private static void CacheFingerChain(Dictionary<string, Transform> bones, string[] names,
            out Transform[] resolved, out Quaternion[] bind)
        {
            resolved = new Transform[names.Length];
            bind = new Quaternion[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                bones.TryGetValue(names[i], out var b);
                resolved[i] = b;
                bind[i] = b != null ? b.localRotation : Quaternion.identity; // bind pose at Awake
            }
        }

        /// <summary>This proxy's networked held item ID, via the RemotePlayerManager view whose root is us.</summary>
        private bool TryResolveHeld(out int heldItem)
        {
            heldItem = 0;
            if (_manager == null)
                _manager = GetComponentInParent<RemotePlayerManager>();
            if (_manager == null)
                return false;

            foreach (var kvp in _manager.ActivePlayers)
            {
                var view = kvp.Value;
                if (view != null && view.root == transform)
                {
                    heldItem = view.heldItem;
                    return true;
                }
            }
            return false;
        }
    }
}
