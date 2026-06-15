using PolymindGames.InventorySystem;
using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    [Serializable]
    public sealed class ItemStackDisplay
    {
        [SerializeField]
        private GameObject _stackObject;

        [SerializeField]
        private TextMeshProUGUI _stackTxt;

        public void UpdateInfo(in ItemStack data)
        {
            if (data.HasItem())
            {
                if (_stackObject != null)
                    _stackObject.SetActive(data.Count > 1);
                
                if (_stackTxt != null)
                    _stackTxt.text = data.Count > 1 ? "x" + data.Count : string.Empty;
            }
            else
            {
                if (_stackObject != null)
                    _stackObject.SetActive(false);
                
                if (_stackTxt != null)
                    _stackTxt.text = string.Empty;
            }
        }
    }
}