using PolymindGames;
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
    /// Slot positions are COMPUTED, not authored `Transform[]` children: the rack has 4 equal open
    /// tiers of 4 columns each (measured in E1, see <see cref="TierCount"/>/<see cref="RackWidth"/>/
    /// <see cref="RackHeight"/> — same numbers as <c>BackroomsBuildingPieceCreator</c>'s
    /// `StorageRack*` constants, an editor-only class in a different assembly this one cannot
    /// reference, hence the duplication). A formula survives a re-measure or slot-count change
    /// without another editor round-trip through this project's Unity-reimport pipeline (see memory
    /// unity-inedit-trigger-automation), at the cost of the exact resting spot being an estimate
    /// (<see cref="ShelfClearance"/>) rather than read off the model's own geometry. Good enough for
    /// v1; if it reads wrong in play, nudge the constants, no prefab surgery needed.
    /// </summary>
    [RequireComponent(typeof(StorageStation))]
    public sealed class StorageRackDisplay : MonoBehaviour
    {
        private const int TierCount = 4;
        private const int SlotsPerTier = 4;
        private const float RackWidth = 1.4527f;
        private const float RackHeight = 1.9026f;

        // How far above a tier's floor/shelf board a placed item's pivot sits — most pickup meshes
        // are NOT authored with their pivot at their own base, so this is a flat estimate, not a
        // per-item measurement (see class doc).
        private const float ShelfClearance = 0.06f;

        private IItemContainer _container;
        private GameObject[] _visuals;

        // Lazy-resolved on the first Update, not Start: StorageStation.GetContainers() reads
        // Workstation.Name, which reads a private _interactable field that Workstation.Start() only
        // assigns once its own Start() has run — script execution order between two components on
        // the same GameObject is NOT guaranteed, so resolving in Start() here could race it. Every
        // Start() in the scene has already run by the time ANY Update() runs (Unity's per-frame
        // Awake→Start→Update ordering), so this is race-free without needing
        // [DefaultExecutionOrder]. Same pattern as InventoryReporter.ResolveAndSubscribe.
        private void Update()
        {
            if (_container != null)
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
            visual.transform.SetLocalPositionAndRotation(AnchorLocalPosition(index), Quaternion.identity);
            NeutralizeToVisualOnly(visual.gameObject);
            _visuals[index] = visual.gameObject;
        }

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
