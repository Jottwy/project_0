using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Zone-kind wiring, Pieza 1 — client-side registry of the backend's per-chunk
    /// zone_kind, keyed by XZ chunk coordinate (zone_kind is assigned per-structure/
    /// template, independent of vertical layer).
    ///
    /// Reads <see cref="WorldStateMsg.visibleChunks"/> — the same live snapshot
    /// <see cref="BackroomsSurvival.UI.PoiDebugHud"/> already reads via
    /// <c>IPCClient.LatestState</c> — NOT <see cref="ChunkVisualLifecycle"/> (owned by
    /// <see cref="ChunkRenderer"/>, which is disabled in GameBootstrap; the live
    /// renderer, ChunkStreamer, streams grid_gen geometry independently of this data).
    /// Self-bootstraps a pump GameObject so callers don't need to wire anything up.
    /// </summary>
    public static class ZoneRegistry
    {
        private static readonly Dictionary<(int cx, int cz), byte> _zoneByChunk =
            new Dictionary<(int, int), byte>();

        private static GameObject _pump;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _zoneByChunk.Clear();
            _pump = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_pump != null)
                return;
            _pump = new GameObject("ZoneRegistryPump");
            Object.DontDestroyOnLoad(_pump);
            _pump.AddComponent<ZoneRegistryPump>();
        }

        /// <summary>Last known zone_kind for chunk (cx, cz); false if never seen.</summary>
        public static bool TryGetZone(int cx, int cz, out byte zoneKind) =>
            _zoneByChunk.TryGetValue((cx, cz), out zoneKind);

        /// <summary>Diagnostic only — Pieza 2/3 should use TryGetZone, not the raw count.</summary>
        public static int KnownChunkCount => _zoneByChunk.Count;

        internal static void Refresh(WorldStateMsg state)
        {
            if (state?.visibleChunks == null)
                return;

            foreach (var cv in state.visibleChunks)
            {
                // Pieza 3 fix: visibleChunks carries every loaded layer of a column, and the
                // backend orders its views by (x, layer, z) — so without this filter the LAST
                // write for a given (cx,cz) is whichever layer sorts highest, not layer 0 where
                // the player actually walks. Multi-layer V30A columns (the spawn cluster is one)
                // were silently keying zone_kind off their TOP layer. TryGetZone never returned
                // false for these — the value wasn't missing, it was wrong — so no existing gate
                // (ZoneWaitTimeout, ChunkLootManager's zone gate) could have caught it.
                if (cv.layer != 0)
                    continue;
                _zoneByChunk[(cv.pos[0], cv.pos[1])] = (byte)cv.zoneKind;
            }
        }
    }

    /// <summary>Drives ZoneRegistry.Refresh() every frame from the live IPC snapshot.</summary>
    internal sealed class ZoneRegistryPump : MonoBehaviour
    {
        private void Update()
        {
            if (IPCClient.TryGetInstance(out var ipc) && ipc.LatestState != null)
                ZoneRegistry.Refresh(ipc.LatestState);
        }
    }
}
