namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Abstract base class for handling barrel effects in firearms.
    /// </summary>
    public abstract class FirearmBarrelEffectBehaviour : FirearmComponentBehaviour, IFirearmBarrelEffect
    {
        protected const string AddMenuPath = "Polymind Games/Wieldables/Firearms/Barrel Effects/";

        /// <summary>
        /// Triggers the fire effect for the barrel, such as muzzle flash or smoke.
        /// </summary>
        public abstract void TriggerFireEffect();

        /// <summary>
        /// Triggers the effect that occurs when firing stops, such as dissipating smoke.
        /// </summary>
        public abstract void TriggerFireStopEffect();

        protected virtual void OnEnable()
        {
            if (Firearm != null)
                Firearm.BarrelEffect = this;
        }
    }
}