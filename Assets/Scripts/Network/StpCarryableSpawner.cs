using System.Collections.Generic;
using PolymindGames.WieldableSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase B2.5: makes the host the single source of truth for the world carryables that are
    /// authored in the scene (logs/stone piles nested in the level/structure prefabs). After a
    /// warmup scan, the HOST enumerates the authored <see cref="CarryablePickup"/>s, registers
    /// them with the backend (SendSetStpCarryables) and destroys the local authored copies; the
    /// StpCarryableReplicator then respawns them as gated, replicated carryables on every client.
    /// JOINERS destroy their own authored copies once the host's list arrives (or after a grace),
    /// so a late-joiner sees exactly the host's current set (picked-up ones never reappear).
    /// Mirrors the role of <see cref="StpItemSpawner"/>. Self-bootstraps; fully removable.
    /// </summary>
    public sealed class StpCarryableSpawner : MonoBehaviour
    {
        private static StpCarryableSpawner _instance;

        [Min(0f)] public float warmupSeconds = 2f;
        [Min(0.05f)] public float scanInterval = 0.25f;
        [Tooltip("If the host registers no carryables, joiners still clear authored ones after this grace.")]
        [Min(0f)] public float joinerCleanupGrace = 5f;

        private readonly HashSet<CarryablePickup> _authored = new HashSet<CarryablePickup>();
        private float _warmupEnd;
        private float _nextScan;
        private bool _warmedUp;
        private bool _hostSent;
        private bool _joinerCleaned;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpCarryableSpawner]");
            _instance = go.AddComponent<StpCarryableSpawner>();
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
            _hostSent = false;
            _joinerCleaned = false;
            _warmupEnd = Time.unscaledTime + warmupSeconds;
            _authored.Clear();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + scanInterval;

            if (!_warmedUp)
            {
                CollectAuthored();
                if (Time.unscaledTime >= _warmupEnd)
                    _warmedUp = true;
                return;
            }

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            var init = NetworkInitializer.Instance;
            bool isHost = init != null && init.CurrentRole == NetworkInitializer.Role.Host;

            if (isHost)
            {
                if (!_hostSent)
                    HostRegister(ipc);
            }
            else if (!_joinerCleaned)
            {
                JoinerCleanup(ipc);
            }
        }

        // Record the authored carryables present in the scene (those WITHOUT a network id).
        private void CollectAuthored()
        {
            var pickups = FindObjectsByType<CarryablePickup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var p in pickups)
            {
                if (p == null || p.GetComponent<NetworkCarryableInstance>() != null)
                    continue;
                _authored.Add(p);
            }
        }

        private void HostRegister(IPCClient ipc)
        {
            var specs = new List<StpCarryableSpec>();
            uint nextId = 1;
            foreach (var p in _authored)
            {
                if (p == null)
                    continue;
                var def = p.Definition;
                if (def == null)
                    continue;

                p.transform.GetPositionAndRotation(out var pos, out var rot);
                specs.Add(new StpCarryableSpec
                {
                    id = nextId++,
                    defId = def.Id,
                    position = pos,
                    rotation = rot.eulerAngles.y,
                });
            }

            ipc.SendSetStpCarryables(specs);
            DestroyAuthored();
            _hostSent = true;
            Debug.Log($"[StpCarryableSpawner] host registered {specs.Count} authored carryables.");
        }

        private void JoinerCleanup(IPCClient ipc)
        {
            var state = ipc.LatestState;
            bool listReady = state != null && state.stpCarryables.Count > 0;
            bool graceElapsed = Time.unscaledTime > _warmupEnd + joinerCleanupGrace;
            if (!listReady && !graceElapsed)
                return;

            DestroyAuthored();
            _joinerCleaned = true;
            Debug.Log($"[StpCarryableSpawner] joiner cleared authored carryables (listReady={listReady}).");
        }

        private void DestroyAuthored()
        {
            foreach (var p in _authored)
            {
                if (p != null && p.GetComponent<NetworkCarryableInstance>() == null)
                    Destroy(p.gameObject);
            }
            _authored.Clear();
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
