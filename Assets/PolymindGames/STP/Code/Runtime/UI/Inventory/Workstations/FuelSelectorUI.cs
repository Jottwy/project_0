using PolymindGames.InventorySystem;
using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    public sealed class FuelSelectorUI : MonoBehaviour
    {
        [SerializeField]
        private SelectableButton _nextBtn;

        [SerializeField]
        private SelectableButton _previousBtn;

        [SerializeField]
        private Image _iconImg;

        private FuelItem[] _fuelItems = Array.Empty<FuelItem>();
        private IInventory _inventory;
        private int _selectedFuelIdx;
        
        public FuelItem SelectedFuel { get; private set; }

        public void AttachToInventory(IInventory inventory)
        {
            _inventory = inventory;
            inventory.Changed += OnInventoryChanged;
            OnInventoryChanged();
        }

        public void DetachFromInventory()
        {
            _inventory.Changed -= OnInventoryChanged;
            _inventory = null;
        }

        private void Awake()
        {
            CacheFuelItems();
            _selectedFuelIdx = 0;

            _nextBtn.Clicked += _ => SelectNextFuel(true);
            _previousBtn.Clicked += _ => SelectNextFuel(false);

            SelectFuelAtIndex(0);
        }

        private void OnInventoryChanged()
        {
            RefreshFuelList();

            if (_selectedFuelIdx == -1 || _fuelItems[_selectedFuelIdx].Count == 0)
                SelectNextFuel(true);
        }

        private void SelectNextFuel(bool selectNext)
        {
            RefreshFuelList();

            bool foundValidFuel = false;
            int iterations = 0;
            int i = _selectedFuelIdx;

            do
            {
                i = (int)Mathf.Repeat(i + (selectNext ? 1 : -1), _fuelItems.Length);
                iterations++;

                if (_fuelItems[i].Count > 0)
                {
                    foundValidFuel = true;
                    _selectedFuelIdx = i;
                }
            } while (!foundValidFuel && iterations < _fuelItems.Length);

            _selectedFuelIdx = foundValidFuel ? i : -1;
            SelectFuelAtIndex(_selectedFuelIdx);
        }

        private void SelectFuelAtIndex(int index)
        {
            if (_fuelItems == null || _fuelItems.Length < 1)
                return;

            _iconImg.enabled = index > -1;

            if (index > -1)
            {
                SelectedFuel = _fuelItems[index];

                if (ItemDefinition.TryGetWithId(SelectedFuel.Item, out var itemDef))
                    _iconImg.sprite = itemDef.Icon;
            }
        }

        private void CacheFuelItems()
        {
            var fuelItems = new List<FuelItem>();

            foreach (var itemDef in ItemDefinition.Definitions)
            {
                if (itemDef.TryGetDataOfType(out FuelData fuelData))
                    fuelItems.Add(new FuelItem(itemDef.Id, 0, fuelData.FuelCapacity));
            }

            _fuelItems = fuelItems.ToArray();
        }

        private void RefreshFuelList()
        {
            foreach (var item in _fuelItems)
                item.Count = 0;

            var containers = _inventory.Containers;
            foreach (var container in containers)
            {
                if (container.HasRestriction<TagContainerRestriction>())
                    return;

                foreach (var slot in container.GetSlots())
                {
                    if (slot.TryGetStack(out var stack)
                        && stack.Item.Definition.HasDataOfType(typeof(FuelData))
                        && TryGetFuelItem(stack.Item.Id, out FuelItem fuelItem))
                    {
                        fuelItem.Count += stack.Count;
                    }
                }
            }
        }

        private bool TryGetFuelItem(int itemId, out FuelItem fuelItem)
        {
            foreach (var item in _fuelItems)
            {
                if (item.Item == itemId)
                {
                    fuelItem = item;
                    return true;
                }
            }

            fuelItem = null;
            return false;
        }

        #region Internal Types
        public sealed class FuelItem
        {
            public readonly DataIdReference<ItemDefinition> Item;
            public readonly float Capacity;

            public int Count;

            public FuelItem(int id, int count, float capacity)
            {
                Item = id;
                Count = count;
                Capacity = capacity;
            }
        }
        #endregion
    }
}