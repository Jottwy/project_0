using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
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
    ///
    /// Loot-scatter addition: alongside the scene-authored carryables (one-shot, unaffected by the
    /// retry logic below — an authored object is already valid geometry, nothing to retry), the
    /// host ALSO scatters a handful of procedural "resource zone" carryables (Log/Stone/Metal) —
    /// wider, looser spreads than StpItemSpawner's tight item caches, simulating a gathering
    /// patch. These are plain <see cref="StpCarryableSpec"/> entries (no GameObject involved),
    /// sent through the exact same list/registration path as the authored ones —
    /// StpCarryableReplicator can't tell the two apart.
    ///
    /// RETRY (2026-07-06): same reasoning as StpItemSpawner — the client-rendered/collidable
    /// window around the host is only ~150 m (ProceduralWorldGenerator viewRadius=1) and follows
    /// the host as it walks. A zone whose candidate lands outside that window fails the walkable
    /// check at scatter time; it stays PENDING and is retried every RetryIntervalSeconds for up to
    /// RetryWindowSeconds as the host explores. Walkable check mirrors ProxyGroundingHook (raycast
    /// vs. GridChunkBuilder.GeoMask). No hook into ProceduralWorldGenerator/ChunkStreamer.
    /// </summary>
    public sealed class StpCarryableSpawner : MonoBehaviour
    {
        private static StpCarryableSpawner _instance;

        [Min(0f)] public float warmupSeconds = 2f;
        [Min(0.05f)] public float scanInterval = 0.25f;
        [Tooltip("If the host registers no carryables, joiners still clear authored ones after this grace.")]
        [Min(0f)] public float joinerCleanupGrace = 5f;

        // TODO(balance): first-playtest placeholder counts, not tuned. ~4*8=32 carryables max —
        // see STATE.md/session report for the broadcast-size discussion before raising these.
        private const int ZoneCount = 4;
        private const int CarryablesPerZone = 8;
        private const float ZoneMinRadius = 8f;
        // Margin over the confirmed ~150 m render window (viewRadius=1, 3x3 chunks @ 50 m) —
        // same reasoning as StpItemSpawner.ScatterMaxRadius.
        private const float ZoneMaxRadius = 200f;
        private const float ZoneSpreadRadius = 6f; // wider/looser than StpItemSpawner's cache pile
        private const int MaxPlacementAttempts = 12;
        // MUST stay under LAYER_HEIGHT (4 m): with the old value (5), the ray origin started
        // ABOVE the layer's ceiling and landed on its top face / the next layer's floor (logged
        // Y = 4.0 / 8.0 instead of the real -1.0 floor). Mirrors ProxyGroundingHook's validated
        // _rayUp=1/_rayDown=3 — origin below the ceiling, short reach down to the walkable slab.
        private const float RaycastUpOffset = 1f;
        private const float RaycastDownRange = 3f;

        // Retry cadence: same simple Update()-polling this file already used for the authored
        // scan, just kept alive for the procedural zones instead of stopping after one attempt.
        private const float RetryIntervalSeconds = 10f;
        private const float RetryWindowSeconds = 180f; // ~3 minutes total, then give up silently
        // Small settle delay before the FIRST zone attempt, so ipc.LatestState.stpCarryables has
        // round-tripped through the backend and reflects the just-registered authored carryables
        // (else the zone merge could momentarily see an empty/stale list and reuse colliding ids).
        private const float FirstZoneAttemptDelay = 1f;

        // Each zone leans toward one type (a "logging patch" / "quarry" / "scrapyard" feel)
        // rather than a uniform random mix per item — cheap and readable, no extra pooling logic.
        private static readonly string[] CarryableTypesByZone = { "Log", "Stone", "Metal", "Log" };

        private readonly HashSet<CarryablePickup> _authored = new HashSet<CarryablePickup>();
        private float _warmupEnd;
        private float _nextScan;
        private bool _warmedUp;
        private bool _hostSent;
        private bool _joinerCleaned;

        // Pending-zone tracking (procedural scatter only — independent of _hostSent/_authored).
        private int _pendingZoneCount = ZoneCount;
        // Placement-order counter (NOT tied to any original "zone slot" — retries reshuffle which
        // zone lands when) used only to cycle through CarryableTypesByZone for flavour variety.
        private int _zonesConfirmedSoFar;
        private bool _zoneDeadlineArmed;
        private float _nextZoneAttemptAt;
        private float _zoneDeadline;

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
            _pendingZoneCount = ZoneCount;
            _zonesConfirmedSoFar = 0;
            _zoneDeadlineArmed = false;
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
                {
                    if (Camera.main == null)
                        return; // no local camera yet (host not in a playable scene) — retry next scan

                    HostRegisterAuthored(ipc);
                    return;
                }

                // Fase-0 "Opción C": the PROCEDURAL resource-zone scatter is superseded by
                // ChunkLootManager (per-chunk, streaming). The scene-AUTHORED carryable registration
                // above and the joiner cleanup below are NOT loot scatter and stay live regardless.
                // Left intact behind the flag as the A/B baseline; removed in a later prompt.
                if (!ChunkLootManager.Enabled)
                    TryAdvanceZoneScatter(ipc);
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

        /// <summary>One-shot: scans + registers the scene-authored carryables. Unaffected by the
        /// zone-scatter retry logic below — an authored object is already valid, placed geometry,
        /// there is nothing to retry for it.</summary>
        private void HostRegisterAuthored(IPCClient ipc)
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

        /// <summary>
        /// Procedural resource-zone scatter, polled every Update() like the rest of this file.
        /// Zones that fail the walkable check stay pending and are retried every
        /// RetryIntervalSeconds for up to RetryWindowSeconds; each zone seals individually the
        /// moment its centre raycast succeeds (partial fill within a zone — some of its
        /// CarryablesPerZone points failing — does not block sealing the zone, same tolerance
        /// StpItemSpawner already applies per-item within a cache).
        /// </summary>
        private void TryAdvanceZoneScatter(IPCClient ipc)
        {
            if (_pendingZoneCount <= 0)
                return; // every zone placed, or the retry window gave up on the rest

            if (!_zoneDeadlineArmed)
            {
                _zoneDeadlineArmed = true;
                _zoneDeadline = Time.unscaledTime + RetryWindowSeconds;
                _nextZoneAttemptAt = Time.unscaledTime + FirstZoneAttemptDelay;
            }

            if (Time.unscaledTime < _nextZoneAttemptAt)
                return;

            if (Time.unscaledTime > _zoneDeadline)
            {
                Debug.Log($"[StpCarryableSpawner] retry window elapsed with {_pendingZoneCount} zone(s) never finding walkable ground; giving up on them.");
                _pendingZoneCount = 0;
                return;
            }

            _nextZoneAttemptAt = Time.unscaledTime + RetryIntervalSeconds;

            var cam = Camera.main;
            if (cam == null)
                return; // no local camera yet — retry next interval

            var newlyConfirmed = TryConfirmPendingZones(cam.transform.position, ref _pendingZoneCount, ref _zonesConfirmedSoFar);
            if (newlyConfirmed.Count == 0)
                return; // nothing new landed this round — retry again in RetryIntervalSeconds

            SendMergedList(ipc, newlyConfirmed);

            if (_pendingZoneCount <= 0)
                Debug.Log("[StpCarryableSpawner] all resource zones placed.");
        }

        /// <summary>
        /// One fresh random zone-centre candidate per still-pending zone per call. A zone whose
        /// centre fails the walkable check stays pending for the next retry. Returned specs have
        /// <c>id = 0</c> — the caller assigns real ids when merging with the backend's current
        /// authoritative list (see <see cref="SendMergedList"/>).
        /// </summary>
        private static List<StpCarryableSpec> TryConfirmPendingZones(Vector3 hostPos, ref int pendingZoneCount, ref int zonesConfirmedSoFar)
        {
            var confirmed = new List<StpCarryableSpec>();
            int attemptsThisRound = pendingZoneCount;

            for (int z = 0; z < attemptsThisRound; z++)
            {
                if (!TryFindWalkablePoint(hostPos, ZoneMinRadius, ZoneMaxRadius, out Vector3 zoneCenter))
                    continue; // stays pending

                pendingZoneCount--;

                string typeName = CarryableTypesByZone[zonesConfirmedSoFar % CarryableTypesByZone.Length];
                zonesConfirmedSoFar++;
                var def = CarryableDefinition.GetWithName(typeName);
                if (def == null)
                {
                    Debug.LogWarning($"[StpCarryableSpawner] carryable '{typeName}' not found in CarryableDefinition DB; zone skipped.");
                    continue;
                }

                for (int i = 0; i < CarryablesPerZone; i++)
                {
                    if (!TryFindWalkablePoint(zoneCenter, 0f, ZoneSpreadRadius, out Vector3 pos))
                        continue;

                    confirmed.Add(new StpCarryableSpec
                    {
                        id = 0, // assigned by SendMergedList
                        defId = def.Id,
                        position = pos,
                        rotation = Random.Range(0f, 360f)
                    });
                }
            }

            return confirmed;
        }

        /// <summary>
        /// `set_stp_carryables` REPLACES the backend's entire list (game_loop.rs:
        /// `net.stp_carryables = carryables;`), so every send must carry the full current set —
        /// but building that "full current set" from our own static copy would resurrect anything
        /// a player has since picked up. Instead, start from `ipc.LatestState.stpCarryables` (the
        /// live, already pickup-aware mirror of the backend's authoritative list) and append only
        /// the newly confirmed zone entries with fresh ids above the highest id currently known.
        /// </summary>
        private static void SendMergedList(IPCClient ipc, List<StpCarryableSpec> newlyConfirmed)
        {
            var merged = new List<StpCarryableSpec>();
            uint maxKnownId = 0;

            var latest = ipc.LatestState;
            if (latest != null)
            {
                foreach (var c in latest.stpCarryables)
                {
                    merged.Add(new StpCarryableSpec
                    {
                        id = c.id,
                        defId = c.defId,
                        position = c.position,
                        rotation = c.rotation
                    });
                    if (c.id > maxKnownId)
                        maxKnownId = c.id;
                }
            }

            uint nextId = maxKnownId + 1;
            foreach (var spec in newlyConfirmed)
            {
                var s = spec;
                s.id = nextId++;
                merged.Add(s);
            }

            ipc.SendSetStpCarryables(merged);
            Debug.Log($"[StpCarryableSpawner] sent {merged.Count} carryables total ({newlyConfirmed.Count} newly placed this round).");
        }

        /// <summary>
        /// Same walkable check <c>ProxyGroundingHook</c> already uses: raycast down against the
        /// rendered floor's GeoMask. Duplicated (not shared) in StpItemSpawner to keep this change
        /// contained to the two spawner files named in scope.
        /// </summary>
        private static bool TryFindWalkablePoint(Vector3 center, float minRadius, float maxRadius, out Vector3 point)
        {
            for (int attempt = 0; attempt < MaxPlacementAttempts; attempt++)
            {
                float ang = Random.value * Mathf.PI * 2f;
                float dist = Mathf.Lerp(minRadius, maxRadius, Random.value);
                Vector3 candidate = center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * dist;
                Vector3 rayOrigin = candidate + Vector3.up * RaycastUpOffset;
                if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, RaycastUpOffset + RaycastDownRange,
                        GridChunkBuilder.GeoMask, QueryTriggerInteraction.Ignore))
                {
                    point = hit.point;
                    return true;
                }
            }

            point = center;
            return false;
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
