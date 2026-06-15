using PolymindGames.UserInterface;
using System.Collections.Generic;
using PolymindGames.SaveSystem;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/player/modules-and-behaviours/crafting#crafting-manager-module")]
    public sealed class CraftingManager : CharacterBehaviour, ICraftingManagerCC, ISaveableComponent
    {
        [SerializeField]
        [Tooltip("Craft Sound: Sound that will be played after crafting an item.")]
        private AudioData _craftAudio = new(null);

        private readonly List<DataIdReference<ItemDefinition>> _favoriteBlueprints = new(4);
        private ItemDefinition _currentItemToCraft;
        
        public bool IsCrafting => _currentItemToCraft != null;
        public IReadOnlyList<DataIdReference<ItemDefinition>> FavoriteBlueprints => _favoriteBlueprints;

        public void AddFavoriteBlueprint(DataIdReference<ItemDefinition> blueprint)
        {
            if (!_favoriteBlueprints.Contains(blueprint))
                _favoriteBlueprints.Add(blueprint);
        }

        public void RemoveFavoriteBlueprint(DataIdReference<ItemDefinition> blueprint)
        {
            _favoriteBlueprints.Remove(blueprint);
        }

        public void Craft(ItemDefinition itemDef)
        {
            if (IsCrafting || itemDef == null)
                return;

            if (itemDef.TryGetDataOfType<CraftingData>(out var craftingData))
            {
                var blueprint = craftingData.Blueprint;
                var inventory = Character.Inventory;

                // Verify if all blueprint crafting materials exist in the inventory
                foreach (var item in blueprint)
                {
                    if (inventory.GetItemCountById(item.Item) < item.Amount)
                        return;
                }

                // Start crafting
                _currentItemToCraft = itemDef;
                var craftingParams = new CustomActionArgs($"Crafting <b>{itemDef.Name}</b>...", craftingData.CraftDuration, true, OnCraftItemEnd, OnCraftCancel);
                ActionManagerUI.Instance.StartAction(craftingParams);
                Character.Audio.PlayClip(_craftAudio, BodyPoint.Torso);
            }
        }

        public void CancelCrafting()
        {
            if (IsCrafting)
                ActionManagerUI.Instance.CancelCurrentAction();
        }

        private void OnCraftItemEnd()
        {
            var craftData = _currentItemToCraft.GetDataOfType<CraftingData>();
            var blueprint = craftData.Blueprint;
            var inventory = Character.Inventory;

            // Verify if all blueprint crafting materials exist in the inventory
            foreach (var item in blueprint)
            {
                if (inventory.GetItemCountById(item.Item) < item.Amount)
                    return;
            }

            // Remove the blueprint items from the inventory
            foreach (var item in blueprint)
            {
                inventory.RemoveItemsById(item.Item, item.Amount);
            }

            // Add the crafted item to the inventory
            int addedCount = inventory.AddItemsById(_currentItemToCraft.Id, craftData.CraftAmount).addedCount;

            // If the crafted item couldn't be added to the inventory, spawn the world prefab
            if (addedCount < craftData.CraftAmount)
            {
                Character.Inventory.DropItem(new ItemStack(new Item(_currentItemToCraft), craftData.CraftAmount - addedCount));
            }
            else
            {
                MessageDispatcher.Instance.Dispatch(Character, MsgType.Info, $"Crafted {_currentItemToCraft.Name}", _currentItemToCraft.Icon);
            }

            _currentItemToCraft = null;
        }

        private void OnCraftCancel() => _currentItemToCraft = null;

        #region Save & Load
        // ADR-009: STP save disabled — Rust is the single source of truth.
        public void LoadMembers(object data) { }
        public object SaveMembers() => null;
        #endregion
    }
}