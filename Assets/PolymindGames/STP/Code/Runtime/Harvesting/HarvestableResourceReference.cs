using UnityEngine;
using System;

namespace PolymindGames.ResourceHarvesting
{
    /// <summary>
    /// A readonly struct that encapsulates either an <see cref="IHarvestableResource"/> or an <see cref="IHarvestableResourcesHandler"/>.
    /// Provides methods to interact with either the individual resource or the handler managing multiple resources.
    /// </summary>
    public readonly struct HarvestableResourceReference : IEquatable<HarvestableResourceReference>
    {
        private readonly IHarvestableResourcesHandler _handler;
        private readonly IHarvestableResource _resource;
        private readonly int _resourceIndex;

        /// <summary>
        /// Initializes a new instance of the <see cref="HarvestableResourceReference"/> struct with a resource.
        /// </summary>
        /// <param name="resource">The harvestable resource instance.</param>
        public HarvestableResourceReference(IHarvestableResource resource)
        {
            _resourceIndex = -1;
            _resource = resource;
            _handler = null;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HarvestableResourceReference"/> struct with a handler.
        /// </summary>
        /// <param name="handler">The harvestable resources handler instance.</param>
        /// <param name="resourceIndex"></param>
        public HarvestableResourceReference(IHarvestableResourcesHandler handler, int resourceIndex)
        {
            _resourceIndex = resourceIndex;
            _handler = handler;
            _resource = null;
        }

        public static HarvestableResourceReference Create(GameObject gameObject, in Vector3 position, float radius)
        {
            if (gameObject.TryGetComponent(out IHarvestableResource resource))
            {
                return new HarvestableResourceReference(resource);
            }

            if (gameObject.TryGetComponent(out IHarvestableResourcesHandler handler))
            {
                int resourceIndex = handler.FindResourceIndex(position, radius);
                if (resourceIndex != -1)
                {
                    return new HarvestableResourceReference(handler, resourceIndex);
                }
            }

            return default(HarvestableResourceReference);
        }

        /// <summary>
        /// Indicates whether the context contains a valid resource or handler.
        /// </summary>
        public bool IsValid => _resource != null || _handler != null;

        /// <summary>
        /// Indicates whether the context contains a valid resource.
        /// </summary>
        public bool HasResource => _resource != null;

        /// <summary>
        /// Indicates whether the context contains a valid handler.
        /// </summary>
        public bool HasHandler => _handler != null;

        /// <summary>
        /// Gets the resource type.
        /// </summary>
        public HarvestableResourceType ResourceType => GetResourceDefinition()?.ResourceType ?? HarvestableResourceType.None;

        /// <summary>
        /// Retrieves the bounds where harvesting can occur based on the resource or handler.
        /// </summary>
        /// <returns>The bounding box of the harvestable area.</returns>
        public Bounds GetHarvestBounds()
        {
            if (HasResource)
            {
                return _resource.GetHarvestBounds();
            }

            if (HasHandler)
            {
                return _handler.GetHarvestBoundsAt(_resourceIndex);
            }

            throw new InvalidOperationException("No valid resource or handler available.");
        }

        /// <summary>
        /// Retrieves the definition of the harvestable resource, either directly from a resource or at a specific position for a handler.
        /// </summary>
        /// <returns>The resource definition if one exists; otherwise, null.</returns>
        public HarvestableResourceDefinition GetResourceDefinition()
        {
            if (HasResource)
            {
                return _resource.ResourceDefinition;
            }

            if (HasHandler)
            {
                return _handler.GetResourceDefinitionAt(_resourceIndex);
            }

            throw new InvalidOperationException("No valid resource or handler available.");
        }

        /// <summary>
        /// Retrieves the remaining harvest amount of the resource, either directly from a resource or through a handler.
        /// </summary>
        /// <returns>The remaining harvest amount as a float between 0 and 1.</returns>
        public float GetRemainingHarvestAmount()
        {
            if (HasResource)
            {
                return _resource.RemainingHarvestAmount;
            }

            if (HasHandler)
            {
                return 1f;
            }

            throw new InvalidOperationException("No valid resource or handler available.");
        }

        /// <summary>
        /// Determines if the resource or handler can be harvested based on tool proficiencies and strength.
        /// </summary>
        /// <returns>True if the resource can be harvested; otherwise, false.</returns>
        public bool CanHarvestAt(float harvestPower, Vector3 worldPosition)
        {
            if (HasResource)
            {
                return _resource.CanBeHarvested(harvestPower, worldPosition);
            }

            if (HasHandler)
            {
                return _handler.CanHarvestAt(harvestPower, worldPosition);
            }

            throw new InvalidOperationException("No valid resource or handler available.");
        }

        /// <summary>
        /// Determines if the resource or handler can be harvested using a raycast for a specific position.
        /// </summary>
        /// <returns>True if the resource can be harvested at the raycast position; otherwise, false.</returns>
        public bool CanHarvestAt(float harvestPower, Ray ray, Vector3 worldPosition, out Vector3 adjustedWorldPosition)
        {
            if (HasResource)
            {
                if (_resource.CanBeHarvested(harvestPower, worldPosition))
                {
                    adjustedWorldPosition = worldPosition;
                    return true;
                }

                return IsRayWithinHarvestBounds(_resource.GetHarvestBounds(), _resource.transform.rotation, ray, out adjustedWorldPosition)
                    && _resource.CanBeHarvested(harvestPower, adjustedWorldPosition);
            }

            if (HasHandler)
            {
                if (_handler.CanHarvestAt(harvestPower, worldPosition))
                {
                    adjustedWorldPosition = worldPosition;
                    return true;
                }

                return IsRayWithinHarvestBounds(_handler.GetHarvestBoundsAt(_resourceIndex), Quaternion.identity, ray, out adjustedWorldPosition)
                    && _handler.CanHarvestAt(harvestPower, adjustedWorldPosition);
            }

            throw new InvalidOperationException("No valid resource or handler available.");
        }

        /// <summary>
        /// Checks if the ray intersects with the provided harvest bounds and determines if the hit point is in the front or back half
        /// of the bounds, ignoring the Y axis (XZ plane comparison).
        /// </summary>
        /// <returns>True if the ray intersects the bounds and the hit point is in the front half (closer to the ray origin than the bounds' center); otherwise, false.</returns>
        private static bool IsRayWithinHarvestBounds(Bounds bounds, Quaternion rotation, Ray ray, out Vector3 hitPoint)
        {
            // Check if the ray intersects the rotated bounds
            if (bounds.RayIntersectsRotatedBounds(rotation, bounds.center, ray, out float hitDistance))
            {
                // Calculate the hit point based on the ray intersection distance
                hitPoint = ray.GetPoint(hitDistance + 0.001f);

                // Ignore the Y-axis, compare positions in the XZ plane
                Vector3 rayXZ = ray.origin.WithY(0f);
                Vector3 hitXZ = hitPoint.WithY(0f);
                Vector3 boundsCenterXZ = bounds.center.WithY(0f);

                // Return true if the hit point is in the front half of the bounds
                return Vector3.Distance(rayXZ, hitXZ) < Vector3.Distance(rayXZ, boundsCenterXZ);
            }

            // No intersection found, set default value for hitPoint and return false
            hitPoint = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Attempts to harvest the resource or resources based on the tool's proficiencies and strength.
        /// </summary>
        /// <returns>True if the harvesting was successful; otherwise, false.</returns>
        public bool TryHarvest(float harvestPower, float harvestAmount, in DamageArgs args)
        {
            if (HasResource)
            {
                return _resource.TryHarvest(harvestPower, harvestAmount, in args);
            }

            if (HasHandler)
            {
                return _handler.TryHarvestAt(harvestPower, harvestAmount, in args);
            }

            throw new InvalidOperationException("No valid resource or handler available.");
        }

        public static bool operator ==(HarvestableResourceReference left, HarvestableResourceReference right)
            => left.Equals(right);

        public static bool operator !=(HarvestableResourceReference left, HarvestableResourceReference right)
            => !(left == right);

        public override bool Equals(object obj)
        {
            if (obj is HarvestableResourceReference other)
                return Equals(other);

            return false;
        }

        public bool Equals(HarvestableResourceReference other)
            => Equals(_resource, other._resource) && Equals(_handler, other._handler) && Equals(_resourceIndex, other._resourceIndex);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + (_resource != null ? _resource.GetHashCode() : 0);
                hash = hash * 23 + (_handler != null ? _handler.GetHashCode() : 0);
                return hash;
            }
        }
    }
}