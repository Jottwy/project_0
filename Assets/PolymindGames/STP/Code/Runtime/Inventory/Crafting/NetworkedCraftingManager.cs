using System;
using PolymindGames.UserInterface;
using System.Collections.Generic;
using PolymindGames.SaveSystem;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// ADR-064 enm. 1 (E1.5) — sustituto del <see cref="CraftingManager"/> del vendor que hace
    /// EXACTAMENTE lo mismo (copia línea a línea de <c>Craft</c>/<c>OnCraftItemEnd</c>) más una
    /// cosa: publica <see cref="ItemCrafted"/> cuando un crafteo termina, para que el ensamblado
    /// del juego (<c>CraftingReporter</c>) se lo cuente al backend con <c>craft_item</c>.
    ///
    /// **Fichero AÑADIDO en territorio vendor, a propósito y con precedente (`GameBootGate`).**
    /// No puede vivir en <c>BackroomsSurvival</c>: <c>Character</c> construye su mapa de
    /// componentes con <c>baseType.Assembly.GetTypes()</c> (sólo este ensamblado), así que un
    /// <see cref="ICraftingManagerCC"/> de fuera revienta <c>GetCharacterComponentsInChildren</c>
    /// con <c>KeyNotFoundException</c>. Y el vendor es <c>sealed</c> sin evento de «crafteado».
    /// Inventariado en <c>docs/systems/vendor-patches.md</c> (fila 8): un reimport lo borra, el
    /// menú <c>Backrooms/Create Craft Assets</c> lo vuelve a poner en <c>STP_Player.prefab</c>.
    ///
    /// Sólo puede haber UN <see cref="ICraftingManagerCC"/> bajo el personaje (el mapa lanza en
    /// release): el menú quita el del vendor antes de añadir éste, y un test lo afirma.
    /// </summary>
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/player/modules-and-behaviours/crafting#crafting-manager-module")]
    public sealed class NetworkedCraftingManager : CharacterBehaviour, ICraftingManagerCC, ISaveableComponent
    {
        /// <summary>Un crafteo TERMINADO: el personaje, lo crafteado y cuántas unidades produjo la
        /// receta (<c>CraftingData.CraftAmount</c>). Se dispara después de descontar el blueprint
        /// y de meter el producto (o soltarlo si no cabía), nunca al cancelar.</summary>
        public static event Action<ICharacter, ItemDefinition, int> ItemCrafted;

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

            // ADR-064 enm. 1 — lo único que este fichero añade al vendor. Después de la mutación
            // local, que es la que STP hace de verdad (§4 del ADR); el backend sólo valida y espeja.
            var crafted = _currentItemToCraft;
            _currentItemToCraft = null;
            ItemCrafted?.Invoke(Character, crafted, craftData.CraftAmount);
        }

        private void OnCraftCancel() => _currentItemToCraft = null;

        #region Save & Load
        // ADR-009: STP save disabled — Rust is the single source of truth (igual que el vendor).
        public void LoadMembers(object data) { }
        public object SaveMembers() => null;
        #endregion
    }
}
