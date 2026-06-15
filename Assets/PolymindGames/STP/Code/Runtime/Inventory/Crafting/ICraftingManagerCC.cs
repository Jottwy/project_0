using PolymindGames.InventorySystem;
using System.Collections.Generic;

namespace PolymindGames
{
    /// <summary>
    /// Manages crafting actions for a character.
    /// </summary>
    public interface ICraftingManagerCC : ICharacterComponent
    {
        /// <summary> Gets a value indicating whether the character is currently crafting. </summary>
        bool IsCrafting { get; }
        
        IReadOnlyList<DataIdReference<ItemDefinition>> FavoriteBlueprints { get; }

        void AddFavoriteBlueprint(DataIdReference<ItemDefinition> blueprint);
        void RemoveFavoriteBlueprint(DataIdReference<ItemDefinition> blueprint);

        /// <summary> Crafts the specified item. </summary>
        void Craft(ItemDefinition blueprint);

        /// <summary> Cancels the current crafting process. </summary>
        void CancelCrafting();
    }
}