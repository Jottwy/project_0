using PolymindGames.InventorySystem;
using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    [Serializable]
    public sealed class ItemNameDisplay
    {
        [SerializeField, NotNull]
        private TextMeshProUGUI _nameTxt; 

        public void UpdateInfo(Item item)
        {
            if (item != null)
            {
                var def = item.Definition;
                _nameTxt.text = def.Name;
                _nameTxt.color = def.Color;
            }
            else
                _nameTxt.text = string.Empty;
        }
    }
}