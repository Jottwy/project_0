using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.World
{
    /// <summary>
    /// TEST / PROTOTYPE TOOL — NOT the authoritative world generator.
    ///
    /// Streams decorative "rooms" on a coarse grid as the player moves, purely to
    /// prototype room placement plus an occupancy/reservation system in the editor.
    ///
    /// Hard boundaries (why this is a fenced test tool, not a core system):
    ///  - World content is authored by the Rust backend (ADR-002). This component
    ///    invents placements client-side and is meant to be replaced by the IPC
    ///    path in Fase 4 (ADR-005). It is never authoritative.
    ///  - Placement is DETERMINISTIC: a pure function of (seed, gridPos), so the
    ///    same seed reproduces the same rooms on every client and every Play
    ///    (CONVENTIONS.md: determinismo). It does NOT use UnityEngine.Random.
    ///  - It does NOT coordinate with ChunkStreamer geometry; rooms may visually
    ///    overlap streamed chunks. The "no overlaps" guarantee applies only within
    ///    this grid and against reservations made via <see cref="ReserveGrid"/>
    ///    (mega-chunks / fixed structures).
    /// </summary>
    public sealed class RoomSpawner : MonoBehaviour
    {
        /// <summary>One authorable room: the prefab plus the world Y it spawns at.</summary>
        [System.Serializable]
        public struct RoomEntry
        {
            public GameObject prefab;
            [Tooltip("World-space Y at which this room spawns (default 0).")]
            public float spawnHeight;
            [Tooltip("Per-axis scale multiplier of the prefab's authored size. Each " +
                     "axis at 0 (default) = unset → original size on that axis; " +
                     "2 = double, 0.5 = half. X/Y/Z scale independently.")]
            public Vector3 spawnScale;
        }

        // Wire-up fields (set by the test harness, matching the ChunkStreamer style).
        public RoomEntry[] rooms;
        public Transform playerTransform;
        [Tooltip("World seed — placement is a pure function of this + gridPos.")]
        public long seed = 42;

        [Header("Grid")]
        [Tooltip("Cell size in metres (independent of the 50 m chunk grid).")]
        public float roomGridSize = 100f;
        [Tooltip("Cells visible in each direction ((2r+1)² total).")]
        public int viewRadius = 1;
        [Range(0f, 1f)]
        [Tooltip("Per-cell probability of a room (deterministic per cell).")]
        public float roomSpawnChance = 0.3f;
        [Tooltip("Log each room spawn — useful when diagnosing \"no rooms appear\".")]
        public bool debugLogging;

        /// <summary>
        /// True only during the frame in which one or more rooms were actually
        /// instantiated. GridTestWorld reads this to re-snapshot destruction zones.
        /// Raised per real Instantiate (not by counting), so a spawn that coincides
        /// with a removal in the same pass is still reported.
        /// </summary>
        public bool SpawnedNewRoomsThisFrame { get; private set; }

        private readonly Dictionary<Vector2Int, GameObject> _activeRooms = new();

        // gridPos → occupant label. "room" = procedural (owned by this spawner);
        // any other label = an external reservation we must never overwrite/free.
        private const string RoomOccupant = "room";
        private readonly Dictionary<Vector2Int, string> _gridOccupancy = new();

        private int _lastGX = int.MinValue;
        private int _lastGZ = int.MinValue;
        private bool _warnedNoRooms;

        private void Start()
        {
            // Fallback for standalone use (component dropped directly in a scene
            // instead of being wired by GridTestWorld): grab the player by tag.
            if (playerTransform == null)
            {
                var playerGo = GameObject.FindWithTag("Player");
                if (playerGo != null) playerTransform = playerGo.transform;
            }
        }

        /// <summary>
        /// Forces the first room-streaming pass around the player immediately.
        /// Call before the chunk streamer builds chunks so the rooms' DestructionZone
        /// trigger volumes exist when the carver snapshots them. Primes the
        /// cell-crossing guard so Update() won't repeat the work the same frame.
        /// </summary>
        public void ForceInitialSpawn()
        {
            if (playerTransform == null) return;
            Vector2Int g = GetGridCoords(playerTransform.position);
            _lastGX = g.x;
            _lastGZ = g.y;
            UpdateRoomStreaming(g);
        }

        private void Update()
        {
            if (playerTransform == null) return;

            // Reset each frame; TrySpawnRoomAtGrid raises it again if a room is
            // actually realised this pass.
            SpawnedNewRoomsThisFrame = false;

            // Only re-stream when the player crosses a cell boundary — same guard
            // ChunkStreamer uses. This is also what kills the per-frame re-roll: a
            // cell is evaluated once per crossing, and the roll is deterministic.
            Vector2Int g = GetGridCoords(playerTransform.position);
            if (g.x == _lastGX && g.y == _lastGZ) return;
            _lastGX = g.x;
            _lastGZ = g.y;

            UpdateRoomStreaming(g);
        }

        private void UpdateRoomStreaming(Vector2Int playerGridPos)
        {
            for (int x = -viewRadius; x <= viewRadius; x++)
            {
                for (int z = -viewRadius; z <= viewRadius; z++)
                {
                    TrySpawnRoomAtGrid(playerGridPos + new Vector2Int(x, z));
                }
            }

            RemoveDistantRooms(playerGridPos);
        }

        private void TrySpawnRoomAtGrid(Vector2Int gridPos)
        {
            if (_activeRooms.ContainsKey(gridPos)) return;    // already realised
            if (_gridOccupancy.ContainsKey(gridPos)) return;  // reserved or occupied
            if (!ShouldSpawn(gridPos)) return;                // deterministic decision

            if (rooms == null || rooms.Length == 0)
            {
                if (!_warnedNoRooms)
                {
                    Debug.LogWarning("[RoomSpawner] No rooms assigned — nothing to spawn.");
                    _warnedNoRooms = true;
                }
                return;
            }

            // Deterministic entry pick (independent stream from the spawn roll).
            int idx = CellRandom(gridPos, salt: 1).Next(rooms.Length);
            RoomEntry entry = rooms[idx];
            if (entry.prefab == null) return; // tolerate empty inspector slots

            // XZ from the grid-cell centre; Y from the per-room spawn height.
            Vector3 worldPos = GridToWorld(gridPos);
            worldPos.y = entry.spawnHeight;
            // Instantiate unparented (world space), then parent preserving the world
            // transform — so the room never inherits a non-1 scale from this parent.
            GameObject room = Instantiate(entry.prefab, worldPos, Quaternion.identity);
            room.transform.SetParent(transform, worldPositionStays: true);

            // Per-room scale, independent per axis. Each axis at 0 (the struct
            // default) means "unset" → keep the prefab's authored size on that axis;
            // a positive value multiplies it (1 = original, 2 = double, 0.5 = half).
            Vector3 s = entry.spawnScale;
            room.transform.localScale = Vector3.Scale(room.transform.localScale, new Vector3(
                s.x > 0f ? s.x : 1f,
                s.y > 0f ? s.y : 1f,
                s.z > 0f ? s.z : 1f));

            _activeRooms[gridPos] = room;
            _gridOccupancy[gridPos] = RoomOccupant;
            SpawnedNewRoomsThisFrame = true;

            if (debugLogging)
                Debug.Log($"[RoomSpawner] Spawned room at grid {gridPos}, height {entry.spawnHeight}");
        }

        /// <summary>
        /// Deterministic spawn decision: a pure function of (seed, gridPos).
        /// Re-evaluating the same cell always yields the same answer, so the
        /// per-frame / return-visit re-roll problem cannot occur.
        /// </summary>
        private bool ShouldSpawn(Vector2Int gridPos)
        {
            return CellRandom(gridPos, salt: 0).NextDouble() < roomSpawnChance;
        }

        // A System.Random deterministically seeded from (worldSeed, gridPos, salt).
        // System.Random — deliberately NOT UnityEngine.Random, which is unseeded
        // global state and would break cross-client / cross-run reproducibility.
        private System.Random CellRandom(Vector2Int gridPos, int salt)
        {
            unchecked
            {
                int h = (int)seed;
                h = h * 31 + (int)(seed >> 32);
                h = h * 31 + gridPos.x;
                h = h * 31 + gridPos.y;
                h = h * 31 + salt;
                return new System.Random(h);
            }
        }

        private Vector2Int GetGridCoords(Vector3 worldPos)
        {
            return new Vector2Int(
                Mathf.FloorToInt(worldPos.x / roomGridSize),
                Mathf.FloorToInt(worldPos.z / roomGridSize));
        }

        private Vector3 GridToWorld(Vector2Int gridPos)
        {
            return new Vector3(
                gridPos.x * roomGridSize + roomGridSize / 2f,
                0f,
                gridPos.y * roomGridSize + roomGridSize / 2f);
        }

        private void RemoveDistantRooms(Vector2Int playerGridPos)
        {
            var toRemove = new List<Vector2Int>();

            foreach (var kvp in _activeRooms)
            {
                // Chebyshev distance to match the square spawn region, plus a
                // 1-cell hysteresis buffer so cells at the edge don't churn.
                int cheby = Mathf.Max(
                    Mathf.Abs(kvp.Key.x - playerGridPos.x),
                    Mathf.Abs(kvp.Key.y - playerGridPos.y));
                if (cheby > viewRadius + 1)
                {
                    Destroy(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var key in toRemove)
            {
                _activeRooms.Remove(key);
                // Free only occupancy we own; never clear an external reservation.
                if (_gridOccupancy.TryGetValue(key, out string occ) && occ == RoomOccupant)
                {
                    _gridOccupancy.Remove(key);
                }
            }
        }

        // ─── Public API: reservations for fixed structures / mega-chunks ───

        /// <summary>
        /// Reserves a grid cell for a fixed structure (mega-chunk, boss room, etc.)
        /// so random rooms never spawn there. No-op if already occupied.
        /// </summary>
        public void ReserveGrid(Vector2Int gridPos, string occupantType)
        {
            if (_gridOccupancy.TryGetValue(gridPos, out string existing))
            {
                Debug.LogWarning($"[RoomSpawner] Grid {gridPos} already occupied by {existing}");
                return;
            }

            _gridOccupancy[gridPos] = occupantType;
        }

        /// <summary>Releases a previously reserved grid cell.</summary>
        public void ReleaseGrid(Vector2Int gridPos)
        {
            _gridOccupancy.Remove(gridPos);
        }

        /// <summary>True if no room or reservation occupies the cell.</summary>
        public bool IsGridFree(Vector2Int gridPos)
        {
            return !_gridOccupancy.ContainsKey(gridPos);
        }

        /// <summary>Converts a world position to room-grid coordinates.</summary>
        public Vector2Int WorldToGridCoords(Vector3 worldPos)
        {
            return GetGridCoords(worldPos);
        }

        /// <summary>Converts room-grid coordinates to the cell-centre world position.</summary>
        public Vector3 GridCoordsToWorld(Vector2Int gridPos)
        {
            return GridToWorld(gridPos);
        }
    }
}
