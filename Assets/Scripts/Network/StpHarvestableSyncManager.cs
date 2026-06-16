using System;
using System.Collections.Generic;
using System.Reflection;
using PolymindGames;
using PolymindGames.Demo;
using PolymindGames.ResourceHarvesting;
using PolymindGames.WieldableSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase B2.6: makes scene-placed STP harvestables (tree/rock prefabs) host-authoritative,
    /// with zero vendor edits. Calques the B2.5 enumeration but does NOT destroy/respawn the
    /// harvestables (complex prefabs): the host enumerates them, assigns a net id + position and
    /// registers them (SendSetStpHarvestables); every client maps net id → local harvestable by
    /// position proximity and binds it.
    ///
    /// On bind, the vendor LOG SPAWNERS are stripped (HarvestableFallBehaviour + PrefabSpawner)
    /// so no client spawns random local logs — their (cached) carryable def + count is read
    /// first by reflection. The host-authoritative health (`remaining`) is reflected on every
    /// client by setting <c>_remainingHarvestAmount</c> + invoking the protected
    /// <c>SetState</c> (no event → no feedback loop). When the host's `remaining` crosses to 0,
    /// the HOST spawns the resource carryables deterministically (a ring) into stp_carryables
    /// (B2.5) — so every client sees the SAME logs at the SAME positions. Self-bootstraps.
    /// Concurrency (two players one tree) is safe: the host serializes hits + dedups by hit_id.
    /// </summary>
    public sealed class StpHarvestableSyncManager : MonoBehaviour
    {
        [Min(0f)] public float warmupSeconds = 2f;
        [Min(0.05f)] public float scanInterval = 0.25f;
        [Tooltip("Max distance to match a net id to a local harvestable (authored positions are unique).")]
        [Min(0.1f)] public float matchRadius = 1.0f;
        [Min(0.1f)] public float spawnRingRadius = 1.5f;
        [Min(0.5f)] public float groundRayDistance = 6f;
        public float groundOffset = 0.05f;

        private static StpHarvestableSyncManager _instance;

        private readonly Dictionary<uint, NetworkHarvestableInstance> _bound = new Dictionary<uint, NetworkHarvestableInstance>();
        private readonly List<HarvestableResource> _localHarvestables = new List<HarvestableResource>();
        private bool _warmedUp;
        private bool _hostRegistered;
        private float _warmupEnd;
        private float _nextScan;
        private uint _dropCounter;

        // Reflection (HarvestableResource): set health field + invoke protected SetState.
        private static FieldInfo _remainingField;
        private static MethodInfo _setStateMethod;
        private static bool _hrReflectionResolved;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpHarvestableSyncManager]");
            _instance = go.AddComponent<StpHarvestableSyncManager>();
            DontDestroyOnLoad(go);
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            ArmWarmup();
        }

        private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ArmWarmup();

        private void ArmWarmup()
        {
            _warmedUp = false;
            _hostRegistered = false;
            _warmupEnd = Time.unscaledTime + warmupSeconds;
            _bound.Clear();
            _localHarvestables.Clear();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + scanInterval;

            if (!_warmedUp)
            {
                CollectLocalHarvestables();
                if (Time.unscaledTime >= _warmupEnd)
                    _warmedUp = true;
                return;
            }

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            var init = NetworkInitializer.Instance;
            bool isHost = init != null && init.CurrentRole == NetworkInitializer.Role.Host;

            if (isHost && !_hostRegistered)
                HostRegister(ipc);

            var state = ipc.LatestState;
            if (state == null)
                return;

            foreach (var h in state.stpHarvestables)
            {
                if (!_bound.ContainsKey(h.id))
                    TryBindByProximity(h);

                ApplyAuthoritative(h);

                if (isHost)
                    MaybeSpawnOnDeplete(ipc, h);
            }
        }

        private void CollectLocalHarvestables()
        {
            var found = FindObjectsByType<HarvestableResource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var hr in found)
            {
                if (hr != null && !_localHarvestables.Contains(hr))
                    _localHarvestables.Add(hr);
            }
        }

        // Host: assign net ids to the local harvestables, register them, and bind locally.
        private void HostRegister(IPCClient ipc)
        {
            var specs = new List<StpHarvestableSpec>();
            uint nextId = 1;
            foreach (var hr in _localHarvestables)
            {
                if (hr == null)
                    continue;

                uint id = nextId++;
                specs.Add(new StpHarvestableSpec { id = id, position = hr.transform.position });
                Bind(id, hr);
            }

            ipc.SendSetStpHarvestables(specs);
            _hostRegistered = true;
            Debug.Log($"[StpHarvestableSyncManager] host registered {specs.Count} harvestables.");
        }

        // Joiner: map a net id to the nearest unbound local harvestable (unique authored positions).
        private void TryBindByProximity(StpHarvestableMsg h)
        {
            HarvestableResource best = null;
            float bestSqr = matchRadius * matchRadius;
            foreach (var hr in _localHarvestables)
            {
                if (hr == null || hr.GetComponent<NetworkHarvestableInstance>() != null)
                    continue;

                float sqr = (hr.transform.position - h.position).sqrMagnitude;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    best = hr;
                }
            }

            if (best != null)
                Bind(h.id, best);
        }

        private void Bind(uint id, HarvestableResource hr)
        {
            var (logDefId, logCount) = ReadAndStripSpawners(hr);

            var nh = hr.gameObject.AddComponent<NetworkHarvestableInstance>();
            nh.id = id;
            nh.logDefId = logDefId;
            nh.logCount = logCount;
            _bound[id] = nh;
        }

        // Reads the resource-carryable def + count from whichever vendor spawner is present, then
        // DESTROYS the spawners so no client produces random local logs (the host spawns instead).
        private (int logDefId, int logCount) ReadAndStripSpawners(HarvestableResource hr)
        {
            int logDefId = -1;
            int logCount = 0;

            var fall = hr.GetComponentInChildren<HarvestableFallBehaviour>(true);
            if (fall != null)
            {
                var prefab = ReflGet<Rigidbody>(fall, "_itemPrefab");
                int def = CarryableDefIdOf(prefab);
                if (def >= 0)
                {
                    logDefId = def;
                    logCount = Mathf.Max(1, ReflGetInt(fall, "_itemCount"));
                }
                Destroy(fall);
            }

            var spawner = hr.GetComponentInChildren<PrefabSpawner>(true);
            if (spawner != null)
            {
                if (logDefId < 0)
                {
                    var prefabs = ReflGet<Rigidbody[]>(spawner, "_prefabs");
                    if (prefabs != null && prefabs.Length > 0)
                    {
                        int def = CarryableDefIdOf(prefabs[0]);
                        if (def >= 0)
                        {
                            var range = ReflGetV2I(spawner, "_spawnCountRange");
                            logDefId = def;
                            logCount = Mathf.Max(1, (range.x + range.y) / 2);
                        }
                    }
                }
                Destroy(spawner);
            }

            return (logDefId, logCount);
        }

        // Apply the host-authoritative health to a bound harvestable (reflection; no events).
        private void ApplyAuthoritative(StpHarvestableMsg h)
        {
            if (!_bound.TryGetValue(h.id, out var nh) || nh == null)
                return;

            var hr = nh.GetComponent<HarvestableResource>();
            if (hr == null)
                return;

            ResolveHrReflection();

            if (_remainingField != null)
                _remainingField.SetValue(hr, h.remaining);

            if (_setStateMethod != null)
            {
                HarvestableState target = h.remaining < 0.001f
                    ? HarvestableState.FullyHarvested
                    : (h.remaining < 0.999f ? HarvestableState.PartiallyHarvested : HarvestableState.Unharvested);
                try { _setStateMethod.Invoke(hr, new object[] { target }); }
                catch (Exception e) { Debug.LogWarning($"[StpHarvestableSyncManager] SetState failed id={h.id}: {e.Message}"); }
            }
        }

        // Host only: when a harvestable depletes, spawn its resource carryables once, at a
        // deterministic ring, into stp_carryables (B2.5) — same positions for everyone.
        private void MaybeSpawnOnDeplete(IPCClient ipc, StpHarvestableMsg h)
        {
            if (!_bound.TryGetValue(h.id, out var nh) || nh == null || nh.spawnedOnDeplete)
                return;
            if (h.remaining > 0.001f)
                return;

            nh.spawnedOnDeplete = true;

            if (nh.logDefId < 0 || nh.logCount <= 0)
            {
                Debug.LogWarning($"[StpHarvestableSyncManager] harvestable id={h.id} depleted but no resource carryable config; nothing spawned.");
                return;
            }

            Vector3 basePos = nh.transform.position;
            int n = Mathf.Max(1, nh.logCount);
            for (int i = 0; i < n; i++)
            {
                float ang = i * Mathf.PI * 2f / n;
                Vector3 ring = basePos + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * spawnRingRadius;
                Vector3 grounded = GroundPosition(ring);
                ipc.SendStpCarryableDrop(NextDropId(), nh.logDefId, grounded, ang * Mathf.Rad2Deg);
            }

            Debug.Log($"[StpHarvestableSyncManager] depleted id={h.id} → host spawned {n} resource carryables (def={nh.logDefId}).");
        }

        private Vector3 GroundPosition(Vector3 from)
        {
            Vector3 origin = from + Vector3.up * 1.5f;
            if (Physics.Raycast(origin, Vector3.down, out var hit, groundRayDistance,
                    LayerConstants.SimpleSolidObjectsMask, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * groundOffset;

            return from;
        }

        private long NextDropId()
        {
            int netId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
            return (long)Mathf.Max(1, netId) * 1000000000L + 500000000L + (++_dropCounter);
        }

        #region Reflection helpers
        private static void ResolveHrReflection()
        {
            if (_hrReflectionResolved)
                return;
            _hrReflectionResolved = true;

            _remainingField = typeof(HarvestableResource).GetField("_remainingHarvestAmount", BindingFlags.NonPublic | BindingFlags.Instance);
            _setStateMethod = typeof(HarvestableResource).GetMethod("SetState", BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(HarvestableState) }, null);

            if (_remainingField == null || _setStateMethod == null)
                Debug.LogWarning("[StpHarvestableSyncManager] HarvestableResource reflection incomplete; health may not apply.");
        }

        private static int CarryableDefIdOf(Rigidbody prefab)
        {
            if (prefab == null)
                return -1;
            var pickup = prefab.GetComponent<CarryablePickup>();
            var def = pickup != null ? pickup.Definition : null;
            return def != null ? def.Id : -1;
        }

        private static T ReflGet<T>(object obj, string field) where T : class
        {
            var fi = obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            return fi != null ? fi.GetValue(obj) as T : null;
        }

        private static int ReflGetInt(object obj, string field)
        {
            var fi = obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            return fi != null ? Convert.ToInt32(fi.GetValue(obj)) : 0;
        }

        private static Vector2Int ReflGetV2I(object obj, string field)
        {
            var fi = obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            return fi != null && fi.GetValue(obj) is Vector2Int v ? v : default;
        }
        #endregion

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
