using System;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    [Serializable]
    public struct CraftRequirement
    {
        [BeginHorizontal, HideLabel, DataReference(NullElement = "")]
        public DataIdReference<ItemDefinition> Item;

        [Range(1, 20), EndHorizontal, HideLabel]
        public int Amount;

        public CraftRequirement(int itemId, int amount)
        {
            Item = new DataIdReference<ItemDefinition>(itemId);
            Amount = amount;
        }
    }
}