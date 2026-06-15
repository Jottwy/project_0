using PolymindGames.WieldableSystem;
using UnityEngine.Events;

namespace PolymindGames
{
    /// <summary>
    /// Interface for a character controller responsible for carrying and managing carryable objects.
    /// </summary>
    public interface ICarryableControllerCC : ICharacterComponent
    {
        /// <summary>
        /// Gets a value indicating whether the character is currently carrying an object.
        /// </summary>
        bool IsCarrying { get; }
        
        /// <summary>
        /// Gets the number of objects currently being carried.
        /// </summary>
        int CarryCount { get; }

        /// <summary>
        /// Event raised when carrying an object starts.
        /// </summary>
        event UnityAction<CarryableDefinition> ObjectCarryStarted;

        /// <summary>
        /// Event raised when carrying an object stops.
        /// </summary>
        event UnityAction ObjectCarryStopped;

        /// <summary>
        /// Attempts to carry the given carryable object.
        /// </summary>
        /// <param name="pickup">The carryable object to carry.</param>
        /// <returns>True if the object can be carried; otherwise, false.</returns>
        bool TryCarryObject(CarryablePickup pickup);

        /// <summary>
        /// Drops a specified number of carried objects.
        /// </summary>
        /// <param name="amount">The number of objects to drop.</param>
        void DropCarriedObjects(int amount);

        /// <summary>
        /// Uses the carried object.
        /// </summary>
        void UseCarriedObject();
    }
}