using PolymindGames.InventorySystem;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    [Serializable]
    public sealed class ItemRequirementDisplay
    {
        [SerializeField, NotNull]
        private GameObject _requirementsRoot;

        [SerializeField, ReorderableList(ListStyle.Lined, HasLabels = false)]
        private RequirementUI[] _requirements;

        [SerializeField]
        private Color _hasEnoughColor = Color.white;

        [SerializeField]
        private Color _notEnoughColor = Color.red;

        public void UpdateInfo(Item data)
        {
            if (data != null)
            {
                _requirementsRoot.SetActive(true);

                var def = data.Definition;

                if (!def.TryGetDataOfType<CraftingData>(out var craftData))
                    return;

                var blueprint = craftData.Blueprint;

                var inventory = GameMode.Instance.LocalPlayer.Inventory;

                for (int i = 0; i < _requirements.Length; i++)
                {
                    if (i > blueprint.Length - 1)
                    {
                        _requirements[i].gameObject.SetActive(false);
                        continue;
                    }

                    _requirements[i].gameObject.SetActive(true);

                    if (blueprint[i].Item.IsNull)
                        continue;

                    CraftRequirement requirement = blueprint[i];
                    ItemDefinition requiredItem = requirement.Item.Def;

                    int itemCount = inventory.GetItemCountById(requirement.Item);
                    bool hasEnoughMaterials = itemCount >= requirement.Amount;
                    _requirements[i].SetIconAndAmount(requiredItem.Icon, "x" + requirement.Amount, hasEnoughMaterials ? _hasEnoughColor : _notEnoughColor);
                }
            }
            else
            {
                _requirementsRoot.SetActive(false);
            }
        }
    }
}