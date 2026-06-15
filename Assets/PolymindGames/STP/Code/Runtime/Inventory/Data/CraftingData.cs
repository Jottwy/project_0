using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    [Serializable]
    public sealed class CraftingData : ItemData
    {
        [SerializeField, SpaceArea, ReorderableList(ListStyle.Lined), IgnoreParent]
        [Tooltip("A list with all the 'ingredients' necessary to craft this item, it's also used in dismantling.")]
        private CraftRequirement[] _blueprint;

        [SerializeField, Range(0, 100)]
        [Help("Note: A craft amount of 0 will disable the ability to craft this item.")]
        private int _craftAmount = 1;

        [SerializeField, Range(0f, 30f)]
        [EnableIf(nameof(_craftAmount), 0, Comparison = UnityComparisonMethod.Greater)]
        [Tooltip("How much time does it take to craft this item, in seconds.")]
        private float _craftDuration = 1f;

        [SerializeField, Range(0, 20)]
        [EnableIf(nameof(_craftAmount), 0, Comparison = UnityComparisonMethod.Greater)]
        [Tooltip("Makes this item only craft-able from stations of the same tier.")]
        private int _craftLevel;

        [SerializeField, Range(0, 1f), SpaceArea(3f)]
        [Help("Note: A dismantle efficiency of 0 will disable the ability to dismantle this item.")]
        [Tooltip("An efficiency of 1 will result in getting all of the item back after dismantling, while 0 means that no item from the blueprint will be made available.")]
        private float _dismantleEfficiency = 0.75f;
        
        public CraftRequirement[] Blueprint => _blueprint;
        public bool IsCraftable => _blueprint.Length > 0 && _craftAmount > 0;
        public float CraftDuration => _craftDuration;
        public int CraftAmount => _craftAmount;
        public int CraftLevel => _craftLevel;
        public bool AllowDismantle => _blueprint.Length > 0 && _dismantleEfficiency > 0.01f;
        public float DismantleEfficiency => _dismantleEfficiency;

        public CraftRequirement[] CreateCraftRequirements(float durability)
        {
            var req = new CraftRequirement[_blueprint.Length];

            for (int i = 0; i < _blueprint.Length; i++)
            {
                int requiredAmount = Mathf.Max(Mathf.RoundToInt(_blueprint[i].Amount * Mathf.Clamp01((100f - durability) / 100f)), 1);
                req[i] = new CraftRequirement(_blueprint[i].Item, requiredAmount);
            }

            return req;
        }
    }
}