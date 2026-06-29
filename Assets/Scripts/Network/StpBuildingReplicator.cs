using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using PolymindGames.BuildingSystem;
using PolymindGames.SaveSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Spawns/destroys replicated STP building pieces to match world_state.stp_buildings on
    /// EVERY instance (host + joiners), keyed by the host-assigned network instance id. Reuses
    /// the reconcile pattern of <see cref="StpItemReplicator"/>. Self-bootstraps; removable.
    ///
    /// B1: a placed piece appears for everyone, frozen, host-authoritative.
    /// B2: pieces spawn in PLACED state with the host-authoritative construction progress
    /// applied to <c>Constructable._requirements</c> via the public ISaveableComponent.LoadMembers
    /// hook (no reflection for progress). As the host accepts material adds, the progress is
    /// reconciled here; on the transition to fully built the piece is respawned in CONSTRUCTED
    /// (one flicker, at completion only). Spawning Placed (not Constructed) is required so the
    /// vendor builder will target the piece (it skips IsConstructed pieces,
    /// CharacterConstructableBuilder.cs:191).
    ///
    /// The only vendor-protected touch is forcing the BuildingPiece visual state (the enum is
    /// protected) via the serialized `_state` field of THIS spawned instance — no vendor source
    /// is edited. Construction PROGRESS uses the public LoadMembers API.
    /// </summary>
    public sealed class StpBuildingReplicator : MonoBehaviour
    {
        // BuildingPiece.BuildingPieceState values (vendor enum): Constructed = 3, Placed = 4.
        private const int ConstructedStateValue = 3;
        private const int PlacedStateValue = 4;

        private static StpBuildingReplicator _instance;

        private sealed class Tracked
        {
            public GameObject go;
            public uint groupId;    // B3: the group this piece belongs to (0 = standalone)
            public bool complete;   // sticky: progress only advances, never downgrades
            public string addedKey; // last-applied progress snapshot (change detection)
        }

        private readonly Dictionary<uint, Tracked> _spawned = new Dictionary<uint, Tracked>();
        // B3: group_id → reconstructed BuildingPieceGroup. Pieces sharing a group_id are
        // bucketed here so their sockets cohere (membership + OccupyAdjacentSockets) on every
        // client, exactly like STP's own save/load path.
        private readonly Dictionary<uint, BuildingPieceGroup> _groups = new Dictionary<uint, BuildingPieceGroup>();
        // def_id → prefab-authored build requirements (the required amounts live in the prefab).
        private readonly Dictionary<int, BuildRequirement[]> _authoredByDef = new Dictionary<int, BuildRequirement[]>();

        // Cached reflection for the protected serialized `_state` field + its boxed values
        // (the enum type is not nameable from outside the vendor assembly).
        private static FieldInfo _stateField;
        private static object _placedBoxed;
        private static object _constructedBoxed;
        private static bool _stateReflectionResolved;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpBuildingReplicator]");
            _instance = go.AddComponent<StpBuildingReplicator>();
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
            foreach (var b in state.stpBuildings)
            {
                alive.Add(b.id);
                string key = AddedKey(b.added);

                if (!_spawned.TryGetValue(b.id, out var tracked) || tracked.go == null)
                {
                    var go = SpawnBuilding(b, out bool complete);
                    if (go != null)
                        _spawned[b.id] = new Tracked { go = go, groupId = b.groupId, complete = complete, addedKey = key };
                }
                else if (tracked.addedKey != key)
                {
                    ApplyProgressUpdate(b, tracked);
                    tracked.addedKey = key;
                }
            }

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

            // B3: drop group buckets whose BuildingPieceGroup was auto-destroyed by the vendor
            // (it self-destructs when its last piece is removed). EnsureGroup recreates on demand.
            List<uint> deadGroups = null;
            foreach (var kv in _groups)
            {
                if (kv.Value == null)
                    (deadGroups ??= new List<uint>()).Add(kv.Key);
            }
            if (deadGroups != null)
                foreach (uint k in deadGroups)
                    _groups.Remove(k);
        }

        // B3: returns the reconstructed BuildingPieceGroup for a group_id, creating it (tagged
        // with a NetworkBuildingGroupInstance companion) on first use. group_id 0 = standalone
        // (free pieces) → null, matching the B1 ungrouped path.
        private BuildingPieceGroup EnsureGroup(uint groupId, Vector3 at)
        {
            if (groupId == 0)
                return null;

            if (_groups.TryGetValue(groupId, out var existing) && existing != null)
                return existing;

            var prefab = BuildingManager.Instance != null ? BuildingManager.Instance.DefaultGroupPrefab : null;
            if (prefab == null)
            {
                Debug.LogWarning($"[StpBuildingReplicator] no DefaultGroupPrefab; group {groupId} pieces spawn standalone.");
                return null;
            }

            var grp = Instantiate(prefab, at, Quaternion.identity);
            grp.gameObject.AddComponent<NetworkBuildingGroupInstance>().groupId = groupId;
            _groups[groupId] = grp;
            return grp;
        }

        private GameObject SpawnBuilding(StpBuildingMsg b, out bool complete)
        {
            complete = false;

            var def = BuildingPieceDefinition.GetWithId(b.defId);
            if (def == null)
            {
                Debug.LogWarning($"[StpBuildingReplicator] unknown def_id={b.defId} (building id={b.id}); skipped.");
                return null;
            }

            var prefab = def.Prefab;
            if (prefab == null)
            {
                Debug.LogWarning($"[StpBuildingReplicator] no prefab for '{def.Name}' (building id={b.id}); skipped.");
                return null;
            }

            var authored = GetAuthored(b.defId);
            var (newReqs, isComplete) = ComputeProgress(authored, b.added);
            complete = isComplete;

            var rot = Quaternion.Euler(0f, b.rotation, 0f);
            var prefabGo = prefab.gameObject;

            // B3: bucket this piece under its group so sockets cohere on every client.
            var grp = EnsureGroup(b.groupId, b.position);

            // Instantiate INACTIVE so progress + state are set before Awake/Start run. Parent
            // under the group (4-arg overload keeps the WORLD pose) so GroupBuildingPiece's
            // LoadMembers can find the group via GetComponentInParent.
            bool prefabWasActive = prefabGo.activeSelf;
            prefabGo.SetActive(false);
            GameObject go;
            try
            {
                go = Instantiate(prefabGo, b.position, rot, grp != null ? grp.transform : null);
            }
            finally
            {
                prefabGo.SetActive(prefabWasActive);
            }

            // Apply construction progress via the public save hook (no reflection).
            if (go.GetComponent<Constructable>() is ISaveableComponent sc)
                sc.LoadMembers(newReqs);

            // Force the visual state: Constructed when fully built, otherwise Placed.
            ForceState(go, isComplete);

            var nid = go.AddComponent<NetworkBuildingInstance>();
            nid.id = b.id;

            go.SetActive(true);

            // Host-authoritative transform → freeze any physics so all instances match exactly.
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            // B3: wire group membership + socket occupancy via the SAME public save hook STP's
            // own load path uses (_parentGroup via GetComponentInParent, AddBuildingPiece,
            // OccupyAdjacentSockets). Runs active so GetComponentInParent finds the live group.
            // Only for group pieces; free pieces (grp == null) stay standalone, exactly like B1.
            if (grp != null && go.GetComponent<BuildingPiece>() is ISaveableComponent groupSaveable)
                groupSaveable.LoadMembers(GetBoxedState(isComplete));

            Debug.Log($"[StpBuildingReplicator] spawned id={b.id} def_id={b.defId} '{def.Name}' group_id={b.groupId} at {b.position:F1} (complete={isComplete}).");
            return go;
        }

        // Reconciles a progress change on an already-spawned piece. Sticky-complete: once a piece
        // is fully built we never downgrade it (the host's `added` only ever increases, and the
        // local vendor may have optimistically completed it first).
        private void ApplyProgressUpdate(StpBuildingMsg b, Tracked tracked)
        {
            if (tracked.complete || tracked.go == null)
                return;

            var authored = GetAuthored(b.defId);
            var (newReqs, complete) = ComputeProgress(authored, b.added);

            if (complete)
            {
                // Completion transition → respawn in Constructed (the only flicker, at the end).
                // Defer the respawn: Unity's Destroy runs at end of frame, so spawning inline
                // would briefly leave two coincident pieces in the group and corrupt socket
                // occupancy (the dying piece's ClearAdjacentSockets would unoccupy the new one).
                // Null the handle and let the next reconcile re-spawn cleanly via EnsureGroup
                // (which recreates the group if this was its last piece).
                Destroy(tracked.go);
                tracked.go = null;
                tracked.complete = true;
            }
            else if (tracked.go.GetComponent<Constructable>() is ISaveableComponent sc)
            {
                // Intermediate progress: update requirements in place, no respawn.
                sc.LoadMembers(newReqs);
            }
        }

        // Derives per-piece requirements from the prefab-authored requirements + host-accepted
        // counts. Returns the requirement list and whether the piece is fully built. STP's
        // convention is that a constructed piece has an EMPTY requirement list.
        private static (List<BuildRequirement>, bool) ComputeProgress(BuildRequirement[] authored, List<StpBuildProgressMsg> added)
        {
            if (authored == null || authored.Length == 0)
                return (new List<BuildRequirement>(), true);

            var reqs = new List<BuildRequirement>(authored.Length);
            bool allComplete = true;

            foreach (var req in authored)
            {
                int addedCount = 0;
                if (added != null)
                {
                    foreach (var p in added)
                    {
                        if (p.materialId == req.BuildMaterialId)
                        {
                            addedCount = p.count;
                            break;
                        }
                    }
                }

                int current = Mathf.Min(addedCount, req.RequiredAmount);
                reqs.Add(new BuildRequirement(req.BuildMaterialId, req.RequiredAmount, current));
                if (current < req.RequiredAmount)
                    allComplete = false;
            }

            if (allComplete)
                return (new List<BuildRequirement>(), true);

            return (reqs, false);
        }

        // Prefab-authored required amounts (cached per def). Read off the prefab's Constructable.
        private BuildRequirement[] GetAuthored(int defId)
        {
            if (_authoredByDef.TryGetValue(defId, out var cached))
                return cached;

            BuildRequirement[] arr = Array.Empty<BuildRequirement>();
            var def = BuildingPieceDefinition.GetWithId(defId);
            var prefab = def != null ? def.Prefab : null;
            var constructable = prefab != null ? prefab.GetComponent<Constructable>() : null;
            if (constructable != null)
            {
                var list = constructable.GetBuildRequirements();
                if (list != null)
                {
                    arr = new BuildRequirement[list.Count];
                    for (int i = 0; i < list.Count; i++)
                        arr[i] = list[i];
                }
            }

            _authoredByDef[defId] = arr;
            return arr;
        }

        private static string AddedKey(List<StpBuildProgressMsg> added)
        {
            if (added == null || added.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var p in added)
                sb.Append(p.materialId).Append(':').Append(p.count).Append(';');
            return sb.ToString();
        }

        private static void ForceState(GameObject go, bool complete)
        {
            var piece = go.GetComponent<BuildingPiece>();
            if (piece == null)
                return;

            object boxed = GetBoxedState(complete);
            if (boxed == null)
                return;

            try
            {
                _stateField.SetValue(piece, boxed);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[StpBuildingReplicator] could not force state: {e.Message}");
            }
        }

        // Boxed BuildingPieceState (Constructed | Placed) for the vendor's protected enum. Used
        // both to force the visual state and as the `data` arg for GroupBuildingPiece.LoadMembers
        // (which does `State = (BuildingPieceState)data`). Same reflection as B1 — none new.
        private static object GetBoxedState(bool complete)
        {
            ResolveStateReflection();
            if (_stateField == null)
                return null;

            return complete ? _constructedBoxed : _placedBoxed;
        }

        private static void ResolveStateReflection()
        {
            if (_stateReflectionResolved)
                return;
            _stateReflectionResolved = true;

            _stateField = typeof(BuildingPiece).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_stateField != null)
            {
                _constructedBoxed = Enum.ToObject(_stateField.FieldType, ConstructedStateValue);
                _placedBoxed = Enum.ToObject(_stateField.FieldType, PlacedStateValue);
            }
            else
            {
                Debug.LogWarning("[StpBuildingReplicator] BuildingPiece._state field not found; pieces spawn in default state.");
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
