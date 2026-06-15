using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Abstract base class for a firearm's fire system behavior, managing firing logic and ammo consumption.
    /// </summary>
    public abstract class FirearmFireSystemBehaviour : FirearmComponentBehaviour, IFirearmFireSystem
    {
        [SerializeField, Range(0, 100)]
        private int _ammoPerShot = 1;

        protected const string AddMenuPath = "Polymind Games/Wieldables/Firearms/Fire Systems/";

        /// <inheritdoc/>
        public int AmmoPerShot => _ammoPerShot;

        /// <inheritdoc/>
        public abstract void Fire(float accuracy, IFirearmImpactEffect impactEffect);

        /// <inheritdoc/>
        public abstract LaunchContext GetLaunchContext();

        protected virtual void OnEnable()
        {
            if (Firearm != null)
                Firearm.FireSystem = this;
        }
    }
}