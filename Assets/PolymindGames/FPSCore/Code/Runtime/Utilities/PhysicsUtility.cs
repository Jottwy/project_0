using UnityEngine;

namespace PolymindGames
{
    public static class PhysicsUtility
    {
        public static readonly LayerMask AllLayers = ~int.MinValue;

        private static readonly Collider[] _overlappedColliders = new Collider[64];
        private static readonly RaycastHit[] _raycastHits = new RaycastHit[32];

        public static Ray GenerateRay(Transform transform, float randomSpread, in Vector3 localOffset = default(Vector3))
        {
            Vector3 raySpreadVector = transform.TransformVector(new Vector3(Random.Range(-randomSpread, randomSpread), Random.Range(-randomSpread, randomSpread), 0f));
            Vector3 rayDirection = Quaternion.Euler(raySpreadVector) * transform.forward;

            return new Ray(transform.position + transform.TransformVector(localOffset), rayDirection);
        }

        public static float RaycastOptimizedClosestDistance(Ray ray, float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.DefaultRaycastLayers, Transform ignoredRoot = null, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore)
        {
            int hitCount = Physics.RaycastNonAlloc(ray, _raycastHits, maxDistance, layerMask, triggerInteraction);
            if (hitCount > 0)
            {
                float closestDistance = float.PositiveInfinity;
                bool hasIgnoredRoot = ignoredRoot != null;

                for (int i = 0; i < hitCount; i++)
                {
                    if (hasIgnoredRoot)
                    {
                        // Check if the transform is part of the ignored root.
                        if (_raycastHits[i].transform.IsChildOf(ignoredRoot))
                            continue;
                    }

                    if (_raycastHits[i].distance < closestDistance)
                        closestDistance = _raycastHits[i].distance;
                }

                return closestDistance;
            }

            return float.PositiveInfinity;
        }

        public static int RaycastAllOptimized(Ray ray, float distance, out RaycastHit[] hits,
            int layerMask = Physics.DefaultRaycastLayers, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore)
        {
            int count = Physics.RaycastNonAlloc(ray, _raycastHits, distance, layerMask, triggerInteraction);
            hits = _raycastHits;
            return count;
        }

        public static bool RaycastOptimized(Ray ray, float distance, out RaycastHit hit,
            int layerMask = Physics.DefaultRaycastLayers, Transform ignoredRoot = null, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore)
        {
            var hitCount = Physics.RaycastNonAlloc(ray, _raycastHits, distance, layerMask, triggerInteraction);
            if (hitCount > 0)
            {
                int closestHit = -1;
                float closestDistance = float.PositiveInfinity;
                bool hasIgnoredRoot = ignoredRoot != null;

                for (int i = 0; i < hitCount; i++)
                {
                    if (hasIgnoredRoot)
                    {
                        // Check if the transform is part of the ignored root.
                        if (_raycastHits[i].transform.IsChildOf(ignoredRoot))
                            continue;
                    }

                    if (_raycastHits[i].distance < closestDistance)
                    {
                        closestDistance = _raycastHits[i].distance;
                        closestHit = i;
                    }
                }

                if (closestHit != -1)
                {
                    hit = _raycastHits[closestHit];
                    return true;
                }
            }

            hit = default;
            return false;
        }

        /// <summary>
        /// Performs an optimized sphere cast, ignoring specified root transforms if any hits are found.
        /// </summary>
        /// <param name="ray">The ray to cast from.</param>
        /// <param name="radius">Radius of the sphere for the cast.</param>
        /// <param name="maxDistance">Maximum distance for the cast.</param>
        /// <param name="layerMask">Layer mask to filter collisions.</param>
        /// <param name="ignoredRoot">Root transform to ignore in the results.</param>
        /// <param name="triggerInteraction">Specifies whether to include trigger colliders.</param>
        /// <returns>True if a hit was found, excluding ignored transforms; otherwise, false.</returns>
        public static bool SphereCastOptimized(Ray ray, float radius, float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.DefaultRaycastLayers, Transform ignoredRoot = null, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore)
        {
            int hitCount = Physics.SphereCastNonAlloc(ray, radius, _raycastHits, maxDistance, layerMask, triggerInteraction);
            if (hitCount > 0)
            {
                if (ignoredRoot == null)
                    return true;

                for (int i = 0; i < hitCount; i++)
                {
                    // Check if the transform is part of the ignored root.
                    if (_raycastHits[i].transform.IsChildOf(ignoredRoot))
                        continue;

                    return true;
                }
            }

            return false;
        }
        
        public static int SphereCastAllOptimized(Ray ray, float radius, float distance, out RaycastHit[] hits,
            int layerMask = Physics.DefaultRaycastLayers, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore)
        {
            int count = Physics.SphereCastNonAlloc(ray, radius, _raycastHits, distance, layerMask, triggerInteraction);
            hits = _raycastHits;
            return count;
        }

