using System.Collections.Generic;
using UnityEngine;

namespace PolymindGames.BuildingSystem
{
    /// <summary>
    /// Represents a group of building pieces.
    /// </summary>
    public interface IBuildingPieceGroup : IMonoBehaviour
    {
        /// <summary>
        /// Gets the list of building pieces in the group.
        /// </summary>
        /// <returns>The list of building pieces in the group.</returns>
        IReadOnlyList<BuildingPiece> BuildingPieces { get; }

        /// <summary>
        /// Adds a building piece to the group.
        /// </summary>
        /// <param name="buildingPiece">The building piece to add.</param>
        void AddBuildingPiece(BuildingPiece buildingPiece);

        /// <summary>
        /// Removes a building piece from the group.
        /// </summary>
        /// <param name="buildingPiece">The building piece to remove.</param>
        void RemoveBuildingPiece(BuildingPiece buildingPiece);
        
        /// <summary>
        /// Clears all building pieces from the group.
        /// </summary>
        void Clear();
        
        /// <summary>
        /// Gets the world-space bounds of the placeable object.
        /// </summary>
        /// <returns>The world-space bounds of the object.</returns>
        Bounds GetWorldBounds();
    }
}