using System;
using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Build/rebuild/destroy policy for streamed chunk visuals.
    ///
    /// Reconcile() is data-level only: it diffs the snapshot's visible chunks
    /// against the pool and queues work. ProcessQueues() executes that work
    /// under a per-frame budget, so a spawn burst (~49 chunks) or a boundary
    /// crossing (~7-13 chunks) never builds everything in a single frame —
    /// that synchronous burst (CreatePrimitive storms + CombineMeshes per
    /// chunk) was the FPS collapse.
    ///
    /// A chunk is rebuilt only when its STRUCTURE changes (template, floor
    /// profile, vertical flags, layer, layout cells/edges, volumetric grid) —
    /// never from volatile snapshot fields (state tint, entities, items),
    /// so existing chunks survive every 10hz snapshot untouched.
    /// </summary>
    public sealed class ChunkVisualLifecycle
    {
        private readonly Dictionary<long, GameObject> _pool = new Dictionary<long, GameObject>();
        private readonly Dictionary<long, int> _signatures = new Dictionary<long, int>();
        private readonly HashSet<long> _alive = new HashSet<long>();

        // Pending work. _pendingBuilds always holds the LATEST ChunkViewMsg for
        // a queued key (refreshed each snapshot), so a chunk that teleports
        // while queued is built from current data, never stale data.
        private readonly Dictionary<long, ChunkViewMsg> _pendingBuilds = new Dictionary<long, ChunkViewMsg>();
        private readonly Queue<long> _buildOrder = new Queue<long>();
        private readonly Queue<long> _destroyQueue = new Queue<long>();
        private readonly HashSet<long> _queuedDestroys = new HashSet<long>();

        // Scratch buffers (reused to avoid per-snapshot allocations).
        private readonly List<(long key, int distance)> _newBuilds = new List<(long, int)>();
        private readonly List<long> _droppedPending = new List<long>();

        public int PoolCount => _pool.Count;
        public int PendingBuilds => _pendingBuilds.Count;
        public int PendingDestroys => _queuedDestroys.Count;

        // Accumulate across frames until ResetCounters() (once-per-second log).
        public int Built { get; private set; }
        public int Rebuilt { get; private set; }
        public int Destroyed { get; private set; }

        // Last ProcessQueues() call only.
        public int BuiltThisFrame { get; private set; }
        public int RebuiltThisFrame { get; private set; }
        public int DestroyedThisFrame { get; private set; }

        public void ResetCounters()
        {
            Built = 0;
            Rebuilt = 0;
            Destroyed = 0;
        }

        public static long Key(int x, int layer, int z)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                h = (h ^ x) * 1099511628211L;
                h = (h ^ layer) * 1099511628211L;
                h = (h ^ z) * 1099511628211L;
                return h;
            }
        }

        /// <summary>
        /// Stable structural signature. layoutCells already carries the packed
        /// cell grid plus both edge arrays (cells, edges_v, edges_h), so one
        /// content hash covers cells and edge walls. Volatile fields (state,
        /// entities, items, positions) are deliberately excluded.
        /// </summary>
        public static int StructuralSignature(ChunkViewMsg cv)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + cv.templateId;
                h = h * 31 + cv.floorProfile;
                h = h * 31 + cv.verticalFlags;
                h = h * 31 + cv.layer;
                h = h * 31 + cv.layoutGridSize;
                h = h * 31 + ContentHash(cv.layoutCells);
                h = h * 31 + (cv.HasVolumetricGrid ? 1 : 0);

                if (cv.HasVolumetricGrid)
                {
                    var g = cv.volumetricGrid;
                    h = h * 31 + (g.source != null ? g.source.GetHashCode() : 0);
                    h = h * 31 + g.columnId.GetHashCode();
                    h = h * 31 + g.nx;
                    h = h * 31 + g.ny;
                    h = h * 31 + g.nz;
                    h = h * 31 + (g.cells != null ? g.cells.Length : 0);
                    h = h * 31 + (g.faces != null ? g.faces.Count : 0);
                    h = h * 31 + (g.verticalAccess != null ? g.verticalAccess.Count : 0);
                    h = h * 31 + g.openCellCount;
                    h = h * 31 + g.solidCellCount;
                    h = h * 31 + g.verticalConnectionCount;
                    h = h * 31 + g.validVerticalOpeningCount;
                    h = h * 31 + (g.atriumSpan ? 1 : 0);
                }

                return h;
            }
        }

        // FNV-1a over the packed layout (cells + vertical edges + horizontal edges).
        private static int ContentHash(ushort[] data)
        {
            unchecked
            {
                if (data == null)
                    return 0;
                uint h = 2166136261u;
                for (int i = 0; i < data.Length; i++)
                {
                    h = (h ^ data[i]) * 16777619u;
                }
                return (int)h;
            }
        }

        /// <summary>
        /// Data-level reconcile against a new snapshot: queue missing chunks
        /// for build (nearest to the player first), queue structural rebuilds,
        /// queue stale chunks for destruction, cancel work that the snapshot
        /// made obsolete. Never builds or destroys here.
        /// </summary>
        public void Reconcile(List<ChunkViewMsg> visibleChunks, int playerChunkX, int playerChunkZ)
        {
            _alive.Clear();
            _newBuilds.Clear();

            foreach (var cv in visibleChunks)
            {
                long key = Key(cv.pos[0], cv.layer, cv.pos[1]);
                _alive.Add(key);

                // Chunk came back into range while waiting to be destroyed:
                // cancel the destroy, keep the existing GameObject.
                _queuedDestroys.Remove(key);

                int signature = StructuralSignature(cv);

                if (_pool.ContainsKey(key))
                {
                    if (_signatures.TryGetValue(key, out int previous) && previous == signature)
                    {
                        // Structure unchanged. Cancel a queued rebuild if the
                        // structure flipped back before we got to it.
                        _pendingBuilds.Remove(key);
                    }
                    else
                    {
                        // Structural change → queue rebuild with latest data.
                        if (!_pendingBuilds.ContainsKey(key))
                            _buildOrder.Enqueue(key);
                        _pendingBuilds[key] = cv;
                    }
                }
                else if (_pendingBuilds.ContainsKey(key))
                {
                    // Already queued — refresh to the latest snapshot data.
                    _pendingBuilds[key] = cv;
                }
                else
                {
                    int dx = Mathf.Abs(cv.pos[0] - playerChunkX);
                    int dz = Mathf.Abs(cv.pos[1] - playerChunkZ);
                    _newBuilds.Add((key, Mathf.Max(dx, dz)));
                    _pendingBuilds[key] = cv;
                }
            }

            // New chunks build nearest-first so the ground under the player
            // appears before the horizon does.
            _newBuilds.Sort((a, b) => a.distance - b.distance);
            foreach (var (key, _) in _newBuilds)
                _buildOrder.Enqueue(key);

            // Pending builds for chunks that left the visible set: drop them.
            _droppedPending.Clear();
            foreach (var key in _pendingBuilds.Keys)
            {
                if (!_alive.Contains(key))
                    _droppedPending.Add(key);
            }
            foreach (long key in _droppedPending)
                _pendingBuilds.Remove(key);

            // Pooled chunks that left the visible set: queue for destruction.
            foreach (var kv in _pool)
            {
                if (!_alive.Contains(kv.Key) && _queuedDestroys.Add(kv.Key))
                    _destroyQueue.Enqueue(kv.Key);
            }
        }

        /// <summary>
        /// Execute queued work under per-frame budgets. Destroys run first
        /// (cheap, frees memory); builds are the expensive part and are capped
        /// at <paramref name="buildBudget"/> per call.
        /// </summary>
        public void ProcessQueues(int buildBudget, int destroyBudget, Func<ChunkViewMsg, GameObject> build)
        {
            BuiltThisFrame = 0;
            RebuiltThisFrame = 0;
            DestroyedThisFrame = 0;

            while (destroyBudget > 0 && _destroyQueue.Count > 0)
            {
                long key = _destroyQueue.Dequeue();
                if (!_queuedDestroys.Remove(key))
                    continue; // canceled — chunk came back into range
                if (_pool.TryGetValue(key, out var go))
                {
                    if (go != null)
                        UnityEngine.Object.Destroy(go);
                    _pool.Remove(key);
                    _signatures.Remove(key);
                    Destroyed++;
                    DestroyedThisFrame++;
                    destroyBudget--;
                }
            }

            while (buildBudget > 0 && _buildOrder.Count > 0)
            {
                long key = _buildOrder.Dequeue();
                if (!_pendingBuilds.TryGetValue(key, out var cv))
                    continue; // canceled or already handled
                _pendingBuilds.Remove(key);

                bool rebuild = _pool.TryGetValue(key, out var existing);
                if (rebuild && existing != null)
                    UnityEngine.Object.Destroy(existing);

                _pool[key] = build(cv);
                _signatures[key] = StructuralSignature(cv);

                if (rebuild)
                {
                    Rebuilt++;
                    RebuiltThisFrame++;
                }
                else
                {
                    Built++;
                    BuiltThisFrame++;
                }
                buildBudget--;
            }
        }

        /// <summary>Destroy every pooled chunk and drop queued work (teardown).</summary>
        public void DestroyAll()
        {
            foreach (var go in _pool.Values)
            {
                if (go != null)
                    UnityEngine.Object.Destroy(go);
            }
            _pool.Clear();
            _signatures.Clear();
            _pendingBuilds.Clear();
            _buildOrder.Clear();
            _destroyQueue.Clear();
            _queuedDestroys.Clear();
        }
    }
}
