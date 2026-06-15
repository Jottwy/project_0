using UnityEngine;

namespace PolymindGames.BuildingSystem
{
    /// <summary>
    /// Definition for a category of building pieces.
    /// </summary>
    [CreateAssetMenu(menuName = "Polymind Games/Building/Building Piece Category", fileName = "BuildingPieceCategory_")]
    public sealed class BuildingPieceCategoryDefinition : GroupDefinition<BuildingPieceCategoryDefinition, BuildingPieceDefinition>
    { }
}