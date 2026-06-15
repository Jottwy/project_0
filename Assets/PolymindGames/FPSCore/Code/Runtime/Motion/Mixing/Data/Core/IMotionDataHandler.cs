
namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Interface for managing motion data and profiles.
    /// </summary>
    public interface IMotionDataHandler
    {
        /// <summary>
        /// Pushes a motion profile onto the stack, making it the active profile.
        /// </summary>
        /// <param name="profile">The motion profile to add.</param>
        void PushProfile(MotionProfile profile);

        /// <summary>
        /// Pops a motion profile off the stack, deactivating it.
        /// </summary>
        /// <param name="profile">The motion profile to remove.</param>
        void PopProfile(MotionProfile profile);

        /// <summary>
        /// Adds a listener to be notified when motion data changes.
        /// </summary>
        /// <param name="listener">The listener to register for data change notifications.</param>
        void AddChangedListener(IMotionDataListener listener);

        /// <summary>
        /// Removes a previously added listener from data change notifications.
        /// </summary>
        /// <param name="listener">The listener to remove.</param>
        void RemoveChangedListener(IMotionDataListener listener);

        /// <summary>
        /// Sets an override for a specific type of motion data.
        /// </summary>
        /// <typeparam name="T">The type of motion data to override.</typeparam>
        /// <param name="data">The new motion data override.</param>
        void SetDataOverride<T>(T data) where T : MotionData;

        /// <summary>
        /// Sets the current movement state type and updates all associated motion data entries.
        /// </summary>
        /// <param name="stateType">The new state type to apply.</param>
        void SetStateType(MovementStateType stateType);
    }
}