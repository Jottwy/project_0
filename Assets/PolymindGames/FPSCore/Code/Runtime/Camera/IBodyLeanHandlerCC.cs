namespace PolymindGames
{
    /// <summary>
    /// Interface for character components that handle body leaning.
    /// </summary>
    public interface IBodyLeanHandlerCC : ICharacterComponent
    {
        /// <summary>
        /// Gets the current state of body leaning.
        /// </summary>
        BodyLeanState LeanState { get; }

        /// <summary>
        /// Sets the state of body leaning.
        /// </summary>
        /// <param name="leanState">The state to set for body leaning.</param>
        void SetLeanState(BodyLeanState leanState);
    }

    /// <summary>
    /// Enumeration representing different states of body leaning.
    /// </summary>
    public enum BodyLeanState
    {
        /// <summary>
        /// Centered position.
        /// </summary>
        Center = 0,

        /// <summary>
        /// Leaning to the left.
        /// </summary>
        Left = -1,

        /// <summary>
        /// Leaning to the right.
        /// </summary>
        Right = 1
    }

}