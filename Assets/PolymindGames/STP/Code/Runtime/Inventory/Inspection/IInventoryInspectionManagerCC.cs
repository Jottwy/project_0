using PolymindGames.InventorySystem;
using System.Collections.Generic;
using UnityEngine.Events;

namespace PolymindGames
{
    /// <summary>
    /// Manages the inspection process for a character’s inventory and external containers.
    /// </summary>
    public interface IInventoryInspectionManagerCC : ICharacterComponent
    {
        /// <summary> Indicates whether the character is currently in the process of inspecting. </summary>
        bool IsInspecting { get; }

        /// <summary> The workstation being inspected, or null if the default inspection is active. </summary>
        IWorkstation Workstation { get; }

        /// <summary> A list of all external containers being inspected, including those from the current workstation. </summary>
        IReadOnlyList<IItemContainer> InspectedContainers { get; }

        /// <summary> Event triggered when the inspection process starts. </summary>
        event UnityAction InspectionStarted;

        /// <summary> Event triggered immediately after the inspection starts. </summary>
        event UnityAction InspectionPostStarted;

        /// <summary> Event triggered when the inspection process ends. </summary>
        event UnityAction InspectionEnded;

        /// <summary>
        /// Starts the inspection process for the specified workstation.
        /// </summary>
        /// <param name="workstation">The workstation to inspect, or null to inspect the default setup.</param>
        void StartInspection(IWorkstation workstation);

        /// <summary>
        /// Stops the current inspection process.
        /// </summary>
        void StopInspection();

        /// <summary>
        /// Starts the inspection of a specific item container.
        /// </summary>
        /// <param name="container">The container to inspect.</param>
        void InspectContainer(IItemContainer container);

        /// <summary>
        /// Removes a container from the inspection list.
        /// </summary>
        /// <param name="container">The container to stop inspecting.</param>
        void RemoveContainerFromInspection(IItemContainer container);
    }
}