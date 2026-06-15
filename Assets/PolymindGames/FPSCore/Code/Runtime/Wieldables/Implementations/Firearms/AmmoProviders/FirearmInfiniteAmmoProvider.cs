using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Infinite Ammo-Provider")]
    public sealed class FirearmInfiniteAmmoProvider : FirearmAmmoProviderBehaviour
    {
        /// <inheritdoc/>
        public override event UnityAction<int> AmmoCountChanged
        {
            add { }
            remove { }
        }
        
        /// <inheritdoc/>
        public override int RemoveAmmo(int amount) => amount;
        
        /// <inheritdoc/>
        public override int AddAmmo(int amount) => amount;
        
        /// <inheritdoc/>
        public override int GetAmmoCount() => int.MaxValue;
        
        /// <inheritdoc/>
        public override bool HasAmmo() => true;
    }
}
