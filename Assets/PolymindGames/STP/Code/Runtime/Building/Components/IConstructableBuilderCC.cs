using PolymindGames.BuildingSystem;
using UnityEngine.Events;

namespace PolymindGames
{
    /// <summary>
    /// Interface for a character component responsible for building construction functionality.
    /// </summary>
    public interface IConstructableBuilderCC : ICharacterComponent
    {
        /// <summary>
        /// Gets the constructable currently in view.
        /// </summary>
        IConstructable CurrentConstructable { get; }

        /// <summary>
        /// Gets or sets a value indicating whether detection is enabled for constructables.
        /// </summary>
        bool DetectionEnabled { get; set; }

        /// <summary>
        /// Event triggered when the currently in-view constructable changes.
        /// </summary>
        event UnityAction<IConstructable> ConstructableChanged;

        /// <summary>
        /// Event triggered when the progress of cancelling constructable preview changes.
        /// </summary>
        event UnityAction<float> CancelConstructableProgressChanged;

        /// <summary>
        /// Event triggered when a build material is added during construction.
        /// </summary>
        event UnityAction<BuildMaterialDefinition, int> BuildMaterialAdded;

        /// <summary>
        /// Starts the process of cancelling the preview of construction.
        /// </summary>
        void StartCancellingPreview();

        /// <summary>
        /// Stops the process of cancelling the preview of construction.
        /// </summary>
        void StopCancellingPreview();

        /// <summary>
        /// Attempts to add a material from the player's inventory to the construction.
        /// </summary>
        /// <returns>True if a material was successfully added, false otherwise.</returns>
        bool TryAddMaterialFromPlayer();
        
        /// <summary>
        /// Attempts to add the specified build material to the construction.
        /// </summary>
        /// <param name="buildMaterial">The build material to add.</param>
        /// <returns>True if the material was successfully added, false otherwise.</returns>
        bool TryAddMaterial(BuildMaterialDefinition buildMaterial);
    }
}