        /// <summary>
        /// Performs an optimized sphere cast and returns information on the closest hit.
        /// </summary>
        /// <param name="ray">The ray to cast from.</param>
        /// <param name="radius">Radius of the sphere for the cast.</param>
        /// <param name="distance">Maximum distance for the cast.</param>
        /// <param name="hit">Closest hit information if a valid hit was found.</param>
        /// <param name="layerMask">Layer mask to filter collisions.</param>
        /// <param name="ignoredRoot">Root transform to ignore in the results.</param>
        /// <param name="triggerInteraction">Specifies whether to include trigger colliders.</param>
        /// <returns>True if a valid hit was found; otherwise, false.</returns>
        public static bool SphereCastOptimized(Ray ray, float radius, float distance, out RaycastHit hit,
            int layerMask = Physics.DefaultRaycastLayers, Transform ignoredRoot = null, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore)
        {
            var hitCount = Physics.SphereCastNonAlloc(ray, radius, _raycastHits, distance, layerMask, triggerInteraction);
            if (hitCount > 0)
            {
                int closestHit = -1;
                float closestDistance = float.PositiveInfinity;
                bool hasIgnoredRoot = ignoredRoot != null;

                for (int i = 0; i < hitCount; i++)
                {
                    if (hasIgnoredRoot)
                    {
                        // Check if the transform is part of the ignored root.
                        if (_raycastHits[i].transform.IsChildOf(ignoredRoot))
                            continue;
                    }

                    if (_raycastHits[i].distance < closestDistance)
                    {
                        closestDistance = _raycastHits[i].distance;
                        closestHit = i;
                    }
                }

                if (closestHit != -1)
                {
                    hit = _raycastHits[closestHit];
                    return true;
                }
            }

            hit = default(RaycastHit);
            return false;
        }
        
        /// <summary>
        /// Performs an optimized sphere cast and returns the closest hit distance, ignoring specified root transforms.
        /// </summary>
        /// <param name="ray">The ray to cast from.</param>
        /// <param name="radius">Radius of the sphere for the cast.</param>
        /// <param name="maxDistance">Maximum distance for the cast.</param>
        /// <param name="layerMask">Layer mask to filter collisions.</param>
        /// <param name="ignoredRoot">Root transform to ignore in the results.</param>
        /// <param name="triggerInteraction">Specifies whether to include trigger colliders.</param>
        /// <returns>The closest hit distance, or float.PositiveInfinity if no valid hit was found.</returns>
        public static float SphereCastOptimizedClosestDistance(Ray ray, float radius, float maxDistance = float.PositiveInfinity,
            int layerMask = Physics.DefaultRaycastLayers, Transform ignoredRoot = null, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore)
        {
            int hitCount = Physics.SphereCastNonAlloc(ray, radius, _raycastHits, maxDistance, layerMask, triggerInteraction);
            if (hitCount > 0)
            {
                float closestDistance = float.PositiveInfinity;
                bool hasIgnoredRoot = ignoredRoot != null;

                for (int i = 0; i < hitCount; i++)
                {
                    if (hasIgnoredRoot)
                    {
                        // Check if the transform is part of the ignored root.
                        if (_raycastHits[i].transform.IsChildOf(ignoredRoot))
                            continue;
                    }

                    if (_raycastHits[i].distance < closestDistance)
                        closestDistance = _raycastHits[i].distance;
                }

                return closestDistance;
            }

            return float.PositiveInfinity;
        }

        /// <summary>
        /// Performs an optimized box overlap check, returning the colliders in the specified bounds.
        /// </summary>
        /// <param name="bounds">Bounds of the box for the overlap check.</param>
        /// <param name="orientation">Orientation of the box.</param>
        /// <param name="colliders">Array of colliders that overlap with the box.</param>
        /// <param name="layerMask">Layer mask to filter collisions.</param>
        /// <param name="triggerInteraction">Specifies whether to include trigger colliders.</param>
        /// <returns>The number of colliders that overlapped with the box.</returns>
        public static int OverlapBoxOptimized(in Bounds bounds, Quaternion orientation, out Collider[] colliders,
            int layerMask, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore)
        {
            int hitCount = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, _overlappedColliders, orientation, layerMask, triggerInteraction);
            colliders = _overlappedColliders;
            return hitCount;
        }

        /// <summary>
        /// Performs an optimized box overlap check with specified center and extents, returning the colliders within the box.
        /// </summary>
        /// <param name="center">Center of the box for the overlap check.</param>
        /// <param name="extents">Half-size of the box in each dimension.</param>
        /// <param name="orientation">Orientation of the box.</param>
        /// <param name="colliders">Array of colliders that overlap with the box.</param>
        /// <param name="layerMask">Layer mask to filter collisions.</param>
        /// <param name="triggerInteraction">Specifies whether to include trigger colliders.</param>
        /// <returns>The number of colliders that overlapped with the box.</returns>
        public static int OverlapBoxOptimized(Vector3 center, Vector3 extents, Quaternion orientation, out Collider[] colliders,
            int layerMask = Physics.DefaultRaycastLayers, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore)
        {
            int hitCount = Physics.OverlapBoxNonAlloc(center, extents, _overlappedColliders, orientation, layerMask, triggerInteraction);
            colliders = _overlappedColliders;
            return hitCount;
        }

        /// <summary>
        /// Performs an optimized sphere overlap check, returning the colliders within the specified radius.
        /// </summary>
        /// <param name="position">Center of the sphere for the overlap check.</param>
        /// <param name="radius">Radius of the sphere.</param>
        /// <param name="colliders">Array of colliders that overlap with the sphere.</param>
        /// <param name="layerMask">Layer mask to filter collisions.</param>
        /// <param name="triggerInteraction">Specifies whether to include trigger colliders.</param>
        /// <returns>The number of colliders that overlapped with the sphere.</returns>
        public static int OverlapSphereOptimized(Vector3 position, float radius, out Collider[] colliders,
            int layerMask = Physics.DefaultRaycastLayers, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore)
        {
            int hitCount = Physics.OverlapSphereNonAlloc(position, radius, _overlappedColliders, layerMask, triggerInteraction);
            colliders = _overlappedColliders;
            return hitCount;
        }
    }
}