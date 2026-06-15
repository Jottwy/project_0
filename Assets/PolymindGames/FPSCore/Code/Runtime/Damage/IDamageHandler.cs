using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Represents an entity that can receive damage within the game.
    /// </summary>
    public interface IDamageHandler : IMonoBehaviour
    {
        /// <summary>
        /// Gets the parent character associated with this damage handler.
        /// </summary>
        ICharacter Character { get; }

        /// <summary>
        /// Receives damage with additional arguments and returns the result of the damage applied.
        /// </summary>
        /// <param name="damage">The amount of damage to apply.</param>
        /// <param name="args">Additional arguments that provide context or modifiers for the damage.</param>
        /// <returns>A <see cref="DamageResult"/> indicating the outcome of the damage (e.g., Normal, Critical, Fatal, Ignored).</returns>
        DamageResult HandleDamage(float damage, in DamageArgs args = default(DamageArgs));
    }

    /// <summary>
    /// Enumeration representing the possible outcomes of a damage application.
    /// </summary>
    public enum DamageResult : byte
    {
        /// <summary>
        /// Normal damage was applied.
        /// </summary>
        Normal = 0,

        /// <summary>
        /// Critical damage was applied.
        /// </summary>
        Critical = 1,

        /// <summary>
        /// The damage was fatal, killing the entity.
        /// </summary>
        Fatal = 2,

        /// <summary>
        /// The damage was ignored, having no effect.
        /// </summary>
        Ignored = 3
    }
}