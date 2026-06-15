using UnityEngine.Events;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Defines an interface for handling indexed attachment modes on a firearm.
    /// </summary>
    public interface IFirearmIndexModeHandler
    {
        /// <summary>
        /// Gets the currently active firearm attachment mode.
        /// </summary>
        FirearmAttachment CurrentMode { get; }

        /// <summary>
        /// Gets the firearm associated with this mode handler.
        /// </summary>
        IFirearm Firearm { get; }

        /// <summary>
        /// Event triggered when the firearm's attachment mode changes.
        /// </summary>
        event UnityAction<FirearmAttachment> ModeChanged;

        /// <summary>
        /// Cycles to the next available attachment mode on the firearm.
        /// </summary>
        void ToggleNextMode();
    }
}