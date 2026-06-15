using System.Collections.Generic;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase 1: spawns/destroys real STP <see cref="ItemPickup"/>s to match
    /// world_state.stp_items on EVERY instance (host + joiners), keyed by the
    /// host-assigned network instance id. Position is host-authoritative, so spawned
    /// pickups are frozen (kinematic) to keep the 3 instances identical. Reuses the
    /// reconcile pattern of ItemRenderer. Self-bootstraps; fully removable.
    ///
    /// NOTE (Phase 1 scope): the spawned pickups are still locally interactable (STP).
    /// Gating a joiner's pickup to a host request is Phase 2/3 — for now players only
    /// observe that the same items appear at the same positions.
    /// </summary>
    public sealed class StpItemReplicator : MonoBehaviour
    {
        private static StpItemReplicator _instance;
        private readonly Dictionary<uint, GameObject> _spawned = new Dictionary<uint, GameObject>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpItemReplicator]");
            _instance = go.AddComponent<StpItemReplicator>();
            DontDestroyOnLoad(go);
        }

        private void LateUpdate()
        {
            if (!IPCClient.TryGetInstance(out var ipc))
                return;

            var state = ipc.LatestState;
            if (state == null)
                return;

            var alive = new HashSet<uint>();
            foreach (var it in state.stpItems)
            {
                alive.Add(it.id);
                // Re-spawn if missing OR destroyed (e.g. after a scene reload).
                if (!_spawned.TryGetValue(it.id, out var existing) || existing == null)
                {
                    var spawned = SpawnItem(it);
                    if (spawned != null)
                        _spawned[it.id] = spawned;
                }
            }

            var stale = new List<uint>();
            foreach (var kv in _spawned)
            {
                if (!alive.Contains(kv.Key))
                {
                    if (kv.Value != null)
                        Destroy(kv.Value);
                    stale.Add(kv.Key);
                }
            }
            foreach (uint k in stale)
                _spawned.Remove(k);
        }

        private GameObject SpawnItem(StpItemMsg it)
        {
            var def = ItemDefinition.GetWithId(it.defId);
            if (def == null)
            {
                Debug.LogWarning($"[StpItemReplicator] unknown def_id={it.defId} (item id={it.id}); skipped.");
                return null;
            }

            int count = Mathf.Max(1, it.count);
            var prefab = def.GetPickupForItemCount(count);
            if (prefab == null)
            {
                Debug.LogWarning($"[StpItemReplicator] no pickup prefab for '{def.Name}' (item id={it.id}); skipped.");
                return null;
            }

            var pickup = Instantiate(prefab, it.position, Quaternion.Euler(0f, it.rotation, 0f));
            var go = pickup.gameObject;

            // Phase 2: neutralize the vendor local pickup so interaction routes through the host.
            // Destroy the ItemPickup component instance(s) now (runtime config, NOT a vendor-source
            // edit). Because we never call AttachItem and Destroy lands before the new object's
            // Start runs, ItemPickup never subscribes to Interacted nor auto-adds to inventory.
            foreach (var vendorPickup in go.GetComponentsInChildren<ItemPickup>(true))
                Destroy(vendorPickup);

            // Keep the hover prompt meaningful now that the vendor pickup no longer sets it.
            var hov = go.GetComponentInChildren<IHoverableInteractable>(true);
            if (hov != null && string.IsNullOrEmpty(hov.Title))
                hov.Title = "Pick Up " + def.Name;

            // Host-authoritative position → freeze physics so all instances match exactly.
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            var nid = go.AddComponent<NetworkItemInstance>();
            nid.id = it.id;

            // Phase 2 host-authoritative pickup gate (subscribes to the item's Interacted event).
            var gate = go.AddComponent<NetworkItemPickupGate>();
            gate.itemId = it.id;

            Debug.Log($"[StpItemReplicator] spawned id={it.id} def_id={it.defId} '{def.Name}' at {it.position:F1}");
            return go;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
