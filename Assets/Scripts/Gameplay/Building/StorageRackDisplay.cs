using PolymindGames;
using PolymindGames.BuildingSystem;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// The visual half of the storage rack (FARMING-ROADMAP.md bloque E, tarea E3): whatever sits
    /// in a <see cref="StorageStation"/> slot is instantiated as an inert prop at that slot's shelf
    /// position, in real time, and removed the moment the slot empties — Joel's original idea for
    /// this piece ("que se vea placeado en tiempo real").
    ///
    /// Slot positions/rotations come from <see cref="_shelfAnchors"/> when authored — one child
    /// Transform per slot, seeded by <c>BackroomsBuildingPieceCreator.EnsureStorageRackDisplay</c>
    /// and freely draggable afterward in the Inspector/Scene view (position AND rotation — e.g. to
    /// stand a bottle upright instead of however its pickup prefab happens to rest by default).
    /// Editing an anchor is a normal prefab edit, no code round-trip.
    ///
    /// If a slot's anchor is missing or the array is short (a prefab authored before this existed,
    /// or a slot count that outgrew it), <see cref="AnchorLocalPosition"/> computes a fallback from
    /// 4 constants (the rack has 4 equal open tiers of 4 columns each, measured in E1 — same numbers
    /// as <c>BackroomsBuildingPieceCreator</c>'s `StorageRack*` constants, an editor-only class in a
    /// different assembly this one cannot reference, hence the duplication) so the piece never goes
    /// visually broken just because an anchor is unset.
    /// </summary>
    [RequireComponent(typeof(StorageStation))]
    public sealed class StorageRackDisplay : MonoBehaviour
    {
        [Tooltip("One Transform per container slot. Drag to reposition/reorient where that slot's " +
                 "item appears — empty or short entries fall back to a computed shelf position.")]
        [SerializeField]
        private Transform[] _shelfAnchors;

        private const int TierCount = 4;
        private const int SlotsPerTier = 4;
        private const float RackWidth = 1.4527f;
        private const float RackHeight = 1.9026f;

        // How far above a tier's floor/shelf board a placed item's pivot sits — most pickup meshes
        // are NOT authored with their pivot at their own base, so this is a flat estimate, not a
        // per-item measurement (see class doc). Only used as the FALLBACK, when no anchor is set.
        private const float ShelfClearance = 0.06f;

        // Almond Water's own pickup mesh rests with its long axis on Z (measured: 0.072 x 0.072 x
        // 0.230 m, Backrooms/Diagnostics/Measure Item Pickup) — correct for lying on the ground, not
        // for standing on a shelf. -90° on X maps that long axis onto world-up Y. This is tuned for
        // THAT item, not item-agnostic — a differently-shaped pickup (or this one re-exported) could
        // want a different correction, which is exactly what per-slot anchor rotation (dragged by
        // hand in the Inspector) is for; this is only the fallback default.
        private static readonly Quaternion FallbackUprightRotation = Quaternion.Euler(-90f, 0f, 0f);

        private IItemContainer _container;
        private GameObject[] _visuals;

        // Lazy-resolved on the first Update, not Start: StorageStation.GetContainers() reads
        // Workstation.Name, which reads a private _interactable field that Workstation.Start() only
        // assigns once its own Start() has run — script execution order between two components on
        // the same GameObject is NOT guaranteed, so resolving in Start() here could race it. Every
        // Start() in the scene has already run by the time ANY Update() runs (Unity's per-frame
        // Awake→Start→Update ordering), so this is race-free without needing
        // [DefaultExecutionOrder]. Same pattern as InventoryReporter.ResolveAndSubscribe.
        //
        // NOT enough by itself, though — found live in Joel's playtest: while the piece is still a
        // GHOST/placed-not-built preview, this component is already enabled and ticking (nothing
        // about BuildingPiece's placement lifecycle disables it), so it kept calling
        // GetContainers() every frame before StorageStation had ever gone through a real Start(),
        // spamming NullReferenceException from Workstation.Name. Gating on
        // BuildingPiece.IsConstructed fixes it at the source AND is the semantically correct
        // condition anyway — an unbuilt rack has no business tracking contents.
        private void Update()
        {
            if (_container != null)
                return;

            var piece = GetComponent<BuildingPiece>();
            if (piece == null || !piece.IsConstructed)
                return;

            var containers = GetComponent<StorageStation>().GetContainers();
            if (containers == null || containers.Count == 0)
                return;

            _container = containers[0];
            _visuals = new GameObject[_container.SlotsCount];
            _container.SlotChanged += OnSlotChanged;

            for (int i = 0; i < _container.SlotsCount; i++)
                RefreshSlot(i);
        }

        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType) => RefreshSlot(slot.Index);

        private void RefreshSlot(int index)
        {
            if (_visuals == null || index < 0 || index >= _visuals.Length)
                return;

            if (_visuals[index] != null)
            {
                Destroy(_visuals[index]);
                _visuals[index] = null;
            }

            var stack = _container.GetItemAtIndex(index);
            if (!stack.HasItem())
                return;

            var pickup = stack.Item.Definition.Pickup;
            if (pickup == null)
            {
                Debug.LogWarning($"[StorageRackDisplay] '{stack.Item.Definition.Name}' has no Pickup " +
                                  $"prefab assigned; slot {index} stays visually empty.");
                return;
            }

            var visual = Instantiate(pickup, transform);
            var anchor = _shelfAnchors != null && index < _shelfAnchors.Length ? _shelfAnchors[index] : null;
            if (anchor != null)
                visual.transform.SetLocalPositionAndRotation(anchor.localPosition, anchor.localRotation);
            else
                visual.transform.SetLocalPositionAndRotation(AnchorLocalPosition(index), FallbackUprightRotation);
            NeutralizeToVisualOnly(visual.gameObject);
            _visuals[index] = visual.gameObject;
        }

        // Fallback only — see class doc. Kept in sync with the anchors
        // BackroomsBuildingPieceCreator.EnsureStorageRackDisplay seeds, so an unset/short anchor
        // array reads the same as freshly-seeded ones would.
        private static Vector3 AnchorLocalPosition(int index)
        {
            int tier = index / SlotsPerTier;
            int column = index % SlotsPerTier;

            float y = tier * (RackHeight / TierCount) + ShelfClearance;
            float x = RackWidth * ((column + 0.5f) / SlotsPerTier - 0.5f);
            return new Vector3(x, y, 0f);
        }

        /// <summary>
        /// Local reimplementation of ProxyRigUtil.NeutralizeToVisualOnly
        /// (Assets/_Migration/STPIntegration/RemoteAvatar/ProxyRigUtil.cs) — that class has no
        /// asmdef of its own, so it compiles into the implicit Assembly-CSharp, which
        /// BackroomsSurvival.asmdef (this file's assembly) does not reference and, being a named
        /// assembly, cannot: Assembly-CSharp compiles last. Same order for the same reason: Unity
        /// silently REFUSES to destroy a component another one depends on via
        /// [RequireComponent] — the object survives with a log line, nothing else. Go outside-in:
        /// dependents first, then Interactable (things like Workstation depend on it), then
        /// Rigidbody/Collider. ItemPickup itself has no such dependents, but pickups can carry other
        /// riders (glow/bob effects etc.), so the full order is kept rather than assumed unnecessary.
        /// </summary>
        private static void NeutralizeToVisualOnly(GameObject go)
        {
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb is not Interactable)
                    Destroy(mb);
            }
            foreach (var interactable in go.GetComponentsInChildren<Interactable>(true))
                Destroy(interactable);
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                Destroy(rb);
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                Destroy(col);
        }

        private void OnDestroy()
        {
            if (_container != null)
                _container.SlotChanged -= OnSlotChanged;
        }
    }
}
