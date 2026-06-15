using PolymindGames.InventorySystem;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [RequireComponent(typeof(Firearm))]
    [AddComponentMenu("Polymind Games/Wieldables/Behaviours/Firearm Item")]
    public class FirearmItem : WieldableItem
    {
        private ItemProperty _ammoInMagazineProperty;
        private IFirearmReloadableMagazine _magazine;
        private IFirearm _firearm;

        protected override void OnItemChanged(ItemStack stack)
        {
            base.OnItemChanged(stack);
            
            _ammoInMagazineProperty = null;

            // Load the current 'ammo in magazine count' that's saved in one of the properties on the given item.
            if (stack.Item?.TryGetProperty(ItemConstants.AmmoInMagazine, out _ammoInMagazineProperty) ?? false)
            {
                _magazine.ForceSetAmmo(_ammoInMagazineProperty.Integer);
            }
        }

        protected override void OnInitialized(IWieldable wieldable)
        {
            base.OnInitialized(wieldable);
            
            _firearm = Wieldable as IFirearm;
            if (_firearm == null)
            {
                Debug.LogError("No firearm found on this object!", gameObject);
                return;
            }

            _magazine = _firearm.ReloadableMagazine;
            _magazine.AmmoCountChanged += OnAmmoCountChanged;
            _firearm.AddChangedListener(FirearmComponentType.ReloadableMagazine, OnMagazineChanged);
        }

        private void OnMagazineChanged()
        {
            _magazine.AmmoCountChanged -= OnAmmoCountChanged;
            _magazine = _firearm.ReloadableMagazine;
            _magazine.AmmoCountChanged += OnAmmoCountChanged;
        }

        private void OnAmmoCountChanged(int prevAmmo, int ammo)
        {
            if (_ammoInMagazineProperty != null)
            {
                _ammoInMagazineProperty.Integer = ammo;
            }
        }
    }
}