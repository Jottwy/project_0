using PolymindGames.BuildingSystem;
using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    [Serializable]
    public sealed class BuildMaterialData : ItemData
    {
        [SerializeField]
        private DataIdReference<BuildMaterialDefinition> _buildMaterial;

        [SerializeField, Range(1, 100)]
        private int _materialCount = 1;
        
        public DataIdReference<BuildMaterialDefinition> BuildMaterial => _buildMaterial;
        public int MaterialCount => _materialCount;
    }
}