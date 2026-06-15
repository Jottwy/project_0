using Object = UnityEngine.Object;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Specifies the behavior of a tween when it is reset.
    /// </summary>
    public enum TweenResetBehavior
    {
        /// <summary>
        /// Keeps the tween's current value as-is without any modification.
        /// </summary>
        KeepCurrentValue = 0,

        /// <summary>
        /// Resets the tween to its start value before the next tween animation begins.
        /// This allows the tween to start from the beginning again.
        /// </summary>
        ResetToStartValue = 1,

        /// <summary>
        /// Jumps to the end value before the next tween animation begins.
        /// This immediately sets the tween to its final value.
        /// </summary>
        ResetToEndValue = 2
    }

    /// <summary>
    /// Interface for a tween that allows for various operations such as ticking, releasing, and resetting.
    /// </summary>
    public interface ITween
    {
        /// <summary>
        /// Gets the parent object of the tween.
        /// </summary>
        /// <returns>The parent object of the tween.</returns>
        Object GetParent();

        /// <summary>
        /// Updates the tween based on the current time and progress.
        /// This is typically called every frame.
        /// </summary>
        void Tick();

        /// <summary>
        /// Resets and releases the tween, stopping it and cleaning up resources.
        /// </summary>
        void Release(TweenResetBehavior behavior);

        /// <summary>
        /// Resets the tween to its default state with the specified behavior.
        /// This can either keep the current value, reset to the start value, or jump to the end value.
        /// </summary>
        /// <param name="behavior">The reset behavior to apply.</param>
        void Reset(TweenResetBehavior behavior);
    }
}