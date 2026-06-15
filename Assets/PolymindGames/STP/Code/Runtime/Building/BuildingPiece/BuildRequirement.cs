using UnityEngine;
using System;

namespace PolymindGames.BuildingSystem
{
    /// <summary>
    /// Represents a requirement for building, specifying the needed build material, required amount, and current amount.
    /// </summary>
    [Serializable]
    public struct BuildRequirement
    {
        [SerializeField, HideLabel, BeginHorizontal]
        [DataReference(DataType = typeof(BuildMaterialDefinition), NullElement = "")]
        public int BuildMaterialId;
        
        [SerializeField, Clamp(0, 100), HideLabel, Disable]
        public short CurrentAmount;
        
        [SerializeField, Clamp(1, 100), HideLabel, EndHorizontal]
        public short RequiredAmount;

        public BuildRequirement(BuildMaterialDefinition buildMaterial, int requiredAmount, int currentAmount)
            : this(buildMaterial.Id, requiredAmount, currentAmount) { }
        
        public BuildRequirement(int buildMaterialId, int requiredAmount, int currentAmount)
        {
            BuildMaterialId = buildMaterialId;
            RequiredAmount = (short)Mathf.Max(requiredAmount, 0);
            CurrentAmount = (short)Mathf.Clamp(currentAmount, 0, RequiredAmount);
        }

        public BuildMaterialDefinition BuildMaterial => BuildMaterialDefinition.GetWithId(BuildMaterialId);
        
        /// <summary>
        /// Determines whether the requirement is completed, indicating that the current amount equals the required amount.
        /// </summary>
        /// <returns><c>true</c> if the requirement is completed; otherwise, <c>false</c>.</returns>
        public bool IsCompleted() => RequiredAmount == CurrentAmount;
    }
}