using PolymindGames.InventorySystem;
using PolymindGames.WieldableSystem;
using UnityEngine.UI;
using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    [Serializable]
    public sealed class ItemFiremodeDisplay
    {
        [SerializeField]
        private TextMeshProUGUI _firemodeText;

        [SerializeField]
        private Image _firemodeImg;
        
        private IFirearmIndexModeHandler _firemodeHandler;
        private IWieldableInventoryCC _selection;

        public void UpdateInfo(Item data)
        {
            if (IsItemValid(data))
            {
                var mode = _firemodeHandler.CurrentMode;

                if (_firemodeText != null)
                    _firemodeText.text = mode.Name;

                if (_firemodeImg != null)
                {
                    _firemodeImg.enabled = _firemodeImg.sprite != null;
                    _firemodeImg.sprite = mode.Icon;
                    _firemodeImg.rectTransform.sizeDelta = new Vector2(mode.IconSize, mode.IconSize);
                }
            }
            else
            {
                if (_firemodeText != null) _firemodeText.text = string.Empty;
                if (_firemodeImg != null) _firemodeImg.enabled = false;
            }
        }

        private bool IsItemValid(Item item)
        {
            if (item == null)
                return false;

            _selection ??= GameMode.Instance.LocalPlayer.GetCC<IWieldableInventoryCC>();
            var wieldable = _selection.GetWieldableWithId(item.Id);
            return wieldable != null && wieldable.gameObject.TryGetComponent(out _firemodeHandler);
        }
    }
}