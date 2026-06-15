using UnityEngine.Events;

namespace PolymindGames
{
    /// <summary>
    /// Interface for character components that handle wieldable healing items.
    /// </summary>
    public interface IWieldableHealingHandlerCC : ICharacterComponent
    {
        /// <summary>
        /// Gets the count of remaining heals.
        /// </summary>
        int HealsCount { get; }

        /// <summary>
        /// Event triggered when the count of remaining heals changes.
        /// </summary>
        event UnityAction<int> HealsCountChanged;

        /// <summary>
        /// Tries to use a healing item. Returns true if healing was successful, false otherwise.
        /// </summary>
        /// <returns>True if healing was successful, false otherwise.</returns>
        bool TryHeal();
    }

}
