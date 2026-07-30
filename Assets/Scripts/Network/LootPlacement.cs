using BackroomsSurvival.Gameplay.GridWorld;
using UnityEngine;
using Random = UnityEngine.Random;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Shared ground placement for the world-loot spawners (items, carryables, chests).
    /// The three of them carried byte-identical copies of this search and of its three tuning
    /// constants. That duplication is exactly what produced the floating-loot bug fixed on
    /// 2026-07-07: the ray used to start ABOVE the 4 m ceiling and hit the wrong surface, and the
    /// fix (up 5 to 1, down 10 to 3) had to be applied three times by hand. One copy now.
    /// Mirrors the validated ProxyGroundingHook ray geometry (up 1 / down 3): the origin always
    /// sits below the layer ceiling.
    /// </summary>
    internal static class LootPlacement
    {
        private const int MaxPlacementAttempts = 12;
        private const float RaycastUpOffset = 1f;
        private const float RaycastDownRange = 3f;

        /// Rejection-samples a point on a ring around <paramref name="center"/> that has real
        /// rendered geometry under it. Returns false (and <paramref name="point"/> = center) when
        /// no attempt lands on the floor.
        internal static bool TryFindWalkablePoint(Vector3 center, float minRadius, float maxRadius, out Vector3 point)
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
    }
}
