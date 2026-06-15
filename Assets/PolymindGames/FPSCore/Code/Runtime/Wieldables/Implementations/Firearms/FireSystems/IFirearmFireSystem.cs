namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Interface representing the firing system of a firearm,
    /// responsible for managing shooting mechanics and projectiles.
    /// </summary>
    public interface IFirearmFireSystem : IFirearmComponent
    {
        /// <summary>
        /// Gets the number of ammo used per shot.
        /// </summary>
        int AmmoPerShot { get; }

        /// <summary>
        /// Executes the shooting action with specified accuracy and impact effect.
        /// </summary>
        /// <param name="accuracy">The accuracy modifier for the shot.</param>
        /// <param name="impactEffect">The effect to apply upon impact.</param>
        void Fire(float accuracy, IFirearmImpactEffect impactEffect);

        /// <summary>
        /// Retrieves the launch context for the projectile,
        /// containing information about the shot's trajectory and settings.
        /// </summary>
        /// <returns>The context used for launching the projectile.</returns>
        LaunchContext GetLaunchContext();
    }

    public sealed class DefaultFirearmFireSystem : IFirearmFireSystem
    {
        public static readonly DefaultFirearmFireSystem Instance = new();

        public int AmmoPerShot => 1;

        public void Fire(float accuracy, IFirearmImpactEffect impactEffect) { }
        public LaunchContext GetLaunchContext() => new();

        public void Attach() { }
        public void Detach() { }
    }
}