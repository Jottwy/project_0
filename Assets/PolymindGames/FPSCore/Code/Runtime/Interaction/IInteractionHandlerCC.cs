namespace PolymindGames
{
    using UnityEngine.Events;

    /// <summary>
    /// Interface for character components that handle interactions with objects in the environment.
    /// </summary>
    public interface IInteractionHandlerCC : ICharacterComponent
    {
        /// <summary>
        /// Gets or sets a value indicating whether interaction is enabled.
        /// </summary>
        bool InteractionEnabled { get; set; }

        /// <summary>
        /// Gets the hoverable object currently in view.
        /// </summary>
        IHoverable Hoverable { get; }

        /// <summary>
        /// Event triggered when the hoverable object in view changes.
        /// </summary>
        event UnityAction<IHoverable> HoverableInViewChanged;

        /// <summary>
        /// Event triggered when the progress of interaction changes.
        /// </summary>
        event UnityAction<float> InteractProgressChanged;

        /// <summary>
        /// Event triggered when the state of interaction enabling changes.
        /// </summary>
        event UnityAction<bool> InteractionEnabledChanged;

        /// <summary>
        /// Starts an interaction with the current hoverable object.
        /// </summary>
        void StartInteraction();

        /// <summary>
        /// Stops the current interaction.
        /// </summary>
        void StopInteraction();
    }
}