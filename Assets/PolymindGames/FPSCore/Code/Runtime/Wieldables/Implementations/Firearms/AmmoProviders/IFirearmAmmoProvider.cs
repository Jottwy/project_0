using UnityEngine.Events;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Interface representing a provider for firearm ammunition management,
    /// allowing for adding, removing, and checking ammo availability.
    /// </summary>
    public interface IFirearmAmmoProvider : IFirearmComponent
    {
        /// <summary>
        /// Event triggered when the ammo count changes.
        /// </summary>
        event UnityAction<int> AmmoCountChanged;

        /// <summary>
        /// Removes a specified amount of ammo from the provider.
        /// </summary>
        /// <param name="amount">The amount of ammo to remove.</param>
        /// <returns>The actual amount of ammo removed.</returns>
        int RemoveAmmo(int amount);

        /// <summary>
        /// Adds a specified amount of ammo to the provider.
        /// </summary>
        /// <param name="amount">The amount of ammo to add.</param>
        /// <returns>The actual amount of ammo added.</returns>
        int AddAmmo(int amount);

        /// <summary>
        /// Gets the current ammo count available in the provider.
        /// </summary>
        /// <returns>The current ammo count.</returns>
        int GetAmmoCount();

        /// <summary>
        /// Checks if there is any ammo available in the provider.
        /// </summary>
        /// <returns>True if ammo is available; otherwise, false.</returns>
        bool HasAmmo();
    }

    public sealed class DefaultFirearmAmmoProvider : IFirearmAmmoProvider
    {
        public static readonly DefaultFirearmAmmoProvider Instance = new();

        public event UnityAction<int> AmmoCountChanged
        {
            add { }
            remove { }
        }

        public int RemoveAmmo(int amount) => amount;
        public int AddAmmo(int amount) => amount;
        public int GetAmmoCount() => int.MaxValue;
        public bool HasAmmo() => true;
        public void Attach() { }
        public void Detach() { }
    }
}