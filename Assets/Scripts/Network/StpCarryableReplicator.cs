using System.Collections.Generic;
using PolymindGames;
using PolymindGames.WieldableSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase B2.5: spawns/destroys replicated STP world carryables to match
    /// world_state.stp_carryables on EVERY instance (host + joiners), keyed by the host-assigned
    /// network id. Reuses the reconcile pattern of <see cref="StpItemReplicator"/>. Spawned
    /// carryables are frozen, host-authoritative copies whose vendor CarryablePickup is removed so
    /// the pickup routes through the host (<see cref="StpCarryablePickupGate"/>). Self-bootstraps.
    /// </summary>
    public sealed class StpCarryableReplicator : MonoBehaviour
    {
        private static StpCarryableReplicator _instance;
        private readonly Dictionary<uint, GameObject> _spawned = new Dictionary<uint, GameObject>();

        // Perf: reused across LateUpdate calls instead of allocating a fresh HashSet/List every
        // frame (docs/perf/PERF_AUDIT_v1.md §1.5). Cleared at the top of each use.
        private readonly HashSet<uint> _aliveScratch = new HashSet<uint>();
        private readonly List<uint> _staleScratch = new List<uint>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpCarryableReplicator]");
            _instance = go.AddComponent<StpCarryableReplicator>();
            DontDestroyOnLoad(go);
        }

        private void LateUpdate()
        {
            if (!IPCClient.TryGetInstance(out var ipc))
                return;

            var state = ipc.LatestState;
            if (state == null)
                return;

            _aliveScratch.Clear();
            foreach (var c in state.stpCarryables)
            {
                _aliveScratch.Add(c.id);
                if (!_spawned.TryGetValue(c.id, out var existing) || existing == null)
                {
                    var spawned = SpawnCarryable(c);
                    if (spawned != null)
                        _spawned[c.id] = spawned;
                }
            }

            _staleScratch.Clear();
            foreach (var kv in _spawned)
            {
                if (!_aliveScratch.Contains(kv.Key))
                {
                    if (kv.Value != null)
                        Destroy(kv.Value);
                    _staleScratch.Add(kv.Key);
                }
            }
            foreach (uint k in _staleScratch)
                _spawned.Remove(k);
        }

        private GameObject SpawnCarryable(StpCarryableMsg c)
        {
            var def = CarryableDefinition.GetWithId(c.defId);
            if (def == null)
            {
                Debug.LogWarning($"[StpCarryableReplicator] unknown def_id={c.defId} (carryable id={c.id}); skipped.");
                return null;
            }

            var prefab = def.Pickup;
            if (prefab == null)
            {
                Debug.LogWarning($"[StpCarryableReplicator] no pickup prefab for '{def.Name}' (carryable id={c.id}); skipped.");
                return null;
            }

            var rot = Quaternion.Euler(0f, c.rotation, 0f);
            var pickup = Instantiate(prefab, c.position, rot);
            var go = pickup.gameObject;

            // Neutralize the vendor CarryablePickup so interaction routes through the host (mirrors
            // StpItemReplicator destroying ItemPickup). Destroying before Start runs means it never
            // subscribes Interacted nor carries locally; only the gate responds.
            // CarryableBuildAction has [RequireComponent(typeof(CarryablePickup))], so it must be
            // destroyed first or Unity refuses to remove CarryablePickup.
            foreach (var buildAction in go.GetComponentsInChildren<CarryableBuildAction>(true))
                Destroy(buildAction);
            foreach (var vendorPickup in go.GetComponentsInChildren<CarryablePickup>(true))
                Destroy(vendorPickup);

            // Keep the hover prompt meaningful now that the vendor pickup no longer sets it.
            var hov = go.GetComponentInChildren<IHoverableInteractable>(true);
            if (hov != null && string.IsNullOrEmpty(hov.Title))
                hov.Title = "Carry " + def.Name;

            // Host-authoritative position → freeze physics so all instances match exactly.
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            var nid = go.AddComponent<NetworkCarryableInstance>();
            nid.id = c.id;

            var gate = go.AddComponent<StpCarryablePickupGate>();
            gate.carryableId = c.id;
            gate.defId = c.defId;

            Debug.Log($"[StpCarryableReplicator] spawned id={c.id} def_id={c.defId} '{def.Name}' at {c.position:F1}");
            return go;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
