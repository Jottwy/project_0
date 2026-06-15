using PolymindGames.InventorySystem;
using UnityEngine.UI;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    [Serializable]
    public sealed class ItemIconDisplay
    {
        [SerializeField, NotNull]
        private Image _iconImg;

        [SerializeField]
        private Image _bgIconImg;
        
        public Image IconImage => _iconImg;
        public Image BgIconImage => _bgIconImg;

        public void UpdateInfo(Item item)
        {
            if (item != null)
            {
                if (_bgIconImg != null)
                    _bgIconImg.enabled = false;

                _iconImg.enabled = true;
                _iconImg.sprite = item.Definition.Icon;
            }
            else
            {
                if (_bgIconImg != null)
                    _bgIconImg.enabled = true;

                _iconImg.enabled = false;
            }
        }
    }
}