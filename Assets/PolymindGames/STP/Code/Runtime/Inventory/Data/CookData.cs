using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    [Serializable]
    public sealed class CookData : ItemData
    {
        [SerializeField, Range(0, 1440)]
        [Help("The cooking time in game minutes.")]
        private int _cookTime = 60;

        [SerializeField]
        [Help("Ensure the cook output item has a cooked amount property.")]
        private DataIdReference<ItemDefinition> _cookOutput;
        
        public int CookTimeInMinutes => _cookTime;
        public DataIdReference<ItemDefinition> CookedOutput => _cookOutput;
    }
}