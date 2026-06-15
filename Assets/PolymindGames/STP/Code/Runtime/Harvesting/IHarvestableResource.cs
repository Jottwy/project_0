using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.ResourceHarvesting
{
    /// <summary>
    /// Enum representing different types of harvestable resources.
    /// </summary>
    public enum HarvestableResourceType
    {
        None = -1,
        Tree = 0,
        Rock = 1,
        Plant = 2,
    }

    /// <summary>
    /// Represents the different states of a harvestable resource in the game.
    /// </summary>
    public enum HarvestableState
    {
        /// <summary> The resource requires initialization and is not ready for harvesting. </summary>
        NotReady,

        /// <summary> The resource is fresh and ready to begin the harvesting process. </summary>
        Unharvested,

        /// <summary> Harvesting is currently in progress, but the resource is not fully harvested yet. </summary>
        PartiallyHarvested,

        /// <summary> The resource has been fully harvested, and no more can be gathered from it. </summary>
        FullyHarvested
    }

    /// <summary>
    /// Represents a single harvestable resource, providing interaction and management methods.
    /// </summary>
    public interface IHarvestableResource : IMonoBehaviour
    {
        /// <summary>
        /// Gets the definition that describes this harvestable resource.
        /// </summary>
        HarvestableResourceDefinition ResourceDefinition { get; }

        /// <summary>
        /// Gets the remaining harvestable amount of the resource, represented as a value between 0 (depleted) and 1 (full).
        /// </summary>
        float RemainingHarvestAmount { get; }

        /// <summary>
        /// Gets the current state of the resource (e.g., available, depleted, respawning).
        /// </summary>
        HarvestableState HarvestableState { get; }

        /// <summary>
        /// Event triggered when the resource has been harvested by a certain amount.
        /// </summary>
        event DamageReceivedDelegate Harvested;

        /// <summary>
        /// Event triggered when the resource has been harvested by a certain amount.
        /// </summary>
        event DamageReceivedDelegate FullyHarvested;

        /// <summary>
        /// Event triggered when the resource has fully respawned.
        /// </summary>
        event UnityAction<IHarvestableResource> Respawned;

        /// <summary>
        /// Retrieves the world-space bounds of the resource for interaction and collision purposes.
        /// </summary>
        Bounds GetHarvestBounds();

        /// <summary>
        /// Determines whether the resource can be harvested using a given tool at a specific position.
        /// </summary>
        /// <param name="harvestPower">The strength of the tool's effectiveness.</param>
        /// <param name="worldPosition">The world position to check.</param>
        /// <returns>True if the resource can be harvested; otherwise, false.</returns>
        bool CanBeHarvested(float harvestPower, Vector3 worldPosition);

        /// <summary>
        /// Attempts to harvest the resource using a given tool and amount.
        /// </summary>
        /// <param name="harvestPower">The strength of the tool's effectiveness.</param>
        /// <param name="harvestAmount">The amount of the resource to harvest.</param>
        /// <param name="harvestArgs">Additional harvest details, such as damage info.</param>
        /// <returns>True if the harvesting was successful; otherwise, false.</returns>
        bool TryHarvest(float harvestPower, float harvestAmount, in DamageArgs harvestArgs);
    }

    /// <summary>
    /// Manages multiple harvestable resources, including virtual or non-instantiated ones.
    /// </summary>
    public interface IHarvestableResourcesHandler : IMonoBehaviour
    {
        /// <summary>
        /// Gets the total number of harvestable resources managed by this handler.
        /// </summary>
        int ResourcesCount { get; }

        /// <summary>
        /// Finds the index of a harvestable resource located near a given world position.
        /// </summary>
        /// <param name="worldPosition">The position to search around.</param>
        /// <param name="radius">The search radius.</param>
        /// <returns>The index of the found resource, or -1 if none are found.</returns>
        int FindResourceIndex(Vector3 worldPosition, float radius);

        /// <summary>
        /// Gets the world-space bounds of the specified resource.
        /// </summary>
        /// <param name="resourceIndex">The index of the resource.</param>
        /// <returns>The bounding box of the resource.</returns>
        Bounds GetHarvestBoundsAt(int resourceIndex);

        /// <summary>
        /// Retrieves the definition of the harvestable resource at the specified index.
        /// </summary>
        /// <param name="resourceIndex">The index of the resource.</param>
        /// <returns>The resource definition, or null if not found.</returns>
        HarvestableResourceDefinition GetResourceDefinitionAt(int resourceIndex);

        /// <summary>
        /// Determines if a resource at the specified index can be harvested with a given tool.
        /// </summary>
        /// <param name="harvestPower">The strength of the tool's effectiveness.</param>
        /// <param name="worldPosition">The position to check (optional).</param>
        /// <returns>True if the resource can be harvested; otherwise, false.</returns>
        bool CanHarvestAt(float harvestPower, Vector3 worldPosition = default);

        /// <summary>
        /// Attempts to harvest a resource at the specified index using a given tool.
        /// </summary>
        /// <param name="harvestPower">The strength of the tool's effectiveness.</param>
        /// <param name="harvestAmount">The amount to harvest.</param>
        /// <param name="harvestArgs">Additional harvest details, such as damage info.</param>
        /// <returns>True if the harvesting was successful; otherwise, false.</returns>
        bool TryHarvestAt(float harvestPower, float harvestAmount, in DamageArgs harvestArgs);
    }
}