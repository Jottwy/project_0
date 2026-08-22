using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
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
        private readonly Dictionary<uint, Tracked> _spawned = new Dictionary<uint, Tracked>();

        [Tooltip("ADR-070: render a falling item this far behind real time (s) so interpolation always " +
                 "runs between two received samples instead of extrapolating past the last one.")]
        public float latencyMarginSec = 0.12f;

        /// <summary>
        /// ADR-070: one replicated item. While <see cref="settling"/>, the host's position MOVES
        /// between relays, so the transform follows it through a two-sample buffer — the same shape
        /// StatInterpolator already uses for the stat stream, for the same reason (a 10 Hz
        /// authoritative signal rendered at frame rate).
        ///
        /// The Rigidbody stays kinematic for the WHOLE fall, including while settling. Letting PhysX
        /// drive it would put the client back in charge of where the object goes — the exact thing
        /// ADR-070 takes away — and then the flip to settled would yank it back to the host's answer.
        /// The roll below is cosmetic and deliberately NOT physics: ADR-070 decision 3 keeps
        /// orientation client-side precisely so it can be faked cheaply.
        /// </summary>
        private sealed class Tracked
        {
            public GameObject go;
            public bool settling;
            public Vector3 p0, p1;
            public float t0, t1;
            public bool hasSample;
            public Vector3 rollAxis;

            // ItemPickupOrientation.PivotHalfHeightFor, resolved once at spawn (the item id never
            // changes definition). Every use of the host's it.position adds this — the host reports
            // where the item's BASE settled, but the pickup's pivot is at its own centre (see that
            // method's doc), so the raw position alone sinks it halfway into the ground.
            public float pivotHalfHeight;

            public Vector3 Adjust(Vector3 raw) => raw + Vector3.up * pivotHalfHeight;

            public void Push(Vector3 value, float now)
            {
                value = Adjust(value);
                if (!hasSample) { p0 = value; t0 = now; }
                else { p0 = p1; t0 = t1; }
                p1 = value;
                t1 = now;
                hasSample = true;
            }
        }

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

            float now = Time.time;
            var alive = new HashSet<uint>();
            foreach (var it in state.stpItems)
            {
                alive.Add(it.id);
                // Re-spawn if missing OR destroyed (e.g. after a scene reload).
                if (!_spawned.TryGetValue(it.id, out var tracked) || tracked.go == null)
                {
                    var spawned = SpawnItem(it);
                    if (spawned == null)
                        continue;

                    var itemDef = ItemDefinition.GetWithId(it.defId);
                    tracked = new Tracked
                    {
                        go = spawned,
                        settling = it.settling,
                        rollAxis = RandomRollAxis(),
                        pivotHalfHeight = itemDef != null ? ItemPickupOrientation.PivotHalfHeightFor(itemDef.Name) : 0f,
                    };
                    tracked.Push(it.position, now);
                    _spawned[it.id] = tracked;
                    continue;
                }

                // ADR-070: while the host is still settling this item its position changes every
                // relay, so feed the buffer. The frame it stops settling, pin the final answer —
                // that snap is at most one interpolation step wide because the last sample the host
                // sent is the resting place.
                if (it.settling)
                {
                    tracked.settling = true;
                    tracked.Push(it.position, now);
                }
                else if (tracked.settling)
                {
                    tracked.settling = false;
                    tracked.go.transform.position = tracked.Adjust(it.position);
                }
            }

            AdvanceSettling(now);

            var stale = new List<uint>();
            foreach (var kv in _spawned)
            {
                if (!alive.Contains(kv.Key))
                {
                    if (kv.Value.go != null)
                        Destroy(kv.Value.go);
                    stale.Add(kv.Key);
                }
            }
            foreach (uint k in stale)
                _spawned.Remove(k);
        }

        /// <summary>
        /// ADR-070: drive every falling item's transform this frame. Interpolates between the last
        /// two host samples at (now - latency) with a Clamp01, so it never extrapolates past what
        /// the host actually said — if the stream stalls the item holds still instead of drifting
        /// through a wall the host already stopped it against.
        /// </summary>
        private void AdvanceSettling(float now)
        {
            foreach (var kv in _spawned)
            {
                var t = kv.Value;
                if (!t.settling || !t.hasSample || t.go == null)
                    continue;

                float renderT = now - latencyMarginSec;
                float span = t.t1 - t.t0;
                float f = span > 1e-4f ? Mathf.Clamp01((renderT - t.t0) / span) : 1f;
                Vector3 next = Vector3.Lerp(t.p0, t.p1, f);

                // Cosmetic roll, proportional to the distance actually travelled — no physics, no
                // wire cost. Two clients will disagree on the final orientation and that is the
                // documented trade of ADR-070 decision 3: pickup goes by id and position, never by
                // which way the tin can ended up facing.
                Vector3 moved = next - t.go.transform.position;
                float horizontal = new Vector2(moved.x, moved.z).magnitude;
                if (horizontal > 1e-4f)
                    t.go.transform.Rotate(t.rollAxis, horizontal * RollDegreesPerMetre, Space.World);

                t.go.transform.position = next;
            }
        }

        // Spin axis for the cosmetic roll. Random per item so a handful of identical cans dropped
        // together do not tumble in lockstep, which reads as a glitch rather than as physics.
        private static Vector3 RandomRollAxis()
        {
            var axis = new Vector3(Random.value - 0.5f, 0f, Random.value - 0.5f);
            return axis.sqrMagnitude < 1e-4f ? Vector3.right : axis.normalized;
        }

        private const float RollDegreesPerMetre = 220f;

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

            // Stand up, then face wherever the host said (yaw only) — same per-item correction as
            // the storage rack (ItemPickupOrientation), same root cause: this pickup's own resting
            // rotation (identity) has it lying on its side. Composed as yaw * upright (upright
            // applies first, in the pickup's own frame; yaw spins that result around world Y) so the
            // item stands regardless of which way it's facing.
            var rotation = Quaternion.Euler(0f, it.rotation, 0f) * ItemPickupOrientation.UprightCorrectionFor(def.Name);

            // Host reports where the item's BASE settled; the pickup's own pivot is at its centre
            // (see PivotHalfHeightFor) — lift the spawn point or it plants half-sunk into the ground
            // on the very first frame, before LateUpdate's Tracked.Adjust ever runs for it.
            var spawnPosition = it.position + Vector3.up * ItemPickupOrientation.PivotHalfHeightFor(def.Name);
            var pickup = Instantiate(prefab, spawnPosition, rotation);
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
            gate.defId = it.defId;
            gate.count = count;

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
