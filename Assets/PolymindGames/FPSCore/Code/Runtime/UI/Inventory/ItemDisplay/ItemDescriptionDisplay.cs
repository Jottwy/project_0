using PolymindGames.InventorySystem;
using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    [Serializable]
    public sealed class ItemDescriptionDisplay
    {
        private enum DescriptionType : byte
        {
            Short,
            Long
        }

        [SerializeField]
        private DescriptionType _descriptionType = DescriptionType.Short;

        [SerializeField]
        private TextMeshProUGUI _descriptionTxt;

        [SerializeField]
        private TextMeshProUGUI _categoryTxt;

        public void UpdateInfo(Item data)
        {
            if (data != null)
            {
                var def = data.Definition;

                if (_categoryTxt != null)
                    _categoryTxt.text = def.ParentGroup.Name;

                if (_descriptionTxt != null)
                    _descriptionTxt.text = _descriptionType == DescriptionType.Short ? def.Description : def.LongDescription;
            }
            else
            {
                if (_categoryTxt != null)
                    _categoryTxt.text = string.Empty;

                if (_descriptionTxt != null)
                    _descriptionTxt.text = string.Empty;
            }
        }
    }
}