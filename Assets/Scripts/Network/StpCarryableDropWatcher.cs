using System.Collections.Generic;
using PolymindGames;
using PolymindGames.WieldableSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase B2.5: syncs DROPPED carryables with zero vendor edits. When the player drops a
    /// carried object, the vendor re-activates it in the world as a physics CarryablePickup
    /// (CarryableController.TryDropCarryable → ThrowObject). This watcher detects MY freshly-
    /// dropped carryable, reads its def, DESTROYS it, and asks the host to spawn the replicated
    /// version (SendStpCarryableDrop → process_stp_carryable_drop → stp_carryables → relay →
    /// StpCarryableReplicator). Mirrors <see cref="StpNativeDropWatcher"/>.
    ///
    /// Anti-false-adoption: (1) a warmup seen-set excludes pre-existing/authored carryables;
    /// (2) replicated carryables carry <see cref="NetworkCarryableInstance"/> and are skipped;
    /// (3) a CARRIED carryable is kinematic + parented, a DROPPED one is non-kinematic — only
    /// non-kinematic ones are adopted, so the object in hand is never mistaken for a drop.
    /// </summary>
    public sealed class StpCarryableDropWatcher : MonoBehaviour
    {
        private static StpCarryableDropWatcher _instance;

        [Tooltip("Only adopt carryables within this distance of the local player (a drop lands next to me).")]
        [Min(0.5f)] public float adoptRadius = 4f;

        [Tooltip("Record everything already in the scene for this long before adopting anything.")]
        [Min(0f)] public float warmupSeconds = 2f;

        [Min(0.02f)] public float scanInterval = 0.1f;

        [Tooltip("Down-raycast length to find the floor under a dropped carryable.")]
        [Min(0.5f)] public float groundRayDistance = 6f;
        public float groundOffset = 0.05f;
        public float fallbackDrop = 1.4f;

        private readonly HashSet<CarryablePickup> _seen = new HashSet<CarryablePickup>();
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

            var go = new GameObject("[StpCarryableDropWatcher]");
            _instance = go.AddComponent<StpCarryableDropWatcher>();
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
            _warmupEnd = Time.unscaledTime + warmupSeconds;
            _seen.Clear();
            _adopted.Clear();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + scanInterval;

            var pickups = FindObjectsByType<CarryablePickup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

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
                return;
            Vector3 localPos = character.transform.position;

            for (int i = 0; i < pickups.Length; i++)
            {
                var p = pickups[i];
                if (p == null)
                    continue;

                int iid = p.gameObject.GetInstanceID();
                if (_adopted.Contains(iid) || _seen.Contains(p))
                    continue;

                // Replicated carryables carry NetworkCarryableInstance — never adopt those.
                if (p.GetComponent<NetworkCarryableInstance>() != null)
                {
                    _seen.Add(p);
                    continue;
                }

                // A CARRIED carryable is kinematic (OnPickUp); a DROPPED one is non-kinematic
                // (OnDrop). Only adopt dropped ones, so the object in hand is never adopted.
                var rb = p.Rigidbody;
                if (rb != null && rb.isKinematic)
                    continue; // still carried (or mid-pickup) — leave it, re-check next scan

                // Only MY fresh drop: lands next to me.
                if ((p.transform.position - localPos).sqrMagnitude > adoptRadius * adoptRadius)
                {
                    _seen.Add(p);
                    continue;
                }

                var def = p.Definition;
                if (def == null)
                    continue;

                if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                    continue;

                // Commit dedup BEFORE destroying/sending so the same drop can never go twice.
                _adopted.Add(iid);
                _seen.Add(p);

                int defId = def.Id;
                long dropId = NextDropId();
                Vector3 grounded = GroundPosition(p.transform.position);
                float yaw = p.transform.eulerAngles.y;

                Destroy(p.gameObject);
                ipc.SendStpCarryableDrop(dropId, defId, grounded, yaw);
                Debug.Log($"[StpCarryableDropWatcher] adopted drop drop_id={dropId} def_id={defId} grounded={grounded:F2} → host.");
            }
        }

        // Raycast straight down to rest the carryable on the floor (it spawns frozen at this
        // position on every client, so it must already be grounded — no client-side physics).
        private Vector3 GroundPosition(Vector3 from)
        {
            Vector3 origin = from + Vector3.up * 0.5f;
            if (Physics.Raycast(origin, Vector3.down, out var hit, groundRayDistance,
                    LayerConstants.SimpleSolidObjectsMask, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * groundOffset;

            return from + Vector3.down * fallbackDrop;
        }

        // Globally-unique per logical drop: NET_ID-prefixed counter (host dedups via drop_id).
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
