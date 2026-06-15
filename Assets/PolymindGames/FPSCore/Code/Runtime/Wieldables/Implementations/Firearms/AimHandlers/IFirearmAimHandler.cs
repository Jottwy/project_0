namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Provides functionality to manage aiming behavior for firearms.
    /// </summary>
    public interface IFirearmAimHandler : IFirearmComponent
    {
        /// <summary>
        /// Gets a value indicating whether the firearm is currently aiming.
        /// </summary>
        bool IsAiming { get; }

        /// <summary>
        /// Gets the modifier applied to firing accuracy while aiming.
        /// </summary>
        float FireAccuracyModifier { get; }

        /// <summary>
        /// Initiates the aiming process.
        /// </summary>
        /// <returns>True if aiming started successfully; otherwise, false.</returns>
        bool StartAiming();

        /// <summary>
        /// Ends the aiming process.
        /// </summary>
        /// <returns>True if aiming ended successfully; otherwise, false.</returns>
        bool StopAiming();
    }

    public sealed class DefaultFirearmAimHandler : IFirearmAimHandler
    {
        public static readonly DefaultFirearmAimHandler Instance = new();

        public bool IsAiming => false;
        public float FireAccuracyModifier => 1f;

        public bool StartAiming() => false;
        public bool StopAiming() => false;
        public void Attach() { }
        public void Detach() { }
    }
}