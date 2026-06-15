namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Interface for handling input related to using a wieldable object.
    /// </summary>
    public interface IUseInputHandler
    {
        /// <summary>
        /// Gets a value indicating whether the use action is active.
        /// </summary>
        bool IsUsing { get; }

        /// <summary>
        /// The action blocker that controls the use input flow.
        /// </summary>
        ActionBlockHandler UseBlocker { get; }

        /// <summary>
        /// Handles the use action based on the input phase (start, hold, end).
        /// </summary>
        /// <param name="inputPhase">The current phase of the use input.</param>
        /// <returns>True if the action is successfully handled; otherwise, false.</returns>
        bool Use(WieldableInputPhase inputPhase);
    }
    
    /// <summary>
    /// Interface for handling input related to aiming with a wieldable object.
    /// </summary>
    public interface IAimInputHandler
    {
        /// <summary>
        /// Gets a value indicating whether the aim action is active.
        /// </summary>
        bool IsAiming { get; }

        /// <summary>
        /// The action blocker that controls the aim input flow.
        /// </summary>
        ActionBlockHandler AimBlocker { get; }

        /// <summary>
        /// Handles the aim action based on the input phase (start, hold, end).
        /// </summary>
        /// <param name="inputPhase">The current phase of the aim input.</param>
        /// <returns>True if the aiming action is successfully handled; otherwise, false.</returns>
        bool Aim(WieldableInputPhase inputPhase);
    }
    
    /// <summary>
    /// Interface for handling input related to reloading a wieldable object.
    /// </summary>
    public interface IReloadInputHandler
    {
        /// <summary>
        /// Gets a value indicating whether the reload action is active.
        /// </summary>
        bool IsReloading { get; }

        /// <summary>
        /// The action blocker that controls the reload input flow.
        /// </summary>
        ActionBlockHandler ReloadBlocker { get; }

        /// <summary>
        /// Handles the reload action based on the input phase (start, hold, end).
        /// </summary>
        /// <param name="inputPhase">The current phase of the reload input.</param>
        /// <returns>True if the reload is successfully handled; otherwise, false.</returns>
        bool Reload(WieldableInputPhase inputPhase);
    }
    
    /// <summary>
    /// Represents the different phases of a wieldable input action (start, hold, end).
    /// </summary>
    public enum WieldableInputPhase
    {
        /// <summary>
        /// The initial phase of the input.
        /// </summary>
        Start = 0,

        /// <summary>
        /// The phase where the input is held down.
        /// </summary>
        Hold = 1,

        /// <summary>
        /// The phase where the input is released.
        /// </summary>
        End = 2
    }
}