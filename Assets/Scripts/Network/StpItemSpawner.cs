using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using PolymindGames.InventorySystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// HOST-ONLY authoritative loot scatter: generalizes the original Phase 1 "test ring" into a
    /// handful of item caches placed on the rendered, walkable floor around the host's spawn.
    /// Resolves item definitions by NAME at runtime (robust, no hardcoded ids), assigns each a
    /// monotonic network instance id, and registers the list with the backend via the IPC
    /// `set_stp_items` action. The backend relays it to joiners; StpItemReplicator spawns the
    /// actual STP pickups on every instance from world_state.stp_items. Joiners never decide
    /// items themselves (and the backend ignores set_stp_items from a non-host, so a joiner can't
    /// diverge the world even if this runs).
    ///
    /// "Walkable" is checked the same way <c>ProxyGroundingHook</c> already grounds proxies: a
    /// raycast against the rendered floor's <see cref="GridChunkBuilder.GeoMask"/>. Neither this
    /// class nor StpCarryableSpawner had their own walkable-query before this change; this is the
    /// only existing ground-truth mechanism in the codebase, reused rather than reading grid_gen
    /// cell data directly (which would mean touching worldgen — out of scope here).
    ///
    /// RETRY (2026-07-06): the client-rendered/collidable window around the host is only ~150 m
    /// (ProceduralWorldGenerator viewRadius=1 → 3x3 chunks of 50 m) and it FOLLOWS the host as it
    /// walks, not the spawn point. A one-shot scatter at scene-load time would silently fail any
    /// cache whose random candidate lands outside that small initial window. Caches that fail the
    /// walkable check stay PENDING (not discarded) and are retried every <see cref="RetryIntervalSeconds"/>
    /// for up to <see cref="RetryWindowSeconds"/>, so caches further out get a real chance to land
    /// as the host explores and more chunks stream in. Each cache seals individually the moment its
    /// raycast succeeds — the run does not wait for every cache before sending anything.
    /// No hook into ProceduralWorldGenerator/ChunkStreamer: purely polling the same raycast.
    /// </summary>
    public sealed class StpItemSpawner : MonoBehaviour
    {
        private static StpItemSpawner _instance;

        [Tooltip("Only spawn once the host is in this scene (avoids placing items in the menu).")]
        public string gameplayScene = "STP_Showcase";

        [Tooltip("Seconds to wait after scene load before the first scatter attempt (lets the chunks around the host stream in).")]
        [Min(0f)] public float warmupSeconds = 2f;

        // TODO(balance): first-playtest placeholder counts, not tuned. ~6*6=36 items max — see
        // STATE.md/session report for the broadcast-size discussion before raising these.
        private const int CacheCount = 6;
        private const int ItemsPerCache = 6;
        private const float CacheClusterRadius = 1.5f;
        private const float ScatterMinRadius = 5f;
        // Margin over the confirmed ~150 m render window (viewRadius=1, 3x3 chunks @ 50 m) — a
        // candidate can land just past the window's edge and still get picked up on a later retry
        // once the host walks a bit closer.
        private const float ScatterMaxRadius = 200f;
        private const float WeaponRollChance = 0.15f;
        // Placement attempts + ray geometry live in LootPlacement (shared with the carryable and
        // chest spawners); the ray origin MUST stay under LAYER_HEIGHT — see the note there.

        // Retry cadence: same simple Update()-polling this file already used, just kept alive
        // past the first attempt instead of stopping at _sent=true.
        private const float RetryIntervalSeconds = 10f;
        private const float RetryWindowSeconds = 180f; // ~3 minutes total, then give up silently

        // "Almond Water" is deliberately NOT here (ADR-030 amendment) — chest-only by design, see
        // the exception note on StpChestSpawner.ConsumablePool. Not an oversight.
        private static readonly string[] ConsumablePool =
        {
            "Apple", "Cooked Meat", "Raw Meat", "Energy Bar", "Small Food Can", "Large Food Can", "Water Bottle"
        };
        private static readonly string[] MedicalPool = { "Antibiotics", "Medicinal Corn" };
        private static readonly string[] AmmoPool = { "30-30 Bullet", "Wooden Arrow", "Stone Arrow", "Metal Arrow" };
        private static readonly string[] MaterialPool =
        {
            "Stick", "Rope", "Cloth", "Leather", "Metal Shard", "Stone Shard", "Feather", "Duct Tape", "Wooden Torch"
        };
        // ADR-029: the same 8 real weapon ids confirmed in the PvP allowlist session, entered as
        // rarer/"better" loot (WeaponRollChance) rather than the common pools above.
        private static readonly string[] WeaponPool =
        {
            "Marlin 336", "Wooden Bow", "Bone Club", "Hunting Axe", "Hunting Knife", "Steel Pickaxe", "Stone Spear", "Wooden Spear"
        };

        private float _warmupEnd;
        private bool _warmedUp;

        // Pending-cache tracking (replaces the old single-shot bool): how many of the CacheCount
        // caches have NOT yet found walkable ground. Reaches 0 either because every cache
        // succeeded, or because the retry window elapsed and the remainder were given up on.
        private int _pendingCacheCount = CacheCount;
        private float _nextAttemptAt;
        private float _retryDeadline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpItemSpawner]");
            _instance = go.AddComponent<StpItemSpawner>();
            DontDestroyOnLoad(go);
        }

        private void OnEnable()
        {
            _warmupEnd = Time.unscaledTime + warmupSeconds;
        }

        private void Update()
        {
            // Fase-0 "Opción C": when ChunkLootManager governs, it authors all procedural item loot
            // (per-chunk, streaming). This whole spawner is one-shot scatter, so it stands down
            // entirely. Left INTACT behind the flag as the A/B baseline; removed in a later prompt.
            if (ChunkLootManager.Enabled)
                return;

            if (_pendingCacheCount <= 0)
                return; // every cache placed, or the retry window gave up on the rest — done forever

            var init = NetworkInitializer.Instance;
            if (init == null || init.CurrentRole != NetworkInitializer.Role.Host)
                return;

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            if (SceneManager.GetActiveScene().name != gameplayScene)
                return;

            if (!_warmedUp)
            {
                if (Time.unscaledTime < _warmupEnd)
                    return; // let the chunks around the host stream in first
                _warmedUp = true;
                _retryDeadline = Time.unscaledTime + RetryWindowSeconds;
                _nextAttemptAt = Time.unscaledTime; // allow an immediate first attempt
            }

            if (Time.unscaledTime < _nextAttemptAt)
                return;

            if (Time.unscaledTime > _retryDeadline)
            {
                Debug.Log($"[StpItemSpawner] retry window elapsed with {_pendingCacheCount} cache(s) never finding walkable ground; giving up on them.");
                _pendingCacheCount = 0;
                return;
            }

            _nextAttemptAt = Time.unscaledTime + RetryIntervalSeconds;

            var cam = Camera.main;
            if (cam == null)
                return; // no local camera yet — retry next interval

            var newlyConfirmed = TryConfirmPendingCaches(cam.transform.position, ref _pendingCacheCount);
            if (newlyConfirmed.Count == 0)
                return; // nothing new landed this round — retry again in RetryIntervalSeconds

            SendMergedList(ipc, newlyConfirmed);

            if (_pendingCacheCount <= 0)
                Debug.Log("[StpItemSpawner] all caches placed.");
        }

        /// <summary>
        /// Attempts to find walkable ground for up to <paramref name="pendingCacheCount"/> still-
        /// pending caches (one fresh random candidate per cache per call — a cache that fails
        /// simply stays pending for the next retry). Returns the newly-generated item specs for
        /// whichever caches succeeded THIS call and decrements <paramref name="pendingCacheCount"/>
        /// per success. Returned specs have <c>id = 0</c> — the caller assigns real ids when
        /// merging with the backend's current authoritative list (see <see cref="SendMergedList"/>).
        /// </summary>
        private static List<StpItemSpec> TryConfirmPendingCaches(Vector3 hostPos, ref int pendingCacheCount)
        {
            var confirmed = new List<StpItemSpec>();
            int attemptsThisRound = pendingCacheCount;

            for (int c = 0; c < attemptsThisRound; c++)
            {
                if (!LootPlacement.TryFindWalkablePoint(hostPos, ScatterMinRadius, ScatterMaxRadius, out Vector3 cacheCenter))
                    continue; // stays pending

                pendingCacheCount--;

                for (int i = 0; i < ItemsPerCache; i++)
                {
                    string name = RollItemName();
                    var def = ItemDefinition.GetWithName(name);
                    if (def == null)
                    {
                        Debug.LogWarning($"[StpItemSpawner] item '{name}' not found in ItemDefinition DB; skipped.");
                        continue;
                    }

                    // Items within one cache share the cache's grounded Y (no per-item raycast) —
                    // cheap and good enough for a tight 1.5 m pile; not exact terrain-following.
                    Vector2 offset = Random.insideUnitCircle * CacheClusterRadius;
                    Vector3 itemPos = cacheCenter + new Vector3(offset.x, 0f, offset.y);

                    confirmed.Add(new StpItemSpec
                    {
                        id = 0, // assigned by SendMergedList
                        defId = def.Id,
                        count = 1,
                        position = itemPos,
                        rotation = Random.Range(0f, 360f)
                    });
                }
            }

            return confirmed;
        }

        /// <summary>
        /// `set_stp_items` REPLACES the backend's entire list (game_loop.rs: `net.stp_items =
        /// items;`), so every send must carry the full current set — but building that "full
        /// current set" from OUR OWN static copy would resurrect anything a player has since
        /// picked up. Instead, start from `ipc.LatestState.stpItems` (the live, already
        /// pickup-aware mirror of the backend's authoritative list — the host's own Unity
        /// receives its own WorldState broadcast just like a joiner) and append only the newly
        /// confirmed caches with fresh ids above the highest id currently known.
        /// </summary>
        private static void SendMergedList(IPCClient ipc, List<StpItemSpec> newlyConfirmed)
        {
            var merged = new List<StpItemSpec>();
            uint maxKnownId = 0;

            var latest = ipc.LatestState;
            if (latest != null)
            {
                foreach (var it in latest.stpItems)
                {
                    merged.Add(new StpItemSpec
                    {
                        id = it.id,
                        defId = it.defId,
                        count = it.count,
                        position = it.position,
                        rotation = it.rotation
                    });
                    if (it.id > maxKnownId)
                        maxKnownId = it.id;
                }
            }

            uint nextId = maxKnownId + 1;
            foreach (var spec in newlyConfirmed)
            {
                var s = spec;
                s.id = nextId++;
                merged.Add(s);
            }

            ipc.SendSetStpItems(merged);
            Debug.Log($"[StpItemSpawner] sent {merged.Count} STP items total ({newlyConfirmed.Count} newly placed this round).");
        }

        private static string RollItemName()
        {
            if (Random.value < WeaponRollChance)
                return WeaponPool[Random.Range(0, WeaponPool.Length)];

            // Even split across the three common pools, then a random entry within it.
            int poolPick = Random.Range(0, 3);
            string[] pool = poolPick switch
            {
                0 => ConsumablePool,
                1 => MedicalPool,
                _ => Random.value < 0.5f ? AmmoPool : MaterialPool,
            };
            return pool[Random.Range(0, pool.Length)];
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
