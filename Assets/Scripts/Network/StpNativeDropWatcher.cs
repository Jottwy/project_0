using System.Collections.Generic;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase 3 (Option B): syncs STP's NATIVE drops (inventory "Drop" button + UI drag-out) with
    /// zero vendor edits. Both native paths end in a vendor DropAction that instantiates a local
    /// <see cref="ItemPickup"/> next to the dropper. This watcher detects MY freshly-dropped
    /// pickup, reads its item, DESTROYS it, and asks the host to spawn the replicated version
    /// (SendStpDrop → process_stp_drop → stp_items → Phase 1 relay → spawn with the Phase 2 gate).
    /// The 1-frame local pickup → host echo round-trip is a tolerable flicker for pre-alpha.
    ///
    /// Anti-false-adoption: a per-scene WARMUP records every pre-existing ItemPickup into a
    /// seen-set (Showcase props, items others dropped), so only brand-new pickups that appear
    /// NEAR me AFTER warmup are adopted. Replicated pickups (Phase 1/others' drops) carry
    /// <see cref="NetworkItemInstance"/> and are never adopted. Self-bootstraps; fully removable.
    /// </summary>
    public sealed class StpNativeDropWatcher : MonoBehaviour
    {
        private static StpNativeDropWatcher _instance;

        [Tooltip("Only adopt pickups within this distance of the local player (a native drop lands next to me).")]
        [Min(0.5f)] public float adoptRadius = 3f;

        [Tooltip("Record everything already in the scene for this long before adopting anything (anti-Showcase).")]
        [Min(0f)] public float warmupSeconds = 2f;

        [Min(0.02f)] public float scanInterval = 0.1f;
        public string gameplayScene = "STP_Showcase";

        [Tooltip("Down-raycast length to find the floor under a native drop.")]
        [Min(0.5f)] public float groundRayDistance = 6f;
        [Tooltip("How far above the floor hit the item rests.")]
        public float groundOffset = 0.05f;
        [Tooltip("If no floor is found, place the item this far below the hand drop position.")]
        public float fallbackDrop = 1.4f;

        private readonly HashSet<ItemPickup> _seen = new HashSet<ItemPickup>();
        // GameObject InstanceIDs already adopted — committed BEFORE the request is sent so a
        // re-scan or a slow/laggy frame can never send the same drop twice (dedup layer 1).
        private readonly HashSet<int> _adopted = new HashSet<int>();
        private uint _dropCounter;
        private float _warmupEnd;
        private float _nextScan;
        private bool _warmedUp;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpNativeDropWatcher]");
            _instance = go.AddComponent<StpNativeDropWatcher>();
            DontDestroyOnLoad(go);
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            ArmWarmup();
        }

        private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        // Re-arm the warmup + clear the seen-set whenever a scene loads, so the items that come
        // in WITH the gameplay scene (Showcase) are recorded fresh and never adopted.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ArmWarmup();

        private void ArmWarmup()
        {
            _warmedUp = false;
            _warmupEnd = Time.unscaledTime + warmupSeconds;
            _seen.Clear();
            _adopted.Clear();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + scanInterval;

            // Only act in the gameplay scene (avoids menu/other scenes).
            if (SceneManager.GetActiveScene().name != gameplayScene)
                return;

            var pickups = FindObjectsByType<ItemPickup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            // Warmup: record everything that already exists; adopt nothing.
            if (!_warmedUp)
            {
                for (int i = 0; i < pickups.Length; i++)
                    _seen.Add(pickups[i]);

                if (Time.unscaledTime >= _warmupEnd)
                    _warmedUp = true;
                return;
            }

            var gm = GameMode.Instance;
            var character = gm != null ? gm.LocalPlayer : null;
            if (character == null)
                return; // no local player → can't judge "near me"; record nothing, retry next scan
            Vector3 localPos = character.transform.position;

            for (int i = 0; i < pickups.Length; i++)
            {
                var p = pickups[i];
                if (p == null)
                    continue;

                int iid = p.gameObject.GetInstanceID();
                if (_adopted.Contains(iid) || _seen.Contains(p))
                    continue;

                // Replicated pickups (Phase 1 / others' drops) carry NetworkItemInstance — even in
                // the 1-frame window before their vendor ItemPickup is destroyed. Never adopt those.
                if (p.GetComponent<NetworkItemInstance>() != null)
                {
                    _seen.Add(p);
                    continue;
                }

                // Only MY fresh native drop: a brand-new vendor pickup that landed next to me.
                if ((p.transform.position - localPos).sqrMagnitude > adoptRadius * adoptRadius)
                {
                    _seen.Add(p); // a far pickup that appeared (e.g. a loot spawner) — record, don't adopt
                    continue;
                }

                var stack = p.AttachedItem;
                if (!stack.HasItem())
                    continue; // DropAction instantiates then AttachItem — retry next scan (don't mark)

                if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                    continue; // can't sync yet; leave it so a later scan retries

                // Commit dedup BEFORE destroying/sending so the same drop can never go twice.
                _adopted.Add(iid);
                _seen.Add(p);

                int defId = stack.Item.Definition.Id;
                int count = Mathf.Max(1, stack.Count);
                long dropId = NextDropId();
                Vector3 grounded = GroundPosition(p.transform.position);
                float yaw = p.transform.eulerAngles.y;

                Destroy(p.gameObject); // immediate (this frame) — closes the double-detection window
                ipc.SendStpDrop(dropId, defId, count, grounded, yaw);
                Debug.Log($"[StpNativeDropWatcher] adopted native drop drop_id={dropId} def_id={defId} x{count} grounded={grounded:F2} → host.");
            }
        }

        // Raycast straight down from the drop point to rest the item on the floor (it spawns frozen
        // at this position on every client, so it must already be grounded — no client-side physics).
        private Vector3 GroundPosition(Vector3 from)
        {
            Vector3 origin = from + Vector3.up * 0.5f;
            if (Physics.Raycast(origin, Vector3.down, out var hit, groundRayDistance,
                    LayerConstants.SimpleSolidObjectsMask, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * groundOffset;

            // No floor found below → approximate feet by lowering a fixed amount.
            return from + Vector3.down * fallbackDrop;
        }

        // Globally-unique per logical drop: NET_ID-prefixed counter, so two clients never collide
        // and the host can dedup (layer 3).
        private long NextDropId()
        {
            int netId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
            return (long)Mathf.Max(1, netId) * 1000000000L + (++_dropCounter);
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
