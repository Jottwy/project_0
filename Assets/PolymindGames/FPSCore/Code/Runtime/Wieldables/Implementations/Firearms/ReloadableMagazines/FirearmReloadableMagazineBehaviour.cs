using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Abstract base class for a reloadable magazine behavior in a firearm system.
    /// </summary>
    public abstract class FirearmReloadableMagazineBehaviour : FirearmComponentBehaviour, IFirearmReloadableMagazine
    {
        [SerializeField, Range(0, 500)]
        private int _magazineSize;
    
        private int _ammoInMagazine = -1;
        private bool _isReloading;
    
        protected const string AddMenuPath = "Polymind Games/Wieldables/Firearms/Reloadable Magazines/";
    
        /// <inheritdoc/>
        public bool IsReloading
        {
            get => _isReloading;
            protected set
            {
                if (value == _isReloading)
                    return;
    
                _isReloading = value;
    
                if (_isReloading)
                    ReloadStarted?.Invoke(AmmoToLoad);
            }
        }
    
        /// <inheritdoc/>
        public int CurrentAmmoCount
        {
            get => _ammoInMagazine;
            protected set
            {
                int clampedValue = Mathf.Clamp(value, 0, Capacity);
    
                if (clampedValue != _ammoInMagazine)
                {
                    int prevInMagazine = _ammoInMagazine;
                    _ammoInMagazine = clampedValue;
                    AmmoCountChanged?.Invoke(prevInMagazine, clampedValue);
                    OnAmmoUsed(clampedValue);
                }
            }
        }
    
        /// <inheritdoc/>
        public int Capacity => _magazineSize;
    
        protected int AmmoToLoad { get; set; }
        protected bool IsMagazineEmpty => CurrentAmmoCount <= 0;
        protected bool IsMagazineFull => CurrentAmmoCount >= Capacity;
    
        /// <inheritdoc/>
        public event UnityAction<int, int> AmmoCountChanged;
    
        /// <inheritdoc/>
        public event UnityAction<int> ReloadStarted;

        /// <summary>
        /// Attempts to use a specified amount of ammo.
        /// </summary>
        /// <param name="amount">The amount of ammo to use.</param>
        /// <returns>True if ammo was successfully used; otherwise, false.</returns>
        public bool TryUseAmmo(int amount)
        {
            if (CurrentAmmoCount < amount)
                return false;

            CurrentAmmoCount -= amount;
            return true;
        }
        
        /// <summary>
        /// Forces the magazine to set the current ammo count to a specified amount.
        /// </summary>
        /// <param name="amount">The amount of ammo to set in the magazine.</param>
        public void ForceSetAmmo(int amount) => CurrentAmmoCount = amount;
        
        /// <inheritdoc/>
        public abstract bool TryBeginReload(IFirearmAmmoProvider ammoProvider);

        /// <inheritdoc/>
        public abstract bool TryCancelReload(IFirearmAmmoProvider ammoProvider, out float endDuration);
        
        /// <summary>
        /// Called when the the amount of ammo in the magazine changes.
        /// </summary>
        protected virtual void OnAmmoUsed(int ammo) { }
        
        /// <summary>
        /// Sets the reloadable magazine for the associated firearm.
        /// </summary>
        protected virtual void OnEnable()
        {
            if (Firearm != null)
                Firearm.ReloadableMagazine = this;
        }
    
        /// <summary>
        /// Cancels reloading.
        /// </summary>
        protected virtual void OnDisable()
        {
            TryCancelReload(Firearm.AmmoProvider, out _);
            IsReloading = false;
        }
    }
}