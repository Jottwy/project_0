using System.Runtime.CompilerServices;
using UnityEngine.Events;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Interface representing a reloadable magazine for a firearm,
    /// managing ammunition and reloading mechanics.
    /// </summary>
    public interface IFirearmReloadableMagazine : IFirearmComponent
    {
        /// <summary>
        /// Gets a value indicating whether the magazine is currently reloading.
        /// </summary>
        bool IsReloading { get; }

        /// <summary>
        /// Gets the number of ammo currently in the magazine.
        /// </summary>
        int CurrentAmmoCount { get; }

        /// <summary>
        /// Gets the total capacity of the magazine.
        /// </summary>
        int Capacity { get; }

        /// <summary>
        /// Event triggered when the amount of ammo in the magazine changes.
        /// </summary>
        /// <remarks>Provides previous and current ammo counts.</remarks>
        event UnityAction<int, int> AmmoCountChanged;

        /// <summary>
        /// Event triggered when a reload starts.
        /// </summary>
        /// <param name="ammoToLoad">The amount of ammo being loaded.</param>
        event UnityAction<int> ReloadStarted;

        /// <summary>
        /// Attempts to start reloading the magazine with the specified ammo provider.
        /// </summary>
        /// <param name="ammoProvider">The provider from which to load ammo.</param>
        /// <returns>True if reloading started successfully; otherwise, false.</returns>
        bool TryBeginReload(IFirearmAmmoProvider ammoProvider);

        /// <summary>
        /// Attempts to cancel the ongoing reload process.
        /// </summary>
        /// <param name="ammoProvider">The provider involved in the reload.</param>
        /// <param name="endDuration">The duration until the reload process ends, if applicable.</param>
        /// <returns>True if the reload was canceled; otherwise, false.</returns>
        bool TryCancelReload(IFirearmAmmoProvider ammoProvider, out float endDuration);

        /// <summary>
        /// Attempts to use a specified amount of ammo from the magazine.
        /// </summary>
        /// <param name="amount">The amount of ammo to use.</param>
        /// <returns>True if the ammo was successfully used; otherwise, false.</returns>
        bool TryUseAmmo(int amount);

        /// <summary>
        /// Forces the magazine to set its current ammo count to a specified amount.
        /// </summary>
        /// <param name="amount">The amount of ammo to set.</param>
        void ForceSetAmmo(int amount);
    }

    public static class ReloadableMagazineExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsMagazineEmpty(this IFirearmReloadableMagazine magazine)
        {
            return magazine.CurrentAmmoCount == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsMagazineFull(this IFirearmReloadableMagazine magazine)
        {
            return magazine.CurrentAmmoCount != 0 && magazine.CurrentAmmoCount == magazine.Capacity;
        }
    }

    public sealed class DefaultReloadableFirearmMagazine : IFirearmReloadableMagazine
    {
        public static readonly DefaultReloadableFirearmMagazine Instance = new();

        public int CurrentAmmoCount => 0;
        public bool IsReloading => false;
        public int Capacity => 0;

        public event UnityAction<int, int> AmmoCountChanged
        {
            add { }
            remove { }
        }

        public event UnityAction<int> ReloadStarted
        {
            add { }
            remove { }
        }

        public bool TryBeginReload(IFirearmAmmoProvider ammoProvider) => false;
        public bool TryUseAmmo(int amount) => false;
        public void ForceSetAmmo(int amount) { }

        public bool TryCancelReload(IFirearmAmmoProvider ammoProvider, out float endDuration)
        {
            endDuration = 0f;
            return false;
        }

        public void Attach() { }
        public void Detach() { }
    }
}