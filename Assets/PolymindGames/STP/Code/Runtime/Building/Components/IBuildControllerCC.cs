using PolymindGames.BuildingSystem;
using UnityEngine.Events;

namespace PolymindGames
{
    /// <summary>
    /// Controls the building process for a character.
    /// </summary>
    public interface IBuildControllerCC : ICharacterComponent
    {
        /// <summary> The currently selected building piece. </summary>
        BuildingPiece BuildingPiece { get; }

        /// <summary> The rotation offset for the building piece. </summary>
        float RotationOffset { get; set; }

        /// <summary> Event invoked when building starts. </summary>
        event UnityAction BuildingStarted;

        /// <summary> Event invoked when building stops. </summary>
        event UnityAction BuildingStopped;

        /// <summary> Event invoked when an object is placed. </summary>
        event UnityAction<BuildingPiece> BuildingPieceChanged;

        /// <summary> Event invoked when the current building piece changes. </summary>
        event UnityAction<BuildingPiece> ObjectPlaced;

        /// <summary>
        /// Sets the currently selected building piece.
        /// </summary>
        /// <param name="buildingPiece">The building piece to set.</param>
        void SetBuildingPiece(BuildingPiece buildingPiece);

        /// <summary>
        /// Tries to place the currently selected building piece.
        /// </summary>
        /// <returns>True if the building piece was successfully placed, otherwise false.</returns>
        bool TryPlaceBuildingPiece();
    }
}