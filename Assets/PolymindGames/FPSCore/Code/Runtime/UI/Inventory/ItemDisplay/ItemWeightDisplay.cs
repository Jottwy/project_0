using PolymindGames.InventorySystem;
using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    [Serializable]
    public sealed class ItemWeightDisplay
    {
        [SerializeField, NotNull]
        private TextMeshProUGUI _weightTxt;

        public void UpdateInfo(ItemStack data)
        {
            _weightTxt.text = data.HasItem()
                ? $"{Math.Round(data.GetTotalWeight(), 3)} {ItemDefinition.WeightUnit}"
                 : string.Empty;
        }
    }
}