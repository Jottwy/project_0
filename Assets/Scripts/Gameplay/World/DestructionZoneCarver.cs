using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.World
{
    /// <summary>
    /// Client-side "destruction zones" applied AFTER a chunk is built: removes the
    /// built pieces that overlap any trigger collider tagged
    /// <see cref="DefaultZoneTag"/>, carving a clean hole for a handcrafted
    /// mini-structure to sit in without overlapping the procedural geometry.
    ///
    /// IMPORTANT — this never touches the generator. <c>WorldGenerator</c> stays
    /// pure/deterministic and the Rust wire format is unchanged; this only edits the
    /// already-instantiated GameObject hierarchy locally, so the client remains in
    /// sync with the server. Call it from your placement code after spawning a
    /// chunk, e.g. <c>DestructionZoneCarver.Carve(spawnedChunk);</c>.
    ///
    /// Setup: give each zone an empty GameObject + BoxCollider (Is Trigger = ON),
    /// tagged "DestructionZone" (add the tag in the Tags &amp; Layers manager first).
    /// </summary>
    public static class DestructionZoneCarver
    {
        public const string DefaultZoneTag = "DestructionZone";

        /// <summary>Convenience: collect the tagged zones, then carve <paramref name="chunkRoot"/>.</summary>
        public static int Carve(GameObject chunkRoot, string zoneTag = DefaultZoneTag)
            => Carve(chunkRoot, CollectZones(zoneTag));

        /// <summary>
        /// Destroys every renderer-bearing piece under <paramref name="chunkRoot"/>
        /// whose world bounds intersect one of <paramref name="zones"/>. Returns the
        /// number of pieces removed. Pass pre-collected zones when carving many
        /// chunks to avoid re-scanning the scene each call.
        /// </summary>
        public static int Carve(GameObject chunkRoot, IReadOnlyList<Bounds> zones)
        {
            if (chunkRoot == null || zones == null || zones.Count == 0) return 0;

            int removed = 0;
            var renderers = chunkRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;                       // child already destroyed with its parent
                var piece = r.gameObject;
                if (piece == chunkRoot) continue;              // never destroy the root itself
                if (!IntersectsAnyZone(r.bounds, zones)) continue;

                DestroySafe(piece);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// World-space bounds of every active trigger collider tagged
        /// <paramref name="zoneTag"/>. Uses the tag getter (string compare) so it
        /// never throws when the tag is undefined — it just returns an empty list.
        /// </summary>
        public static List<Bounds> CollectZones(string zoneTag = DefaultZoneTag)
        {
            var result = new List<Bounds>();
            var colliders = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
            foreach (var c in colliders)
                if (c.isTrigger && c.gameObject.tag == zoneTag)
                    result.Add(c.bounds);
            return result;
        }

        private static bool IntersectsAnyZone(Bounds b, IReadOnlyList<Bounds> zones)
        {
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].Intersects(b)) return true;
            return false;
        }

        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
