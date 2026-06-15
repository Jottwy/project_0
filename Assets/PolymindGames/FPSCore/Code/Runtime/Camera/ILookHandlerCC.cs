using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Interface for character components that handle character look behavior.
    /// </summary>
    public interface ILookHandlerCC : ICharacterComponent
    {
        /// <summary>
        /// Gets the current view angles.
        /// </summary>
        Vector2 ViewAngles { get; }

        /// <summary>
        /// Gets the current look input.
        /// </summary>
        Vector2 LookInput { get; }

        /// <summary>
        /// Gets the current look delta.
        /// </summary>
        Vector2 LookDelta { get; }

        /// <summary>
        /// Sets the look input method for the look handler.
        /// </summary>
        /// <param name="input">The method providing the look input.</param>
        void SetLookInput(LookHandlerInputDelegate input);

        /// <summary>
        /// Sets the additive look input method for the look handler.
        /// </summary>
        /// <param name="input">The method providing the additive look input.</param>
        void SetAdditiveLookInput(LookHandlerInputDelegate input);
    }

    /// <summary>
    /// Delegate used to provide look input for the look handler.
    /// </summary>
    /// <returns>The look input as a Vector2.</returns>
    public delegate Vector2 LookHandlerInputDelegate();

}