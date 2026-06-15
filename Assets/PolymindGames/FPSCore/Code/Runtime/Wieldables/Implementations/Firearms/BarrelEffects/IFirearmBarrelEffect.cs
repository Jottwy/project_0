namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Interface for handling effects produced by the firearm's barrel during firing.
    /// </summary>
    public interface IFirearmBarrelEffect : IFirearmComponent
    {
        /// <summary>
        /// Executes the visual and audio effects when the firearm is fired,
        /// such as muzzle flashes or sounds.
        /// </summary>
        void TriggerFireEffect();
        
        /// <summary>
        /// Executes the effects that occur when firing stops,
        /// such as fading out sounds or visual effects.
        /// </summary>
        void TriggerFireStopEffect();
    }

    public sealed class DefaultFirearmBarrelEffect : IFirearmBarrelEffect
    {
        public static readonly DefaultFirearmBarrelEffect Instance = new();

        public void TriggerFireEffect() { }
        public void TriggerFireStopEffect() { }
        public void Attach() { }
        public void Detach() { }
    }
}