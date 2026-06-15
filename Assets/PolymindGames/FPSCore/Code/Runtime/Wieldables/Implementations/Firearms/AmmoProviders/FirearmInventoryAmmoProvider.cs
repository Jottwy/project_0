using PolymindGames.InventorySystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Inventory Ammo-Provider")]
    public class FirearmInventoryAmmoProvider : FirearmAmmoProviderBehaviour
    {
        [SerializeField, DataReference(NullElement = "")]
        private DataIdReference<ItemDefinition> _ammoItem;

        private IItemContainer _ammoContainer;

        /// <inheritdoc/>
        public override event UnityAction<int> AmmoCountChanged;
        
        protected override void OnEnable()
        {
            var inventory = Wieldable.Character.Inventory;
            _ammoContainer = inventory.FindContainer(ItemContainerFilters.WithoutTag);
            _ammoContainer.Changed += OnContainerChanged;
            base.OnEnable();
        }

        private void OnDisable() => _ammoContainer.Changed -= OnContainerChanged;
        private void OnContainerChanged() => AmmoCountChanged?.Invoke(GetAmmoCount());

        /// <inheritdoc/>
        public override int RemoveAmmo(int amount) => _ammoContainer.RemoveItemsById(_ammoItem, amount);
        
        /// <inheritdoc/>
        public override int AddAmmo(int amount) => _ammoContainer.AddItemsById(_ammoItem, amount).addedCount;
        
        /// <inheritdoc/>
        public override int GetAmmoCount() => _ammoContainer.GetItemCountById(_ammoItem);
        
        /// <inheritdoc/>
        public override bool HasAmmo() => _ammoContainer.ContainsItemById(_ammoItem);

        /// <inheritdoc/>
        public override bool CanAttach()
        {
            base.CanAttach();
            _ammoContainer ??= Wieldable.Character.Inventory.FindContainer(ItemContainerFilters.WithoutTag);
            return HasAmmo();
        }
    }
}