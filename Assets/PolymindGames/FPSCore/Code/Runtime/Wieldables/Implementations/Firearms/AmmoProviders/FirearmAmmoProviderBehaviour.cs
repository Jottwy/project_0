using UnityEngine.Events;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Abstract base class for handling ammunition provision in firearms.
    /// </summary>
    public abstract class FirearmAmmoProviderBehaviour : FirearmComponentBehaviour, IFirearmAmmoProvider
    {
        protected const string AddMenuPath = "Polymind Games/Wieldables/Firearms/Ammo Providers/";

        /// <inheritdoc/>
        public abstract event UnityAction<int> AmmoCountChanged;

        /// <inheritdoc/>
        public abstract int RemoveAmmo(int amount);

        /// <inheritdoc/>
        public abstract int AddAmmo(int amount);

        /// <inheritdoc/>
        public abstract int GetAmmoCount();

        /// <inheritdoc/>
        public abstract bool HasAmmo();

        /// <summary>
        /// Sets the ammo provider for the associated firearm.
        /// </summary>
        protected virtual void OnEnable()
        {
            if (Firearm != null)
                Firearm.AmmoProvider = this;
        }
    }
}