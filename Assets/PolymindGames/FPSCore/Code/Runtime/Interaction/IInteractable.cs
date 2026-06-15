namespace PolymindGames
{
    public interface IInteractable : IMonoBehaviour
    {
        bool InteractionEnabled { get; }
        float HoldDuration { get; }

        event InteractEventHandler Interacted;

        /// <summary>
        /// Called when a character interacts with this object.
        /// </summary>
        void OnInteract(ICharacter character);
    }

    public delegate void InteractEventHandler(IInteractable interactable, ICharacter character);
    
    /*
    public interface IInteractableEventHandler : IMonoBehaviour { }

    /// <summary>
    /// Represents the content and basic settings of an interactable object.
    /// </summary>
    public interface IInteractableContent : IInteractableEventHandler
    {
        /// <summary>
        /// Gets a value indicating whether interaction with this object is currently enabled.
        /// </summary>
        bool IsInteractionEnabled { get; }

        /// <summary>
        /// Gets or sets the title displayed for this interactable object.
        /// </summary>
        string Title { get; set; }

        /// <summary>
        /// Gets or sets the description displayed for this interactable object.
        /// </summary>
        string Description { get; set; }

        /// <summary>
        /// Gets the offset from the object's center for interaction-related visualizations.
        /// </summary>
        Vector3 InteractionCenterOffset { get; }

        /// <summary>
        /// Event triggered when the description of the interactable object changes.
        /// </summary>
        event UnityAction DescriptionChanged;
    }
    
    /// <summary>
    /// Handles the initial phase of an interaction with an object, such as pressing or holding.
    /// </summary>
    public interface IInteractionStartHandler : IInteractableEventHandler
    {
        /// <summary>
        /// Gets the duration required to begin the interaction, in seconds.
        /// </summary>
        float InteractionStartDuration { get; }

        /// <summary>
        /// Called when an interaction with the object begins.
        /// </summary>
        /// <param name="character">The character initiating the interaction.</param>
        void OnInteractionStart(ICharacter character);
    }
    
    /// <summary>
    /// Handles updates during an ongoing interaction, such as holding or repeated actions.
    /// </summary>
    public interface IInteractionUpdateHandler : IInteractableEventHandler
    {
        /// <summary>
        /// Called periodically while an interaction with the object is ongoing.
        /// </summary>
        /// <param name="character">The character maintaining the interaction.</param>
        void OnInteractionUpdate(ICharacter character);
    }
    
    /// <summary>
    /// Handles the end of an interaction, such as releasing a button or completing an action.
    /// </summary>
    public interface IInteractionEndHandler : IInteractableEventHandler
    {
        /// <summary>
        /// Called when an interaction with the object ends.
        /// </summary>
        /// <param name="character">The character ending the interaction.</param>
        void OnInteractionEnd(ICharacter character);
    }
    
    /// <summary>
    /// Handles the behavior when a character begins hovering over the object,
    /// typically when it comes into focus or the player looks at it.
    /// </summary>
    public interface IHoverStartHandler : IInteractableEventHandler
    {
        /// <summary>
        /// Called when a character starts hovering over the object.
        /// </summary>
        /// <param name="character">The character initiating the hover action.</param>
        void OnHoverStart(ICharacter character);
    }
    
    /// <summary>
    /// Handles periodic updates during a hover action, such as continuous focus tracking.
    /// </summary>
    public interface IHoverUpdateHandler : IInteractableEventHandler
    {
        /// <summary>
        /// Called periodically while a character is hovering over the object.
        /// </summary>
        /// <param name="character">The character maintaining the hover action.</param>
        void OnHoverUpdate(ICharacter character);
    }
    
    /// <summary>
    /// Handles the behavior when a character stops hovering over the object,
    /// typically when it goes out of focus or the player looks away.
    /// </summary>
    public interface IHoverEndHandler : IInteractableEventHandler
    {
        /// <summary>
        /// Called when a character stops hovering over the object.
        /// </summary>
        /// <param name="character">The character ending the hover action.</param>
        void OnHoverEnd(ICharacter character);
    }*/
}